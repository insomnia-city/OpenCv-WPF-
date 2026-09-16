using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using HalconWorkflow.App.ViewModels;
using HalconWorkflow.Core.Model;

namespace HalconWorkflow.App.Controls;

/// <summary>
/// Picks the node card template by contract family (start / suspended / default). 
/// 按契约族选择节点卡片模板(起点/挂起/默认)
/// </summary>
public sealed class NodeTemplateSelector : DataTemplateSelector
{
    /// <summary>Standard card. · 标准卡片</summary>
    public required DataTemplate DefaultTemplate { get; set; }

    /// <summary>Start-card (distinct accent + run badge). · 起点卡片</summary>
    public required DataTemplate StartTemplate { get; set; }

    /// <summary>Suspended-card with suspension reason. · 挂起卡片</summary>
    public required DataTemplate SuspendedTemplate { get; set; }

    /// <inheritdoc />
    public override DataTemplate SelectTemplate(object item, DependencyObject container)
    {
        if (item is not NodeViewModel vm) return DefaultTemplate;
        if (vm.IsSuspended) return SuspendedTemplate;
        if (vm.Kernel.Contract.Namespace.StartsWith("test.start", StringComparison.Ordinal)) return StartTemplate;
        return DefaultTemplate;
    }
}

/// <summary>
/// Colors a node's header band by contract family. · 按契约族为节点头部上色
/// </summary>
public sealed class AccentToBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, Brush> FamilyColors = new(StringComparer.Ordinal)
    {
        ["start"] = new SolidColorBrush(Color.FromRgb(0x7C, 0x4D, 0xFF)),
        ["grabber"] = new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3)),
        ["threshold"] = new SolidColorBrush(Color.FromRgb(0x00, 0xBC, 0xD4)),
        ["decision"] = new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00)),
        ["result"] = new SolidColorBrush(Color.FromRgb(0x43, 0xA0, 0x47)),
        ["delay"] = new SolidColorBrush(Color.FromRgb(0x60, 0x7D, 0x8B))
    };

    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is string header && FamilyColors.TryGetValue(header, out var brush)) return brush;
        return new SolidColorBrush(Color.FromRgb(0x54, 0x63, 0x6E));
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Colors execution-state indicators (status dot / card overlay). · 为执行状态指示器(状态点/卡片遮罩)上色
/// </summary>
public sealed class StateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        return value is NodeState s
            ? s switch
            {
                NodeState.Running => new SolidColorBrush(Colors.Gold),
                NodeState.Succeeded => new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),
                NodeState.Faulted => new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)),
                NodeState.Cancelled => new SolidColorBrush(Color.FromRgb(0x90, 0xA4, 0xAE)),
                _ => new SolidColorBrush(Color.FromRgb(0x37, 0x47, 0x4F))
            }
            : new SolidColorBrush(Color.FromRgb(0x37, 0x47, 0x4F));
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Port-coloring by kind: exec amber, data blue. · 端口着色：控制流琥珀色，数据流蓝色
/// </summary>
public sealed class PortKindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => value is bool isExec && isExec
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F))
            : new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7));

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}