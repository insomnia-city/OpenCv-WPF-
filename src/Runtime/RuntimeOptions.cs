namespace HalconWorkflow.Runtime;

/// <summary>
/// Parsed command-line options for the headless runtime. · 无头运行时的命令行参数
/// </summary>
public sealed class RuntimeOptions
{
    public string GraphFile { get; set; } = "";
    public bool Once { get; set; }
    public int Cycles { get; set; }                       // 0 = unlimited (loop mode) · 0 = 不限次数(循环)
    public int IntervalMs { get; set; } = 1000;           // timer period between triggers · 定时触发周期
    public int? TimeoutMs { get; set; }                   // hard cancel timeout for a cycle · 整轮硬超时
    public string? TraceFile { get; set; }
    public string? Batch { get; set; }
    public string Factory { get; set; } = "sample";
    public bool Quiet { get; set; }
    public bool Step { get; set; }

    /// <summary>
    /// Parses argv. Throws ArgumentException on bad usage. · 解析 argv;用法错误抛 ArgumentException
    /// </summary>
    public static RuntimeOptions Parse(string[] args)
    {
        var o = new RuntimeOptions();
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--once": o.Once = true; break;
                case "--quiet": o.Quiet = true; break;
                case "--step": o.Step = true; break;
                case "--cycles": o.Cycles = ParsePositive(args, ref i, a); break;
                case "--interval": o.IntervalMs = ParsePositive(args, ref i, a); break;
                case "--timeout": o.TimeoutMs = ParsePositive(args, ref i, a); break;
                case "--trace": o.TraceFile = Next(args, ref i, a); break;
                case "--batch": o.Batch = Next(args, ref i, a); break;
                case "--factory": o.Factory = Next(args, ref i, a); break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException($"Unknown option '{a}'.");
                    positional.Add(a);
                    break;
            }
        }

        if (positional.Count != 1)
            throw new ArgumentException("Expected exactly one graph.json path.");
        o.GraphFile = positional[0];
        if (o.Once && o.Cycles > 0)
            throw new ArgumentException("--once and --cycles are mutually exclusive.");

        return o;
    }

    private static string Next(string[] args, ref int i, string opt)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"Option '{opt}' requires a value.");
        return args[++i];
    }

    private static int ParsePositive(string[] args, ref int i, string opt)
    {
        var raw = Next(args, ref i, opt);
        if (!int.TryParse(raw, out var v) || v < 0)
            throw new ArgumentException($"Option '{opt}' expects a non-negative integer, got '{raw}'.");
        return v;
    }
}