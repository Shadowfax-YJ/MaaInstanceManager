using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MaaInstanceManager;

internal static class InstanceConfiguration
{
    public static bool TryReadCurrentPort(string directory, out int port)
    {
        port = 0;
        string path = Path.Combine(directory, "config", "gui.new.json");
        if (!File.Exists(path))
        {
            return false;
        }

        var root = JsonNode.Parse(File.ReadAllText(path));
        string current = root?["Current"]?.GetValue<string>() ?? "Default";
        string? address = root?["Configurations"]?[current]?["Gui"]?["ConnectSettings"]?["Address"]?.GetValue<string>();
        string? value = address?.Split(':').Last();
        return int.TryParse(value, out port) && port is > 0 and <= 65535;
    }

    public static bool ApplyCurrentPort(string directory, int port)
    {
        string config = Path.Combine(directory, "config", "gui.new.json");
        string defaults = Path.Combine(directory, "resource", "blackflow-gui-defaults.json");
        if (!File.Exists(config) && File.Exists(Path.Combine(directory, "config", "gui.json")))
        {
            // Let MAA migrate the existing legacy configuration on its next launch.
            return false;
        }
        string source = File.Exists(config) ? config : defaults;
        if (!File.Exists(source))
        {
            return false;
        }

        var root = JsonNode.Parse(File.ReadAllText(source)) as JsonObject ?? throw new InvalidDataException("实例配置格式无效");
        string current = root["Current"]?.GetValue<string>() ?? "Default";
        var configurations = Object(root, "Configurations");
        var profile = Object(configurations, current);
        var settings = Object(Object(profile, "Gui"), "ConnectSettings");
        string address = $"127.0.0.1:{port}";
        settings["Address"] = address;
        var history = settings["AddressHistory"] as JsonArray;
        if (history == null)
        {
            history = [];
            settings["AddressHistory"] = history;
        }
        if (!history.Any(node => node?.GetValue<string>() == address))
        {
            history.Insert(0, address);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        string temporary = config + ".tmp";
        File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, config, overwrite: true);
        return true;
    }

    private static JsonObject Object(JsonObject parent, string key)
    {
        if (parent[key] is not JsonObject result)
        {
            result = [];
            parent[key] = result;
        }

        return result;
    }
}
