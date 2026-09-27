# 第三方组件声明 · Third-Party Notices

本项目（HalconWorkflow）在构建与分发时会引入下列第三方组件。
分发本软件时，须一并附上本文件与各组件的完整许可文本。

---

## 运行时依赖（随产品分发）

| 组件 | 版本 | 许可 | 用途 |
|---|---|---|---|
| [OpenCvSharp4](https://github.com/shimat/opencvsharp) | 4.13.0.20260627 | Apache-2.0 | 视觉后端托管绑定 |
| [OpenCvSharp4.runtime.win](https://github.com/shimat/opencvsharp) | 4.13.0.20260627 | Apache-2.0 | OpenCV x64 原生库（win-x64） |
| [Nodify](https://github.com/oleg-shilo/nodify) | 7.3.0 | MIT | WPF 节点编辑器控件 |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.4.2 | MIT | MVVM 基础（源生成器） |
| [Dapper](https://github.com/StackExchange/Dapper) | 2.1.35 | Apache-2.0 | 存储层轻量 ORM |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/SQLite) | 9.0.0 | MIT | SQLite 驱动 |

### OpenCV 本体 — Apache-2.0（**非 BSD**）

**版本分界线为 4.5.0**：

| OpenCV 版本 | 许可 |
|---|---|
| 4.5.0 及更高（含 4.5-pre，2020-08 起） | **Apache License 2.0** |
| 4.4.0 及更早（含 3.x、2.x、1.x） | 3-clause BSD |

本产品分发的是 **OpenCV 4.13**，故适用 **Apache-2.0**。切换动因（BSD 无专利条款）
见 <https://opencv.org/opencv-is-to-change-the-license-to-apache-2/>。
若日后降级至 4.4.0 或更早，必须改用 BSD-3-Clause 并重新核验。

已随本仓库附带（逐字复制自 `opencv/opencv` `4.x` 分支）：
[`third-party/opencv/LICENSE`](third-party/opencv/LICENSE)（Apache-2.0 全文）、
[`COPYRIGHT`](third-party/opencv/COPYRIGHT)（各版权方声明）、
[`LICENSE_CHANGE_NOTICE.txt`](third-party/opencv/LICENSE_CHANGE_NOTICE.txt)（含切换前 BSD 全文）、
[`NOTICE.md`](third-party/opencv/NOTICE.md)（本项目撰写的归属说明）。

> OpenCV 仓库**不提供** `NOTICE` 文件（`4.x` 分支该路径 404），故 Apache-2.0 §4(d) 对其不适用；
> §4(a) 的「随附许可副本」由上述 `LICENSE` 满足。

### FFmpeg — LGPL-2.1-or-later（**非 GPL**）

`opencv_videoio_ffmpeg4130_64.dll` 封装了 FFmpeg。OpenCV 官方
`3rdparty/ffmpeg/readme.txt` 明确其 Windows 预编译版本
「built with proper flags (**without GPL components**)」且为「**LGPL library, not BSD libraries**」。

**因此不存在 GPL 传染。** 分发时须在以下两者中择一（详见
[`third-party/ffmpeg/NOTICE.md`](third-party/ffmpeg/NOTICE.md)）：

- **方案 A（随包分发）**：须附 FFmpeg `COPYING.LGPLv2.1` 全文 + 对应版本源码或书面要约
  + 允许用户替换该 DLL。⚠ 本仓库**尚未**包含这些内容，属 §7 事项 2 必须补齐项。
- **方案 B（排除该 DLL）**：不分发即不触发 LGPL 义务；相机取像仍走 DirectShow/MSMF，
  仅失去 FFmpeg 视频文件解码能力。**建议采用此方案。**

### 关于 NuGet 包不携带许可文件

实测 `OpenCvSharp4.runtime.win` 包目录内**不含任何 `LICENSE`/`NOTICE`/`COPYING` 文件**，
其 `.nuspec` 仅以 license *expression* 声明 `Apache-2.0`。故 Apache-2.0 §4(a)/(c)
**不由 NuGet 自动满足**，须在制作发布包时（§7 事项 2）显式附上
[`third-party/opencvsharp/LICENSE`](third-party/opencvsharp/LICENSE)
与 [`NOTICE.md`](third-party/opencvsharp/NOTICE.md)。

---

## 仅开发/测试期依赖（不随产品分发）

| 组件 | 版本 | 许可 | 用途 |
|---|---|---|---|
| [xunit](https://github.com/xunit/xunit) | 2.9.2 / 2.9.3 | Apache-2.0 | 单元测试框架 |
| [xunit.runner.visualstudio](https://github.com/xunit/xunit) | 2.8.2 / 3.0.2 | Apache-2.0 | 测试运行器 |
| [Microsoft.NET.Test.Sdk](https://github.com/dotnet/sdk) | 17.12.0 / 17.14.1 | MIT | VSTest 宿主 |
| [coverlet.collector](https://github.com/coverlet-coverage/coverlet) | 6.0.2 | MIT | 覆盖率收集 |
| [System.Text.Encoding.CodePages](https://github.com/dotnet/runtime) | 9.0.0 | MIT | `Storage/CsvExporter.cs` 注册代码页编码 provider（GBK 等非 UTF-8 导出） |

> 上表版本与许可取自本机 NuGet 缓存中各包 `.nuspec` 的实际元数据（`license` 字段），
> 非凭记忆填写；如升级依赖须重新核对。

---

## 本项目原创代码

本仓库中由乙方原创的源码不依赖上述组件的源码再分发条款；其许可见仓库根 `LICENSE`
（**该文件当前为占位状态**：版权所有方尚未选定许可，现按「保留所有权利」处理，
在此之前不得对外分发本仓库）。
