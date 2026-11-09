using LPS.APS.Core.Dto;

namespace LPS.APS.Application.Services;

/// <summary>
/// 逐 Stage 良率的**取值来源**（R04 Y-02／Y-03：「回退正确」之外还要「标记来源」）。
///
/// 【用途】只用于**我方诊断与日志**：判定某一行加工量用的良率到底是精确规则、还是回退的物料默认、
///   还是兜底 100。**不进 1↔2 载荷**（`DemandStageProcessQuantity` 仍是
///   `{LogicalDemandKey, StageCode, PlannedProcessQty}` 三列）—— 加字段属契约变更，须 1号位 同意后在 G-1 会签里改。
/// </summary>
public enum YieldMatchLevel
{
    /// <summary>`(物料编码, StageCode)` **精确命中**有效规则。</summary>
    STAGE_EXACT = 1,

    /// <summary>未命中 Stage 级，**回退到物料级默认**（`StageCode` 为空的行）。</summary>
    MATERIAL_DEFAULT = 2,

    /// <summary>存在匹配该键的规则，但取值**全部越界**（≤0 或 &gt;100）⇒ 按未配置处理。</summary>
    OUT_OF_RANGE = 3,

    /// <summary>该物料**没有任何可用规则** ⇒ 100 兜底（不放大）；即「缺配」。</summary>
    NO_CONFIG = 4,

    /// <summary>未命中该物料精确，命中「空 MaterialId（物料编码）＋该 Stage」的通配规则（全物料该 Stage）。</summary>
    STAGE_WILDCARD = 5,
}

/// <summary>良率取值结果：百分数 ＋ 来源等级。</summary>
/// <param name="Percent">良率百分数（0 &lt; p &lt;= 100）。</param>
/// <param name="Level">该值从哪一级取到。</param>
public readonly record struct YieldResolution(decimal Percent, YieldMatchLevel Level);

/// <summary>
/// **分 Stage** 计划良率解析（G-1「逐 Stage 计划加工量」的良率口径；
/// 1号位 2026-10-25 核证回执 §五 点名，参数次序由 3号位 2026-10-23 五项会签回执第 2 项定）。
///
/// 【四级次序】
///   ① `(MaterialCode, StageCode)` **精确命中** → 用它；
///   ② 未命中 → 该物料的**物料级默认**（`StageCode` 为空的行）；
///   ③ 仍无 → **全物料 Stage 通配**（`MaterialId`（物料编码）为空的规则，匹配该 `StageCode` 的所有物料，契约升版）；
///   ④ 全无 / 值越界（≤0 或 &gt;100）→ **100**（不放大，与既有缺配兜底同向）；
///   ⑤ 同一键重复发布 → **取最低良率**（3号位 回执逐字「运行时取最低良率兜底」）。
///   注：`MaterialId` 空串 = 全物料该 Stage 通配（不再跳过）；空 M + 空 Stage 的「全局通配」本轮不开放。
///
/// 【来源标记（R04 Y-02／Y-03）】<see cref="ResolveWithSource"/> 除数值外还返回
///   <see cref="YieldMatchLevel"/>，区分「精确命中／回退物料默认／规则越界／完全缺配」四种情形。
///   <see cref="Resolve"/> 是它的薄包装，**取值行为与语义逐字未变**。
///
/// 【与既有需求级口径的关系】`PeggingOrchestrator` 内 `SupplyPool.PlanningYieldPercent(materialCode)`
///   是需求级 `PlannedProcessQty` 的**既有**口径（只认物料级、first-match、实库已落值）；
///   本类只服务 G-1 逐 Stage 表，两者互不影响 —— 改既有口径会连带漂移已落库数量，超出本轮范围。
///
/// 【为什么不并进 SupplyPool】那是 `PeggingOrchestrator` 的私有嵌套类，外部（含单测）不可见；
///   良率取值是本轮唯一会**改变产能负载**的参数，必须能被单测逐条钉住。
/// </summary>
public static class PlanningYieldResolver
{
    /// <summary>无损兜底值（未配置 / 越界）。</summary>
    public const decimal NoLoss = 100m;

    /// <summary>
    /// 解析某物料某 Stage 的计划良率（百分数，0 &lt; p &lt;= 100），**并标记来源等级**。
    /// </summary>
    /// <param name="yields">冻结快照里的良率规则集（可为 null / 空）。</param>
    /// <param name="materialCode">物料**编码**（`PlanningYieldRule.MaterialId` 是编码语义，不是主键 Id）。</param>
    /// <param name="stageCode">大工艺阶段码；空/null = 只要物料级默认。</param>
    public static YieldResolution ResolveWithSource(
        IReadOnlyList<PlanningYieldRule>? yields, string? materialCode, string? stageCode)
    {
        if (yields is not { Count: > 0 } || string.IsNullOrWhiteSpace(materialCode))
            return new YieldResolution(NoLoss, YieldMatchLevel.NO_CONFIG);

        decimal? stageExact = null;          // 优先级1 物料精确 Stage
        decimal? materialDefault = null;     // 优先级2 物料级默认（StageCode 空）
        decimal? wildcardStage = null;       // 优先级3 空 MaterialId（物料编码）＋该 Stage ⇒ 全物料该 Stage 通配
        var sawOutOfRange = false;
        var sawWildcardStageOutOfRange = false;
        var hasStageCode = !string.IsNullOrWhiteSpace(stageCode);

        foreach (var rule in yields)
        {
            if (rule is null) continue;

            var isWildcard = string.IsNullOrWhiteSpace(rule.MaterialId);   // 空 MaterialId = 全物料通配（契约升版）
            if (isWildcard)
            {
                // 通配规则：只认带该 Stage 的全物料（MaterialId 空串 + StageCode 空 ⇒ 无范围通配，忽略）
                var ruleStageLevel = !string.IsNullOrWhiteSpace(rule.StageCode);
                if (!ruleStageLevel) continue;   // 空 M + 空 Stage 的「全局通配」本轮不开放，避免隐式放大范围
                if (!(hasStageCode && string.Equals(rule.StageCode, stageCode, StringComparison.OrdinalIgnoreCase)))
                    continue;                    // 别的 Stage 的通配，与本次无关
                if (rule.YieldPercent <= 0m || rule.YieldPercent > NoLoss)
                {
                    sawWildcardStageOutOfRange = true;
                    continue;
                }
                if (wildcardStage is null || rule.YieldPercent < wildcardStage.Value)
                    wildcardStage = rule.YieldPercent;
                continue;
            }

            if (!string.Equals(rule.MaterialId, materialCode, StringComparison.OrdinalIgnoreCase))
                continue;

            var isStageLevel = !string.IsNullOrWhiteSpace(rule.StageCode);
            if (isStageLevel
                && !(hasStageCode && string.Equals(rule.StageCode, stageCode, StringComparison.OrdinalIgnoreCase)))
                continue;   // 别的 Stage 的规则，与本次无关

            if (rule.YieldPercent <= 0m || rule.YieldPercent > NoLoss)
            {
                sawOutOfRange = true;   // 命中键但取值越界 ⇒ 记「遇到过」，继续找有效行
                continue;
            }

            if (isStageLevel)
            {
                if (stageExact is null || rule.YieldPercent < stageExact.Value)
                    stageExact = rule.YieldPercent;
            }
            else
            {
                if (materialDefault is null || rule.YieldPercent < materialDefault.Value)
                    materialDefault = rule.YieldPercent;
            }
        }

        // 优先级：物料精确 Stage > 物料级默认 > Stage 通配（全物料）> 越界兜底 > 缺配兜底
        if (stageExact is not null)
            return new YieldResolution(stageExact.Value, YieldMatchLevel.STAGE_EXACT);
        if (materialDefault is not null)
            return new YieldResolution(materialDefault.Value, YieldMatchLevel.MATERIAL_DEFAULT);
        if (wildcardStage is not null)
            return new YieldResolution(wildcardStage.Value, YieldMatchLevel.STAGE_WILDCARD);
        if (sawOutOfRange || sawWildcardStageOutOfRange)
            return new YieldResolution(NoLoss, YieldMatchLevel.OUT_OF_RANGE);

        return new YieldResolution(NoLoss, YieldMatchLevel.NO_CONFIG);
    }

    /// <summary>
    /// 解析某物料某 Stage 的计划良率（百分数，0 &lt; p &lt;= 100）。
    /// </summary>
    /// <param name="yields">冻结快照里的良率规则集（可为 null / 空）。</param>
    /// <param name="materialCode">物料**编码**（`PlanningYieldRule.MaterialId` 是编码语义，不是主键 Id）。</param>
    /// <param name="stageCode">大工艺阶段码；空/null = 只要物料级默认。</param>
    public static decimal Resolve(
        IReadOnlyList<PlanningYieldRule>? yields, string? materialCode, string? stageCode)
        => ResolveWithSource(yields, materialCode, stageCode).Percent;
}
