using System.Diagnostics;
using HalconWorkflow.Abstractions;
using HalconWorkflow.Abstractions.Parameters;
using HalconWorkflow.Core.Contracts;
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