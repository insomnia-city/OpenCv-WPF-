using HalconWorkflow.Nodes.Vision.Adapters;
using HalconWorkflow.Nodes.Vision.Engines;
using HalconWorkflow.Nodes.Vision.Imaging;
using Xunit;

namespace HalconWorkflow.Nodes.Vision.Tests;

/// <summary>
/// Stage-14 gate: deployment-adapter resolution policy. A registered adapter factory is
/// the ONLY way the real engine is selected; every other state degrades to the phantom
/// fallback with a stable reason — resolution never throws. The machine probe is injected
/// so all four states are exercised without a Halcon SDK.
/// / 阶段 14 闸门：部署适配器解析策略。注册适配器工厂是选中真实引擎的唯一途径;其余状态一律带
///   稳定原因降级幻影——解析永不抛异常。本机探测可注入，四态在无 Halcon SDK 下全覆盖。
/// </summary>
[Collection("adapter-registry")]
public class VisionEngineFactoryTests
{
    private static string? NoRuntime() => null;
    private static string? RuntimePresent() => @"C:\fake\MVTec\HALCON-24.11\bin\x64-win64\halcondotnet.dll";

    private static void ResetRegistry() => HalconAdapterRegistry.Unregister();

    [Fact]
    public void ForcePhantom_ReturnsPhantom_WithForcedReason()
    {
        ResetRegistry();
        var engine = VisionEngineFactory.CreateResolvedCore(forcePhantom: true, RuntimePresent, out var resolution);

        Assert.IsType<PhantomVisionEngine>(engine);
        Assert.True(resolution.IsFallback);
        Assert.Equal("phantom", resolution.Id);
        Assert.Equal("forced", resolution.Reason);
    }

    [Fact]
    public void NoRuntime_FallsBackToPhantom_WithReason()
    {
        ResetRegistry();
        var engine = VisionEngineFactory.CreateResolvedCore(forcePhantom: false, NoRuntime, out var resolution);

        Assert.IsType<PhantomVisionEngine>(engine);
        Assert.True(resolution.IsFallback);
        Assert.Equal("no-halcon-runtime", resolution.Reason);
    }

    [Fact]
    public void RuntimePresent_ButNoAdapterRegistered_FallsBackToPhantom()
    {
        ResetRegistry();
        var engine = VisionEngineFactory.CreateResolvedCore(forcePhantom: false, RuntimePresent, out var resolution);

        // Previously this case threw PlatformNotSupportedException; now it degrades cleanly.
        // / 此前该状态会抛 PlatformNotSupportedException;现改为干净降级。
        Assert.IsType<PhantomVisionEngine>(engine);
        Assert.True(resolution.IsFallback);
        Assert.Equal("phantom", resolution.Id);
        Assert.Equal("halcon-unwired", resolution.Reason);
        Assert.Null(resolution.Detail);
    }

    [Fact]
    public void RuntimePresent_AdapterRegistered_ReturnsHalconEngine()
    {
        ResetRegistry();
        HalconAdapterRegistry.Register(() => new FakeHalconAdapter { Provider = "halcondotnet" });
        try
        {
            var engine = VisionEngineFactory.CreateResolvedCore(forcePhantom: false, RuntimePresent, out var resolution);

            Assert.IsType<HalconVisionEngine>(engine);
            Assert.False(resolution.IsFallback);
            Assert.Equal("halcondotnet", resolution.Id);
            Assert.Equal("halcon-adapter", resolution.Reason);
        }
        finally
        {
            ResetRegistry();
        }
    }

    [Fact]
    public void AdapterFactoryThrows_FallsBackToPhantom_WithDetail()
    {
        ResetRegistry();
        HalconAdapterRegistry.Register(() => throw new InvalidOperationException("halcondotnet bind boom"));
        try
        {
            var engine = VisionEngineFactory.CreateResolvedCore(forcePhantom: false, RuntimePresent, out var resolution);

            Assert.IsType<PhantomVisionEngine>(engine);
            Assert.True(resolution.IsFallback);
            Assert.Equal("halcon-adapter-failed", resolution.Reason);
            Assert.Contains("boom", resolution.Detail);
        }
        finally
        {
            ResetRegistry();
        }
    }

    [Fact]
    public void ForcePhantom_IgnoresRegisteredAdapter()
    {
        ResetRegistry();
        HalconAdapterRegistry.Register(() => new FakeHalconAdapter { Provider = "halcondotnet" });
        try
        {
            var engine = VisionEngineFactory.CreateResolvedCore(forcePhantom: true, RuntimePresent, out var resolution);

            Assert.IsType<PhantomVisionEngine>(engine);
            Assert.Equal("forced", resolution.Reason);
        }
        finally
        {
            ResetRegistry();
        }
    }

    [Fact]
    public async Task ResolvedHalconEngine_RoutesOpsThroughRegisteredAdapter()
    {
        ResetRegistry();
        var fake = new FakeHalconAdapter { Provider = "halcondotnet" };
        HalconAdapterRegistry.Register(() => fake);
        try
        {
            var engine = VisionEngineFactory.CreateResolvedCore(forcePhantom: false, RuntimePresent, out _);
            var input = new VisionFrame(2, 2, PixFormat.Gray8, new byte[4], FrameDomain.Halcon);
            var args = new Dictionary<string, object> { ["min"] = 10 };

            Assert.True(engine.Supports("threshold"));
            var result = await engine.ExecuteAsync("threshold", input, args, CancellationToken.None);

            Assert.Equal(1, fake.ExecuteCount);
            Assert.NotNull(fake.LastCall);
            Assert.Equal("threshold", fake.LastCall!.Value.Op);
            Assert.Same(input, fake.LastCall!.Value.Input);
            Assert.Same(args, fake.LastCall!.Value.Args);
            Assert.IsType<VisionFrame>(result);
        }
        finally
        {
            ResetRegistry();
        }
    }

    [Fact]
    public void Unregister_ThenResolveAgain_FallsBackToPhantom()
    {
        ResetRegistry();
        HalconAdapterRegistry.Register(() => new FakeHalconAdapter());
        Assert.True(HalconAdapterRegistry.IsRegistered);

        HalconAdapterRegistry.Unregister();
        var engine = VisionEngineFactory.CreateResolvedCore(forcePhantom: false, RuntimePresent, out var resolution);

        Assert.IsType<PhantomVisionEngine>(engine);
        Assert.Equal("halcon-unwired", resolution.Reason);
        Assert.False(HalconAdapterRegistry.IsRegistered);
    }
}