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
/// **新 DTO 逐需求链的端到端消费**（0号位 2026-11-25 v1.2 §1.1 DTO-04 / DTO-08）。
///
/// <para>0号位 逐字：「构造同料不同链，断言 Phase1/2/5 及约束消费均正确，**不靠旧字段回落**」；
///   DTO-08：「缺链/缺绑定/悬空引用/冲突/非法 Stage 厂、合法旧版兼容边界，**显式诊断**，
///   **不允许新合同缺失时静默旧字段兜底**」。</para>
///
/// <para>【为什么必须端到端】解析器单测只能证明「取到了哪条链」；改造前真正的问题是
///   **同一物料的不同需求在各个 Phase 走了不同的链且不报错**。本组用同一物料的三条需求：</para>
/// <list type="bullet">
///   <item><c>D1</c> 绑 ROOT 链 A：`CS1 → CS2`；</item>
///   <item><c>D2</c> 绑 ROOT 链 B：`CS1 → CS3`；</item>
///   <item><c>D3</c> **无绑定行**（新契约在场）⇒ <c>BindingAbsent</c> ⇒ 无链，**不得**拿物料级旧链顶替。</item>
/// </list>
/// <para>旧载体 `StageSequenceChains[M]` 故意放一条**第三形态**的链（`CS1 → CS9`）：
///   D1/D2 若误回落旧载体就会算出 `CS9`，D3 若误回落就会凭空多出一段时间。</para>
///
/// ⚠ 纯内存：只调公开入口 <see cref="FiniteCapacitySolver.SolveAsync"/>，不触库、不读写文件。
/// </summary>
public class NewDtoChainConsumptionTests
{
    private static readonly DateTime Day = new(2026, 9, 1, 0, 0, 0);
    private const int M = 7001;
    private const int Dept = 1401;

    private readonly FiniteCapacitySolver _solver = new();

    private static readonly StageSequenceStep[] ChainA =
    {
        new() { StageCode = "CS1", StageSeq = 10, ProductionDepartmentId = Dept, StageFactoryCode = "F1" },
        new() { StageCode = "CS2", StageSeq = 20, ProductionDepartmentId = Dept, StageFactoryCode = "F1" }
    };

    private static readonly StageSequenceStep[] ChainB =
    {
        new() { StageCode = "CS1", StageSeq = 10, ProductionDepartmentId = Dept, StageFactoryCode = "F1" },
        new() { StageCode = "CS3", StageSeq = 20, ProductionDepartmentId = Dept, StageFactoryCode = "F1" }
    };

    [Fact]
    public async Task 同物料不同需求各绑不同链_各自只走自己的链_不回落旧载体()
    {
        var result = await _solver.SolveAsync(Build());

        var byKey = result.DemandCompletions.ToDictionary(c => c.LogicalDemandKey, StringComparer.Ordinal);

        Assert.Equal(new[] { "CS1", "CS2" }, StageCodes(byKey["D1"]));
        Assert.Equal(new[] { "CS1", "CS3" }, StageCodes(byKey["D2"]));
        // 旧载体是 `CS1 → CS9`：只要出现 CS9 就说明发生了静默回落。
        Assert.DoesNotContain(
            result.DemandCompletions.SelectMany(c => c.StageTimings ?? new List<StageTimingSegmentFact>()),
            s => string.Equals(s.StageCode, "CS9", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 新契约在场但需求无绑定行_不回落旧载体_不凭空产出段时间()
    {
        var result = await _solver.SolveAsync(Build());

        var d3 = Assert.Single(result.DemandCompletions.Where(c => c.LogicalDemandKey == "D3"));

        Assert.Empty(d3.StageTimings ?? new List<StageTimingSegmentFact>());
        // 无链 ⇒ 无时间段、无末端 Task ⇒ 时间不得当承诺（既有 `CompletionTimeCertain` 判据覆盖）。
        Assert.NotEqual(true, d3.CompletionTimeCertain);
    }

    [Fact]
    public async Task 两条链同物料同Stage_各自份额互不串用()
    {
        var result = await _solver.SolveAsync(Build());

        foreach (var key in new[] { "D1", "D2" })
        {
            var fact = Assert.Single(result.DemandCompletions.Where(c => c.LogicalDemandKey == key));
            // 两段都各自 5h+3h：起点都是计划起点，完成时间分别 = Day+8h。
            Assert.Equal(Day.AddHours(8), fact.CompletionTime);
        }
    }

    private static string[] StageCodes(DemandCompletionFact fact)
        => (fact.StageTimings ?? new List<StageTimingSegmentFact>())
            .Select(s => s.StageCode)
            .ToArray();

    private static DomainSolveRequest Build()
        => new()
        {
            PlanVersionId = 1,
            DomainKey = "D",
            PlanningStart = Day,
            PlanningEnd = Day.AddDays(90),
            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                Demand("D1"), Demand("D2"), Demand("D3")
            },
            RoutingOperations = new List<RoutingOperation>(),
            RoutingDependencies = new List<RoutingDependency>(),
            OperationResourceEligibility = new List<OperationResourceEligibility>(),
            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = M, StageCode = "CS1", ProductionDepartmentId = Dept },
                new() { MaterialId = M, StageCode = "CS2", ProductionDepartmentId = Dept },
                new() { MaterialId = M, StageCode = "CS3", ProductionDepartmentId = Dept },
                new() { MaterialId = M, StageCode = "CS9", ProductionDepartmentId = Dept }
            },
            DepartmentWorkWindows = new[]
            {
                new DepartmentWorkWindow { FactoryId = 1, DepartmentId = Dept, Start = Day, End = Day.AddYears(100) }
            },
            StageLeadTimes = new List<StageLeadTimeFact>
            {
                new() { MaterialId = M, StageCode = "CS1", LeadTimeHours = 5m, MatchLevel = "DEPT_EXACT" },
                new() { MaterialId = M, StageCode = "CS2", LeadTimeHours = 3m, MatchLevel = "DEPT_EXACT" },
                new() { MaterialId = M, StageCode = "CS3", LeadTimeHours = 3m, MatchLevel = "DEPT_EXACT" },
                new() { MaterialId = M, StageCode = "CS9", LeadTimeHours = 40m, MatchLevel = "DEPT_EXACT" }
            },
            // 旧载体：故意放一条第三形态的链（CS1 → CS9）。新契约在场 ⇒ 任何需求都不得用它。
            StageSequenceChains = new List<StageSequenceChain>
            {
                new()
                {
                    MaterialId = M,
                    Stages = new List<StageSequenceStep>
                    {
                        new() { StageCode = "CS1", StageSeq = 10, ProductionDepartmentId = Dept },
                        new() { StageCode = "CS9", StageSeq = 20, ProductionDepartmentId = Dept }
                    }
                }
            },
            StageChainDefinitions = new List<StageChainDefinition>
            {
                new()
                {
                    ChainRefId = "ROOT:10:CS1|20:CS2", StageScopeType = "ROOT", ChainSignature = "10:CS1|20:CS2",
                    Stages = ChainA
                },
                new()
                {
                    ChainRefId = "ROOT:10:CS1|20:CS3", StageScopeType = "ROOT", ChainSignature = "10:CS1|20:CS3",
                    Stages = ChainB
                }
            },
            DemandStageChainBindings = new List<DemandStageChainBinding>
            {
                new()
                {
                    LogicalDemandKey = "D1", BindingKey = "ROOT|BOM|WS",
                    ChainRefId = "ROOT:10:CS1|20:CS2", StageScopeType = "ROOT",
                    Status = StageChainBindingStatus.Resolved
                },
                new()
                {
                    LogicalDemandKey = "D2", BindingKey = "ROOT|BOM|WS",
                    ChainRefId = "ROOT:10:CS1|20:CS3", StageScopeType = "ROOT",
                    Status = StageChainBindingStatus.Resolved
                }
                // D3 故意不给绑定行 ⇒ 新契约在场 ⇒ BindingAbsent，不得回落旧载体。
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

    private static LogicalProductionDemand Demand(string key)
        => new()
        {
            LogicalDemandKey = key, PlanVersionId = 1, DomainKey = "D",
            AllocationSequence = key switch { "D1" => 1, "D2" => 2, _ => 3 },
            DemandKey = key,
            MaterialId = M, FactoryId = 1, FactoryCode = "F1",
            StartStageCode = "CS1", StartOperationCode = null,
            NetOutputQty = 10m, PlannedProcessQty = 10m,
            RequiredAvailableTime = Day.AddDays(20),
            RouteCode = null, PathId = null
        };
}
