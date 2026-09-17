using HalconWorkflow.Abstractions;

namespace HalconWorkflow.Storage;

/// <summary>
/// Batch sink that actually persists records (SQL, file, memory…). · 真正落库的批量接收器(SQL/文件/内存…)
/// </summary>
public interface ITraceSink
{
    /// <summary>Writes one batch atomically-best-effort. · 尽力原子地写入一批</summary>
    Task WriteBatchAsync(IReadOnlyList<TraceRecord> batch, CancellationToken ct);
}

/// <summary>Tuning for the hot-path write queue (§8.3). · 热路径写入队列调参(§8.3)</summary>
public sealed class TraceQueueOptions
{
    /// <summary>Queue capacity; overflow drops the oldest record + alarm. · 队列上限；溢出丢弃最旧记录 + 告警</summary>
    public int Capacity { get; init; } = 100_000;

    /// <summary>Max records per transaction. · 单事务最大记录数</summary>
    public int BatchSize { get; init; } = 500;

    /// <summary>Batching window: flush when it elapses or the batch is full. · 批量窗口：到期或满批即刷</summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Retries per batch before it is abandoned + reported. · 每批失败重试次数，超限弃批并上报</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>Delay between retries. · 重试间隔</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(50);
}

/// <summary>
/// Hot-path traceability queue (§8.3): <see cref="Enqueue"/> never blocks on I/O — it appends
/// to an in-memory ring and returns. A single background pump aggregates batches, persists them
/// with bounded retry, and never back-pressures the execution thread. Overflow drops the oldest
/// record and raises <see cref="DroppedAlarm"/>.
/// · 热路径追溯队列(§8.3)：Enqueue 绝不阻塞 I/O——只入内存环形队列即返回。单一后台泵聚合批次、
///   受限重试落库，绝不反压执行线程。溢出丢最旧并触发 DroppedAlarm。
/// </summary>
public sealed class TraceWriteQueue : IAsyncDisposable
{
    private readonly Queue<TraceRecord> _queue = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly ITraceSink _sink;
    private readonly TraceQueueOptions _options;
    private long _enqueued;
    private long _written;
    private long _dropped;
    private long _retries;
    private long _failedBatches;
    private long _lastAlarmAt;
    private Task? _pump;

    /// <summary>Creates a queue over the given sink. · 在给定接收器上创建队列</summary>
    public TraceWriteQueue(ITraceSink sink, TraceQueueOptions? options = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _options = options ?? new TraceQueueOptions();
    }

    /// <summary>Raised when records are dropped due to overflow (argument: total dropped). · 溢出丢记录时触发(参数：累计丢弃数)</summary>
    public event Action<long>? DroppedAlarm;

    /// <summary>Raised when a batch is abandoned after retries (args: batch size, error). · 重试超限弃批时触发(参数：批大小、错误)</summary>
    public event Action<int, Exception>? BatchFailed;

    /// <summary>Total enqueued records. · 累计入队数</summary>
    public long EnqueuedCount => Interlocked.Read(ref _enqueued);

    /// <summary>Total persisted records. · 累计落库数</summary>
    public long WrittenCount => Interlocked.Read(ref _written);

    /// <summary>Total dropped records (overflow). · 累计丢弃数(溢出)</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>Total retry attempts. · 累计重试次数</summary>
    public long RetryCount => Interlocked.Read(ref _retries);

    /// <summary>Total abandoned batches. · 累计弃批数</summary>
    public long FailedBatches => Interlocked.Read(ref _failedBatches);

    /// <summary>Starts the background pump (idempotent). · 启动后台泵(幂等)</summary>
    public void Start() => _pump ??= Task.Run(PumpAsync);

    /// <summary>
    /// Enqueues one record; never blocks. On overflow the oldest record is dropped.
    /// · 入队一条记录；绝阻塞。溢出时丢弃最旧记录。
    /// </summary>
    public void Enqueue(TraceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            if (_queue.Count >= _options.Capacity)
            {
                _queue.Dequeue();
                Interlocked.Increment(ref _dropped);
            }
            _queue.Enqueue(record);
            Interlocked.Increment(ref _enqueued);
        }
        _wake.Release();
        RaiseAlarmIfDropped();
    }

    /// <summary>Completes when every enqueued record has been written or dropped. · 所有入队记录均已写入或丢弃时完成</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        while (Processed() < Interlocked.Read(ref _enqueued))
            await Task.Delay(2, ct).ConfigureAwait(false);
    }

    private long Processed() => Interlocked.Read(ref _written) + Interlocked.Read(ref _dropped);

    private void RaiseAlarmIfDropped()
    {
        var dropped = Interlocked.Read(ref _dropped);
        if (dropped == 0 || dropped == Interlocked.Read(ref _lastAlarmAt)) return;
        Interlocked.Exchange(ref _lastAlarmAt, dropped);
        DroppedAlarm?.Invoke(dropped);
    }

    private async Task PumpAsync()
    {
        var ct = _cts.Token;
        var batch = new List<TraceRecord>(_options.BatchSize);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await WaitForWorkAsync(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) break;
                await CollectAsync(batch, ct).ConfigureAwait(false);
                if (batch.Count == 0) continue;
                await WriteBatchAsync(batch, ct).ConfigureAwait(false);
                Interlocked.Add(ref _written, batch.Count);
                batch.Clear();
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown: fall through to the final drain · 关机：进入最终排空
        }
        await FinalDrainAsync(batch).ConfigureAwait(false);
    }

    private async Task WaitForWorkAsync(CancellationToken ct)
    {
        while (!HasWork())
            await _wake.WaitAsync(5, ct).ConfigureAwait(false);
    }

    private bool HasWork()
    {
        lock (_gate) return _queue.Count > 0;
    }

    private async Task CollectAsync(List<TraceRecord> batch, CancellationToken ct)
    {
        var start = Environment.TickCount64;
        while (batch.Count < _options.BatchSize)
        {
            DrainInto(batch);
            if (batch.Count >= _options.BatchSize) break;
            if (Environment.TickCount64 - start >= (long)_options.FlushInterval.TotalMilliseconds) break;
            await Task.Delay(1, ct).ConfigureAwait(false);
        }
    }

    private void DrainInto(List<TraceRecord> batch)
    {
        lock (_gate)
        {
            while (batch.Count < _options.BatchSize && _queue.Count > 0)
                batch.Add(_queue.Dequeue());
        }
    }

    private async Task FinalDrainAsync(List<TraceRecord> batch)
    {
        while (true)
        {
            DrainInto(batch);
            if (batch.Count == 0) break;
            await WriteBatchAsync(batch, CancellationToken.None).ConfigureAwait(false);
            Interlocked.Add(ref _written, batch.Count);
            batch.Clear();
        }
    }

    private async Task WriteBatchAsync(List<TraceRecord> batch, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await _sink.WriteBatchAsync(batch, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt >= _options.MaxRetries)
                {
                    Interlocked.Increment(ref _failedBatches);
                    BatchFailed?.Invoke(batch.Count, ex);
                    return;
                }
                Interlocked.Increment(ref _retries);
                await Task.Delay(_options.RetryDelay, ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_pump is not null) await _pump.ConfigureAwait(false);
        _cts.Dispose();
        _wake.Dispose();
    }
}
