namespace HalconWorkflow.Abstractions;

/// <summary>
/// Marker/base for visual nodes if needed by plugins; the graph engine only depends on Core contracts. 
/// 视觉节点的可选基类/标记;图引擎只依赖 Core 契约
/// </summary>
public interface IVisionNode
{
}

/// <summary>
/// Visual engine pool: borrow-return-cancel semantics per engine instance (§6.2). 
/// 视觉引擎池：按引擎实例借出-归还-取消语义(§6.2)
/// </summary>
public interface IVisionEnginePool : IAsyncDisposable
{
    /// <summary>
    /// Borrows an engine; blocks until one is free. Returns a lease. 
    /// 借出引擎;阻塞到空闲。返回租约
    /// </summary>
    ValueTask<IVisionEngineLease> BorrowAsync(CancellationToken ct);

    /// <summary>
    /// Maximum borrowable engine instances (parallelism bound). · 最大可借出引擎数(并行度上界)
    /// </summary>
    int Capacity { get; }
}

/// <summary>
/// A borrowed engine lease; return on dispose (§5.6/§6.2). · 借出的引擎租约;Dispose 归还
/// </summary>
public interface IVisionEngineLease : IDisposable
{
    /// <summary>
    /// Engine instance; may be a OpenCV or phantom engine. Each instance is non-thread-safe. 
/// / 引擎实例;OpenCV 或 phantom 引擎。每个实例非线程安全。
    /// </summary>
    object Engine { get; }
}
