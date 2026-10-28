namespace KarSlides.Core.Audio;

/// <summary>
/// Interleaved float ring between the loopback capture (writer, packets of ~10 ms) and the paced output
/// (reader, one video frame of samples per tick). The reader always gets exactly the requested number of
/// samples: missing samples are silence. After running dry it waits until <see cref="TargetLevel"/> frames are
/// buffered (pre-roll). Latency is kept low by trimming: the minimum backlog over ~1 s is the real excess
/// (independent of packet jitter); if it exceeds <see cref="SafetyFrames"/> by more than 5 ms, the oldest
/// samples are dropped. Thread-safe (one lock, tiny critical sections).
/// </summary>
public sealed class AudioRing
{
    private const int TrimWindowReads = 50;

    private readonly object _sync = new();
    private readonly float[] _data;
    private readonly int _capacity; // in frames (samples per channel)
    private int _read, _count;
    private bool _preroll = true;
    private int _minBacklog = int.MaxValue;
    private int _readsInWindow;

    public int SampleRate { get; }
    public int Channels { get; }
    public int TargetLevel { get; }

    /// <summary>Backlog that must stay after a read: absorbs 10 ms packets against 20 ms reads.</summary>
    public int SafetyFrames { get; }

    public long FramesWritten { get; private set; }
    public long FramesRead { get; private set; }
    public long UnderrunFrames { get; private set; }
    public long DroppedFrames { get; private set; }
    public long Prerolls { get; private set; }
    public long Trims { get; private set; }

    public AudioRing(int sampleRate, int channels, double targetMs = 20, double safetyMs = 5)
    {
        SampleRate = sampleRate;
        Channels = channels;
        TargetLevel = (int)(sampleRate * targetMs / 1000);
        SafetyFrames = (int)(sampleRate * safetyMs / 1000);
        _capacity = sampleRate; // 1 s
        _data = new float[_capacity * channels];
    }

    public int Level
    {
        get { lock (_sync) return _count; }
    }

    /// <summary>Writes interleaved frames; null means silence (WASAPI "silent" packet).</summary>
    public unsafe void Write(float* interleaved, int frames)
    {
        lock (_sync)
        {
            for (var i = 0; i < frames; i++)
            {
                if (_count == _capacity)
                {
                    _read = (_read + 1) % _capacity;
                    _count--;
                    DroppedFrames++;
                }
                var w = (_read + _count) % _capacity;
                for (var c = 0; c < Channels; c++)
                    _data[w * Channels + c] = interleaved == null ? 0f : interleaved[i * Channels + c];
                _count++;
            }
            FramesWritten += frames;
        }
    }

    public unsafe void Write(ReadOnlySpan<float> interleaved)
    {
        fixed (float* p = interleaved) Write(p, interleaved.Length / Channels);
    }

    /// <summary>Clears the buffer (source changed, e.g. PowerPoint restarted).</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _read = _count = 0;
            _preroll = true;
            _minBacklog = int.MaxValue;
            _readsInWindow = 0;
        }
    }

    /// <summary>Reads exactly <paramref name="frames"/> frames into planar layout (channel after channel).</summary>
    public void ReadPlanar(Span<float> planar, int frames)
    {
        lock (_sync)
        {
            if (_preroll && _count >= TargetLevel)
            {
                _preroll = false;
                Prerolls++;
            }
            var available = _preroll ? 0 : Math.Min(frames, _count);
            for (var i = 0; i < available; i++)
            {
                var r = (_read + i) % _capacity;
                for (var c = 0; c < Channels; c++)
                    planar[c * frames + i] = _data[r * Channels + c];
            }
            for (var i = available; i < frames; i++)
            for (var c = 0; c < Channels; c++)
                planar[c * frames + i] = 0f;

            _read = (_read + available) % _capacity;
            _count -= available;
            FramesRead += frames;
            if (!_preroll && available < frames)
            {
                UnderrunFrames += frames - available;
                _preroll = true; // ran dry: wait for TargetLevel again
            }
            TrimBacklog();
        }
    }

    private void TrimBacklog()
    {
        if (_preroll) return;
        _minBacklog = Math.Min(_minBacklog, _count);
        if (++_readsInWindow < TrimWindowReads) return;

        var excess = _minBacklog - SafetyFrames;
        if (excess > SampleRate / 200) // > 5 ms
        {
            _read = (_read + excess) % _capacity;
            _count -= excess;
            DroppedFrames += excess;
            Trims++;
        }
        _minBacklog = int.MaxValue;
        _readsInWindow = 0;
    }
}
