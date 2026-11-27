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
/// **0号位《未排案例分类总表》M11-CASE-212800 / 213355**：
/// 「程序当前 `P2_NO_SLOT` **没有**给出『最早可排时刻』——这是可补的」。
///
/// <para>本轮在 `P2_NO_SLOT(正排)` 报文里补**逐资源的最早可容纳时点**，
/// 判据见 `PhaseTwoInitialScheduler.DescribeEarliestFeasiblePerResource`。本类锁三件事：</para>
/// <list type="number">
///   <item>**需跨窗**：工序时长 &gt; 该资源最长日历窗 ⇒ 正文写「需跨窗」并给出**日历末端**，
///         且**不得**编造一个窗内的时刻；</item>
///   <item>**窗内被占满**：时长装得下，但被前面的 Task 占到日历末端 ⇒ 正文写「已被占满至日历末端」；</item>
///   <item>**口径边界**：Phase2 不做跨窗 ⇒ 这两类都**不**给出「最早 X 时刻」。</item>
/// </list>
///
/// <para>⚠ 纯内存：只调 `FiniteCapacitySolver.SolveAsync`，不触库、不读写文件。</para>
/// </summary>
public class NoSlotEarliestFeasibleTests
{
    private static readonly DateTime Day = new(2026, 9, 1, 0, 0, 0);
    private const string Factory = "F1";
    private const int Dept = 2101;

    private readonly FiniteCapacitySolver _solver = new();

    /// <summary>
    /// **反例 1（需跨窗）**：唯一合格资源的日历只有一个 60 分钟的窗，而本工序需 600 分钟
    /// ⇒ 必须留 `P2_NO_SLOT`，且正文含「需跨窗」与「日历末端」，**不含**「最早 」时刻。
    /// </summary>
    [Fact]
    public async Task 单窗装不下_报文给出需跨窗与日历末端_不编造最早时刻()
    {
        // 刻意用**极端短窗**（1 分钟）：无论 StandardDuration 的秒/分换算与批次口径如何，
        //   本工序的时长都远大于 1 分钟 ⇒ 必然「需跨窗」。这样断言的成立**不依赖**单位换算假设。
        var request = Build(
            materialId: 8801,
            demandQty: 1m,
            standardDurationMinutes: 600m,
            resourceIds: new[] { 30001 },
            windowEnd: Day.AddMinutes(1));

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.FinalTasks);

        // ⚠ Phase2 的出口正文最终会**嵌进** Phase4 的 `PHASE4_NO_TASK_AFTER_REPAIR` 回显里
        //   （外层出口码与内层明细不同源）⇒ 断言按**全部 notes 的正文并集**判，
        //   只认「这段文字有没有进结果」，不绑定它挂在哪个 ReasonCode 下。
        var allText = string.Join(" ‖ ", result.SolveTraceNotes.Select(n => n.Message));

        Assert.Contains("P2_NO_SLOT(正排)", allText);
        Assert.Contains("逐资源最早可容纳=", allText);
        Assert.Contains("需跨窗", allText);
        Assert.Contains("日历末端", allText);
        // 不得出现「最早 <时间>」这种窗内时刻（Phase2 不做跨窗，给不出来）
        Assert.DoesNotContain("=最早 ", allText);
    }

    /// <summary>
    /// **反例 2（窗内被占满）**：两需求抢同一个单窗资源；前者占满到窗末，
    /// 后者时长装得下但排不进 ⇒ 正文必须写「已被占满至日历末端」，不是「需跨窗」。
    /// </summary>
    [Fact]
    public async Task 窗够长但被占满_报文给出已占满至日历末端()
    {
        var request = Build(
            materialId: 8802,
            demandQty: 1m,
            standardDurationMinutes: 50m,        // 第二需求需 50 分钟
            resourceIds: new[] { 30002 },
            windowEnd: Day.AddHours(1),          // 窗 08:00-09:00 = 60 分钟（装得下 50）
            secondDemand: (qty: 1m, durationMinutes: 60m));  // 第一需求先占满 60 分钟

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);

        var blocked = result.SolveTraceNotes
            .Where(n => n.ReasonCode == "PHASE2_INITIAL_SCHEDULE_FAILED")
            .Select(n => n.Message)
            .ToList();

        // 必须有一个需求被 NO_SLOT 挡下，且正文如实说明「被占满至日历末端」
        Assert.Contains(blocked, m => m.Contains("P2_NO_SLOT(正排)"));
        Assert.Contains(blocked, m => m.Contains("已被占满至日历末端"));
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private static DomainSolveRequest Build(
        int materialId,
        decimal demandQty,
        decimal standardDurationMinutes,
        int[] resourceIds,
        DateTime windowEnd,
        (decimal qty, decimal durationMinutes)? secondDemand = null)
    {
        const string stage = "S1";
        const string op = "OP10";

        var demands = new List<LogicalProductionDemand>
        {
            new()
            {
                LogicalDemandKey = "D1", PlanVersionId = 1L, DomainKey = "D",
                AllocationSequence = 1, DemandKey = "D1", DemandSequence = 1,
                MaterialId = materialId, FactoryId = 1, FactoryCode = Factory,
                StartStageCode = stage, NetOutputQty = demandQty, PlannedProcessQty = demandQty,
                RequiredAvailableTime = Day.AddDays(1)
            }
        };

        var ops = new List<RoutingOperation>
        {
            new()
            {
                MaterialId = materialId, ProductionDepartmentId = Dept, RouteCode = "RT", PathId = 1,
                OperationCode = op, StageCode = stage,
                // ⚠ `RoutingOperation.StandardDuration` 的输入单位是**秒**（MES CycletTime），
                //   Phase1 会 `/ 60m` 归一成分钟（`PhaseOneConstraintBuilder.cs:1164`）。
                //   ⇒ 想要「整批共 N 分钟」，这里要填 `N × 60 ÷ PlannedProcessQty` 秒。
                StandardDuration = standardDurationMinutes * 60m / demandQty,
                SetupTime = 0m
            }
        };

        var elig = resourceIds.Select(rid => new OperationResourceEligibility
        {
            MaterialId = materialId, ProductionDepartmentId = Dept, RouteCode = "RT", PathId = 1,
            OperationCode = op, ResourceId = rid, Priority = 1, CapacityFactor = 1m
        }).ToList();

        var mats = new List<int> { materialId };

        // 第二需求：同资源、更长时长，靠 AllocationSequence 先排，把窗占满
        if (secondDemand is { } sd)
        {
            const int mat2 = 8999;
            demands.Insert(0, new LogicalProductionDemand
            {
                LogicalDemandKey = "D0", PlanVersionId = 1L, DomainKey = "D",
                AllocationSequence = 0, DemandKey = "D0", DemandSequence = 0,
                MaterialId = mat2, FactoryId = 1, FactoryCode = Factory,
                StartStageCode = stage, NetOutputQty = sd.qty, PlannedProcessQty = sd.qty,
                RequiredAvailableTime = Day.AddDays(1)
            });
            ops.Add(new RoutingOperation
            {
                MaterialId = mat2, ProductionDepartmentId = Dept, RouteCode = "RT", PathId = 1,
                OperationCode = op, StageCode = stage,
                StandardDuration = sd.durationMinutes * 60m / sd.qty,   // 同上：输入单位是秒
                SetupTime = 0m
            });
            elig.AddRange(resourceIds.Select(rid => new OperationResourceEligibility
            {
                MaterialId = mat2, ProductionDepartmentId = Dept, RouteCode = "RT", PathId = 1,
                OperationCode = op, ResourceId = rid, Priority = 1, CapacityFactor = 1m
            }));
            mats.Add(mat2);
        }

        var depts = mats.Select(m => new MaterialStageDepartmentContextDto
        {
            MaterialId = m, StageCode = stage, ProductionDepartmentId = Dept
        }).ToList();

        var resources = resourceIds.Select(rid => new ResourceDefinition
        {
            ResourceId = rid, ResourceCode = $"R{rid}", FactoryCode = Factory, Capacity = 1m
        }).ToList();

        var slots = resourceIds.Select(rid => new ResourceCalendarSlot
        {
            ResourceId = rid, Start = Day, End = windowEnd, IsAvailable = true
        }).ToList();

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "D",
            PlanningStart = Day,
            PlanningEnd = Day.AddDays(1),
            LogicalProductionDemands = demands,
            MaterialRequirementLinks = new List<MaterialRequirementLink>(),
            RoutingOperations = ops,
            RoutingDependencies = new List<RoutingDependency>(),
            OperationResourceEligibility = elig,
            MaterialStageDepartmentContexts = depts,
            DepartmentWorkWindows = new[]
            {
                new DepartmentWorkWindow { FactoryId = 1, DepartmentId = Dept, Start = Day, End = Day.AddDays(1) }
            },
            Resources = resources,
            CalendarSlots = slots,
            ExecutionConstraints = Array.Empty<ExecutionConstraint>(),
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters { SchedulingDirection = "FORWARD" },
                BatchPolicies = mats.Select(m => new BatchPolicyRuleSnapshot
                {
                    MaterialId = m, ProductionDepartmentId = Dept,
                    MinExecutionBatchQty = null, MaxExecutionBatchQty = null,
                    AllowSplit = false, AllowMerge = true
                }).ToList(),
                SetupTransitionRules = Array.Empty<SetupTransitionRuleSnapshot>()
            }
        };
    }
}
