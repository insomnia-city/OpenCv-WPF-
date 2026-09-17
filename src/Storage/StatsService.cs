using System.Globalization;
using Dapper;
using HalconWorkflow.Abstractions;

namespace HalconWorkflow.Storage;

/// <summary>
/// Yield / throughput stats (§9.5.4) aggregated directly over <c>cycle_records</c> — the same
/// single source of truth as the trace board and CSV export (ADR-014: no second copy).
/// · 良率/产量统计(§9.5.4)直接聚合 cycle_records——与追溯看板/CSV 导出同一事实来源
///   (ADR-014：不另建第二份)。
/// </summary>
public sealed class StatsService(IDbConnectionFactory factory, DbProviderKind kind) : IStatsService
{
    private const string SelectTemplate =
        "SELECT {0} AS SliceKey, COUNT(*) AS Cycles, " +
        "COALESCE(SUM(CASE WHEN kind = 'ok' THEN 1 ELSE 0 END), 0) AS Ok, " +
        "COALESCE(SUM(CASE WHEN kind = 'ng' THEN 1 ELSE 0 END), 0) AS Ng " +
        "FROM cycle_records WHERE ts >= @from AND ts < @to";

    /// <inheritdoc />
    public async Task<IReadOnlyList<YieldSlice>> SliceAsync(YieldDimension dimension, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var key = KeyExpression(dimension);
        var sql = string.Format(CultureInfo.InvariantCulture, SelectTemplate, key)
                  + " GROUP BY " + key + " ORDER BY Cycles DESC";
        var rows = await QueryAsync(sql, from, to, ct).ConfigureAwait(false);
        return rows.Select(ToSlice).ToList();
    }

    /// <inheritdoc />
    public async Task<YieldSlice> SummaryAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var sql = string.Format(CultureInfo.InvariantCulture, SelectTemplate, "'(all)'");
        var rows = await QueryAsync(sql, from, to, ct).ConfigureAwait(false);
        return rows.Count == 0 ? new YieldSlice("(all)", 0, 0, 0, 0) : ToSlice(rows[0]);
    }

    private async Task<List<SliceRow>> QueryAsync(string sql, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<SliceRow>(new CommandDefinition(
            sql, new { from = from.UtcDateTime, to = to.UtcDateTime }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    private static YieldSlice ToSlice(SliceRow row)
    {
        var judged = row.Ok + row.Ng;
        var yield = judged == 0 ? 0d : row.Ok * 100d / judged;
        return new YieldSlice(row.SliceKey ?? "", (int)row.Cycles, (int)row.Ok, (int)row.Ng, yield);
    }

    private string KeyExpression(YieldDimension dimension) => dimension switch
    {
        YieldDimension.Date => "COALESCE(" + DbDialect.DateKey(kind) + ", '(none)')",
        YieldDimension.Line => Column(DimensionKeys.Line),
        YieldDimension.Machine => Column(DimensionKeys.Machine),
        YieldDimension.Shift => Column(DimensionKeys.Shift),
        YieldDimension.Model => Column(DimensionKeys.Model),
        YieldDimension.Recipe => Column(DimensionKeys.Recipe),
        _ => throw new ArgumentOutOfRangeException(nameof(dimension), dimension, "unknown dimension")
    };

    private static string Column(string key) => "COALESCE(" + DbDialect.DimensionColumn(key) + ", '(none)')";

    private sealed class SliceRow
    {
        public string? SliceKey { get; set; }
        public long Cycles { get; set; }
        public long Ok { get; set; }
        public long Ng { get; set; }
    }
}
