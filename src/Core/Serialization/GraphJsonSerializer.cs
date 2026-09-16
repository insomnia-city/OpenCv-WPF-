using System.Text.Json;
using System.Text.Json.Nodes;
using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Graph;
using HalconWorkflow.Core.Model;

namespace HalconWorkflow.Core.Serialization;

/// <summary>
/// Persists graphs as contract-name based JSON (§5.8). Nodes reference contracts, never CLR type names. 
/// 以契约名 JSON 持久化图(§5.8)；节点引用契约，绝不使用类名
/// </summary>
public static class GraphJsonSerializer
{
    /// <summary>
    /// Schema marker for the graph file. /* 图文件的 schema 标识 */
    /// </summary>
    public const string Schema = "vision.workflow/graph";
    public const int SchemaVersion = 1;

    /// <summary>
    /// Static JSON options shared across read/write. /* 读写共享的静态 JSON 选项 */
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>
    /// Serializes the graph topology (structure only, no recipe params). · 序列化图拓扑(仅结构，不含配方参数)
    /// </summary>
    public static string Serialize(GraphModel graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var root = new JsonObject
        {
            ["schema"] = Schema,
            ["schemaVersion"] = SchemaVersion,
            ["trigger"] = new JsonObject
            {
                ["source"] = graph.Trigger.Source.ToString(),
                ["tag"] = graph.Trigger.Tag,
                ["debounceMs"] = graph.Trigger.DebounceMs,
                ["queueLimit"] = graph.Trigger.QueueLimit
            },
            ["nodes"] = new JsonArray(),
            ["links"] = new JsonArray()
        };

        var nodes = (JsonArray)root["nodes"]!;
        foreach (var gn in graph.Nodes.Values)
        {
            nodes.Add(new JsonObject
            {
                ["id"] = gn.Id,
                ["contract"] = new JsonObject
                {
                    ["ns"] = gn.Contract.Namespace,
                    ["version"] = gn.Contract.Version
                },
                ["pos"] = new JsonObject
                {
                    ["x"] = gn.Position.X,
                    ["y"] = gn.Position.Y
                },
                ["recipeId"] = gn.RecipeId,
                ["suspended"] = gn.IsSuspended
            });
        }

        var links = (JsonArray)root["links"]!;
        foreach (var l in graph.Links)
            links.Add(SerializeLink(l));

        return root.ToJsonString(Options);
    }

    /// <summary>
    /// Deserializes a graph. Unknown contracts produce suspended nodes unless throwOnUnknown is set. 
    /// 反序列化图；未知契约为挂起节点(除非 throwOnUnknown=true)
    /// </summary>
    public static GraphModel Deserialize(string json, INodeFactory factory, bool throwOnUnknown = false)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(factory);

        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        }) ?? throw new FormatException("Graph JSON is empty.");

        var schema = (string?)root["schema"];
        if (schema != Schema)
            throw new FormatException($"Unsupported schema '{schema}'. Expected '{Schema}'.");

        var graph = new GraphModel();
        var nodesByName = new Dictionary<string, GraphNode>(StringComparer.Ordinal);

        if (root["trigger"] is JsonObject tg)
        {
            graph.Trigger = new TriggerConfig
            {
                Source = Enum.TryParse<TriggerSource>((string?)tg["source"], out var s) ? s : TriggerSource.Manual,
                Tag = (string?)tg["tag"],
                DebounceMs = (int?)tg["debounceMs"] ?? 10,
                QueueLimit = (int?)tg["queueLimit"] ?? 1
            };
        }

        if (root["nodes"] is JsonArray nodeArr)
        {
            foreach (var item in nodeArr)
            {
                if (item is not JsonObject jo) continue;
                var id = (string?)jo["id"] ?? throw new FormatException("Node is missing 'id'.");
                var contractNs = (string?)jo["contract"]?["ns"]
                    ?? throw new FormatException($"Node '{id}' is missing contract.ns.");
                var version = (int?)jo["contract"]?["version"] ?? 1;
                var contract = new NodeContract(contractNs, version);

                var pos = jo["pos"] as JsonObject;
                var x = (double?)pos?["x"] ?? 0;
                var y = (double?)pos?["y"] ?? 0;

                var node = factory.Create(contract, id);
                if (node is null)
                {
                    if (throwOnUnknown)
                        throw new ContractNotFoundException(contract);
                    // Suspended node: create a placeholder that fails on ExecuteAsync. · 挂起节点：创建执行时失败的占位符
                    node = new SuspendedNode(id, contract,
                        $"Plugin for contract '{contract}' is not loaded.");
                }

                graph.AddNode(node);
                nodesByName[id] = graph.Nodes[id];
                nodesByName[id].Position = (x, y);
                nodesByName[id].RecipeId = (string?)jo["recipeId"];
                nodesByName[id].IsSuspended = node is SuspendedNode || (bool?)jo["suspended"] == true;
            }
        }

        if (root["links"] is JsonArray linkArr)
        {
            foreach (var item in linkArr)
            {
                if (item is not JsonObject jo) continue;
                var fromNode = (string?)jo["from"]?["node"];
                var fromPort = (string?)jo["from"]?["port"];
                var toNode = (string?)jo["to"]?["node"];
                var toPort = (string?)jo["to"]?["port"];
                if (fromNode is null || fromPort is null || toNode is null || toPort is null) continue;

                if (!nodesByName.TryGetValue(fromNode, out var gnf)
                    || !nodesByName.TryGetValue(toNode, out var gnt)) continue;

                var from = ResolvePort(gnf, fromPort);
                var to = ResolvePort(gnt, toPort);
                if (from is null || to is null) continue;

                graph.Connect(from, to);
            }
        }

        return graph;
    }

    /// <summary>
    /// Serializes a single link in schema shape. · 以 schema 形状序列化一条连线
    /// </summary>
    private static JsonObject SerializeLink(GraphLink l)
    {
        var from = l.From;
        var fromOwner = from.Owner;
        var to = l.To;
        var toOwner = to.Owner;
        return new JsonObject
        {
            ["from"] = new JsonObject
            {
                ["node"] = fromOwner.Id,
                ["port"] = $"{(from.Direction == PortDirection.Out ? "out" : "in")}:{from.Name}"
            },
            ["to"] = new JsonObject
            {
                ["node"] = toOwner.Id,
                ["port"] = $"{(to.Direction == PortDirection.Out ? "out" : "in")}:{to.Name}"
            }
        };
    }

    private static IPort? ResolvePort(GraphNode gn, string qualifier)
    {
        var isOut = qualifier.StartsWith("out:", StringComparison.Ordinal);
        var name = qualifier[(qualifier.IndexOf(':') + 1)..];
        try
        {
            return isOut ? gn.GetOutput(name) : gn.GetInput(name);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }
}

/// <summary>
/// Placeholder node for missing contracts; throws at execution time. 
/// 缺失契约的占位节点;执行时抛异常
/// </summary>
public sealed class SuspendedNode : INode
{
    private readonly string _reason;

    public SuspendedNode(string id, NodeContract contract, string reason)
    {
        Id = id;
        Contract = contract;
        _reason = reason;
        Inputs = [new SuspendedPort(this, "exec", PortDirection.In, PortKind.Exec)];
        Outputs = [new SuspendedPort(this, "exec", PortDirection.Out, PortKind.Exec)];
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public NodeContract Contract { get; }

    /// <inheritdoc />
    public IReadOnlyList<IPort> Inputs { get; }

    /// <inheritdoc />
    public IReadOnlyList<IPort> Outputs { get; }

    /// <inheritdoc />
    public NodeState State => NodeState.Faulted;

    /// <inheritdoc />
    public Task ExecuteAsync(IExecutionContext ctx, CancellationToken ct)
        => throw new InvalidOperationException($"Suspended node '{Id}' ({Contract}): {_reason}");

    private sealed class SuspendedPort(INode owner, string name, PortDirection dir, PortKind kind) : IPort
    {
        public INode Owner { get; } = owner;
        public string Name { get; } = name;
        public PortDirection Direction { get; } = dir;
        public PortKind Kind { get; } = kind;
        public ITypeDescriptor? Type => null;
        public bool IsConnected { get; set; }
        public object? Value { get; set; }
    }
}