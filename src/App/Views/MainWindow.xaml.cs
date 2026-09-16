using System.Windows;
using HalconWorkflow.App.ViewModels;
using Nodify;

namespace HalconWorkflow.App.Views;

/// <summary>
/// Main shell window; DataContext is injected by App startup.
/// Handles container selection only (view wiring, no business logic); the shell
/// routes the picked NodeViewModel into the property panel.
/// 主壳窗口;DataContext 由 App 启动时注入。
/// 仅处理容器选择(视图接线,无业务逻辑);壳层将选中的 NodeViewModel 路由到属性面板。
/// </summary>
internal sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnItemSelected(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ItemContainer { DataContext: NodeViewModel vm })
            ((ShellViewModel)DataContext).SelectNode(vm);
    }

    private void OnItemUnselected(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ItemContainer { DataContext: NodeViewModel vm })
            ((ShellViewModel)DataContext).DeselectNode(vm);
    }
}