using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Execution;
using HalconWorkflow.Core.Graph;
using HalconWorkflow.Core.Model;
using Xunit;

namespace HalconWorkflow.Core.Tests;

/// <summary>Fake tag-change source feeding an observable the engine subscribes to. · 供引擎订阅的伪 Tag 变化源</summary>
internal sealed class FakeTagSource : ITagObserverSource
{
    public Action<object?>? OnNext;

    public IDisposable Subscribe(Action<object?> onNext)
    {
        OnNext = onNext;
        return new Subscription { Owner = this };
    }

    public sealed class Subscription : IDisposable
    {
        public bool Disposed;
        public FakeTagSource? Owner;
        public void Dispose()
        {
            Disposed = true;
            var owner = Owner; // engines detach by disposing; mirror that here · 引擎解绑即调用 Dispose;此处同步清空
            if (owner is not null) owner.OnNext = null;
            Owner = null;
        }
    }
}

/// <summary>
/// Stage-20 trigger-plane engine tests (§5.4): timed / tag-change pulse generation and cleanup. 
/// · 阶段20 触发平面引擎测试(§5.4)：定时/Tag 变化脉冲生成与清理
/// </summary>
public class TriggerEngineTests
{
    private static TriggerConfig Config(TriggerSource source, bool enabled = true) => new()
    {
        Enabled = enabled,
        Source = source,
        Tag = "dev/area/x",
        IntervalMs = 25,
        DebounceMs = 5
    };

    [Fact]
    public async Task Timer_EmitsPeriodicPulses()
    {
        var sources = new List<TriggerSource>();
        var engine = new TriggerEngine(Config(TriggerSource.Timer), sources.Add);
        engine.Start();
        await Task.Delay(180);
        await engine.StopAsync();

        Assert.True(engine.TimerPulses >= 3, $"expected ≥3 timer pulses, got {engine.TimerPulses}");
        Assert.DoesNotContain(TriggerSource.TagChange, sources);
        Assert.All(sources, s => Assert.Equal(TriggerSource.Timer, s));
    }

    [Fact]
    public async Task Disabled_EmitsNothing()
    {
        var sources = new List<TriggerSource>();
        var engine = new TriggerEngine(Config(TriggerSource.Timer, enabled: false), sources.Add);
        engine.Start();
        await Task.Delay(120);
        await engine.StopAsync();
        Assert.Empty(sources);
    }

    [Fact]
    public async Task ManualSource_EmitsNothing_UntilCalled()
    {
        var sources = new List<TriggerSource>();
        var engine = new TriggerEngine(Config(TriggerSource.Manual), sources.Add);
        engine.Start();
        await Task.Delay(120);
        await engine.StopAsync();
        Assert.Empty(sources);
    }

    [Fact]
    public async Task TagChange_PushesOnePulsePerChange()
    {
        var sources = new List<TriggerSource>();
        var fake = new FakeTagSource();
        var engine = new TriggerEngine(Config(TriggerSource.TagChange), sources.Add, _ => fake);
        engine.Start();
        Assert.NotNull(fake.OnNext);

        fake.OnNext!(42);
        fake.OnNext!("second");
        await Task.Delay(50);
        await engine.StopAsync();

        Assert.True(engine.TagPulses >= 2, $"expected ≥2 tag pulses, got {engine.TagPulses}");
        Assert.All(sources, s => Assert.Equal(TriggerSource.TagChange, s));
        Assert.Null(fake.OnNext); // engine detached on stop · 停止后已解绑
    }

    [Fact]
    public async Task Stop_DisposesTagSubscription()
    {
        var fake = new FakeTagSource();
        var engine = new TriggerEngine(Config(TriggerSource.TagChange), _ => { }, _ => fake);
        engine.Start();
        await engine.StopAsync();
        Assert.Null(fake.OnNext);
    }

    [Fact]
    public async Task TagChange_UnresolvedPattern_StaysIdle_WithError()
    {
        var sources = new List<TriggerSource>();
        var engine = new TriggerEngine(Config(TriggerSource.TagChange), sources.Add, _ => null);
        engine.Start();
        await Task.Delay(80);
        await engine.StopAsync();
        Assert.Empty(sources);
        Assert.NotNull(engine.Error);
    }

    [Fact]
    public async Task Stop_CancelsTimerLoop()
    {
        var engine = new TriggerEngine(Config(TriggerSource.Timer), _ => { });
        engine.Start();
        await Task.Delay(70);
        await engine.StopAsync();
        var before = engine.TimerPulses;
        await Task.Delay(120);
        Assert.Equal(before, engine.TimerPulses);
    }
}

/// <summary>
/// Stage-20 scheduler lifecycle: stop notifies nodes implementing <see cref="IStoppableNode"/>. 
/// · 阶段20 调度器生命周期：停止时通知实现 IStoppableNode 的节点
/// </summary>
public class SchedulerStoppableTests
{
    private sealed class TrackedNode(string id) : INode, IStoppableNode
    {
        public bool Stopped;
        public string Id => id;
        public NodeContract Contract => new("tracked", 1);
        public IReadOnlyList<IPort> Inputs => [new StubPort(this, PortDirection.In)];
        public IReadOnlyList<IPort> Outputs => [new StubPort(this, PortDirection.Out)];
        public NodeState State => NodeState.Idle;
        public Task ExecuteAsync(IExecutionContext ctx, CancellationToken ct) => Task.CompletedTask;
        public Task OnSchedulerStopAsync(CancellationToken ct) { Stopped = true; return Task.CompletedTask; }
    }

    private sealed record StubPort(INode OwnerNode, PortDirection Direction) : IPort
    {
        public INode Owner => OwnerNode;
        public string Name => "exec";
        public PortKind Kind => PortKind.Exec;
        public ITypeDescriptor? Type => null;
        public bool IsConnected { get; set; }
        public object? Value { get; set; }
    }

    [Fact]
    public async Task StopAsync_NotifiesStoppableNodes()
    {
        var graph = new GraphModel();
        var node = new TrackedNode("n1");
        graph.AddNode(node);

        var scheduler = new GraphScheduler();
        scheduler.Load(graph);
        using var cts = new CancellationTokenSource();
        await scheduler.StartAsync(cts.Token);
        scheduler.Trigger(TriggerSource.Manual);
        await scheduler.StopAsync();

        Assert.True(node.Stopped);
        // Idempotent second stop must not blow up. · 幂等二次停止不应异常
        await scheduler.StopAsync();
    }

    [Fact]
    public async Task DisposeAsync_AlsoStopsNodes()
    {
        var graph = new GraphModel();
        var node = new TrackedNode("n2");
        graph.AddNode(node);
        var scheduler = new GraphScheduler();
        scheduler.Load(graph);
        await scheduler.StartAsync(CancellationToken.None);
        await scheduler.DisposeAsync();
        Assert.True(node.Stopped);
    }
}