using HalconWorkflow.Core.Graph;

namespace HalconWorkflow.Core.Tests;

public class GraphValidationTests
{
    [Fact]
    public void Connect_ValidExecAndImageChain_Succeeds()
    {
        var graph = new GraphModel();
        graph.AddNode(TestNodes.Grabber("cam"));
        graph.AddNode(TestNodes.Threshold());
        var grab = graph.Nodes["cam"];
        var thresh = graph.Nodes["threshold"];

        Assert.NotNull(graph.Connect(grab.GetOutput("exec"), thresh.GetInput("exec")));
        Assert.NotNull(graph.Connect(grab.GetOutput("Image"), thresh.GetInput("Image")));
        Assert.Empty(graph.Validate());
    }

    [Fact]
    public void Connect_DataTypeMismatch_Fails()
    {
        var graph = new GraphModel();
        graph.AddNode(TestNodes.TextOut("txt"));
        graph.AddNode(TestNodes.Threshold());
        var outText = graph.Nodes["txt"];
        var thresh = graph.Nodes["threshold"];

        // String → Image is not assignable. · String → Image 不可赋值
        Assert.Null(graph.Connect(outText.GetOutput("Value"), thresh.GetInput("Image")));
    }

    [Fact]
    public void Connect_SelfLoop_Fails()
    {
        var graph = new GraphModel();
        graph.AddNode(TestNodes.Threshold());
        var thresh = graph.Nodes["threshold"];

        Assert.Null(graph.Connect(thresh.GetOutput("exec"), thresh.GetInput("exec")));
    }

    [Fact]
    public void Connect_ExecToData_Fails()
    {
        var graph = new GraphModel();
        graph.AddNode(TestNodes.Grabber("cam"));
        graph.AddNode(TestNodes.Threshold());
        var grab = graph.Nodes["cam"];
        var thresh = graph.Nodes["threshold"];

        var issue = graph.ValidateLink(grab.GetOutput("exec"), thresh.GetInput("Image"));
        Assert.True(issue.HasValue);
        Assert.Equal(ValidationIssueKind.InvalidLink, issue.Value.Kind);
    }

    [Fact]
    public void Cycle_Detected()
    {
        var graph = new GraphModel();
        graph.AddNode(TestNodes.Threshold("a"));
        graph.AddNode(TestNodes.Threshold("b"));
        var a = graph.Nodes["a"];
        var b = graph.Nodes["b"];

        Assert.NotNull(graph.Connect(a.GetOutput("exec"), b.GetInput("exec")));
        // b → a would close the cycle. · b → a 将构成闭环
        var issue = graph.ValidateLink(b.GetOutput("exec"), a.GetInput("exec"));
        Assert.True(issue.HasValue);
        Assert.Equal(ValidationIssueKind.Cycle, issue.Value.Kind);
        Assert.Null(graph.Connect(b.GetOutput("exec"), a.GetInput("exec")));
    }

    [Fact]
    public void Validate_DanglingDataInput_Reported()
    {
        var graph = new GraphModel();
        graph.AddNode(TestNodes.Grabber("cam"));
        graph.AddNode(TestNodes.Threshold());
        var grab = graph.Nodes["cam"];
        var thresh = graph.Nodes["threshold"];

        // Link exec but leave Image data input dangling. · 只连控制流，Image 数据输入悬空
        Assert.NotNull(graph.Connect(grab.GetOutput("exec"), thresh.GetInput("exec")));
        var issues = graph.Validate();
        Assert.Contains(issues, i => i.Kind == ValidationIssueKind.DanglingPort);
    }

    [Fact]
    public void DataInput_SingleSource_Enforced()
    {
        var graph = new GraphModel();
        graph.AddNode(TestNodes.Grabber("cam1"));
        graph.AddNode(TestNodes.Grabber("cam2"));
        graph.AddNode(TestNodes.Threshold());
        var g1 = graph.Nodes["cam1"];
        var g2 = graph.Nodes["cam2"];
        var thresh = graph.Nodes["threshold"];

        Assert.NotNull(graph.Connect(g1.GetOutput("Image"), thresh.GetInput("Image")));
        Assert.Null(graph.Connect(g2.GetOutput("Image"), thresh.GetInput("Image")));
    }

    [Fact]
    public void TopologicalOrder_RespectsExecChains()
    {
        var graph = new GraphModel();
        // Added out of order to ensure sort not insertion order. · 乱序添加以验证排序不受插入序影响
        graph.AddNode(TestNodes.ResultOut("result"));
        graph.AddNode(TestNodes.Threshold());
        graph.AddNode(TestNodes.Grabber("cam"));
        var grab = graph.Nodes["cam"];
        var thresh = graph.Nodes["threshold"];
        var result = graph.Nodes["result"];

        Assert.NotNull(graph.Connect(grab.GetOutput("exec"), thresh.GetInput("exec")));
        Assert.NotNull(graph.Connect(thresh.GetOutput("exec"), result.GetInput("exec")));
        Assert.NotNull(graph.Connect(grab.GetOutput("Image"), thresh.GetInput("Image")));
        Assert.NotNull(graph.Connect(thresh.GetOutput("Region"), result.GetInput("Region")));

        graph.Validate();
        var order = graph.TopologicalOrder.Select(n => n.Id).ToList();
        var iCam = order.IndexOf("cam");
        var iThr = order.IndexOf("threshold");
        var iRes = order.IndexOf("result");
        Assert.True(iCam >= 0 && iThr >= 0 && iRes >= 0);
        Assert.True(iCam < iThr && iThr < iRes);
    }
}