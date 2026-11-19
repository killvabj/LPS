namespace LPS.APS.Web.Dto.Auth;

/// <summary>
/// 改自己资料请求（R1）
/// 仅允许改三项：userName（显示名）/ email / phoneNumber。UserCode/Status/角色/范围归管理面，不在此自助通道。
/// </summary>
public class UpdateProfileRequest
{
    /// <summary>显示名（DisplayName）；可空（不传则保持原值；传则覆盖）</summary>
    public string? UserName { get; set; }

    /// <summary>邮箱</summary>
    public string? Email { get; set; }

    /// <summary>手机号</summary>
    public string? PhoneNumber { get; set; }
}