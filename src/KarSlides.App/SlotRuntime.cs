using System.Diagnostics;
using KarSlides.Audio;
using KarSlides.Capture;
using KarSlides.Control;
using KarSlides.Core.Abstractions;
using KarSlides.Core.Audio;
using KarSlides.Core.Configuration;
using KarSlides.Core.Model;
using KarSlides.Core.Monitors;
using KarSlides.Core.Preflight;
using KarSlides.Core.Preview;
using KarSlides.Core.Slots;
using KarSlides.Ndi;
using KarSlides.PowerPoint;
using KarSlides.Vmix;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KarSlides.App;

/// <summary>
/// Background start-up of the slot pipeline: checks the environment, builds PowerPoint host, capture,
/// NDI output and the slot, adds it to <see cref="SlotSet"/>; tears everything down in reverse order on stop.
/// The API is already up while this runs (it reports problems and an empty slot list).
/// </summary>
internal sealed class SlotRuntime(KarSlidesOptions options, AppStatus status, SlotSet slots, PreviewSet previews, EventHub hub,
    ILoggerFactory loggers)
    : BackgroundService
{
    private readonly ILogger _log = loggers.CreateLogger<SlotRuntime>();
    private readonly List<IDisposable> _disposables = [];
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private FocusGuard? _focus;
    private PowerPointRuntime? _powerPoint;

    public string ApiUrl => $"http://localhost:{options.Control.Port}/api/state";
    public string PanelUrl => $"http://localhost:{options.Control.Port}/";
    public string? NdiName { get; private set; }

    /// <summary>Completes when the start-up checks are done (successfully or with problems).</summary>
    public Task Started => _started.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await StartPipelineAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Problem($"Start-up failed: {ex.Message}", ex);
        }
        finally
        {
            status.Started = true;
            _started.TrySetResult();
        }
    }

    private async Task StartPipelineAsync(CancellationToken ct)
    {
        if (options.Slots.Count != 1)
            _log.LogWarning("Slots.Count = {Count}: one slot is supported until M3, starting slot 1 only", options.Slots.Count);

        var rate = FrameRate.Parse(options.Output.Fps);
        var testPattern = options.Output.Source.Equals("testpattern", StringComparison.OrdinalIgnoreCase);

        var ndiOk = Check("NDI runtime", () =>
        {
            NdiRuntime.EnsureLoaded();
            _log.LogInformation("NDI runtime {Version} from {Path}", NdiRuntime.Version, NdiRuntime.LoadedFrom);
        });
        var pptOk = Check("PowerPoint", () =>
        {
            if (!PowerPointHost.IsInstalled) throw new InvalidOperationException("Microsoft PowerPoint is not installed");
        });
        var monitorSpec = options.Slots.Monitors.FirstOrDefault() ?? "virtual";
        var monitor = testPattern ? null : await WaitForMonitorAsync(monitorSpec, ct);
        if (!ndiOk) return;

        NdiName = options.Slots.NdiNameTemplate.Replace("{n}", "1");
        var output = new NdiOutput(NdiName, options.Output.Width, options.Output.Height, rate,
            !options.Output.Send.Equals("sync", StringComparison.OrdinalIgnoreCase), loggers.CreateLogger<NdiOutput>());
        _disposables.Add(output);

        // Everything that goes on air passes the live preview of the web UI.
        IOutputSink sink = output;
        if (options.Preview.Live)
        {
            var preview = new LivePreview(output, new GdiJpegEncoder(),
                new LivePreviewOptions { Fps = options.Preview.Fps, Width = options.Preview.Width, Quality = options.Preview.Quality },
                loggers.CreateLogger<LivePreview>(), "Slot 1");
            _disposables.Insert(0, preview); // after the source (inserted before it below), before the output
            previews.Add(1, preview);
            sink = preview;
            _log.LogInformation("Live preview: {Width}px, up to {Fps} fps, JPEG quality {Quality}",
                options.Preview.Width, options.Preview.Fps, options.Preview.Quality);
        }

        // Runs without NDI audio too: it also keeps PowerPoint on its playback device.
        {
            var ring = options.Audio.Enabled ? PowerPointAudioService.CreateRing(options.Audio.BufferMs) : null;
            var route = new AudioRouteKeeper(new WindowsAudioRouter(), options.Audio.OutputDevice,
                loggers.CreateLogger<AudioRouteKeeper>());
            var audio = new PowerPointAudioService(ring, route, loggers.CreateLogger<PowerPointAudioService>());
            audio.DeviceChecked += OnDeviceChecked;
            if (ring is not null) output.AttachAudio(ring);
            _disposables.Insert(0, audio); // disposed before PowerPoint quits: resets its device while it still runs
            audio.Start();
            _log.LogInformation("PowerPoint playback device: {Device}; sound in NDI: {Ndi}",
                route.DeviceId.Length == 0 ? "Windows default" : route.DeviceId, options.Audio.Enabled ? "on" : "off");
        }

        IFrameSource? source = null;
        if (testPattern)
        {
            source = new TestPatternSource(options.Output.Width, options.Output.Height, rate.Numerator / (double)rate.Denominator,
                loggers.CreateLogger<TestPatternSource>());
            sink.Mode = OutputMode.Live;
        }
        else if (monitor is not null)
        {
            var probe = new ShowWindowProbe(monitor.Left, monitor.Top, monitor.Width, monitor.Height);
            source = new MonitorCapture(monitor.DeviceName, debugLayer: false, loggers.CreateLogger<MonitorCapture>())
            {
                Gate = probe.IsCovered, // the desktop of the slot monitor never reaches the output
            };
        }

        if (source is not null)
        {
            source.FrameArrived += sink.PushVideo;
            _disposables.Insert(0, source); // stop the source before the output
            Check("Capture", source.Start);
        }

        if (!pptOk || (monitor is null && !testPattern)) return;

        _focus = new FocusGuard(FocusGuard.Parse(options.Show.Focus), loggers.CreateLogger<FocusGuard>());
        var focus = _focus;
        _powerPoint = new PowerPointRuntime(() => new PowerPointHost(
                new PowerPointHostOptions
                {
                    ComTimeout = TimeSpan.FromMilliseconds(options.Watchdog.ComTimeoutMs),
                    Topmost = options.Show.Topmost,
                },
                focus, loggers),
            loggers.CreateLogger<PowerPointRuntime>());
        // With the test pattern the output is not driven by the slot (the pattern stays on air).
        IOutputSink slotOutput = testPattern ? new DetachedOutput() : sink;
        var outputAspect = options.Output.Width / (double)options.Output.Height;
        slots.Add(new SlotController(1, _powerPoint, slotOutput, () => ResolveMonitor(monitorSpec),
            new SlotOptions
            {
                HoldRelease = TimeSpan.FromMilliseconds(options.Output.HoldReleaseMs),
                Cache = new FileCache(options.Paths.CacheExpanded),
                Evaluate = info => PreflightEvaluator.Evaluate(info, InstalledFonts.Get(), outputAspect, File.Exists),
                Thumbnails = options.Preflight.Thumbnails,
            },
            loggers.CreateLogger("KarSlides.Slot1")));
        _log.LogInformation("Slot 1 ready for commands");
        foreach (var s in slots.All)
        {
            s.Changed += hub.PublishSlot;
            hub.PublishSlot(s.Status);
        }

        if (options.Watchdog.Enabled)
        {
            Watchdog = new Watchdog(slots, _powerPoint, options.Watchdog, loggers.CreateLogger<Watchdog>(), status);
            Watchdog.Recovered += hub.PublishRecovery;
            _watchdogTask = Watchdog.RunAsync(ct);
        }

        if (options.Vmix.Enabled && NdiName is not null)
        {
            var vmix = new VmixClient(options.Vmix, _http, loggers.CreateLogger<VmixClient>());
            var slot = slots.Find(1)!;
            vmix.AddSlot(1, NdiName, slot.SetTally);
            status.Vmix = () => vmix.Status;
            _vmixTask = vmix.RunAsync(ct);
        }
    }

    private readonly HttpClient _http = new();
    private Task _vmixTask = Task.CompletedTask;

    /// <summary>Null until start-up finished or when disabled.</summary>
    public Watchdog? Watchdog { get; private set; }

    private Task _watchdogTask = Task.CompletedTask;

    private string? _lastDeviceWarning = "";

    /// <summary>
    /// The device PowerPoint plays to: missing or not routable, and audio enhancements on it, which delay
    /// PowerPoint audio in NDI. Warnings stay while the cause stays.
    /// </summary>
    private void OnDeviceChecked(AudioRouteState state)
    {
        status.SetWarning("audio-device", state.Warning);
        var device = state.Effective;
        var warning = device is { EnhancementsEnabled: true }
            ? $"Audio enhancements are on for '{device.Name}', where PowerPoint plays: its audio comes 20-40 ms later than " +
              "the picture. Turn them off: Settings > System > Sound > the device > Audio enhancements: Off"
            : null;
        status.SetWarning("audio-enhancements", warning);
        if (warning == _lastDeviceWarning) return;
        _lastDeviceWarning = warning;
        if (warning is not null) _log.LogWarning("{Warning}", warning);
        else _log.LogInformation("PowerPoint plays to {Device}, enhancements off", device?.Name ?? "none");
    }

    private bool Check(string what, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            Problem($"{what}: {ex.Message}", ex);
            return false;
        }
    }

    private void Problem(string message, Exception? ex = null)
    {
        status.AddProblem(message);
        _log.LogError(ex, "Start-up problem: {Problem}", message);
    }

    /// <summary>IddCx drivers come up a few seconds after logon: wait for the monitor, check its size.</summary>
    private async Task<MonitorDescriptor?> WaitForMonitorAsync(string spec, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var all = DisplayMonitors.Enumerate();
        var m = MonitorMatcher.Match(spec, all);
        while (m is null && sw.Elapsed.TotalSeconds < options.Slots.MonitorWaitSeconds)
        {
            await Task.Delay(1000, ct);
            all = DisplayMonitors.Enumerate();
            m = MonitorMatcher.Match(spec, all);
        }

        _log.LogInformation("Monitors: {Monitors}", string.Join("; ", all.Select(x =>
            $"{x.DeviceName} '{x.FriendlyName}' {x.Connection} {x.Width}x{x.Height} at ({x.Left},{x.Top}) " +
            $"virtual={x.IsVirtual} primary={x.IsPrimary} id={x.DeviceId}")));
        if (m is null)
        {
            Problem($"Monitor '{spec}' for slot 1 not found within {options.Slots.MonitorWaitSeconds} s " +
                    $"(available: {string.Join(", ", all.Select(x => x.DeviceName))})");
            return null;
        }
        if (m.Width != options.Output.Width || m.Height != options.Output.Height)
        {
            Problem($"Monitor {m.DeviceName} is {m.Width}x{m.Height}, slot monitors must be " +
                    $"{options.Output.Width}x{options.Output.Height}: change it in Windows display settings");
            return null;
        }
        _log.LogInformation("Slot 1 monitor: '{Spec}' -> {Device} ({Id}), found after {Ms} ms",
            spec, m.DeviceName, m.DeviceId, sw.ElapsedMilliseconds);
        return m;
    }

    /// <summary>Resolved before every show start: \\.\DISPLAYn of a virtual monitor is not stable.</summary>
    private static MonitorTarget ResolveMonitor(string spec)
    {
        var all = DisplayMonitors.Enumerate();
        var m = MonitorMatcher.Match(spec, all)
                ?? throw new InvalidOperationException($"Monitor '{spec}' not found (available: {string.Join(", ", all.Select(x => x.DeviceName))})");
        return DisplayMonitors.ToTarget(m);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken); // cancels the watchdog and vMix loops
        await _watchdogTask;
        await _vmixTask;
        _http.Dispose();
        foreach (var d in _disposables)
        {
            try { d.Dispose(); }
            catch (Exception ex) { _log.LogError(ex, "Dispose of {Type} failed", d.GetType().Name); }
        }
        _powerPoint?.Dispose(); // closes shows, quits PowerPoint if we started it, restores monitor settings
        _focus?.Dispose();
    }

    /// <summary>Slot output when the test pattern is on air: modes are logged only.</summary>
    private sealed class DetachedOutput : IOutputSink
    {
        public OutputMode Mode { get; set; }
        public void PushVideo(in VideoFrame frame) { }
        public void Dispose() { }
    }
}
