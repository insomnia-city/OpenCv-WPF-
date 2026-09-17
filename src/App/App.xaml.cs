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
    private ShellViewModel? _shell;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var loc = AppServices.Localization;
        loc.Culture = new System.Globalization.CultureInfo("zh-Hans");

        _shell = new ShellViewModel(loc, new WindowsDialogService());
        var window = new MainWindow { DataContext = _shell };
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_shell is not null)
        {
            try
            {
                _shell.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // best-effort teardown on exit · 退出时尽力释放
            }
        }
        base.OnExit(e);
    }
}