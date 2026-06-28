using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using MicTip.Audio;
using MicTip.Hotkeys;
using MicTip.Models;
using MicTip.Services;
using MicTip.Startup;
using MicTip.UI.Overlay;
using MicTip.UI.Tray;
using Microsoft.Win32;

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
    private UserActivityMonitor? _userActivity;
    private IdleMicAlerter? _idleAlerter;
    private UpdateChecker? _updateChecker;
    private Logger? _logger;
    private string? _pendingUpdateUrl;

    // UI 渲染用的最近状态缓存
    private MicStateChangedEventArgs? _snap;
    private double _currentLevel;
    private bool _idleAlertActive;

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

        // 日志服务 (与配置文件同目录, 便携模式优先)
        _logger = new Logger(_settingsService.CurrentDir);

        // 音频后端
        _deviceManager = new AudioDeviceManager();
        _controller = new MicMuteController(_deviceManager, () => _settings!, _logger);
        _controller.StateChanged += OnControllerStateChanged;

        // 悬浮窗 (隐藏初始化)
        _overlay = new OverlayWindow();
        _overlay.PositionAt(_settings.OverlayPosition, new System.Windows.Point(_settings.OverlayX, _settings.OverlayY));
        // 预热: 强制创建 HWND + 触发 SourceInitialized (ApplyNoActivate 设置 WS_EX_NOACTIVATE
        // 等样式), 把首次 Show() 时最重的 HWND 创建/COM interop 初始化移到启动期。
        // 否则首次按热键静音时 Show() 会同步完成 HWND 创建 + 模板应用 + 首次布局/渲染,
        // 约 100~300ms 可感知延迟; 后续 Show() 因 HWND 已存在而快得多。
        new WindowInteropHelper(_overlay).EnsureHandle();

        // 用户活跃检测 + 无声提醒状态机
        _userActivity = new UserActivityMonitor();
        _idleAlerter = new IdleMicAlerter(
            _controller,
            () => _settings!,
            _userActivity,
            OnIdleAlertShow,
            OnIdleAlertHide);
        _idleAlerter.PauseStateChanged += OnIdleAlertPauseStateChanged;

        // 电平轮询 → 刷新悬浮窗音量条 + 喂入无声检测
        _meterPoller = new VolumeMeterPoller(_controller, level =>
        {
            _currentLevel = level;
            _idleAlerter.OnLevelSample(level);
            if (_idleAlertActive)
            {
                // 提醒展示中: 只刷新音量条, 不走常规可见性逻辑
                _overlay?.UpdateMeter(level);
            }
            else
            {
                RenderOverlay();
            }
        });

        // 托盘
        _tray = new TrayManager();
        _tray.ToggleRequested += OnToggleRequested;
        _tray.SettingsRequested += OnSettingsRequested;
        _tray.OpenConfigFolderRequested += OnOpenConfigFolderRequested;
        _tray.ExitRequested += OnExitRequested;
        _tray.RestartRequested += OnRestartRequested;
        _tray.RestartAudioServiceRequested += OnRestartAudioServiceRequested;
        _tray.IdleAlertResumeRequested += OnIdleAlertResume;
        _tray.IdleAlertPauseRequested += OnIdleAlertPause;
        _tray.IdleAlertDisableRequested += OnIdleAlertDisable;
        _tray.AboutRequested += OnAboutRequested;
        _tray.UpdateNotificationClicked += OnUpdateNotificationClicked;
        _tray.UpdateIdleAlertMenu(_settings.IdleAlertEnabled, paused: false, pausedUntil: null);

        // 系统电源事件: 睡眠/唤醒适配
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // 更新检查器
        _updateChecker = new UpdateChecker();

        // 启动音频核心 (延迟到 Dispatcher 运行后, 确保 UI 窗口能正常显示)
        // 注意: 电平轮询器不在此处启动, 而是由 RenderOverlay 按悬浮窗可见性按需启停;
        // 若启用了无声提醒, 则常驻运行
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _controller.Start();
            if (_settings!.IdleAlertEnabled)
            {
                _meterPoller?.Start();
            }
            // 启动后延迟 10s 检查更新 (不阻塞启动, 静默)
            if (_settings.CheckUpdatesOnStartup)
            {
                Dispatcher.BeginInvoke(new Action(CheckForUpdatesSilently),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }), System.Windows.Threading.DispatcherPriority.Loaded);

        // 热键 (键盘+鼠标低级钩子)
        _hotkeyManager = new HotkeyManager(_controller);
        _hotkeyManager.ToggleFailed += OnToggleFailed;
        _hotkeyManager.ToggleIntercepted = OnToggleIntercepted;
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
        // 通知无声提醒: 非静音/断开状态变化可能需要隐藏提醒
        _idleAlerter?.OnMicStateChanged(e);
        RenderOverlay();
    }

    /// <summary>根据最新状态 + 电平刷新悬浮窗内容与可见性。</summary>
    private void RenderOverlay()
    {
        if (_snap == null || _overlay == null) return;

        // 无声提醒展示中: 由 IdleMicAlerter 管理可见性, 跳过常规逻辑
        if (_idleAlertActive) return;

        // 错误提示展示中: 由 ShowError 的 1.5s 计时器管理可见性, 跳过常规逻辑
        // 避免 66ms 音量轮询的 ApplyState 覆盖 "切换失败" 等错误提示
        if (_overlay.IsErrorMode) return;

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
            // 仅在悬浮窗已实际隐藏后停止轮询 (淡出动画期间仍需刷新音量条);
            // 启用了无声提醒时需常驻轮询以持续检测
            if (!_overlay.IsVisible && _settings?.IdleAlertEnabled != true)
            {
                _meterPoller?.Stop();
            }
        }
    }

    // ===== 无声提醒 =====

    private void OnIdleAlertShow()
    {
        _idleAlertActive = true;
        var snap = _controller?.GetSnapshot();
        _overlay?.ShowIdleAlert(snap?.DeviceName, _currentLevel);
    }

    private void OnIdleAlertHide()
    {
        _idleAlertActive = false;
        _overlay?.HideIdleAlert();
        // 隐藏后刷新一次以恢复正常可见性评估
        RenderOverlay();
    }

    /// <summary>切换快捷键拦截: 无声提醒正在展示时, 关闭本次提醒而非切换静音。</summary>
    private bool OnToggleIntercepted()
    {
        if (_idleAlerter == null) return false;
        return _idleAlerter.Dismiss();
    }

    private void OnIdleAlertResume(object? sender, EventArgs e)
    {
        _idleAlerter?.Resume();
        _tray?.UpdateIdleAlertMenu(_settings?.IdleAlertEnabled == true, paused: false, pausedUntil: null);
    }

    private void OnIdleAlertPause(object? sender, TimeSpan duration)
    {
        _idleAlerter?.Pause(duration);
        _tray?.UpdateIdleAlertMenu(_settings?.IdleAlertEnabled == true, paused: true, pausedUntil: _idleAlerter?.PausedUntil);
    }

    /// <summary>暂停状态变化回调: 来自暂停自然到期 / Pause / Resume。</summary>
    private void OnIdleAlertPauseStateChanged()
    {
        var enabled = _settings?.IdleAlertEnabled == true;
        var paused = _idleAlerter?.IsPaused ?? false;
        _tray?.UpdateIdleAlertMenu(enabled, paused, _idleAlerter?.PausedUntil);
    }

    private void OnIdleAlertDisable(object? sender, EventArgs e)
    {
        if (_settings == null) return;
        _settings.IdleAlertEnabled = false;
        _settingsService?.Save(_settings);
        _idleAlerter?.OnSettingsChanged();
        _tray?.UpdateIdleAlertMenu(enabled: false, paused: false, pausedUntil: null);
        // 关闭常驻轮询 (若悬浮窗也隐藏)
        if (_overlay is { IsVisible: false }) _meterPoller?.Stop();
    }

    // ===== 电源事件 (睡眠/唤醒适配) =====

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Resume:
                // 唤醒: 音频 COM 对象可能失效, 通过自愈流程刷新 (复用 SelfHealingRefresh 的
                // "重建枚举器 + 刷新目标 + 验证 + 日志" 完整路径, 避免空 catch 吞错);
                // 无声提醒进入宽限期, 避免睡眠时长被计入无声时长而误触发
                _logger?.LogInfo("系统唤醒, 触发自愈刷新与无声提醒宽限期");
                _idleAlerter?.OnPowerResume();
                // TriggerSelfHealingRefresh 内部 400ms 延迟 + Dispatcher.Invoke, 给音频服务恢复时间
                _controller?.TriggerSelfHealingRefresh();
                break;
            case PowerModes.Suspend:
                _logger?.LogInfo("系统进入睡眠, 重置无声提醒计时");
                _idleAlerter?.OnPowerSuspend();
                break;
        }
    }

    // ===== 重启 =====

    /// <summary>用户点击"重启"菜单: 释放单实例锁后启动新进程并退出当前实例。</summary>
    private void OnRestartRequested(object? sender, EventArgs e)
    {
        try
        {
            // 启动新实例 (detached), 与当前实例重叠几秒以避免短暂无程序运行
            var exePath = Environment.ProcessPath;
            if (exePath != null)
            {
                var psi = new System.Diagnostics.ProcessStartInfo(exePath)
                {
                    UseShellExecute = false,
                    // 不附属于当前进程, 当前进程退出后继续运行
                };
                System.Diagnostics.Process.Start(psi);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError("启动新实例失败", ex);
        }
        finally
        {
            // 先释放单实例锁, 让新实例能获取 Mutex; 然后退出
            try { _singleInstance?.Dispose(); _singleInstance = null; } catch { }
            Shutdown(0);
        }
    }

    // ===== 重启 Windows Audio 服务 =====

    /// <summary>
    /// 用户点击"重启 Windows Audio 服务": 后台执行 net stop/start audiosrv,
    /// 完成后触发自愈刷新重建失效的 COM 对象。
    /// 本程序已以管理员权限运行 (app.manifest requireAdministrator), 无需额外提权。
    /// </summary>
    private void OnRestartAudioServiceRequested(object? sender, EventArgs e)
    {
        // 先提示用户操作进行中, 避免误以为无响应
        _tray?.ShowBalloon("正在重启音频服务", "正在重启 Windows Audio 服务, 请稍候…");
        _logger?.LogInfo("用户请求重启 Windows Audio 服务");

        System.Threading.Tasks.Task.Run(() =>
        {
            bool ok = RestartAudioServiceCore();
            // 回到 UI 线程: 刷新设备 + 通知结果
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // 重置自愈冷却, 确保后续失败能立即重试
                try { _deviceManager?.RecreateEnumerator(); } catch { }
                try { _controller?.RefreshTargets(); } catch { }
                if (ok)
                {
                    _tray?.ShowBalloon("音频服务已重启", "Windows Audio 服务已重启, 设备已刷新。");
                }
                else
                {
                    _tray?.ShowBalloon("重启失败", "重启 Windows Audio 服务失败, 请查看日志。", warning: true);
                }
            }));
        });
    }

    /// <summary>
    /// 重启 Audiosrv 服务, 并以 PID 变化验证是否真正重启。
    /// 使用 PowerShell 的 Restart-Service -Force: 会带着依赖 audiosrv 的服务一并
    /// 优雅重启, 避免 sc stop 因 1051 错误而失败。
    /// 本程序已以管理员权限运行 (app.manifest requireAdministrator), 无需 RunAs 提权。
    /// </summary>
    private bool RestartAudioServiceCore()
    {
        // 1) 记录重启前 PID (最终验证用)
        int? pidBefore = GetServicePid("audiosrv");
        _logger?.LogInfo($"重启前 audiosrv PID={pidBefore?.ToString() ?? "(未知)"}");

        // 2) 调用 PowerShell Restart-Service -Force
        //    -Force: 带着依赖 audiosrv 的服务一并重启 (对应 sc stop 的 1051 场景)
        //    -ErrorAction Stop: 出错时抛异常, 使 $LASTEXITCODE 非 0
        _logger?.LogInfo("正在执行 Restart-Service -Name Audiosrv -Force …");
        (bool ok, string output) = RunCommand(
            "powershell.exe",
            "-NoProfile -Command \"try { Restart-Service -Name Audiosrv -Force -ErrorAction Stop; exit 0 } catch { Write-Host $_.Exception.Message; exit 1 }\"");
        _logger?.LogInfo($"Restart-Service 退出 ok={ok}, 输出: {output.Trim()}");

        // 3) 等待服务进程稳定 (Restart-Service 返回时服务应已重启, 给 1s 缓冲)
        System.Threading.Thread.Sleep(1000);

        // 4) 验证: PID 必须改变, 才能确认服务进程真的被替换了
        int? pidAfter = GetServicePid("audiosrv");
        _logger?.LogInfo($"重启后 audiosrv PID={pidAfter?.ToString() ?? "(未知)"}");

        if (pidBefore.HasValue && pidAfter.HasValue && pidBefore.Value != pidAfter.Value)
        {
            _logger?.LogInfo($"服务进程已更换 (PID {pidBefore} → {pidAfter}), 重启成功");
            return true;
        }
        if (pidBefore.HasValue && !pidAfter.HasValue)
        {
            _logger?.LogError($"重启后 audiosrv 进程不存在 (PID {pidBefore} → null), 服务未恢复运行");
            return false;
        }
        if (!ok)
        {
            _logger?.LogError($"Restart-Service 执行失败且 PID 未变 ({pidBefore} → {pidAfter})");
            return false;
        }
        // Restart-Service 返回成功但 PID 未变 (可能服务非常快重启用了相同 PID 池, 极罕见)
        _logger?.LogInfo($"Restart-Service 返回成功, PID {pidBefore} → {pidAfter} (未变, 但命令已成功)");
        return true;
    }

    /// <summary>
    /// 通过 sc queryex 获取服务进程的 PID。失败返回 null。
    /// sc queryex 输出含一行 "        PID                : 1234"。
    /// </summary>
    private int? GetServicePid(string service)
    {
        (bool _, string output) = RunCommand("sc.exe", $"queryex {service}");
        var match = System.Text.RegularExpressions.Regex.Match(
            output, @"PID\s*:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out int pid) && pid > 0)
        {
            return pid;
        }
        return null;
    }

    /// <summary>
    /// 通用命令执行: 返回 (是否退出码为 0, 标准输出+错误输出合并文本)。
    /// 异步读流避免管道死锁; 30s 超时避免永久挂起 (PowerShell 启动较慢)。
    /// </summary>
    private (bool ok, string output) RunCommand(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return (false, "Process.Start 返回 null");

            // 异步收集输出, 防止管道缓冲区满导致死锁
            var sb = new System.Text.StringBuilder();
            DataReceivedEventHandler onOut = (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            p.OutputDataReceived += onOut;
            p.ErrorDataReceived += onOut;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            // 超时 30s; PowerShell 启动 + Restart-Service 涉及多个服务, 可能需要 10s+
            if (!p.WaitForExit(30_000))
            {
                try { p.Kill(); } catch { }
                lock (sb) sb.AppendLine("[超时, 已强制结束]");
                return (false, sb.ToString());
            }
            // 等待异步输出读取完成
            p.WaitForExit();
            lock (sb) return (p.ExitCode == 0, sb.ToString());
        }
        catch (Exception ex)
        {
            _logger?.LogError($"{exe} {args} 执行异常", ex);
            return (false, ex.Message);
        }
    }

    // ===== 更新检查 =====

    /// <summary>启动时静默检查: 有新版本才弹气泡通知。</summary>
    private async void CheckForUpdatesSilently()
    {
        if (_updateChecker == null) return;
        var result = await _updateChecker.CheckAsync();
        if (result.HasUpdate && result.LatestVersion != null)
        {
            _pendingUpdateUrl = result.ReleaseUrl;
            _tray?.ShowUpdateNotification(result.LatestVersion);
        }
    }

    /// <summary>用户点击更新气泡 → 打开浏览器跳转 release 页。</summary>
    private void OnUpdateNotificationClicked(object? sender, EventArgs e)
    {
        if (_pendingUpdateUrl == null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_pendingUpdateUrl)
            {
                UseShellExecute = true,
            });
        }
        catch { /* 忽略 */ }
        _pendingUpdateUrl = null;
    }

    // ===== 关于窗口 =====

    private void OnAboutRequested(object? sender, EventArgs e)
    {
        var win = new UI.About.AboutWindow();
        win.ShowDialog();
    }

    // ===== 托盘事件 =====

    private void OnToggleRequested(object? sender, EventArgs e)
    {
        if (_controller == null) return;
        if (!_controller.Toggle())
        {
            // 设备断开或写入失败: 用悬浮窗显示错误提示 (比托盘闪烁更醒目)
            string msg = _controller.IsDisconnected ? "设备已断开" : "切换失败";
            LogToggleFailed("托盘", msg, _controller.GetSnapshot().DeviceName);
            _overlay?.ShowError(msg);
        }
    }

    /// <summary>热键触发 Toggle 失败时的回调: 直接显示错误提示 (不再重试 Toggle)。</summary>
    private void OnToggleFailed(object? sender, EventArgs e)
    {
        if (_controller == null) return;
        string msg = _controller.IsDisconnected ? "设备已断开" : "切换失败";
        LogToggleFailed("热键", msg, _controller.GetSnapshot().DeviceName);
        _overlay?.ShowError(msg);
    }

    /// <summary>记录一次切换失败到日志 (含来源、原因、设备名)。</summary>
    private void LogToggleFailed(string source, string reason, string? deviceName)
    {
        _logger?.LogError($"切换失败 [来源={source}] 原因={reason} 设备={deviceName ?? "?"}");
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
        bool idleAlertToggle = next.IdleAlertEnabled != _settings.IdleAlertEnabled;

        // 替换生效设置
        _settings.ToggleHotkey = next.ToggleHotkey;
        _settings.PttHotkey = next.PttHotkey;
        _settings.DeviceStrategy = next.DeviceStrategy;
        _settings.SpecificDeviceId = next.SpecificDeviceId;
        _settings.OverlayEnabled = next.OverlayEnabled;
        _settings.OverlayPosition = next.OverlayPosition;
        _settings.OverlayX = next.OverlayX;
        _settings.OverlayY = next.OverlayY;
        _settings.IdleAlertEnabled = next.IdleAlertEnabled;
        _settings.IdleAlertThresholdMinutes = next.IdleAlertThresholdMinutes;
        _settings.CheckUpdatesOnStartup = next.CheckUpdatesOnStartup;

        // 持久化
        _settingsService?.Save(_settings);

        // 应用到运行中的组件
        if (hotkeysChanged) _hotkeyManager?.Configure(_settings.ToggleHotkey, _settings.PttHotkey);
        if (strategyChanged) _controller?.RefreshTargets();

        if (positionChanged)
        {
            _overlay?.PositionAt(_settings.OverlayPosition, new System.Windows.Point(_settings.OverlayX, _settings.OverlayY));
        }

        // 无声提醒设置变更: 通知状态机刷新
        _idleAlerter?.OnSettingsChanged();
        if (idleAlertToggle)
        {
            if (_settings.IdleAlertEnabled)
            {
                _meterPoller?.Start();
            }
            else if (_overlay is { IsVisible: false })
            {
                _meterPoller?.Stop();
            }
            _tray?.UpdateIdleAlertMenu(_settings.IdleAlertEnabled, _idleAlerter?.IsPaused ?? false, _idleAlerter?.PausedUntil);
        }

        // 刷新悬浮窗可见性 (OverlayEnabled 可能变了)
        RenderOverlay();
    }

    private void OnExitRequested(object? sender, EventArgs e) => Shutdown(0);

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _hotkeyManager?.Dispose();
        _meterPoller?.Stop();
        _controller?.Dispose();
        _deviceManager?.Dispose();
        _tray?.Dispose();
        // 重启路径会提前释放 SingleInstance, 这里避免重复释放
        _singleInstance?.Dispose();
        _singleInstance = null;
        base.OnExit(e);
    }
}
