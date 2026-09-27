namespace HalconWorkflow.Nodes.Vision.Imaging;

/// <summary>
/// Pixel storage formats understood by the cross-engine image carrier. 
/// / 两种引擎共同识别的像素存储格式
/// </summary>
public enum PixFormat
{
    /// <summary>Single channel 8-bit grayscale. / 单通道 8 位灰度</summary>
    Gray8 = 1,
    /// <summary>Three channels BGR (OpenCV default). / 三通道 BGR（OpenCV 默认）</summary>
    Bgr8 = 3,
    /// <summary>Four channels BGRA with alpha. / 四通道 BGRA（含 Alpha）</summary>
    Bgra8 = 4,
}

/// <summary>
/// Provenance domain of a frame. Bridges (Synthetic frame↔Mat) flip this without touching pixels. 
/// / 帧的来源域;桥节点（Synthetic frame↔Mat）只翻转该域、不改像素
/// </summary>
public enum FrameDomain
{
    /// <summary>Simulated (software fallback) provenance. / 仿真（软回退）来源</summary>
    Synthetic,
    /// <summary>Real SDK (OpenCV Mat) semantics. / 真实 SDK（OpenCV Mat）语义</summary>
    Mat,
}

/// <summary>
/// Immutable, engine-agnostic image carrier: contiguous row-major bytes plus metadata. 
/// The Kernel value flowing on Image ports is a <see cref="VisionFrame"/>. 
/// / 不可变、与引擎无关的图像载体：连续行主序字节 + 元数据。Image 端口上流转的内核值即 VisionFrame
/// </summary>
public sealed class VisionFrame
{
    /// <summary>Row-major contiguous pixel bytes; length matches Width*Height*Channels. / 行主序连续像素字节</summary>
    public byte[] Bits { get; }

    public int Width { get; }
    public int Height { get; }
    public PixFormat Format { get; }

    /// <summary>Provenance domain (Synthetic vs Mat). / 来源域（Synthetic 或 Mat）</summary>
    public FrameDomain Domain { get; private set; }

    /// <summary>Channels for the pixel format. / 当前格式的通道数</summary>
    public int Channels => (int)Format;

    public static int ChannelsOf(PixFormat f) => (int)f;

    /// <summary>
    /// Creates a frame; validates the buffer length up front. / 构造帧并前置校验缓冲长度
    /// </summary>
    public VisionFrame(int width, int height, PixFormat format, byte[] bits, FrameDomain domain = FrameDomain.Synthetic)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(bits);
        int expect = width * height * (int)format;
        if (bits.Length != expect)
            throw new ArgumentOutOfRangeException(nameof(bits),
                $"buffer length {bits.Length} does not match {width}x{height}x{(int)format} = {expect}");
        Width = width;
        Height = height;
        Format = format;
        Bits = bits;
        Domain = domain;
    }

    /// <summary>
    /// Copies the backing buffer so callers can mutate safely. / 深拷贝载入缓冲，调用方可安全改字节
    /// </summary>
    public static VisionFrame FromCopy(int width, int height, PixFormat format, byte[] src, FrameDomain domain = FrameDomain.Synthetic)
    {
        src = (byte[])src.Clone();
        return new VisionFrame(width, height, format, src, domain);
    }

    /// <summary>Marks this frame as Mat provenance (no pixel change). / 标记为 Mat 域（像素不变）</summary>
    public void AsMat() => Domain = FrameDomain.Mat;

    /// <summary>Marks this frame as Synthetic provenance (no pixel change). / 标记为 Synthetic 域（像素不变）</summary>
    public void AsSynthetic() => Domain = FrameDomain.Synthetic;

    public VisionFrame Clone() => new(Width, Height, Format, (byte[])Bits.Clone(), Domain);

    /// <summary>Content-based equality (bytes compare). / 按内容相等（逐字节比较）</summary>
    public bool ContentEquals(VisionFrame other)
    {
        if (other is null || Width != other.Width || Height != other.Height || Format != other.Format)
            return false;
        return Bits.AsSpan().SequenceEqual(other.Bits);
    }

    public override string ToString() => $"VisionFrame {Width}x{Height} {Format} ({Domain}) [{Bits.Length} bytes]";
}
