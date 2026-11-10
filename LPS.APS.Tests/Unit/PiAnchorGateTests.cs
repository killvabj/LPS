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

    // ─────────────────────────────────────────────────────────────
    // E1（0号位《审核与下一轮指令 v1.0》§2 反证清单）：
    //   「锚A硬拒绝、锚B硬合法 ⇒ 必须继续试B」
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// E1 夹具：三条同 PI 需求 / 单资源 / 单工序（单件 3 分钟）/ 连续日历 07:00-20:00。
    ///
    /// <para><b>排出来的形状（PI_SORT 下，逐条推演）</b></para>
    /// <list type="bullet">
    ///   <item>K1（qty 10，交期 07:20，seq 1）先排 ⇒ <b>T1 = 07:00-07:30</b>（已延期，延期是合法结果）。</item>
    ///   <item>K2（qty 5，交期 20:00，seq 2）想并进 T1：(10+5)×3 = 45 分钟 ⇒ 07:00-07:45，
    ///         越过 T1 既有份额的交期 07:20 ⇒ `TryMergeDemandIntoTask:9896` <b>硬拒</b>
    ///         ⇒ T2 独立排在 <b>07:30-07:45</b>。</item>
    ///   <item>K3（qty 10，交期 09:30，seq 3）候选 = {T1, T2}。
    ///         排序口径是相对**当前需求**交期：T1 交期 07:20 ≤ 09:30 ⇒ tier0/距离 130 分钟；
    ///         T2 交期 20:00 &gt; 09:30 ⇒ tier1/距离 630 分钟 ⇒ <b>T1 先试</b>。</item>
    ///   <item>试 T1：(10+10)×3 = 60 分钟 ⇒ 07:00-08:00，越过 T1 份额交期 07:20 ⇒ <b>硬拒</b>（9896）。</item>
    ///   <item>试 T2：(5+10)×3 = 45 分钟 ⇒ <b>07:30-08:15</b> ≤ K3 交期 09:30、不撞 T1（07:00-07:30）
    ///         ⇒ <b>成功</b>，K3 并入 T2。</item>
    /// </list>
    ///
    /// <para><b>为什么这组有鉴别力</b>：若循环只试第一个锚批（即 `attemptLimit == 1` 的 LEGACY 行为），
    ///   K3 会走独立排程落在 07:45 之后 ⇒ 最终 3 条 Task；而正确行为是 2 条、且 T2 的加工量变成 15。
    ///   两个方向（Task 条数、合并后数量/时段）都能分辨，不是恒真断言。</para>
    /// </summary>
    [Fact(DisplayName = "E1_锚A硬拒绝_必须继续试锚B并合入B")]
    public async Task E1_AnchorAHardRejected_MustStillTryAnchorB()
    {
        DomainSolveResult result;
        long attempts;
        using (var scope = SolverDiagnostics.BeginScope())
        {
            result = await InModeAsync("PI_SORT", () => SolveAsync(BuildE1()));
            attempts = scope.Counters.MergeAttempts;
        }

        Assert.True(result.Success, result.ErrorMessage);

        var shape = string.Join(" | ", result.FinalTasks
            .OrderBy(t => t.PlannedStartTime)
            .Select(t => $"{t.PlannedStartTime:HH:mm}-{t.PlannedEndTime:HH:mm} qty={t.PlannedProcessQty}"));

        // ① 真的试了不止一次（否则「依次试到首个成功」的循环没生效）。
        Assert.True(attempts >= 2,
            $"锚A硬拒绝后没有继续试锚B：MergeAttempts={attempts}。现场：{shape}");

        // ② K3 被并进锚批，而不是独立排出一条新 Task。
        Assert.True(result.FinalTasks.Count == 2,
            $"期望 2 条 Task（T1 原样 + T2 吸收 K3），实得 {result.FinalTasks.Count} 条 —— 说明 K3 走了独立排程。现场：{shape}");

        // ③ 合并落在锚B（T2）上，且时段/数量都对得上。
        var mergedIntoB = result.FinalTasks.SingleOrDefault(t => t.PlannedProcessQty == 15m);
        Assert.True(mergedIntoB is not null, $"没找到被吸入 K3 的锚批（qty=15）。现场：{shape}");
        Assert.Equal(new DateTime(2026, 11, 2, 7, 30, 0), mergedIntoB!.PlannedStartTime);
        Assert.Equal(new DateTime(2026, 11, 2, 8, 15, 0), mergedIntoB.PlannedEndTime);

        // ④ 锚A（T1）必须保持原样：硬拒绝不能留下任何副作用。
        var anchorA = result.FinalTasks.SingleOrDefault(t => t.PlannedProcessQty == 10m);
        Assert.True(anchorA is not null, $"锚A 不见了或被改写（找不到 qty=10 的 Task）。现场：{shape}");
        Assert.Equal(new DateTime(2026, 11, 2, 7, 0, 0), anchorA!.PlannedStartTime);
        Assert.Equal(new DateTime(2026, 11, 2, 7, 30, 0), anchorA.PlannedEndTime);
    }

    // ─────────────────────────────────────────────────────────────
    // E4（0号位《审核与下一轮指令 v1.0》§2 反证清单）：
    //   「锚A含历史多个需求，任一成员交期不满足 ⇒ A 硬拒绝、完整回滚，再试 B」
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// E4：与 E1 同一套机制，但锚A是**多个历史成员**拼出来的批，且只有其中一个成员的交期紧。
    ///
    /// <para>形状（单件 3 分钟，日历 07:00-20:00）：</para>
    /// <list type="bullet">
    ///   <item>K0（qty 8，交期 20:00，seq 1）⇒ <b>T1 = 07:00-07:24</b>。</item>
    ///   <item>K1（qty 10，交期 08:00，seq 2）并入 T1：18 件 = 54 分钟 ⇒ 07:00-07:54；
    ///         K1 自身交期 08:00 ≥ 07:54、T1 既有成员 K0 交期 20:00 ⇒ 两侧都不违约 ⇒ <b>合并成功</b>
    ///         ⇒ <b>T1 = 07:00-07:54，qty 18，成员 {K0(20:00), K1(08:00)}</b>。锚A自此含两个历史成员。</item>
    ///   <item>K2（qty 5，交期 20:00，seq 3）想并入 T1：23 件 = 69 分钟 ⇒ 07:00-08:09；
    ///         锚A的成员交期取最小 = K1 的 08:00 ⇒ `:9896` <b>硬拒</b> ⇒ T2 独立排 <b>07:54-08:09</b>。</item>
    ///   <item>K3（qty 10，交期 10:00，seq 4）候选 {T1, T2}：T1 锚批交期 08:00（取成员最小）≤ 10:00 ⇒ tier0；
    ///         T2 交期 20:00 &gt; 10:00 ⇒ tier1 ⇒ <b>T1 先试</b>。
    ///         试 T1：28 件 = 84 分钟 ⇒ 07:00-08:24 &gt; 08:00 ⇒ <b>硬拒</b>；
    ///         试 T2：15 件 = 45 分钟 ⇒ <b>07:54-08:39</b> ≤ 10:00、不撞 T1 ⇒ <b>成功</b>。</item>
    /// </list>
    ///
    /// <para><b>为什么它验的是 E4 而不是 E1 的重复</b>：判定 `targetDue` 的那一步必须读**全部已承接成员**
    ///   （`allocationTaskShare[T1] = {K0, K1}`），只读 `SourceDraftId`（= K0，交期 20:00）时
    ///   **不会**拒绝 —— 那是 08:09 &lt; 20:00，合并会成功、份额会被改写。所以本用例同时钉住了
    ///   「多成员交期口径」和「硬拒后完整回滚（T1 仍是 qty 18 / 07:00-07:54，两个成员原样）」。</para>
    /// </summary>
    [Fact(DisplayName = "E4_锚A多成员交期不满足_硬拒并完整回滚_再试锚B")]
    public async Task E4_AnchorWithMultipleMembers_HardRejectsAndRollsBack()
    {
        DomainSolveResult result;
        long attempts;
        using (var scope = SolverDiagnostics.BeginScope())
        {
            result = await InModeAsync("PI_SORT", () => SolveAsync(BuildE4()));
            attempts = scope.Counters.MergeAttempts;
        }

        Assert.True(result.Success, result.ErrorMessage);

        var shape = string.Join(" | ", result.FinalTasks
            .OrderBy(t => t.PlannedStartTime)
            .Select(t => $"{t.PlannedStartTime:HH:mm}-{t.PlannedEndTime:HH:mm} qty={t.PlannedProcessQty}"));

        Assert.True(attempts >= 2,
            $"锚A硬拒绝后没有继续试锚B：MergeAttempts={attempts}。现场：{shape}");

        Assert.True(result.FinalTasks.Count == 2,
            $"期望 2 条 Task（含 2 个历史成员的锚A + 吸收 K3 的锚B），实得 {result.FinalTasks.Count} 条。现场：{shape}");

        // 完整回滚：锚A 必须仍是 07:00-07:54 / qty 18（K0+K1），一个字节都没被 K3 改过。
        var anchorA = result.FinalTasks.SingleOrDefault(t => t.PlannedProcessQty == 18m);
        Assert.True(anchorA is not null, $"锚A（qty=18，成员 K0+K1）不见了或被改写。现场：{shape}");
        Assert.Equal(new DateTime(2026, 11, 2, 7, 0, 0), anchorA!.PlannedStartTime);
        Assert.Equal(new DateTime(2026, 11, 2, 7, 54, 0), anchorA.PlannedEndTime);

        // 锚B 吸收了 K3：qty 5+10 = 15，07:54-08:39（紧接在锚A之后起排）。
        var anchorB = result.FinalTasks.SingleOrDefault(t => t.PlannedProcessQty == 15m);
        Assert.True(anchorB is not null, $"锚B（qty=15，成员 K2+K3）不见了。现场：{shape}");
        Assert.Equal(new DateTime(2026, 11, 2, 7, 54, 0), anchorB!.PlannedStartTime);
        Assert.Equal(new DateTime(2026, 11, 2, 8, 39, 0), anchorB.PlannedEndTime);
    }

    /// <summary>E4 专用夹具：锚A 由 K0+K1 两个历史成员构成，其中只有 K1 的交期紧。</summary>
    private static DomainSolveRequest BuildE4()
    {
        var request = BuildE1();

        static LogicalProductionDemand Mk(string key, decimal qty, int seq, DateTime due) => new()
        {
            LogicalDemandKey = key,
            PlanVersionId = 1L,
            DomainKey = "DOMAIN",
            AllocationSequence = seq,
            DemandKey = key,
            MaterialId = MaterialId,
            FactoryId = 1,
            StartStageCode = Stage,
            NetOutputQty = qty,
            PlannedProcessQty = qty,
            RequiredAvailableTime = due,
            DemandSequence = seq,
            ProductionInstructionNo = "P1",
            IsRootDemand = true,
            RouteCode = "DEFAULT",
            PathId = 1
        };

        var demands = new List<LogicalProductionDemand>
        {
            Mk("K0", 8m, 1, new DateTime(2026, 11, 2, 20, 0, 0)),   // 交期松 ⇒ 先排，随后与 K1 拼成锚A
            Mk("K1", 10m, 2, new DateTime(2026, 11, 2, 8, 0, 0)),    // 能合进锚A，但是锚A里最紧的交期
            Mk("K2", 5m, 3, new DateTime(2026, 11, 2, 20, 0, 0)),    // 交期松 ⇒ 独立成锚B
            Mk("K3", 10m, 4, new DateTime(2026, 11, 2, 10, 0, 0))    // 主角
        };

        return new DomainSolveRequest
        {
            PlanVersionId = request.PlanVersionId,
            DomainKey = request.DomainKey,
            PlanningStart = request.PlanningStart,
            PlanningEnd = request.PlanningEnd,
            LogicalProductionDemands = demands,
            PiRemainingFacts = request.PiRemainingFacts,
            PiStageQuantityBaselines = request.PiStageQuantityBaselines,
            RoutingOperations = request.RoutingOperations,
            OperationResourceEligibility = request.OperationResourceEligibility,
            MaterialStageDepartmentContexts = request.MaterialStageDepartmentContexts,
            Resources = request.Resources,
            CalendarSlots = request.CalendarSlots,
            StrategySnapshot = request.StrategySnapshot
        };
    }

    // ─────────────────────────────────────────────────────────────
    // R04（0号位《Batch_Code_Review_and_Action_Plan v1.0》§3-R04）：
    //   合并成功但结束时刻不变 ⇒ 必须仍按「已落定」上报，不能判成「没有产出」
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// R04 端到端反证。K1 的**计划加工量为 0**、净产出 5 件：
    ///   → 并入 K0 的 Task 后 `mergedQty = 10 + 0 = 10` ⇒ 时长不变 ⇒ `PlannedEndTime` 不变，
    ///     但 `Quantity` 由 10 变 15、份额已写入 ⇒ **合并真实发生**。
    ///
    /// <para><b>旧判据为什么会挂</b>：调用方用 `PlannedEndTime` 是否变化推断合并，
    ///   于是把这次真实合并上报成 `mergedIntoTask = null`；而任务表已被替换 ⇒ 状态与上报不一致，
    ///   K1 会被当成「没有落定」处理。</para>
    ///
    /// <para><b>鉴别力</b>：把 `mergedIntoTask = mergedTask;` 临时改回
    ///   `endChanged ? mergedTask : null`，本用例必须变红。</para>
    /// </summary>
    [Fact(DisplayName = "R04_计划加工量为0的合并_仍须如实上报落定")]
    public async Task R04_ZeroProcessQtyMerge_MustStillReportLanded()
    {
        var result = await InModeAsync("PI_SORT", () => SolveAsync(BuildZeroProcessQty()));
        Assert.True(result.Success, result.ErrorMessage);

        var shape = string.Join(" | ", result.FinalTasks
            .OrderBy(t => t.PlannedStartTime)
            .Select(t => $"{t.PlannedStartTime:HH:mm}-{t.PlannedEndTime:HH:mm} qty={t.Quantity}/proc={t.PlannedProcessQty}"));

        Assert.True(result.FinalTasks.Count == 1,
            $"期望 K1 并入 K0 后只剩 1 条 Task，实得 {result.FinalTasks.Count} 条。现场：{shape}");
        Assert.Equal(15m, result.FinalTasks[0].Quantity);
        Assert.Equal(10m, result.FinalTasks[0].PlannedProcessQty);

        Assert.DoesNotContain(result.UnscheduledTasks, u => u.DraftId == "K1");
    }

    /// <summary>R04 夹具：K0 正常 10 件；K1 净产出 5 件但**计划加工量 0**。</summary>
    private static DomainSolveRequest BuildZeroProcessQty()
    {
        var request = BuildE1();
        var baseDemand = request.LogicalProductionDemands[0];

        static LogicalProductionDemand Mk(string key, decimal netQty, decimal procQty, int seq) => new()
        {
            LogicalDemandKey = key,
            PlanVersionId = 1L,
            DomainKey = "DOMAIN",
            AllocationSequence = seq,
            DemandKey = key,
            MaterialId = MaterialId,
            FactoryId = 1,
            StartStageCode = Stage,
            NetOutputQty = netQty,
            PlannedProcessQty = procQty,
            RequiredAvailableTime = new DateTime(2026, 11, 2, 20, 0, 0),
            DemandSequence = seq,
            ProductionInstructionNo = "P1",
            IsRootDemand = true,
            RouteCode = "DEFAULT",
            PathId = 1
        };

        return new DomainSolveRequest
        {
            PlanVersionId = request.PlanVersionId,
            DomainKey = request.DomainKey,
            PlanningStart = request.PlanningStart,
            PlanningEnd = request.PlanningEnd,
            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                Mk("K0", 10m, 10m, 1),
                Mk("K1", 5m, 0m, 2)     // ← 加工量 0：合并后时长不变
            },
            PiRemainingFacts = request.PiRemainingFacts,
            PiStageQuantityBaselines = request.PiStageQuantityBaselines,
            RoutingOperations = request.RoutingOperations,
            OperationResourceEligibility = request.OperationResourceEligibility,
            MaterialStageDepartmentContexts = request.MaterialStageDepartmentContexts,
            Resources = request.Resources,
            CalendarSlots = request.CalendarSlots,
            StrategySnapshot = request.StrategySnapshot
        };
    }

    // ─────────────────────────────────────────────────────────────
    // A6 / §5.1 正交拆分：S1（只有多 Op PI 过滤）vs S2（+ 单 Op 无 PI 快路 + 来源 PI 守卫）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 0号位 §5.1 要求「补正交开关，拆分单 Op 快路与来源 PI 守卫的独立增量，
    ///   避免把 2.19× 全归功于某一刀」。
    ///
    /// <para>本用例用**同一份夹具**跑两次，只动两个实验开关：</para>
    /// <list type="bullet">
    ///   <item><b>S1</b> = `PI_ONLY` + 快路OFF + 守卫OFF ⇒ 无 PI 的 K2 仍会扫锚表并合进 K1；
    ///         `SingleOpMergeSkippedNoPi = 0`，最终 **1 条** Task。</item>
    ///   <item><b>S2</b> = `PI_ONLY` + 快路ON + 守卫ON ⇒ K2 连锚表都不扫；
    ///         `SingleOpMergeSkippedNoPi = 1`，最终 **2 条** Task。</item>
    /// </list>
    ///
    /// <para>这就是「S1→S2 的独立增量」—— 与 S0→S2 的整组收益分开。</para>
    /// </summary>
    [Fact(DisplayName = "A6_正交拆分_S1与S2的单Op快路必须可分辨")]
    public async Task A6_S1_vs_S2_SingleOpFastPathIsIsolable()
    {
        static async Task<(DomainSolveResult Result, long Skipped)> RunAsync(string fastPath, string guard)
        {
            PhaseTwoInitialScheduler.OverrideAnchorModeForTest("PI_ONLY");
            PhaseTwoInitialScheduler.OverrideExperimentSwitchesForTest(fastPath, guard);
            try
            {
                using var scope = SolverDiagnostics.BeginScope();
                var result = await new FiniteCapacitySolver().SolveAsync(BuildMixedPi(), CancellationToken.None);
                return (result, scope.Counters.SingleOpMergeSkippedNoPi);
            }
            finally
            {
                PhaseTwoInitialScheduler.OverrideExperimentSwitchesForTest(null, null);
                PhaseTwoInitialScheduler.OverrideAnchorModeForTest(null);
            }
        }

        // S1：快路关、守卫关 ⇒ K2 扫锚表并合进 K1。
        var (s1, s1Skipped) = await RunAsync(fastPath: "0", guard: "0");
        Assert.True(s1.Success, s1.ErrorMessage);
        Assert.True(s1Skipped == 0,
            $"S1（快路关）不应触发无 PI 快路；实得 SingleOpMergeSkippedNoPi={s1Skipped}");
        Assert.True(s1.FinalTasks.Count == 1,
            $"S1 下无 PI 的 K2 应合进 K1（1 条 Task），实得 {s1.FinalTasks.Count} 条");

        // S2：快路开、守卫开 ⇒ K2 直接跳过，不合批。
        var (s2, s2Skipped) = await RunAsync(fastPath: "1", guard: "1");
        Assert.True(s2.Success, s2.ErrorMessage);
        Assert.True(s2Skipped == 1,
            $"S2（快路开）必须正好触发一次无 PI 快路；实得 {s2Skipped}");
        Assert.True(s2.FinalTasks.Count == 2,
            $"S2 下 K2 不得合批（应 2 条 Task），实得 {s2.FinalTasks.Count} 条");
    }

    /// <summary>正交对照夹具：K1 有 PI、K2 无 PI，其余字段完全相同。</summary>
    private static DomainSolveRequest BuildMixedPi()
    {
        var request = Build(pi: "P1");
        var baseDemand = request.LogicalProductionDemands[0];

        static LogicalProductionDemand Mk(string key, string? pi, int seq) => new()
        {
            LogicalDemandKey = key,
            PlanVersionId = 1L,
            DomainKey = "DOMAIN",
            AllocationSequence = seq,
            DemandKey = key,
            MaterialId = MaterialId,
            FactoryId = 1,
            StartStageCode = Stage,
            NetOutputQty = 10m,
            PlannedProcessQty = 10m,
            RequiredAvailableTime = PlanningEnd,
            DemandSequence = seq,
            ProductionInstructionNo = pi,
            IsRootDemand = true,
            RouteCode = "DEFAULT",
            PathId = 1
        };

        return new DomainSolveRequest
        {
            PlanVersionId = request.PlanVersionId,
            DomainKey = request.DomainKey,
            PlanningStart = request.PlanningStart,
            PlanningEnd = request.PlanningEnd,
            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                Mk("K1", "P1", 1),
                Mk("K2", null, 2)      // ← 无 PI：快路的主角
            },
            PiRemainingFacts = request.PiRemainingFacts,
            PiStageQuantityBaselines = request.PiStageQuantityBaselines,
            RoutingOperations = request.RoutingOperations,
            OperationResourceEligibility = request.OperationResourceEligibility,
            MaterialStageDepartmentContexts = request.MaterialStageDepartmentContexts,
            Resources = request.Resources,
            CalendarSlots = request.CalendarSlots,
            StrategySnapshot = request.StrategySnapshot
        };
    }

    // ─────────────────────────────────────────────────────────────
    // 跨 PI 拒绝（0号位《审核与下一轮指令 v1.0》§6-A 反证清单）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 「源需求 PI 不相等不能合并」——**带 LEGACY 对照**，证明夹具本来是可合批的。
    ///
    /// <para>两条需求除 PI 外完全相同（同物料/同工序/同路径/同数量/交期都松）。
    ///   若只断言 PI_SORT 下是 2 条，无法排除「夹具根本合不了」的伪绿 ——
    ///   所以同一夹具再跑一遍 LEGACY（无 PI 过滤）：它必须合成 <b>1 条 qty 20</b>。
    ///   两次结果不同，差异才只能归因于 PI 门禁。</para>
    /// </summary>
    [Fact(DisplayName = "跨PI_不得合并_PI_SORT分2条_LEGACY对照合1条")]
    public async Task CrossPi_MustNotMerge_LegacyControlShowsMergeable()
    {
        var sorted = await InModeAsync("PI_SORT", () => SolveAsync(BuildCrossPi()));
        Assert.True(sorted.Success, sorted.ErrorMessage);

        var legacy = await InModeAsync("LEGACY", () => SolveAsync(BuildCrossPi()));
        Assert.True(legacy.Success, legacy.ErrorMessage);

        string Shape(DomainSolveResult r) => string.Join(" | ", r.FinalTasks
            .OrderBy(t => t.PlannedStartTime)
            .Select(t => $"{t.PlannedStartTime:HH:mm}-{t.PlannedEndTime:HH:mm} qty={t.PlannedProcessQty}"));

        // 对照：LEGACY 必须真的合上，否则本用例对「跨 PI 拒绝」没有鉴别力。
        Assert.True(legacy.FinalTasks.Count == 1,
            $"对照失败：LEGACY 下这两条需求也没合并（{legacy.FinalTasks.Count} 条）—— 夹具本身不可合批，本用例无鉴别力。现场：{Shape(legacy)}");
        Assert.Equal(20m, legacy.FinalTasks[0].PlannedProcessQty);

        // 被测：PI_SORT 下跨 PI 必须分开。
        Assert.True(sorted.FinalTasks.Count == 2,
            $"跨 PI 的需求被合并了（{sorted.FinalTasks.Count} 条）—— 违反同 PI 合批归属。现场：{Shape(sorted)}");
        Assert.Contains(sorted.FinalTasks, t => t.PlannedProcessQty == 10m);
    }

    /// <summary>跨 PI 夹具：K1(P1) / K2(P2)，除 PI 外逐字段相同且交期都松 ⇒ LEGACY 下必然可合。</summary>
    private static DomainSolveRequest BuildCrossPi()
    {
        var request = BuildE1();
        var baseDemand = request.LogicalProductionDemands[0];

        static LogicalProductionDemand Mk(string key, string pi, int seq) => new()
        {
            LogicalDemandKey = key,
            PlanVersionId = 1L,
            DomainKey = "DOMAIN",
            AllocationSequence = seq,
            DemandKey = key,
            MaterialId = MaterialId,
            FactoryId = 1,
            StartStageCode = Stage,
            NetOutputQty = 10m,
            PlannedProcessQty = 10m,
            RequiredAvailableTime = new DateTime(2026, 11, 2, 20, 0, 0),
            DemandSequence = seq,
            ProductionInstructionNo = pi,
            IsRootDemand = true,
            RouteCode = "DEFAULT",
            PathId = 1
        };

        return new DomainSolveRequest
        {
            PlanVersionId = request.PlanVersionId,
            DomainKey = request.DomainKey,
            PlanningStart = request.PlanningStart,
            PlanningEnd = request.PlanningEnd,
            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                Mk("K1", "P1", 1),
                Mk("K2", "P2", 2)
            },
            PiRemainingFacts = new List<PiRemainingFact>
            {
                new()
                {
                    ProductionInstructionNo = "P1", MaterialId = MaterialId, FactoryId = 1,
                    OrderType = string.Empty, PiQuantity = 100m, PiReceivedQty = 0m, PiRemainingQty = 100m
                },
                new()
                {
                    ProductionInstructionNo = "P2", MaterialId = MaterialId, FactoryId = 1,
                    OrderType = string.Empty, PiQuantity = 100m, PiReceivedQty = 0m, PiRemainingQty = 100m
                }
            },
            PiStageQuantityBaselines = new List<PiStageQuantityBaseline>
            {
                new()
                {
                    ProductionInstructionNo = "P1", MaterialId = MaterialId, StageCode = Stage,
                    StageFreeEligibleQty = 100m, AlreadyCommittedQty = 0m
                },
                new()
                {
                    ProductionInstructionNo = "P2", MaterialId = MaterialId, StageCode = Stage,
                    StageFreeEligibleQty = 100m, AlreadyCommittedQty = 0m
                }
            },
            RoutingOperations = request.RoutingOperations,
            OperationResourceEligibility = request.OperationResourceEligibility,
            MaterialStageDepartmentContexts = request.MaterialStageDepartmentContexts,
            Resources = request.Resources,
            CalendarSlots = request.CalendarSlots,
            StrategySnapshot = request.StrategySnapshot
        };
    }

    /// <summary>E1 专用夹具。三条同 PI、同物料、同工序，故意给出「锚A先试但必硬拒、锚B后试且合法」的时序。</summary>
    private static DomainSolveRequest BuildE1()
    {
        var planningStart = new DateTime(2026, 11, 2, 6, 0, 0);
        var planningEnd = new DateTime(2026, 11, 2, 20, 0, 0);

        static LogicalProductionDemand Mk(string key, decimal qty, int seq, DateTime due) => new()
        {
            LogicalDemandKey = key,
            PlanVersionId = 1L,
            DomainKey = "DOMAIN",
            AllocationSequence = seq,
            DemandKey = key,
            MaterialId = MaterialId,
            FactoryId = 1,
            StartStageCode = Stage,
            NetOutputQty = qty,
            PlannedProcessQty = qty,
            RequiredAvailableTime = due,
            DemandSequence = seq,
            ProductionInstructionNo = "P1",
            IsRootDemand = true,
            RouteCode = "DEFAULT",
            PathId = 1
        };

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "DOMAIN",
            PlanningStart = planningStart,
            PlanningEnd = planningEnd,
            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                Mk("K1", 10m, 1, new DateTime(2026, 11, 2, 7, 20, 0)),   // 交期紧 ⇒ T1 必延期
                Mk("K2", 5m, 2, new DateTime(2026, 11, 2, 20, 0, 0)),    // 交期松 ⇒ 排到 07:30
                Mk("K3", 10m, 3, new DateTime(2026, 11, 2, 9, 30, 0))
            },
            PiRemainingFacts = new List<PiRemainingFact>
            {
                new()
                {
                    ProductionInstructionNo = "P1",
                    MaterialId = MaterialId,
                    FactoryId = 1,
                    OrderType = string.Empty,
                    PiQuantity = 100m,
                    PiReceivedQty = 0m,
                    PiRemainingQty = 100m
                }
            },
            PiStageQuantityBaselines = new List<PiStageQuantityBaseline>
            {
                new()
                {
                    ProductionInstructionNo = "P1",
                    MaterialId = MaterialId,
                    StageCode = Stage,
                    StageFreeEligibleQty = 100m,
                    AlreadyCommittedQty = 0m
                }
            },
            RoutingOperations = new List<RoutingOperation>
            {
                new()
                {
                    MaterialId = MaterialId,
                    ProductionDepartmentId = DeptId,
                    RouteCode = "DEFAULT",
                    PathId = 1,
                    OperationCode = OpCode,
                    StageCode = Stage,
                    // ⚠ `RoutingOperation.StandardDuration` 是 **秒/件**（MES CycletTime 原样），
                    //   装配点 `PhaseOneConstraintBuilder.cs:1135` 会 `÷60` 转成分钟。
                    //   这里要 3 分钟/件 ⇒ 填 180 秒。（填 3 会被除成 0.05 分钟/件，合批时长全塌成秒级。）
                    StandardDuration = 180m,
                    SetupTime = 0m
                }
            },
            OperationResourceEligibility = new List<OperationResourceEligibility>
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
            },
            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = MaterialId, StageCode = Stage, ProductionDepartmentId = DeptId }
            },
            Resources = new List<ResourceDefinition>
            {
                new() { ResourceId = MaterialId, ResourceCode = "R1", FactoryCode = "F1", Capacity = 1m }
            },
            CalendarSlots = new List<ResourceCalendarSlot>
            {
                new()
                {
                    ResourceId = MaterialId,
                    Start = new DateTime(2026, 11, 2, 7, 0, 0),
                    End = new DateTime(2026, 11, 2, 20, 0, 0),
                    IsAvailable = true
                }
            },
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD",
                    AllowMerge = true,
                    AllowSplit = false
                },
                BatchPolicies = TestBatchPolicy.Permissive(new[] { MaterialId }, DeptId, allowMerge: true)
            }
        };
    }
}