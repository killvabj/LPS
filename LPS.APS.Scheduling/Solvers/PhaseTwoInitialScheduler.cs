using LPS.APS.Core.Dto;
using LPS.APS.Shared.Models;

namespace LPS.APS.Scheduling.Solvers;

/// <summary>
/// Phase 2: 初始有限产能排程
/// 文档：《APS_V1_1号位有限产能排程开发实施包_v1.0_20260814.md》§六 Phase 2
///
/// 职责：
/// - 按冻结策略执行初始排程（Forward/Backward/Mixed）
/// - 优先形成一版可行计划
/// - 为每个 LogicalProductionDemand 生成初步的 FinalTaskDraft
/// </summary>
internal class PhaseTwoInitialScheduler
{
    /// <summary>
    /// 执行初始有限产能排程
    /// </summary>
    public InitialScheduleResult Schedule(
        DomainSolveRequest request,
        ConstraintContext constraints)
    {
        var result = new InitialScheduleResult();

        // 获取排程方向参数
        var direction = request.StrategySnapshot.Parameters.SchedulingDirection;

        // P0-07修复：构建锁定任务的DraftId集合，用于排除已固定的需求
        var lockedDraftIds = new HashSet<string>(constraints.LockedTasks.Keys);

        // 按 DemandSequence 排序（2号位已排好序）
        var sortedDemands = request.LogicalProductionDemands
            .OrderBy(d => d.DemandSequence)
            .ToList();

        // 资源占用追踪：ResourceId → 已占用时间窗列表
        var resourceOccupancy = InitializeResourceOccupancy(request.Resources, constraints);

        // P0-07修复：先将锁定任务直接继承为FinalTask（原地保留）
        foreach (var lockedTask in constraints.LockedTasks.Values)
        {
            // 从对应的Demand获取数量、物料等信息
            var demand = request.LogicalProductionDemands
                .FirstOrDefault(d => d.LogicalDemandKey == lockedTask.DraftId);

            var inheritedTask = new FinalTaskDraft
            {
                FinalDraftId = Guid.NewGuid().ToString(),
                SourceDraftId = lockedTask.DraftId,
                MaterialId = demand?.MaterialId ?? 0,
                FactoryId = demand?.FactoryId ?? 0,
                StageCode = string.Empty, // TODO: 从ExecutionConstraint获取Stage信息
                OperationCode = string.Empty, // TODO: 从ExecutionConstraint获取Operation信息
                TaskType = "PRODUCTION", // P0-16修复：锁定任务仍是生产Task，不是ConstraintType
                ResourceId = lockedTask.ResourceId,
                ResourceCode = string.Empty,
                RouteCode = null,
                PathId = null,
                Quantity = demand?.NetOutputQty ?? 0m,
                PlannedProcessQty = demand?.PlannedProcessQty ?? 0m,
                UOM = string.Empty,
                PlannedStartTime = lockedTask.LockedStart,
                PlannedEndTime = lockedTask.LockedEnd,
                Priority = demand?.DemandSequence ?? 0,
                IsVirtual = false,
                ExecutionLockId = null // TODO: 关联ExecutionConstraint.Id
            };
            result.ScheduledTasks.Add(inheritedTask);
        }

        // 逐个需求排程
        // TODO P5: 实现合批逻辑 - 检测相同 Material/Operation/工艺的多个 Demand 是否可以合并成一个 Task
        // 文档§十 10.3: 不同Demand可以合并成一个FinalTask，只要 Material/Operation/工艺相容、Resource能力允许、交期不被破坏
        foreach (var demand in sortedDemands)
        {
            // P0-07修复：如果该Demand对应锁定任务，跳过排程（已在上面继承）
            if (lockedDraftIds.Contains(demand.LogicalDemandKey))
            {
                continue;
            }
            // 获取该需求的工艺路线
            if (!constraints.RoutingGraphs.TryGetValue(demand.MaterialId, out var routeGraphs))
            {
                // 无工艺路线 → 无法排程
                result.UnscheduledDemandKeys.Add(demand.LogicalDemandKey);
                continue;
            }

            // V1 固定使用 DEFAULT 路径
            if (!routeGraphs.TryGetValue("DEFAULT", out var routingGraph))
            {
                result.UnscheduledDemandKeys.Add(demand.LogicalDemandKey);
                continue;
            }

            // 从 StartStageCode 开始的工序列表
            var operationsToSchedule = GetOperationsFromStage(
                demand.StartStageCode,
                routingGraph,
                constraints);

            if (operationsToSchedule.Count == 0)
            {
                // P0-03修复：Routing有环或非法，属于技术失败
                result.TechnicalFailure = true;
                result.TechnicalFailureReason = $"Routing图非法或存在环：MaterialId={demand.MaterialId}, RouteCode=DEFAULT";
                return result;
            }

            // 排程该需求的所有工序
            var demandTasks = ScheduleDemandOperations(
                demand,
                operationsToSchedule,
                routingGraph,
                direction,
                constraints,
                resourceOccupancy,
                request.PlanningStart,
                request.PlanningEnd);

            if (demandTasks.Count == 0)
            {
                result.UnscheduledDemandKeys.Add(demand.LogicalDemandKey);
            }
            else
            {
                result.ScheduledTasks.AddRange(demandTasks);
            }
        }

        return result;
    }

    /// <summary>
    /// 初始化资源占用追踪
    /// 文档：§四 4.8、§六 Phase 1
    /// 预填充 Execution/Firm/Frozen 锁定任务的资源占用
    /// P0-06修复：同时预填充Candidate外Domain ACTIVE共享资源阻挡块
    /// </summary>
    private Dictionary<int, List<TimeWindow>> InitializeResourceOccupancy(
        IReadOnlyList<ResourceDefinition> resources,
        ConstraintContext constraints)
    {
        var occupancy = new Dictionary<int, List<TimeWindow>>();
        foreach (var resource in resources)
        {
            occupancy[resource.ResourceId] = new List<TimeWindow>();
        }

        // 预填充锁定任务的资源占用
        foreach (var lockedTask in constraints.LockedTasks.Values)
        {
            if (occupancy.ContainsKey(lockedTask.ResourceId))
            {
                occupancy[lockedTask.ResourceId].Add(
                    new TimeWindow(lockedTask.LockedStart, lockedTask.LockedEnd));
            }
        }

        // P0-06修复：预填充外Domain ResourceBlock（Candidate外ACTIVE共享资源阻挡）
        foreach (var blockList in constraints.ResourceBlocks.Values)
        {
            foreach (var block in blockList)
            {
                if (occupancy.ContainsKey(block.ResourceId))
                {
                    occupancy[block.ResourceId].Add(
                        new TimeWindow(block.StartTime, block.EndTime));
                }
            }
        }

        return occupancy;
    }

    /// <summary>
    /// 获取从指定阶段开始的工序列表（拓扑排序）
    /// 文档：§四 4.4 RoutingDependency，§七 Level 0硬约束
    /// </summary>
    private List<OperationNode> GetOperationsFromStage(
        string startStageCode,
        RoutingGraph routingGraph,
        ConstraintContext constraints)
    {
        // 构建邻接表和入度表
        var adjacency = new Dictionary<string, List<string>>();
        var inDegree = new Dictionary<string, int>();

        foreach (var op in routingGraph.Operations.Values)
        {
            adjacency[op.OperationCode] = new List<string>();
            inDegree[op.OperationCode] = 0;
        }

        // 根据 RoutingDependency 构建图
        foreach (var dep in routingGraph.Dependencies.Values.SelectMany(list => list))
        {
            if (adjacency.ContainsKey(dep.FromOperationCode) &&
                adjacency.ContainsKey(dep.ToOperationCode))
            {
                adjacency[dep.FromOperationCode].Add(dep.ToOperationCode);
                inDegree[dep.ToOperationCode]++;
            }
        }

        // Kahn 算法：拓扑排序
        var queue = new Queue<string>();
        var result = new List<OperationNode>();

        // 将入度为0的工序加入队列
        foreach (var op in routingGraph.Operations.Values)
        {
            if (inDegree[op.OperationCode] == 0)
            {
                queue.Enqueue(op.OperationCode);
            }
        }

        // BFS 拓扑排序
        while (queue.Count > 0)
        {
            var currentCode = queue.Dequeue();
            var currentOp = routingGraph.Operations[currentCode];
            result.Add(currentOp);

            // 减少后继工序的入度
            foreach (var successor in adjacency[currentCode])
            {
                inDegree[successor]--;
                if (inDegree[successor] == 0)
                {
                    queue.Enqueue(successor);
                }
            }
        }

        // P0-03修复：Routing有环时返回空列表，由调用方判定为技术失败
        if (result.Count < routingGraph.Operations.Count)
        {
            // Routing图存在环，属于输入数据结构非法
            return new List<OperationNode>();
        }

        // P0-02修复：根据StartStageCode裁剪已完成的Stage
        if (!string.IsNullOrEmpty(startStageCode))
        {
            // 找到StartStageCode对应的第一个工序位置
            var startIndex = result.FindIndex(op => op.StageCode == startStageCode);

            if (startIndex == -1)
            {
                // StartStageCode在Routing中不存在
                // 这可能是PI Position数据问题或Routing更新后Stage定义变化
                // 保守处理：返回整条Routing，由排程阶段的时间约束自然过滤已完成工序
                // 不作为技术失败，因为这是输入数据不一致，不是Solver算法错误
                return result;
            }

            if (startIndex > 0)
            {
                // 裁剪掉前面已完成的Stage
                result = result.Skip(startIndex).ToList();
            }
        }

        return result;
    }

    /// <summary>
    /// 排程单个需求的所有工序
    /// 文档：§八 Forward/Backward/Mixed
    /// </summary>
    private List<FinalTaskDraft> ScheduleDemandOperations(
        LogicalProductionDemand demand,
        List<OperationNode> operations,
        RoutingGraph routingGraph,
        string direction,
        ConstraintContext constraints,
        Dictionary<int, List<TimeWindow>> resourceOccupancy,
        DateTime planningStart,
        DateTime planningEnd)
    {
        var tasks = new List<FinalTaskDraft>();

        // 根据排程方向选择策略
        if (direction == "BACKWARD")
        {
            tasks = ScheduleBackward(demand, operations, routingGraph, constraints, resourceOccupancy, planningStart, planningEnd);
        }
        else if (direction == "FORWARD")
        {
            tasks = ScheduleForward(demand, operations, routingGraph, constraints, resourceOccupancy, planningStart, planningEnd);
        }
        else // MIXED 或其他
        {
            // 先尝试倒排，失败则转正排（§八 8.3 Mixed模式）
            tasks = ScheduleBackward(demand, operations, routingGraph, constraints, resourceOccupancy, planningStart, planningEnd);
            if (tasks.Count == 0)
            {
                tasks = ScheduleForward(demand, operations, routingGraph, constraints, resourceOccupancy, planningStart, planningEnd);
            }
        }

        return tasks;
    }

    /// <summary>
    /// 倒排：从 RequiredAvailableTime 往前推
    /// P0-05修复：倒排也必须服从物料时间约束，Task不能早于Material AvailableTime
    /// </summary>
    private List<FinalTaskDraft> ScheduleBackward(
        LogicalProductionDemand demand,
        List<OperationNode> operations,
        RoutingGraph routingGraph,
        ConstraintContext constraints,
        Dictionary<int, List<TimeWindow>> resourceOccupancy,
        DateTime planningStart,
        DateTime planningEnd)
    {
        var tasks = new List<FinalTaskDraft>();
        var currentEndTime = demand.RequiredAvailableTime;

        // P0-05修复：获取物料最早可用时间，作为倒排的硬约束下界
        var materialEarliestTime = GetMaterialEarliestTime(
            demand.AllocationSequence,
            demand.NetOutputQty,
            constraints,
            planningStart,
            out bool isMaterialSufficient);

        // P0-05修复：物料总量不足时，标记为业务Unscheduled（不是技术失败）
        if (!isMaterialSufficient)
        {
            return new List<FinalTaskDraft>(); // 物料总量不足
        }

        // 从最后一道工序往前倒推
        for (int i = operations.Count - 1; i >= 0; i--)
        {
            var operation = operations[i];

            // 查找合格资源
            var eligibleResources = GetEligibleResources(demand.MaterialId, operation.OperationCode, constraints);
            if (eligibleResources.Count == 0)
            {
                return new List<FinalTaskDraft>(); // 无合格资源
            }

            // 尝试在合格资源上找时间槽
            FinalTaskDraft? scheduledTask = null;
            foreach (var resourceId in eligibleResources)
            {
                // P0-04修复：Duration = StandardDuration × PlannedProcessQty ÷ CapacityFactor
                var capacityFactor = GetCapacityFactor(operation.OperationCode, resourceId, constraints);
                var adjustedDuration = operation.StandardDuration * demand.PlannedProcessQty / capacityFactor;
                var duration = TimeSpan.FromMinutes((double)adjustedDuration);

                // 计算候选开始时间
                var candidateStart = currentEndTime - duration;

                // P0-05修复：倒排Task不能早于物料可用时间
                if (candidateStart < materialEarliestTime)
                {
                    continue; // 尝试下一个资源
                }

                // 检查是否早于计划期起点
                if (candidateStart < planningStart)
                {
                    continue; // 尝试下一个资源
                }

                var slot = FindBackwardSlot(
                    candidateStart,
                    currentEndTime,
                    resourceId,
                    constraints,
                    resourceOccupancy,
                    planningStart);

                if (slot.HasValue)
                {
                    // 找到可用槽 → 生成任务
                    scheduledTask = CreateTask(demand, operation, resourceId, slot.Value.Start, slot.Value.End);

                    // 更新资源占用
                    resourceOccupancy[resourceId].Add(new TimeWindow(slot.Value.Start, slot.Value.End));

                    // P0-17修复：应用Routing LagTime到前序工序的结束时间约束
                    currentEndTime = slot.Value.Start;
                    if (i > 0)
                    {
                        var prevOperation = operations[i - 1];
                        var lagTime = GetLagTime(prevOperation.OperationCode, operation.OperationCode, routingGraph);
                        currentEndTime = currentEndTime.AddMinutes(-(double)lagTime);
                    }
                    break;
                }
            }

            if (scheduledTask == null)
            {
                return new List<FinalTaskDraft>(); // 排程失败
            }

            tasks.Insert(0, scheduledTask); // 倒序插入
        }

        return tasks;
    }

    /// <summary>
    /// 正排：从物料可用时间往后推
    /// P0-17修复：应用Routing LagTime到工序间时间依赖
    /// </summary>
    private List<FinalTaskDraft> ScheduleForward(
        LogicalProductionDemand demand,
        List<OperationNode> operations,
        RoutingGraph routingGraph,
        ConstraintContext constraints,
        Dictionary<int, List<TimeWindow>> resourceOccupancy,
        DateTime planningStart,
        DateTime planningEnd)
    {
        var tasks = new List<FinalTaskDraft>();

        // 获取物料最早可用时间
        // P0-05修复：传入所需数量，根据累计可用量确定启动时间，并验证总量是否足够
        var earliestStart = GetMaterialEarliestTime(
            demand.AllocationSequence,
            demand.NetOutputQty,
            constraints,
            planningStart,
            out bool isMaterialSufficient);

        // P0-05修复：物料总量不足时，标记为业务Unscheduled（不是技术失败）
        if (!isMaterialSufficient)
        {
            return new List<FinalTaskDraft>(); // 物料总量不足
        }

        // 从第一道工序往后推
        // TODO P7: Stage overlap / 阈值启动
        // 文档§十二：如果配置了阈值数量，上游完成达到阈值后下游即可开始，无需等待整批全部完成
        // 这是保留 40件/60件 分段 AvailableTime 的重要原因
        for (int i = 0; i < operations.Count; i++)
        {
            var operation = operations[i];

            // 查找合格资源
            var eligibleResources = GetEligibleResources(demand.MaterialId, operation.OperationCode, constraints);
            if (eligibleResources.Count == 0)
            {
                return new List<FinalTaskDraft>(); // 无合格资源
            }

            // 尝试在合格资源上找时间槽
            FinalTaskDraft? scheduledTask = null;
            foreach (var resourceId in eligibleResources)
            {
                // P0-04修复：Duration = StandardDuration × PlannedProcessQty ÷ CapacityFactor
                var capacityFactor = GetCapacityFactor(operation.OperationCode, resourceId, constraints);
                var adjustedDuration = operation.StandardDuration * demand.PlannedProcessQty / capacityFactor;
                var duration = TimeSpan.FromMinutes((double)adjustedDuration);

                var slot = FindForwardSlot(
                    earliestStart,
                    duration,
                    resourceId,
                    constraints,
                    resourceOccupancy,
                    planningEnd);

                if (slot.HasValue)
                {
                    scheduledTask = CreateTask(demand, operation, resourceId, slot.Value.Start, slot.Value.End);
                    resourceOccupancy[resourceId].Add(new TimeWindow(slot.Value.Start, slot.Value.End));

                    // P0-17修复：应用Routing LagTime到下道工序的最早开始时间
                    earliestStart = slot.Value.End;
                    if (i < operations.Count - 1)
                    {
                        var nextOperation = operations[i + 1];
                        var lagTime = GetLagTime(operation.OperationCode, nextOperation.OperationCode, routingGraph);
                        earliestStart = earliestStart.AddMinutes((double)lagTime);
                    }

                    break;
                }
            }

            if (scheduledTask == null)
            {
                return new List<FinalTaskDraft>(); // 排程失败
            }

            tasks.Add(scheduledTask);
        }

        return tasks;
    }

    /// <summary>
    /// 获取物料最早可用时间
    /// 文档：§四 4.6、§十二 Stage overlap
    /// P0-05修复：支持多段Quantity-Time，根据所需数量确定可用时间，并验证总量是否足够
    /// </summary>
    private DateTime GetMaterialEarliestTime(
        long allocationSequence,
        decimal requiredQuantity,
        ConstraintContext constraints,
        DateTime planningStart,
        out bool isSufficient)
    {
        isSufficient = true;

        if (constraints.MaterialAvailability.TryGetValue(allocationSequence, out var segments) && segments.Count > 0)
        {
            // P0-05修复：累计可用数量，找到满足需求数量的最早时间
            decimal accumulated = 0m;
            foreach (var segment in segments.OrderBy(s => s.AvailableTime))
            {
                accumulated += segment.Quantity;
                if (accumulated >= requiredQuantity)
                {
                    // 累计数量满足需求，返回该段时间
                    return segment.AvailableTime;
                }
            }

            // P0-05修复：所有段累计仍不足需求量，标记不足并返回最后一段时间
            isSufficient = false;
            return segments.Max(s => s.AvailableTime);
        }
        return planningStart;
    }

    /// <summary>
    /// 获取工序的合格资源列表（按优先级排序）
    /// </summary>
    private List<int> GetEligibleResources(
        int materialId,
        string operationCode,
        ConstraintContext constraints)
    {
        var key = $"DEFAULT::{operationCode}"; // V1 固定 DEFAULT 路径
        if (constraints.OperationResourceEligibility.TryGetValue(key, out var resources))
        {
            return resources;
        }
        return new List<int>();
    }

    /// <summary>
    /// 倒排寻找时间槽
    /// </summary>
    private TimeWindow? FindBackwardSlot(
        DateTime candidateStart,
        DateTime candidateEnd,
        int resourceId,
        ConstraintContext constraints,
        Dictionary<int, List<TimeWindow>> resourceOccupancy,
        DateTime planningStart)
    {
        var candidate = new TimeWindow(candidateStart, candidateEnd);

        // 检查日历约束
        if (!IsWithinCalendar(candidate, resourceId, constraints))
        {
            return null;
        }

        // 检查资源占用冲突
        if (HasConflict(candidate, resourceId, resourceOccupancy))
        {
            return null;
        }

        return candidate;
    }

    /// <summary>
    /// 正排寻找时间槽
    /// </summary>
    private TimeWindow? FindForwardSlot(
        DateTime earliestStart,
        TimeSpan duration,
        int resourceId,
        ConstraintContext constraints,
        Dictionary<int, List<TimeWindow>> resourceOccupancy,
        DateTime planningEnd)
    {
        // 获取资源日历
        if (!constraints.ResourceCalendars.TryGetValue(resourceId, out var calendar) || calendar.Count == 0)
        {
            return null;
        }

        // 遍历日历窗口
        foreach (var calWindow in calendar.OrderBy(w => w.Start))
        {
            if (calWindow.End <= earliestStart) continue;
            if (calWindow.Start >= planningEnd) break;

            var windowStart = calWindow.Start > earliestStart ? calWindow.Start : earliestStart;
            var windowEnd = calWindow.End < planningEnd ? calWindow.End : planningEnd;

            if (windowEnd - windowStart < duration) continue;

            // 在窗口内寻找空闲槽
            var slot = FindFirstAvailableSlot(windowStart, duration, resourceId, resourceOccupancy);
            if (slot.HasValue && slot.Value.End <= windowEnd)
            {
                return slot;
            }
        }

        return null;
    }

    /// <summary>
    /// 在窗口内找第一个空闲槽
    /// TODO P15: 性能优化（§十九）
    /// 当前 O(N) 扫描，目标 10万Task / 15分钟需要：
    /// - Interval Timeline（不用分钟Grid）
    /// - Resource时间轴内存索引
    /// - 避免 O(N²) 全Task扫描
    /// - Setup只局部更新
    /// - Candidate只传播实际变化
    /// </summary>
    private TimeWindow? FindFirstAvailableSlot(
        DateTime windowStart,
        TimeSpan duration,
        int resourceId,
        Dictionary<int, List<TimeWindow>> resourceOccupancy)
    {
        var occupied = resourceOccupancy[resourceId].OrderBy(w => w.Start).ToList();
        var cursor = windowStart;

        foreach (var occ in occupied)
        {
            if (occ.Start >= cursor + duration)
            {
                // 找到间隙
                return new TimeWindow(cursor, cursor + duration);
            }
            cursor = occ.End > cursor ? occ.End : cursor;
        }

        // 最后一个占用槽之后的空间
        return new TimeWindow(cursor, cursor + duration);
    }

    /// <summary>
    /// 检查候选窗口是否在日历内
    /// </summary>
    private bool IsWithinCalendar(
        TimeWindow candidate,
        int resourceId,
        ConstraintContext constraints)
    {
        if (!constraints.ResourceCalendars.TryGetValue(resourceId, out var calendar))
        {
            return false;
        }

        return calendar.Any(c => c.Start <= candidate.Start && c.End >= candidate.End);
    }

    /// <summary>
    /// 检查是否与已占用时间冲突
    /// </summary>
    private bool HasConflict(
        TimeWindow candidate,
        int resourceId,
        Dictionary<int, List<TimeWindow>> resourceOccupancy)
    {
        var occupied = resourceOccupancy[resourceId];
        return occupied.Any(o => Overlaps(candidate, o));
    }

    /// <summary>
    /// 检查两个时间窗是否重叠
    /// </summary>
    private bool Overlaps(TimeWindow a, TimeWindow b)
    {
        return a.Start < b.End && a.End > b.Start;
    }

    /// <summary>
    /// 创建 FinalTaskDraft
    /// 文档：§四 4.2、§五 5.1、§十六 Firm/Frozen/Execution 继承
    /// Task.Quantity = NetOutputQty（净合格产出）
    /// Task.PlannedProcessQty = 计划加工量（用于资源能力占用计算）
    /// TaskType 继承 Demand 的 Firm/Frozen/Execution 标记
    /// </summary>
    private FinalTaskDraft CreateTask(
        LogicalProductionDemand demand,
        OperationNode operation,
        int resourceId,
        DateTime start,
        DateTime end)
    {
        // P0-16修复：V1新生成的都是生产Task，统一使用PRODUCTION
        // UNLOCATED、无PI等作为独立标识/来源事实，不增加新TaskType
        string taskType = "PRODUCTION";

        // P0-04修复：补齐FinalTaskDraft必需字段，Duration已使用 StandardDuration × PlannedProcessQty ÷ CapacityFactor

        return new FinalTaskDraft
        {
            FinalDraftId = Guid.NewGuid().ToString(),
            SourceDraftId = demand.LogicalDemandKey,
            MaterialId = demand.MaterialId,
            FactoryId = demand.FactoryId,
            StageCode = operation.StageCode ?? string.Empty,
            OperationCode = operation.OperationCode,
            TaskType = taskType,
            ResourceId = resourceId,
            ResourceCode = string.Empty, // TODO: 从Resources查找ResourceCode
            RouteCode = null, // TODO: 从Routing获取RouteCode
            PathId = null, // TODO: 从Routing获取PathId
            Quantity = demand.NetOutputQty,
            PlannedProcessQty = demand.PlannedProcessQty,
            UOM = string.Empty,
            PlannedStartTime = start,
            PlannedEndTime = end,
            Priority = demand.DemandSequence,
            IsVirtual = false
        };
    }

    /// <summary>
    /// P0-04修复：获取资源产能系数
    /// </summary>
    private decimal GetCapacityFactor(string operationCode, int resourceId, ConstraintContext constraints)
    {
        var key = $"DEFAULT::{operationCode}"; // V1固定DEFAULT路径
        if (constraints.ResourceCapacityFactors.TryGetValue(key, out var resourceFactors))
        {
            if (resourceFactors.TryGetValue(resourceId, out var capacityFactor))
            {
                return capacityFactor;
            }
        }
        return 1.0m; // 默认产能系数为1.0
    }

    /// <summary>
    /// P0-17修复：获取工序间的Lag时间（分钟）
    /// 应用Routing LagTime到工序间时间依赖
    /// </summary>
    private decimal GetLagTime(string fromOperationCode, string toOperationCode, RoutingGraph routingGraph)
    {
        // 查找fromOperation的所有依赖边
        if (routingGraph.Dependencies.TryGetValue(fromOperationCode, out var edges))
        {
            // 找到指向toOperation的边
            var edge = edges.FirstOrDefault(e => e.ToOperationCode == toOperationCode);
            if (edge != null)
            {
                return edge.LagTime;
            }
        }
        return 0m; // 默认无延迟
    }
}

/// <summary>
/// 初始排程结果（Phase 2 输出）
/// </summary>
internal class InitialScheduleResult
{
    public List<FinalTaskDraft> ScheduledTasks { get; set; } = new();
    public List<string> UnscheduledDemandKeys { get; set; } = new();

    /// <summary>
    /// P0-03+P0-15修复：技术失败标记（Routing非法、数据结构错误等）
    /// </summary>
    public bool TechnicalFailure { get; set; } = false;
    public string? TechnicalFailureReason { get; set; }
}
