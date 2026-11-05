namespace LPS.APS.Core.Authorization;

/// <summary>
/// 业务范围越界异常（F-G4，fail-closed）。
/// 当操作用户的 DataScopePolicy 未授权目标 (ScopeType, ScopeValue) 时由 IDataScopeService.EnsureInScopeAsync 抛出，
/// Web 层统一映射 HTTP 403（区别于普通参数/状态错误 400）。
/// </summary>
public sealed class ScopeViolationException : InvalidOperationException
{
    /// <summary>越界维度（对齐 <see cref="DataScopeTypes"/> 常量值）</summary>
    public string ScopeType { get; }

    /// <summary>越界目标值（DomainKey / FactoryCode 等）</summary>
    public string Value { get; }

    /// <summary>
    /// 固定原因码（可选）。用于编排层与前端按码区分，不依赖文案：
    /// DEPT_SCOPE_VIOLATION（部门越界）/ DEPT_SCOPE_ACTOR_UNRESOLVED（主体无法解析）。
    /// </summary>
    public string? Code { get; }

    public ScopeViolationException(string scopeType, string value)
        : this(scopeType, value, null)
    {
    }

    public ScopeViolationException(string scopeType, string value, string? code)
        : base(code is null
            ? $"业务范围越界（{scopeType}）：{value} 不在当前用户授权范围"
            : $"[{code}] 业务范围越界（{scopeType}）：{value}")
    {
        ScopeType = scopeType;
        Value = value;
        Code = code;
    }
}