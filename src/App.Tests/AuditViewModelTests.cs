using System.Globalization;
using Dapper;
using HalconWorkflow.Abstractions;
using HalconWorkflow.App.Services;
using HalconWorkflow.App.ViewModels;
using HalconWorkflow.Storage;
using Xunit;

namespace HalconWorkflow.App.Tests;

/// <summary>
/// Stage-9 audit acceptance (§9.1): the audit view refreshes from the dedicated append-only store,
/// forwards the user/action/object filter, exports the same filtered query, and every surface is a
/// projection. · 阶段9 审计验收(§9.1)：审计视图从独立追加存储刷新、转发用户/动作/对象过滤、
/// 以同一过滤查询导出，且所有表面均为投影。
/// </summary>
public class AuditViewModelTests
{
    private sealed class FakeAudit : IAuditStore
    {
        public IReadOnlyList<OperationRecord> Records { get; set; } = [];
        public AuditFilter? LastFilter { get; private set; }
        public Exception? Fail { get; set; }

        public Task<long> RecordAsync(OperationRecord op, CancellationToken ct) => Task.FromResult(1L);

        public Task<IReadOnlyList<OperationRecord>> QueryAsync(AuditFilter filter, CancellationToken ct)
        {
            LastFilter = filter;
            return Fail is null
                ? Task.FromResult(Records)
                : Task.FromException<IReadOnlyList<OperationRecord>>(Fail);
        }

        public Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct) => Task.FromResult(0);
    }

    private static OperationRecord Record(long id, string action, string? target = null) =>
        new(id, new DateTimeOffset(2026, 1, 1, 8, 0, (int)(id % 60), TimeSpan.Zero), "alice", action, target);

    [Fact]
    public async Task Refresh_PopulatesRowsFromTheStore()
    {
        var store = new FakeAudit { Records = [Record(1, AuditActions.SaveGraph, "a.hflow"), Record(2, AuditActions.Run)] };
        var vm = new AuditViewModel(new LocalizationService(), store);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Rows.Count);
        Assert.True(vm.HasRows);
        Assert.Equal("alice", vm.Rows[0].User);
        Assert.Equal(AuditActions.SaveGraph, vm.Rows[0].Action);
        Assert.Equal("a.hflow", vm.Rows[0].Target);
        Assert.NotEmpty(vm.RangeText);
        Assert.Empty(vm.StatusText);
    }

    [Fact]
    public async Task Refresh_ForwardsUserActionAndObjectFilter()
    {
        var store = new FakeAudit();
        var vm = new AuditViewModel(new LocalizationService(), store)
        {
            FilterUser = "alice",
            FilterAction = AuditActions.SetParameter,
            FilterTarget = "threshold"
        };

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.NotNull(store.LastFilter);
        Assert.Equal("alice", store.LastFilter!.User);
        Assert.Equal(AuditActions.SetParameter, store.LastFilter.Action);
        Assert.Equal("threshold", store.LastFilter.TargetContains);
        Assert.Equal(500, store.LastFilter.Limit);
    }

    [Fact]
    public void Export_RaisesFilteredAuditQuery()
    {
        var vm = new AuditViewModel(new LocalizationService(), new FakeAudit()) { FilterUser = "alice" };
        CsvExportRequest? captured = null;
        vm.ExportRequested += request => captured = request;

        vm.ExportCommand.Execute(null);

        Assert.NotNull(captured);
        Assert.Contains("operation_records", captured!.Sql);
        Assert.Contains("user_name = @user", captured.Sql);
        var parameters = Assert.IsType<DynamicParameters>(captured.Parameters);
        Assert.Equal("alice", parameters.Get<string>("user"));
    }

    [Fact]
    public async Task Clear_EmptiesRowsAndFilters()
    {
        var store = new FakeAudit { Records = [Record(1, AuditActions.Run)] };
        var vm = new AuditViewModel(new LocalizationService(), store) { FilterUser = "alice" };
        await vm.RefreshCommand.ExecuteAsync(null);

        vm.ClearCommand.Execute(null);

        Assert.Empty(vm.Rows);
        Assert.False(vm.HasRows);
        Assert.Equal("", vm.FilterUser);
        Assert.Equal("", vm.RangeText);
    }

    [Fact]
    public async Task Refresh_WhenStoreFails_SurfacesStatus()
    {
        var vm = new AuditViewModel(new LocalizationService(), new FakeAudit { Fail = new InvalidOperationException("boom") });

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("boom", vm.StatusText);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public void CultureSwitch_RelocalizesLabels()
    {
        var loc = new LocalizationService();
        var vm = new AuditViewModel(loc, new FakeAudit());

        loc.Culture = new CultureInfo("en");
        Assert.Equal("Operation Audit", vm.Title);
        Assert.Equal("User", vm.UserLabel);

        loc.Culture = new CultureInfo("zh-Hans");
        Assert.Equal("操作审计", vm.Title);
        Assert.Equal("用户", vm.UserLabel);

        loc.Culture = new CultureInfo("ko");
        Assert.Equal("작업 감사", vm.Title);
    }
}
