using HalconWorkflow.Nodes.Vision.Imaging;
using OpenCvSharp;

namespace HalconWorkflow.Nodes.Vision.Adapters;

/// <summary>
/// Production vision provider backed by OpenCV (OpenCvSharp). Replaces the previous
/// OpenCV deployment provider: the SDK ships as a NuGet package with x64/x86
/// native runtimes, so there is no machine probe and no license to locate — the only
/// failure mode is the native runtime failing to load, which <see cref="OpenCvProbe"/>
/// reports. / 生产视觉提供器：基于 OpenCV（OpenCvSharp），取代OpenCV 部署提供器。
///   真实 SDK 以 NuGet 形式随包分发（含 x64/x86 原生运行时），因此无需机器探测、
///   无需查找授权；唯一失败模式是原生运行时加载失败，由 OpenCvProbe 上报。
/// </summary>
/// <remarks>
/// Not thread-safe: exactly one executor uses one provider at a time (pool lease
/// contract, same as <see cref="IVisionEngine"/>). Failures surface as exceptions —
/// an unavailable camera throws rather than fabricating an image.
/// / 实例非线程安全：同一时刻仅一个执行线程使用（池租约契约，同 IVisionEngine）。
///   失败以异常上抛——相机不可用时抛错，绝不伪造图像。
/// </remarks>
public sealed class OpenCvVisionProvider : IVisionProvider
{
    /// <inheritdoc />
    public string Provider => "opencv";

    /// <inheritdoc />
    public bool Supports(string op) => op is "grab" or "threshold" or "measure";

    /// <inheritdoc />
    public Task<object> ExecuteAsync(string op, VisionFrame? input,
        IReadOnlyDictionary<string, object>? args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        object result = op switch
        {
            "grab" => Grab(args),
            "threshold" => Threshold(input, args),
            "measure" => Measure(input),
            _ => throw new NotSupportedException($"opencv provider does not support op '{op}'"),
        };
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(result);
    }

    /// <summary>
    /// Acquires one frame from a real capture device. Accepts "index" (camera ordinal,
    /// default 0) or "path" (a video file). Never fabricates a frame: an unopenable
    /// device throws with an actionable message.
    /// / 从真实采集设备取一帧：接受 "index"（相机序号，默认 0）或 "path"（视频文件）。
    ///   绝不伪造帧：设备打不开则抛出可操作的消息。
    /// </summary>
    private static VisionFrame Grab(IReadOnlyDictionary<string, object>? args)
    {
        string path = GetString(args, "path", "");
        using var capture = path.Length > 0
            ? new VideoCapture(path, VideoCaptureAPIs.ANY)
            : new VideoCapture(GetInt(args, "index", 0), VideoCaptureAPIs.ANY);

        if (!capture.IsOpened())
        {
            string what = path.Length > 0 ? $"file '{path}'" : $"camera index {GetInt(args, "index", 0)}";
            throw new InvalidOperationException(
                $"opencv grab: cannot open {what}. Check the device/driver, or supply args[\"path\"] with a readable video file.");
        }

        using var frame = new Mat();
        if (!capture.Read(frame) || frame.Empty())
            throw new InvalidOperationException("opencv grab: capture returned no frame (device opened but delivered nothing).");

        // Normalize to Gray8 so downstream ops keep one frame shape across providers.
        // 归一化为 Gray8，使下游算子在各提供器间保持同一帧形状。
        if (frame.Channels() == 3)
        {
            using var gray = new Mat();
            Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
            return ToFrame(gray);
        }
        if (frame.Channels() == 1)
            return ToFrame(frame);
        if (frame.Channels() == 4)
        {
            using var gray = new Mat();
            Cv2.CvtColor(frame, gray, ColorConversionCodes.BGRA2GRAY);
            return ToFrame(gray);
        }
        throw new InvalidOperationException($"opencv grab: unsupported channel count {frame.Channels()}.");
    }

    /// <summary>
    /// Binary mask from grayscale thresholds, matching the phantom engine's contract:
    /// a pixel is 255 when min &lt;= gray &lt;= max, else 0.
    /// / 灰度阈值二值化掩膜，契约与幻影引擎一致：min ≤ 灰度 ≤ max 输出 255，否则 0。
    /// </summary>
    private static VisionFrame Threshold(VisionFrame? input, IReadOnlyDictionary<string, object>? args)
    {
        if (input is null || input.Format != PixFormat.Gray8)
            throw new InvalidOperationException("opencv threshold requires a Gray8 input frame");

        double min = GetDouble(args, "min", 128);
        double max = GetDouble(args, "max", 255);

        using var src = ToMat(input);
        using var mask = new Mat();
        // InRange, not Threshold: ThresholdTypes.Binary compares against `thresh` only
        // (max is ignored), while the contract is the two-sided closed window
        // min <= gray <= max. InRange matches it exactly.
        // 用 InRange 而非 Threshold：ThresholdTypes.Binary 只与 thresh 比较（max 被忽略），
        // 而契约是双侧闭窗口 min ≤ 灰度 ≤ max，InRange 精确对应。
        Cv2.InRange(src, new Scalar(min), new Scalar(max), mask);
        return ToFrame(mask);
    }

    /// <summary>
    /// Connected-component measurement over a binarized frame, returning the same
    /// Distance/Edges dictionary shape the phantom engine produced, so nodes and tests
    /// stay provider-agnostic. / 对二值化帧做连通域测量，返回与幻影引擎同构的
    ///   Distance/Edges 字典，使节点与测试保持与提供器无关。
    /// </summary>
    private static Dictionary<string, object> Measure(VisionFrame? input)
    {
        if (input is null || input.Format != PixFormat.Gray8)
            throw new InvalidOperationException("opencv measure requires a Gray8 input frame");

        // 128 mirrors the phantom engine's mid-gray split, so both providers agree.
        // 128 与幻影引擎的中灰分割保持一致，两个提供器结果可比。
        const double level = 128;
        using var src = ToMat(input);
        using var bin = new Mat();
        // Bright (255) stays foreground so connected components count the object.
        // ThresholdTypes.Binary is `src > thresh`, so thresh = level - 1 yields
        // `src >= level`, matching the phantom's inclusive split.
        // ThresholdTypes.Binary 语义为 `src > thresh`，故 thresh = level - 1 等价
        // `src >= level`，与幻影引擎的闭区间分割一致。
        Cv2.Threshold(src, bin, level - 1, 255, ThresholdTypes.Binary);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();

        int count = Cv2.ConnectedComponentsWithStats(bin, labels, stats, centroids,
            PixelConnectivity.Connectivity8);

        int left = int.MaxValue, right = int.MinValue;
        for (int i = 1; i < count; i++) // 0 is the background label
        {
            int l = stats.At<int>(i, (int)ConnectedComponentsTypes.Left);
            int w = stats.At<int>(i, (int)ConnectedComponentsTypes.Width);
            if (l < left) left = l;
            if (l + w - 1 > right) right = l + w - 1;
        }

        return new Dictionary<string, object>
        {
            ["Distance"] = left <= right ? right - left : 0,
            ["Edges"] = Math.Max(0, count - 1),
        };
    }

    /// <summary>Wraps frame bytes in a continuous CV_8UC1 Mat. / 将帧字节包装为连续 CV_8UC1 Mat</summary>
    private static Mat ToMat(VisionFrame frame)
    {
        var mat = new Mat(frame.Height, frame.Width, MatType.CV_8UC1);
        frame.Bits.AsSpan().CopyTo(mat.AsSpan<byte>());
        return mat;
    }

    /// <summary>Reads a CV_8UC1 Mat back into a Mat-domain frame. / 将 CV_8UC1 Mat 读回 Mat 域帧</summary>
    private static VisionFrame ToFrame(Mat mat)
    {
        var bits = new byte[mat.Width * mat.Height];
        mat.AsSpan<byte>().CopyTo(bits.AsSpan());
        return new VisionFrame(mat.Width, mat.Height, PixFormat.Gray8, bits, FrameDomain.Mat);
    }

    private static int GetInt(IReadOnlyDictionary<string, object>? args, string key, int fallback)
        => args is not null && args.TryGetValue(key, out var v) && v is IConvertible c ? c.ToInt32(null) : fallback;

    private static double GetDouble(IReadOnlyDictionary<string, object>? args, string key, double fallback)
        => args is not null && args.TryGetValue(key, out var v) && v is IConvertible c ? c.ToDouble(null) : fallback;

    private static string GetString(IReadOnlyDictionary<string, object>? args, string key, string fallback)
        => args is not null && args.TryGetValue(key, out var v) ? Convert.ToString(v) ?? fallback : fallback;

    /// <summary>Releases nothing: every native handle is scoped per call. / 不持有资源：原生句柄均按调用作用域释放</summary>
    public void Dispose() { }
}
