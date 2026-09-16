using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Model;

namespace HalconWorkflow.Core.Execution;

/// <summary>
/// Default execution context: scoped data map + scope + event bus + signal. · 默认执行上下文：作用域数据表 + 资源作用域 + 事件总线 + 信号令牌
/// </summary>
public sealed class ExecutionContext : IExecutionContext
{
    private readonly Dictionary<string, object?> _data = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, object> _services;

    public ExecutionContext(Signal current, IScope scope, IEventBus events, Dictionary<Type, object>? services = null)
    {
        Current = current;
        Scope = scope;
        Events = events;
        _services = services ?? new Dictionary<Type, object>();
    }

    /// <inheritdoc />
    public Signal Current { get; }

    /// <inheritdoc />
    public IScope Scope { get; }

    /// <inheritdoc />
    public IEventBus Events { get; }

    /// <inheritdoc />
    public object? GetData(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return _data.TryGetValue(tag, out var v) ? v : null;
    }

    /// <inheritdoc />
    public void SetData(string tag, object? value)
    {
        ArgumentNullException.ThrowIfNull(tag);
        _data[tag] = value;
    }

    /// <inheritdoc />
    public T GetService<T>() where T : class
        => _services.TryGetValue(typeof(T), out var s) ? (T)s : throw new InvalidOperationException($"Service '{typeof(T).Name}' is not registered.");
}