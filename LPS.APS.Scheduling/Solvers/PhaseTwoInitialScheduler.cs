using LPS.APS.Core.Dto;
using LPS.APS.Shared.Models;

namespace LPS.APS.Scheduling.Solvers;

/// <summary>
/// Phase 2: 初始有限产能排程
/// 文档：《APS_V1_1号位有限产能排程开发实施包_v1.2_20260906_PI_Position执行起点上下文冻结对齐版.md》§六 Phase 2
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

        // P1-02（StageOverlap）：读冻结大工艺重叠参数（AllowOverlap 开关 + TransferBatchQty 转运批量）
        var stageOverlap = request.StrategySnapshot.SolverStrategy.StageOverlap;

        // P1-02（OnTimeTarget，0号位 20260916 裁决）：整单按期率是冻结的 Level 2 目标——TargetPercent 是「整单按期率保护门槛」而非权重，
        // 达标前禁止 Setup/WIP/利用率等次级目标反压；IsPrimaryObjective 在 V1 恒按 true 语义、不用于开关目标层级。
        // 当前 Phase2 为确定性单次排程（无多候选择优），故以 RequiredAvailableTime（EDD）排序作为「先保按期」的贪心近似；
        // 精确的「达标门槛 → 次级优化」候选比较语义留待候选方案比较框架引入后消费。

        // P0-07修复：构建锁定任务的DraftId集合，用于排除已固定的需求
        // P1-07：复合键 (DraftId, OperationCode)，提取 DraftId 去重。
        var lockedDraftIds = new HashSet<string>(constraints.LockedTasks.Keys.Select(k => k.DraftId));

        // 跨物料时序硬约束（任务喂任务，方案A）：子先父后分层遍历。
        // 若存在 BOM 依赖环，Phase1 已标记，此处直接判技术失败。
        if (constraints.CrossMaterialHasCycle)
        {
            result.TechnicalFailure = true;
            result.TechnicalFailureReason = "跨物料 BOM 依赖存在环（任务喂任务）";
            return result;
        }

        // 按跨物料分层顺序排序（子层先、父层后，层内按 DemandSequence）；无跨物料关系时等价于旧平铺。
        var demandByKey = request.LogicalProductionDemands
            .ToDictionary(d => d.LogicalDemandKey);

        List<LogicalProductionDemand> sortedDemands;
        if (constraints.CrossMaterialOrder.Count > 0)
        {
            sortedDemands = constraints.CrossMaterialOrder
                .Select(key => demandByKey.GetValueOrDefault(key))
                .Where(d => d != null)
                .ToList()!;
        }
        else
        {
            // P1-02（OnTimeTarget）：整单按期率为 Level 2 冻结目标，恒高于次级优化，故始终按交期（EDD）优先排序，
            // DemandSequence 仅作并列稳定 tiebreaker（0号位 20260916：IsPrimaryObjective 不用于开关目标层级）。
            sortedDemands = request.LogicalProductionDemands
                .OrderBy(d => constraints.EffectiveDue(d))   // M5 第一批：Run 级覆盖交期 ?? RequiredAvailableTime
                .ThenBy(d => d.DemandSequence)
                .ToList();
        }

        // P0-08：同 PI 连续份额先于自由份额。稳定重排，仅在同 PI 组内把连续份额移到自由份额之前，
        // 跨 PI 相对顺序按「该 PI 首次出现位置」保持，避免破坏跨物料父子拓扑序。
        sortedDemands = OrderContinuityFirst(sortedDemands);

        // 资源占用追踪：ResourceId → 已占用时间窗列表
        var resourceOccupancy = InitializeResourceOccupancy(request.Resources, constraints);

        // P0-07修复：先将锁定任务直接继承为FinalTask（原地保留）
        // 第8轮P0-01修复：使用LockedQuantity和Stage/Operation，不再写空字符串
        // 2026-10-07 P0-01 二次整改（0号位 审核）：**禁止再硬编码 RouteCode="DEFAULT" / PathId=1**。
        //   锁定任务经 (StageCode, OperationCode) 反查真实路径身份；
        //   图缺失 / 节点缺失 ⇒ **Fail Closed**（不产 Task、不编造伪身份、不猜 Path，见下方 Q-3 注释）。
        foreach (var lockedTask in constraints.LockedTasks.Values)
        {
            // 从对应的Demand获取数量、物料等信息
            var demand = request.LogicalProductionDemands
                .FirstOrDefault(d => d.LogicalDemandKey == lockedTask.DraftId);

            // 真实路径身份反查（v1.6 §1：业务真值 RouteCode/PathId 必须真实，禁 DEFAULT/1）：
            //   (StageCode, OperationCode) → OperationNode。
            //   · 需求自带固定路径（A/B 桶，v1.6 `:26`）⇒ 用**该固定路径**的图解析（Path-aware）；
            //   · 无固定路径 ⇒ 回落 `TryGetSingleRoutingGraph`（**仅单路径物料可用**；多路径时该方法
            //     返回 false ⇒ 身份保持 null，**不猜**）。
            //   ⚠ 正式载体（ExecutionConstraint 补 RouteCode/PathId）**尚未冻结**（v1.6 未列此字段）
            //     ⇒ 本号位不造字段。多路径 + 无固定路径的锁定任务解不出身份 ⇒ **Fail Closed**
            //     （记入 `UnscheduledDemandKeys`、不产 Task），**不回传 null 伪身份**。
            string? lockedRouteCode = null;
            long? lockedPathId = null;
            RoutingGraph? lockedGraph = null;
            if (demand != null)
            {
                if (!string.IsNullOrEmpty(demand.RouteCode) || demand.PathId is not null)
                {
                    constraints.TryGetRoutingGraph(demand.MaterialId, demand.RouteCode, demand.PathId, out lockedGraph);
                }
                else
                {
                    constraints.TryGetSingleRoutingGraph(demand.MaterialId, out lockedGraph);
                }
            }

            OperationNode? lockedNode = null;
            if (lockedGraph != null)
            {
                lockedGraph.Operations.TryGetValue(
                    OperationNodeKey.Of(lockedTask.StageCode, lockedTask.OperationCode), out lockedNode);
            }

            // Q-3 Fail Closed（0号位 2026-10-07 裁决 Q-3 `:186`：「Route/Path 缺失时应 Fail Closed 或
            //   进入明确异常，**不得跨 Path 寻找替代节点**」；同件职责表 `:357`：「A/B 固定 Route/Path …
            //   **缺值 Fail Closed**」）：
            //   锁定任务按 A/B「固定真实 RouteCode/PathId」语义处理 —— 必须能解出**真实**路径身份才产出
            //   FinalTask。解不出身份（图缺失 / 节点缺失 / 需求缺失）⇒ **不产伪身份 Task**，明确记为未排程
            //   （与需求级 Fail Closed 同一口径：`UnscheduledDemandKeys` + 不产 Task），
            //   **不得**回退 `TryGetSingleRoutingGraph` 猜唯一 Path、**不得**跨 Path 找替代节点。
            if (lockedNode == null)
            {
                result.UnscheduledDemandKeys.Add(lockedTask.DraftId);
                continue;
            }

            lockedRouteCode = lockedNode.RouteCode;
            lockedPathId = lockedNode.PathId;

            var inheritedTask = new FinalTaskDraft
            {
                FinalDraftId = Guid.NewGuid().ToString(),
                SourceDraftId = lockedTask.DraftId,
                MaterialId = demand?.MaterialId ?? 0,
                FactoryId = demand?.FactoryId ?? 0,
                StageCode = lockedTask.StageCode ?? string.Empty,
                OperationCode = lockedTask.OperationCode ?? string.Empty,
                TaskType = "PRODUCTION", // P0-16修复：锁定任务仍是生产Task，不是ConstraintType
                ResourceId = lockedTask.ResourceId,
                ResourceCode = GetResourceCode(lockedTask.ResourceId, constraints),
                // P0-01 整改：真实路径身份（反查失败时为 null，不再 DEFAULT/1）
                RouteCode = lockedRouteCode,
                PathId = lockedPathId,
                Quantity = lockedTask.LockedNetOutputQty ?? lockedTask.LockedQuantity ?? demand?.NetOutputQty ?? 0m,
                PlannedProcessQty = lockedTask.LockedPlannedProcessQty ?? lockedTask.LockedQuantity ?? demand?.PlannedProcessQty ?? 0m,
                UOM = demand?.UOM ?? string.Empty,
                PlannedStartTime = lockedTask.LockedStart,
                PlannedEndTime = lockedTask.LockedEnd,
                SetupTime = 0m,
                SetupSource = null,   // SetupSource 填充：锁定继承任务无 1号位 Setup 解析来源 → null（2号位 落库留空）
                Priority = demand?.DemandSequence ?? 0,
                IsVirtual = false,
                ExecutionLockId = null, // TODO: 关联ExecutionConstraint.Id
                // v1.6 §1：FinalTask 一律原样回传 ContinuationKey；路径身份可解出时才生成执行批键
                //（与上方「不编造 RouteCode/PathId」同口径 —— 解不出身份就不给批键，不留伪真值）。
                // P0-02 后键域改为 (需求键, 批序号)：无真实 Path ⇒ 该 Task 不构成「一条完整 Path 的执行批」
                // ⇒ 仍不给批键（守卫理由由「身份反推」改为「无完整 Path 不构成执行批」，语义更贴合冻结口径）。
                ContinuationKey = demand?.ContinuationKey,
                ExecutionBatchDraftKey = lockedPathId is null
                    ? null
                    : ExecutionBatchKey(lockedTask.DraftId)
            };
            result.ScheduledTasks.Add(inheritedTask);
        }

        // P1-02 item1 接线（阶段二）：产品时间线以继承的锁定 Task 重置种子（它们是可追溯上一产品，v1.2 §14.3）。
        // Schedule 每次进入都重置——Phase4 Fallback 重跑本方法时避免重复累计。
        constraints.ProductTimeline = ResourceProductTimeline.FromTasks(result.ScheduledTasks);

        // P0-08：记录各 PI 连续份额的完成时间，供同 PI 自由份额做「不得早于连续份额」的时间下界。
        // 先登记已锁定的连续份额（原地继承，不参与后续排程循环）。
        var continuityCompletionByPI = new Dictionary<string, DateTime>();
        foreach (var lockedTask in constraints.LockedTasks.Values)
        {
            var lockedDemand = request.LogicalProductionDemands
                .FirstOrDefault(d => d.LogicalDemandKey == lockedTask.DraftId);
            if (lockedDemand?.IsContinuation == true && !string.IsNullOrEmpty(lockedDemand.ProductionInstructionNo))
            {
                var pi = lockedDemand.ProductionInstructionNo!;
                if (!continuityCompletionByPI.TryGetValue(pi, out var existing) || lockedTask.LockedEnd > existing)
                    continuityCompletionByPI[pi] = lockedTask.LockedEnd;
            }
        }

        // 逐个需求排程
        // 第4轮Merge修复：记录Demand到Task的份额追溯，支持合批
        var allocationTaskShare = new Dictionary<string, List<(string DemandKey, decimal ShareQty)>>();

        // 跨物料时序（块3/块4）：记录每个已排需求的最晚完成时间，供父件取子件完成时间作为动态物料下界。
        var demandCompletion = new Dictionary<string, DateTime>();

        foreach (var demand in sortedDemands)
        {
            // B-1（0号位 2026-09-29 裁决 §2.3）：StageSeq 全序冲突的物料
            // **不生成可能顺序错误的正式计划** —— 直接置 Unscheduled（Reason 由 Phase5 补 STAGE_SEQUENCE_CONFLICT）。
            // 注意：此处不删 Routing（Routing 本身合法，问题在 StagePath 数据），只拒绝出计划。
            if (constraints.StageSequenceConflictMaterialIds.Contains(demand.MaterialId))
            {
                result.UnscheduledDemandKeys.Add(demand.LogicalDemandKey);
                continue;
            }

            // 第8轮P0-01修复：部分数量冻结处理
            // 第9轮P0-01完整闭环：真正减掉锁定数量，只排剩余份额
            // 如果该Demand有锁定任务，检查锁定数量：
            // - 锁定数量 >= Demand总量：完全锁定，跳过
            // - 锁定数量 < Demand总量：部分锁定，只排剩余部分
            LogicalProductionDemand actualDemand = demand;

            if (lockedDraftIds.Contains(demand.LogicalDemandKey))
            {
                // P1-07：同 LogicalDemand 可有多操作锚点（多 ExecutionConstraint），
                // 按复合键 (DraftId, OperationCode) 聚合该需求所有锁定任务的锁定数量。
                var demandLockedTasks = constraints.LockedTasks.Values
                    .Where(t => t.DraftId == demand.LogicalDemandKey)
                    .ToList();

                // P1-01：净产出与加工量分别取锁定字段（YIELD 场景），缺省依次回落 LockedQuantity → 需求原值
                var lockedNetOutputQty = demandLockedTasks
                    .Sum(t => t.LockedNetOutputQty ?? t.LockedQuantity ?? demand.NetOutputQty);
                var lockedPlannedProcessQty = demandLockedTasks
                    .Sum(t => t.LockedPlannedProcessQty ?? t.LockedQuantity ?? demand.PlannedProcessQty);

                // 如果锁定数量 >= 需求总量，完全锁定，跳过排程
                if (lockedNetOutputQty >= demand.NetOutputQty)
                {
                    continue;
                }

                // 部分锁定：计算剩余数量，创建剩余需求对象
                var remainingNetOutputQty = demand.NetOutputQty - lockedNetOutputQty;
                var remainingPlannedProcessQty = demand.PlannedProcessQty - lockedPlannedProcessQty;

                // 创建剩余需求对象（只排这部分）
                actualDemand = new LogicalProductionDemand
                {
                    LogicalDemandKey = demand.LogicalDemandKey,
                    PlanVersionId = demand.PlanVersionId,
                    DomainKey = demand.DomainKey,
                    AllocationSequence = demand.AllocationSequence,
                    DemandKey = demand.DemandKey,
                    OrderId = demand.OrderId,
                    MaterialId = demand.MaterialId,
                    FactoryId = demand.FactoryId,
                    StartStageCode = demand.StartStageCode,
                    StartOperationCode = demand.StartOperationCode,
                    // 供给阈值 Stage（PM《BOM取用…完整链路说明》§八）：2号位 回填、1号位 消费。
                    // 必须随克隆一起带走 —— 漏拷会让「部分锁定」需求静默丢失阈值语义（与 2号位 2026-09-28 回执 §5.1 对应）。
                    RequiredStageCode = demand.RequiredStageCode,
                    NetOutputQty = remainingNetOutputQty,
                    PlannedProcessQty = remainingPlannedProcessQty,
                    RequiredAvailableTime = demand.RequiredAvailableTime,
                    DemandSequence = demand.DemandSequence,
                    ProductionInstructionNo = demand.ProductionInstructionNo,
                    IsUnlocated = demand.IsUnlocated,
                    UOM = demand.UOM,
                    PreferredResourceId = demand.PreferredResourceId,
                    FallbackResourceId = demand.FallbackResourceId,
                    IsContinuation = demand.IsContinuation,
                    // ── 以下 5 项随克隆一并带走（与上方 RequiredStageCode 同款理由）──
                    // 漏拷 = 「部分锁定」需求静默丢失路径身份/连续键/拆合批约束/软偏好，
                    // 表现为：C桶多路径退化为「无固定路径 ⇒ 候选择优」（可接受）但 A/B 固定路径
                    // 退化为候选择优（**违反 v1.6「A/B 固定 Route/Path」**），且 ContinuationKey 断链。
                    ContinuationKey = demand.ContinuationKey,
                    RouteCode = demand.RouteCode,
                    PathId = demand.PathId,
                    NoSplitMerge = demand.NoSplitMerge,
                    PreferredResourceCode = demand.PreferredResourceCode
                };
            }
            // ── P0-05（0号位 2026-10-07 (5).md）：连续份额**输入完整性 Fail Closed** ──
            //   判据必须**从 `IsContinuation` 出发**，而**不是**从「有没有 RouteCode」倒推。
            //   旧实现用 `RouteCode 非空 || PathId 非空` 决定「固定路径 / 自由候选」⇒
            //   `IsContinuation=true` 但缺 Route/Path 的 A/B 会掉进自由候选选路（把 A/B 当 C 桶），
            //   违反 v1.6「A/B 输入必须带真实固定 RouteCode / PathId / StartOperation 且不得切换 Routing」。
            //
            //   硬校验（任一缺失 ⇒ 明确 Unscheduled，**禁止**进入 Routing 竞争；
            //   指定 Route/Path 是否**真实存在于图中**由下方 `TryGetRoutingGraph` 继续 Fail Closed）。
            if (actualDemand.IsContinuation)
            {
                var missing = new List<string>(5);
                if (string.IsNullOrEmpty(actualDemand.ContinuationKey)) missing.Add("ContinuationKey");
                if (string.IsNullOrEmpty(actualDemand.RouteCode)) missing.Add("RouteCode");
                if (actualDemand.PathId is null) missing.Add("PathId");
                if (string.IsNullOrEmpty(actualDemand.StartOperationCode)) missing.Add("StartOperationCode");
                if (!actualDemand.NoSplitMerge) missing.Add("NoSplitMerge");

                if (missing.Count > 0)
                {
                    // 与 Phase2 其它 Fail Closed 点同口径（:129/:203/:314/:325/:364…）：**只记 UnscheduledDemandKeys、
                    // 不产 Task、不写 TraceNotes**（TraceNotes 会经 SolveTraceNote 落库给 2号位，不得自造码）。
                    result.UnscheduledDemandKeys.Add(actualDemand.LogicalDemandKey);
                    continue;
                }
            }

            // ── 需求级路径候选解析（Path-aware，0号位 2026-10-07 裁决 Q-3 / §四）──
            //
            //   ① 需求**自带固定路径**（v1.6：「A/B 输入必须带真实固定 RouteCode / PathId / StartOperation」）
            //      ⇒ 固定该条，**不选路**；图中不存在该路径 ⇒ **Fail Closed**
            //      （Q-3 红线：不得回退 TryGetSingleRoutingGraph 猜唯一 Path）。
            //   ② 无固定路径（Free Slice / C桶）
            //      ⇒ 取该物料全部候选 Path；1 条 = 既有单路径行为（零回归），N 条 = 候选内联合择优。
            //
            //   ⚠ 禁止用「候选条数」反推 A/B/C 身份（0号位 裁决 §四）—— 桶身份只认 IsContinuation / 固定路径。
            if (!constraints.TryGetRoutingGraphs(actualDemand.MaterialId, out var allPaths))
            {
                // 无工艺路线 → 无法排程
                result.UnscheduledDemandKeys.Add(actualDemand.LogicalDemandKey);
                continue;
            }

            List<KeyValuePair<RoutePathKey, RoutingGraph>> candidatePaths;
            if (!string.IsNullOrEmpty(actualDemand.RouteCode) || actualDemand.PathId is not null)
            {
                if (!constraints.TryGetRoutingGraph(
                        actualDemand.MaterialId, actualDemand.RouteCode, actualDemand.PathId, out var fixedGraph))
                {
                    // Fail Closed：需求声明了固定路径但图缺失 ⇒ 不排（不猜、不跨 Path 找替代节点）
                    result.UnscheduledDemandKeys.Add(actualDemand.LogicalDemandKey);
                    continue;
                }

                candidatePaths = new List<KeyValuePair<RoutePathKey, RoutingGraph>>
                {
                    new(RoutePathKey.Of(actualDemand.RouteCode, actualDemand.PathId!.Value), fixedGraph)
                };
            }
            else
            {
                candidatePaths = allPaths;
            }

            // 步骤 1：为每条候选求工序列表（纯读，不改任何上下文）。
            var plannedCandidates = new List<(RoutePathKey Key, RoutingGraph Graph, List<OperationNode> Ops)>();
            foreach (var (pathKey, graph) in candidatePaths)
            {
                var ops = GetOperationsFromStage(
                    actualDemand.StartStageCode,
                    actualDemand.StartOperationCode,
                    graph,
                    constraints,
                    out var reason);

                if (ops.Count == 0 && reason == null)
                {
                    // P0-03修复：Routing有环或非法，属于技术失败（与既有单路径语义一致）
                    result.TechnicalFailure = true;
                    result.TechnicalFailureReason = $"Routing图非法或存在环：MaterialId={actualDemand.MaterialId}, Route={pathKey}";
                    return result;
                }

                plannedCandidates.Add((pathKey, graph, ops));
            }

            if (plannedCandidates.All(p => p.Ops.Count == 0))
            {
                // S26：全部候选的 StartStage/StartOperation 均非法 ⇒ 业务 Unscheduled（非技术失败，不静默回退）
                result.UnscheduledDemandKeys.Add(actualDemand.LogicalDemandKey);
                continue;
            }

            // 块3/块4（任务喂任务，方案A）：子件完成时间 → 父件动态物料下界。
            // 拓扑序保证排父件时子件已排过；子件未成功排程则父件缺料 → Unscheduled。
            var dynamicMaterialFloor = GetDynamicMaterialFloor(
                demand.LogicalDemandKey, constraints, demandCompletion, out bool childUnavailable);

            if (childUnavailable)
            {
                result.UnscheduledDemandKeys.Add(demand.LogicalDemandKey);
                continue;
            }

            // P0-08：同 PI 自由份额不得排到连续份额之前（也不得与其时间重叠）。
            // 自由份额的最早开始下界 = max(跨物料子件下界, 同 PI 连续份额完成时间)。
            if (!actualDemand.IsContinuation && !string.IsNullOrEmpty(actualDemand.ProductionInstructionNo)
                && continuityCompletionByPI.TryGetValue(actualDemand.ProductionInstructionNo!, out var continuityEnd)
                && continuityEnd > dynamicMaterialFloor)
            {
                dynamicMaterialFloor = continuityEnd;
            }

            // ── P0-01 / P0-02 / P0-03（0号位 2026-10-07《未命名的Markdown文件 (5).md》）──
            //   执行批**先于 Routing 选择**形成，判词根因是旧实现把 `LogicalDemand` **当成了** Execution Batch
            //   （批身份由 Route/Path 反推 —— 同一 Demand 拆出的两批会撞成同一个键）。
            //
            //   0号位 指定链：
            //     `Free Slice → Batch Policy → 1..N ExecutionBatchDraft(BatchDraftKey, Qty)
            //      → 每个 Batch 分别进行 Direction + RoutingCandidate + Resource + Calendar + Setup 联合求解
            //      → 每个 Batch 选择一条完整 Path
            //      → 同 Batch 全部 Operation FinalTask 共享 BatchDraftKey → 不同 Batch 必定不同 Key`
            var batches = FormExecutionBatches(actualDemand, constraints.ExecutionBatchPolicy);

            // 批键**先于试排/择优/落定**登记 ⇒ Phase4 重建 Task 时经 ResolveExecutionBatchKeyForRebuild 复用同键
            //（局部修复不得把同一执行批劈成两个键）。
            constraints.ExecutionBatchDraftKeys[actualDemand.LogicalDemandKey] =
                batches.Select(b => b.BatchDraftKey).ToList();

            // 多批时**禁止 Merge**：Merge 会把本批工序并入**别的批**的既有 Task ⇒ 一个 Task 混两个批键，
            //   违反「同 Batch 共 Key、不同 Batch 必不同键」。
            // 单批（生产现状：⑧块 Batch Policy 载体未达 1号位 ⇒ 恒 1 批）行为与旧版**逐字一致**（零回归）。
            var multiBatch = batches.Count > 1;
            var allowMergeForDemand = request.StrategySnapshot.Parameters.AllowMerge && !multiBatch;

            var demandTasksAll = new List<FinalTaskDraft>();
            var batchFailed = false;

            foreach (var batch in batches)
            {
                // 单批：直接用原实例（零回归）；多批才克隆并覆写本批数量。
                var batchDemand = multiBatch
                    ? CloneDemandWithBatchQty(actualDemand, batch.NetOutputQty, batch.PlannedProcessQty)
                    : actualDemand;

                // 步骤 2：候选试排（每条候选在**同一初始上下文**上跑完整排程，互不污染）。
                //   单候选（V1 常态 / A/B 固定路径）⇒ 试排后在真实上下文重跑，结果与既有完全一致（零回归）。
                var realTimeline = constraints.ProductTimeline;
                var trials = new List<(RoutePathKey Key, bool Feasible, DateTime Completion, decimal SetupMinutes)>();

                // 试排隔离：`constraints.TraceNotes` 是**共享可变**状态（Setup 追踪三元组在此追加），
                // 试排会把它当真实产出写进去 ⇒ 多候选试排会留下 N 份重复 trace。
                // 记下水位，试排结束回滚；步骤 4 在真实上下文重跑会重新写恰好一份。
                var traceNotesWatermark = constraints.TraceNotes.Count;

                foreach (var (pathKey, graph, ops) in plannedCandidates)
                {
                    if (ops.Count == 0)
                    {
                        trials.Add((pathKey, false, DateTime.MinValue, 0m));
                        continue;
                    }

                    constraints.ProductTimeline = realTimeline.Clone();
                    var trialOccupancy = CloneOccupancy(resourceOccupancy);
                    var trialTasks = new List<FinalTaskDraft>(result.ScheduledTasks);
                    var trialShares = CloneShares(allocationTaskShare);
                    var beforeEnds = trialTasks.ToDictionary(t => t.FinalDraftId, t => t.PlannedEndTime);

                    var produced = RunDemandSchedule(
                        batchDemand, ops, graph, direction, constraints, trialOccupancy,
                        trialTasks, trialShares, demandByKey, request.PlanningStart, request.PlanningEnd,
                        dynamicMaterialFloor, stageOverlap, allowMergeForDemand, batch.BatchDraftKey);

                    // Merge 成功时返回空 List，且替换了 scheduledTasks 中的既有 Task（完成时间变化）
                    var merged = produced.Count == 0 &&
                                 trialTasks.Any(t => beforeEnds.TryGetValue(t.FinalDraftId, out var oldEnd)
                                                     && oldEnd != t.PlannedEndTime);

                    var feasible = produced.Count > 0 || merged;

                    // ── P0-04（0号位 2026-10-07 (5).md）：Merge 候选必须取**合并后真实 PlannedEndTime** ──
                    //   旧实现 Merge 成功给 `DateTime.MinValue`（「并入既有 Task，不产生新完成时间」）⇒
                    //   `DelayMinutes(MinValue, due)` 恒 0（见 :913 短路）⇒ FORWARD 下被误评为「0 延期、完成最早」，
                    //   **永远压过真实更优的另一条 Routing**，择优结果反向。
                    DateTime completion;
                    if (produced.Count > 0)
                    {
                        completion = produced.Max(t => t.PlannedEndTime);
                    }
                    else if (merged)
                    {
                        // 合并成功的 signal：既有 Task 的 PlannedEndTime 相对试排前发生了变化。
                        var mergedTask = trialTasks.FirstOrDefault(t =>
                            beforeEnds.TryGetValue(t.FinalDraftId, out var oldEnd) && oldEnd != t.PlannedEndTime);
                        completion = mergedTask?.PlannedEndTime ?? DateTime.MinValue;
                    }
                    else
                    {
                        completion = DateTime.MinValue;   // 不可行候选：仅占位，SelectBestRoutingCandidate 只取可行者
                    }

                    var setupMinutes = produced.Sum(t => t.SetupTime);

                    trials.Add((pathKey, feasible, completion, setupMinutes));
                }

                constraints.ProductTimeline = realTimeline;   // 试排结束，还原真实上下文（试排不污染）

                // 试排产生的 Setup 追踪一并回滚（见上方 watermark）；落定重跑会重新写一份。
                if (constraints.TraceNotes.Count > traceNotesWatermark)
                {
                    constraints.TraceNotes.RemoveRange(
                        traceNotesWatermark, constraints.TraceNotes.Count - traceNotesWatermark);
                }

                // 步骤 3：**本批**候选内择优（Q-1 冻结四层目标；P0-03：Routing 择优的业务单位是执行批，不是需求）。
                var winnerIndex = SelectBestRoutingCandidate(trials, batchDemand, direction, constraints);
                if (winnerIndex < 0)
                {
                    // 本批全部候选不可排 ⇒ 该批落不下（需求整体记 Unscheduled，见循环后聚合）
                    batchFailed = true;
                    break;
                }

                // 步骤 4：在**真实上下文**上重跑选中候选落定（排程确定性 ⇒ 与试排同结果）。
                var winner = plannedCandidates[winnerIndex];

                if (!multiBatch)
                {
                    constraints.ChosenRoutePaths[actualDemand.LogicalDemandKey] = winner.Key;   // RT-002：局部修复不得换路径
                }
                // 多批：各批**各自**可能选中不同 Path，而 `ChosenRoutePaths` 是「需求 → 单一路径键」的单值表，
                //   **无法承载**多批结果 ⇒ 不登记（Phase4 取图回落「多路径且未登记 ⇒ 不猜」，Fail Closed）。
                //   待办 EBD-01：EBD-02（⑧块载体接入）销账后须把 `ChosenRoutePaths` 升维为「批键 → 路径键」。

                var batchTasks = RunDemandSchedule(
                    batchDemand, winner.Ops, winner.Graph, direction, constraints, resourceOccupancy,
                    result.ScheduledTasks, allocationTaskShare, demandByKey,
                    request.PlanningStart, request.PlanningEnd, dynamicMaterialFloor, stageOverlap,
                    allowMergeForDemand, batch.BatchDraftKey);

                // 第5轮Merge修复：Merge成功时返回空List，但Demand已进入TaskShare，不应标记为Unscheduled
                if (batchTasks.Count == 0)
                {
                    // 检查该Demand是否已通过Merge进入TaskShare
                    bool isMerged = allocationTaskShare.Values.Any(shares =>
                        shares.Any(s => s.DemandKey == demand.DemandKey || s.DemandKey == demand.LogicalDemandKey));

                    if (!isMerged)
                    {
                        batchFailed = true;
                        break;
                    }
                    // Merge 成功：本批无新 Task（份额已并入既有 Task），继续下一批。
                    continue;
                }

                result.ScheduledTasks.AddRange(batchTasks);
                demandTasksAll.AddRange(batchTasks);
            }

            // 批循环后的聚合。
            //   ⚠ 判据必须区分「**批失败**」与「**批以 Merge 落定**（无新 Task）」—— 后者不是失败：
            //     旧实现即「tasks==0 且 isMerged ⇒ 不标 Unscheduled」。若此处把 Merge 也当失败，
            //     需求会被误标 Unscheduled ⇒ Phase4 会把它当未排需求**整链重建**（等于把 Merge 撤销）。
            if (batchFailed)
            {
                // 任一批判失败 ⇒ 需求整体记 Unscheduled。
                //   已落定的前序批**保留**（「能排下的排下」，与「排不下不得静默丢弃」同向），
                //   需求仍进 `UnscheduledDemandKeys` 以示**未按完整执行批全部满足**。
                result.UnscheduledDemandKeys.Add(demand.LogicalDemandKey);
                continue;
            }

            if (demandTasksAll.Count == 0)
            {
                // 全部批均以 Merge 落定（份额已并入既有 Task，无新 Task）：与旧版同口径 ——
                // 不登记完成时间、不标 Unscheduled。
                continue;
            }

            // 块3/块4：登记本需求完成时间（所有批所有工序 Task 的最晚 End），供父件取动态物料下界。
            var demandCompletionTime = demandTasksAll.Max(t => t.PlannedEndTime);
            demandCompletion[demand.LogicalDemandKey] = demandCompletionTime;

            // P0-08：登记连续份额完成时间，供同 PI 自由份额做时间下界。
            if (actualDemand.IsContinuation && !string.IsNullOrEmpty(actualDemand.ProductionInstructionNo))
            {
                var pi = actualDemand.ProductionInstructionNo!;
                if (!continuityCompletionByPI.TryGetValue(pi, out var existing) || demandCompletionTime > existing)
                    continuityCompletionByPI[pi] = demandCompletionTime;
            }
        }

        // P0-04修复：把 Merge M:N 份额追溯返回给 Phase5，避免合批份额血缘丢失
        result.AllocationTaskShare = allocationTaskShare;

        return result;
    }

    /// <summary>
    /// 块4（任务喂任务，方案A）：计算某需求的「子件完成时间」动态物料下界。
    /// 取该需求所有直接子件（CrossMaterialParentToChildren）已排程完成时间的最大值。
    /// 若任一子件尚未成功排程（缺料），返回 childUnavailable=true，调用方应标记父件 Unscheduled。
    /// 无子件时返回 DateTime.MinValue（无动态下界，等价于旧行为）。
    /// </summary>
    private DateTime GetDynamicMaterialFloor(
        string demandKey,
        ConstraintContext constraints,
        Dictionary<string, DateTime> demandCompletion,
        out bool childUnavailable)
    {
        childUnavailable = false;

        if (!constraints.CrossMaterialParentToChildren.TryGetValue(demandKey, out var children)
            || children.Count == 0)
        {
            return DateTime.MinValue;
        }

        DateTime floor = DateTime.MinValue;
        foreach (var childKey in children)
        {
            if (!demandCompletion.TryGetValue(childKey, out var childEnd))
            {
                // 子件未成功排程 → 父件缺料，硬约束无法满足
                childUnavailable = true;
                continue;
            }

            // P1-12：父件消费相对子件完工的滞后时间（分钟），累加到子件完成时间上。
            if (constraints.CrossMaterialLagMinutes.TryGetValue((demandKey, childKey), out var lagMinutes))
            {
                childEnd = childEnd.AddMinutes((double)lagMinutes);
            }

            if (childEnd > floor)
            {
                floor = childEnd;
            }
        }

        return floor;
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
    /// 获取从指定阶段/工序开始的工序列表（拓扑排序）
    /// 文档：§四 4.4 RoutingDependency，§七 Level 0硬约束
    /// PI Position 执行起点上下文专项：支持 Operation 粒度裁剪（StartOperationCode）
    /// P0-01修复：改为 internal static，供 Phase4 TryResourceSwitch 复用，
    /// 使 Phase4 与 Phase2 使用同一套 Kahn 拓扑排序 + StartOperationCode/StartStageCode 裁剪逻辑。
    /// </summary>
    internal static List<OperationNode> GetOperationsFromStage(
        string startStageCode,
        string? startOperationCode,
        RoutingGraph routingGraph,
        ConstraintContext constraints,
        out string? failureReason)
    {
        failureReason = null;

        // 构建邻接表和入度表。节点身份 = (StageCode, OperationCode)（0号位 2026-09-29 裁决 §5.3）
        var adjacency = new Dictionary<OperationNodeKey, List<OperationNodeKey>>();
        var inDegree = new Dictionary<OperationNodeKey, int>();

        foreach (var nodeKey in routingGraph.Operations.Keys)
        {
            adjacency[nodeKey] = new List<OperationNodeKey>();
            inDegree[nodeKey] = 0;
        }

        // 根据 RoutingDependency 构建图。边的两端已是节点键（Phase1 已消解 Stage 归属），
        // 故不再有「按工序码对不上 ⇒ 静默漏接」的旧问题。
        foreach (var dep in routingGraph.Dependencies.Values.SelectMany(list => list))
        {
            if (adjacency.ContainsKey(dep.From) &&
                adjacency.ContainsKey(dep.To))
            {
                adjacency[dep.From].Add(dep.To);
                inDegree[dep.To]++;
            }
        }

        // Kahn 算法：拓扑排序
        var queue = new Queue<OperationNodeKey>();
        var result = new List<OperationNode>();

        // 将入度为0的工序加入队列
        foreach (var nodeKey in routingGraph.Operations.Keys)
        {
            if (inDegree[nodeKey] == 0)
            {
                queue.Enqueue(nodeKey);
            }
        }

        // BFS 拓扑排序
        while (queue.Count > 0)
        {
            var currentKey = queue.Dequeue();
            result.Add(routingGraph.Operations[currentKey]);

            // 减少后继工序的入度
            foreach (var successor in adjacency[currentKey])
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
            // failureReason 保持 null，调用方据此判定为技术失败
            return new List<OperationNode>();
        }

        // PI Position 执行起点上下文专项：Operation 粒度裁剪优先
        // S24/S26：从 StartOperationCode 继续，裁掉已完成前序；非法时失败，不静默回退
        if (!string.IsNullOrEmpty(startOperationCode))
        {
            // ⚠ 同码跨 Stage（2号位 实测 117 物料）：起点须用 StartStageCode 消解，
            //   旧实现按工序码单键命中 ⇒ 任取一个 Stage 的同名工序，起点可能取错。
            var candidates = routingGraph.Operations.Keys
                .Where(k => string.Equals(k.OperationCode, startOperationCode, StringComparison.Ordinal))
                .ToList();

            if (candidates.Count == 0)
            {
                // S26：StartOperationCode 不存在于 Routing，输入/求解失败
                failureReason = $"StartOperationCode '{startOperationCode}' 不存在于 Routing";
                return new List<OperationNode>();
            }

            OperationNodeKey startKey;

            if (candidates.Count == 1)
            {
                startKey = candidates[0];

                // S26：StartOperationCode 与 StartStageCode 明显不一致
                var resolvedStage = routingGraph.Operations[startKey].StageCode;
                if (!string.IsNullOrEmpty(startStageCode) &&
                    !string.IsNullOrEmpty(resolvedStage) &&
                    !string.Equals(resolvedStage, startStageCode, StringComparison.Ordinal))
                {
                    failureReason = $"StartOperationCode '{startOperationCode}' 的 StageCode '{resolvedStage}' 与 StartStageCode '{startStageCode}' 不一致";
                    return new List<OperationNode>();
                }
            }
            else
            {
                var matched = candidates
                    .Where(k => string.Equals(k.StageCode, startStageCode, StringComparison.Ordinal))
                    .ToList();

                if (matched.Count != 1)
                {
                    // 起点自身歧义：不猜（0号位 §5.3 禁半升级 + 不静默原则）
                    failureReason = $"StartOperationCode '{startOperationCode}' 在 Routing 中对应多个 Stage"
                        + $"（{string.Join("/", candidates.Select(c => c.StageCode))}），无法唯一确定起点";
                    return new List<OperationNode>();
                }

                startKey = matched[0];
            }

            // 从该 Operation 开始，找所有可达后续工序（含自己）
            return CropToReachable(result, new[] { startKey }, routingGraph);
        }

        // P0-02修复 + 第4轮修复：根据StartStageCode裁剪已完成的Stage
        // 第4轮修复：StartStage不存在时不返回整条Routing，而是返回空（数据不一致）
        if (!string.IsNullOrEmpty(startStageCode))
        {
            var startOperations = result.Where(op => op.StageCode == startStageCode).ToList();

            if (startOperations.Count == 0)
            {
                // StartStageCode 在 Routing 中不存在，业务 Unscheduled（非技术失败）
                failureReason = $"StartStageCode '{startStageCode}' 不存在于 Routing";
                return new List<OperationNode>();
            }

            // 从 StartStage 工序开始，找所有可达后续工序（含自己）
            return CropToReachable(
                result,
                startOperations.Select(op => OperationNodeKey.Of(op.StageCode, op.OperationCode)),
                routingGraph);
        }

        // 两者都空：返回完整 Routing（S23，从首工序开始）
        return result;
    }

    /// <summary>
    /// 从给定起点工序集合出发，裁剪拓扑序为"起点 + 所有可达后续工序"
    /// PI Position 执行起点上下文专项：Operation/Stage 粒度裁剪共用
    /// P0-01修复：改为 internal static，供 Phase4 TryResourceSwitch 复用。
    /// </summary>
    internal static List<OperationNode> CropToReachable(
        List<OperationNode> orderedOperations,
        IEnumerable<OperationNodeKey> startNodes,
        RoutingGraph routingGraph)
    {
        var reachableOps = new HashSet<OperationNodeKey>();
        var bfsQueue = new Queue<OperationNodeKey>();

        foreach (var startNode in startNodes)
        {
            if (reachableOps.Add(startNode))
            {
                bfsQueue.Enqueue(startNode);
            }
        }

        // BFS遍历：从Dependencies找每个工序的所有后续工序
        while (bfsQueue.Count > 0)
        {
            var currentOp = bfsQueue.Dequeue();

            // 遍历所有依赖边，找以currentOp为前驱的后续工序
            foreach (var kvp in routingGraph.Dependencies)
            {
                var toOp = kvp.Key;
                var edges = kvp.Value;

                // 如果存在从currentOp到toOp的边，且toOp未访问过
                if (edges.Any(e => e.From == currentOp) && !reachableOps.Contains(toOp))
                {
                    reachableOps.Add(toOp);
                    bfsQueue.Enqueue(toOp);
                }
            }
        }

        // 过滤：只保留可达的工序（节点身份须带 Stage 上下文，与图键同口径）
        return orderedOperations
            .Where(op => reachableOps.Contains(OperationNodeKey.Of(op.StageCode, op.OperationCode)))
            .ToList();
    }

    /// <summary>
    /// P1-02（StageOverlap）：取有效转运批量——优先路由 OperationNode.TransferBatchSize，缺省回落冻结 TransferBatchQty。
    /// </summary>
    private static decimal? GetEffectiveTransferBatch(OperationNode operation, StageOverlapParams stageOverlap)
    {
        if (operation.TransferBatchSize.HasValue && operation.TransferBatchSize.Value > 0)
            return operation.TransferBatchSize;
        if (stageOverlap.TransferBatchQty > 0)
            return stageOverlap.TransferBatchQty;
        return null;
    }

    /// <summary>
    /// P1-02（StageOverlap）：上游完成转运批量后下游即可提前开工；倒排时把上游结束时间向后延伸 overlapExtension。
    /// overlapExtension = 加工时长 × (1 - 转运批量/计划加工量)。AllowOverlap=false 或未配置批量时返回 Zero（严格串行）。
    /// </summary>
    private static TimeSpan GetOverlapExtension(OperationNode operation, decimal plannedProcessQty, TimeSpan processDuration, StageOverlapParams stageOverlap)
    {
        if (!stageOverlap.AllowOverlap)
            return TimeSpan.Zero;
        var transferBatch = GetEffectiveTransferBatch(operation, stageOverlap);
        if (transferBatch == null || transferBatch.Value <= 0 || transferBatch.Value >= plannedProcessQty)
            return TimeSpan.Zero;
        var ratio = transferBatch.Value / plannedProcessQty;
        return TimeSpan.FromMinutes(processDuration.TotalMinutes * (1 - (double)ratio));
    }

    /// <summary>
    /// 在**给定上下文**上排程一个需求的全部工序（Merge 开关分支）。
    /// 抽出本方法供 C桶候选内择优的**试排**与**落定**共用同一段逻辑，保证试排与落定结果一致。
    /// ⚠ 调用方负责传入隔离的 <paramref name="resourceOccupancy"/> / <paramref name="scheduledTasks"/> /
    ///   <paramref name="allocationTaskShare"/>（试排传克隆，落定传真实对象）。
    /// </summary>
    private List<FinalTaskDraft> RunDemandSchedule(
        LogicalProductionDemand demand,
        List<OperationNode> operations,
        RoutingGraph routingGraph,
        string direction,
        ConstraintContext constraints,
        Dictionary<int, List<TimeWindow>> resourceOccupancy,
        List<FinalTaskDraft> scheduledTasks,
        Dictionary<string, List<(string DemandKey, decimal ShareQty)>> allocationTaskShare,
        Dictionary<string, LogicalProductionDemand> demandByKey,
        DateTime planningStart,
        DateTime planningEnd,
        DateTime dynamicMaterialFloor,
        StageOverlapParams stageOverlap,
        bool allowMerge,
        string? batchDraftKey = null)   // P0-01/P0-02：本批归批键（null ⇒ 回落 ExecutionBatchKey(demandKey, 1)）
    {
        // 第4轮Merge修复：检测是否可以合并到已有Task
        if (allowMerge)
        {
            return TryMergeOrSchedule(
                demand, operations, routingGraph, direction, constraints,
                resourceOccupancy, scheduledTasks, allocationTaskShare, demandByKey,
                planningStart, planningEnd, dynamicMaterialFloor, stageOverlap, batchDraftKey);
        }

        return ScheduleDemandOperations(
            demand, operations, routingGraph, direction, constraints,
            resourceOccupancy, planningStart, planningEnd, dynamicMaterialFloor, stageOverlap, batchDraftKey);
    }

    /// <summary>
    /// C桶候选内择优（0号位 2026-10-07 裁决 Q-1/Q-2）——
    /// 按冻结**四层目标**逐层比较，**不新增** Routing 专属加权目标函数（Q-1 明文禁止）。
    ///
    /// 冻结依据：《APS_有限产能排产与滚动90天计划业务说明 v1.7》+《APS_V1_最终全部流程与业务基线 v1.8》——
    ///   ① 硬约束 → ② 履约优先 → ③ 客户承诺/交期目标 → ④ 次级优化（Setup / WIP / 利用率 / 稳定性）。
    ///   「如果存在能满足客户承诺目标的方案，不能为了 Setup 或利用率选择更差履约方案」。
    ///
    /// 比较顺序（可解释的目标顺序，非加权）：
    ///   1) 不可行候选淘汰（Q-1：「先过滤违反硬约束的候选」）；
    ///   2) ② 履约：按期（延期 = 0）优于延期；都延期 ⇒ 延期更短者优先；
    ///   3) ③ 交期：都按期时受该 Execution Batch 的 **Direction** 控制（Q-1 定死的表）——
    ///        FORWARD    → 更早可行完成优先；
    ///        BACKWARD   → 在不延期前提下更靠近 RequiredAvailableTime / Due（避免过早生产）；
    ///        MIXED/AUTO → 沿用现有求解结果比较（取更早完成，稳定）；
    ///   4) ④ 次级：Setup 总量小者优先；
    ///   5) 确定性 tiebreak：(RouteCode, PathId) 序。
    ///
    /// ⚠ PreferredResource 只进次级优化、**不得反压履约**（Q-1 明文）。V1 把它放在既有 Phase2 排程内部
    ///   消费（资源连续性软偏好），候选比较层不再额外加权 —— 否则即等于新增 Routing 专属加权目标。
    /// </summary>
    /// <returns>选中候选下标；全部不可行时返回 -1。</returns>
    private static int SelectBestRoutingCandidate(
        List<(RoutePathKey Key, bool Feasible, DateTime Completion, decimal SetupMinutes)> trials,
        LogicalProductionDemand demand,
        string direction,
        ConstraintContext constraints)
    {
        var due = constraints.EffectiveDue(demand);

        static int Compare(
            (RoutePathKey Key, bool Feasible, DateTime Completion, decimal SetupMinutes) a,
            (RoutePathKey Key, bool Feasible, DateTime Completion, decimal SetupMinutes) b,
            DateTime due,
            string direction)
        {
            // ① 可行优于不可行
            if (a.Feasible != b.Feasible)
            {
                return a.Feasible ? -1 : 1;
            }

            // ② 履约：延期短者优先（按期 = 0）
            var delayA = DelayMinutes(a.Completion, due);
            var delayB = DelayMinutes(b.Completion, due);
            if (delayA != delayB)
            {
                return delayA < delayB ? -1 : 1;
            }

            // ③ 交期：均按期时受 Direction 控制
            if (delayA == 0 && a.Completion != b.Completion)
            {
                if (string.Equals(direction, "BACKWARD", StringComparison.Ordinal))
                {
                    // 倒排：不延期前提下更靠近 Due（避免过早生产）⇒ 完成更晚者优先（两者均 ≤ Due）
                    return a.Completion > b.Completion ? -1 : 1;
                }

                // FORWARD / MIXED / AUTO：更早可行完成优先
                return a.Completion < b.Completion ? -1 : 1;
            }

            // ④ 次级：Setup 总量小者优先
            if (a.SetupMinutes != b.SetupMinutes)
            {
                return a.SetupMinutes < b.SetupMinutes ? -1 : 1;
            }

            // ⑤ 确定性 tiebreak：(RouteCode, PathId) 序
            var rc = string.CompareOrdinal(a.Key.RouteCode, b.Key.RouteCode);
            return rc != 0 ? rc : a.Key.PathId.CompareTo(b.Key.PathId);
        }

        var best = -1;
        for (int i = 0; i < trials.Count; i++)
        {
            if (!trials[i].Feasible)
            {
                continue;
            }

            if (best < 0 || Compare(trials[i], trials[best], due, direction) < 0)
            {
                best = i;
            }
        }

        return best;
    }

    /// <summary>延期分钟数（0 = 按期）。Merge 无新完成时间 / 无交期 ⇒ 视为按期。</summary>
    private static long DelayMinutes(DateTime completion, DateTime due)
    {
        if (completion == DateTime.MinValue || due == DateTime.MaxValue)
        {
            return 0;
        }

        var minutes = (long)(completion - due).TotalMinutes;
        return minutes > 0 ? minutes : 0;
    }

    /// <summary>资源占用深拷贝（候选试排隔离用）。</summary>
    private static Dictionary<int, List<TimeWindow>> CloneOccupancy(
        Dictionary<int, List<TimeWindow>> source)
        => source.ToDictionary(kv => kv.Key, kv => new List<TimeWindow>(kv.Value));

    /// <summary>合批份额深拷贝（候选试排隔离用）。</summary>
    private static Dictionary<string, List<(string DemandKey, decimal ShareQty)>> CloneShares(
        Dictionary<string, List<(string DemandKey, decimal ShareQty)>> source)
        => source.ToDictionary(kv => kv.Key, kv => new List<(string, decimal)>(kv.Value));

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
        DateTime planningEnd,
        DateTime dynamicMaterialFloor,
        StageOverlapParams stageOverlap,
        string? batchDraftKey = null)
    {
        var tasks = new List<FinalTaskDraft>();

        // 根据排程方向选择策略
        // 冻结口径：
        //   · 规则清单 v1.5 **B-004**：「Direction 支持 AUTO/FORWARD/BACKWARD/MIXED；OrderType 不得直接决定 Direction」
        //     ⇒ `AUTO` 是**正式冻结取值**，必须显式承载，不再由 `else` 隐式兜底。
        //   · 规则清单 v1.5 **B-005**：Direction 由 DemandGoal / RequiredAvailableTime / Slack / Material /
        //     Resource / Execution / Firm-Frozen-Lock 等上下文综合决定，Owner = 1号位。
        //   · 0号位 2026-10-07 裁决 **Q-1**：「MIXED/**AUTO** → 沿用现有 Mixed 结果」。
        // ⇒ 本实现内 `AUTO` 与 `MIXED` **行为等价**（先倒排、失败转正排），此处显式并列为同一分支；
        //   真正的「按上下文自决方向」（B-005 完整语义）尚未实现，属**未落码项**，不得据此认为已达标。
        if (direction == "BACKWARD")
        {
            tasks = ScheduleBackward(demand, operations, routingGraph, constraints, resourceOccupancy, planningStart, planningEnd, dynamicMaterialFloor, stageOverlap, batchDraftKey);
        }
        else if (direction == "FORWARD")
        {
            tasks = ScheduleForward(demand, operations, routingGraph, constraints, resourceOccupancy, planningStart, planningEnd, dynamicMaterialFloor, stageOverlap, batchDraftKey);
        }
        else if (direction is "MIXED" or "AUTO")
        {
            // MIXED：先尝试倒排，失败则转正排（§八 8.3 Mixed模式）。
            // AUTO：0号位 Q-1 裁定「沿用现有 Mixed 结果」⇒ 与 MIXED 同分支。
            tasks = ScheduleBackward(demand, operations, routingGraph, constraints, resourceOccupancy, planningStart, planningEnd, dynamicMaterialFloor, stageOverlap, batchDraftKey);
            if (tasks.Count == 0)
            {
                tasks = ScheduleForward(demand, operations, routingGraph, constraints, resourceOccupancy, planningStart, planningEnd, dynamicMaterialFloor, stageOverlap, batchDraftKey);
            }
        }
        else
        {
            // 未知 Direction：2号位 投影侧 `SolverStrategyModeMap.ToDirection` 已对未知枚举防御为 "BACKWARD"，
            // 故此处理论不可达；万一到达，保持历史行为（等效 MIXED），**不改变结果**。
            tasks = ScheduleBackward(demand, operations, routingGraph, constraints, resourceOccupancy, planningStart, planningEnd, dynamicMaterialFloor, stageOverlap, batchDraftKey);
            if (tasks.Count == 0)
            {
                tasks = ScheduleForward(demand, operations, routingGraph, constraints, resourceOccupancy, planningStart, planningEnd, dynamicMaterialFloor, stageOverlap, batchDraftKey);
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
        DateTime planningEnd,
        DateTime dynamicMaterialFloor,
        StageOverlapParams stageOverlap,
        string? batchDraftKey = null)
    {
        var tasks = new List<FinalTaskDraft>();
        var currentEndTime = constraints.EffectiveDue(demand);   // M5 第一批：倒排锚用覆盖交期

        // P0-05修复：获取物料最早可用时间，作为倒排的硬约束下界
        // 块4（任务喂任务）：合并子件完成时间，父件不能早于子件完成
        var materialEarliestTime = GetMaterialEarliestTime(
            demand.AllocationSequence,
            demand.NetOutputQty,
            constraints,
            planningStart,
            dynamicMaterialFloor,
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

            // OperationPlanningMode 分型（0号位 20260922）：非资源工序（UNCONSTRAINED/WAIT_ONLY）
            // 不占资源、保留工艺时间走链 —— 以 currentEndTime 为锚倒推产 ResourceId=NULL 的 Task，不判失败。
            if (IsNonResourceMode(operation.OperationPlanningMode))
            {
                var nonResourceTask = CreateNonResourceTask(demand, operation, currentEndTime, backward: true, batchDraftKey);
                tasks.Insert(0, nonResourceTask);   // 倒序插入
                // 更新倒排锚：本工序开始时间（含 LagTime）
                currentEndTime = nonResourceTask.PlannedStartTime;
                if (i > 0)
                {
                    var prevOperation = operations[i - 1];
                    var lagTime = GetLagTime(
                        OperationNodeKey.Of(prevOperation.StageCode, prevOperation.OperationCode),
                        OperationNodeKey.Of(operation.StageCode, operation.OperationCode),
                        routingGraph);
                    currentEndTime = currentEndTime.AddMinutes(-(double)lagTime);
                }
                continue;
            }

            // 查找合格资源（P1-11：软偏好资源优先，Preferred 最前、Fallback 次之）
            var eligibleResources = OrderResourcesByPreference(
                demand,
                GetEligibleResources(demand.MaterialId, operation, constraints),
                constraints,
                operation.OperationCode);   // P1-02：软偏好仅作用于当前承接工序
            if (eligibleResources.Count == 0)
            {
                return new List<FinalTaskDraft>(); // 无合格资源（FINITE_RESOURCE fail-closed）
            }

            // 尝试在合格资源上找时间槽
            FinalTaskDraft? scheduledTask = null;
            foreach (var resourceId in eligibleResources)
            {
                // P0-04修复：Duration = StandardDuration × PlannedProcessQty ÷ CapacityFactor
                // 第4轮Setup修复：加上SetupTime占用资源时间轴
                // 第5轮修复：CapacityFactor缺失或非法时不能继续
                var capacityFactor = GetCapacityFactor(demand.MaterialId, operation, resourceId, constraints);
                if (capacityFactor == null || capacityFactor <= 0)
                {
                    return new List<FinalTaskDraft>(); // CapacityFactor缺失/非法，无法计算Duration
                }
                var adjustedDuration = operation.StandardDuration * demand.PlannedProcessQty / capacityFactor.Value;
                var processDuration = TimeSpan.FromMinutes((double)adjustedDuration);

                // P1-02 item1 接线（阶段二）：倒排 Setup 同样走规则查找（v1.2 §2/§5），不再读 RoutingOperation.SetupTime。
                // Setup 影响 candidateStart 反推，有界迭代至解析收敛（≤4轮：以当前 Setup 搜槽 → 槽起点前产品重解析 → 变化则重搜）；
                // 未收敛/无槽 → 该资源不可行（与旧「找不到槽→下一资源」语义一致）。
                // P1-02（StageOverlap）：上游完成转运批量后下游可提前开工，倒排把上游结束时间后延 overlapExtension。
                var overlapExtension = GetOverlapExtension(operation, demand.PlannedProcessQty, processDuration, stageOverlap);

                decimal setupMinutes = 0m;
                TimeWindow? foundSlot = null;
                SetupOptimizer.SetupResolution? convergedResolution = null;   // SetupSource 填充：记录收敛解析结果
                for (int iter = 0; iter < 4; iter++)
                {
                    var totalDuration = processDuration + TimeSpan.FromMinutes((double)setupMinutes);

                    // 计算候选时间窗（包含Setup占用）
                    var candidateEnd = currentEndTime + overlapExtension;
                    var candidateStart = candidateEnd - totalDuration;

                    // P0-05修复：倒排Task不能早于物料可用时间；也不能早于计划期起点
                    if (candidateStart < materialEarliestTime || candidateStart < planningStart)
                    {
                        foundSlot = null;
                        break;
                    }

                    var slot = FindBackwardSlot(
                        candidateStart,
                        candidateEnd,
                        resourceId,
                        constraints,
                        resourceOccupancy,
                        planningStart);

                    if (!slot.HasValue)
                    {
                        foundSlot = null;
                        break;
                    }

                    var resolved = SetupOptimizer.ResolveSetupCore(
                        operation.OperationCode, resourceId,
                        constraints.ProductTimeline.GetPrevMaterial(resourceId, slot.Value.Start),
                        demand.MaterialId, constraints.SetupExactRules, constraints.SetupDefaultRules);

                    if (resolved.SetupMinutes == setupMinutes)
                    {
                        foundSlot = slot;   // 收敛：占用窗与规则解析值一致
                        convergedResolution = resolved;   // SetupSource 填充：收敛值即实际命中类型
                        break;
                    }
                    setupMinutes = resolved.SetupMinutes;   // 未收敛：以新 Setup 重搜
                }

                if (foundSlot.HasValue)
                {
                    // 找到可用槽 → 生成任务
                    // Task的PlannedStartTime是加工开始时间（不含Setup）
                    var setupDuration = TimeSpan.FromMinutes((double)setupMinutes);
                    var taskStart = foundSlot.Value.Start + setupDuration;
                    var taskSetupSource = convergedResolution.HasValue
                        ? SetupOptimizer.SetupOutcomeToSource(convergedResolution.Value.Outcome)
                        : null;
                    scheduledTask = CreateTask(demand, operation, resourceId, taskStart, foundSlot.Value.End, setupMinutes, constraints, taskSetupSource, batchDraftKey);

                    // 更新资源占用：从Setup开始到End结束
                    resourceOccupancy[resourceId].Add(new TimeWindow(foundSlot.Value.Start, foundSlot.Value.End));
                    constraints.ProductTimeline.Place(resourceId, foundSlot.Value.End, demand.MaterialId);

                    // P0-17修复：应用Routing LagTime到前序工序的结束时间约束
                    // currentEndTime应该是加工开始时间（Setup之前）
                    currentEndTime = foundSlot.Value.Start;
                    if (i > 0)
                    {
                        var prevOperation = operations[i - 1];
                        var lagTime = GetLagTime(
                            OperationNodeKey.Of(prevOperation.StageCode, prevOperation.OperationCode),
                            OperationNodeKey.Of(operation.StageCode, operation.OperationCode),
                            routingGraph);
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
    /// 第4轮C2修复：按真实DAG依赖执行，不串行化并行分支
    /// </summary>
    private List<FinalTaskDraft> ScheduleForward(
        LogicalProductionDemand demand,
        List<OperationNode> operations,
        RoutingGraph routingGraph,
        ConstraintContext constraints,
        Dictionary<int, List<TimeWindow>> resourceOccupancy,
        DateTime planningStart,
        DateTime planningEnd,
        DateTime dynamicMaterialFloor,
        StageOverlapParams stageOverlap,
        string? batchDraftKey = null)
    {
        var tasks = new List<FinalTaskDraft>();

        // 获取物料最早可用时间
        // P0-05修复：传入所需数量，根据累计可用量确定启动时间，并验证总量是否足够
        // 块4（任务喂任务）：合并子件完成时间，父件不能早于子件完成
        var materialEarliestStart = GetMaterialEarliestTime(
            demand.AllocationSequence,
            demand.NetOutputQty,
            constraints,
            planningStart,
            dynamicMaterialFloor,
            out bool isMaterialSufficient);

        // P0-05修复：物料总量不足时，标记为业务Unscheduled（不是技术失败）
        if (!isMaterialSufficient)
        {
            return new List<FinalTaskDraft>(); // 物料总量不足
        }

        // 第4轮C2修复：记录每个Operation的实际完成时间，用于DAG依赖计算。
        // 0号位 2026-09-29 裁决 §5.3：键升维为 (StageCode, OperationCode) —— 同码跨 Stage 时
        // 旧单键会让两个 Stage 的同名工序**互相覆盖**（完成时间串台）。
        var operationEndTimes = new Dictionary<OperationNodeKey, DateTime>();

        // 第4轮P7修复：记录每个Operation的阈值启动时间（完成TransferBatchSize数量的时间）
        // 用于Stage overlap：下游工序可在上游达到TransferBatchSize后启动，无需等待全部完成
        var operationThresholdTimes = new Dictionary<OperationNodeKey, DateTime>();

        // 从第一道工序往后推
        for (int i = 0; i < operations.Count; i++)
        {
            var operation = operations[i];
            var operationKey = OperationNodeKey.Of(operation.StageCode, operation.OperationCode);

            // 第4轮C2修复：计算当前Operation的真实最早开始时间
            // 1. 如果是根工序（无前驱），从物料可用时间开始
            // 2. 如果有前驱，从所有前驱的（结束时间+Lag）中取最大值
            DateTime earliestStart = materialEarliestStart;

            if (routingGraph.Dependencies.TryGetValue(operationKey, out var predecessorEdges))
            {
                // 有前驱：遍历所有前驱边，计算最晚的（前驱结束时间+Lag）
                // 第4轮P7修复：如果前驱配置了TransferBatchSize，使用阈值时间而非完成时间
                foreach (var edge in predecessorEdges)
                {
                    if (operationEndTimes.TryGetValue(edge.From, out var predecessorEnd))
                    {
                        // 检查前驱工序是否配置了TransferBatchSize（P1-02：受 AllowOverlap 门控 + 冻结 TransferBatchQty 回落）
                        DateTime effectiveTime = predecessorEnd;
                        if (stageOverlap.AllowOverlap
                            && routingGraph.Operations.TryGetValue(edge.From, out var predecessorOp)
                            && GetEffectiveTransferBatch(predecessorOp, stageOverlap).HasValue
                            && operationThresholdTimes.TryGetValue(edge.From, out var thresholdTime))
                        {
                            // 有阈值配置且已计算阈值时间，使用阈值时间
                            effectiveTime = thresholdTime;
                        }

                        var candidateStart = effectiveTime.AddMinutes((double)edge.LagTime);
                        if (candidateStart > earliestStart)
                        {
                            earliestStart = candidateStart;
                        }
                    }
                }
            }

            // OperationPlanningMode 分型（0号位 20260922）：非资源工序（UNCONSTRAINED/WAIT_ONLY）
            // 不占资源、保留工艺时间走链 —— 产 ResourceId=NULL 的 Task，不判失败（FINITE_RESOURCE 才 fail-closed）。
            if (IsNonResourceMode(operation.OperationPlanningMode))
            {
                var nonResourceTask = CreateNonResourceTask(demand, operation, earliestStart, backward: false, batchDraftKey);
                tasks.Add(nonResourceTask);
                operationEndTimes[operationKey] = nonResourceTask.PlannedEndTime;
                continue;
            }

            // 查找合格资源（P1-11：软偏好资源优先，Preferred 最前、Fallback 次之）
            var eligibleResources = OrderResourcesByPreference(
                demand,
                GetEligibleResources(demand.MaterialId, operation, constraints),
                constraints,
                operation.OperationCode);   // P1-02：软偏好仅作用于当前承接工序
            if (eligibleResources.Count == 0)
            {
                return new List<FinalTaskDraft>(); // 无合格资源（FINITE_RESOURCE fail-closed）
            }

            // 尝试在合格资源上找时间槽
            FinalTaskDraft? scheduledTask = null;
            foreach (var resourceId in eligibleResources)
            {
                // P0-04修复：Duration = StandardDuration × PlannedProcessQty ÷ CapacityFactor
                // 第4轮Setup修复：加上SetupTime占用资源时间轴
                // 第5轮修复：CapacityFactor缺失或非法时不能继续
                var capacityFactor = GetCapacityFactor(demand.MaterialId, operation, resourceId, constraints);
                if (capacityFactor == null || capacityFactor <= 0)
                {
                    return new List<FinalTaskDraft>(); // CapacityFactor缺失/非法，无法计算Duration
                }
                var adjustedDuration = operation.StandardDuration * demand.PlannedProcessQty / capacityFactor.Value;
                var processDuration = TimeSpan.FromMinutes((double)adjustedDuration);

                // P1-02 item1 接线（阶段二）：Setup = 规则查找「当前工序+当前设备+前产品→当前产品」（v1.2 §2/§5），
                // RoutingOperation.SetupTime 不再读取（§1.2/§20.3 废止运行真相）；§12：Setup 从候选槽评价阶段参与，
                // 动态 Setup 与槽位置的相互依赖由 FindSlotWithDynamicSetup 有界迭代解决。
                var found = SetupOptimizer.FindSlotWithDynamicSetup(
                    earliestStart, processDuration, resourceId, operation.OperationCode, demand.MaterialId,
                    constraints.ProductTimeline, constraints.SetupExactRules, constraints.SetupDefaultRules,
                    (f, total) => FindForwardSlot(f, total, resourceId, constraints, resourceOccupancy, planningEnd));

                if (found.HasValue)
                {
                    var (occSlot, setupMinutes, setupResolution) = found.Value;
                    // B.2：Setup 追踪三元组写出（0号位 20260917 回复 §5.1 / §14.3）。
                    // 载体 = 2号位 r13494 落地的 DomainSolveResult.SolveTraceNote；此处只收集，Phase5 统一导出。
                    // 判据：仅写「需说明」的解析结果（ExplanationType 非空 = INITIAL_SETUP_STATE / SETUP_RULE_MISSING_ZERO_FALLBACK /
                    //       DEFAULT_SETUP_FALLBACK）；正常命中（EXACT/DEFAULT/SameProduct）ExplanationType 为 null → 不产 trace，
                    //       避免 10 万 Task 级 trace 体积（与实施包 §19 同源约束）。
                    // ⚠ ReasonCode 位承载的是 ExplanationType（trace 层），**不是**冻结字典 ScheduleExplanationFact.ReasonCode
                    //   （0号位 Q4：决策说明不进 ReasonCode 体系）—— 命名撞域已提请 2号位 改名，未落前在此显式标注。
                    // ⚠ Level 值域归一：SetupOptimizer 填 "INFO"/"WARNING"（全大写），SolveTraceNote.Level 契约为 "Info"/"Warning"/"Error"。
                    // ⚠⚠ 落码前置偏差（2026-09-28 复核，1号位 自记）：1号位 2026-09-24《白天候选配合事项 回执 v1.1》
                    //   §2.3/§2.4 已向 2号位 提请 3 项（`ReasonCode` 是否改名 / `Level` 取值域 / `Key` 语义）并声明
                    //   「三小项一并明确后…同批落两处产出点」。**2号位 尚未答复**，本处属先行落码。
                    //   影响面可控：三项均为字段级 —— 若答复为 §2.3(i) 改名，仅需改 `ReasonCode =` 一行；
                    //   若答复 Level 与 Severity 同域，仅需改 `NormalizeTraceLevel` 映射表；若 Key 语义改为「产出点标识」，仅需改 `Key =`。
                    //   方向（第二层「决策说明」，不进冻结 ReasonCode 字典）已由 0号位 Q4 + 该回执 §2.2 确认，无结构性返工。
                    if (!string.IsNullOrEmpty(setupResolution.ExplanationType))
                    {
                        constraints.TraceNotes.Add(new SolveTraceNote
                        {
                            Key = demand.LogicalDemandKey,
                            ReasonCode = setupResolution.ExplanationType,
                            Message = setupResolution.TraceMessage,
                            Level = NormalizeTraceLevel(setupResolution.TraceLevel)
                        });
                    }
                    var setupDuration = TimeSpan.FromMinutes((double)setupMinutes);

                    // Task的PlannedStartTime是加工开始时间（Setup之后）
                    var taskStart = occSlot.Start + setupDuration;
                    // SetupSource 填充：解析命中类型 → 大写 5 值（5号位 值契约统一 20260921）
                    var taskSetupSource = SetupOptimizer.SetupOutcomeToSource(setupResolution.Outcome);
                    scheduledTask = CreateTask(demand, operation, resourceId, taskStart, occSlot.End, setupMinutes, constraints, taskSetupSource, batchDraftKey);

                    // 资源占用从Setup开始
                    resourceOccupancy[resourceId].Add(occSlot);
                    constraints.ProductTimeline.Place(resourceId, occSlot.End, demand.MaterialId);

                    // 第4轮C2修复：记录该Operation的实际完成时间，供后续工序使用
                    operationEndTimes[operationKey] = occSlot.End;

                    // 第4轮P7修复：如果配置了TransferBatchSize，计算阈值启动时间（P1-02：AllowOverlap 门控 + 冻结 TransferBatchQty 回落）
                    var transferBatch = GetEffectiveTransferBatch(operation, stageOverlap);
                    if (stageOverlap.AllowOverlap && transferBatch.HasValue && transferBatch.Value > 0 && transferBatch.Value < demand.PlannedProcessQty)
                    {
                        // 阈值时间 = 占用开始 + Setup时间 + (转运批量 / PlannedProcessQty) × 加工时长
                        var thresholdRatio = transferBatch.Value / demand.PlannedProcessQty;
                        var thresholdDuration = setupDuration + TimeSpan.FromMinutes((double)(processDuration.TotalMinutes * (double)thresholdRatio));
                        operationThresholdTimes[operationKey] = occSlot.Start + thresholdDuration;
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
    /// 块4（任务喂任务，方案A）：合并子件完成时间（dynamicMaterialFloor），
    /// 父件的物料最早可用时间 = max(静态到货时间, 子件真实完成时间)。
    /// </summary>
    private DateTime GetMaterialEarliestTime(
        long allocationSequence,
        decimal requiredQuantity,
        ConstraintContext constraints,
        DateTime planningStart,
        DateTime dynamicMaterialFloor,
        out bool isSufficient)
    {
        isSufficient = true;

        DateTime staticEarliest;

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
                    staticEarliest = segment.AvailableTime;
                    return staticEarliest > dynamicMaterialFloor ? staticEarliest : dynamicMaterialFloor;
                }
            }

            // P0-05修复：所有段累计仍不足需求量，标记不足并返回最后一段时间
            isSufficient = false;
            staticEarliest = segments.Max(s => s.AvailableTime);
            return staticEarliest > dynamicMaterialFloor ? staticEarliest : dynamicMaterialFloor;
        }

        // 无静态物料约束：直接用动态下界（无子件时 dynamicMaterialFloor 为 MinValue，等价于 planningStart）
        staticEarliest = planningStart;
        return staticEarliest > dynamicMaterialFloor ? staticEarliest : dynamicMaterialFloor;
    }

    /// <summary>
    /// 获取工序的合格资源列表（按优先级排序）
    /// </summary>
    private List<int> GetEligibleResources(
        int materialId,
        OperationNode operation,
        ConstraintContext constraints)
    {
        // 第4轮C1修复：索引加入MaterialId
        // 0号位 2026-09-29 裁决 §5.3 落实：键升为强类型 EligibilityLookupKey，**补上 ProductionDepartmentId**
        // （旧键写死 "DEFAULT" 且无部门 ⇒ 两个部门的同名工序资格被合并 ⇒ 跨部门串资源）。
        // ⚠ 契约 OperationResourceEligibility **无 StageCode 字段**，无法再细到 Stage（残留见键类型注释）。
        var key = new EligibilityLookupKey(
            materialId, operation.ProductionDepartmentId, operation.RouteCode, operation.PathId, operation.OperationCode);

        if (constraints.OperationResourceEligibility.TryGetValue(key, out var resources))
        {
            return resources;
        }
        return new List<int>();
    }

    /// <summary>
    /// P1-11：软偏好资源优先——在合法资源集内把偏好资源排最前、FallbackResourceId 次之，
    /// 其余保持原顺序。软偏好不改变合法性（非硬锁），偏好资源不可用时自然回落。
    ///
    /// v1.6 §1号位新增/替换实施要求（`:27`）：新增契约字段 `PreferredResourceCode?`（业务编码）为**软偏好**。
    /// 消费口径（0号位 2026-10-07 裁决 §六 授权落地）：
    ///   · Code 非空且经 <c>ResourceIdsByCode</c> 反查到 ResourceId ⇒ 以该 ResourceId 为软偏好首选；
    ///   · Code 为空 / 反查不到 / 反查到的 ResourceId 不在合法资源集内 ⇒ **回落** `PreferredResourceId`；
    ///   · **不得 Hard Lock**（Q-1 明文）：偏好资源不可用时不失败，按原顺序继续找槽。
    ///
    /// **P1-02（0号位 2026-10-07 (5).md）作用域收窄**：`PreferredResourceCode` 的语义是
    ///   「上一 ACTIVE Task 的资源」= **当前承接工序的连续性偏好**，不是「整条 Route 的设备偏好」。
    ///   原实现把它对每个 Operation 都调用，等于把连续性偏好扩散到后续全部工序。
    ///   现收窄为：**仅作用于 Continuation Slice 的 StartOperation（当前承接工序）**；
    ///   `currentOperationCode` 与 `demand.StartOperationCode` 不一致时**不施加任何偏好**。
    ///   （非连续份额 / 未提供 currentOperationCode ⇒ 维持原行为，零回归。）
    ///
    /// **P1-01**：本函数为 Phase2/Phase4 **统一纯函数**，Phase4 不再自带只认 Id 的旧版
    ///   （旧版完全不看 `PreferredResourceCode`，Phase4 局部修复会丢弃 Code 偏好）。
    /// </summary>
    internal static List<int> OrderResourcesByPreference(
        LogicalProductionDemand demand,
        List<int> eligibleResources,
        ConstraintContext constraints,
        string? currentOperationCode = null)
    {
        // P1-02：连续份额的软偏好只对「当前承接工序」生效，不得扩散到后续整条 Route。
        if (demand.IsContinuation
            && currentOperationCode is not null
            && !string.Equals(currentOperationCode, demand.StartOperationCode, StringComparison.Ordinal))
        {
            return eligibleResources;
        }

        // Code 优先，反查不到则回落 Id（Code 为空串不算合法业务编码，与 RouteCode 同口径）
        int? preferred = null;
        if (!string.IsNullOrEmpty(demand.PreferredResourceCode) &&
            constraints.ResourceIdsByCode.TryGetValue(demand.PreferredResourceCode!, out var byCode))
        {
            preferred = byCode;
        }
        preferred ??= demand.PreferredResourceId;

        if (preferred == null && demand.FallbackResourceId == null)
        {
            return eligibleResources;
        }

        var ordered = new List<int>(eligibleResources.Count);

        if (preferred is int preferredId && eligibleResources.Contains(preferredId))
        {
            ordered.Add(preferredId);
        }

        if (demand.FallbackResourceId is int fallback
            && fallback != preferred
            && eligibleResources.Contains(fallback))
        {
            ordered.Add(fallback);
        }

        foreach (var resourceId in eligibleResources)
        {
            if (!ordered.Contains(resourceId))
            {
                ordered.Add(resourceId);
            }
        }

        return ordered;
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
        // 0号位裁决（2026-09-12）：无末期限制 —— PlanningEnd 不是硬上界，
        // 正排不再被 planningEnd 截断，只受资源日历窗口（calWindow.End）约束。
        // planningEnd 参数保留仅为签名一致，不再作为末期硬边界。
        foreach (var calWindow in calendar.OrderBy(w => w.Start))
        {
            if (calWindow.End <= earliestStart) continue;

            var windowStart = calWindow.Start > earliestStart ? calWindow.Start : earliestStart;
            var windowEnd = calWindow.End;

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
    /// 在窗口内找第一个空闲槽（按资源占用窗线性扫描）。
    ///
    /// 性能口径（原「TODO P15：性能优化（§十九）」已闭合 —— 2026-09-24 改写，勿再按原技术清单重写本方法）：
    ///   端到端目标「10万 Task / 15 分钟」**经实测达成**，故原 TODO 所列五条（Interval Timeline /
    ///   资源时间轴内存索引 / 避免 O(N²) 全 Task 扫描 / Setup 只局部更新 / Candidate 只传播实际变化）
    ///   不再是待办项。
    ///   实测（2026-09-20 性能标定；FORWARD/FULL；200 资源 × 500 Task/资源 = 10万 Task；物料池 500；
    ///   EXACT 规则 5% + DEFAULT 30%；单窗口 90 天日历；seeded 可重放）：
    ///     P2 默认预算(500/50) = 173s；P3 上限(5000/150) = 220s；红线 900s → 余量 ≥ 75%；
    ///     全部 Success 且 0 未排。
    ///   报告：《APS_V1_Setup换型_性能标定报告与预算参数修订提值_1号位致3号位_v1.0_20260920.md》。
    ///   已落地的性能加固：Phase5 CompactGaps / OptimizeSegment 的段内 O(n²) 全表扫描改为 TaskIndex
    ///   三重索引（BySource / ByResource / ContinuationByPi + FreeByPi，FinalDraftId→PI 反查 O(1)）。
    ///   复核触发条件：真实日历接入 / 规模 &gt; 15万 Task / 规则密度 &gt; 30% / 性能红线调整。
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
    /// <summary>
    /// OperationPlanningMode 非资源工序判定（UNCONSTRAINED / WAIT_ONLY）：
    /// 不占资源、保留工艺时间走链（0号位 20260922 无设备小工序裁决）。
    /// </summary>
    /// <summary>
    /// B.2：TraceLevel 值域归一。
    /// 上游 <see cref="SetupOptimizer.SetupResolution.TraceLevel"/> 按 0号位 §5.1/§5.3 填「全大写」字面量（"INFO"/"WARNING"），
    /// 而 2号位 r13494 的 <see cref="SolveTraceNote.Level"/> 契约值域为「首字母大写」（Info/Warning/Error）。
    /// 不归一即会写出两套值 → 4号位 数据质量查询/2号位 落库按值过滤时漏数。此处为唯一转换点。
    /// 未知值一律回落契约默认 "Info"（Level 为不可空 string，禁止写 null）。
    /// </summary>
    private static string NormalizeTraceLevel(string? level)
        => level switch
        {
            null => "Info",
            "INFO" => "Info",
            "WARNING" => "Warning",
            "ERROR" => "Error",
            _ => level   // 已是契约值域（Info/Warning/Error）或其他自定义值 → 原样透传，不吞信息
        };

    private static bool IsNonResourceMode(string? mode)
        => string.Equals(mode, "UNCONSTRAINED", StringComparison.Ordinal)
        || string.Equals(mode, "WAIT_ONLY", StringComparison.Ordinal);

    /// <summary>
    /// 非资源工序 Task（0号位 20260922 裁决：非资源工序也产出 Task，ResourceId=NULL，不占资源时间轴）。
    /// 时长 = StandardDuration × PlannedProcessQty（1号位 自定：与 FINITE 同源但不除 CapacityFactor —— 非资源无产能系数）。
    /// backward=true 以 anchorTime 为结束倒推；false 以 anchorTime 为开始正推。
    /// </summary>
    private FinalTaskDraft CreateNonResourceTask(
        LogicalProductionDemand demand,
        OperationNode operation,
        DateTime anchorTime,
        bool backward,
        string? batchDraftKey = null)   // P0-01/P0-02：本批归批键（null ⇒ 回落 ExecutionBatchKey(demandKey, 1)）
    {
        var durationMinutes = operation.StandardDuration * demand.PlannedProcessQty;
        var duration = TimeSpan.FromMinutes((double)durationMinutes);
        var start = backward ? anchorTime - duration : anchorTime;
        var end = backward ? anchorTime : anchorTime + duration;

        return new FinalTaskDraft
        {
            FinalDraftId = Guid.NewGuid().ToString(),
            SourceDraftId = demand.LogicalDemandKey,
            MaterialId = demand.MaterialId,
            FactoryId = demand.FactoryId,
            StageCode = operation.StageCode ?? string.Empty,
            OperationCode = operation.OperationCode,
            TaskType = "PRODUCTION",
            ResourceId = null,             // 非资源工序：不占资源（0号位 20260922）
            ResourceCode = string.Empty,
            RouteCode = operation.RouteCode,
            PathId = operation.PathId,
            Quantity = demand.NetOutputQty,
            PlannedProcessQty = demand.PlannedProcessQty,
            UOM = demand.UOM ?? string.Empty,
            PlannedStartTime = start,
            PlannedEndTime = end,
            SetupTime = 0m,                // 非资源无换型
            SetupSource = null,            // 非资源无 Setup 来源
            Priority = demand.DemandSequence,
            IsVirtual = false,
            // 非资源工序 Task 同样属该执行批 ⇒ 与资源 Task 共 Key（v1.6：FinalTask 一律回传）。
            // P0-02：键域为 (需求键, 批序号)；批键由本批入参给定，缺省即 1 号批。
            ContinuationKey = demand.ContinuationKey,
            ExecutionBatchDraftKey = batchDraftKey ?? ExecutionBatchKey(demand.LogicalDemandKey)
        };
    }

    /// <summary>
    /// 生成执行批键（P0-02，0号位 2026-10-07《未命名的Markdown文件 (5).md》）。
    ///
    /// 【为什么改键域】旧键 <c>EB|{demand}|{route}|{path}</c> 由 **Route/Path 派生** ⇒
    ///   ① 批身份被路由选择**反向决定**（0号位 判词：「把 LogicalDemand 当成了 Execution Batch」）；
    ///   ② 同一 Demand 拆出的 N 个批**会撞成同一个键**（同 Route / 同 Path）⇒
    ///      「同 Batch 共键、不同 Batch 必不同键」**结构性失效**。
    /// 新键只由 **(逻辑需求键, 批序号)** 决定 ⇒ 批身份是**主键**，Route/Path 是批的**属性**
    ///   （每批各自选一条完整 Path，见 P0-03），构造上保证「不同 Batch 即使同 Route/Path 也必不同键」。
    ///
    /// ⚠ 批序号由 `FormExecutionBatches` 产出（1 起、连续、确定性），**不由候选 Path 条数反推**
    ///   （0号位 裁决 §四）。
    /// </summary>
    public static string ExecutionBatchKey(string logicalDemandKey, int batchOrdinal = 1)
        => $"EB|{logicalDemandKey}|{batchOrdinal:D3}";

    /// <summary>
    /// P0-02：Phase4 重建 Task（`TryResourceSwitch` / `TrySplitOperation`）的归批键解析。
    ///
    /// 语义：重建的 Task 必然是**某个既有执行批的工序重建**，其批键必须与 Phase2 为该批发出的键
    /// **逐字一致**（否则同一批被劈成两个键，违反「同批共键」）。
    ///
    /// 解析顺序：
    ///   ① Phase2 已登记该需求的批键（`ConstraintContext.ExecutionBatchDraftKeys`）⇒ **复用**（生产现状恒此，且只有 1 个）；
    ///   ② 未登记（Phase4 先于 Phase2 落定 / 需求未进入 Phase2 主循环）⇒ 回落 `ExecutionBatchKey(demandKey)`（1 号批）。
    ///
    /// ⚠ **待办 EBD-01（未销账，不静默）**：`keys.Count &gt; 1` 时本处取**首批键** —— 这**仅当 N=1 时正确**。
    ///   Phase4 的整链重建（`TryResourceSwitch` 逐工序重建整条工艺）目前**无「批序号」入参**，
    ///   多批情形下无法唯一归属。因 ⑧块 Batch Policy 载体未达 1号位（见 EBD-02），生产路径恒 N=1，
    ///   该分支当前**不可达**；**EBD-02 一旦销账（载体接入），必须同步给 Phase4 补批序号入参**。
    /// </summary>
    public static string ResolveExecutionBatchKeyForRebuild(string logicalDemandKey, ConstraintContext constraints)
    {
        if (constraints.ExecutionBatchDraftKeys.TryGetValue(logicalDemandKey, out var keys) && keys.Count > 0)
        {
            // keys.Count > 1 见上方 EBD-01。
            return keys[0];
        }

        return ExecutionBatchKey(logicalDemandKey);
    }

    /// <summary>
    /// 一个执行批（Execution Batch）的排程草稿（P0-01 / P0-02，0号位 2026-10-07 (5).md）。
    ///
    /// 冻结单位：**执行批是 Routing 择优的业务单位**（「每个 Execution Batch 只允许一条完整 Path」）。
    /// 因此 `BatchDraftKey` 只依赖 (逻辑需求键, 批序号)，与 Route/Path **无关**。
    /// </summary>
    public sealed record ExecutionBatchDraft(
        string BatchDraftKey,            // EB|{逻辑需求键}|{批序号:D3}
        string SourceLogicalDemandKey,
        int Ordinal,                     // 1 起、连续
        decimal NetOutputQty,            // 本批净产出（各批求和 = 需求 NetOutputQty，逐分不丢）
        decimal PlannedProcessQty);      // 本批计划加工量（各批求和 = 需求 PlannedProcessQty）

    /// <summary>
    /// ⑧块 Batch Policy 在 **1号位 侧**的执行批策略输入（P0-01）。
    ///
    /// ⚠ 契约来源：字段口径取自 ⑧块 `BatchPolicyRuleSnapshot`（`FrozenStrategySnapshot.cs`），
    ///   但该类属 **2↔3** 层，**未投影进 1↔2 的 `SolverStrategySnapshot`**（无 BatchPolicy 成员）。
    ///   本类型是 1号位 侧的**消费侧形参**，不是新契约：生产装配处当前恒 null（见 EBD-02），
    ///   由单测直接注入以锁住多批行为。
    /// </summary>
    public sealed record ExecutionBatchPolicyInput(
        decimal MinExecutionBatchQty = 0m,          // ⑧块 MinExecutionBatchQty（硬最小批量）
        decimal? MaxExecutionBatchQty = null,       // ⑧块 MaxExecutionBatchQty（硬最大批量；null = 无硬上限）
        decimal? PreferredBatchQty = null,          // ⑧块 PreferredBatchQty（软偏好切点；V1 未消费，登记不实现）
        bool AllowSplit = false,                    // ⑧块 AllowSplit
        bool AllowMerge = false,                    // ⑧块 AllowMerge
        int? MaxOptimizationSplitCount = null,      // ⑧块 MaxOptimizationSplitCount（仅限制优化性拆分搜索）
        int? MaxBatchCandidates = null);            // ⑧块 MaxBatchCandidates（V1 未消费，登记不实现）

    /// <summary>
    /// 执行批形成（P0-01）—— 0号位 指定链路的第一环：`Free Slice → Batch Policy → 1..N ExecutionBatchDraft`。
    ///
    /// 硬约束（任何策略都不得推翻）：
    ///   · A/B（`IsContinuation` / `NoSplitMerge`）⇒ **恒 1 批**（v1.6 NoSplitMerge；Phase4 P0_07 同向）。
    ///   · 缺策略（`policy == null`）⇒ **恒 1 批**（缺 ⑧块不拆）。
    ///
    /// V1 批数裁决（`DecideExecutionBatchCount`）：
    ///   · **硬最大批量**（`MaxExecutionBatchQty`）⇒ **强制拆到每批不超过它**（`SplitParams.MaxOptimizationSplitCount`
    ///     注释明文：「仅限制优化性拆分搜索（**不限制硬 Max 强制拆分**）」⇒ 硬 Max 不受 AllowSplit 限制）。
    ///   · **优化性拆批**（`AllowSplit` + `MaxOptimizationSplitCount` / `PreferredBatchQty`）⇒ **V1 不做搜索**：
    ///     多拆一批的收益须由「跨批 Routing/资源/日历联合求解」的目标值比较得出，
    ///     而该比较的输入（⑧块搜索预算/切点语义）本轮无载体 ⇒ **登记不实现**（待办 EBD-03），
    ///     **不静默降级为「拆得越多越好」**（那会把 Setup 与在制推高，违反四层目标第④层）。
    ///   · **硬最小批量**（`MinExecutionBatchQty`）⇒ **V1 无作用面**（有硬 Max 时批数已是最小可行批数；
    ///     无硬 Max 时恒 1 批）⇒ 不落代码，登记待办 **EBD-03**（详见 `DecideExecutionBatchCount` 内注释）。
    ///
    /// 数量切分：各批**逐分不丢**（前 N-1 批向下取整到 4 位小数，末批取余数）——
    ///   保证 `Σ NetOutputQty = 需求 NetOutputQty`、`Σ PlannedProcessQty = 需求 PlannedProcessQty`。
    /// </summary>
    public static List<ExecutionBatchDraft> FormExecutionBatches(
        LogicalProductionDemand demand,
        ExecutionBatchPolicyInput? policy)
    {
        int count = DecideExecutionBatchCount(demand, policy);

        var batches = new List<ExecutionBatchDraft>(count);
        decimal netAssigned = 0m;
        decimal procAssigned = 0m;

        for (int ordinal = 1; ordinal <= count; ordinal++)
        {
            bool last = ordinal == count;

            decimal batchNet = last
                ? demand.NetOutputQty - netAssigned
                : decimal.Round(demand.NetOutputQty / count, 4, MidpointRounding.ToZero);
            decimal batchProc = last
                ? demand.PlannedProcessQty - procAssigned
                : decimal.Round(demand.PlannedProcessQty / count, 4, MidpointRounding.ToZero);

            netAssigned += batchNet;
            procAssigned += batchProc;

            batches.Add(new ExecutionBatchDraft(
                ExecutionBatchKey(demand.LogicalDemandKey, ordinal),
                demand.LogicalDemandKey,
                ordinal,
                batchNet,
                batchProc));
        }

        return batches;
    }

    /// <summary>
    /// V1 批数裁决（判据来源与边界见 <see cref="FormExecutionBatches"/> 的文档块）。
    /// </summary>
    private static int DecideExecutionBatchCount(LogicalProductionDemand demand, ExecutionBatchPolicyInput? policy)
    {
        // ① A/B（连续份额）硬约束：不拆批。
        if (demand.IsContinuation || demand.NoSplitMerge)
        {
            return 1;
        }

        // ② 缺 ⑧块策略：不拆（不认领 BATCH_POLICY_MISSING —— 与 0号位 20260928 §十五 四级兜底链冲突，属待裁项）。
        if (policy is null || demand.NetOutputQty <= 0m)
        {
            return 1;
        }

        int count = 1;

        // ③ 硬最大批量：强制拆（不受 AllowSplit / MaxOptimizationSplitCount 限制）。
        if (policy.MaxExecutionBatchQty is > 0m)
        {
            count = (int)Math.Ceiling(demand.NetOutputQty / policy.MaxExecutionBatchQty.Value);
        }

        // ④ 硬最小批量（MinExecutionBatchQty）：**V1 无作用面，故不落代码**（登记 EBD-03，不静默）——
        //    · 有硬 Max 时：count = ceil(qty / Max) 已是「满足硬 Max 的最小批数」，
        //      再减一批必使每批 > Max ⇒ 违反硬 Max（容量型硬约束优先）⇒ 收不下去；
        //    · 无硬 Max 时：count 恒 1 ⇒ 不存在「批太小」的批 ⇒ 无批可减。
        //    ⇒ 两种情形下 Min 都不改变 count。**硬 Min 要起作用必须先有「优化性拆批」**，
        //      而优化性拆批的搜索预算/切点语义本轮无载体（EBD-02/03）⇒ 一并待裁。
        //    （旧版此处有一段 Min 收缩 while 循环，经上述分析确认**不可达**，已删除；
        //      「硬 Max vs 硬 Min 冲突」因此在 V1 **不会发生**，无需优先级裁决代码。）
        return count;
    }

    /// <summary>
    /// P0-01/P0-03：按执行批数量克隆需求（只改数量，其余字段**全量逐字拷贝**，不得漏项）。
    ///
    /// 只在 `batches.Count &gt; 1` 时调用；`count == 1` 一律直接用原 `LogicalProductionDemand` 实例
    /// （保证单批路径与旧版**逐字一致**，零回归）。
    /// </summary>
    private static LogicalProductionDemand CloneDemandWithBatchQty(
        LogicalProductionDemand demand,
        decimal netOutputQty,
        decimal plannedProcessQty)
        => new()
        {
            LogicalDemandKey = demand.LogicalDemandKey,
            PlanVersionId = demand.PlanVersionId,
            DomainKey = demand.DomainKey,
            AllocationSequence = demand.AllocationSequence,
            DemandKey = demand.DemandKey,
            OrderId = demand.OrderId,
            MaterialId = demand.MaterialId,
            FactoryId = demand.FactoryId,
            StartStageCode = demand.StartStageCode,
            RequiredStageCode = demand.RequiredStageCode,
            StartOperationCode = demand.StartOperationCode,
            NetOutputQty = netOutputQty,
            PlannedProcessQty = plannedProcessQty,
            UOM = demand.UOM,
            RequiredAvailableTime = demand.RequiredAvailableTime,
            DemandSequence = demand.DemandSequence,
            ProductionInstructionNo = demand.ProductionInstructionNo,
            IsUnlocated = demand.IsUnlocated,
            PreferredResourceId = demand.PreferredResourceId,
            FallbackResourceId = demand.FallbackResourceId,
            IsContinuation = demand.IsContinuation,
            ContinuationKey = demand.ContinuationKey,
            RouteCode = demand.RouteCode,
            PathId = demand.PathId,
            NoSplitMerge = demand.NoSplitMerge,
            PreferredResourceCode = demand.PreferredResourceCode
        };

    private FinalTaskDraft CreateTask(
        LogicalProductionDemand demand,
        OperationNode operation,
        int? resourceId,          // 非资源工序（UNCONSTRAINED/WAIT_ONLY）传 null → Task.ResourceId=NULL，不占资源（0号位 2026-09-22 裁决）
        DateTime start,
        DateTime end,
        decimal setupMinutes,     // item1 接线（阶段二）：规则解析出的换型分钟（调用方经 FindSlotWithDynamicSetup/收敛迭代取得）
        ConstraintContext constraints,
        string? setupSource = null,  // SetupSource 填充（v1.2/5号位 值契约）：SetupOutcome → 大写 5 值
        string? batchDraftKey = null)   // P0-01/P0-02：本批归批键（null ⇒ 回落 ExecutionBatchKey(demandKey, 1)）
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
            ResourceCode = GetResourceCode(resourceId, constraints),
            RouteCode = operation.RouteCode,
            PathId = operation.PathId,
            Quantity = demand.NetOutputQty,
            PlannedProcessQty = demand.PlannedProcessQty,
            UOM = demand.UOM ?? string.Empty,
            PlannedStartTime = start,
            PlannedEndTime = end,
            SetupTime = setupMinutes,   // item1 接线：规则值（原 operation.SetupTime 已废止，v1.2 §1.2）
            SetupSource = setupSource,  // SetupSource 填充：SetupOutcome → 大写 5 值（2号位 原样落库）
            Priority = demand.DemandSequence,
            IsVirtual = false,
            // v1.6 §新增/替换实施要求：FinalTask 必须原样回传 ContinuationKey + 回传真实 RouteCode/PathId。
            // ContinuationKey：1号位 **不生成、不解析**，从源 Demand 逐字拷贝（null 表示无该身份）。
            ContinuationKey = demand.ContinuationKey,
            // ExecutionBatchDraftKey：同一执行批下多 Operation 共 Key。
            // P0-02：键域为 (需求键, 批序号)，**不再由 Route/Path 派生**；批键由本批入参给定，缺省即 1 号批。
            ExecutionBatchDraftKey = batchDraftKey ?? ExecutionBatchKey(demand.LogicalDemandKey)
        };
    }

    /// <summary>
    /// P1-08修复：资源编码回填（ResourceId → ResourceCode）。
    /// 查不到时返回空串，不抛异常（资源定义缺省时 FinalTaskDraft.ResourceCode 留空，2号位落库兜底）。
    /// resourceId 为 null（非资源工序 Task）时同样返回空串——非资源 Task 无资源编码。
    /// </summary>
    private static string GetResourceCode(int? resourceId, ConstraintContext constraints)
        => resourceId is int id && constraints.ResourceCodes.TryGetValue(id, out var code)
            ? code
            : string.Empty;

    /// <summary>
    /// P0-04修复：获取资源产能系数
    /// 第4轮C1修复：索引加入MaterialId
    /// </summary>
    private decimal? GetCapacityFactor(int materialId, OperationNode operation, int resourceId, ConstraintContext constraints)
    {
        // 0号位 2026-09-29 裁决 §5.3 落实：与 GetEligibleResources 同步升维（键补 ProductionDepartmentId）
        var key = new EligibilityLookupKey(
            materialId, operation.ProductionDepartmentId, operation.RouteCode, operation.PathId, operation.OperationCode);
        if (constraints.ResourceCapacityFactors.TryGetValue(key, out var resourceFactors))
        {
            if (resourceFactors.TryGetValue(resourceId, out var capacityFactor))
            {
                return capacityFactor;
            }
        }
        // 第5轮修复：CapacityFactor查不到时返回null，不静默使用1.0
        return null;
    }

    /// <summary>
    /// 第4轮Merge修复：尝试合并到已有Task，或新排程
    /// 文档§十 10.3：不同Demand可以合并成一个FinalTask，只要Material/Operation/工艺相容、Resource能力允许、交期不被破坏、AllocationTaskShare完整保留
    /// </summary>
    private List<FinalTaskDraft> TryMergeOrSchedule(
        LogicalProductionDemand demand,
        List<OperationNode> operations,
        RoutingGraph routingGraph,
        string direction,
        ConstraintContext constraints,
        Dictionary<int, List<TimeWindow>> resourceOccupancy,
        List<FinalTaskDraft> scheduledTasks,
        Dictionary<string, List<(string DemandKey, decimal ShareQty)>> allocationTaskShare,
        Dictionary<string, LogicalProductionDemand> demandByKey,
        DateTime planningStart,
        DateTime planningEnd,
        DateTime dynamicMaterialFloor,
        StageOverlapParams stageOverlap,
        string? batchDraftKey = null)   // P0-01/P0-02：本批归批键（转交 ScheduleDemandOperations）
    {
        // P0-07：连续份额不可被普通 Merge 破坏逐工单身份，直接独立排程，不尝试合并。
        // v1.6 `:26` + 0号位 2026-10-07 裁决 `:246`「`NoSplitMerge` 及固定 Route/Path 应**显式落实**」：
        //   A/B 语义含 `NoSplitMerge=true`（不拆不合）⇒ 此处显式读该冻结字段，
        //   不再只靠 `IsContinuation` 间接覆盖（两者当前同源，但契约字段须显式落实）。
        if (demand.IsContinuation || demand.NoSplitMerge)
        {
            return ScheduleDemandOperations(
                demand, operations, routingGraph, direction, constraints,
                resourceOccupancy, planningStart, planningEnd, dynamicMaterialFloor, stageOverlap, batchDraftKey);
        }

        // 检测是否可以合并到已有Task
        var candidateTasks = FindMergeableTasks(demand, operations, scheduledTasks, constraints, demandByKey);

        if (candidateTasks.Count > 0)
        {
            // 尝试将Demand合并到第一个候选Task
            var targetTask = candidateTasks[0];

            // 检查合并后是否破坏交期：合并后Duration增加，End时间延后
            var mergedTask = TryMergeDemandIntoTask(demand, targetTask, routingGraph, constraints, resourceOccupancy, allocationTaskShare, planningEnd, demandByKey);
            if (mergedTask != null)
            {
                // 合并成功：替换scheduledTasks中的旧Task
                var index = scheduledTasks.FindIndex(t => t.FinalDraftId == targetTask.FinalDraftId);
                if (index >= 0)
                {
                    scheduledTasks[index] = mergedTask;
                }
                // 不生成新Task
                return new List<FinalTaskDraft>();
            }
        }

        // 无法合并：正常排程
        return ScheduleDemandOperations(
            demand,
            operations,
            routingGraph,
            direction,
            constraints,
            resourceOccupancy,
            planningStart,
            planningEnd,
            dynamicMaterialFloor,
            stageOverlap,
            batchDraftKey);
    }

    /// <summary>
    /// 第4轮Merge修复：查找可合并的已有Task
    /// 条件：Material/Operation/工艺相容、Resource相同、时间窗口邻近
    /// 第5轮修复：多工序Demand必须检查完整Routing兼容性
    /// </summary>
    private List<FinalTaskDraft> FindMergeableTasks(
        LogicalProductionDemand demand,
        List<OperationNode> operations,
        List<FinalTaskDraft> scheduledTasks,
        ConstraintContext constraints,
        Dictionary<string, LogicalProductionDemand> demandByKey)
    {
        var candidates = new List<FinalTaskDraft>();

        // 第5轮修复：多工序场景不能Merge到单一Task
        // Merge只适用于单工序Demand，多工序必须独立排程保证DAG完整性
        if (operations.Count != 1)
        {
            return candidates; // 多工序不支持Merge
        }

        var targetOp = operations[0];

        // 遍历已排程的Task，找同Material、同Operation的Task
        foreach (var task in scheduledTasks)
        {
            // Material必须相同
            if (task.MaterialId != demand.MaterialId) continue;

            // FactoryId必须相同
            if (task.FactoryId != demand.FactoryId) continue;

            // Stage和Operation必须完全匹配
            if (task.StageCode != (targetOp.StageCode ?? string.Empty)) continue;
            if (task.OperationCode != targetOp.OperationCode) continue;

            // ── P0-06（0号位 2026-10-07 (5).md）：**跨 Path 不得 Merge** ──
            //   评估 Routing A 时若把既有 Routing B 的 Task 当成合并目标，会保留 target 的
            //   (RouteCode, PathId, ExecutionBatchDraftKey) ⇒ 同一 Execution Batch 里混入另一条 Path 的
            //   节点与资源，违反「每个 Execution Batch 只允许一条完整 Path」。
            //   两条 Path 存在**同名工序码**时（OP10 在 A、B 各一份）本检查是唯一拦截点。
            if (!string.Equals(task.RouteCode ?? string.Empty, targetOp.RouteCode ?? string.Empty, StringComparison.Ordinal))
            {
                continue;
            }
            if (task.PathId != targetOp.PathId)
            {
                continue;
            }

            // S29：执行起点一致性校验——不同执行起点不得合批，避免已完成工序被重新排产
            // 反查候选Task的来源Demand，比较 StartStageCode / StartOperationCode
            if (!demandByKey.TryGetValue(task.SourceDraftId, out var sourceDemand))
            {
                continue; // 无法追溯来源 demand，保守不 merge
            }
            // P0-07：连续份额任务不能被普通 Merge 吸收，自由份额也不得并入连续份额任务（保逐工单身份）。
            // NoSplitMerge（不拆不合）显式落实：任一侧带该标记都不得合批。
            if (sourceDemand.IsContinuation || demand.IsContinuation
                || sourceDemand.NoSplitMerge || demand.NoSplitMerge)
            {
                continue;
            }
            if (!string.Equals(demand.StartStageCode, sourceDemand.StartStageCode, StringComparison.Ordinal))
            {
                continue; // 不同 StartStage，不 merge
            }
            if (!string.Equals(demand.StartOperationCode, sourceDemand.StartOperationCode, StringComparison.Ordinal))
            {
                continue; // 不同 StartOperation，不 merge
            }

            candidates.Add(task);
        }

        return candidates;
    }

    /// <summary>
    /// 第4轮Merge修复：尝试将Demand合并到已有Task
    /// 检查Resource能力是否允许、交期是否被破坏
    /// 返回合并后的新Task，失败时返回null
    /// </summary>
    private FinalTaskDraft? TryMergeDemandIntoTask(
        LogicalProductionDemand demand,
        FinalTaskDraft targetTask,
        RoutingGraph routingGraph,
        ConstraintContext constraints,
        Dictionary<int, List<TimeWindow>> resourceOccupancy,
        Dictionary<string, List<(string DemandKey, decimal ShareQty)>> allocationTaskShare,
        DateTime planningEnd,
        Dictionary<string, LogicalProductionDemand>? demandByKey = null)
    {
        // 计算合并后的总数量
        var mergedQty = targetTask.PlannedProcessQty + demand.PlannedProcessQty;

        // 获取Operation信息
        // 0号位 2026-09-29 裁决 §5.3：节点身份 = (StageCode, OperationCode)。
        // FinalTaskDraft 侧两字段均已在建 Task 时落值（:1352/:1393 `StageCode = operation.StageCode ?? string.Empty`），
        // 与 OperationNodeKey.Of 的 null→Empty 归一化一致，可安全回查。
        if (!routingGraph.Operations.TryGetValue(
                OperationNodeKey.Of(targetTask.StageCode, targetTask.OperationCode), out var operation))
        {
            return null; // Operation不存在，无法合并
        }

        // 非资源工序 Task（OperationPlanningMode=UNCONSTRAINED/WAIT_ONLY，ResourceId=null，0号位 2026-09-22 裁决）
        // 不占资源、无产能系数可查，无法参与「基于资源占用窗」的合并 → 直接放弃合并，语义上等价于原来就匹配不到资源窗。
        if (targetTask.ResourceId is not int targetResourceId)
        {
            return null;
        }

        // 计算合并后的Duration
        // 第5轮修复：CapacityFactor缺失或非法时不能继续
        var capacityFactor = GetCapacityFactor(demand.MaterialId, operation, targetResourceId, constraints);
        if (capacityFactor == null || capacityFactor <= 0)
        {
            return null; // CapacityFactor缺失/非法，无法计算合并后Duration
        }
        var mergedDuration = operation.StandardDuration * mergedQty / capacityFactor.Value;
        var newDuration = TimeSpan.FromMinutes((double)mergedDuration);

        // 计算新的结束时间
        var newEndTime = targetTask.PlannedStartTime + newDuration;

        // 检查是否超出计划窗口
        if (newEndTime > planningEnd)
        {
            return null; // 合并后超出计划窗口，无法合并
        }

        // ── P0-04（0号位 2026-10-07 (5).md）：Merge 必须校验**交期不被破坏** ──
        //   旧实现只查 PlanningEnd + 资源后续冲突，**未查新 Demand 的有效交期，也未查被合并 Task
        //   已有份额的交期是否因延长而恶化**。合批会延长 Task 的 PlannedEndTime ⇒ 必须验证：
        //   ① 本 Demand：newEndTime 不得越过 `RequiredAvailableTime`（有效交期）；
        //   ② target 已服务的**全部既有份额**（血缘在 allocationTaskShare，另加 SourceDraftId 兜底）：
        //      目标 Task 原完成时间已合规，但延长后若越过任一既有份额的交期 ⇒ 该份额被推违约 ⇒ 放弃合并。
        if (demand.RequiredAvailableTime != DateTime.MaxValue && newEndTime > demand.RequiredAvailableTime)
        {
            return null;
        }

        if (demandByKey is not null)
        {
            var targetDue = DateTime.MaxValue;
            void WidenDue(string demandKey)
            {
                if (demandByKey.TryGetValue(demandKey, out var d) && d.RequiredAvailableTime < targetDue)
                {
                    targetDue = d.RequiredAvailableTime;
                }
            }

            WidenDue(targetTask.SourceDraftId);
            if (allocationTaskShare.TryGetValue(targetTask.FinalDraftId, out var existingShares))
            {
                foreach (var (shareDemandKey, _) in existingShares)
                {
                    WidenDue(shareDemandKey);
                }
            }

            if (targetDue != DateTime.MaxValue && newEndTime > targetDue)
            {
                return null; // 延长后使既有份额交期恶化，无法合并
            }
        }

        // 第5轮修复：检查延长Task后是否与同资源的后续Task冲突
        // item1 接线（阶段二）顺手修复：自身占用窗起点 = PlannedStartTime - SetupTime。
        // 原比较用 PlannedStartTime 直接比 window.Start，漏了 Setup 偏移——规则 Setup>0 时自身窗
        // 永远匹配不上、被当成"其它Task冲突"，Merge 必被误拒。
        var targetOccStart = targetTask.PlannedStartTime - TimeSpan.FromMinutes((double)targetTask.SetupTime);
        if (resourceOccupancy.ContainsKey(targetResourceId))
        {
            var occupancies = resourceOccupancy[targetResourceId];
            foreach (var window in occupancies)
            {
                // 跳过当前Task自己的占用窗口
                if (window.Start == targetOccStart && window.End == targetTask.PlannedEndTime)
                {
                    continue;
                }

                // 检查延长后的结束时间是否侵入其他占用窗口
                if (newEndTime > window.Start && targetTask.PlannedStartTime < window.End)
                {
                    return null; // 延长后与资源上其他Task冲突，无法合并
                }
            }
        }

        // 创建合并后的新Task（因为FinalTaskDraft属性是init-only，不能修改已有对象）
        var mergedTask = new FinalTaskDraft
        {
            FinalDraftId = targetTask.FinalDraftId, // 保持相同的DraftId
            SourceDraftId = targetTask.SourceDraftId,
            MaterialId = targetTask.MaterialId,
            FactoryId = targetTask.FactoryId,
            StageCode = targetTask.StageCode,
            OperationCode = targetTask.OperationCode,
            TaskType = targetTask.TaskType,
            ResourceId = targetTask.ResourceId,
            ResourceCode = targetTask.ResourceCode,
            RouteCode = targetTask.RouteCode,
            PathId = targetTask.PathId,
            Quantity = targetTask.Quantity + demand.NetOutputQty, // 合并数量
            PlannedProcessQty = mergedQty, // 合并加工数量
            UOM = targetTask.UOM,
            PlannedStartTime = targetTask.PlannedStartTime,
            PlannedEndTime = newEndTime, // 新的结束时间
            SetupTime = targetTask.SetupTime,
            SetupSource = targetTask.SetupSource,   // SetupSource 填充：合并保留 target 来源（2号位 原样落库）
            Priority = targetTask.Priority,
            IsVirtual = targetTask.IsVirtual,
            // 合批：保留 target 批身份（同 Key ⇒ 同 Path 不变量天然成立 —— RouteCode/PathId 亦取自 target）。
            // 被吸收 Demand 的血缘不丢：见下方 allocationTaskShare 追加记录。
            // Merge 仅适用于非连续份额（FindMergeableTasks 已排除 IsContinuation）⇒ ContinuationKey 恒 null。
            ContinuationKey = targetTask.ContinuationKey,
            ExecutionBatchDraftKey = targetTask.ExecutionBatchDraftKey
        };

        // 找到targetTask在scheduledTasks中的索引，替换为mergedTask
        // 注意：这里需要外部传入scheduledTasks的引用并支持修改
        // 简化处理：直接修改字段（需要调整方法签名）

        // 第6轮Merge修复：记录AllocationTaskShare时保留原Task的历史Demand
        // 原Task可能已经服务于其他Demand，不能覆盖
        if (!allocationTaskShare.ContainsKey(mergedTask.FinalDraftId))
        {
            allocationTaskShare[mergedTask.FinalDraftId] = new List<(string, decimal)>();
        }
        // 追加当前Demand的份额（原Task的份额已在之前记录）
        allocationTaskShare[mergedTask.FinalDraftId].Add((demand.LogicalDemandKey, demand.NetOutputQty));

        // 第6轮Merge修复：更新资源占用，包含Setup时间
        if (resourceOccupancy.ContainsKey(targetResourceId))
        {
            // 移除旧的时间窗（item1 接线：按含 Setup 的占用起点匹配）
            var oldWindows = resourceOccupancy[targetResourceId]
                .Where(w => w.Start == targetOccStart && w.End == targetTask.PlannedEndTime)
                .ToList();

            foreach (var oldWindow in oldWindows)
            {
                resourceOccupancy[targetResourceId].Remove(oldWindow);
            }

            // 添加新的时间窗：Setup时间也占用资源
            var setupDuration = TimeSpan.FromMinutes((double)mergedTask.SetupTime);
            var resourceStart = mergedTask.PlannedStartTime - setupDuration;
            resourceOccupancy[targetResourceId].Add(
                new TimeWindow(resourceStart, newEndTime));
        }

        // item1 接线（阶段二）：产品时间线同步——Merge 是同物料合并（v1.2 §六 同产品语义，Setup 继承不变），
        // 仅占用末端延长：移除旧末端、登记新末端。
        constraints.ProductTimeline.Remove(targetResourceId, targetTask.PlannedEndTime, targetTask.MaterialId);
        constraints.ProductTimeline.Place(targetResourceId, newEndTime, targetTask.MaterialId);

        return mergedTask; // 合并成功，返回合并后的Task
    }

    /// <summary>
    /// P0-08：同 PI 连续份额先于自由份额的稳定排序。
    /// 仅在「同 PI 组内」把 IsContinuation=true 的 demand 移到 IsContinuation=false 之前；
    /// 跨 PI 的相对顺序按「该 PI 首次出现的位置」保持，不破坏跨物料父子拓扑序
    ///（同一 PI 的连续/自由份额属于同一物料、同一拓扑层）。
    /// 无 PI（ProductionInstructionNo 为空）的 demand 不参与分组，保持原序。
    /// </summary>
    private static List<LogicalProductionDemand> OrderContinuityFirst(List<LogicalProductionDemand> demands)
    {
        // 无连续份额时直接返回原列表，避免无意义的排序开销与顺序扰动。
        if (demands.Count == 0 || !demands.Any(d => d.IsContinuation))
        {
            return demands;
        }

        var firstIndexByPI = new Dictionary<string, int>();
        for (var i = 0; i < demands.Count; i++)
        {
            var pi = demands[i].ProductionInstructionNo;
            if (string.IsNullOrEmpty(pi)) continue; // 无 PI 不分组
            if (!firstIndexByPI.ContainsKey(pi))
            {
                firstIndexByPI[pi] = i;
            }
        }

        return demands
            .Select((d, i) => new
            {
                Demand = d,
                Index = i,
                // 有 PI：用该 PI 首次出现的位置作为组主键；无 PI：用自身下标，保持原序不合并。
                GroupRank = string.IsNullOrEmpty(d.ProductionInstructionNo) ? i : firstIndexByPI[d.ProductionInstructionNo!]
            })
            .OrderBy(x => x.GroupRank)
            .ThenBy(x => x.Demand.IsContinuation ? 0 : 1)
            .ThenBy(x => x.Index)
            .Select(x => x.Demand)
            .ToList();
    }

    /// <summary>
    /// P0-17修复：获取工序间的Lag时间（分钟）
    /// 应用Routing LagTime到工序间时间依赖
    /// 第4轮审核修正：Dependencies按ToOperationCode存储，应查toOperationCode
    /// 0号位 2026-09-29 裁决 §5.3：节点身份升维为 (StageCode, OperationCode)，
    ///   出入参由 string operationCode 改为 OperationNodeKey，避免同码跨 Stage 时取到错误边的 LagTime。
    /// </summary>
    private decimal GetLagTime(OperationNodeKey fromNode, OperationNodeKey toNode, RoutingGraph routingGraph)
    {
        // Dependencies结构：Key=To节点, Value=该To的所有前驱边
        // 应查找toNode的前驱边列表，找到From匹配的边
        if (routingGraph.Dependencies.TryGetValue(toNode, out var edges))
        {
            // 找到从fromNode来的边
            var edge = edges.FirstOrDefault(e => e.From == fromNode);
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
    /// P0-04修复：Merge M:N 份额追溯（Demand → Task 的份额血缘），
    /// 由 Phase2 产出并传递给 Phase5，避免合并批次的份额血缘丢失。
    /// Key = 合并后 Task 的 FinalDraftId，Value = 该 Task 承载的各 Demand 份额。
    /// </summary>
    public Dictionary<string, List<(string DemandKey, decimal ShareQty)>> AllocationTaskShare { get; set; } = new();

    /// <summary>
    /// P0-03+P0-15修复：技术失败标记（Routing非法、数据结构错误等）
    /// </summary>
    public bool TechnicalFailure { get; set; } = false;
    public string? TechnicalFailureReason { get; set; }
}
