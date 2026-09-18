using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HalconWorkflow.Nodes.Vision.Imaging;

namespace HalconWorkflow.App.Services;

/// <summary>
/// Encodes a <see cref="VisionFrame"/> (raw row-major pixels) to PNG bytes for the preview ring /
/// dashboard (§9.5.1, stage-13 producer wiring). Null frame or unsupported format → null.
/// / 把 VisionFrame(裸行主序像素)编码为 PNG 字节，供预览环/看板使用(§9.5.1,阶段13 生产者接线)。
///   空帧或未知格式返回 null。
/// </summary>
public static class VisionPreviewEncoder
{
    public static byte[]? ToPng(VisionFrame? frame)
    {
        if (frame is null) return null;

        PixelFormat? format = frame.Format switch
        {
            PixFormat.Gray8 => PixelFormats.Gray8,
            PixFormat.Bgr8 => PixelFormats.Bgr24,
            PixFormat.Bgra8 => PixelFormats.Bgra32,
            _ => null
        };
        if (format is null) return null;

        var bitmap = BitmapSource.Create(
            frame.Width, frame.Height, 96, 96, format.Value, null, frame.Bits, frame.Width * frame.Channels);
        bitmap.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}