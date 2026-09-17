using HalconWorkflow.Native;
using Xunit;

namespace HalconWorkflow.Native.Tests;

/// <summary>
/// Stage-10 gate: the native kernel must load process-wide at most once (never via an ALC), report
/// a missing library explicitly instead of throwing, and map the vx_* status codes — including the
/// cancel hot path — onto the managed contract. · 阶段 10 闸门：原生内核必须进程级最多装载一次
///   （绝不经由 ALC）、缺失库时显式报告而非抛出，并把 vx_* 状态码（含取消热路径）映射到托管契约。
/// </summary>
public sealed class NativeKernelTests
{
    private static readonly float[] Cloud = [0f, 0f, 0f, 1f, 1f, 1f];
    private static readonly VoxelGridSpec Spec = new(1, 1, 1, 16);

    [Fact]
    public void Load_MissingLibrary_ReportsExplicitFallback()
    {
        const string absent = "vx_voxel_absent_probe_9f1c";
        try
        {
            var load = NativeKernelLibrary.Load(absent);

            Assert.True(load.IsMissing);
            Assert.Equal(0, load.Handle);
            Assert.Equal(NativeKernelLoadScope.Process, load.Scope);
            Assert.Contains("fallback", load.Message);
            Assert.True(NativeKernelLibrary.IsResolved(absent));

            // Repeat resolution returns the cached record: loaded at most once per process.
            Assert.Equal(load, NativeKernelLibrary.Load(absent));
        }
        finally
        {
            NativeKernelLibrary.Unload(absent);
        }
    }

    [Fact]
    public void Load_KnownSystemLibrary_IsProcessScopedAndIdempotent()
    {
        var name = OperatingSystem.IsWindows() ? "kernel32" : "libc";
        try
        {
            var first = NativeKernelLibrary.Load(name);
            if (!first.IsLoaded)
            {
                Assert.True(first.IsMissing);
                return; // resolver without the system library: still explicit · 无系统库时仍显式
            }

            Assert.Equal(NativeKernelLoadScope.Process, first.Scope);
            Assert.NotEqual(0, first.Handle);
            Assert.Equal(first, NativeKernelLibrary.Load(name));
        }
        finally
        {
            NativeKernelLibrary.Unload(name);
        }
    }

    [Fact]
    public void TryBind_ZeroHandle_ReturnsNull()
    {
        Assert.Null(VxNativeFunctionTable.TryBind(0));
    }

    [Fact]
    public void Probe_ReportsBackendExplicitly()
    {
        var probe = PointCloudKernelFactory.Probe();

        Assert.Equal(NativeKernelLibrary.DefaultLibraryName, probe.Library);
        Assert.False(string.IsNullOrWhiteSpace(probe.Message));
        if (probe.IsNative)
            Assert.StartsWith("native:", probe.Backend);
        else
            Assert.Equal(ManagedPointCloudKernel.BackendId, probe.Backend);
    }

    [Fact]
    public void Create_MatchesProbe_AndFallsBackToManaged()
    {
        var probe = PointCloudKernelFactory.Probe();
        using var kernel = PointCloudKernelFactory.Create();

        Assert.Equal(probe.IsNative, kernel.IsNative);
        Assert.Equal(probe.Backend, kernel.Backend);
        if (!probe.IsNative) Assert.False(kernel.IsNative);
    }

    [Fact]
    public void CreateManaged_IsNeverNative()
    {
        using var kernel = PointCloudKernelFactory.CreateManaged();
        Assert.False(kernel.IsNative);
        Assert.Equal(ManagedPointCloudKernel.BackendId, kernel.Backend);
    }

    [Fact]
    public async Task NativeKernel_MapsResultsOverTheAbi()
    {
        var table = new FakeTable
        {
            OnVoxel = (_, _, _, output) =>
            {
                output[0] = 1f; output[1] = 2f; output[2] = 3f;
                output[3] = 4f; output[4] = 5f; output[5] = 6f;
                return (VxStatus.Ok, 2);
            },
            OnNormals = (_, _, _, normals) =>
            {
                for (var i = 0; i < normals.Length; i += 3) normals[i + 2] = 1f;
                return VxStatus.Ok;
            }
        };
        using var kernel = new NativeVoxelKernel(table);

        var voxel = await kernel.VoxelDownsampleAsync(Cloud, Spec, CancellationToken.None);
        var normals = await kernel.EstimateNormalsAsync(Cloud, 3, CancellationToken.None);

        Assert.True(kernel.IsNative);
        Assert.Equal("native:test", kernel.Backend);
        Assert.Equal(2, voxel.InputCount);
        Assert.Equal(2, voxel.Points.Length / 3);
        Assert.Equal(6f, voxel.Points.Span[5]);
        Assert.False(voxel.Cancelled);
        Assert.Equal(2, normals.NormalCount);
        Assert.Equal(1f, normals.Normals.Span[2]);
        Assert.Equal(1, table.VoxelCalls);
        Assert.Equal(1, table.NormalCalls);
    }

    [Fact]
    public async Task NativeKernel_BufferTooSmall_GrowsOnceAndRetries()
    {
        var calls = 0;
        var table = new FakeTable
        {
            OnVoxel = (_, _, _, output) =>
            {
                calls++;
                if (calls == 1) return (VxStatus.BufferTooSmall, 5);
                for (var i = 0; i < 15; i++) output[i] = i;
                return (VxStatus.Ok, 5);
            }
        };
        using var kernel = new NativeVoxelKernel(table);

        var result = await kernel.VoxelDownsampleAsync(Cloud, Spec, CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal(5, result.Points.Length / 3);
        Assert.Equal(14f, result.Points.Span[14]);
    }

    [Fact]
    public async Task NativeKernel_CancelledStatus_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        var table = new FakeTable
        {
            OnVoxel = (_, _, _, _) =>
            {
                cts.Cancel(); // native observes the flipped flag and returns §6.3 · 原生观察到标志翻转并返回
                return (VxStatus.Cancelled, 0);
            }
        };
        using var kernel = new NativeVoxelKernel(table);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => kernel.VoxelDownsampleAsync(Cloud, Spec, cts.Token));
    }

    [Fact]
    public async Task NativeKernel_PreCancelled_DoesNotTouchTheApi()
    {
        var called = false;
        var table = new FakeTable
        {
            OnVoxel = (_, _, _, _) => { called = true; return (VxStatus.Ok, 0); },
            OnNormals = (_, _, _, _) => { called = true; return VxStatus.Ok; }
        };
        using var kernel = new NativeVoxelKernel(table);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => kernel.VoxelDownsampleAsync(Cloud, Spec, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => kernel.EstimateNormalsAsync(Cloud, 3, cts.Token));
        Assert.False(called);
    }

    [Fact]
    public async Task NativeKernel_ErrorStatus_ThrowsNativeKernelException()
    {
        var table = new FakeTable
        {
            OnVoxel = (_, _, _, _) => (VxStatus.InvalidArgument, 0),
            LastErrorText = "leaf size must be positive"
        };
        using var kernel = new NativeVoxelKernel(table);

        var ex = await Assert.ThrowsAsync<NativeKernelException>(
            () => kernel.VoxelDownsampleAsync(Cloud, Spec, CancellationToken.None));
        Assert.Equal(VxStatus.InvalidArgument, ex.Status);
        Assert.Contains("leaf size", ex.Message);
    }

    [Fact]
    public async Task NativeKernel_RejectsNonTripletCloud()
    {
        using var kernel = new NativeVoxelKernel(new FakeTable());
        await Assert.ThrowsAsync<ArgumentException>(
            () => kernel.VoxelDownsampleAsync(new float[4], Spec, CancellationToken.None));
    }

    /// <summary>In-memory <see cref="IVxFunctionTable"/> standing in for the native ABI. · 替代原生 ABI 的内存实现</summary>
    private sealed class FakeTable : IVxFunctionTable
    {
        public string Version { get; init; } = "test";

        public Func<ReadOnlySpan<float>, int, VoxelGridSpec, Span<float>, (VxStatus Status, int OutCount)>? OnVoxel { get; init; }

        public Func<ReadOnlySpan<float>, int, int, Span<float>, VxStatus>? OnNormals { get; init; }

        public string LastErrorText { get; init; } = "fake error";

        public int VoxelCalls { get; private set; }

        public int NormalCalls { get; private set; }

        public VxStatus VoxelDownsample(ReadOnlySpan<float> xyz, int pointCount, VoxelGridSpec spec,
            Span<float> output, out int outCount, ref int cancel)
        {
            VoxelCalls++;
            if (OnVoxel is null) { outCount = 0; return VxStatus.Ok; }
            var (status, written) = OnVoxel(xyz, pointCount, spec, output);
            outCount = written;
            return status;
        }

        public VxStatus EstimateNormals(ReadOnlySpan<float> xyz, int pointCount, int neighbors,
            Span<float> normals, ref int cancel)
        {
            NormalCalls++;
            return OnNormals?.Invoke(xyz, pointCount, neighbors, normals) ?? VxStatus.Ok;
        }

        public string LastError(int handle) => LastErrorText;

        public void Dispose()
        {
        }
    }
}
