namespace HalconWorkflow.Native;

/// <summary>
/// Pure managed point-cloud kernel used when the native vx_* library is absent (§6.3 software
/// fallback). It is deliberately simple and deterministic — "only slower, still correct" — with
/// the same cooperative-cancellation contract and the same borrow/return pool semantics as the
/// native kernel. · 原生 vx_* 库缺失时使用的纯托管点云内核(§6.3 软件回退)。刻意简单且确定——
///   "仅更慢、但正确"——具备与原生内核相同的协作式取消契约和借出/归还池语义。
/// </summary>
public sealed class ManagedPointCloudKernel : IPointCloudKernel
{
    private const int PointChunk = 1 << 15;
    private const int NormalChunk = 1 << 12;

    /// <summary>Backend identifier for the managed fallback. · 托管回退的后端标识</summary>
    public const string BackendId = "managed";

    private int _disposed;

    /// <inheritdoc />
    public string Backend => BackendId;

    /// <inheritdoc />
    public bool IsNative => false;

    /// <inheritdoc />
    public Task<VoxelDownsampleResult> VoxelDownsampleAsync(ReadOnlyMemory<float> xyz, VoxelGridSpec spec, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!spec.IsValid) throw new ArgumentOutOfRangeException(nameof(spec), "voxel spec needs positive leaves and cap");
        RequireTriplets(xyz.Length);
        ct.ThrowIfCancellationRequested();

        var span = xyz.Span;
        var count = span.Length / 3;
        if (count == 0)
            return Task.FromResult(new VoxelDownsampleResult(0, ReadOnlyMemory<float>.Empty, Backend, false, null));

        var cells = new Dictionary<(long X, long Y, long Z), (double X, double Y, double Z, int N)>();
        var order = new List<(long X, long Y, long Z)>();
        for (var i = 0; i < count; i++)
        {
            if ((i & (PointChunk - 1)) == 0) ct.ThrowIfCancellationRequested();
            double x = span[i * 3], y = span[i * 3 + 1], z = span[i * 3 + 2];
            var key = (Floor(x, spec.LeafX), Floor(y, spec.LeafY), Floor(z, spec.LeafZ));
            if (cells.TryGetValue(key, out var acc))
                cells[key] = (acc.X + x, acc.Y + y, acc.Z + z, acc.N + 1);
            else if (order.Count < spec.MaxPoints)
            {
                cells[key] = (x, y, z, 1);
                order.Add(key);
            }
        }

        var points = new float[order.Count * 3];
        for (var j = 0; j < order.Count; j++)
        {
            var acc = cells[order[j]];
            points[j * 3] = (float)(acc.X / acc.N);
            points[j * 3 + 1] = (float)(acc.Y / acc.N);
            points[j * 3 + 2] = (float)(acc.Z / acc.N);
        }
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new VoxelDownsampleResult(count, points, Backend, false, null));
    }

    /// <inheritdoc />
    public Task<NormalEstimateResult> EstimateNormalsAsync(ReadOnlyMemory<float> xyz, int neighbors, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (neighbors < 1) throw new ArgumentOutOfRangeException(nameof(neighbors), "neighbors must be >= 1");
        RequireTriplets(xyz.Length);
        ct.ThrowIfCancellationRequested();

        var span = xyz.Span;
        var count = span.Length / 3;
        if (count == 0)
            return Task.FromResult(new NormalEstimateResult(0, ReadOnlyMemory<float>.Empty, Backend, false, null));

        var normals = new float[count * 3];
        if (count < 3)
        {
            for (var i = 0; i < count; i++) normals[i * 3 + 2] = 1f; // degenerate cloud: default +Z · 退化点云：默认 +Z
            return Task.FromResult(new NormalEstimateResult(count, normals, Backend, false, null));
        }

        var k = Math.Min(neighbors, count - 1);
        var grid = SpatialGrid.Build(span, count);
        var candidates = new List<int>();

        for (var i = 0; i < count; i++)
        {
            if ((i & (NormalChunk - 1)) == 0) ct.ThrowIfCancellationRequested();
            grid.Nearest(span, count, i, k, candidates);
            WriteNormal(span, i, candidates, normals);
        }

        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new NormalEstimateResult(count, normals, Backend, false, null));
    }

    private static long Floor(double value, double leaf)
    {
        var index = Math.Floor(value / leaf);
        if (double.IsNaN(index) || index > long.MaxValue / 2 || index < long.MinValue / 2) return 0;
        return (long)index;
    }

    private static void WriteNormal(ReadOnlySpan<float> xyz, int index, List<int> neighbors, float[] normals)
    {
        // Local tangent plane: centroid → 3x3 covariance → eigenvector of the smallest eigenvalue.
        // 局部切平面：质心 → 3x3 协方差 → 最小特征值对应特征向量。
        double cx = 0, cy = 0, cz = 0;
        foreach (var n in neighbors)
        {
            cx += xyz[n * 3];
            cy += xyz[n * 3 + 1];
            cz += xyz[n * 3 + 2];
        }
        var inv = neighbors.Count == 0 ? 0 : 1.0 / neighbors.Count;
        cx *= inv; cy *= inv; cz *= inv;

        double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        foreach (var n in neighbors)
        {
            double dx = xyz[n * 3] - cx, dy = xyz[n * 3 + 1] - cy, dz = xyz[n * 3 + 2] - cz;
            xx += dx * dx; xy += dx * dy; xz += dx * dz;
            yy += dy * dy; yz += dy * dz; zz += dz * dz;
        }

        var covariance = new[,] { { xx, xy, xz }, { xy, yy, yz }, { xz, yz, zz } };
        var normal = SmallestEigenvector(covariance);
        normals[index * 3] = (float)normal.X;
        normals[index * 3 + 1] = (float)normal.Y;
        normals[index * 3 + 2] = (float)normal.Z;
    }

    /// <summary>
    /// Smallest-eigenvalue eigenvector of a symmetric 3x3 matrix via cyclic Jacobi rotations.
    /// / 对称 3x3 矩阵的最小特征值特征向量(循环 Jacobi 旋转)。
    /// </summary>
    private static (double X, double Y, double Z) SmallestEigenvector(double[,] m)
    {
        var a = (double[,])m.Clone();
        var v = new[,] { { 1.0, 0, 0 }, { 0, 1.0, 0 }, { 0, 0, 1.0 } };
        for (var sweep = 0; sweep < 16; sweep++)
        {
            var off = Math.Abs(a[0, 1]) + Math.Abs(a[0, 2]) + Math.Abs(a[1, 2]);
            if (off < 1e-12) break;
            for (var p = 0; p < 2; p++)
            {
                for (var q = p + 1; q < 3; q++)
                {
                    if (Math.Abs(a[p, q]) < 1e-15) continue;
                    var theta = 0.5 * Math.Atan2(2 * a[p, q], a[q, q] - a[p, p]);
                    var c = Math.Cos(theta);
                    var s = Math.Sin(theta);
                    var ap = new double[3];
                    var aq = new double[3];
                    for (var r = 0; r < 3; r++)
                    {
                        ap[r] = a[r, p];
                        aq[r] = a[r, q];
                    }
                    for (var r = 0; r < 3; r++)
                    {
                        a[r, p] = c * ap[r] - s * aq[r];
                        a[r, q] = s * ap[r] + c * aq[r];
                    }
                    for (var r = 0; r < 3; r++)
                    {
                        var pr = a[p, r];
                        var qr = a[q, r];
                        a[p, r] = c * pr - s * qr;
                        a[q, r] = s * pr + c * qr;
                    }
                    for (var r = 0; r < 3; r++)
                    {
                        var vp = v[r, p];
                        var vq = v[r, q];
                        v[r, p] = c * vp - s * vq;
                        v[r, q] = s * vp + c * vq;
                    }
                }
            }
        }

        var best = 0;
        if (a[1, 1] < a[best, best]) best = 1;
        if (a[2, 2] < a[best, best]) best = 2;
        var x = v[0, best];
        var y = v[1, best];
        var z = v[2, best];
        var len = Math.Sqrt(x * x + y * y + z * z);
        if (len < 1e-12) return (0, 0, 1);
        return (x / len, y / len, z / len);
    }

    private static void RequireTriplets(int length)
    {
        if (length % 3 != 0)
            throw new ArgumentException("point cloud must be flat x,y,z triplets (length % 3 == 0)", "xyz");
    }

    /// <summary>Releases managed state. · 释放托管状态</summary>
    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

    /// <summary>
    /// Uniform spatial hash for k-nearest-neighbour queries over a small neighbourhood. Cell size
    /// tracks the average point spacing so the first ring almost always suffices.
    /// / 用于小邻域 k 近邻查询的均匀空间哈希。单元尺寸跟随平均点距，通常第一圈即足够。
    /// </summary>
    private sealed class SpatialGrid
    {
        private readonly double _cell;
        private readonly Dictionary<(long X, long Y, long Z), List<int>> _buckets = new();

        private SpatialGrid(double cell) => _cell = cell;

        public static SpatialGrid Build(ReadOnlySpan<float> xyz, int count)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            for (var i = 0; i < count; i++)
            {
                double x = xyz[i * 3], y = xyz[i * 3 + 1], z = xyz[i * 3 + 2];
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (z < minZ) minZ = z;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
                if (z > maxZ) maxZ = z;
            }
            var extent = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
            var cell = Math.Max(extent / Math.Cbrt(count) * 1.5, 1e-9);
            var grid = new SpatialGrid(cell);
            for (var i = 0; i < count; i++)
            {
                var key = grid.Key(xyz[i * 3], xyz[i * 3 + 1], xyz[i * 3 + 2]);
                if (!grid._buckets.TryGetValue(key, out var list)) grid._buckets[key] = list = new List<int>(4);
                list.Add(i);
            }
            return grid;
        }

        private (long X, long Y, long Z) Key(double x, double y, double z)
            => ((long)Math.Floor(x / _cell), (long)Math.Floor(y / _cell), (long)Math.Floor(z / _cell));

        /// <summary>Collects up to <paramref name="k"/> nearest neighbour indices (excluding self) into <paramref name="sink"/>.</summary>
        public void Nearest(ReadOnlySpan<float> xyz, int count, int index, int k, List<int> sink)
        {
            sink.Clear();
            double px = xyz[index * 3], py = xyz[index * 3 + 1], pz = xyz[index * 3 + 2];
            var (cx, cy, cz) = Key(px, py, pz);
            var scored = new List<(double Distance, int Index)>();
            for (var radius = 1; radius <= 6; radius++)
            {
                scored.Clear();
                for (var dx = -radius; dx <= radius; dx++)
                for (var dy = -radius; dy <= radius; dy++)
                for (var dz = -radius; dz <= radius; dz++)
                {
                    if (radius > 1 && Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz))) < radius) continue;
                    if (!_buckets.TryGetValue((cx + dx, cy + dy, cz + dz), out var bucket)) continue;
                    foreach (var n in bucket)
                    {
                        if (n == index) continue;
                        double ddx = xyz[n * 3] - px, ddy = xyz[n * 3 + 1] - py, ddz = xyz[n * 3 + 2] - pz;
                        scored.Add((ddx * ddx + ddy * ddy + ddz * ddz, n));
                    }
                }
                if (scored.Count >= k) break;
            }
            scored.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
            var take = Math.Min(k, scored.Count);
            for (var i = 0; i < take; i++) sink.Add(scored[i].Index);
            if (sink.Count == 0) sink.Add(index); // isolated point: degenerate but defined · 孤立点：退化但有定义
        }
    }
}
