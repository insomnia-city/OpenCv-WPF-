# 部署说明 · Deployment Guide

适用包：`HalconWorkflow-<version>-win-x64.zip`（版本标签见包内 `MANIFEST.txt`）

本包为**免安装解压即用**形态：解压到任意目录即可运行，不需要安装器、不需要写注册表。
包内自带 .NET 运行时，目标机器**无需预装任何软件**。

---

## 1. 前置条件

| 项 | 要求 |
|---|---|
| 操作系统 | Windows 10 1809 (17763) 及以上，64 位 |
| 运行时 | **无需预装**（自包含包内含 .NET 9 运行时与 WPF） |
| 磁盘 | 解压后约 210 MB；另需可写目录用于配置与日志 |
| 权限 | 普通用户即可。**不需要管理员权限**（未写 Program Files 以外的系统位置） |
| 相机 | 直连相机由 DirectShow / MSMF 后端访问；多相机或经采集卡时需先装好厂商驱动 |

> 若使用 `-FrameworkDependent` 产出的精简包，则目标机器须预装
> **.NET 9 Desktop Runtime (x64)**。

---

## 2. 安装（解压部署）

1. 将 zip **完整解压**到目标目录，例如 `D:\HalconWorkflow`。
   - 请勿在压缩软件内直接双击运行。
   - 目录路径建议避免含空格与中文；`OpenCvSharpExtern.dll` 为 x64 原生库，
     路径过长（>260 字符）会导致原生加载失败。
2. 校验完整性（可选但建议）：
   ```powershell
   Get-FileHash -Algorithm SHA256 .\HalconWorkflow-<version>-win-x64.zip
   ```
   与包旁的 `.zip.sha256` 比对。
3. 运行 `HalconWorkflow.App.exe`。

解压后的目录结构：

```
HalconWorkflow-<version>-win-x64\
├─ HalconWorkflow.App.exe          程序入口
├─ OpenCvSharpExtern.dll          OpenCV 4.13 x64 原生库（必需，勿删）
├─ *.dll                          托管程序集与 .NET 运行时
├─ cs\ de\ es\ fr\ ...            .NET 卫星资源（多语言运行时）
├─ plugins\                       插件放置目录（可为空）
├─ LICENSE                        本软件专有许可（© 2026 陈浪）
├─ THIRD-PARTY-NOTICES.md         第三方许可总表
├─ third-party\                   第三方许可正文（8 个文件，4 个子目录）
│  ├─ opencv\         Apache-2.0 全文、COPYRIGHT、变更说明、NOTICE
│  ├─ opencvsharp\    Apache-2.0 全文、NOTICE
│  ├─ system.drawing.common\  MIT 全文（System.Drawing.Common 传递依赖）
│  └─ ffmpeg\         LGPL 方案 B 要约与说明
├─ docs\DEPLOYMENT.md             本文件
├─ MANIFEST.txt                   文件清单
└─ SHA256SUMS.txt                 逐文件哈希
```

---

## 3. 配置与数据位置

程序**不写安装目录**，全部用户态数据落在：

```
%LOCALAPPDATA%\HalconWorkflow\
├─ config\appsettings.json     应用设置
├─ devices.json                设备目录
└─ <日志目录>                  由 appsettings.json 的 LogDirectory 决定
```

`appsettings.json` 可配置项（缺文件、损坏或无权限时自动回退为同值默认，不影响启动）：

| 键 | 默认值 | 说明 |
|---|---|---|
| `Culture` | `zh-Hans` | 界面语言。支持 `zh-Hans` / `zh-Hant` / `en-US` |
| `LogDirectory` | 空（程序内默认） | 日志目录 |
| `EnablePreview` | 见默认 | 图像窗实时预览开关 |
| `TraceRetentionDays` | 见默认 | 追溯数据保留天数 |

也可在程序内「设置」对话框修改，**需重启程序生效**。

> **备份建议**：迁移或重装前，备份 `%LOCALAPPDATA%\HalconWorkflow` 整个目录。
> 追溯数据库、配方与设备目录都在其中。

---

## 4. 卸载

1. 关闭程序（确认托盘/窗口已退出）。
2. 删除解压目录。
3. 如需彻底清理，删除 `%LOCALAPPDATA%\HalconWorkflow`。

**不写注册表，无服务，无计划任务**，因此不存在残留注册项。

---

## 5. 许可合规（重要，请勿删除 `third-party\`）

Apache-2.0 §4(a) 要求分发时随附许可副本。NuGet 包本身不含任何许可文件，
因此包内 `third-party\` 目录是**唯一**满足该义务的载体，**不得删除或改名**。
`System.Drawing.Common` 为 MIT，MIT 同样要求「版权与许可声明须包含在软件所有副本中」，
故其正文 `third-party\system.drawing.common\LICENSE.txt` 也必须随包分发。

**包内不含 `opencv_videoio_ffmpeg*.dll`，这是刻意为之。** 该 DLL 封装 FFmpeg
（LGPL-2.1-or-later，非 GPL），本产品已采用**方案 B**：在构建与发布阶段将其排除，
从而**不触发** LGPL 的源码提供、书面要约、允许替换三项义务。

由此产生一项**能力边界**，请在验收时注意：

- ✅ 相机实时取像正常（走 DirectShow / MSMF，不经 FFmpeg）
- ❌ 依赖 FFmpeg 的**视频文件解码不可用**（部分 mp4/h264 容器）
- 替代：如日后需要视频回放，改用 OpenCV 自带 MJPG 编码器（写入 `.avi`），无需 FFmpeg

若确需随包分发 FFmpeg，则必须改为**方案 A**并补齐 LGPL 全文 + 对应版本源码或书面要约
+ 允许用户替换的说明，届时须同步更新 `third-party/ffmpeg/NOTICE.md`。

---

## 6. 故障排查

| 现象 | 排查方向 |
|---|---|
| 启动即报 `DllNotFoundException` / `BadImageFormatException` | `OpenCvSharpExtern.dll` 缺失或被安全软件隔离；确认目录完整、路径不过长 |
| 相机 `grab` 报错、设备打不开 | 先在「设备管理」确认索引与厂商驱动；`grab` 不会伪造图像，设备不可用即抛错 |
| 视频文件无法解码 | **预期行为**，见第 5 节方案 B 说明 |
| 配置改了不生效 | 该组设置需重启程序 |
| 追溯记录丢失 | 检查 `%LOCALAPPDATA%\HalconWorkflow` 磁盘空间与写权限 |
| 界面语言不对 | `appsettings.json` 的 `Culture`；受支持值见上表 |

日志位置见 `appsettings.json` 的 `LogDirectory`，排障时请附日志。

---

## 7. 支持

- 保修与响应口径见 `docs/DELIVERY-AGREEMENT.md` §5（P0 4h / P1 1 工作日 / P2 3 工作日）。
- 漏洞报告方式见仓库 `SECURITY.md`（**不要**通过公开 issue 报告）。
- 本软件为**专有闭源**，© 2026 陈浪，保留所有权利；授权范围以双方另行签署的书面合同为准。
