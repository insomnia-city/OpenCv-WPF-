using HalconWorkflow.Native;
using Xunit;

namespace HalconWorkflow.Native.Tests;

/// <summary>
/// Stage-10 software fallback: the managed kernel must produce the same contract as the native one
/// when the vx_* library is absent — "only slower, still correct" — including the cancellation
/// contract and input validation. · 阶段 10 软件回退：vx_* 库缺失时托管内核须给出与原生一致的契约
///   （"仅更慢但正确"），含取消契约与输入校验。
/// </summary>
public sealed class ManagedKernelTests
{
    private static readonly VoxelGridSpec Unit = new(1, 1, 1, 1024);

    [Fact]
    public async Task Voxel_EmptyCloud_ReturnsEmpty()
    {
        using var kernel = new ManagedPointCloudKernel();

        var result = await kernel.VoxelDownsampleAsync(ReadOnlyMemory<float>.Empty, Unit, CancellationToken.None);

        Assert.Equal(0, result.OutputCount);
        Assert.Equal(0, result.InputCount);
        Assert.Equal("managed", result.Backend);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public async Task Voxel_MergesSameCellIntoCentroid()
    {
        using var kernel = new ManagedPointCloudKernel();
        float[] cloud = [0.05f, 0.05f, 0.05f, 0.06f, 0.04f, 0.06f];

        var result = await kernel.VoxelDownsampleAsync(cloud, Unit, CancellationToken.None);

        Assert.Equal(2, result.InputCount);
        Assert.Equal(1, result.OutputCount);
        var span = result.Points.Span;
        Assert.Equal(0.055f, span[0], 4);
        Assert.Equal(0.045f, span[1], 4);
        Assert.Equal(0.055f, span[2], 4);
    }

    [Fact]
    public async Task Voxel_KeepsSeparateCells()
    {
        using var kernel = new ManagedPointCloudKernel();
        float[] cloud = [0.1f, 0.1f, 0.1f, 1.1f, 0.1f, 0.1f];

        var result = await kernel.VoxelDownsampleAsync(cloud, Unit, CancellationToken.None);

        Assert.Equal(2, result.OutputCount);
    }

    [Fact]
    public async Task Voxel_HonoursMaxPointCap()
    {
        using var kernel = new ManagedPointCloudKernel();
        float[] cloud = [0.1f, 0.1f, 0.1f, 1.1f, 0.1f, 0.1f, 2.1f, 0.1f, 0.1f];

        var result = await kernel.VoxelDownsampleAsync(cloud, new VoxelGridSpec(1, 1, 1, 1), CancellationToken.None);

        Assert.Equal(1, result.OutputCount);
    }

    [Fact]
    public async Task Voxel_LargeCloud_CompletesAllChunks()
    {
        using var kernel = new ManagedPointCloudKernel();
        const int points = 100_000;
        var cloud = new float[points * 3];
        for (var i = 0; i < points; i++)
        {
            cloud[i * 3] = i * 0.25f;
            cloud[i * 3 + 2] = 1f;
        }

        var result = await kernel.VoxelDownsampleAsync(cloud, Unit, CancellationToken.None);

        Assert.Equal(points, result.InputCount);
        Assert.Equal(Unit.MaxPoints, result.OutputCount); // cap reached, chunk loop completed · 达到上限，分块循环完整跑完
    }

    [Fact]
    public async Task Voxel_PreCancelled_Throws()
    {
        using var kernel = new ManagedPointCloudKernel();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => kernel.VoxelDownsampleAsync(new float[] { 0, 0, 0 }, Unit, cts.Token));
    }

    [Fact]
    public async Task Voxel_InvalidInput_Throws()
    {
        using var kernel = new ManagedPointCloudKernel();
        await Assert.ThrowsAsync<ArgumentException>(
            () => kernel.VoxelDownsampleAsync(new float[4], Unit, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => kernel.VoxelDownsampleAsync(new float[3], new VoxelGridSpec(0, 1, 1, 8), CancellationToken.None));
    }

    [Fact]
    public async Task Normals_PlanarCloud_AlignsWithZ()
    {
        using var kernel = new ManagedPointCloudKernel();
        var cloud = new List<float>();
        for (var x = 0; x < 5; x++)
        for (var y = 0; y < 5; y++)
        {
            cloud.Add(x * 0.1f);
            cloud.Add(y * 0.1f);
            cloud.Add(0f);
        }

        var result = await kernel.EstimateNormalsAsync(cloud.ToArray(), 8, CancellationToken.None);

        Assert.Equal(25, result.NormalCount);
        var normals = result.Normals.Span;
        for (var i = 0; i < 25; i++)
        {
            Assert.True(Math.Abs(normals[i * 3 + 2]) > 0.9f, $"normal {i} should be Z-aligned");
            var length = MathF.Sqrt(normals[i * 3] * normals[i * 3]
                + normals[i * 3 + 1] * normals[i * 3 + 1]
                + normals[i * 3 + 2] * normals[i * 3 + 2]);
            Assert.Equal(1f, length, 3);
        }
    }

    [Fact]
    public async Task Normals_DegenerateCloud_DefaultsToZ()
    {
        using var kernel = new ManagedPointCloudKernel();

        var result = await kernel.EstimateNormalsAsync(new float[] { 1f, 2f, 3f }, 4, CancellationToken.None);

        Assert.Equal(1, result.NormalCount);
        Assert.Equal(1f, result.Normals.Span[2]);
    }

    [Fact]
    public async Task Normals_EmptyCloud_ReturnsEmpty()
    {
        using var kernel = new ManagedPointCloudKernel();
        var result = await kernel.EstimateNormalsAsync(ReadOnlyMemory<float>.Empty, 4, CancellationToken.None);
        Assert.Equal(0, result.NormalCount);
    }

    [Fact]
    public async Task Normals_InvalidInput_Throws()
    {
        using var kernel = new ManagedPointCloudKernel();
        await Assert.ThrowsAsync<ArgumentException>(
            () => kernel.EstimateNormalsAsync(new float[4], 4, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => kernel.EstimateNormalsAsync(new float[3], 0, CancellationToken.None));
    }

    [Fact]
    public async Task Normals_PreCancelled_Throws()
    {
        using var kernel = new ManagedPointCloudKernel();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => kernel.EstimateNormalsAsync(new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, 2, cts.Token));
    }
}
