using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Execution;
using HalconWorkflow.Core.Graph;
using HalconWorkflow.Core.Model;
using HalconWorkflow.Core.Types;

namespace HalconWorkflow.Core.Tests;

/// <summary>
/// Regression gates for link-driven data flow (§5.4): parameter passing follows the links,
/// not the flat tag names — different-named source/target ports pass values, same-named
/// outputs on parallel branches no longer cross-talk, data links order the producer before
/// the consumer, and data feedback cycles are rejected. · 连线驱动数据流传参的回归门(§5.4):
/// 参数传递跟随连线而非扁平 tag 名——异名源/目标端口可传值，并行分支的同名输出不再串扰，
/// 数据连线把生产者排在消费者之前，数据反馈环被拒绝。
/// </summary>
public sealed class LinkedDataFlowTests
{
    [Fact]
    public async Task RunOnce_DifferentlyNamedLinkedPorts_PassesValueByLink()
    {
        var capture = new List<object?>();
        var graph = new GraphModel();
        graph.AddNode(Node("src", outPort: ("Alpha", "hello"), execOut: true));
        graph.AddNode(Node("sink", inName: "Beta", capture: capture, execIn: true));

        graph.Connect(Exec(graph, "src", PortDirection.Out), Exec(graph, "sink", PortDirection.In));
        graph.Connect(Data(graph, "src", PortDirection.Out, "Alpha"), Data(graph, "sink", PortDirection.In, "Beta"));

        var result = await RunOnce(graph);

        Assert.True(result.Success);
        Assert.Equal(["hello"], capture);
    }

    [Fact]
    public async Task RunOnce_SameNamedOutputsOnParallelBranches_NoCrossTalk()
    {
        var c1 = new List<object?>();
        var c2 = new List<object?>();
        var graph = new GraphModel();
        graph.AddNode(Node("cam1", outPort: ("Image", "A"), execOut: true));
        graph.AddNode(Node("th1", inName: "Image", capture: c1, execIn: true));
        graph.AddNode(Node("cam2", outPort: ("Image", "B"), execOut: true));
        graph.AddNode(Node("th2", inName: "Image", capture: c2, execIn: true));

        graph.Connect(Exec(graph, "cam1", PortDirection.Out), Exec(graph, "th1", PortDirection.In));
        graph.Connect(Data(graph, "cam1", PortDirection.Out, "Image"), Data(graph, "th1", PortDirection.In, "Image"));
        graph.Connect(Exec(graph, "cam2", PortDirection.Out), Exec(graph, "th2", PortDirection.In));
        graph.Connect(Data(graph, "cam2", PortDirection.Out, "Image"), Data(graph, "th2", PortDirection.In, "Image"));

        var result = await RunOnce(graph);

        Assert.True(result.Success);
        Assert.Equal(["A"], c1);
        Assert.Equal(["B"], c2);
    }

    [Fact]
    public async Task RunOnce_DataOnlyLink_ProducerRunsBeforeConsumer()
    {
        // Insert the consumer first on purpose: without data-link ordering it would run before
        // its producer and read null. · 故意先插入消费者：若数据连线不参与排序，它将先于生产者执行并读到 null
        var capture = new List<object?>();
        var graph = new GraphModel();
        graph.AddNode(Node("sink", inName: "X", capture: capture, execIn: true));
        graph.AddNode(Node("src", outPort: ("X", 42), execOut: true));

        Assert.NotNull(graph.Connect(Data(graph, "src", PortDirection.Out, "X"), Data(graph, "sink", PortDirection.In, "X")));
        Assert.Empty(graph.Validate());

        var result = await RunOnce(graph);

        Assert.True(result.Success);
        Assert.Equal([42], capture);
    }

    [Fact]
    public async Task RunOnce_UnlinkedOptionalInput_IsClearedNotStale()
    {
        var capture = new List<object?>();
        var graph = new GraphModel();
        graph.AddNode(Node("polluter", outPort: ("Shared", "stale"), execOut: true));
        graph.AddNode(Node("sink", inName: "Shared", capture: capture, execIn: true, optional: true));

        graph.Connect(Exec(graph, "polluter", PortDirection.Out), Exec(graph, "sink", PortDirection.In));
        Assert.Empty(graph.Validate());

        var result = await RunOnce(graph);

        Assert.True(result.Success);
        // Old name-based flow read "stale" by accident; the link-driven flow clears unlinked inputs. 
        // · 旧按名流会误读 "stale"；连线驱动流对未接线输入清空
        Assert.Equal([null], capture);
    }

    [Fact]
    public void Connect_DataFeedbackCycle_IsRejected()
    {
        var graph = new GraphModel();
        graph.AddNode(Node("a", outPort: ("Q", 1), inName: "P", execOut: true));
        graph.AddNode(Node("b", outPort: ("P", 2), inName: "Q", execOut: true));

        Assert.NotNull(graph.Connect(Data(graph, "a", PortDirection.Out, "Q"), Data(graph, "b", PortDirection.In, "Q")));
        Assert.Null(graph.Connect(Data(graph, "b", PortDirection.Out, "P"), Data(graph, "a", PortDirection.In, "P")));
    }

    [Fact]
    public async Task RunOnce_SeededInput_MirrorsPortValue()
    {
        var capture = new List<object?>();
        var graph = new GraphModel();
        graph.AddNode(Node("src", outPort: ("Alpha", 7), execOut: true));
        graph.AddNode(Node("sink", inName: "Beta", capture: capture, execIn: true));
        graph.Connect(Exec(graph, "src", PortDirection.Out), Exec(graph, "sink", PortDirection.In));
        graph.Connect(Data(graph, "src", PortDirection.Out, "Alpha"), Data(graph, "sink", PortDirection.In, "Beta"));

        var result = await RunOnce(graph);

        Assert.True(result.Success);
        var input = graph.Nodes["sink"].Node.Inputs.First(p => p.Kind == PortKind.Data);
        Assert.Equal(7, input.Value);
    }

    private static async Task<GraphRunResult> RunOnce(GraphModel graph)
    {
        await using var scheduler = new GraphScheduler { DebounceMs = 0 };
        scheduler.Load(graph);
        return await scheduler.RunOnceAsync(CancellationToken.None);
    }

    private static IPort Exec(GraphModel graph, string nodeId, PortDirection dir) =>
        PortOf(graph, nodeId, dir, PortKind.Exec, "exec");

    private static IPort Data(GraphModel graph, string nodeId, PortDirection dir, string name) =>
        PortOf(graph, nodeId, dir, PortKind.Data, name);

    private static IPort PortOf(GraphModel graph, string nodeId, PortDirection dir, PortKind kind, string name)
    {
        var ports = dir == PortDirection.In
            ? graph.Nodes[nodeId].Node.Inputs
            : graph.Nodes[nodeId].Node.Outputs;
        return ports.First(p => p.Kind == kind && p.Name == name);
    }

    private static INode Node(string id, (string Name, object? Value)? outPort = null,
        string? inName = null, IList<object?>? capture = null, bool execIn = false, bool execOut = false,
        bool optional = false)
        => new TestNode(id, outPort, execIn, execOut, inName is null ? null : (inName, optional),
            ctx =>
            {
                if (inName is not null) capture?.Add(ctx.GetData(inName));
                if (outPort is { } o) ctx.SetData(o.Name, o.Value);
                return Task.CompletedTask;
            });

    private sealed class TestNode : INode
    {
        private readonly Func<IExecutionContext, Task> _onExec;
        private readonly List<IPort> _inputs = [];
        private readonly List<IPort> _outputs = [];

        public TestNode(string id,
            (string Name, object? Value)? outPort, bool execIn, bool execOut,
            (string Name, bool Optional)? inPort, Func<IExecutionContext, Task> onExec)
        {
            Id = id;
            _onExec = onExec;
            Contract = new NodeContract("test.link", 1);
            if (execIn) _inputs.Add(new P(this, "exec", PortDirection.In, PortKind.Exec, null, false));
            if (inPort is { } i) _inputs.Add(new P(this, i.Name, PortDirection.In, PortKind.Data, ResultDescriptor.Instance, !i.Optional));
            if (execOut) _outputs.Add(new P(this, "exec", PortDirection.Out, PortKind.Exec, null, false));
            if (outPort is { } o) _outputs.Add(new P(this, o.Name, PortDirection.Out, PortKind.Data, ResultDescriptor.Instance, false));
        }

        public string Id { get; }
        public NodeContract Contract { get; }
        public IReadOnlyList<IPort> Inputs => _inputs;
        public IReadOnlyList<IPort> Outputs => _outputs;
        public NodeState State { get; private set; } = NodeState.Idle;

        public async Task ExecuteAsync(IExecutionContext ctx, CancellationToken ct)
        {
            State = NodeState.Running;
            await _onExec(ctx);
            State = NodeState.Succeeded;
        }

        private sealed class P(INode owner, string name, PortDirection dir, PortKind kind, ITypeDescriptor? type, bool required) : IPort
        {
            public INode Owner { get; } = owner;
            public string Name { get; } = name;
            public PortDirection Direction { get; } = dir;
            public PortKind Kind { get; } = kind;
            public ITypeDescriptor? Type { get; } = type;
            public bool IsConnected { get; set; }
            public object? Value { get; set; }
            public bool IsRequired => required;
        }
    }
}