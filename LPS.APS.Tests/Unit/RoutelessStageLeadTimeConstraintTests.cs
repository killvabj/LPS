using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using Xunit;
using OperationResourceEligibility = LPS.APS.Core.Entities.APS.OperationResourceEligibility;
using RoutingDependency = LPS.APS.Core.Entities.APS.RoutingDependency;
using RoutingOperation = LPS.APS.Core.Entities.APS.RoutingOperation;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **R09-③ 验收：中间无 Routing Stage 成为 Phase2 硬时间约束**（2026-10-29 落码）。
///
/// <para><b>改造前的缺陷（已实测，不是推测）</b>：`StageLeadTimeFact` 的提前期**只在**
/// `StageTimingNodeBuilder.Build` 里被累加，而 `Build` **只在 `PhaseFiveCompression.cs:470`
/// 被调用一次** —— 排完 Phase2/3/4 之后。⇒ 它是**事后描述**，Phase2 排程时压根不看它
/// ⇒ 工艺链「STAGE1（有 Routing）→ STAGE2（无 Routing）→ STAGE3（有 Routing）」中间那个
/// STAGE2 的提前期**不占任何时间**，STAGE3 被排得比工艺允许的更早。</para>
///
/// <para><b>本文件钉死三件事</b>：</para>
/// <list type="number">
///   <item><b>载体真的建出来了</b>：Phase1 把 (STAGE1 → STAGE3) 段连同中间的 STAGE2 leg 登记进
///     <see cref="ConstraintContext.RoutelessLeadStepsByDemand"/>；缺 LT 时 `LeadTimeResolved=false`；
///     链尾那段**不登记**（避免与 `BuildCore` 的末端累加双计）。</item>
///   <item><b>正排真的被约束住</b>：STAGE3 的开工 ≥ STAGE1 的完工 + 4h。</item>
///   <item><b>反排同样被约束住</b>：STAGE1 的完工 ≤ STAGE3 的开工 − 4h。</item>
/// </list>
///
/// <para><b>鉴别力（防止空测）</b>：每条正向断言都配一条**负对照** —— 把 STAGE2 的
/// `StageLeadTime` 去掉（⇒ 缺 LT、不臆造时间）后，同样的夹具必须**不产生**这段位移。
/// 若负对照也满足正向断言，说明断言是夹具自己送出来的，与本改动无关。</para>
/// </summary>
public class RoutelessStageLeadTimeConstraintTests
{
    private static readonly DateTime Day = new(2026, 9, 1);

    private const int MaterialId = 1;
    private const int DeptPre = 100;      // STAGE1 的部门
    private const int DeptMid = 200;      // STAGE2（无 Routing）与 STAGE3 的部门
    private const int ResPre = 1;
    private const int ResPost = 2;
    private const int FactoryId = 1;

    private const string StagePre = "STAGE1";
    private const string StageMid = "STAGE2";   // **无 RoutingOperation**
    private const string StagePost = "STAGE3";

    private const decimal MidLeadTimeHours = 4m;

    private const string DemandKey = "ORD-R09C|MAT-1|STAGE1|OP10";

    // ════════════════════════════════════════════════════════════════════
    // A 组：Phase1 载体
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// STAGE1 →（STAGE2 无 Routing）→ STAGE3 ⇒ 登记一条 (STAGE1, STAGE3) 步骤，
    /// 中间 leg = STAGE2、4 小时、提前期**已解析**。
    /// </summary>
    [Fact]
    public void A1_中间无RoutingStage_登记为STAGE1到STAGE3一段()
    {
        var context = BuildContext(withMidLeadTime: true);

        Assert.True(context.RoutelessLeadStepsByDemand.TryGetValue(DemandKey, out var steps),
            "R09-③：中间无 Routing Stage 的提前期没有登记 ⇒ Phase2 拿不到约束 ⇒ 等同未落码。");
        var step = Assert.Single(steps!);

        Assert.Equal(StagePre, step.FromStageCode);
        Assert.Equal(StagePost, step.ToStageCode);

        var leg = Assert.Single(step.Legs);
        Assert.Equal(StageMid, leg.StageCode);
        Assert.Equal(MidLeadTimeHours, leg.Hours);
        Assert.True(leg.LeadTimeResolved, "STAGE2 有 StageLeadTime 事实 ⇒ 必须判作已解析。");
    }

    /// <summary>
    /// 同一段链，但 STAGE2 **没有** `StageLeadTime` 记录 ⇒ leg 仍登记（结构上它还在那里），
    /// 但 `LeadTimeResolved = false` ⇒ Phase2 **不推进**（0号位 2026-10-15 第九轮 `:32`：
    /// 「不使用 0 或 72h 臆造时间」）。
    /// </summary>
    [Fact]
    public void A2_中间Stage缺LT_登记但标未解析_不臆造时间()
    {
        var context = BuildContext(withMidLeadTime: false);

        Assert.True(context.RoutelessLeadStepsByDemand.TryGetValue(DemandKey, out var steps));
        var leg = Assert.Single(Assert.Single(steps!).Legs);

        Assert.Equal(StageMid, leg.StageCode);
        Assert.False(leg.LeadTimeResolved, "缺 StageLeadTime 必须判作未解析 —— 不得当成「零提前期」。");
        Assert.Equal(0m, leg.Hours);   // 占位值，**不参与**时间推进（由 LeadTimeResolved 拦住）
    }

    /// <summary>
    /// 无 Routing Stage 落在**链尾**（STAGE3 之后）⇒ **不登记**：链尾那段本就是
    /// `StageTimingNodeBuilder.BuildCore` 从末端 Task 往后累加的范围，登记会**双计**。
    /// </summary>
    [Fact]
    public void A3_链尾无RoutingStage_不登记_避免与BuildCore双计()
    {
        // 链改成 STAGE1 → STAGE3 → STAGE_TAIL；routeless = STAGE_TAIL（STAGE2 从链里拿掉）
        var request = BuildRequest(
            withMidLeadTime: true,
            chains: new List<StageSequenceChain>
            {
                Chain((StagePre, 1), (StagePost, 2), ("STAGE_TAIL", 3))
            },
            leadTimes: new List<StageLeadTimeFact>
            {
                Fact("STAGE_TAIL", 4m)
            });

        var context = new PhaseOneConstraintBuilder().BuildConstraints(request);

        Assert.False(context.RoutelessLeadStepsByDemand.ContainsKey(DemandKey),
            "链尾的无 Routing Stage 不得登记 —— 它已由 BuildCore 的末端累加覆盖。");
    }

    // ════════════════════════════════════════════════════════════════════
    // B 组：Phase2 硬时间约束（Solver 级）
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// **正排**：STAGE3 的开工必须 ≥ STAGE1 的完工 **+ 4 小时**（STAGE2 的提前期）。
    /// 两个 Stage 用**不同资源**，所以这条位移不可能来自「抢同一台设备」。
    /// </summary>
    [Fact]
    public async Task B1_正排_STAGE3开工不早于STAGE1完工加4小时()
    {
        var result = await new FiniteCapacitySolver().SolveAsync(BuildRequest(withMidLeadTime: true, forward: true));

        Assert.True(result.Success, result.ErrorMessage);

        var pre = TaskOf(result, StagePre);
        var post = TaskOf(result, StagePost);

        var requiredStart = pre.PlannedEndTime.AddHours((double)MidLeadTimeHours);
        Assert.True(post.PlannedStartTime >= requiredStart,
            $"STAGE3 开工 {post.PlannedStartTime:yyyy-MM-dd HH:mm} 早于 {requiredStart:yyyy-MM-dd HH:mm}"
            + "（= STAGE1 完工 + STAGE2 提前期 4h）⇒ 中间无 Routing Stage 的提前期没有成为硬约束。");
    }

    /// <summary>
    /// **B1 的负对照**：同一夹具去掉 STAGE2 的 `StageLeadTime` ⇒ 缺 LT ⇒ **不推进**
    /// ⇒ STAGE3 会在 STAGE1 完工后**立刻**开工（&lt; 4h）。这一条证明 B1 的位移来自本改动，
    /// 而不是夹具里的依赖边或日历凑出来的。
    /// </summary>
    [Fact]
    public async Task B2_负对照_去掉中间Stage的LT_位移消失()
    {
        var result = await new FiniteCapacitySolver().SolveAsync(BuildRequest(withMidLeadTime: false, forward: true));

        Assert.True(result.Success, result.ErrorMessage);

        var pre = TaskOf(result, StagePre);
        var post = TaskOf(result, StagePost);

        var requiredStart = pre.PlannedEndTime.AddHours((double)MidLeadTimeHours);
        Assert.True(post.PlannedStartTime < requiredStart,
            $"去掉 STAGE2 的 LT 后 STAGE3 仍被推到 {post.PlannedStartTime:yyyy-MM-dd HH:mm}（≥ {requiredStart:yyyy-MM-dd HH:mm}）"
            + "⇒ B1 的断言没有鉴别力（位移不是本改动给的）。");
    }

    /// <summary>
    /// **倒排**：STAGE1 的完工必须 ≤ STAGE3 的开工 **− 4 小时**。
    /// 不补这一支的话，同一份输入换个排程方向就会算出不一致的时间链。
    /// </summary>
    [Fact]
    public async Task B3_倒排_STAGE1完工不晚于STAGE3开工减4小时()
    {
        var result = await new FiniteCapacitySolver().SolveAsync(BuildRequest(withMidLeadTime: true, forward: false));

        Assert.True(result.Success, result.ErrorMessage);

        var pre = TaskOf(result, StagePre);
        var post = TaskOf(result, StagePost);

        var requiredEnd = post.PlannedStartTime.AddHours(-(double)MidLeadTimeHours);
        Assert.True(pre.PlannedEndTime <= requiredEnd,
            $"STAGE1 完工 {pre.PlannedEndTime:yyyy-MM-dd HH:mm} 晚于 {requiredEnd:yyyy-MM-dd HH:mm}"
            + "（= STAGE3 开工 − STAGE2 提前期 4h）⇒ 倒排没有扣中间无 Routing Stage 的提前期。");
    }

    /// <summary>B3 的负对照：缺 LT ⇒ 倒排也不产生这段位移。</summary>
    [Fact]
    public async Task B4_负对照_倒排去掉中间Stage的LT_位移消失()
    {
        var result = await new FiniteCapacitySolver().SolveAsync(BuildRequest(withMidLeadTime: false, forward: false));

        Assert.True(result.Success, result.ErrorMessage);

        var pre = TaskOf(result, StagePre);
        var post = TaskOf(result, StagePost);

        var requiredEnd = post.PlannedStartTime.AddHours(-(double)MidLeadTimeHours);
        Assert.True(pre.PlannedEndTime > requiredEnd,
            $"去掉 STAGE2 的 LT 后 STAGE1 完工仍在 {pre.PlannedEndTime:yyyy-MM-dd HH:mm}（≤ {requiredEnd:yyyy-MM-dd HH:mm}）"
            + "⇒ B3 的断言没有鉴别力。");
    }

    // ════════════════════════════════════════════════════════════════════
    // 夹具
    // ════════════════════════════════════════════════════════════════════

    private static FinalTaskDraft TaskOf(DomainSolveResult result, string stageCode)
        => result.FinalTasks.Single(t => string.Equals(t.StageCode, stageCode, StringComparison.Ordinal));

    private static ConstraintContext BuildContext(bool withMidLeadTime)
        => new PhaseOneConstraintBuilder().BuildConstraints(BuildRequest(withMidLeadTime));

    /// <summary>
    /// 工艺链 `STAGE1（有 Routing）→ STAGE2（**无 RoutingOperation**）→ STAGE3（有 Routing）`。
    /// 两个有 Routing 的 Stage 各用**自己的资源**（不同部门），⇒ 两者的先后**只**能来自
    /// Routing 依赖边 + 本项的提前期推进。
    /// </summary>
    private static DomainSolveRequest BuildRequest(
        bool withMidLeadTime,
        bool forward = true,
        IReadOnlyList<StageSequenceChain>? chains = null,
        IReadOnlyList<StageLeadTimeFact>? leadTimes = null)
        => new()
        {
            PlanVersionId = 1,
            ScheduleRunId = 42,
            DomainKey = "DOMAIN",
            PlanningStart = Day,
            PlanningEnd = Day.AddDays(30),

            LogicalProductionDemands = new List<LogicalProductionDemand> { Demand() },

            StageSequenceChains = chains ?? new List<StageSequenceChain>
            {
                Chain((StagePre, 1), (StageMid, 2), (StagePost, 3))
            },

            StageLeadTimes = leadTimes ?? (withMidLeadTime
                ? new List<StageLeadTimeFact> { Fact(StageMid, MidLeadTimeHours) }
                : new List<StageLeadTimeFact>()),

            // 只有两个真实工序；STAGE2 **没有** RoutingOperation ⇒ 它是「无 Routing Stage」。
            RoutingOperations = new List<RoutingOperation>
            {
                new()
                {
                    MaterialId = MaterialId, ProductionDepartmentId = DeptPre,
                    RouteCode = "DEFAULT", PathId = 1,
                    OperationCode = "OP10", StageCode = StagePre,
                    StandardDuration = 60m * 60m,   // 秒：60 分钟
                    SetupTime = 0m
                },
                new()
                {
                    MaterialId = MaterialId, ProductionDepartmentId = DeptMid,
                    RouteCode = "DEFAULT", PathId = 1,
                    OperationCode = "OP20", StageCode = StagePost,
                    StandardDuration = 60m * 60m,
                    SetupTime = 0m
                }
            },

            RoutingDependencies = new List<RoutingDependency>
            {
                new()
                {
                    MaterialId = MaterialId, ProductionDepartmentId = DeptMid,
                    RouteCode = "DEFAULT", PathId = 1,
                    FromOperationCode = "OP10", ToOperationCode = "OP20",
                    DependencyType = "ES", LagTime = 0m
                }
            },

            OperationResourceEligibility = new List<OperationResourceEligibility>
            {
                new()
                {
                    MaterialId = MaterialId, ProductionDepartmentId = DeptPre,
                    RouteCode = "DEFAULT", OperationCode = "OP10",
                    ResourceId = ResPre, Priority = 1, CapacityFactor = 1m
                },
                new()
                {
                    MaterialId = MaterialId, ProductionDepartmentId = DeptMid,
                    RouteCode = "DEFAULT", OperationCode = "OP20",
                    ResourceId = ResPost, Priority = 1, CapacityFactor = 1m
                }
            },

            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = MaterialId, StageCode = StagePre, ProductionDepartmentId = DeptPre },
                new() { MaterialId = MaterialId, StageCode = StageMid, ProductionDepartmentId = DeptMid },
                new() { MaterialId = MaterialId, StageCode = StagePost, ProductionDepartmentId = DeptMid }
            },

            // 无 Routing Stage 的提前期是**有效生产小时**，要在该 Stage 自己部门的 WORK 窗口里消耗
            // （R09-① 口径）⇒ 给一条连续 24×7 窗口，使「4 小时」就是墙钟 4 小时，断言可精确表达。
            DepartmentWorkWindows = new List<DepartmentWorkWindow>
            {
                new() { FactoryId = FactoryId, DepartmentId = DeptMid, Start = Day, End = Day.AddDays(30) }
            },

            Resources = new List<ResourceDefinition>
            {
                new() { ResourceId = ResPre, ResourceCode = "R-PRE", FactoryCode = "F1", Capacity = 1m },
                new() { ResourceId = ResPost, ResourceCode = "R-POST", FactoryCode = "F1", Capacity = 1m }
            },

            CalendarSlots = new List<ResourceCalendarSlot>
            {
                new() { ResourceId = ResPre, Start = Day, End = Day.AddDays(30), IsAvailable = true },
                new() { ResourceId = ResPost, Start = Day, End = Day.AddDays(30), IsAvailable = true }
            },

            ExecutionConstraints = new List<ExecutionConstraint>(),
            MaterialConstraints = new List<MaterialAvailabilitySlice>(),

            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = forward ? "FORWARD" : "BACKWARD",
                    AllowMerge = false,
                    AllowSplit = false
                },
                BatchPolicies = TestBatchPolicy.Permissive(new[] { MaterialId }, DeptPre),
                SetupTransitionRules = new List<SetupTransitionRuleSnapshot>()
            }
        };

    private static LogicalProductionDemand Demand()
        => new()
        {
            LogicalDemandKey = DemandKey,
            PlanVersionId = 1L,
            DomainKey = "DOMAIN",
            AllocationSequence = 1,
            DemandKey = DemandKey,
            OrderId = 1001,
            MaterialId = MaterialId,
            FactoryId = FactoryId,
            StartStageCode = StagePre,
            StartOperationCode = "OP10",
            NetOutputQty = 1m,
            PlannedProcessQty = 1m,
            RequiredAvailableTime = Day.AddDays(20),
            DemandSequence = 1
        };

    private static StageSequenceChain Chain(params (string Stage, int Seq)[] steps)
        => new()
        {
            MaterialId = MaterialId,
            Stages = steps.Select(s => new StageSequenceStep { StageCode = s.Stage, StageSeq = s.Seq }).ToList()
        };

    private static StageLeadTimeFact Fact(string stageCode, decimal hours)
        => new()
        {
            MaterialId = MaterialId,
            StageCode = stageCode,
            LeadTimeHours = hours,
            MatchLevel = "DEPT_EXACT"
        };
}
