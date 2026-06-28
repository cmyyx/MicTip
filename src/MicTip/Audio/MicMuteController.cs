using System.Runtime.InteropServices;
using MicTip.Models;
using MicTip.Services;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MicTip.Audio;

/// <summary>
/// 麦克风静音核心状态机。
///
/// 状态模型:
///   BaseMuted      静音基线 (Toggle / 外部静音变更驱动)
///   PttActive      PTT 按住 && BaseMuted → 临时取消静音
///   EffectiveMuted = BaseMuted && !PttActive   → 写入 Core Audio 的物理静音
///
/// 职责:
///   - 按策略解析目标设备并读写其 AudioEndpointVolume
///   - 监听 OnVolumeNotification 同步 BaseMuted (区分自身写入与外部变更)
///   - 对外推送 MicStateChangedEventArgs
///   - 暴露 Toggle() / SetPtt(bool) 供热键层调用
///
/// 设备热插拔/默认变更在 Phase 3 通过 RefreshTargets() + 事件接入。
/// </summary>
public sealed class MicMuteController : IDisposable
{
    private readonly AudioDeviceManager _deviceManager;
    private readonly Func<Settings> _getSettings;
    private readonly Logger? _logger;

    // ===== 状态机 =====
    private bool _baseMuted;
    private bool _pttActive;

    // ===== 当前接管的目标设备 =====
    private List<MMDevice> _targets = new();
    private string? _currentDeviceName;
    private bool _disconnected = true;

    // 防止自身 SetMute 触发 OnVolumeNotification 时回环误判为"外部变更"
    // 与 ApplyEffectiveToTargets / OnVolumeNotification 共用此锁, 避免跨线程读写 race
    private readonly object _suppressLock = new();
    private bool _suppressNotification;

    // 缓存上一次推送的状态, 用于判断是否需要 RaiseEvent
    private MicStateChangedEventArgs? _lastSnapshot;

    // ===== 设备热插拔防抖 (插拔瞬间会触发多次通知) =====
    private System.Threading.Timer? _refreshDebounce;
    private readonly object _refreshLock = new();

    // ===== 自愈刷新 (独立于设备变更防抖, 避免共享 Timer 导致回调错配 + 永不触发) =====
    private System.Threading.Timer? _selfHealTimer;
    // 自愈已排程但尚未执行期间为 true, 用于抑制 66ms 轮询的重复日志和重复排程
    private volatile bool _selfHealPending;
    // 自愈执行后的冷却截止时间; 冷却期内静默返回 0, 避免服务持续异常时刷屏
    private DateTime _selfHealCooldownUntil = DateTime.MinValue;

    public event EventHandler<MicStateChangedEventArgs>? StateChanged;

    /// <summary>当前是否处于断开状态 (无可用目标设备)。</summary>
    public bool IsDisconnected => _disconnected;

    public MicMuteController(AudioDeviceManager deviceManager, Func<Settings> getSettings, Logger? logger = null)
    {
        _deviceManager = deviceManager;
        _getSettings = getSettings;
        _logger = logger;
    }

    /// <summary>启动: 解析目标设备, 监听设备变更, 同步初始状态。</summary>
    public void Start()
    {
        _deviceManager.DevicesChanged += OnDevicesChanged;
        RefreshTargets();
    }

    // ===== 设备热插拔 (Phase 3) =====

    /// <summary>设备增删/默认变更回调 (来自 COM 线程)。防抖后刷新目标。</summary>
    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        // 插拔瞬间 COM 会连续触发多次, 用 400ms 防抖收敛为一次刷新
        lock (_refreshLock)
        {
            _refreshDebounce ??= new System.Threading.Timer(
                _ => { try { RefreshTargets(); } catch { } },
                null, 400, Timeout.Infinite);
            _refreshDebounce.Change(400, Timeout.Infinite);
        }
    }


    // ===== 目标设备解析 =====

    /// <summary>按当前策略重新解析目标设备, 并 (重新) 挂载静音通知。</summary>
    public void RefreshTargets()
    {
        // 释放旧目标的钩子并 Dispose COM 对象, 避免热插拔/服务重启场景下缓慢泄漏
        DetachFromTargets();
        var old = _targets;
        _targets = new List<MMDevice>();
        foreach (var d in old) try { d.Dispose(); } catch { }

        var settings = _getSettings();
        List<MMDevice> resolved = new();

        switch (settings.DeviceStrategy)
        {
            case DeviceStrategy.DefaultComm:
                {
                    var d = _deviceManager.GetDefaultCommunications();
                    if (d != null) resolved.Add(d);
                    break;
                }
            case DeviceStrategy.DefaultConsole:
                {
                    var d = _deviceManager.GetDefaultConsole();
                    if (d != null) resolved.Add(d);
                    break;
                }
            case DeviceStrategy.Specific:
                {
                    if (!string.IsNullOrWhiteSpace(settings.SpecificDeviceId))
                    {
                        var d = _deviceManager.FindByFriendlyName(settings.SpecificDeviceId!);
                        if (d != null) resolved.Add(d);
                    }
                    break;
                }
            case DeviceStrategy.All:
                {
                    foreach (var d in _deviceManager.EnumerateCaptureDevices())
                        resolved.Add(d);
                    break;
                }
        }

        _targets = resolved;

        bool nowDisconnected = _targets.Count == 0;
        if (nowDisconnected)
        {
            _disconnected = true;
            _currentDeviceName = settings.DeviceStrategy == DeviceStrategy.Specific
                ? settings.SpecificDeviceId
                : null;
            RaiseStateChanged();
            return;
        }

        _disconnected = false;
        _currentDeviceName = _targets.Count == 1
            ? _targets[0].FriendlyName
            : $"{_targets.Count} 个设备";

        // 挂载静音通知 + 同步 BaseMuted 来自物理状态
        AttachToTargets();

        // 以物理静音状态初始化 BaseMuted (首次启动尊重设备当前状态)
        try
        {
            _baseMuted = _targets[0].AudioEndpointVolume.Mute;
        }
        catch
        {
            _baseMuted = false;
        }

        // 把 EffectiveMuted 应用到所有目标 (确保一致)
        ApplyEffectiveToTargets();
        RaiseStateChanged();
    }

    private void DetachFromTargets()
    {
        foreach (var d in _targets)
        {
            try { d.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification; } catch { }
        }
    }

    private void AttachToTargets()
    {
        foreach (var d in _targets)
        {
            try { d.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification; } catch { }
        }
    }

    // ===== 外部静音变更回调 (NAudio 通知) =====

    private void OnVolumeNotification(AudioVolumeNotificationData data)
    {
        // 复制标志到栈, 避免与 ApplyEffectiveToTargets 写入端 race
        bool suppressed;
        lock (_suppressLock) suppressed = _suppressNotification;
        if (suppressed) return; // 我们自己改的, 忽略
        if (_targets.Count == 0) return;

        bool physicalMuted;
        try { physicalMuted = _targets[0].AudioEndpointVolume.Mute; }
        catch { return; }

        // PTT 激活时物理静音是 false（我们自己临时解除的），
        // 此时 physicalMuted=false 而 _baseMuted=true，属于正常状态，不应视为外部变更。
        // 仅当物理状态等于 EffectiveMuted 以外的值时才认定为外部变更。
        bool expectedPhysical = _baseMuted && !_pttActive;
        if (physicalMuted == expectedPhysical) return;

        // 真正的外部变更：将 _baseMuted 同步到物理状态
        // PTT 在外部静音被改变时同样复位（避免状态不一致）
        _pttActive = false;
        _baseMuted = physicalMuted;
        ApplyEffectiveToTargets();
        RaiseStateChanged();
    }

    // ===== 公开动作 (供热键层调用) =====

    /// <summary>切换静音基线。返回 false 表示设备断开或写入失败。</summary>
    public bool Toggle()
    {
        if (_disconnected) return false;
        return SetBaseMuted(!_baseMuted);
    }

    /// <summary>直接设置静音基线。返回 false 表示设备断开或写入失败。</summary>
    public bool SetBaseMuted(bool muted)
    {
        if (_disconnected) { return false; }
        if (_baseMuted == muted) { RaiseIfStale(); return true; }
        _baseMuted = muted;
        bool ok = ApplyEffectiveToTargets();
        if (!ok)
        {
            // 写入失败: 回滚状态机, 避免与物理状态不一致
            _baseMuted = !muted;
        }
        RaiseStateChanged();
        return ok;
    }

    /// <summary>
    /// 设置 PTT 激活态。仅在 BaseMuted=true 时有意义 (PTT 临时取消静音)。
    /// 非静音时调用 = 无操作。
    /// </summary>
    public void SetPtt(bool active)
    {
        if (_disconnected) return;
        if (_pttActive == active) return;
        // PTT 只在 BaseMuted 时生效
        if (active && !_baseMuted) return;
        _pttActive = active;
        ApplyEffectiveToTargets();
        RaiseStateChanged();
    }

    // ===== 写入 Core Audio =====

    /// <summary>把 EffectiveMuted 写入所有目标设备。返回是否全部成功。</summary>
    private bool ApplyEffectiveToTargets()
    {
        bool effectiveMuted = _baseMuted && !_pttActive;
        bool allOk = true;
        lock (_suppressLock) _suppressNotification = true;
        try
        {
            foreach (var d in _targets)
            {
                string? deviceName = null;
                try { deviceName = d.FriendlyName; } catch { }
                try
                {
                    d.AudioEndpointVolume.Mute = effectiveMuted;
                }
                catch (COMException ex)
                {
                    allOk = false;
                    _logger?.LogError(
                        $"写入设备静音失败 (目标 Mute={effectiveMuted}, 设备={deviceName ?? "?"})",
                        ex);
                }
                catch (Exception ex)
                {
                    allOk = false;
                    _logger?.LogError(
                        $"写入设备静音失败 (目标 Mute={effectiveMuted}, 设备={deviceName ?? "?"})",
                        ex);
                }
            }
        }
        finally
        {
            lock (_suppressLock) _suppressNotification = false;
        }
        return allOk;
    }

    // ===== 电平采样 (悬浮窗音量条用) =====

    /// <summary>读取当前峰值电平 (0.0~1.0)。断开或多设备时取首个; 失败返回 0。</summary>
    public double ReadPeakLevel()
    {
        if (_targets.Count == 0) return 0;
        try
        {
            // All 策略: 取所有目标的峰值最大值, 直观反映"是否有设备在收音"
            double peak = 0;
            foreach (var d in _targets)
            {
                double v = d.AudioMeterInformation.MasterPeakValue;
                if (v > peak) peak = v;
            }
            // 成功读取: 清除冷却, 下次失败可立即触发自愈
            _selfHealCooldownUntil = DateTime.MinValue;
            return peak;
        }
        catch (Exception ex)
        {
            // COM 对象失效 (常见于 Windows 音频服务重启或睡眠唤醒后)。
            // 66ms 轮询会连续命中此分支, 必须抑制重复日志/排程, 否则日志刷屏且
            // 自愈 Timer 被 Change(400) 反复延后导致永不执行。
            // - 自愈已排程 (_selfHealPending): 静默等待执行
            // - 自愈冷却期内: 静默, 避免服务持续异常时每 400ms 刷一条日志
            if (_selfHealPending) return 0;
            if (DateTime.Now < _selfHealCooldownUntil) return 0;

            _selfHealPending = true;
            _logger?.LogError("读取峰值电平失败, 触发设备自愈刷新", ex);
            ScheduleSelfHealingRefresh();
            return 0;
        }
    }

    /// <summary>
    /// 触发一次带 enumerator 重建的自愈刷新。延迟 400ms 执行, 期间若再次调用会重置延迟。
    /// 在 COM 线程/UI 线程都可调用, 实际刷新在 UI 线程执行。
    /// </summary>
    private void ScheduleSelfHealingRefresh()
    {
        // 使用独立的 _selfHealTimer, 不与设备变更防抖 _refreshDebounce 共享
        // (共享会导致回调错配: 谁先创建就用谁的回调)。
        if (_selfHealTimer == null)
        {
            _selfHealTimer = new System.Threading.Timer(
                _ =>
                {
                    // 必须在 UI 线程执行: MMDeviceEnumerator 和它枚举出的 MMDevice 绑定到
                    // 创建线程的 COM apartment。ReadPeakLevel 由 DispatcherTimer 在 UI 线程 (STA)
                    // 调用, 若在线程池线程 (MTA) 重建 enumerator, 后续 UI 线程访问
                    // AudioMeterInformation 会因跨 apartment QueryInterface 失败而抛
                    // InvalidCastException (E_NOINTERFACE) — 这正是 "重启软件能恢复,
                    // 但进程内自愈无效" 的根因。
                    try
                    {
                        var disp = System.Windows.Application.Current?.Dispatcher;
                        if (disp != null) disp.Invoke(SelfHealingRefresh);
                        else SelfHealingRefresh();
                    }
                    catch { }
                },
                null, Timeout.Infinite, Timeout.Infinite);
        }
        // 重排程: _selfHealPending 保证一次失败周期内只调用一次, 不会出现
        // "Change(400) 反复延后导致永不执行" 的问题 (那是早先与 _refreshDebounce
        // 共享 Timer 时的隐患, 现在独立 Timer 已不存在)。
        // 不用 ??= 是因为 Timer 构造时 period=Infinite, 一次性触发后不会自动再触发,
        // 第二次失败时 ??= 是 no-op, 会导致自愈只生效一次。
        _selfHealTimer.Change(400, Timeout.Infinite);
    }

    /// <summary>
    /// 外部主动触发自愈刷新 (例如系统唤醒后)。复用 SelfHealingRefresh 的完整流程:
    /// UI 线程执行 + 重建枚举器 + 刷新目标 + 验证 + 日志。
    /// </summary>
    public void TriggerSelfHealingRefresh() => ScheduleSelfHealingRefresh();

    /// <summary>自愈刷新: 先尝试重建 enumerator, 再 RefreshTargets。执行后进入冷却。</summary>
    private void SelfHealingRefresh()
    {
        bool recreated = false, refreshed = false;
        try
        {
            _deviceManager.RecreateEnumerator();
            recreated = true;
        }
        catch (Exception ex)
        {
            _logger?.LogError("重建 MMDeviceEnumerator 失败", ex);
        }
        try { RefreshTargets(); refreshed = true; }
        catch (Exception ex) { _logger?.LogError("RefreshTargets 失败", ex); }

        // 立即验证新 COM 对象在 UI 线程是否可用: 唤醒后音频服务慢恢复时, 即使
        // enumerator 重建成功, 端点对象本身可能仍是坏的, 此时验证会暴露问题
        // 而非靠下一次 66ms 轮询被动发现。
        bool verified = false;
        if (_targets.Count > 0)
        {
            try
            {
                _ = _targets[0].AudioMeterInformation.MasterPeakValue;
                verified = true;
            }
            catch (Exception ex)
            {
                _logger?.LogError("自愈后验证读峰值仍失败", ex);
            }
        }
        _logger?.LogInfo($"自愈刷新完成: 重建枚举器={recreated}, 刷新目标={refreshed}, 验证读峰值={verified}, 目标数={_targets.Count}");

        // 清除排程标志, 设置 10s 冷却:
        // - 若自愈生效, 下次 ReadPeakLevel 成功会立即清除冷却
        // - 若自愈未生效 (服务持续异常), 冷却期内静默, 避免每 400ms 刷一条日志
        _selfHealPending = false;
        _selfHealCooldownUntil = DateTime.Now + TimeSpan.FromSeconds(10);
    }

    /// <summary>当前状态快照 (供外部按需查询, 如电平轮询时附带)。</summary>
    public MicStateChangedEventArgs GetSnapshot() => BuildSnapshot();

    // ===== 推送事件 =====

    private void RaiseIfStale() => RaiseStateChanged();

    private void RaiseStateChanged()
    {
        var snap = BuildSnapshot();
        // 仅在状态语义变化时推送 (避免重复)
        if (_lastSnapshot != null
            && _lastSnapshot.IsDisconnected == snap.IsDisconnected
            && _lastSnapshot.BaseMuted == snap.BaseMuted
            && _lastSnapshot.PttActive == snap.PttActive
            && _lastSnapshot.DeviceName == snap.DeviceName)
        {
            _lastSnapshot = snap;
            return;
        }
        _lastSnapshot = snap;
        StateChanged?.Invoke(this, snap);
    }

    private MicStateChangedEventArgs BuildSnapshot() => new()
    {
        IsDisconnected = _disconnected,
        DeviceName = _currentDeviceName,
        BaseMuted = _baseMuted,
        PttActive = _pttActive,
        EffectiveMuted = !_disconnected && _baseMuted && !_pttActive,
    };

    public void Dispose()
    {
        _deviceManager.DevicesChanged -= OnDevicesChanged;
        _refreshDebounce?.Dispose();
        _selfHealTimer?.Dispose();
        DetachFromTargets();
        foreach (var d in _targets) d.Dispose();
        _targets.Clear();
    }
}
