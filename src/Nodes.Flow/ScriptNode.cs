using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Model;
using HalconWorkflow.Core.Serialization;
using HalconWorkflow.Core.Types;

namespace HalconWorkflow.Nodes.Flow;

/// <summary>
/// Script node: evaluates a safe expression against the cycle scope and emits a typed value. 
/// 脚本节点：对执行作用域求安全表达式并输出类型化值
/// </summary>
public sealed class ScriptNode : INode
{
    /// <summary>Output coercion kind. · 输出类型强制方式</summary>
    public enum OutKind { Integer, Real, String, Bool, Result }

    private readonly OutKind _kind;
    private readonly string _script;
    private NodeState _state;

    public ScriptNode(string id, string script, OutKind kind = OutKind.Result)
    {
        Id = id;
        Contract = new NodeContract("flow.script", 1);
        _script = script;
        _kind = kind;
        _state = NodeState.Idle;
        var desc = DescriptorFor(kind);
        Inputs = [new Port(this, "exec", PortDirection.In, PortKind.Exec, null)];
        Outputs = [new Port(this, "exec", PortDirection.Out, PortKind.Exec, null), new Port(this, "value", PortDirection.Out, PortKind.Data, desc)];
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public NodeContract Contract { get; }

    /// <inheritdoc />
    public IReadOnlyList<IPort> Inputs { get; }

    /// <inheritdoc />
    public IReadOnlyList<IPort> Outputs { get; }

    /// <inheritdoc />
    public NodeState State => _state;

    /// <inheritdoc />
    public async Task ExecuteAsync(IExecutionContext ctx, CancellationToken ct)
    {
        _state = NodeState.Running;
        try
        {
            var result = new ExpressionEvaluator(_script, ctx.GetData).Evaluate();
            ctx.SetData("value", ConvertFor(result, _kind));
            ctx.SetData("_script", _script);
            _state = NodeState.Succeeded;
            await Task.CompletedTask;
        }
        catch
        {
            _state = NodeState.Faulted;
            throw;
        }
    }

    /// <summary>Coerces the evaluated value into the declared output type. · 把求值结果强制转换为声明的输出类型</summary>
    public static object? ConvertFor(object? value, OutKind kind)
    {
        switch (kind)
        {
            case OutKind.Integer: return ValueCoercion.ToInt64(value);
            case OutKind.Real: return ValueCoercion.ToDouble(value);
            case OutKind.String: return ValueCoercion.ToText(value);
            case OutKind.Bool: return ValueCoercion.ToBool(value);
            default: return new FlowNodes.FlowResult(ValueCoercion.ToText(value));
        }
    }

    private static ITypeDescriptor DescriptorFor(OutKind kind) => kind switch
    {
        OutKind.Integer => IntegerDescriptor.Instance,
        OutKind.Real => RealDescriptor.Instance,
        OutKind.String => StringDescriptor.Instance,
        OutKind.Bool => BoolDescriptor.Instance,
        _ => ResultDescriptor.Instance
    };

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

/// <summary>
/// Factory resolving flow.* contracts to node instances (name+version, no CLR types). 
/// 按契约名(名称+版本)解析 flow.* 为节点实例的工厂
/// </summary>
public sealed class FlowNodeFactory : INodeFactory
{
    /// <inheritdoc />
    public INode? Create(NodeContract contract, string id)
    {
        return contract switch
        {
            { Namespace: "flow.branch" } => FlowNodes.Branch(id),
            { Namespace: "flow.join" } => FlowNodes.Join(id),
            { Namespace: "flow.counter" } => FlowNodes.Counter(id),
            { Namespace: "flow.delay" } => FlowNodes.Delay(id),
            { Namespace: "flow.script" } => new ScriptNode(id, ScriptNodeFactory.DefaultScript),
            _ => null
        };
    }
}

/// <summary>Well-known script defaults for palette spawns. · 调色板生成时的默认脚本</summary>
public static class ScriptNodeFactory
{
    public const string DefaultScript = "1.0";
    public static ScriptNode Create(string id, string script, ScriptNode.OutKind kind) => new(id, script, kind);
}