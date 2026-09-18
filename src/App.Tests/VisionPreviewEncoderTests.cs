using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HalconWorkflow.App.Services;
using HalconWorkflow.Nodes.Vision.Imaging;
using Xunit;

namespace HalconWorkflow.App.Tests;

/// <summary>
/// Stage-13 unit tests: VisionFrame → PNG encoder feeding the preview ring / dashboard.
/// / 阶段13 单测：VisionFrame → PNG 编码器(预览环/看板生产者)
/// </summary>
public sealed class VisionPreviewEncoderTests
{
    private static readonly byte[] PngMagic = [137, 80, 78, 71, 13, 10, 26, 10];

    [Fact]
    public void ToPng_Gray8_RoundTrips()
    {
        var frame = new VisionFrame(4, 4, PixFormat.Gray8,
            [0, 64, 128, 255, 10, 20, 30, 40, 1, 2, 3, 4, 200, 100, 50, 25], FrameDomain.Mat);
        var png = VisionPreviewEncoder.ToPng(frame);

        Assert.NotNull(png);
        Assert.True(png.Length > 50, $"png too small: {png.Length}");
        Assert.Equal(PngMagic, png[..8]);

        var decoded = Decode(png);
        Assert.Equal(4, decoded.PixelWidth);
        Assert.Equal(4, decoded.PixelHeight);
        Assert.Equal(PixelFormats.Gray8, decoded.Format);
    }

    [Fact]
    public void ToPng_MultiChannelFormats_Encode()
    {
        Assert.NotNull(VisionPreviewEncoder.ToPng(new VisionFrame(2, 2, PixFormat.Bgr8, new byte[2 * 2 * 3], FrameDomain.Halcon)));
        Assert.NotNull(VisionPreviewEncoder.ToPng(new VisionFrame(2, 2, PixFormat.Bgra8, new byte[2 * 2 * 4], FrameDomain.Halcon)));
    }

    [Fact]
    public void ToPng_Null_IsNull()
    {
        Assert.Null(VisionPreviewEncoder.ToPng(null));
    }

    private static BitmapSource Decode(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return decoder.Frames[0];
    }
}