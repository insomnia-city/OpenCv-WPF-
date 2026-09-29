using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using HalconWorkflow.Abstractions;
using HalconWorkflow.Abstractions.Parameters;
using HalconWorkflow.App.Services;
using HalconWorkflow.App.ViewModels;
using HalconWorkflow.Core.Graph;
using HalconWorkflow.Core.Serialization;
using HalconWorkflow.Nodes.Vision.Nodes;
using Xunit;

namespace HalconWorkflow.App.Tests;

/// <summary>
/// Stage-11 gate (§13.1 row 11): the §9 application layer closes over the whole app — one global
/// <c>UndoService</c> covers every edit entry point, permission refusals are persisted to the audit
/// table, all three cultures carry a complete inventory through the fallback chain, and production
/// sources keep the bilingual comment convention (§4.9).
/// / 阶段 11 门(§13.1 第 11 行)：§9 应用层在全应用范围收口——唯一全局 UndoService 覆盖每个编辑入口，
///   权限拒绝落审计表，三语在回退链下词条完整，且生产源码保持双语注释规范(§4.9)。
/// </summary>
public sealed class Stage11GateTests : IDisposable
{
    private readonly List<ShellViewModel> _shells = [];

    private ShellViewModel CreateShell(IDialogService? dialogs = null)
    {
        var shell = TestShell.Create(new LocalizationService(), dialogs ?? new StubDialogService());
        _shells.Add(shell);
        return shell;
    }

    public void Dispose()
    {
        foreach (var shell in _shells) shell.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Undo_GlobalStack_CoversPalettePanelAndCanvasDelete()
    {
        var shell = CreateShell();

        // The canvas delete routes through the shell handler, not a local kernel edit. · 画布删除经壳层处理程序而非本地内核编辑
        Assert.NotNull(shell.Editor.RemoveAsyncHandler);

        shell.AddNodeCommand.Execute(shell.Palette.First(p => p.Key == "threshold"));
        var vm = Assert.Single(shell.Editor.Nodes);
        shell.SelectNode(vm);
        shell.PropertyPanel.Rows.Single(r => r.Name == "Min").ValueString = "200";

        shell.Editor.Nodes.Single().DeleteCommand.Execute(null);
        Assert.Empty(shell.Editor.Nodes);
        Assert.True(shell.CanUndo);
        Assert.True(shell.CanUndoToSavePoint);

        // One stack unwinds palette add + panel edit + canvas delete across subsystem boundaries. · 同一栈跨子系统回滚添加/改参/删除
        shell.UndoCommand.Execute(null);
        Assert.Single(shell.Editor.Nodes);

        shell.UndoCommand.Execute(null);
        var parameters = (ThresholdParameters)((IParameterized)shell.Editor.Graph.Nodes.Values.Single().Node).ParameterObject;
        Assert.Equal(128, parameters.Min);

        shell.UndoCommand.Execute(null);
        Assert.Empty(shell.Editor.Nodes);
        Assert.False(shell.CanUndo);
        Assert.True(shell.CanRedo);
    }

    [Fact]
    public void Undo_NewAndLoad_ResetTheGlobalStack()
    {
        var graphPath = Path.Combine(Path.GetTempPath(), $"stage11-{Guid.NewGuid():N}.graph.json");
        File.WriteAllText(graphPath, GraphJsonSerializer.Serialize(new GraphModel()));
        try
        {
            var shell = CreateShell(new StubDialogService { OpenGraphPath = graphPath });

            shell.AddNodeCommand.Execute(shell.Palette.First(p => p.Key == "threshold"));
            Assert.True(shell.CanUndo);

            shell.NewCommand.Execute(null); // stubbed confirm = proceed · 桩确认=继续
            Assert.Empty(shell.Editor.Nodes);
            Assert.False(shell.CanUndo);

            shell.AddNodeCommand.Execute(shell.Palette.First(p => p.Key == "threshold"));
            Assert.True(shell.CanUndo);

            shell.LoadCommand.Execute(null);
            Assert.False(shell.CanUndo);
            Assert.False(shell.CanUndoToSavePoint);
        }
        finally
        {
            File.Delete(graphPath);
        }
    }

    [Fact]
    public async Task Permission_DeniedAttempts_LandInTheAuditTable()
    {
        var shell = CreateShell();
        shell.SelectedRole = shell.RoleOptions.Single(r => r.Value == UserRole.ReadOnly);

        shell.AddNodeCommand.Execute(shell.Palette.First(p => p.Key == "threshold"));
        shell.RunCommand.Execute(null);

        Assert.Empty(shell.Editor.Nodes);   // refused before touching the kernel · 触碰内核前即被拒
        Assert.False(shell.IsRunning);

        var denied = await WaitForAuditAsync(shell, new AuditFilter(Action: AuditActions.AccessDenied),
            rows => rows.Any(r => r.Target == AuditActions.AddNode) && rows.Any(r => r.Target == AuditActions.Run));

        Assert.Contains(denied, r => r.Target == AuditActions.AddNode);
        Assert.Contains(denied, r => r.Target == AuditActions.Run);
        Assert.All(denied, r => Assert.Equal(shell.CurrentUser, r.User));
    }

    [Fact]
    public void Localization_ThreeCultures_HaveCompleteInventories()
    {
        var loc = new LocalizationService();
        var en = Table("En");
        var zh = Table("ZhHans");
        var ko = Table("Ko");

        var expected = en.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, zh.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal(expected, ko.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.All(en.Values, v => Assert.False(string.IsNullOrWhiteSpace(v)));
        Assert.All(zh.Values, v => Assert.False(string.IsNullOrWhiteSpace(v)));
        Assert.All(ko.Values, v => Assert.False(string.IsNullOrWhiteSpace(v)));
        Assert.True(en.Count >= 95, $"localization inventory shrank to {en.Count} keys");
    }

    [Fact]
    public void Localization_FallbackChain_ResolvesAndNeverBlank()
    {
        var loc = new LocalizationService();
        var keys = Table("En").Keys;

        foreach (var culture in new[] { "zh-Hans", "zh-CN", "en-US", "ko-KR" })
        {
            loc.Culture = new CultureInfo(culture);
            foreach (var key in keys)
            {
                var text = loc.Get(key);
                Assert.False(string.IsNullOrWhiteSpace(text));
                Assert.NotEqual(key, text); // a resolved key must not echo itself · 已解析的键不得回显自身
            }
        }

        loc.Culture = new CultureInfo("zh-Hans");
        Assert.Equal("运行", loc["menu.run"]);
        loc.Culture = new CultureInfo("zh-CN");           // two-letter alias still resolves Chinese · 双字母别名仍解析中文
        Assert.Equal("运行", loc["menu.run"]);
        loc.Culture = new CultureInfo("ko-KR");
        Assert.Equal("실행", loc["menu.run"]);
        loc.Culture = new CultureInfo("en-GB");
        Assert.Equal("Run", loc["menu.run"]);
        loc.Culture = new CultureInfo("fr-FR");           // unsupported culture falls back to English (§4.8) · 未知语言回退英文(§4.8)
        Assert.Equal("Run", loc["menu.run"]);
        Assert.Equal("unknown.key", loc["unknown.key"]);  // missing key echoes itself, never blank · 缺键回显自身，不空白
    }

    [Fact]
    public void BilingualComments_AllProductionSourcesContainChinese()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.Contains("/obj/") || rel.Contains("/bin/") || rel.Contains(".Tests/")
                || rel.EndsWith("AssemblyInfo.cs") || rel.EndsWith(".g.cs")) continue;

            var text = File.ReadAllText(file);
            if (!text.Contains("//") && !text.Contains("/*")) continue; // comment-free file: nothing to judge · 无注释文件不参与
            if (!Regex.IsMatch(text, "[\\u4e00-\\u9fff]")) offenders.Add(rel);
        }

        Assert.True(offenders.Count == 0, "bilingual comments missing Chinese in: " + string.Join(", ", offenders));
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

    private static IReadOnlyDictionary<string, string> Table(string fieldName)
        => (IReadOnlyDictionary<string, string>)typeof(LocalizationService)
            .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HalconWorkflow.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private sealed class StubDialogService : IDialogService
    {
        public string? OpenGraphPath { get; set; }

        public string? OpenGraphFile(string filter) => OpenGraphPath;

        public string? SaveGraphFile(string defaultName, string filter) => null;

        public string? SaveCsvFile(string defaultName) => null;

        public bool Confirm(string message) => true;

        public void ReportError(string message)
        {
        }

        public void ShowImageWindow(ImageWindowViewModel vm) { }
    }
}
