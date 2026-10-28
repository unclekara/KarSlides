using System.Runtime.InteropServices;
using KarSlides.PowerPoint.Interop;

namespace KarSlides.PowerPoint;

/// <summary>
/// Answers "is the slot monitor covered by a PowerPoint slide show window right now?" for the capture gate:
/// frames are published only while it is, so the desktop of the virtual monitor never reaches the output —
/// not after PowerPoint is killed or crashes, not when someone closes the show. A desktop frame
/// always arrives after the window is gone, so the check cannot lose that race. Cheap: the last window found
/// is re-checked with a few user32 calls; the window list is enumerated only when it is gone.
/// Thread-safe (called on the capture callback thread).
/// </summary>
public sealed class ShowWindowProbe(int left, int top, int width, int height)
{
    private IntPtr _hwnd;

    public bool IsCovered()
    {
        var h = _hwnd;
        if (h != IntPtr.Zero && Covers(h)) return true;
        _hwnd = Find();
        return _hwnd != IntPtr.Zero;
    }

    private IntPtr Find()
    {
        var found = IntPtr.Zero;
        Win32.EnumWindowsProc cb = (h, _) =>
        {
            if (Win32.GetClassName(h) == ShowWindows.ShowWindowClass && Covers(h))
            {
                found = h;
                return false;
            }
            return true;
        };
        Win32.EnumWindows(cb, IntPtr.Zero);
        GC.KeepAlive(cb);
        return found;
    }

    private bool Covers(IntPtr h) =>
        IsWindow(h) && Win32.IsWindowVisible(h) && Win32.GetWindowRect(h, out var r) &&
        r.Left <= left && r.Top <= top && r.Right >= left + width && r.Bottom >= top + height;

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);
}
