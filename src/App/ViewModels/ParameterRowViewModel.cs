using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HalconWorkflow.Nodes.Vision.Components;

namespace HalconWorkflow.App.ViewModels;

/// <summary>
/// One reflected parameter row (§4.4). Commits typed values on LostFocus; the
/// shell routes each commit through the undo service.
/// / 一行反射参数（§4.4）。失焦时提交类型化值;壳层将每次提交经撤销服务路由。
/// </summary>
public sealed partial class ParameterRowViewModel : ObservableObject
{
    private readonly Func<string, object?, bool> _commit;
    private string _valueString = "";
    private bool _boolValue;
    private string? _selectionValue;

    public ParameterRowViewModel(ParameterMetadata meta, Func<string, object?, bool> commit)
    {
        Meta = meta ?? throw new ArgumentNullException(nameof(meta));
        _commit = commit;
        _valueString = Format(meta.Value);
        _boolValue = meta.Value is true;
        _selectionValue = meta.Value?.ToString();
    }

    /// <summary>Reflection metadata. · 反射元数据</summary>
    public ParameterMetadata Meta { get; }

    public string Name => Meta.Name;
    public string Group => Meta.Group;
    public ParameterKind Kind => Meta.Kind;
    public string? Unit => Meta.Unit;
    public string? Description => Meta.Description;
    public bool HasRange => Kind is ParameterKind.Integer or ParameterKind.Real && (Meta.Min is not null || Meta.Max is not null);
    public string RangeText => $"({Meta.Min?.ToString("0.##", CultureInfo.InvariantCulture) ?? "—"}…{Meta.Max?.ToString("0.##", CultureInfo.InvariantCulture) ?? "—"})";

    /// <summary>Editable text value (Integer/Real/String). · 可编辑文本值</summary>
    public string ValueString
    {
        get => _valueString;
        set
        {
            if (!SetProperty(ref _valueString, value)) return;
            _commit(Meta.Name, Parse(value));
        }
    }

    /// <summary>Editable boolean value. · 可编辑布尔值</summary>
    public bool BoolValue
    {
        get => _boolValue;
        set
        {
            if (!SetProperty(ref _boolValue, value)) return;
            _commit(Meta.Name, value);
        }
    }

    /// <summary>Editable selection value. · 可编辑选项值</summary>
    public string? SelectionValue
    {
        get => _selectionValue;
        set
        {
            if (!SetProperty(ref _selectionValue, value)) return;
            if (value is not null) _commit(Meta.Name, value);
        }
    }

    /// <summary>Refreshes the displayed value after an undo/redo or external change. · 撤销/重做或外部修改后刷新显示值</summary>
    public void Refresh(object? value)
    {
        _valueString = Format(value);
        OnPropertyChanged(nameof(ValueString));
        _boolValue = value is true;
        OnPropertyChanged(nameof(BoolValue));
        _selectionValue = value?.ToString();
        OnPropertyChanged(nameof(SelectionValue));
    }

    private object? Parse(string text)
        => Kind switch
        {
            ParameterKind.String => text,
            _ when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => l,
            _ when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => text
        };

    private static string Format(object? value) => value switch
    {
        null => "",
        bool b => b ? "True" : "False",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };
}