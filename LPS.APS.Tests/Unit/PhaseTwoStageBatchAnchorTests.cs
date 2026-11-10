using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Core.Dto;
using RoutingOperation = LPS.APS.Core.Entities.APS.RoutingOperation;
using RoutingDependency = LPS.APS.Core.Entities.APS.RoutingDependency;
using OperationResourceEligibility = LPS.APS.Core.Entities.APS.OperationResourceEligibility;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// A4（0号位 2026-11-10《Batch_Code_Review_and_Action_Plan v1.0》§4-A4 / §3-R07）：
/// **多 Operation Stage 执行批的锚点擂台与甲方案早停**。
///
/// <para><b>为什么必须另建一组</b>：0号位 R07 逐字指出「新测试的 E5 夹具走**单 Op 分支**，
///   根本不覆盖 `PreferStageMerge` 的物理可行/比较/早停。不能用 `PI_FIRST &lt;= PI_SORT` 这个弱不等式
///   或单 Op 两模式相同证明多 Op 正确」。本文件的用例都走 `operations.Count &gt; 1` 的真实路径。</para>
///
/// <para><b>甲方案（唯一有效口径）</b>：首个通过**完整硬约束**的物理可行锚批，完成与独立排程的比较后
///   **立即停止**；**即使比较输给独立方案也停止**。</para>
///
/// <para><b>三个必须分开的计数</b>：`MergeTrialStarted`（试排几次）／`MergePhysicalFeasible`
///   （其中几次物理可行）／`MergeFinallyAdopted`（最后真的采纳几次）。</para>
///
/// <para>⚠ 纯内存夹具，零 DB；不改任何生产判定，只测现状。</para>
/// </summary>
[Collection("P2AnchorModeSerial")]
public class PhaseTwoStageBatchAnchorTests
{
    private static readonly DateTime Day = new(2026, 9, 1, 0, 0, 0);
    private static readonly DateTime PlanningStart = Day;
    private static readonly DateTime PlanningEnd = Day.AddDays(90);

    private const int MaterialId = 1;
    private const int DeptId = 100;
    private const string Pi = "P1";

    private readonly FiniteCapacitySolver _solver = new();

    private readonly record struct OpSpec(
        string StageCode, string OpCode, int ResourceId, decimal Minutes,
        IReadOnlyList<(int ResourceId, decimal CapacityFactor)>? Alternatives = null);

    private readonly record struct PathSpec(
        string RouteCode, int PathId, IReadOnlyList<OpSpec> Ops, IReadOnlyList<(string From, string To)> Deps);

    private readonly record struct DemandSpec(
        string Key, long Seq, decimal Qty, DateTime? Due,
        string RouteCode, int PathId);

    /// <summary>三工序串行路径（OP10@STAGE1 → OP20@STAGE2 → OP30@STAGE3），每道工序单一资源。</summary>
    private static PathSpec ThreeOpPath(
        string routeCode, int pathId, int r1, int r2, int r3, decimal minutesPerUnit)
        => new(
            routeCode, pathId,
            new[]
            {
                new OpSpec("STAGE1", "OP10", r1, minutesPerUnit),
                new OpSpec("STAGE2", "OP20", r2, minutesPerUnit),
                new OpSpec("STAGE3", "OP30", r3, minutesPerUnit)
            },
            new[] { ("OP10", "OP20"), ("OP20", "OP30") });

    /// <summary>
    /// 同产品连续（M→M）显式换型规则 —— 让「独立排程」真实付出换型成本，
    /// 而「合批」因目标批被移出、资源上无前产品（InitialState）而不付
    /// ⇒ 冻结目标第 ④ 层（Setup 总量）与第 ③ 层（完成更早）同时指向合批。
    /// </summary>
    private static IReadOnlyList<SetupTransitionRuleSnapshot> SameProductSetup(
        decimal minutes, params int[] resourceIds)
        => resourceIds
            .Select(rid => new SetupTransitionRuleSnapshot
            {
                ProductionDepartmentId = DeptId,
                StageCode = $"STAGE{rid}",
                OperationCode = $"OP{rid}0",
                ResourceId = rid,
                FromMaterialId = MaterialId,
                ToMaterialId = MaterialId,
                RuleType = "EXACT",
                SetupMinutes = minutes
            })
            .ToArray();

    /// <summary>
    /// 三工序串行路径，**首工序单一资源**、**末两道工序工时为 0** ⇒ 合批结束时刻 = 首工序结束时刻。
    /// 用途：A4-3 的交期边界要落在「首工序」上才有精确的分钟级控制。
    /// </summary>
    private static PathSpec ZeroTailSinglePath(
        string routeCode, int pathId, int r1, int r2, int r3, decimal minutesPerUnit)
        => new(
            routeCode, pathId,
            new[]
            {
                new OpSpec("STAGE1", "OP10", r1, minutesPerUnit),
                new OpSpec("STAGE2", "OP20", r2, 0m),
                new OpSpec("STAGE3", "OP30", r3, 0m)
            },
            new[] { ("OP10", "OP20"), ("OP20", "OP30") });

    /// <summary>
    /// 三工序串行路径，**首工序三台合法设备**（优先级 1/2/3）、**末两道工序工时为 0**。
    /// 用途：A4-1 —— 用三台设备的**日历窗宽窄**精确控制「合批后能不能装下」。
    /// </summary>
    private static PathSpec ThreeAltZeroTailPath(
        string routeCode, int pathId, int primary, int alt1, int alt2,
        decimal minutesPerUnit, int r2, int r3)
        => new(
            routeCode, pathId,
            new[]
            {
                new OpSpec("STAGE1", "OP10", primary, minutesPerUnit,
                    new[] { (alt1, 1m), (alt2, 1m) }),
                new OpSpec("STAGE2", "OP20", r2, 0m),
                new OpSpec("STAGE3", "OP30", r3, 0m)
            },
            new[] { ("OP10", "OP20"), ("OP20", "OP30") });

    /// <summary>
    /// 三工序串行路径，**首工序两台合法替代设备**、**末两道工序工时为 0**。
    ///
    /// <para>为什么这样造：末工序 0 分钟 ⇒ 末工序完成时刻 = `OP10` 完成时刻 ⇒ 冻结目标 ③（更早可行完成）
    ///   可以把「合批」与「独立」直接比出来；无换型规则 ⇒ ④ 层恒平；交期远 ⇒ ② 履约恒平。
    ///   首工序两台设备 ⇒ 先到的两条需求**各自独立成批**（各占一台），从而真的存在**两个锚批**。</para>
    /// </summary>
    private static PathSpec DivergentZeroTailPath(
        string routeCode, int pathId, int firstResourceId, int firstAltResourceId,
        decimal minutesPerUnit, int r2, int r3)
        => new(
            routeCode, pathId,
            new[]
            {
                new OpSpec("STAGE1", "OP10", firstResourceId, minutesPerUnit,
                    new[] { (firstAltResourceId, 1m) }),
                new OpSpec("STAGE2", "OP20", r2, 0m),
                new OpSpec("STAGE3", "OP30", r3, 0m)
            },
            new[] { ("OP10", "OP20"), ("OP20", "OP30") });

    private static DomainSolveRequest Build(
        IReadOnlyList<PathSpec> paths,
        IReadOnlyList<DemandSpec> demands,
        IReadOnlyList<(int ResourceId, DateTime Start, DateTime End)> calendar,
        IReadOnlyList<SetupTransitionRuleSnapshot>? setupRules = null)
    {
        var routingOps = new List<RoutingOperation>();
        var elig = new List<OperationResourceEligibility>();
        var deps = new List<RoutingDependency>();
        var stageDepts = new List<MaterialStageDepartmentContextDto>();

        foreach (var p in paths)
        {
            foreach (var op in p.Ops)
            {
                routingOps.Add(new RoutingOperation
                {
                    MaterialId = MaterialId, ProductionDepartmentId = DeptId,
                    RouteCode = p.RouteCode, PathId = p.PathId,
                    OperationCode = op.OpCode, StageCode = op.StageCode,
                    StandardDuration = op.Minutes * 60m, OperationPlanningMode = "FINITE_RESOURCE"
                });
                elig.Add(new OperationResourceEligibility
                {
                    MaterialId = MaterialId, ProductionDepartmentId = DeptId,
                    RouteCode = p.RouteCode, PathId = p.PathId,
                    OperationCode = op.OpCode, ResourceId = op.ResourceId,
                    Priority = 1, CapacityFactor = 1m
                });
                if (op.Alternatives is not null)
                {
                    var altPriority = 2;
                    foreach (var (altResourceId, altCapacityFactor) in op.Alternatives)
                    {
                        elig.Add(new OperationResourceEligibility
                        {
                            MaterialId = MaterialId, ProductionDepartmentId = DeptId,
                            RouteCode = p.RouteCode, PathId = p.PathId,
                            OperationCode = op.OpCode, ResourceId = altResourceId,
                            Priority = altPriority++, CapacityFactor = altCapacityFactor
                        });
                    }
                }
                if (stageDepts.All(s => !string.Equals(s.StageCode, op.StageCode, StringComparison.Ordinal)))
                {
                    stageDepts.Add(new MaterialStageDepartmentContextDto
                    {
                        MaterialId = MaterialId, StageCode = op.StageCode, ProductionDepartmentId = DeptId
                    });
                }
            }

            foreach (var (from, to) in p.Deps)
            {
                deps.Add(new RoutingDependency
                {
                    MaterialId = MaterialId, ProductionDepartmentId = DeptId,
                    RouteCode = p.RouteCode, PathId = p.PathId,
                    FromOperationCode = from, ToOperationCode = to,
                    DependencyType = "ES", LagTime = 0m, IsActive = true
                });
            }
        }

        var resources = paths
            .SelectMany(p => p.Ops.SelectMany(o => o.Alternatives is null
                ? new[] { o.ResourceId }
                : new[] { o.ResourceId }.Concat(o.Alternatives.Select(a => a.ResourceId))))
            .Distinct()
            .Select(rid => new ResourceDefinition { ResourceId = rid, ResourceCode = $"R{rid}", FactoryCode = "F1", Capacity = 1m })
            .ToList();

        var slots = calendar
            .Select(c => new ResourceCalendarSlot { ResourceId = c.ResourceId, Start = c.Start, End = c.End, IsAvailable = true })
            .ToList();

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            DomainKey = "DOMAIN",
            PlanningStart = PlanningStart,
            PlanningEnd = PlanningEnd,
            LogicalProductionDemands = demands.Select(d => new LogicalProductionDemand
            {
                LogicalDemandKey = d.Key, PlanVersionId = 1L, DomainKey = "DOMAIN",
                AllocationSequence = d.Seq, DemandKey = d.Key, MaterialId = MaterialId, FactoryId = 1,
                StartStageCode = "STAGE1",
                NetOutputQty = d.Qty, PlannedProcessQty = d.Qty,
                RequiredAvailableTime = d.Due ?? PlanningStart.AddDays(20),
                DemandSequence = (int)d.Seq,
                RouteCode = d.RouteCode, PathId = d.PathId,
                // A4 要走多 Op 真实路径，而甲方案早停只在 PI_FIRST 生效 ⇒ 必须有可信同 PI 身份。
                ProductionInstructionNo = Pi,
                IsRootDemand = true
            }).ToList(),
            PiRemainingFacts = new List<PiRemainingFact>
            {
                new()
                {
                    ProductionInstructionNo = Pi, MaterialId = MaterialId, FactoryId = 1,
                    OrderType = string.Empty, PiQuantity = 100m, PiReceivedQty = 0m, PiRemainingQty = 100m
                }
            },
            // 多工序需求每个 Stage 都要有基线，否则 PI 门禁会在第二道工序上 Fail Closed。
            PiStageQuantityBaselines = new[]
            {
                "STAGE1", "STAGE2", "STAGE3"
            }.Select(s => new PiStageQuantityBaseline
            {
                ProductionInstructionNo = Pi, MaterialId = MaterialId, StageCode = s,
                StageFreeEligibleQty = 100m, AlreadyCommittedQty = 0m
            }).ToList(),
            RoutingOperations = routingOps,
            RoutingDependencies = deps,
            OperationResourceEligibility = elig,
            MaterialStageDepartmentContexts = stageDepts,
            ExecutionConstraints = Array.Empty<ExecutionConstraint>(),
            Resources = resources,
            CalendarSlots = slots,
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD",
                    AllowMerge = true,
                    AllowSplit = false
                },
                BatchPolicies = TestBatchPolicy.Permissive(MaterialId, DeptId, allowMerge: true, allowSplit: false),
                SetupTransitionRules = setupRules ?? Array.Empty<SetupTransitionRuleSnapshot>()
            }
        };
    }

    private readonly record struct MergeCounts(
        long TrialStarted, long PhysicalFeasible, long HardRejected,
        long ComparedWins, long ComparedRejected, long FinallyAdopted);

    private async Task<(DomainSolveResult Result, MergeCounts Counts)> SolveWithCounters(
        DomainSolveRequest request, string? mode)
    {
        PhaseTwoInitialScheduler.OverrideAnchorModeForTest(mode);
        try
        {
            using var scope = SolverDiagnostics.BeginScope();
            var result = await _solver.SolveAsync(request);
            var c = scope.Counters;
            return (result, new MergeCounts(
                c.MergeTrialStarted, c.MergePhysicalFeasible, c.MergeHardRejectedCalendar,
                c.MergeComparedWins, c.MergeComparedRejected, c.MergeFinallyAdopted));
        }
        finally
        {
            PhaseTwoInitialScheduler.OverrideAnchorModeForTest(null);
        }
    }

    /// <summary>
    /// **A4-2（最核心）**：两锚都物理可行，第一个判**独立胜出** ⇒ 甲方案要求**立即停止**，不再试第二个锚批。
    ///
    /// <para><b>夹具推演</b>（OP10 = 1 分钟/件，末两道工序 0 分钟，OP10 有设备 1/2）：</para>
    /// <list type="bullet">
    ///   <item>A(30) 先排 ⇒ 设备1 的 [0,30]，成批①。</item>
    ///   <item>B(20) 排 ⇒ 试批①：合批 50 件 ⇒ [0,50]；独立：设备2 [0,20]。
    ///         ③ 层「更早可行完成」20 &lt; 50 ⇒ **独立胜** ⇒ B 独立成批②。</item>
    ///   <item>C(10) 排 ⇒ 候选 {批①, 批②}。试批①：合批 40 件 ⇒ [0,40]；独立 [20,30] ⇒ 30。
    ///         ⇒ **独立胜**。甲方案：**到此停止**，不再试批②。</item>
    /// </list>
    ///
    /// <para><b>判据</b>：`MergeTrialStarted` 必须 = **2**（B 一次 + C 一次）。
    ///   若早停失效（退回「必须 mergeWins 才停」），C 会再试批② ⇒ 变 **3**。</para>
    /// </summary>
    [Fact(DisplayName = "A4-2_甲方案_首锚物理可行但比较落败_必须停止不再试第二锚")]
    public async Task A4_2_FirstFeasibleAnchorLoses_StillStops()
    {
        var request = Build(
            paths: new[] { DivergentZeroTailPath("RT", 1, 1, 2, 1m, 3, 4) },
            demands: new[]
            {
                new DemandSpec("A", 1, 30m, Day.AddDays(200), "RT", 1),
                new DemandSpec("B", 2, 20m, Day.AddDays(200), "RT", 1),
                new DemandSpec("C", 3, 10m, Day.AddDays(200), "RT", 1)
            },
            calendar: new[]
            {
                (1, Day, Day.AddDays(120)),
                (2, Day, Day.AddDays(120)),
                (3, Day, Day.AddDays(120)),
                (4, Day, Day.AddDays(120))
            });

        var (result, counters) = await SolveWithCounters(request, "PI_FIRST");

        Assert.True(result.Success, result.ErrorMessage);

        var shape = string.Join(" | ", result.FinalTasks
            .OrderBy(t => t.PlannedStartTime)
            .ThenBy(t => t.OperationCode)
            .Select(t => $"{t.OperationCode}@{t.PlannedStartTime:MM-dd HH:mm}-{t.PlannedEndTime:HH:mm} q={t.Quantity}"));

        // ① 三个计数必须**互不相同**，且都符合推演。
        Assert.True(counters.PhysicalFeasible == 2,
            $"物理可行应为 2 次（B 试批①、C 试批①），实得 {counters.PhysicalFeasible}。"
            + $"Trial={counters.TrialStarted} ComparedWins={counters.ComparedWins}。现场：{shape}");

        Assert.True(counters.TrialStarted == 2,
            $"甲方案要求首锚比较后立即停止 ⇒ 试排总数应为 2（B 一次 + C 一次）；实得 {counters.TrialStarted}。"
            + $"若为 3 说明 C 又试了第二个锚批，早停失效。现场：{shape}");

        Assert.True(counters.ComparedWins == 0,
            $"本夹具两次比较都判独立胜出，不应有胜出次数；实得 {counters.ComparedWins}");
        Assert.True(counters.FinallyAdopted == 0,
            $"全部判独立胜出 ⇒ 最终采纳合批应为 0；实得 {counters.FinallyAdopted}");

        // ② 结果形状：三条需求各自独立成批 ⇒ 3 个批键 × 3 道工序 = 9 条 Task。
        Assert.True(result.FinalTasks.Count == 9,
            $"期望 9 条 Task（3 批 × 3 工序），实得 {result.FinalTasks.Count}。现场：{shape}");
        Assert.Equal(3, result.FinalTasks
            .Select(t => t.ExecutionBatchDraftKey)
            .Where(k => !string.IsNullOrEmpty(k))
            .Distinct(StringComparer.Ordinal)
            .Count());
    }

    /// <summary>
    /// **A4-3**：锚批已吸收**第三个历史成员**，该成员交期不满足 ⇒ **整锚拒绝、完整回滚**。
    ///
    /// <para><b>三步时序</b>（1 分钟/件，三道工序，省换型规则让合批真实胜出）：</para>
    /// <list type="bullet">
    ///   <item>A(30) 先排 ⇒ 批①，[0,30]。</item>
    ///   <item>X(20) 交期 Day+55min ⇒ 合批到批①：50 件 ⇒ [0,50] ≤ 55min，
    ///         且省 3×60 分钟 Setup ⇒ **合批胜出并被采纳** ⇒ 批① 变成 50 件、成员 {A, X}。</item>
    ///   <item>C(10) 交期宽松 ⇒ 合批到批①：60 件 ⇒ [0,60]。C 自己不违约、A 也不违约，
    ///         **但 X 的交期 55min 被顶穿** ⇒ 整锚必须拒绝、回滚，C 走独立排程。</item>
    /// </list>
    ///
    /// <para><b>判据</b>：`HardRejected ≥ 1`，且最终 = 6 条 Task（批① 50 件 × 3 工序 + C 独立 10 件 × 3 工序）。
    ///   去掉成员交期检查后，C 会并进批① ⇒ 3 条 qty 60 —— 本用例**红**。</para>
    /// </summary>
    [Fact(DisplayName = "A4-3_锚批历史成员交期被顶穿_整锚拒绝并回滚")]
    public async Task A4_3_ThirdMemberDueBreached_WholeAnchorRejected()
    {
        var request = Build(
            paths: new[] { ZeroTailSinglePath("RT", 1, 1, 2, 3, 1m) },
            demands: new[]
            {
                new DemandSpec("A", 1, 30m, Day.AddDays(200), "RT", 1),
                // X 的交期卡在「A+X（50 分钟）装得下、A+X+C（60 分钟）装不下」之间
                new DemandSpec("X", 2, 20m, Day.AddMinutes(55), "RT", 1),
                new DemandSpec("C", 3, 10m, Day.AddDays(200), "RT", 1)
            },
            calendar: new[]
            {
                (1, Day, Day.AddDays(120)),
                (2, Day, Day.AddDays(120)),
                (3, Day, Day.AddDays(120))
            },
            // 首工序同产品换型 60 分钟 ⇒ 独立排程的 X 付 Setup、合批不付 ⇒ 合批真实胜出。
            setupRules: new[]
            {
                new SetupTransitionRuleSnapshot
                {
                    ProductionDepartmentId = DeptId,
                    StageCode = "STAGE1",
                    OperationCode = "OP10",
                    ResourceId = 1,
                    FromMaterialId = MaterialId,
                    ToMaterialId = MaterialId,
                    RuleType = "EXACT",
                    SetupMinutes = 60m
                }
            });

        var (result, counters) = await SolveWithCounters(request, "PI_FIRST");

        Assert.True(result.Success, result.ErrorMessage);

        var shape = string.Join(" | ", result.FinalTasks
            .OrderBy(t => t.OperationCode)
            .ThenBy(t => t.PlannedStartTime)
            .Select(t => $"{t.OperationCode}@{t.PlannedStartTime:HH:mm}-{t.PlannedEndTime:HH:mm} q={t.Quantity} key={t.ExecutionBatchDraftKey}"));

        // ① C 合批到批①必须被拒（否则第三次并入会把 X 推过交期）。
        Assert.True(counters.HardRejected >= 1,
            $"C 合批到已含 {{A,X}} 的锚批应被**成员交期**拒绝；实得 HardRejected={counters.HardRejected}。现场：{shape}");

        // ② 批① 保持 50 件（A30 + X20），C 独立成 10 件 ⇒ 6 条 Task。
        Assert.True(result.FinalTasks.Count == 6,
            $"期望 6 条 Task（批① 50×3 + C 独立 10×3），实得 {result.FinalTasks.Count}。"
            + $"若是 3 条且 qty=60，说明第三次并入未被拦住。现场：{shape}");

        var fifty = result.FinalTasks.Where(t => t.Quantity == 50m).ToList();
        var ten = result.FinalTasks.Where(t => t.Quantity == 10m).ToList();
        Assert.True(fifty.Count == 3, $"批① 应有 3 道工序 × qty50，实得 {fifty.Count}。现场：{shape}");
        Assert.True(ten.Count == 3, $"C 应独立排成 3 道工序 × qty10，实得 {ten.Count}。现场：{shape}");

        // ③ 两个不同的批键（批① 与 C 的独立批）。
        Assert.Equal(2, result.FinalTasks
            .Select(t => t.ExecutionBatchDraftKey)
            .Where(k => !string.IsNullOrEmpty(k))
            .Distinct(StringComparer.Ordinal)
            .Count());
    }

    /// <summary>
    /// **A4-1**：第一锚**真实硬失败**（合批后装不进任何日历窗）⇒ 必须继续试第二个锚，
    /// 且第二个锚**物理可行**。
    ///
    /// <para><b>夹具机制</b>：首工序三台设备，日历窗宽分别 **30 / 20 / 30 分钟**（1 分钟/件）。</para>
    /// <list type="bullet">
    ///   <item>A(30) 先排 ⇒ 设备1 [0,30]（正好装满，设备1 无余量）。</item>
    ///   <item>B(20) 排 ⇒ 试 A：合批 50 分钟，三台窗分别 30/20/30 ⇒ **全装不下** ⇒ 硬拒。
    ///         独立 ⇒ 设备2 [0,20]。</item>
    ///   <item>C(10) 排 ⇒ 候选 {批①, 批②}，A 交期更近 ⇒ **A 先试**：
    ///         合批 40 分钟，三台窗 30/20/30 ⇒ **仍全装不下** ⇒ **硬拒**。</item>
    ///   <item>甲方案：硬拒**不是**停止条件 ⇒ **继续试批②**：
    ///         合批 30 分钟，设备3 窗 30 ⇒ **装得下** ⇒ **物理可行**。</item>
    ///   <item>可行后立即停止并比较：独立 C 在设备3 [0,10] 完成 10 &lt; 合批完成 30 ⇒ 独立胜 ⇒ 不采纳。</item>
    /// </list>
    ///
    /// <para><b>判据</b>：`Trial=3`、`HardRejected=2`、`Physical=1`。
    ///   若「硬拒后不再试下一个锚」，`Trial` 会停在 1 或 2 且 `Physical=0`。</para>
    /// </summary>
    [Fact(DisplayName = "A4-1_首锚真实硬失败_必须继续试第二锚且第二锚物理可行")]
    public async Task A4_1_FirstAnchorHardRejected_MustTrySecondAnchor()
    {
        var request = Build(
            paths: new[] { ThreeAltZeroTailPath("RT", 1, primary: 1, alt1: 2, alt2: 3, 1m, r2: 4, r3: 5) },
            demands: new[]
            {
                // C 交期 Day+200；A 交期 Day+180（更近 ⇒ A 先试）；B 交期 Day+50（最远）。
                new DemandSpec("A", 1, 30m, Day.AddDays(180), "RT", 1),
                new DemandSpec("B", 2, 20m, Day.AddDays(50), "RT", 1),
                new DemandSpec("C", 3, 10m, Day.AddDays(200), "RT", 1)
            },
            calendar: new[]
            {
                (1, Day, Day.AddMinutes(30)),   // 只能装 A(30)
                (2, Day, Day.AddMinutes(20)),   // 只能装 B(20)
                (3, Day, Day.AddMinutes(30)),   // 能装 B+C(30)，装不下 A+C(40)
                (4, Day, Day.AddMinutes(60)),   // OP20（0 分钟，不占时间）
                (5, Day, Day.AddMinutes(60))    // OP30（0 分钟，不占时间）
            });

        var (result, counters) = await SolveWithCounters(request, "PI_FIRST");

        Assert.True(result.Success, result.ErrorMessage);

        var shape = string.Join(" | ", result.FinalTasks
            .OrderBy(t => t.OperationCode)
            .ThenBy(t => t.PlannedStartTime)
            .Select(t => $"{t.OperationCode}@{t.PlannedStartTime:HH:mm}-{t.PlannedEndTime:HH:mm} q={t.Quantity} r={t.ResourceId}"));

        Assert.True(counters.HardRejected == 2,
            $"应有两次真实硬拒（B 试批①、C 试批①），实得 {counters.HardRejected}。"
            + $"Trial={counters.TrialStarted} Physical={counters.PhysicalFeasible}。现场：{shape}");

        Assert.True(counters.TrialStarted == 3,
            $"硬拒后必须继续试下一个锚 ⇒ 试排总数应为 3（B 一次 + C 两次）；实得 {counters.TrialStarted}。现场：{shape}");

        Assert.True(counters.PhysicalFeasible == 1,
            $"第二锚（批②）应物理可行，实得 {counters.PhysicalFeasible}。现场：{shape}");

        Assert.True(counters.ComparedWins == 0 && counters.FinallyAdopted == 0,
            $"本夹具下合批在 ③ 层输给独立（10 &lt; 30）⇒ 不应胜出/采纳；"
            + $"实得 Wins={counters.ComparedWins} Adopted={counters.FinallyAdopted}。现场：{shape}");

        // 三条需求各自独立成批 ⇒ 9 条 Task（合批未采纳）。
        Assert.True(result.FinalTasks.Count == 9,
            $"期望 9 条 Task（3 批 × 3 工序），实得 {result.FinalTasks.Count}。现场：{shape}");
    }

    /// <summary>
    /// **A4-5**：单锚全链合法 ⇒ 采纳（与 A4-2 构成同族对照：一个判独立胜、一个判合批胜）。
    ///
    /// <para><b>夹具机制</b>（同 `StageBatchMergeTests.M_01` 的省换型思路）：三道工序的资源上各有一条
    ///   `EXACT(M→M) = 60min` 同产品换型规则。
    ///   · 独立排程：B 的三道工序各自命中 M→M ⇒ 付 3×60 = 180 分钟 Setup，完成被推后；
    ///   · 合批：目标批被移出后重排，资源上无前产品（InitialState）⇒ Setup = 0，且 50 件一次成型。</para>
    ///
    /// <para><b>判据</b>：四个计数必须全部落在「试了 1 次 → 可行 1 次 → 胜出 1 次 → 采纳 1 次」。
    ///   若只断言「3 条 Task」而不看计数，无法区分「合批赢」与「根本没能试排但恰好 3 条」。</para>
    /// </summary>
    [Fact(DisplayName = "A4-5_单锚全链合法_合批胜出并被采纳")]
    public async Task A4_5_SingleAnchorFullyLegal_IsAdopted()
    {
        var request = Build(
            paths: new[] { ThreeOpPath("RT", 1, 1, 2, 3, 1m) },
            demands: new[]
            {
                new DemandSpec("A", 1, 30m, Day.AddDays(200), "RT", 1),
                new DemandSpec("B", 2, 20m, Day.AddDays(200), "RT", 1)
            },
            calendar: new[]
            {
                (1, Day.AddDays(120), Day.AddDays(125)),
                (2, Day.AddDays(120), Day.AddDays(125)),
                (3, Day.AddDays(120), Day.AddDays(125))
            },
            setupRules: SameProductSetup(60m, 1, 2, 3));

        var (result, counters) = await SolveWithCounters(request, "PI_FIRST");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.UnscheduledTasks);

        var shape = string.Join(" | ", result.FinalTasks
            .OrderBy(t => t.OperationCode)
            .Select(t => $"{t.OperationCode} q={t.Quantity} setup={t.SetupTime} key={t.ExecutionBatchDraftKey}"));

        // ① 四个计数必须逐级收敛：试 1 → 可行 1 → 胜出 1 → 采纳 1。
        Assert.True(counters.TrialStarted == 1,
            $"两个需求 ⇒ 只有 B 会试一次锚批；实得 {counters.TrialStarted}。现场：{shape}");
        Assert.True(counters.PhysicalFeasible == 1,
            $"该锚批应物理可行；实得 {counters.PhysicalFeasible}。现场：{shape}");
        Assert.True(counters.ComparedWins == 1,
            $"省换型机制下合批应在 ③/④ 层胜出；实得 {counters.ComparedWins}。现场：{shape}");
        Assert.True(counters.FinallyAdopted == 1,
            $"胜出后应被真正采纳为合批；实得 {counters.FinallyAdopted}。现场：{shape}");

        // ② 结果形状：一个 Stage 执行批、完整 3 道工序、净产出 50（按 Stage 只计一次）。
        Assert.True(result.FinalTasks.Count == 3,
            $"合批 ⇒ 3 条 Operation 级 Task；实得 {result.FinalTasks.Count}（若是 6 条说明退回了独立排程）。现场：{shape}");
        Assert.Equal(new[] { "OP10", "OP20", "OP30" },
            result.FinalTasks.Select(t => t.OperationCode).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.All(result.FinalTasks, t => Assert.Equal(50m, t.Quantity));
        Assert.All(result.FinalTasks, t => Assert.Equal(50m, t.PlannedProcessQty));

        var batchKeys = result.FinalTasks
            .Select(t => t.ExecutionBatchDraftKey)
            .Where(k => !string.IsNullOrEmpty(k))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Assert.Single(batchKeys);
    }
}