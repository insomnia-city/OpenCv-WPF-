using HalconWorkflow.Core.Model;

namespace HalconWorkflow.Core.Contracts;

/// <summary>
/// Execution context handed to a node when it executes. /* 节点执行时获得的执行上下文 */
/// </summary>
public interface IExecutionContext
{
    /// <summary>
    /// Resolves data by intended tag name. /* 按 tag 名查找作用域内数据 */
    /// </summary>
    object? GetData(string tag);

    /// <summary>
    /// Stores a value into the scope. /* 将值写入作用域 */
    /// </summary>
    void SetData(string tag, object? value);

    /// <summary>
    /// Resource scope: register IDisposables (HObject pool, snapshots...), auto-release on exit. /* 资源作用域：登记 IDisposable(HObject 池/快照等)，出作用域自动释放 */
    /// </summary>
    IScope Scope { get; }

    /// <summary>
    /// Event bus for logging and traceability pulses. /* 事件总线，用于日志与追溯 */
    /// </summary>
    IEventBus Events { get; }

    /// <summary>
    /// Current control-flow signal token. /* 当前控制流信号令牌 */
    /// </summary>
    Signal Current { get; }

    /// <summary>
    /// Service locator: camera handle, connection, TagTable... /* 服务定位：相机句柄/连接/TagTable 等 */
    /// </summary>
    T GetService<T>() where T : class;
}