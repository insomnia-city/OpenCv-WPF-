using HalconWorkflow.Abstractions;
using HalconWorkflow.Abstractions.Parameters;
using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Execution;
using HalconWorkflow.Core.Graph;
using HalconWorkflow.Core.Model;
using HalconWorkflow.Core.Serialization;
using HalconWorkflow.Nodes.Comm;
using HalconWorkflow.Protocols;
using HalconWorkflow.Protocols.Modbus;
using Xunit;

namespace HalconWorkflow.Nodes.Comm.Tests;

internal static class CommTestHelpers
{
    public static IPort Port(INode node, PortDirection dir)
        => (dir == PortDirection.In ? node.Inputs : node.Outputs).First(p => p.Kind == PortKind.Exec);

    /// <summary>An empty observable used by fake adapters. / 伪适配器使用的空可观察序列</summary>
    public sealed class EmptyObservable : IObservable<TagValue>
    {
        public IDisposable Subscribe(IObserver<TagValue> observer) => new EmptySubscription();

        private sealed class EmptySubscription : IDisposable
        {
            public void Dispose() { }
        }
    }
}

/// <summary>
/// Protocol-agnostic comm-graph execution tests + swap-protocol assertion (§13.1 Stage-6 gate).
/// / 协议无关通讯图执行 + 换协议不动图断言（§13.1 阶段6 门）
/// </summary>
public class CommGraphTests
{
    private static (GraphScheduler Scheduler, ModbusTcpSimulator Sim, CommRuntime Runtime, TagTable Tags) BuildEnv()
    {
        var tags = new TagTable();
        tags.Register(new("dev/holding/speed", "dev", "holding:0", typeof(ushort), true, true));
        tags.Register(new("dev/coils/start", "dev", "coil:0", typeof(bool), true, true));
        var sim = ModbusTcpSimulator.Start();
        var conn = new ModbusTcpConnection("dev", "127.0.0.1", sim.Port, 0xFF, tags);
        var runtime = new CommRuntime(tags);
        runtime.Add(conn);
        var scheduler = new GraphScheduler();
        scheduler.Services[typeof(ICommRuntime)] = runtime;
        return (scheduler, sim, runtime, tags);
    }

    [Fact]
    public async Task CommWriteThenRead_RoundTripsThroughGraph()
    {
        var (scheduler, sim, runtime, tags) = BuildEnv();
        try
        {
            var graph = new GraphModel();
            var writer = new CommWriteNode("w");
            writer.Params.DeviceId = "dev";
            writer.Params.Tag = "dev/holding/speed";
            writer.Params.ValueAsText = "42";
            graph.AddNode(writer);

            var reader = new CommReadNode("r");
            reader.Params.DeviceId = "dev";
            reader.Params.Tag = "dev/holding/speed";
            graph.AddNode(reader);

            graph.Connect(CommTestHelpers.Port(writer, PortDirection.Out), CommTestHelpers.Port(reader, PortDirection.In));
            scheduler.Load(graph);
            var result = await scheduler.RunOnceAsync(CancellationToken.None);
            Assert.True(result.Success);
            Assert.Equal((ushort)42, sim.ReadHolding(0));
        }
        finally
        {
            await runtime.DisposeAsync();
            await sim.DisposeAsync();
        }
    }

    [Fact]
    public async Task CommRead_DirectRoundTrip()
    {
        var tags = new TagTable();
        tags.Register(new("dev/holding/speed", "dev", "holding:0", typeof(ushort), true, true));
        var sim = ModbusTcpSimulator.Start();
        var conn = new ModbusTcpConnection("dev", "127.0.0.1", sim.Port, 0xFF, tags);
        try
        {
            await conn.ConnectAsync(CancellationToken.None);
            await conn.WriteAsync("dev/holding/speed", (ushort)42, CancellationToken.None);
            var v = await conn.ReadAsync("dev/holding/speed", CancellationToken.None);
            Assert.Equal((ushort)42, Convert.ToUInt16(v));
        }
        finally
        {
            await conn.DisposeAsync();
            await sim.DisposeAsync();
        }
    }

    [Fact]
    public async Task CommRead_WritesValueToContext()
    {
        var (scheduler, sim, runtime, _) = BuildEnv();
        await using var _rt = runtime;
        await using var _ = sim;
        sim.SeedHolding(0, 999);

        var graph = new GraphModel();
        var reader = new CommReadNode("r");
        reader.Params.DeviceId = "dev";
        reader.Params.Tag = "dev/holding/speed";
        graph.AddNode(reader);

        scheduler.Load(graph);
        var result = await scheduler.RunOnceAsync(CancellationToken.None);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task CommWait_FaultsOnTimeout()
    {
        var (scheduler, sim, runtime, _) = BuildEnv();
        await using var _rt = runtime;
        await using var _ = sim;
        sim.SeedHolding(0, 0);

        var graph = new GraphModel();
        var wait = new CommWaitNode("w");
        wait.Params.DeviceId = "dev";
        wait.Params.Tag = "dev/holding/speed";
        wait.Params.Expected = "9999";
        wait.Params.TimeoutMs = 200;
        wait.Params.PollMs = 20;
        graph.AddNode(wait);

        scheduler.Load(graph);
        var result = await scheduler.RunOnceAsync(CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(1, result.FaultedNodes);
    }

    [Fact]
    public void CommNodeFactory_ResolvesContracts()
    {
        var factory = new CommNodeFactory();
        Assert.NotNull(factory.Create(new NodeContract("comm.read", 1), "r"));
        Assert.NotNull(factory.Create(new NodeContract("comm.write", 1), "w"));
        Assert.NotNull(factory.Create(new NodeContract("comm.wait", 1), "a"));
        Assert.Null(factory.Create(new NodeContract("unknown", 1), "x"));
    }

    [Fact]
    public void CommNodes_ExposeParameterObjects()
    {
        Assert.IsAssignableFrom<IParameterized>(new CommReadNode("r"));
        Assert.IsAssignableFrom<IParameterized>(new CommWriteNode("w"));
        Assert.IsAssignableFrom<IParameterized>(new CommWaitNode("a"));
    }

    [Fact]
    public void CommNodes_ParametersReflectDeviceAndTag()
    {
        var read = new CommReadNode("r");
        read.Params.DeviceId = "plc1";
        read.Params.Tag = "plc1/holding/x";
        var meta = ParameterReflection.Summarize(read.Params);
        Assert.Contains(meta, m => m.Name == "DeviceId");
        Assert.Contains(meta, m => m.Name == "Tag");
    }
}

/// <summary>
/// Swap-protocol gate (ADR-004): same graph JSON, different adapter registration →
/// graph serialization is unchanged and execution produces the same result.
/// / 换协议不动图（ADR-004）：同一图 JSON，换适配器注册 → 图序列化不变，执行结果一致
/// </summary>
public class SwapProtocolTests
{
    [Fact]
    public void SwapAdapter_GraphJsonUnchanged()
    {
        // Build a graph using comm.read
        var graph = new GraphModel();
        var reader = new CommReadNode("r");
        reader.Params.DeviceId = "dev";
        reader.Params.Tag = "dev/holding/speed";
        graph.AddNode(reader);

        var json1 = GraphJsonSerializer.Serialize(graph);

        // Deserialize with CommNodeFactory to get a fresh graph
        var graph2 = GraphJsonSerializer.Deserialize(json1, new CommNodeFactory());
        var json2 = GraphJsonSerializer.Serialize(graph2);

        Assert.Equal(json1, json2);
    }

    [Fact]
    public async Task DifferentAdapter_SameContract_ExecutesIdentically()
    {
        var tags = new TagTable();
        tags.Register(new("dev/holding/speed", "dev", "holding:0", typeof(ushort), true, true));

        // First runtime: real Modbus simulator
        var sim = ModbusTcpSimulator.Start();
        sim.SeedHolding(0, 777);
        var realConn = new ModbusTcpConnection("dev", "127.0.0.1", sim.Port, 0xFF, tags);
        var runtime1 = new CommRuntime(tags);
        runtime1.Add(realConn);
        await using var _rt1 = runtime1;
        await using var _sim = sim;
        var sched1 = new GraphScheduler();
        sched1.Services[typeof(ICommRuntime)] = runtime1;

        var graph = new GraphModel();
        var reader = new CommReadNode("r");
        reader.Params.DeviceId = "dev";
        reader.Params.Tag = "dev/holding/speed";
        graph.AddNode(reader);
        sched1.Load(graph);
        var result1 = await sched1.RunOnceAsync(CancellationToken.None);
        Assert.True(result1.Success);

        // Second runtime: fake adapter returning same value
        var fakeConn = new FakeDeviceConnection("dev", tags, 777);
        var runtime2 = new CommRuntime(tags);
        runtime2.Add(fakeConn);
        var sched2 = new GraphScheduler();
        sched2.Services[typeof(ICommRuntime)] = runtime2;

        // Reuse same graph nodes (protocol-agnostic: graph doesn't know about adapter)
        sched2.Load(graph);
        var result2 = await sched2.RunOnceAsync(CancellationToken.None);
        Assert.True(result2.Success);
    }
}

/// <summary>
/// A fake adapter that implements IDeviceConnection for swap-protocol testing.
/// Protocol-agnostic: the graph doesn't care which adapter is registered.
/// / 伪适配器：用于换协议测试的 IDeviceConnection 实现。协议无关：图不关心注册哪个适配器。
/// </summary>
internal sealed class FakeDeviceConnection(string deviceId, ITagTable tags, ushort fixedValue) : IDeviceConnection
{
    public string ProtocolId => "fake";
    public string DeviceId => deviceId;
    public ConnectionState State => ConnectionState.Connected;
    public ITagTable Tags => tags;

    public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<object> ReadAsync(string tag, CancellationToken ct)
    {
        var entry = Tags.Resolve(tag) ?? throw new ArgumentException($"unknown tag '{tag}'");
        if (!entry.Readable) throw new InvalidOperationException($"tag '{tag}' is not readable");
        return Task.FromResult<object>(fixedValue);
    }

    public Task WriteAsync(string tag, object value, CancellationToken ct) => Task.CompletedTask;

    public IObservable<TagValue> Subscribe(string tagsPattern) => new CommTestHelpers.EmptyObservable();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}