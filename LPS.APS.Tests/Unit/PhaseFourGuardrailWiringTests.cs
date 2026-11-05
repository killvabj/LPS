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
/// **R11 Phase4 三字段接线实测**（2026-10-29）。
///
/// <para>接的三个字段都在冻结 `CandidateGuardrailBlock` 里，此前在 `LPS.APS.Scheduling` 内 **0 命中**：</para>
/// <list type="bullet">
///   <item><b>`WarnOnlyOnMaxImpacted`</b>（默认 true）—— 传播受影响 Task 超警戒线时
///     **仅 Warning、不中断传播**；false 才保持整改前行为（改道本 Domain 全量重排 Fallback）。
///     两种取值都**不会**把半修的盘当可行交出去。</item>
///   <item><b>`ResourceTopN`</b>（默认 5）—— 资源候选的**软优先窗口**：
///     先按偏好顺序试前 N 个，前 N 个全试不通**继续试其余**（硬截断会造出未排 ⇒ 撞排不下红线），
///     仅留 `PHASE4_RESOURCE_TOPN_EXCEEDED` 作为「TopN 配小了」的判别证据。</item>
///   <item><b>`MaxRepairAttempts`</b>（默认 5）—— **诊断阈值，不是硬上限**：
///     未落定执行批数超阈值只留 `PHASE4_REPAIR_ATTEMPTS_EXCEEDED`，全部单元照旧逐一尝试。
///     （硬上限会把剩余执行批留在未排 ⇒ 降目标。）</item>
/// </list>
///
/// <para><b>本文件钉什么</b>：超警戒线在两种取值下都**必留痕**，且处置文字可区分
/// （`true` ⇒ 继续传播；`false` ⇒ 改道全量重排 Fallback）—— 这条能鉴别字段是否真在改变处置。</para>
///
/// <para><b>本文件**不**覆盖什么（写清楚，免得被当成假绿）</b>：</para>
/// <list type="bullet">
///   <item><b>`ResourceTopN`</b>：消费点在 Phase4 `TryResourceSwitch`（修复「Phase2 没排下的需求」）。
///     Unit 里造不出「Phase2 排不下、Phase4 排得下」的差异夹具（两者共用同一套资源候选扫描，
///     我试过把首个资源日历压到 1 分钟，结果 Phase2 直接落到第二个资源、Phase4 根本没进修复路径）⇒
///     **没有可鉴别用例**。因此本文件不为它写"看起来绿"的空断言；
///     该分支的覆盖要靠 Phase4 修复路径的集成级夹具（归 2号位 同版集成一档）。</item>
///   <item><b>`MaxRepairAttempts`</b>：需要「同一需求多执行批且全部修不好」的夹具，同理未覆盖。</item>
/// </list>
/// </summary>
public class PhaseFourGuardrailWiringTests
{
    private static readonly DateTime Day = new(2026, 9, 1);

    private const string Stage1 = "STAGE1";
    private const int MaterialId = 1;
    private const int DeptId = 100;
    private const int ResourceId = 1;
    private const int ResourceId2 = 2;
    private const int FactoryId = 1;

    // ════════════════════════════════════════════════════════════════════
    // WarnOnlyOnMaxImpacted
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 2 个已排 Task 全在资源 1 上，`ChangedResourceIds=[1]` ⇒ 受影响 2 个；
    /// 警戒线 = `max(1, (int)(2 × 30%)) = 1` ⇒ 2 &gt; 1 **必超线**。
    /// `WarnOnlyOnMaxImpacted=true`（默认）⇒ 留 Warning 且**继续传播**，不因超线改道全量重排。
    /// </summary>
    [Fact]
    public async Task T1_WarnOnly为true_超警戒线留Warning且不中断传播()
    {
        var result = await new FiniteCapacitySolver().SolveAsync(
            BuildTwoDemands(warnOnlyOnMaxImpacted: true, changedResource: true));

        Assert.True(result.Success, result.ErrorMessage);

        var note = Assert.Single(result.SolveTraceNotes,
            n => n.ReasonCode == "PHASE4_PROPAGATION_THRESHOLD_BREACH");
        Assert.Equal("Warning", note.Level);
        Assert.Contains("继续传播", note.Message);

        // 不中断 ⇒ 两条 Task 一条都不能丢。
        Assert.Equal(2, result.FinalTasks.Count);
    }

    /// <summary>
    /// 同一夹具、`WarnOnlyOnMaxImpacted=false` ⇒ 仍然**必留痕**，但处置文字是「全量重排 Fallback」。
    /// 与 T1 合起来说明该字段真在改变处置，而不只是「有没有痕」。
    /// </summary>
    [Fact]
    public async Task T2_WarnOnly为false_超警戒线留Warning并改道全量重排()
    {
        var result = await new FiniteCapacitySolver().SolveAsync(
            BuildTwoDemands(warnOnlyOnMaxImpacted: false, changedResource: true));

        Assert.True(result.Success, result.ErrorMessage);

        var note = Assert.Single(result.SolveTraceNotes,
            n => n.ReasonCode == "PHASE4_PROPAGATION_THRESHOLD_BREACH");
        Assert.Contains("Fallback", note.Message);

        // Fallback 也是硬合法排程，不得把需求排丢。
        Assert.Equal(2, result.FinalTasks.Count);
    }

    // ════════════════════════════════════════════════════════════════════
    // 夹具
    // ════════════════════════════════════════════════════════════════════

    private static DomainSolveRequest BuildTwoDemands(bool warnOnlyOnMaxImpacted, bool changedResource)
        => BuildCore(
            demandCount: 2,
            resourceIds: new[] { ResourceId },
            guardrail: new CandidateGuardrailBlock { WarnOnlyOnMaxImpacted = warnOnlyOnMaxImpacted },
            changedResourceIds: changedResource ? new[] { ResourceId } : null);

    private static DomainSolveRequest BuildCore(
        int demandCount,
        IReadOnlyList<int> resourceIds,
        CandidateGuardrailBlock guardrail,
        IReadOnlyList<int>? changedResourceIds)
    {
        var demands = Enumerable.Range(1, demandCount)
            .Select(i => (Seq: (long)i, Qty: 6m))
            .ToArray();

        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            ScheduleRunId = 42,
            DomainKey = "DOMAIN",
            PlanningStart = Day,
            PlanningEnd = Day.AddDays(30),

            RunScope = changedResourceIds is null
                ? new RunScope()
                : new RunScope { ChangedResourceIds = changedResourceIds.ToList() },

            LogicalProductionDemands = demands
                .Select((d, i) => new LogicalProductionDemand
                {
                    LogicalDemandKey = $"D{i + 1}",
                    PlanVersionId = 1L,
                    DomainKey = "DOMAIN",
                    AllocationSequence = d.Seq,
                    DemandKey = $"D{i + 1}",
                    MaterialId = MaterialId,
                    FactoryId = FactoryId,
                    StartStageCode = Stage1,
                    NetOutputQty = d.Qty,
                    PlannedProcessQty = d.Qty,
                    RequiredAvailableTime = Day.AddDays(20),
                    DemandSequence = i + 1,
                    QuantityAuthority = QuantityAuthorityValues.None
                })
                .ToList(),

            RoutingOperations = new List<RoutingOperation>
            {
                new()
                {
                    MaterialId = MaterialId, ProductionDepartmentId = DeptId,
                    RouteCode = "DEFAULT",
                    OperationCode = "OP10", StageCode = Stage1,
                    StandardDuration = 60m,          // 源事实：秒 ⇒ 1 分钟/件 ⇒ 6 分钟/需求
                    OperationPlanningMode = "FINITE_RESOURCE"
                }
            },

            OperationResourceEligibility = resourceIds
                .Select((rid, idx) => new OperationResourceEligibility
                {
                    MaterialId = MaterialId, ProductionDepartmentId = DeptId,
                    RouteCode = "DEFAULT",
                    OperationCode = "OP10", ResourceId = rid,
                    Priority = idx + 1, CapacityFactor = 1m
                })
                .ToList(),

            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = MaterialId, StageCode = Stage1, ProductionDepartmentId = DeptId }
            },

            Resources = resourceIds
                .Select(rid => new ResourceDefinition
                {
                    ResourceId = rid, ResourceCode = $"R{rid}", FactoryCode = "F1", Capacity = 1m
                })
                .ToList(),

            CalendarSlots = resourceIds
                .Select(rid => new ResourceCalendarSlot
                {
                    ResourceId = rid,
                    Start = Day,
                    End = Day.AddDays(29),
                    IsAvailable = true
                })
                .ToList(),

            MaterialConstraints = demands
                .Select(d => new MaterialAvailabilitySlice
                {
                    AllocationSequence = d.Seq,
                    MaterialId = MaterialId,
                    FactoryId = FactoryId,
                    Quantity = 10m,
                    AvailableTime = Day
                })
                .ToList(),

            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD",
                    AllowMerge = false,
                    AllowSplit = false
                },
                CandidateGuardrail = guardrail,
                BatchPolicies = TestBatchPolicy.Permissive(MaterialId, DeptId)
            }
        };
    }
}
