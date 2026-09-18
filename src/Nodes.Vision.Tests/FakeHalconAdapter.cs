using HalconWorkflow.Nodes.Vision.Adapters;
using HalconWorkflow.Nodes.Vision.Engines;
using HalconWorkflow.Nodes.Vision.Imaging;

namespace HalconWorkflow.Nodes.Vision.Tests;

/// <summary>
/// Deterministic in-memory <see cref="IHalconAdapter"/> for resolver/engine tests. Counts
/// calls so proxy routing and dispose semantics are observable without any Halcon SDK.
/// / 供解析器/引擎测试的确定性内存 IHalconAdapter。统计调用次数，使代理路由与释放语义在
///   无任何 Halcon SDK 时也可观测。
/// </summary>
internal sealed class FakeHalconAdapter : IHalconAdapter
{
    public string Provider { get; init; } = "fake-halcon";
    public bool SupportsAll { get; init; } = true;
    private int _disposed;
    public int DisposedCount => _disposed;
    public int ExecuteCount { get; private set; }
    public Exception? Fault { get; set; }
    public CancellationToken SeenToken { get; private set; }
    public (string Op, VisionFrame? Input, IReadOnlyDictionary<string, object>? Args)? LastCall { get; private set; }

    public bool Supports(string op) => SupportsAll;

    public Task<object> ExecuteAsync(string op, VisionFrame? input,
        IReadOnlyDictionary<string, object>? args, CancellationToken ct)
    {
        ExecuteCount++;
        SeenToken = ct;
        LastCall = (op, input, args);
        if (Fault is not null) throw Fault;
        return Task.FromResult<object>(
            new VisionFrame(4, 4, PixFormat.Gray8, new byte[16], FrameDomain.Halcon));
    }

    public void Dispose() => Interlocked.Increment(ref _disposed);
}