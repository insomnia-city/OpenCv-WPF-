using HalconWorkflow.Runtime;
using HalconWorkflow.Runtime.Nodes;

namespace HalconWorkflow.Runtime;

internal static class Program
{
    private static int Main(string[] args)
    {
        RuntimeOptions options;
        try
        {
            options = RuntimeOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Console.Error.WriteLine("Usage: HalconWorkflow.Runtime <graph.json> [--once] [--cycles N] [--interval MS] [--timeout MS] [--trace path.jsonl] [--batch name] [--quiet] [--step]");
            return 1;
        }

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