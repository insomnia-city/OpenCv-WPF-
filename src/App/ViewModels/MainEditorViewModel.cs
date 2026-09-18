using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Graph;

namespace HalconWorkflow.App.ViewModels;

/// <summary>
/// Editor surface: mirrors a kernel GraphModel into node/connection projections and feeds edits back. 
/// 编辑器画布：将内核 GraphModel 镜像为节点/连线投影，并把编辑操作回写内核
/// </summary>
public sealed partial class MainEditorViewModel : ObservableObject
{
    private string _title = "untitled";

    /// <summary>
    /// When set by the shell, node deletion runs through the undoable command path (handler) instead of the local kernel edit.
    /// 壳层设置后，节点删除走可撤销命令路径(处理程序)而非本地内核编辑
    /// </summary>
    public Func<NodeViewModel, Task>? RemoveAsyncHandler { get; set; }

    /// <summary>
    /// When set by the shell, connection disconnection runs through the undoable command path (§9.2, stage-12).
    /// 壳层设置后，断线走可撤销命令路径(§9.2,阶段12)
    /// </summary>
    public Func<ConnectionViewModel, Task>? DisconnectAsyncHandler { get; set; }

    /// <summary>
    /// Localization lookup injected by the shell so connection projections can show localized menu labels.
    /// 壳层注入的本地化查询，使连线投影可显示本地化菜单文本
    /// </summary>
    public Func<string, string>? TextProvider { get; set; }

    /// <summary>Kernel graph the canvas edits. · 画布编辑的内核图</summary>
    public GraphModel Graph { get; private set; } = new();

    /// <summary>Node projections. · 节点投影</summary>
    public ObservableCollection<NodeViewModel> Nodes { get; } = [];

    /// <summary>Connection projections. · 连线投影</summary>
    public ObservableCollection<ConnectionViewModel> Connections { get; } = [];

    /// <summary>Window/editor title. · 窗口/编辑器标题</summary>
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    /// <summary>Replaces the whole graph from a loaded/deserialized model. · 以载入的图整体替换画布</summary>
    public void Load(GraphModel graph, string title = "untitled")
    {
        Graph = graph ?? throw new ArgumentNullException(nameof(graph));
        Title = title;
        RebindCollections();
    }

    /// <summary>Starts a fresh empty graph. · 开启空白新图</summary>
    public void New()
    {
        Graph = new GraphModel();
        Title = "untitled";
        RebindCollections();
    }

    /// <summary>Projects a kernel link set into connection VMs after structural changes. · 在内核结构变更后重建连线投影</summary>
    public void RebindConnections()
    {
        Connections.Clear();
        var byId = Nodes.ToDictionary(n => n.Kernel.Id, StringComparer.Ordinal);
        foreach (var l in Graph.Links)
        {
            if (!byId.TryGetValue(l.From.Owner.Id, out var f)
                || !byId.TryGetValue(l.To.Owner.Id, out var t)) continue;
            var fp = f.Inputs.Concat(f.Outputs).FirstOrDefault(p => ReferenceEquals(p.Kernel, l.From));
            var tp = t.Inputs.Concat(t.Outputs).FirstOrDefault(p => ReferenceEquals(p.Kernel, l.To));
            if (fp is null || tp is null) continue;
            var cvm = new ConnectionViewModel(l, f, fp, t, tp);
            cvm.DisconnectHandler = DisconnectAsyncHandler;
            cvm.DisconnectLabel = TextProvider?.Invoke("link.disconnect") ?? "Disconnect";
            Connections.Add(cvm);
        }
    }

    /// <summary>Adds a node at a location; returns its projection or null when the id collides. · 在指定坐标添加节点</summary>
    public NodeViewModel? AddNode(INode node, double x, double y)
    {
        if (!Graph.AddNode(node)) return null;
        var kernel = Graph.Nodes[node.Id];
        kernel.Position = (x, y);
        var vm = CreateNodeViewModel(kernel);
        return vm;
    }

    /// <summary>Removes a node and all its connection projections. · 移除节点及其连线段</summary>
    public void RemoveNode(NodeViewModel vm)
    {
        foreach (var c in Connections.Where(c => c.From.Id == vm.Id || c.To.Id == vm.Id).ToList())
            Connections.Remove(c);
        if (Graph.RemoveNode(vm.Id)) Nodes.Remove(vm);
    }

    private NodeViewModel CreateNodeViewModel(GraphNode kernel)
    {
        var vm = new NodeViewModel(kernel);
        vm.DeleteCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() =>
        {
            if (RemoveAsyncHandler is not null)
            {
                _ = RemoveAsyncHandler(vm);   // undoable path · 可撤销路径
            }
            else
            {
                RemoveNode(vm);
            }
        });
        Nodes.Add(vm);
        return vm;
    }

    /// <summary>
    /// Re-projects the whole kernel into projections after undo/redo structural edits. · 撤销/重做结构编辑后整体重建投影
    /// </summary>
    public void RebindAll() => RebindCollections();

    private void RebindCollections()
    {
        Nodes.Clear();
        Connections.Clear();
        foreach (var gn in Graph.Nodes.Values)
            CreateNodeViewModel(gn);
        RebindConnections();
    }
}