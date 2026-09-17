using HalconWorkflow.Abstractions;

namespace HalconWorkflow.App.Services;

/// <summary>
/// Default role gate (§9.3): holds the effective role and answers action checks through
/// <see cref="RolePolicy"/>. Defaults to <see cref="UserRole.Engineer"/> so a normal operator
/// station is fully usable until a stricter role is chosen. · 默认权限门(§9.3)：持有当前角色，
/// 经 RolePolicy 回答动作校验。默认 Engineer，使普通操作站开箱可用，直到选择更严格角色。
/// </summary>
public sealed class RoleService : IRoleService
{
    private UserRole _current = UserRole.Engineer;

    /// <inheritdoc />
    public UserRole Current => _current;

    /// <inheritdoc />
    public event Action<UserRole>? Changed;

    /// <inheritdoc />
    public void SetRole(UserRole role)
    {
        if (_current == role) return;
        _current = role;
        Changed?.Invoke(role);
    }

    /// <inheritdoc />
    public bool IsAllowed(string action) => RolePolicy.Allows(_current, action);
}
