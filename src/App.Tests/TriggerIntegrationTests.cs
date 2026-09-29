using HalconWorkflow.App.Services;
using HalconWorkflow.App.ViewModels;
using HalconWorkflow.Core.Graph;
using HalconWorkflow.Core.Model;
using Xunit;

namespace HalconWorkflow.App.Tests;

/// <summary>
/// Stage-20 acceptance at the shell seams: background triggers (timer / tag change) drive extra
/// cycles until Stop, the trigger-settings pipeline lands as an undoable audited edit, and the
/// settings view-model clamps inputs without touching the config until Apply.
/// · 阶段20 壳层验收：后台触发(定时/Tag 变化)驱动额外周期直到 Stop;触发设置管线以可撤销、
///   可审计的编辑落地;设置视图模型在 Apply 之前只做钳制、不改动配置。
/// </summary>
public sealed class TriggerIntegrationTests : IDisposable
{
    private readonly List<ShellViewModel> _shells = [];
    private readonly MutatingDialogService _dialog = new();

    private ShellViewModel CreateShell()
    {
        var shell = TestShell.Create(new LocalizationService(), _dialog);
        _shells.Add(shell);
        return shell;
    }

    public void Dispose()
    {
        foreach (var shell in _shells) shell.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task TimerSource_DrivesCycles_UntilStopped()
    {
        var shell = CreateShell();
        shell.Editor.Graph.Trigger.Enabled = true;
        shell.Editor.Graph.Trigger.Source = TriggerSource.Timer;
        shell.Editor.Graph.Trigger.IntervalMs = 40;
        shell.Editor.Graph.Trigger.DebounceMs = 0;
        shell.AddNodeCommand.Execute(shell.Palette.First(p => p.Key == "start"));
        Assert.Empty(shell.Editor.Graph.Validate());

        shell.RunCommand.Execute(null);
        Assert.True(await WaitUntilAsync(() => FinishedCount(shell) >= 3, 8000),
            $"timer did not drive ≥3 cycles in 8s, saw {FinishedCount(shell)}");

        shell.StopCommand.Execute(null);
        Assert.True(await WaitUntilAsync(() => !shell.IsRunning), "stop did not end the background run");

        var steady = FinishedCount(shell);
        await Task.Delay(350);
        Assert.Equal(steady, FinishedCount(shell)); // engine actually stopped · 引擎确实已停止
    }

    [Fact]
    public async Task TagChangeSource_DrivesCycle()
    {
        var shell = CreateShell();
        shell.Editor.Graph.Trigger.Enabled = true;
        shell.Editor.Graph.Trigger.Source = TriggerSource.TagChange;
        shell.Editor.Graph.Trigger.Tag = "demo/coil/run";
        shell.Editor.Graph.Trigger.DebounceMs = 0;
        shell.AddNodeCommand.Execute(shell.Palette.First(p => p.Key == "start"));
        Assert.Empty(shell.Editor.Graph.Validate());

        var dev = shell.Comm.Resolve("demo");
        Assert.NotNull(dev);

        shell.RunCommand.Execute(null);
        Assert.True(await WaitUntilAsync(() => FinishedCount(shell) >= 1, 8000),
            "manual kick did not run a cycle");

        await Task.Delay(800); // let the poller establish its baseline · 让轮询器先建立基线
        await dev!.WriteAsync("demo/coil/run", true, CancellationToken.None);

        Assert.True(await WaitUntilAsync(() => FinishedCount(shell) >= 2, 8000),
            $"tag change did not drive a second cycle, saw {FinishedCount(shell)}");

        shell.StopCommand.Execute(null);
        Assert.True(await WaitUntilAsync(() => !shell.IsRunning), "stop did not end the tag-driven run");
    }

    [Fact]
    public void TriggerSettings_MutateDialog_AppliesClampedConfig()
    {
        var loc = new LocalizationService();
        var config = new TriggerConfig { Enabled = false, Source = TriggerSource.Manual, IntervalMs = 10, DebounceMs = -5, Tag = "  demo/x/y " };
        var vm = new TriggerSettingsViewModel(config, loc);

        Assert.Equal(TriggerSource.Manual, vm.Source.Source); // dialogs read unlisted sources as manual · 未列出来源按手动阅读
        Assert.Equal(3, vm.Sources.Count);
        Assert.Equal(loc["trigger.timer"], vm.Sources[1].Name);

        vm.Enabled = true;
        vm.Source = vm.Sources[1];
        vm.IntervalMs = -999;
        vm.DebounceMs = int.MaxValue;
        vm.Tag = "demo/coil/run";
        vm.Apply();

        Assert.True(config.Enabled);
        Assert.Equal(TriggerSource.Timer, config.Source);
        Assert.Equal(50, config.IntervalMs);                 // clamped up · 下限钳制
        Assert.Equal(int.MaxValue, config.DebounceMs);       // debounce has no upper clamp · 去抖无上限
        Assert.Equal("demo/coil/run", config.Tag);

        vm.Apply();                                          // second apply is idempotent · 二次 Apply 幂等
        Assert.Equal(50, config.IntervalMs);
    }

    [Fact]
    public void TriggerSettings_CancelLeavesConfigUntouched()
    {
        var config = new TriggerConfig { Enabled = false, Source = TriggerSource.Manual, Tag = null };
        var vm = new TriggerSettingsViewModel(config, new LocalizationService());
        vm.Enabled = true;
        vm.Source = vm.Sources[2];
        vm.Tag = "demo/coil/run";
        // no Apply → the live graph config must stay as it was · 不 Apply → 图配置保持不变
        Assert.False(config.Enabled);
        Assert.Equal(TriggerSource.Manual, config.Source);
        Assert.Null(config.Tag);
    }

    [Fact]
    public async Task TriggerSettingsCommand_PipelineLandsAuditedUndoableEdit()
    {
        var shell = CreateShell();
        _dialog.Enabled = true;
        _dialog.Source = TriggerSource.Timer;
        _dialog.IntervalMs = 250;

        shell.TriggerSettingsCommand.Execute(null);
        await WaitUntilAsync(() => shell.Log.Entries.Any(e => e.Message.StartsWith("Trigger config:")), 2000);

        Assert.True(shell.Editor.Graph.Trigger.Enabled);
        Assert.Equal(TriggerSource.Timer, shell.Editor.Graph.Trigger.Source);
        Assert.Equal(250, shell.Editor.Graph.Trigger.IntervalMs);
        Assert.True(shell.UndoCommand.CanExecute(null), "trigger edit must be undoable");
    }

    private static int FinishedCount(ShellViewModel shell)
        // Snapshot, not live enumeration: the scheduler thread appends log rows while
        // this polls, which made the read throw "Collection was modified".
        // 取快照而非实时枚举：调度线程在轮询期间追加日志行，曾导致读取抛
        // "Collection was modified"。
        => shell.Log.Snapshot().Count(e => e.Message.StartsWith("Run finished", StringComparison.Ordinal));

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }

    private class NoopDialogService : IDialogService
    {
        public string? OpenGraphFile(string filter) => null;
        public string? SaveGraphFile(string defaultName, string filter) => null;
        public string? SaveCsvFile(string defaultName) => null;
        public bool Confirm(string message) => true;
        public void ReportError(string message) { }
        public void ShowImageWindow(ImageWindowViewModel vm) { }
        public virtual bool EditTrigger(Core.Graph.TriggerConfig config) => false;
    }

    private sealed class MutatingDialogService : NoopDialogService
    {
        public bool Enabled { get; set; }
        public TriggerSource Source { get; set; }
        public int IntervalMs { get; set; }

        public override bool EditTrigger(Core.Graph.TriggerConfig config)
        {
            config.Enabled = Enabled;
            config.Source = Source;
            config.IntervalMs = IntervalMs;
            config.DebounceMs = 10;
            config.Tag = null;
            return true;
        }
    }
}