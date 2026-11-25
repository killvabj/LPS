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
/// **无 Routing 未排的诊断分因必须指向真实阻断**（2026-11-25 修复；对应 0号位 §3 T4「根因须指向最早真实阻断」）。
///
/// <para>修复前的缺陷（实测样本 `999634_72`）：链 `BJ_ASSY(10) > BJ_FINAL(20)`，`BJ_FINAL` **有 LT 但无部门**
///   ⇒ 无合法 WORK 窗 ⇒ 需求实际未排；而文案写的是「无Routing Stage **有合法 LT** ⇒ 按冻结只形成
///   StageTimingNode、不生成 Task（O-009）」——**读起来像已放行，与"未排"结果自相矛盾**。</para>
///
/// <para>修复后：文案与 `IsRoutelessTimingOnly` **同源**（复用同一个 `TryAdvanceByWorkHours` 预演），
///   卡在哪一道就报哪一道，不可能再与结果不一致。</para>
///
/// ⚠ 纯内存：只调公开入口 <see cref="FiniteCapacitySolver.SolveAsync"/>，不触库、不读写文件。
/// </summary>
public class RoutelessDiagnosticAttributionTests
{
    private static readonly DateTime Day = new(2026, 9, 1, 0, 0, 0);
    private const int M = 31659;
    private const int Dept = 1094;

    private readonly FiniteCapacitySolver _solver = new();

    private static string NoRoutingNote(DomainSolveResult r)
    {
        var note = r.SolveTraceNotes.FirstOrDefault(n =>
            (n.Message ?? string.Empty).Contains("P2_NO_ROUTING", StringComparison.Ordinal)
            && (n.Message ?? string.Empty).Contains("分因", StringComparison.Ordinal));
        Assert.NotNull(note);
        return note!.Message!;
    }

    [Fact]
    public async Task 链上某Stage有LT但无部门_文案必须报缺WORK窗而非有合法LT()
    {
        // CS1 有部门、CS2 只有 LT 没有部门 ⇒ 与 999634_72 同形
        var result = await _solver.SolveAsync(Build(withDeptForCs2: false));

        Assert.Contains(result.UnscheduledTasks, u => u.DraftId == "D1");

        var msg = NoRoutingNote(result);

        Assert.Contains("无合法 WORK 窗口", msg, StringComparison.Ordinal);
        Assert.Contains("CS2", msg, StringComparison.Ordinal);
        Assert.DoesNotContain("有合法 LT** ⇒ 按冻结只形成 StageTimingNode", msg, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 链上缺LT时_文案必须报STAGE_LEADTIME_MISSING()
    {
        var result = await _solver.SolveAsync(Build(withDeptForCs2: true, dropLeadTimeFor: "CS2"));

        Assert.Contains(result.UnscheduledTasks, u => u.DraftId == "D1");
        var msg = NoRoutingNote(result);
        Assert.Contains("STAGE_LEADTIME_MISSING", msg, StringComparison.Ordinal);
        Assert.Contains("CS2", msg, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 起点不在链内时_文案必须报起点非法()
    {
        var result = await _solver.SolveAsync(Build(withDeptForCs2: true, startStage: "XX_MACH"));

        Assert.Contains(result.UnscheduledTasks, u => u.DraftId == "D1");
        var msg = NoRoutingNote(result);
        Assert.Contains("不在本需求的 Stage 链内", msg, StringComparison.Ordinal);
    }

    private static DomainSolveRequest Build(
        bool withDeptForCs2,
        string? dropLeadTimeFor = null,
        string startStage = "CS1")
    {
        var ctx = new List<MaterialStageDepartmentContextDto>
        {
            new() { MaterialId = M, StageCode = "CS1", ProductionDepartmentId = Dept }
        };
        if (withDeptForCs2)
        {
            ctx.Add(new MaterialStageDepartmentContextDto { MaterialId = M, StageCode = "CS2", ProductionDepartmentId = Dept });
        }

        var lts = new List<StageLeadTimeFact>
        {
            new() { MaterialId = M, StageCode = "CS1", LeadTimeHours = 7m, MatchLevel = "DEPT_EXACT" }
        };
        if (!string.Equals(dropLeadTimeFor, "CS2", StringComparison.Ordinal))
        {
            lts.Add(new StageLeadTimeFact { MaterialId = M, StageCode = "CS2", LeadTimeHours = 3m, MatchLevel = "DEPT_EXACT" });
        }

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "D",
            PlanningStart = Day,
            PlanningEnd = Day.AddDays(90),
            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                new()
                {
                    LogicalDemandKey = "D1", PlanVersionId = 1, DomainKey = "D",
                    AllocationSequence = 1, DemandKey = "D1",
                    MaterialId = M, FactoryId = 1, FactoryCode = "BJ",
                    StartStageCode = startStage, StartOperationCode = null,
                    NetOutputQty = 100m, PlannedProcessQty = 100m,
                    RequiredAvailableTime = Day.AddDays(20),
                    RouteCode = null, PathId = null
                }
            },
            RoutingOperations = new List<RoutingOperation>(),
            RoutingDependencies = new List<RoutingDependency>(),
            OperationResourceEligibility = new List<OperationResourceEligibility>(),
            MaterialStageDepartmentContexts = ctx,
            DepartmentWorkWindows = new[]
            {
                new DepartmentWorkWindow { FactoryId = 1, DepartmentId = Dept, Start = Day, End = Day.AddYears(100) }
            },
            StageLeadTimes = lts,
            StageSequenceChains = new List<StageSequenceChain>
            {
                new()
                {
                    MaterialId = M,
                    Stages = new List<StageSequenceStep>
                    {
                        new() { StageCode = "CS1", StageSeq = 10 },
                        new() { StageCode = "CS2", StageSeq = 20 }
                    }
                }
            },
            Resources = new List<ResourceDefinition>(),
            CalendarSlots = new List<ResourceCalendarSlot>(),
            ExecutionConstraints = Array.Empty<ExecutionConstraint>(),
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD", AllowMerge = false, AllowSplit = false
                },
                BatchPolicies = Array.Empty<BatchPolicyRuleSnapshot>(),
                SetupTransitionRules = Array.Empty<SetupTransitionRuleSnapshot>()
            }
        };
    }
}
