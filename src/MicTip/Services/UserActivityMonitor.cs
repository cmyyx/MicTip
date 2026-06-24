using System.Runtime.InteropServices;

namespace MicTip.Services;

/// <summary>
/// 基于 Win32 GetLastInputInfo 判断用户是否在与电脑交互。
/// 用于"无声提醒"防误触发: 仅当电脑活跃 (有键鼠输入) 时才累积无声时长,
/// 避免在看电影/离开时误报。
/// </summary>
public sealed class UserActivityMonitor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("kernel32.dll")]
    private static extern uint GetTickCount();

    /// <summary>
    /// 返回距离上次键鼠/触摸输入的时长。
    /// GetLastInputInfo 基于会话级输入, 覆盖键盘、鼠标、触摸。
    /// </summary>
    public TimeSpan GetIdleDuration()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info))
        {
            return TimeSpan.Zero;
        }

        // dwTime 与 GetTickCount 同源 (系统启动后毫秒, ~49 天回绕)
        uint now = GetTickCount();
        uint last = info.dwTime;
        uint diff = now >= last ? now - last : (uint.MaxValue - last) + now;
        return TimeSpan.FromMilliseconds(diff);
    }

    /// <summary>用户在过去 <paramref name="threshold"/> 内有过输入即为活跃。</summary>
    public bool IsUserActive(TimeSpan threshold)
    {
        try
        {
            return GetIdleDuration() <= threshold;
        }
        catch
        {
            // 取值失败时按"活跃"处理, 不阻断提醒逻辑
            return true;
        }
    }
}
