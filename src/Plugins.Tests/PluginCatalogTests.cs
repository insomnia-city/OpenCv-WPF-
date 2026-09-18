using HalconWorkflow.Core.Model;
using Xunit;

namespace HalconWorkflow.Plugins.Tests;

/// <summary>
/// Stage-17 unit tests: the host catalog behaves as an <c>INodeFactory</c> for plugin contracts and
/// store services with last-wins semantics. · 阶段17 单测：宿主目录作为插件契约的 INodeFactory，并以“后注册覆盖”存服务。
/// </summary>
public sealed class PluginCatalogTests
{
    [Fact]
    public void AddNode_ThenCreate_ResolvesNode()
    {
        var catalog = new PluginCatalog();
        catalog.AddNode(new NodeContract("test.good", 1), id => new GoodNode(id));

        var node = catalog.Create(new NodeContract("test.good", 1), "n1");

        Assert.NotNull(node);
        Assert.Equal("n1", node!.Id);
        Assert.Equal([new NodeContract("test.good", 1)], catalog.RegisteredContracts);
    }

    [Fact]
    public void Create_UnknownContract_ReturnsNull()
    {
        var catalog = new PluginCatalog();
        catalog.AddNode(new NodeContract("test.good", 1), id => new GoodNode(id));

        Assert.Null(catalog.Create(new NodeContract("test.other", 1), "n1"));
    }

    [Fact]
    public void Create_VersionMismatch_ReturnsNull()
    {
        var catalog = new PluginCatalog();
        catalog.AddNode(new NodeContract("test.good", 1), id => new GoodNode(id));

        Assert.Null(catalog.Create(new NodeContract("test.good", 2), "n1")); // Version is major · 版本即 major
    }

    [Fact]
    public void AddNode_DuplicateContract_LastWins_AndListedOnce()
    {
        var catalog = new PluginCatalog();
        catalog.AddNode(new NodeContract("test.good", 1), id => new GoodNode("first-" + id));
        catalog.AddNode(new NodeContract("test.good", 1), id => new GoodNode("second-" + id));

        var node = catalog.Create(new NodeContract("test.good", 1), "n");

        Assert.Equal("second-n", node!.Id);
        Assert.Single(catalog.RegisteredContracts);
    }

    [Fact]
    public void AddService_TryGetService_RoundTrips()
    {
        var catalog = new PluginCatalog();
        catalog.AddService<IProbeService>(new ProbeService());

        Assert.True(catalog.TryGetService<IProbeService>(out var service));
        Assert.Equal("probe", service!.Name);
    }

    [Fact]
    public void TryGetService_Unregistered_ReturnsFalse()
    {
        var catalog = new PluginCatalog();

        Assert.False(catalog.TryGetService<IProbeService>(out var service));
        Assert.Null(service);
    }
}
