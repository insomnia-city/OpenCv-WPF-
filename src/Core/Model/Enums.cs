namespace HalconWorkflow.Core.Model;

/// <summary>
/// Individual node runtime state. /* 单个节点的运行时状态 */
/// </summary>
public enum NodeState
{
    Idle,
    Ready,
    Running,
    Succeeded,
    Faulted,
    Cancelled
}

/// <summary>
/// Whole-graph runtime state. /* 整图运行时状态 */
/// </summary>
public enum GraphState
{
    Stopped,
    Starting,
    Running,
    Pausing,
    Faulted,
    Broken
}

/// <summary>
/// Port direction. /* 端口方向 */
/// </summary>
public enum PortDirection
{
    In,
    Out
}

/// <summary>
/// Port kind: control-flow (Exec) vs data-flow (Data). /* 端口类别：控制流(Exec) vs 数据流(Data) */
/// </summary>
public enum PortKind
{
    Exec,
    Data
}

/// <summary>
/// Trigger source of a control-flow pulse. /* 控制流脉冲的触发来源 */
/// </summary>
public enum TriggerSource
{
    Manual,
    Timer,
    TagChange,
    IoInterrupt,
    Edge,
    Internal
}

/// <summary>
/// Control-flow signal token carried through the whole chain for traceability. /* 贯穿全链路的控制流信号令牌，用于追溯与日志关联 */
/// </summary>
public sealed record Signal(
    TriggerSource Source,
    string TriggerId,
    DateTimeOffset Timestamp,
    string? Batch = null)
{
    /// <summary>
    /// Creates a fresh signal with a new unique trigger id. /* 生成带新唯一触发ID的信号 */
    /// </summary>
    public static Signal Create(TriggerSource source, string? batch = null) =>
        new(source, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, batch);
}