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

### OpenCV 本体与 FFmpeg（**需在打包时处理**）

[OpenCV](https://github.com/opencv/opencv) 自 4.x 起以 **Apache-2.0** 授权。
`OpenCvSharp4.runtime.win` 通过 `runtimes/win-x64/native/` 提供两个原生文件：

- `OpenCvSharpExtern.dll`
- `opencv_videoio_ffmpeg4130_64.dll`（videoio 模块，内含 FFmpeg）

**实测结论（务必据实对待）：**

- 该 NuGet 包目录内**不含任何 `LICENSE` / `NOTICE` / `COPYING` 文件**，其 nuspec 仅以
  license *expression* 声明 `Apache-2.0`，许可正文需从
  <https://github.com/shimat/opencvsharp> 获取。
- 因此 Apache-2.0 §4(a)(d) 要求的「随附许可副本」与「保留 NOTICE 内容」**不由 NuGet
  自动满足**，须在制作安装包/发布目录时（交付协议 §7 事项 2）显式附上：
  1. OpenCvSharp 的 Apache-2.0 全文；
  2. OpenCV 的 `LICENSE` 与 `NOTICE`（<https://github.com/opencv/opencv>）。
- ⚠ **未决项**：`opencv_videoio_ffmpeg4130_64.dll` 静态链接了 FFmpeg。FFmpeg 的许可取决于
  构建开关（**LGPL-2.1** 或 **GPL-2.0**）。上游 README 仅对 Linux/macOS 说明
  「FFmpeg (LGPL v2.1)」，**未声明 Windows 构建的实际许可**。
  正式分发前必须核实该 DLL 的实际许可（可查 OpenCV 官方 Windows 预编译包的构建配置或
  供应商确认）；若为 GPL-2.0，随产品再分发可能对整体分发方式产生额外约束。
  **在此项核实前，不得声称已完成许可合规。**

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

本仓库中由乙方原创的源码不依赖上述组件的源码再分发条款；其许可见仓库根 `LICENSE`。
