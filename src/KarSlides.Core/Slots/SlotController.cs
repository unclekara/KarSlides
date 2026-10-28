using KarSlides.Core.Abstractions;
using KarSlides.Core.Model;
using KarSlides.Core.Preflight;
using Microsoft.Extensions.Logging;

namespace KarSlides.Core.Slots;

/// <summary>Thrown when a command is not allowed in the current slot state (API: 409).</summary>
public class SlotStateException(string message) : InvalidOperationException(message);

/// <summary>Load / unload / reload of a slot that is on program without force=true (API: 409).</summary>
public sealed class OnAirException(string message) : SlotStateException(message);

public sealed class SlotOptions
{
    /// <summary>
    /// After the show is placed, the output keeps the previous frame this long, so the first frames of a
    /// window being created (desktop, half-drawn slide) never go on air.
    /// </summary>
    public TimeSpan HoldRelease { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Local copy before opening. Null = open the source directly.</summary>
    public FileCache? Cache { get; init; }

    /// <summary>Pre-flight evaluation of the opened presentation. Null = no pre-flight.</summary>
    public Func<PresentationInfo, PreflightReport>? Evaluate { get; init; }

    /// <summary>Thumbnails of all slides during load, before the show starts; needs <see cref="Cache"/>.</summary>
    public bool Thumbnails { get; init; }

    public int ThumbnailWidth { get; init; } = 480;
    public int ThumbnailHeight { get; init; } = 270;
}

/// <summary>
/// One slot: presentation -> show on its monitor -> output. Serialises commands, tracks the position itself
/// (PowerPoint's click index is wrong after Previous) and decides what the output sends:
/// Black only on command; Freeze on command or while the show is not Ready, so the desktop of the virtual
/// monitor never goes on air. Reload and watchdog recovery return to the same slide and click.
/// </summary>
public sealed class SlotController
{
    private readonly int _index;
    private readonly IPresentationHost _host;
    private readonly IOutputSink _output;
    private readonly Func<MonitorTarget> _resolveMonitor;
    private readonly SlotOptions _options;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();

    private SlotState _state = SlotState.Empty;
    private SlotHandle? _handle;
    private string? _source;
    private ShowPosition _position;
    private int _clickCount;
    private bool _seekPending; // the last Next only fast-forwarded the slide's automatic sequence
    private string? _error;
    private bool _black, _freeze;
    private Tally _tally;
    private PreflightReport? _preflight;
    private IReadOnlyList<string> _notes = [];
    private int _thumbnails;

    public SlotController(int index, IPresentationHost host, IOutputSink output, Func<MonitorTarget> resolveMonitor,
        SlotOptions options, ILogger log)
    {
        _index = index;
        _host = host;
        _output = output;
        _resolveMonitor = resolveMonitor;
        _options = options;
        _log = log;
        ApplyOutputMode();
    }

    public int Index => _index;

    /// <summary>Raised after every change (state, position, output, tally, thumbnails); for WebSocket events.</summary>
    public event Action<SlotStatus>? Changed;

    public SlotStatus Status
    {
        get
        {
            lock (_sync)
            {
                return new SlotStatus(_index, _state, _source, _position.Slide, _handle?.SlideCount ?? 0,
                    _position.Click, _clickCount, ComputeOutputMode(), _black, _freeze, _error,
                    _preflight, _thumbnails, _tally.OnProgram, _tally.OnPreview);
            }
        }
    }

    /// <summary>Speaker notes of the loaded presentation, one entry per slide.</summary>
    public IReadOnlyList<string> Notes
    {
        get { lock (_sync) return _notes; }
    }

    /// <summary>The PNG of a slide if it has been exported, else null.</summary>
    public string? ThumbnailPath(int slide)
    {
        string? source;
        lock (_sync) source = _source;
        if (source is null || _options.Cache is null) return null;
        var path = Path.Combine(_options.Cache.ThumbnailFolder(source), $"{slide}.png");
        return File.Exists(path) ? path : null;
    }

    // ---- Load / unload / reload ----

    public async Task<SlotStatus> LoadAsync(string path, int startSlide, CancellationToken ct, bool force = false)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"File not found: {path}", path);

        await _gate.WaitAsync(ct);
        try
        {
            if (_state is SlotState.Loading or SlotState.Unloading or SlotState.Recovering)
                throw new SlotStateException($"Slot {_index} is {_state}");
            GuardOnAir("Load", force);

            if (_handle is not null) await UnloadCoreAsync(ct);

            lock (_sync)
            {
                _source = path;
                _preflight = null;
            }
            SetState(SlotState.Loading, error: null);
            _log.LogInformation("Slot {Slot}: loading '{Path}', start slide {Start}", _index, path, startSlide);
            await OpenAndStartAsync(path, new ShowPosition(startSlide, 0), copyToCache: true, ct);
            return Status;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SlotStatus> UnloadAsync(CancellationToken ct, bool force = false)
    {
        await _gate.WaitAsync(ct);
        try
        {
            GuardOnAir("Unload", force);
            if (_handle is not null) await UnloadCoreAsync(ct);
            lock (_sync)
            {
                _source = null;
                _preflight = null;
                _notes = [];
            }
            SetState(SlotState.Empty, error: null);
            return Status;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Re-reads the file from its source and returns to the same slide and click.</summary>
    public async Task<SlotStatus> ReloadAsync(CancellationToken ct, bool force = false)
    {
        await _gate.WaitAsync(ct);
        try
        {
            string source;
            ShowPosition position;
            lock (_sync)
            {
                source = _source ?? throw new SlotStateException($"Slot {_index} has no presentation to reload");
                position = _position;
            }
            if (_state is SlotState.Loading or SlotState.Unloading or SlotState.Recovering)
                throw new SlotStateException($"Slot {_index} is {_state}");
            GuardOnAir("Reload", force);
            if (!File.Exists(source)) throw new FileNotFoundException($"File not found: {source}", source);

            _log.LogInformation("Slot {Slot}: reloading '{Path}' at {Position}", _index, source, position);
            if (_handle is not null) await UnloadCoreAsync(ct);
            SetState(SlotState.Loading, error: null);
            await OpenAndStartAsync(source, position, copyToCache: true, ct);
            return Status;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- Watchdog recovery ----

    /// <summary>
    /// Freezes the output and marks the slot as recovering. Does not touch PowerPoint (it may be hung)
    /// and does not wait for the command gate.
    /// </summary>
    public void BeginRecovery(string reason)
    {
        lock (_sync)
        {
            if (_source is null) return; // nothing loaded: nothing to recover
            _handle = null; // belongs to the dead PowerPoint
        }
        _log.LogWarning("Slot {Slot}: recovery started ({Reason}), output frozen at {Position}", _index, reason, _position);
        SetState(SlotState.Recovering, error: reason);
    }

    /// <summary>After PowerPoint was restarted: reopens the cached copy and returns to the tracked position.</summary>
    public async Task<SlotStatus> RecoverAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            string? source;
            ShowPosition position;
            lock (_sync)
            {
                source = _source;
                position = _position;
            }
            if (_state != SlotState.Recovering || source is null) return Status;

            _log.LogInformation("Slot {Slot}: restoring '{Path}' at {Position}", _index, source, position);
            await OpenAndStartAsync(source, position, copyToCache: false, ct);
            return Status;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Watchdog heartbeat: is the show alive, answering, not paused by PowerPoint? Throws on COM timeout or
    /// cancellation. Does not take the command gate: a hung command must not hide a hung PowerPoint.
    /// </summary>
    public async Task<HostPosition> PingAsync(CancellationToken ct)
    {
        SlotHandle? handle;
        lock (_sync) handle = IsWatchedState ? _handle : null;
        if (handle is null) return new HostPosition(0, 0, 0, Running: true); // nothing to watch
        return await _host.GetPositionAsync(handle, ct);
    }

    /// <summary>
    /// Starts the show again at the tracked position from the cached copy, PowerPoint keeps running. For a show
    /// PowerPoint paused and will not resume. The output stays frozen on the last frame meanwhile.
    /// </summary>
    public async Task<SlotStatus> RestartShowAsync(string reason, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            string? source;
            ShowPosition position;
            SlotHandle? handle;
            lock (_sync)
            {
                (source, position, handle) = (_source, _position, _handle);
            }
            if (_state != SlotState.Ready || source is null || handle is null) return Status;

            _log.LogWarning("Slot {Slot}: restarting the show at {Position} ({Reason}), output frozen", _index, position, reason);
            SetState(SlotState.Recovering, error: reason);
            try
            {
                await _host.CloseAsync(handle, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Slot {Slot}: close before the restart failed", _index);
            }
            lock (_sync) _handle = null;
            await OpenAndStartAsync(source, position, copyToCache: false, ct);
            return Status;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// True when the slot has a show the watchdog must keep alive: Ready, or Error with an open presentation
    /// (a command that hung or a show that vanished puts the slot in Error; the watchdog brings it back).
    /// </summary>
    public bool IsActive
    {
        get { lock (_sync) return IsWatchedState && _handle is not null; }
    }

    private bool IsWatchedState => _state is SlotState.Ready or SlotState.Error;

    private async Task OpenAndStartAsync(string source, ShowPosition target, bool copyToCache, CancellationToken ct)
    {
        try
        {
            var openPath = source;
            if (_options.Cache is not null)
            {
                var cached = Path.Combine(_options.Cache.FolderFor(source), Path.GetFileName(source));
                openPath = copyToCache || !File.Exists(cached) ? _options.Cache.Prepare(source) : cached;
            }

            var handle = await _host.OpenAsync(openPath, _index, ct);
            lock (_sync) _handle = handle;

            var info = await _host.InspectAsync(handle, ct);
            lock (_sync) _notes = info.Notes ?? [];
            if (_options.Evaluate is not null)
            {
                var report = _options.Evaluate(info);
                lock (_sync) _preflight = report;
                foreach (var w in report.Warnings) _log.LogWarning("Slot {Slot}: pre-flight: {Warning}", _index, w);
            }

            // Before the show starts: exporting while a show runs delays navigation by 0.3-0.6 s.
            await ExportThumbnailsAsync(handle, source, regenerate: copyToCache, ct);

            var slide = target.Slide < 1 || target.Slide > handle.SlideCount ? 1 : target.Slide;
            var monitor = _resolveMonitor();
            await _host.StartShowAsync(handle, monitor, slide, ct);
            _seekPending = false;
            if (target.Click > 0 && slide == target.Slide) await _host.GotoClickAsync(handle, slide, target.Click, ct);
            var hp = await _host.GetPositionAsync(handle, ct);
            SetPosition(new ShowPosition(hp.Slide, Math.Min(target.Slide == slide ? target.Click : 0, hp.ClickCount)), hp.ClickCount);

            if (_options.HoldRelease > TimeSpan.Zero) await Task.Delay(_options.HoldRelease, ct);
            SetState(SlotState.Ready, error: null);
            _log.LogInformation("Slot {Slot}: ready on {Monitor}, {Count} slides, position {Position}",
                _index, monitor, handle.SlideCount, _position);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail("Load failed", ex);
            throw;
        }
    }

    private async Task UnloadCoreAsync(CancellationToken ct)
    {
        var handle = _handle!;
        SetState(SlotState.Unloading, error: null); // output freezes before the show window disappears
        _log.LogInformation("Slot {Slot}: unloading '{Path}'", _index, _source);
        try
        {
            await _host.CloseAsync(handle, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The slot is released anyway; the watchdog deals with a hung PowerPoint.
            _log.LogError(ex, "Slot {Slot}: close failed", _index);
        }
        lock (_sync)
        {
            _handle = null;
            _position = default;
            _clickCount = 0;
            _thumbnails = 0;
        }
    }

    // ---- Thumbnails ----

    /// <summary>
    /// Exports every slide (final state) during loading, before the show starts. Recovery reuses existing files.
    /// A failure only loses thumbnails, never the load.
    /// </summary>
    private async Task ExportThumbnailsAsync(SlotHandle handle, string source, bool regenerate, CancellationToken ct)
    {
        if (!_options.Thumbnails || _options.Cache is null) return;
        var folder = _options.Cache.ThumbnailFolder(source);
        var complete = Enumerable.Range(1, handle.SlideCount).All(i => File.Exists(Path.Combine(folder, $"{i}.png")));
        if (complete && !regenerate)
        {
            lock (_sync) _thumbnails = handle.SlideCount;
            return;
        }

        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            Directory.CreateDirectory(folder);
            var started = DateTime.UtcNow;
            for (var slide = 1; slide <= handle.SlideCount; slide++)
            {
                await _host.ExportThumbnailAsync(handle, slide, Path.Combine(folder, $"{slide}.png"),
                    _options.ThumbnailWidth, _options.ThumbnailHeight, ct);
                lock (_sync) _thumbnails = slide;
            }
            _log.LogInformation("Slot {Slot}: {Count} thumbnails in {Ms:F0} ms", _index, handle.SlideCount,
                (DateTime.UtcNow - started).TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("Slot {Slot}: thumbnails not exported: {Message}", _index, ex.Message);
        }
    }

    // ---- Navigation ----

    public Task<SlotStatus> NextAsync(CancellationToken ct) => NavigateAsync("Next", async (h, before) =>
    {
        if (before.Slide >= h.SlideCount && before.Click >= _clickCount)
        {
            // Next past the end would show PowerPoint's end screen or close the show: never on air.
            _log.LogInformation("Slot {Slot}: Next ignored, already at the end ({Position})", _index, before);
            return null;
        }
        await _host.NextAsync(h, ct);
        var hp = await _host.GetPositionAsync(h, ct);
        _seekPending = hp.Slide == before.Slide && hp.ClickIndex == before.Click;
        if (_seekPending)
            _log.LogInformation("Slot {Slot}: Next stayed on {Position}: PowerPoint fast-forwarded the slide's automatic animations, the next press goes on",
                _index, before);
        return (new ShowPosition(hp.Slide, hp.Slide == before.Slide ? hp.ClickIndex : 0), hp);
    }, ct);

    public Task<SlotStatus> PreviousAsync(CancellationToken ct) => NavigateAsync("Previous", async (h, before) =>
    {
        if (before is { Slide: 1, Click: 0 })
        {
            _log.LogInformation("Slot {Slot}: Previous ignored, already at the start", _index);
            return null;
        }
        if (_seekPending)
        {
            // View.Previous now would undo the fast-forward and freeze the slide's media for good.
            // Go where Previous leads from the slide start instead: the previous slide in its final state.
            _seekPending = false;
            if (before.Slide == 1)
            {
                _log.LogInformation("Slot {Slot}: Previous ignored after a fast-forward on the first slide", _index);
                return null;
            }
            _log.LogInformation("Slot {Slot}: Previous after a fast-forward on {Position}: going to slide {Slide} directly",
                _index, before, before.Slide - 1);
            return await PreviousSlideFinalStateAsync(h, before.Slide - 1, ct);
        }
        await _host.PreviousAsync(h, ct);
        var hp = await _host.GetPositionAsync(h, ct);
        if (hp.Slide == before.Slide && hp.ClickIndex < 0)
        {
            // On a slide that starts by itself (effects "with/after previous" first: a video chain, an intro),
            // Previous from its start only undoes that start: PowerPoint stays on the slide, reports click -1 and
            // the media freezes. From the start the operator meant the previous slide.
            if (before.Click == 0 && before.Slide > 1)
            {
                _log.LogInformation("Slot {Slot}: Previous stayed on slide {Slide} (click -1, automatic start undone), going to slide {Prev}",
                    _index, hp.Slide, before.Slide - 1);
                return await PreviousSlideFinalStateAsync(h, before.Slide - 1, ct);
            }
            _log.LogWarning("Slot {Slot}: Previous left slide {Slide} frozen (click -1) from {Position}, restarting the slide",
                _index, hp.Slide, before);
            await _host.GotoSlideAsync(h, hp.Slide, ct);
            hp = await _host.GetPositionAsync(h, ct);
            return (new ShowPosition(hp.Slide, 0), hp);
        }
        if (hp.Slide == before.Slide) return (new ShowPosition(hp.Slide, hp.ClickIndex), hp);

        // Back to the previous slide: PowerPoint shows it in its final state but reports click 0.
        if (hp.ClickIndex != hp.ClickCount)
            _log.LogDebug("Slot {Slot}: host reports click {Reported} after Previous, using final state {Final}",
                _index, hp.ClickIndex, hp.ClickCount);
        return (new ShowPosition(hp.Slide, hp.ClickCount), hp);
    }, ct);

    /// <summary>Where Previous from a slide start leads: the previous slide in its final state.</summary>
    private async Task<(ShowPosition, HostPosition)> PreviousSlideFinalStateAsync(SlotHandle h, int slide, CancellationToken ct)
    {
        await _host.GotoSlideAsync(h, slide, ct);
        var prev = await _host.GetPositionAsync(h, ct);
        if (prev.ClickCount > 0)
        {
            await _host.GotoClickAsync(h, slide, prev.ClickCount, ct);
            prev = await _host.GetPositionAsync(h, ct);
        }
        return (new ShowPosition(prev.Slide, prev.ClickCount), prev);
    }

    public Task<SlotStatus> FirstAsync(CancellationToken ct) => GotoAsync(1, 0, ct);

    public Task<SlotStatus> LastAsync(CancellationToken ct)
    {
        var count = Status.SlideCount;
        if (count == 0) throw new SlotStateException($"Slot {_index} has no presentation");
        return GotoAsync(count, 0, ct);
    }

    public Task<SlotStatus> GotoAsync(int slide, int click, CancellationToken ct) => NavigateAsync("Goto", async (h, _) =>
    {
        if (slide < 1 || slide > h.SlideCount)
            throw new ArgumentOutOfRangeException(nameof(slide), $"Slide must be 1..{h.SlideCount}");
        if (click < 0) throw new ArgumentOutOfRangeException(nameof(click), "Click must be >= 0");
        _seekPending = false;

        if (click == 0) await _host.GotoSlideAsync(h, slide, ct);
        else await _host.GotoClickAsync(h, slide, click, ct);
        var hp = await _host.GetPositionAsync(h, ct);
        return (new ShowPosition(slide, Math.Min(click, hp.ClickCount)), hp);
    }, ct);

    private async Task<SlotStatus> NavigateAsync(string name,
        Func<SlotHandle, ShowPosition, Task<(ShowPosition Position, HostPosition Host)?>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_state != SlotState.Ready || _handle is null)
                throw new SlotStateException($"Slot {_index} is {_state}, {name} is not possible");

            var before = _position;
            (ShowPosition Position, HostPosition Host)? result;
            try
            {
                result = await action(_handle, before);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or ArgumentException))
            {
                Fail($"{name} failed", ex);
                throw;
            }

            if (result is { } r)
            {
                if (!r.Host.Running)
                {
                    Fail($"{name}: the show is no longer running", null);
                    throw new SlotStateException($"Slot {_index}: the show has ended");
                }
                SetPosition(r.Position, r.Host.ClickCount);
                if (r.Host.ClickIndex != r.Position.Click)
                    _log.LogDebug("Slot {Slot}: {Name} {Before} -> {After} (host click {HostClick})",
                        _index, name, before, r.Position, r.Host.ClickIndex);
                else
                    _log.LogDebug("Slot {Slot}: {Name} {Before} -> {After}", _index, name, before, r.Position);
            }
            return Status;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- Output and tally ----

    public SlotStatus SetBlack(bool on)
    {
        lock (_sync) _black = on;
        _log.LogInformation("Slot {Slot}: black {State}", _index, on ? "on" : "off");
        ApplyOutputMode();
        return Status;
    }

    public SlotStatus SetFreeze(bool on)
    {
        lock (_sync) _freeze = on;
        _log.LogInformation("Slot {Slot}: freeze {State}", _index, on ? "on" : "off");
        ApplyOutputMode();
        return Status;
    }

    /// <summary>vMix tally of the slot's input.</summary>
    public void SetTally(Tally tally)
    {
        lock (_sync)
        {
            if (_tally == tally) return;
            _tally = tally;
        }
        _log.LogInformation("Slot {Slot}: tally program={Program} preview={Preview}", _index, tally.OnProgram, tally.OnPreview);
        RaiseChanged();
    }

    private void GuardOnAir(string command, bool force)
    {
        bool onProgram;
        lock (_sync) onProgram = _tally.OnProgram;
        if (onProgram && !force)
            throw new OnAirException($"Slot {_index} is on program in vMix: {command} requires force=true");
        if (onProgram) _log.LogWarning("Slot {Slot}: {Command} on program forced by the operator", _index, command);
    }

    private OutputMode ComputeOutputMode()
    {
        if (_black) return OutputMode.Black;
        if (_freeze || _state != SlotState.Ready) return OutputMode.Freeze;
        return OutputMode.Live;
    }

    private void ApplyOutputMode()
    {
        OutputMode mode;
        lock (_sync) mode = ComputeOutputMode();
        if (_output.Mode != mode)
        {
            _output.Mode = mode;
            _log.LogDebug("Slot {Slot}: output {Mode}", _index, mode);
        }
        RaiseChanged();
    }

    // ---- State helpers ----

    private void SetState(SlotState state, string? error)
    {
        lock (_sync)
        {
            _state = state;
            _error = error;
        }
        ApplyOutputMode();
    }

    private void SetPosition(ShowPosition position, int clickCount)
    {
        lock (_sync)
        {
            _position = position;
            _clickCount = clickCount;
        }
        RaiseChanged();
    }

    private void Fail(string what, Exception? ex)
    {
        var message = ex is null ? what : $"{what}: {ex.Message}";
        if (ex is null) _log.LogError("Slot {Slot}: {Message}", _index, message);
        else _log.LogError(ex, "Slot {Slot}: {Message}", _index, message);
        SetState(SlotState.Error, message);
    }

    private void RaiseChanged()
    {
        var handler = Changed;
        if (handler is null) return;
        try { handler(Status); }
        catch (Exception ex) { _log.LogWarning(ex, "Slot {Slot}: status subscriber failed", _index); }
    }
}
