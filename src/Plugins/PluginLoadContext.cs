using System.Reflection;
using System.Runtime.Loader;

namespace HalconWorkflow.Plugins;

/// <summary>
/// Collectible load context per plugin assembly (§10, ADR-007): a plugin can be swapped or
/// unloaded without restarting the process. Contract assemblies are deliberately shared with the
/// default context so a plugin's <c>IServiceRegistrar</c> is identity-compatible with the host.
/// / 每个插件程序集一个可回收装载上下文（§10，ADR-007）：无需重启进程即可替换/卸载插件。
///   契约程序集刻意与默认上下文共享，使插件的 <c>IServiceRegistrar</c> 与宿主类型同一。
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly string[] SharedAssemblies =
    [
        "HalconWorkflow.Abstractions",
        "HalconWorkflow.Core"
    ];

    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath)
        : base(name: $"plugin:{Path.GetFileNameWithoutExtension(pluginPath)}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Share contract assemblies with the host; load everything else from the plugin's own folder.
        // · 契约程序集与宿主共享；其余从插件自身目录解析。
        if (assemblyName.Name is { } name && SharedAssemblies.Contains(name, StringComparer.Ordinal))
            return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is not null ? LoadFromAssemblyPath(path) : null;
    }
}
