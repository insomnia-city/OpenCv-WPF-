using System.Windows;
using System.Windows.Threading;
using HalconWorkflow.App.ViewModels;

namespace HalconWorkflow.App.Views;

/// <summary>
/// Independent image window (stage-24) bound to an <see cref="ImageWindowViewModel"/>.
/// Drives the VM's <see cref="ImageWindowViewModel.Tick"/> from a dispatcher timer so frame
/// draining happens on the UI thread and never back-pressures the execution thread (§4.5).
/// · 独立图像窗(阶段24)，绑定 ImageWindowViewModel。由调度器计时器驱动 Tick()，帧排空在
///   UI 线程完成，绝不反压执行线程(§4.5)。
/// </summary>
internal partial class ImageWindow : Window
{
    private readonly DispatcherTimer _render = new() { Interval = TimeSpan.FromMilliseconds(33) };

    /// <summary>Creates a window bound to the view model; the VM is opened/closed by the caller.
    /// · 创建绑定到视图模型的窗口；VM 的开关由调用方负责</summary>
    public ImageWindow(ImageWindowViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        _render.Tick += (_, _) => vm.Tick();
        _render.Start();
        Closed += (_, _) => _render.Stop();
    }
}