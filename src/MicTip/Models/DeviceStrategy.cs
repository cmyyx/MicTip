namespace MicTip.Models;

/// <summary>
/// 麦克风目标设备策略。
/// </summary>
public enum DeviceStrategy
{
    /// <summary>跟随默认通讯设备 (游戏语音/Discord/会议软件使用的角色)</summary>
    DefaultComm,

    /// <summary>跟随默认控制台设备 (eConsole, 覆盖面最广)</summary>
    DefaultConsole,

    /// <summary>仅操作指定设备 (SpecificDeviceId)</summary>
    Specific,

    /// <summary>静音所有输入设备</summary>
    All,
}
