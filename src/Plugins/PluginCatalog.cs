using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Model;
using HalconWorkflow.Core.Serialization;

namespace HalconWorkflow.Plugins;

/// <summary>
/// Host-side registry filled by plugin registrars: node contract → factory, plus services.
/// Implements <see cref="INodeFactory"/> so graph loading resolves plugin-provided contracts
/// exactly like built-in layers (§10, ADR-007). Thread-safe.
/// / 宿主侧注册表，由插件注册器填充：节点契约 → 工厂，外加服务。
///   实现 <see cref="INodeFactory"/>，使图装载能像内置层一样解析插件契约（§10，ADR-007）。线程安全。
/// </summary>
public sealed class PluginCatalog : INodeFactory
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Func<string, INode?>> _nodeFactories = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, object> _services = new();
    private readonly List<NodeContract> _contracts = [];

    /// <summary>
    /// Contracts currently registered, in first-registration order. · 当前已注册契约（按首次注册顺序）。
    /// </summary>
    public IReadOnlyList<NodeContract> RegisteredContracts
    {
        get { lock (_gate) return _contracts.ToArray(); }
    }

    /// <summary>
    /// Registers (or replaces) a node factory for a contract. · 注册（或覆盖）某契约的节点工厂。
    /// </summary>
    public void AddNode(NodeContract contract, Func<string, INode?> factory)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(factory);
        lock (_gate)
        {
            var key = contract.ToString();
            if (!_nodeFactories.ContainsKey(key)) _contracts.Add(contract);
            _nodeFactories[key] = factory; // last registration wins · 后注册覆盖
        }
    }

    /// <summary>
    /// Registers (or replaces) a service by its static type. · 按静态类型注册（或覆盖）服务。
    /// </summary>
    public void AddService<TService>(TService instance) where TService : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        lock (_gate) _services[typeof(TService)] = instance;
    }

    /// <summary>
    /// Resolves a previously registered service. · 解析此前注册的服务。
    /// </summary>
    public bool TryGetService<TService>(out TService? service) where TService : class
    {
        lock (_gate)
        {
            if (_services.TryGetValue(typeof(TService), out var value) && value is TService typed)
            {
                service = typed;
                return true;
            }
        }
        service = null;
        return false;
    }

    /// <inheritdoc />
    public INode? Create(NodeContract contract, string id)
    {
        ArgumentNullException.ThrowIfNull(contract);
        Func<string, INode?>? factory;
        lock (_gate)
        {
            // NodeContract compatibility is namespace + version equality (Version is the major). · 兼容性=命名空间+版本相等（版本即 major）
            if (!_nodeFactories.TryGetValue(contract.ToString(), out factory)) return null;
        }
        return factory(id);
    }
}
