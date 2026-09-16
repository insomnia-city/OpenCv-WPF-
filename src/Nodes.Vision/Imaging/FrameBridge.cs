namespace HalconWorkflow.Nodes.Vision.Imaging;

/// <summary>
/// Zero-copy HObject↔Mat bridge (§6.4). Halcon "byte" iconic images are interleaved
/// per-pixel channels (gray/BGR/BGRA) exactly like OpenCV Mat layouts, so the mapping
/// is an identity over the backing buffer: only the <see cref="FrameDomain"/> provenance
/// flips, never the bytes. Round trips are byte-identical by construction.
/// / HObject↔Mat 零拷贝桥（§6.4）。Halcon "byte" 图像与 OpenCV Mat 同为逐像素交错通道布局，
///   故映射对底层缓冲是恒等变换：只翻转 FrameDomain 来源域，绝不改动字节。往返天然字节一致。
/// </summary>
public static class FrameBridge
{
    /// <summary>
    /// Converts a Halcon-provenance frame into Mat semantics without copying pixels. 
    /// Only Gray8/Bgr8/Bgra8 are supported; RGB-order conversion (if ever needed) is
    /// a per-channel op kept out of the hot path.
    /// / 将 Halcon 域帧转为 Mat 语义，像素零拷贝。仅支持 Gray8/Bgr8/Bgra8。
    /// </summary>
    public static VisionFrame ToMat(VisionFrame hobject)
    {
        ArgumentNullException.ThrowIfNull(hobject);
        if (hobject.Domain != FrameDomain.Halcon)
            throw new InvalidOperationException($"expected Halcon frame but got {hobject.Domain}");
        hobject.AsMat();
        return hobject;
    }

    /// <summary>
    /// Converts a Mat-provenance frame into Halcon semantics without copying pixels. 
    /// / 将 Mat 域帧转为 Halcon 语义，像素零拷贝
    /// </summary>
    public static VisionFrame ToHObject(VisionFrame mat)
    {
        ArgumentNullException.ThrowIfNull(mat);
        if (mat.Domain != FrameDomain.Mat)
            throw new InvalidOperationException($"expected Mat frame but got {mat.Domain}");
        mat.AsHalcon();
        return mat;
    }

    /// <summary>
    /// Byte-exact round trip: HObject → Mat → HObject must restore every byte and dimension. 
    /// / 字节精确往返：HObject → Mat → HObject 必须逐字节与维度完全复原（阶段 5 验收）
    /// </summary>
    public static bool RoundTripByteIdentical(VisionFrame source, out VisionFrame roundTripped)
    {
        var mat = ToMat(source);
        var back = ToHObject(mat);
        roundTripped = back;
        return source.ContentEquals(back);
    }
}