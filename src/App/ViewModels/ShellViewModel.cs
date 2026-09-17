using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HalconWorkflow.Abstractions;
using HalconWorkflow.Abstractions.Parameters;
using HalconWorkflow.Abstractions.Undo;
using HalconWorkflow.App.Services;
using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Execution;
using HalconWorkflow.Core.Model;
using HalconWorkflow.Core.Serialization;
using HalconWorkflow.Nodes.Comm;
using HalconWorkflow.Nodes.Data;
using HalconWorkflow.Nodes.Flow;
using HalconWorkflow.Nodes.Motion;
using HalconWorkflow.MotionDrivers;
using HalconWorkflow.Nodes.Vision;
using HalconWorkflow.Nodes.Vision.Commands;
using HalconWorkflow.Nodes.Vision.Engines;
using HalconWorkflow.Nodes.Vision.Nodes;
using HalconWorkflow.Protocols;
using HalconWorkflow.Protocols.Modbus;
using HalconWorkflow.Runtime.Nodes;
using HalconWorkflow.Storage;

namespace HalconWorkflow.App.ViewModels;

/// <summary>A selectable application role with its localized label (§9.3). · 可选应用角色(含本地化标签)(§9.3)</summary>
public sealed record RoleOption(UserRole Value, string Name);

/// <summary>
/// Shell coordinator: node library, document commands and run/stop wiring. All bindings are projections, never kernel entities. 
/// 壳层协调器：节点库、文档命令与运行/停止接线。绑定只使用投影，绝不使用内核实体
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IAsyncDisposable
{
    internal const string GraphFilter = "Halcon Graph (*.graph.json)|*.graph.json|All files (*.*)|*.*";

    private readonly LocalizationService _loc;
    private readonly IDialogService _dialogs;
    private readonly SynchronizationContext? _ui;
    private readonly GraphScheduler _scheduler = new();
    private readonly UndoService _undo = new();
    private readonly VisionEnginePool _visionPool = new(
        () => VisionEngineFactory.CreateResolved(forcePhantom: true), capacity: 2);
    private readonly CommRuntime _comm;
    private readonly ModbusTcpSimulator _commSim;
    private readonly MotionRuntime _motion;
    private readonly DataRuntime _data;
    private readonly StatsService _stats;
    private readonly ImageArchiveStore _images;
    private readonly PreviewRing _preview;
    private readonly SqlAuditService _audit;
    private readonly RoleService _roles = new();
    private readonly Dictionary<IUndoableCommand, long> _auditIds = new(ReferenceEqualityComparer.Instance);
    private CancellationTokenSource? _runCts;
    private string? _currentPath;
    private bool _running;
    private int _seq;
    private int _paletteOffset;
    private NodeViewModel? _selectedNode;

    /// <summary>Localization facade. · 本地化门面</summary>
    public LocalizationService Loc => _loc;

    /// <summary>Editor surface. · 画布</summary>
    public MainEditorViewModel Editor { get; }

    /// <summary>Session log. · 会话日志</summary>
    public LogViewModel Log { get; }

    /// <summary>Property panel for the selected node (§4.4). · 选中节点的属性面板(§4.4)</summary>
    public PropertyPanelViewModel PropertyPanel { get; }

    /// <summary>Shared comm runtime with the demo loopback device registered (§7). · 共享通讯运行时(已注册演示回环设备)</summary>
    public ICommRuntime Comm => _comm;

    /// <summary>Shared motion runtime with a demo controller registered (§7). · 共享运动运行时(已注册演示控制器)</summary>
    public IMotionRuntime Motion => _motion;

    /// <summary>Shared data runtime with a SQLite trace source registered (§8). · 共享数据运行时(已注册 SQLite 追溯数据源)</summary>
    public IDataRuntime Data => _data;

    /// <summary>Yield / throughput stats over the trace source (§9.5.4). · 追溯数据源上的良率/产量统计(§9.5.4)</summary>
    public IStatsService Stats => _stats;

    /// <summary>File-backed image archive for original / rendered snapshots (§9.5.3). · 原图/渲染图文件归档(§9.5.3)</summary>
    public IImageArchive Images => _images;

    /// <summary>Bounded recent-frames ring for the result preview (§9.5.1). · 结果预览的最近帧有界环(§9.5.1)</summary>
    public PreviewRing Preview => _preview;

    /// <summary>Append-only operation audit store, separate from the cycle trace (§9.1). · 独立于周期追溯的追加式操作审计存储(§9.1)</summary>
    public IAuditStore AuditStore => _audit;

    /// <summary>Trace board + yield dashboard + result preview (§9.4/§9.5.1/§9.5.4). · 追溯看板 + 良率看板 + 结果预览</summary>
    public DashboardViewModel Dashboard { get; }

    /// <summary>Operation-audit view (§9.1). · 操作审计视图(§9.1)</summary>
    public AuditViewModel AuditView { get; }

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
    public string MenuUndo => _loc["menu.undo"];
    public string MenuRedo => _loc["menu.redo"];
    public string MenuLanguage => _loc["menu.language"];
    public string LogTitle => _loc["log.title"];
    public string PaletteTitle => _loc["palette.title"];
    public string StatusReady => _loc["status.ready"];
    public string PropertiesTitle => _loc["property.title"];
    public string TabEditor => _loc["tab.editor"];
    public string TabDashboard => _loc["tab.dashboard"];
    public string TabAudit => _loc["tab.audit"];
    public string MenuUndoToSave => _loc["menu.undoToSave"];
    public string RoleLabel => _loc["role.label"];

    /// <summary>Operating-system user stamped onto every audit entry (§9.1). · 写入每条审计的操作系统用户(§9.1)</summary>
    public string CurrentUser { get; } = Environment.UserName;

    /// <summary>Role gate used by command guards and UI enablement (§9.3). · 命令守卫与界面启用使用的权限门(§9.3)</summary>
    public IRoleService Roles => _roles;

    /// <summary>Selectable roles for the toolbar. · 工具栏可选角色</summary>
    public IReadOnlyList<RoleOption> RoleOptions { get; private set; }

    /// <summary>True when the current role may edit the graph. · 当前角色可编辑图</summary>
    public bool CanEdit => _roles.IsAllowed(AuditActions.AddNode);

    /// <summary>True when the current role may run / stop the graph. · 当前角色可运行/停止图</summary>
    public bool CanOperate => _roles.IsAllowed(AuditActions.Run);

    /// <summary>True when the graph is currently executing. · 正在执行标记</summary>
    public bool IsRunning => _running;

    /// <summary>Undo availability for the toolbar. · 撤销可用状态</summary>
    public bool CanUndo => !_running && _undo.CanUndoCount > 0;

    /// <summary>Redo availability for the toolbar. · 重做可用状态</summary>
    public bool CanRedo => !_running && _undo.CanRedoCount > 0;

    /// <summary>Undo-to-save-point availability (§9.2). · 撤回到保存点可用状态(§9.2)</summary>
    public bool CanUndoToSavePoint => !_running && _undo.CanUndoToSavePoint;

    private RoleOption _selectedRole = null!;

    /// <summary>Selected role; switching it audits and re-evaluates UI gates (§9.3). · 当前角色;切换时落审计并重算界面门控(§9.3)</summary>
    public RoleOption SelectedRole
    {
        get => _selectedRole;
        set
        {
            if (value is null || !SetProperty(ref _selectedRole, value)) return;
            _roles.SetRole(value.Value);
            RecordAudit(AuditActions.SetRole, value.Value.ToString());
            OnRoleChanged();
        }
    }

    public ShellViewModel(LocalizationService loc, IDialogService dialogs)
    {
        _loc = loc;
        _dialogs = dialogs;
        _ui = SynchronizationContext.Current;
        Editor = new MainEditorViewModel();
        Log = new LogViewModel();
        PropertyPanel = new PropertyPanelViewModel(loc);
        PropertyPanel.ParameterCommitted += (name, value) =>
            _ = ApplyParameterAsync(_selectedNode, name, value);
        Editor.RemoveAsyncHandler = RemoveNodeAsync;
        _loc.PropertyChanged += (_, _) => OnCultureChanged();
        Editor.New();
        BuildPalette();
        _scheduler.NodeExecuted += OnNodeEvent;
        _scheduler.RunCompleted += OnRunCompleted;
        _scheduler.Services[typeof(IVisionEnginePool)] = _visionPool;
        (_comm, _commSim) = BuildCommRuntime();
        _scheduler.Services[typeof(ICommRuntime)] = _comm;
        _motion = BuildMotionRuntime();
        _scheduler.Services[typeof(IMotionRuntime)] = _motion;
        (_data, _stats, _images, _preview, _audit) = BuildDataRuntime();
        _scheduler.Services[typeof(IDataRuntime)] = _data;
        _scheduler.Services[typeof(IStatsService)] = _stats;
        _scheduler.Services[typeof(IImageArchive)] = _images;
        _scheduler.Services[typeof(PreviewRing)] = _preview;
        _scheduler.Services[typeof(IAuditStore)] = _audit;
        Dashboard = new DashboardViewModel(_loc, _stats, _preview, LoadTraceRowsAsync);
        Dashboard.ExportRequested += OnDashboardExport;
        AuditView = new AuditViewModel(_loc, _audit);
        AuditView.ExportRequested += OnAuditExport;
        RoleOptions = BuildRoleOptions();
        _selectedRole = RoleOptions.First(o => o.Value == _roles.Current);
        _undo.MarkSaved();
        Status = _loc["status.noGraph"];
    }

    /// <summary>Builds the localized role list for the toolbar (§9.3). · 构建工具栏本地化角色列表(§9.3)</summary>
    private IReadOnlyList<RoleOption> BuildRoleOptions() =>
    [
        new(UserRole.ReadOnly, _loc["role.readonly"]),
        new(UserRole.Operator, _loc["role.operator"]),
        new(UserRole.Engineer, _loc["role.engineer"]),
        new(UserRole.Admin, _loc["role.admin"])
    ];

    /// <summary>Re-evaluates role-dependent gates and refreshes undo/redo availability. · 重算角色相关门控并刷新撤销/重做可用性</summary>
    private void OnRoleChanged()
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanOperate));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(CanUndoToSavePoint));
    }

    /// <summary>Rebuilds localized strings after a language switch (§4.8). · 语言切换后重建本地化字符串(§4.8)</summary>
    private void OnCultureChanged()
    {
        var current = _roles.Current;
        RoleOptions = BuildRoleOptions();
        _selectedRole = RoleOptions.First(o => o.Value == current);
        OnPropertyChanged(nameof(RoleOptions));
        OnPropertyChanged(nameof(SelectedRole));
        OnPropertyChanged((string?)null);
    }

    /// <summary>
    /// Permission guard (§9.3): when the current role may not perform the action, the attempt is
    /// audited as denied and refused. · 权限守卫(§9.3)：当前角色不可执行该动作时，记录拒绝审计并拒绝执行。
    /// </summary>
    private bool EnsureAllowed(string action)
    {
        if (_roles.IsAllowed(action)) return true;
        RecordAudit(AuditActions.AccessDenied, action);
        Post(() =>
        {
            Log.Add("warn", $"Denied: {action} (role {_roles.Current})");
            Status = _loc["status.denied"];
        });
        return false;
    }

    /// <summary>
    /// Builds a demo comm runtime hosting a loopback Modbus device (§7, stage-6).
    /// The graph stays adapter-agnostic: swapping this for a real PLC only changes here.
    /// / 构建演示通讯运行时，承载回环 Modbus 设备(§7,阶段6)。图保持适配器无关：
    ///   换成真实 PLC 只需改动此处。
    /// </summary>
    private static (CommRuntime Runtime, ModbusTcpSimulator Sim) BuildCommRuntime()
    {
        var tags = new TagTable();
        tags.Register(new TagTableEntry("demo/holding/speed", "demo", "holding:0", typeof(ushort), true, true));
        tags.Register(new TagTableEntry("demo/holding/count", "demo", "holding:1", typeof(ushort), true, true));
        tags.Register(new TagTableEntry("demo/coil/run", "demo", "coil:0", typeof(bool), true, true));
        tags.Register(new TagTableEntry("demo/input/temp", "demo", "input:0", typeof(ushort), true, false));
        tags.Register(new TagTableEntry("demo/discrete/ready", "demo", "discrete:0", typeof(bool), true, false));
        var sim = ModbusTcpSimulator.Start();
        var runtime = new CommRuntime(tags);
        runtime.Add(new ModbusTcpConnection("demo", "127.0.0.1", sim.Port, 1, tags));
        return (runtime, sim);
    }

    /// <summary>
    /// Builds a demo motion runtime. The Googol driver is probed first; when the native SDK
    /// is absent the deterministic phantom controller is engaged (§6.3). Registered under
    /// both "demo" and "{vendor}:{card}" so graphs may reference either.
    /// / 构建演示运动运行时。先探测 Googol 驱动；原生 SDK 缺失时启用确定性幻影控制器(§6.3)。
    ///   同时以 "demo" 与 "{厂商}:{卡号}" 登记，图可引用任一。
    /// </summary>
    private static MotionRuntime BuildMotionRuntime()
    {
        var runtime = new MotionRuntime();
        var controller = MotionDriverFactory.Create("googol", 0);
        runtime.Add("demo", controller);
        runtime.Add(controller);
        return runtime;
    }

    /// <summary>
    /// Builds the demo data runtime: one SQLite trace source named "trace" holding the
    /// hot-path record store, the query store and the CSV exporter (§8, stage-8), plus the
    /// stage-9 stats service, file-backed image archive and preview ring sharing that source.
    /// Graphs reference "trace" by name and stay provider-agnostic.
    /// / 构建演示数据运行时：名为 "trace" 的 SQLite 追溯数据源，承载热路径记录存储、查询存储与
    ///   CSV 导出器(§8,阶段8)，外加共享该数据源的阶段9 统计服务、文件存图归档与预览环。
    ///   图按名引用 "trace"，保持提供商无关。
    /// </summary>
    private static (DataRuntime Runtime, StatsService Stats, ImageArchiveStore Images, PreviewRing Preview, SqlAuditService Audit) BuildDataRuntime()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HalconWorkflow");
        Directory.CreateDirectory(dir);
        var config = new DbConfig("trace", DbProviderKind.Sqlite,
            $"Data Source={Path.Combine(dir, "trace.db")};Pooling=False");
        var storage = new SqlStorage(config);
        storage.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        var runtime = new DataRuntime();
        var factory = new DbConnectionFactory(config);
        runtime.Add("trace", records: storage, query: storage, export: new CsvExporter(factory, DbProviderKind.Sqlite));
        var stats = new StatsService(factory, DbProviderKind.Sqlite);
        var images = new ImageArchiveStore(factory, DbProviderKind.Sqlite, Path.Combine(dir, "trace_images"));
        var preview = new PreviewRing();
        var audit = new SqlAuditService(factory, DbProviderKind.Sqlite);
        return (runtime, stats, images, preview, audit);
    }

    /// <summary>
    /// Disposes the shared comm runtime and the loopback simulator on app exit.
    /// / 退出时释放共享通讯运行时与回环模拟器。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _comm.DisposeAsync().ConfigureAwait(false);
        await _commSim.DisposeAsync().ConfigureAwait(false);
        await _motion.DisposeAsync().ConfigureAwait(false);
        await _data.DisposeAsync().ConfigureAwait(false);
        await _visionPool.DisposeAsync().ConfigureAwait(false);
    }

    private void BuildPalette()
    {
        var vision = new VisionNodeFactory();
        var comm = new CommNodeFactory();
        var motion = new MotionNodeFactory();
        var data = new DataNodeFactory();
        Palette.Clear();
        Palette.Add(new NodeCatalogItem("start", _loc["palette.start"], "test.start:1", id => SampleNodes.Start(id)));
        Palette.Add(new NodeCatalogItem("grabber", _loc["palette.grabber"], "vision.grab:1", id => vision.Create(new NodeContract("vision.grab", 1), id)!));
        Palette.Add(new NodeCatalogItem("threshold", _loc["palette.threshold"], "vision.threshold:2", id => vision.Create(new NodeContract("vision.threshold", 2), id)!));
        Palette.Add(new NodeCatalogItem("decision", _loc["palette.decision"], "app.decision:1", id => SampleNodes.Decision(id)));
        Palette.Add(new NodeCatalogItem("result", _loc["palette.result"], "app.result:1", id => SampleNodes.LogResult(id)));
        Palette.Add(new NodeCatalogItem("branch", _loc["palette.branch"], "flow.branch:1", id => FlowNodes.Branch(id)));
        Palette.Add(new NodeCatalogItem("join", _loc["palette.join"], "flow.join:1", id => FlowNodes.Join(id)));
        Palette.Add(new NodeCatalogItem("script", _loc["palette.script"], "flow.script:1", id => ScriptNodeFactory.Create(id, ScriptNodeFactory.DefaultScript, ScriptNode.OutKind.Result)));
        Palette.Add(new NodeCatalogItem("counter", _loc["palette.counter"], "flow.counter:1", id => FlowNodes.Counter(id)));
        Palette.Add(new NodeCatalogItem("delay", _loc["palette.delay"], "flow.delay:1", id => FlowNodes.Delay(id, 20)));
        Palette.Add(new NodeCatalogItem("measure", _loc["palette.measure"], "vision.measure:1", id => vision.Create(new NodeContract("vision.measure", 1), id)!));
        Palette.Add(new NodeCatalogItem("hdev", _loc["palette.hdev"], "vision.hdev:1", id => vision.Create(new NodeContract("vision.hdev", 1), id)!));
        Palette.Add(new NodeCatalogItem("tomat", _loc["palette.tomat"], "vision.tomat:1", id => vision.Create(new NodeContract("vision.tomat", 1), id)!));
        Palette.Add(new NodeCatalogItem("tohobject", _loc["palette.tohobject"], "vision.tohobject:1", id => vision.Create(new NodeContract("vision.tohobject", 1), id)!));
        Palette.Add(new NodeCatalogItem("read", _loc["palette.read"], "comm.read:1", id => comm.Create(new NodeContract("comm.read", 1), id)!));
        Palette.Add(new NodeCatalogItem("write", _loc["palette.write"], "comm.write:1", id => comm.Create(new NodeContract("comm.write", 1), id)!));
        Palette.Add(new NodeCatalogItem("wait", _loc["palette.wait"], "comm.wait:1", id => comm.Create(new NodeContract("comm.wait", 1), id)!));
        Palette.Add(new NodeCatalogItem("home", _loc["palette.home"], "motion.home:1", id => motion.Create(new NodeContract("motion.home", 1), id)!));
        Palette.Add(new NodeCatalogItem("moveAbs", _loc["palette.moveAbs"], "motion.moveAbs:1", id => motion.Create(new NodeContract("motion.moveAbs", 1), id)!));
        Palette.Add(new NodeCatalogItem("moveRel", _loc["palette.moveRel"], "motion.moveRel:1", id => motion.Create(new NodeContract("motion.moveRel", 1), id)!));
        Palette.Add(new NodeCatalogItem("line", _loc["palette.line"], "motion.line:1", id => motion.Create(new NodeContract("motion.line", 1), id)!));
        Palette.Add(new NodeCatalogItem("waitInPos", _loc["palette.waitInPos"], "motion.waitInPos:1", id => motion.Create(new NodeContract("motion.waitInPos", 1), id)!));
        Palette.Add(new NodeCatalogItem("dout", _loc["palette.dout"], "motion.dout:1", id => motion.Create(new NodeContract("motion.dout", 1), id)!));
        Palette.Add(new NodeCatalogItem("dataWrite", _loc["palette.dataWrite"], "data.write:1", id => data.Create(new NodeContract("data.write", 1), id)!));
        Palette.Add(new NodeCatalogItem("dataQuery", _loc["palette.dataQuery"], "data.query:1", id => data.Create(new NodeContract("data.query", 1), id)!));
    }

    /// <summary>Adds a palette node as an undoable graph edit at a cascading location. · 以可撤销图编辑在级联坐标添加调色板节点</summary>
    [RelayCommand]
    private async Task AddNodeAsync(NodeCatalogItem item)
    {
        if (item is null || _running) return;
        if (!EnsureAllowed(AuditActions.AddNode)) return;
        if (Editor.Graph.Nodes.Count >= 500) { _dialogs.ReportError("Too many nodes."); return; }
        var id = $"{item.Key}{++_seq}";
        var x = 60 + (_paletteOffset % 5) * 40;
        var y = 60 + (_paletteOffset % 5) * 40;
        _paletteOffset++;
        var command = GraphCommands.AddNode(Editor.Graph, item.Factory(id), x, y);
        try
        {
            await _undo.PushAndRunAsync(command, CancellationToken.None);
            Editor.RebindAll();
            RefreshPanelAfterStructuralChange();
            Log.Add("info", $"Added node {id} ({item.Contract})");
            _auditIds[command] = await RecordAuditAsync(AuditActions.AddNode, id, after: item.Contract);
        }
        catch (Exception ex)
        {
            _dialogs.ReportError(ex.Message);
            Log.Add("error", $"Add failed: {ex.Message}");
        }
        RaiseUndoState();
    }

    /// <summary>Undoable node removal (wired into each NodeViewModel's DeleteCommand). · 可撤销的节点删除(接入各节点删除命令)</summary>
    private async Task RemoveNodeAsync(NodeViewModel vm)
    {
        if (vm is null || _running) return;
        if (!EnsureAllowed(AuditActions.RemoveNode)) return;
        var command = GraphCommands.RemoveNode(Editor.Graph, vm.Id);
        try
        {
            await _undo.PushAndRunAsync(command, CancellationToken.None);
            Editor.RebindAll();
            RefreshPanelAfterStructuralChange();
            Log.Add("info", $"Removed node {vm.Id}");
            _auditIds[command] = await RecordAuditAsync(AuditActions.RemoveNode, vm.Id);
        }
        catch (Exception ex)
        {
            _dialogs.ReportError(ex.Message);
            Log.Add("error", $"Remove failed: {ex.Message}");
        }
        RaiseUndoState();
    }

    /// <summary>Prepares the canvas for keyboard/product gestures. · 撤销一步编辑</summary>
    [RelayCommand]
    private async Task UndoAsync()
    {
        if (_running) return;
        if (!EnsureAllowed(AuditActions.Undo)) return;
        var command = _undo.PeekUndo();
        var structural = command is IGraphEditCommand;
        if (await _undo.UndoAsync(CancellationToken.None))
        {
            if (structural) { Editor.RebindAll(); RefreshPanelAfterStructuralChange(); }
            else RefreshPanel();
            await RecordAuditAsync(AuditActions.Undo, structural ? "structural" : "parameter",
                undoRecordId: LinkOf(command));
        }
        RaiseUndoState();
    }

    /// <summary>Re-applies the last undone edit. · 重做最近撤销的编辑</summary>
    [RelayCommand]
    private async Task RedoAsync()
    {
        if (_running) return;
        if (!EnsureAllowed(AuditActions.Redo)) return;
        var command = _undo.PeekRedo();
        var structural = command is IGraphEditCommand;
        if (await _undo.RedoAsync(CancellationToken.None))
        {
            if (structural) { Editor.RebindAll(); RefreshPanelAfterStructuralChange(); }
            else RefreshPanel();
            await RecordAuditAsync(AuditActions.Redo, structural ? "structural" : "parameter",
                undoRecordId: LinkOf(command));
        }
        RaiseUndoState();
    }

    /// <summary>Rolls the graph back to the last save point (§9.2). · 将图回滚至最近保存点(§9.2)</summary>
    [RelayCommand]
    private async Task UndoToSaveAsync()
    {
        if (_running) return;
        if (!EnsureAllowed(AuditActions.Undo)) return;
        var steps = await _undo.UndoToSavePointAsync(CancellationToken.None);
        if (steps > 0)
        {
            Editor.RebindAll();
            RefreshPanelAfterStructuralChange();
            await RecordAuditAsync(AuditActions.Undo, "save-point", after: steps.ToString(CultureInfo.InvariantCulture));
        }
        RaiseUndoState();
    }

    /// <summary>Raises the toolbar undo/redo/save-point availability after a stack change. · 栈变化后刷新工具栏撤销/重做/保存点可用性</summary>
    private void RaiseUndoState()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(CanUndoToSavePoint));
    }

    private long? LinkOf(IUndoableCommand? command)
        => command is not null && _auditIds.TryGetValue(command, out var id) ? id : null;

    /// <summary>Refreshes the panel view after an edit that was not structural (parameter undo). · 非结构变更(参数撤销)后刷新面板显示</summary>
    private void RefreshPanel()
    {
        if (_selectedNode is not null) PropertyPanel.Show(SelectedKernel(_selectedNode)!, _selectedNode.Id);
    }

    /// <summary>Rebinds the selected projection after undo/redo rebuilt the canvas. · 撤销/重做重建画布后重建选中投影</summary>
    private void RefreshPanelAfterStructuralChange()
    {
        _selectedNode = null;
        PropertyPanel.Clear();
    }

    /// <summary>Marks a node as the property-panel target (view wiring from the editor). · 将节点标记为属性面板目标(视图接线传入)</summary>
    public void SelectNode(NodeViewModel vm)
    {
        if (vm is null) return;
        if (_selectedNode is not null && !ReferenceEquals(_selectedNode, vm)) _selectedNode.IsSelected = false;
        _selectedNode = vm;
        vm.IsSelected = true;
        PropertyPanel.Show(SelectedKernel(vm)!, $"{vm.Id} · {vm.ContractDisplay}");
    }

    /// <summary>Clears the panel when the selected container is deselected. · 选中容器取消选中时清空面板</summary>
    public void DeselectNode(NodeViewModel vm)
    {
        if (vm is null) return;
        if (ReferenceEquals(_selectedNode, vm))
        {
            _selectedNode = null;
            vm.IsSelected = false;
            PropertyPanel.Clear();
        }
    }

    /// <summary>Kernel node behind a projection (documented service accessor). · 投影背后的内核节点(文档化服务访问器)</summary>
    private static INode? SelectedKernel(NodeViewModel vm) => vm.Kernel.Node;

    /// <summary>Applies a panel commit as an undoable SetParameterCommand. · 将面板提交以可撤销 SetParameterCommand 应用</summary>
    private async Task ApplyParameterAsync(NodeViewModel? vm, string name, object value)
    {
        if (vm is null || _running) return;
        if (!EnsureAllowed(AuditActions.SetParameter)) return;
        var node = SelectedKernel(vm);
        if (node is not IParameterized ip)
        {
            _dialogs.ReportError($"{vm.Id} has no editable parameters");
            return;
        }
        var command = new SetParameterCommand(ip, name, value);
        try
        {
            await _undo.PushAndRunAsync(command, CancellationToken.None);
            RefreshPanel();
            Log.Add("info", $"param {vm.Id}.{name} = {value}");
            _auditIds[command] = await RecordAuditAsync(AuditActions.SetParameter, $"{vm.Id}.{name}",
                before: command.BeforeValue?.ToString(), after: command.AfterValue?.ToString());
        }
        catch (Exception ex)
        {
            _dialogs.ReportError(ex.Message);
            RefreshPanel();
        }
        RaiseUndoState();
    }

    /// <summary>New blank graph. · 新建空白图</summary>
    [RelayCommand]
    private void New()
    {
        if (!EnsureAllowed(AuditActions.NewGraph)) return;
        if (!ConfirmDiscard()) return;
        _currentPath = null;
        _undo.Clear();
        _auditIds.Clear();
        Editor.New();
        _selectedNode = null;
        PropertyPanel.Clear();
        RaiseUndoState();
        Log.Add("info", "New graph");
        RecordAudit(AuditActions.NewGraph);
        Status = _loc["status.ready"];
    }

    /// <summary>Loads a graph JSON. · 载入图 JSON</summary>
    [RelayCommand]
    private void Load()
    {
        if (_running) return;
        if (!EnsureAllowed(AuditActions.LoadGraph)) return;
        var path = _dialogs.OpenGraphFile(GraphFilter);
        if (path is null) return;
        try
        {
            var graph = GraphJsonSerializer.Deserialize(
                File.ReadAllText(path),
                new CombinedNodeFactory(new VisionNodeFactory(), new SampleNodeFactory(), new Nodes.Flow.FlowNodeFactory(), new CommNodeFactory(), new MotionNodeFactory(), new DataNodeFactory()));
            var issues = graph.Validate();
            foreach (var i in issues) Log.Add("warn", $"Validate: {i.Kind}: {i.Message}");
            _undo.Clear();
            _auditIds.Clear();
            _selectedNode = null;
            PropertyPanel.Clear();
            Editor.Load(graph, Path.GetFileName(path));
            RaiseUndoState();
            _currentPath = path;
            Log.Add("info", $"Loaded {graph.Nodes.Count} nodes / {graph.Links.Count} links from {path}");
            RecordAudit(AuditActions.LoadGraph, path);
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
        if (!EnsureAllowed(AuditActions.SaveGraph)) return;
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
            _undo.MarkSaved(); // edits above here are "unsaved" · 此点之上视为未保存
            RaiseUndoState();
            Log.Add("info", $"Saved {Editor.Graph.Nodes.Count} nodes / {Editor.Graph.Links.Count} links to {path}");
            RecordAudit(AuditActions.SaveGraph, path);
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
        if (!EnsureAllowed(AuditActions.Run)) return;
        var issues = Editor.Graph.Validate();
        if (issues.Count > 0)
        {
            Log.Add("error", $"Cannot run: {issues[0].Message}");
            Status = "Invalid graph";
            return;
        }
        _running = true;
        OnPropertyChanged(nameof(IsRunning));
        RaiseUndoState();
        _runCts = new CancellationTokenSource();
        _scheduler.Load(Editor.Graph);
        RecordAudit(AuditActions.Run, _currentPath);
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
        if (!EnsureAllowed(AuditActions.Stop)) return;
        await _scheduler.StopAsync();
        _runCts?.Cancel();
        _running = false;
        OnPropertyChanged(nameof(IsRunning));
        RaiseUndoState();
        Post(() => Log.Add("warn", "Run stopped by user"));
        RecordAudit(AuditActions.Stop, _currentPath);
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
            _ = Dashboard.RefreshCommand.ExecuteAsync(null);
        });
    }

    /// <summary>Projects the newest trace rows from the single "trace" source for the board (§9.4). · 为看板从唯一 "trace" 源投影最新追溯行</summary>
    private async Task<IReadOnlyList<TraceRow>> LoadTraceRowsAsync(CancellationToken ct)
    {
        var store = _data.ResolveQueryStore("trace");
        if (store is null) return [];
        var rows = await store.QueryRowsAsync(DashboardViewModel.BoardSql, null, ct).ConfigureAwait(false);
        return rows.Select(row => new TraceRow(
            ToLong(row, "Seq"),
            ToText(row, "TriggerId") ?? "",
            ToText(row, "Batch"),
            ToText(row, "Node"),
            ToText(row, "Kind"),
            ToText(row, "Ts"))).ToList();
    }

    private static object? Cell(IReadOnlyDictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var value) ? value : null;

    private static string? ToText(IReadOnlyDictionary<string, object?> row, string key)
    {
        var value = Cell(row, key);
        return value is null or DBNull ? null : value as string ?? value.ToString();
    }

    private static long ToLong(IReadOnlyDictionary<string, object?> row, string key)
        => Cell(row, key) is { } value ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : 0;

    /// <summary>Opens a destination and streams the board query to CSV through the host exporter (§9.5.2). · 选择目标并经宿主导出器把看板查询流式导出为 CSV</summary>
    private void OnDashboardExport(CsvExportRequest request)
    {
        var export = _data.ResolveExportService("trace");
        if (export is null)
        {
            _dialogs.ReportError("No export service registered for 'trace'.");
            return;
        }
        var path = _dialogs.SaveCsvFile($"trace-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        if (path is null) return;
        RecordAudit(AuditActions.Export, path);
        _ = ExportAsync(export, request, path);
    }

    private async Task ExportAsync(IExportService export, CsvExportRequest request, string path)
    {
        try
        {
            await using var stream = File.Create(path);
            var count = await export.ExportCsvAsync(request, stream, CancellationToken.None).ConfigureAwait(false);
            Post(() => Log.Add("info", $"Exported {count} rows to {path}"));
        }
        catch (Exception ex)
        {
            Post(() =>
            {
                _dialogs.ReportError(ex.Message);
                Log.Add("error", $"Export failed: {ex.Message}");
            });
        }
    }

    /// <summary>Opens a destination and streams the audit query to CSV through the host exporter (§9.1/§9.5.2). · 选择目标并经宿主导出器把审计查询流式导出为 CSV</summary>
    private void OnAuditExport(CsvExportRequest request)
    {
        var export = _data.ResolveExportService("trace");
        if (export is null)
        {
            _dialogs.ReportError("No export service registered for 'trace'.");
            return;
        }
        var path = _dialogs.SaveCsvFile($"audit-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        if (path is null) return;
        RecordAudit(AuditActions.Export, path);
        _ = ExportAsync(export, request, path);
    }

    /// <summary>
    /// Stamps one operation-audit entry (§9.1). Fire-and-forget: a failing audit write is logged
    /// but never disrupts the user's operation. · 写入一条操作审计(§9.1)。即发即忘：审计写入失败仅记日志，
    /// 绝不打断用户操作。
    /// </summary>
    public void RecordAudit(string action, string? target = null, string? before = null, string? after = null)
        => _ = RecordAuditAsync(action, target, before, after);

    /// <summary>
    /// Stamps one operation-audit entry and returns its row id, so an undo can later point back at
    /// the edit it reverted (§9.2 back-link). · 写入一条操作审计并返回其行 id，供撤回时回指被撤销的编辑(§9.2 回链)。
    /// </summary>
    public Task<long> RecordAuditAsync(string action, string? target = null, string? before = null,
        string? after = null, long? undoRecordId = null)
    {
        var record = new OperationRecord(0, DateTimeOffset.UtcNow, CurrentUser, action, target, before, after, undoRecordId);
        return AppendAuditAsync(record);
    }

    private async Task<long> AppendAuditAsync(OperationRecord record)
    {
        try
        {
            return await _audit.RecordAsync(record, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Post(() => Log.Add("warn", $"audit failed: {ex.Message}"));
            return 0;
        }
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