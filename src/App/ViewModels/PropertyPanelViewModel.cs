using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HalconWorkflow.Abstractions.Parameters;
using HalconWorkflow.App.Services;
using HalconWorkflow.Core.Contracts;

namespace HalconWorkflow.App.ViewModels;

/// <summary>
/// Property panel for the selected node (§4.4): reflects IParameterized parameter
/// objects into editable rows. Commits are relayed to the shell which routes them
/// through the undo service. Bindings hit projections only.
/// / 选中节点的属性面板（§4.4）：将 IParameterized 参数对象反射为可编辑行。
///   提交事件发给壳层,由壳层经撤销服务路由。绑定只指投影。
/// </summary>
public sealed partial class PropertyPanelViewModel : ObservableObject
{
    private readonly LocalizationService _loc;
    private object? _parameterObject;
    private string _header = "";
    private bool _hasTarget;

    public PropertyPanelViewModel(LocalizationService loc)
    {
        _loc = loc;
        _loc.PropertyChanged += (_, _) => OnPropertyChanged((string?)null);
    }

    /// <summary>Raised on row commit; the shell applies it as an undoable SetParameterCommand. · 行提交时触发;壳层以可撤销命令应用</summary>
    public event Action<string, object>? ParameterCommitted;

    public string Title => _loc["property.title"];
    public string EmptyText => _loc["property.empty"];
    public string Header => _header;
    public bool HasTarget => _hasTarget;

    /// <summary>Editable rows projected from the parameter metadata. · 从参数元数据投影的可编辑行</summary>
    public ObservableCollection<ParameterRowViewModel> Rows { get; } = [];

    /// <summary>Shows parameters for a node (or clears when it has none). Runs on UI thread. · 显示节点参数(无参数时清空);在 UI 线程调用</summary>
    public void Show(INode node, string header)
    {
        _header = header;
        OnPropertyChanged(nameof(Header));
        Rows.Clear();
        if (node is IParameterized ip)
        {
            _parameterObject = ip.ParameterObject;
            foreach (var meta in ParameterReflection.Summarize(_parameterObject))
                Rows.Add(new ParameterRowViewModel(meta, OnRowCommitted));
            _hasTarget = true;
        }
        else
        {
            _parameterObject = null;
            _hasTarget = false;
        }
        OnPropertyChanged(nameof(HasTarget));
    }

    /// <summary>Clears the panel (empty canvas click). · 清空面板(点击空白画布)</summary>
    public void Clear()
    {
        _header = "";
        _parameterObject = null;
        _hasTarget = false;
        Rows.Clear();
        OnPropertyChanged(nameof(HasTarget));
        OnPropertyChanged(nameof(Header));
    }

    /// <summary>Refreshes displayed values after an undo/redo replayed a change. · 撤销/重做回放变更后刷新显示值</summary>
    public void Refresh()
    {
        if (!_hasTarget || _parameterObject is null) return;
        foreach (var row in Rows)
        {
            var meta = ParameterReflection.Summarize(_parameterObject).FirstOrDefault(m => m.Name == row.Name);
            if (meta is not null) row.Refresh(meta.Value);
        }
    }

    private bool OnRowCommitted(string name, object? value)
    {
        if (value is null) return false;
        ParameterCommitted?.Invoke(name, value);
        return true;
    }
}