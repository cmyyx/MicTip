using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MicTip.Models;

namespace MicTip.Services;

/// <summary>
/// 设置的加载/保存。存放在 %AppData%\MicTip\settings.json。
/// </summary>
public sealed class SettingsService
{
    private static readonly string AppDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MicTip");

    private static readonly string FilePath = Path.Combine(AppDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>加载设置; 不存在或损坏时返回默认值并忽略错误。</summary>
    public Settings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return Settings.Default();
            var json = File.ReadAllText(FilePath);
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
        try
        {
            Directory.CreateDirectory(AppDir);
            var json = JsonSerializer.Serialize(settings, JsonOpts);
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // 写入失败静默忽略; 下次启动仍可运行
        }
    }
}
