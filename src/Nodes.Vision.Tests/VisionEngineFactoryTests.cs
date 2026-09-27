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
///   稳定原生探测，不需真实机器；异常路径由"显式注册"策略驱动，无需 OpenCV SDK 即可测。
/// </summary>
[Collection("adapter-registry")]
public class VisionEngineFactoryTests
{
    private static string? NoRuntime() => null;
    // OpenCvSharp needs no path hunting: "present" just means the native runtime
    // described itself. Stub a version string rather than a filesystem location.
    // OpenCvSharp 无需探测路径："存在"即原生运行时能自我描述，故用版本串代替文件路径。
    private static string? RuntimePresent() => "OpenCV 4.13.0";

    private static void ResetRegistry() => VisionProviderRegistry.Unregister();

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
        Assert.Equal("no-provider-runtime", resolution.Reason);
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
        Assert.Equal("provider-unwired", resolution.Reason);
        Assert.Null(resolution.Detail);
    }

    [Fact]
    public void RuntimePresent_ProviderRegistered_ReturnsRealEngine()
    {
        ResetRegistry();
        VisionProviderRegistry.Register(() => new FakeVisionProvider { Provider = "OpenCvSharp" });
        try
        {
            var engine = VisionEngineFactory.CreateResolvedCore(forcePhantom: false, RuntimePresent, out var resolution);

            Assert.IsType<OpenCvVisionEngine>(engine);
            Assert.False(resolution.IsFallback);
            Assert.Equal("OpenCvSharp", resolution.Id);
            Assert.Equal("provider-active", resolution.Reason);
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
        VisionProviderRegistry.Register(() => throw new InvalidOperationException("OpenCvSharp bind boom"));
        try
        {
            var engine = VisionEngineFactory.CreateResolvedCore(forcePhantom: false, RuntimePresent, out var resolution);

            Assert.IsType<PhantomVisionEngine>(engine);
            Assert.True(resolution.IsFallback);
            Assert.Equal("provider-failed", resolution.Reason);
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
        VisionProviderRegistry.Register(() => new FakeVisionProvider { Provider = "OpenCvSharp" });
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
    public async Task ResolvedRealEngine_RoutesOpsThroughRegisteredProvider()
    {
        ResetRegistry();
        var fake = new FakeVisionProvider { Provider = "OpenCvSharp" };
        VisionProviderRegistry.Register(() => fake);
        try
        {
            var engine = VisionEngineFactory.CreateResolvedCore(forcePhantom: false, RuntimePresent, out _);
            var input = new VisionFrame(2, 2, PixFormat.Gray8, new byte[4], FrameDomain.Synthetic);
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
        VisionProviderRegistry.Register(() => new FakeVisionProvider());
        Assert.True(VisionProviderRegistry.IsRegistered);

        VisionProviderRegistry.Unregister();
        var engine = VisionEngineFactory.CreateResolvedCore(forcePhantom: false, RuntimePresent, out var resolution);

        Assert.IsType<PhantomVisionEngine>(engine);
        Assert.Equal("provider-unwired", resolution.Reason);
        Assert.False(VisionProviderRegistry.IsRegistered);
    }
}
