using System.Threading.Channels;
using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Graph;
using HalconWorkflow.Core.Model;

namespace HalconWorkflow.Core.Execution;

/// <summary>
/// Scheduler state. /* 调度器状态 */
/// </summary>
public enum SchedulerState
{
    Stopped,
    Running,
    Paused
}

/// <summary>
/// Scheduler interface: static topology + event-driven execution. · 调度器接口：静态拓扑 + 事件驱动执行
/// </summary>
public interface IGraphScheduler
{
    /// <summary>
    /// Current scheduler state. /* 当前调度器状态 */
    /// </summary>
    SchedulerState State { get; }

    /// <summary>
    /// Per-node execution events (timing / phase). /* 节点执行事件(耗时/阶段) */
    /// </summary>
    event Action<NodeExecutionEvent>? NodeExecuted;

    /// <summary>
    /// Fired when a full run cycle finishes. /* 整轮执行完成时触发 */
    /// </summary>
    event Action<GraphRunResult>? RunCompleted;

    /// <summary>
    /// Sets the graph model and validates it. Returns validation issues (empty = ok). /* 设置图模型并校验;返回校验问题(空=通过) */
    /// </summary>
    IReadOnlyList<ValidationIssue> Load(GraphModel graph);

    /// <summary>
    /// Enqueues a trigger with queue-limit-1 (drop-old-keep-new) + debounce semantics. /* 以队列上限1(丢旧保新)+去抖语义入队触发 */
    /// </summary>
    void Trigger(TriggerSource source);

    /// <summary>
    /// Starts the trigger-consumption loop. · 启动触发消费循环
    /// </summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>
    /// Stops the loop and drains pending work. · 停止循环并清理挂起工作
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Runs a single cycle immediately and returns its result. · 立即执行单轮并返回结果
    /// </summary>
    Task<GraphRunResult> RunOnceAsync(CancellationToken ct);
}

/// <summary>
/// Default scheduler. · 默认调度器
/// </summary>
public sealed class GraphScheduler : IGraphScheduler, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Channel<bool> _kicks = Channel.CreateUnbounded<bool>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    private GraphModel? _graph;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private Signal? _pending;
    private DateTimeOffset _lastTriggerAt;

    /// <summary>
    /// Debounce window in ms; 0 disables coalescing. /* 去抖窗口(ms);0 关闭合并 */
    /// </summary>
    public int DebounceMs { get; set; } = 10;

    /// <summary>
    /// Services handed to every node's context. · 提供给所有节点上下文的服务
    /// </summary>
    public Dictionary<Type, object> Services { get; } = [];

    /// <inheritdoc />
    public SchedulerState State { get; private set; } = SchedulerState.Stopped;

    /// <inheritdoc />
    public event Action<NodeExecutionEvent>? NodeExecuted;

    /// <inheritdoc />
    public event Action<GraphRunResult>? RunCompleted;

    /// <inheritdoc />
    public IReadOnlyList<ValidationIssue> Load(GraphModel graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        lock (_gate)
        {
            _graph = graph;
            return graph.Validate();
        }
    }

    /// <inheritdoc />
    public void Trigger(TriggerSource source)
    {
        lock (_gate)
        {
            // Drop-old-keep-new: always replace the latest pending signal. · 丢旧保新：总是替换最新挂起信号
            _pending = Signal.Create(source);
            _lastTriggerAt = DateTimeOffset.UtcNow;
        }
        _kicks.Writer.TryWrite(true);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (State == SchedulerState.Running) return Task.CompletedTask;
            State = SchedulerState.Running;
            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _loopTask = Task.Run(() => ConsumeLoopAsync(_loopCts.Token));
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        Task? loop;
        lock (_gate)
        {
            if (State == SchedulerState.Stopped) return;
            State = SchedulerState.Stopped;
            _loopCts?.Cancel();
            loop = _loopTask;
        }
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    /// <inheritdoc />
    public async Task<GraphRunResult> RunOnceAsync(CancellationToken ct)
    {
        GraphModel? graph;
        lock (_gate) graph = _graph;
        if (graph is null)
            throw new InvalidOperationException("No graph loaded.");

        ValidateGraph(graph);
        return await ExecuteCycleAsync(graph, Signal.Create(TriggerSource.Manual), ct).ConfigureAwait(false);
    }

    private async Task ConsumeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _kicks.Reader.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Debounce: coalesce bursts arriving within the window before firing. · 去抖：窗口内的突发触发合并后再执行
            if (DebounceMs > 0)
            {
                while (true)
                {
                    var wait = Task.Delay(DebounceMs, CancellationToken.None);
                    var read = _kicks.Reader.WaitToReadAsync(ct).AsTask();
                    var done = await Task.WhenAny(wait, read).ConfigureAwait(false);
                    if (ReferenceEquals(done, wait)) break;          // quiet for a full window · 窗口内安静
                    _kicks.Reader.TryRead(out _);                     // consume the extra kick along the way · 顺带消费多余 kick
                }
            }

            Signal? signal;
            lock (_gate)
            {
                signal = _pending;
                _pending = null;
            }

            GraphModel? graph = _graph;
            if (signal is null || graph is null) continue;

            if (ValidateGraph(graph)) continue;

            var result = await ExecuteCycleAsync(graph, signal, ct).ConfigureAwait(false);
            RunCompleted?.Invoke(result);
        }
    }

    private static bool ValidateGraph(GraphModel graph)
    {
        var issues = graph.Validate();
        return issues.Any(i => i.Kind == ValidationIssueKind.Cycle);
    }

    private async Task<GraphRunResult> ExecuteCycleAsync(GraphModel graph, Signal signal, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var success = 0;
        var faulted = 0;
        string? error = null;

        using var scope = new Scope();
        var ctx = new ExecutionContext(signal, scope, new NullEventBus(), Services);

        foreach (var gn in graph.TopologicalOrder)
        {
            if (ct.IsCancellationRequested)
            {
                NodeExecuted?.Invoke(new NodeExecutionEvent(gn.Id, signal, NodeExecutionPhase.Cancelled, 0));
                error = "Cancelled.";
                break;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            NodeExecutionEvent evt;
            try
            {
                NodeExecuted?.Invoke(new NodeExecutionEvent(gn.Id, signal, NodeExecutionPhase.Started, 0));
                await gn.Node.ExecuteAsync(ctx, ct).ConfigureAwait(false);
                sw.Stop();
                success++;
                // Snapshot the node's data outputs immediately after execution. Capturing right here
                // (not later) keeps the flat tag table collision-free across identical port names.
                // / 节点执行完立即对其数据输出做快照。此处即时捕获(而非事后)避免扁平 tag 表同名端口互相覆盖
                var values = CaptureOutputValues(ctx, gn.Node);
                evt = new NodeExecutionEvent(gn.Id, signal, NodeExecutionPhase.Completed, sw.ElapsedMilliseconds,
                    null, values);
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                faulted++;
                evt = new NodeExecutionEvent(gn.Id, signal, NodeExecutionPhase.Cancelled, sw.ElapsedMilliseconds, "Cancelled.");
                error = "Cancelled.";
                break;
            }
            catch (Exception ex)
            {
                sw.Stop();
                faulted++;
                evt = new NodeExecutionEvent(gn.Id, signal, NodeExecutionPhase.Faulted, sw.ElapsedMilliseconds, ex.Message);
                error = ex.Message;
                break;
            }
            NodeExecuted?.Invoke(evt);
        }

        var finished = DateTimeOffset.UtcNow;
        var ok = error is null;
        return new GraphRunResult(ok, signal, started, finished, ok ? null : error, success, faulted);
    }

    /// <summary>
    /// Captures every data-output value the node just produced into the shared tag table, mirrors
    /// it onto the port's Value reference and returns the dictionary (null when the node has no
    /// data outputs). Exec ports carry no values and are skipped. · 捕获该节点刚写入共享 tag 表的
    /// 全部数据输出值：回写端口 Value 引用并返回快照字典(无数据输出返回 null)；控制流端口不参与。
    /// </summary>
    private static IReadOnlyDictionary<string, object?>? CaptureOutputValues(ExecutionContext ctx, INode node)
    {
        Dictionary<string, object?>? values = null;
        foreach (var port in node.Outputs)
        {
            if (port.Kind != PortKind.Data) continue;
            var value = ctx.GetData(port.Name);
            port.Value = value;
            values ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            values[port.Name] = value;
        }
        return values;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _kicks.Writer.TryComplete();
    }
}