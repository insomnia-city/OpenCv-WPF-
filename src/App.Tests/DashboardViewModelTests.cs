using System.Globalization;
using HalconWorkflow.Abstractions;
using HalconWorkflow.App.Services;
using HalconWorkflow.App.ViewModels;
using HalconWorkflow.Storage;
using Xunit;

namespace HalconWorkflow.App.Tests;

/// <summary>
/// Stage-9 dashboard acceptance (§9.4/§9.5.1/§9.5.4): the board/summary/slices/preview refresh
/// from injected host services, dimensions reload slices, export carries the single-source board
/// query, and every surface is a projection. · 阶段9 看板验收：看板/汇总/切片/预览经注入宿主服务刷新、
/// 维度切换重载切片、导出携带同源看板查询、所有表面均为投影。
/// </summary>
public class DashboardViewModelTests
{
    private sealed class FakeStats : IStatsService
    {
        public YieldSlice Summary { get; set; } = new("(all)", 10, 8, 2, 80d);
        public IReadOnlyList<YieldSlice> Slices { get; set; } = [new("L1", 5, 5, 0, 100d)];
        public YieldDimension LastDimension { get; private set; } = (YieldDimension)(-1);

        public Task<YieldSlice> SummaryAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
            => Task.FromResult(Summary);

        public Task<IReadOnlyList<YieldSlice>> SliceAsync(YieldDimension dimension, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
        {
            LastDimension = dimension;
            return Task.FromResult(Slices);
        }
    }

    private static DashboardViewModel Build(FakeStats stats, PreviewRing ring, LocalizationService loc,
        IReadOnlyList<TraceRow>? rows = null) =>
        new(loc, stats, ring, _ => Task.FromResult(rows ?? [new TraceRow(1, "t1", "B1", "data.write:1", "ok", "2026-01-01 00:00:00")]));

    [Fact]
    public async Task Refresh_PopulatesBoardSummarySlicesAndPreview()
    {
        var stats = new FakeStats();
        var ring = new PreviewRing();
        ring.Publish(new PreviewFrame("vision.inspect:2", DateTimeOffset.UtcNow, Summary: "ok"));
        var vm = Build(stats, ring, new LocalizationService());

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Single(vm.Records);
        Assert.True(vm.HasRecords);
        Assert.Equal("10", vm.CyclesText);
        Assert.Equal("8", vm.OkText);
        Assert.Equal("2", vm.NgText);
        Assert.Equal("80.0%", vm.YieldText);
        Assert.Single(vm.Slices);
        Assert.Equal("L1", vm.Slices[0].Key);
        Assert.True(vm.HasPreview);
        Assert.Equal("vision.inspect:2", vm.PreviewNode);
        Assert.Equal(YieldDimension.Line, stats.LastDimension);
    }

    [Fact]
    public async Task DimensionChange_ReloadsSlicesForThatDimension()
    {
        var stats = new FakeStats();
        var vm = Build(stats, new PreviewRing(), new LocalizationService());

        vm.SelectedDimension = vm.DimensionOptions.Single(o => o.Value == YieldDimension.Machine);
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(YieldDimension.Machine, stats.LastDimension);
    }

    [Fact]
    public void Export_RaisesRequestOverTheSingleTraceSource()
    {
        var vm = Build(new FakeStats(), new PreviewRing(), new LocalizationService());
        CsvExportRequest? captured = null;
        vm.ExportRequested += request => captured = request;

        vm.ExportCommand.Execute(null);

        Assert.NotNull(captured);
        Assert.Equal(DashboardViewModel.BoardSql, captured!.Sql);
        Assert.Contains("cycle_records", captured.Sql);
    }

    [Fact]
    public async Task Clear_EmptiesBoardAndPreview_WithoutTouchingSource()
    {
        var stats = new FakeStats();
        var ring = new PreviewRing();
        ring.Publish(new PreviewFrame("vision.inspect:2", DateTimeOffset.UtcNow, Summary: "ok"));
        var vm = Build(stats, ring, new LocalizationService());
        await vm.RefreshCommand.ExecuteAsync(null);

        vm.ClearCommand.Execute(null);

        Assert.Empty(vm.Records);
        Assert.Empty(vm.Slices);
        Assert.False(vm.HasPreview);
        Assert.Equal("-", vm.YieldText);
        Assert.False(vm.HasRecords);
    }

    [Fact]
    public void CultureSwitch_RelocalizesLabelsAndDimensions()
    {
        var loc = new LocalizationService();
        var vm = Build(new FakeStats(), new PreviewRing(), loc);

        loc.Culture = new CultureInfo("en");

        Assert.Equal("Trace Board", vm.Title);
        Assert.Equal("Line", vm.DimensionOptions[0].Name);

        loc.Culture = new CultureInfo("zh-Hans");
        Assert.Equal("追溯看板", vm.Title);
        Assert.Equal("线别", vm.DimensionOptions[0].Name);
    }
}
