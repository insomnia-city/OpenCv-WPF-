using HalconWorkflow.Abstractions;

namespace HalconWorkflow.Protocols;

/// <summary>
/// Thread-safe in-memory tag table (§7.1). Registration is rare (recipe load,
/// device bring-up) while resolve/all happen on the graph fast path.
/// / 线程安全的内存 Tag 表（§7.1）。登记稀疏(换型/设备上电)，解析/枚举在图热路径上。
/// </summary>
public sealed class TagTable : ITagTable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TagTableEntry> _map = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public TagTableEntry? Resolve(string tag)
    {
        lock (_gate) return _map.TryGetValue(tag, out var e) ? e : null;
    }

    /// <inheritdoc />
    public void Register(TagTableEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate) _map[entry.Tag] = entry;
    }

    /// <inheritdoc />
    public IEnumerable<TagTableEntry> All()
    {
        lock (_gate) return _map.Values.ToList();
    }
}