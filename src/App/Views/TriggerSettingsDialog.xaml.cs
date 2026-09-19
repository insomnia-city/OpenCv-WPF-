using System.Windows;
using HalconWorkflow.App.ViewModels;

namespace HalconWorkflow.App.Views;

/// <summary>
/// Trigger-settings dialog (§5.4, stage-20): commits on OK, leaves the config untouched on Cancel. 
/// · 触发设置对话框(§5.4,阶段20)：确定时提交;取消不动配置
/// </summary>
internal sealed partial class TriggerSettingsDialog : Window
{
    private readonly TriggerSettingsViewModel _vm;

    public TriggerSettingsDialog(TriggerSettingsViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        _vm.Apply();
        DialogResult = true;
    }
}