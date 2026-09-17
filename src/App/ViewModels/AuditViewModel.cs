using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HalconWorkflow.Abstractions;
using HalconWorkflow.App.Services;
using HalconWorkflow.Storage;

namespace HalconWorkflow.App.ViewModels;

/// <summary>One audit row shown in the operation-audit view (§9.1). · 审计视图中的一条操作记录(§9.1)</summary>
public sealed record AuditRow(long Id, string At, string User, string Action, string? Target);

/// <summary>
/// Operation-audit view (§9.1): filterable, exportable view over the dedicated append-only
/// <c>operation_records</c> table — separate from the cycle trace. Every binding is a projection,
/// never a kernel entity. · 操作审计视图(§9.1)：对独立追加表 operation_records 的可过滤/可导出视图，
/// 与周期追溯分离。所有绑定都是投影，绝不暴露内核实体。
/// </summary>
public sealed partial class AuditViewModel : ObservableObject
{
    private const int RangeDays = 30;
    private const int MaxRows = 500;

    private readonly LocalizationService _loc;
    private readonly IAuditStore _audit;

    public AuditViewModel(LocalizationService loc, IAuditStore audit)
    {
        _loc = loc ?? throw new ArgumentNullException(nameof(loc));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _loc.PropertyChanged += (_, _) => OnPropertyChanged((string?)null);
    }

    /// <summary>Newest audit rows matching the current filter. · 匹配当前过滤的最新审计行</summary>
    public ObservableCollection<AuditRow> Rows { get; } = [];

    [ObservableProperty] private string _filterUser = "";
    [ObservableProperty] private string _filterAction = "";
    [ObservableProperty] private string _filterTarget = "";
    [ObservableProperty] private string _rangeText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _busy;

    /// <summary>True when the audit view has rows. · 审计视图有行</summary>
    public bool HasRows => Rows.Count > 0;

    /// <summary>Raised on export; the shell owns the file dialog + IExportService (§9.5.2). · 导出时触发；壳层负责文件对话框与导出服务(§9.5.2)</summary>
    public event Action<CsvExportRequest>? ExportRequested;

    public string Title => _loc["audit.title"];
    public string RefreshLabel => _loc["audit.refresh"];
    public string ClearLabel => _loc["audit.clear"];
    public string ExportLabel => _loc["audit.export"];
    public string UserLabel => _loc["audit.user"];
    public string ActionLabel => _loc["audit.action"];
    public string TargetLabel => _loc["audit.target"];
    public string EmptyLabel => _loc["audit.empty"];
    public string ColAt => _loc["audit.col.at"];
    public string ColUser => _loc["audit.col.user"];
    public string ColAction => _loc["audit.col.action"];
    public string ColTarget => _loc["audit.col.target"];

    /// <summary>Reloads audit rows from the store under the current filter. · 按当前过滤从存储重载审计行</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            var to = DateTimeOffset.UtcNow;
            var from = to.AddDays(-RangeDays);
            RangeText = $"{from:yyyy-MM-dd} → {to:yyyy-MM-dd}";

            var records = await _audit.QueryAsync(BuildFilter(from, to), CancellationToken.None).ConfigureAwait(true);
            Rows.Clear();
            foreach (var record in records) Rows.Add(ToRow(record));
            OnPropertyChanged(nameof(HasRows));
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

    /// <summary>Clears the audit view (does not touch the store). · 清空审计视图(不动存储)</summary>
    [RelayCommand]
    private void Clear()
    {
        Rows.Clear();
        FilterUser = "";
        FilterAction = "";
        FilterTarget = "";
        RangeText = "";
        StatusText = "";
        OnPropertyChanged(nameof(HasRows));
    }

    /// <summary>Raises an export request over the same filtered query the view shows. · 对视图所示同一过滤查询触发导出请求</summary>
    [RelayCommand]
    private void Export()
    {
        var to = DateTimeOffset.UtcNow;
        var from = to.AddDays(-RangeDays);
        var (where, parameters) = AuditSql.Filter(BuildFilter(from, to));
        var sql = AuditSql.BaseSelect + where + AuditSql.OrderByNewest;
        ExportRequested?.Invoke(new CsvExportRequest(sql, parameters, PageSize: 500));
    }

    private AuditFilter BuildFilter(DateTimeOffset from, DateTimeOffset to) => new(
        From: from,
        To: to,
        User: string.IsNullOrWhiteSpace(FilterUser) ? null : FilterUser.Trim(),
        Action: string.IsNullOrWhiteSpace(FilterAction) ? null : FilterAction.Trim(),
        TargetContains: string.IsNullOrWhiteSpace(FilterTarget) ? null : FilterTarget.Trim(),
        Limit: MaxRows);

    private static AuditRow ToRow(OperationRecord record) => new(
        record.Id,
        record.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture),
        record.User,
        record.Action,
        record.Target);
}
