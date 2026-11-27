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

    /// <summary>
    /// **0号位 v1.3 §4.1**：同物料不同链的两条需求，一条的 Stage **有 Routing**、另一条**没有**，
    /// 两条**不得互相污染**——A 链上的有无 Routing 不能改变 B 链的判定。
    ///
    /// <para>修前：`ResolveRoutelessStages` 按 `MaterialId` 取**物料级并集** ⇒ 只要该物料任一需求有
    /// Routing 工序，另一条需求也会被算成「范围内有 Routing」，进而影响 `HasRoutingInScope`
    /// （= 是否抑制候选提前期）。多链物料上这就是跨链污染。</para>
    /// </summary>
    [Fact]
    public async Task 同物料不同链_一条Stage有Routing另一条没有_互不污染()
    {
        // 给物料 M 的 CS2 一条 Routing 工序；D1 的链含 CS2、D2 的链只含 CS1/CS3 ⇒ 互不相干。
        var result = await _solver.SolveAsync(Build(withCs2Routing: true));

        var byKey = result.DemandCompletions.ToDictionary(c => c.LogicalDemandKey, StringComparer.Ordinal);

        // D1：**自己的链**里有 Routing 工序（CS2）⇒ `HasRoutingInScope=true` ⇒ 未排时抑制候选提前期，
        //     `StageTimings` 必为空（0号位 第七轮 §八.3 门控）。
        Assert.Empty(byKey["D1"].StageTimings ?? new List<StageTimingSegmentFact>());

        // D2：**自己的链**（CS1→CS3）里没有任何 Routing 工序 ⇒ 必须仍产出纯提前期段时间。
        //     ⚠ 这就是污染判据：修前按物料并集判「有 Routing」⇒ D2 也会被抑制成空（本断言失败）。
        Assert.Equal(new[] { "CS1", "CS3" }, StageCodes(byKey["D2"]));
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

    /// <summary>按用例给定的 Stage 执行厂（代码＋Id）构造链步骤。</summary>
    private static List<StageSequenceStep> Steps(
        string factoryCode, int? factoryId, params (int Seq, string StageCode)[] xs)
        => xs.Select(x => new StageSequenceStep
        {
            StageCode = x.StageCode,
            StageSeq = x.Seq,
            ProductionDepartmentId = Dept,
            StageFactoryCode = factoryCode,
            StageFactoryId = factoryId
        }).ToList();

    private static string[] StageCodes(DemandCompletionFact fact)
        => (fact.StageTimings ?? new List<StageTimingSegmentFact>())
            .Select(s => s.StageCode)
            .ToArray();

    /// <summary>
    /// **0号位 v1.3 §4.2**：WORK 日历取窗按**当前 Stage 的实际执行厂**，不是需求厂。
    ///
    /// <para>正例：需求厂 F1、Stage 厂 F2（Id=2），窗口只投影在 F2 ⇒ 必须用 F2 的窗排下。</para>
    /// <para>反例：窗口只投影在 F1（需求厂）⇒ 修复后**不得**拿需求厂的窗顶替，
    ///   应报 `NO_WORK_WINDOW`（不产段时间、不造窗）。</para>
    /// </summary>
    [Fact]
    public async Task Stage实际执行厂与需求厂不同_取窗须用Stage厂_不得用需求厂顶替()
    {
        // 正例：窗口在 Stage 厂（F2 = Id 2）
        var ok = await _solver.SolveAsync(Build(
            stageFactoryCode: "F2", stageFactoryId: 2, windowFactoryId: 2));

        var okFact = Assert.Single(ok.DemandCompletions.Where(c => c.LogicalDemandKey == "D1"));
        Assert.Contains("CS1", StageCodes(okFact));

        // 反例：同样的输入，但窗口只投影在需求厂（F1 = Id 1）⇒ 不得顶替
        var bad = await _solver.SolveAsync(Build(
            stageFactoryCode: "F2", stageFactoryId: 2, windowFactoryId: 1));

        var badFact = Assert.Single(bad.DemandCompletions.Where(c => c.LogicalDemandKey == "D1"));
        Assert.Empty(badFact.StageTimings ?? new List<StageTimingSegmentFact>());
    }

    private static DomainSolveRequest Build(
        bool withCs2Routing = false,
        string stageFactoryCode = "F1",
        int? stageFactoryId = null,
        int windowFactoryId = 1)
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
            // §4.1 污染判据用：仅 CS2 有 Routing 工序（物料级并集里因此「有 Routing」）。
            RoutingOperations = withCs2Routing
                ? new List<RoutingOperation>
                {
                    new()
                    {
                        MaterialId = M, StageCode = "CS2", OperationCode = "OP2",
                        OperationName = "OP2", RouteCode = "DEFAULT", PathId = 1
                    }
                }
                : new List<RoutingOperation>(),
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
                new DepartmentWorkWindow { FactoryId = windowFactoryId, DepartmentId = Dept, Start = Day, End = Day.AddYears(100) }
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
                    Stages = Steps(stageFactoryCode, stageFactoryId, (10, "CS1"), (20, "CS2"))
                },
                new()
                {
                    ChainRefId = "ROOT:10:CS1|20:CS3", StageScopeType = "ROOT", ChainSignature = "10:CS1|20:CS3",
                    Stages = Steps(stageFactoryCode, stageFactoryId, (10, "CS1"), (20, "CS3"))
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
