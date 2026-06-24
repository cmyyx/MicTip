using MicTip.Hotkeys;
using System.Text.Json.Serialization;

namespace MicTip.Models;

/// <summary>悬浮窗在屏幕上的锚定位置。</summary>
public enum OverlayPosition
{
    BottomRight,
    BottomLeft,
    TopRight,
    TopLeft,
    Custom,
}

/// <summary>用户可持久化的设置。</summary>
public sealed class Settings
{
    /// <summary>切换静音的快捷键。默认 Ctrl+Alt+M。</summary>
    public HotkeyDef ToggleHotkey { get; set; } = new()
    {
        Modifiers = KeyModifiers.Control | KeyModifiers.Alt,
        Key = 'M',
    };

    /// <summary>Push-to-talk 按键。默认鼠标侧键 X1。</summary>
    public HotkeyDef PttHotkey { get; set; } = new()
    {
        Modifiers = KeyModifiers.None,
        Key = HotkeyDef.MouseX1,
    };

    /// <summary>麦克风目标设备策略。</summary>
    public DeviceStrategy DeviceStrategy { get; set; } = DeviceStrategy.DefaultConsole;

    /// <summary>当策略为 Specific 时使用的设备 ID。</summary>
    public string? SpecificDeviceId { get; set; }

    /// <summary>是否显示悬浮窗。</summary>
    public bool OverlayEnabled { get; set; } = true;

    /// <summary>悬浮窗锚定位置。</summary>
    public OverlayPosition OverlayPosition { get; set; } = OverlayPosition.BottomRight;

    /// <summary>自定义位置 (OverlayPosition = Custom 时使用)。</summary>
    public double OverlayX { get; set; } = double.NaN;

    public double OverlayY { get; set; } = double.NaN;

    /// <summary>是否启用无声提醒 (麦克风长时间无声音时弹窗提醒)。</summary>
    public bool IdleAlertEnabled { get; set; } = false;

    /// <summary>无声多久后触发提醒 (分钟)。默认 5。</summary>
    public int IdleAlertThresholdMinutes { get; set; } = 5;

    /// <summary>是否在启动时自动检查更新。默认 true。</summary>
    public bool CheckUpdatesOnStartup { get; set; } = true;

    /// <summary>产生一份带默认值的设置。</summary>
    public static Settings Default() => new();
}
