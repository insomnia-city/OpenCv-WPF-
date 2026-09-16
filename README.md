# Halcon 可视化工作流

面向机器视觉现场的可视化节点编程平台：Halcon 视觉算子 + 多协议通讯 + 节点图编排，WPF 呈现。

## 架构总览

```
┌──────────────────────────────────────────────────────┐
│ 表现层 Shell      WPF + MVVM + Nodify 画布 / 属性面板 / 图像窗 / 日志窗 │
├──────────────────────────────────────────────────────┤
│ 应用层           工程管理 · 配方(Recipe) · 撤销重做 · 权限 · 追溯      │
├──────────────────────────────────────────────────────┤
│ 图引擎内核       GraphModel · 类型系统 · 调度器 · 序列化（零 UI 依赖） │
├──────────────────────────────────────────────────────┤
│ 能力层（插件）    视觉节点库(Halcon) │ 通讯节点库 │ 采集/IO │ 脚本节点 │
├──────────────────────────────────────────────────────┤
│ 基础设施         DI · 插件装载(MEF/ALC) · Serilog · 事件总线 · Polly   │
└──────────────────────────────────────────────────────┘
```

三段式核心：**图引擎内核（无 UI 依赖）+ 插件化节点库 + 协议无关通讯抽象层**。
三者必须分层，因为时间模型完全不同：节点图是"静态拓扑 + 事件驱动执行"、视觉算法是"同步阻塞计算"、通讯是"异步 I/O + 状态机"。

## 文档

- [docs/DESIGN.md](docs/DESIGN.md) — 完整设计（分层、表现层、图内核、Halcon 封装、通讯、数据库、操作记录与撤回/权限/追溯、骨架、避坑、版本迁移、里程碑、开放问题、ADR）
- `docs/` 下还将补充：迁移机制说明、节点契约版本约定、标签表(Tag Table)规范

## 目录（规划）

```
/src
  Core/            # 图引擎内核：Node/Port/Graph/Scheduler/Serialization（零依赖）
  Nodes.Vision/    # Halcon 节点库（插件）─ halcondotnet.dll 算子直调为主，HDevEngine 仅做通用脚本节点；含 HObject↔Mat 桥节点（§6.4）
  Nodes.OpenCV/    # OpenCV 节点库（插件）─ OpenCvSharp4（`OpenCvEnginePool`）+ HObject↔Mat 桥 + Onnx/DNN 推理（§6.4）
  Nodes.Comm/      # 通讯节点库（插件）
  Nodes.Motion/    # 运动控制节点库（插件）
  Nodes.Data/      # 数据库节点库（插件）
  Nodes.Flow/      # 分支/循环/延时/脚本 等流程节点
  Abstractions/    # IDeviceConnection, IVisionNode, ITagTable, IMotionController, IDataStore …
  Protocols/       # Modbus / S7 / OpcUa / Mc / Fins / Mqtt / Tcp / Serial
  MotionDrivers/   # 固高/正运动/雷赛/凌华 运动卡驱动插件
  Storage/         # Sqlite / SqlServer / MySql / Postgres 数据访问（Dapper）
  App/             # WPF Shell、面板、主题
  Runtime/         # 无头执行宿主（生产模式/服务化）
/plugins           # 外部节点与协议插件目录（独立 AssemblyLoadContext 装载）
```

## 技术栈

.NET 9 · CommunityToolkit.MVVM · Nodify · Serilog · ScottPlot · Polly · halcondotnet.dll · **OpenCvSharp4（OpenCV 并行）** · C++ 原生计算内核（体素/点云热点） · SQLite/SQL Server/MySQL (Dapper)

## 状态

当前落地进度：设计文档 v0.7（含 §9 操作记录与撤回 · 权限 · 追溯、**§9.5 算子结果预览 · 原图/渲染存图 · CSV 导出 · 良率/产量统计**、§6.3 原生计算内核、§6.4 OpenCV、§8 数据存储、ADR-005~014；§13.1 阶段式要求与验收标准（Stage Gate/ADR-015））。

**阶段 1（图引擎内核 Core + Abstractions）已落地**：
- `src/Core/` — 节点/端口/类型系统/调度器/序列化契约（零依赖，ADR-002 断言通过）
  - `Contracts/` — INode / IPort / ITypeDescriptor / IExecutionContext / IScope / IEventBus
  - `Model/` — NodeContract / NodeState / GraphState / Signal / TriggerSource
  - `Types/` — 描述符层级（Image⊂VisionObject，Integer⊂Number 等）
  - `Graph/` — GraphModel / GraphLink / GraphPort / 拓扑排序 / 成环检测 / 静态校验
  - `Execution/` — GraphScheduler（触发队列 丢旧保新+去抖 / 串行主干 / 可取消）/ ExecutionContext / Scope
  - `Serialization/` — 契约名 JSON 图序列化（挂起节点回退）+ Recipe 独立持久化
- `src/Abstractions/` — IDeviceConnection(+Tag) / IVisionNode / IMotionController / IRecordStore / IQueryStore / IStatsService / IAuditStore / IUndoService / 插件注册契约
- `src/Core.Tests/` — 36 项单测全绿

**阶段 2（无头运行时 Runtime）已落地**：
- `src/Runtime/` — 控制台无头宿主：JSON 载图 → 执行 → 结果输出，可停止/单步/取消
  - `HeadlessHost.cs` — 调度器驱动、取消、单步暂停、硬超时
  - `Nodes/` — 5 个内置示例节点（Start/Grabber/Threshold/Decision/LogResult），零插件即可跑通全图
  - `Trace/TraceWriter.cs` — 追溯 JSONL 输出，字段对齐 §8 cycle_records 原型（trigger_id / batch / node / kind / result_json / ts）
  - `RuntimeOptions.cs` — CLI 参数解析（--once/--cycles/--interval/--timeout/--trace/--step/--quiet/--batch）
- `src/Runtime.Tests/` — 11 项冒烟测试（解析/执行/停止/取消/追踪文件落盘）

**阶段 3（WPF 最小画布 App）已落地**：
- `src/App/` — WPF（net9.0-windows）+ MVVM（CommunityToolkit.Mvvm）+ Nodify 画布
  - `ViewModels/` — ShellViewModel（载图/保存/新建/运行/停止 + 节点库）/ MainEditorViewModel（节点与连线投影镜像）/ NodeViewModel / PortViewModel / ConnectionViewModel（端点移动自动重锚）/ LogViewModel
  - `Views/MainWindow.xaml` — 工具栏 + 节点库(200) + Nodify 画布(全屏缩放/拖移) + 日志面板(330) + 状态栏
  - `Controls/` — NodeTemplateSelector（起点/挂起/标准卡片路由）+ 配色转换器（契约族/执行状态/端口类别）
  - `Services/` — LocalizationService（zh-Hans/en/ko 热切换 + 英文缺键回退，ADR-010）/ IDialogService（VM 可测）
  - 节点位置双向同步内核 → 保存到契约名 JSON（`*.graph.json` schema §5.8）
- `src/App.Tests/` — 6 项验收：绑定面仅投影（不得泄漏内核实体 + 服务访问器白名单）/ 本地化热切换 / 编辑器序列化往返 / 连线移动重锚 / 增删节点 / 调色板生成
- 全解决方案：**53 项单测全绿**（36 Core + 11 Runtime + 6 App）
- 冒烟：`src/App/bin/Debug/net9.0-windows/HalconWorkflow.App.exe` 启动窗口正常

**阶段 4（流程节点 Nodes.Flow + §9.2 撤销）已落地**：
- `src/Nodes.Flow/` — 图内一等公民的流程节点（§13.1 第 182/592 行）：`flow.branch`（按条件二选一数据路由）、`flow.join`（双输入齐备后汇聚，缺一即故障）、`flow.counter`（批次内计数）、`flow.delay`、`flow.script`
  - `ScriptNode` — 安全递归下降求值器（无动态编译）：字面量/标识符(作用域 tag)/算术/比较/逻辑/三目/字符串拼接；输出可按 Integer/Real/String/Bool/Result 强制转换
  - `FlowNodeFactory` — 按契约名（ns+版本）反序列化；`IPort.IsRequired`（接口默认实现）让可选输入（如二选一路由）豁免悬空检查，未破坏既有节点
- `src/Abstractions/Undo/` — 具体 `UndoService`（撤销栈+重做+组合命令+上限 200+跨工程清空）与图编辑命令（AddNode/RemoveNode 含节点+坐标+连线快照回滚 / Connect/Disconnect）
- App 接线：调色板新增 5 个流程节点（中/英/韩本地化）、加/删/撤销/重做全部走全局命令栈、工具栏 Undo/Redo + Ctrl+Z/Ctrl+Y、`CombinedNodeFactory` 混合反序列化示例+流程节点、flow 契约族卡片配色
- `src/Nodes.Flow.Tests/` — 17 项：流程节点单测 + 求值器单测 + **§9.2 与内核同 Undo 冒烟**（加节点→撤销→内核回滚→重做→恢复；连线撤销；组合命令一次性撤销；栈上限 200）
  - `src/App.Tests/` 新增 1 项：壳层撤销/重做驱动内核 + 流程节点可生成
- 全解决方案：**71 项单测全绿**（36 Core + 11 Runtime + 6 App + 17 Nodes.Flow + 1 App 撤销验收 = 36+11+6+17+1）
- 冒烟：`HalconWorkflow.App.exe` 启动 6 秒存活

> 下一步（阶段 5）：交互式连线（Nodify PendingConnection 拖拽建线/断线）、属性面板、数据面板、运行期实时数据（scope 值/图像预览）。

本地化（中/英/韩）与双语注释规范见 DESIGN §4.8 / §4.9。