using RuleSetVersion = LPS.APS.Core.Entities.APS.RuleSetVersion;
using LPS.APS.Core.DTOs.Governance;

namespace LPS.APS.Core.Interfaces;

/// <summary>
/// 规则集版本仓储接口（阶段 A-2：Engine 治理仓储，Dapper + DatabaseConnectionManager 实现）
/// 对应 APS_Production.RuleSetVersion。
/// 红线：已发布版本不可原地修改，须创建新版本（R01 验收场景）。
/// </summary>
public interface IRuleSetVersionRepository
{
    /// <summary>按 Id 查询规则集版本</summary>
    Task<RuleSetVersion?> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>按规则集 Id 查询全部版本（按 VersionCode 排序）</summary>
    Task<IReadOnlyList<RuleSetVersion>> GetByRuleSetIdAsync(long ruleSetId, CancellationToken ct = default);

    /// <summary>
    /// 按规则集 Id 查询版本摘要（2026-10-15 4号位 来文 Q2 同款缺陷·潜伏：与参数集列表同源 SELECT * 整列搬大 LOB；
    /// 现 rule-set 无大快照故 16ms 非问题，先一并落码防将来规则集快照变大后版本下拉同方式不可用）。
    /// 显式列仅交付摘要字段（排除 ContentSnapshotJson/DemandPriorityJson 载体列），列表读端点专用；
    /// 需要完整快照的消费方（G3 当前生效版本投影、详情端点）仍走 <see cref="GetByRuleSetIdAsync"/>。
    /// </summary>
    Task<IReadOnlyList<RuleSetVersionSummaryDto>> GetSummariesByRuleSetIdAsync(long ruleSetId, CancellationToken ct = default);

    /// <summary>新增版本（新版本须为新记录，历史不可覆盖）</summary>
    Task<RuleSetVersion> AddAsync(RuleSetVersion version, CancellationToken ct = default);

    /// <summary>更新版本（仅限非 PUBLISHED 状态的可编辑字段）</summary>
    Task UpdateAsync(RuleSetVersion version, CancellationToken ct = default);

    /// <summary>
    /// 原子设置版本为同族（RuleSetId）默认（D-3 默认版本承载，2026-10-17）：
    /// 单事务内「清同族其它 IsDefault=1」+「置目标 IsDefault=1」，修正 SPV 现 ClearDefaultFlag+Update 两条独立 SQL 非原子的缺陷。
    /// 由 Application 层预先校验目标 PUBLISHED（<see cref="GovernanceVersionService.SetDefaultRuleSetVersionAsync"/>）。
    /// </summary>
    Task SetDefaultAsync(long ruleSetId, long versionId, CancellationToken ct = default);

    /// <summary>
    /// 读侧默认解析（D-3，2026-10-17）：同族 RuleSetId 内 IsDefault=1 且 PUBLISHED 的候选全集（不 TOP 1），
    /// 供 Application 层结合生效窗过滤后判定 0/1/多（多 → 歧义报错，红线 #4 禁止随机取）。
    /// 显式列避免大 LOB（ContentSnapshotJson）搬运。
    /// </summary>
    Task<IReadOnlyList<RuleSetVersion>> GetDefaultCandidatesAsync(long ruleSetId, CancellationToken ct = default);

    /// <summary>
    /// 物理删除单个版本（DELETE /rule-set/version/{id}，R1 2026-10-25）：仅用于清 DRAFT 草稿壳。
    /// 调用方须先保证 Status=DRAFT 且未被 StrategyProfileVersion 引用（否则 FK 违约）。</summary>
    Task DeleteByIdAsync(long id, CancellationToken ct = default);
}
