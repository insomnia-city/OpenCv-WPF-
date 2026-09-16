using HalconWorkflow.Abstractions;

namespace HalconWorkflow.Nodes.Vision.Engines;

/// <summary>
/// Concrete borrow-return-cancel engine pool bound to <see cref="IVisionEngine"/>.
/// Parallelism is capped by <paramref name="capacity"/>; <see cref="BorrowAsync"/>
/// honours cancellation before a slot is consumed (a cancelled borrow leaks nothing).
/// / 实现 IVisionEnginePool 的引擎池：按实例借出-归还-取消。并行度受 capacity 上限约束;
///   BorrowAsync 在占用槽位前响应取消（取消不泄漏槽位）。
/// </summary>
public sealed class VisionEnginePool : IVisionEnginePool, IAsyncDisposable
{
    private readonly Func<IVisionEngine> _factory;
    private readonly SemaphoreSlim _gate;
    private readonly Queue<IVisionEngine> _idle = new();
    private readonly object _lock = new();

    public int Capacity { get; }

    public VisionEnginePool(Func<IVisionEngine> factory, int capacity)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _factory = factory;
        Capacity = capacity;
        _gate = new SemaphoreSlim(capacity, capacity);
    }

    /// <inheritdoc />
    public async ValueTask<IVisionEngineLease> BorrowAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false); // cancelled awaits consume no slot
        IVisionEngine engine;
        try
        {
            lock (_lock)
            {
                if (_idle.Count > 0) engine = _idle.Dequeue();
                else engine = _factory();
            }
        }
        catch
        {
            _gate.Release(); // factory failed: return the slot
            throw;
        }
        return new Lease(this, engine);
    }

    /// <summary>
    /// Returns an engine to the pool and frees one slot. Idempotent. / 归还引擎并释放一个槽位;幂等
    /// </summary>
    internal void Return(IVisionEngine engine)
    {
        if (Disposed) { engine.Dispose(); return; }
        bool released;
        lock (_lock)
        {
            _idle.Enqueue(engine);
            released = true;
        }
        if (released) _gate.Release();
    }

    private sealed class Lease : IVisionEngineLease
    {
        private readonly VisionEnginePool _pool;
        private readonly IVisionEngine _engine;
        private int _disposed;

        public object Engine => _engine;

        public Lease(VisionEnginePool pool, IVisionEngine engine)
        {
            _pool = pool;
            _engine = engine;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _pool.Return(_engine);
        }
    }

    private volatile bool Disposed;

    public async ValueTask DisposeAsync()
    {
        if (Disposed) return;
        Disposed = true;
        IVisionEngine[] all;
        lock (_lock)
        {
            all = _idle.ToArray();
            _idle.Clear();
        }
        foreach (var e in all) e.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
        _gate.Dispose();
    }
}