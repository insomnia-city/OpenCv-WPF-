using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Model;

namespace HalconWorkflow.Nodes.Flow;

/// <summary>
/// Shared INode implementation for flow nodes: declarative exec/data ports + an execute delegate. 
/// 流程节点共用 INode 实现：声明式 exec/data 端口 + 执行委托
/// </summary>
internal sealed class FlowNodeBase : INode
{
    private readonly List<IPort> _inputs = [];
    private readonly List<IPort> _outputs = [];
    private readonly Func<IExecutionContext, CancellationToken, Task> _execute;

    public FlowNodeBase(
        string id,
        NodeContract contract,
        Func<IExecutionContext, CancellationToken, Task>? execute = null,
        bool execIn = false,
        bool execOut = false,
        params (string Name, ITypeDescriptor Type, bool Optional)[] dataIn)
    {
        Id = id;
        Contract = contract;
        _execute = execute ?? ((_, _) => Task.CompletedTask);
        if (execIn) _inputs.Add(new Port(this, "exec", PortDirection.In, PortKind.Exec, null, false));
        foreach (var (name, type, optional) in dataIn)
            _inputs.Add(new Port(this, name, PortDirection.In, PortKind.Data, type, !optional));
        if (execOut) _outputs.Add(new Port(this, "exec", PortDirection.Out, PortKind.Exec, null, false));
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public NodeContract Contract { get; }

    /// <inheritdoc />
    public IReadOnlyList<IPort> Inputs => _inputs;

    /// <inheritdoc />
    public IReadOnlyList<IPort> Outputs => _outputs;

    /// <inheritdoc />
    public NodeState State { get; private set; } = NodeState.Idle;

    /// <summary>Declares an extra data output. Called before the node is published to a graph. · 声明额外数据输出(节点发布到图前调用)</summary>
    public void AddDataOut(string name, ITypeDescriptor type)
        => _outputs.Add(new Port(this, name, PortDirection.Out, PortKind.Data, type, false));

    /// <inheritdoc />
    public async Task ExecuteAsync(IExecutionContext ctx, CancellationToken ct)
    {
        State = NodeState.Running;
        try
        {
            await _execute(ctx, ct);
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