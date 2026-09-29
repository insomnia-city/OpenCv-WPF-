using HalconWorkflow.App.Services;
using HalconWorkflow.App.ViewModels;

namespace HalconWorkflow.App.Tests;

/// <summary>
/// Builds ShellViewModel instances with an isolated temp data root so tests never read or
/// write the user's %LOCALAPPDATA%\HalconWorkflow directory (trace.db + devices.json).
/// A fresh root per shell keeps every test deterministic and parallel-safe on any machine.
/// · 用独立临时数据根构造 ShellViewModel,测试不读写用户 %LOCALAPPDATA%\HalconWorkflow
///   目录(trace.db + devices.json)。每个 shell 全新数据根,保证任何机器上测试确定且可并行。
/// </summary>
internal static class TestShell
{
    public static ShellViewModel Create(LocalizationService loc, IDialogService dialogs)
        => new ShellViewModel(loc, dialogs,
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HalconWorkflow.Tests", System.Guid.NewGuid().ToString("N")));
}