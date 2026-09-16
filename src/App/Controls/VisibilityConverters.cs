using System.Globalization;
using System.Windows;
using System.Windows.Data;
using HalconWorkflow.Nodes.Vision.Components;

namespace HalconWorkflow.App.Controls;

/// <summary>
/// bool → Visibility; Inverse flips the mapping. · bool→Visibility; Inverse 取反
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Inverse { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var visible = value is true;
        return (visible != Inverse) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// ParameterKind → Visibility; Not inverts the match. · ParameterKind→Visibility; Not 反转匹配
/// </summary>
public sealed class ParameterKindVisibilityConverter : IValueConverter
{
    public ParameterKind Kind { get; set; }
    public bool Not { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var match = value is ParameterKind k && k == Kind;
        return (match != Not) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}