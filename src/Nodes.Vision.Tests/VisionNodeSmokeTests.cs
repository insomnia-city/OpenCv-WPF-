using HalconWorkflow.Abstractions;
using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Execution;
using HalconWorkflow.Core.Graph;
using HalconWorkflow.Core.Model;
using HalconWorkflow.Nodes.Vision.Engines;
using HalconWorkflow.Nodes.Vision.Imaging;
using HalconWorkflow.Nodes.Vision.Nodes;
using Xunit;

namespace HalconWorkflow.Nodes.Vision.Tests;

/// <summary>
/// End-to-end vision chain smoke: nodes flow data through the shared scope tags
/// ("image"/"region"/"result") while an engine pool is borrowed per execute.
/// / 视觉链路端到端冒烟：节点经共享作用域 tag（image/region/result）传值;每次执行借取引擎池。
/// </summary>
public class VisionNodeSmokeTests
{
    private static readonly VisionNodeFactory Factory = new();

    private static INode Create(string ns)
    {
        // vision.threshold lives at v2; v1 is reserved to the legacy scaffold nodes. · vision.threshold 为 v2;v1 保留给旧脚手架
        var version = ns == "vision.threshold" ? 2 : 1;
        return Factory.Create(new NodeContract(ns, version), "n_" + ns.Split('.')[1])!;
    }

    private static async Task RunVisionAsync(GraphModel graph, VisionEnginePool pool)
    {
        var scheduler = new GraphScheduler();
        scheduler.Services[typeof(IVisionEnginePool)] = pool;
        scheduler.Load(graph);
        await scheduler.RunOnceAsync(CancellationToken.None);
    }

    private static void ChainExec(GraphModel graph, params INode[] nodes)
    {
        for (var i = 0; i < nodes.Length - 1; i++)
        {
            var execOut = nodes[i].Outputs.First(p => p.Kind == PortKind.Exec);
            var execIn = nodes[i + 1].Inputs.First(p => p.Kind == PortKind.Exec);
            graph.Connect(execOut, execIn);

            // connect the shared "image" data port when the downstream input requires it · 下游必需时接共享 image 数据端口
            var requiredIn = nodes[i + 1].Inputs.FirstOrDefault(p => p.Kind == PortKind.Data && p.IsRequired);
            var matchingOut = requiredIn is null ? null
                : nodes[i].Outputs.FirstOrDefault(p => p.Kind == PortKind.Data && p.Name == requiredIn.Name);
            if (requiredIn is not null && matchingOut is not null)
                graph.Connect(matchingOut, requiredIn);
        }
    }

    [Fact]
    public async Task Grab_ExecutesSucceeded()
    {
        await using var pool = new VisionEnginePool(() => new PhantomVisionEngine(), 1);
        var graph = new GraphModel();
        var grab = Create("vision.grab");
        graph.AddNode(grab);
        await RunVisionAsync(graph, pool);
        Assert.Equal(NodeState.Succeeded, grab.State);
    }

    [Fact]
    public async Task GrabThreshold_ProducesNonTrivialMask()
    {
        await using var pool = new VisionEnginePool(() => new PhantomVisionEngine(), 1);
        var graph = new GraphModel();
        var region = new List<object?>();
        var grab = Create("vision.grab");
        var thr = Create("vision.threshold");
        var probe = new ProbeNode("probe", region, "region");
        graph.AddNode(grab); graph.AddNode(thr); graph.AddNode(probe);
        ChainExec(graph, grab, thr, probe);

        await RunVisionAsync(graph, pool);

        var mask = Assert.IsType<VisionFrame>(Assert.Single(region));
        Assert.Equal(PixFormat.Gray8, mask.Format);
        Assert.True(mask.Bits.Any(b => b == 255), "synthetic frame must contain bright pixels above threshold");
    }

    [Fact]
    public async Task GrabMeasure_EmitsMeasurementResult()
    {
        await using var pool = new VisionEnginePool(() => new PhantomVisionEngine(), 1);
        var graph = new GraphModel();
        var results = new List<object?>();
        var grab = Create("vision.grab");
        var measure = Create("vision.measure");
        var probe = new ProbeNode("probe", results, "result");
        graph.AddNode(grab); graph.AddNode(measure); graph.AddNode(probe);
        ChainExec(graph, grab, measure, probe);

        await RunVisionAsync(graph, pool);

        var m = Assert.IsType<MeasurementResult>(Assert.Single(results));
        Assert.True(m.Distance > 0, "synthetic sine image must produce a measurable edge span");
        Assert.True(m.Edges >= 2);
    }

    [Fact]
    public async Task ToMat_FlipsProvenance_ToMatDomain()
    {
        await using var pool = new VisionEnginePool(() => new PhantomVisionEngine(), 1);
        var graph = new GraphModel();
        var images = new List<object?>();
        var grab = Create("vision.grab");
        var tomat = Create("vision.tomat");
        var probe = new ProbeNode("probe", images, "image");
        graph.AddNode(grab); graph.AddNode(tomat); graph.AddNode(probe);
        ChainExec(graph, grab, tomat, probe);

        await RunVisionAsync(graph, pool);

        var frame = Assert.IsType<VisionFrame>(Assert.Single(images));
        Assert.Equal(FrameDomain.Mat, frame.Domain);
    }

    [Fact]
    public async Task EndToEndBridge_ReturnsToSyntheticDomain()
    {
        await using var pool = new VisionEnginePool(() => new PhantomVisionEngine(), 1);
        var graph = new GraphModel();
        var images = new List<object?>();
        var grab = Create("vision.grab");
        var tomat = Create("vision.tomat");
        var tohobject = Create("vision.tohobject");
        var probe = new ProbeNode("probe", images, "image");
        graph.AddNode(grab); graph.AddNode(tomat); graph.AddNode(tohobject); graph.AddNode(probe);
        ChainExec(graph, grab, tomat, tohobject, probe);

        await RunVisionAsync(graph, pool);

        var frame = Assert.IsType<VisionFrame>(Assert.Single(images));
        Assert.Equal(FrameDomain.Synthetic, frame.Domain); // back to HObject after round trip / 经往返后回到 HObject
    }
}

/// <summary>Minimal capture node mirroring the flow test double. / 最小捕获节点（同流程测试替身）</summary>
internal sealed class ProbeNode : INode
{
    private readonly IList<object?> _capture;
    private readonly string _tag;

    public ProbeNode(string id, IList<object?> capture, string tag)
    {
        Id = id;
        _capture = capture;
        _tag = tag;
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
        _capture.Add(ctx.GetData(_tag));
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
        public bool IsRequired => true;
    }
}