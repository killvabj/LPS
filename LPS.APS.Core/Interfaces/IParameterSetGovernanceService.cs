using ParameterSet = LPS.APS.Core.Entities.APS.ParameterSet;

namespace LPS.APS.Core.Interfaces;

/// <summary>
/// 参数集主表治理服务（2026-10-25：补齐 4号位 缺口「参数集缺主表写端点」，照 rule-set 10-22 范式，3号位 应用编排）
/// 防线：镜像 rule-set 主表写范式（唯一校验 → EnsureWritableAsync 前置 → 写 → AuditAsync 落 AuditLog）。
/// Q3＝方案 A 跨域通用：参数集不分域，本服务**无业务范围 Scope 校验**（与 rule-set 一致，fail-closed 由版本发布对象化校验承接）。
/// Q5＝**不提供物理删除端点，一律以 IsActive 停用**（Q5 从属策略族，零 R1 例外——不给 DELETE）。
/// Q6＝挂 aps.parameter.edit（PermissionCodes.ParameterEdit）。
/// </summary>
/// <remarks>开发者：3号位</remarks>
public interface IParameterSetGovernanceService
{
    /// <summary>
    /// 新建参数集主表（POST /parameter-set）。
    /// Q1：ParameterSetCode 用户填写、创建时校验唯一（大小写不敏感）、创建后不可变更；唯一冲突抛 InvalidOperationException（→ 400）。
    /// Q2：主表仅建壳（默认 IsActive=true），第一版由前端随后自动跟建空白 DRAFT 版本承载。
    /// 写前 EnsureWritableAsync 前置（跨库无 2PC，fail-before-write），写后 AuditAsync(ActionCode=Create)。
    /// </summary>
    Task<ParameterSet> CreateParameterSetAsync(ParameterSet input, string? createdBy, int actorUserId, CancellationToken ct = default);

    /// <summary>
    /// 更新参数集主表（PUT /parameter-set/{id}）：仅改 Name/Description/IsActive + 审计；ParameterSetCode 冻结不可改。
    /// Q4：IsActive=0 仅关主表不级联版本（运行时判主表归 2号位/1号位 转达，本服务不落）。
    /// 写前 EnsureWritableAsync 前置，写后 AuditAsync(ActionCode=Update)。
    /// </summary>
    Task<ParameterSet> UpdateParameterSetAsync(long id, ParameterSet input, string? operatedBy, int actorUserId, CancellationToken ct = default);

    /// <summary>
    /// 物理删除参数集整族（DELETE /parameter-set/{id}，2026-10-26 同 R1 规则集删除口径）。
    /// 允许删除条件：无任何 PUBLISHED（已发布）版本才可删＋其 DRAFT（草稿）版本连带删除＋含 PUBLISHED（已发布）必拒 400「请先停用」。
    /// FK 反查阻断：任一版本被策略包版本引用则拒绝（CountReferencingParameterSetVersionAsync）。
    /// 审计前置（ActionCode=Delete，行消失前落 AuditLog）后 DeleteFamilyAsync 单事务硬删。
    /// 拒删 message：含 PUBLISHED（已发布）→「该参数集已发布过，不能删除（请先停用版本）」；
    /// 被策略包版本引用 →「该参数集存在被策略包引用的版本，不能删除」。
    /// </summary>
    Task DeleteParameterSetAsync(long id, string? operatedBy, int actorUserId, CancellationToken ct = default);
}