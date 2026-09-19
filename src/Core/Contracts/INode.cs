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
    /// Executes the node. Must observe cancellation. · 执行节点;必须遵守取消令牌
    /// </summary>
    Task ExecuteAsync(IExecutionContext ctx, CancellationToken ct);
}

/// <summary>
/// Optional lifecycle hook for nodes holding resources bound to a scheduler run
/// (subscriptions, handles). The scheduler calls it on Stop so the node can detach
/// cleanly; a later run re-arms on first Execute. · 持有与某次调度运行绑定的资源
/// (订阅/句柄)的节点可选生命周期钩子：调度器在 Stop 时调用使节点干净解除；
/// 下次运行时首个 Execute 重新武装。
/// </summary>
public interface IStoppableNode
{
    /// <summary>
    /// Invoked by the scheduler after the trigger loop drains on stop. · 停止时触发循环排空后由调度器调用
    /// </summary>
    Task OnSchedulerStopAsync(CancellationToken ct);
}