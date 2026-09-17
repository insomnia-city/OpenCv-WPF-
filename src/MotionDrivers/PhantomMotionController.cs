using HalconWorkflow.Abstractions;

namespace HalconWorkflow.MotionDrivers;

/// <summary>
/// Deterministic pure-.NET motion controller used as the §6.3 software fallback and by
/// the stage-7 tests. Axes integrate instantly (configurable 1 ms submit delay); every
/// semantic action is recorded in <see cref="CommandLog"/> for assertions.
/// / 确定性纯 .NET 运动控制器，用作 §6.3 软件回退与阶段 7 测试。轴瞬时到位(可配置 1ms 提交延时)；
///   每个语义动作都记入 CommandLog 以便断言。
/// </summary>
public sealed class PhantomMotionController : IMotionController
{
    private readonly object _gate = new();
    private readonly Dictionary<int, Axis> _axes = [];
    private readonly Dictionary<int, bool> _dout = [];
    private readonly Dictionary<int, ObservableBuffer<AxisState>> _watch = [];
    private readonly List<StopMode> _stopLog = [];
    private readonly List<string> _commandLog = [];

    public PhantomMotionController(string vendorId = "phantom", int cardNo = 0)
    {
        VendorId = vendorId;
        CardNo = cardNo;
    }

    /// <inheritdoc />
    public string VendorId { get; }

    /// <inheritdoc />
    public int CardNo { get; }

    /// <summary>Submit delay applied by each motion command. / 每个运动命令的提交延时</summary>
    public TimeSpan MoveDelay { get; set; } = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// When false, a move never reports in-position, which lets tests exercise
    /// <see cref="WaitInPositionAsync"/> cancellation/timeout deterministically.
    /// / 为 false 时移动永不报到位，便于测试确定性地验证 WaitInPosition 的取消/超时。
    /// </summary>
    public bool HoldInPosition { get; set; } = true;

    /// <summary>Recorded stop modes, in order. / 记录停止模式(按顺序)</summary>
    public IReadOnlyList<StopMode> StopLog
    {
        get { lock (_gate) return _stopLog.ToArray(); }
    }

    /// <summary>Recorded semantic commands, in order. / 记录的语义命令(按顺序)</summary>
    public IReadOnlyList<string> CommandLog
    {
        get { lock (_gate) return _commandLog.ToArray(); }
    }

    /// <summary>Seed an axis position without issuing a command. / 不经命令直接播种轴位置</summary>
    public void SeedPosition(int axis, double position)
    {
        lock (_gate)
        {
            var a = Get(axis);
            a.Position = position;
            a.Target = position;
        }
    }

    /// <summary>Raise an alarm on an axis (test helper for fault handling). / 在轴上置报警(故障处理测试辅助)</summary>
    public void RaiseAlarm(int axis)
    {
        lock (_gate) Get(axis).Alarm = true;
        Raise(axis);
    }

    /// <inheritdoc />
    public async Task HomeAsync(int axis, CancellationToken ct)
    {
        await Task.Delay(MoveDelay, ct).ConfigureAwait(false);
        lock (_gate)
        {
            var a = Get(axis);
            a.Position = 0;
            a.Target = 0;
            a.Velocity = 0;
            a.InPosition = true;
            a.Busy = false;
            a.Alarm = false;
            _commandLog.Add($"home:{axis}");
        }
        Raise(axis);
    }

    /// <inheritdoc />
    public async Task MoveAbsoluteAsync(int axis, double pos, double vel, double acc, CancellationToken ct)
    {
        Validate(pos, vel);
        await Task.Delay(MoveDelay, ct).ConfigureAwait(false);
        lock (_gate)
        {
            var a = Get(axis);
            a.Position = pos;
            a.Target = pos;
            a.Velocity = 0;
            a.InPosition = HoldInPosition;
            a.Busy = !HoldInPosition;
            _commandLog.Add($"moveAbs:{axis}->{pos:G}");
        }
        Raise(axis);
    }

    /// <inheritdoc />
    public async Task MoveRelativeAsync(int axis, double dist, double vel, CancellationToken ct)
    {
        Validate(dist, vel);
        double current;
        lock (_gate) current = Get(axis).Position;
        await MoveAbsoluteAsync(axis, current + dist, vel, vel, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task MoveLineAsync(int[] axes, double[] dest, double vel, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(axes);
        ArgumentNullException.ThrowIfNull(dest);
        if (axes.Length == 0 || axes.Length != dest.Length)
            throw new ArgumentException("axes and dest must be non-empty and equal length");
        Validate(0, vel);
        await Task.Delay(MoveDelay, ct).ConfigureAwait(false);
        lock (_gate)
        {
            for (int i = 0; i < axes.Length; i++)
            {
                var a = Get(axes[i]);
                a.Position = dest[i];
                a.Target = dest[i];
                a.Velocity = 0;
                a.InPosition = HoldInPosition;
                a.Busy = !HoldInPosition;
            }
            _commandLog.Add($"line:[{string.Join(",", axes)}]->[{string.Join(",", dest.Select(d => d.ToString("G")))}]");
        }
        foreach (var axis in axes) Raise(axis);
    }

    /// <inheritdoc />
    public async Task WaitInPositionAsync(int axis, double tol, CancellationToken ct)
    {
        tol = Math.Abs(tol);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            bool ok;
            bool alarm;
            lock (_gate)
            {
                var a = Get(axis);
                alarm = a.Alarm;
                ok = a.InPosition && Math.Abs(a.Position - a.Target) <= tol;
            }
            if (alarm) throw new InvalidOperationException($"axis {axis} is in alarm");
            if (ok) return;
            await Task.Delay(1, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(StopMode mode)
    {
        lock (_gate)
        {
            _stopLog.Add(mode);
            _commandLog.Add($"stop:{mode}");
            foreach (var a in _axes.Values)
            {
                a.Velocity = 0;
                a.Busy = false;
                a.Target = a.Position;
                a.InPosition = true;
                if (mode == StopMode.EStop) a.Alarm = false;
            }
        }
        foreach (var axis in _axes.Keys.ToArray()) Raise(axis);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<double> ReadPositionAsync(int axis)
    {
        lock (_gate) return Task.FromResult(Get(axis).Position);
    }

    /// <inheritdoc />
    public Task SetDigitalOutAsync(int port, bool on)
    {
        lock (_gate) _dout[port] = on;
        return Task.CompletedTask;
    }

    /// <summary>Reads a digital output for assertions. / 读取数字输出以便断言</summary>
    public bool ReadDigitalOut(int port)
    {
        lock (_gate) return _dout.TryGetValue(port, out var v) && v;
    }

    /// <inheritdoc />
    public IObservable<AxisState> Watch(int axis)
    {
        lock (_gate)
        {
            if (!_watch.TryGetValue(axis, out var buffer))
            {
                buffer = new ObservableBuffer<AxisState>();
                _watch[axis] = buffer;
            }
            return buffer;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            foreach (var b in _watch.Values) b.Done();
            _watch.Clear();
        }
        return ValueTask.CompletedTask;
    }

    private Axis Get(int axis)
    {
        if (!_axes.TryGetValue(axis, out var a))
        {
            a = new Axis();
            _axes[axis] = a;
        }
        return a;
    }

    private void Raise(int axis)
    {
        ObservableBuffer<AxisState>? buffer;
        AxisState state;
        lock (_gate)
        {
            if (!_watch.TryGetValue(axis, out buffer)) return;
            state = Snapshot(axis, Get(axis));
        }
        buffer.Push(state);
    }

    private static AxisState Snapshot(int axis, Axis a)
        => new(axis, a.Position, a.Velocity, a.InPosition, a.Busy, a.Alarm);

    private static void Validate(double value, double vel)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new ArgumentOutOfRangeException(nameof(value), "value must be finite");
        if (double.IsNaN(vel) || double.IsInfinity(vel) || vel <= 0)
            throw new ArgumentOutOfRangeException(nameof(vel), "velocity must be positive and finite");
    }

    private sealed class Axis
    {
        public double Position;
        public double Target;
        public double Velocity;
        public bool InPosition = true;
        public bool Busy;
        public bool Alarm;
    }
}
