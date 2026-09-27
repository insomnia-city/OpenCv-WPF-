# FFmpeg（随 OpenCV Windows 运行时分发）— 归属声明

## 结论：LGPL-2.1-or-later，**不是 GPL**

`OpenCvSharp4.runtime.win` 4.13.0.20260627 在
`runtimes/win-x64/native/` 下分发 `opencv_videoio_ffmpeg4130_64.dll`，
该 DLL 封装了 FFmpeg（OpenCV 的 `videoio` 模块在运行时加载它以解码/编码视频）。

OpenCV 官方 `3rdparty/ffmpeg/readme.txt`（`4.5.0` 与 `master` 表述一致）明确：

> On Windows OpenCV uses pre-built ffmpeg binaries, built with proper flags
> (**without GPL components**) and wrapped with simple, stable OpenCV-compatible API.
> …
> The pre-built `opencv_videoio_ffmpeg*.dll` is:
> * **LGPL library, not BSD libraries.**
> * Loaded at runtime by `opencv_videoio` module.

- 来源：<https://github.com/opencv/opencv/blob/4.x/3rdparty/ffmpeg/readme.txt>
- FFmpeg 许可总览：<https://ffmpeg.org/legal.html>

因构建时排除了 GPL 组件（如 x264、libac3），**不存在 GPL 传染**，
Apache-2.0 的视觉后端不会被 FFmpeg 的 GPL 条款污染。

> 此前交付协议 §7 事项 6 标记的「许可未核实」风险**至此关闭**。

## 分发时的两条路径（须择一）

OpenCV 官方给出明确指引：

> If LGPL/GPL software can not be supplied with your OpenCV-based product, simply
> **exclude `opencv_videoio_ffmpeg*.dll` from your distribution**; OpenCV will stay fully
> functional except for the ability to decode/encode videos using FFMPEG (though, it may
> still be able to do that using other API, such as **Video for Windows, Windows Media
> Foundation** or our self-contained motion jpeg codec).

### 方案 A：随包分发该 DLL（保留 FFmpeg 视频解码）

须同时履行 LGPL 的以下义务：

1. 附带 FFmpeg 的 `COPYING.LGPLv2.1`（及 `COPYING.LGPLv3` 若涉及）**全文**；
2. 提供 FFmpeg **对应版本的完整源码**或**书面要约**（ LGPL-2.1 §4 / §6；
   书面要约通常有效期至少三年）；
3. 允许用户替换该 DLL（LGPL 的「可重新链接」要求）。

> ⚠ **本仓库尚未包含 FFmpeg 的 `COPYING.LGPLv2.1` 全文与源码要约文本。**
> 这是交付协议 §7 事项 2（安装包/部署脚本）**必须补齐**的内容，
> 不可仅以本文件代替。

### 方案 B：发布包中排除该 DLL

- 直接**不分发** `opencv_videoio_ffmpeg4130_64.dll`，即不触发上述 LGPL 义务。
- 影响：本产品**无法**用 FFmpeg 后端解码视频文件。
- 相机取像（`OpenCvVisionProvider.grab` 走的 `VideoCapture` 设备索引路径）
  仍可用 **DirectShow / MSMF** 后端，不依赖 FFmpeg。
- 若后续需要「读取视频文件」能力，可用 OpenCV 自带的 **MJPG 编码器**
  （`CV_FOURCC('M','J','P','G')` 写入 `.avi`），官方明示可放心使用。

### 建议

**采用方案 B（排除该 DLL）**。理由：本产品主用途是相机实时取像，不依赖 FFmpeg；
排除后 LGPL 义务归零，合规面最小。若日后确有视频文件回放需求，再转方案 A 并补齐
LGPL 全文与源码要约。

> 该决策属交付协议 §7 事项 2 的实施细节，最终以发布包实际内容为准。
