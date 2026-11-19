namespace LPS.APS.Web.Dto.Auth;

/// <summary>改自己资料响应（R1 回包：回显改后的显示名/邮箱/手机号）</summary>
public class UpdateProfileResponse
{
    /// <summary>显示名（DisplayName）</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>邮箱</summary>
    public string? Email { get; set; }

    /// <summary>手机号</summary>
    public string? PhoneNumber { get; set; }
}