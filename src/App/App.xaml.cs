using System.Windows;
using HalconWorkflow.App.Services;
using HalconWorkflow.App.ViewModels;
using HalconWorkflow.App.Views;

namespace HalconWorkflow.App;

/// <summary>
/// Application shell: boots localization, the shell VM and the main window. 
/// 应用外壳：启动本地化、壳 VM 与主窗口
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var loc = AppServices.Localization;
        loc.Culture = new System.Globalization.CultureInfo("zh-Hans");

        var shell = new ShellViewModel(loc, new WindowsDialogService());
        var window = new MainWindow { DataContext = shell };
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
    }
}