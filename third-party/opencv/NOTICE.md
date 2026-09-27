# OpenCV — 归属声明 / Attribution Notice

本目录 [`LICENSE`](LICENSE) 为 OpenCV 官方 Apache License 2.0 全文，逐字复制自
`opencv/opencv` 仓库 `4.x` 分支，未作任何修改。

## 许可版本边界（重要）

OpenCV 按版本适用**两种不同许可**：

| OpenCV 版本 | 许可 |
|---|---|
| **4.5.0 及更高**（含 4.5-pre，2020-08 起） | **Apache License 2.0** |
| **4.4.0 及更早**（含 3.x、2.x、1.x） | **3-clause BSD** |

分界线为 **4.5.0**。切换原因（BSD 无专利条款，Apache-2 增加了专利授权与专利反诉终止条款）
见 <https://opencv.org/opencv-is-to-change-the-license-to-apache-2/>。

> **本产品实际分发的是 OpenCV 4.13（经 `OpenCvSharp4.runtime.win` 4.13.0.20260627），
> 因此适用 Apache-2.0，而非 3-clause BSD。** 若日后降级至 4.4.0 或更早版本，
> 必须改用 BSD-3-Clause 并重新核验本目录内容。

3-clause BSD 全文见 [`LICENSE_CHANGE_NOTICE.txt`](LICENSE_CHANGE_NOTICE.txt)——OpenCV 官方在
该文件中保留了切换前的原始 BSD 许可正文，以合规覆盖切换前的历史贡献。

## 版权声明

OpenCV 各版权方声明见 [`COPYRIGHT`](COPYRIGHT)（逐字复制自 `opencv/opencv` `4.x`）：

```
Copyright (C) 2000-2022, Intel Corporation, all rights reserved.
Copyright (C) 2009-2011, Willow Garage Inc., all rights reserved.
Copyright (C) 2009-2016, NVIDIA Corporation, all rights reserved.
Copyright (C) 2010-2013, Advanced Micro Devices, Inc., all rights reserved.
Copyright (C) 2015-2023, OpenCV Foundation, all rights reserved.
Copyright (C) 2008-2016, Itseez Inc., all rights reserved.
Copyright (C) 2019-2023, Xperience AI, all rights reserved.
Copyright (C) 2019-2022, Shenzhen Institute of Artificial Intelligence and Robotics for Society, all rights reserved.
Copyright (C) 2022-2023, Southern University of Science And Technology, all rights reserved.
Copyright (C) 2023-2025, OpenCV AI, all rights reserved.

Third party copyrights are property of their respective owners.
```

## 关于 NOTICE 文件

**OpenCV 仓库不提供 `NOTICE` 文件**（`opencv/opencv` `4.x` 分支下 `NOTICE` 路径返回 404）。
因此 Apache-2.0 §4(d)（"If the Work includes a NOTICE text file…"）对 OpenCV **不适用**。

本文件是**本项目自行撰写的归属说明**，不是上游 NOTICE 的副本。
Apache-2.0 §4(a) 要求的「向接收方提供本许可副本」由本目录的 `LICENSE` 满足。

## 第三方组件（随 OpenCV Windows 预编译包分发）

`opencv_videoio_ffmpeg4130_64.dll` 静态/动态封装了 FFmpeg，其许可**独立于** OpenCV，
见 [`../ffmpeg/NOTICE.md`](../ffmpeg/NOTICE.md)。

## 来源

- 许可正文：<https://github.com/opencv/opencv/blob/4.x/LICENSE>
- 版权声明：<https://github.com/opencv/opencv/blob/4.x/COPYRIGHT>
- 许可变更说明：<https://github.com/opencv/opencv/blob/4.x/doc/LICENSE_CHANGE_NOTICE.txt>
- 许可总览：<https://opencv.org/license/>
