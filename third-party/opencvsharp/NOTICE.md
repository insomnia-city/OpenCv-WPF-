# OpenCvSharp — 归属声明 / Attribution Notice

本目录 [`LICENSE`](LICENSE) 为 OpenCvSharp 官方许可全文，逐字复制自
`shimat/opencvsharp` 仓库，未作任何修改。

- **许可**：Apache License 2.0（与本机 NuGet 缓存中 `OpenCvSharp4` /
  `OpenCvSharp4.runtime.win` 4.13.0.20260627 的 `.nuspec` `license` 字段一致）
- **来源**：<https://github.com/shimat/opencvsharp/blob/master/LICENSE>
- **作用**：OpenCV 的 .NET 托管绑定。本产品通过它访问 OpenCV 原生库
  （`runtimes/win-x64/native/OpenCvSharpExtern.dll`）。

## 版权声明

Apache-2.0 §4(c) 要求保留源文件中的版权、专利、商标与归属声明。OpenCvSharp 的版权
声明保留在其 `LICENSE` 文件内（逐字复制于本目录），随本产品一同分发。

## 第三方组件

OpenCvSharp 的 Windows 原生运行时包内含 FFmpeg 封装，其许可独立存在，
见 [`../ffmpeg/NOTICE.md`](../ffmpeg/NOTICE.md)。

## 注意

`OpenCvSharp4.runtime.win` NuGet 包目录内**不含任何 `LICENSE`/`NOTICE`/`COPYING` 文件**
（已实测确认），其 `.nuspec` 仅以 license *expression* 声明 `Apache-2.0`。
因此 Apache-2.0 §4(a) 要求的「随附许可副本」**不由 NuGet 自动满足**，
须由本目录的文件在制作发布包时显式附带（交付协议 §7 事项 2）。
