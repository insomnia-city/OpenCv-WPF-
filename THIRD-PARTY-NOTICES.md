# 第三方组件声明 · Third-Party Notices

本项目（HalconWorkflow）在构建与分发时会引入下列第三方组件。
分发本软件时，须一并附上本文件与各组件的完整许可文本。

---

## 运行时依赖（随产品分发）

| 组件 | 版本 | 许可 | 用途 |
|---|---|---|---|
| [OpenCvSharp4](https://github.com/shimat/opencvsharp) | 4.13.0.20260627 | Apache-2.0 | 视觉后端托管绑定 |
| [OpenCvSharp4.runtime.win](https://github.com/shimat/opencvsharp) | 4.13.0.20260627 | Apache-2.0 | OpenCV x64 原生库（win-x64） |
| [OpenCvSharp4.Windows](https://github.com/shimat/opencvsharp) | 4.13.0.20260627 | Apache-2.0 | **元包**，聚合下列三项，自身不含任何二进制 |
| [OpenCvSharp4.WpfExtensions](https://github.com/shimat/opencvsharp) | 4.13.0.20260627 | Apache-2.0 | WPF 图像窗的 `Mat` ↔ `BitmapSource` 互操作（仅托管 DLL） |
| [System.Drawing.Common](https://github.com/dotnet/runtime) | 10.0.9 | MIT | 上述 WpfExtensions 的传递依赖 |
| [Nodify](https://github.com/oleg-shilo/nodify) | 7.3.0 | MIT | WPF 节点编辑器控件 |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.4.2 | MIT | MVVM 基础（源生成器） |
| [Dapper](https://github.com/StackExchange/Dapper) | 2.1.35 | Apache-2.0 | 存储层轻量 ORM |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/SQLite) | 9.0.0 | MIT | SQLite 驱动 |

> 四项 OpenCvSharp 系列许可表达式均为 `Apache-2.0`，与 `third-party/opencvsharp/LICENSE` 同一份 Apache-2.0 全文，不另附正文。
> `System.Drawing.Common` 为 MIT，许可正文逐字取自 NuGet 包内 `LICENSE.TXT`（SHA256 A89886665765362EB77E0F8E26602C924520041D1711B2EEDC136434FE4D01AB），
> 存放于 `third-party/system.drawing.common/LICENSE.txt` 并随包分发，以满足 MIT「版权与许可声明须包含在软件所有副本中」的要求。

### OpenCvSharp4.Windows / WpfExtensions 的放置约束（构建期硬性要求）

`OpenCvSharp4.WpfExtensions` 仅提供 `net48` 与 `net8.0-windows7.0` 两套资产。
**只有 `net*-windows` 目标框架才能取到正确资产**；若在 `net9.0` 这类不带 `-windows`
的项目中引用，NuGet 会回退到 .NET Framework 资产并报：

```
NU1701: 已使用 .NETFramework ... 而不是项目目标框架 net9.0 还原包
        OpenCvSharp4.WpfExtensions 4.13.0.20260627。此包可能与项目不完全兼容
```

即在 .NET 9 应用中加载 net48 编译产物，运行时可能 `TypeLoadException`。故这两个包
**必须**声明在 `src/App`（`net9.0-windows`），**不得**放入 `net9.0` 的库项目。
`Directory.Build.targets` 的 FFmpeg 排除不受影响：`OpenCvSharp4.Windows` 是纯元包
（0 个二进制），`WpfExtensions` 亦无原生库，二者都不引入新的 FFmpeg 载体。

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

**因此不存在 GPL 传染。** 已选定并实施**方案 B：从发布包中排除该 DLL**，
故 LGPL 的源码提供、书面要约、允许替换等义务**均不触发**。实施方式为仓库根
`Directory.Build.targets`（`RemoveFfmpegRuntimeFromOutput` /
`RemoveFfmpegRuntimeFromPublish`），在 `Build` 与 `Publish` 之后删除
`runtimes\win-x64\native\opencv_videoio_ffmpeg*.dll`。

> 连 **build 输出也一并删除**（而非仅 publish），以免开发/测试环境带 FFmpeg 而发布产物
> 不带，导致「测试覆盖的原生能力」与「实际交付的」出现漂移。

**能力影响（已核实）**：相机实时取像不受影响（`grab` 走 `VideoCapture` +
`VideoCaptureAPIs.ANY`，由 DirectShow/MSMF 处理）；全仓不使用 `VideoWriter` /
`Cv2.ImShow` / highgui。仅失去依赖 FFmpeg 的视频文件解码，日后若需要可用 OpenCV 自带
MJPG 编码器替代。详见 [`third-party/ffmpeg/NOTICE.md`](third-party/ffmpeg/NOTICE.md)。

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

本仓库中由乙方原创的源码**采用专有闭源许可**，著作权归**陈浪**所有，
© 2026 陈浪，保留所有权利。许可全文见仓库根 [`LICENSE`](LICENSE)。

要点：授权范围以**双方另行签署的书面合同**为准；未经著作权人事先书面许可，不得
复制、修改、分发、公开托管、制作衍生作品或反编译本软件，亦不得移除其中的版权与
许可声明。

专有许可**不改变**上述第三方组件的许可条款。选择专有许可亦不影响本产品对这些
Apache-2.0 / LGPL 组件的合规使用义务。
