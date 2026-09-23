using System.Text.Json.Nodes;
using HalconWorkflow.Abstractions.Parameters;
using HalconWorkflow.App.Services;
using HalconWorkflow.App.ViewModels;
using HalconWorkflow.Core.Graph;
using HalconWorkflow.Core.Model;
using HalconWorkflow.Core.Serialization;
using HalconWorkflow.Nodes.Vision;
using HalconWorkflow.Nodes.Vision.Nodes;
using Xunit;

namespace HalconWorkflow.App.Tests;

/// <summary>
/// Stage-18 gate (§5.8): node parameters round-trip through the sidecar recipe file.
/// The binder snapshots/restores reflected params, capture prunes deleted nodes while
/// preserving suspended ones, and the shell writes <c>X.recipe.json</c> beside
/// <c>X.graph.json</c> so save→new→load restores live values. /
/// 阶段18 门(§5.8)：节点参数经伴生配方文件往返。绑定器快照/还原反射参数，捕获时清理已删节点
/// 且保留挂起节点，壳层在 X.graph.json 旁写 X.recipe.json，使 保存→新建→载入 还原实值。
/// </summary>
public sealed class RecipePersistenceTests : IDisposable
{
    private readonly List<ShellViewModel> _shells = [];

    public void Dispose()
    {
        foreach (var shell in _shells) shell.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
    }

    private sealed class DemoParameters
    {
        [NodeParameter("Gain", "Main", 0, 100)] public int Gain { get; set; } = 7;
        [NodeParameter("Label", "Main")] public string Label { get; set; } = "seed";
        [NodeParameter("Enabled", "Main")] public bool Enabled { get; set; }
        [NodeParameter("Ratio", "Main", 0, 1)] public double Ratio { get; set; } = 0.25;
        [NodeParameter("Mode", "Main")] public DemoMode Mode { get; set; } = DemoMode.Alpha;
    }

    private enum DemoMode { Alpha, Beta }

    private sealed class FileDialogService : IDialogService
    {
        public string? OpenGraphPath { get; set; }
        public string? SaveGraphPath { get; set; }
        public string? OpenGraphFile(string filter) => OpenGraphPath;
        public string? SaveGraphFile(string defaultName, string filter) => SaveGraphPath;
        public string? SaveCsvFile(string defaultName) => null;
        public bool Confirm(string message) => true;
        public void ReportError(string message) { }
        public void ShowImageWindow(ImageWindowViewModel vm) { }
    }

    [Fact]
    public void Snapshot_CapturesTypedValues()
    {
        var obj = RecipeBinder.Snapshot(new DemoParameters
        {
            Gain = 12,
            Label = "x",
            Enabled = true,
            Ratio = 0.75,
            Mode = DemoMode.Beta
        });

        Assert.Equal(12, obj["Gain"]!.GetValue<long>());
        Assert.Equal("x", obj["Label"]!.GetValue<string>());
        Assert.True(obj["Enabled"]!.GetValue<bool>());
        Assert.Equal(0.75, obj["Ratio"]!.GetValue<double>());
        Assert.Equal("Beta", obj["Mode"]!.GetValue<string>());
    }

    [Fact]
    public void Apply_WritesValidEntries_AndSkipsBadOnes()
    {
        var target = new DemoParameters();
        var values = new JsonObject
        {
            ["Gain"] = 9999, // out of declared range -> rejected, instance unchanged · 越界被拒
            ["Label"] = "from-recipe",
            ["Enabled"] = true,
            ["Ratio"] = 0.9,
            ["Mode"] = "Beta",
            ["Unknown"] = 5 // unknown name -> skipped · 未知名称跳过
        };

        Assert.True(RecipeBinder.Apply(target, values));
        Assert.Equal(7, target.Gain);
        Assert.Equal("from-recipe", target.Label);
        Assert.True(target.Enabled);
        Assert.Equal(0.9, target.Ratio);
        Assert.Equal(DemoMode.Beta, target.Mode);
    }

    [Fact]
    public void Capture_And_Restore_RoundTrip_AndPruneDeletedNodes()
    {
        var factory = new VisionNodeFactory();
        var graph = new GraphModel();
        var a = factory.Create(new NodeContract("vision.threshold", 2), "a")!;
        var b = factory.Create(new NodeContract("vision.threshold", 2), "b")!;
        Assert.True(graph.AddNode(a));
        Assert.True(graph.AddNode(b));
        ((ThresholdParameters)((IParameterized)a).ParameterObject).Min = 11;
        ((ThresholdParameters)((IParameterized)b).ParameterObject).Min = 22;

        var recipe = new Recipe();
        RecipeBinder.Capture(graph, recipe);
        Assert.Equal(2, recipe.Params.Count);
        Assert.Equal("a", graph.Nodes["a"].RecipeId);

        // Reset then restore from the recipe. · 清零后从配方还原
        ((ThresholdParameters)((IParameterized)a).ParameterObject).Min = 0;
        Assert.Equal(2, RecipeBinder.Restore(graph, recipe));
        Assert.Equal(11, ((ThresholdParameters)((IParameterized)a).ParameterObject).Min);

        // A deleted node's entry is pruned on the next capture. · 删除节点后其条目被清理
        Assert.True(graph.RemoveNode("b"));
        RecipeBinder.Capture(graph, recipe);
        Assert.Single(recipe.Params);
        Assert.True(recipe.Params.ContainsKey("a"));
    }

    [Fact]
    public async Task Save_New_Load_RoundTripsParameters_WithRecipeSidecar()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"hwf-recipe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var graphPath = Path.Combine(dir, "flow.graph.json");
        var recipePath = Path.Combine(dir, "flow.recipe.json");
        var dialogs = new FileDialogService { OpenGraphPath = graphPath, SaveGraphPath = graphPath };
        var shell = new ShellViewModel(new LocalizationService(), dialogs);
        _shells.Add(shell);
        try
        {
            await shell.AddNodeCommand.ExecuteAsync(shell.Palette.First(p => p.Key == "threshold"));
            var node = shell.Editor.Graph.Nodes.Values.Single(n => n.Contract.Namespace == "vision.threshold");
            var parameterized = (IParameterized)node.Node;
            ParameterReflection.Apply(parameterized.ParameterObject, "Min", 42L);
            ParameterReflection.Apply(parameterized.ParameterObject, "Max", 200L);

            shell.SaveCommand.Execute(null);
            Assert.True(File.Exists(graphPath));
            Assert.True(File.Exists(recipePath));

            shell.NewCommand.Execute(null);
            Assert.Empty(shell.Editor.Graph.Nodes);

            shell.LoadCommand.Execute(null);
            var reloaded = (IParameterized)shell.Editor.Graph.Nodes.Values
                .Single(n => n.Contract.Namespace == "vision.threshold").Node;
            var byName = ParameterReflection.Summarize(reloaded.ParameterObject).ToDictionary(m => m.Name);
            Assert.Equal(42L, Convert.ToInt64(byName["Min"].Value));
            Assert.Equal(200L, Convert.ToInt64(byName["Max"].Value));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
