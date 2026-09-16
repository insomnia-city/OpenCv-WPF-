using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Model;
using HalconWorkflow.Core.Serialization;
using HalconWorkflow.Core.Types;

namespace HalconWorkflow.Core.Tests;

/// <summary>
/// Shared test node helpers implementing the plugin-side contracts. 
/// 实现插件侧契约的测试节点辅助
/// </summary>
internal static class TestNodes
{
    public static INode Start(string id = "start") =>
        new SimpleNode(id, new NodeContract("test.start", 1),
            execOut: true)
        {
            ExecuteImpl = async (_, _) => { await Task.CompletedTask; }
        };

    public static INode Grabber(string id = "cam") =>
        new SimpleNode(id, new NodeContract("vision.grabber", 1),
            execOut: true,
            dataOut: ("Image", ImageDescriptor.Instance))
        {
            ExecuteImpl = (ctx, _) =>
            {
                ctx.SetData("Image", new object());
                return Task.CompletedTask;
            }
        };

    public static INode TextOut(string id = "txt") =>
        new SimpleNode(id, new NodeContract("test.text", 1),
            execOut: true,
            dataOut: ("Value", StringDescriptor.Instance))
        {
            ExecuteImpl = async (_, _) => { await Task.CompletedTask; }
        };

    public static INode Threshold(string id = "threshold") =>
        new SimpleNode(id, new NodeContract("vision.threshold", 1),
            execIn: true, execOut: true,
            dataIn: ("Image", ImageDescriptor.Instance),
            dataOut: ("Region", RegionDescriptor.Instance))
        {
            ExecuteImpl = async (ctx, ct) =>
            {
                var v = ctx.GetData("Image");
                if (v is null) throw new InvalidOperationException("No image input.");
                ctx.SetData("Region", new object());
                await Task.Delay(5, ct);
            }
        };

    public static INode ResultOut(string id = "result") =>
        new SimpleNode(id, new NodeContract("test.result", 1),
            execIn: true,
            dataIn: ("Region", RegionDescriptor.Instance))
        {
            ExecuteImpl = async (_, _) => { await Task.CompletedTask; }
        };

    /// <summary>
    /// Generic node builder for tests. · 通用测试节点构造器
    /// </summary>
    private sealed class SimpleNode : INode
    {
        private readonly List<IPort> _inputs = [];
        private readonly List<IPort> _outputs = [];

        public SimpleNode(string id, NodeContract contract, bool execIn = false, bool execOut = false,
            (string Name, ITypeDescriptor Type)? dataIn = null, (string Name, ITypeDescriptor Type)? dataOut = null)
        {
            Id = id;
            Contract = contract;
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

        public Func<IExecutionContext, CancellationToken, Task>? ExecuteImpl { get; set; }

        public async Task ExecuteAsync(IExecutionContext ctx, CancellationToken ct)
        {
            State = NodeState.Running;
            if (ExecuteImpl is not null)
                await ExecuteImpl(ctx, ct);
            State = NodeState.Succeeded;
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

/// <summary>
/// Test node factory resolving the contracts used above. · 用于加载上述契约的测试节点工厂
/// </summary>
internal sealed class TestNodeFactory : INodeFactory
{
    public INode? Create(NodeContract contract, string id) => contract.Namespace switch
    {
        "test.start" => TestNodes.Start(id),
        "test.text" => TestNodes.TextOut(id),
        "vision.grabber" => TestNodes.Grabber(id),
        "vision.threshold" => TestNodes.Threshold(id),
        "test.result" => TestNodes.ResultOut(id),
        _ => null
    };
}