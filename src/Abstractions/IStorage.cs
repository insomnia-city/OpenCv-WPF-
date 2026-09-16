namespace HalconWorkflow.Abstractions;

/// <summary>
/// A traceability record (one measurement / decision / trace). · 一条追溯记录(测量/判定/追踪)
/// </summary>
public interface IRecord
{
    /// <summary>
    /// Signal token (trigger id). · 信号令牌(触发ID)
    /// </summary>
    string TriggerId { get; }

    /// <summary>
    /// Optional batch number. · 批次号
    /// </summary>
    string? Batch { get; }

    /// <summary>
    /// Contract:instance id of the emitting node. · 发出节点的 契约:实例ID
    /// </summary>
    string Node { get; }

    /// <summary>
    /// ok / ng / measure / trace. · 结果类别
    /// </summary>
    string Kind { get; }

    /// <summary>
    /// Result snapshot (HObject converted to metadata only). · 结果快照(HObject 仅转元数据)
    /// </summary>
    string? ResultJson { get; }

    /// <summary>
    /// Snapshot file relative path (images are not stored as BLOB). 
    /// 快照文件相对路径(图像不塞 BLOB)
    /// </summary>
    string? ImageRef { get; }
}

/// <summary>
/// Hot-path traceability writer: enqueue and return immediately, never block a cycle (§8.3). 
/// 热路径追溯写入：只入队立即返回,绝不阻塞周期(§8.3)
/// </summary>
public interface IRecordStore : IAsyncDisposable
{
    /// <summary>
    /// Appends a record to the write queue. · 将记录追加到写入队列
    /// </summary>
    Task AppendAsync(IRecord record, CancellationToken ct);
}

/// <summary>
/// Low-frequency query/export store. Parameters are always bound (§8.2). 
/// 低频查询/导出存储。SQL 一律参数化(§8.2)
/// </summary>
public interface IQueryStore : IAsyncDisposable
{
    /// <summary>
    /// Runs a parameterized query. · 执行参数化查询
    /// </summary>
    Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? p, CancellationToken ct);

    /// <summary>
    /// Runs a non-query command. · 执行非查询命令
    /// </summary>
    Task<int> ExecuteAsync(string sql, object? p, CancellationToken ct);
}

/// <summary>
/// Yield/throughput statistics aggregator over the cycle table (§9.5.4). 
/// 基于周期表的良率/产量统计聚合(§9.5.4)
/// </summary>
public interface IStatsService
{
    /// <summary>
    /// Aggregates cycle counts / yield by the given slice dimension. 
    /// 按切片维度聚合周期计数/良率
    /// </summary>
    Task<YieldSlice> SliceAsync(YieldDimension dimension, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
}

/// <summary>
/// Supported slice dimensions for stats. · 统计支持的切片维度
/// </summary>
public enum YieldDimension
{
    Line,        // 线别
    Machine,     // 机台
    Shift,       // 班次
    Model,       // 机型
    Recipe,      // 配方
    Date         // 日期
}

/// <summary>
/// A single aggregated slice. · 单个聚合切片
/// </summary>
public readonly record struct YieldSlice(
    string Key,
    int Cycles,
    int Ok,
    int Ng,
    double YieldPercent);