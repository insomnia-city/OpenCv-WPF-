using HalconWorkflow.Abstractions;
using HalconWorkflow.MotionDrivers.Native;

namespace HalconWorkflow.MotionDrivers;

/// <summary>
/// Outcome of probing for a vendor motion driver. When <see cref="IsNative"/> is false
/// the driver fell back to the phantom controller and <see cref="Message"/> says why.
/// / 探测厂商运动驱动的结果。IsNative 为 false 时已回退幻影控制器，Message 说明原因。
/// </summary>
public readonly record struct MotionDriverProbe(
    string VendorId,
    int CardNo,
    bool IsNative,
    bool IsFallback,
    string Message);

/// <summary>
/// Selects a motion controller for a vendor. Native SDKs are probed first; a missing or
/// unknown driver falls back to the deterministic phantom controller with an explicit
/// notice (§6.3) — never a silent wrong result, only "slower, still correct".
/// / 为厂商选择运动控制器。先探测原生 SDK；缺失或未知驱动回退到确定性幻影控制器并显式提示
///   （§6.3）——绝不静默给出错误结果，只是"较慢但仍正确"。
/// </summary>
public static class MotionDriverFactory
{
    /// <summary>Known vendors → (native library, display). / 已知厂商 → (原生库, 显示名)</summary>
    public static readonly IReadOnlyDictionary<string, (string Library, string Display)> KnownVendors =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["googol"] = ("gmotion", "Googol GT/GE"),
            ["zmotion"] = ("zauxdll", "ZMotion"),
            ["leadshine"] = ("smc5x", "Leadshine SMC5x"),
            ["adlink"] = ("aps168", "Adlink APS168")
        };

    /// <summary>Probes a vendor without constructing a controller. / 探测厂商但不构造控制器</summary>
    public static MotionDriverProbe Probe(string vendorId, int cardNo)
    {
        if (!KnownVendors.TryGetValue(vendorId, out var vendor))
            return new MotionDriverProbe(vendorId, cardNo, IsNative: false, IsFallback: true,
                $"unknown motion vendor '{vendorId}'; phantom fallback engaged");

        var load = NativeMotionLibrary.Load(vendor.Library);
        return load.IsLoaded
            ? new MotionDriverProbe(vendorId, cardNo, IsNative: true, IsFallback: false, load.Message)
            : new MotionDriverProbe(vendorId, cardNo, IsNative: false, IsFallback: true, load.Message);
    }

    /// <summary>
    /// Creates a controller for the vendor: native when the SDK is present, otherwise the
    /// phantom software fallback. / 为厂商创建控制器：SDK 存在用原生，否则回退幻影软件实现。
    /// </summary>
    public static IMotionController Create(string vendorId, int cardNo)
    {
        var probe = Probe(vendorId, cardNo);
        return probe.IsNative
            ? new NativeMotionController(vendorId, cardNo, KnownVendors[vendorId].Library)
            : new PhantomMotionController(vendorId, cardNo);
    }
}
