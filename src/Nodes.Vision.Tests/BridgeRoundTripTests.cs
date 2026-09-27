using HalconWorkflow.Nodes.Vision.Imaging;
using Xunit;

namespace HalconWorkflow.Nodes.Vision.Tests;

/// <summary>
/// Stage-5 gate 2: Synthetic frame↔Mat bridge must round-trip byte-identical (§6.4). Bytes
/// provenience flips only; pixels and dimensions must survive exactly.
/// / 阶段 5 闸门 2：Synthetic frame↔Mat 桥须字节往返一致（§6.4）。仅来源域翻转;像素与尺寸必须精确保持。
/// </summary>
public class BridgeRoundTripTests
{
    private static readonly Random Rng = new(77);

    private static byte[] Bytes(int count)
    {
        var b = new byte[count];
        Rng.NextBytes(b);
        return b;
    }

    [Theory]
    [InlineData(PixFormat.Gray8, 32, 18)]
    [InlineData(PixFormat.Bgr8, 41, 27)]
    [InlineData(PixFormat.Bgra8, 50, 30)]
    public void RoundTrip_IsByteIdentical_ForEveryFormat(PixFormat format, int w, int h)
    {
        var source = new VisionFrame(w, h, format, Bytes(w * h * (int)format), FrameDomain.Synthetic);

        var mat = FrameBridge.ToMat(source);
        Assert.Equal(FrameDomain.Mat, mat.Domain);
        Assert.Equal(source.Bits, mat.Bits); // zero-copy: same buffer, no pixel edit / 零拷贝：同缓冲、无像素改动
        Assert.Equal(w, mat.Width);
        Assert.Equal(h, mat.Height);

        var back = FrameBridge.ToHObject(mat);
        Assert.Equal(FrameDomain.Synthetic, back.Domain);
        Assert.True(source.ContentEquals(back)); // byte-identical round trip / 字节一致往返
    }

    [Fact]
    public void ToMat_RejectsNonHalconFrame()
    {
        var frame = new VisionFrame(4, 4, PixFormat.Gray8, Bytes(16), FrameDomain.Mat);
        Assert.Throws<InvalidOperationException>(() => FrameBridge.ToMat(frame));
    }

    [Fact]
    public void ToHObject_RejectsNonMatFrame()
    {
        var frame = new VisionFrame(4, 4, PixFormat.Gray8, Bytes(16), FrameDomain.Synthetic);
        Assert.Throws<InvalidOperationException>(() => FrameBridge.ToHObject(frame));
    }

    [Fact]
    public void Frame_ValidatesBufferLength()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new VisionFrame(4, 4, PixFormat.Bgra8, new byte[15]));
    }

    [Fact]
    public void Frame_LengthIsWidthTimesHeightTimesChannels()
    {
        var f = new VisionFrame(10, 8, PixFormat.Bgr8, new byte[240]);
        Assert.Equal(3, f.Channels);
        Assert.Equal(240, f.Bits.Length);
    }
}
