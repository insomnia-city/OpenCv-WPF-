namespace HalconWorkflow.Native;

/// <summary>
/// Concrete borrow-return-cancel kernel pool, semantically identical to <c>VisionEnginePool</c>
/// (§6.3): parallelism is capped by capacity and <see cref="BorrowAsync"/> honours cancellation
/// before a slot is consumed, so a cancelled borrow leaks nothing.
/// / 实现借出-归还-取消的内核池，语义与 VisionEnginePool 完全一致(§6.3)：并行度受 capacity 约束;
///   BorrowAsync 在占用槽位前响应取消，取消不泄漏槽位。
/// </summary>
public sealed class PointCloudKernelPool : IAsyncDisposable, IDisposable
{
    private readonly Func<IPointCloudKernel> _factory;
    private readonly SemaphoreSlim _gate;
    private readonly Queue<IPointCloudKernel> _idle = new();
    private readonly object _lock = new();
    private volatile bool _disposed;

    /// <summary>Maximum number of kernels borrowed at once. · 同时可借出的内核数上限</summary>
    public int Capacity { get; }

    public PointCloudKernelPool(Func<IPointCloudKernel> factory, int capacity)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _factory = factory;
        Capacity = capacity;
        _gate = new SemaphoreSlim(capacity, capacity);
    }

    /// <summary>Borrows a kernel; a cancelled wait consumes no slot and leaks nothing. · 借出内核;取消等待不占槽位不泄漏</summary>
    public async ValueTask<PointCloudKernelLease> BorrowAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false); // cancelled awaits consume no slot · 取消的等待不占槽位
        IPointCloudKernel kernel;
        try
        {
            lock (_lock)
            {
                kernel = _idle.Count > 0 ? _idle.Dequeue() : _factory();
            }
        }
        catch
        {
            _gate.Release(); // factory failed: return the slot · 工厂失败：归还槽位
            throw;
        }
        return new PointCloudKernelLease(this, kernel);
    }

    /// <summary>Returns a kernel and frees one slot. Idempotent. · 归还内核并释放一个槽位;幂等</summary>
    internal void Return(IPointCloudKernel kernel)
    {
        if (_disposed) { kernel.Dispose(); return; }
        lock (_lock)
        {
            _idle.Enqueue(kernel);
        }
        _gate.Release();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IPointCloudKernel[] all;
        lock (_lock)
        {
            all = _idle.ToArray();
            _idle.Clear();
        }
        foreach (var kernel in all) kernel.Dispose();
        _gate.Dispose();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A borrowed kernel. Disposing returns it to the pool exactly once (idempotent).
/// / 借出的内核。释放时恰好归还一次(幂等)。
/// </summary>
public sealed class PointCloudKernelLease : IDisposable
{
    private readonly PointCloudKernelPool _pool;
    private int _disposed;

    internal PointCloudKernelLease(PointCloudKernelPool pool, IPointCloudKernel kernel)
    {
        _pool = pool;
        Kernel = kernel;
    }

    /// <summary>Leased kernel; valid until the lease is disposed. · 借出的内核；租约释放前有效</summary>
    public IPointCloudKernel Kernel { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _pool.Return(Kernel);
    }
}
