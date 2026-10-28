using System.Diagnostics;
using System.Runtime.InteropServices;
using KarSlides.Core.Abstractions;
using KarSlides.Core.Model;
using Microsoft.Extensions.Logging;

namespace KarSlides.Core.Preview;

/// <summary>Turns a small BGRA image into JPEG (GDI+/WIC in the app, a fake in tests).</summary>
public interface IPreviewEncoder
{
    byte[] Encode(byte[] bgra, int width, int height, int quality);
}

/// <summary>One encoded preview picture; <see cref="Sequence"/> grows with every new picture.</summary>
public sealed record PreviewImage(long Sequence, byte[] Jpeg, int Width, int Height);

public sealed class LivePreviewOptions
{
    public double Fps { get; init; } = 25;
    public int Width { get; init; } = 960;
    public int Quality { get; init; } = 75;
}

/// <summary>
/// Live picture of what goes on air, for the web UI. Decorates the slot output: every frame and mode
/// change goes to the inner output unchanged. The newest Live frame is kept in a triple buffer (one copy on the
/// capture thread, only when the picture changes); a separate thread scales and encodes it to JPEG at most
/// <see cref="LivePreviewOptions.Fps"/> times a second and only while someone watches. Black gives a black picture,
/// Freeze keeps the frozen one — the preview shows what vMix gets. Never blocks the output path.
/// </summary>
public sealed class LivePreview : IOutputSink
{
    private readonly IOutputSink _inner;
    private readonly IPreviewEncoder _encoder;
    private readonly LivePreviewOptions _options;
    private readonly ILogger _log;
    private readonly object _swap = new();
    private readonly byte[][] _raw = new byte[3][];
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private int _write = 0, _ready = 1, _read = 2; // triple buffer indices, swapped under _swap
    private bool _dirty;
    private int _rawWidth, _rawHeight, _rawStride;
    private volatile OutputMode _mode = OutputMode.Freeze;
    private volatile bool _blackPending;
    private int _refresh; // 1: re-publish the current picture (mode change, first watcher)
    private volatile bool _stop;
    private int _viewers;
    private volatile PreviewImage? _latest;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _sequence;
    private long _encoded, _encodeTicks;

    public LivePreview(IOutputSink inner, IPreviewEncoder encoder, LivePreviewOptions options, ILogger log, string name = "slot")
    {
        _inner = inner;
        _encoder = encoder;
        _options = options;
        _log = log;
        _mode = inner.Mode;
        _thread = new Thread(Loop) { IsBackground = true, Name = $"LivePreview {name}", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public OutputMode Mode
    {
        get => _inner.Mode;
        set
        {
            _inner.Mode = value;
            _mode = value;
            if (value == OutputMode.Black) _blackPending = true;
            else Volatile.Write(ref _refresh, 1); // back from Black: the current picture even if the capture sends nothing new
            _wake.Set();
        }
    }

    public int Viewers => Volatile.Read(ref _viewers);

    /// <summary>Newest picture, or null before the first one.</summary>
    public PreviewImage? Latest => _latest;

    public void PushVideo(in VideoFrame frame)
    {
        _inner.PushVideo(frame);
        if (_mode != OutputMode.Live || _stop) return;

        var rowBytes = frame.Width * 4;
        var size = rowBytes * frame.Height;
        var buffer = _raw[_write];
        if (buffer is null || buffer.Length != size) _raw[_write] = buffer = new byte[size];
        for (var y = 0; y < frame.Height; y++)
            Marshal.Copy(frame.Data + (nint)y * frame.Stride, buffer, y * rowBytes, rowBytes);

        lock (_swap)
        {
            (_write, _ready) = (_ready, _write);
            (_rawWidth, _rawHeight, _rawStride) = (frame.Width, frame.Height, rowBytes);
            _dirty = true;
        }
        if (Volatile.Read(ref _viewers) > 0) _wake.Set();
    }

    /// <summary>Counts a watcher while the returned handle is not disposed; pictures are produced only with watchers.</summary>
    public IDisposable Watch()
    {
        if (Interlocked.Increment(ref _viewers) == 1)
        {
            Volatile.Write(ref _refresh, 1); // the stored picture may be from another mode
            _wake.Set();
        }
        return new Watcher(this);
    }

    /// <summary>The first picture newer than <paramref name="after"/>; null when <paramref name="timeout"/> passes first.</summary>
    public async Task<PreviewImage?> WaitNewerAsync(long after, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            Task changed;
            lock (_swap) changed = _changed.Task;
            if (_latest is { } img && img.Sequence > after) return img;
            var left = deadline - Stopwatch.GetTimestamp();
            if (left <= 0) return null;
            await Task.WhenAny(changed, Task.Delay(TimeSpan.FromSeconds((double)left / Stopwatch.Frequency), ct));
            ct.ThrowIfCancellationRequested();
        }
    }

    private void Loop()
    {
        var period = TimeSpan.FromSeconds(1 / Math.Max(1, _options.Fps));
        var last = Stopwatch.GetTimestamp() - (long)(period.TotalSeconds * Stopwatch.Frequency);
        byte[]? small = null;
        var report = Stopwatch.GetTimestamp();
        try
        {
            while (!_stop)
            {
                _wake.WaitOne(TimeSpan.FromSeconds(1));
                if (_stop) break;
                if (Stopwatch.GetTimestamp() - report > 60 * Stopwatch.Frequency) report = Report();
                if (Volatile.Read(ref _viewers) == 0) continue;

                // Pace: at most Fps pictures a second; the newest frame wins, intermediate ones are skipped.
                var wait = last + (long)(period.TotalSeconds * Stopwatch.Frequency) - Stopwatch.GetTimestamp();
                if (wait > 0) Thread.Sleep(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency)); // preview thread only, never the output path

                int w, h, stride;
                byte[]? src = null;
                var refresh = Interlocked.Exchange(ref _refresh, 0) == 1;
                var isBlack = _mode == OutputMode.Black;
                var black = isBlack && (_blackPending || refresh);
                lock (_swap)
                {
                    // Frames arrive only in Live; in Freeze the ready buffer still holds the last frame before it.
                    if (!isBlack && _dirty)
                    {
                        (_read, _ready) = (_ready, _read);
                        _dirty = false;
                        src = _raw[_read];
                    }
                    else if (!isBlack && refresh)
                    {
                        src = _raw[_read]; // the newest frame was already taken: encode it again
                    }
                    (w, h, stride) = (_rawWidth, _rawHeight, _rawStride);
                }
                if (black) _blackPending = false;
                if (!black && src is null) continue;

                var dw = Math.Min(_options.Width, w > 0 ? w : _options.Width);
                var dh = w > 0 ? Math.Max(1, (int)Math.Round((double)dw * h / w)) : dw * 9 / 16;
                if (small is null || small.Length != dw * dh * 4) small = new byte[dw * dh * 4];
                if (black) PreviewScaler.FillBlack(small);
                else PreviewScaler.Downscale(src!, w, h, stride, small, dw, dh);

                var started = Stopwatch.GetTimestamp();
                var jpeg = _encoder.Encode(small, dw, dh, _options.Quality);
                _encodeTicks += Stopwatch.GetTimestamp() - started;
                _encoded++;
                last = Stopwatch.GetTimestamp();
                Publish(new PreviewImage(Interlocked.Increment(ref _sequence), jpeg, dw, dh));
                if (_dirty) _wake.Set(); // a newer frame arrived meanwhile
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Live preview loop crashed, the web UI falls back to thumbnails");
        }
    }

    private long Report()
    {
        if (_encoded > 0)
            _log.LogInformation("Live preview 60s: {Count} pictures, encode avg {Ms:F1} ms, viewers {Viewers}",
                _encoded, _encodeTicks * 1000.0 / Stopwatch.Frequency / _encoded, Viewers);
        _encoded = _encodeTicks = 0;
        return Stopwatch.GetTimestamp();
    }

    private void Publish(PreviewImage image)
    {
        TaskCompletionSource done;
        lock (_swap)
        {
            _latest = image;
            done = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        done.TrySetResult();
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
        _wake.Dispose(); // the inner output belongs to the caller
    }

    private sealed class Watcher(LivePreview owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref owner._viewers);
        }
    }
}

/// <summary>Live previews by slot number, for the API (empty when <c>Preview.Live</c> is off).</summary>
public sealed class PreviewSet
{
    private readonly Dictionary<int, LivePreview> _bySlot = [];

    public void Add(int slot, LivePreview preview)
    {
        lock (_bySlot) _bySlot[slot] = preview;
    }

    public LivePreview? Find(int slot)
    {
        lock (_bySlot) return _bySlot.GetValueOrDefault(slot);
    }
}

/// <summary>BGRA scaling for the preview: area average, good enough for 2x-4x reduction.</summary>
public static class PreviewScaler
{
    public static void Downscale(byte[] src, int sw, int sh, int sStride, byte[] dst, int dw, int dh)
    {
        // Integer source ranges per destination pixel (box filter); for 1920 -> 960 this is an exact 2x2 average.
        for (var y = 0; y < dh; y++)
        {
            var y0 = (int)((long)y * sh / dh);
            var y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * sh / dh));
            var drow = y * dw * 4;
            for (var x = 0; x < dw; x++)
            {
                var x0 = (int)((long)x * sw / dw);
                var x1 = Math.Max(x0 + 1, (int)((long)(x + 1) * sw / dw));
                int b = 0, g = 0, r = 0, n = 0;
                for (var yy = y0; yy < y1; yy++)
                {
                    var p = yy * sStride + x0 * 4;
                    for (var xx = x0; xx < x1; xx++, p += 4)
                    {
                        b += src[p];
                        g += src[p + 1];
                        r += src[p + 2];
                        n++;
                    }
                }
                var d = drow + x * 4;
                dst[d] = (byte)(b / n);
                dst[d + 1] = (byte)(g / n);
                dst[d + 2] = (byte)(r / n);
                dst[d + 3] = 255;
            }
        }
    }

    public static void FillBlack(byte[] dst)
    {
        for (var i = 0; i < dst.Length; i += 4)
        {
            dst[i] = dst[i + 1] = dst[i + 2] = 0;
            dst[i + 3] = 255;
        }
    }
}
