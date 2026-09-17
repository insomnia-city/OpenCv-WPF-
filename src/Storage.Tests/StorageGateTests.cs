using System.Diagnostics;
using System.Text;
using HalconWorkflow.Abstractions;
using HalconWorkflow.Storage;
using Xunit;

namespace HalconWorkflow.Storage.Tests;

/// <summary>
/// A sink that blocks on an in-test gate, used to prove the hot path never waits on I/O. 
/// / 由测试门控制的阻塞接收器，用于证明热路径绝不等待 I/O。
/// </summary>
internal sealed class BlockingSink : ITraceSink
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Batches;
    public int Records;

    public void Release() => _gate.TrySetResult();

    public async Task WriteBatchAsync(IReadOnlyList<TraceRecord> batch, CancellationToken ct)
    {
        await _gate.Task.WaitAsync(ct).ConfigureAwait(false);
        Interlocked.Increment(ref Batches);
        Interlocked.Add(ref Records, batch.Count);
    }
}

/// <summary>
/// Stage-8 gate (§13.1): batch writes never block execution, trace queries cancel, CSV export
/// streams page-by-page and cancels, and SQLite round-trips. 
/// / 阶段 8 门(§13.1)：批量写不阻塞执行、追溯查询可取消、CSV 流式分页且可取消、SQLite 往返。
/// </summary>
public class StorageGateTests
{
    private static string TempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"halcon-store-{Guid.NewGuid():N}.db");

    private static DbConfig Sqlite(string path) =>
        new("test", DbProviderKind.Sqlite, path == ":memory:" ? "Data Source=:memory:" : $"Data Source={path};Pooling=False");

    [Fact]
    public async Task AppendAsync_ReturnsImmediately_WhileSinkIsBlocked()
    {
        var sink = new BlockingSink();
        await using var storage = new SqlStorage(Sqlite(":memory:"), sink: sink);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 500; i++)
            await storage.AppendAsync(new TraceRecord($"trig{i}", "data.write:1", "measure"), CancellationToken.None);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"hot path blocked for {sw.Elapsed}");
        Assert.Equal(500, storage.Queue.EnqueuedCount);
        Assert.Equal(0, sink.Records);          // sink never ran: the pump had no chance to block us
        Assert.Equal(0, storage.Queue.WrittenCount);
    }

    [Fact]
    public async Task Queue_Overflow_DropsOldest_AndRaisesAlarm()
    {
        var sink = new BlockingSink();
        var queue = new TraceWriteQueue(sink, new TraceQueueOptions { Capacity = 10, BatchSize = 5 });
        long lastAlarm = 0;
        queue.DroppedAlarm += n => lastAlarm = n;

        for (var i = 0; i < 25; i++)
            queue.Enqueue(new TraceRecord(i.ToString(), "nn", "measure"));

        Assert.Equal(25, queue.EnqueuedCount);
        Assert.Equal(15, queue.DroppedCount);
        Assert.Equal(15, lastAlarm);
        await queue.DisposeAsync();
    }

    [Fact]
    public async Task FlushAsync_Persists_AndReadAllAsync_RoundTrips()
    {
        var path = TempDbPath();
        try
        {
            await using var storage = new SqlStorage(Sqlite(path));
            await storage.InitializeAsync(CancellationToken.None);
            await storage.InitializeAsync(CancellationToken.None); // idempotent migration · 迁移幂等

            for (var i = 0; i < 250; i++)
            {
                await storage.AppendAsync(
                    new TraceRecord($"trig{i}", "data.write:1", i % 2 == 0 ? "ok" : "ng", "B1", $"{{\"i\":{i}}}"),
                    CancellationToken.None);
            }

            await storage.FlushAsync(CancellationToken.None);

            var rows = await storage.ReadAllAsync(CancellationToken.None);
            Assert.Equal(250, rows.Count);
            Assert.Equal(250, storage.Queue.WrittenCount);
            Assert.Equal(0, storage.Queue.DroppedCount);
            Assert.Contains(rows, r => r.Kind == "ok" && r.Batch == "B1");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task FlushAsync_HonoursCancellation_WhileDraining()
    {
        var sink = new BlockingSink();
        await using var storage = new SqlStorage(Sqlite(":memory:"), sink: sink);
        await storage.AppendAsync(new TraceRecord("t0", "data.write:1", "measure"), CancellationToken.None);

        // Pump never started, so flush must wait for the enqueued record and pick up cancellation.
        // · 后台泵未启动，flush 需等待入队记录，从而感受到取消。
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.FlushAsync(cts.Token));
    }

    [Fact]
    public async Task CsvExport_StreamsPages_EmitsBom_AndCountsRows()
    {
        var path = TempDbPath();
        try
        {
            var config = Sqlite(path);
            await using var storage = new SqlStorage(config);
            await storage.InitializeAsync(CancellationToken.None);
            for (var i = 0; i < 250; i++)
                await storage.AppendAsync(new TraceRecord($"t{i}", "data.write:1", "measure"), CancellationToken.None);
            await storage.FlushAsync(CancellationToken.None);

            var exporter = new CsvExporter(new DbConnectionFactory(config), DbProviderKind.Sqlite);
            using var output = new MemoryStream();
            long lastProgress = 0;
            var progressCalls = 0;

            var exported = await exporter.ExportCsvAsync(
                new CsvExportRequest("SELECT seq, trigger_id, kind FROM cycle_records ORDER BY seq",
                    PageSize: 50,
                    OnProgress: n => { lastProgress = n; progressCalls++; }),
                output, CancellationToken.None);

            Assert.Equal(250, exported);
            Assert.Equal(250, lastProgress);
            Assert.Equal(5, progressCalls); // five pages of 50 · 5 页，每页 50

            var bytes = output.ToArray();
            Assert.True(bytes.Length > 3);
            Assert.Equal(0xEF, bytes[0]);
            Assert.Equal(0xBB, bytes[1]);
            Assert.Equal(0xBF, bytes[2]);

            var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(251, lines.Length); // header + 250 rows · 表头 + 250 行
            Assert.StartsWith("seq,trigger_id,kind", lines[0]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task CsvExport_CancelsOnProgress_AfterFirstPage()
    {
        var path = TempDbPath();
        try
        {
            var config = Sqlite(path);
            await using var storage = new SqlStorage(config);
            await storage.InitializeAsync(CancellationToken.None);
            for (var i = 0; i < 250; i++)
                await storage.AppendAsync(new TraceRecord($"t{i}", "data.write:1", "measure"), CancellationToken.None);
            await storage.FlushAsync(CancellationToken.None);

            var exporter = new CsvExporter(new DbConnectionFactory(config), DbProviderKind.Sqlite);
            using var output = new MemoryStream();
            using var cts = new CancellationTokenSource();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exporter.ExportCsvAsync(
                new CsvExportRequest("SELECT seq FROM cycle_records ORDER BY seq",
                    PageSize: 50,
                    OnProgress: _ => cts.Cancel()),
                output, cts.Token));

            var text = Encoding.UTF8.GetString(output.ToArray());
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(51, lines.Length); // first page flushed before cancel · 取消前已刷出首页
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
