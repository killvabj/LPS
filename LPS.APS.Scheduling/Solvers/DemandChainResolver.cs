using System;
using System.Collections.Generic;
using System.Linq;
using LPS.APS.Core.Dto;

namespace LPS.APS.Scheduling.Solvers;

/// <summary>
/// **逐 Demand 取 Stage 链的唯一入口**（0号位 2026-11-24 v1.2 §3 / 2026-11-25 联合验收）。
///
/// <para>【为什么必须有它】改造前 1号位 有 **5 处**取链点、**3 种口径**：
///   `PhaseOneConstraintBuilder.BuildStageChainIndex`(last-wins)／`BuildReachableStages`(**并集**)／
///   `StageTimingNodeBuilder`(last-wins)／`PhaseTwoInitialScheduler` 两处(first-wins)。
///   同一物料在两次计算里可能走不同链，且**不报错**。本类把这 5 处收敛为同一条规则。</para>
///
/// <para>【取链优先级】（与 2号位 2026-11-25 答复 §Q2 逐条一致）</para>
/// <list type="number">
///   <item>有 <c>DemandStageChainBindings</c> 行：
///     <list type="bullet">
///       <item><c>RESOLVED</c> / <c>PARENT_EDGE_SCOPED</c> / <c>UNVERIFIED_IDENTITY</c> ⇒ 按 <c>ChainRefId</c> 取定义；</item>
///       <item><c>NO_CHAIN</c> / <c>NO_ANCHOR</c> / <c>CONFLICT</c> ⇒ **无链，且不回退旧载体**；</item>
///       <item>同一需求多条绑定且 <c>ChainRefId</c> 不一致 ⇒ **歧义，无链**（不猜、不任取）。</item>
///     </list>
///   </item>
///   <item>**无绑定行**：
///     <list type="bullet">
///       <item>本请求**完全没有**新契约（老载荷 / 单测）⇒ 回落 <c>StageSequenceChains[MaterialId]</c>（合法兼容边界）；</item>
///       <item>本请求**已带**新契约（链目录或绑定任一非空）⇒ <c>BindingAbsent</c>：**不回落**、无链
///         （0号位 2026-11-25 v1.2 DTO-08「不允许新合同缺失时静默旧字段兜底」）。</item>
///     </list>
///   </item>
///   <item>两者皆无 ⇒ 无链。</item>
/// </list>
///
/// <para>⚠ 「无绑定行」与「有绑定但 NO_CHAIN」**语义不同**：前者是载体未投影（兼容期），后者是
///   2号位 明确判定本需求本次没有可用链。二者都不得用别的需求的链顶替。</para>
/// </summary>
internal static class DemandChainResolver
{
    /// <summary>取链结果来源（供诊断；不改变判定）。</summary>
    internal enum ChainSource
    {
        /// <summary>按逐需求绑定取到。</summary>
        Binding = 0,
        /// <summary>无绑定行，回落物料级兼容载体。</summary>
        LegacyMaterial = 1,
        /// <summary>有绑定但状态为 NO_CHAIN / NO_ANCHOR / CONFLICT（**不回退**）。</summary>
        BindingNoChain = 2,
        /// <summary>绑定指向的 ChainRefId 在链目录里找不到（契约破损）。</summary>
        BindingDangling = 3,
        /// <summary>同一需求多条绑定且链不一致（歧义）。</summary>
        BindingAmbiguous = 4,
        /// <summary>既无绑定也无可兼容的物料级链。</summary>
        None = 5,
        /// <summary>
        /// **新契约在场**（本请求已携带 `StageChainDefinitions` / `DemandStageChainBindings`），
        /// 但本需求**没有绑定行** ⇒ 无链，且**不回落** `StageSequenceChains`。
        ///
        /// <para>0号位 2026-11-25 v1.2 DTO-08 逐字：「缺链/缺绑定/悬空引用/冲突/非法 Stage 厂、
        ///   合法旧版兼容边界，**显式诊断**，**不允许新合同缺失时静默旧字段兜底**。」</para>
        ///
        /// <para>与 <see cref="LegacyMaterial"/> 的区别即「载体未投影（合法兼容）」与
        ///   「新契约已生效但本需求漏绑定（契约缺口）」——后者必须暴露，不能被旧载体掩盖。</para>
        /// </summary>
        BindingAbsent = 6
    }

    /// <summary>预建索引：每轮求解建一次，5 处共用。</summary>
    internal sealed class Index
    {
        internal Index(
            Dictionary<string, IReadOnlyList<DemandStageChainBinding>> bindingByDemand,
            Dictionary<string, StageChainDefinition> defByRefId,
            Dictionary<int, IReadOnlyList<StageSequenceStep>> legacyByMaterial,
            bool hasNewContract)
        {
            BindingByDemand = bindingByDemand;
            DefByRefId = defByRefId;
            LegacyByMaterial = legacyByMaterial;
            HasNewContract = hasNewContract;
        }

        internal Dictionary<string, IReadOnlyList<DemandStageChainBinding>> BindingByDemand { get; }
        internal Dictionary<string, StageChainDefinition> DefByRefId { get; }
        internal Dictionary<int, IReadOnlyList<StageSequenceStep>> LegacyByMaterial { get; }

        /// <summary>
        /// 本请求是否**已携带新契约**（链目录或绑定任一非空）。
        /// <c>false</c> = 老载荷 ⇒ 回落旧载体是**合法兼容边界**；
        /// <c>true</c>  = 新契约已生效 ⇒ 缺绑定是**契约缺口**，不得被旧载体掩盖。
        /// </summary>
        internal bool HasNewContract { get; }
    }

    /// <summary>建索引（O(n)，纯内存）。空载荷 ⇒ 三个字典都为空，行为等价改造前。</summary>
    internal static Index BuildIndex(DomainSolveRequest request)
    {
        var bindingByDemand = new Dictionary<string, IReadOnlyList<DemandStageChainBinding>>(StringComparer.Ordinal);
        if (request.DemandStageChainBindings is { Count: > 0 } bindings)
        {
            foreach (var group in bindings
                         .Where(b => b is not null && !string.IsNullOrEmpty(b.LogicalDemandKey))
                         .GroupBy(b => b.LogicalDemandKey, StringComparer.Ordinal))
            {
                // 按 BindingKey 定序，保证同输入同结果。
                bindingByDemand[group.Key] = group
                    .OrderBy(b => b.BindingKey ?? string.Empty, StringComparer.Ordinal)
                    .ToList();
            }
        }

        var defByRefId = new Dictionary<string, StageChainDefinition>(StringComparer.Ordinal);
        if (request.StageChainDefinitions is { Count: > 0 } defs)
        {
            foreach (var d in defs)
            {
                if (d is null || string.IsNullOrEmpty(d.ChainRefId)) continue;
                defByRefId[d.ChainRefId] = d;
            }
        }

        var legacyByMaterial = new Dictionary<int, IReadOnlyList<StageSequenceStep>>();
        if (request.StageSequenceChains is { Count: > 0 } chains)
        {
            foreach (var c in chains)
            {
                if (c?.Stages is null || c.Stages.Count == 0) continue;
                legacyByMaterial[c.MaterialId] = c.Stages;
            }
        }

        var hasNewContract = bindingByDemand.Count > 0 || defByRefId.Count > 0;
        return new Index(bindingByDemand, defByRefId, legacyByMaterial, hasNewContract);
    }

    /// <summary>
    /// 取该需求的链（**按 StageSeq 升序**，已由载体保证）。
    /// 返回 false ⇒ 无链；<paramref name="source"/> 给出原因，供诊断文本使用。
    /// </summary>
    internal static bool TryResolve(
        Index index,
        LogicalProductionDemand demand,
        out IReadOnlyList<StageSequenceStep> stages,
        out ChainSource source)
    {
        stages = Array.Empty<StageSequenceStep>();

        if (index.BindingByDemand.TryGetValue(demand.LogicalDemandKey, out var rows) && rows.Count > 0)
        {
            // 多绑定：链必须一致，否则歧义 ⇒ 无链（不任取）。
            string? refId = null;
            var mismatch = false;
            foreach (var b in rows)
            {
                var id = b.ChainRefId ?? string.Empty;
                if (refId is null) refId = id;
                else if (!string.Equals(refId, id, StringComparison.Ordinal)) { mismatch = true; break; }
            }
            if (mismatch)
            {
                source = ChainSource.BindingAmbiguous;
                return false;
            }

            var first = rows[0];
            if (!IsUsableStatus(first.Status))
            {
                source = ChainSource.BindingNoChain;
                return false;
            }

            if (string.IsNullOrEmpty(refId) || !index.DefByRefId.TryGetValue(refId!, out var def)
                || def.Stages is null || def.Stages.Count == 0)
            {
                source = ChainSource.BindingDangling;
                return false;
            }

            stages = def.Stages;
            source = ChainSource.Binding;
            return true;
        }

        // 新契约**已在场**（本请求带了链目录或绑定）但本需求**没有绑定行** ⇒ 契约缺口，**不回落**旧载体。
        //   0号位 2026-11-25 v1.2 DTO-08：「不允许新合同缺失时静默旧字段兜底」。
        //   ⚠ 只在**整个请求都没带新契约**时才走下面的兼容回落（老载荷/单测）。
        if (index.HasNewContract)
        {
            source = ChainSource.BindingAbsent;
            return false;
        }

        // 无绑定行 ⇒ 兼容回落（老载荷 / 单测 / 未投影新 DTO 的 Run）。
        if (index.LegacyByMaterial.TryGetValue(demand.MaterialId, out var legacy) && legacy.Count > 0)
        {
            stages = legacy;
            source = ChainSource.LegacyMaterial;
            return true;
        }

        source = ChainSource.None;
        return false;
    }

    /// <summary>
    /// 可用状态 = 链内容已确定（可信度不同，但都指向唯一一条链）。
    /// <c>NO_CHAIN</c> / <c>NO_ANCHOR</c> / <c>CONFLICT</c> **一律不可用**，且不得回落旧载体。
    /// </summary>
    private static bool IsUsableStatus(string? status)
    {
        if (string.IsNullOrEmpty(status)) return false;
        return string.Equals(status, StageChainBindingStatus.Resolved, StringComparison.Ordinal)
            || string.Equals(status, StageChainBindingStatus.ParentEdgeScoped, StringComparison.Ordinal)
            || string.Equals(status, StageChainBindingStatus.UnverifiedIdentity, StringComparison.Ordinal);
    }
}
