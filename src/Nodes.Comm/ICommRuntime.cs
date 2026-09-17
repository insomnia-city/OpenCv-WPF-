using HalconWorkflow.Abstractions;

namespace HalconWorkflow.Nodes.Comm;

/// <summary>
/// Protocol-agnostic communication runtime: resolves connections and provides the
/// shared tag table. Registered as a scheduler service so comm nodes stay decoupled
/// from concrete adapters. / 协议无关的通讯运行时：解析连接并提供共享 Tag 表。
///   作为调度器服务注册，使通讯节点与具体适配器解耦。
/// </summary>
public interface ICommRuntime
{
    /// <summary>Shared tag table used for device addressing. / 用于设备寻址的共享 Tag 表</summary>
    ITagTable Tags { get; }

    /// <summary>Resolves a connection by device id; null when unknown. / 按设备ID解析连接；未知返回 null</summary>
    IDeviceConnection? Resolve(string deviceId);
}

/// <summary>
/// In-memory runtime holding registered connections. / 持有已注册连接的内存运行时
/// </summary>
public sealed class CommRuntime : ICommRuntime, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IDeviceConnection> _devices = new(StringComparer.Ordinal);

    public CommRuntime(ITagTable tags) => Tags = tags ?? throw new ArgumentNullException(nameof(tags));

    public ITagTable Tags { get; }

    public void Add(IDeviceConnection device)
    {
        ArgumentNullException.ThrowIfNull(device);
        lock (_gate) _devices[device.DeviceId] = device;
    }

    public bool Remove(string deviceId)
    {
        lock (_gate) return _devices.Remove(deviceId);
    }

    public IDeviceConnection? Resolve(string deviceId)
    {
        lock (_gate) return _devices.TryGetValue(deviceId, out var c) ? c : null;
    }

    public async ValueTask DisposeAsync()
    {
        IDeviceConnection[] conns;
        lock (_gate)
        {
            conns = _devices.Values.ToArray();
            _devices.Clear();
        }
        foreach (var c in conns) await c.DisposeAsync().ConfigureAwait(false);
    }
}