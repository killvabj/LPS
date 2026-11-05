using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using Xunit;
using RoutingOperation = LPS.APS.Core.Entities.APS.RoutingOperation;
using OperationResourceEligibility = LPS.APS.Core.Entities.APS.OperationResourceEligibility;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **R10-O02 验收**（1号位 2026-10-31，应 0号位《致1号位_二次回执完整审核回复_v3.0》`:18`／`:10`）。
///
/// <para>裁决原文：「客户DATE业务当日23:59:59，技术以次日00:00<b>排他</b>上界
/// （最终完成 <c>&lt; nextMidnight</c>），不写死100%」；Manual 手工覆盖走已冻结的精准时刻规则。</para>
///
/// <para>本类只打 <see cref="ConstraintContext.ToJudgementBound"/>（纯函数）与
/// <see cref="ConstraintContext.EffectiveDueJudgementBound"/>（带覆盖分流的上下文方法），
/// <b>不连库、不起 Solver</b>。</para>
///
/// <para>0号位 点名要的边界正反例：23:59:59.500 / 次日 00:00 / Setup 归因跨 OFF，全部在 §二、§三。</para>
/// </summary>
public class DueDateExclusiveBoundTests
{
    private static readonly DateTime DueDate = new(2026, 11, 15);   // 客户 DATE 交期：2026-11-15，载入后 00:00:00

    /// <summary>交期日的「最后一刻」= 次日 00:00 前一个 tick。</summary>
    private static DateTime LastMomentOfDueDay => new DateTime(2026, 11, 16).AddTicks(-1);

    // ─────────────────────────── 一、纯函数：边界值怎么算 ───────────────────────────

    [Fact(DisplayName = "R10-O02｜客户 DATE（00:00 值）⇒ 上界 = 交期日 23:59:59.9999999")]
    public void DateOnly_SnapsToLastMomentOfDueDay()
    {
        var bound = ConstraintContext.ToJudgementBound(DueDate, isManualOverride: false);

        Assert.Equal(LastMomentOfDueDay, bound);
        Assert.Equal(new DateTime(2026, 11, 15, 23, 59, 59, 999).AddTicks(9999), bound);
        Assert.True(bound < new DateTime(2026, 11, 16), "上界必须严格早于次日 00:00（排他）");
    }

    [Theory(DisplayName = "R10-O02｜跨月/跨年/闰日都能正确加一天")]
    [InlineData(2026, 12, 31, 2027, 1, 1)]
    [InlineData(2028, 2, 28, 2028, 2, 29)]   // 闰年
    [InlineData(2027, 2, 28, 2027, 3, 1)]    // 平年
    public void DateOnly_CrossesMonthYearAndLeapDay(
        int y, int mo, int d, int ey, int emo, int ed)
    {
        var bound = ConstraintContext.ToJudgementBound(new DateTime(y, mo, d), isManualOverride: false);

        Assert.Equal(new DateTime(ey, emo, ed).AddTicks(-1), bound);
    }

    [Fact(DisplayName = "R10-O02｜Manual 覆盖是精确时刻 ⇒ 上界原样，不延到次日")]
    public void ManualOverride_IsPassedThrough()
    {
        var manual = new DateTime(2026, 11, 15, 12, 0, 0);

        var bound = ConstraintContext.ToJudgementBound(manual, isManualOverride: true);

        Assert.Equal(manual, bound);
    }

    [Fact(DisplayName = "R10-O02｜Manual 覆盖值恰好是 00:00 ⇒ 仍然原样（Manual 优先于「像 DATE」判断）")]
    public void ManualOverride_AtMidnight_StillPassedThrough()
    {
        var manual = new DateTime(2026, 11, 15);   // 手工填了个日期，回落到 00:00

        var bound = ConstraintContext.ToJudgementBound(manual, isManualOverride: true);

        Assert.Equal(manual, bound);
        Assert.NotEqual(LastMomentOfDueDay, bound);
    }

    [Fact(DisplayName = "R10-O02｜非 Manual 但值已带时刻成分 ⇒ 不是 DATE，原样（防 DATETIME2 分支混入）")]
    public void ValueWithTimeComponent_IsNotTreatedAsDate()
    {
        var precise = new DateTime(2026, 11, 15, 8, 30, 0);

        var bound = ConstraintContext.ToJudgementBound(precise, isManualOverride: false);

        Assert.Equal(precise, bound);
    }

    [Fact(DisplayName = "R10-O02｜DateTime.MaxValue（无交期哨兵）⇒ 原样，不溢出")]
    public void MaxValue_IsPassedThrough()
    {
        var bound = ConstraintContext.ToJudgementBound(DateTime.MaxValue, isManualOverride: false);

        Assert.Equal(DateTime.MaxValue, bound);
    }

    // ─────────────────── 二、判定语义：既有 `>` / `<=` 写法下的实际结论 ───────────────────

    [Theory(DisplayName = "R10-O02｜客户 DATE：交期日整天算按期，次日 00:00 起算延期")]
    [InlineData(2026, 11, 15, 0, 0, 0, false)]                       // 交期日 00:00：按期
    [InlineData(2026, 11, 15, 18, 0, 0, false)]                      // 交期日 18:00：按期
    [InlineData(2026, 11, 15, 23, 59, 59, false)]                    // 交期日 23:59:59：按期
    [InlineData(2026, 11, 16, 0, 0, 0, true)]                        // 次日 00:00：延期（排他上界）
    [InlineData(2026, 11, 16, 0, 0, 1, true)]                        // 次日 00:00:01：延期
    [InlineData(2026, 11, 14, 9, 0, 0, false)]                       // 提前完成：按期
    public void DateOnly_Verdict(int y, int mo, int d, int h, int mi, int s, bool expectLate)
    {
        var bound = ConstraintContext.ToJudgementBound(DueDate, isManualOverride: false);
        var completion = new DateTime(y, mo, d, h, mi, s);

        // 调用点沿用的既有写法：完成 > 上界 ⇒ 延期
        Assert.Equal(expectLate, completion > bound);
    }

    [Fact(DisplayName = "R10-O02｜0号位 点名的 23:59:59.500 ⇒ 按期（小数秒被上界覆盖）")]
    public void FractionalSecond_BeforeMidnight_IsOnTime()
    {
        var bound = ConstraintContext.ToJudgementBound(DueDate, isManualOverride: false);
        var completion = new DateTime(2026, 11, 15, 23, 59, 59, 500);

        Assert.True(completion <= bound, "23:59:59.500 必须判按期");
        Assert.False(completion > bound);
    }

    [Fact(DisplayName = "R10-O02｜0号位 点名的次日 00:00.000 ⇒ 延期；00:00.001 也延期")]
    public void MidnightAndJustAfter_AreLate()
    {
        var bound = ConstraintContext.ToJudgementBound(DueDate, isManualOverride: false);

        Assert.True(new DateTime(2026, 11, 16, 0, 0, 0) > bound);
        Assert.True(new DateTime(2026, 11, 16, 0, 0, 0, 1) > bound);
    }

    [Fact(DisplayName = "R10-O02｜Manual 精确时刻沿用 `<=`：恰好等于按期，超一刻即延期")]
    public void ManualOverride_UsesInclusiveRule()
    {
        var manual = new DateTime(2026, 11, 15, 12, 0, 0);
        var bound = ConstraintContext.ToJudgementBound(manual, isManualOverride: true);

        Assert.False(manual > bound);                                     // 恰好等于 ⇒ 按期
        Assert.True(manual.AddTicks(1) > bound);                          // 超 1 tick ⇒ 延期
    }

    // ─────────────────── 三、Setup 归因反事实（跨 OFF 正反例） ───────────────────

    [Fact(DisplayName = "R10-O02｜Setup 反事实：次日00:30 完工 − 60min Setup ⇒ 交期日23:30，判「是 Setup 导致延期」")]
    public void SetupCounterfactual_WithoutSetupWouldBeOnTime()
    {
        var bound = ConstraintContext.ToJudgementBound(DueDate, isManualOverride: false);

        var lastEnd = new DateTime(2026, 11, 16, 0, 30, 0);
        var withoutSetup = lastEnd.AddMinutes(-60);

        Assert.True(lastEnd > bound, "实际判定：延期");
        // PhaseThreeDiagnostics 既有写法：完工 − Setup <= 上界 ⇒ 归因 Setup
        Assert.True(withoutSetup <= bound, "去掉 Setup 就不延期 ⇒ 应归因 Setup");
    }

    [Fact(DisplayName = "R10-O02｜Setup 反事实反例：次日02:00 完工 − 60min Setup ⇒ 仍过交期，不归因 Setup")]
    public void SetupCounterfactual_EvenWithoutSetupStillLate()
    {
        var bound = ConstraintContext.ToJudgementBound(DueDate, isManualOverride: false);

        var lastEnd = new DateTime(2026, 11, 16, 2, 0, 0);
        var withoutSetup = lastEnd.AddMinutes(-60);

        Assert.True(lastEnd > bound, "实际判定：延期");
        Assert.False(withoutSetup <= bound, "去掉 Setup 仍延期 ⇒ 不得把责任推给 Setup");
    }

    // ─────────────────── 四、上下文方法：覆盖分流真的生效 ───────────────────

    [Fact(DisplayName = "R10-O02｜无 Manual 覆盖 ⇒ 走客户 DATE，上界延到交期日末")]
    public void Context_NoOverride_UsesCustomerDate()
    {
        var constraints = new ConstraintContext();
        var demand = Demand("D-1", DueDate);

        var bound = constraints.EffectiveDueJudgementBound(demand);

        Assert.Equal(LastMomentOfDueDay, bound);
    }

    [Fact(DisplayName = "R10-O02｜有 Manual 覆盖 ⇒ 走精确时刻，上界不延")]
    public void Context_WithOverride_UsesManualInstant()
    {
        var manual = new DateTime(2026, 11, 12, 15, 30, 0);
        var constraints = new ConstraintContext();
        constraints.DueDateOverrides["D-1"] = manual;
        var demand = Demand("D-1", DueDate);

        var bound = constraints.EffectiveDueJudgementBound(demand);

        Assert.Equal(manual, bound);
        Assert.False(bound > manual);
    }

    [Fact(DisplayName = "R10-O02｜EffectiveDue 本身不动：覆盖/无覆盖都仍是原值")]
    public void Context_EffectiveDue_IsUnchanged()
    {
        var constraints = new ConstraintContext();
        Assert.Equal(DueDate, constraints.EffectiveDue(Demand("D-1", DueDate)));

        var manual = new DateTime(2026, 11, 12, 15, 30, 0);
        constraints.DueDateOverrides["D-2"] = manual;
        Assert.Equal(manual, constraints.EffectiveDue(Demand("D-2", DueDate)));
    }

    // ─────────── 五、端到端鉴别：真跑 FiniteCapacitySolver（纯内存，不连库） ───────────
    //
    // 上面四节只证明「纯函数算得对」，证明不了「调用点真的换过去了」。
    // 这两条是**鉴别性反证**：同一条需求、同一道 60 分钟工序，只把日历窗挪一天 ——
    //   完工落在交期日当天 ⇒ 必须**零**延期事实；
    //   完工被推到次日 ⇒ 必须产出延期事实、ImpactHours ≈ 1。
    // 改回旧的 `EffectiveDue` 取法，第一条必红（完工 01:00 > 交期日 00:00 ⇒ 会报延期 60 分钟）。

    private static readonly DateTime PlanningStart = new(2026, 9, 1, 0, 0, 0);
    private static readonly DateTime PlanningEnd = new(2026, 10, 31, 0, 0, 0);

    [Fact(DisplayName = "R10-O02｜端到端：完工落在客户 DATE 交期日当天 ⇒ 不产出延期事实")]
    public async Task EndToEnd_CompletionOnDueDay_IsOnTime()
    {
        var due = PlanningStart.AddDays(10);          // 2026-09-11 00:00，客户 DATE 交期
        var result = await new FiniteCapacitySolver()
            .SolveAsync(BuildSolverRequest(due, windowStart: due, opMinutes: 60m));

        Assert.True(result.FinalTasks.Count == 1,
            $"应当排下 1 条任务；实际 tasks={result.FinalTasks.Count}, unsched={result.UnscheduledTasks.Count}, "
            + $"reason={string.Join("|", result.UnscheduledTasks.Select(u => u.Reason))}");
        // 完工 09-11 01:00，在交期日之内 ⇒ 按期（旧口径「完工 > 交日零点」会报延期 60 分钟）
        Assert.Equal(new DateTime(2026, 9, 11, 1, 0, 0), result.FinalTasks[0].PlannedEndTime);
        Assert.Empty(OrderFacts(result));
    }

    [Fact(DisplayName = "R10-O02｜端到端反例：完工越过次日 00:00 ⇒ 产出延期事实，延期约 3 小时")]
    public async Task EndToEnd_CompletionNextDay_IsLate()
    {
        var due = PlanningStart.AddDays(10);          // 2026-09-11 00:00
        var result = await new FiniteCapacitySolver()
            .SolveAsync(BuildSolverRequest(due, windowStart: due.AddHours(22), opMinutes: 300m));

        Assert.True(result.FinalTasks.Count == 1,
            $"应当排下 1 条任务；实际 tasks={result.FinalTasks.Count}, unsched={result.UnscheduledTasks.Count}, "
            + $"reason={string.Join("|", result.UnscheduledTasks.Select(u => u.Reason))}");
        // 完工 09-12 03:00 ⇒ 越过排他上界 3 小时
        Assert.Equal(new DateTime(2026, 9, 12, 3, 0, 0), result.FinalTasks[0].PlannedEndTime);
        var fact = Assert.Single(OrderFacts(result));
        Assert.Equal(3.0m, Math.Round(fact.ImpactHours!.Value, 4));
    }

    private static IEnumerable<ScheduleExplanationFact> OrderFacts(DomainSolveResult result)
        => result.ExplanationFacts.Where(f => f.ObjectType == "ORDER");

    /// <summary>单一需求 / 单一工序 / 单一资源；日历只有 <paramref name="windowStart"/> 起 6 小时一个窗。</summary>
    private static DomainSolveRequest BuildSolverRequest(DateTime due, DateTime windowStart, decimal opMinutes)
    {
        const int deptId = 100;
        const string stage = "STAGE1";
        const string opCode = "OP10";

        var demands = new List<LogicalProductionDemand> { Demand("M1", due, stage) };

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "DOMAIN",
            PlanningStart = PlanningStart,
            PlanningEnd = PlanningEnd,
            LogicalProductionDemands = demands,
            RoutingOperations = new List<RoutingOperation>
            {
                new()
                {
                    MaterialId = 1, ProductionDepartmentId = deptId, RouteCode = "DEFAULT",
                    OperationCode = opCode, StageCode = stage,
                    StandardDuration = opMinutes * 60m, SetupTime = 0m   // 秒（PhaseOneConstraintBuilder 里 /60 折成分钟）
                }
            },
            OperationResourceEligibility = new List<OperationResourceEligibility>
            {
                new()
                {
                    MaterialId = 1, ProductionDepartmentId = deptId, RouteCode = "DEFAULT",
                    OperationCode = opCode, ResourceId = 1, Priority = 1, CapacityFactor = 1m
                }
            },
            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = 1, StageCode = stage, ProductionDepartmentId = deptId }
            },
            Resources = new List<ResourceDefinition>
            {
                new() { ResourceId = 1, ResourceCode = "R1", FactoryCode = "F1", Capacity = 1m }
            },
            CalendarSlots = new List<ResourceCalendarSlot>
            {
                new()
                {
                    ResourceId = 1,
                    Start = windowStart,
                    End = windowStart.AddHours(6),
                    IsAvailable = true
                }
            },
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD",
                    AllowMerge = false,
                    AllowSplit = false
                },
                BatchPolicies = TestBatchPolicy.Permissive(demands.Select(d => d.MaterialId), deptId),
                SolverStrategy = new SolverStrategyBlock
                {
                    BottleneckMode = DynamicBottleneckMode.Auto,
                    AnchorResourceCode = null
                }
            }
        };
    }

    private static LogicalProductionDemand Demand(string key, DateTime required, string startStage = "STAGE1")
        => new()
        {
            LogicalDemandKey = key,
            PlanVersionId = 1L,
            DomainKey = "DOMAIN",
            AllocationSequence = 1,
            DemandKey = key,
            MaterialId = 1,
            FactoryId = 1,
            StartStageCode = startStage,
            NetOutputQty = 1m,
            PlannedProcessQty = 1m,
            RequiredAvailableTime = required,
            DemandSequence = 1,
        };
}
