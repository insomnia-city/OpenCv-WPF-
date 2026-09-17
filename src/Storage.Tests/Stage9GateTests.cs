using System.Data.Common;
using HalconWorkflow.Abstractions;
using HalconWorkflow.Storage;
using Xunit;

namespace HalconWorkflow.Storage.Tests;

/// <summary>
/// Stage-9 gate (§13.1): the board, stats and CSV read the same single trace source (no second
/// copy), schema v2 migrates in place, image archiving round-trips files + rows, and the preview
/// ring stays bounded. · 阶段 9 门(§13.1)：看板/统计/CSV 同读唯一追溯源(不另建第二份)、schema v2
/// 就地迁移、存图落文件+落行往返、预览环保持有界。
/// </summary>
public class Stage9GateTests
{
    private static string TempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"halcon-s9-{Guid.NewGuid():N}.db");

    private static DbConfig Sqlite(string path) =>
        new("test", DbProviderKind.Sqlite, $"Data Source={path};Pooling=False");

    private static async Task<DbConnection> OpenAsync(DbConfig config)
    {
        var conn = DbDialect.CreateConnection(config.Kind);
        conn.ConnectionString = config.ConnectionString;
        await conn.OpenAsync();
        return conn;
    }

    private static async Task ExecuteAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> ReadStringsAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var values = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return values;
    }

    [Fact]
    public async Task Schema_V2_HasDimensionColumnsAndImagesTable_AndIsIdempotent()
    {
        var path = TempDbPath();
        try
        {
            var config = Sqlite(path);
            await using var storage = new SqlStorage(config);
            await storage.InitializeAsync(CancellationToken.None);
            await storage.InitializeAsync(CancellationToken.None);

            await using var conn = await OpenAsync(config);
            Assert.Equal(TraceSchema.Version, await TraceSchema.GetVersionAsync(conn, CancellationToken.None));

            var tables = await ReadStringsAsync(conn,
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name");
            Assert.Equal(new[] { "cycle_records", "schema_version", "trace_images" }, tables);

            var columns = await ReadStringsAsync(conn, "SELECT name FROM pragma_table_info('cycle_records')");
            foreach (var dimension in new[] { "line", "machine", "shift", "model", "recipe" })
                Assert.Contains(DbDialect.DimensionColumn(dimension), columns);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Schema_MigratesV1_ToV2_PreservingRows()
    {
        var path = TempDbPath();
        try
        {
            var config = Sqlite(path);
            await using (var conn = await OpenAsync(config))
            {
                await ExecuteAsync(conn, "CREATE TABLE schema_version (component TEXT PRIMARY KEY, version INTEGER NOT NULL)");
                await ExecuteAsync(conn, "CREATE TABLE cycle_records (seq INTEGER PRIMARY KEY AUTOINCREMENT, "
                    + "trigger_id TEXT NOT NULL, batch TEXT NULL, node TEXT NULL, kind TEXT NULL, "
                    + "result_json TEXT NULL, image_ref TEXT NULL, ts TIMESTAMP DEFAULT CURRENT_TIMESTAMP)");
                await ExecuteAsync(conn, "INSERT INTO schema_version (component, version) VALUES ('trace', 1)");
                await ExecuteAsync(conn, "INSERT INTO cycle_records (trigger_id, kind) VALUES ('legacy', 'ok')");
            }

            await using var storage = new SqlStorage(config);
            await storage.InitializeAsync(CancellationToken.None);
            await storage.AppendAsync(new TraceRecord("t-new", "data.write:1", "ng")
            {
                Dimensions = new Dictionary<string, string?> { [DimensionKeys.Line] = "L1" }
            }, CancellationToken.None);
            await storage.FlushAsync(CancellationToken.None);

            await using var migrateCheck = await OpenAsync(config);
            Assert.Equal(2, await TraceSchema.GetVersionAsync(migrateCheck, CancellationToken.None));

            var rows = await storage.ReadAllAsync(CancellationToken.None);
            Assert.Equal(2, rows.Count);
            Assert.Equal("legacy", rows[0].TriggerId);
            Assert.Equal("L1", rows[1].Line);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Stats_ReadsSameSingleTraceSource_AsBoard()
    {
        var path = TempDbPath();
        try
        {
            var config = Sqlite(path);
            var factory = new DbConnectionFactory(config);
            await using var storage = new SqlStorage(config);
            await storage.InitializeAsync(CancellationToken.None);

            for (var i = 0; i < 10; i++)
            {
                await storage.AppendAsync(new TraceRecord($"t{i}", "data.write:1", i < 8 ? "ok" : "ng")
                {
                    Dimensions = new Dictionary<string, string?>
                    {
                        [DimensionKeys.Line] = i < 5 ? "L1" : "L2"
                    }
                }, CancellationToken.None);
            }
            await storage.FlushAsync(CancellationToken.None);

            var stats = new StatsService(factory, DbProviderKind.Sqlite);
            var from = DateTimeOffset.UtcNow.AddYears(-1);
            var to = DateTimeOffset.UtcNow.AddYears(1);

            var summary = await stats.SummaryAsync(from, to, CancellationToken.None);
            Assert.Equal(10, summary.Cycles);
            Assert.Equal(8, summary.Ok);
            Assert.Equal(2, summary.Ng);
            Assert.Equal(80d, summary.YieldPercent, 3);

            var slices = await stats.SliceAsync(YieldDimension.Line, from, to, CancellationToken.None);
            Assert.Equal(2, slices.Count);
            var l1 = Assert.Single(slices, s => s.Key == "L1");
            Assert.Equal(5, l1.Cycles);
            Assert.Equal(100d, l1.YieldPercent, 3);
            var l2 = Assert.Single(slices, s => s.Key == "L2");
            Assert.Equal(5, l2.Cycles);
            Assert.Equal(60d, l2.YieldPercent, 3); // 3 ok + 2 ng · 3 良 + 2 不良

            // Both the board and stats read one and the same cycle_records table — no second copy.
            // · 看板与统计同读同一 cycle_records 表——无第二份。
            await using var conn = await OpenAsync(config);
            var tables = await ReadStringsAsync(conn,
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name");
            Assert.Equal(new[] { "cycle_records", "schema_version", "trace_images" }, tables);
            var board = await storage.ReadAllAsync(CancellationToken.None);
            Assert.Equal(summary.Cycles, board.Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ImageArchive_SavesFiles_QueriesByTrigger_AndPrunes()
    {
        var path = TempDbPath();
        var root = Path.Combine(Path.GetTempPath(), $"halcon-img-{Guid.NewGuid():N}");
        try
        {
            var config = Sqlite(path);
            await using var storage = new SqlStorage(config);
            await storage.InitializeAsync(CancellationToken.None);
            var archive = new ImageArchiveStore(new DbConnectionFactory(config), DbProviderKind.Sqlite, root);

            var png = new byte[] { 1, 2, 3, 4, 5 };
            var original = await archive.SaveAsync(
                new ImageArchiveRequest("T1", ImageKind.Original, Node: "vision.inspect:2", Width: 640, Height: 480),
                png, CancellationToken.None);
            var rendered = await archive.SaveAsync(
                new ImageArchiveRequest("T1", ImageKind.Rendered, Node: "vision.inspect:2"),
                new byte[] { 9, 9 }, CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(root, original.RelativePath.Replace('/', Path.DirectorySeparatorChar))));
            Assert.True(rendered.Id > original.Id);
            Assert.Equal("image/png", original.ContentType);

            var assets = await archive.QueryByTriggerAsync("T1", CancellationToken.None);
            Assert.Equal(2, assets.Count);
            Assert.Equal(ImageKind.Original, assets[0].Kind);
            Assert.Equal(ImageKind.Rendered, assets[1].Kind);
            Assert.Equal(5, assets[0].SizeBytes);
            Assert.Equal(640, assets[0].Width);

            await using (var stream = await archive.OpenReadAsync(original.Id, CancellationToken.None))
            {
                Assert.NotNull(stream);
                using var buffer = new MemoryStream();
                await stream!.CopyToAsync(buffer);
                Assert.Equal(png, buffer.ToArray());
            }
            Assert.Null(await archive.OpenReadAsync(9999, CancellationToken.None));

            var pruned = await archive.PruneAsync(DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
            Assert.Equal(2, pruned);
            Assert.Empty(await archive.QueryByTriggerAsync("T1", CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(root, original.RelativePath.Replace('/', Path.DirectorySeparatorChar))));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PreviewRing_KeepsNewest_AndHonoursEnabled()
    {
        var ring = new PreviewRing(capacity: 3);
        var node = "vision.inspect:2";
        for (var i = 0; i < 5; i++)
            ring.Publish(new PreviewFrame(node, DateTimeOffset.UtcNow.AddSeconds(i), Summary: i.ToString()));

        Assert.Equal(3, ring.Recent(node, 10).Count);
        Assert.Equal("4", ring.Latest(node)!.Summary);
        Assert.Equal(new[] { "4", "3", "2" }, ring.Recent(node, 3).Select(f => f.Summary));
        Assert.Contains(node, ring.Nodes);

        ring.Enabled = false;
        ring.Publish(new PreviewFrame(node, DateTimeOffset.UtcNow, Summary: "off"));
        Assert.Equal("4", ring.Latest(node)!.Summary);

        ring.Clear();
        Assert.Null(ring.Latest(node));
        Assert.Empty(ring.Nodes);
    }
}
