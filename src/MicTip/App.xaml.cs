using System.Windows;
using MicTip.Audio;
using MicTip.Hotkeys;
using MicTip.Models;
using MicTip.Services;
using MicTip.Startup;
using MicTip.UI.Overlay;
using MicTip.UI.Tray;

namespace MicTip;

/// <summary>
/// 应用入口。负责:
/// - 单实例保护
/// - 音频控制器 / 托盘 / 悬浮窗 / 设置的协调
/// 热键在 Phase 4 接入。
/// </summary>
public partial class App : Application
{
    private SingleInstance? _singleInstance;
    private TrayManager? _tray;
    private OverlayWindow? _overlay;
    private SettingsService? _settingsService;
    private Settings? _settings;

    private AudioDeviceManager? _deviceManager;
    private MicMuteController? _controller;
    private VolumeMeterPoller? _meterPoller;
    private HotkeyManager? _hotkeyManager;

    // UI 渲染用的最近状态缓存
    private MicStateChangedEventArgs? _snap;
    private double _currentLevel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 单实例: 第二次启动直接退出
        _singleInstance = new SingleInstance();
        if (!_singleInstance.IsFirstInstance)
        {
            Shutdown(0);
            return;
        }

        // 加载设置
        _settingsService = new SettingsService();
        _settings = _settingsService.Load();

        // 音频后端
        _deviceManager = new AudioDeviceManager();
        _controller = new MicMuteController(_deviceManager, () => _settings!);
        _controller.StateChanged += OnControllerStateChanged;

        // 悬浮窗 (隐藏初始化)
        _overlay = new OverlayWindow();
        _overlay.PositionAt(_settings.OverlayPosition, new System.Windows.Point(_settings.OverlayX, _settings.OverlayY));
        _overlay.SetShowDeviceName(_settings.ShowDeviceName);
        _overlay.SetShowMeter(_settings.ShowMeter);

        // 电平轮询 → 刷新悬浮窗音量条
        _meterPoller = new VolumeMeterPoller(_controller, level =>
        {
            _currentLevel = level;
            RenderOverlay();
        });

        // 托盘
        _tray = new TrayManager();
        _tray.ToggleRequested += OnToggleRequested;
        _tray.SettingsRequested += OnSettingsRequested;
        _tray.OpenConfigFolderRequested += OnOpenConfigFolderRequested;
        _tray.ExitRequested += OnExitRequested;

        // 启动音频核心 (延迟到 Dispatcher 运行后, 确保 UI 窗口能正常显示)
        // 注意: 电平轮询器不在此处启动, 而是由 RenderOverlay 按悬浮窗可见性按需启停
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _controller.Start();
        }), System.Windows.Threading.DispatcherPriority.Loaded);

        // 热键 (键盘+鼠标低级钩子)
        _hotkeyManager = new HotkeyManager(_controller);
        _hotkeyManager.Configure(_settings.ToggleHotkey, _settings.PttHotkey);
        _hotkeyManager.Start();

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    // ===== 控制器 → UI =====

    private void OnControllerStateChanged(object? sender, MicStateChangedEventArgs e)
    {
        _snap = e;
        // 托盘/悬浮窗样式
        _tray?.Update(e.State, e.DeviceName);
        RenderOverlay();
    }

    /// <summary>根据最新状态 + 电平刷新悬浮窗内容与可见性。</summary>
    private void RenderOverlay()
    {
        if (_snap == null || _overlay == null) return;

        _overlay.ApplyState(_snap.State, _snap.DeviceName, _currentLevel, _snap.PttActive);

        // 悬浮窗可见性:
        //   BaseMuted=true  → 显示 (含 PTT 说话中, 这是临时状态仍需提示)
        //   BaseMuted=false → 延迟隐藏 (让用户看到"已开启"的短暂提示)
        bool overlayVisible = _settings?.OverlayEnabled == true
                              && !_snap.IsDisconnected
                              && _snap.BaseMuted;
        if (overlayVisible)
        {
            _overlay.ShowOverlay();
            _meterPoller?.Start();
        }
        else
        {
            _overlay.HideOverlayDelayed();
            // 仅在悬浮窗已实际隐藏后停止轮询 (淡出动画期间仍需刷新音量条)
            if (!_overlay.IsVisible)
            {
                _meterPoller?.Stop();
            }
        }
    }

    // ===== 托盘事件 =====

    private void OnToggleRequested(object? sender, EventArgs e)
    {
        _controller?.Toggle();
    }

    private void OnOpenConfigFolderRequested(object? sender, EventArgs e)
    {
        if (_settingsService == null) return;
        try
        {
            var dir = _settingsService.CurrentDir;
            if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
        catch { /* 忽略 */ }
    }

    private void OnSettingsRequested(object? sender, EventArgs e)
    {
        if (_settings == null || _deviceManager == null || _controller == null || _settingsService == null) return;

        var win = new UI.Settings.SettingsWindow(_settings, _deviceManager, ApplySettings, _settingsService);
        win.ShowDialog();
    }

    /// <summary>应用新设置 (来自设置窗口的"确定")。</summary>
    private void ApplySettings(Settings next)
    {
        bool strategyChanged = next.DeviceStrategy != _settings!.DeviceStrategy
                               || next.SpecificDeviceId != _settings.SpecificDeviceId;
        bool hotkeysChanged = !Equals(next.ToggleHotkey, _settings.ToggleHotkey)
                              || !Equals(next.PttHotkey, _settings.PttHotkey);
        bool positionChanged = next.OverlayPosition != _settings.OverlayPosition
                               || next.OverlayX != _settings.OverlayX
                               || next.OverlayY != _settings.OverlayY;

        // 替换生效设置
        _settings.ToggleHotkey = next.ToggleHotkey;
        _settings.PttHotkey = next.PttHotkey;
        _settings.DeviceStrategy = next.DeviceStrategy;
        _settings.SpecificDeviceId = next.SpecificDeviceId;
        _settings.OverlayEnabled = next.OverlayEnabled;
        _settings.ShowDeviceName = next.ShowDeviceName;
        _settings.ShowMeter = next.ShowMeter;
        _settings.OverlayPosition = next.OverlayPosition;
        _settings.OverlayX = next.OverlayX;
        _settings.OverlayY = next.OverlayY;

        // 持久化
        _settingsService?.Save(_settings);

        // 应用到运行中的组件
        if (hotkeysChanged) _hotkeyManager?.Configure(_settings.ToggleHotkey, _settings.PttHotkey);
        if (strategyChanged) _controller?.RefreshTargets();

        // 悬浮窗显示项
        _overlay?.SetShowDeviceName(_settings.ShowDeviceName);
        _overlay?.SetShowMeter(_settings.ShowMeter);
        if (positionChanged)
        {
            _overlay?.PositionAt(_settings.OverlayPosition, new System.Windows.Point(_settings.OverlayX, _settings.OverlayY));
        }

        // 刷新悬浮窗可见性 (OverlayEnabled 可能变了)
        RenderOverlay();
    }

    private void OnExitRequested(object? sender, EventArgs e) => Shutdown(0);

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeyManager?.Dispose();
        _meterPoller?.Stop();
        _controller?.Dispose();
        _deviceManager?.Dispose();
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
