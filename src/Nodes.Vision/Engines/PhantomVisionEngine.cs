using HalconWorkflow.Nodes.Vision.Imaging;

namespace HalconWorkflow.Nodes.Vision.Engines;

/// <summary>
/// Pure-.NET Halcon substitute used when no licensed Halcon runtime is installed
/// (§6.3 software fallback). Deterministic, thread-unaware (single executor per
/// instance by pool contract). Op names mirror the future real-engine registry.
/// / 纯 .NET 的 Halcon 软件替代：当本机无授权的 Halcon 运行时启用（§6.3 软回退）。
///   确定性输出;操作名与将来真实引擎注册表一一对应。
/// </summary>
public sealed class PhantomVisionEngine : IVisionEngine
{
    private readonly Random _rng;
    public string Id => "phantom";

    public PhantomVisionEngine(int? seed = null)
    {
        // Fixed seed per engine so tests are reproducible. / 每引擎固定种子，保证测试可复现
        _rng = new Random(seed ?? 20240513);
    }

    public bool Supports(string op) => op is "grab" or "threshold" or "measure" or "hdev";

    public Task<object> ExecuteAsync(string op, VisionFrame? input,
        IReadOnlyDictionary<string, object>? args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return op switch
        {
            "grab" => Task.FromResult<object>(Grab(args)),
            "threshold" => Task.FromResult<object>(Threshold(input, args)),
            "measure" => Task.FromResult<object>(Measure(input)),
            "hdev" => Task.FromResult<object>(Hdev(input, args)),
            _ => throw new NotSupportedException($"phantom engine does not support op '{op}'"),
        };
    }

    /// <summary>Synthetic deterministic grayscale frame. / 确定性合成的灰度帧</summary>
    private VisionFrame Grab(IReadOnlyDictionary<string, object>? args)
    {
        int w = GetInt(args, "width", 320);
        int h = GetInt(args, "height", 240);
        var bits = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int v = 96 + (int)(64 * Math.Sin(x * Math.PI / 24.0)) + (y % 7 == 0 ? 16 : 0);
                bits[row + x] = (byte)Math.Clamp(v, 0, 255);
            }
        }
        return new VisionFrame(w, h, PixFormat.Gray8, bits, FrameDomain.Halcon);
    }

    /// <summary>Binary mask from grayscale thresholds. / 灰度阈值二值化掩膜</summary>
    private static VisionFrame Threshold(VisionFrame? input, IReadOnlyDictionary<string, object>? args)
    {
        if (input is null || input.Format != PixFormat.Gray8)
            throw new InvalidOperationException("threshold requires a Gray8 input frame");
        double min = GetDouble(args, "min", 128);
        double max = GetDouble(args, "max", 255);
        var bits = new byte[input.Bits.Length];
        for (int i = 0; i < bits.Length; i++)
            bits[i] = input.Bits[i] >= min && input.Bits[i] <= max ? (byte)255 : (byte)0;
        return new VisionFrame(input.Width, input.Height, PixFormat.Gray8, bits, FrameDomain.Halcon);
    }

    /// <summary>Rising-edge span along the middle row → measurement result. / 中间行上升沿跨度 → 测量结果</summary>
    private static Dictionary<string, object> Measure(VisionFrame? input)
    {
        if (input is null || input.Format != PixFormat.Gray8)
            throw new InvalidOperationException("measure requires a Gray8 input frame");
        int midRow = input.Height / 2;
        int first = -1, last = -1, edges = 0;
        int start = midRow * input.Width;
        for (int x = 0; x < input.Width; x++)
        {
            bool bright = input.Bits[start + x] >= 128;
            bool prevBright = x > 0 && input.Bits[start + x - 1] >= 128;
            if (bright && !prevBright)
            {
                if (first < 0) first = x;
                last = x;
                edges++;
            }
        }
        return new Dictionary<string, object>
        {
            ["Distance"] = first >= 0 ? last - first : 0,
            ["Edges"] = edges,
        };
    }

    /// <summary>Generic .hdev procedure in fallback mode via a small registry. / 软回退模式下 .hdev 通用脚本</summary>
    private static VisionFrame Hdev(VisionFrame? input, IReadOnlyDictionary<string, object>? args)
    {
        string proc = GetString(args, "procedure", "simulate_probe");
        return proc switch
        {
            "simulate_probe" => ProbeGradient(input?.Width ?? 160, input?.Height ?? 120),
            _ => throw new NotSupportedException($"fallback registry has no procedure '{proc}'"),
        };
    }

    private static VisionFrame ProbeGradient(int w, int h)
    {
        var bits = new byte[w * h];
        for (int i = 0; i < bits.Length; i++) bits[i] = (byte)(i * 255 / bits.Length);
        return new VisionFrame(w, h, PixFormat.Gray8, bits, FrameDomain.Halcon);
    }

    private static int GetInt(IReadOnlyDictionary<string, object>? args, string key, int fallback)
        => args is not null && args.TryGetValue(key, out var v) && v is IConvertible c ? c.ToInt32(null) : fallback;

    private static double GetDouble(IReadOnlyDictionary<string, object>? args, string key, double fallback)
        => args is not null && args.TryGetValue(key, out var v) && v is IConvertible c ? c.ToDouble(null) : fallback;

    private static string GetString(IReadOnlyDictionary<string, object>? args, string key, string fallback)
        => args is not null && args.TryGetValue(key, out var v) ? Convert.ToString(v) ?? fallback : fallback;

    public void Dispose() { /* phantom holds nothing */ }
}