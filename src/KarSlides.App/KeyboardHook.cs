using System.Runtime.InteropServices;
using KarSlides.Core.Clicker;
using Microsoft.Extensions.Logging;

namespace KarSlides.App;

/// <summary>
/// Global low-level keyboard hook (WH_KEYBOARD_LL) on its own thread with a message loop, so it works
/// while vMix has the focus. The callback only asks <see cref="ClickerController"/> (which queues the command)
/// and returns at once: Windows drops hooks that take longer than ~300 ms.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const uint WM_QUIT = 0x0012;
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    private readonly ClickerController _clicker;
    private readonly ILogger _log;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private HookProc? _proc; // keep alive
    private IntPtr _hook;
    private uint _threadId;

    public KeyboardHook(ClickerController clicker, ILogger<KeyboardHook> log)
    {
        _clicker = clicker;
        _log = log;
        _thread = new Thread(Loop) { IsBackground = true, Name = "ClickerHook", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _ready.Wait();
    }

    private void Loop()
    {
        _threadId = GetCurrentThreadId();
        _proc = OnKey;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) _log.LogError("Keyboard hook failed (error {Error}): clicker disabled", Marshal.GetLastWin32Error());
        else _log.LogInformation("Clicker keyboard hook installed");
        _ready.Set();

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
    }

    private IntPtr OnKey(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = (int)wParam;
            var down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            if (down || msg is WM_KEYUP or WM_SYSKEYUP)
            {
                var vk = Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode
                if (vk is not (VK_SHIFT or VK_CONTROL or VK_MENU or VK_LWIN or VK_RWIN))
                {
                    var modifiers = Pressed(VK_CONTROL) || Pressed(VK_MENU) || Pressed(VK_LWIN) || Pressed(VK_RWIN);
                    try
                    {
                        var swallow = _clicker.Handle(vk, down, modifiers);
                        if (_log.IsEnabled(LogLevel.Debug))
                            _log.LogDebug("Key 0x{Vk:X2} {Dir} modifiers={Mod} clicker={On} swallow={Swallow}",
                                vk, down ? "down" : "up", modifiers, _clicker.Enabled, swallow);
                        if (swallow) return 1;
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Clicker hook callback failed");
                    }
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool Pressed(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose()
    {
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int x, y;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
