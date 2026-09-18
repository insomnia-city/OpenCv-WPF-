using HalconWorkflow.Nodes.Vision.Adapters;
using Xunit;

namespace HalconWorkflow.Nodes.Vision.Tests;

/// <summary>
/// Stage-14 gate: the process-wide adapter registry (deployment seam). Registration is
/// explicit and overwriting; TryCreate reports "no factory" separately from "factory threw".
/// / 阶段 14 闸门：进程级适配器注册表（部署接缝）。注册显式且后覆盖先;
///   TryCreate 区分「无工厂」与「工厂抛异常」两种失败。
/// </summary>
[Collection("adapter-registry")]
public class HalconAdapterRegistryTests
{
    [Fact]
    public void Register_NullFactory_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => HalconAdapterRegistry.Register(null!));
    }

    [Fact]
    public void Register_ThenTryCreate_YieldsAdapterFromFactory()
    {
        HalconAdapterRegistry.Unregister();
        var fake = new FakeHalconAdapter();
        HalconAdapterRegistry.Register(() => fake);
        try
        {
            Assert.True(HalconAdapterRegistry.IsRegistered);
            Assert.True(HalconAdapterRegistry.TryCreate(out var adapter, out var detail));
            Assert.Same(fake, adapter);
            Assert.Null(detail);
        }
        finally
        {
            HalconAdapterRegistry.Unregister();
        }
    }

    [Fact]
    public void LatestRegistration_Wins()
    {
        HalconAdapterRegistry.Unregister();
        var first = new FakeHalconAdapter { Provider = "first" };
        var second = new FakeHalconAdapter { Provider = "second" };
        HalconAdapterRegistry.Register(() => first);
        HalconAdapterRegistry.Register(() => second);
        try
        {
            Assert.True(HalconAdapterRegistry.TryCreate(out var adapter, out _));
            Assert.Same(second, adapter);
        }
        finally
        {
            HalconAdapterRegistry.Unregister();
        }
    }

    [Fact]
    public void FactoryThrows_TryCreateReportsDetail()
    {
        HalconAdapterRegistry.Unregister();
        HalconAdapterRegistry.Register(() => throw new ApplicationException("adapter ctor failed"));
        try
        {
            Assert.False(HalconAdapterRegistry.TryCreate(out var adapter, out var detail));
            Assert.Null(adapter);
            Assert.Contains("adapter ctor failed", detail);
        }
        finally
        {
            HalconAdapterRegistry.Unregister();
        }
    }

    [Fact]
    public void Unregister_ClearsState()
    {
        HalconAdapterRegistry.Unregister();
        HalconAdapterRegistry.Register(() => new FakeHalconAdapter());
        HalconAdapterRegistry.Unregister();

        Assert.False(HalconAdapterRegistry.IsRegistered);
        Assert.False(HalconAdapterRegistry.TryCreate(out var adapter, out var detail));
        Assert.Null(adapter);
        Assert.Null(detail);
    }
}