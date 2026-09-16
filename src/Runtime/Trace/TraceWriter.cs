using System.Text.Json;
using System.Text.Json.Nodes;
using HalconWorkflow.Core.Execution;
using HalconWorkflow.Core.Model;

namespace HalconWorkflow.Runtime.Trace;

/// <summary>
/// One cycle record matching the §8 cycle_records shape (prototype). 
/// 一条周期记录，对齐 §8 cycle_records 结构(原型)
/// </summary>
public sealed record CycleRecord(
    string TriggerId,
    string? Batch,
    string Node,
    string Kind,             // ok / ng / measure / trace · 结果类别
    string? ResultJson,
    string? ImageRef,
    DateTimeOffset Ts)
{
    /// <summary>
    /// Serializes to a JSON object for file output. · 序列化为 JSON 对象用于文件输出
    /// </summary>
    public JsonObject ToJson() => new()
    {
        ["trigger_id"] = TriggerId,
        ["batch"] = Batch,
        ["node"] = Node,
        ["kind"] = Kind,
        ["result_json"] = ResultJson,
        ["image_ref"] = ImageRef,
        ["ts"] = Ts.ToString("O")
    };
}

/// <summary>
/// Writes JSONL trace lines to a file; the run loop feeds node events into it. 
/// 将 JSONL 追溯行写入文件;运行循环把节点事件喂进来
/// </summary>
public sealed class TraceWriter : IDisposable
{
    private readonly TextWriter _writer;
    private readonly object _gate = new();
    private bool _disposed;

    public TraceWriter(TextWriter writer) => _writer = writer;

    /// <summary>
    /// Builds a trace writer over a file path (UTF-8). · 在文件路径上构建追溯写入器(UTF-8)
    /// </summary>
    public static TraceWriter Open(string path)
    {
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        return new TraceWriter(writer);
    }

    /// <summary>
    /// Wraps a completed node event into a cycle record and writes it. 
    /// 将完成的节点事件包装成周期记录并写入
    /// </summary>
    public void WriteNodeEvent(NodeExecutionEvent e, Signal signal, string? batch, string? resultJson)
    {
        if (_disposed) return;
        var kind = e.Phase switch
        {
            NodeExecutionPhase.Completed => "trace",
            NodeExecutionPhase.Faulted => "ng",
            NodeExecutionPhase.Cancelled => "ng",
            _ => "trace"
        };
        var record = new CycleRecord(
            signal.TriggerId, batch, e.NodeId, kind, resultJson, null, DateTimeOffset.UtcNow);
        lock (_gate)
            _writer.WriteLine(record.ToJson().ToJsonString());
    }

    /// <summary>
    /// Emits a cycle summary line (ok/ng decision recorded as-is). · 输出周期汇总行
    /// </summary>
    public void WriteCycle(Signal signal, string? batch, GraphRunResult result)
    {
        if (_disposed) return;
        var record = new CycleRecord(
            signal.TriggerId, batch, "*cycle", result.Success ? "ok" : "ng",
            $"{{\"succeeded\":{result.SucceededNodes},\"faulted\":{result.FaultedNodes},\"ms\":{(long)result.Duration.TotalMilliseconds}}}",
            null, DateTimeOffset.UtcNow);
        lock (_gate)
            _writer.WriteLine(record.ToJson().ToJsonString());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
            _writer.Flush();
        _writer.Dispose();
    }
}