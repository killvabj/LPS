using LPS.APS.Core.Authorization;

namespace LPS.APS.Core.Interfaces;

/// <summary>
/// 业务范围解析服务（F-G4，3号位 Auth 域职责）
/// 解析当前用户的有效业务范围：用户直接范围 ∪ 角色范围，Global 优先。
/// </summary>
public interface IDataScopeService
{
    /// <summary>
    /// 解析指定用户的有效业务范围
    /// </summary>
    /// <param name="userId">用户 Id</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>有效业务范围上下文；无任何授权时返回 <see cref="DataScopeContext.Empty"/>（拒绝全部）</returns>
    Task<DataScopeContext> ResolveScopeAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 断言指定用户对 (scopeType, value) 拥有业务范围授权（F-G4，fail-closed）。
    /// Global 放行；该维度未授权或不含目标值一律抛 <see cref="ScopeViolationException"/>。
    /// </summary>
    /// <param name="userId">用户 Id</param>
    /// <param name="scopeType">业务范围维度（<see cref="DataScopeTypes"/> 常量值）</param>
    /// <param name="value">目标值（DomainKey / FactoryCode 等）</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task EnsureInScopeAsync(int userId, string scopeType, string value, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按登录名（User.LoginName）解析有效业务范围（队列3 Department enforce 专用）
    /// 由来：ScheduleRun.TriggeredBy 冻结的是<b>登录名</b>（CandidateRunCreateSpec.Actor 原样写入），
    /// 而夜间/装载链没有 userId 变量；本方法把「登录名 → 用户 → 有效范围」收敛在 Auth 域内，
    /// 调用方（部门范围守卫）因此无需搬运 actor，装载侧也无需新增 SELECT 列。
    /// </summary>
    /// <param name="userCode">登录名（User.LoginName，唯一）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>有效业务范围上下文；登录名无法解析为有效用户时返回 <c>null</c>（由调用方按 DEPT_SCOPE_ACTOR_UNRESOLVED fail-closed，不猜）</returns>
    Task<DataScopeContext?> ResolveScopeByUserCodeAsync(string userCode, CancellationToken cancellationToken = default);
}
