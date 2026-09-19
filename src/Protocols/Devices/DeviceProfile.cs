namespace HalconWorkflow.Protocols.Devices;

/// <summary>
/// One device connection definition as persisted to the device catalog (§7.1, stage-21).
/// Protocol-agnostic: the shell maps protocol ids to concrete adapters.
/// · 持久化到设备目录的设备连接定义(§7.1,阶段21)。与协议无关:壳层把协议 id 映射到具体适配器。
/// </summary>
public sealed class DeviceProfile
{
    /// <summary>Stable device id within the host, referenced by tags and graphs. · 主机内稳定设备ID,被 Tag 与图引用</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>Adapter protocol id, e.g. "modbus-tcp". · 适配器协议 ID,如 "modbus-tcp"</summary>
    public string Protocol { get; set; } = "";

    /// <summary>Host address of the controller. · 控制器主机地址</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>TCP port of the controller. · 控制器端口</summary>
    public int Port { get; set; } = 502;

    /// <summary>Modbus unit/slave id (modbus-only). · Modbus 单元号/从站号(modbus 专用)</summary>
    public byte UnitId { get; set; } = 1;

    /// <summary>Change-poll cadence in ms. · 变化轮询周期(ms)</summary>
    public int PollIntervalMs { get; set; } = 200;

    /// <summary>Heartbeat probe cadence in ms; 0 = off. · 心跳探测周期(ms);0 关闭</summary>
    public int HeartbeatMs { get; set; } = 0;

    /// <summary>
    /// True when this device is the in-process loopback simulator (ephemeral port picked at
    /// each start; address itself is not persisted meaningfully). · 是否为进程内回环模拟器
    /// (每次启动端口随机;端口本身无持久化意义)
    /// </summary>
    public bool Loopback { get; set; }

    /// <summary>Tags the device offers. · 设备提供的 Tag 清单</summary>
    public List<TagProfile> Tags { get; set; } = [];
}

/// <summary>One tag definition of a device catalog entry. · 设备目录条目中的一个 Tag 定义</summary>
public sealed class TagProfile
{
    /// <summary>Tag path (device/area/name) referenced by graphs and the tag table. · 图与 Tag 表引用的路径(device/area/name)</summary>
    public string Tag { get; set; } = "";

    /// <summary>Protocol/low-level address, e.g. "holding:0". · 协议底层地址,如 "holding:0"</summary>
    public string Address { get; set; } = "";

    /// <summary>CLR type name resolved by <see cref="DeviceTypeNames"/>. · 由 DeviceTypeNames 解析的 CLR 类型名</summary>
    public string DataType { get; set; } = "ushort";

    /// <summary>Can the graph read this tag. · 图是否可读该 Tag</summary>
    public bool Readable { get; set; } = true;

    /// <summary>Can the graph write this tag. · 图是否可写该 Tag</summary>
    public bool Writable { get; set; }

    /// <summary>Optional scale/alias expression. · 可选缩放/别名表达式</summary>
    public string? Scale { get; set; }
}

/// <summary>Root of the persisted device catalog. · 持久化设备目录的根对象</summary>
public sealed class DeviceCatalogFile
{
    public const int CurrentVersion = 1;
    public int Version { get; set; } = CurrentVersion;
    public List<DeviceProfile> Devices { get; set; } = [];
}

/// <summary>
/// Maps storage-friendly type names to CLR types and back (no assembly-qualified names).
/// · 存储友好类型名 ↔ CLR 类型双向映射(不使用程序集限定名)
/// </summary>
public static class DeviceTypeNames
{
    private static readonly Dictionary<string, Type> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bool"] = typeof(bool),
        ["byte"] = typeof(byte),
        ["short"] = typeof(short),
        ["ushort"] = typeof(ushort),
        ["int"] = typeof(int),
        ["uint"] = typeof(uint),
        ["long"] = typeof(long),
        ["ulong"] = typeof(ulong),
        ["float"] = typeof(float),
        ["double"] = typeof(double),
        ["string"] = typeof(string)
    };

    private static readonly Dictionary<Type, string> Reverse = Map.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static Type ToType(string name) =>
        Map.TryGetValue(name ?? "", out var t) ? t : typeof(ushort);

    public static string FromType(Type type) =>
        Reverse.TryGetValue(type, out var name) ? name : "ushort";
}