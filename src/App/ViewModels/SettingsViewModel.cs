using System;
using System.Collections.Generic;
using System.Globalization;
using HalconWorkflow.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace HalconWorkflow.App.ViewModels;

/// <summary>
/// Settings dialog view model (§5.8, stage-30): opened via <see cref="IDialogService.EditSettings"/>,
/// binds a copy of the current <see cref="AppSettings"/> snapshot and returns an immutable replacement
/// record on OK — the Shell swaps its ambient <see cref="AppServices.Settings"/> instance rather than
/// mutating (init-only record). Cancel leaves the ambient untouched. · 设置对话框视图模型(§5.8,阶段30):
/// 经 IDialogService.EditSettings 打开;绑定当前 AppSettings 快照的副本;确定时返回不可变替换记录——
/// Shell 以 record 快照整体替换其 ambient AppServices.Settings 实例而非修改(init-only record)。
/// 取消则不动 ambient。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly LocalizationService _loc;
    private AppSettings? _result;

    /// <summary>True when OK was pressed, so the dialog host reads <see cref="Result"/>. · 确定已按;对话框宿主应读取 Result</summary>
    public bool HasSaved { get; private set; }

    /// <summary>Immutable edited snapshot (init-only record); null on Cancel. · 不可变编辑快照;取消为 null</summary>
    public AppSettings? Result => _result;

    /// <summary>Culture display options (fixed enumeration). · 语言选项(固定枚举)</summary>
    public IReadOnlyList<CultureInfo> CultureOptions { get; } =
        new[] { "zh-Hans", "en-US", "ko-KR" }
            .Select(c => CultureInfo.GetCultureInfo(c))
            .ToList();

    private CultureInfo _culture;
    /// <summary>Selected culture. · 所选语言</summary>
    public CultureInfo Culture { get => _culture; set => SetProperty(ref _culture, value); }

    private string _logDirectory;
    /// <summary>Log/trace output directory. · 日志/追溯输出目录</summary>
    public string LogDirectory { get => _logDirectory; set => SetProperty(ref _logDirectory, value); }

    private bool _enablePreview;
    /// <summary>Whether the preview ring captures by default (§4.2). · 预览环默认是否捕获(§4.2)</summary>
    public bool EnablePreview { get => _enablePreview; set => SetProperty(ref _enablePreview, value); }

    private int _traceRetentionDays;
    /// <summary>Trace retention in days before pruning (§9.4.2). · 追溯保留天数,过期修剪(§9.4.2)</summary>
    public int TraceRetentionDays { get => _traceRetentionDays; set => SetProperty(ref _traceRetentionDays, value); }

    /// <summary>Dialog caption. · 对话框标题</summary>
    public string Title => _loc["settings.title"];
    /// <summary>Culture field label. · 语言字段标签</summary>
    public string CultureLabel => _loc["settings.culture"];
    /// <summary>Log directory field label. · 日志目录字段标签</summary>
    public string LogDirectoryLabel => _loc["settings.logDir"];
    /// <summary>Preview enable label. · 预览启用标签</summary>
    public string PreviewLabel => _loc["settings.preview"];
    /// <summary>Trace retention field label. · 追溯保留字段标签</summary>
    public string RetentionLabel => _loc["settings.retention"];
    /// <summary>OK button label. · 确定按钮标签</summary>
    public string OkLabel => _loc["settings.ok"];
    /// <summary>Cancel button label. · 取消按钮标签</summary>
    public string CancelLabel => _loc["settings.cancel"];

    /// <summary>Builds a VM bound to a copy of <paramref name="current"/>; null source → defaults.
    /// · 绑定 current 的副本;source 为 null 时使用默认值</summary>
    public SettingsViewModel(LocalizationService loc, AppSettings? current)
    {
        _loc = loc;
        var src = current ?? AppSettingsFile.Defaults;
        _culture = CultureInfo.GetCultureInfo(src.Culture);
        _logDirectory = src.LogDirectory;
        _enablePreview = src.EnablePreview;
        _traceRetentionDays = src.TraceRetentionDays;
    }

    /// <summary>Commits the edited values to an immutable snapshot. · 将编辑值提交为不可变快照</summary>
    [RelayCommand]
    private void Save()
    {
        _result = new AppSettings
        {
            Culture = Culture.Name,
            LogDirectory = LogDirectory,
            EnablePreview = EnablePreview,
            TraceRetentionDays = Math.Max(0, TraceRetentionDays)
        };
        HasSaved = true;
    }
}
