using LPS.APS.Core.Entities.APS;

namespace LPS.APS.Core.Interfaces;

/// <summary>
/// 策略包版本仓储接口（阶段 B-3：3号位）
/// 对应表：APS_Production.dbo.StrategyProfileVersion
/// </summary>
/// <remarks>开发者：3号位</remarks>
public interface IStrategyProfileVersionRepository
{
    /// <summary>获取策略包版本详情</summary>
    System.Threading.Tasks.Task<StrategyProfileVersion?> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>获取策略包的所有版本</summary>
    System.Threading.Tasks.Task<IReadOnlyList<StrategyProfileVersion>> GetByStrategyProfileIdAsync(long strategyProfileId, CancellationToken ct = default);

    /// <summary>
    /// 获取策略包的所有版本（列表投影：JOIN 带出版本编码与父级主表 Id；4号位 2026-10-14 审计 R2/R3 补字段）。
    /// 返回 StrategyProfileVersionListItem 而非实体——供前端版本列表直显编码与编辑时定位父级（消除 N+1 反查）。
    /// </summary>
    System.Threading.Tasks.Task<IReadOnlyList<LPS.APS.Core.DTOs.Governance.StrategyProfileVersionListItem>> GetByStrategyProfileIdWithCodesAsync(long strategyProfileId, CancellationToken ct = default);

    /// <summary>获取策略包的默认版本（IsDefault=1 且 PUBLISHED）</summary>
    System.Threading.Tasks.Task<StrategyProfileVersion?> GetDefaultByStrategyProfileIdAsync(long strategyProfileId, CancellationToken ct = default);

    /// <summary>创建策略包版本</summary>
    System.Threading.Tasks.Task<StrategyProfileVersion> AddAsync(StrategyProfileVersion version, CancellationToken ct = default);

    /// <summary>更新策略包版本</summary>
    System.Threading.Tasks.Task UpdateAsync(StrategyProfileVersion version, CancellationToken ct = default);

    /// <summary>清除同一策略包内其他版本的 IsDefault 标志（A-6 不变量）</summary>
    System.Threading.Tasks.Task ClearDefaultFlagAsync(long strategyProfileId, long exceptVersionId, CancellationToken ct = default);

    /// <summary>
    /// 单事务原子默认切换（R12-O02 / S-04 落码，2026-11-03）：同一事务内「清同 Profile 其它 IsDefault=1」
    /// +「置目标 IsDefault=1」，修正 <see cref="ClearDefaultFlagAsync"/> + <see cref="UpdateAsync"/>
    /// 两条独立 SQL 的非原子缺陷（崩溃/并发下可能同族双默认；镜像 RuleSet/ParameterSet 已修的
    /// <c>SetDefaultAsync</c> 一致语义，规避 UQ_StrategyProfileVersion_DefaultPublished 过滤唯一索引冲突）。
    /// </summary>
    System.Threading.Tasks.Task SetDefaultAsync(long strategyProfileId, long versionId, CancellationToken ct = default);

    /// <summary>
    /// 按 RunType 获取所有候选默认版本（P0-06：歧义检测用）
    /// 语义：RunType 匹配 IsActive=1 父 StrategyProfile 的 IsDefault=1 + PUBLISHED 版本全集。
    /// 返回 IReadOnlyList 而非单对象（红线 #4：禁止盲目 First()），
    /// 由 Application 层结合 EffectiveFrom/EffectiveTo 生效窗口过滤后判定 0/1/多（多 → 歧义报错）。
    /// </summary>
    System.Threading.Tasks.Task<IReadOnlyList<StrategyProfileVersion>> GetDefaultByRunTypeAsync(string runType, CancellationToken ct = default);

    /// <summary>
    /// 按 RunType 解析默认候选，**含 NULL 通用兜底**（AE-007 / Q8 落码，2026-10-29）。
    /// 语义（AE-007 ＋ 06 §S5）：**精确 `RunType` 优先；仅当该时点「精确 `RunType`」下无任何生效默认时，
    /// 才回退到 `RunType IS NULL`（「适用所有」）的默认；两层不混合**。
    /// 生效窗口（`EffectiveFrom`/`EffectiveTo`，有值才校验）与 `IsDefault`/`PUBLISHED`/父 `IsActive`
    /// 在 SQL 内一次判定，避免「精确层存在但已过期」错误地挡住 NULL 兜底。
    /// 返回 IReadOnlyList 而非单对象（红线 #4）；**同层多默认由调用方判歧义并返回 HTTP 409**，不得随机取第一个。
    /// 与 <see cref="GetDefaultByRunTypeAsync"/> 的关系：后者保持**精确匹配**语义不变（写侧跨包收敛等仍用它），
    /// 本方法是**新增**的读侧两级解析入口，不改既有签名（红线 #5）。
    /// </summary>
    System.Threading.Tasks.Task<IReadOnlyList<StrategyProfileVersion>> GetDefaultByRunTypeOrFallbackAsync(
        string runType, DateTime asOf, CancellationToken ct = default);

    /// <summary>
    /// C-5/D-2 引用关系检查（3号位，2026-10-17）：
    /// 反查「仍被合法可供新Run使用的已发布策略包版本」的引用。
    /// 语义：引用 v.[RuleSetVersionId]=@ReferencedRuleSetVersionId 或 v.[ParameterSetVersionId]=@ReferencedParameterSetVersionId，
    /// 且 v.[Status]='PUBLISHED'、父 StrategyProfile p.[IsActive]=1（合法可新Run资格，镜像 <see cref="GetDefaultByRunTypeAsync"/> 判定）。
    /// 返回 IReadOnlyList 而非单对象（红线 #4）；生效窗口（EffectiveFrom/EffectiveTo）过滤由 Application 层完成。
    /// 停用哪族传哪族（二选一）；被停用版本仍被合法可新Run策略包引用时，调用方须阻断停用（0号位 D-2 §4.2 方案a）。
    /// </summary>
    System.Threading.Tasks.Task<IReadOnlyList<StrategyProfileVersion>> GetReferencingPublishedVersionsAsync(
        long? referencedRuleSetVersionId,
        long? referencedParameterSetVersionId,
        CancellationToken ct = default);

    /// <summary>
    /// R1 删除 FK 防护（2026-10-25）：统计引用任一给定 RuleSetVersionId 的策略包版本行数（**不区分状态**——
    /// 物理删除该版本会连带 FK 违约，故任何引用都应阻断删除）。返回 0=可安全删，&gt;0=被引用须拒绝。
    /// 传入空列表返回 0。Dapper `IN @Ids` 多值。
    /// </summary>
    System.Threading.Tasks.Task<int> CountReferencingRuleSetVersionAsync(
        IReadOnlyList<long> ruleSetVersionIds,
        CancellationToken ct = default);

    /// <summary>
    /// 参数集删除 FK 防护（2026-10-26 同 R1 口径）：统计引用任一给定 ParameterSetVersionId 的策略包版本行数
    /// （StrategyProfileVersion.ParameterSetVersionId FK；**不区分状态**——物理删除该参数集版本会连带 FK 违约，
    /// 故任何引用都应阻断删除）。返回 0=可安全删，&gt;0=被引用须拒绝。传入空列表返回 0。Dapper `IN @Ids` 多值。
    /// </summary>
    System.Threading.Tasks.Task<int> CountReferencingParameterSetVersionAsync(
        IReadOnlyList<long> parameterSetVersionIds,
        CancellationToken ct = default);
}
