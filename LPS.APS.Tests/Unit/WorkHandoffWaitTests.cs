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
/// **AE-006 / 0号位 v1.3 §4.3：`WORK` 基准的 Stage 交接等待必须按合法有效窗口推进**。
///
/// <para>逐字：「真正有向 Stage 对、当前 Stage 厂、认证有效规则、整批最后必要完成为基准，
///   WORK 按合法有效窗口推进，ELAPSED 自然分钟，**单次应用**，禁止与 Stage LT/跨厂物流 LT 重复。
///   缺真实窗局部未证/阻断，**不把 WORK 静默当 0/ELAPSED**」。</para>
///
/// <para>修复前：`WORK` 分支直接 `continue` —— 等待**从不应用**，且只在追溯里留一条
///   `WORK_NO_CALENDAR`；等于把 WORK 规则整体作废。</para>
///
/// <para>夹具：链 `CS1(LT 1h) → CS2(LT 1h)`，部门 1501 的合法窗口
///   `09-01 08:00–16:00` 与 `09-02 08:00–16:00`；CS1 从 09-01 00:00 起算 ⇒ CS1 结束 09-01 09:00。</para>
///
/// ⚠ 纯内存：只调公开入口 <see cref="FiniteCapacitySolver.SolveAsync"/>，不触库、不读写文件。
/// </summary>
public class WorkHandoffWaitTests
{
    private static readonly DateTime Day = new(2026, 9, 1, 0, 0, 0);
    private const int M = 9101;
    private const int Dept = 1501;
    private const string Factory = "F1";

    private readonly FiniteCapacitySolver _solver = new();

    [Fact]
    public async Task WORK等待300分钟_窗口内可一次折算_完成时间按有效工时后移()
    {
        // CS1 结束 09-01 09:00 → 等待 5 工作小时（09:00–14:00 都在窗内）→ CS2 1h → 15:00
        var result = await _solver.SolveAsync(Build(300, withCs2Dept: true));

        var fact = Assert.Single(result.DemandCompletions);
        Assert.Equal(Day.AddHours(15), fact.CompletionTime);
    }

    [Fact]
    public async Task WORK等待600分钟_跨OFF不得按自然时间直加_应落次日窗口()
    {
        // 09-01 09:00 后只剩 7 工作小时（到 16:00）→ 尚差 3 小时落到 09-02 08:00–11:00
        // → CS2 1h → 09-02 12:00。若把 WORK 当自然分钟直加，会得到 09-01 19:00（越窗，错）。
        var result = await _solver.SolveAsync(Build(600, withCs2Dept: true));

        var fact = Assert.Single(result.DemandCompletions);
        Assert.Equal(Day.AddDays(1).AddHours(12), fact.CompletionTime);
    }

    [Fact]
    public async Task 缺合法窗口时_WORK等待不得静默当0或ELAPSED()
    {
        // CS2 无部门上下文 ⇒ 取不到合法窗口 ⇒ 该条等待**不应用**（不是当 0、也不是当 ELAPSED）。
        //   CS1 正常推进到 09-01 09:00；CS2 自身缺窗也推不动 ⇒ 完成时间停在此刻。
        var result = await _solver.SolveAsync(Build(300, withCs2Dept: false));

        var fact = Assert.Single(result.DemandCompletions);
        Assert.Equal(Day.AddHours(9), fact.CompletionTime);

        // 缺窗仍须留痕（既有 `WORK_NO_CALENDAR` 通道不得丢失）
        Assert.Contains(
            result.SolveTraceNotes,
            n => string.Equals(n.ReasonCode, "WORK_NO_CALENDAR", StringComparison.Ordinal));
    }

    // ────────────────────────────────────────────────────────────────────
    // 0号位 v1.3 §4.2：WORK 窗口的「OFF / 跨周末 / 缺窗」正反例
    //   直接测窗口消费器 `TryAdvanceByWorkHours`（与 Stage 提前期、WORK 交接同一个实现）。
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void 跨周末_周五余下工时用尽后落到周一_不得跳过OFF直接加()
    {
        // 周五 2026-09-04 与 周一 2026-09-07 各有 08:00–16:00；周六日无窗（OFF）。
        var friday = new DateTime(2026, 9, 4, 0, 0, 0);
        var monday = new DateTime(2026, 9, 7, 0, 0, 0);

        var request = WindowRequest(new[]
        {
            (friday.AddHours(8), friday.AddHours(16)),
            (monday.AddHours(8), monday.AddHours(16))
        });

        var ok = StageTimingNodeBuilder.TryAdvanceByWorkHours(
            request, request.LogicalProductionDemands[0], "CS1",
            friday.AddHours(14), 6m, out var end, out var reason);

        Assert.True(ok, reason);
        // 周五 14:00–16:00 取 2h，剩 4h 落到周一 08:00–12:00 ⇒ 周一 12:00
        Assert.Equal(monday.AddHours(12), end);
    }

    [Fact]
    public void 日内OFF_午休不计入工时()
    {
        // 单日 08:00–12:00 与 13:00–17:00（12:00–13:00 为 OFF）
        var d = new DateTime(2026, 9, 1, 0, 0, 0);
        var request = WindowRequest(new[]
        {
            (d.AddHours(8), d.AddHours(12)),
            (d.AddHours(13), d.AddHours(17))
        });

        var ok = StageTimingNodeBuilder.TryAdvanceByWorkHours(
            request, request.LogicalProductionDemands[0], "CS1",
            d.AddHours(11), 3m, out var end, out var reason);

        Assert.True(ok, reason);
        // 11:00–12:00 取 1h，跳过午休，13:00–15:00 取 2h ⇒ 15:00
        Assert.Equal(d.AddHours(15), end);
    }

    [Fact]
    public void 缺窗_不得静默当0或ELAPSED_须显式失败()
    {
        var request = WindowRequest(Array.Empty<(DateTime, DateTime)>());

        var ok = StageTimingNodeBuilder.TryAdvanceByWorkHours(
            request, request.LogicalProductionDemands[0], "CS1",
            Day, 2m, out var end, out var reason);

        Assert.False(ok);
        // 2026-11-26：这份夹具**一条窗口都没有** ⇒ 连「部门所属厂」都派生不出来，
        //   故诚实原因是 `STAGE_FACTORY_UNKNOWN`（不是 NO_WORK_WINDOW：那是「厂已知但该 scope 无窗」）。
        //   两者都满足本用例的意图：**显式失败、游标不动、不静默当 0/ELAPSED**。
        Assert.Equal("STAGE_FACTORY_UNKNOWN", reason);
        Assert.Equal(Day, end);   // 游标不动
    }

    // ────────────────────────────────────────────────────────────────────
    // 2026-11-25（0号位 v1.5 ① / v1.3 §4.2）：**取窗厂 = 当前 Stage 的权威执行厂**
    //   ① 正向 `TryAdvanceByWorkHours` 与反向 `TryRetreatByWorkHours` **同源同一口径**；
    //   ② 链已给出该需求 ⇒ 链是唯一权威，链里缺该 Stage 的厂 = 未知，**不回落需求厂**；
    //   ③ 该需求完全没有链 ⇒ 不存在 Stage 厂事实，需求厂是唯一事实（不是回落）。
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void 严格取厂_链里缺该Stage厂_正向不回落需求厂()
    {
        var request = StrictWindowRequest();
        var map = new Dictionary<string, Dictionary<string, int>>
        {
            ["D1"] = new Dictionary<string, int> { ["CS1"] = 1 }   // 只有 CS1，没有 CS2
        };

        var ok = StageTimingNodeBuilder.TryAdvanceByWorkHours(
            request, request.LogicalProductionDemands[0], "CS2", Day, 2m, out var end, out var reason,
            isLeadTimeOnly: false, stageFactoryIdByDemandStage: map);

        Assert.False(ok);
        Assert.Equal("STAGE_FACTORY_UNKNOWN", reason);
        Assert.Equal(Day, end);   // 游标不动
    }

    [Fact]
    public void 严格取厂_反向与正向同源_同样不回落需求厂()
    {
        var request = StrictWindowRequest();
        var map = new Dictionary<string, Dictionary<string, int>>
        {
            ["D1"] = new Dictionary<string, int> { ["CS1"] = 1 }
        };

        var ok = StageTimingNodeBuilder.TryRetreatByWorkHours(
            request, request.LogicalProductionDemands[0], "CS2", Day.AddHours(20), 2m, out var start, out var reason,
            isLeadTimeOnly: false, stageFactoryIdByDemandStage: map);

        Assert.False(ok);
        Assert.Equal("STAGE_FACTORY_UNKNOWN", reason);
        Assert.Equal(Day.AddHours(20), start);
    }

    [Fact]
    public void 严格取厂_链给的厂与需求厂不同_按链厂取窗()
    {
        // 需求厂 1：08:00–20:00；链厂 2：10:00–12:00。
        // 从 09:00 起推进 2h：按**链厂 2** ⇒ 10:00 起算 ⇒ 12:00；若错按需求厂 1 ⇒ 11:00。
        var request = StrictWindowRequest();
        var map = new Dictionary<string, Dictionary<string, int>>
        {
            ["D1"] = new Dictionary<string, int> { ["CS1"] = 2 }
        };

        var ok = StageTimingNodeBuilder.TryAdvanceByWorkHours(
            request, request.LogicalProductionDemands[0], "CS1", Day.AddHours(9), 2m, out var end, out var reason,
            isLeadTimeOnly: false, stageFactoryIdByDemandStage: map);

        Assert.True(ok, reason);
        Assert.Equal(Day.AddHours(12), end);
    }

    [Fact]
    public void 无链需求_按部门唯一所属厂取窗_不回落需求厂()
    {
        // 2026-11-26（0号位 v1.7 §2：「无链又回落Demand厂为自划规则，不批准」）：
        //   需求厂 = 2，但 CS1 的部门只属于厂 1 ⇒ **必须用厂 1** 的窗。
        //   若回落需求厂 2（其窗 10:00–12:00）会得到 12:00；按部门厂 1（08:00–20:00）则是 11:00。
        var request = DeptOwnedByFactory1Request();
        var map = new Dictionary<string, Dictionary<string, int>>();   // 该需求没有任何链

        var ok = StageTimingNodeBuilder.TryAdvanceByWorkHours(
            request, request.LogicalProductionDemands[0], "CS1", Day.AddHours(9), 2m, out var end, out var reason,
            isLeadTimeOnly: false, stageFactoryIdByDemandStage: map);

        Assert.True(ok, reason);
        Assert.Equal(Day.AddHours(11), end);
    }

    [Fact]
    public void 无链且部门无归属_判厂未知_不回落需求厂()
    {
        var request = DeptOwnedByFactory1Request();
        // 把部门窗全部去掉 ⇒ 部门→厂无法派生 ⇒ 厂未知
        var bare = new DomainSolveRequest
        {
            PlanVersionId = request.PlanVersionId,
            DomainKey = request.DomainKey,
            PlanningStart = request.PlanningStart,
            PlanningEnd = request.PlanningEnd,
            LogicalProductionDemands = request.LogicalProductionDemands,
            MaterialStageDepartmentContexts = request.MaterialStageDepartmentContexts,
            DepartmentWorkWindows = Array.Empty<DepartmentWorkWindow>(),
            StageChainDefinitions = request.StageChainDefinitions,
            StrategySnapshot = request.StrategySnapshot
        };

        var ok = StageTimingNodeBuilder.TryAdvanceByWorkHours(
            bare, bare.LogicalProductionDemands[0], "CS1", Day.AddHours(9), 2m, out var end, out var reason,
            isLeadTimeOnly: false, stageFactoryIdByDemandStage: new Dictionary<string, Dictionary<string, int>>());

        Assert.False(ok);
        Assert.Equal("STAGE_FACTORY_UNKNOWN", reason);
        Assert.Equal(Day.AddHours(9), end);
    }

    /// <summary>需求厂 = 2，但 CS1 的部门只属于厂 1（厂 1 窗 08:00–20:00，厂 2 窗 10:00–12:00）。</summary>
    private static DomainSolveRequest DeptOwnedByFactory1Request()
    {
        var reordered = new List<DepartmentWorkWindow>();
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
                    MaterialId = M, FactoryId = 2, FactoryCode = "F2",   // ← 需求厂 2
                    StartStageCode = "CS1", NetOutputQty = 10m, PlannedProcessQty = 10m,
                    RequiredAvailableTime = Day.AddDays(20)
                }
            },
            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = M, StageCode = "CS1", ProductionDepartmentId = Dept }
            },
            DepartmentWorkWindows = new[]
            {
                // 部门 Dept 只出现在厂 1 ⇒ 唯一归属
                new DepartmentWorkWindow { FactoryId = 1, DepartmentId = Dept, Start = Day.AddHours(8), End = Day.AddHours(20) }
            },
            StageChainDefinitions = new List<StageChainDefinition>(),
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters { SchedulingDirection = "FORWARD" },
                BatchPolicies = Array.Empty<BatchPolicyRuleSnapshot>(),
                SetupTransitionRules = Array.Empty<SetupTransitionRuleSnapshot>()
            }
        };
    }

    // ────────────────────────────────────────────────────────────────────
    // 2026-11-25（0号位 v1.5 M06 / §4.3「单次应用，禁止与 Stage LT 重复」）
    //   整单两条交接规则 40 + 60，配三段 Stage LT，各**只加一次**。
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task E4_整单两条交接规则40与60_各应用一次_不与StageLT重复()
    {
        var result = await _solver.SolveAsync(TwoRuleRequest());

        // 三段无 Routing 各 1h LT；规则 CS1→CS2 = 40min、CS2→CS3 = 60min（ELAPSED）
        //   时间线：CS1 1h → +40 → CS2 1h → +60 → CS3 1h ⇒ Day + 4h40m。
        //   反例形态（本断言能挡住的）：把等待当 Stage LT 再加一遍（⇒ 6h20m）、
        //   只应用最后一条（⇒ 4h）、或按数量 10 放大等待（⇒ 远大于 4h40m）。
        var fact = Assert.Single(result.DemandCompletions);
        Assert.Equal(Day.AddHours(4).AddMinutes(40), fact.CompletionTime);
    }

    /// <summary>三段链 + 两条交接规则（40 / 60）的最小整单请求。</summary>
    private static DomainSolveRequest TwoRuleRequest()
    {
        var stages = new List<StageSequenceStep>
        {
            new() { StageCode = "CS1", StageSeq = 1, ProductionDepartmentId = Dept, StageFactoryCode = Factory, StageFactoryId = 1 },
            new() { StageCode = "CS2", StageSeq = 2, ProductionDepartmentId = Dept, StageFactoryCode = Factory, StageFactoryId = 1 },
            new() { StageCode = "CS3", StageSeq = 3, ProductionDepartmentId = Dept, StageFactoryCode = Factory, StageFactoryId = 1 }
        };

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
                    MaterialId = M, FactoryId = 1, FactoryCode = Factory,
                    StartStageCode = "CS1", NetOutputQty = 10m, PlannedProcessQty = 10m,
                    RequiredAvailableTime = Day.AddDays(20)
                }
            },
            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = M, StageCode = "CS1", ProductionDepartmentId = Dept },
                new() { MaterialId = M, StageCode = "CS2", ProductionDepartmentId = Dept },
                new() { MaterialId = M, StageCode = "CS3", ProductionDepartmentId = Dept }
            },
            DepartmentWorkWindows = new[]
            {
                new DepartmentWorkWindow { FactoryId = 1, DepartmentId = Dept, Start = Day, End = Day.AddYears(100) }
            },
            StageLeadTimes = new List<StageLeadTimeFact>
            {
                new() { MaterialId = M, StageCode = "CS1", LeadTimeHours = 1m, MatchLevel = "DEPT_EXACT" },
                new() { MaterialId = M, StageCode = "CS2", LeadTimeHours = 1m, MatchLevel = "DEPT_EXACT" },
                new() { MaterialId = M, StageCode = "CS3", LeadTimeHours = 1m, MatchLevel = "DEPT_EXACT" }
            },
            StageChainDefinitions = new List<StageChainDefinition>
            {
                new()
                {
                    ChainRefId = "ROOT:1:CS1|2:CS2|3:CS3", StageScopeType = "ROOT",
                    ChainSignature = "1:CS1|2:CS2|3:CS3", Stages = stages
                }
            },
            DemandStageChainBindings = new List<DemandStageChainBinding>
            {
                new()
                {
                    LogicalDemandKey = "D1", BindingKey = "ROOT|BOM|WS",
                    ChainRefId = "ROOT:1:CS1|2:CS2|3:CS3", StageScopeType = "ROOT",
                    Status = StageChainBindingStatus.Resolved
                }
            },
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
                        FactoryCode = Factory, FromStageCode = "CS1", ToStageCode = "CS2",
                        WaitMinutes = 40, TimeBasis = StageHandoffTimeBasis.ELAPSED, IsActive = true
                    },
                    new StageHandoffWaitRule
                    {
                        FactoryCode = Factory, FromStageCode = "CS2", ToStageCode = "CS3",
                        WaitMinutes = 60, TimeBasis = StageHandoffTimeBasis.ELAPSED, IsActive = true
                    }
                }
            }
        };
    }

    // ────────────────────────────────────────────────────────────────────
    // 2026-11-25（0号位 v1.5 M05：「FINAL/OUTS」）
    //   FINAL/OUTS 在 ProductionDepartment 字典里**没有部门** ⇒ 只能按**厂级** WORK 窗推进
    //   （`DepartmentId == null` 的行），且**不得**因「缺部门上下文」被拒。
    //   此前该分支零单测覆盖（全仓 `IsLeadTimeOnly = true` 的测试为 0 条）。
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FINAL_OUTS_无部门上下文_按厂级窗口推进_不得判缺部门()
    {
        var result = await _solver.SolveAsync(LeadTimeOnlyRequest());

        // CS1（普通，部门窗 08:00–16:00）2h ⇒ Day+10:00；
        //   OUTS（IsLeadTimeOnly，厂级窗全开）3h ⇒ Day+13:00。
        var fact = Assert.Single(result.DemandCompletions);
        Assert.Equal(Day.AddHours(13), fact.CompletionTime);

        // 不得因「缺部门」失败（那是普通 Stage 的判据，对 FINAL/OUTS 不适用）
        Assert.DoesNotContain(
            result.SolveTraceNotes,
            n => n.Message is not null
                 && n.Message.Contains("MISSING_DEPARTMENT_CONTEXT", StringComparison.Ordinal)
                 && n.Message.Contains("OUTS", StringComparison.Ordinal));
    }

    /// <summary>CS1（部门窗）+ OUTS（厂级窗、IsLeadTimeOnly）的最小请求。</summary>
    private static DomainSolveRequest LeadTimeOnlyRequest()
    {
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
                    MaterialId = M, FactoryId = 1, FactoryCode = Factory,
                    StartStageCode = "CS1", NetOutputQty = 10m, PlannedProcessQty = 10m,
                    RequiredAvailableTime = Day.AddDays(20)
                }
            },
            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = M, StageCode = "CS1", ProductionDepartmentId = Dept }
                // OUTS 故意**不给**部门上下文
            },
            DepartmentWorkWindows = new[]
            {
                new DepartmentWorkWindow { FactoryId = 1, DepartmentId = Dept, Start = Day.AddHours(8), End = Day.AddHours(16) },
                // 厂级行：DepartmentId == null，供 FINAL/OUTS 使用
                new DepartmentWorkWindow { FactoryId = 1, DepartmentId = null, Start = Day, End = Day.AddYears(100) }
            },
            StageLeadTimes = new List<StageLeadTimeFact>
            {
                new() { MaterialId = M, StageCode = "CS1", LeadTimeHours = 2m, MatchLevel = "DEPT_EXACT" },
                new() { MaterialId = M, StageCode = "OUTS", LeadTimeHours = 3m, MatchLevel = "GLOBAL_STAGE_DEFAULT", IsLeadTimeOnly = true }
            },
            StageChainDefinitions = new List<StageChainDefinition>
            {
                new()
                {
                    ChainRefId = "ROOT:1:CS1|2:OUTS", StageScopeType = "ROOT",
                    ChainSignature = "1:CS1|2:OUTS",
                    Stages = new List<StageSequenceStep>
                    {
                        new() { StageCode = "CS1", StageSeq = 1, ProductionDepartmentId = Dept, StageFactoryCode = Factory, StageFactoryId = 1 },
                        new() { StageCode = "OUTS", StageSeq = 2, ProductionDepartmentId = null, StageFactoryCode = Factory, StageFactoryId = 1 }
                    }
                }
            },
            DemandStageChainBindings = new List<DemandStageChainBinding>
            {
                new()
                {
                    LogicalDemandKey = "D1", BindingKey = "ROOT|BOM|WS",
                    ChainRefId = "ROOT:1:CS1|2:OUTS", StageScopeType = "ROOT",
                    Status = StageChainBindingStatus.Resolved
                }
            },
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

    /// <summary>两个厂各有一段窗口的最小请求（CS1/CS2 均有部门上下文）。</summary>
    private static DomainSolveRequest StrictWindowRequest()
    {
        var single = WindowRequest(Array.Empty<(DateTime, DateTime)>());
        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "D",
            PlanningStart = Day,
            PlanningEnd = Day.AddDays(90),
            LogicalProductionDemands = single.LogicalProductionDemands,
            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = M, StageCode = "CS1", ProductionDepartmentId = Dept },
                new() { MaterialId = M, StageCode = "CS2", ProductionDepartmentId = Dept }
            },
            DepartmentWorkWindows = new[]
            {
                new DepartmentWorkWindow { FactoryId = 1, DepartmentId = Dept, Start = Day.AddHours(8), End = Day.AddHours(20) },
                new DepartmentWorkWindow { FactoryId = 2, DepartmentId = Dept, Start = Day.AddHours(10), End = Day.AddHours(12) }
            },
            StageChainDefinitions = new List<StageChainDefinition>(),
            StrategySnapshot = single.StrategySnapshot
        };
    }

    /// <summary>构造只含一条需求 + 一个 Stage 部门上下文 + 指定窗口的最小请求。</summary>
    private static DomainSolveRequest WindowRequest(IReadOnlyList<(DateTime Start, DateTime End)> windows)
    {
        var demand = new LogicalProductionDemand
        {
            LogicalDemandKey = "D1", PlanVersionId = 1, DomainKey = "D",
            AllocationSequence = 1, DemandKey = "D1",
            MaterialId = M, FactoryId = 1, FactoryCode = Factory,
            StartStageCode = "CS1", NetOutputQty = 10m, PlannedProcessQty = 10m,
            RequiredAvailableTime = Day.AddDays(30)
        };

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "D",
            PlanningStart = Day,
            PlanningEnd = Day.AddDays(90),
            LogicalProductionDemands = new List<LogicalProductionDemand> { demand },
            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = M, StageCode = "CS1", ProductionDepartmentId = Dept }
            },
            DepartmentWorkWindows = windows
                .Select(w => new DepartmentWorkWindow
                {
                    FactoryId = 1, DepartmentId = Dept, Start = w.Start, End = w.End
                })
                .ToArray(),
            StageChainDefinitions = new List<StageChainDefinition>(),
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

    private static DomainSolveRequest Build(int waitMinutes, bool withCs2Dept)
    {
        var depts = new List<MaterialStageDepartmentContextDto>
        {
            new() { MaterialId = M, StageCode = "CS1", ProductionDepartmentId = Dept }
        };
        if (withCs2Dept)
        {
            depts.Add(new MaterialStageDepartmentContextDto
            {
                MaterialId = M, StageCode = "CS2", ProductionDepartmentId = Dept
            });
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
                    MaterialId = M, FactoryId = 1, FactoryCode = Factory,
                    StartStageCode = "CS1", StartOperationCode = null,
                    NetOutputQty = 10m, PlannedProcessQty = 10m,
                    RequiredAvailableTime = Day.AddDays(20),
                    RouteCode = null, PathId = null
                }
            },
            RoutingOperations = new List<RoutingOperation>(),
            RoutingDependencies = new List<RoutingDependency>(),
            OperationResourceEligibility = new List<OperationResourceEligibility>(),
            MaterialStageDepartmentContexts = depts,
            DepartmentWorkWindows = new[]
            {
                new DepartmentWorkWindow
                {
                    FactoryId = 1, DepartmentId = Dept,
                    Start = Day.AddHours(8), End = Day.AddHours(16)
                },
                new DepartmentWorkWindow
                {
                    FactoryId = 1, DepartmentId = Dept,
                    Start = Day.AddDays(1).AddHours(8), End = Day.AddDays(1).AddHours(16)
                }
            },
            StageLeadTimes = new List<StageLeadTimeFact>
            {
                new() { MaterialId = M, StageCode = "CS1", LeadTimeHours = 1m, MatchLevel = "DEPT_EXACT" },
                new() { MaterialId = M, StageCode = "CS2", LeadTimeHours = 1m, MatchLevel = "DEPT_EXACT" }
            },
            StageSequenceChains = new List<StageSequenceChain>(),
            StageChainDefinitions = new List<StageChainDefinition>
            {
                new()
                {
                    ChainRefId = "ROOT:1:CS1|2:CS2", StageScopeType = "ROOT", ChainSignature = "1:CS1|2:CS2",
                    Stages = new List<StageSequenceStep>
                    {
                        new()
                        {
                            StageCode = "CS1", StageSeq = 1, ProductionDepartmentId = Dept,
                            StageFactoryCode = Factory, StageFactoryId = 1
                        },
                        new()
                        {
                            StageCode = "CS2", StageSeq = 2, ProductionDepartmentId = Dept,
                            StageFactoryCode = Factory, StageFactoryId = 1
                        }
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
                        FactoryCode = Factory,
                        FromStageCode = "CS1",
                        ToStageCode = "CS2",
                        WaitMinutes = waitMinutes,
                        TimeBasis = StageHandoffTimeBasis.WORK,
                        IsActive = true
                    }
                }
            }
        };
    }
}
