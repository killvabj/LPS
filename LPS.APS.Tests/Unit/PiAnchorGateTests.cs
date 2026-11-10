using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Core.Dto;
using RoutingOperation = LPS.APS.Core.Entities.APS.RoutingOperation;
using OperationResourceEligibility = LPS.APS.Core.Entities.APS.OperationResourceEligibility;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 【为什么需要它】本组会通过 <c>OverrideAnchorModeForTest</c> 改写**进程级**锚批模式。
///   xUnit 默认让不同测试类**并行**执行 ⇒ 若不禁用并行，本组设的 `PI_ONLY` 会漏进同时运行的
///   其它测试类（实测撞红 `StageBatchMergeTests.M_13`）。
///   `DisableParallelization = true` 让本 Collection 与其它并行 Collection **互斥**执行。
/// </summary>
[CollectionDefinition("P2AnchorModeSerial", DisableParallelization = true)]
public class P2AnchorModeSerialCollection
{
}

/// <summary>
/// 【§C 反证矩阵 · PI 锚批门禁】0号位《BOM并行期 Phase2 安全性能优化 v1.0》
///   §2-A2（单Op无PI快路 + 来源PI守卫）与 §D 红线（**不得用 PI_ONLY 把无 PI 需求删掉**）。
///
/// <para><b>本组要证明的核心命题</b>：`P2FilterByPi` 只让「无 PI 的需求」**没有合批锚批**，
///   该需求仍然**照常独立排程、照常产 Task、照常可判未排** —— 不是"从 Solver 删除"。</para>
///
/// <para><b>为什么必须有这组测试</b>：0号位 §D 把「用 PI_ONLY 删掉无 PI 需求」列为**直接阻断验收**的做法。
///   只靠口头声称"我没删"不算证据，必须有用例钉住。</para>
/// </summary>
[Collection("P2AnchorModeSerial")]
public class PiAnchorGateTests
{
    private static readonly DateTime PlanningStart = new(2026, 11, 2, 8, 0, 0);
    private static readonly DateTime PlanningEnd = new(2026, 11, 30, 8, 0, 0);

    private const int DeptId = 100;
    private const int MaterialId = 1;
    private const string Stage = "STAGE1";
    private const string OpCode = "OP10";
    private const string DemandKey = "K1";

    private readonly FiniteCapacitySolver _solver = new();

    /// <summary>在指定锚批模式下执行，结束后**必定还原**为未覆盖状态，避免污染同进程其它用例。</summary>
    private static async Task<T> InModeAsync<T>(string? mode, Func<Task<T>> body)
    {
        PhaseTwoInitialScheduler.OverrideAnchorModeForTest(mode);
        try
        {
            return await body();
        }
        finally
        {
            PhaseTwoInitialScheduler.OverrideAnchorModeForTest(null);
        }
    }

    /// <summary>
    /// 最小可运行夹具：单物料 / 单工序（60 分钟）/ 独占资源 / 整窗日历 / 单条需求。
    /// `pi` 传 null 表示这条需求**没有生产指令号**（本组的主角）。
    /// </summary>
    private static DomainSolveRequest Build(string? pi)
    {
        var demand = new LogicalProductionDemand
        {
            LogicalDemandKey = DemandKey,
            PlanVersionId = 1L,
            DomainKey = "DOMAIN",
            AllocationSequence = 1L,
            DemandKey = DemandKey,
            MaterialId = MaterialId,
            FactoryId = 1,
            StartStageCode = Stage,
            NetOutputQty = 10m,
            PlannedProcessQty = 10m,
            RequiredAvailableTime = PlanningEnd,
            DemandSequence = 1,
            ProductionInstructionNo = pi,
            IsRootDemand = true,
            RouteCode = "DEFAULT",
            PathId = 1
        };

        var routingOps = new List<RoutingOperation>
        {
            new()
            {
                MaterialId = MaterialId,
                ProductionDepartmentId = DeptId,
                RouteCode = "DEFAULT",
                PathId = 1,
                OperationCode = OpCode,
                StageCode = Stage,
                StandardDuration = 60m * 60m,   // 秒
                SetupTime = 0m
            }
        };

        var eligibilities = new List<OperationResourceEligibility>
        {
            new()
            {
                MaterialId = MaterialId,
                ProductionDepartmentId = DeptId,
                RouteCode = "DEFAULT",
                PathId = 1,
                OperationCode = OpCode,
                ResourceId = MaterialId,
                Priority = 1,
                CapacityFactor = 1m
            }
        };

        var deptContexts = new List<MaterialStageDepartmentContextDto>
        {
            new() { MaterialId = MaterialId, StageCode = Stage, ProductionDepartmentId = DeptId }
        };

        var resources = new List<ResourceDefinition>
        {
            new() { ResourceId = MaterialId, ResourceCode = "R1", FactoryCode = "F1", Capacity = 1m }
        };

        var calendarSlots = new List<ResourceCalendarSlot>
        {
            new()
            {
                ResourceId = MaterialId,
                Start = PlanningStart,
                End = PlanningEnd,
                IsAvailable = true
            }
        };

        // 带 PI 的自由需求必须投影 PI 权威事实，否则数量门禁会 Fail Closed（与 PI 锚批门禁无关的既有语义）。
        var piFacts = string.IsNullOrWhiteSpace(pi)
            ? new List<PiRemainingFact>()
            : new List<PiRemainingFact>
            {
                new()
                {
                    ProductionInstructionNo = pi!,
                    MaterialId = MaterialId,
                    FactoryId = 1,
                    OrderType = string.Empty,
                    PiQuantity = 10m,
                    PiReceivedQty = 0m,
                    PiRemainingQty = 10m
                }
            };

        var piStageBaselines = string.IsNullOrWhiteSpace(pi)
            ? new List<PiStageQuantityBaseline>()
            : new List<PiStageQuantityBaseline>
            {
                new()
                {
                    ProductionInstructionNo = pi!,
                    MaterialId = MaterialId,
                    StageCode = Stage,
                    StageFreeEligibleQty = 10m,
                    AlreadyCommittedQty = 0m
                }
            };

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "DOMAIN",
            PlanningStart = PlanningStart,
            PlanningEnd = PlanningEnd,
            LogicalProductionDemands = new List<LogicalProductionDemand> { demand },
            PiRemainingFacts = piFacts,
            PiStageQuantityBaselines = piStageBaselines,
            RoutingOperations = routingOps,
            OperationResourceEligibility = eligibilities,
            MaterialStageDepartmentContexts = deptContexts,
            Resources = resources,
            CalendarSlots = calendarSlots,
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD",
                    AllowMerge = true,
                    AllowSplit = false
                },
                // ⚠ `Permissive` 的 `allowMerge` 默认 **false** —— 不显式打开，策略层直接关掉合批，
//   则 `TryMergeOrSchedule` 根本不会被调用（实测 `MergeAttempts=0`）。E5 需要真的合批。
                BatchPolicies = TestBatchPolicy.Permissive(new[] { MaterialId }, DeptId, allowMerge: true)
            }
        };
    }

    private async Task<DomainSolveResult> SolveAsync(DomainSolveRequest request)
        => await _solver.SolveAsync(request, CancellationToken.None);

    // ─────────────────────────────────────────────────────────────
    // §C-1-03 / §D 红线：无 PI 的内制需求在 PI_ONLY 下**不得被删除**
    // ─────────────────────────────────────────────────────────────

    [Fact(DisplayName = "无PI需求_PI_ONLY_仍产出独立Task_不被删除")]
    public async Task NoPiDemand_UnderPiOnly_StillSchedules()
    {
        var result = await InModeAsync("PI_ONLY", () => SolveAsync(Build(pi: null)));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.FinalTasks.Count > 0,
            "无 PI 需求被 PI_ONLY 删掉了 —— 违反 0号位 §D 红线（缺合批锚批 ≠ 不排产）");
        Assert.DoesNotContain(result.UnscheduledTasks, u => u.DraftId == DemandKey);
    }

    [Fact(DisplayName = "无PI需求_PI_FIRST_仍产出独立Task_不被删除")]
    public async Task NoPiDemand_UnderPiFirst_StillSchedules()
    {
        var result = await InModeAsync("PI_FIRST", () => SolveAsync(Build(pi: null)));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.FinalTasks.Count > 0,
            "无 PI 需求被 PI_FIRST 删掉了 —— 违反 0号位 §D 红线");
    }

    [Fact(DisplayName = "无PI需求_LEGACY_同样产出独立Task（对照：新旧都不删）")]
    public async Task NoPiDemand_UnderLegacy_StillSchedules()
    {
        var result = await InModeAsync("LEGACY", () => SolveAsync(Build(pi: null)));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.FinalTasks.Count > 0);
    }

    // ─────────────────────────────────────────────────────────────
    // 有 PI 的需求：PI_ONLY 下仍能独立排程（PI 门禁不放宽、也不多杀）
    // ─────────────────────────────────────────────────────────────

    [Fact(DisplayName = "有PI需求_PI_ONLY_仍产出Task")]
    public async Task WithPiDemand_UnderPiOnly_StillSchedules()
    {
        var result = await InModeAsync("PI_ONLY", () => SolveAsync(Build(pi: "P1")));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.FinalTasks.Count > 0, "有 PI 的单条需求不应被 PI 门禁杀掉");
    }

    [Fact(DisplayName = "有PI需求_LEGACY_产出Task（对照）")]
    public async Task WithPiDemand_UnderLegacy_StillSchedules()
    {
        var result = await InModeAsync("LEGACY", () => SolveAsync(Build(pi: "P1")));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.FinalTasks.Count > 0);
    }

    // ─────────────────────────────────────────────────────────────
    // E5（0号位 2026-11-10《审核与下一轮指令 v1.0》§2 反证清单）：
    //   「PI_FIRST 关闭时不引入本轮早停行为」——用同一份输入切换模式，
    //   比较「锚批完整试排次数」：早停只在 PI_FIRST 生效。
    // ─────────────────────────────────────────────────────────────

    private async Task<long> TrialCountAsync(string? mode, Func<DomainSolveRequest> makeRequest)
    {
        using var scope = SolverDiagnostics.BeginScope();
        var counters = scope.Counters;
        var request = makeRequest();
        var result = await InModeAsync(mode, () => SolveAsync(request));
        Assert.True(result.Success, result.ErrorMessage);

        // ⚠ 单工序合批走 `TryMergeDemandIntoTask`，计在 `MergeAttempts`；
        //   `MergeTrialStarted` 记的是**多工序 Stage 批**的试排 —— 本夹具是单工序，必须取前者。
        return counters.MergeAttempts;
    }

    [Fact(DisplayName = "E5_同一输入下_PI_FIRST的锚批试排次数不得多于PI_SORT")]
    public async Task E5_EarlyStop_OnlyInPiFirst()
    {
        var sorted = await TrialCountAsync("PI_SORT", () => BuildThreeSamePi());
        var first = await TrialCountAsync("PI_FIRST", () => BuildThreeSamePi());

        Assert.True(sorted > 0, $"单工序合批一次都没尝试（{sorted}）—— 夹具没造出可合批场景，本用例无鉴别力");
        Assert.True(first <= sorted,
            $"早停模式试排次数({first}) 不应多于非早停模式({sorted}) —— 否则早停逻辑可能反向生效");
    }

    [Fact(DisplayName = "E5_LEGACY不启用早停_试排次数与PI_SORT同量级")]
    public async Task E5_Legacy_DoesNotEarlyStop()
    {
        var legacy = await TrialCountAsync("LEGACY", () => BuildThreeSamePi());
        var sorted = await TrialCountAsync("PI_SORT", () => BuildThreeSamePi());

        // LEGACY 不做 PI 过滤、也不早停 ⇒ 至少要有一次真实合批尝试（否则夹具无鉴别力）。
        Assert.True(legacy > 0, $"LEGACY 下压根没尝试合批（{legacy}）—— 夹具没造出可合批场景");
        Assert.True(sorted > 0, $"PI_SORT 下压根没尝试合批（{sorted}）");
    }

    /// <summary>三条同 PI、同物料、同工序的需求 —— 后两条会把前一条已排 Task 当锚批。</summary>
    private static DomainSolveRequest BuildThreeSamePi()
    {
        var request = Build(pi: "P1");
        var baseDemand = request.LogicalProductionDemands[0];

        var demands = new List<LogicalProductionDemand>();
        for (var i = 0; i < 3; i++)
        {
            demands.Add(new LogicalProductionDemand
            {
                LogicalDemandKey = $"K{i + 1}",
                PlanVersionId = baseDemand.PlanVersionId,
                DomainKey = baseDemand.DomainKey,
                AllocationSequence = i + 1,
                DemandKey = $"K{i + 1}",
                MaterialId = baseDemand.MaterialId,
                FactoryId = baseDemand.FactoryId,
                StartStageCode = baseDemand.StartStageCode,
                NetOutputQty = baseDemand.NetOutputQty,
                PlannedProcessQty = baseDemand.PlannedProcessQty,
                RequiredAvailableTime = baseDemand.RequiredAvailableTime,
                DemandSequence = i + 1,
                ProductionInstructionNo = "P1",
                IsRootDemand = true,
                RouteCode = baseDemand.RouteCode,
                PathId = baseDemand.PathId
            });
        }

        var piFacts = new List<PiRemainingFact>
        {
            new()
            {
                ProductionInstructionNo = "P1",
                MaterialId = MaterialId,
                FactoryId = 1,
                OrderType = string.Empty,
                PiQuantity = 30m,
                PiReceivedQty = 0m,
                PiRemainingQty = 30m
            }
        };

        var piStageBaselines = new List<PiStageQuantityBaseline>
        {
            new()
            {
                ProductionInstructionNo = "P1",
                MaterialId = MaterialId,
                StageCode = Stage,
                StageFreeEligibleQty = 30m,
                AlreadyCommittedQty = 0m
            }
        };

        return new DomainSolveRequest
        {
            PlanVersionId = request.PlanVersionId,
            DomainKey = request.DomainKey,
            PlanningStart = request.PlanningStart,
            PlanningEnd = request.PlanningEnd,
            LogicalProductionDemands = demands,
            PiRemainingFacts = piFacts,
            PiStageQuantityBaselines = piStageBaselines,
            RoutingOperations = request.RoutingOperations,
            OperationResourceEligibility = request.OperationResourceEligibility,
            MaterialStageDepartmentContexts = request.MaterialStageDepartmentContexts,
            Resources = request.Resources,
            CalendarSlots = request.CalendarSlots,
            StrategySnapshot = request.StrategySnapshot
        };
    }
}