using System.Collections.Concurrent;
using System.Diagnostics;
using KarSlides.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace KarSlides.Capture;

/// <summary>
/// Windows.Graphics.Capture of a whole monitor: the capture callback only queues a GPU copy into a
/// ring of 3 staging textures; a dedicated readback thread maps the newest one and raises
/// <see cref="FrameArrived"/> with the mapped pointer (valid during the call, the consumer copies it).
/// WGC delivers frames only when the picture changes; the output repeats the last one.
/// </summary>
public sealed unsafe class MonitorCapture : IFrameSource
{
    private const int RingSize = 3;

    private readonly string _deviceName;
    private readonly bool _debugLayer;
    private readonly ILogger _log;
    private readonly object _contextLock = new();

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;

    private readonly ID3D11Texture2D?[] _staging = new ID3D11Texture2D?[RingSize];
    private readonly long[] _stagingTicks = new long[RingSize];
    private readonly int[] _stagingBusy = new int[RingSize];
    private int _writeSlot;

    private readonly ConcurrentQueue<int> _readbackQueue = new();
    private readonly AutoResetEvent _readbackSignal = new(false);
    private Thread? _readbackThread;
    private volatile bool _stop;

    private long _arrived, _published, _overruns, _coalesced, _sizeMismatch;
    private double _maxReadbackMs;

    public event FrameArrivedHandler? FrameArrived;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public MonitorCapture(string deviceName, bool debugLayer, ILogger<MonitorCapture> log)
    {
        _deviceName = deviceName;
        _debugLayer = debugLayer;
        _log = log;
    }

    /// <summary>
    /// Frames are dropped while this returns false: the slot monitor shows the desktop, not a show.
    /// Null = publish everything.
    /// </summary>
    public Func<bool>? Gate { get; init; }

    private long _gated;
    private bool _gateOpen = true;

    public void Start()
    {
        if (!GraphicsCaptureSession.IsSupported())
            throw new InvalidOperationException("Windows.Graphics.Capture is not supported on this system");

        CreateDevice();
        _winrtDevice = CaptureInterop.CreateWinRtDevice(_device!);
        _item = CaptureInterop.CreateItemForMonitor(DisplayMonitors.FindHandle(_deviceName));
        Width = _item.Size.Width;
        Height = _item.Size.Height;

        for (var i = 0; i < RingSize; i++)
        {
            _staging[i] = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)Width,
                Height = (uint)Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
            });
        }

        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _item.Size);
        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = false;
        if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
        {
            try { _session.IsBorderRequired = false; }
            catch (Exception ex) { _log.LogWarning("IsBorderRequired = false rejected: {Message}", ex.Message); }
        }

        _pool.FrameArrived += OnFrameArrived;
        _readbackThread = new Thread(ReadbackLoop) { IsBackground = true, Name = "CaptureReadback", Priority = ThreadPriority.AboveNormal };
        _readbackThread.Start();
        _session.StartCapture();
        _log.LogInformation("Capture of {Monitor} started: {Width}x{Height}, cursor off", _deviceName, Width, Height);
    }

    private void CreateDevice()
    {
        var flags = DeviceCreationFlags.BgraSupport | (_debugLayer ? DeviceCreationFlags.Debug : 0);
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
        var result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, levels, out _device, out _context);
        if (result.Failure && _debugLayer)
        {
            _log.LogWarning("D3D11 debug layer unavailable ({Result}), creating the device without it", result);
            flags &= ~DeviceCreationFlags.Debug;
            result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, levels, out _device, out _context);
        }
        result.CheckError();

        using var dxgi = _device!.QueryInterface<IDXGIDevice>();
        using var adapter = dxgi.GetAdapter();
        _log.LogInformation("D3D11 device on '{Adapter}', feature level {Level}", adapter.Description.Description, _device.FeatureLevel);
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame is null) return;
        Interlocked.Increment(ref _arrived);
        var arrivedTicks = Stopwatch.GetTimestamp();

        if (frame.ContentSize.Width != Width || frame.ContentSize.Height != Height)
        {
            if (Interlocked.Increment(ref _sizeMismatch) == 1)
                _log.LogError("Monitor {Monitor} changed size to {W}x{H} (capture started at {SW}x{SH}): frames dropped",
                    _deviceName, frame.ContentSize.Width, frame.ContentSize.Height, Width, Height);
            return;
        }

        var open = Gate?.Invoke() ?? true;
        if (open != _gateOpen)
        {
            _gateOpen = open;
            _log.LogInformation("Capture of {Monitor}: {State}", _deviceName,
                open ? "slide show on the monitor, frames published" : "no slide show on the monitor, frames dropped");
        }
        if (!open)
        {
            Interlocked.Increment(ref _gated);
            return;
        }

        var slot = _writeSlot;
        if (Interlocked.CompareExchange(ref _stagingBusy[slot], 1, 0) != 0)
        {
            Interlocked.Increment(ref _overruns); // readback still busy: drop, the next frame will come
            return;
        }

        using (var texture = CaptureInterop.GetTexture(frame.Surface))
        {
            lock (_contextLock)
            {
                _context!.CopyResource(_staging[slot]!, texture);
                _context.Flush();
            }
        }
        _stagingTicks[slot] = arrivedTicks;
        _writeSlot = (slot + 1) % RingSize;
        _readbackQueue.Enqueue(slot);
        _readbackSignal.Set();
    }

    private void ReadbackLoop()
    {
        var lastReport = Stopwatch.GetTimestamp();
        while (!_stop)
        {
            _readbackSignal.WaitOne(1000);

            var newest = -1;
            while (_readbackQueue.TryDequeue(out var s))
            {
                if (newest >= 0)
                {
                    Volatile.Write(ref _stagingBusy[newest], 0);
                    Interlocked.Increment(ref _coalesced);
                }
                newest = s;
            }
            if (newest >= 0)
            {
                try { ReadBack(newest); }
                catch (Exception ex) { _log.LogError(ex, "Capture readback failed"); }
            }

            var now = Stopwatch.GetTimestamp();
            if (now - lastReport >= 60 * Stopwatch.Frequency)
            {
                _log.LogInformation(
                    "Capture {Monitor} 60s: arrived={Arrived} published={Published} gated={Gated} coalesced={Coalesced} overruns={Overruns} maxReadback={Max:F1}ms",
                    _deviceName, Interlocked.Exchange(ref _arrived, 0), Interlocked.Exchange(ref _published, 0),
                    Interlocked.Exchange(ref _gated, 0), Interlocked.Exchange(ref _coalesced, 0), Interlocked.Exchange(ref _overruns, 0),
                    _maxReadbackMs);
                _maxReadbackMs = 0;
                lastReport = now;
            }
        }
    }

    private void ReadBack(int slot)
    {
        var started = Stopwatch.GetTimestamp();
        lock (_contextLock)
        {
            // Blocks this thread until the GPU copy is done; the capture callback is not affected.
            var mapped = _context!.Map(_staging[slot]!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var frame = new VideoFrame(mapped.DataPointer, Width, Height, (int)mapped.RowPitch, _stagingTicks[slot]);
                FrameArrived?.Invoke(in frame);
            }
            finally
            {
                _context.Unmap(_staging[slot]!, 0);
            }
        }
        Volatile.Write(ref _stagingBusy[slot], 0);
        Interlocked.Increment(ref _published);
        _maxReadbackMs = Math.Max(_maxReadbackMs, (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency);
    }

    public void Dispose()
    {
        _stop = true;
        _readbackSignal.Set();
        _readbackThread?.Join(TimeSpan.FromSeconds(2));

        if (_pool is not null) _pool.FrameArrived -= OnFrameArrived;
        _session?.Dispose();
        _pool?.Dispose();

        lock (_contextLock)
        {
            foreach (var t in _staging) t?.Dispose();
            _context?.ClearState();
            _context?.Dispose();
        }
        (_winrtDevice as IDisposable)?.Dispose();
        _device?.Dispose();
        _readbackSignal.Dispose();
        _log.LogInformation("Capture of {Monitor} stopped", _deviceName);
    }
}
