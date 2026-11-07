using LPS.APS.Core.Dto;
using System.Text.Json;

namespace LPS.APS.Tests.Fixtures;

/// <summary>
/// Demand Priority Fixture 数据（阶段 C-4）
/// 供 2号位联调使用的示例 FrozenStrategySnapshot
/// </summary>
public static class DemandPriorityFixture
{
    /// <summary>
    /// 标准三层优先级策略：销售订单 > VIP客户 > 生产指示（示例）
    /// </summary>
    /// <remarks>
    /// 2026-11-07 0号位 裁决：DelayStatus / DemandMaturityStatus 归【参数集】（需求保护），不入规则集。
    /// 原夹具段1「紧急延迟订单」以 DelayStatus 命中 —— 现已归 `ProtectDelayed`，规则集不再承载（投影器
    /// fail-closed）。此处改段1 用规则集合法字段 OrderType=SALES_ORDER 命中，保留「可发布示例」语义：
    /// 本夹具必须过得过 <c>DemandPriorityValidator</c>，否则集成测试验的是一份永远发不出去的配置。
    /// 排序字段均用 <c>DueDate</c>（`RemainingTimeHours` 已于 2026-10-20 剔除白名单，运行侧不认）。
    /// </remarks>
    public static FrozenStrategySnapshot GetStandardPrioritySnapshot()
    {
        return new FrozenStrategySnapshot
        {
            StrategyProfileVersionId = 1001,
            RuleSetVersionId = 2001,
            ParameterSetVersionId = 3001,
            FrozenAt = DateTime.Now,
            DemandPriority = new DemandPriorityBlock
            {
                Segments = new List<PrioritySegment>
                {
                    new PrioritySegment
                    {
                        SegmentOrder = 1,
                        SegmentName = "销售订单",
                        IsEnabled = true,
                        MatchConditions = new List<SegmentMatchCondition>
                        {
                            new SegmentMatchCondition { Field = DemandField.OrderType, Operator = ConditionOperator.Equals, Value = "SALES_ORDER" }
                        },
                        SortFields = new List<SegmentSortField>
                        {
                            new SegmentSortField { Field = DemandField.DueDate, Direction = SortDirection.Asc }
                        },
                        StableTieBreakFields = new List<TieBreakField> { TieBreakField.DEMANDKEY }
                    },
                    new PrioritySegment
                    {
                        SegmentOrder = 2,
                        SegmentName = "VIP客户",
                        IsEnabled = true,
                        MatchConditions = new List<SegmentMatchCondition>
                        {
                            new SegmentMatchCondition { Field = DemandField.CustomerTier, Operator = ConditionOperator.Equals, Value = "VIP" }
                        },
                        SortFields = new List<SegmentSortField>
                        {
                            new SegmentSortField { Field = DemandField.DueDate, Direction = SortDirection.Asc }
                        }
                    }
                }
            },
            Lock = new LockBlock(),
            Supply = new SupplyBlock(),
            Procurement = new ProcurementBlock(),
            SolverStrategy = new SolverStrategyBlock(),
            CandidateGuardrail = new CandidateGuardrailBlock()
        };
    }

    public static string ToJson(FrozenStrategySnapshot snapshot)
    {
        return JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
    }
}
