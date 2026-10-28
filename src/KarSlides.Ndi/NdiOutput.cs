using System.Diagnostics;
using System.Runtime.InteropServices;
using KarSlides.Core.Abstractions;
using KarSlides.Core.Audio;
using KarSlides.Core.Configuration;
using KarSlides.Core.Model;
using KarSlides.Ndi.Interop;
using KarSlides.Ndi.Output;
using Microsoft.Extensions.Logging;

namespace KarSlides.Ndi;

/// <summary>
/// NDI output with a constant frame rate: own high-resolution timer on an absolute schedule,
/// async send, the newest frame or a repeat of the last one on every tick. Black only in
/// <see cref="OutputMode.Black"/>; before the first real frame nothing is sent.
/// </summary>
public sealed unsafe class NdiOutput : IOutputSink
{
    private readonly ILogger _log;
    private readonly NdiSender _sender;
    private readonly FrameExchange _frames;
    private readonly FrameSelector _selector;
    private readonly IntPtr _black;
    private readonly FrameRate _rate;
    private readonly bool _async;
    private readonly Thread _thread;
    private volatile bool _stop;
    private volatile OutputMode _mode = OutputMode.Freeze;
    private volatile AudioRing? _audio;
    private float[] _audioPlanar = [];
    private long _sizeMismatch;

    public NdiOutput(string name, int width, int height, FrameRate rate, bool asyncSend, ILogger<NdiOutput> log)
    {
        _log = log;
        _rate = rate;
        _async = asyncSend;
        _sender = new NdiSender(name, log);
        _frames = new FrameExchange(width, height);
        _selector = new FrameSelector(_frames);

        var size = (nuint)(width * height * 4);
        _black = (IntPtr)NativeMemory.AlignedAlloc(size, 64);
        var px = (uint*)_black;
        for (var i = 0; i < width * height; i++) px[i] = 0xFF000000; // opaque black

        _thread = new Thread(Loop) { IsBackground = true, Name = $"NdiOut {name}", Priority = ThreadPriority.Highest };
        _thread.Start();
        _log.LogInformation("NDI output '{Name}' {Width}x{Height} @ {Fps}, send={Send}",
            name, width, height, rate, asyncSend ? "async" : "sync");
    }

    public string Name => _sender.Name;

    public OutputMode Mode
    {
        get => _mode;
        set => _mode = value;
    }

    /// <summary>
    /// Audio for this output: sent by the output loop right after every video tick, exactly one
    /// frame worth of samples with the same timecode, so audio and video share one clock and audio is
    /// continuous (silence when the ring is empty). Null = no audio (another slot owns it).
    /// </summary>
    public void AttachAudio(AudioRing? ring)
    {
        if (ring is not null)
            _audioPlanar = new float[(ring.SampleRate * _rate.Denominator / _rate.Numerator + 2) * ring.Channels];
        _audio = ring;
        _log.LogInformation("NDI '{Name}': audio {State}", Name, ring is null ? "detached" : $"attached ({ring.SampleRate} Hz x{ring.Channels})");
    }

    public void PushVideo(in VideoFrame frame)
    {
        if (frame.Width != _frames.Width || frame.Height != _frames.Height)
        {
            // Fixed 1920x1080 without scaling: the start-up check should have caught this.
            if (Interlocked.Increment(ref _sizeMismatch) % 500 == 1)
                _log.LogError("Frame {W}x{H} does not match output {OW}x{OH}, dropped ({Count} so far)",
                    frame.Width, frame.Height, _frames.Width, _frames.Height, _sizeMismatch);
            return;
        }

        var dst = (byte*)_frames.BackBuffer;
        var src = (byte*)frame.Data;
        var rowBytes = _frames.Stride;
        if (frame.Stride == rowBytes)
        {
            Buffer.MemoryCopy(src, dst, (long)rowBytes * frame.Height, (long)rowBytes * frame.Height);
        }
        else
        {
            for (var y = 0; y < frame.Height; y++)
                Buffer.MemoryCopy(src + (long)y * frame.Stride, dst + (long)y * rowBytes, rowBytes, rowBytes);
        }
        _frames.Publish(frame.Timestamp);
    }

    private void Loop()
    {
        try
        {
            RunLoop();
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "NDI output loop crashed");
        }
        finally
        {
            // NDI may still read the last async buffer: release it before the buffers are freed.
            if (_async) _sender.FlushAsync();
        }
    }

    private void RunLoop()
    {
        using var timer = new HighResTimer();
        var periodTicks = (double)Stopwatch.Frequency * _rate.Denominator / _rate.Numerator;
        var lateMs = _rate.PeriodMs * 1.5;
        var scheduleStart = Stopwatch.GetTimestamp();
        long slot = 0, frameIndex = 0, prevSend = 0;
        long sent = 0, fresh = 0, repeat = 0, black = 0, late = 0, skipped = 0;
        double maxInterval = 0, maxCall = 0;
        var lastReport = scheduleStart;
        _log.LogInformation("NDI output loop started, high-resolution timer: {HighRes}", timer.IsHighResolution);

        while (!_stop)
        {
            var deadline = scheduleStart + (long)(slot * periodTicks);
            var behind = Stopwatch.GetTimestamp() - deadline;
            if (behind > 2 * periodTicks)
            {
                var n = (long)(behind / periodTicks);
                skipped += n;
                frameIndex += n;
                _log.LogWarning("NDI output {Ms:F1} ms behind schedule, skipping {Slots} slot(s)",
                    behind * 1000.0 / Stopwatch.Frequency, n);
                scheduleStart = Stopwatch.GetTimestamp();
                slot = 0;
            }
            else
            {
                timer.WaitUntil(deadline);
            }

            var kind = _selector.Next(_mode);
            var now = Stopwatch.GetTimestamp();
            if (kind != TickKind.None)
            {
                var buffer = kind == TickKind.Black ? _black : _frames.FrontBuffer;
                var timecode = _rate.Timecode(frameIndex);
                _sender.SendVideo(buffer, _frames.Width, _frames.Height, _frames.Stride,
                    _rate.Numerator, _rate.Denominator, timecode, _async);
                if (_audio is { } ring) SendAudio(ring, frameIndex, timecode);
                var after = Stopwatch.GetTimestamp();

                sent++;
                if (kind == TickKind.Fresh) fresh++;
                else if (kind == TickKind.Repeat) repeat++;
                else black++;
                maxCall = Math.Max(maxCall, (after - now) * 1000.0 / Stopwatch.Frequency);
                if (prevSend != 0)
                {
                    var interval = (now - prevSend) * 1000.0 / Stopwatch.Frequency;
                    maxInterval = Math.Max(maxInterval, interval);
                    if (interval > lateMs) late++;
                }
                prevSend = now;
            }
            else
            {
                prevSend = 0; // gaps while nothing is on air are not "late"
            }

            frameIndex++;
            slot++;

            if (now - lastReport >= 60 * Stopwatch.Frequency)
            {
                _log.LogInformation(
                    "NDI '{Name}' 60s: sent={Sent} fresh={Fresh} repeat={Repeat} black={Black} late={Late} skipped={Skipped} " +
                    "maxInterval={MaxInterval:F1}ms maxCall={MaxCall:F2}ms mode={Mode} connections={Connections}",
                    Name, sent, fresh, repeat, black, late, skipped, maxInterval, maxCall, _mode, _sender.Connections);
                if (_audio is { } a)
                    _log.LogInformation(
                        "NDI '{Name}' audio: level={Level} samples, underrun total={Underrun}, trimmed total={Dropped} ({Trims} trims), pre-rolls={Prerolls}",
                        Name, a.Level, a.UnderrunFrames, a.DroppedFrames, a.Trims, a.Prerolls);
                sent = fresh = repeat = black = 0;
                maxInterval = maxCall = 0;
                lastReport = now;
            }
        }
    }

    private void SendAudio(AudioRing ring, long frameIndex, long timecode)
    {
        var samples = _rate.SamplesInFrame(frameIndex, ring.SampleRate);
        var planar = _audioPlanar.AsSpan(0, samples * ring.Channels);
        ring.ReadPlanar(planar, samples);
        fixed (float* p = planar)
            _sender.SendAudio((IntPtr)p, ring.SampleRate, ring.Channels, samples, samples * sizeof(float), timecode);
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
        _sender.Dispose();
        _frames.Dispose();
        NativeMemory.AlignedFree((void*)_black);
    }
}
