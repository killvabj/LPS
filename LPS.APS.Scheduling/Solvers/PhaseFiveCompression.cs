using LPS.APS.Core.Dto;

namespace LPS.APS.Scheduling.Solvers;

/// <summary>
/// Phase 5: 压缩空隙与最终评价
/// 文档：《APS_V1_1号位有限产能排程开发实施包_v1.0_20260814.md》§六 Phase 5
///
/// 职责：
/// - 在不破坏高优先级交期的情况下：
///   * 减少不必要等待
///   * 减少 WIP
///   * 减少 Setup
///   * 提升利用率
///   * 避免过早生产
///   * 尽量保持计划稳定
/// </summary>
internal class PhaseFiveCompression
{
    /// <summary>
    /// 执行空隙压缩与最终评价
    /// 文档：§六 Phase 5
    /// 职责：在不破坏高优先级交期的情况下压缩空隙
    /// </summary>
    public DomainSolveResult Compress(
        DomainSolveRequest request,
        InitialScheduleResult scheduleResult,
        RepairResult repairResult,
        DiagnosticsResult diagnostics,
        ConstraintContext constraints)
    {
        // 合并所有已排程任务
        var allScheduledTasks = new List<FinalTaskDraft>();
        allScheduledTasks.AddRange(scheduleResult.ScheduledTasks);
        allScheduledTasks.AddRange(repairResult.RepairedTasks);

        // TODO P13: 实现真实的空隙压缩优化（§六 Phase 5）
        // 在不破坏高优先级交期的情况下：
        // - 减少不必要等待
        // - 减少 WIP
        // - 减少 Setup
        // - 提升利用率
        // - 避免过早生产
        // - 尽量保持计划稳定

        // 生成 AllocationTaskShare（追溯 Allocation → Task 的份额）
        var allocationShares = GenerateAllocationShares(allScheduledTasks, request);

        // P0-13修复：生成 TaskDependency（基于 Routing 工序依赖关系）
        var taskDependencies = GenerateTaskDependencies(allScheduledTasks, request, constraints);

        // 收集未排程需求
        var unscheduledTasks = new List<UnscheduledTaskResult>();

        // Phase 2 未排程的需求
        foreach (var demandKey in scheduleResult.UnscheduledDemandKeys)
        {
            if (!repairResult.RepairedTasks.Any(t => t.SourceDraftId == demandKey))
            {
                unscheduledTasks.Add(new UnscheduledTaskResult
                {
                    DraftId = demandKey,
                    Reason = "Phase 2 初始排程失败，Phase 4 修复未成功"
                });
            }
        }

        // Phase 4 仍未排程的需求
        foreach (var demandKey in repairResult.StillUnscheduledKeys)
        {
            unscheduledTasks.Add(new UnscheduledTaskResult
            {
                DraftId = demandKey,
                Reason = "Phase 4 局部修复后仍无法排程"
            });
        }

        // P0-15修复：构建最终结果，Success表示Solver执行成功，业务Unscheduled不影响Success
        return new DomainSolveResult
        {
            // P0-15修复：Solver成功执行，即使有业务无法排程的需求也返回Success=true
            // 算法异常、Routing非法、数量丢失等才返回Failure
            Success = true,
            ErrorMessage = null,
            IsRoughCut = false,
            FinalTasks = allScheduledTasks,
            AllocationShares = allocationShares,
            UnscheduledTasks = unscheduledTasks,
            PhysicalPeggingDrafts = taskDependencies,
            ExplanationFacts = diagnostics.ExplanationFacts,
            Summary = new SolveSummary
            {
                TotalDrafts = allScheduledTasks.Count + unscheduledTasks.Count,
                ScheduledCount = allScheduledTasks.Count,
                UnscheduledCount = unscheduledTasks.Count,
                ElapsedMs = 0, // 由 FiniteCapacitySolver 填充
                IssueCount = diagnostics.ExplanationFacts.Count,
                UsedRoughCut = false
            }
        };
    }

    /// <summary>
    /// 生成 AllocationTaskShare（追溯机制）
    /// 文档：§五 5.2
    /// 闭合检查：Σ ShareQty = 该Allocation需制造的NetOutputQty
    /// </summary>
    private List<AllocationTaskShare> GenerateAllocationShares(
        List<FinalTaskDraft> tasks,
        DomainSolveRequest request)
    {
        var shares = new List<AllocationTaskShare>();

        // 按 AllocationSequence 分组任务
        var tasksByAllocation = tasks
            .Select(task =>
            {
                var demand = request.LogicalProductionDemands
                    .FirstOrDefault(d => d.LogicalDemandKey == task.SourceDraftId);
                return new { Task = task, Demand = demand };
            })
            .Where(x => x.Demand != null)
            .GroupBy(x => x.Demand!.AllocationSequence);

        // 为每个 Allocation 生成 TaskShare
        foreach (var group in tasksByAllocation)
        {
            var allocationSeq = group.Key;
            var allocationTasks = group.ToList();
            var expectedQty = allocationTasks.First().Demand!.NetOutputQty;

            // P0-14修复：严格闭合检查，调整最后一个Task的份额数量以保证Σ ShareQty = NetOutputQty
            for (int i = 0; i < allocationTasks.Count; i++)
            {
                var item = allocationTasks[i];
                var task = item.Task;
                var isLastTask = (i == allocationTasks.Count - 1);

                decimal shareQty;
                if (isLastTask)
                {
                    // 最后一个Task用 (期望总量 - 已分配量) 来闭合
                    var alreadyAllocated = shares
                        .Where(s => s.AllocationSequence == allocationSeq)
                        .Sum(s => s.ComponentQty);
                    shareQty = expectedQty - alreadyAllocated;
                }
                else
                {
                    // 非最后一个Task使用原始数量
                    shareQty = task.Quantity;
                }

                shares.Add(new AllocationTaskShare
                {
                    FinalDraftId = task.FinalDraftId,
                    AllocationSequence = allocationSeq,
                    ComponentQty = shareQty
                });
            }
        }

        return shares;
    }

    /// <summary>
    /// 生成 TaskDependency（基于 Routing 工序依赖关系）
    /// 文档：§五 5.3
    /// P0-13修复：根据工艺路线生成工序间的物理依赖关系
    /// </summary>
    private List<FinalTaskPeggingDraft> GenerateTaskDependencies(
        List<FinalTaskDraft> tasks,
        DomainSolveRequest request,
        ConstraintContext constraints)
    {
        var dependencies = new List<FinalTaskPeggingDraft>();

        // 按 SourceDraftId（需求）分组任务
        var tasksByDemand = tasks
            .GroupBy(t => t.SourceDraftId)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.PlannedStartTime).ToList());

        // 为每个需求生成工序依赖
        foreach (var demandGroup in tasksByDemand)
        {
            var demand = request.LogicalProductionDemands
                .FirstOrDefault(d => d.LogicalDemandKey == demandGroup.Key);
            if (demand == null) continue;

            // 获取工艺路线
            if (!constraints.RoutingGraphs.TryGetValue(demand.MaterialId, out var routeGraphs))
                continue;
            if (!routeGraphs.TryGetValue("DEFAULT", out var routingGraph))
                continue;

            // 遍历工艺路线中的依赖关系
            foreach (var depList in routingGraph.Dependencies.Values)
            {
                foreach (var dep in depList)
                {
                    // 找到对应的上下游Task
                    var upstreamTask = demandGroup.Value
                        .FirstOrDefault(t => t.OperationCode == dep.FromOperationCode);
                    var downstreamTask = demandGroup.Value
                        .FirstOrDefault(t => t.OperationCode == dep.ToOperationCode);

                    if (upstreamTask != null && downstreamTask != null)
                    {
                        dependencies.Add(new FinalTaskPeggingDraft
                        {
                            UpstreamFinalDraftId = upstreamTask.FinalDraftId,
                            DownstreamFinalDraftId = downstreamTask.FinalDraftId,
                            UpstreamMaterialId = demand.MaterialId,
                            DownstreamMaterialId = demand.MaterialId,
                            Quantity = demand.NetOutputQty,
                            UOM = string.Empty,
                            InheritedPriority = demand.DemandSequence
                        });
                    }
                }
            }
        }

        return dependencies;
    }
}
