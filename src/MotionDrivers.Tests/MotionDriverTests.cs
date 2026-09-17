using HalconWorkflow.Abstractions;
using HalconWorkflow.MotionDrivers;
using HalconWorkflow.MotionDrivers.Native;
using Xunit;

namespace HalconWorkflow.MotionDrivers.Tests;

/// <summary>
/// Stage-7 gate: native load rules (process-level once, explicit missing-native) and the
/// §6.3 phantom software fallback. / 阶段 7 闸门：原生加载规则(进程级一次、缺失显式)与 §6.3 幻影软件回退。
/// </summary>
public class MotionDriverTests
{
    [Fact]
    public void NativeLoader_MissingLibrary_ReportsExplicitFallback()
    {
        var result = NativeMotionLibrary.Load("hw_definitely_absent_vendor_sdk_9f3a");

        Assert.True(result.IsMissing);
        Assert.False(result.IsLoaded);
        Assert.Equal(NativeLoadScope.Process, result.Scope);
        Assert.Contains("hw_definitely_absent_vendor_sdk_9f3a", result.Message);
        Assert.Contains("fallback", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NativeLoader_IsIdempotentAtProcessScope()
    {
        // Native vendor SDKs must be loaded at most once per process and never into an ALC (ADR-006).
        // 原生厂商 SDK 每进程最多加载一次且绝不进入 ALC（ADR-006）。
        string library = OperatingSystem.IsWindows() ? "kernel32.dll" : "libc";
        try
        {
            var first = NativeMotionLibrary.Load(library);
            if (first.IsMissing) return; // host lacks the probe library; rule still covered by the missing test
            var second = NativeMotionLibrary.Load(library);

            Assert.True(first.IsLoaded);
            Assert.Equal(NativeLoadScope.Process, first.Scope);
            Assert.Equal(first.Handle, second.Handle);
        }
        finally
        {
            NativeMotionLibrary.Unload(library);
        }
    }

    [Fact]
    public void NativeController_MissingNative_ThrowsExplicitError()
    {
        if (NativeMotionLibrary.Load("gmotion").IsLoaded) return;

        var ex = Assert.Throws<NativeLibraryMissingException>(() => new NativeMotionController("googol", 0, "gmotion"));
        Assert.Equal("gmotion", ex.Library);
        Assert.Contains("missing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NativeController_WithInjectedApi_MapsSemanticCalls()
    {
        var api = new FakeNativeApi();
        var controller = new NativeMotionController("googol", 2, "fake", api);

        await controller.HomeAsync(1, CancellationToken.None);
        await controller.MoveAbsoluteAsync(1, 250, 50, 80, CancellationToken.None);
        await controller.MoveLineAsync([0, 1], [10, 20], 30, CancellationToken.None);
        await controller.SetDigitalOutAsync(3, true);
        await controller.StopAsync(StopMode.EStop);

        Assert.Equal(2, api.Opened);
        Assert.Equal(1, api.Homes);
        Assert.Equal((2, 1, 250d), api.LastMoveAbs);
        Assert.Equal((2, 2), api.LastLine);
        Assert.Equal((2, 3, 1), api.LastDout);
        Assert.Equal((2, (int)StopMode.EStop), api.LastStop);
    }

    [Fact]
    public async Task Phantom_HomeAndMoveAbsolute_UpdatePositionAndLog()
    {
        var c = new PhantomMotionController { MoveDelay = TimeSpan.Zero };
        await c.HomeAsync(0, CancellationToken.None);
        await c.MoveAbsoluteAsync(0, 12.5, 100, 100, CancellationToken.None);

        Assert.Equal(12.5, await c.ReadPositionAsync(0));
        Assert.Contains("home:0", c.CommandLog);
        Assert.Contains("moveAbs:0->12.5", c.CommandLog);
    }

    [Fact]
    public async Task Phantom_MoveLine_IsSingleCommand()
    {
        var c = new PhantomMotionController { MoveDelay = TimeSpan.Zero };
        await c.MoveLineAsync([0, 1, 2], [1, 2, 3], 10, CancellationToken.None);

        Assert.Single(c.CommandLog, e => e.StartsWith("line:", StringComparison.Ordinal));
        Assert.Equal(1d, await c.ReadPositionAsync(0));
        Assert.Equal(2d, await c.ReadPositionAsync(1));
        Assert.Equal(3d, await c.ReadPositionAsync(2));
    }

    [Fact]
    public async Task Phantom_WaitInPosition_ReturnsWhenHolding()
    {
        var c = new PhantomMotionController { MoveDelay = TimeSpan.Zero, HoldInPosition = true };
        await c.MoveAbsoluteAsync(0, 5, 10, 10, CancellationToken.None);
        await c.WaitInPositionAsync(0, 0.01, CancellationToken.None);
    }

    [Fact]
    public async Task Phantom_WaitInPosition_CancelsWhenNotHolding()
    {
        var c = new PhantomMotionController { MoveDelay = TimeSpan.Zero, HoldInPosition = false };
        await c.MoveAbsoluteAsync(0, 5, 10, 10, CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => c.WaitInPositionAsync(0, 0.01, cts.Token));
    }

    [Fact]
    public async Task Phantom_Stop_RecordsModes()
    {
        var c = new PhantomMotionController { MoveDelay = TimeSpan.Zero };
        await c.StopAsync(StopMode.Decel);
        await c.StopAsync(StopMode.EStop);

        Assert.Equal(new[] { StopMode.Decel, StopMode.EStop }, c.StopLog);
    }

    [Fact]
    public async Task Phantom_DigitalOut_RoundTrips()
    {
        var c = new PhantomMotionController { MoveDelay = TimeSpan.Zero };
        await c.SetDigitalOutAsync(7, true);
        Assert.True(c.ReadDigitalOut(7));
        await c.SetDigitalOutAsync(7, false);
        Assert.False(c.ReadDigitalOut(7));
    }

    [Fact]
    public async Task Phantom_Watch_EmitsAxisState()
    {
        var c = new PhantomMotionController { MoveDelay = TimeSpan.Zero };
        var seen = new List<AxisState>();
        using (c.Watch(0).Subscribe(new Collector(seen)))
        {
            await c.HomeAsync(0, CancellationToken.None);
            await c.MoveAbsoluteAsync(0, 9, 10, 10, CancellationToken.None);
        }

        Assert.NotEmpty(seen);
        Assert.Contains(seen, s => s.Axis == 0 && Math.Abs(s.Position - 9) < 1e-9);
    }

    [Fact]
    public void Factory_KnownVendorMissingNative_FallsBackToPhantomWithNotice()
    {
        if (NativeMotionLibrary.Load("gmotion").IsLoaded) return;

        var probe = MotionDriverFactory.Probe("googol", 0);
        Assert.False(probe.IsNative);
        Assert.True(probe.IsFallback);
        Assert.Contains("gmotion", probe.Message);

        var controller = MotionDriverFactory.Create("googol", 0);
        Assert.IsType<PhantomMotionController>(controller);
    }

    [Fact]
    public void Factory_UnknownVendor_FallsBack()
    {
        var probe = MotionDriverFactory.Probe("nope-corp", 1);
        Assert.True(probe.IsFallback);
        Assert.Contains("unknown", probe.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<PhantomMotionController>(MotionDriverFactory.Create("nope-corp", 1));
    }

    private sealed class Collector(List<AxisState> sink) : IObserver<AxisState>
    {
        public void OnNext(AxisState value) => sink.Add(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private sealed class FakeNativeApi : INativeMotionApi
    {
        public int Opened;
        public int Homes;
        public (int Card, int Axis, double Pos) LastMoveAbs;
        public (int Card, int Count) LastLine;
        public (int Card, int Port, int On) LastDout;
        public (int Card, int Mode) LastStop;
        private double _pos;

        public int Open(int cardNo) { Opened = cardNo; return 0; }
        public void Close(int cardNo) { }
        public void Home(int cardNo, int axis) { Homes++; _pos = 0; }
        public void MoveAbsolute(int cardNo, int axis, double pos, double vel, double acc)
        { LastMoveAbs = (cardNo, axis, pos); _pos = pos; }
        public void MoveLine(int cardNo, int[] axes, double[] dest, double vel) => LastLine = (cardNo, axes.Length);
        public void Stop(int cardNo, int mode) => LastStop = (cardNo, mode);
        public double ReadPosition(int cardNo, int axis) => _pos;
        public void SetDigitalOut(int cardNo, int port, int on) => LastDout = (cardNo, port, on);
    }
}
