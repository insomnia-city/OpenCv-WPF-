using HalconWorkflow.Abstractions;

namespace HalconWorkflow.Protocols;

/// <summary>
/// Minimal ordered single-subscriber observable buffer used by subscription
/// (change-detection) implementations such as <see cref="Modbus.ModbusTcpConnection.Subscribe"/>
/// and <see cref="S7.S7TcpConnection.Subscribe"/>. Values pushed before Subscribe are
/// replayed on subscription; a single active observer receives live pushes; Done marks
/// the end of the stream (unsubscribe/dispose). · 极简有序的单订阅缓冲,供订阅(变化检测)
///   实现(如 ModbusTcpConnection.Subscribe 与 S7TcpConnection.Subscribe)使用。
///   订阅前推送的值会在订阅时回放;唯一活跃观察者接收实时推送;Done 标记流结束(退订/释放)。
/// </summary>
internal sealed class SubjectBuffer : IObservable<TagValue>, IDisposable
{
    private readonly List<TagValue> _pending = [];
    private IObserver<TagValue>? _observer;
    private bool _done;

    public IDisposable Subscribe(IObserver<TagValue> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_pending)
        {
            _observer ??= observer;
            foreach (var v in _pending) _observer.OnNext(v);
            _pending.Clear();
        }
        return this;
    }

    public void Push(TagValue v)
    {
        lock (_pending)
        {
            if (_observer is { } o && !_done) o.OnNext(v);
            else _pending.Add(v);
        }
    }

    public void Done()
    {
        lock (_pending)
        {
            if (_observer is { } o && !_done) o.OnCompleted();
            _done = true;
        }
    }

    public void Dispose() { }
}