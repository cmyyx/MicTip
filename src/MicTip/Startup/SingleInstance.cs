using System.Threading;

namespace MicTip.Startup;

/// <summary>
/// 单实例保护。基于命名 Mutex, 防止重复启动。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = "Global\\MicTip_SingleInstance_3F7A1E";
    private readonly Mutex _mutex;
    public bool IsFirstInstance { get; }

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out bool createdNew);
        IsFirstInstance = createdNew;
    }

    public void Dispose()
    {
        if (IsFirstInstance)
        {
            try { _mutex.ReleaseMutex(); } catch { }
        }
        _mutex.Dispose();
    }
}
