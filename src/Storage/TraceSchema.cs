namespace HalconWorkflow.Storage;

/// <summary>
/// A materialized cycle row for queries/exports. Column aliases match the SELECT in
/// <see cref="TraceSchema.QueryAllSql"/>. · 查询/导出用周期行。列别名与 TraceSchema.QueryAllSql 一致。
/// </summary>
public sealed record CycleRow(
    long Seq,
    string TriggerId,
    string? Batch,
    string? Node,
    string? Kind,
    string? ResultJson,
    string? ImageRef,
    string? Ts);

/// <summary>
/// Versioned schema for the traceability table (§8.4): DDL per provider + embedded migration.
/// · 追溯表的分版本 schema(§8.4)：按提供商的 DDL + 内嵌迁移。
/// </summary>
public static class TraceSchema
{
    /// <summary>Current trace schema version. · 当前追溯 schema 版本</summary>
    public const int Version = 1;

    /// <summary>Default SELECT of all cycle columns with stable aliases. · 全列稳定别名默认查询</summary>
    public const string QueryAllSql =
        "SELECT seq AS Seq, trigger_id AS TriggerId, batch AS Batch, node AS Node, " +
        "kind AS Kind, result_json AS ResultJson, image_ref AS ImageRef, ts AS Ts " +
        "FROM cycle_records ORDER BY seq";

    /// <summary>
    /// Creates the trace schema if absent and stamps the version. Idempotent. · 追溯 schema 不存在则创建并写入版本。幂等。
    /// </summary>
    public static async Task EnsureCreatedAsync(System.Data.Common.DbConnection conn, DbProviderKind kind, CancellationToken ct)
    {
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct).ConfigureAwait(false);
        if (await GetVersionAsync(conn, ct).ConfigureAwait(false) >= Version) return;

        foreach (var statement in CreateStatements(kind))
            await ExecuteAsync(conn, statement, ct).ConfigureAwait(false);

        await ExecuteAsync(conn, UpsertVersionSql(kind), ct, ("@v", Version)).ConfigureAwait(false);
    }

    /// <summary>Reads the stamped schema version (0 when the table is absent). · 读取 schema 版本(表缺失返回 0)</summary>
    public static async Task<int> GetVersionAsync(System.Data.Common.DbConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version FROM schema_version WHERE component = 'trace'";
        try
        {
            var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return value is null or DBNull ? 0 : Convert.ToInt32(value);
        }
        catch (System.Data.Common.DbException)
        {
            return 0; // schema_version table not created yet · 版本表尚未创建
        }
    }

    /// <summary>DDL creating all trace objects for a provider. · 为提供商创建所有追溯对象的 DDL</summary>
    public static IReadOnlyList<string> CreateStatements(DbProviderKind kind) =>
    [
        CreateTableIfMissing(kind, "schema_version",
            "component " + DbDialect.BoundedText(kind, 64) + " PRIMARY KEY, version INTEGER NOT NULL"),
        CreateTableIfMissing(kind, "cycle_records",
            "seq " + DbDialect.AutoIdColumn(kind)
            + ", trigger_id " + DbDialect.BoundedText(kind, 64) + " NOT NULL"
            + ", batch " + DbDialect.BoundedText(kind, 128) + " NULL"
            + ", node " + DbDialect.BoundedText(kind, 200) + " NULL"
            + ", kind " + DbDialect.BoundedText(kind, 32) + " NULL"
            + ", result_json " + DbDialect.LongText(kind) + " NULL"
            + ", image_ref " + DbDialect.BoundedText(kind, 400) + " NULL"
            + ", ts " + DbDialect.TimestampColumn(kind)),
        CreateIndexIfMissing(kind, "ix_cycle_batch", "cycle_records", "batch, ts"),
        CreateIndexIfMissing(kind, "ix_cycle_trigger", "cycle_records", "trigger_id")
    ];

    private static string CreateTableIfMissing(DbProviderKind kind, string table, string columns) => kind switch
    {
        DbProviderKind.SqlServer => $"IF OBJECT_ID(N'{table}', N'U') IS NULL CREATE TABLE {table} ({columns})",
        DbProviderKind.MySql => $"CREATE TABLE IF NOT EXISTS {table} ({columns})",
        _ => $"CREATE TABLE IF NOT EXISTS {table} ({columns})"
    };

    private static string CreateIndexIfMissing(DbProviderKind kind, string index, string table, string columns) => kind switch
    {
        DbProviderKind.SqlServer =>
            $"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = '{index}' AND object_id = OBJECT_ID('{table}')) CREATE INDEX {index} ON {table} ({columns})",
        DbProviderKind.MySql => $"CREATE INDEX {index} ON {table} ({columns})",
        _ => $"CREATE INDEX IF NOT EXISTS {index} ON {table} ({columns})"
    };

    private static string UpsertVersionSql(DbProviderKind kind) => kind switch
    {
        DbProviderKind.Sqlite => "INSERT OR REPLACE INTO schema_version (component, version) VALUES ('trace', @v)",
        DbProviderKind.MySql => "REPLACE INTO schema_version (component, version) VALUES ('trace', @v)",
        DbProviderKind.Postgres => "INSERT INTO schema_version (component, version) VALUES ('trace', @v) " +
                                   "ON CONFLICT (component) DO UPDATE SET version = EXCLUDED.version",
        DbProviderKind.SqlServer => "MERGE schema_version AS t USING (SELECT 'trace' AS component, @v AS version) AS s " +
                                    "ON t.component = s.component WHEN MATCHED THEN UPDATE SET version = s.version " +
                                    "WHEN NOT MATCHED THEN INSERT (component, version) VALUES (s.component, s.version);",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown provider")
    };

    private static async Task ExecuteAsync(System.Data.Common.DbConnection conn, string sql, CancellationToken ct,
        (string Name, object Value)? parameter = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (parameter is { } p)
        {
            var pcm = cmd.CreateParameter();
            pcm.ParameterName = p.Name;
            pcm.Value = p.Value;
            cmd.Parameters.Add(pcm);
        }
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
