using HalconWorkflow.Nodes.Vision.Imaging;

namespace HalconWorkflow.Nodes.Vision.Adapters;

/// <summary>
/// Deployment-time bridge between this library and a licensed machine-vision SDK
/// (§6.3 "software fallback"). The whole MVTec halcondotnet translation lives in a
/// concrete adapter — never in the engine proxy, so this library stays buildable and
/// testable without any Halcon installation. Shapes are OUR types (<see cref="VisionFrame"/>,
/// stringly-typed args, <see cref="Dictionary{TKey,TValue}"/> results), identical to the
/// phantom engine's registry, so a dropped-in adapter swaps behaviour transparently.
/// / 本库与「已授权机器视觉 SDK」之间的部署期桥（§6.3 软回退）。MVTec halcondotnet 的
///   全部翻译驻留在具体适配器内，绝不属于代理引擎——因此本库无 Halcon 安装也能编译与测试。
///   出入参一律用本库类型（VisionFrame、string 键参数、Dictionary 结果），与幻影引擎注册表
///   完全一致：部署时撘入适配器即无缝切换真实行为。
/// </summary>
/// <remarks>
/// Instances are NOT thread-safe: exactly one executor uses one adapter at a time
/// (pool lease contract, same as <see cref="IVisionEngine"/>). Failures are surfaced as
/// exceptions by <see cref="ExecuteAsync"/>; cancellation is cooperative.
/// / 实例非线程安全：同一时刻仅一个执行线程使用（池租约契约，与 IVisionEngine 相同）。
///   失败以 ExecuteAsync 抛异常上报;取消为协作式。
/// </remarks>
public interface IHalconAdapter : IDisposable
{
    /// <summary>
    /// Engine/module id reported on the engine (<see cref="IVisionEngine.Id"/>), e.g. "halcondotnet".
    /// / 引擎/模块标识（经引擎 Id 上报），如 "halcondotnet" 
    /// </summary>
    string Provider { get; }

    /// <summary>Whether this adapter can run the named op. / 本适配器是否支持指定操作</summary>
    bool Supports(string op);

    /// <summary>
    /// Runs a named op over an optional gray/color input with stringly-typed args.
    /// Returns a frame or a <see cref="Dictionary{TKey,TValue}"/> result exactly like the
    /// phantom engine. Throws on op failure; cooperative cancellation.
    /// / 运行指定操作（可选图像输入 + string 键参数）。与幻影引擎一致地返回
    ///   VisionFrame 或 Dictionary 结果;失败即抛;支持协作式取消。
    /// </summary>
    Task<object> ExecuteAsync(string op, VisionFrame? input, IReadOnlyDictionary<string, object>? args, CancellationToken ct);
}