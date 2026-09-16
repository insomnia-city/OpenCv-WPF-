using HalconWorkflow.Core.Model;

namespace HalconWorkflow.Core.Contracts;

/// <summary>
/// A node in the graph. Implementations live in plugins. /* 图中的节点;实现全部在插件侧 */
/// </summary>
public interface INode
{
    /// <summary>
    /// Unique instance id inside the graph. /* 图内唯一实例ID */
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Contract identity used for serialization and plugin lookup. /* 序列化与插件查找用的契约身份 */
    /// </summary>
    NodeContract Contract { get; }

    /// <summary>
    /// Input ports (Exec + Data). /* 输入端口(控制流+数据) */
    /// </summary>
    IReadOnlyList<IPort> Inputs { get; }

    /// <summary>
    /// Output ports (Exec + Data). /* 输出端口(控制流+数据) */
    /// </summary>
    IReadOnlyList<IPort> Outputs { get; }

    /// <summary>
    /// Current node state. /* 当前节点状态 */
    /// </summary>
    NodeState State { get; }

    /// <summary>
    /// Executes the node. Must observe cancellation. /* 执行节点;必须遵守取消令牌 */
    /// </summary>
    Task ExecuteAsync(IExecutionContext ctx, CancellationToken ct);
}