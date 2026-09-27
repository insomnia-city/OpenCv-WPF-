using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Execution;
using HalconWorkflow.Core.Graph;
using HalconWorkflow.Core.Model;

namespace HalconWorkflow.Core.Tests;

/// <summary>
/// Stage-23 debug gate (§5.7): breakpoints, pause/step, rerun-from-node and elapsed reporting.
/// · 阶段23 调试闸门(§5.7)：断点、暂停/单步、从节点重跑与耗时上报。
/// </summary>
public class SchedulerDebugTests
{
    [Fact]
    public async Task Breakpoint_HaltsBeforeNode_Continue_RunsRemainder()
    {
        var graph = SchedulerTests.MakeHeadlessGraph(); // cam → threshold → result
        await using var scheduler = new GraphScheduler { DebounceMs = 0 };
        scheduler.Load(graph);
        scheduler.AddBreakpoint("threshold");

        var completed = new List<string>();
        scheduler.NodeExecuted += e => { if (e.Phase == NodeExecutionPhase.Completed) completed.Add(e.NodeId); };
        var paused = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.DebugPaused += id => paused.TrySetResult(id);

        using var cts = new CancellationTokenSource(10000);
        await scheduler.StartAsync(cts.Token);
        scheduler.Trigger(TriggerSource.Manual);

        var at = await paused.Task;
        Assert.Equal("threshold", at);
        // cam completed, threshold has not · cam 已完成,threshold 未执行
        Assert.Contains("cam", completed);
        Assert.DoesNotContain("threshold", completed);
        Assert.DoesNotContain("result", completed);
        Assert.Equal(SchedulerState.Paused, scheduler.State);
        Assert.Equal("threshold", scheduler.PausedNodeId);

        // Subscribe before releasing the gate. Continue() lets the remaining nodes run, and the
        // scheduler can raise RunCompleted before the next statement executes; subscribing
        // afterwards loses the one-shot signal and the wait below then times out. Observed under
        // CPU load as a TaskCanceledException from runDone. · 先订阅再释放闸门：Continue() 会让
        // 剩余节点跑完，RunCompleted 可能在下一条语句前就触发，之后再订阅会丢失一次性信号并导致
        // 等待超时（高负载下表现为 runDone 抛 TaskCanceledException）。
        using var runDone = new CancellationTokenSource(10000);
        var runTcs = new TaskCompletionSource<GraphRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.RunCompleted += r => runTcs.TrySetResult(r);
        scheduler.Continue();
        var result = await runTcs.Task.WaitAsync(runDone.Token);
        Assert.True(result.Success);
        Assert.Contains("threshold", completed);
        Assert.Contains("result", completed);
        Assert.Equal(SchedulerState.Running, scheduler.State);
        Assert.Null(scheduler.PausedNodeId);
    }

    [Fact]
    public async Task Breakpoint_Continue_StopsAtNextBreakpoint()
    {
        var graph = SchedulerTests.MakeHeadlessGraph();
        await using var scheduler = new GraphScheduler { DebounceMs = 0 };
        scheduler.Load(graph);
        scheduler.AddBreakpoint("cam");
        scheduler.AddBreakpoint("result");

        var completed = new List<string>();
        scheduler.NodeExecuted += e => { if (e.Phase == NodeExecutionPhase.Completed) completed.Add(e.NodeId); };
        var pauses = new Chan<string>();
        scheduler.DebugPaused += pauses.Push;

        using var cts = new CancellationTokenSource(10000);
        await scheduler.StartAsync(cts.Token);
        scheduler.Trigger(TriggerSource.Manual);

        Assert.Equal("cam", await pauses.PopAsync(5000));
        scheduler.Continue();
        Assert.Equal("result", await pauses.PopAsync(5000));
        // cam ran during the first gap; threshold/result haven't yet · 首个间隙只跑了 cam
        Assert.Contains("cam", completed);
        Assert.DoesNotContain("result", completed);
        scheduler.Continue();
        await Task.Delay(200, CancellationToken.None); // let the rest drain · 让剩余节点跑完
        Assert.Contains("result", completed);
    }

    [Fact]
    public async Task Step_FromBreakpoint_ExecutesOneNodeThenPausesAtNext()
    {
        var graph = SchedulerTests.MakeHeadlessGraph();
        await using var scheduler = new GraphScheduler { DebounceMs = 0 };
        scheduler.Load(graph);
        scheduler.AddBreakpoint("threshold");

        var completed = new List<string>();
        scheduler.NodeExecuted += e => { if (e.Phase == NodeExecutionPhase.Completed) completed.Add(e.NodeId); };
        var pauses = new Chan<string>();
        scheduler.DebugPaused += pauses.Push;

        using var cts = new CancellationTokenSource(10000);
        await scheduler.StartAsync(cts.Token);
        scheduler.Trigger(TriggerSource.Manual);

        Assert.Equal("threshold", await pauses.PopAsync(5000));
        scheduler.Step();                       // Step Over: run threshold, halt before result · 单步：执行 threshold,result 前停驻
        Assert.Equal("result", await pauses.PopAsync(5000));
        Assert.Contains("threshold", completed);
        Assert.DoesNotContain("result", completed);

        // Subscribe before Continue() — see the note in Breakpoint_HaltsBeforeNode_...
        using var done = new CancellationTokenSource(5000);
        var runTcs = new TaskCompletionSource<GraphRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.RunCompleted += r => runTcs.TrySetResult(r);
        scheduler.Continue();
        await runTcs.Task.WaitAsync(done.Token);
        Assert.Contains("result", completed);
    }

    [Fact]
    public async Task Step_FromFreerun_HaltsAtNextNode()
    {
        var graph = SchedulerTests.MakeHeadlessGraph();
        await using var scheduler = new GraphScheduler { DebounceMs = 0 };
        scheduler.Load(graph);

        var paused = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.DebugPaused += id => paused.TrySetResult(id);

        using var cts = new CancellationTokenSource(10000);
        await scheduler.StartAsync(cts.Token);
        scheduler.Trigger(TriggerSource.Manual);
        scheduler.Step();                       // running freely → halt at the next boundary · 运行中→下个节点前停驻

        var at = await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(at is "cam" or "threshold" or "result");
        scheduler.Continue();
        await scheduler.StopAsync();
    }

    [Fact]
    public async Task Pause_ThenContinue_CompletesCycle()
    {
        var graph = SchedulerTests.MakeHeadlessGraph();
        await using var scheduler = new GraphScheduler { DebounceMs = 0 };
        scheduler.Load(graph);

        var paused = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.DebugPaused += id => paused.TrySetResult(id);

        using var cts = new CancellationTokenSource(10000);
        await scheduler.StartAsync(cts.Token);
        scheduler.Trigger(TriggerSource.Manual);
        scheduler.Pause();

        Assert.NotNull(await paused.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(SchedulerState.Paused, scheduler.State);
        // Subscribe before Continue() — see the note in Breakpoint_HaltsBeforeNode_...
        using var done = new CancellationTokenSource(5000);
        var runTcs = new TaskCompletionSource<GraphRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.RunCompleted += r => runTcs.TrySetResult(r);
        scheduler.Continue();

        var result = await runTcs.Task.WaitAsync(done.Token);
        Assert.True(result.Success);
        Assert.Equal(3, result.SucceededNodes);
    }

    [Fact]
    public async Task RerunFromNode_ExecutesOnlyTheCone()
    {
        var log = new List<string>();
        var graph = new GraphModel();
        graph.AddNode(new ExecNode("start", log, execOut: true));
        graph.AddNode(new ExecNode("a", log));
        graph.AddNode(new ExecNode("b", log, execIn: true, execOut: false));
        graph.AddNode(new ExecNode("side", log, execIn: true, execOut: false));
        graph.Validate();
        graph.Connect(graph.Nodes["start"].Outputs[0], graph.Nodes["a"].Inputs[0]);
        graph.Connect(graph.Nodes["a"].Outputs[0], graph.Nodes["b"].Inputs[0]);
        graph.Connect(graph.Nodes["start"].Outputs[0], graph.Nodes["side"].Inputs[0]);
        graph.Validate();

        await using var scheduler = new GraphScheduler { DebounceMs = 0 };
        scheduler.Load(graph);
        using var cts = new CancellationTokenSource(10000);
        await scheduler.StartAsync(cts.Token);

        var firstTcs = new TaskCompletionSource<GraphRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.RunCompleted += r => firstTcs.TrySetResult(r);
        scheduler.Trigger(TriggerSource.Manual);
        await firstTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["start", "a", "side", "b"], log);

        log.Clear();
        var rerunResult = await scheduler.RunFromAsync("a", CancellationToken.None);
        Assert.True(rerunResult.Success);
        Assert.Equal(3, rerunResult.SucceededNodes);
        // cone(a) = {start, a, b}: the unrelated branch never executes · 锥(a)={start,a,b};无关分支不执行
        Assert.Equal(["start", "a", "b"], log);
        Assert.DoesNotContain("side", log);
    }

    [Fact]
    public async Task RerunFromNode_RequiresActiveSession()
    {
        var graph = SchedulerTests.MakeHeadlessGraph();
        await using var scheduler = new GraphScheduler();
        scheduler.Load(graph);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scheduler.RunFromAsync("cam", CancellationToken.None));
    }

    [Fact]
    public async Task RerunFromNode_UnknownId_Throws()
    {
        await using var scheduler = new GraphScheduler();
        scheduler.Load(SchedulerTests.MakeHeadlessGraph());
        await Assert.ThrowsAsync<ArgumentException>(
            () => scheduler.RunFromAsync("nope", CancellationToken.None));
    }

    [Fact]
    public async Task Rerun_WhilePaused_ResumeThenConeHaltsAgain()
    {
        var log = new List<string>();
        var graph = new GraphModel();
        graph.AddNode(new ExecNode("a", log));
        graph.AddNode(new ExecNode("b", log, execOut: false));
        graph.Validate();
        graph.Connect(graph.Nodes["a"].Outputs[0], graph.Nodes["b"].Inputs[0]);
        graph.Validate();

        await using var scheduler = new GraphScheduler { DebounceMs = 0 };
        scheduler.Load(graph);
        scheduler.AddBreakpoint("b");
        var pauses = new Chan<string>();
        scheduler.DebugPaused += pauses.Push;

        using var cts = new CancellationTokenSource(10000);
        await scheduler.StartAsync(cts.Token);
        scheduler.Trigger(TriggerSource.Manual);
        Assert.Equal("b", await pauses.PopAsync(5000));       // first halt · 首次停驻

        var rerunTask = scheduler.RunFromAsync("a", CancellationToken.None);
        await Task.Delay(50);
        scheduler.Continue();                                 // finish cycle1, loop starts the cone · 结束周期1,循环开始锥
        Assert.Equal("b", await pauses.PopAsync(5000));       // cone halts again · 锥再次停驻
        scheduler.Continue();
        var r = await rerunTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(r.Success);
    }

    [Fact]
    public async Task StopAsync_WhilePaused_Unblocks()
    {
        var graph = SchedulerTests.MakeHeadlessGraph();
        await using var scheduler = new GraphScheduler { DebounceMs = 0 };
        scheduler.Load(graph);
        scheduler.AddBreakpoint("threshold");

        var paused = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.DebugPaused += id => paused.TrySetResult(id);

        using var cts = new CancellationTokenSource(10000);
        await scheduler.StartAsync(cts.Token);
        scheduler.Trigger(TriggerSource.Manual);
        Assert.Equal("threshold", await paused.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        await scheduler.StopAsync();            // must unblock the gate await · 必须解除闸门等待
        Assert.Equal(SchedulerState.Stopped, scheduler.State);
        Assert.Null(scheduler.PausedNodeId);
    }

    [Fact]
    public void BreakpointCollection_AddRemoveClearWorks()
    {
        var scheduler = new GraphScheduler();
        Assert.Empty(scheduler.Breakpoints);
        scheduler.AddBreakpoint("a");
        scheduler.AddBreakpoint("b");
        Assert.True(scheduler.IsBreakpoint("a"));
        Assert.True(scheduler.IsBreakpoint("b"));
        Assert.False(scheduler.IsBreakpoint("c"));
        Assert.Equal(2, scheduler.Breakpoints.Count);
        scheduler.RemoveBreakpoint("a");
        Assert.False(scheduler.IsBreakpoint("a"));
        scheduler.ClearBreakpoints();
        Assert.Empty(scheduler.Breakpoints);
    }

    /// <summary>A single-producer FIFO of string tokens, used to await halt order deterministically. · 单生产者字符串队列,用于确定性等待停驻顺序</summary>
    private sealed class Chan<T> : IDisposable where T : notnull
    {
        private readonly SemaphoreSlim _sem = new(0);
        private readonly Queue<T> _items = new();

        public void Push(T item)
        {
            lock (_items) _items.Enqueue(item);
            _sem.Release();
        }

        public async Task<T> PopAsync(int timeoutMs)
        {
            if (!await _sem.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs)))
                throw new TimeoutException($"No token arrived within {timeoutMs}ms.");
            lock (_items) return _items.Dequeue();
        }

        public void Dispose() => _sem.Dispose();
    }

    /// <summary>Minimal exec-only node recording its execution order. · 仅控制流的极简节点,记录执行顺序</summary>
    private sealed class ExecNode : INode
    {
        private readonly IReadOnlyList<IPort> _inputs;
        private readonly IReadOnlyList<IPort> _outputs;
        private readonly List<string> _logTarget;

        public ExecNode(string id, List<string> log, bool execIn = true, bool execOut = true)
        {
            Id = id;
            Contract = new NodeContract("test.exec.debug", 1);
            _logTarget = log;
            _inputs = execIn ? [new Port(this, PortDirection.In)] : [];
            _outputs = execOut ? [new Port(this, PortDirection.Out)] : [];
        }

        public string Id { get; }
        public NodeContract Contract { get; }
        public IReadOnlyList<IPort> Inputs => _inputs;
        public IReadOnlyList<IPort> Outputs => _outputs;
        public NodeState State => NodeState.Idle;

        public Task ExecuteAsync(IExecutionContext ctx, CancellationToken ct)
        {
            lock (_logTarget) _logTarget.Add(Id);
            return Task.CompletedTask;
        }

        private sealed class Port(INode owner, PortDirection direction) : IPort
        {
            public INode Owner { get; } = owner;
            public string Name => "exec";
            public PortDirection Direction => direction;
            public PortKind Kind => PortKind.Exec;
            public ITypeDescriptor? Type => null;
            public bool IsConnected { get; set; }
            public object? Value { get; set; }
        }
    }
}