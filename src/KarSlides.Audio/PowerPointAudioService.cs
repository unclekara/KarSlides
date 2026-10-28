using System.Diagnostics;
using KarSlides.Core.Audio;
using Microsoft.Extensions.Logging;

namespace KarSlides.Audio;

/// <summary>
/// Keeps a loopback capture on the current POWERPNT.EXE: one PowerPoint process for all slots, so its
/// sound goes to one ring (the audio owner slot). Checks once a second; when PowerPoint is (re)started – by a load
/// or by the watchdog – capture moves to the new process. Without PowerPoint the ring runs dry and the output
/// sends silence. The same loop keeps PowerPoint on its playback device (<see cref="AudioRouteKeeper"/>);
/// without a ring (Audio.Enabled off) only that runs.
/// </summary>
public sealed class PowerPointAudioService : IDisposable
{
    private readonly AudioRing? _ring;
    private readonly AudioRouteKeeper _route;
    private readonly ILogger _log;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new(false);
    private ProcessLoopbackCapture? _capture;

    public PowerPointAudioService(AudioRing? ring, AudioRouteKeeper route, ILogger<PowerPointAudioService> log)
    {
        _ring = ring;
        _route = route;
        _log = log;
        _thread = new Thread(Loop) { IsBackground = true, Name = "PowerPointAudio" };
    }

    public static AudioRing CreateRing(double bufferMs) =>
        new(ProcessLoopbackCapture.SampleRate, ProcessLoopbackCapture.Channels, bufferMs);

    public void Start() => _thread.Start();

    /// <summary>The playback device PowerPoint plays to: at start, for each new PowerPoint process and every few seconds.</summary>
    public event Action<AudioRouteState>? DeviceChecked;

    private void Loop()
    {
        do
        {
            try
            {
                var pid = Process.GetProcessesByName("POWERPNT").OrderBy(p => p.StartTime).Select(p => (uint?)p.Id).FirstOrDefault();
                if (_route.Tick(pid)) DeviceChecked?.Invoke(_route.State);
                if (_ring is not null) Check(pid, _ring);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "PowerPoint audio check failed");
            }
        } while (!_stop.Wait(1000));

        _capture?.Dispose();
        _route.Release();
    }

    private void Check(uint? pid, AudioRing ring)
    {
        if (_capture is not null && (pid != _capture.ProcessId || _capture.Failed))
        {
            _log.LogInformation("PowerPoint process {Old} gone or capture failed, stopping its audio capture", _capture.ProcessId);
            _capture.Dispose();
            _capture = null;
            ring.Reset();
        }

        if (_capture is null && pid is { } p)
        {
            var capture = new ProcessLoopbackCapture(p, ring, _log);
            try
            {
                capture.Start();
                _capture = capture;
                _failedPid = null;
            }
            catch (Exception ex)
            {
                capture.Dispose();
                // Retried every second; the full error once per process.
                if (_failedPid != p) _log.LogError(ex, "Audio loopback of PowerPoint pid {Pid} failed, retrying", p);
                _failedPid = p;
            }
        }
    }

    private uint? _failedPid;

    public void Dispose()
    {
        _stop.Set();
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(3));
        _stop.Dispose();
    }
}
