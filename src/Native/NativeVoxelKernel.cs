namespace HalconWorkflow.Native;

/// <summary>
/// Native vx_* point-cloud kernel. Marshals caller-owned pinned buffers over the C ABI and drives
/// cooperative cancellation: a <see cref="CancellationToken"/> registration flips the flag the
/// native code polls per chunk, so a cancelled call returns promptly without unwinding native
/// state (§6.3). One instance is leased at a time (see <see cref="PointCloudKernelPool"/>).
/// / 原生 vx_* 点云内核。将调用方持有的固定缓冲经 C ABI 编组，并驱动协作式取消：
///   CancellationToken 注册翻转原生代码按块轮询的标志，使取消调用及时返回而不破坏原生状态(§6.3)。
///   实例一次只被借出一个(见 PointCloudKernelPool)。
/// </summary>
public sealed class NativeVoxelKernel(IVxFunctionTable api) : IPointCloudKernel
{
    private readonly IVxFunctionTable _api = api ?? throw new ArgumentNullException(nameof(api));
    private int _disposed;

    /// <inheritdoc />
    public string Backend => $"native:{_api.Version}";

    /// <inheritdoc />
    public bool IsNative => true;

    /// <inheritdoc />
    public Task<VoxelDownsampleResult> VoxelDownsampleAsync(ReadOnlyMemory<float> xyz, VoxelGridSpec spec, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!spec.IsValid) throw new ArgumentOutOfRangeException(nameof(spec), "voxel spec needs positive leaves and cap");
        RequireTriplets(xyz.Length);
        ct.ThrowIfCancellationRequested();

        var inputCount = xyz.Length / 3;
        if (inputCount == 0)
            return Task.FromResult(new VoxelDownsampleResult(0, ReadOnlyMemory<float>.Empty, Backend, false, null));

        var capacity = Math.Min(inputCount, spec.MaxPoints);
        var output = new float[capacity * 3];
        var cancel = 0;
        using var registration = ct.Register(() => Interlocked.Exchange(ref cancel, 1));

        var status = _api.VoxelDownsample(xyz.Span, inputCount, spec, output, out var written, ref cancel);
        if (status == VxStatus.BufferTooSmall && written > capacity)
        {
            // Native reported the exact requirement; grow once and retry. · 原生返回精确需求；扩容重试一次
            capacity = written;
            output = new float[capacity * 3];
            cancel = 0;
            status = _api.VoxelDownsample(xyz.Span, inputCount, spec, output, out written, ref cancel);
        }

        ThrowIfFailed(status);
        var points = Slice(output, written * 3);
        return Task.FromResult(new VoxelDownsampleResult(inputCount, points, Backend, false, null));
    }

    /// <inheritdoc />
    public Task<NormalEstimateResult> EstimateNormalsAsync(ReadOnlyMemory<float> xyz, int neighbors, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (neighbors < 1) throw new ArgumentOutOfRangeException(nameof(neighbors), "neighbors must be >= 1");
        RequireTriplets(xyz.Length);
        ct.ThrowIfCancellationRequested();

        var inputCount = xyz.Length / 3;
        if (inputCount == 0)
            return Task.FromResult(new NormalEstimateResult(0, ReadOnlyMemory<float>.Empty, Backend, false, null));

        var normals = new float[inputCount * 3];
        var cancel = 0;
        using var registration = ct.Register(() => Interlocked.Exchange(ref cancel, 1));

        var status = _api.EstimateNormals(xyz.Span, inputCount, neighbors, normals, ref cancel);
        ThrowIfFailed(status);

        return Task.FromResult(new NormalEstimateResult(inputCount, Slice(normals, normals.Length), Backend, false, null));
    }

    private void ThrowIfFailed(VxStatus status)
    {
        if (status == VxStatus.Cancelled) throw new OperationCanceledException("vx kernel cancelled");
        if (status == VxStatus.Ok) return;
        throw new NativeKernelException(status, _api.LastError((int)status));
    }

    private static void RequireTriplets(int length)
    {
        if (length % 3 != 0)
            throw new ArgumentException("point cloud must be flat x,y,z triplets (length % 3 == 0)", "xyz");
    }

    private static ReadOnlyMemory<float> Slice(float[] source, int length)
    {
        var trimmed = new float[length];
        Array.Copy(source, trimmed, length);
        return trimmed;
    }

    /// <summary>Releases the managed proxy; the native library stays process-level (ADR-006). · 释放托管代理；原生库保持进程级</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _api.Dispose();
    }
}
