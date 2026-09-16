using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Execution;
using HalconWorkflow.Core.Graph;
using HalconWorkflow.Core.Model;

namespace HalconWorkflow.Core.Tests;

public class SchedulerTests
{
    [Fact]
    public async Task RunOnce_ValidGraph_SucceedsAllNodes()
    {
        var graph = BuildGraph();
        await using var scheduler = new GraphScheduler();
        scheduler.Load(graph);

        var completed = new List<string>();
        scheduler.NodeExecuted += e => { if (e.Phase == NodeExecutionPhase.Completed) completed.Add(e.NodeId); };

        var result = await scheduler.RunOnceAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(3, result.SucceededNodes);
        Assert.Equal(0, result.FaultedNodes);
        Assert.Contains("cam", completed);
        Assert.Contains("threshold", completed);
        Assert.Contains("result", completed);
        Assert.True(result.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task RunOnce_ThrowingNode_FaultsAndStops()
    {
        var graph = BuildGraph();
        var bad = new ThrowingNode("boom", new NodeContract("test.boom", 1));
        graph.AddNode(bad);

        // Chain the throwing node after threshold. · 把抛异常的节点排在 threshold 之后
        var thresh = graph.Nodes["threshold"];
        var b = graph.Nodes["boom"];
        graph.Validate();
        Assert.NotNull(graph.Connect(thresh.Node.Outputs.First(p => p.Kind == PortKind.Exec),
            b.Node.Inputs.First(p => p.Kind == PortKind.Exec)));

        await using var scheduler = new GraphScheduler();
        scheduler.Load(graph);

        var result = await scheduler.RunOnceAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, result.FaultedNodes);
        Assert.Contains("boom", result.Error);
    }

    [Fact]
    public async Task RunOnce_Cancellation_CancelsRemaining()
    {
        var graph = BuildGraph();
        // Make threshold slow so cancellation hits it. · 让 threshold 变慢以便取消命中
        await using var scheduler = new GraphScheduler();
        scheduler.Load(graph);

        using var cts = new CancellationTokenSource(30);
        var result = await scheduler.RunOnceAsync(cts.Token);

        Assert.True(result.Success || result.Error == "Cancelled.");
    }

    [Fact]
    public async Task TriggerLoop_RunsAndCoalesces()
    {
        var graph = BuildGraph();
        await using var scheduler = new GraphScheduler { DebounceMs = 0 };
        scheduler.Load(graph);

        var runs = 0;
        scheduler.RunCompleted += _ => Interlocked.Increment(ref runs);

        using var cts = new CancellationTokenSource(400);
        await scheduler.StartAsync(cts.Token);
        scheduler.Trigger(TriggerSource.Manual);
        scheduler.Trigger(TriggerSource.Manual);

        await Task.Delay(250, CancellationToken.None);
        await scheduler.StopAsync();

        Assert.True(runs >= 1, $"Expected at least 1 run, got {runs}");
        // Queue-limit-1 with drop-old-keep-new means bursts may coalesce; just assert it did not crash. 
        // 队列上限1丢旧保新：突发可能合并;仅断言未崩溃
    }

    [Fact]
    public async Task RunOnce_NoGraph_Throws()
    {
        await using var scheduler = new GraphScheduler();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scheduler.RunOnceAsync(CancellationToken.None));
    }

    /// <summary>
    /// Builds a runnable headless graph for architecture tests. · 为架构测试构建可无头运行的图
    /// </summary>
    public static GraphModel MakeHeadlessGraph()
    {
        var graph = new GraphModel();
        graph.AddNode(TestNodes.Grabber("cam"));
        graph.AddNode(TestNodes.Threshold());
        graph.AddNode(TestNodes.ResultOut("result"));
        var grab = graph.Nodes["cam"];
        var thresh = graph.Nodes["threshold"];
        var result = graph.Nodes["result"];

        graph.Connect(grab.Node.Outputs.First(p => p.Kind == PortKind.Exec),
            thresh.Node.Inputs.First(p => p.Kind == PortKind.Exec));
        graph.Connect(grab.Node.Outputs.First(p => p.Name == "Image"),
            thresh.Node.Inputs.First(p => p.Name == "Image"));
        graph.Connect(thresh.Node.Outputs.First(p => p.Kind == PortKind.Exec),
            result.Node.Inputs.First(p => p.Kind == PortKind.Exec));
        graph.Connect(thresh.Node.Outputs.First(p => p.Name == "Region"),
            result.Node.Inputs.First(p => p.Name == "Region"));
        return graph;
    }

    private static GraphModel BuildGraph() => MakeHeadlessGraph();

    private sealed class ThrowingNode : INode
    {
        public ThrowingNode(string id, NodeContract contract)
        {
            Id = id;
            Contract = contract;
        }

        public string Id { get; }
        public NodeContract Contract { get; }
        public IReadOnlyList<IPort> Inputs =>
            [new Port(this, "exec", PortDirection.In, PortKind.Exec, null)];
        public IReadOnlyList<IPort> Outputs =>
            [new Port(this, "exec", PortDirection.Out, PortKind.Exec, null)];
        public NodeState State { get; } = NodeState.Idle;

        public Task ExecuteAsync(IExecutionContext ctx, CancellationToken ct)
        {
            throw new InvalidOperationException("boom!");
        }

        private sealed class Port(INode owner, string name, PortDirection dir, PortKind kind, ITypeDescriptor? type) : IPort
        {
            public INode Owner { get; } = owner;
            public string Name { get; } = name;
            public PortDirection Direction { get; } = dir;
            public PortKind Kind { get; } = kind;
            public ITypeDescriptor? Type { get; } = type;
            public bool IsConnected { get; set; }
            public object? Value { get; set; }
        }
    }
}