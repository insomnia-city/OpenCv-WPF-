/*
 * vx_abi.h — stable C ABI for the Halcon 工作流 native compute kernel (§6.3, ADR-006).
 *
 * Rules (locked by ADR-006 / §6.3):
 *   - extern "C", cdecl, no C++ types across the boundary, no exceptions propagated.
 *   - Errors are returned as status codes; read text via vx_last_error.
 *   - Heavy calls poll `volatile int* cancel` per chunk and return VX_CANCELLED promptly.
 *   - The DLL is loaded once per process by the managed proxy (never unloaded with an ALC).
 *
 * vx_abi.h — Halcon 工作流原生计算内核的稳定 C ABI（§6.3, ADR-006）。
 *   规则：extern "C"/cdecl、边界不出现 C++ 类型、不跨边界抛异常；错误以状态码返回、
 *   经 vx_last_error 读文本；重型调用按块轮询 cancel 并及时返回 VX_CANCELLED；
 *   DLL 由托管代理进程级装载一次（绝不随 ALC 卸载）。
 */
#ifndef VX_ABI_H
#define VX_ABI_H

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Export decoration for a shared library. · 动态库导出修饰。 */
#if defined(_WIN32)
#define VX_API __declspec(dllexport)
#else
#define VX_API __attribute__((visibility("default")))
#endif

/* Voxel grid parameters: axis-aligned leaf size (world units). · 体素栅格参数：轴对齐叶尺寸。 */
typedef struct VxVoxelParams {
    double leafX;
    double leafY;
    double leafZ;
} VxVoxelParams;

/* Status codes mirrored by HalconWorkflow.Native.VxStatus. · 与 VxStatus 对应的状态码。 */
enum VxStatus {
    VX_OK = 0,
    VX_CANCELLED = 1,
    VX_BUFFER_TOO_SMALL = 2,
    VX_INVALID_ARGUMENT = 3,
    VX_NATIVE_ERROR = 4
};

/* Algorithm version string, e.g. "1.0.0". Never null. · 算法版本串。永不为 null。 */
VX_API const char* vx_version(void);

/* Human-readable text for a status code. Never null. · 状态码的可读文本。永不为 null。 */
VX_API const char* vx_last_error(int status);

/*
 * Voxel grid downsample.
 *   xyz     flat x,y,z triplets, n points (3*n floats), caller-pinned.
 *   out     caller-owned buffer; capacity `cap` points (3*cap floats).
 *   outN    [out] points written, or points required on VX_BUFFER_TOO_SMALL.
 *   cancel  cooperative cancel flag polled per chunk.
 * Returns VX_OK / VX_CANCELLED / VX_BUFFER_TOO_SMALL / VX_INVALID_ARGUMENT.
 *
 * 体素栅格下采样。xyz 为 3*n 平铺；out 容量 cap 点；outN 返回写入数（缓冲不足时为所需数）；
 *   cancel 按块轮询。返回上述状态码之一。
 */
VX_API int vx_voxel_downsample(const float* xyz, size_t n, const VxVoxelParams* p,
                               float* out, size_t* outN, size_t cap, volatile int* cancel);

/*
 * k-nearest surface normals: one unit vector per input point, flat x,y,z.
 *   k        neighbours used for the local tangent plane (clamped to [1, n-1]).
 *   normals  caller-owned buffer of 3*n floats.
 *   cancel   cooperative cancel flag polled per chunk.
 * Returns VX_OK / VX_CANCELLED / VX_INVALID_ARGUMENT.
 *
 * k 近邻表面法线：每点一个单位向量，x,y,z 平铺。k 为局部切平面邻居数；normals 为 3*n 缓冲。
 */
VX_API int vx_estimate_normals(const float* xyz, size_t n, int k,
                               float* normals, size_t cap, volatile int* cancel);

#ifdef __cplusplus
}
#endif

#endif /* VX_ABI_H */
