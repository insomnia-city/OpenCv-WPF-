using System.Text.Json.Nodes;
using HalconWorkflow.Core.Graph;
using HalconWorkflow.Core.Serialization;
using Xunit;

namespace HalconWorkflow.Core.Tests;

/// <summary>
/// Stage-19 gate (§12): schema/contract migration chains and suspended-node reporting.
/// Legacy documents without a version marker are upgraded; per-contract migrators walk
/// N-1→N; a node recovers (not suspended) once its plugin is present again. /
/// 阶段19 门(§12)：schema/契约迁移链与挂起节点报告。无版本标记的遗留文档被升级；
/// 逐契约迁移器走 N-1→N；插件重新存在后节点恢复(非挂起)。
/// </summary>
public class MigrationTests
{
    private sealed class BumpMigrator(string ns, int from) : IMigrator
    {
        public string ContractNs { get; } = ns;
        public int FromVersion { get; } = from;

        public void Migrate(JsonNode node, Recipe recipe) => node["migratedTo"] = from + 1;
    }

    [Fact]
    public void LegacyDocument_MigratesToCurrentSchema()
    {
        var root = new JsonObject
        {
            ["schema"] = GraphJsonSerializer.Schema,
            ["nodes"] = new JsonArray()
        };

        var report = new GraphMigrator(schemaMigrators: [new LegacySchemaV0ToV1()]).Migrate(root);

        Assert.True(report.HasChanges);
        Assert.Equal(0, report.FromSchemaVersion);
        Assert.Equal(1, report.ToSchemaVersion);
        Assert.Equal([1], report.SchemaSteps);
        Assert.Equal(1, (int?)root["schemaVersion"]);
        Assert.NotNull(root["trigger"]);
        Assert.NotNull(root["links"]);
    }

    [Fact]
    public void NodeContractChain_WalksUpToLatest()
    {
        var root = new JsonObject
        {
            ["schema"] = GraphJsonSerializer.Schema,
            ["schemaVersion"] = 1,
            ["nodes"] = new JsonArray(new JsonObject
            {
                ["id"] = "t",
                ["contract"] = new JsonObject { ["ns"] = "vision.threshold", ["version"] = 1 }
            })
        };

        var migrator = new GraphMigrator(
        [
            new BumpMigrator("vision.threshold", 1),
            new BumpMigrator("vision.threshold", 2)
        ]);
        var report = migrator.Migrate(root);

        Assert.Equal(3, (int?)root["nodes"]![0]!["contract"]!["version"]);
        var step = Assert.Single(report.NodeMigrations);
        Assert.Equal(new NodeMigration("vision.threshold", 1, 3), step);
    }

    [Fact]
    public void Load_LegacyDocument_ReportsMigration()
    {
        const string json = """{"schema":"vision.workflow/graph","nodes":[],"links":[]}""";

        var result = GraphJsonSerializer.Load(json, new TestNodeFactory(),
            new GraphMigrator(schemaMigrators: [new LegacySchemaV0ToV1()]));

        Assert.True(result.Migration.HasChanges);
        Assert.Equal(1, result.Migration.ToSchemaVersion);
        Assert.Empty(result.Suspended);
    }

    [Fact]
    public void Load_UnknownContract_ReportsSuspendedNodeWithReason()
    {
        const string json = """
            {"schema":"vision.workflow/graph","schemaVersion":1,
             "nodes":[{"id":"ghost","contract":{"ns":"vision.gone","version":1},"pos":{"x":1,"y":2}}],
             "links":[]}
            """;

        var result = GraphJsonSerializer.Load(json, new TestNodeFactory());

        var info = Assert.Single(result.Suspended);
        Assert.Equal("ghost", info.Id);
        Assert.NotNull(info.Reason);
        Assert.True(result.Graph.Nodes["ghost"].IsSuspended);
        Assert.NotNull(result.Graph.Nodes["ghost"].SuspensionReason);
    }

    [Fact]
    public void Load_PluginPresent_RecoversPreviouslySuspendedNode()
    {
        var graph = new GraphModel();
        graph.AddNode(TestNodes.Grabber("cam"));
        graph.Nodes["cam"].IsSuspended = true; // persisted flag from an earlier session · 上一会话持久化的标记
        var json = GraphJsonSerializer.Serialize(graph);
        Assert.Contains("\"suspended\": true", json);

        var restored = GraphJsonSerializer.Deserialize(json, new TestNodeFactory());

        Assert.False(restored.Nodes["cam"].IsSuspended);
    }
}
