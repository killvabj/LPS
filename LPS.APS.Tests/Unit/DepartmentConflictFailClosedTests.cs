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
/// **部门冲突 Fail Closed**（0号位 2026-10-26 四份整改单 P0 ②
/// 「部门冲突 Fail Closed（`PhaseOneConstraintBuilder:417-419`）」）。
///
/// 旧实现 `GroupBy((MaterialId, StageCode)).ToDictionary(g => g.First().ProductionDepartmentId)`：
///   同键出现两个**不同**部门时，取谁取决于载荷顺序 ⇒ 不可复现，且锁进来的可能是
///   「别的部门的小工序集合」（v5.0.16 冻结：部门是「物料×阶段」联合属性）。错了也不报错。
///
/// 现在必须做到两件事，本文件各一条：
///   ① 真冲突（**不同**部门）⇒ **Fail Closed**：不猜、该物料整条不参与部门锁定（业务未排），
///      并留 `PRODUCTION_DEPARTMENT_CONFLICT`；
///   ② 同键**同**部门的重复行**不是**冲突 ⇒ 照常排程、不得误报（否则正常数据会被打挂）。
/// </summary>
public class DepartmentConflictFailClosedTests
{
    private static readonly DateTime Day = new(2026, 9, 1);

    private readonly FiniteCapacitySolver _solver = new();

    [Fact]
    public async Task 同键不同部门_FailClosed_不猜_且留原因码()
    {
        var result = await _solver.SolveAsync(Build(
            new MaterialStageDepartmentContextDto { MaterialId = 1, StageCode = "STAGE1", ProductionDepartmentId = 100 },
            new MaterialStageDepartmentContextDto { MaterialId = 1, StageCode = "STAGE1", ProductionDepartmentId = 200 }));

        // 不猜任何一个部门 ⇒ 该物料整条不参与部门锁定 ⇒ Phase2 无路由 ⇒ 业务未排
        Assert.Empty(result.FinalTasks);
        Assert.Contains(result.SolveTraceNotes,
            n => n.ReasonCode == "PRODUCTION_DEPARTMENT_CONFLICT");
    }

    [Fact]
    public async Task 同键同部门重复行_不是冲突_照常排程且不误报()
    {
        var result = await _solver.SolveAsync(Build(
            new MaterialStageDepartmentContextDto { MaterialId = 1, StageCode = "STAGE1", ProductionDepartmentId = 100 },
            new MaterialStageDepartmentContextDto { MaterialId = 1, StageCode = "STAGE1", ProductionDepartmentId = 100 }));

        // 排得出来 ⇒ 部门锁定正常过了（冲突才会让整条 Routing 被剔除、一个 Task 都没有）
        var task = Assert.Single(result.FinalTasks);
        Assert.Equal(1, task.ResourceId);
        Assert.DoesNotContain(result.SolveTraceNotes,
            n => n.ReasonCode == "PRODUCTION_DEPARTMENT_CONFLICT");
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private static DomainSolveRequest Build(params MaterialStageDepartmentContextDto[] departmentContexts)
        => new()
        {
            PlanVersionId = 1,
            ScheduleRunId = 42,
            DomainKey = "DOMAIN",
            PlanningStart = Day,
            PlanningEnd = Day.AddDays(30),
            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                new()
                {
                    LogicalDemandKey = "D1", PlanVersionId = 1L, DomainKey = "DOMAIN",
                    AllocationSequence = 1, DemandKey = "D1", MaterialId = 1, FactoryId = 1,
                    StartStageCode = "STAGE1",
                    NetOutputQty = 1m, PlannedProcessQty = 1m,
                    RequiredAvailableTime = Day.AddDays(20), DemandSequence = 1,
                    QuantityAuthority = QuantityAuthorityValues.None
                }
            },
            RoutingOperations = new List<RoutingOperation>
            {
                new()
                {
                    MaterialId = 1, ProductionDepartmentId = 100, RouteCode = "DEFAULT",
                    OperationCode = "OP10", StageCode = "STAGE1",
                    StandardDuration = 60m,                    // 秒 = 1 分钟
                    OperationPlanningMode = "FINITE_RESOURCE"
                }
            },
            OperationResourceEligibility = new List<OperationResourceEligibility>
            {
                new()
                {
                    MaterialId = 1, ProductionDepartmentId = 100, RouteCode = "DEFAULT",
                    OperationCode = "OP10", ResourceId = 1, Priority = 1, CapacityFactor = 1m
                }
            },
            MaterialStageDepartmentContexts = departmentContexts.ToList(),
            Resources = new List<ResourceDefinition>
            {
                new() { ResourceId = 1, ResourceCode = "R1", FactoryCode = "F1", Capacity = 1m }
            },
            CalendarSlots = new List<ResourceCalendarSlot>
            {
                new() { ResourceId = 1, Start = Day, End = Day.AddDays(29), IsAvailable = true }
            },
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD",
                    AllowMerge = false,
                    AllowSplit = false
                },
                BatchPolicies = TestBatchPolicy.Permissive(1, 100)
            }
        };
}
