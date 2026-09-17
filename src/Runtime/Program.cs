using HalconWorkflow.Runtime;
using HalconWorkflow.Runtime.Nodes;

namespace HalconWorkflow.Runtime;

/// <summary>
/// Headless entry point: parse CLI options, run one graph to completion, exit with its code (§13 stage-2).
/// 无头入口：解析命令行参数，执行一次图并以其退出码结束(§13 阶段2)。
/// </summary>
internal static class Program
{
    /// <summary>Parses arguments, hosts the graph, and maps failures to exit code 1. · 解析参数、承载图，失败映射为退出码 1</summary>
    private static int Main(string[] args)
    {
        RuntimeOptions options;
        try
        {
            options = RuntimeOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            // Bad CLI usage: print the reason plus the accepted flags, then fail fast. · 命令行用法错误：打印原因与受支持开关后快速失败
            Console.Error.WriteLine($"Error: {ex.Message}");
            Console.Error.WriteLine("Usage: HalconWorkflow.Runtime <graph.json> [--once] [--cycles N] [--interval MS] [--timeout MS] [--trace path.jsonl] [--batch name] [--quiet] [--step]");
            return 1;
        }

        // Ctrl+C requests cooperative cancellation rather than killing the process. · Ctrl+C 请求协作式取消而非直接终止进程
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var host = new HeadlessHost(options);
        try
        {
            var exit = host.RunAsync(cts.Token, Console.Error.WriteLine).GetAwaiter().GetResult();
            return exit;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal: {ex.Message}");
            return 1;
        }
    }
}