using HalconWorkflow.Nodes.Vision.Adapters;
using HalconWorkflow.Nodes.Vision.Engines;
using OpenCvSharp;

namespace HalconWorkflow.Nodes.Vision;

/// <summary>
/// Reports whether the OpenCV native runtime is usable in this process. Unlike the
/// previous OpenCV probe there is nothing to locate: OpenCvSharp ships its native
/// runtime as a package, so the only question is whether it loads and answers.
/// Returns a non-null descriptor (the linked OpenCV version) when usable, else null.
/// / 报告本进程内 OpenCV 原生运行时是否可用。与原 OpenCV 探测不同，此处无需"查找"：
///   OpenCvSharp 随包携带原生运行时，唯一问题是能否加载并应答。可用时返回非空描述符
///   （链接到的 OpenCV 版本），否则返回 null。
/// </summary>
public static class OpenCvProbe
{
    private static readonly object Gate = new();
    private static bool _probed;
    private static string? _descriptor;
    private static string? _failure;

    /// <summary>OpenCV version when the native runtime answered, else null. / 原生运行时应答时返回版本，否则 null</summary>
    public static string? TryDescribeRuntime()
    {
        lock (Gate)
        {
            if (!_probed)
            {
                _probed = true;
                try
                {
                    _descriptor = Cv2.GetVersionString();
                }
                catch (Exception ex)
                {
                    _descriptor = null;
                    _failure = ex.GetType().Name + ": " + ex.Message;
                }
            }
            return _descriptor;
        }
    }

    /// <summary>Why the native runtime was unavailable, else null. / 原生运行时不可用的原因，否则 null</summary>
    public static string? FailureReason
    {
        get { lock (Gate) { return _failure; } }
    }
}

/// <summary>
/// Outcome of <see cref="VisionEngineFactory.CreateResolved"/> — which engine was
/// chosen, whether it is the software fallback, and why (stable reason codes).
/// / CreateResolved 的解析结果：选中了哪个引擎、是否为软回退、原因（稳定码）。
/// </summary>
public sealed record EngineResolution(string Id, bool IsFallback, string Reason, string? Detail = null)
{
    /// <summary>Fallback forced by the caller (forcePhantom). · 调用方强制回退（forcePhantom）</summary>
    public static EngineResolution ForcedFallback => new("phantom", true, "forced");

    /// <summary>Fallback because no OpenCV native runtime was found. · 未找到授权 OpenCV 运行时的回退</summary>
    public static EngineResolution NoProviderRuntime => new("phantom", true, "no-provider-runtime");

    /// <summary>Fallback because a runtime exists but no adapter is registered. · 有运行时但未注册适配器的回退</summary>
    public static EngineResolution ProviderUnwired => new("phantom", true, "provider-unwired");

    /// <summary>Fallback because the registered adapter factory threw. · 注册的适配器工厂抛异常时的回退</summary>
    public static EngineResolution AdapterFailed(string detail)
        => new("phantom", true, "provider-failed", detail);

    /// <summary>Real engine over a registered adapter. · 经已注册适配器启用的真实引擎</summary>
    public static EngineResolution RealEngine(string provider)
        => new(provider, false, "provider-active");
}

/// <summary>
/// Resolves a concrete engine given the machine state: a real OpenCV provider when a
/// licensed runtime is installed and a deployment adapter is registered, otherwise the
/// deterministic software fallback (§6.3). Resolution never throws — every unexpected
/// state degrades to the phantom engine with an explicit <see cref="EngineResolution"/>.
/// / 依据宿主环境解析具体引擎：已有授权运行时且注册了部署适配器时用真实 OpenCV;
///   其余任何状态统一回退确定性软回退（§6.3）。解析永不抛异常，所有意外状态均降级幻影并留显式原因。
/// </summary>
public static class VisionEngineFactory
{
    /// <summary>
    /// Builds an engine; explicitly forces the fallback for guaranteed-deterministic runs. 
    /// / 构造引擎;可强制使用回退引擎以保证运行确定性
    /// </summary>
    public static IVisionEngine CreateResolved(bool forcePhantom = false)
        => CreateResolved(forcePhantom, out _);

    /// <summary>
    /// Builds an engine and reports the resolution outcome. / 构造引擎并返回解析结果
    /// </summary>
    public static IVisionEngine CreateResolved(bool forcePhantom, out EngineResolution resolution)
        => CreateResolvedCore(forcePhantom, OpenCvProbe.TryDescribeRuntime, out resolution);

    /// <summary>
    /// Resolution core with an injectable probe (test seam; the machine probe otherwise).
    /// / 解析核心，探测函数可注入（测试接缝;生产用本机探测）。
    /// </summary>
    internal static IVisionEngine CreateResolvedCore(bool forcePhantom,
        Func<string?> probe, out EngineResolution resolution)
    {
        if (forcePhantom)
        {
            resolution = EngineResolution.ForcedFallback;
            return new PhantomVisionEngine();
        }

        string? managed = probe();
        if (managed is null)
        {
            resolution = EngineResolution.NoProviderRuntime;
            return new PhantomVisionEngine();
        }

        if (VisionProviderRegistry.TryCreate(out var adapter, out var detail))
        {
            resolution = EngineResolution.RealEngine(adapter!.Provider);
            return new OpenCvVisionEngine(adapter);
        }

        resolution = detail is null
            ? EngineResolution.ProviderUnwired
            : EngineResolution.AdapterFailed(detail);
        return new PhantomVisionEngine();
    }
}
