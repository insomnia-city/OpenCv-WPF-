# vx_voxel — Halcon 工作流原生计算内核（§6.3, ADR-006）

无依赖 C++17 参考实现，导出 `vx_*` C ABI（见 `vx_abi.h`）。托管代理
`src/Native/HalconWorkflow.Native.csproj` 在进程级装载本动态库（`NativeLibrary.TryLoad`，
**绝不随 ALC 卸载**）并绑定导出。库缺失时托管层自动回退到 `ManagedPointCloudKernel`，
结果"更慢但正确"，并写出显式提示。

## 导出

| 导出 | 说明 |
| --- | --- |
| `vx_version()` | 算法版本串，如 `1.0.0` |
| `vx_last_error(status)` | 状态码可读文本 |
| `vx_voxel_downsample(...)` | 体素栅格下采样（质心），按块轮询 `cancel` |
| `vx_estimate_normals(...)` | k 近邻表面法线（Jacobi 最小特征向量），按块轮询 `cancel` |

状态码与 `HalconWorkflow.Native.VxStatus` 一一对应：`0 Ok` / `1 Cancelled` /
`2 BufferTooSmall` / `3 InvalidArgument` / `4 NativeError`。

## 构建

需要 CMake ≥ 3.20 与任意 C++17 工具链（MSVC / clang / gcc）。

```powershell
cmake -S native/vx_voxel -B build/vx_voxel -DCMAKE_BUILD_TYPE=Release
cmake --build build/vx_voxel --config Release
```

产物：

- Windows：`build/vx_voxel/Release/vx_voxel.dll`
- Linux：`build/vx_voxel/libvx_voxel.so`
- macOS：`build/vx_voxel/libvx_voxel.dylib`

## 部署

把产物拷到宿主可被 `NativeLibrary` 解析的目录（与宿主 exe 同目录，或 `runtimes/`
布局下的原生资产目录），Windows 另需 VC++ 运行时：

```powershell
Copy-Item build/vx_voxel/Release/vx_voxel.dll -Destination src/App/bin/Debug/net9.0/
```

未提供 DLL 时，`dotnet test` 仍全绿（走托管回退 + 装载探测断言）。

## 约定

- 边界不出现 C++ 类型、不跨边界抛异常；错误只用状态码 + `vx_last_error`。
- 低拷贝：缓冲由调用方持有并 pin 后直传（托管侧 `VxNativeFunctionTable` 内 `fixed`）。
- 协作式取消：重型循环每 8192 个点轮询一次 `volatile int* cancel`，命中即返回
  `VX_CANCELLED`，不破坏原生状态。
