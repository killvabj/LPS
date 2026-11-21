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
/// **W2 八反例之④**（0号位 11-07 任务包 P1-E §154）：
/// 「**Merge 改善 Setup 却损伤更高优交付 ⇒ 独立胜**」。
///
/// <para>依据（源码内注释 `PhaseTwoInitialScheduler.cs:11068` 逐字）：
/// 「合并会延长批次占用 ⇒ 若把更紧急一方推违约，则合批方案**不可选**（不得为省 Setup 延误高优先需求）」。</para>
///
/// <para>夹具设计：**同一物料、同一工序、Setup 1h**，两条需求——
/// A 交期紧（单独排能按期、合批就违约），B 交期宽松。
/// 断言：**A 的完工不得晚于它的交期**（即不得为省一次 Setup 把 A 推违约）。</para>
///
/// <para>纯内存，不碰数据库。</para>
/// </summary>
public sealed class W2CounterexampleMergeVsDueDateTests
{
    private static readonly DateTime Day = new(2026, 11, 21, 0, 0, 0);
    private static readonly DateTime Start = Day.AddHours(8);
    private const int DeptId = 100;
    private const string Stage = "S1";
    private const string Op = "OP10";
    private const int MaterialId = 1;

    private readonly FiniteCapacitySolver _solver = new();

    [Fact(DisplayName = "W2④：合批会把高优 A 推违约 ⇒ A 必须按期（不得为省 Setup 延误）")]
    public async Task MergeWouldViolateHighPriorityDue_MustNotMerge()
    {
        // 时间账（资源唯一、1 分钟/件；⚠ 本夹具两条需求**同物料**，按 `ResolveSetupCore` 的
        //   `from == toMaterialId ⇒ 0` 分支，Setup 恒为 0，故此处**不含 Setup**）：
        //   A 单独：08:00 起加工 10 件 → 08:10 完工 ≤ A 交期 08:15 ✓
        //   A+B 合批：08:00 起加工 20 件 → 08:20 完工 > 08:15 ✗（违约）
        //   ⚠ 2026-11-21 修正：原交期写 09:15 时，合批 08:20 仍"按期" ⇒ 用例**空转**（已由探针抓到）。
        var dueA = Day.AddHours(8).AddMinutes(15);
        var dueB = Day.AddDays(5);
        var request = Build(dueA, dueB, allowMerge: true);

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);

        var aTasks = result.FinalTasks.Where(t => t.SourceDraftId == "A").ToList();
        Assert.NotEmpty(aTasks);

        // 探针（2026-11-21）：A 若被并入（作锚点或被并入），它承载的量会 > 10 ⇒ 合批**发生过**。
        // 这条断言让本用例**非空转**：没有它，合批发生后 A 仍挂 A 的身份，上面的"按期"断言会空过。
        Assert.Equal(10m, aTasks.Sum(t => t.Quantity));

        var aEnd = aTasks.Max(t => t.PlannedEndTime);
        Assert.True(aEnd <= dueA,
            $"高优 A 完工 {aEnd:O} 不得晚于交期 {dueA:O}（省 Setup 不得牺牲高优交付）；"
            + "若合批导致该结果，说明交期保护未被生效");
    }

    [Fact(DisplayName = "W2④对照：A/B 交期都宽松 ⇒ 两条都能排下（守卫不误杀）")]
    public async Task BothDueLoose_BothScheduled()
    {
        var request = Build(Day.AddDays(5), Day.AddDays(5), allowMerge: true);

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        // ⚠ 合批成功后 Task 只挂**锚点**的 `SourceDraftId`，被并入方的份额不体现在 Task 上
        //   ⇒ 不能按 `SourceDraftId` 数条数（那样合批反而会被判失败）。改判：
        //   ①两条需求都没有未排；②已排总量 == 20（A10 + B10，合批与否都必须排满）。
        Assert.DoesNotContain(result.UnscheduledTasks, u => u.DraftId is "A" or "B");
        Assert.Equal(20m, result.FinalTasks.Sum(t => t.Quantity));
    }

    // ─────────────────────────── 夹具 ───────────────────────────

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
                    RequiredAvailableTime = dueA, DemandSequence = 1,     // 高优（序号小）
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
                        StandardDuration = 60m,        // 60 秒/件 = 1 分钟/件
                        SetupTime = 60m,               // 1 小时换型
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
