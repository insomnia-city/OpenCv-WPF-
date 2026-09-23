using System.Globalization;
using HalconWorkflow.Abstractions;
using HalconWorkflow.App.Services;
using HalconWorkflow.App.ViewModels;
using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Runtime.Nodes;
using Xunit;

namespace HalconWorkflow.App.Tests;

/// <summary>
/// Stage-23 acceptance at the shell seams (§5.7): breakpoints described on the node projection
/// and audited, a halted run resumed / stepped through to completion, rerun-from-node audited
/// while a session is live, and pause semantics staying visible on the status bar.
/// · 阶段23 壳层验收(§5.7)：节点投影上的断点与审计、停驻后的继续/单步直到完成、
///   会话活动期间的从节点重跑审计,以及暂停语义在状态栏可见。
/// </summary>
public sealed class DebugIntegrationTests : IDisposable
{
    private readonly List<ShellViewModel> _shells = [];

    private ShellViewModel CreateShell()
    {
        var shell = new ShellViewModel(new LocalizationService(), new NoopDialogService());
        _shells.Add(shell);
        return shell;
    }

    public void Dispose()
    {
        foreach (var shell in _shells) shell.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void DebugKeys_ResolveInEveryLanguage()
    {
        var loc = new LocalizationService();
        foreach (var culture in new[] { "en-US", "zh-Hans", "ko-KR" })
        {
            loc.Culture = new CultureInfo(culture);
            foreach (var key in new[] { "menu.pause", "menu.resume", "menu.step", "menu.rerun", "menu.breakpoint", "status.paused" })
                Assert.False(string.IsNullOrWhiteSpace(loc[key]), $"{key} blank in {culture}");
        }
    }

    [Fact]
    public async Task ToggleBreakpoint_OnSelectedNode_AuditsAndMirrors()
    {
        var shell = CreateShell();
        var g = Add(shell, SampleNodes.Grabber("g"), 0, 0);
        shell.SelectNode(g);

        shell.ToggleSelectedBreakpointCommand.Execute(null);
        Assert.True(g.IsBreakpoint);
        var set = await WaitForAuditAsync(shell, new AuditFilter(Action: AuditActions.SetBreakpoint),
            rows => rows.Any(r => r.Target == "g" && r.After == "True"));

        shell.ToggleSelectedBreakpointCommand.Execute(null);
        Assert.False(g.IsBreakpoint);
        var removed = await WaitForAuditAsync(shell, new AuditFilter(Action: AuditActions.SetBreakpoint),
            rows => rows.Count(r => r.Target == "g") >= 2 && rows.Any(r => r.Target == "g" && r.After == "False"));

        Assert.NotEmpty(set);
        Assert.NotEmpty(removed);
    }

    [Fact]
    public async Task Breakpoint_HaltsRun_ResumeCompletes_WithElapsedBadge()
    {
        var shell = CreateShell();
        (var g, var t) = AddPair(shell);
        shell.SelectNode(t);
        shell.ToggleSelectedBreakpointCommand.Execute(null);   // halt before threshold · threshold 前停驻
        var issues = shell.Editor.Graph.Validate();
        Assert.True(issues.Count == 0, issues.Count > 0 ? issues[0].Message : "");

        shell.RunCommand.Execute(null);
        Assert.True(await WaitUntilAsync(() => shell.IsPaused, 8000), $"run did not halt, status='{shell.Status}'");

        Assert.True(shell.Status.Contains("t"), $"status should name the halted node, got '{shell.Status}'");
        Assert.True(shell.IsRunning, "a halted run is still a live session");

        shell.TogglePauseCommand.Execute(null);                // resume · 继续
        Assert.True(await WaitUntilAsync(() => !shell.IsPaused, 8000), "resume did not release the session");
        Assert.True(await WaitUntilAsync(() => !shell.IsRunning, 8000), "manual run did not finish after resume");

        Assert.NotNull(t.LastElapsedMsText);                   // stage-23 on-node timing badge · 阶段23 节点耗时徽标
        Assert.Matches(@"^\d+(\.\d+)? ms$", t.LastElapsedMsText!);
        Assert.NotNull(g.LastElapsedMsText);                   // g ran too, its badge persists · g 已运行,徽标保留
    }

    [Fact]
    public async Task StepFromBreakpoint_RunsOneNode_ThenFinishes()
    {
        var shell = CreateShell();
        (_, var t) = AddPair(shell);
        shell.SelectNode(t);
        shell.ToggleSelectedBreakpointCommand.Execute(null);

        shell.RunCommand.Execute(null);
        Assert.True(await WaitUntilAsync(() => shell.IsPaused, 8000), "run did not halt at breakpoint");

        shell.StepOnceCommand.Execute(null);                   // step over threshold → cycle ends · 单步越过 threshold
        Assert.True(await WaitUntilAsync(() => !shell.IsRunning, 8000), "step-over of the last node did not finish the run");
        Assert.NotNull(t.LastElapsedMsText);

        var steps = await WaitForAuditAsync(shell, new AuditFilter(Action: AuditActions.RunStep), r => r.Count > 0);
        Assert.NotEmpty(steps);
    }

    [Fact]
    public async Task RerunFromSelected_QueuedBehindPause_ThenRunsCone_AndAudits()
    {
        var shell = CreateShell();
        (var g, var t) = AddPair(shell);
        shell.SelectNode(t);
        shell.ToggleSelectedBreakpointCommand.Execute(null);   // halt before threshold · threshold 前停驻

        shell.RunCommand.Execute(null);
        Assert.True(await WaitUntilAsync(() => shell.IsPaused, 8000), "run did not halt at breakpoint");

        shell.RerunFromSelectedCommand.Execute(null);          // queued behind the paused cycle · 排于被暂停的周期之后
        await Task.Delay(50, CancellationToken.None);
        Assert.True(shell.IsPaused, "session left pause while the rerun was queued");

        shell.TogglePauseCommand.Execute(null);                // finish first cycle → loop starts the rerun cone
        var repaused = await WaitUntilAsync(() => shell.IsPaused, 15000);
        if (!repaused)
        {
            var dbg = string.Join(" | ", shell.Log.Entries.Select(e => $"{e.Level}:{e.Message}").TakeLast(14));
            Assert.True(repaused, $"rerun cone did not halt at the breakpoint again. running={shell.IsRunning} paused={shell.IsPaused} status='{shell.Status}' log: {dbg}");
        }

        shell.TogglePauseCommand.Execute(null);                // finish the rerun · 结束重跑
        Assert.True(await WaitUntilAsync(() => !shell.IsRunning, 8000), "manual run did not finish after rerun");

        var reruns = await WaitForAuditAsync(shell, new AuditFilter(Action: AuditActions.RunRerun),
            rows => rows.Any(r => r.Target == "t"));
        Assert.NotEmpty(reruns);
        Assert.NotNull(t.LastElapsedMsText);
    }

    [Fact]
    public async Task DebugCommands_WithoutSelectionOrRun_NoOp()
    {
        var shell = CreateShell();
        Add(shell, SampleNodes.Grabber("solo"), 0, 0);   // unique id keeps the shared audit store clean per assert · 唯一 id 使共享审计库断言精确

        shell.ToggleSelectedBreakpointCommand.Execute(null);   // no selection → no-op · 无选中→空操作
        var rows = await shell.AuditStore.QueryAsync(new AuditFilter(Action: AuditActions.SetBreakpoint, TargetContains: "solo"), CancellationToken.None);
        Assert.DoesNotContain(rows, r => r.Target == "solo");

        shell.StepOnceCommand.Execute(null);                   // not running → no-op · 未运行→空操作
        Assert.False(shell.IsRunning);
    }

    [Fact]
    public async Task DebugToolbar_GatingProps_FollowRunAndSelection()
    {
        var shell = CreateShell();
        shell.Loc.Culture = new CultureInfo("en-US");
        Add(shell, SampleNodes.Grabber("solo2"), 0, 0);

        // Not running, no selection → nothing debug-actionable.
        Assert.False(shell.CanPauseResume);
        Assert.False(shell.CanStep);
        Assert.False(shell.CanRerun);
        Assert.False(shell.CanSetBreakpoint);
        Assert.Equal("Pause", shell.MenuPauseResume);      // not paused → pause label

        // A selection outside a session enables only breakpoint toggle.
        var g = shell.Editor.Nodes[0];
        shell.SelectNode(g);
        Assert.True(shell.CanSetBreakpoint);
        Assert.False(shell.CanRerun);

        // Breakpoint on the grabber, then run: the session halts before it → live + paused.
        shell.ToggleSelectedBreakpointCommand.Execute(null);
        shell.RunCommand.Execute(null);
        Assert.True(await WaitUntilAsync(() => shell.IsPaused, 8000), "run did not halt for toolbar gating");

        Assert.True(shell.CanPauseResume);
        Assert.True(shell.CanStep);                            // paused at breakpoint → step enabled
        Assert.True(shell.CanRerun);                           // live + selection
        Assert.False(shell.CanSetBreakpoint);                  // mid-session
        Assert.Equal("Resume", shell.MenuPauseResume);         // paused → resume label

        // Resume finishes the manual session; toolbar gates collapse.
        shell.TogglePauseCommand.Execute(null);                // resume
        Assert.True(await WaitUntilAsync(() => !shell.IsRunning, 8000), "manual run did not finish after resume");
        Assert.False(shell.CanPauseResume);
        Assert.False(shell.CanStep);
        Assert.Equal("Pause", shell.MenuPauseResume);
    }

    private static NodeViewModel Add(ShellViewModel shell, INode node, double x, double y)
        => shell.Editor.AddNode(node, x, y)!;

    /// <summary>grabber → threshold with exec + Image data links, so the graph validates cleanly. · grabber → threshold 含执行与图像数据线,图校验干净</summary>
    private static (NodeViewModel G, NodeViewModel T) AddPair(ShellViewModel shell)
    {
        var g = Add(shell, SampleNodes.Grabber("g"), 0, 0);
        var t = Add(shell, SampleNodes.Threshold("t"), 300, 0);
        Drag(shell, g.Outputs[0], t.Inputs[0]);   // exec out → exec in
        Drag(shell, g.Outputs[1], t.Inputs[1]);   // Image out → Image in
        return (g, t);
    }

    private static void Drag(ShellViewModel shell, PortViewModel source, PortViewModel target)
    {
        shell.ConnectionStartedCommand.Execute(source);
        shell.ConnectionCompletedCommand.Execute(target);
    }

    private static async Task<IReadOnlyList<OperationRecord>> WaitForAuditAsync(
        ShellViewModel shell, AuditFilter filter, Func<IReadOnlyList<OperationRecord>, bool>? until = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        IReadOnlyList<OperationRecord> rows = [];
        while (DateTime.UtcNow < deadline)
        {
            rows = await shell.AuditStore.QueryAsync(filter, CancellationToken.None);
            if (until is null ? rows.Count > 0 : until(rows)) break;
            await Task.Delay(50);
        }
        return rows;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }

    private sealed class NoopDialogService : IDialogService
    {
        public string? OpenGraphFile(string filter) => null;
        public string? SaveGraphFile(string defaultName, string filter) => null;
        public string? SaveCsvFile(string defaultName) => null;
        public bool Confirm(string message) => true;
        public void ReportError(string message) { }
        public void ShowImageWindow(ImageWindowViewModel vm) { }
    }
}
