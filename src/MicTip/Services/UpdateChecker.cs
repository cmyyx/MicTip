using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace MicTip.Services;

/// <summary>
/// 通过 GitHub Releases API 检查新版本。
/// 比较当前程序集版本与最新 release 的 tag_name。
/// </summary>
public sealed class UpdateChecker
{
    private const string RepoApiUrl = "https://api.github.com/repos/cmyyx/MicTip/releases/latest";
    private const string RepoUrl = "https://github.com/cmyyx/MicTip";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    /// <summary>检查结果。</summary>
    public sealed class UpdateResult
    {
        /// <summary>是否有新版本。</summary>
        public bool HasUpdate { get; init; }

        /// <summary>最新版本号 (不含 v 前缀)。</summary>
        public string? LatestVersion { get; init; }

        /// <summary>Release 页面 URL。</summary>
        public string? ReleaseUrl { get; init; }

        /// <summary>错误信息 (请求失败时)。</summary>
        public string? Error { get; init; }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }
    }

    /// <summary>当前程序集版本 (从 AssemblyInformationalVersion 读取, 形如 "0.1.0")。</summary>
    public static string CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?.Split('+')[0]
        ?? "0.0.0";

    /// <summary>仓库首页 URL。</summary>
    public static string RepoHomeUrl => RepoUrl;

    /// <summary>异步检查更新。forceCheck=true 时无论如何都发请求。</summary>
    public async Task<UpdateResult> CheckAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, RepoApiUrl);
            // GitHub API 要求 User-Agent
            req.Headers.UserAgent.ParseAdd($"MicTip/{CurrentVersion}");
            // 接受 JSON
            req.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                return new UpdateResult { Error = $"GitHub 返回 {resp.StatusCode}" };
            }

            var json = await resp.Content.ReadAsStringAsync();
            var release = JsonSerializer.Deserialize<GitHubRelease>(json);
            if (release?.TagName == null)
            {
                return new UpdateResult { Error = "未能解析 release 信息" };
            }

            string latest = release.TagName.TrimStart('v', 'V');
            bool newer = IsNewer(latest, CurrentVersion);

            return new UpdateResult
            {
                HasUpdate = newer,
                LatestVersion = latest,
                ReleaseUrl = release.HtmlUrl ?? RepoUrl,
            };
        }
        catch (Exception ex)
        {
            return new UpdateResult { Error = ex.Message };
        }
    }

    /// <summary>比较 latest 是否比 current 新 (按 Major.Minor.Build 数值)。</summary>
    private static bool IsNewer(string latest, string current)
    {
        if (!TryParseVersion(latest, out var l)) return false;
        if (!TryParseVersion(current, out var c)) return false;

        if (l.major != c.major) return l.major > c.major;
        if (l.minor != c.minor) return l.minor > c.minor;
        return l.build > c.build;
    }

    private static bool TryParseVersion(string s, out (int major, int minor, int build) v)
    {
        v = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var parts = s.Split('.');
        if (parts.Length < 2) return false;
        if (!int.TryParse(parts[0], out int major)) return false;
        int minor = 0, build = 0;
        if (parts.Length >= 2 && !int.TryParse(parts[1], out minor)) return false;
        if (parts.Length >= 3 && !int.TryParse(parts[2], out build)) build = 0;
        v = (major, minor, build);
        return true;
    }
}
