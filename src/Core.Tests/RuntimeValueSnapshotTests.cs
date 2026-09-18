using HalconWorkflow.Core.Execution;
using HalconWorkflow.Core.Graph;
using HalconWorkflow.Core.Model;
using Xunit;

namespace HalconWorkflow.Core.Tests;

/// <summary>
/// Stage-13 gate: the scheduler snapshots each node's data-output values right after it executes,
/// mirrors them onto the port Value reference, and carries them on the Completed event for live
/// scope display. Static-topology runs stay unaffected. · 阶段13 闸门：调度器在节点执行后立即
/// 快照其数据输出值：回写端口 Value 引用并随 Completed 事件携带(供运行期 scope 值显示)。
/// 静态拓扑执行语义不受影响。
/// </summary>
public sealed class RuntimeValueSnapshotTests
{
    [Fact]
    public async Task CompletedEvent_CarriesOnlyDataOutputValues()
    {
        var graph = SchedulerTests.MakeHeadlessGraph();
        var events = new List<NodeExecutionEvent>();
        await using var scheduler = new GraphScheduler();
        scheduler.NodeExecuted += events.Add;
        scheduler.Load(graph);

        var result = await scheduler.RunOnceAsync(CancellationToken.None);

        Assert.True(result.Success);
        var cam = events.Single(e => e.NodeId == "cam" && e.Phase == NodeExecutionPhase.Completed);
        var threshold = events.Single(e => e.NodeId == "threshold" && e.Phase == NodeExecutionPhase.Completed);
        var resultOut = events.Single(e => e.NodeId == "result" && e.Phase == NodeExecutionPhase.Completed);

        Assert.NotNull(cam.Values);
        Assert.True(cam.Values!.ContainsKey("Image"));
        Assert.DoesNotContain("exec", cam.Values.Keys, StringComparer.Ordinal);   // exec never captured · 控制流端口不参与

        Assert.NotNull(threshold.Values);
        Assert.True(threshold.Values!.ContainsKey("Region"));
        Assert.DoesNotContain("exec", threshold.Values.Keys, StringComparer.Ordinal);

        Assert.Null(resultOut.Values);   // no data outputs → no snapshot · 无数据输出则无快照
    }

    [Fact]
    public async Task CompletedEvent_SnapshotIsTakenImmediately_NotLeakedAcrossNodes()
    {
        var graph = SchedulerTests.MakeHeadlessGraph();
        var events = new List<NodeExecutionEvent>();
        await using var scheduler = new GraphScheduler();
        scheduler.NodeExecuted += events.Add;
        scheduler.Load(graph);

        await scheduler.RunOnceAsync(CancellationToken.None);

        // The "Region" produced by threshold must not appear on cam's snapshot even though the tag
        // table is flat — each node is captured right after its own execution. · 扁平 tag 表下，
        // threshold 产出的 Region 不应混入 cam 的快照——每个节点执行后立即捕获。
        var cam = events.Single(e => e.NodeId == "cam" && e.Phase == NodeExecutionPhase.Completed);
        Assert.NotNull(cam.Values);
        Assert.DoesNotContain("Region", cam.Values!.Keys, StringComparer.Ordinal);
        Assert.DoesNotContain("result", cam.Values!.Keys, StringComparer.Ordinal);
        Assert.Single(cam.Values);   // exactly one data output: "Image" · 恰有一个数据输出 "Image"
    }

    [Fact]
    public async Task CompletedEvent_WritesOutputPortValuesBack()
    {
        var graph = SchedulerTests.MakeHeadlessGraph();
        await using var scheduler = new GraphScheduler();
        scheduler.Load(graph);

        await scheduler.RunOnceAsync(CancellationToken.None);

        var cam = graph.Nodes["cam"];
        var threshold = graph.Nodes["threshold"];
        Assert.NotNull(cam.GetOutput("Image").Value);
        Assert.NotNull(threshold.GetOutput("Region").Value);

        // Exec ports never carry values. · 控制流端口从不携带运行值
        Assert.Null(cam.Outputs.First(p => p.Kind == PortKind.Exec).Value);
        Assert.Null(threshold.Outputs.First(p => p.Kind == PortKind.Exec).Value);
    }

    [Fact]
    public async Task StartedAndFaultedEvents_CarryNoValues()
    {
        var graph = SchedulerTests.MakeHeadlessGraph();
        var events = new List<NodeExecutionEvent>();
        await using var scheduler = new GraphScheduler();
        scheduler.NodeExecuted += events.Add;
        scheduler.Load(graph);

        await scheduler.RunOnceAsync(CancellationToken.None);

        Assert.All(events.Where(e => e.Phase == NodeExecutionPhase.Started), e => Assert.Null(e.Values));
    }
}