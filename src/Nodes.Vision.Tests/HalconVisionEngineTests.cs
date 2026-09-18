using HalconWorkflow.Nodes.Vision.Engines;
using HalconWorkflow.Nodes.Vision.Imaging;
using Xunit;

namespace HalconWorkflow.Nodes.Vision.Tests;

/// <summary>
/// Stage-14 gate: <see cref="HalconVisionEngine"/> is a thin façade — it must forward the
/// whole executor contract (id, op support, args/input routing, exceptions, cancellation)
/// to the adapter and dispose it exactly once. No registry and no SDK involved.
/// / 阶段 14 闸门：HalconVisionEngine 是薄门面——必须把完整执行契约（标识、操作支持、
///   参数/输入路由、异常、取消）转发给适配器，且恰好释放一次。不涉注册表与 SDK。
/// </summary>
public class HalconVisionEngineTests
{
    [Fact]
    public void Id_ComesFromAdapterProvider()
    {
        using var engine = new HalconVisionEngine(new FakeHalconAdapter { Provider = "halcondotnet" });
        Assert.Equal("halcondotnet", engine.Id);
    }

    [Fact]
    public void Constructor_NullAdapter_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new HalconVisionEngine(null!));
    }

    [Fact]
    public void Supports_DelegatesToAdapter()
    {
        var fake = new FakeHalconAdapter { SupportsAll = false };
        using var engine = new HalconVisionEngine(fake);
        Assert.False(engine.Supports("anything"));
    }

    [Fact]
    public async Task ExecuteAsync_RoutesOpInputAndArgs_ReturnsAdapterResult()
    {
        var fake = new FakeHalconAdapter();
        using var engine = new HalconVisionEngine(fake);
        var input = new VisionFrame(3, 2, PixFormat.Gray8, new byte[6], FrameDomain.Halcon);
        var args = new Dictionary<string, object> { ["min"] = 5, ["max"] = 200 };

        var result = await engine.ExecuteAsync("threshold", input, args, CancellationToken.None);

        Assert.Equal(1, fake.ExecuteCount);
        Assert.NotNull(fake.LastCall);
        Assert.Equal("threshold", fake.LastCall!.Value.Op);
        Assert.Same(input, fake.LastCall!.Value.Input);
        Assert.Same(args, fake.LastCall!.Value.Args);
        Assert.IsType<VisionFrame>(result);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesAdapterFault()
    {
        var fake = new FakeHalconAdapter { Fault = new InvalidOperationException("halcon op failed") };
        using var engine = new HalconVisionEngine(fake);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await engine.ExecuteAsync("measure", null, null, CancellationToken.None));
        Assert.Contains("halcon op failed", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_PassesCancellationTokenThrough()
    {
        var fake = new FakeHalconAdapter();
        using var engine = new HalconVisionEngine(fake);
        using var cts = new CancellationTokenSource();

        _ = await engine.ExecuteAsync("grab", null, null, cts.Token);

        Assert.Equal(cts.Token, fake.SeenToken);
    }

    [Fact]
    public void Dispose_IsIdempotent_DisposesAdapterOnce()
    {
        var fake = new FakeHalconAdapter();
        var engine = new HalconVisionEngine(fake);

        engine.Dispose();
        engine.Dispose();

        Assert.Equal(1, fake.DisposedCount);
    }
}