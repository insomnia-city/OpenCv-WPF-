using HalconWorkflow.Abstractions;
using HalconWorkflow.Abstractions.Undo;
using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Execution;
using HalconWorkflow.Core.Graph;
using HalconWorkflow.Core.Model;
using HalconWorkflow.Core.Serialization;
using HalconWorkflow.Nodes.Flow;
using Xunit;

namespace HalconWorkflow.Nodes.Flow.Tests;

/// <summary>
/// §9.2 smoke: undo drives the same kernel the editor mutates; engine state rolls back to the save point. 
/// §9.2 冒烟：撤销与编辑器操作同一内核；撤回到保存点即引擎状态回滚
/// </summary>
public class UndoSmokeTests
{
    [Fact]
    public async Task Adding_And_Undoing_FlowNode_RollsBackKernel()
    {
        var graph = new GraphModel();
        var undo = new UndoService();

        Assert.False(graph.Nodes.ContainsKey("branch"));
        await undo.PushAndRunAsync(GraphCommands.AddNode(graph, FlowNodes.Branch("branch"), 10, 20), CancellationToken.None);
        Assert.True(graph.Nodes.ContainsKey("branch"));
        Assert.Single(graph.Nodes);
        Assert.Equal((10, 20), graph.Nodes["branch"].Position);

        Assert.Equal(1, undo.CanUndoCount);
        Assert.True(await undo.UndoAsync(CancellationToken.None));
        Assert.False(graph.Nodes.ContainsKey("branch"));          // engine rolled back · 引擎回滚
        Assert.Empty(graph.Nodes);
        Assert.Equal(1, undo.CanRedoCount);

        Assert.True(await undo.RedoAsync(CancellationToken.None));
        Assert.True(graph.Nodes.ContainsKey("branch"));
        Assert.Single(graph.Nodes);                       // sanity: nothing leaked · 无泄漏
    }

    [Fact]
    public async Task Connect_And_Undo_RollsBackLinks()
    {
        var graph = new GraphModel();
        var undo = new UndoService();
        var counter = FlowNodes.Counter("c");
        var join = FlowNodes.Join("j");
        await undo.PushAndRunAsync(GraphCommands.AddNode(graph, counter, 0, 0), CancellationToken.None);
        await undo.PushAndRunAsync(GraphCommands.AddNode(graph, join, 0, 0), CancellationToken.None);

        var link = await Connector(graph, undo, counter, join);
        Assert.NotNull(link);
        Assert.Single(graph.Links);

        await undo.UndoAsync(CancellationToken.None); // undo connect
        Assert.Empty(graph.Links);

        await undo.RedoAsync(CancellationToken.None); // redo connect
        Assert.Single(graph.Links);
    }

    [Fact]
    public async Task CompositeCommand_UndoesAsOneStep()
    {
        var graph = new GraphModel();
        var undo = new UndoService();
        var steps = new IUndoableCommand[]
        {
            GraphCommands.AddNode(graph, FlowNodes.Counter("c"), 0, 0),
            GraphCommands.AddNode(graph, FlowNodes.Join("j"), 120, 0),
        };
        await undo.PushAndRunAsync(new CompositeCommand("paste cluster", steps), CancellationToken.None);
        Assert.Equal(2, graph.Nodes.Count);
        Assert.Equal(1, undo.CanUndoCount);

        await undo.UndoAsync(CancellationToken.None);
        Assert.Empty(graph.Nodes);   // one undo for the whole cluster · 一次撤销还原整批
    }

    [Fact]
    public async Task StackCaps_AtTwoHundred()
    {
        var undo = new UndoService();
        for (var i = 0; i < 250; i++)
        {
            var graph = new GraphModel();
            await undo.PushAndRunAsync(GraphCommands.AddNode(graph, FlowNodes.Delay($"d{i}", 0), 0, 0), CancellationToken.None);
        }
        Assert.Equal(200, undo.CanUndoCount);
    }

    [Fact]
    public async Task Serialized_Graph_UndoRoundTrip_StartsWith_CleanFactory()
    {
        var graph = new GraphModel();
        var branch = FlowNodes.Branch("b");
        graph.AddNode(branch);
        var json = GraphJsonSerializer.Serialize(graph);

        var undo = new UndoService();
        var reloaded = GraphJsonSerializer.Deserialize(json, new FlowNodeFactory());
        await undo.PushAndRunAsync(GraphCommands.AddNode(reloaded, FlowNodes.Counter("c"), 0, 0), CancellationToken.None);
        Assert.Equal(2, reloaded.Nodes.Count);

        await undo.UndoAsync(CancellationToken.None);
        Assert.Equal(1, reloaded.Nodes.Count);   // counter removed from the reloaded kernel · 从重载内核移除 counter
    }

    [Fact]
    public async Task SavePoint_UndoToSavePoint_RollsBackOnlyUnsavedEdits()
    {
        var graph = new GraphModel();
        var undo = new UndoService();
        await undo.PushAndRunAsync(GraphCommands.AddNode(graph, FlowNodes.Counter("c"), 0, 0), CancellationToken.None);
        undo.MarkSaved();                              // "saved" the counter · 已保存 counter
        Assert.False(undo.CanUndoToSavePoint);

        await undo.PushAndRunAsync(GraphCommands.AddNode(graph, FlowNodes.Join("j"), 0, 0), CancellationToken.None);
        await undo.PushAndRunAsync(GraphCommands.AddNode(graph, FlowNodes.Branch("b"), 0, 0), CancellationToken.None);
        Assert.Equal(3, graph.Nodes.Count);
        Assert.True(undo.CanUndoToSavePoint);

        var steps = await undo.UndoToSavePointAsync(CancellationToken.None);
        Assert.Equal(2, steps);
        Assert.Equal(["c"], graph.Nodes.Keys);
        Assert.False(undo.CanUndoToSavePoint);
        Assert.Equal(2, undo.CanRedoCount);            // roll-back is itself redoable · 回滚本身可重做

        Assert.True(await undo.RedoAsync(CancellationToken.None));
        Assert.Equal(2, graph.Nodes.Count);
    }

    [Fact]
    public async Task SavePoint_SurvivesStackCapTrim()
    {
        var graph = new GraphModel();
        var undo = new UndoService();
        undo.MarkSaved();
        for (var i = 0; i < 250; i++)
            await undo.PushAndRunAsync(GraphCommands.AddNode(graph, FlowNodes.Delay($"d{i}", 0), 0, 0), CancellationToken.None);

        Assert.Equal(200, undo.CanUndoCount);          // cap trims the oldest · 上限裁剪最旧
        Assert.True(undo.CanUndoToSavePoint);
        Assert.Equal(200, await undo.UndoToSavePointAsync(CancellationToken.None));
        Assert.False(undo.CanUndoToSavePoint);
    }

    [Fact]
    public async Task Clear_ResetsSavePoint()
    {
        var undo = new UndoService();
        var graph = new GraphModel();
        await undo.PushAndRunAsync(GraphCommands.AddNode(graph, FlowNodes.Counter("c"), 0, 0), CancellationToken.None);
        undo.MarkSaved();
        undo.Clear();
        Assert.Equal(0, undo.CanUndoCount);
        Assert.False(undo.CanUndoToSavePoint);
    }

    private static async Task<GraphLink?> Connector(GraphModel graph, UndoService undo, INode from, INode to)
    {
        var execOut = from.Outputs.First(p => p.Kind == PortKind.Exec);
        var execIn = to.Inputs.First(p => p.Kind == PortKind.Exec);
        var ok = await TryConnect(graph, undo, execOut, execIn);
        return ok;
    }

    private static async Task<GraphLink?> TryConnect(GraphModel graph, UndoService undo, IPort from, IPort to)
    {
        var cmd = GraphCommands.Connect(graph, from, to);
        await undo.PushAndRunAsync(cmd, CancellationToken.None);
        return graph.Links.FirstOrDefault();
    }
}

/// <summary>
/// Minimal test node that captures scope tags for assertions. · 最小测试节点：捕获作用域 tag 供断言
/// </summary>
internal sealed class ProbeNode : INode
{
    private readonly IList<string> _capture;

    public ProbeNode(string id, IList<string> capture)
    {
        Id = id;
        _capture = capture;
        Inputs = [new P(this, "exec", PortDirection.In, PortKind.Exec, null)];
    }

    public string Id { get; }
    public NodeContract Contract { get; } = new("test.probe", 1);
    public NodeState State { get; private set; } = NodeState.Idle;
    public IReadOnlyList<IPort> Inputs { get; }
    public IReadOnlyList<IPort> Outputs { get; } = [];

    public Task ExecuteAsync(IExecutionContext ctx, CancellationToken ct)
    {
        State = NodeState.Running;
        _capture.Add(ctx.GetData("Count")?.ToString() ?? "null");
        State = NodeState.Succeeded;
        return Task.CompletedTask;
    }

    private sealed class P(INode owner, string name, PortDirection dir, PortKind kind, ITypeDescriptor? type) : IPort
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

public class CounterFeedsScopeTests
{
    [Fact]
    public async Task Counter_EmitsCountOne_Downstream()
    {
        var graph = new GraphModel();
        var captures = new List<string>();
        var counter = FlowNodes.Counter("c");
        var probe = new ProbeNode("p", captures);
        graph.AddNode(counter);
        graph.AddNode(probe);
        graph.Connect(counter.Outputs.First(p => p.Kind == PortKind.Exec),
            probe.Inputs.First(p => p.Kind == PortKind.Exec));
        var scheduler = new GraphScheduler();
        scheduler.Load(graph);
        await scheduler.RunOnceAsync(CancellationToken.None);
        Assert.Equal(["1"], captures);
    }
}