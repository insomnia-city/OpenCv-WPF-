using HalconWorkflow.MotionDrivers;
using HalconWorkflow.Nodes.Vision;
using HalconWorkflow.Nodes.Vision.Adapters;

namespace HalconWorkflow.App.Services;

/// <summary>
/// One optional subsystem's resolved capability: whether a REAL backend (hardware /
/// licensed runtime) is engaged or the deterministic software fallback, plus a technical
/// detail string. <see cref="LabelKey"/> is a localization key for the panel.
/// / 单个可选子系统的解析能力：接了真实后端（硬件/授权运行时）还是确定性软件回退，附技术细节。
///   LabelKey 为面板本地化键。
/// </summary>
public sealed record CapabilityStatus(string LabelKey, bool Real, string Detail);

/// <summary>
/// Field-readiness self-check: surfaces which optional subsystems run on real backends vs
/// the §6.3 software fallback. The probes are injected so the report is deterministic and
/// testable without any hardware; the parameterless overload uses the production probes.
/// The subsystem resolvers previously computed this truth and threw it away, leaving the
/// operator unable to tell real from phantom on the shop floor.
/// / 现场就绪自检：呈现哪些可选子系统跑真实后端、哪些跑 §6.3 软回退。探测函数可注入，使报告
///   在无硬件时也确定且可测;无参重载用生产探测。此前各解析器算完即丢，现场操作员无从分辨真实与幻影。
/// </summary>
public static class RuntimeCapabilities
{
    /// <summary>Production probes: OpenCV runtime/provider + Googol motion driver. · 生产探测：OpenCV 运行时/提供器 + 固高运动驱动</summary>
    public static IReadOnlyList<CapabilityStatus> Collect()
        => Collect(
            OpenCvProbe.TryDescribeRuntime,
            () => VisionProviderRegistry.IsRegistered,
            () => MotionDriverFactory.Probe("googol", 0));

    /// <summary>
    /// Builds the report from injectable probes (test seam). · 由可注入探测构建报告（测试接缝）
    /// </summary>
    public static IReadOnlyList<CapabilityStatus> Collect(
        Func<string?> describeOpenCvRuntime,
        Func<bool> visionProviderRegistered,
        Func<MotionDriverProbe> motionProbe)
    {
        ArgumentNullException.ThrowIfNull(describeOpenCvRuntime);
        ArgumentNullException.ThrowIfNull(visionProviderRegistered);
        ArgumentNullException.ThrowIfNull(motionProbe);

        return [DescribeVision(describeOpenCvRuntime(), visionProviderRegistered()), DescribeMotion(motionProbe())];
    }

    /// <summary>
    /// Vision capability: real only when the OpenCV native runtime loads AND a provider is
    /// registered (the exact preconditions <c>VisionEngineFactory</c> needs).
    /// / 视觉能力：仅当 OpenCV 原生运行时可加载且已注册提供器时为真实
    ///   （正是 VisionEngineFactory 所需的前提）。
    /// </summary>
    private static CapabilityStatus DescribeVision(string? runtimeDescription, bool providerRegistered)
    {
        if (runtimeDescription is null)
            return new CapabilityStatus("capability.vision", false, "no OpenCV native runtime detected; phantom fallback");
        return providerRegistered
            ? new CapabilityStatus("capability.vision", true, $"OpenCV runtime ({runtimeDescription}) + provider registered")
            : new CapabilityStatus("capability.vision", false,
                "OpenCV runtime present but no provider registered; phantom fallback");
    }

    /// <summary>Motion capability straight from the driver probe. · 运动能力直接取自驱动探测。</summary>
    private static CapabilityStatus DescribeMotion(MotionDriverProbe probe)
        => new("capability.motion", probe.IsNative, probe.Message);
}