using MicTip.Models;

namespace MicTip.Audio;

/// <summary>
/// 控制器向 UI 推送的状态快照。包含所有 UI 刷新所需信息。
/// </summary>
public sealed class MicStateChangedEventArgs : EventArgs
{
    /// <summary>是否处于断开 (设备缺失) 状态。</summary>
    public bool IsDisconnected { get; init; }

    /// <summary>当前目标设备友好名 (断开时为已配置的设备名或 null)。</summary>
    public string? DeviceName { get; init; }

    /// <summary>静音基线 (由 Toggle / 外部静音变更驱动, 不含 PTT)。</summary>
    public bool BaseMuted { get; init; }

    /// <summary>当前有效静音 = BaseMuted &amp;&amp; !PttActive (写入 Core Audio 的物理状态)。</summary>
    public bool EffectiveMuted { get; init; }

    /// <summary>PTT 临时取消静音中。</summary>
    public bool PttActive { get; init; }

    /// <summary>对外呈现的 MicState (托盘图标/悬浮窗样式依据)。</summary>
    public MicState State => IsDisconnected
        ? MicState.Disconnected
        : (EffectiveMuted ? MicState.Muted : MicState.Live);
}
