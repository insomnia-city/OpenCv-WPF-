using System.Text.Json.Nodes;
using HalconWorkflow.Core.Graph;
using HalconWorkflow.Core.Model;
using HalconWorkflow.Core.Serialization;
using HalconWorkflow.Runtime.Nodes;

namespace HalconWorkflow.Runtime.Tests;

public class RuntimeTests
{
    [Fact]
    public void Options_ParseOnce_Valid()
    {
        var o = RuntimeOptions.Parse(["graph.json", "--once"]);
        Assert.True(o.Once);
        Assert.Equal("graph.json", o.GraphFile);
    }

    [Fact]
    public void Options_ParseCyclesAndInterval()
    {
        var o = RuntimeOptions.Parse(["graph.json", "--cycles", "5", "--interval", "200"]);
        Assert.Equal(5, o.Cycles);
        Assert.Equal(200, o.IntervalMs);
    }

    [Fact]
    public void Options_UnknownFlag_Throws()
    {
        Assert.Throws<ArgumentException>(() => RuntimeOptions.Parse(["graph.json", "--nope"]));
    }

    [Fact]
    public void Options_OnceAndCycles_Conflicting()
    {
        Assert.Throws<ArgumentException>(() => RuntimeOptions.Parse(["graph.json", "--once", "--cycles", "3"]));
    }

    [Fact]
    public void SampleFactory_ResolvesContracts()
    {
        var factory = new SampleNodeFactory();
        Assert.NotNull(factory.Create(new NodeContract("vision.grabber", 1), "cam"));
        Assert.NotNull(factory.Create(new NodeContract("app.decision", 1), "dv"));
        Assert.Null(factory.Create(new NodeContract("vision.gone", 1), "x"));
    }

    [Fact]
    public void StartNode_ExposesExecOutput()
    {
        var s = SampleNodes.Start("start");
        Assert.Single(s.Outputs);
        Assert.Equal(PortKind.Exec, s.Outputs[0].Kind);
        Assert.Empty(s.Inputs);
    }

    [Fact]
    public void HeadlessHost_LoadsSampleGraphFromDisk()
    {
        var path = WriteSampleGraph();
        try
        {
            var host = new HeadlessHost(new RuntimeOptions { GraphFile = path });
            var graph = host.LoadGraph(_ => { });
            Assert.Equal(5, graph.Nodes.Count);
            Assert.Empty(graph.Validate());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task HeadlessHost_RunOnce_Succeeds()
    {
        var path = WriteSampleGraph();
        try
        {
            var traces = new StringWriter();
            var host = new HeadlessHost(new RuntimeOptions { GraphFile = path, Once = true }, traces)!;
            var exit = await host.RunAsync(CancellationToken.None, _ => { });
            Assert.Equal(0, exit);
            Assert.Contains("Completed", traces.ToString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task HeadlessHost_RunOnce_ProducesTraceFile()
    {
        var path = WriteSampleGraph();
        var tracePath = Path.Combine(Path.GetTempPath(), $"trace_{Guid.NewGuid():N}.jsonl");
        try
        {
            var host = new HeadlessHost(new RuntimeOptions { GraphFile = path, Once = true, Quiet = true, TraceFile = tracePath });
            var exit = await host.RunAsync(CancellationToken.None, _ => { });
            Assert.Equal(0, exit);

            var lines = File.ReadAllLines(tracePath);
            Assert.NotEmpty(lines);
            var first = JsonNode.Parse(lines[0])!;
            Assert.NotNull((string?)first["trigger_id"]);
            Assert.Equal("trace", (string?)first["kind"]);
        }
        finally
        {
            File.Delete(path);
            if (File.Exists(tracePath)) File.Delete(tracePath);
        }
    }

    [Fact]
    public async Task HeadlessHost_Cycles_Timeout_Cancels()
    {
        var path = WriteSampleGraph();
        try
        {
            // 30ms hard timeout while the cycle itself is ~fast; forces cancellation path. 
            // 30ms 硬超时;强制走取消路径
            var host = new HeadlessHost(new RuntimeOptions
            {
                GraphFile = path,
                Cycles = 3,
                IntervalMs = 200,
                TimeoutMs = 30,
                Quiet = true
            });
            var exit = await host.RunAsync(CancellationToken.None, _ => { });
            Assert.InRange(exit, 0, 1);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task SchedulerStopFromCaller_DoesNotThrow()
    {
        var path = WriteSampleGraph();
        try
        {
            using var cts = new CancellationTokenSource(10);
            var host = new HeadlessHost(new RuntimeOptions { GraphFile = path, Cycles = 5, IntervalMs = 50, TimeoutMs = 10, Quiet = true });
            var exit = await host.RunAsync(cts.Token, _ => { });
            Assert.InRange(exit, 0, 1);
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// Writes the canonical 5-node demo graph (grabber → threshold → decision → result). 
    /// 写出 5 节点的标准演示图(grabber → threshold → decision → result)
    /// </summary>
    private static string WriteSampleGraph()
    {
        var graph = new GraphModel();
        graph.AddNode(SampleNodes.Grabber("cam"));
        graph.AddNode(SampleNodes.Threshold("threshold"));
        graph.AddNode(SampleNodes.Decision("decision"));
        graph.AddNode(SampleNodes.LogResult("result"));
        graph.AddNode(SampleNodes.Start("start"));

        var grab = graph.Nodes["cam"];
        var thresh = graph.Nodes["threshold"];
        var decision = graph.Nodes["decision"];
        var result = graph.Nodes["result"];
        var start = graph.Nodes["start"];

        graph.Connect(start.Node.Outputs.First(p => p.Kind == PortKind.Exec), grab.Node.Inputs.First(p => p.Kind == PortKind.Exec));
        graph.Connect(grab.Node.Outputs.First(p => p.Kind == PortKind.Exec), thresh.Node.Inputs.First(p => p.Kind == PortKind.Exec));
        graph.Connect(grab.Node.Outputs.First(p => p.Name == "Image"), thresh.Node.Inputs.First(p => p.Name == "Image"));
        graph.Connect(thresh.Node.Outputs.First(p => p.Kind == PortKind.Exec), decision.Node.Inputs.First(p => p.Kind == PortKind.Exec));
        graph.Connect(thresh.Node.Outputs.First(p => p.Name == "Region"), decision.Node.Inputs.First(p => p.Name == "Inspection"));
        graph.Connect(decision.Node.Outputs.First(p => p.Kind == PortKind.Exec), result.Node.Inputs.First(p => p.Kind == PortKind.Exec));
        graph.Connect(decision.Node.Outputs.First(p => p.Name == "Judgement"), result.Node.Inputs.First(p => p.Name == "Judgement"));

        var path = Path.Combine(Path.GetTempPath(), $"graph_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, GraphJsonSerializer.Serialize(graph));
        return path;
    }
}