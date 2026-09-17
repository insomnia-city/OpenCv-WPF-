/*
 * vx_voxel.cpp — reference implementation of the vx_* C ABI (§6.3, ADR-006).
 *
 * Deliberately dependency-free C++17 so it builds into `vx_voxel.dll` with any toolchain
 * (MSVC / clang / gcc). The managed proxy in `src/Native` binds these exports; the algorithm
 * here only has to be *correct and cancellable* — SIMD/TBB tuning is a separate task.
 *
 * vx_voxel.cpp — vx_* C ABI 的参考实现（§6.3, ADR-006）。刻意使用无依赖 C++17，
 *   便于任意工具链（MSVC/clang/gcc）构建为 vx_voxel.dll。src/Native 的托管代理绑定这些导出；
 *   此处算法只需"正确且可取消"——SIMD/TBB 调优是后续独立任务。
 */
#include "vx_abi.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <limits>
#include <new>
#include <unordered_map>
#include <vector>

namespace {

constexpr size_t kCancelChunk = 8192;   /* cancel poll cadence · 取消轮询节拍 */
constexpr int kMaxRingRadius = 6;       /* k-NN ring expansion cap · k 近邻环扩展上限 */

struct VoxelKey
{
    std::int32_t x, y, z;
    bool operator==(const VoxelKey& o) const { return x == o.x && y == o.y && z == o.z; }
};

struct VoxelHash
{
    std::size_t operator()(const VoxelKey& k) const
    {
        std::uint64_t h = 1469598103934665603ull;
        auto mix = [&h](std::int32_t v) {
            h ^= static_cast<std::uint32_t>(v);
            h *= 1099511628211ull;
        };
        mix(k.x); mix(k.y); mix(k.z);
        return static_cast<std::size_t>(h);
    }
};

struct VoxelAccum
{
    double sx = 0, sy = 0, sz = 0;
    std::uint32_t count = 0;
};

struct CellKey
{
    std::int32_t x, y, z;
    bool operator==(const CellKey& o) const { return x == o.x && y == o.y && z == o.z; }
};

struct CellHash
{
    std::size_t operator()(const CellKey& k) const
    {
        std::uint64_t h = 1469598103934665603ull;
        auto mix = [&h](std::int32_t v) {
            h ^= static_cast<std::uint32_t>(v);
            h *= 1099511628211ull;
        };
        mix(k.x); mix(k.y); mix(k.z);
        return static_cast<std::size_t>(h);
    }
};

inline std::int32_t floor_index(double value, double leaf)
{
    const double idx = std::floor(value / leaf);
    if (!(idx > -2.0e9 && idx < 2.0e9)) return 0;
    return static_cast<std::int32_t>(idx);
}

inline std::int32_t cell_index(double value, double cell)
{
    const double idx = std::floor(value / cell);
    if (!(idx > -2.0e9 && idx < 2.0e9)) return 0;
    return static_cast<std::int32_t>(idx);
}

/* Cyclic Jacobi eigen-decomposition; returns the eigenvector of the smallest eigenvalue. */
/* 循环 Jacobi 特征分解；返回最小特征值对应的特征向量。 */
void smallest_eigenvector(double a[3][3], double out[3])
{
    double v[3][3] = {{1, 0, 0}, {0, 1, 0}, {0, 0, 1}};
    for (int sweep = 0; sweep < 16; ++sweep)
    {
        const double off = std::fabs(a[0][1]) + std::fabs(a[0][2]) + std::fabs(a[1][2]);
        if (off < 1e-12) break;
        for (int p = 0; p < 2; ++p)
        {
            for (int q = p + 1; q < 3; ++q)
            {
                if (std::fabs(a[p][q]) < 1e-15) continue;
                const double theta = 0.5 * std::atan2(2.0 * a[p][q], a[q][q] - a[p][p]);
                const double c = std::cos(theta);
                const double s = std::sin(theta);
                const double ap0 = a[0][p], ap1 = a[1][p], ap2 = a[2][p];
                const double aq0 = a[0][q], aq1 = a[1][q], aq2 = a[2][q];
                a[0][p] = c * ap0 - s * aq0;
                a[1][p] = c * ap1 - s * aq1;
                a[2][p] = c * ap2 - s * aq2;
                a[0][q] = s * ap0 + c * aq0;
                a[1][q] = s * ap1 + c * aq1;
                a[2][q] = s * ap2 + c * aq2;
                for (int r = 0; r < 3; ++r)
                {
                    const double pr = a[p][r], qr = a[q][r];
                    a[p][r] = c * pr - s * qr;
                    a[q][r] = s * pr + c * qr;
                }
                for (int r = 0; r < 3; ++r)
                {
                    const double vp = v[r][p], vq = v[r][q];
                    v[r][p] = c * vp - s * vq;
                    v[r][q] = s * vp + c * vq;
                }
            }
        }
    }

    int best = 0;
    if (a[1][1] < a[best][best]) best = 1;
    if (a[2][2] < a[best][best]) best = 2;
    double x = v[0][best], y = v[1][best], z = v[2][best];
    const double len = std::sqrt(x * x + y * y + z * z);
    if (len < 1e-12) { out[0] = 0; out[1] = 0; out[2] = 1; return; }
    out[0] = x / len; out[1] = y / len; out[2] = z / len;
}

} // namespace

extern "C" {

const char* vx_version(void)
{
    return "1.0.0";
}

const char* vx_last_error(int status)
{
    switch (status)
    {
        case VX_OK: return "ok";
        case VX_CANCELLED: return "cancelled";
        case VX_BUFFER_TOO_SMALL: return "output buffer too small";
        case VX_INVALID_ARGUMENT: return "invalid argument";
        default: return "native error";
    }
}

int vx_voxel_downsample(const float* xyz, size_t n, const VxVoxelParams* p,
                        float* out, size_t* outN, size_t cap, volatile int* cancel)
{
    if (!xyz || !p || !outN) return VX_INVALID_ARGUMENT;
    if (p->leafX <= 0.0 || p->leafY <= 0.0 || p->leafZ <= 0.0) return VX_INVALID_ARGUMENT;
    *outN = 0;
    if (n == 0) return VX_OK;
    if (!out) return VX_INVALID_ARGUMENT;

    try
    {
        std::unordered_map<VoxelKey, VoxelAccum, VoxelHash> cells;
        cells.reserve(std::min<size_t>(n, 1u << 20));

        for (size_t i = 0; i < n; ++i)
        {
            if ((i % kCancelChunk) == 0 && cancel && *cancel) return VX_CANCELLED;
            const float x = xyz[i * 3], y = xyz[i * 3 + 1], z = xyz[i * 3 + 2];
            const VoxelKey key{
                floor_index(x, p->leafX),
                floor_index(y, p->leafY),
                floor_index(z, p->leafZ)};
            VoxelAccum& acc = cells[key];
            acc.sx += x; acc.sy += y; acc.sz += z; ++acc.count;
        }

        if (cells.size() > cap)
        {
            *outN = cells.size();
            return VX_BUFFER_TOO_SMALL;
        }

        size_t written = 0;
        for (const auto& entry : cells)
        {
            const VoxelAccum& acc = entry.second;
            const double inv = 1.0 / static_cast<double>(acc.count);
            out[written * 3] = static_cast<float>(acc.sx * inv);
            out[written * 3 + 1] = static_cast<float>(acc.sy * inv);
            out[written * 3 + 2] = static_cast<float>(acc.sz * inv);
            ++written;
        }
        *outN = written;
        return VX_OK;
    }
    catch (const std::bad_alloc&)
    {
        return VX_NATIVE_ERROR;
    }
    catch (...)
    {
        return VX_NATIVE_ERROR;
    }
}

int vx_estimate_normals(const float* xyz, size_t n, int k,
                        float* normals, size_t cap, volatile int* cancel)
{
    if (!xyz) return VX_INVALID_ARGUMENT;
    if (n == 0) return VX_OK;
    if (!normals || cap < n) return VX_BUFFER_TOO_SMALL;

    try
    {
        if (n < 3)
        {
            for (size_t i = 0; i < n; ++i)
            {
                normals[i * 3] = 0; normals[i * 3 + 1] = 0; normals[i * 3 + 2] = 1;
            }
            return VX_OK;
        }

        double minX = std::numeric_limits<double>::max(), minY = minX, minZ = minX;
        double maxX = std::numeric_limits<double>::lowest(), maxY = maxX, maxZ = maxX;
        for (size_t i = 0; i < n; ++i)
        {
            const double x = xyz[i * 3], y = xyz[i * 3 + 1], z = xyz[i * 3 + 2];
            minX = std::min(minX, x); minY = std::min(minY, y); minZ = std::min(minZ, z);
            maxX = std::max(maxX, x); maxY = std::max(maxY, y); maxZ = std::max(maxZ, z);
        }
        const double extent = std::max(maxX - minX, std::max(maxY - minY, maxZ - minZ));
        const double cell = std::max(extent / std::cbrt(static_cast<double>(n)) * 1.5, 1e-9);

        std::unordered_map<CellKey, std::vector<int>, CellHash> buckets;
        buckets.reserve(n * 2);
        for (size_t i = 0; i < n; ++i)
        {
            const CellKey key{
                cell_index(xyz[i * 3], cell),
                cell_index(xyz[i * 3 + 1], cell),
                cell_index(xyz[i * 3 + 2], cell)};
            buckets[key].push_back(static_cast<int>(i));
        }

        const int wanted = std::max(1, std::min<int>(k, static_cast<int>(n) - 1));
        std::vector<std::pair<double, int>> scored;

        for (size_t i = 0; i < n; ++i)
        {
            if ((i % kCancelChunk) == 0 && cancel && *cancel) return VX_CANCELLED;
            const double px = xyz[i * 3], py = xyz[i * 3 + 1], pz = xyz[i * 3 + 2];
            const CellKey center{
                cell_index(px, cell), cell_index(py, cell), cell_index(pz, cell)};

            for (int radius = 1; radius <= kMaxRingRadius; ++radius)
            {
                scored.clear();
                for (int dx = -radius; dx <= radius; ++dx)
                for (int dy = -radius; dy <= radius; ++dy)
                for (int dz = -radius; dz <= radius; ++dz)
                {
                    if (radius > 1 &&
                        std::max(std::abs(dx), std::max(std::abs(dy), std::abs(dz))) < radius) continue;
                    const CellKey neighbor{center.x + dx, center.y + dy, center.z + dz};
                    const auto it = buckets.find(neighbor);
                    if (it == buckets.end()) continue;
                    for (const int idx : it->second)
                    {
                        if (idx == static_cast<int>(i)) continue;
                        const double ddx = xyz[idx * 3] - px;
                        const double ddy = xyz[idx * 3 + 1] - py;
                        const double ddz = xyz[idx * 3 + 2] - pz;
                        scored.emplace_back(ddx * ddx + ddy * ddy + ddz * ddz, idx);
                    }
                }
                if (static_cast<int>(scored.size()) >= wanted) break;
            }

            if (scored.empty())
            {
                normals[i * 3] = 0; normals[i * 3 + 1] = 0; normals[i * 3 + 2] = 1;
                continue;
            }
            const std::size_t take = std::min<std::size_t>(wanted, scored.size());
            if (scored.size() > take)
            {
                std::nth_element(scored.begin(), scored.begin() + take, scored.end(),
                                 [](const auto& a, const auto& b) { return a.first < b.first; });
            }

            double cx = 0, cy = 0, cz = 0;
            for (std::size_t s = 0; s < take; ++s)
            {
                const int idx = scored[s].second;
                cx += xyz[idx * 3]; cy += xyz[idx * 3 + 1]; cz += xyz[idx * 3 + 2];
            }
            const double inv = 1.0 / static_cast<double>(take);
            cx *= inv; cy *= inv; cz *= inv;

            double cov[3][3] = {{0, 0, 0}, {0, 0, 0}, {0, 0, 0}};
            for (std::size_t s = 0; s < take; ++s)
            {
                const int idx = scored[s].second;
                const double dx = xyz[idx * 3] - cx;
                const double dy = xyz[idx * 3 + 1] - cy;
                const double dz = xyz[idx * 3 + 2] - cz;
                cov[0][0] += dx * dx; cov[0][1] += dx * dy; cov[0][2] += dx * dz;
                cov[1][1] += dy * dy; cov[1][2] += dy * dz;
                cov[2][2] += dz * dz;
            }
            cov[1][0] = cov[0][1]; cov[2][0] = cov[0][2]; cov[2][1] = cov[1][2];

            double normal[3];
            smallest_eigenvector(cov, normal);
            normals[i * 3] = static_cast<float>(normal[0]);
            normals[i * 3 + 1] = static_cast<float>(normal[1]);
            normals[i * 3 + 2] = static_cast<float>(normal[2]);
        }
        return VX_OK;
    }
    catch (const std::bad_alloc&)
    {
        return VX_NATIVE_ERROR;
    }
    catch (...)
    {
        return VX_NATIVE_ERROR;
    }
}

} // extern "C"
