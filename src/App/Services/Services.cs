using HalconWorkflow.Core.Graph;
using HalconWorkflow.App.ViewModels;
using HalconWorkflow.App.Views;

namespace HalconWorkflow.App.Services;

/// <summary>
/// File/dialog seam so ViewModels stay testable without WPF windows. 
/// 文件/对话框服务接口：让 VM 可脱离窗口测试
/// </summary>
public interface IDialogService
{
    /// <summary>Asks for an existing .graph.json file; null when cancelled. · 选择已有图文件;取消返回 null</summary>
    string? OpenGraphFile(string filter);

    /// <summary>Asks where to save the graph; null when cancelled. · 选择保存路径;取消返回 null</summary>
    string? SaveGraphFile(string defaultName, string filter);

    /// <summary>Asks where to save a CSV export; null when cancelled. · 选择 CSV 导出路径;取消返回 null</summary>
    string? SaveCsvFile(string defaultName);

    /// <summary>Shows a blocking error box. · 显示阻塞式错误框</summary>
    void ReportError(string message);

    /// <summary>
    /// Blocking yes/no confirmation; true means proceed. Keeps VMs free of WPF MessageBox calls
    /// so destructive flows (new/load) stay testable (§9.2).
    /// / 阻塞式确认；true 表示继续。使 VM 不直接调用 WPF MessageBox，
    ///   让破坏性流程(新建/载入)保持可测(§9.2)。
    /// </summary>
    bool Confirm(string message);

    /// <summary>
    /// Opens the trigger-settings dialog for the graph config; true when OK was pressed, in which
    /// case the config reference was mutated. Default (test stubs) returns false without editing.
    /// · 打开触发设置对话框编辑图形配置;确定返回 true 且已改写配置。默认实现(测试桩)返回 false 且不改写。
    /// </summary>
    bool EditTrigger(TriggerConfig config) => false;

    /// <summary>
    /// Opens the settings/options dialog bound to a snapshot of the current app settings; returns
    /// the edited <see cref="AppSettings"/> on OK (the VM's Save replaces the caller's init-only record
    /// instance — the inverse of the mutating trigger dialog), null on Cancel. Default (test stubs)
    /// returns null without editing, mirroring <see cref="EditTrigger"/>.
    /// · 打开绑定到当前应用设置快照的设置/选项对话框;确定返回编辑后的 AppSettings(VM 的保存以
    ///   init-only record 实例替换调用方实例——与改写式触发对话框相反),取消返回 null。默认(测试桩)
    ///   返回 null 且不进行编辑,与 EditTrigger 对偶。
    /// </summary>
    AppSettings? EditSettings(AppSettings? current) => null;

    /// <summary>
    /// Opens an independent image window bound to the given view model (§4.5, stage-24).
    /// · 打开绑定到给定视图模型的独立图像窗(§4.5,阶段24)
    /// </summary>
    void ShowImageWindow(ImageWindowViewModel vm);
}

/// <summary>
/// WPF-backed dialog implementation. · WPF 对话框实现
/// </summary>
public sealed class WindowsDialogService : IDialogService
{
    public string? OpenGraphFile(string filter)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = filter, CheckFileExists = true };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public string? SaveGraphFile(string defaultName, string filter)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = filter, FileName = defaultName };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public string? SaveCsvFile(string defaultName)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*",
            FileName = defaultName
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public void ReportError(string message) => System.Windows.MessageBox.Show(message, "Halcon Workflow", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);

    public bool Confirm(string message) => System.Windows.MessageBox.Show(message, "Halcon Workflow",
        System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.OK;

    public bool EditTrigger(TriggerConfig config)
    {
        var vm = new TriggerSettingsViewModel(config, AppServices.Localization);
        var dlg = new TriggerSettingsDialog(vm);
        return dlg.ShowDialog() == true;
    }

    public AppSettings? EditSettings(AppSettings? current)
    {
        var vm = new SettingsViewModel(AppServices.Localization, current);
        var dlg = new SettingsDialog(vm);
        return dlg.ShowDialog() == true ? vm.Result : null;
    }

    public void ShowImageWindow(ImageWindowViewModel vm)
    {
        var dlg = new Views.ImageWindow(vm);
        dlg.Show();
    }
}

/// <summary>
/// Process-wide ambient services shared by markup extensions and startup. 
/// 进程级 Ambient 服务(供标记扩展与启动使用)
/// </summary>
public static class AppServices
{
    /// <summary>UI localization service. · UI 本地化服务</summary>
    public static Services.LocalizationService Localization { get; set; } = new();

    /// <summary>
    /// Application settings snapshot shared by markup and startup (stage-30, §5.8 appsettings.json).
    /// Seeded in App.OnStartup from <see cref="SettingsPath"/>; VMs replace it via the settings dialog.
    /// · 由标记扩展与启动共享的应用设置快照(阶段30,§5.8 appsettings.json)。App.OnStartup 依
    ///   SettingsPath 播种;VM 通过设置对话框以 record 快照替换。
    /// </summary>
    public static AppSettings Settings { get; set; } = AppSettingsFile.Defaults;

    /// <summary>
    /// Canonical appsettings.json path (user-local, survives restarts, §5.8). · 规范的
    /// appsettings.json 路径(用户级,跨重启保留,§5.8)
    /// </summary>
    public static string SettingsPath => AppSettingsFile.PathFor(
        System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "HalconWorkflow", "config"));
}