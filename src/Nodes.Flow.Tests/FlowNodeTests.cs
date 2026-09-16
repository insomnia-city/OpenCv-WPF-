using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Execution;
using HalconWorkflow.Core.Graph;
using HalconWorkflow.Core.Model;
using HalconWorkflow.Core.Serialization;
using HalconWorkflow.Core.Types;
using HalconWorkflow.Nodes.Flow;
using Xunit;

namespace HalconWorkflow.Nodes.Flow.Tests;

/// <summary>
/// Headless execution helper for flow graphs. · 流程图无头执行助手
/// </summary>
internal static class FlowRunner
{
    public static Task<GraphRunResult> RunAsync(GraphModel graph)
    {
        var scheduler = new GraphScheduler();
        scheduler.Load(graph);
        return scheduler.RunOnceAsync(CancellationToken.None);
    }
}

/// <summary>
/// Stage-4 unit tests for flow nodes (§13.1). · 阶段4 流程节点单测
/// </summary>
public class FlowNodeTests
{
    [Fact]
    public async Task FaultingNode_CountsAsFailed()
    {
        var graph = new GraphModel();
        var join = FlowNodes.Join("j");
        graph.AddNode(join);
        var result = await FlowRunner.RunAsync(graph);
        Assert.False(result.Success);
        Assert.Equal(1, result.FaultedNodes);
    }

    [Fact]
    public async Task Join_RequiresBothInputs_FaultsWhenMissing()
    {
        // Join alone has no exec input → never scheduled; wrap with counter to force running. 
        var graph = new GraphModel();
        var counter = FlowNodes.Counter("c");
        var join = FlowNodes.Join("j");
        graph.AddNode(counter);
        graph.AddNode(join);
        graph.Connect(Port(counter, PortDirection.Out), Port(join, PortDirection.In));
        var result = await FlowRunner.RunAsync(graph);
        Assert.False(result.Success);
        Assert.Equal(1, result.FaultedNodes);
    }

    [Fact]
    public async Task Counter_IncrementsPerInvocation()
    {
        var graph = new GraphModel();
        graph.AddNode(FlowNodes.Counter("c"));
        var r1 = await FlowRunner.RunAsync(graph);
        var r2 = await FlowRunner.RunAsync(graph);
        Assert.True(r1.Success && r2.Success);
    }

    [Fact]
    public async Task ScriptNode_EvaluatesArithmeticAndComparison()
    {
        var graph = new GraphModel();
        var script = new ScriptNode("s", "2 * 3 + 4", ScriptNode.OutKind.Real);
        graph.AddNode(script);
        var result = await FlowRunner.RunAsync(graph);
        Assert.True(result.Success);
    }

    [Fact]
    public void ExpressionEvaluator_Reports_NumericLogicComparison()
    {
        Assert.Equal(7L, new ExpressionEvaluator("1 + 2 * 3").Evaluate());
        Assert.Equal(9L, new ExpressionEvaluator("(1 + 2) * 3").Evaluate());
        Assert.True((bool)new ExpressionEvaluator("2 > 1 && 3 >= 3")!.Evaluate()!);
        Assert.True((bool)new ExpressionEvaluator("!false")!.Evaluate()!);
        Assert.Equal(5L, new ExpressionEvaluator("2 > 1 ? 5 : 9").Evaluate());
        Assert.Equal("ab", new ExpressionEvaluator("\"a\" + \"b\"").Evaluate());
    }

    [Fact]
    public void ExpressionEvaluator_ResolvesIdentifiers()
    {
        Func<string, object?> resolve = name => name switch { "x" => 2.0, "y" => 3L, _ => null };
        Assert.Equal(5.0, new ExpressionEvaluator("x + y", resolve).Evaluate());
        Assert.Equal(4.0, new ExpressionEvaluator("x * x", resolve).Evaluate());
        Assert.True((bool)new ExpressionEvaluator("y == 3", resolve)!.Evaluate()!);
    }

    [Fact]
    public void ExpressionEvaluator_Defaults_UnknownIdentifier_ToNullSafe()
    {
        Assert.Null(new ExpressionEvaluator("zz", _ => null).Evaluate());
    }

    [Fact]
    public void ExpressionEvaluator_Throws_OnSyntaxError()
    {
        Assert.Throws<FormatException>(() => new ExpressionEvaluator("2 +").Evaluate());
        Assert.Throws<FormatException>(() => new ExpressionEvaluator("\"unterminated").Evaluate());
        Assert.Throws<FormatException>(() => new ExpressionEvaluator("(1").Evaluate());
    }

    [Fact]
    public void FlowNodeFactory_Resolves_AllContracts()
    {
        var factory = new FlowNodeFactory();
        Assert.NotNull(factory.Create(new NodeContract("flow.branch", 1), "b"));
        Assert.NotNull(factory.Create(new NodeContract("flow.join", 1), "j"));
        Assert.NotNull(factory.Create(new NodeContract("flow.counter", 1), "c"));
        Assert.NotNull(factory.Create(new NodeContract("flow.delay", 1), "d"));
        Assert.NotNull(factory.Create(new NodeContract("flow.script", 1), "s"));
        Assert.Null(factory.Create(new NodeContract("unknown.thing", 1), "x"));
    }

    [Fact]
    public void FlowGraph_RoundTrips_ThroughContractJson()
    {
        var graph = new GraphModel();
        var branch = FlowNodes.Branch("b");
        var counter = FlowNodes.Counter("c");
        graph.AddNode(branch);
        graph.AddNode(counter);
        graph.Connect(Port(branch, PortDirection.Out), Port(counter, PortDirection.In));

        var json = GraphJsonSerializer.Serialize(graph);
        var reloaded = GraphJsonSerializer.Deserialize(json, new FlowNodeFactory());

        Assert.Equal(2, reloaded.Nodes.Count);
        Assert.Single(reloaded.Links);
        Assert.Empty(reloaded.Validate());
    }

    [Fact]
    public void ScriptNode_PortTypes_AreDeclared()
    {
        var s = new ScriptNode("s", "1", ScriptNode.OutKind.Real);
        var valueOut = s.Outputs.First(p => p.Name == "value");
        Assert.Equal(RealDescriptor.Instance, valueOut.Type);
        var i = new ScriptNode("i", "1", ScriptNode.OutKind.Integer);
        Assert.Equal(IntegerDescriptor.Instance, i.Outputs.First(p => p.Name == "value").Type);
    }

    private static IPort Port(INode node, PortDirection dir)
        => (dir == PortDirection.In ? node.Inputs : node.Outputs).First(p => p.Kind == PortKind.Exec);
}