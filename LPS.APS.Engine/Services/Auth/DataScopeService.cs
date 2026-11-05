using LPS.APS.Core.Authorization;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;
using Microsoft.EntityFrameworkCore;

namespace LPS.APS.Engine.Services.Auth;

/// <summary>
/// 业务范围解析服务实现（F-G4，3号位 Auth 域）
/// 有效范围 = UserDataScope（用户直接）∪ RoleDataScope（经 UserRole 关联的角色），含 Global 时全放行。
/// 数据来源：APS_Auth（AuthDbContext）。
/// </summary>
public class DataScopeService : IDataScopeService
{
    private readonly AuthDbContext _context;

    public DataScopeService(AuthDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc />
    public async Task<DataScopeContext> ResolveScopeAsync(int userId, CancellationToken cancellationToken = default)
    {
        // 1. 用户直接范围策略 Id
        var userScopeIds = _context.UserDataScopes
            .Where(x => x.UserId == userId)
            .Select(x => x.ScopePolicyId);

        // 2. 角色范围策略 Id（UserRole → RoleDataScope；join Role 过滤停用角色，与权限解析对齐）
        var roleScopeIds =
            from ur in _context.UserRoles
            join r in _context.Roles on ur.RoleId equals r.Id
            join rd in _context.RoleDataScopes on ur.RoleId equals rd.RoleId
            where ur.UserId == userId && r.IsActive
            select rd.ScopePolicyId;

        var policyIds = await userScopeIds.Union(roleScopeIds).Distinct().ToListAsync(cancellationToken);

        if (policyIds.Count == 0)
            return DataScopeContext.Empty;

        var policies = await (
            from p in _context.DataScopePolicies
            where policyIds.Contains(p.Id) && p.IsEnabled
            select new { p.ScopeType, p.ScopeValue }
        ).ToListAsync(cancellationToken);

        return DataScopeContext.FromPolicies(policies.Select(p => (p.ScopeType, p.ScopeValue)));
    }

    /// <inheritdoc />
    public async Task<DataScopeContext?> ResolveScopeByUserCodeAsync(string userCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userCode))
            return null;

        // LoginName 唯一（UQ_User_LoginName）⇒ 解析出 userId 后复用既有并集逻辑，不复制策略查询
        var userId = await _context.Users
            .Where(x => x.LoginName == userCode)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return userId is null ? null : await ResolveScopeAsync(userId.Value, cancellationToken);
    }

    /// <inheritdoc />
    public async Task EnsureInScopeAsync(int userId, string scopeType, string value, CancellationToken cancellationToken = default)
    {
        var scope = await ResolveScopeAsync(userId, cancellationToken);
        if (!scope.Allows(scopeType, value))
        {
            throw new ScopeViolationException(scopeType, value);
        }
    }
}
