using HalconWorkflow.Abstractions;
using HalconWorkflow.Core.Model;
using SamplePlugin;
using Xunit;

namespace HalconWorkflow.Plugins.Tests;

/// <summary>
/// Stage-17 gate: the host discovers plugin registrars, loads assemblies in an isolated collectible
/// context, and never lets one bad plugin abort the host or its peers (ADR-007).
/// / 阶段17 闸门：宿主发现插件注册器、在隔离可回收上下文中装载程序集，且绝不因单个坏插件中断宿主或同伴（ADR-007）。
/// </summary>
public sealed class PluginHostTests
{
    [Fact]
    public void LoadFromRegistrars_GoodRegistrar_RegistersContractAndService()
    {
        var catalog = new PluginCatalog();

        var result = PluginHost.LoadFromRegistrars([new GoodRegistrar()], catalog);

        var plugin = Assert.Single(result.Plugins);
        Assert.Null(plugin.Error);
        Assert.Equal(1, result.ContractCount);
        Assert.NotNull(catalog.Create(new NodeContract("test.good", 1), "n1"));
        Assert.True(catalog.TryGetService<IProbeService>(out _));
    }

    [Fact]
    public void LoadFromRegistrars_ThrowingRegistrar_IsolatedAndReported()
    {
        var catalog = new PluginCatalog();

        var result = PluginHost.LoadFromRegistrars([new ThrowingRegistrar(), new GoodRegistrar()], catalog);

        Assert.Equal(2, result.Plugins.Count);
        Assert.Single(result.Failed);
        Assert.Contains("registrar boom", result.Failed.Single().Error);
        Assert.Single(result.Succeeded); // the good peer still ran · 好的同伴仍被执行
        Assert.NotNull(catalog.Create(new NodeContract("test.good", 1), "n1"));
    }

    [Fact]
    public void LoadFromAssemblies_FindsPublicRegistrar()
    {
        var catalog = new PluginCatalog();

        var result = PluginHost.LoadFromAssemblies([typeof(GoodRegistrar).Assembly], catalog);

        var plugin = Assert.Single(result.Plugins);
        Assert.Null(plugin.Error);
        Assert.Equal(1, plugin.Contracts);
    }

    [Fact]
    public void LoadDirectory_MissingDirectory_IsEmpty()
    {
        var catalog = new PluginCatalog();

        var result = PluginHost.LoadDirectory(
            Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N")), catalog);

        Assert.Empty(result.Plugins);
        Assert.Empty(catalog.RegisteredContracts);
    }

    [Fact]
    public void LoadDirectory_EmptyDirectory_IsEmpty()
    {
        var dir = NewTempDir();
        try
        {
            var result = PluginHost.LoadDirectory(dir, new PluginCatalog());
            Assert.Empty(result.Plugins);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void LoadDirectory_BogusDll_ReportsFailureWithoutThrowing()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "bogus.dll"), "this is not a managed assembly");
            var result = PluginHost.LoadDirectory(dir, new PluginCatalog());

            var failure = Assert.Single(result.Plugins);
            Assert.NotNull(failure.Error);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void LoadDirectory_EndToEnd_LoadsSamplePluginContract()
    {
        var dir = NewTempDir();
        PluginLoadResult? result = null;
        try
        {
            // Drop the externally-built sample plugin into a fresh plugins folder and load it through
            // the real collectible ALC path. Contract assemblies are shared with the host.
            // 把外部样例插件放入插件目录，经真实可回收 ALC 路径装载；契约程序集与宿主共享。
            File.Copy(typeof(SampleRegistrar).Assembly.Location, Path.Combine(dir, "SamplePlugin.dll"));
            var catalog = new PluginCatalog();

            result = PluginHost.LoadDirectory(dir, catalog);

            var plugin = Assert.Single(result.Plugins);
            Assert.Null(plugin.Error);
            Assert.Equal(1, plugin.Contracts);
            Assert.Single(result.Contexts); // one collectible ALC per plugin · 每插件一个可回收 ALC
            Assert.Equal([new NodeContract("sample.plugin", 1)], catalog.RegisteredContracts);
            var node = catalog.Create(new NodeContract("sample.plugin", 1), "n1");
            Assert.NotNull(node);
            Assert.Equal("n1", node!.Id);
        }
        finally
        {
            // The mapped DLL is released once the collectible context is collected, which is not
            // synchronous; cleanup is therefore best-effort.
            // 被映射的 DLL 在上下文被回收后释放，并非同步；故清理为尽力而为。
            result?.Unload();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            TryDelete(dir);
        }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hw-plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
