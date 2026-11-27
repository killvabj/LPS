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

    // ────────────────────────────────────────────────────────────────────
    // 2026-11-25（0号位 v1.3 §4.3 / v1.5 ①）：Task→Timing 的 **WORK 基准**变体
    //   原夹具窗口是「Day → +100 年」全开 ⇒ WORK 与 ELAPSED 结果相同，**测不出**区别。
    //   本组改用「每天 08:00–16:00」的真实 OFF 窗口，三组结果两两不同：
    //     无规则 ⇒ 09-02 10:00；ELAPSED 300 ⇒ 09-02 11:00；WORK 300 ⇒ 09-02 15:00。
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WORK基准_交接等待按合法窗口推进_不得按自然分钟直加()
    {
        var baseline = await _solver.SolveAsync(BuildWithOffWindows(rule: null));
        var elapsed = await _solver.SolveAsync(BuildWithOffWindows(new StageHandoffWaitRule
        {
            FactoryCode = "F1", FromStageCode = "CS1", ToStageCode = "CS2",
            WaitMinutes = 300, TimeBasis = StageHandoffTimeBasis.ELAPSED, IsActive = true
        }));
        var work = await _solver.SolveAsync(BuildWithOffWindows(new StageHandoffWaitRule
        {
            FactoryCode = "F1", FromStageCode = "CS1", ToStageCode = "CS2",
            WaitMinutes = 300, TimeBasis = StageHandoffTimeBasis.WORK, IsActive = true
        }));

        // 无规则：CS1 08:00→15:00（7h），CS2 15:00→16:00 + 次日 08:00→10:00（3h）
        Assert.Equal(Day.AddDays(1).AddHours(10), Assert.Single(baseline.DemandCompletions).CompletionTime);

        // ELAPSED 300：15:00 + 5h 自然 = 20:00（越窗）；CS2 3h 落次日 08:00→11:00
        Assert.Equal(Day.AddDays(1).AddHours(11), Assert.Single(elapsed.DemandCompletions).CompletionTime);

        // WORK 300：15:00 起只有 1 有效小时到 16:00，余 4h 落次日 08:00→12:00；
        //   再 CS2 3h ⇒ 12:00→15:00。（把它当自然分钟会得到 11:00，即上一行，必须不同）
        Assert.Equal(Day.AddDays(1).AddHours(15), Assert.Single(work.DemandCompletions).CompletionTime);
    }

    [Fact]
    public async Task 无链定义时_按需求厂取窗_不得判STAGE_FACTORY_UNKNOWN()
    {
        // 2026-11-25（§4.2 两档口径的**下档**）：该需求没有任何链 ⇒ 不存在「Stage 执行厂」事实，
        //   需求厂是唯一可用事实（不是回落）。⇒ 正常推进，且**不得**出现 STAGE_FACTORY_UNKNOWN。
        var result = await _solver.SolveAsync(BuildWithOffWindows(rule: null, withChain: false));

        Assert.Equal(Day.AddDays(1).AddHours(10), Assert.Single(result.DemandCompletions).CompletionTime);
        Assert.DoesNotContain(
            result.SolveTraceNotes,
            n => n.Message is not null && n.Message.Contains("STAGE_FACTORY_UNKNOWN", StringComparison.Ordinal));
    }

    /// <summary>
    /// 每天 `08:00–16:00` 两日窗口版本。默认带链且链里写明 `StageFactoryId = 1`（走上档）；
    /// <paramref name="withChain"/> = false 时链/绑定全不给（走「无链」下档）。
    /// </summary>
    private static DomainSolveRequest BuildWithOffWindows(StageHandoffWaitRule? rule, bool withChain = true)
    {
        var request = Build(rule);

        var windows = new List<DepartmentWorkWindow>
        {
            new() { FactoryId = 1, DepartmentId = Dept, Start = Day.AddHours(8), End = Day.AddHours(16) },
            new() { FactoryId = 1, DepartmentId = Dept, Start = Day.AddDays(1).AddHours(8), End = Day.AddDays(1).AddHours(16) }
        };

        if (!withChain)
        {
            // 「无链」= 只有旧物料的 `StageSequenceChains`（链步不带执行厂），没有新契约链定义/绑定。
            //   ⚠ 完全不给链则 R09-② 判「结构上无法定位」⇒ 不推进，那是另一条测试口径。
            return new DomainSolveRequest
            {
                PlanVersionId = request.PlanVersionId,
                DomainKey = request.DomainKey,
                PlanningStart = request.PlanningStart,
                PlanningEnd = request.PlanningEnd,
                LogicalProductionDemands = request.LogicalProductionDemands,
                RoutingOperations = request.RoutingOperations,
                RoutingDependencies = request.RoutingDependencies,
                OperationResourceEligibility = request.OperationResourceEligibility,
                MaterialStageDepartmentContexts = request.MaterialStageDepartmentContexts,
                DepartmentWorkWindows = windows,
                StageLeadTimes = request.StageLeadTimes,
                StageSequenceChains = new List<StageSequenceChain>
                {
                    new()
                    {
                        MaterialId = M,
                        Stages = new List<StageSequenceStep>
                        {
                            new() { StageCode = "CS1", StageSeq = 1 },
                            new() { StageCode = "CS2", StageSeq = 2 }
                        }
                    }
                },
                StageChainDefinitions = new List<StageChainDefinition>(),
                DemandStageChainBindings = new List<DemandStageChainBinding>(),
                ExecutionConstraints = request.ExecutionConstraints,
                Resources = request.Resources,
                CalendarSlots = request.CalendarSlots,
                StrategySnapshot = request.StrategySnapshot
            };
        }

        // 上档：链步带上厂 Id（`StageFactoryId`），窗口按该厂取。
        var withIds = new List<StageChainDefinition>
        {
            new()
            {
                ChainRefId = "ROOT:1:CS1|2:CS2", StageScopeType = "ROOT", ChainSignature = "1:CS1|2:CS2",
                Stages = new List<StageSequenceStep>
                {
                    new() { StageCode = "CS1", StageSeq = 1, ProductionDepartmentId = Dept, StageFactoryCode = "F1", StageFactoryId = 1 },
                    new() { StageCode = "CS2", StageSeq = 2, ProductionDepartmentId = Dept, StageFactoryCode = "F1", StageFactoryId = 1 }
                }
            }
        };

        return new DomainSolveRequest
        {
            PlanVersionId = request.PlanVersionId,
            DomainKey = request.DomainKey,
            PlanningStart = request.PlanningStart,
            PlanningEnd = request.PlanningEnd,
            LogicalProductionDemands = request.LogicalProductionDemands,
            RoutingOperations = request.RoutingOperations,
            RoutingDependencies = request.RoutingDependencies,
            OperationResourceEligibility = request.OperationResourceEligibility,
            MaterialStageDepartmentContexts = request.MaterialStageDepartmentContexts,
            DepartmentWorkWindows = windows,
            StageLeadTimes = request.StageLeadTimes,
            StageSequenceChains = request.StageSequenceChains,
            StageChainDefinitions = withIds,
            DemandStageChainBindings = request.DemandStageChainBindings,
            ExecutionConstraints = request.ExecutionConstraints,
            Resources = request.Resources,
            CalendarSlots = request.CalendarSlots,
            StrategySnapshot = request.StrategySnapshot
        };
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
