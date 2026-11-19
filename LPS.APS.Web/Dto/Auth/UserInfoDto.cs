namespace LPS.APS.Web.Dto.Auth;

/// <summary>当前用户信息</summary>
public class UserInfoDto
{
    /// <summary>用户ID</summary>
    public int UserId { get; set; }

    /// <summary>用户工号</summary>
    public string UserCode { get; set; } = string.Empty;

    /// <summary>用户姓名（显示名 DisplayName；R1 起改读 DB 真值而非 JWT claim，改资料后立即生效）</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>邮箱（可空；库里为空返回 null，前端按「未提供」显示）</summary>
    public string? Email { get; set; }

    /// <summary>手机号（可空；库里为空返回 null，前端按「未提供」显示）</summary>
    public string? PhoneNumber { get; set; }

    /// <summary>角色列表</summary>
    public List<string> Roles { get; set; } = new();

    /// <summary>功能权限码列表（登录签发时注入 JWT，供前端菜单/按钮渲染）</summary>
    public List<string> Permissions { get; set; } = new();

    /// <summary>业务范围：是否全局放行（Global 时为全放行，各维度集合为空）</summary>
    public bool IsGlobal { get; set; }

    /// <summary>业务范围：工厂范围值集合</summary>
    public List<string> Factories { get; set; } = new();

    /// <summary>业务范围：产品族范围值集合</summary>
    public List<string> ProductFamilies { get; set; } = new();

    /// <summary>业务范围：部门范围值集合</summary>
    public List<string> Departments { get; set; } = new();

    /// <summary>业务范围：域范围值集合</summary>
    public List<string> Domains { get; set; } = new();

    /// <summary>业务范围：资源组织组范围值集合（兼容字段）</summary>
    public List<string> ResourceOrgGroups { get; set; } = new();
}
