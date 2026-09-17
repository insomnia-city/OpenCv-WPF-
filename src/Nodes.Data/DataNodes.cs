using System.Text.Json;
using HalconWorkflow.Abstractions;
using HalconWorkflow.Abstractions.Parameters;
using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Model;
using HalconWorkflow.Core.Types;
using HalconWorkflow.Nodes.Data.Nodes;

namespace HalconWorkflow.Nodes.Data;

/// <summary>
/// Strongly-typed parameter objects for data nodes (§8.5). · 数据节点的强类型参数对象(§8.5)
/// </summary>
public sealed class DataWriteParameters
{
    [NodeParameter("Source", "Data", description: "Registered data source name · 已登记数据源名称")]
    public string Source { get; set; } = "";

    [NodeParameter("Kind", "Data", description: "ok / ng / measure / trace · 结果类别")]
    public string Kind { get; set; } = "measure";

    [NodeParameter("Batch", "Data", description: "Batch number; empty falls back to the cycle token · 批次号；空则用周期令牌")]
    public string Batch { get; set; } = "";

    [NodeParameter("ResultJson", "Value", description: "Static result snapshot when the 'Result' input is unconnected · 'Result' 端口未接时的静态结果快照")]
    public string ResultJson { get; set; } = "";

    [NodeParameter("ImageRef", "Value", description: "Snapshot file relative path (images never go into BLOB) · 快照文件相对路径(图像不进 BLOB)")]
    public string ImageRef { get; set; } = "";

    [NodeParameter("Dimensions", "Data", description: "Grouping dimensions k=v; k=v (line/machine/shift/model/recipe) · 分组维度 k=v; k=v")]
    public string Dimensions { get; set; } = "";
}

/// <summary>
/// data.write:1 — Enqueues a traceability record; returns as soon as it is queued (§8.3).
/// / data.write:1 — 入队一条追溯记录；入队即完成(§8.3)
/// </summary>
internal sealed class DataWriteNode(string id)
    : DataNodeBase(id, new NodeContract("data.write", 1), execIn: true, execOut: true,
        ("Result", ResultDescriptor.Instance, true)), IParameterized
{
    public DataWriteParameters Params { get; } = new();
    object IParameterized.ParameterObject => Params;

    protected override async Task RunAsync(IExecutionContext ctx, CancellationToken ct)
    {
        var runtime = ctx.GetService<IDataRuntime>();
        var store = runtime.ResolveRecordStore(Params.Source)
            ?? throw new InvalidOperationException($"data source '{Params.Source}' has no record store");
        var value = ctx.GetData("Result");
        var resultJson = value is null ? NullIfEmpty(Params.ResultJson) : Serialize(value);
        var record = new TraceRecord(
            ctx.Current.TriggerId,
            $"{Contract.Namespace}:{Id}",
            NullIfEmpty(Params.Kind) ?? "trace",
            NullIfEmpty(Params.Batch) ?? ctx.Current.Batch,
            resultJson,
            NullIfEmpty(Params.ImageRef))
        {
            Dimensions = ParseDimensions(Params.Dimensions)
        };
        await store.AppendAsync(record, ct).ConfigureAwait(false);
    }

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private static IReadOnlyDictionary<string, string?>? ParseDimensions(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) continue;
            var key = part[..separator].Trim();
            if (key.Length == 0) continue;
            map[key] = part[(separator + 1)..].Trim();
        }
        return map.Count == 0 ? null : map;
    }

    private static string Serialize(object value) => value is string s ? s : JsonSerializer.Serialize(value);
}

public sealed class DataQueryParameters
{
    [NodeParameter("Source", "Data", description: "Registered data source name · 已登记数据源名称")]
    public string Source { get; set; } = "";

    [NodeParameter("Sql", "Data", description: "Parameterized SELECT (recipe / reference / feed values) · 参数化 SELECT(配方/参考/上料值)")]
    public string Sql { get; set; } = "";

    [NodeParameter("TimeoutMs", "Data", min: 1, max: 120000, unit: "ms", description: "Query timeout · 查询超时")]
    public int TimeoutMs { get; set; } = 5000;
}

/// <summary>
/// data.query:1 — Reads reference/recipe values asynchronously and cancelably (§8.5).
/// Publishes the first scalar to "Result" and all rows (JSON) to "Rows".
/// / data.query:1 — 异步可取消读取参考/配方值(§8.5)。首标量发布到 "Result"，全部行(JSON)发布到 "Rows"。
/// </summary>
internal sealed class DataQueryNode : DataNodeBase, IParameterized
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public DataQueryNode(string id)
        : base(id, new NodeContract("data.query", 1), execIn: true, execOut: true)
    {
        AddDataOut("Result", ResultDescriptor.Instance);
        AddDataOut("Rows", ResultDescriptor.Instance);
    }

    public DataQueryParameters Params { get; } = new();
    object IParameterized.ParameterObject => Params;

    protected override async Task RunAsync(IExecutionContext ctx, CancellationToken ct)
    {
        var runtime = ctx.GetService<IDataRuntime>();
        var store = runtime.ResolveQueryStore(Params.Source)
            ?? throw new InvalidOperationException($"data source '{Params.Source}' has no query store");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(Params.TimeoutMs));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var rows = await store.QueryRowsAsync(Params.Sql, null, linked.Token).ConfigureAwait(false);
        ctx.SetData("Result", rows.Count == 0 ? null : rows[0].Values.FirstOrDefault());
        ctx.SetData("Rows", JsonSerializer.Serialize(rows, JsonOptions));
    }
}

/// <summary>Creates data nodes from contracts: data.write / data.query. · 根据契约创建数据节点</summary>
public sealed class DataNodeFactory : Core.Serialization.INodeFactory
{
    /// <inheritdoc />
    public INode? Create(NodeContract contract, string id) => contract switch
    {
        { Namespace: "data.write", Version: 1 } => new DataWriteNode(id),
        { Namespace: "data.query", Version: 1 } => new DataQueryNode(id),
        _ => null,
    };
}
