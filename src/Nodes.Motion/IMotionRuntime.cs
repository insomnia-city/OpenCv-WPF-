using HalconWorkflow.Abstractions;

namespace HalconWorkflow.Nodes.Motion;

/// <summary>
/// Protocol-agnostic motion runtime: resolves controllers by name. Registered as a
/// scheduler service so motion nodes stay decoupled from concrete vendor drivers.
/// / 协议无关的运动运行时：按名解析控制器。作为调度器服务注册，使运动节点与具体厂商驱动解耦。
/// </summary>
public interface IMotionRuntime
{
    /// <summary>Resolves a controller by name; null when unknown. / 按名解析控制器；未知返回 null</summary>
    IMotionController? Resolve(string controller);
}

/// <summary>
/// In-memory runtime holding named motion controllers. / 持有具名运动控制器的内存运行时
/// </summary>
public sealed class MotionRuntime : IMotionRuntime, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IMotionController> _controllers = new(StringComparer.Ordinal);

    /// <summary>Registers a controller under an explicit name. / 以显式名登记控制器</summary>
    public void Add(string name, IMotionController controller)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(controller);
        lock (_gate) _controllers[name] = controller;
    }

    /// <summary>Registers a controller under "{vendor}:{card}". / 以 "{厂商}:{卡号}" 登记控制器</summary>
    public void Add(IMotionController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        Add($"{controller.VendorId}:{controller.CardNo}", controller);
    }

    /// <summary>Removes a controller by name. / 按名移除控制器</summary>
    public bool Remove(string name)
    {
        lock (_gate) return _controllers.Remove(name);
    }

    /// <inheritdoc />
    public IMotionController? Resolve(string controller)
    {
        lock (_gate) return _controllers.TryGetValue(controller, out var c) ? c : null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        IMotionController[] all;
        lock (_gate)
        {
            all = _controllers.Values.ToArray();
            _controllers.Clear();
        }
        foreach (var c in all) await c.DisposeAsync().ConfigureAwait(false);
    }
}
