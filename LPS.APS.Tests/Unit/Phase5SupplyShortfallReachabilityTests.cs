using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using RoutingOperation = LPS.APS.Core.Entities.APS.RoutingOperation;
using OperationResourceEligibility = LPS.APS.Core.Entities.APS.OperationResourceEligibility;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **R04-⑥「Phase5 供给不足必须未排」的可达性实测**（2026-10-29）。
///
/// <para><b>为什么要有这个文件</b>：0号位 在
/// `03_APS_V1_各号位R01-R12未关闭责任横向校准…_v1.0_20261025.md` §二 1号位行 要求
/// 「Phase5 供给不足必须未排」，并在 `01_正式回复1号位_工艺单位_PV195_R01-R09未闭环_v1.1.md:34`
/// 记作「Phase5 供给不足**未排出口**欠」。我方回执
/// `APS_V1_20261026_R01-R12责任校准…_v1.0_20261026.md` §2.1 ⑥ 承诺交的负例形态是
/// 「旧代码 `Success=true` + 有 Task；新代码未排 + 原因」。</para>
///
/// <para><b>实测结论（本文件钉死）</b>：那个负例的**前提不存在**。</para>
/// <list type="number">
///   <item>T1：单需求缺料时，**Phase2 就把它拦下了**（`PhaseTwoInitialScheduler.cs:3809/4043`
///     与 `PhaseFourLocalRepair.cs:1283` 用的是**同一个**判据：
///     `GetMaterialEarliestTime(demand.AllocationSequence, demand.NetOutputQty, …)` 的 `isSufficient`），
///     Phase5 的入参 `scheduleResult.ScheduledTasks + repairResult.RepairedTasks`（`PhaseFiveCompression.cs:37-38`）
///     里根本不会有这条需求的 Task ⇒ `PhaseFiveCompression.cs:1292 / :1746` 那两个
///     `!sufficient` 分支**在整条流水线上不可达** ⇒ 「Phase5 里把它改成未排」**改不出任何行为差异**，
///     也交不出负例。
///     <para>行号口径：以上全部按**干净 r13961**（`svn cat -r 13961` ＋ `grep -n`），不是按带未提交改动的本地工作副本。</para></item>
///   <item>T2：真正会「供给不足却照样排下」的形状是**两个需求共用同一条 `AllocationSequence`**——
///     `PhaseOneConstraintBuilder.BuildMaterialAvailability:1371-1388`（`GroupBy` 在 `:1374`）只按 `AllocationSequence` 聚合，
///     `GetMaterialEarliestTime` 对**每条需求**都拿**整份**供给去比 `NetOutputQty`，
///     **不扣除同序列其他需求已占用的量** ⇒ 各自都判「够」，合起来超耗。这是可达的、可复算的。</item>
///   <item>T3：对照 —— 同两个需求、同样的量，只把 `AllocationSequence` 拆成两条各 10 ⇒ 全排是**合法**的。
///     T2/T3 一起才说明「差别只在序列共享」，不是夹具凑出来的。</item>
/// </list>
///
/// <para><b>本文件只钉现状，不主张现状正确</b>：T2 断言的「两个都排下」是**当前实现的实际行为**，
/// 它是不是缺陷取决于「一条 `AllocationSequence` 能否被多条需求共享」这个**口径**——
/// 那是 2号位 R03（共享物理库存／跨域 Candidate 守恒）的辖区，不是我方可以自定的。
/// 本文件的作用是：口径一旦裁定，谁改、改哪、负例怎么复算，都从这里出发。</para>
/// </summary>
public class Phase5SupplyShortfallReachabilityTests
{
    private static readonly DateTime Day = new(2026, 9, 1);

    private const string Stage1 = "STAGE1";
    private const int MaterialId = 1;
    private const int DeptId = 100;
    private const int ResourceId = 1;
    private const int FactoryId = 1;

    // ════════════════════════════════════════════════════════════════════
    // T1：Phase5 的缺料出口在流水线上不可达
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 需求要 6、供给只有 1 ⇒ **Phase2 就返回空 Task 列表**
    /// （`RecordPhase2Block` 留痕，消息前缀 `P2_NO_MATERIAL`；注意那是**消息文本前缀**，不是 `ReasonCode`），
    /// 于是 `PhaseFiveCompression` 的入参里没有这条需求的任何 Task
    /// ⇒ 它的 `PHASE5_MATERIAL_INSUFFICIENT` 分支**根本进不去**。
    /// 断言「不出现」这条，才是对「Phase5 未排出口欠」这一措辞的正面反驳。
    /// </summary>
    [Fact]
    public async Task T1_缺料需求在Phase2即被拦下_Phase5缺料出口从未触发()
    {
        var result = await new FiniteCapacitySolver().SolveAsync(
            Build(new[] { (Seq: 1L, Qty: 6m) }, supply: new[] { (Seq: 1L, Qty: 1m) }));

        Assert.Empty(result.FinalTasks);

        // 关键是这两条：Phase5 那两个缺料分支的原因码一个都不该出现（因为它一次也没进去）。
        Assert.DoesNotContain(result.SolveTraceNotes,
            n => n.ReasonCode == "PHASE5_MATERIAL_INSUFFICIENT");
        Assert.DoesNotContain(result.SolveTraceNotes,
            n => n.ReasonCode == "PHASE5_MATERIAL_INSUFFICIENT_FLOOR");

        // 而且 Phase4 的缺料原因码是**这一次**报出来的 —— 证明「缺料」这件事在 Phase4 已经说完，
        // 轮不到 Phase5。若哪天 Phase4 这条没了而 Phase5 那条出现了，说明流水线形状变了。
        Assert.Contains(result.SolveTraceNotes,
            n => n.ReasonCode == "PHASE4_MATERIAL_INSUFFICIENT");
    }

    // ════════════════════════════════════════════════════════════════════
    // T2 / T3：真正可达的形状 —— 共用 AllocationSequence
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 两条需求各要 6，**共用**一条 `AllocationSequence=1`，那条序列上只有 10。
    /// 6 ≤ 10 ⇒ 每条需求**各自**都判「供给充足」正如期排下；合计 12 &gt; 10 ⇒ 事实超耗 2。
    ///
    /// <para>本用例断言的就是**当前**这个结果（两个都排下）。它一旦变红，说明有人开始按
    /// 「同序列扣减」改口径了 —— 那时本用例应改成「第二条未排 + 原因」，
    /// 而不是把断言删掉。</para>
    /// </summary>
    [Fact]
    public async Task T2_两需求共用同一AllocationSequence_供给不足仍双双排下_钉住现状()
    {
        const decimal supplyQty = 10m;
        const decimal perDemandQty = 6m;

        var result = await new FiniteCapacitySolver().SolveAsync(
            Build(
                new[] { (Seq: 1L, Qty: perDemandQty), (Seq: 1L, Qty: perDemandQty) },
                supply: new[] { (Seq: 1L, Qty: supplyQty) }));

        Assert.True(result.Success, result.ErrorMessage);

        // 超耗是算术事实，不是推断：2 × 6 = 12 > 10。
        Assert.True(perDemandQty * 2 > supplyQty,
            "夹具本身就不构成超耗 ⇒ 本用例没在测超耗。");

        Assert.Equal(2, result.FinalTasks.Count);
        Assert.DoesNotContain(result.SolveTraceNotes,
            n => n.ReasonCode == "PHASE4_MATERIAL_INSUFFICIENT"
              || n.ReasonCode == "PHASE5_MATERIAL_INSUFFICIENT"
              || n.ReasonCode == "PHASE5_MATERIAL_INSUFFICIENT_FLOOR");
    }

    /// <summary>
    /// T2 的对照：同样两条需求、同样各 6，只把 `AllocationSequence` 拆成 1 / 2，各供 10。
    /// ⇒ 两条都排下是**合法**的。
    /// 与 T2 合起来才能说明：T2 的「双双排下」不是夹具凑的，差别**只在**序列是否共用。
    /// </summary>
    [Fact]
    public async Task T3_对照_两需求各用独立AllocationSequence_全排合法()
    {
        var result = await new FiniteCapacitySolver().SolveAsync(
            Build(
                new[] { (Seq: 1L, Qty: 6m), (Seq: 2L, Qty: 6m) },
                supply: new[] { (Seq: 1L, Qty: 10m), (Seq: 2L, Qty: 10m) }));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.FinalTasks.Count);
        Assert.DoesNotContain(result.SolveTraceNotes, n =>
            n.ReasonCode.StartsWith("PHASE4_MATERIAL", StringComparison.Ordinal)
            || n.ReasonCode.StartsWith("PHASE5_MATERIAL", StringComparison.Ordinal));
    }

    // ════════════════════════════════════════════════════════════════════
    // 夹具
    // ════════════════════════════════════════════════════════════════════

    /// <param name="demands">每条需求 = (它的 AllocationSequence, 它的 NetOutputQty)。</param>
    /// <param name="supply">每条供给段 = (所属 AllocationSequence, 数量)。</param>
    private static DomainSolveRequest Build(
        IReadOnlyList<(long Seq, decimal Qty)> demands,
        IReadOnlyList<(long Seq, decimal Qty)> supply)
        => new()
        {
            PlanVersionId = 1,
            ScheduleRunId = 42,
            DomainKey = "DOMAIN",
            PlanningStart = Day,
            PlanningEnd = Day.AddDays(30),

            LogicalProductionDemands = demands
                .Select((d, i) => new LogicalProductionDemand
                {
                    LogicalDemandKey = $"D{i + 1}",
                    PlanVersionId = 1L,
                    DomainKey = "DOMAIN",
                    AllocationSequence = d.Seq,
                    DemandKey = $"D{i + 1}",
                    MaterialId = MaterialId,
                    FactoryId = FactoryId,
                    StartStageCode = Stage1,
                    NetOutputQty = d.Qty,
                    PlannedProcessQty = d.Qty,
                    RequiredAvailableTime = Day.AddDays(20),
                    DemandSequence = i + 1,
                    QuantityAuthority = QuantityAuthorityValues.None
                })
                .ToList(),

            RoutingOperations = new List<RoutingOperation>
            {
                new()
                {
                    MaterialId = MaterialId, ProductionDepartmentId = DeptId,
                    RouteCode = "DEFAULT",
                    OperationCode = "OP10", StageCode = Stage1,
                    StandardDuration = 60m,          // 源事实：秒 ⇒ 1 分钟/件
                    OperationPlanningMode = "FINITE_RESOURCE"
                }
            },

            OperationResourceEligibility = new List<OperationResourceEligibility>
            {
                new()
                {
                    MaterialId = MaterialId, ProductionDepartmentId = DeptId,
                    RouteCode = "DEFAULT",
                    OperationCode = "OP10", ResourceId = ResourceId,
                    Priority = 1, CapacityFactor = 1m
                }
            },

            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = MaterialId, StageCode = Stage1, ProductionDepartmentId = DeptId }
            },

            Resources = new List<ResourceDefinition>
            {
                new() { ResourceId = ResourceId, ResourceCode = "R1", FactoryCode = "F1", Capacity = 1m }
            },

            CalendarSlots = new List<ResourceCalendarSlot>
            {
                new() { ResourceId = ResourceId, Start = Day, End = Day.AddDays(29), IsAvailable = true }
            },

            MaterialConstraints = supply
                .Select(s => new MaterialAvailabilitySlice
                {
                    AllocationSequence = s.Seq,
                    MaterialId = MaterialId,
                    FactoryId = FactoryId,
                    Quantity = s.Qty,
                    AvailableTime = Day
                })
                .ToList(),

            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD",
                    AllowMerge = false,
                    AllowSplit = false
                },
                BatchPolicies = TestBatchPolicy.Permissive(MaterialId, DeptId)
            }
        };
}
