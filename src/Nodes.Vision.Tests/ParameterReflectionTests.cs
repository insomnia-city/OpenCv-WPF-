using HalconWorkflow.Abstractions.Parameters;
using HalconWorkflow.Nodes.Vision.Nodes;
using Xunit;

namespace HalconWorkflow.Nodes.Vision.Tests;

/// <summary>
/// §4.4/§6.2: NodeParameter metadata drives the reflected property panel; typed
/// writes are range-checked and return the previous value for undo (§9.2 command).
/// / §4.4/§6.2：NodeParameter 元数据驱动反射属性面板;类型化写入带范围校验并返回旧值供撤销（§9.2 命令）。
/// </summary>
public class ParameterReflectionTests
{
    [Fact]
    public void Summarize_ReadsGroupAndRange()
    {
        var meta = ParameterReflection.Summarize(new ThresholdParameters());
        var min = Assert.Single(meta, m => m.Name == "Min");
        Assert.Equal(ParameterKind.Integer, min.Kind);
        Assert.Equal("Threshold", min.Group);
        Assert.Equal(0.0, min.Min);
        Assert.Equal(255.0, min.Max);
        Assert.Equal(128L, min.Value);
    }

    [Fact]
    public void Apply_SetsValue_AndReturnsOld()
    {
        var p = new GrabParameters { Width = 640, Height = 480 };
        var (old, @new) = ParameterReflection.Apply(p, "Width", 960);
        Assert.Equal(640, old);
        Assert.Equal(960, @new);
        Assert.Equal(960, p.Width);
    }

    [Fact]
    public void Apply_RejectsOutOfRange()
    {
        var p = new ThresholdParameters();
        Assert.Throws<ArgumentOutOfRangeException>(() => ParameterReflection.Apply(p, "Min", -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ParameterReflection.Apply(p, "Max", 256));
        Assert.Equal(128, p.Min); // untouched · 未被改动
    }

    [Fact]
    public void Apply_RejectsUnknownName()
    {
        Assert.Throws<ArgumentException>(() => ParameterReflection.Apply(new GrabParameters(), "NoSuch", 1));
    }

    [Fact]
    public void Apply_CoercesNumericKinds()
    {
        var p = new GrabParameters(); // int property
        ParameterReflection.Apply(p, "Width", 512.0);
        Assert.Equal(512, p.Width); // double→int coercion / 实数→整数提升
    }

    [Fact]
    public void VisionNodes_ExposeParameterObjects()
    {
        Assert.IsType<GrabParameters>(((IParameterized)CreateNode("grab")).ParameterObject);
        Assert.IsType<ThresholdParameters>(((IParameterized)CreateNode("threshold")).ParameterObject);
    }

    private static object CreateNode(string contractName)
    {
        // vision.threshold lives at v2; v1 is reserved to the legacy scaffold nodes. · vision.threshold 为 v2;v1 保留给旧脚手架
        var version = contractName == "threshold" ? 2 : 1;
        return new VisionNodeFactory().Create(new("vision." + contractName, version), "x")!;
    }
}