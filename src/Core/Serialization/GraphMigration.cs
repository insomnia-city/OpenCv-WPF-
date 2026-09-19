using System.Text.Json.Nodes;
using HalconWorkflow.Core.Model;

namespace HalconWorkflow.Core.Serialization;

/// <summary>
/// Upgrades a single node document from one contract version to the next (§12).
/// Node libraries ship an <c>N-1 → N</c> migrator alongside a new version; the
/// <see cref="GraphMigrator"/> walks the chain so an old project opens without loss.
/// / 将单个节点文档从一个契约版本升级到下一个(§12)。节点库随新版本交付 N-1 → N 迁移器;
///   GraphMigrator 逐个串联,使老工程无损打开
/// </summary>
public interface IMigrator
{
    /// <summary>Contract namespace this migrator applies to. · 适用契约命名空间</summary>
    string ContractNs { get; }

    /// <summary>Source version; the migrator produces <c>FromVersion + 1</c>. · 源版本;迁移结果版本为 FromVersion+1</summary>
    int FromVersion { get; }

    /// <summary>Rewrites the node JSON (contract version is bumped by the runner). · 改写节点 JSON(契约版本由运行器提升)</summary>
    void Migrate(JsonNode node, Recipe recipe);
}

/// <summary>
/// Upgrades the whole document's <c>schemaVersion</c> by one step. Used for shape
/// changes that are not tied to a single contract (e.g. adding the version marker
/// and the <c>trigger</c> block). 
/// / 以一步升级整个文档的 schemaVersion。用于与单一契约无关的整体形状变更
///   (例如补上版本标记与 trigger 块)
/// </summary>
public interface IGraphSchemaMigrator
{
    /// <summary>Source schema version; the migrator produces <c>FromVersion + 1</c>. · 源 schema 版本;结果版本为 FromVersion+1</summary>
    int FromVersion { get; }

    /// <summary>Rewrites the document root in place. · 原地改写文档根</summary>
    void Migrate(JsonObject root);
}

/// <summary>One node contract that was upgraded. · 一个被升级的节点契约</summary>
public readonly record struct NodeMigration(string ContractNs, int FromVersion, int ToVersion);

/// <summary>
/// Outcome of a migration pass; <see cref="HasChanges"/> drives the "upgraded" prompt
/// and the pre-save second confirmation (§12). 
/// / 一次迁移的结果;HasChanges 驱动「已升级」提示与落盘前二次确认(§12)
/// </summary>
public sealed class MigrationReport
{
    /// <summary>Schema version read from the document (0 when the marker is absent). · 文档读出的 schema 版本(缺标记为 0)</summary>
    public int FromSchemaVersion { get; internal set; }

    /// <summary>Schema version after the chain. · 迁移链后的 schema 版本</summary>
    public int ToSchemaVersion { get; internal set; }

    /// <summary>Intermediate schema versions that were applied, in order. · 依次应用的中间 schema 版本</summary>
    public List<int> SchemaSteps { get; } = [];

    /// <summary>Node contract upgrades that were applied. · 应用的节点契约升级</summary>
    public List<NodeMigration> NodeMigrations { get; } = [];

    /// <summary>True when the document was touched at all. · 文档是否被改动</summary>
    public bool HasChanges => SchemaSteps.Count > 0 || NodeMigrations.Count > 0;
}

/// <summary>
/// Runs schema + per-contract migration chains on a parsed graph document (§12).
/// Contract namespaces without a matching migrator are left untouched (the node
/// then loads as a suspended placeholder if the plugin is missing). 
/// / 对已解析的图文档运行 schema 与逐契约迁移链(§12)。无匹配迁移器的契约保持原样
///   (若插件缺失则作为挂起占位节点载入)
/// </summary>
public sealed class GraphMigrator
{
    private readonly Dictionary<string, SortedList<int, IMigrator>> _node = new(StringComparer.Ordinal);
    private readonly SortedList<int, IGraphSchemaMigrator> _schema = new();

    /// <summary>Chain with no migrators registered. · 未注册任何迁移器的空链</summary>
    public static GraphMigrator Empty { get; } = new();

    /// <summary>Schema version the chain drives toward. · 迁移链目标 schema 版本</summary>
    public int TargetSchemaVersion { get; }

    public GraphMigrator(IEnumerable<IMigrator>? nodeMigrators = null,
        IEnumerable<IGraphSchemaMigrator>? schemaMigrators = null,
        int targetSchemaVersion = GraphJsonSerializer.SchemaVersion)
    {
        TargetSchemaVersion = targetSchemaVersion;
        if (nodeMigrators is not null)
            foreach (var m in nodeMigrators)
            {
                if (!_node.TryGetValue(m.ContractNs, out var chain))
                    _node[m.ContractNs] = chain = new SortedList<int, IMigrator>();
                chain[m.FromVersion] = m;
            }
        if (schemaMigrators is not null)
            foreach (var m in schemaMigrators)
                _schema[m.FromVersion] = m;
    }

    /// <summary>
    /// Applies the schema chain then every matching node chain. Mutates <paramref name="root"/>
    /// in place; node migrators may move values into <paramref name="recipe"/>. 
    /// / 先应用 schema 链,再应用每个匹配的节点链。原地改写 root;节点迁移器可把值移入 recipe
    /// </summary>
    public MigrationReport Migrate(JsonObject root, Recipe? recipe = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        recipe ??= new Recipe();
        var report = new MigrationReport();

        var current = (int?)root["schemaVersion"] ?? 0;
        report.FromSchemaVersion = current;
        while (current < TargetSchemaVersion && _schema.TryGetValue(current, out var schemaMigrator))
        {
            schemaMigrator.Migrate(root);
            current++;
            root["schemaVersion"] = current;
            report.SchemaSteps.Add(current);
        }
        report.ToSchemaVersion = current;

        if (root["nodes"] is JsonArray nodes)
            foreach (var entry in nodes)
            {
                if (entry is not JsonObject node || node["contract"] is not JsonObject contract) continue;
                var ns = (string?)contract["ns"];
                if (string.IsNullOrEmpty(ns)) continue;
                var from = (int?)contract["version"] ?? 1;
                var version = from;
                while (_node.TryGetValue(ns, out var chain) && chain.TryGetValue(version, out var migrator))
                {
                    migrator.Migrate(node, recipe);
                    version++;
                    contract["version"] = version;
                }
                if (version != from)
                    report.NodeMigrations.Add(new NodeMigration(ns, from, version));
            }

        return report;
    }
}

/// <summary>
/// Schema 0 → 1: documents written before the version marker existed. Fills in the
/// marker, an empty <c>trigger</c> block and empty node/link arrays. 
/// / schema 0 → 1：版本标记出现前所写的文档。补上标记、空 trigger 块与空节点/连线数组
/// </summary>
public sealed class LegacySchemaV0ToV1 : IGraphSchemaMigrator
{
    /// <inheritdoc />
    public int FromVersion => 0;

    /// <inheritdoc />
    public void Migrate(JsonObject root)
    {
        root["schema"] ??= GraphJsonSerializer.Schema;
        root["schemaVersion"] = 1;
        root["trigger"] ??= new JsonObject
        {
            ["source"] = nameof(TriggerSource.Manual),
            ["debounceMs"] = 10,
            ["queueLimit"] = 1
        };
        root["nodes"] ??= new JsonArray();
        root["links"] ??= new JsonArray();
    }
}
