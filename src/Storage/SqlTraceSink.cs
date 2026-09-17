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
        "INSERT INTO cycle_records (trigger_id, batch, node, kind, result_json, image_ref, " +
        "dim_line, dim_machine, dim_shift, dim_model, dim_recipe) " +
        "VALUES (@TriggerId, @Batch, @Node, @Kind, @ResultJson, @ImageRef, " +
        "@DimLine, @DimMachine, @DimShift, @DimModel, @DimRecipe)";

    /// <inheritdoc />
    public async Task WriteBatchAsync(IReadOnlyList<TraceRecord> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        var rows = batch.Select(ToParameters).ToList();
        await using var conn = factory.Create();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition(
            InsertSql, rows, tx, commandTimeoutSeconds, cancellationToken: ct)).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private static object ToParameters(TraceRecord record) => new
    {
        record.TriggerId,
        record.Batch,
        record.Node,
        record.Kind,
        record.ResultJson,
        record.ImageRef,
        DimLine = Dimension(record, DimensionKeys.Line),
        DimMachine = Dimension(record, DimensionKeys.Machine),
        DimShift = Dimension(record, DimensionKeys.Shift),
        DimModel = Dimension(record, DimensionKeys.Model),
        DimRecipe = Dimension(record, DimensionKeys.Recipe)
    };

    private static string? Dimension(TraceRecord record, string key)
        => record.Dimensions is { } d && d.TryGetValue(key, out var value) ? value : null;
}
