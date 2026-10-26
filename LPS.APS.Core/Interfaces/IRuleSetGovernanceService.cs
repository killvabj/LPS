using RuleSet = LPS.APS.Core.Entities.APS.RuleSet;

namespace LPS.APS.Core.Interfaces;

/// <summary>
/// 规则集主表治理服务（2026-10-22：补齐 4号位 缺口「主表零写端点」，3号位 应用编排）
/// 防线：镜像 DomainDefinition 主表写范式（唯一校验 → EnsureWritableAsync 前置 → 写 → AuditAsync 落 AuditLog）。
/// Q3 已裁决（0号位 D1 2026-09-28）＝方案 A 跨域通用：规则集不分域，本服务**无业务范围 Scope 校验**。
/// Q5 已裁＝不提供物理删除端点，一律以 IsActive 停用（防孤儿版本、守「已发布不可改」红线）；
///     **R1 例外（2026-10-25，用户提出「新建未发布的可删」）**：无 PUBLISHED 版本的规则集/纯草稿版本可物理删，
///     有 PUBLISHED 版本仍只可停用（历史不可变守恒）。
/// Q6 已裁＝挂 aps.rule.edit（PermissionCodes.RuleMaintain）。
/// </summary>
/// <remarks>开发者：3号位</remarks>
public interface IRuleSetGovernanceService
{
    /// <summary>
    /// 新建规则集主表（POST /rule-set）。
    /// Q1：RuleSetCode 用户填写、创建时校验唯一（大小写不敏感）、创建后不可变更；
    ///     唯一冲突抛 InvalidOperationException（Web 层 → 400）。
    /// Q2：主表仅建壳（默认 IsActive=true），第一版内容由前端随后自动跟建空白 DRAFT 版本承载。
    /// 写前 EnsureWritableAsync 前置（跨库无 2PC，fail-before-write），写后 AuditAsync(ActionCode=Create)。
    /// </summary>
    Task<RuleSet> CreateRuleSetAsync(RuleSet input, string? createdBy, int actorUserId, CancellationToken ct = default);

    /// <summary>
    /// 更新规则集主表（PUT /rule-set/{id}）：仅改 Name/Description/IsActive + 审计；RuleSetCode 冻结不可改。
    /// Q4：IsActive=0 仅关主表不级联版本（运行时判主表归 2号位/1号位 转达，本服务不落）。
    /// 写前 EnsureWritableAsync 前置，写后 AuditAsync(ActionCode=Update)。
    /// </summary>
    Task<RuleSet> UpdateRuleSetAsync(long id, RuleSet input, string? operatedBy, int actorUserId, CancellationToken ct = default);

    /// <summary>
    /// 物理删除规则集整族（DELETE /rule-set/{id}，R1 2026-10-25）：无任何 PUBLISHED 版本才可删。
    /// 校验失败 / 存在 PUBLISHED / 版本被策略包引用 → 抛 InvalidOperationException（Web 层 → 400）。
    /// 审计前置（物理删前行将消失，须先留 AuditLog ActionCode=Delete）后整族删（版本+主表，单事务）。
    /// </summary>
    Task DeleteRuleSetAsync(long id, string? operatedBy, int actorUserId, CancellationToken ct = default);

    /// <summary>
    /// 物理删除单个草稿版本（DELETE /rule-set/version/{id}，R1 2026-10-25）：仅 Status=DRAFT 可删（清空白草稿壳）。
    /// 已发布/停用/归档版本拒绝。审计前置后删单行。
    /// </summary>
    Task DeleteRuleSetVersionAsync(long versionId, string? operatedBy, int actorUserId, CancellationToken ct = default);
}