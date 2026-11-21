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
/// **W4 / T2 离线样本（0号位 11-07 任务包 §1.2）**：三级 Calendar 的 **WORK / OFF**、**停机**、**加班**。
///
/// <para>W4 原文要求「三级 Calendar 真实 `WORK/OFF`、停机、加班、Stage 有向交接、跨窗的 **T2 离线样本**」。
/// 有向交接（`StageHandoffWaitTests`）与跨窗（`CrossWindowSlotLocatorTests` F1~F6）已有覆盖；
/// 本文件补 **WORK/OFF、停机、加班** 三类。</para>
///
/// <para>夹具口径：`ResourceCalendarSlot.IsAvailable` 是**已合成后**的最终 WORK 窗；不可用段以"窗之间留空"表达。
/// 加班 = 在常规 WORK 窗之外**另加**一个可用窗（同一资源多条 slot）。纯内存，不碰数据库。</para>
/// </summary>
public sealed class W4CalendarWorkOffOvertimeTests
{
    private static readonly DateTime Day = new(2026, 11, 21, 0, 0, 0);
    private const int DeptId = 100;
    private const string Stage = "S1";
    private const string Op = "OP10";

    private readonly FiniteCapacitySolver _solver = new();

    // ── ① OFF 段：计划起点在 WORK 内但任务会横跨 OFF ⇒ 必须推到下一 WORK 窗 ──
    [Fact(DisplayName = "W4：任务横跨 OFF 段 ⇒ 顺延到下一 WORK 窗（不在 OFF 里加工）")]
    public async Task OffWindow_TaskCrossingOff_JumpToNextWorkWindow()
    {
        // WORK 08:00-12:00；OFF 12:00-13:00；WORK 13:00-17:00
        var request = Build(
            planningStart: Day.AddHours(11.5),      // 11:30 起点
            slots: new[]
            {
                (Day.AddHours(8), Day.AddHours(12)),
                (Day.AddHours(13), Day.AddHours(17)),
            },
            qty: 60m);                               // 60 件 × 1 分钟/件 = 60 分钟

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        var task = Assert.Single(result.FinalTasks);
        // 11:30 + 60min = 12:30 会横跨 OFF ⇒ 只能推到 13:00
        Assert.True(task.PlannedStartTime >= Day.AddHours(13),
            $"任务起点 {task.PlannedStartTime:O} 落在 OFF(12:00-13:00) 内则必须顺延到 13:00");
        Assert.True(task.PlannedEndTime > task.PlannedStartTime);
    }

    // ── ② 停机：WORK 中间挖掉一段 ⇒ 任务跳过停机段 ──
    [Fact(DisplayName = "W4：停机段(12:00-14:00) ⇒ 2h 任务从 14:00 起")]
    public async Task DowntimeGap_TaskSkipsToAfterDowntime()
    {
        var request = Build(
            planningStart: Day.AddHours(11),
            slots: new[]
            {
                (Day.AddHours(8), Day.AddHours(12)),    // 停机前
                (Day.AddHours(14), Day.AddHours(18)),   // 停机后
            },
            qty: 120m);                                 // 120 分钟 = 2h

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        var task = Assert.Single(result.FinalTasks);
        Assert.True(task.PlannedStartTime >= Day.AddHours(14),
            $"2h 任务无法放进 11:00-12:00（只剩 1h）⇒ 必须从停机结束 14:00 起；实际 {task.PlannedStartTime:O}");
        Assert.True(task.PlannedEndTime <= Day.AddHours(18));
    }

    // ── ③ 加班：常规窗之外另加班次窗 ⇒ 晚起点任务可排 ──
    [Fact(DisplayName = "W4：常规窗 08-17 + 加班窗 17-21 ⇒ 晚起点任务排进加班窗")]
    public async Task OvertimeWindow_AllowsLateScheduling()
    {
        var request = Build(
            planningStart: Day.AddHours(16.5),          // 16:30 起点
            slots: new[]
            {
                (Day.AddHours(8), Day.AddHours(17)),    // 常规班
                (Day.AddHours(17), Day.AddHours(21)),   // 加班
            },
            qty: 60m);                                   // 1h ⇒ 必须用到加班窗

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        var task = Assert.Single(result.FinalTasks);
        Assert.True(task.PlannedStartTime >= Day.AddHours(17),
            $"16:30 起 1h 任务在常规班内放不下 ⇒ 应落加班窗；实际 {task.PlannedStartTime:O}");
        Assert.True(task.PlannedEndTime <= Day.AddHours(21),
            "任务不得超出加班窗末端 21:00");
    }

    // ── ④ 反例：无加班窗 ⇒ 不能挤进剩余 30 分钟 ──
    [Fact(DisplayName = "W4反例：无加班窗 ⇒ 1h 任务不得挤进当日剩余 30 分钟")]
    public async Task NoOvertime_LateStartCannotFitToday()
    {
        var request = Build(
            planningStart: Day.AddHours(16.5),
            slots: new[]
            {
                (Day.AddHours(8), Day.AddHours(17)),                        // 当日只剩 30 分钟
                (Day.AddDays(1).AddHours(8), Day.AddDays(1).AddHours(17)),  // 次日班
            },
            qty: 60m);

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, "排不下是业务未排，不是技术失败：" + result.ErrorMessage);
        if (result.FinalTasks.Count > 0)
        {
            var task = result.FinalTasks.Single();
            Assert.True(task.PlannedStartTime >= Day.AddDays(1).AddHours(8),
                $"当日只剩 30 分钟，1h 任务只能落次日 08:00 起；实际 {task.PlannedStartTime:O}");
        }
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private static DomainSolveRequest Build(
        DateTime planningStart,
        IReadOnlyList<(DateTime Start, DateTime End)> slots,
        decimal qty)
    {
        const int materialId = 1;
        var end = Day.AddDays(3);

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            ScheduleRunId = 42L,
            DomainKey = "DOMAIN",
            PlanningStart = planningStart,
            PlanningEnd = end,
            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                new()
                {
                    LogicalDemandKey = "D1", PlanVersionId = 1L, DomainKey = "DOMAIN",
                    AllocationSequence = 1, DemandKey = "D1", MaterialId = materialId, FactoryId = 1,
                    StartStageCode = Stage, NetOutputQty = qty, PlannedProcessQty = qty,
                    RequiredAvailableTime = end, DemandSequence = 1,
                    QuantityAuthority = QuantityAuthorityValues.None,
                },
            },
            RoutingOperations = new List<RoutingOperation>
            {
                new() { MaterialId = materialId, ProductionDepartmentId = DeptId, RouteCode = "DEFAULT",
                        OperationCode = Op, StageCode = Stage, StandardDuration = 60m,
                        OperationPlanningMode = "FINITE_RESOURCE" },
            },
            OperationResourceEligibility = new List<OperationResourceEligibility>
            {
                new() { MaterialId = materialId, ProductionDepartmentId = DeptId, RouteCode = "DEFAULT",
                        OperationCode = Op, ResourceId = 1, Priority = 1, CapacityFactor = 1m },
            },
            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = materialId, StageCode = Stage, ProductionDepartmentId = DeptId },
            },
            Resources = new List<ResourceDefinition>
            {
                new() { ResourceId = 1, ResourceCode = "R1", FactoryCode = "F1", Capacity = 1m },
            },
            CalendarSlots = slots.Select(s => new ResourceCalendarSlot
            {
                ResourceId = 1, Start = s.Start, End = s.End, IsAvailable = true,
            }).ToList(),
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD",
                    AllowMerge = false,
                    AllowSplit = false,
                },
                BatchPolicies = TestBatchPolicy.Permissive(materialId, DeptId),
            },
        };
    }
}
