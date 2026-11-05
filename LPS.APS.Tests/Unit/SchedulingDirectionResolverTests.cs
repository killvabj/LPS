using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Shared.Models;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **B-005 Direction 上下文自决**（0号位 2026-10-08《未命名的Markdown文件 (2)(1).md》§三 / §十五 第 3 项）。
///
/// 覆盖 0号位 点名的四类 AUTO 场景：
///   ① 明显倒排场景 ② 明显正排场景 ③ 连续份额场景 ④ Slack / 物料 / 资源导致 Direction 变化场景。
/// 另覆盖：Firm/Frozen/Lock 硬锚点、DemandGoal 判据（含**取值缺失不静默**）、无上下文默认。
///
/// 直接驱动 <see cref="SchedulingDirectionResolver.Resolve"/>（纯函数、确定性），
/// 与 E2E 几何断言（<c>PhaseTwoRoutingCandidateTests</c>）互补：
///   本文件证明「**为什么**是这个方向」（判据层），E2E 证明「方向**确实生效**到排程结果」（行为层）。
/// </summary>
public class SchedulingDirectionResolverTests
{
    private static readonly DateTime Start = new(2026, 10, 8, 8, 0, 0);
    private static readonly DateTime NoMaterialFloor = DateTime.MinValue;

    private const int TestResourceId = 1;
    private const string TestRouteCode = "R1";

    /// <summary>
    /// 单工序、60 分钟**单件**工时的工序节点。
    /// ⚠ 2026-10-28 起 Slack 按 Phase2 同口径算有效时长（`标准工时 × PlannedProcessQty ÷ 产能系数`），
    ///   不再是「标准工时之和」⇒ `Demand()` 的 `PlannedProcessQty = 10`、系数 1 ⇒ **一整批 = 600 分钟**。
    /// </summary>
    private static List<OperationNode> Ops60() => Ops(60m);

    private static List<OperationNode> Ops(decimal standardDurationMinutes) => new()
    {
        new OperationNode
        {
            OperationCode = "OP10",
            StageCode = "STAGE1",
            StandardDuration = standardDurationMinutes,
            RouteCode = TestRouteCode,
            PathId = 1,
            ProductionDepartmentId = 0
        }
    };

    /// <summary>Ops60 的资格键（与 `PhaseOneConstraintBuilder.cs:222` / `PhaseTwoInitialScheduler.cs:4206` 同口径）。</summary>
    private static EligibilityLookupKey Ops60Key() => new(1, 0, TestRouteCode, 1, "OP10");

    /// <summary>
    /// **数据齐全**的上下文：资格 + 产能系数 1 + 工作日历。
    /// ⚠ 2026-10-28 起 Slack 判据**要求这三样齐全**才可信；缺任一（或直接用 `new ConstraintContext()`）
    ///   ⇒ `estimated = true` ⇒ 弃权并发 `DUE_SLACK_UNCERTAIN`，不判紧/松。
    /// </summary>
    private static ConstraintContext Ctx()
    {
        var ctx = new ConstraintContext();
        var key = Ops60Key();
        ctx.OperationResourceEligibility[key] = new List<int> { TestResourceId };
        ctx.ResourceCapacityFactors[key] = new Dictionary<int, decimal> { [TestResourceId] = 1m };
        ctx.ResourceCalendars[TestResourceId] = BuildCalendar();
        return ctx;
    }

    /// <summary>
    /// 基准日历：**全天候**（每天 00:00→次日 00:00，共 60 天）。
    /// 用全天历是为了让「日历跨度 == 自然分钟」⇒ 紧/松用例的断言能直接对数字；
    /// 「跳过非工作时段」这件事由 <c>Slack_日历跳过非工作时段</c> 单独覆盖。
    /// </summary>
    private static List<TimeWindow> BuildCalendar()
    {
        var windows = new List<TimeWindow>();
        for (var i = 0; i < 60; i++)
        {
            var day = Start.Date.AddDays(i);
            windows.Add(new TimeWindow(day, day.AddDays(1)));
        }
        return windows;
    }

    /// <summary>同上，但换成指定的日历（用于「跳过非工作时段」用例）。</summary>
    private static ConstraintContext CtxWithCalendar(List<TimeWindow> calendar)
    {
        var ctx = new ConstraintContext();
        var key = Ops60Key();
        ctx.OperationResourceEligibility[key] = new List<int> { TestResourceId };
        ctx.ResourceCapacityFactors[key] = new Dictionary<int, decimal> { [TestResourceId] = 1m };
        ctx.ResourceCalendars[TestResourceId] = calendar;
        return ctx;
    }

    private static LogicalProductionDemand Demand(
        DateTime? due = null,
        bool isContinuation = false,
        string? preferredResourceCode = null,
        int? preferredResourceId = null,
        int? fallbackResourceId = null,
        string key = "D1",
        decimal plannedProcessQty = 10m)
        => new()
        {
            LogicalDemandKey = key,
            DemandKey = key,
            AllocationSequence = 1,
            MaterialId = 1,
            FactoryId = 1,
            NetOutputQty = 10m,
            PlannedProcessQty = plannedProcessQty,
            RequiredAvailableTime = due ?? Start.AddDays(30),
            DemandSequence = 1,
            IsContinuation = isContinuation,
            NoSplitMerge = isContinuation,
            PreferredResourceCode = preferredResourceCode,
            PreferredResourceId = preferredResourceId,
            FallbackResourceId = fallbackResourceId
        };

    private static ConstraintContext CtxWithLock(string draftId)
    {
        var ctx = new ConstraintContext();
        ctx.LockedTasks[(draftId, "STAGE1", "OP10")] = new LockedTaskConstraint
        {
            DraftId = draftId,
            ResourceId = 1,
            LockedStart = Start,
            LockedEnd = Start.AddHours(1),
            ConstraintType = "ANCHOR"
        };
        return ctx;
    }

    // ════════════════════════════════════════════════════════════
    // ① 明显倒排场景：交期极紧（Slack < 0）⇒ BACKWARD
    // ════════════════════════════════════════════════════════════
    [Fact]
    public void 明显倒排场景_交期紧_Slack为负_判BACKWARD()
    {
        // lead = 60min，Due 在起点后仅 30min ⇒ 最早完成 09:00 已晚于 Due 08:30 ⇒ slack = -30min
        var d = Demand(due: Start.AddMinutes(30));

        var decision = SchedulingDirectionResolver.Resolve(d, Ops60(), Ctx(), Start, NoMaterialFloor);

        Assert.Equal(SchedulingDirectionResolver.Backward, decision.Direction);
        Assert.True(decision.Has(SchedulingDirectionResolver.SignalDueTight));
    }

    // ════════════════════════════════════════════════════════════
    // ② 明显正排场景：交期极松（Slack > lead）⇒ FORWARD
    // ════════════════════════════════════════════════════════════
    [Fact]
    public void 明显正排场景_交期松_Slack大于一个工艺周期_判FORWARD()
    {
        var d = Demand(due: Start.AddDays(30));

        var decision = SchedulingDirectionResolver.Resolve(d, Ops60(), Ctx(), Start, NoMaterialFloor);

        Assert.Equal(SchedulingDirectionResolver.Forward, decision.Direction);
        Assert.True(decision.Has(SchedulingDirectionResolver.SignalDueLoose));
    }

    // ════════════════════════════════════════════════════════════
    // ③ 连续份额场景：A/B 既存执行批 ⇒ FORWARD（最早承诺）
    // ════════════════════════════════════════════════════════════
    [Fact]
    public void 连续份额场景_IsContinuation_判FORWARD()
    {
        var d = Demand(isContinuation: true);

        var decision = SchedulingDirectionResolver.Resolve(d, Ops60(), Ctx(), Start, NoMaterialFloor);

        Assert.Equal(SchedulingDirectionResolver.Forward, decision.Direction);
        Assert.True(decision.Has(SchedulingDirectionResolver.SignalContinuationSlice));
    }

    // ════════════════════════════════════════════════════════════
    // ④ Slack / 物料 / 资源 导致 Direction 变化场景
    //    同一需求，只改**一个**上下文 ⇒ 方向必须随之变化
    // ════════════════════════════════════════════════════════════

    /// <summary>④-a：同一需求，只改 Slack（Due）⇒ 方向从 FORWARD 翻到 BACKWARD。</summary>
    [Fact]
    public void Direction变化_仅改Slack_方向随之翻转()
    {
        var ops = Ops60();
        var ctx = Ctx();

        var loose = SchedulingDirectionResolver.Resolve(
            Demand(due: Start.AddDays(30)), ops, ctx, Start, NoMaterialFloor);
        var tight = SchedulingDirectionResolver.Resolve(
            Demand(due: Start.AddMinutes(30)), ops, ctx, Start, NoMaterialFloor);

        Assert.Equal(SchedulingDirectionResolver.Forward, loose.Direction);
        Assert.Equal(SchedulingDirectionResolver.Backward, tight.Direction);
    }

    /// <summary>④-b：同一需求，只改 Material（子件下界晚于计划起点）⇒ 物料刚可用 ⇒ FORWARD 信号。</summary>
    [Fact]
    public void Direction变化_仅改物料下界_记MATERIAL_LATE()
    {
        var d = Demand(due: Start.AddMinutes(30));   // 交期紧（未过但装不下）

        var decision = SchedulingDirectionResolver.Resolve(
            d, Ops60(), Ctx(), Start, Start.AddDays(5));

        // 交期紧 + 物料晚到 ⇒ 信号冲突 ⇒ MIXED（先倒排、失败转正排）
        Assert.Equal(SchedulingDirectionResolver.Mixed, decision.Direction);
        Assert.True(decision.Has(SchedulingDirectionResolver.SignalMaterialLate));
        Assert.True(decision.Has(SchedulingDirectionResolver.SignalDueTight));
        Assert.True(decision.Has(SchedulingDirectionResolver.SignalConflicting));
    }

    /// <summary>④-b'：交期松 + 物料晚到 ⇒ 同为正向信号 ⇒ FORWARD（不冲突）。</summary>
    [Fact]
    public void Direction变化_物料晚到且交期松_判FORWARD()
    {
        var decision = SchedulingDirectionResolver.Resolve(
            Demand(due: Start.AddDays(30)), Ops60(), Ctx(), Start, Start.AddDays(5));

        Assert.Equal(SchedulingDirectionResolver.Forward, decision.Direction);
        Assert.True(decision.Has(SchedulingDirectionResolver.SignalMaterialLate));
    }

    /// <summary>④-c：同一需求，只改 Resource（资源连续性软偏好）⇒ 正向信号。</summary>
    [Fact]
    public void Direction变化_仅改资源偏好_记为正向信号()
    {
        var withPref = SchedulingDirectionResolver.Resolve(
            Demand(preferredResourceCode: "R1"), Ops60(), Ctx(), Start, NoMaterialFloor);

        Assert.Equal(SchedulingDirectionResolver.Forward, withPref.Direction);
        Assert.True(withPref.Has(SchedulingDirectionResolver.SignalPreferredResource));

        // 旧 ID 形态（PreferredResourceId / FallbackResourceId）同样被认（P1-11 并存）
        Assert.True(SchedulingDirectionResolver.Resolve(
            Demand(preferredResourceId: 7), Ops60(), Ctx(), Start, NoMaterialFloor)
            .Has(SchedulingDirectionResolver.SignalPreferredResource));
        Assert.True(SchedulingDirectionResolver.Resolve(
            Demand(fallbackResourceId: 9), Ops60(), Ctx(), Start, NoMaterialFloor)
            .Has(SchedulingDirectionResolver.SignalPreferredResource));
    }

    // ════════════════════════════════════════════════════════════
    // Firm/Frozen/Lock 硬锚点：最高优先，压过一切软信号
    // ════════════════════════════════════════════════════════════
    [Fact]
    public void 锁定任务存在_硬锚点最高优先_即使交期紧也判FORWARD()
    {
        var decision = SchedulingDirectionResolver.Resolve(
            Demand(due: Start.AddMinutes(-30)),   // 交期紧本应 BACKWARD
            Ops60(), CtxWithLock("D1"), Start, NoMaterialFloor);

        Assert.Equal(SchedulingDirectionResolver.Forward, decision.Direction);
        Assert.True(decision.Has(SchedulingDirectionResolver.SignalLockedAnchor));
        Assert.False(decision.Has(SchedulingDirectionResolver.SignalDueTight));
    }

    // ════════════════════════════════════════════════════════════
    // DemandGoal 判据（P-006 两值）——含**取值缺失不静默**
    // ════════════════════════════════════════════════════════════
    [Fact]
    public void DemandGoal_取值缺失_登记缺口信号且不猜()
    {
        // **2026-10-13 更正**：生产路径取值链**已投产**（2号位 `PeggingOrchestrator.FillDemandGoalsAsync`，
        // `:250` 调用）⇒ 本用例的 `demandGoal: null` 代表**个案缺口**（跨厂 SH 未落地／血缘无法唯一确定），
        // **不再是**系统性恒 null；端到端路径见 `PhaseTwoRoutingCandidateTests.AUTO_DemandGoal非空_端到端确实改变方向`。
        var decision = SchedulingDirectionResolver.Resolve(
            Demand(due: Start.AddDays(30)), Ops60(), Ctx(), Start, NoMaterialFloor, demandGoal: null);

        Assert.True(decision.Has(SchedulingDirectionResolver.SignalDemandGoalAbsent));
    }

    [Fact]
    public void DemandGoal_客户承诺_为反向信号()
    {
        var decision = SchedulingDirectionResolver.Resolve(
            Demand(due: Start.AddDays(30)),   // 交期松本应 FORWARD
            Ops60(), Ctx(), Start, NoMaterialFloor,
            demandGoal: SchedulingDirectionResolver.GoalCustomerCommitment);

        Assert.True(decision.Has(SchedulingDirectionResolver.SignalGoalCustomerCommitment));
        Assert.Equal(SchedulingDirectionResolver.Mixed, decision.Direction);   // 松 + 客户承诺 ⇒ 冲突
    }

    [Fact]
    public void DemandGoal_库存补充_为正向信号()
    {
        var decision = SchedulingDirectionResolver.Resolve(
            Demand(due: Start.AddMinutes(30)),   // 交期紧本应 BACKWARD
            Ops60(), Ctx(), Start, NoMaterialFloor,
            demandGoal: SchedulingDirectionResolver.GoalInventoryReplenishment);

        Assert.True(decision.Has(SchedulingDirectionResolver.SignalGoalInventoryReplenishment));
        Assert.Equal(SchedulingDirectionResolver.Mixed, decision.Direction);   // 紧 + 库存补充 ⇒ 冲突
    }

    [Fact]
    public void DemandGoal_未知取值_同样登记缺口不猜()
    {
        var decision = SchedulingDirectionResolver.Resolve(
            Demand(due: Start.AddDays(30)), Ops60(), Ctx(), Start, NoMaterialFloor,
            demandGoal: "SOMETHING_ELSE");

        Assert.True(decision.Has(SchedulingDirectionResolver.SignalDemandGoalAbsent));
    }

    // ════════════════════════════════════════════════════════════
    // 无上下文可依 / 无有效交期：确定性默认，且**不是**「AUTO 恒等 MIXED」
    // ════════════════════════════════════════════════════════════
    [Fact]
    public void 无任何上下文信号_确定性默认BACKWARD且标记NO_CONTEXT_SIGNAL()
    {
        // Due 为 default ⇒ 交期缺失（不产生交期信号）；无锁定、无连续、无偏好、无物料下界
        var decision = SchedulingDirectionResolver.Resolve(
            Demand(due: DateTime.MinValue), Ops60(), Ctx(), Start, NoMaterialFloor);

        // 注意：「无交期」只认 default；「交期已过」是有效交期（DUE_OVERDUE ⇒ 正排），故不应产生 DUE_TIGHT
        Assert.True(decision.Has(SchedulingDirectionResolver.SignalDueAbsent));
        Assert.False(decision.Has(SchedulingDirectionResolver.SignalDueTight));
        Assert.Equal(SchedulingDirectionResolver.Backward, decision.Direction);
        Assert.True(decision.Has(SchedulingDirectionResolver.SignalNoContext));
    }

    /// <summary>裁决必须是**纯函数**：同输入同输出（确定性、可重放）。</summary>
    [Fact]
    public void 同输入重复调用_结果完全一致()
    {
        var ops = Ops60();

        var a = SchedulingDirectionResolver.Resolve(Demand(), ops, Ctx(), Start, NoMaterialFloor);
        var b = SchedulingDirectionResolver.Resolve(Demand(), ops, Ctx(), Start, NoMaterialFloor);

        Assert.Equal(a.Direction, b.Direction);
        Assert.Equal(a.Signals, b.Signals);
        Assert.Equal(a.Reason, b.Reason);
    }

    // ════════════════════════════════════════════════════════════
    // R05-27-10：Slack 必须用「可信时长」—— 0号位 点名的最小三例
    //   紧交期 / 正常 / 数据不全
    //   口径 = `标准工时 × PlannedProcessQty ÷ 产能系数`（与 Phase2 `:3792` 同式），再过资源日历
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// **紧交期**：一整批 600 分钟（60分/件 × 10件 ÷ 系数1），交期只给 3 小时 ⇒ slack = −420 ⇒ 倒排。
    /// 旧实现拿「标准工时之和 60 分钟」当周期 ⇒ slack 会算成 +120 ⇒ **误判极松、错判正排**。本用例锁死这个回归。
    /// </summary>
    [Fact]
    public void Slack_紧交期_判DueTight且倒排()
    {
        var d = Demand(due: Start.AddHours(3));

        var slack = SchedulingDirectionResolver.ComputeSlackMinutes(
            d, Ops60(), Ctx(), Start, Start, out var absent, out var estimated, out var leadCalendar);

        Assert.False(absent);
        Assert.False(estimated);            // 资格 / 系数 / 日历齐全 ⇒ 估算可信
        Assert.Equal(600m, leadCalendar);   // 全天历 ⇒ 日历跨度 == 600 分钟
        Assert.Equal(-420m, slack);         // 11:00 − 18:00

        var decision = SchedulingDirectionResolver.Resolve(d, Ops60(), Ctx(), Start, NoMaterialFloor);
        Assert.True(decision.Has(SchedulingDirectionResolver.SignalDueTight));
        Assert.Equal(SchedulingDirectionResolver.Backward, decision.Direction);
    }

    /// <summary>
    /// **正常**：slack 落在 `[0, 一个日历周期]` 之间 ⇒ 交期判据**不投票**（既不紧也不松），
    /// 方向交由其它上下文裁决（此处全无 ⇒ BACKWARD + NO_CONTEXT_SIGNAL）。
    /// </summary>
    [Fact]
    public void Slack_正常区间_交期判据弃权不投票()
    {
        var d = Demand(due: Start.AddHours(15));   // slack = 900 − 600 = 300，落在 [0, 600]

        var slack = SchedulingDirectionResolver.ComputeSlackMinutes(
            d, Ops60(), Ctx(), Start, Start, out var absent, out var estimated, out var leadCalendar);

        Assert.False(absent);
        Assert.False(estimated);
        Assert.Equal(600m, leadCalendar);
        Assert.Equal(300m, slack);

        var decision = SchedulingDirectionResolver.Resolve(d, Ops60(), Ctx(), Start, NoMaterialFloor);
        Assert.False(decision.Has(SchedulingDirectionResolver.SignalDueTight));
        Assert.False(decision.Has(SchedulingDirectionResolver.SignalDueLoose));
        Assert.True(decision.Has(SchedulingDirectionResolver.SignalNoContext));
    }

    /// <summary>**明显松**：slack 超过一个日历周期 ⇒ 最早承诺 ⇒ 正排。</summary>
    [Fact]
    public void Slack_明显松_判DueLoose且正排()
    {
        var d = Demand(due: Start.AddDays(30));

        var decision = SchedulingDirectionResolver.Resolve(d, Ops60(), Ctx(), Start, NoMaterialFloor);

        Assert.True(decision.Has(SchedulingDirectionResolver.SignalDueLoose));
        Assert.Equal(SchedulingDirectionResolver.Forward, decision.Direction);
    }

    /// <summary>
    /// **数据不全**：资格 / 产能系数 / 日历任一缺失 ⇒ `estimated = true` ⇒ **不许判紧/松**，
    /// 只发中性 `DUE_SLACK_UNCERTAIN`。这是本条整改的核心：宁可弃权，不许拿不可信时长投票。
    /// </summary>
    [Fact]
    public void Slack_数据不全_标记不可信并弃权()
    {
        var d = Demand(due: Start.AddMinutes(30));   // 表面上「极紧」

        var slack = SchedulingDirectionResolver.ComputeSlackMinutes(
            d, Ops60(), new ConstraintContext(), Start, Start,
            out var absent, out var estimated, out _);

        Assert.False(absent);
        Assert.True(estimated);

        var decision = SchedulingDirectionResolver.Resolve(
            d, Ops60(), new ConstraintContext(), Start, NoMaterialFloor);

        Assert.True(decision.Has(SchedulingDirectionResolver.SignalDueSlackUncertain));
        Assert.False(decision.Has(SchedulingDirectionResolver.SignalDueTight));
        Assert.False(decision.Has(SchedulingDirectionResolver.SignalDueLoose));
    }

    /// <summary>无工序也算数据不全（算不出工艺周期，不能拿 0 分钟冒充）。</summary>
    [Fact]
    public void Slack_无工序_标记不可信()
    {
        SchedulingDirectionResolver.ComputeSlackMinutes(
            Demand(due: Start.AddDays(30)), new List<OperationNode>(), Ctx(), Start, Start,
            out _, out var estimated, out _);

        Assert.True(estimated);
    }

    /// <summary>
    /// 日历必须**真的跳非工作时段**：08:00-09:00 只给 60 分钟，工时要 90 分钟
    /// ⇒ 剩余 30 分钟落到 11:00 ⇒ 完成 11:30、日历跨度 210 分钟（不是 90）。
    /// </summary>
    [Fact]
    public void Slack_日历跳过非工作时段()
    {
        var day = Start.Date;
        var calendar = new List<TimeWindow>
        {
            new(day.AddHours(8), day.AddHours(9)),
            new(day.AddHours(11), day.AddHours(12))
        };

        // 单件 90 分钟 ⇒ PlannedProcessQty 取 1 让有效时长正好 90 分钟
        var slack = SchedulingDirectionResolver.ComputeSlackMinutes(
            Demand(due: day.AddHours(23), plannedProcessQty: 1m), Ops(90m), CtxWithCalendar(calendar),
            Start, Start, out _, out var estimated, out var leadCalendar);

        Assert.False(estimated);
        Assert.Equal(210m, leadCalendar);                 // 08:00 → 11:30
        Assert.Equal((decimal)(day.AddHours(23) - day.AddHours(11).AddMinutes(30)).TotalMinutes, slack);
    }

    /// <summary>无有效交期时不算 slack（`absent`），且标记为 absent 之外不产生交期信号。</summary>
    [Fact]
    public void Slack_无交期_标记Absent()
    {
        var slack = SchedulingDirectionResolver.ComputeSlackMinutes(
            Demand(due: DateTime.MinValue), Ops60(), Ctx(), Start, Start, out var absent, out _, out _);

        Assert.True(absent);
        Assert.Equal(0m, slack);
    }
}
