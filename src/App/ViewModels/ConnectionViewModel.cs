using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using HalconWorkflow.Core.Graph;

namespace HalconWorkflow.App.ViewModels;

/// <summary>
/// Connection projection: exposes graph-space Point anchors for the Nodify line and re-anchors when an endpoint node moves. 
/// 连线投影：暴露 Nodify 线条所需的图空间锚点;任一端节点移动时重新锚定
/// </summary>
public sealed partial class ConnectionViewModel : ObservableObject
{
    private Point _source;
    private Point _target;

    public ConnectionViewModel(GraphLink link, NodeViewModel from, PortViewModel fromPort, NodeViewModel to, PortViewModel toPort)
    {
        Link = link;
        From = from;
        FromPort = fromPort;
        To = to;
        ToPort = toPort;
        Reanchor();
        if (!ReferenceEquals(from, to))
        {
            from.Moved += OnEndpointMoved;
            to.Moved += OnEndpointMoved;
        }
    }

    /// <summary>Kernel link. · 内核连线</summary>
    public GraphLink Link { get; }

    /// <summary>Source node projection. · 源节点投影</summary>
    public NodeViewModel From { get; }

    /// <summary>Source port projection. · 源端口投影</summary>
    public PortViewModel FromPort { get; }

    /// <summary>Target node projection. · 目标节点投影</summary>
    public NodeViewModel To { get; }

    /// <summary>Target port projection. · 目标端口投影</summary>
    public PortViewModel ToPort { get; }

    /// <summary>Line start anchor. · 线起点</summary>
    public Point Source
    {
        get => _source;
        private set => SetProperty(ref _source, value);
    }

    /// <summary>Line end anchor. · 线终点</summary>
    public Point Target
    {
        get => _target;
        private set => SetProperty(ref _target, value);
    }

    private void OnEndpointMoved(NodeViewModel _) => Reanchor();

    /// <summary>Recomputes anchors from endpoint node layout. · 按端点节点布局重算锚点</summary>
    public void Reanchor()
    {
        Source = From.ViewAnchor(FromPort);
        Target = To.ViewAnchor(ToPort);
    }
}