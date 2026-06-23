using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
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
    private static readonly System.Windows.Media.Color MutedBg = System.Windows.Media.Color.FromRgb(60, 20, 24);        // 深红背景
    private static readonly System.Windows.Media.Color LiveBg = System.Windows.Media.Color.FromRgb(20, 50, 36);         // 深绿背景
    private static readonly System.Windows.Media.Color IdleBg = System.Windows.Media.Color.FromRgb(31, 41, 55);         // 中性深灰

    private Action<double, double>? _onPositionChanged;
    private bool? _targetVisibility;
    private System.Windows.Threading.DispatcherTimer? _hideTimer;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyNoActivate();
    }

    /// <summary>设置位置持久化回调 (拖动结束后调用)。</summary>
    public void SetPositionCallback(Action<double, double> onPositionChanged)
        => _onPositionChanged = onPositionChanged;

    /// <summary>是否显示音量条。</summary>
    public void SetShowMeter(bool show) => MeterBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>是否显示设备名。</summary>
    public void SetShowDeviceName(bool show) => DeviceText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

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

    // ===== Win32: 让窗口不抢焦点、不进 Alt-Tab =====
    private void ApplyNoActivate()
    {
        var helper = new WindowInteropHelper(this);
        int exStyle = NativeMethods.GetWindowLong(helper.Handle, NativeMethods.GWL_EXSTYLE);
        // 注意: 不加 WS_EX_TRANSPARENT, 否则无法接收鼠标拖动
        exStyle |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLong(helper.Handle, NativeMethods.GWL_EXSTYLE, exStyle);
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

    // ===== 显示/隐藏 (带淡入淡出) =====

    public void ShowOverlay()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(ShowOverlay)); return; }
        // 取消任何挂起的延迟隐藏
        _hideTimer?.Stop();
        _hideTimer = null;
        if (_targetVisibility == true) { return; }
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

    public void HideOverlay()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(HideOverlay)); return; }
        _hideTimer?.Stop();
        _hideTimer = null;
        if (_targetVisibility == false) { return; }
        _targetVisibility = false;

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

    // ===== 拖动 =====

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
            // DragMove 返回即松手
            _onPositionChanged?.Invoke(Left, Top);
        }
    }

    private static class NativeMethods
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
