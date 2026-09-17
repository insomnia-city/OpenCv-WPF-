using System.Globalization;
using Dapper;
using HalconWorkflow.Abstractions;

namespace HalconWorkflow.Storage;

/// <summary>
/// SQL-backed operation audit (§9.1): an append-only <c>operation_records</c> table, separate from
/// Serilog and the cycle trace. Supports filtered queries (user / time / object) and pruning.
/// · SQL 操作审计(§9.1)：独立于 Serilog 与周期追溯的追加表 operation_records。
///   支持按用户/时间/对象过滤查询与剪枝。
/// </summary>
public sealed class SqlAuditService : IAuditStore
{
    private const string InsertSql =
        "INSERT INTO operation_records (occurred_at, user_name, action, target, before_json, after_json, undo_record_id) " +
        "VALUES (@At, @UserName, @Action, @Target, @Before, @After, @UndoRecordId)";

    private readonly IDbConnectionFactory _factory;
    private readonly DbProviderKind _kind;

    /// <summary>Creates the audit service for one provider. · 为单一提供商创建审计服务</summary>
    public SqlAuditService(IDbConnectionFactory factory, DbProviderKind kind)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _kind = kind;
    }

    /// <inheritdoc />
    public async Task<long> RecordAsync(OperationRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        var parameters = new
        {
            At = record.At.UtcDateTime,
            UserName = record.User,
            record.Action,
            record.Target,
            Before = record.Before,
            After = record.After,
            record.UndoRecordId
        };

        await using var conn = _factory.Create();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        if (_kind == DbProviderKind.Postgres)
            return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                InsertSql + " RETURNING id", parameters, cancellationToken: ct)).ConfigureAwait(false);

        await conn.ExecuteAsync(new CommandDefinition(InsertSql, parameters, cancellationToken: ct)).ConfigureAwait(false);
        var scalar = await conn.ExecuteScalarAsync(new CommandDefinition(
            DbDialect.LastInsertIdSql(_kind), cancellationToken: ct)).ConfigureAwait(false);
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OperationRecord>> QueryAsync(AuditFilter filter, CancellationToken ct)
    {
        var (where, parameters) = AuditSql.Filter(filter);
        var sql = AuditSql.BaseSelect + where + AuditSql.OrderByNewest;
        if (filter.Limit > 0)
        {
            sql = DbDialect.ApplyPaging(_kind, sql, filter.Limit, 0);
            parameters.Add("__take", filter.Limit);
            parameters.Add("__skip", 0L);
        }

        await using var conn = _factory.Create();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<AuditRow>(new CommandDefinition(sql, parameters, cancellationToken: ct))
            .ConfigureAwait(false);
        return rows.Select(ToRecord).ToList();
    }

    /// <inheritdoc />
    public async Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM operation_records WHERE occurred_at < @cutoff",
            new { cutoff = olderThan.UtcDateTime }, cancellationToken: ct)).ConfigureAwait(false);
    }

    private static OperationRecord ToRecord(AuditRow row) => new(
        row.Id,
        new DateTimeOffset(DateTime.SpecifyKind(row.OccurredAt, DateTimeKind.Utc)),
        row.UserName ?? string.Empty,
        row.Action ?? string.Empty,
        row.Target,
        row.BeforeJson,
        row.AfterJson,
        row.UndoRecordId);

    private sealed class AuditRow
    {
        public long Id { get; set; }
        public DateTime OccurredAt { get; set; }
        public string? UserName { get; set; }
        public string? Action { get; set; }
        public string? Target { get; set; }
        public string? BeforeJson { get; set; }
        public string? AfterJson { get; set; }
        public long? UndoRecordId { get; set; }
    }
}
