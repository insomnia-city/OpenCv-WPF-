namespace HalconWorkflow.Native;

/// <summary>
/// Status codes crossing the vx_* C ABI. Errors are returned as codes, never thrown across the
/// boundary (§6.3). / 跨越 vx_* C ABI 的状态码。错误以码返回，绝不跨边界抛异常(§6.3)。
/// </summary>
public enum VxStatus
{
    /// <summary>Success. / 成功</summary>
    Ok = 0,

    /// <summary>Cooperative cancellation was observed (§6.3). / 观察到协作式取消(§6.3)</summary>
    Cancelled = 1,

    /// <summary>The caller-provided output buffer was too small. / 调用方输出缓冲过小</summary>
    BufferTooSmall = 2,

    /// <summary>Malformed arguments (null pointer, bad leaf size, …). / 参数非法(空指针/叶尺寸错误等)</summary>
    InvalidArgument = 3,

    /// <summary>Unrecoverable native error; read <c>vx_last_error</c>. / 不可恢复的原生错误;读 vx_last_error</summary>
    NativeError = 4
}

/// <summary>
/// Voxel grid parameters shared with the native kernel (§6.3). The grid is axis-aligned; every
/// cell keeps the centroid of the points that fall inside it. · 与原生内核共享的体素栅格参数(§6.3)。
///   栅格轴对齐；每个体素保留落入其中点的质心。
/// </summary>
public readonly record struct VoxelGridSpec(double LeafX, double LeafY, double LeafZ, int MaxPoints)
{
    /// <summary>Default 1 cm leaves with a 4M-point output cap. · 默认 1cm 叶尺寸、输出上限 400 万点</summary>
    public static VoxelGridSpec Default => new(0.01, 0.01, 0.01, 4_000_000);

    /// <summary>True when leaves are positive and the cap is positive. · 叶尺寸与上限均为正</summary>
    public bool IsValid => LeafX > 0 && LeafY > 0 && LeafZ > 0 && MaxPoints > 0;
}

/// <summary>Result of a voxel grid downsample. · 体素栅格下采样结果</summary>
public readonly record struct VoxelDownsampleResult(
    int InputCount,
    ReadOnlyMemory<float> Points,
    string Backend,
    bool Cancelled,
    string? Message)
{
    /// <summary>Number of output points. · 输出点数</summary>
    public int OutputCount => Points.Length / 3;
}

/// <summary>
/// Result of a surface normal estimate: one unit vector per input point, flat x,y,z triplets.
/// Sign is arbitrary (a normal and its negation describe the same plane). · 表面法线估计结果：
///   每个输入点一个单位向量，x,y,z 平铺。符号任意(法线与其反向描述同一平面)。
/// </summary>
public readonly record struct NormalEstimateResult(
    int InputCount,
    ReadOnlyMemory<float> Normals,
    string Backend,
    bool Cancelled,
    string? Message)
{
    /// <summary>Number of estimated normals. · 估计出的法线数</summary>
    public int NormalCount => Normals.Length / 3;
}

/// <summary>
/// Managed seam over the vx_* C ABI. Kept tiny so the point-cloud kernel can run against the real
/// native library in production and a fake in tests (§6.3). Implementations receive caller-owned
/// buffers; the caller pins the memory and passes a cooperative cancel flag.
/// / vx_* C ABI 的托管接缝。保持精简：点云内核在生产用真实原生库、测试注入假实现(§6.3)。
///   实现接收调用方缓冲；调用方固定内存并传入协作式取消标志。
/// </summary>
public interface IVxFunctionTable : IDisposable
{
    /// <summary>Native algorithm version string (from <c>vx_version</c>). · 原生算法版本串(来自 vx_version)</summary>
    string Version { get; }

    /// <summary>
    /// Down-samples a flat x,y,z cloud into voxel centroids. <paramref name="output"/> holds at
    /// most <paramref name="output"/>.Length/3 points; <paramref name="outCount"/> reports the
    /// number written (or required on <see cref="VxStatus.BufferTooSmall"/>). The implementation
    /// must poll <paramref name="cancel"/> in chunks and stop promptly (§6.3).
    /// / 将平铺 x,y,z 点云下采样为体素质心。output 至多容纳 Length/3 点；outCount 返回写入数
    ///   (BufferTooSmall 时为所需数)。实现必须分块轮询 cancel 并及时停止(§6.3)。
    /// </summary>
    VxStatus VoxelDownsample(ReadOnlySpan<float> xyz, int pointCount, VoxelGridSpec spec,
        Span<float> output, out int outCount, ref int cancel);

    /// <summary>
    /// Estimates k-nearest surface normals, one unit vector per input point.
    /// / 估计 k 近邻表面法线，每点一个单位向量。
    /// </summary>
    VxStatus EstimateNormals(ReadOnlySpan<float> xyz, int pointCount, int neighbors,
        Span<float> normals, ref int cancel);

    /// <summary>Human-readable text for a status handle; empty when unknown. · 状态句柄的可读文本；未知返回空</summary>
    string LastError(int handle);
}
