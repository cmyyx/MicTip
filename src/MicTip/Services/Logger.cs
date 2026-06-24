using System.IO;
using System.Text;

namespace MicTip.Services;

/// <summary>
/// 轻量文件日志服务。
///
/// 设计要点:
///   - 线程安全 (lock 保护写入), 适用于热键/轮询/UI 多线程并发
///   - 单文件大小上限 512 KB, 超限后滚动: 当前文件 → .bak 覆盖, 新建空文件继续写
///   - 磁盘上最多两份日志 (.log + .log.bak), 合计约 1 MB, 不会无限制增长
///   - 写入失败静默忽略, 不影响主功能
///
/// 存储位置与 SettingsService 一致 (便携模式优先):
///   - 便携模式: exe 目录\mic-tip.log
///   - 普通模式: %AppData%\MicTip\mic-tip.log
/// </summary>
public sealed class Logger
{
    private const long MaxFileSize = 512 * 1024; // 512 KB
    private const string LogFileName = "mic-tip.log";
    private const string BakFileName = "mic-tip.log.bak";

    private readonly string _logPath;
    private readonly string _bakPath;
    private readonly object _lock = new();

    /// <param name="dir">日志所在目录 (通常为 SettingsService.CurrentDir)。</param>
    public Logger(string dir)
    {
        _logPath = Path.Combine(dir, LogFileName);
        _bakPath = Path.Combine(dir, BakFileName);
    }

    public void LogError(string message, Exception? ex = null) => Log("ERR", message, ex);
    public void LogInfo(string message) => Log("INF", message, null);

    private void Log(string level, string message, Exception? ex)
    {
        var sb = new StringBuilder();
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        sb.Append(" [").Append(level).Append("] ");
        sb.Append(message);
        if (ex != null)
        {
            sb.AppendLine();
            sb.Append("  → ").Append(ex.GetType().FullName ?? ex.GetType().Name);
            sb.Append(": ").Append(ex.Message);
            if (ex is System.Runtime.InteropServices.COMException com && com.ErrorCode != 0)
            {
                sb.Append(" (HRESULT=0x").Append(com.ErrorCode.ToString("X8")).Append(')');
            }
            if (!string.IsNullOrEmpty(ex.StackTrace))
            {
                sb.AppendLine();
                sb.Append(ex.StackTrace);
            }
        }
        sb.AppendLine();
        var line = sb.ToString();

        lock (_lock)
        {
            try { WriteInternal(line); }
            catch { /* 日志写入失败不应影响主功能 */ }
        }
    }

    private void WriteInternal(string line)
    {
        // 滚动检查: 超过上限则备份当前文件并新建
        try
        {
            if (File.Exists(_logPath))
            {
                var fi = new FileInfo(_logPath);
                if (fi.Length >= MaxFileSize)
                {
                    // .bak 覆盖旧备份
                    if (File.Exists(_bakPath)) File.Delete(_bakPath);
                    File.Move(_logPath, _bakPath);
                }
            }
        }
        catch { /* 滚动失败不阻止本次写入 */ }

        File.AppendAllText(_logPath, line, Encoding.UTF8);
    }
}
