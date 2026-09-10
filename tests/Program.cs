using System.IO.Compression;
using System.Text.Json.Nodes;
using MaaInstanceManager;

string root = Path.Combine(Path.GetTempPath(), "maa-instance-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
const string OriginalConfig = """{"Current":"A","Configurations":{"A":{"TaskQueue":[{"CoreChar":"kept"}],"Gui":{"ConnectSettings":{"Address":"127.0.0.1:16384","AdbPath":"original-adb","AddressHistory":["127.0.0.1:16384"]}}}}}""";
int passed = 0;
try
{
    string package = Path.Combine(root, "update.zip");
    using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
    {
        var files = new Dictionary<string, string> {
            ["MAA.exe"] = "new exe", ["MaaCore.dll"] = "new core", ["MAA.Updater.exe"] = "updater",
            ["resource/new.txt"] = "new resource", ["config/gui.new.json"] = "PACKAGE DEFAULT - NEVER COPY",
            ["debug/run.zip"] = "PACKAGE DATA - NEVER COPY", ["reports/report.txt"] = "PACKAGE REPORT - NEVER COPY",
        };
        files["filelist.txt"] = string.Join('\n', files.Keys);
        foreach (var (name, text) in files) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(text); }
    }
    using var installer = InstancePackageUpdater.Prepare(package, Path.Combine(root, "cache"));
    string first = CreateInstance("first");
    installer.Apply(first);
    Check(Read(first, "MAA.exe") == "new exe" && Read(first, "resource/new.txt") == "new resource", "install runtime and resources");
    Check(!File.Exists(Path.Combine(first, "resource/obsolete.txt")), "remove obsolete manifest-owned file");
    Check(Read(first, "config/gui.new.json") == OriginalConfig && Read(first, "debug/run.zip") == "original run" && Read(first, "reports/report.txt") == "original report", "preserve config and reports byte for byte");
    Check(Read(first, "my-notes.txt") == "user notes", "preserve unowned user files");

    string failed = CreateInstance("failed");
    try { installer.Apply(failed, name => { if (name == "resource/new.txt") throw new IOException("injected write failure"); }); throw new Exception("failure not injected"); }
    catch (IOException) { }
    Check(Read(failed, "MAA.exe") == "old exe" && Read(failed, "MaaCore.dll") == "old core", "rollback after partial installation");
    Check(Read(failed, "resource/obsolete.txt") == "old resource" && !File.Exists(Path.Combine(failed, "resource/new.txt")), "rollback stale-file removal");
    Check(Read(failed, "config/gui.new.json") == OriginalConfig, "failed update preserves configuration");

    string locked = CreateInstance("locked");
    using (var held = File.Open(Path.Combine(locked, "MaaCore.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
    {
        try { installer.Apply(locked); throw new Exception("locked file accepted"); }
        catch (IOException) { Check(Read(locked, "MAA.exe") == "old exe", "locked runtime rolls back earlier moves"); }
    }

    InstanceConfiguration.ApplyCurrentPort(first, 16416);
    Check(InstanceConfiguration.TryReadCurrentPort(first, out int port) && port == 16416, "read and write current config port");
    var config = JsonNode.Parse(Read(first, "config/gui.new.json"))!;
    Check(config["Configurations"]!["A"]!["TaskQueue"]![0]!["CoreChar"]!.GetValue<string>() == "kept", "port edit preserves tasks");
    Check(config["Configurations"]!["A"]!["Gui"]!["ConnectSettings"]!["AdbPath"]!.GetValue<string>() == "original-adb", "port edit preserves adb executable");
    string clean = Path.Combine(root, "clean");
    Directory.CreateDirectory(Path.Combine(clean, "resource"));
    File.WriteAllText(Path.Combine(clean, "resource/blackflow-gui-defaults.json"), OriginalConfig);
    Check(InstanceConfiguration.ApplyCurrentPort(clean, 16448) && InstanceConfiguration.TryReadCurrentPort(clean, out port) && port == 16448, "new instance seeds defaults before assigning port");

    string traversal = Path.Combine(root, "traversal.zip");
    using (var zip = ZipFile.Open(traversal, ZipArchiveMode.Create)) zip.CreateEntry("../outside.txt");
    try { using var ignored = InstancePackageUpdater.Prepare(traversal, Path.Combine(root, "cache")); throw new Exception("traversal accepted"); }
    catch (InvalidDataException) { Check(!File.Exists(Path.Combine(root, "outside.txt")), "reject archive traversal"); }

    string mirrorZip = Path.Combine(root, "mirror.zip");
    using (var zip = ZipFile.Open(mirrorZip, ZipArchiveMode.Create))
    using (var writer = new StreamWriter(zip.CreateEntry("blackflow-update.json").Open()))
        writer.Write("""{"schema_version":1,"channel":"blackflow-data-collection","version":"v1.2.3"}""");
    byte[] mirrorBytes = File.ReadAllBytes(mirrorZip);
    var mirrorRelease = new BlackFlowReleaseClient.Release("v1.2.3", "MAA-BlackFlow-Data-Collection-v1.2.3-win-x64.zip", new Uri("https://img.lubiao.wiki/fixture"), mirrorBytes.Length,
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(mirrorBytes)));
    var requests = new List<Uri>();
    string downloaded = await BlackFlowReleaseClient.DownloadAsync(mirrorRelease, Path.Combine(root, "mirror-cache"), download: async (url, path) => {
        requests.Add(url);
        await File.WriteAllBytesAsync(path, url.Host == "img.lubiao.wiki" ? new byte[mirrorBytes.Length] : mirrorBytes);
    });
    Check(requests.Select(x => x.Host).SequenceEqual(new[] { "img.lubiao.wiki", "github.com" }) && File.ReadAllBytes(downloaded).SequenceEqual(mirrorBytes), "CDN corrupt download falls back to verified GitHub bytes");
    requests.Clear();
    await BlackFlowReleaseClient.DownloadAsync(mirrorRelease, Path.Combine(root, "mirror-cache"), download: (url, path) => throw new Exception("valid cache should be reused"));
    Check(true, "verified package cache avoids downloading again");
    string mirrorManifest = System.Text.Json.JsonSerializer.Serialize(new {
        schema_version = 1, channel = "blackflow-data-collection", version = mirrorRelease.Version,
        assets = new Dictionary<string, object> { ["win-x64"] = new { name = mirrorRelease.Name, url = mirrorRelease.Url.ToString(), size = mirrorRelease.Size, sha256 = mirrorRelease.Sha256 } },
    });
    await BlackFlowReleaseClient.CheckAsync(fetch: url => {
        requests.Add(url);
        return url.Host == "img.lubiao.wiki" ? Task.FromException<string>(new HttpRequestException("403")) : Task.FromResult(mirrorManifest);
    });
    Check(requests.Count == 2 && requests[1].Host == "github.com", "CDN check failure switches to collection GitHub feed");
    requests.Clear();
    try {
        await BlackFlowReleaseClient.CheckAsync("CDN", url => { requests.Add(url); throw new IOException("offline"); });
        throw new Exception("offline feed accepted");
    } catch (AggregateException) { Check(requests.Count == 1, "explicit source does not silently switch"); }

    if (args.Length == 1)
    {
        string realPackage = Path.GetFullPath(args[0]);
        using var archive = ZipFile.OpenRead(realPackage);
        using var identityStream = archive.GetEntry("blackflow-update.json")!.Open();
        var identity = JsonNode.Parse(identityStream)!;
        string version = identity["version"]!.GetValue<string>();
        BlackFlowReleaseClient.ValidateIdentity(realPackage, version);
        var release = BlackFlowReleaseClient.Parse(new JsonObject {
            ["schema_version"] = 1, ["channel"] = "blackflow-data-collection", ["version"] = version,
            ["assets"] = new JsonObject { ["win-x64"] = new JsonObject {
                ["name"] = Path.GetFileName(realPackage), ["url"] = "https://example.com/fixture.zip",
                ["size"] = new FileInfo(realPackage).Length,
                ["sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(realPackage))),
            } },
        }.ToJsonString());
        Check(await BlackFlowReleaseClient.VerifyAsync(realPackage, release), "real package integrity and channel identity");
        using var realInstaller = InstancePackageUpdater.Prepare(realPackage, Path.Combine(root, "real-cache"));
        string instance = CreateInstance("real-package");
        realInstaller.Apply(instance);
        Check(Read(instance, "config/gui.new.json") == OriginalConfig && Read(instance, "debug/run.zip") == "original run", "real full package preserves existing configuration and data");
        Check(BlackFlowReleaseClient.IsBlackFlowInstance(instance) && !BlackFlowReleaseClient.NeedsUpdate(instance, version), "updated instance joins channel and skips same version");
        Check(File.Exists(Path.Combine(instance, "resource/template/Roguelike/BlackFlow/BlackFlow@Roguelike@SacrificePicker.png")), "real package installs current recognition resources");
    }
    Console.WriteLine($"Passed {passed} instance update checks.");
}
finally { Directory.Delete(root, true); }

string CreateInstance(string name)
{
    string directory = Path.Combine(root, name);
    var files = new Dictionary<string, string> {
        ["MAA.exe"] = "old exe", ["MaaCore.dll"] = "old core", ["resource/obsolete.txt"] = "old resource",
        ["config/gui.new.json"] = OriginalConfig, ["debug/run.zip"] = "original run", ["reports/report.txt"] = "original report",
    };
    foreach (var (file, value) in files) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(directory, file))!); File.WriteAllText(Path.Combine(directory, file), value); }
    File.WriteAllText(Path.Combine(directory, "filelist.txt"), string.Join('\n', files.Keys));
    File.WriteAllText(Path.Combine(directory, "my-notes.txt"), "user notes");
    return directory;
}
string Read(string directory, string name) => File.ReadAllText(Path.Combine(directory, name));
void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
