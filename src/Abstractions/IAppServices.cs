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
    string? Target,         // project/node/recipe reference · 对象引用
    string? Before,         // before-change JSON snapshot · 变更前快照
    string? After,          // after-change JSON snapshot · 变更后快照
    long? UndoRecordId);    // back-link marker set when undone · 撤回回链标记

/// <summary>
/// Audit writer used by the app layer; persists to the Storage adapter (§9.1). 
/// 应用层审计写入器;持久化到 Storage 适配器(§9.1)
/// </summary>
public interface IAuditStore
{
    /// <summary>
    /// Records one operation (or a denied attempt). · 记录一条操作(或一次被拒绝的尝试)
    /// </summary>
    Task RecordAsync(OperationRecord op, CancellationToken ct);
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