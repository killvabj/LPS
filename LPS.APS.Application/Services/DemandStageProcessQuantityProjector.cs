using LPS.APS.Core.Dto;

namespace LPS.APS.Application.Services;

/// <summary>
/// 逐 Stage 计划加工量投影（G-1，1号位 2026-10-25 核证回执 §五 点名；1↔2 契约）。
///
/// 【为什么需要】需求侧只有单个 `LogicalProductionDemand.PlannedProcessQty`，1号位 现把它**全 Stage 复用**
///   ⇒ 一道 Stage 干完、下一道仍按整批量算，多 Stage 的加工负载被**重复计入**；
///   同时 `NetOutputQty / 一次 Yield` 只折了一次损耗，把它当成整条多 Stage 链的总投入同样是错的
///   （《03_R04 2号位 计划良率反算…》§二.3 逐字：「不能把 `NetOutputQty / 一次Yield` 当作整个多 Stage 系统的全部投入计算」）。
///
/// 【口径（逐 Stage 由后向前反算）】
///   终点 Stage（= `RequiredStageCode`，缺省取链尾）的**净产出** = `NetOutputQty`；
///   每段 Stage 的**投入** = 该段净产出 ÷ 该段良率；上一段的净产出 = 下一段的投入。
///   ⇒ 单 Stage 时**式子与舍入**与既有 `PlannedProcessQty` 完全一致（`Math.Round(净量 × 100 ÷ 良率, 4)`）；
///   但「**同值**」只在该物料没有 `(物料编码, StageCode)` 级良率规则、且物料级规则无冲突重复时成立 ——
///   本投影的良率走 `PlanningYieldResolver.Resolve`（Stage 级优先、重复取最低），而需求级 `PlannedProcessQty`
///   走 `SupplyPool.PlanningYieldPercent`（**跳过** Stage 级、命中第一条即返回）。两者取值口径不同 ⇒
///   发布 Stage 级良率规则之日，单 Stage 需求的量变是**预期**，不是回归（1号位 2026-10-25 G-1 回执 §三）。
///   多 Stage 时首段投入 = 净量 ÷ ∏各段良率，这才是整条链的真实投入。
///
/// 【为什么键是 `LogicalDemandKey` 而非 `MaterialId`】同一物料可被切成多个需求片（连续份额 / 拆批 / 跨域），
///   各片良率与起点不同；用物料做键会把不同片的量**合并成一个**。
///
/// 【不做什么】
///   · 不改 `PlannedProcessQty` / `NetOutputQty` / PI 供给 / 已完成 Stage 语义（只**增**一张表）。
///   · 不把需求级量补到缺失的 Stage（<see cref="DomainSolveRequest.StageProcessQuantities"/> 约束 2）；
///     解析不出范围的整个需求直接**不出行**并计数。
///   · 不做材料 BOM 需求量、不做资源工时、不做舍入以外的量化。
/// </summary>
public static class DemandStageProcessQuantityProjector
{
    /// <summary>投影结果（行 + 诊断计数）。</summary>
    public sealed class Result
    {
        /// <summary>逐 Stage 计划加工量（按需求出现顺序、链内由前到后排列，载荷顺序稳定）。</summary>
        public IReadOnlyList<DemandStageProcessQuantity> Rows { get; init; }
            = Array.Empty<DemandStageProcessQuantity>();

        /// <summary>拿到了 Stage 链的需求数（含单 Stage）。</summary>
        public int DemandsWithChain { get; init; }

        /// <summary>无 Stage 链（采购件 / 无 StagePath）⇒ 不出行的需求数。</summary>
        public int DemandsWithoutChain { get; init; }

        /// <summary>`StartStageCode` / `RequiredStageCode` 不在链上、或终点早于起点 ⇒ 解析不出范围的需求数。</summary>
        public int DemandsUnresolvedRange { get; init; }

        /// <summary>净量 ≤ 0 ⇒ 没有加工量的需求数。</summary>
        public int DemandsNonPositiveQty { get; init; }

        /// <summary>链上 Stage 数 &gt; 1 的需求数（这些才是既有「单值全 Stage 复用」真正会算错的对象）。</summary>
        public int MultiStageDemandCount { get; init; }

        /// <summary>
        /// **非续排**需求、但 `StartStageCode` 落在链中段（不是链首）的需求数。
        ///
        /// 【为什么要数】`FillStartStageCodes` 取的是 Routing「无入边源结点」按 **OperationCode 字符串序**的第一个，
        ///   而多源结点的工艺可能有多个阶段（实测 26,332 个物料属「多源结点且阶段不同」）。
        ///   新单本应从链首起算，一旦该步挑到了**后段**，前段 Stage 就既不进 1号位 也不进本表。
        ///   这是 `StartStageCode` 的既有口径问题（1号位 同样按它起点），**不在本轮擅自改**；
        ///   此处只把它数出来、落日志，供 1号位 与 0号位 判断是否要单独立项。
        /// </summary>
        public int NonContinuationMidChainDemandCount { get; init; }

        /// <summary>首段投入 ÷ 净量的最大值（1 = 无放大；&gt;1 即多段良率连乘后的真实放大倍率）。</summary>
        public decimal MaxInflationRatio { get; init; }

        /// <summary>同一物料出现多条链（本次不该发生）⇒ 只取第一条并计数。</summary>
        public int DuplicateChainMaterialCount { get; init; }

        /// <summary>
        /// 命中行数安全上限被截断（<see cref="Rows"/> 只保留到上限为止）。
        ///
        /// 【为什么要单独给真实行数】旧实现在撞上限的当口直接 `break`，日志里的 `Rows` 恒等于上限，
        ///   「到底是刚刚好 50 万、还是 200 万」判不出来 —— 这两种情形对应的处置完全相反
        ///   （前者拆域即可，后者说明需求或链的数据本身有问题）。1号位 2026-10-25 提请补齐。
        /// </summary>
        public bool Truncated { get; init; }

        /// <summary>
        /// **真实应有行数**（撞上限后继续空转计数、不加行）。未截断时恒等于 <see cref="Rows"/>.Count。
        /// </summary>
        public int TotalRowCount { get; init; }

        // ===== 良率来源标记（R04 Y-02／Y-03：「回退正确」之外还要「标记来源」）=====
        // 粒度＝**逐 Stage 行**（一行一次良率取值）；四类合计 = Rows.Count。

        /// <summary>`(物料编码, StageCode)` 精确命中良率的行数。</summary>
        public int YieldStageExactRowCount { get; init; }

        /// <summary>未命中 Stage 级、**回退到物料级默认**的行数（Y-02 要求标记的就是这一类）。</summary>
        public int YieldMaterialDefaultRowCount { get; init; }

        /// <summary>规则存在但**取值越界**（≤0 或 &gt;100）⇒ 按未配置走 100 的行数。</summary>
        public int YieldOutOfRangeRowCount { get; init; }

        /// <summary>**完全缺配**（该物料一条可用规则都没有）⇒ 100 兜底的行数（Y-03）。</summary>
        public int YieldNoConfigRowCount { get; init; }

        /// <summary>
        /// 非精确命中的样例（**只留痕，最多 <see cref="MaxYieldFallbackSamples"/> 条**）。
        /// 用于回答「这一行的 100／95 到底是从哪来的」，不参与计算。
        /// </summary>
        public IReadOnlyList<YieldFallbackSample> YieldFallbackSamples { get; init; }
            = Array.Empty<YieldFallbackSample>();
    }

    /// <summary>良率非精确命中样例行（诊断用）。</summary>
    /// <param name="LogicalDemandKey">需求逻辑键。</param>
    /// <param name="StageCode">Stage 码。</param>
    /// <param name="Level">良率来源等级。</param>
    /// <param name="YieldPercent">实际取到的良率百分数。</param>
    public readonly record struct YieldFallbackSample(
        string LogicalDemandKey, string StageCode, YieldMatchLevel Level, decimal YieldPercent);

    /// <summary>行数安全上限（防异常数据把载荷拖爆；正常量级 = 需求数 × 链长，实测链长 ≤ 5）。</summary>
    public const int MaxRowCount = 500_000;

    /// <summary>良率非精确命中样例的保留条数上限（只留痕，不影响计算）。</summary>
    public const int MaxYieldFallbackSamples = 10;

    /// <summary>数量小数位（与 `PlannedProcessQty` 的 `DECIMAL(18,4)` 同口径）。</summary>
    private const int QtyDecimals = 4;

    /// <summary>
    /// 生成逐 Stage 计划加工量。
    /// </summary>
    /// <param name="demands">本域需求（`LogicalDemandKey` / `MaterialId` / `NetOutputQty` / `StartStageCode` / `RequiredStageCode`）。</param>
    /// <param name="chains">每个物料一条有序 Stage 链（与 `DomainSolveRequest.StageSequenceChains` 同源）。</param>
    /// <param name="yieldPercent">
    /// (物料 Id, StageCode) → <see cref="YieldResolution"/>（百分数 ＋ 来源等级）。返回 ≤0 或 &gt;100 视作**未配置** ⇒ 100（不放大）。
    /// 传 null = 全部 100 ＋ 来源按「缺配」计（无良率事实时的保守口径）。
    /// </param>
    /// <param name="maxRowCount">
    /// 行数安全上限，默认 <see cref="MaxRowCount"/>。**仅供单测把上限缩到个位数**以钉住截断语义；
    /// 生产调用一律不传（保持 50 万）。
    /// </param>
    public static Result Project(
        IReadOnlyList<LogicalProductionDemand>? demands,
        IReadOnlyList<StageSequenceChain>? chains,
        Func<int, string, YieldResolution>? yieldPercent,
        int maxRowCount = MaxRowCount)
    {
        var chainByMaterial = BuildChainIndex(chains, out var duplicateChainMaterials);
        var rows = new List<DemandStageProcessQuantity>();
        var withChain = 0;
        var withoutChain = 0;
        var unresolved = 0;
        var nonPositive = 0;
        var multiStage = 0;
        var midChainNonContinuation = 0;
        var maxRatio = 1m;
        var truncated = false;
        var totalRowCount = 0;
        var yieldStageExact = 0;
        var yieldMaterialDefault = 0;
        var yieldOutOfRange = 0;
        var yieldNoConfig = 0;
        var yieldFallbackSamples = new List<YieldFallbackSample>();

        foreach (var demand in demands ?? Array.Empty<LogicalProductionDemand>())
        {
            // 撞上限后**不 break**：继续空转——量照算、行不加、计数不停，为的是把 `TotalRowCount` 数成真实值。
            if (!chainByMaterial.TryGetValue(demand.MaterialId, out var chain) || chain.Count == 0)
            {
                withoutChain++;
                continue;
            }

            withChain++;

            var startIndex = ResolveStartIndex(chain, demand.StartStageCode);
            var endIndex = ResolveEndIndex(chain, demand.RequiredStageCode);
            if (startIndex < 0 || endIndex < 0 || endIndex < startIndex)
            {
                unresolved++;
                continue;
            }

            var netQty = demand.NetOutputQty;
            if (netQty <= 0m)
            {
                nonPositive++;
                continue;
            }

            var stageQty = endIndex - startIndex + 1;
            if (stageQty > 1) multiStage++;
            if (startIndex > 0 && !demand.IsContinuation) midChainNonContinuation++;

            // 由后向前反算：终点段净产出 = 需求净量；本段投入 = 本段净产出 ÷ 本段良率；上一段净产出 = 本段投入。
            var inputs = new decimal[stageQty];
            var netAtStage = netQty;
            for (var i = stageQty - 1; i >= 0; i--)
            {
                var stageCode = chain[startIndex + i].StageCode;
                var yield = ResolveYield(yieldPercent, demand.MaterialId, stageCode);

                // 本段这一行是否**真的会进 `Rows`**：反算时 `rows.Count` 尚未变化，故行号就是 `rows.Count + i`。
                //   只对真正入行的段记来源与样例，保证「四类合计 = Rows.Count」这条既有不变量在截断时也不被打破。
                var countsTowardRows = rows.Count + i < maxRowCount;

                // 来源标记（R04 Y-02／Y-03）：逐 Stage 行计数，非精确命中留样例
                if (countsTowardRows)
                {
                    switch (yield.Level)
                    {
                        case YieldMatchLevel.STAGE_EXACT:
                            yieldStageExact++;
                            break;
                        case YieldMatchLevel.MATERIAL_DEFAULT:
                            yieldMaterialDefault++;
                            break;
                        case YieldMatchLevel.OUT_OF_RANGE:
                            yieldOutOfRange++;
                            break;
                        default:
                            yieldNoConfig++;
                            break;
                    }

                    if (yield.Level != YieldMatchLevel.STAGE_EXACT
                        && yieldFallbackSamples.Count < MaxYieldFallbackSamples)
                    {
                        yieldFallbackSamples.Add(new YieldFallbackSample(
                            demand.LogicalDemandKey, stageCode, yield.Level, yield.Percent));
                    }
                }

                inputs[i] = Inflate(netAtStage, yield.Percent);
                netAtStage = inputs[i];
            }

            if (netQty > 0m)
            {
                var ratio = inputs[0] / netQty;
                if (ratio > maxRatio) maxRatio = ratio;
            }

            for (var i = 0; i < stageQty; i++)
            {
                totalRowCount++;

                if (rows.Count >= maxRowCount)
                {
                    // 撞上限后**不再加行**，但仍把行数数完（旧实现在这里 `break`，导致 `Rows` 恒等于上限，
                    //   「刚好 50 万」与「实际 200 万」分不出来 —— 1号位 2026-10-25 提请补齐真实行数）。
                    truncated = true;
                    continue;
                }

                rows.Add(new DemandStageProcessQuantity
                {
                    LogicalDemandKey = demand.LogicalDemandKey,
                    StageCode = chain[startIndex + i].StageCode,
                    PlannedProcessQty = inputs[i]
                });
            }
        }

        return new Result
        {
            Rows = rows,
            DemandsWithChain = withChain,
            DemandsWithoutChain = withoutChain,
            DemandsUnresolvedRange = unresolved,
            DemandsNonPositiveQty = nonPositive,
            MultiStageDemandCount = multiStage,
            NonContinuationMidChainDemandCount = midChainNonContinuation,
            MaxInflationRatio = maxRatio,
            DuplicateChainMaterialCount = duplicateChainMaterials,
            Truncated = truncated,
            TotalRowCount = totalRowCount,
            YieldStageExactRowCount = yieldStageExact,
            YieldMaterialDefaultRowCount = yieldMaterialDefault,
            YieldOutOfRangeRowCount = yieldOutOfRange,
            YieldNoConfigRowCount = yieldNoConfig,
            YieldFallbackSamples = yieldFallbackSamples
        };
    }

    /// <summary>物料 → 有序 Stage 链（按 `StageSeq` 升序、空 StageCode 剔除；同物料多链取第一条）。</summary>
    private static Dictionary<int, IReadOnlyList<StageSequenceStep>> BuildChainIndex(
        IReadOnlyList<StageSequenceChain>? chains, out int duplicateChainMaterials)
    {
        duplicateChainMaterials = 0;
        var index = new Dictionary<int, IReadOnlyList<StageSequenceStep>>();

        foreach (var chain in chains ?? Array.Empty<StageSequenceChain>())
        {
            var steps = (chain.Stages ?? Array.Empty<StageSequenceStep>())
                .Where(s => !string.IsNullOrWhiteSpace(s.StageCode))
                .OrderBy(s => s.StageSeq)
                .ToList();
            if (steps.Count == 0) continue;

            if (!index.TryAdd(chain.MaterialId, steps))
                duplicateChainMaterials++;
        }

        return index;
    }

    /// <summary>
    /// 起点下标：`StartStageCode` 为空 ⇒ 链首；非空但不在链上 ⇒ -1（**不猜**，整个需求不出行）。
    /// </summary>
    private static int ResolveStartIndex(IReadOnlyList<StageSequenceStep> chain, string? startStageCode)
    {
        if (string.IsNullOrWhiteSpace(startStageCode)) return 0;
        return IndexOf(chain, startStageCode);
    }

    /// <summary>
    /// 终点下标：`RequiredStageCode` 为空 ⇒ 链尾（既有保守口径「全工艺完成才可供给」）；
    /// 非空但不在链上 ⇒ -1（解析不出终点 ⇒ 不出行，不得退回链尾把量算大）。
    /// </summary>
    private static int ResolveEndIndex(IReadOnlyList<StageSequenceStep> chain, string? requiredStageCode)
    {
        if (string.IsNullOrWhiteSpace(requiredStageCode)) return chain.Count - 1;
        return IndexOf(chain, requiredStageCode);
    }

    private static int IndexOf(IReadOnlyList<StageSequenceStep> chain, string stageCode)
    {
        for (var i = 0; i < chain.Count; i++)
        {
            if (string.Equals(chain[i].StageCode, stageCode, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// 取一件良率**并带来源等级**。回调返回越界值 ⇒ 同旧行为按 100 处理，来源记「越界」。
    /// 回调为 null（无良率事实）⇒ 100 ＋「缺配」，与旧口径数值一致。
    /// </summary>
    private static YieldResolution ResolveYield(
        Func<int, string, YieldResolution>? yieldPercent, int materialId, string stageCode)
    {
        if (yieldPercent is null) return new YieldResolution(PlanningYieldResolver.NoLoss, YieldMatchLevel.NO_CONFIG);

        var res = yieldPercent(materialId, stageCode);
        return res.Percent > 0m && res.Percent <= PlanningYieldResolver.NoLoss
            ? res
            : new YieldResolution(PlanningYieldResolver.NoLoss, YieldMatchLevel.OUT_OF_RANGE);
    }

    /// <summary>投入 = 净产出 × 100 ÷ 良率（与既有 `PlannedProcessQty` 反算同式同舍入）；良率 ≥ 100 不放大。</summary>
    private static decimal Inflate(decimal netQty, decimal yieldPct)
        => yieldPct < 100m
            ? Math.Round(netQty * 100m / yieldPct, QtyDecimals)
            : netQty;
}
