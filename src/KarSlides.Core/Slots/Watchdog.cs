using System.Diagnostics;
using KarSlides.Core.Configuration;
using KarSlides.Core.Model;
using Microsoft.Extensions.Logging;

namespace KarSlides.Core.Slots;

/// <summary>The PowerPoint process behind all slots, as the watchdog sees it.</summary>
public interface IPresentationRuntime
{
    /// <summary>POWERPNT.EXE that serves the slots is running.</summary>
    bool IsProcessAlive { get; }

    /// <summary>Kills POWERPNT.EXE (hung or not) and replaces the COM host; PowerPoint starts again on the next open.</summary>
    Task RestartAsync(CancellationToken ct);
}

public sealed record RecoveryReport(string Reason, TimeSpan Duration, bool Success, IReadOnlyList<SlotStatus> Slots);

/// <summary>
/// Heartbeat and recovery. Once a second every slot with a show is pinged (position read with a
/// short timeout). Three missed heartbeats in a row, POWERPNT.EXE gone, or a show that closed by itself trigger
/// recovery: outputs freeze on the last frame, PowerPoint is killed and restarted, slots reopen from the cache
/// at the same slide and click, outputs go live again. Goal: under 15 s, no black frame.
/// The heartbeat also catches a show PowerPoint paused: a warning, and optionally a restart of the show.
/// </summary>
public sealed class Watchdog(SlotSet slots, IPresentationRuntime runtime, WatchdogOptions options, ILogger log,
    AppStatus? status = null, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<int, string?> _pauseWarnings = new();
    private readonly Dictionary<int, long> _lastPausedRestart = new();
    private int _missed;

    public event Action<RecoveryReport>? Recovered;

    public int Missed => _missed;

    public async Task RunAsync(CancellationToken ct)
    {
        log.LogInformation("Watchdog started: heartbeat {Heartbeat} ms, limit {Limit} missed", options.HeartbeatMs, options.MissedLimit);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
                await Task.Delay(options.HeartbeatMs, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Watchdog tick failed");
            }
        }
    }

    /// <summary>One heartbeat. Returns the recovery report if a recovery was performed.</summary>
    public async Task<RecoveryReport?> TickAsync(CancellationToken ct)
    {
        var all = slots.All;
        var watched = all.Where(s => s.IsActive).ToList();
        foreach (var s in all.Except(watched)) SetPauseWarning(s.Index, null); // unloaded: its warning goes too
        if (watched.Count == 0)
        {
            _missed = 0;
            return null;
        }
        // A loading slot keeps PowerPoint busy (open, thumbnails): not a hang.
        if (all.Any(s => s.Status.State is SlotState.Loading or SlotState.Unloading or SlotState.Recovering))
            return null;

        if (!runtime.IsProcessAlive)
            return await RecoverAsync("POWERPNT.EXE is not running", ct);

        foreach (var slot in watched)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.HeartbeatTimeoutMs);
            HostPosition health;
            try
            {
                health = await slot.PingAsync(timeout.Token);
                if (!health.Running)
                    return await RecoverAsync($"slot {slot.Index}: the slide show closed", ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _missed++;
                log.LogWarning("Watchdog: slot {Slot} missed heartbeat {Missed}/{Limit}: {Message}",
                    slot.Index, _missed, options.MissedLimit, ex is OperationCanceledException ? "no answer" : ex.Message);
                if (_missed >= options.MissedLimit)
                    return await RecoverAsync($"PowerPoint did not answer {_missed} heartbeats", ct);
                return null;
            }
            await CheckPausedAsync(slot, health, ct);
        }

        _missed = 0;
        return null;
    }

    /// <summary>
    /// PowerPoint pauses the show when a document window opens in its process and never resumes it; videos stop
    /// playing. COM cannot resume it without activating the show window, so: warn, and with
    /// <see cref="WatchdogOptions.RestartPausedShow"/> restart the show once the document is closed.
    /// </summary>
    private async Task CheckPausedAsync(SlotController slot, HostPosition health, CancellationToken ct)
    {
        string? warning = null;
        if (health.Paused)
        {
            var restartable = options.RestartPausedShow && health.OtherWindows == 0;
            var now = _time.GetTimestamp();
            var recent = _lastPausedRestart.TryGetValue(slot.Index, out var last) &&
                         _time.GetElapsedTime(last, now).TotalMilliseconds < options.PausedRestartIntervalMs;
            if (restartable && !recent)
            {
                _lastPausedRestart[slot.Index] = now;
                SetPauseWarning(slot.Index, null);
                await slot.RestartShowAsync("the slide show was paused by PowerPoint", ct);
                return;
            }
            warning = health.OtherWindows > 0
                ? $"Slot {slot.Index}: PowerPoint paused the slide show because a document is open in it " +
                  $"({health.OtherWindows} window(s)): videos do not play. Close it" +
                  (options.RestartPausedShow ? ", KarSlides then restarts the show" : ", then reload the presentation")
                : recent
                    ? $"Slot {slot.Index}: the slide show is paused again right after a restart: reload the presentation"
                    : $"Slot {slot.Index}: PowerPoint paused the slide show, videos do not play: reload the presentation";
        }
        SetPauseWarning(slot.Index, warning);
    }

    private void SetPauseWarning(int slot, string? warning)
    {
        if (_pauseWarnings.TryGetValue(slot, out var old) && old == warning) return;
        _pauseWarnings[slot] = warning;
        status?.SetWarning($"slot{slot}-paused", warning);
        if (warning is not null) log.LogWarning("Watchdog: {Warning}", warning);
        else if (old is not null) log.LogInformation("Watchdog: slot {Slot}: the slide show is not paused any more", slot);
    }

    public async Task<RecoveryReport> RecoverAsync(string reason, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        log.LogError("Watchdog: {Reason}. Recovering: freeze outputs, restart PowerPoint, restore positions", reason);
        var all = slots.All;
        foreach (var s in all) s.BeginRecovery(reason);

        var success = true;
        try
        {
            await runtime.RestartAsync(ct);
            log.LogInformation("Watchdog: PowerPoint restarted after {Ms} ms", sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Watchdog: PowerPoint restart failed");
            success = false;
        }

        foreach (var s in all)
        {
            try
            {
                await s.RecoverAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Watchdog: slot {Slot} could not be restored", s.Index);
                success = false;
            }
        }

        _missed = 0;
        var report = new RecoveryReport(reason, sw.Elapsed, success, all.Select(s => s.Status).ToArray());
        if (success)
            log.LogWarning("Watchdog: recovered in {Ms} ms ({Reason}); positions: {Positions}", sw.ElapsedMilliseconds, reason,
                string.Join(", ", report.Slots.Where(s => s.File is not null).Select(s => $"slot {s.Slot} {s.Slide}.{s.Click}")));
        else
            log.LogError("Watchdog: recovery incomplete after {Ms} ms ({Reason})", sw.ElapsedMilliseconds, reason);
        Recovered?.Invoke(report);
        return report;
    }
}
