namespace HalconWorkflow.Abstractions;

/// <summary>
/// Protocol-agnostic device connection: tag addressing + read/write primitives (§7). 
/// 协议无关设备连接：Tag 寻址 + 读写原语(§7)
/// </summary>
public interface IDeviceConnection : IAsyncDisposable
{
    /// <summary>
    /// Adapter protocol id, e.g. "modbus" / "s7" / "opcua". /* 适配器协议ID */
    /// </summary>
    string ProtocolId { get; }

    /// <summary>
    /// Stable device id within the host. /* 主机内稳定的设备ID */
    /// </summary>
    string DeviceId { get; }

    /// <summary>
    /// Current connection state. /* 当前连接状态 */
    /// </summary>
    ConnectionState State { get; }

    /// <summary>
    /// Establishes the connection. /* 建立连接 */
    /// </summary>
    Task ConnectAsync(CancellationToken ct);

    /// <summary>
    /// Reads a value by tag. Tag syntax: device/area/name. 
    /// 按 Tag 读值。Tag 语法：device/area/name
    /// </summary>
    Task<object> ReadAsync(string tag, CancellationToken ct);

    /// <summary>
    /// Writes a value by tag. /* 按 Tag 写值 */
    /// </summary>
    Task WriteAsync(string tag, object value, CancellationToken ct);

    /// <summary>
    /// Subscribes to change events for tags matching the pattern; fires graph triggers. 
    /// 订阅匹配 Tag 的变化事件;驱动图触发
    /// </summary>
    IObservable<TagValue> Subscribe(string tagsPattern);
}

/// <summary>
/// Connection state machine states (§7.2). · 连接状态机状态(§7.2)
/// </summary>
public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting
}

/// <summary>
/// Data quality for tag values. /* Tag 值的数据质量 */
/// </summary>
public enum Quality
{
    Bad = 0,
    Uncertain,
    Good,
    Stale
}

/// <summary>
/// A tag value with timestamp and quality. /* 携带时间戳与质量的 Tag 值 */
/// </summary>
public readonly record struct TagValue(
    string Tag,
    object? Value,
    DateTimeOffset Timestamp,
    Quality Quality);

/// <summary>
/// Tag table entry: device + protocol address + type + permissions (§7.1). 
/// Tag 表项：设备+协议地址+类型+权限(§7.1)
/// </summary>
public sealed record TagTableEntry(
    string Tag,               // device/area/name · 设备/区/名
    string DeviceId,
    string ProtocolAddress,   // adapter-local address · 适配器本地地址
    Type DataType,
    bool Readable,
    bool Writable,
    string? Scale = null,     // scale/alias expression · 缩放/别名表达式
    string? ActiveGraphId = null);

/// <summary>
/// Tag table independent from graphs; swapping a recipe/layout does not touch the graph. 
/// 独立于图的 Tag 表;换型不碰图
/// </summary>
public interface ITagTable
{
    /// <summary>
    /// Resolves protocol/low-level address for a tag; null when unknown. 
    /// 解析 Tag 的协议底层地址;未知返回 null
    /// </summary>
    TagTableEntry? Resolve(string tag);

    /// <summary>
    /// Registers or updates an entry. /* 登记或更新条目 */
    /// </summary>
    void Register(TagTableEntry entry);

    /// <summary>
    /// All registered entries. /* 全部已登记条目 */
    /// </summary>
    IEnumerable<TagTableEntry> All();
}