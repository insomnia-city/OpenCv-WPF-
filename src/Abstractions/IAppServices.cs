namespace HalconWorkflow.Abstractions;

/// <summary>
/// A structured business operation record, distinct from technical logs (§9.1). 
/// 结构化业务操作记录,区别于技术日志(§9.1)
/// </summary>
public readonly record struct OperationRecord(
    long Id,
    DateTimeOffset At,
    string User,
    string Action,          // action code: contract.action · 动作码
    string? Target = null,  // project/node/recipe reference · 对象引用
    string? Before = null,  // before-change JSON snapshot · 变更前快照
    string? After = null,   // after-change JSON snapshot · 变更后快照
    long? UndoRecordId = null); // back-link marker set when undone · 撤回回链标记

/// <summary>
/// Filter for audit queries (§9.1 UI: by user / time / object). 
/// 审计查询过滤(§9.1 界面：按用户/时间/对象)
/// </summary>
public sealed record AuditFilter(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? User = null,
    string? Action = null,
    string? TargetContains = null,
    int Limit = 500);

/// <summary>
/// Well-known audit action codes (§9.1): graph lifecycle, editing, parameters, run control. 
/// 常用审计动作码(§9.1)：图生命周期、编辑、参数、运行控制
/// </summary>
public static class AuditActions
{
    public const string NewGraph = "graph.new";
    public const string LoadGraph = "graph.load";
    public const string SaveGraph = "graph.save";
    public const string AddNode = "node.add";
    public const string RemoveNode = "node.remove";
    public const string Connect = "link.connect";
    public const string Disconnect = "link.disconnect";
    public const string SetParameter = "param.set";
    public const string Undo = "edit.undo";
    public const string Redo = "edit.redo";
    public const string Run = "run.start";
    public const string Stop = "run.stop";
}

/// <summary>
/// Audit writer/reader used by the app layer; persists to the Storage adapter (§9.1) as an
/// append-only table separate from Serilog and the cycle trace. 
/// 应用层审计读写器;持久化到 Storage 适配器(§9.1),独立于 Serilog 与周期追溯的追加表
/// </summary>
public interface IAuditStore
{
    /// <summary>
    /// Records one operation (or a denied attempt); returns the new record id (for undo back-links).
    /// · 记录一条操作(或一次被拒绝的尝试);返回新记录 id(供撤回回链)
    /// </summary>
    Task<long> RecordAsync(OperationRecord op, CancellationToken ct);

    /// <summary>
    /// Queries audit entries newest-first under the filter. · 按过滤条件倒序查询审计条目
    /// </summary>
    Task<IReadOnlyList<OperationRecord>> QueryAsync(AuditFilter filter, CancellationToken ct);

    /// <summary>
    /// Prunes entries older than the cutoff; returns removed count. · 剪枝早于阈值的条目;返回删除数
    /// </summary>
    Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct);
}

/// <summary>
/// Undoable command contract (§9.2). · 可撤销命令契约(§9.2)
/// </summary>
public interface IUndoableCommand
{
    /// <summary>
    /// Human-readable description shown in undo list. · 撤销列表中的人类可读描述
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Applies the change. · 应用变更
    /// </summary>
    Task DoAsync(CancellationToken ct);

    /// <summary>
    /// Reverts the change. · 回滚变更
    /// </summary>
    Task UndoAsync(CancellationToken ct);

    /// <summary>
    /// Re-applies after undo. · 撤销后重做
    /// </summary>
    Task RedoAsync(CancellationToken ct);
}

/// <summary>
/// Global undo service covering the whole application (§9.2). 
/// 覆盖全应用的全局撤销服务(§9.2)
/// </summary>
public interface IUndoService
{
    /// <summary>
    /// Push a command onto the stack and execute it. · 将命令推入栈并执行
    /// </summary>
    Task PushAndRunAsync(IUndoableCommand cmd, CancellationToken ct);

    /// <summary>
    /// Undoes the top command (if any). · 撤销栈顶命令
    /// </summary>
    Task<bool> UndoAsync(CancellationToken ct);

    /// <summary>
    /// Redoes the last undone command. · 重做最近撤销的命令
    /// </summary>
    Task<bool> RedoAsync(CancellationToken ct);

    /// <summary>
    /// Current undo depth. · 当前撤销深度
    /// </summary>
    int CanUndoCount { get; }

    /// <summary>
    /// Current redo depth. · 当前重做深度
    /// </summary>
    int CanRedoCount { get; }
}