using System.Data.Common;
using System.Globalization;
using System.Reflection;
using System.Text;
using Dapper;
using HalconWorkflow.Abstractions;

namespace HalconWorkflow.Storage;

/// <summary>
/// Streaming CSV export (§9.5.2): reads page-by-page from the query store and writes rows
/// incrementally — never loads the whole table into memory. UTF-8 BOM by default (Excel-friendly),
/// GBK optional. Fully cancelable per page and per row.
/// · 流式 CSV 导出(§9.5.2)：按页读、逐行写——绝不整表进内存。默认 UTF-8 带 BOM(Excel 友好)，
///   可选 GBK。每页每行均可取消。
/// </summary>
public sealed class CsvExporter : IExportService
{
    private readonly IDbConnectionFactory _factory;
    private readonly DbProviderKind _kind;

    static CsvExporter()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>Creates the exporter for one provider. · 为单一提供商创建导出器</summary>
    public CsvExporter(IDbConnectionFactory factory, DbProviderKind kind)
    {
        _factory = factory;
        _kind = kind;
    }

    /// <inheritdoc />
    public async Task<long> ExportCsvAsync(CsvExportRequest request, Stream output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(output);
        var pageSize = Math.Max(1, request.PageSize);
        var encoding = ResolveEncoding(request.Encoding);

        await using var writer = new StreamWriter(output, encoding, bufferSize: 4096, leaveOpen: true);
        await using var conn = _factory.Create();
        await conn.OpenAsync(ct).ConfigureAwait(false);

        long offset = 0;
        long exported = 0;
        var headerWritten = false;
        var columns = request.Columns?.ToArray() ?? [];

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var sql = DbDialect.ApplyPaging(_kind, request.Sql, pageSize, offset);
            var page = (await conn.QueryAsync(new CommandDefinition(
                sql, BuildParameters(request.Parameters, pageSize, offset), cancellationToken: ct)).ConfigureAwait(false)).ToList();
            if (page.Count == 0) break;

            if (!headerWritten)
            {
                if (columns.Length == 0) columns = ((IDictionary<string, object>)page[0]).Keys.ToArray();
                await writer.WriteLineAsync(CsvLine(columns).AsMemory(), ct).ConfigureAwait(false);
                headerWritten = true;
            }

            foreach (var row in page)
            {
                ct.ThrowIfCancellationRequested();
                var dict = (IDictionary<string, object>)row;
                await writer.WriteLineAsync(
                    CsvLine(columns.Select(c => Format(dict.TryGetValue(c, out var v) ? v : null))).AsMemory(), ct)
                    .ConfigureAwait(false);
                exported++;
            }

            await writer.FlushAsync(ct).ConfigureAwait(false);
            offset += page.Count;
            request.OnProgress?.Invoke(exported);
            if (page.Count < pageSize) break;
        }

        await writer.FlushAsync(ct).ConfigureAwait(false);
        return exported;
    }

    private static Encoding ResolveEncoding(CsvEncoding encoding) => encoding switch
    {
        CsvEncoding.Gbk => Encoding.GetEncoding(936),
        _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
    };

    private static DynamicParameters BuildParameters(object? parameters, int pageSize, long offset)
    {
        var dp = new DynamicParameters();
        switch (parameters)
        {
            case null:
                break;
            case DynamicParameters existing:
                foreach (var name in existing.ParameterNames) dp.Add(name, existing.Get<object>(name));
                break;
            case IEnumerable<KeyValuePair<string, object?>> map:
                foreach (var pair in map) dp.Add(pair.Key, pair.Value);
                break;
            default:
                foreach (var prop in parameters.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    dp.Add(prop.Name, prop.GetValue(parameters));
                break;
        }
        dp.Add("__take", pageSize);
        dp.Add("__skip", offset);
        return dp;
    }

    private static string CsvLine(IEnumerable<string> fields) => string.Join(',', fields.Select(Escape));

    private static string Escape(string? field)
    {
        field ??= "";
        if (field.IndexOfAny([',', '"', '\n', '\r']) < 0) return field;
        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }

    private static string Format(object? value) => value switch
    {
        null or DBNull => "",
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };
}
