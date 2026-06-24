using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MicTip.Models;

namespace MicTip.UI.Overlay;

/// <summary>
/// 麦克风静音提醒悬浮窗。
/// - 仅在静音基线=开时由 App 决定可见
/// - 不抢焦点 (WS_EX_NOACTIVATE), 不出现在 Alt-Tab (WS_EX_TOOLWINDOW)
/// - 可拖动; 松手后持久化位置
/// - 共享布局: 静音/说话中仅图标/文本/主色不同
/// </summary>
public partial class OverlayWindow : Window
{
    // 主色
    private static readonly System.Windows.Media.Color MutedColor = System.Windows.Media.Color.FromRgb(220, 38, 38);    // 红
    private static readonly System.Windows.Media.Color LiveColor = System.Windows.Media.Color.FromRgb(34, 197, 94);     // 绿
    private static readonly System.Windows.Media.Color WarnColor = System.Windows.Media.Color.FromRgb(245, 158, 11);    // 琥珀
    private static readonly System.Windows.Media.Color MutedBg = System.Windows.Media.Color.FromRgb(60, 20, 24);        // 深红背景
    private static readonly System.Windows.Media.Color LiveBg = System.Windows.Media.Color.FromRgb(20, 50, 36);         // 深绿背景
    private static readonly System.Windows.Media.Color WarnBg = System.Windows.Media.Color.FromRgb(60, 40, 10);         // 深琥珀背景
    private static readonly System.Windows.Media.Color IdleBg = System.Windows.Media.Color.FromRgb(31, 41, 55);         // 中性深灰

    private bool? _targetVisibility;
    private bool _idleAlertMode;
    private bool _errorMode;
    private System.Windows.Threading.DispatcherTimer? _hideTimer;
    private System.Windows.Threading.DispatcherTimer? _topmostTimer;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyNoActivate();
    }

    /// <summary>按 OverlayPosition 定位到屏幕边角; 已定位过的则保持。</summary>
    public void PositionAt(OverlayPosition pos, System.Windows.Point custom)
    {
        double w = Width + 10, h = Height + 10;
        var work = SystemParameters.WorkArea;

        double x, y;
        switch (pos)
        {
            default:
            case OverlayPosition.BottomRight:
                x = work.Right - w; y = work.Bottom - h; break;
            case OverlayPosition.BottomLeft:
                x = work.Left; y = work.Bottom - h; break;
            case OverlayPosition.TopRight:
                x = work.Right - w; y = work.Top; break;
            case OverlayPosition.TopLeft:
                x = work.Left; y = work.Top; break;
            case OverlayPosition.Custom:
                x = double.IsNaN(custom.X) ? work.Right - w : custom.X;
                y = double.IsNaN(custom.Y) ? work.Bottom - h : custom.Y;
                break;
        }
        Left = x;
        Top = y;
    }

    // ===== Win32: 让窗口不抢焦点、不进 Alt-Tab、点击穿透 =====
    private void ApplyNoActivate()
    {
        var helper = new WindowInteropHelper(this);
        int exStyle = NativeMethods.GetWindowLong(helper.Handle, NativeMethods.GWL_EXSTYLE);
        // WS_EX_NOACTIVATE: 永不获取焦点
        // WS_EX_TOOLWINDOW: 不进 Alt-Tab / 任务栏
        // WS_EX_TRANSPARENT: 点击穿透 (鼠标事件直接传给下方窗口)
        exStyle |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLong(helper.Handle, NativeMethods.GWL_EXSTYLE, exStyle);

        // 周期性重置 z-order 到最顶层, 抵抗其他被激活的置顶窗口覆盖
        _topmostTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _topmostTimer.Tick += (_, _) => ReassertTopmost();
        _topmostTimer.Start();
    }

    /// <summary>重新将窗口置于顶层 (不激活)。</summary>
    private void ReassertTopmost()
    {
        if (!IsVisible) return;
        var helper = new WindowInteropHelper(this);
        const uint flags = NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOOWNERZORDER;
        NativeMethods.SetWindowPos(helper.Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, flags);
    }

    /// <summary>根据状态刷新外观。pttActive=true 时 Live 显示说话中，否则显示已开启。</summary>
    public void ApplyState(MicState state, string? deviceName, double level, bool pttActive = false)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => ApplyState(state, deviceName, level, pttActive)));
            return;
        }

        System.Windows.Media.Color accent;
        switch (state)
        {
            case MicState.Muted:
                IconText.Text = "🔇";
                StatusText.Text = "已静音";
                accent = MutedColor;
                RootBackground.Color = MutedBg;
                MeterBar.Foreground = new SolidColorBrush(accent);
                break;
            case MicState.Live:
                IconText.Text = "🎤";
                StatusText.Text = pttActive ? "说话中" : "已开启";
                accent = LiveColor;
                RootBackground.Color = LiveBg;
                MeterBar.Foreground = new SolidColorBrush(accent);
                break;
            default: // Disconnected (通常不显示, 但防御性处理)
                IconText.Text = "🎤";
                StatusText.Text = "设备已断开";
                accent = System.Windows.Media.Colors.SlateGray;
                RootBackground.Color = IdleBg;
                MeterBar.Foreground = new SolidColorBrush(accent);
                break;
        }

        DeviceText.Text = deviceName ?? "";
        MeterBar.Value = Math.Clamp(level, 0, 1);
    }

    // ===== 无声提醒 (独立于 ApplyState 的样式, 琥珀色, 不自动淡出) =====

    /// <summary>展示无声提醒: 琥珀色样式 + 提示文案, 持续显示直到 HideIdleAlert。</summary>
    public void ShowIdleAlert(string? deviceName, double level)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => ShowIdleAlert(deviceName, level)));
            return;
        }

        _idleAlertMode = true;
        _errorMode = false;
        // 取消挂起的隐藏
        _hideTimer?.Stop();
        _hideTimer = null;

        IconText.Text = "🎤";
        StatusText.Text = "麦克风似乎没声音";
        RootBackground.Color = WarnBg;
        MeterBar.Foreground = new SolidColorBrush(WarnColor);

        DeviceText.Text = deviceName ?? "";
        MeterBar.Value = Math.Clamp(level, 0, 1);

        _targetVisibility = true;
        double startOpacity = Opacity;
        if (!IsVisible)
        {
            startOpacity = 0;
            Opacity = 0;
            Show();
        }
        var anim = new DoubleAnimation(startOpacity, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new QuadraticEase() };
        BeginAnimation(OpacityProperty, anim);
    }

    /// <summary>仅刷新音量条 (提醒展示期间由电平轮询调用)。</summary>
    public void UpdateMeter(double level)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => UpdateMeter(level)));
            return;
        }
        MeterBar.Value = Math.Clamp(level, 0, 1);
    }

    /// <summary>退出无声提醒模式，不立即隐藏。可见性交由后续 RenderOverlay 根据当前麦克风状态决定，
    /// 以便在检测到声音时先短暂显示"已开启"样式再淡出，避免视觉上一闪而过。</summary>
    public void HideIdleAlert()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(HideIdleAlert));
            return;
        }
        if (!_idleAlertMode) return;
        _idleAlertMode = false;
        _hideTimer?.Stop();
        _hideTimer = null;
    }

    /// <summary>当前是否处于无声提醒模式。</summary>
    public bool IsIdleAlertMode => _idleAlertMode;

    /// <summary>当前是否处于错误提示模式 (ShowError 触发, 期间阻止 ApplyState 覆盖)。</summary>
    public bool IsErrorMode => _errorMode;

    // ===== 显示/隐藏 (带淡入淡出) =====

    public void ShowOverlay()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(ShowOverlay)); return; }
        // 取消任何挂起的延迟隐藏
        _hideTimer?.Stop();
        _hideTimer = null;
        if (_targetVisibility == true) { return; }
        _targetVisibility = true;
        _idleAlertMode = false;
        _errorMode = false;

        double startOpacity = Opacity;
        if (!IsVisible)
        {
            startOpacity = 0;
            Opacity = 0;
            Show();
        }
        var anim = new DoubleAnimation(startOpacity, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new QuadraticEase() };
        BeginAnimation(OpacityProperty, anim);
    }

    public void HideOverlay()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(HideOverlay)); return; }
        _hideTimer?.Stop();
        _hideTimer = null;
        if (_targetVisibility == false) { return; }
        _targetVisibility = false;
        _errorMode = false;

        var anim = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new QuadraticEase() };
        anim.Completed += (_, _) => { if (_targetVisibility == false && Opacity <= 0.01) Hide(); };
        BeginAnimation(OpacityProperty, anim);
    }

    /// <summary>延迟 800ms 后淡出，期间若 ShowOverlay 被调用则取消。</summary>
    public void HideOverlayDelayed()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(HideOverlayDelayed)); return; }
        // 已是隐藏目标且无挂起计时器，无需操作
        if (_targetVisibility == false && _hideTimer == null) { return; }
        // 已有计时器在倒计时，不重复创建
        if (_hideTimer != null) { return; }

        _hideTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(800)
        };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer?.Stop();
            _hideTimer = null;
            HideOverlay();
        };
        _hideTimer.Start();
    }

    /// <summary>
    /// 显示错误提示 (如静音切换失败): 琥珀色样式 + 警告图标,
    /// 1.5 秒后自动淡出。覆盖普通显示逻辑。
    /// </summary>
    public void ShowError(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => ShowError(message)));
            return;
        }

        // 取消挂起的隐藏
        _hideTimer?.Stop();
        _hideTimer = null;

        // 应用错误样式
        _idleAlertMode = false;
        _errorMode = true;
        IconText.Text = "⚠";
        StatusText.Text = message;
        RootBackground.Color = WarnBg;
        MeterBar.Foreground = new SolidColorBrush(WarnColor);
        MeterBar.Value = 0;

        _targetVisibility = true;
        double startOpacity = Opacity;
        if (!IsVisible)
        {
            startOpacity = 0;
            Opacity = 0;
            Show();
        }
        var anim = new DoubleAnimation(startOpacity, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new QuadraticEase() };
        BeginAnimation(OpacityProperty, anim);

        // 1.5 秒后自动淡出
        _hideTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer?.Stop();
            _hideTimer = null;
            HideOverlay();
        };
        _hideTimer.Start();
    }

    // ===== 拖动 (已移除: 悬浮窗点击穿透, 位置通过设置窗口配置) =====

    private static class NativeMethods
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_TRANSPARENT = 0x00000020;

        public static readonly IntPtr HWND_TOPMOST = new(-1);

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_NOOWNERZORDER = 0x0200;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    }
}
