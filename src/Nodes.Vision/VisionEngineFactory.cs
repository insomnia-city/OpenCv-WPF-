using HalconWorkflow.Nodes.Vision.Adapters;
using HalconWorkflow.Nodes.Vision.Engines;

namespace HalconWorkflow.Nodes.Vision;

/// <summary>
/// Locates a licensed Halcon runtime installed on this machine. Returns the managed
/// halcondotnet assembly path when present, else null (software fallback). The
/// assembly is loaded lazily by a deployment-time adapter, so this library stays
/// buildable without Halcon installed (§6.1/§6.3).
/// / 探测本机已安装的 Halcon 运行时;返回 halcondotnet 托管程序集路径;缺失返回 null
///   （进入软回退）。程序集由部署期适配器惰性加载，本库无 Halcon 也可编译。
/// </summary>
public static class HalconProbe
{
    /// <summary>
    /// Looks under the standard MVTec install layout for halcon.dll + halcondotnet.dll. 
    /// / 在标准 MVTec 安装布局下查找 halcon.dll 与 halcondotnet.dll
    /// </summary>
    public static string? TryLocateManagedAssembly()
    {
        try
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var mvtec = Path.Combine(root, "MVTec");
            if (!Directory.Exists(mvtec)) return null;

            var versionDir = Directory.EnumerateDirectories(mvtec, "HALCON-*")
                .OrderByDescending(d => d, StringComparer.Ordinal)
                .FirstOrDefault();
            if (versionDir is null) return null;

            var bin = Path.Combine(versionDir, "bin", "x64-win64");
            string halconDll = Path.Combine(bin, "halcon.dll");
            string managed = Path.Combine(bin, "halcondotnet.dll");
            return File.Exists(halconDll) && File.Exists(managed) ? managed : null;
        }
        catch
        {
            return null; // probe must never crash the app / 探测异常一律视为缺失
        }
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

    /// <summary>Fallback because no licensed Halcon runtime was found. · 未找到授权 Halcon 运行时的回退</summary>
    public static EngineResolution NoHalconRuntime => new("phantom", true, "no-halcon-runtime");

    /// <summary>Fallback because a runtime exists but no adapter is registered. · 有运行时但未注册适配器的回退</summary>
    public static EngineResolution UnwiredHalcon => new("phantom", true, "halcon-unwired");

    /// <summary>Fallback because the registered adapter factory threw. · 注册的适配器工厂抛异常时的回退</summary>
    public static EngineResolution AdapterFailed(string detail)
        => new("phantom", true, "halcon-adapter-failed", detail);

    /// <summary>Real engine over a registered adapter. · 经已注册适配器启用的真实引擎</summary>
    public static EngineResolution RealEngine(string provider)
        => new(provider, false, "halcon-adapter");
}

/// <summary>
/// Resolves a concrete engine given the machine state: a real Halcon adapter when a
/// licensed runtime is installed and a deployment adapter is registered, otherwise the
/// deterministic software fallback (§6.3). Resolution never throws — every unexpected
/// state degrades to the phantom engine with an explicit <see cref="EngineResolution"/>.
/// / 依据宿主环境解析具体引擎：已有授权运行时且注册了部署适配器时用真实 Halcon;
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
        => CreateResolvedCore(forcePhantom, HalconProbe.TryLocateManagedAssembly, out resolution);

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
            resolution = EngineResolution.NoHalconRuntime;
            return new PhantomVisionEngine();
        }

        if (HalconAdapterRegistry.TryCreate(out var adapter, out var detail))
        {
            resolution = EngineResolution.RealEngine(adapter!.Provider);
            return new HalconVisionEngine(adapter);
        }

        resolution = detail is null
            ? EngineResolution.UnwiredHalcon
            : EngineResolution.AdapterFailed(detail);
        return new PhantomVisionEngine();
    }
}