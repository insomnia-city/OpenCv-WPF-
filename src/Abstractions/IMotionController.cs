namespace HalconWorkflow.Abstractions;

/// <summary>
/// Motion stop mode. /* 运动停止模式 */
/// </summary>
public enum StopMode
{
    Decel,
    Abort,
    EStop
}

/// <summary>
/// Named axis state snapshot. /* 命名轴状态快照 */
/// </summary>
public readonly record struct AxisState(
    int Axis,
    double Position,
    double Velocity,
    bool InPosition,
    bool Busy,
    bool Alarm);

/// <summary>
/// Semantic motion actions = graph nodes; coexists with IDeviceConnection, not merged (§7.5). 
/// 语义化运动动作 = 图节点;与 IDeviceConnection 并存不归并(§7.5)
/// </summary>
public interface IMotionController : IAsyncDisposable
{
    /// <summary>
    /// Vendor id, e.g. "googol" / "zmotion" / "leadshine". · 厂商ID
    /// </summary>
    string VendorId { get; }

    /// <summary>
    /// Card index for multi-card setups. · 多卡索引
    /// </summary>
    int CardNo { get; }

    /// <summary>
    /// Homes an axis. /* 回零 */
    /// </summary>
    Task HomeAsync(int axis, CancellationToken ct);

    /// <summary>
    /// Absolute move with velocity/acceleration. /* 绝对定位移动 */
    /// </summary>
    Task MoveAbsoluteAsync(int axis, double pos, double vel, double acc, CancellationToken ct);

    /// <summary>
    /// Relative move. /* 相对移动 */
    /// </summary>
    Task MoveRelativeAsync(int axis, double dist, double vel, CancellationToken ct);

    /// <summary>
    /// Multi-axis coordinated move via a single interpolation command. 
    /// 多轴联动单命令插补(禁止拆分拼凑)
    /// </summary>
    Task MoveLineAsync(int[] axes, double[] dest, double vel, CancellationToken ct);

    /// <summary>
    /// Cooperative poll until in position with tolerance; cancellable. 
    /// 协作式轮询到位(带公差);可取消
    /// </summary>
    Task WaitInPositionAsync(int axis, double tol, CancellationToken ct);

    /// <summary>
    /// Stops motion. EStop aborts everything. /* 停止运动;EStop 全卡急停 */
    /// </summary>
    Task StopAsync(StopMode mode);

    /// <summary>
    /// Reads current position. /* 读当前位置 */
    /// </summary>
    Task<double> ReadPositionAsync(int axis);

    /// <summary>
    /// Sets a digital output. /* 设置数字输出 */
    /// </summary>
    Task SetDigitalOutAsync(int port, bool on);

    /// <summary>
    /// Watches axis state changes; drives graph triggers. 
    /// 监视轴状态变化;驱动图触发
    /// </summary>
    IObservable<AxisState> Watch(int axis);
}