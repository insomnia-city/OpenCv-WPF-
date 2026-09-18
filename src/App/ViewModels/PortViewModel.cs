using CommunityToolkit.Mvvm.ComponentModel;
using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Model;

namespace HalconWorkflow.App.ViewModels;

/// <summary>
/// Interactive-connection highlight state for a port during pending-connection drag (§4.3, stage-12).
/// / 拖拽连线期间端口的交互高亮状态(§4.3,阶段12)
/// </summary>
public enum ConnectState
{
    /// <summary>No active pending connection targeting this port. · 无拖拽连线指向此端口</summary>
    None,

    /// <summary>A valid target; green ring. · 合法目标(绿环)</summary>
    Valid,

    /// <summary>An invalid target; red ring. · 非法目标(红环)</summary>
    Invalid
}

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

    /// <summary>
    /// Highlight state during interactive pending-connection drag (§4.3). Reset to None when drag ends.
    /// / 拖拽连线期间的高亮状态(§4.3)；拖拽结束后重置为 None
    /// </summary>
    public ConnectState Highlight
    {
        get => _highlight;
        set => SetProperty(ref _highlight, value);
    }
    private ConnectState _highlight = ConnectState.None;

    /// <summary>
    /// Short caption of the last cycle's value on this data port, formatted for the canvas badge
    /// (§5.4, stage-13). Empty for exec ports and for ports that never produced a value.
    /// / 最近一轮该数据端口的运行值短文本(画布徽标，§5.4,阶段13)；控制流端口与从未产值的端口为空串。
    /// </summary>
    public string ValueText
    {
        get => _valueText;
        set => SetProperty(ref _valueText, value);
    }
    private string _valueText = "";
}
