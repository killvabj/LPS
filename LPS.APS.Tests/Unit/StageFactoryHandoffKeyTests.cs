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
/// **交接等待规则的工厂键 = 当前正在排程的 Stage 的执行厂**（0号位 2026-11-25 Q1 逐字：
///   「以当前正在排程的 Stage 为准……系统沿现有权威 `StageCode → StageDict.FactoryCode` 取得当前 Stage 实际执行厂，
///     **不使用 Demand.FactoryCode、根订单厂或任意上下游 Stage 厂**」）。
///
/// <para>改造前 1号位 按 `Demand.FactoryCode` 匹配 ⇒ 真跨厂 Stage 会**取错规则**。
///   本组用「需求厂 F1 / 链定义 Stage 厂 F2」构造正反例：</para>
/// <list type="bullet">
///   <item>正例：规则 `FactoryCode=F2`（= Stage 厂）⇒ **生效**（完成时间晚 120 分钟）；</item>
///   <item>反例：规则 `FactoryCode=F1`（= 需求厂）⇒ **不生效**（完成时间不变）。</item>
/// </list>
///
/// <para>⚠ 缺 Stage 厂信息时（老载荷 / 链未投影）**不得回落 `Demand.FactoryCode`**。
///   0号位 2026-11-25 v1.2 §3 逐字：「**不能缺值回 `Demand.FactoryCode`**。r14421 的回退分支
///   **必须撤除**；缺权威厂应暴露技术未证……不捏造可用时间或全域误阻。」
///   ⇒ 该条等待**不应用**（不加也不减），并产出 `STAGE_FACTORY_UNVERIFIED` 追溯
///   （由本文件第 3、4 条用例覆盖）。</para>
///
/// ⚠ 纯内存：只调公开入口 <see cref="FiniteCapacitySolver.SolveAsync"/>，不触库、不读写文件。
/// </summary>
public class StageFactoryHandoffKeyTests
{
    private static readonly DateTime Day = new(2026, 9, 1, 0, 0, 0);
    private const int M = 1;
    private const int Dept = 100;
    private const string DemandFactory = "F1";   // 需求厂
    private const string StageFactory = "F2";    // 链定义给出的 Stage 执行厂（与需求厂不同）

    private readonly FiniteCapacitySolver _solver = new();

    [Fact]
    public async Task 正例_规则厂等于Stage厂_交接等待生效()
    {
        var result = await _solver.SolveAsync(Build(ruleFactory: StageFactory));

        var fact = Assert.Single(result.DemandCompletions);
        // CS1(7h) → +120min 交接 → CS2(3h) ⇒ Day+12h
        Assert.Equal(Day.AddHours(12), fact.CompletionTime);
    }

    [Fact]
    public async Task 反例_规则厂等于需求厂_不再按需求厂匹配_交接等待不生效()
    {
        var result = await _solver.SolveAsync(Build(ruleFactory: DemandFactory));

        var fact = Assert.Single(result.DemandCompletions);
        // 不应用规则 ⇒ 7h + 3h = Day+10h
        Assert.Equal(Day.AddHours(10), fact.CompletionTime);
    }

    [Fact]
    public async Task 无链定义取不到权威厂时_不得回退需求厂_等待不生效且有追溯()
    {
        // 老载荷（无 `StageChainDefinitions`）⇒ 取不到 CS2 的权威执行厂。
        // 收敛前：回退 `Demand.FactoryCode = F1` ⇒ 规则（厂=F1）命中 ⇒ Day+12h（伪造的可用时间）。
        // 收敛后（0号位 2026-11-25 v1.2 §3）：判定不了 ⇒ **不应用** ⇒ 7h + 3h = Day+10h，并留痕。
        var result = await _solver.SolveAsync(Build(ruleFactory: DemandFactory, withChainBinding: false));

        var fact = Assert.Single(result.DemandCompletions);
        Assert.Equal(Day.AddHours(10), fact.CompletionTime);

        var note = result.SolveTraceNotes.FirstOrDefault(
            n => string.Equals(n.ReasonCode, "STAGE_FACTORY_UNVERIFIED", StringComparison.Ordinal)
                 || string.Equals(n.ReasonCode, "DEMAND_COMPLETION_TIME_UNAVAILABLE", StringComparison.Ordinal));
        Assert.NotNull(note);
        Assert.Equal("D1", note!.Key);
        Assert.Contains("不回退 `Demand.FactoryCode`", note.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 链定义里该Stage缺厂_同样不回退需求厂_等待不生效且有追溯()
    {
        // 新契约在场，但本 Stage 的 `StageFactoryCode` 为空 ⇒ 与老载荷同一种处置：不猜、不回落。
        var result = await _solver.SolveAsync(
            Build(ruleFactory: DemandFactory, withChainBinding: true, dropStageFactoryFor: "CS2"));

        var fact = Assert.Single(result.DemandCompletions);

        // 2026-11-26（0号位 v1.7 §2 / P0-1）更新：
        //   · **交接规则的键**仍是 Stage 的*执行厂代码*，链里没有 ⇒ 规则照旧**不生效**（这是 Q1 的本意）；
        //   · 但**取窗**这一侧不再无解：CS2 的部门唯一属于某厂 ⇒ 用该厂的真实 WORK 窗推进
        //     （撤销了「无链/缺厂 ⇒ 回落需求厂」那条自划规则）。
        //   ⇒ CS2 正常推进 3h，完成时间回到 CS1 末 + 3h = Day+10h。
        Assert.Equal(Day.AddHours(10), fact.CompletionTime);
        Assert.NotEqual(true, fact.CompletionTimeCertain);

        Assert.Contains(
            result.SolveTraceNotes,
            n => string.Equals(n.ReasonCode, "STAGE_FACTORY_UNVERIFIED", StringComparison.Ordinal)
                 || string.Equals(n.ReasonCode, "DEMAND_COMPLETION_TIME_UNAVAILABLE", StringComparison.Ordinal));
        // 2026-11-26：CS2 现在能通过「部门唯一所属厂」取到真实 WORK 窗 ⇒ 不再产
        //   `DEMAND_COMPLETION_TIME_UNAVAILABLE(STAGE_FACTORY_UNKNOWN)`；
        //   但**交接规则的厂码**仍取不到 ⇒ 规则不生效的留痕（上面那条）必须还在。
    }

    private static DomainSolveRequest Build(
        string ruleFactory,
        bool withChainBinding = true,
        string? dropStageFactoryFor = null)
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
                    MaterialId = M, FactoryId = 1, FactoryCode = DemandFactory,
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
            // 有链定义/绑定时，Step 带 StageFactoryCode = F2；否则只给物料级兼容链（Step 无厂）
            StageSequenceChains = withChainBinding
                ? new List<StageSequenceChain>()
                : new List<StageSequenceChain>
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
            StageChainDefinitions = withChainBinding
                ? new List<StageChainDefinition>
                {
                    new()
                    {
                        ChainRefId = "ROOT:1:CS1|2:CS2",
                        StageScopeType = "ROOT",
                        ChainSignature = "1:CS1|2:CS2",
                        Stages = new List<StageSequenceStep>
                        {
                            new() { StageCode = "CS1", StageSeq = 1, ProductionDepartmentId = Dept, StageFactoryCode = StageFactory, StageFactoryId = 1 },
                            new()
                            {
                                StageCode = "CS2", StageSeq = 2, ProductionDepartmentId = Dept,
                                StageFactoryCode = string.Equals(dropStageFactoryFor, "CS2", StringComparison.Ordinal)
                                    ? null
                                    : StageFactory,
                                StageFactoryId = string.Equals(dropStageFactoryFor, "CS2", StringComparison.Ordinal)
                                    ? (int?)null
                                    : 1
                            }
                        }
                    }
                }
                : new List<StageChainDefinition>(),
            DemandStageChainBindings = withChainBinding
                ? new List<DemandStageChainBinding>
                {
                    new()
                    {
                        LogicalDemandKey = "D1",
                        BindingKey = "ROOT|BOM|WS",
                        ChainRefId = "ROOT:1:CS1|2:CS2",
                        StageScopeType = "ROOT",
                        Status = StageChainBindingStatus.Resolved
                    }
                }
                : new List<DemandStageChainBinding>(),
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
                SetupTransitionRules = Array.Empty<SetupTransitionRuleSnapshot>(),
                StageHandoffWaitRules = new[]
                {
                    new StageHandoffWaitRule
                    {
                        FactoryCode = ruleFactory,
                        FromStageCode = "CS1",
                        ToStageCode = "CS2",
                        WaitMinutes = 120,
                        TimeBasis = StageHandoffTimeBasis.ELAPSED,
                        IsActive = true
                    }
                }
            }
        };
}
