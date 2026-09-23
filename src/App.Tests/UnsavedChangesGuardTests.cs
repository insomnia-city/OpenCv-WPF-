using System.Globalization;
using HalconWorkflow.Abstractions;
using HalconWorkflow.App.Services;
using HalconWorkflow.App.ViewModels;
using Xunit;

namespace HalconWorkflow.App.Tests;

/// <summary>
/// Stage-16 gate: unsaved-changes guard. A clean shell closes silently; a dirty shell refuses to
/// close/discard on request, and the discard prompt is localized.
/// / 阶段16 闸门：未保存改动防护。干净壳静默关闭；脏壳按用户意愿拒绝关闭/放弃；放弃提示本地化。
/// </summary>
public sealed class UnsavedChangesGuardTests : IDisposable
{
    private readonly List<ShellViewModel> _shells = [];

    private ShellViewModel CreateShell(IDialogService dialogs)
    {
        var shell = new ShellViewModel(new LocalizationService(), dialogs);
        _shells.Add(shell);
        return shell;
    }

    public void Dispose()
    {
        foreach (var shell in _shells) shell.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void FreshShell_IsClean_AndClosesWithoutPrompt()
    {
        var dialogs = new RecordingDialogService();
        var shell = CreateShell(dialogs);

        Assert.False(shell.HasUnsavedChanges);
        Assert.True(shell.ConfirmClose());
        Assert.Equal(0, dialogs.ConfirmCalls);
    }

    [Fact]
    public void PaletteAdd_MarksDirty_AndUndoReturnsToClean()
    {
        var shell = CreateShell(new RecordingDialogService());

        shell.AddNodeCommand.Execute(shell.Palette.First(p => p.Key == "threshold"));
        Assert.True(shell.HasUnsavedChanges);

        shell.UndoCommand.Execute(null);
        Assert.False(shell.HasUnsavedChanges); // back on the empty saved path · 回到空的已保存路径
    }

    [Fact]
    public void Close_WithUnsavedChanges_Prompts_AndHonoursRefusal()
    {
        var dialogs = new RecordingDialogService { ConfirmResult = false };
        var shell = CreateShell(dialogs);
        shell.AddNodeCommand.Execute(shell.Palette.First(p => p.Key == "threshold"));

        Assert.False(shell.ConfirmClose());
        Assert.Equal(1, dialogs.ConfirmCalls);
    }

    [Fact]
    public void New_WithUnsavedChanges_Refused_KeepsGraph()
    {
        var dialogs = new RecordingDialogService { ConfirmResult = false };
        var shell = CreateShell(dialogs);
        shell.AddNodeCommand.Execute(shell.Palette.First(p => p.Key == "threshold"));

        shell.NewCommand.Execute(null);

        Assert.Single(shell.Editor.Nodes);
        Assert.Equal(1, dialogs.ConfirmCalls);
    }

    [Fact]
    public void New_WithUnsavedChanges_Accepted_ClearsGraph()
    {
        var dialogs = new RecordingDialogService { ConfirmResult = true };
        var shell = CreateShell(dialogs);
        shell.AddNodeCommand.Execute(shell.Palette.First(p => p.Key == "threshold"));

        shell.NewCommand.Execute(null);

        Assert.Empty(shell.Editor.Nodes);
        Assert.False(shell.HasUnsavedChanges);
    }

    [Fact]
    public void New_WhenClean_DoesNotPrompt()
    {
        var dialogs = new RecordingDialogService();
        var shell = CreateShell(dialogs);

        shell.NewCommand.Execute(null);

        Assert.Equal(0, dialogs.ConfirmCalls);
    }

    [Fact]
    public void DiscardPrompt_IsLocalized()
    {
        var dialogs = new RecordingDialogService { ConfirmResult = true };
        var shell = CreateShell(dialogs);
        shell.Loc.Culture = new CultureInfo("zh-Hans");
        shell.AddNodeCommand.Execute(shell.Palette.First(p => p.Key == "threshold"));

        Assert.True(shell.ConfirmClose());
        Assert.Equal("放弃未保存的修改？", dialogs.LastConfirmMessage);
    }

    private sealed class RecordingDialogService : IDialogService
    {
        public bool ConfirmResult { get; set; } = true;
        public int ConfirmCalls { get; private set; }
        public string? LastConfirmMessage { get; private set; }

        public string? OpenGraphFile(string filter) => null;
        public string? SaveGraphFile(string defaultName, string filter) => null;
        public string? SaveCsvFile(string defaultName) => null;
        public void ReportError(string message) { }
        public void ShowImageWindow(ImageWindowViewModel vm) { }

        public bool Confirm(string message)
        {
            ConfirmCalls++;
            LastConfirmMessage = message;
            return ConfirmResult;
        }
    }
}