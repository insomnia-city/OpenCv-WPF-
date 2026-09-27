# 变更日志 · Changelog

本文件记录按阶段划分的实质变更。条目依据 git 提交历史与 README 阶段台账整理。

> **本项目尚无正式版本号**（无 `Version` 属性、无 release tag），故本文件按**阶段**而非
> 语义化版本组织。版本化发布策略属交付协议 §7 事项 4。
> 格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)。

---

## 未发布 — 交付前承诺事项

交付协议 §7 承诺、尚未完成：

- [ ] CI/CD 流水线（Windows runner：build + test）
- [ ] 安装包 / 部署脚本
- [ ] 运维文档（安装手册、故障排查、参数备份恢复）
- [ ] 版本治理基建（`LICENSE`、发布 tag 策略）
- [ ] 人工可操作仿真演示件（不依赖硬件）
- [ ] 分发包附 FFmpeg `COPYING.LGPLv2.1` 全文与源码/书面要约（仅在选择随包分发该 DLL 时需要；
      建议改为排除该 DLL 以规避）

已关闭：

- [x] 核实 `opencv_videoio_ffmpeg4130_64.dll` 内 FFmpeg 的实际许可 → **LGPL-2.1-or-later，
      非 GPL-2.0**（OpenCV 官方 `3rdparty/ffmpeg/readme.txt`：Windows 预编译版本
      "without GPL components"），无 GPL 传染。

---

## 阶段 26 — 视觉后端迁移：HALCON → OpenCV 4.13（2026-09-27）

### 破坏性变更

- **HALCON 路线终止**（非延后）。本机仅 HALCON 12.0 x86，原生调用抛
  `BadImageFormatException`（`0x8007000B`），且无 x64 授权 → 无法交付 64 位可运行程序。
  已用独立 x64 `net9.0` 探针实测 OpenCvSharp 4.13 原生加载与阈值分割均通过。
- **`vision.hdev` 算子下线**：OpenCV 无 `HDevEngine` 对应物。移除 `HdevParameters` /
  `HdevNode`、工厂注册、调色板项（26→25）、配色资源、三语文案。
  无迁移负担（不存在历史图），代码侧未留迁移或兜底逻辑。
- **删除** `HalconDotNetAdapter`（x86-only）及 MVTec 路径探测。

### 新增

- `OpenCvVisionProvider`：`grab`（`VideoCapture`，设备不可用时抛错而非伪造图像）、
  `threshold`、`measure`（连通域统计）。
- `App.OnStartup` 原生可用时显式注册生产提供器；不可用则告警并回退仿真。
- `LogViewModel.Snapshot()`：供非 UI 线程安全读取日志快照。

### 修复

- `threshold` 改用 `Cv2.InRange` 实现双侧闭窗口 `min ≤ 灰度 ≤ max`。
  原 `ThresholdTypes.Binary` 只与 `thresh` 比较、**静默忽略 `max`**。
- `measure` 分割阈值改为 `level-1`，使 `src > thresh` 对齐幻影引擎的 `≥` 语义。
- `LogViewModel.Add` 补锁：此前调度线程追加日志会让 `TriggerIntegrationTests` 偶发
  `Collection was modified`（**已修的既有缺陷**）。
- `RuntimeCapabilities` 原上报「Halcon runtime + adapter」——迁移后属**错误能力报告**，
  改为 OpenCV 运行时 + 提供器注册态。

### 重命名

`IHalconAdapter`→`IVisionProvider` · `HalconAdapterRegistry`→`VisionProviderRegistry` ·
`HalconVisionEngine`→`OpenCvVisionEngine` · `FakeHalconAdapter`→`FakeVisionProvider` ·
`FrameDomain.Halcon`/`AsHalcon()`→`Synthetic`/`AsSynthetic()`

### 测试

新增 `OpenCvVisionProviderTests` 9 项，**直接调用真实 x64 原生运行时、无 mock**。
全解决方案 **421 通过 / 0 失败 / 0 跳过**。

---

## 阶段 30 — 设置对话框（2026-09-26）

`appsettings.json` 持久化（`Culture`/`LogDirectory`/`EnablePreview`/`TraceRetentionDays`）
+ 按次重启重载。`SettingsViewModel` 绑定快照副本、确定时提交不可变替换 record；
缺文件/损坏/无权限回退同值 `Defaults`。

## 阶段 22–24 — 调试与图像窗（2026-09-19 ~ 09-23）

- S7 ISO-on-TCP 协议 + 进程内 `S7TcpSimulator` 回环从站（无硬件可绿测）
- 调试：断点 / 暂停 / 单步 / 重跑
- 独立 WPF 图像窗：按节点过滤、历史回放、实时门控

## 阶段 17–21 — 插件、现场就绪、连接、触发（2026-09-18 ~ 09-19）

- 阶段 17：可回收 `AssemblyLoadContext` 插件装载
- 阶段 15–16：现场就绪自检 + 未保存变更守卫
- 阶段 14：真实 HALCON 适配器接缝与解析管线（**已于阶段 26 移除**）
- 阶段 12–13：交互式连接与运行时实时数据
- 阶段 11：全局撤销、审计拒绝、三语清单
- 阶段 18–21：配方持久化 / schema 迁移 / 触发系统 / 设备目录 / Modbus 重连心跳

## 阶段 9–10 — 追溯、看板、原生内核（2026-09-17）

- 阶段 10：`vx_*` C ABI 原生计算内核 + 托管回退
- 阶段 9：追溯看板能力层（schema v2 + 维度 / 统计 / 图像归档 / 预览环）、
  仪表盘 UI、操作审计（schema v3）、撤销保存点与基于角色的权限

## 阶段 5–8 — 视觉、运动、存储、数据（2026-09-17）

- 阶段 5：`Nodes.Vision`（引擎池 / 帧桥 / 仿真算子）+ 反射式属性面板（可撤销参数）
- 阶段 6：`Protocols`（Tag 表 + 纯 .NET Modbus TCP）+ `Nodes.Comm`
- 阶段 7：`MotionDrivers`（进程级原生加载 + 幻影回退）+ `Nodes.Motion`
- 阶段 8：`Storage`（方言适配 + 非阻塞追溯队列 + 流式 CSV）+ `Nodes.Data`

## 阶段 1–4 — 内核与壳层（2026-09-16）

- 阶段 1（Core + Abstractions）+ 阶段 2（Runtime）：图引擎、序列化、无头宿主
- 阶段 3：WPF 最小画布（Nodify + MVVM + i18n 热切换）
- 阶段 4：`Nodes.Flow`（分支/汇合/计数/延时/脚本）+ 内核跨层撤销

---

## 更早的破坏性变更提示

- **阶段 26**：图契约 `vision.hdev` 不再受支持；`FrameDomain.Halcon` 更名为 `Synthetic`。
  因不存在历史图/存量配方文件，**无数据迁移负担**。
- 持久化 schema（`halcon/device-catalog` 等字符串标识）**刻意保持不变**，
  以免破坏既有设备目录文件；产品更名不改 schema id。
