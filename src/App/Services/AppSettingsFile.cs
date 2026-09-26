using System;
using System.IO;
using System.Text.Json;

namespace HalconWorkflow.App.Services;

/// <summary>
/// File-backed application settings (stage-30): the four document-listed toggles (§5.8
/// appsettings.json) round-tripped as UTF-8 JSON with no third-party serializer (uses the
/// shared-framework System.Text.Json, so no NuGet dependency is introduced, per the offline
/// constraint). A missing or corrupt file falls back to identical defaults and overwrites.
/// · 文件级应用设置(阶段30)：文档所列四个开关(§5.8 appsettings.json)以 UTF-8 JSON 往返,
///   使用共享框架的 System.Text.Json,不新增第三方包(离线约束)。文件缺失/损坏一律回退默认值并覆写。
/// </summary>
public static class AppSettingsFile
{
    /// <summary>The product settings file name. · 产品设置文件名</summary>
    public const string FileName = "appsettings.json";

    /// <summary>Key used when reporting the settings source (§8.前所未有的 debugging). · 上报设置来源时使用的键</summary>
    public const string SourceKey = "app.settings";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Full path for a content base directory (exe folder / test temp). · 某内容基目录下的完整路径</summary>
    public static string PathFor(string baseDirectory) => Path.Combine(baseDirectory, FileName);

    /// <summary>Defaults used in memory before any precedence, and the fallback for missing/corrupt files.
    /// · 内存默认值;文件缺失/损坏时的回退基准
    /// </summary>
    public static AppSettings Defaults => new();

    /// <summary>
    /// Loads settings from <paramref name="path"/>; missing or corrupt → defaults with untouched defaults.
    /// · 从指定路径加载设置;缺失/损坏→默认值
    /// </summary>
    public static AppSettings Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Defaults;
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? Defaults;
        }
        catch (JsonException)
        {
            return Defaults;
        }
        catch (IOException)
        {
            return Defaults;
        }
        catch (UnauthorizedAccessException)
        {
            return Defaults;
        }
    }

    /// <summary>Writes settings to disk as indented UTF-8 JSON; false when the directory is unwritable.
    /// · 以缩进 UTF-8 JSON 写盘;目标目录不可写时返回 false
    /// </summary>
    public static bool Save(string path, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

/// <summary>
/// Serializable application-settings snapshot (§5.8). · 可序列化应用设置快照(§5.8)
/// </summary>
public sealed record AppSettings
{
    /// <summary>Preferred UI culture name (en-US / zh-Hans / ko-KR). · 首选 UI 语言(en-US/zh-Hans/ko-KR)</summary>
    public string Culture { get; init; } = "zh-Hans";

    /// <summary>Directory for rolling log output (kept for stage-25 diagnostics; empty = platform default).
    /// · 追溯日志输出目录(为阶段25诊断预留;空=平台默认)</summary>
    public string LogDirectory { get; init; } = "";

    /// <summary>Whether the node preview ring is capturing by default (§4.2). · 节点预览环默认是否捕获(§4.2)</summary>
    public bool EnablePreview { get; init; } = true;

    /// <summary>Days a trace record is retained before pruning (§9.4.2). · 追溯记录保留天数后修剪(§9.4.2)</summary>
    public int TraceRetentionDays { get; init; } = 30;
}