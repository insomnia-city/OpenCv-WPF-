using CommunityToolkit.Mvvm.ComponentModel;
using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Model;

namespace HalconWorkflow.App.ViewModels;

/// <summary>
/// Port projection for the canvas; exposes only binding-safe data. Never the kernel entity. 
/// 画布端口投影;仅暴露可绑定数据，绝不暴露内核实体
/// </summary>
public sealed partial class PortViewModel : ObservableObject
{
    /// <summary>Kernel port being projected. · 被投影的内核端口</summary>
    public required IPort Kernel { get; init; }

    /// <summary>Slot index within the node (row alignment). · 端口在节点内的槽位序号(行对齐)</summary>
    public int Index { get; init; }

    /// <inheritdoc />
    public string Name => Kernel.Name;

    /// <summary>In / Out. · 入/出</summary>
    public bool IsInput => Kernel.Direction == PortDirection.In;

    /// <summary>In / Out. · 入/出</summary>
    public bool IsOutput => Kernel.Direction == PortDirection.Out;

    /// <summary>Exec or Data. · 控制流/数据流</summary>
    public bool IsExec => Kernel.Kind == PortKind.Exec;

    /// <summary>Exec or Data. · 控制流/数据流</summary>
    public bool IsData => Kernel.Kind == PortKind.Data;

    /// <summary>Port type display name; 'exec' when a control port. · 端口类型显示名;控制流端口显示 exec</summary>
    public string TypeName => Kernel.Type?.Name ?? (IsExec ? "exec" : "?");

    /// <summary>Whether a link is attached (drives connector highlight). · 是否已连线(驱动连接器高亮)</summary>
    public bool IsConnected => Kernel.IsConnected;
}