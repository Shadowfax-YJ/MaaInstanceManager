using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MaaInstanceManager;

internal static class BlackFlowReleaseClient
{
    public const string CdnRoot = "https://img.lubiao.wiki/maa/blackflow";
    public const string GitHubRoot = "https://github.com/Shadowfax-YJ/MaaAssistantArknights/releases/download";
    public const string FeedUrl = CdnRoot + "/latest.json";
    private static readonly HttpClient Client = CreateClient();

    public sealed record Release(string Version, string Name, Uri Url, long Size, string Sha256);

    public static Uri[] SourceUrls(string cdn, string github, string source = "Auto") => source switch {
        "CDN" => [new(cdn)],
        "GitHub" => [new(github)],
        _ => [new(cdn), new(github)],
    };

    public static async Task<Release> CheckAsync(string source = "Auto", Func<Uri, Task<string>>? fetch = null)
    {
        fetch ??= async url => {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            return await Client.GetStringAsync(url, timeout.Token);
        };
        var errors = new List<Exception>();
        foreach (var url in SourceUrls(FeedUrl, GitHubRoot + "/blackflow-updates/latest.json", source))
        {
            try { return Parse(await fetch(url)); }
            catch (Exception ex) { errors.Add(ex); }
        }
        throw new AggregateException("所选更新源均无法提供有效清单", errors);
    }

    public static Release Parse(string json)
    {
        var root = JsonNode.Parse(json) ?? throw new InvalidDataException("更新清单为空");
        if (root["schema_version"]?.GetValue<int>() != 1 || root["channel"]?.GetValue<string>() != "blackflow-data-collection")
        {
            throw new InvalidDataException("不是采集版更新清单");
        }

        string version = root["version"]?.GetValue<string>() ?? "";
        if (!Regex.IsMatch(version, @"^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$"))
        {
            throw new InvalidDataException("更新版本号无效");
        }

        var asset = root["assets"]?["win-x64"] ?? throw new InvalidDataException("更新清单没有 Windows x64 安装包");
        string name = asset["name"]?.GetValue<string>() ?? "";
        string hash = asset["sha256"]?.GetValue<string>() ?? "";
        long size = asset["size"]?.GetValue<long>() ?? 0;
        if (name != $"MAA-BlackFlow-Data-Collection-{version}-win-x64.zip" || size <= 0 ||
            !Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$") ||
            !Uri.TryCreate(asset["url"]?.GetValue<string>(), UriKind.Absolute, out var url) ||
            url.Scheme != "https" || url.UserInfo.Length != 0)
        {
            throw new InvalidDataException("更新包地址或校验信息无效");
        }

        return new(version, name, url, size, hash);
    }

    public static async Task<string> DownloadAsync(Release release, string cacheDirectory, string source = "Auto", Func<Uri, string, Task>? download = null)
    {
        string directory = Path.Combine(cacheDirectory, "blackflow", release.Version);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, release.Name);
        if (File.Exists(path) && await VerifyAsync(path, release))
        {
            return path;
        }

        string temporary = path + ".download";
        download ??= DownloadFileAsync;
        try
        {
            var errors = new List<Exception>();
            foreach (var url in SourceUrls($"{CdnRoot}/{release.Version}/{release.Name}",
                         $"{GitHubRoot}/blackflow-{release.Version}/{release.Name}", source))
            {
                try
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                    await download(url, temporary);
                    if (!await VerifyAsync(temporary, release))
                        throw new InvalidDataException("下载文件大小或 SHA256 不一致");
                    ValidateIdentity(temporary, release.Version);
                    File.Move(temporary, path, overwrite: true);
                    return path;
                }
                catch (Exception ex) { errors.Add(ex); }
            }
            throw new AggregateException("所选更新源均下载失败，未更新任何实例", errors);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task DownloadFileAsync(Uri url, string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        await using var output = File.Create(path);
        await input.CopyToAsync(output, timeout.Token);
    }

    public static async Task<bool> VerifyAsync(string path, Release release)
    {
        await using var stream = File.OpenRead(path);
        return stream.Length == release.Size &&
            Convert.ToHexString(await SHA256.HashDataAsync(stream)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsBlackFlowInstance(string root)
    {
        string metadata = Path.Combine(root, "blackflow-update.json");
        if (File.Exists(metadata))
        {
            return JsonNode.Parse(File.ReadAllText(metadata))?["channel"]?.GetValue<string>() == "blackflow-data-collection";
        }

        // The first public collection packages predate the channel marker.
        string notice = Path.Combine(root, "NOTICE.txt");
        return File.Exists(notice) && File.ReadAllText(notice).StartsWith("黑流树海数据收集专用", StringComparison.Ordinal);
    }

    public static bool NeedsUpdate(string root, string targetVersion)
    {
        string metadata = Path.Combine(root, "blackflow-update.json");
        if (!File.Exists(metadata))
        {
            return true;
        }

        string current = JsonNode.Parse(File.ReadAllText(metadata))?["version"]?.GetValue<string>() ?? "";
        return !Version.TryParse(current.TrimStart('v'), out var version) || version < Version.Parse(targetVersion.TrimStart('v'));
    }

    public static void ValidateIdentity(string archivePath, string version)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        using var stream = (zip.GetEntry("blackflow-update.json") ?? throw new InvalidDataException("更新包缺少采集版标记")).Open();
        var root = JsonNode.Parse(stream);
        if (root?["channel"]?.GetValue<string>() != "blackflow-data-collection" || root?["version"]?.GetValue<string>() != version)
        {
            throw new InvalidDataException("更新包与版本清单不一致");
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MAAInstanceManager/BlackFlowUpdater");
        return client;
    }
}
