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