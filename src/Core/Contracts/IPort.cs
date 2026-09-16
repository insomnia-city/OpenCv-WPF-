using HalconWorkflow.Core.Model;

namespace HalconWorkflow.Core.Contracts;

/// <summary>
/// A port attached to a node. Data ports carry value references, never bindings. 
/// 节点上的端口;数据端口传递引用，绝不参与绑定
/// </summary>
public interface IPort
{
    /// <summary>
    /// Owner node instance. /* 所属节点实例 */
    /// </summary>
    INode Owner { get; }

    /// <summary>
    /// Port name, unique within the node. /* 端口名，节点内唯一 */
    /// </summary>
    string Name { get; }

    /// <summary>
    /// In / Out. /* 入/出 */
    /// </summary>
    PortDirection Direction { get; }

    /// <summary>
    /// Exec or Data. /* 控制流或数据流 */
    /// </summary>
    PortKind Kind { get; }

    /// <summary>
    /// Data type descriptor; null for Exec ports. /* 数据类型描述;控制流端口为 null */
    /// </summary>
    ITypeDescriptor? Type { get; }

    /// <summary>
    /// Whether a link is attached. Settable by the graph model. 
    /// 是否已连线；由图模型设置
    /// </summary>
    bool IsConnected { get; set; }

    /// <summary>
    /// Value reference passed by data ports (not used in bindings). 
    /// 数据端口传递的引用(不参与绑定)
    /// </summary>
    object? Value { get; set; }
}