using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Model;

namespace HalconWorkflow.Nodes.Motion.Nodes;

/// <summary>
/// Shared INode implementation for motion nodes: declarative ports + subclass RunAsync,
/// plus the "rollback on fault" safeguard (safe decel stop before surfacing an error).
/// / 运动节点共用 INode 实现：声明式端口 + 子类 RunAsync，外加"故障回滚"保护(报错前安全减速停车)。
/// </summary>
internal abstract class MotionNodeBase : INode
{
    private readonly List<IPort> _inputs = [];
    private readonly List<IPort> _outputs = [];

    protected MotionNodeBase(string id, NodeContract contract,
        bool execIn = false, bool execOut = false,
        params (string Name, ITypeDescriptor Type, bool Optional)[] dataIn)
    {
        Id = id;
        Contract = contract;
        if (execIn) _inputs.Add(new Port(this, "exec", PortDirection.In, PortKind.Exec, null, false));
        foreach (var (name, type, optional) in dataIn)
            _inputs.Add(new Port(this, name, PortDirection.In, PortKind.Data, type, !optional));
        if (execOut) _outputs.Add(new Port(this, "exec", PortDirection.Out, PortKind.Exec, null, false));
    }

    public string Id { get; }
    public NodeContract Contract { get; }
    public IReadOnlyList<IPort> Inputs => _inputs;
    public IReadOnlyList<IPort> Outputs => _outputs;
    public NodeState State { get; private set; } = NodeState.Idle;

    protected void AddDataOut(string name, ITypeDescriptor type)
        => _outputs.Add(new Port(this, name, PortDirection.Out, PortKind.Data, type, false));

    protected abstract Task RunAsync(IExecutionContext ctx, CancellationToken ct);

    public async Task ExecuteAsync(IExecutionContext ctx, CancellationToken ct)
    {
        State = NodeState.Running;
        try
        {
            await RunAsync(ctx, ct).ConfigureAwait(false);
            State = NodeState.Succeeded;
        }
        catch
        {
            State = NodeState.Faulted;
            throw;
        }
    }

    /// <summary>
    /// Runs a motion action; on failure optionally issues a safe decel stop (rollback) so
    /// the axis is not left mid-move, then rethrows. / 执行运动动作；失败时可选安全减速停车(回滚)
    ///   避免轴停在半途，然后重抛。
    /// </summary>
    protected static async Task WithRollbackAsync(Abstractions.IMotionController controller, bool rollback, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch
        {
            if (rollback)
            {
                try
                {
                    await controller.StopAsync(Abstractions.StopMode.Decel).ConfigureAwait(false);
                }
                catch
                {
                    // rollback is best-effort; the original fault is the one surfaced · 回滚尽力而为，原故障才是要暴露的
                }
            }
            throw;
        }
    }

    private sealed class Port(INode owner, string name, PortDirection dir, PortKind kind, ITypeDescriptor? type, bool required) : IPort
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
