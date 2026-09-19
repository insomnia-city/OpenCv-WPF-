using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Model;

namespace HalconWorkflow.Core.Execution;

/// <summary>
/// Push avenue from graph nodes (tag triggers) back into the scheduler queue (§5.4):
/// nodes stay decoupled from the concrete scheduler; the host injects the bridge as a
/// scheduler service. · 图中节点(Tag 触发)反向进入调度队列的推送通道(§5.4):
///   节点与具体调度器解耦;宿主通过调度器服务注入桥接。
/// </summary>
public interface ITriggerNudger
{
    /// <summary>
    /// Nudges a new pulse into the scheduler queue. · 向调度队列推入一个新脉冲
    /// </summary>
    void Nudge(TriggerSource source);
}

/// <summary>
/// Default nudger forwarding to a callback (usually <c>scheduler.Trigger</c>). 
/// · 转发出回调(通常为 scheduler.Trigger)的默认推入器
/// </summary>
public sealed class ActionNudger(Action<TriggerSource> onNudge) : ITriggerNudger
{
    /// <inheritdoc />
    public void Nudge(TriggerSource source) => onNudge(source);
}