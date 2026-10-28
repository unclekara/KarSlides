using System.Diagnostics;
using KarSlides.Core.Model;
using KarSlides.PowerPoint.Interop;
using Microsoft.Extensions.Logging;

namespace KarSlides.PowerPoint;

/// <summary>
/// Slide show windows of POWERPNT.EXE (class "screenClass"). SlideShowWindow.HWND throws on PowerPoint 2021
///, so the show window is the one that appeared after Run().
/// </summary>
internal static class ShowWindows
{
    public const string ShowWindowClass = "screenClass";

    public static HashSet<IntPtr> FindAll()
    {
        var pids = Process.GetProcessesByName("POWERPNT").Select(p => (uint)p.Id).ToHashSet();
        var result = new HashSet<IntPtr>();
        Win32.EnumWindowsProc cb = (h, _) =>
        {
            Win32.GetWindowThreadProcessId(h, out var pid);
            if (pids.Contains(pid) && Win32.IsWindowVisible(h) && Win32.GetClassName(h) == ShowWindowClass)
                result.Add(h);
            return true;
        };
        Win32.EnumWindows(cb, IntPtr.Zero);
        GC.KeepAlive(cb);
        return result;
    }

    /// <summary>Waits up to 2 s for a new show window (runs on the STA thread right after Run()).</summary>
    public static IntPtr FindNew(HashSet<IntPtr> before, ILogger log)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 2000)
        {
            var fresh = FindAll().Except(before).ToList();
            if (fresh.Count > 0)
            {
                if (fresh.Count > 1) log.LogWarning("{Count} new show windows after Run(), taking the first", fresh.Count);
                return fresh[0];
            }
            Thread.Sleep(10); // bounded wait for window creation in POWERPNT.EXE; not a capture/output path
        }
        throw new InvalidOperationException("Slide show window (screenClass) did not appear within 2 s after Run()");
    }

    /// <summary>Checks the window covers the monitor exactly and moves it there (without activating) if not.</summary>
    public static void EnsureOnMonitor(IntPtr hwnd, MonitorTarget monitor, ILogger log)
    {
        Win32.GetWindowRect(hwnd, out var r);
        if (Covers(r, monitor))
        {
            log.LogInformation("Show window {Window} is on {Monitor}", Win32.Describe(hwnd), monitor);
            return;
        }

        log.LogWarning("Show window at {Rect} is not on {Monitor}, moving it", r, monitor);
        Win32.SetWindowPos(hwnd, IntPtr.Zero, monitor.Left, monitor.Top, monitor.Width, monitor.Height,
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
        Win32.GetWindowRect(hwnd, out r);
        if (!Covers(r, monitor))
            throw new InvalidOperationException($"Could not place the show window on {monitor}: it is at {r}");
    }

    /// <summary>
    /// Keeps the show window above ordinary windows, so a window dragged onto the show monitor does not cover
    /// the captured picture. Without activation: focus stays where it is. Cheap when already set.
    /// </summary>
    public static void EnsureTopmost(IntPtr hwnd, bool initial, ILogger log)
    {
        if (hwnd == IntPtr.Zero) return;
        if ((Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64() & Win32.WS_EX_TOPMOST) != 0) return;

        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
        var ok = (Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64() & Win32.WS_EX_TOPMOST) != 0;
        if (!ok) log.LogWarning("Show window {Window}: could not make it topmost", Win32.Describe(hwnd));
        else if (initial) log.LogInformation("Show window {Window} is topmost", Win32.Describe(hwnd));
        else log.LogWarning("Show window {Window} had lost topmost, restored", Win32.Describe(hwnd));
    }

    private static bool Covers(Win32.RECT r, MonitorTarget m) =>
        r.Left == m.Left && r.Top == m.Top && r.Right == m.Left + m.Width && r.Bottom == m.Top + m.Height;
}
