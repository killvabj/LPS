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
/// **无 Routing Stage 之间的 StageHandoffWait**（0号位 2026-11-24 §二.3 逐字：
///   「1号位将合法 StageTimingNode 作为当前需求/合法上游实际时间节点参与同一求解图，
///    并按 Frozen `StageHandoffWaitRules` **真实有向对应用一次**」）。
///
/// <para>与既有 <c>StageHandoffWaitTests</c> **互补不重复**：那个测的是
/// `PhaseTwoInitialScheduler.ApplyStageHandoffWait`（**有 Task** 的排程路径，基准取「本批 FromStage 全部 Task 最晚结束」）；
/// 本文件测 `StageTimingNodeBuilder.ApplyRoutelessStageHandoffWait`（**纯计时链、无 Task**，基准取「链上前一个已计时 Stage 结束」）。</para>
///
/// <para>改造前本类只按 `StageLeadTimeFact.LeadTimeHours` 累计，**交接等待被漏计** ⇒ 下游拿到的计划时间偏早。</para>
///
/// <list type="bullet">
///   <item>正例：CS1→CS2 配 ELAPSED 120 分钟 ⇒ `CompletionTime` 比无规则时**晚 120 分钟**；</item>
///   <item>反例①：规则方向相反（CS2→CS1）⇒ 不应用（A→B 与 B→A 是独立规则）；</item>
///   <item>反例②：厂码不匹配 ⇒ 不应用（键域 = 需求自带厂代码）；</item>
///   <item>反例③：`IsActive != true` ⇒ 不参与匹配。</item>
/// </list>
///
/// ⚠ 纯内存：只调公开入口 <see cref="FiniteCapacitySolver.SolveAsync"/>，不触库、不读写文件。
/// </summary>
public class RoutelessStageTimingHandoffWaitTests
{
    private static readonly DateTime Day = new(2026, 9, 1, 0, 0, 0);
    private const int M = 1;
    private const int Dept = 100;

    private readonly FiniteCapacitySolver _solver = new();

    [Fact]
    public async Task 正例_CS1到CS2有120分钟ELAPSED_完成时间应晚120分钟()
    {
        var baseline = await _solver.SolveAsync(Build(rule: null));
        var withRule = await _solver.SolveAsync(Build(
            rule: new StageHandoffWaitRule
            {
                FactoryCode = "F1", FromStageCode = "CS1", ToStageCode = "CS2",
                WaitMinutes = 120, TimeBasis = StageHandoffTimeBasis.ELAPSED, IsActive = true
            }));

        var b = Assert.Single(baseline.DemandCompletions);
        var w = Assert.Single(withRule.DemandCompletions);

        // 无规则：7h + 3h = Day+10h
        Assert.Equal(Day.AddHours(10), b.CompletionTime);

        // 有规则：7h 结束 → +120min 交接 → 再 3h = Day+12h
        Assert.Equal(Day.AddHours(12), w.CompletionTime);
    }

    [Fact]
    public async Task 反例1_规则方向相反CS2到CS1_不得应用()
    {
        var result = await _solver.SolveAsync(Build(
            rule: new StageHandoffWaitRule
            {
                FactoryCode = "F1", FromStageCode = "CS2", ToStageCode = "CS1",
                WaitMinutes = 120, TimeBasis = StageHandoffTimeBasis.ELAPSED, IsActive = true
            }));

        var f = Assert.Single(result.DemandCompletions);
        Assert.Equal(Day.AddHours(10), f.CompletionTime);
    }

    [Fact]
    public async Task 反例2_厂码不匹配_不得应用()
    {
        var result = await _solver.SolveAsync(Build(
            rule: new StageHandoffWaitRule
            {
                FactoryCode = "OTHER", FromStageCode = "CS1", ToStageCode = "CS2",
                WaitMinutes = 120, TimeBasis = StageHandoffTimeBasis.ELAPSED, IsActive = true
            }));

        var f = Assert.Single(result.DemandCompletions);
        Assert.Equal(Day.AddHours(10), f.CompletionTime);
    }

    [Fact]
    public async Task 反例3_规则未启用_不得应用()
    {
        var result = await _solver.SolveAsync(Build(
            rule: new StageHandoffWaitRule
            {
                FactoryCode = "F1", FromStageCode = "CS1", ToStageCode = "CS2",
                WaitMinutes = 120, TimeBasis = StageHandoffTimeBasis.ELAPSED, IsActive = false
            }));

        var f = Assert.Single(result.DemandCompletions);
        Assert.Equal(Day.AddHours(10), f.CompletionTime);
    }

    private static DomainSolveRequest Build(StageHandoffWaitRule? rule)
        => new()
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
                    MaterialId = M, FactoryId = 1, FactoryCode = "F1",
                    StartStageCode = "CS1", StartOperationCode = null,
                    NetOutputQty = 10m, PlannedProcessQty = 10m,
                    RequiredAvailableTime = Day.AddDays(20),
                    RouteCode = null, PathId = null
                }
            },
            RoutingOperations = new List<RoutingOperation>(),
            RoutingDependencies = new List<RoutingDependency>(),
            OperationResourceEligibility = new List<OperationResourceEligibility>(),
            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = M, StageCode = "CS1", ProductionDepartmentId = Dept },
                new() { MaterialId = M, StageCode = "CS2", ProductionDepartmentId = Dept }
            },
            DepartmentWorkWindows = new[]
            {
                new DepartmentWorkWindow
                {
                    FactoryId = 1, DepartmentId = Dept, Start = Day, End = Day.AddYears(100)
                }
            },
            StageLeadTimes = new List<StageLeadTimeFact>
            {
                new() { MaterialId = M, StageCode = "CS1", LeadTimeHours = 7m, MatchLevel = "DEPT_EXACT" },
                new() { MaterialId = M, StageCode = "CS2", LeadTimeHours = 3m, MatchLevel = "DEPT_EXACT" }
            },
            // 2026-11-25（0号位 v1.2 §3 / Q1）：交接等待的厂键 = **该 Stage 的权威执行厂**，
            //   且**不得回退** `Demand.FactoryCode`。⇒ 夹具改走新契约（链定义带 `StageFactoryCode`），
            //   与生产路径一致；旧物料的 `StageSequenceChains` 不再承载执行厂。
            StageSequenceChains = new List<StageSequenceChain>(),
            StageChainDefinitions = new List<StageChainDefinition>
            {
                new()
                {
                    ChainRefId = "ROOT:1:CS1|2:CS2", StageScopeType = "ROOT", ChainSignature = "1:CS1|2:CS2",
                    Stages = new List<StageSequenceStep>
                    {
                        new() { StageCode = "CS1", StageSeq = 1, ProductionDepartmentId = Dept, StageFactoryCode = "F1" },
                        new() { StageCode = "CS2", StageSeq = 2, ProductionDepartmentId = Dept, StageFactoryCode = "F1" }
                    }
                }
            },
            DemandStageChainBindings = new List<DemandStageChainBinding>
            {
                new()
                {
                    LogicalDemandKey = "D1", BindingKey = "ROOT|BOM|WS",
                    ChainRefId = "ROOT:1:CS1|2:CS2", StageScopeType = "ROOT",
                    Status = StageChainBindingStatus.Resolved
                }
            },
            ExecutionConstraints = Array.Empty<ExecutionConstraint>(),
            Resources = new List<ResourceDefinition>(),
            CalendarSlots = new List<ResourceCalendarSlot>(),
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD", AllowMerge = true, AllowSplit = false
                },
                BatchPolicies = new[]
                {
                    new BatchPolicyRuleSnapshot
                    {
                        MaterialId = M, ProductionDepartmentId = Dept,
                        MinExecutionBatchQty = null, MaxExecutionBatchQty = null,
                        AllowSplit = false, AllowMerge = true
                    }
                },
                SetupTransitionRules = Array.Empty<SetupTransitionRuleSnapshot>(),
                StageHandoffWaitRules = rule is null
                    ? Array.Empty<StageHandoffWaitRule>()
                    : new[] { rule }
            }
        };
}
