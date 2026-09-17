using HalconWorkflow.Native;
using Xunit;

namespace HalconWorkflow.Native.Tests;

/// <summary>
/// Stage-10 gate: policy that caps parallelism and releases slots correctly — cancellation of a
/// borrow consumes no slot and leaks nothing — mirrors <c>HalconEnginePool</c> (§6.3).
/// / 阶段 10 闸门：限制并行度并正确释放槽位的策略——取消借出不占槽位不泄漏——与 HalconEnginePool 一致(§6.3)。
/// </summary>
public sealed class PointCloudKernelPoolTests
{
    [Fact]
    public async Task Borrow_ReusesReturnedKernel()
    {
        var created = 0;
        await using var pool = new PointCloudKernelPool(() => new TrackingKernel(++created), 1);

        var first = await pool.BorrowAsync(CancellationToken.None);
        var firstKernel = first.Kernel;
        first.Dispose();

        using var second = await pool.BorrowAsync(CancellationToken.None);
        Assert.Same(firstKernel, second.Kernel);
        Assert.Equal(1, created);
        Assert.Equal(1, pool.Capacity);
    }

    [Fact]
    public async Task Borrow_EnforcesCapacity()
    {
        await using var pool = new PointCloudKernelPool(() => new TrackingKernel(0), 1);
        using var first = await pool.BorrowAsync(CancellationToken.None);

        var pending = pool.BorrowAsync(CancellationToken.None).AsTask();
        await Task.Delay(50);
        Assert.False(pending.IsCompleted);

        first.Dispose();
        using var second = await pending;
        Assert.NotNull(second.Kernel);
    }

    [Fact]
    public async Task Borrow_PreCancelled_ConsumesNoSlot()
    {
        await using var pool = new PointCloudKernelPool(() => new TrackingKernel(0), 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pool.BorrowAsync(cts.Token).AsTask());

        // If the cancelled wait had consumed the only slot this would deadlock. · 若取消的等待占用了唯一槽位，此处将死锁。
        using var lease = await pool.BorrowAsync(CancellationToken.None);
        Assert.NotNull(lease.Kernel);
    }

    [Fact]
    public async Task Borrow_FactoryFailure_ReleasesSlot()
    {
        var attempts = 0;
        await using var pool = new PointCloudKernelPool(() =>
        {
            if (++attempts == 1) throw new InvalidOperationException("no native kernel");
            return new TrackingKernel(attempts);
        }, 1);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => pool.BorrowAsync(CancellationToken.None).AsTask());

        using var lease = await pool.BorrowAsync(CancellationToken.None);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Lease_DisposeIsIdempotent()
    {
        var created = 0;
        await using var pool = new PointCloudKernelPool(() => new TrackingKernel(++created), 1);

        var lease = await pool.BorrowAsync(CancellationToken.None);
        var kernel = lease.Kernel;
        lease.Dispose();
        lease.Dispose();

        using var again = await pool.BorrowAsync(CancellationToken.None);
        Assert.Same(kernel, again.Kernel);
        Assert.Equal(1, created); // a double return would have queued the same kernel twice · 双重归还会把同一内核入队两次
    }

    [Fact]
    public async Task Dispose_DisposesIdleKernels()
    {
        var kernels = new List<TrackingKernel>();
        var pool = new PointCloudKernelPool(() =>
        {
            var kernel = new TrackingKernel(kernels.Count);
            kernels.Add(kernel);
            return kernel;
        }, 2);

        var a = await pool.BorrowAsync(CancellationToken.None);
        var b = await pool.BorrowAsync(CancellationToken.None);
        a.Dispose();
        b.Dispose();

        pool.Dispose();

        Assert.Equal(2, kernels.Count);
        Assert.All(kernels, k => Assert.True(k.IsDisposed));
    }

    /// <summary>Minimal kernel that records disposal for pool assertions. · 记录释放状态的最小内核</summary>
    private sealed class TrackingKernel(int id) : IPointCloudKernel
    {
        public int Id { get; } = id;

        public string Backend => "tracking";

        public bool IsNative => false;

        public bool IsDisposed { get; private set; }

        public Task<VoxelDownsampleResult> VoxelDownsampleAsync(ReadOnlyMemory<float> xyz, VoxelGridSpec spec, CancellationToken ct)
            => Task.FromResult(new VoxelDownsampleResult(0, ReadOnlyMemory<float>.Empty, Backend, false, null));

        public Task<NormalEstimateResult> EstimateNormalsAsync(ReadOnlyMemory<float> xyz, int neighbors, CancellationToken ct)
            => Task.FromResult(new NormalEstimateResult(0, ReadOnlyMemory<float>.Empty, Backend, false, null));

        public void Dispose() => IsDisposed = true;
    }
}
