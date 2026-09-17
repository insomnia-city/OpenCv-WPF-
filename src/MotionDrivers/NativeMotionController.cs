using HalconWorkflow.Abstractions;
using HalconWorkflow.MotionDrivers.Native;

namespace HalconWorkflow.MotionDrivers;

/// <summary>
/// Vendor motion controller backed by a P/Invoked native SDK (Googol-first shim). The
/// constructor probes the native library and fails explicitly if it is absent; callers
/// should use <see cref="MotionDriverFactory"/> so the phantom fallback is chosen instead.
/// / 由 P/Invoke 原生 SDK 支撑的厂商运动控制器(固高优先 shim)。构造时探测原生库，缺失即显式失败；
///   调用方应经 MotionDriverFactory 以改用幻影回退。
/// </summary>
public sealed class NativeMotionController : IMotionController
{
    private readonly INativeMotionApi _api;
    private readonly object _gate = new();
    private readonly Dictionary<int, double> _target = [];
    private readonly Dictionary<int, ObservableBuffer<AxisState>> _watch = [];

    /// <summary>
    /// Resolves the vendor library and opens the card. Throws
    /// <see cref="NativeLibraryMissingException"/> when the SDK is absent (§6.3).
    /// / 解析厂商库并打开卡。SDK 缺失时抛 NativeLibraryMissingException（§6.3）。
    /// </summary>
    public NativeMotionController(string vendorId, int cardNo, string library)
    {
        var load = NativeMotionLibrary.Load(library);
        if (load.IsMissing) throw new NativeLibraryMissingException(library);
        VendorId = vendorId;
        CardNo = cardNo;
        Library = library;
        _api = new DllImportMotionApi();
        _api.Open(cardNo);
    }

    /// <summary>Test/DI seam: skips native loading and uses the supplied API. / 测试/DI 接缝：跳过原生加载并使用注入 API</summary>
    internal NativeMotionController(string vendorId, int cardNo, string library, INativeMotionApi api)
    {
        VendorId = vendorId;
        CardNo = cardNo;
        Library = library;
        _api = api;
        _api.Open(cardNo);
    }

    /// <inheritdoc />
    public string VendorId { get; }

    /// <inheritdoc />
    public int CardNo { get; }

    /// <summary>Resolved native library name. / 已解析的原生库名</summary>
    public string Library { get; }

    /// <inheritdoc />
    public async Task HomeAsync(int axis, CancellationToken ct)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        _api.Home(CardNo, axis);
        lock (_gate) _target[axis] = 0;
        await RaiseAsync(axis).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task MoveAbsoluteAsync(int axis, double pos, double vel, double acc, CancellationToken ct)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        _api.MoveAbsolute(CardNo, axis, pos, vel, acc);
        lock (_gate) _target[axis] = pos;
        await RaiseAsync(axis).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task MoveRelativeAsync(int axis, double dist, double vel, CancellationToken ct)
    {
        double current = await ReadPositionAsync(axis).ConfigureAwait(false);
        await MoveAbsoluteAsync(axis, current + dist, vel, vel, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task MoveLineAsync(int[] axes, double[] dest, double vel, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(axes);
        ArgumentNullException.ThrowIfNull(dest);
        if (axes.Length == 0 || axes.Length != dest.Length)
            throw new ArgumentException("axes and dest must be non-empty and equal length");
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        _api.MoveLine(CardNo, axes, dest, vel);
        lock (_gate)
        {
            for (int i = 0; i < axes.Length; i++) _target[axes[i]] = dest[i];
        }
        foreach (var axis in axes) await RaiseAsync(axis).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task WaitInPositionAsync(int axis, double tol, CancellationToken ct)
    {
        tol = Math.Abs(tol);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            double target;
            lock (_gate) target = _target.TryGetValue(axis, out var t) ? t : 0;
            double pos = await ReadPositionAsync(axis).ConfigureAwait(false);
            if (Math.Abs(pos - target) <= tol) return;
            await Task.Delay(1, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(StopMode mode)
    {
        _api.Stop(CardNo, (int)mode);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<double> ReadPositionAsync(int axis)
        => Task.FromResult(_api.ReadPosition(CardNo, axis));

    /// <inheritdoc />
    public Task SetDigitalOutAsync(int port, bool on)
    {
        _api.SetDigitalOut(CardNo, port, on ? 1 : 0);
        return Task.CompletedTask;
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
        try
        {
            _api.Close(CardNo);
        }
        catch
        {
            // best-effort close on dispose · 释放时尽力关闭
        }
        lock (_gate)
        {
            foreach (var b in _watch.Values) b.Done();
            _watch.Clear();
        }
        return ValueTask.CompletedTask;
    }

    private async Task RaiseAsync(int axis)
    {
        ObservableBuffer<AxisState>? buffer;
        lock (_gate) _watch.TryGetValue(axis, out buffer);
        if (buffer is null) return;
        double pos = await ReadPositionAsync(axis).ConfigureAwait(false);
        buffer.Push(new AxisState(axis, pos, 0, true, false, false));
    }
}
