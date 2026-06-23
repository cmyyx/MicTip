using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MicTip.Hotkeys;

/// <summary>
/// 低级鼠标钩子 (WH_MOUSE_LL)。
/// 用于 PTT 绑定鼠标侧键 (Mouse4=XBUTTON1 / Mouse5=XBUTTON2)。
/// 与键盘钩子一样在进程内回调, 不注入其它进程。
/// </summary>
public sealed class MouseHook : IDisposable
{
    private const int WH_MOUSE_LL = 14;

    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_XBUTTONUP = 0x020C;

    // HIWORD(wParam) 中的 XBUTTON 标识: 1 = XBUTTON1(Mouse4), 2 = XBUTTON2(Mouse5)
    private const int XBUTTON1 = 1;
    private const int XBUTTON2 = 2;

    private IntPtr _hookId = IntPtr.Zero;
    private readonly LowLevelMouseProc _proc;

    /// <summary>侧键事件。参数: 侧键码 (HotkeyDef.MouseX1/MouseX2), 是否按下。</summary>
    public event Action<int, bool>? SideButtonEvent;

    public MouseHook()
    {
        _proc = HookCallback;
    }

    public void Start()
    {
        if (_hookId != IntPtr.Zero) return;
        var module = GetModuleHandle();
        _hookId = SetWindowsHookEx(WH_MOUSE_LL, _proc, module, 0);
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
            int msg = wParam.ToInt32();
            if (msg == WM_XBUTTONDOWN || msg == WM_XBUTTONUP)
            {
                // 鼠标钩子的 lParam 是 MSLLHOOKSTRUCT; 高字 x-button 在 mouseData 的 HIWORD
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                int xButton = (int)((info.mouseData >> 16) & 0xFFFF);
                int code = xButton == XBUTTON1 ? HotkeyDef.MouseX1
                         : xButton == XBUTTON2 ? HotkeyDef.MouseX2
                         : 0;
                if (code != 0)
                {
                    bool down = msg == WM_XBUTTONDOWN;
                    SideButtonEvent?.Invoke(code, down);
                }
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static IntPtr GetModuleHandle()
    {
        using var proc = Process.GetCurrentProcess();
        using var module = proc.MainModule;
        return GetModuleHandle(module?.ModuleName ?? null);
    }

    public void Dispose() => Stop();

    // ===== P/Invoke =====
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public Point pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn,
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
