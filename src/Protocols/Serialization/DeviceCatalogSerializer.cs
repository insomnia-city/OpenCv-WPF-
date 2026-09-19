using System.Text.Json;
using System.Text.Json.Nodes;
using HalconWorkflow.Protocols.Devices;

namespace HalconWorkflow.Protocols.Serialization;

/// <summary>
/// Persists the device catalog (device + tag definitions) as contract-name JSON, tolerant to
/// unknown/missing fields (§7.1 device catalog, stage-21). Round-trip is idempotent.
/// · 以 JSON 持久化设备目录(设备 + Tag 定义),容忍未知/缺失字段(§7.1 设备目录,阶段21)。往返幂等。
/// </summary>
public static class DeviceCatalogSerializer
{
    public const string Schema = "halcon/device-catalog";
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>
    /// Serializes the catalog. Missing optional values fall back to storage defaults.
    /// · 序列化目录。缺失的可选值回落到存储默认值。
    /// </summary>
    public static string Serialize(DeviceCatalogFile catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var root = new JsonObject
        {
            ["schema"] = Schema,
            ["schemaVersion"] = SchemaVersion,
            ["version"] = catalog.Version,
            ["devices"] = new JsonArray(
                catalog.Devices.Select(d =>
                {
                    var tagArray = new JsonArray(
                        d.Tags.Select(t => new JsonObject
                        {
                            ["tag"] = t.Tag,
                            ["address"] = t.Address,
                            ["dataType"] = t.DataType,
                            ["readable"] = t.Readable,
                            ["writable"] = t.Writable,
                            ["scale"] = t.Scale
                        }).ToArray());
                    return new JsonObject
                    {
                        ["deviceId"] = d.DeviceId,
                        ["protocol"] = d.Protocol,
                        ["host"] = d.Host,
                        ["port"] = d.Port,
                        ["unitId"] = d.UnitId,
                        ["pollIntervalMs"] = d.PollIntervalMs,
                        ["heartbeatMs"] = d.HeartbeatMs,
                        ["loopback"] = d.Loopback,
                        ["tags"] = tagArray
                    };
                }).ToArray())
        };
        return root.ToJsonString(Options);
    }

    /// <summary>Parses a catalog text; throws on malformed JSON. · 解析目录文本;畸形 JSON 抛异常</summary>
    public static DeviceCatalogFile Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        var root = JsonNode.Parse(json, documentOptions: new()
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        }) ?? throw new FormatException("Empty device catalog document.");
        return Read(root);
    }

    /// <summary>Tolerant parse: null/false on malformed or incompatible schema. · 容错解析:畸形或不匹配 schema 返回 null</summary>
    public static DeviceCatalogFile? TryDeserialize(string json)
    {
        try
        {
            var root = JsonNode.Parse(json, documentOptions: new()
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            if (root is null) return null;
            if ((string?)root["schema"] != Schema) return null;
            return Read(root);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static DeviceCatalogFile Read(JsonNode root)
    {
        var file = new DeviceCatalogFile();
        if (int.TryParse(root["version"]?.ToString(), out var v)) file.Version = v;
        if (root["devices"] is JsonArray devices)
        {
            foreach (var dn in devices.OfType<JsonObject>())
            {
                var d = new DeviceProfile
                {
                    DeviceId = (string?)dn["deviceId"] ?? "",
                    Protocol = (string?)dn["protocol"] ?? "",
                    Host = (string?)dn["host"] ?? "127.0.0.1",
                    Port = ParseInt(dn["port"], 502),
                    UnitId = (byte)ParseInt(dn["unitId"], 1),
                    PollIntervalMs = ParseInt(dn["pollIntervalMs"], 200),
                    HeartbeatMs = ParseInt(dn["heartbeatMs"], 0),
                    Loopback = dn["loopback"]?.GetValue<bool>() ?? false
                };
                if (dn["tags"] is JsonArray tags)
                {
                    foreach (var tn in tags.OfType<JsonObject>())
                    {
                        d.Tags.Add(new TagProfile
                        {
                            Tag = (string?)tn["tag"] ?? "",
                            Address = (string?)tn["address"] ?? "",
                            DataType = (string?)tn["dataType"] ?? "ushort",
                            Readable = tn["readable"]?.GetValue<bool>() ?? true,
                            Writable = tn["writable"]?.GetValue<bool>() ?? false,
                            Scale = tn["scale"]?.ToString()
                        });
                    }
                }
                file.Devices.Add(d);
            }
        }
        return file;
    }

    private static int ParseInt(JsonNode? node, int fallback) =>
        int.TryParse(node?.ToString(), out var v) ? v : fallback;
}