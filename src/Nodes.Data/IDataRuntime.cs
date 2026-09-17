using HalconWorkflow.Abstractions;

namespace HalconWorkflow.Nodes.Data;

/// <summary>
/// Protocol/provider-agnostic data runtime: resolves the record/query/export stores of a named
/// data source. Registered as a scheduler service so data nodes stay decoupled from Storage.
/// · 协议/提供商无关的数据运行时：按数据源名称解析记录/查询/导出存储。作为调度器服务注册，
///   使数据节点与 Storage 解耦。
/// </summary>
public interface IDataRuntime
{
    /// <summary>Resolves the hot-path record store of a source; null when absent. · 解析数据源的热路径记录存储；无则 null</summary>
    IRecordStore? ResolveRecordStore(string source);

    /// <summary>Resolves the query store of a source; null when absent. · 解析数据源的查询存储；无则 null</summary>
    IQueryStore? ResolveQueryStore(string source);

    /// <summary>Resolves the CSV export service of a source; null when absent. · 解析数据源的 CSV 导出服务；无则 null</summary>
    IExportService? ResolveExportService(string source);
}

/// <summary>
/// In-memory runtime holding named data sources. · 持有具名数据源的内存运行时
/// </summary>
public sealed class DataRuntime : IDataRuntime, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Source> _sources = new(StringComparer.Ordinal);

    /// <summary>Registers a data source with any of its stores. · 登记数据源及其存储</summary>
    public void Add(string name, IRecordStore? records = null, IQueryStore? query = null, IExportService? export = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (records is null && query is null && export is null)
            throw new ArgumentException($"data source '{name}' needs at least one store", nameof(name));
        lock (_gate) _sources[name] = new Source(records, query, export);
    }

    /// <summary>Removes a data source. · 移除数据源</summary>
    public bool Remove(string name)
    {
        lock (_gate) return _sources.Remove(name);
    }

    /// <inheritdoc />
    public IRecordStore? ResolveRecordStore(string source) => Find(source)?.Records;

    /// <inheritdoc />
    public IQueryStore? ResolveQueryStore(string source) => Find(source)?.Query;

    /// <inheritdoc />
    public IExportService? ResolveExportService(string source) => Find(source)?.Export;

    private Source? Find(string source)
    {
        if (string.IsNullOrEmpty(source)) return null;
        lock (_gate) return _sources.TryGetValue(source, out var s) ? s : null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Source[] all;
        lock (_gate)
        {
            all = _sources.Values.ToArray();
            _sources.Clear();
        }
        // A store may implement several roles (e.g. SqlStorage is both record + query); dispose once.
        // · 一个存储可能身兼多职(如 SqlStorage 同时是记录与查询存储)；只释放一次。
        var seen = new HashSet<IAsyncDisposable>(ReferenceEqualityComparer.Instance);
        foreach (var source in all)
            foreach (var disposable in new IAsyncDisposable?[] { source.Records, source.Query })
                if (disposable is not null && seen.Add(disposable))
                    await disposable.DisposeAsync().ConfigureAwait(false);
    }

    private sealed record Source(IRecordStore? Records, IQueryStore? Query, IExportService? Export);
}
