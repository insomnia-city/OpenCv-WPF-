using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Model;

namespace HalconWorkflow.Core.Graph;

/// <summary>
/// Concrete port implementation in the graph model. /* 图模型中的具体端口实现 */
/// </summary>
public sealed class GraphPort : IPort
{
    private readonly INode _owner;

    public GraphPort(INode owner, string name, PortDirection direction, PortKind kind, ITypeDescriptor? type)
    {
        _owner = owner;
        Name = name;
        Direction = direction;
        Kind = kind;
        Type = kind == PortKind.Data ? type : null;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public PortDirection Direction { get; }

    /// <inheritdoc />
    public PortKind Kind { get; }

    /// <inheritdoc />
    public ITypeDescriptor? Type { get; }

    /// <summary>
    /// Owner node reference. /* 所属节点引用 */
    /// </summary>
    public INode Owner => _owner;

    /// <inheritdoc />
    public bool IsConnected { get; set; }

    /// <inheritdoc />
    public object? Value { get; set; }

    public override string ToString() => $"{_owner.Id}:{(Direction == PortDirection.In ? "in" : "out")}:{Name}";
}