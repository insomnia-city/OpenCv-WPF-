using System.Windows;
using HalconWorkflow.App.ViewModels;

namespace HalconWorkflow.App.Views;

/// <summary>
/// Settings/options dialog (§5.8, stage-30): commits the edited snapshot on OK, leaves the ambient
/// untouched on Cancel. · 设置/选项对话框(§5.8,阶段30):确定时提交编辑快照;取消不动 ambient
/// </summary>
internal sealed partial class SettingsDialog : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsDialog(SettingsViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
    }
}
