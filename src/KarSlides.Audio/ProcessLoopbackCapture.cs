using System.Runtime.InteropServices;
using KarSlides.Core.Audio;
using Microsoft.Extensions.Logging;
using static KarSlides.Audio.AudioInterop;

namespace KarSlides.Audio;

/// <summary>
/// Audio of one process tree via WASAPI process loopback (Windows 10 2004+): 48 kHz float stereo, event driven,
/// on a dedicated MTA thread with MMCSS "Pro Audio". Packets keep coming (as digital silence) while the process
/// is quiet, so the stream is continuous.
/// </summary>
internal sealed unsafe class ProcessLoopbackCapture : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    private readonly uint _pid;
    private readonly AudioRing _ring;
    private readonly ILogger _log;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new(false);
    private readonly AutoResetEvent _packetEvent = new(false);
    private volatile bool _stop;
    private Exception? _startError;

    public uint ProcessId => _pid;
    public bool Failed { get; private set; }

    public ProcessLoopbackCapture(uint pid, AudioRing ring, ILogger log)
    {
        _pid = pid;
        _ring = ring;
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = $"Loopback {pid}" };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public void Start()
    {
        _thread.Start();
        _started.Wait();
        if (_startError is not null)
            throw new InvalidOperationException($"Process loopback for pid {_pid} failed: {_startError.Message}", _startError);
    }

    private void Run()
    {
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        var mmcss = IntPtr.Zero;
        try
        {
            client = Activate(_pid);
            var format = new WaveFormatEx
            {
                wFormatTag = WAVE_FORMAT_IEEE_FLOAT,
                nChannels = Channels,
                nSamplesPerSec = SampleRate,
                wBitsPerSample = 32,
                nBlockAlign = Channels * 4,
                nAvgBytesPerSec = SampleRate * Channels * 4,
            };
            const uint flags = AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK |
                               AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
            Check(client.Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 200_000 /* 20 ms */, 0, (IntPtr)(&format), IntPtr.Zero),
                "IAudioClient.Initialize");
            Check(client.SetEventHandle(_packetEvent.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
            var iid = IID_IAudioCaptureClient;
            Check(client.GetService(ref iid, out var svc), "GetService(IAudioCaptureClient)");
            capture = (IAudioCaptureClient)svc;

            uint taskIndex = 0;
            mmcss = AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
            Check(client.Start(), "IAudioClient.Start");
            _log.LogInformation("Audio loopback of pid {Pid} started: {Rate} Hz float x{Channels}, MMCSS {Mmcss}",
                _pid, SampleRate, Channels, mmcss != IntPtr.Zero);
            _started.Set();

            long discontinuities = 0;
            while (!_stop)
            {
                _packetEvent.WaitOne(100);
                while (true)
                {
                    Check(capture.GetNextPacketSize(out var next), "GetNextPacketSize");
                    if (next == 0) break;
                    Check(capture.GetBuffer(out var data, out var frames, out var bufFlags, out _, out _), "GetBuffer");
                    if ((bufFlags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) != 0 && ++discontinuities % 100 == 1)
                        _log.LogWarning("Audio loopback discontinuity ({Count} so far)", discontinuities);
                    _ring.Write((bufFlags & AUDCLNT_BUFFERFLAGS_SILENT) != 0 ? null : (float*)data, (int)frames);
                    capture.ReleaseBuffer(frames);
                }
            }
            client.Stop();
        }
        catch (Exception ex)
        {
            Failed = true;
            if (!_started.IsSet)
            {
                _startError = ex;
                _started.Set();
            }
            else
            {
                // Typical when POWERPNT.EXE exits or is killed: the service restarts capture on the new process.
                _log.LogWarning("Audio loopback of pid {Pid} stopped: {Message}", _pid, ex.Message);
            }
        }
        finally
        {
            if (mmcss != IntPtr.Zero) AvRevertMmThreadCharacteristics(mmcss);
            if (capture is not null) Marshal.ReleaseComObject(capture);
            if (client is not null) Marshal.ReleaseComObject(client);
        }
    }

    private static IAudioClient Activate(uint pid)
    {
        var p = new AudioClientActivationParams
        {
            ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
            TargetProcessId = pid,
            ProcessLoopbackMode = PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE,
        };
        var pv = new PropVariantBlob { vt = VT_BLOB, cbSize = (uint)sizeof(AudioClientActivationParams), pBlobData = (IntPtr)(&p) };
        var handler = new CompletionHandler();
        var iid = IID_IAudioClient;
        ActivateAudioInterfaceAsync(VirtualAudioDeviceProcessLoopback, ref iid, (IntPtr)(&pv), handler, out var op);
        try
        {
            if (!handler.Task.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("ActivateAudioInterfaceAsync did not complete in 5 s");
            return handler.Task.Result;
        }
        finally
        {
            Marshal.ReleaseComObject(op);
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"{what} failed: 0x{hr:X8}", hr);
    }

    public void Dispose()
    {
        _stop = true;
        _packetEvent.Set();
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
        _packetEvent.Dispose();
    }
}
