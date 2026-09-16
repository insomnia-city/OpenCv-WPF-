using System.Windows;

namespace HalconWorkflow.App.Views;

/// <summary>
/// Main shell window; DataContext is injected by App startup. 
/// 主壳窗口;DataContext 由 App 启动时注入
/// </summary>
internal sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}