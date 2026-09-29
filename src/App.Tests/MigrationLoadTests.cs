using HalconWorkflow.App.Services;
using HalconWorkflow.App.ViewModels;
using Xunit;

namespace HalconWorkflow.App.Tests;

/// <summary>
/// Stage-19 gate (§12) at the shell seam: opening a legacy document prompts before the
/// in-memory upgrade, suspended nodes can be kept or removed, and the first save after
/// an upgrade asks for a second confirmation. /
/// 阶段19 门(§12) 壳层接缝：打开遗留文档在内存升级前提示，挂起节点可保留或移除，
/// 升级后首次保存二次确认。
/// </summary>
public sealed class MigrationLoadTests : IDisposable
{
    private readonly List<ShellViewModel> _shells = [];
    private readonly List<string> _dirs = [];

    public void Dispose()
    {
        foreach (var shell in _shells) shell.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        foreach (var dir in _dirs)
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class QueueDialogService : IDialogService
    {
        public string? OpenGraphPath { get; set; }
        public string? SaveGraphPath { get; set; }
        public Queue<bool> Confirms { get; } = new();
        public List<string> Messages { get; } = [];

        public string? OpenGraphFile(string filter) => OpenGraphPath;
        public string? SaveGraphFile(string defaultName, string filter) => SaveGraphPath;
        public string? SaveCsvFile(string defaultName) => null;
        public void ReportError(string message) { }
        public void ShowImageWindow(ImageWindowViewModel vm) { }

        public bool Confirm(string message)
        {
            Messages.Add(message);
            return Confirms.Count > 0 ? Confirms.Dequeue() : true;
        }
    }

    private string WriteDocument(string json)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"hwf-migrate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        var path = Path.Combine(dir, "flow.graph.json");
        File.WriteAllText(path, json);
        return path;
    }

    private ShellViewModel CreateShell(QueueDialogService dialogs)
    {
        var shell = TestShell.Create(new LocalizationService(), dialogs);
        _shells.Add(shell);
        return shell;
    }

    private const string LegacyThreshold = """
        {"schema":"vision.workflow/graph",
         "nodes":[{"id":"t","contract":{"ns":"vision.threshold","version":2},"pos":{"x":0,"y":0}}],
         "links":[]}
        """;

    private const string SuspendedDoc = """
        {"schema":"vision.workflow/graph","schemaVersion":1,
         "nodes":[{"id":"ghost","contract":{"ns":"vision.gone","version":1},"pos":{"x":0,"y":0}}],
         "links":[]}
        """;

    [Fact]
    public void Load_LegacyDocument_UpgradesWhenConfirmed()
    {
        var path = WriteDocument(LegacyThreshold);
        var dialogs = new QueueDialogService { OpenGraphPath = path };
        dialogs.Confirms.Enqueue(true);
        var shell = CreateShell(dialogs);

        shell.LoadCommand.Execute(null);

        Assert.Single(shell.Editor.Graph.Nodes);
        Assert.Contains(shell.Editor.Graph.Nodes.Values, n => n.Contract.Namespace == "vision.threshold");
        Assert.NotEmpty(dialogs.Messages);
    }

    [Fact]
    public void Load_LegacyDocument_Cancelled_LeavesGraphUntouched()
    {
        var path = WriteDocument(LegacyThreshold);
        var dialogs = new QueueDialogService { OpenGraphPath = path };
        dialogs.Confirms.Enqueue(false);
        var shell = CreateShell(dialogs);

        shell.LoadCommand.Execute(null);

        Assert.Empty(shell.Editor.Graph.Nodes);
    }

    [Fact]
    public void Load_SuspendedNodes_KeptWhenConfirmed()
    {
        var path = WriteDocument(SuspendedDoc);
        var dialogs = new QueueDialogService { OpenGraphPath = path };
        dialogs.Confirms.Enqueue(true); // keep suspended nodes · 保留挂起节点
        var shell = CreateShell(dialogs);

        shell.LoadCommand.Execute(null);

        var ghost = Assert.Single(shell.Editor.Graph.Nodes.Values);
        Assert.True(ghost.IsSuspended);
    }

    [Fact]
    public void Load_SuspendedNodes_RemovedWhenDeclined()
    {
        var path = WriteDocument(SuspendedDoc);
        var dialogs = new QueueDialogService { OpenGraphPath = path };
        dialogs.Confirms.Enqueue(false); // remove suspended nodes · 移除挂起节点
        var shell = CreateShell(dialogs);

        shell.LoadCommand.Execute(null);

        Assert.Empty(shell.Editor.Graph.Nodes);
    }

    [Fact]
    public void Save_AfterMigration_RequiresSecondConfirmation()
    {
        var path = WriteDocument(LegacyThreshold);
        var dialogs = new QueueDialogService { OpenGraphPath = path, SaveGraphPath = path };
        dialogs.Confirms.Enqueue(true);  // confirm the in-memory upgrade · 确认内存升级
        var shell = CreateShell(dialogs);

        shell.LoadCommand.Execute(null);
        Assert.Single(shell.Editor.Graph.Nodes);

        dialogs.Confirms.Enqueue(false); // decline the save confirmation · 拒绝落盘确认
        shell.SaveCommand.Execute(null);
        Assert.NotEmpty(dialogs.Messages);

        dialogs.Confirms.Enqueue(true);  // accept the second save · 接受二次保存
        shell.SaveCommand.Execute(null);

        Assert.Contains("\"schemaVersion\": 1", File.ReadAllText(path));
    }
}
