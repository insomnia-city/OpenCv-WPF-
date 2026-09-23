using System;
using System.Linq;
using HalconWorkflow.App.Services;
using HalconWorkflow.App.ViewModels;
using HalconWorkflow.Storage;
using Xunit;

namespace HalconWorkflow.App.Tests;

/// <summary>
/// Stage-24 image window view model (§4.5): opens for a single node, filters out foreign-node
/// frames, drains latest-published via <see cref="ImageWindowViewModel.Tick"/> with live gating,
/// and navigates history without leaving stale frames visible.
/// · 阶段24 图像窗视图模型(§4.5)：为单个节点打开、过滤异节点帧、Tick 排空最新发布帧并做
///   实时门控、历史导航不残留过期帧。
/// </summary>
public sealed class ImageWindowViewModelTests
{
    private static ImageWindowViewModel Create(LocalizationService? loc = null, PreviewRing? preview = null)
        => new(loc ?? new LocalizationService(), preview ?? new PreviewRing(capacity: 4));

    private static PreviewFrame Frame(string node, int seq, byte[]? image = null)
        => new(node, DateTimeOffset.UtcNow.AddSeconds(seq), image, $"seq={seq}");

    [Fact]
    public void Open_SubscribesAndClose_Unsubscribes_WithoutLeaking()
    {
        var ring = new PreviewRing();
        var vm = Create(preview: ring);

        // Repeated open/close churn must not accumulate subscribers.
        for (var i = 0; i < 200; i++)
        {
            var vm2 = new ImageWindowViewModel(new LocalizationService(), ring);
            vm2.Open("x", "X");
            vm2.Close();
        }

        vm.Open("n1", "Node 1");
        Assert.Equal("Node 1", vm.NodeLabel);
        Assert.True(vm.Live);

        ring.Publish(Frame("n1", 1));
        vm.Tick();
        Assert.Single(vm.Frames);

        // After close the VM must ignore further publishes: Tick() drains nothing.
        vm.Close();
        ring.Publish(Frame("n1", 2));
        vm.Tick();
        Assert.Empty(vm.Frames);
        Assert.Null(vm.CurrentImage);
    }

    [Fact]
    public void Tick_DrainsOnlyLatestAndOnlyForOpenedNode()
    {
        var ring = new PreviewRing();
        var vm = Create(preview: ring);
        vm.Open("n1", "Node 1");

        // Foreign node frames are ignored entirely (node filter).
        ring.Publish(Frame("n2", 1));
        ring.Publish(Frame("n2", 2));
        vm.Tick();
        Assert.Empty(vm.Frames);
        Assert.Null(vm.CurrentImage);

        // Multiple quick publishes collapse to the newest one.
        ring.Publish(Frame("n1", 1));
        ring.Publish(Frame("n1", 2));
        vm.Tick();
        Assert.Single(vm.Frames);
        Assert.Equal("seq=2", vm.CurrentImage?.Summary);
        Assert.True(vm.HasImage);

        vm.Close();
    }

    [Fact]
    public void LiveDisabled_IgnoresNewFrames_AndRefreshReattaches()
    {
        var ring = new PreviewRing();
        var vm = Create(preview: ring);
        vm.Open("n1", "Node 1");

        ring.Publish(Frame("n1", 1));
        vm.Tick();
        Assert.NotNull(vm.CurrentImage);

        vm.Live = false;
        ring.Publish(Frame("n1", 2));
        vm.Tick();
        Assert.Equal("seq=1", vm.CurrentImage?.Summary);

        vm.RefreshCommand.Execute(null);
        Assert.True(vm.Live);
        Assert.Equal("seq=2", vm.CurrentImage?.Summary);

        vm.Close();
    }

    [Fact]
    public void History_Navigation_ScansCapturedFramesAndNeverOvershoots()
    {
        var ring = new PreviewRing();
        var vm = Create(preview: ring);
        vm.Open("n1", "Node 1");

        for (var i = 1; i <= 3; i++)
        {
            ring.Publish(Frame("n1", i));
            vm.Tick();
        }
        Assert.Equal(3, vm.Frames.Count);

        vm.PreviousCommand.Execute(null);
        Assert.True(vm.BrowsingHistory);
        Assert.Equal("seq=2", vm.CurrentImage?.Summary);
        Assert.Equal("2/3", vm.HistoryText);

        vm.PreviousCommand.Execute(null);
        Assert.Equal("seq=1", vm.CurrentImage?.Summary);
        Assert.Equal("1/3", vm.HistoryText);

        // Bounds: further previous is a no-op.
        vm.PreviousCommand.Execute(null);
        Assert.Equal("seq=1", vm.CurrentImage?.Summary);

        vm.NextCommand.Execute(null);
        Assert.Equal("seq=2", vm.CurrentImage?.Summary);
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
        Assert.Equal("seq=3", vm.CurrentImage?.Summary);
        Assert.Equal("3/3", vm.HistoryText);

        vm.Close();
    }

    [Fact]
    public void BrowsingHistory_GatesLiveDrain_UntilRefresh()
    {
        var ring = new PreviewRing();
        var vm = Create(preview: ring);
        vm.Open("n1", "Node 1");

        for (var i = 1; i <= 2; i++)
        {
            ring.Publish(Frame("n1", i));
            vm.Tick();
        }
        Assert.Equal("seq=2", vm.CurrentImage?.Summary);

        // Enter history browsing; new publishes must not overwrite the browsed frame.
        vm.PreviousCommand.Execute(null);
        Assert.True(vm.BrowsingHistory);
        ring.Publish(Frame("n1", 3));
        vm.Tick();
        Assert.Equal("seq=1", vm.CurrentImage?.Summary);

        // Refresh reattaches to live and jumps to the newest.
        vm.RefreshCommand.Execute(null);
        Assert.False(vm.BrowsingHistory);
        Assert.Equal("seq=3", vm.CurrentImage?.Summary);

        vm.Close();
    }

    [Fact]
    public void Clear_DropsFramesAndPendingSlot()
    {
        var ring = new PreviewRing();
        var vm = Create(preview: ring);
        vm.Open("n1", "Node 1");

        ring.Publish(Frame("n1", 1));
        vm.Tick();
        Assert.Single(vm.Frames);

        vm.ClearCommand.Execute(null);
        Assert.Empty(vm.Frames);
        Assert.Null(vm.CurrentImage);
        Assert.False(vm.HasImage);
        Assert.False(vm.BrowsingHistory);

        // A stale publish that happened before Clear must not resurrect on the next Tick.
        ring.Publish(Frame("n1", 2));
        vm.ClearCommand.Execute(null);
        vm.Tick();
        Assert.Empty(vm.Frames);
        Assert.Null(vm.CurrentImage);

        vm.Close();
    }

    [Fact]
    public void Refresh_BootsFromRing_EvenWhenNothingDrained()
    {
        var ring = new PreviewRing();
        ring.Publish(Frame("n1", 7));
        var vm = Create(preview: ring);
        vm.Open("n1", "Node 1");

        vm.Tick(); // nothing pending yet
        Assert.Null(vm.CurrentImage);

        vm.RefreshCommand.Execute(null);
        Assert.Equal("seq=7", vm.CurrentImage?.Summary);
        Assert.True(vm.HasImage);

        vm.Close();
    }

    [Fact]
    public void Labels_ResolveInEveryLanguage()
    {
        foreach (var culture in new[] { "en-US", "zh-Hans", "ko-KR" })
        {
            var loc = new LocalizationService { Culture = new(culture) };
            var vm = Create(loc: loc);
            foreach (var label in new[] { vm.PreviousLabel, vm.NextLabel, vm.LiveLabel, vm.RoiLabel, vm.CrosshairLabel, vm.RefreshLabel })
                Assert.False(string.IsNullOrWhiteSpace(label), $"blank label in {culture}");
        }
    }

    [Fact]
    public void Reopen_IsIdempotent_NoDoubleSubscription()
    {
        var ring = new PreviewRing();
        var vm = Create(preview: ring);
        vm.Open("n1", "N1");
        vm.Open("n2", "N2");

        // n2-only view; n1 publishes must not leak through the earlier subscription.
        ring.Publish(Frame("n1", 1));
        ring.Publish(Frame("n2", 2));
        vm.Tick();
        Assert.Single(vm.Frames);
        Assert.Equal("seq=2", vm.CurrentImage?.Summary);

        vm.Close();
    }

    [Fact]
    public void Dispose_ClosesSubscription()
    {
        var ring = new PreviewRing();
        var vm = Create(preview: ring);
        vm.Open("n1", "Node 1");
        vm.Dispose();
        ring.Publish(Frame("n1", 1));
        vm.Tick();
        Assert.Empty(vm.Frames);
    }
}