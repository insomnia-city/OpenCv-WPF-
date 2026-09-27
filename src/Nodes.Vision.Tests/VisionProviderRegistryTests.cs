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
public class VisionProviderRegistryTests
{
    [Fact]
    public void Register_NullFactory_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => VisionProviderRegistry.Register(null!));
    }

    [Fact]
    public void Register_ThenTryCreate_YieldsAdapterFromFactory()
    {
        VisionProviderRegistry.Unregister();
        var fake = new FakeVisionProvider();
        VisionProviderRegistry.Register(() => fake);
        try
        {
            Assert.True(VisionProviderRegistry.IsRegistered);
            Assert.True(VisionProviderRegistry.TryCreate(out var adapter, out var detail));
            Assert.Same(fake, adapter);
            Assert.Null(detail);
        }
        finally
        {
            VisionProviderRegistry.Unregister();
        }
    }

    [Fact]
    public void LatestRegistration_Wins()
    {
        VisionProviderRegistry.Unregister();
        var first = new FakeVisionProvider { Provider = "first" };
        var second = new FakeVisionProvider { Provider = "second" };
        VisionProviderRegistry.Register(() => first);
        VisionProviderRegistry.Register(() => second);
        try
        {
            Assert.True(VisionProviderRegistry.TryCreate(out var adapter, out _));
            Assert.Same(second, adapter);
        }
        finally
        {
            VisionProviderRegistry.Unregister();
        }
    }

    [Fact]
    public void FactoryThrows_TryCreateReportsDetail()
    {
        VisionProviderRegistry.Unregister();
        VisionProviderRegistry.Register(() => throw new ApplicationException("adapter ctor failed"));
        try
        {
            Assert.False(VisionProviderRegistry.TryCreate(out var adapter, out var detail));
            Assert.Null(adapter);
            Assert.Contains("adapter ctor failed", detail);
        }
        finally
        {
            VisionProviderRegistry.Unregister();
        }
    }

    [Fact]
    public void Unregister_ClearsState()
    {
        VisionProviderRegistry.Unregister();
        VisionProviderRegistry.Register(() => new FakeVisionProvider());
        VisionProviderRegistry.Unregister();

        Assert.False(VisionProviderRegistry.IsRegistered);
        Assert.False(VisionProviderRegistry.TryCreate(out var adapter, out var detail));
        Assert.Null(adapter);
        Assert.Null(detail);
    }
}