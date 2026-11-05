using System;
using System.Collections.Generic;
using System.Linq;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **R10-O01 验收**（1号位 2026-10-31，应 0号位
/// 《致1号位_二次回执完整审核回复_v3.0_20261031_拟发.md》`:9`／`:18`／`:22`）。
///
/// <para>裁决要的：「同 Domain 逐客户 OrderId 聚合、最终 Stage 全量完工后以原始 DATE 交期比较的
/// 第三层候选比较；先提供最小比较器与不依赖 DB 的 Unit」。</para>
///
/// <para>本类<b>纯内存</b>：不连库、不起 Solver、不读系统时钟。</para>
///
/// <para>重点反证方向（都是「写反了就会红」型）：</para>
/// <list type="bullet">
///   <item>分母是<b>订单张数</b>不是需求条数；</item>
///   <item>履约时刻取<b>根需求</b>末端，<b>不是</b>所有下阶 Task 最大结束时间；</item>
///   <item>DATE 交期判据是 `&lt;= 当日最后一刻`（次日 00:00 排他），恰好等于最后一刻要判<b>按期</b>；</item>
///   <item>不跨 Domain 合并分母；</item>
///   <item>不写死 100%、未达目标不抛不中断。</item>
/// </list>
/// </summary>
public class OrderOnTimeRateEvaluatorTests
{
    private const string Dom = "DOM";

    /// <summary>客户 DATE 交期：2026-09-11（载入后 00:00:00）。</summary>
    private static readonly DateTime DueDate = new(2026, 9, 11);

    /// <summary>该交期日的最后一刻 = 次日 00:00 前一个 tick。</summary>
    private static readonly DateTime DueDayLastMoment = new DateTime(2026, 9, 12).AddTicks(-1);

    // ─────────────────────────── 一、分母按「订单张数」 ───────────────────────────

    [Fact(DisplayName = "R10-O01｜一张订单挂 3 条需求 ⇒ 分母仍是 1 张")]
    public void MultipleDemandsOfSameOrder_CountAsOneOrder()
    {
        var rate = Eval(
            new[]
            {
                D("D1", orderId: 9001, at: new DateTime(2026, 9, 10, 8, 0, 0)),
                D("D2", orderId: 9001, at: new DateTime(2026, 9, 10, 9, 0, 0)),
                D("D3", orderId: 9001, at: new DateTime(2026, 9, 10, 10, 0, 0)),
            },
            Dues((9001, DueDate)));

        Assert.Equal(1, rate.JudgedOrderCount);
        Assert.Equal(1, rate.OnTimeOrderCount);
        Assert.Equal(100m, rate.OnTimeRatePercent);
        Assert.Single(rate.Orders);
        Assert.Equal(3, rate.Orders[0].DemandKeys.Count);
    }

    [Fact(DisplayName = "R10-O01｜一单按期一单延期 ⇒ 50%（不是按需求条数算）")]
    public void OneOnTimeOneLate_YieldsFiftyPercent()
    {
        var rate = Eval(
            new[]
            {
                D("D1", orderId: 1, at: new DateTime(2026, 9, 10, 8, 0, 0)),    // 按期
                D("D2", orderId: 2, at: new DateTime(2026, 9, 12, 3, 0, 0)),    // 延期
            },
            Dues((1, DueDate), (2, DueDate)));

        Assert.Equal(2, rate.JudgedOrderCount);
        Assert.Equal(1, rate.OnTimeOrderCount);
        Assert.Equal(50m, rate.OnTimeRatePercent);
    }

    [Fact(DisplayName = "R10-O01｜5 张订单 3 张按期 ⇒ 60%，逐张判定可追溯")]
    public void FiveOrdersThreeOnTime_YieldsSixtyPercent()
    {
        var onTime = new DateTime(2026, 9, 10, 12, 0, 0);
        var late = new DateTime(2026, 9, 12, 1, 0, 0);
        var rate = Eval(
            new[]
            {
                D("D1", 1, onTime), D("D2", 2, onTime), D("D3", 3, onTime),
                D("D4", 4, late), D("D5", 5, late),
            },
            Dues((1, DueDate), (2, DueDate), (3, DueDate), (4, DueDate), (5, DueDate)));

        Assert.Equal(5, rate.JudgedOrderCount);
        Assert.Equal(3, rate.OnTimeOrderCount);
        Assert.Equal(60m, rate.OnTimeRatePercent);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, rate.Orders.Select(o => o.OrderId).ToArray());
        Assert.False(rate.Orders.Single(o => o.OrderId == 4).IsOnTime);
    }

    [Fact(DisplayName = "R10-O01｜同一需求被重复传入 ⇒ 只算一次，不虚增分母")]
    public void DuplicateDemandKey_IsCountedOnce()
    {
        var at = new DateTime(2026, 9, 10, 8, 0, 0);
        var rate = Eval(
            new[]
            {
                D("D1", 1, at),
                D("D1", 1, at),               // 重复
                D("D2", 2, new DateTime(2026, 9, 12, 1, 0, 0)),
            },
            Dues((1, DueDate), (2, DueDate)));

        Assert.Equal(2, rate.JudgedOrderCount);
        Assert.Equal(2, rate.Orders.Count);
        Assert.Single(rate.Orders.Single(o => o.OrderId == 1).DemandKeys);
    }

    // ─────────────── 二、履约时刻取根需求末端，不用所有下阶 Task 最大结束时间 ───────────────

    [Fact(DisplayName = "R10-O01｜有根需求 ⇒ 取根末端；子需求更晚也不算延期（:18「不用所有下阶 Task 最大结束时间」）")]
    public void RootDemandBasis_IgnoresLaterSubDemandEnd()
    {
        var rate = Eval(
            new[]
            {
                D("ROOT", 1, new DateTime(2026, 9, 10, 12, 0, 0), root: true),   // 根：交期日内
                D("SUB", 1, new DateTime(2026, 9, 12, 3, 0, 0)),                  // 子件：次日 03:00
            },
            Dues((1, DueDate)));

        var order = rate.Orders.Single();
        Assert.Equal(OrderOnTimeRateEvaluator.FulfilmentBasis.RootDemand, order.Basis);
        Assert.Equal(new DateTime(2026, 9, 10, 12, 0, 0), order.FulfilmentTime);
        Assert.True(order.IsOnTime, "取根需求末端 ⇒ 按期；若误取全需求 max（09-12 03:00）会被判延期");
    }

    [Fact(DisplayName = "R10-O01｜无根标记 ⇒ 回落取全需求 max，且 Basis 留痕可见")]
    public void NoRootFlag_FallsBackToAllDemandsMax()
    {
        var rate = Eval(
            new[]
            {
                D("A", 1, new DateTime(2026, 9, 10, 12, 0, 0)),
                D("B", 1, new DateTime(2026, 9, 12, 3, 0, 0)),
            },
            Dues((1, DueDate)));

        var order = rate.Orders.Single();
        Assert.Equal(OrderOnTimeRateEvaluator.FulfilmentBasis.AllDemandsFallback, order.Basis);
        Assert.Equal(new DateTime(2026, 9, 12, 3, 0, 0), order.FulfilmentTime);
        Assert.False(order.IsOnTime, "回落口径取 max ⇒ 次日 03:00 ⇒ 延期");
    }

    [Fact(DisplayName = "R10-O01｜多条根需求 ⇒ 取根需求之间的最大值")]
    public void MultipleRoots_TakesMaxAmongRoots()
    {
        var rate = Eval(
            new[]
            {
                D("R1", 1, new DateTime(2026, 9, 10, 8, 0, 0), root: true),
                D("R2", 1, new DateTime(2026, 9, 11, 20, 0, 0), root: true),   // 交期日内，更晚
                D("S1", 1, new DateTime(2026, 9, 14, 8, 0, 0)),                 // 子件远晚
            },
            Dues((1, DueDate)));

        var order = rate.Orders.Single();
        Assert.Equal(new DateTime(2026, 9, 11, 20, 0, 0), order.FulfilmentTime);
        Assert.True(order.IsOnTime);
    }

    // ─────────────────────────── 三、Fail Closed：缺件 / 时间不可信 ───────────────────────────

    [Fact(DisplayName = "R10-O01｜订单有需求未完成 ⇒ 整单不按期（哪怕根需求已完工）")]
    public void MissingSubDemandCompletion_MakesOrderLate()
    {
        var rate = Eval(
            new[]
            {
                D("ROOT", 1, new DateTime(2026, 9, 10, 12, 0, 0), root: true),
                D("SUB", 1, at: null),                                            // 子件没排下
            },
            Dues((1, DueDate)));

        var order = rate.Orders.Single();
        Assert.False(order.IsOnTime);
        Assert.Equal(new[] { "SUB" }, order.UnfulfilledDemandKeys);
        Assert.Equal(0, rate.OnTimeOrderCount);
    }

    [Fact(DisplayName = "R10-O01｜完成时间不可信（CompletionTimeCertain=false）⇒ 不得当承诺，整单不按期")]
    public void UncertainCompletionTime_IsNotAccepted()
    {
        var rate = Eval(
            new[] { D("D1", 1, new DateTime(2026, 9, 10, 12, 0, 0), certain: false) },
            Dues((1, DueDate)));

        var order = rate.Orders.Single();
        Assert.False(order.IsOnTime);
        Assert.Equal(new[] { "D1" }, order.UnfulfilledDemandKeys);
    }

    // ─────────────────────────── 四、DATE 交期：次日 00:00 排他上界 ───────────────────────────

    [Theory(DisplayName = "R10-O01｜交期日整天算按期，次日 00:00 起算延期")]
    [InlineData(2026, 9, 11, 0, 0, 0, true)]        // 交期日 00:00
    [InlineData(2026, 9, 11, 18, 0, 0, true)]       // 交期日 18:00
    [InlineData(2026, 9, 11, 23, 59, 59, true)]     // 交期日 23:59:59
    [InlineData(2026, 9, 12, 0, 0, 0, false)]       // 次日 00:00 ⇒ 延期
    [InlineData(2026, 9, 12, 0, 0, 1, false)]       // 次日 00:00:01 ⇒ 延期
    [InlineData(2026, 9, 10, 9, 0, 0, true)]        // 提前完成
    public void DateDueDate_Verdict(int y, int mo, int d, int h, int mi, int s, bool expectOnTime)
    {
        var rate = Eval(new[] { D("D1", 1, new DateTime(y, mo, d, h, mi, s)) }, Dues((1, DueDate)));

        Assert.Equal(expectOnTime, rate.Orders.Single().IsOnTime);
    }

    [Fact(DisplayName = "R10-O01｜履约恰好等于交期日最后一刻 ⇒ 按期（判据必须是 <= 不能写成 <）")]
    public void CompletionExactlyAtLastMoment_IsOnTime()
    {
        var rate = Eval(new[] { D("D1", 1, DueDayLastMoment) }, Dues((1, DueDate)));

        var order = rate.Orders.Single();
        Assert.Equal(DueDayLastMoment, order.JudgementBound);
        Assert.Equal(DueDayLastMoment, order.FulfilmentTime);
        Assert.True(order.IsOnTime, "写成 `< 上界` 会在这里红");
    }

    [Fact(DisplayName = "R10-O01｜履约越过最后一刻 1 tick ⇒ 延期")]
    public void CompletionOneTickAfterBound_IsLate()
    {
        var rate = Eval(new[] { D("D1", 1, DueDayLastMoment.AddTicks(1)) }, Dues((1, DueDate)));

        var order = rate.Orders.Single();
        Assert.False(order.IsOnTime);
        Assert.True(order.LatenessHours >= 0m);
    }

    [Fact(DisplayName = "R10-O01｜延期小时数以判定期上界为基准（09-12 03:00 ⇒ 约 3 小时）")]
    public void LatenessHours_IsMeasuredFromJudgementBound()
    {
        var rate = Eval(new[] { D("D1", 1, new DateTime(2026, 9, 12, 3, 0, 0)) }, Dues((1, DueDate)));

        Assert.Equal(3.0m, Math.Round(rate.Orders.Single().LatenessHours, 4));
    }

    // ─────────────────────────── 五、不跨 Domain 合并分母 ───────────────────────────

    [Fact(DisplayName = "R10-O01｜两个 Domain 各自 1 单（一按期一延期）⇒ 各自 100% / 0%，不合成 50%")]
    public void TwoDomains_AreNotMerged()
    {
        var map = OrderOnTimeRateEvaluator.EvaluateByDomain(
            new[]
            {
                D("A1", 1, new DateTime(2026, 9, 10, 8, 0, 0), domain: "DOM-A"),
                D("B1", 2, new DateTime(2026, 9, 12, 8, 0, 0), domain: "DOM-B"),
            },
            Dues((1, DueDate), (2, DueDate)),
            targetPercent: null);

        Assert.Equal(2, map.Count);
        Assert.Equal(100m, map["DOM-A"].OnTimeRatePercent);
        Assert.Equal(0m, map["DOM-B"].OnTimeRatePercent);
        Assert.Equal(1, map["DOM-A"].JudgedOrderCount);
        Assert.Equal(1, map["DOM-B"].JudgedOrderCount);
    }

    [Fact(DisplayName = "R10-O01｜Domain 字典按键序数排序 ⇒ 同一输入恒得同一顺序")]
    public void DomainResults_AreOrdinalSorted()
    {
        var map = OrderOnTimeRateEvaluator.EvaluateByDomain(
            new[]
            {
                D("C1", 3, new DateTime(2026, 9, 10, 8, 0, 0), domain: "DOM-C"),
                D("A1", 1, new DateTime(2026, 9, 10, 8, 0, 0), domain: "DOM-A"),
                D("B1", 2, new DateTime(2026, 9, 10, 8, 0, 0), domain: "DOM-B"),
            },
            Dues((1, DueDate), (2, DueDate), (3, DueDate)),
            targetPercent: null);

        Assert.Equal(new[] { "DOM-A", "DOM-B", "DOM-C" }, map.Keys.ToArray());
    }

    // ─────────────────────────── 六、目标门槛：不写死 100%、未达标不中断 ───────────────────────────

    [Fact(DisplayName = "R10-O01｜目标 90、实际 100 ⇒ 达标")]
    public void Target_Ninety_IsMet()
    {
        var rate = Eval(new[] { D("D1", 1, new DateTime(2026, 9, 10, 8, 0, 0)) }, Dues((1, DueDate)), 90);

        Assert.Equal(100m, rate.OnTimeRatePercent);
        Assert.True(rate.TargetMet);
    }

    [Fact(DisplayName = "R10-O01｜目标 90、实际 80 ⇒ 未达标，但仍给出完整读数（不抛不中断）")]
    public void Target_NotMet_StillReturnsBestPlan()
    {
        var onTime = new DateTime(2026, 9, 10, 8, 0, 0);
        var late = new DateTime(2026, 9, 12, 8, 0, 0);
        var rate = Eval(
            new[]
            {
                D("D1", 1, onTime), D("D2", 2, onTime), D("D3", 3, onTime), D("D4", 4, onTime),
                D("D5", 5, late),
            },
            Dues((1, DueDate), (2, DueDate), (3, DueDate), (4, DueDate), (5, DueDate)),
            target: 90);

        Assert.Equal(80m, rate.OnTimeRatePercent);
        Assert.False(rate.TargetMet);
        Assert.Equal(5, rate.JudgedOrderCount);      // 读数照样齐全
    }

    [Fact(DisplayName = "R10-O01｜目标缺省（null）⇒ TargetMet 为 null，不臆造成 100%")]
    public void NullTarget_DoesNotAssumeHundred()
    {
        var rate = Eval(new[] { D("D1", 1, new DateTime(2026, 9, 12, 8, 0, 0)) }, Dues((1, DueDate)));

        Assert.Null(rate.TargetPercent);
        Assert.Null(rate.TargetMet);
        Assert.Equal(0m, rate.OnTimeRatePercent);
    }

    [Fact(DisplayName = "R10-O01｜目标 100 且实际 100 ⇒ 达标（不写死但也不禁止 100）")]
    public void Target_Hundred_IsAllowed()
    {
        var rate = Eval(new[] { D("D1", 1, new DateTime(2026, 9, 10, 8, 0, 0)) }, Dues((1, DueDate)), 100);

        Assert.True(rate.TargetMet);
    }

    // ─────────────────────────── 七、第三层比较器 ───────────────────────────

    [Fact(DisplayName = "R10-O01｜按期率高者胜：50% vs 40% ⇒ 挑战者正")]
    public void Compare_HigherRate_Wins()
    {
        var challenger = Rate(onTime: 50, judged: 100);
        var incumbent = Rate(onTime: 40, judged: 100);

        Assert.True(OrderOnTimeRateEvaluator.CompareOnTimeRate(challenger, incumbent) > 0);
        Assert.True(OrderOnTimeRateEvaluator.CompareOnTimeRate(incumbent, challenger) < 0);
    }

    [Fact(DisplayName = "R10-O01｜完全判平 ⇒ 0，交回既有第 ③ 层规则")]
    public void Compare_Tie_ReturnsZero()
    {
        Assert.Equal(0, OrderOnTimeRateEvaluator.CompareOnTimeRate(Rate(3, 5), Rate(3, 5)));
    }

    [Fact(DisplayName = "R10-O01｜分母不同但分数相同 ⇒ 判平（交叉相乘，无假胜）")]
    public void Compare_EqualFraction_DifferentDenominator_IsTie()
    {
        // 1/2 == 2/4
        Assert.Equal(0, OrderOnTimeRateEvaluator.CompareOnTimeRate(Rate(1, 2), Rate(2, 4)));
    }

    [Fact(DisplayName = "R10-O01｜交叉相乘方向正确：1/3 劣于 2/5")]
    public void Compare_CrossMultiplication_Direction()
    {
        // 1/3 = 0.333…，2/5 = 0.4 ⇒ 挑战者(1/3) 更差
        Assert.True(OrderOnTimeRateEvaluator.CompareOnTimeRate(Rate(1, 3), Rate(2, 5)) < 0);
        Assert.True(OrderOnTimeRateEvaluator.CompareOnTimeRate(Rate(2, 5), Rate(1, 3)) > 0);
    }

    [Fact(DisplayName = "R10-O01｜分母为 0 ⇒ 不可比返回 0（不得当成按期率 0%）")]
    public void Compare_EmptyDenominator_IsIncomparable()
    {
        Assert.Equal(0, OrderOnTimeRateEvaluator.CompareOnTimeRate(Rate(0, 0), Rate(3, 5)));
        Assert.Equal(0, OrderOnTimeRateEvaluator.CompareOnTimeRate(Rate(3, 5), Rate(0, 0)));
    }

    [Fact(DisplayName = "R10-O01｜任一侧为 null ⇒ 不可比返回 0，不抛")]
    public void Compare_NullSide_ReturnsZero()
    {
        Assert.Equal(0, OrderOnTimeRateEvaluator.CompareOnTimeRate(null!, Rate(3, 5)));
        Assert.Equal(0, OrderOnTimeRateEvaluator.CompareOnTimeRate(Rate(3, 5), null!));
    }

    [Fact(DisplayName = "R10-O01｜真实读数比较：同 Domain 3 单 → 2 单按期优于 1 单按期")]
    public void Compare_RealReadings()
    {
        var better = Eval(
            new[]
            {
                D("D1", 1, new DateTime(2026, 9, 10, 8, 0, 0)),
                D("D2", 2, new DateTime(2026, 9, 10, 8, 0, 0)),
                D("D3", 3, new DateTime(2026, 9, 12, 8, 0, 0)),
            },
            Dues((1, DueDate), (2, DueDate), (3, DueDate)));

        var worse = Eval(
            new[]
            {
                D("D1", 1, new DateTime(2026, 9, 10, 8, 0, 0)),
                D("D2", 2, new DateTime(2026, 9, 12, 8, 0, 0)),
                D("D3", 3, new DateTime(2026, 9, 12, 8, 0, 0)),
            },
            Dues((1, DueDate), (2, DueDate), (3, DueDate)));

        Assert.True(OrderOnTimeRateEvaluator.CompareOnTimeRate(better, worse) > 0);
    }

    // ─────────────────────── 八、未归属需求 / 订单缺交期 ───────────────────────

    [Fact(DisplayName = "R10-O01｜OrderId 为 null ⇒ 不计入分母，但计数可见")]
    public void NullOrderId_IsExcludedAndCounted()
    {
        var rate = Eval(
            new[]
            {
                D("D1", orderId: null, at: new DateTime(2026, 9, 10, 8, 0, 0)),
                D("D2", orderId: null, at: new DateTime(2026, 9, 12, 8, 0, 0)),
                D("D3", orderId: 1, at: new DateTime(2026, 9, 10, 8, 0, 0)),
            },
            Dues((1, DueDate)));

        Assert.Equal(2, rate.UnattributedDemandCount);
        Assert.Equal(1, rate.JudgedOrderCount);
        Assert.Equal(100m, rate.OnTimeRatePercent);   // 不得被两条 `null` 需求拉低
    }

    [Fact(DisplayName = "R10-O01｜订单缺原始交期 ⇒ 不进分母，Basis=None，计数可见")]
    public void OrderWithoutDueDate_IsExcludedAndCounted()
    {
        var rate = Eval(
            new[]
            {
                D("D1", 1, new DateTime(2026, 9, 10, 8, 0, 0)),
                D("D2", 2, new DateTime(2026, 9, 10, 8, 0, 0)),   // 订单 2 无交期
            },
            Dues((1, DueDate)));

        Assert.Equal(1, rate.OrdersWithoutDueDateCount);
        Assert.Equal(1, rate.JudgedOrderCount);
        Assert.Equal(100m, rate.OnTimeRatePercent);
        Assert.Equal(OrderOnTimeRateEvaluator.FulfilmentBasis.None,
            rate.Orders.Single(o => o.OrderId == 2).Basis);
    }

    [Fact(DisplayName = "R10-O01｜无任何可判订单 ⇒ 按期率为 null，不是 0%")]
    public void NoJudgedOrder_RateIsNull()
    {
        var rate = Eval(new[] { D("D1", null, new DateTime(2026, 9, 10, 8, 0, 0)) }, Dues());

        Assert.Equal(0, rate.JudgedOrderCount);
        Assert.Null(rate.OnTimeRatePercent);
        Assert.Null(rate.TargetMet);
    }

    // ─────────────────────── 九、适配器：从既有 Core DTO 摊平 ───────────────────────

    [Fact(DisplayName = "R10-O01｜适配器按 LogicalDemandKey 等值连接：同物料两条需求不串")]
    public void FromSolveResult_JoinsByDemandKey_NotMaterial()
    {
        var demands = new List<LogicalProductionDemand>
        {
            Demand("D1", orderId: 1, materialId: 500, root: true),
            Demand("D2", orderId: 1, materialId: 500, root: false),   // 同物料
        };
        var completions = new List<DemandCompletionFact>
        {
            Completion("D1", new DateTime(2026, 9, 10, 8, 0, 0)),
            Completion("D2", new DateTime(2026, 9, 10, 9, 0, 0)),
        };

        var facts = OrderOnTimeRateEvaluator.FromSolveResult(demands, completions);

        Assert.Equal(2, facts.Count);
        Assert.Equal(new DateTime(2026, 9, 10, 8, 0, 0), facts[0].CompletionTime);
        Assert.Equal(new DateTime(2026, 9, 10, 9, 0, 0), facts[1].CompletionTime);
    }

    [Fact(DisplayName = "R10-O01｜适配器：缺完成事实 ⇒ CompletionTime=null（丢行会抬高按期率）")]
    public void FromSolveResult_MissingCompletion_BecomesNullNotDropped()
    {
        var demands = new List<LogicalProductionDemand>
        {
            Demand("D1", orderId: 1, materialId: 500, root: true),
            Demand("D2", orderId: 1, materialId: 501, root: false),
        };
        var completions = new List<DemandCompletionFact>
        {
            Completion("D1", new DateTime(2026, 9, 10, 8, 0, 0)),
            // D2 无完成事实
        };

        var facts = OrderOnTimeRateEvaluator.FromSolveResult(demands, completions);

        Assert.Equal(2, facts.Count);                       // 没丢行
        Assert.Null(facts.Single(f => f.LogicalDemandKey == "D2").CompletionTime);

        // 端到端：该订单必须判不按期
        var rate = OrderOnTimeRateEvaluator.EvaluateByDomain(facts, Dues((1, DueDate)), null)["DOM"];
        Assert.False(rate.Orders.Single().IsOnTime);
    }

    [Fact(DisplayName = "R10-O01｜适配器：CompletionTimeCertain 为 null（未标注）按现状当可信，只有显式 false 才不可信")]
    public void FromSolveResult_NullCertainty_TreatedAsCertain()
    {
        var demands = new List<LogicalProductionDemand>
        {
            Demand("D1", orderId: 1, materialId: 500, root: true),
            Demand("D2", orderId: 1, materialId: 501, root: true),
        };
        var completions = new List<DemandCompletionFact>
        {
            Completion("D1", new DateTime(2026, 9, 10, 8, 0, 0), certain: null),
            Completion("D2", new DateTime(2026, 9, 10, 9, 0, 0), certain: false),
        };

        var facts = OrderOnTimeRateEvaluator.FromSolveResult(demands, completions);

        Assert.True(facts.Single(f => f.LogicalDemandKey == "D1").CompletionTimeCertain);
        Assert.False(facts.Single(f => f.LogicalDemandKey == "D2").CompletionTimeCertain);
    }

    [Fact(DisplayName = "R10-O01｜适配器：IsRootDemand 逐字透传")]
    public void FromSolveResult_CarriesRootFlag()
    {
        var demands = new List<LogicalProductionDemand>
        {
            Demand("R", orderId: 7, materialId: 1, root: true),
            Demand("S", orderId: 7, materialId: 2, root: false),
        };
        var completions = new List<DemandCompletionFact>
        {
            Completion("R", new DateTime(2026, 9, 10, 8, 0, 0)),
            Completion("S", new DateTime(2026, 9, 10, 9, 0, 0)),
        };

        var facts = OrderOnTimeRateEvaluator.FromSolveResult(demands, completions);

        Assert.True(facts.Single(f => f.LogicalDemandKey == "R").IsRootDemand);
        Assert.False(facts.Single(f => f.LogicalDemandKey == "S").IsRootDemand);
        Assert.Equal(7L, facts[0].OrderId);
        Assert.Equal("DOM", facts[0].DomainKey);
    }

    // ─────────────────────────── 辅助 ───────────────────────────

    private static OrderOnTimeRateEvaluator.DemandFulfilment D(
        string key,
        long? orderId,
        DateTime? at,
        bool certain = true,
        bool root = false,
        string domain = Dom)
        => new(domain, key, orderId, at, certain, root);

    private static IReadOnlyDictionary<long, DateTime> Dues(params (long OrderId, DateTime Due)[] pairs)
        => pairs.ToDictionary(p => p.OrderId, p => p.Due);

    private static OrderOnTimeRateEvaluator.RateResult Eval(
        IEnumerable<OrderOnTimeRateEvaluator.DemandFulfilment> fulfils,
        IReadOnlyDictionary<long, DateTime> dues,
        int? target = null)
        => OrderOnTimeRateEvaluator.EvaluateByDomain(fulfils, dues, target)[Dom];

    /// <summary>直接构造一个读数，用于比较器单测（不经过订单求值）。</summary>
    private static OrderOnTimeRateEvaluator.RateResult Rate(int onTime, int judged)
        => new(
            DomainKey: Dom,
            Orders: Array.Empty<OrderOnTimeRateEvaluator.OrderVerdict>(),
            JudgedOrderCount: judged,
            OnTimeOrderCount: onTime,
            OnTimeRatePercent: judged == 0 ? null : (decimal)onTime * 100m / judged,
            TargetPercent: null,
            TargetMet: null,
            UnattributedDemandCount: 0,
            OrdersWithoutDueDateCount: 0);

    private static LogicalProductionDemand Demand(string key, long? orderId, int materialId, bool root)
        => new()
        {
            LogicalDemandKey = key,
            PlanVersionId = 1L,
            DomainKey = Dom,
            AllocationSequence = 1,
            DemandKey = key,
            OrderId = orderId,
            MaterialId = materialId,
            FactoryId = 1,
            StartStageCode = "S1",
            NetOutputQty = 1m,
            PlannedProcessQty = 1m,
            RequiredAvailableTime = DueDate,
            DemandSequence = 1,
            IsRootDemand = root,
        };

    private static DemandCompletionFact Completion(string key, DateTime at, bool? certain = true)
        => new()
        {
            LogicalDemandKey = key,
            CompletionTime = at,
            AvailableTime = at,
            CompletionTimeCertain = certain,
        };
}
