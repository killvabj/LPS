using LPS.APS.Core.Dto;

namespace LPS.APS.Application.Services;

/// <summary>
/// 需求保护资格派生（B4 §三）—— `FrozenStrategySnapshot.Lock` 的**唯一消费点**。
///
/// 【为什么必须有这个类】`ParameterSetVersion.*.$.Lock`（<see cref="LockBlock"/>）自 2026-08-27 起
/// 就随冻结快照装进 <c>snapshot.Lock</c>，但**全仓零读者**：装进去、没人用。于是「需求保护」在配置面
/// 一直是「已配置、有实值、不生效」。本类把 <see cref="ProtectionTriggerParams"/> 的触发条件
/// 落成 <see cref="UpstreamDemand.ProtectionStatus"/>（`PROTECTED` / `UNPROTECTED`），
/// 使其在<b>排序之前</b>成为需求的真实属性（B4 §1.5 / §3.1）。
///
/// 【口径来源】
/// · `01 v1.14 §42.2`：「触发条件全部应由规则引擎配置……一旦触发，保护的是该 Demand 真正必要的供给数量份额。」
/// · `APS_Pegging v1.6 §14.2`：「触发条件由 3号位 Lock Policy 配置」。
/// · B4 合同 v1.0（2号位 2026-10-20 发）§三 四个自动触发 ＋ §3.4 不可评价必须显性。
/// · 3号位 2026-10-20《B4 四项 B 层口径确认回执》：T4 用运行词表；`AllowPmcManualProtection`
///   为**预留位、当前 V1 不生效**；基准时点 `BasisTime` ＝ 本 Run `DataCutoffTime`，**非** `FrozenAt`。
///
/// 【本类不做的事】
/// · **不写库**（B4 §3.1「不新增列、不新增枚举、不写库」）。资格只是内存属性。
/// · **不建份额锁**。锁的创建/退出是 B4 §四/§六，另一条链（`DemandSupplyHardLock`）。
/// · **不实现人工强制保护**（`AllowPmcManualProtection`）。A 层零依据，且 `SupplyKey NOT NULL`
///   使「尚未选供给的需求保护」在表内无法表达（B4 §3.5）⇒ 由调用方记 WARN。
///
/// 【为什么 `PROTECTIONSTATUS` 之前恒 null 却不报错】执行器 <c>KnownFields</c> 内有该字段，
/// 但取值恒 `null` ⇒ 条件求值恒不命中、排序落 `DemandKey ASC` 兜底，**不抛错不告警**。
/// 那是「配置看着生效、实际全线空转」的典型形态（B4 §二 #16）。本类补的正是这个真实赋值。
/// </summary>
internal static class DemandProtectionTriggerEvaluator
{
    /// <summary>受保护（B4 §3.1 值域）。</summary>
    internal const string ProtectedStatus = "PROTECTED";

    /// <summary>未受保护（B4 §3.1 值域）。</summary>
    internal const string UnprotectedStatus = "UNPROTECTED";

    /// <summary>
    /// 诊断码（B4 §3.4）：配置已要求某触发，但本 Run 的需求事实域内**没有任何一个值能命中**。
    /// 用来挡住「配置为真、实际保护 0 条、日志无痕」。
    /// </summary>
    internal const string UnevaluableDiagnosticCode = "PROTECTION_TRIGGER_UNEVALUABLE";

    /// <summary>触发类型（与 <see cref="ProtectionTriggerParams"/> 的字段一一对应，用于诊断归因）。</summary>
    internal enum ProtectionTrigger
    {
        /// <summary>T1：`(CustomerDueDate − BasisTime).TotalHours &lt;= RemainingTimeThresholdHours`。</summary>
        RemainingTimeThreshold = 0,

        /// <summary>T2：`DelayStatus &lt;&gt; 'ON_TIME'`。</summary>
        Delayed = 1,

        /// <summary>T3：`CustomerTier = VipTierValue`。</summary>
        VipTier = 2,

        /// <summary>T4：`OrderType ∈ ProtectedOrderTypes`。</summary>
        OrderTypes = 3,

        /// <summary>T5：`DemandMaturityStatus ∈ MaturityStatusValues`（2026-11-06 补加的成熟度触发）。</summary>
        MaturityStatus = 4,
    }

    /// <summary>
    /// 参与触发判定的需求事实（只取判据需要的五项，不外扩）。
    /// 刻意做成独立结构而不是直接用 <c>OrderPeggingRow</c>：后者是 <c>PeggingOrchestrator</c> 的私有嵌套类型，
    /// 拿不到就写不了单测，而这段逻辑恰恰最需要单测钉住（它决定谁排前面）。
    /// </summary>
    internal readonly record struct DemandFacts(
        DateTime? CustomerDueDate,
        string? DelayStatus,
        string? CustomerTier,
        string? OrderType,
        string? DemandMaturityStatus);

    /// <summary>单条需求的判定结果。</summary>
    /// <param name="IsProtected">是否受保护。</param>
    /// <param name="MatchedTrigger">命中的触发类型；未受保护或无法判定时为 <c>null</c>。</param>
    /// <param name="Gap">无法判定时说明缺哪个事实（**不保护、但要留痕**，B4 §3.1「失败方向」）。</param>
    internal readonly record struct Verdict(
        bool IsProtected,
        ProtectionTrigger? MatchedTrigger,
        string? Gap)
    {
        /// <summary>写入 <see cref="UpstreamDemand.ProtectionStatus"/> 的字符串值。</summary>
        internal string Status => IsProtected ? ProtectedStatus : UnprotectedStatus;
    }

    /// <summary>
    /// 派生单条需求的保护资格。任一触发命中即 `PROTECTED`；全部不命中即 `UNPROTECTED`。
    /// **不按触发条数加权、不设第二套优先级**（B4 §3.2「合成规则」，冻结 §42.2 只要求这一个二值）。
    /// </summary>
    /// <param name="block">冻结快照的 Lock 子块；<c>null</c> 视为「未配置任何触发」。</param>
    /// <param name="facts">需求事实。</param>
    /// <param name="basisTime">
    /// 业务基准时点 ＝ 本 Run 的 `DataCutoffTime`（B4 §3.4／3号位 2026-10-20 回执 #4）。
    /// **不得**传 `FrozenAt`（治理快照装载时刻，不作业务时钟）。
    /// </param>
    /// <exception cref="NotSupportedException">配置使用了尚未接线的 `TriggerConditionGroups`（路线甲 DNF）。</exception>
    internal static Verdict Evaluate(LockBlock? block, in DemandFacts facts, DateTime basisTime)
    {
        var trigger = block?.Trigger;
        if (trigger is null)
        {
            return new Verdict(false, null, null);
        }

        // DNF 触发组：3号位 2026-11-06 新增，`组非空 ⇒ 以条件组判定触发`。
        // 我方尚未接线 ⇒ **fail-closed**（与 `DemandPriorityProjector.ProjectSegment` 对
        // `MatchConditionGroups` 的处理同形）。若静默回退旧布尔簇，配置里写了 DNF 的策略会
        // 「看着生效、实际按另一套判据跑」，且不报错——比直接失败危险得多。
        if (trigger.TriggerConditionGroups is { Count: > 0 })
        {
            throw new NotSupportedException(
                "冻结策略快照 $.Lock.Trigger.TriggerConditionGroups（路线甲 DNF）非空，"
                + "但 2号位 需求保护触发派生尚未接线条件组判定；"
                + "拒绝静默回退旧布尔簇（那会让配置按另一套判据执行且不报错）。"
                + "请先完成 3号位 DNF 触发口径与执行侧接线后再启用。");
        }

        // T1 剩余交期短于阈值
        if (trigger.UseRemainingTimeThreshold)
        {
            if (facts.CustomerDueDate is null)
            {
                // 无法判定 ⇒ 不保护，但要留痕（B4 §3.1）。继续往下判其余触发，不让一个缺值短路全部。
                var gap = "CustomerDueDate 为空 ⇒ T1(RemainingTimeThreshold) 无法判定";
                return EvaluateRest(trigger, facts, basisTime, gap);
            }

            var remainingHours = (facts.CustomerDueDate.Value - basisTime).TotalHours;
            if (remainingHours <= trigger.RemainingTimeThresholdHours)
            {
                return new Verdict(true, ProtectionTrigger.RemainingTimeThreshold, null);
            }
        }

        return EvaluateRest(trigger, facts, basisTime, null);
    }

    /// <summary>
    /// 判 T2~T5（T1 已在 <see cref="Evaluate"/> 里判过）。
    /// 单列出来只为让 T1 的「缺值留痕」不打断其余触发，避免一条缺值把整条需求判死。
    /// </summary>
    private static Verdict EvaluateRest(
        ProtectionTriggerParams trigger, in DemandFacts facts, DateTime basisTime, string? carriedGap)
    {
        var gap = carriedGap;

        // T2 已延期
        if (trigger.ProtectDelayed)
        {
            if (string.IsNullOrWhiteSpace(facts.DelayStatus))
            {
                gap ??= "DelayStatus 为空 ⇒ T2(ProtectDelayed) 无法判定";
            }
            else if (!string.Equals(facts.DelayStatus, "ON_TIME", StringComparison.OrdinalIgnoreCase))
            {
                return new Verdict(true, ProtectionTrigger.Delayed, null);
            }
        }

        // T3 特定客户等级
        if (trigger.ProtectVipTier && !string.IsNullOrWhiteSpace(trigger.VipTierValue))
        {
            if (string.IsNullOrWhiteSpace(facts.CustomerTier))
            {
                gap ??= "CustomerTier 为空 ⇒ T3(ProtectVipTier) 无法判定";
            }
            else if (string.Equals(facts.CustomerTier, trigger.VipTierValue, StringComparison.Ordinal))
            {
                // 精确等值（B4 §3.2 T3／§3.3 值域对齐）；大小写不同即不命中，由 §3.4 诊断暴露。
                return new Verdict(true, ProtectionTrigger.VipTier, null);
            }
        }

        // T4 指定订单类型
        if (trigger.ProtectedOrderTypes is { Count: > 0 })
        {
            if (string.IsNullOrWhiteSpace(facts.OrderType))
            {
                gap ??= "OrderType 为空 ⇒ T4(ProtectedOrderTypes) 无法判定";
            }
            else if (trigger.ProtectedOrderTypes.Contains(facts.OrderType, StringComparer.Ordinal))
            {
                return new Verdict(true, ProtectionTrigger.OrderTypes, null);
            }
        }

        // T5 需求成熟度（2026-11-06 补加；`MaturityStatusValues` 为 IN 集）
        if (trigger.ProtectMaturityStatus && trigger.MaturityStatusValues is { Count: > 0 })
        {
            if (string.IsNullOrWhiteSpace(facts.DemandMaturityStatus))
            {
                gap ??= "DemandMaturityStatus 为空 ⇒ T5(ProtectMaturityStatus) 无法判定";
            }
            else if (trigger.MaturityStatusValues.Contains(facts.DemandMaturityStatus, StringComparer.Ordinal))
            {
                return new Verdict(true, ProtectionTrigger.MaturityStatus, null);
            }
        }

        _ = basisTime; // T2~T5 不依赖基准时点；保留形参是为了将来 T1 拆分时签名不变。
        return new Verdict(false, null, gap);
    }

    /// <summary>
    /// 该触发是否「已配置」（用于 §3.4 诊断：配置了、却一条都没命中）。
    /// </summary>
    internal static bool IsConfigured(ProtectionTriggerParams? trigger, ProtectionTrigger kind) => kind switch
    {
        ProtectionTrigger.RemainingTimeThreshold => trigger?.UseRemainingTimeThreshold == true,
        ProtectionTrigger.Delayed => trigger?.ProtectDelayed == true,
        ProtectionTrigger.VipTier => trigger?.ProtectVipTier == true && !string.IsNullOrWhiteSpace(trigger.VipTierValue),
        ProtectionTrigger.OrderTypes => trigger?.ProtectedOrderTypes is { Count: > 0 },
        ProtectionTrigger.MaturityStatus => trigger?.ProtectMaturityStatus == true
                                            && trigger.MaturityStatusValues is { Count: > 0 },
        _ => false,
    };

    /// <summary>触发类型 → 配置字段名（诊断里要写清楚是哪个开关要求了它）。</summary>
    internal static string Describe(ProtectionTrigger kind) => kind switch
    {
        ProtectionTrigger.RemainingTimeThreshold => "Trigger.UseRemainingTimeThreshold",
        ProtectionTrigger.Delayed => "Trigger.ProtectDelayed",
        ProtectionTrigger.VipTier => "Trigger.ProtectVipTier/VipTierValue",
        ProtectionTrigger.OrderTypes => "Trigger.ProtectedOrderTypes",
        ProtectionTrigger.MaturityStatus => "Trigger.ProtectMaturityStatus/MaturityStatusValues",
        _ => kind.ToString(),
    };

    /// <summary>触发类型 → 判定所依赖的需求字段名（诊断里附「实测去重值」用）。</summary>
    internal static string FactFieldOf(ProtectionTrigger kind) => kind switch
    {
        ProtectionTrigger.RemainingTimeThreshold => "CustomerDueDate",
        ProtectionTrigger.Delayed => "DelayStatus",
        ProtectionTrigger.VipTier => "CustomerTier",
        ProtectionTrigger.OrderTypes => "OrderType",
        ProtectionTrigger.MaturityStatus => "DemandMaturityStatus",
        _ => string.Empty,
    };
}
