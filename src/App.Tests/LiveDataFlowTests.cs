using HalconWorkflow.App.Services;
using HalconWorkflow.App.ViewModels;
using Xunit;

namespace HalconWorkflow.App.Tests;

/// <summary>
/// Stage-13 acceptance: a real run through the shell drives live scope values onto canvas port
/// badges, writes them back onto kernel ports, and feeds the preview ring with encoded images so
/// the dashboard preview keeps up during execution. · 阶段13 验收：经壳层真实运行一轮，运行期
/// scope 值驱动画布端口徽标与内核端口回写，并把编码图像推入预览环、看板预览实时跟进。
/// </summary>
public sealed class LiveDataFlowTests : IDisposable
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
    public async Task Run_ScopeValuesFlowToPortBadges_AndPublishPreviewImages()
    {
        var shell = CreateShell();
        shell.AddNodeCommand.Execute(shell.Palette.First(p => p.Key == "start"));
        shell.AddNodeCommand.Execute(shell.Palette.First(p => p.Key == "grabber"));
        shell.AddNodeCommand.Execute(shell.Palette.First(p => p.Key == "threshold"));

        var start = shell.Editor.Nodes.First(n => n.Kernel.Contract.Namespace == "test.start");
        var grab = shell.Editor.Nodes.First(n => n.Kernel.Contract.Namespace == "vision.grab");
        var thr = shell.Editor.Nodes.First(n => n.Kernel.Contract.Namespace == "vision.threshold");

        Assert.NotNull(shell.Editor.Graph.Connect(
            start.Outputs.Single(p => p.IsExec && p.IsOutput).Kernel,
            grab.Inputs.Single(p => p.IsExec && p.IsInput).Kernel));
        Assert.NotNull(shell.Editor.Graph.Connect(
            grab.Outputs.Single(p => p.IsExec && p.IsOutput).Kernel,
            thr.Inputs.Single(p => p.IsExec && p.IsInput).Kernel));
        Assert.NotNull(shell.Editor.Graph.Connect(
            grab.Outputs.Single(p => p.IsData && p.Name == "image").Kernel,
            thr.Inputs.Single(p => p.IsData && p.Name == "image").Kernel));
        shell.Editor.RebindConnections();

        Assert.True(shell.Editor.Graph.Validate().Count == 0, "graph must validate before run");

        shell.RunCommand.Execute(null);
        // Wait for a real completion signal, not for !IsRunning. RunCommand.Execute returns before
        // the session starts, so IsRunning is still false and "not running" is true immediately:
        // waiting on it alone returns at once and the assertions below then race the scheduler.
        // Observed under CPU load as Assert.Contains() with an empty preview-ring collection.
        // OnRunCompleted logs "Run finished: ..." for both success and fault, so it is monotonic.
        // · 等真实完成信号，而非 !IsRunning：Execute 在会话启动前就返回，IsRunning 仍为 false，
        // 「未运行」立刻为真，单等它会马上返回并与调度器竞争（高负载下表现为预览环断言时集合为空）。
        Assert.True(await RunFinishedAsync(shell), "run did not finish within 5s");

        var grabImage = grab.Outputs.Single(p => p.Name == "image");
        var thrRegion = thr.Outputs.Single(p => p.Name == "region");
        var grabCaptions = grab.Outputs.Where(p => p.IsData).Select(p => p.ValueText).ToList();

        Assert.Contains(grabCaptions, t => t.StartsWith("320", StringComparison.Ordinal)); // phantom grab 320×240
        Assert.StartsWith("320", grabImage.ValueText, StringComparison.Ordinal);
        Assert.StartsWith("320", thrRegion.ValueText, StringComparison.Ordinal);
        Assert.All(shell.Editor.Nodes.SelectMany(n => n.Outputs.Where(p => p.IsExec)), p => Assert.Equal("", p.ValueText));

        // Preview ring fed with encoded images + dashboard live-preview pushed.
        // The scheduler raises every node's Completed event before RunCompleted, but the shell
        // forwards events to the captured SynchronizationContext, so their delivery can trail
        // RunCompleted. Waiting only for "run finished" therefore asserted on a ring that had not
        // been fed yet — observed under CPU load as Assert.Contains() over an empty collection.
        // Waiting for each node's own completion entry is monotonic and delivery-accurate.
        // · 调度器在 RunCompleted 之前已发出全部节点的 Completed 事件，但壳层经捕获的
        // SynchronizationContext 转发，投递可能晚于 RunCompleted。只等「运行结束」就会在环尚未
        // 写入时断言（高负载下表现为对空集合的 Assert.Contains）。等各节点自身的完成条目既单调又
        // 与投递对齐。
        Assert.True(await NodesCompletedAsync(shell, start.Id, grab.Id, thr.Id), "node completion events were not delivered within 5s");

        Assert.Contains(grab.Id, shell.Preview.Nodes);
        Assert.Contains(thr.Id, shell.Preview.Nodes);
        var frame = shell.Preview.Latest();
        Assert.NotNull(frame);
        Assert.NotNull(frame.Image);
        Assert.True(frame.Image.Length > 50, $"preview png too small: {frame.Image.Length}");
        // Both grab and threshold publish frames this cycle; when the system clock's tick
        // granularity ties their CapturedAt the "latest" node is not guaranteed to be the last
        // in execution order. · 本轮 grab 与 threshold 都发布帧;系统时钟粒度不足使 CapturedAt
        // 并列时,「最新」节点不一定是执行顺序中的最后一个
        Assert.Contains(shell.Dashboard.PreviewNode, new[] { grab.Id, thr.Id });
        Assert.True(shell.Dashboard.HasPreview);
    }

    [Fact]
    public async Task Run_ExecPortsNeverGainCaptions_KernelPortKeepsLastCycleValue()
    {
        var shell = CreateShell();
        shell.AddNodeCommand.Execute(shell.Palette.First(p => p.Key == "start"));
        shell.AddNodeCommand.Execute(shell.Palette.First(p => p.Key == "grabber"));

        var start = shell.Editor.Nodes.First(n => n.Kernel.Contract.Namespace == "test.start");
        var grab = shell.Editor.Nodes.First(n => n.Kernel.Contract.Namespace == "vision.grab");
        Assert.NotNull(shell.Editor.Graph.Connect(
            start.Outputs.Single(p => p.IsExec && p.IsOutput).Kernel,
            grab.Inputs.Single(p => p.IsExec && p.IsInput).Kernel));
        shell.Editor.RebindConnections();

        shell.RunCommand.Execute(null);
        // Monotonic signals, in order — see the notes in the test above. The port caption is
        // written by the node's Completed event, so it also has to wait for delivery.
        // · 依次等待单调信号，同上：端口标注由节点 Completed 事件写入，故同样需等其送达。
        Assert.True(await RunFinishedAsync(shell), "run did not finish within 5s");
        Assert.True(await NodesCompletedAsync(shell, start.Id, grab.Id), "node completion events were not delivered within 5s");

        var caption = grab.Outputs.Single(p => p.Name == "image").ValueText;
        Assert.NotEqual("", caption);

        // After the run the kernel port still holds the last cycle's value. · 运行结束后内核端口仍保留上一轮值
        var kernelImage = grab.Outputs.Single(p => p.Name == "image").Kernel.Value;
        Assert.NotNull(kernelImage);
    }

    /// <summary>
    /// Monotonic wait for a finished run. ShellViewModel logs "Run finished: ..." from
    /// OnRunCompleted for both success and fault, and each test builds a fresh shell, so one
    /// such entry means exactly one completed run. · 等待一次已完成的运行：ShellViewModel 在
    /// OnRunCompleted 中对成功与故障都会写 "Run finished: ..."，且每个测试都新建 shell，
    /// 故出现一条即代表恰好完成了一次运行。
    /// </summary>
    private static Task<bool> RunFinishedAsync(ShellViewModel shell, int timeoutMs = 5000) =>
        WaitUntilAsync(
            () => shell.Log.Snapshot().Any(e => e.Message.StartsWith("Run finished:", StringComparison.Ordinal)),
            timeoutMs);

    /// <summary>
    /// Monotonic wait until every named node's Completed event has been delivered to the shell.
    /// OnNodeEvent logs "&lt;trigger&gt; &lt;node&gt; done &lt;ms&gt;" for each delivered event, so one
    /// such line per node means the event's effects (scope captions, preview publish) are visible.
    /// · 等待指定节点的全部 Completed 事件已送达壳层：OnNodeEvent 对每个送达的事件写
    /// "&lt;触发&gt; &lt;节点&gt; done &lt;ms&gt;"，故每个节点各一条即表示其效果（scope 标注、预览发布）已可见。
    /// </summary>
    private static Task<bool> NodesCompletedAsync(ShellViewModel shell, params string[] nodeIds) =>
        WaitUntilAsync(
            () =>
            {
                var messages = shell.Log.Snapshot().Select(e => e.Message).ToArray();
                // every node, not just one: an entry only counts for its own id
                // · 每个节点都要满足，而非任意一个：条目只对自己的 id 计数
                return nodeIds.All(id => messages.Any(m => m.Contains($" {id} done ", StringComparison.Ordinal)));
            },
            5000);

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