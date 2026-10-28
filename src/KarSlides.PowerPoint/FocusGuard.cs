using KarSlides.PowerPoint.Interop;
using Microsoft.Extensions.Logging;

namespace KarSlides.PowerPoint;

[Flags]
public enum FocusMode
{
    None = 0,

    /// <summary>WS_EX_NOACTIVATE on the show window.</summary>
    NoActivate = 1,

    /// <summary>Give the foreground back as soon as POWERPNT.EXE takes it.</summary>
    Restore = 2,

    Both = NoActivate | Restore,
}

/// <summary>
/// Keeps the operator's window (vMix) in the foreground. A WinEvent hook on EVENT_SYSTEM_FOREGROUND,
/// on its own thread with a message loop, logs every activation of POWERPNT.EXE and, in Restore mode, returns
/// the foreground to the last non-PowerPoint window via AttachThreadInput + SetForegroundWindow.
/// PowerPoint windows are never activated.
/// </summary>
public sealed class FocusGuard : IDisposable
{
    private readonly FocusMode _mode;
    private readonly ILogger _log;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private Win32.WinEventProc? _proc; // keep the delegate alive
    private uint _threadId;
    private IntPtr _lastGood;

    public FocusGuard(FocusMode mode, ILogger<FocusGuard> log)
    {
        _mode = mode;
        _log = log;
        var fg = Win32.GetForegroundWindow();
        _lastGood = IsPowerPoint(fg) ? IntPtr.Zero : fg;
        _thread = new Thread(Loop) { IsBackground = true, Name = "FocusGuard" };
        _thread.Start();
        _ready.Wait();
        _log.LogInformation("Focus guard started, mode {Mode}", mode);
    }

    public static FocusMode Parse(string value) => value.Trim().ToLowerInvariant() switch
    {
        "none" => FocusMode.None,
        "noactivate" => FocusMode.NoActivate,
        "restore" => FocusMode.Restore,
        "both" => FocusMode.Both,
        _ => throw new FormatException($"Unknown focus mode '{value}' (none, noactivate, restore, both)"),
    };

    private void Loop()
    {
        _threadId = Win32.GetCurrentThreadId();
        _proc = OnForeground;
        var hook = Win32.SetWinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _proc, 0, 0, Win32.WINEVENT_OUTOFCONTEXT);
        if (hook == IntPtr.Zero) _log.LogError("SetWinEventHook failed: focus theft will not be detected");
        _ready.Set();

        while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessage(ref msg);
        }
        if (hook != IntPtr.Zero) Win32.UnhookWinEvent(hook);
    }

    private void OnForeground(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (!IsPowerPoint(hwnd))
        {
            if (hwnd != IntPtr.Zero) _lastGood = hwnd;
            return;
        }

        if (!_mode.HasFlag(FocusMode.Restore))
        {
            _log.LogWarning("PowerPoint took the foreground: {Window}", Win32.Describe(hwnd));
            return;
        }
        if (_lastGood == IntPtr.Zero)
        {
            _log.LogWarning("PowerPoint took the foreground, no previous window to return it to");
            return;
        }

        var fgThread = Win32.GetWindowThreadProcessId(hwnd, out _);
        var attached = fgThread != 0 && fgThread != _threadId && Win32.AttachThreadInput(_threadId, fgThread, true);
        var ok = Win32.SetForegroundWindow(_lastGood);
        if (attached) Win32.AttachThreadInput(_threadId, fgThread, false);

        if (ok && Win32.GetForegroundWindow() == _lastGood)
            _log.LogInformation("PowerPoint activation intercepted, foreground returned to {Window}", Win32.Describe(_lastGood));
        else
            _log.LogError("PowerPoint took the foreground and it could not be returned to {Window}", Win32.Describe(_lastGood));
    }

    /// <summary>Called right after a show window appears.</summary>
    public void OnShowWindow(IntPtr hwnd)
    {
        if (!_mode.HasFlag(FocusMode.NoActivate) || hwnd == IntPtr.Zero) return;
        var style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64() | Win32.WS_EX_NOACTIVATE;
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, new IntPtr(style));
        Win32.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED);
    }

    private static bool IsPowerPoint(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && Win32.ProcessNameOf(hwnd).Equals("POWERPNT", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        Win32.PostThreadMessage(_threadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}
