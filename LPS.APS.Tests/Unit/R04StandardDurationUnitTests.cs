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
/// **R04-S-G-Units 单位契约验收**（0号位 2026-10-27《01_正式回复1号位_R04秒转分钟唯一装配_WAIT_ONLY_AUTO与R06本地先修…》§1-R04-S-G-Units
/// ＋ 2026-10-28《00_0号位_3号位秒单位回执核证_无新增裁决_R09旧实现辨析…》§一：**批准方案C**）。
///
/// 现行口径（四层，任一多除/少除 60 本文件就会红）：
///   · 源 MES Cycletime → ODS → APS `RoutingOperation.StandardDuration` = **秒**（原样透传，不改库、不重灌）；
///   · Solver 内 `OperationNode.StandardDuration` = **分钟**（`PhaseOneConstraintBuilder` 唯一装配入口 ÷60，全仓仅一处 `new OperationNode`）；
///   · `RoutingOperation.SetupTime` **不参与**运行（v1.2 §1.2/§20.3 废止其运行真相地位）；
///     换型一律走 `SetupTransitionRuleSnapshot.SetupMinutes` = **分钟**（冻结 DDL v5.1.2），**本次不得 ÷60**；
///   · `ResourceCalendarSlot` 的窗宽 = **自然分钟**，**本次不得 ÷60**。
/// </summary>
public class R04StandardDurationUnitTests
{
    private static readonly DateTime Day = new(2026, 9, 1);

    private readonly FiniteCapacitySolver _solver = new();

    /// <summary>源 60 秒 = 1 分钟：1 件工序只占资源 1 分钟（少这一次 ÷60 就会变成 60 分钟）。</summary>
    [Fact]
    public async Task 源秒转分钟_60秒单件工序_资源占用1分钟()
    {
        var result = await Solve(standardDurationSeconds: 60m, plannedProcessQty: 1m);

        Assert.True(result.Success, result.ErrorMessage);
        var task = Assert.Single(result.FinalTasks);
        Assert.Equal(1d, OccupiedMinutes(task), 3);
    }

    /// <summary>同一路由下 1 件与 60 件的资源占用必须成 60 倍（真实 PlannedQty 参与，而不是只看标准工时）。</summary>
    [Fact]
    public async Task 同路由_1件与60件_资源占用成60倍()
    {
        var one = await Solve(standardDurationSeconds: 60m, plannedProcessQty: 1m);
        var sixty = await Solve(standardDurationSeconds: 60m, plannedProcessQty: 60m);

        Assert.True(one.Success, one.ErrorMessage);
        Assert.True(sixty.Success, sixty.ErrorMessage);

        Assert.Equal(1d, OccupiedMinutes(Assert.Single(one.FinalTasks)), 3);
        Assert.Equal(60d, OccupiedMinutes(Assert.Single(sixty.FinalTasks)), 3);
    }

    /// <summary>
    /// 日历**不被缩放**：窗宽 30 分钟。
    ///   1 件（1 分钟）⇒ 装得下；60 件（60 分钟）⇒ 装不下。
    /// 这条同时反证另外两处：日历若被 ÷60 则 1 件也装不下；标准工时若没 ÷60 则 1 件也装不下（60 分钟 &gt; 30 分钟窗）。
    /// </summary>
    [Fact]
    public async Task 日历不被缩放_30分钟窗只装得下1件()
    {
        var one = await Solve(standardDurationSeconds: 60m, plannedProcessQty: 1m, calendarMinutes: 30);
        Assert.True(one.Success, one.ErrorMessage);
        Assert.Single(one.FinalTasks);

        var sixty = await Solve(standardDurationSeconds: 60m, plannedProcessQty: 60m, calendarMinutes: 30);
        Assert.Empty(sixty.FinalTasks);
    }

    /// <summary>
    /// 换型分钟**不被缩放**：DEFAULT 规则 30 分钟。
    /// ⚠ 换型是「前一产品 → 后一产品」的转换，资源**首次开工**没有前产品 ⇒ 不产换型时间；且换型
    ///   记在 <see cref="FinalTaskDraft.SetupTime"/> 上、把**加工起点**往后推，**不**并入 `PlannedEnd − PlannedStart`
    ///   （后者只含有效加工）。故本算例排两次、断言换型值与起点位移：
    ///   首件 1 分钟加工、`SetupTime = 0`；次件 `SetupTime = 30` 且起点 = 前件终点 + 30 分钟。
    ///   （换型若被 ÷60 会变成 0.5 分钟。）
    /// </summary>
    [Fact]
    public async Task 换型分钟不被缩放_30分钟DEFAULT规则按30分钟计入()
    {
        var result = await SolveTwoMaterialsWithSetup(setupMinutes: 30m);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.FinalTasks.Count);

        var ordered = result.FinalTasks.OrderBy(t => t.PlannedStartTime).ToList();
        Assert.Equal(1d, OccupiedMinutes(ordered[0]), 3);    // 首件：无前产品 ⇒ 无换型
        Assert.Equal(0m, ordered[0].SetupTime);
        Assert.Equal(1d, OccupiedMinutes(ordered[1]), 3);    // 次件：有效加工仍只 1 分钟
        Assert.Equal(30m, ordered[1].SetupTime);             // 换型 30 分钟，**未被 ÷60**
        Assert.Equal(ordered[0].PlannedEndTime.AddMinutes(30), ordered[1].PlannedStartTime);
    }

    // ─────────────────────────── 构造辅助 ───────────────────────────

    private static double OccupiedMinutes(FinalTaskDraft task)
        => (task.PlannedEndTime - task.PlannedStartTime).TotalMinutes;

    /// <summary>两个物料共用 R1，各一道 60 秒工序（各 1 件），带一条 DEFAULT 换型规则。</summary>
    private Task<DomainSolveResult> SolveTwoMaterialsWithSetup(decimal setupMinutes)
    {
        var materials = new[] { 1, 2 };

        var request = new DomainSolveRequest
        {
            PlanVersionId = 1,
            ScheduleRunId = 42,
            DomainKey = "DOMAIN",
            PlanningStart = Day,
            PlanningEnd = Day.AddDays(30),
            LogicalProductionDemands = materials.Select((m, i) => new LogicalProductionDemand
            {
                LogicalDemandKey = $"D{m}", PlanVersionId = 1L, DomainKey = "DOMAIN",
                AllocationSequence = i + 1, DemandKey = $"D{m}", MaterialId = m, FactoryId = 1,
                StartStageCode = "STAGE1",
                NetOutputQty = 1m, PlannedProcessQty = 1m,
                RequiredAvailableTime = Day.AddDays(20), DemandSequence = i + 1,
                QuantityAuthority = QuantityAuthorityValues.None
            }).ToList(),
            RoutingOperations = materials.Select(m => new RoutingOperation
            {
                MaterialId = m, ProductionDepartmentId = 100, RouteCode = "DEFAULT",
                OperationCode = "OP10", StageCode = "STAGE1",
                StandardDuration = 60m,                       // 源事实：**秒** = 1 分钟
                OperationPlanningMode = "FINITE_RESOURCE"
            }).ToList(),
            OperationResourceEligibility = materials.Select(m => new OperationResourceEligibility
            {
                MaterialId = m, ProductionDepartmentId = 100, RouteCode = "DEFAULT",
                OperationCode = "OP10", ResourceId = 1, Priority = 1, CapacityFactor = 1m
            }).ToList(),
            MaterialStageDepartmentContexts = materials.Select(m => new MaterialStageDepartmentContextDto
            {
                MaterialId = m, StageCode = "STAGE1", ProductionDepartmentId = 100
            }).ToList(),
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
                BatchPolicies = TestBatchPolicy.Permissive(materials, 100),
                SetupTransitionRules = new List<SetupTransitionRuleSnapshot>
                {
                    new()
                    {
                        ProductionDepartmentId = 100, StageCode = "STAGE1",
                        OperationCode = "OP10", ResourceId = 1,
                        RuleType = "DEFAULT", SetupMinutes = setupMinutes
                    }
                }
            }
        };

        return _solver.SolveAsync(request);
    }

    private Task<DomainSolveResult> Solve(
        decimal standardDurationSeconds,
        decimal plannedProcessQty,
        double? calendarMinutes = null,
        decimal? setupMinutes = null)
    {
        var windowEnd = calendarMinutes.HasValue
            ? Day.AddMinutes(calendarMinutes.Value)
            : Day.AddDays(29);

        var request = new DomainSolveRequest
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
                    NetOutputQty = plannedProcessQty, PlannedProcessQty = plannedProcessQty,
                    RequiredAvailableTime = Day.AddDays(20), DemandSequence = 1,
                    // 本组用例只验单位、不验数量权威分级 ⇒ 显式 NONE，避免多出 QUANTITY_AUTHORITY_ABSENT 汇总条。
                    QuantityAuthority = QuantityAuthorityValues.None
                }
            },
            RoutingOperations = new List<RoutingOperation>
            {
                new()
                {
                    MaterialId = 1, ProductionDepartmentId = 100, RouteCode = "DEFAULT",
                    OperationCode = "OP10", StageCode = "STAGE1",
                    StandardDuration = standardDurationSeconds,   // 源事实：**秒**
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
            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = 1, StageCode = "STAGE1", ProductionDepartmentId = 100 }
            },
            Resources = new List<ResourceDefinition>
            {
                new() { ResourceId = 1, ResourceCode = "R1", FactoryCode = "F1", Capacity = 1m }
            },
            CalendarSlots = new List<ResourceCalendarSlot>
            {
                new() { ResourceId = 1, Start = Day, End = windowEnd, IsAvailable = true }
            },
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD",
                    AllowMerge = false,
                    AllowSplit = false
                },
                BatchPolicies = TestBatchPolicy.Permissive(1, 100),
                SetupTransitionRules = setupMinutes.HasValue
                    ? new List<SetupTransitionRuleSnapshot>
                    {
                        new()
                        {
                            ProductionDepartmentId = 100, StageCode = "STAGE1",
                            OperationCode = "OP10", ResourceId = 1,
                            RuleType = "DEFAULT", SetupMinutes = setupMinutes.Value
                        }
                    }
                    : new List<SetupTransitionRuleSnapshot>()
            }
        };

        return _solver.SolveAsync(request);
    }
}
