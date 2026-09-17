using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace HalconWorkflow.Storage;

/// <summary>Supported database kinds (ADR-009). · 支持的数据库种类(ADR-009)</summary>
public enum DbProviderKind
{
    Sqlite,
    SqlServer,
    MySql,
    Postgres
}

/// <summary>
/// A named data source: kind + connection string. Graphs reference the name, never the provider.
/// · 具名数据源：种类 + 连接串。图只引用名称，绝不引用具体提供商。
/// </summary>
public sealed record DbConfig(string Name, DbProviderKind Kind, string ConnectionString);

/// <summary>Creates ADO.NET connections for the configured provider. · 为已配置提供商创建 ADO.NET 连接</summary>
public interface IDbConnectionFactory
{
    /// <summary>Creates a new (closed) connection. · 创建新的(未打开)连接</summary>
    DbConnection Create();
}

/// <summary>
/// Default connection factory: SQLite ships in-box; SQL Server / MySQL / PostgreSQL resolve
/// their ADO provider by invariant name at runtime (deployment supplies the driver).
/// · 默认连接工厂：SQLite 随包；SQL Server / MySQL / PostgreSQL 运行期按 invariant 名解析 ADO
///   提供商(部署侧提供驱动)。
/// </summary>
public sealed class DbConnectionFactory(DbConfig config) : IDbConnectionFactory
{
    /// <inheritdoc />
    public DbConnection Create()
    {
        var conn = DbDialect.CreateConnection(config.Kind);
        conn.ConnectionString = config.ConnectionString;
        return conn;
    }
}

/// <summary>
/// Provider dialect: connection creation, DDL bits and keyset-friendly paging.
/// · 提供商方言：连接创建、DDL 片段与利于键集分页的改写。
/// </summary>
public static class DbDialect
{
    /// <summary>Creates a provider connection instance. · 创建提供商连接实例</summary>
    public static DbConnection CreateConnection(DbProviderKind kind) => kind switch
    {
        DbProviderKind.Sqlite => new SqliteConnection(),
        _ => CreateFromFactory(kind)
    };

    private static DbConnection CreateFromFactory(DbProviderKind kind)
    {
        var invariant = InvariantName(kind);
        DbProviderFactory factory;
        try
        {
            factory = DbProviderFactories.GetFactory(invariant);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"ADO.NET provider '{invariant}' for {kind} is not registered; deploy its driver package.", ex);
        }
        return factory.CreateConnection()
            ?? throw new InvalidOperationException($"ADO.NET provider '{invariant}' could not create a connection.");
    }

    /// <summary>ADO provider invariant name. · ADO 提供商 invariant 名</summary>
    public static string InvariantName(DbProviderKind kind) => kind switch
    {
        DbProviderKind.Sqlite => "Microsoft.Data.Sqlite",
        DbProviderKind.SqlServer => "Microsoft.Data.SqlClient",
        DbProviderKind.MySql => "MySqlConnector",
        DbProviderKind.Postgres => "Npgsql",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown provider")
    };

    /// <summary>Auto-increment numeric primary-key column definition. · 自增数字主键列定义</summary>
    public static string AutoIdColumn(DbProviderKind kind) => kind switch
    {
        DbProviderKind.Sqlite => "INTEGER PRIMARY KEY AUTOINCREMENT",
        DbProviderKind.SqlServer => "BIGINT IDENTITY(1,1) PRIMARY KEY",
        DbProviderKind.MySql => "BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY",
        DbProviderKind.Postgres => "BIGSERIAL PRIMARY KEY",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown provider")
    };

    /// <summary>Unbounded text column type. · 无界文本列类型</summary>
    public static string LongText(DbProviderKind kind) => kind switch
    {
        DbProviderKind.SqlServer => "NVARCHAR(MAX)",
        DbProviderKind.MySql => "LONGTEXT",
        _ => "TEXT"
    };

    /// <summary>Bounded text column type. · 有界文本列类型</summary>
    public static string BoundedText(DbProviderKind kind, int length) => kind switch
    {
        DbProviderKind.SqlServer => $"NVARCHAR({length})",
        DbProviderKind.MySql => $"VARCHAR({length})",
        _ => "TEXT"
    };

    /// <summary>Timestamp column type with a default. · 带默认值的时间戳列类型</summary>
    public static string TimestampColumn(DbProviderKind kind) => kind switch
    {
        DbProviderKind.SqlServer => "DATETIME2 DEFAULT SYSUTCDATETIME()",
        DbProviderKind.MySql => "DATETIME DEFAULT CURRENT_TIMESTAMP",
        _ => "TIMESTAMP DEFAULT CURRENT_TIMESTAMP"
    };

    /// <summary>
    /// Appends provider paging. SQL Server requires the caller's query to end with ORDER BY.
    /// · 追加提供商分页。SQL Server 要求调用方查询以 ORDER BY 结尾。
    /// </summary>
    public static string ApplyPaging(DbProviderKind kind, string sql, int pageSize, long offset) => kind switch
    {
        DbProviderKind.SqlServer => $"{sql} OFFSET @__skip ROWS FETCH NEXT @__take ROWS ONLY",
        _ => $"{sql} LIMIT @__take OFFSET @__skip"
    };

    /// <summary>Physical column holding a grouping dimension (§9.5.4): dim_line, dim_machine… · 分组维度物理列</summary>
    public static string DimensionColumn(string key) => "dim_" + key;

    /// <summary>SQL expression extracting a YYYY-MM-DD date key from the ts column. · 从 ts 列提取 YYYY-MM-DD 日期键的 SQL 表达式</summary>
    public static string DateKey(DbProviderKind kind) => kind switch
    {
        DbProviderKind.SqlServer => "CONVERT(varchar(10), ts, 23)",
        DbProviderKind.MySql => "DATE_FORMAT(ts, '%Y-%m-%d')",
        DbProviderKind.Postgres => "to_char(ts, 'YYYY-MM-DD')",
        _ => "substr(ts, 1, 10)"
    };

    /// <summary>
    /// SQL returning the id of the last inserted row. PostgreSQL uses INSERT…RETURNING instead,
    /// so this is only consulted for the other providers. · 返回最后插入行 id 的 SQL。PostgreSQL 用
    /// INSERT…RETURNING，故仅其他提供商使用。
    /// </summary>
    public static string LastInsertIdSql(DbProviderKind kind) => kind switch
    {
        DbProviderKind.SqlServer => "SELECT CAST(SCOPE_IDENTITY() AS BIGINT)",
        DbProviderKind.MySql => "SELECT LAST_INSERT_ID()",
        _ => "SELECT last_insert_rowid()"
    };
}
