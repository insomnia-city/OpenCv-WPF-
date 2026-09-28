# 变更日志 · Changelog

本文件记录按阶段划分的实质变更。条目依据 git 提交历史与 README 阶段台账整理。

> **本项目尚无正式版本号**（无 `Version` 属性、无 release tag），故本文件按**阶段**而非
> 语义化版本组织。版本化发布策略属交付协议 §7 事项 4。
> 格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)。

---

## 首次托管 CI（2026-09-28）

### 修复（重连状态机的跨锁不一致观测）

`ModbusTcpConnection` / `S7TcpConnection` 的恢复（reconnect）路径原先用**两把锁**
（`_recoveryGate` 管 `_recovering`，`_stateGate` 管 `_state`）**分别发布**状态：

- 传输故障后用 `Disconnected → Reconnecting` 两步过渡，暴露一个窗口——`IsRecovering`
  已为 `true` 而 `State` 仍为 `Disconnected`；
- 放弃/愈合同样跨锁清除标志与状态，观察者可能读到 `State==Disconnected`（或
  `Connected`）而 `IsRecovering` 尚未同步。

这在 8 核本地机上从未暴露（窗口极短），但在 GitHub `windows-2022` 托管 runner
（2 vCPU）上被放大，三处重连/恢复测试间歇失败。现已改为**进入恢复、愈合、放弃都
在单把锁内原子发布** `IsRecovering` 与 `State`，并直接落地 `Reconnecting`（不再经
`Disconnected` 过渡）；`IsRecovering` getter 也从裸读改为锁保护。

### 修复（门禁 SDK 断言与产物上传路径）

- 门禁第 1 项原用 `StartsWith(global.json 版本)` 判断——`rollForward: latestFeature`
  允许解析到同特性带的更高 patch（runner 上 `9.0.317 → 9.0.318`），前缀匹配误判失败。
  改为解析为 `Version` 后做「同主版本且不低于 pin」的语义比较。
- `.github/workflows/ci.yml` 的 `upload-artifact` 原先引用 `env.TEMP`——action 的
  `env` 上下文没有该变量，路径解析为空而找不到任何文件，失败 run 的 TRX 拿不到。
  改为把门禁工作目录显式钉到 `${{ runner.temp }}`，上传与之对齐。

---

## 分发包（2026-09-28）

### 新增

- `tools/pack/package_release.ps1` —— 产出免安装分发包。默认**自包含** win-x64
  （目标机零前置），`-FrameworkDependent` 可出精简包。产出目录 + zip + 逐文件
  `SHA256SUMS.txt` + `MANIFEST.txt` + zip 自身 `.sha256`。
  版本标签默认为 `dev-<日期>-<短 sha>`，**刻意不用语义化版本**——项目尚无 `Version`
  属性与 release tag（§7 事项 4 未决），此处宣称 1.0.0 等于替用户做了版本决策。
- `docs/DEPLOYMENT.md` —— 前置条件、解压部署、目录结构、配置路径、备份、卸载、
  许可合规说明（含方案 B 能力边界）、故障排查表。
- CI 门禁新增**第 8 项 `package`**：按产物实际内容校验许可正文随包分发且方案 B 仍成立。

### 修复（**方案 B 曾在一个真实场景下失效**）

`Directory.Build.targets` 原先只删除 `runtimes\win-x64\native\opencv_videoio_ffmpeg*.dll`。
但带 RID 的自包含发布（`dotnet publish -r win-x64 --self-contained`）会把原生库
**平铺到发布根目录**，旧模式匹配不到——`opencv_videoio_ffmpeg4130_64.dll`
（**27.3 MB**）被静默打进交付包，LGPL 的源码/要约/允许替换义务全部复活，方案 B 形同虚设。
现已同时排除 `runtimes` 与平铺两种布局，包体相应从 233.4 MB 降到 206.2 MB。

该缺陷是编写打包脚本时实测发现的，**不是靠读代码想出来的**。门禁第 7、8 项都改为
按产物实际内容判断，而非信任 MSBuild 删除模式是否命中。

### 修复（NU1701：WpfExtensions 放错了项目）

`OpenCvSharp4.WpfExtensions` 仅提供 `net48` 与 `net8.0-windows7.0` 资产，只能被
`net*-windows` 项目正确引用。原将其放入 `net9.0` 的 `Runtime` 项目，NuGet 回退到
.NET Framework 资产并报 `NU1701`（在 .NET 9 应用中加载 net48 产物，运行时可能
`TypeLoadException`）。已移至 `src/App`（`net9.0-windows`），NU1701 归零。
相关放置约束已写入 `THIRD-PARTY-NOTICES.md`，防止再次放错。

### 依赖台账

新增 `OpenCvSharp4.Windows`（元包，0 二进制）、`OpenCvSharp4.WpfExtensions`（仅托管
DLL）、传递依赖 `System.Drawing.Common` 10.0.9（MIT）。前两者许可 expression 为
`Apache-2.0`，与既有 `third-party/opencvsharp/LICENSE` 同一份全文，故无需新增许可正文。
四项均不引入原生库，方案 B 不受影响。

### 修复（自审：MIT 正文曾漏发）

首次打包的提交里，我把 `System.Drawing.Common` 10.0.9 记为 MIT 写进了台账，
却**没有**随包附带它的许可正文——MIT 明确要求「版权声明与许可声明须包含在软件所有
副本中」，而 `System.Drawing.Common.dll` 确实在 zip 里。Apache-2.0 那两项复用既有
全文可以蒙混过关，MIT 没有这种余地，这一项当时是真漏了。

已从 NuGet 包内 `LICENSE.TXT` 逐字复制到
`third-party/system.drawing.common/LICENSE.txt`
（SHA256 `A89886665765362EB77E0F8E26602C924520041D1711B2EEDC136434FE4D01AB`），
并写进打包脚本与 CI 门禁的强制断言。**已做反向验证**：临时删除该文件后门禁第 8 项
立即失败并精确报出缺失路径，退出码 1；恢复后 9/9 转绿。

同时修正三处随改动而失真的说明：台账与部署文档的许可目录清单、`DELIVERY-AGREEMENT.md`
两处「11 项依赖」（实为运行时 9 + 测试/构建 5，共 14 项）。

这条的教训是：把依赖记进台账不等于履行了它的许可义务。台账只是索引，
义务要落到**分发物里**，并由构建断言守住。

### 实测结果

- 门禁 **9/9 通过**，421/421 测试，Release 0 错误。
- 分发包 446 文件 / 206.2 MB，zip 86.7 MB，单一顶层目录，8 项许可文件齐备。
- **启动冒烟测试通过**：`HalconWorkflow.App.exe` 运行 12 s 稳定（工作集 134.6 MB），
  进程内确认已加载 `OpenCvSharpExtern.DLL`，即运行时与 OpenCV 原生均正常。

### 待办

- 构建存在**既有**分析器警告（xUnit2012 ×12、xUnit1031 ×8、MVVMTK0039 ×4、
  xUnit2009 ×2），集中在测试代码与 ViewModel，与本次改动无关，未在本轮处理。

---

## CI 基建（2026-09-27）

### 新增

- `tools/checks/ci_gate.ps1` —— 交付门禁，**唯一判定源**。8 项检查：
  SDK 满足 `global.json` → restore → Release 构建 0 错误 → 全量测试 → TRX 判定
  （0 失败、无异常终态、测试数不低于 421、视觉测试未跳过）→ App publish →
  **FFmpeg 方案 B 排除在发布产物中生效，且 `OpenCvSharpExtern.dll` 未被误删**。
  输出同时写入 `%TEMP%\halcon-ci-gate\gate-output.txt` 供 CI 附档与摘要。
- `.github/workflows/ci.yml` —— Windows x64 runner（App 为 WPF `net9.0-windows`，
  视觉原生为 win-x64，两者都决定了不能用 Linux runner）。**workflow 不含任何判定逻辑**，
  只调用门禁脚本，避免本地与 CI 判定漂移。上传 TRX（always）与发布产物（失败时）。
  当前仓库**尚无 git remote**，故该 workflow 处于待激活状态。

### 修复（`stage21_trx_summarize.ps1` 的三处失效）

1. `--logger trx;LogFileName=<固定名>`：同一 if 无关，各测试项目写入**同一路径互相覆盖**，
   最终只留下最后完成的那个程序集（实测 13 个程序集只剩 1 个，421 项只读到 67 项）。
   改为不指定文件名，VSTest 为每个项目各自命名，再聚合全部 TRX。
2. 从 `UnitTestResult` 读取 `assemblyName` 属性：该属性**在 TRX 格式中不存在**，
   取值恒为空，导致所有测试被归入同一个无名分组。改为经 `testId` 映射到
   `TestDefinitions/UnitTest/TestMethod/@codeBase`，按真实测试程序集分组。
3. 通过 `ProcessStartInfo.ArgumentList` 传参：该属性在 Windows PowerShell 5.1
   （.NET Framework）中不存在，脚本在 5.1 下必然空引用失败，实际只有 pwsh 7 能跑。
   改为直接调用 `dotnet`，并去掉对自动变量 `$args` 的遮蔽。

修正后本机 5.1 实测 421/421、13 个程序集分组正确。

---

## 未发布 — 交付前承诺事项

交付协议 §7 承诺、尚未完成：

- [ ] 运维文档（安装手册、故障排查、参数备份恢复）—— `docs/DEPLOYMENT.md` 已覆盖
      部署/配置/备份/卸载/排查，待补的是长期运维（升级流程、日志归档、备份策略）
- [ ] 发布 tag 策略（版本治理剩余项）
- [ ] 人工可操作仿真演示件（不依赖硬件）

已关闭：

- [x] 安装包 / 部署脚本 → `tools/pack/package_release.ps1` + `docs/DEPLOYMENT.md`，
      免安装自包含分发包，已通过启动冒烟测试。
- [x] CI/CD 流水线 → 门禁脚本 `tools/checks/ci_gate.ps1` 为唯一判定源（**9 项检查**），
      `.github/workflows/ci.yml` 仅调用之。含分发包许可与方案 B 守卫。
- [x] 选定本项目原创代码许可 → **专有闭源**，© 2026 陈浪，保留所有权利，授权以双方
      另行签署的书面合同为准（`LICENSE` 已写入全文）。
- [x] 核实 `opencv_videoio_ffmpeg4130_64.dll` 内 FFmpeg 的实际许可 → **LGPL-2.1-or-later，
      非 GPL-2.0**（OpenCV 官方 `3rdparty/ffmpeg/readme.txt`：Windows 预编译版本
      "without GPL components"），无 GPL 传染。
- [x] FFmpeg 分发路径定案 → **采用方案 B（排除该 DLL）**，由 `Directory.Build.targets`
      在 Build/Publish 后删除；已实测发布产物中残留为 0，相机取像不受影响。
      故不再需要随包附 LGPL 全文与源码要约。
- [x] 修复两个负载敏感的测试竞态（`SchedulerDebugTests` 先发信号后订阅；
      `LiveDataFlowTests` 假设节点事件同步投递）。对照实验：修复前基线串行 4 轮全失败，
      修复后 4 倍 CPU 负载下 10 轮 0 失败，串行/并行均 421/421。

### 修复

- 测试竞态修复仅改测试代码，未触碰任何生产代码。
- `tools/checks/stage21_trx_summarize.ps1` 三处失效（详见下节「CI 基建」）。

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
