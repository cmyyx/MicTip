using MicTip.Audio;
using MicTip.Models;

namespace MicTip.Services;

/// <summary>
/// 麦克风无声提醒状态机。
///
/// 触发条件 (全部满足):
///   - 设置中 IdleAlertEnabled = true 且未被托盘暂停
///   - 麦克风处于 Live (非静音、在线、非 PTT)
///   - 电脑活跃 (GetLastInputInfo 在 30s 内有输入) —— 防误触发
///   - 连续无声时长 >= IdleAlertThresholdMinutes
///
/// 解除条件:
///   - 检测到声音 (level >= 灵敏度阈值) → 立即隐藏并重置
///   - 用户按切换快捷键 → 隐藏并重置 (本次冷却)
///   - 麦克风变为静音/断开/PTT → 隐藏并重置
///   - 电脑转为非活跃 → 隐藏并重置 (看电影等场景)
///
/// 线程模型: 所有方法在 UI 线程调用 (由 VolumeMeterPoller 的 DispatcherTimer 驱动)。
/// </summary>
public sealed class IdleMicAlerter
{
    private readonly MicMuteController _controller;
    private readonly Func<Settings> _getSettings;
    private readonly UserActivityMonitor _activity;

    // UI 回调 (UI 线程)
    private readonly Action _onShowAlert;
    private readonly Action _onHideAlert;

    /// <summary>电脑活跃判定阈值: 过去 30s 内有键鼠输入视为活跃。</summary>
    private static readonly TimeSpan ActiveThreshold = TimeSpan.FromSeconds(30);

    /// <summary>唤醒后宽限期: 系统从睡眠恢复后在此时间内不触发提醒, 等待音频设备稳定与用户实际开始使用。</summary>
    private static readonly TimeSpan PowerResumeGrace = TimeSpan.FromSeconds(30);

    private DateTime? _silentSince;
    private bool _alerting;
    private DateTime? _pausedUntil;
    private DateTime? _powerResumeUntil;

    /// <summary>当前是否正在展示提醒。</summary>
    public bool IsAlerting => _alerting;

    /// <summary>当前是否处于托盘暂停 (尚未到期)。</summary>
    public bool IsPaused => _pausedUntil.HasValue && _pausedUntil.Value > DateTime.Now;

    /// <summary>当前暂停到期时间 (未暂停时为 null)。供 UI 显示"暂停至 HH:mm"。</summary>
    public DateTime? PausedUntil => IsPaused ? _pausedUntil : null;

    /// <summary>暂停状态变化 (进入暂停 / 暂停自然到期 / 手动恢复)。</summary>
    public event Action? PauseStateChanged;

    public IdleMicAlerter(
        MicMuteController controller,
        Func<Settings> getSettings,
        UserActivityMonitor activity,
        Action onShowAlert,
        Action onHideAlert)
    {
        _controller = controller;
        _getSettings = getSettings;
        _activity = activity;
        _onShowAlert = onShowAlert;
        _onHideAlert = onHideAlert;
    }

    /// <summary>由电平轮询驱动 (UI 线程)。level 为 0.0~1.0 峰值。</summary>
    public void OnLevelSample(double level)
    {
        // 清理已过期的暂停: 自然到期时触发事件通知 UI 刷新菜单
        if (_pausedUntil.HasValue && _pausedUntil.Value <= DateTime.Now)
        {
            _pausedUntil = null;
            PauseStateChanged?.Invoke();
        }

        var settings = _getSettings();

        // 功能关闭或暂停: 确保提醒不展示
        if (!settings.IdleAlertEnabled || IsPaused)
        {
            HideIfAlerting();
            _silentSince = null;
            return;
        }

        // 唤醒宽限期内不触发提醒, 等待音频与用户活动稳定
        if (_powerResumeUntil.HasValue && _powerResumeUntil.Value > DateTime.Now)
        {
            _silentSince = null;
            HideIfAlerting();
            return;
        }
        else if (_powerResumeUntil.HasValue)
        {
            _powerResumeUntil = null;
        }

        var snap = _controller.GetSnapshot();

        // 仅在 Live 状态 (非静音、在线) 下检测; PTT 说话中也视为有声
        if (snap.IsDisconnected || snap.BaseMuted)
        {
            HideIfAlerting();
            _silentSince = null;
            return;
        }
        if (snap.PttActive)
        {
            _silentSince = null;
            HideIfAlerting();
            return;
        }

        double thr = 0; // 任何声音 (level > 0) 即算有声

        // 有声音 → 重置计时并隐藏提醒
        if (level > thr)
        {
            _silentSince = null;
            HideIfAlerting();
            return;
        }

        // 无声但电脑非活跃 (看电影/离开) → 重置, 不累积
        bool active = _activity.IsUserActive(ActiveThreshold);
        if (!active)
        {
            _silentSince = null;
            HideIfAlerting();
            return;
        }

        // 活跃 + 无声: 累积计时
        _silentSince ??= DateTime.Now;

        var threshold = TimeSpan.FromMinutes(Math.Max(1, settings.IdleAlertThresholdMinutes));
        if (!_alerting && DateTime.Now - _silentSince >= threshold)
        {
            _alerting = true;
            _onShowAlert();
        }
    }

    /// <summary>麦克风状态变更回调 (UI 线程)。非 Live 状态下隐藏并重置。</summary>
    public void OnMicStateChanged(MicStateChangedEventArgs e)
    {
        if (e.IsDisconnected || e.BaseMuted)
        {
            HideIfAlerting();
            _silentSince = null;
        }
        else if (e.PttActive)
        {
            _silentSince = null;
            HideIfAlerting();
        }
    }

    /// <summary>设置变更回调: 若功能被关闭则隐藏提醒。</summary>
    public void OnSettingsChanged()
    {
        var settings = _getSettings();
        if (!settings.IdleAlertEnabled)
        {
            HideIfAlerting();
            _silentSince = null;
        }
    }

    /// <summary>切换快捷键"关闭本次": 隐藏提醒并重置计时 (再过阈值才会再次触发)。</summary>
    /// <returns>是否实际拦截了本次快捷键 (提醒正在展示时为 true)。</returns>
    public bool Dismiss()
    {
        if (!_alerting) return false;
        _silentSince = DateTime.Now; // 冷却: 重新开始计时
        HideIfAlerting();
        return true;
    }

    /// <summary>托盘暂停: 在指定时长内不检测。duration 为零或负表示立即恢复。</summary>
    public void Pause(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            _pausedUntil = null;
        }
        else
        {
            _pausedUntil = DateTime.Now + duration;
        }
        HideIfAlerting();
        _silentSince = null;
        PauseStateChanged?.Invoke();
    }

    /// <summary>立即恢复检测 (取消暂停)。</summary>
    public void Resume()
    {
        if (_pausedUntil == null) return;
        _pausedUntil = null;
        PauseStateChanged?.Invoke();
    }

    /// <summary>系统从睡眠/休眠恢复: 重置计时并设置宽限期, 避免因睡眠时长误触发。</summary>
    public void OnPowerResume()
    {
        _silentSince = null;
        _powerResumeUntil = DateTime.Now + PowerResumeGrace;
        HideIfAlerting();
    }

    /// <summary>系统进入睡眠/休眠前: 重置计时, 避免唤醒瞬间误触发。</summary>
    public void OnPowerSuspend()
    {
        _silentSince = null;
        HideIfAlerting();
    }

    private void HideIfAlerting()
    {
        if (!_alerting) return;
        _alerting = false;
        _onHideAlert();
    }
}
