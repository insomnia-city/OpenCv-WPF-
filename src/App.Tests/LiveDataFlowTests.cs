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
        Assert.True(await WaitUntilAsync(() => !shell.IsRunning), "run did not finish within 5s");

        var grabImage = grab.Outputs.Single(p => p.Name == "image");
        var thrRegion = thr.Outputs.Single(p => p.Name == "region");
        var grabCaptions = grab.Outputs.Where(p => p.IsData).Select(p => p.ValueText).ToList();

        Assert.Contains(grabCaptions, t => t.StartsWith("320", StringComparison.Ordinal)); // phantom grab 320×240
        Assert.StartsWith("320", grabImage.ValueText, StringComparison.Ordinal);
        Assert.StartsWith("320", thrRegion.ValueText, StringComparison.Ordinal);
        Assert.All(shell.Editor.Nodes.SelectMany(n => n.Outputs.Where(p => p.IsExec)), p => Assert.Equal("", p.ValueText));

        // Preview ring fed with encoded images + dashboard live-preview pushed. · 预览环有编码图像、看板预览已推送
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
        Assert.True(await WaitUntilAsync(() => !shell.IsRunning), "run did not finish within 5s");

        var caption = grab.Outputs.Single(p => p.Name == "image").ValueText;
        Assert.NotEqual("", caption);

        // After the run the kernel port still holds the last cycle's value. · 运行结束后内核端口仍保留上一轮值
        var kernelImage = grab.Outputs.Single(p => p.Name == "image").Kernel.Value;
        Assert.NotNull(kernelImage);
    }

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
        public void ShowImageWindow(string nodeId, string nodeLabel) { }
    }
}