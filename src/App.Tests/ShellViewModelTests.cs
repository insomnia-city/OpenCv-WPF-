using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using HalconWorkflow.App.Services;
using HalconWorkflow.App.ViewModels;
using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Graph;
using HalconWorkflow.Core.Model;
using HalconWorkflow.Core.Serialization;
using HalconWorkflow.Nodes.Vision.Nodes;
using HalconWorkflow.Runtime.Nodes;
using Xunit;

namespace HalconWorkflow.App.Tests;

/// <summary>
/// Stage-3 acceptance: editors reflect graphs, bindings hit projections only, and localization hot-switches. 
/// 阶段3 验收：编辑器镜像图、绑定只指投影、本地化热切换
/// </summary>
public class ShellViewModelTests
{
    private static ShellViewModel CreateShell()
        => new(new LocalizationService(), new NoopDialogService());

    [Fact]
    public void Localization_HotSwitch_ChangesTitlesAndFallsBack()
    {
        var loc = new LocalizationService();
        var shell = new ShellViewModel(loc, new NoopDialogService());

        loc.Culture = new System.Globalization.CultureInfo("zh-Hans");
        Assert.Equal("运行", shell.MenuRun);
        Assert.Equal("日志", shell.LogTitle);

        loc.Culture = new System.Globalization.CultureInfo("en");
        Assert.Equal("Run", shell.MenuRun);
        Assert.Equal("Log", shell.LogTitle);

        loc.Culture = new System.Globalization.CultureInfo("ko");
        Assert.Equal("실행", shell.MenuRun);
        Assert.Equal("로그", shell.LogTitle);

        // Missing key falls back to English (ADR-010 fallback chain). · 缺键回退英文(ADR-010 回退链)
        Assert.Equal("some.missing.key", loc["some.missing.key"]);
    }

    [Fact]
    public void Editor_RoundTrip_SerializeReloadPreservesStructure()
    {
        var shell = CreateShell();
        var n1 = shell.Editor.AddNode(SampleNodes.Start("n1"), 10, 20);
        var n2 = shell.Editor.AddNode(SampleNodes.Grabber("n2"), 300, 20);
        var n3 = shell.Editor.AddNode(SampleNodes.Threshold("n3"), 300, 200);
        Assert.NotNull(n1);
        Assert.NotNull(n2);
        Assert.NotNull(n3);
        n1.Location = new Point(120, 80);

        var json = GraphJsonSerializer.Serialize(shell.Editor.Graph);
        var reloaded = GraphJsonSerializer.Deserialize(json, new SampleNodeFactory());

        Assert.Equal(3, reloaded.Nodes.Count);
        Assert.Equal((120d, 80d), reloaded.Nodes["n1"].Position);

        var editor2 = new MainEditorViewModel();
        editor2.Load(reloaded, "reloaded");
        Assert.Equal(3, editor2.Nodes.Count);
        Assert.Equal(new Point(120, 80), editor2.Nodes.First(n => n.Id == "n1").Location);
        Assert.Equal(new Point(300, 20), editor2.Nodes.First(n => n.Id == "n2").Location);
        Assert.Equal(new Point(300, 200), editor2.Nodes.First(n => n.Id == "n3").Location);
    }

    [Fact]
    public void NodeMove_ReanchorsConnections()
    {
        var editor = new MainEditorViewModel();
        var from = editor.AddNode(SampleNodes.Grabber("f"), 0, 0);
        var to = editor.AddNode(SampleNodes.Threshold("t"), 400, 0);
        Assert.NotNull(from);
        Assert.NotNull(to);

        var link = editor.Graph.Connect(
            from.Kernel.Node.Outputs.First(p => p.Kind == PortKind.Exec),
            to.Kernel.Node.Inputs.First(p => p.Kind == PortKind.Exec));
        Assert.NotNull(link);
        editor.RebindConnections();
        var conn = editor.Connections.Single();

        var before = conn.Target.X;
        to.Location = new Point(to.Location.X + 100, 40);
        Assert.NotEqual(before, conn.Target.X);
        Assert.Equal(to.Location.X, conn.Target.X);
    }

    [Fact]
    public void AddNode_And_DeleteNode_UpdateKernelAndProjections()
    {
        var shell = CreateShell();
        var vm = shell.Editor.AddNode(SampleNodes.Start("s"), 5, 5);
        Assert.NotNull(vm);
        Assert.Single(shell.Editor.Nodes);
        Assert.True(shell.Editor.Graph.Nodes.ContainsKey("s"));

        vm.DeleteCommand.Execute(null);
        Assert.Empty(shell.Editor.Nodes);
        Assert.False(shell.Editor.Graph.Nodes.ContainsKey("s"));
    }

    [Fact]
    public void Palette_VisionEntriesAreReadyToSpawn()
    {
        var shell = CreateShell();
        Assert.Equal(14, shell.Palette.Count);
        var grab = shell.Palette.First(p => p.Key == "grabber");
        Assert.Equal("vision.grab:1", grab.Contract);
        shell.AddNodeCommand.Execute(grab);
        Assert.Single(shell.Editor.Nodes);
        Assert.True(shell.Editor.Nodes[0].Id.StartsWith("grabber", StringComparison.Ordinal));
        Assert.True(shell.Editor.Graph.Nodes.Values.Any(g => g.Contract.Namespace == "vision.grab"));
    }

    [Fact]
    public void VisionThreshold_PaletteSpawnsV2Node()
    {
        var shell = CreateShell();
        var thr = shell.Palette.First(p => p.Key == "threshold");
        Assert.Equal("vision.threshold:2", thr.Contract);
        shell.AddNodeCommand.Execute(thr);
        var kernel = shell.Editor.Graph.Nodes.Values.Single();
        Assert.Equal(2, kernel.Contract.Version);
        Assert.True(kernel.Node is IParameterized);
    }

    /// <summary>
    /// Stage-5 acceptance: selecting a vision node reflects its parameters into the
    /// property panel and edits round-trip through the undo service.
    /// / 阶段5 验收：选中视觉节点将其参数反射进属性面板,编辑经撤销服务往返
    /// </summary>
    [Fact]
    public void PropertyPanel_EditsRoundTripThroughUndo()
    {
        var shell = CreateShell();
        var thr = shell.Palette.First(p => p.Key == "threshold");
        shell.AddNodeCommand.Execute(thr);
        var vm = shell.Editor.Nodes[0];

        shell.SelectNode(vm);
        Assert.True(shell.PropertyPanel.HasTarget);
        Assert.Contains(shell.PropertyPanel.Rows, r => r.Name == "Min");
        Assert.Contains(shell.PropertyPanel.Rows, r => r.Name == "Max");
        Assert.True(shell.PropertyPanel.Rows.Single(r => r.Name == "Min").HasRange);

        // Commit a typed edit through the row binding → undoable set-parameter command. · 经行绑定提交类型化编辑→可撤销参数命令
        var minRow = shell.PropertyPanel.Rows.Single(r => r.Name == "Min");
        minRow.ValueString = "200";
        var parameters = (ThresholdParameters)((IParameterized)vm.Kernel.Node).ParameterObject;
        Assert.Equal(200, parameters.Min);
        Assert.True(shell.CanUndo);

        Assert.True(shell.UndoCommand.CanExecute(null));
        shell.UndoCommand.Execute(null);
        Assert.Equal(128, parameters.Min);
        Assert.Equal("128", shell.PropertyPanel.Rows.Single(r => r.Name == "Min").ValueString);

        shell.RedoCommand.Execute(null);
        Assert.Equal(200, parameters.Min);
        Assert.Equal("200", shell.PropertyPanel.Rows.Single(r => r.Name == "Min").ValueString);
    }

    /// <summary>
    /// Stage-4 acceptance: palette flow nodes spawn and shell undo/redo drives the same kernel the editor edits. 
    /// 阶段4 验收：调色板流程节点可生成，且壳层撤销/重做与编辑器操作同一内核
    /// </summary>
    [Fact]
    public void FlowPalette_And_UndoRedo_DriveKernel()
    {
        var shell = CreateShell();
        Assert.True(shell.Palette.Any(p => p.Key == "branch"));
        Assert.True(shell.Palette.Any(p => p.Key == "script"));
        Assert.True(shell.Palette.Any(p => p.Key == "delay"));

        shell.AddNodeCommand.Execute(shell.Palette[5]);   // flow.branch → spawns via undo path
        shell.AddNodeCommand.Execute(shell.Palette[7]);   // flow.script
        Assert.Equal(2, shell.Editor.Nodes.Count);
        Assert.True(shell.CanUndo);
        Assert.False(shell.CanRedo);

        shell.UndoCommand.Execute(null);
        Assert.Single(shell.Editor.Nodes);
        Assert.False(shell.Editor.Graph.Nodes.Values.Any(n => n.Contract.Namespace == "flow.script"));
        Assert.True(shell.CanRedo);

        shell.RedoCommand.Execute(null);
        Assert.Equal(2, shell.Editor.Nodes.Count);
        Assert.True(shell.Editor.Graph.Nodes.Values.Any(n => n.Contract.Namespace == "flow.script"));
    }

    /// <summary>
    /// Binding surfaces must project only; kernel entities are allowed solely on the documented service accessors. 
    /// 绑定面只允许投影;内核实体仅允许在文档化的服务访问器上出现
    /// </summary>
    [Fact]
    public void BindingSurface_OnlyProjects_NoKernelEntities()
    {
        var assembly = typeof(NodeViewModel).Assembly;
        var banned = new[]
        {
            typeof(GraphNode), typeof(GraphLink), typeof(GraphModel),
            typeof(INode), typeof(IPort), typeof(ITypeDescriptor)
        };

        var vmTypes = assembly.GetTypes().Where(t => t.Namespace == "HalconWorkflow.App.ViewModels");
        foreach (var vm in vmTypes)
        {
            foreach (var prop in vm.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var type = ElementType(prop.PropertyType);
                if (banned.Any(b => b.IsAssignableFrom(type)))
                {
                    // Service accessors documented in kernel comments are allowed. · 服务访问器允许内核类型
                    var allowed = (prop.Name == "Kernel" && (vm == typeof(NodeViewModel) || vm == typeof(PortViewModel)))
                        || (prop.Name == "Graph" && vm == typeof(MainEditorViewModel))
                        || (prop.Name == "Link" && vm == typeof(ConnectionViewModel));
                    Assert.True(allowed, $"{vm.Name}.{prop.Name} leaks entity type {type.Name}");
                }
            }
        }
    }

    private static Type ElementType(Type t)
        => t.IsGenericType && t.GetGenericArguments().Length == 1 ? t.GetGenericArguments()[0] : t;

    private sealed class NoopDialogService : IDialogService
    {
        public string? OpenGraphFile(string filter) => null;
        public string? SaveGraphFile(string defaultName, string filter) => null;
        public void ReportError(string message) { }
    }
}