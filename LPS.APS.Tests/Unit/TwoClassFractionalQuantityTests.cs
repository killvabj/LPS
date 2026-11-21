using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Core.Dto;
using LPS.APS.Shared.Models;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 0号位 v1.7（2026-11-19）《两类小数净量执行批裁决》——**V1 落地反证单测**。
///
/// 业务口径（逐字）：
///   · 第一类（整数需求被拆批）：`13 → 4+4+5`，`Σ = Q_raw`，**不得**各批进整变 15；
///   · 第二类（原生小数、整根执行）：`14.754` ⇒ `Q_exec = ceil(Q_raw) = 15`；
///       拆 3 批 = `5+5+5`（`Σ = 15`）、拆 4 批 = `4+4+4+3`（`Σ = 15`，**不是** 16）；
///       **逐批各自 ceil 是禁止的**，`Σb_i` 必须等于一次圆整出来的 `Q_exec`；
///   · 合批：**先合法合批、后对合批组原始合计向上圆整一次** —— `14.754 + 4.7666 = 19.5206 → 20`；
///       `14.4 + 14.4 = 28.8 → 29`（逐需求各 ceil 会得 30，明确禁止）；
///   · A/B 续作与锁定事实：**不套本规则**，保持原值（13.5 仍是 13.5）。
///
/// 全部纯内存，不触库（用户红线：Integration 直连生产库，只跑 Unit）。
/// </summary>
public class TwoClassFractionalQuantityTests
{
    private static readonly DateTime PlanningStart = new DateTime(2026, 9, 1);

    private static LogicalProductionDemand Demand(
        decimal netQty,
        bool isContinuation = false,
        bool noSplitMerge = false,
        decimal? procQty = null,
        string key = "D1",
        long allocationSequence = 1,
        string? piNo = null)
        => new()
        {
            LogicalDemandKey = key,
            PlanVersionId = 1L,
            DomainKey = "DOMAIN",
            AllocationSequence = allocationSequence,
            DemandKey = key,
            ProductionInstructionNo = piNo,
            MaterialId = 1,
            FactoryId = 1,
            StartStageCode = "STAGE1",
            NetOutputQty = netQty,
            PlannedProcessQty = procQty ?? netQty,
            RequiredAvailableTime = PlanningStart.AddDays(20),
            DemandSequence = 1,
            IsContinuation = isContinuation,
            NoSplitMerge = noSplitMerge
        };

    private static BatchPolicyRuleSnapshot Policy(decimal max, decimal? min = null)
        => new()
        {
            MaterialId = 1,
            ProductionDepartmentId = 100,
            MinExecutionBatchQty = min,
            MaxExecutionBatchQty = max,
            AllowSplit = true,
            AllowMerge = true
        };

    // ═══════════════════════════════════════════════════════════════════
    //  第一类：整数需求被拆批 —— 守恒，不得被圆整
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>N-01：整数 13 拆 3 批 ⇒ `4+4+5`，`Σ = 13`，全整数（逐批进整变 15 即红）。</summary>
    [Fact]
    public void N01_整数13拆三批_四四五_合计13()
    {
        var formation = PhaseTwoInitialScheduler.FormExecutionBatches(Demand(13m), Policy(max: 5m));

        Assert.True(formation.IsLegal, formation.ConflictReason);
        var batches = formation.Batches;

        Assert.Equal(3, batches.Count);
        Assert.Equal(new[] { 4m, 4m, 5m }, batches.Select(b => b.NetOutputQty).OrderBy(x => x).ToArray());
        Assert.Equal(13m, batches.Sum(b => b.NetOutputQty));
        Assert.All(batches, b => Assert.Equal(decimal.Truncate(b.NetOutputQty), b.NetOutputQty));
        // 第一类原始量本就是整数：登记值 = 执行量（不产生虚假的「原始小数」）
        Assert.All(batches, b => Assert.Equal(b.NetOutputQty, b.EffectiveRawNet));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  第二类：原生小数 —— 先 ceil 一次，再按整数分配
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>N-02：`14.754` 不拆批 ⇒ 执行净量 **15**，原始净量 **14.754** 原样保留。</summary>
    [Fact]
    public void N02_原生小数14_754单批_执行15_原始14_754保留()
    {
        var formation = PhaseTwoInitialScheduler.FormExecutionBatches(Demand(14.754m), Policy(max: 100m));

        Assert.True(formation.IsLegal, formation.ConflictReason);
        var batch = Assert.Single(formation.Batches);

        Assert.Equal(15m, batch.NetOutputQty);
        Assert.Equal(14.754m, batch.EffectiveRawNet);
        Assert.Equal(15m, decimal.Truncate(batch.NetOutputQty));   // 执行批必须是正整数根
    }

    /// <summary>N-03（拆批侧）：`14.754` 拆 3 批 ⇒ `5+5+5 = 15`。</summary>
    [Fact]
    public void N03_原生小数14_754拆三批_五五五_合计15()
    {
        var formation = PhaseTwoInitialScheduler.FormExecutionBatches(Demand(14.754m), Policy(max: 6m));

        Assert.True(formation.IsLegal, formation.ConflictReason);
        var batches = formation.Batches;

        Assert.Equal(3, batches.Count);
        Assert.Equal(new[] { 5m, 5m, 5m }, batches.Select(b => b.NetOutputQty).ToArray());
        Assert.Equal(15m, batches.Sum(b => b.NetOutputQty));
        // 原始量逐批不丢：Σ原始 = 14.754（末批补尾差，不因圆整被改写）
        Assert.Equal(14.754m, batches.Sum(b => b.EffectiveRawNet));
        Assert.All(batches, b => Assert.Equal(decimal.Truncate(b.NetOutputQty), b.NetOutputQty));
    }

    /// <summary>
    /// N-03/N-04（拆批侧反证）：`14.754` 拆 4 批 ⇒ `4+4+4+3 = 15`。
    /// 若按「逐批各自 ceil」得 `4+4+4+4 = 16` ⇒ 本用例红 —— 这是本规则的核心鉴别点。
    /// </summary>
    [Fact]
    public void N04_原生小数14_754拆四批_4443_合计15_非16()
    {
        var formation = PhaseTwoInitialScheduler.FormExecutionBatches(Demand(14.754m), Policy(max: 4m));

        Assert.True(formation.IsLegal, formation.ConflictReason);
        var batches = formation.Batches;

        Assert.Equal(4, batches.Count);
        Assert.Equal(new[] { 4m, 4m, 4m, 3m }, batches.Select(b => b.NetOutputQty).ToArray());
        Assert.Equal(15m, batches.Sum(b => b.NetOutputQty));      // 逐批 ceil 会变 16
        Assert.Equal(14.754m, batches.Sum(b => b.EffectiveRawNet));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  A/B 续作：冻结事实不被改写
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>N-10（局部）：A/B 续作 `13.5` 不参与新成批圆整，原值保持 13.5。</summary>
    [Fact]
    public void N10_AB续作13_5_保持原值_不套圆整()
    {
        var formation = PhaseTwoInitialScheduler.FormExecutionBatches(
            Demand(13.5m, isContinuation: true, noSplitMerge: true), Policy(max: 1m));

        Assert.True(formation.IsLegal, formation.ConflictReason);
        var batch = Assert.Single(formation.Batches);

        Assert.Equal(13.5m, batch.NetOutputQty);
        Assert.Equal(13.5m, batch.EffectiveRawNet);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  合批：对「组原始合计」一次圆整（禁逐需求圆整）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>N-04：`14.4 + 14.4 = 28.8` ⇒ 合批执行量 **29**（逐需求各 ceil 得 30 ⇒ 红）。</summary>
    [Fact]
    public void N04_合批14_4与14_4_一次圆整29_非30()
    {
        // 两条需求各自已是「执行量 15（原始 14.4）」—— 合批必须回到原始量求和再 ceil。
        var merged = PhaseTwoInitialScheduler.MergeExecutionQuantity(
            targetExecQuantity: 15m,
            incomingExecQuantity: 15m,
            rawTarget: 14.4m,
            rawIncoming: 14.4m,
            roundingEligible: true);

        Assert.Equal(29m, merged);      // 28.8 → 29
        Assert.NotEqual(30m, merged);   // 逐需求各 15 ⇒ 30（明确禁止）
    }

    /// <summary>N-03：`14.754 + 4.7666 = 19.5206` ⇒ 合批执行量 **20**（余量 0.4794 归组，不塞给任何需求）。</summary>
    [Fact]
    public void N03_合批14_754与4_7666_一次圆整20()
    {
        var merged = PhaseTwoInitialScheduler.MergeExecutionQuantity(
            targetExecQuantity: 15m,
            incomingExecQuantity: 5m,
            rawTarget: 14.754m,
            rawIncoming: 4.7666m,
            roundingEligible: true);

        Assert.Equal(20m, merged);
        Assert.Equal(0.4794m, merged - (14.754m + 4.7666m));   // Q_round = 0.4794，只入生产量
    }

    /// <summary>A/B 合批/续作：不套圆整，执行量按原值相加（无历史小数被悄悄进整）。</summary>
    [Fact]
    public void 合批_AB续作_不套圆整_按原值相加()
    {
        var merged = PhaseTwoInitialScheduler.MergeExecutionQuantity(
            targetExecQuantity: 13.5m,
            incomingExecQuantity: 2.5m,
            rawTarget: 13.5m,
            rawIncoming: 2.5m,
            roundingEligible: false);

        Assert.Equal(16m, merged);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  端到端（纯内存 Solve）：第二类穿过 Phase2/4/5 后仍有整数执行批、原始份额闭合
    // ═══════════════════════════════════════════════════════════════════

    private const int MaterialId = 1;
    private const int DeptId = 100;

    private static DomainSolveRequest BuildRequest(
        IReadOnlyList<LogicalProductionDemand> demands,
        decimal maxBatch,
        bool allowMerge = false,
        IReadOnlyList<PiRemainingFact>? piFacts = null,
        IReadOnlyList<PiStageQuantityBaseline>? piBaselines = null)
    {
        var ops = new List<LPS.APS.Core.Entities.APS.RoutingOperation>();
        var deps = new List<LPS.APS.Core.Entities.APS.RoutingDependency>();
        var elig = new List<LPS.APS.Core.Entities.APS.OperationResourceEligibility>();

        foreach (var (code, stage) in new[] { ("OP10", "STAGE1"), ("OP20", "STAGE2") })
        {
            ops.Add(new LPS.APS.Core.Entities.APS.RoutingOperation
            {
                MaterialId = MaterialId,
                ProductionDepartmentId = DeptId,
                RouteCode = "RTA",
                PathId = 1,
                OperationCode = code,
                StageCode = stage,
                StandardDuration = 60m * 60m,
                OperationPlanningMode = "FINITE_RESOURCE"
            });
            elig.Add(new LPS.APS.Core.Entities.APS.OperationResourceEligibility
            {
                MaterialId = MaterialId,
                ProductionDepartmentId = DeptId,
                RouteCode = "RTA",
                PathId = 1,
                OperationCode = code,
                ResourceId = 1,
                Priority = 1,
                CapacityFactor = 1m
            });
        }

        deps.Add(new LPS.APS.Core.Entities.APS.RoutingDependency
        {
            MaterialId = MaterialId,
            ProductionDepartmentId = DeptId,
            RouteCode = "RTA",
            PathId = 1,
            FromOperationCode = "OP10",
            ToOperationCode = "OP20"
        });

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "DOMAIN",
            PlanningStart = PlanningStart,
            PlanningEnd = PlanningStart.AddDays(60),
            LogicalProductionDemands = demands.ToList(),
            PiRemainingFacts = piFacts?.ToList() ?? new List<PiRemainingFact>(),
            PiStageQuantityBaselines = piBaselines?.ToList() ?? new List<PiStageQuantityBaseline>(),
            RoutingOperations = ops,
            RoutingDependencies = deps,
            OperationResourceEligibility = elig,
            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = MaterialId, StageCode = "STAGE1", ProductionDepartmentId = DeptId },
                new() { MaterialId = MaterialId, StageCode = "STAGE2", ProductionDepartmentId = DeptId }
            },
            ExecutionConstraints = Array.Empty<ExecutionConstraint>(),
            Resources = new List<ResourceDefinition>
            {
                new() { ResourceId = 1, ResourceCode = "R1", FactoryCode = "F1", Capacity = 1m }
            },
            CalendarSlots = new List<ResourceCalendarSlot>
            {
                new() { ResourceId = 1, Start = PlanningStart, End = PlanningStart.AddDays(60), IsAvailable = true }
            },
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD",
                    AllowSplit = true,
                    AllowMerge = allowMerge
                },
                BatchPolicies = new List<BatchPolicyRuleSnapshot>
                {
                    new()
                    {
                        MaterialId = MaterialId,
                        ProductionDepartmentId = DeptId,
                        MaxExecutionBatchQty = maxBatch,
                        AllowSplit = true,
                        AllowMerge = allowMerge
                    }
                }
            }
        };
    }

    /// <summary>
    /// **N-02 端到端**：`14.754` 单批 ⇒ 每道工序 Task 净量 **15**（正整数根），
    /// 而 `AllocationTaskShare` 合计仍为 **14.754**（原始需求份额，圆整增量不塞给需求）。
    /// 旧实现：Task 保持 14.754（小数执行批）或被 Phase5 整数门禁打红。
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task N02_端到端_14_754_执行15_份额仍14_754()
    {
        var demand = Demand(14.754m);

        var result = await new FiniteCapacitySolver().SolveAsync(BuildRequest(new[] { demand }, maxBatch: 100m));

        Assert.True(result.Success, result.ErrorMessage);

        var tasks = result.FinalTasks.Where(t => t.SourceDraftId == "D1").ToList();
        Assert.NotEmpty(tasks);
        Assert.All(tasks, t => Assert.Equal(15m, t.Quantity));
        Assert.All(tasks, t => Assert.Equal(decimal.Truncate(t.Quantity), t.Quantity));

        // 需求承接账：ΣShare = Q_raw = 14.754（不是 15）
        var shares = result.AllocationShares.Where(s => s.AllocationSequence == 1).ToList();
        Assert.Equal(14.754m, shares.Sum(s => s.ComponentQty));
        Assert.Empty(result.UnscheduledTasks);

        // ── 0号位 v1.7 Q2 / P6：圆整增量必须有 Trace 留痕（执行净量 15 − 原始承接 14.754 = 0.246）──
        var rounding = Assert.Single(result.SolveTraceNotes.Where(n => n.ReasonCode == "EXECUTION_QTY_ROUNDING"));
        Assert.Contains("执行净量=15", rounding.Message);
        Assert.Contains("原始需求承接=14.754", rounding.Message);
        // （上面已断言 UnscheduledTasks 为空 ⇒ 圆整增量确实未记成未排）
    }

    /// <summary>
    /// **N-04 真实合批路径**：目标 Task 已是「执行量 15、原始 14.4」，再并入一条原始 `14.4` 的需求
    /// ⇒ 合批执行量必须 **29**（28.8 一次 ceil），**不是** 30（逐需求各 15 相加）。
    /// 与生产一致：目标批的原始净量由 `RawBatchNetByExecutionBatchKey` 提供（Phase2 批形成时登记）。
    /// </summary>
    [Fact]
    public void N04_真实合批路径_目标15原始14_4_并入14_4_执行29_非30()
    {
        var t0 = new DateTime(2026, 11, 10, 8, 0, 0);
        const int resourceId = 1;

        var target = new FinalTaskDraft
        {
            FinalDraftId = "T-D2",
            SourceDraftId = "D2",
            MaterialId = MaterialId,
            ResourceId = resourceId,
            StageCode = "STAGE1",
            OperationCode = "OP10",
            PlannedStartTime = t0,
            PlannedEndTime = t0.AddHours(1),
            SetupTime = 0m,
            Quantity = 15m,             // 第二类：ceil(14.4) 后的执行量
            PlannedProcessQty = 14.4m,
            ExecutionBatchDraftKey = "EB|D2|001",
        };

        var graph = new RoutingGraph();
        graph.Operations[OperationNodeKey.Of("STAGE1", "OP10")] = new OperationNode
        {
            OperationCode = "OP10",
            StageCode = "STAGE1",
            StandardDuration = 6m,
            OperationPlanningMode = "FINITE_RESOURCE",
            RouteCode = "DEFAULT",
            PathId = 1,
            ProductionDepartmentId = DeptId,
        };

        var constraints = new ConstraintContext();
        constraints.RawBatchNetByExecutionBatchKey["EB|D2|001"] = 14.4m;   // 目标批原始净量
        constraints.ResourceCapacityFactors[
            new EligibilityLookupKey(MaterialId, DeptId, "DEFAULT", 1, "OP10")]
            = new Dictionary<int, decimal> { [resourceId] = 1m };
        constraints.ResourceCalendars[resourceId] = new List<TimeWindow>
        {
            new(t0.AddHours(-8), t0.AddHours(20))
        };

        var incoming = new LogicalProductionDemand
        {
            LogicalDemandKey = "D1",
            DemandKey = "D1",
            MaterialId = MaterialId,
            NetOutputQty = 14.4m,
            PlannedProcessQty = 14.4m,
            StartStageCode = "STAGE1",
            RouteCode = "DEFAULT",
            PathId = 1,
            RequiredAvailableTime = DateTime.MaxValue,
        };

        var shares = new Dictionary<string, List<(string DemandKey, decimal ShareQty)>>(StringComparer.Ordinal);
        var occupancy = new Dictionary<int, List<TimeWindow>>();

        var merged = new PhaseTwoInitialScheduler().TryMergeDemandIntoTask(
            incoming, target, graph, constraints, occupancy, shares, t0.AddDays(30));

        Assert.NotNull(merged);
        Assert.Equal(29m, merged!.Quantity);                    // 28.8 → 29
        Assert.NotEqual(30m, merged.Quantity);                  // 逐需求各 15 会得 30
        Assert.Equal(29m, merged.PlannedProcessQty);            // 投入侧按实际执行量 29 同比例（N-11），不是 28.8

        // 份额血缘只记被并入需求的**原始量**（锚点需求由 SourceDraftId 隐式承担）
        Assert.True(shares.TryGetValue(merged.FinalDraftId, out var lineage));
        Assert.Equal(14.4m, lineage!.Single(s => s.DemandKey == "D1").ShareQty);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  N-05 / N-06：PI/Stage 硬上限 = 合法部分排（正常业务未排，非技术 ERROR）
    // ═══════════════════════════════════════════════════════════════════

    private static IReadOnlyList<PiRemainingFact> PiFacts(string pi, decimal cap)
        => new List<PiRemainingFact>
        {
            new()
            {
                ProductionInstructionNo = pi,
                MaterialId = MaterialId,
                FactoryId = 1,
                OrderType = string.Empty,
                PiQuantity = cap,
                PiReceivedQty = 0m,
                PiRemainingQty = cap
            }
        };

    private static IReadOnlyList<PiStageQuantityBaseline> PiBaselines(string pi, decimal stageFree)
        => new List<PiStageQuantityBaseline>
        {
            new()
            {
                ProductionInstructionNo = pi,
                MaterialId = MaterialId,
                StageCode = "STAGE1",
                StageFreeEligibleQty = stageFree,
                AlreadyCommittedQty = 0m
            }
        };

    /// <summary>
    /// **N-05**：原生小数 `14.754`、PI/Stage 合法上限 **14** ⇒ 排 **14**（整数执行批），
    /// 原始未排 **0.754**（正常业务未排），`Success=true`（**不是**技术 ERROR）。
    /// 旧实现：`PI_LEGAL_QTY_OVER_LIMIT` ⇒ 整条未排（0 Task）。
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task N05_上限14_排14_未排0_754_非ERROR()
    {
        var demand = Demand(14.754m, piNo: "PI1");
        var result = await new FiniteCapacitySolver().SolveAsync(
            BuildRequest(new[] { demand }, maxBatch: 100m,
                piFacts: PiFacts("PI1", 14m), piBaselines: PiBaselines("PI1", 14m)));

        Assert.True(result.Success, result.ErrorMessage);

        // 物理生产账：执行批为**整数 14**（不得 15、不得 14.754）
        var tasks = result.FinalTasks.Where(t => t.SourceDraftId == "D1").ToList();
        Assert.NotEmpty(tasks);
        Assert.All(tasks, t => Assert.Equal(14m, t.Quantity));

        // 需求承接账：ΣShare = 14（原始需求只承接被合法排产的部分）
        Assert.Equal(14m, result.AllocationShares.Sum(s => s.ComponentQty));

        // 原需求剩余 0.754 必须**显式**未排，且原因是 PI 合法量不足（正常业务未排）
        var uns = Assert.Single(result.UnscheduledTasks.Where(u => u.DraftId == "D1"));
        Assert.Equal(0.754m, uns.UnscheduledQty);
        Assert.Equal("PI_LEGAL_QTY_OVER_LIMIT", uns.Reason);
    }

    /// <summary>
    /// **N-06**：原生小数 `14.754`、合法上限 **0** ⇒ 整条未排 14.754，**不产任何 Task**
    /// （不得伪造虚拟 Task / 超 PI 量 / 技术 ERROR）。
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task N06_上限0_整条未排14_754_无虚拟Task_非ERROR()
    {
        var demand = Demand(14.754m, piNo: "PI1");
        var result = await new FiniteCapacitySolver().SolveAsync(
            BuildRequest(new[] { demand }, maxBatch: 100m,
                piFacts: PiFacts("PI1", 0m), piBaselines: PiBaselines("PI1", 0m)));

        Assert.True(result.Success, result.ErrorMessage);

        // 不产 Task（含非虚拟、虚拟一概不得有）
        Assert.Empty(result.FinalTasks.Where(t => t.SourceDraftId == "D1"));

        var uns = Assert.Single(result.UnscheduledTasks.Where(u => u.DraftId == "D1"));
        Assert.Equal(14.754m, uns.UnscheduledQty);
        Assert.Equal("PI_LEGAL_QTY_OVER_LIMIT", uns.Reason);
    }

    /// <summary>
    /// **N-09（同 PI 份额）**：同一 PI × 同 Stage 的两条需求、合法自由量只有 14 ⇒
    ///   **先到先得**：一条排满 14，另一条按剩余 0 ⇒ 整条未排 14.754。
    /// 鉴别点：上限必须扣减**本 Run 已形成批**（`PiStageCommitLedger`），
    ///   否则第二条会算出「未扣减」的上限又去抢，批级门禁拒 ⇒ 退回旧「整条未排」或超占。
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task N09_同PI两需求_先到先得_合计不超上限()
    {
        var d1 = Demand(14.754m, key: "D1", allocationSequence: 1, piNo: "PI1");
        var d2 = Demand(14.754m, key: "D2", allocationSequence: 2, piNo: "PI1");

        var result = await new FiniteCapacitySolver().SolveAsync(
            BuildRequest(new[] { d1, d2 }, maxBatch: 100m,
                piFacts: PiFacts("PI1", 14m), piBaselines: PiBaselines("PI1", 14m)));

        Assert.True(result.Success, result.ErrorMessage);

        // 只批出一条约 14（同 PI 同 Stage 不得超 14），另一条整条未排
        var op10 = result.FinalTasks.Where(t => t.OperationCode == "OP10").ToList();
        Assert.Single(op10);
        Assert.Equal(14m, op10[0].Quantity);

        // 两条未排行：一条是「先到者圆整后仍差 0.754」的部分未排，一条是「后到者无余额」的整条未排
        var unsRows = result.UnscheduledTasks.ToDictionary(u => u.DraftId, u => u);
        Assert.Equal(2, unsRows.Count);
        Assert.Equal(0.754m, unsRows["D1"].UnscheduledQty);
        Assert.Equal(14.754m, unsRows["D2"].UnscheduledQty);
        Assert.All(unsRows.Values, u => Assert.Equal("PI_LEGAL_QTY_OVER_LIMIT", u.Reason));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  N-04（Stage 批路径）：多工序 Stage 合批同样按「组原始合计」一次 ceil
    // ═══════════════════════════════════════════════════════════════════

    private static LogicalProductionDemand RawDemand(string key, decimal qty) => new()
    {
        LogicalDemandKey = key,
        DemandKey = key,
        MaterialId = MaterialId,
        NetOutputQty = qty,
        PlannedProcessQty = qty,
        StartStageCode = "STAGE1",
        RouteCode = "DEFAULT",
        PathId = 1,
        RequiredAvailableTime = DateTime.MaxValue,
    };

    /// <summary>
    /// **N-04（Stage 批）**：目标 Stage 批「执行量 15 / 原始 14.4」，并入原始 14.4 的需求
    /// ⇒ 三道工序的执行量都是 **29**（`ceil(28.8)`），投入侧 28.8 保持小数。
    /// 旧实现：`Quantity` 直接相加 ⇒ 15+15=30（逐需求圆整），违反「合批组一次圆整」。
    /// </summary>
    [Fact]
    public void N04_Stage批合批_目标15原始14_4_并入14_4_每道29()
    {
        var t0 = new DateTime(2026, 11, 10, 8, 0, 0);
        const int resourceId = 1;
        var opCodes = new[] { "OP10", "OP20", "OP30" };

        List<OperationNode> Nodes() => opCodes.Select(op => new OperationNode
        {
            OperationCode = op,
            StageCode = "STAGE1",
            StandardDuration = 1m,             // 分钟/件
            OperationPlanningMode = "FINITE_RESOURCE",
            RouteCode = "DEFAULT",
            PathId = 1,
            ProductionDepartmentId = DeptId,
        }).ToList();

        var graph = new RoutingGraph();
        foreach (var node in Nodes()) graph.Operations[OperationNodeKey.Of("STAGE1", node.OperationCode)] = node;
        graph.Dependencies[OperationNodeKey.Of("STAGE1", "OP20")] = new List<DependencyEdge>
        {
            new() { From = OperationNodeKey.Of("STAGE1", "OP10"), To = OperationNodeKey.Of("STAGE1", "OP20") },
        };
        graph.Dependencies[OperationNodeKey.Of("STAGE1", "OP30")] = new List<DependencyEdge>
        {
            new() { From = OperationNodeKey.Of("STAGE1", "OP20"), To = OperationNodeKey.Of("STAGE1", "OP30") },
        };
        graph.RootOperations.Add(OperationNodeKey.Of("STAGE1", "OP10"));

        var constraints = new ConstraintContext();
        foreach (var op in opCodes)
        {
            var key = new EligibilityLookupKey(MaterialId, DeptId, "DEFAULT", 1, op);
            constraints.OperationResourceEligibility[key] = new List<int> { resourceId };
            constraints.ResourceCapacityFactors[key] = new Dictionary<int, decimal> { [resourceId] = 1m };
        }
        constraints.ResourceCalendars[resourceId] = new List<TimeWindow> { new(t0, t0.AddHours(48)) };
        // 原始净量登记：目标批 14.4（执行量 15）、来批 14.4
        constraints.RawBatchNetByExecutionBatchKey["EB|A|001"] = 14.4m;
        constraints.RawBatchNetByExecutionBatchKey["EB|C|001"] = 14.4m;

        var targetBatch = new List<FinalTaskDraft>();
        var cursor = t0;
        foreach (var op in opCodes)
        {
            targetBatch.Add(new FinalTaskDraft
            {
                FinalDraftId = "T-A-" + op,
                SourceDraftId = "A",
                MaterialId = MaterialId,
                ResourceId = resourceId,
                StageCode = "STAGE1",
                OperationCode = op,
                PlannedStartTime = cursor,
                PlannedEndTime = cursor.AddMinutes(15),
                SetupTime = 0m,
                Quantity = 15m,                 // 第二类：ceil(14.4)
                PlannedProcessQty = 14.4m,
                ExecutionBatchDraftKey = "EB|A|001",
            });
            cursor = cursor.AddMinutes(15);
        }

        var scheduled = new List<FinalTaskDraft>(targetBatch);
        var shares = new Dictionary<string, List<(string DemandKey, decimal ShareQty)>>(StringComparer.Ordinal);
        // 资源键必须存在（`FindFirstAvailableSlot` 直接按 resourceId 取表）
        var occupancy = new Dictionary<int, List<TimeWindow>>
        {
            [resourceId] = new List<TimeWindow>()
        };
        var demandByKey = new Dictionary<string, LogicalProductionDemand>(StringComparer.Ordinal)
        {
            ["A"] = RawDemand("A", 14.4m),
            ["C"] = RawDemand("C", 14.4m),
        };

        var merged = new PhaseTwoInitialScheduler().TryMergeDemandIntoStageBatch(
            RawDemand("C", 14.4m), Nodes(), graph, "FORWARD", targetBatch, constraints,
            occupancy, scheduled, shares, demandByKey,
            t0, t0.AddDays(30), t0, new StageOverlapParams(), null,
            out var combinedTasks, incomingBatchKey: "EB|C|001");

        Assert.NotNull(merged);
        Assert.Equal(3, combinedTasks.Count);
        Assert.All(combinedTasks, t => Assert.Equal(29m, t.Quantity));          // 28.8 → 29，不是 30
        Assert.All(combinedTasks, t => Assert.Equal(29m, t.PlannedProcessQty));   // 投入侧按执行量 29 同比例（N-11）

        // 份额只记被并入需求的原始量（锚点 A 由 SourceDraftId 隐式承担）
        Assert.All(combinedTasks, t =>
        {
            Assert.True(shares.TryGetValue(t.FinalDraftId, out var line));
            Assert.Equal(14.4m, line!.Single(s => s.DemandKey == "C").ShareQty);
        });
    }

    /// <summary>
    /// **B-010 缺策略兜底批同样受两类小数约束**（FAMILY_CYL 离线重放实测：兜底批曾直接产出
    /// `13.2924` 的小数执行批 ⇒ 整数门禁打红整个 Domain）。
    /// 无 PI ⇒ 走 v1.15 §1320「合法无 PI 来源」软 Preferred（不进门禁、只出 1 批 Q_C）。
    /// </summary>
    [Fact]
    public void B010兜底批_原生小数13_2924_执行14_原始量保留()
    {
        var formation = PhaseTwoInitialScheduler.FormExecutionBatches(Demand(13.2924m), policy: null);

        Assert.True(formation.IsLegal, formation.ConflictReason);
        var batch = Assert.Single(formation.Batches);
        Assert.Equal(14m, batch.NetOutputQty);                 // ceil(13.2924)，整数执行批
        Assert.Equal(13.2924m, batch.EffectiveRawNet);         // 原始净量原样保留
    }

    /// <summary>
    /// **N-12（0号位 v1.7 §四）**：历史 C01 例 `51 → 25.5+25.5` 是**负例**（新执行批资格下必须被拒），
    /// 合法正例为 `51 → 25+26`。
    /// 这里钉住「形成侧永不产出 25.5」：51 拆 2 批只能是 `26+25`（整数），
    /// 一旦回归成按比例 `25.5+25.5`，本用例立刻红。
    /// （拒绝侧由 Phase5 整数门禁独立把关；本用例覆盖形成侧。）
    /// </summary>
    [Fact]
    public void N12_C01历史反例_51拆两批_必须26加25_不得25点5加25点5()
    {
        var formation = PhaseTwoInitialScheduler.FormExecutionBatches(Demand(51m), Policy(max: 26m));

        Assert.True(formation.IsLegal, formation.ConflictReason);
        var nets = formation.Batches.Select(b => b.NetOutputQty).OrderBy(x => x).ToArray();

        Assert.Equal(new[] { 25m, 26m }, nets);                              // 合法正例
        Assert.Equal(51m, nets.Sum());                                       // 守恒
        Assert.All(formation.Batches, b => Assert.Equal(decimal.Truncate(b.NetOutputQty), b.NetOutputQty));
        Assert.DoesNotContain(formation.Batches, b => b.NetOutputQty == 25.5m);   // 历史负例的数值指纹
    }

    // ═══════════════════════════════════════════════════════════════════
    // N-08（2026-11-20 补）：**不同根订单合批后，各自份额分别守恒**
    //   0号位 v1.8 §二 要求「至少 N01～N12 回归」；N-08 此前只由 check#1 间接覆盖、
    //   未单独立例。本组用硬校验正反两例直接锁「A 的份额不得挪到 B」。
    // ═══════════════════════════════════════════════════════════════════

    private static (bool Valid, string Error) ValidateShares(
        List<LPS.APS.Core.Dto.FinalTaskDraft> tasks,
        List<LPS.APS.Core.Dto.AllocationTaskShare> shares,
        LogicalProductionDemand[] demands,
        Dictionary<string, List<(string, decimal)>>? mergeLineage = null)
        => PhaseFiveCompression.ValidateHardResultForTest(
            tasks,
            shares,
            new List<LPS.APS.Core.Dto.FinalTaskPeggingDraft>(),
            new LPS.APS.Core.Dto.DomainSolveRequest { LogicalProductionDemands = demands.ToList() },
            new ConstraintContext(),
            new HashSet<string>(),
            mergeLineage ?? new Dictionary<string, List<(string, decimal)>>());

    private static LPS.APS.Core.Dto.FinalTaskDraft ShareTask(string id, string source, decimal qty) => new()
    {
        FinalDraftId = id,
        SourceDraftId = source,
        MaterialId = 1,
        FactoryId = 1,
        StageCode = "STAGE1",
        OperationCode = "OP10",
        Quantity = qty,
        PlannedProcessQty = qty,
        SetupTime = 0m,
        PlannedStartTime = PlanningStart,
        PlannedEndTime = PlanningStart.AddHours(1),
    };

    [Fact(DisplayName = "N-08 不同根订单合批：A10+B5 份额各自守恒 通过")]
    public void N08_不同根订单合批_份额分别守恒_通过()
    {
        // A（alloc=1, 净量 10）为锚点，B（alloc=2, 净量 5）并入 ⇒ 执行量 15。
        // 合批血缘声明 B 占 5 ⇒ A 自然占 10（15 − 5），两个根订单各自守恒。
        var tasks = new List<LPS.APS.Core.Dto.FinalTaskDraft> { ShareTask("T1", "A", 15m) };
        var shares = new List<LPS.APS.Core.Dto.AllocationTaskShare>
        {
            new() { FinalDraftId = "T1", AllocationSequence = 1, ComponentQty = 10m },
            new() { FinalDraftId = "T1", AllocationSequence = 2, ComponentQty = 5m },
        };
        var demands = new[]
        {
            Demand(10m, key: "A", allocationSequence: 1),
            Demand(5m, key: "B", allocationSequence: 2),
        };
        var lineage = new Dictionary<string, List<(string, decimal)>>(StringComparer.Ordinal)
        {
            ["T1"] = new List<(string, decimal)> { ("B", 5m) },
        };

        var (valid, err) = ValidateShares(tasks, shares, demands, lineage);

        Assert.True(valid, "A 拿 10、B 拿 5、合计 15 ⇒ 各自份额守恒，必须通过。" + err);
    }

    [Fact(DisplayName = "N-08反例 把 B 的份额挪给 A（血缘只认 2）⇒ 必须拒")]
    public void N08_反例_份额跨根订单挪用_必须拒()
    {
        // 执行量 15、A 净量 10、B 净量 5。血缘却只给 B 2 ⇒ A 被算成 13（超净量 10）、B 少 3。
        var tasks = new List<LPS.APS.Core.Dto.FinalTaskDraft> { ShareTask("T1", "A", 15m) };
        var shares = new List<LPS.APS.Core.Dto.AllocationTaskShare>
        {
            new() { FinalDraftId = "T1", AllocationSequence = 1, ComponentQty = 10m },
            new() { FinalDraftId = "T1", AllocationSequence = 2, ComponentQty = 5m },
        };
        var demands = new[]
        {
            Demand(10m, key: "A", allocationSequence: 1),
            Demand(5m, key: "B", allocationSequence: 2),
        };
        var lineage = new Dictionary<string, List<(string, decimal)>>(StringComparer.Ordinal)
        {
            ["T1"] = new List<(string, decimal)> { ("B", 2m) },   // 少给 B 3 ⇒ A 被多算 3
        };

        var (valid, err) = ValidateShares(tasks, shares, demands, lineage);

        Assert.False(valid, "血缘只给 B 2、A 被算成 13 超其净量 10 ⇒ 份额跨根订单挪用，必须拒。" + err);
    }

    [Fact(DisplayName = "N-12 拒绝侧：非续作需求出现 25.5 小数执行批 ⇒ 整数门禁硬拒")]
    public void N12_拒绝侧_小数执行批_必须拒()
    {
        // 两批 25.5 + 25.5 = 51 ⇒ **三量闭合成立**（不会被 check#1 抢先拦），
        // 再由 check#9 整数门禁判每个 Task 的 Quantity 必须是整数 ⇒ 应命中 FRACTIONAL_EXECUTION_BATCH_QTY。
        var t1 = ShareTask("T1", "A", 25.5m);
        var t2 = ShareTask("T2", "A", 25.5m);
        t1 = new LPS.APS.Core.Dto.FinalTaskDraft
        {
            FinalDraftId = t1.FinalDraftId, SourceDraftId = t1.SourceDraftId,
            MaterialId = t1.MaterialId, FactoryId = t1.FactoryId,
            StageCode = t1.StageCode, OperationCode = t1.OperationCode,
            Quantity = 25.5m, PlannedProcessQty = 25.5m, SetupTime = 0m,
            PlannedStartTime = t1.PlannedStartTime, PlannedEndTime = t1.PlannedEndTime,
            ExecutionBatchDraftKey = "EB1", StageExecutionBatchDraftKey = "EB1",
        };
        t2 = new LPS.APS.Core.Dto.FinalTaskDraft
        {
            FinalDraftId = t2.FinalDraftId, SourceDraftId = t2.SourceDraftId,
            MaterialId = t2.MaterialId, FactoryId = t2.FactoryId,
            StageCode = t2.StageCode, OperationCode = t2.OperationCode,
            Quantity = 25.5m, PlannedProcessQty = 25.5m, SetupTime = 0m,
            PlannedStartTime = t2.PlannedStartTime, PlannedEndTime = t2.PlannedEndTime,
            ExecutionBatchDraftKey = "EB2", StageExecutionBatchDraftKey = "EB2",
        };
        var tasks = new List<LPS.APS.Core.Dto.FinalTaskDraft> { t1, t2 };
        var shares = new List<LPS.APS.Core.Dto.AllocationTaskShare>
        {
            new() { FinalDraftId = "T1", AllocationSequence = 1, ComponentQty = 25.5m },
            new() { FinalDraftId = "T2", AllocationSequence = 1, ComponentQty = 25.5m },
        };
        var demands = new[] { Demand(51m, key: "A", allocationSequence: 1) };

        var (valid, err) = ValidateShares(tasks, shares, demands);

        Assert.False(valid, "非续作需求 51 被拆出 25.5 小数执行批 ⇒ 必须被整数门禁硬拒。" + err);
        Assert.Contains("FRACTIONAL_EXECUTION_BATCH_QTY", err);
    }

    [Fact(DisplayName = "N-12 豁免侧：A/B 续作 13.5 小数执行批 ⇒ 保持冻结事实、不套圆整")]
    public void N12_豁免侧_AB续作小数_放行()
    {
        var tasks = new List<LPS.APS.Core.Dto.FinalTaskDraft> { ShareTask("T1", "AB", 13.5m) };
        var shares = new List<LPS.APS.Core.Dto.AllocationTaskShare>
        {
            new() { FinalDraftId = "T1", AllocationSequence = 1, ComponentQty = 13.5m },
        };
        var demands = new[] { Demand(13.5m, key: "AB", allocationSequence: 1, isContinuation: true) };

        var (valid, err) = ValidateShares(tasks, shares, demands);

        Assert.True(valid, "A/B 续作 13.5 是冻结历史事实，不得套新圆整、更不得被整数门禁误杀。" + err);
    }
}