namespace HalconWorkflow.MotionDrivers;

/// <summary>
/// Minimal ordered observable buffer used by <see cref="PhantomMotionController.Watch"/>.
/// Values pushed before a subscriber attaches are replayed on subscribe. 
/// / 幻影控制器 Watch 使用的极简有序可观察缓冲；订阅前推入的值在订阅时回放。
/// </summary>
internal sealed class ObservableBuffer<T> : IObservable<T>
{
    private readonly object _gate = new();
    private readonly List<T> _pending = [];
    private readonly List<IObserver<T>> _observers = [];
    private bool _done;

    public IDisposable Subscribe(IObserver<T> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate)
        {
            _observers.Add(observer);
            foreach (var v in _pending) observer.OnNext(v);
            _pending.Clear();
        }
        return new Subscription(this, observer);
    }

    public void Push(T value)
    {
        lock (_gate)
        {
            if (_done) return;
            if (_observers.Count == 0)
            {
                _pending.Add(value);
                return;
            }
            foreach (var o in _observers) o.OnNext(value);
        }
    }

    public void Done()
    {
        lock (_gate)
        {
            if (_done) return;
            _done = true;
            foreach (var o in _observers) o.OnCompleted();
            _observers.Clear();
        }
    }

    private void Remove(IObserver<T> observer)
    {
        lock (_gate) _observers.Remove(observer);
    }

    private sealed class Subscription(ObservableBuffer<T> owner, IObserver<T> observer) : IDisposable
    {
        public void Dispose() => owner.Remove(observer);
    }
}
