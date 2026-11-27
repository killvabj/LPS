using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Shared.Models;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **0号位 执行函 v1.7 §B-2「1有责任修正不合理 first-fit」**：资源选择的**快速正反例**。
///
/// <para>判词逐字：「同优先、同资格、同硬约束时不得因输入行序接受远期方案而忽视更早可行资源」；
/// 「不能全局统一成'任何场景最早完成、Priority 永远降级'」；「不得硬编码物料/槽号或轮询均分」；
/// 「比较应无副作用；只提交胜出方案」。</para>
///
/// <para>本类锁四件事（纯内存 `FiniteCapacitySolver.SolveAsync`，不触库、不读写文件、**不整域试跑**）：</para>
/// <list type="number">
///   <item><b>正例</b>：同 Priority、同 CapacityFactor、日历同样合法的两台资源，行序在前的那台**晚 3 小时**
///         才开始可用 ⇒ 必须选**更早完成**的后一台（改前会选行序在前那台 = 缺陷本体）；</item>
///   <item><b>正例</b>：两台等价资源、两条同规格需求 ⇒ 第二条必须落到另一台（改前两条都堆在第一台）；</item>
///   <item><b>反例</b>：高优先层（Priority=1）虽然更晚完成，**不得**被低优先层（Priority=100）抢走
///         —— 证明不是「任何场景最早完成、Priority 永远降级」；</item>
///   <item><b>反例</b>：需求级显式 `PreferredResourceId` 的资源虽然更晚可用，**不得**被时点比较改派
///         —— 业务显式排序优先于同层时点比较。</item>
/// </list>
/// </summary>
public class ResourceSelectionSameTierEarliestTests
{
    private static readonly DateTime Day = new(2026, 9, 1, 0, 0, 0);
    private const string Factory = "F1";
    private const int Dept = 2101;

    private readonly FiniteCapacitySolver _solver = new();

    /// <summary>
    /// **正例 1（同优先层取最早完成）**：R1 行序在前但 Day+3h 才可用；R2 行序在后、Day 起可用。
    /// 60 分钟工序 ⇒ R2 于 Day+1h 完成、R1 于 Day+4h 完成 ⇒ 必须选 R2。
    /// 改前 first-fit 会选 R1（行序第一台即可容纳）—— 这正是 62137/`ASSY_SLOT_01` 单台吃满的机制。
    /// </summary>
    [Fact]
    public async Task 同优先层同资格_取最早完成_不取行序第一台()
    {
        var result = await _solver.SolveAsync(Build(
            resources: new[]
            {
                new ResSpec(30011, Priority: 100, FromOffset: TimeSpan.FromHours(3), ToOffset: TimeSpan.FromHours(8)),
                new ResSpec(30012, Priority: 100, FromOffset: TimeSpan.Zero, ToOffset: TimeSpan.FromHours(8))
            },
            demands: new[]
            {
                new DemSpec("D1", MaterialId: 8901, Qty: 1m, DurationMinutes: 60m, Seq: 1)
            }));

        Assert.True(result.Success, result.ErrorMessage);
        var task = Assert.Single(result.FinalTasks);
        Assert.Equal(30012, task.ResourceId);
        Assert.Equal(Day.AddHours(1), task.PlannedEndTime);
    }

    /// <summary>
    /// **正例 2（同层资源被用满后轮到下一台）**：两台完全等价的资源、两条同规格需求。
    /// 第一条落在先出现的那台（同刻并列 ⇒ 保留原行序，稳定性要求）；第二条必须落到**另一台**，
    /// 而不是排在第一条后面等 1 小时。
    /// 改前：两条都落在 `30041`。
    /// </summary>
    [Fact]
    public async Task 两条同规格需求_同层资源应分散到两台()
    {
        var result = await _solver.SolveAsync(Build(
            resources: new[]
            {
                new ResSpec(30041, Priority: 100, FromOffset: TimeSpan.Zero, ToOffset: TimeSpan.FromHours(8)),
                new ResSpec(30042, Priority: 100, FromOffset: TimeSpan.Zero, ToOffset: TimeSpan.FromHours(8))
            },
            demands: new[]
            {
                new DemSpec("D1", MaterialId: 8941, Qty: 1m, DurationMinutes: 60m, Seq: 1),
                new DemSpec("D2", MaterialId: 8942, Qty: 1m, DurationMinutes: 60m, Seq: 2)
            }));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.FinalTasks.Count);
        var used = result.FinalTasks.Select(t => t.ResourceId).Distinct().OrderBy(x => x).ToArray();
        Assert.Equal(new int?[] { 30041, 30042 }, used);
    }

    /// <summary>
    /// **反例 1（Priority 不得被时点降级）**：R1 是高优先层（Priority=1）但 Day+2h 才可用；
    /// R2 是低优先层（Priority=100）Day 起可用、完成更早。
    /// ⇒ 必须选 R1。若选 R2，说明实现退化成了「任何场景最早完成」——正是 0号位 禁止的口径。
    /// </summary>
    [Fact]
    public async Task 跨优先层_高优先层晚完成也不得被低优先层抢走()
    {
        var result = await _solver.SolveAsync(Build(
            resources: new[]
            {
                new ResSpec(30021, Priority: 1, FromOffset: TimeSpan.FromHours(2), ToOffset: TimeSpan.FromHours(8)),
                new ResSpec(30022, Priority: 100, FromOffset: TimeSpan.Zero, ToOffset: TimeSpan.FromHours(8))
            },
            demands: new[]
            {
                new DemSpec("D1", MaterialId: 8921, Qty: 1m, DurationMinutes: 60m, Seq: 1)
            }));

        Assert.True(result.Success, result.ErrorMessage);
        var task = Assert.Single(result.FinalTasks);
        Assert.Equal(30021, task.ResourceId);
        Assert.Equal(Day.AddHours(3), task.PlannedEndTime);
    }

    /// <summary>
    /// **反例 2（需求级显式偏好优先于同层时点比较）**：两台同优先层资源，偏好资源 Day+3h 才可用、
    /// 另一台 Day 起可用。需求显式 `PreferredResourceId = 30031` ⇒ 必须落在偏好资源上。
    /// （软偏好 = 不 Hard Lock：这里偏好资源可用，故必然命中；不可用的回落由既有 P1-11 用例覆盖。）
    /// </summary>
    [Fact]
    public async Task 需求级显式偏好_不被同层最早完成改派()
    {
        var result = await _solver.SolveAsync(Build(
            resources: new[]
            {
                new ResSpec(30031, Priority: 100, FromOffset: TimeSpan.FromHours(3), ToOffset: TimeSpan.FromHours(8)),
                new ResSpec(30032, Priority: 100, FromOffset: TimeSpan.Zero, ToOffset: TimeSpan.FromHours(8))
            },
            demands: new[]
            {
                new DemSpec("D1", MaterialId: 8931, Qty: 1m, DurationMinutes: 60m, Seq: 1,
                    PreferredResourceId: 30031)
            }));

        Assert.True(result.Success, result.ErrorMessage);
        var task = Assert.Single(result.FinalTasks);
        Assert.Equal(30031, task.ResourceId);
        Assert.Equal(Day.AddHours(4), task.PlannedEndTime);
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private sealed record ResSpec(int ResourceId, int Priority, TimeSpan FromOffset, TimeSpan ToOffset);

    private sealed record DemSpec(
        string Key, int MaterialId, decimal Qty, decimal DurationMinutes, int Seq,
        int? PreferredResourceId = null);

    private static DomainSolveRequest Build(ResSpec[] resources, DemSpec[] demands)
    {
        const string stage = "S1";
        const string op = "OP10";

        var demandList = new List<LogicalProductionDemand>();
        var ops = new List<RoutingOperation>();
        var elig = new List<OperationResourceEligibility>();
        var depts = new List<MaterialStageDepartmentContextDto>();

        foreach (var d in demands)
        {
            demandList.Add(new LogicalProductionDemand
            {
                LogicalDemandKey = d.Key,
                PlanVersionId = 1L,
                DomainKey = "D",
                AllocationSequence = d.Seq,
                DemandKey = d.Key,
                DemandSequence = d.Seq,
                MaterialId = d.MaterialId,
                FactoryId = 1,
                FactoryCode = Factory,
                StartStageCode = stage,
                NetOutputQty = d.Qty,
                PlannedProcessQty = d.Qty,
                RequiredAvailableTime = Day.AddDays(1),
                PreferredResourceId = d.PreferredResourceId
            });

            ops.Add(new RoutingOperation
            {
                MaterialId = d.MaterialId,
                ProductionDepartmentId = Dept,
                RouteCode = "RT",
                PathId = 1,
                OperationCode = op,
                StageCode = stage,
                // `RoutingOperation.StandardDuration` 输入单位是**秒**（MES CycletTime），
                // Phase1 会 `/60` 归一成分钟 ⇒ 想要整批共 N 分钟，这里填 `N × 60 ÷ 计划数量`。
                StandardDuration = d.DurationMinutes * 60m / d.Qty,
                SetupTime = 0m
            });

            elig.AddRange(resources.Select(r => new OperationResourceEligibility
            {
                MaterialId = d.MaterialId,
                ProductionDepartmentId = Dept,
                RouteCode = "RT",
                PathId = 1,
                OperationCode = op,
                ResourceId = r.ResourceId,
                Priority = r.Priority,
                CapacityFactor = 1m
            }));

            depts.Add(new MaterialStageDepartmentContextDto
            {
                MaterialId = d.MaterialId,
                StageCode = stage,
                ProductionDepartmentId = Dept
            });
        }

        var resourceDefs = resources.Select(r => new ResourceDefinition
        {
            ResourceId = r.ResourceId,
            ResourceCode = $"R{r.ResourceId}",
            FactoryCode = Factory,
            Capacity = 1m
        }).ToList();

        var slots = resources.Select(r => new ResourceCalendarSlot
        {
            ResourceId = r.ResourceId,
            Start = Day + r.FromOffset,
            End = Day + r.ToOffset,
            IsAvailable = true
        }).ToList();

        var materialIds = demands.Select(d => d.MaterialId).ToList();

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "D",
            PlanningStart = Day,
            PlanningEnd = Day.AddDays(1),
            LogicalProductionDemands = demandList,
            MaterialRequirementLinks = new List<MaterialRequirementLink>(),
            RoutingOperations = ops,
            RoutingDependencies = new List<RoutingDependency>(),
            OperationResourceEligibility = elig,
            MaterialStageDepartmentContexts = depts,
            DepartmentWorkWindows = new[]
            {
                new DepartmentWorkWindow { FactoryId = 1, DepartmentId = Dept, Start = Day, End = Day.AddDays(1) }
            },
            Resources = resourceDefs,
            CalendarSlots = slots,
            ExecutionConstraints = Array.Empty<ExecutionConstraint>(),
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters { SchedulingDirection = "FORWARD" },
                BatchPolicies = materialIds.Select(m => new BatchPolicyRuleSnapshot
                {
                    MaterialId = m,
                    ProductionDepartmentId = Dept,
                    MinExecutionBatchQty = null,
                    MaxExecutionBatchQty = null,
                    AllowSplit = false,
                    AllowMerge = true
                }).ToList(),
                SetupTransitionRules = Array.Empty<SetupTransitionRuleSnapshot>()
            }
        };
    }
}
