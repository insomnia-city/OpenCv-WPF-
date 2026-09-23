using System;
using System.Collections.ObjectModel;
using System.Windows;
using HalconWorkflow.App.Services;
using HalconWorkflow.Storage;

namespace HalconWorkflow.App.ViewModels;

/// <summary>
/// View model for the independent image window (stage-24). Owns a render timer that
/// drives the pending-frame drain so the UI never back-pressures the execution thread.
/// · 独立图像窗视图模型(阶段24)。独占一个渲染计时器驱动待处理帧排出,从不给执行线程制造回压。
/// </summary>
public sealed class ImageWindowViewModel : IDisposable
{
    private readonly LocalizationService _loc;
    private readonly PreviewRing _preview;
    private readonly ObservableCollection<PreviewFrame> _frames = [];
    private readonly object _gate = new();
    private PreviewFrame? _pending;
    private bool _disposed;

    public ImageWindowViewModel(LocalizationService loc, PreviewRing preview) =>
        (_loc, _preview) = (loc, preview);

    /// <summary>Frames captured for the current node. · 当前节点捕获的帧</summary>
    public IReadOnlyList<PreviewFrame> Frames => _frames;

    /// <summary>Label of the node being viewed. · 正在查看的节点标签</summary>
    public string NodeLabel { get; private set; } = "";

    /// <summary>Localized previous/next/live labels. · 本地化的上一帧/下一帧/实时标签</summary>
    public string PreviousLabel => _loc["imagewindow.previous"];
    public string NextLabel => _loc["imagewindow.next"];
    public string LiveLabel => _loc["imagewindow.live"];
    public string RoiLabel => _loc["imagewindow.roi"];
    public string CrosshairLabel => _loc["imagewindow.crosshair"];
    public string RefreshLabel => _loc["imagewindow.refresh"];

    /// <summary>Current frame index within the node's recent frames. · 当前帧在节点最近帧中的索引</summary>
    public int FrameIndex { get; private set; }

    /// <summary>Whether the live feed is active. · 实时推送是否活跃</summary>
    public bool Live { get; set; } = true;
    /// <summary>Whether the ROI overlay is shown. · ROI 叠加是否显示</summary>
    public bool ShowRoi { get; set; }
    /// <summary>Whether the crosshair overlay is shown. · 十字线叠加是否显示</summary>
    public bool ShowCrosshair { get; set; }

    /// <summary>The current preview frame to render, or null. · 当前要渲染的预览帧,无则 null</summary>
    public PreviewFrame? CurrentImage { get; private set; }
    /// <summary>Info text for the status bar. · 状态栏信息文本</summary>
    public string CurrentInfo => _pending is null ? "" : _pending.Summary ?? "";
    /// <summary>History navigation text. · 历史导航文本</summary>
    public string HistoryText => $"{FrameIndex + 1}/{Frames.Count}";

    /// <summary>Opens the window for a node and subscribes to its preview frames. * 打开窗口并订阅节点的预览帧</summary>
    public void Open(string nodeId, string nodeLabel)
    {
        NodeLabel = nodeLabel;
        _pending = null;
        lock (_gate) _frames.Clear();
        FrameIndex = 0;
        CurrentImage = null;
        _preview.Published += OnPublished;
    }

    /// <summary>Closes the window and unsubscribes. * 关闭窗口并取消订阅</summary>
    public void Close() => _preview.Published -= OnPublished;

    /// <summary>Drains pending frames into the visible collection. * 排出待处理帧到可见集合</summary>
    public void Tick()
    {
        PreviewFrame? frame;
        lock (_gate)
        {
            frame = _pending;
            _pending = null;
        }
        if (frame is not null)
        {
            lock (_frames)
            {
                _frames.Add(frame);
                FrameIndex = _frames.Count - 1;
            }
            CurrentImage = frame;
        }
    }

    /// <summary>Shows the previous frame in history. * 显示历史中的上一帧</summary>
    public void Previous()
    {
        lock (_frames)
        {
            if (FrameIndex > 0) { FrameIndex--; CurrentImage = _frames[FrameIndex]; }
        }
    }

    /// <summary>Shows the next frame in history. * 显示历史中的下一帧</summary>
    public void Next()
    {
        lock (_frames)
        {
            if (FrameIndex < _frames.Count - 1) { FrameIndex++; CurrentImage = _frames[FrameIndex]; }
        }
    }

    /// <summary>Drops all frames. * 清空所有帧</summary>
    public void Clear()
    {
        lock (_gate) { _pending = null; }
        lock (_frames) { _frames.Clear(); FrameIndex = 0; CurrentImage = null; }
    }

    private void OnPublished(string nodeId, PreviewFrame frame)
    {
        if (!Live) return;
        lock (_gate) _pending = frame;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _preview.Published -= OnPublished;
    }
}
