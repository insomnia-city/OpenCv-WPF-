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

    /// <summary>
    /// Optional grouping dimensions (line / machine / shift / model / recipe) used by §9.5.4 stats.
    /// Implementations that have none may leave the default (null).
    /// · 可选分组维度(线别/机台/班次/机型/Recipe)，供 §9.5.4 统计分片。无则沿用默认(null)。
    /// </summary>
    IReadOnlyDictionary<string, string?>? Dimensions => null;
}

/// <summary>Well-known grouping dimension keys (§9.5.4). · 已知分组维度键(§9.5.4)</summary>
public static class DimensionKeys
{
    public const string Line = "line";
    public const string Machine = "machine";
    public const string Shift = "shift";
    public const string Model = "model";
    public const string Recipe = "recipe";
}

/// <summary>
/// Default mutable-free traceability record. · 默认的不可变追溯记录
/// </summary>
public sealed record TraceRecord(
    string TriggerId,
    string Node,
    string Kind,
    string? Batch = null,
    string? ResultJson = null,
    string? ImageRef = null) : IRecord
{
    /// <summary>Optional grouping dimensions (§9.5.4). · 可选分组维度(§9.5.4)</summary>
    public IReadOnlyDictionary<string, string?>? Dimensions { get; init; }
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

    /// <summary>
    /// Runs a parameterized query returning rows as column→value maps (column order preserved).
    /// Implementations that cannot return arbitrary rows may leave the default (throws).
    /// · 执行参数化查询并以"列→值"映射返回行(保留列序)。无法返回任意行的实现可沿用默认(抛异常)。
    /// </summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryRowsAsync(string sql, object? p, CancellationToken ct)
        => throw new NotSupportedException($"{GetType().Name} does not support QueryRowsAsync");
}

/// <summary>CSV text encoding (§9.5.2). · CSV 文本编码(§9.5.2)</summary>
public enum CsvEncoding
{
    /// <summary>UTF-8 with BOM so Excel double-click opens cleanly. · UTF-8 带 BOM，Excel 双击即开</summary>
    Utf8Bom,

    /// <summary>GBK (code page 936) for legacy Excel/machines. · GBK(代码页 936)，兼容老 Excel/老机器</summary>
    Gbk
}

/// <summary>
/// One CSV export request. Export is streamed page-by-page and cancelable (§9.5.2).
/// · 一次 CSV 导出请求。导出流式分页且可取消(§9.5.2)
/// </summary>
public sealed record CsvExportRequest(
    string Sql,
    object? Parameters = null,
    int PageSize = 1000,
    CsvEncoding Encoding = CsvEncoding.Utf8Bom,
    IReadOnlyList<string>? Columns = null,
    Action<long>? OnProgress = null);

/// <summary>
/// Host-side export service (§9.5.2): trace query / stats / audit all export through here.
/// · 宿主侧导出服务(§9.5.2)：追溯查询/统计/审计统一经此导出
/// </summary>
public interface IExportService
{
    /// <summary>
    /// Streams a query to CSV, returning the row count. Cancels promptly (per page and per row).
    /// · 将查询流式导出为 CSV 并返回行数。可及时取消(每页与每行)。
    /// </summary>
    Task<long> ExportCsvAsync(CsvExportRequest request, Stream output, CancellationToken ct);
}

/// <summary>
/// Yield/throughput statistics aggregator over the cycle table (§9.5.4). 
/// 基于周期表的良率/产量统计聚合(§9.5.4)
/// </summary>
public interface IStatsService
{
    /// <summary>
    /// Aggregates cycle counts / yield by the given slice dimension over [from, to).
    /// · 在 [from, to) 内按给定切片维度聚合周期计数/良率
    /// </summary>
    Task<IReadOnlyList<YieldSlice>> SliceAsync(YieldDimension dimension, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    /// <summary>
    /// Overall totals (yield / throughput) over [from, to). · [from, to) 内的总体良率/产量
    /// </summary>
    Task<YieldSlice> SummaryAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
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

/// <summary>
/// Snapshot role for image archiving (§9.5.3). · 存图角色(§9.5.3)
/// </summary>
public enum ImageKind
{
    /// <summary>Raw frame straight from the acquisition node. · 采集节点的原始帧</summary>
    Original,

    /// <summary>Rendered overlay (draw boxes / text) product image. · 叠加渲染后的成品图</summary>
    Rendered
}

/// <summary>
/// One archive request: who/what/when a snapshot belongs to (§9.5.3). 
/// · 一次归档请求：快照归属(谁/什么/何时)(§9.5.3)
/// </summary>
public sealed record ImageArchiveRequest(
    string TriggerId,
    ImageKind Kind,
    string? Node = null,
    string? Batch = null,
    long? CycleSeq = null,
    int? Width = null,
    int? Height = null,
    string ContentType = "image/png",
    string? Extension = null);

/// <summary>
/// A materialized image archive entry. · 已落盘的存图条目
/// </summary>
public sealed record ImageAsset(
    long Id,
    string TriggerId,
    string? Node,
    ImageKind Kind,
    string RelativePath,
    string ContentType,
    long SizeBytes,
    int? Width,
    int? Height,
    string? Batch,
    long? CycleSeq,
    DateTimeOffset CapturedAt);

/// <summary>
/// Host-side image archiving (§9.5.3): snapshots go to files + a <c>trace_images</c> row that
/// links back to the cycle; images are never inlined into the hot-path record.
/// · 宿主侧存图(§9.5.3)：快照落文件 + 一行 trace_images 回链周期；图像绝不内联进热路径记录。
/// </summary>
public interface IImageArchive
{
    /// <summary>Stores snapshot bytes and returns the archive entry. · 存快照字节并返回归档条目</summary>
    Task<ImageAsset> SaveAsync(ImageArchiveRequest request, ReadOnlyMemory<byte> content, CancellationToken ct);

    /// <summary>Opens the archived bytes; null when the entry/file is missing. · 打开归档字节；条目/文件缺失返回 null</summary>
    Task<Stream?> OpenReadAsync(long id, CancellationToken ct);

    /// <summary>Lists all archived images of one trigger, oldest first. · 列出单个触发的全部存图(旧→新)</summary>
    Task<IReadOnlyList<ImageAsset>> QueryByTriggerAsync(string triggerId, CancellationToken ct);

    /// <summary>Prunes entries/files older than the cutoff; returns removed count. · 剪枝早于阈值的条目/文件；返回删除数</summary>
    Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct);
}