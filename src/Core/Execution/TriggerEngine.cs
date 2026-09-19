using HalconWorkflow.Core.Graph;
using HalconWorkflow.Core.Model;

namespace HalconWorkflow.Core.Execution;

/// <summary>
/// Adapter seam so the trigger engine stays free of comm/protocol types: the host
/// maps a tag pattern to a source that pushes values when the device reports a change.
/// · 触发引擎与通讯/协议类型解耦的适配缝：宿主把 Tag 模式映射为设备变化时推送值的源。
/// </summary>
public interface ITagObserverSource
{
    /// <summary>
    /// Subscribes; <paramref name="onNext"/> fires boxed tag values. Returns the detach handle. 
    /// · 订阅;onNext 以 object 推送 Tag 值。返回解绑句柄
    /// </summary>
    IDisposable Subscribe(Action<object?> onNext);
}

/// <summary>
/// Host trigger plane (§5.4): turns an enabled TriggerConfig into scheduler pulses.
/// Timer -> periodic <see cref="TriggerSource.Timer"/> pulses; TagChange -> one
/// <see cref="TriggerSource.TagChange"/> per pushed change (coalesced downstream by the
/// scheduler debounce window). Manual / disabled configs start idle; the manual first kick
/// stays the caller's job. Pure emitter: knows nothing about the scheduler itself.
/// · 宿主触发平面(§5.4)：把已启用的 TriggerConfig 变成调度脉冲。Timer 周期触发;
///   TagChange 每次推送触发一次(下游由调度器去抖窗口合并)。手动/禁用则保持空闲,
///   手动的首次踢动仍是调用方职责。纯发射器：自身不感知调度器。
/// </summary>
public sealed class TriggerEngine
{
    private readonly TriggerConfig _config;
    private readonly Action<TriggerSource> _emit;
    private readonly Func<string, ITagObserverSource?>? _tagSources;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _timerTask;
    private IDisposable? _tagSubscription;
    private string? _error;
    private long _tagPulses;
    private long _timerPulses;

    /// <param name="config">Graph trigger configuration. · 图的触发配置</param>
    /// <param name="emit">Pulse sink, usually <c>scheduler.Trigger</c>. · 脉冲出口，通常为 scheduler.Trigger</param>
    /// <param name="tagSources">
    /// Tag-pattern → change source resolver used when <c>config.Source == TagChange</c>;
    /// null / an unresolved pattern leaves the source idle. · Tag 模式→变化源解析器(Source==TagChange 时使用);
    /// 空或解析失败则源保持空闲
    /// </param>
    public TriggerEngine(TriggerConfig config, Action<TriggerSource> emit, Func<string, ITagObserverSource?>? tagSources = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _emit = emit ?? throw new ArgumentNullException(nameof(emit));
        _tagSources = tagSources;
    }

    /// <summary>Timer pulses emitted since Start. · 本次启动后已发出的定时脉冲数</summary>
    public long TimerPulses => Interlocked.Read(ref _timerPulses);

    /// <summary>Tag-change pulses emitted since Start. · 本次启动后已发出的 Tag 变化脉冲数</summary>
    public long TagPulses => Interlocked.Read(ref _tagPulses);

    /// <summary>
    /// Reason the engine stayed idle when the config asked for an external source (e.g. no
    /// resolver / unresolved tag pattern); null when all is well. · 外部源未能启动的原因;
    /// 无异常时为 null
    /// </summary>
    public string? Error => _error;

    /// <summary>
    /// Starts background sources per config. Cheap to call; Manual/disabled configs return immediately. 
    /// · 按配置启动后台源。手动/禁用立即返回;重复调用无副作用
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_cts is not null) return;
            if (!_config.Enabled) return;

            switch (_config.Source)
            {
                case TriggerSource.Timer:
                {
                    var cts = new CancellationTokenSource();
                    _cts = cts;
                    _timerTask = Task.Run(() => TimerLoopAsync(cts.Token));
                    break;
                }
                case TriggerSource.TagChange when _config.Tag is not null && _tagSources is not null:
                {
                    var source = _tagSources(_config.Tag);
                    if (source is null)
                    {
                        _error = $"No tag source for '{_config.Tag}'.";
                        return;
                    }
                    _tagSubscription = source.Subscribe(_ =>
                    {
                        Interlocked.Increment(ref _tagPulses);
                        _emit(TriggerSource.TagChange);
                    });
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Stops timer/tag sources and detaches the subscription. Idempotent. 
    /// · 停止定时/Tag 源并解绑订阅;幂等
    /// </summary>
    public async Task StopAsync()
    {
        Task? timer;
        IDisposable? sub;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            sub = _tagSubscription;
            timer = _timerTask;
            _cts = null;
            _tagSubscription = null;
            _timerTask = null;
        }
        sub?.Dispose();
        cts?.Cancel();
        if (timer is not null)
        {
            try { await timer.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        cts?.Dispose();
    }

    private async Task TimerLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(1, _config.IntervalMs)));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _timerPulses);
                _emit(TriggerSource.Timer);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop path. · 正常停止路径
        }
    }
}