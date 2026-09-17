using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HalconWorkflow.Abstractions;
using HalconWorkflow.App.Services;
using HalconWorkflow.Storage;

namespace HalconWorkflow.App.ViewModels;

/// <summary>One cycle row shown in the trace board (§9.4). · 看板中的一条周期行(§9.4)</summary>
public sealed record TraceRow(long Seq, string TriggerId, string? Batch, string? Node, string? Kind, string? Ts);

/// <summary>One aggregated yield slice row (§9.5.4). · 一行良率聚合切片(§9.5.4)</summary>
public sealed record YieldRow(string Key, int Cycles, int Ok, int Ng, string YieldText);

/// <summary>A selectable slice dimension with its localized label. · 可选的切片维度(含本地化标签)</summary>
public sealed record DimensionOption(YieldDimension Value, string Name);

/// <summary>
/// Trace board + yield dashboard + result preview (§9.1/§9.4/§9.5.1/§9.5.4). Reads the one and
/// only trace source through injected host services; every binding is a projection, never a
/// kernel entity. · 追溯看板 + 良率看板 + 结果预览(§9.1/§9.4/§9.5.1/§9.5.4)。经注入的宿主服务读取
/// 唯一追溯源；所有绑定都是投影，绝不暴露内核实体。
/// </summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    private const int RangeHours = 24;
    private const int MaxRows = 200;

    /// <summary>Board query: newest cycles first, single source of truth (§8). · 看板查询：最新周期在前，唯一事实源(§8)</summary>
    public static string BoardSql { get; } =
        "SELECT seq AS Seq, trigger_id AS TriggerId, batch AS Batch, node AS Node, kind AS Kind, ts AS Ts " +
        "FROM cycle_records ORDER BY seq DESC LIMIT " + MaxRows.ToString(CultureInfo.InvariantCulture);

    private readonly LocalizationService _loc;
    private readonly IStatsService _stats;
    private readonly PreviewRing _preview;
    private readonly Func<CancellationToken, Task<IReadOnlyList<TraceRow>>> _loadRows;
    private bool _suppressRefresh;

    public DashboardViewModel(
        LocalizationService loc,
        IStatsService stats,
        PreviewRing preview,
        Func<CancellationToken, Task<IReadOnlyList<TraceRow>>> loadRows)
    {
        _loc = loc ?? throw new ArgumentNullException(nameof(loc));
        _stats = stats ?? throw new ArgumentNullException(nameof(stats));
        _preview = preview ?? throw new ArgumentNullException(nameof(preview));
        _loadRows = loadRows ?? throw new ArgumentNullException(nameof(loadRows));
        DimensionOptions = BuildDimensionOptions();
        _suppressRefresh = true;
        SelectedDimension = DimensionOptions[0];
        _suppressRefresh = false;
        _loc.PropertyChanged += (_, _) => OnCultureChanged();
    }

    /// <summary>Newest cycle rows (the board table). · 最新周期行(看板表格)</summary>
    public ObservableCollection<TraceRow> Records { get; } = [];

    /// <summary>Yield slices for the selected dimension. · 选中维度的良率切片</summary>
    public ObservableCollection<YieldRow> Slices { get; } = [];

    /// <summary>Selectable slice dimensions. · 可选切片维度</summary>
    public IReadOnlyList<DimensionOption> DimensionOptions { get; private set; }

    [ObservableProperty] private DimensionOption _selectedDimension = null!;
    [ObservableProperty] private string _cyclesText = "-";
    [ObservableProperty] private string _okText = "-";
    [ObservableProperty] private string _ngText = "-";
    [ObservableProperty] private string _yieldText = "-";
    [ObservableProperty] private string _rangeText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _previewNode = "";
    [ObservableProperty] private string _previewInfo = "";
    [ObservableProperty] private byte[]? _previewImage;
    [ObservableProperty] private bool _busy;

    /// <summary>True when the board has rows. · 看板有行</summary>
    public bool HasRecords => Records.Count > 0;

    /// <summary>True when a preview frame exists. · 存在预览帧</summary>
    public bool HasPreview => !string.IsNullOrEmpty(PreviewNode);

    /// <summary>Raised on export; the shell owns the file dialog + IExportService (§9.5.2). · 导出时触发；壳层负责文件对话框与导出服务(§9.5.2)</summary>
    public event Action<CsvExportRequest>? ExportRequested;

    public string Title => _loc["dash.title"];
    public string RefreshLabel => _loc["dash.refresh"];
    public string ClearLabel => _loc["dash.clear"];
    public string ExportLabel => _loc["dash.export"];
    public string DimensionLabel => _loc["dash.dimension"];
    public string CyclesLabel => _loc["dash.cycles"];
    public string OkLabel => _loc["dash.ok"];
    public string NgLabel => _loc["dash.ng"];
    public string YieldLabel => _loc["dash.yield"];
    public string RecordsLabel => _loc["dash.records"];
    public string SlicesLabel => _loc["dash.slices"];
    public string PreviewLabel => _loc["dash.preview"];
    public string NoPreviewLabel => _loc["dash.nopreview"];
    public string ColSeq => _loc["dash.col.seq"];
    public string ColTrigger => _loc["dash.col.trigger"];
    public string ColBatch => _loc["dash.col.batch"];
    public string ColNode => _loc["dash.col.node"];
    public string ColKind => _loc["dash.col.kind"];
    public string ColTs => _loc["dash.col.ts"];
    public string ColKey => _loc["dash.col.key"];
    public string ColCycles => _loc["dash.col.cycles"];
    public string ColOk => _loc["dash.col.ok"];
    public string ColNg => _loc["dash.col.ng"];
    public string ColYield => _loc["dash.col.yield"];

    /// <summary>Reloads board rows, summary, slices and preview from the trace source. · 从追溯源重载看板行/汇总/切片/预览</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            var to = DateTimeOffset.UtcNow;
            var from = to.AddHours(-RangeHours);
            RangeText = $"{from:yyyy-MM-dd HH:mm} → {to:HH:mm}";

            var rows = await _loadRows(CancellationToken.None).ConfigureAwait(true);
            Records.Clear();
            foreach (var row in rows) Records.Add(row);
            OnPropertyChanged(nameof(HasRecords));

            var summary = await _stats.SummaryAsync(from, to, CancellationToken.None).ConfigureAwait(true);
            CyclesText = summary.Cycles.ToString(CultureInfo.CurrentCulture);
            OkText = summary.Ok.ToString(CultureInfo.CurrentCulture);
            NgText = summary.Ng.ToString(CultureInfo.CurrentCulture);
            YieldText = FormatPercent(summary.YieldPercent);

            var slices = await _stats.SliceAsync(SelectedDimension.Value, from, to, CancellationToken.None).ConfigureAwait(true);
            Slices.Clear();
            foreach (var slice in slices)
                Slices.Add(new YieldRow(slice.Key, slice.Cycles, slice.Ok, slice.Ng, FormatPercent(slice.YieldPercent)));

            RefreshPreview();
            StatusText = "";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Clears the board, slices and preview (does not touch the trace source). · 清空看板/切片/预览(不动追溯源)</summary>
    [RelayCommand]
    private void Clear()
    {
        Records.Clear();
        Slices.Clear();
        PreviewNode = "";
        PreviewInfo = "";
        PreviewImage = null;
        CyclesText = OkText = NgText = YieldText = "-";
        RangeText = "";
        StatusText = "";
        OnPropertyChanged(nameof(HasRecords));
        OnPropertyChanged(nameof(HasPreview));
    }

    /// <summary>Raises the export request for the current board query. · 为当前看板查询触发导出请求</summary>
    [RelayCommand]
    private void Export() => ExportRequested?.Invoke(new CsvExportRequest(BoardSql, PageSize: 500));

    partial void OnSelectedDimensionChanged(DimensionOption value)
    {
        if (_suppressRefresh) return;
        _ = RefreshAsync();
    }

    private void RefreshPreview()
    {
        var frame = _preview.Latest();
        PreviewNode = frame?.Node ?? "";
        PreviewInfo = frame is null
            ? ""
            : string.IsNullOrEmpty(frame.Summary)
                ? frame.CapturedAt.ToString("HH:mm:ss", CultureInfo.CurrentCulture)
                : $"{frame.CapturedAt:HH:mm:ss} · {frame.Summary}";
        PreviewImage = frame?.Image;
        OnPropertyChanged(nameof(HasPreview));
    }

    private void OnCultureChanged()
    {
        var previous = SelectedDimension?.Value ?? YieldDimension.Line;
        DimensionOptions = BuildDimensionOptions();
        _suppressRefresh = true;
        SelectedDimension = DimensionOptions.First(o => o.Value == previous);
        _suppressRefresh = false;
        OnPropertyChanged(nameof(DimensionOptions));
        OnPropertyChanged((string?)null);
    }

    private IReadOnlyList<DimensionOption> BuildDimensionOptions() =>
    [
        new(YieldDimension.Line, _loc["dim.line"]),
        new(YieldDimension.Machine, _loc["dim.machine"]),
        new(YieldDimension.Shift, _loc["dim.shift"]),
        new(YieldDimension.Model, _loc["dim.model"]),
        new(YieldDimension.Recipe, _loc["dim.recipe"]),
        new(YieldDimension.Date, _loc["dim.date"])
    ];

    private static string FormatPercent(double value) => value.ToString("F1", CultureInfo.CurrentCulture) + "%";
}
