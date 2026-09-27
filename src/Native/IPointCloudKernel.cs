namespace HalconWorkflow.Native;

/// <summary>
/// A point-cloud compute kernel (native vx_* or managed fallback). Instances are NOT thread-safe:
/// the pool leases exactly one caller at a time, mirroring <c>VisionEnginePool</c> semantics
/// (§6.3). All heavy calls are asynchronous-shaped and cooperatively cancellable.
/// / 点云计算内核(原生 vx_* 或托管回退)。实例非线程安全：由池一次借给一个调用方，
///   与 VisionEnginePool 语义一致(§6.3)。重型调用均为异步形态且支持协作式取消。
/// </summary>
public interface IPointCloudKernel : IDisposable
{
    /// <summary>Backend identifier (e.g. <c>native:1.0.0</c> or <c>managed</c>). · 后端标识</summary>
    string Backend { get; }

    /// <summary>True when the kernel runs the native vx_* ABI. · 是否运行原生 vx_* ABI</summary>
    bool IsNative { get; }

    /// <summary>
    /// Voxel grid downsample of a flat x,y,z cloud. Throws <see cref="OperationCanceledException"/>
    /// when the token is signalled, including from the native hot path (§6.3).
    /// / 对平铺 x,y,z 点云做体素栅格下采样。令牌触发时抛 OperationCanceledException，
    ///   包括从原生热路径触发(§6.3)。
    /// </summary>
    Task<VoxelDownsampleResult> VoxelDownsampleAsync(ReadOnlyMemory<float> xyz, VoxelGridSpec spec, CancellationToken ct);

    /// <summary>
    /// Estimates k-nearest surface normals (one unit vector per input point). Cancellable.
    /// / 估计 k 近邻表面法线(每点一个单位向量)。可取消。
    /// </summary>
    Task<NormalEstimateResult> EstimateNormalsAsync(ReadOnlyMemory<float> xyz, int neighbors, CancellationToken ct);
}

/// <summary>
/// Raised when the native kernel reports a non-cancel failure. Carries the ABI status and the
/// native error text. · 原生内核报告非取消失败时抛出。携带 ABI 状态与原生错误文本。
/// </summary>
public sealed class NativeKernelException(VxStatus status, string message)
    : Exception($"vx kernel error ({status}): {message}")
{
    /// <summary>ABI status code. · ABI 状态码</summary>
    public VxStatus Status { get; } = status;
}
