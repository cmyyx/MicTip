namespace MicTip.Models;

/// <summary>
/// 麦克风对外呈现的物理状态 (托盘图标 + 悬浮窗样式以此为准)。
/// 已综合 BaseMuted / PttActive / 设备连接情况。
/// </summary>
public enum MicState
{
    /// <summary>设备在线且非静音 (含 PTT 说话中)</summary>
    Live,

    /// <summary>设备在线且静音</summary>
    Muted,

    /// <summary>设备断开/缺失</summary>
    Disconnected,
}
