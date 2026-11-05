using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Core.Dto;
using RoutingOperation = LPS.APS.Core.Entities.APS.RoutingOperation;
using OperationResourceEligibility = LPS.APS.Core.Entities.APS.OperationResourceEligibility;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 跨物料时序 × 合批延长 的隔离测试（2026-10-28，PV190 硬校验失败真因）。
///
/// 【现场】PV190 实跑 Phase5 硬校验失败，整域作废（Scheduled=0）：
///   下游 190_14379/M201 开始 2026-10-19 13:38:38 早于
///   上游 190_14380/M102 结束 2026-10-19 13:42:23（Lag 0）。
///   上游那条 Task 的批键是 `EB|190_14380|001`，但需求 190_14380 只有 **38** 个，
///   Task 上是 **54** 个 ⇒ 16 个是别的需求**合批**并进来的，合批把这条 Task 的 End 从
///   Phase2 登记值 13:38:38 推到 13:42:23，而父件 13:38:38 的起点已经发布、没人回推。
///
/// 【本测试的判别点】
///   · 正例（<see cref="Forward_ParentUsesMergedEnd_NotStaleChildCompletion"/>）：
///     子件需求 C1(30) 与 C2(20) 同物料 ⇒ 合批成一条 50 的 Task；父件 P 的地板必须取
///     **合批后的 End**。整改前 `demandCompletion[C1]` 停在 30 分钟处 ⇒ 父件从 30 分钟起排 ⇒ 红。
///   · 反证（<see cref="Forward_ParentStillRespectsChildEnd_WhenMergeDisabled"/>）：
///     关掉合批，同一夹具 ⇒ 两条子件 Task 各排一段，父件仍然 &gt;= 最晚子件完成 ⇒ 说明本夹具
///     的断言不是「随便都能过」，合批确实是唯一变量。
/// 纯内存，直接调 FiniteCapacitySolver.SolveAsync，不碰数据库。
/// </summary>
public class CrossMaterialMergeFloorTests
{
    private static readonly DateTime PlanningStart = new DateTime(2026, 9, 1, 0, 0, 0);
    private static readonly DateTime PlanningEnd = new DateTime(2026, 10, 1, 0, 0, 0);

    private const int DeptId = 100;
    private const int ChildMaterialId = 901;   // 被合批的子件物料
    private const int ParentMaterialId = 902;  // 父件物料
    private const int ChildResourceId = 11;
    private const int ParentResourceId = 22;
    private const decimal ChildQty1 = 30m;     // C1
    private const decimal ChildQty2 = 20m;     // C2
    private const decimal ParentQty = 10m;     // P

    private readonly FiniteCapacitySolver _solver = new();

    [Fact]
    public async Task Forward_ParentUsesMergedEnd_NotStaleChildCompletion()
    {
        var result = await _solver.SolveAsync(Build(allowMerge: true));

        Assert.True(result.Success, result.ErrorMessage);

        var childTasks = result.FinalTasks.Where(t => t.MaterialId == ChildMaterialId).ToList();
        var parentTask = result.FinalTasks.Single(t => t.MaterialId == ParentMaterialId);

        // 夹具自检：合批**必须真的发生**，否则下面那条断言就是空测。
        //   合批落定 ⇒ C2 不产新 Task，C1 的 Task 被延长到「C1+C2」的数量。
        var mergedTask = Assert.Single(childTasks);
        Assert.Equal(ChildQty1 + ChildQty2, mergedTask.Quantity);

        // 合批让这条 Task 从「C1 一个人」延长到「C1+C2」⇒ End 必然晚于只排 C1 时的 End。
        //   （单位换算不写死：按数量比例从合并后的时长反推「只排 C1」的时长。）
        var mergedSpan = mergedTask.PlannedEndTime - mergedTask.PlannedStartTime;
        var c1AloneEnd = mergedTask.PlannedStartTime
                         + TimeSpan.FromTicks((long)(mergedSpan.Ticks * (double)(ChildQty1 / (ChildQty1 + ChildQty2))));
        Assert.True(mergedTask.PlannedEndTime > c1AloneEnd,
            $"合批后 Task 结束 {mergedTask.PlannedEndTime:O} 应晚于「只排 C1」的 {c1AloneEnd:O}");

        // 主断言（整改前红）：父件起点必须 >= 合批后的子件结束时间。
        Assert.True(parentTask.PlannedStartTime >= mergedTask.PlannedEndTime,
            $"父件开工 {parentTask.PlannedStartTime:O} 必须 >= 合批后子件完成 {mergedTask.PlannedEndTime:O}"
            + "（合批把子件 End 推后时，父件地板必须跟着变）");

        // 判别性：父件是被**合批后**的地板顶住的，不是被「只排 C1」的旧值顶住的。
        Assert.True(parentTask.PlannedStartTime > c1AloneEnd,
            $"父件开工 {parentTask.PlannedStartTime:O} 应晚于「只排 C1」的 {c1AloneEnd:O}"
            + "（等于旧值 ⇒ 说明父件用的是合批前的陈旧的子件完成时间）");
    }

    [Fact]
    public async Task Forward_ParentStillRespectsChildEnd_WhenMergeDisabled()
    {
        var result = await _solver.SolveAsync(Build(allowMerge: false));

        Assert.True(result.Success, result.ErrorMessage);

        var childTasks = result.FinalTasks.Where(t => t.MaterialId == ChildMaterialId).ToList();
        var parentTask = result.FinalTasks.Single(t => t.MaterialId == ParentMaterialId);

        // 关合批 ⇒ 两条子件 Task 各排各的（互不延长）。
        Assert.Equal(2, childTasks.Count);
        // 父件只依赖 C1（C2 不是它的子件）⇒ 地板 = C1 自己的结束，不是同物料的最晚结束。
        var c1Task = childTasks.Single(t => t.SourceDraftId == "C1");
        Assert.True(parentTask.PlannedStartTime >= c1Task.PlannedEndTime,
            $"父件开工 {parentTask.PlannedStartTime:O} 必须 >= 子件 C1 完成 {c1Task.PlannedEndTime:O}");
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private static DomainSolveRequest Build(bool allowMerge)
    {
        var demands = new List<LogicalProductionDemand>
        {
            Demand("C1", ChildMaterialId, 1L, ChildQty1, demandSeq: 1),
            Demand("C2", ChildMaterialId, 2L, ChildQty2, demandSeq: 2),
            Demand("P", ParentMaterialId, 3L, ParentQty, demandSeq: 3),
        };

        // 单工序：1 秒/件。⚠ 单位口径：输入实体 `RoutingOperation.StandardDuration` = **秒**
        //   （`PhaseOneConstraintBuilder.cs:993` 建 `OperationNode` 时 `/60` 转分钟，故这里是秒）；
        //   断言按比例推导，不写死绝对值，故本值只影响时长、不影响判别性。
        var routingOps = new List<RoutingOperation>
        {
            Op(ChildMaterialId, "OP10", 1m),
            Op(ParentMaterialId, "OP10", 1m),
        };

        var eligibility = new List<OperationResourceEligibility>
        {
            Elig(ChildMaterialId, "OP10", ChildResourceId),
            Elig(ParentMaterialId, "OP10", ParentResourceId),
        };

        var stageDepts = new List<MaterialStageDepartmentContextDto>
        {
            new() { MaterialId = ChildMaterialId, StageCode = "STAGE1", ProductionDepartmentId = DeptId },
            new() { MaterialId = ParentMaterialId, StageCode = "STAGE1", ProductionDepartmentId = DeptId },
        };

        var resources = new List<ResourceDefinition>
        {
            new() { ResourceId = ChildResourceId, ResourceCode = "R11", FactoryCode = "F1", Capacity = 1m },
            new() { ResourceId = ParentResourceId, ResourceCode = "R22", FactoryCode = "F1", Capacity = 1m },
        };

        var slots = resources
            .Select(r => new ResourceCalendarSlot
            {
                ResourceId = r.ResourceId,
                Start = PlanningStart,
                End = PlanningEnd,
                IsAvailable = true
            })
            .ToList();

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "DOMAIN",
            PlanningStart = PlanningStart,
            PlanningEnd = PlanningEnd,
            LogicalProductionDemands = demands,
            MaterialRequirementLinks = new List<MaterialRequirementLink>
            {
                // 父件 P 消费子件 C1（Lag 0）。
                new()
                {
                    ConsumerLogicalDemandKey = "P",
                    ProducerLogicalDemandKey = "C1",
                    ConsumerMaterialId = ParentMaterialId,
                    ProducerMaterialId = ChildMaterialId,
                    RequiredQty = 1m,
                    ProducerAllocationSequence = 1L
                }
            },
            RoutingOperations = routingOps,
            OperationResourceEligibility = eligibility,
            MaterialStageDepartmentContexts = stageDepts,
            Resources = resources,
            CalendarSlots = slots,
            ExecutionConstraints = Array.Empty<ExecutionConstraint>(),
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD",
                    AllowMerge = allowMerge,
                    AllowSplit = false
                },
                BatchPolicies = TestBatchPolicy.Permissive(
                    new[] { ChildMaterialId, ParentMaterialId }, DeptId, allowMerge, false),
                SetupTransitionRules = Array.Empty<SetupTransitionRuleSnapshot>()
            }
        };
    }

    private static LogicalProductionDemand Demand(string key, int materialId, long allocSeq, decimal qty, int demandSeq)
        => new()
        {
            LogicalDemandKey = key,
            PlanVersionId = 1L,
            DomainKey = "DOMAIN",
            AllocationSequence = allocSeq,
            DemandKey = key,
            MaterialId = materialId,
            FactoryId = 1,
            StartStageCode = "STAGE1",
            NetOutputQty = qty,
            PlannedProcessQty = qty,
            RequiredAvailableTime = PlanningStart.AddDays(20),
            DemandSequence = demandSeq
        };

    private static RoutingOperation Op(int materialId, string opCode, decimal minutesPerUnit)
        => new()
        {
            MaterialId = materialId,
            ProductionDepartmentId = DeptId,
            RouteCode = "DEFAULT",
            PathId = 1,
            OperationCode = opCode,
            StageCode = "STAGE1",
            StandardDuration = minutesPerUnit,
            OperationPlanningMode = "FINITE_RESOURCE"
        };

    private static OperationResourceEligibility Elig(int materialId, string opCode, int resourceId)
        => new()
        {
            MaterialId = materialId,
            ProductionDepartmentId = DeptId,
            RouteCode = "DEFAULT",
            PathId = 1,
            OperationCode = opCode,
            ResourceId = resourceId,
            Priority = 1,
            CapacityFactor = 1m
        };
}
