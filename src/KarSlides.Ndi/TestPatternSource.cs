using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using KarSlides.Core.Abstractions;
using KarSlides.Ndi.Interop;
using Microsoft.Extensions.Logging;

namespace KarSlides.Ndi;

/// <summary>
/// Test pattern frame source: colour bars, a big frame counter, a moving line and integrity
/// markers. Checks the NDI -> vMix chain without PowerPoint.
/// </summary>
public sealed unsafe class TestPatternSource : IFrameSource
{
    private static readonly uint[] Bars =
    [
        0xFFC0C0C0, 0xFFC0C000, 0xFF00C0C0, 0xFF00C000, 0xFFC000C0, 0xFFC00000, 0xFF0000C0, 0xFF101010,
    ];

    // 5x7 digits, one row per byte (5 low bits, MSB = leftmost)
    private static readonly byte[][] Digits =
    [
        [0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E], [0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E],
        [0x0E, 0x11, 0x01, 0x02, 0x04, 0x08, 0x1F], [0x1F, 0x02, 0x04, 0x02, 0x01, 0x11, 0x0E],
        [0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02], [0x1F, 0x10, 0x1E, 0x01, 0x01, 0x11, 0x0E],
        [0x06, 0x08, 0x10, 0x1E, 0x11, 0x11, 0x0E], [0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08],
        [0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E], [0x0E, 0x11, 0x11, 0x0F, 0x01, 0x02, 0x0C],
    ];

    public const int MarkerSize = 32;

    private readonly int _width, _height;
    private readonly double _fps;
    private readonly uint[] _base;
    private readonly IntPtr _buffer;
    private readonly Thread _thread;
    private readonly ILogger _log;
    private volatile bool _stop;

    public event FrameArrivedHandler? FrameArrived;

    public TestPatternSource(int width, int height, double fps, ILogger<TestPatternSource> log)
    {
        _width = width;
        _height = height;
        _fps = fps;
        _log = log;
        _base = new uint[width * height];
        _buffer = (IntPtr)NativeMemory.AlignedAlloc((nuint)(width * height * 4), 64);
        RenderBars();
        _thread = new Thread(Loop) { IsBackground = true, Name = "TestPattern" };
    }

    public void Start()
    {
        _log.LogInformation("Test pattern {Width}x{Height} at {Fps} fps", _width, _height, _fps);
        _thread.Start();
    }

    private void RenderBars()
    {
        int w = _width, h = _height;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            _base[y * w + x] = y < h * 2 / 3 ? Bars[x * Bars.Length / w] : (uint)(0xFF000000 | (uint)(x * 255 / w) * 0x010101);
    }

    private void Loop()
    {
        using var timer = new HighResTimer();
        var period = Stopwatch.Frequency / _fps;
        var start = Stopwatch.GetTimestamp();
        long n = 0;
        while (!_stop)
        {
            timer.WaitUntil(start + (long)(n * period));
            Render(n);
            var frame = new VideoFrame(_buffer, _width, _height, _width * 4, Stopwatch.GetTimestamp());
            FrameArrived?.Invoke(in frame);
            n++;
        }
    }

    private void Render(long n)
    {
        int w = _width, h = _height;
        var dst = (uint*)_buffer;
        fixed (uint* src = _base)
            Buffer.MemoryCopy(src, dst, (long)w * h * 4, (long)w * h * 4);

        // Moving line: 8 px per frame, 6 px wide.
        var lx = (int)(n * 8 % w);
        for (var y = 0; y < h; y++)
        for (var x = lx; x < Math.Min(lx + 6, w); x++)
            dst[y * w + x] = 0xFFFFFFFF;

        // Frame counter, 7 digits, 12 px per font pixel, on black plate.
        const int scale = 12, digits = 7;
        int cw = 6 * scale, ch = 7 * scale;
        int ox = (w - digits * cw) / 2, oy = h / 3 - ch / 2;
        FillRect(dst, w, ox - scale, oy - scale, digits * cw + scale, ch + 2 * scale, 0xFF000000);
        var s = (n % 10_000_000).ToString("D7");
        for (var i = 0; i < digits; i++)
            DrawDigit(dst, w, ox + i * cw, oy, s[i] - '0', scale);

        // Integrity markers: frame number in two solid blocks, top-left and bottom-right. Written last, so a
        // receiver seeing different numbers knows the frame was torn (checked by "Spike ndi-recv --pattern-check").
        var marker = EncodeMarker(n);
        FillRect(dst, w, 0, 0, MarkerSize, MarkerSize, marker);
        FillRect(dst, w, w - MarkerSize, h - MarkerSize, MarkerSize, MarkerSize, marker);
    }

    /// <summary>n mod 4096 as three 4-bit nibbles in B, G, R (x16 + 8: robust to lossy compression).</summary>
    public static uint EncodeMarker(long n)
    {
        var v = (int)(n % 4096);
        uint b = (uint)((v & 15) * 16 + 8), g = (uint)(((v >> 4) & 15) * 16 + 8), r = (uint)(((v >> 8) & 15) * 16 + 8);
        return 0xFF000000 | (r << 16) | (g << 8) | b;
    }

    public static int DecodeMarker(uint px) =>
        ((int)(px & 0xFF) >> 4) | (((int)((px >> 8) & 0xFF) >> 4) << 4) | (((int)((px >> 16) & 0xFF) >> 4) << 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FillRect(uint* dst, int stride, int x0, int y0, int rw, int rh, uint color)
    {
        for (var y = y0; y < y0 + rh; y++)
        for (var x = x0; x < x0 + rw; x++)
            dst[y * stride + x] = color;
    }

    private static void DrawDigit(uint* dst, int stride, int x0, int y0, int d, int scale)
    {
        var glyph = Digits[d];
        for (var row = 0; row < 7; row++)
        for (var col = 0; col < 5; col++)
            if ((glyph[row] & (0x10 >> col)) != 0)
                FillRect(dst, stride, x0 + col * scale, y0 + row * scale, scale, scale, 0xFFFFFFFF);
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
        NativeMemory.AlignedFree((void*)_buffer);
    }
}
