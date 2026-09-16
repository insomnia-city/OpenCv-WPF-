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
/// Resolves a concrete engine given the machine state: a real Halcon adapter when a
/// licensed runtime is installed, otherwise the deterministic software fallback
/// (§6.3). Today the Halcon adapter plugs in at deployment; the pool and bridge are
/// already hardened against the same leased interface.
/// / 依据宿主环境解析具体引擎：有授权运行时用 Halcon 适配器;否则用确定性软回退（§6.3）。
///   现阶段 Halcon 适配器于部署期接入;池与桥已按同一租约接口加固。
/// </summary>
public static class VisionEngineFactory
{
    /// <summary>
    /// Builds an engine; explicitly forces the fallback for guaranteed-deterministic runs. 
    /// / 构造引擎;可强制使用回退引擎以保证运行确定性
    /// </summary>
    public static IVisionEngine CreateResolved(bool forcePhantom = false)
    {
        if (!forcePhantom && HalconProbe.TryLocateManagedAssembly() is not null)
            throw new PlatformNotSupportedException(
                "Halcon runtime detected but the Halcon adapter is not yet wired; " +
                "use forcePhantom:true or install the adapter (halcon adapter not deployed in this stage).");
        return new PhantomVisionEngine();
    }
}