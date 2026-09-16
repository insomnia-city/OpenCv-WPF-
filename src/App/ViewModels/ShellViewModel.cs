using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HalconWorkflow.App.Services;
using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Execution;
using HalconWorkflow.Core.Model;
using HalconWorkflow.Core.Serialization;
using HalconWorkflow.Runtime.Nodes;

namespace HalconWorkflow.App.ViewModels;

/// <summary>
/// Shell coordinator: node library, document commands and run/stop wiring. All bindings are projections, never kernel entities. 
/// 壳层协调器：节点库、文档命令与运行/停止接线。绑定只使用投影，绝不使用内核实体
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    internal const string GraphFilter = "Halcon Graph (*.graph.json)|*.graph.json|All files (*.*)|*.*";

    private readonly LocalizationService _loc;
    private readonly IDialogService _dialogs;
    private readonly SynchronizationContext? _ui;
    private readonly GraphScheduler _scheduler = new();
    private CancellationTokenSource? _runCts;
    private string? _currentPath;
    private bool _running;
    private int _seq;
    private int _paletteOffset;

    /// <summary>Localization facade. · 本地化门面</summary>
    public LocalizationService Loc => _loc;

    /// <summary>Editor surface. · 画布</summary>
    public MainEditorViewModel Editor { get; }

    /// <summary>Session log. · 会话日志</summary>
    public LogViewModel Log { get; }

    /// <summary>Node library palette. · 节点库</summary>
    public ObservableCollection<NodeCatalogItem> Palette { get; } = [];

    /// <summary>Available UI languages for the selector. · 可选 UI 语言</summary>
    public IReadOnlyList<CultureInfo> Languages => SupportedCultures.All;

    private CultureInfo _culture = new("zh-Hans");

    /// <summary>Selected UI culture; switching hot-updates all bindings. · 当前 UI 语言;切换即时刷新绑定</summary>
    public CultureInfo Culture
    {
        get => _culture;
        set
        {
            if (!SetProperty(ref _culture, value)) return;
            _loc.Culture = value;
        }
    }

    /// <summary>Status-bar text. · 状态栏文本</summary>
    [ObservableProperty]
    private string _status = "";

    // Localized strings refreshed on culture switch via the empty-property-Name broadcast below. · 本地化字符串(语言切换时全量刷新)
    public string AppTitle => _loc["app.title"];
    public string MenuNew => _loc["menu.new"];
    public string MenuLoad => _loc["menu.load"];
    public string MenuSave => _loc["menu.save"];
    public string MenuRun => _loc["menu.run"];
    public string MenuStop => _loc["menu.stop"];
    public string MenuLanguage => _loc["menu.language"];
    public string LogTitle => _loc["log.title"];
    public string PaletteTitle => _loc["palette.title"];
    public string StatusReady => _loc["status.ready"];

    /// <summary>Whether the graph is currently executing. · 正在执行标记</summary>
    public bool IsRunning => _running;

    public ShellViewModel(LocalizationService loc, IDialogService dialogs)
    {
        _loc = loc;
        _dialogs = dialogs;
        _ui = SynchronizationContext.Current;
        Editor = new MainEditorViewModel();
        Log = new LogViewModel();
        _loc.PropertyChanged += (_, _) => OnPropertyChanged((string?)null);
        Editor.New();
        BuildPalette();
        _scheduler.NodeExecuted += OnNodeEvent;
        _scheduler.RunCompleted += OnRunCompleted;
        Status = _loc["status.noGraph"];
    }

    private void BuildPalette()
    {
        Palette.Clear();
        Palette.Add(new NodeCatalogItem("start", _loc["palette.start"], "test.start:1", id => SampleNodes.Start(id)));
        Palette.Add(new NodeCatalogItem("grabber", _loc["palette.grabber"], "vision.grabber:1", id => SampleNodes.Grabber(id)));
        Palette.Add(new NodeCatalogItem("threshold", _loc["palette.threshold"], "vision.threshold:1", id => SampleNodes.Threshold(id)));
        Palette.Add(new NodeCatalogItem("decision", _loc["palette.decision"], "app.decision:1", id => SampleNodes.Decision(id)));
        Palette.Add(new NodeCatalogItem("result", _loc["palette.result"], "app.result:1", id => SampleNodes.LogResult(id)));
    }

    /// <summary>Adds a palette node at a cascading location. · 在级联坐标添加调色板节点</summary>
    [RelayCommand]
    private void AddNode(NodeCatalogItem item)
    {
        if (item is null || _running) return;
        if (Editor.Graph.Nodes.Count >= 500) { _dialogs.ReportError("Too many nodes."); return; }
        var id = $"{item.Key}{++_seq}";
        var x = 60 + (_paletteOffset % 5) * 40;
        var y = 60 + (_paletteOffset % 5) * 40;
        _paletteOffset++;
        if (Editor.AddNode(item.Factory(id), x, y) is not null)
        {
            Log.Add("info", $"Added node {id} ({item.Contract})");
            Status = _loc["status.ready"];
        }
    }

    /// <summary>New blank graph. · 新建空白图</summary>
    [RelayCommand]
    private void New()
    {
        if (!ConfirmDiscard()) return;
        _currentPath = null;
        Editor.New();
        Log.Add("info", "New graph");
        Status = _loc["status.ready"];
    }

    /// <summary>Loads a graph JSON. · 载入图 JSON</summary>
    [RelayCommand]
    private void Load()
    {
        if (_running) return;
        var path = _dialogs.OpenGraphFile(GraphFilter);
        if (path is null) return;
        try
        {
            var graph = GraphJsonSerializer.Deserialize(File.ReadAllText(path), new SampleNodeFactory());
            var issues = graph.Validate();
            foreach (var i in issues) Log.Add("warn", $"Validate: {i.Kind}: {i.Message}");
            Editor.Load(graph, Path.GetFileName(path));
            _currentPath = path;
            Log.Add("info", $"Loaded {graph.Nodes.Count} nodes / {graph.Links.Count} links from {path}");
            Status = _loc["status.ready"];
        }
        catch (Exception ex)
        {
            _dialogs.ReportError(ex.Message);
            Log.Add("error", $"Load failed: {ex.Message}");
        }
    }

    /// <summary>Saves the graph JSON (to path or via dialog). · 保存图 JSON</summary>
    [RelayCommand]
    private void Save()
    {
        if (_running) return;
        var path = _currentPath;
        if (path is null)
        {
            path = _dialogs.SaveGraphFile($"{Editor.Title}.graph.json", GraphFilter);
            if (path is null) return;
        }
        try
        {
            File.WriteAllText(path, GraphJsonSerializer.Serialize(Editor.Graph));
            _currentPath = path;
            Editor.Title = Path.GetFileName(path);
            Log.Add("info", $"Saved {Editor.Graph.Nodes.Count} nodes / {Editor.Graph.Links.Count} links to {path}");
        }
        catch (Exception ex)
        {
            _dialogs.ReportError(ex.Message);
            Log.Add("error", $"Save failed: {ex.Message}");
        }
    }

    /// <summary>Runs one full cycle and keeps the scheduler live for triggers. · 运行一整轮并使调度器保持活动</summary>
    [RelayCommand]
    private async Task RunAsync()
    {
        if (_running) return;
        var issues = Editor.Graph.Validate();
        if (issues.Count > 0)
        {
            Log.Add("error", $"Cannot run: {issues[0].Message}");
            Status = "Invalid graph";
            return;
        }
        _running = true;
        OnPropertyChanged(nameof(IsRunning));
        _runCts = new CancellationTokenSource();
        _scheduler.Load(Editor.Graph);
        Post(() =>
        {
            foreach (var n in Editor.Nodes) n.State = NodeState.Idle;
            Log.Add("info", "Run started");
        });
        await _scheduler.StartAsync(_runCts.Token);
        _scheduler.Trigger(TriggerSource.Manual);
        Status = _loc["status.ready"];
    }

    /// <summary>Stops the scheduler loop. · 停止调度器循环</summary>
    [RelayCommand]
    private async Task StopAsync()
    {
        if (!_running) return;
        await _scheduler.StopAsync();
        _runCts?.Cancel();
        _running = false;
        OnPropertyChanged(nameof(IsRunning));
        Post(() => Log.Add("warn", "Run stopped by user"));
        Status = _loc["status.ready"];
    }

    private void OnNodeEvent(NodeExecutionEvent evt)
    {
        Post(() =>
        {
            var nv = Editor.Nodes.FirstOrDefault(n => n.Id == evt.NodeId);
            if (nv is not null) nv.State = MapPhase(evt.Phase);
            var (level, tag) = evt.Phase switch
            {
                NodeExecutionPhase.Started => ("info", "start"),
                NodeExecutionPhase.Completed => ("info", "done"),
                NodeExecutionPhase.Faulted => ("error", "fail"),
                NodeExecutionPhase.Cancelled => ("warn", "cancelled"),
                _ => ("info", evt.Phase.ToString())
            };
            Log.Add(level, $"<{evt.Signal.TriggerId[..8]}> {evt.NodeId} {tag} {evt.ElapsedMs}ms{(evt.Error is null ? "" : $" {evt.Error}")}");
        });
    }

    private void OnRunCompleted(GraphRunResult result)
    {
        Post(() =>
        {
            _running = false;
            OnPropertyChanged(nameof(IsRunning));
            _runCts?.Dispose();
            Log.Add(result.Success ? "info" : "error",
                $"Run finished: success={result.Success} ok={result.SucceededNodes} failed={result.FaultedNodes} ({result.Duration.TotalMilliseconds:F0}ms)");
            Status = result.Success ? _loc["status.ready"] : "Faulted";
        });
    }

    private static NodeState MapPhase(NodeExecutionPhase p) => p switch
    {
        NodeExecutionPhase.Started => NodeState.Running,
        NodeExecutionPhase.Completed => NodeState.Succeeded,
        NodeExecutionPhase.Faulted => NodeState.Faulted,
        NodeExecutionPhase.Cancelled => NodeState.Cancelled,
        _ => NodeState.Idle
    };

    private bool ConfirmDiscard()
    {
        if (_currentPath is null && Editor.Nodes.Count == 0) return true;
        var r = System.Windows.MessageBox.Show(
            "Discard current graph?", "Halcon Workflow",
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        return r == System.Windows.MessageBoxResult.OK;
    }

    private void Post(Action action)
    {
        if (_ui is null) action();
        else _ui.Post(_ => action(), null);
    }
}