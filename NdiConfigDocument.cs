using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tractus.Ndi.ConfigTui;

internal enum NdiDirection
{
    Receive,
    Send
}

internal sealed class NdiConfigDocument
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    private readonly JsonObject _root;

    private NdiConfigDocument(string path, JsonObject root, bool createdNew)
    {
        ConfigPath = path;
        _root = root;
        CreatedNew = createdNew;
        _ = Ndi;
    }

    public string ConfigPath { get; }

    public bool CreatedNew { get; }

    public static NdiConfigDocument Load(string path)
    {
        if (!File.Exists(path))
        {
            return new NdiConfigDocument(path, new JsonObject { ["ndi"] = new JsonObject() }, createdNew: true);
        }

        var json = File.ReadAllText(path);
        var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        if (node is not JsonObject root)
        {
            throw new InvalidDataException("The NDI config file must contain a JSON object at the root.");
        }

        if (root["ndi"] is not JsonObject)
        {
            root["ndi"] = new JsonObject();
        }

        return new NdiConfigDocument(path, root, createdNew: false);
    }

    public IReadOnlyList<string> GetGroups(NdiDirection direction)
    {
        var groups = SplitCommaList(GetString("groups", DirectionKey(direction)));
        return groups.Count == 0 ? ["Public"] : groups;
    }

    public void SetGroups(NdiDirection direction, IReadOnlyList<string> groups) =>
        SetString(JoinCommaList(groups), "groups", DirectionKey(direction));

    public IReadOnlyList<string> GetExternalSourceIps() =>
        SplitCommaList(GetString("networks", "ips"));

    public void SetExternalSourceIps(IReadOnlyList<string> ips) =>
        SetString(JoinCommaList(ips), "networks", "ips");

    public IReadOnlyList<string> GetDiscoveryServers() =>
        SplitCommaList(GetString("networks", "discovery"));

    public void SetDiscoveryServers(IReadOnlyList<string> servers)
    {
        if (servers.Count == 0)
        {
            RemovePath("networks", "discovery");
            return;
        }

        SetString(JoinCommaList(servers), "networks", "discovery");
    }

    public IReadOnlyList<string> GetAllowedAdapters()
    {
        var node = GetNode("adapters", "allowed");
        if (node is JsonArray array)
        {
            return array
                .Select(item => item?.GetValue<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return SplitCommaList(GetString("adapters", "allowed"));
    }

    public void SetAllowedAdapters(IReadOnlyList<string> adapters)
    {
        if (adapters.Count == 0)
        {
            RemovePath("adapters", "allowed");
            return;
        }

        var array = new JsonArray();
        foreach (var adapter in adapters)
        {
            array.Add((JsonNode?)JsonValue.Create(adapter));
        }

        SetNode(array, "adapters", "allowed");
    }

    public string? MachineName
    {
        get => GetString("machinename");
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                RemovePath("machinename");
            }
            else
            {
                SetString(value.Trim(), "machinename");
            }
        }
    }

    public string? SourceFilterRegex
    {
        get => GetString("sourcefilter", "regex");
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                RemovePath("sourcefilter", "regex");
            }
            else
            {
                SetString(value.Trim(), "sourcefilter", "regex");
            }
        }
    }

    public bool AllowReliableUdpReceive
    {
        get => GetBool(defaultValue: false, "rudp", "recv", "enable");
        set => SetBool(value, "rudp", "recv", "enable");
    }

    public bool AllowUdpReceive
    {
        get => GetBool(defaultValue: false, "unicast", "recv", "enable");
        set => SetBool(value, "unicast", "recv", "enable");
    }

    public bool AllowMultiTcpReceive
    {
        get => GetBool(defaultValue: false, "tcp", "recv", "enable");
        set => SetBool(value, "tcp", "recv", "enable");
    }

    public bool MulticastSendEnabled
    {
        get => GetBool(defaultValue: false, "multicast", "send", "enable");
        set => SetBool(value, "multicast", "send", "enable");
    }

    public string MulticastSendPrefix
    {
        get => GetString("multicast", "send", "netprefix") ?? "239.255.0.0";
        set => SetString(value.Trim(), "multicast", "send", "netprefix");
    }

    public string MulticastSendMask
    {
        get => GetString("multicast", "send", "netmask") ?? "255.255.0.0";
        set => SetString(value.Trim(), "multicast", "send", "netmask");
    }

    public int MulticastTtl
    {
        get => GetInt(defaultValue: 1, "multicast", "send", "ttl");
        set => SetInt(value, "multicast", "send", "ttl");
    }

    public bool MulticastReceiveEnabled
    {
        get => GetBool(defaultValue: false, "multicast", "recv", "enable");
        set => SetBool(value, "multicast", "recv", "enable");
    }

    public string MulticastReceiveSubnets
    {
        get => GetString("multicast", "recv", "subnets") ?? string.Empty;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                RemovePath("multicast", "recv", "subnets");
            }
            else
            {
                SetString(value.Trim(), "multicast", "recv", "subnets");
            }
        }
    }

    public string? VendorName
    {
        get => GetString("vendor", "name");
        set => SetOptionalString(value, "vendor", "name");
    }

    public string? VendorId
    {
        get => GetString("vendor", "id");
        set => SetOptionalString(value, "vendor", "id");
    }

    public int? CodecShqQuality
    {
        get => GetNullableInt("codec", "shq", "quality");
        set
        {
            if (value is null)
            {
                RemovePath("codec", "shq", "quality");
            }
            else
            {
                SetInt(value.Value, "codec", "shq", "quality");
            }
        }
    }

    public string? CodecShqMode
    {
        get => GetString("codec", "shq", "mode");
        set => SetOptionalString(value, "codec", "shq", "mode");
    }

    public void ApplyReceiveMode(ReceiveMode mode)
    {
        AllowReliableUdpReceive = mode == ReceiveMode.ReliableUdp;
        AllowUdpReceive = mode == ReceiveMode.Udp;
        AllowMultiTcpReceive = mode == ReceiveMode.MultiTcp;
    }

    public ReceiveMode GetReceiveMode()
    {
        var enabled = new[]
        {
            AllowReliableUdpReceive,
            AllowUdpReceive,
            AllowMultiTcpReceive
        }.Count(value => value);

        if (enabled > 1)
        {
            return ReceiveMode.Custom;
        }

        if (AllowReliableUdpReceive)
        {
            return ReceiveMode.ReliableUdp;
        }

        if (AllowUdpReceive)
        {
            return ReceiveMode.Udp;
        }

        if (AllowMultiTcpReceive)
        {
            return ReceiveMode.MultiTcp;
        }

        return ReceiveMode.SingleTcp;
    }

    public void Save(bool createBackup)
    {
        var directory = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (createBackup && File.Exists(ConfigPath))
        {
            File.Copy(ConfigPath, ConfigPath + ".bak", overwrite: true);
        }

        var tempPath = ConfigPath + ".tmp";
        File.WriteAllText(tempPath, _root.ToJsonString(WriteOptions) + Environment.NewLine);
        File.Move(tempPath, ConfigPath, overwrite: true);
    }

    private JsonObject Ndi => EnsureObject(_root, "ndi");

    private static string DirectionKey(NdiDirection direction) =>
        direction == NdiDirection.Receive ? "recv" : "send";

    private string? GetString(params string[] path)
    {
        var node = GetNode(path);
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            return node.ToJsonString();
        }
    }

    private bool GetBool(bool defaultValue, params string[] path)
    {
        var node = GetNode(path);
        if (node is null)
        {
            return defaultValue;
        }

        try
        {
            return node.GetValue<bool>();
        }
        catch (InvalidOperationException)
        {
            var text = GetString(path);
            return bool.TryParse(text, out var parsed) ? parsed : defaultValue;
        }
    }

    private int GetInt(int defaultValue, params string[] path)
    {
        var node = GetNode(path);
        if (node is null)
        {
            return defaultValue;
        }

        try
        {
            return node.GetValue<int>();
        }
        catch (InvalidOperationException)
        {
            var text = GetString(path);
            return int.TryParse(text, out var parsed) ? parsed : defaultValue;
        }
    }

    private int? GetNullableInt(params string[] path)
    {
        var node = GetNode(path);
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<int>();
        }
        catch (InvalidOperationException)
        {
            var text = GetString(path);
            return int.TryParse(text, out var parsed) ? parsed : null;
        }
    }

    private JsonNode? GetNode(params string[] path)
    {
        JsonNode? current = Ndi;
        foreach (var part in path)
        {
            if (current is not JsonObject currentObject)
            {
                return null;
            }

            current = currentObject[part];
        }

        return current;
    }

    private void SetString(string value, params string[] path) =>
        SetNode(JsonValue.Create(value), path);

    private void SetOptionalString(string? value, params string[] path)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            RemovePath(path);
        }
        else
        {
            SetString(value.Trim(), path);
        }
    }

    private void SetBool(bool value, params string[] path) =>
        SetNode(JsonValue.Create(value), path);

    private void SetInt(int value, params string[] path) =>
        SetNode(JsonValue.Create(value), path);

    private void SetNode(JsonNode? value, params string[] path)
    {
        if (path.Length == 0)
        {
            throw new ArgumentException("Path cannot be empty.", nameof(path));
        }

        var parent = EnsureParent(path);
        parent[path[^1]] = value;
    }

    private void RemovePath(params string[] path)
    {
        if (path.Length == 0)
        {
            return;
        }

        var parent = GetParent(path);
        parent?.Remove(path[^1]);
    }

    private JsonObject EnsureParent(string[] path)
    {
        var current = Ndi;
        for (var i = 0; i < path.Length - 1; i++)
        {
            current = EnsureObject(current, path[i]);
        }

        return current;
    }

    private JsonObject? GetParent(string[] path)
    {
        JsonNode? current = Ndi;
        for (var i = 0; i < path.Length - 1; i++)
        {
            if (current is not JsonObject currentObject)
            {
                return null;
            }

            current = currentObject[path[i]];
        }

        return current as JsonObject;
    }

    private static JsonObject EnsureObject(JsonObject parent, string property)
    {
        if (parent[property] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        parent[property] = created;
        return created;
    }

    private static List<string> SplitCommaList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string JoinCommaList(IEnumerable<string> values) =>
        string.Join(',', values.Select(value => value.Trim()).Where(value => value.Length > 0));
}
