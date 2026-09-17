using Dapper;
using HalconWorkflow.Abstractions;

namespace HalconWorkflow.Storage;

/// <summary>
/// Single source of the audit SELECT/filter SQL (§9.1). Shared by <see cref="SqlAuditService"/>
/// and the board's CSV export so the on-screen audit view and the export never diverge.
/// · 审计 SELECT/过滤 SQL 的唯一来源(§9.1)。由 SqlAuditService 与看板 CSV 导出共用，
///   使屏幕上的审计视图与导出结果永不偏离。
/// </summary>
public static class AuditSql
{
    /// <summary>Base projection of all audit columns with stable aliases. · 全审计列稳定别名投影</summary>
    public const string BaseSelect =
        "SELECT id AS Id, occurred_at AS OccurredAt, user_name AS UserName, action AS Action, " +
        "target AS Target, before_json AS BeforeJson, after_json AS AfterJson, undo_record_id AS UndoRecordId " +
        "FROM operation_records";

    /// <summary>Base order used by all audit reads (newest first). · 所有审计读取的排序(倒序)</summary>
    public const string OrderByNewest = " ORDER BY id DESC";

    /// <summary>
    /// Builds the WHERE clause + bound parameters for a filter; both are empty when no filter is set.
    /// · 为过滤条件构造 WHERE 子句与绑定参数；无条件时两者皆空。
    /// </summary>
    public static (string Where, DynamicParameters Parameters) Filter(AuditFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var clauses = new List<string>();
        var parameters = new DynamicParameters();

        if (filter.From is { } from)
        {
            clauses.Add("occurred_at >= @from");
            parameters.Add("from", from.UtcDateTime);
        }

        if (filter.To is { } to)
        {
            clauses.Add("occurred_at < @to");
            parameters.Add("to", to.UtcDateTime);
        }

        if (!string.IsNullOrWhiteSpace(filter.User))
        {
            clauses.Add("user_name = @user");
            parameters.Add("user", filter.User);
        }

        if (!string.IsNullOrWhiteSpace(filter.Action))
        {
            clauses.Add("action = @action");
            parameters.Add("action", filter.Action);
        }

        if (!string.IsNullOrWhiteSpace(filter.TargetContains))
        {
            clauses.Add("target LIKE @target");
            parameters.Add("target", "%" + filter.TargetContains + "%");
        }

        return (clauses.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", clauses), parameters);
    }
}
