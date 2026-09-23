using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HalconWorkflow.App.Services;
using HalconWorkflow.Storage;

namespace HalconWorkflow.App.ViewModels;

/// <summary>
/// View model for the independent image window (stage-24). Tracks a single node's recent
/// preview frames: <see cref="Open"/> subscribes to <see cref="PreviewRing.Published"/> and
/// filters by the opened node id; <see cref="Tick"/> drains the latest published frame from a
/// lock-guarded slot so the UI never back-pressures the execution thread (§4.5). History
/// navigation via <see cref="PreviousCommand"/>/<see cref="NextCommand"/>; live/ROI/crosshair
/// toggles; localized labels.
/// · 独立图像窗视图模型(阶段24)。追踪单节点的最近预览帧：Open 订阅 PreviewRing.Published 并按
///   目标节点 Id 过滤;Tick 从锁保护的槽位排出最新帧,绝不反压执行线程(§4.5)。Previous/Next
///   命令做历史导航;实时/ROI/十字线开关;文案本地化。
/// </summary>
public sealed partial class ImageWindowViewModel : ObservableObject, IDisposable
{
    private readonly LocalizationService _loc;
    private readonly PreviewRing _preview;
    private readonly ObservableCollection<PreviewFrame> _frames = [];
    private readonly object _frameGate = new();
    private PreviewFrame? _pending;
    private bool _disposed;

    public ImageWindowViewModel(LocalizationService loc, PreviewRing preview) =>
        (_loc, _preview) = (loc, preview);

    public PreviewRing Preview => _preview;

    /// <summary>Frames captured for the opened node. · 当前节点捕获的帧</summary>
    public ObservableCollection<PreviewFrame> Frames => _frames;

    /// <summary>Label of the node being viewed. · 正在查看的节点标签</summary>
    public string NodeLabel { get; private set; } = "";

    /// <summary>Localized previous/next/live labels. · 本地化的上一帧/下一帧/实时标签</summary>
    public string PreviousLabel => _loc["imagewindow.previous"];
    public string NextLabel => _loc["imagewindow.next"];
    public string LiveLabel => _loc["imagewindow.live"];
    public string RoiLabel => _loc["imagewindow.roi"];
    public string CrosshairLabel => _loc["imagewindow.crosshair"];
    public string RefreshLabel => _loc["imagewindow.refresh"];

    /// <summary>Whether the live feed is active. When false, newer frames are ignored until
    /// <see cref="Live"/> is re-enabled and the view refreshed. · 实时推送是否活跃;为 false 时
    /// 新帧被忽略,直到重新开启并刷新。</summary>
    [ObservableProperty] private bool _live = true;

    /// <summary>Whether the ROI overlay is shown. · ROI 叠加是否显示</summary>
    [ObservableProperty] private bool _showRoi;

    /// <summary>Whether the crosshair overlay is shown. · 十字线叠加是否显示</summary>
    [ObservableProperty] private bool _showCrosshair;

    /// <summary>History slot tracked for <see cref="HistoryText"/>. · 历史导航当前槽位(供 HistoryText 显示)</summary>
    [ObservableProperty] private int _frameIndex;

    /// <summary>Whether history navigation vs live mode. · 当前是否处于历史导航而非实时</summary>
    [ObservableProperty] private bool _browsingHistory;

    /// <summary>The current preview frame to render, or null. · 当前要渲染的预览帧,无则 null</summary>
    [ObservableProperty] private PreviewFrame? _currentImage;

    /// <summary>Whether a frame is currently displayed. · 当前是否正在显示某帧</summary>
    [ObservableProperty] private bool _hasImage;

    /// <summary>Node id currently being viewed. · 当前正在查看的节点 Id</summary>
    private string _nodeId = "";

    /// <summary>History navigation text. · 历史导航文本</summary>
    public string HistoryText => $"{FrameIndex + 1}/{Frames.Count}";

    /// <summary>Opens the window for a node and subscribes to its preview frames. Re-opening is
    /// idempotent: a prior subscription is removed first (§4.5). · 打开窗口并订阅节点的预览帧。
    /// 重复打开幂等：先移除旧订阅。</summary>
    public void Open(string nodeId, string nodeLabel)
    {
        NodeLabel = nodeLabel;
        _nodeId = nodeId;
        ClearFrames();
        _preview.Published -= OnPublished;
        _preview.Published += OnPublished;
    }

    /// <summary>Closes the window and unsubscribes. · 关闭窗口并取消订阅</summary>
    public void Close()
    {
        _preview.Published -= OnPublished;
        ClearFrames();
    }

    /// <summary>Drains the latest pending frame into the visible collection (UI-thread timer).
    /// · 把最新待处理帧排入可见集合(UI 线程计时器驱动)
    /// </summary>
    public void Tick()
    {
        PreviewFrame? frame = Interlocked.Exchange(ref _pending, null);
        if (frame is null) return;
        lock (_frameGate)
        {
            _frames.Add(frame);
            FrameIndex = _frames.Count - 1;
        }
        BrowsingHistory = false;
        CurrentImage = frame;
        OnPropertyChanged(nameof(HistoryText));
    }

    /// <summary>Shows the previous frame in history. · 显示历史中的上一帧</summary>
    [RelayCommand]
    private void Previous()
    {
        lock (_frameGate)
        {
            if (FrameIndex > 0)
            {
                FrameIndex--;
                CurrentImage = _frames[FrameIndex];
                BrowsingHistory = true;
                OnPropertyChanged(nameof(HistoryText));
            }
        }
    }

    /// <summary>Shows the next frame in history. · 显示历史中的下一帧</summary>
    [RelayCommand]
    private void Next()
    {
        lock (_frameGate)
        {
            if (FrameIndex < _frames.Count - 1)
            {
                FrameIndex++;
                CurrentImage = _frames[FrameIndex];
                BrowsingHistory = true;
                OnPropertyChanged(nameof(HistoryText));
            }
        }
    }

    /// <summary>Re-attaches to live mode and shows the newest available frame. · 重新回到实时并显示最新可用帧</summary>
    [RelayCommand]
    private void Refresh()
    {
        var latest = Preview.Latest(_nodeId);
        lock (_frameGate)
        {
            if (latest is not null)
            {
                _frames.Add(latest);
                FrameIndex = _frames.Count - 1;
            }
        }
        BrowsingHistory = false;
        CurrentImage = latest;
        Live = true;
        OnPropertyChanged(nameof(HistoryText));
    }

    /// <summary>Drops all frames. · 清空所有帧</summary>
    [RelayCommand]
    private void Clear()
        => ClearFrames();

    private void ClearFrames()
    {
        Interlocked.Exchange(ref _pending, null);
        lock (_frameGate) { _frames.Clear(); FrameIndex = 0; }
        CurrentImage = null;
        BrowsingHistory = false;
        OnPropertyChanged(nameof(HistoryText));
    }

    partial void OnCurrentImageChanged(PreviewFrame? value) => HasImage = value is not null;

    private void OnPublished(string nodeId, PreviewFrame frame)
    {
        if (!Live || BrowsingHistory) return;
        if (!string.Equals(nodeId, _nodeId, StringComparison.Ordinal)) return;
        Interlocked.Exchange(ref _pending, frame);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
    }
}