using HalconWorkflow.Abstractions;
using HalconWorkflow.Abstractions.Parameters;

namespace HalconWorkflow.Nodes.Vision.Commands;

/// <summary>
/// Undoable parameter write on an <see cref="IParameterized"/> node (§9.2 + §4.4).
/// First DoAsync snapshots the old value so Undo/Redo are exact.
/// / 针对 IParameterized 节点的可撤销参数写（§9.2 + §4.4）。
///   首次 DoAsync 快照旧值，保证 Undo/Redo 精确。
/// </summary>
public sealed class SetParameterCommand(IParameterized node, string name, object value) : IUndoableCommand
{
    private object? _old;
    private bool _materialized;

    public string Description => $"Set '{name}'";

    /// <summary>Name of the written parameter. · 写入的参数名</summary>
    public string ParameterName => name;

    /// <summary>Value before the first apply (materialized by the first DoAsync). · 首次应用前的值(首次 DoAsync 后可用)</summary>
    public object? BeforeValue => _old;

    /// <summary>Value applied by this command. · 本命令应用的值</summary>
    public object? AfterValue => value;

    public Task DoAsync(CancellationToken ct)
    {
        var (old, _) = ParameterReflection.Apply(node.ParameterObject, name, value);
        if (!_materialized)
        {
            _old = old;
            _materialized = true;
        }
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task UndoAsync(CancellationToken ct)
    {
        if (_materialized && _old is not null) ParameterReflection.Apply(node.ParameterObject, name, _old);
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task RedoAsync(CancellationToken ct) => DoAsync(ct);
}