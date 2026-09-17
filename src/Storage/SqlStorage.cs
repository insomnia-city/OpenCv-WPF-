using Dapper;
using HalconWorkflow.Abstractions;

namespace HalconWorkflow.Storage;

/// <summary>
/// SQL-backed storage adapter (ADR-009): hot-path <see cref="IRecordStore"/> over the async batch
/// queue + low-frequency <see cref="IQueryStore"/>. The graph engine never references this type;
/// nodes resolve it through an <c>IDataRuntime</c>.
/// · SQL 存储适配器(ADR-009)：热路径 IRecordStore 走异步批量队列 + 低频 IQueryStore。图引擎从不
///   引用此类型；节点经 IDataRuntime 解析。
/// </summary>
public sealed class SqlStorage : IRecordStore, IQueryStore, IAsyncDisposable
{
    private readonly IDbConnectionFactory _factory;
    private readonly DbProviderKind _kind;
    private readonly TraceWriteQueue _queue;

    /// <summary>Creates the adapter; call <see cref="InitializeAsync"/> before use. · 创建适配器；使用前调用 InitializeAsync</summary>
    public SqlStorage(DbConfig config, TraceQueueOptions? options = null, ITraceSink? sink = null)
    {
        Config = config;
        _factory = new DbConnectionFactory(config);
        _kind = config.Kind;
        _queue = new TraceWriteQueue(sink ?? new SqlTraceSink(_factory), options);
    }

    /// <summary>Data source config. · 数据源配置</summary>
    public DbConfig Config { get; }

    /// <summary>Hot-path queue diagnostics. · 热路径队列诊断</summary>
    public TraceWriteQueue Queue => _queue;

    /// <summary>Ensures the trace schema exists and starts the batch pump. · 确保追溯 schema 存在并启动批量泵</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var conn = _factory.Create();
        await TraceSchema.EnsureCreatedAsync(conn, _kind, ct).ConfigureAwait(false);
        _queue.Start();
    }

    /// <inheritdoc />
    public Task AppendAsync(IRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        ct.ThrowIfCancellationRequested();
        _queue.Enqueue(new TraceRecord(record.TriggerId, record.Node, record.Kind, record.Batch, record.ResultJson, record.ImageRef)
        {
            Dimensions = record.Dimensions
        });
        return Task.CompletedTask;
    }

    /// <summary>Waits until all enqueued records are written or dropped. · 等待所有入队记录写入或丢弃</summary>
    public Task FlushAsync(CancellationToken ct) => _queue.FlushAsync(ct);

    /// <summary>Reads all cycle records (stable alias query). · 读取全部周期记录(稳定别名查询)</summary>
    public Task<IReadOnlyList<CycleRow>> ReadAllAsync(CancellationToken ct)
        => QueryAsync<CycleRow>(TraceSchema.QueryAllSql, null, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? p, CancellationToken ct)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<T>(new CommandDefinition(sql, p, cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    /// <inheritdoc />
    public async Task<int> ExecuteAsync(string sql, object? p, CancellationToken ct)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteAsync(new CommandDefinition(sql, p, cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryRowsAsync(string sql, object? p, CancellationToken ct)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync(new CommandDefinition(sql, p, cancellationToken: ct)).ConfigureAwait(false);
        var result = new List<IReadOnlyDictionary<string, object?>>();
        foreach (IDictionary<string, object> row in rows)
        {
            var dict = new Dictionary<string, object?>(row.Count, StringComparer.Ordinal);
            foreach (var pair in row) dict[pair.Key] = pair.Value is DBNull ? null : pair.Value;
            result.Add(dict);
        }
        return result;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _queue.DisposeAsync().ConfigureAwait(false);
    }
}
