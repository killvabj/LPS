using StrategyProfile = LPS.APS.Core.Entities.APS.StrategyProfile;

namespace LPS.APS.Core.Interfaces;

/// <summary>
/// 策略包主表治理服务（2026-10-25：补齐 4号位 缺口「策略包缺主表写端点」，照 rule-set 10-22 范式，3号位 应用编排）
/// 防线：镜像 rule-set 主表写范式（唯一校验 → EnsureWritableAsync 前置 → 写 → AuditAsync 落 AuditLog）。
/// Q3＝方案 A 跨域通用：策略包不分域，本服务**无业务范围 Scope 校验**。
/// Q5＝**不提供物理删除端点，一律以 IsActive 停用**。
/// Q8＝StrategyProfile.RunType 仅接受五值或 NULL（冻结 07 §5.5，取 <see cref="StrategyProfileRunType"/>）；
///     「同 RunType 多启用包」合法性与运行侧取舍归 2号位 装载侧登记转达（DDL 无唯一约束，本服务不硬拦唯一性）。
/// Q6＝挂 aps.strategy.edit（PermissionCodes.StrategyEdit）。
/// </summary>
/// <remarks>开发者：3号位</remarks>
public interface IStrategyProfileGovernanceService
{
    /// <summary>
    /// 新建策略包主表（POST /strategy-profile）。
    /// Q1：StrategyProfileCode 用户填写、创建时校验唯一（大小写不敏感）、创建后不可变更；唯一冲突抛 InvalidOperationException（→ 400）。
    /// Q2：**只建壳，不能自动跟建版本**（StrategyProfileVersion.RuleSetVersionId/ParameterSetVersionId 均必填 FK，无「空白首版」合法状态）。
    /// 写前 EnsureWritableAsync 前置，写后 AuditAsync(ActionCode=Create)。
    /// </summary>
    Task<StrategyProfile> CreateStrategyProfileAsync(StrategyProfile input, string? createdBy, int actorUserId, CancellationToken ct = default);

    /// <summary>
    /// 更新策略包主表（PUT /strategy-profile/{id}）：仅改 Name/Description/RunType/IsActive + 审计；StrategyProfileCode 冻结不可改。
    /// Q4：IsActive=0 仅关主表不级联版本（运行时判主表归 2号位/1号位 转达，本服务不落）。
    /// 写前 EnsureWritableAsync 前置，写后 AuditAsync(ActionCode=Update)。
    /// </summary>
    Task<StrategyProfile> UpdateStrategyProfileAsync(long id, StrategyProfile input, string? operatedBy, int actorUserId, CancellationToken ct = default);

    /// <summary>
    /// 物理删除策略包整族（DELETE /strategy-profile/{id}，2026-10-26 同 R1 规则集删除口径）。
    /// 允许删除条件：无任何 PUBLISHED（已发布）版本才可删＋其 DRAFT（草稿）版本连带删除＋含 PUBLISHED（已发布）必拒 400「请先停用」。
    /// FK 反查阻断：任一版本被 ScheduleRun（运行）引用（ScheduleRun.StrategyProfileVersionId）则拒绝。
    /// 审计前置（ActionCode=Delete，行消失前落 AuditLog）后 DeleteFamilyAsync 单事务硬删。
    /// 拒删 message：含 PUBLISHED（已发布）→「该策略包已发布过，不能删除（请先停用版本）」；
    /// 被运行引用 →「该策略包存在已被运行引用的版本，不能删除」。
    /// </summary>
    Task DeleteStrategyProfileAsync(long id, string? operatedBy, int actorUserId, CancellationToken ct = default);
}