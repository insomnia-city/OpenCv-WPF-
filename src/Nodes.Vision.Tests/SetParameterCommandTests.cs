using HalconWorkflow.Abstractions;
using HalconWorkflow.Abstractions.Undo;
using HalconWorkflow.Core.Model;
using HalconWorkflow.Nodes.Vision.Commands;
using HalconWorkflow.Nodes.Vision.Nodes;
using Xunit;

namespace HalconWorkflow.Nodes.Vision.Tests;

/// <summary>
/// §4.4/§9.2: undoable parameter writes round-trip through ParameterReflection.
/// / §4.4/§9.2：可撤销参数写经 ParameterReflection 往返（Do/Undo/Redo）。
/// </summary>
public class SetParameterCommandTests
{
    private static IParameterized CreateThreshold() =>
        (IParameterized)new VisionNodeFactory().Create(new NodeContract("vision.threshold", 2), "t")!;

    [Fact]
    public async Task Apply_Undo_Redo_RoundTripsValue()
    {
        var node = CreateThreshold();
        var parameters = (ThresholdParameters)node.ParameterObject;
        var undo = new UndoService();
        Assert.Equal(128, parameters.Min);

        await undo.PushAndRunAsync(new SetParameterCommand(node, "Min", 200L), CancellationToken.None);
        Assert.Equal(200, parameters.Min);

        Assert.True(await undo.UndoAsync(CancellationToken.None));
        Assert.Equal(128, parameters.Min);

        Assert.True(await undo.RedoAsync(CancellationToken.None));
        Assert.Equal(200, parameters.Min);
    }

    [Fact]
    public void SetParameterCommand_ExposesNameInDescription()
    {
        var cmd = new SetParameterCommand(CreateThreshold(), "Min", 100L);
        Assert.Equal("Set 'Min'", cmd.Description);
    }

    [Fact]
    public void Factory_ReservesVisionThresholdV1_ForLegacyScaffold()
    {
        // v1 belongs to the handle-based scaffold nodes in Runtime; v2 is the real vision line (§12 migration). 
        // v1 归 Runtime 的句柄式脚手架;v2 才是真实视觉链（§12 迁移）
        var factory = new VisionNodeFactory();
        Assert.Null(factory.Create(new NodeContract("vision.threshold", 1), "t"));
        Assert.IsType<ThresholdParameters>(((IParameterized)factory.Create(new NodeContract("vision.threshold", 2), "t")!).ParameterObject);
        Assert.IsType<GrabParameters>(((IParameterized)factory.Create(new NodeContract("vision.grab", 1), "g")!).ParameterObject);
    }
}