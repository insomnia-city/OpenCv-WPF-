using HalconWorkflow.Nodes.Vision;
using HalconWorkflow.Nodes.Vision.Adapters;
using HalconWorkflow.Nodes.Vision.Imaging;
using Xunit;

namespace HalconWorkflow.Nodes.Vision.Tests;

/// <summary>
/// Exercises the production OpenCV provider against the real x64 native runtime —
/// no mocks. This is the gate that replaced the Halcon "unvalidated scaffold": the
/// ops must actually execute, not merely be declared. If the native runtime is missing
/// for the current RID, the tests fail loudly rather than silently skipping, because a
/// silently-skipped vision gate is exactly the failure mode that reached the floor before.
/// / 以真实 x64 原生运行时验证生产 OpenCV 提供器（无 mock）。这是取代原 Halcon
///   "未验证骨架"的闸门：算子必须真正执行，而非仅仅被声明。若当前 RID 缺少原生运行时，
///   测试直接失败而非静默跳过——静默跳过的视觉闸门正是此前流到现场的那类失效。
/// </summary>
public class OpenCvVisionProviderTests
{
    private static OpenCvVisionProvider Provider() => new();

    private static VisionFrame Gray(int w, int h, Func<int, int, byte> fill)
    {
        var bits = new byte[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            bits[y * w + x] = fill(x, y);
        return new VisionFrame(w, h, PixFormat.Gray8, bits, FrameDomain.Synthetic);
    }

    [Fact]
    public void NativeRuntime_IsLoadable_OnThisRid()
    {
        var descriptor = OpenCvProbe.TryDescribeRuntime();

        Assert.True(OpenCvProbe.TryDescribeRuntime() is not null,
            $"OpenCV native runtime must load (failure: {OpenCvProbe.FailureReason ?? "none"})");
        Assert.False(string.IsNullOrWhiteSpace(descriptor));
    }

    [Fact]
    public void Provider_ReportsOpenCv_AndDoesNotClaimHdev()
    {
        using var provider = Provider();

        Assert.Equal("opencv", provider.Provider);
        Assert.True(provider.Supports("threshold"));
        Assert.True(provider.Supports("measure"));
        Assert.True(provider.Supports("grab"));
        Assert.False(provider.Supports("hdev"), "hdev was dropped: OpenCV has no HDevEngine equivalent");
    }

    [Fact]
    public async Task Threshold_AppliesTwoSidedInclusiveWindow()
    {
        using var provider = Provider();
        // values 100 (below min), 128 (== min), 200 (inside), 255 (== max), 250 (above max)
        var input = Gray(5, 1, (x, _) => x switch { 0 => 100, 1 => 128, 2 => 200, 3 => 255, _ => 250 });

        var args = new Dictionary<string, object> { ["min"] = 128, ["max"] = 255 };
        var result = Assert.IsType<VisionFrame>(
            await provider.ExecuteAsync("threshold", input, args, CancellationToken.None));

        Assert.Equal(5, result.Width);
        Assert.Equal(PixFormat.Gray8, result.Format);
        Assert.Equal(FrameDomain.Mat, result.Domain);
        // 100 below min -> 0; 128 == min -> 255; 200 -> 255; 255 == max -> 255; 250 <= 255 -> 255
        Assert.Equal(new byte[] { 0, 255, 255, 255, 255 }, result.Bits);
    }

    [Fact]
    public async Task Threshold_HonoursNarrowerMaxWindow()
    {
        using var provider = Provider();
        // A max below 255 must actually clip: this is what ThresholdTypes.Binary would
        // silently ignore, hence the regression guard.
        // max 低于 255 必须真正裁剪——这正是 ThresholdTypes.Binary 会静默忽略的行为。
        var input = Gray(3, 1, (x, _) => x switch { 0 => 200, 1 => 220, _ => 250 });

        var args = new Dictionary<string, object> { ["min"] = 128, ["max"] = 210 };
        var result = Assert.IsType<VisionFrame>(
            await provider.ExecuteAsync("threshold", input, args, CancellationToken.None));

        Assert.Equal(new byte[] { 255, 0, 0 }, result.Bits);
    }

    [Fact]
    public async Task Threshold_RejectsNonGrayInput()
    {
        using var provider = Provider();
        var input = new VisionFrame(2, 2, PixFormat.Bgr8, new byte[12], FrameDomain.Synthetic);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ExecuteAsync("threshold", input, null, CancellationToken.None));
    }

    [Fact]
    public async Task Measure_ReturnsPhantomCompatibleShape()
    {
        using var provider = Provider();
        // one bright band from x=2..5 on an 8-wide row
        var input = Gray(8, 8, (x, _) => x >= 2 && x <= 5 ? (byte)200 : (byte)30);

        var result = Assert.IsType<Dictionary<string, object>>(
            await provider.ExecuteAsync("measure", input, null, CancellationToken.None));

        Assert.Equal(2, result.Count);
        Assert.Equal(1, Convert.ToInt32(result["Edges"]));
        Assert.Equal(3, Convert.ToInt32(result["Distance"]));
    }

    [Fact]
    public async Task Measure_TwoSeparateBands_ReportTwoEdges()
    {
        using var provider = Provider();
        var input = Gray(12, 4, (x, _) => (x is 1 or 2 or 8) ? (byte)200 : (byte)10);

        var result = Assert.IsType<Dictionary<string, object>>(
            await provider.ExecuteAsync("measure", input, null, CancellationToken.None));

        Assert.Equal(2, Convert.ToInt32(result["Edges"]));
    }

    [Fact]
    public async Task Grab_WithUnopenableDevice_ThrowsInsteadOfFabricating()
    {
        using var provider = Provider();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ExecuteAsync("grab", null,
                new Dictionary<string, object> { ["index"] = 9999 }, CancellationToken.None));

        Assert.Contains("grab", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownOp_IsRejected()
    {
        using var provider = Provider();

        await Assert.ThrowsAsync<NotSupportedException>(
            () => provider.ExecuteAsync("hdev", null, null, CancellationToken.None));
    }
}
