using HalconWorkflow.Abstractions;
using HalconWorkflow.Nodes.Vision.Engines;
using Xunit;

namespace HalconWorkflow.Nodes.Vision.Tests;

/// <summary>
/// Stage-5 gate 1: VisionEnginePool borrow-return-cancel assertions. The pool is
/// exercised with the deterministic phantom engine; semantics are engine-agnostic.
/// / 阶段 5 闸门 1：引擎池借出-归还-取消断言。以确定性幻影引擎验�?语义与真实引擎无关�?/// </summary>
public class EnginePoolTests
{
    private static VisionEnginePool NewPool(int capacity) => new(() => new PhantomVisionEngine(), capacity);

    [Fact]
    public async Task Borrow_Returns_ReusesSameEngine()
    {
        await using var pool = NewPool(2);
        var a = await pool.BorrowAsync(CancellationToken.None);
        var first = a.Engine;
        a.Dispose();

        var b = await pool.BorrowAsync(CancellationToken.None);
        Assert.Same(first, b.Engine); // returned engine is reused / 归还的引擎被复用
        b.Dispose();
    }

    [Fact]
    public async Task ConcurrentBorrows_AreCappedByCapacity()
    {
        await using var pool = NewPool(3);
        var leases = new List<IVisionEngineLease>();
        for (var i = 0; i < 3; i++)
            leases.Add(await pool.BorrowAsync(CancellationToken.None));

        var distinct = leases.Select(l => l.Engine).Distinct().Count();
        Assert.Equal(3, distinct); // exactly 3 distinct instances · 恰为 3 个不同实例
        foreach (var l in leases) l.Dispose();

        var again = await pool.BorrowAsync(CancellationToken.None);
        Assert.NotNull(again.Engine); // slot freed after return · 归还后槽位释放
        again.Dispose();
    }

    [Fact]
    public async Task PreCancelledBorrow_Throws_WithoutConsumingSlot()
    {
        await using var pool = NewPool(1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await pool.BorrowAsync(cts.Token));

        // The cancelled borrow must not leak a slot: a fresh borrow succeeds. / 取消的借取不得泄漏槽位
        using var ok = await pool.BorrowAsync(CancellationToken.None);
        Assert.NotNull(ok.Engine);
    }

    [Fact]
    public async Task Cancel_WhileWaitingForSlot_Throws_AndLaterBorrowWorks()
    {
        await using var pool = NewPool(1);
        var held = await pool.BorrowAsync(CancellationToken.None); // occupy the only slot

        using var cts = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await pool.BorrowAsync(cts.Token));

        held.Dispose(); // free the slot
        using var after = await pool.BorrowAsync(CancellationToken.None);
        Assert.NotNull(after.Engine);
    }

    [Fact]
    public async Task FactoryFailure_ReturnsSlot()
    {
        var calls = 0;
        IVisionEngine Factory()
        {
            Interlocked.Increment(ref calls);
            return calls == 1
                ? throw new InvalidOperationException("transient factory failure")
                : new PhantomVisionEngine();
        }

await using var pool = new VisionEnginePool(Factory, 1);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await pool.BorrowAsync(CancellationToken.None));

        using var lease = await pool.BorrowAsync(CancellationToken.None); // slot was returned · 槽位已归还
        Assert.NotNull(lease.Engine);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Capacity_IsExposed()
    {
        await using var pool = NewPool(4);
        Assert.Equal(4, pool.Capacity);
    }
}
