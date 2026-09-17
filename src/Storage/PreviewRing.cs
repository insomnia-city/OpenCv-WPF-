using System.Collections.Concurrent;

namespace HalconWorkflow.Storage;

/// <summary>
/// One preview frame captured off a node's output (§9.5.1). Image bytes are optional so a frame
/// may carry only a textual summary. · 节点输出上抓取的单帧预览(§9.5.1)。图像字节可选，故一帧可只带
/// 文本摘要。
/// </summary>
public sealed record PreviewFrame(string Node, DateTimeOffset CapturedAt, byte[]? Image = null, string? Summary = null);

/// <summary>
/// Bounded in-memory ring of the most recent preview frames per node (§9.5.1). Publishing is
/// lock-light and never blocks the cycle; the UI reads a snapshot and renders off-thread.
/// · 每节点最近预览帧的有界内存环(§9.5.1)。发布轻锁、绝不阻塞周期；UI 读快照并在后台渲染。
/// </summary>
public sealed class PreviewRing
{
    private readonly ConcurrentDictionary<string, NodeRing> _rings = new(StringComparer.Ordinal);

    public PreviewRing(int capacity = 8) => Capacity = Math.Max(1, capacity);

    /// <summary>Frames retained per node. · 每节点保留帧数</summary>
    public int Capacity { get; }

    /// <summary>When false, <see cref="Publish"/> is a no-op (preview disabled for throughput). · 为 false 时 Publish 空操作</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Nodes that currently hold at least one frame. · 当前至少持有一帧的节点</summary>
    public IReadOnlyList<string> Nodes => _rings.Keys.ToList();

    /// <summary>Publishes a frame, evicting the oldest once capacity is reached. · 发布一帧，达到容量即淘汰最旧</summary>
    public void Publish(PreviewFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!Enabled) return;
        _rings.GetOrAdd(frame.Node, _ => new NodeRing(Capacity)).Add(frame);
    }

    /// <summary>Most recent frame for a node, or null. · 节点最近一帧，无则 null</summary>
    public PreviewFrame? Latest(string node) =>
        _rings.TryGetValue(node, out var ring) ? ring.Snapshot(1).FirstOrDefault() : null;

    /// <summary>Most recent frames for a node, newest first. · 节点最近若干帧，新→旧</summary>
    public IReadOnlyList<PreviewFrame> Recent(string node, int count) =>
        _rings.TryGetValue(node, out var ring) ? ring.Snapshot(count) : [];

    /// <summary>Drops all rings. · 清空所有环</summary>
    public void Clear() => _rings.Clear();

    private sealed class NodeRing(int capacity)
    {
        private readonly object _gate = new();
        private readonly Queue<PreviewFrame> _frames = new();

        public void Add(PreviewFrame frame)
        {
            lock (_gate)
            {
                _frames.Enqueue(frame);
                while (_frames.Count > capacity) _frames.Dequeue();
            }
        }

        public IReadOnlyList<PreviewFrame> Snapshot(int count)
        {
            if (count <= 0) return [];
            lock (_gate)
            {
                return _frames.Reverse().Take(count).ToList();
            }
        }
    }
}
