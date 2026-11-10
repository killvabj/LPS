using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Shared.Models;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// R03 / A2（0号位 2026-11-10《Batch_Code_Review_and_Action_Plan v1.0》§3-R03、§4-A2）：
/// **把 `TryMergeDemandIntoTask` 的每一个 `return null` 出口都实测一次「零副作用」**，
/// 不再用「第一处写入在 `AddOccupancyWindow` 之前」这种**行号论据**销账。
///
/// <para><b>0号位 的原话</b>：「确有多个 `return null` 在份额修改之前；但『第一处写入是
///   AddOccupancyWindow』这个论据**不准确**……必须以**所有 `return null` 之前没有状态写入**
///   为证明对象，同时用占用、份额、timeline、Task 哈希确认失败后不污染。」</para>
///
/// <para><b>本组的做法</b>：直调 `internal TryMergeDemandIntoTask`，构造**每一个**出口的触发条件，
///   调用前对**五类状态**取指纹，调用后逐项断言完全相同：
///   ① `FinalTaskDraft` 字段 ② `allocationTaskShare` ③ `resourceOccupancy`
///   ④ `ProductTimeline`（用真实消费语义 `GetPrevMaterial` 探针，不是 `Assert.NotNull`）
///   ⑤ `TraceNotes`。</para>
///
/// <para><b>为什么不能用现有的 `BatchMergeCommitTests.C2-05` 顶替</b>：那条只覆盖「交期硬拒」一个出口，
///   且只比了份额与占用两项。R03 要求的是**全部出口**、**五类状态**。</para>
///
/// <para>⚠ 纯内存夹具，零 DB；不修改任何生产代码，只测现状。</para>
/// </summary>
public sealed class PhaseTwoMergeZeroSideEffectTests
{
    private static PhaseTwoInitialScheduler Scheduler => new PhaseTwoInitialScheduler();

    private static readonly DateTime T0 = new(2026, 11, 10, 8, 0, 0);
    private const int ResourceId = 1;
    private const int MaterialId = 100;
    private const string Stage = "S1";
    private const string Op = "OP10";
    private const string TargetTaskId = "T-D2";
    private const string TargetSourceId = "D2";
    /// <summary>物料可用性按 `AllocationSequence` 分组（`ConstraintContext.MaterialAvailability` 的键）。</summary>
    private const long AllocSeq = 1L;

    // ─────────────────────────────────────────────────────────────
    // 夹具
    // ─────────────────────────────────────────────────────────────

    /// <summary>目标 Task：10 件、6 分钟/件 ⇒ 占 T0 ~ T0+1h。</summary>
    private static FinalTaskDraft TargetTask(int? resourceId = ResourceId) => new()
    {
        FinalDraftId = TargetTaskId,
        SourceDraftId = TargetSourceId,
        MaterialId = MaterialId,
        ResourceId = resourceId,
        StageCode = Stage,
        OperationCode = Op,
        PlannedStartTime = T0,
        PlannedEndTime = T0.AddHours(1),
        SetupTime = 0m,
        Quantity = 10m,
        PlannedProcessQty = 10m,
        ExecutionBatchDraftKey = "EB|D2|001",
    };

    private static LogicalProductionDemand Demand(string key, decimal qty, DateTime? due = null) => new()
    {
        LogicalDemandKey = key,
        DemandKey = key,
        AllocationSequence = AllocSeq,
        MaterialId = MaterialId,
        NetOutputQty = qty,
        PlannedProcessQty = qty,
        StartStageCode = Stage,
        RouteCode = "DEFAULT",
        PathId = 1,
        RequiredAvailableTime = due ?? DateTime.MaxValue,
    };

    private static RoutingGraph Graph(bool includeOperation = true)
    {
        var g = new RoutingGraph();
        if (!includeOperation)
        {
            return g;   // 出口①：工序在图上不存在
        }

        g.Operations[OperationNodeKey.Of(Stage, Op)] = new OperationNode
        {
            OperationCode = Op,
            StageCode = Stage,
            StandardDuration = 6m,          // **分钟/件**（此处是 OperationNode，未经 ÷60）
            OperationPlanningMode = "FINITE_RESOURCE",
            RouteCode = "DEFAULT",
            PathId = 1,
            ProductionDepartmentId = 1,
        };
        return g;
    }

    private static ConstraintContext Constraints(FinalTaskDraft target, bool withCapacity = true)
    {
        var c = new ConstraintContext();
        if (withCapacity)
        {
            c.ResourceCapacityFactors[new EligibilityLookupKey(MaterialId, 1, "DEFAULT", 1, Op)]
                = new Dictionary<int, decimal> { [ResourceId] = 1m };
        }

        c.ResourceCalendars[ResourceId] = new List<TimeWindow>
        {
            new TimeWindow(T0.AddHours(-8), T0.AddHours(20)),
        };

        // 必须有真实初值，否则 timeline 指纹恒为全 null，等于没比较。
        c.ProductTimeline = ResourceProductTimeline.FromTasks(new[] { target });
        return c;
    }

    // ─────────────────────────────────────────────────────────────
    // 五类状态指纹
    // ─────────────────────────────────────────────────────────────

    private static string TaskSig(FinalTaskDraft t) =>
        $"{t.FinalDraftId}|{t.SourceDraftId}|{t.ResourceId}|{t.StageCode}|{t.OperationCode}"
        + $"|{t.PlannedStartTime:O}|{t.PlannedEndTime:O}|{t.SetupTime}|{t.Quantity}"
        + $"|{t.PlannedProcessQty}|{t.ExecutionBatchDraftKey}";

    private static string ShareSig(Dictionary<string, List<(string DemandKey, decimal ShareQty)>> shares) =>
        string.Join(";", shares
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key + "=" + string.Join(",", kv.Value
                .Select(s => $"{s.DemandKey}:{s.ShareQty}"))));

    private static string OccupancySig(Dictionary<int, List<TimeWindow>> occupancy) =>
        string.Join(";", occupancy
            .OrderBy(kv => kv.Key)
            .Select(kv => kv.Key + "=[" + string.Join(",", kv.Value
                .Select(w => $"{w.Start:O}-{w.End:O}")) + "]"));

    private static string TimelineSig(ConstraintContext c, FinalTaskDraft target)
    {
        var probes = new[]
        {
            target.PlannedStartTime.AddMinutes(-1), target.PlannedStartTime,
            target.PlannedStartTime.AddMinutes(30), target.PlannedEndTime,
            target.PlannedEndTime.AddMinutes(1),
        };
        return string.Join("|", probes.Select(x =>
            x.ToString("HH:mm") + "=>" + (c.ProductTimeline.GetPrevMaterial(ResourceId, x)?.ToString() ?? "null")));
    }

    private static string TraceSig(ConstraintContext c) =>
        string.Join(";", c.TraceNotes.Select(n => $"{n.Key}|{n.ReasonCode}|{n.Level}|{n.Message}"));

    // ─────────────────────────────────────────────────────────────
    // 统一断言：硬拒 + 五类状态逐项不变
    // ─────────────────────────────────────────────────────────────

    private static void AssertRejectedWithoutSideEffect(
        string because,
        FinalTaskDraft target,
        LogicalProductionDemand demand,
        RoutingGraph graph,
        ConstraintContext constraints,
        Dictionary<int, List<TimeWindow>> occupancy,
        Dictionary<string, List<(string DemandKey, decimal ShareQty)>> shares,
        Dictionary<string, LogicalProductionDemand>? demandByKey = null)
    {
        var taskBefore = TaskSig(target);
        var shareBefore = ShareSig(shares);
        var occBefore = OccupancySig(occupancy);
        var tlBefore = TimelineSig(constraints, target);
        var traceBefore = TraceSig(constraints);

        var merged = Scheduler.TryMergeDemandIntoTask(
            demand, target, graph, constraints, occupancy, shares, T0.AddDays(30), demandByKey);

        // ① 必须真的硬拒（否则本用例压根没走到预期的出口，后面的「不变」断言毫无意义）。
        Assert.True(merged is null,
            $"「{because}」应触发硬拒，实际返回了合并结果（qty={merged?.PlannedProcessQty}）"
            + "—— 夹具没有命中该出口，本用例无鉴别力。");

        // ②～⑥ 五类状态逐项对拍。
        Assert.Equal(taskBefore, TaskSig(target));
        Assert.Equal(shareBefore, ShareSig(shares));
        Assert.Equal(occBefore, OccupancySig(occupancy));
        Assert.Equal(tlBefore, TimelineSig(constraints, target));
        Assert.Equal(traceBefore, TraceSig(constraints));
    }

    // ─────────────────────────────────────────────────────────────
    // 出口①：routingGraph 里没有该工序（Operation 不存在）
    // ─────────────────────────────────────────────────────────────

    [Fact(DisplayName = "R03-1 · 工序不存在 ⇒ 硬拒且五项状态零变化")]
    public void Reject_OperationMissing_NoSideEffect()
    {
        var target = TargetTask();
        AssertRejectedWithoutSideEffect(
            "routingGraph 缺少 (Stage, Op) 节点",
            target, Demand("D1", 5m), Graph(includeOperation: false),
            Constraints(target),
            new Dictionary<int, List<TimeWindow>>(),
            new Dictionary<string, List<(string DemandKey, decimal ShareQty)>>(StringComparer.Ordinal));
    }

    // ─────────────────────────────────────────────────────────────
    // 出口②：目标 Task 的 ResourceId 不是 int（非资源工序）
    // ─────────────────────────────────────────────────────────────

    [Fact(DisplayName = "R03-2 · 目标无资源 ⇒ 硬拒且五项状态零变化")]
    public void Reject_TargetHasNoResource_NoSideEffect()
    {
        var target = TargetTask(resourceId: null);
        AssertRejectedWithoutSideEffect(
            "目标 Task.ResourceId 为 null（UNCONSTRAINED/WAIT_ONLY 工序）",
            target, Demand("D1", 5m), Graph(),
            Constraints(target),
            new Dictionary<int, List<TimeWindow>>(),
            new Dictionary<string, List<(string DemandKey, decimal ShareQty)>>(StringComparer.Ordinal));
    }

    // ─────────────────────────────────────────────────────────────
    // 出口③：CapacityFactor 缺失
    // ─────────────────────────────────────────────────────────────

    [Fact(DisplayName = "R03-3 · 产能系数缺失 ⇒ 硬拒且五项状态零变化")]
    public void Reject_CapacityFactorMissing_NoSideEffect()
    {
        var target = TargetTask();
        AssertRejectedWithoutSideEffect(
            "`ResourceCapacityFactors` 里没有 (Material, Dept, Route, Path, Op) 条目",
            target, Demand("D1", 5m), Graph(),
            Constraints(target, withCapacity: false),
            new Dictionary<int, List<TimeWindow>>(),
            new Dictionary<string, List<(string DemandKey, decimal ShareQty)>>(StringComparer.Ordinal));
    }

    // ─────────────────────────────────────────────────────────────
    // 出口④：延长后超出真实资源日历窗
    // ─────────────────────────────────────────────────────────────

    [Fact(DisplayName = "R03-4 · 超出日历窗 ⇒ 硬拒且五项状态零变化")]
    public void Reject_OutsideCalendar_NoSideEffect()
    {
        var target = TargetTask();
        var constraints = Constraints(target);

        // 日历窗整体挪到 T0+2h 之后 ⇒ 目标自身起点 T0 已不在任何窗内。
        constraints.ResourceCalendars[ResourceId] = new List<TimeWindow>
        {
            new TimeWindow(T0.AddHours(2), T0.AddHours(6)),
        };

        AssertRejectedWithoutSideEffect(
            "合并后的占用窗 [T0, T0+1.5h] 不落在任何资源日历窗内",
            target, Demand("D1", 5m), Graph(), constraints,
            new Dictionary<int, List<TimeWindow>>(),
            new Dictionary<string, List<(string DemandKey, decimal ShareQty)>>(StringComparer.Ordinal));
    }

    // ─────────────────────────────────────────────────────────────
    // 出口⑤：新需求自身的交期被破坏
    // ─────────────────────────────────────────────────────────────

    [Fact(DisplayName = "R03-5 · 新需求交期被破坏 ⇒ 硬拒且五项状态零变化")]
    public void Reject_NewDemandDueBroken_NoSideEffect()
    {
        var target = TargetTask();
        // 10 件 + 5 件 = 15 件 × 6 分钟 = 90 分钟 ⇒ 结束 T0+1.5h > 交期 T0+1h。
        var demand = Demand("D1", 5m, due: T0.AddHours(1));

        AssertRejectedWithoutSideEffect(
            "合并后结束时间 T0+1.5h 越过新需求交期 T0+1h（`:9871`）",
            target, demand, Graph(),
            Constraints(target),
            new Dictionary<int, List<TimeWindow>>(),
            new Dictionary<string, List<(string DemandKey, decimal ShareQty)>>(StringComparer.Ordinal),
            demandByKey: null);
    }

    // ─────────────────────────────────────────────────────────────
    // 出口⑥：锚批既有份额的交期被破坏（多成员口径）
    // ─────────────────────────────────────────────────────────────

    [Fact(DisplayName = "R03-6 · 既有份额交期被破坏 ⇒ 硬拒且五项状态零变化")]
    public void Reject_ExistingShareDueBroken_NoSideEffect()
    {
        var target = TargetTask();
        var shares = new Dictionary<string, List<(string DemandKey, decimal ShareQty)>>(StringComparer.Ordinal)
        {
            [TargetTaskId] = new List<(string, decimal)> { ("D0", 10m) },
        };

        // D0 是既有份额，交期 T0+1h；新需求 D1 自身交期放开（避免先被出口⑤拦下）。
        var demandByKey = new Dictionary<string, LogicalProductionDemand>(StringComparer.Ordinal)
        {
            ["D0"] = Demand("D0", 10m, due: T0.AddHours(1)),
            [TargetSourceId] = Demand(TargetSourceId, 10m),
            ["D1"] = Demand("D1", 5m),
        };

        AssertRejectedWithoutSideEffect(
            "合并后结束 T0+1.5h 越过既有份额 D0 的交期 T0+1h（`:9896`）",
            target, Demand("D1", 5m), Graph(),
            Constraints(target),
            new Dictionary<int, List<TimeWindow>>(),
            shares, demandByKey);
    }

    // ─────────────────────────────────────────────────────────────
    // 出口⑦：延长后把已发布的父件起点顶穿
    // ─────────────────────────────────────────────────────────────

    [Fact(DisplayName = "R03-7 · 顶穿已发布父件 ⇒ 硬拒且五项状态零变化")]
    public void Reject_ParentStartPenetrated_NoSideEffect()
    {
        var target = TargetTask();
        var constraints = Constraints(target);

        // 目标 Task 的来源需求 D2 有一个已落定的父件 P1，其起点 T0+30min。
        constraints.DemandEarliestStart["P1"] = T0.AddMinutes(30);
        constraints.CrossMaterialChildToParents[TargetSourceId] = new List<string> { "P1" };

        AssertRejectedWithoutSideEffect(
            "合并后结束 T0+1.5h + lag0 越过父件 P1 起点 T0+30min（`:9914`）",
            target, Demand("D1", 5m), Graph(), constraints,
            new Dictionary<int, List<TimeWindow>>(),
            new Dictionary<string, List<(string DemandKey, decimal ShareQty)>>(StringComparer.Ordinal),
            demandByKey: null);
    }

    // ─────────────────────────────────────────────────────────────
    // 出口⑧：与同资源上其它占用窗冲突
    // ─────────────────────────────────────────────────────────────

    [Fact(DisplayName = "R03-8 · 与同资源既有占用冲突 ⇒ 硬拒且五项状态零变化")]
    public void Reject_ResourceConflict_NoSideEffect()
    {
        var target = TargetTask();
        var occupancy = new Dictionary<int, List<TimeWindow>>
        {
            [ResourceId] = new List<TimeWindow>
            {
                // 目标自身：[T0, T0+1h]（检查时会按 (Start,End) 精确匹配跳过）
                new TimeWindow(T0, T0.AddHours(1)),
                // 后续占用：[T0+1h, T0+2h] ⇒ 合并延长到 T0+1.5h 会侵入
                new TimeWindow(T0.AddHours(1), T0.AddHours(2)),
            },
        };

        AssertRejectedWithoutSideEffect(
            "合并后 [T0, T0+1.5h] 侵入同资源既有占用 [T0+1h, T0+2h]（`:9946`）",
            target, Demand("D1", 5m), Graph(),
            Constraints(target), occupancy,
            new Dictionary<string, List<(string DemandKey, decimal ShareQty)>>(StringComparer.Ordinal),
            demandByKey: null);
    }

    // ─────────────────────────────────────────────────────────────
    // R04（0号位 §3-R04）：「合并成功但结束时刻不变」这个状态**是否可达**
    //   调用方原判据是 `mergedIntoTask = endChanged ? mergedTask : null`
    //   ⇒ 若合并真的发生而结束时刻恰好不变，就会被上报成「没有产出」。
    //   本用例只证明**状态可达**；调用方判据的修复见 `TryMergeOrSchedule` 处注释。
    // ─────────────────────────────────────────────────────────────

    [Fact(DisplayName = "R04 · PlannedProcessQty=0 时合并成功但结束时刻不变（状态可达）")]
    public void MergeCanSucceed_WhileEndTimeUnchanged()
    {
        var target = TargetTask();

        // 需求有净产出 5 件，但**计划加工量为 0** ⇒ mergedQty 仍是 10 ⇒ 时长不变 ⇒ End 不变。
        var demand = new LogicalProductionDemand
        {
            LogicalDemandKey = "D1",
            DemandKey = "D1",
            MaterialId = MaterialId,
            NetOutputQty = 5m,
            PlannedProcessQty = 0m,
            StartStageCode = Stage,
            RouteCode = "DEFAULT",
            PathId = 1,
            RequiredAvailableTime = DateTime.MaxValue,
        };

        var shares = new Dictionary<string, List<(string DemandKey, decimal ShareQty)>>(StringComparer.Ordinal);

        var merged = Scheduler.TryMergeDemandIntoTask(
            demand, target, Graph(), Constraints(target),
            new Dictionary<int, List<TimeWindow>>(), shares, T0.AddDays(30));

        Assert.True(merged is not null,
            "PlannedProcessQty=0 的合并在本夹具下应成功；若这里就为 null，说明该状态不可达，R04 不成立");

        // 合并**确实发生了**：份额已写入、净量已增加。
        Assert.True(shares.ContainsKey(merged!.FinalDraftId));
        Assert.Equal(15m, merged.Quantity);

        // 但结束时刻**一点没变** —— 正是调用方 `endChanged` 判据会误判的形态。
        Assert.Equal(target.PlannedEndTime, merged.PlannedEndTime);
        Assert.Equal(target.PlannedProcessQty, merged.PlannedProcessQty);
    }

    // ─────────────────────────────────────────────────────────────
    // A3 / R06（0号位 §3-R06、§4-A3）：合并不得提前消费未到物料
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 一正一反，证明「物料可用时间」守卫**有鉴别力**（不是一律拒绝）：
    ///   · 反：物料要到 T0+2h 才可用，而锚批起点是 T0 ⇒ 必须硬拒；
    ///   · 正：物料在 T0-2h 就已可用 ⇒ 必须合并成功。
    /// 两次用的是**同一个** `GetMaterialEarliestTime` 口径（数量取 `NetOutputQty`）。
    /// </summary>
    [Fact(DisplayName = "R06 · 物料未到不得合并（正反各一）")]
    public void MergeRejected_WhenMaterialNotYetAvailable()
    {
        static ConstraintContext WithMaterial(FinalTaskDraft target, DateTime availableAt)
        {
            var c = Constraints(target);
            c.MaterialAvailability[AllocSeq] = new List<MaterialAvailabilitySegment>
            {
                new() { Quantity = 100m, AvailableTime = availableAt }
            };
            return c;
        }

        // ── 反：物料 T0+2h 才到，锚批 T0 就开始 ⇒ 拒 ──
        var target = TargetTask();
        var rejected = Scheduler.TryMergeDemandIntoTask(
            Demand("D1", 5m), target, Graph(), WithMaterial(target, T0.AddHours(2)),
            new Dictionary<int, List<TimeWindow>>(),
            new Dictionary<string, List<(string DemandKey, decimal ShareQty)>>(StringComparer.Ordinal),
            T0.AddDays(30), planningStart: T0);

        Assert.True(rejected is null,
            "物料要到 T0+2h 才可用，锚批却在 T0 就开始 —— 合并**必须**被拒（否则提前消费未到物料）");

        // ── 正：物料 T0-2h 已可用 ⇒ 合 ──
        var target2 = TargetTask();
        var shares2 = new Dictionary<string, List<(string DemandKey, decimal ShareQty)>>(StringComparer.Ordinal);
        var accepted = Scheduler.TryMergeDemandIntoTask(
            Demand("D1", 5m), target2, Graph(), WithMaterial(target2, T0.AddHours(-2)),
            new Dictionary<int, List<TimeWindow>>(), shares2,
            T0.AddDays(30), planningStart: T0);

        Assert.True(accepted is not null,
            "物料在 T0-2h 就已可用，锚批 T0 起 —— 本应合并成功；若这里被拒，说明守卫把合格合批也杀了");
        Assert.Equal(15m, accepted!.Quantity);
    }

    // ─────────────────────────────────────────────────────────────
    // R05（0号位 §3-R05）：客户 DATE 交期必须与 Phase3/Phase5 同口径
    //   合并侧原用裸 `RequiredAvailableTime`（00:00），其它各处用 `EffectiveDueJudgementBound`
    //   （交期日整天，次日零点排他上界）⇒ 同一笔延期在合并路径上被多判一次。
    // ─────────────────────────────────────────────────────────────

    [Fact(DisplayName = "R05 · 客户DATE交期按整天判：跨零点合并不得被误拒")]
    public void MergeAllowed_WhenDueIsCustomerDate_CrossingMidnight()
    {
        // 任务 11-09 23:00 开始、11-10 00:00 结束；再并入 5 件 ⇒ 00:30 结束。
        var dayStart = new DateTime(2026, 11, 9, 23, 0, 0);

        var target = new FinalTaskDraft
        {
            FinalDraftId = TargetTaskId,
            SourceDraftId = TargetSourceId,
            MaterialId = MaterialId,
            ResourceId = ResourceId,
            StageCode = Stage,
            OperationCode = Op,
            PlannedStartTime = dayStart,
            PlannedEndTime = dayStart.AddHours(1),      // 11-10 00:00
            SetupTime = 0m,
            Quantity = 10m,
            PlannedProcessQty = 10m,
            ExecutionBatchDraftKey = "EB|D2|001",
        };

        var constraints = new ConstraintContext();
        constraints.ResourceCapacityFactors[new EligibilityLookupKey(MaterialId, 1, "DEFAULT", 1, Op)]
            = new Dictionary<int, decimal> { [ResourceId] = 1m };
        constraints.ResourceCalendars[ResourceId] = new List<TimeWindow>
        {
            new TimeWindow(dayStart.AddHours(-1), dayStart.AddHours(6)),
        };
        constraints.ProductTimeline = ResourceProductTimeline.FromTasks(new[] { target });

        var demand = new LogicalProductionDemand
        {
            LogicalDemandKey = "D1",
            DemandKey = "D1",
            AllocationSequence = AllocSeq,
            MaterialId = MaterialId,
            NetOutputQty = 5m,
            PlannedProcessQty = 5m,
            StartStageCode = Stage,
            RouteCode = "DEFAULT",
            PathId = 1,
            // **客户 DATE 交期**：当天零点（没有任何时刻成分）。
            RequiredAvailableTime = new DateTime(2026, 11, 10, 0, 0, 0),
        };

        var merged = Scheduler.TryMergeDemandIntoTask(
            demand, target, Graph(), constraints,
            new Dictionary<int, List<TimeWindow>>(),
            new Dictionary<string, List<(string DemandKey, decimal ShareQty)>>(StringComparer.Ordinal),
            dayStart.AddDays(30), planningStart: dayStart);

        Assert.True(merged is not null,
            "客户 DATE 交期 11-10 应按**当天整天**判按期（次日零点排他上界）；"
            + "合并后 00:30 完成仍在该日内 ⇒ 不得拒绝（R05：与 Phase3/Phase5 同口径）");
        Assert.Equal(new DateTime(2026, 11, 10, 0, 30, 0), merged!.PlannedEndTime);
    }

    // ─────────────────────────────────────────────────────────────
    // 出口⑨：同一需求重复并入同一目标
    // ─────────────────────────────────────────────────────────────

    [Fact(DisplayName = "R03-9 · 同需求重复并入 ⇒ 硬拒且五项状态零变化")]
    public void Reject_DuplicateShare_NoSideEffect()
    {
        var target = TargetTask();
        var shares = new Dictionary<string, List<(string DemandKey, decimal ShareQty)>>(StringComparer.Ordinal)
        {
            [TargetTaskId] = new List<(string, decimal)> { ("D1", 5m) },
        };

        AssertRejectedWithoutSideEffect(
            "该 Task 的份额里已有 D1，再次并入会重复计份额（纵深防御出口）",
            target, Demand("D1", 5m), Graph(),
            Constraints(target),
            new Dictionary<int, List<TimeWindow>>(),
            shares, demandByKey: null);
    }
}