using HalconWorkflow.Abstractions;
using HalconWorkflow.Abstractions.Parameters;
using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Execution;
using HalconWorkflow.Core.Graph;
using HalconWorkflow.Core.Model;
using HalconWorkflow.Core.Serialization;
using HalconWorkflow.Nodes.Comm;
using Xunit;

namespace HalconWorkflow.Nodes.Comm.Tests;

/// <summary>Recording nudger capturing every re-arm source. · 记录每次重新武装来源的推入器</summary>
internal sealed class RecordingNudger : ITriggerNudger
{
    public List<TriggerSource> Sources { get; } = [];
    public void Nudge(TriggerSource source) => Sources.Add(source);
}

/// <summary>
/// Fake comm runtime hosting a fake connection; the graph resolves through it. 
/// · 承载伪连接的伪通讯运行时;图经由它解析
/// </summary>
internal sealed class FakeCommRuntime : ICommRuntime
{
    private readonly Dictionary<string, IDeviceConnection> _map = new();
    public FakeCommRuntime(ITagTable tags) => Tags = tags;
    public ITagTable Tags { get; }
    public void Add(IDeviceConnection c) => _map[c.DeviceId] = c;
    public IDeviceConnection? Resolve(string deviceId) => _map.TryGetValue(deviceId, out var c) ? c : null;
}

/// <summary>
/// Fake adapter with a pushable change stream (stage-20 TagTrigger). 
/// · 携带可推送变化流的伪适配器(阶段20 TagTrigger)
/// </summary>
internal sealed class RecordingConnection(string deviceId) : IDeviceConnection
{
    public int SubscribeCount;
    public string? LastPattern;
    private readonly List<IObserver<TagValue>> _observers = [];
    private readonly object _gate = new();

    public string ProtocolId => "fake";
    public string DeviceId => deviceId;
    public ConnectionState State => ConnectionState.Connected;
    public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<object> ReadAsync(string tag, CancellationToken ct) => throw new NotSupportedException();
    public Task WriteAsync(string tag, object value, CancellationToken ct) => Task.CompletedTask;

    public IObservable<TagValue> Subscribe(string tagsPattern)
    {
        lock (_gate) { SubscribeCount++; LastPattern = tagsPattern; }
        return new ChangeObservable(this);
    }

    public void Emit(object? value)
    {
        var tv = new TagValue("dev/area/x", value, DateTimeOffset.UtcNow, Quality.Good);
        IObserver<TagValue>[] obs;
        lock (_gate) obs = _observers.ToArray();
        foreach (var o in obs) o.OnNext(tv);
    }

    private sealed class ChangeObservable(RecordingConnection owner) : IObservable<TagValue>
    {
        public IDisposable Subscribe(IObserver<TagValue> observer)
        {
            lock (owner._gate) owner._observers.Add(observer);
            return new Detach(owner, observer);
        }
    }

    private sealed class Detach(RecordingConnection owner, IObserver<TagValue> observer) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate) owner._observers.Remove(observer);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Stage-20 comm.tagtrigger node tests (§5.4): change subscription, gated outputs, debounce,
/// scheduler-stop detach and re-arm. · 阶段20 comm.tagtrigger 节点测试(§5.4)：变化订阅、门控输出、
/// 去抖、调度器停止解绑与重新武装。
/// </summary>
public class TagTriggerNodeTests
{
    private const string TagPattern = "dev/area/x";

    [Fact]
    public void Factory_CreatesTagTriggerWithPorts()
    {
        var node = new CommNodeFactory().Create(new NodeContract("comm.tagtrigger", 1), "t");
        Assert.NotNull(node);
        Assert.Equal("comm.tagtrigger", node!.Contract.Namespace);
        Assert.Equal(1, node.Contract.Version);
        Assert.Contains(node.Inputs, p => p.Kind == PortKind.Exec && p.Direction == PortDirection.In);
        Assert.Contains(node.Outputs, p => p.Kind == PortKind.Exec && p.Direction == PortDirection.Out);
        Assert.Contains(node.Outputs, p => p.Name == "Triggered");
        Assert.Contains(node.Outputs, p => p.Name == "Count");
        Assert.Contains(node.Outputs, p => p.Name == "Value");
    }

    [Fact]
    public async Task NoChange_Run_DoesNotPulse_AndDoesNotNudge()
    {
        var (scheduler, graph, node, conn, nudger) = BuildEnv();
        await scheduler.RunOnceAsync(CancellationToken.None);

        var gn = graph.Nodes[node.Id];
        Assert.False((bool)gn.GetOutput("Triggered").Value!);
        Assert.Equal(0L, (long)TypeConvert(gn.GetOutput("Count").Value!));
        Assert.Empty(nudger.Sources);
        Assert.Equal(1, conn.SubscribeCount);
    }

    [Fact]
    public async Task Change_RunPulsesWithValueAndCount_AndNudgesInternal()
    {
        var (scheduler, graph, node, conn, nudger) = BuildEnv();
        await scheduler.RunOnceAsync(CancellationToken.None); // arm subscription · 武装订阅

        conn.Emit(1234);
        await scheduler.RunOnceAsync(CancellationToken.None);

        var gn = graph.Nodes[node.Id];
        Assert.True((bool)gn.GetOutput("Triggered").Value!);
        Assert.Equal(1234, gn.GetOutput("Value").Value);
        Assert.Equal(1L, (long)TypeConvert(gn.GetOutput("Count").Value!));
        Assert.Equal([TriggerSource.Internal], nudger.Sources);
        Assert.Equal(1, conn.SubscribeCount); // subscribe is idempotent across cycles · 跨周期订阅幂等
    }

    [Fact]
    public async Task BurstBetweenCycles_CollapsesIntoOnePulse()
    {
        var (scheduler, graph, node, conn, nudger) = BuildEnv();
        await scheduler.RunOnceAsync(CancellationToken.None); // arm · 武装

        conn.Emit("a");
        conn.Emit("b"); // second change before the cycle -> both nudge but merge in the cycle · 周期前两次变化:都推动但周期内合并
        await scheduler.RunOnceAsync(CancellationToken.None);

        var gn = graph.Nodes[node.Id];
        Assert.True((bool)gn.GetOutput("Triggered").Value!);
        Assert.Equal("b", gn.GetOutput("Value").Value);
        Assert.Equal(1L, (long)TypeConvert(gn.GetOutput("Count").Value!)); // one pulse · 仅一次脉冲
        Assert.Equal(2, nudger.Sources.Count);                             // two nudges pre-pulse are undebounced · 未发脉冲前两次推动都不去抖
        Assert.All(nudger.Sources, s => Assert.Equal(TriggerSource.Internal, s));
    }

    [Fact]
    public async Task EdgeDebounce_SwallowsChangeTooCloseAfterPulse()
    {
        var (scheduler, graph, node, conn, nudger) = BuildEnv();
        var parameters = (TagTriggerParameters)((IParameterized)node).ParameterObject;
        parameters.DebounceMs = 600_000;
        await scheduler.RunOnceAsync(CancellationToken.None); // arm · 武装
        conn.Emit("prime");
        await scheduler.RunOnceAsync(CancellationToken.None); // pulse emitted here · 此处发出首脉冲(lastPulseTick 置位)

        conn.Emit("right after");
        await scheduler.RunOnceAsync(CancellationToken.None);

        var gn = graph.Nodes[node.Id];
        Assert.False((bool)gn.GetOutput("Triggered").Value!); // swallowed by edge debounce · 被边沿去抖吞掉
        Assert.Equal(1L, (long)TypeConvert(gn.GetOutput("Count").Value!));
        Assert.Single(nudger.Sources); // only the prime pulse nudged · 只有首脉冲推动了
    }

    [Fact]
    public async Task SchedulerStop_Disconnects_AndNextRunRearms()
    {
        var (scheduler, graph, node, conn, nudger) = BuildEnv();
        await scheduler.StartAsync(CancellationToken.None);
        await scheduler.RunOnceAsync(CancellationToken.None); // arm subscription · 武装订阅
        Assert.Equal(1, conn.SubscribeCount);

        await scheduler.StopAsync(); // Running→Stopped fires the node detach hook · 运行→停止触发节点解绑钩子
        conn.Emit("stale");
        await scheduler.RunOnceAsync(CancellationToken.None); // stale session must not bubble into a fresh cycle · 过期会话不得渗入新周期
        var gn = graph.Nodes[node.Id];
        Assert.False((bool)gn.GetOutput("Triggered").Value!);
        Assert.Empty(nudger.Sources); // stale session must not nudge · 过期会话不应再推动

        await scheduler.StartAsync(CancellationToken.None);
        await scheduler.RunOnceAsync(CancellationToken.None); // re-arms on the next execute · 下次执行重新武装
        Assert.Equal(2, conn.SubscribeCount);
        conn.Emit("fresh");
        await scheduler.RunOnceAsync(CancellationToken.None); // consume the fresh change · 消费新变化
        await scheduler.StopAsync();

        gn = graph.Nodes[node.Id];
        Assert.True((bool)gn.GetOutput("Triggered").Value!);
        Assert.Single(nudger.Sources);
    }

    private static object TypeConvert(object o) => o switch
    {
        long l => l,
        int i => (long)i,
        _ => o
    };

    private static (GraphScheduler Scheduler, GraphModel Graph, INode Node, RecordingConnection Conn, RecordingNudger Nudger) BuildEnv()
    {
        var tags = new Protocols.TagTable();
        var conn = new RecordingConnection("dev");
        var runtime = new FakeCommRuntime(tags);
        runtime.Add(conn);

        var node = new CommNodeFactory().Create(new NodeContract("comm.tagtrigger", 1), "t")!;
        var parameters = (TagTriggerParameters)((IParameterized)node).ParameterObject;
        parameters.DeviceId = "dev";
        parameters.Tag = TagPattern;

        var graph = new GraphModel();
        graph.AddNode(node);

        var nudger = new RecordingNudger();
        var scheduler = new GraphScheduler { DebounceMs = 0 };
        scheduler.Services[typeof(ICommRuntime)] = runtime;
        scheduler.Services[typeof(ITriggerNudger)] = nudger;
        scheduler.Load(graph);
        return (scheduler, graph, node, conn, nudger);
    }
}