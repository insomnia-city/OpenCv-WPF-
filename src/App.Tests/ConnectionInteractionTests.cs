using System.Windows;
using HalconWorkflow.Abstractions;
using HalconWorkflow.App.Services;
using HalconWorkflow.App.ViewModels;
using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Runtime.Nodes;
using Xunit;

namespace HalconWorkflow.App.Tests;

/// <summary>
/// Stage-12 acceptance: interactive connections via the pending-connection commands (§4.3).
/// Every create/delete goes through the global undo path with permission + audit (§9.2/§9.3);
/// the killer validation reuses the kernel Validator used by save-time static validation.
/// / 阶段12 验收：经拖拽连线命令的交互连线(§4.3)。
///   建线/断线均走全局撤销路径(§9.2/§9.3)；校验与保存时静态校验同源(内核)。
/// </summary>
public sealed class ConnectionInteractionTests : IDisposable
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
    public void Connect_CreatesExecLink_UndoRestoresAndRedoReapplies()
    {
        var shell = CreateShell();
        var g = Add(shell, SampleNodes.Grabber("g"), 0, 0);
        var t = Add(shell, SampleNodes.Threshold("t"), 300, 0);

        Drag(shell, g.Outputs[0], t.Inputs[0]);   // exec out → exec in

        var link = shell.Editor.Graph.Links.Single();
        Assert.Equal("g", link.From.Owner.Id);
        Assert.Equal("t", link.To.Owner.Id);
        Assert.Single(shell.Editor.Connections);

        shell.UndoCommand.Execute(null);
        Assert.Empty(shell.Editor.Graph.Links);
        Assert.Empty(shell.Editor.Connections);

        shell.RedoCommand.Execute(null);
        Assert.Single(shell.Editor.Graph.Links);
        Assert.Single(shell.Editor.Connections);
    }

    [Fact]
    public void Connect_NormalizesReversedDragDirection()
    {
        var shell = CreateShell();
        var g = Add(shell, SampleNodes.Grabber("g"), 0, 0);
        var t = Add(shell, SampleNodes.Threshold("t"), 300, 0);

        // Dragging from the input towards the output still connects output → input.
        // 从输入拖向输出时仍按 输出→输入 建线
        Drag(shell, t.Inputs[0], g.Outputs[0]);

        var link = shell.Editor.Graph.Links.Single();
        Assert.Equal("g", link.From.Owner.Id);
        Assert.Equal("t", link.To.Owner.Id);
    }

    [Fact]
    public void Connect_RejectsTypeMismatchedDataPorts()
    {
        var shell = CreateShell();
        var g = Add(shell, SampleNodes.Grabber("g"), 0, 0);
        var d = Add(shell, SampleNodes.Decision("d"), 300, 200);

        Drag(shell, g.Outputs[1], d.Inputs[1]);   // Image out → Region in

        Assert.Empty(shell.Editor.Graph.Links);
        Assert.Empty(shell.Editor.Connections);
    }

    [Fact]
    public void Connect_RejectsSameDirectionAndExecDataCrossing()
    {
        var shell = CreateShell();
        var g = Add(shell, SampleNodes.Grabber("g"), 0, 0);
        var t = Add(shell, SampleNodes.Threshold("t"), 300, 0);

        Drag(shell, g.Outputs[0], t.Outputs[0]);   // out → out
        Assert.Empty(shell.Editor.Graph.Links);

        Drag(shell, g.Outputs[1], t.Inputs[0]);    // data out → exec in
        Assert.Empty(shell.Editor.Graph.Links);
    }

    [Fact]
    public void Connect_RejectsSelfConnection()
    {
        var shell = CreateShell();
        var g = Add(shell, SampleNodes.Grabber("g"), 0, 0);

        Drag(shell, g.Outputs[0], g.Inputs[0]);

        Assert.Empty(shell.Editor.Graph.Links);
    }

    [Fact]
    public void Connect_RejectsCycle()
    {
        var shell = CreateShell();
        var g = Add(shell, SampleNodes.Grabber("g"), 0, 0);
        var t = Add(shell, SampleNodes.Threshold("t"), 300, 0);
        var d = Add(shell, SampleNodes.Decision("d"), 600, 0);

        Drag(shell, g.Outputs[0], t.Inputs[0]);
        Drag(shell, t.Outputs[0], d.Inputs[0]);
        Assert.Equal(2, shell.Editor.Graph.Links.Count);

        Drag(shell, d.Outputs[0], g.Inputs[0]);   // closes the loop · 成环
        Assert.Equal(2, shell.Editor.Graph.Links.Count);
    }

    [Fact]
    public void Connect_RejectsSecondSourceOnFedDataInput()
    {
        var shell = CreateShell();
        var g1 = Add(shell, SampleNodes.Grabber("g1"), 0, 0);
        var g2 = Add(shell, SampleNodes.Grabber("g2"), 0, 400);
        var t = Add(shell, SampleNodes.Threshold("t"), 300, 0);

        Drag(shell, g1.Outputs[1], t.Inputs[1]);   // first Image source feeds t.Image
        Assert.Single(shell.Editor.Graph.Links);

        Drag(shell, g2.Outputs[1], t.Inputs[1]);   // second source refused · 第二来源被拒
        Assert.Single(shell.Editor.Graph.Links);
    }

    [Fact]
    public async Task Connect_RequiresEngineerRoleAndAuditsDenial()
    {
        var shell = CreateShell();
        var g = Add(shell, SampleNodes.Grabber("g"), 0, 0);
        var t = Add(shell, SampleNodes.Threshold("t"), 300, 0);
        shell.SelectedRole = shell.RoleOptions.Single(r => r.Value == UserRole.Operator);

        Drag(shell, g.Outputs[0], t.Inputs[0]);

        Assert.Empty(shell.Editor.Graph.Links);
        Assert.Empty(shell.Editor.Connections);
        var denied = await QueryAuditAsync(shell, new AuditFilter(Action: AuditActions.AccessDenied));
        Assert.Contains(denied, r => r.Target == AuditActions.Connect);
    }

    [Fact]
    public async Task Connect_AuditsSuccessfulLink()
    {
        var shell = CreateShell();
        var g = Add(shell, SampleNodes.Grabber("g"), 0, 0);
        var t = Add(shell, SampleNodes.Threshold("t"), 300, 0);

        Drag(shell, g.Outputs[0], t.Inputs[0]);

        var rows = await WaitForAuditAsync(shell, new AuditFilter(Action: AuditActions.Connect),
            rows => rows.Any(r => r.Target == "g:exec→t:exec"));
        Assert.Contains(rows, r => r.Target == "g:exec→t:exec" && r.User == shell.CurrentUser);
    }

    [Fact]
    public void ConnectionStarted_HighlightsValidAndInvalidTargets_ThenClearsOnDrop()
    {
        var shell = CreateShell();
        var g = Add(shell, SampleNodes.Grabber("g"), 0, 0);
        var t = Add(shell, SampleNodes.Threshold("t"), 300, 0);

        shell.ConnectionStartedCommand.Execute(g.Outputs[1]);   // drag an Image out

        Assert.Equal(ConnectState.None, g.Outputs[1].Highlight);                 // source itself unmarked
        Assert.Equal(ConnectState.Valid, t.Inputs[1].Highlight);                 // Image in ← Image out
        Assert.Equal(ConnectState.Invalid, t.Inputs[0].Highlight);               // exec in ← data out
        Assert.Equal(ConnectState.Invalid, g.Inputs[0].Highlight);               // same-node self link
        Assert.Equal(ConnectState.Invalid, g.Outputs[0].Highlight);              // out → out

        shell.ConnectionCompletedCommand.Execute(t.Inputs[1]);
        Assert.All(shell.Editor.Nodes.SelectMany(n => n.Inputs.Concat(n.Outputs)),
            p => Assert.Equal(ConnectState.None, p.Highlight));
    }

    [Fact]
    public void Disconnect_ViaConnectionMenu_RemovesLinkAndUndoRestores()
    {
        var shell = CreateShell();
        var g = Add(shell, SampleNodes.Grabber("g"), 0, 0);
        var t = Add(shell, SampleNodes.Threshold("t"), 300, 0);
        Drag(shell, g.Outputs[0], t.Inputs[0]);
        var conn = shell.Editor.Connections.Single();

        conn.DisconnectCommand.Execute(null);

        Assert.Empty(shell.Editor.Graph.Links);
        shell.UndoCommand.Execute(null);
        Assert.Single(shell.Editor.Graph.Links);
    }

    [Fact]
    public void DisconnectConnector_RemovesAllIncidentLinks_UndoRestores()
    {
        var shell = CreateShell();
        var g = Add(shell, SampleNodes.Grabber("g"), 0, 0);
        var t = Add(shell, SampleNodes.Threshold("t"), 300, 0);
        var d = Add(shell, SampleNodes.Decision("d"), 600, 0);
        Drag(shell, g.Outputs[0], t.Inputs[0]);
        Drag(shell, g.Outputs[0], d.Inputs[0]);
        Assert.Equal(2, shell.Editor.Graph.Links.Count);

        shell.DisconnectConnectorCommand.Execute(g.Outputs[0]);

        Assert.Empty(shell.Editor.Graph.Links);
        shell.UndoCommand.Execute(null);          // one disconnect per undo step · 一次撤销回滚一条断线
        Assert.Single(shell.Editor.Graph.Links);
        shell.UndoCommand.Execute(null);
        Assert.Equal(2, shell.Editor.Graph.Links.Count);
    }

    [Fact]
    public void Disconnect_NormalizedLinkProjectionMatchesKernel()
    {
        var shell = CreateShell();
        var g = Add(shell, SampleNodes.Grabber("g"), 0, 0);
        var t = Add(shell, SampleNodes.Threshold("t"), 300, 0);
        Drag(shell, t.Inputs[0], g.Outputs[0]);            // reversed drag still created g→t

        var conn = shell.Editor.Connections.Single();
        Assert.Equal("g", conn.From.Id);
        Assert.Equal("t", conn.To.Id);
    }

    private static NodeViewModel Add(ShellViewModel shell, INode node, double x, double y)
        => shell.Editor.AddNode(node, x, y)!;

    private static void Drag(ShellViewModel shell, PortViewModel source, PortViewModel target)
    {
        shell.ConnectionStartedCommand.Execute(source);
        shell.ConnectionCompletedCommand.Execute(target);
    }

    private static async Task<IReadOnlyList<OperationRecord>> QueryAuditAsync(ShellViewModel shell, AuditFilter filter)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var rows = await shell.AuditStore.QueryAsync(filter, CancellationToken.None);
            if (rows.Count > 0) return rows;
            await Task.Delay(50);
        }
        return await shell.AuditStore.QueryAsync(filter, CancellationToken.None);
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