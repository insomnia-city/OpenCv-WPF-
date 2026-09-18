using HalconWorkflow.Abstractions;
using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Model;

namespace HalconWorkflow.Plugins;

/// <summary>
/// Bridges a plugin's <see cref="IServiceRegistrar.Register"/> callback into the host catalog (§10).
/// / 将插件的 <see cref="IServiceRegistrar.Register"/> 回调桥接到宿主目录（§10）。
/// </summary>
public sealed class PluginRegistrar(PluginCatalog catalog) : IPluginServiceRegistrar
{
    private readonly PluginCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    /// <inheritdoc />
    public void RegisterNode(NodeContract contract, Func<string, INode?> factory)
        => _catalog.AddNode(contract, factory);

    /// <inheritdoc />
    public void RegisterService<TService>(TService instance) where TService : class
        => _catalog.AddService(instance);
}
