using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HalconWorkflow.App.ViewModels;
using HalconWorkflow.Core.Graph;
using HalconWorkflow.Core.Model;

namespace HalconWorkflow.App.ViewModels;

/// <summary>
/// Node projection for the canvas; two-way syncs Location with the kernel GraphNode. Bindings hit this projection only. 
/// 画布节点投影;Location 与内核 GraphNode 双向同步;绑定只命中此投影
/// </summary>
public sealed partial class NodeViewModel : ObservableObject
{
    /// <summary>Card pixel width used by the anchor formula; keep in sync with the XAML template. · 锚点公式用的卡片宽(须与 XAML 模板一致)</summary>
    public const double CardWidth = 200;

    /// <summary>Card header height; keep in sync with the XAML template. · 卡片头部高(须与 XAML 模板一致)</summary>
    public const double HeaderHeight = 34;

    /// <summary>Per-row port slot height; keep in sync with the XAML template. · 每行端口槽高(须与 XAML 模板一致)</summary>
    public const double SlotHeight = 26;

    /// <summary>Top offset of the first port row inside the body. · 首行端口距卡片顶的偏移</summary>
    public const double PortsTop = HeaderHeight + 6;

    private readonly GraphNode _kernel;
    private Point _location;
    private NodeState _state;

    public NodeViewModel(GraphNode kernel, Action? deleteRequested = null)
    {
        _kernel = kernel;
        _location = new Point(kernel.Position.X, kernel.Position.Y);
        _state = kernel.Node.State;
        Inputs = new System.Collections.ObjectModel.ObservableCollection<PortViewModel>(
            kernel.Inputs.Select((p, i) => new PortViewModel { Kernel = p, Index = i }));
        Outputs = new System.Collections.ObjectModel.ObservableCollection<PortViewModel>(
            kernel.Outputs.Select((p, i) => new PortViewModel { Kernel = p, Index = i }));
        if (deleteRequested is not null) DeleteCommand = new RelayCommand(deleteRequested);
    }

    /// <summary>Kernel graph node; understood by the shell services, kept out of bindings. · 内核图节点(仅服务层使用)</summary>
    public GraphNode Kernel => _kernel;

    /// <summary>Instance id. · 实例 ID</summary>
    public string Id => _kernel.Id;

    /// <summary>Contract display, e.g. "vision.threshold:1". · 契约显示名</summary>
    public string ContractDisplay => $"{_kernel.Contract.Namespace}:{_kernel.Contract.Version}";

    /// <summary>Short header: last contract segment. · 节点头：契约最后一段</summary>
    public string Header => _kernel.Contract.Namespace.Split('.').Last();

    /// <summary>Suspended (missing plugin) flag. · 挂起(缺插件)标记</summary>
    public bool IsSuspended => _kernel.IsSuspended;

    /// <summary>Input port projections. · 输入端口投影</summary>
    public System.Collections.ObjectModel.ObservableCollection<PortViewModel> Inputs { get; }

    /// <summary>Output port projections. · 输出端口投影</summary>
    public System.Collections.ObjectModel.ObservableCollection<PortViewModel> Outputs { get; }

    /// <summary>Canvas location; pushes back into the kernel on change. · 画布坐标;变更回写内核</summary>
    public Point Location
    {
        get => _location;
        set
        {
            if (SetProperty(ref _location, value))
            {
                _kernel.Position = (value.X, value.Y);
                Moved?.Invoke(this);
            }
        }
    }

    /// <summary>Exec state for UI coloring (replayed from scheduler events). · 执行状态(由调度器事件回放)</summary>
    public NodeState State
    {
        get => _state;
        set => SetProperty(ref _state, value);
    }

    /// <summary>Deletes this node from the editor (wired by the shell). · 从编辑器删除本节点(壳层接线)</summary>
    public ICommand DeleteCommand
    {
        get => _deleteCommand ??= new RelayCommand(() => { });
        set { _deleteCommand = value; OnPropertyChanged(); }
    }
    private ICommand? _deleteCommand;

    /// <summary>Raised when the Location changes so the shell can re-anchor connections. · 坐标变化时触发(连线重新锚定)</summary>
    public event Action<NodeViewModel>? Moved;

    /// <summary>
    /// Connection anchor for a port in graph space: inputs on the left edge, outputs on the right edge, laid out in slot rows. 
    /// 端口的连线锚点：输入在左缘，输出在右缘，按槽行布局
    /// </summary>
    public Point ViewAnchor(PortViewModel port)
    {
        var row = PortsTop + port.Index * SlotHeight + SlotHeight / 2;
        return port.IsOutput
            ? new Point(Location.X + CardWidth, Location.Y + row)
            : new Point(Location.X, Location.Y + row);
    }
}