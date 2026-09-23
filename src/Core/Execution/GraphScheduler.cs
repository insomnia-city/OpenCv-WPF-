using System.Collections.Concurrent;
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
    /// Fired when the scheduler pauses at a breakpoint. /* 调度器在断点处暂停时触发 */
    /// </summary>
    event Action<string>? DebugPaused;

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

    /// <summary>
    /// Runs a cone from the given node (ancestors ∪ node ∪ descendants). · 从指定节点运行锥集(祖先∪自身∪后代)
    /// </summary>
    Task<GraphRunResult> RunFromAsync(string nodeId, CancellationToken ct);
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
    private readonly HashSet<string> _breakpoints = [];
    private readonly PauseGate _pauseGate = new();

    /// <summary>
    /// Debounce window in ms; 0 disables coalescing. /* 去抖窗口(ms);0 关闭合并 */
    /// </summary>
    public int DebounceMs { get; set; } = 10;

    /// <summary>
    /// Services handed to every node's context. · 提供给所有节点上下文的服务
    /// </summary>
    public Dictionary<Type, object> Services { get; } = [];

    /// <summary>Registered breakpoint ids. · 已注册的断点 ID */
    public IReadOnlyList<string> Breakpoints => _breakpoints.ToList();

    /// <summary>NodeId currently holding the pause gate, or null when not paused. · 当前暂停的节点 ID，未暂停时为 null */
    public string? PausedNodeId { get; private set; }

    /// <inheritdoc />
    public SchedulerState State { get; private set; } = SchedulerState.Stopped;

    /// <inheritdoc />
    public event Action<NodeExecutionEvent>? NodeExecuted;

    /// <inheritdoc />
    public event Action<GraphRunResult>? RunCompleted;

    /// <inheritdoc />
    public event Action<string>? DebugPaused;

    /// <summary>True when a rerun-from-node is queued behind the current paused cycle. · 当前暂停周期后有排队的从节点重跑 */
    public bool HasPendingRerun => _pendingRunFrom is not null;
    private string? _pendingRunFrom;
    private TaskCompletionSource<GraphRunResult>? _rerunTcs;
    private bool _rerunFromPausedSession;

    /// <summary>
    /// Node snapshot cache for stage-24 (optional; registered via Services). · 阶段24 节点快照缓存(可选;通过 Services 注册)
    /// </summary>
    private readonly NodeSnapshotCache? _snapshotCache;

    /// <summary>
    /// Adds a breakpoint by node id. /* 按节点 ID 添加断点 */
    /// </summary>
    public void AddBreakpoint(string nodeId)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        lock (_gate) _breakpoints.Add(nodeId);
    }

    /// <summary>
    /// Removes a breakpoint by node id. /* 按节点 ID 移除断点 */
    /// </summary>
    public void RemoveBreakpoint(string nodeId)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        lock (_gate) _breakpoints.Remove(nodeId);
    }

    /// <summary>
    /// Clears all breakpoints. /* 清空所有断点 */
    /// </summary>
    public void ClearBreakpoints()
    {
        lock (_gate) _breakpoints.Clear();
    }

    /// <summary>
    /// Whether a node id is a registered breakpoint. /* 节点 ID 是否为已注册断点 */
    /// </summary>
    public bool IsBreakpoint(string nodeId)
    {
        lock (_gate) return _breakpoints.Contains(nodeId);
    }

    /// <summary>
    /// Pauses the current cycle at the next node boundary (even without a breakpoint).
    /// /* 在下一边界(即使无断点)暂停当前周期 */
    /// </summary>
    public void Pause()
    {
        _pauseGate.RequestHalt();
    }

    /// <summary>
    /// Resumes the scheduler from a paused state. The state flips synchronously so callers
    /// (and observers of <see cref="State"/>) never see a stale "Paused" right after resuming —
    /// otherwise a release-then-poll races the gateway continuation and can double-release or
    /// wrongly pause the resumed cycle. * 从暂停状态恢复调度器。State 同步翻转，使调用方
    /// (及 State 观察者)在恢复后不会读到过期的 "Paused"——否则"释放后立即轮询"会与闸门
    /// 续延赛跑，导致重复释放或错误暂停已恢复的周期 */
    /// </summary>
    public void Continue()
    {
        bool wasPaused;
        lock (_gate)
        {
            wasPaused = State == SchedulerState.Paused;
            _pauseGate.Release();
            if (wasPaused) State = SchedulerState.Running;
        }
    }

    /// <summary>
    /// Single-step: resume (if paused) then halt at the next node boundary.
    /// The halt request is armed BEFORE releasing, so the boundary that resumes
    /// always sees it — no race window. · 单步：继续(若已暂停)后在下一边界再停驻。
    /// 先注册停驻请求再释放，确保恢复边界的门检查一定看到该请求，无竞态窗口。
    /// </summary>
    public void Step()
    {
        _pauseGate.RequestHalt();
        _pauseGate.Release();
    }

    /// <summary>
    /// Runs a cone from the given node (ancestors ∪ node ∪ descendants). /* 从指定节点运行锥集(祖先∪自身∪后代) */
    /// </summary>
    public async Task<GraphRunResult> RunFromAsync(string nodeId, CancellationToken ct)
    {
        GraphModel? graph;
        lock (_gate) graph = _graph;
        if (graph is null || !graph.Nodes.ContainsKey(nodeId))
            throw new ArgumentException($"Unknown node: {nodeId}");
        lock (_gate)
        {
            if (_loopCts is null || State is not (SchedulerState.Running or SchedulerState.Paused))
                throw new InvalidOperationException("No active session.");
            _pendingRunFrom = nodeId;
            _rerunTcs = new TaskCompletionSource<GraphRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _rerunFromPausedSession = State == SchedulerState.Paused;
        }
        _kicks.Writer.TryWrite(true); // wake the loop to process the rerun
        return await _rerunTcs.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the cone: ancestors ∪ nodeId ∪ descendants (transitive over links).
    /// Ancestor and descendant traversals keep separate visit sets so the start node's
    /// descendants are expanded even when it was found during the ancestor pass.
    /// /* 创建锥集：传递祖先∪nodeId∪传递后继(沿 link 传递)。
    ///    祖先与后继遍历使用独立访问集合，保证起始节点即便已在祖先集合中也继续展开其后继。 */
    /// </summary>
    private HashSet<string> CreateCone(GraphModel graph, string nodeId)
    {
        var cone = new HashSet<string>(StringComparer.Ordinal);
        var ancestors = new HashSet<string>(StringComparer.Ordinal);
        var descendants = new HashSet<string>(StringComparer.Ordinal);

        VisitAncestors(nodeId);
        VisitDescendants(nodeId);

        cone.UnionWith(ancestors);
        cone.UnionWith(descendants);
        return cone;

        void VisitAncestors(string id)
        {
            if (!ancestors.Add(id)) return;
            foreach (var link in graph.Links)
                if (link.To.Owner.Id == id)
                    VisitAncestors(link.From.Owner.Id);
        }

        void VisitDescendants(string id)
        {
            if (!descendants.Add(id)) return;
            foreach (var link in graph.Links)
                if (link.From.Owner.Id == id)
                    VisitDescendants(link.To.Owner.Id);
        }
    }

    /// <summary>
    /// Pauses gate: awaits a signal that the cycle should pause. Returns true when paused.
    /// /* 暂停闸门：在断点处等待暂停信号。返回 true 表示已暂停 */
    /// </summary>
    private Task PauseGateAsync(string nodeId, CancellationToken ct)
    {
        // 1. A Pause()/Step() halt request forces a halt here regardless of breakpoints.
        //    * Pause()/Step() 的停驻请求优先：无论是否断点，一律在此停驻 */
        if (_pauseGate.ConsumeHaltRequest())
            return HaltAsync(nodeId, ct);

        // 2. Breakpoint halt. · 断点停驻
        return IsBreakpoint(nodeId) ? HaltAsync(nodeId, ct) : Task.CompletedTask;
    }

    /// <summary>
    /// Blocks at the node until Continue/Step/StopAsync. Cancellation leaves State untouched so
    /// StopAsync's Stopped state is not clobbered. · 阻塞至 Continue/Step/StopAsync。
    /// 取消时不改动 State，避免覆盖 StopAsync 设置的 Stopped。
    /// </summary>
    private async Task HaltAsync(string nodeId, CancellationToken ct)
    {
        PausedNodeId = nodeId;
        State = SchedulerState.Paused;
        _pauseGate.RequestPause();
        DebugPaused?.Invoke(nodeId);
        await _pauseGate.AwaitRelease(ct).ConfigureAwait(false);
        PausedNodeId = null;
        if (ct.IsCancellationRequested) return;
        State = SchedulerState.Running;
    }

    /// <summary>
    /// Default scheduler. · 默认调度器
    /// </summary>
    public GraphScheduler(NodeSnapshotCache? snapshotCache = null)
    {
        _snapshotCache = snapshotCache;
    }

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
        IStoppableNode[]? stoppers;
        lock (_gate)
        {
            if (State == SchedulerState.Stopped) return;
            State = SchedulerState.Stopped;
            _loopCts?.Cancel();
            loop = _loopTask;
            stoppers = _graph?.Nodes.Values.Select(g => g.Node).OfType<IStoppableNode>().ToArray();
            _pauseGate.Cancel();
            _rerunTcs?.TrySetCanceled();
            _rerunTcs = null;
            _pendingRunFrom = null;
            _rerunFromPausedSession = false;
        }
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        lock (_gate)
        {
            _loopCts = null;
            _loopTask = null;
        }
        PausedNodeId = null;
        if (stoppers is not null)
        {
            foreach (var node in stoppers)
                await node.OnSchedulerStopAsync(CancellationToken.None).ConfigureAwait(false);
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
        return await ExecuteCycleAsync(graph, Signal.Create(TriggerSource.Manual), ct, null).ConfigureAwait(false);
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

            // Debounce: coalesce bursts arriving within the window before firing.
            if (DebounceMs > 0)
            {
                while (true)
                {
                    var wait = Task.Delay(DebounceMs, CancellationToken.None);
                    var read = _kicks.Reader.WaitToReadAsync(ct).AsTask();
                    var done = await Task.WhenAny(wait, read).ConfigureAwait(false);
                    if (ReferenceEquals(done, wait)) break;
                    _kicks.Reader.TryRead(out _);
                }
            }

            Signal? signal;
            lock (_gate)
            {
                signal = _pending;
                _pending = null;
            }

            GraphModel? graph = _graph;
            if (graph is null) continue;
            if (ValidateGraph(graph)) continue;

            // If a rerun-from-node was queued, run the cone directly instead of a new cycle.
            bool gateCone;
            lock (_gate)
            {
                gateCone = _rerunFromPausedSession;
            }
            if (await RunPendingRerunAsync(graph, gateCone, ct).ConfigureAwait(false))
                continue;

            if (signal is null) continue;
            var result = await ExecuteCycleAsync(graph, signal, ct, null).ConfigureAwait(false);
            RunCompleted?.Invoke(result);

            // After a normal cycle ends, check if a rerun-from-node was queued.
            while (!ct.IsCancellationRequested)
            {
                bool gateRerun;
                lock (_gate) { gateRerun = _rerunFromPausedSession; }
                if (!await RunPendingRerunAsync(graph, gateRerun, ct).ConfigureAwait(false))
                    break;
            }
        }
    }

    /// <summary>
    /// Runs the queued rerun-from-node (if any) via the cone executor. Returns true when a cone ran.
    /// · 有排队重跑则执行锥集；返回是否确实运行了锥集
    /// </summary>
    private async Task<bool> RunPendingRerunAsync(GraphModel graph, bool gateCone, CancellationToken ct)
    {
        string? pendingRerun;
        lock (_gate) { pendingRerun = _pendingRunFrom; }
        if (pendingRerun is null) return false;

        var cone = CreateCone(graph, pendingRerun);
        var rerunResult = await ExecuteConeAsync(graph, pendingRerun, cone, gateCone, ct).ConfigureAwait(false);
        lock (_gate)
        {
            _rerunTcs?.TrySetResult(rerunResult);
            _rerunTcs = null;
            _pendingRunFrom = null;
            _rerunFromPausedSession = false;
        }
        RunCompleted?.Invoke(rerunResult);
        return true;
    }

    /// <summary>
    /// Executes a cone (ancestors ∪ nodeId ∪ descendants) from the given start node.
    /// /* 从指定起始节点执行锥集(祖先∪nodeId∪后代) */
    /// </summary>
    private async Task<GraphRunResult> ExecuteConeAsync(GraphModel graph, string startNodeId, HashSet<string> cone, bool gateCone, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var success = 0;
        var faulted = 0;
        string? error = null;
        using var scope = new Scope();
        var ctx = new ExecutionContext(Signal.Create(TriggerSource.Manual), scope, new NullEventBus(), Services);
        // Run ancestors first, then the cone in topological order filtered to cone members.
        // * 先运行祖先，再按拓扑序运行锥成员 */
        foreach (var gn in graph.TopologicalOrder.Where(n => cone.Contains(n.Id)))
        {
            if (ct.IsCancellationRequested)
            {
                NodeExecuted?.Invoke(new NodeExecutionEvent(gn.Id, Signal.Create(TriggerSource.Manual), NodeExecutionPhase.Cancelled, 0));
                error = "Cancelled.";
                break;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            NodeExecutionEvent evt;
            try
            {
                NodeExecuted?.Invoke(new NodeExecutionEvent(gn.Id, Signal.Create(TriggerSource.Manual), NodeExecutionPhase.Started, 0));
                await gn.Node.ExecuteAsync(ctx, ct).ConfigureAwait(false);
                sw.Stop();
                success++;
                var values = CaptureOutputValues(ctx, gn.Node);
                _snapshotCache?.Capture(gn.Id, values);
                evt = new NodeExecutionEvent(gn.Id, Signal.Create(TriggerSource.Manual), NodeExecutionPhase.Completed, sw.ElapsedMilliseconds, null, values);
                // When the rerun is launched from a paused session, the cone also honors
                // breakpoints / pending halt requests so the user can step through it.
                // * 从暂停会话发起的重跑，锥集同样响应断点/停驻请求，便于继续单步 */
                if (gateCone)
                    await PauseGateAsync(gn.Id, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                faulted++;
                evt = new NodeExecutionEvent(gn.Id, Signal.Create(TriggerSource.Manual), NodeExecutionPhase.Cancelled, sw.ElapsedMilliseconds, "Cancelled.");
                error = "Cancelled.";
                break;
            }
            catch (Exception ex)
            {
                sw.Stop();
                faulted++;
                evt = new NodeExecutionEvent(gn.Id, Signal.Create(TriggerSource.Manual), NodeExecutionPhase.Faulted, sw.ElapsedMilliseconds, ex.Message);
                error = ex.Message;
                break;
            }
            NodeExecuted?.Invoke(evt);
        }
        var finished = DateTimeOffset.UtcNow;
        var ok = error is null;
        return new GraphRunResult(ok, Signal.Create(TriggerSource.Manual), started, finished, ok ? null : error, success, faulted);
    }

    private static bool ValidateGraph(GraphModel graph)
    {
        var issues = graph.Validate();
        return issues.Any(i => i.Kind == ValidationIssueKind.Cycle);
    }

    private async Task<GraphRunResult> ExecuteCycleAsync(GraphModel graph, Signal signal, CancellationToken ct, string? runFrom)
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

            // If a rerun-from-node is pending, skip nodes not in its cone.
            // * 如果排了从节点重跑，跳过锥集外的节点 */
            if (runFrom is not null && !CreateCone(graph, runFrom).Contains(gn.Id))
                continue;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            NodeExecutionEvent evt;
            try
            {
                NodeExecuted?.Invoke(new NodeExecutionEvent(gn.Id, signal, NodeExecutionPhase.Started, 0));
                await gn.Node.ExecuteAsync(ctx, ct).ConfigureAwait(false);
                sw.Stop();
                success++;
                var values = CaptureOutputValues(ctx, gn.Node);
                _snapshotCache?.Capture(gn.Id, values);
                evt = new NodeExecutionEvent(gn.Id, signal, NodeExecutionPhase.Completed, sw.ElapsedMilliseconds, null, values);
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

            // Pause gate: honor halt requests / breakpoints BEFORE the Completed event fires.
            // Blocks until Continue/Step/StopAsync; then the node's Completed event fires and the
            // cycle continues on the next node. · 在此节点边界响应停驻请求/断点(Completed 之前)。
            // 阻塞至 Continue/Step/StopAsync，然后触发 Completed 并继续下一个节点。
            await PauseGateAsync(gn.Id, ct).ConfigureAwait(false);
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

/// <summary>
/// Pause gate: TCS-based coordination between the execution thread and the Pause/Continue/Step commands.
/// <para>
/// - <see cref="RequestHalt"/> / <see cref="ConsumeHaltRequest"/> implement "halt at the next boundary"
///   (Pause/Step); the flag is armed first and consumed by the next node gate.</para>
/// <para>
/// - <see cref="RequestPause"/> creates a fresh TCS so each halted node awaits its own release and
///   Continue/Step releases exactly one pause.</para>
/// * 暂停闸门：执行线程与 Pause/Continue/Step 命令之间的 TCS 协调。
///   RequestHalt/ConsumeHaltRequest 实现"下一节点边界停驻"(Pause/Step)；
///   RequestPause 每次创建新 TCS，使每次停驻等待独立释放信号，Continue/Step 恰好释放一次。
/// </summary>
internal sealed class PauseGate
{
    private readonly object _gate = new();
    private TaskCompletionSource<object?> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _haltRequested;

    /// <summary>Arm a halt-at-next-boundary request (used by Pause/Step). * 注册下一边界停驻请求(Pause/Step 使用) */
    public void RequestHalt()
    {
        lock (_gate) _haltRequested = true;
    }

    /// <summary>Consume a pending halt request (fires once). * 消费停驻请求(仅触发一次) */
    public bool ConsumeHaltRequest()
    {
        lock (_gate)
        {
            var pending = _haltRequested;
            _haltRequested = false;
            return pending;
        }
    }

    /// <summary>Request a pause; the next boundary will halt. * 请求暂停；下一边界将停驻 */
    public void RequestPause()
    {
        lock (_gate)
        {
            // Create a fresh TCS so the next AwaitRelease waits on a new signal.
            // * 创建新的 TCS，使下一次 AwaitRelease 等待新信号 */
            _tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Release the pause gate, resuming execution. * 释放暂停闸门，恢复执行 */
    public void Release()
    {
        lock (_gate) { _tcs.TrySetResult(null); }
    }

    /// <summary>Awaits a release signal; returns when released or cancelled. * 等待释放信号；返回时释放或取消 */
    public async Task AwaitRelease(CancellationToken ct)
    {
        try { await _tcs.Task.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    /// <summary>Cancels any awaiting release (used by StopAsync). * 取消任何等待的释放(StopAsync 使用) */
    public void Cancel()
    {
        lock (_gate) { _tcs.TrySetCanceled(); }
    }
}

/// <summary>
/// Bounded per-node ring of captured output-value snapshots (stage-24).
/// * 每节点已捕获输出值快照的有界环(阶段24) */
public sealed class NodeSnapshotCache
{
    private readonly ConcurrentDictionary<string, NodeRing> _rings = new(StringComparer.Ordinal);

    public NodeSnapshotCache(int capacity = 32) => Capacity = Math.Max(1, capacity);

    /// <summary>Frames retained per node. * 每节点保留帧数 */
    public int Capacity { get; }

    /// <summary>Captures a snapshot of the node's current output values. * 捕获节点当前输出值的快照 */
    public void Capture(string nodeId, IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        if (values is null || values.Count == 0) return;
        _rings.GetOrAdd(nodeId, _ => new NodeRing(Capacity)).Add(values);
    }

    /// <summary>Most recent snapshot for a node, or null. * 节点最近快照，无则 null */
    public IReadOnlyDictionary<string, object?>? Latest(string nodeId) =>
        _rings.TryGetValue(nodeId, out var ring) ? ring.Snapshot(1).FirstOrDefault() : null;

    /// <summary>Drops all rings. * 清空所有环 */
    public void Clear() => _rings.Clear();

    private sealed class NodeRing(int capacity)
    {
        private readonly object _gate = new();
        private readonly Queue<IReadOnlyDictionary<string, object?>> _frames = new();

        public void Add(IReadOnlyDictionary<string, object?> frame)
        {
            lock (_gate)
            {
                _frames.Enqueue(frame);
                while (_frames.Count > capacity) _frames.Dequeue();
            }
        }

        public IReadOnlyList<IReadOnlyDictionary<string, object?>> Snapshot(int count)
        {
            if (count <= 0) return [];
            lock (_gate)
            {
                return _frames.Reverse().Take(count).ToList();
            }
        }
    }
}
