using System.Runtime.InteropServices;

namespace KarSlides.Ndi.Output;

/// <summary>
/// Lock-free exchange of BGRA frames: a producer (test pattern / capture) writes into the back buffer
/// and publishes; the consumer (paced NDI output) takes the newest published frame or keeps its current one.
/// The consumer never waits and never sees a half-written frame.
/// Four buffers instead of the classic three: with NDIlib_send_send_video_async_v2 the frame sent last
/// stays in use by NDI until the *next* send call, so the consumer holds two buffers ("front" being sent
/// now and "spare" = the previous front) and hands back to the producer only the one NDI has released.
/// </summary>
internal sealed unsafe class FrameExchange : IDisposable
{
    private const int FreshBit = 4;
    private const int IndexMask = 3;

    private const int BufferCount = 4;

    private readonly IntPtr[] _buffers = new IntPtr[BufferCount];
    private readonly long[] _sourceTicks = new long[BufferCount];
    private int _state = 1;      // "ready" slot index | FreshBit
    private int _back = 2;       // producer-owned
    private int _front;          // consumer-owned: sent on the next send call
    private int _spare = 3;      // consumer-owned: sent on the previous call, released by NDI at the next call
    private long _published;

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public long PublishedCount => Interlocked.Read(ref _published);

    public FrameExchange(int width, int height)
    {
        Width = width;
        Height = height;
        Stride = width * 4;
        for (var i = 0; i < BufferCount; i++)
        {
            _buffers[i] = (IntPtr)NativeMemory.AlignedAlloc((nuint)(Stride * height), 64);
            NativeMemory.Clear((void*)_buffers[i], (nuint)(Stride * height));
        }
    }

    /// <summary>Producer: buffer to write the next frame into.</summary>
    public IntPtr BackBuffer => _buffers[_back];

    /// <summary>Producer: publishes the back buffer. sourceTicks = Stopwatch timestamp of the content (for latency).</summary>
    public void Publish(long sourceTicks)
    {
        _sourceTicks[_back] = sourceTicks;
        var old = Interlocked.Exchange(ref _state, _back | FreshBit);
        _back = old & IndexMask;
        Interlocked.Increment(ref _published);
    }

    /// <summary>
    /// Consumer: switches to the newest frame if there is one. Returns true if the front frame changed.
    /// Must be called before the send call: the buffer given back to the producer is the one sent two calls
    /// ago, which NDI released when the previous send call started.
    /// </summary>
    public bool TryTakeNewest()
    {
        if ((Volatile.Read(ref _state) & FreshBit) == 0) return false;
        var old = Interlocked.Exchange(ref _state, _spare);
        _spare = _front;
        _front = old & IndexMask;
        return true;
    }

    public IntPtr FrontBuffer => _buffers[_front];
    public long FrontSourceTicks => _sourceTicks[_front];

    public void Dispose()
    {
        for (var i = 0; i < BufferCount; i++)
        {
            if (_buffers[i] != IntPtr.Zero) NativeMemory.AlignedFree((void*)_buffers[i]);
            _buffers[i] = IntPtr.Zero;
        }
    }
}
