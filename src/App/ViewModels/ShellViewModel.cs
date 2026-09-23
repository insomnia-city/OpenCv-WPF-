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
using HalconWorkflow.Core.Graph;
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
using HalconWorkflow.Nodes.Vision.Imaging;
using HalconWorkflow.Nodes.Vision.Nodes;
using HalconWorkflow.Plugins;
using HalconWorkflow.Protocols;
using HalconWorkflow.Protocols.Devices;
using HalconWorkflow.Protocols.Modbus;
using HalconWorkflow.Protocols.Serialization;
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

    /// <summary>
    /// Device catalog location shared with the trace database (§7.1 stage-21): user-level,
    /// survives restarts, and is git-ignored by the environment. · 设备目录存储位置,与追溯库同目录
    /// (§7.1 阶段21)：用户级、跨重启保留,环境中不入库。
    /// </summary>
    internal static string DevicesFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HalconWorkflow", "devices.json");

    private readonly LocalizationService _loc;
    private readonly IDialogService _dialogs;
    private readonly SynchronizationContext? _ui;
    private readonly NodeSnapshotCache _snapshotCache;
    private readonly GraphScheduler _scheduler;
    private readonly UndoService _undo = new();
    // Resolved at runtime: real Halcon adapter when the deployment site registers one and
    // a licensed runtime is present; otherwise the deterministic phantom fallback (§6.3).
    // / 运行期解析：部署现场注册适配器且存在授权运行时用真实 Halcon;否则确定性幻影回退（§6.3）。
    private readonly VisionEnginePool _visionPool = new(
        () => VisionEngineFactory.CreateResolved(), capacity: 2);
    private readonly CommRuntime _comm;
    // Null when no catalog entry is a loopback simulator (e.g. all real PLC profiles).
    // · 目录中没有回环模拟器设备时为 null(如全为真实 PLC 配置)。
    private readonly ModbusTcpSimulator? _commSim;
    private readonly MotionRuntime _motion;
    private readonly DataRuntime _data;
    private readonly StatsService _stats;
    private readonly ImageArchiveStore _images;
    private readonly PreviewRing _preview;
    private readonly SqlAuditService _audit;
    private readonly RoleService _roles = new();
    // Node contracts contributed by external plugins discovered at startup (§10, ADR-007).
    // / 启动时发现的外部插件贡献的节点契约（§10，ADR-007）。
    private readonly PluginCatalog _plugins = new();
    private readonly Dictionary<IUndoableCommand, long> _auditIds = new(ReferenceEqualityComparer.Instance);
    private CancellationTokenSource? _runCts;
    private string? _currentPath;
    // Loaded (or default demo) device catalog that the comm runtime is built from (§7.1 stage-21).
    // · 已装载(或默认演示)设备目录,通讯运行时由它构建(§7.1 阶段21)。
    private readonly DeviceCatalogFile _deviceFile;
    private readonly string _devicesPath;
    // Recipe persisted separately from topology (§5.8). · 与拓扑分开持久化的配方(§5.8)
    private Recipe _recipe = new();
    // Migration chain for legacy documents / contract versions (§12). · 遗留文档/契约版本的迁移链(§12)
    private readonly GraphMigrator _migrator = new(schemaMigrators: [new LegacySchemaV0ToV1()]);
    private bool _migratedSinceLoad;
    private bool _running;
    private int _seq;
    private int _paletteOffset;
    private NodeViewModel? _selectedNode;
    private PortViewModel? _pendingSource;
    // Background trigger plane for this run (timer / tag-change pulses, §5.4 stage-20). · 本次运行的触发平面(定时/Tag 变化脉冲,§5.4 阶段20)
    private TriggerEngine? _triggerEngine;
    // Stage-24 independent image window view model. · 阶段24 独立图像窗视图模型
    private readonly ImageWindowViewModel _imageWindowVm;

    /// <summary>True when the scheduler is currently paused at a breakpoint. · 调度器当前在断点处暂停 */
    public bool IsPaused => _scheduler.State == SchedulerState.Paused;

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

    /// <summary>Resolved optional-subsystem capabilities: real backend vs §6.3 software fallback. · 已解析的可选子系统能力：真实后端 vs §6.3 软回退</summary>
    public IReadOnlyList<CapabilityStatus> Capabilities { get; }

    /// <summary>External plugins loaded from the <c>/plugins</c> folder at startup (§10, ADR-007). · 启动时从 <c>/plugins</c> 载入的外部插件（§10，ADR-007）</summary>
    public PluginLoadResult? Plugins { get; }

    /// <summary>Bounded recent-frames ring for the result preview (§9.5.1). · 结果预览的最近帧有界环(§9.5.1)</summary>
    public PreviewRing Preview => _preview;

    /// <summary>Stage-24 independent image window view model. · 阶段24 独立图像窗视图模型</summary>
    public ImageWindowViewModel ImageWindowVm => _imageWindowVm;

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
    public string TriggerEditLabel => _loc["trigger.edit"];
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
    public string MenuSaveDevices => _loc["menu.saveDevices"];
    public string MenuImageWindow => _loc["menu.imageWindow"];
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

    /// <summary>
    /// True when the graph differs from the last save/load point, so New/Load/Close must confirm.
    /// · 图与最近保存/载入点不同时为真；新建/载入/关闭需确认。
    /// </summary>
    public bool HasUnsavedChanges => _undo.IsModifiedSinceSave;

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
        : this(loc, dialogs, DevicesFilePath)
    {
    }

    /// <summary>
    /// Internal: injects the device-catalog path so tests isolate catalog state to a temp dir.
    /// · 内部：注入设备目录路径,测试可将目录状态隔离到临时目录。
    /// </summary>
    internal ShellViewModel(LocalizationService loc, IDialogService dialogs, string devicesPath)
    {
        _devicesPath = devicesPath;
        _loc = loc;
        _dialogs = dialogs;
        _ui = SynchronizationContext.Current;
        _snapshotCache = new NodeSnapshotCache();
        _scheduler = new GraphScheduler(_snapshotCache);
        Editor = new MainEditorViewModel();
        Log = new LogViewModel();
        PropertyPanel = new PropertyPanelViewModel(loc);
        PropertyPanel.ParameterCommitted += (name, value) =>
            _ = ApplyParameterAsync(_selectedNode, name, value);
        Editor.RemoveAsyncHandler = RemoveNodeAsync;
        Editor.DisconnectAsyncHandler = DisconnectConnectionAsync;
        Editor.TextProvider = key => _loc[key];
        _loc.PropertyChanged += (_, _) => OnCultureChanged();
        Editor.New();
        BuildPalette();
        _scheduler.NodeExecuted += OnNodeEvent;
        _scheduler.RunCompleted += OnRunCompleted;
        _scheduler.DebugPaused += id => Post(() => Status = $"{_loc["status.paused"]} {id}");
        _scheduler.Services[typeof(IVisionEnginePool)] = _visionPool;
        _deviceFile = LoadDeviceCatalog();
        (_comm, _commSim) = BuildCommRuntime(_deviceFile);
        _scheduler.Services[typeof(ICommRuntime)] = _comm;
        _motion = BuildMotionRuntime();
        _scheduler.Services[typeof(IMotionRuntime)] = _motion;
        (_data, _stats, _images, _preview, _audit) = BuildDataRuntime();
        _scheduler.Services[typeof(IDataRuntime)] = _data;
        _scheduler.Services[typeof(IStatsService)] = _stats;
        _scheduler.Services[typeof(IImageArchive)] = _images;
        _scheduler.Services[typeof(PreviewRing)] = _preview;
        _scheduler.Services[typeof(IAuditStore)] = _audit;
        _imageWindowVm = new ImageWindowViewModel(_loc, _preview);
        Dashboard = new DashboardViewModel(_loc, _stats, _preview, LoadTraceRowsAsync);
        Dashboard.ExportRequested += OnDashboardExport;
        AuditView = new AuditViewModel(_loc, _audit);
        AuditView.ExportRequested += OnAuditExport;
        RoleOptions = BuildRoleOptions();
        _selectedRole = RoleOptions.First(o => o.Value == _roles.Current);
        _undo.MarkSaved();
        Capabilities = RuntimeCapabilities.Collect();
        foreach (var capability in Capabilities)
            Log.Add("info", $"{_loc[capability.LabelKey]}: " +
                $"{(capability.Real ? _loc["capability.real"] : _loc["capability.fallback"])} — {capability.Detail}");
        Plugins = PluginHost.LoadDirectory(
            Path.Combine(AppContext.BaseDirectory, PluginHost.DefaultFolder), _plugins);
        foreach (var loaded in Plugins.Plugins)
            Log.Add(loaded.Error is null ? "info" : "warn",
                loaded.Error is null
                    ? $"plugin {loaded.Name} {loaded.Version}: {loaded.Contracts} contract(s)"
                    : $"plugin {loaded.Name} failed: {loaded.Error}");
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
        Editor.RebindConnections(); // refresh localized connection menus · 刷新连线菜单的本地化文本
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
    /// Loads the persisted device catalog, falling back to the default demo profile when
    /// missing or malformed (§7.1, stage-21). The graph stays adapter-agnostic: swapping a
    /// profile connects a real PLC instead of the loopback demo.
    /// / 装载持久化设备目录；缺失或损坏时回退默认演示配置(§7.1,阶段21)。图保持适配器无关：
    ///   更换配置即可连接真实 PLC 而非回环演示。
    /// </summary>
    private DeviceCatalogFile LoadDeviceCatalog()
    {
        try
        {
            if (File.Exists(_devicesPath))
            {
                var loaded = DeviceCatalogSerializer.TryDeserialize(File.ReadAllText(_devicesPath));
                if (loaded is { Devices.Count: > 0 })
                {
                    Log.Add("info", $"Loaded {loaded.Devices.Count} device(s) from {_devicesPath}");
                    return loaded;
                }
                Log.Add("warn", $"Ignoring empty/invalid device catalog '{_devicesPath}'; using demo profile");
            }
        }
        catch (Exception ex)
        {
            Log.Add("warn", $"Failed to load device catalog '{_devicesPath}': {ex.Message}");
        }
        var demo = DeviceCatalog.CreateDemoCatalog();
        Log.Add("info", $"No device catalog yet; using demo profile ({demo.Devices[0].Tags.Count} tags). " +
            "Save Devices to persist it. · 暂无设备目录,使用演示配置;点击『保存设备』持久化。");
        return demo;
    }

    /// <summary>
    /// Builds the comm runtime from persisted device profiles (§7.1, stage-21): every device
    /// connection (loopback-simulated or real) auto-reconnects on transport failure. The graph
    /// references tags by name and stays connection-agnostic.
    /// / 按持久化设备配置构建通讯运行时(§7.1,阶段21)：回环模拟或真实连接在传输故障时自动重连。
    ///   图按名引用 Tag,与连接方式无关。
    /// </summary>
    private (CommRuntime Runtime, ModbusTcpSimulator? Sim) BuildCommRuntime(DeviceCatalogFile catalog)
    {
        var tags = new DeviceCatalog(catalog).BuildTagTable();
        var needsLoopback = catalog.Devices.Any(d => d.Loopback);
        var sim = needsLoopback ? ModbusTcpSimulator.Start() : null;
        var runtime = new CommRuntime(tags);
        foreach (var profile in catalog.Devices)
            runtime.Add(new DeviceCatalog(catalog).CreateConnection(profile, tags, loopbackPort: sim?.Port));
        return (runtime, sim);
    }

    /// <summary>
    /// Persists the current device catalog to the user data folder (§7.1, stage-21). On next
    /// start the same devices/tags are restored; real PLC edits survive restarts too.
    /// / 把当前设备目录持久化到用户数据目录(§7.1,阶段21)。下次启动即恢复相同设备/Tag;
    ///   真实 PLC 的修改同样跨重启保留。
    /// </summary>
    [RelayCommand]
    private void SaveDevices()
    {
        if (!EnsureAllowed(AuditActions.DeviceSave)) return;
        try
        {
            var dir = Path.GetDirectoryName(_devicesPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(_devicesPath, DeviceCatalogSerializer.Serialize(_deviceFile));
            Log.Add("info", $"Saved {_deviceFile.Devices.Count} device(s) / " +
                $"{_deviceFile.Devices.Sum(d => d.Tags.Count)} tag(s) to {_devicesPath}");
            RecordAudit(AuditActions.DeviceSave, _devicesPath);
            Status = _loc["status.devicesSaved"];
        }
        catch (Exception ex)
        {
            _dialogs.ReportError(ex.Message);
            Log.Add("error", $"Device save failed: {ex.Message}");
        }
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
        if (_commSim is not null)
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
        Palette.Add(new NodeCatalogItem("tagTrigger", _loc["palette.tagTrigger"], "comm.tagtrigger:1", id => comm.Create(new NodeContract("comm.tagtrigger", 1), id)!));
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

    /// <summary>
    /// Interactive pending-connection start (§4.3, stage-12): remembers the source port and lights
    /// every other port as a valid (green) or invalid (red) drop target, then clears on drop/cancel.
    /// / 拖拽连线开始(§4.3,阶段12)：记录源端口并将其它端口标记为合法(绿)或非法(红)落点；落点/取消后清除
    /// </summary>
    [RelayCommand]
    private void ConnectionStarted(PortViewModel? source)
    {
        if (source is null || _running) return;
        _pendingSource = source;
        UpdateConnectHighlights(source);
    }

    /// <summary>
    /// Interactive pending-connection drop (§4.3, stage-12): validates and creates the link through
    /// the global undo path with permission + audit (§9.2/§9.3).
    /// / 拖拽连线落点(§4.3,阶段12)：经全局撤销路径校验并建线，含权限与审计(§9.2/§9.3)
    /// </summary>
    [RelayCommand]
    private async Task ConnectionCompletedAsync(PortViewModel? target)
    {
        var source = _pendingSource;
        _pendingSource = null;
        ClearConnectHighlights();
        if (source is null || target is null || _running) return;
        await ConnectAsync(source, target);
    }

    /// <summary>Disconnects every link attached to a port (Nodify connector disconnect gesture). · 断开某端口关联的全部连线(连接器手势)</summary>
    [RelayCommand]
    private async Task DisconnectConnectorAsync(PortViewModel? port)
    {
        if (port is null || _running) return;
        var affected = Editor.Connections
            .Where(c => ReferenceEquals(c.FromPort, port) || ReferenceEquals(c.ToPort, port)).ToList();
        foreach (var conn in affected) await DisconnectConnectionAsync(conn);
    }

    /// <summary>
    /// Lights every port as a drop target for the pending connection (§4.3). Same kernel Validator
    /// as save-time static validation. · 将每个端口标为拖拽落点(§4.3)；与保存时静态校验同源
    /// </summary>
    private void UpdateConnectHighlights(PortViewModel source)
    {
        foreach (var nv in Editor.Nodes)
            foreach (var p in nv.Inputs.Concat(nv.Outputs))
                p.Highlight = ReferenceEquals(p, source) ? ConnectState.None : JudgeTarget(source, p);
    }

    private ConnectState JudgeTarget(PortViewModel source, PortViewModel candidate)
    {
        // An already-fed data input is never a valid second source. · 已连线的数据输入不可再作为目标
        if (candidate.IsInput && candidate.IsData && candidate.IsConnected) return ConnectState.Invalid;
        var pair = NormalizePair(source, candidate);
        if (pair is null) return ConnectState.Invalid;
        return Editor.Graph.ValidateLink(pair.Value.From.Kernel, pair.Value.To.Kernel) is null
            ? ConnectState.Valid
            : ConnectState.Invalid;
    }

    private void ClearConnectHighlights()
    {
        foreach (var nv in Editor.Nodes)
            foreach (var p in nv.Inputs.Concat(nv.Outputs))
                p.Highlight = ConnectState.None;
    }

    /// <summary>
    /// Normalizes a drag pair into (output, input); null when both ports share a direction.
    /// 把拖拽端口对归一化为(输出,输入)；同向时返回 null
    /// </summary>
    private static (PortViewModel From, PortViewModel To)? NormalizePair(PortViewModel a, PortViewModel b)
    {
        if (a.IsOutput && b.IsInput) return (a, b);
        if (a.IsInput && b.IsOutput) return (b, a);
        return null;
    }

    /// <summary>
    /// Interactive connect: permission → kernel validation → undoable command → rebind → audit (§9.2/§9.3).
    /// / 交互连线：权限 → 内核校验 → 可撤销命令 → 重建投影 → 审计(§9.2/§9.3)
    /// </summary>
    private async Task ConnectAsync(PortViewModel a, PortViewModel b)
    {
        if (_running) return;
        if (!EnsureAllowed(AuditActions.Connect)) return;

        var pair = NormalizePair(a, b);
        if (pair is null)
        {
            ReportConnectError(_loc["link.error.direction"]);
            return;
        }
        var (from, to) = pair.Value;
        if (to.Kernel.Kind == PortKind.Data && to.IsConnected)
        {
            ReportConnectError(_loc["link.error.singleSource"]);
            return;
        }
        var issue = Editor.Graph.ValidateLink(from.Kernel, to.Kernel);
        if (issue is not null)
        {
            ReportConnectError(LocalizeIssue(issue.Value));
            return;
        }

        var command = GraphCommands.Connect(Editor.Graph, from.Kernel, to.Kernel);
        try
        {
            await _undo.PushAndRunAsync(command, CancellationToken.None);
            Editor.RebindConnections();
            Log.Add("info", $"Connected {from.Kernel.Owner.Id}:{from.Name} → {to.Kernel.Owner.Id}:{to.Name}");
            _auditIds[command] = await RecordAuditAsync(AuditActions.Connect,
                $"{from.Kernel.Owner.Id}:{from.Name}→{to.Kernel.Owner.Id}:{to.Name}");
        }
        catch (Exception ex)
        {
            _dialogs.ReportError(ex.Message);
            Log.Add("error", $"Connect failed: {ex.Message}");
        }
        RaiseUndoState();
    }

    /// <summary>
    /// Interactive disconnect: permission → undoable command → rebind → audit (§9.2/§9.3).
    /// / 交互断线：权限 → 可撤销命令 → 重建投影 → 审计(§9.2/§9.3)
    /// </summary>
    private async Task DisconnectConnectionAsync(ConnectionViewModel conn)
    {
        if (conn is null || _running) return;
        if (!EnsureAllowed(AuditActions.Disconnect)) return;
        var command = GraphCommands.Disconnect(Editor.Graph, conn.Link);
        try
        {
            await _undo.PushAndRunAsync(command, CancellationToken.None);
            Editor.RebindConnections();
            Log.Add("info", $"Disconnected {conn.From.Id}:{conn.FromPort.Name} → {conn.To.Id}:{conn.ToPort.Name}");
            _auditIds[command] = await RecordAuditAsync(AuditActions.Disconnect,
                $"{conn.From.Id}:{conn.FromPort.Name}→{conn.To.Id}:{conn.ToPort.Name}");
        }
        catch (Exception ex)
        {
            _dialogs.ReportError(ex.Message);
            Log.Add("error", $"Disconnect failed: {ex.Message}");
        }
        RaiseUndoState();
    }

    /// <summary>Maps a kernel validation issue to a localized message (ADR-010). · 将内核校验问题映射为本地化消息</summary>
    private string LocalizeIssue(ValidationIssue issue) => issue.Kind switch
    {
        ValidationIssueKind.TypeMismatch => _loc["link.error.type"],
        ValidationIssueKind.Cycle => _loc["link.error.cycle"],
        _ => _loc["link.error.invalid"]
    };

    /// <summary>Logs and reflects a refused interactive connection in the status bar. · 记录拒绝的交互连线并反映到状态栏</summary>
    private void ReportConnectError(string message)
    {
        Log.Add("warn", message);
        Post(() => Status = message);
    }
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
        OnPropertyChanged(nameof(HasUnsavedChanges));
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
        _recipe = new();
        _migratedSinceLoad = false;
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
            var factory = new CombinedNodeFactory(new VisionNodeFactory(), new SampleNodeFactory(), new Nodes.Flow.FlowNodeFactory(), new CommNodeFactory(), new MotionNodeFactory(), new DataNodeFactory(), _plugins);
            var recipePath = RecipePathFor(path);
            var hasRecipe = File.Exists(recipePath);
            _recipe = hasRecipe ? Recipe.Deserialize(File.ReadAllText(recipePath)) : new Recipe();

            var result = GraphJsonSerializer.Load(File.ReadAllText(path), factory, _migrator, _recipe);
            var graph = result.Graph;

            if (result.Migration.HasChanges)
            {
                Log.Add("warn", $"Upgraded document schema {result.Migration.FromSchemaVersion}→{result.Migration.ToSchemaVersion} with {result.Migration.NodeMigrations.Count} node contract change(s)");
                if (!_dialogs.Confirm(_loc.Get("dialog.migrated", result.Migration.ToSchemaVersion, result.Migration.NodeMigrations.Count)))
                {
                    Log.Add("warn", "Load cancelled at migration confirmation");
                    return;
                }
                _migratedSinceLoad = true;
            }
            else
            {
                _migratedSinceLoad = false;
            }

            if (result.Suspended.Count > 0)
            {
                Log.Add("warn", $"Loaded {result.Suspended.Count} suspended node(s): {string.Join(", ", result.Suspended.Select(s => s.Id))}");
                if (!_dialogs.Confirm(_loc.Get("dialog.suspended", result.Suspended.Count)))
                {
                    foreach (var s in result.Suspended) graph.RemoveNode(s.Id);
                    Log.Add("warn", $"Removed {result.Suspended.Count} suspended node(s) on request");
                }
            }

            var issues = graph.Validate();
            foreach (var i in issues) Log.Add("warn", $"Validate: {i.Kind}: {i.Message}");

            var restored = RecipeBinder.Restore(graph, _recipe);
            Log.Add(hasRecipe ? "info" : "warn", hasRecipe
                ? $"Applied recipe to {restored} node(s) from {recipePath}"
                : $"No recipe beside {Path.GetFileName(path)}; using parameter defaults");

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
        if (_migratedSinceLoad && !_dialogs.Confirm(_loc["dialog.migratedSave"]))
        {
            Log.Add("warn", "Save cancelled at upgrade confirmation");
            return;
        }
        try
        {
            File.WriteAllText(path, GraphJsonSerializer.Serialize(Editor.Graph));
            RecipeBinder.Capture(Editor.Graph, _recipe);
            var recipePath = RecipePathFor(path);
            File.WriteAllText(recipePath, Recipe.Serialize(_recipe));
            _currentPath = path;
            _migratedSinceLoad = false;
            Editor.Title = Path.GetFileName(path);
            _undo.MarkSaved(); // edits above here are "unsaved" · 此点之上视为未保存
            RaiseUndoState();
            Log.Add("info", $"Saved {Editor.Graph.Nodes.Count} nodes / {Editor.Graph.Links.Count} links to {path}");
            Log.Add("info", $"Saved recipe ({_recipe.Params.Count} node(s)) to {recipePath}");
            RecordAudit(AuditActions.SaveGraph, path);
        }
        catch (Exception ex)
        {
            _dialogs.ReportError(ex.Message);
            Log.Add("error", $"Save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Sidecar recipe path for a graph document (§5.8): <c>X.graph.json</c> pairs with
    /// <c>X.recipe.json</c>; any other suffix becomes <c>path.recipe.json</c>. 
    /// / 图文档的伴生配方路径(§5.8)：X.graph.json 对应 X.recipe.json;其它后缀追加 .recipe.json
    /// </summary>
    private static string RecipePathFor(string graphPath)
    {
        const string suffix = ".graph.json";
        return graphPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? graphPath[..^suffix.Length] + ".recipe.json"
            : graphPath + ".recipe.json";
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
        // Stage-20 trigger plane: debounce + background sources (timer / tag change) feeding the queue. · 阶段20 触发平面：去抖 + 后台源(定时/Tag 变化)喂入调度队列
        _scheduler.DebounceMs = Editor.Graph.Trigger.DebounceMs;
        var triggerConfig = Editor.Graph.Trigger;
        var nudger = new ActionNudger(src => _scheduler.Trigger(src));
        _scheduler.Services[typeof(ITriggerNudger)] = nudger;
        _triggerEngine = new TriggerEngine(triggerConfig, src => _scheduler.Trigger(src), tagSources: TagSourceFor);
        RecordAudit(AuditActions.Run, _currentPath);
        Post(() =>
        {
            foreach (var n in Editor.Nodes) n.State = NodeState.Idle;
            Log.Add("info", "Run started");
        });
        await _scheduler.StartAsync(_runCts.Token);
        _triggerEngine.Start();
        _scheduler.Trigger(TriggerSource.Manual);
        Status = _loc["status.ready"];
    }

    /// <summary>
    /// Resolves a tag-pattern to a change source on the comm runtime; null when the device is
    /// unknown (engine then stays idle on TagChange). · 把 Tag 模式解析为通讯运行时上的变化源;
    /// 设备未知返回 null(引擎在 TagChange 下保持空闲)
    /// </summary>
    private ITagObserverSource? TagSourceFor(string pattern)
    {
        var deviceId = pattern.Split('/', 2)[0];
        var conn = _comm.Resolve(deviceId);
        return conn is null ? null : new CommTagSource(conn, pattern);
    }

    /// <summary>Adapts a device connection's <see cref="IDeviceConnection"/> observable into the engine seam. · 把设备连接的可观察序列适配为引擎缝</summary>
    private sealed class CommTagSource(IDeviceConnection connection, string pattern) : ITagObserverSource
    {
        public IDisposable Subscribe(Action<object?> onNext)
            => connection.Subscribe(pattern).Subscribe(new TagValueObserver(onNext));

        private sealed class TagValueObserver(Action<object?> onNext) : IObserver<HalconWorkflow.Abstractions.TagValue>
        {
            public void OnCompleted() { }
            public void OnError(Exception error) { }
            public void OnNext(HalconWorkflow.Abstractions.TagValue value) => onNext(value.Value);
        }
    }

    /// <summary>Opens the independent image window for the selected node. · 打开选中节点的独立图像窗</summary>
    [RelayCommand]
    private void OpenImageWindow()
    {
        var sel = _selectedNode;
        if (sel is null) return;
        _imageWindowVm.Open(sel.Id, sel.Header);
        _dialogs.ShowImageWindow(_imageWindowVm);
    }

    /// <summary>Toggles the selected node's breakpoint; audits the change. · 切换选中节点的断点;审计变更</summary>
    [RelayCommand]
    private void ToggleSelectedBreakpoint()
    {
        if (_selectedNode is null || _running) return;
        if (_scheduler.IsBreakpoint(_selectedNode.Id))
            _scheduler.RemoveBreakpoint(_selectedNode.Id);
        else
            _scheduler.AddBreakpoint(_selectedNode.Id);
        _selectedNode.IsBreakpoint = _scheduler.IsBreakpoint(_selectedNode.Id);
        RecordAudit(AuditActions.SetBreakpoint, _selectedNode.Id, after: _scheduler.IsBreakpoint(_selectedNode.Id).ToString());
    }

    /// <summary>Pauses/resumes the scheduler. · 暂停/继续调度器</summary>
    [RelayCommand]
    private void TogglePause()
    {
        if (!_running) return;
        if (_scheduler.State == SchedulerState.Paused)
            _scheduler.Continue();
        else
            _scheduler.Pause();
    }

    /// <summary>Single-step: resume for one cycle then halt again. · 单步：继续一轮后再次停驻</summary>
    [RelayCommand]
    private void StepOnce()
    {
        if (!_running || _scheduler.State != SchedulerState.Paused) return;
        _scheduler.Step();
    }

    /// <summary>Queues a rerun from the selected node behind the current cycle. · 从选中节点重跑，排于当前周期之后</summary>
    [RelayCommand]
    private async void RerunFromSelected()
    {
        if (_selectedNode is null || !_running) return;
        RecordAudit(AuditActions.RunRerun, _selectedNode.Id);
        try
        {
            _ = await _scheduler.RunFromAsync(_selectedNode.Id, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // Session ended concurrently; ignore. · 会话并发结束,忽略
        }
    }

    /// <summary>Stops the scheduler loop. · 停止调度器循环</summary>
    [RelayCommand]
    private async Task StopAsync()
    {
        if (!_running) return;
        if (!EnsureAllowed(AuditActions.Stop)) return;
        if (_triggerEngine is not null) await _triggerEngine.StopAsync();
        _triggerEngine = null;
        await _scheduler.StopAsync();
        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = null;
        _running = false;
        OnPropertyChanged(nameof(IsRunning));
        RaiseUndoState();
        Post(() => Log.Add("warn", "Run stopped by user"));
        RecordAudit(AuditActions.Stop, _currentPath);
        Status = _loc["status.ready"];
    }

    /// <summary>
    /// Opens the trigger-settings dialog; a confirmed edit lands as an undoable command and audits. 
    /// · 打开触发设置对话框;确认的修改以可撤销命令落地并审计
    /// </summary>
    [RelayCommand]
    private async Task TriggerSettingsAsync()
    {
        if (_running) return;
        if (!EnsureAllowed(AuditActions.TriggerEdit)) return;
        var before = Snapshot(Editor.Graph.Trigger);
        var changed = _dialogs.EditTrigger(Editor.Graph.Trigger);
        if (!changed) { Log.Add("info", "Trigger settings cancelled"); return; }
        var after = Snapshot(Editor.Graph.Trigger);
        var command = GraphCommands.SetTrigger(Editor.Graph, before, after);
        try
        {
            await _undo.PushAndRunAsync(command, CancellationToken.None);
            RaiseUndoState();
            Log.Add("info", $"Trigger config: enabled={after.Enabled} source={after.Source}" +
                $"{(after.Tag is { } t ? $" tag={t}" : "")} interval={after.IntervalMs}ms debounce={after.DebounceMs}ms");
            await RecordAuditAsync(AuditActions.TriggerEdit, after.Source.ToString(),
                after: $"{after.Enabled};{after.Source};{after.Tag};{after.IntervalMs};{after.DebounceMs}");
        }
        catch (Exception ex)
        {
            _dialogs.ReportError(ex.Message);
        }
    }

    /// <summary>Snapshots a trigger config for undo before/after comparison. · 为撤销对比快照一份触发配置</summary>
    private static TriggerConfig Snapshot(TriggerConfig config) => new()
    {
        Enabled = config.Enabled,
        Source = config.Source,
        Tag = config.Tag,
        DebounceMs = config.DebounceMs,
        QueueLimit = config.QueueLimit,
        IntervalMs = config.IntervalMs
    };

    private void OnNodeEvent(NodeExecutionEvent evt)
    {
        Post(() =>
        {
            var nv = Editor.Nodes.FirstOrDefault(n => n.Id == evt.NodeId);
            if (nv is not null)
            {
                nv.State = MapPhase(evt.Phase);
                if (evt.Phase == NodeExecutionPhase.Completed)
                {
                    nv.LastElapsedMsText = $"{evt.ElapsedMs} ms";
                    nv.IsBreakpoint = _scheduler.IsBreakpoint(evt.NodeId);
                }
                if (evt.Values is not null)     // stage-13 live scope values · 阶段13 运行期 scope 值
                {
                    foreach (var port in nv.Outputs.Where(p => p.IsData))
                    {
                        if (evt.Values.TryGetValue(port.Name, out var value))
                            port.ValueText = RuntimeValueFormatter.Format(value);
                    }
                    PublishLivePreview(nv.Id, evt.Values);
                }
            }
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

    /// <summary>
    /// Stage-13 image preview producer: encodes the first VisionFrame among the node's outputs to PNG
    /// and pushes it into the preview ring (with a lightweight live dashboard repaint). Gated by the
    /// ring's Enabled switch so high-throughput runs can shut warming off. · 阶段13 图像预览生产者：
    /// 把节点输出中的首个 VisionFrame 编码为 PNG 推入预览环(并轻量刷新看板预览)。受预览环 Enabled
    /// 开关门控，高吞吐运行可关闭以免升温。
    /// </summary>
    private void PublishLivePreview(string nodeId, IReadOnlyDictionary<string, object?> values)
    {
        if (!_preview.Enabled) return;
        var frame = values.Values.OfType<VisionFrame>().FirstOrDefault();
        if (frame is null) return;
        var png = VisionPreviewEncoder.ToPng(frame);
        if (png is null) return;
        _preview.Publish(new PreviewFrame(
            nodeId, DateTimeOffset.UtcNow, png, $"{frame.Width}×{frame.Height} {frame.Format}"));
        Dashboard.PushPreview();
    }

    private void OnRunCompleted(GraphRunResult result)
    {
        Post(() =>
        {
            // One-shot (manual) sessions end when the first cycle completes, unless a
            // rerun-from-node cone is still queued behind it — the session stays live
            // until the cone completes too. · 手动单发会话在首轮完成后结束，除非其后仍有
            // 排队的从节点重跑锥集——会话保持活动直到锥集也完成。
            if (!HasBackgroundTrigger && !_scheduler.HasPendingRerun)
            {
                _runCts?.Cancel();
                _runCts?.Dispose();
                _runCts = null;
                _running = false;
                OnPropertyChanged(nameof(IsRunning));
            }
            Log.Add(result.Success ? "info" : "error",
                $"Run finished: success={result.Success} ok={result.SucceededNodes} failed={result.FaultedNodes} ({result.Duration.TotalMilliseconds:F0}ms)");
            Status = result.Success ? _loc["status.ready"] : "Faulted";
            _ = Dashboard.RefreshCommand.ExecuteAsync(null);
        });
    }

    /// <summary>
    /// True when the graph config asks for a background pulse source that must run until stopped. 
    /// · 图配置是否请求需运行到被停止的后台脉冲源
    /// </summary>
    private bool HasBackgroundTrigger =>
        Editor.Graph.Trigger.Enabled
        && Editor.Graph.Trigger.Source is TriggerSource.Timer or TriggerSource.TagChange;

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
        => !_undo.IsModifiedSinceSave || _dialogs.Confirm(_loc["dialog.discard"]);

    /// <summary>
    /// Guards window close: refuses to close while there are unsaved changes the user won't discard.
    /// · 关闭窗口守卫：存在未保存改动且用户不放弃时拒绝关闭。
    /// </summary>
    public bool ConfirmClose() => ConfirmDiscard();

    private void Post(Action action)
    {
        if (_ui is null) action();
        else _ui.Post(_ => action(), null);
    }
}