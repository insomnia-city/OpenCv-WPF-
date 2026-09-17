using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Model;

namespace HalconWorkflow.Nodes.Comm.Nodes;

/// <summary>
/// Shared INode implementation for comm nodes: declarative ports + subclass RunAsync. 
/// / 通讯节点共用 INode 实现：声明式端口 + 子类 RunAsync。
/// </summary>
internal abstract class CommNodeBase : INode
{
    private readonly List<IPort> _inputs = [];
    private readonly List<IPort> _outputs = [];

    protected CommNodeBase(string id, NodeContract contract,
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