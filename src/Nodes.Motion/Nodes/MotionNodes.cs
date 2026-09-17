using System.Globalization;
using HalconWorkflow.Abstractions;
using HalconWorkflow.Abstractions.Parameters;
using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Model;
using HalconWorkflow.Core.Types;
using HalconWorkflow.Nodes.Motion.Nodes;

namespace HalconWorkflow.Nodes.Motion;

/// <summary>
/// Strongly-typed parameter objects for motion nodes (§7.5). · 运动节点的强类型参数对象(§7.5)
/// </summary>
public sealed class MotionHomeParameters
{
    [NodeParameter("Controller", "Controller", description: "Registered controller name · 已登记控制器名")]
    public string Controller { get; set; } = "";

    [NodeParameter("Axis", "Controller", min: 0, max: 63, description: "Axis index · 轴号")]
    public int Axis { get; set; }

    [NodeParameter("TimeoutMs", "Controller", min: 1, max: 60000, unit: "ms", description: "Home timeout · 回零超时")]
    public int TimeoutMs { get; set; } = 5000;

    [NodeParameter("RollbackOnFault", "Safety", description: "Safe decel stop on fault · 故障时安全减速停车")]
    public bool RollbackOnFault { get; set; } = true;
}

/// <summary>motion.home:1 — Homes an axis. / 轴回零</summary>
internal sealed class MotionHomeNode(string id)
    : MotionNodeBase(id, new NodeContract("motion.home", 1), execIn: true, execOut: true), IParameterized
{
    public MotionHomeParameters Params { get; } = new();
    object IParameterized.ParameterObject => Params;

    protected override Task RunAsync(IExecutionContext ctx, CancellationToken ct)
    {
        var controller = MotionNode.Resolve(ctx, Params.Controller);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(Params.TimeoutMs));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        return WithRollbackAsync(controller, Params.RollbackOnFault, () => controller.HomeAsync(Params.Axis, linked.Token));
    }
}

public sealed class MotionMoveAbsParameters
{
    [NodeParameter("Controller", "Controller", description: "Registered controller name · 已登记控制器名")]
    public string Controller { get; set; } = "";

    [NodeParameter("Axis", "Controller", min: 0, max: 63, description: "Axis index · 轴号")]
    public int Axis { get; set; }

    [NodeParameter("Pos", "Motion", unit: "unit", description: "Target position (engineering units) · 目标位置(工程单位)")]
    public double Pos { get; set; }

    [NodeParameter("Vel", "Motion", min: 0.001, max: 100000, unit: "unit/s", description: "Velocity · 速度")]
    public double Vel { get; set; } = 100;

    [NodeParameter("Acc", "Motion", min: 0.001, max: 1000000, unit: "unit/s²", description: "Acceleration · 加速度")]
    public double Acc { get; set; } = 100;

    [NodeParameter("Scale", "Motion", min: 0.0001, max: 100000, description: "Native counts per engineering unit · 每工程单位原生计数")]
    public double Scale { get; set; } = 1;

    [NodeParameter("TimeoutMs", "Controller", min: 1, max: 120000, unit: "ms", description: "Command timeout · 命令超时")]
    public int TimeoutMs { get; set; } = 10000;

    [NodeParameter("RollbackOnFault", "Safety", description: "Safe decel stop on fault · 故障时安全减速停车")]
    public bool RollbackOnFault { get; set; } = true;
}

/// <summary>motion.moveAbs:1 — Absolute move; target may come from the "Pos" input. / 绝对定位；目标可取 "Pos" 输入</summary>
internal sealed class MotionMoveAbsNode(string id)
    : MotionNodeBase(id, new NodeContract("motion.moveAbs", 1), execIn: true, execOut: true,
        ("Pos", NumberDescriptor.Instance, true)), IParameterized
{
    public MotionMoveAbsParameters Params { get; } = new();
    object IParameterized.ParameterObject => Params;

    protected override Task RunAsync(IExecutionContext ctx, CancellationToken ct)
    {
        var controller = MotionNode.Resolve(ctx, Params.Controller);
        double engineering = MotionNode.ReadNumber(ctx, "Pos", Params.Pos);
        double native = MotionUnits.ToNative(engineering, Params.Scale);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(Params.TimeoutMs));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        return WithRollbackAsync(controller, Params.RollbackOnFault,
            () => controller.MoveAbsoluteAsync(Params.Axis, native, Params.Vel, Params.Acc, linked.Token));
    }
}

public sealed class MotionMoveRelParameters
{
    [NodeParameter("Controller", "Controller", description: "Registered controller name · 已登记控制器名")]
    public string Controller { get; set; } = "";

    [NodeParameter("Axis", "Controller", min: 0, max: 63, description: "Axis index · 轴号")]
    public int Axis { get; set; }

    [NodeParameter("Dist", "Motion", unit: "unit", description: "Relative distance (engineering units) · 相对距离(工程单位)")]
    public double Dist { get; set; }

    [NodeParameter("Vel", "Motion", min: 0.001, max: 100000, unit: "unit/s", description: "Velocity · 速度")]
    public double Vel { get; set; } = 100;

    [NodeParameter("Scale", "Motion", min: 0.0001, max: 100000, description: "Native counts per engineering unit · 每工程单位原生计数")]
    public double Scale { get; set; } = 1;

    [NodeParameter("TimeoutMs", "Controller", min: 1, max: 120000, unit: "ms", description: "Command timeout · 命令超时")]
    public int TimeoutMs { get; set; } = 10000;

    [NodeParameter("RollbackOnFault", "Safety", description: "Safe decel stop on fault · 故障时安全减速停车")]
    public bool RollbackOnFault { get; set; } = true;
}

/// <summary>motion.moveRel:1 — Relative move. / 相对移动</summary>
internal sealed class MotionMoveRelNode(string id)
    : MotionNodeBase(id, new NodeContract("motion.moveRel", 1), execIn: true, execOut: true,
        ("Dist", NumberDescriptor.Instance, true)), IParameterized
{
    public MotionMoveRelParameters Params { get; } = new();
    object IParameterized.ParameterObject => Params;

    protected override Task RunAsync(IExecutionContext ctx, CancellationToken ct)
    {
        var controller = MotionNode.Resolve(ctx, Params.Controller);
        double engineering = MotionNode.ReadNumber(ctx, "Dist", Params.Dist);
        double native = MotionUnits.ToNative(engineering, Params.Scale);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(Params.TimeoutMs));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        return WithRollbackAsync(controller, Params.RollbackOnFault,
            () => controller.MoveRelativeAsync(Params.Axis, native, Params.Vel, linked.Token));
    }
}

public sealed class MotionLineParameters
{
    [NodeParameter("Controller", "Controller", description: "Registered controller name · 已登记控制器名")]
    public string Controller { get; set; } = "";

    [NodeParameter("Axes", "Line", description: "Axis list, e.g. \"0,1\" · 轴号列表，如 \"0,1\"")]
    public string Axes { get; set; } = "0,1";

    [NodeParameter("Dest", "Line", description: "Destinations, e.g. \"10,20\" · 目标列表，如 \"10,20\"")]
    public string Dest { get; set; } = "0,0";

    [NodeParameter("Vel", "Line", min: 0.001, max: 100000, unit: "unit/s", description: "Interpolated velocity · 插补速度")]
    public double Vel { get; set; } = 100;

    [NodeParameter("Scale", "Line", min: 0.0001, max: 100000, description: "Native counts per engineering unit · 每工程单位原生计数")]
    public double Scale { get; set; } = 1;

    [NodeParameter("TimeoutMs", "Controller", min: 1, max: 120000, unit: "ms", description: "Command timeout · 命令超时")]
    public int TimeoutMs { get; set; } = 15000;

    [NodeParameter("RollbackOnFault", "Safety", description: "Safe decel stop on fault · 故障时安全减速停车")]
    public bool RollbackOnFault { get; set; } = true;
}

/// <summary>
/// motion.line:1 — Multi-axis coordinated move via a single interpolation command
/// (never split into per-axis moves; §7.5 number-one correctness rule).
/// / 多轴联动单命令插补(禁止拆成多次单轴移动；§7.5 头号正确性规则)。
/// </summary>
internal sealed class MotionLineNode(string id)
    : MotionNodeBase(id, new NodeContract("motion.line", 1), execIn: true, execOut: true), IParameterized
{
    public MotionLineParameters Params { get; } = new();
    object IParameterized.ParameterObject => Params;

    protected override Task RunAsync(IExecutionContext ctx, CancellationToken ct)
    {
        var controller = MotionNode.Resolve(ctx, Params.Controller);
        int[] axes = MotionUnits.ParseAxes(Params.Axes);
        double[] dest = MotionUnits.ParseValues(Params.Dest);
        if (axes.Length != dest.Length)
            throw new InvalidOperationException($"Axes ({axes.Length}) and Dest ({dest.Length}) must have equal length");
        for (int i = 0; i < dest.Length; i++) dest[i] = MotionUnits.ToNative(dest[i], Params.Scale);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(Params.TimeoutMs));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        return WithRollbackAsync(controller, Params.RollbackOnFault,
            () => controller.MoveLineAsync(axes, dest, Params.Vel, linked.Token));
    }
}

public sealed class MotionWaitInPosParameters
{
    [NodeParameter("Controller", "Controller", description: "Registered controller name · 已登记控制器名")]
    public string Controller { get; set; } = "";

    [NodeParameter("Axis", "Controller", min: 0, max: 63, description: "Axis index · 轴号")]
    public int Axis { get; set; }

    [NodeParameter("Tol", "Condition", min: 0, max: 100000, unit: "unit", description: "In-position tolerance · 到位公差")]
    public double Tol { get; set; } = 0.01;

    [NodeParameter("Scale", "Condition", min: 0.0001, max: 100000, description: "Native counts per engineering unit · 每工程单位原生计数")]
    public double Scale { get; set; } = 1;

    [NodeParameter("TimeoutMs", "Condition", min: 1, max: 120000, unit: "ms", description: "Wait timeout · 等待超时")]
    public int TimeoutMs { get; set; } = 5000;
}

/// <summary>
/// motion.waitInPos:1 — Cooperatively waits for in-position (ms-level soft trigger link),
/// then publishes the position. Cancellable / bounded by timeout. / 协作式等待到位(ms 级软触发链路)，
///   随后发布位置。可取消且有超时上界。
/// </summary>
internal sealed class MotionWaitInPosNode : MotionNodeBase, IParameterized
{
    public MotionWaitInPosNode(string id)
        : base(id, new NodeContract("motion.waitInPos", 1), execIn: true, execOut: true)
    {
        AddDataOut("Position", NumberDescriptor.Instance);
    }

    public MotionWaitInPosParameters Params { get; } = new();
    object IParameterized.ParameterObject => Params;

    protected override async Task RunAsync(IExecutionContext ctx, CancellationToken ct)
    {
        var controller = MotionNode.Resolve(ctx, Params.Controller);
        double nativeTol = MotionUnits.ToNative(Params.Tol, Params.Scale);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(Params.TimeoutMs));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        await controller.WaitInPositionAsync(Params.Axis, nativeTol, linked.Token).ConfigureAwait(false);
        double native = await controller.ReadPositionAsync(Params.Axis).ConfigureAwait(false);
        ctx.SetData("Position", MotionUnits.FromNative(native, Params.Scale));
    }
}

public sealed class MotionDoutParameters
{
    [NodeParameter("Controller", "Controller", description: "Registered controller name · 已登记控制器名")]
    public string Controller { get; set; } = "";

    [NodeParameter("Port", "IO", min: 0, max: 255, description: "Digital output port · 数字输出端口")]
    public int Port { get; set; }

    [NodeParameter("On", "IO", description: "Output level · 输出电平")]
    public bool On { get; set; } = true;
}

/// <summary>motion.dout:1 — Sets a motion-card digital output. / 设置运动卡数字输出</summary>
internal sealed class MotionDoutNode(string id)
    : MotionNodeBase(id, new NodeContract("motion.dout", 1), execIn: true, execOut: true), IParameterized
{
    public MotionDoutParameters Params { get; } = new();
    object IParameterized.ParameterObject => Params;

    protected override Task RunAsync(IExecutionContext ctx, CancellationToken ct)
    {
        var controller = MotionNode.Resolve(ctx, Params.Controller);
        return controller.SetDigitalOutAsync(Params.Port, Params.On);
    }
}

/// <summary>Helpers shared by motion node bodies. · 运动节点体共享的辅助方法</summary>
internal static class MotionNode
{
    public static IMotionController Resolve(IExecutionContext ctx, string name)
        => ctx.GetService<IMotionRuntime>().Resolve(name)
           ?? throw new InvalidOperationException($"motion controller '{name}' not registered");

    public static double ReadNumber(IExecutionContext ctx, string port, double fallback)
    {
        var value = ctx.GetData(port);
        if (value is null) return fallback;
        if (value is IConvertible) return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        throw new InvalidOperationException($"input '{port}' is not numeric (got {value.GetType().Name})");
    }
}
