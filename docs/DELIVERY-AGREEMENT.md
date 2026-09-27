# 交付协议 · Delivery Agreement

> **状态：v1.0 已确认（2026-09-27）。** 双方已确认全文，包括 §5 保修与响应时限的具体数值，
> 以及 §2 中 1~5 项工程化缺口**须在正式交付前补齐**（见 §7 承诺事项跟踪）。
> 本文为技术交付口径确认，不替代商务/法务正式合同。
> 全文以**仓库实际代码**为准；任何与代码不符的表述以代码为准并应修订本文。

- 项目：HalconWorkflow — 图引擎内核 + 插件化节点库 + 协议无关通讯抽象层
- 技术栈：.NET 9 · WPF · xUnit · OpenCvSharp4 4.13.0.20260627（Apache-2.0）
- 文档版本：v1.0（阶段 26 视觉后端迁移后全文确认）

---

## 1. 交付范围

### 1.1 已交付且有测试覆盖

| 模块 | 内容 | 测试数 |
|---|---|---|
| 图引擎内核 `src/Core` | `GraphModel`、类型系统、拓扑排序、调度器（含触发/撤回）、序列化与迁移、撤销/重做、权限、事件总线 | 67 |
| 通讯 `src/Protocols` | Modbus TCP + S7 帧编解码、连接与重连、设备目录 | 44 |
| 运动 `src/MotionDrivers` `src/Nodes.Motion` | 原生/幻影驱动、轴单位、限位 | 13 + 13 |
| 视觉 `src/Nodes.Vision` | 节点契约、参数反射、帧桥、引擎池、**OpenCV 生产提供器** | 56 |
| 采集 `src/Nodes.Comm` | 订阅/发布、Tag 触发器 | 15 |
| 数据 `src/Nodes.Data` | 数据节点 | 8 |
| 存储 `src/Storage` | Dapper + SQLite、审计、追溯 JSONL、图像归档 | 12 |
| 插件 `src/Plugins` | ALC 装载、白名单、样例插件 | 13 |
| 原生计算 `src/Native` | 体素/点云 ABI、托管侧原语 | 30 |
| 运行时 `src/Runtime` | 无头宿主、批处理、追溯 sink | 11 |
| 壳层 `src/App` | 壳层、节点面板、图像窗、设置对话框、触发设置、能力面板 | 115 |

**全解决方案合计 421 项单测，0 失败、0 跳过。**

### 1.2 视觉后端决策（阶段 26）

HALCON 路线**已终止**，不是待办项：

- 本机仅安装 HALCON 12.0 **x86**；托管程序集可加载，但任何原生调用抛
  `System.BadImageFormatException`（HRESULT `0x8007000B`）。
- 无 x64 HALCON 授权 ⇒ 无法交付 64 位可运行程序。
- 结论：改用 **OpenCvSharp4 4.13.0.20260627**（Apache-2.0），已用独立 x64 `net9.0`
  探针实测：原生加载 OK，`Cv2.GetVersionString()` = 4.13.0。

`hdev` 算子**已下线**并从产品中移除（OpenCV 无 `HDevEngine` 对应物）。经确认**不存在任何
历史/存量图文件**（未交付过含 `vision.hdev` 节点的生产图），故此为**无迁移负担的干净断代**：
不存在需要改接 `vision.threshold` / `vision.measure` 的既有图。代码侧亦未保留任何 hdev 迁移或
兜底逻辑，不产生死代码。

---

## 2. 已知缺口

**均不得在验收时按「已完成」计。** 1~5 项经双方确认为**正式交付前必须补齐**的承诺事项，
跟踪状态见 §7；6~7 项为已确认的后续范围外项。

| # | 缺口 | 性质 |
|---|---|---|
| 1 | **无 CI/CD**：仓库无流水线配置，质量门只能本地执行（§3.1） | 交付前必须补齐 |
| 2 | **无安装包/部署脚本**：仅 `dotnet run`/`dotnet publish` 产物，无 MSI/MSIX、无代码签名 | 交付前必须补齐 |
| 3 | **无运维文档**：无安装手册、故障排查手册、参数备份/恢复说明 | 交付前必须补齐 |
| 4 | **无版本治理基建**：无 `LICENSE`、`CHANGELOG.md`、`SECURITY.md`、版本化发布标签策略、`global.json` 锁 SDK | 交付前必须补齐 |
| 5 | **无独立视觉仿真演示**：`PhantomVisionEngine` 仅作测试后备，不是交付演示件 | 交付前必须补齐 |
| 6 | **诊断视图（阶段 25）未落地**：原计划的绘图库未引入 | 范围外，后续排期 |
| 7 | **`Nodes.OpenCV` 独立节点库（阶段 27）未落地**：当前视觉算子在 `Nodes.Vision` 内 | 范围外，后续排期 |

> 说明：第 5 项与 §3.3「仿真验收暂缓」不冲突——暂缓的是**现场相机相关**的仿真验收；
> 此处承诺的是**不依赖硬件**的人工可操作演示件。

---

## 3. 验收口径

### 3.1 质量门（甲方可自行复现）

```powershell
dotnet build HalconWorkflow.sln --nologo
dotnet test  HalconWorkflow.sln --nologo
```

- 构建：**0 error**。
- 测试：**421 通过 / 0 失败 / 0 跳过**。
- App.Tests 曾存在 `LogViewModel` 并发竞态导致偶发红，已修复（锁内追加 + `Snapshot()`），
  **连续 5 轮零失败**为当前实测结果。若要求更强保证，应引入 CI 做 N 轮回归（§7 事项 1）。

### 3.2 视觉算子为「真跑」而非声明

`Nodes.Vision.Tests/OpenCvVisionProviderTests.cs` 9 项**直接调用真实 x64 原生运行时**，无 mock：

- 原生运行时可加载；提供器声明 `threshold`/`measure`/`grab` 并拒绝 `hdev`
- `threshold` 双侧闭窗口 `min ≤ 灰度 ≤ max` 的边界值；窄 `max` 裁剪回归
- `measure` 单/双连通域结果与幻影引擎一致
- `grab` 在设备不可用时**抛错而非伪造图像**

### 3.3 暂缓项与硬件阻塞

- **相机现场验收**：需实机相机/驱动，交付前无法覆盖。`grab` 现场路径待接实机后复验。
- **运动卡 / PLC 现场验收**：受硬件阻塞，代码侧为原生 + 幻影双路径，幻影非现场证据。
- **仿真验收**：按当前排期暂缓；如需提前交付演示件，须另行约定范围。

---

## 4. 知识产权与第三方许可

- 甲方对源码中由乙方原创的部分享有使用权。
- 第三方组件随包分发，须保留其许可与声明：

  | 组件 | 版本 | 许可 |
  |---|---|---|
  | OpenCvSharp4 / OpenCvSharp4.runtime.win | 4.13.0.20260627 | Apache-2.0 |
  | Nodify | 7.3.0 | MIT |
  | CommunityToolkit.Mvvm | 8.4.2 | MIT |
  | Dapper | 2.1.35 | Apache-2.0 |
  | Microsoft.Data.Sqlite | 9.0.0 | MIT |

- **OpenCV 许可版本边界**：4.5.0 起为 **Apache-2.0**；4.4.0 及更早（含 3.x/2.x/1.x）为
  **3-clause BSD**。本产品分发 OpenCV **4.13** → 适用 **Apache-2.0**，**非 BSD**。
  OpenCV 仓库不提供 `NOTICE` 文件，故 §4(d) 对其不适用；§4(a) 的许可副本已随仓库附带于
  `third-party/`。
- **FFmpeg 为 LGPL-2.1-or-later，非 GPL-2.0**（OpenCV 官方说明其 Windows 预编译版本
  已排除 GPL 组件），**无 GPL 传染**。**已采用方案 B**：由 `Directory.Build.targets`
  在 Build/Publish 后排除 `opencv_videoio_ffmpeg4130_64.dll`，不触发 LGPL 义务。
  相机实时取像不受影响（走 DirectShow/MSMF），详见 `third-party/ffmpeg/NOTICE.md`。
- `OpenCvSharp4.runtime.win` NuGet 包内**无任何许可文件**（实测），
  Apache-2.0 §4(a)/(c) 须由 `third-party/opencvsharp/` 在发布包中显式满足（§7 事项 2）。
- **已补（§7 事项 4）**：`LICENSE` 已定为**专有闭源许可**，© 2026 陈浪，保留所有权利，
  授权以双方另行签署的书面合同为准；`THIRD-PARTY-NOTICES.md` 与 `third-party/` 许可正文
  目录已建，11 项依赖许可已按本机 NuGet `.nuspec` 实际元数据逐条核验，
  OpenCV/OpenCvSharp 的 Apache-2.0 全文另经 OpenCV 4.14.0 真实源码树 SHA256 交叉复核一致。
  **未完成**：发布 tag 策略。

---

## 5. 保修与支持（已确认数值）

- 保修期：验收后 **90 天**内修复缺陷。
- 缺陷分级与响应时限（工作日）：
  - **P0 阻断**（无法启动 / 无法出图 / 数据损坏）：响应 ≤ **4h**
  - **P1 严重**（主流程功能不可用、有绕行方案）：响应 ≤ **1 个工作日**
  - **P2 一般**（次要功能异常）：响应 ≤ **3 个工作日**
- SLA 可用性：不含硬件故障、相机/运动卡/PLC 现场问题、第三方组件上游缺陷。

> 上述数值已于 v1.0 由双方确认为议定值。

---

## 6. 已确认事项（2026-09-27）

1. **OpenCV 替代 HALCON 为最终视觉后端**。`hdev` 算子随之下线；因不存在历史图，
   此为**无迁移负担的干净断代**（详见 §1.2），不产生额外改造成本。
2. 阶段 25 诊断视图、阶段 27 `Nodes.OpenCV` **不在本次交付范围**，留待后续排期。
3. 相机 / 运动卡 / PLC 现场验收**时间窗与责任方待定**，不阻塞 §7 的软件侧承诺事项。
4. 仿真演示件：按 §7 事项 5 补齐**不依赖硬件**的人工可操作演示件（区别于受硬件阻塞的
   现场仿真验收，见 §3.3）。
5. 保修期 90 天与 §5 分级响应时限为**议定值**。
6. §2 中 1~5 项工程化缺口**须在正式交付前补齐**，跟踪见 §7。

---

## 7. 交付前承诺事项跟踪

| # | 事项 | 状态 | 备注 |
|---|---|---|---|
| 1 | CI/CD 流水线 | **已完成** | 门禁脚本 `tools/checks/ci_gate.ps1`（**唯一判定源**，8 项检查：SDK 与 `global.json` 一致、restore、Release 构建 0 错误、全量测试、TRX 判定含测试数下限与视觉原生未跳过、App publish、**FFmpeg 方案 B 排除在发布产物中生效且 `OpenCvSharpExtern.dll` 未被误删**）+ `.github/workflows/ci.yml`（Windows x64 runner，仅调用该脚本，不重复实现判定逻辑；上传 TRX 与失败时的发布产物）。本机实测 421/421、8/8 通过；并已注入双重故障反向验证两个关键守卫会拦截并返回退出码 1 |
| 2 | 安装包 / 部署脚本 | 待办 | 需产出可分发目录或安装包 + 部署说明；须一并附 OpenCvSharp/OpenCV 的许可与 NOTICE（见 §4） |
| 3 | 运维文档 | 待办 | 安装手册、故障排查、参数备份/恢复 |
| 4 | 版本治理基建 | **基本完成** | 已建 `global.json`（锁 SDK 9.0.317 + `rollForward: latestFeature`）、`CHANGELOG.md`、`SECURITY.md`、`THIRD-PARTY-NOTICES.md`（11 项依赖许可已按 NuGet `.nuspec` 实际元数据逐条核验）、`third-party/` 许可正文目录（OpenCV/OpenCvSharp 的 Apache-2.0 全文、OpenCV `COPYRIGHT`、许可变更说明、FFmpeg LGPL 声明）。**`LICENSE` 已定为专有闭源许可**：© 2026 陈浪，保留所有权利，授权以双方另行签署的书面合同为准。**未完成**：发布 tag 策略 |
| 5 | 人工可操作仿真演示件 | 待办 | 不依赖硬件；跑通 grab→threshold→measure→存图 |
| 6 | ~~FFmpeg 许可核实~~ | **已关闭** | OpenCV 官方 `3rdparty/ffmpeg/readme.txt` 明确 Windows 预编译 ffmpeg「without GPL components」且为 **LGPL-2.1-or-later，非 GPL-2.0** → 无 GPL 传染 |
| 7 | 第三方许可正文入包 | **已完成** | OpenCV/OpenCvSharp 的 Apache-2.0 全文、`COPYRIGHT`、许可变更说明、FFmpeg LGPL 声明已落 `third-party/`（SHA256 校验逐字一致）。**已采用方案 B**：由 `Directory.Build.targets` 在 Build/Publish 后排除 `opencv_videoio_ffmpeg4130_64.dll`，从而不触发 LGPL 源码/要约/可替换义务。已实测发布产物中该 DLL 残留为 0 |

**验收口径不变**：以上任一项未完成，均不得声称「可正式交付」。
