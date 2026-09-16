using HalconWorkflow.Core.Execution;
using HalconWorkflow.Core.Graph;
using HalconWorkflow.Core.Model;
using HalconWorkflow.Core.Serialization;
using HalconWorkflow.Runtime.Nodes;
using HalconWorkflow.Runtime.Trace;

namespace HalconWorkflow.Runtime;

/// <summary>
/// Headless execution host: loads a graph JSON, drives the scheduler, emits trace. 
/// 无头执行宿主：载入图 JSON、驱动调度器、输出追溯
/// </summary>
public sealed class HeadlessHost
{
    private readonly RuntimeOptions _options;
    private readonly TextWriter _out;

    public HeadlessHost(RuntimeOptions options, TextWriter? output = null)
    {
        _options = options;
        _out = output ?? Console.Out;
    }

    /// <summary>
    /// Loads the graph from disk using the selected node factory. · 用所选节点工厂从磁盘载入图
    /// </summary>
    public GraphModel LoadGraph(Action<string>? report = null)
    {
        if (!File.Exists(_options.GraphFile))
            throw new FileNotFoundException($"Graph file not found: {_options.GraphFile}");

        var json = File.ReadAllText(_options.GraphFile);
        var factory = BuildFactory(_options.Factory);
        var graph = GraphJsonSerializer.Deserialize(json, factory);
        var issues = graph.Validate();

        report?.Invoke($"Loaded graph: {graph.Nodes.Count} nodes, {graph.Links.Count} links.");
        foreach (var issue in issues)
            report?.Invoke($"  [validation] {issue.Kind}: {issue.Message}");

        if (issues.Any(i => i.Kind == ValidationIssueKind.Cycle))
            throw new InvalidOperationException("Graph has a cycle; refusing to run.");

        return graph;
    }

    /// <summary>
    /// Runs the host: single cycle or timer loop, with optional hard timeout and single-step pause.
    /// 运行宿主：单轮或定时循环，支持硬超时与单步暂停
    /// </summary>
    public async Task<int> RunAsync(CancellationToken ct, Action<string>? report = null)
    {
        var graph = LoadGraph(report ?? (_ => { }));
        if (graph.TopologicalOrder.Count == 0)
            throw new InvalidOperationException("Graph has no executable nodes.");

        using var trace = _options.TraceFile is null ? null : TraceWriter.Open(_options.TraceFile);
        await using var scheduler = new GraphScheduler { DebounceMs = 0 };
        scheduler.Load(graph);

        var totalRuns = 0;
        var totalFailed = 0;

        scheduler.NodeExecuted += e => OnNodeExecuted(e, scheduler, trace, ct);
        scheduler.RunCompleted += r =>
        {
            totalRuns++;
            if (!r.Success) totalFailed++;
            report?.Invoke($"Cycle #{totalRuns}: success={r.Success} okNodes={r.SucceededNodes} faulted={r.FaultedNodes} durationMs={(long)r.Duration.TotalMilliseconds}");
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_options.TimeoutMs is int timeout)
            cts.CancelAfter(timeout);

        if (_options.Once || _options.Cycles == 0)
        {
            // Single cycle then exit. · 单轮执行后退出
            var result = await scheduler.RunOnceAsync(cts.Token).ConfigureAwait(false);
            report?.Invoke(result.Success ? "DONE (ok)" : "DONE (failed)");
            return result.Success ? 0 : result.Success is false && result.Error == "Cancelled." ? 0 : 1;
        }

        // Timer loop: trigger every interval for Cycles rounds. · 定时循环：按间隔触发 Cycles 轮
        await scheduler.StartAsync(cts.Token);
        try
        {
            for (var run = 0; run < _options.Cycles && !cts.IsCancellationRequested; run++)
            {
                if (_options.Step && run > 0)
                    await WaitForStepAsync(ct);
                scheduler.Trigger(TriggerSource.Timer);
                await Task.Delay(_options.IntervalMs, cts.Token).ConfigureAwait(false);
            }
            if (!cts.IsCancellationRequested)
            {
                await Task.Delay(_options.IntervalMs, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            report?.Invoke("Cancelled by timeout or Ctrl+C.");
        }
        finally
        {
            await scheduler.StopAsync().ConfigureAwait(false);
        }

        report?.Invoke($"SUMMARY runs={totalRuns} failed={totalFailed}");
        return totalFailed == 0 ? 0 : 1;
    }

    private static INodeFactory BuildFactory(string name) => name switch
    {
        "sample" => new SampleNodeFactory(),
        _ => throw new NotSupportedException($"Unknown node factory '{name}'. Supported: sample")
    };

    private void OnNodeExecuted(NodeExecutionEvent e, GraphScheduler scheduler, TraceWriter? trace, CancellationToken ct)
    {
        if (!_options.Quiet)
            _out.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] {e.NodeId,-12} {e.Phase,-9} {e.ElapsedMs}ms {e.Error}");

        trace?.WriteNodeEvent(e, e.Signal, _options.Batch, null);
    }

    private static async Task WaitForStepAsync(CancellationToken ct)
    {
        Console.Write("— step: press ENTER to continue, or Ctrl+C to stop —");
        for (; ; )
        {
            var read = Task.Run(() => Console.In.ReadLine(), CancellationToken.None);
            var done = await Task.WhenAny(read, Task.Delay(50, ct)).ConfigureAwait(false);
            if (ReferenceEquals(done, read))
            {
                Console.WriteLine();
                return;
            }
        }
    }
}