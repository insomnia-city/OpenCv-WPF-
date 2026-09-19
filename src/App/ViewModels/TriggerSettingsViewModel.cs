using CommunityToolkit.Mvvm.ComponentModel;
using HalconWorkflow.App.Services;
using HalconWorkflow.Core.Graph;
using HalconWorkflow.Core.Model;

namespace HalconWorkflow.App.ViewModels;

/// <summary>Selectable trigger source with its localized label. · 可选触发源(含本地化标签)</summary>
public sealed record TriggerSourceOption(TriggerSource Source, string Name);

/// <summary>
/// View-model for the trigger-settings dialog (§5.4, stage-20). Mutates the live graph config
/// only on <see cref="Apply"/> (pressed OK), so Cancel/skip leaves it untouched.
/// · 触发设置对话框的视图模型(§5.4,阶段20)。仅在 Apply()(按确定)时改写图的实时配置,
///   取消/跳过则不动原配置。
/// </summary>
public sealed partial class TriggerSettingsViewModel : ObservableObject
{
    private const int MinIntervalMs = 50;
    private const int MaxIntervalMs = 3_600_000;
    private readonly TriggerConfig _config;
    private readonly LocalizationService _loc;

    public TriggerSettingsViewModel(TriggerConfig config, LocalizationService loc)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _loc = loc ?? throw new ArgumentNullException(nameof(loc));
        Sources =
        [
            new(TriggerSource.Manual, _loc["trigger.manual"]),
            new(TriggerSource.Timer, _loc["trigger.timer"]),
            new(TriggerSource.TagChange, _loc["trigger.tag"])
        ];
        _enabled = config.Enabled;
        _source = Sources.First(o => o.Source == MapSource(config.Source));
        _intervalMs = Math.Max(MinIntervalMs, config.IntervalMs);
        _debounceMs = config.DebounceMs;
        _tag = config.Tag ?? "";
    }

    public string Title => _loc["trigger.title"];
    public string EnableLabel => _loc["trigger.enable"];
    public string SourceLabel => _loc["trigger.source"];
    public string IntervalLabel => _loc["trigger.interval"];
    public string DebounceLabel => _loc["trigger.debounce"];
    public string TagLabel => _loc["trigger.tagPattern"];
    public string OkLabel => _loc["trigger.ok"];
    public string CancelLabel => _loc["trigger.cancel"];

    /// <summary>The subset of sources the dialog can configure. · 对话框可配置的触发源子集</summary>
    public IReadOnlyList<TriggerSourceOption> Sources { get; }

    [ObservableProperty]
    [property: System.ComponentModel.Description("Whether any background trigger source is active. · 是否启用后台触发源")]
    private bool _enabled;

    [ObservableProperty]
    private TriggerSourceOption _source = null!;

    [ObservableProperty]
    private int _intervalMs;

    [ObservableProperty]
    private int _debounceMs;

    [ObservableProperty]
    private string _tag = "";

    /// <summary>Writes current dialog values back to the graph config with clamps. · 按当前对话框值(含约束)回写图形配置</summary>
    public void Apply()
    {
        _config.Enabled = Enabled;
        _config.Source = Source?.Source ?? TriggerSource.Manual;
        _config.Tag = string.IsNullOrWhiteSpace(Tag) ? null : Tag.Trim();
        _config.DebounceMs = Math.Max(0, DebounceMs);
        _config.IntervalMs = Math.Clamp(IntervalMs, MinIntervalMs, MaxIntervalMs);
    }

    /// <summary>Maps any source to the closest configurable one (external/hardware sources read as manual). · 把任意来源映射到最近的可配置项(外部/硬件触发按手动看待)</summary>
    private static TriggerSource MapSource(TriggerSource source) => source switch
    {
        TriggerSource.Timer => TriggerSource.Timer,
        TriggerSource.TagChange => TriggerSource.TagChange,
        _ => TriggerSource.Manual
    };
}