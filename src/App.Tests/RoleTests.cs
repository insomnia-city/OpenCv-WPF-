using HalconWorkflow.Abstractions;
using HalconWorkflow.App.Services;
using Xunit;

namespace HalconWorkflow.App.Tests;

/// <summary>
/// Stage-9 acceptance: the role policy is the single source of truth for permissions, and the
/// service reports change events exactly once per switch. 
/// 阶段9 验收：角色策略是权限的唯一来源;服务在每次切换时恰好报告一次变更
/// </summary>
public class RoleTests
{
    [Theory]
    [InlineData(UserRole.ReadOnly, AuditActions.AddNode, false)]
    [InlineData(UserRole.ReadOnly, AuditActions.Run, false)]
    [InlineData(UserRole.Operator, AuditActions.AddNode, false)]
    [InlineData(UserRole.Operator, AuditActions.Run, true)]
    [InlineData(UserRole.Operator, AuditActions.Stop, true)]
    [InlineData(UserRole.Engineer, AuditActions.AddNode, true)]
    [InlineData(UserRole.Engineer, AuditActions.SetParameter, true)]
    [InlineData(UserRole.Engineer, AuditActions.Undo, true)]
    [InlineData(UserRole.Engineer, AuditActions.SetRole, false)]
    [InlineData(UserRole.Admin, AuditActions.SetRole, true)]
    public void Allows_FollowsMinimumRolePerAction(UserRole role, string action, bool expected)
        => Assert.Equal(expected, RolePolicy.Allows(role, action));

    [Fact]
    public void ReadOnly_CanStillViewAndExport()
    {
        Assert.True(RolePolicy.Allows(UserRole.ReadOnly, AuditActions.Export));
        Assert.Equal(UserRole.ReadOnly, RolePolicy.MinimumFor("board.refresh"));
    }

    [Fact]
    public void Service_RaisesChangedOncePerDistinctSwitch()
    {
        var roles = new RoleService();
        Assert.Equal(UserRole.Engineer, roles.Current);
        Assert.True(roles.IsAllowed(AuditActions.AddNode));

        var events = new List<UserRole>();
        roles.Changed += events.Add;

        roles.SetRole(UserRole.ReadOnly);
        Assert.False(roles.IsAllowed(AuditActions.AddNode));
        roles.SetRole(UserRole.ReadOnly);            // no-op · 无变化
        roles.SetRole(UserRole.Admin);

        Assert.Equal([UserRole.ReadOnly, UserRole.Admin], events);
        Assert.True(roles.IsAllowed(AuditActions.SetRole));
    }
}
