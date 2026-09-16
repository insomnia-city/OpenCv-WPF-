namespace HalconWorkflow.Core.Contracts;

/// <summary>
/// Resource scope: registers disposables and releases them when scope exits. /* 资源作用域：登记可释放对象，出作用域统一释放 */
/// </summary>
public interface IScope : IDisposable
{
    /// <summary>
    /// Registers an object to be disposed on scope exit. /* 登记对象，作用域退出时释放 */
    /// </summary>
    void Register(IDisposable disposable);

    /// <summary>
    /// Registers a callback invoked on scope exit. /* 登记作用域退出时回调 */
    /// </summary>
    void Register(Action onExit);
}

/// <summary>
/// Default scope implementation. /* 默认作用域实现 */
/// </summary>
public sealed class Scope : IScope
{
    private readonly List<IDisposable> _disposables = [];
    private readonly List<Action> _callbacks = [];

    /// <inheritdoc />
    public void Register(IDisposable disposable)
    {
        ArgumentNullException.ThrowIfNull(disposable);
        lock (_disposables)
            _disposables.Add(disposable);
    }

    /// <inheritdoc />
    public void Register(Action onExit)
    {
        ArgumentNullException.ThrowIfNull(onExit);
        lock (_callbacks)
            _callbacks.Add(onExit);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Exception? first = null;
        IDisposable[] disposes;
        Action[] actions;
        lock (_disposables)
        {
            disposes = [.. _disposables];
            _disposables.Clear();
        }
        lock (_callbacks)
        {
            actions = [.. _callbacks];
            _callbacks.Clear();
        }

        // Release in registration order (LIFO would be safer for nesting; keep FIFO for determinism). /* 按登记顺序释放(嵌套场景 LIFO 更安全；此处用 FIFO 保证确定性) */
        foreach (var cb in actions)
        {
            try { cb(); }
            catch (Exception ex) { first ??= ex; }
        }
        foreach (var d in disposes)
        {
            try { d.Dispose(); }
            catch (Exception ex) { first ??= ex; }
        }
        if (first is not null) throw first;
    }
}