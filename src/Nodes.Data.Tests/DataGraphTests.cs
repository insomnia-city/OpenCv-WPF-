using HalconWorkflow.Abstractions;
using HalconWorkflow.Abstractions.Parameters;
using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Execution;
using HalconWorkflow.Core.Graph;
using HalconWorkflow.Core.Model;
using HalconWorkflow.Core.Serialization;
using HalconWorkflow.Nodes.Data;
using Xunit;

namespace HalconWorkflow.Nodes.Data.Tests;

internal static class DataTestHelpers
{
    public static IPort Exec(INode node, PortDirection dir)
        => (dir == PortDirection.In ? node.Inputs : node.Outputs).First(p => p.Kind == PortKind.Exec);

    public static (GraphScheduler Scheduler, DataRuntime Runtime, InMemoryStore Store) BuildEnv(bool throwOnQuery = false)
    {
        var store = new InMemoryStore { ThrowOnQuery = throwOnQuery };
        var runtime = new DataRuntime();
        runtime.Add("mem", records: store, query: store);
        var scheduler = new GraphScheduler();
        scheduler.Services[typeof(IDataRuntime)] = runtime;
        return (scheduler, runtime, store);
    }
}

/// <summary>
/// Stage-8 gate (§8.5): DB nodes are first-class — data.write enqueues, data.query reads,
/// both resolve their store through IDataRuntime and honour cancellation. 
/// / 阶段 8 门(§8.5)：DB 节点是一等公民——data.write 入队、data.query 读取，二者经 IDataRuntime
///   解析存储并遵循取消。
/// </summary>
public class DataGraphTests
{
    [Fact]
    public void DataNodeFactory_ResolvesContracts()
    {
        var factory = new DataNodeFactory();
        Assert.NotNull(factory.Create(new NodeContract("data.write", 1), "w"));
        Assert.NotNull(factory.Create(new NodeContract("data.query", 1), "q"));
        Assert.Null(factory.Create(new NodeContract("data.trigger", 1), "t"));
        Assert.Null(factory.Create(new NodeContract("data.unknown", 1), "x"));
    }

    [Fact]
    public void DataNodes_ExposeParameterObjects()
    {
        Assert.IsAssignableFrom<IParameterized>(new DataWriteNode("w"));
        Assert.IsAssignableFrom<IParameterized>(new DataQueryNode("q"));
    }

    [Fact]
    public void DataNodes_ParametersReflectSource()
    {
        var write = new DataWriteNode("w");
        write.Params.Source = "prod";
        write.Params.Kind = "ok";
        var meta = ParameterReflection.Summarize(write.Params);
        Assert.Contains(meta, m => m.Name == "Source");
        Assert.Contains(meta, m => m.Name == "Kind");
        Assert.Contains(meta, m => m.Name == "ImageRef");
        Assert.Contains(meta, m => m.Name == "Dimensions");

        var query = new DataQueryNode("q");
        var qmeta = ParameterReflection.Summarize(query.Params);
        Assert.Contains(qmeta, m => m.Name == "Source");
        Assert.Contains(qmeta, m => m.Name == "Sql");
    }

    [Fact]
    public async Task Graph_DataWriteThenQuery_RoundTrips()
    {
        var (scheduler, runtime, store) = DataTestHelpers.BuildEnv();
        await using var _ = runtime;

        var graph = new GraphModel();
        var write = new DataWriteNode("w");
        write.Params.Source = "mem";
        write.Params.Kind = "ok";
        write.Params.Batch = "B7";
        write.Params.ResultJson = "{\"v\":1}";
        var query = new DataQueryNode("q");
        query.Params.Source = "mem";
        query.Params.Sql = "SELECT v FROM t";
        graph.AddNode(write);
        graph.AddNode(query);
        graph.Connect(DataTestHelpers.Exec(write, PortDirection.Out), DataTestHelpers.Exec(query, PortDirection.In));

        scheduler.Load(graph);
        var result = await scheduler.RunOnceAsync(CancellationToken.None);

        Assert.True(result.Success);
        var record = Assert.Single(store.Records);
        Assert.Equal("data.write:w", record.Node);
        Assert.Equal("ok", record.Kind);
        Assert.Equal("B7", record.Batch);
        Assert.Equal("{\"v\":1}", record.ResultJson);
        Assert.False(string.IsNullOrEmpty(record.TriggerId));
    }

    [Fact]
    public async Task Graph_DataWrite_PublishesGroupingDimensions()
    {
        var (scheduler, runtime, store) = DataTestHelpers.BuildEnv();
        await using var _ = runtime;

        var graph = new GraphModel();
        var write = new DataWriteNode("w");
        write.Params.Source = "mem";
        write.Params.Kind = "ok";
        write.Params.Dimensions = "line=L1; machine=M2 ; shift = night ;bogus";
        graph.AddNode(write);

        scheduler.Load(graph);
        var result = await scheduler.RunOnceAsync(CancellationToken.None);

        Assert.True(result.Success);
        var dimensions = Assert.Single(store.Records).Dimensions;
        Assert.NotNull(dimensions);
        Assert.Equal("L1", dimensions![DimensionKeys.Line]);
        Assert.Equal("M2", dimensions[DimensionKeys.Machine]);
        Assert.Equal("night", dimensions[DimensionKeys.Shift]);
        Assert.False(dimensions.ContainsKey("bogus"));
    }

    [Fact]
    public async Task Graph_DataQuery_CancelReportsFault()
    {
        var (scheduler, runtime, _) = DataTestHelpers.BuildEnv(throwOnQuery: true);
        await using var __ = runtime;

        var graph = new GraphModel();
        var query = new DataQueryNode("q");
        query.Params.Source = "mem";
        query.Params.Sql = "SELECT 1";
        graph.AddNode(query);

        scheduler.Load(graph);
        var result = await scheduler.RunOnceAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, result.FaultedNodes);
    }

    [Fact]
    public async Task Graph_DataWrite_MissingSourceFaults()
    {
        var runtime = new DataRuntime();
        await using var _ = runtime;
        var scheduler = new GraphScheduler();
        scheduler.Services[typeof(IDataRuntime)] = runtime;

        var graph = new GraphModel();
        var write = new DataWriteNode("w");
        write.Params.Source = "absent";
        graph.AddNode(write);

        scheduler.Load(graph);
        var result = await scheduler.RunOnceAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, result.FaultedNodes);
    }

    [Fact]
    public void GraphJson_DataRoundTrip_IsIdempotent()
    {
        var graph = new GraphModel();
        var query = new DataQueryNode("q");
        query.Params.Source = "prod";
        query.Params.Sql = "SELECT * FROM recipes";
        graph.AddNode(query);

        var json1 = GraphJsonSerializer.Serialize(graph);
        var graph2 = GraphJsonSerializer.Deserialize(json1, new DataNodeFactory());
        var json2 = GraphJsonSerializer.Serialize(graph2);

        Assert.Equal(json1, json2);
    }
}

/// <summary>A combined in-memory record + query store. · 合一的内存记录/查询存储</summary>
internal sealed class InMemoryStore : IRecordStore, IQueryStore
{
    public List<IRecord> Records { get; } = [];

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; set; } = [];

    public bool ThrowOnQuery { get; set; }

    public Task AppendAsync(IRecord record, CancellationToken ct)
    {
        Records.Add(record);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? p, CancellationToken ct)
        => throw new NotSupportedException();

    public Task<int> ExecuteAsync(string sql, object? p, CancellationToken ct) => Task.FromResult(0);

    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryRowsAsync(string sql, object? p, CancellationToken ct)
        => ThrowOnQuery
            ? Task.FromException<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(new OperationCanceledException(ct))
            : Task.FromResult(Rows);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
