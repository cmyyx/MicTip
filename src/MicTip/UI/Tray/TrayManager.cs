using System.Drawing;
using System.Windows;
using MicTip.Models;
using MicTip.UI.Icons;
using WF = System.Windows.Forms;

namespace MicTip.UI.Tray;

/// <summary>
/// 系统托盘图标管理。三态图标 + 右键菜单。
/// 线程模型: 由 UI 线程创建, 状态更新需回到 UI 线程。
/// </summary>
public sealed class TrayManager : IDisposable
{
    private readonly WF.NotifyIcon _notifyIcon;
    private Icon? _currentIcon;
    private MicState _state = MicState.Disconnected;
    private bool _blinkOn = true;
    private System.Threading.Timer? _blinkTimer;
    private bool _blinking;

    // 菜单项引用 (用于根据状态启用/禁用/修改)
    private readonly WF.ToolStripMenuItem _statusItem;
    private readonly WF.ToolStripMenuItem _settingsItem;

    /// <summary>用户左键单击托盘图标, 请求切换静音。</summary>
    public event EventHandler? ToggleRequested;

    /// <summary>用户点击"设置"。</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>用户点击"打开配置目录"。</summary>
    public event EventHandler? OpenConfigFolderRequested;

    /// <summary>用户点击"退出"。</summary>
    public event EventHandler? ExitRequested;

    public TrayManager()
    {
        _notifyIcon = new WF.NotifyIcon
        {
            Visible = true,
            Text = "MicTip",
        };
        _notifyIcon.MouseClick += OnTrayClick;

        _statusItem = new WF.ToolStripMenuItem("…") { Enabled = false };
        _settingsItem = new WF.ToolStripMenuItem("设置…");
        _settingsItem.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        var openConfigItem = new WF.ToolStripMenuItem("打开配置目录…");
        openConfigItem.Click += (_, _) => OpenConfigFolderRequested?.Invoke(this, EventArgs.Empty);
        var exitItem = new WF.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new WF.ContextMenuStrip();
        menu.Items.AddRange(new WF.ToolStripItem[]
        {
            _statusItem,
            new WF.ToolStripSeparator(),
            _settingsItem,
            openConfigItem,
            new WF.ToolStripSeparator(),
            exitItem,
        });
        _notifyIcon.ContextMenuStrip = menu;

        // 初始图标
        Update(MicState.Disconnected, null);
    }

    private void OnTrayClick(object? sender, WF.MouseEventArgs e)
    {
        // 左键单击: 切换静音 (设置通过右键菜单访问)
        if (e.Button == WF.MouseButtons.Left)
        {
            ToggleRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>更新托盘状态 (图标 + tooltip + 菜单文案)。</summary>
    public void Update(MicState state, string? deviceName)
    {
        if (System.Windows.Application.Current is { } app)
        {
            if (!app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(new Action(() => Update(state, deviceName)));
                return;
            }
        }

        _state = state;

        // 断开状态下停止闪烁
        if (state != MicState.Disconnected)
        {
            StopBlink();
        }

        ApplyIcon(state, deviceName);

        _statusItem.Text = state switch
        {
            MicState.Live => $"状态: 已开启{(deviceName is null ? "" : $" ({deviceName})")}",
            MicState.Muted => $"状态: 已静音{(deviceName is null ? "" : $" ({deviceName})")}",
            MicState.Disconnected => deviceName is null
                ? "状态: 未找到麦克风设备"
                : $"状态: 设备已断开 (已配置: {deviceName})",
            _ => "状态: 未知",
        };
    }

    private void ApplyIcon(MicState state, string? deviceName)
    {
        var built = IconFactory.Build(state, deviceName);
        var old = _currentIcon;
        _notifyIcon.Icon = built.Icon;
        _notifyIcon.Text = TruncateForTooltip(built.Tooltip);
        _currentIcon = built.Icon;
        // 旧 HIcon 不主动销毁 (Icon 对象 GC 时由 Finalizer 处理; 频繁切换下可接受)
        old?.Dispose();
    }

    /// <summary>断开状态下按了快捷键时调用: 短暂闪烁托盘提示"无效操作"。</summary>
    public void Flash()
    {
        if (System.Windows.Application.Current is { } app && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(new Action(Flash));
            return;
        }
        StartBlink();
    }

    private void StartBlink()
    {
        _blinking = true;
        _blinkOn = true;
        _blinkTimer ??= new System.Threading.Timer(_ => BlinkTick(), null, 0, 200);
    }

    private void StopBlink()
    {
        if (!_blinking) return;
        _blinking = false;
        _blinkTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void BlinkTick()
    {
        var app = System.Windows.Application.Current;
        if (app == null) return;
        app.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_blinking) return;
            _blinkOn = !_blinkOn;
            // 闪烁时在 Muted 与 Live 间切换外观以引起注意
            var display = _blinkOn ? MicState.Muted : MicState.Disconnected;
            var built = IconFactory.Build(display, null);
            _notifyIcon.Icon = built.Icon;
            // 计数闪 6 次 (约 1.2s) 后停止
            _blinkCount++;
            if (_blinkCount >= 6)
            {
                _blinkCount = 0;
                StopBlink();
            }
        }));
    }
    private int _blinkCount;

    /// <summary>tooltip 上限 63 字符。</summary>
    private static string TruncateForTooltip(string s) => s.Length <= 63 ? s : s[..60] + "...";

    public void Dispose()
    {
        _blinkTimer?.Dispose();
        if (_notifyIcon.Visible)
        {
            _notifyIcon.Visible = false;
        }
        _notifyIcon.Dispose();
        _currentIcon?.Dispose();
    }
}
