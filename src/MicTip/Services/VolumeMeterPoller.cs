using System.Windows.Threading;
using MicTip.Audio;

namespace MicTip.Services;

/// <summary>
/// 以 ~15fps 轮询麦克风峰值电平, 推送到悬浮窗音量条与无声提醒检测。
/// 轻量: 仅读一个 COM 属性, 由 UI 线程 DispatcherTimer 驱动。
/// </summary>
public sealed class VolumeMeterPoller
{
    private readonly DispatcherTimer _timer;
    private readonly MicMuteController _controller;
    private readonly Action<double> _onLevel;

    /// <param name="controller">音频控制器, 提供 ReadPeakLevel。</param>
    /// <param name="onLevel">UI 回调, 在 UI 线程被调用, 参数为 0.0~1.0 电平。</param>
    public VolumeMeterPoller(MicMuteController controller, Action<double> onLevel)
    {
        _controller = controller;
        _onLevel = onLevel;
        _timer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(66), // ~15fps
        };
        _timer.Tick += (_, _) =>
        {
            _onLevel(_controller.ReadPeakLevel());
        };
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();
}
