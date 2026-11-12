using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Shared.Models;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 2026-11-07 C2 数量闭合证据（0号位《54c687e 六项答复与继续整改指令》§四 第 1 步验收项：
/// 「同需求前批合并、后批失败、**同目标重复合并**；数量闭合」）。
///
/// <para>【测什么】直接调 <c>PhaseTwoInitialScheduler.TryMergeDemandIntoTask</c>（已由 private 改 internal），
/// 观察合并落定后 <c>allocationTaskShare</c> 里该需求的份额**是否只增加一次、金额等于本批净量**。</para>
///
/// <para>【为什么单独测这条】合并分支里那一句是无条件
/// <c>mergedShareNext.Add((demand.LogicalDemandKey, demand.NetOutputQty))</c> —— 没有去重检查。
/// 若同一需求能被合并进同一个目标 Task 两次，同一 `DemandKey` 就会在该 Task 上出现两条份额
/// ⇒ **数量闭合被破坏**（∑Share 超过需求净量）。代码里另有一道「目标必须是另一条需求」+
/// 「目标已属别的执行批则拒绝」的守卫，本用例就是把那道守卫**测出来**，而不是只靠读代码断言它存在。</para>
///
/// <para>【不碰生产库】纯内存夹具，零 DB。</para>
/// </summary>
public sealed class BatchMergeCommitTests
{
    private static PhaseTwoInitialScheduler Scheduler => new PhaseTwoInitialScheduler();

    private static readonly DateTime T0 = new DateTime(2026, 11, 10, 8, 0, 0);
    private const int ResourceId = 1;
    private const int MaterialId = 100;
    private const string Stage = "S1";
    private const string Op = "OP10";

    private static FinalTaskDraft TargetTask() => new FinalTaskDraft
    {
        FinalDraftId = "T-D2",
        SourceDraftId = "D2",
        MaterialId = MaterialId,
        ResourceId = ResourceId,
        StageCode = Stage,
        OperationCode = Op,
        PlannedStartTime = T0,
        PlannedEndTime = T0.AddHours(1),
        SetupTime = 0m,
        Quantity = 10m,
        PlannedProcessQty = 10m,
        ExecutionBatchDraftKey = "EB|D2|001",
    };

    private static LogicalProductionDemand Demand(string key, decimal qty, DateTime? due = null)
        => new LogicalProductionDemand
    {
        LogicalDemandKey = key,
        DemandKey = key,
        MaterialId = MaterialId,
        NetOutputQty = qty,
        PlannedProcessQty = qty,
        // ScheduleDemandOperations 靠 StartStageCode 定位工序图起点；缺它会直接零产出
        StartStageCode = Stage,
        RouteCode = "DEFAULT",
        PathId = 1,
        // init-only：必须在初始化器里赋
        RequiredAvailableTime = due ?? DateTime.MaxValue,
    };

    private static RoutingGraph Graph()
    {
        var g = new RoutingGraph();
        g.Operations[OperationNodeKey.Of(Stage, Op)] = new OperationNode
        {
            OperationCode = Op,
            StageCode = Stage,
            StandardDuration = 6m,          // 分钟/件
            OperationPlanningMode = "FINITE_RESOURCE",
            RouteCode = "DEFAULT",
            PathId = 1,
            ProductionDepartmentId = 1,
        };
        return g;
    }

    private static ConstraintContext Constraints()
    {
        var c = new ConstraintContext();
        c.ResourceCapacityFactors[new EligibilityLookupKey(MaterialId, 1, "DEFAULT", 1, Op)]
            = new Dictionary<int, decimal> { [ResourceId] = 1m };
        c.ResourceCalendars[ResourceId] = new List<TimeWindow>
        {
            new TimeWindow(T0.AddHours(-8), T0.AddHours(20)),
        };
        return c;
    }

    [Fact(DisplayName = "C2 · 首次合并：份额只增加一条，金额＝本批净量")]
    public void FirstMerge_AddsExactlyOneShare()
    {
        var target = TargetTask();
        var shares = new Dictionary<string, List<(string DemandKey, decimal ShareQty)>>(StringComparer.Ordinal);
        var occupancy = new Dictionary<int, List<TimeWindow>>();
        var d1 = Demand("D1", 5m);

        var merged = Scheduler.TryMergeDemandIntoTask(
            d1, target, Graph(), Constraints(), occupancy, shares, T0.AddDays(30));

        Assert.NotNull(merged);
        Assert.True(shares.TryGetValue(merged!.FinalDraftId, out var list), "合并后份额表里必须有该 Task 的条目");

        var d1Shares = list!.Where(s => s.DemandKey == "D1").ToList();
        Assert.Single(d1Shares);
        Assert.Equal(5m, d1Shares[0].ShareQty);

        // ── C2-01（0号位 v2.0 §2.3）：**完整构成**，不只看新增的 D1 ──
        //   「目标D2=10 + 首次D1=5 单Operation合并 ⇒ 合批 15；源构成 D2=10、D1=5；无额外重复供给」
        //   合并后 Task 的业务量必须是 10+5=15（一份实物），D2 记在 SourceDraftId（本 Task 的归属需求），
        //   D1 记在分配份额里 —— 两者相加恰等于 15，不多不少。
        Assert.Equal("D2", merged!.SourceDraftId);
        Assert.Equal(15m, merged.PlannedProcessQty);

        var d2Part = merged.PlannedProcessQty - d1Shares.Sum(x => x.ShareQty);
        Assert.Equal(10m, d2Part);   // 原目标 D2 的 10 件必须原样保留，不被吃掉
    }

    [Fact(DisplayName = "C2-05 · 合并被硬约束拒绝（交期）⇒ 份额/占用/任务全部不动")]
    public void MergeRejectedByHardConstraint_LeavesStateUntouched()
    {
        var target = TargetTask();
        var shares = new Dictionary<string, List<(string DemandKey, decimal ShareQty)>>(StringComparer.Ordinal);
        var occupancy = new Dictionary<int, List<TimeWindow>>
        {
            [ResourceId] = new List<TimeWindow> { new TimeWindow(T0, T0.AddHours(1)) },
        };
        var constraints = Constraints();

        // D1 的有效交期压在「合并后必然超期」的位置：
        //   目标 10 件 + 新增 5 件 ⇒ 合并后时长 6min/件 × 15 = 90min，起点 T0 ⇒ 结束 T0+1.5h > 交期 T0+1h
        //   ⇒ `TryMergeDemandIntoTask` 必须按 P0-04（交期不得被破坏）拒绝。
        var d1 = Demand("D1", 5m, due: T0.AddHours(1));

        var beforeOccupancy = occupancy[ResourceId].Select(w => (w.Start, w.End)).ToList();

        var merged = Scheduler.TryMergeDemandIntoTask(
            d1, target, Graph(), constraints, occupancy, shares, T0.AddDays(30));

        Assert.Null(merged);                                    // 硬拒
        Assert.Empty(shares);                                   // 份额表**一条都不许留**
        Assert.Equal(beforeOccupancy,
            occupancy[ResourceId].Select(w => (w.Start, w.End)).ToList());   // 占用窗逐项不变
    }

    // ══════════════════════════════════════════════════════════════════
    //  C2-04：三 Operation Stage 合批 —— 实物总量只算一份
    //  按 0号位 2026-11-07 答复 §Q1 的完整清单构造：
    //   ① targetBatch 必须是**整批 3 道 Operation**（不是逐道调 3 次）
    //   ② 必须同时填 OperationResourceEligibility（GetEligibleResources 读它）
    //      与 ResourceCapacityFactors（效率系数），两者资源 ID 要对上
    //   ③ resourceOccupancy 必须有该 ResourceId 的键且与既有 Task 时间一致
    //   ④ Graph 要有 OP10→OP20→OP30 依赖
    // ══════════════════════════════════════════════════════════════════

    private static readonly string[] StageOps = { "OP10", "OP20", "OP30" };

    private static List<OperationNode> ThreeOps()
        => StageOps.Select(op => new OperationNode
        {
            OperationCode = op,
            StageCode = Stage,
            StandardDuration = 1m,          // **分钟/件**（Solver 输入口径），不是秒
            OperationPlanningMode = "FINITE_RESOURCE",
            RouteCode = "DEFAULT",
            PathId = 1,
            ProductionDepartmentId = 1,
        }).ToList();

    private static RoutingGraph ThreeOpGraph()
    {
        var g = new RoutingGraph();
        foreach (var op in ThreeOps()) g.Operations[OperationNodeKey.Of(Stage, op.OperationCode)] = op;

        // DAG：OP20 依赖 OP10，OP30 依赖 OP20（Dependencies 是「To → 前驱列表」）
        g.Dependencies[OperationNodeKey.Of(Stage, "OP20")] = new List<DependencyEdge>
        {
            new DependencyEdge { From = OperationNodeKey.Of(Stage, "OP10"), To = OperationNodeKey.Of(Stage, "OP20") },
        };
        g.Dependencies[OperationNodeKey.Of(Stage, "OP30")] = new List<DependencyEdge>
        {
            new DependencyEdge { From = OperationNodeKey.Of(Stage, "OP20"), To = OperationNodeKey.Of(Stage, "OP30") },
        };
        g.RootOperations.Add(OperationNodeKey.Of(Stage, "OP10"));
        return g;
    }

    /// <summary>资格表与效率系数**两张都填**（0号位 §Q1 点名：只填 CapacityFactors 会拿到空资格）。</summary>
    private static ConstraintContext ThreeOpConstraints()
    {
        var c = new ConstraintContext();
        foreach (var op in StageOps)
        {
            var key = new EligibilityLookupKey(MaterialId, 1, "DEFAULT", 1, op);
            c.OperationResourceEligibility[key] = new List<int> { ResourceId };   // 资格
            c.ResourceCapacityFactors[key] = new Dictionary<int, decimal> { [ResourceId] = 1m };  // 效率系数
        }
        c.ResourceCalendars[ResourceId] = new List<TimeWindow> { new TimeWindow(T0, T0.AddHours(48)) };
        return c;
    }

    /// <summary>既有 Stage 执行批：**三道工序各一条 Task、各 50 件（A30+B20）、共用同一批键**，
    /// 时间首尾相接（1min/件 × 50 = 50min）。</summary>
    private static List<FinalTaskDraft> AnchorStageBatch()
    {
        var list = new List<FinalTaskDraft>();
        var cursor = T0;
        foreach (var op in StageOps)
        {
            list.Add(new FinalTaskDraft
            {
                FinalDraftId = "T-A-" + op,
                SourceDraftId = "A",
                MaterialId = MaterialId,
                ResourceId = ResourceId,
                StageCode = Stage,
                OperationCode = op,
                PlannedStartTime = cursor,
                PlannedEndTime = cursor.AddMinutes(50),
                SetupTime = 0m,
                Quantity = 50m,
                PlannedProcessQty = 50m,
                ExecutionBatchDraftKey = "EB|A|001",
            });
            cursor = cursor.AddMinutes(50);
        }
        return list;
    }

    [Fact(DisplayName = "C2-04深度回滚 · 前两道试排成功、第三道因日历无槽失败 ⇒ 全状态逐项还原")]
    public void StageMerge_ThirdOperationFails_RestoresEverything()
    {
        var constraints = ThreeOpConstraints();
        // ── 日历 165min（0号位 v3.0 §2.1 指定口径）──
        //   **原批合法**：3 道 × 50min = 150min ≤ 165min ✓（原批 08:00~10:30 落在窗内）
        //   **合并后不合法**：3 道 × 60min = 180min > 165min ✗ ⇒ 第三道必然无槽
        //   ⚠ 用 130min 会让**原批本身也非法**，那样的「Before」是坏的、测试没有证明力（v3.0 点名）。
        constraints.ResourceCalendars[ResourceId] = new List<TimeWindow>
        {
            new TimeWindow(T0, T0.AddMinutes(165)),
        };

        var targetBatch = AnchorStageBatch();
        var occupancy = new Dictionary<int, List<TimeWindow>>
        {
            [ResourceId] = targetBatch.Select(t => new TimeWindow(t.PlannedStartTime, t.PlannedEndTime)).ToList(),
        };
        var scheduled = new List<FinalTaskDraft>(targetBatch);
        var shares = new Dictionary<string, List<(string DemandKey, decimal ShareQty)>>(StringComparer.Ordinal);
        foreach (var t in targetBatch)
        {
            shares[t.FinalDraftId] = new List<(string, decimal)> { ("A", 30m), ("B", 20m) };
        }
        var demandByKey = new Dictionary<string, LogicalProductionDemand>(StringComparer.Ordinal)
        {
            ["A"] = Demand("A", 30m),
        };

        // ── 进入前完整快照 ──
        var beforeTaskIds = scheduled.Select(t => t.FinalDraftId).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var beforeTaskQty = scheduled.ToDictionary(t => t.FinalDraftId, t => t.PlannedProcessQty);
        var beforeOcc = occupancy[ResourceId].Select(w => (w.Start, w.End)).ToList();
        var beforeShares = shares.ToDictionary(
            kv => kv.Key,
            kv => string.Join(";", kv.Value.Select(x => x.DemandKey + "=" + x.ShareQty)));
        // ── 初始 ProductTimeline 必须与既有 Task 一致（v3.0 §3.1-3）──
        //   原来留空 ⇒ 「Before」是坏的，回滚前后必然不等，测不出真实还原。
        constraints.ProductTimeline = ResourceProductTimeline.FromTasks(targetBatch);

        // ── 0号位 v3.0 §3.1-3：**先断言夹具初始状态已经合法**，否则测试没有证明力 ──
        //   原批 3 道共 150min，日历 165min ⇒ 每条 Task 都必须完整落在窗内、且首尾相接无重叠。
        var cal = constraints.ResourceCalendars[ResourceId][0];
        foreach (var t in targetBatch)
        {
            Assert.True(t.PlannedStartTime >= cal.Start && t.PlannedEndTime <= cal.End,
                $"初始夹具非法：Task {t.FinalDraftId} [{t.PlannedStartTime:HH:mm}~{t.PlannedEndTime:HH:mm}] 不在日历窗内");
        }
        Assert.True(targetBatch[^1].PlannedEndTime <= cal.End, "初始夹具非法：原批整体超出日历窗");

        // ── ProductTimeline 的可观察内容指纹（不用 Assert.NotNull 糊过去，见 v3.0 §2.1）──
        //   探测点覆盖：原批起点前 / 每道 Task 结束点前后 / 原批终点后（同产品、不同产品边界都落在这些点上）
        var probes = new[]
        {
            T0.AddMinutes(-1), T0, T0.AddMinutes(49), T0.AddMinutes(50), T0.AddMinutes(51),
            T0.AddMinutes(99), T0.AddMinutes(100), T0.AddMinutes(101),
            T0.AddMinutes(149), T0.AddMinutes(150), T0.AddMinutes(151), T0.AddMinutes(200),
        };
        string TimelineFingerprint()
        {
            var tl = constraints.ProductTimeline;
            return string.Join("|", probes.Select(x => x.ToString("HH:mm") + "=>" + (tl.GetPrevMaterial(ResourceId, x)?.ToString() ?? "null")));
        }
        var timelineBefore = TimelineFingerprint();

        var merged = Scheduler.TryMergeDemandIntoStageBatch(
            Demand("C", 10m), ThreeOps(), ThreeOpGraph(), "FORWARD", targetBatch, constraints,
            occupancy, scheduled, shares, demandByKey,
            T0, T0.AddDays(30), T0, new StageOverlapParams(), null,
            out var combinedTasks);

        // ── ① 失败：返回 null 且零产出 ──
        Assert.Null(merged);
        Assert.Empty(combinedTasks);

        // ── ② 任务集合逐项还原 ──
        Assert.Equal(beforeTaskIds,
            scheduled.Select(t => t.FinalDraftId).OrderBy(x => x, StringComparer.Ordinal).ToList());
        foreach (var t in scheduled)
        {
            Assert.Equal(beforeTaskQty[t.FinalDraftId], t.PlannedProcessQty);
        }

        // ── ③ 占用窗逐项还原 ──
        Assert.Equal(beforeOcc, occupancy[ResourceId].Select(w => (w.Start, w.End)).ToList());

        // ── ④ 份额逐项还原（A30+B20 原样，未留 C10 残迹）──
        Assert.Equal(beforeShares.Count, shares.Count);
        foreach (var kv in beforeShares)
        {
            Assert.True(shares.TryGetValue(kv.Key, out var now), $"份额键 {kv.Key} 丢失");
            Assert.Equal(kv.Value, string.Join(";", now!.Select(x => x.DemandKey + "=" + x.ShareQty)));
        }

        // ── ⑤ ProductTimeline：**可观察内容等价**（不是 Assert.NotNull，也不是 Assert.Same）──
        //   实例不同是设计如此（`PhaseTwoInitialScheduler.cs:9879` 入口 `Clone()`、回滚赋它），
        //   但**内容**必须一致 —— 用真实消费语义 `GetPrevMaterial(resourceId, time)` 在 12 个探测点上逐点比对。
        Assert.Equal(timelineBefore, TimelineFingerprint());
    }

    [Fact(DisplayName = "C2-04 · 三 Operation Stage 合批：A30+B20 已批 + C10 并入 ⇒ 每道 60 件、业务总量 60")]
    public void StageMerge_ThreeOperations_CountsPhysicalOnce()
    {
        var constraints = ThreeOpConstraints();
        var targetBatch = AnchorStageBatch();

        // 占用窗与既有 Task 一一对应（0号位 §Q1：不能只给 scheduledTasks 而占用账全空）
        var occupancy = new Dictionary<int, List<TimeWindow>>
        {
            [ResourceId] = targetBatch.Select(t => new TimeWindow(t.PlannedStartTime, t.PlannedEndTime)).ToList(),
        };
        var scheduled = new List<FinalTaskDraft>(targetBatch);

        // 既有批的规范需求构成：每道 Task 上 A30 + B20
        var shares = new Dictionary<string, List<(string DemandKey, decimal ShareQty)>>(StringComparer.Ordinal);
        foreach (var t in targetBatch)
        {
            shares[t.FinalDraftId] = new List<(string, decimal)> { ("A", 30m), ("B", 20m) };
        }

        var demandByKey = new Dictionary<string, LogicalProductionDemand>(StringComparer.Ordinal)
        {
            ["A"] = Demand("A", 30m),   // 锚点需求（可被 CloneDemandWithBatchQty 克隆）
            // ── 2026-11-10 P0-R1 补齐 ──
            //   上面 `shares` 里每道 Task 都有 `A30 + B20`，但原夹具只建了 "A"。
            //   以前 `TryMergeDemandIntoStageBatch` 对查不到的份额成员是 `continue`（Fail-Open）
            //   ⇒ 本用例能在「B 的来源无从追溯」的情况下照样跑绿 —— 正是 0号位 P0-R1 指出的那种放行。
            //   改为 Fail Closed 后，缺 "B" 会让整锚被拒（返回 null），用例红得对。
            //   ⇒ 补齐 "B"，让夹具反映真实的「份额成员一定来自输入需求」这一前提。
            ["B"] = Demand("B", 20m),
        };

        // **一次调用**把 C10 并入整批（不是逐道调三次）
        var merged = Scheduler.TryMergeDemandIntoStageBatch(
            Demand("C", 10m), ThreeOps(), ThreeOpGraph(), "FORWARD", targetBatch, constraints,
            occupancy, scheduled, shares, demandByKey,
            T0, T0.AddDays(30), T0, new StageOverlapParams(), null,
            out var combinedTasks);

        Assert.NotNull(merged);
        Assert.Equal(3, combinedTasks.Count);   // 三道工序各一条

        // ① 每道 Operation 的**实物量都是 60**（A30+B20+C10），不是把三道相加当 180
        Assert.All(combinedTasks, t => Assert.Equal(60m, t.PlannedProcessQty));

        // ② 业务总量只算一份：三道的 PlannedProcessQty 之和 ÷ 工序数 = 60，而不是 180
        //    ⚠ 这里不是「算平均值冒充闭合」—— 因为三道时必须**逐道等于 60** 已由 ① 钉死，
        //      本行只是把「170/180 这种重复累加」显式挡住。
        Assert.Equal(3 * 60m, combinedTasks.Sum(t => t.PlannedProcessQty));

        // ③ 按 DemandKey × Operation 逐项验构成（0号位 §3 要求，不用平均值代替）
        foreach (var t in combinedTasks)
        {
            Assert.True(shares.TryGetValue(t.FinalDraftId, out var list), $"Task {t.FinalDraftId} 缺份额构成");
            var byKey = list!.GroupBy(x => x.DemandKey).ToDictionary(g => g.Key, g => g.Sum(x => x.ShareQty));
            Assert.Equal(30m, byKey["A"]);
            Assert.Equal(20m, byKey["B"]);
            Assert.Equal(10m, byKey["C"]);
            Assert.Equal(60m, byKey.Values.Sum());          // 构成之和 = 该道实物量
            Assert.Equal(t.PlannedProcessQty, byKey.Values.Sum());   // 两者必须相等，不得超出
        }
    }

    /// <summary>
    /// **C2-02**：同 `DemandKey` 同目标再合并必须被拒（纵深防御）。
    ///
    /// v3.0 §2.1 C2-02 逐字要求：「断言 `null` 且**拒绝前后**任务值、所有份额、资源占用、
    /// **ProductTimeline 查询**及 **TraceNotes** 一致，不只比较 D1 数量。」
    ///
    /// ⚠ 本用例第一版只比了「D1 份额条数 + 合计金额」，并把 `second is null || all == 5m`
    ///   写成「两种情况都算过」——这正是 v3.0 §3.2 批的「伪绿」：
    ///   只要金额没变，**即使第二次真的被接受**也判绿。已推翻重做：
    ///   · 拒绝判据必须是 `second is null`（严格）；
    ///   · 五项状态**逐项对拍**：Task 字段 / 全部份额 / 资源占用窗 / ProductTimeline 可观察查询 / TraceNotes。
    /// </summary>
    [Fact(DisplayName = "C2-02 · 同目标重复合并被拒：Task/份额/占用/ProductTimeline/TraceNotes 五项逐项不变")]
    public void DuplicateMergeIntoSameTarget_DoesNotDoubleCount()
    {
        var target = TargetTask();
        var shares = new Dictionary<string, List<(string DemandKey, decimal ShareQty)>>(StringComparer.Ordinal);
        var occupancy = new Dictionary<int, List<TimeWindow>>();
        var constraints = Constraints();

        // ProductTimeline 必须有**真实初值**才能做内容对拍（空对象上比较等于没比较）。
        constraints.ProductTimeline = ResourceProductTimeline.FromTasks(new[] { target });

        // 第一次合并：D1 的 5 件进目标 Task
        var first = Scheduler.TryMergeDemandIntoTask(
            Demand("D1", 5m), target, Graph(), constraints, occupancy, shares, T0.AddDays(30));
        Assert.NotNull(first);

        // ── 拒绝前快照（五项）──
        string TaskSig(FinalTaskDraft t) =>
            $"{t.FinalDraftId}|{t.SourceDraftId}|{t.ResourceId}|{t.StageCode}|{t.OperationCode}"
            + $"|{t.PlannedStartTime:O}|{t.PlannedEndTime:O}|{t.SetupTime}|{t.Quantity}"
            + $"|{t.PlannedProcessQty}|{t.ExecutionBatchDraftKey}";

        string ShareSig() => string.Join(";", shares
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key + "=" + string.Join(",", kv.Value
                .Select(s => $"{s.DemandKey}:{s.ShareQty}"))));

        string OccupancySig() => string.Join(";", occupancy
            .OrderBy(kv => kv.Key)
            .Select(kv => kv.Key + "=[" + string.Join(",", kv.Value
                .Select(w => $"{w.Start:O}-{w.End:O}")) + "]"));

        // ProductTimeline：用**真实消费语义**做内容指纹（`GetPrevMaterial`），覆盖目标 Task 起止点前后。
        //   ⚠ 不能用 Assert.NotNull 代替内容比较，也不要求实例相同（回滚会 Clone）。
        var probes = new[]
        {
            target.PlannedStartTime.AddMinutes(-1), target.PlannedStartTime,
            target.PlannedStartTime.AddMinutes(30), target.PlannedEndTime,
            target.PlannedEndTime.AddMinutes(1),
        };
        string TimelineSig() => string.Join("|", probes.Select(x =>
            x.ToString("HH:mm") + "=>" + (constraints.ProductTimeline.GetPrevMaterial(ResourceId, x)?.ToString() ?? "null")));

        string TraceSig() => string.Join(";", constraints.TraceNotes
            .Select(n => $"{n.Key}|{n.ReasonCode}|{n.Level}|{n.Message}"));

        var taskBefore = TaskSig(first!);
        var shareBefore = ShareSig();
        var occBefore = OccupancySig();
        var tlBefore = TimelineSig();
        var traceBefore = TraceSig();

        // 第二次：同一需求再合并一次（模拟「同需求的两批先后命中同一目标」）
        var second = Scheduler.TryMergeDemandIntoTask(
            Demand("D1", 5m), first!, Graph(), constraints, occupancy, shares, T0.AddDays(30));

        // ── ① 严格拒绝（不允许「接受但没加钱」蒙混）──
        Assert.True(second is null,
            "同一需求再次合并进同一目标**未被拒绝** ⇒ 纵深防御失效"
            + $"（份额现状：{ShareSig()}）");

        // ── ② 五项状态逐项对拍 ──
        Assert.Equal(taskBefore, TaskSig(first!));
        Assert.Equal(shareBefore, ShareSig());
        Assert.Equal(occBefore, OccupancySig());
        Assert.Equal(tlBefore, TimelineSig());
        Assert.Equal(traceBefore, TraceSig());

        // ── ③ 数量闭合（原先唯一断言，保留）──
        var d1 = shares.Values.SelectMany(v => v).Where(s => s.DemandKey == "D1").ToList();
        Assert.Single(d1);
        Assert.Equal(5m, d1.Sum(s => s.ShareQty));
        Assert.Equal(5m, shares.Values.SelectMany(v => v).Sum(s => s.ShareQty));
    }

    /// <summary>
    /// 2026-11-10 P0-R1 反证（0号位《63c130e1 Batch Code Audit Guidance v1.0》§二）：
    /// **份额里的成员键在 <c>demandByKey</c> 里查不到 ⇒ 整锚拒绝，且状态零变化。**
    ///
    /// <para>【为什么要这条】原实现对查不到的成员是 <c>continue</c>（Fail-Open）——
    /// 等于「资料缺失 ⇒ 交期守卫放行 ⇒ 仍宣称物理可行」。0号位 明令收口。</para>
    ///
    /// <para>【鉴别力】把生产代码里那处 Fail Closed 改回 <c>continue</c>，本用例**立刻变红**。</para>
    ///
    /// <para>【不碰数据库】纯内存夹具，零 DB。</para>
    /// </summary>
    [Fact(DisplayName = "P0-R1 · 份额成员缺 demandByKey 键 ⇒ 整锚拒绝（Fail Closed）")]
    public void StageMerge_MemberKeyMissing_MustRejectAnchor()
    {
        var constraints = ThreeOpConstraints();
        var targetBatch = AnchorStageBatch();

        var occupancy = new Dictionary<int, List<TimeWindow>>
        {
            [ResourceId] = targetBatch.Select(t => new TimeWindow(t.PlannedStartTime, t.PlannedEndTime)).ToList(),
        };
        var scheduled = new List<FinalTaskDraft>(targetBatch);

        // 每道 Task 上 A30 + B20 —— 与 `StageMerge_ThreeOperations_CountsPhysicalOnce` 同形
        var shares = new Dictionary<string, List<(string DemandKey, decimal ShareQty)>>(StringComparer.Ordinal);
        foreach (var t in targetBatch)
        {
            shares[t.FinalDraftId] = new List<(string, decimal)> { ("A", 30m), ("B", 20m) };
        }

        // ⚠ 故意**只建 A、不建 B** —— 这就是「份额里有该成员、但其来源无从追溯」的现场
        var demandByKey = new Dictionary<string, LogicalProductionDemand>(StringComparer.Ordinal)
        {
            ["A"] = Demand("A", 30m),
        };

        string TaskSig() => string.Join("|", scheduled.Select(t => t.FinalDraftId + ":" + t.PlannedProcessQty));
        string ShareSig() => string.Join("|", shares.OrderBy(k => k.Key, StringComparer.Ordinal)
            .Select(k => k.Key + "=" + string.Join(";", k.Value.Select(x => x.DemandKey + ":" + x.ShareQty))));
        var taskBefore = TaskSig();
        var shareBefore = ShareSig();

        using var scope = SolverDiagnostics.BeginScope();
        var counters = scope.Counters;

        var merged = Scheduler.TryMergeDemandIntoStageBatch(
            Demand("C", 10m), ThreeOps(), ThreeOpGraph(), "FORWARD", targetBatch, constraints,
            occupancy, scheduled, shares, demandByKey,
            T0, T0.AddDays(30), T0, new StageOverlapParams(), null,
            out var combinedTasks);

        // ── ① 必须拒绝（Fail Closed）──
        Assert.True(merged is null,
            "份额成员 B 在 demandByKey 里查不到，整锚**应当**被拒；"
            + "返回非 null 说明退回了 Fail-Open —— 拿没核过的成员当物理可行。");

        // ── ② 零产出 ──
        Assert.Empty(combinedTasks);

        // ── ③ 五项状态零变化（这里能直接观察的是任务表与份额表）──
        Assert.Equal(taskBefore, TaskSig());
        Assert.Equal(shareBefore, ShareSig());

        // ── ④ 异常键必须被计数到，不能悄悄吞掉 ──
        Assert.True(counters.MergeMemberKeyMissing > 0,
            $"缺键应当被登记，实得 {counters.MergeMemberKeyMissing} 次。");
    }
}