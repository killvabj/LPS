using System.Diagnostics.CodeAnalysis;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Shared.Models;

namespace LPS.APS.Scheduling.Solvers;

/// <summary>
/// Phase 1: 硬约束构建器
/// 文档：《APS_V1_1号位有限产能排程开发实施包_v1.2_20260906_PI_Position执行起点上下文冻结对齐版.md》§六 Phase 1
///
/// 职责：
/// - 建立 Routing（工艺路线）
/// - Resource Eligibility（资源资格约束）
/// - Calendar（日历可用时间）
/// - Material AvailableTime（物料多段可用时间）
/// - Execution/Firm/Frozen（不可逆约束）
/// - Shared-resource blocks（共享资源阻挡）
/// - Quantity-Time（数量-时间约束）
/// - 工序先后关系
/// </summary>
internal class PhaseOneConstraintBuilder
{
    /// <summary>
    /// 构建硬约束上下文
    /// </summary>
    public ConstraintContext BuildConstraints(DomainSolveRequest request)
    {
        var context = new ConstraintContext();

        // ── R09-③（2026-10-29 落码）：Phase2 要按 WORK 窗口推进「中间无 Routing Stage 的提前期」──
        //   为什么挂在上下文上而不是改签名：`ScheduleForward`/`ScheduleBackward` 的形参里没有 request，
        //   而它们的 5 个调用点**全在 Phase2 内部**（`PhaseTwoInitialScheduler.cs:3677/3683/3748/3771`）⇒
        //   改签名要连测试夹具一起动；挂只读引用只动一处，行为面完全一样。
        //   ⚠ 只持有**引用**：不改 request、不复制大表（试排克隆共用同一份，天然只读）。
        context.Request = request;

        // R-2 设备口径白名单（1号位 2026-10-16 回执 §四）：**原样透传**、**不改一字**。
        //   本号位不做资源身份判别、不硬编码阈值 —— 判据（`ManualSlotResourceOffset`）在 2号位 装载侧。
        //   ⚠ 三态（`null` 未投影／非 null 纯设备白名单／**空集 ≠ null**）由 `ConstraintContext` 属性承载；
        //     本处**不得**做 `?? Array.Empty<int>()` 之类的归一化（会把「未投影」与「无纯设备」抹平）。
        context.DeviceResourceIds = request.DeviceResourceIds;

        // ═══════════════════════════════════════════════
        // 0.0 StageSeq 全序校验（B-1，0号位 2026-09-29 裁决 §二）
        //     冲突物料记入 context.StageSequenceConflictMaterialIds ⇒ 下游不生成正式计划
        // ═══════════════════════════════════════════════
        ValidateStageSequences(request, context);

        // ═══════════════════════════════════════════════
        // 0. 部门锁定（最小 B）：按 (MaterialId, StageCode) → ProductionDepartmentId
        //    过滤 Routing 三件套；缺失 Context 的 Material 记入 context.MissingDepartmentContextMaterialIds
        // ═══════════════════════════════════════════════
        ApplyDepartmentLock(request, context,
            out var lockedOperations,
            out var lockedDependencies,
            out var lockedEligibilities);

        // ═══════════════════════════════════════════════
        // 1. 解析工序依赖图（使用部门锁定后的 Routing）
        // ═══════════════════════════════════════════════
        BuildRoutingGraphs(lockedOperations, lockedDependencies, context);

        // ═══════════════════════════════════════════════
        // 1.5 解析跨物料依赖 DAG（任务喂任务，方案A）：子先父后拓扑分层
        // ═══════════════════════════════════════════════
        BuildCrossMaterialDag(request, context);

        // ═══════════════════════════════════════════════
        // 2. 解析工序资源资格（使用部门锁定后的 Eligibility）
        // ═══════════════════════════════════════════════
        BuildOperationResourceEligibility(lockedEligibilities, context);

        // ═══════════════════════════════════════════════
        // 2.5 解析资源编码（ResourceId → ResourceCode）
        // ═══════════════════════════════════════════════
        BuildResourceCodes(request, context);

        // ═══════════════════════════════════════════════
        // 2.6 工序资源模式合法性（R2-F）——**分层**处置：
        //     · 值域损坏（模式越域）⇒ 整域 Fail Closed（①/④）；
        //     · 候选 Path 无合法资源候选 ⇒ **仅该候选不成立** + 留痕（②/③），**不**传播为整域失败。
        // ═══════════════════════════════════════════════
        ValidateOperationPlanningModes(request, context);

        // ═══════════════════════════════════════════════
        // 3. 解析资源日历（Resource → 可用时间窗列表）
        // ═══════════════════════════════════════════════
        BuildResourceCalendars(request, context);

        // ═══════════════════════════════════════════════
        // 4. 解析物料多段可用性（AllocationSequence → Quantity-Time 分段）
        // ═══════════════════════════════════════════════
        BuildMaterialAvailability(request, context);

        // ═══════════════════════════════════════════════
        // 5. 解析锁定任务约束（DraftId → 锁定信息）
        // ═══════════════════════════════════════════════
        BuildLockedTasks(request, context);

        // ═══════════════════════════════════════════════
        // 6. 解析共享资源占用块（Resource → 占用时间块列表）
        // ═══════════════════════════════════════════════
        BuildResourceBlocks(request, context);

        // ═══════════════════════════════════════════════
        // 7. 装载 Setup 换型规则（P1-02 item1：第⑦块 → EXACT/DEFAULT 查找字典）
        // ═══════════════════════════════════════════════
        BuildSetupTransitionRules(request, context);

        // ═══════════════════════════════════════════════
        // 8. 装载 RunScope（M5 第一批：Run 级交期覆盖 + Task 软目标）
        // ═══════════════════════════════════════════════
        BuildRunScope(request, context);

        // ═══════════════════════════════════════════════
        // 9. 装载 ⑧块 Batch Policy（P0-01：Material + ProductionDepartment 键控）
        // ═══════════════════════════════════════════════
        BuildExecutionBatchPolicies(request, context);

        return context;
    }

    /// <summary>
    /// R2-F（0号位 2026-10-14《致1号位第五轮代码审核正式复核回复 v1.0》§三 R2-F）：**`ResourceId == null`
    ///   的合法性必须在 Solver 的「生产入口」校验** —— 判词逐字：「仅在评分诊断处增加计数并不足以关闭错误路径」。
    ///
    ///   冻结判别器 = **工序的资源模式**（`UNCONSTRAINED`/`WAIT_ONLY` ⇒ 非资源工序；0号位 2026-09-22 裁决），
    ///   而 `FinalTaskDraft` **不携带**该模式 ⇒ 模式一旦**越域**，下游**无法**判定
    ///   `ResourceId == null` 是「合法非生产」还是「契约违背」——后者会被资源效率层**静默排除**（少算占用）。
    ///
    ///   ── **R2-F1 整改（0号位 2026-10-14《致1号位第六轮代码审核正式复核意见 v1.0》§四 R2-F1，`CONFIRMED`）** ──
    ///   判词逐字：「**要求停止无差别传播失败**」「是否对整域 Fail Closed 应由对应已冻结硬合同和事实影响范围判定，
    ///   而不是把『单个路径没有资源资格』一律升级成『整个 Domain 失效』」。
    ///   ⇒ 本入口按同文 §四 的**四分层**处置，**不再**把所有异常都升级为整域失败：
    ///     ① **输入值域损坏（工序资源模式越域）** ⇒ **该候选 Path 整条排除**（不产 Task）+ 登记输入契约 Issue，
    ///        **不**传播为整域失败（**2026-10-14 第七轮 §八.2a 口径变更**，理由见下文专段）；
    ///     ② **某一合法候选 Path 缺可用资格/无可用设备** ⇒ **仅该候选 Path 不成立**（**不抛**，见下）；
    ///     ③ **一个需求全部 Path 均无法合法排下** ⇒ 既有未排语义（**不抛**，保留净量/身份/诊断）；
    ///     ④ **整个运行契约不可组装** ⇒ 同 ①。
    ///
    ///   **为什么 ② 不抛**（冻结依据 + 读码实测，非本函数自造注释）：
    ///     · 冻结依据 = 0号位 同文 §四 引《03有限产能》逐字：「资格不满足或资源/Calendar 无窗口，
    ///       **不伪造批次，保留未排净量、身份及诊断**」⇒ 缺资格的正确出口是「该候选不成立 / 该需求未排」；
    ///     · 读码实测：`PhaseTwoInitialScheduler.GetEligibleResources`（`:2650-2667`）无资格行 ⇒ 返回**空表**
    ///       ⇒ `eligibleResources.Count == 0` ⇒ 该候选**产不出任何 Task**（`:2287-2290` / `:2506-2509`）；
    ///       `GetCapacityFactor`（`:4520-4534`）缺系数 ⇒ 返回 `null` ⇒ 该资源**被跳过**
    ///       （`:2300` / `:2519`，**不静默回落 1.0**）
    ///       ⇒ 5th 轮 R2-F 要关闭的「真实 Operation Task 带 `ResourceId == null` 被静默排除」这一错误路径
    ///         **在这两种情形下结构上产不出**，已由 Phase2 自身关闭；入口再抛异常只会**多杀**合法兄弟 Path
    ///         与无关需求（0号位 判词逐字：「**合法的A也没有机会被排程**」）。
    ///     · **不静默**：改为经 `ConstraintContext.TraceNotes`（B.2 决策说明层，
    ///       `DomainSolveResult.SolveTraceNotes` 出口）逐需求登记「该候选 Path 无合法资源候选」。
    ///
    ///   ── **R2-F2 整改（同文 §四 R2-F2，`RISK_UNVERIFIED`）** ──
    ///   判词逐字：旧判据「并未验证同一 `ResourceId` 同时存在于两个集合」；要求「以资源身份**交集**检查
    ///   `resourceIds` 所指具体合法资源是否拥有**同 ID、同工序、正数**产能系数」。⇒ 本入口改为
    ///   `resourceIds` 中**存在**某个 `resourceId` 使 `ResourceCapacityFactors[key][resourceId] > 0`；
    ///   「资格 `{R1}` / 系数只有 `{R2}`」不再被放过（旧式 `factors.Values.Any(cf > 0m)` 会放过）。
    ///
    ///   ⚠ 本校验**只**判定「模式与资格是否可判定」，**不**改变「无合格资源 ⇒ 该需求不排程」
    ///     （<see cref="PhaseTwoInitialScheduler"/> 的既有出口语义）；也**不**把「非资源工序」判为非法。
    ///   ⚠ `OperationPlanningMode` 缺失已在 `BuildRoutingGraphs`（`:658`）收敛为 `"FINITE_RESOURCE"`
    ///     ⇒ ① 分支只会被**真实的、未登记的**非空值触发。
    ///
    ///   ── **① 的 2026-10-14 第七轮口径变更（0号位 §八.2a）** ──
    ///   判词逐字：「『任意一个候选 Path 的一个非法枚举值都必然使整个 Domain 失败』**不是**本次证据能推出的结论」
    ///   「若能**可靠识别该 Path/Demand**，**禁止此非法 Path 产生 Task、登记输入契约 Issue**，其他完整合法 Path
    ///   及无关 Demand 仍应被求解」「若整个请求所必需的**全局类型/结构**确实无法解释和安全组装，再走整域 Fail Closed」。
    ///   ⇒ 本入口改为 **整条排除该 (MaterialId, RouteCode, PathId) 候选 Path**
    ///     （置 <see cref="RoutingGraph.IsOperationPlanningModeInvalid"/>）+ 登记 **Error 级**输入契约 Issue，
    ///     **不抛异常**。理由（读码实测，非自造）：模式是**逐节点**事实，而节点自带
    ///     `RouteCode/PathId/StageCode/OperationCode` ⇒ 损害面**可可靠定位**到单条候选 Path，
    ///     不构成「整个请求所必需的全局类型/结构无法解释」；旧行为的代价则是**同需求合法 Path
    ///     与无关需求一并被拒**（与 ② 类同款副作用，即 0号位 点名的「合法的A也没有机会被排程」）。
    ///   ⚠ **本入口此后不再有任何整域 Fail Closed 分支**；④「整个运行契约不可组装」仍由**既有**出口承载
    ///     （<see cref="SolverInputContractException"/>：`FiniteCapacitySolver.cs:75`/`:102` 捕获，
    ///     本文件同类的既有抛点见 `:1040` 的越界校验）。
    ///
    ///   ── **为什么是 `internal` 而非 `private`（0号位 第六轮 §四 R2-F2 的可证性要求）** ──
    ///   R2-F2 要求「增加资格 `R1`／系数只有 `R2`…等**三组差异反例**」。但读码实测
    ///   `BuildOperationResourceEligibility`（`:925-949`）中 `OperationResourceEligibility` 与
    ///   `ResourceCapacityFactors` 是**同一 `eligibilities` 列表、同一 `GroupBy` 键**的两个投影
    ///   ⇒ 两键集**恒等**，「资格 `{R1}` / 系数只有 `{R2}`」经 `DomainSolveRequest` **结构上不可达**。
    ///   若只留 Solver 级用例，则 R2-F2 的整改（区间式 vs 旧的 `Values.Any(cf > 0m)`）
    ///   在**所有可达输入上取值相同** ⇒ **无鉴别性反例**。故放宽本方法可见性至 `internal`，
    ///   由 `LPS.APS.Tests`（`InternalsVisibleTo`，`LPS.APS.Scheduling.csproj:23`）**直调谓词**构造
    ///   该不可达输入。**仅可见性放宽，签名与行为不变**（先例：`PhaseFiveCompression.cs:2230`）。
    /// </summary>
    internal static void ValidateOperationPlanningModes(DomainSolveRequest request, ConstraintContext context)
    {
        foreach (var materialGraphs in context.RoutingGraphs)
        {
            foreach (var pathEntry in materialGraphs.Value)
            {
                var pathKey = pathEntry.Key;
                var graph = pathEntry.Value;

                foreach (var node in graph.Operations.Values)
                {
                    var mode = node.OperationPlanningMode;

                    // ── ①（2026-10-14 第七轮 §八.2a）：模式**越域** ⇒ **该候选 Path 整条排除** ──
                    //   处置：置图标记（Phase2 据此不产 Task）+ 登记 Error 级输入契约 Issue；
                    //   **不抛** ⇒ 不牵连同需求其余合法 Path、也不牵连无关需求。
                    if (!IsLegalOperationPlanningMode(mode))
                    {
                        if (!graph.IsOperationPlanningModeInvalid)
                        {
                            graph.IsOperationPlanningModeInvalid = true;
                            RegisterOperationPlanningModeExclusion(
                                request, context, materialGraphs.Key, pathKey, node, mode);
                        }

                        // 该 Path 已整条排除 ⇒ **不再**对其工序做 ② 类缺项登记：
                        //   否则「模式越域（输入契约违背）」会被下游读成「无资源资格（业务不可行）」，把 Error 降级成 Warning。
                        continue;
                    }

                    if (mode != "FINITE_RESOURCE")
                    {
                        continue;   // 非资源工序：**合法地**不占资源（Task.ResourceId == null）
                    }

                    var identity = $"MaterialId={materialGraphs.Key}, RouteCode={node.RouteCode}, " +
                                   $"PathId={node.PathId}, StageCode={node.StageCode ?? "(null)"}, " +
                                   $"OperationCode={node.OperationCode}";

                    var key = new EligibilityLookupKey(
                        materialGraphs.Key, node.ProductionDepartmentId,
                        node.RouteCode, node.PathId, node.OperationCode);

                    // ── ② 资格缺项（**不再抛**）──
                    var hasEligibilityRows =
                        context.OperationResourceEligibility.TryGetValue(key, out var resourceIds)
                        && resourceIds is { Count: > 0 };

                    // ── R2-F2：资格资源与产能系数必须**同 ID**对上（交集，不是「两边各自非空」）──
                    var hasSameIdLegalFactor = false;
                    if (hasEligibilityRows)
                    {
                        hasSameIdLegalFactor =
                            context.ResourceCapacityFactors.TryGetValue(key, out var capacityFactors)
                            && resourceIds!.Any(
                                resourceId => capacityFactors.TryGetValue(resourceId, out var cf) && cf > 0m);
                    }

                    if (!hasEligibilityRows || !hasSameIdLegalFactor)
                    {
                        RegisterResourceCandidateGap(
                            request, context, materialGraphs.Key, identity,
                            hasEligibilityRows
                                ? "该候选 Path 该工序的资格资源**无同 ID 正产能系数**" +
                                  "（CapacityFactor 缺项／≤ 0／与资格不同源）"
                                : "该候选 Path 该工序**无任何资源资格行**（Eligibility 缺项／空集合）");
                    }
                }
            }
        }
    }

    /// <summary>
    /// 冻结值域判别器（唯一一处）：`FINITE_RESOURCE` 占资源；`UNCONSTRAINED` / `WAIT_ONLY` 非资源工序
    ///   （**合法地**产出 `ResourceId == null`）。与 `PhaseTwoInitialScheduler.IsNonResourceMode`
    ///   （`:2999`）**成对**：本方法判「合法非资源」，后者判「走非资源分支」——
    ///   两者必须同源，改其一请同步另一处。
    /// </summary>
    private static bool IsLegalOperationPlanningMode(string? mode)
        => string.Equals(mode, "FINITE_RESOURCE", StringComparison.Ordinal)
           || string.Equals(mode, "UNCONSTRAINED", StringComparison.Ordinal)
           || string.Equals(mode, "WAIT_ONLY", StringComparison.Ordinal);

    /// <summary>
    /// 追溯码（**决策说明层**，**非**冻结 `ReasonCode` 字典）：某候选 Path 因**工序资源模式越域**
    ///   被**整条排除**（R2-F ①，0号位 2026-10-14 第七轮 §八.2a）。
    /// ⚠ 与 ② 类的 <see cref="TraceResourceCandidateAbsent"/> **分列**（**不**同码、**不**同级别）：
    ///   本码是**输入契约违背**（`Level = "Error"`，权威事实不可解释）；后者是**该候选业务上不可行**
    ///   （`Level = "Warning"`）。两者根因、处置与下游告警都不同，合并会把 Error 降级成 Warning。
    /// </summary>
    internal const string TraceOperationPlanningModeOutOfDomain =
        "OPERATION_PLANNING_MODE_OUT_OF_DOMAIN_PATH_EXCLUDED";

    /// <summary>
    /// R2-F ①（0号位 2026-10-14 第七轮 §八.2a）：登记「某候选 Path 因 `OperationPlanningMode` 越域
    ///   被整条排除」这一**输入契约 Issue**。
    ///
    ///   ⚠ **不抛异常**（该候选 Path 不产 Task 由 Phase2 的候选剔除/Fail Closed 落实）。
    ///   ⚠ **不静默**：`Level = "Error"` 经 B.2 决策说明层出口（`DomainSolveResult.SolveTraceNotes`）留痕。
    ///   ⚠ 追溯键 = **`LogicalDemandKey`**（与 Phase2/3 留痕**同键域**，不在本层自造键域）；
    ///     一个物料可承载多个逻辑需求 ⇒ 逐个登记。
    ///   ⚠ 只登记**事实**，不自造业务裁决：若该需求**全部**候选 Path 均被排除，走既有未排语义
    ///     （Phase2/3 已按 15 码值域给出未排原因），本层不额外编造原因码。
    /// </summary>
    private static void RegisterOperationPlanningModeExclusion(
        DomainSolveRequest request,
        ConstraintContext context,
        int materialId,
        RoutePathKey pathKey,
        OperationNode node,
        string? mode)
    {
        var identity = $"MaterialId={materialId}, RouteCode={pathKey.RouteCode}, PathId={pathKey.PathId}, " +
                       $"StageCode={node.StageCode ?? "(null)"}, OperationCode={node.OperationCode}";

        foreach (var demand in request.LogicalProductionDemands)
        {
            if (demand.MaterialId != materialId)
            {
                continue;
            }

            context.TraceNotes.Add(new SolveTraceNote
            {
                Key = demand.LogicalDemandKey,
                ReasonCode = TraceOperationPlanningModeOutOfDomain,
                Message = "候选 Path 的工序资源模式**越域**（R2-F ①）：`OperationPlanningMode` 不在冻结值域 " +
                          "{FINITE_RESOURCE, UNCONSTRAINED, WAIT_ONLY} ⇒ 该工序产出的 Task 若 `ResourceId == null`，" +
                          "无法判定是「合法非资源工序」还是「契约违背」（会被静默排除出资源占用口径）。" +
                          "已**整条排除该候选 Path**（不得产出任何 FinalTask）—— 同需求的其余完整合法 Path " +
                          "与无关 Demand 仍应被求解，**不**升级为整域输入失败。" +
                          $"{identity}, OperationPlanningMode={mode ?? "(null)"}。",
                Level = "Error"
            });
        }
    }

    /// <summary>
    /// 追溯码（**决策说明层**，**非**冻结 `ReasonCode` 字典；与 `STAGE_TIMING_PATH_UNDETERMINED`
    ///   同一先例）：某候选 Path 的 `FINITE_RESOURCE` 工序**无合法资源候选**。
    /// ⚠ 刻意**不**用 `RESOURCE_EFFICIENCY_` 前缀 —— 那是 ④.6 资源效率层的专属码族，
    ///   下游按该前缀筛选「④.6 是否留痕」，两者不得混淆。
    /// </summary>
    internal const string TraceResourceCandidateAbsent = "FINITE_RESOURCE_CANDIDATE_ABSENT";

    /// <summary>
    /// R2-F1：登记「某候选 Path 的某 `FINITE_RESOURCE` 工序无合法资源候选」这一**非阻塞**事实。
    ///
    ///   ⚠ **不抛异常**（② 类：该候选 Path 不成立，由 Phase2 逐候选试排自然淘汰）。
    ///   ⚠ **不静默**：经 B.2 决策说明层出口留痕。
    ///   ⚠ 追溯键 = **`LogicalDemandKey`**（与 Phase2/3 留痕**同键域**，不在本层自造键域）；
    ///     一个物料可承载多个逻辑需求 ⇒ 逐个登记（夹具内通常 1:1）。
    ///   ⚠ 只登记**事实**，不自造业务裁决：该需求若全部候选 Path 均如此，走既有未排语义
    ///     （Phase2/3 已按 15 码值域给出未排原因），本层不额外编造原因码。
    /// </summary>
    private static void RegisterResourceCandidateGap(
        DomainSolveRequest request,
        ConstraintContext context,
        int materialId,
        string operationIdentity,
        string detail)
    {
        foreach (var demand in request.LogicalProductionDemands)
        {
            if (demand.MaterialId != materialId)
            {
                continue;
            }

            context.TraceNotes.Add(new SolveTraceNote
            {
                Key = demand.LogicalDemandKey,
                ReasonCode = TraceResourceCandidateAbsent,
                Message = "候选 Path 的 `FINITE_RESOURCE` 工序**无合法资源候选** —— " + detail
                          + $"；{operationIdentity}。该候选 Path **不成立**（Phase2 逐候选试排自然淘汰），"
                          + "**不**升级为整域输入失败；若该需求全部候选 Path 均如此，按既有未排语义出口"
                          + "（保留净量/身份/诊断，**不伪造批次**，**不补** 8h/7×24 日历）。",
                Level = "Warning"
            });
        }
    }

    /// <summary>
    /// B-1：StageSeq 全序校验（0号位 2026-09-29 裁决 §二，逐字口径）。
    ///
    /// 裁决原文要点：
    /// - §2.1 `StageSeq` 采用**全序链**语义，不采用「相同 StageSeq 表示并行 Stage」的偏序语义；
    ///   唯一性范围是**一个已解析好的具体 StageSequenceChain 内**，不是 Material 全局；
    /// - §2.2 同 StageCode 重复 → 取最小 StageSeq 收敛（DTO 既有兼容规则，「可以继续保留」）；
    ///   收敛后**不同 StageCode 不得拥有相同 StageSeq**；
    /// - §2.3 若冲突：不允许静默按数组顺序、不允许静默并行、输出明确 Issue、
    ///   **不生成可能顺序错误的正式计划**。
    ///
    /// 本方法只做「校验 + 登记冲突」，**不**在此建立有序链：有序链是 ④（无 Routing Stage 的
    /// StageTimingNode 时间层）的输入，尚无消费者，提前落库即死代码。冲突登记本身已是完整能力
    /// ——它直接改变该物料需求的排程结果（Unscheduled + 明确 Reason）。
    /// </summary>
    private void ValidateStageSequences(DomainSolveRequest request, ConstraintContext context)
    {
        foreach (var chain in request.StageSequenceChains)
        {
            // ① 同 StageCode 取最小 StageSeq 收敛（裁决 §2.2 明文保留的既有兼容规则）。
            //    2号位 装载时已做（StageSequenceChain 文档「同一 StageCode 取最小 StageSeq」），
            //    此处再收敛一次是防御：契约口径不因上游实现细节而失效。
            var converged = chain.Stages
                .GroupBy(s => s.StageCode ?? string.Empty, StringComparer.Ordinal)
                .Select(g => g.OrderBy(s => s.StageSeq).First())
                .ToList();

            // ② 全序校验：收敛后不同 StageCode 的 StageSeq 必须互不相同
            var hasDuplicateSeq = converged
                .GroupBy(s => s.StageSeq)
                .Any(g => g.Count() > 1);

            if (hasDuplicateSeq)
            {
                context.StageSequenceConflictMaterialIds.Add(chain.MaterialId);
            }
        }
    }

    /// <summary>
    /// 部门锁定（最小 B）：按 (MaterialId, StageCode) → ProductionDepartmentId 过滤 Routing 三件套。
    /// 文档：PM 裁定《ProductionDepartment回复.md》最小 B
    /// 1号位只消费 2号位传入的 MaterialStageDepartmentContexts，不得重新推导部门、不得跨部门优化选择。
    /// </summary>
    private void ApplyDepartmentLock(
        DomainSolveRequest request,
        ConstraintContext context,
        out List<RoutingOperation> lockedOperations,
        out List<RoutingDependency> lockedDependencies,
        out List<OperationResourceEligibility> lockedEligibilities)
    {
        // (MaterialId, StageCode) → ProductionDepartmentId（上游保证 (MaterialId, StageCode) 唯一）
        //
        // ── 0号位 2026-10-26 四份整改单 P0 ② 「部门冲突 Fail Closed」──
        //   旧实现 `g.First().ProductionDepartmentId`：同键出现**两个不同部门**时，取谁完全取决于
        //   载荷到达顺序 ⇒ **不可复现**，而且锁进来的可能是「别的部门的小工序集合」
        //   （v5.0.16 冻结：部门是「物料×阶段」联合属性，同码不同部门是**两套不同工序**）。
        //   这种静默择一错了也不报错。现改为：先登记冲突，**不猜**，由下方 1.5 整条排除该物料。
        //   ⚠ 只有「**不同**部门」才算冲突；同键同部门的重复行无害（照常取该部门）。
        var deptContext = new Dictionary<(int MaterialId, string StageCode), int>();
        var deptConflicts = new List<(int MaterialId, string StageCode, int FirstDept, int SecondDept)>();

        foreach (var group in request.MaterialStageDepartmentContexts.GroupBy(c => (c.MaterialId, c.StageCode)))
        {
            var firstDept = 0;
            var secondDept = 0;
            var hasFirst = false;
            var conflicted = false;

            foreach (var entry in group)
            {
                if (!hasFirst)
                {
                    firstDept = entry.ProductionDepartmentId;
                    hasFirst = true;
                }
                else if (!conflicted && entry.ProductionDepartmentId != firstDept)
                {
                    conflicted = true;
                    secondDept = entry.ProductionDepartmentId;
                }
            }

            if (!hasFirst)
            {
                continue;
            }

            deptContext[group.Key] = firstDept;

            if (conflicted)
            {
                deptConflicts.Add((group.Key.MaterialId, group.Key.StageCode, firstDept, secondDept));
            }
        }

        // 1. 识别缺失 Context 的 Material：仅校验「本次求解在范围」的工序 Stage。
        //    P1-06修复：StartOperation/StartStage 之前的已完成 Stage 不再参与本次求解，
        //    即使其缺 Department Context 也不应误杀当前剩余 Routing。
        //    无 Start 信息的需求（新单从首工序起）仍校验全量 Stage。
        var reachableStages = BuildReachableStages(request);

        // 供 Phase5 计算「无 Routing Stage」时间节点（0号位 2026-09-29 裁决 §十四第4项）。
        // 见 ConstraintContext.EffectiveStages 注释：语义不变，仅把原本的局部变量挂出来复用。
        context.EffectiveStages = reachableStages;

        // R09-③（2026-10-29）：把「中间无 Routing Stage」的提前期前置算好，供 Phase2 当硬时间约束。
        //   必须在 EffectiveStages 赋值**之后**调用 —— 它读 context.EffectiveStages 判「本范围无 Routing」。
        BuildRoutelessLeadSteps(request, context);
        foreach (var op in request.RoutingOperations)
        {
            var stageCode = op.StageCode ?? string.Empty;

            // 防御：该 Material 无任何需求时，不参与缺失判定。
            if (!reachableStages.TryGetValue(op.MaterialId, out var inScopeStages))
            {
                continue;
            }

            // P1-06修复：仅当该工序 Stage 在「本次求解范围」内时，才纳入缺失判定。
            if (!inScopeStages.Contains(stageCode))
            {
                continue;
            }

            if (!deptContext.ContainsKey((op.MaterialId, stageCode)))
            {
                context.MissingDepartmentContextMaterialIds.Add(op.MaterialId);
                // ── 2026-11-03｜**逐 (物料, Stage) 留痕**（纯观测，不参与判定）──────────────
                //   起因：`MISSING_PRODUCTION_DEPARTMENT_CONTEXT` 这一档此前**只登记物料号、
                //   不登记是哪个 Stage 缺**，Phase5 输出时只能给泛化原因 ⇒ 未排明细里
                //   2,143 行全是「无明细」，数据方拿到只有 DraftId、定位不了要补哪一行。
                //   这里把缺的 Stage 记下来，Phase5 据此拼「物料 + 缺的 Stage + 该物料现有部门」。
                //   ⚠ 同一个 (物料, Stage) 有多条工序行 ⇒ 去重，避免同一 Stage 重复登记。
                if (!context.MissingDepartmentContextStages.TryGetValue(op.MaterialId, out var missStages))
                {
                    missStages = new List<string>();
                    context.MissingDepartmentContextStages[op.MaterialId] = missStages;
                }
                if (!missStages.Contains(stageCode))
                {
                    missStages.Add(stageCode);
                }
            }
        }

        // 1.5 部门冲突 ⇒ **Fail Closed**（0号位 2026-10-26 P0 ②）。
        //   载体复用：与「缺 Context」同走 `MissingDepartmentContextMaterialIds`（物料整条不参与部门锁定
        //   ⇒ Phase2 无路由 ⇒ 业务 Unscheduled），**不新增契约字段、不改冻结 ReasonCode**。
        //   差别只在留痕：`PRODUCTION_DEPARTMENT_CONFLICT` 是**决策说明层**码（与 `PHASE5_BREACH_SCOPE`
        //   同一先例），明确指出真因是「同键多部门」而不是「没有部门」，两条路要补的东西不一样。
        //   ⚠ 只对**本次求解在范围内**的冲突 Stage 生效：起点之前已完成的 Stage 再怎么冲突也不牵连本批
        //     （与上方缺失判定的 P1-06 裁剪同口径）。
        foreach (var (materialId, stageCode, firstDept, secondDept) in deptConflicts)
        {
            if (!reachableStages.TryGetValue(materialId, out var conflictInScope) ||
                !conflictInScope.Contains(stageCode))
            {
                continue;
            }

            context.MissingDepartmentContextMaterialIds.Add(materialId);
            context.TraceNotes.Add(new SolveTraceNote
            {
                Key = materialId.ToString(),
                ReasonCode = "PRODUCTION_DEPARTMENT_CONFLICT",
                Message = $"物料 {materialId} 的 Stage `{stageCode}` 在 `MaterialStageDepartmentContexts` 中"
                          + $"对应了**多个不同部门**（{firstDept} / {secondDept} …）⇒ **Fail Closed**："
                          + "不猜任何一个部门，本物料整条不参与部门锁定（Phase2 无路由 ⇒ 业务未排）。"
                          + "该键本应唯一，请上游消除「同物料同阶段多部门」后重算；"
                          + "注意这不是「缺部门」，两者要补的数据不是一回事。",
                Level = "Error"
            });
        }

        // 1.6 挂出「(物料, Stage) → 部门」表（2026-10-28，供 Setup 键域含部门时反查）：
        //      冲突键一律不进表 ⇒ 查不到即 Setup 判 SourceUnavailable（WARNING），**不猜部门**。
        foreach (var (conflictMaterialId, conflictStageCode, _, _) in deptConflicts)
        {
            deptContext.Remove((conflictMaterialId, conflictStageCode));
        }
        context.DepartmentByMaterialStage = deptContext;

        // 2. 过滤 RoutingOperation：缺失 Material 整条剔除 + 部门不符剔除
        // v5.0.16 冻结：Routing 三件套唯一键升级为含 ProductionDepartmentId 的三元/四元组；
        // 部门=「物料×阶段」联合属性，同物料同阶段不同部门有不同小工序集合，
        // 因此 validOperationKeys 必须带 ProductionDepartmentId，否则会把别的部门同名 Operation 的
        // Dependency / Eligibility 漏进当前部门的锁定图。
        lockedOperations = new List<RoutingOperation>();
        var validOperationKeys = new HashSet<(int MaterialId, int ProductionDepartmentId, string RouteCode, string OperationCode)>();

        foreach (var op in request.RoutingOperations)
        {
            if (context.MissingDepartmentContextMaterialIds.Contains(op.MaterialId))
            {
                continue; // 缺失 Context 的 Material 整条剔除，Phase2 无路由 → Unscheduled
            }

            var stageCode = op.StageCode ?? string.Empty;

            // P1-06 对齐：与第一轮同口径——无需求 Material 不参与部门锁定；范围外 Stage
            // （续排起点之前已完成）不参与，避免索引 deptContext 时 KeyNotFound。
            if (!reachableStages.TryGetValue(op.MaterialId, out var inScopeStages2) ||
                !inScopeStages2.Contains(stageCode))
            {
                continue;
            }

            var expectedDeptId = deptContext[(op.MaterialId, stageCode)];

            if (op.ProductionDepartmentId != expectedDeptId)
            {
                continue; // 部门不符，剔除
            }

            lockedOperations.Add(op);
            validOperationKeys.Add((op.MaterialId, op.ProductionDepartmentId, op.RouteCode, op.OperationCode));
        }

        // 3. 过滤 RoutingDependency：两端 Operation 都必须已锁定。
        // ⚠ PM 0928-1 §五 终裁：「跨 Stage Dependency 不能要求 FromDepartment = ToDepartment，
        //   否则会错误限制真实制造流程」——`RoutingDependency.ProductionDepartmentId` 是**单值**，
        //   跨 Stage 边两端分属不同部门时，该行无论填哪个部门值都不可能同时命中两端 ⇒ 必被误杀。
        //   故此处按 (MaterialId, RouteCode, OperationCode) 判定：lockedOperations 已在上面按各自
        //   Stage 的期望部门过滤过，键内已隐含「部门正确」，无需再要求 dep 的部门值同时命中两端。
        //   同 Stage 边**零回归**（其 dep 部门本就等于两端部门）。
        //
        // ⚠ 残留（诚实登记，不静默）：本层只能用三元组做**必要**条件 —— `RoutingDependency` 契约内
        //   无 StageCode，无法在此判定边的 Stage 归属。同码跨 Stage 时，「另一 Stage 的同名工序仍在锁定集内」
        //   会让一条本应随其 Stage 一起被剔除的边在此存活。精确 Stage 归属在 `BuildRoutingGraphs` 里
        //   由图内节点反查消解（唯一 ⇒ 取；歧义 ⇒ 计数 + 丢边，不猜）。已核：同 Stage 边不会误接
        //   （同部门 ⇒ 两端节点都锁定 ⇒ 消解歧义 ⇒ 丢边计数，不产生错边）。
        var lockedOperationKeys = lockedOperations
            .Select(op => (op.MaterialId, op.RouteCode, op.OperationCode))
            .ToHashSet();

        lockedDependencies = request.RoutingDependencies
            .Where(dep =>
                lockedOperationKeys.Contains((dep.MaterialId, dep.RouteCode, dep.FromOperationCode)) &&
                lockedOperationKeys.Contains((dep.MaterialId, dep.RouteCode, dep.ToOperationCode)))
            .ToList();

        // 4. 过滤 OperationResourceEligibility：Operation 必须合法，且部门与锁定 Operation 一致
        lockedEligibilities = request.OperationResourceEligibility
            .Where(e => validOperationKeys.Contains((e.MaterialId, e.ProductionDepartmentId, e.RouteCode, e.OperationCode)))
            .ToList();
    }

    /// <summary>
    /// 按需求计算「本次求解在范围」的 Stage 集合。**两个取源取并集**：
    ///   ① <b>BOM/Stage 权威链</b>（`request.StageSequenceChains`，按起点裁剪）—— N8-01；
    ///   ② <b>Routing 可达集</b>（BFS 自起点工序，P1-06）。
    ///
    /// 【① 为什么必须进范围】0号位 2026-10-15 第九轮 §3.2/§3.3 + 冻结规则 S-006 +《04》场景7：
    ///   无 RoutingOperation 的 Stage 也要形成 StageTimingNode（只传播时间，不排 Task）。
    ///   原实现唯一取源是 `RoutingOperations`，无 Routing 的物料在下面直接 `continue`
    ///   ⇒ 这类 Stage 永远进不了范围 ⇒ `ResolveRoutelessStages` 恒返回空 ⇒ 时间链结构性不可达。
    ///
    /// 【P1-06 为什么仍要按起点裁剪】续排（有 StartOperationCode/StartStageCode）时，起点之前的
    ///   已完成 Stage 不在本次求解范围：既不该因此把整条 Routing 误判为
    ///   MISSING_PRODUCTION_DEPARTMENT_CONTEXT，也不该凭空多出提前期。
    ///   无 Start 信息的需求（新单从首工序起）⇒ 整条链都在范围内。
    ///
    /// 返回：MaterialId → 该物料在范围内的 StageCode 集合（①②并集，多个需求再取并集）。
    /// </summary>
    private Dictionary<int, HashSet<string>> BuildReachableStages(DomainSolveRequest request)
    {
        var result = new Dictionary<int, HashSet<string>>();

        // 预处理：物料 → 工序行 与依赖行（V1 默认 DEFAULT 路径）
        var opsByMaterial = request.RoutingOperations
            .GroupBy(op => op.MaterialId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var depsByMaterial = request.RoutingDependencies
            .GroupBy(dep => dep.MaterialId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // N8-01（0号位 2026-10-15 第九轮 §3.2/§3.3）：BOM/Stage 权威链也是 Stage 范围的取源。
        var chainByMaterial = BuildStageChainIndex(request);

        foreach (var demand in request.LogicalProductionDemands)
        {
            opsByMaterial.TryGetValue(demand.MaterialId, out var ops);

            // ① N8-01：BOM/Stage 权威链里、按起点裁剪后仍在范围内的 Stage（含零 Routing 的 Stage）。
            var chainStages = ResolveChainStagesInScope(request, demand, chainByMaterial);

            if (ops == null && chainStages.Count == 0)
            {
                continue; // 既无 Routing、又无 Stage 链 ⇒ 本次求解范围内没有 Stage
            }

            if (!result.TryGetValue(demand.MaterialId, out var stageSet))
            {
                stageSet = new HashSet<string>();
                result[demand.MaterialId] = stageSet;
            }

            foreach (var stageCode in chainStages)
            {
                stageSet.Add(stageCode);
            }

            if (ops == null)
            {
                continue; // 该物料无 Routing：上面已并入 Stage 链，没有 BFS 可跑
            }

            var depsForMaterial = depsByMaterial.TryGetValue(demand.MaterialId, out var dl)
                ? dl
                : new List<RoutingDependency>();

            // 按 (RouteCode, PathId) **逐路径**计算可达 Stage 后取**并集**（v1.6 + Q1：PathId 进图键、禁止跨 Path 连边）。
            // V1 装载层归一化为单路径（'DEFAULT'/1）⇒ 恰好一组，与旧实现（只取 'DEFAULT'）**行为等价**；
            // V2 多路径 ⇒ 逐路径各算再并集：只并**集合**、不跨路径连边（并集是保守口径，
            // 宁可判「该 Stage 在范围内」也不误报缺失，延续 P1-06 初衷）。
            foreach (var pathGroup in ops.GroupBy(op => RoutePathKey.Of(op.RouteCode, op.PathId)))
            {
                CollectReachableStagesForPath(
                    pathGroup,
                    depsForMaterial.Where(d => d.RouteCode == pathGroup.Key.RouteCode
                                            && d.PathId == pathGroup.Key.PathId),
                    demand,
                    stageSet);
            }
        }

        return result;

        // 单条 (RouteCode, PathId) 路径内的可达 Stage 收集（局部函数：复用外层实例方法 ResolveNodeKey）
        void CollectReachableStagesForPath(
            IEnumerable<RoutingOperation> pathOps,
            IEnumerable<RoutingDependency> pathDeps,
            LogicalProductionDemand demand,
            HashSet<string> stageSet)
        {
            // 工序节点集合（单条路径内）：键 = (StageCode, OperationCode)。
            // ⚠ 0号位 2026-09-29 裁决 §5.3：原实现是「工序码 → StageCode」单键 + First() 任取，
            //   同码跨 Stage 时会**判错整个可达 Stage 集合**（连带影响 P1-06 缺失判定与部门锁定范围），
            //   比单点覆盖更严重。此处升维。
            var nodes = pathOps
                .GroupBy(op => OperationNodeKey.Of(op.StageCode, op.OperationCode))
                .ToDictionary(g => g.Key, g => g.First());

            // 工序码 → 候选节点键（消解依赖端点 / 起点工序用；同码跨 Stage 时 >1 个候选）
            var nodesByOperationCode = new Dictionary<string, List<OperationNodeKey>>();
            foreach (var nodeKey in nodes.Keys)
            {
                if (!nodesByOperationCode.TryGetValue(nodeKey.OperationCode, out var keyList))
                {
                    keyList = new List<OperationNodeKey>();
                    nodesByOperationCode[nodeKey.OperationCode] = keyList;
                }

                keyList.Add(nodeKey);
            }

            // 依赖邻接表：From → List&lt;To&gt; + 入度（用于识别根工序）
            var adjacency = new Dictionary<OperationNodeKey, List<OperationNodeKey>>();
            var inDegree = new Dictionary<OperationNodeKey, int>();
            foreach (var nodeKey in nodes.Keys)
            {
                inDegree[nodeKey] = 0;
            }

            foreach (var dep in pathDeps)
            {
                var fromKey = nodesByOperationCode.TryGetValue(dep.FromOperationCode, out var fromCandidates)
                    ? ResolveNodeKey(fromCandidates, k => nodes[k].ProductionDepartmentId, dep.ProductionDepartmentId)
                    : null;

                var toKey = nodesByOperationCode.TryGetValue(dep.ToOperationCode, out var toCandidates)
                    ? ResolveNodeKey(toCandidates, k => nodes[k].ProductionDepartmentId, dep.ProductionDepartmentId)
                    : null;

                if (fromKey == null || toKey == null)
                {
                    continue; // 无法消解 ⇒ 不猜（丢边由 BuildRoutingGraphs 统一计数）
                }

                if (!adjacency.TryGetValue(fromKey.Value, out var tos))
                {
                    tos = new List<OperationNodeKey>();
                    adjacency[fromKey.Value] = tos;
                }
                tos.Add(toKey.Value);

                if (inDegree.ContainsKey(toKey.Value))
                {
                    inDegree[toKey.Value]++;
                }
            }

            // 起点工序集合
            var hasStartOperation = !string.IsNullOrWhiteSpace(demand.StartOperationCode);
            var hasStartStage = !string.IsNullOrWhiteSpace(demand.StartStageCode);
            var startOps = new List<OperationNodeKey>();

            if (hasStartOperation)
            {
                // 5号位 交付的工序级起点。同码跨 Stage 时须用 StartStageCode 收窄，否则会把
                // 另一 Stage 的同名工序也当作起点（旧实现按工序码单键命中，是同一根因的另一面）。
                if (nodesByOperationCode.TryGetValue(demand.StartOperationCode!, out var startCandidates))
                {
                    foreach (var candidate in startCandidates)
                    {
                        if (hasStartStage && candidate.StageCode != demand.StartStageCode)
                        {
                            continue;
                        }

                        startOps.Add(candidate);
                    }
                }
            }
            else
            {
                // 无工序级起点：新单从根工序起；若有 StartStageCode 则收窄到该 Stage 的根工序
                foreach (var nodeKey in nodes.Keys)
                {
                    if (inDegree[nodeKey] != 0)
                    {
                        continue; // 非根工序
                    }
                    if (hasStartStage && nodeKey.StageCode != demand.StartStageCode)
                    {
                        continue; // 不在起始 Stage
                    }
                    startOps.Add(nodeKey);
                }
            }

            // BFS 可达工序 → 收集 StageCode
            var visited = new HashSet<OperationNodeKey>();
            var queue = new Queue<OperationNodeKey>();
            foreach (var s in startOps)
            {
                if (visited.Add(s))
                {
                    queue.Enqueue(s);
                }
            }

            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                stageSet.Add(cur.StageCode); // 键即含 Stage，不再有「查不到」分支

                if (adjacency.TryGetValue(cur, out var tos))
                {
                    foreach (var to in tos)
                    {
                        if (visited.Add(to))
                        {
                            queue.Enqueue(to);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// N8-01：把 <see cref="DomainSolveRequest.StageSequenceChains"/> 收敛成「物料 → 去重后的 Stage 步」。
    /// 同 StageCode 取最小 StageSeq（DTO 契约本就如此，此处再收敛一次是防御，与
    /// <see cref="ValidateStageSequences"/> 同口径）。
    /// </summary>
    private static Dictionary<int, List<StageSequenceStep>> BuildStageChainIndex(DomainSolveRequest request)
    {
        var index = new Dictionary<int, List<StageSequenceStep>>();

        foreach (var chain in request.StageSequenceChains)
        {
            if (chain?.Stages == null || chain.Stages.Count == 0)
            {
                continue;
            }

            index[chain.MaterialId] = chain.Stages
                .Where(s => !string.IsNullOrWhiteSpace(s.StageCode))
                .GroupBy(s => s.StageCode, StringComparer.Ordinal)
                .Select(g => g.OrderBy(s => s.StageSeq).First())
                .ToList();
        }

        return index;
    }

    /// <summary>
    /// **R09-③（2026-10-29 落码）**：把每个需求「**中间**无 Routing Stage」的提前期**前置算好**，
    /// 挂到 <see cref="ConstraintContext.RoutelessLeadStepsByDemand"/>，供 Phase2 当硬时间约束。
    ///
    /// <para><b>缺陷是什么</b>：改造前 `StageLeadTimeFact` 的提前期**只在**
    /// `StageTimingNodeBuilder.Build` 里被累加，而 `Build` **只在 `PhaseFiveCompression.cs:470`
    /// 被调用一次**（排完 Phase2/3/4 之后）。⇒ 它是**事后描述**，Phase2 排程时压根不看它
    /// ⇒ 链上「有 Routing → 无 Routing → 有 Routing」中间那个 Stage **不占任何时间**，
    /// 下游工序被排得比工艺允许的更早。这就是 R09-③ 的原始缺陷。</para>
    ///
    /// <para><b>口径（不新造任何键域/原因码）</b>：</para>
    /// <list type="bullet">
    ///   <item>「无 Routing Stage」判定**复用** <see cref="StageTimingNodeBuilder.ResolveRoutelessStages"/> ——
    ///     与事后描述**同一函数**，两处口径不可能分叉。</item>
    ///   <item>提前期值**复用** <see cref="StageTimingNodeBuilder.ResolveLeadTime"/> ——
    ///     同样只认契约 4 值，缺记录仍判 `STAGE_LEADTIME_MISSING`，**不臆造 0 或 72h 时间**。</item>
    ///   <item>**逐 Stage 一片**（<see cref="RoutelessLeadLeg"/>），不先求总和 ——
    ///     提前期是**有效生产小时**，要在**该 Stage 自己部门**的 WORK 窗口内消耗（R09-① 口径）。</item>
    ///   <item>只登记**两端都是真实 Routing Stage** 的段（`From` / `To` 均非空）⇒ 恰好是「**中间**」。</item>
    /// </list>
    ///
    /// <para><b>为什么不登记链首/链尾的无 Routing Stage</b>：链尾部分本来就由
    /// `StageTimingNodeBuilder.BuildCore` 从末端 Task 往后累加（在 `:344` 按 `seq <= terminalSeq` 筛），
    /// 重复登记会**双计**；链首部分不在 R09-③「**中间**」的字面范围内，本次不扩。</para>
    /// </summary>
    private static void BuildRoutelessLeadSteps(DomainSolveRequest request, ConstraintContext context)
    {
        if (request.LogicalProductionDemands.Count == 0 || request.StageSequenceChains.Count == 0)
        {
            return;
        }

        var chainByMaterial = BuildStageChainIndex(request);

        var leadTimeByKey = new Dictionary<(int MaterialId, string StageCode), StageLeadTimeFact>();
        foreach (var fact in request.StageLeadTimes)
        {
            leadTimeByKey[(fact.MaterialId, fact.StageCode)] = fact;
        }

        foreach (var demand in request.LogicalProductionDemands)
        {
            if (string.IsNullOrEmpty(demand.LogicalDemandKey))
            {
                continue;
            }

            // 权威链（已按起点裁剪）：顺序就是工艺顺序，跨 Stage 段的「中间」由它定义。
            var chainStages = ResolveChainStagesInScope(request, demand, chainByMaterial);
            if (chainStages.Count == 0)
            {
                continue;
            }

            // ⚠ 路径口径用**需求自述**的 RouteCode/PathId：Phase1 还没选路（Phase2 才选），
            //   与 `ResolveRoutelessStages` 的「未确定 ⇒ 全部 Route 并集（候选口径）」一致。
            var routeless = StageTimingNodeBuilder.ResolveRoutelessStages(
                demand.MaterialId,
                context.EffectiveStages,
                request,
                demand.RouteCode,
                demand.PathId);

            if (routeless.Stages.Count == 0)
            {
                continue;
            }

            var routelessSet = new HashSet<string>(routeless.Stages, StringComparer.Ordinal);

            var steps = new List<ConstraintContext.RoutelessLeadStep>();
            string? prevRoutingStage = null;
            List<ConstraintContext.RoutelessLeadLeg>? run = null;

            foreach (var stageCode in chainStages)
            {
                if (routelessSet.Contains(stageCode))
                {
                    run ??= new List<ConstraintContext.RoutelessLeadLeg>();

                    var (hours, level) = StageTimingNodeBuilder.ResolveLeadTime(
                        leadTimeByKey, demand.MaterialId, stageCode);

                    run.Add(new ConstraintContext.RoutelessLeadLeg(
                        stageCode,
                        hours,
                        !string.Equals(level, StageTimingNodeBuilder.MissingLevel, StringComparison.Ordinal)));
                    continue;
                }

                // 碰到真实 Routing Stage：把「上一个 Routing Stage → 这一个」之间的无 Routing 段收口。
                if (run is { Count: > 0 } && prevRoutingStage is not null)
                {
                    steps.Add(new ConstraintContext.RoutelessLeadStep(prevRoutingStage, stageCode, run));
                }

                run = null;
                prevRoutingStage = stageCode;
            }

            // 循环结束时 run 若还非空 = 链尾那段（无 ToStageCode）⇒ 按上方注释**不登记**。

            if (steps.Count > 0)
            {
                context.RoutelessLeadStepsByDemand[demand.LogicalDemandKey] = steps;
            }
        }
    }

    /// <summary>
    /// N8-01：取「该需求在本次求解范围内」的 BOM/Stage 权威链 Stage 集合。
    ///
    /// 取源 = <see cref="DomainSolveRequest.StageSequenceChains"/>（2号位 按
    /// `APS_BOM_STAGE_PATH_RAW WHERE BatchNo = @BatchNo` 装载，已限定在本次 Run 的 BOM 批次内），
    /// **不是**全量历史 BOM StagePath。仍须按起点裁剪：续排（`StartOperationCode` / `StartStageCode`）
    /// 时起点之前的已完成 Stage 不属于本次求解范围 —— 否则会凭空多出提前期
    /// （见 <see cref="ConstraintContext.EffectiveStages"/> 注释里的禁令）。
    /// 无起点信息（新单从首工序起）⇒ 整条链都在范围内。
    /// </summary>
    private static List<string> ResolveChainStagesInScope(
        DomainSolveRequest request,
        LogicalProductionDemand demand,
        Dictionary<int, List<StageSequenceStep>> chainByMaterial)
    {
        if (!chainByMaterial.TryGetValue(demand.MaterialId, out var steps) || steps.Count == 0)
        {
            return new List<string>();
        }

        var startSeq = ResolveStartStageSeq(request, demand, steps);

        var stages = new List<string>();
        foreach (var step in steps)
        {
            if (startSeq.HasValue && step.StageSeq < startSeq.Value)
            {
                continue; // 起点之前已完成
            }

            stages.Add(step.StageCode);
        }

        return stages;
    }

    /// <summary>
    /// 起点 Stage 在链内的 StageSeq（裁剪下界）。
    ///
    /// 与 <c>CollectReachableStagesForPath</c> 的 BFS 起点**同口径**：
    ///   · 有 `StartOperationCode` ⇒ 用该工序所属 Stage（同码跨 Stage 时再用 `StartStageCode` 收窄）；
    ///   · 否则有 `StartStageCode` ⇒ 就用该 Stage；
    ///   · 都没有 ⇒ `null`（新单，整条链都在范围内）。
    /// 有起点信息但链内定位不到 ⇒ 返回 <see cref="int.MaxValue"/>（= 不放行任何 Stage），
    /// 与「BFS 起点集为空 ⇒ 可达集为空」保持一致，避免退化成「整条历史链无条件当范围」。
    /// </summary>
    private static int? ResolveStartStageSeq(
        DomainSolveRequest request,
        LogicalProductionDemand demand,
        List<StageSequenceStep> chainSteps)
    {
        var hasStartOperation = !string.IsNullOrWhiteSpace(demand.StartOperationCode);
        var hasStartStage = !string.IsNullOrWhiteSpace(demand.StartStageCode);

        if (!hasStartOperation && !hasStartStage)
        {
            return null;
        }

        string? boundaryStage;

        if (hasStartOperation)
        {
            var candidates = request.RoutingOperations
                .Where(op => op.MaterialId == demand.MaterialId
                          && string.Equals(op.OperationCode, demand.StartOperationCode, StringComparison.Ordinal)
                          && !string.IsNullOrWhiteSpace(op.StageCode))
                .Select(op => op.StageCode!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(c => c, StringComparer.Ordinal)
                .ToList();

            if (hasStartStage)
            {
                candidates = candidates
                    .Where(c => string.Equals(c, demand.StartStageCode, StringComparison.Ordinal))
                    .ToList();
            }

            boundaryStage = candidates.Count > 0 ? candidates[0] : null;
        }
        else
        {
            boundaryStage = demand.StartStageCode;
        }

        if (string.IsNullOrWhiteSpace(boundaryStage))
        {
            return int.MaxValue;
        }

        var seqs = chainSteps
            .Where(s => string.Equals(s.StageCode, boundaryStage, StringComparison.Ordinal))
            .Select(s => s.StageSeq)
            .ToList();

        return seqs.Count > 0 ? seqs.Min() : int.MaxValue;
    }

    /// <summary>
    /// 构建工序依赖图（使用部门锁定后的 Routing）
    /// </summary>
    private void BuildRoutingGraphs(
        List<RoutingOperation> routingOperations,
        List<RoutingDependency> routingDependencies,
        ConstraintContext context)
    {
        // ── 2026-11-02｜`P2_NO_ROUTING` 归因观测（纯观测）──
        //   `P2_NO_ROUTING` 的报文说「本次输入里一条 Routing 图都没有」，但**分不出两种责任方**：
        //     · 输入侧**根本没有**该物料的工序行（上游装载/过滤掉了）⇒ 上游；
        //     · 输入里**有行**，但图没建出来（全被去重/裁掉）⇒ **我方** `BuildRoutingGraphs`。
        //   把「输入里出现过该物料」这件事记下来，Phase2 出口处即可一次分清。不改任何判定。
        context.MaterialsWithRoutingInput = routingOperations.Select(op => op.MaterialId).ToHashSet();

        // 按 MaterialId 分组
        var operationsByMaterial = routingOperations
            .GroupBy(op => op.MaterialId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (materialId, operations) in operationsByMaterial)
        {
            // 按 (RouteCode, PathId) 再分组（v1.6 + Q1：PathId 必须进图键，禁止跨 Path 连边）
            var operationsByPath = operations
                .GroupBy(op => RoutePathKey.Of(op.RouteCode, op.PathId))
                .ToDictionary(g => g.Key, g => g.ToList());

            var routeGraphs = new Dictionary<RoutePathKey, RoutingGraph>();

            foreach (var (pathKey, routeOps) in operationsByPath)
            {
                var graph = new RoutingGraph();

                // 构建工序节点：键 = (StageCode, OperationCode)（0号位 2026-09-29 裁决 §5.2/§5.3）
                foreach (var op in routeOps)
                {
                    var nodeKey = OperationNodeKey.Of(op.StageCode, op.OperationCode);

                    if (graph.Operations.ContainsKey(nodeKey))
                    {
                        // 同 (StageCode, OperationCode) 重复行 = MES 数据异常。
                        // 旧实现是**静默覆盖**（后者覆盖前者 ⇒ 边被错接）；此处改为保留首行 + 计数，不静默。
                        context.DuplicateOperationNodeKeyCount++;
                        continue;
                    }

                    graph.Operations[nodeKey] = new OperationNode
                    {
                        OperationCode = op.OperationCode,
                        OperationName = op.OperationName,
                        ProcessType = op.ProcessType,
                        StageCode = op.StageCode,
                        // ── 2026-10-28｜R04-S-G-Units：秒 → 分钟，**只在本入口转一次** ──
                        //   0号位 2026-10-27《01_正式回复1号位_R04秒转分钟唯一装配…》§1-R04-S-G-Units
                        //   与 2026-10-28《00_0号位_3号位秒单位回执核证…》§一：批准方案C ——
                        //   MES CycletTime（秒）→ ODS 原样透传 → APS `RoutingOperation.StandardDuration` **保留秒**，
                        //   不改 ODS、不要求全量重灌；由 1号位 在**唯一 Operation 装配入口**（本行）÷60 成分钟，
                        //   之后所有 Phase 一律按分钟求解。
                        //   全仓 `new OperationNode` **只有这一处** ⇒ 所有下游消费点读的都是转换后的值
                        //   （Phase2 `:3792/:4058/:4405/:4699/:8493`、Phase4 `:1187/:1725`、
                        //     `ResourceEfficiencyEvaluator`、`SchedulingDirectionResolver` 的 Slack 估算）
                        //   ⇒ **任何下游都不得再除一次 60**，否则就重复转换。
                        //   ⚠ `Task.Duration` 对外小时合同**不因此改变**；本行只影响 Solver 内部时长。
                        //   ⚠ 同数据结构里的 `op.SetupTime` **不除**：冻结 DDL v5.1.2 明确
                        //     「SetupTime 准备时间 = 换型时间，单位：分钟」，未做秒改判 ⇒ 本次只转 StandardDuration。
                        //   ⚠ 契约 `RoutingOperation.cs:52` 那句「标准工时（分钟）」是**待改冻的旧注释**，不是现行口径。
                        StandardDuration = op.StandardDuration / 60m,
                        // item1 接线（阶段二）：不再拷贝 op.SetupTime——v1.2 §1.2/§20.3 废止其运行真相地位，
                        // Setup 一律走 SetupTransitionRule 规则查找（SetupExactRules/SetupDefaultRules）。
                        TransferBatchSize = op.TransferBatchSize,
                        OperationPlanningMode = op.OperationPlanningMode ?? "FINITE_RESOURCE",
                        RouteCode = op.RouteCode,
                        PathId = op.PathId,
                        ProductionDepartmentId = op.ProductionDepartmentId
                    };
                }

                // 构建依赖边
                // ⚠ 契约 RoutingDependency **无 StageCode 字段**（只有 From/ToOperationCode + 单值
                //   ProductionDepartmentId）⇒ 边的 Stage 归属必须从本图节点反查消解。消解不唯一时
                //   计数并丢弃该边，**不猜**（0号位 §5.3「禁止半升级」+ 不静默原则）。
                var dependencies = routingDependencies
                    .Where(dep => dep.MaterialId == materialId
                                  && dep.RouteCode == pathKey.RouteCode
                                  && dep.PathId == pathKey.PathId)
                    .ToList();

                // 工序码 → 候选节点键索引（消解依赖端点用；同码跨 Stage 时 >1 个候选）
                var nodesByOperationCode = new Dictionary<string, List<OperationNodeKey>>();
                foreach (var nodeKey in graph.Operations.Keys)
                {
                    if (!nodesByOperationCode.TryGetValue(nodeKey.OperationCode, out var keyList))
                    {
                        keyList = new List<OperationNodeKey>();
                        nodesByOperationCode[nodeKey.OperationCode] = keyList;
                    }

                    keyList.Add(nodeKey);
                }

                foreach (var dep in dependencies)
                {
                    var fromKey = nodesByOperationCode.TryGetValue(dep.FromOperationCode, out var fromCandidates)
                        ? ResolveNodeKey(fromCandidates, k => graph.Operations[k].ProductionDepartmentId, dep.ProductionDepartmentId)
                        : null;

                    var toKey = nodesByOperationCode.TryGetValue(dep.ToOperationCode, out var toCandidates)
                        ? ResolveNodeKey(toCandidates, k => graph.Operations[k].ProductionDepartmentId, dep.ProductionDepartmentId)
                        : null;

                    if (fromKey == null || toKey == null)
                    {
                        context.UnresolvedDependencyEdgeCount++;
                        continue;
                    }

                    if (!graph.Dependencies.TryGetValue(toKey.Value, out var preds))
                    {
                        preds = new List<DependencyEdge>();
                        graph.Dependencies[toKey.Value] = preds;
                    }

                    preds.Add(new DependencyEdge
                    {
                        From = fromKey.Value,
                        To = toKey.Value,
                        DependencyType = dep.DependencyType,
                        LagTime = dep.LagTime
                    });
                }

                // 识别根工序（无前驱的工序）
                var allToOps = graph.Dependencies.Keys.ToHashSet();
                graph.RootOperations = graph.Operations.Keys
                    .Where(nodeKey => !allToOps.Contains(nodeKey))
                    .ToList();

                routeGraphs[pathKey] = graph;
            }

            context.RoutingGraphs[materialId] = routeGraphs;
        }
    }

    /// <summary>
    /// 从候选节点中消解出唯一节点键：
    ///   ① 唯一 → 直接取（跨 Stage 边的常规情形）；
    ///   ② 多个（2号位 实测 117 物料同码跨 Stage）→ 用部门区分（部门是「物料×阶段」联合属性）；
    ///   ③ 仍不唯一 → 返回 null，调用方计数丢弃，**不猜**。
    /// 契约 <c>RoutingDependency</c> 无 StageCode 字段（只有单值 ProductionDepartmentId），
    /// 故同码跨 Stage 时只能靠部门消解 —— 这是该契约下能做到的最强口径。
    /// </summary>
    private static OperationNodeKey? ResolveNodeKey(
        IReadOnlyList<OperationNodeKey> candidates,
        Func<OperationNodeKey, int> departmentOf,
        int departmentId)
    {
        if (candidates.Count == 0)
        {
            return null; // 悬空：端点不在本上下文
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        OperationNodeKey? byDepartment = null;
        foreach (var key in candidates)
        {
            if (departmentOf(key) != departmentId)
            {
                continue;
            }

            if (byDepartment != null)
            {
                return null; // 部门也不唯一 ⇒ 歧义，不猜
            }

            byDepartment = key;
        }

        return byDepartment; // null = 无法消解
    }

    /// <summary>
    /// 构建跨物料依赖 DAG（任务喂任务，方案A）：子先父后拓扑分层
    /// 文档：0号位 2026-09-10 裁决 方案A —— 跨物料时序作为硬约束在求解内实现
    ///
    /// 消费 request.MaterialRequirementLinks（父 ConsumerLogicalDemandKey → 子 ProducerLogicalDemandKey）
    /// 产出：
    ///   - context.CrossMaterialOrder：按「子层先、父层后」拍平的有序 DemandKey 列表（层内按 DemandSequence）
    ///   - context.CrossMaterialLayers：分层结构（每层一个 DemandKey 列表），供 Phase2 层内排序
    ///   - context.CrossMaterialHasCycle：是否检测到 BOM 依赖环（技术失败）
    /// 无 link（如子件全库存/全PI，不产 link）时：CrossMaterialOrder 保持原 DemandSequence 平铺顺序，零影响。
    /// </summary>
    private void BuildCrossMaterialDag(DomainSolveRequest request, ConstraintContext context)
    {
        var links = request.MaterialRequirementLinks;
        if (links == null || links.Count == 0)
        {
            // 无跨物料依赖：直接按 DemandSequence 平铺（等价于旧行为）
            context.CrossMaterialOrder = request.LogicalProductionDemands
                .OrderBy(d => d.DemandSequence)
                .Select(d => d.LogicalDemandKey)
                .ToList();
            context.CrossMaterialLayers = new List<List<string>> { context.CrossMaterialOrder };
            return;
        }

        // 参与拓扑的节点 = 所有 demand 的 LogicalDemandKey
        var allDemandKeys = request.LogicalProductionDemands
            .Select(d => d.LogicalDemandKey)
            .ToHashSet();

        // 邻接表：child → list<parent>（子先排，父后排）
        var childrenToParents = new Dictionary<string, List<string>>();
        var parentToChildren = new Dictionary<string, List<string>>();
        var inDegree = new Dictionary<string, int>();
        foreach (var key in allDemandKeys)
        {
            inDegree[key] = 0;
        }

        foreach (var link in links)
        {
            // 0910 §二十 Case B：子件全库存 / 采购占位 → ProducerLogicalDemandKey = null
            //（2号位 20260924 方案 (a) 落码：link 仍产，但无子件生产身份）。
            // null 不进 DAG（不参与跨物料时序）；link 本身仍保留「父需求→子需求」真相。
            // 显式判空（而非依赖 Contains(null)==false）：语义更清楚，且消除 CS8604。
            var child = link.ProducerLogicalDemandKey;
            if (child is null)
            {
                continue;
            }

            // 只处理两端节点都在当前 Demand 集合内的 link（防御：忽略脏数据/域外引用）
            if (!allDemandKeys.Contains(child) ||
                !allDemandKeys.Contains(link.ConsumerLogicalDemandKey))
            {
                continue;
            }

            var parent = link.ConsumerLogicalDemandKey;

            if (!childrenToParents.TryGetValue(child, out var parents))
            {
                parents = new List<string>();
                childrenToParents[child] = parents;
            }
            parents.Add(parent);

            if (!parentToChildren.TryGetValue(parent, out var children))
            {
                children = new List<string>();
                parentToChildren[parent] = children;
            }
            children.Add(child);

            // P1-12：记录 父→子 边界的滞后时间（分钟），供 GetDynamicMaterialFloor 累加。
            context.CrossMaterialLagMinutes[(parent, child)] = link.LagMinutes;

            // 父的入度 = 它依赖的子件数
            inDegree[parent]++;
        }

        context.CrossMaterialParentToChildren = parentToChildren;
        // 2026-10-28：反向索引落库（合批守卫用，见 ConstraintContext.CrossMaterialChildToParents）。
        context.CrossMaterialChildToParents = childrenToParents;

        // 层内按 DemandSequence 排序（保持 2号位 业务优先级）——索引只建一次
        var demandByKey = request.LogicalProductionDemands
            .ToDictionary(d => d.LogicalDemandKey);

        // Kahn 拓扑排序：入度为 0 的（不依赖任何子件的）先入队
        var queue = new Queue<string>();
        foreach (var key in allDemandKeys)
        {
            if (inDegree[key] == 0)
            {
                queue.Enqueue(key);
            }
        }

        var layers = new List<List<string>>();
        var ordered = new List<string>();
        var visited = 0;

        while (queue.Count > 0)
        {
            var layer = new List<string>(queue.Count);
            var nextLayer = new List<string>();

            foreach (var node in queue)
            {
                layer.Add(node);
                ordered.Add(node);
                visited++;

                if (childrenToParents.TryGetValue(node, out var parents))
                {
                    foreach (var parent in parents)
                    {
                        inDegree[parent]--;
                        if (inDegree[parent] == 0)
                        {
                            nextLayer.Add(parent);
                        }
                    }
                }
            }

            layer.Sort((a, b) => DemandSeq(a).CompareTo(DemandSeq(b)));
            nextLayer.Sort((a, b) => DemandSeq(a).CompareTo(DemandSeq(b)));

            layers.Add(layer);
            queue = new Queue<string>(nextLayer);
        }

        int DemandSeq(string key)
            => demandByKey.TryGetValue(key, out var d) ? d.DemandSequence : int.MaxValue;

        // 环检测：visited < 总节点数 → 存在环
        if (visited < allDemandKeys.Count)
        {
            context.CrossMaterialHasCycle = true;
            context.CrossMaterialOrder = ordered;
            context.CrossMaterialLayers = layers;
            return;
        }

        context.CrossMaterialOrder = ordered;
        context.CrossMaterialLayers = layers;
    }

    /// <summary>
    /// 构建工序资源资格映射（使用部门锁定后的 Eligibility）
    /// </summary>
    private void BuildOperationResourceEligibility(
        List<OperationResourceEligibility> eligibilities,
        ConstraintContext context)
    {
        // 按 EligibilityLookupKey(MaterialId, ProductionDepartmentId, RouteCode, PathId, OperationCode) → ResourceId 列表（按 Priority 排序）
        // P0-01修复：使用冻结接口 OperationResourceEligibility，不再使用旧的 ResourceEligibility
        // 第4轮C1修复：索引加入MaterialId，避免不同物料共享资源资格
        // 0号位 2026-09-29 §5.3 落实：**补上 ProductionDepartmentId** —— 上游 lockedEligibilities 已按
        //   四元组（含部门）过滤，故此处以同一四元组分组才是自洽口径；旧三元键会把两个部门的同名工序
        //   资格合并成一份（跨部门串资源）。契约无 StageCode 字段，无法再细到 Stage（残留已在键类型上注明）。
        var eligibilityGroups = eligibilities
            .GroupBy(e => new EligibilityLookupKey(e.MaterialId, e.ProductionDepartmentId, e.RouteCode, e.PathId, e.OperationCode))
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(e => e.Priority)
                      .Select(e => e.ResourceId)
                      .ToList()
            );

        context.OperationResourceEligibility = eligibilityGroups;

        // P0-04修复：同时构建 ResourceCapacityFactors 映射
        // 第4轮C1修复：索引加入MaterialId；本次再补部门（同上）
        // (EligibilityLookupKey, ResourceId) → CapacityFactor
        var capacityFactors = eligibilities
            .GroupBy(e => new EligibilityLookupKey(e.MaterialId, e.ProductionDepartmentId, e.RouteCode, e.PathId, e.OperationCode))
            .ToDictionary(
                g => g.Key,
                g => g.ToDictionary(
                    e => e.ResourceId,
                    e => e.CapacityFactor
                )
            );

        context.ResourceCapacityFactors = capacityFactors;
    }

    /// <summary>
    /// 构建资源编码映射（ResourceId → ResourceCode）
    /// P1-08修复：供 Phase2/Phase4 生成 FinalTaskDraft 时回填 ResourceCode。
    /// </summary>
    private void BuildResourceCodes(DomainSolveRequest request, ConstraintContext context)
    {
        context.ResourceCodes = request.Resources
            .GroupBy(r => r.ResourceId)
            .ToDictionary(g => g.Key, g => g.First().ResourceCode);

        // P1-02（BottleneckMode 锚点）：反向映射 Code → ResourceId，供 Phase3 消费 AnchorResourceCode 反查。
        // 空编码（string.Empty）跳过；编码重复时取首个 ResourceId（业务编码应唯一，防御 GroupBy）。
        context.ResourceIdsByCode = request.Resources
            .Where(r => !string.IsNullOrEmpty(r.ResourceCode))
            .GroupBy(r => r.ResourceCode)
            .ToDictionary(g => g.Key, g => g.First().ResourceId);

        // ── R06 动态瓶颈（0号位 2026-10-28《01_正式回复1号位_R04秒转分钟唯一装配_WAIT_ONLY_AUTO与R06本地先修…》§1-R06）──
        //   0号位 原文：「Phase3 仅诊断不等于瓶颈策略进入 Phase1/2/4。先用你已有策略、Operation 资源资格、
        //   Calendar、排程约束，证明四个 Mode 如何影响合法选择。」
        //   本段只做**取值**，不改任何合法性：三个值全部来自既有输入（冻结策略字段 + 已建好的资格表），
        //   真正起作用的地方是 `PhaseTwoInitialScheduler.OrderResourcesByPreference`（Phase2 正排/倒排 + Phase4 修
        //   三处共用）的**候选顺序**，因此**不会**让任何原本不合法的排程变合法，反之亦然。
        var solverStrategy = request.StrategySnapshot.SolverStrategy;
        context.BottleneckMode = solverStrategy.BottleneckMode;

        // 冻结字段口径：「Auto 忽略锚点编码；空 = 未指定 ⇒ 等价 Auto」。
        //   故 Auto 下**不必**反查，锚点恒为 null；其余三个 Mode 才反查（反查不到 ⇒ null ⇒ 排序降级为不干预）。
        if (solverStrategy.BottleneckMode != DynamicBottleneckMode.Auto
            && !string.IsNullOrEmpty(solverStrategy.AnchorResourceCode)
            && context.ResourceIdsByCode.TryGetValue(solverStrategy.AnchorResourceCode!, out var anchorResourceId))
        {
            context.BottleneckAnchorResourceId = anchorResourceId;
        }

        // 「有负荷」静态代理：谁在**任一条**需求的合格资源表里出现过 ⇒ 认为排程后可能有占用。
        //   ⚠ 不含需求量级（本判据只回答「有没有」，量级大小在这里没有判据地位）。
        context.ResourcesWithStaticLoad.Clear();
        foreach (var eligible in context.OperationResourceEligibility.Values)
        {
            foreach (var resourceId in eligible)
            {
                context.ResourcesWithStaticLoad.Add(resourceId);
            }
        }
    }

    /// <summary>
    /// 构建资源日历（只保留可用时间窗）
    /// </summary>
    private void BuildResourceCalendars(DomainSolveRequest request, ConstraintContext context)
    {
        var calendarsByResource = request.CalendarSlots
            .Where(slot => slot.IsAvailable)  // 只保留可用时间窗
            .GroupBy(slot => slot.ResourceId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(slot => slot.Start)
                      .Select(slot => new TimeWindow(slot.Start, slot.End))
                      .ToList()
            );

        context.ResourceCalendars = calendarsByResource;
    }

    /// <summary>
    /// 构建物料多段可用性
    /// </summary>
    private void BuildMaterialAvailability(DomainSolveRequest request, ConstraintContext context)
    {
        var availabilityByAllocation = request.MaterialConstraints
            .GroupBy(m => m.AllocationSequence)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(m => m.AvailableTime)
                      .Select(m => new MaterialAvailabilitySegment
                      {
                          Quantity = m.Quantity,
                          AvailableTime = m.AvailableTime,
                          SourceType = m.SourceType,
                          SourceKey = m.SourceKey
                      })
                      .ToList()
            );

        context.MaterialAvailability = availabilityByAllocation;
    }

    /// <summary>
    /// 构建锁定任务约束
    /// 第8轮P0-01修复：完整传递ExecutionConstraint的StageCode/OperationCode/LockedQuantity/TaskKey
    /// </summary>
    private void BuildLockedTasks(DomainSolveRequest request, ConstraintContext context)
    {
        // P1-07：复合键。同一 LogicalDemand 多操作锚点（多 ExecutionConstraint）不再因重复 DraftId 抛异常；
        //   StageCode / OperationCode 归一化为 string.Empty（2号位 口径：恒非空）。
        // P0-02（0号位 2026-10-09《APS_V1_2_20261009.md》§三）：键域**必须含 StageCode** ——
        //   已生效工序身份规则要求 StageCode 与 OperationCode **共同**定位工序，不得假设 OperationCode 全局唯一。
        //   并改**显式循环 + 重复检测**（`ToDictionary` 的重复 Key 抛的是未受控 ArgumentException）：
        //   同一三元键出现两条 ⇒ 输入契约无法唯一标识锚点 ⇒ **不得静默抹除锚点**，须受控 Fail Closed
        //   （抛 `SolverInputContractException`，由 `FiniteCapacitySolver.SolveAsync` 捕获）。
        var lockedTasks = new Dictionary<(string DraftId, string StageCode, string OperationCode), LockedTaskConstraint>();
        foreach (var ec in request.ExecutionConstraints)
        {
            var lockedKey = (ec.DraftId, ec.StageCode ?? string.Empty, ec.OperationCode ?? string.Empty);
            if (lockedTasks.ContainsKey(lockedKey))
            {
                throw new SolverInputContractException(
                    "锁定约束键域冲突：同一 (DraftId, StageCode, OperationCode) 出现多条 ExecutionConstraint ⇒ " +
                    $"无法唯一标识锚点，不得静默抹除。DraftId={ec.DraftId}, " +
                    $"StageCode={ec.StageCode ?? "(null)"}, OperationCode={ec.OperationCode ?? "(null)"}");
            }

            lockedTasks[lockedKey] = new LockedTaskConstraint
            {
                DraftId = ec.DraftId,
                ResourceId = ec.ResourceId,
                LockedStart = ec.LockedStart,
                LockedEnd = ec.LockedEnd,
                ConstraintType = ec.ConstraintType,
                StageCode = ec.StageCode,
                OperationCode = ec.OperationCode,
                LockedQuantity = ec.LockedQuantity,
                LockedNetOutputQty = ec.LockedNetOutputQty,
                LockedPlannedProcessQty = ec.LockedPlannedProcessQty,
                TaskKey = ec.TaskKey
            };
        }

        context.LockedTasks = lockedTasks;
    }

    /// <summary>
    /// 构建共享资源占用块
    /// </summary>
    private void BuildResourceBlocks(DomainSolveRequest request, ConstraintContext context)
    {
        // Candidate §11：其它 Domain 当前 ACTIVE 共享资源占用（外部不可移动阻挡块）
        var blocks = new List<ResourceBlock>();
        if (request.CandidateContext?.ExternalDomainResourceBlocks != null)
            blocks.AddRange(request.CandidateContext.ExternalDomainResourceBlocks);

        // FULL §9：前序 Domain 成功后的共享 Resource 占用块（与 Candidate 外部块同语义：不可用时间窗）
        if (request.UpstreamDomainResourceBlocks != null)
            blocks.AddRange(request.UpstreamDomainResourceBlocks);

        if (blocks.Count == 0)
        {
            return;
        }

        var blocksByResource = blocks
            .GroupBy(block => block.ResourceId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(block => new ResourceBlockInfo
                {
                    ResourceId = block.ResourceId,
                    StartTime = block.StartTime,
                    EndTime = block.EndTime,
                    Reason = block.Reason
                })
                .ToList()
            );

        context.ResourceBlocks = blocksByResource;
    }

    /// <summary>
    /// P1-02 item1 接线（阶段一）：装载第⑦块冻结 Setup 换型规则 → 运行时 EXACT/DEFAULT 查找字典。
    /// 数据链路（0号位 裁决项2）：RuleSetVersion 已发布规则 → 本 Run 一次性 FrozenStrategySnapshot.SetupTransitionRules
    /// → 2号位 按 Domain（Dept+Stage）裁剪投影进 SolverStrategySnapshot（r13392）→ 此处经
    /// SetupOptimizer.BuildRuleLookups 适配（EXACT/DEFAULT 按 RuleType 分流、无效行防御跳过、同键取第一条不随机）。
    /// Solver 运行中不查 3号位 规则库；规则为空 = 无规则（三层命中按 0 分钟 + 追踪兜底）。
    /// </summary>
    private void BuildSetupTransitionRules(DomainSolveRequest request, ConstraintContext context)
    {
        var (exact, defaults, report) = SetupOptimizer.BuildRuleLookups(request.StrategySnapshot?.SetupTransitionRules);
        context.SetupExactRules = exact;
        context.SetupDefaultRules = defaults;
        context.SetupRuleLoadReport = report;

        // 0号位 R07R08 v1.1 第 3 条「禁止同 Run 静默首条覆盖」：同键异值不是静默吞掉，而是
        //   该键整条剔除（解析落到下一层，0 分钟 + WARNING 可见）+ 在此留 WARNING 追踪。
        //   同键同值 / 无法识别的行只计数，不逐行产 trace（控体积）。
        if (report.ExactConflictKeyCount > 0 || report.DefaultConflictKeyCount > 0)
        {
            context.TraceNotes.Add(new SolveTraceNote
            {
                Key = "SetupTransitionRules",
                ReasonCode = "SETUP_RULE_KEY_CONFLICT",
                Message = $"Setup 规则同键异值：EXACT {report.ExactConflictKeyCount} 个键 / DEFAULT {report.DefaultConflictKeyCount} 个键"
                          + " ⇒ 该键**整条剔除**（不取首条、不随机选），本次解析按下一层兜底并留 WARNING。留样："
                          + string.Join("；", report.ConflictExamples),
                Level = "Warning"
            });
        }

        if (report.UnusableRowCount > 0)
        {
            context.TraceNotes.Add(new SolveTraceNote
            {
                Key = "SetupTransitionRules",
                ReasonCode = "SETUP_RULE_UNUSABLE_ROWS",
                Message = $"Setup 规则中 {report.UnusableRowCount} 行结构无效（EXACT 缺前后产品 / 未知 RuleType），已跳过并计数。",
                Level = "Warning"
            });
        }
    }

    /// <summary>
    /// P0-01 接线：装载第⑧块冻结 Batch Policy 规则 → <see cref="ConstraintContext.ExecutionBatchPolicies"/>。
    ///
    /// 数据链路：`TaskSplitRuleConfig`（3号位 治理）→ 本 Run 一次性 `FrozenStrategySnapshot.BatchPolicies`
    ///   → 2号位 投影进 `SolverStrategySnapshot.BatchPolicies`（1↔2 契约，0号位 (7).md §十）
    ///   → 此处**整块逐字收进** `ConstraintContext`（本层**不裁剪**；载荷的**物料轴**裁剪由 2号位 在**上游**完成，
    ///     本层只做保真搬运、不按 Domain 塌成单值）
    ///   → `PhaseTwoInitialScheduler.ResolveExecutionBatchPolicy` 按 `(MaterialId, Dept)` **逐需求**解析。
    ///
    /// **不得**在本层做「同键合并 / 取第一条」等裁剪：键域语义（**仅 `(MaterialId, 明确 ProductionDepartmentId)`
    ///   精确命中**；⚠ AUD-1-004 起**已删除** Material 级默认回落）由解析侧 `ResolveExecutionBatchPolicy`
    ///   统一负责，本层只做**保真搬运**。
    /// 空（`StrategySnapshot` 为 null 或 ⑧块为空）⇒ 策略集为空 ⇒ **C 桶需求一律判 `BATCH_POLICY_MISSING`
    ///   并 Fail Closed**（P0-01 起；**不再**是「每需求恒 1 批」）。
    /// </summary>
    private static void BuildExecutionBatchPolicies(DomainSolveRequest request, ConstraintContext context)
    {
        var policies = request.StrategySnapshot?.BatchPolicies;
        if (policies is null || policies.Count == 0)
        {
            context.ExecutionBatchPolicies = new List<LPS.APS.Core.Dto.BatchPolicyRuleSnapshot>();
            return;
        }

        context.ExecutionBatchPolicies = new List<LPS.APS.Core.Dto.BatchPolicyRuleSnapshot>(policies);
    }

    /// <summary>
    /// M5 第一批：装载 RunScope 投影（Run 级交期覆盖 + Task 软目标）成内存词典。
    /// 键体系已由 2号位 转为内存键（DueDateOverrides=LogicalDemandKey；TaskTargetOverrides=(DraftId, OperationCode)）。
    /// null/空 RunScope = FULL 语义 → 两词典均空，现有行为零改变（向后兼容）。
    /// </summary>
    private void BuildRunScope(DomainSolveRequest request, ConstraintContext context)
    {
        var scope = request.RunScope;
        if (scope == null) return;

        foreach (var o in scope.DueDateOverrides)
        {
            if (!string.IsNullOrEmpty(o.LogicalDemandKey))
                context.DueDateOverrides[o.LogicalDemandKey] = o.ManualTargetDueDate;
        }

        foreach (var t in scope.TaskTargetOverrides)
        {
            if (!string.IsNullOrEmpty(t.DraftId) && !string.IsNullOrEmpty(t.OperationCode))
                context.TaskTargetOverrides[(t.DraftId, t.OperationCode)] = t.TargetTime;
        }
    }
}

/// <summary>
/// 硬约束上下文（Phase 1 输出）
/// </summary>
internal class ConstraintContext
{
    /// <summary>
    /// **PV195 跨日历窗找槽的启用开关（2026-10-29）**。默认 <c>false</c>。
    ///
    /// <para><b>为什么要有这个开关（而不是让找槽方法自己判断）</b>：跨窗会改写「一个 Task 必须整体落在某个
    /// 日历窗内」这条既有语义。实测代价（14:18 全量 Unit）：在 Phase2 常态找槽里无条件开跨窗后，6 个既有用例变红 ——
    /// `P0_01`（90 分钟跨两个 60 分钟窗）本该「Phase2 排不下 → Phase4 拆 2×45」，
    /// 被跨窗一次整段排下变成 1 个 Task；合批可行性、批数选择、批血缘同源变形。
    /// 结论：**能排下的东西一律不许被跨窗改写**。</para>
    ///
    /// <para><b>唯一置 true 的地方</b>：`PhaseFourLocalRepair.RepairUnscheduledDemands` 里，
    /// 一个执行批经**常规修复一个 Task 都没排出**之后的重试（`try/finally` 内临时置 true，出块即还原）。
    /// 也就是说，跨窗只救「今天出来 0 个 Task」的那些需求，其余结果逐位不变。</para>
    /// </summary>
    internal bool AllowCrossWindowSlot { get; set; }

    /// <summary>
    /// 工序依赖图：MaterialId → <see cref="RoutePathKey"/>(RouteCode, PathId) → 工序依赖关系。
    /// 键升维依据见 <see cref="RoutePathKey"/>（v1.6 新增要求 + Q1「禁止跨 Path 连边」）。
    /// 旧键为 RouteCode 单键 ⇒ V2 多路径时把互斥备选路径并成一张图（跨 Path 连边）。
    /// </summary>
    public Dictionary<int, Dictionary<RoutePathKey, RoutingGraph>> RoutingGraphs { get; set; } = new();

    /// <summary>
    /// V1 单路径解析：取该物料**唯一**一条 (RouteCode, PathId) 的 Routing 图。
    /// ⚠ **2026-10-07 起口径收窄**：`LogicalProductionDemand` 已补 `RouteCode`/`PathId` 契约字段
    ///   （v1.6 §新增/替换实施要求 + 0号位 裁决 §六 授权落 Core），需求**已能自述走哪条路径**
    ///   ⇒ 常规调用应改用 <see cref="TryGetRoutingGraph"/> / <see cref="TryGetDemandRoutingGraph"/>。
    ///   本方法**仅保留两处兜底**（均要求「唯一」才命中，**任何情况下都不猜**，与 Q-3 红线不冲突）：
    ///     · 需求**未**声明固定路径、且该物料在本请求内**只有一条**路径时直接取用；
    ///     · 锁定任务继承时的路径身份反查（需求无固定路径时）。
    ///   · 0 条 → false（无 Routing，调用方按既有语义处理）；
    ///   · 1 条 → 命中（与升维前 <c>TryGetValue("DEFAULT")</c> 行为等价）；
    ///   · &gt;1 条 → **false 且登记**（多路径必须由需求固定路径或 C 桶候选择优定夺，
    ///     此处**不猜**、不取 First()，与全仓「不静默」原则一致）。
    /// </summary>
    public bool TryGetSingleRoutingGraph(int materialId, [NotNullWhen(true)] out RoutingGraph? graph)
    {
        graph = null;
        if (!RoutingGraphs.TryGetValue(materialId, out var byPath) || byPath.Count == 0)
        {
            return false;
        }

        if (byPath.Count == 1)
        {
            graph = byPath.Values.First();
            return true;
        }

        AmbiguousRoutingPathMaterialIds.Add(materialId);
        return false;
    }

    /// <summary>
    /// 【Path-aware 解析（0号位 2026-10-07 裁决 Q-3，**必须整改**）】按**给定的** (RouteCode, PathId)
    /// 精确取图，不再假设「一物料一图」。
    ///
    /// 任务级调用点一律用**任务自身**的 (RouteCode, PathId) 调用本方法：已有 FinalTask 却按 MaterialId
    /// 取「物料唯一图」是旧的单路径假设，多 Path 下会**串 Path**（拿另一条备选路径的前驱/后继边接本任务工序）。
    ///
    /// **Fail Closed（0号位 2026-10-07 裁决 §三 新增红线）**：routeCode / pathId 任一缺失 ⇒ 返回 false，
    ///   **不得**退回 <see cref="TryGetSingleRoutingGraph"/> 按物料猜唯一 Path，也不得跨 Path 找替代节点。
    ///   调用方按既有语义处理（跳过该项判定 / 该需求 Unscheduled）。
    /// </summary>
    public bool TryGetRoutingGraph(
        int materialId,
        string? routeCode,
        long? pathId,
        [NotNullWhen(true)] out RoutingGraph? graph)
    {
        graph = null;

        // Fail Closed：缺路径身份不猜（空串不是合法 RouteCode）。
        // pathId 用 long? 承载（FinalTaskDraft.PathId 为 long?），越 int 域视为非法身份 ⇒ 同样不猜。
        if (string.IsNullOrEmpty(routeCode) || pathId is null
            || pathId.Value < int.MinValue || pathId.Value > int.MaxValue)
        {
            return false;
        }

        if (!RoutingGraphs.TryGetValue(materialId, out var byPath) || byPath.Count == 0)
        {
            return false;
        }

        return byPath.TryGetValue(RoutePathKey.Of(routeCode, (int)pathId.Value), out graph);
    }

    /// <summary>
    /// 取该物料在本次请求内的**全部**候选路径（(RouteCode, PathId) → 图），按 (RouteCode, PathId) 升序 ——
    /// 顺序确定、可重放（C桶候选内择优要求确定性）。无路径 ⇒ false。
    /// </summary>
    public bool TryGetRoutingGraphs(
        int materialId,
        [NotNullWhen(true)] out List<KeyValuePair<RoutePathKey, RoutingGraph>>? candidates)
    {
        candidates = null;
        if (!RoutingGraphs.TryGetValue(materialId, out var byPath) || byPath.Count == 0)
        {
            return false;
        }

        candidates = byPath
            .OrderBy(kv => kv.Key.RouteCode, StringComparer.Ordinal)
            .ThenBy(kv => kv.Key.PathId)
            .ToList();
        return true;
    }

    /// <summary>
    /// 需求 → **已选中路径** 登记表（Scheduling 内部，不动 Core）。
    ///
    /// Phase2 需求级选路后登记；Phase4 局部修复 / Phase5 各需求级消费点复用。
    /// 依据 RT-002「A/B 固定真实 RouteCode + PathId，**局部修复不得换路径**」——
    /// 局部修复**不得重选路径**，只能复用 Phase2 的选中结果。
    /// 未登记（单 Path 退化 / 需求自带固定路径）⇒ 调用方回落需求自身 RouteCode/PathId 或唯一那条。
    /// </summary>
    public Dictionary<string, RoutePathKey> ChosenRoutePaths { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// **批键 → 本批选中路径键**（P0-03 升维，0号位 (7).md §六/§七）。
    ///
    /// 为什么必须升维：`ChosenRoutePaths` 是「**需求** → 单一路径键」的单值表，**无法承载**多批各自选路
    ///   （`D1 → Batch-001 走 RouteA`、`Batch-002 走 RouteB` 完全合法，RT-003/RT-004）。
    ///   本表按**执行批**登记，Phase4 局部修复据此**逐批**取图 —— 不串批、不重选路径（RT-002）。
    ///
    /// 登记时机：Phase2 步骤 4 落定后**每批**登记（单批亦登记，与 `ChosenRoutePaths` 同值 ⇒ 行为零回归）。
    /// 未登记（该批未及落定）⇒ 调用方回落需求固定路径 → 唯一那条（同 <see cref="TryGetDemandRoutingGraph"/>）。
    /// </summary>
    public Dictionary<string, RoutePathKey> ChosenBatchRoutePaths { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// **需求 → 本 Run 该需求的执行批清单（批键 + 本批数量）**（P0-03）。
    ///
    /// Phase4 局部修复的基本单位 = **执行批**（不是 `LogicalProductionDemand`）：据此**逐批**重建，
    ///   **不得**用整份需求数量重建，**不得**把 Batch-002 的修复结果写回 Batch-001 的键。
    /// Phase2 `FormExecutionBatches` 产出后**在需求循环开头即登记**（先于试排/择优/落定）。
    /// 空（未登记）⇒ 该需求按**单批**回落（键 = `EB|{需求键}|001`，数量 = 需求数量）—— 与升维前逐字一致。
    /// </summary>
    public Dictionary<string, List<ExecutionBatchPlanEntry>> ExecutionBatchPlans { get; set; }
        = new(StringComparer.Ordinal);

    /// <summary>
    /// 需求 → **本 Run 该需求的全部执行批归批键**（Scheduling 内部，不动 Core）。
    ///
    /// Phase2 `FormExecutionBatches` 产出后**在需求循环开头即登记**（先于试排/择优/落定），
    /// Phase4 **逐批**修复（`PhaseFourLocalRepair.ExpandRepairUnits` 读本表 + <see cref="ExecutionBatchPlans"/>）
    /// 按**本批键**重建 —— 保证「局部修复不得把同一执行批劈成两个键」，
    /// 且不得把 Batch-002 写回 Batch-001 的键（P0-03；原 `ResolveExecutionBatchKeyForRebuild` 已删除）。
    /// </summary>
    public Dictionary<string, List<string>> ExecutionBatchDraftKeys { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 需求 → **本 Run 该需求自决后的排程方向**（`LogicalDemandKey` → `FORWARD`/`BACKWARD`/`MIXED`）。
    ///
    /// Phase2 在**本需求进入批/路由择优之前**用 `SchedulingDirectionResolver` 解析一次并登记
    ///   （P1-01 整改：同一 ResolvedDirection 贯穿 试排/候选比较/落定）；Phase5 的空隙压实与
    ///   Setup 序列优化据此判定「本需求是否 FORWARD」，**不再使用 Run 级原始策略值**。
    ///
    /// ⚠ 为什么必须登记而不是在 Phase5 重算：Phase2 的解析输入含**本需求的候选工序集**与
    ///   `dynamicMaterialFloor`（试排上下文）；Phase5 已无该上下文 ⇒ 重算会**漂移**。登记 = 一次解析、全链一致。
    ///
    /// Run 级 `FORWARD`/`BACKWARD` ⇒ 全量同向（Phase2 不解析、直接取原值）⇒ Phase5 行为与整改前**逐字一致**；
    /// Run 级 `AUTO` ⇒ 逐需求方向不同 ⇒ Phase5 得以按需求粒度门控（整改前 Phase5 见到 `AUTO` 恒跳过两项优化）。
    /// 未登记（旁路/早期 continue 的需求）⇒ 消费侧回落 Run 级原值（保守，与整改前一致）。
    /// </summary>
    public Dictionary<string, string> ResolvedDirections { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// **执行批键 → 该批实际落定所用方向**（NEW-P1-01，0号位 2026-10-09《APS_V1_3_20261009.md》§三）。
    ///
    /// 【为什么单靠 <see cref="ResolvedDirections"/> 不够】
    ///   冻结模型是「**每个 Execution Batch 独立**做 Direction + RoutingCandidate + Resource + Calendar + Setup
    ///   联合求解」⇒ 同一 `LogicalDemand` 的不同执行批**可以各自落定到不同方向**（例如 Batch-001 选中长 Lead 的
    ///   Path 自决 BACKWARD，Batch-002 选中短 Lead 的 Path 自决 FORWARD）。
    ///   而 <see cref="ResolvedDirections"/> 是**需求级单值**，只能承载「最后一个落定批」的方向
    ///   ⇒ 用它驱动 Phase5 会**把某一批的方向套到全需求**（复审判词：「多批方向被需求级单值覆盖」）。
    ///
    /// 【消费口径】Phase5 先按 **Task 自己的 `ExecutionBatchDraftKey`** 查本表；未命中才回落
    ///   <see cref="ResolvedDirections"/>（需求级，兼容旁路/单批），再回落 Run 级原值。
    ///   回调顺序保证**零回归**：Run 级 `FORWARD`/`BACKWARD` ⇒ 两级登记的均是该原值；
    ///   单批需求 ⇒ 批级与需求级同值。
    /// </summary>
    public Dictionary<string, string> ResolvedBatchDirections { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// ⑧块 Batch Policy —— **按 `(MaterialId, ProductionDepartmentId?)` 键控的策略集**
    /// （P0-01 整改，0号位 2026-10-07《未命名的Markdown文件 (7).md》§四）。
    ///
    /// **为什么必须是集合而不是单值**：正式业务粒度 = **Material + ProductionDepartment**（B-001；
    ///   `TaskSplitRuleConfig` / `BatchPolicyRuleSnapshot` 同口径）。一个 Domain 内
    ///   `Material A + Dept X`、`Material A + Dept Y`、`Material B + Dept X` 各有不同策略是**正常的**；
    ///   用单值代表整个 Domain 会让不同物料/部门**错误共用批量硬约束**，直接改变排程业务结果。
    ///
    /// **解析**：<see cref="PhaseTwoInitialScheduler.ResolveExecutionBatchPolicy"/> 按需求
    ///   `(MaterialId, StartStageCode → ProductionDepartmentId)` 精确命中；未精确命中 ⇒ `null`（**缺策略**）——
    ///   ⚠ **AUD-1-004（2026-10-10）起已删除** `(MaterialId, ProductionDepartmentId == null)` 的
    ///   「Material 级默认」回落（NULL 部门行是历史兼容记录，不得在 Solver 里复活）。
    ///   ⚠ P0-01（0号位 2026-10-08 §四）后缺策略**不再**等于「不拆、恒 1 批」：
    ///   C 桶需求一律判 `BATCH_POLICY_MISSING` 并 **Fail Closed**（不产 FinalTask、不进 Phase4）。
    ///   正式兜底链第③级「Global Batch Default」由 2/3号位 在上游**投影成有效 Policy** 后再交 1号位，
    ///   1号位 不得自行展开该级、更不得把末级 `null` 解释成「单批合法」。
    ///
    /// **载体**：`SolverStrategySnapshot.BatchPolicies`（⑧块，1↔2 契约，0号位 (7).md §十/§十一）⇒
    ///   `PhaseOneConstraintBuilder.BuildExecutionBatchPolicies` **整块逐字**收进本集合（本层**不裁剪**；
    ///   载荷的**物料轴**裁剪由 2号位 在**上游**完成，键域解析统一由 `ResolveExecutionBatchPolicy` 负责）。
    ///   空集合 ⇒ 每个 C 桶需求均判 `BATCH_POLICY_MISSING`（Fail Closed）。
    /// </summary>
    public List<LPS.APS.Core.Dto.BatchPolicyRuleSnapshot> ExecutionBatchPolicies { get; set; } = new();

    /// <summary>
    /// 需求级取图（Phase4 / Phase5 消费点用），解析顺序：
    ///   ① **Phase2 已登记选中路径**（<see cref="ChosenRoutePaths"/>）—— RT-002「局部修复不得换路径」，**优先且不重选**；
    ///   ② 需求自带固定路径（A/B，<c>LogicalProductionDemand.RouteCode/PathId</c>）；
    ///   ③ 唯一那条（单路径退化，行为与升维前等价）；多路径且未登记 ⇒ **false，不猜**。
    /// </summary>
    public bool TryGetDemandRoutingGraph(
        string logicalDemandKey,
        int materialId,
        string? demandRouteCode,
        int? demandPathId,
        [NotNullWhen(true)] out RoutingGraph? graph)
    {
        graph = null;

        if (ChosenRoutePaths.TryGetValue(logicalDemandKey, out var chosen))
        {
            return RoutingGraphs.TryGetValue(materialId, out var byPath)
                   && byPath.TryGetValue(chosen, out graph);
        }

        if (TryGetRoutingGraph(materialId, demandRouteCode, demandPathId, out graph))
        {
            return true;
        }

        return TryGetSingleRoutingGraph(materialId, out graph);
    }

    /// <summary>
    /// **批级取图**（P0-03，Phase4 局部修复用）。解析顺序：
    ///   ① **本批已登记选中路径**（<see cref="ChosenBatchRoutePaths"/>）—— RT-002「局部修复不得换路径」，优先且不重选；
    ///   ② 需求自带固定路径（A/B，`RouteCode/PathId`）；
    ///   ③ 唯一那条；多路径且未登记 ⇒ **false，不猜**（Fail Closed）。
    ///
    /// 与 <see cref="TryGetDemandRoutingGraph"/> 的唯一差别是①的键：本方法按**批键**，后者按**需求键**。
    /// </summary>
    public bool TryGetBatchRoutingGraph(
        string batchKey,
        int materialId,
        string? demandRouteCode,
        int? demandPathId,
        [NotNullWhen(true)] out RoutingGraph? graph)
    {
        graph = null;

        if (ChosenBatchRoutePaths.TryGetValue(batchKey, out var chosen))
        {
            return RoutingGraphs.TryGetValue(materialId, out var byPath)
                   && byPath.TryGetValue(chosen, out graph);
        }

        if (TryGetRoutingGraph(materialId, demandRouteCode, demandPathId, out graph))
        {
            return true;
        }

        return TryGetSingleRoutingGraph(materialId, out graph);
    }

    /// <summary>
    /// 需求级取**路径键**（Calendar 判据 Path 隔离用，P1-04）。解析顺序与
    /// <see cref="TryGetDemandRoutingGraph"/> 完全一致：选中路径 → 需求固定路径 → 唯一那条。
    /// 多路径且未登记 ⇒ false（不猜）。
    /// </summary>
    public bool TryGetDemandRoutePath(
        string logicalDemandKey,
        int materialId,
        string? demandRouteCode,
        int? demandPathId,
        out RoutePathKey pathKey)
    {
        pathKey = default;

        if (ChosenRoutePaths.TryGetValue(logicalDemandKey, out var chosen))
        {
            pathKey = chosen;
            return true;
        }

        if (!string.IsNullOrEmpty(demandRouteCode) && demandPathId is not null)
        {
            pathKey = RoutePathKey.Of(demandRouteCode, demandPathId.Value);
            return true;
        }

        if (RoutingGraphs.TryGetValue(materialId, out var byPath) && byPath.Count == 1)
        {
            pathKey = byPath.Keys.First();
            return true;
        }

        return false;
    }

    /// <summary>
    /// 同一物料存在 &gt;1 条 (RouteCode, PathId) 路径、而需求侧无路径选择载体的物料集合。
    /// V1 装载层归一化为单路径时恒为空；Q1 停用归一化后若出现，即 V2 多路径选路缺口的可见信号。
    /// </summary>
    public HashSet<int> AmbiguousRoutingPathMaterialIds { get; set; } = new();

    /// <summary>
    /// 本次求解范围内的 Stage：MaterialId → StageCode 集合（Phase1 <c>BuildReachableStages</c> 产出）。
    ///
    /// 【取源 = 两个取源取并集】（N8-01，2026-10-15）：
    ///   ① BOM/Stage 权威链 `request.StageSequenceChains`（按起点裁剪）；
    ///   ② Routing 可达集（BFS 自起点工序）。
    ///   ⇒ **含零 RoutingOperation 的 Stage**。这是 S-006／《04》场景7 要求的：
    ///     无 Routing 的 Stage 要形成 StageTimingNode（只传播时间，不排 Task）。
    ///
    /// 【为什么不能直接用全量 BOM StagePath】0号位 2026-09-29 裁决 §十四第4项 + P1-06：
    ///   会把 StartOperation/StartStage 之前已完成、本次根本不求解的 Stage 也算进来，凭空多出提前期，
    ///   并可能把整条 Routing 误判为 MISSING_PRODUCTION_DEPARTMENT_CONTEXT。
    ///   故取源虽含 BOM/Stage 链，但**必须按起点裁剪**（见 `ResolveChainStagesInScope`）。
    /// </summary>
    public Dictionary<int, HashSet<string>> EffectiveStages { get; set; } = new();

    /// <summary>
    /// **R09-③ 载体｜原始求解入参的只读引用**（2026-10-29）。
    ///
    /// 【为什么挂在这里】Phase2 的 `ScheduleForward` / `ScheduleBackward` 形参里**没有** request，
    ///   而这两个方法要把「中间无 Routing Stage 的提前期」按 **WORK 窗口**推进/回退，
    ///   必须拿到 `MaterialStageDepartmentContexts`（定部门）与 `DepartmentWorkWindows`（定窗口）。
    ///   这两个表只在 `DomainSolveRequest` 上。它们的**全部 4 个调用点都在 Phase2 内部**
    ///   （`PhaseTwoInitialScheduler.cs:3677/3683/3748/3771`）⇒ 改签名要连测试夹具一起动；
    ///   挂只读引用只动一处，行为面完全一样。
    ///
    /// ⚠ **只持有引用、只读**：不改 request 任何字段、不复制大表；
    ///   候选试排的 `ShallowCloneForTrial` 会 `MemberwiseClone` **共享同一个引用**（只读 ⇒ 安全）。
    /// ⚠ 可能为 <c>null</c>（手工构造的测试夹具）：**读它之前必须判空**，判空即「无提前期推进」，
    ///   与改造前逐字等价（零回归），不得因此抛异常。
    /// </summary>
    public DomainSolveRequest? Request { get; set; }

    /// <summary>
    /// **R09-③ 载体｜每个需求「中间无 Routing Stage」的提前期步骤**（2026-10-29）。
    ///
    /// 键 = `LogicalDemandKey`；值 = 按 Stage 链顺序排好的步骤，每条回答
    /// 「从 `FromStageCode` 这一步跨到 `ToStageCode`，中间夹着哪些**没有 RoutingOperation** 的 Stage」。
    ///
    /// 【为什么需要】`StageLeadTimeFact` 的提前期原本只在 `StageTimingNodeBuilder.Build` 里被累加，
    ///   而 `Build` **只在 `PhaseFiveCompression.cs:470` 被调用一次** —— 排完之后才建时间链。
    ///   ⇒ 它是**事后描述**，Phase2 排程时**完全不看**它，无 Routing 的中间 Stage 因而
    ///   **不占用任何时间**（R09-③ 的原始缺陷：中间 Stage 不成为硬时间约束）。
    ///   本表把这部分提前期**前置到 Phase1 算好**，供 Phase2 在跨 Stage 边上前推/后回。
    ///
    /// 【只收「中间」的】`From` / `To` 两端**都必须是真实 Routing Stage** ⇒
    ///   链首之前、链尾之后那些无 Routing Stage **不进本表**：链尾部分本来就是
    ///   `StageTimingNodeBuilder.BuildCore` 从末端 Task 往后累加的范围（不重复算）；
    ///   链首部分不属于 R09-③「**中间**无 Routing Stage」的字面范围。
    ///
    /// 【缺 LT 的处理】逐 leg 带 `LeadTimeResolved`。缺记录的 leg **不臆造 0 或 72h 时间**
    ///   （0号位 2026-10-15 第九轮 `:32` 原话），排程时按「不推进」处理，
    ///   由 `StageTimingNodeBuilder.BuildCore` **同一函数、同一输入**独立算出
    ///   `CompletionTimeCertain=false` —— 本层**不自行造原因码、不写 TraceNotes**。
    /// </summary>
    public Dictionary<string, List<RoutelessLeadStep>> RoutelessLeadStepsByDemand { get; set; } =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 「中间无 Routing Stage」的一段（见 <see cref="RoutelessLeadStepsByDemand"/>）。
    /// </summary>
    /// <param name="FromStageCode">前一个**有 Routing** 的 Stage 编码（跨段起点，严格非空）。</param>
    /// <param name="ToStageCode">后一个**有 Routing** 的 Stage 编码（跨段终点，严格非空）。</param>
    /// <param name="Legs">中间那些无 Routing Stage，按 Stage 链顺序；逐 leg 各自带部门与提前期。</param>
    internal readonly record struct RoutelessLeadStep(
        string FromStageCode,
        string ToStageCode,
        IReadOnlyList<RoutelessLeadLeg> Legs);

    /// <summary>
    /// 一个「无 Routing Stage」的提前期片（见 <see cref="RoutelessLeadStepsByDemand"/>）。
    /// **逐 Stage 分开**而不是先求和：提前期是**有效生产小时**，要在**该 Stage 自己的部门**
    /// WORK 窗口内消耗（R09-① 口径），部门不同就不能先加再推。
    /// </summary>
    /// <param name="StageCode">该无 Routing Stage 的编码。</param>
    /// <param name="Hours">`StageLeadTimeFact.LeadTimeHours` 原值（不做符号裁决）。</param>
    /// <param name="LeadTimeResolved">
    /// 是否命中契约 4 值之一。<c>false</c> = `STAGE_LEADTIME_MISSING` ⇒ **不得**当作「零提前期」，
    /// 只是排程层编不出时长、不推进。
    /// </param>
    internal readonly record struct RoutelessLeadLeg(
        string StageCode,
        decimal Hours,
        bool LeadTimeResolved);

    /// <summary>
    /// 工序资源资格：<see cref="EligibilityLookupKey"/> → 合法 ResourceId 列表（按优先级排序）。
    /// ⚠ 旧键为字符串 <c>"{MaterialId}::{RouteCode}::{OperationCode}"</c> —— **缺部门维度**，
    /// 会把「同物料同工序码但分属不同部门」的两套资格**合并成一份** ⇒ 跨部门串资源。
    /// 现键补上 ProductionDepartmentId，与部门锁定谓词（<c>validOperationKeys</c>）同口径。
    /// 残留：契约 <c>OperationResourceEligibility</c> 无 StageCode 字段，无法再细到 Stage（见 <see cref="EligibilityLookupKey"/> 注释）。
    /// </summary>
    public Dictionary<EligibilityLookupKey, List<int>> OperationResourceEligibility { get; set; } = new();

    /// <summary>
    /// 2026-11-02（**纯观测**）：本次求解输入里**出现过工序行**的 `MaterialId` 集合。
    /// 用途只有一个 —— `P2_NO_ROUTING` 出口处区分「输入里没有该物料」与「有行但图没建出来」。
    /// 不参与任何判定；`MemberwiseClone` 走引用共享（见本类克隆注释），只读不写。
    /// </summary>
    public HashSet<int> MaterialsWithRoutingInput { get; set; } = new();

    /// <summary>
    /// 2026-11-03（**纯观测**）：`MaterialId` → 本次求解范围内**缺部门上下文**的 `StageCode` 列表。
    /// 用途只有一个 —— Phase5 输出 `MISSING_PRODUCTION_DEPARTMENT_CONTEXT` 时，
    /// 把「哪个 Stage 缺」写进人可读正文，让数据方能直接定位要补 `MaterialStageDeptContext` 的哪一行。
    /// 不参与任何判定；与 <see cref="MissingDepartmentContextMaterialIds"/> 同源、只多一层 Stage 粒度。
    /// </summary>
    public Dictionary<int, List<string>> MissingDepartmentContextStages { get; set; } = new();

    /// <summary>
    /// 2026-11-02（**纯观测**）：`MaterialId` → 该物料在本输入里的**全部**资格键。
    /// 用途只有一个 —— `P2_NO_ELIGIBLE_RESOURCE` 出口处区分「该物料一条资格行都没有」与
    /// 「有资格行，但 (部门/Route/Path/工序) 五元组对不上」。懒建、只读。不参与任何判定。
    /// </summary>
    private Dictionary<int, List<EligibilityLookupKey>>? _eligibilityKeysByMaterial;

    public IReadOnlyList<EligibilityLookupKey> GetEligibilityKeysForMaterial(int materialId)
    {
        if (_eligibilityKeysByMaterial is null)
        {
            var byMaterial = new Dictionary<int, List<EligibilityLookupKey>>();
            foreach (var key in OperationResourceEligibility.Keys)
            {
                if (!byMaterial.TryGetValue(key.MaterialId, out var list))
                {
                    list = new List<EligibilityLookupKey>();
                    byMaterial[key.MaterialId] = list;
                }
                list.Add(key);
            }
            _eligibilityKeysByMaterial = byMaterial;
        }

        return _eligibilityKeysByMaterial.TryGetValue(materialId, out var keys)
            ? keys
            : (IReadOnlyList<EligibilityLookupKey>)Array.Empty<EligibilityLookupKey>();
    }

    /// <summary>
    /// P0-04修复：工序资源产能系数映射：(<see cref="EligibilityLookupKey"/>, ResourceId) → CapacityFactor
    /// 用于Duration计算：StandardDuration × PlannedProcessQty ÷ CapacityFactor
    /// </summary>
    public Dictionary<EligibilityLookupKey, Dictionary<int, decimal>> ResourceCapacityFactors { get; set; } = new();

    /// <summary>
    /// 资源编码映射：ResourceId → ResourceCode（源 ResourceDefinition.ResourceCode）。
    /// P1-08修复：供 Phase2/Phase4 生成 FinalTaskDraft 时回填 ResourceCode，不再留空。
    /// </summary>
    public Dictionary<int, string> ResourceCodes { get; set; } = new();

    /// <summary>
    /// 资源编码反向映射：ResourceCode → ResourceId。
    /// P1-02（BottleneckMode 锚点）：供 Phase3 消费 AnchorResourceCode（业务编码）反查 ResourceId。
    /// 空编码（string.Empty）不纳入，避免无效键。
    /// </summary>
    public Dictionary<string, int> ResourceIdsByCode { get; set; } = new();

    /// <summary>
    /// **R06 动态瓶颈｜锚点 ResourceId**：`StrategySnapshot.SolverStrategy.AnchorResourceCode` 经
    /// `ResourceIdsByCode` 反查的结果。<c>null</c> = 未指定编码／反查不到 ⇒ **等价 Auto**（冻结字段注释逐字：
    /// 「Auto 忽略；空=未指定→等价 Auto」）。由 <c>BuildResourceCodes</c> 一次填好，Phase2/Phase4 候选取用。
    /// </summary>
    public int? BottleneckAnchorResourceId { get; set; }

    /// <summary>
    /// **R06 动态瓶颈｜策略**（`Auto` / `PreferAnchor` / `ForceAnchor` / `NotAnchor`）。
    /// Phase3 原有用途 = 算瓶颈集诊断归属；2026-10-28 起 Phase2/Phase4 的**候选资源排序**也读它
    /// （见 <c>PhaseTwoInitialScheduler.OrderResourcesByPreference</c>）。
    /// </summary>
    public DynamicBottleneckMode BottleneckMode { get; set; } = DynamicBottleneckMode.Auto;

    /// <summary>
    /// **R06 动态瓶颈｜「有负荷」判据载体**：在**至少一条**需求的合格工序上出现过资格的资源集合。
    ///
    /// ⚠ 这是**排程前**的静态代理，不是 Phase3 的事后利用率：真实利用率（`Σ占用时长 ÷ 日历可用时长`）
    ///   只有在 Phase2 排完之后才算得出，**排程前不可得** ⇒ 排序阶段只能用「有没有需求排得上它」这个
    ///   布尔代理。<c>PreferAnchor</c> 要求「锚点有负荷才优先」，无此集合就只能退化成 `ForceAnchor`
    ///   （两者结果恒同、四模式分不开）。本代理**只用于排序取舍**，**不参与**可行性判定。
    /// </summary>
    public HashSet<int> ResourcesWithStaticLoad { get; } = new();

    /// <summary>
    /// **R-2 设备口径白名单**（原样透传 `DomainSolveRequest.DeviceResourceIds`；1号位 2026-10-16 回执 §四）。
    /// **三态**（与契约逐字对齐）：
    ///   · `null` ⇒ **未投影** ⇒ ④.6 留痕**不产**设备口径段，行为与改造前**逐字一致**（零回归）；
    ///   · 非 `null` ⇒ 只列**纯设备** `ResourceId`（升序去重、结果确定性）；
    ///   · **空集 ≠ `null`** ⇒ **已投影、但本 Run 无任何纯设备**（本身有信息量，**不得**回落成 `null` 语义）。
    /// ⚠ 键域与 `ResourceDefinition.ResourceId` **同域**、**原样透传**；本号位**不做任何资源身份判别、
    ///   不硬编码任何阈值**（合成键规则归 2号位 `SolverResourceProjection`）。
    /// ⚠ **只作留痕**，**不参与**择优（默认仍取「含人工槽」那个排序键）。
    /// </summary>
    public IReadOnlyList<int>? DeviceResourceIds { get; set; }

    /// <summary>
    /// Setup EXACT 换型规则（P1-02 item1 接线）：(当前工序, 当前设备, 前产品, 后产品) → 换型分钟。
    /// 方向性键（A→B ≠ B→A）；源 = 第⑦块 SetupTransitionRules（2号位 按 Domain 裁剪投影，r13392）。
    /// </summary>
    public Dictionary<SetupOptimizer.SetupExactKey, decimal> SetupExactRules { get; set; } = new();

    /// <summary>
    /// 第⑦块 Setup 规则的装载报告（0号位 R07R08 v1.1 第 3 条）：同键异值剔除数 / 无效行数 / 冲突留样。
    /// Phase1 装载时填，Phase3 出诊断用；**不参与排程计算**。
    /// </summary>
    public SetupOptimizer.SetupRuleLoadReport SetupRuleLoadReport { get; set; } = new();

    /// <summary>
    /// **(MaterialId, StageCode) → ProductionDepartmentId**（部门锁定时的同一张表，2026-10-28 挂出复用）。
    ///
    /// 【为什么需要】Setup 规则键自 2026-10-28 起含生产部门（0号位 R07R08 v1.1 第 3 条），
    ///   而 Phase4/Phase5 手上只有 <see cref="FinalTaskDraft"/>（有 StageCode、**无部门**）⇒
    ///   必须按「物料 + Stage」反查部门，**与既有部门锁定同源、不另造部门**。
    /// 【口径】只登记**未冲突**的键（同键多部门的物料已被 Fail Closed 整条排除，不会进排程）。
    /// 【缺键语义】查不到 ⇒ 返回 null ⇒ Setup 解析判 <c>SourceUnavailable</c>（WARNING，不冒充「无前态」）。
    /// </summary>
    public Dictionary<(int MaterialId, string StageCode), int> DepartmentByMaterialStage { get; set; } = new();

    /// <summary>Setup DEFAULT 换型规则：(当前工序, 当前设备) → 默认换型分钟（同源；EXACT 未命中时第二优先）。</summary>
    public Dictionary<SetupOptimizer.SetupDefaultKey, decimal> SetupDefaultRules { get; set; } = new();

    /// <summary>
    /// 资源级已排产品时间线（item1 接线阶段二）：FromMaterial = 时间邻接的上一 Task 产品。
    /// Phase2 Schedule 入口以锁定继承 Task 重置种子，放置/移动/合并时与 resourceOccupancy 成对维护；
    /// Phase4 兜底重跑 Schedule 时自动重置。
    /// </summary>
    public ResourceProductTimeline ProductTimeline { get; set; } = new();

    /// <summary>
    /// M5 第一批：Run 级交期覆盖 LogicalDemandKey → ManualTargetDueDate（OrderCanonicalId 已由 2号位 按需求展开，
    /// 仅非空手工目标交期行投影；空 = 无覆盖）。Phase2/3/5 经 <see cref="EffectiveDue"/> 统一取「覆盖 ?? RequiredAvailableTime」。
    /// </summary>
    public Dictionary<string, DateTime> DueDateOverrides { get; set; } = new();

    /// <summary>M5 第一批：Task 软目标 (DraftId=SourceDraftId, OperationCode) → TargetTime（软偏好，非硬约束，未达走决策说明）。</summary>
    public Dictionary<(string DraftId, string OperationCode), DateTime> TaskTargetOverrides { get; set; } = new();

    /// <summary>
    /// B.2 求解过程追溯收集器：各阶段产出的「决策说明」（非排程结果，**不进 ReasonCode 体系** —— 0号位 Q4 裁决）。
    /// Phase2/4 产出，Phase5 经 <c>DomainSolveResult.SolveTraceNotes</c> 导出，2号位 原样落库/透传。
    /// 载体 = 2号位 r13494 落地的 <see cref="SolveTraceNote"/>；本收集器由 Phase 1 建立、贯穿 Phase 2–5。
    /// 只承载「求解过程决策上下文」，不承载排程硬结果 —— 排程事实走 FinalTasks/ExplanationFacts。
    /// </summary>
    public List<SolveTraceNote> TraceNotes { get; set; } = new();

    /// <summary>
    /// 2026-10-27 四十三刀：**候选试排用的浅拷贝上下文**（候选并行的前置条件）。
    ///
    /// 【为什么需要】候选试排会往上下文里写两个成员：`ProductTimeline`（逐候选 `realTimeline.Clone()` 后
    ///   被占用/合批改写）与 `TraceNotes`（Setup 追踪三元组追加）。串行时「写 → 用完还原」没问题，
    ///   并行时两条线程会同时写同一个对象 ⇒ 结果互串。
    ///
    /// 【做什么】`MemberwiseClone` 复制全部字段（**共享**所有只读集合的引用：RoutingGraphs / 各类规则表 /
    ///   资源日历 / 锁定任务 / 跨物料 DAG 等 —— 这些在 Phase2 全程只读，抄一份既浪费又没必要），
    ///   再给两个**可写**成员各配一份新的空实例：
    ///     · `ProductTimeline`：调用方随后会 `= realTimeline.Clone()`，这里给空对象只为「赋值前也不与父共享」；
    ///     · `TraceNotes`：给**空表**。候选的 trace 增量因此天然落在这张空表上，
    ///       裁剪区间恒为 `[0, Count)` —— 与串行「按水位切片」得到的结果**逐条相同**。
    ///
    /// 【为什么别的不必经手】Phase2 候选试排对 `constraints` 的写入点已全仓核过：只有上面这两个。
    ///   其余成员（含 `AmbiguousRoutingPathMaterialIds` / `MissingDepartmentContextMaterialIds` /
    ///   `StageSequenceConflictMaterialIds` 三个 HashSet）在 `PhaseOneConstraintBuilder` 之外**只读**，
    ///   且唯一会写它们的 `TryGetSingleRoutingGraph` 调用点在候选循环之外。
    /// </summary>
    public ConstraintContext ShallowCloneForTrial()
    {
        var clone = (ConstraintContext)MemberwiseClone();
        clone.ProductTimeline = new ResourceProductTimeline();
        clone.TraceNotes = new List<SolveTraceNote>();
        return clone;
    }

    /// <summary>demand 的有效交期 = Run 级覆盖（ManualTargetDueDate）?? 正式 RequiredAvailableTime（M5 第一批统一换用点）。</summary>
    public DateTime EffectiveDue(LogicalProductionDemand demand)
        => DueDateOverrides.TryGetValue(demand.LogicalDemandKey, out var due) ? due : demand.RequiredAvailableTime;

    /// <summary>
    /// **判定期**交期上界：与 <see cref="EffectiveDue"/> 只差「客户 DATE 交期当整天都算按期」这一件事。
    ///
    /// 裁决依据：0号位 2026-10-31《致1号位_二次回执完整审核回复_v3.0》`:18` ——
    ///   「客户DATE业务当日23:59:59，技术以次日00:00**排他**上界（最终完成 `&lt; nextMidnight`），不写死100%」；
    ///   Manual 手工覆盖走已冻结的精准时刻规则。
    /// 与查询侧同口径：`ScheduleQueryService.DelayedOnDate` = `CAST(PlannedEndTime AS date) &gt; CustomerDueDate`
    ///   （Q1 裁决 2026-10-17）—— 同一笔延期，Solver 与查询必须给同一个结论。
    ///
    /// **为什么返回「当日最后一刻」而不是字面上的次日00:00**：调用点全部沿用
    ///   `完成 &gt; 上界 ⇒ 延期` / `完成 &lt;= 上界 ⇒ 按期` 的既有比较式。返回 `次日00:00 − 1 tick` 后，
    ///   这些比较式**一字不改**即与 `完成 &lt; 次日00:00` 完全等价（`DateTime` 最小刻度就是 1 tick）。
    ///   改值不改式 —— 十处调用点逐点翻比较符太容易写反一个。
    ///
    /// 原样返回的三种情形：`DateTime.MaxValue`（无交期哨兵）、Manual 覆盖（精确时刻，按 `&lt;=` 判）、
    ///   值本身已带时刻成分（不是 DATE，例如将来若真有分支喂进 `Order_Canonical.DueDate` 这种 `DATETIME2`）。
    /// </summary>
    public DateTime EffectiveDueJudgementBound(LogicalProductionDemand demand)
        => ToJudgementBound(
            EffectiveDue(demand),
            isManualOverride: DueDateOverrides.ContainsKey(demand.LogicalDemandKey));

    /// <summary>纯函数形态（不依赖上下文，供 Unit 直接打正反例）。语义见 <see cref="EffectiveDueJudgementBound"/>。</summary>
    internal static DateTime ToJudgementBound(DateTime effectiveDue, bool isManualOverride)
    {
        if (effectiveDue == DateTime.MaxValue) return effectiveDue;        // 无交期哨兵：原样（必须先判，MaxValue 自带时刻成分）
        if (isManualOverride) return effectiveDue;                          // Manual 精确时刻：原样，沿用 `<=`
        if (effectiveDue.TimeOfDay != TimeSpan.Zero) return effectiveDue;   // 已有时刻成分 ⇒ 不是 DATE：原样
        return effectiveDue.AddDays(1).AddTicks(-1);                        // 客户 DATE ⇒ 交期日 23:59:59.9999999
    }

    /// <summary>
    /// 资源日历：ResourceId → 可用时间窗列表（已排序）
    /// </summary>
    public Dictionary<int, List<TimeWindow>> ResourceCalendars { get; set; } = new();

    /// <summary>
    /// 物料多段可用性：AllocationSequence → Quantity-Time 分段列表（已按时间排序）
    /// </summary>
    public Dictionary<long, List<MaterialAvailabilitySegment>> MaterialAvailability { get; set; } = new();

    /// <summary>
    /// 锁定任务约束：复合键 <c>(DraftId, StageCode, OperationCode)</c> → 锁定信息。
    ///
    /// · P1-07：同一 LogicalDemand 可有多操作锚点 ⇒ 不能再用 DraftId 单键。
    /// · P0-02（0号位 2026-10-09 第四轮复审《APS_V1_2_20261009.md》§三）：**必须含 StageCode**。
    ///   已生效的工序身份规则要求 `StageCode` 与 `OperationCode` **共同**定位工序，不得假设
    ///   `OperationCode` 全局唯一；否则「同 DraftId、Stage A/OP10 与 Stage B/OP10 同时锁定」
    ///   会在建字典阶段抛重复 Key 异常。此前仅二维键 ⇒ 键域不足。
    /// </summary>
    public Dictionary<(string DraftId, string StageCode, string OperationCode), LockedTaskConstraint> LockedTasks { get; set; } = new();

    /// <summary>
    /// 共享资源占用块：ResourceId → 占用时间块列表
    /// </summary>
    public Dictionary<int, List<ResourceBlockInfo>> ResourceBlocks { get; set; } = new();

    /// <summary>
    /// 缺失生产部门 Context 的 MaterialId 集合。
    /// 部门锁定（最小 B）：按 (MaterialId, StageCode) 查 MaterialStageDepartmentContexts，
    /// 若某 Material 的任一工序 Stage 查不到 Context，则该 Material 整条 Routing 无法锁定部门，
    /// 对应的 Demand 应标记 Unscheduled，Reason = MISSING_PRODUCTION_DEPARTMENT_CONTEXT。
    /// </summary>
    public HashSet<int> MissingDepartmentContextMaterialIds { get; set; } = new();

    /// <summary>
    /// 0号位 2026-09-29 裁决 §5.3 落实（不静默）：同 (StageCode, OperationCode) 的 RoutingOperation 重复行计数。
    /// 旧实现为静默覆盖（后者覆盖前者 ⇒ 边被错接、一道工序静默丢失）。保留首行 + 计数，供 Phase3 出诊断。
    /// </summary>
    public int DuplicateOperationNodeKeyCount { get; set; }

    /// <summary>
    /// 0号位 2026-09-29 裁决 §5.3 落实（不静默）：<c>RoutingDependency</c> 端点 Stage 归属**无法消解**的边计数。
    /// 成因：契约 RoutingDependency 无 StageCode 字段，而该工序码在同物料内跨多个 Stage 且部门也无法区分
    /// （2号位 实测 117 物料同码跨 Stage）。此类边被丢弃并计数，**不猜**（不静默塞给任一 Stage）。
    /// </summary>
    public int UnresolvedDependencyEdgeCount { get; set; }

    /// <summary>
    /// 跨物料依赖（任务喂任务，方案A）：子先父后的有序 LogicalDemandKey 列表（已按层拍平）。
    /// 无跨物料 link 时等价于按 DemandSequence 平铺。
    /// </summary>
    public List<string> CrossMaterialOrder { get; set; } = new();

    /// <summary>
    /// 跨物料依赖分层：每层一个 LogicalDemandKey 列表（子件层在前，父件层在后）。
    /// </summary>
    public List<List<string>> CrossMaterialLayers { get; set; } = new();

    /// <summary>
    /// 父 → 子 映射（父 LogicalDemandKey → 其直接子件 LogicalDemandKey 列表）。
    /// 供 Phase2 在排父件时取子件的真实完成时间（动态物料可用时间合并）。
    /// </summary>
    public Dictionary<string, List<string>> CrossMaterialParentToChildren { get; set; } = new();

    /// <summary>
    /// 子 → 父 映射（子件 LogicalDemandKey → 直接父件 LogicalDemandKey 列表）。
    /// 2026-10-28：与 <see cref="CrossMaterialParentToChildren"/> 同一份数据反向索引，供合批守卫使用 ——
    ///   合批延长目标 Task 的 End 时，要能 O(1) 找到「谁会因为这个 End 变晚而被顶穿」。
    /// </summary>
    public Dictionary<string, List<string>> CrossMaterialChildToParents { get; set; } = new();

    /// <summary>
    /// 本域**已落定**需求的「最早 Task 起点」（需求键 → 该需求已排 Task 的最早 PlannedStartTime）。
    /// 2026-10-28（PV190 真因）：只在 Phase2 外层需求循环**落定后**写入（合批落定的需求也写），
    ///   候选试排期间**只读不写** ⇒ 不破坏候选隔离。用途 = 合批守卫：
    ///   已发布的父件起点不会回推，故合批一旦会把目标 Task 的 End 顶过父件起点，就必须拒绝这次合批。
    /// </summary>
    public Dictionary<string, DateTime> DemandEarliestStart { get; set; } = new();

    /// <summary>
    /// P1-12：父 → (父, 子) 边界的滞后时间（分钟）。键为 (父 LogicalDemandKey, 子 LogicalDemandKey)。
    /// 供 Phase2/Phase4 的 GetDynamicMaterialFloor 在子件完成时间上累加滞后。
    /// </summary>
    public Dictionary<(string, string), decimal> CrossMaterialLagMinutes { get; set; } = new();

    /// <summary>
    /// 跨物料 BOM 依赖存在环（技术失败标记）。
    /// </summary>
    public bool CrossMaterialHasCycle { get; set; } = false;

    /// <summary>
    /// B-1（0号位 2026-09-29 裁决 §二）落实：StageSeq 全序链冲突的 MaterialId 集合。
    ///
    /// 判定口径（逐字对齐裁决 §2.1/§2.2）：唯一性范围 = **一个已解析好的具体 StageSequenceChain 内**，
    /// 不是 Material 全局。同 StageCode 取最小 StageSeq 收敛后，**不同 StageCode 不得拥有相同 StageSeq**。
    /// 出现即登记本集合 ⇒ 该物料的需求**不生成正式计划**（§2.3：不允许静默按数组顺序、不允许静默并行、
    /// 必须输出明确 Issue）。下游 Reason = <c>STAGE_SEQUENCE_CONFLICT</c>。
    /// </summary>
    public HashSet<int> StageSequenceConflictMaterialIds { get; set; } = new();
}

/// <summary>
/// 工序节点身份：**(StageCode, OperationCode)**。
/// 依据：0号位 2026-09-29 裁决《Stage顺序、无Routing Stage、Operation标识与Task/MES粒度》§5.1/§5.2 逐字——
/// 「OperationCode 不是全局唯一业务身份。同一个物料不同Stage可能出现相同OperationCode」
/// 「在一个已经限定 Material / ProductionDepartment / Route / Path 的 Routing 图内部，最小节点键采用 (StageCode, OperationCode)」。
/// 唯一性范围 = 单个已限定上下文的 Routing 图内部（**不是 Material 全局**）。
/// ⚠ 禁止再退回 OperationCode 单键（裁决 §5.3 明文「不能出现 Phase1 用 (Stage,Operation)、Phase2 又退回 OperationCode 这种半升级」）。
/// </summary>
internal readonly record struct OperationNodeKey(string StageCode, string OperationCode)
{
    /// <summary>空安全构造：null 归一化为 string.Empty（与既有 2号位 口径「OperationCode 恒非空」一致）。</summary>
    public static OperationNodeKey Of(string? stageCode, string? operationCode)
        => new(stageCode ?? string.Empty, operationCode ?? string.Empty);

    public override string ToString() => $"{StageCode}::{OperationCode}";
}

/// <summary>
/// Routing 路径身份：**(RouteCode, PathId)**。
/// 依据：《APS_V1_1号位有限产能排程开发实施包 v1.6》新增实施要求 + 本轮统一执行红线 Q1——
/// 「PathId 必须进入 Graph/Dependency/Eligibility 节点和匹配键，**禁止跨 Path 连边或 Eligibility 混用**」。
/// 契约 <c>RoutingOperation.PathId</c> / <c>RoutingDependency.PathId</c> / <c>OperationResourceEligibility.PathId</c>
/// 均为真实值 ⇒ 图/边的分组键可携带。升维后，同 (物料, RouteCode) 但不同 Path 的工序与依赖
/// **不再被并入同一张图**（旧实现按 RouteCode 单键分组，V2 多路径时会把互斥备选路径并成一张图）。
/// V1 装载层目前把所有工序归一化为 'DEFAULT'/1（2号位 <c>NormalizeToSingleRoute</c>），
/// 故本键在 V1 恒为唯一一组，行为与升维前等价；Q1 要求装载层停用该归一化后，本键即真实生效。
/// </summary>
/// <summary>
/// 执行批的**求解态身份**（P0-03）：Phase2 `FormExecutionBatches` 形成后登记，
/// 供 Phase4 局部修复**逐批**重建（键 + 本批数量）。Scheduling 内部类型，非 1↔2 契约面。
/// </summary>
internal readonly record struct ExecutionBatchPlanEntry(
    string BatchKey,
    decimal NetOutputQty,
    decimal PlannedProcessQty);

internal readonly record struct RoutePathKey(string RouteCode, int PathId)
{
    /// <summary>空安全构造：null RouteCode 归一化为 string.Empty（与 2号位「RouteCode 恒非空」口径一致）。</summary>
    public static RoutePathKey Of(string? routeCode, int pathId)
        => new(routeCode ?? string.Empty, pathId);

    public override string ToString() => $"{RouteCode}::{PathId}";
}

/// <summary>
/// 工序资源资格 / 产能系数查找键：MaterialId + ProductionDepartmentId + RouteCode + OperationCode。
/// ⚠ **结构上限**：契约 <c>OperationResourceEligibility</c>（`LPS.APS.Core/Entities/Aps/OperationResourceEligibility.cs`）
/// **没有 StageCode 字段** —— 故该查找键**无法**携带 Stage 上下文（裁决 §5.2 的完整复合键在此不可达）。
/// 本键相对旧实现的唯一可行动改进 = **补上 ProductionDepartmentId**（旧键连部门都没有，会把两个部门
/// 同名工序的资源资格合并成一份 ⇒ 跨部门串资源）。部门维度是「物料×阶段」的联合属性，可作 Stage 的近似区分。
/// **残留（数据模型限制，非 1号位 可修）**：同物料 + 同部门下若两个 Stage 出现同一 OperationCode，
/// 其资格仍合并 —— 该情形由装载层诊断登记，1号位 不猜。
///
/// 2026-10-05 冻结补齐（《APS_V1_1号位有限产能排程开发实施包 v1.6》新增要求 + Q1）：
/// **补上 PathId**。原文：「PathId 必须进入 Graph/Dependency/Eligibility 节点和匹配键，
/// 禁止跨 Path 连边或 Eligibility 混用」。契约 <c>OperationResourceEligibility.PathId</c>
/// 与 <c>RoutingOperation.PathId</c> 均为真实值 ⇒ 键可携带。补 PathId 后，同
/// (物料, 部门, RouteCode, OperationCode) 但不同 Path 的资格不再被合并
/// （2号位 已在 PeggingOrchestrator.cs:3967 记录过该塌组现象）。
/// </summary>
internal readonly record struct EligibilityLookupKey(
    int MaterialId, int ProductionDepartmentId, string RouteCode, int PathId, string OperationCode);

/// <summary>
/// 工序依赖图（单个物料单条路径）
/// </summary>
internal class RoutingGraph
{
    /// <summary>
    /// 工序节点：(StageCode, OperationCode) → 工序信息。
    /// 0号位 2026-09-29 裁决 §5.3：本字典为必须升维的三处之一（原为 OperationCode 单键 ⇒ 后者覆盖前者、
    /// 边被错接、其中一道工序静默丢失；2号位 实测 117 物料受影响）。
    /// </summary>
    public Dictionary<OperationNodeKey, OperationNode> Operations { get; set; } = new();

    /// <summary>
    /// 依赖边：To 节点 → 前驱列表。
    /// 键与边的两端均为 <see cref="OperationNodeKey"/>，不再用 OperationCode 单键。
    /// </summary>
    public Dictionary<OperationNodeKey, List<DependencyEdge>> Dependencies { get; set; } = new();

    /// <summary>
    /// 根工序（无前驱的工序）
    /// </summary>
    public List<OperationNodeKey> RootOperations { get; set; } = new();

    /// <summary>
    /// R2-F ①（0号位 2026-10-14 第七轮正式复核 §八.2a）：本候选 Path 因**工序资源模式越域**
    ///   （`OperationPlanningMode` 不在冻结值域 `{FINITE_RESOURCE, UNCONSTRAINED, WAIT_ONLY}`）
    ///   被**整条排除** —— 该 Path **不得产出任何 FinalTask**。
    ///
    /// 置位点：<see cref="PhaseOneConstraintBuilder.ValidateOperationPlanningModes"/>（Phase1 步骤 2.6）。
    /// 消费点（<see cref="PhaseTwoInitialScheduler"/>）：自由候选枚举处**剔除**（`:477`）、
    ///   需求固定路径处 **Fail Closed 转未排**（`:462`）、锁定任务继承处 **Fail Closed 转未排**（`:141`）。
    ///
    /// ⚠ 为什么是**整条 Path** 而不是**单个节点**：抽掉一个节点会在依赖图上留断口
    ///   （后继节点失去前驱 ⇒ 顺序错），与 Q-3「禁止跨 Path 找替代节点 / 禁止半升级」冲突。
    /// ⚠ 为什么**不**沿用旧的「抛 <see cref="SolverInputContractException"/>」整域 Fail Closed：见
    ///   <see cref="PhaseOneConstraintBuilder.ValidateOperationPlanningModes"/> 文档的 ① 变更专段 ——
    ///   损害面可可靠定位到单条候选 Path，按 0号位 §八.2a 逐字「禁止此非法 Path 产生 Task、登记输入契约 Issue」处置。
    /// </summary>
    public bool IsOperationPlanningModeInvalid { get; set; }
}

/// <summary>
/// 工序节点
/// </summary>
internal class OperationNode
{
    public string OperationCode { get; set; } = string.Empty;
    public string OperationName { get; set; } = string.Empty;
    public string ProcessType { get; set; } = string.Empty;
    public string? StageCode { get; set; }
    public decimal StandardDuration { get; set; }
    // OperationPlanningMode 消费接线（0号位 20260922）：工序规划模式随 RoutingOperation 透传，
    // Phase2 正排/倒排据此分型——FINITE_RESOURCE 走资源找槽；UNCONSTRAINED/WAIT_ONLY 跳过资源、
    // 保留工艺时间走链（Task ResourceId=NULL）。DDL v5.1.8.2 定义，默认与 DDL DEFAULT 对齐。
    public string OperationPlanningMode { get; set; } = "FINITE_RESOURCE";
    // item1 接线（阶段二）：原 SetupTime 字段已删除——Setup 走 SetupTransitionRule 规则查找（v1.2 §1.2/§20.3），
    // 工序节点不再携带静态换型值，防止误读。
    public decimal? TransferBatchSize { get; set; }

    // P1-08修复：工序节点补齐工艺路径编码与路径序号（源 RoutingOperation.RouteCode/PathId）。
    // 供 Phase2/Phase4 生成 FinalTaskDraft 时回填 RouteCode/PathId，不再留 null。
    public string RouteCode { get; set; } = "DEFAULT";
    public int PathId { get; set; } = 1;

    // 0号位 2026-09-29 裁决 §5.2 落实：节点携带 ProductionDepartmentId，
    // 供 (a) 资源资格/产能系数查找键 EligibilityLookupKey 补齐部门维度；
    // (b) RoutingDependency 无 StageCode 字段时，用「工序码 + 部门」消解同名工序的归属歧义。
    public int ProductionDepartmentId { get; set; }
}

/// <summary>
/// 依赖边。两端为 <see cref="OperationNodeKey"/>（含 Stage 上下文）。
/// ⚠ 契约 <c>RoutingDependency</c>（`LPS.APS.Core/Entities/Aps/RoutingDependency.cs`）**只有
/// From/To OperationCode 与单值 ProductionDepartmentId，没有 StageCode 字段** ⇒ 边的 Stage 归属
/// 必须由 1号位 在 <c>BuildRoutingGraphs</c> 里从 RoutingOperation 反查消解；
/// 消解不唯一时**登记 Issue 并丢弃该边，不猜**（0号位 2026-09-29 §5.3「禁止半升级」+ 不静默原则）。
/// </summary>
internal class DependencyEdge
{
    public OperationNodeKey From { get; set; }
    public OperationNodeKey To { get; set; }
    public string DependencyType { get; set; } = "ES";
    public decimal LagTime { get; set; }
}

/// <summary>
/// 物料可用性分段
/// </summary>
internal class MaterialAvailabilitySegment
{
    public decimal Quantity { get; set; }
    public DateTime AvailableTime { get; set; }
    public string? SourceType { get; set; }
    public string? SourceKey { get; set; }
}

/// <summary>
/// 锁定任务约束
/// </summary>
internal class LockedTaskConstraint
{
    public string DraftId { get; set; } = string.Empty;
    public int ResourceId { get; set; }
    public DateTime LockedStart { get; set; }
    public DateTime LockedEnd { get; set; }
    public string ConstraintType { get; set; } = string.Empty;

    // 第8轮P0-01修复：Anchor部分数量和工序信息闭环
    public string? StageCode { get; set; }
    public string? OperationCode { get; set; }
    public decimal? LockedQuantity { get; set; }

    // P1-01：净合格数量（YIELD 场景，锁定 Task 的净产出，!= 加工量）
    public decimal? LockedNetOutputQty { get; set; }

    // P1-01：产能加工数量（锁定 Task 的计划加工量）
    public decimal? LockedPlannedProcessQty { get; set; }

    public string? TaskKey { get; set; }
}

/// <summary>
/// 求解器**输入契约**异常：输入无法满足冻结契约要求的唯一标识 / 完整身份（不是业务「排不下」）。
///
/// 由 <see cref="FiniteCapacitySolver.SolveAsync"/> 捕获 ⇒ **受控 Fail Closed**（`Success=false` + `ErrorMessage`），
/// 不向外抛未处理异常、也不静默抹除锚点。
/// 依据：0号位 2026-10-09《APS_V1_2_20261009.md》§三 P0-02（「SolveAsync 没有对该异常做受控 Fail Closed」）。
/// </summary>
internal sealed class SolverInputContractException : Exception
{
    public SolverInputContractException(string message) : base(message) { }
}

/// <summary>
/// 资源占用块信息
/// </summary>
internal class ResourceBlockInfo
{
    public int ResourceId { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public string Reason { get; set; } = string.Empty;
}
