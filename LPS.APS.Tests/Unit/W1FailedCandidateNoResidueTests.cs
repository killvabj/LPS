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
/// **W1（0号位 v1.8 §一 ／ 11-07 任务包 §P1-D 第 1 条）**：
/// 真正失败的合批候选之后，**状态不得泄进后续需求**。
///
/// <para>原件逐字：「真正失败候选**已摘旧 Task、部分 Op 重排**之后，
/// Task/占用/份额/时间线/Trace/**DemandCompletion/依赖和索引缓存**恢复逐位一致」。</para>
///
/// <para><b>本文件的思路（比逐字段断言更能抓残留）</b>：构造一个**所有合批都会被交期拒掉**的夹具，
/// 然后跑两遍——`AllowMerge=true`（会真去试合批、真被拒）与 `AllowMerge=false`（根本不试）。
/// 若失败候选留下了任何残留（批次索引、占用、份额、时间线），两遍的**最终 Task 与会话事实必然不同**。
/// 两遍逐字相同 ⇒ 残留为零。</para>
///
/// <para><b>关于 DemandCompletion</b>：它在 **Phase5** 由最终 Task 列表派生
/// （`PhaseFiveCompression.cs:630` `DemandCompletions = TimedStageTimingBuild(...)`），
/// 在 Phase2 试排期间**尚不存在** ⇒ 它的隔离由「Task 列表被还原」+「Phase5 从零派生」**构造上保证**。
/// 本文件仍把它纳入对拍，以固定这条结论。</para>
///
/// <para>纯内存，不碰数据库。</para>
/// </summary>
public sealed class W1FailedCandidateNoResidueTests
{
    private static readonly DateTime Day = new(2026, 11, 21, 0, 0, 0);
    private static readonly DateTime Start = Day.AddHours(8);
    private const int DeptId = 100;
    private const string Stage = "S1";
    private const string Op = "OP10";
    private const int MaterialId = 1;

    private readonly FiniteCapacitySolver _solver = new();

    [Fact(DisplayName = "W1 · 合批全被交期拒 ⇒ allowMerge 开/关两跑最终 Task 与会话事实逐字相同（零残留）")]
    public async Task FailedCandidates_LeaveNoResidue()
    {
        DomainSolveResult withMerge;
        // 先证明本夹具**确实走进了"合批尝试过、但全部被拒"的路径**——否则本用例没有证明力
        using (var scope = SolverDiagnostics.BeginScope())
        {
            withMerge = await SolveAsync(allowMerge: true);
            Assert.True(scope.Counters.MergeAttempts > 0,
                "夹具没有触发任何合批尝试 ⇒ 本用例测不到失败候选的残留");
            Assert.Equal(0L, scope.Counters.MergeHits);
        }

        var withoutMerge = await SolveAsync(allowMerge: false);

        Assert.True(withMerge.Success, withMerge.ErrorMessage);
        Assert.True(withoutMerge.Success, withoutMerge.ErrorMessage);

        // 前提：本夹具下合批确实被拒（三条需求都独立排）
        Assert.Equal(3, withMerge.FinalTasks.Count);
        Assert.Equal(3, withoutMerge.FinalTasks.Count);

        // ① 最终 Task 逐条相同（含时间、数量、资源、批键）
        Assert.Equal(TaskSignature(withoutMerge), TaskSignature(withMerge));

        // ② 份额归属逐条相同
        Assert.Equal(ShareSignature(withoutMerge), ShareSignature(withMerge));

        // ③ 会话事实（DemandCompletion）逐条相同
        Assert.Equal(CompletionSignature(withoutMerge), CompletionSignature(withMerge));

        // ④ 未排集合相同（本夹具应为空）
        Assert.Empty(withMerge.UnscheduledTasks);
        Assert.Empty(withoutMerge.UnscheduledTasks);
    }

    // ─────────────────────────── 断言用的签名 ───────────────────────────

    private static string TaskSignature(DomainSolveResult r)
        => string.Join("\n", r.FinalTasks
            .OrderBy(t => t.SourceDraftId, StringComparer.Ordinal)
            .ThenBy(t => t.OperationSeq)
            .Select(t => string.Join("|",
                t.SourceDraftId, t.OperationCode, t.OperationSeq,
                t.Quantity.ToString("G29"), t.UOM ?? "",
                t.ResourceId?.ToString() ?? "null",
                t.PlannedStartTime.ToString("O"), t.PlannedEndTime.ToString("O"),
                t.SetupTime.ToString("G29"), t.SetupSource ?? "", t.ExecutionBatchDraftKey ?? "")));

    /// <summary>
    /// 份额签名**不含 `FinalDraftId`**：那是每次运行新生成的 GUID，跨跑必然不同。
    /// 用 `(AllocationSequence, ComponentQty)` 就够抓残留——若失败候选漏撤一条份额，
    /// 这里会多出一行或数量不等。
    /// </summary>
    private static string ShareSignature(DomainSolveResult r)
        => string.Join("\n", r.AllocationShares
            .OrderBy(s => s.AllocationSequence)
            .ThenBy(s => s.ComponentQty)
            .Select(s => $"{s.AllocationSequence}|{s.ComponentQty:G29}"));

    private static string CompletionSignature(DomainSolveResult r)
        => string.Join("\n", r.DemandCompletions
            .OrderBy(c => c.LogicalDemandKey, StringComparer.Ordinal)
            .Select(c => string.Join("|",
                c.LogicalDemandKey,
                c.CompletionTime.ToString("O"),
                c.AvailableTime.ToString("O"),
                c.RequiredStageCode ?? "",
                c.CompletionTimeCertain?.ToString() ?? "null")));

    // ─────────────────────────── 夹具 ───────────────────────────

    private Task<DomainSolveResult> SolveAsync(bool allowMerge) => _solver.SolveAsync(Build(allowMerge));

    private static DomainSolveRequest Build(bool allowMerge)
    {
        // 单一资源、每条 10 件 × 1 分钟；三条需求首尾相接：
        //   A 08:00-08:10（交期 08:15）／B 08:10-08:20（交期 08:25）／C 08:20-08:30（交期 08:35）
        // 任一条合批会把它自己的完成时间推后 ⇒ 全部被交期层拒 ⇒ 走"失败候选"路径。
        return new DomainSolveRequest
        {
            PlanVersionId = 1,
            ScheduleRunId = 42L,
            DomainKey = "DOMAIN",
            PlanningStart = Start,
            PlanningEnd = Day.AddDays(30),
            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                Demand("A", 1, Day.AddHours(8).AddMinutes(15)),
                Demand("B", 2, Day.AddHours(8).AddMinutes(25)),
                Demand("C", 3, Day.AddHours(8).AddMinutes(35)),
            },
            RoutingOperations = new List<RoutingOperation>
            {
                new() { MaterialId = MaterialId, ProductionDepartmentId = DeptId, RouteCode = "DEFAULT",
                        OperationCode = Op, StageCode = Stage, StandardDuration = 60m,
                        OperationPlanningMode = "FINITE_RESOURCE" },
            },
            OperationResourceEligibility = new List<OperationResourceEligibility>
            {
                new() { MaterialId = MaterialId, ProductionDepartmentId = DeptId, RouteCode = "DEFAULT",
                        OperationCode = Op, ResourceId = 1, Priority = 1, CapacityFactor = 1m },
            },
            MaterialStageDepartmentContexts = new List<MaterialStageDepartmentContextDto>
            {
                new() { MaterialId = MaterialId, StageCode = Stage, ProductionDepartmentId = DeptId },
            },
            Resources = new List<ResourceDefinition>
            {
                new() { ResourceId = 1, ResourceCode = "R1", FactoryCode = "F1", Capacity = 1m },
            },
            CalendarSlots = new List<ResourceCalendarSlot>
            {
                new() { ResourceId = 1, Start = Day, End = Day.AddDays(30), IsAvailable = true },
            },
            StrategySnapshot = new SolverStrategySnapshot
            {
                Parameters = new FiniteCapacityParameters
                {
                    SchedulingDirection = "FORWARD",
                    AllowMerge = allowMerge,
                    AllowSplit = false,
                },
                BatchPolicies = new[]
                {
                    new BatchPolicyRuleSnapshot
                    {
                        MaterialId = MaterialId, ProductionDepartmentId = DeptId,
                        MinExecutionBatchQty = null, MaxExecutionBatchQty = null,
                        AllowSplit = false, AllowMerge = allowMerge,
                    },
                },
            },
        };
    }

    private static LogicalProductionDemand Demand(string key, int seq, DateTime due) => new()
    {
        LogicalDemandKey = key, PlanVersionId = 1L, DomainKey = "DOMAIN",
        AllocationSequence = seq, DemandKey = key, MaterialId = MaterialId, FactoryId = 1,
        StartStageCode = Stage, NetOutputQty = 10m, PlannedProcessQty = 10m,
        RequiredAvailableTime = due, DemandSequence = seq,
        QuantityAuthority = QuantityAuthorityValues.None,
    };
}
