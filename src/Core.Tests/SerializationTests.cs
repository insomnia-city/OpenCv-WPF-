using HalconWorkflow.Core.Graph;
using HalconWorkflow.Core.Model;
using HalconWorkflow.Core.Serialization;

namespace HalconWorkflow.Core.Tests;

public class SerializationTests
{
    private static GraphModel BuildSampleGraph()
    {
        var graph = new GraphModel();
        graph.AddNode(TestNodes.Grabber("cam"));
        graph.AddNode(TestNodes.Threshold());
        graph.AddNode(TestNodes.ResultOut("result"));
        var grab = graph.Nodes["cam"];
        var thresh = graph.Nodes["threshold"];
        var result = graph.Nodes["result"];

        graph.Connect(grab.Node.Outputs.First(p => p.Kind == PortKind.Exec),
            thresh.Node.Inputs.First(p => p.Kind == PortKind.Exec));
        graph.Connect(grab.Node.Outputs.First(p => p.Name == "Image"), thresh.Node.Inputs.First(p => p.Name == "Image"));
        graph.Connect(thresh.Node.Outputs.First(p => p.Name == "Region"), result.Node.Inputs.First(p => p.Name == "Region"));

        thresh.RecipeId = "r1";
        thresh.Position = (120, 80);
        return graph;
    }

    [Fact]
    public void RoundTrip_Graph_IsIdempotent()
    {
        var json = GraphJsonSerializer.Serialize(BuildSampleGraph());
        var restored = GraphJsonSerializer.Deserialize(json, new TestNodeFactory());

        Assert.Equal(3, restored.Nodes.Count);
        Assert.Equal("vision.threshold", restored.Nodes["threshold"].Contract.Namespace);
        Assert.Equal(1, restored.Nodes["threshold"].Contract.Version);
        Assert.Equal("r1", restored.Nodes["threshold"].RecipeId);
        Assert.Equal((120.0, 80.0), restored.Nodes["threshold"].Position);
        Assert.Equal(3, restored.Links.Count);
        Assert.Empty(restored.Validate());
    }

    [Fact]
    public void RoundTrip_Twice_ProducesSameJson()
    {
        var once = GraphJsonSerializer.Serialize(BuildSampleGraph());
        var twice = GraphJsonSerializer.Serialize(
            GraphJsonSerializer.Deserialize(once, new TestNodeFactory()));
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Schema_SaysVisionWorkflowGraph()
    {
        var json = GraphJsonSerializer.Serialize(BuildSampleGraph());
        Assert.Contains("\"schema\": \"vision.workflow/graph\"", json);
    }

    [Fact]
    public void UnknownContract_ProducesSuspendedNode()
    {
        var graph = new GraphModel();
        var grab = TestNodes.Grabber("cam");
        graph.AddNode(grab);

        var json = GraphJsonSerializer.Serialize(graph);
        // Hand-edit the serialized namespace to an unknown one. · 手工把序列化命名空间改成未知
        json = json.Replace("vision.grabber", "vision.gone");

        var restored = GraphJsonSerializer.Deserialize(json, new TestNodeFactory());
        Assert.True(restored.Nodes["cam"].IsSuspended);
    }

    [Fact]
    public void UnknownContract_ThrowOnUnknown_Throws()
    {
        var graph = new GraphModel();
        graph.AddNode(TestNodes.Grabber("cam"));
        var json = GraphJsonSerializer.Serialize(graph).Replace("vision.grabber", "vision.gone");

        Assert.Throws<ContractNotFoundException>(
            () => GraphJsonSerializer.Deserialize(json, new TestNodeFactory(), throwOnUnknown: true));
    }

    [Fact]
    public void TriggerConfig_RoundTrips_NewFields()
    {
        var graph = BuildSampleGraph();
        graph.Trigger.Enabled = true;
        graph.Trigger.Source = TriggerSource.TagChange;
        graph.Trigger.Tag = "demo/coil/run";
        graph.Trigger.DebounceMs = 7;
        graph.Trigger.IntervalMs = 250;

        var json = GraphJsonSerializer.Serialize(graph);
        var restored = GraphJsonSerializer.Deserialize(json, new TestNodeFactory());

        Assert.True(restored.Trigger.Enabled);
        Assert.Equal(TriggerSource.TagChange, restored.Trigger.Source);
        Assert.Equal("demo/coil/run", restored.Trigger.Tag);
        Assert.Equal(7, restored.Trigger.DebounceMs);
        Assert.Equal(250, restored.Trigger.IntervalMs);
    }

    [Fact]
    public void LegacyTrigger_Document_DefaultsIdleManual()
    {
        // A pre-stage-20 doc lacks the enabled/intervalMs fields; defaults must kick in. · 阶段20 之前的文档没有 enabled/intervalMs 字段;须回退默认值
        const string json = """
            {"schema":"vision.workflow/graph","schemaVersion":1,
             "trigger":{"source":"Timer","tag":"a/b/c","debounceMs":5,"queueLimit":1},
             "nodes":[]}
            """;
        var restored = GraphJsonSerializer.Deserialize(json, new TestNodeFactory());

        Assert.Equal(TriggerSource.Timer, restored.Trigger.Source); // source honored · 来源生效
        Assert.False(restored.Trigger.Enabled);                     // but disabled by default · 但默认禁用
        Assert.Equal(500, restored.Trigger.IntervalMs);
        Assert.Equal(5, restored.Trigger.DebounceMs);
        Assert.Equal("a/b/c", restored.Trigger.Tag);
    }

    [Fact]
    public void Recipe_RoundTrip_PreservesParams()
    {
        var recipe = new Recipe();
        recipe.Params["r1"] = new System.Text.Json.Nodes.JsonObject
        {
            ["minGray"] = 128,
            ["maxGray"] = 255
        };

        var json = Recipe.Serialize(recipe);
        var restored = Recipe.Deserialize(json);

        Assert.Equal(128, restored.Get<int>("r1", "minGray"));
        Assert.Equal(255, restored.Get<int>("r1", "maxGray"));
        Assert.Equal(0, restored.Get<int>("missing", "minGray"));
    }
}