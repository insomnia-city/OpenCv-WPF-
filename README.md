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
- 全解决方案：**47 项单测全绿**（36 Core + 11 Runtime）

本地化（中/英/韩）与双语注释规范见 DESIGN §4.8 / §4.9。