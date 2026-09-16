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

    /// <summary>Shows a blocking error box. · 显示阻塞式错误框</summary>
    void ReportError(string message);
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

    public void ReportError(string message) => System.Windows.MessageBox.Show(message, "Halcon Workflow", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
}

/// <summary>
/// Process-wide ambient services shared by markup extensions and startup. 
/// 进程级 Ambient 服务(供标记扩展与启动使用)
/// </summary>
public static class AppServices
{
    /// <summary>UI localization service. · UI 本地化服务</summary>
    public static Services.LocalizationService Localization { get; set; } = new();
}