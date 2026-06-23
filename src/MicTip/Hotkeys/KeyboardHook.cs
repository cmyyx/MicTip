using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MicTip.Hotkeys;

/// <summary>
/// 低级键盘钩子 (WH_KEYBOARD_LL)。
/// 全局拦截键盘事件, 在进程内回调, 不注入其它进程 → 抗反作弊。
/// 游戏全屏独占下仍能可靠收到事件 (优于 RegisterHotKey)。
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;

    // lParam bit 30 = 1 表示该键此前已按下 (用于过滤 auto-repeat)
    private const int KF_REPEAT = 0x4000;

    private IntPtr _hookId = IntPtr.Zero;
    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookModule = IntPtr.Zero;

    /// <summary>键事件。参数: 虚拟键码, 是否按下 (true=按下, false=松开), 是否为 auto-repeat。</summary>
    public event Action<int, bool, bool>? KeyEvent;

    public KeyboardHook()
    {
        _proc = HookCallback;
    }

    public void Start()
    {
        if (_hookId != IntPtr.Zero) return;
        _hookModule = GetModuleHandle();
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, _hookModule, 0);
    }

    public void Stop()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vk = Marshal.ReadInt32(lParam);
            int flags = Marshal.ReadInt32(lParam, 8);
            bool isRepeat = (flags & KF_REPEAT) != 0;

            int msg = wParam.ToInt32();
            bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;

            if (down && !isRepeat)
                KeyEvent?.Invoke(vk, true, false);
            else if (down && isRepeat)
                KeyEvent?.Invoke(vk, true, true);
            else if (up)
                KeyEvent?.Invoke(vk, false, false);
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    /// <summary>获取当前进程主模块句柄 (低级钩子需要)。</summary>
    private static IntPtr GetModuleHandle()
    {
        using var proc = Process.GetCurrentProcess();
        using var module = proc.MainModule;
        return GetModuleHandle(module?.ModuleName ?? null);
    }

    public void Dispose() => Stop();

    // ===== P/Invoke =====
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn,
        IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode,
        IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
