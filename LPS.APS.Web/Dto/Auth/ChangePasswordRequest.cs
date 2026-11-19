namespace LPS.APS.Web.Dto.Auth;

/// <summary>
/// 修改密码请求（R2）
/// 必须带上旧密码验身；newPassword 不落审计（U42 脱敏）。
/// </summary>
public class ChangePasswordRequest
{
    /// <summary>旧密码</summary>
    public string OldPassword { get; set; } = string.Empty;

    /// <summary>新密码</summary>
    public string NewPassword { get; set; } = string.Empty;
}