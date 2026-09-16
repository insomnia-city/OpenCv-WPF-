using HalconWorkflow.Core.Model;

namespace HalconWorkflow.Core.Execution;

/// <summary>
/// Signals emitted by the scheduler, consumed by UI/diagnostics. Carries the cycle token (§5.1). 
/// 调度器发出的事件，供 UI/诊断消费;携带信号令牌(§5.1)
/// </summary>
public sealed record NodeExecutionEvent(
    string NodeId,
    Signal Signal,
    NodeExecutionPhase Phase,
    long ElapsedMs,
    string? Error = null);

/// <summary>
/// Per-node execution phase. · 节点执行阶段
/// </summary>
public enum NodeExecutionPhase
{
    Started,
    Completed,
    Faulted,
    Cancelled
}

/// <summary>
/// Execution result of a single run; carries the originating signal for tracing. 
/// 单轮执行结果;携带来源信号以便追溯
/// </summary>
public sealed record GraphRunResult(
    bool Success,
    Signal Signal,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? Error,
    int SucceededNodes,
    int FaultedNodes)
{
    /// <summary>
    /// Total wall time of the run. · 本轮总耗时
    /// </summary>
    public TimeSpan Duration => FinishedAt is null ? TimeSpan.Zero : FinishedAt.Value - StartedAt;
}