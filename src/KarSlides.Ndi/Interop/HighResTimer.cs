using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KarSlides.Ndi.Interop;

/// <summary>
/// High-resolution waitable timer (Windows 10 1803+): sub-millisecond waits without timeBeginPeriod
/// and without Thread.Sleep. Used to pace the NDI output on an absolute schedule.
/// </summary>
internal sealed class HighResTimer : IDisposable
{
    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    private const uint TIMER_ALL_ACCESS = 0x1F0003;
    private const uint INFINITE = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint ms);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private IntPtr _handle;

    public bool IsHighResolution { get; }

    public HighResTimer()
    {
        _handle = CreateWaitableTimerExW(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        IsHighResolution = _handle != IntPtr.Zero;
        if (!IsHighResolution)
        {
            // Windows 10 before 1803: regular resolution (~1 ms with the default timer), see IsHighResolution.
            _handle = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TIMER_ALL_ACCESS);
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException($"CreateWaitableTimerExW failed, error {Marshal.GetLastWin32Error()}");
        }
    }

    /// <summary>Blocks until Stopwatch.GetTimestamp() reaches <paramref name="deadlineTicks"/>.</summary>
    public void WaitUntil(long deadlineTicks)
    {
        var remaining = deadlineTicks - Stopwatch.GetTimestamp();
        if (remaining <= 0) return;
        long due = -(long)(remaining * 10_000_000.0 / Stopwatch.Frequency); // relative, 100 ns units
        if (due == 0) return;
        if (!SetWaitableTimer(_handle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            throw new InvalidOperationException($"SetWaitableTimer failed, error {Marshal.GetLastWin32Error()}");
        WaitForSingleObject(_handle, INFINITE);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) CloseHandle(_handle);
        _handle = IntPtr.Zero;
    }
}
