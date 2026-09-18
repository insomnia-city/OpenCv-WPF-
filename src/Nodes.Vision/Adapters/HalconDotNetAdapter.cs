using System.Reflection;
using HalconWorkflow.Nodes.Vision.Imaging;

namespace HalconWorkflow.Nodes.Vision.Adapters;

/// <summary>
/// DEPLOYMENT-ONLY Halcon adapter scaffold. This file is the field integration point:
/// it loads halcondotnet via <see cref="Assembly.LoadFrom"/> (never a compile-time
/// reference, so the library builds without Halcon) and routes the phantom-mirrored op
/// registry ("grab"/"threshold"/"measure"/"hdev") onto the real SDK. It is NOT registered
/// implicitly — the deployment site registers it explicitly, e.g.
/// <code language="text">
///   var adapter = HalconDotNetAdapter.Create(HalconProbe.TryLocateManagedAssembly());
///   if (adapter is not null) HalconAdapterRegistry.Register(() => adapter);
/// </code>
/// / 仅部署期的 Halcon 适配器骨架，现场集成点：经 Assembly.LoadFrom 惰性加载 halcondotnet
///   （无编译期引用，本库无 Halcon 亦可编译），把与幻影对齐的操作注册表
///   （"grab"/"threshold"/"measure"/"hdev"）映射到真实 SDK。绝不隐式注册——部署现场按上方
///   代码显式注册。
/// </summary>
/// <remarks>
/// UNVALIDATED SCAFFOLD: no Halcon SDK was available when this file was authored, so the
/// per-op bodies are explicit stubs that throw an actionable message. The field team
/// implements each body against the canonical HOperatorSet sequence listed in the op's
/// comment and verifies it against the installed SDK before commissioning. Op failures
/// surface as exceptions and the node faults visibly — never a silent wrong result.
/// / 未验证骨架：编写时本机无 Halcon SDK，各算子主体为「抛出可操作消息」的显式桩。现场团队按
///   各算子注释中的 HOperatorSet 正典调用序列实现，并在调试期对照已装 SDK 核实。算子失败以
///   异常上抛、节点显式故障，绝不静默造假结果。
/// </remarks>
public sealed class HalconDotNetAdapter : IHalconAdapter
{
    private readonly Assembly _assembly;
    private readonly object _acquire = new();
    private int _disposed;

    /// <inheritdoc />
    public string Provider => "halcondotnet";

    private HalconDotNetAdapter(Assembly assembly)
    {
        _assembly = assembly;
        // Bind the static HOperatorSet entry type eagerly so a wrong/missing assembly
        // classifies as "factory failed" (resolver falls back) instead of crashing later.
        // / 立即绑定静态入口类型 HOperatorSet，装配不当一律归类为「工厂失败」→ 解析器回退而非后续崩溃。
        _ = Resolve("HalconDotNet.HOperatorSet");
    }

    /// <summary>
    /// Creates the adapter when the managed assembly is present and bindable, else null.
    /// / 托管程序集存在且可绑定则创建适配器，否则返回 null。
    /// </summary>
    public static HalconDotNetAdapter? Create(string? managedPath)
    {
        if (string.IsNullOrWhiteSpace(managedPath) || !File.Exists(managedPath)) return null;
        try
        {
            return new HalconDotNetAdapter(Assembly.LoadFrom(managedPath));
        }
        catch (Exception)
        {
            return null; // unloadable → treat as absent; resolver notes the fallback and stays deterministic
        }
    }

    /// <inheritdoc />
    public bool Supports(string op) => op is "threshold" or "measure" or "hdev" or "grab";

    /// <inheritdoc />
    public Task<object> ExecuteAsync(string op, VisionFrame? input,
        IReadOnlyDictionary<string, object>? args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        object result = op switch
        {
            "threshold" => Threshold(input, args),
            "hdev" => HdevProgram(input),
            "measure" => Measure(input),
            "grab" => throw NotDeployed("grab", "HFramegrabber / ImageAcquisition + camera .dat descriptor"),
            _ => throw new NotSupportedException($"halcon adapter does not support op '{op}'"),
        };
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(result);
    }

    /// <summary>
    /// Canonical sequence for "threshold": build an HImage from raw bytes via
    /// <c>GenImage1("byte", w, h, ptr)</c>, binarize with <c>Threshold(Image, Min, Max)</c>,
    /// restore image pixels with <c>RegionToBin(Region, 255, 0, w, h)</c>, then read gray
    /// bytes back with <c>GetImagePointer1</c> into a <see cref="VisionFrame"/>.
    /// / "threshold" 正典序列：GenImage1 造图 → Threshold 二值化 → RegionToBin 还原图 →
    ///   GetImagePointer1 取灰度字节回 VisionFrame。
    /// </summary>
    private VisionFrame Threshold(VisionFrame? input, IReadOnlyDictionary<string, object>? args)
    {
        if (input is null || input.Format != PixFormat.Gray8)
            throw new InvalidOperationException("halcon threshold requires a Gray8 input frame");
        lock (_acquire)
        {
            double min = GetDouble(args, "min", 128);
            double max = GetDouble(args, "max", 255);
            _ = min; _ = max;
            // NotImplemented on purpose: implement the canonical sequence above and verify
            // against the installed SDK (operator overloads are version specific).
            // / 刻意未实现：按上方正典序列实现并对照已装 SDK 核实（算子重载随版本而异）。
            throw NotDeployed("threshold", "GenImage1 → Threshold → RegionToBin → GetImagePointer1 (§TODO)");
        }
    }

    /// <summary>
    /// Canonical sequence for "hdev": <c>HDevEngine.SetProcedurePath(progDir)</c> +
    /// <c>RunProcedure</c> over the procedure named in args["procedure"]; marshal the input
    /// frame in and the result out. / 正典序列：HDevEngine.SetProcedurePath + RunProcedure
    ///   运行 args["procedure"] 指定的 .hdev 过程;输入帧入、结果出。
    /// </summary>
    private static object HdevProgram(VisionFrame? input)
        => throw NotDeployed("hdev", "HDevEngine.SetProcedurePath + RunProcedure (§TODO)");

    /// <summary>
    /// Canonical sequence for "measure": <c>CreateMetrologyModel</c> → <c>AddMetrologyObjectCircle</c>
    /// → <c>ApplyMetrologyModel</c> → <c>GetMetrologyObjectResult</c>; return a distance dict
    /// mirroring the phantom "measure" shape (Distance/Edges).
    /// / 正典序列：CreateMetrologyModel → AddMetrologyObjectCircle → ApplyMetrologyModel →
    ///   GetMetrologyObjectResult;返回与幻影 "measure" 同构的距离字典（Distance/Edges）。
    /// </summary>
    private static object Measure(VisionFrame? input)
        => throw NotDeployed("measure", "CreateMetrologyModel + MeasurePos (§TODO)");

    private static NotSupportedException NotDeployed(string op, string where)
        => new($"halcon adapter op '{op}' is an unvalidated deployment scaffold — implement {where}.");

    private Type Resolve(string typeName)
        => _assembly.GetType(typeName, throwOnError: true)
           ?? throw new InvalidOperationException($"managed assembly lacks '{typeName}' — is halcondotnet present?");

    private static double GetDouble(IReadOnlyDictionary<string, object>? args, string key, double fallback)
        => args is not null && args.TryGetValue(key, out var v) && v is IConvertible c ? c.ToDouble(null) : fallback;

    /// <summary>Disposes the adapter exactly once. / 适配器恰好释放一次</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                _assembly.GetType("HalconDotNet.HDevEngine", throwOnError: false)
                    ?.GetMethod("Dispose")?.Invoke(null, null);
            }
            catch { /* best-effort engine teardown / 尽力释放引擎 */ }
        }
    }
}