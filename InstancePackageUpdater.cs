using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace MaaInstanceManager;

/// <summary>Installs a full package into a stopped instance without replacing user state.</summary>
internal sealed class InstancePackageUpdater : IDisposable
{
    private static readonly HashSet<string> Preserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "config", "cache", "data", "debug", "reports", "achievement", ".old",
    };

    private readonly string _staging;
    private readonly string[] _files;

    private InstancePackageUpdater(string staging, string[] files)
    {
        _staging = staging;
        _files = files;
    }

    public static InstancePackageUpdater Prepare(string archivePath, string cacheDirectory)
    {
        string staging = Path.Combine(Path.GetFullPath(cacheDirectory), "install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                string name = NormalizeRelativePath(entry.FullName.TrimEnd('/', '\\'));
                if (!names.Add(name) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                {
                    throw new InvalidDataException("更新包包含重复路径或符号链接");
                }
            }

            foreach (var name in new[] { "MAA.exe", "MaaCore.dll", "MAA.Updater.exe", "filelist.txt" })
            {
                if (!names.Contains(name))
                {
                    throw new InvalidDataException("请选择完整 MAA 安装包，缺少 " + name);
                }
            }

            if (names.Contains("changes.json") || names.Contains("removelist.txt"))
            {
                throw new InvalidDataException("批量更新需要完整安装包");
            }

            zip.ExtractToDirectory(staging);
            var files = Directory.GetFiles(staging, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(staging, path).Replace('\\', '/'))
                .Where(path => !IsPreserved(path)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            // Validate the previous-version inventory before it is ever used to remove stale program files.
            _ = ReadManifest(staging);
            return new(staging, files);
        }
        catch
        {
            Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    public void Apply(string instanceDirectory, Action<string>? installingFile = null)
    {
        string root = Path.GetFullPath(instanceDirectory);
        if (!File.Exists(Path.Combine(root, "MAA.exe")))
        {
            throw new InvalidDataException("实例目录下没有 MAA.exe");
        }

        string backup = Path.Combine(root, ".instance-update-" + Guid.NewGuid().ToString("N"));
        string[] previousFiles = ReadManifest(root);
        string[] affected = previousFiles.Concat(_files).Append("filelist.txt")
            .Where(path => !IsPreserved(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string path in affected)
        {
            EnsureNoLinks(root, path);
        }

        var saved = new List<string>();
        var written = new List<string>();
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "transaction.json"), JsonSerializer.Serialize(new { affected, state = "applying" }));
        try
        {
            foreach (string path in affected)
            {
                string target = UnderRoot(root, path);
                if (Directory.Exists(target))
                {
                    throw new IOException("文件路径被目录占用: " + path);
                }

                if (!File.Exists(target))
                {
                    continue;
                }

                string old = UnderRoot(backup, path);
                Directory.CreateDirectory(Path.GetDirectoryName(old)!);
                File.Move(target, old);
                saved.Add(path);
            }

            foreach (string path in _files)
            {
                installingFile?.Invoke(path);
                string target = UnderRoot(root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                written.Add(path);
                File.Copy(UnderRoot(_staging, path), target);
            }

            File.WriteAllText(Path.Combine(backup, "transaction.json"), JsonSerializer.Serialize(new { affected, state = "complete" }));
        }
        catch (Exception failure)
        {
            try
            {
                foreach (string path in written.AsEnumerable().Reverse())
                {
                    File.Delete(UnderRoot(root, path));
                }

                foreach (string path in saved.AsEnumerable().Reverse())
                {
                    File.Move(UnderRoot(backup, path), UnderRoot(root, path));
                }

                File.WriteAllText(Path.Combine(backup, "transaction.json"), JsonSerializer.Serialize(new { affected, state = "rolled_back" }));
            }
            catch (Exception rollbackFailure)
            {
                throw new AggregateException("更新失败，旧程序备份保留在 " + backup, failure, rollbackFailure);
            }

            throw new IOException("更新失败，已恢复旧程序: " + failure.Message, failure);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_staging))
        {
            Directory.Delete(_staging, recursive: true);
        }
    }

    private static string[] ReadManifest(string root)
    {
        string path = Path.Combine(root, "filelist.txt");
        return File.Exists(path)
            ? File.ReadAllLines(path).Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => NormalizeRelativePath(line.Trim())).ToArray()
            : [];
    }

    private static bool IsPreserved(string path)
    {
        string normalized = NormalizeRelativePath(path);
        return Preserved.Contains(normalized.Split('/')[0]) ||
            normalized.StartsWith("Res/Backgrounds/Wallpapers/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelativePath(string path)
    {
        string value = path.Replace('\\', '/');
        if (value.Length == 0 || value.StartsWith('/') || value.Contains(':') ||
            value.Split('/').Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ')))
        {
            throw new InvalidDataException("更新包路径无效: " + path);
        }

        return value;
    }

    private static string UnderRoot(string root, string relative)
    {
        return Path.Combine(root, NormalizeRelativePath(relative).Replace('/', Path.DirectorySeparatorChar));
    }

    private static void EnsureNoLinks(string root, string relative)
    {
        string current = root;
        foreach (string segment in ("/" + relative).Split('/'))
        {
            if (segment.Length > 0)
            {
                current = Path.Combine(current, segment);
            }

            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("更新路径不能经过目录链接: " + current);
            }
        }
    }
}
