using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Model;
using HalconWorkflow.Core.Types;

namespace HalconWorkflow.Runtime.Nodes;

/// <summary>
/// Built-in sample nodes shipped with the headless runtime so a graph can run with zero plugins. 
/// 无头运行时的内置示例节点：零插件即可跑通全图
/// </summary>
public static class SampleNodes
{
    /// <summary>
    /// Trigger / Start node: emits an exec pulse, optionally stamps batch info. 
    /// 触发/起点节点：发出控制流脉冲，可附带批次
    /// </summary>
    public static INode Start(string id, string? batch = null) =>
        new SimpleNode(id, new NodeContract("test.start", 1),
            execOut: true,
            execute: (ctx, _) =>
            {
                ctx.SetData("batch", batch ?? ctx.Current.Batch);
                return Task.CompletedTask;
            });

    /// <summary>
    /// Grabber: produces an "Image" value in the scope. · 采集器：在作用域产出 "Image"
    /// </summary>
    public static INode Grabber(string id) =>
        new SimpleNode(id, new NodeContract("vision.grabber", 1),
            execIn: true, execOut: true,
            dataOut: ("Image", ImageDescriptor.Instance),
            execute: (ctx, _) =>
            {
                ctx.SetData("Image", new ImageHandle());
                return Task.CompletedTask;
            });

    /// <summary>
    /// Threshold: consumes Image, produces Region. · 阈值分割：消费 Image，产出 Region
    /// </summary>
    public static INode Threshold(string id) =>
        new SimpleNode(id, new NodeContract("vision.threshold", 1),
            execIn: true, execOut: true,
            dataIn: ("Image", ImageDescriptor.Instance),
            dataOut: ("Region", RegionDescriptor.Instance),
            execute: (ctx, _) =>
            {
                if (ctx.GetData("Image") is not ImageHandle)
                    throw new InvalidOperationException("Grabber did not produce an Image.");
                ctx.SetData("Region", new RegionHandle());
                return Task.CompletedTask;
            });

    /// <summary>
    /// Decision: evaluates OK/NG, writes a trace-ready measurement result. 
    /// 判定节点：计算 OK/NG，写入可追溯的测量结果
    /// </summary>
    public static INode Decision(string id, double passThreshold = 1.0) =>
        new SimpleNode(id, new NodeContract("app.decision", 1),
            execIn: true, execOut: true,
            dataIn: ("Inspection", RegionDescriptor.Instance),
            dataOut: ("Judgement", ResultDescriptor.Instance),
            execute: (ctx, _) =>
            {
                var measured = Math.Abs(1.0 - ThresholdFaultProbability());
                var ok = measured >= passThreshold;
                ctx.SetData("Judgement", new Judgement(ok, measured));
                return Task.CompletedTask;
            });

    /// <summary>
    /// LogResult: emits a cycle record for the trace output (§8 prototype). 
    /// 结果输出节点：为追溯输出发出周期记录(§8 原型)
    /// </summary>
    public static INode LogResult(string id) =>
        new SimpleNode(id, new NodeContract("app.result", 1),
            execIn: true,
            dataIn: ("Judgement", ResultDescriptor.Instance),
            execute: (ctx, _) =>
            {
                var j = ctx.GetData("Judgement") as Judgement;
                ctx.SetData("LastResult", j ?? new Judgement(false, 0));
                return Task.CompletedTask;
            });

    /// <summary>
    /// Delay: simulates a long operator; honour cancellation. 
    /// 延时节点：模拟长耗时算子;遵守取消
    /// </summary>
    public static INode Delay(string id, int ms = 20) =>
        new SimpleNode(id, new NodeContract("test.delay", 1),
            execIn: true, execOut: true,
            execute: async (_, ct) => await Task.Delay(ms, ct));

    private static double ThresholdFaultProbability() => 0.03;

    /// <summary>
    /// Image handle placeholder (HObject would live here in the vision plugin). 
    /// 图像句柄占位(Halcon 节点将在此承载 HObject)
    /// </summary>
    public sealed record ImageHandle;

    /// <summary>
    /// Region handle placeholder. · 区域句柄占位
    /// </summary>
    public sealed record RegionHandle;

    /// <summary>
    /// A measurement judgement. · 一次测量判定
    /// </summary>
    public sealed record Judgement(bool Ok, double Score)
    {
        public override string ToString() => Ok ? $"OK({Score:F3})" : $"NG({Score:F3})";
    }

    /// <summary>
    /// Minimal INode implementation for the sample library. · 示例库的最小 INode 实现
    /// </summary>
    private sealed class SimpleNode : INode
    {
        private readonly List<IPort> _inputs = [];
        private readonly List<IPort> _outputs = [];

        public SimpleNode(
            string id, NodeContract contract, bool execIn = false, bool execOut = false,
            (string Name, ITypeDescriptor Type)? dataIn = null,
            (string Name, ITypeDescriptor Type)? dataOut = null,
            Func<IExecutionContext, CancellationToken, Task>? execute = null)
        {
            Id = id;
            Contract = contract;
            Execute = execute ?? ((_, _) => Task.CompletedTask);
            if (execIn) _inputs.Add(new Port(this, "exec", PortDirection.In, PortKind.Exec, null));
            if (dataIn is not null) _inputs.Add(new Port(this, dataIn.Value.Name, PortDirection.In, PortKind.Data, dataIn.Value.Type));
            if (execOut) _outputs.Add(new Port(this, "exec", PortDirection.Out, PortKind.Exec, null));
            if (dataOut is not null) _outputs.Add(new Port(this, dataOut.Value.Name, PortDirection.Out, PortKind.Data, dataOut.Value.Type));
        }

        public string Id { get; }
        public NodeContract Contract { get; }
        public IReadOnlyList<IPort> Inputs => _inputs;
        public IReadOnlyList<IPort> Outputs => _outputs;
        public NodeState State { get; private set; } = NodeState.Idle;
        private Func<IExecutionContext, CancellationToken, Task> Execute { get; }

        public async Task ExecuteAsync(IExecutionContext ctx, CancellationToken ct)
        {
            State = NodeState.Running;
            try
            {
                await Execute(ctx, ct);
                State = NodeState.Succeeded;
            }
            catch
            {
                State = NodeState.Faulted;
                throw;
            }
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