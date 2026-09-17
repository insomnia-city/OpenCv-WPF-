using Dapper;
using HalconWorkflow.Abstractions;

namespace HalconWorkflow.Storage;

/// <summary>
/// SQL batch sink: one Dapper multi-execute per batch inside a transaction (§8.3). 
/// · SQL 批量接收器：每批在一个事务内用 Dapper 多次执行(§8.3)
/// </summary>
public sealed class SqlTraceSink(IDbConnectionFactory factory, int commandTimeoutSeconds = 30) : ITraceSink
{
    private const string InsertSql =
        "INSERT INTO cycle_records (trigger_id, batch, node, kind, result_json, image_ref) " +
        "VALUES (@TriggerId, @Batch, @Node, @Kind, @ResultJson, @ImageRef)";

    /// <inheritdoc />
    public async Task WriteBatchAsync(IReadOnlyList<TraceRecord> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        await using var conn = factory.Create();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition(
            InsertSql, batch, tx, commandTimeoutSeconds, cancellationToken: ct)).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }
}
