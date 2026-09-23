using System.Globalization;
using HalconWorkflow.Abstractions;
using HalconWorkflow.App.Services;
using HalconWorkflow.App.ViewModels;
using HalconWorkflow.MotionDrivers;
using Xunit;

namespace HalconWorkflow.App.Tests;

/// <summary>
/// Stage-15 gate: field-readiness self-check (real backend vs §6.3 fallback surfaced to
/// the operator) and last-resort crash logging. The capability probes are injected, so
/// every combination is exercised without any hardware or Halcon SDK.
/// / 阶段15 闸门：现场就绪自检（向操作员呈现真实后端 vs §6.3 回退）+ 最后防线崩溃日志。
///   能力探测可注入，任意组合在无硬件/无 Halcon SDK 时均可覆盖。
/// </summary>
public sealed class RuntimeCapabilitiesTests
{
    private const string RuntimePath = @"C:\fake\MVTec\HALCON-24.11\bin\x64-win64\halcondotnet.dll";

    private static MotionDriverProbe NativeProbe()
        => new("googol", 0, IsNative: true, IsFallback: false, "native gmotion loaded");

    private static MotionDriverProbe FallbackProbe()
        => new("googol", 0, IsNative: false, IsFallback: true,
            "native motion library 'gmotion' not found; phantom fallback engaged");

    [Fact]
    public void Collect_AllReal_ReportsBothCapabilitiesReal()
    {
        var caps = RuntimeCapabilities.Collect(() => RuntimePath, () => true, NativeProbe);

        Assert.Equal(2, caps.Count);
        Assert.Equal(new[] { "capability.vision", "capability.motion" },
            caps.Select(c => c.LabelKey).ToArray());
        Assert.All(caps, c => Assert.True(c.Real));
        Assert.All(caps, c => Assert.False(string.IsNullOrWhiteSpace(c.Detail)));
    }

    [Fact]
    public void Collect_NoRuntime_VisionIsFallback()
    {
        var caps = RuntimeCapabilities.Collect(() => null, () => false, NativeProbe);

        var vision = caps.Single(c => c.LabelKey == "capability.vision");
        Assert.False(vision.Real);
        Assert.Contains("no Halcon runtime", vision.Detail);
    }

    [Fact]
    public void Collect_RuntimeWithoutAdapter_VisionIsFallback()
    {
        var caps = RuntimeCapabilities.Collect(() => RuntimePath, () => false, NativeProbe);

        var vision = caps.Single(c => c.LabelKey == "capability.vision");
        Assert.False(vision.Real);
        Assert.Contains("no adapter registered", vision.Detail);
    }

    [Fact]
    public void Collect_MotionFallback_SurfacesProbeMessage()
    {
        var caps = RuntimeCapabilities.Collect(() => RuntimePath, () => true, FallbackProbe);

        var motion = caps.Single(c => c.LabelKey == "capability.motion");
        Assert.False(motion.Real);
        Assert.Contains("phantom fallback engaged", motion.Detail);
    }

    [Fact]
    public async Task Shell_Startup_LogsCapabilityLinesForBothSubsystems()
    {
        var shell = new ShellViewModel(new LocalizationService(), new NoopDialogService());
        try
        {
            Assert.Equal(2, shell.Capabilities.Count);
            Assert.Contains(shell.Log.Entries, e => e.Message.StartsWith(shell.Loc["capability.vision"]));
            Assert.Contains(shell.Log.Entries, e => e.Message.StartsWith(shell.Loc["capability.motion"]));
            // Default culture is zh-Hans: labels are localized, not raw keys. · 默认中文：用本地化标签而非原始键
            Assert.Contains(shell.Log.Entries, e => e.Message.Contains("视觉引擎"));
        }
        finally
        {
            await shell.DisposeAsync();
        }
    }

    [Fact]
    public void CrashGuard_Describe_IncludesSourceTypeAndMessage()
    {
        var text = CrashGuard.Describe(new InvalidOperationException("boom"), "ui");

        Assert.Contains("[ui]", text);
        Assert.Contains("InvalidOperationException", text);
        Assert.Contains("boom", text);
    }

    [Fact]
    public void Localization_CapabilityKeys_AreLocalizedInAllThreeCultures()
    {
        var loc = new LocalizationService();

        loc.Culture = new CultureInfo("en");
        Assert.Equal("Vision engine", loc["capability.vision"]);
        Assert.Contains("1", loc.Get("capability.summary", 1, 1));

        loc.Culture = new CultureInfo("zh-Hans");
        Assert.Equal("视觉引擎", loc["capability.vision"]);

        loc.Culture = new CultureInfo("ko");
        Assert.Equal("비전 엔진", loc["capability.vision"]);
    }

    private sealed class NoopDialogService : IDialogService
    {
        public string? OpenGraphFile(string filter) => null;
        public string? SaveGraphFile(string defaultName, string filter) => null;
        public string? SaveCsvFile(string defaultName) => null;
        public bool Confirm(string message) => true;
        public void ReportError(string message) { }
        public void ShowImageWindow(string nodeId, string nodeLabel) { }
    }
}