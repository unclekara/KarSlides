using System.Diagnostics;
using System.Runtime.InteropServices;
using KarSlides.Core.Abstractions;
using KarSlides.Core.Model;
using KarSlides.Core.Preflight;
using Microsoft.Extensions.Logging;

namespace KarSlides.PowerPoint;

public sealed class PowerPointHostOptions
{
    public TimeSpan ComTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan OpenTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan RunTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Keep the show window topmost (checked at start and on every position read).</summary>
    public bool Topmost { get; init; } = true;

    /// <summary>Where the user's PowerPoint monitor settings are saved while changed (crash recovery).</summary>
    public string SettingsBackupFile { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KarSlides", "powerpoint-monitor-backup.json");
}

/// <summary>
/// PowerPoint through late-bound COM, every call on one STA thread (<see cref="StaDispatcher"/>).
/// One POWERPNT.EXE for all slots. COM objects are released explicitly; PowerPoint is quit on dispose only if
/// this process started it. Findings from M0: presentations are opened without a window and close
/// themselves when their show ends; the show window is found by class; the monitor is set through
/// PowerPoint's settings for the duration of Run() and checked afterwards.
/// </summary>
public sealed class PowerPointHost : IPresentationHost, IDisposable
{
    private const int MsoTrue = -1;
    private const int MsoFalse = 0;
    private const int PpShowTypeSpeaker = 1;
    private const int PpShowAll = 1;
    private const int PpSlideShowManualAdvance = 1;
    private const int PpSlideShowDone = 5;
    private const int PpSlideShowPaused = 2;
    private const int PpEffectMorphByObject = 3953;
    private const int PpEffectMorphByChar = 3955;
    private const int MsoLinkedOleObject = 10;
    private const int MsoLinkedPicture = 11;
    private const int MsoPlaceholder = 14;
    private const int MsoMedia = 16;
    private const int PpPlaceholderBody = 2;
    private const int PpMediaTypeSound = 2;
    private const int PpMediaTypeMovie = 3;

    private readonly PowerPointHostOptions _options;
    private readonly FocusGuard _focus;
    private readonly ILogger _log;
    private readonly StaDispatcher _sta;

    // STA-thread only:
    private readonly Dictionary<Guid, Session> _sessions = new();
    private dynamic? _app;
    private dynamic? _presentations;
    private bool _weStartedApp;
    private MonitorSettingsGuard? _monitorSettings;
    private bool _disposed;

    private sealed class Session(SlotHandle handle, dynamic presentation)
    {
        public SlotHandle Handle { get; } = handle;
        public dynamic? Presentation { get; set; } = presentation;
        public dynamic? Settings { get; set; }
        public dynamic? Window { get; set; }
        public dynamic? View { get; set; }
        public IntPtr Hwnd { get; set; }
    }

    public PowerPointHost(PowerPointHostOptions options, FocusGuard focus, ILoggerFactory loggers)
    {
        _options = options;
        _focus = focus;
        _log = loggers.CreateLogger<PowerPointHost>();
        _sta = new StaDispatcher("PowerPointSta", options.ComTimeout, loggers.CreateLogger<StaDispatcher>());
    }

    /// <summary>True if the PowerPoint.Application ProgID is registered (start-up check).</summary>
    public static bool IsInstalled => Type.GetTypeFromProgID("PowerPoint.Application") is not null;

    // ---- IPresentationHost ----

    public Task<SlotHandle> OpenAsync(string path, int slotIndex, CancellationToken ct) =>
        _sta.InvokeAsync("Presentations.Open", () =>
        {
            EnsureApp();
            // Open(FileName, ReadOnly, Untitled, WithWindow)
            dynamic pres = _presentations!.Open(path, MsoTrue, MsoFalse, MsoFalse);
            dynamic slides = pres.Slides;
            int count = slides.Count;
            Release(slides);

            var handle = new SlotHandle(Guid.NewGuid(), path, count);
            _sessions[handle.Id] = new Session(handle, pres);
            _log.LogInformation("Slot {Slot}: opened '{Path}', {Count} slides", slotIndex, path, count);
            return handle;
        }, _options.OpenTimeout, ct);

    public Task StartShowAsync(SlotHandle handle, MonitorTarget monitor, int startSlide, CancellationToken ct) =>
        _sta.InvokeAsync("SlideShowSettings.Run", () =>
        {
            var s = Get(handle);
            s.Settings = s.Presentation!.SlideShowSettings;
            s.Settings.ShowType = PpShowTypeSpeaker;
            s.Settings.ShowPresenterView = MsoFalse;
            s.Settings.RangeType = PpShowAll;
            s.Settings.AdvanceMode = PpSlideShowManualAdvance;
            s.Settings.LoopUntilStopped = MsoFalse;
            s.Settings.ShowWithAnimation = MsoTrue;
            s.Settings.ShowWithNarration = MsoTrue;

            var before = ShowWindows.FindAll();
            _monitorSettings!.Apply(monitor.DeviceName);
            try
            {
                s.Window = s.Settings.Run();
            }
            finally
            {
                // PowerPoint reads the settings inside Run(): keep the user's values changed only that long.
                _monitorSettings.Restore();
            }
            s.View = s.Window!.View;
            s.Hwnd = ShowWindows.FindNew(before, _log);
            _focus.OnShowWindow(s.Hwnd);
            ShowWindows.EnsureOnMonitor(s.Hwnd, monitor, _log);
            if (_options.Topmost) ShowWindows.EnsureTopmost(s.Hwnd, initial: true, _log);

            if (startSlide > 1) s.View.GotoSlide(startSlide, MsoTrue);
        }, _options.RunTimeout, ct);

    public Task NextAsync(SlotHandle handle, CancellationToken ct) =>
        _sta.InvokeAsync("View.Next", () => View(handle).Next(), ct: ct);

    public Task PreviousAsync(SlotHandle handle, CancellationToken ct) =>
        _sta.InvokeAsync("View.Previous", () => View(handle).Previous(), ct: ct);

    public Task GotoSlideAsync(SlotHandle handle, int slide, CancellationToken ct) =>
        _sta.InvokeAsync("View.GotoSlide", () => View(handle).GotoSlide(slide, MsoTrue), ct: ct);

    public Task GotoClickAsync(SlotHandle handle, int slide, int click, CancellationToken ct) =>
        _sta.InvokeAsync("View.GotoClick", () =>
        {
            var view = View(handle);
            view.GotoSlide(slide, MsoTrue);
            if (click > 0) view.GotoClick(click);
        }, ct: ct);

    public Task<HostPosition> GetPositionAsync(SlotHandle handle, CancellationToken ct) =>
        _sta.InvokeAsync("View.Position", () =>
        {
            var view = View(handle);
            // Read once a second by the watchdog: also restores topmost if something reset it.
            if (_options.Topmost) ShowWindows.EnsureTopmost(Get(handle).Hwnd, initial: false, _log);
            try
            {
                int state = view.State;
                dynamic slide = view.Slide;
                int index = slide.SlideIndex;
                Release(slide);
                int click = view.GetClickIndex();
                int clicks = view.GetClickCount();
                dynamic windows = _app!.Windows;
                int others = windows.Count;
                Release(windows);
                return new HostPosition(index, click, clicks, state != PpSlideShowDone, state == PpSlideShowPaused, others);
            }
            catch (COMException ex)
            {
                // The show window is gone (closed by hand, PowerPoint lost it): not running.
                _log.LogWarning("Show of '{Path}' is not available: {Message}", handle.Path, ex.Message);
                return new HostPosition(0, 0, 0, false);
            }
        }, ct: ct);

    public Task CloseAsync(SlotHandle handle, CancellationToken ct) =>
        _sta.InvokeAsync("Close", () =>
        {
            if (_sessions.Remove(handle.Id, out var s)) CloseSession(s);
        }, ct: ct);

    public Task<PresentationInfo> InspectAsync(SlotHandle handle, CancellationToken ct) =>
        _sta.InvokeAsync<PresentationInfo>("Inspect", () => Inspect(Get(handle).Presentation!), _options.OpenTimeout, ct);

    public Task ExportThumbnailAsync(SlotHandle handle, int slide, string path, int width, int height, CancellationToken ct) =>
        _sta.InvokeAsync($"Slide.Export {slide}", () =>
        {
            dynamic slides = Get(handle).Presentation!.Slides;
            dynamic s = slides.Item(slide);
            try { s.Export(path, "PNG", width, height); }
            finally
            {
                Release(s);
                Release(slides);
            }
        }, _options.OpenTimeout, ct);

    /// <summary>Walks fonts, slides and shapes. STA thread; every RCW released.</summary>
    private PresentationInfo Inspect(dynamic pres)
    {
        dynamic setup = pres.PageSetup;
        double width = setup.SlideWidth, height = setup.SlideHeight;
        Release(setup);

        var fonts = new List<FontUse>();
        dynamic fontsCol = pres.Fonts;
        int fontCount = fontsCol.Count;
        for (var i = 1; i <= fontCount; i++)
        {
            dynamic f = fontsCol.Item(i);
            fonts.Add(new FontUse((string)f.Name, (int)f.Embedded != MsoFalse));
            Release(f);
        }
        Release(fontsCol);

        var linked = new List<string>();
        var notes = new List<string>();
        bool video = false, audio = false, morph = false;
        dynamic slides = pres.Slides;
        int slideCount = slides.Count;
        for (var i = 1; i <= slideCount; i++)
        {
            dynamic slide = slides.Item(i);
            dynamic transition = slide.SlideShowTransition;
            int effect = transition.EntryEffect;
            morph |= effect is >= PpEffectMorphByObject and <= PpEffectMorphByChar;
            Release(transition);
            notes.Add(ReadNotes(slide, i));

            dynamic shapes = slide.Shapes;
            int shapeCount = shapes.Count;
            for (var j = 1; j <= shapeCount; j++)
            {
                dynamic shape = shapes.Item(j);
                try
                {
                    int type = shape.Type;
                    var isMedia = type == MsoMedia || (type == MsoPlaceholder && (int)shape.PlaceholderFormat.ContainedType == MsoMedia);
                    if (isMedia)
                    {
                        int mediaType = shape.MediaType;
                        video |= mediaType == PpMediaTypeMovie;
                        audio |= mediaType == PpMediaTypeSound;
                    }
                    if (isMedia || type is MsoLinkedOleObject or MsoLinkedPicture)
                    {
                        string? source = TryGetLinkSource(shape);
                        if (source is not null) linked.Add(source);
                    }
                }
                catch (COMException ex)
                {
                    _log.LogDebug("Inspect: slide {Slide} shape {Shape}: {Message}", i, j, ex.Message);
                }
                finally
                {
                    Release(shape);
                }
            }
            Release(shapes);
            Release(slide);
        }
        Release(slides);

        return new PresentationInfo(slideCount, width, height, fonts, linked, video, audio, morph, notes);
    }

    /// <summary>Text of the notes body placeholder of a slide ("" if none).</summary>
    private string ReadNotes(dynamic slide, int index)
    {
        try
        {
            dynamic page = slide.NotesPage;
            dynamic shapes = page.Shapes;
            var text = "";
            int count = shapes.Count;
            for (var k = 1; k <= count && text.Length == 0; k++)
            {
                dynamic shape = shapes.Item(k);
                try
                {
                    if ((int)shape.Type == MsoPlaceholder && (int)shape.PlaceholderFormat.Type == PpPlaceholderBody &&
                        (int)shape.HasTextFrame != MsoFalse)
                    {
                        dynamic frame = shape.TextFrame;
                        dynamic range = frame.TextRange;
                        text = ((string)range.Text).Replace('\r', '\n').Replace('\v', '\n').Trim(); // PowerPoint: \r paragraphs, \v line breaks
                        Release(range);
                        Release(frame);
                    }
                }
                finally
                {
                    Release(shape);
                }
            }
            Release(shapes);
            Release(page);
            return text;
        }
        catch (COMException ex)
        {
            _log.LogDebug("Notes of slide {Slide}: {Message}", index, ex.Message);
            return "";
        }
    }

    /// <summary>Path of a linked (not embedded) file, or null.</summary>
    private static string? TryGetLinkSource(dynamic shape)
    {
        try
        {
            dynamic link = shape.LinkFormat;
            string source = link.SourceFullName;
            Release(link);
            return string.IsNullOrWhiteSpace(source) ? null : source;
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null; // embedded: no LinkFormat
        }
    }

    // ---- STA-thread helpers ----

    private Session Get(SlotHandle handle) =>
        _sessions.TryGetValue(handle.Id, out var s) ? s : throw new InvalidOperationException($"Unknown presentation '{handle.Path}'");

    private dynamic View(SlotHandle handle) => Get(handle).View ?? throw new InvalidOperationException("Slide show is not running");

    private void EnsureApp()
    {
        if (_app is not null) return;
        var type = Type.GetTypeFromProgID("PowerPoint.Application")
                   ?? throw new InvalidOperationException("Microsoft PowerPoint is not installed (ProgID PowerPoint.Application not found)");
        // A process killed by the watchdog can still be listed for a moment: compare live pids before and after.
        var before = LivePowerPointPids();
        _app = Activator.CreateInstance(type)!;
        _weStartedApp = LivePowerPointPids().Except(before).Any();
        if (!_weStartedApp)
            _log.LogWarning("POWERPNT.EXE was already running: attached to it, it will not be closed on exit");

        _presentations = _app.Presentations;
        string version = _app.Version;
        string build = _app.Build;
        _log.LogInformation("PowerPoint {Version} build {Build} started", version, build);

        _monitorSettings = new MonitorSettingsGuard(new RegistryOptionsStore(version), _options.SettingsBackupFile, _log);
        if (_monitorSettings.Restore())
            _log.LogWarning("PowerPoint monitor settings left changed by a previous run were restored");
    }

    private static HashSet<int> LivePowerPointPids()
    {
        var pids = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName("POWERPNT"))
        {
            try
            {
                if (!p.HasExited) pids.Add(p.Id);
            }
            catch (Exception)
            {
                // access denied or gone: not ours to judge
            }
            finally
            {
                p.Dispose();
            }
        }
        return pids;
    }

    private void CloseSession(Session s)
    {
        if (s.View is not null)
        {
            try { s.View.Exit(); }
            catch (COMException ex) { _log.LogDebug("View.Exit: {Message} (show already closed)", ex.Message); }
        }
        Release(s.View); s.View = null;
        Release(s.Window); s.Window = null;
        Release(s.Settings); s.Settings = null;

        // Opened without a window, the presentation closes together with its show; close it if it did not.
        if (s.Presentation is not null)
        {
            try
            {
                string _ = s.Presentation.Name;
                s.Presentation.Close();
            }
            catch (COMException)
            {
                // already closed by PowerPoint
            }
            Release(s.Presentation);
            s.Presentation = null;
        }
        _log.LogInformation("Closed '{Path}'", s.Handle.Path);
    }

    private static void Release(object? o)
    {
        if (o is not null && Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o);
    }

    /// <summary>
    /// Drops a dead or hung PowerPoint without any COM call (the STA thread may be stuck in one): restores the
    /// user's monitor settings from the backup file and lets the STA thread end once the killed process
    /// releases it. RCWs of the dead process are left to the GC.
    /// </summary>
    public void Abandon()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _monitorSettings?.Restore(); // registry and file only, no COM
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not restore PowerPoint monitor settings while abandoning the host");
        }
        _ = Task.Run(() => _sta.Dispose()); // may block until the killed call returns; never on the caller
        _log.LogWarning("PowerPoint host abandoned ({Count} open presentation(s) dropped)", _sessions.Count);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _sta.InvokeAsync("Shutdown", () =>
            {
                foreach (var s in _sessions.Values) CloseSession(s);
                _sessions.Clear();
                _monitorSettings?.Restore();
                Release(_presentations);
                _presentations = null;
                if (_app is not null)
                {
                    if (_weStartedApp)
                    {
                        try { _app.Quit(); }
                        catch (COMException ex) { _log.LogWarning("Application.Quit failed: {Message}", ex.Message); }
                    }
                    Release(_app);
                    _app = null;
                }
                // RCWs created implicitly by the dynamic binder; we are on the STA thread.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }, TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PowerPoint shutdown failed");
        }
        _sta.Dispose();

        if (_weStartedApp) WaitForExit(TimeSpan.FromSeconds(10));
    }

    private void WaitForExit(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (Process.GetProcessesByName("POWERPNT").Length == 0)
            {
                _log.LogInformation("POWERPNT.EXE exited {Ms} ms after release", sw.ElapsedMilliseconds);
                return;
            }
            Thread.Sleep(100); // shutdown only
        }
        _log.LogError("POWERPNT.EXE still running {Seconds} s after release: COM references leaked", timeout.TotalSeconds);
    }
}
