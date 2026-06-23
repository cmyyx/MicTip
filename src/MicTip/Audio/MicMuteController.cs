using System.Runtime.InteropServices;
using MicTip.Models;
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

    // ===== 状态机 =====
    private bool _baseMuted;
    private bool _pttActive;

    // ===== 当前接管的目标设备 =====
    private List<MMDevice> _targets = new();
    private string? _currentDeviceName;
    private bool _disconnected = true;

    // 防止自身 SetMute 触发 OnVolumeNotification 时回环误判为"外部变更"
    private bool _suppressNotification;

    // 缓存上一次推送的状态, 用于判断是否需要 RaiseEvent
    private MicStateChangedEventArgs? _lastSnapshot;

    // ===== 设备热插拔防抖 (插拔瞬间会触发多次通知) =====
    private System.Threading.Timer? _refreshDebounce;
    private readonly object _refreshLock = new();

    public event EventHandler<MicStateChangedEventArgs>? StateChanged;

    /// <summary>当前是否处于断开状态 (无可用目标设备)。</summary>
    public bool IsDisconnected => _disconnected;

    public MicMuteController(AudioDeviceManager deviceManager, Func<Settings> getSettings)
    {
        _deviceManager = deviceManager;
        _getSettings = getSettings;
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
        // 释放旧目标的钩子
        DetachFromTargets();
        _targets.Clear();

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
        if (_suppressNotification) return; // 我们自己改的, 忽略
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

    /// <summary>切换静音基线。</summary>
    public void Toggle()
    {
        if (_disconnected) return;
        SetBaseMuted(!_baseMuted);
    }

    /// <summary>直接设置静音基线。</summary>
    public void SetBaseMuted(bool muted)
    {
        if (_disconnected) { return; }
        if (_baseMuted == muted) { RaiseIfStale(); return; }
        _baseMuted = muted;
        ApplyEffectiveToTargets();
        RaiseStateChanged();
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

    private void ApplyEffectiveToTargets()
    {
        bool effectiveMuted = _baseMuted && !_pttActive;
        _suppressNotification = true;
        try
        {
            foreach (var d in _targets)
            {
                try { d.AudioEndpointVolume.Mute = effectiveMuted; }
                catch (COMException) { /* 设备可能刚失效 */ }
                catch { }
            }
        }
        finally
        {
            _suppressNotification = false;
        }
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
            return peak;
        }
        catch { return 0; }
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
        DetachFromTargets();
        foreach (var d in _targets) d.Dispose();
        _targets.Clear();
    }
}
