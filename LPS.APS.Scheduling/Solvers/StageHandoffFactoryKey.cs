using System;
using System.Collections.Generic;
using LPS.APS.Core.Dto;

namespace LPS.APS.Scheduling.Solvers;

/// <summary>
/// **交接等待规则的工厂键解析：只认权威执行厂，缺了就报缺，不回退。**
///
/// <para>0号位 2026-11-25 v1.2 §3 逐字：「`StageCode→StageDict.FactoryCode` 权威取得，不能截前缀，
///   **不能缺值回 `Demand.FactoryCode`**。r14421 的回退分支**必须撤除**；缺权威厂应暴露技术未证
///   并阻断受影响需用时间/份额，不捏造可用时间或全域误阻。」</para>
///
/// <para>【唯一权威来源】`StageChainDefinition.Stages[].StageFactoryCode`
///   （2号位 由 `StageDict.StageCode → FactoryCode → FactoryId` 映射后随链下发）。
///   本类**不做**前缀截取、**不做**按资源厂反推、**不做**需求厂兜底。</para>
///
/// <para>【返回 <c>null</c> 的语义】= 判定不了该规则是否命中该需求 ⇒ 调用方
///   **不得应用该条等待**（既不加也不减），并把该需求的需用时间标为**不可信**。
///   这是「暴露技术未证」，不是「静默取一个近似值」。</para>
/// </summary>
internal static class StageHandoffFactoryKey
{
    /// <summary>
    /// 取该需求在该 Stage 的**权威执行厂**；取不到 ⇒ <c>null</c>（不猜、不回落）。
    /// </summary>
    internal static string? Resolve(
        IReadOnlyDictionary<string, Dictionary<string, string>>? stageFactoryByDemandStage,
        LogicalProductionDemand demand,
        string? stageCode)
    {
        if (stageFactoryByDemandStage is null
            || string.IsNullOrEmpty(stageCode)
            || string.IsNullOrEmpty(demand.LogicalDemandKey))
        {
            return null;
        }

        if (!stageFactoryByDemandStage.TryGetValue(demand.LogicalDemandKey, out var byStage)
            || byStage is null
            || !byStage.TryGetValue(stageCode!, out var factory)
            || string.IsNullOrEmpty(factory))
        {
            return null;
        }

        return factory;
    }
}
