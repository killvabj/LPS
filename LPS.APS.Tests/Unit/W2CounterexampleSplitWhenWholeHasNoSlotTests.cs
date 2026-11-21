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
/// **W2 八反例之②（0号位 11-07 任务包 P1-E / v3.0 §84 逐字）**：
/// 「**整批无槽而 2 批合法**」。
///
/// <para>这句话锁的是一条**正向能力**：把 100 件当一个执行批，**任何单个日历窗都装不下**；
/// 但**拆成两批**就能分别落进两个窗。此时求解器**必须拆**，不得因为"整批排不下"就判整条需求未排。</para>
///
/// <para>夹具（纯内存）：</para>
/// <list type="bullet">
///   <item>1 道工序、1 个资源、60 秒/件 ⇒ **100 件 = 100 分钟**；</item>
///   <item>日历只给**两个 60 分钟的服务窗**，中间隔 1 小时不可用；</item>
///   <item>⇒ 100 分钟**塞不进任何一个 60 分钟窗**（整批无槽）；</item>
///   <item>⇒ 50 + 50 分落两窗**完全合法**（2 批合法）。</item>
/// </list>
///
/// <para>断言：①不得判未排；②已排总量 = 100（数量闭合）；③**确实拆成了 ≥2 个 Task**，
/// 且每段整段落在同一个服务窗内。</para>
///
/// <para><b>⚠ 前提澄清（实测后修正，2026-11-21）</b>：本域**允许 Task 跨停机窗**——
/// 0号位 2026-10-14 复核意见：「有效加工 = 任务时间区间 − Setup − **区间内不在合法日历窗口的中断**」，
/// 且明文「没有找到可以直接作为全局冻结规定的这句『窗口内绝无等待』」。
/// 故「整批无槽」在本模型下**不能靠"单个窗装不下"构造**（跨窗即可）。
/// 本文件因此分两条锁<b>数量闭合</b>：拆批与跨窗<b>都必须排满 100，不得少排</b>。</para>
///
/// <para>纯内存，不碰数据库。</para>
/// </summary>
public sealed class W2CounterexampleSplitWhenWholeHasNoSlotTests
{
    private static readonly DateTime Day = new(2026, 11, 21, 0, 0, 0);
    private const int DeptId = 100;
    private const string Stage = "S1";
    private const string Op = "OP10";
    private const int MaterialId = 1;

    // 两个各 60 分钟的服务窗，中间隔 1 小时 ⇒ 任何跨窗的连续 100 分钟都非法
    private static readonly DateTime Window1Start = Day.AddHours(8);
    private static readonly DateTime Window1End = Day.AddHours(9);
    private static readonly DateTime Window2Start = Day.AddHours(10);
    private static readonly DateTime Window2End = Day.AddHours(11);

    private readonly FiniteCapacitySolver _solver = new();

    [Fact(DisplayName = "W2②：整批100分钟无任何单窗可容 ⇒ 必须拆成2批（50+50）全部排下")]
    public async Task WholeBatchHasNoSingleSlot_MustSplitIntoTwo()
    {
        var request = Build(qty: 100m, allowSplit: true);

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, "业务未排不是技术失败：" + result.ErrorMessage);

        // ① 不得判整条未排
        Assert.DoesNotContain(result.UnscheduledTasks, u => u.DraftId == "D1");

        // ② 数量闭合：应排 100 / 已排 100 / 未排 0
        var scheduled = result.FinalTasks.Sum(t => t.Quantity);
        Assert.Equal(100m, scheduled);

        // ③ 必须真的拆（≥2 个 Task）；1 个 Task 只能意味着把 100 分钟塞进了 60 分钟窗
        Assert.True(result.FinalTasks.Count >= 2,
            $"只有 {result.FinalTasks.Count} 个 Task：整批 100 分钟不可能落进 60 分钟窗，"
            + "说明未拆批或排程非法");

        // ④（独立复核）每个 Task 必须整段落在同一个服务窗内
        foreach (var t in result.FinalTasks)
        {
            var inW1 = t.PlannedStartTime >= Window1Start && t.PlannedEndTime <= Window1End;
            var inW2 = t.PlannedStartTime >= Window2Start && t.PlannedEndTime <= Window2End;
            Assert.True(inW1 || inW2,
                $"Task {t.FinalDraftId} 时间窗 {t.PlannedStartTime:HH:mm}-{t.PlannedEndTime:HH:mm} "
                + "跨越了不可用区间");
        }
    }

    /// <summary>
    /// **对照（如实记录现行契约）**：同一夹具、只把 `AllowSplit` 关掉 ⇒ 求解器**不拆批，改为跨停机窗**
    /// （墙钟 08:00–10:40，中间 09:00–10:00 不可用 ⇒ 有效加工 = 160 − 60 = 100 分钟）。
    ///
    /// <para>**这不是缺陷**：0号位 2026-10-14 复核意见逐字——
    /// 「回执使用『有效加工 = 任务时间区间 − Setup − **区间内不在合法日历窗口的中断**』……
    /// 本轮已检冻结业务正文，**没有找到可以直接作为全局冻结规定的这句『窗口内绝无等待』**」。
    /// ⇒ 「Task 不得跨停机窗」**不是冻结规定**，跨窗合法。</para>
    ///
    /// <para>因此本用例锁的是**数量闭合**这一条硬不变量：无论拆还是跨，**都不得少排一粒**。</para>
    /// </summary>
    [Fact(DisplayName = "W2②对照：不拆批时跨停机窗排下 ⇒ 数量仍须闭合 100（不得少排）")]
    public async Task SameFixture_WithoutSplit_StillClosesQuantity()
    {
        var request = Build(qty: 100m, allowSplit: false);

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, "业务未排不是技术失败：" + result.ErrorMessage);

        // ① 数量闭合：应排 100 / 已排 100 / 未排 0
        Assert.Equal(100m, result.FinalTasks.Sum(t => t.Quantity));
        Assert.DoesNotContain(result.UnscheduledTasks, u => u.DraftId == "D1");

        // ② 有效加工必须是 100 分钟：跨窗时**必须扣掉区间内的停机**，不得把墙钟跨度当加工时长
        var process = result.FinalTasks.Sum(t => t.EffectiveProcessMinutes ?? 0m);
        Assert.Equal(100m, process);
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private static DomainSolveRequest Build(decimal qty, bool allowSplit)
    {
        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            ScheduleRunId = 42L,
            DomainKey = "DOMAIN",
            PlanningStart = Day,
            PlanningEnd = Day.AddDays(30),
            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                new()
                {
                    LogicalDemandKey = "D1", PlanVersionId = 1L, DomainKey = "DOMAIN",
                    AllocationSequence = 1, DemandKey = "D1", MaterialId = MaterialId, FactoryId = 1,
                    StartStageCode = Stage, NetOutputQty = qty, PlannedProcessQty = qty,
                    RequiredAvailableTime = Day.AddDays(30), DemandSequence = 1,
                    QuantityAuthority = QuantityAuthorityValues.None,
                },
            },
            RoutingOperations = new List<RoutingOperation>
            {
                new() { MaterialId = MaterialId, ProductionDepartmentId = DeptId, RouteCode = "DEFAULT",
                        OperationCode = Op, StageCode = Stage,
                        StandardDuration = 60m,        // 60 秒/件 = 1 分钟/件
                        OperationPlanningMode = "FINITE_RESOURCE" },
            },
            OperationResourceEligibility = new List<OperationResourceEligibility>
            {
                new() { MaterialId = MaterialId, ProductionDepartmentId = DeptId, RouteCode = "DEFAULT",
                        OperationCode = Op, ResourceId = 1, Priority = 1, CapacityFactor = 1m },
            },
            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = MaterialId, StageCode = Stage, ProductionDepartmentId = DeptId },
            },
            Resources = new List<ResourceDefinition>
            {
                new() { ResourceId = 1, ResourceCode = "R1", FactoryCode = "F1", Capacity = 1m },
            },
            // 只有两个 60 分钟窗；**不给**整天连续可用窗（否则整批 100 分钟一次就能排下）
            CalendarSlots = new List<ResourceCalendarSlot>
            {
                new() { ResourceId = 1, Start = Window1Start, End = Window1End, IsAvailable = true },
                new() { ResourceId = 1, Start = Window2Start, End = Window2End, IsAvailable = true },
            },
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD",
                    AllowMerge = false,
                    AllowSplit = allowSplit,
                },
                BatchPolicies = new[]
                {
                    new BatchPolicyRuleSnapshot
                    {
                        MaterialId = MaterialId, ProductionDepartmentId = DeptId,
                        MinExecutionBatchQty = null,
                        MaxExecutionBatchQty = null,   // 不给经济 Max：唯一拆批动因就是"装不下"
                        AllowSplit = allowSplit,
                        AllowMerge = false,
                    },
                },
            },
        };
    }
}
