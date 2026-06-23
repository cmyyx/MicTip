using MicTip.Audio;

namespace MicTip.Hotkeys;

/// <summary>
/// 热键调度: 把 Toggle / PTT 两个 HotkeyDef 绑定到键盘+鼠标低级钩子,
/// 并翻译为对 MicMuteController 的调用。
///
/// Toggle 行为: 匹配快捷键的"首次按下"(过滤 auto-repeat) → controller.Toggle()。
/// PTT 行为:    按下 → controller.SetPtt(true); 松开 → controller.SetPtt(false)。
///              松开总会配对发送, 保证不会卡在"开麦"状态。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly MicMuteController _controller;
    private readonly KeyboardHook _keyboard = new();
    private readonly MouseHook _mouse = new();

    private HotkeyDef _toggle = HotkeyDef.Empty;
    private HotkeyDef _ptt = HotkeyDef.Empty;

    // PTT 防止 keyup 配对丢失 (例如按键匹配按下后状态变化)
    private bool _pttDown;

    public HotkeyManager(MicMuteController controller)
    {
        _controller = controller;
        _keyboard.KeyEvent += OnKeyEvent;
        _mouse.SideButtonEvent += OnSideButtonEvent;
    }

    /// <summary>更新当前生效的热键 (设置变更或启动时调用)。</summary>
    public void Configure(HotkeyDef toggle, HotkeyDef ptt)
    {
        _toggle = toggle;
        _ptt = ptt;
    }

    public void Start()
    {
        _keyboard.Start();
        _mouse.Start();
    }

    public void Stop()
    {
        _keyboard.Stop();
        _mouse.Stop();
        // 停止时确保 PTT 复位
        if (_pttDown)
        {
            _pttDown = false;
            _controller.SetPtt(false);
        }
    }

    // ===== 键盘事件 =====

    private void OnKeyEvent(int vk, bool down, bool isRepeat)
    {
        if (_toggle.Key > 0 && vk == _toggle.Key)
        {
            // Toggle: 仅响应非重复的首次按下
            if (down && !isRepeat && ModifiersMatch(_toggle))
            {
                _controller.Toggle();
            }
            return;
        }
        if (_ptt.Key > 0 && vk == _ptt.Key)
        {
            if (down && !isRepeat && ModifiersMatch(_ptt))
            {
                _pttDown = true;
                _controller.SetPtt(true);
            }
            else if (!down && _pttDown)
            {
                _pttDown = false;
                _controller.SetPtt(false);
            }
        }
    }

    // ===== 鼠标侧键事件 =====

    private void OnSideButtonEvent(int buttonCode, bool down)
    {
        if (_ptt.Key == buttonCode)
        {
            if (down)
            {
                _pttDown = true;
                _controller.SetPtt(true);
            }
            else if (_pttDown)
            {
                _pttDown = false;
                _controller.SetPtt(false);
            }
            return;
        }
        if (_toggle.Key == buttonCode)
        {
            if (down)
            {
                _controller.Toggle();
            }
        }
    }

    /// <summary>读取当前修饰键状态, 与 def.Modifiers 比对。</summary>
    private static bool ModifiersMatch(HotkeyDef def)
    {
        var mods = def.Modifiers;
        bool ctrl = (NativeMethods.GetKeyState(0xA2) & 0x8000) != 0
                  || (NativeMethods.GetKeyState(0xA3) & 0x8000) != 0; // LCtrl / RCtrl
        bool alt = (NativeMethods.GetKeyState(0xA4) & 0x8000) != 0
                 || (NativeMethods.GetKeyState(0xA5) & 0x8000) != 0;  // LAlt / RAlt
        bool shift = (NativeMethods.GetKeyState(0xA0) & 0x8000) != 0
                   || (NativeMethods.GetKeyState(0xA1) & 0x8000) != 0; // LShift / RShift

        if (mods.HasFlag(KeyModifiers.Control) != ctrl) return false;
        if (mods.HasFlag(KeyModifiers.Alt) != alt) return false;
        if (mods.HasFlag(KeyModifiers.Shift) != shift) return false;
        return true;
    }

    public void Dispose()
    {
        Stop();
        _keyboard.Dispose();
        _mouse.Dispose();
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern short GetKeyState(int nVirtKey);
    }
}
