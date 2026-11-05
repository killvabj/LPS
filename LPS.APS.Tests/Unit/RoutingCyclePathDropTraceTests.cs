using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using Xunit;
using RoutingOperation = LPS.APS.Core.Entities.APS.RoutingOperation;
using RoutingDependency = LPS.APS.Core.Entities.APS.RoutingDependency;
using OperationResourceEligibility = LPS.APS.Core.Entities.APS.OperationResourceEligibility;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **候选路径因 Routing 环被剔除，必须逐需求留痕**（1号位 2026-11-02）。
///
/// <para><b>补的是什么缺口</b>：`PhaseTwoInitialScheduler.cs:901-907` 现在遇到环只做两件事 ——
/// 置 `TechnicalFailure`、把正文写进 `_phase2BlockedDetails`。而那份登记**只有 Phase5 对「未排需求」才会读**
/// （`PhaseFiveCompression.cs:315`）。于是：**一条需求如果靠另一条合法候选 Path 排上了**，
/// 它那条被剔掉的 Path 在出口上**一个字都没有** —— 既没有 `SolveTraceNote`，也没有 `ExplanationFact`。
/// 这正是 N24「失败粒度」问题里「需求级不可规划却看不出是哪条路径」的那一半。</para>
///
/// <para><b>本类钉住两件事</b>：</para>
/// <list type="bullet">
///   <item>有环被剔 ⇒ 无论该需求最后排没排上，都有一条 `Key = 需求键` 的 trace；</item>
///   <item>无环 ⇒ **一条都不该有**（防止实现写成「无条件都写」，那样痕迹就失去鉴别力）。</item>
/// </list>
///
/// <para>⚠ 只用 `DomainSolveResult.SolveTraceNotes`（既有对外载体，2号位 已落库），
/// **不新增 Core 字段**、**不新增 ReasonCode 枚举**。</para>
/// </summary>
public class RoutingCyclePathDropTraceTests
{
    private static readonly System.DateTime PlanningStart = new(2026, 11, 1);
    private static readonly System.DateTime PlanningEnd = new(2026, 12, 1);
    private const int DeptId = 100;
    private const string Stage = "STAGE1";
    private const string DemandKey = "DEMAND-CYCLE-1";

    // 被剔路径用的原因码（沿用 Phase2 既有出口名 `P2_ROUTING_CYCLE`，不自造枚举）
    private const string DroppedReasonCode = "P2_ROUTING_CYCLE";

    // ───────────── 一、鉴别性用例：靠另一条路径排上了，被剔路径仍要留痕 ─────────────

    [Fact(DisplayName = "Routing环｜两条候选路径、其中一条有环 ⇒ 需求照排，但被剔路径要留一条逐需求 trace")]
    public async Task DroppedPath_LeavesTrace_EvenWhenDemandIsScheduled()
    {
        var request = BuildRequest(withCyclicPath: true);

        var result = await new FiniteCapacitySolver().SolveAsync(request);

        // ★ 前提：这条需求**确实排上了**（靠无环的 Path 1）。若这里为 0，本用例就变成
        //   「未排需求留痕」——那是 Phase5 既有能力，测不到本缺口，故先钉住前提。
        Assert.True(result.FinalTasks.Count > 0,
            $"夹具应让需求靠 Path 1 排上，实际 FinalTask={result.FinalTasks.Count}；" +
            $"ErrorMessage={result.ErrorMessage}");

        var notes = NotesFor(result, DemandKey);
        Assert.True(notes.Count > 0,
            "需求已排上，但被剔掉的有环路径没有任何 trace —— 这就是本用例要红的缺口。" +
            $"已有 trace：{string.Join(" | ", (result.SolveTraceNotes ?? System.Array.Empty<SolveTraceNote>()).Select(n => $"{n.Key}/{n.ReasonCode}"))}");

        var dropped = notes.Where(n => n.ReasonCode == DroppedReasonCode).ToList();
        Assert.Single(dropped);

        // ★ 鉴别点：trace 必须指向**具体那条 Path**，不能只说「这需求有问题」。
        Assert.Contains("PathId=2", dropped[0].Message);
    }

    // ───────────── 二、控制组：没有环就不能凭空造痕 ─────────────

    [Fact(DisplayName = "Routing环｜只有无环路径 ⇒ 不得出现任何 P2_ROUTING_CYCLE 痕迹")]
    public async Task AcyclicRouting_NoRoutingCycleTrace()
    {
        var request = BuildRequest(withCyclicPath: false);

        var result = await new FiniteCapacitySolver().SolveAsync(request);

        Assert.True(result.FinalTasks.Count > 0, result.ErrorMessage);

        var dropped = NotesFor(result, DemandKey)
            .Where(n => n.ReasonCode == DroppedReasonCode)
            .ToList();

        Assert.Empty(dropped);
    }

    // ════════════════════════ 夹具 ════════════════════════

    private static List<SolveTraceNote> NotesFor(DomainSolveResult result, string demandKey)
        => (result.SolveTraceNotes ?? System.Array.Empty<SolveTraceNote>())
            .Where(n => n.Key == demandKey)
            .ToList();

    /// <summary>
    /// Path 1（`DEFAULT/1`）：`OP01 → OP02`，无环 ⇒ 这条会把需求排上。
    /// Path 2（`DEFAULT/2`）：`OP03 → OP04 → OP03`，有环 ⇒ 拓扑序覆盖不全，被剥掉。
    /// </summary>
    private static DomainSolveRequest BuildRequest(bool withCyclicPath)
    {
        var ops = new List<RoutingOperation>
        {
            Op("OP01", pathId: 1),
            Op("OP02", pathId: 1),
            Op("OP03", pathId: 2),
            Op("OP04", pathId: 2),
        };

        var deps = new List<RoutingDependency>
        {
            Dep("OP01", "OP02", pathId: 1),
        };

        if (withCyclicPath)
        {
            deps.Add(Dep("OP03", "OP04", pathId: 2));
            deps.Add(Dep("OP04", "OP03", pathId: 2));
        }

        var elig = new List<OperationResourceEligibility>();
        foreach (var op in ops)
        {
            elig.Add(new OperationResourceEligibility
            {
                MaterialId = 1,
                ProductionDepartmentId = DeptId,
                RouteCode = op.RouteCode,
                OperationCode = op.OperationCode,
                ResourceId = 1,
                Priority = 1,
                CapacityFactor = 1m
            });
        }

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "DOMAIN",
            PlanningStart = PlanningStart,
            PlanningEnd = PlanningEnd,
            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                new()
                {
                    LogicalDemandKey = DemandKey,
                    PlanVersionId = 1L,
                    DomainKey = "DOMAIN",
                    AllocationSequence = 1,
                    DemandKey = DemandKey,
                    MaterialId = 1,
                    FactoryId = 1,
                    StartStageCode = Stage,
                    // RouteCode / PathId 都不给 ⇒ 走「无固定路径 ⇒ 取全部候选 Path」分支
                    NetOutputQty = 1m,
                    PlannedProcessQty = 1m,
                    RequiredAvailableTime = PlanningEnd,
                    DemandSequence = 1,
                }
            },
            RoutingOperations = ops,
            RoutingDependencies = deps,
            OperationResourceEligibility = elig,
            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = 1, StageCode = Stage, ProductionDepartmentId = DeptId }
            },
            Resources = new List<ResourceDefinition>
            {
                new() { ResourceId = 1, ResourceCode = "R1", FactoryCode = "F1", Capacity = 1m }
            },
            CalendarSlots = new List<ResourceCalendarSlot>
            {
                new()
                {
                    ResourceId = 1,
                    Start = PlanningStart,
                    End = PlanningStart.AddDays(20),
                    IsAvailable = true
                }
            },
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD",
                    AllowMerge = false,
                    AllowSplit = false
                },
                BatchPolicies = TestBatchPolicy.Permissive(new[] { 1 }, DeptId),
                SolverStrategy = new SolverStrategyBlock
                {
                    BottleneckMode = DynamicBottleneckMode.Auto,
                    AnchorResourceCode = null
                }
            }
        };
    }

    private static RoutingOperation Op(string opCode, int pathId) => new()
    {
        MaterialId = 1,
        ProductionDepartmentId = DeptId,
        RouteCode = "DEFAULT",
        PathId = pathId,
        OperationCode = opCode,
        StageCode = Stage,
        StandardDuration = 60m * 60m,   // 秒（PhaseOneConstraintBuilder 里 /60 折成分钟）
        SetupTime = 0m
    };

    private static RoutingDependency Dep(string from, string to, int pathId) => new()
    {
        MaterialId = 1,
        ProductionDepartmentId = DeptId,
        RouteCode = "DEFAULT",
        PathId = pathId,
        FromOperationCode = from,
        ToOperationCode = to,
        DependencyType = "ES",
        LagTime = 0m,
        IsActive = true
    };
}
