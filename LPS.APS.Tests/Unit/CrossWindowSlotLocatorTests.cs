using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Shared.Models;
using Xunit;
using OperationResourceEligibility = LPS.APS.Core.Entities.APS.OperationResourceEligibility;
using RoutingOperation = LPS.APS.Core.Entities.APS.RoutingOperation;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **PV195 跨窗找槽（2026-10-29）** 的反证。分两段：
///
/// <para><b>① 纯函数段</b>（<see cref="CrossWindowSlotLocator"/>）：正排「凑够有效分钟」、闸门、跳过占用、
/// 日历走完、不乱序改写调用方列表，逐条钉死。这些是**边界**，不是「跑一下看看」。</para>
///
/// <para><b>② 端到端段</b>：PV195 的真实形状 —— 单个 Task 的有效加工（600 分钟）**长于该资源最长日历窗**
/// （两个 300 分钟的窗）。改动前这条路恒为 0 Task；改动后必须排出 1 个 Task，
/// 且它的墙钟跨度（900 分钟）**大于**有效加工（600 分钟），差额正是两个窗之间的非工作空档。</para>
///
/// <para><b>口径来源（1号位 2026-10-25 跨窗裁决，抄送 0号位）</b>：
/// `Start`/`End` 是真实墙钟；`Duration`/`EffectiveProcessMinutes` 是**有效加工**，二者**不相等**；
/// 占用记**连续一段** `[Start, End]`（窗间空档也占住）；窗间中断由评估层 `CrossCalendarInterrupt` 扣减；
/// **不新增任何契约字段**。故本文件里断言「墙钟跨度 &gt; 有效加工」不是凑数，而是这条口径的直接判据。</para>
///
/// <para><b>闸门（两层，刻意收窄）</b>：外层是 `ConstraintContext.AllowCrossWindowSlot`（默认 false，
/// 只有 Phase4「常规修复一个 Task 都没排出」的重试才临时置 true）；内层是「时长**长于该资源任何一个日历窗**」。
/// 仅仅是「没抢到空档 / 被占用挤满」的仍如实排不下 —— 由 <c>F2</c> 与端到端段共同锁住，
/// 防止本次改动偷偷扩大行为面。</para>
///
/// <para><b>端到端为什么走得到</b>：Phase2 常态找槽**不开**跨窗（开了会让 6 个既有用例变形，已实测），
/// 所以这条需求先落进未排程，再由 Phase4 修复重试时开开关排下。</para>
/// </summary>
public class CrossWindowSlotLocatorTests
{
    private static readonly DateTime Day = new(2026, 10, 19);

    private static DateTime At(int minutes) => Day.AddMinutes(minutes);

    private static TimeWindow W(int fromMinute, int toMinute) => new(At(fromMinute), At(toMinute));

    /// <summary>两个 60 分钟窗：[08:00,09:00] 与 [10:00,11:00]，中间空 60 分钟。</summary>
    private static List<TimeWindow> TwoHours() => new() { W(0, 60), W(120, 180) };

    /// <summary>三个 60 分钟窗：[08:00,09:00] / [10:00,11:00] / [12:00,13:00]。</summary>
    private static List<TimeWindow> ThreeHours() => new() { W(0, 60), W(120, 180), W(240, 300) };

    // ════════════════════════════════════════════════════════════════════
    // ① 正排
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void F1_正排跨窗_90分钟落在两个60分钟窗内_起点08点终点10点半()
    {
        var slot = CrossWindowSlotLocator.Locate(
            TwoHours(), At(0), TimeSpan.FromMinutes(90), null);

        slot.Should().NotBeNull();
        slot!.Value.Start.Should().Be(At(0));      // 08:00
        slot.Value.End.Should().Be(At(150));       // 10:30 = 08:00 + 60 + 30
        // 墙钟 150 分钟 ≠ 有效加工 90 分钟 —— 差额正是 09:00~10:00 的空档。
        (slot.Value.End - slot.Value.Start).TotalMinutes.Should().Be(150d);
    }

    [Fact]
    public void F2_正排闸门_时长不超过最长窗时交回单窗路径()
    {
        // 60 == 最长窗长 ⇒ 必须返回 null（不许抢单窗路径的活，保证既有结果逐位不变）
        CrossWindowSlotLocator.Locate(TwoHours(), At(0), TimeSpan.FromMinutes(60), null)
            .Should().BeNull();
    }

    [Fact]
    public void F3_正排跨窗跳过占用_首窗被占30分钟后从08点半起算()
    {
        var occupied = new List<TimeWindow> { W(0, 30) };

        var slot = CrossWindowSlotLocator.Locate(
            TwoHours(), At(0), TimeSpan.FromMinutes(90), occupied);

        slot.Should().NotBeNull();
        slot!.Value.Start.Should().Be(At(30));     // 08:30
        slot.Value.End.Should().Be(At(180));       // 11:00（08:30~09:00 共 30 + 10:00~11:00 共 60 = 90）
    }

    [Fact]
    public void F4_正排日历走完仍凑不够_如实返回null不编造完成点()
    {
        // 两个窗合计 120 分钟，要 150 ⇒ 装不下
        CrossWindowSlotLocator.Locate(TwoHours(), At(0), TimeSpan.FromMinutes(150), null)
            .Should().BeNull();
    }

    [Fact]
    public void F5_正排乱序入参不改调用方占用列表()
    {
        // Phase5 `CompactGaps` 依赖占用列表**实例与次序不变** ⇒ 这里传乱序，事后必须原样。
        // ⚠ 2026-10-29 改夹具：原夹具的占用块 `[10:00,10:40]` 落在返回段**内部**，
        //   期望值 `[08:10, 11:00]` 等于把 F6 要修的缺陷写成了断言（跨窗段把别人套在里面）。
        //   换成本条只验证「乱序入参不被改写」这一件事：占用块全部落在起点附近，不被任何返回段套住。
        var occupied = new List<TimeWindow> { W(20, 30), W(0, 10) };   // 08:20~08:30 先、08:00~08:10 后（乱序）

        var slot = CrossWindowSlotLocator.Locate(
            TwoHours(), At(0), TimeSpan.FromMinutes(90), occupied);

        slot.Should().NotBeNull();
        slot!.Value.Start.Should().Be(At(30));     // 08:30（08:00~08:30 全被占，从块尾起算）
        slot.Value.End.Should().Be(At(180));       // 11:00（08:30~09:00 共 30 + 10:00~11:00 共 60 = 90）

        occupied[0].Start.Should().Be(At(20));     // 实参次序未被排序改写
        occupied[1].Start.Should().Be(At(0));
    }

    [Fact]
    public void F6_正排跨窗不得把已有占用块套进连续占用段()
    {
        // ── 真实现场（PV190，2026-10-29 全量跑批）────────────────────────────
        //   Phase5 入口快照（`APS_PHASE5_SNAPSHOT_DIR`，1,212,673 个 Task）离线扫描：
        //   同资源按 PlannedStartTime 升序、占用起点 = 计划起点 − Setup，按校验器第 4 项逐字判，
        //   **39 对冲突，39/39 同形**：起始更早的那条是跨窗任务，它的连续占用块把另一条**整段套住**。
        //   例：Resource 13394，跨窗块 `[2027-02-10 16:59 → 2027-03-11 11:47]`（有效加工仅 752 分钟）
        //       套住了 `[2027-02-11 08:00 → 08:50]`，重叠 50.1 分钟。
        //
        // ── 为什么这是缺陷而不是口径 ────────────────────────────────────────
        //   跨窗裁决原文：「占用记为**连续一段** `[Start, End]`（窗间的非工作时间也占住，
        //   **别的 Task 不得插进来**）」⇒ 这一整段是**真实的资源预约**。
        //   段内夹着任何已存在的占用块，就是同资源同一时刻被两个 Task 占用 —— 物理上不成立，
        //   Phase5 硬校验第 4 项必然判失败，而 A 路是**整份 FinalTask 清空**（不是丢一条）。
        //   旧实现遇到占用块只「跳过、继续往后凑分钟」，`start` 与 `accumulated` 都不重置
        //   ⇒ 返回段必然把这个块套在里面。
        //   正确做法：**碰到占用块就作废已攒进度，从块尾重新起算**（宁可排不下，也不产出重叠排程）。
        var occupied = new List<TimeWindow> { W(120, 150) };   // 10:00~10:30 已被别的 Task 占用

        var slot = CrossWindowSlotLocator.Locate(
            ThreeHours(), At(0), TimeSpan.FromMinutes(90), occupied);

        slot.Should().NotBeNull();
        slot!.Value.Start.Should().Be(At(150));    // 10:30（08:00~09:00 那 60 分钟作废，从块尾重新起算）
        slot.Value.End.Should().Be(At(300));       // 13:00（10:30~11:00 共 30 + 12:00~13:00 共 60 = 90）

        // 直接判据：返回段内不许夹着任何一个已存在的占用块（与 Phase5 校验第 4 项同口径、半开区间）。
        foreach (var occ in occupied)
        {
            (slot.Value.Start < occ.End && slot.Value.End > occ.Start)
                .Should().BeFalse(
                    $"跨窗段 [{slot.Value.Start:HH:mm},{slot.Value.End:HH:mm}] 套住了占用块 "
                    + $"[{occ.Start:HH:mm},{occ.End:HH:mm}] ⇒ 同资源时间重叠");
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // ② 端到端：PV195 形状
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 600 件 × 60 秒/件 = 600 分钟有效加工；该资源日历只有两个 300 分钟的窗。
    /// 改动前：单窗恒装不下 ⇒ 0 个 Task。改动后：跨窗排出 1 个 Task，墙钟跨度 900 分钟 &gt; 有效加工 600 分钟。
    /// </summary>
    [Fact]
    public async Task E2E_单窗结构性装不下_PV195形状_现在能排出1个Task且跨窗()
    {
        var result = await new FiniteCapacitySolver().SolveAsync(BuildPv195ShapedDemand());

        Assert.True(result.Success, result.ErrorMessage);

        var task = Assert.Single(result.FinalTasks);
        Assert.Equal(600m, task.EffectiveProcessMinutes);            // 有效加工 = 600 分钟

        var wallSpan = (task.PlannedEndTime - task.PlannedStartTime).TotalMinutes;
        Assert.True(wallSpan > 600d,
            $"墙钟跨度必须含窗间空档（有效加工 600 分钟，两窗各 300 分钟、中间空 300 分钟）"
            + $"⇒ 期望 900，实测 {wallSpan}；Start={task.PlannedStartTime:yyyy-MM-dd HH:mm}，"
            + $"End={task.PlannedEndTime:yyyy-MM-dd HH:mm}，ResourceId={task.ResourceId}");
        Assert.Equal(900d, wallSpan, 3);
    }

    // ════════════════════════════════════════════════════════════════════
    // 夹具
    // ════════════════════════════════════════════════════════════════════

    private const string Stage1 = "STAGE1";
    private const int MaterialId = 7;
    private const int DeptId = 100;
    private const int ResourceId = 3;
    private const int FactoryId = 1;

    private static DomainSolveRequest BuildPv195ShapedDemand()
        => new()
        {
            PlanVersionId = 1,
            ScheduleRunId = 43,
            DomainKey = "DOMAIN",
            PlanningStart = Day,
            PlanningEnd = Day.AddDays(30),

            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                new()
                {
                    LogicalDemandKey = "PV195",
                    PlanVersionId = 1L,
                    DomainKey = "DOMAIN",
                    AllocationSequence = 1,
                    DemandKey = "PV195",
                    MaterialId = MaterialId,
                    FactoryId = FactoryId,
                    StartStageCode = Stage1,
                    NetOutputQty = 600m,
                    PlannedProcessQty = 600m,
                    RequiredAvailableTime = Day.AddDays(5),
                    DemandSequence = 1,
                    QuantityAuthority = QuantityAuthorityValues.None
                }
            },

            RoutingOperations = new List<RoutingOperation>
            {
                new()
                {
                    MaterialId = MaterialId, ProductionDepartmentId = DeptId,
                    RouteCode = "DEFAULT",
                    OperationCode = "OP10", StageCode = Stage1,
                    StandardDuration = 60m,      // 源事实：秒/件 ⇒ 600 件 = 600 分钟有效加工
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
                new() { ResourceId = ResourceId, ResourceCode = "R3", FactoryCode = "F1", Capacity = 1m }
            },

            // 两个 300 分钟的窗：[08:00,13:00] 与 [18:00,23:00]。最长窗 300 < 600 ⇒ 单窗结构性装不下。
            CalendarSlots = new List<ResourceCalendarSlot>
            {
                new() { ResourceId = ResourceId, Start = Day.AddHours(8),  End = Day.AddHours(13), IsAvailable = true },
                new() { ResourceId = ResourceId, Start = Day.AddHours(18), End = Day.AddHours(23), IsAvailable = true }
            },

            MaterialConstraints = new List<MaterialAvailabilitySlice>
            {
                new()
                {
                    AllocationSequence = 1,
                    MaterialId = MaterialId,
                    FactoryId = FactoryId,
                    Quantity = 1000m,
                    AvailableTime = Day
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
                BatchPolicies = TestBatchPolicy.Permissive(MaterialId, DeptId)
            }
        };
}
