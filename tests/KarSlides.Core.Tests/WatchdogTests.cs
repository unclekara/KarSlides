using KarSlides.Core.Abstractions;
using KarSlides.Core.Configuration;
using KarSlides.Core.Model;
using KarSlides.Core.Preflight;
using KarSlides.Core.Slots;
using KarSlides.Core.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace KarSlides.Core.Tests;

/// <summary>
/// A host that can hang: while <see cref="Hang"/> is set every call blocks until the token is cancelled
/// (like a PowerPoint stuck on the STA thread). Restart = new inner fake, as PowerPointRuntime does.
/// </summary>
internal sealed class HangingRuntime(Func<FakePresentationHost> create) : IPresentationHost, IPresentationRuntime
{
    public FakePresentationHost Inner { get; private set; } = create();
    public bool Hang { get; set; }
    public bool Alive { get; set; } = true;
    public int Restarts { get; private set; }

    public bool IsProcessAlive => Alive;

    public Task RestartAsync(CancellationToken ct)
    {
        Restarts++;
        Hang = false;
        Alive = true;
        Inner = create();
        return Task.CompletedTask;
    }

    private async Task Gate(CancellationToken ct)
    {
        if (Hang) await Task.Delay(Timeout.Infinite, ct);
    }

    public async Task<SlotHandle> OpenAsync(string p, int i, CancellationToken ct) { await Gate(ct); return await Inner.OpenAsync(p, i, ct); }
    public async Task StartShowAsync(SlotHandle h, MonitorTarget m, int s, CancellationToken ct) { await Gate(ct); await Inner.StartShowAsync(h, m, s, ct); }
    public async Task NextAsync(SlotHandle h, CancellationToken ct) { await Gate(ct); await Inner.NextAsync(h, ct); }
    public async Task PreviousAsync(SlotHandle h, CancellationToken ct) { await Gate(ct); await Inner.PreviousAsync(h, ct); }
    public async Task GotoSlideAsync(SlotHandle h, int s, CancellationToken ct) { await Gate(ct); await Inner.GotoSlideAsync(h, s, ct); }
    public async Task GotoClickAsync(SlotHandle h, int s, int c, CancellationToken ct) { await Gate(ct); await Inner.GotoClickAsync(h, s, c, ct); }
    public async Task<HostPosition> GetPositionAsync(SlotHandle h, CancellationToken ct) { await Gate(ct); return await Inner.GetPositionAsync(h, ct); }
    public async Task CloseAsync(SlotHandle h, CancellationToken ct) { await Gate(ct); await Inner.CloseAsync(h, ct); }
    public Task<PresentationInfo> InspectAsync(SlotHandle h, CancellationToken ct) => Inner.InspectAsync(h, ct);
    public Task ExportThumbnailAsync(SlotHandle h, int s, string p, int w, int hh, CancellationToken ct) => Inner.ExportThumbnailAsync(h, s, p, w, hh, ct);
}

public sealed class WatchdogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"karslides-wd-{Guid.NewGuid():N}");
    private readonly string _file;
    private readonly HangingRuntime _runtime = new(() => new FakePresentationHost(0, 2, 1));
    private readonly FakeOutputSink _output = new();
    private readonly SlotController _slot;
    private readonly Watchdog _watchdog;

    public WatchdogTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "talk.pptx");
        File.WriteAllText(_file, "x");
        _slot = new SlotController(1, _runtime, _output, () => new MonitorTarget(@"\\.\DISPLAY5", 1920, 0, 1920, 1080),
            new SlotOptions { HoldRelease = TimeSpan.Zero, Cache = new FileCache(Path.Combine(_dir, "cache")) },
            NullLogger.Instance);
        _watchdog = new Watchdog(new SlotSet([_slot]), _runtime,
            new WatchdogOptions { HeartbeatTimeoutMs = 50, MissedLimit = 3 }, NullLogger.Instance);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task HealthyShow_NoRecovery()
    {
        await _slot.LoadAsync(_file, 1, default);
        for (var i = 0; i < 5; i++) Assert.Null(await _watchdog.TickAsync(default));
        Assert.Equal(0, _runtime.Restarts);
    }

    [Fact]
    public async Task EmptySlot_IsNotWatched_EvenWithoutPowerPoint()
    {
        _runtime.Alive = false;
        Assert.Null(await _watchdog.TickAsync(default));
        Assert.Equal(0, _runtime.Restarts);
    }

    [Fact]
    public async Task HungPowerPoint_ThreeMissedHeartbeats_RestartsAndRestoresPosition()
    {
        await _slot.LoadAsync(_file, 3, default);
        await _slot.PreviousAsync(default); // 2.2 (final state)
        _runtime.Hang = true;

        Assert.Null(await _watchdog.TickAsync(default));
        Assert.Null(await _watchdog.TickAsync(default));
        Assert.Equal(2, _watchdog.Missed);
        var report = await _watchdog.TickAsync(default);

        Assert.NotNull(report);
        Assert.True(report!.Success);
        Assert.Equal(1, _runtime.Restarts);
        var s = _slot.Status;
        Assert.Equal(SlotState.Ready, s.State);
        Assert.Equal((2, 2), (s.Slide, s.Click));
        Assert.Equal(new ShowPosition(2, 2), _runtime.Inner.Truth);
        Assert.DoesNotContain(OutputMode.Black, _output.Modes);
        Assert.Equal(OutputMode.Live, _output.Mode);
    }

    [Fact]
    public async Task OneMissedHeartbeat_ThenHealthy_ResetsCounter()
    {
        await _slot.LoadAsync(_file, 1, default);
        _runtime.Hang = true;
        await _watchdog.TickAsync(default);
        _runtime.Hang = false;
        await _watchdog.TickAsync(default);
        Assert.Equal(0, _watchdog.Missed);
        Assert.Equal(0, _runtime.Restarts);
    }

    [Fact]
    public async Task ProcessGone_RecoversImmediately()
    {
        await _slot.LoadAsync(_file, 2, default);
        _runtime.Alive = false;

        var report = await _watchdog.TickAsync(default);

        Assert.Contains("not running", report!.Reason);
        Assert.Equal(2, _slot.Status.Slide);
        Assert.Equal(SlotState.Ready, _slot.Status.State);
    }

    [Fact]
    public async Task ShowClosedBySomeone_Recovers()
    {
        await _slot.LoadAsync(_file, 2, default);
        _runtime.Inner.Crash();

        var report = await _watchdog.TickAsync(default);

        Assert.Contains("closed", report!.Reason);
        Assert.Equal(SlotState.Ready, _slot.Status.State);
    }

    [Fact]
    public async Task CommandThatHung_PutsSlotInError_WatchdogBringsItBack()
    {
        await _slot.LoadAsync(_file, 1, default);
        _runtime.Hang = true;
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _slot.NextAsync(cts.Token));
        // A COM timeout throws TimeoutException in the real host; here the command was cancelled.
        Assert.True(_slot.IsActive || _slot.Status.State == SlotState.Ready);

        for (var i = 0; i < 3; i++) await _watchdog.TickAsync(default);

        Assert.Equal(1, _runtime.Restarts);
        Assert.Equal(SlotState.Ready, _slot.Status.State);
    }

    [Fact]
    public async Task RecoveredEvent_IsRaised()
    {
        RecoveryReport? seen = null;
        _watchdog.Recovered += r => seen = r;
        await _slot.LoadAsync(_file, 1, default);
        _runtime.Alive = false;

        await _watchdog.TickAsync(default);

        Assert.NotNull(seen);
        Assert.True(seen!.Duration < TimeSpan.FromSeconds(15));
    }

    // ---- Show paused by PowerPoint ----

    private sealed class ManualTime : TimeProvider
    {
        public long Now { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Now;
    }

    private (Watchdog Watchdog, AppStatus Status, ManualTime Time) PauseWatchdog(bool restart)
    {
        var status = new AppStatus();
        var time = new ManualTime();
        var wd = new Watchdog(new SlotSet([_slot]), _runtime,
            new WatchdogOptions { HeartbeatTimeoutMs = 50, RestartPausedShow = restart, PausedRestartIntervalMs = 30000 },
            NullLogger.Instance, status, time);
        return (wd, status, time);
    }

    private async Task LoadAtTwoOne()
    {
        await _slot.LoadAsync(_file, 2, default);
        await _slot.NextAsync(default); // 2.1
    }

    [Fact]
    public async Task Paused_WithDocumentOpen_WarnsAndWaits()
    {
        var (wd, status, _) = PauseWatchdog(restart: true);
        await LoadAtTwoOne();
        (_runtime.Inner.Paused, _runtime.Inner.OtherWindows) = (true, 1);
        var calls = _runtime.Inner.Calls.Count;

        Assert.Null(await wd.TickAsync(default));

        Assert.Contains(status.Problems, p => p.Contains("document is open") && p.Contains("restarts the show"));
        Assert.Equal(calls, _runtime.Inner.Calls.Count); // nothing restarted while the document is open
        Assert.Equal(0, _runtime.Restarts);
    }

    [Fact]
    public async Task Paused_DocumentClosed_RestartsShowAtSamePosition_WithoutKillingPowerPoint()
    {
        var (wd, status, _) = PauseWatchdog(restart: true);
        await LoadAtTwoOne();
        (_runtime.Inner.Paused, _runtime.Inner.OtherWindows) = (true, 1);
        await wd.TickAsync(default);

        _runtime.Inner.OtherWindows = 0;
        var modes = new List<OutputMode>();
        _slot.Changed += _ => modes.Add(_output.Mode);
        await wd.TickAsync(default);

        Assert.Equal(0, _runtime.Restarts);
        Assert.Contains("Close", _runtime.Inner.Calls);
        Assert.Equal("Start 2", _runtime.Inner.Calls.Last(c => c.StartsWith("Start")));
        Assert.False(_runtime.Inner.Paused);
        Assert.Equal((2, 1), (_slot.Status.Slide, _slot.Status.Click));
        Assert.Equal(SlotState.Ready, _slot.Status.State);
        Assert.Contains(OutputMode.Freeze, modes);
        Assert.DoesNotContain(OutputMode.Black, modes);
        Assert.Empty(status.Problems);
    }

    [Fact]
    public async Task Paused_OptionOff_OnlyWarns()
    {
        var (wd, status, _) = PauseWatchdog(restart: false);
        await LoadAtTwoOne();
        _runtime.Inner.Paused = true;
        var calls = _runtime.Inner.Calls.Count;

        await wd.TickAsync(default);

        Assert.Contains(status.Problems, p => p.Contains("reload the presentation"));
        Assert.Equal(calls, _runtime.Inner.Calls.Count);
    }

    [Fact]
    public async Task PausedAgainRightAfterRestart_NoSecondRestartWithinInterval()
    {
        var (wd, status, time) = PauseWatchdog(restart: true);
        await LoadAtTwoOne();
        _runtime.Inner.Paused = true;
        await wd.TickAsync(default); // restart 1
        var starts = _runtime.Inner.Calls.Count(c => c.StartsWith("Start"));

        _runtime.Inner.Paused = true;
        time.Now += 5000;
        await wd.TickAsync(default);
        Assert.Equal(starts, _runtime.Inner.Calls.Count(c => c.StartsWith("Start")));
        Assert.Contains(status.Problems, p => p.Contains("paused again"));

        time.Now += 30000;
        await wd.TickAsync(default);
        Assert.Equal(starts + 1, _runtime.Inner.Calls.Count(c => c.StartsWith("Start")));
    }

    [Fact]
    public async Task PauseWarning_ClearsWhenResumed_AndOnUnload()
    {
        var (wd, status, _) = PauseWatchdog(restart: false);
        await LoadAtTwoOne();
        _runtime.Inner.Paused = true;
        await wd.TickAsync(default);
        Assert.NotEmpty(status.Problems);

        _runtime.Inner.Paused = false;
        await wd.TickAsync(default);
        Assert.Empty(status.Problems);

        _runtime.Inner.Paused = true;
        await wd.TickAsync(default);
        await _slot.UnloadAsync(default);
        await wd.TickAsync(default);
        Assert.Empty(status.Problems);
    }
}
