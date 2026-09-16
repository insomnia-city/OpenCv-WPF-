using HalconWorkflow.Nodes.Vision.Imaging;

namespace HalconWorkflow.Nodes.Vision.Engines;

/// <summary>
/// A single visual engine instance (Halcon or fallback phantom). Instances are NOT
/// thread-safe: exactly one executor uses an instance at a time (§6.2). Ops are
/// named so the same graph node payload can run on a real Halcon engine or a
/// software fallback (§6.3 "software fallback").
/// / 单个视觉引擎实例（Halcon 或软件回退幻影）。实例非线程安全：一个执行线程独占（§6.2）。
///   操作按名调度，同一节点载荷既可跑真实 Halcon 也能跑软回退（§6.3）。
/// </summary>
public interface IVisionEngine : IDisposable
{
    /// <summary>Engine display id / module. / 引擎标识/来源</summary>
    string Id { get; }

    /// <summary>Whether this engine can run the named op. / 该引擎是否支持指定操作</summary>
    bool Supports(string op);

    /// <summary>
    /// Runs a named op with optional gray/color input and stringly-typed args.
    /// Returns a frame result, or a <see cref="Dictionary{TKey,TValue}"/> result
    /// for non-image ops. Throws on op failure; cooperative cancellation.
    /// / 运行指定操作;带可选图像输入与字符串键参数。图像操作返回 VisionFrame
    ///   非图像操作返回 Dictionary。失败即抛;支持协作式取消。
    /// </summary>
    Task<object> ExecuteAsync(string op, VisionFrame? input, IReadOnlyDictionary<string, object>? args, CancellationToken ct);
}