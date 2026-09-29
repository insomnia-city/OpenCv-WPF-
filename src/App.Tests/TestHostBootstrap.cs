using System.Runtime.CompilerServices;
using System.Threading;

namespace HalconWorkflow.App.Tests;

/// <summary>
/// Bumps the CLR thread-pool minimums for the test host. The Windows-2022 runner has 2 vCPUs and
/// xunit runs test collections in parallel, so the scheduler's background consume-loop Task plus
/// per-node awaits and the tests' own Task.Delay continuations can starve for seconds under load —
/// seen as LiveDataFlow "run did not finish within 5s" only on CI. Raising the floor lets the pool
/// spin up workers immediately instead of throttled thread injection.
/// · 抬升测试宿主线程池下限。Windows-2022 runner 仅 2 vCPU 且 xunit 并行跑测试集合,调度器的
///   后台消费循环任务、逐节点 await 与测试自身的 Task.Delay 续延在负载下可被饿数秒——表现为
///   仅在 CI 偶发的 "run did not finish within 5s"。调高下限使线程池立即可注入足够工作者,
///   不再受节流式缓慢注入的影响。
/// </summary>
internal static class TestHostBootstrap
{
    [ModuleInitializer]
    public static void Init()
    {
        var workers = Math.Max(16, Environment.ProcessorCount * 4);
        ThreadPool.SetMinThreads(workers, workers);
    }
}