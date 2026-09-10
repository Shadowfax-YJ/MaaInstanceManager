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
