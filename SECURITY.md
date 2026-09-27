# 安全策略 · Security Policy

## 支持范围

| 版本 | 状态 |
|---|---|
| `main`（当前开发线） | 受支持 |
| 既往提交 | 不受支持（无发布标签，仅有 git 历史） |

> 本项目**尚未发布正式版本号**（无 `Version` 属性、无 release tag），因此不提供
> 历史版本的安全更新。版本化发布策略属交付协议 §7 事项 4。

## 许可与分发范围

本项目为**专有闭源软件**，© 2026 陈浪，保留所有权利，许可全文见 [`LICENSE`](LICENSE)。
未经著作权人事先书面许可，**不得**复制、分发、公开托管或制作衍生作品。因此漏洞报告
请勿附带可分发的代码片段或数据导出，必要时以脱敏描述替代。

## 报告漏洞

请**不要**通过公开 issue 报告安全问题。请通过私下渠道联系交付方，并提供：

- 受影响模块（项目/程序集名）与文件路径
- 复现步骤或最小复现代码
- 影响评估（数据损坏 / 越权 / 拒绝服务 / 信息泄露）
- 你已知的缓解措施

我们承诺在收到后 **3 个工作日**内给出初步回应（P2 口径，参见交付协议 §5）。
P0 级问题（可远程触发且导致数据损坏或代码执行）按 **4h** 响应。

## 已知安全相关的设计约束

以下是**有意的设计取舍**，非缺陷，请在报告前先确认：

1. **插件 ALC 装载**：插件在独立 `AssemblyLoadContext` 中加载，白名单前缀为
   `HalconWorkflow.`。加载第三方插件等同于执行其代码，请只加载可信来源的插件。
2. **原生互操作**：`MotionDrivers` / `Native` 通过 P/Invoke 调用 C++ 原生库
   （`gmotion`、`vx_voxel`）。原生内存边界的安全性取决于对应 C++ 实现。
3. **SQL 存储**：使用参数化查询（Dapper），不接受拼接 SQL。
4. **视觉后端**：`grab` 经 `VideoCapture` 访问相机设备；设备索引来自图/配方参数，
   未做设备白名单。现场部署应限制可访问的相机设备。
5. **配置与配方**：`appsettings.json`、`devices.json`、图与配方均以明文 JSON 存于
   `%LOCALAPPDATA%\HalconWorkflow`，**不含加密**。部署在多人可访问的机器上时，
   应依赖文件系统 ACL 保护。

## 供应链

- 运行时第三方依赖与许可见 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)，
  许可正文见 [`third-party/`](third-party/)。
- OpenCV 4.13 适用 **Apache-2.0**（4.5.0 起由 BSD 切换）。所附 `third-party/opencv/`
  下的 `LICENSE`/`COPYRIGHT`/许可变更说明已与 **OpenCV 4.14.0 真实源码树 SHA256 交叉
  复核一致**，故对 4.13 与 4.14 均准确。
- 随包的 `opencv_videoio_ffmpeg4130_64.dll` 封装 **LGPL-2.1-or-later（非 GPL）**。
  **已采用方案 B**：由 `Directory.Build.targets` 在 Build/Publish 后将该 DLL 排除，
  故不触发 LGPL 的源码/要约/可替换义务，也不引入 FFmpeg 库的供应链面。
  详见 [`third-party/ffmpeg/NOTICE.md`](third-party/ffmpeg/NOTICE.md)。
- 本项目采用专有许可**不改变**上述第三方组件的许可条款，亦不影响对其的合规使用义务。
