using System.Reflection;
using System.Runtime.Loader;
using HalconWorkflow.Abstractions;

namespace HalconWorkflow.Plugins;

/// <summary>
/// Outcome of one plugin assembly load. <see cref="Error"/> is null on success.
/// / 单个插件程序集装载结果；成功时 <see cref="Error"/> 为 null。
/// </summary>
public sealed record LoadedPlugin(string Name, string Version, int Contracts, string? Error);

/// <summary>
/// Result of a host load pass, with convenience views over successes and failures. Carries the
/// collectible contexts it created so the caller can unload them (§10, ADR-007).
/// / 一次宿主装载的结果，并提供成功/失败视图。携带其创建的可回收上下文，供调用方卸载（§10，ADR-007）。
/// </summary>
public sealed record PluginLoadResult(IReadOnlyList<LoadedPlugin> Plugins, IReadOnlyList<AssemblyLoadContext> Contexts)
{
    /// <summary>Plugins that registered without error. · 无错误完成注册的插件。</summary>
    public IEnumerable<LoadedPlugin> Succeeded => Plugins.Where(p => p.Error is null);

    /// <summary>Plugins that failed to load or register. · 装载或注册失败的插件。</summary>
    public IEnumerable<LoadedPlugin> Failed => Plugins.Where(p => p.Error is not null);

    /// <summary>Total contracts contributed by successful plugins. · 成功插件贡献的契约总数。</summary>
    public int ContractCount => Plugins.Where(p => p.Error is null).Sum(p => p.Contracts);

    /// <summary>
    /// Initiates unload of every collectible context created by this pass. Mapped plugin files are
    /// released once the runtime collects the context, not synchronously (ADR-007).
    /// · 卸载本次装载创建的全部可回收上下文。被映射的插件文件在运行时回收上下文后释放，并非同步释放（ADR-007）。
    /// </summary>
    public void Unload()
    {
        foreach (var context in Contexts) context.Unload();
    }
}

/// <summary>
/// Discovers and loads plugins into a <see cref="PluginCatalog"/> (§10, ADR-007). Loading is
/// best-effort: a bad plugin is reported and skipped, never aborting the host or other plugins.
/// / 发现并把插件装入 <see cref="PluginCatalog"/>（§10，ADR-007）。装载为尽力而为：
///   坏插件被报告并跳过，绝不中断宿主或其它插件。
/// </summary>
public static class PluginHost
{
    /// <summary>Conventional sub-folder scanned at startup. · 启动时扫描的约定子目录。</summary>
    public const string DefaultFolder = "plugins";

    private static readonly string[] FrameworkPrefixes =
        ["System", "Microsoft", "netstandard", "xunit", "HalconWorkflow."];

    /// <summary>
    /// Scans a directory for plugin assemblies and loads each in its own collectible context.
    /// A missing directory yields an empty result. · 扫描目录中的插件程序集，各自装入独立可回收上下文。目录不存在返回空结果。
    /// </summary>
    public static PluginLoadResult LoadDirectory(string directory, PluginCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var results = new List<LoadedPlugin>();
        var contexts = new List<AssemblyLoadContext>();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return new PluginLoadResult(results, contexts);

        foreach (var file in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            if (!ShouldProbe(Path.GetFileNameWithoutExtension(file))) continue;
            try
            {
                var context = new PluginLoadContext(file);
                contexts.Add(context);
                var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(file));
                results.AddRange(LoadFromAssembly(assembly, catalog));
            }
            catch (Exception ex)
            {
                results.Add(new LoadedPlugin(Path.GetFileNameWithoutExtension(file), string.Empty, 0, ex.Message));
            }
        }
        return new PluginLoadResult(results, contexts);
    }

    /// <summary>
    /// Loads already-materialized assemblies (test/in-process seam). · 装载已就绪的程序集（测试/进程内接缝）。
    /// </summary>
    public static PluginLoadResult LoadFromAssemblies(IEnumerable<Assembly> assemblies, PluginCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var results = new List<LoadedPlugin>();
        foreach (var assembly in assemblies)
        {
            try
            {
                results.AddRange(LoadFromAssembly(assembly, catalog));
            }
            catch (Exception ex)
            {
                results.Add(new LoadedPlugin(assembly.GetName().Name ?? "?", string.Empty, 0, ex.Message));
            }
        }
        return new PluginLoadResult(results, []);
    }

    /// <summary>
    /// Runs a set of registrars against the catalog (pure seam; each registrar isolated).
    /// · 对目录运行一组注册器（纯接缝；每个注册器相互隔离）。
    /// </summary>
    public static PluginLoadResult LoadFromRegistrars(IEnumerable<IServiceRegistrar> registrars, PluginCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var results = new List<LoadedPlugin>();
        foreach (var registrar in registrars)
        {
            var before = catalog.RegisteredContracts.Count;
            try
            {
                registrar.Register(new PluginRegistrar(catalog));
                results.Add(new LoadedPlugin(registrar.GetType().Name, string.Empty,
                    catalog.RegisteredContracts.Count - before, null));
            }
            catch (Exception ex)
            {
                results.Add(new LoadedPlugin(registrar.GetType().Name, string.Empty,
                    catalog.RegisteredContracts.Count - before, ex.Message));
            }
        }
        return new PluginLoadResult(results, []);
    }

    private static IEnumerable<LoadedPlugin> LoadFromAssembly(Assembly assembly, PluginCatalog catalog)
    {
        var registrars = FindRegistrars(assembly);
        if (registrars.Count == 0) yield break; // not a plugin assembly · 非插件程序集

        var name = assembly.GetName();
        var before = catalog.RegisteredContracts.Count;
        var perPlugin = LoadFromRegistrars(registrars, catalog);
        var failed = perPlugin.Failed.ToList();
        yield return new LoadedPlugin(
            name.Name ?? "?",
            name.Version?.ToString() ?? string.Empty,
            catalog.RegisteredContracts.Count - before,
            failed.Count == 0 ? null : string.Join("; ", failed.Select(f => $"{f.Name}: {f.Error}")));
    }

    private static IReadOnlyList<IServiceRegistrar> FindRegistrars(Assembly assembly)
    {
        return assembly.GetTypes()
            .Where(t => t is { IsPublic: true, IsAbstract: false, IsInterface: false }
                        && typeof(IServiceRegistrar).IsAssignableFrom(t))
            .Select(t => (IServiceRegistrar)Activator.CreateInstance(t)!)
            .ToList();
    }

    private static bool ShouldProbe(string assemblyName)
        => !FrameworkPrefixes.Any(p => assemblyName.StartsWith(p, StringComparison.Ordinal));
}
