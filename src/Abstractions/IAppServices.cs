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

    /// <summary>Data was read out to a file (CSV export). · 数据被导出为文件(CSV 导出)</summary>
    public const string Export = "data.export";

    /// <summary>Current role was changed (§9.3). · 当前角色被变更(§9.3)</summary>
    public const string SetRole = "role.set";

    /// <summary>A permission check failed and the operation was refused (§9.3). · 权限校验失败且操作被拒(§9.3)</summary>
    public const string AccessDenied = "access.denied";
}

/// <summary>
/// Application roles, ordered least → most privileged (§9.3). 
/// 应用角色，按权限由低到高排列(§9.3)
/// </summary>
public enum UserRole
{
    /// <summary>May only view boards / export. · 仅可查看看板/导出</summary>
    ReadOnly = 0,

    /// <summary>May also run and stop the graph. · 另可运行/停止图</summary>
    Operator = 1,

    /// <summary>May also edit the graph and parameters. · 另可编辑图与参数</summary>
    Engineer = 2,

    /// <summary>May also manage roles. · 另可管理角色</summary>
    Admin = 3
}

/// <summary>
/// Role → minimum-role policy for audit action codes (§9.3): the single place that decides which
/// role may perform which operation, so the UI gate and the audit record never disagree.
/// · 审计动作码 → 最低角色的策略(§9.3)：决定哪个角色可执行哪个操作的唯一位置，
///   使界面门控与审计记录永不矛盾。
/// </summary>
public static class RolePolicy
{
    /// <summary>Minimum role required to perform an action. · 执行某动作所需的最低角色</summary>
    public static UserRole MinimumFor(string action) => action switch
    {
        AuditActions.NewGraph or AuditActions.LoadGraph or AuditActions.SaveGraph
            or AuditActions.AddNode or AuditActions.RemoveNode
            or AuditActions.Connect or AuditActions.Disconnect
            or AuditActions.SetParameter or AuditActions.Undo or AuditActions.Redo
            => UserRole.Engineer,
        AuditActions.Run or AuditActions.Stop => UserRole.Operator,
        AuditActions.SetRole => UserRole.Admin,
        _ => UserRole.ReadOnly // viewing, refreshing, exporting are read operations · 查看/刷新/导出属只读
    };

    /// <summary>True when the role is at least the minimum for the action. · 角色达到动作最低要求</summary>
    public static bool Allows(UserRole role, string action) => role >= MinimumFor(action);
}

/// <summary>
/// Role-based permission gate (§9.3): the effective role, a change event and the action check.
/// · 基于角色的权限门(§9.3)：当前角色、变更事件与动作校验。
/// </summary>
public interface IRoleService
{
    /// <summary>Effective role. · 当前生效角色</summary>
    UserRole Current { get; }

    /// <summary>Raised after the role changes. · 角色变更后触发</summary>
    event Action<UserRole>? Changed;

    /// <summary>Switches the effective role. · 切换当前生效角色</summary>
    void SetRole(UserRole role);

    /// <summary>True when the current role may perform the action. · 当前角色是否可执行该动作</summary>
    bool IsAllowed(string action);
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

    /// <summary>
    /// True when there is at least one edit above the last save point. · 保存点之上是否还有编辑
    /// </summary>
    bool CanUndoToSavePoint { get; }

    /// <summary>
    /// True when the current state differs from the last save/load point in either direction:
    /// edits above the save point, OR undone below it, OR a divergent branch after undoing.
    /// Drives the unsaved-changes guard. · 当前状态是否偏离最近保存/载入点（保存点之上有编辑、已回退到其下、或撤销后走出新分支）——驱动未保存改动防护。
    /// </summary>
    bool IsModifiedSinceSave { get; }

    /// <summary>
    /// Undoes until the last save point; returns how many steps were rolled back (§9.2).
    /// · 撤销至最近保存点；返回回滚步数(§9.2)
    /// </summary>
    Task<int> UndoToSavePointAsync(CancellationToken ct);

    /// <summary>
    /// Marks the current stack depth as the save point (after load / save). · 将当前栈深标记为保存点(载入/保存后)
    /// </summary>
    void MarkSaved();
}