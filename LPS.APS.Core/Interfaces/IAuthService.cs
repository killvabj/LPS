using LPS.APS.Core.Dto;

namespace LPS.APS.Core.Interfaces;

/// <summary>
/// 认证服务接口
/// 职责：用户登录验证、Token 签发与刷新、密码管理
/// 
/// 接口定义在 Core 层，实现在 Engine 层（访问 Auth 数据库）
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// 用户登录
    /// </summary>
    /// <param name="userCode">用户工号</param>
    /// <param name="password">明文密码</param>
    /// <param name="clientIp">客户端 IP（由控制器从 HttpContext 传入，供审计，可空）</param>
    /// <param name="userAgent">客户端 UA（由控制器从 HttpContext 传入，供审计，可空）</param>
    /// <returns>登录结果（含 Token）</returns>
    Task<LoginResult> LoginAsync(string userCode, string password, string? clientIp = null, string? userAgent = null);

    /// <summary>
    /// 改自己资料（R1，4号位 2026-11-17 来文）。
    /// 自助通道仅允许改三项：userName（显示名 DisplayName）/ email / phoneNumber；
    /// 登录名 LoginName（UserCode）、Status、角色、业务范围不可经此修改（归 auth.manage 管理面）。
    /// 返回改后生效值（userName 已 trim、未传字段保持原值）。
    /// </summary>
    Task<(string UserName, string? Email, string? PhoneNumber)> UpdateProfileAsync(
        int userId, string? userName, string? email, string? phoneNumber,
        string? clientIp = null, string? userAgent = null);

    /// <summary>
    /// 修改密码（R2，4号位 2026-11-17 来文）。
    /// 旧密码须经 <see cref="PasswordHasher.Verify"/> 校验通过；新密码沿用建用户口径（长度 8~128），
    /// 且不得与旧密码/登录名相同；成功后重哈希 PasswordHash 并清空 RefreshToken/RefreshTokenExpiry（旧会话失效）。
    /// 审计 Action=ChangePassword，不记明文（U42 脱敏）。
    /// </summary>
    Task ChangePasswordAsync(
        int userId, string oldPassword, string newPassword,
        string? clientIp = null, string? userAgent = null);

    /// <summary>
    /// 刷新 AccessToken
    /// </summary>
    /// <param name="accessToken">过期的 AccessToken</param>
    /// <param name="refreshToken">有效的 RefreshToken</param>
    /// <returns>新的 Token 对</returns>
    Task<LoginResult> RefreshTokenAsync(string accessToken, string refreshToken);

    /// <summary>
    /// 登出（吊销 RefreshToken）
    /// </summary>
    /// <param name="userId">用户ID</param>
    /// <param name="userCode">用户工号（由控制器从 JWT 声明传入，供审计 entityId，可空）</param>
    /// <param name="clientIp">客户端 IP（由控制器从 HttpContext 传入，供审计，可空）</param>
    /// <param name="userAgent">客户端 UA（由控制器从 HttpContext 传入，供审计，可空）</param>
    Task LogoutAsync(int userId, string? userCode = null, string? clientIp = null, string? userAgent = null);

    /// <summary>
    /// 读取个人资料（R1，4号位 2026-11-18 来文：GET /api/auth/me 补 email/phoneNumber）。
    /// 按主键读 [User] 的 显示名 DisplayName / 邮箱 / 手机号（一次 [User] 主键查询，供 /me 页面回读）。
    /// **显示名从 DB 读而非 JWT claim**（claim 至多晚 120 分钟才随重签更新；DB 为真值，改资料后立即生效）。
    /// 用户不存在或已删除返回 null（调用方按「无可显示资料」处理，不抛）。
    /// </summary>
    /// <param name="userId">用户ID（PK，来自 JWT NameIdentifier 声明）</param>
    /// <param name="ct">取消令牌</param>
    Task<(string DisplayName, string? Email, string? PhoneNumber)?> GetProfileAsync(int userId, CancellationToken ct = default);
}
