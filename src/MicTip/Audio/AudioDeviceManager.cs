using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MicTip.Audio;

/// <summary>
/// 基于 NAudio 的设备枚举、默认设备解析与设备变更通知。
/// 负责"按策略找到目标麦克风设备"并监听热插拔/默认变更, 不负责状态机。
/// </summary>
public sealed class AudioDeviceManager : IDisposable, IMMNotificationClient
{
    private MMDeviceEnumerator _enumerator = new();
    private readonly object _enumeratorLock = new();

    /// <summary>设备集合或默认设备发生变更时触发 (Phase 3)。</summary>
    public event EventHandler? DevicesChanged;

    public AudioDeviceManager()
    {
        // 注册 COM 设备通知; NAudio 的 MMDeviceEnumerator 自身实现回调
        _enumerator.RegisterEndpointNotificationCallback(this);
    }

    /// <summary>枚举所有活动的输入(捕获)设备。</summary>
    public IList<MMDevice> EnumerateCaptureDevices()
    {
        lock (_enumeratorLock)
        {
            return _enumerator
                .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .ToList();
        }
    }

    /// <summary>按友好名查找设备 (用于 Specific 策略的匹配)。</summary>
    public MMDevice? FindByFriendlyName(string friendlyName)
    {
        // 先取快照再遍历, 未命中的设备统一释放, 避免 COM 引用缓慢泄漏
        var devices = EnumerateCaptureDevices();
        try
        {
            for (int i = 0; i < devices.Count; i++)
            {
                var d = devices[i];
                bool match = false;
                try { match = d.FriendlyName == friendlyName; } catch { }
                if (match) return d;
                d.Dispose();
            }
            return null;
        }
        catch
        {
            // 异常路径: 释放剩余设备
            foreach (var d in devices) try { d.Dispose(); } catch { }
            throw;
        }
    }

    /// <summary>默认通讯设备 (eCommunications 角色)。</summary>
    public MMDevice? GetDefaultCommunications()
    {
        try
        {
            lock (_enumeratorLock)
            {
                return _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            }
        }
        catch { return null; } // 无设备时抛异常
    }

    /// <summary>默认控制台设备 (eConsole 角色, 即"默认设备")。</summary>
    public MMDevice? GetDefaultConsole()
    {
        try
        {
            lock (_enumeratorLock)
            {
                return _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            }
        }
        catch { return null; }
    }

    /// <summary>
    /// 重建 MMDeviceEnumerator (在原实例疑似失效时调用, 例如 Windows 音频服务重启后)。
    /// 释放旧实例并注销回调, 再创建新实例并重新注册回调。
    /// </summary>
    public void RecreateEnumerator()
    {
        lock (_enumeratorLock)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
            try { _enumerator.Dispose(); } catch { }
            _enumerator = new MMDeviceEnumerator();
            _enumerator.RegisterEndpointNotificationCallback(this);
        }
    }

    public void Dispose()
    {
        try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
        _enumerator.Dispose();
    }

    // ===== IMMNotificationClient: 这些回调在 NAudio 的 COM 线程触发, 我们只转发事件 =====

    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        => RaiseChanged();

    public void OnDeviceAdded(string deviceId) => RaiseChanged();

    public void OnDeviceRemoved(string deviceId) => RaiseChanged();

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        // 只关心输入设备的默认变更
        if (flow == DataFlow.Capture) RaiseChanged();
    }

    public void OnPropertyValueChanged(string deviceId, PropertyKey propertyKey) { }

    private void RaiseChanged()
    {
        // 在后台线程触发, 避免阻塞 COM 回调线程; 订阅方需自行切回 UI 线程
        try { DevicesChanged?.Invoke(this, EventArgs.Empty); }
        catch { }
    }
}
