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
    {
        // The shell captures SynchronizationContext.Current at construction (_ui). xunit runs
        // async tests with its own AsyncTestSyncContext installed, so a shell built inside a test
        // body would capture it and funnel every Post (node events, run-finished log) through a
        // queued, unordered delivery path — the live-data tests then spuriously time out waiting
        // for node "done" lines under 2-vCPU load. Clearing the ambient context here gives the
        // shell _ui == null, so Post runs callbacks inline and event ordering is linear and
        // deterministic, exactly what these tests assume. Production keeps the WPF dispatcher.
        // · 壳层构造时捕获环境 SynchronizationContext(_ui)。xunit 的异步测试自带
        // AsyncTestSyncContext,在测试体内构造的壳会捕获它,使所有 Post(节点事件、运行结束日志)
        // 走排队且无序的投递路径,实况数据测试于是在 2 核负载下偶发等不到节点 done 行而超时。
        // 此处临时清空环境上下文,使 _ui==null、Post 内联执行,事件顺序线性且确定,与这些测试的
        // 前提一致;生产环境仍捕获 WPF Dispatcher 上下文,不受影响。
        var previous = System.Threading.SynchronizationContext.Current;
        System.Threading.SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            return new ShellViewModel(loc, dialogs,
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HalconWorkflow.Tests", System.Guid.NewGuid().ToString("N")));
        }
        finally
        {
            System.Threading.SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}