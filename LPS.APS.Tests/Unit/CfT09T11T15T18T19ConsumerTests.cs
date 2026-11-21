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
/// **0号位 v1.8 §二 第二批（2026-11-20）**：`CF-T09 / T11 / T15 / T18 / T19` 的
/// **消费侧单测**（纯内存，不碰数据库、不跑真实 SH 输入）。
///
/// <para>条款原文（2026-11-13《CF-T09复核确认及SH硬依赖联合验收要求》§五 与 v1.8:67）：</para>
/// <list type="bullet">
///   <item><b>CF-T09</b>：父件缺料数量阻断 —— 前提是 `MaterialConstraints.Quantity` 按**真实可用量**填；
///         我方消费侧的责任是「切片按 Quantity 累计，够了才排、不够显式未排」。</item>
///   <item><b>CF-T11</b>：`AvailableAt=NULL` **不当即时可用** —— Solver 不碰该字段，只消费 `LagMinutes`。</item>
///   <item><b>CF-T15</b>：受阻订单与独立合法订单**分开**，不得全域误阻断。</item>
///   <item><b>CF-T18</b>：同可信快照/规则/排序键连续重跑 ⇒ 关键需求/分配/结果**一致**。</item>
///   <item><b>CF-T19</b>：中间 `AvailableTime=NULL` 不能伪即刻可用；**终态失败才记 `SH_TIME_UNCLOSED`**
///         —— 1号位 必须证明硬边、源目标 Task 及 Solver 失败/成功三分支，且不直接写结果表。</item>
/// </list>
///
/// <para>⚠ 本文件只证 **Solver 消费侧**（1号位 辖区）；真实的 SH 数量+时间闭合与 `CF-T01~T20` 同 Run 联验
/// 仍依赖 2号位 冻结的 Solver 前 SH 输入，不因本文件通过而被宣称闭合。</para>
/// </summary>
public sealed class CfT09T11T15T18T19ConsumerTests
{
    private static readonly DateTime Day = new(2026, 11, 20, 0, 0, 0);
    private static readonly DateTime PlanEnd = Day.AddDays(30);
    private const int DeptId = 100;
    private const string Stage = "STAGE1";
    private const string Op = "OP10";

    private readonly FiniteCapacitySolver _solver = new();

    // ─────────────────────────── CF-T09 ───────────────────────────

    [Fact(DisplayName = "CF-T09：切片按真实 Quantity 累计，够量即排且时点取累计够的那一刻")]
    public async Task CfT09_SlicesAccumulateByRealQuantity()
    {
        // 需求 10：4 件 @Day + 6 件 @Day+5 ⇒ 累计在 Day+5 才够 10。
        var request = Build(
            new[] { D("D1", 1, 1, 10m) },
            new[]
            {
                new SliceSpec { Alloc = 1, MaterialId = 1, Qty = 4m, At = Day, Basis = "KNOWN" },
                new SliceSpec { Alloc = 1, MaterialId = 1, Qty = 6m, At = Day.AddDays(5), Basis = "KNOWN" },
            },
            Array.Empty<LinkSpec>());

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        var task = Assert.Single(result.FinalTasks);
        Assert.True(task.PlannedStartTime >= Day.AddDays(5),
            $"累计够量在 Day+5，Task 不得早于该时点；实际 {task.PlannedStartTime:O}");
        Assert.Empty(result.UnscheduledTasks);
    }

    [Fact(DisplayName = "CF-T09反例：切片累计不足需求量 ⇒ 该需求显式未排，不造半成品 Task")]
    public async Task CfT09_InsufficientQuantity_ReportsUnscheduled()
    {
        var request = Build(
            new[] { D("D1", 1, 1, 10m) },
            new[]
            {
                new SliceSpec { Alloc = 1, MaterialId = 1, Qty = 4m, At = Day, Basis = "KNOWN" },
                new SliceSpec { Alloc = 1, MaterialId = 1, Qty = 5m, At = Day.AddDays(5), Basis = "KNOWN" },
            },
            Array.Empty<LinkSpec>());

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, "缺料是业务未排，不是技术失败：" + result.ErrorMessage);
        Assert.Empty(result.FinalTasks);
        Assert.Contains(result.UnscheduledTasks, u => u.DraftId == "D1");
    }

    // ─────────────────────────── CF-T11 ───────────────────────────

    [Fact(DisplayName = "CF-T11：SH_TIME_UNRESOLVED 切片（AvailableTime 只是占位/上界）不得当即时可用")]
    public async Task CfT11_TimeUnresolved_NotTreatedAsImmediatelyAvailable()
    {
        // `AvailableTime=Day` 是占位（= 计划期起点），`TimeBasis=SH_TIME_UNRESOLVED` 表示**时间未定**、
        // 且无任何硬边 ⇒ 下界不可得 ⇒ 必须显式缺料，而不是"当期就能用"。
        var request = Build(
            new[] { D("D1", 1, 1, 10m) },
            new[] { new SliceSpec { Alloc = 1, MaterialId = 1, Qty = 10m, At = Day, Basis = "SH_TIME_UNRESOLVED" } },
            Array.Empty<LinkSpec>());

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.FinalTasks);
        Assert.Contains(result.UnscheduledTasks, u => u.DraftId == "D1");
    }

    [Fact(DisplayName = "CF-T11对照：同一占位时点，TimeBasis=KNOWN 则正常排下（差异只在 TimeBasis）")]
    public async Task CfT11_KnownBasis_ControlSchedules()
    {
        var request = Build(
            new[] { D("D1", 1, 1, 10m) },
            new[] { new SliceSpec { Alloc = 1, MaterialId = 1, Qty = 10m, At = Day, Basis = "KNOWN" } },
            Array.Empty<LinkSpec>());

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Single(result.FinalTasks);
        Assert.Empty(result.UnscheduledTasks);
    }

    [Fact(DisplayName = "CF-T11：硬依赖时点只走 LagMinutes（父件开工 ≥ 子件完工 + Lag）")]
    public async Task CfT11_HardDependencyUsesLagMinutesOnly()
    {
        var request = Build(
            new[] { D("child", 1, 1, 1m), D("parent", 2, 2, 1m) },
            Array.Empty<SliceSpec>(),
            new[] { new LinkSpec { Parent = "parent", Child = "child", LagMinutes = 120m } });

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        var childTask = result.FinalTasks.Single(t => t.SourceDraftId == "child");
        var parentTask = result.FinalTasks.Single(t => t.SourceDraftId == "parent");

        Assert.True(parentTask.PlannedStartTime >= childTask.PlannedEndTime.AddMinutes(120),
            $"父件开工 {parentTask.PlannedStartTime:O} 必须 >= 子件完工 {childTask.PlannedEndTime:O} + 120 分钟");
        // 夹具里父件除该 Lag 外无其它约束 ⇒ 恰好卡在 Lag 下界（证明 Lag 真的进了 floor，而不是被忽略）。
        Assert.Equal(childTask.PlannedEndTime.AddMinutes(120), parentTask.PlannedStartTime);
    }

    // ─────────────────────────── CF-T15 ───────────────────────────

    [Fact(DisplayName = "CF-T15：一条受阻需求不得把同域独立合法需求一起阻断")]
    public async Task CfT15_BlockedDemandDoesNotBlockIndependentDemand()
    {
        var request = Build(
            new[] { D("BLOCKED", 1, 1, 10m), D("OK", 2, 2, 10m) },
            new[]
            {
                new SliceSpec { Alloc = 1, MaterialId = 1, Qty = 1m, At = Day, Basis = "KNOWN" },   // 缺料
                new SliceSpec { Alloc = 2, MaterialId = 2, Qty = 10m, At = Day, Basis = "KNOWN" },  // 充足
            },
            Array.Empty<LinkSpec>());

        var result = await _solver.SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains(result.UnscheduledTasks, u => u.DraftId == "BLOCKED");
        // 独立需求照排：其 Task 必在，且不因另一条需求的缺料被连坐。
        var okTasks = result.FinalTasks.Where(t => t.SourceDraftId == "OK").ToList();
        Assert.NotEmpty(okTasks);
        Assert.DoesNotContain(result.UnscheduledTasks, u => u.DraftId == "OK");
    }

    // ─────────────────────────── CF-T18 ───────────────────────────

    [Fact(DisplayName = "CF-T18：同一不可变 DTO 连续三次求解，业务身份/时点/数量逐位一致")]
    public async Task CfT18_SameFrozenDto_ThreeRuns_Identical()
    {
        // `FinalDraftId` 是 `Guid.NewGuid()`（每次运行不同，属落库身份而非业务身份），
        // 故比较口径按 0号位 条款「任务身份/时点/数量」取 **业务键**：
        //   (SourceDraftId, StageCode, OperationCode, PathId, Quantity, Start, End)。
        var request = Build(
            new[] { D("D1", 1, 1, 10m), D("D2", 2, 2, 3m) },
            new[]
            {
                new SliceSpec { Alloc = 1, MaterialId = 1, Qty = 10m, At = Day, Basis = "KNOWN" },
                new SliceSpec { Alloc = 2, MaterialId = 2, Qty = 3m, At = Day, Basis = "KNOWN" },
            },
            Array.Empty<LinkSpec>());

        var digests = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var result = await _solver.SolveAsync(request);
            Assert.True(result.Success, $"第 {i + 1} 次运行失败：{result.ErrorMessage}");

            var rows = result.FinalTasks
                .OrderBy(t => t.SourceDraftId, StringComparer.Ordinal)
                .ThenBy(t => t.StageCode, StringComparer.Ordinal)
                .ThenBy(t => t.OperationCode, StringComparer.Ordinal)
                .ThenBy(t => t.PathId)
                .Select(t => string.Join('|',
                    t.SourceDraftId, t.StageCode, t.OperationCode,
                    t.PathId.ToString(), t.Quantity.ToString("0.####"),
                    t.PlannedStartTime.ToString("O"), t.PlannedEndTime.ToString("O")))
                .ToList();
            var unscheduled = result.UnscheduledTasks
                .OrderBy(u => u.DraftId, StringComparer.Ordinal)
                .Select(u => $"{u.DraftId}|{u.Reason}|{u.UnscheduledQty:0.####}")
                .ToList();
            digests.Add(string.Join("\n", rows) + "\n--unscheduled--\n" + string.Join("\n", unscheduled));
        }

        Assert.Equal(digests[0], digests[1]);
        Assert.Equal(digests[0], digests[2]);
    }

    // ─────────────────────────── CF-T19 ───────────────────────────

    [Fact(DisplayName = "CF-T19：中间态时间未定 ⇒ Solver 不得产 SH_TIME_UNCLOSED（终态码只归失败分支）")]
    public async Task CfT19_IntermediateNull_MustNotEmitShTimeUnclosed()
    {
        var request = Build(
            new[] { D("D1", 1, 1, 10m) },
            new[] { new SliceSpec { Alloc = 1, MaterialId = 1, Qty = 10m, At = Day, Basis = "SH_TIME_UNRESOLVED" } },
            Array.Empty<LinkSpec>());

        var result = await _solver.SolveAsync(request);

        // 成功分支（整域不因此技术失败）：时间未定按「不可判 ⇒ 缺料业务未排」处理。
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains(result.UnscheduledTasks, u => u.DraftId == "D1");

        // 中间态不得写终态失败码：结果里任何通道都不许出现 `SH_TIME_UNCLOSED`。
        Assert.DoesNotContain(result.UnscheduledTasks, u => u.Reason == "SH_TIME_UNCLOSED");
        Assert.DoesNotContain(result.SolveTraceNotes, n => n.ReasonCode == "SH_TIME_UNCLOSED");
        Assert.DoesNotContain(result.ExplanationFacts, f => f.ReasonCode == "SH_TIME_UNCLOSED");
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private sealed class DemandSpec
    {
        public string Key = string.Empty;
        public int MaterialId;
        public long Alloc;
        public decimal NetQty;
    }

    private sealed class SliceSpec
    {
        public long Alloc;
        public int MaterialId;
        public decimal Qty;
        public DateTime At = Day;
        public string? Basis;
    }

    private sealed class LinkSpec
    {
        public string Parent = string.Empty;
        public string Child = string.Empty;
        public decimal LagMinutes;
    }

    private static DemandSpec D(string key, int materialId, long alloc, decimal netQty)
        => new() { Key = key, MaterialId = materialId, Alloc = alloc, NetQty = netQty };

    private static DomainSolveRequest Build(
        IReadOnlyList<DemandSpec> demands,
        IReadOnlyList<SliceSpec> slices,
        IReadOnlyList<LinkSpec> links)
    {
        var materials = demands.Select(d => d.MaterialId).Distinct().ToList();

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            ScheduleRunId = 42L,
            DomainKey = "DOMAIN",
            PlanningStart = Day,
            PlanningEnd = PlanEnd,
            LogicalProductionDemands = demands.Select(d => new LogicalProductionDemand
            {
                LogicalDemandKey = d.Key,
                PlanVersionId = 1L,
                DomainKey = "DOMAIN",
                AllocationSequence = d.Alloc,
                DemandKey = d.Key,
                MaterialId = d.MaterialId,
                FactoryId = 1,
                StartStageCode = Stage,
                NetOutputQty = d.NetQty,
                PlannedProcessQty = d.NetQty,
                RequiredAvailableTime = PlanEnd,
                DemandSequence = (int)d.Alloc,
                QuantityAuthority = QuantityAuthorityValues.None,
            }).ToList(),
            RoutingOperations = materials.Select(m => new RoutingOperation
            {
                MaterialId = m,
                ProductionDepartmentId = DeptId,
                RouteCode = "DEFAULT",
                OperationCode = Op,
                StageCode = Stage,
                StandardDuration = 60m,          // 源事实：秒 = 1 分钟/件
                SetupTime = 0m,
                OperationPlanningMode = "FINITE_RESOURCE",
            }).ToList(),
            OperationResourceEligibility = materials.Select(m => new OperationResourceEligibility
            {
                MaterialId = m,
                ProductionDepartmentId = DeptId,
                RouteCode = "DEFAULT",
                OperationCode = Op,
                ResourceId = m,
                Priority = 1,
                CapacityFactor = 1m,
            }).ToList(),
            MaterialStageDepartmentContexts = materials.Select(m => new MaterialStageDepartmentContextDto
            {
                MaterialId = m,
                StageCode = Stage,
                ProductionDepartmentId = DeptId,
            }).ToList(),
            Resources = materials.Select(m => new ResourceDefinition
            {
                ResourceId = m,
                ResourceCode = $"R{m}",
                FactoryCode = "F1",
                Capacity = 1m,
            }).ToList(),
            CalendarSlots = materials.Select(m => new ResourceCalendarSlot
            {
                ResourceId = m,
                Start = Day,
                End = PlanEnd,
                IsAvailable = true,
            }).ToList(),
            MaterialConstraints = slices.Select(s => new MaterialAvailabilitySlice
            {
                AllocationSequence = s.Alloc,
                MaterialId = s.MaterialId,
                FactoryId = 1,
                Quantity = s.Qty,
                AvailableTime = s.At,
                TimeBasis = s.Basis,
            }).ToList(),
            MaterialRequirementLinks = links.Select(l => new MaterialRequirementLink
            {
                ConsumerLogicalDemandKey = l.Parent,
                ProducerLogicalDemandKey = l.Child,
                ConsumerMaterialId = demands.First(d => d.Key == l.Parent).MaterialId,
                ProducerMaterialId = demands.First(d => d.Key == l.Child).MaterialId,
                RequiredQty = demands.First(d => d.Key == l.Child).NetQty,
                ProducerAllocationSequence = demands.First(d => d.Key == l.Child).Alloc,
                LagMinutes = l.LagMinutes,
            }).ToList(),
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD",
                    AllowMerge = false,
                    AllowSplit = false,
                },
                // P0-01（0号位 2026-10-08 §四）：C 桶必须显式给出有效 Batch Policy，否则 Fail Closed。
                BatchPolicies = TestBatchPolicy.Permissive(materials, DeptId),
            },
        };
    }
}
