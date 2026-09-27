using System.Windows;
using HalconWorkflow.App.Services;
using HalconWorkflow.App.ViewModels;
using HalconWorkflow.App.Views;
using HalconWorkflow.Nodes.Vision;
using HalconWorkflow.Nodes.Vision.Adapters;

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

        // Seed the ambient settings snapshot from appsettings.json (stage-30, §5.8); a missing or
        // corrupt file falls back to identical defaults, so startup never fails on it.
        // 依 appsettings.json 播种 ambient 设置快照(阶段30,§5.8);文件缺失/损坏回退同值默认值,启动不因此失败。
        AppServices.Settings = AppSettingsFile.Load(AppServices.SettingsPath);

        var loc = AppServices.Localization;
        loc.Culture = new System.Globalization.CultureInfo(AppServices.Settings.Culture);

        _shell = new ShellViewModel(loc, new WindowsDialogService());

        // Wire the production vision provider so the real engine is selected at runtime.
        // Registration is explicit by design (tests inject fakes through the same seam);
        // without it the factory would fall back to the simulated phantom engine.
        // 显式注册生产视觉提供器，使运行时选中真实引擎。注册按设计为显式（测试经同一接缝
        // 注入假实现）；不注册则工厂回退到仿真幻影引擎。
        if (OpenCvProbe.TryDescribeRuntime() is not null)
            VisionProviderRegistry.Register(static () => new OpenCvVisionProvider());
        else
            _shell.Log.Add("warn", $"opencv native runtime unavailable ({OpenCvProbe.FailureReason ?? "unknown"}); vision falls back to simulation");

        CrashGuard.Install(this, message => _shell?.Log.Add("error", message));
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