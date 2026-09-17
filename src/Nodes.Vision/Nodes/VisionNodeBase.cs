using HalconWorkflow.Abstractions;
using HalconWorkflow.Abstractions.Parameters;
using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Model;
using HalconWorkflow.Nodes.Vision.Engines;

namespace HalconWorkflow.Nodes.Vision.Nodes;

/// <summary>
/// Shared INode implementation for vision nodes: declarative ports + engine-backed
/// <see cref="RunAsync"/>. Each execute borrows an engine from <see cref="IVisionEnginePool"/>
/// (registered as a scheduler service) and auto-returns it when the scope exits.
/// / 视觉节点共用 INode 实现：声明式端口 + 依赖引擎的 RunAsync。每次执行从 IVisionEnginePool
///   借出引擎（调度器服务注册），作用域退出自动归还。
/// </summary>
internal abstract class VisionNodeBase : INode
{
    private readonly List<IPort> _inputs = [];
    private readonly List<IPort> _outputs = [];

    protected VisionNodeBase(string id, NodeContract contract,
        bool execIn = false, bool execOut = false,
        params (string Name, ITypeDescriptor Type, bool Optional)[] dataIn)
    {
        Id = id;
        Contract = contract;
        if (execIn) _inputs.Add(new Port(this, "exec", PortDirection.In, PortKind.Exec, null));
        foreach (var (name, type, optional) in dataIn)
            _inputs.Add(new Port(this, name, PortDirection.In, PortKind.Data, type, !optional));
        if (execOut) _outputs.Add(new Port(this, "exec", PortDirection.Out, PortKind.Exec, null));
    }

    public string Id { get; }
    public NodeContract Contract { get; }

    public IReadOnlyList<IPort> Inputs => _inputs;
    public IReadOnlyList<IPort> Outputs => _outputs;

    public NodeState State { get; private set; } = NodeState.Idle;

    /// <summary>Declares a data output before the node is published. / 发布前声明数据输出</summary>
    protected void AddDataOut(string name, ITypeDescriptor type)
        => _outputs.Add(new Port(this, name, PortDirection.Out, PortKind.Data, type));

    /// <summary>Runs the node body against a borrowed engine. / 使用借出的引擎执行节点主体</summary>
    protected abstract Task RunAsync(IExecutionContext ctx, IVisionEngine engine, CancellationToken ct);

    public async Task ExecuteAsync(IExecutionContext ctx, CancellationToken ct)
    {
        State = NodeState.Running;
        try
        {
            // Borrow per-node and return when the node body finishes (the cycle scope
            // would hold leases until the whole cycle ends and deadlock capacity-1 chains).
            // / 按节点借出、节点主体结束即归还（周期级作用域会持有到整轮结束，容量 1 链路会死锁）。
            var pool = ctx.GetService<IVisionEnginePool>();
            using var lease = await pool.BorrowAsync(ct).ConfigureAwait(false);
            await RunAsync(ctx, (IVisionEngine)lease.Engine, ct).ConfigureAwait(false);
            State = NodeState.Succeeded;
        }
        catch
        {
            State = NodeState.Faulted;
            throw;
        }
    }

    private sealed class Port(INode owner, string name, PortDirection dir, PortKind kind, ITypeDescriptor? type, bool required = true) : IPort
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