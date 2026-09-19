using System.Diagnostics;
using HalconWorkflow.Abstractions;
using HalconWorkflow.Abstractions.Parameters;
using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Execution;
using HalconWorkflow.Core.Model;
using HalconWorkflow.Core.Types;
using HalconWorkflow.Nodes.Comm.Nodes;

namespace HalconWorkflow.Nodes.Comm;

/// <summary>
/// Strongly-typed parameter objects for comm nodes (§7.4).
/// / 通讯节点的强类型参数对象(§7.4)
/// </summary>
public sealed class CommReadParameters
{
    [NodeParameter("DeviceId", "Connection", description: "Target device id in the comm runtime · 运行时中的目标设备ID")]
    public string DeviceId { get; set; } = "";

    [NodeParameter("Tag", "Address", description: "Tag path resolved by the tag table · 由 Tag 表解析的路径")]
    public string Tag { get; set; } = "";

    [NodeParameter("TimeoutMs", "Connection", min: 1, max: 10000, unit: "ms", description: "Read timeout · 读取超时")]
    public int TimeoutMs { get; set; } = 1000;
}

/// <summary>comm.read:1 — Reads one tag value via the comm runtime. / 按运行时读取单个 Tag 值</summary>
internal sealed class CommReadNode(string id) : CommNodeBase(id, new NodeContract("comm.read", 1), execIn: true, execOut: true), IParameterized
{
    public CommReadParameters Params { get; } = new();
    object IParameterized.ParameterObject => Params;

    protected override async Task RunAsync(IExecutionContext ctx, CancellationToken ct)
    {
        var runtime = ctx.GetService<ICommRuntime>();
        var device = runtime.Resolve(Params.DeviceId) ?? throw new InvalidOperationException($"device '{Params.DeviceId}' not registered");
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(Params.TimeoutMs));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var value = await device.ReadAsync(Params.Tag, linked.Token).ConfigureAwait(false);
        ctx.SetData("Value", value);
    }
}

public sealed class CommWriteParameters
{
    [NodeParameter("DeviceId", "Connection", description: "Target device id · 目标设备ID")]
    public string DeviceId { get; set; } = "";

    [NodeParameter("Tag", "Address", description: "Tag path · Tag 路径")]
    public string Tag { get; set; } = "";

    [NodeParameter("ValueAsText", "Value", description: "Static value when input port is not connected · 输入端口未接时使用的静态值")]
    public string ValueAsText { get; set; } = "";
}

/// <summary>comm.write:1 — Writes a value to a tag. / 向 Tag 写入值</summary>
internal sealed class CommWriteNode(string id) : CommNodeBase(id, new NodeContract("comm.write", 1), execIn: true, execOut: true,
    ("Value", ResultDescriptor.Instance, true)), IParameterized
{
    public CommWriteParameters Params { get; } = new();
    object IParameterized.ParameterObject => Params;

    protected override async Task RunAsync(IExecutionContext ctx, CancellationToken ct)
    {
        var runtime = ctx.GetService<ICommRuntime>();
        var device = runtime.Resolve(Params.DeviceId) ?? throw new InvalidOperationException($"device '{Params.DeviceId}' not registered");
        object? value = ctx.GetData("Value");
        if (value is null) value = Params.ValueAsText;
        if (value is null)
            throw new InvalidOperationException("comm.write requires either connected 'Value' port or static ValueAsText");
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        await device.WriteAsync(Params.Tag, value, linked.Token).ConfigureAwait(false);
    }
}

public sealed class CommWaitParameters
{
    [NodeParameter("DeviceId", "Connection", description: "Target device id · 目标设备ID")]
    public string DeviceId { get; set; } = "";

    [NodeParameter("Tag", "Address", description: "Tag path · Tag 路径")]
    public string Tag { get; set; } = "";

    [NodeParameter("Expected", "Condition", description: "Expected value (string comparison) · 期望值(字符串比较)")]
    public string Expected { get; set; } = "";

    [NodeParameter("TimeoutMs", "Condition", min: 100, max: 60000, unit: "ms", description: "Poll timeout · 轮询超时")]
    public int TimeoutMs { get; set; } = 5000;

    [NodeParameter("PollMs", "Condition", min: 20, max: 5000, unit: "ms", description: "Poll interval · 轮询间隔")]
    public int PollMs { get; set; } = 100;

    [NodeParameter("Invert", "Condition", description: "Wait until value is NOT equal · 等待值不等")]
    public bool Invert { get; set; }
}

/// <summary>comm.wait:1 — Polls a tag until the expected value is reached (or timeout). / 轮询直到期望值到达(或超时)</summary>
internal sealed class CommWaitNode(string id) : CommNodeBase(id, new NodeContract("comm.wait", 1), execIn: true, execOut: true), IParameterized
{
    public CommWaitParameters Params { get; } = new();
    object IParameterized.ParameterObject => Params;

    protected override async Task RunAsync(IExecutionContext ctx, CancellationToken ct)
    {
        var runtime = ctx.GetService<ICommRuntime>();
        var device = runtime.Resolve(Params.DeviceId) ?? throw new InvalidOperationException($"device '{Params.DeviceId}' not registered");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(Params.TimeoutMs));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var token = linked.Token;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            object? value = null;
            try
            {
                value = await device.ReadAsync(Params.Tag, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* transient error; keep polling until deadline */ }
            string actual = Convert.ToString(value ?? "") ?? "";
            bool met = string.Equals(actual, Params.Expected, StringComparison.OrdinalIgnoreCase);
            if (Params.Invert) met = !met;
            if (met) { ctx.SetData("Value", value); return; }
            await Task.Delay(Params.PollMs, token).ConfigureAwait(false);
        }
    }
}

public sealed class TagTriggerParameters
{
    [NodeParameter("DeviceId", "Connection", description: "Target device id · 目标设备ID")]
    public string DeviceId { get; set; } = "";

    [NodeParameter("Tag", "Address", description: "Tag path subscribed for changes · 订阅其变化的 Tag 路径")]
    public string Tag { get; set; } = "";

    [NodeParameter("DebounceMs", "Trigger", min: 10, max: 60000, unit: "ms", description: "Edge debounce after each emitted pulse · 每个已发脉冲后的边沿去抖")]
    public int DebounceMs { get; set; } = 10;
}

/// <summary>
/// comm.tagtrigger:1 — Subscribes a tag change and re-arms the scheduler pulse; on each
/// cycle reports whether a change happened since the previous one. Pipeline-style engines
/// run every node each cycle, so downstream gating is driven by the <c>Triggered</c>
/// output, not by halting execution (§5.4). · 订阅 Tag 变化并重新武装调度脉冲;
/// 每个周期报告自上个周期以来是否发生变化。流水线引擎每轮都执行所有节点,
/// 因此下游门控由 Triggered 输出驱动而非中断执行(§5.4)。
/// </summary>
internal sealed class TagTriggerNode : CommNodeBase, IParameterized, IStoppableNode
{
    private readonly object _gate = new();
    private IDisposable? _sub;
    private ITriggerNudger? _nudger;
    private long _count;
    private int _pending;
    private object? _lastValue;
    private long _lastPulseTick;

    public TagTriggerNode(string id) : base(id, new NodeContract("comm.tagtrigger", 1), execIn: true, execOut: true)
    {
        AddDataOut("Triggered", BoolDescriptor.Instance);
        AddDataOut("Count", IntegerDescriptor.Instance);
        AddDataOut("Value", ResultDescriptor.Instance);
    }

    public TagTriggerParameters Params { get; } = new();
    object IParameterized.ParameterObject => Params;

    protected override Task RunAsync(IExecutionContext ctx, CancellationToken ct)
    {
        var runtime = ctx.GetService<ICommRuntime>();
        var device = runtime.Resolve(Params.DeviceId) ?? throw new InvalidOperationException($"device '{Params.DeviceId}' not registered");
        EnsureSubscribed(device, ctx.GetService<ITriggerNudger>());

        int changes;
        object? value;
        lock (_gate)
        {
            changes = _pending;
            _pending = 0;
            value = _lastValue;
            if (changes > 0)
            {
                _count++;
                _lastPulseTick = DateTimeOffset.UtcNow.Ticks;
            }
        }
        ctx.SetData("Triggered", changes > 0);
        ctx.SetData("Count", _count);
        ctx.SetData("Value", changes > 0 ? value : null);
        return Task.CompletedTask;
    }

    private void EnsureSubscribed(IDeviceConnection device, ITriggerNudger nudger)
    {
        lock (_gate)
        {
            _nudger = nudger;
            if (_sub is not null) return;
            _sub = device.Subscribe(Params.Tag).Subscribe(new ChangeObserver(OnChanged));
        }
    }

    private sealed class ChangeObserver(Action<Abstractions.TagValue> onNext) : IObserver<Abstractions.TagValue>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(Abstractions.TagValue value) => onNext(value);
    }

    private void OnChanged(Abstractions.TagValue value)
    {
        var now = DateTimeOffset.UtcNow.Ticks;
        var debounce = Math.Max(0, Params.DebounceMs) * System.TimeSpan.TicksPerMillisecond;
        ITriggerNudger? nudger;
        lock (_gate)
        {
            // Edge debounce: a change too close after the last emitted pulse is swallowed. · 边沿去抖：紧邻已发脉冲的变化被吞掉
            if (_lastPulseTick != 0 && now - _lastPulseTick < debounce) return;
            _pending++;
            _lastValue = value.Value;
            nudger = _nudger;
        }
        nudger?.Nudge(TriggerSource.Internal);
    }

    /// <inheritdoc />
    public Task OnSchedulerStopAsync(CancellationToken ct)
    {
        IDisposable? sub;
        lock (_gate)
        {
            sub = _sub;
            _sub = null;
            _pending = 0;
            _count = 0;
            _lastValue = null;
            _lastPulseTick = 0;
            _nudger = null;
        }
        sub?.Dispose();
        return Task.CompletedTask;
    }
}