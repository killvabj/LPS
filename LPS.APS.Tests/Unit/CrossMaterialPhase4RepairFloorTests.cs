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
/// 「子件修复后父件不得提前」（0号位 2026-10-28《R07R08_致1号位更正回复_v1.1_20261028.md》§五）。
///
/// 【机理】`PhaseFourLocalRepair.RepairUnscheduledDemands` 开头的 `demandCompletion` 只由
///   **Phase2 已排 Task** 建一次。Phase4 在本轮里修出来的 Task（更晚的 End）若不回灌，
///   后面处理的父件在 `GetDynamicMaterialFloor` 里读到的仍是**子件修复前**的旧完成时间
///   ⇒ 父件按过早下界开工 ⇒ 跨物料越序（Phase5 `CheckTaskDependencies` 一票打成 FinalTask=0）。
///   本用例就是这条因果链的**隔离反证**。
///
/// 【几何】（确定性推演，见 <see cref="Build"/> 注释）
///   · 子件 901：需求 2 件、批域 Min=Max=1 + AllowSplit ⇒ 唯一合法批数 = 2（各 1 件），单工序 60 分钟，资源 1；
///   · 父件 902：需求 1 件，单工序 60 分钟，**资源 2**（刻意不同资源 ⇒ 资源占用表挡不住父件提前，
///     能否不提前**只取决于**子件完成时间下界 —— 这正是本用例要单独隔离的那一条）；
///   · 日历两资源均 `[Day+6h, Day+20h]`，Run 级 BACKWARD；
///   · 子件交期 `Day+7h30`：批 1 倒排成功 `[Day+6h30, Day+7h30]`；批 2 需再往前一段、早于日历起点 ⇒ **Phase2 失败**；
///   · 父件交期 `Day+6h30`：倒排需 `[Day+5h30, Day+6h30]`、早于日历起点，且子件下界 `Day+7h30` 已过交期 ⇒ **Phase2 未排**；
///   · Phase4 正排修复：
///       – 子件批 2 → 资源 1 上 `[Day+7h30, Day+8h30]`（下界 = 批 1 占用末端）⇒ 子件**实际完成 = Day+8h30**；
///       – 父件 → 下界必须取**回灌后**的 `Day+8h30` ⇒ 落在资源 2 的 `[Day+8h30, Day+9h30]`。
///
/// 【鉴别力】整改前：父件读到的是「子件修复前」的 `Day+7h30` ⇒ 落在 `[Day+7h30, Day+8h30]`
///   ⇒ 父件开工早于子件完成 ⇒ Phase5 硬校验失败、`Success=false`、FinalTask=0（PV190 run 的同型终局）。
///   整改后：`Success=true` 且父件开工 **>=** 子件全部 Task 的最晚完成时间。
/// 纯内存，直接调 `FiniteCapacitySolver.SolveAsync`，不碰数据库。
/// </summary>
public class CrossMaterialPhase4RepairFloorTests
{
    private static readonly DateTime Day = new(2026, 9, 1, 0, 0, 0);
    private static readonly DateTime PlanningEnd = Day.AddDays(30);

    private const int DeptId = 100;
    private const int ChildMaterialId = 901;
    private const int ParentMaterialId = 902;
    private const int ChildResourceId = 1;
    private const int ParentResourceId = 2;

    /// <summary>`RoutingOperation.StandardDuration` 单位 = **秒**（与 `Phase5BatchDirectionTests` 的 `60m * 60m` 同口径），60 分钟 = 3600。</summary>
    private const decimal SixtyMinutesInSeconds = 3600m;

    /// <summary>两资源共用日历窗。</summary>
    private static readonly DateTime WindowStart = Day.AddHours(6);
    private static readonly DateTime WindowEnd = Day.AddHours(20);

    /// <summary>子件交期：批 1 恰好倒排进 `[Day+6h30, Day+7h30]`，批 2 越界。</summary>
    private static readonly DateTime ChildDue = Day.AddHours(7).AddMinutes(30);

    /// <summary>父件交期：倒排需早于日历起点 ⇒ Phase2 未排。</summary>
    private static readonly DateTime ParentDue = Day.AddHours(6).AddMinutes(30);

    private readonly FiniteCapacitySolver _solver = new();

    [Fact]
    public async Task RepairedChild_ParentMustNotStartBeforeRepairedChildEnd()
    {
        var result = await _solver.SolveAsync(Build());

        Assert.True(result.Success, result.ErrorMessage);

        var childTasks = result.FinalTasks.Where(t => t.MaterialId == ChildMaterialId).ToList();
        var parentTask = Assert.Single(result.FinalTasks.Where(t => t.MaterialId == ParentMaterialId));

        // 夹具自检 1：子件确实是「批 1 Phase2 落定 + 批 2 Phase4 修复」两段，否则本用例退化成单批、失去隔离意义。
        Assert.Equal(2, childTasks.Count);

        // 主断言：父件开工不得早于**子件全部 Task**（含 Phase4 修复出来的那段）的最晚完成时间 + Lag(0)。
        var childEnd = childTasks.Max(t => t.PlannedEndTime);
        Assert.True(parentTask.PlannedStartTime >= childEnd,
            $"父件开工 {parentTask.PlannedStartTime:O} 早于子件完成 {childEnd:O}"
            + "（子件修复后的完成时间未回灌 ⇒ 父件按过早下界开工）");

        // 判别性：父件必须是**被修复后的**子件完成时间顶住的，不是被「修复前」的旧值顶住的。
        var childEndBeforeRepair = childTasks.Min(t => t.PlannedEndTime);
        Assert.True(childEnd > childEndBeforeRepair,
            $"子件最晚结束 {childEnd:O} 应晚于最早结束 {childEndBeforeRepair:O}（夹具未产生「修复推后」效果）");
        Assert.True(parentTask.PlannedStartTime > childEndBeforeRepair,
            $"父件开工 {parentTask.PlannedStartTime:O} 应晚于修复前的子件完成 {childEndBeforeRepair:O}"
            + "（等于旧值 ⇒ 说明父件用的是子件修复之前的陈旧完成时间）");
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    /// <summary>
    /// 几何推演见类注释。关键点：父件与子件**不同资源** ⇒ 资源占用表不参与拦截，
    /// 父件能否不提前完全由「子件完成时间下界」决定，隔离出 <c>demandCompletion</c> 回灌这一条因果。
    /// </summary>
    private static DomainSolveRequest Build()
    {
        var demands = new List<LogicalProductionDemand>
        {
            Demand("C1", ChildMaterialId, 1L, 2m, demandSeq: 1, due: ChildDue),
            Demand("P1", ParentMaterialId, 2L, 1m, demandSeq: 2, due: ParentDue),
        };

        var routingOps = new List<RoutingOperation>
        {
            Op(ChildMaterialId),
            Op(ParentMaterialId),
        };

        var eligibility = new List<OperationResourceEligibility>
        {
            Elig(ChildMaterialId, ChildResourceId),
            Elig(ParentMaterialId, ParentResourceId),
        };

        var stageDepts = new List<MaterialStageDepartmentContextDto>
        {
            new() { MaterialId = ChildMaterialId, StageCode = "STAGE1", ProductionDepartmentId = DeptId },
            new() { MaterialId = ParentMaterialId, StageCode = "STAGE1", ProductionDepartmentId = DeptId },
        };

        var resources = new List<ResourceDefinition>
        {
            new() { ResourceId = ChildResourceId, ResourceCode = "R1", FactoryCode = "F1", Capacity = 1m },
            new() { ResourceId = ParentResourceId, ResourceCode = "R2", FactoryCode = "F1", Capacity = 1m },
        };

        var slots = resources
            .Select(r => new ResourceCalendarSlot
            {
                ResourceId = r.ResourceId,
                Start = WindowStart,
                End = WindowEnd,
                IsAvailable = true
            })
            .ToList();

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "DOMAIN",
            PlanningStart = Day,
            PlanningEnd = PlanningEnd,
            LogicalProductionDemands = demands,
            MaterialRequirementLinks = new List<MaterialRequirementLink>
            {
                // 父件 P1 消费子件 C1（Lag 0）。
                new()
                {
                    ConsumerLogicalDemandKey = "P1",
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
                    // 固定倒排：子件批 1 必 BACKWARD、批 2 必失败 ⇒ Phase4 才有活干（不给 AUTO 自决留扰动）。
                    SchedulingDirection = "BACKWARD",
                    AllowMerge = false,
                    AllowSplit = false
                },
                // P0-01：C 桶必须显式给出有效 Batch Policy。
                //   子件 Min = Max = 1 + AllowSplit ⇒ 2 件需求**唯一合法**批数 = 2（各 1 件）。
                BatchPolicies = new[]
                {
                    new BatchPolicyRuleSnapshot
                    {
                        MaterialId = ChildMaterialId,
                        ProductionDepartmentId = DeptId,
                        MinExecutionBatchQty = 1m,
                        MaxExecutionBatchQty = 1m,
                        AllowSplit = true,
                        AllowMerge = false
                    },
                    new BatchPolicyRuleSnapshot
                    {
                        MaterialId = ParentMaterialId,
                        ProductionDepartmentId = DeptId,
                        MinExecutionBatchQty = 1m,
                        MaxExecutionBatchQty = 1m,
                        AllowSplit = false,
                        AllowMerge = false
                    }
                },
                SetupTransitionRules = Array.Empty<SetupTransitionRuleSnapshot>()
            }
        };
    }

    private static LogicalProductionDemand Demand(
        string key, int materialId, long allocSeq, decimal qty, int demandSeq, DateTime due)
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
            RequiredAvailableTime = due,
            DemandSequence = demandSeq
            // RouteCode / PathId 留空 ⇒ C 桶候选（本夹具每种物料只有一条路由 ⇒ 无候选择优扰动）。
        };

    private static RoutingOperation Op(int materialId)
        => new()
        {
            MaterialId = materialId,
            ProductionDepartmentId = DeptId,
            RouteCode = "DEFAULT",
            PathId = 1,
            OperationCode = "OP10",
            StageCode = "STAGE1",
            StandardDuration = SixtyMinutesInSeconds,
            OperationPlanningMode = "FINITE_RESOURCE"
        };

    private static OperationResourceEligibility Elig(int materialId, int resourceId)
        => new()
        {
            MaterialId = materialId,
            ProductionDepartmentId = DeptId,
            RouteCode = "DEFAULT",
            PathId = 1,
            OperationCode = "OP10",
            ResourceId = resourceId,
            Priority = 1,
            CapacityFactor = 1m
        };
}
