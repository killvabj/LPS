using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using RoutingOperation = LPS.APS.Core.Entities.APS.RoutingOperation;
using OperationResourceEligibility = LPS.APS.Core.Entities.APS.OperationResourceEligibility;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **W3（0号位 v1.8 §七 ／ 11-07 任务包 §P1-E）**：独立 vs Merge 的**真实 Setup 净变化**，
/// 在 <see cref="FiniteCapacitySolver.SolveAsync"/> 真实入口上复算。
///
/// <para><b>⚠ 本文件得出的是一个"证伪"结论，不是"验证通过"</b></para>
///
/// <para>0号位 的 W3 前提是「Merge 改善 Setup」。**在当前实现下这个前提不成立**，两条源码依据：</para>
/// <list type="number">
///   <item><b>合批只允许同物料</b>：`PhaseTwoInitialScheduler` 合并段注释逐字
///     「Merge 是同物料合并（v1.2 §六 同产品语义，Setup 继承不变）」。 </item>
///   <item><b>同产品不换型</b>：`SetupOptimizer.ResolveSetupCore` 在 `fromMaterialId == toMaterialId`
///     时**直接返回 0 分钟**（`SetupOutcome.SameProductZero`），且这一步**早于** DEFAULT/EXACT 规则命中。</item>
/// </list>
///
/// <para>⇒ 两条同物料需求，无论合批还是独立排：

///   · 第一条前面若有「不同产品」→ 都付**同一次**换型；
///   · 第二条与第一条同产品 → **本来就 0**；
///   ⇒ **ΣSetup 恒等**。合批在这条链上**一分钟 Setup 都省不掉**。</para>
///
/// <para>本文件因此锁的是**等式**（allowed vs forbidden 的 ΣSetup 相同），
/// 而不是"合批更省"。若将来允许跨物料合批，这个等式会变红——那时才需要重写本文件。</para>
///
/// <para>纯内存，不碰数据库。</para>
/// </summary>
public sealed class W3SolverSetupNetDeltaTests
{
    private static readonly DateTime Day = new(2026, 11, 21, 0, 0, 0);
    private static readonly DateTime Start = Day.AddHours(8);
    private const int DeptId = 100;
    private const string Stage = "S1";
    private const string Op = "OP10";
    private const int MaterialId = 1;
    private const decimal SetupMinutes = 60m;

    private readonly FiniteCapacitySolver _solver = new();

    [Fact(DisplayName = "W3①：允许合批 ⇒ 合成 1 条 Task（证明比较对象真实存在）")]
    public async Task MergeAllowed_ProducesSingleTask()
    {
        var result = await SolveAsync(Day.AddDays(5), Day.AddDays(5), allowMerge: true);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.DoesNotContain(result.UnscheduledTasks, u => u.DraftId is "A" or "B");

        Assert.Equal(1, result.FinalTasks.Count);
        Assert.Equal(20m, result.FinalTasks.Sum(t => t.Quantity));
    }

    [Fact(DisplayName = "W3②：禁止合批 ⇒ 独立排 2 条 Task")]
    public async Task MergeForbidden_ProducesTwoTasks()
    {
        var result = await SolveAsync(Day.AddDays(5), Day.AddDays(5), allowMerge: false);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.DoesNotContain(result.UnscheduledTasks, u => u.DraftId is "A" or "B");

        Assert.Equal(2, result.FinalTasks.Count);
        Assert.Equal(20m, result.FinalTasks.Sum(t => t.Quantity));
    }

    [Fact(DisplayName = "W3③（核心）：同物料合批 vs 独立 ⇒ 真实 ΣSetup **完全相同**（合批省不到 Setup）")]
    public async Task SameMaterial_MergeSavesNoSetup()
    {
        var merged = await SolveAsync(Day.AddDays(5), Day.AddDays(5), allowMerge: true);
        var independent = await SolveAsync(Day.AddDays(5), Day.AddDays(5), allowMerge: false);

        var mergedSetup = merged.FinalTasks.Sum(t => t.SetupTime);
        var independentSetup = independent.FinalTasks.Sum(t => t.SetupTime);

        Assert.Equal(independentSetup, mergedSetup);

        // 同时给出绝对值，避免"两边都是 0"这种空洞相等：
        // 无前产品 ⇒ `SetupOutcome.InitialState` ⇒ 0；两条同产品 ⇒ 第二条 `SameProductZero` ⇒ 0。
        Assert.Equal(0m, mergedSetup);
        Assert.Equal(0m, independentSetup);
    }

    [Fact(DisplayName = "W3④（反例）：合批会推违约 ⇒ 必须独立排（此时代价是工期，不是 Setup）")]
    public async Task MergeBreaksHighPriorityDue_IndependentWins()
    {
        // 无 Setup 情况下的时间账（每条 10 件 × 1 分钟）：
        //   A 单独：08:00 起加工 10 件 → 08:10 完工 ≤ 交期 08:15 ✓
        //   A+B 合批：08:00 起加工 20 件 → 08:20 完工 > 08:15 ✗
        var dueA = Day.AddHours(8).AddMinutes(15);
        var result = await SolveAsync(dueA, Day.AddDays(5), allowMerge: true);

        Assert.True(result.Success, result.ErrorMessage);

        var aTasks = result.FinalTasks.Where(t => t.SourceDraftId == "A").ToList();
        Assert.NotEmpty(aTasks);   // 合批成立时 A 无独立 Task ⇒ 此处必须仍有

        // 探针：A 若被并入（作锚点或并入锚点），它承载的量就会 > 10 ⇒ 说明合批**发生了**
        Assert.Equal(10m, aTasks.Sum(t => t.Quantity));
        Assert.True(aTasks.Max(t => t.PlannedEndTime) <= dueA,
            $"高优 A 完工 {aTasks.Max(t => t.PlannedEndTime):HH:mm} 晚于交期 {dueA:HH:mm}");

        // 数量闭合；且「省 Setup」不是这次取舍的动因（两边都是 0）
        Assert.Equal(20m, result.FinalTasks.Sum(t => t.Quantity));
        Assert.Equal(0m, result.FinalTasks.Sum(t => t.SetupTime));
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private Task<DomainSolveResult> SolveAsync(DateTime dueA, DateTime dueB, bool allowMerge)
        => _solver.SolveAsync(Build(dueA, dueB, allowMerge));

    private static DomainSolveRequest Build(DateTime dueA, DateTime dueB, bool allowMerge)
    {
        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            ScheduleRunId = 42L,
            DomainKey = "DOMAIN",
            PlanningStart = Start,
            PlanningEnd = Day.AddDays(30),
            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                new()
                {
                    LogicalDemandKey = "A", PlanVersionId = 1L, DomainKey = "DOMAIN",
                    AllocationSequence = 1, DemandKey = "A", MaterialId = MaterialId, FactoryId = 1,
                    StartStageCode = Stage, NetOutputQty = 10m, PlannedProcessQty = 10m,
                    RequiredAvailableTime = dueA, DemandSequence = 1,
                    QuantityAuthority = QuantityAuthorityValues.None,
                },
                new()
                {
                    LogicalDemandKey = "B", PlanVersionId = 1L, DomainKey = "DOMAIN",
                    AllocationSequence = 2, DemandKey = "B", MaterialId = MaterialId, FactoryId = 1,
                    StartStageCode = Stage, NetOutputQty = 10m, PlannedProcessQty = 10m,
                    RequiredAvailableTime = dueB, DemandSequence = 2,
                    QuantityAuthority = QuantityAuthorityValues.None,
                },
            },
            RoutingOperations = new List<RoutingOperation>
            {
                new() { MaterialId = MaterialId, ProductionDepartmentId = DeptId, RouteCode = "DEFAULT",
                        OperationCode = Op, StageCode = Stage,
                        StandardDuration = 60m,          // 60 秒/件 = 1 分钟/件
                        SetupTime = SetupMinutes,        // ⚠ 该列**已废止**、不参与计算（v1.2 §1.2）
                        OperationPlanningMode = "FINITE_RESOURCE" },
            },
            OperationResourceEligibility = new List<OperationResourceEligibility>
            {
                new() { MaterialId = MaterialId, ProductionDepartmentId = DeptId, RouteCode = "DEFAULT",
                        OperationCode = Op, ResourceId = 1, Priority = 1, CapacityFactor = 1m },
            },
            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = MaterialId, StageCode = Stage, ProductionDepartmentId = DeptId },
            },
            Resources = new List<ResourceDefinition>
            {
                new() { ResourceId = 1, ResourceCode = "R1", FactoryCode = "F1", Capacity = 1m },
            },
            CalendarSlots = new List<ResourceCalendarSlot>
            {
                new() { ResourceId = 1, Start = Day, End = Day.AddDays(30), IsAvailable = true },
            },
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD",
                    AllowMerge = allowMerge,
                    AllowSplit = false,
                },
                // ⚠ Setup 的唯一真实来源是**规则快照**，不是 `RoutingOperation.SetupTime`
                //   （`PhaseTwoInitialScheduler.cs:7841`：「原 operation.SetupTime 已废止，v1.2 §1.2」）。
                //   这里给一条 DEFAULT 兜底：该资源上的换型固定 60 分钟。
                //   **但同产品在本方法里更早短路为 0**（`ResolveSetupCore` 的 `from == to` 分支），
                //   本夹具两条需求同物料 ⇒ 这条规则实际不会被命中 —— 正是本文件要证明的点。
                SetupTransitionRules = new[]
                {
                    new SetupTransitionRuleSnapshot
                    {
                        ProductionDepartmentId = DeptId,
                        StageCode = Stage,
                        OperationCode = Op,
                        ResourceId = 1,
                        RuleType = "DEFAULT",
                        SetupMinutes = SetupMinutes,
                    },
                },
                BatchPolicies = new[]
                {
                    new BatchPolicyRuleSnapshot
                    {
                        MaterialId = MaterialId, ProductionDepartmentId = DeptId,
                        MinExecutionBatchQty = null, MaxExecutionBatchQty = null,
                        AllowSplit = false, AllowMerge = allowMerge,
                    },
                },
            },
        };
    }
}
