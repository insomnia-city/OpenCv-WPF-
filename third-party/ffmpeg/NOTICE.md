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

### ✅ 已采用：方案 B（排除该 DLL）

**决定日期：2026-09-27。经审阅后选定方案 B。**

`Directory.Build.targets`（仓库根）中的 `RemoveFfmpegRuntimeFromOutput` 与
`RemoveFfmpegRuntimeFromPublish` 两个 target，在 `Build` 与 `Publish` 之后删除
`runtimes\win-x64\native\opencv_videoio_ffmpeg*.dll`。

**从 build 输出也一并删除（而非只在 publish 时删）**，是为避免「测试覆盖的原生能力」
与「实际交付的」不一致：若开发/测试环境仍带 FFmpeg 而发布产物不带，两者行为会出现
难以察觉的漂移。故测试环境与交付环境保持一致。

**因不分发该 DLL，LGPL 的源码提供、书面要约、允许替换等义务均不触发。**
本仓库因此**无需**附带 `COPYING.LGPLv2.1` 全文与 FFmpeg 源码要约。

**能力影响（已核实）**：

- 相机实时取像不受影响。`OpenCvVisionProvider.grab` 使用
  `VideoCapture(..., VideoCaptureAPIs.ANY)`，枚举到相机时由 **DirectShow / MSMF** 后端处理，
  不经 FFmpeg。
- 本项目代码不使用 `VideoWriter`、`Cv2.ImShow` 或任何 highgui API，全仓无相关引用。
- **仅失去**依赖 FFmpeg 的视频文件解码（部分 mp4/h264 容器与编码）。若日后需要视频文件
  回放，改用 OpenCV 自带 **MJPG** 编码器（`CV_FOURCC('M','J','P','G')` 写入 `.avi`）即可，
  官方明示可放心使用，无需 FFmpeg。

**若日后要改为随包分发（方案 A）**：须删除 `Directory.Build.targets` 中的两个 target，
并补齐 LGPL 全文 + 对应版本源码或书面要约 + 允许用户替换的说明。

### 方案 A（备选，未采用）：随包分发该 DLL

须同时履行 LGPL 的以下义务：

1. 附带 FFmpeg 的 `COPYING.LGPLv2.1`（及 `COPYING.LGPLv3` 若涉及）**全文**；
2. 提供 FFmpeg **对应版本的完整源码**或**书面要约**（LGPL-2.1 §4 / §6；
   书面要约通常有效期至少三年）；
3. 允许用户替换该 DLL（LGPL 的「可重新链接」要求）。
