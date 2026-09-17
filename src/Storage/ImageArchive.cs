using System.Globalization;
using Dapper;
using HalconWorkflow.Abstractions;

namespace HalconWorkflow.Storage;

/// <summary>
/// File-backed image archive (§9.5.3): snapshot bytes are written as files under an archive root,
/// while <c>trace_images</c> stores the relative path and links back to the cycle. Images never
/// touch the hot-path record. · 文件归档存图(§9.5.3)：快照字节以文件落在归档根目录，
/// trace_images 记相对路径并回链周期。图像绝不进热路径记录。
/// </summary>
public sealed class ImageArchiveStore : IImageArchive
{
    private const string InsertSql =
        "INSERT INTO trace_images (trigger_id, cycle_seq, node, kind, rel_path, content_type, width, height, size_bytes, batch) " +
        "VALUES (@TriggerId, @CycleSeq, @Node, @Kind, @RelPath, @ContentType, @Width, @Height, @SizeBytes, @Batch)";

    private const string SelectSql =
        "SELECT id AS Id, trigger_id AS TriggerId, cycle_seq AS CycleSeq, node AS Node, kind AS Kind, " +
        "rel_path AS RelPath, content_type AS ContentType, width AS Width, height AS Height, " +
        "size_bytes AS SizeBytes, batch AS Batch, ts AS Ts FROM trace_images";

    private readonly IDbConnectionFactory _factory;
    private readonly DbProviderKind _kind;
    private readonly string _root;

    public ImageArchiveStore(IDbConnectionFactory factory, DbProviderKind kind, string archiveRoot)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _kind = kind;
        _root = Path.GetFullPath(archiveRoot ?? throw new ArgumentNullException(nameof(archiveRoot)));
    }

    /// <summary>Absolute archive root directory. · 归档根目录绝对路径</summary>
    public string ArchiveRoot => _root;

    /// <inheritdoc />
    public async Task<ImageAsset> SaveAsync(ImageArchiveRequest request, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = DateTimeOffset.UtcNow;
        var day = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var extension = NormalizeExtension(request.Extension, request.ContentType);
        var fileName = string.Create(CultureInfo.InvariantCulture,
            $"{Sanitize(request.TriggerId)}_{Sanitize(request.Node ?? "node")}_{request.Kind}_{Guid.NewGuid():N}{extension}");
        var relative = day + "/" + fileName;
        var directory = Path.Combine(_root, day);
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, fileName), content.ToArray(), ct).ConfigureAwait(false);

        var parameters = new
        {
            request.TriggerId,
            request.CycleSeq,
            request.Node,
            Kind = request.Kind.ToString(),
            RelPath = relative,
            request.ContentType,
            request.Width,
            request.Height,
            SizeBytes = (long)content.Length,
            request.Batch
        };

        await using var conn = _factory.Create();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        long id;
        if (_kind == DbProviderKind.Postgres)
        {
            id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                InsertSql + " RETURNING id", parameters, cancellationToken: ct)).ConfigureAwait(false);
        }
        else
        {
            await conn.ExecuteAsync(new CommandDefinition(InsertSql, parameters, cancellationToken: ct)).ConfigureAwait(false);
            var scalar = await conn.ExecuteScalarAsync(new CommandDefinition(
                DbDialect.LastInsertIdSql(_kind), cancellationToken: ct)).ConfigureAwait(false);
            id = Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
        }

        return new ImageAsset(id, request.TriggerId, request.Node, request.Kind, relative, request.ContentType,
            content.Length, request.Width, request.Height, request.Batch, request.CycleSeq, now);
    }

    /// <inheritdoc />
    public async Task<Stream?> OpenReadAsync(long id, CancellationToken ct)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        var relative = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT rel_path FROM trace_images WHERE id = @id", new { id }, cancellationToken: ct)).ConfigureAwait(false);
        if (relative is null) return null;
        var absolute = Absolute(relative);
        return File.Exists(absolute) ? File.OpenRead(absolute) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ImageAsset>> QueryByTriggerAsync(string triggerId, CancellationToken ct)
    {
        var rows = await QueryAsync(SelectSql + " WHERE trigger_id = @triggerId ORDER BY id",
            new { triggerId }, ct).ConfigureAwait(false);
        return rows.Select(ToAsset).ToList();
    }

    /// <inheritdoc />
    public async Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct)
    {
        var rows = await QueryAsync(SelectSql + " WHERE ts < @cutoff",
            new { cutoff = olderThan.UtcDateTime }, ct).ConfigureAwait(false);
        if (rows.Count == 0) return 0;

        foreach (var row in rows)
        {
            var absolute = Absolute(row.RelPath);
            try
            {
                if (File.Exists(absolute)) File.Delete(absolute);
            }
            catch (IOException)
            {
                // A locked file must not stop row pruning; its row still goes. · 文件被锁不阻断删行。
            }
        }

        await using var conn = _factory.Create();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        var ids = rows.Select(r => r.Id).ToArray();
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM trace_images WHERE id IN @ids", new { ids }, cancellationToken: ct)).ConfigureAwait(false);
    }

    private async Task<List<ImageRow>> QueryAsync(string sql, object parameters, CancellationToken ct)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<ImageRow>(new CommandDefinition(sql, parameters, cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    private static ImageAsset ToAsset(ImageRow row) => new(
        row.Id,
        row.TriggerId,
        row.Node,
        Enum.TryParse<ImageKind>(row.Kind, ignoreCase: true, out var kind) ? kind : ImageKind.Original,
        row.RelPath,
        row.ContentType ?? "application/octet-stream",
        row.SizeBytes ?? 0,
        row.Width,
        row.Height,
        row.Batch,
        row.CycleSeq,
        row.Ts);

    private string Absolute(string relative) =>
        Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string NormalizeExtension(string? extension, string contentType)
    {
        if (!string.IsNullOrWhiteSpace(extension))
            return extension.StartsWith('.') ? extension : "." + extension;
        return contentType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/bmp" => ".bmp",
            "image/tiff" => ".tif",
            _ => ".bin"
        };
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    private sealed class ImageRow
    {
        public long Id { get; set; }
        public string TriggerId { get; set; } = string.Empty;
        public long? CycleSeq { get; set; }
        public string? Node { get; set; }
        public string? Kind { get; set; }
        public string RelPath { get; set; } = string.Empty;
        public string? ContentType { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public long? SizeBytes { get; set; }
        public string? Batch { get; set; }
        public DateTime Ts { get; set; }
    }
}
