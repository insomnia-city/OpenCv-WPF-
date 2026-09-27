using HalconWorkflow.Nodes.Vision.Adapters;
using HalconWorkflow.Nodes.Vision.Imaging;

namespace HalconWorkflow.Nodes.Vision.Engines;

/// <summary>
/// <see cref="IVisionEngine"/> façade over a deployment-supplied <see cref="IVisionProvider"/>.
/// Keeps the executor contract (op registry, single-threaded lease, frame/dictionary
/// results, cancellation) while the real-SDK translation stays in the adapter. The
/// resolver returns this engine only when the OpenCV native runtime is loadable AND a
/// adapter is registered; otherwise the phantom engine is the deterministic fallback.
/// / 部署供应 IVisionProvider 之上的 IVisionEngine 门面。执行契约（操作注册表、单线程租约、
///   帧/字典结果、取消）保持不变，真实 SDK 翻译全部留在适配器内。仅当「已有OpenCV 运行时
///   且已注册适配器」时解析器才会返回本引擎;否则幻影引擎为确定性回退。
/// </summary>
public sealed class OpenCvVisionEngine : IVisionEngine
{
    private readonly IVisionProvider _adapter;
    private int _disposed;

    /// <summary>Engine/module id reported to callers. / 上报给调用方的引擎/模块标识</summary>
    public string Id => _adapter.Provider;

    public OpenCvVisionEngine(IVisionProvider adapter)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    /// <inheritdoc />
    public bool Supports(string op)
        => _adapter.Supports(op);

    /// <inheritdoc />
    public Task<object> ExecuteAsync(string op, VisionFrame? input,
        IReadOnlyDictionary<string, object>? args, CancellationToken ct)
        => _adapter.ExecuteAsync(op, input, args, ct);

    /// <summary>Disposes the underlying adapter exactly once. · 底层适配器恰好释放一次</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _adapter.Dispose();
    }
}