using Microsoft.Extensions.Logging;

namespace KarSlides.Core.Audio;

/// <summary>An active playback device. <see cref="Id"/> is the MMDevice id ({0.0.0.00000000}.{guid}).</summary>
public sealed record PlaybackDevice(string Id, string Name, bool IsDefault, bool EnhancementsEnabled);

/// <summary>
/// Playback device of a process, set from outside: PowerPoint has no device setting and always plays to the
/// Windows default. The process loopback for NDI does not depend on the device.
/// </summary>
public interface IAudioRouter
{
    IReadOnlyList<PlaybackDevice> Devices();

    /// <summary>Routes the process to the device; null — back to the Windows default.</summary>
    void Route(uint pid, string? deviceId);

    /// <summary>The device the process is routed to, "" when it follows the Windows default.</summary>
    string Routed(uint pid);
}

/// <summary>Where PowerPoint plays now (null: no devices) and what is wrong with it, if anything.</summary>
public sealed record AudioRouteState(PlaybackDevice? Effective, string? Warning);

/// <summary>
/// Keeps POWERPNT.EXE on the configured playback device (Audio.OutputDevice, "" = Windows default). Windows
/// persists the choice per application, so: it is (re)applied to every new PowerPoint process (load, watchdog
/// restart), re-applied when the device comes back, and reset to the default on exit — otherwise PowerPoint
/// started by hand later would still play there. With "" a choice left over by a crash is reset.
/// Called once a second from the audio thread; does real work on a new process and every
/// <see cref="CheckEveryTicks"/> ticks. A PowerPoint that has just started does not answer the device query for a
/// few seconds (E_INVALIDARG, measured): it is routed anyway, a failed attempt is retried on the next tick and
/// reported after <see cref="FailuresBeforeWarning"/> in a row.
/// </summary>
public sealed class AudioRouteKeeper(IAudioRouter router, string deviceId, ILogger log)
{
    public const int CheckEveryTicks = 5;
    public const int FailuresBeforeWarning = 3;

    private readonly string _deviceId = deviceId.Trim();
    private uint? _pid;
    private bool _routed;
    private bool _leftoverChecked;
    private int _failures;
    private int _ticks = CheckEveryTicks - 1; // the first tick checks
    private string? _lastLogged;
    private AudioRouteState _state = new(null, null);

    public string DeviceId => _deviceId;

    public AudioRouteState State => _state;

    /// <summary>Returns true when the state was re-evaluated (new process or periodic check).</summary>
    public bool Tick(uint? pid)
    {
        var newProcess = pid != _pid;
        if (newProcess)
        {
            _pid = pid;
            _routed = false;
            _leftoverChecked = false;
            _failures = 0;
        }
        if (!newProcess && ++_ticks < CheckEveryTicks) return false;
        _ticks = 0;
        _state = Evaluate(pid);
        return true;
    }

    private AudioRouteState Evaluate(uint? pid)
    {
        IReadOnlyList<PlaybackDevice> devices;
        try
        {
            devices = router.Devices();
        }
        catch (Exception ex)
        {
            return Report(new AudioRouteState(null, $"Cannot list playback devices: {ex.Message}"), ex);
        }
        var fallback = devices.FirstOrDefault(d => d.IsDefault);

        if (_deviceId.Length == 0)
        {
            // Once per process: a choice made by hand in the volume mixer later is left alone.
            if (pid is { } p && !_leftoverChecked) Try(() =>
            {
                var left = router.Routed(p);
                _leftoverChecked = true;
                if (left.Length == 0) return;
                log.LogInformation("PowerPoint pid {Pid} was routed to {Device} (left over), back to the Windows default", p, Name(left, devices));
                router.Route(p, null);
            });
            return Report(new AudioRouteState(fallback, null));
        }

        var target = devices.FirstOrDefault(d => d.Id.Equals(_deviceId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
            return Report(new AudioRouteState(fallback,
                $"PowerPoint playback device is not connected ({_deviceId}): PowerPoint plays to the Windows default " +
                $"'{fallback?.Name ?? "none"}'. Connect it or choose another one in the settings"));
        if (pid is not { } pp) return Report(new AudioRouteState(target, null));

        var error = Try(() =>
        {
            string current;
            try { current = router.Routed(pp); }
            catch { current = "?"; } // not answering yet: route anyway
            if (current.Equals(_deviceId, StringComparison.OrdinalIgnoreCase))
            {
                _routed = true;
                return;
            }
            router.Route(pp, _deviceId);
            _routed = true;
            log.LogInformation("PowerPoint pid {Pid} routed to playback device {Device} (was {Was})", pp, target.Name,
                current switch { "" => "Windows default", "?" => "unknown", _ => Name(current, devices) });
        });
        if (error is null)
        {
            _failures = 0;
            return Report(new AudioRouteState(target, null));
        }
        _ticks = CheckEveryTicks - 1; // retry on the next tick
        if (++_failures < FailuresBeforeWarning)
        {
            log.LogDebug("Routing PowerPoint pid {Pid} failed ({Failures}), retrying: {Error}", pp, _failures, error.Message);
            return new AudioRouteState(fallback, _state.Warning);
        }
        return Report(new AudioRouteState(fallback, $"Cannot route PowerPoint to '{target.Name}': {error.Message}"), error);
    }

    /// <summary>Back to the Windows default before KarSlides exits (PowerPoint is still running then).</summary>
    public void Release()
    {
        if (_pid is not { } pid || !_routed) return;
        var error = Try(() => router.Route(pid, null));
        if (error is null) log.LogInformation("PowerPoint pid {Pid} back to the Windows default playback device", pid);
        else log.LogWarning(error, "Cannot reset the PowerPoint playback device");
        _routed = false;
    }

    private static Exception? Try(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static string Name(string id, IReadOnlyList<PlaybackDevice> devices) =>
        devices.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Name ?? id;

    /// <summary>Logs a warning once per distinct text, and when it clears.</summary>
    private AudioRouteState Report(AudioRouteState state, Exception? ex = null)
    {
        if (state.Warning != _lastLogged)
        {
            if (state.Warning is not null) log.LogWarning(ex, "{Warning}", state.Warning);
            else if (_lastLogged is not null) log.LogInformation("PowerPoint playback device: {Device}", state.Effective?.Name ?? "none");
            _lastLogged = state.Warning;
        }
        return state;
    }
}
