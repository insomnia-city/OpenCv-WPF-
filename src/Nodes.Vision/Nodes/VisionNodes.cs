using HalconWorkflow.Abstractions.Parameters;
using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Model;
using HalconWorkflow.Core.Types;
using HalconWorkflow.Nodes.Vision.Engines;
using HalconWorkflow.Nodes.Vision.Imaging;

namespace HalconWorkflow.Nodes.Vision.Nodes;

// Strongly-typed parameter objects: properties drive the reflected property panel (§4.4/§6.2). 
// / 强类型参数对象：属性驱动反射属性面板（§4.4/§6.2）

/// <summary>Grab source parameters (acquisition geometry). / 采集源参数（图像几何）</summary>
public sealed class GrabParameters
{
    [NodeParameter("Width", "Source", 16, 8192, "px")]
    public int Width { get; set; } = 320;

    [NodeParameter("Height", "Source", 16, 8192, "px")]
    public int Height { get; set; } = 240;
}

/// <summary>Threshold binarization parameters. / 阈值二值化参数</summary>
public sealed class ThresholdParameters
{
    [NodeParameter("Min", "Threshold", 0, 255)]
    public int Min { get; set; } = 128;

    [NodeParameter("Max", "Threshold", 0, 255)]
    public int Max { get; set; } = 255;
}

/// <summary>Generic .hdev procedure parameters. / 通用 .hdev 脚本参数</summary>
public sealed class HdevParameters
{
    [NodeParameter("Procedure", "Script", description: "Procedure name resolved by the engine registry. / 由引擎注册表解析的过程名")]
    public string Procedure { get; set; } = "simulate_probe";
}

/// <summary>
/// Measurement result flowing on a ResultDescriptor port. / 在 Result 端口上流转的测量结果
/// </summary>
public sealed record MeasurementResult(double Distance, int Edges)
{
    public override string ToString() => $"Distance={Distance} Edges={Edges}";
}

/// <summary>
/// vision.grab — acquires a frame from the pool's engine and outputs it as "image".
/// / vision.grab — 从引擎池借引擎采集一帧，输出到 "image"
/// </summary>
internal sealed class GrabNode : VisionNodeBase, IParameterized
{
    public GrabParameters Params { get; } = new();

    public GrabNode(string id) : base(id, new NodeContract("vision.grab", 1), execIn: true, execOut: true)
    {
        AddDataOut("image", ImageDescriptor.Instance);
    }

    public object ParameterObject => Params;

    protected override async Task RunAsync(IExecutionContext ctx, IVisionEngine engine, CancellationToken ct)
    {
        var args = new Dictionary<string, object> { ["width"] = Params.Width, ["height"] = Params.Height };
        var frame = (VisionFrame)await engine.ExecuteAsync("grab", null, args, ct);
        ctx.SetData("image", frame);
    }
}

/// <summary>
/// vision.threshold:v2 — grayscale binarization producing a region mask on "region".
/// v1 is reserved to the legacy scaffold nodes (handle-based) so old graphs keep loading (§12 migration).
/// / vision.threshold:v2 — 灰度二值化，输出区域掩膜到 "region"。
///   v1 保留给旧脚手架节点（句柄式），保证旧图可继续载入（§12 迁移）。
/// </summary>
internal sealed class ThresholdNode : VisionNodeBase, IParameterized
{
    public ThresholdParameters Params { get; } = new();

    public ThresholdNode(string id) : base(id, new NodeContract("vision.threshold", 2),
        execIn: true, execOut: true,
        ("image", ImageDescriptor.Instance, false))
    {
        AddDataOut("region", RegionDescriptor.Instance);
    }

    public object ParameterObject => Params;

    protected override async Task RunAsync(IExecutionContext ctx, IVisionEngine engine, CancellationToken ct)
    {
        var image = ctx.GetData("image") as VisionFrame;
        var args = new Dictionary<string, object> { ["min"] = Params.Min, ["max"] = Params.Max };
        var mask = (VisionFrame)await engine.ExecuteAsync("threshold", image, args, ct);
        ctx.SetData("region", mask);
    }
}

/// <summary>
/// vision.measure — edge-span measurement along the middle row; emits a MeasurementResult on "result".
/// / vision.measure — 中间行沿线的边沿跨度测量;输出 MeasurementResult 到 "result"
/// </summary>
internal sealed class MeasureNode : VisionNodeBase
{
    public MeasureNode(string id) : base(id, new NodeContract("vision.measure", 1),
        execIn: true, execOut: true,
        ("image", ImageDescriptor.Instance, false))
    {
        AddDataOut("result", ResultDescriptor.Instance);
    }

    protected override async Task RunAsync(IExecutionContext ctx, IVisionEngine engine, CancellationToken ct)
    {
        var image = ctx.GetData("image") as VisionFrame;
        var dict = (Dictionary<string, object>)await engine.ExecuteAsync("measure", image, null, ct);
        var dist = dict.TryGetValue("Distance", out var d) && d is IConvertible c ? c.ToDouble(null) : 0.0;
        var edges = dict.TryGetValue("Edges", out var e) && e is IConvertible e2 ? e2.ToInt32(null) : 0;
        ctx.SetData("result", new MeasurementResult(dist, edges));
    }
}

/// <summary>
/// vision.hdev — generic HDevProcedure node; in fallback mode it resolves procedures
/// from the engine registry, on a deployed Halcon host it loads .hdev procedures (§6.1).
/// / vision.hdev — 通用 HDevProcedure 节点;回退模式由引擎注册表解析过程,部署到 Halcon 主机则加载 .hdev（§6.1）
/// </summary>
internal sealed class HdevNode : VisionNodeBase, IParameterized
{
    public HdevParameters Params { get; } = new();

    public HdevNode(string id) : base(id, new NodeContract("vision.hdev", 1),
        execIn: true, execOut: true,
        ("image", ImageDescriptor.Instance, true))
    {
        AddDataOut("image", ImageDescriptor.Instance);
    }

    public object ParameterObject => Params;

    protected override async Task RunAsync(IExecutionContext ctx, IVisionEngine engine, CancellationToken ct)
    {
        var image = ctx.GetData("image") as VisionFrame;
        var args = new Dictionary<string, object> { ["procedure"] = Params.Procedure };
        var frame = (VisionFrame)await engine.ExecuteAsync("hdev", image, args, ct);
        ctx.SetData("image", frame);
    }
}

/// <summary>
/// vision.tomat — bridge HObject→Mat, flipping frame provenance without touching pixels (§6.4). 
/// Emits the Mat-provenance frame on the "image" output tag.
/// / vision.tomat — HObject→Mat 桥,只翻转帧来源域、不改像素（§6.4）。Mat 域帧输出到 "image"
/// </summary>
internal sealed class ToMatNode : VisionNodeBase
{
    public ToMatNode(string id) : base(id, new NodeContract("vision.tomat", 1),
        execIn: true, execOut: true,
        ("image", ImageDescriptor.Instance, false))
    {
        AddDataOut("image", ImageDescriptor.Instance);
    }

    protected override Task RunAsync(IExecutionContext ctx, IVisionEngine engine, CancellationToken ct)
    {
        var frame = ctx.GetData("image") as VisionFrame
            ?? throw new InvalidOperationException("tomat expects a VisionFrame on 'image'");
        ctx.SetData("image", FrameBridge.ToMat(frame));
        return Task.CompletedTask;
    }
}

/// <summary>
/// vision.tohobject — bridge Mat→HObject, flipping frame provenance back (§6.4). 
/// / vision.tohobject — Mat→HObject 桥,将来源域翻回 Halcon（§6.4）
/// </summary>
internal sealed class ToHObjectNode : VisionNodeBase
{
    public ToHObjectNode(string id) : base(id, new NodeContract("vision.tohobject", 1),
        execIn: true, execOut: true,
        ("image", ImageDescriptor.Instance, false))
    {
        AddDataOut("image", ImageDescriptor.Instance);
    }

    protected override Task RunAsync(IExecutionContext ctx, IVisionEngine engine, CancellationToken ct)
    {
        var frame = ctx.GetData("image") as VisionFrame
            ?? throw new InvalidOperationException("tohobject expects a VisionFrame on 'image'");
        ctx.SetData("image", FrameBridge.ToHObject(frame));
        return Task.CompletedTask;
    }
}