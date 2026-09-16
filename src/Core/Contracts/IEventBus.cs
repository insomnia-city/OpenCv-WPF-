namespace HalconWorkflow.Core.Contracts;

/// <summary>
/// Event bus used for structured traceability and diagnostics. /* 事件总线，用于结构化追溯与诊断 */
/// </summary>
public interface IEventBus
{
    /// <summary>
    /// Publishes an event; handlers run inline (no queueing). /* 发布事件；处理器同步内联执行（不入队） */
    /// </summary>
    void Publish(object evt);
}

/// <summary>
/// Null event bus that drops everything. /* 丢弃所有事件的空事件总线 */
/// </summary>
public sealed class NullEventBus : IEventBus
{
    /// <inheritdoc />
    public void Publish(object evt) { }
}