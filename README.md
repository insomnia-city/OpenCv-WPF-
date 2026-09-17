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

**阶段 5（视觉节点库 Nodes.Vision + 反射属性面板）已落地**：
- `src/Nodes.Vision/` — Halcon 节点库（插件，§6.1~6.4）：算子直调 + HDev 通用脚本 + **HObject↔Mat 桥**，全部走 `IVisionEnginePool`（借出/归还/并发上限/取消，闸门 1）
  - `Imaging/VisionFrame.cs` — 不可变帧 DTO（Gray8/Bgr8/Bgra8 + Halcon/Mat 域 + 缓冲长度校验）；`FrameBridge` — 零拷贝域翻转，HObject↔Mat 往返字节一致（闸门 2）
  - `Engines/` — `IVisionEngine` / `VisionEnginePool`（SemaphoreSlim 门闸+空闲队列）/ `PhantomVisionEngine`（纯 .NET 确定性软回退：grab/threshold/measure/hdev）/ `VisionEngineFactory.CreateResolved`（部署机 MVTec 探测，未接真实适配器前自动软回退）
  - `Nodes/` — 6 个节点：`vision.grab` / `vision.threshold`（**v2**，v1 保留给旧脚手架，§12 版本迁移链）/ `vision.measure` / `vision.hdev` / `vision.tomat` / `vision.tohobject`；`VisionNodeBase` 节点级借池租约
  - `Components/` — `NodeParameterAttribute` + `ParameterReflection`（§4.4 反射面板元数据：分组/范围/种类/单元，类型化写入+范围校验返回旧值）
  - `Commands/SetParameterCommand` — §9.2 可撤销参数写（Do/Undo/Redo 精确往返）；`IGraphEditCommand` 标记区隔结构/参数撤销
- `src/Nodes.Vision.Tests/` — 28 项：**池闸门**（复用/并发上限/取消不泄漏/等待中取消/工厂失败还槽）、**桥闸门**（3 像素格式往返字节一致）、视觉链路冒烟（grab→threshold→mask、grab→measure、hdev、tomat/tohobject 往返）、参数反射、撤销命令
- App 接线：调色板 grabber/threshold 换成真实 vision 节点并新增 measure/hdev/tomat/tohobject（中/英/韩本地化 + 契约族配色）；`CombinedNodeFactory` 混入 `VisionNodeFactory`（threshold 按版本分流）；调度器注册 `IVisionEnginePool`；右栏新增**属性面板**（选中节点 → `NodeParameter` 反射行，失焦提交经撤销服务，Undo/Redo 按命令分类重建/刷新）
- 全解决方案：**101 项单测全绿**（36 Core + 11 Runtime + 9 App + 17 Nodes.Flow + 28 Nodes.Vision）

**阶段 6（通讯层 Protocols + Nodes.Comm）已落地**：
- `src/Protocols/` — 协议层（引 Abstractions）：`TagTable`（线程安全 Tag 表，§7.1）+ `Modbus/`（**纯 .NET MBAP 组帧，无第三方 Modbus 栈**）
  - `Modbus/ModbusFrame.cs` — MBAP 组帧器 + `ModbusException` + 区/地址解析（`coil:N` / `holding:N` / `discrete:N` / `input:N`）
  - `Modbus/ModbusTcpConnection.cs` — 客户端适配器（实现 `IDeviceConnection`：`SemaphoreSlim` 请求串行化 + 取消 + `Subscribe` 轮询）
  - `Modbus/ModbusTcpSimulator.cs` — 进程内回环从站（真实 MBAP 帧服务线圈/离散输入/保持/输入寄存器；供测试与 App 演示）
- `src/Nodes.Comm/` — 通讯节点库（引 Core + Abstractions）：`ICommRuntime`/`CommRuntime`（按设备ID解析连接 + 共享 Tag 表，注册为调度器服务）
  - `Nodes/CommNodes.cs` — 一等公民通讯节点：`comm.read:1` / `comm.write:1`（Value 端口或静态值）/ `comm.wait:1`（轮询至期望值，可反相）；强类型参数对象经 `[NodeParameter]` 反射进属性面板
  - `Nodes/CommNodeFactory.cs` — 按契约名（ns+版本）反序列化
- 阶段闸门（§13.1 第 614 行）：**协议无关 Tag 表 I/O 单测**（TagTable 解析/登记/枚举 + Modbus 帧往返）+ **换协议不动图**断言（ADR-004：适配器替换后图 JSON 字节不变，同一 `comm.*` 契约跨适配器执行结果一致）
- App 接线：引 Nodes.Comm+Protocols；`ShellViewModel` 构建**演示回环 Modbus 设备**并注册 `ICommRuntime` 调度器服务（换真实 PLC 只改此处）；调色板新增 3 个通讯节点（中/英/韩本地化 + 配色）；`CombinedNodeFactory` 混入 `CommNodeFactory`；退出经 `IAsyncDisposable` 释放运行时与模拟器
- `src/Protocols.Tests/` — 11 项：Tag 表 5 + Modbus TCP 往返 5 + 匹配 1
- `src/Nodes.Comm.Tests/` — 9 项：工厂/参数反射 + **图内读写往返** + **换协议不动图**（2 项）+ 直接往返
- 全解决方案：**122 项单测全绿**（36 Core + 11 Runtime + 10 App + 17 Nodes.Flow + 28 Nodes.Vision + 11 Protocols + 9 Nodes.Comm）
- 冒烟：`HalconWorkflow.App.exe` 启动 6 秒存活（已注册回环 Modbus 设备）

**阶段 7（运动控制 MotionDrivers + Nodes.Motion）已落地**：
- `src/MotionDrivers/` — 运动驱动层（引 Abstractions）
  - `Native/NativeMotionLibrary.cs` — **进程级原生库解析器**（`NativeLoadScope.Process`，绝不挂 ALC；缺失返回 `IsMissing`+显式消息）
  - `Native/MotionAbi.cs` — `INativeMotionApi` 接缝 + `DllImportMotionApi`（gmotion C ABI）
  - `PhantomMotionController.cs` — 确定性纯 .NET **软件回退**（§6.3；CommandLog/StopLog/HoldInPosition 便于测试）
  - `NativeMotionController.cs` — 原生控制器（public 探测 ctor 缺失即抛 `NativeLibraryMissingException`；internal 注入接缝供测试）
  - `MotionDriverFactory.cs` — 厂商探测/创建（googol/zmotion/leadshine/adlink；缺失/未知→phantom 回退 + 提示）
- `src/Nodes.Motion/` — 运动节点库（引 Core + Abstractions）：`IMotionRuntime`/`MotionRuntime`（按名解析控制器，注册为调度器服务）
  - `Nodes/MotionNodes.cs` — 6 个节点：`motion.home:1` / `motion.moveAbs:1`（可选 "Pos" 输入）/ `motion.moveRel:1`（"Dist"）/ `motion.line:1`（多轴**单命令插补**，§7.5 头号正确性规则）/ `motion.waitInPos:1`（可取消/超时，发布 "Position"）/ `motion.dout:1`
  - `MotionUnits.cs` — 工程单位↔原生计数换算 + CSV 解析；`Nodes/MotionNodeBase.cs` — 基类 + 故障**减速停车回滚**
  - `Nodes/MotionNodeFactory.cs` — 按契约名（ns+版本）反序列化
- 阶段闸门（§13.1）：**P/Invoke + 进程级装载规则单测**（同路径同句柄、缺失显式）+ **native 缺失软件回退冒烟**（§6.3）+ 图内 home→moveAbs(scale)→waitInPos→dout 往返、单位换算、故障回滚、waitInPos 超时/取消
- App 接线：引 MotionDrivers+Nodes.Motion；`ShellViewModel` 经 `MotionDriverFactory` 建演示控制器（googol 探测→phantom 回退）并注册 `IMotionRuntime`（"demo" 与 "{厂商}:{卡号}"）；调色板新增 6 个运动节点（中/英/韩本地化）；`CombinedNodeFactory` 混入 `MotionNodeFactory`
- `src/MotionDrivers.Tests/` — 13 项：原生装载规则/缺失回退/注入 API 语义映射 + phantom 行为
- `src/Nodes.Motion.Tests/` — 13 项：工厂/参数反射 + 图内运动往返/单位缩放/单命令插补/线性长度校验/超时/回滚/JSON 往返 + 单位换算
- 全解决方案：**149 项单测全绿**（36 Core + 11 Runtime + 11 App + 17 Nodes.Flow + 28 Nodes.Vision + 11 Protocols + 9 Nodes.Comm + 13 MotionDrivers + 13 Nodes.Motion）
- 冒烟：`HalconWorkflow.App.exe` 启动 6 秒存活（已注册回环 Modbus 设备与演示运动控制器）

**阶段 8（数据存储 Storage + Nodes.Data）已落地**：
- `src/Storage/` — 存储适配器（引 Abstractions；Dapper 2.1.35 + Microsoft.Data.Sqlite 9.0.0）
  - `DbDialect.cs` — `DbProviderKind`(Sqlite/SqlServer/MySql/Postgres) + `DbConfig` + `IDbConnectionFactory`/`DbConnectionFactory` + 方言（连接创建/invariant 名/自增列/长文本/时间戳列/`ApplyPaging`）
  - `TraceSchema.cs` — `CycleRow` + 版本化 schema（`schema_version` 表 + 按方言 DDL 的**幂等迁移**，§8.4）
  - `TraceWriteQueue.cs` — 热路径批量队列（§8.3：`Enqueue` 只入锁内环形队列并立即返回、**绝不反压 Execution**；后台泵 100ms/500 条先到为准聚合；**溢出丢最旧 + `DroppedAlarm`**；批失败受限重试后弃批 + `BatchFailed`；`FlushAsync` 排空）
  - `SqlTraceSink.cs` — 单事务 Dapper 批量插入 `cycle_records`；`SqlStorage.cs` — `IRecordStore`+`IQueryStore` 适配器（`InitializeAsync`/`AppendAsync`/`FlushAsync`/`ReadAllAsync`/`QueryAsync<T>`/`ExecuteAsync`/`QueryRowsAsync`）
  - `CsvExporter.cs` — `IExportService` 流式分页 CSV（§9.5.2：默认 UTF-8 带 BOM、可选 GBK；按页查逐行写、不整表进内存；每页每行可取消 + `OnProgress`）
- `src/Abstractions/IStorage.cs` — 阶段 8 扩展：`TraceRecord`、`IQueryStore.QueryRowsAsync`（默认方法，未实现即抛）、`CsvEncoding`/`CsvExportRequest`/`IExportService`（既有 `IRecord`/`IRecordStore`/`IQueryStore` 复用，勿重复定义）
- `src/Nodes.Data/` — 数据节点库（引 Core + Abstractions）：`IDataRuntime`/`DataRuntime`（按名解析记录/查询/导出存储；`ReferenceEqualityComparer` 避免同一存储重复释放）
  - `Nodes/DataNodes.cs` — 一等公民 DB 节点：`data.write:1`（热路径入队即返回）/ `data.query:1`（异步可取消读取，发布 "Result"/"Rows"）；强类型参数对象经 `[NodeParameter]` 反射进属性面板
  - `DataNodeFactory.cs` — 按契约名（ns+版本）反序列化（`data.trigger` 预留未实现）
- 阶段闸门（§13.1 第 8 行）：**批量写不阻塞 Execution 断言** + **追溯查询可取消** + **CSV 流式分页可取消冒烟** + SQLite 真库往返
- App 接线：引 Storage+Nodes.Data；`ShellViewModel` 建 SQLite "trace" 数据源（`%LOCALAPPDATA%\HalconWorkflow\trace.db`）并注册 `IDataRuntime`；调色板新增 2 个数据节点（中/英/韩本地化）；`CombinedNodeFactory` 混入 `DataNodeFactory`；退出经 `IAsyncDisposable` 释放数据运行时
- `src/Storage.Tests/` — 6 项：热路径不阻塞（阻塞 sink 下 500 次 Append 立即返回）、队列溢出丢最旧+告警、SQLite 建表+入队+flush+ReadAll 往返、flush 取消、CSV 分页流式（250 行/BOM/进度）、CSV 首页后取消
- `src/Nodes.Data.Tests/` — 7 项：工厂/参数反射 + 图内 `data.write`→`data.query` 往返 + 查询取消 + 缺失数据源故障 + JSON 往返
- 全解决方案：**163 项单测全绿**（36 Core + 11 Runtime + 12 App + 17 Nodes.Flow + 28 Nodes.Vision + 11 Protocols + 9 Nodes.Comm + 13 MotionDrivers + 13 Nodes.Motion + 6 Storage + 7 Nodes.Data）
- 冒烟：`HalconWorkflow.App.exe` 启动 6 秒存活（已注册回环 Modbus 设备、演示运动控制器与 SQLite 追溯数据源）

**阶段 9（追溯看板能力层：存图 · 良率/产量统计 · 结果预览）已落地**：
- `src/Abstractions/IStorage.cs` — 阶段 9 扩展：`IRecord.Dimensions`（默认成员，无则 null）+ `TraceRecord.Dimensions` init 属性 + `DimensionKeys`(line/machine/shift/model/recipe) + `ImageKind`/`ImageArchiveRequest`/`ImageAsset`/`IImageArchive`；`IStatsService` 改签名（`SliceAsync` 返回 `IReadOnlyList<YieldSlice>` + `SummaryAsync`）
- `src/Storage/DbDialect.cs` — 新增 `DimensionColumn`(`dim_*`)、`DateKey`(按方言日期表达式)、`LastInsertIdSql`（Postgres 走 INSERT…RETURNING，其余用 last-insert-id）
- `src/Storage/TraceSchema.cs` — **schema v1→v2 就地迁移**：`cycle_records` 新增 5 个 `dim_*` 维度列 + `trace_images` 表（原图/渲染图归档行）+ 索引；`UpgradeStatements` 用跨库 `ALTER TABLE … ADD …`；`QueryAllSql`/`CycleRow` 带维度别名；`EnsureCreatedAsync` 幂等
- `src/Storage/StatsService.cs` — `IStatsService`：直接 `GROUP BY` 聚合 `cycle_records`（§9.5.4，**与看板/CSV 同源，不另建第二份**）；按线别/机台/班次/机型/Recipe/日期分片 + `SummaryAsync` 总良率
- `src/Storage/ImageArchive.cs` — `ImageArchiveStore`：快照**落文件**（归档根 + 相对路径入 `trace_images` 行，§9.5.3），`SaveAsync`/`OpenReadAsync`/`QueryByTriggerAsync`/`PruneAsync`；图像绝不进热路径记录
- `src/Storage/PreviewRing.cs` — `PreviewRing`（§9.5.1）：每节点最近 N 帧有界内存环，`Publish` 轻锁不阻塞周期、`Enabled` 可关
- `src/Nodes.Data/DataNodes.cs` — `data.write` 新增可选 `Dimensions` 参数（`k=v;k=v`）
- 阶段闸门（§13.1 第 9 行）：**看板/统计/CSV 同源不另建第二份断言**（库内仅 `schema_version`/`cycle_records`/`trace_images` 三表）+ **存图归档冒烟**（落文件+落行+按 trigger 查+剪枝）
- App 接线：注册 `IStatsService`/`IImageArchive`/`PreviewRing`（经 `ShellViewModel.Stats`/`Images`/`Preview` 暴露），与看板共享同一 "trace" SQLite 源
- `src/Storage.Tests/Stage9GateTests.cs` — 5 项：schema v2 建表+维度列+幂等、v1→v2 迁移保行、统计同源（线别良率 100%/60%、库内仅三表）、存图落文件+按 trigger 查+剪枝、预览环有界
- `src/Nodes.Data.Tests/` — 8 项（+1：`data.write` 维度解析）；`src/App.Tests/` — 13 项（+1：阶段9 服务同源）
- 全解决方案：**170 项单测全绿**（36 Core + 11 Runtime + 13 App + 17 Nodes.Flow + 28 Nodes.Vision + 11 Protocols + 9 Nodes.Comm + 13 MotionDrivers + 13 Nodes.Motion + 11 Storage + 8 Nodes.Data）
- 冒烟：`HalconWorkflow.App.exe` 启动 6 秒存活（已注册 SQLite 追溯源与阶段9 统计/存图/预览服务）

> 下一步：看板 WPF 视图（追溯表/良率图/结果预览面板接线）、交互式连线（Nodify PendingConnection 拖拽建线/断线）、运行期实时数据（scope 值/图像预览）、真实 Halcon 适配器（部署机接入 $MVTEC）。

本地化（中/英/韩）与双语注释规范见 DESIGN §4.8 / §4.9。