using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MicTip.Models;

namespace MicTip.Services;

/// <summary>
/// 设置的加载/保存。
///
/// 存储位置策略 (便携模式优先):
///   1. 若 exe 同目录下存在 settings.json → 便携模式, 读写均在此目录
///   2. 否则 → %AppData%\MicTip\settings.json
///
/// 便携模式让用户可把整个程序文件夹拷贝到任意位置 (如 U 盘) 而不丢失配置。
/// </summary>
public sealed class SettingsService
{
    private static readonly string RoamingDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MicTip");

    private static readonly string RoamingFile = Path.Combine(RoamingDir, "settings.json");

    /// <summary>exe 所在目录 (用于便携模式检测)。</summary>
    private static readonly string ExeDir =
        Path.GetDirectoryName(Environment.ProcessPath) ?? RoamingDir;

    private static readonly string PortableFile = Path.Combine(ExeDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>当前是否处于便携模式 (配置文件位于 exe 同目录)。</summary>
    public bool IsPortable => File.Exists(PortableFile);

    /// <summary>当前生效的配置文件完整路径。</summary>
    public string CurrentFilePath => IsPortable ? PortableFile : RoamingFile;

    /// <summary>当前生效的配置所在目录。</summary>
    public string CurrentDir => IsPortable ? ExeDir : RoamingDir;

    /// <summary>加载设置; 不存在或损坏时返回默认值并忽略错误。</summary>
    public Settings Load()
    {
        var path = CurrentFilePath;
        try
        {
            if (!File.Exists(path)) return Settings.Default();
            var json = File.ReadAllText(path);
            var s = JsonSerializer.Deserialize<Settings>(json, JsonOpts);
            return s ?? Settings.Default();
        }
        catch
        {
            // 设置损坏不应阻止程序启动
            return Settings.Default();
        }
    }

    /// <summary>保存设置到磁盘。</summary>
    public void Save(Settings settings)
    {
        var path = CurrentFilePath;
        var dir = CurrentDir;
        try
        {
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(settings, JsonOpts);
            File.WriteAllText(path, json);
        }
        catch
        {
            // 写入失败静默忽略; 下次启动仍可运行
        }
    }
}
