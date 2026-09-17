using HalconWorkflow.Abstractions;
using HalconWorkflow.Abstractions.Parameters;
using HalconWorkflow.Core.Contracts;
using HalconWorkflow.Core.Execution;
using HalconWorkflow.Core.Graph;
using HalconWorkflow.Core.Model;
using HalconWorkflow.Core.Serialization;
using HalconWorkflow.MotionDrivers;
using HalconWorkflow.Nodes.Motion;
using HalconWorkflow.Nodes.Motion.Nodes;
using Xunit;

namespace HalconWorkflow.Nodes.Motion.Tests;

internal static class MotionTestHelpers
{
    public static IPort Exec(INode node, PortDirection dir)
        => (dir == PortDirection.In ? node.Inputs : node.Outputs).First(p => p.Kind == PortKind.Exec);

    public static (GraphScheduler Scheduler, MotionRuntime Runtime, PhantomMotionController Controller) BuildEnv(
        PhantomMotionController? controller = null)
    {
        controller ??= new PhantomMotionController("phantom", 0) { MoveDelay = TimeSpan.Zero };
        var runtime = new MotionRuntime();
        runtime.Add("m", controller);
        var scheduler = new GraphScheduler();
        scheduler.Services[typeof(IMotionRuntime)] = runtime;
        return (scheduler, runtime, controller);
    }
}

/// <summary>
/// Stage-7 gate: motion nodes (home/moveAbs/moveRel/line/waitInPos/dout) run against the
/// phantom controller, honour engineering units, and roll back on fault. 
/// / 阶段 7 闸门：运动节点对幻影控制器执行，遵循工程单位，故障回滚。
/// </summary>
public class MotionGraphTests
{
    [Fact]
    public void MotionNodeFactory_ResolvesContracts()
    {
        var factory = new MotionNodeFactory();
        Assert.NotNull(factory.Create(new NodeContract("motion.home", 1), "h"));
        Assert.NotNull(factory.Create(new NodeContract("motion.moveAbs", 1), "a"));
        Assert.NotNull(factory.Create(new NodeContract("motion.moveRel", 1), "r"));
        Assert.NotNull(factory.Create(new NodeContract("motion.line", 1), "l"));
        Assert.NotNull(factory.Create(new NodeContract("motion.waitInPos", 1), "w"));
        Assert.NotNull(factory.Create(new NodeContract("motion.dout", 1), "d"));
        Assert.Null(factory.Create(new NodeContract("motion.unknown", 1), "x"));
    }

    [Fact]
    public void MotionNodes_ExposeParameterObjects()
    {
        Assert.IsAssignableFrom<IParameterized>(new MotionHomeNode("h"));
        Assert.IsAssignableFrom<IParameterized>(new MotionMoveAbsNode("a"));
        Assert.IsAssignableFrom<IParameterized>(new MotionMoveRelNode("r"));
        Assert.IsAssignableFrom<IParameterized>(new MotionLineNode("l"));
        Assert.IsAssignableFrom<IParameterized>(new MotionWaitInPosNode("w"));
        Assert.IsAssignableFrom<IParameterized>(new MotionDoutNode("d"));
    }

    [Fact]
    public void MotionNodes_ParametersReflectControllerAndAxis()
    {
        var home = new MotionHomeNode("h");
        home.Params.Controller = "m";
        home.Params.Axis = 3;
        var meta = ParameterReflection.Summarize(home.Params);
        Assert.Contains(meta, m => m.Name == "Controller");
        Assert.Contains(meta, m => m.Name == "Axis");
        Assert.Contains(meta, m => m.Name == "RollbackOnFault");
    }

    [Fact]
    public async Task Graph_HomeMoveAbsWaitInPos_RoundTrips()
    {
        var (scheduler, runtime, phantom) = MotionTestHelpers.BuildEnv();
        await using var _ = runtime;

        var graph = new GraphModel();
        var home = new MotionHomeNode("h");
        home.Params.Controller = "m";
        home.Params.Axis = 0;
        var move = new MotionMoveAbsNode("a");
        move.Params.Controller = "m";
        move.Params.Axis = 0;
        move.Params.Pos = 42;
        move.Params.Vel = 100;
        move.Params.Acc = 100;
        var wait = new MotionWaitInPosNode("w");
        wait.Params.Controller = "m";
        wait.Params.Axis = 0;
        wait.Params.Tol = 0.01;
        wait.Params.TimeoutMs = 1000;
        graph.AddNode(home);
        graph.AddNode(move);
        graph.AddNode(wait);
        graph.Connect(MotionTestHelpers.Exec(home, PortDirection.Out), MotionTestHelpers.Exec(move, PortDirection.In));
        graph.Connect(MotionTestHelpers.Exec(move, PortDirection.Out), MotionTestHelpers.Exec(wait, PortDirection.In));

        scheduler.Load(graph);
        var result = await scheduler.RunOnceAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(42d, await phantom.ReadPositionAsync(0));
    }

    [Fact]
    public async Task Graph_MoveAbs_AppliesUnitScale()
    {
        var (scheduler, runtime, phantom) = MotionTestHelpers.BuildEnv();
        await using var _ = runtime;

        var graph = new GraphModel();
        var move = new MotionMoveAbsNode("a");
        move.Params.Controller = "m";
        move.Params.Axis = 1;
        move.Params.Pos = 10;
        move.Params.Scale = 2; // 10 engineering units → 20 native counts · 10 工程单位 → 20 原生计数
        graph.AddNode(move);

        scheduler.Load(graph);
        var result = await scheduler.RunOnceAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(20d, await phantom.ReadPositionAsync(1));
    }

    [Fact]
    public async Task Graph_Line_IsSubmittedAsSingleCommand()
    {
        var (scheduler, runtime, phantom) = MotionTestHelpers.BuildEnv();
        await using var _ = runtime;

        var graph = new GraphModel();
        var line = new MotionLineNode("l");
        line.Params.Controller = "m";
        line.Params.Axes = "0,1";
        line.Params.Dest = "3,4";
        graph.AddNode(line);

        scheduler.Load(graph);
        var result = await scheduler.RunOnceAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(phantom.CommandLog, e => e.StartsWith("line:", StringComparison.Ordinal));
        Assert.Equal(3d, await phantom.ReadPositionAsync(0));
        Assert.Equal(4d, await phantom.ReadPositionAsync(1));
    }

    [Fact]
    public async Task Graph_Line_MismatchedLengths_Faults()
    {
        var (scheduler, runtime, _) = MotionTestHelpers.BuildEnv();
        await using var __ = runtime;

        var graph = new GraphModel();
        var line = new MotionLineNode("l");
        line.Params.Controller = "m";
        line.Params.Axes = "0,1";
        line.Params.Dest = "3";
        graph.AddNode(line);

        scheduler.Load(graph);
        var result = await scheduler.RunOnceAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, result.FaultedNodes);
    }

    [Fact]
    public async Task Graph_WaitInPos_TimeoutFaults()
    {
        var never = new PhantomMotionController("phantom", 0) { MoveDelay = TimeSpan.Zero, HoldInPosition = false };
        var (scheduler, runtime, _) = MotionTestHelpers.BuildEnv(never);
        await using var __ = runtime;
        await never.MoveAbsoluteAsync(0, 5, 10, 10, CancellationToken.None); // axis never reports in-position · 轴永不报到位

        var graph = new GraphModel();
        var wait = new MotionWaitInPosNode("w");
        wait.Params.Controller = "m";
        wait.Params.Axis = 0;
        wait.Params.TimeoutMs = 50;
        graph.AddNode(wait);

        scheduler.Load(graph);
        var result = await scheduler.RunOnceAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, result.FaultedNodes);
    }

    [Fact]
    public async Task Graph_MoveFault_RollsBackWithDecelStop()
    {
        var faulting = new FaultingMotionController();
        var runtime = new MotionRuntime();
        runtime.Add("m", faulting);
        await using var _ = runtime;
        var scheduler = new GraphScheduler();
        scheduler.Services[typeof(IMotionRuntime)] = runtime;

        var graph = new GraphModel();
        var move = new MotionMoveAbsNode("a");
        move.Params.Controller = "m";
        move.Params.Axis = 0;
        move.Params.Pos = 5;
        move.Params.RollbackOnFault = true;
        graph.AddNode(move);

        scheduler.Load(graph);
        var result = await scheduler.RunOnceAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(StopMode.Decel, faulting.StopLog);
    }

    [Fact]
    public void GraphJson_MotionRoundTrip_IsIdempotent()
    {
        var graph = new GraphModel();
        var move = new MotionMoveAbsNode("a");
        move.Params.Controller = "m";
        move.Params.Axis = 2;
        move.Params.Pos = 7.5;
        graph.AddNode(move);

        var json1 = GraphJsonSerializer.Serialize(graph);
        var graph2 = GraphJsonSerializer.Deserialize(json1, new MotionNodeFactory());
        var json2 = GraphJsonSerializer.Serialize(graph2);

        Assert.Equal(json1, json2);
    }
}

/// <summary>Unit conversion / CSV parsing tests. / 单位换算与 CSV 解析测试</summary>
public class MotionUnitsTests
{
    [Fact]
    public void ToNative_And_FromNative_RoundTrip()
    {
        Assert.Equal(20d, MotionUnits.ToNative(10, 2));
        Assert.Equal(10d, MotionUnits.FromNative(20, 2));
    }

    [Fact]
    public void ParseValues_HandlesSeparatorsAndInvariantCulture()
    {
        Assert.Equal(new[] { 1.5, 2.25, 3.0 }, MotionUnits.ParseValues("1.5, 2.25;3"));
        Assert.Equal(new[] { 0, 1, 2 }, MotionUnits.ParseAxes("0 1\t2"));
    }

    [Fact]
    public void ParseValues_RejectsNonNumeric()
    {
        Assert.Throws<FormatException>(() => MotionUnits.ParseValues("1,x"));
    }
}

/// <summary>A controller whose moves always fault, to exercise rollback. / 移动必失败的控制器，用于验证回滚</summary>
internal sealed class FaultingMotionController : IMotionController
{
    public List<StopMode> StopLog { get; } = [];

    public string VendorId => "faulting";
    public int CardNo => 0;

    public Task HomeAsync(int axis, CancellationToken ct) => Task.CompletedTask;
    public Task MoveAbsoluteAsync(int axis, double pos, double vel, double acc, CancellationToken ct)
        => throw new InvalidOperationException("drive fault");
    public Task MoveRelativeAsync(int axis, double dist, double vel, CancellationToken ct)
        => throw new InvalidOperationException("drive fault");
    public Task MoveLineAsync(int[] axes, double[] dest, double vel, CancellationToken ct)
        => throw new InvalidOperationException("drive fault");
    public Task WaitInPositionAsync(int axis, double tol, CancellationToken ct) => Task.CompletedTask;

    public Task StopAsync(StopMode mode)
    {
        StopLog.Add(mode);
        return Task.CompletedTask;
    }

    public Task<double> ReadPositionAsync(int axis) => Task.FromResult(0.0);
    public Task SetDigitalOutAsync(int port, bool on) => Task.CompletedTask;
    public IObservable<AxisState> Watch(int axis) => new EmptyObservable();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class EmptyObservable : IObservable<AxisState>
    {
        public IDisposable Subscribe(IObserver<AxisState> observer) => new Subscription();

        private sealed class Subscription : IDisposable
        {
            public void Dispose() { }
        }
    }
}
