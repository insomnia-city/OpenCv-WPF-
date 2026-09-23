using HalconWorkflow.Abstractions;
using HalconWorkflow.Protocols.Devices;
using HalconWorkflow.Protocols.Modbus;
using HalconWorkflow.Protocols.S7;

namespace HalconWorkflow.Protocols;

/// <summary>
/// Composes persisted device definitions into live runtime parts: the tag table and concrete
/// adapter connections (§7.1 device catalog, stage-21). Protocol ids map to concrete adapters;
/// unknown protocols fail fast with a clear message.
/// · 将持久化设备定义组装为运行时部件:Tag 表与具体适配器连接(§7.1 设备目录,阶段21)。
///   协议 ID 映射到具体适配器;未知协议快速失败并给出明确信息。
/// </summary>
public sealed class DeviceCatalog
{
    /// <summary>Devices that require an in-process loopback simulator. · 需要进程内回环模拟器的设备</summary>
    public bool RequiresLoopback { get; }

    private readonly DeviceCatalogFile _file;

    public DeviceCatalog(DeviceCatalogFile file)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
        Validate(file);
        RequiresLoopback = file.Devices.Any(d => d.Loopback);
    }

    /// <summary>Registers every tag definition into a new tag table. · 把全部 Tag 定义注册到一个新 Tag 表</summary>
    public ITagTable BuildTagTable()
    {
        var table = new TagTable();
        foreach (var device in _file.Devices)
        {
            foreach (var tag in device.Tags)
            {
                table.Register(new TagTableEntry(
                    tag.Tag,
                    device.DeviceId,
                    tag.Address,
                    DeviceTypeNames.ToType(tag.DataType),
                    tag.Readable,
                    tag.Writable));
            }
        }
        return table;
    }

    /// <summary>
    /// Creates a concrete adapter for one profile. For loopback devices pass the simulator
    /// port chosen at start; the persisted port is ignored then.
    /// · 为单个 profile 创建适配器。回环设备请传入启动时选择的模拟器端口(持久化端口此时被忽略)。
    /// </summary>
    public IDeviceConnection CreateConnection(DeviceProfile profile, ITagTable tags, int? loopbackPort = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(tags);
        return profile.Protocol switch
        {
            "modbus-tcp" => new ModbusTcpConnection(
                profile.DeviceId,
                profile.Host,
                profile.Loopback ? (loopbackPort ?? profile.Port) : profile.Port,
                profile.UnitId,
                tags,
                TimeSpan.FromMilliseconds(profile.PollIntervalMs),
                new ReconnectOptions
                {
                    HeartbeatMs = profile.HeartbeatMs,
                    InitialDelayMs = 200,
                    MaxDelayMs = 5000,
                    Multiplier = 2.0,
                    MaxAttempts = 0
                }),
            "s7" => new S7TcpConnection(
                profile.DeviceId,
                profile.Host,
                profile.Loopback ? (loopbackPort ?? profile.Port) : profile.Port,
                tags,
                TimeSpan.FromMilliseconds(profile.PollIntervalMs),
                new ReconnectOptions
                {
                    HeartbeatMs = profile.HeartbeatMs,
                    InitialDelayMs = 200,
                    MaxDelayMs = 5000,
                    Multiplier = 2.0,
                    MaxAttempts = 0
                }),
            _ => throw new NotSupportedException(
                $"Unknown device protocol '{profile.Protocol}' for '{profile.DeviceId}'. " +
                $"Supported: modbus-tcp, s7. / 不支持的协议 '{profile.Protocol}' (设备 '{profile.DeviceId}')。支持:modbus-tcp、s7。")
        };
    }

    /// <summary>Default in-process demo catalog (matches the legacy hard-coded demo profile). · 默认进程内演示目录(兼容旧硬编码演示)</summary>
    public static DeviceCatalogFile CreateDemoCatalog()
    {
        var device = new DeviceProfile
        {
            DeviceId = "demo",
            Protocol = "modbus-tcp",
            Host = "127.0.0.1",
            Port = 502,
            UnitId = 1,
            PollIntervalMs = 200,
            HeartbeatMs = 200,
            Loopback = true
        };
        device.Tags.Add(new TagProfile { Tag = "demo/holding/speed", Address = "holding:0", DataType = "ushort", Readable = true, Writable = true });
        device.Tags.Add(new TagProfile { Tag = "demo/holding/count", Address = "holding:1", DataType = "ushort", Readable = true, Writable = true });
        device.Tags.Add(new TagProfile { Tag = "demo/coil/run", Address = "coil:0", DataType = "bool", Readable = true, Writable = true });
        device.Tags.Add(new TagProfile { Tag = "demo/input/temp", Address = "input:0", DataType = "ushort", Readable = true });
        device.Tags.Add(new TagProfile { Tag = "demo/discrete/ready", Address = "discrete:0", DataType = "bool", Readable = true });
        return new DeviceCatalogFile { Devices = { device } };
    }

    private static void Validate(DeviceCatalogFile file)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in file.Devices)
        {
            if (string.IsNullOrEmpty(device.DeviceId))
                throw new ArgumentException("Device catalog contains a device without DeviceId. · 设备目录中存在缺少 DeviceId 的设备。");
            if (!seen.Add(device.DeviceId))
                throw new ArgumentException(
                    $"Duplicate device id '{device.DeviceId}'. · 重复的设备 ID '{device.DeviceId}'。");
            var tagSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in device.Tags)
            {
                if (string.IsNullOrEmpty(tag.Tag) || !tag.Tag.StartsWith(device.DeviceId + "/", StringComparison.Ordinal))
                    throw new ArgumentException(
                        $"Tag '{tag.Tag}' must be under device id '{device.DeviceId}'. · Tag '{tag.Tag}' 必须位于设备 '{device.DeviceId}' 之下。");
                if (!tagSeen.Add(tag.Tag))
                    throw new ArgumentException($"Duplicate tag '{tag.Tag}'. · 重复的 Tag '{tag.Tag}'。");
            }
        }
    }
}