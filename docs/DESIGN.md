# 设计文档（Design Document）

> 版本：v0.7 落地版 | 状态：设计评审中，尚未实现 | 关联：机器视觉工作流平台

---

## 1. 目标与非目标

### 目标
- 用**节点图**可视化编排"触发 → 采集 → 视觉处理 → 判定 → 通讯回写"的完整流程。
- 图引擎内核可作为无界面生产/服务/CI 运行，UI 仅是表达层。
- 多协议设备接入通过**统一 Tag 寻址 + 适配器插件**，不污染流程逻辑。

### 非目标（当前阶段）
- 不做 Web/Blazor 前端（相机 SDK、Halcon、板卡全为 Windows 原生，Web 化收益低、坑成倍增加）。
- 不做可视化节点外的完整 MES/追溯平台，只预留接口。

---

## 2. 分层原则与时间模型

| 层 | 内容 | 时间模型 |
|---|---|---|
| 图引擎内核 | GraphModel / 类型系统 / 调度器 / 序列化 | **静态拓扑 + 事件驱动执行** |
| 能力层（插件） | Halcon 视觉节点、通讯节点、运动节点、数据节点、采集、脚本 | **同步阻塞计算**（视觉）/ **异步 I/O + 状态机**（通讯/运动） |
| 数据层（Storage） | 追溯写入 · 查询 · 报表（能力层下沿，纯 I/O 化） | **异步批量 I/O** |
| 表现层 | WPF + MVVM + Nodify | UI 线程渲染 |

**严格规则**：图引擎内核不许引用任何 WPF / Halcon / 通讯库。它只定义 `INode`、`IPort`、`ITypeDescriptor`、`IExecutionContext`。这是单元测试、CI、远程部署/无头运行的先决条件（依赖方向见下图）。

```
表现层  →   应用层  →   能力层(插件)  →   图引擎内核(依赖方向倒置，内核零依赖)
                         └──> 通讯层(I/O)
 任何层都不得反向引用内核
```

---

## 3. 技术选型结论

| 维度 | 决策 | 说明 |
|---|---|---|
| UI 框架 | **WPF + MVVM + Nodify** | 见 3.1「WinForms 评估」。Nodify：MIT、MVVM 原生、零依赖、数百节点优化、自带撤销/重做/主题/缩放平移 |
| MVVM | CommunityToolkit.MVVM（源生成器） | 所有面板以 ObservableObject + RelayCommand 驱动 |
| DI | Microsoft.Extensions.DependencyInjection | 宿主统一注册；插件通过注册器回调把服务汇入容器 |
| 日志 | Serilog（结构化，文件滚动 + 控制台） | 全链路 TraceId（触发来源→节点链）贯穿 |
| 重试/熔断 | Polly v8（ResiliencePipeline） | 只在通讯层 I/O 使用，视觉节点不做重试 |
| 属性面板 | PropertyGrid（WPF 移植版/自写反射树） | 配合「强类型参数特性」反射生成，几十个节点 UI 免手写 |
| 图序列化 | System.Text.Json + 节点契约版本 | 不用类名，见 §5.8 |

### 3.1 WinForms 评估（结论：不换）

| 维度 | WPF + Nodify | WinForms |
|---|---|---|
| 节点画布 | Nodify 现成（MVVM/撤销重做/缩放） | 无成熟等价物；`NodeEditorWinForms` 远不如 Nodify；GDI+ 自绘成本极高 |
| MVVM | 原生 Binding/Command/样式模板 | CommunityToolkit 源生成虽可用，但无 UI 绑定，手写同步量大 |
| 参数面板 | 用 WPF 移植版 PropertyGrid | 原生 PropertyGrid 略占优，但不抵消画布成本 |
| HSmartWindowControl | Halcon 官供 WPF 版 | Halcon 也有 WinForms 版，仅此一项占便宜 |
| 高 DPI / 主题 | 原生好 | 缩放/字体仍有坑 |
| 线程协同 | Dispatcher 回 UI | SynchronizationContext 等价，难度相当 |

**结论**：核心风险在画布与参数面板工程量。WinForms 省下的（PropertyGrid）抵不上要还的（自绘画布 + 手写绑定）。若维护团队全为 WinForms 老手且自绘画布可接受，再议；否则维持 WPF。

---

## 4. 表现层设计（WPF + MVVM + Nodify）

### 4.1 模块结构

```
App/Views        # ShellView（Dock：画布 | 属性 | 节点库 | 日志 | 诊断 | 状态栏）
App/ViewModels   # ShellViewModel / MainEditorViewModel / NodeViewModel / PortViewModel
                 # PropertyPanelViewModel / ImageWindowViewModel / LogViewModel
                 # DiagnosticsViewModel / ProjectViewModel(工程/配方/撤销) / OptionViewModel
App/Controls     # NodeTemplateSelector(按契约→DataTemplate) / 属性面板控件 / 图像窗控件
App/Theme        # Nodify 深浅主题资源
```

### 4.2 MVVM 规约
- **UI 模型与内核模型分离**：`MainEditorViewModel`（UI）与 `GraphModel`（内核）双向手写同步；绑定永远指向 VM 投影，禁止把内核实体当绑定源（HObject 尤其，见 §5.6）。
- VM 全部由 DI 构造注入，不 new；View 仅做 DataContext 绑定，不写业务逻辑。
- 图交互（载入/执行/停止/单步/重跑）一律 `AsyncRelayCommand` 进内核异步服务；工作线程只通过有限入口回 UI（见 §4.7）。

### 4.3 Nodify 集成
- `NodifyEditor.ItemsSource` = `Nodes`（NodeViewModel 集合），`Connections` = `Links`；节点 `DataTemplate` 由 `NodeTemplateSelector` 按契约命名（`types/ns.node.xaml`）解析。
- **撤销/重做**：Nodify 画布编辑操作 → 命令入全局 `UndoService`（命令栈，见 §9.2），同步驱动内核 GraphModel —— 保证"UI 撤销 == 引擎状态回滚"，两者永远一致。
- **连接校验实时反馈**：连线时调用内核 `GraphValidator`（与保存前静态校验同一实现），类型/方向/环路不通过则红显并禁止连线。
- 性能：大图开虚拟化；节点上千时使用冻结化 VM。

### 4.4 属性面板（反射生成）
- 节点参数 = `[NodeParameter(name, group, min, max, unit, ...)]` 特性标记的强类型 VM 属性。
- `PropertyPanelViewModel` 反射节点契约参数元数据 → 属性树 → WPF 版 PropertyGrid 渲染；**变更防抖写回 Recipe** 并置 `Dirty`。
- 无参数节点显示节点说明；契约缺版本/插件缺失时为只读 + 缺失标记（见 §11 挂起节点）。

### 4.5 图像窗
- `ImageWindowViewModel` 订阅 Execution 线程快照流 → 取 `ImageSnapshot`（Bitmap/ROI/Overlay 列表）→ `Dispatcher` 绘制到 WPF 画布 / `HSmartWindowControl`。
- 支持：按节点过滤（只看所选节点输出）、历史回放（快照环形缓冲）、ROI 叠加显隐、十字/标尺。
- **高频降采样**：UI 按渲染帧率丢弃重复帧，只读快照生产队列，不反压 Execution 线程。

### 4.6 日志与诊断
- Serilog → `LogViewModel` 环形队列，按级别/节点/TriggerId 过滤。
- `DiagnosticsViewModel`（ScottPlot）：每节点耗时条形图 + 周期总耗时趋势 + 断点/单步状态灯；EStop 快通道事件单独告警着色。

### 4.7 UI 线程铁律
- 唯一跨线程入口：`Dispatcher.BeginInvoke` 投递**只读 UI 投影**；工作线程绝不触碰控件和 `ObservableCollection`。
- 任何长操作异步 + 可取消；UI 线程禁止 `.Result` / `Wait()`。
- 图像/日志高频流合并为 UI 帧批次再投递，避免逐帧 BeginInvoke 洪泛。

---

### 4.8 本地化（中文 / 英文 / 韩文）
- **资源驱动**：UI 全部字符串进 resx / `ResourceManager`，零硬编码；`LocalizationService` 广播 `CultureInfo` 变更，绑定即时刷新（不重启）。
- **覆盖范围**：UI 文案（菜单/按钮/对话框/错误）、节点元数据（节点名/参数说明/端口名，由**插件语言包**按 Culture 解析）、日期时间/数字格式与单位（CultureInfo）。
- **回退链**：缺失语言键回退英文（`zh→en`、`ko→en`），永不白屏。
- 日志/审计字段存**英文键 + 结构化数据**，展示层按 Culture 渲染（避免中/韩文跨环境乱码检索）。
- 装箱验证：三语冒烟（界面切换 + 节点库 + 审计窗口）。

### 4.9 注释规范（英中双语）
- 源码注释一行双份：**英文在前、中文在后**。
```csharp
// Debounce window for coalescing triggers. /* 触发合并去抖窗口(ms) */
```
- 节点/参数的对外文档进多语言资源（3 语）；代码内 `///` 与行注释保持英中双行。
- 标识符（SQL/JSON/Schema/变量/提交信息）用英文；面向用户的展示文案走资源；**说明性注释必须带中文**。
- 原因：英文保国际可读性与检索稳定，中文接现场工程师。

---

## 5. 图引擎内核（核心竞争力）

### 5.1 执行模型：控制流为主 + 数据流引用
- 视觉流程是强时序的（触发 → 采集 → 预处理 → 定位 → 测量/检测 → OK/NG 分支 → 回传），用 **Exec In/Out** 端口串联时序。
- 数据端口（`HObject`/`HTuple`/数值/结果对象）只做**引用传递**，而非 Node-RED 式纯 msg 流。
- 控制流每个脉冲携带一个 **Signal 令牌**：`{ TriggerSource, TriggerId, Timestamp, 批次号 }`，贯穿全链路用于追溯与日志关联。
- 受益：符合工程师思维、可单步调试、可离线重放（令牌重放）。

### 5.2 内核契约（Core 只定义这些，实现全部在插件侧）

```csharp
// Core/Contracts
public interface INode {
    string Id { get; }                          // 图内唯一实例 Id
    NodeContract Contract { get; }              // { namespace, version } 契约名
    IReadOnlyList<IPort> Inputs { get; }
    IReadOnlyList<IPort> Outputs { get; }
    NodeState State { get; }
    Task ExecuteAsync(IExecutionContext ctx, CancellationToken ct);
}

public interface IPort {
    string Name { get; }
    PortDirection Direction { get; }            // In / Out
    PortKind Kind { get; }                      // Exec / Data
    ITypeDescriptor? Type { get; }              // Data 端口才有效
    bool IsConnected { get; }
    object? Value { get; }                      // Data 端口传递的引用（不参与绑定）
}

public interface ITypeDescriptor {
    string Name { get; }                        // "Image" / "Number" / "Result"
    bool IsAssignableTo(ITypeDescriptor target); // 兼容判定
}

public interface IExecutionContext {
    object? GetData(string tag);                // 作用域内数据查找（端口 tag）
    void SetData(string tag, object? value);
    IScope Scope { get; }                       // 资源作用域（HObject 池、快照、IDisposable 登记）
    IEventBus Events { get; }
    Signal Current { get; }                     // 当前控制流令牌
    T GetService<T>();                          // 插件可自取（相机句柄、连接、TagTable 等）
}
```

### 5.3 端口类型系统
- 基类型：`Value`（数值 Number{Integer/Real}/String/Bool/Point/Result），`VisionObject`（Image/Region/XLD，句柄语义）。
- 兼容规则：**子类可赋父类**（Image→VisionObject）；`Integer→Real` 隐式提升（可配）；`Result` 结构化可含多值。
- 连接校验顺序：方向 → 类型兼容 → 环路检测 → 触达 Duck-typed（`HasExecIn/Out`）。
- `VisionObject` 与 `Number` 等纯数据物理隔离：图像走句柄/对象池，数值走值类型，杜绝 HObject 进绑定。

### 5.4 调度器
- **拓扑序静态排序一次**（图装载/编辑完成时），每轮触发仅按序执行，不做运行时动态重排。
- 执行语义：默认**串行主干**；`Fork/Parallel` 显式分支并行；`Join` 汇聚（等待全部输入齐后继续）。
- 触发源（TriggerSource）：软触发 / 定时器(周期) / Tag 变化事件 / IO 中断 / 电平边沿。触发统一进调度队列，**合并去抖**：10ms 窗口、队列上限 1（丢旧保新）。

### 5.5 节点状态机

```
Idle ─触发→ Ready ─启动→ Running ─→ Succeeded ─→ (下一节点)
                     │               └→ (Faulted → 断点/回滚作用域)
                     └─取消→ Cancelled
```
- 图状态：`Stopped / Starting / Running / Pausing(单步) / Faulted / Broken(静态校验失败)`。
- 每节点执行打点：`开始/结束/耗时/输入输出快照引用`，写入环形事件缓冲，UI 只读。

### 5.6 线程模型
- **UI 线程**（WPF Dispatcher）：只渲染画布/图像/面板；一切回 UI 用 `Dispatcher.BeginInvoke`，禁止阻塞。
- **Execution 线程**：每图一个工作线程，串行跑拓扑（保证 Halcon 单实例安全）；并行分支按 `SemaphoreSlim` 控制并发度。
- **I/O 线程**：通讯层全异步，`ConfigureAwait(false)`，绝不占用 Execution 线程。
- 图像显示：节点产生 `HImage` 快照 → 转 Bitmap（句柄）→ `Dispatcher` 投递绘制，HObject 不进任何 `INotifyPropertyChanged`。

### 5.7 调试能力
- 单步（Step Over）、断点节点（到点挂起等 UI 恢复指令）。
- 每节点耗时统计与总周期时间（基准: Cycle 耗时基线）。
- **中间快照缓存**：每节点环形缓冲（如 32 帧）存 `Roi/参数/图像句柄`，调参全靠它，出作用域或覆盖时 Dispose。
- "**从选中节点重跑**"：重建作用域，仅执行该节点及下游，供快速迭代。
- 离线重放：用 Signal 令牌按时间线回放整轮执行。

### 5.8 序列化与版本兼容
- 图结构存 JSON；节点用**契约名 + 版本号**，绝不用类名。
- 参数抽离为 **Recipe** 独立文件；拓扑文件只含结构 + 节点引用 Recipe 中的参数。
- 保存前校验：成环 / 孤立分支 / 端口未连接 / 契约缺失 → 阻止保存；执行前静态检查（类型、依赖服务可解析）。

**图文件 schema 草案**
```json
{
  "schema": "vision.workflow/graph",
  "schemaVersion": 1,
  "trigger": { "source": "tag", "tag": "line1/go", "debounceMs": 10 },
  "nodes": [
    { "id": "n1", "contract": { "ns": "vision.threshold", "version": 1 },
      "pos": { "x": 120, "y": 80 }, "recipeId": "r1" }
  ],
  "links": [
    { "from": { "node": "n1", "port": "out:image" },
      "to":   { "node": "n2", "port": "in:image" } }
  ]
}
```

**Recipe 文件 schema 草案**
```json
{
  "schema": "vision.workflow/recipe",
  "version": 1,
  "params": { "r1": { "minGray": 128, "maxGray": 255 } }
}
```

---

## 6. Halcon 封装层

### 6.1 三种集成方式取舍

| 方式 | 适用 | 建议 |
|---|---|---|
| 直接调用 `halcondotnet.dll` 算子 | 性能最优、可控性最强 | **节点库主体采用** |
| HDevelop 导出 C# | 快速迁移、原型 | 仅作一次性脚手架 |
| HDevEngine 加载 `.hdev` | 算法频繁变更、算法/软件分工 | 提供一个"HDevProcedure 通用节点" |

### 6.2 落地要点
- 每个视觉算子/工具封装成 `IVisionNode`：`HObject in → HObject/结果 out`；参数 = **强类型 ViewModel + 特性标注**，反射自动生成参数面板。
- **HObject 生命周期**：节点执行完立即 `Dispose`；`IScope` 登记所有分配（`HObject`/`HRegion`/`HXLD`...），出作用域统一释放（保证遗漏不泄漏）。
- **HObject 池**：高频图像对象（采集帧）走对象池复用，Store/Dispose 配对；池 MaxPerNode 可配（防峰值爆内存）。
- **线程安全**：Halcon 实例非线程安全。默认单 Execution 线程串行；并行分支需要每引擎实例隔离（`HalconEnginePool`：按引擎实例借出/归还，并行度 = 引擎数）。
- 显示用 `HSmartWindowControl`，跨线程一律回 UI 线程绘制。

### 6.3 原生计算内核（C++ 混合）——体素/点云热点

**定位**：只承接"纯计算热点"，不替代 Halcon 封装。Halcon 赢在算子封装/授权/开发效率，原生赢在 SIMD/多线程/内存布局可控。当前明确的场景是 **3D 点云/体素**（Halcon `voxel_*`/`points_*` 系列在大点数上偏慢）。

**热点清单（首期）**
- 体素栅格下采样（Voxel Grid Downsample，点数 >100 万）
- 表面法线估计、平面拟合 / RANSAC
- ICP / 位姿精配准
- 体积 / 包围盒 / 高度图统计

**跨语言方式：P/Invoke + C ABI（已决策）**

| 方式 | 取舍 |
|---|---|
| P/Invoke + C ABI | 边界清晰、稳定 ABI、可 AOT、插件可换——**已采纳，锁定** |
| C++/CLI | 仅当需要直接钻进 `HObjectModel3D` 内部；异构建折腾，**排除** |
| 独立 native 进程 + IPC | 最重，仅服务化场景再议，**当前不启用** |

**Native ABI 契约草案（`vx_*.dll`，导出按版本号管理）**
```cpp
extern "C" {
  struct VoxelParams { double leafX, leafY, leafZ; };
  // xyz 为 Pin 住的 float 缓冲；out 为复用缓冲池，cap 前置检查
  int vx_voxel_downsample(const float* xyz, size_t n,
      const VoxelParams* p, float* out, size_t* outN, size_t cap,
      volatile int* cancel);              // 协作式取消轮询
  const char* vx_last_error(int handle);  // 错误直读，不抛异常过 ABI
}
```

**数据通道（低拷贝优先）**
- `HObjectModel3D` → `GetObjectModel3dParam("point_coord_*")` 得顶点数组。
- .NET 侧 `GCHandle.Alloc(Pinned)` / `MemoryMarshal` 固定缓冲直传，零中转拷贝。
- 输出缓冲走**复用对象池**（每引擎一份），避免每帧 GC + 反复 `AllocHGlobal`。
- 大点数先做粗体素抽稀再精算，两级流水。

**线程与取消**
- native 调用 = CPU 密集同步调用，跑在 Execution 线程，与 Halcon 串行语义一致。
- native 内部 TBB/OpenMP 自控并行，不依赖调用线程。
- 协作式取消：计算分块，每块轮询 `volatile int* cancel`，被取消即返错误码（Halcon 自身算子不可取消，native 路径补上这个短板）。

**装载与 ALC 冲突（关键坑）**
- native DLL 一旦 `LoadLibrary` 后**无法随 ALC 卸载**：原生库必须**进程级装载一次**，插件只持 managed proxy（P/Invoke 封装）；升级算法 DLL 走进程重启或热替换双缓冲，绝不挂进 `/plugins` 的 ALC。
- 部署：x64、VC++ Runtime、`vx_*.dll` 版本与 proxy 校验，CI 装箱验证。
- **软件回退**：原生 DLL 缺失时自动回退到 Halcon 3D 算子/HDevProcedure 节点，仅慢不快错，并亮提示。

---

### 6.4 OpenCV 接入（与 Halcon 并行，不归并）

定位：**Halcon 是视觉主芯，OpenCV 是生态补齐**，两者在图中"接龙"合作，不给 Halcon 挖坑也不让 OpenCV 喧宾夺主（ADR-013）。

接入形态（三件，各独立）
1. **引擎池**：`OpenCvEnginePool`（+ `OpenCvEngineSession`），与 `HalconEnginePool`（§6.3）同协议：借出-归还-取消；OpenCV 算子同样 non-threadsafe，Execution 线程一次只占一个视觉引擎（§5.4 §5.6 合并执行）。
2. **桥节点**：`ToOpenCV` / `FromHalcon`  桥接 `HObject ↔ Mat`（零拷贝内存桥：`HObject → Mat` 尽量重映射 buffer，`Mat → HObject` 走 `gen_image1_external` 承接）。桥的存在让 Halcon 节点可**直连** OpenCV 节点（Mat 端口 ↔ HObject 端口自动转换，视觉链"接龙"无障碍）。
3. **算子节点库 `Nodes.OpenCV`**（插件）：按需封装常用算子（滤波/形态学/轮廓/Hough/透射等）作为一等公民节点，与 Halcon 节点同服调度、同 Undo/审计、同 Recipe（§4.8 §9.1）。
   - **DNN/Onnx 推理**：独立 `OpenVINO/OnnxRuntime` 引擎实例进 `OpenCvEnginePool`，推理可取消（§6.3 同语义）；模型文件与 Recipe 共存扔/预装载策略同 §6.2。

选型与部署（呼应 §3）
- .NET 侧首选 **OpenCvSharp**（`OpenCvSharp4`，x64、MIT，活跃维护、可脱离外部 UI 直用算子）。
- native 侧若需专用算子/性能热点（如 YOLO 前处理、大图重采样）走 §6.3 的 **原生计算内核 C ABI**（`vx_*.dll`，进程级装载一次，与 Halcon native 同规则）。
- 部署：`opencv_world*.dll` 版本与 OpenCvSharp 包版本必须锁定一致（dll ABI 破坏即崩，同 §6.2 dll 校验铁律）；CI 装箱验证 x64。

边界（避坑清单呼应 §11）
- **不抢 Halcon 主芯**：Hough/亚像素/测量等 Halcon 强项保持 Halcon 实现，OpenCV 不平行重复；OpenCV 只补 Halcon 生态外（DNN/Onnx、部分开源算子、生态桥接）。
- **不双写状态**：桥节点只做数据转换不维护业务状态；两引擎实例换入换出通过池统一管理，避免"OpenCV Mat 在 Halcon 栈上裸奔"。
- **取消与线程**：OpenCV DNN 推理长任务同样可取消、可重入（图内可多路并发 DNN），一律回 **执行快照 → 取消 → 释放** 循环。

### 6.5 视觉节点行的 recipe/参数同步
- 视觉节点的 Recipe 参数走 §9.2 命令栈提交；`ITypeDescriptor` 保证 `Mat` 与 `HObject` 两个数据端口兼容（桥节点内部完成互转、对外同一类型语义）。
- 视觉节点统一在单执行栈上串行（§5.6）；Halcon 与 OpenCV 引擎实例各自隔离（§5.4 `HalconEnginePool` 类比，OpenCV 用 `OpenCvEnginePool`）。

### 6.6 线程与取消
- Halcon 与 OpenCV 均 non-threadsafe：`HalconEnginePool` / `OpenCvEnginePool` 按引擎实例借出,Execution 线程一次只占一个视觉引擎（§5.4 §5.6 §4.12 合并执行）。OpenCV 节点与 Halcon 节点在图内可"接龙"（Mat↔HObject 桥节点处置互转、渲染回 Halcon 快照）。

## 7. 通讯层：协议无关 Tag + 适配器插件

不抽象成 `ReadCoil/ReadDB` 这类协议专属方法，而是**点表 + 读写原语**：

```csharp
public interface IDeviceConnection : IAsyncDisposable {
    string ProtocolId { get; }
    string DeviceId { get; }
    ConnectionState State { get; }
    Task ConnectAsync(CancellationToken ct);
    Task<object> ReadAsync(string tag, CancellationToken ct);
    Task WriteAsync(string tag, object value, CancellationToken ct);
    IObservable<TagValue> Subscribe(string tags);   // 变化即触发图执行
}
```

### 7.1 Tag 寻址规范
- Tag 字符串语法：`device/area/name`（如 `plc1/db10/startBtn`）。`area` 各协议自行解释（DB 号 / 寄存器区 / 命名空间），适配器内翻译成本协议地址。
- **Tag Table 独立于图**：每个 Tag 登记 `{ 设备, 协议地址, 数据类型, 读写权限, 缩放/别名, 生效画布 }`，便于换型不改图。
- 订阅语义：适配器维护本地点表缓存 + 变化检测，只推送**变化**事件（含 `Quality`）。

```csharp
public enum Quality { Bad = 0, Uncertain, Good, Stale }
public readonly record struct TagValue(
    string Tag, object? Value, DateTimeOffset Timestamp, Quality Quality);
```

### 7.2 连接状态机
```
Disconnected ⇄ Connecting ⇄ Connected ⇄ Reconnecting
                    ↑________Heartbeat 超时________↓
```
- 心跳（可配周期）+ 自动重连（Polly 指数退避 + 熔断）。
- **同一连接读写串行化**：每个连接一个 `SemaphoreSlim(1,1)` 网关，防串包。
- 读写超时（默认如 1000ms）单独可配；粘包/半包由各协议解码器负责。

### 7.3 适配器选型
- Modbus TCP/RTU → NModbus；西门子 S7 → S7NetPlus（异步、性能优）；OPC UA → OPCFoundation UA-.NETStandard；三菱 MC / 欧姆龙 FINS / EtherNet/IP / BACnet → 开源实现按需接入；MQTT → MQTTnet；裸 TCP/UDP、串口 → 自写。

### 7.4 通讯即一等公民
通讯做成**图里的节点**：`CommWait(等待信号)`、`CommRead(读值)`、`CommWrite(写结果)`、`TagTrigger(事件触发)`。订阅事件经 EventBus → 调度器触发队列，触发即执行，不硬编码进流程。

### 7.5 运动控制板卡扩展（预留形态）

运动板卡与通讯设备同构：SDK 均为 Windows 原生 DLL（C 接口），资源是"**板卡句柄 + 轴号 + IO 端口**"，托管侧无对象生命周期泄漏问题。设计上复用三件套：**驱动插件 + 抽象接口 + 图内节点**，不单独立一套体系。

**抽象接口（语义化动作，不是读写点）**
```csharp
public interface IMotionController : IAsyncDisposable {
    string VendorId { get; }                    // "googol" / "zmotion" / "leadshine"
    int CardNo { get; }                         // 多卡索引
    Task HomeAsync(int axis, CancellationToken ct);
    Task MoveAbsoluteAsync(int axis, double pos, double vel, double acc, CancellationToken ct);
    Task MoveRelativeAsync(int axis, double dist, double vel, CancellationToken ct);
    Task MoveLineAsync(int[] axes, double[] dest, double vel, CancellationToken ct); // 单命令联动插补
    Task WaitInPositionAsync(int axis, double tol, CancellationToken ct); // 硬/软到位可配
    Task StopAsync(StopMode mode);              // Decel / Abort / EStop
    Task<double> ReadPositionAsync(int axis);
    Task SetDigitalOutAsync(int port, bool on);
    IObservable<AxisState> Watch(int axis);     // 状态变化 → 图触发
}
```
注意：**`IMotionController` 与 `IDeviceConnection`/`ITagTable` 并存不归并**——运动是"语义动作"，通讯是"位/寄存器读写"，混在一起会让两边接口都失血。

**图内节点（运动即一等公民，与通讯同级）**
- `Home / MoveAbs / MoveRel / MoveLine / WaitInPos / SetMotionIO / MoveDone事件`。
- **到位触发精度路由**：µs 级用板卡硬件输出触发相机；ms 级用 `WaitInPos → 触发节点` 软链路；两种都暴露，默认硬触发。

**线程与优先级**
- 运动命令 = 异步提交 + 变长等待：Move* 仅在板卡接受命令时返回；`WaitInPos` 协作式轮询 + 可取消（同 §6.3 语义）。
- **EStop 快通道**：独立于调度器普通触发队列，`EventBus` 上的急停通道直通所有卡 `AbortAsync`——运动优先级永远高于图执行。
- 长移动不挂死 Execution 线程：移动期间可让出给其他分支，`WaitInPos` 主动释放。

**关键正确性坑**
- 多轴联动**必须单命令提交**（`MoveLineAsync` 走板卡插补 API），禁止拆成多次单轴 Move 拼凑——这是运动控制头号错误。
- 部分老 SDK 非线程安全：每卡一把命令锁串行化（同 §7.2 单连接串行化）。
- native 装载/升级：随 ADR-006 规则——驱动 DLL 进程级装载一次，不挂 `/plugins` ALC。

**部署**
- 厂商驱动 + DLL 版本 + x64/x86 与宿主一致；老 x86 卡片会限制主进程位数，装箱验证。
- 验收首例：固高或正运动一张卡跑通"Home → MoveAbs → 到位硬触发采集 → 视觉判定 → 结果回写 PLC"闭环。

---

## 8. 数据存储与追溯

### 8.1 定位
数据库是**第三类能力**：既不是协议设备（不归 `IDeviceConnection`/Tag），也不是计算热点。它走"**异步批量 I/O + 查询**"时间模型，由能力层适配器承载，**图引擎内核依旧零 DB 依赖**。

| 用途 | 时间路径 | 承载 |
|---|---|---|
| 追溯记录（每周期 OK/NG/测量值） | 高频异步批量 | `IRecordStore`（热路径） |
| 查询/报表/趋势 | 低频，UI/运维侧 | `IQueryStore` |
| 图内数据查询（配方/参考值/上料参数） | 异步 + 可取消 | `Nodes.Data` 节点 |

### 8.2 抽象接口（能力层，不落具体提供商）

```csharp
public interface IRecordStore : IAsyncDisposable {
    Task AppendAsync(IRecord record, CancellationToken ct);  // 只入队，立即返回
}
public interface IQueryStore : IAsyncDisposable {
    Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? p, CancellationToken ct);
    Task<int> ExecuteAsync(string sql, object? p, CancellationToken ct);  // 非查询
}
```
- 连接串/提供者来自**主机配置**（数据源名称），图只引用数据源名，配方里绑定。
- SQL 一律参数化（防注入）；连接池交给 ADO 提供者管理。

### 8.3 追溯热路径（高性能写入）
- Execution 线程只 `AppendAsync` 入 **Channel 队列**（无锁、立即返回），绝不阻塞周期。
- 后台 Writer 定时聚合（100ms / 500 条，先到为准）单事务批量插入。
- **溢出保护**：队列上限（如 10 万条）→ 丢最旧 + 告警，不反压 Execution。
- 批失败：Polly 有限重试后弃批并记错误，当前周期不受影响。
- **图像不塞 BLOB**：快照存盘 + DB 记相对路径，事务保持精简。

### 8.4 追溯 schema 草案
```sql
CREATE TABLE cycle_records (
  seq         INTEGER PRIMARY KEY AUTOINCREMENT,
  trigger_id  TEXT NOT NULL,            -- Signal 令牌（§5.1）
  batch       TEXT,                     -- 批次号
  node        TEXT,                     -- 契约名:实例Id
  kind        TEXT,                     -- ok / ng / measure / trace
  result_json TEXT,                     -- 结果快照（HObject 转元数据）
  image_ref   TEXT,                     -- 快照文件相对路径
  ts          TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);
CREATE INDEX ix_cycle_batch   ON cycle_records(batch, ts);
CREATE INDEX ix_cycle_trigger ON cycle_records(trigger_id);
-- 现场大流量按期分段：周表 / 月归档（运维配置）
-- schema 版本化：schema_version 表 + 嵌入式 SQL 脚本迁移（类比 §11 IMigrator）
```

### 8.5 DB 节点（一等公民）
- `DbWrite(记录结果)`：把当前结果/测量值写入追溯（绑定 `IRecordStore`，入队即完成）。
- `DbQuery(条件查询)`：读参考值/配方/上料参数 → Number/String/Result 供下游分支。
- `DbTrigger(数据变化触发)`（预留）：轮询/变更通知作触发源 → 触发图执行。

### 8.6 选型与部署
- **SQLite**（本地零配置）：工程元数据、追溯回退、离线看板。
- **SQL Server / MySQL / PostgreSQL**（现场 MES/上位）：热路径用 **Dapper**（ADO 直连、无跟踪开销）；EF Core 仅作管理/报表工具（可选）。
- 全部异步；部署装箱验证：目标机驱动/运行时/TLS 证书齐备。

---

## 9. 应用层：操作记录与撤回 · 权限 · 追溯

### 9.1 操作信息记录（业务审计，区别于技术日志）
- 记录"**谁、何时、做了什么**"：载入/新建/保存工程、增删节点、连线、参数修改、Recipe 切换、执行/停止/单步、登录与权限变更等。
- 与 Serilog 分离：Serilog 是技术日志（堆栈/错误），审计是**结构化业务操作**，独立建表，可筛选导出。

```csharp
public readonly record struct OperationRecord(
    long Id, DateTimeOffset At, string User, string Action,   // 动作码 Contract.Action
    string? Target,                                          // 工程/节点/配方引用
    string? Before, string? After,                           // 变更前后快照(JSON)
    long? UndoRecordId);                                     // 被某次撤回时回链标记
```
- 存储复用 `Storage` 适配器（§8）；审计表独立于周期追溯表；轮转 + 归档（周表）。
- UI：审计视图（按用户/时间/对象类型筛选，导出 CSV）。

### 9.2 撤回 / 撤销重做（全局 UndoService）
- **中央命令栈**：`IUndoService` 从画布（§4.3）升格为全局，覆盖节点编辑、连线、参数修改、Recipe 字段、Tag 表/设备配置。
- 命令契约：`IUndoableCommand { Task DoAsync(); Task UndoAsync(); Task RedoAsync(); }`；支持**组合命令**（一次"粘贴一簇节点"= 一步撤销）与命令组事务。
- **两种实现策略**：参数类大改走"变更前后 JSON 快照"（稳健容错）；结构编辑走命令重放（细粒度、可跨步合并）。
- **一致性铁律**：内核 `GraphModel` 的每次变更都经命令提交——UI 撤销 == 引擎回滚（呼应 §4.3）；执行期间禁止结构编辑，排队等待周期结束后应用。
- **栈策略**：上限（如 200 步）丢最旧；跨工程切换清空；提供"撤回到保存点"。
- 每个操作既落审计（§9.1），撤回时写回 `UndoRecordId` 标记。

### 9.3 权限
- 角色：只读 / 操作员 / 工程师 / 管理员；界面元素按角色禁用；审计记录放行与**拒绝**（含失败的权限尝试）。

### 9.4 追溯
- 周期追溯见 §8；本层提供查询/看板/导出入口，与 `cycle_records` 对接。

### 9.5 算子运行结果预览 · 存图（原图/渲染图）· CSV 导出 · 良率/产量统计
- 本节四个能力共享同一事实（§8 周期表 + §9.1 审计 + §9.4 看板），**不重造第二份数据**（ADR-014）：预览取快照，存图走归档，统计聚合周期表，导出即查询落地——全部在宿主/Storage 侧，图内核零依赖。

#### 9.5.1 运行结果预览（不重跑算子）
- Execution 每周期结束后，对图上命中"**预览端口**"的节点**旁路**产出快照：HObject / Mat → JPG/PNG（或内存 Mat，走 §4.5 图像窗控件复用渲染），存 **近 N 周期 ring**（如最近 200 个）。
- 预览是**副产物**，绝不阻塞 Execution（§5.6）：快照由周期完成信号触发、在宿主线程池异步压缩/出图；无头宿主可整体关闭预览（省内存）。
- 旧结果随周期覆盖；单节点"只看该节点最后一次输出"，配合 §5.6 结果缓存不会与执行态扯皮。

#### 9.5.2 CSV 导出
- 导出入口覆盖三处：追溯查询结果（§9.4）、良率/产量统计（§9.5.4）、操作审计（§9.1）——统一 `IExportService`（宿主服务）。
- **编码**：默认 **UTF-8 带 BOM**（Excel 双击即开不乱码）；现场可选手动 GBK（老机器/老 Excel）。
- 大表**流式分页 + 可取消**：按页查（`Storage` §8 异步查询）+ 逐行写文件，不整表进内存；导出动作本身落审计（§9.1"谁导出了谁"）。

#### 9.5.3 存图：原图 / 渲染图
- **原图** = 采集原始帧（Halcon 采集节点输出）；**渲染图** = 视觉算子叠加结果后的成品图（Halcon 画框/叠字，或 OpenCV 渲染，§6 桥）。
- 存图策略（Recipe 维度可配）：完整存 / 按频率抽存 / 仅 NG 存 / 现场关闭；落 `trace_images`（Storage §8，BLOB 或文件归档路径二选一），与 `cycle_records` 外键回链，追溯看板"点周期 → 看三图（原图/渲染图/良率卡）"。
- 图像文件复用 §8 归档轮转/剪枝规则；SQL Server/MySQL 用 varbinary/blob、SQLite 用 BLOB（§8 已含 BLOB 决策）。

#### 9.5.4 良率 / 产量统计
- 基于 §8 周期表**聚合成统计**：产量 = 周期完成数；良率 = OK÷已判定 ×100%；NG 分分类（按 Recipe/Recipe 判定基础码）。
- 维度：按 线别 / 机台 / 班次 / 机型 / Recipe / 日期 分片；`IStatsService` 提供异步查询（宿主侧，内核零依赖，复用 §8 Storage 异步规则）。
- **良率看板**：ScottPlot 折线/柱（产量/良率/NG TOP），刷新周期可配；复用 §4.6 看板组件；统计视图同样**随时 CSV 导出**（§9.5.2）。

---

## 10. 项目骨架与插件装载

```
/src
  Core/            # 图引擎内核：Node/Port/Graph/Scheduler/Serialization（零依赖）
  Nodes.Vision/    # Halcon 节点库（插件）
  Nodes.Comm/      # 通讯节点库（插件）
  Nodes.Motion/    # 运动控制节点库（插件）
  Nodes.Data/      # 数据库节点库（插件）：DbWrite / DbQuery / DbTrigger
  Nodes.Flow/      # 分支/循环/延时/脚本 等流程节点
  Abstractions/    # IDeviceConnection, IVisionNode, ITagTable, IMotionController, IDataStore, IEventBus …
  Protocols/       # Modbus / S7 / OpcUa / Mc / Fins / Mqtt / Tcp / Serial
  MotionDrivers/   # 固高 / 正运动 / 雷赛 / 凌华 等运动卡驱动插件
  Storage/         # 数据访问适配器：Sqlite / SqlServer / MySql / Postgres（Dapper）
  App/             # WPF Shell、面板、主题
  Runtime/         # 无头执行宿主（生产模式/服务化）
  Core.Tests/      # 内核单测（调度/校验/序列化/迁移链）
/plugins           # 外部节点与协议插件目录
```

- **插件装载**：DI + 目录扫描 + `AssemblyLoadContext`（每插件独立 ALC，可卸载）。约定每个插件输出一个 `IServiceRegistrar { void Register(...); }`，宿主扫描装载后回调注入容器。
- **契约注册**：插件声明 `NodeContract { ns, version } → Type` 映射，语义版本（`major.minor`）兼容 `major` 相同即可加载。
- **离线可部署**：`/plugins` 与 `/projects` 解耦，换节点 DLL 不弹框不重启（ALC 卸载旧、装载新）。

---

## 11. 避坑清单（按踩中概率排序）

| # | 坑 | 对策 |
|---|---|---|
| 1 | **HObject 泄漏**，跑一夜内存爆掉 | 出作用域统一 Dispose + 对象池 + IScope 登记 |
| 2 | **工程文件版本兼容** | 节点契约版本号 + IMigrator 迁移链 + Recipe 分离 |
| 3 | **UI 线程被图执行阻塞** | 严格分线程；长耗时节点必须可取消（CancellationToken 冒泡） |
| 4 | **协议并发读写串包** | 每连接 SemaphoreSlim(1,1) 串行化 |
| 5 | **Halcon 部署** | x64、Runtime license、dll 版本与授权必须匹配（装箱即验证） |
| 6 | **节点图成环/孤立分支** | 保存前校验、执行前静态检查 |
| 7 | Halcon 实例并发不安全 | 单 Execution 线程串行 / HalconEnginePool 显式隔离 |
| 8 | `HObject` 进绑定导致 UI 卡死 | 句柄传递，快照转 Bitmap 后由 Dispatcher 绘制 |

---

## 12. 版本迁移机制（提前设计，否则升级即灾难）

- **节点契约**：`{ ns: "vision.threshold", version: 1 }`；图 JSON 存契约名而非类型全名。
- **迁移器链**：`IMigrator { string ContractNs; int FromVersion; void Migrate(JsonNode node, Recipe recipe); }`，按版本逐个升级，类似 EF 迁移；打开老工程必走迁移链并提示已升级，落盘前二次确认。
- **Recipe 分离**：拓扑（结构）与参数（Recipe）分文件存；迁移只动结构，Recipe 单独版本独立迁移。
- 交付约定：节点库发新版本，必须随包交付 N-1 → N 的 `IMigrator`，否则版本号不允提升。
- 契约缺失（找不到插件）：**挂起节点**而非丢弃，待装载对应插件版本后可恢复。

---

## 13. 落地里程碑（后续实现顺序）

> 说明：里程碑按迭代走完图引擎零依赖主干 + 无头宿主 + 最小画布，先让"现场闭环验证"最早开始；并行分支等能力逐批补进，不阻塞验证。

1. `Core` + `Abstractions`：节点/端口/类型系统/调度器/序列化 + 单测（无 UI）。
2. `Runtime`：无头执行宿主（控制台启动、从 JSON 载图执行、结果输出）。
3. `App`：WPF + Nodify 最小画布（载图、编辑、保存、执行/停止、日志窗）。
4. `Nodes.Flow`：分支/循环/延时/Join。
5. `Nodes.Vision` + Halcon 封装：定位/测量主体算子 + HDevProcedure 通用节点 + 参数反射面板。
6. `Protocols` + `Nodes.Comm`：Modbus TCP 先行，S7 / OPC UA 跟进；Tag Table 与设备管理 UI。
7. `MotionDrivers` + `Nodes.Motion`：以固高/正运动为首验证 `IMotionController` 与到位触发闭环。
8. 调试能力：单步/断点/耗时统计/快照缓存/从选中节点重跑/离线重放。
9. `Storage` + `Nodes.Data`：SQLite/SqlServer 适配 + 批量写入队列 + 追溯记录节点 + 追溯看板。
10. 原生计算内核：`vx_*.dll` C ABI 骨架 + 体素下采样/法线估计 managed proxy + 回退路径。
11. 应用层：工程管理、Recipe、撤销重做、权限、追溯。

---

### 13.1 阶段门（Stage Gate）——阶段式要求与验收标准

> 场景定了：现场落地不是"一口气写完全部"，而是**一阶段一闸门**。只有本阶段验收标准**全绿**才开下一阶段（ADR-015）。验收标准一律**可测判定**（单测 / CI / 装箱 / 现场试点 / 性能预算），不接受"看着行"。

| 阶段 | 入口（上一闸门） | 阶段要求（交付物） | 验收标准（可测） | 出口闸门 → |
|---|---|---|---|---|
| 1 图引擎内核 `Core`+`Abstractions` | —（起点） | 节点/端口/类型系统/调度器/序列化；契约名+版本+Recipe；**双语注释**（§4.9） | **零依赖断言**（Core 不引用 WPF/Halcon/通讯）+ 序列化往返幂等单测全绿 + 无头宿主可载图 | → 2 |
| 2 无头运行时 `Runtime` | 1 | Execution 调度器无头执行；JSON 载图 → 结果；可停止 / 单步 / 取消 | 解析 / 执行 / 停止 / 取消 单测 + 无头冒烟；追溯字段 §8 原型就位 | → 3 |
| 3 最小画布 `App`(Nodify) | 2 | 画布载图 / 编辑 / 保存 / 运行 / 日志；**绑定只指 VM 投影，禁止内核实体**（§4.3） | VM 不引用内核断言 + 本地化（中英韩）热切换冒烟 + 现场旧图载入不崩 | → 4 |
| 4 `Nodes.Flow` | 3 | 分支 / 循环 / 延时 / 脚本节点，图内一等公民 | 流程节点单测 + 与内核同 Undo（§9.2）冒烟 | → 5 |
| 5 视觉节点行 `Nodes.Vision`(Halcon) | 4 | Halcon 算子直调为主 + HDev 通用脚本；**HObject↔Mat 桥**（§6.4）；Recipe 参数面板 | `HalconEnginePool` 借出 / 归还 / 取消断言 + 桥节点 HObject↔Mat 往返字节一致 | → 6 |
| 6 通讯层 `Protocols`+`Nodes.Comm` | 5 | Tag 寻址 + 读写原语 + 适配器插件（Modbus 首批，S7/OpcUa 跟进）；通讯节点一等公民 | 协议无关 Tag 表 I/O 单测 + **换协议不动图**断言（ADR-004） | → 7 |
| 7 运动控制 `MotionDrivers`+`Nodes.Motion` | 6 | `IMotionController` + 运动卡插件（固高 / 正运动 / 雷赛 / 凌华）+ 到位触发闭环 | P/Invoke + ALC 装载规则单测 + native 缺失**软件回退**冒烟（§6.3） | → 8 |
| 8 数据存储 `Storage`+`Nodes.Data` | 7 | Storage 适配器（Dapper + SQLite/SqlServer/MySql/Pg）+ 异步批量写入队列；追溯周期表 + **CSV 导出**（§9.5.2） | 批量写不阻塞 Execution 断言 + 追溯查询可取消 + CSV 流式分页可取消冒烟 | → 9 |
| 9 追溯看板 · 存图 · 良率/产量统计 | 8 | 看板（§8 §9.1 §9.4）；良率 / 产量统计（§9.5.4）；原图 / 渲染图存图（§9.5.3）；结果预览（§9.5.1） | 看板与周期表 / 追溯**同源不另建第二份**断言 + 存图入 `trace_images` 归档冒烟 | → 10 |
| 10 原生计算内核 P/Invoke + C ABI | 9 | native `vx_*.dll` + 体素 / 点云计算，可取消；native DLL 进程级装载一次（不随 ALC） | native 装载单测 + 取消热路径断言 + 与 `HalconEnginePool` 同语义（§6.3） | → 11 |
| 11 应用层（操作记录与撤回 · 权限 · 追溯 · 预览 / 存图 / CSV / 统计收口） | 10 | §9 全层 + §9.5 四能力 + 本地化中英韩全套 | 全局 `UndoService` 覆盖全应用断言 + 权限 / 审计拒绝落表 + 双语注释抽查 + 三语资源回退链 | → 发布就绪（现场试点循环） |

- **闸门铁律**：上一阶段未全绿，不硬化后续阶段；验收只看可测判定，现场试点循环为末级确认。
- **与里程碑的关系**：§13 上部 1~11 是**顺序**，本表把每一步翻译成**阶段 + 验收门**；里程碑编号不动、ADR-014（预览/存图/CSV/统计）与 ADR-015（阶段门）在此收口。

---

## 14. 待定 / 开放问题（实现前需拍板）

- 并行分支的 Halcon 引擎隔离粒度默认策略（`HalconEnginePool` 大小与相机/引擎绑定关系）。
- Tag 订阅 → 图触发的背压与去抖参数（窗口/上限是否随触发源差异化）。
- 无头宿主与 WPF Shell 是否共享同一调度器（进程内切换 vs 独立进程 IPC，涉及现场"在线预览 vs 生产无头"切换）。
- 脚本节点首选语言（C# Script / Python / HDevelop）及沙箱边界。
- 快照缓存与 HObject 池的内存上限阈值（按物理内存比例审计）。
- native 取消粒度：分块大小如何兼顾"取消响应"与"分块开销"。
- EStop 快通道打断后，图中断节点（WaitInPos/Move 挂起中）的恢复语义与现场复位流程。
- 运动到位触发：硬件触发与软触发链路的默认路由与可配置粒度。

---

## 15. 决策记录（ADR）

| ID | 决策 | 状态 | 理由摘要 |
|---|---|---|---|
| ADR-001 | UI 用 WPF + MVVM + Nodify，不用 WinForms | ✅ 已定 | 画布/MVVM/DPI 全优，WinForms 自绘画布与手写绑定成本不可接受 |
| ADR-002 | 图引擎内核零依赖（不许引用 WPF/Halcon/通讯库） | ✅ 已定 | 无头运行/单测/CI 的前提 |
| ADR-003 | 节点序列化用契约名+版本号，参数抽离 Recipe | ✅ 已定 | 老工程兼容 + 迁移器链，升级不炸现场 |
| ADR-004 | 通讯抽象为 Tag 寻址 + 读写原语，通讯节点为一等公民 | ✅ 已定 | 协议无关 + 换型不动图 |
| ADR-005 | Halcon 以算子直调为主，HDevEngine 仅做通用脚本节点 | ✅ 已定 | 性能/可控性最优 |
| ADR-006 | 计算热点走 **P/Invoke + C ABI** 的原生计算内核（体素/点云） | ✅ 已定 | 边界清晰、稳定 ABI、可取消、native DLL 进程级装载一次（不随 ALC 卸载） |
| ADR-007 | 插件装载 = DI + 目录扫描 + AssemblyLoadContext | ✅ 已定 | 现场不重启换节点 |
| ADR-008 | 运动板卡 = 驱动插件 + `IMotionController` + 图内节点，与通讯层并存不归并 | ✅ 已定 | 语义动作 ≠ 位/寄存器读写；复用 P/Invoke + ALC 装载规则 |
| ADR-009 | 数据库 = `Storage` 适配器（Dapper + SQLite/SqlServer/MySql/Pg）+ 异步批量写入队列；DB 节点为一等公民 | ✅ 已定 | 追溯热路径不阻塞 Execution；图内查询异步可取消；内核零 DB 依赖 |
| ADR-010 | 多语言（中 / 英 / 韩）= 资源驱动 + 运行时热门切换 + 回退链；UI 与节点元数据同走语言包 | ✅ 已定 | 现场换语言不重启；三语资源缺一自动回退 |

| ADR-011 | 操作记录与撤回 = 全局 `UndoService` + 业务审计表；UI 撤回 == 引擎回滚（命令栈覆盖全应用） | ✅ 已定 | 一键回溯谁何时改了什么；撤回粒度与内核命令一致 |
| ADR-012 | 代码注释规范 = **双语注释**：代码注释英文在前、中文在后（与本地化 ADR-010 分离，互不重叠） | ✅ 已定 | 现场双语注释降低维护门槛；注释语言与运行时语言包无关 |
| ADR-013 | **OpenCV 并行接入**：OpenCvSharp（`OpenCvEnginePool`）+ `HObject↔Mat` 桥节点 + `Nodes.OpenCV` 节点库；与 Halcon 并行不归并、桥节点数据互转 | ✅ 已定 | 补 Halcon 生态外算子/DNN/Onnx 推理；视觉链"HObject↔Mat 接龙"无阻 |
| ADR-014 | **结果预览 · 存图 · CSV 导出 · 良率/产量统计** = 4 个 §9.5 子能力，全部基于 §8 周期表/追溯同一份事实：预览走快照池零重跑、存图入 `trace_images` 归档、CSV 走流式分页可取消（UTF-8 BOM/GBK 双编码）、统计为周期表聚合（良率看板） | ✅ 已定 | 预览不重跑算子、存图/统计/导出不另建第二份数据；追溯热路径零阻（§8.6 §9.5） |
| ADR-015 | **阶段闸门（Stage Gate）= §13.1 主闸表**：只有上一层闸门全绿才开下一阶段；备案（§9.3 权限）+ 验收 = 可测判定（单测/CI/冒烟/性能预算），拒绝"看着行" | ✅ 已定 | 阶段式要求与验收标准落地；空转不进场、带病不硬闯下一阶段 |