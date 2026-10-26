using System.Data;
using System.Diagnostics;
using System.Text.Json;
using Dapper;
using LPS.APS.Application.Models;
using LPS.APS.Core.Dto;
using LPS.APS.Core.DTOs.Governance;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Core.Enum;
using LPS.APS.Core.Interfaces;
using LPS.APS.Core.Rules;
using LPS.APS.BusinessRules.Loaders;
using LPS.APS.BusinessRules.Models;
using LPS.APS.BusinessRules.Repositories;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Logging;
using ApsTask = LPS.APS.Core.Entities.APS.Task;

namespace LPS.APS.Application.Services;

/// <summary>
/// Pegging 编排器（2号位职责）
/// </summary>
public class PeggingOrchestrator : IPeggingOrchestrator
{
    private readonly IDemandSupplyHardLockRepository _lockRepo;
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly ILogger<PeggingOrchestrator> _logger;
    private readonly IFiniteCapacityScheduler _scheduler;
    private readonly IDemandPriorityExecutor _demandPriorityExecutor;
    private readonly IDemandPriorityConfigProvider _demandPriorityConfigProvider;
    private readonly IFrozenStrategySnapshotProvider _frozenStrategySnapshotProvider;
    private readonly ITimedSupplyFactLoader _timedSupplyFactLoader;
    private readonly IProcurementManualEtaRepository _procurementManualEtaRepo;
    private readonly IProductionInstructionPositionCalculator _piPositionCalculator;
    private readonly IProductionInstructionPositionSnapshotRepository _piPositionSnapshotRepo;

    public PeggingOrchestrator(
        IDemandSupplyHardLockRepository lockRepo,
        DatabaseConnectionManager connectionManager,
        ILogger<PeggingOrchestrator> logger,
        IFiniteCapacityScheduler scheduler,
        IDemandPriorityExecutor demandPriorityExecutor,
        IDemandPriorityConfigProvider demandPriorityConfigProvider,
        IFrozenStrategySnapshotProvider frozenStrategySnapshotProvider,
        ITimedSupplyFactLoader timedSupplyFactLoader,
        IProcurementManualEtaRepository procurementManualEtaRepo,
        IProductionInstructionPositionCalculator piPositionCalculator,
        IProductionInstructionPositionSnapshotRepository piPositionSnapshotRepo)
    {
        _lockRepo           = lockRepo           ?? throw new ArgumentNullException(nameof(lockRepo));
        _connectionManager  = connectionManager  ?? throw new ArgumentNullException(nameof(connectionManager));
        _logger             = logger             ?? throw new ArgumentNullException(nameof(logger));
        _scheduler          = scheduler          ?? throw new ArgumentNullException(nameof(scheduler));
        _demandPriorityExecutor = demandPriorityExecutor ?? throw new ArgumentNullException(nameof(demandPriorityExecutor));
        _demandPriorityConfigProvider = demandPriorityConfigProvider ?? throw new ArgumentNullException(nameof(demandPriorityConfigProvider));
        _frozenStrategySnapshotProvider = frozenStrategySnapshotProvider ?? throw new ArgumentNullException(nameof(frozenStrategySnapshotProvider));
        _timedSupplyFactLoader = timedSupplyFactLoader ?? throw new ArgumentNullException(nameof(timedSupplyFactLoader));
        _procurementManualEtaRepo = procurementManualEtaRepo ?? throw new ArgumentNullException(nameof(procurementManualEtaRepo));
        _piPositionCalculator = piPositionCalculator ?? throw new ArgumentNullException(nameof(piPositionCalculator));
        _piPositionSnapshotRepo = piPositionSnapshotRepo ?? throw new ArgumentNullException(nameof(piPositionSnapshotRepo));
    }

    /// <inheritdoc />
    public async System.Threading.Tasks.Task<PeggingOrchestrationResult> ExecutePeggingWorkflowAsync(
        PeggingExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new PeggingOrchestrationResult
        {
            PlanVersionId = request.PlanVersionId,
            OrderId       = request.OrderIds.FirstOrDefault()
        };

        _logger.LogInformation(
            "[Pegging] 开始: PlanVersionId={PlanVersionId}, 订单数={OrderCount}",
            request.PlanVersionId, request.OrderIds.Count);

        // ── 运行进度（可观测性，2号位）──────────────────────────────────────────────
        //   每个阶段一行写进 `ComputeStageProgress`，后台每 2 秒刷一次心跳。
        //   为什么要有：历史上多次「跑了几十分钟不知道在干嘛」，只能登服务器 tail 日志，
        //   而日志会被轮转、上线后也没人守着。有了这张表，任意客户端一条 SQL 即可回答
        //   「算到哪一步 / 这一步多久 / 是卡住还是在算（心跳年龄）」。
        //   观测设施不参与任何判定：写库失败自动降级为仅日志，绝不影响主流程。
        await using var progress = ComputeStageProgressMonitor.Start(
            _connectionManager, _logger, request.PlanVersionId,
            request.SchedulingContext?.ScheduleRunId, request.DomainKey);
        progress.Begin("① BOM 快照装载");

        try
        {
            var bomSnapshot = await LoadBomSnapshotAsync(request.PlanVersionId, cancellationToken);
            _logger.LogInformation(
                "[Pegging] BOM 快照加载完成: PlanVersionId={PlanVersionId}, 批号={BatchNo}, 边数={EdgeCount}",
                request.PlanVersionId, bomSnapshot.BatchNo ?? "<无关联批>", bomSnapshot.EdgeCount);

            // ── BOM 输入有效性闸门（0号位 2026-10-17 §三.D）──
            //   BOM 输入缺失/技术不可用时**不得**继续：否则下游只有两条错路可选 ——
            //   要么按「无 BOM」把需求全判 Unscheduled（冤枉业务），要么拿别的批冒充本 PV 的 BOM（Q4 静默回落）。
            //   故在此 Fail-Closed，把原因写进既有 Domain/Run 错误载体（`PeggingOrchestrationResult.IsSuccess/ErrorMessage`），
            //   由 `SchedulingOrchestrator` 汇总进 `ScheduleRun` 终态。
            if (bomSnapshot.InputState != BomInputState.Available)
            {
                var bomInputReason = bomSnapshot.InputState == BomInputState.NoLinkedBatch
                    ? "本 PlanVersion 无 OrderBomRequestLink 关联批（BOM 请求身份缺失）"
                    : $"关联批 {bomSnapshot.BatchNo} 在 APS_BOM_RAW 无任何行（夜间接货未完成/失败）";

                _logger.LogError(
                    "[Pegging] BOM 输入不可用 ⇒ Fail-Closed 不进入 Pegging/求解: PlanVersionId={PlanVersionId}, 原因={Reason}",
                    request.PlanVersionId, bomInputReason);

                result.IsSuccess = false;
                result.ErrorMessage = $"BOM 输入不可用：{bomInputReason}。已按 Fail-Closed 停止（不再回落任意最新批）。";
                return result;
            }

            // ── 冻结策略快照（3号位）：Run 启动按已冻结 VersionId 装载一次，供 Supply 多键排序使用 ──
            // 缺失版本号即运行上下文不完整（与 BuildDemandSequenceMapAsync 同口径），禁止静默回退。
            var strategyProfileVersionId = request.SchedulingContext?.StrategyProfileVersionId;
            if (!strategyProfileVersionId.HasValue || strategyProfileVersionId.Value <= 0)
            {
                throw new InvalidOperationException(
                    "Supply 排序策略上下文不完整：SchedulingContext.StrategyProfileVersionId 为空。正式运行必须有冻结策略版本，禁止静默回退。");
            }
            var frozenSnapshot = await _frozenStrategySnapshotProvider
                .GetFrozenStrategySnapshotAsync(strategyProfileVersionId.Value, cancellationToken);

            // ── ⑦ Setup 换型规则（重构方案 S-5）：由 FrozenStrategySnapshotProvider 装配第⑦块 ──
            // （原物理表装载 LoadSetupTransitionRulesAsync 已移除；红线：Solver 不得运行中逐 Task 查 3号位规则库）
            progress.Begin("② 供给池装载(①-⑧ 分段在表内逐行)");
            var (supplyPool, continuityFacts) = await LoadSupplyPoolAsync(request, frozenSnapshot, bomSnapshot, cancellationToken);
            _logger.LogInformation(
                "[Pegging] 供给池装载完成: PlanVersionId={PlanVersionId}, 条目={EntryCount}",
                request.PlanVersionId, supplyPool.TotalEntries);

            // ── 步骤3：PeggingLoop BOM 遍历 + 供给扣减 ──
            progress.Begin("③ Pegging 回路(BOM 遍历)");
            var voucher = await ExecutePeggingLoopAsync(request, bomSnapshot, supplyPool, cancellationToken);
            result.Voucher = voucher;

            // ── 结果红线校验（PM 口径，前 4 项；在 Solver 与落库之前拦截）──
            var redLineErrors = ValidatePeggingResult(supplyPool, voucher);
            if (redLineErrors.Count > 0)
            {
                foreach (var e in redLineErrors)
                    _logger.LogError("[Pegging] 结果红线校验失败: {Error}", e);
                result.IsSuccess   = false;
                result.ErrorMessage = string.Join("; ", redLineErrors);
                return result;
            }

            progress.Begin("④ 构建求解请求(装载+投影)");
            var (solveRequest, overCommitEvents, peggingMs, _) =
                await BuildSolveRequestAsync(
                    request, voucher, frozenSnapshot, continuityFacts, bomSnapshot, sw, cancellationToken);

            // 求解器在 1号位 库内、**自身不输出任何进度**（历史 PV190 全量实测 414s）。
            // 这里把阶段切过去，心跳就能证明「求解器正在算」而不是「整轮卡死」。
            progress.Begin("⑤ 求解器(1号位, 无内部日志)");
            var solverSw = System.Diagnostics.Stopwatch.StartNew();
            var solveResult = await _scheduler.SolveAsync(solveRequest, cancellationToken);
            solverSw.Stop();
            Console.WriteLine($"[PeggingOrchestrator] IFiniteCapacityScheduler.SolveAsync完成: FinalTasks={solveResult.FinalTasks?.Count ?? 0}, Success={solveResult.Success}");

            // ── 求解结果可观测性（2026-10-08 新增，纯读、不改行为）────────────────────────
            // 背景：2026-10-08 真实域跑批出现「22,135 条需求进求解器 → 722s → FinalTask=0 → Success=true」的
            //   **静默空跑**。原实现只打 `FinalTasks.Count` + `Success` ⇒ 求解器**为什么**一条不产
            //   （`UnscheduledTasks.Reason` / `ExplanationFacts.ReasonCode` / `Summary.UnscheduledCount`）
            //   全部不可见，只能回 1号位 源码里猜丢弃点。
            //   与 2026-09-29「优雅失败 ErrorMessage 恒 null」同族：**失败现场必须能从日志自证**。
            //   `DomainSolveResult` 的这三个集合是 1号位 已出口的字段，2号位 只是原样打印，不解释、不推断。
            var unscheduledByReason = solveResult.UnscheduledTasks
                .GroupBy(t => string.IsNullOrEmpty(t.Reason) ? "(空原因)" : t.Reason, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .Select(g => $"{g.Count()}×{g.Key}");
            var explanationByReason = solveResult.ExplanationFacts
                .Where(f => !string.IsNullOrEmpty(f.ReasonCode))
                .GroupBy(f => f.ReasonCode, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .Select(g => $"{g.Count()}×{g.Key}");

            // 求解过程追溯（B.2）：`DomainSolveResult.SolveTraceNotes` 是 1号位 已出口的字段，
            //   此前 2号位 全仓零消费（`LPS.APS.Application`/`LPS.APS.Engine` grep 无引用）⇒ **静默丢弃**。
            //   该集合是「非排程结果」的求解过程上下文（如 SCOPE_REFERENCE_MISSING / TARGET_MISSED /
            //   SETUP_RESOLUTION），正是排不下时最需要的证据。此处按 Level+ReasonCode 聚合打印，不落库。
            var traceByCode = solveResult.SolveTraceNotes
                .GroupBy(n => $"{n.Level}:{n.ReasonCode}", StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .Select(g => $"{g.Count()}×{g.Key}");

            _logger.LogInformation(
                "[Pegging] 求解结果明细: Success={Success}, IsRoughCut={Rough}, Error={Error}, "
                + "Summary(Total={Total}, Scheduled={Scheduled}, Unscheduled={Unscheduled}, Issues={Issues}, ElapsedMs={Elapsed}) "
                + "| UnscheduledTasks 原因 Top5: {UnschedReasons} "
                + "| ExplanationFacts ReasonCode Top5: {ExplReasons} "
                + "| SolveTraceNotes 共 {TraceCount} 条，Level:ReasonCode Top5: {TraceReasons}",
                solveResult.Success, solveResult.IsRoughCut, solveResult.ErrorMessage ?? "(null)",
                solveResult.Summary.TotalDrafts, solveResult.Summary.ScheduledCount,
                solveResult.Summary.UnscheduledCount, solveResult.Summary.IssueCount, solveResult.Summary.ElapsedMs,
                unscheduledByReason.Any() ? string.Join(" | ", unscheduledByReason) : "(空)",
                explanationByReason.Any() ? string.Join(" | ", explanationByReason) : "(空)",
                solveResult.SolveTraceNotes.Count,
                traceByCode.Any() ? string.Join(" | ", traceByCode) : "(空)");

            // A1（v1.1 §六.1.3）：Task.ProductionDepartmentId 的**部门事实**来源。
            // 口径链：**ERP 物料级部门事实** → `(MaterialId, StageCode)` → `MaterialStageDeptContext(IsCurrent=1)`。
            //   · 源 = `MaterialSupplyContext.DefaultProductionDeptCode`（ERP 同步的物料级 3 位部门码）经 `ProductionDepartment` 归一化
            //     （实查 `sp_RebuildMaterialStageDeptContext`：`FROM MaterialSupplyContext … LEFT JOIN ProductionDepartment`，
            //      **无 RoutingOperation join**）⇒ 取值出自 **ERP 侧**，**非 Routing 反推、非猜测**。
            //   · 同一张表**也已经喂给 1号位** Solver（`LoadMaterialStageDeptContextAsync` → `MaterialStageDepartmentContextDto`，
            //     见该方法注释）⇒ 本节是「把 1号位 已在用的同一输入落库」，**不是另起一个兜底来源**。
            // ⚠ **在事务外装载**：零新增事务内 SQL、不拉长事务；MSC 为物料级上下文表，快照轻微偏移不影响。
            // ⚠ **`Task` 无来源列** ⇒ 落库值不可区分来源，故凡命中/未命中均留日志。
            // ⚠ **无「真值优先」分支**：`FinalTaskDraft` 现无部门成员，落库取值**恒**来自本上下文；
            //   若日后该 DTO 带出生产指示侧真值，须在落库点先判真值非空（见该处注释），否则会静默覆盖真值。
            var deptCtxByMaterialStage =
                await LoadMaterialStageDeptContextAsync(
                    solveResult.FinalTasks.Select(t => t.MaterialId).Distinct().ToList(), cancellationToken);

            // J17-04（0号位 第十七轮／第十八轮 KEY_SCOPE_RISK）：本域**实际交给 Solver 的**非资源工序键集
            //   （`UNCONSTRAINED` / `WAIT_ONLY`，0号位 2026-09-22 裁决）⇒ 该工序的 Task 允许无资源落库；
            //   其余工序（`FINITE_RESOURCE`）若 `ResourceId` 为空，一律按非法资源隔离，不得落 NULL 冒充已分配。
            //
            // ⚠ 键 = **完整工序身份**（Material + Stage + Route + Path + OperationCode），不再用
            //   `(MaterialId, OperationCode)` 短键：冻结的工序业务身份含 Route/Path/Stage，`OperationCode`
            //   不具全局唯一性 ⇒ 短键会把「另一路合法非资源工序」的模式错套到有限资源工序的 Task 上。
            //   实测（2026-10-17 14:48，`RoutingOperation` IsActive=1 共 1,701,834 行）：全表与 PV2 物料集内
            //   异模式碰撞均为 0，故本次加宽**零行为变更**（`UNCONSTRAINED` 仅 1 行、`WAIT_ONLY` 0 行，且该行不在 PV2）。
            var nonResourceOperations = solveRequest.RoutingOperations
                .Where(o => IsNonResourcePlanningMode(o.OperationPlanningMode))
                .Select(o => (o.MaterialId, StageCode: o.StageCode ?? string.Empty,
                              RouteCode: o.RouteCode ?? string.Empty, PathId: (long)o.PathId, o.OperationCode))
                .ToHashSet();

            progress.Begin("⑥ 结果落库(统一事务)");
            var persistSw = System.Diagnostics.Stopwatch.StartNew();
            (result.GeneratedTasks, result.PhysicalPeggingCount, result.SupplyAllocationCount) =
                await PersistDomainAndPeggingInTransactionAsync(
                    request.PlanVersionId, voucher, solveResult, overCommitEvents, cancellationToken,
                    deptCtxByMaterialStage, nonResourceOperations, supplyPool);
            persistSw.Stop();
            _logger.LogInformation(
                "[Pegging] 统一事务落库: Task={Tasks}, Pegging={Pegging}, SupplyAllocation={Alloc}",
                result.GeneratedTasks.Count, result.PhysicalPeggingCount, result.SupplyAllocationCount);

            // 3号位 2026-10-25 缺配三态：同值同源另打一行日志，便于不查库就看出兜底占比。
            // 口径＝物料去重；「重复」为整份快照 PlanningYields 的同 (MaterialId, StageCode) 多规则键数。
            _logger.LogInformation(
                "[Pegging][计划良率三态] 查询物料={Queried}, 缺配={Missing}, 越界={OutOfRange}, 重复键={Dup}",
                supplyPool.YieldDiagnostics.QueriedCount,
                supplyPool.YieldDiagnostics.MissingCount,
                supplyPool.YieldDiagnostics.OutOfRangeCount,
                supplyPool.PlanningYieldDuplicateKeyCount());

            sw.Stop();
            // 【2026-10-08 修复】原为 `result.IsSuccess = true;` **无条件置真** ⇒ 求解器返回
            //   `Success=false`（1号位 语义 = **技术失败**：Routing 非法 / 数量闭合错误 / 硬资源约束破坏）
            //   时，本编排仍报成功；上层 `ExecuteDomainAsync` 的 `isSuccess = peggingFailed.Count == 0`
            //   因此恒真 ⇒ **PV 被标 `Computed`、ETL 记「失败:0」、库里查不到任何失败痕迹**。
            //   2026-10-08 实测即栽在此：PV2 22,135 条需求、求解 722s、FinalTask=0，而 ETL 写的是「失败:0」，
            //   只能回 1号位 源码里猜丢弃点（`ErrorMessage` 也一并被丢）。
            //   现口径：**Pegging 编排成功 且 求解器未报技术失败** ⇒ IsSuccess=true。
            //   ⚠️ 不改变业务语义：1号位 明示「产能不足 / 排不下」属**业务结果**，走 `Success=true`
            //      + `UnscheduledTasks` 出口，**不会**在此被判失败；此处只兜技术失败。
            result.IsSuccess = solveResult.Success;
            result.ErrorMessage = solveResult.Success
                ? null
                : string.IsNullOrWhiteSpace(solveResult.ErrorMessage)
                    ? "求解器返回 Success=false（技术失败）但未提供 ErrorMessage"
                    : $"求解器技术失败：{solveResult.ErrorMessage}";

            // 【2026-10-23 · C1 取证】`Success=true`（业务未排下）时，1号位 仍会在
            //   `DomainSolveResult.ErrorMessage` 里带一段 `[BatchPolicy] …`（`PhaseFiveCompression.ComposeErrorMessage`）：
            //   三类出口码计数 + 至多 3 条 `[{LogicalDemandKey}] {ConflictReason}` 样本（如 `PI_LEGAL_QTY_OVER_LIMIT`
            //   / `PI_LEGAL_QTY_CONTRACT_PENDING：…`）。上面那行**按既有语义置 null**，该段原样丢弃 ⇒
            //   `BATCH_POLICY_CONFLICT` 在库里只剩一个码，回不出 0号位「按真实执行分支计数」的取证。
            //   此处**不改 `ErrorMessage` 语义**，只把被丢弃的这段写进 `APS_ETL_Log` 留痕（`Message` 为 nvarchar(max)）；
            //   写失败不影响主流程。
            // 【2026-10-25 收严】原条件是 `Success && ErrorMessage 非空` ⇒ **失败态一条都不落**，
            //   而 Phase5 硬校验失败（E3，`FinalTasks` 整组清零、`UnscheduledCount` 保留）恰恰是最需要现场的那条出口。
            //   1号位 2026-10-25《FAMILY_X 四项对账》§2.4 请的三个数（`ErrorMessage` 首行 / `Summary` 两计数 /
            //   `SolveTraceNotes.Count`）之所以一个都取不到，就是因为这三样从未随失败态落库。
            //   现改为：**每域每 Run 落一行**（成功/失败都落），一行内含出口判据所需的全部三项。
            static string FirstLine(string? text)
            {
                if (string.IsNullOrWhiteSpace(text)) return string.Empty;
                var idx = text!.IndexOfAny(new[] { '\r', '\n' });
                var line = idx > 0 ? text.Substring(0, idx) : text;
                return line.Length <= 300 ? line.Trim() : line.Substring(0, 300).Trim() + "…";
            }

            try
            {
                var summary = solveResult.Summary;
                var detailMessage =
                    $"[{request.DomainKey}] Success={solveResult.Success}"
                    + $" | Scheduled={summary?.ScheduledCount ?? 0} Unscheduled={summary?.UnscheduledCount ?? 0}"
                    + $" Issues={summary?.IssueCount ?? 0} TraceNotes={(solveResult.SolveTraceNotes?.Count ?? 0)}"
                    + (string.IsNullOrWhiteSpace(solveResult.ErrorMessage)
                        ? string.Empty
                        : $" | Error={FirstLine(solveResult.ErrorMessage)}");

                await _connectionManager.ExecuteAsync(
                    @"INSERT INTO APS_ETL_Log (BatchNo, Step, Message, Status, CreatedAt)
                      VALUES (@BatchNo, N'Pegging.SolveDetail', @Message, @Status, GETDATE())",
                    new
                    {
                        BatchNo = request.PlanVersionId.ToString(),
                        Message = detailMessage,
                        Status = solveResult.Success ? "INFO" : "ERROR",
                    },
                    db: DatabaseId.APS);
            }
            catch (Exception detailEx)
            {
                _logger.LogWarning(detailEx,
                    "[Pegging] 求解明细落 APS_ETL_Log 失败（不影响主流程）: PlanVersionId={PlanVersionId}",
                    request.PlanVersionId);
            }

            // 【2026-10-23 · C1 分支计数】1号位 2026-10-22 回执 §3.2 已落码：每个硬失败需求另产一条
            //   `SolveTraceNote`（`Key`=LogicalDemandKey／`ReasonCode`=出口码／`Message`=明细正文），明细前缀共 6 种
            //   （`PI_LEGAL_QTY_CONTRACT_PENDING：`／`BATCH_POLICY_CONFLICT：批量策略配置无效`／`BATCH_POLICY_INVALID：`
            //    ／`BATCH_POLICY_STATE_UNAVAILABLE：`／`BATCH_POLICY_MISSING：`／`B010_PI_QUANTITY_CONTRACT_PENDING：`）。
            //   本编排此前只把这些 note 打 Top5 日志、**不落库** ⇒ 库里只剩出口码，分支回不出来（0号位 要求「按真实执行
            //   分支计数」）。`ExplainTrace` 不能用（`TaskId NOT NULL`，而硬失败需求没有 Task），故落 `APS_ETL_Log`：
            //   按 (Level, ReasonCode, 明细前缀) **聚合**，每域至多 MAX_TRACE_GROUPS 行，每行带条数与至多 3 个样本键。
            if (solveResult.SolveTraceNotes.Count > 0)
            {
                const int maxTraceGroups = 50;
                const int sampleKeys = 3;
                const int prefixMaxChars = 40;
                const int fullTextMaxChars = 800;

                static string DetailPrefix(string? message)
                {
                    if (string.IsNullOrWhiteSpace(message))
                    {
                        return "(无正文)";
                    }

                    var text = message!;

                    // 1号位 现行写法已把 `[LogicalDemandKey] ` 前缀剥掉再进 `Message`（`PhaseFiveCompression.IndexBatchPolicyDetail`），
                    //   这里再兜一层：万一将来原样带出，方括号里的需求键会把每个分组切成一条，聚合失效。
                    if (text.Length > 2 && text[0] == '[')
                    {
                        var close = text.IndexOf(']');
                        if (close > 1 && close + 1 < text.Length)
                        {
                            text = text.Substring(close + 1).TrimStart();
                        }
                    }

                    var sep = text.IndexOfAny(new[] { '：', ':' });
                    if (sep > 0 && sep <= prefixMaxChars)
                    {
                        return text.Substring(0, sep + 1);
                    }

                    return text.Length <= prefixMaxChars ? text : text.Substring(0, prefixMaxChars) + "…";
                }

                // 正文样本：保留**整条** `Message`（只在尾部截断防超长），因为出口标签与它的明细都在分隔符之后。
                static string TruncateFullText(string? message)
                {
                    if (string.IsNullOrWhiteSpace(message)) return "(无正文)";
                    var text = message!.Trim();
                    return text.Length <= fullTextMaxChars ? text : text.Substring(0, fullTextMaxChars) + "…";
                }

                var traceGroups = solveResult.SolveTraceNotes
                    .GroupBy(n => (n.Level, n.ReasonCode, Prefix: DetailPrefix(n.Message)))
                    .Select(g => new
                    {
                        g.Key.Level,
                        g.Key.ReasonCode,
                        g.Key.Prefix,
                        Count = g.Count(),
                        Samples = string.Join(", ",
                            g.Select(n => n.Key)
                             .Where(k => !string.IsNullOrEmpty(k))
                             .Distinct(StringComparer.Ordinal)
                             .Take(sampleKeys)),
                        // `Message` 的**全部诊断信息都在第一个分隔符之后**（1号位 2026-10-25 起：
                        //   `Phase2 出口=P2_xxx：<内层子出口 / Stage / OperationCode / 合格资源数 / 累计找槽数>`）。
                        //   只留前缀等于把答案切掉，聚合行就退化成「知道有这一类、不知道是哪一条」⇒ 每组再留一条正文。
                        FullText = TruncateFullText(
                            g.Select(n => n.Message).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m))),
                    })
                    .OrderByDescending(g => g.Count)
                    .Take(maxTraceGroups)
                    .ToList();

                foreach (var grp in traceGroups)
                {
                    try
                    {
                        await _connectionManager.ExecuteAsync(
                            @"INSERT INTO APS_ETL_Log (BatchNo, Step, Message, Status, CreatedAt)
                              VALUES (@BatchNo, N'Pegging.SolveTraceBranch', @Message, N'INFO', GETDATE())",
                            new
                            {
                                BatchNo = request.PlanVersionId.ToString(),
                                Message = $"[{request.DomainKey}] {grp.Level}/{grp.ReasonCode}"
                                        + $" | 前缀={grp.Prefix} | 条数={grp.Count}"
                                        + (grp.Samples.Length > 0 ? $" | 样本键={grp.Samples}" : "")
                                        + $" | 正文样本={grp.FullText}",
                            },
                            db: DatabaseId.APS);
                    }
                    catch (Exception traceEx)
                    {
                        _logger.LogWarning(traceEx,
                            "[Pegging] SolveTraceNote 分支落 APS_ETL_Log 失败（不影响主流程）: PlanVersionId={PlanVersionId}",
                            request.PlanVersionId);
                        break;   // 库写不通就不再逐组重试
                    }
                }
            }
            result.ExecutionTimeMs = sw.ElapsedMilliseconds;
            result.PeggingMs      = peggingMs;
            result.SolverMs       = solverSw.ElapsedMilliseconds;
            result.PersistMs      = persistSw.ElapsedMilliseconds;

            _logger.LogInformation(
                "[Pegging] 完成: PlanVersionId={PlanVersionId}, Task={Tasks}, 分配={Alloc}, 耗时={Ms}ms",
                request.PlanVersionId, result.GeneratedTasks.Count,
                result.SupplyAllocationCount, result.ExecutionTimeMs);

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            // 进度表留一行 FAILED：事后不必翻日志就知道停在哪一阶段、什么异常。
            progress.Fail($"{ex.GetType().Name}: {ex.Message}");
            _logger.LogError(ex, "[Pegging] 编排异常: PlanVersionId={PlanVersionId}", request.PlanVersionId);
            Console.WriteLine($"[PeggingOrchestrator] 捕获异常: {ex.Message}");
            Console.WriteLine($"[PeggingOrchestrator] 异常堆栈: {ex.StackTrace}");
            result.IsSuccess      = false;
            result.ErrorMessage   = ex.Message;
            result.ExecutionTimeMs = sw.ElapsedMilliseconds;
            return result;
        }
    }
    ///
    /// <inheritdoc />
    /// <summary>
    /// 组装交给 1号位 的 <see cref="DomainSolveRequest"/>（**Solver 之前的全部装载**，2026-10-09 抽出）：
    /// 连续性分桶（Continuation/Free + E&gt;Q 超量登记）→ Routing 三件套（按 (MaterialId,StageCode) 过滤，不混载）
    /// → 部门归属上下文 → StartStageCode/RequiredStageCode 填值 → 无工序阶段提前期 → 有效 Stage 链
    /// → 候选上下文/RunScope → 求解参数投影。
    ///
    /// 抽出的目的：给「装载给 1号位」一个**可独立调用的验收入口**
    /// （<see cref="RunPeggingOnlyAsync"/>），使整块载荷能被逐项核对，而不必调用 Solver 或落库。
    /// 与 <c>ExecutePeggingWorkflowAsync</c> 共用同一段代码，**杜绝验收路径与生产路径分叉**。
    /// </summary>
    private async System.Threading.Tasks.Task<(
        DomainSolveRequest SolveRequest,
        List<ContinuityOverCommitEvent> OverCommits,
        long PeggingMs,
        IReadOnlyList<LogicalProductionDemand> PreBucketingDemands)>
        BuildSolveRequestAsync(
            PeggingExecutionRequest request,
            PeggingResultVoucher voucher,
            FrozenStrategySnapshot frozenSnapshot,
            ContinuityFacts continuityFacts,
            BomSnapshot bomSnapshot,
            Stopwatch sw,
            CancellationToken cancellationToken)
    {
        // 【2026-10-26 埋点】④ 构建求解请求 整段 PV190 实测 57,878ms，此前**零内部计时点**，无从判断钱花在哪。
        // 只写日志、**不调用 `ComputeStageProgressMonitor`**（阶段数对外可见，不因观测而增加阶段数）。
        // 纯观测：不改任何取值、不改执行顺序、不改分块大小。
        var swBuild = System.Diagnostics.Stopwatch.StartNew();
        long buildLastMs = 0;
        void MarkBuild(string name)
        {
            var nowMs = swBuild.ElapsedMilliseconds;
            _logger.LogInformation("[Pegging][求解请求分段] {Name}={Delta}ms (累计 {Total}ms)",
                name, nowMs - buildLastMs, nowMs);
            buildLastMs = nowMs;
        }

        // ── DemandGoal 回填（2026-10-13）：需求级业务目标 → LogicalProductionDemand.DemandGoal ──
        // 必须在**分桶之前**：`CloneDemandSlice` / `CloneDemandRescaled` 均原样继承 `DemandGoal`
        //   （同一切片血缘不变），先填则两片自动带值，无需在分桶里再补。
        await FillDemandGoalsAsync(voucher, cancellationToken);
        MarkBuild("1 DemandGoal 回填");

        // ── 数量权威回填（1号位 2026-10-22 回执 §四）：需求级「数量由 PI 授权还是由订单授权」──
        // 同样必须在**分桶之前**：`CloneDemandSlice` / `CloneDemandRescaled` 逐项继承该字段，
        //   先填则两片自动带值；放到分桶后会出现「源需求有值、切片 null」⇒ 1号位 侧汇总登记缺失。
        FillQuantityAuthority(voucher, continuityFacts);
        MarkBuild("2 数量权威回填");

        // ── 需求锚点回填（J14-02）：需求 → OrderId → OrderBomRequestLink(ResolvedBOMNO / RequestDetailId / RepWorksetId) ──
        // 必须在**分桶之前**：切片克隆（`CloneDemandSlice`／`CloneDemandRescaled`）逐项显式继承锚点字段，
        //   先填则两片自动带值；若放到分桶后，只有源需求有值、切片全是 null ⇒ Stage 装载又退回「不做请求收窄」。
        await FillDemandRequestAnchorsAsync(voucher.LogicalProductionDemands, request.PlanVersionId, cancellationToken);
        MarkBuild("3 需求锚点回填");

        // ── 跨版本连续性分桶（PM 0914 红线）：先形成的 Q 按 IN_PROGRESS 既存执行切 Continuation/Free ──
        // Q 不因 WIP 供给减小；被覆盖份额以 Continuation Slice（IsContinuation=true）＋自由份额 Free Slice
        // 一起进 1号位（1号位必须看到 Continuation E + Free (Q−E)，不得只看到 Free）。E 来源 = StageProgressSnapshot.RemainingQty。
        // 分桶**前**的源需求快照：分桶是「一进多出」，守恒性（Σ片Q == 源Q）只能拿它做基准核验，
        // 探针靠它验「分桶只切不增」（`RunPeggingOnlyAsync` 出口带回；生产路径不使用、零行为影响）。
        var preBucketingDemands = voucher.LogicalProductionDemands;
        var (bucketedDemands, overCommitEvents) = ApplyContinuityBucketing(
            voucher.LogicalProductionDemands, continuityFacts);
        voucher.LogicalProductionDemands = bucketedDemands;
        MarkBuild($"4 连续性分桶(片={bucketedDemands.Count})");

        // v5.1.2架构整改：不再预先生成TaskDrafts，改为传递LogicalProductionDemands给1号位
        // 1号位基于LogicalProductionDemands生成FinalTasks（含拆批/合批决策）
        _logger.LogInformation("[Pegging] 准备传递LogicalProductionDemands给Solver: {Count} 个",
            voucher.LogicalProductionDemands.Count);

        // ── 装载 Routing 三件套 + 部门归属上下文（PM 裁定：最小 B）──
        // 【2026-09-28 改造】按 PM《BOM取用_Pegging_Stage_Routing完整链路说明》§二十一/§二十二：
        //   Stage 的存在性与顺序由 `APS_BOM_STAGE_PATH_RAW`（本次 BOM 上下文）决定，**不得从 Routing 反推**；
        //   Routing 只回答「该 Stage 内部有哪些小工序」⇒ 须按 (MaterialId, StageCode) 过滤装载。
        //   原实现 `WHERE MaterialId IN @Ids` 会把该物料所有 Stage 的工序全捞进来（PM 指出的「混载」）。
        var demandMaterialIds = voucher.LogicalProductionDemands
            .Select(d => d.MaterialId)
            .Distinct()
            .ToList();

        // 本次 BOM 上下文下的有效 Stage：MaterialId → 该物料本次需经过的 StageCode（**每物料恰好一条真实链**）。
        //   2026-10-16 J12-02：原口径「ROOT ∪ EDGE 并集」已废除（违反 `07 v5.1.13 :416`／`:722`，见方法注释）；
        //   现按 `StageScopeType` 隔离 —— 作为子件被消耗的物料取 EDGE、须自身完工的取 ROOT（`07 :724` 两式）。
        //   2026-10-17 J14-01/02/03：入参改为**需求本身**（角色由 `IsRootDemand` 决定）＋ BOM 快照（按请求的父边索引），
        //   逐需求按 `BomNo`／`RepWorksetId`／父边选行；首选 scope 无行即无链，不回退。
        var effectiveStages = await LoadEffectiveStageKeysAsync(
            request.PlanVersionId, voucher.LogicalProductionDemands, bomSnapshot, cancellationToken);
        MarkBuild("5 有效Stage装载");

        // 有效 Stage 的**输入事实缺失**不得静默（0号位 2026-10-17 §三.D）：
        //   装载结果保留真实的 `UnavailableReason` 与结构化 Issue，此处逐条落到日志；
        //   BOM 关联批缺失这一支已在 `ExecutePeggingWorkflowAsync` 的输入闸门 Fail-Closed，此处覆盖其余缺失类。
        if (!effectiveStages.IsAvailable)
        {
            _logger.LogError(
                "[Pegging] 有效 Stage 输入事实缺失：{Reason}（PlanVersionId={PlanVersionId}，诊断 {IssueCount} 条：" +
                "{IssueCodes}）⇒ 下游 Stage 过滤按既有「物料未命中即不过滤」口径放行，该结果不得视为已完成 BOM 上下文校验",
                effectiveStages.UnavailableReason, request.PlanVersionId,
                effectiveStages.Issues.Count,
                string.Join(",", effectiveStages.Issues.Select(i => i.ReasonCode).Distinct()));
        }

        var (routingOperations, routingDependencies, operationResourceEligibility) =
            await LoadRoutingContextAsync(
                demandMaterialIds, effectiveStages.ByMaterial, effectiveStages.RejectedMaterials, cancellationToken);
        MarkBuild("6 Routing三件套+资质");

        var materialStageDeptContexts =
            await LoadMaterialStageDeptContextAsync(demandMaterialIds, cancellationToken);
        MarkBuild($"7 部门上下文(物料={demandMaterialIds.Count})");

        // ── ⑧ 批量策略：装载侧**表直读**（2026-10-17 切换；PM 2026-10-15 §T-1038）──────────────
        // 旧路径 = ParameterSetVersion.ContentSnapshotJson.BatchPolicy 子块（发布侧自 2026-10-15 起停止注入）。
        // 新路径 = 本域需求物料 → TaskSplitRuleConfig **一条集合式 SELECT**（RCSI 语句级一致读；不建 #temp、不包事务）。
        // 生效时点 = **本域 Batch 装载开始时刻的库时间**（`DomainBatchEvaluationTime`），不再用 Run 冻结时点。
        var batchPolicy = await LoadBatchPolicyRulesAsync(demandMaterialIds);
        MarkBuild("8 批量策略");

        // ③ StartStageCode 填值（2026-09-11，5号位 O3 回复划归 2号位）：Routing 图「无入边源结点」的
        // 大工艺阶段码 → LogicalProductionDemand.StartStageCode（原 BuildLogicalProductionDemand 写空）。
        FillStartStageCodes(voucher, routingOperations, routingDependencies);
        MarkBuild("9 StartStage填值");

        // ③c 连续份额路径身份**只诊断、不回填**（2026-10-22 拆桥）。
        //
        // 1号位 `PhaseTwoInitialScheduler` 对 `IsContinuation` 需求硬 Fail Closed（`ContinuationKey / RouteCode /
        //   PathId / StartOperationCode / NoSplitMerge` 缺任一 ⇒ 记 `UnscheduledDemandKeys`、不产 Task）。
        //   缺真路径的片因此**不进 Task**，这是 0号位 2026-10-22 §五(一) 要求的终态：不得用 `DEFAULT / 1`
        //   填假身份，也不得把缺真路径的 A/B 当普通 C 桶重新选路。冻结 `11_2号位v2.1` 红线 Q1 同向：
        //   `DEFAULT / 1` 不得作为归一化后的业务真值；A/B 必须发固定真实 Route/Path。
        //   真值来源唯一：5号位 按 slice 交付（唯一命中才交付、歧义/无匹配留 null），我方原样消费、不补默认值。
        // 运行期留痕：一条统计日志（已带真值 / 缺 RouteCode / 缺 PathId / 物料无路由 / 载荷多条路径样本）。
        DiagnoseContinuationRouteIdentities(voucher, routingOperations, _logger);

        // ④ 数量单位回填（2026-10-09，P1-08 方案a 取值源修正）：需求**自身物料**的 Material.UOM
        //   → LogicalProductionDemand.UOM。原实现从不在 BuildLogicalProductionDemand 里赋值 ⇒ PV2 实测
        //   96,627/96,627 条需求 UOM=null ⇒ 1号位各处 `demand.UOM ?? string.Empty` ⇒ **每个 Task 的 UOM 恒为空串**。
        await FillDemandUomAsync(voucher, cancellationToken);
        MarkBuild("10 UoM回填");

        // ③b 供给阈值 Stage 填值（PM《BOM取用…完整链路说明》§八，2026-09-28）：BOM 边
        //   `ChildRequiredStageCode`（主源，已随 bomSnapshot 按本批上下文装载）→ 兜底 StagePath `IsSupplyThreshold=1`（按本批）
        //   → LogicalProductionDemand.RequiredStageCode。
        //   语义：「子件做到该 Stage 以后才成为父件可使用的 Supply」⇒ 该需求做到此阶段即为终点。
        await FillRequiredStageCodesAsync(voucher, bomSnapshot, request.PlanVersionId, cancellationToken);
        MarkBuild("11 供给阈值Stage回填");

        // ── 无 Routing 阶段的提前期（PM《无Routing Stage统一处理建议》§九：2号位 装载 → 1号位 消费）──
        // StagePath 决定阶段存在性；Routing 不存在 ≠ Stage 不存在 ⇒ 对「有效 Stage 但零工序」的
        // (物料, 阶段) 解析 StageLeadTimeParam 提前期，交由 1号位 保留该阶段的时间与前后依赖。
        //   部门码复用上一行已整批装好的 `materialStageDeptContexts`（其物料集 = 本 Domain 需求物料，
        //   是 gapPairs 物料的超集）——**不在循环内逐对重查**（原为 N+1，见方法内注释）。
        var stageLeadTimes = await LoadStageLeadTimesAsync(
            effectiveStages.ByMaterial, routingOperations, voucher.LogicalProductionDemands,
            materialStageDeptContexts);
        MarkBuild("12 无Routing阶段提前期");

        if (demandMaterialIds.Count > 0 && routingOperations.Count == 0)
        {
            _logger.LogWarning(
                "[Pegging] Routing 三件套为空（需求物料数={MaterialCount}），1号位将把所有新增生产需求判定为 Unscheduled；请确认 routing-sync（00:25）已灌入数据",
                demandMaterialIds.Count);
        }

        // ── 求解参数：由冻结 SolverStrategy 块投影，不再硬编码（Q9 修复，2026-09-04）──
        // Mode → Direction 1:1（固定映射，见 SolverStrategyModeMap）；AllowSplit 由 Split.MaxOptimizationSplitCount>1 推；
        // AllowMerge 由 3号位 补源字段直接投影（2026-09-04 回执认可口径，零行为变化）。
        // P1-02 B 组：ImpactedTaskWarningPercent/MaxPropagationRounds/SplitAlternatives/MinBatchQty 直接投影
        // （3号位 已冻结；字段先行，1号位 换读后逐项激活，零行为变化）。
        var solverStrategy = frozenSnapshot.SolverStrategy;
        var schedulingDirection = SolverStrategyModeMap.ToDirection(solverStrategy.Mode);

        // P0-04：CandidateContext（FULL 为 null）。Base 锚点 = request.BasePlanVersionId（3号位冻结），
        // ChangeSeed/ExternalDomainResourceBlocks 由 BuildCandidateContextAsync 计算/透传。
        var candidateContext = await BuildCandidateContextAsync(request, voucher);
        MarkBuild("13 候选上下文");

        // ScopeJsonV2 → RunScope 投影（M2；null = FULL 零改动）
        var runScope = await BuildRunScopeAsync(request, voucher, candidateContext?.BasePlanVersionId, cancellationToken);
        MarkBuild("14 运行范围");

        // T-04 契约①：硬锚点装载（上一 ACTIVE 的 Frozen Task → ExecutionConstraint）。
        // 必须在分桶之后调用（voucher.LogicalProductionDemands 此时已是本 Run 的最终需求集，
        // 其 LogicalDemandKey 才是 1号位 认可的 DraftId）。
        // T-04：一次装载同时得两条契约 —— ①硬锚点（Frozen 窗口/执行事实，锁位置）②订单保护事实（Firm，只通报）。
        var anchorPayload = await BuildAnchorPayloadAsync(request, voucher);
        MarkBuild("15 锚点+订单保护装载");

        // 资源定义只构建一次：`Resources` 与 `DeviceResourceIds`（R-2 留痕增强）共用同一份，
        //   避免重复走 G-16 的 `int.TryParse` 告警路径（同一批资源会被告警两遍）。
        var resourceDefinitions = BuildResourceDefinitions(request.SchedulingContext);

        // ── G-2：工厂 / 生产部门级**生效 WORK 窗口**投影（1号位 2026-10-25 核证回执 §五 点名）──
        //   1号位 推进「无 Routing 的 Stage」时只需要「该 Stage 所属部门当天有多少合法工时」；
        //   `CalendarSlots` 只有资源级、工厂/部门作用域在投影前被抹掉 ⇒ 由我方按 `ProductionWorkCalendarSlot`
        //   合成后单独给一份。**合成规则与资源三级日历同一条**（RC-001：2号位 合成、1号位 只消费）。
        //   计划期与 `PlanningStart/End` **同源同值**（下面直接复用），避免两处各算一遍产生漂移。
        var planningStart = request.SchedulingContext != null && request.SchedulingContext.PlanHorizonStart != default
            ? request.SchedulingContext.PlanHorizonStart
            : (request.SnapshotAt == default ? DateTime.Now : request.SnapshotAt);
        var planningEnd = request.SchedulingContext != null && request.SchedulingContext.PlanHorizonEnd != default
            ? request.SchedulingContext.PlanHorizonEnd
            : DateTime.Now.AddDays(90);

        // 厂级 scope 的来源取**并集**：部门所属工厂 ∪ 本域需求工厂 ——
        //   后者兜住「该 Stage 没有部门上下文、但有厂级事实」的工厂，否则 1号位 按 (厂, null) 会查空、误判成无工时。
        var demandFactoryIds = voucher.LogicalProductionDemands
            .Select(d => d.FactoryId)
            .Where(f => f > 0)
            .Distinct()
            .ToList();

        // 上界必须与资源日历**同一个常量**（`SchedulingHorizonPolicy.SoftBoundaryCalendarBufferDays`）。
        //   此前传裸 `planningEnd` ⇒ G-2 比 `CalendarSlots` 短 180 天：同一条需求里前段有 Routing 能排到 +180，
        //   后段无 Routing 到 +90 就「无工时」，出口会在第 90 天附近集中出一批「排不下」，根因却是投影截断
        //   （1号位 2026-10-25 G-2 回执 §四）。
        //   ⚠ 只抬「日历/工时投影」的上界；载荷里的 `PlanningStart/PlanningEnd` 仍是 90 天求解窗口，两者不混。
        var departmentWorkWindowProjection = await LoadDepartmentWorkWindowsAsync(
            materialStageDeptContexts, demandFactoryIds,
            planningStart, planningEnd.AddDays(SchedulingHorizonPolicy.SoftBoundaryCalendarBufferDays));
        MarkBuild("16 部门WORK窗口投影");

        // Stage 顺序事实（与 `StageSequenceChains` 同一份 `OrderedChains`）——G-1 与它必须同源，
        //   否则「1号位 拿到的 StageCode 集合」与「我方算量的 StageCode」会分叉。
        var stageSequenceChains = BuildEffectiveStagePaths(effectiveStages.OrderedChains, materialStageDeptContexts);

        // G-1：逐 Stage 计划加工量（1号位 2026-10-25 核证回执 §五 点名；成因见 DTO 注释）。
        //   只**增**表，不改 `PlannedProcessQty` / `NetOutputQty` / PI 供给 ⇒ 整表为空时零回归。
        var stageProcessQuantities = await BuildStageProcessQuantitiesAsync(
            voucher.LogicalProductionDemands, stageSequenceChains, frozenSnapshot, cancellationToken);
        MarkBuild($"17 逐Stage加工量投影(行={stageProcessQuantities.Count})");

        var solveRequest = new DomainSolveRequest
        {
            ScheduleRunId = request.SchedulingContext?.ScheduleRunId,
            PlanVersionId = request.PlanVersionId,
            DomainKey     = request.DomainKey,
            DataCutoffTime = request.SnapshotAt == default ? DateTime.Now : request.SnapshotAt,
            // P0-03：PlanningStart/End 是有限产能求解窗口 = PlanVersion 的 90 天边界（PlanHorizonStart/End），
            // 与「FrozenWindowEnd(now+2h 滑动冻结窗)」解耦；冻结窗是约束（P0-05），不再充当 Solver Horizon。
            PlanningStart = planningStart,
            PlanningEnd = planningEnd,

            LogicalProductionDemands = voucher.LogicalProductionDemands,
            MaterialRequirementLinks = voucher.MaterialRequirementLinks,
            AllocationLineage = BuildAllocationLineage(voucher),

            // AUD-1-D04 / 1号位 §1.3（2026-10-12）：PI 级 ERP 权威剩余事实投影。
            // 事实集已由 LoadPiRemainingFactsAsync 按 B-01 身份闸门产出、一个 PI 一条；此处原样透传。
            PiRemainingFacts = continuityFacts.PiRemainingFacts,

            // 1号位 §1.4(a)（2026-10-12）：Stage **入口基线自由量**投影（B-009 门禁后两项的唯一上游）。
            // 只在分桶**之后**构建——须按 IsContinuation 排除 A/B（V1_4 NEW-02 门禁不适用），
            // 故取 voucher.LogicalProductionDemands（本方法开头已被 ApplyContinuityBucketing 覆写）。
            PiStageQuantityBaselines = BuildPiStageQuantityBaselines(voucher.LogicalProductionDemands, continuityFacts),

            RoutingOperations = routingOperations,
            RoutingDependencies = routingDependencies,
            OperationResourceEligibility = operationResourceEligibility,
            MaterialStageDepartmentContexts = materialStageDeptContexts,
            StageLeadTimes                 = stageLeadTimes,
            // Stage 顺序事实（PM §七 权威 = StageSeq；1号位 2026-09-28 回执 §六 第 2 项同意先落）。
            // 每个物料一条完整有序链、每步带 StageSeq 数值 + ProductionDepartmentId
            // —— 即 PM《…接口裁决回复》2026-09-28 §三 命名的 `EffectiveStagePath` 业务事实
            //（MaterialId + StageCode + StageSeq + ProductionDepartmentId 四条一组）。
            StageSequenceChains            = stageSequenceChains,
            // G-1：逐 Stage 计划加工量（键 = LogicalDemandKey × StageCode）。
            //   空表 = 「本轮未投影或本域无可投影需求」⇒ 1号位 沿用需求级 `PlannedProcessQty`，零行为变化。
            StageProcessQuantities         = stageProcessQuantities,

            MaterialConstraints = BuildMaterialConstraints(voucher),

            Resources     = resourceDefinitions,
            // R-2「**设备**有效稼动」的留痕增强（1号位 2026-10-16 回执 §四 点名载体 `DeviceResourceIds`）。
            //   资源集合**含**人工槽合成键（`SchedulingOrchestrator` 装载时按 `1e9 + ManualSlotId` 注入），
            //   故单一排序键区分不出「含人工槽」与「纯设备」两个口径 ⇒ 由我方给出纯设备白名单。
            //   ⚠ 无 `SchedulingContext` ⇒ **null（未投影）**，不是空集 —— 空集是「已投影但无纯设备」，有信息量。
            //   默认行为不变：1号位 非 null 时算两个键、**默认仍取含人工槽那个**（其回执 §四 逐字）。
            DeviceResourceIds = request.SchedulingContext == null
                ? null
                : SolverResourceProjection.SelectDeviceResourceIds(resourceDefinitions.Select(r => r.ResourceId)),
            CalendarSlots = BuildResourceCalendarSlots(request.SchedulingContext),
            // G-2：工厂 / 部门级**生效** WORK 窗口（已合成、已按本域部门裁剪、已摊平到计划期）。
            //   与 `CalendarSlots` 并存不替换：后者只用于资源可排性，本字段只用于无 Routing Stage 的工时累计。
            DepartmentWorkWindows = departmentWorkWindowProjection.Windows,
            // 只读诊断位（1号位 2026-10-25 G-2 回执 §五 乙）：这两种「查不到」是**数据侧**缺陷，
            //   按 RC-007 落到「该 scope 该日无工时」即可，不阻断 Run；不单列就会被记成「无工时」。
            DepartmentWorkWindowConflictDayCount = departmentWorkWindowProjection.ConflictDayCount,
            DepartmentsWithoutFactoryCount = departmentWorkWindowProjection.DepartmentsWithoutFactoryCount,
            ResourceEligibility = Array.Empty<ResourceEligibilityDefinition>(),
            // T-04 契约①：本域上一 ACTIVE 的硬锚点（Frozen 窗口 ＋ 执行事实）→ ExecutionConstraint。
            //   非 Candidate / 无 BasePlanVersionId ⇒ 空集（与改造前等价，零回归）。
            //   判据 = V1FenceCalculator（窗口三带）＋ V1AnchorQualifier（执行事实覆盖）；**Firm 不入此通道**。
            ExecutionConstraints = anchorPayload.Constraints,

            // T-04 契约②：订单保护事实（Firm / Frozen；**不锁位置**，供影响分析与业务通报）。
            //   依据 PM回复0923-1 §三.2「Firm 不是锁。Firm 不是不可移动」＋§八「应提供：订单保护事实」；
            //   键 = OrderId（1号位 内存键），None 不投影。详见 OrderProtection / OrderProtectionType。
            OrderProtections = anchorPayload.Protections,

            StrategySnapshot = new SolverStrategySnapshot
            {
                StrategyProfileVersionId = request.SchedulingContext?.StrategyProfileVersionId,
                ParameterSetVersionId = frozenSnapshot.ParameterSetVersionId,
                // P1-02：⑤⑥ 全字段整块透传（强类型零漂移）——1号位从整块读，不再等 2号位 逐批平铺/读投影子集硬编码。
                SolverStrategy     = frozenSnapshot.SolverStrategy,
                CandidateGuardrail = frozenSnapshot.CandidateGuardrail,
                // ⑦ 换型规则：2号位装载投影后按 Domain（Dept+Stage）裁剪，只传本域涉及规则（§19.3「只加载本 Domain」）。
                SetupTransitionRules = SetupTransitionRuleProjector.CropToDomain(frozenSnapshot.SetupTransitionRules, materialStageDeptContexts),
                // ⑧ 批量策略（05 契约 §2.3 第 7 类）：2号位装载投影后**按域裁剪**，只传本 Domain 需求物料
                //   （`MaterialId` ∈ 本域需求物料集）的策略行 —— 与 ⑦ 同向（0号位 2026-10-12 联合回复 §三.4
                //   「设计可行的**最小投影**与性能方案，防止巨大载荷」）。
                //   ① 粒度 = Material + ProductionDepartmentId；裁剪走 `MaterialId` **单轴**，保留该物料**全部部门变体**
                //      ⇒ 结构上不可能裁掉一条 1号位 本可命中的行。
                //   ② 旧注释「裁剪会丢掉 Material 级默认行(Dept=NULL)」**已作废**：正式链上不存在 NULL 部门行
                //      （治理写路径 Create/Update 强制 `ProductionDepartmentId` 必填；⑧投影已退休，装载由 2号位 表直读亦非 NULL），
                //      且 1号位 侧 `AUD-1-004` 早已删除该 Material 级兜底。
                //   ③ 与 1号位 行为等价（已读其代码核实）：`PhaseTwoInitialScheduler.ResolveExecutionBatchPolicy`
                //      在「集合为空」与「非空但无命中」两条路径上**同样返回 null** ⇒ 裁剪只减载荷、不改判定。
                //   匹配仍由 1号位 PhaseOneConstraintBuilder 按本域 Material/Dept 自行完成。
                BatchPolicies = TaskSplitRuleConfigProjector.CropToDomain(batchPolicy.Rules, demandMaterialIds),
                // ⑧ 三态＋不可用**最小载体**（2026-10-13 落；1号位 明示「最小形态由 2号位 择定」）。
                //   依据：06 集成接口 **v1.36 `:17`**（G2-T01 补正：业务键含生效时点；只许最小 DTO、
                //   不得增加 Batch 表、不得把所有物料规则发送 Solver）＋ 规则清单 **v1.7** B-013
                //   ＋ 0号位 2026-10-13《未命名的Markdown文件3》§2（第 4 类「不可用（技术失败）」）。
                //   ⚠ 本属性**非 null** ⇒ 本 Run 已按业务键完整给出三态；消费侧不得再按
                //   「`BatchPolicies` 为空/查不到行」无差别判 MISSING（06 `:17` 逐字已判违规）。
                //   业务键与 1号位 `ResolveExecutionBatchPolicy` **同源**（同一份
                //   `materialStageDeptContexts`）⇒ 其按 (MaterialId, StartStageCode→Dept) 查本载体必命中。
                //   生效时点 = **本域 Batch 装载开始时刻的库时间**（`DomainBatchEvaluationTime`；PM 2026-10-15
                //   §T-1038 第 1 条选定，**不得**用 `ScheduleRun.FrozenAt` / `DataCutoffTime` 替代）——
                //   「发布时有效、Run 时已失效」由此可判 INVALID。
                //   ⚠ 传入的是**裁剪前全量**（本域全部规则行），不是 `CropToDomain` 后的列表：一致性护栏须看到全部行。
                BatchPolicyStates = TaskSplitRuleConfigProjector.BuildStates(
                    batchPolicy.Rules,
                    BuildBatchPolicyBusinessKeys(demandMaterialIds, materialStageDeptContexts),
                    batchPolicy.AsOf,
                    batchPolicy.UnavailableReason),
                Parameters = new FiniteCapacityParameters
                {
                    AllowSplit = solverStrategy.Split.MaxOptimizationSplitCount > 1,
                    AllowMerge = solverStrategy.AllowMerge,
                    SchedulingDirection = schedulingDirection,

                    // P1-02 B 组：⑤⑥ 已冻结字段直接投影（零行为变化；1号位 消费点后置）
                    ImpactedTaskWarningPercent = frozenSnapshot.CandidateGuardrail.ImpactedTaskWarningPercent,
                    MaxPropagationRounds = frozenSnapshot.CandidateGuardrail.MaxPropagationRounds,
                    SplitAlternatives = frozenSnapshot.CandidateGuardrail.SplitAlternatives,
                    MinBatchQty = solverStrategy.Split.MinBatchQty
                }
            },

            CandidateContext = candidateContext,

            RunScope = runScope,

            // FULL §9：前序 Domain 成功后的共享 Resource 占用块 → 1号位 作为不可用时间窗
            UpstreamDomainResourceBlocks = request.UpstreamResourceBlocks ?? Array.Empty<ResourceBlock>()
        };

        // 阶段3前：Pegging 阶段耗时（供给装载 + BOM 遍历扣减 + Routing 装载 + 请求构建）
        var peggingMs = sw.ElapsedMilliseconds;
        MarkBuild("18 求解请求装配");
        return (solveRequest, overCommitEvents, peggingMs, preBucketingDemands);
    }

    /// <summary>
    /// ⑧块三态载体的**业务键集合**（2026-10-13）。
    ///
    /// 来源与 1号位 `PhaseTwoInitialScheduler.ResolveExecutionBatchPolicy` **同源**：1号位 用
    /// `request.MaterialStageDepartmentContexts` 按 (MaterialId, StartStageCode) 反查部门，故本载体
    /// 按**同一份上下文**逐 (MaterialId, ProductionDepartmentId) 出键 ⇒ 其查询必命中，不会落到「未覆盖」。
    /// （该上下文的物料集 = 本 Domain 需求物料，见 `LoadMaterialStageDeptContextAsync(demandMaterialIds)`。）
    ///
    /// 另：域内需求物料若本 Run **未解析到部门**，补一条 `ProductionDepartmentId = null` 的键
    /// （显式「业务键不完整」），使消费侧不必把「键缺失」误读成「接口未传递状态」。
    /// </summary>
    private static List<(int MaterialId, int? ProductionDepartmentId)> BuildBatchPolicyBusinessKeys(
        IReadOnlyCollection<int> demandMaterialIds,
        IReadOnlyList<MaterialStageDepartmentContextDto> materialStageDeptContexts)
    {
        var keys = materialStageDeptContexts
            .Select(c => (c.MaterialId, (int?)c.ProductionDepartmentId))
            .ToHashSet();

        var covered = materialStageDeptContexts.Select(c => c.MaterialId).ToHashSet();
        foreach (var materialId in demandMaterialIds)
        {
            if (!covered.Contains(materialId))
            {
                keys.Add((materialId, null));
            }
        }

        return keys.ToList();
    }

    /// <summary>
    /// ⑧ 批量策略装载结果（2026-10-17 装载侧表直读切换）。
    /// </summary>
    /// <param name="Rules">本域需求物料的全部规则行 —— **未按生效窗过滤**（窗口判定归 <see cref="TaskSplitRuleConfigProjector.BuildStates"/>，
    /// 提前剔行会把本该判 `Invalid` 的键退化成 `Missing`）。</param>
    /// <param name="AsOf">生效时点 = 本域 Batch 装载**开始**时刻的库时间（`DomainBatchEvaluationTime`）。</param>
    /// <param name="UnavailableReason">非空 ⇒ 规则装载技术失败；原样交给 `BuildStates`，全部业务键判 `Unavailable`（**不得降级为 MISSING**）。</param>
    private sealed record BatchPolicyLoadResult(
        IReadOnlyList<BatchPolicyRuleSnapshot> Rules,
        DateTime AsOf,
        string? UnavailableReason);

    /// <summary>
    /// ⑧ 批量策略**表直读**（2026-10-17 切换；PM 2026-10-15 §T-1038）。
    ///
    /// 形态（逐条对齐 PM 裁决）：
    ///   * **一条集合式 SELECT** ＋ `OPENJSON` 传 `MaterialId` 集合 ⇒ RCSI 语句级一致读；
    ///     **不建 `#temp`、不包显式事务**（本库连接层为每库单连接 ＋ 信号量，包事务会阻塞全库访问）。
    ///   * **过滤轴 = `MaterialId` 单轴**（与 <see cref="TaskSplitRuleConfigProjector.CropToDomain"/> 同向）：
    ///     保留同物料的**全部部门变体**，按部门精确对裁会丢掉「本部门无命中、他部门有记录」的行，
    ///     那正是 `Missing(OTHER_DEPT_ONLY)` 的证据。复合键 `(MaterialId, ProductionDepartmentId)`
    ///     只在内存中作业务键参与 `BuildStates`，**不进 SQL 谓词**。
    ///   * **不按生效窗过滤**：`EffectiveFrom / EffectiveTo` 随行下发，由 `BuildStates` 在 `AsOf` 上判半开区间。
    ///   * 行范围 `IsActive = 1`（软删＝`IsActive=0` 视为无规则，不进集合、不进 INVALID）＋
    ///     `ProductionDepartmentId IS NOT NULL`（NULL 部门行不默认为所有部门生效，v5.1.10 收口④）。
    /// </summary>
    private async Task<BatchPolicyLoadResult> LoadBatchPolicyRulesAsync(IReadOnlyList<int> demandMaterialIds)
    {
        if (demandMaterialIds.Count == 0)
        {
            return new BatchPolicyLoadResult([], DateTime.Now, null);
        }

        try
        {
            // 生效时点 = 本域 Batch 装载**开始**时刻的库时间。取时点与取规则是两次往返，
            // 时点先行 ⇒ 满足「D2 阶段开始 Batch 装载时取库时间」；规则本身由**单条** SELECT 给出，不受切面漂移影响。
            var asOf = await _connectionManager.QueryFirstOrDefaultAsync<DateTime>(
                "SELECT CAST(GETDATE() AS datetime2)", db: DatabaseId.APS);

            var materialIdsJson = JsonSerializer.Serialize(demandMaterialIds);

            var rows = await _connectionManager.QueryAsync<TaskSplitRuleConfig>(
                @"SELECT r.Id, r.MaterialId, r.ProductionDepartmentId,
                         r.MinExecutionBatchQty, r.MaxExecutionBatchQty, r.PreferredBatchQty,
                         r.AllowSplit, r.AllowMerge, r.IsActive, r.EffectiveFrom, r.EffectiveTo
                    FROM dbo.TaskSplitRuleConfig AS r
                   WHERE r.IsActive = 1
                     AND r.ProductionDepartmentId IS NOT NULL
                     AND r.MaterialId IN (SELECT j.value FROM OPENJSON(@MaterialIdsJson) AS j)",
                new { MaterialIdsJson = materialIdsJson },
                db: DatabaseId.APS);

            return new BatchPolicyLoadResult(
                TaskSplitRuleConfigProjector.ProjectFromTableRows(rows),
                asOf == default ? DateTime.Now : asOf,
                null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 技术失败 ≠ 业务无规则：原样交给 BuildStates 判 Unavailable，1号位 侧 fail-closed、不取 Q_C。
            _logger.LogError(ex,
                "[Pegging] ⑧批量策略表直读失败（需求物料数={MaterialCount}）⇒ 本域全部业务键按 Unavailable 上报"
                + "（不得降级为 MISSING）",
                demandMaterialIds.Count);
            return new BatchPolicyLoadResult([], DateTime.Now, ex.Message);
        }
    }

    /// <summary>
    /// **装载给 1号位 的验收入口（不调 Solver、不落库）**：跑完 Pegging 全链 + 组出
    /// <see cref="DomainSolveRequest"/> 后即返回，供探针逐项核对载荷（需求数量闭合、PI 归属、
    /// 分桶守恒/E≤Q、Routing 依赖闭合、部门上下文覆盖、Stage 链有序等）。
    ///
    /// ⚠️ 与生产路径 <c>ExecutePeggingWorkflowAsync</c> 的唯一差异 = **到 solveRequest 为止就返回**：
    /// 同一 <c>BuildSolveRequestAsync</c>、同一 <c>ExecutePeggingLoopAsync</c>、同一供给池装载。
    /// <c>persistPositionSnapshot:false</c> —— 探针不覆盖夜间 run 的 PI Position 快照。
    /// </summary>
    internal async System.Threading.Tasks.Task<PeggingPayload> RunPeggingOnlyAsync(
        PeggingExecutionRequest request,
        CancellationToken ct = default,
        bool scopeBomToOrders = false)
    {
        var sw = Stopwatch.StartNew();

        var strategyProfileVersionId = request.SchedulingContext?.StrategyProfileVersionId;
        if (!strategyProfileVersionId.HasValue || strategyProfileVersionId.Value <= 0)
            throw new InvalidOperationException(
                "装载验收策略上下文不完整：SchedulingContext.StrategyProfileVersionId 为空。正式运行必须有冻结策略版本，禁止静默回退。");
        var frozenSnapshot = await _frozenStrategySnapshotProvider
            .GetFrozenStrategySnapshotAsync(strategyProfileVersionId.Value, ct);

        // 分段计时（2026-10-09）：小样本定向跑只缩到 160s（全量 ~200s），说明大头**不在订单/BOM 侧**。
        // 没有分段耗时就是黑盒，遂在此埋点。四处即可定位：BOM 快照 / 供给池(含 PI 事实) / Pegging 回路 / 请求构建(含分桶)。
        var t0 = sw.ElapsedMilliseconds;
        // `scopeBomToOrders=true` 时把 BOM 宇宙收窄到 request.OrderIds（**仅小样本定向跑用**，见方法注释）。
        var bomSnapshot = await LoadBomSnapshotAsync(
            request.PlanVersionId, ct, scopeBomToOrders ? request.OrderIds : null);
        var t1 = sw.ElapsedMilliseconds;
        var (supplyPool, continuityFacts) = await LoadSupplyPoolAsync(
            request, frozenSnapshot, bomSnapshot, ct, persistPositionSnapshot: false);
        var t2 = sw.ElapsedMilliseconds;

        var voucher = await ExecutePeggingLoopAsync(request, bomSnapshot, supplyPool, ct);
        var t3 = sw.ElapsedMilliseconds;
        var redLineErrors = ValidatePeggingResult(supplyPool, voucher);

        var (solveRequest, overCommits, peggingMs, preBucketing) = await BuildSolveRequestAsync(
            request, voucher, frozenSnapshot, continuityFacts, bomSnapshot, sw, ct);
        var t4 = sw.ElapsedMilliseconds;

        _logger.LogInformation(
            "[Pegging][阶段耗时] BOM快照={Bom}ms 供给池(含PI事实)={Supply}ms Pegging回路={Loop}ms 请求构建(含分桶)={Build}ms 合计={Total}ms 订单={Orders}",
            t1 - t0, t2 - t1, t3 - t2, t4 - t3, t4 - t0, request.OrderIds?.Count ?? 0);

        return new PeggingPayload(
            request.PlanVersionId, voucher, solveRequest, overCommits, redLineErrors,
            peggingMs, sw.ElapsedMilliseconds, preBucketing, continuityFacts.PiPositions);
    }

    /// <summary>装载给 1号位 的完整产出（Solver 之前的全部数据）——验收探针读取用。</summary>
    internal sealed record PeggingPayload(
        int PlanVersionId,
        PeggingResultVoucher Voucher,
        DomainSolveRequest SolveRequest,
        IReadOnlyList<ContinuityOverCommitEvent> OverCommits,
        IReadOnlyList<string> RedLineErrors,
        long PeggingMs,
        long TotalMs,
        // 分桶**前**的源需求（分桶守恒核验基准）
        IReadOnlyList<LogicalProductionDemand> PreBucketingDemands,
        // PI Position 事实（含 ExistingExecutionContexts；E 重复计核验基准）
        IReadOnlyDictionary<string, ProductionInstructionPositionResult> PiPositions);

    public async System.Threading.Tasks.Task<IEnumerable<PeggingOrchestrationResult>> ExecuteBatchPeggingWorkflowAsync(
        PeggingExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        // P0-01：撤销「20单=独立 Pegging/Solver/事务」的业务切批（原 const int batchSize=20 + OrderIds.Chunk）。
        // 冻结口径：一个 Domain = 一个数量真相边界 / 一次 DemandPriority / 一条 AllocationSequence /
        // 一次有限产能 Solver / 单Domain事务。LoadSupplyPoolAsync 本就按 DataCutoffTime 装载整域供给池（不分单），
        // 20单切批反而导致四个直接错误（见 2号位代码审核报告 P0-01）：
        //   A. 供给被多批重复消费（每批重载原始数量、不回扣上一批已落分配）；
        //   B. DemandPriority 只在批次内部成立，全 Domain 高优先级订单无法跨批争抢同一供给；
        //   C. 同一 Domain 被多次独立调用 Solver，FinalTask 无法在同一有限产能问题中竞争/合批/拆批；
        //   D. AllocationSequence 每批从 1 重复起始，撞 UNIQUE(PlanVersionId, AllocationSequence)。
        // 最小整改（PM 方向）：保留内存结构，整域单次调用；方法名保留 Batch 仅为 IPeggingOrchestrator 接口
        // 与 SchedulingOrchestrator 的聚合口径（r => 单结果集）兼容。
        return new[] { await ExecutePeggingWorkflowAsync(request, cancellationToken) };
    }

    /// <summary>
    /// `Task.Duration` 回写公式（`Duration-2`，纯函数 ⇒ 供 `LPS.APS.Tests` 直接断言，不需要库）。
    ///
    /// 【公式】命中时 <c>Duration = EffectiveProcessMinutes + SetupTime</c>；缺值时 <c>= 墙钟跨度</c>（**不再加 Setup**）。
    ///   单位＝分钟，保留 4 位小数。`SetupTime` **只加在命中分支** —— 墙钟跨度本就含 Setup，两边都加会重复计。
    /// 【口径来源】1号位 2026-10-26 回执 §一／§6.1：
    ///   · `EffectiveProcessMinutes` ＝ 标准工时 × 该 draft 的 `PlannedProcessQty` ÷ 产能系数，**不含 Setup、不含跨窗中断**；
    ///   · 冻结 <c>07 :33</c>「Setup 占设备但不属于有效加工」⇒ 落 `Task.Duration` 的「工时」＝ Setup ＋ 加工，两块都要进。
    ///
    /// ⚠ **为什么必须有缺值回落**：`EffectiveProcessMinutes` 为 `null` 时（1号位 明确：锁定继承 Task 恒给 null），
    ///   只能回落墙钟跨度。**单窗下墙钟跨度恰好等于 有效加工+Setup ⇒ 单窗单测全绿，只有跨日历窗才暴露**。
    ///   故本函数的行为必须由「跨窗」样本锁住，见 <c>LPS.APS.Tests/Unit/TaskDurationWriteBackTests.cs</c>。
    ///
    /// ⚠ 本公式**只用于回写 `Task.Duration`**。`PhaseFiveCompression.CompactGaps` 的占用槽长是**占用**口径
    ///   （含跨日历中断），改成有效加工会少算中断段 ⇒ 槽重叠（1号位 §六 明令不动）。
    /// </summary>
    internal static decimal ComputeTaskDurationMinutes(
        DateTime plannedStart,
        DateTime plannedEnd,
        decimal? effectiveProcessMinutes,
        decimal setupTime)
    {
        if (effectiveProcessMinutes.HasValue)
        {
            // 命中：工时 = 有效加工 ＋ Setup（两块都要进，冻结 `07 :33`）。
            return Math.Round(effectiveProcessMinutes.Value + setupTime, 4);
        }

        // 缺值回落：**直接用墙钟跨度，不再加 Setup**。
        //   ⚠ 墙钟跨度 `PlannedEnd − PlannedStart` **本就含 Setup**（单窗下它 ≡ 有效加工 ＋ Setup，
        //     这正是单窗测不出本缺陷的原因）。若在这里再加一次 `setupTime` 就是**重复计 Setup**。
        //   1号位 2026-10-26 回执 §6.1 的原文也是「按 `null` 回落 `PlannedEnd − PlannedStart` 即可」。
        return Math.Round((decimal)(plannedEnd - plannedStart).TotalMinutes, 4);
    }

    /// <summary>
    /// 统一事务落库：**单次 <c>ExecuteInTransactionAsync</c>，任一失败全部回滚**。
    ///
    /// 【2026-10-13 更正】原注释写「DELETE 占位 Task → INSERT Task → INSERT Pegging 血缘 → INSERT AllocationLedger。四步」，
    /// **与代码不符且已证实误导**：本方法内**没有任何 DELETE**；`AllocationLedger` 全仓**无此表**（无 DDL / 无引用）。
    /// 实际语句组为**五段**，全部挂在同一个 `tx` 上：
    ///   ① INSERT `[Task]`（逐条 `solveResult.FinalTasks`）
    ///   ② INSERT `[Pegging]`（Task→Task 血缘）
    ///   ③ INSERT `AllocationTaskShare`（需求↔Task 份额；前置 Inv1~Inv4 fail-closed 校验）
    ///   ④ INSERT `PeggingSupplyAllocation`（仅非 Task 供给）
    ///   ⑤ INSERT `ScheduleExplanationFact`（解释事实）
    /// 求解**前**的清场 DELETE 不在本方法内，而在 `SchedulingOrchestrator` 的跑前幂等清理（按 PlanVersionId 四条 DELETE）。
    /// </summary>
    /// <param name="deptContexts">
    /// A1：本 Run 需求物料的部门事实（`MaterialStageDeptContext(IsCurrent=1)` 裁剪行，调用方在**事务外**装载）。
    /// 源＝ERP 物料级部门码经 `ProductionDepartment` 归一化（`sp_RebuildMaterialStageDeptContext`，无 RoutingOperation join）。
    /// 用途为填 `Task.ProductionDepartmentId`；**未命中不算异常**，留 NULL 并记 Issue[DEPT_CONTEXT_MISS]。
    /// </param>
    /// <param name="supplyPool">
    /// 本域供给池。**只为取计划良率三态只读计数**（`YieldDiagnostics` 与 `PlanningYieldDuplicateKeyCount()`），
    /// 写进 `DOMAIN_QUANTITY_RECONCILIATION` 那一行 `EvidenceJson`（3号位 2026-10-25 缺配三态词表 §四.1）。
    /// 不读账本内容、不影响落库判定；传 null 时三态字段记 0（单测/旧调用方兼容）。
    /// </param>
    private async System.Threading.Tasks.Task<(List<ApsTask> tasks, int peggingCount, int supplyAllocationCount)>
        PersistDomainAndPeggingInTransactionAsync(
            int planVersionId,
            PeggingResultVoucher voucher,
            DomainSolveResult solveResult,
            IReadOnlyList<ContinuityOverCommitEvent> overCommits,
            CancellationToken ct,
            IReadOnlyList<MaterialStageDepartmentContextDto>? deptContexts = null,
            IReadOnlySet<(int MaterialId, string StageCode, string RouteCode, long PathId, string OperationCode)>? nonResourceOperations = null,
            SupplyPool? supplyPool = null)
    {
        return await _connectionManager.ExecuteInTransactionAsync<(List<ApsTask>, int, int)>(
            async (conn, tx) =>
            {
                Console.WriteLine($"[PersistDomainAndPeggingInTransactionAsync] 开始事务，FinalTasks数量={solveResult.FinalTasks.Count}");
                var now    = DateTime.Now;
                var tasks  = new List<ApsTask>(solveResult.FinalTasks.Count);

                var finalDraftToTaskId = new Dictionary<string, long>(
                    solveResult.FinalTasks.Count, StringComparer.Ordinal);

                // A1：部门**默认上下文**索引，(MaterialId, StageCode) → DefaultProductionDepartmentId。
                //   `deptContexts` 由调用方在事务外按本 Run 需求物料裁剪装载（`LoadMaterialStageDeptContextAsync`，
                //   内部走 `QueryChunkedInAsync` 分批，**无裸 `IN @` 超参风险**）。
                //   ⚠ MSC 的 `StageCode` 实测零空（v1.1 §六.1 取证行），故空/纯空白 StageCode 必然未命中 ⇒ 记 Issue，不静默。
                // 【重复键为什么会在这里静默首值胜出】`deptContexts` 已在事务外由
                //   `LoadMaterialStageDeptContextAsync` 按 J15-04 **Fail-Closed** 剔除了冲突键
                //   （同一 `(MaterialId, StageCode)` 出现多个**不同**部门 ⇒ 整键丢弃、只留上面那条 Error 日志），
                //   所以到这里同键只可能是**同一个部门值**，`TryAdd` 不会做出选择。
                //   1号位 2026-10-25 R04 回执 §四 甲 要求「至少把重复计数写进同一诊断位」⇒ 这里显式数一遍：
                //   若真数出**不同值**（说明上游 Fail-Closed 被绕开/改坏），落 Error 点名，不再静默首值胜出。
                var deptIdByMaterialStage = new Dictionary<(int MaterialId, string StageCode), int>();
                var deptDuplicateSameKey = 0;
                var deptDuplicateConflictSample = new List<string>();
                foreach (var c in deptContexts ?? Array.Empty<MaterialStageDepartmentContextDto>())
                {
                    var deptKey = (c.MaterialId, c.StageCode);
                    if (deptIdByMaterialStage.TryAdd(deptKey, c.ProductionDepartmentId))
                        continue;

                    if (deptIdByMaterialStage[deptKey] != c.ProductionDepartmentId)
                    {
                        deptDuplicateSameKey++;
                        if (deptDuplicateConflictSample.Count < 5)
                            deptDuplicateConflictSample.Add(
                                $"({c.MaterialId},{c.StageCode}:{deptIdByMaterialStage[deptKey]}→{c.ProductionDepartmentId})");
                    }
                }

                if (deptDuplicateSameKey > 0)
                {
                    _logger.LogError(
                        "[Pegging] 部门上下文出现 {Count} 组**同键不同部门**（应已由 `LoadMaterialStageDeptContextAsync` 按 J15-04 Fail-Closed 剔除）" +
                        "⇒ 本处首值胜出，1号位 侧对同一输入会 Fail Closed，两侧口径分叉。示例：{Samples}",
                        deptDuplicateSameKey, string.Join(", ", deptDuplicateConflictSample));
                }
                var deptHit = 0;
                var deptMiss = 0;
                // `Duration-2`（2026-10-26）：1号位 填值命中数 / 回落数。回落**必须可见**——
                // 单窗下回落值恰好等于真值会让单测全绿，只有跨窗才暴露（1号位 2026-10-26 回执 §6.3）。
                var durationHit = 0;
                var durationFallbackNull = 0;

                // ── 部门最小安全校验（0号位 第十四轮 J14-06）：**无 ERP 部门事实的 Task 不落库**（Fail-Closed）──
                //   口径链：ERP 物料级部门码（`MaterialSupplyContext.DefaultProductionDeptCode`）
                //          → `ProductionDepartment` 归一化 → `MaterialStageDeptContext(IsCurrent=1)` → `(MaterialId, StageCode)`。
                //   实测该上下文对 StagePath 的 (物料,Stage) 对覆盖率 79,349/126,693（**缺 37.4%**）⇒ **不能「任一缺失即整批 abort」**：
                //     · 有缺口 ⇒ 缺口那批 FinalTask **不 INSERT**（否则会落下 `ProductionDepartmentId=NULL` 的 Task，
                //       进入 MES 下发与 Candidate 变化基线）；
                //     · 本域**一条可落库都没有**而缺口非空 ⇒ 升级为 Domain 失败（抛异常 ⇒ 事务回滚），
                //       不得静默返回 0 Task 冒充成功。
                var (executableTasks, missingDeptTasks) = PartitionByDepartment(
                    solveResult.FinalTasks, deptIdByMaterialStage);

                // ── J16-04（三）：**资源身份非法 ⇒ 该 Task 不落库**（0号位 第十六轮）──
                //   原实现在此把 `ResourceId<=0` 改写成 NULL/NULL 后照常 INSERT —— 只避免「幽灵设备 Id」，
                //   并没有证明该 Task 仍有合法资源；`Task.ResourceId=NULL AND ManualSlotId=NULL` 不等于「已分配资源」。
                //   非正 Id 既不是 `Resource` 主数据（实测最小 Id=6,444），也不是人工槽（人工槽是 ≥1e9 的合成键）⇒ 一律隔离。
                // J17-04（0号位 第十七轮）：`ResourceId == null` 不再一律视为合法。
                //   冻结判别器（0号位 2026-09-22 裁决；1号位 `PhaseOneConstraintBuilder:256-264` 是唯一实现处）：
                //     · `OperationPlanningMode ∈ {UNCONSTRAINED, WAIT_ONLY}` ⇒ **非资源工序**，Task 无资源是合法形态；
                //     · `FINITE_RESOURCE` ⇒ 必须落在真实设备或人工槽上，`null` 与「非正 Id」同属非法资源分区。
                //   `nonResourceOperations` 取自**本域实际交给 Solver 的** `RoutingOperations`（已过 Stage 过滤），
                //   不是全表推断 ⇒ 不会把未参与本域的工序模式混进来。
                var invalidResourceTasks = executableTasks
                    .Where(t => IsResourceIdentityIllegal(t, nonResourceOperations))
                    .ToList();
                if (invalidResourceTasks.Count > 0)
                {
                    executableTasks = executableTasks
                        .Where(t => !IsResourceIdentityIllegal(t, nonResourceOperations))
                        .ToList();
                    _logger.LogWarning(
                        "[Pegging] FinalTask 资源身份非法（J16-04／J17-04，Fail-Closed 隔离）：{Count}/{Total} 个 Task " +
                        "ResourceId 非正数或（FINITE_RESOURCE 工序）为空" +
                        "⇒ 不 INSERT（不得以 ResourceId=NULL 冒充「已分配资源」），其需求按未排真相计入 U（DomainKey={DomainKey}）。样本：{Samples}",
                        invalidResourceTasks.Count, solveResult.FinalTasks.Count, voucher.DomainKey,
                        string.Join("; ", invalidResourceTasks.Take(10)
                            .Select(t => $"M{t.MaterialId}/{t.StageCode ?? "(空Stage)"}/R{t.ResourceId?.ToString() ?? "NULL"}")));
                }

                // ── J16-04（二）：前置被隔离 ⇒ 后序一并隔离（原实现静默略去 Pegging 边、保留后序 Task）──
                var excludedDraftIds = missingDeptTasks
                    .Concat(invalidResourceTasks)
                    .Select(t => t.FinalDraftId)
                    .ToHashSet(StringComparer.Ordinal);
                var cascade = CascadeExcludeDependents(
                    executableTasks, solveResult.PhysicalPeggingDrafts, excludedDraftIds);
                executableTasks = cascade.Executable;
                if (cascade.Cascaded.Count > 0)
                {
                    _logger.LogWarning(
                        "[Pegging] 前序 Task 被隔离导致后序一并隔离（J16-04）：{Count} 个 FinalTask 的上游供给 Task 不落库" +
                        "⇒ 后序不得单独发布（否则失去前驱约束），其数量按未排真相计入 U（DomainKey={DomainKey}）。样本：{Samples}",
                        cascade.Cascaded.Count, voucher.DomainKey,
                        string.Join("; ", cascade.Cascaded.Take(10)
                            .Select(t => $"M{t.MaterialId}/{t.StageCode ?? "(空Stage)"}")));
                }

                var excludedTotal = missingDeptTasks.Count + invalidResourceTasks.Count + cascade.Cascaded.Count;
                if (excludedTotal > 0 && executableTasks.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"部门/资源事实缺失导致本域全部 Task 不可落库（FinalTasks={solveResult.FinalTasks.Count}，" +
                        $"缺部门={missingDeptTasks.Count}，资源非法={invalidResourceTasks.Count}，前序隔离级联={cascade.Cascaded.Count}，" +
                        $"DomainKey={voucher.DomainKey}）—— Fail-Closed，事务回滚。" +
                        "来源=MaterialStageDeptContext(IsCurrent=1)／FinalTask.ResourceId；须先补齐 ERP 物料级部门码" +
                        "（MaterialSupplyContext.DefaultProductionDeptCode）与 1号位 资源身份。");
                }

                if (missingDeptTasks.Count > 0)
                {
                    _logger.LogWarning(
                        "[Pegging] 部门事实缺失（Fail-Closed 隔离，本轮不落库）：{Missing}/{Total} 个 FinalTask 在 " +
                        "MaterialStageDeptContext(IsCurrent=1) 无 (MaterialId, StageCode) 记录 ⇒ 不 INSERT，不计入已排量" +
                        "（DomainKey={DomainKey}）。样本：{Samples}",
                        missingDeptTasks.Count, solveResult.FinalTasks.Count, voucher.DomainKey,
                        string.Join("; ", missingDeptTasks.Take(10)
                            .Select(t => $"M{t.MaterialId}/{t.StageCode ?? "(空Stage)"}")));
                }

                // W3：Task 落库必须携带 MTS_InstructionNo（连续份额尤须，供 MES 绑原工单识别）。
                // FinalTaskDraft.SourceDraftId = 源 LogicalDemandKey（1号位 `PhaseTwoInitialScheduler` 的 `SourceDraftId = demand.LogicalDemandKey`）→ 反查 LPD.ProductionInstructionNo。
                // Continuation 保持源 key、Free 用源 key+"/FREE"——两者都在 voucher.LogicalProductionDemands 里带了同一 PI，均能命中。
                var piByDemandKey = voucher.LogicalProductionDemands
                    .Where(d => !string.IsNullOrEmpty(d.ProductionInstructionNo))
                    .GroupBy(d => d.LogicalDemandKey, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First().ProductionInstructionNo!);

                // G4：反查「旧 TaskNo ↔ MESWorkOrderNo」映射（TaskNo 继承，冻结 §12.10.1 / T10）。
                // 映射落点 = 下发中间表 [TaskDispatch]，不是 [Task]（MES 侧工单身份不进核心 Task 模型，0号位 2026-09-17 P2/P3/P4 裁决）。
                // 只对连续份额有 MES 工单号；显式剔除当前 SourcePlanVersionId，命中均为历史版本的 TaskDispatch 下发绑定。
                // 键 = MES 工单号（一个 TaskNo ↔ 一个 MESWorkOrderNo，§7.1），取该工单最新下发记录的旧 TaskNo。
                var continuationWorkOrders = solveResult.FinalTasks
                    .Select(f => ExtractMesWorkOrderNo(f.SourceDraftId))
                    .Where(w => w is not null)
                    .Select(w => w!)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                var oldTaskNoByWorkOrder = new Dictionary<string, string>(StringComparer.Ordinal);
                if (continuationWorkOrders.Count > 0)
                {
                    // 分片：`continuationWorkOrders` = 本域 **全部** FinalTasks 的 MES 工单号去重（全量跑可达数千，
                    // 远超 2100）⇒ 裸 `IN @Wos` 会把「TaskNo 继承」这一步整个打断。lambda 内闭包 `tx`，同一事务不变。
                    var bindRows = await QueryChunkedInAsync(continuationWorkOrders, chunk =>
                        conn.QueryAsync<TaskNoBindingRow>(
                            @"SELECT d.MESWorkOrderNo, d.TaskNo
                          FROM [TaskDispatch] d
                          JOIN (
                              SELECT MESWorkOrderNo, MAX(Id) AS MaxId
                              FROM [TaskDispatch]
                              WHERE MESWorkOrderNo IN @Wos AND SourcePlanVersionId <> @CurrentPlanVersionId
                              GROUP BY MESWorkOrderNo
                          ) m ON m.MaxId = d.Id",
                            new { Wos = chunk, CurrentPlanVersionId = planVersionId },
                            transaction: tx));

                    foreach (var r in bindRows)
                        oldTaskNoByWorkOrder[r.MESWorkOrderNo] = r.TaskNo;
                }

                // B/C 桶「同批共号」登记表（T-002）：批身份 → 本 Run 内已分配的新 TaskNo。
                // 作用域 = 本 PlanVersion 落库事务（跨域循环不共享，故声明在此 foreach 之前）。
                var taskNoByBatch = new Dictionary<string, string>(StringComparer.Ordinal);

                // 只落「部门事实可解析」的 FinalTask（J14-06 Fail-Closed；缺口集合见上方分区与告警）。
                foreach (var final in executableTasks)
                {
                    ct.ThrowIfCancellationRequested();

                    piByDemandKey.TryGetValue(final.SourceDraftId, out var mtsInstructionNo);

                    // G4：连续份额从 ContinuationKey 反解 MES 工单号；TaskNo 三分支（T-002/T-003/T-004）
                    var mesWorkOrderNo = ExtractMesWorkOrderNo(final.SourceDraftId);
                    var taskNo = ResolveTaskNo(planVersionId, final, mesWorkOrderNo, oldTaskNoByWorkOrder, taskNoByBatch);

                    // S3c：人工槽合成 ResourceId 拆回——合成键（≥ ManualSlotResourceOffset）→ Task.ResourceId=NULL + Task.ManualSlotId=原槽号；
                    // 设备真实 ResourceId 原样落 ResourceId（P0-06 不丢 1号位 时间资源真相）。
                    int? persistResourceId;
                    int? persistManualSlotId;
                    if (final.ResourceId.HasValue && SolverResourceProjection.IsManualSlotId(final.ResourceId.Value))
                    {
                        persistResourceId = null;
                        var slotId = SolverResourceProjection.ToManualSlotId(final.ResourceId.Value);
                        persistManualSlotId = slotId;

                        // G-17 上界守卫：拆回结果越界 ⇒ 合成键异常。仅告警，**不改变落库结果**（仍落拆回值）。
                        if (!SolverResourceProjection.IsWithinManualSlotRange(slotId))
                            _logger.LogWarning(
                                "[Pegging] 人工槽合成键拆回越界：SyntheticResourceId={SyntheticResourceId}, ManualSlotId={ManualSlotId}, 上界={MaxGuard}",
                                final.ResourceId.Value, slotId, SolverResourceProjection.ManualSlotIdMaxGuard);
                    }
                    else if (final.ResourceId is int ridRaw && ridRaw <= 0)
                    {
                        // 【J16-04 起：本分支**已不可达**】非正 ResourceId 的 Task 在 `invalidResourceTasks`
                        //   分区处就整体隔离（不 INSERT），不会走到这里。保留为防御性兜底：
                        //   万一分区判据被改坏，这里仍不放幽灵设备进 Task，并**必须**留下错误级日志。
                        //   （旧口径「落 NULL 即安全」已被 0号位 第十六轮否决：NULL/NULL 不等于「已分配资源」。）
                        persistResourceId = null;
                        persistManualSlotId = null;
                        _logger.LogError(
                            "[Pegging] 防御分支被触发（不应发生）：非正 ResourceId 的 FinalTask 未被前置隔离，已落 NULL：" +
                            "ResourceId={ResourceId}, MaterialId={MaterialId}, StageCode={StageCode}, TaskNo={TaskNo}",
                            ridRaw, final.MaterialId, final.StageCode, taskNo);
                    }
                    else
                    {
                        persistResourceId = final.ResourceId;
                        persistManualSlotId = null;
                    }

                    // 真实路径身份落库（红线 Q1 / RT-001 / DB-002）：1号位 FinalTaskDraft 原样透传，**不回填 DEFAULT/1**。
                    //   ⚠ `FinalTaskDraft.PathId` 是 `long?`，而 Task.PathId 是 `INT`（DDL v5.1.8.3）⇒ 越界即置 NULL，不静默截断。
                    //   ⚠ 当前值仍会是 DEFAULT/1 —— 因为 1号位 侧 `StageTimingNodeBuilder` 尚硬编码
                    //     RouteCode=="DEFAULT"，2号位 不敢先停归一化（见台账 §T-1008l）。待 1号位 整改后本列自动变真值。
                    int? persistPathId =
                        final.PathId is { } pid && pid >= int.MinValue && pid <= int.MaxValue ? (int)pid : null;

                    // ProductionDepartmentId：DB-002 要求落真实部门（**不得默认 NULL**）。
                    //   口径链：**ERP 物料级部门事实** → `(MaterialId, StageCode)` → `MaterialStageDeptContext(IsCurrent=1)`。
                    //   · 取值**出自 ERP 侧**：`sp_RebuildMaterialStageDeptContext` 由
                    //     `MaterialSupplyContext.DefaultProductionDeptCode` LEFT JOIN `ProductionDepartment` 归一化产出，
                    //     **无 `RoutingOperation` join** ⇒ **非 Routing 反推、非猜测**（＝PM 2026-08-31 裁定的「码 → Id」归一化路径）。
                    //     ⚠ 该表 `DefaultProductionDepartmentId` 全 NULL **不构成缺口** —— 归一化就发生在上述 LEFT JOIN。
                    //   · ⚠ **实现现状（勿按旧注释想象）**：`FinalTaskDraft` **无部门成员**
                    //     （`DomainSolveResult.cs` 全文 0 命中 `ProductionDepartmentId`）⇒ 本处**无条件**取上下文值，
                    //     **代码中不存在**「生产指示侧真值优先」的分支（旧注释曾如此声称，与语句不符，已改）。
                    //     ⇒ 若日后 1号位 的 `FinalTaskDraft` 带出生产指示侧部门真值，**必须在此处先判真值非空、再回退到本节**；
                    //       否则新真值会被本上下文**静默覆盖** —— 那正是审核位 J12-04 点名禁止的情形。
                    //   · `Task` 无来源列，落库值不可区分来源 ⇒ 命中/未命中均留日志。
                    var persistDepartmentId =
                        ResolvePersistedDepartmentId(deptIdByMaterialStage, final.MaterialId, final.StageCode);
                    if (persistDepartmentId.HasValue)
                    {
                        deptHit++;
                    }
                    else
                    {
                        // v1.1 §六.1.3 逐字要求：「**不得让 join 失败静默变 NULL**」。
                        deptMiss++;
                        _logger.LogWarning(
                            "[Pegging] Task 部门未命中 Issue[DEPT_CONTEXT_MISS]: 来源＝ERP 物料级部门上下文「{}」，" +
                            "MaterialId={MaterialId}, StageCode={StageCode}, TaskNo={TaskNo} ⇒ 该 Task.ProductionDepartmentId 留 NULL",
                            "MaterialStageDeptContext(IsCurrent=1)", final.MaterialId, final.StageCode, taskNo);
                    }

                    // ── Duration 回写（`Duration-2`，1号位 2026-10-26 回执 §6.1 解锁）──
                    //   口径（命中）：`Duration = EffectiveProcessMinutes + SetupTime`，单位＝分钟；
                    //   缺值回落：`Duration = 墙钟跨度`（**不再加 Setup**——跨度本就含 Setup，两边都加＝重复计）。
                    //   冻结依据：`07 :33`「Setup 占设备但不属于有效加工」⇒ 工时 = Setup + 加工，两块必须都进。
                    //   · `EffectiveProcessMinutes` 由 1号位 填值，是**不变量**（压实/重排/Setup 重解析都不改它）；
                    //     我方只按值使用，**不自行推算**。
                    //   · `null` ⇒ 回落墙钟跨度并**计数**（1号位 点名：单窗下回落值恰好等于真值 ⇒ 全绿，
                    //     只有跨窗才暴露；不得静默通过）。**锁定继承 Task 恒为 null 属预期**，不是漏拷。
                    //   · 不做此回写的两处 `PhaseFiveCompression.CompactGaps` 占用槽长：那是**占用**口径，
                    //     改成有效加工会少算跨日历中断段 ⇒ 槽重叠（1号位 §六 明令不动）。
                    if (final.EffectiveProcessMinutes.HasValue)
                    {
                        durationHit++;
                    }
                    else
                    {
                        // 逐条不刷屏：只记首个样本，汇总在循环后一次性出数
                        if (durationFallbackNull == 0)
                        {
                            _logger.LogInformation(
                                "[Pegging] Duration 回落样本: TaskNo={TaskNo}, MaterialId={MaterialId}, StageCode={StageCode}, " +
                                "TaskType={TaskType} ⇒ EffectiveProcessMinutes=NULL，按墙钟跨度 {Wall:F4}min + Setup {Setup:F4}min 回写",
                                taskNo, final.MaterialId, final.StageCode, final.TaskType,
                                (final.PlannedEndTime - final.PlannedStartTime).TotalMinutes, final.SetupTime);
                        }
                        durationFallbackNull++;
                    }
                    var durationMinutes = ComputeTaskDurationMinutes(
                        final.PlannedStartTime, final.PlannedEndTime, final.EffectiveProcessMinutes, final.SetupTime);

                    var ids = await conn.QueryAsync<long>(
                        @"INSERT INTO [Task] (
                              PlanVersionId, TaskNo, OrderId, MaterialId,
                              OperationSeq, OperationCode,
                              Quantity, PlannedProcessQty, UOM, PlannedStartTime, PlannedEndTime, Duration,
                              ResourceId, ManualSlotId,
                              Status, IsLocked, IsCriticalPath, TaskType,
                              MTS_InstructionNo, SetupSource, StageCode,
                              MESWorkOrderNo, ProductionDepartmentId, RouteCode, PathId,
                              StageExecutionBatchDraftKey, StageExecutionBatchQty,
                              CreatedAt, UpdatedAt
                          )
                          OUTPUT INSERTED.Id
                          VALUES (
                              @PlanVersionId, @TaskNo, @OrderId, @MaterialId,
                              @OperationSeq, @OperationCode,
                              @Quantity, @PlannedProcessQty, @UOM, @PlannedStartTime, @PlannedEndTime, @Duration,
                              @ResourceId, @ManualSlotId,
                              @Status, @IsLocked, @IsCriticalPath, @TaskType,
                              @MTS_InstructionNo, @SetupSource, @StageCode,
                              @MESWorkOrderNo, @ProductionDepartmentId, @RouteCode, @PathId,
                              @StageExecutionBatchDraftKey, @StageExecutionBatchQty,
                              @CreatedAt, @UpdatedAt
                          )",
                        new
                        {
                            PlanVersionId    = planVersionId,
                            TaskNo           = taskNo,
                            OrderId          = voucher.OrderId,
                            MaterialId       = final.MaterialId,
                            OperationSeq     = final.OperationSeq,
                            OperationCode    = final.OperationCode,
                            Quantity         = final.Quantity,
                            // P0-06：1号位 FinalTask 的 PlannedProcessQty 原样落库（DB 列 v5.1.2 已加）
                            PlannedProcessQty = final.PlannedProcessQty,
                            UOM              = final.UOM,
                            PlannedStartTime = final.PlannedStartTime,
                            PlannedEndTime   = final.PlannedEndTime,
                            // Duration 回写（表有列；单位=分钟）。2026-10-26 `Duration-2` 起改为「工时」口径，
                            // 见方法首部 `ComputeTaskDurationMinutes` 的注释（命中＝有效加工+Setup；缺值＝墙钟跨度原值）。
                            Duration         = durationMinutes,
                            // S3c：人工槽合成键拆回 ResourceId=NULL + ManualSlotId；设备 Resource 原样落库（P0-06 不丢时间资源真相）
                            ResourceId       = persistResourceId,
                            ManualSlotId     = persistManualSlotId,
                            Status           = "PLANNED",
                            IsLocked         = false,
                            IsCriticalPath   = false,
                            TaskType         = final.TaskType,
                            // W3：连续份额 / 自由份额 Task 回填 MTS_InstructionNo（无 PI 需求 = null，不得下发 MES）
                            MTS_InstructionNo = mtsInstructionNo,
                            // SetupSource 增列（5号位 动作单 2026-09-21）：1号位 SetupOutcome 原样落库，2号位 不反推
                            SetupSource      = final.SetupSource,
                            // StageCode 增列（2026-09-21）：FinalTaskDraft.StageCode（=RoutingOperation.StageCode 大工艺）原样落库，
                            // 供 TaskDispatch 下发时区分「哪个 Stage 的指示」；空串落 NULL（与 MTS_InstructionNo 同口径）。
                            StageCode        = string.IsNullOrWhiteSpace(final.StageCode) ? null : final.StageCode,
                            // ── v5.1.8.3 新增列：真实身份 + MES 执行批（T-002/T-003 / DB-002 / 红线 Q1）──
                            MESWorkOrderNo   = mesWorkOrderNo,
                            ProductionDepartmentId = persistDepartmentId,
                            // 空串落 NULL（与 StageCode 同口径）；**不得**把 DEFAULT 当占位真值写进去
                            RouteCode        = string.IsNullOrWhiteSpace(final.RouteCode) ? null : final.RouteCode,
                            PathId           = persistPathId,
                            // C 桶由 1号位 给 Stage 执行批键（A/B 桶留空，见 09-29 加列脚本口径）；当前 1号位 尚未产出 ⇒ 恒 NULL
                            StageExecutionBatchDraftKey = string.IsNullOrWhiteSpace(final.StageExecutionBatchDraftKey) ? null : final.StageExecutionBatchDraftKey,
                            StageExecutionBatchQty      = final.StageExecutionBatchQty,
                            CreatedAt        = now,
                            UpdatedAt        = now
                        },
                        transaction: tx);

                    var taskId = ids.Single();
                    Console.WriteLine($"[PersistDomainAndPeggingInTransactionAsync] Task插入成功: TaskId={taskId}, TaskNo={taskNo}");
                    finalDraftToTaskId[final.FinalDraftId] = taskId;

                    tasks.Add(new ApsTask
                    {
                        Id               = taskId,
                        PlanVersionId    = planVersionId,
                        TaskNo           = taskNo,
                        OrderId          = voucher.OrderId,
                        MaterialId       = final.MaterialId,
                        OperationSeq     = final.OperationSeq,
                        OperationCode    = final.OperationCode,
                        ResourceId       = persistResourceId,   // S3c：拆键后（人工槽→NULL）；设备保留 1号位 实际 Resource（供跨域占用块提取）
                        ManualSlotId     = persistManualSlotId,
                        // 真实路径身份（红线 Q1）：与上方 INSERT 同源逐字一致，**不再硬编码 DEFAULT/1**
                        RouteCode        = string.IsNullOrWhiteSpace(final.RouteCode) ? null : final.RouteCode,
                        PathId           = persistPathId,
                        MESWorkOrderNo   = mesWorkOrderNo,
                        ProductionDepartmentId = persistDepartmentId,
                        StageExecutionBatchDraftKey = string.IsNullOrWhiteSpace(final.StageExecutionBatchDraftKey) ? null : final.StageExecutionBatchDraftKey,
                        StageExecutionBatchQty      = final.StageExecutionBatchQty,
                        Quantity         = final.Quantity,
                        UOM              = final.UOM,
                        PlannedStartTime = final.PlannedStartTime,
                        PlannedEndTime   = final.PlannedEndTime,
                        Duration         = durationMinutes,
                        Status           = "PLANNED",
                        IsLocked         = false,
                        IsCriticalPath   = false,
                        TaskType         = final.TaskType,
                        MTS_InstructionNo = mtsInstructionNo,
                        SetupSource      = final.SetupSource,
                        StageCode        = string.IsNullOrWhiteSpace(final.StageCode) ? null : final.StageCode,
                        CreatedAt        = now,
                        UpdatedAt        = now
                    });
                }

                // A1 汇总：让部门事实的覆盖度**在落库那一刻就有数** —— 命中率低即说明
                //   `MaterialStageDeptContext` 对本轮物料覆盖不足（而非「库里有事实」的错觉）。
                _logger.LogInformation(
                    "[Pegging] Task 部门事实（来源＝ERP 物料级部门码经 ProductionDepartment 归一化 → " +
                    "MaterialStageDeptContext(IsCurrent=1)）：命中={Hit}，未命中={Miss}，" +
                    "未命中留 NULL 并已逐条记 Issue[DEPT_CONTEXT_MISS]",
                    deptHit, deptMiss);

                // `Duration-2` 汇总：**回落数非 0 不是异常**（锁定继承 Task 1号位 恒给 null），
                // 但跨窗 Task 一旦落进回落分支，值会偏大 ⇒ 必须在日志里一眼看得见。
                _logger.LogInformation(
                    "[Pegging] Task.Duration 回写（= EffectiveProcessMinutes + SetupTime）：" +
                    "1号位 填值命中={Hit}，回落墙钟跨度={Fallback}（回落＝1号位 未给 EffectiveProcessMinutes，" +
                    "含锁定继承 Task，属预期）",
                    durationHit, durationFallbackNull);

                // 3. INSERT Pegging 血缘（C1修复：使用 solveResult.PhysicalPeggingDrafts，键为 FinalDraftId）
                var peggingRows = solveResult.PhysicalPeggingDrafts
                    .Where(ppd =>
                        finalDraftToTaskId.ContainsKey(ppd.UpstreamFinalDraftId) &&
                        finalDraftToTaskId.ContainsKey(ppd.DownstreamFinalDraftId))
                    .Select(ppd => new
                    {
                        PlanVersionId        = planVersionId,
                        UpstreamTaskId       = finalDraftToTaskId[ppd.UpstreamFinalDraftId],
                        DownstreamTaskId     = finalDraftToTaskId[ppd.DownstreamFinalDraftId],
                        UpstreamMaterialId   = ppd.UpstreamMaterialId,
                        DownstreamMaterialId = ppd.DownstreamMaterialId,
                        Quantity             = ppd.Quantity,
                        UOM                  = ppd.UOM,
                        PeggingType          = "TASK_TO_TASK",
                        AllocatedQuantity    = ppd.Quantity,
                        InheritedPriority    = ppd.InheritedPriority,
                        AllocationReason     = (string?)null
                    })
                    .ToList();

                // J16-04：被略去的血缘边必须**可数**（原实现静默过滤）。正常情形下这些边的端点就是上方
                //   已隔离的 Task（部门/资源/前序级联），故本计数应与 `excludedTotal` 覆盖的边集合一致。
                var droppedPeggingEdges = solveResult.PhysicalPeggingDrafts.Count - peggingRows.Count;
                if (droppedPeggingEdges > 0)
                {
                    _logger.LogWarning(
                        "[Pegging] Pegging 血缘边未落库：{Dropped}/{Total} 条（端点 Task 已隔离或不存在）" +
                        "⇒ 不写半条血缘；对应后序 Task 已在前置段一并隔离，数量计入 U（DomainKey={DomainKey}）",
                        droppedPeggingEdges, solveResult.PhysicalPeggingDrafts.Count, voucher.DomainKey);
                }

                if (peggingRows.Count > 0)
                {
                    await conn.ExecuteAsync(
                        @"INSERT INTO [Pegging] (
                              PlanVersionId,
                              UpstreamTaskId, DownstreamTaskId,
                              UpstreamMaterialId, DownstreamMaterialId,
                              Quantity, UOM, PeggingType,
                              LeadTimeDays, IsCrossDomain,
                              AllocatedQuantity, InheritedPriority, AllocationReason,
                              CreatedAt
                          )
                          VALUES (
                              @PlanVersionId,
                              @UpstreamTaskId, @DownstreamTaskId,
                              @UpstreamMaterialId, @DownstreamMaterialId,
                              @Quantity, @UOM, @PeggingType,
                              0, 0,
                              @AllocatedQuantity, @InheritedPriority, @AllocationReason,
                              GETDATE()
                          )",
                        peggingRows,
                        transaction: tx);
                }

                // B5. INSERT AllocationTaskShare (v5.1.2冻结设计：轻量中间表，支持批次拆分多对多)
                // PM《回复0928-2》§三/§六 裁决：防重复不是「显示问题」而是 **Candidate 正确性的前置条件**
                // （本文件 ComputeChangeSeedKeysAsync 以 `SUM(ShareQty) BY DemandKey` 为变化基线）。
                // ⇒ Inv1~Inv4 纳入正式运行校验，违例 **fail-closed**（不落库），不得静默跳过。
                // 被 Fail-Closed 隔离的 Draft 必须从**份额不变量**的输入中剔除：
                //   否则 Inv4「份额指向无生产任务的 Draft」会把「已隔离」升级成「整域失败」，与 J14-06
                //   「逐 Task 隔离、仅全缺才升级 Domain 失败」的口径相反。
                // J17-03（0号位 第十七轮）：份额不变量必须用**实际全部不落库的 Draft 集合**作为同一真相 ——
                //   缺部门 ∪ 资源非法 ∪ 前序隔离级联。只传「缺部门」会让非法资源/级联的份额留在校验输入里，
                //   又找不到对应 Task ⇒ Inv4 判「份额指向无生产任务」直接抛、整域回滚，
                //   与「逐 Task 隔离、仅全缺才升级 Domain 失败」的口径相反。
                var excludedForInvariants = excludedDraftIds
                    .Concat(cascade.Cascaded.Select(t => t.FinalDraftId))
                    .ToHashSet(StringComparer.Ordinal);
                ValidateAllocationShareInvariants(
                    solveResult, voucher, finalDraftToTaskId, excludedForInvariants);

                var seqToShareId = new Dictionary<long, long>();
                if (solveResult.AllocationShares.Count > 0)
                {
                    foreach (var share in solveResult.AllocationShares)
                    {
                        if (!finalDraftToTaskId.TryGetValue(share.FinalDraftId, out var taskId))
                            continue;

                        var allocation = voucher.SupplyAllocations
                            .FirstOrDefault(a => a.AllocationSequence == share.AllocationSequence);

                        if (allocation == null)
                        {
                            _logger.LogWarning(
                                "[PeggingPersist] AllocationSequence={Seq} 未找到对应的SupplyAllocation",
                                share.AllocationSequence);
                            continue;
                        }

                        // P0-06：TaskShare 真实 Demand 归属来自该 AllocationSequence 对应的 Allocation 自身，
                        // 不再用批次第一单 voucher.OrderId；RootOrderId 从 DemandKey 反解真实 OrderId。
                        var rootOrderId = ExtractOrderIdFromDemandKey(allocation.DemandKey);

                        var shareId = await conn.ExecuteScalarAsync<long>(
                            @"INSERT INTO AllocationTaskShare (
                                  PlanVersionId, AllocationSequence, DemandType, DemandKey,
                                  RootOrderId, TaskId, ShareQty, CreatedAt
                              ) OUTPUT INSERTED.Id
                              VALUES (
                                  @PlanVersionId, @AllocationSequence, @DemandType, @DemandKey,
                                  @RootOrderId, @TaskId, @ShareQty, @CreatedAt
                              )",
                            new
                            {
                                PlanVersionId = planVersionId,
                                AllocationSequence = share.AllocationSequence,
                                DemandType = "ORDER",
                                DemandKey = allocation.DemandKey,
                                RootOrderId = rootOrderId,
                                TaskId = taskId,
                                ShareQty = share.ComponentQty,
                                CreatedAt = now
                            },
                            transaction: tx);
                        seqToShareId[share.AllocationSequence] = shareId;
                    }
                }

                // B6. INSERT PeggingSupplyAllocation (仅对非Task供给)
                // v5.1.2: 直接使用allocation.AllocationSequence（在供需扣减时已生成）
                var nonTaskAllocations = voucher.SupplyAllocations
                    .Where(a => a.SourceType != Core.Enum.SupplySourceType.NEW_REQUIREMENT)
                    .ToList();

                Console.WriteLine($"[PersistDomainAndPeggingInTransactionAsync] 准备INSERT PeggingSupplyAllocation，nonTaskAllocations={nonTaskAllocations.Count}");

                // 查询ScheduleRunId（B6和B7都需要）
                var scheduleRunId = await conn.ExecuteScalarAsync<int?>(
                    "SELECT SourceScheduleRunId FROM PlanVersion WHERE Id = @PlanVersionId",
                    new { PlanVersionId = planVersionId },
                    transaction: tx) ?? 0;

                if (nonTaskAllocations.Count > 0)
                {
                    var supplyMaterialIds = nonTaskAllocations.Select(a => a.SupplyMaterialId).Distinct().ToList();
                    var materialMap = new Dictionary<int, string>();
                    for (var i = 0; i < supplyMaterialIds.Count; i += SqlServerInParameterLimit)
                    {
                        var chunk = supplyMaterialIds.Skip(i).Take(SqlServerInParameterLimit).ToList();
                        var batch = await conn.QueryAsync(
                            "SELECT Id, MaterialCode FROM Material WHERE Id IN @Ids",
                            new { Ids = chunk },
                            transaction: tx);
                        foreach (var r in batch)
                            materialMap[(int)r.Id] = (string)r.MaterialCode;
                    }

                    // PM《Stage、生产部门、Routing、Dependency、StageLeadTimeParam 接口裁决回复》§十一（2026-09-28）：
                    //   `RequiredStageCode` **两处保留** —— ① 运行输入 `LogicalProductionDemand.RequiredStageCode`（Solver 用，已回填）
                    //   ② Pegging 结果 `PeggingSupplyAllocation.NextRequiredStageCode`（追溯「为什么这个 Supply 可以承接这个需求」）。
                    //
                    // 【2026-09-29 修正】取值改为**按承接行自身物料直查**（主），`DemandKey` 关联降为兜底。
                    //   原实现只走 DemandKey ⇒ 经 `LogicalProductionDemand` 中转，而 LPD **只为「有生产缺口」的需求生成**
                    //   ⇒ 纯供给承接（库存/在途直接接走）的行取不到值。实测 PlanVersionId=540：**1,205 行只填出 40 行**，
                    //   而 BOM 侧有 30,652 个物料带阈值 ⇒ 缺的是取值路径，不是数据。
                    //   `SupplyAllocationItem` 无需求物料字段，但 `ValidateEligibility` 红线1 保证供需同物料
                    //   ⇒ 落库行的 `MaterialId`（= supply.MaterialId）即需求物料，可直接命中物料级映射。
                    var requiredStageByDemandKey = voucher.LogicalProductionDemands
                        .Where(d => !string.IsNullOrEmpty(d.RequiredStageCode))
                        .GroupBy(d => d.DemandKey, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.First().RequiredStageCode!, StringComparer.Ordinal);

                    var supplyRows = nonTaskAllocations
                        .Select(a =>
                        {
                            materialMap.TryGetValue(a.SupplyMaterialId, out var materialCode);
                            return new
                            {
                                PlanVersionId          = planVersionId,
                                ScheduleRunId          = scheduleRunId,
                                AllocationSequence     = a.AllocationSequence,
                                RootOrderId            = ExtractOrderIdFromDemandKey(a.DemandKey),
                                MaterialId             = a.SupplyMaterialId,
                                MaterialCode           = materialCode ?? string.Empty,
                                DemandFactoryCode      = a.FactoryCode,
                                DemandQty              = a.DemandQuantity,
                                AllocatedQty           = a.AllocatedQuantity,
                                SupplyType             = a.SourceType.ToString(),
                                SupplyFactoryCode      = a.FactoryCode,
                                KnownAvailableTime     = a.AvailableAt,
                                SupplyDocumentNo       = a.SourceReference,
                                // 主：按承接行自身物料查物料级映射（覆盖全部承接关系）
                                // 兜底：按 DemandKey 查运行输入侧已填值（防御性，二者同源，正常结果一致）
                                NextRequiredStageCode  = voucher.RequiredStageByMaterialId.TryGetValue(a.SupplyMaterialId, out var reqStage)
                                                             ? reqStage
                                                             : (requiredStageByDemandKey.TryGetValue(a.DemandKey, out var reqStageByKey)
                                                                 ? reqStageByKey
                                                                 : null),
                                CreatedAt              = now
                            };
                        })
                        .ToList();

                    await conn.ExecuteAsync(
                        @"INSERT INTO PeggingSupplyAllocation (
                              PlanVersionId, ScheduleRunId, AllocationSequence,
                              RootOrderId, MaterialId, MaterialCode,
                              DemandFactoryCode, DemandQty, AllocatedQty,
                              SupplyType, SupplyFactoryCode,
                              KnownAvailableTime, SupplyDocumentNo, NextRequiredStageCode, CreatedAt
                          ) VALUES (
                              @PlanVersionId, @ScheduleRunId, @AllocationSequence,
                              @RootOrderId, @MaterialId, @MaterialCode,
                              @DemandFactoryCode, @DemandQty, @AllocatedQty,
                              @SupplyType, @SupplyFactoryCode,
                              @KnownAvailableTime, @SupplyDocumentNo, @NextRequiredStageCode, @CreatedAt
                          )",
                        supplyRows,
                        transaction: tx);

                    Console.WriteLine($"[PersistDomainAndPeggingInTransactionAsync] PeggingSupplyAllocation INSERT成功: {supplyRows.Count} 条");
                }

                // B7. INSERT ScheduleExplanationFact (v5.1.2: 排程决策解释事实)
                //   ★ 2026-10-13（《PM回复1013》§3「必须保留原始 EvidenceJson」「不允许未排程数量丢失」）：
                //     原 `.Where(f => f.TaskId > 0)` 会把 `finalDraftToTaskId` 未命中的解释事实**整条丢弃**
                //     （连同它的 `EvidenceJson`）——且**无计数、无日志**，属静默丢数据。
                //     现改为：未命中 ⇒ `TaskId = NULL` 照样落库（DB 列本可空，与 B8 同形），并打显性计数。
                //     ⚠ 只「不再丢行」：**原本就落库的那些行取值逐字不变**。
                if (solveResult.ExplanationFacts.Count > 0)
                {
                    var factRows = solveResult.ExplanationFacts
                        .Select(fact =>
                        {
                            var mapped = finalDraftToTaskId.TryGetValue(fact.FinalDraftId, out var taskId);
                            return new
                            {
                                PlanVersionId = planVersionId,
                                ScheduleRunId = scheduleRunId,
                                ObjectType = fact.ObjectType,
                                OrderId = fact.OrderId,
                                TaskId = mapped ? (long?)taskId : (long?)null,
                                ResourceId = fact.ResourceId,
                                StageCode = fact.StageCode ?? string.Empty,
                                ReasonCode = fact.ReasonCode,
                                Severity = fact.Severity ?? string.Empty,
                                ImpactHours = fact.ImpactHours ?? 0m,
                                EvidenceJson = fact.EvidenceJson,
                                CreatedAt = now
                            };
                        })
                        .ToList();

                    if (factRows.Count > 0)
                    {
                        await conn.ExecuteAsync(
                            @"INSERT INTO [APS_Production].[dbo].[ScheduleExplanationFact] (
                                  PlanVersionId, ScheduleRunId, ObjectType, OrderId, TaskId,
                                  ResourceId, StageCode, ReasonCode, Severity, ImpactHours,
                                  EvidenceJson, CreatedAt
                              ) VALUES (
                                  @PlanVersionId, @ScheduleRunId, @ObjectType, @OrderId, @TaskId,
                                  @ResourceId, @StageCode, @ReasonCode, @Severity, @ImpactHours,
                                  @EvidenceJson, @CreatedAt
                              )",
                            factRows,
                            transaction: tx);

                        var unmappedFactCount = factRows.Count(f => f.TaskId == null);
                        Console.WriteLine(
                            $"[PersistDomainAndPeggingInTransactionAsync] ScheduleExplanationFact INSERT成功: {factRows.Count} 条"
                            + (unmappedFactCount > 0
                                ? $"（其中 {unmappedFactCount} 条无对应 FinalTask、TaskId=NULL —— 保留原始证据，不再丢弃）"
                                : string.Empty));
                    }
                }

                // ── 已排份额按 **LogicalDemandKey** 聚合（B8 未排净量 ＋ B10 Run 级三量对账 **共用**）──
                //   ★★ 2026-10-13 P3「未排净量闭合验证」修复 ★★
                //   原实现（§T-1013c，同日）把聚合键取成 `voucher.SupplyAllocations.DemandKey`，
                //   而所有查表用的都是 `u.DraftId`(=`LogicalDemandKey`)。两者**不是同一命名空间**：
                //     · `SupplyAllocation.DemandKey` = `ORDER_{OrderId}_{MaterialCode}_{Seq}`（业务需求键，
                //       `ExtractOrderIdFromDemandKey` 能从它解出订单号）；
                //     · `LogicalDemandKey`           = `{PlanVersionId}_{AllocationSequence}`（见 `BuildLogicalProductionDemand`）。
                //   实库实证：`AllocationTaskShare.DemandKey` = `ORDER_363139_SYN-FG-001_2`，
                //   而同表 `ScheduleExplanationFact.EvidenceJson.LogicalDemandKey` = `2_265307` / `142_1`。
                //   ⇒ 查表**恒不命中**，后果三连：
                //     (1) B8 的 `ScheduledShareQty` 恒 0 ⇒ `UnscheduledQty` 退化成**修复前的硬等于**（改动等于没改）；
                //     (2) `OverlapDetected` 恒 false ⇒ 「部分已排却报未排程」这个信号永不触发；
                //     (3) B10 拿 `ORDER_…` 键去比 `LogicalDemandKey` ⇒ **每条已排需求都被误报为「缺失」**、
                //         `ScheduledDemandCount` 无意义、Severity 恒 ERROR（对账行沦为噪声）。
                //   修法：走 1号位 自己的任务↔需求链接 `FinalTaskDraft.FinalDraftId → SourceDraftId`
                //   （`SourceDraftId` 即 `LogicalDemandKey`，含 `/WO:…`、`/FREE` 分片后缀，见 `:569`/`:581`），
                //   与 `UnscheduledTaskResult.DraftId` **同命名空间**，且**天然覆盖分片**。
                //   计算核已抽为纯函数 `QuantityReconciliation`（可单测，见 `QuantityReconciliationTests`）。
                // 只用**真正落库**的 FinalTask：被部门 Fail-Closed 隔离的那部分不构成「已排」，
                //   否则未排净量会少算、恒等式 Δ 被隔离量抵消（J14-06 要求「未落库量真实反映为未排」）。
                var scheduledAgg = QuantityReconciliation.AggregateScheduledByDemandKey(
                    executableTasks, solveResult.AllocationShares);
                var scheduledQtyByDemandKey = scheduledAgg.ScheduledQtyByDemandKey;
                if (scheduledAgg.UnmappedShareCount > 0)
                {
                    _logger.LogWarning(
                        "[PeggingPersist] {Count} 条 AllocationShare 的 FinalDraftId 无法经 FinalTask→SourceDraftId 归到需求键"
                        + "（未计入已排净量，恒等式 Δ 会显性报出）", scheduledAgg.UnmappedShareCount);
                }

                // 需求净量索引（B8 反查 NetOutputQty ＋ B10 对账 **共用**）
                var demandByKey = voucher.LogicalProductionDemands
                    .Where(d => !string.IsNullOrEmpty(d.LogicalDemandKey))
                    .GroupBy(d => d.LogicalDemandKey)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

                // B8. 真无解 Unscheduled 闭合记账（PM 2026-09-11：产能不足≠排不下，Unscheduled 只兜「真无解」异常）
                //   1号位 UnscheduledTaskResult.DraftId = LogicalDemandKey；2号位 反查 NetOutputQty，
                //   复用 ScheduleExplanationFact（不加表），**UnscheduledQty = max(NetOutputQty − Σ已排份额, 0)**；
                //   数量/血缘键进 EvidenceJson，TaskId=null（无对应 FinalTask）。
                //   ★ 2026-10-13：原为「整单记账、ΣTaskShare=0」的**未校验假定**（`UnscheduledQty = netOutputQty`）；
                //     现按上方聚合的已排份额落**真实剩余**（《PM回复1013》§3「未排净量须准确」）。
                // ★ 2026-10-17 J16-03（0号位 第十六轮）：原条件 `if (solveResult.UnscheduledTasks.Count > 0)` 把
                //   **整段逐需求对账**包在里面 ⇒ 1号位 报 0 条未排程、而 Q−S>0 时（**部门 Fail-Closed 隔离正是这种情形**）
                //   一行账都不写，缺口在库里看不见。现改为：只要本轮有需求就无条件以
                //   `voucher.LogicalProductionDemands` **全集**生成 Q/S/U（`UnscheduledTasks` 只提供原因，不是触发条件）。
                if (voucher.LogicalProductionDemands.Count > 0 || solveResult.UnscheduledTasks.Count > 0)
                {

                    // 不 Drop 未反查命中的 DraftId（数量不得静默消失）：demand 为 null 时 NetOutputQty 记 0，
                    // 但 EvidenceJson 仍带 LogicalDemandKey 可追溯，问题显性化。
                    //
                    // ── 原因码兜底**已移除**（0号位 2026-10-12《PM回复1012》§一.2／§三.3／§五 三处点名）──
                    //   原文：「**2号位不得在1号位没有具体原因时，直接填 `ScheduleExplanationFact.ReasonCode =
                    //   UNSCHEDULABLE`**；也不得为满足必填字段随意猜『资源不足』。需核对现有标准码与
                    //   `UnscheduledTaskResult`/Explanation 契约，**分别保存「未排程状态」和「有依据的原因」**。
                    //   无法获得具体原因属于**解释证据缺口**，应通知 1号位补齐，而不是创造业务原因。」
                    //   · 原实现 `ReasonCode = string.IsNullOrWhiteSpace(u.Reason) ? "UNSCHEDULABLE" : u.Reason`
                    //     属**自造原因码**：`UNSCHEDULABLE` 不在《APS数据库字段说明文档》§八.1 的 15 个冻结码内
                    //     （1号位 `PhaseThreeDiagnostics` 类注释已声明「只准取 15 码」）⇒ 越域。
                    //   · **两个概念本已分离**，无需新载体：**未排程状态** = 本行存在且 `TaskId=null`（`ObjectType='ORDER'`
                    //     ＋ `Severity='ERROR'`）；**有依据的原因** = `ReasonCode`。故 1号位 未给原因时
                    //     `ReasonCode` 留**空串**（DB 列 `nvarchar(100) NOT NULL` ⇒ 空串合法、无需 DDL），
                    //     并在 `EvidenceJson` 打 `ReasonCodeGap=true` 让该行自证是「缺口」而非「无原因」。
                    //   · 缺口**必须显性通知 1号位**（下方计数 + 逐条日志），不静默、不猜。
                    //   · 实况（2026-10-12 库测）：`ScheduleExplanationFact` 现有 13 行中 **`UNSCHEDULABLE` 出现 0 次**
                    //     ⇒ 本兜底**至今从未触发**（1号位 `PhaseFiveCompression.ReportUnscheduled` 的每条路径
                    //     都传非空 `Reason`）⇒ **本次改动不改变任何已有载荷**，只封住这个口子。
                    var reasonGapDraftIds = solveResult.UnscheduledTasks
                        .Where(u =>
                        {
                            if (!string.IsNullOrWhiteSpace(u.Reason)) return false;
                            // U=0（按真实份额已排满）没有可归因的未排量 ⇒ 不属于「原因缺口」，不通知补齐。
                            scheduledQtyByDemandKey.TryGetValue(u.DraftId, out var s);
                            demandByKey.TryGetValue(u.DraftId, out var d);
                            return QuantityReconciliation
                                .PerDemandRemaining(d?.NetOutputQty ?? 0m, s).UnscheduledQty > 0m;
                        })
                        .Select(u => u.DraftId)
                        .ToList();

                    // 值域外原因**单列记账**（0号位 2026-10-12 裁决 §六.3：「已发现 `BATCH_POLICY_MISSING`
                    //   等非标值需**单列整改**」）。冻结 §八.1 逐字要求「全文 ReasonCode 必须属于此列表」，
                    //   而实库该列**无 CHECK 约束** ⇒ 值域只能由写入方自律；此前按 `u.Reason` 原样直写
                    //   导致中文自由文本/非标码进了码列。此处改为：域内写码、域外写空串并把原值原样搬到
                    //   `EvidenceJson.UpstreamReasonCode`（证据不丢、按值计数可查），
                    //   放开条件见 `ScheduleReasonCodeDomain` 类注释（0号位 增码 / 冻结码表回写）。
                    var outOfDomainReasons = solveResult.UnscheduledTasks
                        .Where(u => !string.IsNullOrWhiteSpace(u.Reason)
                                    && !ScheduleReasonCodeDomain.IsAuthoritative(u.Reason))
                        .GroupBy(u => u.Reason.Trim(), StringComparer.Ordinal)
                        .OrderByDescending(g => g.Count())
                        .ToList();

                    // 重叠检出：既被部分排、又被列为未排程的 DraftId —— 显性化，不静默按整单记账。
                    // 判据与 `PerDemandRemaining` **同源**（必须 `已排>0 且 剩余>0`）：已排满（U=0）的
                    // 回执偏差不是重叠，不得混进本清单。
                    var overlapDraftIds = solveResult.UnscheduledTasks
                        .Where(u =>
                        {
                            scheduledQtyByDemandKey.TryGetValue(u.DraftId, out var alreadyScheduled);
                            demandByKey.TryGetValue(u.DraftId, out var overlapDemand);
                            return QuantityReconciliation
                                .PerDemandRemaining(overlapDemand?.NetOutputQty ?? 0m, alreadyScheduled)
                                .OverlapDetected;
                        })
                        .Select(u => u.DraftId)
                        .ToList();

                    var unscheduledRows = solveResult.UnscheduledTasks
                        .Select(u =>
                        {
                            demandByKey.TryGetValue(u.DraftId, out var demand);
                            var netOutputQty = demand?.NetOutputQty ?? 0m;
                            // 已排份额（同 LogicalDemandKey）⇒ **未排净量 = 需求净量 − 已排份额**（不得为负）
                            scheduledQtyByDemandKey.TryGetValue(u.DraftId, out var scheduledShareQty);
                            var (unscheduledQty, overlapDetected) =
                                QuantityReconciliation.PerDemandRemaining(netOutputQty, scheduledShareQty);
                            var hasReason = !string.IsNullOrWhiteSpace(u.Reason);
                            // 只有「1号位 给了原因**且该原因在冻结 15 码值域内**」才允许进码列；
                            // 域外值不丢弃、不猜测映射，转入 EvidenceJson 诊断位。
                            var reasonCode = ScheduleReasonCodeDomain.Normalize(u.Reason);
                            var outOfDomain = hasReason && reasonCode.Length == 0;
                            var evidence = System.Text.Json.JsonSerializer.Serialize(new
                            {
                                LogicalDemandKey   = u.DraftId,
                                MaterialId         = demand?.MaterialId,
                                DomainKey          = voucher.DomainKey,
                                AllocationSequence = demand?.AllocationSequence,
                                NetOutputQty       = netOutputQty,
                                // 已排净量（同 DemandKey 的 ΣAllocationTaskShare.ShareQty）——与未排净量构成
                                // 「需求净量 = 已排净量 + 未排净量」的对账三元组（可追溯）。
                                ScheduledShareQty  = scheduledShareQty,
                                UnscheduledQty     = unscheduledQty,
                                // true ⇒ 该需求**部分已排**却仍被 1号位 列为未排程：V1「整单记账」假定被打破，
                                //   显性化以便对账（《PM回复1013》§3「未排净量必须准确」）。
                                OverlapDetected    = overlapDetected,
                                // 解释证据缺口标记：1号位 未给具体原因（PM回复1012 §三.3 要求「分别保存
                                // 未排程状态与有依据的原因」⇒ 本行是**状态**，原因待 1号位 补齐）。
                                // 且只在**确有未排净量**时成立：U=0 的行已被排满，没有可归因的未排量。
                                ReasonCodeGap      = !hasReason && unscheduledQty > 0m,
                                // 原值留痕：**不是**改码、**不是**映射，只在码列留空后把原值搬到诊断位。
                                //   · 越域值（非冻结 15 码）：使「非标值单列整改」可逐值计数（0号位 2026-10-12 §六.3）；
                                //   · U=0（已排满）的行：1号位 虽报了未排程、但按真实份额算已无未排量，
                                //     其原因对「已排满」不成立 ⇒ 码列留空（U-004「不得错误贴业务原因」），
                                //     原值搬到此位，保证不丢上游回执原文。
                                UpstreamReasonCode = (outOfDomain || (hasReason && unscheduledQty <= 0m))
                                    ? u.Reason.Trim()
                                    : null,
                                // 来源标记（1号位 2026-10-17 §十一.2 要的「可取区分的过滤条件」）：
                                // 同一事务、同一 `CreatedAt` 下 `ObjectType='ORDER'` 有三族行，只按 ObjectType 取会混入
                                // 1号位 延期诊断行与跨版本连续性异常行 ⇒ 逐行标来源，两族统一用本键区分：
                                //   `UNSCHEDULED_TASK`（1号位 报上来的未排程）／`RECONCILIATION_REMAINDER`（对账剩余量）。
                                UnscheduledSource  = "UNSCHEDULED_TASK"
                            });
                            return new
                            {
                                PlanVersionId = planVersionId,
                                ScheduleRunId = scheduleRunId,
                                // 2026-10-12（1号位 §2.1）：ObjectType 值域 = ORDER/TASK/RESOURCE/STAGE/DOMAIN，
                                // 原 "DEMAND" 越域；本行填 OrderId ⇒ 取 ORDER（与 2026-10-07 0号位 P0-02 同向）。
                                ObjectType    = "ORDER",
                                OrderId       = demand?.OrderId,
                                TaskId        = (long?)null,
                                ResourceId    = (int?)null,
                                StageCode     = demand?.StartStageCode ?? string.Empty,
                                // 只透传 1号位 给的原因，且**必须落在冻结 15 码值域内**；未给或越域 ⇒ 留空
                                // （**不兜底、不猜、不映射**，PM回复1012 §三.3 ＋ 2026-10-12 §六.3）。
                                // U=0（已排满）⇒ 同样留空：该原因不作用于任何未排量，原值见 UpstreamReasonCode。
                                ReasonCode    = unscheduledQty > 0m ? reasonCode : string.Empty,
                                // U>0 ⇒ 确有未排净量，异常；U=0 ⇒ 1号位 报了未排程但按真实份额已排满，
                                //   这是**回执口径偏差**的正向记录（数量不欠），降为 INFO，不冒充排程异常。
                                Severity      = unscheduledQty > 0m ? "ERROR" : "INFO",
                                ImpactHours   = (decimal?)null,
                                EvidenceJson  = evidence,
                                // 与 remainderRows 同名同位：供全集计数/日志（Dapper 忽略多余属性，不参与 INSERT 绑定）
                                UnscheduledQty = unscheduledQty,
                                CreatedAt     = now
                            };
                        })
                        .ToList();

                    // ── J15-05：证据必须覆盖**全部需求**，不只是 1号位 报上来的 UnscheduledTaskResult ──
                    //   两类需求此前**没有任何逐需求 U 行**：
                    //     ① 被部门 Fail-Closed 隔离、因而未落 Task 的需求（S=0，U=Q）；
                    //     ② 既未被排、也未被 1号位 列为未排程的需求（此前只出现在域级 `MissingDemandKeys`）。
                    //   口径不变：U = max(NetOutputQty − Σ真实落库份额, 0)，Q 全额保留，**不因隔离而扣减**。
                    var unscheduledDraftIdSet = solveResult.UnscheduledTasks
                        .Select(u => u.DraftId)
                        .ToHashSet(StringComparer.Ordinal);

                    //   ★ 2026-10-17 J16-03：**不得**再按 `UnscheduledQty > 0` 筛掉已全部排定的需求 ——
                    //     审核位要的是「全已排／部分排／全未排都留可检索的同层需求账」，
                    //     否则 `Q` 只在未排行里可读，用 `Σ ATS` 推 Q 会混淆「已排」与「需排」。
                    //     U=0 行保留，但 Severity 降为 INFO（不是异常），来源标记 `RECONCILIATION_FULLY_SCHEDULED`。
                    var remainderRows = voucher.LogicalProductionDemands
                        .Where(d => !string.IsNullOrEmpty(d.LogicalDemandKey)
                                    && !unscheduledDraftIdSet.Contains(d.LogicalDemandKey))
                        .Select(d =>
                        {
                            scheduledQtyByDemandKey.TryGetValue(d.LogicalDemandKey, out var scheduledShareQty);
                            return new
                            {
                                Demand = d,
                                ScheduledShareQty = scheduledShareQty,
                                UnscheduledQty = Math.Max(d.NetOutputQty - scheduledShareQty, 0m)
                            };
                        })
                        .Select(x => new
                        {
                            PlanVersionId = planVersionId,
                            ScheduleRunId = scheduleRunId,
                            ObjectType    = "ORDER",
                            OrderId       = x.Demand.OrderId,
                            TaskId        = (long?)null,
                            ResourceId    = (int?)null,
                            StageCode     = x.Demand.StartStageCode ?? string.Empty,
                            // 不是 15 码内的排程原因：本行的「未排」是**对账剩余量**，原因待 1号位/部门事实补齐。
                            ReasonCode    = string.Empty,
                            // U>0 ⇒ 异常（ERROR）；U=0 ⇒ 已全部排定的**正向证据行**，不是异常（INFO）。
                            Severity      = x.UnscheduledQty > 0m ? "ERROR" : "INFO",
                            ImpactHours   = (decimal?)null,
                            EvidenceJson  = System.Text.Json.JsonSerializer.Serialize(new
                            {
                                LogicalDemandKey   = x.Demand.LogicalDemandKey,
                                MaterialId         = x.Demand.MaterialId,
                                DomainKey          = voucher.DomainKey,
                                AllocationSequence = x.Demand.AllocationSequence,
                                NetOutputQty       = x.Demand.NetOutputQty,
                                ScheduledShareQty  = x.ScheduledShareQty,
                                UnscheduledQty     = x.UnscheduledQty,
                                OverlapDetected    = false,
                                // 缺口标记只对「真未排净」成立：U>0 时原因码确为空缺（待 1号位/部门事实补），
                                // U=0（已排满）不是缺口 —— 该行是正向对账证据，无原因可缺。
                                // 2026-10-19 受控回归暴露：旧实现 U=0 行也打 `true`，使
                                // `UnscheduledSource=RECONCILIATION_FULLY_SCHEDULED` 的行被对账脚本标成 `REASON_GAP`，
                                // 把「已排满」读成「原因缺失」。
                                ReasonCodeGap      = x.UnscheduledQty > 0m,
                                UpstreamReasonCode = (string?)null,
                                // 本行来源：不是 1号位 的未排程回执，而是「Q − 真实落库 S」的对账剩余量。
                                //   U=0 的行改标 `RECONCILIATION_FULLY_SCHEDULED`：语义是「已排满的需求账」，
                                //   与「有未排净量」区分开（同一张表、同一 Run，可按来源逐族计数）。
                                UnscheduledSource  = x.UnscheduledQty > 0m
                                    ? "RECONCILIATION_REMAINDER"
                                    : "RECONCILIATION_FULLY_SCHEDULED"
                            }),
                            // 仅供下方计数/日志使用（Dapper 忽略多余的匿名属性，不参与 INSERT 参数绑定）
                            UnscheduledQty = x.UnscheduledQty,
                            CreatedAt = now
                        })
                        .ToList();

                    if (remainderRows.Count > 0)
                    {
                        var remainderWithGap = remainderRows.Count(r => r.UnscheduledQty > 0m);
                        Console.WriteLine(
                            $"[PersistDomainAndPeggingInTransactionAsync] 全集需求对账记账（J16-03）：{remainderRows.Count} 个需求未被 1号位 列为未排程，" +
                            $"其中未排净 {remainderWithGap} 条（U>0，UnscheduledSource=RECONCILIATION_REMAINDER）、已排满 " +
                            $"{remainderRows.Count - remainderWithGap} 条（U=0，RECONCILIATION_FULLY_SCHEDULED，Severity=INFO）；" +
                            "Q 全额保留、S 取真实落库份额");
                    }

                    var allUnscheduledRows = unscheduledRows.Concat(remainderRows).ToList();

                    // 非标值单列（逐值计数）——供 0号位 §六.3「收集原因…再判断是否存在需要新增的具体原因码」取数。
                    if (outOfDomainReasons.Count > 0)
                    {
                        Console.WriteLine(
                            $"[PersistDomainAndPeggingInTransactionAsync] ⚠ ReasonCode 值域外原因（已留空并转 EvidenceJson.UpstreamReasonCode）" +
                            $"{outOfDomainReasons.Sum(g => g.Count())}/{unscheduledRows.Count} 条、{outOfDomainReasons.Count} 个不同取值：" +
                            string.Join("；", outOfDomainReasons.Select(g => $"「{g.Key}」×{g.Count()}")));
                    }

                    // 重叠（已排份额 ＋ 同时被列为未排程）**显性化**——不静默、不按整单记账。
                    if (overlapDraftIds.Count > 0)
                    {
                        Console.WriteLine(
                            $"[PersistDomainAndPeggingInTransactionAsync] ⚠ 未排程需求中检出**已排份额**：{overlapDraftIds.Count}/{unscheduledRows.Count} 条" +
                            "（未排净量已按「需求净量 − 已排份额」落真实剩余，EvidenceJson.OverlapDetected=true、" +
                            "ScheduledShareQty 为该需求已排净量）");
                    }

                    // 解释证据缺口**显性通知 1号位**（PM回复1012 §三.3：缺口要通知补齐，不得自造原因）。
                    // 不改载荷语义（行照样落、数量照样闭合），只把「谁缺原因」打到运行日志上。
                    if (reasonGapDraftIds.Count > 0)
                    {
                        Console.WriteLine(
                            $"[PersistDomainAndPeggingInTransactionAsync] ⚠ 解释证据缺口：{reasonGapDraftIds.Count}/{unscheduledRows.Count} " +
                            $"条未排程需求 1号位 未给具体原因（ReasonCode 已留空、EvidenceJson.ReasonCodeGap=true）" +
                            $"⇒ 请 1号位 补齐具体原因（15 码内）；样本 DraftId：{string.Join(", ", reasonGapDraftIds.Take(10))}");
                    }

                    if (allUnscheduledRows.Count > 0)
                    {
                        await conn.ExecuteAsync(
                            @"INSERT INTO [APS_Production].[dbo].[ScheduleExplanationFact] (
                                  PlanVersionId, ScheduleRunId, ObjectType, OrderId, TaskId,
                                  ResourceId, StageCode, ReasonCode, Severity, ImpactHours,
                                  EvidenceJson, CreatedAt
                              ) VALUES (
                                  @PlanVersionId, @ScheduleRunId, @ObjectType, @OrderId, @TaskId,
                                  @ResourceId, @StageCode, @ReasonCode, @Severity, @ImpactHours,
                                  @EvidenceJson, @CreatedAt
                              )",
                            allUnscheduledRows,
                            transaction: tx);

                        Console.WriteLine(
                            $"[PersistDomainAndPeggingInTransactionAsync] Unscheduled 闭合记账 ScheduleExplanationFact INSERT成功: {allUnscheduledRows.Count} 条" +
                            $"（其中 1号位 未排程回执 {unscheduledRows.Count} 条、对账剩余量 {remainderRows.Count} 条）");
                    }
                }

                // B9. 跨版本连续性 Execution Over-Commit / Demand Mismatch 异常登记（G6，§9.1/§9.2）
                //   复用 ScheduleExplanationFact（与 Q7 Unscheduled 同表，不加表）：E>Q 执行超量 / Q=0 仍开工，
                //   均不产 Task、只登记异常事实供后续对账；ObjectType="ORDER"、TaskId=null、Severity 按 Kind。
                //
                //   ── 2026-10-12：`ReasonCode = e.Kind` **已撤**（与上方 UNSCHEDULABLE 同类越域，一并整改）──
                //   冻结文档《APS数据库字段说明文档 v5.1.10》§八.1（:4863）原文：「**权威枚举（全文 ReasonCode
                //   必须属于此列表，0号位审批冻结）**：RESOURCE_CAPACITY_WAIT / MATERIAL_SHORTAGE / PRECEDENCE_WAIT /
                //   FROZEN_ZONE_LOCK / ROUTING_FALLBACK / STAGE_LEADTIME_FALLBACK / BOM_DEGRADE / CROSS_ORG_HANDOFF /
                //   PRIORITY_LOWER_THAN_OTHERS / DUE_DATE_RISK / LOGISTICS_DELAY / PRIORITY_INHERITANCE /
                //   CROSS_DOMAIN_VERSION_MISMATCH_RISK / MANUAL_COMPLETED_SHORT / EQUIPMENT_BREAKDOWN_RISK」。
                //   本处 4 个字面量（`EXECUTION_OVER_COMMIT` / `DEMAND_MISMATCH` / `DUPLICATE_WO_SHARE` /
                //   `PI_FREE_OVER_COMMIT`）**一个都不在 15 码内**，且**全仓冻结文档 0 命中**（自造字面量）；
                //   语义上也**不是排程原因**，而是**数据一致性异常类型** ⇒ 硬塞进排程原因字典是错的。
                //   处置按同文档 §八.1 注解的既有模式：「枚举**外**的原因**不新增码**，细分改由**本表自己的**
                //   `EvidenceJson` 外壳 `evidenceType` 承载」⇒ `ReasonCode` 留空，`Kind` 改由
                //   `EvidenceJson.evidenceType` 承载（**信息不丢**，只是不占原因码位）。
                //   ⚠ 如 0号位 认为这 4 类需在 15 码内表达 ⇒ 请指定映射或批准增码（PM回复1012 §三.5 通道）；
                //   我方**不自行挑一个近似码**顶上。实况：库中本类行**当前 0 条** ⇒ 本改动零载荷影响。
                if (overCommits.Count > 0)
                {
                    var overCommitRows = overCommits
                        .Select(e => new
                        {
                            PlanVersionId = planVersionId,
                            ScheduleRunId = scheduleRunId,
                            // 2026-10-12（1号位 §2.1）：同 Q7，ObjectType 越域 "DEMAND"→"ORDER"（本行填 OrderId）。
                            ObjectType    = "ORDER",
                            OrderId       = e.OrderId,
                            TaskId        = (long?)null,
                            ResourceId    = (int?)null,
                            StageCode     = e.StartStageCode,
                            // 留空：本类异常**不是 15 码内的排程原因**（细分走 EvidenceJson.evidenceType）。
                            ReasonCode    = string.Empty,
                            Severity      = e.Severity,
                            ImpactHours   = (decimal?)null,
                            EvidenceJson  = System.Text.Json.JsonSerializer.Serialize(new
                            {
                                // 冻结文档 §八.1 EvidenceJson 外壳的 `evidenceType` 位（原因码枚举外的细分在此承载）。
                                evidenceType            = e.Kind,
                                ProductionInstructionNo = e.ProductionInstructionNo,
                                LogicalDemandKey        = e.LogicalDemandKey,
                                DomainKey               = e.DomainKey,
                                AllocationSequence      = e.AllocationSequence,
                                MaterialId              = e.MaterialId,
                                Quantity                = e.Quantity,
                                ContinuationQty         = e.ContinuationQty,
                                ExcessQty               = e.ExcessQty,
                                Kind                    = e.Kind
                            }),
                            CreatedAt = now
                        })
                        .ToList();

                    await conn.ExecuteAsync(
                        @"INSERT INTO [APS_Production].[dbo].[ScheduleExplanationFact] (
                              PlanVersionId, ScheduleRunId, ObjectType, OrderId, TaskId,
                              ResourceId, StageCode, ReasonCode, Severity, ImpactHours,
                              EvidenceJson, CreatedAt
                          ) VALUES (
                              @PlanVersionId, @ScheduleRunId, @ObjectType, @OrderId, @TaskId,
                              @ResourceId, @StageCode, @ReasonCode, @Severity, @ImpactHours,
                              @EvidenceJson, @CreatedAt
                          )",
                        overCommitRows,
                        transaction: tx);

                    Console.WriteLine($"[PersistDomainAndPeggingInTransactionAsync] Over-Commit 异常登记 ScheduleExplanationFact INSERT成功: {overCommitRows.Count} 条");
                }

                // B10. Run 级**三量对账行**（《PM回复1013》§3：2号位 确保生产需求净量、已排净量、未排净量
                //      **准确且可追溯**）—— 复用 `ScheduleExplanationFact`（**不加表、零 DDL**）。
                //
                //   【为什么需要】「**需求净量 = 已排净量 + 未排净量**」是**恒等式**，成立前提只有两条：
                //     ①并集完整（不丢）②交集为空（不重）。B7 已保证不再丢行、B8 已保证重叠显性化；
                //     但若某条需求**既未产生任何份额、又未被 1号位 报为未排程**，它在两张业务表里**都不存在**
                //     ⇒ 只靠 ① ② 抓不到。唯一办法是把 **Run 级总量**记下来，事后比对才能发现「凭空蒸发」的部分。
                //
                //   【为什么 ObjectType='DOMAIN'】值域本为 ORDER/TASK/RESOURCE/STAGE/DOMAIN（见上方 B8 注释），
                //     本行是**域/Run 级**事实，取 DOMAIN 名实相符；实库该列**无 CHECK 约束**（已查 `sys.check_constraints`）。
                //
                //   【口径】三者全部**净量**、且全部在内存可得（**不新增任何落库量**）：
                //     需求净量Σ = Σ LogicalProductionDemand.NetOutputQty；
                //     已排净量Σ = Σ AllocationTaskShare.ComponentQty（按 DemandKey 聚合，见上方共用块）；
                //     未排净量Σ = 与 B8 **同式** Σ max(NetOutputQty − 已排份额, 0)。
                //   ★ 【未排净量必须分两段记，2026-10-19 复核】`UnscheduledQtyTotal` 只覆盖
                //     1号位 **报上来的** `UnscheduledTasks` ⇒ 它**不是**本 Run 的全部未排净量。
                //     未报未排程的需求（Missing／UnderScheduled 两族）的剩余量单独记 `UnreportedRemainderQty`；
                //     两段之和 `UnscheduledQtyClosedTotal` 才是与 `需求净量Σ − 已排净量Σ` 对齐的量。
                //     只读前者会把「凭空蒸发／部分已排未报」的缺口读成 0。
                //
                //   【不阻断】本行**只记账、不 fail-closed**：`DeltaQty≠0` 是**信号**不是本方法的失败条件
                //     （PM 2026-09-11 硬口径：排不下 ≠ 产能不足；记账差异不得反过来阻断落库）。
                //     Δ≠0 或存在「缺失需求」时置 `Severity='ERROR'` 并打显性日志，供对账取数。
                //   【Severity 取值】本行首次引入 `'INFO'`（实库此前只有 `'ERROR'`）——「对账通过」不是错误；
                //     若 0号位 另有 Severity 值域规定，请指定，我方随之调整。
                {
                    //   计算核 = 纯函数 `QuantityReconciliation.Reconcile`（与 B8 单需求算式**同一处口径**，
                    //   见 `QuantityReconciliation.PerDemandRemaining`），可单测。
                    var recon = QuantityReconciliation.Reconcile(
                        voucher.LogicalProductionDemands, scheduledQtyByDemandKey, solveResult.UnscheduledTasks);

                    //   闭合判据 = Δ=0 **且** 三条失效清单全空。三条清单各管一类恒等式覆盖不到的缺口：
                    //     Missing          ①既无份额、又未报未排程（凭空蒸发）
                    //     UnderScheduled   ③有份额但不足、且未报未排程（**原口径的哑火点**：Δ 会报警但旧名单为空）
                    //     UnmatchedSched   ④份额归到一个 voucher 里不存在的需求键（1号位 回传越域键）
                    var hasDiscrepancy =
                        recon.DeltaQty != 0m
                        || recon.MissingDemandKeys.Count > 0
                        || recon.UnderScheduledDemandKeys.Count > 0
                        || recon.UnmatchedScheduledKeys.Count > 0;

                    var reconciliationEvidence = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        EvidenceType           = "DOMAIN_QUANTITY_RECONCILIATION",
                        DomainKey              = voucher.DomainKey,
                        DemandNetQtyTotal      = recon.DemandNetQtyTotal,
                        ScheduledQtyTotal      = recon.ScheduledQtyTotal,
                        // 1号位 报上来的未排程集合的剩余量之**和**（口径与 B8 `UNSCHEDULED_TASK` 行同源）
                        UnscheduledQtyTotal    = recon.UnscheduledQtyTotal,
                        // 未报未排程的需求（Missing／UnderScheduled）的剩余量之和 —— 旧口径整族漏记的部分
                        UnreportedRemainderQty = recon.UnreportedRemainderQty,
                        // 闭总量 = 上两者之和；无越域份额时严格等于 `DemandNetQtyTotal − ScheduledQtyTotal`
                        UnscheduledQtyClosedTotal = recon.UnscheduledQtyTotal + recon.UnreportedRemainderQty,
                        DeltaQty               = recon.DeltaQty,
                        DemandCount            = recon.DemandCount,
                        ScheduledDemandCount   = recon.ScheduledDemandCount,
                        UnscheduledDemandCount = recon.UnscheduledDemandCount,
                        MissingDemandCount     = recon.MissingDemandKeys.Count,
                        MissingDemandKeys      = recon.MissingDemandKeys.Take(10).ToList(),
                        // 2026-10-13 P3：两条**新增**失效清单（原设计只覆盖 Missing，部分已排的缺口无处可查）
                        UnderScheduledDemandCount = recon.UnderScheduledDemandKeys.Count,
                        UnderScheduledDemandKeys  = recon.UnderScheduledDemandKeys.Take(10).ToList(),
                        UnmatchedScheduledCount   = recon.UnmatchedScheduledKeys.Count,
                        UnmatchedScheduledKeys    = recon.UnmatchedScheduledKeys.Take(10).ToList(),

                        // 3号位 2026-10-25《缺配三态词表说明 v1.0》§四.1：缺配三态**只读计数**落在同一诊断位
                        // （沿本行既有 `EvidenceJson` 诊断模式，不立新表、不改契约 DTO）。
                        // ⚠ 只读：不参与判定、不中断 Run；`return 100m` 无损兜底语义不变。
                        // ⚠ 口径：物料**去重**计数；`Duplicate` 为整份快照 `PlanningYields` 的同 (MaterialId, StageCode) 多规则键数。
                        PlanningYieldQueriedCount      = supplyPool?.YieldDiagnostics.QueriedCount ?? 0,
                        PlanningYieldMissingCount      = supplyPool?.YieldDiagnostics.MissingCount ?? 0,
                        PlanningYieldOutOfRangeCount   = supplyPool?.YieldDiagnostics.OutOfRangeCount ?? 0,
                        PlanningYieldDuplicateKeyCount = supplyPool?.PlanningYieldDuplicateKeyCount() ?? 0,
                        PlanningYieldMissingSamples    = supplyPool?.YieldDiagnostics.MissingSamples ?? Array.Empty<string>(),
                        PlanningYieldOutOfRangeSamples = supplyPool?.YieldDiagnostics.OutOfRangeSamples ?? Array.Empty<string>()
                    });

                    await conn.ExecuteAsync(
                        @"INSERT INTO [APS_Production].[dbo].[ScheduleExplanationFact] (
                              PlanVersionId, ScheduleRunId, ObjectType, OrderId, TaskId,
                              ResourceId, StageCode, ReasonCode, Severity, ImpactHours,
                              EvidenceJson, CreatedAt
                          ) VALUES (
                              @PlanVersionId, @ScheduleRunId, @ObjectType, @OrderId, @TaskId,
                              @ResourceId, @StageCode, @ReasonCode, @Severity, @ImpactHours,
                              @EvidenceJson, @CreatedAt
                          )",
                        new
                        {
                            PlanVersionId = planVersionId,
                            ScheduleRunId = scheduleRunId,
                            ObjectType    = "DOMAIN",
                            OrderId       = (long?)null,
                            TaskId        = (long?)null,
                            ResourceId    = (int?)null,
                            StageCode     = string.Empty,
                            // 本行**不是 15 码内的排程原因**，是**对账事实** ⇒ 与 B8/B9 同口径留空
                            //（细分由 EvidenceJson.evidenceType 承载，不占原因码位、不新增第 16 码）。
                            ReasonCode    = string.Empty,
                            Severity      = hasDiscrepancy ? "ERROR" : "INFO",
                            ImpactHours   = (decimal?)null,
                            EvidenceJson  = reconciliationEvidence,
                            CreatedAt     = now
                        },
                        transaction: tx);

                    var reconciliationMessage =
                        $"[PersistDomainAndPeggingInTransactionAsync] Run 级三量对账（ObjectType=DOMAIN）："
                        + $"需求净量Σ={recon.DemandNetQtyTotal}，已排净量Σ={recon.ScheduledQtyTotal}，"
                        + $"未排净量Σ={recon.UnscheduledQtyTotal + recon.UnreportedRemainderQty}"
                        + $"（1号位回执 {recon.UnscheduledQtyTotal} ＋ 未报未排程 {recon.UnreportedRemainderQty}），"
                        + $"差额={recon.DeltaQty}；需求 {recon.DemandCount} 条"
                        + $"（已排 {recon.ScheduledDemandCount}／未排 {recon.UnscheduledDemandCount}／"
                        + $"**缺失 {recon.MissingDemandKeys.Count}／部分已排 {recon.UnderScheduledDemandKeys.Count}／"
                        + $"越域份额 {recon.UnmatchedScheduledKeys.Count}**）";

                    Console.WriteLine(hasDiscrepancy
                        ? reconciliationMessage
                          + " ⚠ 恒等式未闭合（缺失=既无份额又未报未排程；部分已排=有份额但不足且未报未排程；越域=份额键不在需求集）"
                          + $"｜缺失样本：{string.Join(", ", recon.MissingDemandKeys.Take(10))}"
                          + $"｜部分已排样本：{string.Join(", ", recon.UnderScheduledDemandKeys.Take(10))}"
                        : reconciliationMessage);
                }

                // ── B11. Demand 级完成时间事实（0号位 2026-10-25《四份回执联合核证》§二-5／§五 P0-B）──
                //   `DomainSolveResult.DemandCompletions` 在此之前于 Application／Engine／Web **零消费**，
                //   0号位 点名「出入口断链」。此处补 2号位 这一半：**只读记账**，零 DDL、零新表、零契约变更。
                //   ⚠ 不建 Task／TaskNo／MES工单（0号位 §9.2／§9.3）—— 本块只读 `Fact` 计数与样本。
                //   ⚠ `CompletionTimeCertain == false` 的行**不得**作承诺／供给可用时间／下游 `AvailableTime`；
                //     闸门由 `DemandCompletionDiagnostics.CertainOnly` 承担（单测覆盖）。本行只如实计数。
                //   ⚠ 当前生产侧尚未置值（恒 `null`）⇒ `CertainNullCount == FactCount` 是预期状态，不是告警。
                {
                    var completionSummary = DemandCompletionDiagnostics.Summarize(solveResult.DemandCompletions);

                    var completionEvidence = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        EvidenceType          = "DEMAND_COMPLETION_TIME",
                        DomainKey             = voucher.DomainKey,
                        FactCount             = completionSummary.FactCount,
                        CertainTrueCount      = completionSummary.CertainTrueCount,
                        CertainFalseCount     = completionSummary.CertainFalseCount,
                        CertainNullCount      = completionSummary.CertainNullCount,
                        EmptyDemandKeyCount   = completionSummary.EmptyDemandKeyCount,
                        StageTimingSegmentCount          = completionSummary.StageTimingSegmentCount,
                        MissingStageLeadTimeSegmentCount = completionSummary.MissingStageLeadTimeSegmentCount,
                        UncertainSamples      = completionSummary.UncertainSamples,
                        UnannotatedSamples    = completionSummary.UnannotatedSamples,
                        // 闸门口径留痕：读方不必翻代码就知道哪一类被排除
                        Gate = "CompletionTimeCertain == false ⇒ 不作承诺／供给可用时间／下游 AvailableTime；"
                             + "null = 未标注按现状放行；true = 可信",
                        NotATask = "StageTimings 为纯时间节点，不得据其建 Task／TaskNo／MES工单（0号位 §9.2／§9.3）"
                    });

                    await conn.ExecuteAsync(
                        @"INSERT INTO [APS_Production].[dbo].[ScheduleExplanationFact] (
                              PlanVersionId, ScheduleRunId, ObjectType, OrderId, TaskId,
                              ResourceId, StageCode, ReasonCode, Severity, ImpactHours,
                              EvidenceJson, CreatedAt
                          ) VALUES (
                              @PlanVersionId, @ScheduleRunId, @ObjectType, @OrderId, @TaskId,
                              @ResourceId, @StageCode, @ReasonCode, @Severity, @ImpactHours,
                              @EvidenceJson, @CreatedAt
                          )",
                        new
                        {
                            PlanVersionId = planVersionId,
                            ScheduleRunId = scheduleRunId,
                            ObjectType    = "DOMAIN",
                            OrderId       = (long?)null,
                            TaskId        = (long?)null,
                            ResourceId    = (int?)null,
                            StageCode     = string.Empty,
                            ReasonCode    = string.Empty,
                            // 诊断事实而非排程错误：存量语义里「不可信时间」是信号不是失败（与 B10 'INFO' 同口径）
                            Severity      = "INFO",
                            ImpactHours   = (decimal?)null,
                            EvidenceJson  = completionEvidence,
                            CreatedAt     = now
                        },
                        transaction: tx);

                    _logger.LogInformation(
                        "[Pegging][Demand完成时间] 域={Domain} 事实={Facts} 可信={Certain} 不可信={Uncertain} 未标注={Unannotated} 段={Segments}(缺LT段 {MissingLt})",
                        voucher.DomainKey,
                        completionSummary.FactCount,
                        completionSummary.CertainTrueCount,
                        completionSummary.CertainFalseCount,
                        completionSummary.CertainNullCount,
                        completionSummary.StageTimingSegmentCount,
                        completionSummary.MissingStageLeadTimeSegmentCount);
                }

                _logger.LogInformation(
                    "[Pegging] 统一事务提交: Task={Tasks}, Pegging={Pegging} (PlanVersionId={PlanVersionId})",
                    tasks.Count, peggingRows.Count, planVersionId);
                Console.WriteLine($"[PersistDomainAndPeggingInTransactionAsync] 事务即将返回: Tasks={tasks.Count}, Pegging={peggingRows.Count}");

                return (tasks, peggingRows.Count, nonTaskAllocations.Count);
            },
            db: DatabaseId.APS);
    }

    /// <summary>
    /// 结果红线校验（PM 口径，6 项全）。
    /// 在 Solver 与落库之前调用，返回错误列表；空列表 = 通过。
    /// ① DemandQuantity 由 TraverseBomNode 逐节点累计；③ PhysicalSourceKey 由 SupplyPool.Add 填充；
    /// ⑤⑥ SH 读 ShippingInstructionNo（INTER_FACTORY_ORDER 分配落 SH No）+ 池内 SH 段 OriginalQty（=指令总量 Order.Quantity）；
    /// ⑤ 判「分配携带的 SH 身份 = 该供给自身的 SH 身份」（不跨 SH 串用），**不判「一个需求只能对一条 SH」**。
    /// </summary>
    private static List<string> ValidatePeggingResult(SupplyPool pool, PeggingResultVoucher voucher)
    {
        var errors = new List<string>();

        // 1. Demand 闭合：Σ(已分配) + 短缺 不得超过需求总量（DemandQuantity 为 0 表示无需求节点，跳过）
        if (voucher.DemandQuantity > 0m)
        {
            var totalAllocated = voucher.SupplyAllocations.Sum(a => a.AllocatedQuantity);
            if (totalAllocated + voucher.ShortageQuantity > voucher.DemandQuantity)
                errors.Add($"Demand 闭合失败: 已分配 {totalAllocated} + 短缺 {voucher.ShortageQuantity} > 需求 {voucher.DemandQuantity}");
        }

        // 2. SupplyBalance 非负：任一供给条目 RemainingQty < 0 即超额消费
        var overConsumed = pool.GetAllEntries().Where(e => e.RemainingQty < 0m).ToList();
        if (overConsumed.Count > 0)
            errors.Add($"SupplyBalance 为负: {overConsumed.Count} 条供给被超额消费（RemainingQty < 0）");

        // 3. 同物理 Supply 不重复消费（判定见 FindPhysicalOverConsumption，纯函数可单测）
        errors.AddRange(FindPhysicalOverConsumption(
            pool.GetAllEntries().Select(e => (e.PhysicalSourceKey, e.OriginalQty, e.Allocations.Sum(a => a.AllocatedQty)))));


        // 4. Allocation 合法
        var items = voucher.SupplyAllocations;
        if (items.Any(a => a.AllocatedQuantity <= 0m))
            errors.Add("存在非法分配量：AllocatedQuantity <= 0");
        var seqs = items.Select(a => a.AllocationSequence).ToList();
        if (seqs.Any(s => s <= 0))
            errors.Add("存在非法 AllocationSequence（<= 0）");
        if (seqs.Distinct().Count() != seqs.Count)
            errors.Add("AllocationSequence 存在重复");
        for (var i = 1; i < seqs.Count; i++)
        {
            if (seqs[i] <= seqs[i - 1])
            {
                errors.Add("AllocationSequence 未单调递增");
                break;
            }
        }
        if (items.Any(a => !Enum.IsDefined(typeof(SupplySourceType), a.SourceType)))
            errors.Add("存在非法 SourceType");

        // 5/6. SH 同 SH 匹配不串 SH（= 分配携带的 SH 身份与该供给自身的 SH 身份一致）/ 份额不重复计量：
        //      读 ShippingInstructionNo（SH 分配落 SH No）+ 池内 SH 段总量（=Order.Quantity 指令总量）
        var shipmentTotals = pool.GetAllEntries()
            .Where(e => e.SourceType == Core.Enum.SupplySourceType.INTER_FACTORY_ORDER
                     && !string.IsNullOrWhiteSpace(e.PhysicalSourceKey))
            .GroupBy(e => e.PhysicalSourceKey!)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.OriginalQty), StringComparer.Ordinal);
        errors.AddRange(ValidateShConsistency(voucher.SupplyAllocations, shipmentTotals));

        return errors;
    }

    /// <summary>
    /// R03-01/02：同一 <c>PhysicalSourceKey</c> 的**跨条目合计**已分配量不得超过该物理量。
    /// 口径：
    ///   - **先在组上合计、再与物理量比**；逐条自比在同键多条目时恒不触发
    ///     （每条各自都没超，合计已超）；
    ///   - 物理量取组内 <c>OriginalQty</c> 的**最大值**：同键多条目是同一批物理库存的多个呈现
    ///     （跨族/跨域重复装载），不是各自独立的分片，求和会把重复放大当成合法容量。
    /// 无键条目（虚拟供给：NEW_REQUIREMENT / PLANNING_PURCHASE_PLACEHOLDER / 上游域产物）不参与。
    /// </summary>
    internal static List<string> FindPhysicalOverConsumption(
        IEnumerable<(string? PhysicalKey, decimal OriginalQty, decimal AllocatedQty)> entries)
    {
        var errors = new List<string>();
        foreach (var group in entries
                     .Where(e => !string.IsNullOrWhiteSpace(e.PhysicalKey))
                     .GroupBy(e => e.PhysicalKey!, StringComparer.Ordinal))
        {
            var consumed    = group.Sum(e => e.AllocatedQty);
            var physicalQty = group.Max(e => e.OriginalQty);
            if (consumed > physicalQty)
                errors.Add($"物理供给 {group.Key} 被重复消费: 同键合计已分配 {consumed} > 物理量 {physicalQty}（条目数 {group.Count()}）");
        }
        return errors;
    }

    /// <summary>
    /// A5：SH 源厂是否被「**目标厂静默冒充**」—— 冻结 DDL `08:83` 明令禁止的情形
    /// （逐字：「INTER_FACTORY禁止目标Factory静默冒充源工厂。」）。
    ///
    /// 判据：源厂**有值且等于**目标厂。
    /// ⚠ 源厂为 NULL ⇒ **不属于本 Issue**（那是「源厂缺失」，另一种性质，不得与本情形合并计数）。
    ///
    /// **仅供登记 Issue 使用**：不参与任何入池取值 / 分支决策（入池厂恒为 `TargetFactoryId`，行为不变）。
    /// </summary>
    internal static bool IsSourceFactoryMasked(int? sourceFactoryId, int targetFactoryId)
        => sourceFactoryId.HasValue && sourceFactoryId.Value == targetFactoryId;

    /// <summary>
    /// A5 / G-27：SH 源厂「未知」判据。
    ///
    /// `SourceFactoryId IS NULL` 的语义是**字典缺项**（`sp_ValidateAndPromoteOrders` 1h：
    /// ERP `RevInstrProcCode` 有值但未命中 `ext_MES_ProcessCode_View`），
    /// 与「源厂＝目标厂（冒充）」是两件不同的事，**不得合并成一个计数**。
    /// 单列出来是为了让调用侧的三态分支可被单测断言，而不是只存在于日志字符串里。
    /// </summary>
    internal static bool IsSourceFactoryUnknown(int? sourceFactoryId)
        => !sourceFactoryId.HasValue;

    /// <summary>
    /// A-6：`INTERPLANT_IN_TRANSIT` 的**供给身份硬闸**（冻结规则：该类型**不得**作为独立 Supply/PSA）。
    ///
    /// 背景：厂间在途**不是**一笔独立供给 —— 它只是 PI Position 的一种当前位置，
    /// 由 `ProductionInstructionPositionCalculator.CalculateTransitPositions` 承载
    /// （见 `CrossFactoryPeggingHandler` 类注释逐字：「跨厂在途（Interplant Transit）＝ PI Position
    ///  的一种当前位置，**不是独立 Supply**」）。
    ///
    /// **闸门设在我方消费侧**：装载器 `TimedSupplyFactLoader.LoadRawFactsAsync`
    /// （`LPS.APS.BusinessRules`，5号位 辖区）的白名单第五条**仍放行**该类型，
    /// 故本层在入池前拒绝，不依赖对方先改白名单。审核位 2026-10-16 J12-06 逐字：
    /// 「此类型按已冻结规则不得进入独立Supply/PSA，**移除/拦截它无需新业务裁决**」——
    /// 本判据即「拦截」那一半（「移除」那一半属 5号位 白名单）。
    ///
    /// ⚠ 大小写不敏感：冻结值为全大写常量，但不以大小写作为身份判据，避免拼写漂移绕过闸门。
    /// </summary>
    internal static bool IsForbiddenStandaloneSupplyType(string? supplyType)
        => string.Equals(supplyType, InterplantInTransitSupplyType, StringComparison.OrdinalIgnoreCase);

    /// <summary>冻结禁令类型名（`SupplyFact_Pipeline.SupplyType` 落库字面值）。</summary>
    internal const string InterplantInTransitSupplyType = "INTERPLANT_IN_TRANSIT";

    /// <summary>
    /// A1：解析落库用的 `Task.ProductionDepartmentId` **默认上下文**值（纯函数，可单测）。
    ///
    /// 契约（v1.1 §六.1.3 对 0号位 的承诺，**双轨**）：
    ///   · 命中 → 落该 `DefaultProductionDepartmentId`；
    ///   · 未命中 → **返回 `null`（留 NULL）** —— 由调用方**逐条记 Issue**，**不得静默**。
    ///
    /// 键 ＝ **`(MaterialId, StageCode)` 双键**，**不是** `MaterialId` 单键：
    ///   同一物料的不同 Stage 可对应不同部门（MSC 逐 `(MaterialId, StageCode)` 一行），
    ///   退化成单键 ⇒ 会**静默**把某一 Stage 的部门套到其它 Stage 上。
    /// ⚠ `StageCode` 为 `null`/空 ⇒ **必然未命中**（MSC 实测 `StageCode` 零空）⇒ 返回 `null`，不得拿空键去撞。
    /// </summary>
    /// <summary>
    /// J14-06：按**部门事实**把本轮 FinalTask 分区为「可落库」与「必须隔离」（纯函数，可单测）。
    ///
    /// 判据与落库点**同一函数** <see cref="ResolvePersistedDepartmentId"/>（`(MaterialId, StageCode)` 双键，
    /// `StageCode` 空必然未命中）—— 分区与落库不得各写一套口径，否则隔离集与落库集会出现缝隙。
    ///
    /// 语义：
    ///   · `Executable` ＝ 能解析到 `ProductionDepartmentId` 的 Draft（照常 INSERT）；
    ///   · `Missing`    ＝ 解析不到的 Draft（**不 INSERT**，由调用方聚合告警并计入未排量）。
    /// 顺序与入参一致（同一输入同一输出，可复算）；只读入参，不改任何状态。
    /// </summary>
    internal static (List<FinalTaskDraft> Executable, List<FinalTaskDraft> Missing)
        PartitionByDepartment(
            IReadOnlyList<FinalTaskDraft> finalTasks,
            IReadOnlyDictionary<(int MaterialId, string StageCode), int> deptContexts)
    {
        var executable = new List<FinalTaskDraft>(finalTasks.Count);
        var missing = new List<FinalTaskDraft>();

        foreach (var t in finalTasks)
        {
            if (ResolvePersistedDepartmentId(deptContexts, t.MaterialId, t.StageCode).HasValue)
                executable.Add(t);
            else
                missing.Add(t);
        }

        return (executable, missing);
    }

    /// <summary>
    /// J17-04（0号位 2026-10-17 第十七轮／第十八轮）：**资源身份是否非法**（纯函数，可单测）。
    ///
    /// 判据在冻结判别器之上（0号位 2026-09-22 裁决；1号位 `PhaseOneConstraintBuilder:256-264` 是唯一实现处）：
    /// <list type="bullet">
    ///   <item>`ResourceId` 非正数（含 0、负数、上游空串解析成的 0） ⇒ **非法**（既不是 `Resource` 主数据，也不是人工槽合成键）。</item>
    ///   <item>`ResourceId == null` 且该工序（按**完整身份** Material + Stage + Route + Path + OperationCode）属
    ///         `UNCONSTRAINED` / `WAIT_ONLY` 非资源工序 ⇒ **合法**（Task 可有时间区间、无资源）。</item>
    ///   <item>`ResourceId == null` 且工序为 `FINITE_RESOURCE`（或不在本域交给 Solver 的工序集里） ⇒ **非法**
    ///         （落 `Task.ResourceId=NULL` 等于宣称「已分配」却没有资源，与冻结有限资源约束不符）。</item>
    /// </list>
    ///
    /// **为什么不用 `(MaterialId, OperationCode)` 短键**（0号位 第十八轮 `KEY_SCOPE_RISK`）：冻结的工序业务身份
    /// 含 Route／Path／Stage，`OperationCode` 不具全局唯一性 ⇒ 同物料同工序码若在不同 Path/Stage 下规划模式不同，
    /// 短键会把非资源工序的放行资格错套到有限资源工序的 Task 上。改用完整身份后，两侧必须逐段相等。
    ///
    /// **身份不足怎么办**：Task 的 `RouteCode` 为空或 `PathId` 为 null ⇒ 无法证明其非资源身份 ⇒ 按**非法**处理
    /// （Fail Closed 方向；**不反向猜 `DEFAULT / 1`**）。实测 `[Task]` 210 行 `RouteCode`/`PathId`/`StageCode`
    /// 非空率均为 100%（2026-10-17 14:50），故当前无实例触发该分支。
    ///
    /// `nonResourceOperations` 取自**本域实际交给 Solver 的** `RoutingOperations`（已过 Stage 过滤）；
    /// 传 null 视为「一个非资源工序都没证明」 ⇒ 空资源一律按非法处理（保守方向，不会放行）。
    /// </summary>
    internal static bool IsResourceIdentityIllegal(
        FinalTaskDraft task,
        IReadOnlySet<(int MaterialId, string StageCode, string RouteCode, long PathId, string OperationCode)>? nonResourceOperations)
    {
        if (task.ResourceId is int rid)
            return rid <= 0;

        if (nonResourceOperations is null)
            return true;

        // 完整身份缺段 ⇒ 不能证明是合法非资源工序；Fail Closed，不猜 DEFAULT/1。
        if (task.PathId is not long pathId || string.IsNullOrEmpty(task.RouteCode))
            return true;

        return !nonResourceOperations.Contains(
            (task.MaterialId, task.StageCode, task.RouteCode, pathId, task.OperationCode));
    }

    /// <summary>
    /// 工序规划模式是否属「非资源工序」（`UNCONSTRAINED` / `WAIT_ONLY`，0号位 2026-09-22 裁决）。
    /// 纯函数，供装载侧构键集与单测共用，避免两处各写一份字符串比较而漂移。
    /// </summary>
    internal static bool IsNonResourcePlanningMode(string? operationPlanningMode)
        => string.Equals(operationPlanningMode, "UNCONSTRAINED", StringComparison.Ordinal)
        || string.Equals(operationPlanningMode, "WAIT_ONLY", StringComparison.Ordinal);

    /// <summary>
    /// J16-04（0号位 2026-10-17 第十六轮）：**前置 Task 被隔离 ⇒ 后序 Task 必须一并隔离**（纯函数，可单测）。
    ///
    /// 背景：落库时 Pegging 血缘只在两端 Task 都已 INSERT 时才写；若前置 Task 因部门事实缺失（或资源身份非法）
    /// 被隔离、后序 Task 合法，原实现**直接略去关联边并保留后序 Task** ⇒ 后序 Task 失去前驱约束却照常发布。
    ///
    /// 本函数把该情形从「静默丢边」改为「后序一并隔离」：后序 Task 缺了它必须消耗的上游供给，本就不能成立，
    /// 隔离后其数量由 Q/S/U 全集对账计入未排（J16-03），不会消失。
    ///
    /// 判据只用 1号位 回传的 `PhysicalPeggingDrafts`（`Upstream→Downstream` 的真实物理血缘），
    /// 不猜、不补边；反复扫描到不动点，保证多级链上的每一个后序都被处理。
    /// 输出顺序与入参一致（可复算）。
    /// </summary>
    internal static (List<FinalTaskDraft> Executable, List<FinalTaskDraft> Cascaded)
        CascadeExcludeDependents(
            IReadOnlyList<FinalTaskDraft> executable,
            IReadOnlyList<FinalTaskPeggingDraft> peggingDrafts,
            IReadOnlySet<string> excludedDraftIds)
    {
        var cascaded = new List<FinalTaskDraft>();
        if (executable.Count == 0 || peggingDrafts.Count == 0 || excludedDraftIds.Count == 0)
            return (executable.ToList(), cascaded);

        var upstreamByDownstream = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var edge in peggingDrafts)
        {
            if (string.IsNullOrEmpty(edge.UpstreamFinalDraftId) || string.IsNullOrEmpty(edge.DownstreamFinalDraftId))
                continue;
            if (!upstreamByDownstream.TryGetValue(edge.DownstreamFinalDraftId, out var ups))
                upstreamByDownstream[edge.DownstreamFinalDraftId] = ups = new List<string>(2);
            ups.Add(edge.UpstreamFinalDraftId);
        }

        var excluded = new HashSet<string>(excludedDraftIds, StringComparer.Ordinal);
        // 防御：调用方传入的 `executable` 里若仍带已隔离项，一律视为不落库（不重复计入 cascaded）
        var alive = executable.Where(t => !excluded.Contains(t.FinalDraftId)).ToList();
        var changed = true;
        while (changed)
        {
            changed = false;
            var next = new List<FinalTaskDraft>(alive.Count);
            foreach (var task in alive)
            {
                var blocked = upstreamByDownstream.TryGetValue(task.FinalDraftId, out var ups)
                              && ups.Exists(excluded.Contains);
                if (blocked)
                {
                    excluded.Add(task.FinalDraftId);
                    cascaded.Add(task);
                    changed = true;
                    continue;
                }
                next.Add(task);
            }
            alive = next;
        }

        // 确定性：级联结果按**入参顺序**输出，与扫描轮次无关
        if (cascaded.Count > 1)
        {
            var order = new Dictionary<string, int>(executable.Count, StringComparer.Ordinal);
            for (var i = 0; i < executable.Count; i++)
                order[executable[i].FinalDraftId] = i;
            cascaded.Sort((a, b) => order[a.FinalDraftId].CompareTo(order[b.FinalDraftId]));
        }

        return (alive, cascaded);
    }

    internal static int? ResolvePersistedDepartmentId(
        IReadOnlyDictionary<(int MaterialId, string StageCode), int> deptContexts,
        int materialId,
        string? stageCode)
    {
        if (string.IsNullOrWhiteSpace(stageCode))
            return null;
        return deptContexts.TryGetValue((materialId, stageCode), out var deptId) ? deptId : null;
    }

    /// <summary>
    /// 红线⑤⑥ SH 校验（纯函数，可单测）。
    ///
    /// ⑤ **SH 履行事实自洽（不跨 SH 串用）**：每条厂间出荷指示供给的分配，其出荷指示号必须落在分配上，
    ///    且与该供给条目**自身的 SH 身份**一致 —— 即不得把别的 SH 的 Transit/Received 事实拿来用。
    /// ⑥ **同一 SH 份额不重复计量**：同一 SH 的分配合计不得超过其指令总量（Order.Quantity，shipmentTotals）。
    ///
    /// 【口径来源】PM《pm回复826》§2 逐字：「SH场景下 Transit / Received 必须同SH匹配，不能串SH」
    ///   ＋「同一SH的Transit、Received、未生产份额不得重复计量」；《pm回复828》:95 同；
    ///   《APS_V1_2号位5号位接口裁决临时说明_v1.0_20260827》§3.2 逐字：「2号位处理当前SH时判断…DocumentNo
    ///   是否等于当前SH号…禁止跨SH串用」，§4.3 逐字：「当前SH → 同SH Transit → 同SH Received →
    ///   剩余未生产份额…Received不得跨SH串用」；《Position5_SH事实字段联调解答_20260828》§四 逐字：
    ///   「同一SH号下，Transit的 SourceDocumentNo 必须等于 Received 的 DocumentNo，不得跨SH串用」。
    ///   ⇒ ⑤ 的**比较键是「SH号」**，比的是**同一张 SH 的履行事实有没有被别的 SH 串用**。
    ///
    /// 【⚠ 不得按 DemandKey 分组】冻结正文逐字：「第一层普通Demand**可由一张或多张SH承接**，
    ///   普通Demand↔SH**允许多对多**」（Pegging v1.6 §6.3 / §12.1；业务基线 v1.13 §40.1）——
    ///   **一个需求跨多张 SH 是正例，不是违规**；「一张SH只形成一条完整源厂Demand」（§12.3）约束的是
    ///   SH→源厂需求方向，不是需求→SH 方向。
    /// </summary>
    internal static List<string> ValidateShConsistency(
        IReadOnlyList<SupplyAllocationItem> allocations,
        IReadOnlyDictionary<string, decimal> shipmentTotals)
    {
        var errors = new List<string>();
        var shAllocations = allocations
            .Where(a => a.SourceType == SupplySourceType.INTER_FACTORY_ORDER)
            .ToList();
        if (shAllocations.Count == 0) return errors;

        // 5. 不跨 SH 串用：分配携带的 SH 身份必须落在分配上，且与该供给条目自身的 SH 身份一致。
        foreach (var a in shAllocations)
        {
            if (string.IsNullOrWhiteSpace(a.ShippingInstructionNo))
            {
                errors.Add($"SH 身份缺失: 分配 {a.AllocationSequence}（需求 {a.DemandKey}）为厂间出荷指示供给，但未落出荷指示号");
                continue;
            }
            if (!string.IsNullOrWhiteSpace(a.SourceReference)
                && !string.Equals(a.ShippingInstructionNo, a.SourceReference, StringComparison.Ordinal))
            {
                errors.Add($"SH 跨SH串用: 分配 {a.AllocationSequence} 的出荷指示号 {a.ShippingInstructionNo} " +
                           $"与其供给身份 {a.SourceReference} 不一致");
            }
        }

        // 6. 份额不重复计量
        foreach (var group in shAllocations
                     .Where(a => !string.IsNullOrWhiteSpace(a.ShippingInstructionNo))
                     .GroupBy(a => a.ShippingInstructionNo!, StringComparer.Ordinal))
        {
            var consumed = group.Sum(a => a.AllocatedQuantity);
            var total = shipmentTotals.TryGetValue(group.Key, out var t) ? t : 0m;
            if (consumed > total)
                errors.Add($"SH 份额重复计量: {group.Key} 已分配 {consumed} > 指令总量 {total}");
        }

        return errors;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 私有辅助方法
    // ─────────────────────────────────────────────────────────────────────────

    internal sealed record BomEdge(
        string ParentCode,
        string ChildCode,
        int ChildMaterialId,
        decimal Qty,
        int Level,
        bool IsLeaf,
        bool IsPurchased,
        string? ChildRequiredStageCode,
        /// <summary>
        /// 该边所属的 BOMNO（= `OrderBomRequestLink.ResolvedBOMNO`）。
        ///
        /// 【为什么要带上它】同一父件可能在**多个 BOMNO** 下各有一条子件边，且配比不同。
        /// 展开时若不分 BOMNO，`ToBomStructure` 会把它们按子件求和 ⇒ 该父件的单位用量被放大
        /// （多份 BOM 的配比叠在一起）。而一笔订单只走**一个** ResolvedBOMNO（`OrderBomRequestLink`），
        /// 故展开必须按订单自己的 BOMNO 取边。空值＝该行无 BOMNO（退化口径：按全部边展开）。
        /// </summary>
        string? BomNo);

    private sealed record BomSnapshot(
        ILookup<string, BomEdge> ByParent,
        IReadOnlyDictionary<string, int> LLCByMaterial,
        IReadOnlyDictionary<string, bool> IsPurchasedByMaterial,
        int EdgeCount,
        /// <summary>
        /// 本域「BOM 可达物料宇宙」= 本次快照里全部 <c>ParentMaterialCode ∪ ChildMaterialCode</c>。
        ///
        /// 用途：<c>LoadPiRemainingFactsAsync</c> 不再只从 <c>[Order]</c> 取 PI 事实 —— <c>[Order]</c> 的装载带
        /// 产品族闸门（<c>m.ProductFamilyId = @ProductFamilyId</c>），而物料归族规则只覆盖**成品**工艺码，
        /// 下阶加工件一律无族 ⇒ 它们的 MTS 生产指示全被挡在 <c>[Order]</c> 之外，PI 供给池在下阶永远是空的
        /// （实测 PV2：<c>[Order]</c> 22,144 行 / 9,857 物料 **100% 是 FINAL_FG-* 且 0 个是 BOM 子件**）。
        /// PI 事实是**供给事实**，不该被「顶层需求归域」这件事限死，故按本域物料宇宙从 <c>Order_Canonical</c> 补取。
        /// </summary>
        IReadOnlySet<string> MaterialUniverse,
        /// <summary>
        /// `BOMNO → 子件码 → 父件码集合`（J14-02：EDGE 侧按**本请求的父边**选行）。
        /// 在**精确去重后**的行上构建（去重键含 BOMNO，不跨 BOM 折叠）；用于 Stage 链选行，
        /// **不参与** BFS 展开 —— 展开的 BOMNO 分桶见 `ToBomStructures`。
        /// </summary>
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyCollection<string>>> ParentsByBomChild,
        /// <summary>
        /// `BOMNO → 子件码 → 供给阈值 StageCode 集合`（J14-03：阈值只作 EDGE 链的**截断点**，
        /// 不再用于判定 ROOT/EDGE 角色；同键多值＝源数据冲突，调用方留 null 并告警）。
        /// </summary>
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyCollection<string>>> ThresholdsByBomChild)
    {
        /// <summary>
        /// 本次快照**实际装载**的 BOM 批次号（= `OrderBomRequestLink` 中本 PlanVersion 最新一条的 `BatchNo`）；
        /// <c>null</c> ⇒ 本 PlanVersion 无任何可证关联批，见 <see cref="InputState"/>。
        ///
        /// 0号位 2026-10-17 §三.D：批号只能来自**可证关联**，不得用 `TOP 1 APS_BOM_RAW ORDER BY SyncedAt DESC` 推断。
        /// </summary>
        public string? BatchNo { get; init; }

        /// <summary>
        /// 本快照的输入有效性。下游据此区分「业务合法空集」与「BOM 输入缺失/技术不可用」——
        /// 后者不得被当成「本次不需要 Stage/无需求」静默放行（0号位 2026-10-17 §三.D）。
        /// </summary>
        public BomInputState InputState { get; init; } = BomInputState.Available;
    }

    /// <summary>
    /// BOM 快照的输入有效性三态（0号位 2026-10-17 §三.D）。
    ///
    /// 与「装载成功但行数为 0」的区别：<c>Available</c> 下 `EdgeCount == 0` 只表示该批无父子边；
    /// 而 <c>NoLinkedBatch</c> / <c>LinkedBatchNoRows</c> 表示**没有可信的 BOM 输入**，
    /// 任何「按无 BOM 继续跑」的路径都必须留痕并按缺事实处理。
    /// </summary>
    private enum BomInputState
    {
        /// <summary>关联批已解析且装载到行。</summary>
        Available = 0,

        /// <summary>本 PlanVersion 无 <c>OrderBomRequestLink</c> ⇒ 无法证明「本次该用哪一批」（旧批兜底已取消）。</summary>
        NoLinkedBatch = 1,

        /// <summary>关联批已解析，但 <c>APS_BOM_RAW</c> 中该批无任何行 ⇒ 夜间接货未完成/失败。</summary>
        LinkedBatchNoRows = 2,
    }

    private sealed class BomRawRow
    {
        public string? BomNo                  { get; set; }
        public string ParentMaterialCode      { get; set; } = string.Empty;
        public string ChildMaterialCode       { get; set; } = string.Empty;
        public int ChildMaterialId            { get; set; }
        public decimal Quantity               { get; set; }
        public int Level                      { get; set; }
        public int? LLC                       { get; set; }
        public bool IsLeaf                    { get; set; }
        public bool IsPurchased               { get; set; }
        public string? ChildRequiredStageCode { get; set; }
    }

    /// <summary>
    /// <c>MaterialSupplyContext</c> 的 (MaterialCode, SupplyMode, SourceSystem) 装载行。
    /// 用途见 <c>LoadBomSnapshotAsync</c> 的「采购/自制判定源修正」：`Material.IsPurchased` 是死列，
    /// `SupplyMode='PURCHASE'` 才是生产上唯一有值的采购判据；`SourceSystem` 用于实现
    /// **ERP 优先、他源兜底**（采购/自制的权威判据在 ERP）。
    /// </summary>
    private sealed class MaterialSupplyModeRow
    {
        public string MaterialCode { get; set; } = string.Empty;
        public string SupplyMode   { get; set; } = string.Empty;
        public string SourceSystem { get; set; } = string.Empty;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 供给池内部数据结构
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 计划良率三态**只读**诊断（3号位 2026-10-25《缺配三态词表说明 v1.0》§三／§四）。
    ///
    /// 三态（词表以 3号位 该件为准，本类只计数、不判定、不改行为）：
    ///   · **缺配** —— 该物料没有可用的 Material 级规则，或 `PlanningYields` 整块未配置。
    ///                对应原出口 A（未配置）＋ 出口 C（无匹配）。
    ///   · **越界** —— 有 Material 级规则命中，但 `YieldPercent ∉ (0,100]` 被跳过（原出口 B，
    ///                原实现落回出口 C、同返回 100 而不可区分）。
    ///   · **重复** —— 同 `(MaterialId, StageCode)` 出现多条规则；发布校验
    ///                `DUPLICATE_PLANNING_YIELD`（3号位 2026-10-25 已落）拦新增，此处计存量/再发布前残留。
    ///
    /// ⚠ **只读**：不参与判定、不中断 Run、不改 `return 100m` 无损兜底语义（3号位 §四.2）。
    /// ⚠ **口径**：按**物料去重**计数（同一物料被多次查询只计一次），与「调用次数」不是同一把尺。
    /// </summary>
    internal sealed class PlanningYieldDiagnostics
    {
        private readonly HashSet<string> _queried    = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _missing    = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _outOfRange = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>本次查询过的物料数（去重）。</summary>
        public int QueriedCount => _queried.Count;

        /// <summary>缺配物料数（去重）。</summary>
        public int MissingCount => _missing.Count;

        /// <summary>越界物料数（去重）。</summary>
        public int OutOfRangeCount => _outOfRange.Count;

        /// <summary>缺配样本（最多 10 个物料码，便于人工抽查；非全量清单）。</summary>
        public IReadOnlyList<string> MissingSamples => _missing.Take(10).ToList();

        /// <summary>越界样本（最多 10 个物料码；非全量清单）。</summary>
        public IReadOnlyList<string> OutOfRangeSamples => _outOfRange.Take(10).ToList();

        internal void Query(string? materialCode)
        {
            if (!string.IsNullOrWhiteSpace(materialCode)) _queried.Add(materialCode);
        }

        internal void Miss(string? materialCode)
        {
            if (!string.IsNullOrWhiteSpace(materialCode)) _missing.Add(materialCode);
        }

        internal void OutOfRange(string? materialCode)
        {
            if (!string.IsNullOrWhiteSpace(materialCode)) _outOfRange.Add(materialCode);
        }
    }

    /// <summary>
    /// **Demand 级完成时间事实**的只读诊断 ＋ 消费闸门（0号位 2026-10-25《四份回执联合核证》§二-5／§五 P0-B）。
    ///
    /// 【背景】<see cref="DomainSolveResult.DemandCompletions"/> 是 1号位 出口的 Demand 级时间事实
    ///   （含无 Routing Stage 的纯 LeadTime 段时间；**不得**据其建 Task，见 Core 类注释）。
    ///   该字段族在 Application／Engine／Web **长期零消费**，0号位 点名「出入口断链」。
    ///   本类补的正是 2号位 这一半：**记账 ＋ 消费闸门**，不改 1号位 任何取值语义。
    ///
    /// 【闸门】`CompletionTimeCertain == false` 的完成时间**不得**当承诺、**不得**作供给可用时间、
    ///   **不得**据其推下游 `AvailableTime`（Core 契约 `CompletionTimeCertain` 注释「2号位 读侧义务」）。
    ///   `null` = 未标注 ⇒ 按现状放行（向后兼容，旧载荷一律落此支）；`true` = 可信。
    ///   ⇒ **唯一排除项是 `false`**，由 <see cref="CertainOnly"/> 承担。
    ///
    /// 【当前实效】生产侧尚未置值（恒 `null`）⇒ 闸门**当前不排除任何行**，
    ///   计数会如实显示 `CertainNullCount == FactCount`。这是「契约骨架已落、置值未落」的状态，不是故障。
    ///
    /// ⚠ **只读**：不参与判定、不中断 Run、不建 Task／TaskNo／MES工单（0号位 §9.2／§9.3）。
    /// </summary>
    internal static class DemandCompletionDiagnostics
    {
        /// <summary>Stage 段时间未命中任何 LT 层级时的 `MatchLevel` 取值（V1 合法四值之一）。</summary>
        internal const string MissingStageLeadTimeLevel = "STAGE_LEADTIME_MISSING";

        internal static DemandCompletionSummary Summarize(IReadOnlyList<DemandCompletionFact>? facts)
        {
            if (facts == null || facts.Count == 0) return DemandCompletionSummary.Empty;

            var certainTrue = 0;
            var certainFalse = 0;
            var certainNull = 0;
            var emptyKey = 0;
            var segmentCount = 0;
            var missingLeadTimeSegments = 0;
            var uncertainSamples = new List<string>();
            var unannotatedSamples = new List<string>();

            foreach (var fact in facts)
            {
                if (string.IsNullOrWhiteSpace(fact.LogicalDemandKey)) emptyKey++;

                if (fact.CompletionTimeCertain == true)
                {
                    certainTrue++;
                }
                else if (fact.CompletionTimeCertain == false)
                {
                    certainFalse++;
                    if (uncertainSamples.Count < 10 && !string.IsNullOrWhiteSpace(fact.LogicalDemandKey))
                        uncertainSamples.Add(fact.LogicalDemandKey);
                }
                else
                {
                    certainNull++;
                    if (unannotatedSamples.Count < 10 && !string.IsNullOrWhiteSpace(fact.LogicalDemandKey))
                        unannotatedSamples.Add(fact.LogicalDemandKey);
                }

                var segments = fact.StageTimings;
                if (segments == null) continue;
                segmentCount += segments.Count;
                foreach (var segment in segments)
                {
                    if (string.Equals(segment.MatchLevel, MissingStageLeadTimeLevel, StringComparison.OrdinalIgnoreCase))
                        missingLeadTimeSegments++;
                }
            }

            return new DemandCompletionSummary(
                FactCount: facts.Count,
                CertainTrueCount: certainTrue,
                CertainFalseCount: certainFalse,
                CertainNullCount: certainNull,
                EmptyDemandKeyCount: emptyKey,
                StageTimingSegmentCount: segmentCount,
                MissingStageLeadTimeSegmentCount: missingLeadTimeSegments,
                UncertainSamples: uncertainSamples,
                UnannotatedSamples: unannotatedSamples);
        }

        /// <summary>
        /// 消费闸门：只放行**非** `false` 的完成时间事实。
        /// `null`（未标注，向后兼容）与 `true`（可信）放行；`false`（缺 LT／缺合法 WORK 窗口）排除。
        /// 任何「把完成时间当承诺或供给可用时间」的消费点都必须经本方法过滤。
        /// </summary>
        internal static IReadOnlyList<DemandCompletionFact> CertainOnly(
            IReadOnlyList<DemandCompletionFact>? facts)
            => facts == null
                ? Array.Empty<DemandCompletionFact>()
                : facts.Where(f => f.CompletionTimeCertain != false).ToList();
    }

    /// <summary>
    /// <see cref="DemandCompletionDiagnostics.Summarize"/> 的只读汇总结果。
    /// 样本各最多 10 条 = **抽样非全量清单**。
    /// </summary>
    internal sealed record DemandCompletionSummary(
        int FactCount,
        int CertainTrueCount,
        int CertainFalseCount,
        int CertainNullCount,
        int EmptyDemandKeyCount,
        int StageTimingSegmentCount,
        int MissingStageLeadTimeSegmentCount,
        IReadOnlyList<string> UncertainSamples,
        IReadOnlyList<string> UnannotatedSamples)
    {
        internal static readonly DemandCompletionSummary Empty = new(
            0, 0, 0, 0, 0, 0, 0, Array.Empty<string>(), Array.Empty<string>());

        /// <summary>是否存在被闸门排除（`CompletionTimeCertain == false`）的完成时间事实。</summary>
        internal bool AnyUncertain => CertainFalseCount > 0;
    }

    /// <summary>
    /// 计划良率三态判定的**唯一实现**（`SupplyPool.PlanningYieldPercent` 与单测共用同一函数）。
    ///
    /// 出口语义（与改造前逐字一致）：
    ///   · `Configured`  —— 命中合法 Material 级规则 ⇒ 返回其 `YieldPercent`；
    ///   · `OutOfRange`  —— 命中 Material 级规则但 `YieldPercent ∉ (0,100]` ⇒ 返回 100（原出口 B）；
    ///   · `Missing`     —— 整块未配置（原出口 A）或无任一规则匹配（原出口 C）⇒ 返回 100。
    /// `StageCode` 非空的规则一律跳过（本方法只认 Material 级默认，既有行为不变）。
    /// </summary>
    internal static (PlanningYieldState State, decimal Percent) ClassifyPlanningYield(
        string? materialCode,
        IReadOnlyList<PlanningYieldRule>? yields)
    {
        if (yields is not { Count: > 0 }) return (PlanningYieldState.Missing, 100m);

        var hitButOutOfRange = false;
        foreach (var rule in yields)
        {
            if (!string.Equals(rule.MaterialId, materialCode, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(rule.StageCode)) continue;
            if (rule.YieldPercent <= 0m || rule.YieldPercent > 100m) { hitButOutOfRange = true; continue; }
            return (PlanningYieldState.Configured, rule.YieldPercent);
        }

        return hitButOutOfRange
            ? (PlanningYieldState.OutOfRange, 100m)
            : (PlanningYieldState.Missing, 100m);
    }

    /// <summary>计划良率三态（词表词形：缺配 / 越界 / 重复；本枚举对应前两态，重复态见键计数）。</summary>
    internal enum PlanningYieldState
    {
        /// <summary>命中合法规则，返回实际值（非三态之一，仅作判定出口）。</summary>
        Configured = 0,
        /// <summary>缺配：无可用 Material 级规则，或整块未配置。</summary>
        Missing = 1,
        /// <summary>越界：命中但 `YieldPercent ∉ (0,100]`。</summary>
        OutOfRange = 2,
    }

    /// <summary>
    /// 「重复」态：同 `(MaterialId, StageCode)` 出现 &gt;1 条规则的**键个数**。
    ///
    /// ⚠ 口径＝**整份快照 `PlanningYields`** 范围（不做物料收窄）——词表定义本身不带物料范围。
    /// 大小写按 `ToUpperInvariant` 归一，与发布校验 `DUPLICATE_PLANNING_YIELD` 同把尺。
    /// </summary>
    internal static int CountPlanningYieldDuplicateKeys(IReadOnlyList<PlanningYieldRule>? yields)
    {
        if (yields is not { Count: > 0 }) return 0;

        var keyCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var rule in yields)
        {
            // `\u0001` 作分隔符：物料码/StageCode 均为业务文本，不含控制字符
            var key = (rule.MaterialId ?? string.Empty).ToUpperInvariant()
                      + '\u0001'
                      + (rule.StageCode ?? string.Empty).ToUpperInvariant();
            keyCounts.TryGetValue(key, out var seen);
            keyCounts[key] = seen + 1;
        }

        var duplicates = 0;
        foreach (var kv in keyCounts)
            if (kv.Value > 1) duplicates++;
        return duplicates;
    }

    /// <summary>
    /// 内存供给账本。PeggingLoop 遍历时直接在此对象上扣减，不回写数据库。
    /// 最终扣减结果通过 SupplyAllocationItem 列表落库到 PeggingSupplyAllocation。
    /// </summary>
    private sealed class SupplyPool
    {
        // Key: "MaterialCode|FactoryId"
        private readonly Dictionary<string, List<SupplyLedgerEntry>> _ledger
            = new(StringComparer.Ordinal);

        /// <summary>冻结策略快照（Inventory/PI/Procurement 三类排序参数）；缺省时按稳定兜底排序</summary>
        private readonly FrozenStrategySnapshot? _snapshot;

        /// <summary>
        /// Run 级跨 Domain 物理消耗账本（R03-02）：Key = PhysicalSourceKey，Value = 前序成功 Domain 已消耗量。
        /// null = 单域运行（不跨域守恒），行为与改造前一致。
        /// </summary>
        private readonly IDictionary<string, decimal>? _crossDomainConsumed;

        /// <summary>
        /// Candidate 侧跨 Run 聚合占用账本（R03-02「甲」口径）：Key = <see cref="BuildAggregateConsumptionKey"/>，
        /// Value = 其它 ACTIVE 域已占用的库存总量。null = 非 Candidate 或本域无外部占用，行为与改造前一致。
        /// </summary>
        private readonly IDictionary<string, decimal>? _crossDomainAggregateConsumed;

        /// <summary>R03-02（Candidate）：被聚合扣减命中的供给条目数（扣减量 &gt; 0 才计）。</summary>
        public int CrossDomainAggregateDeductedEntryCount { get; private set; }

        /// <summary>R03-02（Candidate）：聚合扣减累计量（Σ 各条目被扣走的部分）。</summary>
        public decimal CrossDomainAggregateDeductedQty { get; private set; }

        /// <summary>
        /// R03-02（Candidate）：外部占用**超出**本域同键可用量、扣不完的余量。
        /// &gt; 0 说明别的域账上占了、本域供给池里却没有这么多 ⇒ 供给快照与分配不是同一时点，
        /// 属须留痕的异常（此时本域同键已被扣到 0，不会超发，但差额无从归属）。
        /// </summary>
        public decimal CrossDomainAggregateUnappliedQty { get; private set; }

        public int TotalEntries { get; private set; }

        /// <summary>本次运行供给快照的数据截止时点（= ScheduleRun.DataCutoffTime；编排层 Set，缺省回退 Now）。</summary>
        public DateTime DataCutoffTime { get; set; }

        /// <summary>冻结 DefaultPurchaseLt（天）→ 规划采购占位 AvailableTime = DataCutoffTime + 该天数（P0-07）。</summary>
        public int DefaultPurchaseLtDays => _snapshot == null ? 0 : BuildFrozenFactParameters(_snapshot).DefaultPurchaseLt;

        /// <summary>
        /// Planning Yield（Material 级默认，清单 27）→ PlannedProcessQty 反算用（P1-02）。
        /// YieldPercent 语义为百分数（0 &lt; YieldPercent &lt;= 100，见 PlanningYieldRule 注释 + 3号位校验）。
        /// 返回 100 = 无损兜底（未配置快照 / 无匹配规则 / 越界），调用侧据此不放大。
        /// 当前仅匹配空 StageCode 的 Material 级默认；StageCode 级精确规则待 startStageCode 接入工艺路由后补。
        ///
        /// 【2026-10-25 只读诊断】返回值与判定**一字未改**；仅在原三个出口上按 3号位《缺配三态词表说明
        /// v1.0》§三补**计数留痕**（见 <see cref="PlanningYieldDiagnostics"/>），供 `EvidenceJson` 只读对账。
        /// </summary>
        public decimal PlanningYieldPercent(string materialCode)
        {
            // 判定本身走 `ClassifyPlanningYield`（与单测同一函数，见其注释）；本方法只多一句计数留痕。
            _yieldDiag.Query(materialCode);

            var (state, percent) = ClassifyPlanningYield(materialCode, _snapshot?.Procurement.PlanningYields);

            switch (state)
            {
                case PlanningYieldState.OutOfRange: _yieldDiag.OutOfRange(materialCode); break; // 出口 B
                case PlanningYieldState.Missing:    _yieldDiag.Miss(materialCode);       break; // 出口 A / C
            }

            return percent;
        }

        private readonly PlanningYieldDiagnostics _yieldDiag = new();

        /// <summary>计划良率三态计数句柄（只读；由落库侧原样取走写 `EvidenceJson`）。</summary>
        public PlanningYieldDiagnostics YieldDiagnostics => _yieldDiag;

        private int? _yieldDuplicateKeyCount;

        /// <summary>「重复」态的键数（首算后缓存：快照在一轮内不可变）。实现见 <see cref="CountPlanningYieldDuplicateKeys"/>。</summary>
        public int PlanningYieldDuplicateKeyCount()
        {
            _yieldDuplicateKeyCount ??= CountPlanningYieldDuplicateKeys(_snapshot?.Procurement.PlanningYields);
            return _yieldDuplicateKeyCount.Value;
        }

        public SupplyPool(FrozenStrategySnapshot? snapshot = null,
                          IDictionary<string, decimal>? crossDomainConsumed = null,
                          IDictionary<string, decimal>? crossDomainAggregateConsumed = null)
        {
            _snapshot                     = snapshot;
            _crossDomainConsumed          = crossDomainConsumed;
            _crossDomainAggregateConsumed = crossDomainAggregateConsumed;
        }

        /// <summary>
        /// R03-02：把本 Domain 的**成功**分配量计入 Run 级跨域账本，供后续 Domain 入池时扣减。
        /// 无 PhysicalSourceKey（虚拟供给：NEW_REQUIREMENT / PLANNING_PURCHASE_PLACEHOLDER / 上游域产物）时不计。
        /// 未传入账本（单域运行）时为空操作。
        /// </summary>
        public void RecordPhysicalConsumption(string? physicalSourceKey, decimal qty)
        {
            if (_crossDomainConsumed is null || string.IsNullOrWhiteSpace(physicalSourceKey) || qty <= 0m)
                return;
            _crossDomainConsumed.TryGetValue(physicalSourceKey, out var current);
            _crossDomainConsumed[physicalSourceKey] = current + qty;
        }

        /// <summary>
        /// R03-02（Candidate 侧，「甲」口径）：把「其它 Domain 当前 ACTIVE 计划版本已占用的库存总量」
        /// 按聚合键扣到本域供给池，使候选**看不见**已被别的域吃掉的量。
        ///
        /// 调用时机：**库存条目全部 Add 完之后、其它供给类型 Add 之前**。此刻每个「物料+厂」列表里
        /// 只有库存条目，按 <see cref="BuildAggregateConsumptionKey"/> 取物料码/工厂码不会有歧义。
        ///
        /// 扣减顺序 = <see cref="SupplyLedgerEntry.SupplySourceId"/> 升序（再以物理键字典序兜底）。
        /// 这样「扣哪一批」只取决于数据本身，不取决于 SQL 返回顺序 —— 同一批输入每次结果一致。
        /// 每批扣多少由 <see cref="AllocateAggregateDeduction"/> 一次分摊，**不是每批都扣一遍总量**。
        /// </summary>
        public void ApplyAggregateCrossDomainConsumption()
        {
            if (_crossDomainAggregateConsumed is null || _crossDomainAggregateConsumed.Count == 0)
                return;

            foreach (var (ledgerKey, list) in _ledger)
            {
                if (list.Count == 0) continue;

                // ledgerKey = (物料|工厂Id)；物料码本身不挂在条目上，从键里取。
                var sep = ledgerKey.LastIndexOf('|');
                if (sep <= 0) continue;
                var materialCode = ledgerKey[..sep];

                var inventory = list
                    .Where(e => e.SourceType == Core.Enum.SupplySourceType.INVENTORY && e.RemainingQty > 0m)
                    .OrderBy(e => e.SupplySourceId ?? long.MaxValue)
                    .ThenBy(e => e.PhysicalSourceKey ?? string.Empty, StringComparer.Ordinal)
                    .ToList();
                if (inventory.Count == 0) continue;

                // 同一「物料+厂」列表内工厂码唯一 ⇒ 取首条即可。
                var aggregateKey = BuildAggregateConsumptionKey(
                    materialCode, inventory[0].FactoryCode, Core.Enum.SupplySourceType.INVENTORY);

                if (!_crossDomainAggregateConsumed.TryGetValue(aggregateKey, out var occupied) || occupied <= 0m)
                    continue;

                var available = inventory.Select(e => e.RemainingQty).ToList();
                var deductions = AllocateAggregateDeduction(occupied, available);

                for (var i = 0; i < inventory.Count; i++)
                {
                    if (deductions[i] <= 0m) continue;
                    inventory[i].RemainingQty -= deductions[i];
                    CrossDomainAggregateDeductedQty += deductions[i];
                    CrossDomainAggregateDeductedEntryCount++;
                }

                var applied = deductions.Sum();
                if (applied < occupied)
                    CrossDomainAggregateUnappliedQty += occupied - applied;
            }
        }

        public SupplyLedgerEntry Add(
            string materialCode, int materialId, int factoryId, decimal qty,
            DateTime? availableAt, Core.Enum.SupplySourceType sourceType,
            string? sourceRef, string factoryCode, long? supplySourceId = null,
            SupplyConfidence confidence = SupplyConfidence.CONFIRMED,
            SupplyCommitment commitment = SupplyCommitment.COMMITTED,
            SupplySortFacts? sort = null,
            string? physicalSourceKey = null)
        {
            var key = BuildKey(materialCode, factoryId);
            if (!_ledger.TryGetValue(key, out var list))
            {
                list = new List<SupplyLedgerEntry>();
                _ledger[key] = list;
            }
            var entry = new SupplyLedgerEntry
            {
                OriginalQty     = qty,
                RemainingQty    = qty,
                MaterialId      = materialId,
                AvailableAt     = availableAt,
                SourceType      = sourceType,
                SourceReference = sourceRef,
                PhysicalSourceKey = physicalSourceKey,
                FactoryCode     = factoryCode,
                FactoryId       = factoryId,
                SupplySourceId  = supplySourceId,
                Confidence      = confidence,
                Commitment      = commitment,
                WarehouseCode   = sort?.WarehouseCode,
                ReleaseDate     = sort?.ReleaseDate,
                PoNo            = sort?.PoNo,
                LineNo          = sort?.LineNo,
                PiNo            = sort?.PiNo,
                IssueDate       = sort?.IssueDate,
                CreatedAt       = sort?.CreatedAt
            };
            // R03-02 跨域守恒：同一物理供给被前序 Domain 合法消耗后，本 Domain 只能看到剩余量。
            // 不足则可用量归 0（条目保留，落库口径与排序不受影响，只是接不到量）。
            if (physicalSourceKey is not null && _crossDomainConsumed is not null
                && _crossDomainConsumed.TryGetValue(physicalSourceKey, out var upstreamConsumed)
                && upstreamConsumed > 0m)
            {
                var available = qty - upstreamConsumed;
                entry.RemainingQty = available > 0m ? available : 0m;
            }

            list.Add(entry);
            TotalEntries++;
            return entry;
        }

        /// <summary>
        /// 返回指定物料+工厂在当前 Demand 业务身份下允许进入的供给条目（PM 2026-08-28 最终裁决：
        /// 不存在 Inventory/PI/Procurement 三类全局优先级；先定允许供给集合，再类内各自排序）：
        ///   - 允许库存时（includeInventory，即 BOM 下阶）：Inventory 按 Warehouse Priority（SupplyBlock.Inventory）
        ///   - 自制件（isPurchased=false）：PI 按 Issue/Create Time ASC → Stable PiNo（SupplyBlock.PiSort）
        ///   - 采购件（isPurchased=true）：Procurement 按 Warehouse Priority → AvailableTime → ReleaseDate → PO+Line
        ///                               （固定链，不可重排；参数取 ProcurementBlock）
        /// 跨厂 Transit/Received（绑定消费）与缺口（Placeholder/Planned）不进入本排序。
        /// 未配置/字段缺失时自动降级为稳定兜底（SourceReference），不引入随机顺序。
        /// </summary>
        public IReadOnlyList<SupplyLedgerEntry> GetEntries(string materialCode, int factoryId, bool isPurchased, bool includeInventory, bool includePi = true)
        {
            var key = BuildKey(materialCode, factoryId);
            if (!_ledger.TryGetValue(key, out var list)) return Array.Empty<SupplyLedgerEntry>();

            var ordered = new List<SupplyLedgerEntry>(list.Count);
            if (includeInventory)
                ordered.AddRange(SortInventory(list));
            if (isPurchased)
                ordered.AddRange(SortProcurement(list));
            else
            {
                // T2-02：PI 只在 BOM 下阶参与承接（includePi = bomLevel > 0）。
                // 顶层（L0）需求本身的生产是**组装工序**，它找不到 PI —— 部品出口的出荷指示没有对应的组装指示，
                // 出荷指示↔组装指示的挂钩由生管做，APS 不得自造（用户 2026-10-09 口径）。
                // 顶层生产需求的 PI 身份 = 该行挂着的 PI（生管挂好的），没有就用订单号（出荷指示号）。
                if (includePi)
                    ordered.AddRange(SortPi(list));
                // 跨 Domain：上游域生产输出作为分段虚拟供给（§8/D12），按可用时间升序
                ordered.AddRange(SortUpstreamDomainProduction(list));
            }
            return ordered;
        }

        /// <summary>
        /// INTER_FACTORY_ORDER（厂间出荷指示，SH级）供给：按 AvailableAt 升序。
        /// 不进入 GetEntries 三类通用排序（PM 裁决：跨厂 SH 绑定消费，走独立消费路径）。
        /// 每个 SH 是单一供给身份（PhysicalSourceKey=SH No），量=Order.Quantity（指令总量，整单一次入库）。
        /// </summary>
        public IReadOnlyList<SupplyLedgerEntry> GetInterFactoryEntries(string materialCode, int factoryId)
        {
            var key = BuildKey(materialCode, factoryId);
            if (!_ledger.TryGetValue(key, out var list)) return Array.Empty<SupplyLedgerEntry>();

            return list
                .Where(e => e.SourceType == Core.Enum.SupplySourceType.INTER_FACTORY_ORDER)
                .OrderBy(e => e.AvailableAt ?? DateTime.MaxValue)
                .ThenBy(e => e.SourceReference ?? string.Empty, StringComparer.Ordinal)
                .ToList();
        }

        private IReadOnlyList<SupplyLedgerEntry> SortUpstreamDomainProduction(IReadOnlyList<SupplyLedgerEntry> list)
            => list
                .Where(e => e.SourceType == Core.Enum.SupplySourceType.UPSTREAM_DOMAIN_PRODUCTION)
                .OrderBy(e => e.AvailableAt ?? DateTime.MaxValue)
                .ThenBy(e => e.SourceReference ?? string.Empty, StringComparer.Ordinal)
                .ToList();

        private IReadOnlyList<SupplyLedgerEntry> SortInventory(IReadOnlyList<SupplyLedgerEntry> list)
        {
            var rankMap = BuildRankMap(_snapshot?.Supply.Inventory.WarehousePriority);
            return list
                .Where(e => e.SourceType == Core.Enum.SupplySourceType.INVENTORY)
                .OrderBy(e => WarehouseRank(e.WarehouseCode, rankMap))
                .ThenBy(e => e.SourceReference ?? string.Empty, StringComparer.Ordinal)
                .ToList();
        }

        private IReadOnlyList<SupplyLedgerEntry> SortPi(IReadOnlyList<SupplyLedgerEntry> list)
        {
            var sortBy = _snapshot?.Supply.PiSort?.SortBy ?? PiSortBy.IssueDateAsc;
            var tieBreak = _snapshot?.Supply.PiSort?.UseStablePiNoTieBreak ?? false;

            var eligible = list.Where(e => e.SourceType == Core.Enum.SupplySourceType.WIP
                                        || e.SourceType == Core.Enum.SupplySourceType.PRODUCTION_INSTRUCTION);

            if (sortBy == PiSortBy.StablePiNoAsc)
                return eligible
                    .OrderBy(e => e.PiNo ?? string.Empty, StringComparer.Ordinal)
                    .ThenBy(e => e.SourceReference ?? string.Empty, StringComparer.Ordinal)
                    .ToList();

            var ordered = eligible.OrderBy(e => PiSortTime(e, sortBy));
            if (tieBreak)
                ordered = ordered.ThenBy(e => e.PiNo ?? string.Empty, StringComparer.Ordinal);
            return ordered.ThenBy(e => e.SourceReference ?? string.Empty, StringComparer.Ordinal).ToList();
        }

        private IReadOnlyList<SupplyLedgerEntry> SortProcurement(IReadOnlyList<SupplyLedgerEntry> list)
        {
            // 固定链（PM 裁决）：Eligibility → Warehouse Priority → AvailableTime → ReleaseDate → PO+Line，
            // 不可重排、无 Enable/Disable 开关；3号位只治理参数值（此处取 WarehousePriority）。
            var rankMap = BuildRankMap(_snapshot?.Procurement.WarehousePriority);
            return list
                .Where(e => e.SourceType == Core.Enum.SupplySourceType.PIPELINE
                         || e.SourceType == Core.Enum.SupplySourceType.PURCHASE_ORDER)
                .OrderBy(e => WarehouseRank(e.WarehouseCode, rankMap))
                .ThenBy(e => e.AvailableAt ?? DateTime.MaxValue)
                .ThenBy(e => e.ReleaseDate ?? DateTime.MaxValue)
                .ThenBy(e => e.PoNo ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(e => e.LineNo ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(e => e.SourceReference ?? string.Empty, StringComparer.Ordinal)
                .ToList();
        }

        private static IReadOnlyDictionary<string, int> BuildRankMap(IReadOnlyList<string>? priority)
            => (priority ?? [])
                .Select((code, i) => new { code, i })
                .ToDictionary(x => x.code, x => x.i, StringComparer.OrdinalIgnoreCase);

        private static int WarehouseRank(string? code, IReadOnlyDictionary<string, int> rankMap)
            => code != null && rankMap.TryGetValue(code, out var i) ? i : int.MaxValue;

        private static DateTime PiSortTime(SupplyLedgerEntry e, PiSortBy sortBy) => sortBy switch
        {
            PiSortBy.CreatedAtAsc => e.CreatedAt ?? DateTime.MaxValue,
            _                     => e.IssueDate ?? DateTime.MaxValue  // IssueDateAsc（默认）；StablePiNoAsc 已在 SortPi 上游单独处理
        };

        /// <summary>返回所有供给条目（用于加载Lock数据）</summary>
        public IEnumerable<SupplyLedgerEntry> GetAllEntries()
        {
            return _ledger.Values.SelectMany(list => list);
        }

        public static string BuildKey(string materialCode, int factoryId)
            => $"{materialCode}|{factoryId}";

        /// <summary>
        /// 深拷贝供给账本（阶段2 S2.3 双跑隔离）。旧 DFS 与新 BFS 必须各自跑一份独立供给账本，
        /// 否则先跑的一遍会把共享 Inventory/PI/Procurement 扣光，后跑的一遍无可比性。
        /// 调用时机 = LoadSupplyPoolAsync（含 LoadActiveLockDataAsync 硬锁装载）之后、Pegging 扣减之前。
        /// </summary>
        public SupplyPool Clone()
        {
            var copy = new SupplyPool(_snapshot) { DataCutoffTime = DataCutoffTime };
            foreach (var (key, list) in _ledger)
                copy._ledger[key] = list.Select(CloneEntry).ToList();
            copy.TotalEntries = TotalEntries;
            return copy;
        }

        private static SupplyLedgerEntry CloneEntry(SupplyLedgerEntry e) => new()
        {
            OriginalQty        = e.OriginalQty,
            RemainingQty       = e.RemainingQty,
            LockedQty          = e.LockedQty,
            SupplyKey          = e.SupplyKey,
            PhysicalSourceKey  = e.PhysicalSourceKey,
            MaterialId         = e.MaterialId,
            FactoryId          = e.FactoryId,
            FactoryCode        = e.FactoryCode,
            SourceType         = e.SourceType,
            AvailableAt        = e.AvailableAt,
            SourceReference    = e.SourceReference,
            SupplySourceId     = e.SupplySourceId,
            WarehouseCode      = e.WarehouseCode,
            ReleaseDate        = e.ReleaseDate,
            PoNo               = e.PoNo,
            LineNo             = e.LineNo,
            PiNo               = e.PiNo,
            IssueDate          = e.IssueDate,
            CreatedAt          = e.CreatedAt,
            Confidence         = e.Confidence,
            Commitment         = e.Commitment,
            Allocations        = e.Allocations.Select(a => new AllocationRecord
            {
                AllocationSequence = a.AllocationSequence,
                AllocatedQty       = a.AllocatedQty,
                SupplyKey          = a.SupplyKey,
                SupplyType         = a.SupplyType,
                DemandKey          = a.DemandKey,
                MaterialId         = a.MaterialId,
                AllocatedAt        = a.AllocatedAt,
                RequiresProduction = a.RequiresProduction
            }).ToList(),
            Locks              = e.Locks.Select(l => new LockRecord
            {
                LockType          = l.LockType,
                LockedQty         = l.LockedQty,
                LockedToOrderId   = l.LockedToOrderId,
                LockedToDemandKey = l.LockedToDemandKey,
                LockedAt          = l.LockedAt
            }).ToList()
        };
    }

    /// <summary>供给排序事实（从 SupplyLoadRow 抽取，供多键排序使用；字段缺失为 null，排序时降级兜底）</summary>
    /// <summary>
    /// R03-01：现货物理供给键 —— 5号位 确认的正式键合同
    /// <c>(SourceSystem, ERP_MasterID/MES_ID, StorageCode, FactoryId)</c>。
    ///
    /// 与「仅 物料＋厂＋仓」的粗合并的区别：保留来源系统与 ERP/MES 主档号，
    /// 不让不同真实源行被并成一条；同时**不含** 产品族与明细行 Id ——
    /// 那两者正是原实现把同一实物拆成多个来源的根因（A-5）。
    ///
    /// 实库（批 `INVENTORY_20261008_114137`）：该键组数 102,955 ＝ 合格候选数，跨物料组 = 0。
    /// </summary>
    internal static string BuildInventoryPhysicalKey(
        string? sourceSystem, int? erpMasterId, int? mesId, string? storageCode, int factoryId)
        => $"PHY:{sourceSystem ?? "-"}:{erpMasterId?.ToString() ?? "-"}:{mesId?.ToString() ?? "-"}:{storageCode ?? "-"}:{factoryId}";

    /// <summary>
    /// R03-02（Candidate 侧，2026-10-25 0号位 授权「甲」口径）：跨 Run 的**聚合**供给身份键
    /// <c>(物料, 供给工厂, 供给类型)</c>。
    ///
    /// 与 <see cref="BuildInventoryPhysicalKey"/> 的区别：物理键是**同一次 Run 内**前序 Domain 刚消耗的
    /// 那一批货，能在内存里精确对上；聚合键是**跨 Run**读别的 ACTIVE 计划版本已占用的总量 ——
    /// 分配表里没有物理键可回连（见 <see cref="PeggingExecutionRequest.UpstreamAggregateConsumption"/> 注释）。
    ///
    /// 装载侧（`SchedulingOrchestrator.LoadExternalDomainInventoryOccupationAsync`）与入池侧
    /// （<see cref="SupplyPool"/>.Add）**必须共用本函数**生成键，否则两边键格式一漂移，扣减会静默失效。
    /// </summary>
    internal static string BuildAggregateConsumptionKey(
        string materialCode, string? factoryCode, Core.Enum.SupplySourceType sourceType)
        => $"AGG:{sourceType}:{materialCode}|{factoryCode ?? "-"}";

    /// <summary>
    /// 把「同键外部占用总量」按给定顺序分摊到各批可用量上，返回**每批被扣掉的量**。
    ///
    /// 为什么要一次分摊、而不是每批都扣总量：同键 3 批各 100、外部占用 150 时，
    ///   逐批各扣 150 会把 450 全扣光（超扣、候选凭空少 300）；本函数只扣 150，**总量守恒**。
    /// 不变式：`0 ≤ 返回值[i] ≤ availableQty[i]`，且 `Σ返回值 = min(occupiedQty, ΣavailableQty)`。
    /// 纯函数、零依赖 —— 供单测直接覆盖（候选侧跨域扣减的全部算术风险都在这里）。
    /// </summary>
    internal static decimal[] AllocateAggregateDeduction(
        decimal occupiedQty, IReadOnlyList<decimal> availableQty)
    {
        var result = new decimal[availableQty?.Count ?? 0];
        if (availableQty is null || occupiedQty <= 0m || availableQty.Count == 0)
            return result;

        var remaining = occupiedQty;
        for (var i = 0; i < availableQty.Count && remaining > 0m; i++)
        {
            var avail = availableQty[i];
            if (avail <= 0m) continue;
            var deduct = remaining < avail ? remaining : avail;
            result[i] = deduct;
            remaining -= deduct;
        }
        return result;
    }

    private sealed record SupplySortFacts(
        string? WarehouseCode = null,
        DateTime? ReleaseDate = null,
        string? PoNo = null,
        string? LineNo = null,
        string? PiNo = null,
        DateTime? IssueDate = null,
        DateTime? CreatedAt = null);

    /// <summary>
    /// 供给侧内存账本（V1.2增强版，对齐实施包§5.1）
    ///
    /// 职责：
    ///   - 维护供给的剩余可用数量（RemainingQty）
    ///   - 记录供给的业务属性（SupplyType、AvailableTime、Confidence等）
    ///   - 支持Lock份额管理
    ///   - 与DemandBalance配合，共同实现供需原子匹配
    ///
    /// V1.2核心红线：
    ///   同一物理数量在同一PlanVersion中只能有一个Supply身份
    ///   例如同一PI：PI总量、PI的XC、PI的在途、PI的Stage WIP 不能被当成四份Supply重复消费
    /// </summary>
    private sealed class SupplyLedgerEntry
    {
        // ═══════════════════════════════════════════════════════════════════════
        // 数量字段
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>原始总量（初始值，不可变）</summary>
        public decimal OriginalQty                      { get; init; }

        /// <summary>剩余可用数量（遍历时可变，初始值=OriginalQty）</summary>
        public decimal RemainingQty                     { get; set; }

        /// <summary>已锁定份额（STRICT_BINDING/DEMAND_PROTECTION/Execution）</summary>
        public decimal LockedQty                        { get; set; }

        // ═══════════════════════════════════════════════════════════════════════
        // 供给身份字段
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>供给唯一身份键（格式：MaterialId|FactoryId|SourceType|SupplySourceId）</summary>
        public string SupplyKey                         { get; init; } = string.Empty;

        /// <summary>物理来源键（例如：ProductionInstructionNo、InventoryBatchNo、PONo等）</summary>
        public string? PhysicalSourceKey                { get; init; }

        /// <summary>供给物料ID</summary>
        public int MaterialId                           { get; init; }

        /// <summary>供给工厂ID</summary>
        public int FactoryId                            { get; init; }

        /// <summary>供给工厂代码</summary>
        public string FactoryCode                       { get; init; } = string.Empty;

        // ═══════════════════════════════════════════════════════════════════════
        // 供给类型与时间
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>供给类型（INVENTORY/PRODUCTION_INSTRUCTION/PURCHASE_ORDER/VMI/IN_TRANSIT等）</summary>
        public Core.Enum.SupplySourceType SourceType    { get; init; }

        /// <summary>供给可用时间（库存=当前时间，采购=ETA，生产=预计完成时间）</summary>
        public DateTime? AvailableAt                    { get; init; }

        /// <summary>供给来源引用（原有字段，用于追溯）</summary>
        public string? SourceReference                  { get; init; }

        /// <summary>供给来源ID（原有字段，用于关联）</summary>
        public long? SupplySourceId                     { get; init; }

        // ═══════════════════════════════════════════════════════════════════════
        // 排序字段（阶段2：按 3号位 Frozen 规则三类独立排序，不扁平混排）
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>仓库/库位编码（Inventory WarehousePriority / Procurement Warehouse Priority）</summary>
        public string? WarehouseCode                    { get; init; }

        /// <summary>PO 发行时间（Procurement Release Time 排序）</summary>
        public DateTime? ReleaseDate                    { get; init; }

        /// <summary>采购订单号（Procurement PO+Line 稳定排序）</summary>
        public string? PoNo                             { get; init; }

        /// <summary>采购订单行号（Procurement PO+Line 稳定排序）</summary>
        public string? LineNo                           { get; init; }

        /// <summary>生产指示号（PI 稳定 Tie-break）</summary>
        public string? PiNo                             { get; init; }

        /// <summary>PI 发行时间（PiSort.IssueDateAsc）</summary>
        public DateTime? IssueDate                      { get; init; }

        /// <summary>PI 创建时间（PiSort.CreatedAtAsc）</summary>
        public DateTime? CreatedAt                      { get; init; }

        // ═══════════════════════════════════════════════════════════════════════
        // 置信度与承诺度（V1.2新增）
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>置信度（CONFIRMED=确定供给，ESTIMATED=估计供给/Planning-only）</summary>
        public SupplyConfidence Confidence              { get; init; } = SupplyConfidence.CONFIRMED;

        /// <summary>承诺度（词表＝`COMMITTED / CONFIRMED / ESTIMATED / NOT_COMMITTED`）</summary>
        public SupplyCommitment Commitment              { get; init; } = SupplyCommitment.COMMITTED;

        // ═══════════════════════════════════════════════════════════════════════
        // Lock与Allocation追溯（V1.2新增）
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>本次运行的Allocation记录列表（用于追溯和校验）</summary>
        public List<AllocationRecord> Allocations       { get; init; } = new();

        /// <summary>Lock记录列表（STRICT_BINDING/DEMAND_PROTECTION/Execution）</summary>
        public List<LockRecord> Locks                   { get; init; } = new();
    }

    /// <summary>供给置信度枚举</summary>
    private enum SupplyConfidence
    {
        /// <summary>确定供给（库存/已确认PO/VMI/确定PI）</summary>
        CONFIRMED,
        /// <summary>估计供给（Planning-only占位）</summary>
        ESTIMATED
    }

    /// <summary>
    /// 供给承诺度枚举。**值域对齐冻结**（`07 v5.1.13 :4136`／`:4380`／`:4841`；`06 v1.38 §4.4`；`01 v1.13 §110`）：
    /// `COMMITTED / CONFIRMED / ESTIMATED / NOT_COMMITTED`。
    /// 语义边界（`01 §110`／`06 §4.4` 逐字）：**只用于正式供给事实**；Planning-only Purchase Placeholder、
    /// 虚拟采购、虚拟生产指示**不得**使用本字段。
    /// ⚠ 本枚举目前**尚未连接到 `PeggingSupplyAllocation.CommitmentStatus` 落库**（该列现为 NULL）；
    ///     补全 4 值是为落库前置的**词表对齐**，不改变任何既有行为（默认仍 `COMMITTED`）。
    /// </summary>
    private enum SupplyCommitment
    {
        /// <summary>已承诺（可作为CTP承诺）</summary>
        COMMITTED,
        /// <summary>未承诺（不可作为CTP承诺）</summary>
        NOT_COMMITTED,
        /// <summary>已确认（来源事实已确认，未达承诺）</summary>
        CONFIRMED,
        /// <summary>估计供给（Planning-only 口径；不得作为确定性 CTP 承诺）</summary>
        ESTIMATED
    }

    /// <summary>Lock类型（§8）</summary>
    private enum LockType
    {
        /// <summary>严格绑定：1对1强绑定，其他需求完全不可用</summary>
        STRICT_BINDING,
        /// <summary>需求保护：1对N保护，保护组内需求可用，组外不可用</summary>
        DEMAND_PROTECTION,
        /// <summary>执行锁：不可逆事实（已投料、已发货），不得再分配</summary>
        EXECUTION
    }

    /// <summary>Allocation记录（用于Supply侧追溯）</summary>
    /// <summary>
    /// Allocation记录（V1.2）
    /// 记录Pegging阶段的通用逻辑分配，不是PeggingSupplyAllocation本身
    /// PlanVersionId + AllocationSequence唯一标识一笔Allocation
    /// </summary>
    private sealed class AllocationRecord
    {
        public long AllocationSequence  { get; init; }
        public decimal AllocatedQty     { get; init; }
        public string SupplyKey         { get; init; } = string.Empty;
        public string SupplyType        { get; init; } = string.Empty;
        public string DemandKey         { get; init; } = string.Empty;
        public int MaterialId           { get; init; }
        public DateTime AllocatedAt     { get; init; }

        /// <summary>
        /// 是否需要通过生产形成
        /// true: 需要生成LogicalProductionDemand交给Solver
        /// false: 库存/PO/VMI/Received等直接承接，不生成Task
        /// </summary>
        public bool RequiresProduction  { get; init; }
    }

    /// <summary>Lock记录（用于Supply侧锁定管理）</summary>
    private sealed class LockRecord
    {
        public LockType LockType        { get; init; }
        public decimal LockedQty        { get; init; }
        public long? LockedToOrderId    { get; init; }
        public string? LockedToDemandKey { get; init; }
        public DateTime LockedAt        { get; init; }
    }

    private sealed class DemandKeyQtyRow
    {
        public string DemandKey { get; set; } = string.Empty;
        public decimal Qty      { get; set; }
    }

    private sealed class SupplyLoadRow
    {
        public string MaterialCode      { get; set; } = string.Empty;
        public int MaterialId           { get; set; }
        public int FactoryId            { get; set; }
        public string FactoryCode       { get; set; } = string.Empty;
        public decimal AvailableQty     { get; set; }
        public DateTime? AvailableAt    { get; set; }
        public string? SourceReference  { get; set; }
        public long? SupplySourceId     { get; set; }
        public string? WarehouseCode    { get; set; }
        public DateTime? ReleaseDate    { get; set; }
        public string? PoNo             { get; set; }
        public string? LineNo           { get; set; }
        public string? PiNo             { get; set; }
        public DateTime? IssueDate      { get; set; }
        public DateTime? CreatedAt      { get; set; }
        // R03-01：现货物理键三列（明细层原样，仅现货行填充）
        public string? SourceSystem     { get; set; }
        public int? ERP_MasterID        { get; set; }
        public int? MES_ID              { get; set; }
        public string? StorageCode      { get; set; }
    }

    /// <summary>跨域依赖行（Domain_Dependency，真实 DomainKey 口径）</summary>
    private sealed class UpstreamDomainDependencyRow
    {
        public string UpstreamDomainCode { get; set; } = string.Empty;
        public string ChildMaterialCode  { get; set; } = string.Empty;
        public int DefaultLeadTimeDays   { get; set; }
    }

    /// <summary>上游域 PlanVersion 定位行</summary>
    private sealed class UpstreamPlanVersionRow
    {
        public int Id { get; set; }
    }

    /// <summary>上游域已落盘 Task 供给行（跨域 Quantity-Time 分段虚拟供给源）</summary>
    private sealed class UpstreamTaskSupplyRow
    {
        public string MaterialCode      { get; set; } = string.Empty;
        public int MaterialId           { get; set; }
        public int FactoryId            { get; set; }
        public string FactoryCode       { get; set; } = string.Empty;
        public decimal Quantity         { get; set; }
        public DateTime? PlannedEndTime { get; set; }
    }

    /// <summary>WIP Stage 明细行（StageProgressSnapshot 逐 Stage 行，供 PI Position 两级装载）</summary>
    private sealed class WipStageLoadRow
    {
        public string ProductionInstructionNo { get; set; } = string.Empty;
        public string MaterialCode            { get; set; } = string.Empty;
        public int MaterialId                 { get; set; }
        public int FactoryId                  { get; set; }
        public string FactoryCode             { get; set; } = string.Empty;
        public string StageCode               { get; set; } = string.Empty;
        public decimal GoodCompletedQty       { get; set; }
        public decimal PlannedQty             { get; set; }
        public decimal RemainingQty           { get; set; }
    }

    /// <summary>
    /// 跨版本连续性装载产物（由 LoadSupplyPoolAsync 随供给池一起返回）。
    /// WipRows = IN_PROGRESS StageProgressSnapshot 行（连续份额位置明细）；PiPositions = 5号位 PI Position 结果（执行起点）。
    /// PiRemainingByPi = 分桶的数量根（ERP 权威 PI 剩余，见 <see cref="PiRemainingFact.PiRemainingQty"/>）。
    /// </summary>
    private sealed class ContinuityFacts
    {
        public ContinuityFacts(
            IReadOnlyList<WipStageLoadRow> wipRows,
            IReadOnlyDictionary<string, ProductionInstructionPositionResult> piPositions,
            IReadOnlyDictionary<string, decimal> piRemainingByPi,
            PiOperationCodeResolver operationCodeResolver,
            IReadOnlyList<PiRemainingFact> piRemainingFacts)
        {
            WipRows               = wipRows;
            PiPositions           = piPositions;
            PiRemainingByPi       = piRemainingByPi;
            OperationCodeResolver = operationCodeResolver;
            PiRemainingFacts      = piRemainingFacts;
        }

        public IReadOnlyList<WipStageLoadRow> WipRows { get; }
        public IReadOnlyDictionary<string, ProductionInstructionPositionResult> PiPositions { get; }

        /// <summary>
        /// PI 级 ERP 权威剩余**事实全表**（一个 PI 一条），投影进 <see cref="DomainSolveRequest.PiRemainingFacts"/>
        /// 供 1号位 数量门禁消费（1号位 §1.3 / AUD-1-D04，2026-10-12）。
        /// 与 <see cref="PiRemainingByPi"/> 的区别：后者是 2号位 内部按 PI 号取「剩余最大」的**派生字典**（仅本层用），
        /// 前者是**未聚合事实原件**（含 MaterialId/FactoryId 等键域，供 1号位 按三元组自行索引）。
        /// </summary>
        public IReadOnlyList<PiRemainingFact> PiRemainingFacts { get; }

        /// <summary>
        /// PI 号 → ERP 权威剩余 <c>max(Quantity − ReceivedQty, 0)</c>（用户 2026-10-09 定盘）。
        /// 这是全链唯一总量根：大工艺/MES工序各层只回答「剩余现在在哪」，**任何一层的量都不得突破它**。
        /// </summary>
        public IReadOnlyDictionary<string, decimal> PiRemainingByPi { get; }

        /// <summary>续排起点「名→码 反查」器（见 <see cref="PiOperationCodeResolver"/>）。</summary>
        public PiOperationCodeResolver OperationCodeResolver { get; }
    }

    /// <summary>
    /// 续排起点「名→码 反查」（2号位 装载层，2026-10-10；口径由 5号位 2026-10-09 三轮回执 §三/§六.3 钉死）。
    ///
    /// 背景：1号位 入口按 **APS <c>RoutingOperation.OperationCode</c>** 匹配
    /// （<c>nodesByOperationCode[demand.StartOperationCode]</c>），非空但查不中即记 S26 Unscheduled、**无兜底**。
    /// 而 5号位 两条出口的 <c>StartOperationCode</c> **行为并不一致**（实读 5号位
    /// <c>ProductionInstructionPositionCalculator</c>，非据记忆）：
    ///   · <c>NextOperationContextDto.StartOperationCode</c> 装的是 **MES 侧 <c>OperationName</c>**
    ///     （<c>SplitByOperationProgress</c> 取 <c>remainingOps[0].OperationName</c>）⇒ **本解析器的适用面**；
    ///   · <c>ExistingExecutionContextDto.StartOperationCode</c>（Continuation 片的来源）在 DAG 拓扑前沿命中时
    ///     给的是**真码**（<c>BuildExistingExecutionContexts</c> 取 <c>f.OpCode</c>），走「中间态」降级分支时写死
    ///     <c>null</c>（<c>BuildIntermediateSlices</c>）并**同时吐工序名**
    ///     <c>ExistingExecutionSliceDto.StartOperationName</c> ⇒ 名确实传到了 2号位，本解析器据此反查回码。
    ///     （旧注「该 DTO 没有承载工序名的字段、名根本没传到 2号位」已作废：字段已于 5号位 侧承载。）
    ///
    /// 2号位 在本辖区（装载层）用「该 PI 事实包内的 <c>RoutingOperations</c>」（= 5号位 产出该名时的同一集合，
    /// 见 <see cref="LoadRoutingFactsAsync"/>）把名解析回码：
    ///   · 该 <c>OperationName</c> 在 PI 路由集内只对应 1 个 <c>OperationCode</c> ⇒ 命中；
    ///   · 对应多个码（= 「同文本多 PathId」的真歧义）⇒ 用**工单真实 RouteCode 子串包含**消解
    ///     （5号位 §三：APS 短码 ∈ MES 长串；双向包含以兼容 ASSY 侧两侧都是长名字串的形态）；
    ///   · 消解后仍不唯一 ⇒ 返回 <c>null</c>（Fail Closed——**绝不按相似名猜码、绝不伪造码、绝不回填 DEFAULT/1**，5号位 §六.3）。
    ///
    /// 加工侧（MACH/FORGE/MOLD）MES 工序名是纯数字私有编码，与 APS 字母表结构性不相交 ⇒ 天然反查不到，
    /// 留 null 由 1号位 Fail Closed 可观测；组装侧（ASSY）名字在 APS 字母表内 ⇒ 可命中。
    /// </summary>
    internal sealed class PiOperationCodeResolver
    {
        // PI → (OperationName → 候选 (OperationCode, RouteCode))
        private readonly Dictionary<string, Dictionary<string, List<(string Code, string RouteCode)>>> _byPi;
        // PI → 该 PI 的 OperationCode 全集（防御：出口值本身已是码时原样放行）
        private readonly Dictionary<string, HashSet<string>> _codeSpaceByPi;

        public PiOperationCodeResolver(
            IReadOnlyDictionary<string, IReadOnlyList<RoutingOperationFact>> routingOpsByPi)
        {
            _byPi = new Dictionary<string, Dictionary<string, List<(string Code, string RouteCode)>>>(StringComparer.Ordinal);
            _codeSpaceByPi = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            foreach (var (pi, ops) in routingOpsByPi)
            {
                var byName = new Dictionary<string, List<(string Code, string RouteCode)>>(StringComparer.Ordinal);
                var space = new HashSet<string>(StringComparer.Ordinal);
                foreach (var o in ops)
                {
                    if (string.IsNullOrEmpty(o.OperationCode)) continue;
                    space.Add(o.OperationCode);
                    if (string.IsNullOrEmpty(o.OperationName)) continue;
                    if (!byName.TryGetValue(o.OperationName, out var list))
                        byName[o.OperationName] = list = new List<(string, string)>();
                    list.Add((o.OperationCode, o.RouteCode ?? string.Empty));
                }
                _byPi[pi] = byName;
                _codeSpaceByPi[pi] = space;
            }
        }

        /// <summary>该 PI 的合法 <c>OperationCode</c> 空间内是否已含该值（= 出口值本身已是码，非工序名）。</summary>
        public bool IsKnownCode(string? pi, string? value)
            => !string.IsNullOrEmpty(pi) && !string.IsNullOrEmpty(value)
               && _codeSpaceByPi.TryGetValue(pi!, out var space) && space.Contains(value!);

        /// <summary>
        /// 把 5号位 出口值解析为 APS <c>OperationCode</c>；无法唯一确定时返回 <c>null</c>。
        /// </summary>
        /// <param name="pi">生产指示号（定位该 PI 的路由集）。</param>
        /// <param name="emittedValue">5号位 出口的 <c>StartOperationCode</c>（实测为 MES 工序名）。</param>
        /// <param name="workOrderRouteCode">工单真实 RouteCode（歧义消解用；PI 级上下文无此值时可传 null）。</param>
        public string? Resolve(string? pi, string? emittedValue, string? workOrderRouteCode)
        {
            if (string.IsNullOrEmpty(pi) || string.IsNullOrEmpty(emittedValue)) return null;

            // 已是本 PI 的合法码 ⇒ 原样放行（未来 MACH 侧清洗后 5号位 直接给码时不需要反查）。
            if (IsKnownCode(pi, emittedValue)) return emittedValue;

            if (!_byPi.TryGetValue(pi!, out var byName))
                return null;   // 无路由事实 ⇒ 无从反查（与「无工序」同类，非本器可解）

            if (!byName.TryGetValue(emittedValue!, out var candidates) || candidates.Count == 0)
                return null;   // 既不是码、也不匹配任何工序名 ⇒ Fail Closed（不猜码）

            var codes = candidates.Select(c => c.Code).Distinct(StringComparer.Ordinal).ToList();
            if (codes.Count == 1) return codes[0];

            // 真歧义（同名多码）：用工单真实 RouteCode 子串包含消解（5号位 §三 钉死口径）。
            if (!string.IsNullOrEmpty(workOrderRouteCode))
            {
                var narrowed = candidates
                    .Where(c => !string.IsNullOrEmpty(c.RouteCode)
                                && (workOrderRouteCode!.Contains(c.RouteCode, StringComparison.Ordinal)
                                    || c.RouteCode.Contains(workOrderRouteCode!, StringComparison.Ordinal)))
                    .Select(c => c.Code)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (narrowed.Count == 1) return narrowed[0];
            }

            return null;   // 仍不唯一 ⇒ Fail Closed（不猜码）
        }
    }

    /// <summary>MESWorkOrderSnapshot 装载行（仅 IN_PROGRESS 工单）</summary>
    private sealed class WorkOrderSnapshotRow
    {
        public string ProductionInstructionNo { get; set; } = string.Empty;
        public string MESWorkOrderNo { get; set; } = string.Empty;
        public string MaterialCode { get; set; } = string.Empty;
        public decimal PlannedQty { get; set; }
        public string WorkOrderStatus { get; set; } = string.Empty;
        public DateTime DataCutoffTime { get; set; }
    }

    /// <summary>OperationProgressSnapshot 装载行（每工单工序进度）</summary>
    private sealed class OperationProgressRow
    {
        public string ProductionInstructionNo { get; set; } = string.Empty;
        public string MESWorkOrderNo { get; set; } = string.Empty;
        public string OperationName { get; set; } = string.Empty;
        public string StageCode { get; set; } = string.Empty;
        public decimal PlannedQty { get; set; }
        public decimal GoodQty { get; set; }
        public DateTime? LastReportTime { get; set; }
        public DateTime? SourceUpdatedAt { get; set; }
        public string? LastReportResourceCode { get; set; }

        // 2026-09-30 新增：工单级路线/部门（同一工单所有行同值）
        public string? RouteCode { get; set; }
        public int? ProductionDepartmentId { get; set; }
    }

    /// <summary>Stage 顺序行（APS_BOM_STAGE_PATH_RAW，ChildMaterialCode+StageCode → StageSeq）</summary>
    private sealed class StagePathLoadRow
    {
        public string ChildMaterialCode { get; set; } = string.Empty;
        public string StageCode         { get; set; } = string.Empty;
        public int StageSeq             { get; set; }

        /// <summary>
        /// `APS_BOM_STAGE_PATH_RAW.StageScopeType`（J14-12）：`ROOT` = 该物料自身作为需求根时的链；
        /// `EDGE` = 该物料作为某父件子件时的链段（可能在父件供给阈值处截断）。
        /// **必须参与分组**——不分组就是跨 scope 的静默并集：同一 (物料, Stage) 在两种 scope 下
        /// 可给出不同 StageSeq（实库 486 个物料、581 个 (物料,Stage) 对存在此分歧），
        /// `MIN(StageSeq)` 会在无声中按「较小值」选定其一。
        /// </summary>
        public string StageScopeType { get; set; } = string.Empty;
    }

    /// <summary>`StageScopeType` 权威值：该物料**自身作为需求根**时的链（完整链）。</summary>
    private const string StageScopeRoot = "ROOT";

    /// <summary>`StageScopeType` 权威值：该物料**作为某父件子件**时的链段（可能被父件供给阈值截断）。</summary>
    private const string StageScopeEdge = "EDGE";

    /// <summary>
    /// `APS_BOM_STAGE_PATH_RAW.StageScopeType` 归一（J14-12）。实测值域只有 `ROOT` / `EDGE`；
    /// 未知/空值按 `EDGE` 处理（保守：当作子件链段，不会被误当成「自身的完整链」）。
    /// </summary>
    private static string NormalizeStageScope(string? scopeType)
        => string.Equals(scopeType?.Trim(), StageScopeRoot, StringComparison.OrdinalIgnoreCase)
            ? StageScopeRoot
            : StageScopeEdge;

    /// <summary>
    /// 单个物料的 Stage 顺序 scope 选择结果（J14-12）。
    /// <para><see cref="Scope"/> 为 null ⇒ 该物料在两种 scope 下都没有任何 StagePath（调用方计入「无 StagePath」）。</para>
    /// </summary>
    internal sealed record StageScopeSelection(
        string? Scope,
        IReadOnlyDictionary<string, int> Stages,
        int BatchPairs,
        int FallbackPairs,
        int ConflictPairs,
        IReadOnlyList<string> ConflictSamples);

    /// <summary>
    /// 单物料的 Stage 顺序合并与 scope 选择（纯函数，J14-12）。
    ///
    /// 【为什么必须按 scope 分开】`ROOT` = 该物料自身作为需求根时的**完整链**；
    ///   `EDGE` = 它作为某父件子件时的**链段**（可能被父件供给阈值截断）。
    ///   两者对同一 (物料, Stage) 可以给出不同 `StageSeq`（全库实测 486 物料 / 581 对）。
    ///   不分 scope 的 `MIN(StageSeq)` 等于**跨 scope 静默并集**：结果取决于谁的值更小，
    ///   既不表达任何业务口径，也无法在结果差异时回溯原因。
    ///
    /// 【选择口径】**ROOT 优先，EDGE 仅兜底**：PI 是「该物料自身的生产指示」（自身完工），
    ///   只有完整链才能定位在制位置；EDGE 链被父件阈值截断，会把物料误判成「行将完工」。
    ///   两 scope 同时存在且同 Stage 的 Seq 不同时，逐对计数并回传样本，供调用方 WARNING 留痕。
    ///
    /// 【未见回归的依据（2026-10-17 实测）】PV2 关联批次内的 9,857 个 PI 物料**全部只有 ROOT 行**
    ///   （EDGE 行 0 个）⇒ 本口径选择对当前数据零差异；改动只为消除「跨 scope 取 MIN」这一形态本身。
    /// </summary>
    internal static StageScopeSelection SelectStageScope(
        IReadOnlyDictionary<string, int>? rootBatch,
        IReadOnlyDictionary<string, int>? rootFallback,
        IReadOnlyDictionary<string, int>? edgeBatch,
        IReadOnlyDictionary<string, int>? edgeFallback)
    {
        static Dictionary<string, int>? Merge(
            IReadOnlyDictionary<string, int>? batch, IReadOnlyDictionary<string, int>? fallback)
        {
            if (batch is null && fallback is null) return null;
            var merged = new Dictionary<string, int>(StringComparer.Ordinal);
            if (fallback is not null)
                foreach (var kv in fallback) merged[kv.Key] = kv.Value;
            if (batch is not null)
                foreach (var kv in batch) merged[kv.Key] = kv.Value;
            return merged;
        }

        var root = Merge(rootBatch, rootFallback);
        var edge = Merge(edgeBatch, edgeFallback);

        var conflictPairs = 0;
        var samples = new List<string>();
        if (root is not null && edge is not null)
        {
            foreach (var kv in edge)
            {
                if (!root.TryGetValue(kv.Key, out var rootSeq) || rootSeq == kv.Value) continue;
                conflictPairs++;
                if (samples.Count < 10) samples.Add($"{kv.Key}:ROOT={rootSeq},EDGE={kv.Value}");
            }
        }

        var chosen = root ?? edge;
        if (chosen is null)
            return new StageScopeSelection(null, new Dictionary<string, int>(), 0, 0, conflictPairs, samples);

        var scope = root is not null ? StageScopeRoot : StageScopeEdge;
        var batchPairs = (root is not null ? rootBatch?.Count : edgeBatch?.Count) ?? 0;
        return new StageScopeSelection(
            scope, chosen, batchPairs, chosen.Count - batchPairs, conflictPairs, samples);
    }

    /// <summary>
    /// PI 级库存行（ext_ERP_Inventory_View × ext_MES_ProcessCode_View）
    /// WarehouseCode=6位工序码 LEFT JOIN ProcessCode→StageCode，未命中 StageCode 落 UNKNOWN。
    /// </summary>
    private sealed class PiInventoryLoadRow
    {
        public string MaterialCode  { get; set; } = string.Empty;
        public string FactoryCode   { get; set; } = string.Empty;
        public string WarehouseCode { get; set; } = string.Empty;
        public decimal Quantity     { get; set; }
        public string? StageCode    { get; set; }
        /// <summary>该仓库工序码是否为 XC（ext_MES_ProcessCode_View.ERPProperty='XC'）。</summary>
        public int IsXc             { get; set; }
    }

    /// <summary>
    /// PI 级 XC 行：APS 本地夜间快照 <c>InventoryFact_PiXc</c>
    /// （由 <c>sp_SyncPiXcSnapshot</c> 从 ODS `ODS_ProductionInstructionInventoryBalance`
    ///   经同义词 `ext_ODS_…` 全量刷新；八列口径见冻结 `08` §8a，落表时 ERPProperty 已是 'XC'）。
    /// </summary>
    private sealed class PiXcLoadRow
    {
        public string ProductionInstructionNo { get; set; } = string.Empty;
        public string MaterialCode            { get; set; } = string.Empty;
        public string FactoryCode             { get; set; } = string.Empty;
        public string WarehouseCode           { get; set; } = string.Empty;
        public string? StageCode              { get; set; }
        public decimal Quantity               { get; set; }
        public DateTime? SnapshotTime         { get; set; }
    }

    /// <summary>
    /// 快照刷新台账行（`APS_ETL_Log` 中 `Step='sp_SyncPiXcSnapshot'` 的最近一条）。
    /// 用来把「表里有数据」与「这批数据可信且新鲜」分开：刷新失败时 SP 不清表，旧批仍在。
    /// </summary>
    private sealed class PiXcSyncLogRow
    {
        public string BatchNo     { get; set; } = string.Empty;
        public string Status      { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public string? Message    { get; set; }
    }

    /// <summary>
    /// 跨厂边行：ext_MES_APS_BOM_Workset_CrossFactoryEdge。
    /// 物理表无 EdgeSequence，装载时按 (ChildMaterialCode, BatchNo, WorksetId, Id) 稳定排序自编号。
    /// 归组键 = ChildMaterialCode（PI 物料作为子件，其产品跨厂转运的边）。
    /// </summary>
    private sealed class CrossFactoryEdgeLoadRow
    {
        public string ChildMaterialCode { get; set; } = string.Empty;
        public string FromStageCode     { get; set; } = string.Empty;
        public string FromFactoryCode   { get; set; } = string.Empty;
        public string ToStageCode       { get; set; } = string.Empty;
        public string ToFactoryCode     { get; set; } = string.Empty;
    }

    private sealed class TransitLoadRow
    {
        public string MaterialCode      { get; set; } = string.Empty;
        public string FactoryCode       { get; set; } = string.Empty;  // 目标工厂（到货厂）
        public string SourceFactoryCode { get; set; } = string.Empty;  // 源工厂（发货厂）
        public decimal Quantity         { get; set; }
        public DateTime? Eta            { get; set; }
        public DateTime? ReleaseDate     { get; set; }
        public string SourceDocumentNo  { get; set; } = string.Empty;
        public string? OrderType        { get; set; }
    }

    /// <summary>
    /// MaterialStageDeptContext 裁剪行（IsCurrent=1）：
    /// (MaterialId, StageCode) → DefaultProductionDepartmentId。
    /// </summary>
    private sealed class MaterialStageDeptContextLoadRow
    {
        public int MaterialId { get; set; }
        public string StageCode { get; set; } = string.Empty;
        public int DefaultProductionDepartmentId { get; set; }
    }

    /// <summary>生产部门主档最小列（G-2 投影要「部门 → 工厂」，日历行的 FactoryId 不作权威）。</summary>
    private sealed class DepartmentFactoryRow
    {
        public int Id { get; set; }
        public int FactoryId { get; set; }
    }

    private sealed class ManualCapacitySlotKeyDto
    {
        public int ManualSlotId { get; set; }
        public int ProductionDepartmentId { get; set; }
        public string OperationName { get; set; } = string.Empty;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 供给池装载
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 装载供给池（INVENTORY + PIPELINE + WIP）
    /// </summary>
    private async Task<(SupplyPool Pool, ContinuityFacts Continuity)> LoadSupplyPoolAsync(
        PeggingExecutionRequest request,
        FrozenStrategySnapshot frozenSnapshot,
        BomSnapshot bomSnapshot,
        CancellationToken ct,
        bool persistPositionSnapshot = true)
    {
        // R03-02：跨域物理消耗账本由编排层持有并透传；单域运行时为 null，行为不变。
        // 两个账本互斥：FULL 同一 Run 内逐域串行走细粒度物理键；Candidate 跨 Run 只能走聚合键（分配表无物理键）。
        var pool    = new SupplyPool(
            frozenSnapshot,
            request.UpstreamPhysicalConsumption,
            request.UpstreamAggregateConsumption);
        var cutoff  = request.SnapshotAt == default ? DateTime.Now : request.SnapshotAt;
        pool.DataCutoffTime = cutoff;   // P0-07：占位采购 AvailableTime 基准 = 本 Run 冻结 DataCutoffTime

        // 分段计时（2026-10-09）：本方法是全链最大一块（小样本实测 126s / 总 161s），
        // 且**完全不随订单集收窄** ⇒ 不逐段量出来就是黑盒。定稿验收后可按需摘除。
        var ssw  = System.Diagnostics.Stopwatch.StartNew();
        var last = 0L;
        void Mark(string name)
        {
            var now = ssw.ElapsedMilliseconds;
            _logger.LogInformation("[Pegging][供给池阶段耗时] {Name}={Delta}ms (累计 {Total}ms)",
                name, now - last, now);
            // 同步落一行进度（`ComputeStageProgress`）：日志只在本机、会被轮转，
            // 表是为了上线后从任意客户端一条 SQL 就能看到「这一段花了多久」。
            ComputeStageProgressMonitor.Current?.Completed(name, now - last);
            last = now;
        }

        var inventoryRows = await _connectionManager.QueryAsync<SupplyLoadRow>(
            // R03-01（2026-10-23）：现货装载改到**物理粒度**，不再读 InventoryBalance。
            //
            // 原实现读 `InventoryBalance`（物料×族×厂，Step 5 按族 SUM 明细），且 `OUTER APPLY TOP 1`
            // 按 `ProductFamilyId` 取明细 Id 作 `INV:{Id}`。后果（实库已证）：
            //   ① 同一批物理库存被复制到 N 个产品族明细行，余额层按族 SUM ⇒ 供给池把同一实物算 N 次；
            //   ② 每族的明细 Id 不同 ⇒ 同一实物被当作 N 个不同物理来源 ⇒ 重复消费校验连组都进不到一起。
            //
            // 现改为按 §2.2 已证口径直接取自明细层：**物理键 = (SourceSystem, ERP_MasterID/MES_ID, StorageCode, FactoryId)**，
            // 组内取 `MAX(Quantity)`（同族复制份数量恒等，实测「多族组内数量不等」= 0 组）。
            // 实库闭合（批 INVENTORY_20261008_114137，2026-10-23 复核）：
            //   明细 105,704 行 → 物理组 **102,955** ＝ `InventorySupplyCandidate.IsEligible=1` 行数 102,955；
            //   族求和 262,614,785.8190 → 物理量 **248,048,845.5748**（差 14,565,940.2442，即 2,275 组的重复计量）；
            //   物理键跨物料组 = 0 ⇒ 键与物料一一对应，按 物料＋键 分组不会误并。
            // `AllocatedQty` 全表恒 0（运行期禁止回写，`01 §97`），故不读余额层不丢任何已占用扣减。
            // 同时补 `WarehouseCode`（原实现传 null ⇒ 冻结的 Inventory.WarehousePriority 对现货不生效）。
            @"SELECT
                  d.MaterialCode,
                  m.Id              AS MaterialId,
                  d.FactoryId,
                  f.Code            AS FactoryCode,
                  MAX(d.Quantity)   AS AvailableQty,
                  NULL              AS AvailableAt,
                  NULL              AS SourceReference,
                  MIN(d.Id)         AS SupplySourceId,
                  d.StorageCode,
                  d.SourceSystem,
                  d.ERP_MasterID,
                  d.MES_ID
              FROM InventoryAvailableSupplyDetail d
              INNER JOIN Material m ON m.MaterialCode = d.MaterialCode
              INNER JOIN Factory f  ON f.Id           = d.FactoryId
              GROUP BY d.MaterialCode, m.Id, d.FactoryId, f.Code,
                       d.StorageCode, d.SourceSystem, d.ERP_MasterID, d.MES_ID
              HAVING MAX(d.Quantity) > 0",
            db: DatabaseId.APS);

        foreach (var r in inventoryRows)
            pool.Add(r.MaterialCode, r.MaterialId, r.FactoryId, r.AvailableQty,
                     null, Core.Enum.SupplySourceType.INVENTORY,
                     null, r.FactoryCode, r.SupplySourceId,
                     sort: new SupplySortFacts(WarehouseCode: r.StorageCode),
                     physicalSourceKey: BuildInventoryPhysicalKey(
                         r.SourceSystem, r.ERP_MasterID, r.MES_ID, r.StorageCode, r.FactoryId));
        Mark("①库存 INVENTORY(物理粒度 GROUP BY)");

        // R03-02（Candidate）：库存条目已全部入池、其它供给类型尚未入池 —— 此刻做跨域聚合扣减，
        // 保证「其它 Domain 现行 ACTIVE 的库存占用」不会被本域候选释放。
        if (request.UpstreamAggregateConsumption is { Count: > 0 } aggregateConsumption)
        {
            pool.ApplyAggregateCrossDomainConsumption();
            _logger.LogInformation(
                "[Pegging][R03-02][Candidate] 跨域库存聚合扣减：外部占用聚合键 {KeyCount} 个 / Σ{Occupied}；" +
                "本域命中 {HitEntries} 个供给条目 / 实扣 Σ{Deducted}" +
                "{Tail}",
                aggregateConsumption.Count,
                aggregateConsumption.Values.Sum(),
                pool.CrossDomainAggregateDeductedEntryCount,
                pool.CrossDomainAggregateDeductedQty,
                pool.CrossDomainAggregateUnappliedQty > 0m
                    ? $"；⚠ 扣不完 Σ{pool.CrossDomainAggregateUnappliedQty}（外部占用超过本域同键可用量，" +
                      "供给快照与其它域分配不是同一时点，需核对）"
                    : string.Empty);
        }

        // 采购/定时供给：切 5号位 ITimedSupplyFactLoader 读原始事实（Eta/ReleaseDate，AvailableTime 留 2号位算），
        // 再由 2号位用 AvailableTimeCalculator（EtaInvariant 三级链 + ArrivalToUsableOffset）内存计算 AvailableTime（阶段 3，
        // 不再直读 sfp.AvailableTime）。
        // ⚠ 白名单现状（2026-10-16 逐行核对 `TimedSupplyFactLoader.LoadRawFactsAsync` 的 `SupplyType IN (...)`）：
        //   装载器白名单**仍含** `INTERPLANT_IN_TRANSIT`（第五项），即**并未**在对方层排除。
        //   ⇒ 本层在入池前用 `IsForbiddenStandaloneSupplyType` 拦下（见下方循环），不依赖对方先改白名单。
        // 厂间在途分两类（PM 2026-09-09 v1.0）：PI 级 Transit = PI Position（LoadTransitFactsAsync →
        // ProductionInstructionPositionCalculator）；SH 级供给 = [Order] CustomerSegment='跨厂' 订单
        // （LoadInterFactoryShipmentsAsync 单一供给，不再三段式 Transit/Received 闭合）。均待真实事实就绪后正式启用。
        var rawFacts = await _timedSupplyFactLoader.LoadRawFactsAsync(
            new SupplyFactScope { DataCutoffTime = cutoff }, ct);

        Dictionary<(string, int, int, string), DateTime> manualEtaMap = new();
        if (rawFacts.Count > 0)
        {
            // Manual ETA 覆盖表人工维护、天然小：全量查 IsActive=1，内存按业务键 (PO,LineNo,MaterialId,Warehouse)
            // 精确匹配（BuildManualEtaMap 兜底）。避免 distinct MaterialId=36k / PONo=56k 大 IN 列表超
            // SQL Server 2100 参数上限（1号位 2026-09-21 催办）。
            var manualEtaOverrides = await _procurementManualEtaRepo.GetActiveOverridesAsync(ct);
            manualEtaMap = AvailableTimeCalculator.BuildManualEtaMap(manualEtaOverrides);
        }

        var blockedForbiddenType = 0;

        // ②-b 无时点采购供给留痕（0号位《03_R04 2/5号位回执交叉核证》§三-1）。
        //   三级链（Manual → ERP → ReleaseDate+DefaultLT）皆空 ⇒ `AvailableAt = null`，
        //   而 `pool.Add` 照样收、扣减侧对 `AvailableAt` **只排序不判可行性** ⇒ 等价「立即可用」，会顶需求。
        //   ⇒ 这正是 §三-1 逐字禁止的「借假日期生成错误 Supply / 默认 0 天形成可信时点」。
        //   **0号位 2026-10-26 Q3 已裁定**：「不可分配/扣减，可独立记录诊断，不必新库列；及时阻断错误即期消耗」
        //   ⇒ 本处已由「只计数留痕」改为**不入池**（见下方循环内的 `continue`）。
        var noTimeBySupplyType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var noTimeNoLtRule = 0;
        var noTimeWarehouses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var fact in rawFacts)
        {
            // A-6 供给身份硬闸：冻结禁令类型不得作为独立 Supply 入池（不分辨时间，先拒）。
            if (IsForbiddenStandaloneSupplyType(fact.SupplyType))
            {
                blockedForbiddenType++;
                _logger.LogWarning(
                    "[Pegging] 供给入池拒绝 Issue[FORBIDDEN_SUPPLY_TYPE]: SupplyType={SupplyType} 按冻结规则" +
                    "不得作为独立 Supply/PSA（厂间在途只是 PI Position 的一种当前位置）。" +
                    "MaterialId={MaterialId}, FactoryId={FactoryId}, SourceDocumentNo={DocNo} ⇒ 已拦截，不入池",
                    fact.SupplyType, fact.MaterialId, fact.FactoryId, fact.SourceDocumentNo);
                continue;
            }

            // Explain = Compute 的带因版本（同源，不会与 Compute 漂移）；多拿的那几个 bool 只用于下面留痕。
            // 逾期重估基准 = 本 Run 数据截止时点（与上面 SupplyFactScope.DataCutoffTime 同一个变量，不另取）。
            var eta = AvailableTimeCalculator.Explain(fact, manualEtaMap, frozenSnapshot, cutoff);
            var availableAt = eta.AvailableAt;
            if (availableAt.HasValue && availableAt > cutoff)
                continue; // 超出本次运行时窗的未来供给（等价旧 SQL 的 AvailableTime <= Cutoff 过滤）

            if (!availableAt.HasValue)
            {
                noTimeBySupplyType[fact.SupplyType] =
                    noTimeBySupplyType.GetValueOrDefault(fact.SupplyType) + 1;
                if (!eta.HasPurchaseLtRule) noTimeNoLtRule++;
                var warehouse = string.IsNullOrWhiteSpace(fact.StorageCode) ? "<NULL>" : fact.StorageCode;
                noTimeWarehouses[warehouse] = noTimeWarehouses.GetValueOrDefault(warehouse) + 1;

                // ── 0号位 2026-10-26《采购ETA三处整改六问正式回复 v1.1》§二A ＋ §三「第一拍」落码 ──
                //   ⚠ 引用锚点已于 2026-10-26 更换：本处原先引的是同日《采购ETA与参数治理联合审核
                //   正式回复》（下称 v1.0）§二 Q3，而 v1.1 已**完整替代并整体废止 v1.0**（「不允许两版混用」）。
                //   结论本身被 v1.1 §二A 原样保留、且正是其「第一拍（可立即做）」：
                //   「同意仅在本轮采购管道供给中对无合法 `AvailableAt` 禁扣」「采购有正式来源但时间未知，
                //   保留来源事实/Issue，**禁形成确定性可扣供给**」。
                //   此前本处**只留痕、照常入池**：扣减侧对 `AvailableAt` 只排序不判可行性 ⇒ 等价「立即可用」，
                //   会顶掉真实需求（即 Q3 要阻断的「错误即期消耗」）。现改为**不入池**，事实与分类诊断照旧留痕。
                //   影响面（PV190 实测）：3,778 / 382,939 行（PURCHASE_IN_TRANSIT=3,160、ARRIVED_NOT_RECEIVED=618）。
                //   ⚠ 作用域＝**本循环（采购/定时供给）**。下方 PI Supply 的 `AvailableAt=null` 表达的是
                //   「在制/在库事实、无到货时点语义」，**不是**「算不出时点」，故不在 Q3 所指之列，不得一并阻断
                //   （否则 `includePi` 的 PI 承接会整片失效）。
                continue;
            }

            pool.Add(fact.MaterialCode, fact.MaterialId, fact.FactoryId, fact.RemainingQty,
                     availableAt, Core.Enum.SupplySourceType.PIPELINE,
                     fact.SourceDocumentNo, fact.FactoryCode,
                     sort: new SupplySortFacts(
                         WarehouseCode: fact.StorageCode,
                         ReleaseDate: fact.ReleaseDate,
                         PoNo: fact.SourceDocumentNo,
                         LineNo: fact.SourceDocumentLineNo),
                     physicalSourceKey: string.IsNullOrWhiteSpace(fact.SourceDocumentNo)
                         ? null
                         : $"PO:{fact.SourceDocumentNo}:{fact.SourceDocumentLineNo}");
        }
        var noTimeTotal = 0;
        foreach (var kv in noTimeBySupplyType) noTimeTotal += kv.Value;
        if (noTimeTotal > 0)
        {
            var breakdown = string.Join(", ", noTimeBySupplyType
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key}={kv.Value}"));
            var topWarehouses = string.Join(", ", noTimeWarehouses
                .OrderByDescending(kv => kv.Value)
                .Take(5)
                .Select(kv => $"{kv.Key}({kv.Value})"));
            _logger.LogWarning(
                "[Pegging] ②定时供给 Issue[SUPPLY_AVAILABLE_AT_NULL]：{NoTime}/{Total} 行**算不出任何可用时点**" +
                "（Manual / ERP / ReleaseDate+DefaultLT 三级皆空）⇒ **已按 0号位 v1.1 §二A／§三「第一拍」阻断：不入池、不参与分配/扣减**" +
                "（事实与分类诊断保留；不新增库列）。分类：{Breakdown}。" +
                "其中 {NoRule} 行连 DefaultPurchaseLt 规则都未命中（收货仓码 / 物料未配置）；样本收货仓：{Warehouses}。" +
                "此前的「以 null 入池、等价立即可用、可顶需求」即 Q3 判定的「错误即期消耗」，已关闭。",
                noTimeTotal, rawFacts.Count, breakdown, noTimeNoLtRule, topWarehouses);
        }

        if (blockedForbiddenType > 0)
            _logger.LogWarning(
                "[Pegging] ②定时供给：装载 {Total} 行，其中 {Blocked} 行因 SupplyType 属冻结禁令类型被拦截未入池" +
                "（见逐条 Issue[FORBIDDEN_SUPPLY_TYPE]）",
                rawFacts.Count, blockedForbiddenType);
        else
            _logger.LogInformation(
                "[Pegging] ②定时供给：装载 {Total} 行，冻结禁令类型拦截 0 行", rawFacts.Count);

        Mark($"②定时供给(采购在途 LoadRawFacts={rawFacts.Count}行 + AvailableTime 计算)");

        // ── T2-01：PI 权威剩余事实（来源 [Order] ∪ 本域 BOM 可达物料的订单链，与 Stage / MES 工单 / PI Position 完全解耦）──
        // 必须**先于**一切 Stage 判断建立 PI 宇宙：真实 PI 即使当前没有任何 Stage 报工行，
        // 也必须在这里出现、也必须有权威剩余量（PM T2-04.2）。
        // 口径：PiRemainingQty = max(Quantity − ISNULL(ReceivedQty, 0), 0)（用户 2026-10-09 定盘）。
        // 范围：见 LoadPiRemainingFactsAsync —— [Order] 只能给出顶层成品（归域闸门所限），
        //       下阶加工件的 PI 事实按本域物料宇宙从 Order_Canonical 补取。
        var piFacts = await LoadPiRemainingFactsAsync(request.PlanVersionId, bomSnapshot.MaterialUniverse, ct);
        Mark($"③PI 权威剩余事实(LoadPiRemainingFacts={piFacts.Count}行)");

        // ── T2-02：真实 PI Supply 入池（PM 1009 §T2-02；供给侧的**唯一**写入点）──
        // 目的：Demand 先承接 PI，缺口才转新生产。缺了这段，所有本可由 PI 承接的需求会整批塌成
        // NEW_REQUIREMENT ⇒ 重复生产（正是代码审核报告 P0-02「PI Position 反向裁剪 PI Supply」的极端形态）。
        //
        // 口径（逐条对应 PM 整改令）：
        //   · 数量 = PiRemainingQty（ERP 权威剩余，T2-01），**不由 Position/Stage 反向裁剪**（T2-02.1 / P0-02 裁定）；
        //   · 一个 PI = 一个物理供给身份（physicalSourceKey = "PI:"+PI号），**不拆成多个 Stage Supply**（T2-02.1）；
        //   · 保留 Material + Factory + PI 身份校验（入池键 = MaterialCode|FactoryId，物料身份另由
        //     `ValidateEligibility` 二次校验，T2-02.1）；
        //   · 同 Run 内同一物理数量只消费一次 —— 由 `SupplyLedgerEntry.RemainingQty` 原子扣减保证（T2-02.2）；
        //   · 先有 PI 号 + 承接量，Position 只用于起点切片，**不是第二笔供给**（T2-02.3）；
        //   · 自消费保护由**层级门**实现，不再用逐行卫语句：`NetAndAllocate` 传 `includePi = bomLevel > 0`
        //     ⇒ 顶层（L0）整层不取 PI 条目，任何 L0 需求（O 行客户订单 / P 行 PI 指令行）都无从消费 PI，
        //     结构上不可能自消费（T2-02.4）；下阶（L1+）组件需求正常取 PI 池。
        //   · **身份闸门**（B-01）：进池的 `piFacts` 已在上游剔掉**全部**客户订单行（含厂间 SH），见
        //     `LoadPiRemainingFactsAsync` 的 B-01 说明 —— **层级门管「哪一层能取」，身份门管「什么身份能进」**，
        //     两者不可互相替代：没有身份门时，客户订单行仍可在 L1+ 被消费掉。
        //
        // ⛔ 不得恢复「StageProgressSnapshot.RemainingQty 直接当 PI Supply」的旧写法（T2-02.5）——
        //    那是 MES 在制量（执行连续性数量，走 T2-06 分桶），与 ERP PI 剩余是两个不同的量。
        //
        // ⚠️ SupplySourceType 取 WIP：`SortPi`（供给池 PI 内排序）认 WIP|PRODUCTION_INSTRUCTION 两种；
        //    审核报告原文亦称其为「PI/WIP Supply」。**此命名待 PM/1号位 口径确认**（落台账留痕）。
        var piSupplyEntries = 0;
        var piSupplyQty = 0m;
        foreach (var f in piFacts)
        {
            if (f.PiRemainingQty <= 0m) continue;   // 剩余 0 = 已完工入库，无可承接份额（T2-01.2 的 clamp 结果）

            pool.Add(f.MaterialCode, f.MaterialId, f.FactoryId, f.PiRemainingQty,
                     null, Core.Enum.SupplySourceType.WIP,
                     f.ProductionInstructionNo, f.FactoryCode,
                     sort: new SupplySortFacts(
                         PiNo: f.ProductionInstructionNo,
                         IssueDate: f.IssueDate),
                     physicalSourceKey: $"PI:{f.ProductionInstructionNo}");
            piSupplyEntries++;
            piSupplyQty += f.PiRemainingQty;
        }
        _logger.LogInformation(
            "[Pegging][T2-02] PI Supply 入池完成：条目={Entries} 个（PI 事实 {Facts} 个，剩余≤0 跳过 {Skipped} 个），" +
            "Σ承接量={Qty}；身份=PI号（physicalSourceKey=\"PI:<PI号>\"），数量=ERP PiRemainingQty，未经 Position 裁剪",
            piSupplyEntries, piFacts.Count, piFacts.Count - piSupplyEntries, piSupplyQty);

        // ── Stage 定位明细（**不再是 PI 总量来源**）──
        // PM 2026-08-28（先选 PI → 再消费该 PI 内部 PI Position）+ PM 2026-09-07 P0-02 正式回复的原始三步：
        //   1) 读 Stage 明细（StageCode/GoodCompletedQty/RemainingQty），按 PI 聚合取 PI 级 RemainingQty；
        //   2) 调 5号位 IProductionInstructionPositionCalculator 拆 located/unlocated（供执行起点切片，不决定供给数量）；
        //   3) PI Supply = ERP RemainingQty 全额入池；Located + UNLOCATED 合计 = RemainingQty（数量不闭合由快照登记）。
        //
        // ⚠️ T2-04.1（2026-10-09 整改）：第 1 条的「按 PI 聚合取 PI 级 RemainingQty」**已废止**。
        //    本查询从此只回答「该 PI 已报到哪些 Stage、各 Stage 进度多少」——是**定位明细**，
        //    绝不再充当 PI 总量（旧写法 `ErpRemainingQty = group.Max(r => r.RemainingQty)` 已删）。
        //    总量唯一来源 = 上面的 piFacts。
        // ── 原始 MES 快照源 Run（0号位《PM回复1009-1》§三 A-Q1/A-Q2/A-Q3）──
        // 读侧：候选 / FAILED 恢复须沿 Base 链继承「有三类完整快照」的源 Run，不得读候选自身 Run；夜间 = 本 Run（行为不变）。
        // 写侧不受影响：候选衍生结果（PI Position 快照等）仍落候选自身 Run（见 PersistDomainAndPeggingInTransactionAsync / SavePiPositionSnapshotsAsync）。
        var snapshotRunId = await SnapshotRunResolver.ResolveSnapshotRunIdAsync(
            _connectionManager, request.PlanVersionId, _logger, ct);

        var wipStageRows = (await _connectionManager.QueryAsync<WipStageLoadRow>(
            @"SELECT sp.ProductionInstructionNo,
                     sp.MaterialCode,
                     m.Id             AS MaterialId,
                     o.FactoryId      AS FactoryId,
                     f.Code           AS FactoryCode,
                     sp.StageCode,
                     sp.GoodCompletedQty,
                     sp.PlannedQty,
                     sp.RemainingQty
              FROM StageProgressSnapshot sp
              INNER JOIN Material m ON m.MaterialCode = sp.MaterialCode
              -- 圈定在制 PI 集合用「需求侧订单」([Order] 携带 MTS_InstructionNo)，不得用 [Task]：
              -- [Task] 是本轮求解的落库输出，首跑为空 → join 恒 0 → wipStageRows 空 → 连续性切不出（鸡生蛋）。
              INNER JOIN (
                  SELECT DISTINCT o.MTS_InstructionNo, o.FactoryId
                  FROM [Order] o
                  WHERE o.PlanVersionId = @PlanVersionId
                    AND o.MTS_InstructionNo IS NOT NULL
              ) o ON o.MTS_InstructionNo = sp.ProductionInstructionNo
              INNER JOIN Factory f ON f.Id = o.FactoryId
              WHERE sp.ScheduleRunId = @SnapshotRunId
              AND sp.RemainingQty > 0",
            new { request.PlanVersionId, SnapshotRunId = snapshotRunId },
            db: DatabaseId.APS)).ToList();

        Mark($"④Stage 定位明细(StageProgressSnapshot={wipStageRows.Count}行)");

        var (piPositions, opCodeResolver) = await LoadPiPositionsAsync(piFacts, wipStageRows, frozenSnapshot, request.PlanVersionId, snapshotRunId, cutoff, ct, persistPositionSnapshot);
        Mark($"⑤PI Position(5号位计算器,={piPositions.Count}个PI)");

        // ── W1（PM 0914 红线）：IN_PROGRESS 的 RemainingQty 不再作为 WIP Supply 把需求 Q 扣掉。
        // 被覆盖部分必须以 Continuation Slice + Free Slice 一起进 1号位（见 ApplyContinuityBucketing）。
        // wipStageRows / piPositions 仍保留并随返回带出：供「逐 PI 识别连续份额 + 执行起点（StartOperation）」，
        // 以及 LoadPiPositionsAsync 的 PI Position 快照落库 / 数量闭环校验副作用。

        // ── INTER_FACTORY_ORDER（厂间出荷指示，SH级）供给：SH 单一身份（PM 2026-09-09 v1.0）──
        // SH 本身是 Order（OrderType=SALES_ORDER + CustomerSegment='跨厂'，OrderNo=SH号），
        // 单一 Supply 身份，禁拆 Transit/Received/Unproduced 三段；单个 SH 整单一次入库。
        // 每 SH 只入池一条（PhysicalSourceKey=SH号=OrderNo），量=Order.Quantity（指令总量），由红线③⑥ 防重复计量。
        // AvailableAt = SourceReadyTime + CrossFactoryLT；SourceReadyTime = max(源厂 Task PlannedEndTime)，
        // 依赖 Layer-2「源厂需求→求解→回传」（待 PM 接口/源厂方向字段落地）——当前置 null（源厂未排），见台账。
        var shipments = await LoadInterFactoryShipmentsAsync(request.PlanVersionId, ct);
        Mark($"⑥厂间 SH 供给({shipments.Count}行)");
        foreach (var s in shipments)
        {
            // TODO(Layer-2)：s.SourceFactoryId 就绪后，读上游源厂域落盘 Task 取 max(PlannedEndTime)=SourceReadyTime，
            // 再 +CrossFactoryLT 得 availableAt；当前 SourceReadyTime 尚未产出，置 null。

            // A5（v1.1 §B-8.4 口径 A）：SH 源厂**照取 V3**（[Order].SourceFactoryId）。
            // 冻结 DDL `08:83` 逐字：
            //   「sp_SyncOrdersToPartitionTable补SourceFactoryId映射；INTER_FACTORY禁止目标Factory静默冒充源工厂。」
            // 三态，**仅登记 Issue（日志）**，**不改既有入池取值**（仍以 TargetFactoryId 入池）：
            //   源厂 ＝ 目标厂 → SH_SOURCE_FACTORY_MASKED（目标厂冒充源厂）
            //   源厂 IS NULL  → SH_SOURCE_FACTORY_UNKNOWN（ERP 未给 RevInstrProcCode，或工序码未命中字典）
            //   源厂 ≠ 目标厂 → 健康态
            // G-27 归 2号位：`sp_ValidateAndPromoteOrders` 已定「命中→厂码；未命中→NULL＋Issue（1h）」，
            // 所以 NULL 不再是「翻译链没做」，而是「字典缺项」的事实表达，必须与 MASKED 分开计数。
            if (IsSourceFactoryUnknown(s.SourceFactoryId))
                _logger.LogWarning(
                    "[Pegging] SH 源厂 Issue[SH_SOURCE_FACTORY_UNKNOWN]：源厂未知" +
                    "（ERP 未给 RevInstrProcCode，或工序码未命中 ext_MES_ProcessCode_View 字典）。" +
                    "SH={ShipmentNo}, MaterialCode={MaterialCode}, FactoryId={FactoryId}",
                    s.ShipmentNo, s.MaterialCode, s.TargetFactoryId);
            else if (IsSourceFactoryMasked(s.SourceFactoryId, s.TargetFactoryId))
                _logger.LogWarning(
                    "[Pegging] SH 源厂 Issue[SH_SOURCE_FACTORY_MASKED]：源厂＝目标厂（目标厂静默冒充源工厂，对齐冻结 DDL 08:83）。" +
                    "SH={ShipmentNo}, MaterialCode={MaterialCode}, FactoryId={FactoryId}",
                    s.ShipmentNo, s.MaterialCode, s.TargetFactoryId);

            pool.Add(s.MaterialCode, s.MaterialId, s.TargetFactoryId, s.InstructionQty,
                     null, Core.Enum.SupplySourceType.INTER_FACTORY_ORDER,
                     s.ShipmentNo, s.TargetFactoryCode,
                     physicalSourceKey: s.ShipmentNo);
        }

        _logger.LogDebug(
            "[Pegging] 供给池明细: INVENTORY={Inv}, WIP(PI)={Wip}, PIPELINE={Pipe}, PI Position={PiPos}, SH={Sh}；" +
            "PI 权威事实={PiFacts}（Stage 定位明细行={StageRows}，覆盖 PI={StagePi}）",
            inventoryRows.Count(), piFacts.Count,
            rawFacts.Count, piPositions.Count, shipments.Count,
            piFacts.Count, wipStageRows.Count,
            wipStageRows.Select(r => r.ProductionInstructionNo).Distinct().Count());

        // 跨 Domain Quantity-Time（§8/D12）：注入上游域生产输出为分段虚拟供给
        await LoadUpstreamDomainSupplyAsync(pool, request, ct);

        Mark("⑦上游域虚拟供给(跨Domain)");

        await LoadActiveLockDataAsync(pool, ct);
        Mark("⑧活跃锁数据");

        // 诊断（2026-09-18 BOM 无记忆化缺陷期间）：连续事实在 BOM 回路【之前】就已算完，这里直接打 DerivedQty>0 与 ΣE，
        // 不必等随后的 BOM 展开（专项 ① 落地后可删）。
        var ctxAll = piPositions.Values
            .SelectMany(p => p.ExistingExecutionContexts ?? Array.Empty<ExistingExecutionContextDto>())
            .ToList();
        _logger.LogInformation(
            "[Pegging][Continuity] 事实摘要(回路前): PI Position={PiPos}, 上下文总数={Ctx}, DerivedQty>0={Active}, ΣE={E}",
            piPositions.Count, ctxAll.Count,
            ctxAll.Count(c => c.DerivedRemainingQty > 0m),
            ctxAll.Sum(c => c.DerivedRemainingQty));

        // 数量根：PI 号 → ERP 权威剩余（用户 2026-10-09 定盘：PI总剩余 = max(Quantity−ReceivedQty,0)，
        // **与 MES 无关**）。同一 PI 多行取「剩余最大」——与 LoadPiRemainingFactsAsync 同口径，不静默求和。
        var piRemainingByPi = piFacts
            .Where(f => !string.IsNullOrEmpty(f.ProductionInstructionNo) && f.PiRemainingQty > 0m)
            .GroupBy(f => f.ProductionInstructionNo, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Max(f => f.PiRemainingQty), StringComparer.Ordinal);

        return (pool, new ContinuityFacts(wipStageRows, piPositions, piRemainingByPi, opCodeResolver, piFacts));
    }

    /// <summary>
    /// 跨版本连续性分桶（PM 0914 红线）：对 Pegging 后已形成的 LogicalProductionDemand，按 IN_PROGRESS 既存执行
    /// 切 Continuation/Free 两片一起进 1号位。Q 不因 WIP 供给减小；被覆盖份额以 Continuation Slice（IsContinuation=true）
    /// ＋自由份额 Free Slice（IsContinuation=false）提交，1号位必须同时看到 各工单 Continuation 与 Free。
    /// 连续份额 = 5号位 ExistingExecutionContexts：每 IN_PROGRESS MES 工单一条，绝不合并（§8/§9，P0 红线）。
    /// </summary>
    private (List<LogicalProductionDemand> Demands, List<ContinuityOverCommitEvent> OverCommits) ApplyContinuityBucketing(
        List<LogicalProductionDemand> demands,
        ContinuityFacts facts)
    {
        // PI → 逐工单既存执行上下文（5号位 ExistingExecutionContexts；每 IN_PROGRESS 工单一条）
        var contextsByPi = new Dictionary<string, IReadOnlyList<ExistingExecutionContextDto>>(StringComparer.Ordinal);
        foreach (var (pi, pos) in facts.PiPositions)
        {
            if (pos.ExistingExecutionContexts is { Count: > 0 } list)
                contextsByPi[pi] = list;
        }

        // 诊断：分桶输入规模（无既存执行上下文 = 无 IN_PROGRESS 工单进入，连续份额切不出）
        _logger.LogInformation(
            "[Pegging][Continuity] 分桶输入: PI Position数={PiPos}, 既存执行上下文总数={Ctx}, 有上下文的PI={PiCtx}",
            facts.PiPositions.Count,
            facts.PiPositions.Values.Sum(p => p.ExistingExecutionContexts?.Count ?? 0),
            contextsByPi.Count);

        if (contextsByPi.Count == 0)
        {
            _logger.LogWarning("[Pegging][Continuity] 无 IN_PROGRESS 既存执行上下文，不切分（Continuation/Free 未产生）");
            return (demands, new List<ContinuityOverCommitEvent>());
        }

        // 诊断：上下文的派生剩余量 + 需求侧命中情况（定位切不出 Continuation 的根因）
        var ctxList = facts.PiPositions.Values
            .SelectMany(p => p.ExistingExecutionContexts ?? Array.Empty<ExistingExecutionContextDto>())
            .ToList();
        var demandsWithPi = demands.Count(d => !string.IsNullOrEmpty(d.ProductionInstructionNo));
        var demandsMatching = demands.Count(d => !string.IsNullOrEmpty(d.ProductionInstructionNo) && contextsByPi.ContainsKey(d.ProductionInstructionNo));
        _logger.LogInformation(
            "[Pegging][Continuity] 明细: 上下文总数={Tot}, DerivedQty>0={Active}, ΣE={E}; 需求总数={Dem}, 带PI={WithPi}, 命中上下文PI={Match}",
            ctxList.Count, ctxList.Count(c => c.DerivedRemainingQty > 0m), ctxList.Sum(c => c.DerivedRemainingQty),
            demands.Count, demandsWithPi, demandsMatching);

        // 数量根可用性：MES 位置量全 0 时，分桶靠这张表兜底（见 BucketContinuityShares §9.4 分支）
        _logger.LogInformation(
            "[Pegging][Continuity] 数量根(ERP PI权威剩余): 可根PI={RootPi}个 ΣP={SumP}; 其中「有上下文但位置量全0」={NoPos}个",
            facts.PiRemainingByPi.Count, facts.PiRemainingByPi.Values.Sum(),
            contextsByPi.Keys.Count(pi =>
                contextsByPi[pi].All(c => c.DerivedRemainingQty <= 0m) &&
                facts.PiRemainingByPi.ContainsKey(pi)));

        var overCommits = new List<ContinuityOverCommitEvent>();
        var result = BucketContinuityShares(demands, contextsByPi, overCommits.Add, facts.PiRemainingByPi);

        // ── PI续排起点接入（2026-09-24）：Free 增产从 5号位 NextOperationContext 开始，不复从首工序 ──
        // 5号位 CalculateNextOperationContexts（r13369）产出各 PI 的 NextOperation/StartOperationCode，
        // 2号位 此前白拿未消费（Free 恒 startOperationCode=null→从头开始）。
        // 现改为：Free 若无 MES 工单覆盖，从 5号位 给的 StartOperationCode 起（跳过已完成工序）。
        var nextOpsByPi = facts.PiPositions
            .Where(kv => kv.Value.NextOperationContexts is { Count: > 0 })
            .ToDictionary(
                kv => kv.Key,
                kv => kv.Value.NextOperationContexts!
                    .Where(c => !string.IsNullOrEmpty(c.StartOperationCode))
                    .Select(c => (StartOperationCode: c.StartOperationCode!, StartStageCode: c.StartStageCode))
                    .FirstOrDefault(),
                StringComparer.Ordinal);

        _logger.LogInformation(
            "[Pegging][Continuity] 5号位 NextOperation 起点可用性: PI Position={PiPos}个, 其中有可用起点={NextOpPi}个",
            facts.PiPositions.Count, nextOpsByPi.Count);

        // ── 名→码 反查（2026-10-10，5号位 §三/§六.3）：把 5号位 出口可能带的 MES 工序名换成 APS OperationCode ──
        // 上一行 BucketContinuityShares 把 5号位 的原始出口值逐字搬进片。1号位 入口按
        // RoutingOperation.OperationCode 匹配 ⇒ 装的是名则非空也必然 S26。此处逐片反查：
        // 命中→换成码；不中→置 null（Fail Closed，不猜码/不伪造/不回填 DEFAULT/1，5号位 §六.3）。
        // 注：Continuation 片的源 <c>ExistingExecutionSliceDto</c> 在 5号位 取 A（2026-10-10）后**可能是名**
        // （中间态 `StartOperationName`，见 <c>BucketContinuityShares</c> 的起点取值），另有 <c>NextOperationContextDto</c>
        // 本就装名（见 PiOperationCodeResolver 注释）⇒ 本循环是**实路径**，不再是防御性。
        // Continuation 片可用「该工单真实 RouteCode」消解同名多码（子串包含，5号位 §三）；Free 无工单路由上下文。
        var ctxRouteByPiWo = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (piKey, ctxs) in contextsByPi)
            foreach (var c in ctxs)
                if (!string.IsNullOrEmpty(c.RouteCode))
                    ctxRouteByPiWo[piKey + "|" + c.MESWorkOrderNo] = c.RouteCode;

        var resolver = facts.OperationCodeResolver;
        var alreadyCode = 0;
        var resolvedFromName = 0;
        var unresolvedSlices = 0;
        var unresolvedByStage = new Dictionary<string, int>(StringComparer.Ordinal);
        var unresolvedSamples = new List<string>();
        foreach (var r in result)
        {
            if (string.IsNullOrEmpty(r.StartOperationCode)) continue;
            var pi = r.ProductionInstructionNo ?? string.Empty;

            if (resolver.IsKnownCode(pi, r.StartOperationCode)) { alreadyCode++; continue; }

            string? route = null;
            var wo = ExtractMesWorkOrderNo(r.LogicalDemandKey);
            if (wo != null && ctxRouteByPiWo.TryGetValue(pi + "|" + wo, out var rc)) route = rc;

            var resolved = resolver.Resolve(pi, r.StartOperationCode, route);
            if (resolved != null) { r.StartOperationCode = resolved; resolvedFromName++; continue; }

            unresolvedSlices++;
            var stageOfSlice = string.IsNullOrEmpty(r.StartStageCode) ? "(无Stage)" : r.StartStageCode;
            unresolvedByStage[stageOfSlice] = unresolvedByStage.TryGetValue(stageOfSlice, out var n) ? n + 1 : 1;
            if (unresolvedSamples.Count < 10) unresolvedSamples.Add(r.StartOperationCode);
            r.StartOperationCode = null;   // Fail Closed：宁可到 Stage 级 / 由 1号位 记 Unscheduled，也不给名校
        }

        if (alreadyCode + resolvedFromName + unresolvedSlices > 0)
            _logger.LogInformation(
                "[Pegging][Continuity] 续排起点名→码 反查：带起点片 {Tot}（已是码 {Already} / 名反查命中 {Resolved} / **反查不中→留null {Unresolved}**）",
                alreadyCode + resolvedFromName + unresolvedSlices, alreadyCode, resolvedFromName, unresolvedSlices);
        if (unresolvedSlices > 0)
            _logger.LogWarning(
                "[Pegging][Continuity] 续排起点反查不中按 Stage：{ByStage}；样例=[{Samples}]（留 null 交 1号位 Fail Closed，不猜码）",
                string.Join("/", unresolvedByStage.OrderByDescending(k => k.Value).Select(k => k.Key + "=" + k.Value)),
                string.Join(",", unresolvedSamples.Distinct()));

        var nextOpResolved = 0;
        var nextOpUnresolved = 0;
        foreach (var r in result)
        {
            // 已带起点的片不动（MES 工单上下文已给出 StartOperationCode）；
            // 其余 —— Free，以及 §9.4 兜底切出的「MES 位置事实缺失」Continuation —— 退回 5号位 NextOperation 起点，
            // 跳过 MES 已报工工序、不复从首工序。PI 级上下文无工单 RouteCode ⇒ 反查只按「唯一名→码」。
            if (r.StartOperationCode != null) continue;
            var pi = r.ProductionInstructionNo;
            if (pi is null or "") continue;
            if (!nextOpsByPi.TryGetValue(pi, out var nextOp) || string.IsNullOrEmpty(nextOp.StartOperationCode)) continue;

            var resolvedNextOp = resolver.Resolve(pi, nextOp.StartOperationCode, workOrderRouteCode: null);
            if (resolvedNextOp != null) { r.StartOperationCode = resolvedNextOp; nextOpResolved++; }
            else nextOpUnresolved++;

            // Free 沿用原行为（以 NextOperation 的 Stage 为准）；Continuation 的 Stage 来自 MES 工单上下文（位置事实），仅在空时补
            if (!string.IsNullOrEmpty(nextOp.StartStageCode)
                && (!r.IsContinuation || string.IsNullOrEmpty(r.StartStageCode)))
                r.StartStageCode = nextOp.StartStageCode;
        }

        if (nextOpResolved + nextOpUnresolved > 0)
            _logger.LogInformation(
                "[Pegging][Continuity] NextOperation 起点回填名→码 反查：命中 {Ok} 片 / **不中留 null {Bad} 片**（§9.4 兜底 Continuation + Free）",
                nextOpResolved, nextOpUnresolved);

        foreach (var evt in overCommits)
            _logger.LogWarning("[Pegging][Continuity]{Kind} {Msg}", evt.Kind, evt.Message);

        // 红线验收日志：正常 E≤Q 切分路径此前静默，补一条结构化计数，跑完可直接核「同 PI 下 Continuation + Free 同现」。
        var contSlices = result.Where(d => d.IsContinuation).ToList();
        var freeSlices = result.Where(d => !d.IsContinuation && d.LogicalDemandKey.EndsWith("/FREE", StringComparison.Ordinal)).ToList();
        if (contSlices.Count > 0)
        {
            _logger.LogInformation(
                "[Pegging][Continuity] 分桶完成: Continuation={ContCount}片 ΣE={E}, Free={FreeCount}片 ΣQ={FreeQty}, 涉及PI={PiCount}个",
                contSlices.Count, contSlices.Sum(d => d.NetOutputQty),
                freeSlices.Count, freeSlices.Sum(d => d.NetOutputQty),
                contSlices.Select(d => d.ProductionInstructionNo).Distinct().Count());

            // ── A/B 输入完整性显式告警（不静默）──
            // 1号位 `PhaseTwoInitialScheduler` 对 `IsContinuation` 需求硬 Fail Closed：
            //   ContinuationKey / RouteCode / PathId / StartOperationCode / NoSplitMerge 缺任一
            //   ⇒ 记 `UnscheduledDemandKeys` 且**不产任何 Task** —— 从 2号位 视角表现为「A/B 数量凭空消失」。
            //   ContinuationKey / NoSplitMerge 由 `CloneDemandSlice` 补齐；RouteCode/PathId 由
            //   `CloneDemandSlice` 从**该片所属 MES 工单**的 `ExistingExecutionContextDto` 真值带入
            //   （分桶本就逐工单切，故「各片走不同 Path」这件事已经被工单身份隔开，不需要 slice 级新字段）。
            //   仍缺 = 5号位 该工单 RouteCode 为空或 PathId 未唯一命中（其侧登记 AB_PATH_ID_* Issue）。
            //   故此处把缺口**显式打到日志**，便于对账而非静默丢单。
            var noPath = contSlices.Count(d => string.IsNullOrEmpty(d.RouteCode) || d.PathId is null);
            if (noPath > 0)
                _logger.LogWarning(
                    "[Pegging][Continuity] A/B 输入不完整：{NoPath}/{Total} 片缺真实 RouteCode/PathId ⇒ " +
                    "1号位 将按 Fail Closed 记 Unscheduled 且不产 Task。缺的是 5号位 工单级真值未交付（见其 AB_PATH_ID_* Issue）。涉及PI={PiCount}个",
                    noPath, contSlices.Count,
                    contSlices.Select(d => d.ProductionInstructionNo).Distinct().Count());
        }

        return (result, overCommits);
    }

    /// <summary>
    /// 连续份额分桶纯函数（可单测）。逐 MES 工单独立产出 Continuation（§8，P0 红线：绝不合并多工单）。
    /// 规则：
    ///   无 PI / 无 IN_PROGRESS 工单 → 原样透传（自由需求，不切）；
    ///   Q≤0 且仍有开工中剩余 → 不产 Demand/Task + 登记 Demand Mismatch（§9.2）；
    ///   每工单 → Continuation = min(DerivedRemainingQty, 剩余额度)，**ΣE 封顶 = min(Q, PI权威剩余)**；
    ///   MES 位置量缺失（DerivedRemainingQty 全 ≤0）但 PI 有权威剩余 → §9.4 保守兜底：总量取 PI 剩余、
    ///     位置退回 5号位 上下文，量在工单间均分（与 5号位 AllocateStageRemaining 的 totalPlannedQty=0 兜底同惯例）；
    ///   Free = max(Q − ΣE, 0)，**再受 PI 权威剩余的累计额度约束**（F-06 / R-21：同 PI 的各工单连续份额与
    ///     各处新增自由批合计不得越过 PI 剩余；截下的部分登记 PI_FREE_OVER_COMMIT，不静默丢）；
    ///   E&gt;Q 不砍单、登记 Execution Over-Commit（§9.1）。
    /// Slice 键（LogicalDemandKey）= 源 LogicalDemandKey + "/WO:" + MESWorkOrderNo；Free = 源 key + "/FREE"；
    /// **ContinuationKey 与 Slice 键是两回事**（红线 Q3）：ContinuationKey = f(ScheduleRun, MESWorkOrderNo)，
    ///   同一 MES 工单的全部 Slice **共享同一个** ContinuationKey，**不得把 LogicalDemandKey 拼进去**（否则同一工单裂分）。
    /// 共用源 AllocationSequence/DemandKey（P3.2 不新建 Allocation）。
    /// PlannedProcessQty 逐片按材料级良率单位投入比（源 PlannedProcessQty/NetOutputQty）反算（§16）。
    /// </summary>
    /// <param name="piRemainingByPi">
    /// PI 号 → ERP 权威剩余（用户 2026-10-09 定盘口径）。传 null = 退回旧行为（不封顶、不兜底、
    /// 不做同 PI 累计扣减），便于既有单测与「无权威剩余来源」的调用方保持不变。
    /// </param>
    internal static List<LogicalProductionDemand> BucketContinuityShares(
        List<LogicalProductionDemand> demands,
        IReadOnlyDictionary<string, IReadOnlyList<ExistingExecutionContextDto>> contextsByPi,
        System.Action<ContinuityOverCommitEvent>? onOverCommit = null,
        IReadOnlyDictionary<string, decimal>? piRemainingByPi = null)
    {
        var result = new List<LogicalProductionDemand>(demands.Count + 8);

        // F-04 / PM T2-06.2 红线：**一个 MES 工单的既存执行份额，在本 Run 内只消费一次**。
        // 多个 Demand 合法共用同一真实 PI 时，MES 工单份额只有**一份物理量**；不得让每条 Demand
        // 都完整复制同一份 E（PM C-Q4：不按 Q 比例猜分、不平均切、不放大需求）。
        // 键 = PI 号 | MESWorkOrderNo；值 = 本 Run 已消费掉的份额。
        var consumedByWo = new Dictionary<string, decimal>(StringComparer.Ordinal);

        // F-06 / PM T2-06.6（R-21）：**PI 权威剩余的累计账本**（本 Run 内）。
        // E 侧已有 consumedByWo 去重，**Free 侧此前是空的** ⇒ 同 PI 多需求时 Σ(Continuation+Free) 可越过 PI 剩余。
        // 同一 PI 的「各工单连续份额 + 各处新增自由批」合计不得突破 PI 剩余（R-21：300+200 合法 400 不许累计 500）。
        // 超出的部分**登记**、不静默丢（PM T2-06.5「不得只靠 Math.Min 悄悄丢掉未满足部分」/ §九.6）。
        // 键 = PI 号；值 = 本 Run 已产出的 (Continuation + Free) 累计量。
        var emittedByPi = new Dictionary<string, decimal>(StringComparer.Ordinal);

        foreach (var demand in demands)
        {
            var pi = demand.ProductionInstructionNo;
            if (string.IsNullOrEmpty(pi))
            {
                result.Add(demand);
                continue;
            }

            if (!contextsByPi.TryGetValue(pi, out var contexts) || contexts.Count == 0)
            {
                result.Add(demand);
                continue;
            }

            var q = demand.NetOutputQty;

            // ── 数量根（用户 2026-10-09 定盘）──────────────────────────────────────────────
            // PI总剩余 = max(Quantity − ReceivedQty, 0)，**与 MES 无关**，是全链唯一总量；
            // 大工艺(Stage) 与 MES 各工序只回答「剩余现在在哪」，**任何一层的量都不得突破 PI 剩余**。
            // 上限同时受 Q 约束（§4.2A：连续份额仍属于当前 Q，不得在 Q 之外额外叠加）。
            var pRoot = piRemainingByPi is not null
                        && piRemainingByPi.TryGetValue(pi, out var pv)
                        && pv > 0m
                ? pv
                : (decimal?)null;
            // ── F-06（R-21）：额度不再恒用 pRoot 原值，而是扣掉**本 Run 已为该 PI 产出**的部分 ──
            // 无根（调用方未提供 piRemainingByPi）→ budgetLeft 取 MaxValue ⇒ 逐字旧行为，既有单测不受影响。
            emittedByPi.TryGetValue(pi, out var emittedForPi);
            var budgetLeft = pRoot.HasValue ? Math.Max(pRoot.Value - emittedForPi, 0m) : decimal.MaxValue;
            var cap = pRoot.HasValue ? Math.Min(q, budgetLeft) : q;

            // 逐工单申报量：正常路径取 5号位 DerivedRemainingQty；位置量全 0 时走 §9.4 兜底
            var active = contexts.Where(c => c.DerivedRemainingQty > 0m).ToList();
            var shareQty = new List<(ExistingExecutionContextDto Ctx, decimal Qty)>(active.Count);
            if (active.Count > 0)
            {
                foreach (var c in active) shareQty.Add((c, c.DerivedRemainingQty));
            }
            else if (pRoot.HasValue)
            {
                // ── MES 位置事实缺失的保守兜底（§2.2 / §9.4）────────────────────────────────
                // 5号位 的 Stage 位置事实取自 StageProgressSnapshot，实测该快照**未覆盖 5,706/35,278 个
                // 在制 PI**（PV2 样本 284/284 全落在缺口里）⇒ stageFact.RemainingQty=0 ⇒ DerivedRemainingQty
                // 恒 0 ⇒ 连续份额切不出。按用户口径，总量仍以 ERP PI 剩余为准，位置退回 5号位 上下文
                // （起点由调用方按 NextOperationContext 回填）；无位置量时按工单**均分**——与 5号位
                // AllocateStageRemaining 的 totalPlannedQty=0 兜底同惯例。
                var perWo = Math.Round(cap / contexts.Count, 4);
                if (perWo > 0m)
                    foreach (var c in contexts) shareQty.Add((c, perWo));
            }

            // ── F-04 / PM T2-06.2：同一 MES 工单份额在本 Run 内**只消费一次** ────────────────
            // 多个 Demand 合法共用同一真实 PI 时，MES 工单只有**一份物理可承接量**；先到先得，
            // 后来者不得再复制同一份 E（PM C-Q4：不按 Q 比例猜分、不平均切、不放大需求）。
            // 被别的 Demand 抢先消费掉的部分 → 登记 DUPLICATE_WO_SHARE（保守保留 Issue，不静默）。
            for (var i = 0; i < shareQty.Count; i++)
            {
                var (ctxI, qtyI) = shareQty[i];
                consumedByWo.TryGetValue(pi + "|" + ctxI.MESWorkOrderNo, out var already);
                var avail = qtyI - already;
                if (avail <= 0m)
                {
                    onOverCommit?.Invoke(new ContinuityOverCommitEvent(
                        pi, demand.OrderId, demand.MaterialId, demand.DomainKey, demand.AllocationSequence,
                        demand.StartStageCode, demand.LogicalDemandKey, qtyI, already, qtyI, "DUPLICATE_WO_SHARE"));
                    shareQty[i] = (ctxI, 0m);
                }
                else if (avail < qtyI)
                {
                    shareQty[i] = (ctxI, avail);
                }
            }
            shareQty.RemoveAll(x => x.Qty <= 0m);

            if (shareQty.Count == 0)
            {
                // 无权威根 → 逐字旧行为（原样透传，不记账）
                if (!pRoot.HasValue)
                {
                    result.Add(demand);
                    continue;
                }

                // 有权威根：本需求无可认领的既存执行份额（该 PI 无活跃 MES 工单，或额度已被前面的需求用尽）。
                // 整条走自由侧，但**仍受 PI 权威剩余累计约束**：额度够 → 原样透传（逐字旧行为）；不够 → 截到额度 + 登记。
                var grantQty = Math.Min(q, budgetLeft);
                if (grantQty >= q)
                {
                    emittedByPi[pi] = emittedForPi + q;
                    result.Add(demand);
                    continue;
                }

                onOverCommit?.Invoke(new ContinuityOverCommitEvent(
                    pi, demand.OrderId, demand.MaterialId, demand.DomainKey, demand.AllocationSequence,
                    demand.StartStageCode, demand.LogicalDemandKey, q, 0m, q - grantQty, "PI_FREE_OVER_COMMIT"));

                if (grantQty <= 0m) continue;   // 额度已用尽：不产需求（已登记，不静默丢）

                // 额度不够整条：同键克隆并按额度截小（PlannedProcessQty 同比例反算）；已登记未满足部分
                var scaledProcess = q > 0m ? Math.Round(demand.PlannedProcessQty * (grantQty / q), 4) : demand.PlannedProcessQty;
                emittedByPi[pi] = emittedForPi + grantQty;
                result.Add(CloneDemandRescaled(demand, grantQty, scaledProcess));
                continue;
            }

            // §9.2：Q=0 但仍有开工中剩余 → 不产正常 Demand/Task，登记 Demand Mismatch
            if (q <= 0m)
            {
                var leftover = shareQty.Sum(x => x.Qty);
                onOverCommit?.Invoke(new ContinuityOverCommitEvent(
                    pi, demand.OrderId, demand.MaterialId, demand.DomainKey, demand.AllocationSequence,
                    demand.StartStageCode, demand.LogicalDemandKey, q, leftover, leftover, "DEMAND_MISMATCH"));
                continue;
            }

            // §16：材料级良率单位投入比（PlannedProcessQty / NetOutputQty），逐片反算 PlannedProcessQty
            var processRatio = demand.PlannedProcessQty > 0m && q > 0m
                ? demand.PlannedProcessQty / q
                : 1m;

            // §8：逐工单各出一条 Continuation，绝不合并。
            // 有权威根（PI剩余）→ ΣE 封顶 = min(Q, PI剩余)；无根（调用方未提供）→ 保持旧行为，不封顶、只登记。
            var rawSumE = shareQty.Sum(x => x.Qty);
            var sumE = 0m;
            foreach (var (ctx, rawQty) in shareQty)
            {
                var ci = pRoot.HasValue
                    ? (rawQty <= cap - sumE ? rawQty : cap - sumE)   // 逐工单不得越封顶
                    : rawQty;                                        // 无根 → 逐字旧行为
                if (ci <= 0m) continue;
                sumE += ci;

                // F-04：登记本 Run 已消费的该 MES 工单份额（后续同 PI 的 Demand 只能拿剩余）
                var woKey = pi + "|" + ctx.MESWorkOrderNo;
                consumedByWo.TryGetValue(woKey, out var prevConsumed);
                consumedByWo[woKey] = prevConsumed + ci;

                var key = demand.LogicalDemandKey + "/WO:" + ctx.MESWorkOrderNo;
                // 起点值：优先真码；无码时退回 5号位 新出口的**工序名**（取 A，2026-10-10）。
                // 中间态（前道完工/后道未开工，占 82%）5号位 只吐名不吐码，此前本处直接取 null ⇒
                // 名从未进入片 ⇒ 下游「名→码 反查」（见 ApplyContinuityBucketing 的 resolver 循环）拿不到输入，
                // 232 片恒缺 StartOperationCode ⇒ 1号位 硬 Fail Closed 不产 Task。
                // 此处放进字段的可能是「名」，但下游循环无条件过一遍 resolver：命中换真码、不中置 null
                // ⇒ **载荷里绝不会留下名**，与原「宁可 null 不给名校」口径不冲突。
                var startOp = !string.IsNullOrEmpty(ctx.StartOperationCode)
                    ? ctx.StartOperationCode
                    : (string.IsNullOrEmpty(ctx.StartOperationName) ? null : ctx.StartOperationName);
                var startStage = !string.IsNullOrEmpty(ctx.StartStageCode) ? ctx.StartStageCode : demand.StartStageCode;

                result.Add(CloneDemandSlice(demand, key, ci, Math.Round(ci * processRatio, 4),
                    isContinuation: true, startStage, startOp,
                    continuationKey: ContinuationKeyOf(demand.PlanVersionId, ctx.MESWorkOrderNo),
                    // 逐工单取该工单上下文里的真实路径身份（5号位 按工单唯一交付）。
                    routeCode: ctx.RouteCode,
                    pathId: ctx.PathId));
            }

            // ── Free 侧：F-06 —— 先受 PI 权威剩余**累计**约束，再把未满足部分登记（不静默）──
            // desiredFree = q − ΣE（本需求自由侧的本意量）；freeQty = 再扣掉同 PI 已被其它需求/批次占用的额度后真正合法的新增量。
            var desiredFree = q - sumE;
            var freeQty = pRoot.HasValue
                ? Math.Max(Math.Min(desiredFree, budgetLeft - sumE), 0m)
                : desiredFree;

            if (freeQty > 0m)
            {
                // Free 是新生产量：起点从路由首工序起（StartOperation=null，StartStage 由 FillStartStageCodes 回填）
                result.Add(CloneDemandSlice(demand, demand.LogicalDemandKey + "/FREE", freeQty,
                    Math.Round(freeQty * processRatio, 4),
                    isContinuation: false, demand.StartStageCode, startOperationCode: null));
            }
            else if (freeQty < 0m && !pRoot.HasValue)
            {
                // 旧行为（无权威根）：E>Q 执行超量 —— 不砍单、不产新自由 Task，登记 Execution Over-Commit
                onOverCommit?.Invoke(new ContinuityOverCommitEvent(
                    pi, demand.OrderId, demand.MaterialId, demand.DomainKey, demand.AllocationSequence,
                    demand.StartStageCode, demand.LogicalDemandKey, q, sumE, -freeQty, "EXECUTION_OVER_COMMIT"));
            }

            // 本 Run 该 PI 的产出累计（Continuation + Free），供后续同 PI 需求扣减（R-21）
            if (pRoot.HasValue)
                emittedByPi[pi] = emittedForPi + sumE + freeQty;

            // F-06 / R-21：自由侧被 PI 权威剩余累计截下的部分 —— 登记，绝不静默丢
            if (pRoot.HasValue && desiredFree > freeQty)
            {
                onOverCommit?.Invoke(new ContinuityOverCommitEvent(
                    pi, demand.OrderId, demand.MaterialId, demand.DomainKey, demand.AllocationSequence,
                    demand.StartStageCode, demand.LogicalDemandKey, q, sumE, desiredFree - freeQty, "PI_FREE_OVER_COMMIT"));
            }

            // §9.1 红线（有权威根时）：申报量 > 封顶（= min(Q, PI权威剩余)）——不砍单、登记，绝不静默
            if (pRoot.HasValue && rawSumE > cap)
            {
                onOverCommit?.Invoke(new ContinuityOverCommitEvent(
                    pi, demand.OrderId, demand.MaterialId, demand.DomainKey, demand.AllocationSequence,
                    demand.StartStageCode, demand.LogicalDemandKey, cap, rawSumE, rawSumE - cap, "EXECUTION_OVER_COMMIT"));
            }
        }

        return result;
    }

    /// <summary>
    /// 同键克隆 + 按 PI 权威剩余额度**截小**数量（F-06 用）。与 <see cref="CloneDemandSlice"/> 的区别：
    /// 不切分（键不变）、不改身份字段、不重算起点 —— 只是把需求量收敛到合法额度内。
    /// </summary>
    private static LogicalProductionDemand CloneDemandRescaled(
        LogicalProductionDemand s, decimal netQty, decimal processQty) => new()
    {
        LogicalDemandKey        = s.LogicalDemandKey,
        PlanVersionId           = s.PlanVersionId,
        DomainKey               = s.DomainKey,
        AllocationSequence      = s.AllocationSequence,
        DemandKey               = s.DemandKey,
        OrderId                 = s.OrderId,
        // ── 需求锚点与角色（J14-01/02）：切片**原样继承**，否则分桶后 Stage 装载丢失请求维度 ──
        IsRootDemand            = s.IsRootDemand,
        BomNo                   = s.BomNo,
        RequestDetailId         = s.RequestDetailId,
        RepWorksetId            = s.RepWorksetId,
        MaterialId              = s.MaterialId,
        FactoryId               = s.FactoryId,
        StartStageCode          = s.StartStageCode,
        RequiredStageCode       = s.RequiredStageCode,
        StartOperationCode      = s.StartOperationCode,
        NetOutputQty            = netQty,
        PlannedProcessQty       = processQty,
        UOM                     = s.UOM,
        RequiredAvailableTime   = s.RequiredAvailableTime,
        DemandSequence          = s.DemandSequence,
        ProductionInstructionNo = s.ProductionInstructionNo,
        IsUnlocated             = s.IsUnlocated,
        PreferredResourceId     = s.PreferredResourceId,
        FallbackResourceId      = s.FallbackResourceId,
        IsContinuation          = s.IsContinuation,
        ContinuationKey         = s.ContinuationKey,
        RouteCode               = s.RouteCode,
        PathId                  = s.PathId,
        NoSplitMerge            = s.NoSplitMerge,
        PreferredResourceCode   = s.PreferredResourceCode,
        DemandGoal              = s.DemandGoal,
        // QuantityAuthority（2026-10-22）：数量权威随切片血缘原样继承（分桶只改 Q，不改「谁授权数量」）。
        QuantityAuthority       = s.QuantityAuthority,
    };

    private static LogicalProductionDemand CloneDemandSlice(
        LogicalProductionDemand s,
        string logicalDemandKey,
        decimal netQty,
        decimal processQty,
        bool isContinuation,
        string? startStageCode,
        string? startOperationCode,
        string? continuationKey = null,
        string? routeCode = null,
        int? pathId = null) => new()
    {
        LogicalDemandKey       = logicalDemandKey,
        PlanVersionId          = s.PlanVersionId,
        DomainKey              = s.DomainKey,
        AllocationSequence     = s.AllocationSequence,
        DemandKey              = s.DemandKey,
        OrderId                = s.OrderId,
        // ── 需求锚点与角色（J14-01/02）：切片**原样继承**，否则分桶后 Stage 装载丢失请求维度 ──
        IsRootDemand           = s.IsRootDemand,
        BomNo                  = s.BomNo,
        RequestDetailId        = s.RequestDetailId,
        RepWorksetId           = s.RepWorksetId,
        MaterialId             = s.MaterialId,
        FactoryId              = s.FactoryId,
        StartStageCode         = string.IsNullOrEmpty(startStageCode) ? s.StartStageCode : startStageCode,
        StartOperationCode     = startOperationCode,
        NetOutputQty           = netQty,
        PlannedProcessQty      = processQty,
        UOM                    = s.UOM,
        RequiredAvailableTime  = s.RequiredAvailableTime,
        DemandSequence         = s.DemandSequence,
        ProductionInstructionNo = s.ProductionInstructionNo,
        IsUnlocated            = s.IsUnlocated,
        PreferredResourceId    = s.PreferredResourceId,
        FallbackResourceId     = s.FallbackResourceId,
        IsContinuation         = isContinuation,
        // ── v1.6 §1 + 0号位 2026-10-07 裁决：A/B 切片必须**显式**带齐身份字段，不靠默认值 ──
        // 1号位 `PhaseTwoInitialScheduler` 对 `IsContinuation` 需求做**硬 Fail Closed**：
        //   ContinuationKey / RouteCode / PathId / StartOperationCode / NoSplitMerge 缺任一 ⇒
        //   进 `UnscheduledDemandKeys`、**不产任何 Task**（静默丢单）。
        //   ⇒ 本方法逐项显式赋值；缺项必须在此处可见，不得依赖「反正有默认值」。
        ContinuationKey        = continuationKey,
        // A/B 路径身份取自「该切片所属 MES 工单」的 5号位 上下文真值
        // （`ExistingExecutionContextDto.RouteCode` / `.PathId`，按工单唯一交付）。
        // 缺值保持 null —— 不填 `DEFAULT / 1`、不按「物料唯一 Path」猜（0号位 2026-10-07 §三、
        // 2026-10-22 §五(一)），由 1号位 硬闸 Fail Closed。
        RouteCode              = string.IsNullOrEmpty(routeCode) ? s.RouteCode : routeCode,
        PathId                 = pathId ?? s.PathId,
        NoSplitMerge           = isContinuation,
        PreferredResourceCode  = s.PreferredResourceCode,
        // DemandGoal（2026-10-12 载体落地）：需求级业务目标，切片**原样继承**（同一切片血缘不变）。
        DemandGoal             = s.DemandGoal,
        // QuantityAuthority（2026-10-22）：数量权威同样原样继承 —— 分桶只切 Q，不改「谁授权数量」。
        QuantityAuthority      = s.QuantityAuthority,
    };

    /// <summary>
    /// 连续份额身份键（红线 Q3：一个 ScheduleRun 内，一个 MESWorkOrderNo 有且仅有一个 ContinuationKey）。
    ///
    /// 【口径】`CK-{PlanVersionId}-{MESWorkOrderNo}`
    ///   · 生成权属 **2号位**（1号位 只透明消费 + FinalTask 原样回传，不生成/不解析/不拼接）；
    ///   · 作用域 = 本 ScheduleRun（以 `PlanVersionId` 为运行身份）⇒ **同一工单跨 Run 的 Key 不同**，符合红线「Run 内唯一」；
    ///   · **刻意不含 `LogicalDemandKey`** —— 拼进去会让同一 MES 工单的多个 Slice 各自得到不同的 Key，
    ///     即红线明令禁止的「同一 MES 工单裂分」。
    ///   · 确定性纯函数：同输入必得同值，可复算、可单测。
    /// </summary>
    internal static string ContinuationKeyOf(long planVersionId, string mesWorkOrderNo)
        => $"CK-{planVersionId}-{mesWorkOrderNo}";

    /// <summary>
    /// 连续性分桶异常事件（G6）：E&gt;Q 执行超量 / Q=0 仍开工 两类，登记到 ScheduleExplanationFact（复用不加表）。
    /// </summary>
    internal sealed record ContinuityOverCommitEvent(
        string ProductionInstructionNo,
        long? OrderId,
        int MaterialId,
        string DomainKey,
        long AllocationSequence,
        string StartStageCode,
        string LogicalDemandKey,
        decimal Quantity,
        decimal ContinuationQty,
        decimal ExcessQty,
        string Kind)   // "EXECUTION_OVER_COMMIT" | "DEMAND_MISMATCH" | "DUPLICATE_WO_SHARE" | "PI_FREE_OVER_COMMIT"
    {
        /// <summary>DEMAND_MISMATCH（Q=0 仍开工）=错误级；其余（E&gt;Q 超量 / 同工单份额被重复引用 / 同 PI 自由额度超顶）=警告级（登记不阻断）。</summary>
        /// <remarks>2026-10-12（1号位 §2.1）：Severity 权威值域 = INFO/WARN/ERROR，原 "WARNING" 越域 ⇒ 改 "WARN"。</remarks>
        public string Severity => Kind == "DEMAND_MISMATCH" ? "ERROR" : "WARN";

        public string Message => Kind switch
        {
            "EXECUTION_OVER_COMMIT" =>
                $"PI {ProductionInstructionNo}: E={ContinuationQty} > Q={Quantity}，执行超量 {ExcessQty}（现场执行/数量不一致），不产自由 Task",
            // PM 2026-10-09 T2-06.2（F-04）：同一 MES 工单份额在本 Run 内已被别的需求消费完，
            // 本需求不得再复制同一份 E ⇒ 登记并转 Free（不砍单、不按 Q 比例猜分）。
            "DUPLICATE_WO_SHARE" =>
                $"PI {ProductionInstructionNo}: MES 工单份额 {ExcessQty} 已被本 Run 内其它需求消费（累计 {ContinuationQty}），不再重复计提，转自由份额",
            // PM 2026-10-09 T2-06.6（F-06 / R-21）：同 PI 的新增自由批累计不得突破 PI 权威剩余，
            // 超出部分**登记**（T2-06.5：不得只靠 Math.Min 悄悄丢掉未满足部分 / §九.6）。
            "PI_FREE_OVER_COMMIT" =>
                $"PI {ProductionInstructionNo}: 本需求带 PI（Q={Quantity}, 连续 ΣE={ContinuationQty}），自由侧超出该 PI 权威剩余累计额度 {ExcessQty}，已截小并入 Issue（不静默丢）",
            _ =>
                $"PI {ProductionInstructionNo}: Q=0 但存在开工中剩余 ΣE={ContinuationQty}（Demand Mismatch），不产 Demand/Task"
        };
    }

    /// <summary>
    /// Task 落库阶段旧 TaskNo 反查行（G4）：TaskNo ↔ MESWorkOrderNo 1:1 绑定，来源 = 下发中间表 TaskDispatch（冻结 §7.1）。
    /// </summary>
    private sealed record TaskNoBindingRow(string MESWorkOrderNo, string TaskNo);

    /// <summary>
    /// 从 FinalTaskDraft.SourceDraftId（= LogicalDemandKey，1号位 T08 原样带回）解析 ContinuationKey 携带的 MES 工单号。
    /// 连续份额键 = 源 key + "/WO:" + MESWorkOrderNo（BucketContinuityShares 生成）；自由份额源 key + "/FREE" / 普通需求不含 "/WO:" → 返回 null。
    /// 身份复用 LogicalDemandKey 承载、不另设字段（冻结 §7.2 + LogicalProductionDemand 注释），故落库前从这里反解。
    /// </summary>
    internal static string? ExtractMesWorkOrderNo(string? sourceDraftId)
    {
        if (string.IsNullOrEmpty(sourceDraftId))
            return null;

        const string marker = "/WO:";
        var idx = sourceDraftId.IndexOf(marker, StringComparison.Ordinal);
        return idx >= 0 ? sourceDraftId[(idx + marker.Length)..] : null;
    }

    /// <summary>
    /// TaskNo 三分支（冻结规则清单 v1.5 **T-002 / T-003 / T-004**，红线 Q2）：
    ///
    ///   · **T-002** TaskNo 是「**Stage 内 MES 执行批**」的 APS 跨版本业务身份，**一个 TaskNo 可对应 N 条 Operation Task**；
    ///   · **T-003** 一个 TaskNo 绑定一个 MESWorkOrderNo；**MES 工单不跨 Stage**；
    ///   · **T-004** A 继续旧 TaskNo/原 WO；B Solver 前 TaskNo=NULL、FinalTask 后由 2号位 生成新 TaskNo 绑定原 WO；
    ///              只有 C 新建 MES 工单；
    ///   · **T-005** `ExecutionBatchDraftKey` 是 **Solver 归组键，不是 TaskNo** ⇒ 本方法只拿它当**归组判据**，号另行生成。
    ///
    /// 分支：
    ///   ① **A 桶**（有 MES 工单，且 `TaskDispatch` 反查到旧号）⇒ **继承旧号**（跨版本连续性，不新建）；
    ///   ② **B 桶**（有 MES 工单但无旧号）⇒ 本 Run 内按 **(MESWorkOrderNo, StageCode)** 归组，同批共用一个新号；
    ///   ③ **C 桶**（无 MES 工单）⇒ 本 Run 内按 **(ExecutionBatchDraftKey, StageCode)** 归组，同批共用一个新号。
    ///
    /// 为何 ②③ 都要再叠 `StageCode`：`ExecutionBatchDraftKey` 的键域是 `(需求键, 批序号)`
    /// （`PhaseTwoInitialScheduler.ExecutionBatchKey`）**不含 Stage**，单用它会让一个 TaskNo 跨 Stage ⇒ 违反 T-003。
    ///
    /// ⚠ 旧实现（2026-10-08 前）：非继承分支一律 `PEGG-{pv}-{finalDraftId[..8]}` —— **按 Operation 一条一个号**，
    ///   同一执行批的 N 条工序各拿一个号，直接违反 T-002「一个 TaskNo 可对应 N 条 Operation Task」。
    /// </summary>
    internal static string ResolveTaskNo(
        long planVersionId,
        FinalTaskDraft final,
        string? mesWorkOrderNo,
        IReadOnlyDictionary<string, string> oldTaskNoByWorkOrder,
        IDictionary<string, string> taskNoByBatch)
    {
        // ① A 桶：跨版本继承（T-004「A 继续旧 TaskNo/原 WO」）
        if (mesWorkOrderNo != null && oldTaskNoByWorkOrder.TryGetValue(mesWorkOrderNo, out var oldTaskNo))
            return oldTaskNo;

        // ② / ③ B、C 桶：本 Run 内按「Stage 内 MES 执行批」归组，同批共号（T-002）
        var batchKey = TaskNoBatchKey(mesWorkOrderNo, final.ExecutionBatchDraftKey, final.StageCode);
        if (taskNoByBatch.TryGetValue(batchKey, out var existing))
            return existing;

        var newTaskNo = $"PEGG-{planVersionId}-{ShortHash16(batchKey)}";
        taskNoByBatch[batchKey] = newTaskNo;
        return newTaskNo;
    }

    /// <summary>
    /// TaskNo 归组键 = 「Stage 内 MES 执行批」（T-002 + T-003「MES 工单不跨 Stage」）。
    ///   · B 桶（<paramref name="mesWorkOrderNo"/> 非空）⇒ 以 **MES 工单 + Stage** 为批身份（一个 WO 一个号）；
    ///   · C 桶（为空）⇒ 以 **Solver 归组键 + Stage** 为批身份（此时 MES 工单尚未创建，T-004）。
    /// 两级键都带前缀（`WO|` / `EB|`）以免「工单号恰好等于某批键」时两类撞键。
    /// </summary>
    internal static string TaskNoBatchKey(string? mesWorkOrderNo, string? executionBatchDraftKey, string? stageCode)
        => mesWorkOrderNo is not null
            ? $"WO|{mesWorkOrderNo}|{stageCode}"
            : $"EB|{executionBatchDraftKey}|{stageCode}";

    /// <summary>
    /// 批身份 → TaskNo 后缀的**确定性**短哈希（SHA-256 前 16 个十六进制字符 = 64 bit）。
    ///
    /// 为什么不能用 <c>string.GetHashCode()</c>：.NET Core 起它对 string 每进程随机加盐，
    ///   **同一批键跨运行会得到不同值** ⇒ TaskNo 不可复算，破坏 T-002「跨版本业务身份」。
    /// 为什么取 16 位：10 万批规模下生日碰撞概率 ≈ 3×10⁻¹¹，可忽略；且 `PEGG-{pv}-{16hex}` 远短于列宽 NVARCHAR(50)。
    /// </summary>
    internal static string ShortHash16(string s)
        => Convert.ToHexString(
               System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)),
               0, 8);

    /// <summary>
    /// 跨 Domain Quantity-Time（§8/D12）：下游域启动前，从上游域已落盘 Task 读取 ChildMaterialCode
    /// 的完工时间 + DefaultLeadTimeDays，构造成分段虚拟供给注入本域供给池。
    /// 保持多段（40@15日 + 60@17日），禁止压平——每一条上游 Task 独立 Add 一段。
    /// 工厂口径：Domain_Dependency 承载同厂跨族血缘（FAMILY 域），上游 Task 的 Order.FactoryId 即消耗侧工厂；
    /// 跨厂流动走 CrossFactoryEdge 主链，不经此路径。
    /// Domain_Dependency 为空（V1 现状）时直接跳过，零额外开销。
    /// </summary>
    private async System.Threading.Tasks.Task LoadUpstreamDomainSupplyAsync(
        SupplyPool pool,
        PeggingExecutionRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DomainKey))
            return;

        var runId = request.SchedulingContext?.ScheduleRunId;
        if (!runId.HasValue || runId.Value <= 0)
            return;

        var dependencies = (await _connectionManager.QueryAsync<UpstreamDomainDependencyRow>(
            @"SELECT UpstreamDomainCode, ChildMaterialCode, DefaultLeadTimeDays
              FROM Domain_Dependency
              WHERE DownstreamDomainCode = @DomainKey",
            new { DomainKey = request.DomainKey },
            db: DatabaseId.APS)).ToList();

        if (dependencies.Count == 0)
            return;

        foreach (var dep in dependencies)
        {
            // 定位同一 ScheduleRun 内上游域的 PlanVersion（分域后每 Domain 一个 PlanVersion）
            var upstreamPv = await _connectionManager.QueryFirstOrDefaultAsync<UpstreamPlanVersionRow>(
                @"SELECT TOP 1 Id
                  FROM PlanVersion
                  WHERE SourceScheduleRunId = @RunId AND DomainKey = @UpstreamDomainCode
                  ORDER BY Id DESC",
                new { RunId = runId.Value, UpstreamDomainCode = dep.UpstreamDomainCode },
                db: DatabaseId.APS);

            if (upstreamPv == null)
            {
                _logger.LogWarning(
                    "[Pegging] 上游域 {UpstreamDomain} 无对应 PlanVersion（跨域 Quantity-Time 跳过）",
                    dep.UpstreamDomainCode);
                continue;
            }

            // 读上游已落盘 Task 的完工时间，逐条 Add 为分段虚拟供给（保留多段，不压平）
            var upstreamTasks = (await _connectionManager.QueryAsync<UpstreamTaskSupplyRow>(
                @"SELECT m.MaterialCode,
                         t.MaterialId,
                         o.FactoryId,
                         f.Code          AS FactoryCode,
                         t.Quantity,
                         t.PlannedEndTime
                  FROM [Task] t
                  INNER JOIN Material m ON m.Id = t.MaterialId
                  INNER JOIN [Order]  o ON o.Id = t.OrderId
                  INNER JOIN Factory  f ON f.Id = o.FactoryId
                  WHERE t.PlanVersionId = @UpstreamPlanVersionId
                    AND m.MaterialCode = @ChildMaterialCode
                    AND t.PlannedEndTime IS NOT NULL",
                new { UpstreamPlanVersionId = upstreamPv.Id, ChildMaterialCode = dep.ChildMaterialCode },
                db: DatabaseId.APS)).ToList();

            if (upstreamTasks.Count == 0)
                continue;

            var leadTimeDays = dep.DefaultLeadTimeDays > 0 ? dep.DefaultLeadTimeDays : 0;
            foreach (var t in upstreamTasks)
            {
                pool.Add(
                    materialCode: t.MaterialCode,
                    materialId: t.MaterialId,
                    factoryId: t.FactoryId,
                    qty: t.Quantity,
                    availableAt: t.PlannedEndTime.Value.AddDays(leadTimeDays),
                    sourceType: Core.Enum.SupplySourceType.UPSTREAM_DOMAIN_PRODUCTION,
                    sourceRef: $"UPSTREAM_{upstreamPv.Id}_{t.MaterialId}_{t.PlannedEndTime:yyyyMMddHHmmss}",
                    factoryCode: t.FactoryCode);
            }
        }
    }

    private async System.Threading.Tasks.Task LoadActiveLockDataAsync(SupplyPool pool, CancellationToken ct)
    {
        var allSupplyKeys = pool.GetAllEntries()
            .Select(e => e.SupplyKey)
            .Distinct()
            .ToList();

        if (allSupplyKeys.Count == 0)
        {
            _logger.LogDebug("[Pegging] 供给池为空，跳过 Lock 数据加载");
            return;
        }

        // 批量查询所有供给上的活跃 Lock
        var lockTasks = allSupplyKeys.Select(key => _lockRepo.GetActiveLocksOnSupplyAsync(key, ct));
        var lockResults = await System.Threading.Tasks.Task.WhenAll(lockTasks);
        var allLocks = lockResults.SelectMany(x => x).ToList();

        if (allLocks.Count == 0)
        {
            _logger.LogDebug("[Pegging] 未发现活跃 Lock 记录");
            return;
        }

        // 按 SupplyKey 分组，附加到对应的 SupplyLedgerEntry
        var locksBySupplyKey = allLocks.GroupBy(l => l.SupplyKey).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var entry in pool.GetAllEntries())
        {
            if (locksBySupplyKey.TryGetValue(entry.SupplyKey, out var locks))
            {
                foreach (var dbLock in locks)
                {
                    var lockType = dbLock.LockType switch
                    {
                        "STRICT_BINDING" => LockType.STRICT_BINDING,
                        "DEMAND_PROTECTION" => LockType.DEMAND_PROTECTION,
                        _ => (LockType?)null
                    };

                    if (!lockType.HasValue)
                    {
                        _logger.LogWarning(
                            "[Pegging] 未识别的 LockType: {LockType}，SupplyKey={SupplyKey}",
                            dbLock.LockType, entry.SupplyKey);
                        continue;
                    }

                    entry.Locks.Add(new LockRecord
                    {
                        LockType = lockType.Value,
                        LockedQty = dbLock.LockedQty,
                        LockedToOrderId = dbLock.SourcePlanVersionId.HasValue
                            ? null
                            : ExtractOrderIdFromDemandKey(dbLock.DemandKey),
                        LockedToDemandKey = dbLock.DemandKey,
                        LockedAt = dbLock.CreatedAt
                    });

                    // 更新 LockedQty 累计
                    entry.LockedQty += dbLock.LockedQty;
                }
            }
        }

        _logger.LogDebug(
            "[Pegging] 已加载 {LockCount} 条活跃 Lock 记录到供给池",
            allLocks.Count);
    }

    /// <summary>
    /// 从 DemandKey 提取 OrderId（如：ORDER_12345_MAT001_F01 → 12345）
    /// </summary>
    private static long? ExtractOrderIdFromDemandKey(string demandKey)
    {
        if (string.IsNullOrEmpty(demandKey)) return null;

        var parts = demandKey.Split('_');
        if (parts.Length >= 2 && parts[0] == "ORDER" && long.TryParse(parts[1], out var orderId))
            return orderId;

        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PI Position 两级装载（§8）
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 从 3号位 FrozenStrategySnapshot 投影出 5号位 FrozenFactParameters（C2-6：2号位在集成层投影，不落 DTO）。
    /// V1 最小集仅 Stage 进度走 PI Position；DefaultPurchaseLt / OverdueMargin / ArrivalToUsableOffsets
    /// 供 5号位后续事实（XC/Transit/Received）消费时使用，当前先按已冻结 ProcurementBlock 参数值投影。
    /// </summary>
    internal static FrozenFactParameters BuildFrozenFactParameters(FrozenStrategySnapshot snapshot)
    {
        // Warehouse 级默认 LT（MaterialId 为空）作为 DefaultPurchaseLt；无配置时 0。
        var defaultLtDays = snapshot.Procurement.DefaultPurchaseLt?
            .FirstOrDefault(r => string.IsNullOrWhiteSpace(r.MaterialId))?.DefaultLtDays ?? 0;

        return new FrozenFactParameters
        {
            StrategyProfileVersionId = snapshot.StrategyProfileVersionId,
            DefaultPurchaseLt = (int)Math.Round(defaultLtDays, MidpointRounding.AwayFromZero),
            // OverdueMargin 语义对齐：FrozenFactParameters.OverdueMargin 为「天」，取 ProcurementBlock 的 MinimumExtraDays。
            OverdueMargin = snapshot.Procurement.OverdueMargin?.MinimumExtraDays ?? 0,
            ArrivalToUsableOffsets = (snapshot.Procurement.ArrivalToUsableOffsets ?? [])
                .GroupBy(r => r.WarehouseCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => (int)Math.Round(g.First().OffsetHours, MidpointRounding.AwayFromZero),
                    StringComparer.OrdinalIgnoreCase)
        };
    }

    private const int SqlServerInParameterLimit = 2000;

    /// <summary>
    /// `QueryJsonInAsync` 用于**一次性单遍装载**的分块大小。
    ///
    /// 走 JSON 参数时不存在 2,100 参数上限，真正的成本是「每批要扫一遍目标表」：
    /// 分几批就扫几遍。取 20 万是为了让本系统的取值集合（PV190 实测 9.5 万个 PI、
    /// 3.4 万～12 万个物料）**恒定落在一批内**，从而只扫一遍表。
    /// 超过 20 万时仍会自动分批，行为不变（只是多扫一遍）。
    /// </summary>
    private const int JsonInSinglePassChunkSize = 200_000;

    /// <summary>
    /// 分批执行带 IN 列表的查询，规避 SQL Server TDS 2100 参数上限。
    /// wipStageRows 非空后 distinct MaterialCode/PI 可达数千，单条 `IN @List`（Dapper 展开为逐值参数）会溢出。
    /// </summary>
    private async Task<List<TRow>> QueryChunkedInAsync<TValue, TRow>(
        IReadOnlyList<TValue> values,
        Func<IReadOnlyList<TValue>, Task<IEnumerable<TRow>>> query,
        string? tag = null)
    {
        // 【2026-10-25 增】逐块耗时留痕。⑤ PI Position 里三个分块查询（批次桥接 / 本批 StagePath /
        // 兜底 StagePath）原本一个数都看不到，212s 只能靠猜。tag 非空 ⇒ 每块都打；
        // tag 为空 ⇒ 只在单块 >5s 时打（避免其它调用点刷屏）。纯观测，不改取值与分块大小。
        var rows = new List<TRow>();
        var chunkIndex = 0;
        var chunkTotal = (values.Count + SqlServerInParameterLimit - 1) / SqlServerInParameterLimit;
        for (var i = 0; i < values.Count; i += SqlServerInParameterLimit)
        {
            var chunk = values.Skip(i).Take(SqlServerInParameterLimit).ToList();
            var swChunk = System.Diagnostics.Stopwatch.StartNew();
            var got = (await query(chunk)).ToList();
            swChunk.Stop();
            chunkIndex++;
            if (tag != null || swChunk.ElapsedMilliseconds >= 5000)
                _logger.LogInformation(
                    "[Pegging][分块查询] {Tag} 第 {Index}/{Total} 块: 值={Count}, 返回={Rows} 行, 耗时={Ms}ms",
                    tag ?? "QueryChunkedIn", chunkIndex, chunkTotal, chunk.Count, got.Count, swChunk.ElapsedMilliseconds);
            rows.AddRange(got);
        }

        return rows;
    }

    /// <summary>
    /// 分批执行「值集合作为**单个 JSON 参数** ＋ `OPENJSON` 表值函数」的查询。
    ///
    /// 与 <see cref="QueryChunkedInAsync"/> 的区别只在**传参方式**：后者把值展开成逐值参数
    /// （2,000 个/批），值多时会退化成病态计划（PV2 实测单条 59.9s，见 `LoadBatchByPiAsync` 注释），
    /// 且始终受 SQL Server 2,100 参数上限约束；本方法只传 **1 个** nvarchar(max) 参数，
    /// SQL 侧写 `IN (SELECT value FROM OPENJSON(@Json))`。**值域与结果集完全不变。**
    /// 可用性：SQL Server 2016+（本库 2019 / 兼容级 150）。
    /// </summary>
    private async Task<List<TRow>> QueryJsonInAsync<TValue, TRow>(
        IReadOnlyList<TValue> values,
        Func<string, Task<IEnumerable<TRow>>> query,
        int chunkSize = 20000,
        string? tag = null)
    {
        var rows = new List<TRow>();
        var chunkIndex = 0;
        var chunkTotal = (values.Count + chunkSize - 1) / chunkSize;
        for (var i = 0; i < values.Count; i += chunkSize)
        {
            var chunk = values.Skip(i).Take(chunkSize).ToList();
            var swChunk = System.Diagnostics.Stopwatch.StartNew();
            var got = (await query(JsonSerializer.Serialize(chunk))).ToList();
            swChunk.Stop();
            chunkIndex++;
            if (tag != null || swChunk.ElapsedMilliseconds >= 5000)
                _logger.LogInformation(
                    "[Pegging][分块查询] {Tag} 第 {Index}/{Total} 块: 值={Count}, 返回={Rows} 行, 耗时={Ms}ms",
                    tag ?? "QueryJsonIn", chunkIndex, chunkTotal, chunk.Count, got.Count, swChunk.ElapsedMilliseconds);
            rows.AddRange(got);
        }

        return rows;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // T2-01（2026-10-09）：PI 权威剩余事实 —— 来源 [Order] ∪ 本域订单链，与 Stage/MES 解耦
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>[Order] / Order_Canonical → PI 权威剩余事实 的装载行（Dapper 映射用，一 PI 可能多行，装载后归并）。</summary>
    private sealed class PiRemainingLoadRow
    {
        public string ProductionInstructionNo { get; set; } = string.Empty;
        public int MaterialId { get; set; }
        public string MaterialCode { get; set; } = string.Empty;
        public int FactoryId { get; set; }
        public string FactoryCode { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public decimal? ReceivedQty { get; set; }

        /// <summary>来源 <c>[Order].Id</c>。来自 <c>Order_Canonical</c> 的补取行没有 <c>[Order]</c> 承载行 ⇒ 恒 0。</summary>
        public long OrderId { get; set; }

        /// <summary>来源 <c>Order_Canonical.Id</c>（两种来源都有；追溯与 T2-01.5 缺口样本核验用）。</summary>
        public long OrderCanonicalId { get; set; }

        public string OrderType { get; set; } = string.Empty;
        public DateTime? DueDate { get; set; }
        public DateTime? IssueDate { get; set; }
    }

    /// <summary>
    /// 装载 PI 权威剩余事实（T2-01）。**任何 Stage / MES 工单 / PI Position 判断之前**先做这一步，
    /// 建立 <c>PI号 → {原始量, 已入库量, 剩余量}</c> 的运行期权威映射。
    ///
    /// 口径（用户 2026-10-09 定盘）：<c>PiRemainingQty = max(Quantity − ISNULL(ReceivedQty,0), 0)</c>，
    /// 两列在整条链（ODS 视图 → Staging → Canonical → [Order]）原样透传、零运算，故先后 - 原样等价。
    /// 判别「有 PI」只看 <c>MTS_InstructionNo IS NOT NULL</c>——不看 ZPQF、不看 OrderNo 前缀。
    ///
    /// <b>但「有 PI 号」≠「可入供给池」</b>：本方法是**供给侧**装载，另有一道**业务身份闸门**（PM v1.3 B-01）——
    /// 普通客户订单（<c>OrderType='SALES_ORDER'</c>）**即使挂了 MTS 指示号**也**不得**作为 Supply：
    /// 它是「要交货」（需求），不是「已有货」（供给），同一行同时进两张清单会让需求找到自己 ⇒ 静默不生产。
    /// 厂间 SH（<c>CustomerSegment='跨厂'</c>）的供给资格**由其专路保留**（<c>LoadInterFactoryShipmentsAsync</c>），
    /// **不在此路**——否则同一张 SH 会以 WIP 与 INTER_FACTORY_ORDER 两个身份重复入池。
    /// 闸门对 <c>OrderType</c> 为 NULL 的行**放行**（不臆断身份，保持原行为），避免 `NULL='SALES_ORDER'`⇒UNKNOWN 静默滤空。
    ///
    /// 边界（PM T2-01.2：不用 0 / 客户需求量 / Stage MAX **偷偷**替代）：
    ///   · <c>ReceivedQty IS NULL</c> → 按 0 参与公式，但置 <c>HasMissingReceivedQty</c> 并留痕；
    ///   · <c>ReceivedQty &gt; Quantity</c> → 剩余 clamp 到 0，置 <c>HasOverReceivedQty</c> 并留痕；
    ///   · 同一 PI 出现在多行且数量矛盾 → 取「剩余量最大」的那行为准（不静默求和，避免重复计量），并留痕。
    ///
    /// ── 范围（2026-10-09 整改，PM T2-01.5「若现有 [Order] 确实无法取得某些真实 PI 的原始量，
    ///    应报告具体样本与最小现有链路缺口」对应的落码）──
    /// 两个来源合并，按 <c>OrderCanonicalId</c> 去重：
    ///   A) <c>[Order]</c>：本 PlanVersion 已装载的行（顶层成品）。见 <see cref="BomSnapshot.MaterialUniverse"/> 说明
    ///      —— <c>[Order]</c> 的装载带产品族闸门，而下阶加工件物料的 <c>ProductFamilyId</c> 恒 NULL（归族规则
    ///      只覆盖成品工艺码），**PI 事实在供给池里因此永远只覆盖顶层**。
    ///   B) <c>Order_Canonical</c>：本域 <paramref name="materialUniverse"/>（BOM 父件∪子件）内、
    ///      <c>Status = 'OPEN'</c> 的 PI 行 —— **不加产品族闸门**。PI 是供给事实，
    ///      与「顶层需求归哪个域」是两件事；不改归域口径、不改 <c>[Order]</c> 内容（加工件 PI 行若装进
    ///      <c>[Order]</c> 会变成顶层需求 ⇒ 重复生产）。
    ///
    /// ⚠️ B 源**不加 <c>DueDate</c> 时间窗**（<c>[Order]</c> 装载带的「90 天」是**需求侧**规划视界）。
    ///    供给侧装载（库存/采购在途）从无时间窗，PI 同属供给；若给 B 源加 90 天窗，则「交期在外、但父件要得急」
    ///    的下阶 PI 会被误判为「无供给」⇒ 塌成新增生产（正是本次要修的失败形态）。实测 PV2：
    ///    宇宙内 PI 行 118,154（Σ剩余 ≈6,265万）→ 加 90 天窗后只剩 25,713（Σ剩余 ≈2,485万）。
    ///
    /// ⛔ 不在此处构造无 PI 的客户订单（PM T2-03.3：无 PI 需求不走 PI 公式，按冻结 Order.Quantity 处理）。
    /// ⛔ 不预置任何数据库列/表/接口（PM T2-01.3：现有 DTO/内存上下文最小实现）。
    /// </summary>
    private async Task<IReadOnlyList<PiRemainingFact>> LoadPiRemainingFactsAsync(
        int planVersionId,
        IReadOnlySet<string> materialUniverse,
        CancellationToken ct)
    {
        var rows = (await _connectionManager.QueryAsync<PiRemainingLoadRow>(
            @"SELECT o.MTS_InstructionNo AS ProductionInstructionNo,
                     o.MaterialId,
                     m.MaterialCode,
                     o.FactoryId,
                     f.Code           AS FactoryCode,
                     o.Quantity,
                     o.ReceivedQty,
                     o.Id             AS OrderId,
                     ISNULL(o.OrderCanonicalId, 0) AS OrderCanonicalId,
                     o.OrderType,
                     o.CustomerDueDate AS DueDate,
                     o.IssueDate
              FROM [Order] o
              INNER JOIN Material m ON m.Id = o.MaterialId
              INNER JOIN Factory  f ON f.Id = o.FactoryId
              WHERE o.PlanVersionId = @PlanVersionId
                AND o.MTS_InstructionNo IS NOT NULL
                AND o.MTS_InstructionNo <> ''
                -- B-01 业务身份闸门：客户订单行不得作为 Supply —— **不区分是否厂间 SH**。
                -- 「挂了 MTS 指示号」不改变身份（PM v1.3 B-01 点名的正是这一形态）。
                -- 厂间 SH 走**它自己的专路**（LoadInterFactoryShipmentsAsync → INTER_FACTORY_ORDER）；
                -- 此处若再留 `OR CustomerSegment='跨厂'`，同一张 SH 会同时以 WIP 与 INTER_FACTORY_ORDER
                -- 两个身份入池 = 重复计量（PV2 实测 4 行 / Σ593）；且源厂的 PI 本就不是本厂 WIP。
                -- ISNULL 保证 OrderType 为 NULL 时不被 `NULL='SALES_ORDER'`⇒UNKNOWN 静默滤掉（放行，保持原行为）。
                AND ISNULL(o.OrderType, N'') <> 'SALES_ORDER'",
            new { PlanVersionId = planVersionId },
            db: DatabaseId.APS)).ToList();

        var orderRowCount = rows.Count;

        // ── B 源：本域物料宇宙内的订单链 PI 行（无产品族闸门）──
        // 与 A 源的列一一对应（OrderId 恒 0；DueDate 用 oc.DueDate 对齐 A 源的 CustomerDueDate 语义）。
        // 2026-10-26 性能整改：原按 2,000 物料一批（31 批）走 `IN @MaterialCodes`。宇宙物料实测 6 万+，
        // 裸 IN 会撞 2,100 参数上限（loader-no-raw-in-list 教训），分块后每批的批内选择性差异又把计划推向
        // 全表扫 —— PV190(Run=192) 实测 31 批里 2 批各 6,107ms / 5,010ms，③ 段合计 21,704ms。
        // 改为**单 JSON 参数 + 显式 HASH JOIN**：Order_Canonical 全表 283,137 行，实测一次扫描 760ms。
        // 谓词与结果集不变，只换传参方式。
        if (materialUniverse.Count > 0)
        {
            var universeCodes = materialUniverse.ToList();
            var extra = await QueryJsonInAsync(universeCodes, json =>
                _connectionManager.QueryAsync<PiRemainingLoadRow>(
                    @"SELECT oc.MTS_InstructionNo AS ProductionInstructionNo,
                             m.Id             AS MaterialId,
                             oc.MaterialCode,
                             f.Id             AS FactoryId,
                             f.Code           AS FactoryCode,
                             oc.Quantity,
                             oc.ReceivedQty,
                             0                AS OrderId,
                             oc.Id            AS OrderCanonicalId,
                             oc.OrderType,
                             oc.DueDate,
                             oc.IssueDate
                      FROM Order_Canonical oc
                      INNER HASH JOIN OPENJSON(@Json) j ON j.value = oc.MaterialCode
                      INNER JOIN Material m ON m.MaterialCode = oc.MaterialCode
                      INNER JOIN Factory  f ON f.Code = oc.FactoryCode AND f.IsActive = 1
                      WHERE oc.Status = N'OPEN'
                        AND oc.MTS_InstructionNo IS NOT NULL
                        AND oc.MTS_InstructionNo <> ''
                        -- B-01 同一道业务身份闸门（与 A 源逐字同口径）：客户订单行不得作为 Supply，含厂间 SH
                        AND ISNULL(oc.OrderType, N'') <> 'SALES_ORDER'
                        AND NOT EXISTS (SELECT 1 FROM [Order] o
                                        WHERE o.PlanVersionId = @PlanVersionId
                                          AND o.OrderCanonicalId = oc.Id)",
                    new { PlanVersionId = planVersionId, Json = json },
                    db: DatabaseId.APS),
                chunkSize: JsonInSinglePassChunkSize,
                tag: $"3b 订单链PI(物料={universeCodes.Count})");

            rows.AddRange(extra);
        }

        // B-01 闸门剔除量：只对 A 源计数（一次轻量 COUNT）。B 源是分块查询、同一谓词，
        // 不再另开计数查询；A 源这一条即足以在运行日志里留证「闸门确实在剔行」。
        var excludedSoRows = await _connectionManager.QueryFirstOrDefaultAsync<int>(
            $@"SELECT COUNT(*)
              FROM [Order] o
              WHERE o.PlanVersionId = @PlanVersionId
                AND o.MTS_InstructionNo IS NOT NULL
                AND o.MTS_InstructionNo <> N''
                AND ISNULL(o.OrderType, N'') = 'SALES_ORDER'
                AND ISNULL(o.CustomerSegment, N'') <> N'{CustomerSegmentCodes.InterFactory}'",
            new { PlanVersionId = planVersionId },
            db: DatabaseId.APS);

        _logger.LogInformation(
            "[Pegging][T2-01] PI 事实装载来源：A=[Order] {OrderRows} 行 / B=Order_Canonical(本域物料宇宙 {Universe} 个物料，" +
            "无产品族闸门) {ExtraRows} 行；合计 {Rows} 行（PlanVersionId={PlanVersionId}）。" +
            "[B-01 身份闸门] 已剔除普通客户订单（非厂间 SH，含挂 MTS 指示号者）A 源 {ExcludedSoRows} 行",
            orderRowCount, materialUniverse.Count, rows.Count - orderRowCount, rows.Count, planVersionId, excludedSoRows);

        if (rows.Count == 0)
        {
            _logger.LogWarning(
                "[Pegging][T2-01] 无任何带 MTS_InstructionNo 的 PI 行（[Order] 与本域订单链均为空，PlanVersionId={PlanVersionId}）" +
                "⇒ 本次无真实 PI 事实，PI Position 与 PI Supply 均为空。请确认订单同步链（ODS → Staging → Canonical）已跑。",
                planVersionId);
            return Array.Empty<PiRemainingFact>();
        }

        var facts = new List<PiRemainingFact>(rows.Count);
        var missingReceived = 0;
        var overReceived = 0;
        var duplicatePi = 0;
        var duplicateConflict = 0;

        foreach (var group in rows.GroupBy(r => r.ProductionInstructionNo, StringComparer.Ordinal))
        {
            var first = group.First();
            if (group.Count() > 1) duplicatePi++;

            PiRemainingLoadRow chosen = first;
            decimal chosenRemaining = decimal.MinValue;

            foreach (var r in group)
            {
                if (r.ReceivedQty is null) missingReceived++;
                if (r.ReceivedQty is { } rcv && rcv > r.Quantity) overReceived++;

                var remaining = Math.Max(r.Quantity - (r.ReceivedQty ?? 0m), 0m);

                // 同一 PI 多行：取剩余量最大者为准（不是求和——同一 PI 的总量只应计量一次）。
                if (remaining > chosenRemaining)
                {
                    chosen = r;
                    chosenRemaining = remaining;
                }
            }

            if (group.Count() > 1)
            {
                // 多行之间若 (Quantity, ReceivedQty) 不一致，即为 PM T2-01.2 所说的「PI 映射重复矛盾」。
                var qtySet = group.Select(r => (r.Quantity, r.ReceivedQty)).Distinct().Count();
                if (qtySet > 1)
                {
                    duplicateConflict++;
                    _logger.LogWarning(
                        "[Pegging][T2-01] PI {Pi} 在 [Order] 中出现 {N} 行且数量字段不一致（{Vals}），" +
                        "按剩余量最大行取值（OrderId={OrderId}，剩余={Remaining}）。同一物理数量不得重复计量，请核上游 PI 映射。",
                        group.Key, group.Count(),
                        string.Join(" | ", group.Select(r => $"{r.Quantity}/{r.ReceivedQty?.ToString() ?? "NULL"}")),
                        chosen.OrderId, chosenRemaining);
                }

                // 分组键只取 PI 号（见本方法 GroupBy）：同一 PI 若跨多个 FactoryId，
                // 组内只保留剩余量最大的一行 ⇒ 另一厂的工厂身份被静默折叠。
                // 下游 1号位 的 PI 唯一性键含 FactoryId（PhaseTwoInitialScheduler 内），
                // 代表行若非需求所在厂，匹配数为 0 ⇒ 记为 PI_LEGAL_QTY_CONTRACT_PENDING。
                // 此处只留证、不改变键域与取值：当前实测跨厂同 PI = 0 行 ⇒ 零行为差异。
                var factorySet = group.Select(r => r.FactoryId).Distinct().Count();
                if (factorySet > 1)
                {
                    _logger.LogWarning(
                        "[Pegging][D-2] PI {Pi} 出现在 {F} 个不同 FactoryId（{Factories}），" +
                        "本分组按 PI 号只保留一行（取剩余量最大者，FactoryId={ChosenFactory}），其余工厂身份被折叠。" +
                        "下游 1号位 按 (PI, Material, FactoryId) 匹配，代表行若不在需求所在厂则匹配为 0。请核跨厂同 PI 来源。",
                        group.Key, factorySet,
                        string.Join(" | ", group.Select(r => $"{r.FactoryId}/{r.FactoryCode}").Distinct()),
                        chosen.FactoryId);
                }
            }

            var fact = new PiRemainingFact
            {
                ProductionInstructionNo = chosen.ProductionInstructionNo,
                MaterialId              = chosen.MaterialId,
                MaterialCode            = chosen.MaterialCode,
                FactoryId               = chosen.FactoryId,
                FactoryCode             = chosen.FactoryCode,
                PiQuantity              = chosen.Quantity,
                PiReceivedQty           = chosen.ReceivedQty,
                PiRemainingQty          = chosenRemaining,
                OrderId                 = chosen.OrderId,
                OrderType               = chosen.OrderType,
                OrderCanonicalId        = chosen.OrderCanonicalId,
                DueDate                 = chosen.DueDate,
                IssueDate               = chosen.IssueDate,
                HasMissingReceivedQty   = chosen.ReceivedQty is null,
                HasOverReceivedQty      = chosen.ReceivedQty is { } c && c > chosen.Quantity
            };

            if (fact.HasMissingReceivedQty)
                _logger.LogWarning(
                    "[Pegging][T2-01] PI {Pi} 的 ReceivedQty 为 NULL（[Order].Id={OrderId} / OrderCanonicalId={CanonicalId}）" +
                    "——已按 0 参与公式，剩余={Remaining}。该 PI 的已入库事实不可得，请核上游同步链。",
                    fact.ProductionInstructionNo, fact.OrderId, fact.OrderCanonicalId, fact.PiRemainingQty);

            if (fact.HasOverReceivedQty)
                _logger.LogWarning(
                    "[Pegging][T2-01] PI {Pi} 的 ReceivedQty({Rcv}) > Quantity({Qty})（[Order].Id={OrderId} / " +
                    "OrderCanonicalId={CanonicalId}）——剩余已 clamp 到 0，不再作为可排产供给。请核上游数量口径。",
                    fact.ProductionInstructionNo, fact.PiReceivedQty, fact.PiQuantity, fact.OrderId, fact.OrderCanonicalId);

            facts.Add(fact);
        }

        _logger.LogInformation(
            "[Pegging][T2-01] PI 权威剩余事实装载完成：PI={Pi} 个（装载行 {Rows} 行 = [Order] {OrderRows} + 订单链补取 {ExtraRows}；" +
            "重复PI {Dup} 个其中数量矛盾 {Conflict} 个；ReceivedQty NULL {Miss} 处；超量 {Over} 处）；" +
            "Σ原始量={SumQ}，Σ已入库={SumR}，Σ剩余={SumRem}，剩余>0 的 PI={Pos} 个",
            facts.Count, rows.Count, orderRowCount, rows.Count - orderRowCount,
            duplicatePi, duplicateConflict, missingReceived, overReceived,
            facts.Sum(f => f.PiQuantity), facts.Sum(f => f.PiReceivedQty ?? 0m), facts.Sum(f => f.PiRemainingQty),
            facts.Count(f => f.PiRemainingQty > 0m));

        return facts;
    }

    /// <summary>
    /// 装载 PI Position（消费 5号位 IProductionInstructionPositionCalculator）。返回 ProductionInstructionNo → 结果。
    ///
    /// ⚠️ T2-04（2026-10-09 整改）——**PI 宇宙 = <paramref name="piFacts"/>（[Order]），不是 Stage**：
    ///   · 真实 PI 即使当前没有任何 Stage 报工行，也必须出现在 inputs / Routing / 快照里（PM T2-04.2），
    ///     否则它会从 PI Position 里整个消失，下游也就永远看不到这个在制 PI。
    ///   · PI 总量 <c>ErpRemainingQty</c> 只取 <c>piFacts.PiRemainingQty</c>（= max(Quantity − ReceivedQty, 0)）；
    ///     旧写法 <c>ErpRemainingQty = group.Max(r =&gt; r.RemainingQty)</c>（Stage MAX）**已删除**（PM T2-04.1）。
    ///     Stage 明细从此只回答「已报到哪些 Stage、各 Stage 进度多少」——是**定位明细**，不是总量。
    ///
    /// 事实范围（V1 最小集）：Stage 进度（StageProgressSnapshot）→ StageProgressFact（可空）；
    /// Stage 顺序取 APS_BOM_STAGE_PATH_RAW（ChildMaterialCode+StageCode → StageSeq，多批次/BOMNO/Scope 取 MIN，近似）；
    /// PiInventory / XC / CrossFactoryEdge / Transit 已绑定（ext_ 同义词 Loader，Transit 实测 0 行）；
    /// Received 已退出计算主链（不再装载，见 PM 裁定）；
    /// 计算器按 UNLOCATED 兜底闭合（**位置不可信**，不是「无供给」）。
    ///
    /// 【2026-10-26 更正】原注释写「2号位据此把未定位份额路由到新增生产」，**与可执行代码相反**：
    ///   · 本仓 `LPS.APS.Application` **没有任何一行读 `PositionType.UNLOCATED` 作判据**（唯二两处读
    ///     `PositionType` 的是 `:6680` 转字符串留痕、`:12946-12947` 读 `FIRST_STAGE_PENDING`/`STAGE_WAITING`）；
    ///   · 与本类 `:7035`「落入 UNLOCATED（**不得删除 Supply / 转新增生产**）」、`:12883`「UNLOCATED ⇒ 不产行」
    ///     两句新注释直接矛盾 ⇒ 本句是旧漂移，作废。
    ///   · 0号位 2026-10-26《XC范围·影子计量与时间单位正式回复 v1.1》§〇二.2 再次明令：
    ///     **不得把「普通库存被拒」自动转译为 PI 的 `UNLOCATED`／MES 在制／新增生产需求**。
    ///     `UNLOCATED` 只表示「已有真实 PI，但其在 Position 中位置不可信」，**不是**供给资格结果的通用兜底。
    ///   · 真缺口走的是 `:12378` 的**自制件供给不足 ⇒ `NEW_REQUIREMENT` 缺口虚拟供给**（正常 BFS 递归），
    ///     与 UNLOCATED 无因果链。
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, ProductionInstructionPositionResult> Positions,
                        PiOperationCodeResolver Resolver)> LoadPiPositionsAsync(
        IReadOnlyList<PiRemainingFact> piFacts,
        IReadOnlyList<WipStageLoadRow> wipStageRows,
        FrozenStrategySnapshot frozenSnapshot,
        int planVersionId,
        int snapshotRunId,
        DateTime dataCutoffTime,
        CancellationToken ct,
        bool persistPositionSnapshot = true)
    {
        if (piFacts.Count == 0)
        {
            // 无真实 PI（[Order] 里没有任何带 MTS_InstructionNo 的行）⇒ 本次没有在制 PI 可定位。
            // 注意：这与「有 PI 但 Stage 明细为空」是两回事——后者仍须逐 PI 产出 Position（T2-04.2）。
            _logger.LogWarning(
                "[Pegging][T2-04] PI 权威事实为空 ⇒ 本次不产出任何 PI Position（无在制 PI）。" +
                "若业务上确有在制 PI，请先核订单同步链是否已把 MTS_InstructionNo 带进 [Order]。");
            return (new Dictionary<string, ProductionInstructionPositionResult>(),
                    new PiOperationCodeResolver(new Dictionary<string, IReadOnlyList<RoutingOperationFact>>(StringComparer.Ordinal)));
        }

        // T2-04.2：driverRows = 「每个 PI 一行身份行」（StageCode 空、数量 0，仅供下游装载器枚举 PI/物料/工厂）
        //           ∪ 真实 Stage 定位明细行。
        // 下游装载器（库存/XC/跨厂边/在途/工单快照/工序进度/Routing）一律收 driverRows：
        // 它们要回答的是「这个 PI 有哪些事实」，不是「这个 PI 报到哪了」——后者才需要 Stage 行。
        // 【2026-10-25 分段埋点】⑤「PI Position」在域总账里只有**一个**总耗时（PV190 实测 212,197ms），
        // 里面混着四段性质完全不同的开销：① 批次桥接（JSON 分块查 [Order]×OBRL）② 本批 StagePath
        // ③ 兜底 StagePath ④ 5号位 计算器 ⑤ 快照落库（5号位 仓储逐行 INSERT）。
        // 不切开就无法判断慢在哪一段、也无法向对应号位报量级；本轮实测该段已两次把整轮 Pegging 拖死
        // （`LoadBatchByPiAsync` 的 SQL 触发 60s 命令超时 ⇒ 异常冒到顶 ⇒ Task=0）。
        // 纯观测：不动任何取值、不动分块大小、不改判定。
        var swPos = System.Diagnostics.Stopwatch.StartNew();
        long posLastMs = 0;
        void MarkPos(string name)
        {
            var nowMs = swPos.ElapsedMilliseconds;
            _logger.LogInformation("[Pegging][PI Position 分段] {Name}={Delta}ms (累计 {Total}ms)",
                name, nowMs - posLastMs, nowMs);
            // 同步落一行进度（见 `ComputeStageProgressMonitor`）：本段历史上曾把整轮拖死而无任何外部可见信号。
            ComputeStageProgressMonitor.Current?.Completed(name, nowMs - posLastMs);
            posLastMs = nowMs;
        }

        var driverRows = new List<WipStageLoadRow>(piFacts.Count + wipStageRows.Count);
        driverRows.AddRange(piFacts.Select(f => new WipStageLoadRow
        {
            ProductionInstructionNo = f.ProductionInstructionNo,
            MaterialCode            = f.MaterialCode,
            MaterialId              = f.MaterialId,
            FactoryId               = f.FactoryId,
            FactoryCode             = f.FactoryCode,
            StageCode               = string.Empty,
            GoodCompletedQty        = 0m,
            PlannedQty              = 0m,
            RemainingQty            = 0m
        }));
        driverRows.AddRange(wipStageRows);

        // 1) Stage 顺序映射：ChildMaterialCode+StageCode → StageSeq
        //    【2026-09-28 改造，1号位 回执 §4.2 双方同意】原实现是**全局** `MIN(StageSeq)`、无任何批次上下文
        //    （PM §二十一.3 明令禁止的「跨订单/BOM/父件汇总」形态）。现改为三态：
        //      ① 每条 WIP 行按自身 PI → 订单 → BOM 批次，取**该批次**的 StagePath（精确，优先）；
        //      ② 该批次内查不到 ⇒ 回退**本 PV 关联批次**的 `MIN(StageSeq)`
        //         （**2026-10-17 J14-12 收窄**：不再是无批次上下文的全局兜底；实测当前全库仅 1 批，
        //           故本次零差异，但跨批时不会再静默抓到旧批。无关联批次 ⇒ 不做兜底、留缺 + 计数）；
        //      ③ 三态**可计数**（1号位 要求：兜底占比必须可监控 —— 悄悄涨到 100% 的兜底等于没改）。
        //
        //    【2026-10-17 J14-12】**两处 SQL 均加 `StageScopeType` 并参与 GROUP BY**：
        //      `ROOT` = 该物料**自身作为需求根**时的链；`EDGE` = 该物料**作为某父件子件**时的链段
        //      （可能在父件供给阈值处截断 ⇒ 阶段集更少）。
        //      不分 scope 的 `MIN(StageSeq)` 就是**跨 scope 静默并集**：同一 (物料,Stage) 在两种 scope 下
        //      可给出不同 StageSeq（全库实测 486 个物料 / 581 个 (物料,Stage) 对），MIN 会在无声中取较小者。
        //      取用顺序 **ROOT → EDGE**：PI 是「该物料自身的生产指示」（自身完工），完整链才能定位在制位置；
        //      EDGE 仅在该物料没有 ROOT 行时兜底。两种 scope 同时存在且同 Stage 的 Seq 不同 ⇒
        //      逐对计数 + WARNING 留痕，不再静默择一。
        var materialCodes = driverRows.Select(r => r.MaterialCode).Distinct().ToList();

        // 1.a) PI → 本次 BOM 批次
        var piNos = driverRows.Select(r => r.ProductionInstructionNo)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var batchByPi = await LoadBatchByPiAsync(piNos, planVersionId);
        MarkPos($"1a 批次桥接(PI={piNos.Count})");

        // 1.b) 本批 StagePath（逐批一次分块查询）：(BatchNo, MaterialCode, Scope) → { StageCode → StageSeq }
        var batchStageByMaterial =
            new Dictionary<(string BatchNo, string MaterialCode, string Scope), Dictionary<string, int>>();
        // 提到循环外：1.c) 要用它判断「哪些关联批 1b 已经装过」。
        var loadedBatches = batchByPi.Values.Distinct(StringComparer.Ordinal).ToList();
        foreach (var batch in loadedBatches)
        {
            // 2026-10-26 性能整改：原 2,000 物料一批（17 批），PV190 实测本段 7,677ms。
            // 该表 2,275,284 行**全属同一批**（BatchNo 不具选择性），按物料 seek 的分块收益抵不过
            // 17 次往返 + 17 次聚合；改单 JSON 参数 + 显式 HASH JOIN 一次扫描（实测 1,328ms）。
            var rows = await QueryJsonInAsync(materialCodes, json =>
                _connectionManager.QueryAsync<StagePathLoadRow>(
                    @"SELECT ChildMaterialCode, StageCode, StageScopeType, MIN(StageSeq) AS StageSeq
                      FROM APS_BOM_STAGE_PATH_RAW r
                      INNER HASH JOIN OPENJSON(@Json) mc ON mc.value = r.ChildMaterialCode
                      WHERE r.BatchNo = @BatchNo
                      GROUP BY r.ChildMaterialCode, r.StageCode, r.StageScopeType",
                    new { BatchNo = batch, Json = json },
                    db: DatabaseId.APS, commandTimeout: 300),
                chunkSize: JsonInSinglePassChunkSize,
                tag: $"1b 本批StagePath(批次={batch}, 物料={materialCodes.Count})");

            foreach (var r in rows)
            {
                var key = (batch, r.ChildMaterialCode, NormalizeStageScope(r.StageScopeType));
                if (!batchStageByMaterial.TryGetValue(key, out var m))
                    batchStageByMaterial[key] = m = new Dictionary<string, int>(StringComparer.Ordinal);
                m[r.StageCode] = r.StageSeq;
            }
        }
        MarkPos($"1b 本批 StagePath(批次={loadedBatches.Count}, 物料={materialCodes.Count})");

        // 1.c) 兜底：收窄到**本 PV 关联批次**（J14-12）。无关联批次 ⇒ 不兜底（宁缺不跨批抓旧批）。
        var linkedBatches = (await ResolveLinkedBatchNosAsync(planVersionId))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var globalStageByMaterial =
            new Dictionary<(string MaterialCode, string Scope), Dictionary<string, int>>();

        if (linkedBatches.Count == 0)
        {
            _logger.LogWarning(
                "[Pegging][J14-12] 本 PlanVersion 无可解析的 BOM 关联批次 ⇒ Stage 顺序**不做跨批兜底**：" +
                "批内查不到的 PI 物料将留缺并计入「无 StagePath」计数，不静默抓历史批次。");
        }
        else
        {
            // 1c-a) 关联批中 1b 已按批装过的部分：在内存里并进去，**不再二次查库**。
            //   PV190（FAMILY_X 全量域）实测：`OrderBomRequestLink` 21,778 行只有 **1 个**批次，
            //   而 1b 的批次同样由「`[Order]` ⋈ `OrderBomRequestLink` ⋈ 本 PlanVersion」派生
            //   ⇒ 两侧批次集**恒等**，1c 原先那次查询装的是 1b 已装过的同一批行（实测耗时 27.4s 的纯重复）。
            //   MIN 可结合 ⇒ 「先并已装批次、再并残余批次」与「一次查全部关联批」**逐值等价**。
            var loadedSet = new HashSet<string>(loadedBatches, StringComparer.Ordinal);
            var linkedSet = new HashSet<string>(linkedBatches, StringComparer.Ordinal);
            foreach (var kv in batchStageByMaterial)
            {
                if (!linkedSet.Contains(kv.Key.BatchNo)) continue;
                MergeStageSeq(globalStageByMaterial, kv.Key.MaterialCode, kv.Key.Scope, kv.Value);
            }

            // 1c-b) 只有 1b 未装过的关联批才需要查库（正常单批场景下该集合为空 ⇒ 整条查询跳过）。
            var residualBatches = linkedBatches.Where(b => !loadedSet.Contains(b)).ToList();
            if (residualBatches.Count == 0)
            {
                _logger.LogInformation(
                    "[Pegging][1c] 关联批={Linked} 个，全部已由 1b 按批装载 ⇒ 跳过兜底查询（原为重复装载）",
                    linkedBatches.Count);
            }
            else
            {
                var residualBatchesJson = JsonSerializer.Serialize(residualBatches);
                var globalRows = await QueryChunkedInAsync(materialCodes, chunk =>
                    _connectionManager.QueryAsync<StagePathLoadRow>(
                        @"SELECT ChildMaterialCode, StageCode, StageScopeType, MIN(StageSeq) AS StageSeq
                          FROM APS_BOM_STAGE_PATH_RAW
                          WHERE BatchNo IN (SELECT value FROM OPENJSON(@BatchesJson))
                            AND ChildMaterialCode IN @MaterialCodes
                          GROUP BY ChildMaterialCode, StageCode, StageScopeType",
                        new { BatchesJson = residualBatchesJson, MaterialCodes = chunk },
                        db: DatabaseId.APS, commandTimeout: 300),
                    tag: $"1c 兜底StagePath(关联批={linkedBatches.Count}, 需补查={residualBatches.Count}, 物料={materialCodes.Count})");

                foreach (var r in globalRows)
                {
                    MergeStageSeq(globalStageByMaterial, r.ChildMaterialCode,
                        NormalizeStageScope(r.StageScopeType),
                        new[] { new KeyValuePair<string, int>(r.StageCode, r.StageSeq) });
                }
            }
        }
        MarkPos($"1c 兜底 StagePath(关联批={linkedBatches.Count})");

        // 1.d) 合并：**按 scope 各自**做「批次优先 + 本 PV 兜底」，再按 ROOT → EDGE 选用；
        //      同时统计三态与「scope 分歧」。
        //      同物料多 WIP 行时取其**首个可解析批次**（确定性优先，避免同物料不同行取到不同批次）。
        var batchByMaterial = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in driverRows)
        {
            if (batchByMaterial.ContainsKey(r.MaterialCode)) continue;
            if (!string.IsNullOrEmpty(r.ProductionInstructionNo)
                && batchByPi.TryGetValue(r.ProductionInstructionNo, out var b))
                batchByMaterial[r.MaterialCode] = b;
        }

        var stageSeqMap = new Dictionary<(string MaterialCode, string StageCode), int>();
        var stageSeqByMaterial = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var batchHitPairs = 0;
        var globalFallbackPairs = 0;
        var unresolvedStageSeqMaterials = 0;
        var rootPreferredMaterials = 0;
        var edgeFallbackMaterials = 0;
        var scopeConflictPairs = 0;
        var scopeConflictSamples = new List<string>();

        foreach (var code in materialCodes)
        {
            var hasBatch = batchByMaterial.TryGetValue(code, out var batch) && !string.IsNullOrEmpty(batch);

            // 每个 scope 各自：批次图 + 本 PV 兜底图，一并交给纯函数做「合并 → 选 scope → 算分歧」
            Dictionary<string, int>? Pick((string BatchNo, string MaterialCode, string Scope) key)
                => batchStageByMaterial.TryGetValue(key, out var m) ? m : null;

            var selection = SelectStageScope(
                rootBatch:      hasBatch ? Pick((batch!, code, StageScopeRoot)) : null,
                rootFallback:   globalStageByMaterial.TryGetValue((code, StageScopeRoot), out var rf) ? rf : null,
                edgeBatch:      hasBatch ? Pick((batch!, code, StageScopeEdge)) : null,
                edgeFallback:   globalStageByMaterial.TryGetValue((code, StageScopeEdge), out var ef) ? ef : null);

            scopeConflictPairs += selection.ConflictPairs;
            foreach (var s in selection.ConflictSamples)
                if (scopeConflictSamples.Count < 10)
                    scopeConflictSamples.Add($"{code}/{s}");

            if (selection.Scope is null || selection.Stages.Count == 0)
            {
                unresolvedStageSeqMaterials++;
                continue;
            }

            if (selection.Scope == StageScopeRoot) rootPreferredMaterials++;
            else edgeFallbackMaterials++;

            batchHitPairs      += selection.BatchPairs;
            globalFallbackPairs += selection.FallbackPairs;

            stageSeqByMaterial[code] = new Dictionary<string, int>(selection.Stages, StringComparer.Ordinal);
            foreach (var kv in selection.Stages)
                stageSeqMap[(code, kv.Key)] = kv.Value;
        }

        if (scopeConflictPairs > 0)
        {
            _logger.LogWarning(
                "[Pegging][J14-12] ROOT 与 EDGE 两种 scope 对同一 Stage 给出不同 StageSeq：共 {Pairs} 个 (物料,Stage) 对；" +
                "已按 **ROOT 优先** 取用（完整链），不再由 MIN(StageSeq) 静默择一。样本：{Samples}",
                scopeConflictPairs,
                string.Join(" | ", scopeConflictSamples));
        }

        // 兜底占比可监控（1号位 要求）：若「兜底」长期接近「批次内命中」，说明 PI→批次 解析失效
        // 或批次内确无 StagePath —— 兜底形同虚设，必须能从日志直接发现，而非等结果变差再回溯。
        _logger.LogInformation(
            "[Pegging] PI Stage 顺序装载：物料 {Materials} 个（批次内命中 {BatchHit} 个(物料,阶段) / 本PV批次兜底 {GlobalFallback} 个 / " +
            "无 StagePath {Unresolved} 个物料；PI→批次解析 {BatchResolved} 个 PI；scope 取用 ROOT {RootPicked} 个物料 / EDGE 兜底 {EdgePicked} 个；" +
            "关联批次 {LinkedBatches} 个）",
            materialCodes.Count, batchHitPairs, globalFallbackPairs, unresolvedStageSeqMaterials, batchByPi.Count,
            rootPreferredMaterials, edgeFallbackMaterials, linkedBatches.Count);

        // 每物料 Stage 路径（按 StageSeq 升序，首/末标记 IsStartStage/IsEndStage）。
        // 关键：5号位 DetermineEffectiveStartStage / CalculateStagePositions(isFirstStage) /
        //       CalculateNextOperationContexts 都消费 input.StagePath；缺了会 startStageCode 为空 →
        //       BuildExistingExecutionContexts 的 derivedRemainingQty=0 → 连续份额切不出（实测 406 上下文全 0）。
        var stagePathByMaterial = stageSeqByMaterial
            .ToDictionary(
                kv => kv.Key,
                kv =>
                {
                    var ordered = kv.Value
                        .Select(x => (StageCode: x.Key, StageSequence: x.Value))
                        .OrderBy(x => x.StageSequence)
                        .ToList();
                    return (IReadOnlyList<StagePathFact>)ordered.Select((x, i) => new StagePathFact
                    {
                        StageCode = x.StageCode,
                        StageSequence = x.StageSequence,
                        IsStartStage = i == 0,
                        IsEndStage = i == ordered.Count - 1
                    }).ToList();
                },
                StringComparer.Ordinal);

        // 1.5) PI 级库存事实（ext_ 同义词：ERP_Inventory_View.WarehouseCode=6位工序码 → MES_ProcessCode_View.StageCode）
        //      ⚠️ XC 仓在装载侧剔除（冻结 `11 v2.5 §176`）：同一批 XC 只能走 1.6 的 PI 级专路，
        //      不得从 InventoryAvailableSupplyDetail 再派生一份同份额 PiInventories。
        var piInventoryMap = await LoadPiInventoryFactsAsync(driverRows, ct);
        MarkPos("1d-1 PI 库存事实装载");

        // 1.6) XC（线边仓）事实 —— **PI 级**（冻结 `11 v2.5 §174/§175`、`04 V3.31 PI-XC-01..05`）
        //      源 = APS 本地夜间快照 `InventoryFact_PiXc`（每日 00:37 由 sp_SyncPiXcSnapshot 刷新，
        //      再上游是 ODS `ODS_ProductionInstructionInventoryBalance`，八列见 `08` §8a）。
        //      ⛔ Run 期**不直连** `[mes]` 链接服务器；⛔ 不按 (MaterialCode, FactoryCode) 归组后
        //         灌给该物料+厂下的每一个 PI —— 那是 1.50× 虚高主因。
        var xcByPi = await LoadPiXcFactsAsync(piFacts, dataCutoffTime, ct);
        MarkPos("1d-2 PI-XC 事实装载");

        // 1.7) 跨厂边事实（ext_MES_APS_BOM_Workset_CrossFactoryEdge，按 ChildMaterialCode 归组）
        var crossFactoryEdgeMap = await LoadCrossFactoryEdgesAsync(driverRows, ct);
        MarkPos("1d-3 跨厂边事实装载");

        // 1.9) 厂间在途事实（Transit，ext_ERP_InterplantInTransit_View；0 行，待 5号位 ODS 数据）
        // PM 0910：Transit 必须按生产指示号（TransitDocumentNo = PI No）精确归属，Material/目标厂仅做一致性校验。
        var transitFacts = await LoadTransitFactsAsync(driverRows, planVersionId, ct);
        var piIdentities = driverRows
            .GroupBy(r => r.ProductionInstructionNo, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (g.First().MaterialCode, g.First().FactoryCode), StringComparer.Ordinal);
        var (transitByPi, transitIssues) = AttributeInterplantTransitToPi(transitFacts, piIdentities);
        MarkPos("1d-4 厂间在途装载+归属");
        foreach (var issue in transitIssues)
            _logger.LogWarning("[Pegging] Transit 归属 Issue: {Issue}", issue);

        // 1.95) 原始 MES 快照源 Run（0号位《PM回复1009-1》§三 A-Q1/A-Q2/A-Q3）
        //   由 LoadSupplyPoolAsync 统一解析后传入：候选/恢复 = Base 链上「有三类完整快照」的源 Run；夜间 = 本 Run。
        //   ⚠️ 只用于**读原始快照**；本 PV 的 PI Position 快照**写**点仍用自身 SourceScheduleRunId（见 SavePiPositionSnapshotsAsync）。
        var scheduleRunId = snapshotRunId;

        // 1.96) 每工单快照（IN_PROGRESS）+ 每工单工序进度——5号位 BuildExistingExecutionContexts 的两路事实源
        // 【2026-10-26 埋点】本段 PV190 实测 55,623ms，是我方最大单项，但内部三个装载器共用一个 MarkPos，
        //   看不出钱花在哪一个上。此处只加拆分计时与行数，**不新增进度阶段**（`ComputeStageProgress` 的
        //   阶段数是对外可见指标，增段会改口径），故仍保留原 `1d-5` 锚点，另打一行拆分。
        var swD5 = System.Diagnostics.Stopwatch.StartNew();
        var workOrderFactsByPi = await LoadWorkOrderSnapshotFactsAsync(scheduleRunId, driverRows, ct);
        var d5aMs = swD5.ElapsedMilliseconds;
        var operationProgressByPi = await LoadOperationProgressFactsAsync(scheduleRunId, driverRows, ct);
        var d5bMs = swD5.ElapsedMilliseconds - d5aMs;
        var (routingOpsByPi, routingDepsByPi) = await LoadRoutingFactsAsync(driverRows, ct);
        var d5cMs = swD5.ElapsedMilliseconds - d5aMs - d5bMs;
        MarkPos("1d-5 工单快照/工序进度/Routing 装载");
        _logger.LogInformation(
            "[Pegging][1d-5 拆分] 工单快照={A}ms / 工序进度={B}ms / Routing={C}ms（合计 {T}ms；PI={Pi} 个，工单={Wo} 行，工序进度={Op} 行）",
            d5aMs, d5bMs, d5cMs, swD5.ElapsedMilliseconds,
            driverRows.Select(r => r.ProductionInstructionNo).Distinct(StringComparer.Ordinal).Count(),
            workOrderFactsByPi.Values.Sum(v => (long)v.Count),
            operationProgressByPi.Values.Sum(v => (long)v.Count));

        // 诊断：事实装载计数（连续份额依赖 WorkOrder IN_PROGRESS + OperationProgress）
        _logger.LogInformation(
            "[Pegging][Diag] PI Position 事实: WIP行={Wip}, IN_PROGRESS工单={Wo}, 工序进度={Op行}, 工单PI={WoPi}",
            wipStageRows.Count,
            workOrderFactsByPi.Values.Sum(v => (long)v.Count),
            operationProgressByPi.Values.Sum(v => (long)v.Count),
            workOrderFactsByPi.Count);

        MarkPos("1d-6 事实装载后汇总/诊断");

        // 2) 按 PI 构建输入 —— 宇宙 = piFacts（[Order]），Stage 明细可为空（T2-04.2）
        var stageRowsByPi = wipStageRows
            .GroupBy(r => r.ProductionInstructionNo, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var inputs = new List<ProductionInstructionPositionInput>(piFacts.Count);
        foreach (var fact in piFacts)
        {
            var pi = fact.ProductionInstructionNo;
            stageRowsByPi.TryGetValue(pi, out var stageRows);

            var stageProgress = new List<StageProgressFact>(stageRows?.Count ?? 0);
            if (stageRows != null)
            {
                foreach (var s in stageRows)
                {
                    stageProgress.Add(new StageProgressFact
                    {
                        StageCode = s.StageCode,
                        GoodCompletedQty = s.GoodCompletedQty,
                        PlannedQty = s.PlannedQty,
                        RemainingQty = s.RemainingQty,
                        StageSequence = stageSeqMap.TryGetValue((s.MaterialCode, s.StageCode), out var seq) ? seq : 0,
                        SnapshotId = null
                    });
                }
            }

            var key = (fact.MaterialCode, fact.FactoryCode);
            piInventoryMap.TryGetValue(key, out var piInventories);
            // XC 按 PI 键取（不再是 material+factory）：同一物料多 PI 各自只拿自己的份额。
            xcByPi.TryGetValue(pi, out var xcFacts);
            crossFactoryEdgeMap.TryGetValue(fact.MaterialCode, out var crossFactoryEdges);
            transitByPi.TryGetValue(pi, out var piTransitFacts);
            workOrderFactsByPi.TryGetValue(pi, out var workOrderFacts);
            operationProgressByPi.TryGetValue(pi, out var operationProgress);
            routingOpsByPi.TryGetValue(pi, out var routingOperations);
            routingDepsByPi.TryGetValue(pi, out var routingDependencies);

            inputs.Add(new ProductionInstructionPositionInput
            {
                ProductionInstructionNo = pi,
                MaterialId = fact.MaterialId,
                FactoryId = fact.FactoryId,
                MaterialCode = fact.MaterialCode,
                FactoryCode = fact.FactoryCode,
                // T2-04.1：PI 总量只取权威事实 max(Quantity − ReceivedQty, 0)。
                // 旧写法 `group.Max(r => r.RemainingQty)`（Stage MAX，实测可达 700 而 PI 只有 600）已删除。
                ErpRemainingQty = fact.PiRemainingQty,
                StageProgress = stageProgress,
                StagePath = stagePathByMaterial.TryGetValue(fact.MaterialCode, out var spList)
                    ? spList
                    : (IReadOnlyList<StagePathFact>)Array.Empty<StagePathFact>(),
                WorkOrders = workOrderFacts ?? (IReadOnlyList<WorkOrderSnapshotFact>)Array.Empty<WorkOrderSnapshotFact>(),
                OperationProgress = operationProgress ?? (IReadOnlyList<OperationProgressFact>)Array.Empty<OperationProgressFact>(),
                PiInventories = piInventories ?? (IReadOnlyList<PiInventoryFact>)Array.Empty<PiInventoryFact>(),
                XcFacts = xcFacts ?? (IReadOnlyList<XcFact>)Array.Empty<XcFact>(),
                CrossFactoryEdges = crossFactoryEdges ?? (IReadOnlyList<CrossFactoryEdgeFact>)Array.Empty<CrossFactoryEdgeFact>(),
                TransitFacts = piTransitFacts ?? (IReadOnlyList<InterplantTransitFact>)Array.Empty<InterplantTransitFact>(),
                RoutingOperations = routingOperations ?? (IReadOnlyList<RoutingOperationFact>)Array.Empty<RoutingOperationFact>(),
                RoutingDependencies = routingDependencies ?? (IReadOnlyList<RoutingDependencyFact>)Array.Empty<RoutingDependencyFact>()
            });
        }

        // 3) 调 5号位计算器（纯计算，无 I/O）
        MarkPos($"1d-7 输入构造(PI={inputs.Count})");
        var parameters = BuildFrozenFactParameters(frozenSnapshot);
        var results = await _piPositionCalculator.CalculateProductionInstructionPositionsAsync(inputs, parameters, ct);
        MarkPos($"2 5号位计算器(PI={inputs.Count}, 结果={results.Count})");

        // 3.5) 续排起点出口核验（2026-09-29）：StartOperationCode 是需求「从哪道工序续排」的唯一入口。
        // 1号位 按 OperationCode 建图/匹配该值（PhaseOneConstraintBuilder.BuildReachableStages 的
        // nodesByOperationCode[demand.StartOperationCode]、PhaseTwoInitialScheduler.GetOperationsFromStage 的
        // k.OperationCode == startOperationCode）——非空但查不中时无兜底，需求直接判 S26 Unscheduled。
        // ⚠️ 该风险「随时会发」而非「潜在」：5号位 出口值取的是工序名（OperationName）、1号位 匹配空间是
        //    工序号（OperationCode），两侧取值集合交集为 0（432 名 ∩ 188 号 = ∅）⇒ 一旦有值产出，100% 落 S26。
        //    产能源侧 confirms 该形态普遍存在：OperationProgressSnapshot 无 OperationCode 列（源头只有名）；
        //    全库 (MaterialCode,StageCode) 在制工序数=1 的有 6,530 组（唯一前沿，最多的档）、PI 在制工序数=1 的 49,586 个。
        // ⛔ 反面口径（勿再复述）：曾据「PV 633 实测非空 0 条」判其「未触发/上了膛没击发」——**那是抽样假象**：
        //    PV 633 的 4,109 个 MTS_InstructionNo 在快照批次里在制工序数恰为 0，整批落进 remainingOps.Count==0
        //    的设计内合法空分支。一次交集为 0 不等于代码不会产出值，别拿单次抽样当普遍规律。
        // 此处只观测、不改写：把「出口值是否落在该PI的 RoutingOperation.OperationCode 空间内」打成计数，
        // 跑完可直接从日志核，避免该类静默失败再靠结果变差回溯。
        var opCodeSpaceByPi = inputs.ToDictionary(
            i => i.ProductionInstructionNo,
            i => new HashSet<string>(i.RoutingOperations.Select(o => o.OperationCode), StringComparer.Ordinal),
            StringComparer.Ordinal);
        // 名→码 反查器（2号位 装载层）：构造一次，供本方法出口核验与下游分桶消费共用同一份口径。
        var opCodeResolver = new PiOperationCodeResolver(routingOpsByPi);

        var startOpNonEmpty = 0;      // 出口非空（原始值）
        var startOpAlreadyCode = 0;   // 出口值本身已是本 PI 的合法 OperationCode
        var startOpResolved = 0;      // 出口为名、按名反查命中（含工单 RouteCode 消解）
        var startOpUnresolved = 0;    // 出口为名、反查不中 ⇒ 消费侧留 null（Fail Closed）
        var unresolvedByStage = new Dictionary<string, int>(StringComparer.Ordinal);
        var startOpSamples = new List<string>();
        var startOps = results
            .SelectMany(r =>
                (r.NextOperationContexts ?? (IReadOnlyList<NextOperationContextDto>)Array.Empty<NextOperationContextDto>())
                    .Select(c => (Pi: r.ProductionInstructionNo, Value: c.StartOperationCode,
                                  Stage: (string?)c.StartStageCode, Route: (string?)null))
                .Concat((r.ExistingExecutionContexts ?? (IReadOnlyList<ExistingExecutionContextDto>)Array.Empty<ExistingExecutionContextDto>())
                    .Select(c => (Pi: r.ProductionInstructionNo, Value: c.StartOperationCode,
                                  Stage: (string?)c.StartStageCode, Route: (string?)c.RouteCode))));
        foreach (var (pi, value, stage, route) in startOps)
        {
            if (string.IsNullOrEmpty(value)) continue;
            startOpNonEmpty++;
            // 只在「该PI有 Routing 节点」时判：无 Routing 的物料是另一类问题（无工序），不混计。
            if (!opCodeSpaceByPi.TryGetValue(pi, out var codeSpace) || codeSpace.Count == 0)
                continue;
            if (codeSpace.Contains(value!)) { startOpAlreadyCode++; continue; }

            var resolved = opCodeResolver.Resolve(pi, value, route);
            if (resolved != null) { startOpResolved++; continue; }

            startOpUnresolved++;
            var st = string.IsNullOrEmpty(stage) ? "(无Stage)" : stage!;
            unresolvedByStage[st] = unresolvedByStage.TryGetValue(st, out var n) ? n + 1 : 1;
            if (startOpSamples.Count < 10) startOpSamples.Add(value!);
        }
        if (startOpNonEmpty > 0)
            _logger.LogInformation(
                "[Pegging][红线] 续排起点出口核验：StartOperationCode 非空 {Tot} 条；其中已是合法码 {IsCode} 条 / " +
                "按「名→码 反查」命中 {Resolved} 条 / **反查不中 {Unresolved} 条**（消费侧留 null，1号位 Fail Closed 记 S26，不猜码）。" +
                "反查不中按 Stage：{ByStage}；样例=[{Samples}]",
                startOpNonEmpty, startOpAlreadyCode, startOpResolved, startOpUnresolved,
                string.Join("/", unresolvedByStage.OrderByDescending(k => k.Value).Select(k => k.Key + "=" + k.Value)),
                string.Join(",", startOpSamples.Distinct()));
        else
            _logger.LogInformation("[Pegging][红线] 续排起点出口核验：本次 StartOperationCode 全为空。⚠️ 这**不代表风险不存在**——它只说明本次这一批 PI 恰好全落在「工序已完/无 Routing 数据」的合法空分支（详见下条断口定位）。产工序名的分支在全库形态上普遍存在（(Material,Stage) 单在制工序 6,530 组），换批数据即会命中 ⇒ 别把「本次全空」记成「已排除」");

        // 3.6) 续排起点「全空」时定位断口在哪一级：无Routing节点 / 无上下文 / 上下文E=0 / 只到Stage级。
        var nextCtx = results.Sum(r => (r.NextOperationContexts ?? Array.Empty<NextOperationContextDto>()).Count);
        var nextCtxWithStartStage = results.Sum(r => (r.NextOperationContexts ?? Array.Empty<NextOperationContextDto>()).Count(c => !string.IsNullOrEmpty(c.StartStageCode)));
        var nextCtxWithQty = results.Sum(r => (r.NextOperationContexts ?? Array.Empty<NextOperationContextDto>()).Count(c => c.SliceQty > 0m));
        var existCtx = results.Sum(r => (r.ExistingExecutionContexts ?? Array.Empty<ExistingExecutionContextDto>()).Count);
        var existCtxWithStage = results.Sum(r => (r.ExistingExecutionContexts ?? Array.Empty<ExistingExecutionContextDto>()).Count(c => !string.IsNullOrEmpty(c.StartStageCode)));
        var existCtxWithQty = results.Sum(r => (r.ExistingExecutionContexts ?? Array.Empty<ExistingExecutionContextDto>()).Count(c => c.DerivedRemainingQty > 0m));
        _logger.LogInformation(
            "[Pegging][红线] 续排起点断口定位：结果 {Res} 个PI；其中 RoutingOperations 非空 {PiWithRouting} 个 / RoutingDependencies 非空 {PiWithDeps} 个；" +
            "NextOperationContext 共 {NextCtx} 条（有StartStage {NextStage} / SliceQty>0 {NextQty}），ExistingExecutionContext 共 {ExistCtx} 条（有StartStage {ExistStage} / E>0 {ExistQty}）",
            results.Count,
            inputs.Count(i => i.RoutingOperations.Count > 0),
            inputs.Count(i => i.RoutingDependencies.Count > 0),
            nextCtx, nextCtxWithStartStage, nextCtxWithQty,
            existCtx, existCtxWithStage, existCtxWithQty);

        // 4) 保存 PI Position 快照 + 数量闭环校验（2号位职责，不修正 5号位 事实）
        //    `persistPositionSnapshot=false`（BFS-only / 双跑对照探针）：**只做闭环校验与留痕，
        //    不落库**。原因：该落库按 `PlanVersion.SourceScheduleRunId` DELETE+INSERT，探针跑一次就会
        //    覆盖夜间 run 的真实 PI Position 快照；而探针本身宣称「只读、不落库」。
        MarkPos("2b 起点核验/断口诊断");
        await SavePiPositionSnapshotsAsync(planVersionId, inputs, results, piFacts, ct, persistPositionSnapshot);
        MarkPos($"3 快照落库(5号位仓储逐行INSERT, 落库={persistPositionSnapshot})");

        return (results.ToDictionary(r => r.ProductionInstructionNo, StringComparer.Ordinal), opCodeResolver);
    }

    /// <summary>
    /// 每工单快照事实装载（IN_PROGRESS 工单）——5号位 ExistingExecutionContext 的工单身份源。
    /// 冻结红线（v1.6/v2.6）：只传 IN_PROGRESS；RELEASED/CLOSED/DELETED 不传；工单级不带 RemainingQty。
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<WorkOrderSnapshotFact>>> LoadWorkOrderSnapshotFactsAsync(
        int scheduleRunId,
        IReadOnlyList<WipStageLoadRow> wipStageRows,
        CancellationToken ct)
    {
        if (scheduleRunId <= 0)
            return new Dictionary<string, IReadOnlyList<WorkOrderSnapshotFact>>();

        var piNos = wipStageRows.Select(r => r.ProductionInstructionNo).Distinct().ToList();
        // 2026-10-26 性能整改：原按 2,000 值一批走 `IN @PiNos`，每批优化器都选
        // `Clustered Index Scan` + Bitmap + Hash Match ⇒ 48 批 = 整表扫 48 遍。
        // PV190(Run=192) 实测 48 批 × 约 494ms = 23,731ms。改为**单 JSON 参数 + 显式 HASH JOIN**，
        // 实测同一结果集一次扫描（8,727 页 / 378ms）。谓词与结果集不变，只换传参方式。
        var rows = await QueryJsonInAsync(piNos, json =>
            _connectionManager.QueryAsync<WorkOrderSnapshotRow>(
                @"SELECT ProductionInstructionNo, MESWorkOrderNo, MaterialCode, PlannedQty, WorkOrderStatus, DataCutoffTime
                  FROM MESWorkOrderSnapshot w
                  INNER HASH JOIN OPENJSON(@Json) j ON j.value = w.ProductionInstructionNo
                  WHERE w.ScheduleRunId = @ScheduleRunId
                    AND w.WorkOrderStatus = 'IN_PROGRESS'",
                new { ScheduleRunId = scheduleRunId, Json = json },
                db: DatabaseId.APS),
            chunkSize: JsonInSinglePassChunkSize,
            tag: $"1d-5a 工单快照(PI={piNos.Count})");

        return rows
            .GroupBy(r => r.ProductionInstructionNo, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<WorkOrderSnapshotFact>)g.Select(r => new WorkOrderSnapshotFact
                {
                    ProductionInstructionNo = r.ProductionInstructionNo,
                    MESWorkOrderNo = r.MESWorkOrderNo,
                    MaterialCode = r.MaterialCode,
                    PlannedQty = r.PlannedQty,
                    WorkOrderStatus = r.WorkOrderStatus,
                    DataCutoffTime = r.DataCutoffTime
                }).ToList(),
                StringComparer.Ordinal);
    }

    /// <summary>
    /// 每工单工序进度事实装载——5号位按 MESWorkOrderNo 找当前有效工序的输入事实源。
    /// V1 口径：工序身份 = OperationName（MES 无稳定工序码）。PM 2026-09-15 裁决已废弃线性 OperationSeq：
    /// OperationSequence 保留兼容字段（=0），不参与 StartOperation/Routing/连续份额/Solver 判断；
    /// StartOperationCode 由 5号位基于 RoutingOperation + RoutingDependency 拓扑前沿解析输出，2号位只消费、不拼序。
    /// RemainingQty = max(PlannedQty − GoodQty, 0)，与 v1.6 DerivedRemainingQty 同一公式。
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<OperationProgressFact>>> LoadOperationProgressFactsAsync(
        int scheduleRunId,
        IReadOnlyList<WipStageLoadRow> wipStageRows,
        CancellationToken ct)
    {
        if (scheduleRunId <= 0)
            return new Dictionary<string, IReadOnlyList<OperationProgressFact>>();

        var piNos = wipStageRows.Select(r => r.ProductionInstructionNo).Distinct().ToList();
        // 2026-10-26 性能整改：同 1d-5a —— 48 批各扫一遍全表（实测 48 × 约 326ms = 15,657ms），
        // 改单 JSON 参数 + 显式 HASH JOIN 后一次扫描。谓词与结果集不变。
        var rows = await QueryJsonInAsync(piNos, json =>
            _connectionManager.QueryAsync<OperationProgressRow>(
                @"SELECT ProductionInstructionNo, MESWorkOrderNo, OperationName, StageCode,
                         PlannedQty, GoodQty, LastReportTime, SourceUpdatedAt, LastReportResourceCode,
                         RouteCode, ProductionDepartmentId
                  FROM OperationProgressSnapshot op
                  INNER HASH JOIN OPENJSON(@Json) j ON j.value = op.ProductionInstructionNo
                  WHERE op.ScheduleRunId = @ScheduleRunId",
                new { ScheduleRunId = scheduleRunId, Json = json },
                db: DatabaseId.APS),
            chunkSize: JsonInSinglePassChunkSize,
            tag: $"1d-5b 工序进度(PI={piNos.Count})");

        return rows
            .GroupBy(r => r.ProductionInstructionNo, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<OperationProgressFact>)g.Select(r => new OperationProgressFact
                {
                    OperationCode = r.OperationName,   // V1：工序身份 = OperationName，无独立工序码列
                    OperationName = r.OperationName,
                    StageCode = r.StageCode,
                    // 2026-09-30：MES 工单完整计划工序清单 + 工单创建部门，供 5号位 做路线定位
                    RouteCode = r.RouteCode ?? string.Empty,
                    ProductionDepartmentId = r.ProductionDepartmentId,
                    MESWorkOrderNo = r.MESWorkOrderNo,
                    PlannedQty = r.PlannedQty,
                    GoodQty = r.GoodQty,
                    RemainingQty = Math.Max(r.PlannedQty - r.GoodQty, 0m),
                    OperationSequence = 0,             // PM 裁决（2026-09-15）：废弃线性序，保留兼容字段，不参与业务判断
                    LastReportTime = r.LastReportTime,
                    LastReportResourceCode = string.IsNullOrWhiteSpace(r.LastReportResourceCode) ? null : r.LastReportResourceCode,
                    SnapshotId = null,
                    UpdatedAt = r.SourceUpdatedAt
                }).ToList(),
                StringComparer.Ordinal);
    }

    /// <summary>
    /// Routing 节点/边事实装载（按物料+部门过滤后的 PI 子集）——5号位 DAG 拓扑前沿算法的输入事实源。
    /// 部门维度复用 MaterialStageDeptContext((MaterialId, StageCode) → DefaultProductionDepartmentId)，
    /// 与 1号位 部门锁定同源（5号位 落码执行说明 2026-09-16 §4.3）。
    /// RouteCode/PathId 忠实传真实值（RouteCode 实为工序名序列串、非 "DEFAULT"，勿硬编码）。
    /// V1 简化：按 (MaterialId, ProductionDepartmentId) 过滤；跨部门拼接口径待 2↔5 另行对齐，不阻塞。
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, IReadOnlyList<RoutingOperationFact>> OpsByPi,
                        IReadOnlyDictionary<string, IReadOnlyList<RoutingDependencyFact>> DepsByPi)>
        LoadRoutingFactsAsync(IReadOnlyList<WipStageLoadRow> wipStageRows, CancellationToken ct)
    {
        var emptyOps = new Dictionary<string, IReadOnlyList<RoutingOperationFact>>(StringComparer.Ordinal);
        var emptyDeps = new Dictionary<string, IReadOnlyList<RoutingDependencyFact>>(StringComparer.Ordinal);

        if (wipStageRows.Count == 0)
            return (emptyOps, emptyDeps);

        var materialIds = wipStageRows.Select(r => r.MaterialId).Distinct().ToList();

        // 【2026-10-26 埋点】四段计时：部门上下文 / 节点 / 边 / 内存组装。PV190 的 1d-5 共 55,623ms，
        //   本方法三段查库 + 一段逐 PI 组装，原先一个数都看不到。纯观测，不改取值。
        var swRouting = System.Diagnostics.Stopwatch.StartNew();

        // a) (MaterialId, StageCode) → 部门集合（与 1号位 部门锁定同源；缺映射时该 Stage 无部门）
        var deptCtx = await LoadMaterialStageDeptContextAsync(materialIds, ct);
        var routingDeptMs = swRouting.ElapsedMilliseconds;
        var deptByMaterialStage = deptCtx
            .GroupBy(c => (c.MaterialId, c.StageCode))
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.ProductionDepartmentId).Distinct().ToHashSet());

        // b) 装载 Routing 节点/边（IsActive=1），按 PI 材料集合一次取回，再逐 PI 按部门过滤
        var operations = await QueryJsonInAsync(materialIds, json =>
            _connectionManager.QueryAsync<RoutingOperation>(
                @"SELECT MaterialId, ProductionDepartmentId, RouteCode, PathId, OperationCode, OperationName, StageCode
                  FROM RoutingOperation ro
                  INNER HASH JOIN OPENJSON(@Json) j ON j.value = ro.MaterialId
                  WHERE ro.IsActive = 1",
                new { Json = json },
                db: DatabaseId.APS),
            chunkSize: JsonInSinglePassChunkSize,
            tag: $"1d-5c Routing节点(物料={materialIds.Count})");
        var routingOpsMs = swRouting.ElapsedMilliseconds - routingDeptMs;

        var dependencies = await QueryJsonInAsync(materialIds, json =>
            _connectionManager.QueryAsync<RoutingDependency>(
                @"SELECT MaterialId, ProductionDepartmentId, RouteCode, PathId,
                         FromOperationCode, ToOperationCode, DependencyType
                  FROM RoutingDependency rd
                  INNER HASH JOIN OPENJSON(@Json) j ON j.value = rd.MaterialId
                  WHERE rd.IsActive = 1",
                new { Json = json },
                db: DatabaseId.APS),
            chunkSize: JsonInSinglePassChunkSize,
            tag: $"1d-5c Routing边(物料={materialIds.Count})");
        var routingDepsMs = swRouting.ElapsedMilliseconds - routingDeptMs - routingOpsMs;

        // c) 按 PI 组装：每个 PI 的部门 = 其 Stage 集在 MSC 的去重部门；无映射时回退到物料全路由
        //
        // ⚠ 2026-10-09 性能整改（`dotnet-stack` 钉死）：原实现逐 PI 写
        //   `operations.Where(o => o.MaterialId == materialId)` —— `operations` 是**全域**路由节点列表，
        //   于是每个 PI 都对整表线性扫一遍 ⇒ O(PI 数 × 全域节点数)。PV2 实测 118,154 个 PI，热线程整段卡在
        //   本方法的 `.Where(...).ToList()` 闭包里；期间 DB 全程空闲（`sys.dm_exec_requests` 无用户会话）
        //   ⇒ 纯客户端 CPU 空转，装载被拖到十几分钟。改为**一次建 MaterialId 索引**后 O(1) 取用。
        var opsByMaterial = operations
            .GroupBy(o => o.MaterialId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var depsByMaterial = dependencies
            .GroupBy(d => d.MaterialId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var opsByPi = new Dictionary<string, IReadOnlyList<RoutingOperationFact>>(StringComparer.Ordinal);
        var depsByPi = new Dictionary<string, IReadOnlyList<RoutingDependencyFact>>(StringComparer.Ordinal);

        foreach (var group in wipStageRows.GroupBy(r => r.ProductionInstructionNo, StringComparer.Ordinal))
        {
            var pi = group.Key;
            var materialId = group.First().MaterialId;

            var deptIds = new HashSet<int>();
            foreach (var s in group)
            {
                if (deptByMaterialStage.TryGetValue((materialId, s.StageCode), out var set))
                    deptIds.UnionWith(set);
            }

            IEnumerable<RoutingOperation> ops = opsByMaterial.TryGetValue(materialId, out var matOps)
                ? matOps
                : Array.Empty<RoutingOperation>();
            IEnumerable<RoutingDependency> deps = depsByMaterial.TryGetValue(materialId, out var matDeps)
                ? matDeps
                : Array.Empty<RoutingDependency>();

            if (deptIds.Count > 0)
            {
                ops = ops.Where(o => deptIds.Contains(o.ProductionDepartmentId));
                deps = deps.Where(d => deptIds.Contains(d.ProductionDepartmentId));
            }
            else
            {
                // 无 MSC 部门映射时回退物料全路由（避免静默漏装致 NEXT_OPERATION 误报），与 LoadRoutingContextAsync 同口径。
                // T2-04.2：只在「该 PI 确实有 Stage 定位行」时告警——PI 无任何 Stage 报工（driverRows 身份行
                // StageCode 为空）是合法形态，不是映射缺失，不该刷告警。
                if (group.Any(r => !string.IsNullOrEmpty(r.StageCode)))
                    _logger.LogWarning("[Pegging] PI {Pi} 无 MaterialStageDeptContext 部门映射，Routing 事实回退到物料全路由", pi);
            }

            opsByPi[pi] = ops.Select(o => new RoutingOperationFact
            {
                OperationCode = o.OperationCode,
                OperationName = o.OperationName,
                StageCode = o.StageCode ?? string.Empty,
                RouteCode = o.RouteCode,
                PathId = o.PathId
            }).ToList();

            depsByPi[pi] = deps.Select(d => new RoutingDependencyFact
            {
                FromOperationCode = d.FromOperationCode,
                ToOperationCode = d.ToOperationCode,
                DependencyType = d.DependencyType,
                RouteCode = d.RouteCode,
                PathId = d.PathId
            }).ToList();
        }

        _logger.LogInformation(
            "[Pegging][Routing 拆分] 部门上下文={Dept}ms(物料={Mat}) / 节点={Ops}ms({OpRows} 行) / " +
            "边={Deps}ms({DepRows} 行) / 内存组装={Asm}ms(PI={Pi}) / 合计={Total}ms",
            routingDeptMs, materialIds.Count,
            routingOpsMs, operations.Count,
            routingDepsMs, dependencies.Count,
            swRouting.ElapsedMilliseconds - routingDeptMs - routingOpsMs - routingDepsMs,
            wipStageRows.Select(r => r.ProductionInstructionNo).Distinct(StringComparer.Ordinal).Count(),
            swRouting.ElapsedMilliseconds);

        return (opsByPi, depsByPi);
    }

    /// <summary>
    /// 保存 PI Position 快照 + 数量闭环校验（2号位职责，2026-09-02 双专项冻结）。
    /// 冻结约束：Σ Position.Quantity = ERP PI RemainingQty；异常（数量不闭合 / 计算失败 / 位置缺失）
    /// 只登记（IssueCode 落快照 + 日志），不自行修正 5号位 计算出的位置数量。
    ///
    /// 2026-10-09（PM回复1009 F-08 / T2-04.4）：闭环校验升级为**两层**——
    ///   ① 权威 ERP 入参自洽（<paramref name="piFacts"/> 的 `max(Quantity−ReceivedQty,0)` 是否等于喂给计算器的
    ///      `ErpRemainingQty`、Received 是否缺失/倒挂、Quantity 是否非正）；
    ///   ② 5号位 Position 与入参闭合。**只做 ① 时不知道源值对不对，只做 ② 时不知道两边一起错。**
    /// </summary>
    private async System.Threading.Tasks.Task SavePiPositionSnapshotsAsync(
        int planVersionId,
        IReadOnlyList<ProductionInstructionPositionInput> inputs,
        IReadOnlyList<ProductionInstructionPositionResult> results,
        IReadOnlyList<PiRemainingFact> piFacts,
        CancellationToken ct,
        bool persist = true)
    {
        if (inputs.Count == 0)
            return;

        var scheduleRunId = await _connectionManager.QueryFirstOrDefaultAsync<int>(
            "SELECT SourceScheduleRunId FROM PlanVersion WHERE Id = @PlanVersionId",
            new { PlanVersionId = planVersionId },
            db: DatabaseId.APS);

        if (scheduleRunId <= 0)
        {
            _logger.LogWarning("[Pegging] PI Position 快照跳过：PlanVersionId={PlanVersionId} 无 SourceScheduleRunId", planVersionId);
            return;
        }

        var resultsByPi = results.ToDictionary(r => r.ProductionInstructionNo, StringComparer.Ordinal);
        // T2-04.2：物料码取自 inputs（= piFacts 权威身份），不再取自 Stage 行——
        // 无 Stage 报工的 PI 在旧实现里查不到物料码，快照会缺列。
        var materialCodeByPi = inputs
            .GroupBy(i => i.ProductionInstructionNo, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().MaterialCode, StringComparer.Ordinal);

        // F-08 第二层：权威 ERP 入参事实（同 PI 多行取「剩余最大」，与 LoadPiRemainingFactsAsync 同口径）
        var piFactByPi = piFacts
            .Where(f => !string.IsNullOrEmpty(f.ProductionInstructionNo))
            .GroupBy(f => f.ProductionInstructionNo, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(f => f.PiRemainingQty).First(), StringComparer.Ordinal);

        var (rows, issues) = MapPositionSnapshots(scheduleRunId, planVersionId, inputs, resultsByPi, materialCodeByPi, piFactByPi);

        foreach (var issue in issues)
            _logger.LogWarning("[Pegging] PI Position 闭环异常: {Issue}", issue);

        if (rows.Count == 0)
            return;

        if (!persist)
        {
            _logger.LogInformation(
                "[Pegging] PI Position 快照**只校验不落库**（探针/双跑路径）：{Rows} 行未写入 " +
                "(ScheduleRunId={RunId}, PlanVersionId={PlanVersionId})；闭环异常 {Issues} 条已在上方登记",
                rows.Count, scheduleRunId, planVersionId, issues.Count);
            return;
        }

        await _piPositionSnapshotRepo.SaveBatchAsync(scheduleRunId, planVersionId, rows, ct);
    }

    /// <summary>
    /// 把 5号位 计算结果映射为快照行，并做数量闭环校验（纯函数，可单测）。
    /// 校验规则（不修正事实，只登记）：
    ///   - 数量不闭合：Σ Position.Quantity != ErpRemainingQty（容差 0.0001）→ IssueCode=QUANTITY_GAP
    ///   - 计算失败：result.IsSuccess == false → IssueCode=POSITION_FAILED
    ///   - 位置缺失：inputs 有 PI 但计算器无结果 → 记 issue，不落行
    ///
    /// 2026-10-09（PM回复1009 F-08 / T2-04.4）：新增**第二层**——校验喂进去的 `ErpRemainingQty` **自身**是否可信。
    /// 只比 `ΣPosition vs 入参` 只能证明「两边一起错也照样闭合」；入参的真相在 `piFactByPi` 里。
    ///   - `ErpRemainingQty != max(PiQuantity − ISNULL(PiReceivedQty,0), 0)` → ERP_SOURCE_MISMATCH
    ///   - Quantity 非正 / Received 倒挂 / Received 缺失但按全额计              → ERP_QUANTITY_INVALID
    ///   - 入参 PI 在权威事实里查不到                                          → ERP_FACT_MISSING
    /// 传 `piFactByPi = null` ⇒ 退回旧行为（只做 Position 侧闭合），既有单测不变。
    /// </summary>
    internal static (List<ProductionInstructionPositionSnapshot> Rows, List<string> Issues) MapPositionSnapshots(
        int scheduleRunId,
        int planVersionId,
        IReadOnlyList<ProductionInstructionPositionInput> inputs,
        IReadOnlyDictionary<string, ProductionInstructionPositionResult> resultsByPi,
        IReadOnlyDictionary<string, string> materialCodeByPi,
        IReadOnlyDictionary<string, PiRemainingFact>? piFactByPi = null)
    {
        const decimal tolerance = 0.0001m;

        var rows = new List<ProductionInstructionPositionSnapshot>();
        var issues = new List<string>();

        foreach (var input in inputs)
        {
            if (!resultsByPi.TryGetValue(input.ProductionInstructionNo, out var result))
            {
                issues.Add($"PI {input.ProductionInstructionNo}: 位置缺失（计算器无结果）");
                continue;
            }

            materialCodeByPi.TryGetValue(input.ProductionInstructionNo, out var materialCode);

            var sum = result.Positions.Sum(p => p.Quantity);
            var hasClosureGap = Math.Abs(sum - input.ErpRemainingQty) > tolerance;
            var isFailed = !result.IsSuccess;

            // ── 第二层：权威 ERP 入参自洽（F-08）────────────────────────────────────────
            // 只登记、不修正：入参真相不在这里产生，2号位 无权在映射阶段改写它。
            string? erpSourceIssueCode = null;
            if (piFactByPi is not null)
            {
                if (!piFactByPi.TryGetValue(input.ProductionInstructionNo, out var piFact))
                {
                    erpSourceIssueCode = "ERP_FACT_MISSING";
                    issues.Add($"PI {input.ProductionInstructionNo}: ERP 权威事实缺失（入参 ErpRemainingQty={input.ErpRemainingQty} 无对应 PiRemainingFact）");
                }
                else
                {
                    var expected = Math.Max(piFact.PiQuantity - (piFact.PiReceivedQty ?? 0m), 0m);
                    if (Math.Abs(expected - input.ErpRemainingQty) > tolerance)
                    {
                        erpSourceIssueCode = "ERP_SOURCE_MISMATCH";
                        issues.Add(
                            $"PI {input.ProductionInstructionNo}: ERP 入参不自洽 —— 入参 ErpRemainingQty={input.ErpRemainingQty} " +
                            $"vs 权威 max(Quantity={piFact.PiQuantity} − Received={(piFact.PiReceivedQty?.ToString() ?? "NULL")},0)={expected}");
                    }
                    else if (piFact.PiQuantity <= 0m && input.ErpRemainingQty > 0m)
                    {
                        erpSourceIssueCode = "ERP_QUANTITY_INVALID";
                        issues.Add($"PI {input.ProductionInstructionNo}: ERP Quantity={piFact.PiQuantity} 非正但入参 ErpRemainingQty={input.ErpRemainingQty} > 0");
                    }
                    else if (piFact.HasOverReceivedQty)
                    {
                        erpSourceIssueCode = "ERP_QUANTITY_INVALID";
                        issues.Add(
                            $"PI {input.ProductionInstructionNo}: ERP Received 倒挂 —— Quantity={piFact.PiQuantity} Received={piFact.PiReceivedQty} ⇒ 剩余被压成 {input.ErpRemainingQty}");
                    }
                    else if (piFact.HasMissingReceivedQty)
                    {
                        erpSourceIssueCode = "ERP_QUANTITY_INVALID";
                        issues.Add($"PI {input.ProductionInstructionNo}: ERP ReceivedQty 缺失，剩余按 Quantity={piFact.PiQuantity} 全额计（口径存疑）");
                    }
                }
            }

            if (hasClosureGap)
                issues.Add($"PI {input.ProductionInstructionNo}: 数量不闭合 ΣPosition={sum} vs ERP RemainingQty={input.ErpRemainingQty}");
            else if (isFailed)
                issues.Add($"PI {input.ProductionInstructionNo}: 计算失败 {result.FailureReason}");

            // IssueCode 单列 ⇒ 按「Position 侧优先、ERP 源侧次之」固定优先级取一条；全部原因已在上面逐条登记。
            var issueCode = hasClosureGap ? "QUANTITY_GAP"
                          : isFailed      ? "POSITION_FAILED"
                          : erpSourceIssueCode;

            foreach (var slice in result.Positions)
            {
                rows.Add(new ProductionInstructionPositionSnapshot
                {
                    ScheduleRunId = scheduleRunId,
                    PlanVersionId = planVersionId,
                    ProductionInstructionNo = input.ProductionInstructionNo,
                    MaterialId = input.MaterialId,
                    MaterialCode = materialCode ?? string.Empty,
                    PositionType = slice.PositionType.ToString(),
                    Quantity = slice.Quantity,
                    CurrentStageCode = slice.StageCode,
                    NextStageCode = null,
                    AvailableTime = slice.AvailableTime,
                    SourceType = null,
                    SourceKey = slice.SourceKey,
                    IssueCode = issueCode,
                    Confidence = null
                });
            }
        }

        return (rows, issues);
    }

    /// <summary>
    /// 装载 PI 级库存事实（PiInventory）。
    ///
    /// 结构：ext_ERP_Inventory_View（MaterialCode/FactoryCode/WarehouseCode=6位工序码/Quantity）
    ///       LEFT JOIN ext_MES_ProcessCode_View（ProcessCode→StageCode）。
    /// LocationCategory 由 2号位按映射结果判定：
    ///   - 命中 StageCode → STAGE_INVENTORY（RelatedStageCode=StageCode）
    ///   - 未命中 → UNKNOWN（RelatedStageCode=null，由计算器 UNLOCATED 兜底）
    /// 返回 (MaterialCode, FactoryCode) → 事实列表，供按 PI 归属装载。
    ///
    /// ⚠️ **XC 仓在此剔除**（冻结 `11 v2.5 §176`：「只把可信该 PI 的 XC 送入 XcFacts，
    ///    不从 InventoryAvailableSupplyDetail 派生同份额 PiInventories」）。
    ///    剔除量按本轮聚合打点：这是「两套 XC 账」（仓储存量 vs PI 号别净额）差额的可观测口径，
    ///    不是静默丢弃 —— 见台账，与 5号位 的净额口径对账未闭环前必须留痕。
    /// </summary>
    private async Task<IReadOnlyDictionary<(string MaterialCode, string FactoryCode), List<PiInventoryFact>>>
        LoadPiInventoryFactsAsync(IReadOnlyList<WipStageLoadRow> wipStageRows, CancellationToken ct)
    {
        var materialCodes = wipStageRows.Select(r => r.MaterialCode).Distinct().ToList();
        var factoryCodes  = wipStageRows.Select(r => r.FactoryCode).Distinct().ToList();

        // 2026-10-26：`ext_MES_ProcessCode_View` 是**链接服务器同义词**（→ 10.116.2.73），
        // 每块都要跨网一次；按 2,000 物料分 17 块 ≈ 17 次往返，PV190 实测本段 6,378ms，
        // 而本地 `InventoryAvailableSupplyDetail` 只有 16.2 万行 —— 成本在往返不在扫描。
        // 改单 JSON 参数后只剩 1 次往返。此处**不加哈希提示**：远端对象加哈希会迫使整表落地。
        var rows = await QueryJsonInAsync(materialCodes, json =>
            _connectionManager.QueryAsync<PiInventoryLoadRow>(
                @"SELECT d.MaterialCode,
                         fa.Code        AS FactoryCode,
                         d.StorageCode   AS WarehouseCode,
                         d.Quantity,
                         pc.StageCode,
                         CASE WHEN pcx.ProcessCode IS NULL THEN 0 ELSE 1 END AS IsXc
                  FROM InventoryAvailableSupplyDetail d
                  INNER JOIN Factory fa
                    ON fa.Id = d.FactoryId
                   AND fa.IsActive = 1
                  -- 先按 ProcessCode 去重再关联：ext_MES_ProcessCode_View 实测 257 行 / 256 个
                  -- 不同 ProcessCode（`51305101` 有 2 条重复映射），直接 JOIN 会把这 8 行 MES 明细
                  -- （1,418）放大成 16 行 / 2,836。取 MIN(StageCode) 保证确定性；该重复码两条同为
                  -- `TJ_ASSY`，故本次为零差异修正。
                  LEFT JOIN (SELECT ProcessCode, MIN(StageCode) AS StageCode
                             FROM ext_MES_ProcessCode_View
                             GROUP BY ProcessCode) pc
                    ON pc.ProcessCode = d.StorageCode
                  LEFT JOIN (SELECT DISTINCT ProcessCode
                             FROM ext_MES_ProcessCode_View
                             WHERE ERPProperty = 'XC') pcx
                    ON pcx.ProcessCode = d.StorageCode
                  WHERE d.Quantity > 0
                    AND d.MaterialCode IN (SELECT value FROM OPENJSON(@Json))
                    AND fa.Code         IN @FactoryCodes",
                new { Json = json, FactoryCodes = factoryCodes },
                db: DatabaseId.APS, commandTimeout: 300),
            chunkSize: JsonInSinglePassChunkSize,
            tag: $"1d-1 PI库存(物料={materialCodes.Count})");

        var map = new Dictionary<(string MaterialCode, string FactoryCode), List<PiInventoryFact>>();
        var excludedXcRows = 0;
        var excludedXcQty  = 0m;
        foreach (var row in rows)
        {
            // XC 仓不进 PiInventories（§176）。同一份 XC 由 LoadPiXcFactsAsync 按 PI 键单独送。
            if (row.IsXc == 1)
            {
                excludedXcRows++;
                excludedXcQty += row.Quantity;
                continue;
            }

            var key = (row.MaterialCode, row.FactoryCode);
            if (!map.TryGetValue(key, out var list))
            {
                list = new List<PiInventoryFact>();
                map[key] = list;
            }

            list.Add(new PiInventoryFact
            {
                WarehouseCode = row.WarehouseCode,
                Quantity      = row.Quantity,
                AvailableTime = null,
                SourceDocument = null,
                RelatedStageCode = row.StageCode,
                LocationCategory = string.IsNullOrWhiteSpace(row.StageCode) ? "UNKNOWN" : "STAGE_INVENTORY"
            });
        }

        if (excludedXcRows > 0)
        {
            _logger.LogInformation(
                "[Pegging][PI-XC] PiInventories 剔除 XC 仓明细 {Rows} 行 / Σ{Sum}（冻结 11 §176：XC 只走 PI 级 XcFacts）",
                excludedXcRows, excludedXcQty);
        }

        return map;
    }

    /// <summary>
    /// 装载 **PI 级** XC（线边仓）事实。
    ///
    /// 源：APS 本地夜间快照 `InventoryFact_PiXc`（每日 00:37 由 `sp_SyncPiXcSnapshot` 全量刷新；
    ///     再上游 = ODS `MES_Integration.dbo.ODS_ProductionInstructionInventoryBalance`
    ///     ← APS 同义词 `ext_ODS_ProductionInstructionInventoryBalance`，八列见冻结 `08` §8a）。
    ///     **Run 期不直连链接服务器**（用户 2026-10-22 定）：跨机超时/断链会拖垮整轮，
    ///     且单批 TRUNCATE+INSERT 天然满足 `11 §175`「固定单一 Run 事实包、不混采集时点」。
    ///
    /// 键：`ProductionInstructionNo` —— 冻结 `11 v2.5 §174`：「以本Run活跃PI键 + 真实Factory + 标准Material
    ///     成对批量读取，禁止按 Material+Factory 反拆 PI」；`04 V3.31 PI-XC-01`：PI001=100 / PI002=80
    ///     必须保持 100/80，绝不能仅以 Material+Factory 推成各 180。
    ///
    /// 物料/厂只作**一致性校验**（同 PM 0910 对 Transit 的口径）：与 PI 身份行不符的行不计入，
    /// 并记账，避免源侧串号被静默吸收。
    ///
    /// 缺源/过期**不回落**、也**不当零**：按 `11 §174` ＋ `06 :471`（「技术不可用不能伪装零，
    /// 也不能用普通库存填补」）抛错拒绝本轮，而不是把 0 条 XC 灌进 Position。
    /// 只有「快照成功刷新且确实没有该 PI 的行」才按真实零处理。
    /// </summary>
    private async Task<IReadOnlyDictionary<string, List<XcFact>>>
        LoadPiXcFactsAsync(IReadOnlyList<PiRemainingFact> piFacts, DateTime dataCutoffTime, CancellationToken ct)
    {
        var map = new Dictionary<string, List<XcFact>>(StringComparer.Ordinal);
        if (piFacts.Count == 0)
            return map;

        // ── 闸门①：快照表必须存在 ──
        var tableExists = (await _connectionManager.QueryAsync<int>(
            "SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'dbo.InventoryFact_PiXc')",
            db: DatabaseId.APS)).FirstOrDefault() > 0;

        if (!tableExists)
        {
            throw new InvalidOperationException(
                "[Pegging][PI-XC] V1_DEPLOY_BLOCKER_PI_XC：快照表 InventoryFact_PiXc 不存在 ⇒ 拒绝本轮。" +
                "按冻结 11 §174 / 06:471，缺源不得按 XC=0 计算、也不得用普通库存填补。" +
                "执行 Database/Scripts/APS/03_SQL脚本/APS_create_InventoryFact_PiXc_and_sp_20261022.sql 并跑一次 " +
                "sp_SyncPiXcSnapshot 后重试。");
        }

        // ── 闸门②③：最近一次刷新的状态与新鲜度 ──
        // 刷新失败时 SP 不清表（保留上一批），所以「表里有数据」不等于「数据可信」，
        // 必须以最近一次 ETL 结果 + 采集时刻为准。
        var lastRun = (await _connectionManager.QueryAsync<PiXcSyncLogRow>(
            @"SELECT TOP 1 BatchNo, Status, CreatedAt, Message
              FROM APS_ETL_Log
              WHERE Step = N'sp_SyncPiXcSnapshot'
              ORDER BY CreatedAt DESC",
            db: DatabaseId.APS, commandTimeout: 60)).FirstOrDefault();

        if (lastRun == null)
        {
            throw new InvalidOperationException(
                "[Pegging][PI-XC] V1_DEPLOY_BLOCKER_PI_XC：APS_ETL_Log 中没有任何 sp_SyncPiXcSnapshot 记录 ⇒ " +
                "PI 级 XC 从未成功刷新，拒绝本轮（缺源不得当零）。");
        }

        if (!string.Equals(lastRun.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(lastRun.Status, "WARN", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[Pegging][PI-XC] PI 级 XC 快照最近一次刷新未成功（{lastRun.Status} @ {lastRun.CreatedAt:yyyy-MM-dd HH:mm:ss}）：" +
                $"{lastRun.Message} ⇒ 拒绝本轮（缺源不得当零）。");
        }

        // 新鲜度：快照采集时刻不得早于本 Run 数据截止时点超过 1 天。
        // 口径出处 —— 冻结 11 §175 要求「固定单一 Run Position 事实包」且与 DataCutoffTime 相容；
        // 采集节拍为每日一次（00:37），跨日即说明该 Run 用的是隔夜以前的切片。
        var stalenessLimit = TimeSpan.FromHours(24);
        if (dataCutoffTime != default && lastRun.CreatedAt < dataCutoffTime - stalenessLimit)
        {
            throw new InvalidOperationException(
                $"[Pegging][PI-XC] PI 级 XC 快照过期：最近刷新 {lastRun.CreatedAt:yyyy-MM-dd HH:mm:ss}，" +
                $"本 Run 数据截止 {dataCutoffTime:yyyy-MM-dd HH:mm:ss}，已超过 {stalenessLimit.TotalHours} 小时 ⇒ " +
                "拒绝本轮（过期切片不得混入本 Run Position 事实包）。");
        }

        // PI → 允许的 (物料, 厂) 集合。同一 PI 在 A/B 两源若给出多组身份，全部保留为合法校验集。
        var identities = new Dictionary<string, HashSet<(string MaterialCode, string FactoryCode)>>(StringComparer.Ordinal);
        foreach (var f in piFacts)
        {
            if (string.IsNullOrWhiteSpace(f.ProductionInstructionNo))
                continue;

            if (!identities.TryGetValue(f.ProductionInstructionNo, out var set))
            {
                set = new HashSet<(string, string)>();
                identities[f.ProductionInstructionNo] = set;
            }
            set.Add((f.MaterialCode, f.FactoryCode));
        }

        // 按本 Run 活跃 PI 键批量读取（`11 §174`）。本地表 + 单 JSON 参数 + OPENJSON，
        // 既不是逐 PI N+1，也不受 2,100 参数上限约束。
        var piKeys = identities.Keys.ToList();
        var rows = await QueryJsonInAsync(piKeys, json =>
            _connectionManager.QueryAsync<PiXcLoadRow>(
                @"SELECT ProductionInstructionNo, MaterialCode, FactoryCode, WarehouseCode,
                         StageCode, Quantity, SnapshotTime
                  FROM InventoryFact_PiXc
                  WHERE ProductionInstructionNo IN (SELECT value FROM OPENJSON(@Json))",
                new { Json = json },
                db: DatabaseId.APS, commandTimeout: 300),
            // 2026-10-26：同 1d-5 族 —— 默认分块 20,000 会让 9 万+ PI 拆成 5 批、每批扫一遍表。
            // 抬到 20 万恒定单批，只扫一遍。不加 HASH 提示（本表无大表连接，子查询形态足够）。
            chunkSize: JsonInSinglePassChunkSize,
            tag: $"PI级XC(PI={piKeys.Count})");

        var accepted        = 0;
        var unknownPi       = 0;
        var identityMismatch = 0;
        var nonPositive     = 0;

        foreach (var row in rows)
        {
            if (row.Quantity <= 0m)
            {
                nonPositive++;
                continue;
            }

            if (!identities.TryGetValue(row.ProductionInstructionNo, out var allowed))
            {
                unknownPi++;
                continue;
            }

            if (!allowed.Contains((row.MaterialCode, row.FactoryCode)))
            {
                identityMismatch++;
                continue;
            }

            if (!map.TryGetValue(row.ProductionInstructionNo, out var list))
            {
                list = new List<XcFact>();
                map[row.ProductionInstructionNo] = list;
            }

            list.Add(new XcFact
            {
                ProductionInstructionNo = row.ProductionInstructionNo,
                XcWarehouseCode         = row.WarehouseCode,
                Quantity                = row.Quantity,
                RelatedStageCode        = string.IsNullOrWhiteSpace(row.StageCode) ? null : row.StageCode,
                // SnapshotTime 是「最后一次业务动作时刻」，不是可用时点 ⇒ 不映射 AvailableTime。
                AvailableTime           = null,
                SourceDocument          = null,
                SnapshotTime            = row.SnapshotTime
            });
            accepted++;
        }

        _logger.LogInformation(
            "[Pegging][PI-XC] PI 级 XC 装载（快照批 {Batch} @ {Captured:yyyy-MM-dd HH:mm:ss}）：" +
            "源 {Src} 行 / 入池 {Ok} 行 / 命中 PI {Pi} 个 / 活跃 PI {Active} 个" +
            " ⊕ 非正量 {NonPos} / 非本轮 PI {Unknown} / 物料-厂身份不符 {Mismatch}",
            lastRun.BatchNo, lastRun.CreatedAt,
            rows.Count, accepted, map.Count, identities.Count, nonPositive, unknownPi, identityMismatch);

        // 真实零 vs 静默零：源刷新成功但本 Run 一个 PI 都没命中时，必须能在日志里分辨是
        // 「本域确实没有 XC」还是「PI 键体系对不上」。冻结 06:471 只允许前者按零处理。
        if (accepted == 0 && identities.Count > 0 && rows.Count == 0)
        {
            _logger.LogInformation(
                "[Pegging][PI-XC] 本 Run 活跃 PI {Active} 个在快照内**无一命中**；快照本身非空（批 {Batch}，" +
                "详见 sp_SyncPiXcSnapshot 台账）⇒ 按真实零处理（冻结 06:471：来源成功、键完整且确无 XC）。",
                identities.Count, lastRun.BatchNo);
        }

        if (identityMismatch > 0)
        {
            _logger.LogWarning(
                "[Pegging][PI-XC] 有 {N} 行 XC 的 (物料,厂) 与其 PI 身份行不符，已排除（防源侧串号）。",
                identityMismatch);
        }

        return map;
    }

    /// <summary>
    /// 装载跨厂边事实（CrossFactoryEdge）。
    /// 链：ext_MES_APS_BOM_Workset_CrossFactoryEdge（18374 行）。
    /// 归组键 = ChildMaterialCode（PI 物料作为子件、其产品跨厂转运的边）；
    /// 组内按 (From,To) 四元组去重后自编号 EdgeSequence（物理表无此列）。
    /// </summary>
    private async Task<IReadOnlyDictionary<string, List<CrossFactoryEdgeFact>>>
        LoadCrossFactoryEdgesAsync(IReadOnlyList<WipStageLoadRow> wipStageRows, CancellationToken ct)
    {
        var materialCodes = wipStageRows.Select(r => r.MaterialCode).Distinct().ToList();

        // 2026-10-26（二次修正）：**本段不能走单 JSON 参数**。r13903 曾改为
        // `ChildMaterialCode IN (SELECT value FROM OPENJSON(@Json))` 单趟，PV190 实跑直接撞 APS 默认
        // `CommandTimeout=60` 超时（跨厂边是全流程唯一一处）。原因：该表在链接服务器上，
        // 远端 18,842 行对 `OPENJSON(33k 值的 JSON)` 的语义化谓词退化为逐行解析，成本随「远端行数 × JSON 长度」涨。
        // 现改为**整表一次拉回 + 本地过滤**：远端仅 18,842 行（实测 0.18s 拉全表），仍只 1 次往返，
        // 且不再把大 JSON 交给链接服务器。`ORDER BY` 原样保留 ⇒ 组内 EdgeSequence 编号与改前逐行一致。
        var materialSet = new HashSet<string>(materialCodes, StringComparer.Ordinal);
        var remoteRows = await _connectionManager.QueryAsync<CrossFactoryEdgeLoadRow>(
            @"SELECT ChildMaterialCode,
                     FromStageCode,
                     FromFactoryCode,
                     ToStageCode,
                     ToFactoryCode
              FROM ext_MES_APS_BOM_Workset_CrossFactoryEdge
              ORDER BY ChildMaterialCode, BatchNo, WorksetId, Id",
            null,
            db: DatabaseId.APS);
        var rows = remoteRows.Where(r => materialSet.Contains(r.ChildMaterialCode)).ToList();

        var map = new Dictionary<string, List<CrossFactoryEdgeFact>>();
        foreach (var group in rows.GroupBy(r => r.ChildMaterialCode))
        {
            var edges = new List<CrossFactoryEdgeFact>();
            var seen = new HashSet<(string FromStage, string FromFactory, string ToStage, string ToFactory)>();
            foreach (var row in group)
            {
                var tuple = (row.FromStageCode, row.FromFactoryCode, row.ToStageCode, row.ToFactoryCode);
                if (!seen.Add(tuple))
                    continue; // 多 BatchNo/WorksetId 的重复边去重

                edges.Add(new CrossFactoryEdgeFact
                {
                    FromStageCode   = row.FromStageCode,
                    FromFactoryCode = row.FromFactoryCode,
                    ToStageCode     = row.ToStageCode,
                    ToFactoryCode   = row.ToFactoryCode,
                    EdgeSequence    = edges.Count
                });
            }
            map[group.Key] = edges;
        }

        return map;
    }

    /// <summary>
    /// 将厂间在途事实按生产指示号精确归属到 PI（PM 0910 裁决 §十三，§11 Stage Handoff INTERPLANT_TRANSIT Position 强事实）。
    /// 归属键 = TransitDocumentNo（= SourceDocumentNo = PI No）；Material/目标厂仅做一致性校验，不得替代 PI 号归属（禁 Material+Factory 归组/按 PI 排序/按 RemainingQty 比例分摊）。
    /// 校验顺序（PM §十）：PI 号非空 → 命中唯一有效 PI → Material 一致 → 目标厂一致 → Quantity>0 → 归入该 PI 的 INTERPLANT_TRANSIT。
    /// 任一失败则该事实不作为已定位 Position 消费，记 Issue（TRANSIT_PI_*），对应剩余数量由 PI Position 闭合逻辑落入 UNLOCATED（不得删除 Supply / 转新增生产）。
    /// 同一 PI 多行聚合（PM §十一：TransitQty ≤ PI RemainingQty 由 5号位计算器闭合校验，不在此裁剪）。
    /// </summary>
    internal static (Dictionary<string, List<InterplantTransitFact>> ByPi, List<string> Issues)
        AttributeInterplantTransitToPi(
            IReadOnlyList<InterplantTransitFact> transits,
            IReadOnlyDictionary<string, (string MaterialCode, string FactoryCode)> piIdentities)
    {
        var byPi = new Dictionary<string, List<InterplantTransitFact>>(StringComparer.Ordinal);
        var issues = new List<string>();

        foreach (var t in transits)
        {
            var piNo = t.TransitDocumentNo;
            if (string.IsNullOrWhiteSpace(piNo))
            {
                issues.Add("TRANSIT_PI_NO_MISSING: 厂间在途事实缺少生产指示号（TransitDocumentNo 为空），无法归属 PI。");
                continue;
            }

            if (!piIdentities.TryGetValue(piNo, out var pi))
            {
                issues.Add($"TRANSIT_PI_NOT_FOUND: 在途单号 {piNo} 找不到对应 PI。");
                continue;
            }

            if (!string.Equals(t.MaterialCode, pi.MaterialCode, StringComparison.Ordinal))
            {
                issues.Add($"TRANSIT_PI_MATERIAL_MISMATCH: 在途单号 {piNo} 物料 {t.MaterialCode} ≠ PI 物料 {pi.MaterialCode}。");
                continue;
            }

            if (!string.Equals(t.TargetFactoryCode, pi.FactoryCode, StringComparison.Ordinal))
            {
                issues.Add($"TRANSIT_PI_FACTORY_MISMATCH: 在途单号 {piNo} 目标厂 {t.TargetFactoryCode} ≠ PI 工厂 {pi.FactoryCode}。");
                continue;
            }

            if (t.Quantity <= 0m)
            {
                issues.Add($"TRANSIT_PI_INVALID_QUANTITY: 在途单号 {piNo} 数量 {t.Quantity} ≤ 0。");
                continue;
            }

            if (!byPi.TryGetValue(piNo, out var list))
            {
                list = new List<InterplantTransitFact>();
                byPi[piNo] = list;
            }
            list.Add(t);
        }

        return (byPi, issues);
    }

    /// <summary>
    /// 装载厂间在途事实（Transit），仅 PI 级（§11 大工艺接续 Stage Handoff）。链：ext_ERP_InterplantInTransit_View（实测 0 行，待 5号位 ODS 数据）。
    /// 分流依据：SourceDocumentNo = [Order].OrderNo（PM 已确认 OrderNo 是唯一连接键）→ 仅取 OrderType=PRODUCTION_INSTRUCTION；
    /// OrderType=SALES_ORDER（含 CustomerSegment='跨厂' 的 SH）的在途 = 履行状态，走 INTER_FACTORY_ORDER 单一 Supply（量已并入 Order.Quantity），不进 PI Position。
    /// 返回扁平事实列表（不按键归组），由 AttributeInterplantTransitToPi 按 TransitDocumentNo（= PI No）精确归属（PM 0910 裁决，禁 Material+Factory 归组）。
    /// </summary>
    private async Task<IReadOnlyList<InterplantTransitFact>>
        LoadTransitFactsAsync(IReadOnlyList<WipStageLoadRow> wipStageRows, int planVersionId, CancellationToken ct)
    {
        var materialCodes = wipStageRows.Select(r => r.MaterialCode).Distinct().ToList();
        var factoryCodes  = wipStageRows.Select(r => r.FactoryCode).Distinct().ToList();

        // 🔴 JOIN 键 `o.OrderNo` 是**跨 PlanVersionId 重复的业务键**（实测同一 OrderNo 平均在 2.97 个版本里各存一份）。
        // 不过滤 ⇒ 每条在途视图行按命中的版本份数被放大，且 `o.OrderType` 可能取自历史版本副本。
        // 必须锁死本 Run 的 PlanVersionId。（该视图实测 0 行，本改动为结构性堵漏。）
        // 2026-10-26：同 1d-1 —— `ext_ERP_InterplantInTransit_View` 是链接服务器同义词（该视图实测 0 行），
        // 17 次往返纯属跨网开销（PV190 实测本段 5,610ms）。改单 JSON 参数，只留 1 次往返，不加哈希提示。
        var rows = await QueryJsonInAsync(materialCodes, json =>
            _connectionManager.QueryAsync<TransitLoadRow>(
                @"SELECT t.MaterialCode,
                         t.FactoryCode,
                         t.SourceFactoryCode,
                         t.Quantity,
                         t.ETA,
                         t.ReleaseDate,
                         t.SourceDocumentNo,
                         o.OrderType
                  FROM ext_ERP_InterplantInTransit_View t
                  INNER JOIN [Order] o ON o.OrderNo = t.SourceDocumentNo
                  WHERE o.PlanVersionId = @PlanVersionId
                    AND t.Quantity > 0
                    AND o.OrderType = 'PRODUCTION_INSTRUCTION'
                    AND t.MaterialCode IN (SELECT value FROM OPENJSON(@Json))
                    AND t.FactoryCode  IN @FactoryCodes",
                new { PlanVersionId = planVersionId, Json = json, FactoryCodes = factoryCodes },
                db: DatabaseId.APS, commandTimeout: 300),
            chunkSize: JsonInSinglePassChunkSize,
            tag: $"1d-4 厂间在途(物料={materialCodes.Count})");

        var facts = new List<InterplantTransitFact>(rows.Count);
        foreach (var row in rows)
        {
            facts.Add(new InterplantTransitFact
            {
                TransitDocumentNo    = row.SourceDocumentNo,
                MaterialCode         = row.MaterialCode,
                SourceFactoryCode    = row.SourceFactoryCode,
                TargetFactoryCode    = row.FactoryCode,
                Quantity             = row.Quantity,
                EstimatedArrivalTime = row.Eta,
                SourceDocument       = row.SourceDocumentNo,
                ShippedAt            = row.ReleaseDate
            });
        }

        return facts;
    }

    /// <summary>厂间出荷指示（SH）单一供给事实：直接来自 [Order]（CustomerSegment='跨厂' 的 SALES_ORDER）。</summary>
    private sealed class InterFactoryShipmentFact
    {
        /// <summary>SH号 = OrderNo</summary>
        public string ShipmentNo { get; set; } = string.Empty;
        public string MaterialCode { get; set; } = string.Empty;
        public int MaterialId { get; set; }
        /// <summary>目标/到货厂代码（= Order.FactoryCode）</summary>
        public string TargetFactoryCode { get; set; } = string.Empty;
        /// <summary>目标/到货厂 Id（= Order.FactoryId）</summary>
        public int TargetFactoryId { get; set; }
        /// <summary>源/发货厂 Id（= Order.SourceFactoryId；装载链已就绪，重跑夜间编排器重载订单后落库）</summary>
        public int? SourceFactoryId { get; set; }
        /// <summary>指令总量（= Order.Quantity，整单一次入库）</summary>
        public decimal InstructionQty { get; set; }
    }

    /// <summary>
    /// 装载厂间出荷指示（INTER_FACTORY_ORDER，SH级）事实。
    /// v1.0 口径：SH 本身是 Order（OrderType=SALES_ORDER + CustomerSegment='跨厂'，OrderNo=SH号），
    /// 单一 Supply 身份，量=Order.Quantity（指令总量），不再从 Transit/Received 视图归并两段。
    ///
    /// 🔴 必须按 <paramref name="planVersionId"/> 过滤：`[Order]` 按 PlanVersionId 分区，**同一张 SH
    /// 在多个计划版本（夜间/手工各一次快照）里各存一份**（实测同一 OrderNo 同时存在于 PV2/PV5/PV24）。
    /// 不过滤 ⇒ 本次 run 把同一批 SH 的**历史版本副本一并收进来**，供给凭空翻倍。
    /// </summary>
    private async Task<IReadOnlyList<InterFactoryShipmentFact>> LoadInterFactoryShipmentsAsync(
        int planVersionId, CancellationToken ct)
    {
        var rows = await _connectionManager.QueryAsync<InterFactoryShipmentFact>(
            $@"SELECT o.OrderNo         AS ShipmentNo,
                     m.MaterialCode,
                     o.MaterialId,
                     f.Code           AS TargetFactoryCode,
                     o.FactoryId      AS TargetFactoryId,
                     o.SourceFactoryId,
                     o.Quantity       AS InstructionQty
              FROM [Order] o
              INNER JOIN Material m ON m.Id = o.MaterialId
              INNER JOIN Factory  f ON f.Id = o.FactoryId
              WHERE o.PlanVersionId = @PlanVersionId
                AND o.CustomerSegment = N'{CustomerSegmentCodes.InterFactory}'
                AND o.OrderType = 'SALES_ORDER'
                AND o.Quantity > 0",
            new { PlanVersionId = planVersionId },
            db: DatabaseId.APS);

        return rows.ToList();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Routing 三件套 + 部门归属上下文装载（PM 裁定：最小 B）
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 装载「本次 BOM 上下文下的有效 Stage」——MaterialId → 该物料本次需经过的 StageCode，
    /// 并**每物料收敛为恰好一条真实有序链**（供 1号位 判 Stage 先后 + `RequiredStageCode` 截断）。
    ///
    /// 【依据】PM《BOM取用_Pegging_Stage_Routing完整链路说明》§二十一/§二十二：
    ///   Stage 的存在性与顺序由 `APS_BOM_STAGE_PATH_RAW`（本次 BOM 上下文）决定，
    ///   **不得从 Routing 反推、不得按 MaterialCode 全局聚合**。本方法按「当前批次的 StagePath」取：
    ///     · ROOT 行（ParentMaterialCode=NULL）= 订单根产品自身的工艺路径；
    ///     · EDGE 行（父件→子件）= 该子件自身需经过的工艺路径（做到 IsSupplyThreshold 即可供父件）。
    ///
    /// 🔴 【2026-10-16 J12-02 改造 —— 原「取并集」已废除】
    ///   原实现把 ROOT ∪ EDGE、跨父件、跨边实例、跨请求**并成一个集合**（并在类注释里自述其「保守」理由）。
    ///   该形态**违反冻结明文**：`07 v5.1.13 :416`／`:723`「必须按 `StageScopeType` 区分查询（不得混查EDGE+ROOT）」
    ///   与 `:722`／`:415`「多条不一致**取最长路径+记WARNING（不静默并集）**」。
    ///   并集在**装载第一步**就销毁了 `StageScopeType` 这条区分轴 ⇒ 下游无法再区分 ⇒ 必须在载体侧（本处）修。
    ///   现由纯函数 <see cref="EffectiveStageChainResolver"/> 按「请求＋边实例」隔离后收敛为**一条真实链**：
    ///   不并集、不补步、不改 Seq；同 scope 多条 ⇒ 取最长（`:722`）＋**告警**（`:416` 的可见性落实）。
    ///
    /// 【为什么恒「每物料一条」】1号位 `PhaseOneConstraintBuilder.BuildStageChainIndex` 与
    ///   `StageTimingNodeBuilder` 都按 `MaterialId` 建字典 ⇒ 同一 `MaterialId` 出两条会被**静默覆盖**
    ///   （只是把静默丢弃从装载层搬到消费层）⇒ 「每物料恰好一条」是硬不变量，`StageSequenceChain` DTO
    ///   因此**不需要扩展**（与 0号位 J12-02「先形成一次运行中唯一合法、隔离后的 Stage 链，再交 1号位
    ///   零 DB Solver 消费」逐字一致）。
    ///
    /// 【无 StagePath 时的兜底】若某物料在本批 StagePath 里无记录（数据缺口 / 采购件），
    ///   则**不为其限定 Stage**（结果里不含该 MaterialId），由调用方回退「按 MaterialId 全量装载」。
    ///
    /// 🔴 【2026-10-17 J14-01/02/03 改造 —— 角色与选行】
    ///   · **角色**＝需求自身结构（`LogicalProductionDemand.IsRootDemand`：本订单顶层根 = ROOT，其余 = EDGE），
    ///     **不再**由「该物料是否有供给阈值」反推（阈值 NULL 的子件此前被错判成 ROOT）；
    ///   · **选行前置**＝需求锚点 `BomNo`（`ResolvedBOMNO`）＋ ROOT 的 `RepWorksetId` ＋ EDGE 的父边集合，
    ///     全部来自 `OrderBomRequestLink` 与按 BOMNO 索引的 `BomSnapshot`；
    ///   · **不回退另一 scope**：首选 scope 按选择键收窄后无行 ⇒ 该需求无链（交 StageLeadTime/未排真相）。
    /// </summary>
    /// <param name="demands">本次运行的全部逻辑需求（角色与请求锚点随需求携带；同一物料可有多个需求）。</param>
    /// <param name="bomSnapshot">本 PV 的 BOM 快照（提供 `ParentsByBomChild` / `ThresholdsByBomChild` 按请求索引）。</param>
    private async Task<EffectiveStageLoad> LoadEffectiveStageKeysAsync(
        int planVersionId,
        IReadOnlyList<LogicalProductionDemand> demands,
        BomSnapshot bomSnapshot,
        CancellationToken ct)
    {
        // 物料集合由**需求**派生（本方法的核心入参已是需求本身：角色与请求锚点随需求携带）。
        var materialIds = demands
            .Select(d => d.MaterialId)
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        if (materialIds.Count == 0)
            return EffectiveStageLoad.Empty;

        // 1) 当前批次：**直接沿用 BOM 快照已解析的关联批**（同一处解析 ⇒ 两处口径物理上不可能漂移）。
        //    多批次诊断也随取批统一迁到 `LoadBomSnapshotAsync`，此处不再重查、不再重复告警。
        var batchNo = bomSnapshot.BatchNo ?? string.Empty;

        if (string.IsNullOrEmpty(batchNo))
        {
            // 0号位 2026-10-17 §三.D：这是「**BOM 输入缺失**」，不是「业务合法空集」。
            //   原实现 `return EffectiveStageLoad.Empty` ⇒ 与「本域确实不需要 Stage」在调用方**无法区分**
            //   （下游按 `ByMaterial.Count == 0` 判定「不做 Stage 过滤」），属静默放行。
            //   现返回带不可用原因的装载结果：**过滤口径不变**（仍按「物料未命中 StagePath 不过滤」的既有规则），
            //   但该状态必须经 Issue/日志透出，并由调用方按缺事实处置。
            _logger.LogError(
                "[Pegging] 有效 Stage 不可用：本 PlanVersion 无 `OrderBomRequestLink` 关联批（PlanVersionId={PlanVersionId}，" +
                "需求物料数={MaterialCount}）⇒ 非「业务合法空集」；Stage 过滤将按既有「物料未命中即不过滤」口径放行，" +
                "该结果**不得**被当作已完成 BOM 上下文校验",
                planVersionId, materialIds.Count);

            return EffectiveStageLoad.Unavailable(
                $"本 PlanVersion 无 OrderBomRequestLink 关联批（PlanVersionId={planVersionId}）",
                new[]
                {
                    new EffectiveStageIssue(
                        string.Empty,
                        StageChainIssueNoBomBatchLink,
                        $"BOM 输入缺失：PlanVersionId={planVersionId} 无关联批 ⇒ 有效 Stage 未装载（需求 {demands.Count} 条）")
                });
        }

        if (bomSnapshot.InputState == BomInputState.LinkedBatchNoRows)
        {
            _logger.LogError(
                "[Pegging] 有效 Stage 上下文不完整：关联批 {BatchNo} 在 APS_BOM_RAW 无任何行（PlanVersionId={PlanVersionId}）" +
                "⇒ Stage 链虽仍按 StagePath 装载，但父边/阈值索引为空（BOM 输入缺失）",
                batchNo, planVersionId);
        }

        // 【2026-10-26 细分埋点】只读实测：StagePath 12,253 值单趟 2.56s、2,000/块 ≈1.75s ⇒ SQL 约 2s，
        //   其余 ~6.8s 在**逐需求选行的内存计算**。故本方法保持分片原样，改报细分。
        var swStage = System.Diagnostics.Stopwatch.StartNew();

        // 2) 物料编码（StagePath 用 MaterialCode 字符串）
        //    分片：`materialIds` = 本域需求物料，真实规模上千（2026-09-29 真跑：需求 42,193 条 / 物料数千）；
        //    紧邻的 ③ 步对**由它派生的** `materialCodes` 已经在走 QueryChunkedInAsync —— 同源同量级的这一条却漏了。
        //    ⚠ 2026-10-26 实测**不改单 JSON**：本方法三处 `IN @` 换 `OPENJSON` 后反而更慢
        //      （2,000/块 = 250ms × 7 ≈ 1.75s；单趟 12,253 值需 2.56s —— 本表按 `(BatchNo, ChildMaterialCode)`
        //       逐块 seek 就能收敛，块数不是这里的杠杆）。`5 有效Stage装载` 的 8,838ms 主要花在**逐需求选行的内存计算**，
        //      不在 SQL。故保持分片原样，别再照搬 1d-5 的改法。
        var materialCodes = (await QueryChunkedInAsync(materialIds, chunk =>
            _connectionManager.QueryAsync<string>(
                "SELECT MaterialCode FROM Material WHERE Id IN @Ids",
                new { Ids = chunk },
                db: DatabaseId.APS))).ToList();

        if (materialCodes.Count == 0)
        {
            // 需求物料在 `Material` 主档里一条都查不到 ⇒ 事实缺失，不是「本次无需 Stage」。
            _logger.LogError(
                "[Pegging] 有效 Stage 不可用：{Count} 个需求物料在 Material 主档均不存在（PlanVersionId={PlanVersionId}）",
                materialIds.Count, planVersionId);

            return EffectiveStageLoad.Unavailable(
                $"{materialIds.Count} 个需求物料在 Material 主档均不存在（PlanVersionId={planVersionId}）",
                new[]
                {
                    new EffectiveStageIssue(
                        string.Empty,
                        StageChainIssueNoMaterialMaster,
                        $"Material 主档缺失：需求物料 {materialIds.Count} 个（PlanVersionId={planVersionId}）")
                });
        }

        // 3) 取本批 StagePath 的**行实例**（**绝不聚合、绝不并集**）。
        //    【2026-10-16 J12-02 改造，行为变更】原实现 =
        //      `SELECT ChildMaterialCode, StageCode, MIN(StageSeq) … GROUP BY ChildMaterialCode, StageCode`
        //      ⇒ ROOT ∪ EDGE、跨父件、跨边实例、跨请求**在装载第一步就被并成一个集合**，
        //        `StageScopeType` 这条区分轴当场销毁 ⇒ 下游（1号位）无论怎么查都**无法**再区分。
        //      ⇒ 收敛改由纯函数 `EffectiveStageChainResolver` 做（见其类注释引的冻结行号）。
        //    隔离列：
        //      · `StageScopeType` —— 区分轴本体（`07 :416`／`:723`／`:724`）；
        //      · `ParentMaterialCode` —— EDGE 的父边（`07 :724` EDGE 式带 `@Parent`；ROOT 恒 NULL，`07 :414`）；
        //      · `WorksetId` —— 「请求＋边实例」第二层锚点（`08 v5.1.8.7 :2008` 冻结 DDL 本就要求该列；
        //        `08 :359` 定 ROOT 语义 = 该 RequestDetail 下 Level=1 代表锚点 `MIN(Workset.Id)`；
        //        `08 :459-463` 定 EDGE 语义 = 该行所属**具体 BOM 边实例**）。
        //      · `BOMNO` —— `WorksetId` 缺失（旧批次）时的退化实例键。
        //    ⚠【部署顺序】本 SELECT 起引用 `WorksetId` ⇒ **目标表必须先补上该列**：
        //      `Database/Scripts/APS/03_SQL脚本/APS_BOM_STAGE_PATH_RAW_add_WorksetId_col_20261016.sql`
        //      先上新二进制、后跑脚本 ⇒ 本查询直接抛「列名无效」、整条 Pegging 断在这一步。
        //    行数可控：实测 `APS_BOM_STAGE_PATH_RAW`（2,384,758 行 / 79,554 子件）单物料最多 3,632 个去重行实例。
        var stageRows = await QueryChunkedInAsync(materialCodes, chunk =>
            _connectionManager.QueryAsync<StagePathInstanceRow>(
                @"SELECT DISTINCT
                        ChildMaterialCode,
                        StageScopeType,
                        ParentMaterialCode,
                        WorksetId,
                        BOMNO,
                        StageSeq,
                        StageCode
                  FROM APS_BOM_STAGE_PATH_RAW
                  WHERE BatchNo = @BatchNo
                    AND ChildMaterialCode IN @Codes
                    AND StageCode IS NOT NULL",
                new { BatchNo = batchNo, Codes = chunk },
                db: DatabaseId.APS));

        // 4) MaterialCode → MaterialId 反查，组装结果
        //    分片：同上 `materialCodes` 与 `materialIds` 同量级（上千）⇒ 裸 `IN @Codes` 同样会撞 2100。
        var idByCode = (await QueryChunkedInAsync(materialCodes, chunk =>
            _connectionManager.QueryAsync<MaterialIdCodeRow>(
                "SELECT Id, MaterialCode FROM Material WHERE MaterialCode IN @Codes",
                new { Codes = chunk },
                db: DatabaseId.APS)))
            .ToDictionary(r => r.MaterialCode, r => r.Id, StringComparer.Ordinal);

        var stageSqlMs = swStage.ElapsedMilliseconds;
        swStage.Restart();

        // 5) **逐需求**选行 → **按物料**汇总（2026-10-17 J14-01/02/03 改造）
        //    · 角色＝需求自身结构（`IsRootDemand`），**不再**由「该物料有没有供给阈值」反推
        //      （阈值 NULL 的子件此前被错判成 ROOT —— J14-03）；
        //    · 选行前置＝需求锚点 `BomNo`（`ResolvedBOMNO`）＋ ROOT 的 `RepWorksetId` ＋ EDGE 的父边集合
        //      （`BomSnapshot.ParentsByBomChild` 按 BOMNO 索引）—— 别的请求/别的父边的链不得被取到（J14-02）；
        //    · 首选 scope 按选择键收窄后无行 ⇒ 该需求**无链**，绝不回退另一 scope（J14-01）；
        //    · 同一物料在本次运行内若收敛出**多条不同链**（典型：既作子件被消耗、又作顶层独立需求）⇒
        //      本载体每物料仅容一条（1号位 按 `MaterialId` 建字典、last-wins 静默覆盖）⇒ **隔离该物料**
        //      （不出链）＋ 结构化诊断，绝不任取一条。
        //    两条产出：集合（供 Routing 三件套存在性过滤）+ 有序链（供 1号位 判断 Stage 先后）。
        var rowsByCode = new Dictionary<string, IReadOnlyList<EffectiveStageChainResolver.InstanceRow>>(StringComparer.Ordinal);
        foreach (var group in stageRows
                     .Where(r => !string.IsNullOrEmpty(r.StageCode))
                     .GroupBy(r => r.ChildMaterialCode, StringComparer.Ordinal))
        {
            rowsByCode[group.Key] = group
                .Select(r => new EffectiveStageChainResolver.InstanceRow(
                    r.StageScopeType, r.ParentMaterialCode, r.WorksetId, r.BOMNO, r.StageSeq, r.StageCode))
                .ToList();
        }

        // MaterialId → MaterialCode（idByCode 的反向索引；重复编码取首个，Index 唯一性由 Material 主键保证）
        var codeByMaterialId = new Dictionary<int, string>();
        foreach (var pair in idByCode)
            codeByMaterialId.TryAdd(pair.Value, pair.Key);

        var picks = new List<DemandChainPick>();
        var issues = new List<EffectiveStageIssue>();

        var noRowDemands = 0;            // 本批 StagePath 无该物料的行（数据缺口/采购件 ⇒ 合法「不限定 Stage」）
        var missingScopeDemands = 0;     // 有行，但按选择键收窄后首选 scope 无行（J14-01：不回退）
        var noAnchorDemands = 0;         // 无请求锚点（`BomNo` 空）⇒ 无权威身份（J15-01 状态 c）
        var unverifiedIdentityDemands = 0; // 有 `BOMNO` 但缺第二层锚点 ⇒ 内容等价兼容（J15-01 状态 b）
        var parentEdgeScopedDemands = 0;   // EDGE 仅按父边收窄（J16-01：非请求级可信，不得称 Anchored）
        var ambiguousIdentityDemands = 0;  // 身份未证实且候选不一致 ⇒ 不出链（J16-01 准入）
        // J17-01：身份歧义物料必须并入 **Routing 拒绝集**（0号位 第十七轮）。
        //   仅「不出链」不够——`ByMaterial` 无键时 Routing 走「未命中即不过滤」放行，
        //   物料级工序会整片进 Solver、在不具权威 Stage 上下文的前提下产出 Task（隔离在日志成立、在 Task 表不成立）。
        //   与角色冲突同属「**已有事实且互相冲突**」；`NoRows`／`无锚点` 属「**事实缺失**」
        //   （0号位 §J17-01 明确的「无事实允许兼容」），保持现口径放行、由未排真相承担。
        //
        // J17-01 追加（0号位 2026-10-17 第十八轮）：`MissingPreferredScope` **不属于**「无事实」——
        //   本批 StagePath **有**该物料的行，只是不在本需求的请求（`BOMNO`）／角色（scope）下。
        //   实测（2026-10-17 14:57，批次 REQ_20261008_84f845c89e35461）：874,337 条 BOM 边中
        //     309,368 条有精确 (BOMNO,父件,子件) EDGE 链（0 条父边错配）；563,283 条子件**全无** StagePath 行（采购件类，属 NoRows）；
        //     **199 个物料**属「有行但不在本请求」，其中 **158 个带活跃 RoutingOperation（857 条）**——
        //     若沿用「缺键即不过滤」，这 158 个物料的全工序会整片进 Solver（含其作为别家产品时的 ROOT 完工工序）。
        //   落地口径：**仅当该物料最终没有任何链**（另一需求已出链则不拦，任务本就共用那条链）时并入拒绝集，
        //     使其 Routing 三件套被剔除 ⇒ 不产 Task、按未排真相计入 U（与 `EffectiveStageChainResolver` 类注释
        //     「缺链的合法降级交未排真相」一致）。
        var ambiguousMaterials = new HashSet<int>();
        var missingScopeMaterials = new HashSet<int>();

        // J15-01：区分「**个别**需求缺锚点」与「**整批**缺锚点载体」。前者是无权威身份的歧义 ⇒ 不产链；
        //   后者（例如该 PV 的 `OrderBomRequestLink` 根本没装载）属系统性数据缺口，须显性告警，
        //   否则单域会因载体未装载而整体无链、且看不出原因。
        var anchoredDemandCount = demands.Count(d => !string.IsNullOrWhiteSpace(d.BomNo));
        var systemicNoAnchor = demands.Count > 0 && anchoredDemandCount == 0;
        if (systemicNoAnchor)
        {
            _logger.LogWarning(
                "[Pegging] 本批需求**全部无请求锚点**（`OrderBomRequestLink` 未装载或未覆盖该 PlanVersion，PlanVersionId={PlanVersionId}，" +
                "需求数={Count}）⇒ Stage 链只能走系统性兼容路径（`IdentityState=NoAnchor`，按 BOMNO 以外的全量行收敛）。" +
                "该路径产出的链**不得**被当作已实现请求隔离；须待夜间接货重灌 `OrderBomRequestLink` 后复核。",
                planVersionId, demands.Count);
        }
        var worksetFilteredDemands = 0;  // ROOT 侧真正按 `RepWorksetId` 收窄过（当前批次该列未回灌时恒 0）
        var rejectedChainDemands = 0;    // 同 scope 内另有未选用的合法链（取最长 + 必须报出）
        var divergentScopeDemands = 0;   // 另一 scope 的链与选用链不同（仅计数，不参与选定）
        var edgeMultiChainDemands = 0;   // EDGE 同子件不同父边（**另一类**，超本载体表达能力 ⇒ 必须告警）

        foreach (var demand in demands)
        {
            if (!codeByMaterialId.TryGetValue(demand.MaterialId, out var code))
                continue;

            if (!rowsByCode.TryGetValue(code, out var rows))
            {
                noRowDemands++;
                issues.Add(new EffectiveStageIssue(code, StageChainIssueNoRows,
                    $"本批 StagePath 无该物料行（BatchNo={batchNo}，DemandKey={demand.DemandKey}）"));
                continue;
            }

            // EDGE 侧父边集合：本需求所属 BOMNO 下、该子件的父件集（ROOT 侧不使用；ROOT 父件恒 NULL）
            IReadOnlyCollection<string>? parents = null;
            if (!string.IsNullOrWhiteSpace(demand.BomNo)
                && bomSnapshot.ParentsByBomChild.TryGetValue(demand.BomNo.Trim(), out var parentsByChild))
            {
                parentsByChild.TryGetValue(code, out parents);
            }

            var prefersEdge = !demand.IsRootDemand;
            var pick = EffectiveStageChainResolver.Resolve(
                rows,
                prefersEdge,
                new EffectiveStageChainResolver.SelectionContext(demand.BomNo, demand.RepWorksetId, parents),
                allowUnanchoredCompatibility: systemicNoAnchor);

            if (pick is null)
            {
                noRowDemands++;
                continue;
            }

            // J15-01／J16-01：身份可信度留痕 —— 只有 Anchored（真实请求键等值）才算「已实现请求隔离」。
            switch (pick.IdentityState)
            {
                case EffectiveStageChainResolver.RequestIdentityState.Anchored:
                    break;
                case EffectiveStageChainResolver.RequestIdentityState.ParentEdgeScoped:
                    // EDGE 只做到边维度收窄（0号位 第十六轮 J16-01：不得称 Anchored）
                    parentEdgeScopedDemands++;
                    break;
                case EffectiveStageChainResolver.RequestIdentityState.UnverifiedRequestIdentity:
                    unverifiedIdentityDemands++;
                    break;
                default:
                    noAnchorDemands++;
                    break;
            }

            // J16-01 准入：身份未证实且候选链内容不一致 ⇒ **不出链**（不得取最长冒充本请求的路径），
            //   Q/U 不受影响；全部候选签名进诊断供取证。
            if (pick.IdentityAmbiguous)
            {
                ambiguousIdentityDemands++;
                ambiguousMaterials.Add(demand.MaterialId);
                issues.Add(new EffectiveStageIssue(code, StageChainIssueIdentityAmbiguous,
                    $"请求实例未经证明（IdentityState={pick.IdentityState}）但候选链内容不一致（{pick.RejectedChainKeys.Count} 条）" +
                    $"⇒ 本需求**不出链**，交 StageLeadTime／未排真相；候选={string.Join(" ; ", pick.RejectedChainKeys)}" +
                    $"（BomNo={demand.BomNo ?? "(无锚点)"}，RepWorksetId={demand.RepWorksetId?.ToString() ?? "NULL"}，" +
                    $"角色={(prefersEdge ? "EDGE" : "ROOT")}，DemandKey={demand.DemandKey}，BatchNo={batchNo}）"));
                continue;
            }

            if (pick.IdentityState == EffectiveStageChainResolver.RequestIdentityState.NoAnchor
                && pick.MissingPreferredScope)
            {
                // 状态 c（严格口径）：三键全空 ⇒ 无权威身份，**无链**；Q/U 不受影响，按未排真相处理。
                issues.Add(new EffectiveStageIssue(code, StageChainIssueNoRequestAnchor,
                    $"三键全空（BomNo/RepWorksetId/父边均空）⇒ 无权威请求身份，本需求不出链（DemandKey={demand.DemandKey}，" +
                    $"BatchNo={batchNo}）；Q/U 不受影响，按未排真相处理。"));
                continue;
            }

            if (pick.WorksetIdFilterApplied)
                worksetFilteredDemands++;

            if (pick.MissingPreferredScope)
            {
                missingScopeDemands++;
                missingScopeMaterials.Add(demand.MaterialId);
                issues.Add(new EffectiveStageIssue(code, StageChainIssueMissingPreferredScope,
                    $"首选 scope={pick.ScopeType} 按选择键收窄后无行（BomNo={demand.BomNo ?? "(无锚点)"}，" +
                    $"RepWorksetId={demand.RepWorksetId?.ToString() ?? "NULL"}，父边={(parents is null ? "(无)" : string.Join(",", parents))}，" +
                    $"BatchNo={batchNo}）；另一 scope 链条数={pick.OtherScopeDistinctChainCount}，按 J14-01 不回退。DemandKey={demand.DemandKey}"));
                continue;
            }

            if (pick.Stages.Count == 0)
                continue;

            var chainKey = EffectiveStageChainResolver.BuildChainKey(pick.Stages);

            if (pick.RejectedChainKeys.Count > 0)
            {
                rejectedChainDemands++;
                if (prefersEdge)
                    edgeMultiChainDemands++;
                _logger.LogWarning(
                    "[Pegging] {Scope} 同请求候选多链：物料 {MaterialCode} 共 {Count} 条不同链，按冻结明文（07 v5.1.13 :722／:415）「多条不一致取最长路径+记WARNING」取最长。选用={Chosen}；未选用={Rejected}（BomNo={BomNo}，BatchNo={BatchNo}）",
                    pick.ScopeType, code, pick.RejectedChainKeys.Count + 1, chainKey,
                    string.Join(" ; ", pick.RejectedChainKeys), demand.BomNo ?? "(无锚点)", batchNo);
            }

            if (pick.OtherScopeDistinctChainCount > 0 &&
                !string.Equals(pick.OtherScopeLongestChainKey, chainKey, StringComparison.Ordinal))
                divergentScopeDemands++;

            picks.Add(new DemandChainPick(
                demand.MaterialId,
                chainKey,
                pick.Stages,
                prefersEdge ? EffectiveStageChainResolver.ScopeEdge : EffectiveStageChainResolver.ScopeRoot));
        }

        // 6) 按**物料**汇总：去重后 0 条 ⇒ 无链；1 条 ⇒ 出链；>1 条 ⇒ **隔离**（不出链）+ 诊断
        var aggregate = AggregateDemandChains(picks);

        var result = new Dictionary<int, HashSet<string>>();
        var chains = new List<StageSequenceChain>();
        var conflictExamples = new List<string>(5);

        foreach (var pick in aggregate.Chains)
        {
            result[pick.MaterialId] = new HashSet<string>(pick.Stages.Select(s => s.StageCode), StringComparer.Ordinal);
            chains.Add(new StageSequenceChain { MaterialId = pick.MaterialId, Stages = pick.Stages });
        }

        foreach (var conflict in aggregate.Conflicts)
        {
            var matCode = codeByMaterialId.TryGetValue(conflict.Key, out var c) ? c : conflict.Key.ToString();
            issues.Add(new EffectiveStageIssue(matCode, StageChainIssueRoleConflict,
                $"同一物料在本次运行内收敛出 {conflict.Value.Count} 条不同的 Stage 链：{string.Join(" ; ", conflict.Value)}" +
                "（角色按需求结构判定）。`StageSequenceChain` 每物料仅容一条" +
                "（1号位 `BuildStageChainIndex`／`StageTimingNodeBuilder` 按 `MaterialId` 建字典、last-wins 会静默覆盖）" +
                "⇒ 该物料本轮**不出链**（隔离），交 `StageLeadTime`／未排真相。"));
            if (conflictExamples.Count < 5)
                conflictExamples.Add($"{matCode}({conflict.Value.Count})");
        }

        var roleConflictMats = aggregate.Conflicts.Count;

        if (roleConflictMats > 0)
        {
            _logger.LogWarning(
                "[Pegging] Stage 链角色冲突（已隔离）：{Count} 个物料在本次运行内收敛出多条不同链，本轮不出链（示例：{Examples}；BatchNo={BatchNo}）。" +
                "合规前提＝同一物料在一次运行内只承担一种角色；「既作子件被消耗、又作顶层独立需求」须按需求实例扩载体（J14-04/J14-03）。",
                roleConflictMats, string.Join(", ", conflictExamples), batchNo);
        }

        if (missingScopeDemands > 0)
        {
            _logger.LogWarning(
                "[Pegging] 首选 scope 无链（未回退）：{Count} 个需求按请求/父边收窄后首选 scope 无行（J14-01：不回退另一 scope；BatchNo={BatchNo}）",
                missingScopeDemands, batchNo);
        }

        // J15-01 聚合留痕：身份可信度必须显性，不得把「仅按 BOMNO 回退」当成已实现请求隔离。
        if (unverifiedIdentityDemands > 0 || parentEdgeScopedDemands > 0)
        {
            issues.Add(new EffectiveStageIssue("*", StageChainIssueUnverifiedIdentity,
                $"{unverifiedIdentityDemands + parentEdgeScopedDemands}/{demands.Count} 个需求的请求实例身份**未经验证**" +
                $"（仅 BOMNO＝{unverifiedIdentityDemands}；EDGE 仅父边收窄＝{parentEdgeScopedDemands}）" +
                "⇒ 只在候选链内容**完全一致**时才出链（J16-01 准入）；链**非请求级可信**" +
                $"（BatchNo={batchNo}）；不得据此声称已隔离到具体请求。"));
            _logger.LogWarning(
                "[Pegging] 请求身份未验证（仅 BOMNO={OnlyBomNo}，EDGE 仅父边={EdgeScoped}，合计 {Count}/{Total}；BatchNo={BatchNo}）",
                unverifiedIdentityDemands, parentEdgeScopedDemands,
                unverifiedIdentityDemands + parentEdgeScopedDemands, demands.Count, batchNo);
        }

        if (ambiguousIdentityDemands > 0)
        {
            _logger.LogWarning(
                "[Pegging] 请求身份歧义（已不出链，J16-01）：{Count}/{Total} 个需求身份未经证明且候选链内容不一致 ⇒ 无 Stage 链，Q/U 不受影响（BatchNo={BatchNo}）。" +
                "合规前提＝按真实请求键（WorksetId／RequestDetailId）等值取链，或证明候选内容等价。",
                ambiguousIdentityDemands, demands.Count, batchNo);
        }

        if (systemicNoAnchor)
        {
            issues.Add(new EffectiveStageIssue("*", StageChainIssueNoRequestAnchor,
                $"本批全部 {demands.Count} 个需求均无请求锚点（`OrderBomRequestLink` 未装载或未覆盖本 PlanVersion）" +
                "⇒ Stage 链走系统性兼容路径，链**不具权威请求身份**" +
                $"（BatchNo={batchNo}）；须待夜间接货重灌锚点后复核。"));
        }

        _logger.LogInformation(
            "[Pegging] 有效 Stage 装载（逐需求选行、按 StageScopeType 隔离、每物料一条真实链）：需求={Demands}，需求物料={DemandMats}，命中 StagePath 物料={HitMats}，有序链={Chains}，" +
            "无 StagePath 行={NoRows}，首选 scope 无链={MissingScope}，无权威锚点={NoAnchor}，身份未验证={Unverified}，EDGE 仅父边={EdgeScoped}，身份歧义不出链={Ambiguous}，系统级无锚点={SystemicNoAnchor}，按 RepWorksetId 收窄={WorksetFiltered}，" +
            "同 scope 未选用链={Rejected}，EDGE 多父边={EdgeMulti}，两 scope 分歧={Divergent}，角色冲突隔离={RoleConflict}" +
            "（BatchNo={BatchNo}；细分：SQL={SqlMs}ms / 逐需求选行={LoopMs}ms）",
            demands.Count, materialIds.Count, rowsByCode.Count, chains.Count,
            noRowDemands, missingScopeDemands, noAnchorDemands, unverifiedIdentityDemands,
            parentEdgeScopedDemands, ambiguousIdentityDemands, systemicNoAnchor,
            worksetFilteredDemands,
            rejectedChainDemands, edgeMultiChainDemands, divergentScopeDemands, roleConflictMats, batchNo,
            stageSqlMs, swStage.ElapsedMilliseconds);

        // J16-02：被隔离的物料必须与「无 Stage 事实」区分开，否则 Routing 缺键会把它整片放行。
        // J17-01：拒绝集 = 角色冲突 ∪ 请求身份歧义 ∪ 首选 scope 无链（「已有事实但不属于本需求」三类）；
        //   `NoRows`（本批根本没有该物料行，采购件类）与「无锚点」不进此集 —— 那是事实缺失，交未排真相/兼容口径。
        var rejectedMaterials = ComputeRejectedMaterials(
            aggregate.Conflicts.Keys, ambiguousMaterials, missingScopeMaterials, result);

        if (missingScopeMaterials.Count > 0)
        {
            var interceptCount = missingScopeMaterials.Count(m => !result.ContainsKey(m));
            _logger.LogWarning(
                "[Pegging] 首选 scope 无链（J14-01 不回退）⇒ Routing 拦截：{MissingScope} 个物料无本请求/本角色下的链，" +
                "其中 {Intercepted} 个**本次无任何链**已并入拒绝集（不产 Task，按未排真相计入 U）；" +
                "其余 {Kept} 个另有需求成功出链，保留该链做 Stage 过滤（BatchNo={BatchNo}）",
                missingScopeMaterials.Count, interceptCount, missingScopeMaterials.Count - interceptCount, batchNo);
        }

        return new EffectiveStageLoad
        {
            ByMaterial = result,
            OrderedChains = chains,
            Issues = issues,
            RejectedMaterials = rejectedMaterials
        };
    }

    /// <summary>
    /// Routing 拒绝集（J16-02／J17-01）：这些物料**不出链且不得经「缺键放行」复活**。
    ///
    /// · 角色冲突、请求身份歧义 —— 无条件拒绝（既有事实互相冲突，任何一条都不能拿去产正式 Task）；
    /// · 首选 scope 无链 —— **仅当该物料本次没有任何链**时拒绝。同一物料另有需求成功出链时保留：
    ///   Task 是物料级的、本就共用那一条链，全拒会把合法的另一条需求一起杀掉。
    ///   （实测口径：有 StagePath 行但不在本请求／本 scope ⇒ 不是「无事实」，不能走 `NoRows` 的兼容放行。）
    /// </summary>
    internal static HashSet<int> ComputeRejectedMaterials(
        IEnumerable<int> roleConflictMaterials,
        IEnumerable<int> ambiguousIdentityMaterials,
        IEnumerable<int> missingPreferredScopeMaterials,
        IReadOnlyDictionary<int, HashSet<string>> chainsByMaterial)
    {
        var rejected = new HashSet<int>(roleConflictMaterials);
        rejected.UnionWith(ambiguousIdentityMaterials);
        foreach (var materialId in missingPreferredScopeMaterials)
        {
            if (!chainsByMaterial.ContainsKey(materialId))
                rejected.Add(materialId);
        }

        return rejected;
    }

    /// <summary>需求级选链结果：一个需求一条链（同一物料可有多个需求）。</summary>
    internal sealed record DemandChainPick(
        int MaterialId,
        string ChainKey,
        IReadOnlyList<StageSequenceStep> Stages,
        string Role);

    /// <summary>
    /// 物料级收敛结果：<see cref="Chains"/> ＝可出链的物料（每物料恰好一条）；
    /// <see cref="Conflicts"/> ＝被隔离的物料 → 其全部不同链签名（升序，**确定性**）。
    /// </summary>
    internal sealed record MaterialChainAggregate(
        IReadOnlyList<DemandChainPick> Chains,
        IReadOnlyDictionary<int, IReadOnlyList<string>> Conflicts);

    /// <summary>
    /// 「一物料一条链」的**纯收敛器**（2026-10-17 J14-03/J14-04，`internal` 供单测）。
    ///
    /// 输入是**逐需求**选出的链（每个需求已按自己的角色与请求锚点选好）：
    ///   · 同一物料多个需求收敛出**同一**链签名 ⇒ 去重为一条，正常出链（这不是冲突）；
    ///   · 收敛出**不同**链签名 ⇒ 该物料被隔离（不出链），并给出全部候选签名供诊断。
    ///
    /// 为什么隔离而不是「任取一条」：载体 `StageSequenceChain` 只有 `MaterialId`，1号位 四个消费点
    /// （`PhaseOneConstraintBuilder.BuildStageChainIndex`／`ResolveChainStagesInScope`、`StageTimingNodeBuilder`、
    /// `ConstraintContext.EffectiveStages`）按 `MaterialId` 建字典 **last-wins** ⇒ 任取会在下游被静默覆盖，
    /// 等于把「静默并集」搬了个家。PM §J14-04 允许「将不可信需求隔离并保留诊断」，隔离是本载体下唯一合规出口。
    /// 结果与输入顺序无关（物料按 MaterialId 升序、冲突签名按 ordinal 升序）。
    /// </summary>
    internal static MaterialChainAggregate AggregateDemandChains(IEnumerable<DemandChainPick> picks)
    {
        var byMaterial = new Dictionary<int, List<DemandChainPick>>();
        foreach (var pick in picks)
        {
            if (!byMaterial.TryGetValue(pick.MaterialId, out var list))
                byMaterial[pick.MaterialId] = list = new List<DemandChainPick>(2);

            if (!list.Any(x => string.Equals(x.ChainKey, pick.ChainKey, StringComparison.Ordinal)))
                list.Add(pick);
        }

        var chains = new List<DemandChainPick>();
        var conflicts = new Dictionary<int, IReadOnlyList<string>>();

        foreach (var pair in byMaterial.OrderBy(kv => kv.Key))
        {
            if (pair.Value.Count == 1)
            {
                chains.Add(pair.Value[0]);
                continue;
            }

            conflicts[pair.Key] = pair.Value
                .Select(x => x.ChainKey)
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();
        }

        return new MaterialChainAggregate(chains, conflicts);
    }

    /// <summary>
    /// 「本次 BOM 有效 Stage」装载结果（两种形态，用途不同，勿混用）：
    /// - `ByMaterial`（集合）：供 Routing 三件套**存在性**过滤（该物料本次有没有这个 Stage）；
    /// - `OrderedChains`（有序链）：供 1号位 判断 Stage **先后**（`RequiredStageCode` 截断 / `StageLeadTimes` 前后依赖）。
    /// </summary>
    private sealed class EffectiveStageLoad
    {
        public static readonly EffectiveStageLoad Empty = new();

        public IReadOnlyDictionary<int, HashSet<string>> ByMaterial { get; init; }
            = new Dictionary<int, HashSet<string>>();

        public IReadOnlyList<StageSequenceChain> OrderedChains { get; init; }
            = Array.Empty<StageSequenceChain>();

        /// <summary>
        /// 装载期诊断（J14-01/02/03/04）。**不是**冻结 `ScheduleReasonCodeDomain.Frozen15` 里的原因码，
        /// 不进 `FinalTask`／`UnscheduledDemandKeys`；供调用方聚合告警与追溯用。
        /// </summary>
        public IReadOnlyList<EffectiveStageIssue> Issues { get; init; }
            = Array.Empty<EffectiveStageIssue>();

        /// <summary>
        /// 为什么本次有效 Stage **不可用**（<c>null</c> = 可用）。不可用 ≠ 合法空集：
        /// 「业务合法空集」（本域确无需要 Stage 的需求）仍走 <see cref="Empty"/>；
        /// 而 BOM 关联批缺失 / 需求物料主档缺失属**输入事实缺失**，必须由调用方留痕并按缺事实处置
        /// （0号位 2026-10-17 §三.D：不得静默转换成 Empty 去放开全部 Stage 过滤）。
        /// </summary>
        public string? UnavailableReason { get; init; }

        /// <summary>是否可用于「本次 Stage 上下文已装载」的判定。</summary>
        public bool IsAvailable => UnavailableReason is null;

        /// <summary>
        /// **因角色冲突被主动隔离**的物料（0号位 2026-10-17 第十六轮 J16-02）。
        ///
        /// 必须与「本批 StagePath 无该物料行」严格区分：
        ///   · 无行 ＝ 数据缺口／采购件 ⇒ 合法「不限定 Stage」，Routing 不过滤（既有口径）；
        ///   · 本集合 ＝ **已发现有真实冲突** ⇒ 该物料的 Routing 必须一并剔除，
        ///     否则「字典缺键就放行」会让被隔离的需求经 Routing 复活成正式 Task。
        /// </summary>
        public IReadOnlySet<int> RejectedMaterials { get; init; } = new HashSet<int>();

        /// <summary>构造「输入事实缺失」形态：内容为空，但带原因与结构化诊断。</summary>
        public static EffectiveStageLoad Unavailable(string reason, IReadOnlyList<EffectiveStageIssue> issues)
            => new() { UnavailableReason = reason, Issues = issues };
    }

    /// <summary>Stage 链装载诊断：本需求/本物料为何没有拿到链（或为何被隔离）。</summary>
    private sealed record EffectiveStageIssue(string MaterialCode, string ReasonCode, string Detail);

    private const string StageChainIssueNoRows = "STAGE_CHAIN_NO_STAGEPATH_ROWS";
    private const string StageChainIssueMissingPreferredScope = "STAGE_CHAIN_MISSING_PREFERRED_SCOPE";

    /// <summary>
    /// **BOM 输入缺失**：本 PlanVersion 无 `OrderBomRequestLink` 关联批 ⇒ 有效 Stage 无法按请求装载
    /// （0号位 2026-10-17 §三.D。区别于 <see cref="StageChainIssueNoRows"/>：那是「有批、但该物料无行」）。
    /// </summary>
    private const string StageChainIssueNoBomBatchLink = "STAGE_CHAIN_NO_BOM_BATCH_LINK";

    /// <summary>需求物料在 `Material` 主档不存在 ⇒ Stage 装载无输入（同属「事实缺失」，非合法空集）。</summary>
    private const string StageChainIssueNoMaterialMaster = "STAGE_CHAIN_NO_MATERIAL_MASTER";

    /// <summary>三键全空 ⇒ 无权威请求身份，本需求不出链（J15-01 状态 c）。</summary>
    private const string StageChainIssueNoRequestAnchor = "STAGE_CHAIN_NO_REQUEST_ANCHOR";

    /// <summary>仅有 `BOMNO`、缺第二层锚点 ⇒ 内容等价兼容，链可用但**请求身份未验证**（J15-01 状态 b）。</summary>
    private const string StageChainIssueUnverifiedIdentity = "STAGE_CHAIN_UNVERIFIED_REQUEST_IDENTITY";

    /// <summary>
    /// 请求实例未经证明且候选链内容不一致 ⇒ **不出链**（0号位 2026-10-17 第十六轮 J16-01 准入条件）。
    /// 与 <see cref="StageChainIssueRoleConflict"/> 的区别：后者是同一物料跨需求多链，前者是同一需求内候选不等价。
    /// </summary>
    private const string StageChainIssueIdentityAmbiguous = "STAGE_CHAIN_REQUEST_IDENTITY_AMBIGUOUS";

    private const string StageChainIssueRoleConflict = "STAGE_CHAIN_ROLE_CONFLICT";

    /// <summary>StagePath 有效 Stage 行（ChildMaterialCode + StageCode + StageSeq）</summary>
    private sealed class EffectiveStageRow
    {
        public string ChildMaterialCode { get; set; } = string.Empty;
        public string? StageCode { get; set; }
        public int StageSeq { get; set; }
    }

    /// <summary>StagePath 供给阈值行（按请求 BOMNO 区分；J14-03）。</summary>
    private sealed class StageThresholdRow
    {
        public string ChildMaterialCode { get; set; } = string.Empty;
        public string? BOMNO { get; set; }
        public string? StageCode { get; set; }
    }

    /// <summary>
    /// StagePath **行实例**（未聚合）—— 含「请求＋边实例」隔离列（0号位 J12-02 的载体）。
    /// 与 <see cref="EffectiveStageRow"/> 的区别：后者是**聚合后**的 (子件, Stage) 投影，**隔离列已丢**。
    /// </summary>
    private sealed class StagePathInstanceRow
    {
        public string ChildMaterialCode { get; set; } = string.Empty;

        /// <summary>`StageScopeType`（ROOT／EDGE）；DDL NOT NULL，此处可空仅为容错。</summary>
        public string? StageScopeType { get; set; }

        /// <summary>EDGE = 父件编码；ROOT = NULL（`07 :414`）。</summary>
        public string? ParentMaterialCode { get; set; }

        /// <summary>`08 v5.1.8.7 :2008` 冻结要求的「请求＋边实例」锚点（旧批次可为 NULL）。</summary>
        public long? WorksetId { get; set; }

        /// <summary>`WorksetId` 缺失时的退化实例键。</summary>
        public string? BOMNO { get; set; }

        public int StageSeq { get; set; }
        public string? StageCode { get; set; }
    }

    /// <summary>
    /// `OrderBomRequestLink` 的**需求锚点**装载（0号位 2026-10-17 第十四轮 J14-02）。
    ///
    /// 键 = `OrderId`（`LogicalProductionDemand.OrderId` 已是根订单号）＋ 本 PV；值 = 该订单在本次 BOM 请求里的
    /// `ResolvedBOMNO / RequestDetailId / RepWorksetId`。这是「真实 Demand → RequestDetail → Workset」关系在库里的
    /// **现成载体**（`08 v5.1.8.7 :2045-2076` 冻结 DDL），无需新增表、无需扩 DTO 到 1号位。
    ///
    /// 只填空值：调用方在 `BuildSolveRequestAsync` 里对需求做幂等回填（分桶切片已复制副本，各自有值）。
    /// 一个订单在同一 PV 只应有一条链接（唯一键 `(PlanVersionId, OrderCanonicalId)`）；出现多条时取 `SyncedAt` 最新，
    /// 并对多 BOMNO 情况留痕（与 `LoadBomSnapshotAsync` 的批次口径一致）。
    /// </summary>
    private async Task<Dictionary<long, BomAnchorRow>> LoadBomAnchorsAsync(
        int planVersionId,
        CancellationToken ct)
    {
        var rows = (await _connectionManager.QueryAsync<BomAnchorRow>(
            @"SELECT o.Id AS OrderId,
                     r.BatchNo,
                     r.RequestDetailId,
                     r.ResolvedBOMNO,
                     r.RepWorksetId,
                     r.SyncedAt
              FROM OrderBomRequestLink r
              INNER JOIN [Order] o ON o.Id = r.OrderId
              WHERE o.PlanVersionId = @PlanVersionId
                AND r.ResolvedBOMNO IS NOT NULL
              ORDER BY r.SyncedAt DESC",
            new { PlanVersionId = planVersionId },
            db: DatabaseId.APS)).ToList();

        var anchors = new Dictionary<long, BomAnchorRow>();
        var multiBom = 0;
        foreach (var r in rows)
        {
            if (!anchors.TryAdd(r.OrderId, r))
            {
                // 同一订单多条链接（多 BOMNO）：保留最新的（SQL 已按 SyncedAt DESC），并计数留痕。
                if (!string.Equals(anchors[r.OrderId].ResolvedBOMNO, r.ResolvedBOMNO, StringComparison.Ordinal))
                    multiBom++;
            }
        }

        if (multiBom > 0)
        {
            _logger.LogWarning(
                "[Pegging] 同一订单关联多个 ResolvedBOMNO：{Count} 个订单存在多条 OrderBomRequestLink（取 SyncedAt 最新一条，PlanVersionId={PlanVersionId}）",
                multiBom, planVersionId);
        }

        _logger.LogInformation(
            "[Pegging] BOM 需求锚点装载：{Orders} 个订单 → ResolvedBOMNO/RequestDetailId/RepWorksetId（PlanVersionId={PlanVersionId}）",
            anchors.Count, planVersionId);

        return anchors;
    }

    /// <summary>
    /// 把 <see cref="LoadBomAnchorsAsync"/> 取回的请求锚点**幂等回填**到需求上（2026-10-17 J14-02）。
    ///
    /// 只填空值（不覆盖调用方已显式给定的值）；无 `OrderId` 或无链接的需求保持 null ⇒ 下游
    /// <see cref="EffectiveStageChainResolver.SelectionContext"/> 三键皆空，退化为「不做请求收窄」并计数留痕 ——
    /// 这是**可见的降级**，不是静默采用别的请求的链。
    /// </summary>
    private async System.Threading.Tasks.Task FillDemandRequestAnchorsAsync(
        IReadOnlyList<LogicalProductionDemand> demands,
        int planVersionId,
        CancellationToken ct)
    {
        if (demands.Count == 0)
            return;

        var anchoredOrders = demands
            .Where(d => d.OrderId.HasValue)
            .Select(d => d.OrderId!.Value)
            .Distinct()
            .ToList();

        if (anchoredOrders.Count == 0)
        {
            _logger.LogWarning(
                "[Pegging] 需求锚点回填跳过：{Count} 个需求全部无 OrderId ⇒ Stage 链装载无法按请求选行（只用 BOMNO 为空时的退化口径）",
                demands.Count);
            return;
        }

        var anchors = await LoadBomAnchorsAsync(planVersionId, ct);

        var filledBomNo = 0;
        var filledWorkset = 0;
        var missing = 0;
        foreach (var demand in demands)
        {
            if (!demand.OrderId.HasValue || !anchors.TryGetValue(demand.OrderId.Value, out var anchor))
            {
                missing++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(demand.BomNo) && !string.IsNullOrWhiteSpace(anchor.ResolvedBOMNO))
            {
                demand.BomNo = anchor.ResolvedBOMNO;
                filledBomNo++;
            }

            if (demand.RequestDetailId is null && anchor.RequestDetailId > 0)
                demand.RequestDetailId = anchor.RequestDetailId;

            if (demand.RepWorksetId is null && anchor.RepWorksetId.HasValue)
            {
                demand.RepWorksetId = anchor.RepWorksetId;
                filledWorkset++;
            }
        }

        if (missing > 0)
        {
            _logger.LogWarning(
                "[Pegging] 需求锚点缺失：{Count}/{Total} 个需求无 `OrderBomRequestLink`（PlanVersionId={PlanVersionId}）⇒ 其 Stage 链只能按无锚点退化口径解析",
                missing, demands.Count, planVersionId);
        }

        _logger.LogInformation(
            "[Pegging] 需求锚点回填：BomNo={BomNo}／RepWorksetId={Workset}／缺锚点={Missing}（共 {Total} 个需求，PlanVersionId={PlanVersionId}）",
            filledBomNo, filledWorkset, missing, demands.Count, planVersionId);
    }

    /// <summary>`OrderBomRequestLink` 需求锚点装载行。</summary>
    private sealed class BomAnchorRow
    {
        public long OrderId { get; set; }
        public string? BatchNo { get; set; }
        public long RequestDetailId { get; set; }
        public string? ResolvedBOMNO { get; set; }
        public long? RepWorksetId { get; set; }
        public DateTime SyncedAt { get; set; }
    }

    /// <summary>OrderBomRequestLink 批次行（SQL 已按 SyncedAt DESC 排序，取首个 = 最新批次）</summary>
    private sealed class BatchLinkRow
    {
        public string? BatchNo { get; set; }
    }

    /// <summary>
    /// 「三级均未命中」时的兜底提前期（小时）= 3 天。
    /// PM 2026-09-29 裁决：「未来没有 routing 如果没有数据，则是在 StageLeadTimeParam 中查找，按照三级查找，
    ///   如果三级均未命中，则记入 STAGE_LEADTIME_MISSING，**并按 3 天兜底**」。
    /// ⚠️ 与 0号位 §4.4「不得默认 0 小时」不冲突：§4.4 禁的是**默认 0 小时**（= 不给时间），
    ///    兜底给的是**非 0 的显式值**且同时出码，两者可区分、可审计。
    /// </summary>
    private const decimal MissingStageLeadTimeFallbackHours = 72m;

    /// <summary>
    /// 「三级均未命中 ⇒ 3 天兜底」这条 Fact 的 `MatchLevel` 取值 = **`STAGE_LEADTIME_MISSING`**（PM 原码逐字）。
    ///
    /// ⚠️ 2026-09-29 与 1号位 对齐后改名：我方曾自起 `STAGE_LEADTIME_MISSING_3DAY_FALLBACK`，
    ///   而 1号位 回执《无RoutingStage时间出口字段_字段载体落码提请_v1.0》§四 已把 V1 合法值域**定死为 4 个**：
    ///   `DEPT_EXACT` / `FACTORY_STAGE_DEFAULT` / `GLOBAL_STAGE_DEFAULT` / `STAGE_LEADTIME_MISSING`，
    ///   并明文「4 个之外的任何值 ⇒ **契约违例**，不用该值」⇒ 我方的自起名会被消费侧当违例。
    ///   ⇒ 改回 PM 原码。语义仍清楚：**本条 Fact 带值**（72h），MISSING 指的是「参数未命中」。
    /// </summary>
    private const string MissingStageLeadTimeFallbackLevel = "STAGE_LEADTIME_MISSING";

    /// <summary>
    /// 装载「无 Routing 阶段的提前期」——PM《无Routing Stage统一处理建议》§九：2号位 装载 → 1号位 消费。
    ///
    /// 口径：对每个需求物料的每个「本次有效 Stage」，若该 Stage **无任何 RoutingOperation**，
    ///   则查 `StageLeadTimeParam` 解析提前期。命中梯次 **三级**（2026-09-29 用户明确口径，逐字）：
    ///     ① `DEPT_EXACT`             = ProductionDeptCode + FactoryCode + StageCode
    ///     ② `FACTORY_STAGE_DEFAULT`  = FactoryCode + StageCode
    ///     ③ `GLOBAL_STAGE_DEFAULT`   = **StageCode**（**不带 IsDefault 条件**；见 ResolveStageLeadTime 内注）
    ///   （旧的 MaterialCode 级 / ProductFamilyCode 级 §4.1 定为「在V1不作为正常有效匹配级别」，**已移除**，
    ///     遇旧数据由 <see cref="HasLegacyLevelRow"/> 登记兼容告警。）
    ///   命中即停（同层多条取 Priority 最小）；
    ///   **走完三级仍未命中** ⇒ 记 `STAGE_LEADTIME_MISSING` 并**按 3 天兜底产出 Fact**（PM 2026-09-29 裁决，见
    ///   <see cref="MissingStageLeadTimeFallbackHours"/>）——**不再省略**。
    ///   **兜底只看「算不算命中」，不按成因分治**（甲/乙/丙 都走同一支）——**只有一种情形算未命中：三级全不中**。
    ///   ⚠️ **2026-09-29 用户更正**：本节原有第二句「命中到参数行但折算值为 0（或负）⇒ 视同未命中」
    ///   **已作废**。**命中就是命中**：折算值为 0（或负）**仍按命中产出**，`LeadTimeHours = 0`、
    ///   `MatchLevel` = 实际命中层级（实测 4 行 `*_FINAL` 的 `LeadTimeDays = 0.00`
    ///   ⇒ 产出 **0 小时 + `FACTORY_STAGE_DEFAULT`**，**不带** `STAGE_LEADTIME_MISSING`）。
    ///   0 是参数行写下的值，装载层**不得替参数侧改判**；仅单记 `zeroHitCount`（信息级日志）供参数 Owner 查看。
    ///
    /// **性能（2026-09-29 修复 N+1）**：循环体内原对每个 `(物料, 阶段)` 调一次
    ///   `LoadMaterialStageDeptContextAsync(new[] { materialId })` 取部门码 —— 单元素查询 × gapPairs 条数，
    ///   而 `MaterialStageDeptContext` 实测 813,424 行（`IsCurrent=1`）且无缓存 ⇒ 一次 Pegging 发万级串行往返
    ///   （实测 PV=539 BOM 范围内 gapPairs = **16,383**；每个 Domain 各来一遍）。
    ///   现改为**复用调用方已整批装好的 `materialStageDeptContexts`** 建查表（其物料集 = 需求物料 ⊇ gapPairs 物料），
    ///   循环内零查询；本方法自身仍保留 5 次批量预取（参数表 / 物料 / 产品族 / 部门 / 工厂），无逐行查询。
    /// </summary>
    private async System.Threading.Tasks.Task<IReadOnlyList<StageLeadTimeFact>> LoadStageLeadTimesAsync(
        IReadOnlyDictionary<int, HashSet<string>> effectiveStages,
        List<RoutingOperation> operations,
        IReadOnlyList<LogicalProductionDemand> demands,
        IReadOnlyList<MaterialStageDepartmentContextDto> materialStageDeptContexts)
    {
        if (effectiveStages.Count == 0)
            return Array.Empty<StageLeadTimeFact>();

        // 1) 找出「有效 Stage 但零工序」的 (MaterialId, StageCode)
        var opsByMatStage = operations
            .Where(o => !string.IsNullOrEmpty(o.StageCode))
            .Select(o => (o.MaterialId, o.StageCode!))
            .ToHashSet();

        var gapPairs = effectiveStages
            .SelectMany(kv => kv.Value.Select(stage => (MaterialId: kv.Key, StageCode: stage)))
            .Where(p => !opsByMatStage.Contains((p.MaterialId, p.StageCode)))
            .ToList();

        if (gapPairs.Count == 0)
            return Array.Empty<StageLeadTimeFact>();

        // 2) 一次取全参数表（人工维护、体量小；避免 IN 大列表）
        var paramRows = (await _connectionManager.QueryAsync<StageLeadTimeParamRow>(
            @"SELECT FactoryCode, StageCode, ProductionDeptCode, MaterialCode, ProductFamilyCode,
                     LeadTimeDays, LeadTimeHours, Priority, IsDefault,
                     CAST(CASE WHEN EffectiveFrom IS NULL THEN 1 ELSE 0 END AS bit) AS EffectiveFromNull
              FROM StageLeadTimeParam
              WHERE IsActive = 1
                AND (EffectiveTo   IS NULL OR EffectiveTo   >= CAST(GETDATE() AS DATE))
                -- ⚠️ 2026-09-29 修复：EffectiveFrom 必须与 EffectiveTo **对称**处理（NULL = 不限）。
                --    实测 3号位 补进来的 282 行 **EffectiveFrom 全为 NULL**，而 NULL 比较返回 UNKNOWN
                --    ⇒ 旧谓词 `EffectiveFrom <= GETDATE()` 把这 282 行**全部滤掉**，装载层看到 0 行、
                --    静默走「整表空」分支 ⇒ 参数表「看起来补完了」，实际一条也没生效。
                AND (EffectiveFrom IS NULL OR EffectiveFrom <= CAST(GETDATE() AS DATE))",
            db: DatabaseId.APS)).ToList();

        if (paramRows.Count == 0)
        {
            // ⚠️ 2026-09-29 PM 裁决（口径变更）：三级均未命中 ⇒ 记 `STAGE_LEADTIME_MISSING` **并按 3 天兜底**。
            //    整表为空 = 全部 gapPairs 三级均未命中 ⇒ **不再早退**。旧实现 `return 空` 让 1号位 拿到「无提前期」，
            //    净效果等价于 0 小时，正是 0号位 §4.4 明令禁止的「默认 0 小时」（省略 ≠ 合规）。
            //    此处只把「整表空」这个比逐条未命中更强的信号单独吼一声，随后仍走下面的兜底产出。
            _logger.LogWarning(
                "[Pegging] StageLeadTimeParam 无任何有效参数行，但存在 {Count} 个「有效 Stage 但无 RoutingOperation」的 (物料,阶段)"
                + " —— 全部记 STAGE_LEADTIME_MISSING 并按 {Hours} 小时兜底（PM 2026-09-29 裁决；Owner=3号位、粒度=工厂+StageCode+生产部门，待其铺数）",
                gapPairs.Count, MissingStageLeadTimeFallbackHours);
        }
        else
        {
            // 生效日期缺口不静默：3号位 补的参数若没填 `EffectiveFrom`，装载层按「不限」采用，
            // 但必须让它可见 —— 否则「参数到底生效了没有」谁也说不清。
            var nullFrom = paramRows.Count(r => r.EffectiveFromNull);
            if (nullFrom > 0)
            {
                _logger.LogWarning(
                    "[Pegging] StageLeadTimeParam 有 {NullFrom}/{Total} 行未填 `EffectiveFrom`（NULL）—— 装载层按「不限」采用"
                    + "（与 `EffectiveTo IS NULL` 对称）。若 3号位 本意是「尚未生效」，请补日期，否则这批参数已直接生效。",
                    nullFrom, paramRows.Count);
            }
        }

        // 3) 取物料编码 + 产品族 + 工厂（供命中降级）
        //    分片：`gapPairs` 是**跨全单聚合**的 (物料,阶段) 集合，真实规模下物料数可超 2100
        //    （同 FillRequiredStageCodesAsync 的实测踩点，2026-09-29）。走统一帮手，别再写裸 `IN @`。
        var matIds = gapPairs.Select(p => p.MaterialId).Distinct().ToList();
        var matRows = (await QueryChunkedInAsync(matIds, chunk =>
            _connectionManager.QueryAsync<StageLeadTimeMatRow>(
                "SELECT Id, MaterialCode, ProductFamilyId FROM Material WHERE Id IN @Ids",
                new { Ids = chunk },
                db: DatabaseId.APS))).ToList();
        var matById = matRows.ToDictionary(r => r.Id, r => r);

        // 产品族编码（键 = int Id，勿传 StringComparer）
        var familyCodes = (await _connectionManager.QueryAsync<FamilyCodeRow>(
            "SELECT Id, Code FROM ProductFamily",
            db: DatabaseId.APS)).ToDictionary(r => r.Id, r => r.Code);

        // 部门编码（StageLeadTimeParam.ProductionDeptCode 用 DeptCode 而非 Id）
        var deptCodes = (await _connectionManager.QueryAsync<DeptCodeRow>(
            "SELECT Id, SourceDeptCode FROM ProductionDepartment WHERE IsActive = 1 AND SourceDeptCode IS NOT NULL",
            db: DatabaseId.APS)).ToDictionary(r => r.Id, r => r.SourceDeptCode ?? string.Empty);

        // 工厂编码
        var factoryCodes = (await _connectionManager.QueryAsync<FactoryCodeRow>(
            "SELECT Id, Code FROM Factory WHERE IsActive = 1",
            db: DatabaseId.APS)).ToDictionary(r => r.Id, r => r.Code);

        // 物料 → 本次需求的工厂（同一物料可能跨厂，取首个；命中降级用）
        var factoryByMaterial = demands
            .GroupBy(d => d.MaterialId)
            .ToDictionary(g => g.Key, g => g.First().FactoryId);

        // (物料, 阶段) → 首个非空部门码（MSC 给，与 1号位 部门锁定同源）—— **循环外一次建好**。
        //   数量守恒：`materialStageDeptContexts` 是调用方按 demandMaterialIds 整批装好的，
        //   而 effectiveStages 的键 ⊆ demandMaterialIds（见 LoadEffectiveStageKeysAsync 的 Id 反查）
        //   ⇒ 本表覆盖 gapPairs 的全部物料，循环内无需再查库。
        //   `TryAdd` 保留**首个**非空值 = 原 `FirstOrDefault(dc => !string.IsNullOrEmpty(dc))` 语义
        //   （同一物料的行在批量查询与单物料查询中均按同一自然序返回，逐物料顺序不变）。
        var deptCodeByMaterialStage = new Dictionary<(int MaterialId, string StageCode), string>();
        foreach (var c in materialStageDeptContexts)
        {
            if (string.IsNullOrEmpty(c.StageCode)) continue;
            if (!deptCodes.TryGetValue(c.ProductionDepartmentId, out var dc) || string.IsNullOrEmpty(dc)) continue;

            deptCodeByMaterialStage.TryAdd((c.MaterialId, c.StageCode), dc);
        }

        var result = new List<StageLeadTimeFact>(gapPairs.Count);
        var missingCount = 0;
        // 与 `missingCount` 分开计：「参数行没配」与「配了值 = 0」对参数 Owner 是两件事。
        // ⚠️ 口径提醒：`zeroHitCount` **不是**未命中 —— 它属于**命中**（0 是参数行写下的值），见下方内注。
        var zeroHitCount = 0;
        var legacyLevelCount = 0;
        var matchLevelTally = new Dictionary<string, int>(StringComparer.Ordinal);
        // 命中里有几条**不是**靠字面相等、而是靠 `JoinFactoryStage` 重建权威值域才命中的。
        //   用途：让参数 Owner 看见「StageLeadTimeParam.StageCode 值域与全库不一致」这件事，
        //   而不是被装载侧兼容伪装成「一切正常」。见 <see cref="JoinFactoryStage"/>。
        var normalizedMatchCount = 0;

        foreach (var (materialId, stageCode) in gapPairs)
        {
            matById.TryGetValue(materialId, out var mat);
            var materialCode = mat?.MaterialCode;
            var familyCode = mat?.ProductFamilyId is int fid && familyCodes.TryGetValue(fid, out var fc) ? fc : null;

            // 该物料本次涉及的部门（MSC 给）→ 取首个用于 DEPT_EXACT 级命中（查表，零查询）
            var deptCode = deptCodeByMaterialStage.TryGetValue((materialId, stageCode), out var dept)
                ? dept
                : null;

            var factoryCode = factoryByMaterial.TryGetValue(materialId, out var fid2)
                              && factoryCodes.TryGetValue(fid2, out var fcode) ? fcode : null;

            // §4.3 兼容告警：存在已废止的 Material/Family 级旧数据即计数（与本次是否命中无关）。
            if (HasLegacyLevelRow(paramRows, materialCode, familyCode, factoryCode, stageCode))
                legacyLevelCount++;

            var hit = ResolveStageLeadTime(paramRows, deptCode, factoryCode, stageCode);
            if (hit is null)
            {
                // ⚠️ 2026-09-29 PM 裁决（口径变更，推翻本节旧写法）：
                //    「按照三级查找，如果三级均未命中，则记入 STAGE_LEADTIME_MISSING，**并按 3 天兜底**」。
                // ⇒ **既出码、也产出带值的 Fact**：
                //    ① 出码：`missingCount` + 告警，让「参数未配」在日志上可见（旧旧实现是静默 continue，缺口不可见）；
                //    ② 产出：`LeadTimeHours = 72`（3 天）——**不得省略**。省略会让 1号位 拿到「无提前期」，
                //       净效果等价于 0 小时，正是 0号位 §4.4 禁止的效果；§4.4 禁的是「默认 0 小时」，不是「兜底」。
                // `MatchLevel` 取 `STAGE_LEADTIME_MISSING`（PM 原码逐字 = 1号位 回执 §四 定死的 4 值之一，
                //   见 <see cref="MissingStageLeadTimeFallbackLevel"/> 的改名说明）。
                missingCount++;
                matchLevelTally[MissingStageLeadTimeFallbackLevel] =
                    matchLevelTally.TryGetValue(MissingStageLeadTimeFallbackLevel, out var nm) ? nm + 1 : 1;

                result.Add(new StageLeadTimeFact
                {
                    MaterialId    = materialId,
                    StageCode     = stageCode,
                    LeadTimeHours = MissingStageLeadTimeFallbackHours,
                    MatchLevel    = MissingStageLeadTimeFallbackLevel
                });
                continue;
            }

            // 换算口径（DTO 注释逐字）：LeadTimeHours > 0 取之，否则按 LeadTimeDays × 24 折算。
            // 两者均可空（实测 LeadTimeHours 全 NULL、值在 LeadTimeDays）⇒ 先 ?? 归零再判。
            var hitHours = hit.LeadTimeHours ?? 0m;
            var leadTimeHours = hitHours > 0m
                ? hitHours
                : (hit.LeadTimeDays ?? 0m) * 24m;

            // ⚠️ 2026-09-29 用户更正（口径）：**命中到参数行就是命中** —— 折算值为 0（或负）**不视同未命中**。
            //    【推翻什么】本节曾写「命中但折算值 ≤ 0 ⇒ 视同未命中、与三级全不中一视同仁走 3 天兜底」，
            //      该口径**已作废**：0 是参数行**明明白白写下的值**，装载层不得替参数侧改判成 72。
            //    【实测影响】4 行 `*_FINAL`（BJ/CN/SH/TJ 完工，`LeadTimeDays = 0.00`、`ProductionDeptCode` 空）
            //      ⇒ 现在产出 **`LeadTimeHours = 0` + `MatchLevel = FACTORY_STAGE_DEFAULT`**，不再带 MISSING 码。
            //    【只保留可见性】`zeroHitCount` 与 `missingCount` 分开计 + 一条**信息级**日志，
            //      让参数 Owner 能看见「有 N 行命中的值是 0」，但**不改写它的值、不动它的层级码**。
            if (leadTimeHours <= 0m) zeroHitCount++;

            matchLevelTally[hit.MatchLevel] = matchLevelTally.TryGetValue(hit.MatchLevel, out var n) ? n + 1 : 1;

            // 命中行的 StageCode 与传入值**字面不等** ⇒ 本次是靠 `JoinFactoryStage` 重建值域才命中的。
            if (!string.Equals(hit.StageCode, stageCode, StringComparison.Ordinal))
                normalizedMatchCount++;

            result.Add(new StageLeadTimeFact
            {
                MaterialId     = materialId,
                StageCode      = stageCode,
                LeadTimeHours  = leadTimeHours,
                MatchLevel     = hit.MatchLevel
            });
        }

        if (result.Count > 0)
        {
            _logger.LogInformation(
                "[Pegging] 无 Routing 阶段提前期装载：缺口 (物料,阶段)={Gap}，产出 Fact={Fact}（其中三级全不中⇒3 天兜底 {Missing} 条；命中级里折算值 = 0 ⇒ 按命中产出 0 小时 {Zero} 条；靠值域重建命中 {Normalized} 条），命中层级={Levels}",
                gapPairs.Count, result.Count, missingCount, zeroHitCount, normalizedMatchCount,
                string.Join(",", matchLevelTally.Select(kv => $"{kv.Key}={kv.Value}")));
        }

        if (normalizedMatchCount > 0)
        {
            // 值域漂移的**事实可见**（不是缺陷告警，是给参数 Owner 的信标）：
            //   这批命中**不是**靠字面相等，而是靠 `FactoryCode + "_" + StageCode` 重建权威形态才中的
            //   ⇒ `StageLeadTimeParam.StageCode` 与全库权威值域（带工厂前缀，`StageDict` 为准）**不一致**。
            //   装载侧兼容**不改变**命中结果、也不阻断；但值域统一终态归 3号位（S-004 / 07 §366），
            //   故必须留痕，避免「兼容」把它掩盖成「一切正常」而永远不修。
            _logger.LogWarning(
                "[Pegging] StageLeadTimeParam 值域兼容：{Normalized}/{Fact} 条提前期是靠 `FactoryCode + '_' + StageCode` 重建权威 StageCode 后才命中的（参数表 StageCode 均不带工厂前缀，而 `StageDict`/`APS_BOM_STAGE_PATH_RAW`/`RoutingOperation`/`RoutingStage` 四处值域均带前缀）。值域统一终态归 3号位（S-004；07 字段文档 §366）",
                normalizedMatchCount, result.Count);
        }

        if (missingCount > 0)
        {
            // STAGE_LEADTIME_MISSING：出码 + **按 3 天兜底产出**（PM 2026-09-29 裁决）。
            //   注意口径：禁的是「默认 0 小时」（§4.4），不是「兜底」——省略 Fact 才是等价 0 小时的那个错。
            _logger.LogWarning(
                "[Pegging] STAGE_LEADTIME_MISSING：{Missing}/{Gap} 个「有效 Stage 但无 RoutingOperation」的 (物料,阶段) 命中链（DEPT_EXACT→FACTORY_STAGE_DEFAULT→GLOBAL_STAGE_DEFAULT）走完仍未命中，已按 {Hours} 小时（3 天）兜底产出（PM 2026-09-29 裁决；不删 Stage、不默认 0 小时，0号位 §4.4）",
                missingCount, gapPairs.Count, MissingStageLeadTimeFallbackHours);
        }

        if (zeroHitCount > 0)
        {
            // 仅作**事实可见**，不是告警：这批 (物料,阶段) 命中了参数行、值是 0，**按命中产出 0 小时**。
            //   保留这条是因为「值 = 0」与「压根没配」在报表上看起来一样，参数 Owner 需要能分清。
            //   ⚠️ 口径已定（2026-09-29 用户更正）：0 = **命中**，不换成 3 天兜底，**勿按缺陷处理**。
            _logger.LogInformation(
                "[Pegging] 无 Routing 阶段提前期：{Zero}/{Gap} 个 (物料,阶段) 命中到参数行且折算值为 0（LeadTimeHours 与 LeadTimeDays×24 均 ≤ 0），按**命中**产出 LeadTimeHours=0，层级码 = 实际命中级（**不是** STAGE_LEADTIME_MISSING）。实测为 4 行 `*_FINAL`（完工阶段零时长）",
                zeroHitCount, gapPairs.Count);
        }

        if (legacyLevelCount > 0)
        {
            // §4.3：不让 1号位 自行解释旧数据。这里只告警不采用——旧口径下这两级比 DEPT 更细、会抢占命中，
            // 若确有此类行，本次装载结果与旧口径**已经不同**，必须让 3号位 知道去下线/迁移。
            _logger.LogWarning(
                "[Pegging] StageLeadTimeParam 兼容告警：{Legacy} 个 (物料,阶段) 存在已废止的 Material/ProductFamily 级参数行（V1 不作为正常有效匹配级别，0号位 §4.1/§4.3），本次装载**已不采用**该两级。请 3号位 下线/迁移该批参数",
                legacyLevelCount);
        }

        return result;
    }

    /// <summary>
    /// 按命中顺序从细到粗降级解析提前期；命中即停，同层取 Priority 最小。返回 null = 全部未命中。
    /// 层级码：`DEPT_EXACT` / `FACTORY_STAGE_DEFAULT` / `GLOBAL_STAGE_DEFAULT`。
    ///
    /// ⚠️ 0号位 2026-09-29《Stage顺序_无RoutingStage_Operation标识与TaskMES粒度》§4.1/§4.3：
    ///    V1 主业务粒度 = **工厂 + StageCode + 生产部门**；Material 级 / ProductFamily 级
    ///    「**在V1不作为正常有效匹配级别**」、3号位 后续停止发布 ⇒ **本梯次已移除这两级**。
    ///    旧口径下 Material/Family 比 DEPT 更细、会**抢占**命中，留着等于继续按废止口径跑。
    ///    遇旧数据**不静默采用也不静默忽略**，由调用方登记兼容告警（见 <see cref="HasLegacyLevelRow"/>，§4.3）。
    /// </summary>
    /// <remarks>
    /// 2026-10-15：由 `private` 改 **`internal`**，**仅供 `LPS.APS.Tests`（`InternalsVisibleTo`）直接断言命中行为**
    ///   新增值域等价分支（见 <see cref="JoinFactoryStage"/>）——与 <c>SchedulingOrchestrator.LoadResourcesAndCalendarAsync</c>
    ///   同一动机：让测试调用**生产同一份代码**，而不是复刻一份判据（复刻即第二个漂移源）。
    ///   **不改任何行为**：签名语义、SQL、SQL 之外的逻辑逐字不变。
    /// </remarks>
    internal static StageLeadTimeParamRow? ResolveStageLeadTime(
        IReadOnlyList<StageLeadTimeParamRow> rows,
        string? deptCode,
        string? factoryCode,
        string stageCode)
    {
        // 各层候选筛选器：(谓词, 层级码)，按从细到粗顺序；第一、二层均含 FactoryCode（冻结口径）
        //
        // ⚠️ 2026-10-15 修复「三级命中率恒为 0」：参数行的 `StageCode` **不带工厂前缀**（`MACH`），
        //   而传入的 `stageCode` 来自 `APS_BOM_STAGE_PATH_RAW`（`BJ_MACH`）⇒ **字面比较永不相等** ⇒
        //   三级全不中 ⇒ 该域**每一个** (物料,阶段) 都静默走 3 天兜底（`STAGE_LEADTIME_MISSING`），
        //   参数表 282 行**从未生效**（本次实测确认，非推断）。⇒ 补一条**等价形态**比对：按参数行自己的两列
        //   `FactoryCode + "_" + StageCode` 重建权威值域（见 <see cref="JoinFactoryStage"/>）。
        //   **字面优先、等价兜底** ⇒ 对「本就字面能命中」的组合零影响；对「今后 3号位 把数据改成带前缀」
        //   亦兼容（那时字面分支直接命中，等价分支不再参与），故本次改动**不会**成为将来的障碍。
        bool StageOk(StageLeadTimeParamRow r) =>
            string.Equals(r.StageCode, stageCode, StringComparison.Ordinal)
            || string.Equals(JoinFactoryStage(r.FactoryCode, r.StageCode), stageCode, StringComparison.Ordinal);
        bool SameFactory(StageLeadTimeParamRow r) => SameFactoryCode(r, factoryCode);

        // ⚠️ 2026-09-29 用户明确口径（**字面**）：第三层就是「Stage」，**不带 `IsDefault` 条件**。
        //    即：只要该 StageCode 有参数行，第三层就命中；**不再要求该行声明 IsDefault = 1**。
        //    行为影响：本改动**不改变今天的任何结果** —— 实测 282 行的 `IsDefault` 全 = 1，
        //    即「带条件」与「不带条件」命中的是同一组行。故这是一次**口径对齐**，不是行为变更。
        //    已去掉的旧条件曾使 `IsDefault` 成为第三层的门槛；现该列在**本装载链上不再被读取**。
        var levels = new (Func<StageLeadTimeParamRow, bool> Match, string Level)[]
        {
            (r => !string.IsNullOrEmpty(deptCode) && string.Equals(r.ProductionDeptCode, deptCode, StringComparison.Ordinal)
                  && SameFactory(r) && StageOk(r), "DEPT_EXACT"),
            (r => SameFactory(r) && StageOk(r),    "FACTORY_STAGE_DEFAULT"),
            (r => StageOk(r),                      "GLOBAL_STAGE_DEFAULT")
        };

        foreach (var (match, level) in levels)
        {
            var hit = rows.Where(match)
                          .OrderBy(r => r.Priority)
                          .FirstOrDefault();
            if (hit is not null)
            {
                hit.MatchLevel = level;
                return hit;
            }
        }

        return null;
    }

    /// <summary>工厂口径：参数行未限厂时视为通配（冻结口径，FACTORY_STAGE_DEFAULT 依赖此语义）。</summary>
    private static bool SameFactoryCode(StageLeadTimeParamRow r, string? factoryCode) =>
        string.IsNullOrEmpty(factoryCode) || string.Equals(r.FactoryCode, factoryCode, StringComparison.Ordinal);

    /// <summary>
    /// 把 `StageLeadTimeParam` 的一行还原成**权威 StageCode 值域形态** = `{FactoryCode}_{StageCode}`。
    ///
    /// 【为什么需要】`StageCode` 在全库的权威形态**带工厂前缀**，四处相互独立的实测证据完全一致：
    ///   · `StageDict`（ODS `[MES_Integration]` 层，PM 认定的 StageCode 权威字典）29 行全部形如 `BJ_MACH` / `CN6_ASSY`；
    ///   · `APS_BOM_STAGE_PATH_RAW` 2,384,758 行 / 28 个 distinct 值 ⇒ 100% 带前缀；
    ///   · `RoutingOperation` 1,701,834 行 / 15 个 distinct 值 ⇒ 1,701,833 带前缀（唯一例外 = 1 行合成值 `SYN-STAGE-1`）；
    ///   · `RoutingStage` 539,419 行 / 14 个 distinct 值 ⇒ 100% 带前缀。
    ///   **全库唯一异类就是 `StageLeadTimeParam` 自己**：282 行的 `StageCode` 一律只有阶段大类（`MACH`），
    ///   而工厂落在**另一列** `FactoryCode`（`BJ`）⇒ 信息是完整的，只是被拆成了两列。
    ///
    /// 本方法即按这两列重建，**只读、不改数据、不改任何契约字段**。
    /// 【实测边界（2026-10-15）】282 行中 **270 行**拼接后落在 StagePath 值域内（25 个 (厂,阶段) 组合全部 `IN_STAGEPATH`）；
    ///   余 12 行**不是**本方法能猜的：11 行 `FactoryCode` 含脏字符（`CN6` + `U+8BFE`，hex `0x43004E003600FE8B`，
    ///   本应 `CN6`），1 行 `BJ|DRAW`（StagePath 中不存在任何 `*_DRAW`）。该 12 行仍走 3 天兜底。
    ///
    /// ⚠️ **值域统一终态仍归 3号位**：规则清单 S-004「StageLeadTimeParam 治理/维护 Owner=3号位；2号位 装载/匹配」，
    ///   07 字段文档 §366 已点名本表。本方法属「**匹配**」职责内的兼容；调用方把「走了本兼容」的条数
    ///   上抛日志（`normalizedMatchCount`）⇒ **不掩盖**数据侧问题。
    /// </summary>
    private static string JoinFactoryStage(string? factoryCode, string stageCode) =>
        string.IsNullOrEmpty(factoryCode) ? stageCode : factoryCode + "_" + stageCode;

    /// <summary>
    /// 是否存在**已被 V1 废止的匹配层级**（Material 级 / ProductFamily 级）的参数行。
    /// 0号位 2026-09-29 §4.1/§4.3：这两级「在V1不作为正常有效匹配级别」、3号位 后续停止发布。
    /// 判断与「本次是否命中」**无关**——旧口径下它们比 DEPT 级更细、会优先抢占命中，
    /// 所以只要存在，本次装载的命中层级与提前期就已经和旧口径不同，必须登记兼容告警
    /// （§4.3「运行中如遇旧数据，可登记兼容告警，不让1号位自行解释」），而不是静默忽略。
    /// </summary>
    private static bool HasLegacyLevelRow(
        IReadOnlyList<StageLeadTimeParamRow> rows,
        string? materialCode,
        string? familyCode,
        string? factoryCode,
        string stageCode)
    {
        if (string.IsNullOrEmpty(materialCode) && string.IsNullOrEmpty(familyCode))
            return false;

        return rows.Any(r => SameFactoryCode(r, factoryCode)
            && string.Equals(r.StageCode, stageCode, StringComparison.Ordinal)
            && ((!string.IsNullOrEmpty(materialCode)
                 && string.Equals(r.MaterialCode, materialCode, StringComparison.Ordinal))
                || (!string.IsNullOrEmpty(familyCode)
                    && string.Equals(r.ProductFamilyCode, familyCode, StringComparison.Ordinal))));
    }

    /// <summary>
    /// StageLeadTimeParam 参数行（解析用，含命中层级回填）。
    /// 2026-10-15：由 `private` 改 `internal`，供 `LPS.APS.Tests` 构造输入断言命中行为（同 <see cref="ResolveStageLeadTime"/>）。
    /// 本类**不实现任何接口**，故 Scrutor 的 `AsImplementedInterfaces()` 不会把它误注册为服务。
    /// </summary>
    internal sealed class StageLeadTimeParamRow
    {
        public string FactoryCode { get; set; } = string.Empty;
        public string StageCode { get; set; } = string.Empty;
        public string? ProductionDeptCode { get; set; }
        public string? MaterialCode { get; set; }
        public string? ProductFamilyCode { get; set; }
        // ⚠️ 必须可空：实测 282 行的 `LeadTimeHours` **全为 NULL**、值只落在 `LeadTimeDays`（5.00）。
        //    非空 decimal 遇上 DBNull 会抛，且会把"没给小时"和"小时=0"混为一谈。
        public decimal? LeadTimeDays { get; set; }
        public decimal? LeadTimeHours { get; set; }
        public int Priority { get; set; }
        public bool IsDefault { get; set; }

        /// <summary>诊断用：该行的 `EffectiveFrom` 是否为 NULL（= 参数发布方没填生效起点）。</summary>
        public bool EffectiveFromNull { get; set; }

        /// <summary>命中层级（ResolveStageLeadTime 回填，非表列）</summary>
        public string MatchLevel { get; set; } = string.Empty;
    }

    /// <summary>物料编码 + 产品族（提前期命中用）</summary>
    private sealed class StageLeadTimeMatRow
    {
        public int Id { get; set; }
        public string MaterialCode { get; set; } = string.Empty;
        public int? ProductFamilyId { get; set; }
    }

    private sealed class FamilyCodeRow { public int Id { get; set; } public string Code { get; set; } = string.Empty; }
    private sealed class DeptCodeRow { public int Id { get; set; } public string? SourceDeptCode { get; set; } }
    private sealed class FactoryCodeRow { public int Id { get; set; } public string Code { get; set; } = string.Empty; }

    /// <summary>MaterialCode → Id 反查行</summary>
    private sealed class MaterialIdCodeRow
    {
        public int Id { get; set; }
        public string MaterialCode { get; set; } = string.Empty;
    }

    /// <summary>
    /// 按当前 Domain 涉及的 MaterialId 装载 Routing 三件套。
    /// `effectiveStages`（本次 BOM 有效 Stage）非空时按 (MaterialId, StageCode) 过滤——见方法内注释；
    /// 部门锁定仍由 1号位消费 MaterialStageDepartmentContexts 执行（PM 最小 B）。
    /// 三件套缺失时 1号位将把对应新增生产需求判定为 Unscheduled。
    /// <para>
    /// `rejectedMaterials`（0号位 第十六轮 J16-02）＝**因 Stage 角色冲突被主动隔离**的物料。
    /// 它们的三件套**整片剔除** —— 不得沿用「物料未命中 StagePath 就不过滤」那条放行口径：
    /// 那是给**没有 Stage 事实**的物料用的，而这里的事实是「事实已确认冲突」。
    /// 剔除后该物料不产生任何 RoutingOperation ⇒ 1号位无从为其生成 Task ⇒ 需求按未排真相计入 U。
    /// </para>
    /// </summary>
    private async Task<(List<RoutingOperation> Operations, List<RoutingDependency> Dependencies, List<OperationResourceEligibility> Eligibility)>
        LoadRoutingContextAsync(
            IReadOnlyList<int> materialIds,
            IReadOnlyDictionary<int, HashSet<string>> effectiveStages,
            IReadOnlySet<int> rejectedMaterials,
            CancellationToken ct)
    {

        if (materialIds.Count == 0)
            return (new List<RoutingOperation>(), new List<RoutingDependency>(), new List<OperationResourceEligibility>());

        var swRouting = System.Diagnostics.Stopwatch.StartNew();
        long routingSqlMs;

        // TransferBatchSize 冻结口径（DDL v5.1.4）：批量/规划参数从原 Routing 拆出到 RoutingPlanningParam，
        // 不污染工艺事实层 RoutingOperation。故此处 LEFT JOIN RoutingPlanningParam 取 TransferBatchSize，
        // 键 = (MaterialId, RouteCode, PathId, OperationCode)——RoutingPlanningParam 非部门维度（无 ProductionDepartmentId），
        // 同一工序跨部门共享同一 TransferBatchSize。无参数行时 TransferBatchSize=NULL，1号位按无阈值跳过。
        // ro.SetupTime：兼容废弃（v1.2 §20.3/§21）。1号位 2026-09-20 回执确认 Solver 零读取 → 停填（不再 SELECT/装载）；
        // RoutingOperation.SetupTime 实体属性 + 物理列保留兼容废弃，新 Setup 走 SetupTransitionRule，禁止以此兜底。
        var operations = await QueryChunkedInAsync(materialIds, chunk =>
            _connectionManager.QueryAsync<RoutingOperation>(
                @"SELECT ro.MaterialId, ro.ProductionDepartmentId, ro.RouteCode, ro.PathId, ro.OperationCode,
                         ro.OperationName, ro.ProcessType, ro.StageCode, ro.OperationPlanningMode, ro.StandardDuration,
                         rpp.TransferBatchSize, ro.IsActive
                  FROM RoutingOperation ro
                  LEFT JOIN RoutingPlanningParam rpp
                    ON rpp.MaterialId    = ro.MaterialId
                   AND rpp.RouteCode     = ro.RouteCode
                   AND rpp.PathId        = ro.PathId
                   AND rpp.OperationCode = ro.OperationCode
                  WHERE ro.IsActive = 1 AND ro.MaterialId IN @MaterialIds",
                new { MaterialIds = chunk },
                db: DatabaseId.APS));

        var dependencies = await QueryChunkedInAsync(materialIds, chunk =>
            _connectionManager.QueryAsync<RoutingDependency>(
                @"SELECT MaterialId, ProductionDepartmentId, RouteCode, PathId,
                         FromOperationCode, ToOperationCode, DependencyType, LagTime, IsActive
                  FROM RoutingDependency
                  WHERE IsActive = 1 AND MaterialId IN @MaterialIds",
                new { MaterialIds = chunk },
                db: DatabaseId.APS));

        var eligibility = await QueryChunkedInAsync(materialIds, chunk =>
            _connectionManager.QueryAsync<OperationResourceEligibility>(
                @"SELECT MaterialId, ProductionDepartmentId, RouteCode, PathId, OperationCode,
                         ResourceId, Priority, CapacityFactor, IsPrimary, IsActive
                  FROM OperationResourceEligibility
                  WHERE IsActive = 1 AND MaterialId IN @MaterialIds",
                new { MaterialIds = chunk },
                db: DatabaseId.APS));

        routingSqlMs = swRouting.ElapsedMilliseconds;

        // ── J16-02／J17-01：**被隔离**的物料，三件套整片剔除 ──
        //   与下方 ① 的「未命中 StagePath ⇒ 不过滤」是**两条不同的口径**，必须分开：
        //     · 未命中 ＝ 本批没有该物料的 Stage 事实（数据缺口/采购件）⇒ 放行是不让它被判 Unscheduled；
        //     · 本集合 ＝ 有事实但不能用：角色冲突（一物料多条不同链）、请求身份歧义、
        //       首选 scope 无链（有行、但不在本请求 BOMNO／本角色 scope 下，且本次无任何链）⇒ 不得拿去做正式 Task。
        //   若不剔除，① 的 `!TryGetValue` 分支会为它放行全部工序 ⇒ 被隔离的需求经 Routing 复活，
        //   「隔离」在日志上成立、在 Task 表上不成立（0号位 第十六轮 J16-02；第十八轮 J17-01 追加第三类）。
        if (rejectedMaterials.Count > 0)
        {
            var rejectedOps = operations.Count(o => rejectedMaterials.Contains(o.MaterialId));
            operations = operations.Where(o => !rejectedMaterials.Contains(o.MaterialId)).ToList();
            dependencies = dependencies.Where(d => !rejectedMaterials.Contains(d.MaterialId)).ToList();
            eligibility = eligibility.Where(e => !rejectedMaterials.Contains(e.MaterialId)).ToList();

            if (rejectedOps > 0)
            {
                _logger.LogWarning(
                    "[Pegging] Stage 事实不可用的物料已剔除 Routing（J16-02／J17-01，Fail-Closed）：物料 {Count} 个、" +
                    "RoutingOperation {Ops} 条 ⇒ 不产任何 Task，其需求按未排真相计入 U（不得经「缺键放行」复活）",
                    rejectedMaterials.Count, rejectedOps);
            }
        }

        // ── 按「本次 BOM 有效 Stage」过滤三件套（PM 文档 §二十一/§二十二，2026-09-28）──
        // 规则：
        //   ① 工序：只保留 (MaterialId, StageCode) 命中 StagePath 的；
        //      · 未命中 StagePath 的物料（数据缺口 / 采购件）**不过滤**——保持既有「按 MaterialId 全量」兜底，
        //        避免因数据缺失把工序全过滤掉导致 1号位 误判 Unscheduled。
        //   ② 依赖：两端工序都必须存活（否则该边指向被裁掉的 Stage）；
        //   ③ 资质：按存活的 (MaterialId, ProductionDepartmentId, OperationCode) 三元组过滤。
        // 注：用「三元组」而非 OperationCode 单键——同一物料的不同 Stage 可能出现相同 OperationCode（实测 117 物料）。
        if (effectiveStages.Count > 0)
        {
            var beforeOps = operations.Count;
            var beforeDeps = dependencies.Count;
            var beforeElig = eligibility.Count;

            // ① 工序过滤
            var keptOps = operations
                .Where(o => !effectiveStages.TryGetValue(o.MaterialId, out var stages)
                            || string.IsNullOrEmpty(o.StageCode)
                            || stages.Contains(o.StageCode))
                .ToList();

            // 存活工序的三元组（供 ③ 资质过滤：资质自身的自然键 = (MaterialId, Dept, OperationCode)）
            var keptKeys = keptOps
                .Select(o => (o.MaterialId, o.ProductionDepartmentId, o.OperationCode))
                .ToHashSet();

            // ② 依赖：两端工序都必须存活。键 = (MaterialId, OperationCode) —— **不带部门维度**。
            //    理由（1号位 2026-09-28 回执 §三）：本判据是「工序是否**存在**」而非「选哪个」，
            //    存在性检查不需要部门维度；而原三元组键会**静默丢掉跨部门/跨 Stage 边** ——
            //    `RoutingDependency.ProductionDepartmentId` 是单值 int，一条跨部门边两端不可能同时命中同一部门键。
            //    同名工序在别处存活带来的**假阳性**（边多留一次）由 1号位 `validOperationKeys` 再判一次丢弃，不产生错排。
            var keptOpCodes = keptOps
                .Select(o => (o.MaterialId, o.OperationCode))
                .ToHashSet();

            var keptDeps = dependencies
                .Where(d => keptOpCodes.Contains((d.MaterialId, d.FromOperationCode))
                         && keptOpCodes.Contains((d.MaterialId, d.ToOperationCode)))
                .ToList();

            // ③ 资质：按存活三元组过滤
            var keptElig = eligibility
                .Where(e => keptKeys.Contains((e.MaterialId, e.ProductionDepartmentId, e.OperationCode)))
                .ToList();

            // ── 去静默（1号位 核验 §3.3 / 2026-09-28 回执 §六 第 4 项）──
            // 被丢的依赖边必须**分类可数**：否则「数据缺口（端点工序根本不存在）」与「合法裁 Stage」
            // 在日志里无法分辨。实测（2026-09-28，APS_Production）：1,156,465 条活跃依赖边中，
            // 端点不存在者 1,592/1,602 为 M 系（M101–M104 无设备/配品工序族，见 PM「无设备工序三分类」），非随机脏数据。
            var loadedOpCodes = operations
                .Select(o => (o.MaterialId, o.OperationCode))
                .ToHashSet();
            var droppedDeps = beforeDeps - keptDeps.Count;
            var absentDeps = dependencies.Count(d =>
                !loadedOpCodes.Contains((d.MaterialId, d.FromOperationCode))
                || !loadedOpCodes.Contains((d.MaterialId, d.ToOperationCode)));

            _logger.LogInformation(
                "[Pegging] 有效 Stage 过滤：工序 {OpsBefore}→{OpsAfter}，依赖 {DepsBefore}→{DepsAfter}（丢弃 {DepsDropped}：端点工序本批完全不存在 {DepsAbsent} / 仅被 Stage 裁掉 {DepsStageCut}），资质 {EligBefore}→{EligAfter}（需求物料={Mats}，命中 StagePath={HitMats}）",
                beforeOps, keptOps.Count, beforeDeps, keptDeps.Count,
                droppedDeps, absentDeps, droppedDeps - absentDeps,
                beforeElig, keptElig.Count,
                materialIds.Count, effectiveStages.Count);

            operations = keptOps;
            dependencies = keptDeps;
            eligibility = keptElig;
        }

        // ── S3b 人工槽 eligibility 展开（PM 0923 资源模型裁决）──
        // ManualCapacitySlot 主关联 = (ProductionDepartmentId, OperationName)——业务工序名才是「小工序」的 key；
        // OperationCode 只是工艺路线内顺序号（M101/M103…），无业务意义，2026-09-24 已随 5号位 重灌切换主关联。
        // 对每个启用槽，为该部门下所有 OperationName 匹配的 RoutingOperation 工序追加一条「合成 Resource」的 eligibility，
        // 合成 ResourceId = ManualSlotResourceOffset + ManualSlotId（与 S3a 装载投影同键）。
        // 合成键只进运行时 OperationResourceEligibility 列表，不触碰正式表。
        // 注：匹配键用 OperationName，但 eligible 行的 OperationCode 字段仍落各工序自身顺序号（见下方），
        //     因为它须与 1号位 按 RoutingOperation.OperationCode 对齐 eligibility 的口径一致。
        var manualSlots = (await _connectionManager.QueryAsync<ManualCapacitySlotKeyDto>(
            @"SELECT ManualSlotId, ProductionDepartmentId, OperationName
                FROM ManualCapacitySlot
               WHERE IsActive = 1",
            db: DatabaseId.APS)).ToList();

        if (manualSlots.Count > 0)
        {
            var rejectedManualSlots = 0;
            var rejectedManualSlotSamples = new List<string>(5);
            var opsByDeptOp = operations
                .GroupBy(o => (o.ProductionDepartmentId, o.OperationName))
                .ToDictionary(g => g.Key, g => g.ToList());

            var expanded = new List<OperationResourceEligibility>(eligibility);
            foreach (var slot in manualSlots)
            {
                if (!opsByDeptOp.TryGetValue((slot.ProductionDepartmentId, slot.OperationName), out var matchedOps))
                    continue;

                // Priority：设备(via ODS)小者优先；人工槽作为有限产能兜底，取较大固定值让真实设备优先（暂定，未 PM 裁决）。
                // 【2026-10-17 J14-08 改造】越界槽**不再入 Solver**：原为「仅告警、照常合成」，
                //   而 `1e9 + ManualSlotId` 溢出 int 后得到的是**负数**，`IsManualSlotId(负数)=false`
                //   ⇒ 落库侧拆键判据恒假、负数 ResourceId 会被当成真实设备写进 `Task.ResourceId`（死分支）。
                //   现改为跳过该槽（不写 eligibility、不参与评分），并聚合计数留痕。
                if (!SolverResourceProjection.IsWithinManualSlotRange(slot.ManualSlotId))
                {
                    rejectedManualSlots++;
                    if (rejectedManualSlotSamples.Count < 5)
                        rejectedManualSlotSamples.Add(
                            $"{slot.ManualSlotId}@{slot.ProductionDepartmentId}/{slot.OperationName}");
                    continue;
                }

                var syntheticResourceId = SolverResourceProjection.ManualSlotResourceOffset + slot.ManualSlotId;
                foreach (var op in matchedOps)
                {
                    expanded.Add(new OperationResourceEligibility
                    {
                        MaterialId           = op.MaterialId,
                        ProductionDepartmentId = op.ProductionDepartmentId,
                        RouteCode            = op.RouteCode,
                        PathId               = op.PathId,
                        OperationCode        = op.OperationCode,
                        ResourceId           = syntheticResourceId,
                        Priority             = 100,
                        CapacityFactor       = 1.0m,
                        IsPrimary            = false,
                        IsActive             = true
                    });
                }
            }
            eligibility = expanded;

            if (rejectedManualSlots > 0)
            {
                _logger.LogWarning(
                    "[Pegging] 越界人工槽已拒绝入 Solver（J14-08）：{Count}/{Total} 个槽的 ManualSlotId 不在 (0, {MaxGuard}] 内，" +
                    "合成 ResourceId 会溢出 int ⇒ 不写 eligibility、不参与评分。样本：{Samples}",
                    rejectedManualSlots, manualSlots.Count, SolverResourceProjection.ManualSlotIdMaxGuard,
                    string.Join("; ", rejectedManualSlotSamples));
            }
        }

        // ── 兜底诊断（2026-09-24 用户嘱「加入兜底功能即可，不需过度设计」）：仅日志、不落表、不改排程行为 ──
        // FINITE_RESOURCE 工序在「设备资质 + 人工槽展开」之后仍零合格资源 = 配置遗漏（新工序未同步建设备资质/人工槽）。
        // 这类工序会被 1号位 PhaseTwo 标 Unscheduled（结果兜底已有）；此处补一道「定位兜底」，
        // 让遗漏可一眼定位到 (物料,部门,工序名)，避免像早前配品 M101 那样静默翻 SQL。
        var eligibleKeys = eligibility
            .Select(e => (e.MaterialId, e.ProductionDepartmentId, e.RouteCode, e.PathId, e.OperationCode))
            .ToHashSet();

        var gapOps = operations
            .Where(o => o.OperationPlanningMode == "FINITE_RESOURCE")
            .Where(o => !eligibleKeys.Contains((o.MaterialId, o.ProductionDepartmentId, o.RouteCode, o.PathId, o.OperationCode)))
            .ToList();

        if (gapOps.Count > 0)
        {
            _logger.LogWarning(
                "[Pegging] 零合格资源兜底诊断：{Count} 道 FINITE_RESOURCE 工序无设备资质且无人工槽（疑似新工序遗漏配置，1号位将标 Unscheduled）。样例：{Sample}",
                gapOps.Count,
                string.Join("; ", gapOps.Take(20).Select(o =>
                    $"Mat={o.MaterialId},Dept={o.ProductionDepartmentId},Op={o.OperationCode}/{o.OperationName}")));
        }

        // ── V1 单路径归一化（2026-09-24）：冻结文档 RouteCode='DEFAULT'/PathId=1，MES 数据填入中文工艺路线名，须装载层规范化 ──
        // 此前原样透传给 1号位 → PhaseOne 按 RouteCode='DEFAULT' 过滤全丢 → 整域 FinalTasks=0。
        // 归一化后统一写 DEFAULT/PathId=1；同 MaterialId 同 OperationCode 的跨 RouteCode 冲突取首条 + Warning。
        NormalizeToSingleRoute(operations, "RouteCode归一化", _logger);
        var opSeen = new HashSet<(int, int, string, int, string)>();
        var opDeduped = operations
            .Where(o => opSeen.Add((o.MaterialId, o.ProductionDepartmentId, o.RouteCode, o.PathId, o.OperationCode)))
            .ToList();
        if (opDeduped.Count < operations.Count)
            _logger.LogWarning("[Pegging] RouteCode归一化: 工序去重 {Dup} 条（跨RouteCode同名OpCode冲突）", operations.Count - opDeduped.Count);
        operations = opDeduped;

        NormalizeToSingleRoute(dependencies, "RouteCode归一化(dep)", _logger);
        NormalizeToSingleRoute(eligibility, "RouteCode归一化(elig)", _logger);

        // ── 资质/依赖去重：归一化**自己制造的**重复（2026-09-29 真跑实测崩溃点）──
        // 🔴 现象（Run #3，PlanVersionId=632）：1号位 `PhaseOneConstraintBuilder.BuildOperationResourceEligibility`
        //    的 `g.ToDictionary(e => e.ResourceId, e => e.CapacityFactor)` 抛
        //    `An item with the same key has already been added. Key: 14235`，整域求解中断。
        // 根因：上游 MES 工艺对同一 (物料,部门,小工序) 存有**多条 RouteCode 变体**。实测
        //    ResourceId=14235 / 物料 5365690 / 部门 1031 / 工序 M102 —— 库内两行 RouteCode 分别为
        //    `ASSY` 与 `ASSY,组装`，**其余列全同**（Priority 同为 1）。`NormalizeToSingleRoute` 把两行都改写为
        //    RouteCode='DEFAULT' ⇒ 在 1号位 分组键 `EligibilityLookupKey(MaterialId, ProductionDepartmentId,
        //    RouteCode, OperationCode)`（1号位 `PhaseOneConstraintBuilder`）下**塌进同一组**，
        //    组内按 ResourceId 建字典 ⇒ 同键 14235 撞字典。**重复不是查询查出来的，是归一化造出来的。**
        // 实测规模（2026-09-29，APS_Production，按 1号位 撞键口径 = 分组键 + ResourceId，COUNT(*)>1）：
        //    **5,533 组 / 98 个 ResourceId / 2,278 个物料** ⇒ 任何真实全量跑必崩；小规模测试撞不到
        //    （与上面 2100 参数溢出同一类「只有真规模才暴露」）。
        // 为何工序没崩：紧接着对 `operations` 做的同键去重 + Warning（见 `[Pegging] RouteCode归一化: 工序去重`）；**资质这一路当时漏了**。
        // 故此处按**与 1号位 完全相同的撞键口径**去重（分组键 4 元 + ResourceId）。
        // 注：撞键两行在 1号位 可见字段上**逐列等同**（RouteCode 已同归一为 DEFAULT）⇒「取首条」无歧义、
        //    结果与取哪条无关。**不含 PathId**——1号位 分组键里没有它，带上它就会漏掉真正的撞键行。
        var eligSeen = new HashSet<(int, int, string, string, int)>();
        var eligDeduped = eligibility
            .Where(e => eligSeen.Add((e.MaterialId, e.ProductionDepartmentId, e.RouteCode, e.OperationCode, e.ResourceId)))
            .ToList();
        if (eligDeduped.Count < eligibility.Count)
            _logger.LogWarning(
                "[Pegging] RouteCode归一化(elig): 资质去重 {Dup} 条（跨 RouteCode 变体塌入同键，1号位 分组键=物料+部门+路线+工序，组内按资源建字典）",
                eligibility.Count - eligDeduped.Count);
        eligibility = eligDeduped;

        // 依赖同类问题：1号位 装进 `List<DependencyEdge>`（`preds.Add`，非字典 Add）⇒ 重复边**不抛异常**，
        // 但同一 from→to 会进两次成为**重复前驱边**。按边业务自然键去重；`DependencyType`/`LagTime` 不同的
        // **真并行边予以保留**（不作合并）。
        var depSeen = new HashSet<(int, string, string, string, decimal)>();
        var depDeduped = dependencies
            .Where(d => depSeen.Add((d.MaterialId, d.FromOperationCode, d.ToOperationCode, d.DependencyType, d.LagTime)))
            .ToList();
        if (depDeduped.Count < dependencies.Count)
            _logger.LogWarning(
                "[Pegging] RouteCode归一化(dep): 依赖去重 {Dup} 条（跨 RouteCode 变体同边）",
                dependencies.Count - depDeduped.Count);
        dependencies = depDeduped;

        // 【2026-10-26 细分埋点】`6 Routing三件套+资质` PV190 实测 8,726ms。只读实测已排除 SQL 是大头
        //   （RoutingOperation 12,253 物料单趟 157ms／单块 2,000 仅 15ms）⇒ 钱花在**装载后的过滤/归一化**。
        //   本行把「三次查询」与「后处理」分开报，下一轮直接看出该切哪一段。
        _logger.LogInformation(
            "[Pegging][求解请求分段-细分] 6 Routing三件套: SQL={SqlMs}ms / 过滤+归一化={PostMs}ms" +
            "（工序={Ops}、依赖={Deps}、资质={Elig}；物料={Mats}）",
            routingSqlMs, swRouting.ElapsedMilliseconds - routingSqlMs,
            operations.Count, dependencies.Count, eligibility.Count, materialIds.Count);

        return (operations, dependencies, eligibility);
    }

    private static void NormalizeToSingleRoute<T>(List<T> items, string label, ILogger? logger = null) where T : class
    {
        var changed = 0;
        foreach (var item in items)
        {
            var rc = item.GetType().GetProperty("RouteCode");
            var pi = item.GetType().GetProperty("PathId");
            if (rc != null && rc.CanWrite && rc.GetValue(item) is string rv && rv != "DEFAULT")
            {
                rc.SetValue(item, "DEFAULT");
                changed++;
            }
            if (pi != null && pi.CanWrite && pi.GetValue(item) is int pv && pv != 1)
            {
                pi.SetValue(item, 1);
            }
        }
        if (changed > 0)
            logger?.LogDebug("[Pegging] {Label}: 已归一化 {Count} 条 RouteCode→DEFAULT", label, changed);
    }

    /// <summary>
    /// ③b 供给阈值 Stage 填值（PM《BOM取用_Pegging_Stage_Routing完整链路说明》§八，2026-09-28）：
    /// 为每个新增生产需求回填「要做到哪个大工艺阶段即算可供给父件」。
    ///
    /// 【2026-09-29：范围扩为**需求 ∪ 承接行**、产出**物料级映射**供两个消费者共用】
    ///   本方法一次算好 `Dictionary&lt;MaterialId, RequiredStageCode&gt;` 并落 `voucher.RequiredStageByMaterialId`：
    ///     · 消费者① = `LogicalProductionDemand.RequiredStageCode`（运行输入，Solver 用）；
    ///     · 消费者② = `PeggingSupplyAllocation.NextRequiredStageCode`（追溯列）。
    ///   之所以不能只算「需求物料」：PM §十一 界定追溯列的用途是「追溯**为什么这个 Supply 可以承接这个需求**」，
    ///   追溯对象 = **全部承接关系**；而 `LogicalProductionDemands` **只为「有生产缺口」的需求生成**，
    ///   纯供给承接的需求不在其中 ⇒ 只按需求物料算，追溯列必然大面积留空
    ///   （实测 PlanVersionId=540：1,205 行只填出 40 行，而 BOM 侧 30,652 个物料带阈值）。
    ///   承接行物料取自 `voucher.SupplyAllocations[].SupplyMaterialId`；`ValidateEligibility` 红线1
    ///   保证供需同物料，故它即需求侧物料，**无需给 `SupplyAllocationItem` 加字段**。
    ///
    /// 【主源 = BOM 边的 `ChildRequiredStageCode`（按边、本批上下文）】
    ///   该字段由 `LoadBomSnapshotAsync` 按 `BatchNo + ResolvedBOMNO` 装载进 `BomEdge`（口径正确），
    ///   但此前**零消费**（1号位 2026-09-28 核验 §3.5 判为**漏接**）。此处即「翻正主次」：直接在内存消费，
    ///   不再另发一条全局查询。
    /// 【兜底 = StagePath 的 `IsSupplyThreshold = 1`】主源为空时按**本次批次**取
    ///   `StageScopeType='EDGE' AND IsSupplyThreshold=1`。原实现**无 `BatchNo`** 且 `First()` 任取，
    ///   正是 PM §二十一.3 明令禁止的「跨订单/BOM/父件汇总」形态（1号位 核验 §3.5）。
    /// 【末位 = null】两级都无 ⇒ 留空 = 保守口径（全工艺完成才可供给，与冻结 DDL 设计决策一致）。
    ///
    /// 实测（2026-09-28，APS_Production）：不存在「同一子件多个阈值 Stage」——
    ///   按 `(BatchNo, ChildMaterialCode)` 与按 `ChildMaterialCode` 全局 `HAVING COUNT(DISTINCT StageCode) &gt; 1`
    ///   的组数**均为 0**。故当前按边取单值与按物料取单值等价；但**按边取**保住了上下文，
    ///   多父件/多 BOM 结构一旦出现也不会静默任取（歧义时打 Warning）。
    ///
    /// 语义（1号位 2026-09-28 回执 §5.2）：落到 `LogicalProductionDemand.RequiredStageCode` 的值必须是
    ///   「做到此 Stage 即为终点」；**1号位 消费口径只认该字段、不看来源**。
    /// </summary>
    private async System.Threading.Tasks.Task FillRequiredStageCodesAsync(
        PeggingResultVoucher voucher,
        BomSnapshot bomSnapshot,
        long planVersionId,
        CancellationToken ct)
    {
        // ── 目标物料集合 = **需求物料 ∪ 承接行物料** ──
        //   为何要并承接行：PM §十一 明确本值的第二处用途是「追溯**为什么这个 Supply 可以承接这个需求**」，
        //   即追溯对象是**全部承接关系**；而 `LogicalProductionDemands` 只为「有生产缺口」的需求生成，
        //   纯供给承接的需求不在其中。若只按需求物料算，追溯列必然大面积留空。
        //   承接行 = `SupplyAllocations`；`SupplyAllocationItem` 无需求物料字段，但 `ValidateEligibility`
        //   红线1（`supply.MaterialId != demand.MaterialId` ⇒ 拒绝）保证供需同物料 ⇒ 用 `SupplyMaterialId` 即可。
        var targetMaterialIds = voucher.LogicalProductionDemands.Select(d => d.MaterialId)
            .Concat(voucher.SupplyAllocations.Select(a => a.SupplyMaterialId))
            .Distinct()
            .ToList();

        if (targetMaterialIds.Count == 0)
            return;

        // MaterialId → MaterialCode（键 = int，勿传 StringComparer）
        // ⚠️ **必须分片**（走 QueryChunkedInAsync）：Dapper 把 `IN @Ids` 展开成**逐个参数**，SQL Server 单次 RPC 上限 2100。
        //   真实规模实测（2026-09-29，PlanVersionId=632 / 4,109 单）：目标物料数超 2100 ⇒ 本方法抛
        //   「传入的请求具有过多的参数。该服务器支持最多 2100 个参数」⇒ `Pegging 失败 1/1` ⇒
        //   Task/Pegging/PSA **全 0**（整条 Pegging 被一个 IN 列表打断，日志里前面所有正常步骤都是假象）。
        //   小规模测试永远碰不到 —— 只有真实全量跑才暴露。
        var codeById = (await QueryChunkedInAsync(targetMaterialIds, chunk =>
                _connectionManager.QueryAsync<MaterialIdCodeRow>(
                    "SELECT Id, MaterialCode FROM Material WHERE Id IN @Ids",
                    new { Ids = chunk },
                    db: DatabaseId.APS)))
            .ToDictionary(r => r.Id, r => r.MaterialCode);

        if (codeById.Count == 0)
            return;

        // ── 主源：BOM 边 ChildRequiredStageCode，**逐需求按 `demand.BomNo` 取**（J14-03）──
        //   原实现把「同物料全部请求/全部父边」的阈值压成物料级字典、冲突时取先遇者 ⇒ 某请求的阈值
        //   会被写进另一个请求的需求。现按请求收窄；同请求内仍冲突 ⇒ **留 null（全工艺，保守方向）**+ WARNING，
        //   不与「取先遇者」妥协。
        var filledFromBomEdge = 0;
        var thresholdConflictDemands = 0;
        var noAnchorDemands = 0;
        var conflictMaterialIds = new HashSet<int>();

        // 无锚点回退视图（循环外**惰性**建一次，真有人走回退才建）。
        // 2026-10-26：原写法把 `ByParent.SelectMany(...).Where(e => e.ChildCode == code)` 放在**循环内**，
        //   每来一个无锚点需求就全量扫一遍 BOM 边；PV190 实测无锚点需求 19,482 个 ⇒
        //   `11 供给阈值Stage回填` **26,933ms**（占 ④ 构建求解请求 56,575ms 的 47%，第二轮才被 MarkBuild 照出来）。
        //   语义逐字不变：仍是「同子件、非空阈值、Trim 后去重」，只是把扫描提到循环外 O(E+D)。
        Dictionary<string, string[]>? thresholdsByChildAllRequests = null;

        foreach (var demand in voucher.LogicalProductionDemands)
        {
            if (!string.IsNullOrEmpty(demand.RequiredStageCode)) continue;
            if (!codeById.TryGetValue(demand.MaterialId, out var code)) continue;

            IReadOnlyCollection<string>? thresholds;
            if (!string.IsNullOrWhiteSpace(demand.BomNo)
                && bomSnapshot.ThresholdsByBomChild.TryGetValue(demand.BomNo.Trim(), out var byChild))
            {
                byChild.TryGetValue(code, out thresholds);
            }
            else
            {
                noAnchorDemands++;
                // 无请求锚点（旧链路/未建链接）：只能退回按物料，出现多值即视为冲突。
                thresholdsByChildAllRequests ??= bomSnapshot.ByParent
                    .SelectMany(g => g)
                    .Where(e => !string.IsNullOrEmpty(e.ChildRequiredStageCode))
                    .GroupBy(e => e.ChildCode, StringComparer.Ordinal)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Select(e => e.ChildRequiredStageCode!.Trim())
                              .Distinct(StringComparer.Ordinal)
                              .ToArray(),
                        StringComparer.Ordinal);

                thresholds = thresholdsByChildAllRequests.TryGetValue(code, out var fallbackThresholds)
                    ? fallbackThresholds
                    : null;
            }

            if (thresholds is null || thresholds.Count == 0)
                continue;

            if (thresholds.Count > 1)
            {
                thresholdConflictDemands++;
                conflictMaterialIds.Add(demand.MaterialId);
                _logger.LogWarning(
                    "[Pegging] 供给阈值歧义：子件 {ChildCode} 在本需求所属请求（BOMNO={BomNo}）内有多条不同阈值 Stage（{Stages}）" +
                    "⇒ 该需求 `RequiredStageCode` 留 null（按冻结保守口径「全工艺完成才可供给」），不任取。DemandKey={DemandKey}",
                    code, demand.BomNo ?? "(无锚点)", string.Join(",", thresholds), demand.DemandKey);
                continue;
            }

            demand.RequiredStageCode = thresholds.First();
            filledFromBomEdge++;
        }

        // ── 兜底：StagePath EDGE + IsSupplyThreshold=1，**按本次批次 + 按 BOMNO**（PM §二十一.3）──
        var stillPendingDemands = voucher.LogicalProductionDemands
            .Where(d => string.IsNullOrEmpty(d.RequiredStageCode))
            .ToList();
        var filledFromStagePath = 0;
        var usedBatchNo = "(未解析)";
        if (stillPendingDemands.Count > 0)
        {
            var batchNo = (await ResolveLinkedBatchNosAsync(planVersionId)).FirstOrDefault();
            if (string.IsNullOrEmpty(batchNo))
            {
                _logger.LogWarning(
                    "[Pegging] 供给阈值兜底未执行：无 OrderBomRequestLink（PlanVersionId={PlanVersionId}），{Count} 个需求留空（保守口径）",
                    planVersionId, stillPendingDemands.Count);
            }
            else
            {
                usedBatchNo = batchNo;
                var codes = stillPendingDemands
                    .Select(d => codeById.TryGetValue(d.MaterialId, out var c) ? c : null)
                    .Where(c => !string.IsNullOrEmpty(c))
                    .Select(c => c!)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                var thresholdRows = await QueryChunkedInAsync(codes, chunk =>
                    _connectionManager.QueryAsync<StageThresholdRow>(
                        @"SELECT DISTINCT ChildMaterialCode, BOMNO, StageCode
                          FROM APS_BOM_STAGE_PATH_RAW
                          WHERE BatchNo = @BatchNo
                            AND StageScopeType = 'EDGE'
                            AND IsSupplyThreshold = 1
                            AND StageCode IS NOT NULL
                            AND ChildMaterialCode IN @Codes",
                        new { BatchNo = batchNo, Codes = chunk },
                        db: DatabaseId.APS));

                var byBomCode = new Dictionary<(string BomNo, string Code), HashSet<string>>();
                var byCode = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                foreach (var r in thresholdRows)
                {
                    if (string.IsNullOrEmpty(r.StageCode)) continue;
                    var bomNo = r.BOMNO?.Trim() ?? string.Empty;
                    var key = (bomNo, r.ChildMaterialCode);
                    if (!byBomCode.TryGetValue(key, out var set))
                        byBomCode[key] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(r.StageCode);

                    if (!byCode.TryGetValue(r.ChildMaterialCode, out var legacySet))
                        byCode[r.ChildMaterialCode] = legacySet = new HashSet<string>(StringComparer.Ordinal);
                    legacySet.Add(r.StageCode);
                }

                foreach (var demand in stillPendingDemands)
                {
                    if (!codeById.TryGetValue(demand.MaterialId, out var code)) continue;

                    HashSet<string>? hits = null;
                    if (!string.IsNullOrWhiteSpace(demand.BomNo))
                        byBomCode.TryGetValue((demand.BomNo.Trim(), code), out hits);
                    else
                        byCode.TryGetValue(code, out hits);

                    if (hits is null || hits.Count == 0) continue;
                    if (hits.Count > 1)
                    {
                        conflictMaterialIds.Add(demand.MaterialId);
                        _logger.LogWarning(
                            "[Pegging] 供给阈值兜底歧义：子件 {ChildCode} 在 BOMNO={BomNo} 有多个 IsSupplyThreshold=1 的 Stage（{Stages}）" +
                            "⇒ 留 null（全工艺，保守）。DemandKey={DemandKey}",
                            code, demand.BomNo ?? "(无锚点)", string.Join(",", hits), demand.DemandKey);
                        continue;
                    }

                    demand.RequiredStageCode = hits.First();
                    filledFromStagePath++;
                }
            }
        }

        // ── 落给追溯列消费（PM §十一 第二处保留）──
        // 追溯列按**承接行自身物料**直接命中，不经 `LogicalProductionDemand.DemandKey` 中转
        // （LPD 只为有生产缺口的需求生成 ⇒ 中转必然漏掉纯供给承接行）。
        // 同一物料在不同请求下阈值不一致 ⇒ **不写**（不能把某请求的阈值冒充该物料的全局结论）。
        var stageByMaterialId = new Dictionary<int, string>();
        foreach (var demand in voucher.LogicalProductionDemands)
        {
            if (string.IsNullOrEmpty(demand.RequiredStageCode)) continue;
            if (conflictMaterialIds.Contains(demand.MaterialId)) continue;

            if (stageByMaterialId.TryGetValue(demand.MaterialId, out var existing)
                && !string.Equals(existing, demand.RequiredStageCode, StringComparison.Ordinal))
            {
                stageByMaterialId.Remove(demand.MaterialId);
                conflictMaterialIds.Add(demand.MaterialId);
                continue;
            }

            stageByMaterialId[demand.MaterialId] = demand.RequiredStageCode!;
        }

        voucher.RequiredStageByMaterialId = stageByMaterialId;

        var unresolved = voucher.LogicalProductionDemands.Count(d => string.IsNullOrEmpty(d.RequiredStageCode));
        _logger.LogInformation(
            "[Pegging] 供给阈值 Stage 回填：主源 BOM 边填需求 {FromEdge} 个 / 兜底 StagePath(本批 {BatchNo}) 补需求 {FromPath} 个 / " +
            "物料映射共 {MapSize} 个 / 需求仍留空 {Unresolved} 个 / 阈值冲突需求 {Conflicts} 个（已留 null=全工艺）/ 无请求锚点需求 {NoAnchor} 个" +
            "（需求总数 {Total}，承接行总数 {AllocTotal}）",
            filledFromBomEdge, usedBatchNo, filledFromStagePath, stageByMaterialId.Count, unresolved,
            thresholdConflictDemands, noAnchorDemands,
            voucher.LogicalProductionDemands.Count, voucher.SupplyAllocations.Count);
    }

    /// <summary>
    /// **AllocationTaskShare 不变量校验（PM《回复0928-2》§三/§六 裁决，fail-closed）**
    ///
    /// PM 冻结的 4 条不变量：
    /// - **Inv1** 每 `(AllocationSequence, FinalDraftId)` 至多一行 —— 防止一个 Task 重复写份额；
    /// - **Inv2** 同 `AllocationSequence` 的 `Σ ShareQty` ＋ `Σ UnscheduledQty` == 该 seq 全部 Demand 的 `NetOutputQty` 之和（三量闭合）；
    /// - **Inv3** 按 `DemandKey` 聚合不得因「一个 TaskNo 多 Operation Task」膨胀 —— 由 Inv1 + 「仅末端 Task 记份额」
    ///   共同在**产出侧**保证；其**落库后**的 DB 级校验见 `ValidateStoredShareInvariantsAsync`；
    /// - **Inv4** 没有生产任务不得产生份额。
    ///
    /// **为何 fail-closed**：PM §六 明确「防重复不是显示问题，而是 Candidate 正确性的前置条件」——
    /// 份额一旦膨胀，`ComputeChangeSeedKeysAsync` 的 `SUM(ShareQty) BY DemandKey` 基线立即错误，
    /// 导致候选排程定位到错误的受影响范围。宁可不落库，也不给候选留错误基线（与「禁止静默回退」红线一致）。
    /// </summary>
    /// <remarks>
    /// 2026-10-17：由 `private` 改 **`internal`**，仅供 `LPS.APS.Tests`（`InternalsVisibleTo`）
    /// 直接断言「某 Allocation 一条份额行都没有、且未记 U ⇒ 必须判 Inv2 违例」（1号位 第二十二轮 §11.4 ②）。
    /// 生产路径调用点不变。
    /// </remarks>
    internal void ValidateAllocationShareInvariants(
        DomainSolveResult solveResult,
        PeggingResultVoucher voucher,
        IReadOnlyDictionary<string, long> finalDraftToTaskId,
        IReadOnlySet<string>? excludedDraftIds = null)
    {
        if (solveResult.AllocationShares.Count == 0)
            return;

        var errors = new List<string>();

        // 被隔离（Fail-Closed 不落库）的 Draft：其份额既不出现在 Inv4（无 Task），也不参与 Inv2 的闭合目标 ——
        //   闭合只在「本轮真正落库的那部分」上成立；被隔离量由未排净量（B8）与三量对账（B10）显性体现。
        var excluded = excludedDraftIds ?? new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<AllocationTaskShare> shares = excluded.Count == 0
            ? solveResult.AllocationShares
            : solveResult.AllocationShares.Where(s => !excluded.Contains(s.FinalDraftId)).ToList();

        if (shares.Count == 0)
        {
            _logger.LogWarning(
                "[AllocationTaskShare] 全部 {Rows} 条份额均属被隔离 Draft（部门事实缺失）⇒ 本轮无份额可校验、无份额落库",
                solveResult.AllocationShares.Count);
            return;
        }

        // Inv1：同 (AllocationSequence, FinalDraftId) 多行
        var inv1 = shares
            .GroupBy(s => (s.AllocationSequence, s.FinalDraftId))
            .Where(g => g.Count() > 1)
            .Take(5)
            .Select(g => $"(seq={g.Key.AllocationSequence}, draft={g.Key.FinalDraftId})×{g.Count()}")
            .ToList();
        if (inv1.Count > 0)
            errors.Add($"Inv1 同分配+同Task多行：{string.Join("; ", inv1)}");

        // Inv4：份额指向的 Draft 无对应生产任务（被隔离 Draft 的份额已在 `shares` 中剔除，不算违例）
        var inv4 = shares
            .Where(s => !finalDraftToTaskId.ContainsKey(s.FinalDraftId))
            .Select(s => s.FinalDraftId)
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToList();
        if (inv4.Count > 0)
            errors.Add($"Inv4 份额指向无生产任务的Draft：{string.Join("; ", inv4)}");

        // Inv2：按 AllocationSequence 闭合（闭合目标 = 该 seq 全部 Demand 的 NetOutputQty 之和）
        //   有隔离 Draft 时，闭合目标同步扣掉这些 Draft 的份额：恒等式只在「本轮真正落库的那部分」上成立，
        //   否则隔离本身会被判成 Inv2 违例 ⇒ 违背「逐 Task 隔离、仅全缺才升级 Domain 失败」。
        var expectedByAlloc = voucher.LogicalProductionDemands
            .GroupBy(d => d.AllocationSequence)
            .ToDictionary(g => g.Key, g => g.Sum(d => d.NetOutputQty));
        if (excluded.Count > 0)
        {
            foreach (var s in solveResult.AllocationShares.Where(s => excluded.Contains(s.FinalDraftId)))
            {
                if (expectedByAlloc.ContainsKey(s.AllocationSequence))
                    expectedByAlloc[s.AllocationSequence] -= s.ComponentQty;
            }
        }
        var actualByAlloc = shares
            .GroupBy(s => s.AllocationSequence)
            .ToDictionary(g => g.Key, g => g.Sum(s => s.ComponentQty));

        // 未排量 U：`UnscheduledTaskResult.DraftId` 的值域是 `LogicalDemandKey`，按它回查所属 Allocation。
        //   归属不到的 U **不静默丢弃**，直接判违例 —— 键域错配若被吞掉，三量对账会退化成二量对账。
        var allocByDemandKey = voucher.LogicalProductionDemands
            .GroupBy(d => d.LogicalDemandKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().AllocationSequence, StringComparer.Ordinal);

        var unscheduledByAlloc = new Dictionary<long, decimal>();
        var unattributedU = new List<string>();
        foreach (var u in solveResult.UnscheduledTasks)
        {
            if (u.UnscheduledQty <= 0m)
                continue;

            if (!allocByDemandKey.TryGetValue(u.DraftId, out var allocSeq))
            {
                unattributedU.Add(u.DraftId);
                continue;
            }

            unscheduledByAlloc[allocSeq] = unscheduledByAlloc.GetValueOrDefault(allocSeq) + u.UnscheduledQty;
        }

        if (unattributedU.Count > 0)
        {
            errors.Add($"Inv2 未排量无法归属到Allocation（DraftId 非 LogicalDemandKey）：" +
                       string.Join("; ", unattributedU.Distinct(StringComparer.Ordinal).Take(5)));
        }

        // 有份额却无对应 Demand：仍属违例，单列一轮。
        foreach (var kv in actualByAlloc)
        {
            if (!expectedByAlloc.ContainsKey(kv.Key))
                errors.Add($"Inv2 seq={kv.Key} 有份额但无对应 Demand");
        }

        // 主判据按 `expectedByAlloc` 遍历 —— Allocation 有无份额都必须过判。
        foreach (var kv in expectedByAlloc)
        {
            var expected = kv.Value;
            var actual = actualByAlloc.GetValueOrDefault(kv.Key);

            // 三量对账：已排份额 S ＋ 未排量 U 必须覆盖该 Allocation 的声明量 Q。
            var unscheduledQty = unscheduledByAlloc.GetValueOrDefault(kv.Key);
            var settledPlusUnscheduled = actual + unscheduledQty;
            // **恒等闭合**（容差 1e-3，见 `allocatedqty-identity-and-four-layer-closure` §四：ΣShareτ 与 Task.Quantity
            //   的 3 处「违反」全是 4 位小数舍入差 +0.0007，闭合断言必须显式声明容差）。
            //
            //   判据依据（冻结正文，逐字）：
            //     · `APS_Pegging…v1.6_20261005…md:746-748`「1号位 排完后：`AllocationQty = Σ AllocationTaskShare
            //       中的净合格产出份额`」—— **等式**，不是不等式；
            //     · 同件 `:752-761` 硬错误八条第 6 条「**TaskShare 与 Allocation 不闭合**」⇒「当前 Domain **不能发布**」；
            //     · 同件 §18.2「必须使 Domain 失败，**不能 Warning 后发布**」。
            //   另一条独立红线 `07_…v5.1.13…md:3731`「每 FinalTask：各 `ShareQty` 之和**不得超过** `Task.Quantity`」
            //   是**逐 Task 的单侧不等式**，与本节**逐 Allocation 的等式**是两条不同的线，不得互相顶替。
            //
            //   **三量口径**（0号位 第二十轮 §三.2／§三.3 ＋ 1号位 2026-10-17 §十一.1）：
            //   闭合对象是 `Σ ShareQty ＋ Σ U ＝ Σ Q`，不是 `Σ ShareQty ＝ Σ Q`。
            //   生成侧闭合目标取**甲式** `closureTarget = 声明量 Q − 未排量 U`（U 由未排清单独立算出，
            //   **不得**由份额反推，否则本判据退化为恒真）；本侧按同一口径校验，等式两侧仍相等。
            //   因此本判据**不得**改成单侧不等式 `ΣShare ≤ ΣQ`：那会把「未排量漏记」放过，等于把硬校验做空。
            //   遍历集合取 `expectedByAlloc ∪ actualByAlloc`：某 Allocation **一条份额行都没有**时**仍要判**
            //   （`ΣShare=0 ＋ ΣU` 必须等于 Q）。否则「Task 全部有下游、既无份额又未记 U」会静默通过，
            //   与该形态「显性失败、交 Routing 排查」的既定处理相反。
            if (settledPlusUnscheduled - expected > 0.001m || expected - settledPlusUnscheduled > 0.001m)
            {
                errors.Add(
                    $"Inv2 seq={kv.Key} TaskShare与Allocation不闭合：ΣShareQty+ΣU={settledPlusUnscheduled}"
                    + $"（ΣShareQty={actual} + ΣU={unscheduledQty}）≠ ΣNetOutputQty={expected}"
                    + $"（差 {settledPlusUnscheduled - expected}）");
            }
        }

        if (errors.Count > 0)
        {
            foreach (var e in errors)
                _logger.LogError("[AllocationTaskShare] 不变量校验失败: {Error}", e);
            throw new InvalidOperationException(
                $"AllocationTaskShare 不变量校验失败（{errors.Count} 项）——已阻止落库，避免污染 Candidate 变化基线：{string.Join(" | ", errors)}");
        }

        _logger.LogInformation(
            "[AllocationTaskShare] 不变量校验通过：份额行={Rows}（隔离剔除 {Excluded}），分配数={Allocs}",
            shares.Count, solveResult.AllocationShares.Count - shares.Count, actualByAlloc.Count);
    }

    /// <summary>
    /// **落库后 DB 级份额不变量校验**（PM《回复0928-2》§六：Candidate ChangeSeed **读取前**必须校验）。
    ///
    /// 与 `ValidateAllocationShareInvariants`（产出侧、内存）互补：本方法查**已落库**的 Base 版本，
    /// 覆盖「历史数据被其它路径写脏」的情形（唯一约束要到阶段C 才加，故此刻必须靠读取前校验兜住）。
    /// 查 Inv1 / **Inv2** / Inv3 / Inv4；任一 &gt; 0 即 fail-closed。
    ///
    /// 【Inv2 的落库口径（2026-10-19 补）】Q 与 U 只能从 `ScheduleExplanationFact.EvidenceJson` 取
    /// （逐 Demand 的 `NetOutputQty`/`UnscheduledQty`，`LogicalProductionDemand` 本身不落表），
    /// S 取 `AllocationTaskShare.ShareQty`，两侧都**按最新一批**对齐（G-21：同 PV 多批并存）。
    /// 只有份额、没有需求事实行的 seq 判为**不可判定**（历史批在 J16-03 前不为「已排满」的需求写账行），
    /// 单列计数并告警，不据此 fail-closed —— 否则会让旧批的已知形态阻塞 Candidate。
    /// </summary>
    private async System.Threading.Tasks.Task ValidateStoredShareInvariantsAsync(int planVersionId)
    {
        var row = await _connectionManager.QueryFirstOrDefaultAsync<ShareInvariantRow>(
            @";WITH factLatest AS (
                  SELECT MAX(CreatedAt) AS B FROM ScheduleExplanationFact WHERE PlanVersionId = @PlanVersionId
              ),
              atsLatest AS (
                  SELECT MAX(CreatedAt) AS B FROM AllocationTaskShare WHERE PlanVersionId = @PlanVersionId
              ),
              -- 逐 Demand 三量：Q/U 只能从 EvidenceJson 取（`LogicalProductionDemand` 无落库表）。
              --   键集与写入侧同源：`LogicalDemandKey` 空的行是资源等待等外壳行，不参与需求对账。
              facts AS (
                  SELECT TRY_CONVERT(bigint, JSON_VALUE(f.EvidenceJson, '$.AllocationSequence')) AS Seq,
                         JSON_VALUE(f.EvidenceJson, '$.LogicalDemandKey')                     AS DemandKey,
                         TRY_CONVERT(decimal(38,6), JSON_VALUE(f.EvidenceJson, '$.NetOutputQty'))   AS Q,
                         TRY_CONVERT(decimal(38,6), JSON_VALUE(f.EvidenceJson, '$.UnscheduledQty')) AS U
                  FROM ScheduleExplanationFact f CROSS JOIN factLatest b
                  WHERE f.PlanVersionId = @PlanVersionId
                    AND f.CreatedAt = b.B
                    AND f.ObjectType = 'ORDER'
                    AND ISJSON(f.EvidenceJson) = 1
                    AND JSON_VALUE(f.EvidenceJson, '$.LogicalDemandKey') IS NOT NULL
              ),
              demand AS (
                  SELECT Seq, DemandKey, MAX(Q) AS Q, MAX(U) AS U
                  FROM facts GROUP BY Seq, DemandKey
              ),
              q AS (SELECT Seq, SUM(Q) AS Q, SUM(U) AS U FROM demand WHERE Seq IS NOT NULL GROUP BY Seq),
              s AS (
                  SELECT a.AllocationSequence AS Seq, SUM(a.ShareQty) AS S
                  FROM AllocationTaskShare a CROSS JOIN atsLatest b
                  WHERE a.PlanVersionId = @PlanVersionId AND a.CreatedAt = b.B
                  GROUP BY a.AllocationSequence
              ),
              cmp AS (
                  SELECT q.Seq, q.Q, q.U, ISNULL(s.S, 0) AS S,
                         CASE WHEN q.Seq IS NULL THEN 1 ELSE 0 END AS Unverifiable
                  FROM q FULL OUTER JOIN s ON s.Seq = q.Seq
              )
              SELECT
                (SELECT COUNT(*) FROM (
                    SELECT AllocationSequence, TaskId FROM AllocationTaskShare
                    WHERE PlanVersionId = @PlanVersionId
                    GROUP BY AllocationSequence, TaskId HAVING COUNT(*) > 1) a) AS Inv1,
                (SELECT COUNT(*) FROM (
                    SELECT s.AllocationSequence, t.TaskNo FROM AllocationTaskShare s
                    JOIN [Task] t ON t.Id = s.TaskId
                    WHERE s.PlanVersionId = @PlanVersionId
                    GROUP BY s.AllocationSequence, t.TaskNo HAVING COUNT(DISTINCT s.TaskId) > 1) b) AS Inv3,
                (SELECT COUNT(*) FROM AllocationTaskShare s
                 WHERE s.PlanVersionId = @PlanVersionId
                   AND NOT EXISTS (SELECT 1 FROM [Task] t WHERE t.Id = s.TaskId)) AS Inv4,
                -- Inv2：ΣShareQty + ΣU 必须闭合到 ΣQ（逐 AllocationSequence，容差 1e-3，与内存侧同口径）
                (SELECT COUNT(*) FROM cmp
                 WHERE Unverifiable = 0 AND ABS(ISNULL(Q,0) - (S + ISNULL(U,0))) > 0.001) AS Inv2Violations,
                -- 只有份额、无 Demand 事实行的 seq：**不可判定**，不是违例（历史批在 J16-03 前不给
                -- 「已排满」的需求写账行 ⇒ 其 Q 无处可取）。单独计数、告警，不 fail-closed。
                (SELECT COUNT(*) FROM cmp WHERE Unverifiable = 1 AND S > 0) AS Inv2Unverifiable,
                (SELECT COUNT(*) FROM cmp WHERE Unverifiable = 0) AS Inv2Checked",
            new { PlanVersionId = planVersionId },
            db: DatabaseId.APS);

        var inv1 = row?.Inv1 ?? 0;
        var inv3 = row?.Inv3 ?? 0;
        var inv4 = row?.Inv4 ?? 0;
        var inv2 = row?.Inv2Violations ?? 0;
        var inv2Unverifiable = row?.Inv2Unverifiable ?? 0;
        var inv2Checked = row?.Inv2Checked ?? 0;

        if (inv2Unverifiable > 0)
        {
            _logger.LogWarning(
                "[AllocationTaskShare] 落库 Inv2 判定受限（PlanVersionId={PlanVersionId}）：{Count} 个 AllocationSequence 只有份额、" +
                "没有逐 Demand 事实行（其 Q 不可得）⇒ 这些 seq 未纳入 Inv2 判定；已判定 {Checked} 个",
                planVersionId, inv2Unverifiable, inv2Checked);
        }

        if (inv1 > 0 || inv3 > 0 || inv4 > 0 || inv2 > 0)
        {
            _logger.LogError(
                "[AllocationTaskShare] 落库数据不变量校验失败（PlanVersionId={PlanVersionId}）：Inv1={Inv1}, Inv2={Inv2}, Inv3={Inv3}, Inv4={Inv4}",
                planVersionId, inv1, inv2, inv3, inv4);
            throw new InvalidOperationException(
                $"AllocationTaskShare 落库数据违反不变量（PlanVersionId={planVersionId}）：Inv1={inv1}, Inv2={inv2}, Inv3={inv3}, Inv4={inv4} —— 拒绝据此计算 Candidate ChangeSeed（份额膨胀会直接污染变化基线）");
        }
    }

    /// <summary>份额不变量 DB 校验结果行</summary>
    private sealed class ShareInvariantRow
    {
        public int Inv1 { get; set; }
        public int Inv3 { get; set; }
        public int Inv4 { get; set; }
        public int Inv2Violations { get; set; }
        public int Inv2Unverifiable { get; set; }
        public int Inv2Checked { get; set; }
    }

    /// <summary>
    /// 组装 PM《Stage、生产部门、Routing、Dependency、StageLeadTimeParam 接口裁决回复》(2026-09-28) §三
    /// 定义的 **`EffectiveStagePath`**：2号位 提供给 1号位 的**业务事实** =
    /// `MaterialId + StageCode + StageSeq + ProductionDepartmentId`（四条一组，不是只有码与序号）。
    ///
    /// 【部门来源】`MaterialStageDeptContext` —— PM §四/§六 逐字：
    ///   「StageCode 不能直接决定生产部门，必须通过 MaterialStageDeptContext 确定」「Stage 不是部门，
    ///    Stage 必须经过 Master 裁决」。
    /// 【null 语义】MSC 无 (MaterialId, StageCode) 映射 ⇒ 该步部门未知，**不推导、不猜**；
    ///   1号位 按既有最小B 口径记 `MISSING_PRODUCTION_DEPARTMENT_CONTEXT` 并置 Unscheduled。
    /// 【PM §三 分工】2号位 只提供业务事实，**不生成 Solver 内部对象**（StageDependency 由 1号位 自建）。
    /// </summary>
    private static List<StageSequenceChain> BuildEffectiveStagePaths(
        IReadOnlyList<StageSequenceChain> chains,
        IReadOnlyList<MaterialStageDepartmentContextDto> deptContexts)
    {
        var deptByMaterialStage = deptContexts
            .GroupBy(c => (c.MaterialId, c.StageCode))
            .ToDictionary(g => g.Key, g => g.First().ProductionDepartmentId);

        return chains
            .Select(chain => new StageSequenceChain
            {
                MaterialId = chain.MaterialId,
                Stages = chain.Stages
                    .Select(s => new StageSequenceStep
                    {
                        StageCode = s.StageCode,
                        StageSeq  = s.StageSeq,
                        ProductionDepartmentId =
                            deptByMaterialStage.TryGetValue((chain.MaterialId, s.StageCode), out var dept)
                                ? dept
                                : null
                    })
                    .ToList()
            })
            .ToList();
    }

    /// <summary>
    /// 解析「本次 BOM 批次」——与 `LoadBomSnapshotAsync` 同口径：
    /// 经 `OrderBomRequestLink` 取本 PlanVersion 关联的批次；SQL 已按 `SyncedAt DESC` 排序，
    /// 故返回列表的**首个 = 最新批次**。返回**全部**关联批次，供调用方同时派生「最新批次」与「多批次诊断」。
    /// </summary>
    private async Task<List<string>> ResolveLinkedBatchNosAsync(long planVersionId)
    {
        var rows = await _connectionManager.QueryAsync<BatchLinkRow>(
            @"SELECT r.BatchNo
              FROM OrderBomRequestLink r
              INNER JOIN [Order] o ON o.Id = r.OrderId
              WHERE o.PlanVersionId = @PlanVersionId AND r.BatchNo IS NOT NULL
              ORDER BY r.SyncedAt DESC",
            new { PlanVersionId = planVersionId },
            db: DatabaseId.APS);

        return rows.Select(r => r.BatchNo ?? string.Empty)
                   .Where(b => !string.IsNullOrEmpty(b))
                   .ToList();
    }

    /// <summary>
    /// 把「(批次, 物料, StageScope) → { StageCode → MIN(StageSeq) }」并入「(物料, StageScope) → 同形」，
    /// **逐 StageCode 取 MIN**。
    ///
    /// 【为什么要这个合并】1.c) 的兜底 StagePath 原先是一次性查全部关联批、`GROUP BY` 里不含批次
    /// ⇒ 得到的就是「跨关联批取 MIN」。现在拆成「1b 已按批装过的部分（内存合并）＋ 1b 未装过的残余批（查库）」，
    /// 靠的正是 **MIN 可结合**：`MIN(MIN(已装), MIN(残余)) ≡ MIN(全部)`，故取值逐位不变。
    ///
    /// ⚠ 只允许用于 StageSeq 这类「越小越优先」的序列号；换成 max／首行这类不满足结合律的口径**不可**照搬。
    /// </summary>
    internal static void MergeStageSeq(
        Dictionary<(string MaterialCode, string Scope), Dictionary<string, int>> target,
        string materialCode,
        string scope,
        IEnumerable<KeyValuePair<string, int>> stages)
    {
        var key = (materialCode, scope);
        if (!target.TryGetValue(key, out var merged))
            target[key] = merged = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var kv in stages)
        {
            if (!merged.TryGetValue(kv.Key, out var existing) || kv.Value < existing)
                merged[kv.Key] = kv.Value;
        }
    }

    /// <summary>
    /// 解析「生产指示号 → 本次 BOM 批次」：经 `[Order].MTS_InstructionNo` + `OrderBomRequestLink` 桥接。
    ///
    /// 用途：`LoadPiPositionsAsync` 的 Stage 顺序装载要**按每条 WIP 行自身的 PI 所属批次**收窄
    /// （而非全局 `MIN(StageSeq)`，PM §二十一.3）。一个 PI 若关联多个批次（异常情形），
    /// 取**字典序最小**者以保证结果稳定可复现——不取「最新」，避免同一份输入两次运行得到不同 Stage 顺序。
    /// </summary>
    private async Task<Dictionary<string, string>> LoadBatchByPiAsync(List<string> piNos, int planVersionId)
    {
        var batchByPi = new Dictionary<string, string>(StringComparer.Ordinal);
        if (piNos.Count == 0)
            return batchByPi;

        // 🔴 必须锁死本 Run 的 PlanVersionId：`MTS_InstructionNo` 同样是**跨 PlanVersionId 重复的业务键**，
        // 不过滤会把历史版本副本上的批次一并捞进来。`OrderBomRequestLink.PlanVersionId` 实测与其
        // `[Order].PlanVersionId` **逐行相等**（22,145 行零例外），故按 `o` 侧限定即可覆盖两侧。
        // ⚠ 2026-10-12：此处**不得**用 `IN @PiNos`（Dapper 展开为逐值参数）。PV2 全量 Pegging 实测：
        //   PI 权威事实 90,471 个 ⇒ 46 批 × 2,000 个 `@PiNosN nvarchar(4000)` 参数，
        //   `sys.dm_exec_requests` 抓到该语句**单次执行 59,900ms**（`status=running`、`wait_type=NULL`
        //   ＝真在跑，不是在等锁），而**同一语句、同一参数集**在 sqlcmd 下 <1s（实测量级：0.03–0.7s）。
        //   命令超时（30s）到点即 `SqlException 执行超时已过期`，异常沿
        //   LoadBatchByPiAsync → LoadPiPositionsAsync → LoadSupplyPoolAsync 冒到顶 ⇒ **整个 Pegging 中断**。
        //   （≥3 个集成测试与「装载验收」验收探针 3/3 复现；与本轮快照/ReasonCode 改动无关，属既有缺陷。）
        //   ⇒ 改为「单个 JSON 参数 + OPENJSON 表值函数」：值域与结果集不变，参数从 2,000 个降到 1 个。
        // ⚠ 2026-10-25：JSON+OPENJSON 这一版在 PV190 全量域上**仍会踩参数嗅探坏计划**，且更隐蔽：
        //   同一语句 sqlcmd 实测 64,478 逻辑读 / 0.5s，而 App 侧 `sys.dm_exec_requests` 抓到
        //   `status=running`、`wait_type=NULL`（＝真在算，不是等锁）、**逻辑读 8,121,691 页**、已跑 105s
        //   ——量级 126 倍。计划把 OPENJSON 集合放在嵌套循环**内侧**，2 万个值 × `[Order]` 全扫。
        //   60s 命令超时一到即 `执行超时已过期`，异常沿 LoadBatchByPiAsync → LoadPiPositionsAsync →
        //   LoadSupplyPoolAsync 冒到顶，被上层记成「Pegging 部分失败」继续排程 ⇒ 最终表现是
        //   `LPD=0 / FinalTask=0`，看起来像「排不出任务」，实际是装到一半炸了。
        //   ⇒ 本语句**不再把 PI 号送进服务端**：按同一个 `PlanVersionId` 谓词取本 PV 全集
        //   （PV190 实测 21,778 行 / 命中 11,610 行），再在内存里按请求的 PI 集收窄。SQL 侧谓词与原来逐字等价、
        //   结果集不变；参数只剩 1 个 int，嗅探无从谈起，也不再受 2,100 参数上限约束。
        var wanted = new HashSet<string>(piNos, StringComparer.Ordinal);
        var allRows = (await _connectionManager.QueryAsync<PiBatchRow>(
            @"SELECT DISTINCT o.MTS_InstructionNo, r.BatchNo
              FROM [Order] o
              INNER JOIN OrderBomRequestLink r ON r.OrderId = o.Id
              WHERE o.PlanVersionId = @PlanVersionId
                AND r.BatchNo IS NOT NULL",
            new { PlanVersionId = planVersionId },
            db: DatabaseId.APS, commandTimeout: 300)).ToList();

        var rows = allRows
            .Where(r => !string.IsNullOrEmpty(r.MTS_InstructionNo) && wanted.Contains(r.MTS_InstructionNo!))
            .ToList();
        _logger.LogInformation(
            "[Pegging][1a 批次桥接] 本 PV 全量 (PI,批次) 行={All} 行；请求 PI={Want} 个 ⇒ 命中 {Hit} 行（服务端不再收 PI 列表）",
            allRows.Count, wanted.Count, rows.Count);

        var ambiguous = 0;
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.MTS_InstructionNo)) continue;
            if (string.IsNullOrEmpty(row.BatchNo)) continue;

            if (batchByPi.TryGetValue(row.MTS_InstructionNo, out var existing))
            {
                if (!string.Equals(existing, row.BatchNo, StringComparison.Ordinal))
                {
                    ambiguous++;
                    if (string.CompareOrdinal(row.BatchNo, existing) >= 0) continue;
                }
                else
                {
                    continue;
                }
            }
            batchByPi[row.MTS_InstructionNo] = row.BatchNo;
        }

        if (ambiguous > 0)
        {
            _logger.LogWarning(
                "[Pegging] {Count} 个生产指示号关联了多个 BOM 批次，Stage 顺序装载取字典序最小批次以保证可复现（请核 PI 与批次的对应关系）",
                ambiguous);
        }

        return batchByPi;
    }

    /// <summary>生产指示号 → BOM 批次 行</summary>
    private sealed class PiBatchRow
    {
        public string? MTS_InstructionNo { get; set; }
        public string? BatchNo { get; set; }
    }

    /// <summary>
    /// ③ StartStageCode 填值（2026-09-11，5号位 O3 回复划归 2号位）：为每个新增生产需求回填
    /// 起点大工艺阶段码 = 该物料 Routing 有向图中「无入边源结点」工序的 StageCode（新生产从第一道工序起）。
    /// 无源结点 / 无 StageCode 时留空（与 1号位 PhaseTwo 未匹配兜底一致）。
    /// </summary>
    private static void FillStartStageCodes(
        PeggingResultVoucher voucher,
        List<RoutingOperation> operations,
        List<RoutingDependency> dependencies)
    {
        if (operations.Count == 0)
            return;

        // 无入边源结点 = OperationCode 不作任何 RoutingDependency.ToOperationCode 出现（该物料）
        var toNodesByMaterial = dependencies
            .GroupBy(d => d.MaterialId)
            .ToDictionary(g => g.Key, g => new HashSet<string>(g.Select(d => d.ToOperationCode), StringComparer.Ordinal));

        var firstStageByMaterial = operations
            .Where(o => !string.IsNullOrEmpty(o.StageCode))
            .GroupBy(o => o.MaterialId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    toNodesByMaterial.TryGetValue(g.Key, out var toNodes);
                    return g.Where(o => toNodes == null || !toNodes.Contains(o.OperationCode))
                            .OrderBy(o => o.OperationCode, StringComparer.Ordinal)
                            .Select(o => o.StageCode!)
                            .FirstOrDefault() ?? string.Empty;
                });

        foreach (var demand in voucher.LogicalProductionDemands)
        {
            if (string.IsNullOrEmpty(demand.StartStageCode) &&
                firstStageByMaterial.TryGetValue(demand.MaterialId, out var startStage))
            {
                demand.StartStageCode = startStage;
            }
        }
    }

    /// <summary>
    /// ③c 连续份额路径身份**诊断**（2026-10-22 起只诊断、不回填）：统计 <c>IsContinuation</c> 需求里多少片
    /// 已带真实 <c>RouteCode / PathId</c>、多少片仍缺。
    ///
    /// <para><b>为什么不回填</b>：1号位 <c>PhaseTwoInitialScheduler</c> 对 A/B 硬 Fail Closed ——
    /// <c>ContinuationKey / RouteCode / PathId / StartOperationCode / NoSplitMerge</c> 缺任一即记
    /// <c>UnscheduledDemandKeys</c> 且不产 Task。缺真路径的片因此不进 Task，这正是 0号位 2026-10-22 §五(一)
    /// 要求的终态：不得用 <c>DEFAULT / 1</c> 填假身份，也不得把缺真路径的 A/B 当普通 C 桶重新选路。
    /// 冻结 <c>11_2号位v2.1</c> 执行红线 Q1 同向：「<c>DEFAULT / 1</c> 不得作为归一化后的业务真值」。</para>
    ///
    /// <para><b>真值来源唯一</b>：5号位 按**工单**交付 <c>ExistingExecutionContextDto.RouteCode / .PathId</c>
    /// （`PathId` 唯一命中才交付、歧义或无匹配留 null，见该 DTO 的口径注释）。分桶逐工单切（§8 绝不合并多工单），
    /// 故工单级真值即片级真值 —— `CloneDemandSlice` 取用，我方不补默认值、不择一。</para>
    ///
    /// <para><b>2026-10-26 起恢复「带入」</b>：此前本报文写作「按 slice 交付」，但
    /// <c>ExistingExecutionSliceDto</c> 并无 <c>RouteCode / PathId</c> 字段（该契约从来只到工单级），
    /// 装载层又未从工单上下文取用 ⇒ A/B 片恒缺身份、100% 被 1号位 硬闸 Fail Closed。现改为在
    /// <c>BucketContinuityShares</c> 逐工单带入。**带入的是真值，不是 `DEFAULT / 1`** —— 后者才是
    /// 2026-10-22 §五(一) 禁止的对象。</para>
    ///
    /// <para><b>留痕</b>：按「缺 RouteCode」「缺 PathId」分列计数，并按「物料无路由节点」「载荷多条
    /// (RouteCode, PathId)」给样本，供 5号位 按 slice 定位。</para>
    /// </summary>
    private static void DiagnoseContinuationRouteIdentities(
        PeggingResultVoucher voucher,
        List<RoutingOperation> operations,
        ILogger? logger = null)
    {
        var contDemands = voucher.LogicalProductionDemands.Where(d => d.IsContinuation).ToList();
        if (contDemands.Count == 0)
            return;

        // 物料 → 本次载荷里的 (RouteCode, PathId) 集合（仅用于诊断分类）
        var routesByMaterial = operations
            .Where(o => !string.IsNullOrEmpty(o.RouteCode))
            .GroupBy(o => o.MaterialId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(o => (o.RouteCode, o.PathId)).Distinct().ToList());

        var alreadyReal = 0;        // 已带真实值（5号位 按 slice 交付）
        var missingRouteCode = 0;
        var missingPathId = 0;
        var noRouting = 0;
        var singleRoute = 0;
        var ambiguous = 0;
        var samples = new List<string>(4);

        foreach (var demand in contDemands)
        {
            var lacksRoute = string.IsNullOrEmpty(demand.RouteCode);
            var lacksPath = demand.PathId is null;
            if (!lacksRoute && !lacksPath)
            {
                alreadyReal++;
                continue;
            }

            if (lacksRoute) missingRouteCode++;
            if (lacksPath) missingPathId++;

            if (!routesByMaterial.TryGetValue(demand.MaterialId, out var routes) || routes.Count == 0)
            {
                noRouting++;
                if (samples.Count < 4) samples.Add($"mat={demand.MaterialId} 无路由节点");
                continue;
            }
            if (routes.Count > 1)
            {
                ambiguous++;
                if (samples.Count < 4)
                    samples.Add($"mat={demand.MaterialId} 多路径[{string.Join(",", routes.Select(r => $"{r.RouteCode}/{r.PathId}"))}]");
                continue;
            }
            singleRoute++;
        }

        var missing = contDemands.Count - alreadyReal;
        logger?.LogInformation(
            "[Pegging][Continuity] A/B 路径身份诊断（不回填）: 片数={Total} 已带真值={AlreadyReal} 缺身份={Missing}" +
            "（缺 RouteCode={MissingRoute}、缺 PathId={MissingPath}）；缺身份中物料无路由={NoRouting}、" +
            "载荷多条路径={Ambiguous}、载荷单条路径={SingleRoute}",
            contDemands.Count, alreadyReal, missing, missingRouteCode, missingPathId, noRouting, ambiguous, singleRoute);

        if (missing > 0)
            logger?.LogWarning(
                "[Pegging][Continuity] A/B 路径身份缺失 {Missing}/{Total} 片 ⇒ 1号位 按硬闸 Fail Closed 记 " +
                "Unscheduled 且不产 Task（预期结果，不补默认值）。样本: {Samples}",
                missing, contDemands.Count,
                samples.Count > 0 ? string.Join(" | ", samples) : "无");
    }

    /// <summary>物料 → UOM 装载行（Dapper 映射用）。</summary>
    private sealed class MaterialUomRow
    {
        public int Id { get; set; }
        public string UOM { get; set; } = string.Empty;
    }
    /// <summary>
    /// ④ 数量单位回填（2026-10-09，P1-08 方案a 取值源修正）：为每个新增生产需求回填**该需求自身物料**的
    /// <c>Material.UOM</c>。取值口径与理由见 <see cref="LogicalProductionDemand.UOM"/>。
    ///
    /// 为什么放在 Pegging 后的统一回填、而不是在 <c>BuildLogicalProductionDemand</c> 里直接传：
    ///   ① <c>BuildLogicalProductionDemand</c> 在 Pegging 热循环内逐条调用，那里没有「物料 → UOM」的**同步**来源；
    ///   ② 与 <c>StartStageCode</c> / <c>RequiredStageCode</c> 同款——「Pegging 后按需求物料集一次性解析」，
    ///      只按需求物料集查一轮（分块，`QueryChunkedInAsync`），不引入 N+1；
    ///   ③ 按**需求自身 materialId** 取值 ⇒ 结构上不可能把成品单位串到子件（与 PI 身份整改同源）。
    ///
    /// 取不到时**保持 null**（不猜、不回退根订单 UOM）——1号位按 <c>demand.UOM ?? string.Empty</c> 处理，
    /// 与旧行为一致，但此时会打出 Warning 便于核对物料主档是否缺行。
    /// </summary>
    private async System.Threading.Tasks.Task FillDemandUomAsync(
        PeggingResultVoucher voucher,
        CancellationToken cancellationToken)
    {
        var demands = voucher.LogicalProductionDemands;
        if (demands.Count == 0)
            return;

        var materialIds = demands.Select(d => d.MaterialId).Distinct().ToList();
        var rows = await QueryChunkedInAsync(materialIds, chunk =>
            _connectionManager.QueryAsync<MaterialUomRow>(
                "SELECT Id, UOM FROM Material WHERE Id IN @Ids",
                new { Ids = chunk },
                db: DatabaseId.APS));

        var uomByMaterial = new Dictionary<int, string>(rows.Count);
        foreach (var r in rows)
        {
            if (!string.IsNullOrWhiteSpace(r.UOM))
                uomByMaterial[r.Id] = r.UOM;
        }

        var filled = 0;
        var missing = 0;
        foreach (var demand in demands)
        {
            if (uomByMaterial.TryGetValue(demand.MaterialId, out var uom))
            {
                demand.UOM = uom;
                filled++;
            }
            else
            {
                missing++;
            }
        }

        if (missing > 0)
        {
            var missingSample = string.Join(",",
                demands.Where(d => !uomByMaterial.ContainsKey(d.MaterialId))
                       .Select(d => d.MaterialId).Distinct().Take(10));
            _logger.LogWarning(
                "[Pegging] 数量单位装载：{Missing}/{Total} 条需求取不到自身物料 UOM（Material 缺行或 UOM 为空）" +
                "⇒ 1号位 Task.UOM 将为空串；样例 MaterialId={Sample}",
                missing, demands.Count, missingSample);
        }
        else
        {
            _logger.LogDebug(
                "[Pegging] 数量单位装载：{Filled}/{Total} 条需求已回填自身物料 UOM（物料数={Materials}）",
                filled, demands.Count, materialIds.Count);
        }
    }

    /// <summary>
    /// DemandGoal 回填（2026-10-13 落码；0号位 裁决《DemandGoal与SH整单及关联问题正式裁决回执 v1.0》
    /// §一 DG-01~DG-05 ＋ §三.3 粒度＝**需求级**）。
    ///
    /// 【为什么在 Pegging 之后填】判据之一是「跨厂 SH 是否被真实上层需求承接」，该事实只存在于
    ///   Pegging 内存结果（<see cref="PeggingResultVoucher.SupplyAllocations"/>），**实库无载体**
    ///   （<c>PeggingSupplyAllocation</c> 0 行）⇒ 必须等 Pegging 跑完才能判。
    ///
    /// 【为什么按「需求血缘根」一刀切即可】每条 <see cref="LogicalProductionDemand"/> 的
    ///   <c>OrderId</c> 携带的都是**根订单** Id（BOM 递归里两个 <c>BuildLogicalProductionDemand</c>
    ///   调用点均传 <c>orderId: order.OrderId</c>，且 <c>ChildDemandKey</c> 编码 <c>order.OrderId</c>）
    ///   ⇒ 子件需求自动继承根件身份，**不需要**遍历 BOM 树或读 <c>MaterialRequirementLinks</c>。
    ///
    /// 【判据】**不得**按 <c>OrderType</c> 硬映射（DG-02/DG-04）：PV2 实测 533 行
    ///   <c>OrderType='PRODUCTION_INSTRUCTION'</c> 却是 O 行。干净判据 = <c>OrderNo</c> 前缀 ＋ <c>CustomerSegment</c>。
    ///
    /// 【未落地的一项：跨厂 SH（SH-02）】判据要求区分「被任意真实上层需求承接」与「孤儿 SH」，
    ///   但**唯一可得信号区分不出二者**（实测见 <see cref="ResolveDemandGoal"/> 的 SH 分支注释：
    ///   PV2 四个 SH 订单 <c>Allocation=4</c> 而需求=0，即 SH 供给在目标厂自扣自己那张订单）⇒
    ///   按 DG-05 §2.4 留空 + 诊断，**不猜**。另注：PV2 实测 SH 订单**产出 0 条需求**
    ///   （自扣净额为 0）⇒ 本分支在当期数据上无实例，不是「已验证通过」。
    ///
    /// 【留空条件】订单主档缺行 / 订单号与客户段皆不可用 ⇒ **留 null + Warning**（§2.4：不猜、不一律当补库）；
    ///   1号位 侧按既有 <c>DEMAND_GOAL_ABSENT</c> 登记缺口，行为与取值链投产前一致。
    ///
    /// 【调用位置】`BuildSolveRequestAsync` 内、`ApplyContinuityBucketing` **之前**——
    ///   `CloneDemandSlice`／`CloneDemandRescaled` 均原样继承 <c>DemandGoal</c>，先填则两片自动带值。
    /// </summary>
    private async System.Threading.Tasks.Task FillDemandGoalsAsync(
        PeggingResultVoucher voucher,
        CancellationToken cancellationToken)
    {
        var demands = voucher.LogicalProductionDemands;
        if (demands.Count == 0)
            return;

        // 根订单 Id 集（需求携带的 OrderId 即根订单 Id；去重后一次分块查，无 N+1）。
        var rootOrderIds = demands
            .Where(d => d.OrderId.HasValue)
            .Select(d => d.OrderId!.Value)
            .Distinct()
            .ToList();

        var classByOrderId = new Dictionary<long, OrderGoalClassRow>(rootOrderIds.Count);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (rootOrderIds.Count > 0)
        {
            // 2026-10-26：改单 JSON 参数 + `INNER HASH JOIN OPENJSON`（同 1d-5 一族）。
            //   原 `QueryChunkedInAsync` 每块 2,000 值 ⇒ 11 块；只读实测单趟 22,128 值 **47ms**、
            //   单块 2,000 **15ms**（≈11×15=165ms）⇒ 两种形态都远小于本段实测 **7,348ms**，
            //   **SQL 不是本段的大头**（真凶在下面的逐需求循环，用细分日志定位）。
            var rows = await QueryJsonInAsync(rootOrderIds, json =>
                _connectionManager.QueryAsync<OrderGoalClassRow>(
                    @"SELECT o.Id AS OrderId, o.OrderNo, o.CustomerSegment
                      FROM [Order] o
                      INNER HASH JOIN OPENJSON(@Json) j ON j.value = o.Id",
                    new { Json = json },
                    db: DatabaseId.APS),
                chunkSize: JsonInSinglePassChunkSize,
                tag: $"1 DemandGoal 根订单(单={rootOrderIds.Count})");

            foreach (var r in rows)
                classByOrderId[r.OrderId] = r;
        }

        var sqlMs = sw.ElapsedMilliseconds;
        sw.Restart();

        // ── 跨厂 SH（SH-02 整单判）：**当前留空**，理由见 ResolveDemandGoal 的 SH 分支注释 ──
        //   不在此构造「被承接」索引：唯一可得信号（`ShippingInstructionNo` 键的分配行）**区分不出**
        //   「SH 供给扣掉它自己那张订单的目标侧需求（自扣）」与「被真实上层需求 Pegging 承接」。
        //   ⇒ 按 DG-05 §2.4「不猜、不一律当补库」留空 + 显式诊断，交 0号位 点名的 2→1 联调定载体。

        var customerCommitment = 0;
        var inventoryReplenishment = 0;
        var unresolved = 0;
        var unresolvedSample = new List<string>(5);

        foreach (var demand in demands)
        {
            var goal = ResolveDemandGoal(demand, classByOrderId, out var reason);
            demand.DemandGoal = goal;

            if (goal is null)
            {
                unresolved++;
                if (unresolvedSample.Count < 5)
                    unresolvedSample.Add($"OrderId={demand.OrderId?.ToString() ?? "null"}/{reason}");
            }
            else if (goal == DemandGoalValues.CustomerCommitment)
                customerCommitment++;
            else
                inventoryReplenishment++;
        }

        _logger.LogInformation(
            "[Pegging] DemandGoal 回填：需求切片 {Total} 条 ⇒ 客户承诺={CustomerCommitment}、库存补充={InventoryReplenishment}、留空={Unresolved}" +
            "（细分：SQL={SqlMs}ms / 逐需求={MemMs}ms）",
            demands.Count, customerCommitment, inventoryReplenishment, unresolved,
            sqlMs, sw.ElapsedMilliseconds);

        if (unresolved > 0)
        {
            _logger.LogWarning(
                "[Pegging] DemandGoal 留空 {Unresolved}/{Total} 条需求 ⇒ 1号位 将按 DEMAND_GOAL_ABSENT 登记缺口" +
                "（不猜值、不自动归补库）。逐条原因见样例：{Sample}",
                unresolved, demands.Count, string.Join("; ", unresolvedSample));
        }
    }

    /// <summary>
    /// 数量权威回填（<c>QuantityAuthority</c>，1号位 2026-10-22 回执 §四点名落地）。
    ///
    /// 【要解决的问题】「有 PI 号」≠「PI 是数量权威」。客户订单行同样携带 <c>MTS_InstructionNo</c>
    ///   （ODS 视图 <c>v_APS_SalesOrder</c> 分支2 的刻意设计），但它的数量边界取 <c>[Order].Quantity</c>；
    ///   若 1号位 按「有 PI 号」一律进 <c>ApplyPiLegalQuantityGate</c>，这类需求会被按 PI 合法量误判
    ///   ⇒ 硬失败与 MISSING 中混入「对象选错」的部分。
    ///
    /// 【判据】需求带 PI 号，且**该 PI 号**在本 Run 的**权威 PI 事实**集中出现过（不要求物料 / 工厂同一）——
    ///   事实集＝<c>LoadPiRemainingFactsAsync</c> 产出的 <see cref="ContinuityFacts.PiRemainingFacts"/>，
    ///   与 1号位 门禁消费的 <c>DomainSolveRequest.PiRemainingFacts</c> **同一份实例**。
    ///   该事实集按 B-01 身份闸门剔除 <c>OrderType='SALES_ORDER'</c> 行 ⇒ 「命中事实集」即
    ///   「PI 是本需求的数量权威」，不需要再按 <c>OrderType</c> 猜一遍（我方需求侧本就不判该列）。
    ///   只比 PI 号、**不比物料/工厂**是刻意的（见 <c>ClassifyQuantityAuthority</c> 内注释）。
    ///
    /// 【取值】带 PI 号且命中事实集 ⇒ <c>MTS_PI</c>；带 PI 号但本 Run 无事实行 ⇒ <c>ORDER_ONLY</c>；
    ///   无 PI 号 ⇒ <c>NONE</c>。
    ///
    /// 【已知边界（已随我方 2026-10-22 致 1号位 件 §2.3 告知）】<c>ORDER_ONLY</c> 判的是
    ///   「**本 Run 无权威事实行**」，不是「ERP 里没有这个 PI」——某 PI 若因快照/截止过滤本轮未载入，
    ///   该需求会落 <c>ORDER_ONLY</c> 并跳过 PI 门禁。要区分「真的没有」与「本轮没取到」需再扩一位契约，
    ///   须 1号位 先点名字段，我方不自行加。
    ///
    /// 【调用位置】<c>BuildSolveRequestAsync</c> 内、<c>ApplyContinuityBucketing</c> **之前**——
    ///   <c>CloneDemandSlice</c>／<c>CloneDemandRescaled</c> 均逐项继承该字段，先填则两片自动带值。
    /// </summary>
    private void FillQuantityAuthority(PeggingResultVoucher voucher, ContinuityFacts continuityFacts)
    {
        var demands = voucher.LogicalProductionDemands;
        if (demands.Count == 0)
            return;

        var (mtsPi, orderOnly, none, piFactCount) =
            ClassifyQuantityAuthority(demands, continuityFacts.PiRemainingFacts);

        _logger.LogInformation(
            "[Pegging] QuantityAuthority 回填：需求切片 {Total} 条 ⇒ MTS_PI={MtsPi}、ORDER_ONLY={OrderOnly}、NONE={None}" +
            "（本 Run 权威 PI 事实 {PiFacts} 张；ORDER_ONLY 含「挂 PI 号但不在 B-01 事实集内」与「事实本轮未载入」两类）",
            demands.Count, mtsPi, orderOnly, none, piFactCount);
    }

    /// <summary>
    /// 数量权威分类（纯函数，<c>internal</c> 供 <c>LPS.APS.Tests</c> 直接断言；行为口径见
    /// <see cref="FillQuantityAuthority"/> 类注释）。就地写入 <c>demand.QuantityAuthority</c> 并返回各档计数。
    /// </summary>
    internal static (int MtsPi, int OrderOnly, int None, int PiFactCount) ClassifyQuantityAuthority(
        IReadOnlyList<LogicalProductionDemand> demands,
        IReadOnlyList<PiRemainingFact> piFacts)
    {
        // 权威 PI 号集合（一个 PI 一条事实）。
        // ⚠ **刻意比 1号位 门禁更宽**：这里只比 PI 号是否在事实集中**出现过**，不要求物料 / 工厂同一；
        //   而门禁 `EvaluatePiLegalQuantityGate` 按 `(PI, MaterialId, FactoryId)` 三元素匹配。两者**不是同一判据**：
        //   PI 在事实集、但物料或工厂不匹配 ⇒ 此处给 `MTS_PI` ⇒ 门禁照跑 ⇒ `matched == 0` ⇒ **显式未满足**
        //   （冻结 V1_4 NEW-03：异厂同号 PI 不作本厂来源）。若把这里改成三元匹配，这类需求会被当成
        //   「无 PI 权威」落 `ORDER_ONLY` 而**跳过门禁、静默排下去** —— 从显式失败变成降低目标。
        //   1号位 2026-10-22 回执 §3.2 点名要求保留本注释，防止后来者「顺手对齐」。
        var authoritativePis = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in piFacts)
        {
            if (!string.IsNullOrEmpty(fact.ProductionInstructionNo))
                authoritativePis.Add(fact.ProductionInstructionNo);
        }

        var mtsPi = 0;
        var orderOnly = 0;
        var none = 0;

        foreach (var demand in demands)
        {
            var pi = demand.ProductionInstructionNo;
            if (string.IsNullOrWhiteSpace(pi))
            {
                demand.QuantityAuthority = QuantityAuthorityValues.None;
                none++;
            }
            else if (authoritativePis.Contains(pi!))
            {
                demand.QuantityAuthority = QuantityAuthorityValues.MtsPi;
                mtsPi++;
            }
            else
            {
                demand.QuantityAuthority = QuantityAuthorityValues.OrderOnly;
                orderOnly++;
            }
        }

        return (mtsPi, orderOnly, none, authoritativePis.Count);
    }

    /// <summary>
    /// 单条需求的 Goal 判据（纯函数）。判据见 <see cref="FillDemandGoalsAsync"/> 类注释。
    /// 返回 null = 血缘无法唯一确定（留空 + 诊断），<paramref name="reason"/> 给出留空原因（进日志）。
    /// </summary>
    private static string? ResolveDemandGoal(
        LogicalProductionDemand demand,
        IReadOnlyDictionary<long, OrderGoalClassRow> classByOrderId,
        out string reason)
    {
        if (!demand.OrderId.HasValue)
        {
            reason = "无根订单Id";
            return null;
        }

        if (!classByOrderId.TryGetValue(demand.OrderId.Value, out var cls)
            || string.IsNullOrWhiteSpace(cls.OrderNo))
        {
            // 判例 8：订单主档缺行 / 无订单号 ⇒ 血缘无法唯一确定，留空不猜（DG-05 §2.4）。
            reason = "订单主档缺行/无订单号";
            return null;
        }

        // ── 跨厂 SH（SH-02「按整单判」）：**当前留空** ──
        //   判据要求区分「被任意真实上层需求 Pegging 承接」与「彻底无上层承接的孤儿 SH」。
        //   唯一可得信号是 `voucher.SupplyAllocations` 里 `ShippingInstructionNo = SH号` 的分配行，
        //   但**实测该信号区分不出这两种情形**（PV2 四个 SH 订单：`Allocation=4` 而需求=0 ——
        //   SH 供给在目标厂把**它自己那张订单**的需求扣光了，即自扣；自扣与真实上层承接都表现为
        //   「有一条该 SH 号的分配行」）。⇒ 用它会**恒判 CUSTOMER_COMMITMENT**（含真孤儿），
        //   违反 SH-02；而「无分配行 ⇒ 孤儿」又会漏掉所有自扣过的 SH。
        //   ⇒ 按 DG-05 §2.4「事实确实不足…不猜值、不一律当补库」**留空 + 显式诊断**，
        //     待 0号位 §末点名的「2→1 三态/血缘联调」定出可区分的载体后再通电。
        if (string.Equals(cls.CustomerSegment, CustomerSegmentCodes.InterFactory, StringComparison.Ordinal))
        {
            reason = "跨厂SH：被承接信号不可区分（自扣 vs 真实上层承接），按 DG-05 §2.4 留空";
            return null;
        }

        // P 行（PI 自身指令行）= MTS 补库需求；被上层消费**不改写**其自身需求身份（DG-03 §一.1）。
        if (cls.OrderNo[0] == 'P' || cls.OrderNo[0] == 'p')
        {
            reason = string.Empty;
            return DemandGoalValues.InventoryReplenishment;
        }

        // O 行（客户订单行）= 客户履约需求。
        reason = string.Empty;
        return DemandGoalValues.CustomerCommitment;
    }

    /// <summary>DemandGoal 判据所需的订单身份最小列（仅 OrderId / OrderNo / CustomerSegment，不带订单其它字段）。</summary>
    private sealed class OrderGoalClassRow
    {
        public long    OrderId         { get; set; }
        public string? OrderNo         { get; set; }
        public string? CustomerSegment { get; set; }
    }

    /// <summary>
    /// 按当前 Domain 涉及的 MaterialId 裁剪 MaterialStageDeptContext（IsCurrent=1），
    /// 组装为 MaterialStageDepartmentContextDto 传入 1号位。只传 (MaterialId, StageCode, ProductionDepartmentId)，
    /// 不带 SourceType / SourceDetail / ValidFrom 等治理字段（1号位不需要）。
    ///
    /// 【同键多行的两种处置（R04 Y-09「同一物料/Stage 唯一生产部门」）】
    ///   · 同键**不同部门** ⇒ Fail-Closed 整体剔除（J15-04），命中 Task 走「部门事实缺失」隔离通道；
    ///   · 同键**同部门** ⇒ 值无歧义，按单值合并、**不剔除**，只 WARNING 留痕「源表有重复行」。
    ///   实测（2026-10-25）`IsCurrent=1` **855,606 行／855,606 个不同键**，两种情形均 **0**，本分支属防御性收口。
    /// </summary>
    private async Task<List<MaterialStageDepartmentContextDto>>
        LoadMaterialStageDeptContextAsync(IReadOnlyList<int> materialIds, CancellationToken ct)
    {
        if (materialIds.Count == 0)
            return new List<MaterialStageDepartmentContextDto>();

        var rows = await QueryJsonInAsync(materialIds, json =>
            _connectionManager.QueryAsync<MaterialStageDeptContextLoadRow>(
                @"SELECT MaterialId, StageCode, DefaultProductionDepartmentId
                  FROM MaterialStageDeptContext mc
                  INNER HASH JOIN OPENJSON(@Json) j ON j.value = mc.MaterialId
                  WHERE mc.IsCurrent = 1",
                new { Json = json },
                db: DatabaseId.APS),
            chunkSize: JsonInSinglePassChunkSize,
            tag: $"1d-5c 部门上下文(物料={materialIds.Count})");

        var dedupe = DedupeDepartmentContexts(
            rows.Select(r => (r.MaterialId, r.StageCode, r.DefaultProductionDepartmentId)));

        if (dedupe.ConflictedKeys.Count > 0)
        {
            _logger.LogError(
                "[Pegging] MaterialStageDeptContext(IsCurrent=1) 同一 (MaterialId, StageCode) 存在多个不同部门：{Count} 组，" +
                "已按 J15-04 **Fail-Closed** 整体剔除（不取 MIN、不猜）；命中这些键的 Task 将按「部门事实缺失」逐 Task 隔离。" +
                "源数据冲突应回 5号位 sp_RebuildMaterialStageDeptContext 侧核查（示例：{Examples}）",
                dedupe.ConflictedKeys.Count,
                string.Join(", ", dedupe.ConflictedKeys.Take(5).Select(k => $"({k.MaterialId},{k.StageCode})")));
        }

        if (dedupe.SameDepartmentDuplicateKeys.Count > 0)
        {
            _logger.LogWarning(
                "[Pegging] MaterialStageDeptContext(IsCurrent=1) 同一 (MaterialId, StageCode) 出现多行、且部门一致：" +
                "{Keys} 组 / 多出 {ExtraRows} 行（本域装载 {Rows} 行）。值无歧义已按单值合并（不剔除、不影响部门判定）；" +
                "源表重复行应回 5号位 sp_RebuildMaterialStageDeptContext 侧核查（示例：{Examples}）",
                dedupe.SameDepartmentDuplicateKeys.Count, dedupe.SameDepartmentExtraRowCount, rows.Count,
                string.Join(", ", dedupe.SameDepartmentDuplicateKeys.Take(5)
                    .Select(k => $"({k.MaterialId},{k.StageCode})")));
        }

        return dedupe.Contexts;
    }

    /// <summary>部门上下文同键去重的结果（R04 Y-09）。</summary>
    /// <param name="Contexts">交给 1号位 的 (MaterialId, StageCode) → 部门 映射（已剔除冲突键）。</param>
    /// <param name="ConflictedKeys">同键出现**不同部门**、被 Fail-Closed 剔除的键。</param>
    /// <param name="SameDepartmentDuplicateKeys">同键多行但**部门一致**、已合并保留的键（源表重复行留痕）。</param>
    /// <param name="SameDepartmentExtraRowCount">后者多出来的**行数**（不是键数）。</param>
    internal sealed record DepartmentContextDedupeResult(
        List<MaterialStageDepartmentContextDto> Contexts,
        IReadOnlyList<(int MaterialId, string StageCode)> ConflictedKeys,
        IReadOnlyList<(int MaterialId, string StageCode)> SameDepartmentDuplicateKeys,
        int SameDepartmentExtraRowCount);

    /// <summary>
    /// 部门上下文同键去重（R04 Y-09「同一物料/Stage 唯一生产部门」；纯函数，便于单测）。
    ///
    /// 【两类重复分开处置】
    ///   · 同键**不同部门** ⇒ 有歧义、**Fail-Closed**：该键整体剔除（J15-04，不取 MIN、不猜），
    ///     命中该键的 Task 走「部门事实缺失」的逐 Task 隔离通道（`PartitionByDepartment`）。
    ///   · 同键**同部门** ⇒ 值唯一、合并不剔除 —— 一旦剔除，这些物料会**凭空**变成「部门事实缺失」；
    ///     只回报「多出几行」，把源表重复行这件事数出来。
    ///
    /// 【为什么不能用 `ToDictionary` / `GroupBy().First()`】两者都**静默**吞掉重复：
    ///   前者抛异常、后者悄悄选一条，两种都答不出「源表到底有没有重复」。
    /// </summary>
    internal static DepartmentContextDedupeResult DedupeDepartmentContexts(
        IEnumerable<(int MaterialId, string StageCode, int DepartmentId)> rows)
    {
        var byKey = new Dictionary<(int MaterialId, string StageCode), int>();
        var conflicted = new HashSet<(int MaterialId, string StageCode)>();
        var extraRowsByKey = new Dictionary<(int MaterialId, string StageCode), int>();

        foreach (var (materialId, stageCode, departmentId) in rows)
        {
            var key = (materialId, stageCode ?? string.Empty);
            if (!byKey.TryGetValue(key, out var existing))
            {
                byKey[key] = departmentId;
                continue;
            }

            extraRowsByKey[key] = extraRowsByKey.TryGetValue(key, out var seen) ? seen + 1 : 1;

            if (existing != departmentId)
                conflicted.Add(key);
        }

        foreach (var key in conflicted)
            byKey.Remove(key);

        var sameDepartmentKeys = extraRowsByKey.Keys
            .Where(k => !conflicted.Contains(k))
            .ToList();

        return new DepartmentContextDedupeResult(
            byKey.Select(kv => new MaterialStageDepartmentContextDto
            {
                MaterialId = kv.Key.MaterialId,
                StageCode = kv.Key.StageCode,
                ProductionDepartmentId = kv.Value
            }).ToList(),
            conflicted.ToList(),
            sameDepartmentKeys,
            sameDepartmentKeys.Sum(k => extraRowsByKey[k]));
    }

    /// <summary>
    /// 装载并投影**工厂 / 生产部门级生效 WORK 窗口**（G-2，1号位 2026-10-25 核证回执 §五 点名）。
    ///
    /// 【与 `CalendarSlots` 的分工】
    ///   · `CalendarSlots`（资源级）= 设备/人工槽**可排性**，已由 `SchedulingOrchestrator` 三级合成；
    ///   · 本方法产出的 `DepartmentWorkWindows` = 工厂/部门**工时口径**，只给「无 Routing 的 Stage」推进时间用。
    ///
    /// 【为什么不把原始行交出去】RC-001 逐字「**2号位 合成合法能力窗口、1号位 只消费**」。合成 = 有事实的层参与
    ///   ＋ WORK 取交集 ＋ OFF 作例外裁剪（含双向授权）；两层都有事实时「部门有事实就用部门」会漏掉工厂级 OFF
    ///   ⇒ 违反 RC-006「明确关闭不可由上级复活」。故只交**合成后的结果**。
    ///
    /// 【裁剪口径】只投影本域 `MaterialStageDepartmentContexts` 里出现的部门（及其所属工厂），
    ///   不按全库 287 个部门铺；计划期与 `PlanningStart/End` 同源同值。
    ///
    /// 【失败面（2026-10-25 按 1号位 G-2 回执 §五 分两类处置）】
    ///   · **投影缺陷 ⇒ 阻断 Run（Fail Closed）**：计划期非法、投影截断。这两种载荷不可用，若照跑，
    ///     消费侧会把「我方投影失败」读成「5号位 日历缺口」——缺陷记错了人。
    ///     依据：我方上一轮会签自写「整表为空属我方投影缺陷，应报错并计数，不能当成『全程不推进』静默通过」，
    ///     而原实现只 `LogError` 后继续跑 ⇒ 该承诺未闭合（1号位 G-2 §五）。
    ///   · **数据侧缺陷 ⇒ 计数随载荷给出、不阻断**：部门主档缺 `FactoryId`、同层矛盾整日拒绝。
    ///     这两种按 RC-007 落到「该 scope 该日无工时」即可；阻断会让一条部门主档缺行停掉整厂排程。
    ///     计数随载荷发出（`DomainSolveRequest.DepartmentWorkWindowConflictDayCount` / `DepartmentsWithoutFactoryCount`）。
    ///   无部门上下文时返回空表（属正常，不是缺陷）。源表行数、部门覆盖、自身有事实的部门数一并留痕，
    ///   便于 5号位 铺数后核对「继承有没有真的生效」。
    /// </summary>
    private async Task<DepartmentWorkWindowProjector.Result> LoadDepartmentWorkWindowsAsync(
        IReadOnlyList<MaterialStageDepartmentContextDto> departmentContexts,
        IReadOnlyCollection<int> usedFactoryIds,
        DateTime planningStart,
        DateTime planningEnd)
    {
        // 两个来源都空 = 本域既没有部门归属、也没有需求工厂 ⇒ 空表属正常，不是缺陷。
        if (departmentContexts.Count == 0 && usedFactoryIds.Count == 0)
        {
            _logger.LogInformation(
                "[Pegging] G-2 WORK 窗口投影跳过：本域无部门上下文、也无需求工厂（DepartmentWorkWindows 传空表）");
            return new DepartmentWorkWindowProjector.Result();
        }

        var usedDepartmentIds = departmentContexts
            .Select(c => c.ProductionDepartmentId)
            .Distinct()
            .ToList();

        // 全表装载（实测 2026-10-25 = 69 行，`IsActive=1`；与 `SchedulingOrchestrator` 同一 SQL 同口径）。
        // 有效期（EffectiveFrom/To）不在这里过滤，由解析器按日判定 —— 否则「今天之后生效」的行会被整条丢掉。
        var rows = (await _connectionManager.QueryAsync<ProductionWorkCalendarSlot>(
            @"SELECT Id, ScopeType, FactoryId, ProductionDepartmentId, WorkDate, WeekdayIso,
                     StartAt, EndAt, WorkState, AllowLowerOverride, IsAuthorizedOverride,
                     SourceSystem, SourceReference, EffectiveFrom, EffectiveTo, IsActive
                FROM ProductionWorkCalendarSlot
               WHERE IsActive = 1",
            db: DatabaseId.APS)).ToList();

        // 部门 → 工厂：部门级日历行本身也带 FactoryId，但主档才是权威归属（与 `SchedulingOrchestrator:1072` 同源）。
        var factoryIdByDepartmentId = (await _connectionManager.QueryAsync<DepartmentFactoryRow>(
                @"SELECT Id, FactoryId FROM ProductionDepartment",
                db: DatabaseId.APS))
            .ToDictionary(x => x.Id, x => x.FactoryId);

        var projection = DepartmentWorkWindowProjector.Project(
            rows, factoryIdByDepartmentId, usedDepartmentIds, usedFactoryIds, planningStart, planningEnd);

        if (projection.HorizonInvalid)
        {
            // Fail Closed（1号位 2026-10-25 G-2 回执 §五 甲）：计划期非法是**输入契约缺陷**，
            //   不该进 Solver 跑一遍再报「无工时」—— 那会把 2号位 的投影缺陷记成 5号位 的日历缺口。
            _logger.LogError(
                "[Pegging] G-2 WORK 窗口投影：计划期非法（PlanningStart={Start:yyyy-MM-dd HH:mm}，PlanningEnd={End:yyyy-MM-dd HH:mm}）⇒ Fail Closed，阻断本域 Run",
                planningStart, planningEnd);
            throw new InvalidOperationException(
                $"G-2 工厂/部门 WORK 窗口投影失败：计划期非法（PlanningStart={planningStart:yyyy-MM-dd HH:mm}，"
                + $"PlanningEnd={planningEnd:yyyy-MM-dd HH:mm}）。DepartmentWorkWindows 无法产出 ⇒ 按 Fail Closed 阻断本域"
                + "（不得把投影缺陷当成『无工时』交给求解器）。");
        }

        _logger.LogInformation(
            "[Pegging] G-2 工厂/部门 WORK 窗口投影：{Windows} 片；厂级 scope {Factories} 个 / 部门 scope {Departments} 个（自身有日历事实 {WithFacts} 个，其余由工厂继承）；" +
            "计划期 {Days} 日（{Start:yyyy-MM-dd}–{End:yyyy-MM-dd}，已含软边界 +180 天，与资源日历同口径）；源表有效行 {Rows} 行，本域部门 {UsedDepts} 个",
            projection.Windows.Count, projection.FactoryScopeCount, projection.DepartmentScopeCount,
            projection.DepartmentWithOwnFactsCount, projection.DaysProjected,
            planningStart, planningEnd.Date, rows.Count, usedDepartmentIds.Count);

        if (projection.DepartmentsWithoutFactoryCount > 0)
        {
            _logger.LogWarning(
                "[Pegging] G-2 投影：{Count} 个生产部门在 `ProductionDepartment` 主档查不到 FactoryId ⇒ 这些部门无窗口（不猜厂）",
                projection.DepartmentsWithoutFactoryCount);
        }

        if (projection.ConflictDayCount > 0)
        {
            _logger.LogWarning(
                "[Pegging] G-2 投影：{Count} 个「scope × 日」因同层日历矛盾（WORK↔WORK / OFF↔OFF 部分重叠、WORK 无窗口）被整日拒绝 ⇒ 请回 5号位 核查日历源数据",
                projection.ConflictDayCount);
        }

        if (projection.Truncated)
        {
            // Fail Closed（与 G-1 截断同一处置）：截断后的载荷是「前半段有窗口、后半段没有」，
            //   消费侧会把缺的那半读成「无工时」⇒ 一批看起来有理由的未排，责任被推给 Solver。
            //   部分载荷比没有载荷更危险（1号位 2026-10-25 G-1 回执 §四 甲、G-2 回执 §五）。
            _logger.LogError(
                "[Pegging] G-2 投影命中窗口上限 {Max} 被截断 ⇒ Fail Closed，阻断本域 Run（载荷不完整不可交付）",
                DepartmentWorkWindowProjector.MaxWindowCountForDiagnostics);
            throw new InvalidOperationException(
                $"G-2 工厂/部门 WORK 窗口投影命中安全上限 {DepartmentWorkWindowProjector.MaxWindowCountForDiagnostics} 行被截断："
                + $"本域部门 {usedDepartmentIds.Count} 个、源表 {rows.Count} 行、计划期 {projection.DaysProjected} 日。"
                + "截断后的载荷会让后半段日历被读成『无工时』⇒ 按 Fail Closed 阻断本域，须核查计划期长度与部门范围。");
        }

        if (projection.Windows.Count == 0)
        {
            _logger.LogWarning(
                "[Pegging] G-2 投影：本域有 {UsedDepts} 个部门归属、源表 {Rows} 行，却一片窗口都没产出 ⇒ " +
                "1号位 所有无 Routing Stage 将无工时可用（该日不推进）。请核查日历有效期与部门/工厂归属",
                usedDepartmentIds.Count, rows.Count);
        }

        return projection;
    }

    /// <summary>`Material` 主档最小列：物料 Id → 物料编码（良率规则按**编码**匹配，`PlanningYieldRule.MaterialId` 是编码语义）。</summary>
    private sealed class MaterialCodeRow
    {
        public int Id { get; set; }
        public string MaterialCode { get; set; } = string.Empty;
    }

    /// <summary>
    /// 投影**逐 Stage 计划加工量**（G-1，1号位 2026-10-25 核证回执 §五 点名）。
    ///
    /// 【口径】见 <see cref="DemandStageProcessQuantityProjector"/>：终点 Stage 净产出 = 需求净量，
    ///   逐段由后向前按**该段良率**反算投入，上一段净产出 = 本段投入。
    ///   良率三级 = `(物料编码, StageCode)` 精确 → 物料级默认 → 100
    ///   （<see cref="PlanningYieldResolver.Resolve"/>，3号位 2026-10-23 回执第 2 项）。
    ///
    /// 【与既有 `PlannedProcessQty` 的关系】**只增不改**：既有需求级反算保持物料级 first-match 原口径，
    ///   本表是 1号位 逐 Stage 取量的新载体；本表为空 ⇒ 消费侧沿用需求级值（零回归）。
    ///
    /// 【诊断】无链 / 范围解析不出 / 净量 ≤ 0 / 多 Stage 分布 / 最大放大倍率 全部落日志，
    ///   「良率连乘把首段投入放大了多少」必须看得见 —— 这是本轮唯一会改变产能负载的量。
    /// </summary>
    private async Task<List<DemandStageProcessQuantity>> BuildStageProcessQuantitiesAsync(
        IReadOnlyList<LogicalProductionDemand> demands,
        IReadOnlyList<StageSequenceChain> chains,
        FrozenStrategySnapshot frozenSnapshot,
        CancellationToken cancellationToken)
    {
        if (demands.Count == 0 || chains.Count == 0)
        {
            _logger.LogInformation(
                "[Pegging] G-1 逐 Stage 加工量跳过：需求 {Demands} 条、Stage 链 {Chains} 条（StageProcessQuantities 传空表 ⇒ 1号位 沿用需求级 PlannedProcessQty）",
                demands.Count, chains.Count);
            return new List<DemandStageProcessQuantity>();
        }

        // 良率规则以物料**编码**为键，需求只有 MaterialId ⇒ 按本域需求物料集回填编码（与 `FillDemandUomAsync` 同款分块）。
        var materialIds = demands.Select(d => d.MaterialId).Distinct().ToList();
        var codeRows = await QueryChunkedInAsync(materialIds, chunk =>
            _connectionManager.QueryAsync<MaterialCodeRow>(
                "SELECT Id, MaterialCode FROM Material WHERE Id IN @Ids",
                new { Ids = chunk },
                db: DatabaseId.APS));

        var codeByMaterialId = new Dictionary<int, string>(codeRows.Count);
        foreach (var row in codeRows)
        {
            if (!string.IsNullOrWhiteSpace(row.MaterialCode))
                codeByMaterialId[row.Id] = row.MaterialCode;
        }

        var yieldRules = frozenSnapshot?.Procurement?.PlanningYields;

        // 良率取「值 ＋ 来源等级」：R04 Y-02／Y-03 要求把「回退」与「缺配」显性化，
        //   否则首段投入被放大时看不出是业务规则还是兜底 100。
        var projection = DemandStageProcessQuantityProjector.Project(
            demands,
            chains,
            (materialId, stageCode) =>
                codeByMaterialId.TryGetValue(materialId, out var code)
                    ? PlanningYieldResolver.ResolveWithSource(yieldRules, code, stageCode)
                    : new YieldResolution(PlanningYieldResolver.NoLoss, YieldMatchLevel.NO_CONFIG));

        _logger.LogInformation(
            "[Pegging] G-1 逐 Stage 加工量投影：出 {Rows} 行（真实应有 {TotalRows} 行 / 安全上限 {Max}）；" +
            "有链需求 {WithChain} 条 / 无链 {NoChain} 条 / " +
            "范围解析不出 {Unresolved} 条 / 净量≤0 {NonPositive} 条；其中多 Stage 需求 {MultiStage} 条，" +
            "良率连乘后首段投入最大放大 {MaxRatio}×（相对净量）",
            projection.Rows.Count, projection.TotalRowCount,
            DemandStageProcessQuantityProjector.MaxRowCount,
            projection.DemandsWithChain, projection.DemandsWithoutChain,
            projection.DemandsUnresolvedRange, projection.DemandsNonPositiveQty,
            projection.MultiStageDemandCount, Math.Round(projection.MaxInflationRatio, 4));

        // 良率来源分布（R04 Y-02／Y-03）：四类合计 = Rows.Count，逐 Stage 行计。
        //   `{RuleCount}` = 冻结快照里的良率规则条数：用来区分两种「全 NO_CONFIG」——
        //   规则集为空（＝本期没发布良率，Info）／规则非空却一行没命中（＝键对不上，下方升 Warning）。
        var yieldRuleCount = yieldRules?.Count ?? 0;
        _logger.LogInformation(
            "[Pegging] G-1 投影良率来源：逐 Stage 行 {Rows} 行 —— Stage 精确命中 {StageExact} / " +
            "回退物料级默认 {MaterialDefault} / 规则越界按 100 {OutOfRange} / 完全缺配按 100 {NoConfig}；" +
            "冻结快照良率规则 {RuleCount} 条",
            projection.Rows.Count, projection.YieldStageExactRowCount,
            projection.YieldMaterialDefaultRowCount, projection.YieldOutOfRangeRowCount,
            projection.YieldNoConfigRowCount, yieldRuleCount);

        // 「规则集非空 ∧ 全行 NO_CONFIG」由 Info 升 Warning（1号位 2026-10-25 提请）：
        //   规则**存在**却一行都没命中，说明物料键对不上（`PlanningYieldRule.MaterialId` 是物料**编码**语义），
        //   不是「本期没配良率」—— 后者才保持 Info。两者处置方向相反：前者回查规则发布键口径，后者等规则发布。
        if (yieldRuleCount > 0 && projection.Rows.Count > 0
            && projection.YieldNoConfigRowCount == projection.Rows.Count)
        {
            _logger.LogWarning(
                "[Pegging] G-1 投影良率：冻结快照有 {RuleCount} 条规则，但逐 Stage {Rows} 行**全部**落到「完全缺配」" +
                "⇒ 规则与物料键完全对不上，所有段按 100 不放大（产能负载偏乐观）。" +
                "请核对规则发布侧的物料键与 `Material.MaterialCode` 是否同一口径",
                yieldRuleCount, projection.Rows.Count);
        }

        if (projection.YieldOutOfRangeRowCount > 0)
        {
            _logger.LogWarning(
                "[Pegging] G-1 投影：{Count} 行命中了良率规则但取值越界（≤0 或 >100）⇒ 按 100 不放大。" +
                "规则本身有值却不能用的，须回查规则发布侧（本表不擅改取值）",
                projection.YieldOutOfRangeRowCount);
        }

        if (projection.YieldFallbackSamples.Count > 0)
        {
            // 只留痕最多 10 条：回答「这一行的 100／95 到底从哪来」，不参与计算。
            var samples = string.Join("; ", projection.YieldFallbackSamples.Select(s =>
                $"{s.LogicalDemandKey}/{s.StageCode}={s.YieldPercent}%({s.Level})"));
            _logger.LogInformation(
                "[Pegging] G-1 投影良率非精确命中样例（最多 {Max} 条）：{Samples}",
                DemandStageProcessQuantityProjector.MaxYieldFallbackSamples, samples);
        }

        if (projection.NonContinuationMidChainDemandCount > 0)
        {
            // 既有口径风险（不在本轮擅自改）：`FillStartStageCodes` 取 Routing 无入边源结点按 OperationCode 序的第一个，
            //   多源结点的工艺可能因此落到链中段 ⇒ 前段 Stage 既不给 1号位、也不给本表。只数、只告警。
            _logger.LogWarning(
                "[Pegging] G-1 投影：{Count} 条**非续排**需求的 `StartStageCode` 落在 Stage 链中段（不是链首）" +
                "⇒ 前段 Stage 无逐 Stage 量。根因疑为 `FillStartStageCodes` 在多源结点工艺上按 OperationCode 字符串序取首" +
                "（实测 26,332 个物料属多源结点且阶段不同）；1号位 起点同用该字段，口径一致，未擅自更改",
                projection.NonContinuationMidChainDemandCount);
        }

        if (projection.DemandsWithoutChain > 0)
        {
            _logger.LogInformation(
                "[Pegging] G-1 投影：{Count} 条需求无有效 Stage 链（采购件 / 无 StagePath）⇒ 不出逐 Stage 行，" +
                "该需求 1号位 沿用需求级 PlannedProcessQty（与投影前一致）",
                projection.DemandsWithoutChain);
        }

        if (projection.DemandsUnresolvedRange > 0)
        {
            _logger.LogWarning(
                "[Pegging] G-1 投影：{Count} 条需求的 `StartStageCode` / `RequiredStageCode` 不在其 Stage 链上" +
                "（或终点早于起点）⇒ 不出行、不猜终点；该需求物料**有链**，按契约应作「Stage 量未匹配」" +
                "记未排/Issue，**不得**回落需求级量（0号位《03_R04 交叉核证》§三-2）。请核查 StagePath 与 BOM 边阈值的一致性",
                projection.DemandsUnresolvedRange);
        }

        if (projection.DemandsNonPositiveQty > 0)
        {
            _logger.LogWarning(
                "[Pegging] G-1 投影：{Count} 条需求净量 ≤ 0 ⇒ 无加工量、不出行（不造零量 Stage）",
                projection.DemandsNonPositiveQty);
        }

        if (projection.DuplicateChainMaterialCount > 0)
        {
            _logger.LogWarning(
                "[Pegging] G-1 投影：{Count} 个物料出现多条 Stage 链 ⇒ 只取第一条（与 `StageSequenceChains` 同口径），" +
                "请回查 `EffectiveStageChainResolver` 的隔离结果",
                projection.DuplicateChainMaterialCount);
        }

        // 接近上限预警（1号位 2026-10-25 提请）：截断是 Fail Closed，等它发生就已经晚了
        //   —— 距安全上限不足 20% 就先告警，给「本轮要不要拆域 / 收缩需求范围」留出决策窗口。
        var rowCap = DemandStageProcessQuantityProjector.MaxRowCount;
        if (projection.TotalRowCount * 10 >= rowCap * 8)
        {
            _logger.LogWarning(
                "[Pegging] G-1 投影已用 {Total}/{Max} 行（{Percent}%），距安全上限不足 20%："
                + "超限即按 Fail Closed 阻断本域 Run，请评估拆分域或收缩需求范围",
                projection.TotalRowCount, rowCap,
                Math.Round(projection.TotalRowCount * 100m / rowCap, 1));
        }

        if (projection.Truncated)
        {
            // Fail Closed（1号位 2026-10-25 G-1 回执 §四 甲）：截断后「有链需求拿到的是有行但缺后几段」，
            //   消费侧按契约会判「该段不注入 ⇒ 未排」—— 数是保守的，但**原因会写错**，
            //   而且除日志里一行 Error 之外没人能看出是截断造成的。部分载荷比没有载荷更危险
            //   （没有载荷会全员回落旧口径、至少一致；部分载荷会产出一批「看起来有理由的未排」）。
            var missingRows = projection.TotalRowCount - projection.Rows.Count;
            _logger.LogError(
                "[Pegging] G-1 投影命中行数上限 {Max} 被截断 ⇒ Fail Closed，阻断本域 Run（逐 Stage 载荷不完整不可交付）；"
                + "载荷 {Rows} 行 / 真实应有 {Total} 行（占 {Percent}%），缺 {Missing} 行",
                rowCap, projection.Rows.Count, projection.TotalRowCount,
                Math.Round(projection.Rows.Count * 100m / projection.TotalRowCount, 1), missingRows);
            throw new InvalidOperationException(
                $"G-1 逐 Stage 加工量投影命中安全上限 {rowCap} 行被截断："
                + $"本域需求 {demands.Count} 条、Stage 链 {chains.Count} 条；"
                + $"载荷 {projection.Rows.Count} 行 / 真实应有 {projection.TotalRowCount} 行（缺 {missingRows} 行）。"
                + "截断后的载荷会让被截需求被读成『Stage 量未匹配』"
                + "⇒ 按 Fail Closed 阻断本域（不得交付部分载荷）。");
        }

        return projection.Rows.ToList();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BOM 快照装载
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 从 APS_BOM_RAW 加载 BOM 快照（按父件编码索引，供 PeggingLoop BOM 树遍历使用）
    ///
    /// 批次策略（0号位 2026-10-17 §三.D）：**只取当前 PlanVersion 可证关联的 BatchNo**（经 `OrderBomRequestLink`，
    /// 同 `SyncedAt DESC` 取最新一条）。
    /// ⛔ 已删除「关联缺失 ⇒ `TOP 1 APS_BOM_RAW ORDER BY SyncedAt DESC` 取最新批」的无条件兜底：
    ///   那等于拿「碰巧最新的一批」冒充「本 PV 应使用的一批」，既无请求身份也无来源/时点核对（Q4）。
    ///   无关联批 ⇒ 返回 <see cref="BomInputState.NoLinkedBatch"/> 的空快照，由调用方按缺事实处置。
    /// </summary>
    /// <param name="scopeOrderIds">
    /// **可选**：把 BOM 快照再收窄到「这些订单」所使用的 BOM（默认 <c>null</c> = 不收窄，与既有行为逐字一致）。
    /// 用途 = **小样本定向跑**（改一行代码要在几秒内看到结果，而不是等全量 160s）：不传时按整 PV 的
    /// <c>ResolvedBOMNO</c> 装载，实测 PV2 = 846,170 行 BOM / 118,151 条 PI 事实 ⇒ 单是 5号位 PI Position
    /// 计算就要 **105s**；传 5 个订单后整个下游物料宇宙一起塌下来。
    /// ⚠️ 生产路径**不传**本参数 —— 「BOM 宇宙是否随订单子集收窄」是业务口径问题（部分订单运行时
    /// 是否仍需看到域内其它物料的 BOM），须 PM 裁决后才可动主链。见台账 §T-1009p。
    /// </param>
    private async Task<BomSnapshot> LoadBomSnapshotAsync(
        int planVersionId,
        CancellationToken ct,
        IReadOnlyCollection<long>? scopeOrderIds = null)
    {
        // ── 批次身份：只认 `OrderBomRequestLink`（本 PV 可证关联），不猜「最新批」──
        //   与 `LoadEffectiveStageKeysAsync` 的有效 Stage 装载**同源同口径**：两处此前各写一份取批 SQL，
        //   一旦漂移就会出现「BOM 父子结构取 A 批、Stage 链取 B 批」的错配。现由本方法统一解析一次。
        var linkedBatchNos = await ResolveLinkedBatchNosAsync(planVersionId);
        var linkedBatchNo = linkedBatchNos.FirstOrDefault();

        if (string.IsNullOrEmpty(linkedBatchNo))
        {
            _logger.LogError(
                "[Pegging] BOM 输入缺失：本 PlanVersion 无 `OrderBomRequestLink` 关联批（PlanVersionId={PlanVersionId}），" +
                "**不再**回落 `TOP 1 APS_BOM_RAW ORDER BY SyncedAt DESC` 的旧批；BOM 快照按空装载并标记 NoLinkedBatch",
                planVersionId);

            return new BomSnapshot(
                Enumerable.Empty<BomEdge>().ToLookup(e => e.ParentCode),
                new Dictionary<string, int>(),
                new Dictionary<string, bool>(),
                0,
                new HashSet<string>(StringComparer.Ordinal),
                new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyCollection<string>>>(StringComparer.Ordinal),
                new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyCollection<string>>>(StringComparer.Ordinal))
            {
                BatchNo = null,
                InputState = BomInputState.NoLinkedBatch,
            };
        }

        // 多批次诊断（原在 `LoadEffectiveStageKeysAsync`，随取批统一迁到此处，避免两处各报一次）。
        var distinctLinkedBatchCount = linkedBatchNos.Distinct(StringComparer.Ordinal).Count();
        if (distinctLinkedBatchCount > 1)
        {
            _logger.LogWarning(
                "[Pegging] 本 PlanVersion 关联 {BatchCount} 个 BOM 批次，取 `SyncedAt` 最新的一批 {BatchNo}" +
                "（BOM 快照与有效 Stage 装载同口径，PlanVersionId={PlanVersionId}）",
                distinctLinkedBatchCount, linkedBatchNo, planVersionId);
        }

        // 【2026-09-28 收窄】按 `OrderBomRequestLink.ResolvedBOMNO` 限定「本 PV 订单实际使用的 BOM」，
// 替代原「只按 BatchNo 取整批」——实测（PV539+540）3,672 BOMNO → 486（13%），
// 行数 3,272,701 → 846,170（**砍掉 74%**）。PM《BOM取用链接说明》§9.2 明文：
// 「使用 BatchNo + ResolvedBOMNO 进入 APS_BOM_RAW 得到本次 BOM 父子结构」。
// 关联批已由上一步解析（无关联批已在上面提前返回）⇒ 本步只需处理「有关联批、但 ResolvedBOMNO 全空」：
//   此时不过滤 BOMNO，按该批整批装载（关联批本身仍是可证的，只是缺少到 BOMNO 的第二层收窄）。
        var scopeIds = scopeOrderIds?.Where(id => id > 0).Distinct().ToArray();
        var resolvedBomNos = scopeIds is { Length: > 0 }
            ? (await QueryChunkedInAsync(scopeIds, chunk =>
                  _connectionManager.QueryAsync<string>(
                      @"SELECT DISTINCT r.ResolvedBOMNO
                        FROM OrderBomRequestLink r
                        WHERE r.ResolvedBOMNO IS NOT NULL AND r.OrderId IN @Ids",
                      new { Ids = chunk },
                      db: DatabaseId.APS))).ToList()
            : (await _connectionManager.QueryAsync<string>(
                  @"SELECT DISTINCT r.ResolvedBOMNO
                    FROM OrderBomRequestLink r
                    INNER JOIN [Order] o ON o.Id = r.OrderId
                    WHERE o.PlanVersionId = @PlanVersionId AND r.ResolvedBOMNO IS NOT NULL",
                  new { PlanVersionId = planVersionId },
                  db: DatabaseId.APS)).ToList();

        // 【2026-09-29 分片】`IN @ResolvedBomNos` 的列表 = 本 PV 用到的**全部** BOMNO（上面注释的收窄口径实测 486，
        //   但那是 PV539/540；全量 FAMILY_X 域可上千）⇒ 裸 `IN` 会撞 2100，把 BOM 快照整个取空（下游全判无 BOM）。
        //   主查询是「按 BatchNo 过滤 APS_BOM_RAW + LEFT JOIN Material」的单次取数，**无聚合、无跨行 ORDER BY**，
        //   故按 BOMNO 分批取并集与单条 `IN` 等价。
        //   `resolvedBomNos` 为空（关联批在、但 `ResolvedBOMNO` 全空）⇒ 单跑该批整批。
        async Task<List<BomRawRow>> LoadBomRowsAsync(IReadOnlyList<string>? bomNos)
        {
            // 【2026-10-26 性能整改】加 `DISTINCT`：本批 727 个 BOMNO 实测返回 1,646,935 行，
            // 其中 74.3% 是**逐列完全相同**的重复行（见下方「精确重复行去重」段），全部靠传输到
            // 应用侧再在内存里丢掉 —— PV190 实测本段 22,859ms 主要就耗在搬这 122 万行废数据上。
            // 列上没有任何 text/ntext（已核 sys.columns），`DISTINCT` 可用。
            // 实测：1,646,935 → 424,211 行。⚠ 比内存去重键 (BOMNO,父,子,数量) 多留 1,584 行
            // （全列比 4 列严），这 1,584 行仍由下方内存去重收口 ⇒ **最终结果与不加 DISTINCT 逐行相同**。
            var loaded = await _connectionManager.QueryAsync<BomRawRow>(
                @"SELECT DISTINCT
                         b.BOMNO,
                         b.ParentMaterialCode,
                         b.ChildMaterialCode,
                         ISNULL(mc.Id, 0)          AS ChildMaterialId,
                         b.Quantity,
                         b.Level,
                         b.LLC,
                         b.IsLeaf,
                         ISNULL(mc.IsPurchased, 0) AS IsPurchased,
                         b.ChildRequiredStageCode
                  FROM APS_BOM_RAW b
                  LEFT JOIN Material mc ON mc.MaterialCode = b.ChildMaterialCode
                  WHERE b.BatchNo = @BatchNo
                    AND (@HasBomNoFilter = 0 OR b.BOMNO IN @ResolvedBomNos)",
                new
                {
                    BatchNo = linkedBatchNo,
                    HasBomNoFilter = bomNos is { Count: > 0 } ? 1 : 0,
                    ResolvedBomNos = bomNos is { Count: > 0 } ? bomNos : new List<string> { "" }
                },
                db: DatabaseId.APS);
            return loaded.ToList();
        }

        var rows = new List<BomRawRow>();
        if (resolvedBomNos.Count == 0)
        {
            rows.AddRange(await LoadBomRowsAsync(null));
        }
        else
        {
            for (var offset = 0; offset < resolvedBomNos.Count; offset += SqlServerInParameterLimit)
            {
                var chunk = resolvedBomNos.Skip(offset).Take(SqlServerInParameterLimit).ToList();
                rows.AddRange(await LoadBomRowsAsync(chunk));
            }
        }

        if (resolvedBomNos.Count > 0)
        {
            _logger.LogInformation(
                "[Pegging] BOM 快照按 ResolvedBOMNO 收窄：{BomNos} 个 BOMNO，装载 {Rows} 行（PlanVersionId={PlanVersionId}）",
                resolvedBomNos.Count, rows.Count, planVersionId);
        }

        if (rows.Count == 0)
        {
            // 关联批已解析，但该批在 APS_BOM_RAW 一行都没有 ⇒ **BOM 输入缺失**（接货未完成/失败），
            //   不是「该批确实没有父子边」。按 Error 留痕并标记状态（0号位 2026-10-17 §三.D）。
            _logger.LogError(
                "[Pegging] BOM 输入缺失：关联批 {BatchNo} 在 APS_BOM_RAW 无任何行" +
                "（BOMNO 收窄数={BomNos}，PlanVersionId={PlanVersionId}）⇒ 快照按空装载并标记 LinkedBatchNoRows",
                linkedBatchNo, resolvedBomNos.Count, planVersionId);

            return new BomSnapshot(
                Enumerable.Empty<BomEdge>().ToLookup(e => e.ParentCode),
                new Dictionary<string, int>(),
                new Dictionary<string, bool>(),
                0,
                new HashSet<string>(StringComparer.Ordinal),
                new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyCollection<string>>>(StringComparer.Ordinal),
                new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyCollection<string>>>(StringComparer.Ordinal))
            {
                BatchNo = linkedBatchNo,
                InputState = BomInputState.LinkedBatchNoRows,
            };
        }

        // 【2026-10-09 修复·T2-02 尾随】精确重复行去重。
        //
        // APS_BOM_RAW 里同一 (BOMNO, Parent, Child) 存在大量**逐列完全相同**的重复行：
        // 源头 MES 一份 BOM 被多个订单共用，ODS 侧靠 `RequestDetailId` 区分（见 09-28
        // `ODS_BOMWorkset真重复自查_给5号位`）；APS_BOM_RAW **未落该列** ⇒ 在 APS 侧退化成纯重复。
        // 实测（全表）：4,425,669 行 → 874,337 组 (BatchNo,BOMNO,Parent,Child)，**80.2% 是纯重复**。
        //
        // ⛔ 影响：原先这些重复行在 `ToBomStructure` 被 `Sum(Qty)` 累加，把真配比 **2.0 累加成 38.0**
        //    （实测 FINAL_FG-MGPL20-50Z → RAW-C2U20-48-AY700：同 BOMNO 同量 19 行）⇒ 逐层复合放大，
        //    1 张 3,744 的订单 4 层内涨到 **7.4e11**，Σ需求 虚高到 14.05T。
        //    **2026-09-28 我判「不影响计算正确性」是错的**——`BomExplosionService` 只在**求和之后**
        //    按 parentId 去重，配比早已被污染。
        //
        // 去重口径：精确重复不携带信息 ⇒ 按 (BOMNO, Parent, Child, Quantity) 折叠为一条。
        //   **BOMNO 必须进键**：同一父件在不同 BOMNO 下的同父同子边是**两份 BOM 的独立事实**，
        //   即使配比相同也不能互相代表 —— 若按 (Parent, Child, Quantity) 折叠，留下的那一行的
        //   `BomNo` 只是一个代表值，按订单 BOMNO 过滤时另一份 BOM 的边会凭空消失。
        // 保留「同 BOMNO 内同父同子不同配比」的真实多行（全表仅 3,243/679,537 对，交由结构层累加）。
        var dedupedRows = rows
            .GroupBy(r => (BomNo: r.BomNo?.Trim(), r.ParentMaterialCode, r.ChildMaterialCode, r.Quantity))
            .Select(g => g.First())
            .ToList();

        if (dedupedRows.Count != rows.Count)
        {
            _logger.LogInformation(
                "[Pegging] BOM 精确重复去重：{Raw} 行 → {Deduped} 行（去掉 {Dup} 行，{Pct:0.0}%）（PlanVersionId={PlanVersionId}）",
                rows.Count, dedupedRows.Count, rows.Count - dedupedRows.Count,
                rows.Count == 0 ? 0d : 100d * (rows.Count - dedupedRows.Count) / rows.Count,
                planVersionId);
        }

        // ── BOM 子件主档缺失：剔除该边（2026-10-09）──
        // 上面 `ISNULL(mc.Id, 0) AS ChildMaterialId` 把「APS_BOM_RAW 有子件码、Material 主档无此码」
        // **静默压成 0**。若放行，下游会生成 `MaterialId=0` 的 LogicalProductionDemand 交给 1号位
        // （PV2 实测 25 条），并把 `RAW-*` / `WIP-*` 原材料当**自制件**走生产缺口分支。
        // 单点卡口放在**装载**：剔边 ⇒ `edges` / `IsPurchasedByMaterial` / `MaterialUniverse` /
        // `ToBomStructure` 一次全部干净，BFS 与 DFS 两条回路都不再看到 materialId=0。
        // ⚠ 这是**边界**而非猜测：主档缺行 ⇒ 该子件既无单位也无采买属性（`ISNULL(mc.IsPurchased,0)=0`），
        //   APS 侧无从判定，交主档 Owner 修数据（样例见告警），不在 APS 里替它编属性。
        var missingChildRows = dedupedRows.Where(r => r.ChildMaterialId <= 0).ToList();
        if (missingChildRows.Count > 0)
        {
            var missingCodes = missingChildRows
                .Select(r => r.ChildMaterialCode)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(c => c, StringComparer.Ordinal)
                .ToList();
            _logger.LogWarning(
                "[Pegging] BOM 子件主档缺失：{Edges} 条 BOM 边指向 {Codes} 个在 Material 主档**无对应行**的子件物料码，" +
                "已**剔除**（不再生成 MaterialId=0 的生产需求、不再误当自制件）。样例={Sample}；请数据 Owner 补主档" +
                "（这些码多为 RAW-*/WIP-* 原材料，缺行时 APS 无法判定单位与采买属性）。",
                missingChildRows.Count, missingCodes.Count,
                string.Join(",", missingCodes.Take(10)));

            dedupedRows = dedupedRows.Where(r => r.ChildMaterialId > 0).ToList();
        }

        // ── 采购/自制判定源修正（2026-10-09；2026-10-14 补「ERP 优先」）──
        // 【缺陷】上面 `ISNULL(mc.IsPurchased, 0) AS IsPurchased` 是**唯一**的采购/自制判据，而
        //   `Material.IsPurchased` 是**死列**：全库 1,021,984 行中 IsPurchased=1 **零行**。冻结同步 SP
        //   （DDL v5.1.8.3 `MERGE Material`）只写 MaterialCode/MaterialName/Spec/MaterialType/UOM/IsActive
        //   ——INSERT 与 UPDATE 分支**都不含 IsPurchased** ⇒ 恒为列默认值 0。
        //   ⇒ ⇒ **采购件分支（PLANNING_PURCHASE_PLACEHOLDER / `if (isPurchased)`）在生产上永不可达。**
        // 【后果·实测 PV2】本域 BOM 子件 42,930 个物料中 15,616 个 `SupplyMode='PURCHASE'`，其中
        //   **15,550 个没有 RoutingOperation**（采购件本就不该有工艺）。旧行为把它们**全当自制件**，
        //   每个缺口都生成 LogicalProductionDemand 交给 1号位，1号位建不出工序 ⇒ 大面积弃排；
        //   这正是装载审计里「需求物料无任何 Routing 节点」的主因。
        // 【判据权威＝ERP】采购/自制以 ERP 为准：`MaterialSupplyContext.SupplyMode` ←
        //   `ext_ERP_Master_View.SupplyMode` ← `CASE WHEN c.Property IS NOT NULL THEN 'MAKE' ELSE 'PURCHASE' END`
        //   （c 取 `ITProcessCode.Property IN ('AC','M','ZP')`）⇒ 依据是 **ERP 的工艺码属性**，
        //   **不是** APS/MES 有没有工艺路线。
        // 【修正】改以 `MaterialSupplyContext.SupplyMode` 为准（2026-10-14 实测 IsCurrent=1 共 1,801,031 行：
        //   MAKE 1,402,860 / PURCHASE 398,171；取值只出现 MAKE/PURCHASE，`OUTSOURCE`/`MIXED` 实库零行）：
        //     · 主档显式 `IsPurchased=1` 仍**优先**（保留，兼容未来主档回填）；
        //     · **ERP 优先、他源兜底**：该物料有 ERP 行 ⇒ **只看 ERP 行**；无 ERP 行 ⇒ 才采信他源
        //       （实测他源=MES；无 ERP 行的物料 4,718 个，其中 MES 说 PURCHASE 的仅 18 个）；
        //     · 生效集合有 PURCHASE ⇒ 采购件；有 MAKE ⇒ 自制件（保持 false）；
        //     · 无 MSC 行 / OUTSOURCE / 其它 ⇒ **保守按自制**（与旧 `ISNULL(...,0)` 逐字同口径，不新增行为）。
        // ⚠ 「同物料多行」**不是多源打架，而是多仓**：唯一索引为 `(MaterialCode, WarehouseCode, IsCurrent)`，
        //   同一 (物料,仓库) 不可能两源并存（实测重复=0）；两源各占**互不相交的仓库码空间**
        //   （ERP 6 位码如 `540789`；MES 8 位码如 `51305101`）。冻结 07 §6.8 明写「同一物料在不同仓库下
        //   业务语义会变化（采购/自制、生产部门等）」，并给的就是「A 仓采购 / B 仓自制」这一例。
        //   物料级只能有一个答案，故生效集合内跨仓冲突时仍**采购优先**（「任一所辖仓为 PURCHASE 即按采购」）：
        //   宁可建采购占位，也不给没工艺的采购件造生产需求。
        // ⚠ 「用哪一列表达 采购/自制」属**业务口径**。本处不是换口径，而是**同一口径（采购⇒占位 / 自制⇒生产）
        //   换一个有值的来源字段**：原字段结构性无值 ⇒ 规则等价于未实现。若 PM 另有权威字段，以 PM 口径覆盖。
        var childCodes = dedupedRows
            .Select(r => r.ChildMaterialCode)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var mscRows = await QueryChunkedInAsync(childCodes, chunk =>
            _connectionManager.QueryAsync<MaterialSupplyModeRow>(
                @"SELECT MaterialCode, SupplyMode, SourceSystem
                  FROM MaterialSupplyContext
                  WHERE IsCurrent = 1 AND MaterialCode IN @Codes",
                new { Codes = chunk },
                db: DatabaseId.APS));

        var erpPurchase      = new HashSet<string>(StringComparer.Ordinal);
        var erpMake          = new HashSet<string>(StringComparer.Ordinal);
        var erpMaterials     = new HashSet<string>(StringComparer.Ordinal);
        var fallbackPurchase = new HashSet<string>(StringComparer.Ordinal);
        var fallbackMake     = new HashSet<string>(StringComparer.Ordinal);

        foreach (var r in mscRows)
        {
            var isErp      = string.Equals(r.SourceSystem, "ERP", StringComparison.OrdinalIgnoreCase);
            var isPurchase = string.Equals(r.SupplyMode, "PURCHASE", StringComparison.OrdinalIgnoreCase);
            var isMake     = string.Equals(r.SupplyMode, "MAKE", StringComparison.OrdinalIgnoreCase);

            if (isErp) erpMaterials.Add(r.MaterialCode);    // 该物料 ERP 有话语权 ⇒ 他源不再参与
            if (!isPurchase && !isMake) continue;           // OUTSOURCE / 其它：无判据，按自制保守

            if (isErp) (isPurchase ? erpPurchase : erpMake).Add(r.MaterialCode);
            else       (isPurchase ? fallbackPurchase : fallbackMake).Add(r.MaterialCode);
        }

        // 生效集合：有 ERP 行的物料只认 ERP；无 ERP 行的物料才让兜底来源进来（见上「ERP 优先、他源兜底」）。
        var purchaseModeCodes = new HashSet<string>(erpPurchase, StringComparer.Ordinal);
        var makeModeCodes     = new HashSet<string>(erpMake, StringComparer.Ordinal);
        var fallbackMaterials = new HashSet<string>(StringComparer.Ordinal);
        foreach (var code in fallbackPurchase)
            if (!erpMaterials.Contains(code)) { purchaseModeCodes.Add(code); fallbackMaterials.Add(code); }
        foreach (var code in fallbackMake)
            if (!erpMaterials.Contains(code)) { makeModeCodes.Add(code); fallbackMaterials.Add(code); }

        var conflictMaterials    = purchaseModeCodes.Intersect(makeModeCodes, StringComparer.Ordinal).ToList();
        var erpConflictMaterials = erpPurchase.Intersect(erpMake, StringComparer.Ordinal).ToList();

        var purchaseOverridden = 0;
        var noSupplyModeSource = 0;
        foreach (var r in dedupedRows)
        {
            if (r.IsPurchased) continue;                    // 主档显式标记优先
            if (purchaseModeCodes.Contains(r.ChildMaterialCode))
            {
                r.IsPurchased = true;                       // 冲突时采购优先（见上）
                purchaseOverridden++;
            }
            else if (!makeModeCodes.Contains(r.ChildMaterialCode))
            {
                noSupplyModeSource++;                       // 无 MSC 行：保守按自制，不新增行为
            }
        }

        _logger.LogInformation(
            "[Pegging] 采购/自制判定源修正：按 MaterialSupplyContext.SupplyMode（**ERP 优先、他源兜底**）判为采购件的 BOM 边={Overridden} 条" +
            "（主档 IsPurchased 全库恒 0，无判据）；无任何 SupplyMode 行按自制保守={NoSource} 条；" +
            "ERP 无行、靠他源兜底的物料={Fallback} 个；生效集合内跨仓冲突物料={Conflict} 个",
            purchaseOverridden, noSupplyModeSource, fallbackMaterials.Count, conflictMaterials.Count);

        if (conflictMaterials.Count > 0)
        {
            _logger.LogWarning(
                "[Pegging] SupplyMode 跨仓冲突物料 {Count} 个（同一物料既有 MAKE 仓又有 PURCHASE 仓，本处按采购优先）：样例={Sample}",
                conflictMaterials.Count, string.Join(",", conflictMaterials.Take(10)));
        }

        if (erpConflictMaterials.Count > 0)
        {
            _logger.LogWarning(
                "[Pegging] ⚠ ERP 来源自身出现跨仓 SupplyMode 冲突物料 {Count} 个（2026-10-14 实测应为 0；出现即 ERP 主数据内部不一致，" +
                "本处按采购优先）：样例={Sample}",
                erpConflictMaterials.Count, string.Join(",", erpConflictMaterials.Take(10)));
        }

        var edges = dedupedRows.Select(r => new BomEdge(
            r.ParentMaterialCode,
            r.ChildMaterialCode,
            r.ChildMaterialId,
            r.Quantity,
            r.Level,
            r.IsLeaf,
            r.IsPurchased,
            r.ChildRequiredStageCode,
            r.BomNo?.Trim())).ToList();

        // LLC 取各物料在所有 BOM 路径中出现的最小值
        var llcByMaterial = dedupedRows
            .Where(r => r.LLC.HasValue)
            .GroupBy(r => r.ChildMaterialCode)
            .ToDictionary(g => g.Key, g => g.Min(r => r.LLC!.Value));

        // IsPurchased 按物料编码分组（每个物料只有一个IsPurchased值）
        var isPurchasedByMaterial = dedupedRows
            .GroupBy(r => r.ChildMaterialCode)
            .ToDictionary(g => g.Key, g => g.First().IsPurchased);

        // 本域 BOM 可达物料宇宙（父件 ∪ 子件）：PI 事实装载按它放宽，见 BomSnapshot.MaterialUniverse 说明。
        var materialUniverse = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in dedupedRows)
        {
            materialUniverse.Add(r.ParentMaterialCode);
            materialUniverse.Add(r.ChildMaterialCode);
        }

        _logger.LogInformation(
            "[Pegging] BOM 可达物料宇宙：{Materials} 个物料（父件∪子件，PlanVersionId={PlanVersionId}）",
            materialUniverse.Count, planVersionId);

        // ── 按请求（BOMNO）建两张选行索引（J14-02/J14-03）──
        // 只影响「需求 → Stage 链/供给阈值」的选取；BFS 展开的按-BOMNO 取边由 `ToBomStructures` 负责。
        var parentsByBomChild = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.Ordinal);
        var thresholdsByBomChild = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.Ordinal);

        foreach (var r in dedupedRows)
        {
            if (string.IsNullOrWhiteSpace(r.BomNo))
                continue;

            var bomNo = r.BomNo.Trim();
            if (!parentsByBomChild.TryGetValue(bomNo, out var parentsByChild))
                parentsByBomChild[bomNo] = parentsByChild = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            if (!parentsByChild.TryGetValue(r.ChildMaterialCode, out var parents))
                parentsByChild[r.ChildMaterialCode] = parents = new HashSet<string>(StringComparer.Ordinal);
            parents.Add(r.ParentMaterialCode);

            if (string.IsNullOrWhiteSpace(r.ChildRequiredStageCode))
                continue;

            if (!thresholdsByBomChild.TryGetValue(bomNo, out var thresholdsByChild))
                thresholdsByBomChild[bomNo] = thresholdsByChild = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            if (!thresholdsByChild.TryGetValue(r.ChildMaterialCode, out var thresholds))
                thresholdsByChild[r.ChildMaterialCode] = thresholds = new HashSet<string>(StringComparer.Ordinal);
            thresholds.Add(r.ChildRequiredStageCode!.Trim());
        }

        static IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyCollection<string>>> Freeze(
            Dictionary<string, Dictionary<string, HashSet<string>>> source)
            => source.ToDictionary(
                b => b.Key,
                b => (IReadOnlyDictionary<string, IReadOnlyCollection<string>>)b.Value.ToDictionary(
                    c => c.Key,
                    c => (IReadOnlyCollection<string>)c.Value.ToArray(),
                    StringComparer.Ordinal),
                StringComparer.Ordinal);

        return new BomSnapshot(edges.ToLookup(e => e.ParentCode), llcByMaterial, isPurchasedByMaterial, edges.Count,
                               materialUniverse, Freeze(parentsByBomChild), Freeze(thresholdsByBomChild))
        {
            BatchNo = linkedBatchNo,
            InputState = BomInputState.Available,
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 订单装载（PeggingLoop 前置步骤）
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class OrderPeggingRow
    {
        public long     OrderId          { get; set; }
        public long?    OrderCanonicalId { get; set; }
        public int      MaterialId       { get; set; }
        public string   MaterialCode     { get; set; } = string.Empty;
        public int      FactoryId        { get; set; }
        public string   FactoryCode      { get; set; } = string.Empty;
        public string?  OrderType        { get; set; }
        public string?  CustomerTier     { get; set; }
        public DateTime? IssueDate       { get; set; }
        /// <summary>
        /// 延迟状态（<c>[Order].DelayStatus</c>）：<c>ON_TIME</c> / <c>FIRST_DELAY</c> / <c>REPEATED_DELAY</c>。
        /// 需求排序可用条件之一（01 v1.13 :536、Pegging v1.6 :257，规则由 3号位 配置、2号位 执行）。
        /// 装载链自 2026-10-17 起由 <c>sp_SyncOrdersToPartitionTable</c> 从 <c>Order_Canonical</c> 透传。
        /// </summary>
        public string?  DelayStatus      { get; set; }
        /// <summary>
        /// 需求根，**按「有没有生产指示」分两类**（用户 2026-10-10 定盘）：
        /// <list type="bullet">
        ///   <item><b>有</b> <see cref="MTS_InstructionNo"/>（真 PI 行）：<c>max(Quantity − ISNULL(ReceivedQty,0), 0)</c>
        ///     —— 与供给侧 PI 剩余同一条公式：Quantity 是**总量**、ReceivedQty 是**已完工入库量**，
        ///     剩余才该进 APS（PV2 实测 PI 15526439：600/已收 480，MES 末道「捆包」=0 恰欠 120）。</item>
        ///   <item><b>无</b> <see cref="MTS_InstructionNo"/>（无 PI 需求）：<c>Quantity</c> 原值，不走 PI 公式（PM R-17）。</item>
        /// </list>
        /// <b>不以 <see cref="OrderType"/> 判别</b>——它就是 <c>ZPQF</c> 的映射（ZPQF=1→SALES_ORDER、ZPQF=2→PRODUCTION_INSTRUCTION），
        /// 而 ZPQF 的区分与我方无关（用户 2026-10-09 定盘）。
        /// 取数见 <see cref="LoadOrdersForPeggingAsync"/> 的 SQL 注释。
        /// </summary>
        public decimal  DemandQty        { get; set; }
        public DateTime DueDate          { get; set; }
        public string   UOM              { get; set; } = string.Empty;
        public int?     ProductFamilyId  { get; set; }
        public string?  MTS_InstructionNo { get; set; }

        /// <summary>订单号（<c>[Order].OrderNo</c>）：P 前缀 = PI 自身指令行，O 前缀 = 客户订单行。</summary>
        public string?  OrderNo          { get; set; }

        /// <summary>源订单 ID（<c>[Order].SourceOrderId</c>）：P 行时恒等于自身 PI 号（实测 100% 一致）。</summary>
        public string?  SourceOrderId    { get; set; }

        /// <summary>
        /// 是否「PI 自身指令行」（P 行）。判据 = <c>OrderNo</c> 以 <c>'P'</c> 开头。
        ///
        /// 为什么不用 <c>OrderType</c>：PV2 实测有 **533 行** <c>OrderType='PRODUCTION_INSTRUCTION'</c>
        /// 却是 O 行（客户订单引用了 PI），用 OrderType 判会把它们误当 P 行。
        /// 为什么不用 <c>SourceOrderId == MTS_InstructionNo</c>：实测与 <c>OrderNo LIKE 'P%'</c>
        /// **22,144 行 100% 一致**，但那是数据巧合式的判据，业务语义不如订单号前缀直白；此处取前缀。
        ///
        /// ⚠️ 2026-10-09 起**不再用于供给侧卫语句**：T2-02.4 自消费保护改由**层级门**实现
        /// （<c>NetAndAllocate</c> 的 <c>includePi = bomLevel &gt; 0</c>），L0 整层不取 PI 条目。
        /// 本属性与 <see cref="OrderNo"/> / <see cref="SourceOrderId"/> 保留给 T2-03「顶层生产需求的
        /// PI 身份 = 订单号（出荷指示号）」用（用户 2026-10-09 口径）。
        /// </summary>
        public bool IsPiSelfRow => !string.IsNullOrEmpty(OrderNo)
                                   && (OrderNo[0] == 'P' || OrderNo[0] == 'p');

        /// <summary>
        /// 竞争轮次（B1 §3.1，2026-10-20）。
        ///
        /// **只做分类，不改需求集合** —— 本属性不参与任何过滤，加载出来的行与之前逐行一致；
        /// 轮次唯一的作用是在 <c>BuildDemandSequenceMapAsync</c> 里决定「先给谁发 DemandSequence」。
        /// 轮次只影响 `DemandSequence` 的**赋值先后**，不新增排序键 ⇒ `RunBfsLoop` 仍只按
        /// `DemandSequence` 升序，1号位 零改动。
        ///
        /// 判据（三条轴分开，不得合并）：
        ///   轴 A 成品层身份 = `Material.MaterialCode LIKE 'FINAL_FG%'`
        ///     （冻结标准化身份：`01 v1.14 :3406`／`04 V3.31 :175`；**不是** MaterialType、**不是** BOM 拓扑、**不是** P 前缀）；
        ///   轴 B 上层承接事实 = 该 PI 是否被销售指示引用（已由视图 P 支的 `NOT EXISTS` 落成「P 行／非 P 行」）；
        ///   ⇒ 孤儿 MTS = P 行 ∧ 非 `FINAL_FG` ⇒ 第 3 轮；成品顶层 MTS 与普通 SO ⇒ 第 1 轮。
        /// 轮次 ≠ BOM 计算层级（0号位 Q1 明令）。
        /// PV190 实测：22,017 行全部 `FINAL_FG` ⇒ 本属性全批为 1，行为与改动前逐字样一致。
        /// </summary>
        public int CompetitionRound { get; set; } = 1;
    }

    private async Task<IReadOnlyList<OrderPeggingRow>> LoadOrdersForPeggingAsync(
        PeggingExecutionRequest request,
        CancellationToken ct)
    {
        var rows = await QueryChunkedInAsync(request.OrderIds, chunk =>
            _connectionManager.QueryAsync<OrderPeggingRow>(
                @"SELECT o.Id          AS OrderId,
                         o.OrderCanonicalId,
                         o.MaterialId,
                         m.MaterialCode,
                         o.FactoryId,
                         f.Code        AS FactoryCode,
                         o.OrderType,
                         o.CustomerTier,
                         o.IssueDate,
                         o.DelayStatus,
                         -- ── 需求根（用户 2026-10-10 定盘）：按「有没有生产指示」分两类 ──
                         -- 判别「有 PI」**只看 `MTS_InstructionNo`**（与供给侧 `LoadPiRemainingFactsAsync` 同一条判据）：
                         --   不看 `OrderType` / ZPQF、不看 OrderNo 前缀 —— `OrderType` 就是 `ZPQF` 的映射
                         --   （`v_APS_SalesOrder`：`Cast(s.ZPQF AS NVARCHAR(50)) AS OrderType`；DDL：ZPQF=1→SALES_ORDER、
                         --   ZPQF=2→PRODUCTION_INSTRUCTION），而 ZPQF 的区分**与我方无关**（用户 2026-10-09 定盘）。
                         --
                         -- 有生产指示（真 PI 行）：需求根 = PI 剩余 = max(Quantity − ISNULL(ReceivedQty,0), 0)，
                         --   与供给侧 `LoadPiRemainingFactsAsync` 同一条公式。Quantity 是**总量**、ReceivedQty 是**已完工入库量**，
                         --   原写法取裸 Quantity 会让需求多出 ReceivedQty，并经 L0 → BOM 子树全量放大（实测 PI 15526439：600 应 120）。
                         -- 无生产指示（无 PI 需求）：需求根 = `Quantity` 原值 —— 不走 PI 公式（PM R-17「PI 公式只用于真 PI」）。
                         --
                         -- 库测（2026-10-10）：PV2 `[Order]` 22,144 行**全部有** `MTS_InstructionNo`（一行=一条需求，
                         --   OrderNo 与 MTS_InstructionNo 各自唯一、零重复、零跨类型）⇒ 本次改动**数值零变化**，
                         --   只是把「一律做减」收紧成「只对真 PI 行做减」，防上游将来给无 PI 行盖上假 ReceivedQty。
                         CASE WHEN o.MTS_InstructionNo IS NOT NULL AND o.MTS_InstructionNo <> ''
                                   AND o.Quantity - ISNULL(o.ReceivedQty, 0) > 0
                              THEN o.Quantity - ISNULL(o.ReceivedQty, 0)
                              WHEN o.MTS_InstructionNo IS NOT NULL AND o.MTS_InstructionNo <> ''
                              THEN 0
                              ELSE o.Quantity END AS DemandQty,
                         o.CustomerDueDate AS DueDate,
                         o.UOM,
                         m.ProductFamilyId,
                         o.MTS_InstructionNo,
                         o.OrderNo,
                         o.SourceOrderId,
                         -- ── 竞争轮次（B1 §3.1）：**只分类、不过滤**，需求集合与改动前逐行一致 ──
                         -- 孤儿 MTS = P 行 ∧ 非 FINAL_FG ⇒ 第 3 轮；其余（普通 SO、成品顶层 MTS）⇒ 第 1 轮。
                         -- 成品层判据用 FINAL_FG 前缀（01 v1.14 :3406 / 04 V3.31 :175「FINAL 不是 MaterialType」），
                         -- 不用 MaterialType、不用 BOM 拓扑、不单靠 P 前缀（0号位 Q2）。
                         -- PV190 实测：22,017 行全 FINAL_FG ⇒ 全批落 1，行为零变化。
                         CASE WHEN o.OrderNo LIKE 'P%'
                                   AND m.MaterialCode NOT LIKE 'FINAL_FG%'
                              THEN 3 ELSE 1 END AS CompetitionRound
                  FROM [Order] o
                  INNER JOIN Material m ON m.Id = o.MaterialId
                  INNER JOIN Factory  f ON f.Id = o.FactoryId
                  WHERE o.Id IN @OrderIds",
                new { OrderIds = chunk },
                db: DatabaseId.APS));

        return rows.ToList();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PeggingLoop 主逻辑
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// BOM 遍历 + 供给扣减主函数。
    /// 每笔订单从根物料出发递归展开 BOM 树，在每个节点对 SupplyPool 执行贪婪扣减。
    /// 扣减结果累积到同一 PeggingResultVoucher。
    /// </summary>
    private async Task<PeggingResultVoucher> ExecutePeggingLoopAsync(
        PeggingExecutionRequest request,
        BomSnapshot bom,
        SupplyPool supplyPool,
        CancellationToken ct)
    {
        var orders = await LoadOrdersForPeggingAsync(request, ct);

        // ── Demand 优先级排序（2号位消费3号位 DemandPriorityConfig）──
        // 位置：LoadOrdersForPeggingAsync 之后、Pegging 循环之前（PM 冻结口径，不进 SQL 排序）
        // 结果：OrderId → DemandSequence，决定订单处理顺序，并透传给 LogicalProductionDemand
        var demandSequenceByOrder = await BuildDemandSequenceMapAsync(orders, request, ct);

        // 主链切新 BFS（2026-09-18 用户裁决：旧 DFS 无记忆化在真实 BOM 上 10 单/20 分钟不收敛，弃旧计算）：
        // 分配层 O(订单×物料×工厂去重数) + 共享子件合一 LPD；旧 RunDfsLoop 仅留 RunDualCompareAsync 对照用。
        var (bomStructure, structuresByBomNo) = ToBomStructures(bom);

        // 订单 → ResolvedBOMNO（与 `FillDemandRequestAnchorsAsync` 同源：`OrderBomRequestLink` 最新一条）。
        //   展开按订单自己的 BOM 取边；无锚点的订单用并集（退化口径，由 RunBfsLoop 计数留痕）。
        var bomAnchors = await LoadBomAnchorsAsync(request.PlanVersionId, ct);
        var bomNoByOrder = bomAnchors.ToDictionary(kv => kv.Key, kv => kv.Value.ResolvedBOMNO);

        return RunBfsLoop(orders, demandSequenceByOrder, request, bom, bomStructure, supplyPool, ct, out _,
            structuresByBomNo, bomNoByOrder);
    }

    /// <summary>
    /// 旧 DFS 循环（阶段2 S2.3 从 ExecutePeggingLoopAsync 原封抽出，语义零变化）：
    /// 按 DemandSequence 升序逐订单从根物料递归展开 BOM，在每个节点对 SupplyPool 贪婪扣减。
    /// 已弃旧（2026-09-18 用户裁决：路径级 visited 无记忆化，真实 BOM 上共享子件指数重遍历不收敛）。
    /// 主链已切 <see cref="RunBfsLoop"/>；本方法仅保留给 <see cref="RunDualCompareAsync"/> 跑「旧」侧对照，非每日排程路径。
    /// </summary>
    private PeggingResultVoucher RunDfsLoop(
        IReadOnlyList<OrderPeggingRow> orders,
        IReadOnlyDictionary<long, int> demandSequenceByOrder,
        PeggingExecutionRequest request,
        BomSnapshot bom,
        SupplyPool supplyPool,
        CancellationToken ct,
        out BomTraversalStats traversalStats)
    {
        var voucher = BuildResultVoucher(request, orders);

        // 按 DemandSequence 升序遍历：优先级高的订单先抢供给
        var orderedOrders = orders
            .OrderBy(o => demandSequenceByOrder.GetValueOrDefault(o.OrderId, int.MaxValue))
            .ToList();

        var orderIdx = 0;
        var loopSw = System.Diagnostics.Stopwatch.StartNew();
        traversalStats = new BomTraversalStats();
        foreach (var order in orderedOrders)
        {
            ct.ThrowIfCancellationRequested();
            orderIdx++;
            if (orderIdx == 1 || orderIdx % 25 == 0)
                _logger.LogInformation("[Pegging][进度] 回路 {Idx}/{Total} OrderId={OrderId} 物料={Material} 累计={Elapsed}ms",
                    orderIdx, orderedOrders.Count, order.OrderId, order.MaterialCode, loopSw.ElapsedMilliseconds);

            var demandSequence = demandSequenceByOrder.GetValueOrDefault(order.OrderId, 0);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            _ = TraverseBomNode(
                order,
                order.MaterialCode,
                order.MaterialId,
                order.FactoryId,
                order.FactoryCode,
                order.DemandQty,
                bomLevel: 0,
                demandSequence: demandSequence,
                bom,
                supplyPool,
                voucher,
                visited,
                traversalStats: traversalStats);
        }

        _logger.LogInformation(
            "[Pegging][BOM埋点] 展开总次数={Total} 唯一节点={Unique} 重复展开={Dup} 最大共享节点={MaxMat}(访问{MaxCnt}) 订单={Orders} 总耗时={Elapsed}ms 平均单节点={Avg:F3}ms",
            traversalStats.TotalVisits,
            traversalStats.UniqueNodes,
            traversalStats.DuplicateExpansions,
            traversalStats.MaxSharedMaterialCode,
            traversalStats.MaximumVisits,
            orderedOrders.Count,
            loopSw.ElapsedMilliseconds,
            traversalStats.TotalVisits == 0 ? 0d : (double)loopSw.ElapsedMilliseconds / traversalStats.TotalVisits);

        if (traversalStats.MissingChildMaterialEdges > 0)
        {
            _logger.LogWarning(
                "[Pegging] BOM 子件主档缺失：{Edges} 条 BOM 边在**回路内**才暴露（装载层卡口应已拦住；此处兜底留证）。样例={Sample}",
                traversalStats.MissingChildMaterialEdges,
                string.Join(",", traversalStats.MissingChildMaterialCodes.Take(10)));
        }

        voucher.IsFullyAllocated = voucher.ShortageQuantity == 0;
        voucher.ExecutionTimeMs = loopSw.ElapsedMilliseconds;
        return voucher;
    }

    /// <summary>构造结果凭证头（阶段2 S2.3 双跑复用：旧 DFS 与新 BFS 各自独立凭证，其余字段由扣减过程填充）。</summary>
    private static PeggingResultVoucher BuildResultVoucher(
        PeggingExecutionRequest request,
        IReadOnlyList<OrderPeggingRow> orders)
    {
        var firstOrder = orders.FirstOrDefault();
        return new PeggingResultVoucher
        {
            PlanVersionId    = request.PlanVersionId,
            DomainKey        = request.DomainKey,
            OrderId          = firstOrder?.OrderId ?? request.OrderIds.FirstOrDefault(),
            DemandMaterialId = firstOrder?.MaterialId ?? 0,
            UOM              = firstOrder?.UOM ?? string.Empty,
            IsSuccess        = true,
            ExecutedAt       = DateTime.Now
        };
    }

    /// <summary>
    /// BomSnapshot → BomStructure 适配（阶段2 S2.3）：把旧主链的 BOM 快照转成 Explosion 纯函数层的结构。
    ///
    /// 【2026-10-19：按 BOMNO 分桶，不再跨 BOM 求和】
    ///   旧实现把 `ByParent` 里**所有**边按子件求和 —— 同一父件在多个 BOMNO 下各有一条边时，
    ///   两份 BOM 的配比被叠成一个单位用量（需求整族放大）。而一笔订单只走 `OrderBomRequestLink`
    ///   指定的**一个** ResolvedBOMNO，故展开必须按订单自己的 BOMNO 取边。
    ///   ⇒ 本方法一次遍历产出两份结构：
    ///     · `ByBomNo` —— 每个 BOMNO 一份（同 BOMNO 内同父同子多配比仍累加，干净 BOM 语义不变）；
    ///     · `Union`   —— 全部边（**退化口径**）：订单无锚点 / 锚点 BOMNO 在本批快照内无行时使用，
    ///                   与改动前逐字等价，避免「无锚点即零需求」。
    ///   ⚠️ **精确重复行已在 <see cref="LoadBomSnapshotAsync"/> 装载时去重**（2026-10-09 修复），
    ///      故此处累加只对**真正不同**配比生效。
    /// </summary>
    private static (BomStructure Union, IReadOnlyDictionary<string, BomStructure> ByBomNo) ToBomStructures(
        BomSnapshot bom)
        => ToBomStructures(bom.ByParent.SelectMany(g => g), bom.IsPurchasedByMaterial);

    /// <summary>分桶实现（`internal` 供单测直接断言「跨 BOMNO 不求和」）。</summary>
    internal static (BomStructure Union, IReadOnlyDictionary<string, BomStructure> ByBomNo) ToBomStructures(
        IEnumerable<BomEdge> edges,
        IReadOnlyDictionary<string, bool> isPurchasedByMaterial)
    {
        var union = new Dictionary<string, Dictionary<string, BomComponent>>(StringComparer.Ordinal);
        var byBom = new Dictionary<string, Dictionary<string, Dictionary<string, BomComponent>>>(StringComparer.Ordinal);

        // 并集桶必须与**改动前**逐字等价：按 (父, 子, 配比) 去重后再按子件求和 ——
        //   若不去重，同一配比在多份 BOM 下各出现一次会被加成两倍（老实现只留一条）。
        var seenForUnion = new HashSet<(string Parent, string Child, decimal Qty)>();

        foreach (var edge in edges)
        {
            if (seenForUnion.Add((edge.ParentCode, edge.ChildCode, edge.Qty)))
                Accumulate(union, edge);

            if (string.IsNullOrEmpty(edge.BomNo))
                continue;
            if (!byBom.TryGetValue(edge.BomNo, out var perParent))
                byBom[edge.BomNo] = perParent = new Dictionary<string, Dictionary<string, BomComponent>>(StringComparer.Ordinal);
            Accumulate(perParent, edge);
        }

        var isPurchased = isPurchasedByMaterial;
        return (
            new BomStructure(Freeze(union), isPurchased),
            byBom.ToDictionary(
                kv => kv.Key,
                kv => new BomStructure(Freeze(kv.Value), isPurchased),
                StringComparer.Ordinal));

        // 同父同子在同一桶内累加配比（干净 BOM 语义）
        static void Accumulate(
            Dictionary<string, Dictionary<string, BomComponent>> target, BomEdge edge)
        {
            if (!target.TryGetValue(edge.ParentCode, out var children))
                target[edge.ParentCode] = children = new Dictionary<string, BomComponent>(StringComparer.Ordinal);

            if (children.TryGetValue(edge.ChildCode, out var existing))
                children[edge.ChildCode] = existing with { QtyPerUnit = existing.QtyPerUnit + edge.Qty };
            else
                children[edge.ChildCode] = new BomComponent(edge.ChildCode, edge.ChildMaterialId, edge.Qty);
        }

        static IReadOnlyDictionary<string, IReadOnlyList<BomComponent>> Freeze(
            Dictionary<string, Dictionary<string, BomComponent>> source)
            => source.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<BomComponent>)kv.Value.Values.ToList(),
                StringComparer.Ordinal);
    }

    /// <summary>
    /// 新 BFS 逐层主流程（阶段2 S2.3，PM 0918-3 §二 批准的「宽度优先逐层 + 每(order,material)同一计算上下文一次净额」）。
    /// 不切主链，仅作双跑对照。与旧 DFS 的等价性见方案 §三.3；核心差异：
    ///   a) 每 (order, material, factory) 恰一次 NetAndAllocate（共享子件不再指数重复展开）；
    ///   b) 共享子件生产合并为一张 LPD（碎片 300+300 → 一张 600）；
    ///   c) 血缘边由本层按 ParentEdges 逐边建（一个节点多父 → 多条 Consumer→Producer 边，RequiredQty=父净×配比）。
    /// </summary>
    private PeggingResultVoucher RunBfsLoop(
        IReadOnlyList<OrderPeggingRow> orders,
        IReadOnlyDictionary<long, int> demandSequenceByOrder,
        PeggingExecutionRequest request,
        BomSnapshot bom,
        BomStructure bomStructure,
        SupplyPool supplyPool,
        CancellationToken ct,
        out BomTraversalStats traversalStats,
        IReadOnlyDictionary<string, BomStructure>? structuresByBomNo = null,
        IReadOnlyDictionary<long, string?>? bomNoByOrder = null)
    {
        var voucher = BuildResultVoucher(request, orders);
        var explosion = new BomExplosionService();
        var perBomNoOrders = 0;
        var bomNoFallbackOrders = 0;
        var noAnchorOrders = 0;

        var orderedOrders = orders
            .OrderBy(o => demandSequenceByOrder.GetValueOrDefault(o.OrderId, int.MaxValue))
            .ToList();

        var orderIdx = 0;
        var loopSw = System.Diagnostics.Stopwatch.StartNew();
        traversalStats = new BomTraversalStats();

        foreach (var order in orderedOrders)
        {
            ct.ThrowIfCancellationRequested();
            orderIdx++;
            if (orderIdx == 1 || orderIdx % 25 == 0)
                _logger.LogInformation("[Pegging][BFS进度] 回路 {Idx}/{Total} OrderId={OrderId} 物料={Material} 累计={Elapsed}ms",
                    orderIdx, orderedOrders.Count, order.OrderId, order.MaterialCode, loopSw.ElapsedMilliseconds);

            var demandSequence = demandSequenceByOrder.GetValueOrDefault(order.OrderId, 0);

            // ── 按订单自己的 ResolvedBOMNO 取边展开（2026-10-19）──
            //   一笔订单只走一个 BOM：不按 BOMNO 分桶会把多份 BOM 的同父同子边求和，单位用量被放大。
            //   退化口径（保持改动前逐字行为）：订单无锚点 / 锚点在本批快照内无行 ⇒ 用全部边的并集。
            var structure = bomStructure;
            var orderBomNo = bomNoByOrder is not null && bomNoByOrder.TryGetValue(order.OrderId, out var bn)
                ? bn
                : null;
            if (string.IsNullOrWhiteSpace(orderBomNo))
            {
                noAnchorOrders++;
            }
            else if (structuresByBomNo is not null
                     && structuresByBomNo.TryGetValue(orderBomNo.Trim(), out var perBomStructure))
            {
                structure = perBomStructure;
                perBomNoOrders++;
            }
            else
            {
                bomNoFallbackOrders++;   // 锚点 BOMNO 在本批快照里没有任何边 ⇒ 退化并集，并计数留痕
            }

            var root = new BomOrderDemand(order.OrderId, order.MaterialId, order.MaterialCode, order.FactoryId, order.DemandQty);
            var nodes = explosion.ExplodeOrderStructure(structure, root);

            // produced：本订单内 (material|factory) → 净额承接结果（父净驱动的源量）。
            // 合并 Key = 订单(循环外层) + material + factory；生产上下文(Stage/Domain/必要Routing)为扩展点
            // （PM 0918-3 §二：不得只按 MaterialId 合并；当前数据单订单内 factory 唯一，预留扩展位）。
            var produced = new Dictionary<string, NetAllocationOutcome>(StringComparer.Ordinal);
            var maxLevel = nodes.Count == 0 ? 0 : nodes.Max(n => n.Level);
            for (var level = 0; level <= maxLevel; level++)
            {
                foreach (var node in nodes.Where(n => n.Level == level))
                {
                    traversalStats.Record(node.MaterialCode, node.FactoryId);

                    // 下级依赖需求 = Σ 父件净产出 × 配比（等效旧实现 `AllocatedQty × edge.Qty`）
                    decimal gross = level == 0
                        ? order.DemandQty
                        : node.ParentEdges.Sum(e =>
                            produced.TryGetValue(SupplyPool.BuildKey(e.ParentMaterialCode, node.FactoryId), out var p)
                                ? p.ProducedQty * e.QtyPerUnit
                                : 0m);

                    var outcome = NetAndAllocate(
                        order, node.MaterialCode, node.MaterialId, node.FactoryId, order.FactoryCode,
                        gross, level, demandSequence, bom, supplyPool, voucher,
                        consumerLogicalDemandKey: null, consumerMaterialId: 0);

                    // 血缘建边（父 → 本物料需求/生产）：只要父件有净产出，无论本节点是否生产，都产 link。
                    // Case B（子件全库存/采购占位）：ProducerLogicalDemandKey=null，仍保留「父需求→子需求」真相（0910 §二十）。
                    // 与旧 DFS 差异：旧在 NetAndAllocate 内建「单 consumer」边；BFS 一个节点多父，在此按 ParentEdges 逐边建。
                    foreach (var edge in node.ParentEdges)
                    {
                        if (!produced.TryGetValue(SupplyPool.BuildKey(edge.ParentMaterialCode, node.FactoryId), out var parent))
                            continue;
                        // ⚠ parent.ProducerLogicalDemandKey! 安全不变式：produced 仅在本方法内「ProducerLogicalDemandKey is null 卫语句」之后才写入，
                        // 故父件有生产 → 其 LPD 键必非空，此处 ! 非掩盖空值；若日后放开该卫语句（如采购占位也入字典），
                        // 此 ! 会静默失守（ConsumerLogicalDemandKey=null），须同步改。（符号锚·勿行号引用——行号会随注释自身插入而漂移）
                        voucher.MaterialRequirementLinks.Add(new Core.Dto.MaterialRequirementLink
                        {
                            ConsumerLogicalDemandKey   = parent.ProducerLogicalDemandKey!,
                            ProducerLogicalDemandKey   = outcome?.ProducerLogicalDemandKey,
                            ConsumerMaterialId         = edge.ParentMaterialId,
                            ProducerMaterialId         = node.MaterialId,
                            // ChildDemandKey 与 NetAndAllocate 内 demandKey 同格式（ORDER_{OrderId}_{MaterialCode}_{FactoryId}）
                            ChildDemandKey             = $"ORDER_{order.OrderId}_{node.MaterialCode}_{node.FactoryId}",
                            RequiredQty                = parent.ProducedQty * edge.QtyPerUnit,
                            ProducerAllocationSequence = outcome?.ProducerAllocationSequence ?? 0
                        });
                    }

                    if (outcome is null) continue;                    // 无生产（真实供给补足/采购占位/失败）：不下钻、不存 produced
                    if (outcome.ProducerLogicalDemandKey is null) continue; // 防御：NEW_REQUIREMENT 生产恒有 LPD 身份
                    produced[SupplyPool.BuildKey(node.MaterialCode, node.FactoryId)] = outcome;
                }
            }
        }

        _logger.LogInformation(
            "[Pegging][BFS埋点] 展开总次数={Total} 唯一节点={Unique} 重复展开={Dup} 订单={Orders} 总耗时={Elapsed}ms",
            traversalStats.TotalVisits,
            traversalStats.UniqueNodes,
            traversalStats.DuplicateExpansions,
            orderedOrders.Count,
            loopSw.ElapsedMilliseconds);

        // BOMNO 分桶命中率：`按BOMNO` 是正常路径；`锚点BOMNO无行`/`无锚点` 走的是全部边并集（退化口径，留痕）。
        if (structuresByBomNo is not null || bomNoByOrder is not null)
        {
            _logger.LogInformation(
                "[Pegging][BOMNO分桶] 按订单 BOMNO 展开={PerBom} 单 / 锚点 BOMNO 在本批无行（退化并集）={Fallback} 单 / " +
                "无 BOM 锚点（退化并集）={NoAnchor} 单（共 {Orders} 单，PlanVersionId={PlanVersionId}）",
                perBomNoOrders, bomNoFallbackOrders, noAnchorOrders, orderedOrders.Count, request.PlanVersionId);
        }

        // ── T2-02 承接汇总（诊断，可删）：按供给类型看需求到底被谁承接 ──
        // 用途：判「PI Supply 入池」是否真的在承接需求，以及有多少塌成 NEW_REQUIREMENT（重复生产）。
        // Σ 需求（voucher.DemandQuantity）与 Σ 承接 + 短缺 应闭合。
        _logger.LogInformation(
            "[Pegging][T2-02] 承接汇总: {BySrc}; Σ需求={DemandQty}, 短缺={ShortageQty}, LPD={Lpd}",
            string.Join(", ", voucher.SupplyAllocations
                .GroupBy(a => a.SourceType)
                .OrderBy(g => g.Key)
                .Select(g => $"{g.Key}={g.Sum(x => x.AllocatedQuantity):0.####}({g.Count()}笔)")),
            voucher.DemandQuantity, voucher.ShortageQuantity, voucher.LogicalProductionDemands.Count);

        // T2-02 口径诊断：PI 承接按 BOM 层级拆分——顶层（BomLevel=0，即 [Order] 各订单行本身）
        // vs 下阶（BomLevel>0，BOM 展开出的子件需求）。用来判「PI 到底被谁承接了」。
        var wipAllocs = voucher.SupplyAllocations
            .Where(a => a.SourceType == Core.Enum.SupplySourceType.WIP).ToList();
        _logger.LogInformation(
            "[Pegging][T2-02] PI 承接分层: 顶层(L0)={L0Qty}({L0N}笔) 下阶(L1+)={L1Qty}({L1N}笔)",
            wipAllocs.Where(a => a.BomLevel == 0).Sum(a => a.AllocatedQuantity),
            wipAllocs.Count(a => a.BomLevel == 0),
            wipAllocs.Where(a => a.BomLevel > 0).Sum(a => a.AllocatedQuantity),
            wipAllocs.Count(a => a.BomLevel > 0));

        // ── 需求侧 PI 身份诊断（2026-10-09 整改 R-18/C-Q1.2）──
        // 判「根订单 PI 号是否还在被无条件传给 BOM 子件」：LPD 上的 PI 号必须**只**出现在
        // 「需求物料 == 该 PI 所属订单物料」的需求上。带 PI 的 LPD 若其物料是下阶子件，即为串味残留。
        _logger.LogInformation(
            "[Pegging][T2-02] 需求侧PI身份: LPD={Lpd} 带PI={WithPi} 无PI={NoPi}(=规划缺口) 去重PI={DistinctPi}",
            voucher.LogicalProductionDemands.Count,
            voucher.LogicalProductionDemands.Count(d => !string.IsNullOrEmpty(d.ProductionInstructionNo)),
            voucher.LogicalProductionDemands.Count(d => string.IsNullOrEmpty(d.ProductionInstructionNo)),
            voucher.LogicalProductionDemands
                .Where(d => !string.IsNullOrEmpty(d.ProductionInstructionNo))
                .Select(d => d.ProductionInstructionNo!)
                .Distinct(StringComparer.Ordinal).Count());

        voucher.IsFullyAllocated = voucher.ShortageQuantity == 0;
        voucher.ExecutionTimeMs = loopSw.ElapsedMilliseconds;
        return voucher;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 阶段2 S2.3 双跑对照（不切主链）：旧 DFS vs 新 BFS，产出 PM 0918-3 §八 7 项 + §六 I1-I4 报告
    // ─────────────────────────────────────────────────────────────────────────

    /// <inheritdoc cref="IPeggingOrchestrator.RunDualCompareAsync" />
    public async System.Threading.Tasks.Task<DualRunReport> RunDualCompareAsync(
        PeggingExecutionRequest request,
        CancellationToken ct = default)
    {
        // 同输入：订单 / DemandSequence / BOM 快照 / 冻结策略快照 / 供给池（含硬锁）
        var orders = await LoadOrdersForPeggingAsync(request, ct);
        var demandSequenceByOrder = await BuildDemandSequenceMapAsync(orders, request, ct);
        var bomSnapshot = await LoadBomSnapshotAsync(request.PlanVersionId, ct);

        var strategyProfileVersionId = request.SchedulingContext?.StrategyProfileVersionId;
        if (!strategyProfileVersionId.HasValue || strategyProfileVersionId.Value <= 0)
            throw new InvalidOperationException(
                "双跑策略上下文不完整：SchedulingContext.StrategyProfileVersionId 为空。正式运行必须有冻结策略版本，禁止静默回退。");
        var frozenSnapshot = await _frozenStrategySnapshotProvider
            .GetFrozenStrategySnapshotAsync(strategyProfileVersionId.Value, ct);

        // `persistPositionSnapshot: false` —— 双跑对照 / BFS 单跑都是**探针路径**（无生产调用方），
        // 只校验不落库，避免覆盖夜间 run 的 PI Position 快照（并省掉 11.8 万行逐行 INSERT 的往返）。
        var (supplyPool, _) = await LoadSupplyPoolAsync(request, frozenSnapshot, bomSnapshot, ct,
            persistPositionSnapshot: false);
        var (bomStructure, structuresByBomNo) = ToBomStructures(bomSnapshot);

        // 与主链同口径：按订单 ResolvedBOMNO 取边（对照才有意义）
        var bomAnchors = await LoadBomAnchorsAsync(request.PlanVersionId, ct);
        var bomNoByOrder = bomAnchors.ToDictionary(kv => kv.Key, kv => kv.Value.ResolvedBOMNO);

        // 旧 DFS / 新 BFS 各自独立供给账本（Clone 隔离），记录耗时与展开统计
        var oldSw = Stopwatch.StartNew();
        var oldVoucher = RunDfsLoop(orders, demandSequenceByOrder, request, bomSnapshot, supplyPool, ct, out var oldStats);
        oldSw.Stop();

        var newSw = Stopwatch.StartNew();
        var newVoucher = RunBfsLoop(orders, demandSequenceByOrder, request, bomSnapshot, bomStructure, supplyPool.Clone(), ct, out var newStats,
            structuresByBomNo, bomNoByOrder);
        newSw.Stop();

        var report = ComparePeggingVouchers(oldVoucher, newVoucher, request, orders.Count) with
        {
            OldTraversalVisits = oldStats.TotalVisits,
            NewTraversalVisits = newStats.TotalVisits,
            OldMs              = oldSw.ElapsedMilliseconds,
            NewMs              = newSw.ElapsedMilliseconds
        };

        LogDualRunReport(report);
        return report;
    }

    /// <summary>
    /// 主链 BFS 单跑（只读，不落库不 Solver）：仅跑 RunBfsLoop，验证新 BFS 展开耗时 + I1/I2 红线校验（ValidatePeggingResult）。
    /// 用户 2026-09-18 裁决弃旧 DFS（真实 BOM 上 10 单/20 分钟不收敛）、主链切 BFS 后，作为快速验收入口。
    /// </summary>
    public async System.Threading.Tasks.Task<BfsRunReport> RunBfsOnlyAsync(
        PeggingExecutionRequest request,
        CancellationToken ct = default)
    {
        var orders = await LoadOrdersForPeggingAsync(request, ct);
        var demandSequenceByOrder = await BuildDemandSequenceMapAsync(orders, request, ct);
        var bomSnapshot = await LoadBomSnapshotAsync(request.PlanVersionId, ct);

        var strategyProfileVersionId = request.SchedulingContext?.StrategyProfileVersionId;
        if (!strategyProfileVersionId.HasValue || strategyProfileVersionId.Value <= 0)
            throw new InvalidOperationException(
                "BFS 单跑策略上下文不完整：SchedulingContext.StrategyProfileVersionId 为空。正式运行必须有冻结策略版本，禁止静默回退。");
        var frozenSnapshot = await _frozenStrategySnapshotProvider
            .GetFrozenStrategySnapshotAsync(strategyProfileVersionId.Value, ct);

        // `persistPositionSnapshot: false` —— 双跑对照 / BFS 单跑都是**探针路径**（无生产调用方），
        // 只校验不落库，避免覆盖夜间 run 的 PI Position 快照（并省掉 11.8 万行逐行 INSERT 的往返）。
        var (supplyPool, _) = await LoadSupplyPoolAsync(request, frozenSnapshot, bomSnapshot, ct,
            persistPositionSnapshot: false);
        var (bomStructure, structuresByBomNo) = ToBomStructures(bomSnapshot);

        // 与主链同口径：按订单 ResolvedBOMNO 取边
        var bomAnchors = await LoadBomAnchorsAsync(request.PlanVersionId, ct);
        var bomNoByOrder = bomAnchors.ToDictionary(kv => kv.Key, kv => kv.Value.ResolvedBOMNO);

        var sw = Stopwatch.StartNew();
        var voucher = RunBfsLoop(orders, demandSequenceByOrder, request, bomSnapshot, bomStructure, supplyPool, ct, out var stats,
            structuresByBomNo, bomNoByOrder);
        sw.Stop();

        var redLineErrors = ValidatePeggingResult(supplyPool, voucher);

        return new BfsRunReport
        {
            PlanVersionId       = request.PlanVersionId,
            OrderCount          = orders.Count,
            RedLineErrors       = redLineErrors,
            LpdCount            = voucher.LogicalProductionDemands.Count,
            AllocationCount     = voucher.SupplyAllocations.Count,
            LineageCount        = voucher.MaterialRequirementLinks.Count,
            DemandQuantity      = voucher.DemandQuantity,
            ShortageQuantity    = voucher.ShortageQuantity,
            TraversalVisits     = stats.TotalVisits,
            UniqueNodes         = stats.UniqueNodes,
            DuplicateExpansions = stats.DuplicateExpansions,
            ElapsedMs           = sw.ElapsedMilliseconds
        };
    }

    /// <summary>双跑逐维比对（纯函数）：I1-I3 + Δ1-Δ3。I4 恒成立；Δ4 与 Task 影响由 DeferredToPosition1 标注待 1号位。</summary>
    private static DualRunReport ComparePeggingVouchers(
        PeggingResultVoucher oldVoucher,
        PeggingResultVoucher newVoucher,
        PeggingExecutionRequest request,
        int orderCount)
    {
        // I1：NEW_REQUIREMENT 按 DemandKey 聚合净产出（旧碎片 N 笔、新 1 笔，聚合后总量应相等）
        var oldNet = AggregateByDemandKey(oldVoucher, SupplySourceType.NEW_REQUIREMENT);
        var newNet = AggregateByDemandKey(newVoucher, SupplySourceType.NEW_REQUIREMENT);
        var netDiffs = DiffMaps(oldNet, newNet);

        // I2：真实供给（非 NEW_REQUIREMENT / 规划占位）按物理供给身份聚合
        var oldSupply = AggregateByPhysicalSupply(oldVoucher);
        var newSupply = AggregateByPhysicalSupply(newVoucher);
        var supplyDiffs = DiffMaps(oldSupply, newSupply);

        // I2-PI：PI 承接（WIP + PRODUCTION_INSTRUCTION）
        var oldPi = AggregateByPhysicalSupply(oldVoucher, SupplySourceType.WIP, SupplySourceType.PRODUCTION_INSTRUCTION);
        var newPi = AggregateByPhysicalSupply(newVoucher, SupplySourceType.WIP, SupplySourceType.PRODUCTION_INSTRUCTION);
        var piDiffs = DiffMaps(oldPi, newPi);

        // I3：血缘边集 (Consumer, Producer, RequiredQty) 多集比对
        var lineageMismatches = CompareLineage(oldVoucher, newVoucher);

        return new DualRunReport
        {
            PlanVersionId      = request.PlanVersionId,
            OrderCount         = orderCount,
            NetOutputClosed    = netDiffs.Count == 0,
            NetOutputDiffs     = netDiffs,
            SupplyClosed       = supplyDiffs.Count == 0,
            SupplyDiffs        = supplyDiffs,
            PiClosed           = piDiffs.Count == 0,
            PiDiffs            = piDiffs,
            LineageClosed      = lineageMismatches.Count == 0,
            LineageMismatches  = lineageMismatches,
            OldLpdCount        = oldVoucher.LogicalProductionDemands.Count,
            NewLpdCount        = newVoucher.LogicalProductionDemands.Count,
            OldAllocationCount = oldVoucher.SupplyAllocations.Count,
            NewAllocationCount = newVoucher.SupplyAllocations.Count,
            OldDemandQuantity  = oldVoucher.DemandQuantity,
            NewDemandQuantity  = newVoucher.DemandQuantity,
            DeferredToPosition1 = new[]
            {
                "AllocationTaskShare 差异：1号位 PhaseFive 派生，2号位 Pegging 段无产物，待排程后对比",
                "Continuation 差异：1号位 排程后份额切片，2号位 Pegging 段无产物，待排程后对比",
                "Task 数量与资源负荷影响分析：PM 0918-3 §风险2 已判属 1号位 阶段",
                "I4 PI Position 最终一致性：2号位未触碰装载，待 1号位 bitmap 级确认"
            }
        };
    }

    private static IReadOnlyDictionary<string, decimal> AggregateByDemandKey(
        PeggingResultVoucher voucher, params SupplySourceType[] sourceTypes)
    {
        var types = sourceTypes.ToHashSet();
        return voucher.SupplyAllocations
            .Where(a => types.Contains(a.SourceType))
            .GroupBy(a => a.DemandKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.AllocatedQuantity), StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, decimal> AggregateByPhysicalSupply(
        PeggingResultVoucher voucher, params SupplySourceType[] sourceTypes)
    {
        var types = sourceTypes.Length == 0 ? null : sourceTypes.ToHashSet();
        return voucher.SupplyAllocations
            .Where(a => a.SourceType != SupplySourceType.NEW_REQUIREMENT
                     && a.SourceType != SupplySourceType.PLANNING_PURCHASE_PLACEHOLDER
                     && (types == null || types.Contains(a.SourceType)))
            .GroupBy(a => PhysicalSupplyKey(a), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.AllocatedQuantity), StringComparer.Ordinal);
    }

    private static string PhysicalSupplyKey(Core.Dto.SupplyAllocationItem a)
        => $"{a.SourceType}|{a.SupplyMaterialId}|{a.FactoryCode}|{a.SupplySourceId?.ToString() ?? "-"}";

    private static IReadOnlyList<DualRunDiff> DiffMaps(
        IReadOnlyDictionary<string, decimal> oldMap,
        IReadOnlyDictionary<string, decimal> newMap)
    {
        var diffs = new List<DualRunDiff>();
        foreach (var kv in oldMap)
        {
            var newQty = newMap.TryGetValue(kv.Key, out var n) ? n : 0m;
            if (kv.Value != newQty)
                diffs.Add(new DualRunDiff(kv.Key, kv.Value, newQty));
        }
        foreach (var kv in newMap)
            if (!oldMap.ContainsKey(kv.Key))
                diffs.Add(new DualRunDiff(kv.Key, 0m, kv.Value));
        return diffs;
    }

    private static IReadOnlyList<string> CompareLineage(
        PeggingResultVoucher oldVoucher,
        PeggingResultVoucher newVoucher)
    {
        var oldEdges = LineageMultiset(oldVoucher);
        var newEdges = LineageMultiset(newVoucher);
        var mismatches = new List<string>();
        foreach (var (key, count) in oldEdges)
        {
            var newCount = newEdges.TryGetValue(key, out var n) ? n : 0;
            if (count != newCount)
                mismatches.Add($"边({key})旧={count}新={newCount}");
        }
        foreach (var key in newEdges.Keys)
            if (!oldEdges.ContainsKey(key))
                mismatches.Add($"边({key})仅新侧存在");
        return mismatches;
    }

    /// <summary>血缘边多集：键 = ConsumerMaterialId|ProducerMaterialId|RequiredQty，值 = 出现次数（供同边重复折叠比对）。</summary>
    private static IReadOnlyDictionary<string, int> LineageMultiset(PeggingResultVoucher voucher)
        => voucher.MaterialRequirementLinks
            .GroupBy(l => $"{l.ConsumerMaterialId}|{l.ProducerMaterialId}|{l.RequiredQty}", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    private void LogDualRunReport(DualRunReport r)
    {
        _logger.LogInformation(
            "[Pegging][双跑] PlanVersionId={Pv} 订单={Orders} I1净产出={I1} I2供给={I2} PI承接={Pi} I3血缘={I3} I4_PIPos={I4} PASS={Pass}",
            r.PlanVersionId, r.OrderCount, r.NetOutputClosed, r.SupplyClosed, r.PiClosed, r.LineageClosed, r.PiPositionClosed, r.Pass);
        _logger.LogInformation(
            "[Pegging][双跑] Δ1 LPD {OldLpd}->{NewLpd} | Δ2 Allocation笔数 {OldAlloc}->{NewAlloc} | Δ3 DemandQuantity {OldDq}->{NewDq} | 重复展开 {OldVisits}->{NewVisits} | 耗时 {OldMs}ms->{NewMs}ms",
            r.OldLpdCount, r.NewLpdCount, r.OldAllocationCount, r.NewAllocationCount,
            r.OldDemandQuantity, r.NewDemandQuantity, r.OldTraversalVisits, r.NewTraversalVisits, r.OldMs, r.NewMs);
        foreach (var d in r.NetOutputDiffs)
            _logger.LogWarning("[Pegging][双跑][I1差异] {Key} 旧={Old} 新={New}", d.Key, d.OldQty, d.NewQty);
        foreach (var d in r.SupplyDiffs)
            _logger.LogWarning("[Pegging][双跑][I2差异] {Key} 旧={Old} 新={New}", d.Key, d.OldQty, d.NewQty);
        foreach (var d in r.PiDiffs)
            _logger.LogWarning("[Pegging][双跑][PI差异] {Key} 旧={Old} 新={New}", d.Key, d.OldQty, d.NewQty);
        foreach (var m in r.LineageMismatches)
            _logger.LogWarning("[Pegging][双跑][I3差异] {Msg}", m);
    }

    /// <summary>
    /// 将订单转换为 UpstreamDemand，消费3号位 DemandPriorityConfig 排序，返回 OrderId → DemandSequence 映射。
    /// </summary>
    private async Task<Dictionary<long, int>> BuildDemandSequenceMapAsync(
        IReadOnlyList<OrderPeggingRow> orders,
        PeggingExecutionRequest request,
        CancellationToken ct)
    {
        // 4.1：策略版本必须取自本 Run 冻结上下文，不再固定传 0；缺失即视为运行上下文不完整，禁止静默回退 Fixture
        var strategyProfileVersionId = request.SchedulingContext?.StrategyProfileVersionId;
        if (!strategyProfileVersionId.HasValue || strategyProfileVersionId.Value <= 0)
        {
            throw new InvalidOperationException(
                "DemandPriority 策略上下文不完整：SchedulingContext.StrategyProfileVersionId 为空。正式运行必须有冻结策略版本，禁止静默回退 Fixture。");
        }

        var config = await _demandPriorityConfigProvider.GetPriorityConfigAsync(strategyProfileVersionId.Value, ct);

        // S5（PM 0923）：EXPEDITE 身份 = ScopeJsonV2.OrderTargets.OrderCanonicalId，仅 PriorityMode=EXPEDITE 时生效；
        // 作为「可调整 Demand 排序前置竞争层」传给 Executor（V1 全量重排：expedite 需求整体前置，层内仍按冻结策略竞争）。
        var expediteOrderCanonicalIds = await LoadExpediteOrderCanonicalIdsAsync(request, ct);

        // ── 分轮排序（B1，2026-10-20）────────────────────────────────────────────
        // 0号位 M2：同一个排序器按轮多次执行，不新增第二套引擎。
        // 轮次只决定 DemandSequence 的**赋值先后**：每轮跑完接着上一轮的 offset 递增，
        // 所以 DemandSequence 仍是**全局唯一递增**的一个键 ⇒ RunBfsLoop 不用改，1号位 零改动。
        //
        // 三条口径：
        //   ① EXPEDITE 只在本轮内前置 —— 只喂给第 1 轮，不带进后续轮（否则插单会跨轮置顶）；
        //   ② 某轮无匹配段 ⇒ 执行器自带「DemandKey ASC 兜底 + WARNING」，不并入前一轮；
        //   ③ 供给池是一个共用池，按轮顺序消耗（池在 BuildSolveRequestAsync 装载一次，这里不碰）。
        var rounds = orders
            .Select(o => o.CompetitionRound)
            .Distinct()
            .OrderBy(r => r)
            .ToList();

        var map = new Dictionary<long, int>();
        var offset = 0;

        foreach (var round in rounds)
        {
            var roundOrders = orders.Where(o => o.CompetitionRound == round).ToList();
            if (roundOrders.Count == 0)
                continue;

            var roundDemands = roundOrders.Select(o => new UpstreamDemand
            {
                DemandKey    = o.OrderId.ToString(),
                OrderCanonicalId = o.OrderCanonicalId,
                OrderType    = o.OrderType,
                CustomerTier = o.CustomerTier,
                DueDate      = o.DueDate,
                IssueDate    = o.IssueDate,
                DelayStatus  = o.DelayStatus,   // 01 v1.13 :536 / Pegging v1.6 :257 需求排序可用条件
                // ProtectionStatus：全库无该列（sys.columns 查无），保持 null（不造假）。
                SourceDemand = o
            }).ToList();

            var roundConfig = new DemandPriorityConfig
            {
                Segments = config.Segments
                    .Where(s => s.CompetitionRound == round)
                    .ToList()
            };

            if (roundConfig.Segments.Count == 0)
            {
                // 不静默并入前一轮：执行器会按 DemandKey ASC 兜底并自己记 WARNING，这里再留一条带轮次的。
                _logger.LogWarning(
                    "[Pegging] 竞争轮次 {Round} 在冻结 DemandPriority 快照中没有匹配段（共 {Count} 条需求），按 DemandKey ASC 兜底排序",
                    round, roundDemands.Count);
            }

            var roundExpedite = round == DemandPriorityProjector.DefaultCompetitionRound
                ? expediteOrderCanonicalIds
                : null;

            var sorted = _demandPriorityExecutor.ExecutePrioritySort(roundDemands, roundConfig, roundExpedite);

            foreach (var demand in sorted)
            {
                if (long.TryParse(demand.DemandKey, out var orderId))
                {
                    // 不用 demand.DemandSequence：那个序号在每轮内从 1 重新开始，
                    // 直接取会把不同轮次的订单撞到同一个序号。这里统一按全局 offset 重发。
                    map[orderId] = ++offset;
                }
            }
        }

        return map;
    }

    /// <summary>
    /// S5（PM 0923）：解析本 Run 的 EXPEDITE 目标订单集合。
    /// 仅当 ScopeJsonV2.PriorityMode == EXPEDITE 且 OrderTargets 非空时，返回 OrderCanonicalId 去重集合；否则空集。
    /// </summary>
    private async Task<IReadOnlySet<long>> LoadExpediteOrderCanonicalIdsAsync(
        PeggingExecutionRequest request,
        CancellationToken ct)
    {
        var scheduleRunId = request.SchedulingContext?.ScheduleRunId;
        if (scheduleRunId == null || scheduleRunId <= 0)
            return new HashSet<long>();

        var scopeJson = await _connectionManager.QueryFirstOrDefaultAsync<string>(
            "SELECT ScopeJson FROM ScheduleRun WHERE Id = @Id",
            new { Id = scheduleRunId.Value }, db: DatabaseId.APS);

        if (string.IsNullOrWhiteSpace(scopeJson))
            return new HashSet<long>();

        var scope = JsonSerializer.Deserialize<ScopeJsonV2>(scopeJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        if (scope == null || scope.PriorityMode != PriorityMode.Expedite || scope.OrderTargets is not { Count: > 0 })
            return new HashSet<long>();

        return scope.OrderTargets.Select(t => t.OrderCanonicalId).ToHashSet();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // V1.2 原子 Allocation 机制（§5.3）
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 原子分配结果
    /// </summary>
    private sealed class AllocationResult
    {
        public bool Success { get; init; }
        public decimal AllocatedQty { get; init; }
        public long AllocationSequence { get; init; }
        public string? FailureReason { get; init; }
        public AllocationRecord? Record { get; init; }

        public static AllocationResult Succeeded(decimal qty, long seq, AllocationRecord record) =>
            new() { Success = true, AllocatedQty = qty, AllocationSequence = seq, Record = record };

        public static AllocationResult Failed(string reason) =>
            new() { Success = false, FailureReason = reason };
    }

    /// <summary>
    /// V1.2 原子 Allocation 机制（§5.3）：9步原子动作
    ///
    /// 一笔供需Allocation成功时，必须在同一内存动作中完成：
    /// 1. 校验Demand还有余额
    /// 2. 校验Supply还有余额
    /// 3. 校验资格（Eligibility，当前版本暂不实现，待5号位规则引擎接入）
    /// 4. 校验Strict Binding
    /// 5. 校验Demand Protection
    /// 6. 校验Execution不可逆事实
    /// 7. 扣DemandBalance
    /// 8. 扣SupplyBalance
    /// 9. 此时生成AllocationSequence
    /// 10. 生成逻辑Allocation/LedgerEntry
    ///
    /// 任何一步失败：Demand/Supply余额均不得部分修改（通过在所有校验通过后才执行扣减实现原子性）
    /// </summary>
    private static AllocationResult TryAtomicAllocation(
        SupplyLedgerEntry supply,
        DemandBalance demand,
        int bomLevel,
        PeggingResultVoucher voucher,
        decimal requestedQty)
    {
        // ══════════════════════════════════════════════════════════════════════
        // 第一阶段：校验（所有校验必须通过才能进入扣减阶段）
        // ══════════════════════════════════════════════════════════════════════

        // Step 1: 校验Demand还有余额
        if (demand.RemainingQty <= 0m)
            return AllocationResult.Failed("Demand has no remaining balance");

        // Step 2: 校验Supply还有余额
        if (supply.RemainingQty <= 0m)
            return AllocationResult.Failed("Supply has no remaining balance");

        // 计算本次分配数量 = Min(供应余额, 需求余额, 请求数量)
        var allocQty = Math.Min(Math.Min(supply.RemainingQty, demand.RemainingQty), requestedQty);

        if (allocQty <= 0m)
            return AllocationResult.Failed("Calculated allocation quantity is zero");

        // Step 3: 校验资格（Eligibility）
        if (!ValidateEligibility(supply, demand))
            return AllocationResult.Failed($"Eligibility violation: Supply {supply.SupplyKey} (material {supply.MaterialId}) cannot serve demand (material {demand.MaterialId})");

        // Step 4: 校验Strict Binding
        if (!ValidateStrictBinding(supply, demand.CurrentOrderId, demand.DemandKey))
            return AllocationResult.Failed($"Strict Binding violation: Supply {supply.SupplyKey} is locked to another demand");

        // Step 5: 校验Demand Protection —— **数量预留闸门**（R02；0号位 2026-10-25《四份回执联合核证》§二-3）
        //   实体注释 §8.2：`DEMAND_PROTECTION` 是「1对N保护，保护组内需求可用，组外不可用」，
        //   配合 §8 的 `LockedQty` ⇒ 语义是**为受保护需求预留一定数量、其余份额仍可竞争**，
        //   **不是**把整条供给对其它需求全部封死。
        //   旧写法按「身份是否等于锁上需求」二值判定 ⇒ 一个 60 的保护会把整条供给（例如 1000）对其它需求全封，
        //   且 `LockedQty` 从未参与任何数量比较 ⇒ 「看起来锁住 60、实际阻断整条 PI」。
        //   现改为：非受保护需求的可分配上限 = 本供给剩余 − 仍未被受保护需求取走的预留量。
        //   ⚠ 扣掉「已被受保护需求取走」的量，是为避免「预留量」与「已实扣量」双减（同条要求）。
        var protectionCap = ComputeDemandProtectionCap(supply, demand.CurrentOrderId, demand.DemandKey);
        if (protectionCap <= 0m)
            return AllocationResult.Failed(
                $"Demand Protection violation: Supply {supply.SupplyKey} 的剩余量已被需求保护预留占满");
        if (protectionCap < allocQty)
            allocQty = protectionCap;

        // Step 6: 校验Execution不可逆事实（Execution Lock）
        // Execution Lock表示供给已被不可逆地消耗（如已投料、已发货），不得再分配
        var executionLock = supply.Locks.FirstOrDefault(l => l.LockType == LockType.EXECUTION);
        if (executionLock != null)
            return AllocationResult.Failed($"Execution Lock violation: Supply {supply.SupplyKey} has been irreversibly consumed");

        // ══════════════════════════════════════════════════════════════════════
        // 第二阶段：原子扣减（所有校验通过，开始修改状态）
        // ══════════════════════════════════════════════════════════════════════

        // Step 7: 扣DemandBalance（§5.2要求：使用DemandBalance对象维护需求余额）
        demand.RemainingQty -= allocQty;

        // Step 8: 扣SupplyBalance
        supply.RemainingQty -= allocQty;

        // Step 9: 生成AllocationSequence（在扣减成功时生成，符合§5.4要求）
        var allocationSeq = voucher.NextAllocationSequence++;

        // Step 10: 生成逻辑Allocation/LedgerEntry
        var allocationRecord = new AllocationRecord
        {
            AllocationSequence = allocationSeq,
            AllocatedQty = allocQty,
            SupplyKey = supply.SupplyKey,
            SupplyType = supply.SourceType.ToString(),
            DemandKey = demand.DemandKey,
            MaterialId = supply.MaterialId,
            AllocatedAt = DateTime.Now,
            RequiresProduction = supply.SourceType == Core.Enum.SupplySourceType.NEW_REQUIREMENT
        };

        supply.Allocations.Add(allocationRecord);

        // 添加到凭证的SupplyAllocations（用于持久化）
        voucher.SupplyAllocations.Add(new Core.Dto.SupplyAllocationItem
        {
            AllocationSequence = allocationSeq,
            DemandKey = demand.DemandKey,
            DemandQuantity = demand.RequiredQty,
            SupplyMaterialId = supply.MaterialId,
            SupplySourceId = supply.SupplySourceId,
            AllocatedQuantity = allocQty,
            SourceType = supply.SourceType,
            SourceReference = supply.SourceReference,
            // SH 级（INTER_FACTORY_ORDER）分配落出荷指示号，供红线⑤⑥ 校验「同 SH 匹配不串 SH / 份额不重复计量」。
            // ⑤ 比的是「本分配携带的 SH 身份 = 本供给自身的 SH 身份」（即 SourceReference 同一张 SH），
            //    **不是**「同一需求只能对一条 SH」—— 冻结正文允许一个需求由多张 SH 共同承接。
            ShippingInstructionNo = supply.SourceType == Core.Enum.SupplySourceType.INTER_FACTORY_ORDER
                ? supply.PhysicalSourceKey
                : null,
            FactoryCode = supply.FactoryCode,
            BomLevel = bomLevel,
            AvailableAt = supply.AvailableAt,
            Priority = demand.Priority
        });

        // 添加到凭证的LedgerEntries（BOM遍历内存账本，§5.5要求）
        voucher.LedgerEntries.Add(new Core.Dto.PeggingLedgerEntry
        {
            OrderId = demand.CurrentOrderId ?? demand.RootOrderId ?? 0,
            DemandMaterialId = demand.MaterialId,
            DemandQuantity = demand.RequiredQty,
            SupplyMaterialId = supply.MaterialId,
            AllocatedQuantity = allocQty,
            SourceType = supply.SourceType,
            SourceId = supply.SupplySourceId,
            BomLevel = bomLevel,
            FactoryCode = supply.FactoryCode,
            ProductFamilyId = demand.ProductFamilyId,
            IsInFrozenZone = demand.IsInFrozenZone,
            Strategy = Core.Enum.PeggingStrategyType.FIFO,
            AvailableAt = supply.AvailableAt ?? DateTime.Now
        });

        return AllocationResult.Succeeded(allocQty, allocationSeq, allocationRecord);
    }

    /// <summary>
    /// 校验供给资格（§5.3 Step 3，Eligibility）
    ///
    /// 当前实现仅做「数量真相 Owner」身份的防御性红线：供给与需求物料必须一致。
    /// 该不变量在入池筛选已保证，此处是原子分配点的二次保险，防止入池装载逻辑演进后出现物料错配被静默承接。
    ///
    /// 复杂资格规则（客户指定供应商、渠道限制、质量等级、OperationResourceEligibility 事实源）待 5号位规则引擎接入，
    /// 注入点在此方法内扩展，2号位不发明规则语义。
    /// 跨厂 INTER_FACTORY_ORDER 的工厂归属由入池装载层归一（落 ShippingInstructionNo），此处不做工厂相等推导，避免与分域口径冲突。
    /// </summary>
    private static bool ValidateEligibility(SupplyLedgerEntry supply, DemandBalance demand)
    {
        // 红线1：物料身份一致（防御性；正常已在 LoadSupplyPoolAsync/LoadDemandPoolAsync 按 Material 归池）
        if (supply.MaterialId != demand.MaterialId)
            return false;

        // 扩展点（V1验收前）：待 5号位规则引擎接入复杂资格规则，例如：
        //   - 客户指定供应商 / 渠道限制
        //   - 质量等级门槛
        //   - OperationResourceEligibility 事实源（操作-资源/供给-需求资格）
        return true;
    }

    /// <summary>
    /// 校验Strict Binding Lock（§8.1）
    ///
    /// Strict Binding表示供给被严格绑定到特定需求，其他需求不得使用。
    /// 场景：客户指定料、工单专用料、冻结区锁定等。
    /// </summary>
    private static bool ValidateStrictBinding(
        SupplyLedgerEntry supply,
        long? demandOrderId,
        string demandKey)
    {
        var strictLock = supply.Locks.FirstOrDefault(l => l.LockType == LockType.STRICT_BINDING);
        if (strictLock == null)
            return true; // 无Strict Binding锁，校验通过

        // 有Strict Binding锁，必须锁定到当前需求才允许分配
        var isLockedToCurrentDemand =
            (strictLock.LockedToOrderId.HasValue && strictLock.LockedToOrderId == demandOrderId) ||
            (strictLock.LockedToDemandKey == demandKey);

        return isLockedToCurrentDemand;
    }

    /// <summary>
    /// 校验Demand Protection Lock（§8.2）—— 供给侧取用上限。
    ///
    /// Demand Protection表示供给为特定需求**预留一定数量**，其余份额仍可被其它需求竞争。
    /// 场景：优先级保护、产品族保护、客户保护等。
    ///
    /// 与Strict Binding的区别：
    /// - Strict Binding：1对1强绑定，其他需求**完全不可用**（本方法不处理，见 <see cref="ValidateStrictBinding"/>）
    /// - Demand Protection：1对N**数量预留**，受保护需求可取到预留量，组外需求竞争剩余份额
    /// </summary>
    private static decimal ComputeDemandProtectionCap(
        SupplyLedgerEntry supply,
        long? demandOrderId,
        string demandKey)
        => ComputeDemandProtectionCap(
            supply.RemainingQty,
            supply.Locks
                .Where(l => l.LockType == LockType.DEMAND_PROTECTION)
                .Select(l => (l.LockedToDemandKey, l.LockedToOrderId, l.LockedQty))
                .ToList(),
            supply.Allocations.Select(a => ((string?)a.DemandKey, a.AllocatedQty)).ToList(),
            demandOrderId,
            demandKey);

    /// <summary>
    /// <see cref="ComputeDemandProtectionCap(SupplyLedgerEntry, long?, string)"/> 的**纯函数版**
    /// （不依赖私有嵌套类型，可直接单测）。
    ///
    /// 返回当前需求可从本供给取用的上限：
    ///   · `decimal.MaxValue` = 不受本机制限制（无保护锁／当前需求本身受保护／预留已被取够）；
    ///   · `0` = 剩余份额被预留占满，其它需求不可再取；
    ///   · `0 &lt; x &lt; MaxValue` = 其余可竞争份额。
    ///
    /// 【为什么不直接 `RemainingQty − ΣLockedQty`】那样会把「已实扣量」与「预留量」双减：
    ///   受保护需求本轮已从本供给取走 20 时，实际仍待预留的只有 40（预留 60 − 已取 20），
    ///   若仍按 60 扣，其它需求会被多挡 20（0号位 §二-3「避免双减」）。
    /// </summary>
    internal static decimal ComputeDemandProtectionCap(
        decimal remainingQty,
        IReadOnlyList<(string? LockedToDemandKey, long? LockedToOrderId, decimal LockedQty)> protections,
        IReadOnlyList<(string? DemandKey, decimal AllocatedQty)> allocations,
        long? demandOrderId,
        string demandKey)
    {
        if (protections == null || protections.Count == 0) return decimal.MaxValue;

        // 当前需求本身是受保护方 ⇒ 不受本机制限制（它就是那 60）。
        var isProtected = protections.Any(p =>
            (p.LockedToOrderId.HasValue && p.LockedToOrderId == demandOrderId)
            || (!string.IsNullOrWhiteSpace(p.LockedToDemandKey)
                && string.Equals(p.LockedToDemandKey, demandKey, StringComparison.Ordinal)));
        if (isProtected) return decimal.MaxValue;

        var protectedKeys = protections
            .Select(p => p.LockedToDemandKey)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .ToHashSet(StringComparer.Ordinal);

        var reservedTotal = protections.Sum(p => p.LockedQty);
        var alreadyTakenByProtected = (allocations ?? Array.Empty<(string? DemandKey, decimal AllocatedQty)>())
            .Where(a => !string.IsNullOrWhiteSpace(a.DemandKey)
                        && protectedKeys.Contains(a.DemandKey!))
            .Sum(a => a.AllocatedQty);

        var outstandingReserve = reservedTotal - alreadyTakenByProtected;
        if (outstandingReserve <= 0m) return decimal.MaxValue;   // 预留已被受保护需求取够 ⇒ 不再设限

        var available = remainingQty - outstandingReserve;
        return available > 0m ? available : 0m;
    }

    /// <summary>
    /// 构建LogicalProductionDemand（V1.2）
    ///
    /// 将需要生产的AllocationRecord转换成Solver输入
    /// 按PM回复Answer 1规范：一个LogicalProductionDemand对应一个AllocationSequence
    /// </summary>
    private static LogicalProductionDemand BuildLogicalProductionDemand(
        AllocationRecord allocation,
        string demandKey,
        long? orderId,
        string? productionInstructionNo,
        int materialId,
        int factoryId,
        string materialCode,
        decimal planningYieldPercent,
        DateTime requiredTime,
        int demandSequence,
        int bomLevel,
        PeggingResultVoucher voucher)
    {
        // LogicalDemandKey格式：PlanVersion_AllocationSeq
        var logicalDemandKey = $"{voucher.PlanVersionId}_{allocation.AllocationSequence}";

        // StartStageCode 由 Pegging 后 FillStartStageCodes 统一回填（Routing 无入边源结点），此处仍占位空。
        var startStageCode = string.Empty;

        // 数量双口径：
        // - NetOutputQty：净产出数量（扣除损耗后的有效产出）
        // - PlannedProcessQty：计划加工数量（含损耗的实际投入）
        // PM C2-5：PlannedProcessQty = NetOutputQty / yield 反算（PlanningYield，清单27）。
        // 红线：已有 PI Supply 不得按 Yield 再次放大——反算只作用于「需要生产的缺口」，不碰已有供给。
        // 无匹配规则/越界（YieldPercent ∉ (0,100)）按 100 无损回退；四舍五入 4 位对齐 DECIMAL(18,4)。
        var netOutputQty = allocation.AllocatedQty;
        var plannedProcessQty = planningYieldPercent > 0m && planningYieldPercent < 100m
            ? Math.Round(netOutputQty * 100m / planningYieldPercent, 4)
            : netOutputQty;

        return new LogicalProductionDemand
        {
            LogicalDemandKey = logicalDemandKey,
            PlanVersionId = voucher.PlanVersionId,
            DomainKey = voucher.DomainKey,
            AllocationSequence = allocation.AllocationSequence,
            DemandKey = demandKey,
            OrderId = orderId,
            // 角色＝需求在本次 BOM 展开中的位置（J14-03）：bomLevel=0 是本订单顶层根需求（取 ROOT 链），
            //   其余一律作为子件被父件消耗（取 EDGE 链）。**不再**用「该物料有没有供给阈值」反推。
            IsRootDemand = bomLevel == 0,
            MaterialId = materialId,
            FactoryId = factoryId,
            StartStageCode = startStageCode,
            NetOutputQty = netOutputQty,
            PlannedProcessQty = plannedProcessQty,
            RequiredAvailableTime = requiredTime,
            DemandSequence = demandSequence,
            ProductionInstructionNo = productionInstructionNo, // W2：Pegging已确定该需求归属哪张PI（无PI=null）
            IsUnlocated = false // INTEGRATION TODO: 联调占位，V1验收前必须接入5号位PI Position实际计算结果
        };
    }

    /// <summary>
    /// BOM 遍历统计（PM 0918-2 埋点）：量化旧主链「共享子件重复展开」的基线。
    /// 阶段1 只记录 Old 侧基线；阶段2 Explosion/Allocation 解耦后同口径对照「重复展开 → 0」验证整改效果。
    /// 仅统计、不改变任何 Allocation 语义。
    /// </summary>
    private sealed class BomTraversalStats
    {
        private readonly HashSet<string> _uniqueNodeKeys = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _visitsByMaterial = new(StringComparer.Ordinal);

        public long TotalVisits { get; private set; }
        public long UniqueNodes => _uniqueNodeKeys.Count;
        public long DuplicateExpansions => TotalVisits - _uniqueNodeKeys.Count;
        public string? MaxSharedMaterialCode { get; private set; }
        public long MaximumVisits { get; private set; }

        /// <summary>
        /// BOM 子件在 <c>Material</c> 主档中**缺行**的边数（被跳过的下钻次数）。
        /// 见 <see cref="RecordMissingChildMaterial"/>：`LoadBomSnapshotAsync` 的
        /// `ISNULL(mc.Id, 0)` 把「子件码无主档行」静默压成 `ChildMaterialId=0`。
        /// </summary>
        public long MissingChildMaterialEdges { get; private set; }

        /// <summary>缺主档的子件物料码（去重，用于告警取样）。</summary>
        private readonly HashSet<string> _missingChildMaterialCodes = new(StringComparer.Ordinal);
        public IReadOnlyCollection<string> MissingChildMaterialCodes => _missingChildMaterialCodes;

        /// <summary>
        /// 登记一条「子件码在 Material 主档缺行」的 BOM 边（2026-10-09）。
        ///
        /// 背景：`LoadBomSnapshotAsync` 的 `ISNULL(mc.Id, 0) AS ChildMaterialId` 把「APS_BOM_RAW 有子件码、
        /// 但 Material 主档无此码」静默压成 **0**。放行会生成 `MaterialId=0` 的 LogicalProductionDemand
        /// 交给 1号位（实测 PV2：25 条，样例 `mat=0`），并把 `RAW-*` / `WIP-*` 原材料当**自制件**走生产缺口分支。
        ///
        /// ⚠ **主卡口在装载层**（`LoadBomSnapshotAsync` 剔边）：正常情形本计数**恒 0**。
        ///   本方法只作回路内兜底留证——万一将来有别的路径绕过装载层过滤塞进 materialId&lt;=0 的边，
        ///   能立刻从日志看到，而不是静默产出物料不存在的需求。
        /// </summary>
        public void RecordMissingChildMaterial(string childMaterialCode)
        {
            MissingChildMaterialEdges++;
            if (!string.IsNullOrEmpty(childMaterialCode))
                _missingChildMaterialCodes.Add(childMaterialCode);
        }

        public void Record(string materialCode, int factoryId)
        {
            TotalVisits++;
            _uniqueNodeKeys.Add($"{materialCode}|{factoryId}");
            _visitsByMaterial.TryGetValue(materialCode, out var cnt);
            cnt++;
            _visitsByMaterial[materialCode] = cnt;
            if (cnt > MaximumVisits)
            {
                MaximumVisits = cnt;
                MaxSharedMaterialCode = materialCode;
            }
        }
    }

    /// <summary>
    /// 递归 BOM 节点供给扣减。
    /// 贪婪扣减成功 → 记录 SupplyAllocationItem；有短缺（自制件）→ 累加 NEW_REQUIREMENT 虚拟供给并
    /// 生成 LogicalProductionDemand，再递归子节点。子件确有生产缺口时，产出「父需求 → 子需求」
    /// MaterialRequirementLink（consumerLogicalDemandKey 非空即本节点是子件）。
    /// 叶节点短缺计入 voucher.ShortageQuantity（真正无法拆解的缺口）。
    /// visited 集合防止当前遍历路径循环，退出时移除以允许 BOM 中的共用子件。
    /// </summary>
    private static string? TraverseBomNode(
        OrderPeggingRow order,
        string materialCode,
        int materialId,
        int factoryId,
        string factoryCode,
        decimal demandQty,
        int bomLevel,
        int demandSequence,
        BomSnapshot bom,
        SupplyPool supplyPool,
        PeggingResultVoucher voucher,
        HashSet<string> visited,
        string? consumerLogicalDemandKey = null,
        int consumerMaterialId = 0,
        BomTraversalStats? traversalStats = null)
    {
        var nodeKey = SupplyPool.BuildKey(materialCode, factoryId);
        if (!visited.Add(nodeKey)) return null;
        traversalStats?.Record(materialCode, factoryId);

        try
        {
            // 净额 + 承接 + LPD 生成（阶段2 S2.2 抽取）：与旧递归内联逻辑逐字等价，唯「子件递归」留在本方法。
            var outcome = NetAndAllocate(
                order, materialCode, materialId, factoryId, factoryCode,
                demandQty, bomLevel, demandSequence, bom, supplyPool, voucher,
                consumerLogicalDemandKey, consumerMaterialId);

            if (outcome is null) return null; // 无生产（真实供给补足 / 采购件占位 / 生产失败）：不下钻

            // 自制件确有生产 → 子件需求 = 父件净产出 × 配比（等效旧 result.AllocatedQty × edge.Qty）
            var children = bom.ByParent[materialCode].ToList();
            if (children.Count > 0)
            {
                foreach (var edge in children)
                {
                    TraverseBomNode(
                        order,
                        edge.ChildCode,
                        edge.ChildMaterialId,
                        factoryId,
                        factoryCode,
                        outcome.ProducedQty * edge.Qty,
                        bomLevel + 1,
                        demandSequence,
                        bom,
                        supplyPool,
                        voucher,
                        visited,
                        consumerLogicalDemandKey: outcome.ProducerLogicalDemandKey,
                        consumerMaterialId: materialId,
                        traversalStats: traversalStats);
                }
            }

            return null;
        }
        finally
        {
            visited.Remove(nodeKey);
        }
    }

    /// <summary>某节点净额承接的结果：NetOutput（NEW_REQUIREMENT 净产出）与生产 LPD 身份（未生产=null）。</summary>
    private sealed class NetAllocationOutcome
    {
        public required decimal ProducedQty { get; init; }
        public required string? ProducerLogicalDemandKey { get; init; }

        /// <summary>生产 Allocations 的序号（阶段2 S2.3 BFS 建血缘边用，Key=LogicalDemandKey 尾段）。</summary>
        public required long ProducerAllocationSequence { get; init; }
    }

    /// <summary>
    /// 单一净额承接入口（阶段2 S2.2 抽取，PM 0918-3 §六 唯一净额入口）。
    /// 从旧 TraverseBomNode 递归体内原封搬出的：DemandBalance 构建 → 三类供给扣减 → 跨厂 SH → 缺口处理
    /// （采购占位 PLANNING_PURCHASE_PLACEHOLDER / 自制缺口 NEW_REQUIREMENT）+ LPD + 任务喂任务血缘。唯独「子件递归」留在调用方（阶段2 由 BFS 逐层驱动）。
    /// 返回 null = 无生产、不下钻；返回 outcome = 生产成功、ProducedQty 供下级按「父净 × 配比」重算。
    /// </summary>
    private static NetAllocationOutcome? NetAndAllocate(
        OrderPeggingRow order,
        string materialCode,
        int materialId,
        int factoryId,
        string factoryCode,
        decimal demandQty,
        int bomLevel,
        int demandSequence,
        BomSnapshot bom,
        SupplyPool supplyPool,
        PeggingResultVoucher voucher,
        string? consumerLogicalDemandKey,
        int consumerMaterialId)
    {
        var demandKey = $"ORDER_{order.OrderId}_{materialCode}_{factoryId}";

        // 红线① Demand 闭合：累加本节点需求量，供 ValidatePeggingResult 校验
        // 「Σ(已分配) + 短缺 ≤ 需求总量」。跨层级 BOM 展开时总量严格大于叶级分配，属有效上界校验。
        voucher.DemandQuantity += demandQty;

        // §5.2 DemandBalance：构建需求侧内存账本
        var demand = new DemandBalance
        {
            // 【2026-09-29 修复】`RequiredQty` 是 `init` 属性，此前**从未赋值** ⇒ 恒为默认值 0。
            //   它是**不可变的需求节点总量**，与 `RemainingQty`（遍历中递减）不同：后者可扣减，它必须保持初始值。
            //   漏赋的直接后果：`TryAtomicAllocation` 落 `SupplyAllocationItem.DemandQuantity = demand.RequiredQty`
            //   ⇒ `PeggingSupplyAllocation.DemandQty` **全表 0**（实测 PV632：53,651/53,651 行 = 0，而 AllocatedQty 正常）。
            //   注意它**不影响分配结果**（分配只读 RemainingQty），只污染落库追溯列 —— 故两轮真跑均未暴露。
            RequiredQty = demandQty,
            RemainingQty = demandQty,
            MaterialId = materialId,
            MaterialCode = materialCode,
            FactoryId = factoryId,
            FactoryCode = factoryCode,
            DemandType = DemandType.ORDER,
            DemandKey = demandKey,
            RootOrderId = order.OrderId,
            CurrentOrderId = order.OrderId,
            BomLevel = bomLevel,
            DueTime = order.DueDate,
            Priority = demandSequence, // §6 Priority Segment：订单级需求优先级（由 DemandPriorityExecutor 排序结果透传）
            ProductFamilyId = 0, // V1.2：暂不使用产品族
            IsInFrozenZone = false,
            WorksetId = null
        };

        // 供给链选择（PM 2026-08-28 最终裁决 + Pegging 专项 v1.1）：不存在 Inventory/PI/Procurement 三类全局
        // 优先级；先按当前 Demand 业务身份确定允许进入的供给集合，再调用对应类内排序规则。
        //  - 顶层 SALES_ORDER（bomLevel=0）：ERP 已扣成品库存，不再搜普通成品库存（§2.1）。
        //  - 顶层（bomLevel=0）**不消费 PI**：顶层需求本身的生产是**组装工序**，它找不到 PI
        //    （部品出口的出荷指示没有对应的组装指示；出荷指示↔组装指示的挂钩由生管做，APS 不得自造）。
        //    顶层生产需求的 PI 身份 = 该行挂着的 PI（生管挂好的），没有就用订单号（出荷指示号）。
        //    PI 供给池服务的是**下阶（组件）需求**（用户 2026-10-09 口径）。
        //  - 下阶自制件（isPurchased=false）：合资格库存 → PI → 生产缺口。
        //  - 采购件（isPurchased=true）：合资格库存 → 正式采购/在途 → 规划采购占位。
        var isPurchased = bom.IsPurchasedByMaterial.TryGetValue(materialCode, out var purchased) && purchased;
        var includeInventory = bomLevel > 0;
        var includePi = bomLevel > 0;

        // ── 需求侧 PI 身份（PM `PM回复1009-1.md` §五 C-Q1 第 2 项 / §九 R-18，2026-10-09 整改）──
        // 规则：`ProductionInstructionNo` 是**承载该 PI 的那张 PI 所属物料**的物理身份。
        //   只有当「本需求物料 == 承载该 PI 的订单物料」时，该 PI 号才属于本需求。
        //
        // ⛔ 旧写法（已废止）：两处 LPD 构造都无条件传 `order.MTS_InstructionNo`。`order` 是**根订单**，
        //    经 `TraverseBomNode` 逐层原样下传 ⇒ 每个 BOM 子件需求都挂着**成品件**的 PI 号。后果：
        //    ① `BucketContinuityShares` 按 `demand.ProductionInstructionNo` 查 `contextsByPi`
        //       ⇒ 子件需求被切出「挂在**父件物料** MES 执行批上」的 Continuation Slice —— 跨物料串味
        //       （PM C-Q4「不能把父件PI号无条件给子件」、R-18「M2 不得继承 M1 PI/ExistingExecution」）；
        //    ② 该 PI 号继续经 `piByDemandKey` 流入 Task 的 `MTS_InstructionNo`（见本文件 W3 注释，
        //       「无 PI 需求 = null，**不得下发 MES**」）⇒ 子件 Task 会带着成品的 PI 号被当成可下发；
        //    ③ 同一根 PI 的执行上下文被**每个**子件需求重复计入 ⇒ ΣE 虚高（PV2 实测 ΣE=7,424,806，
        //       而 PI 承接总量仅 1,836,894）。
        // 顶层（L0，materialId == order.MaterialId）行为**不变**：该行挂的 PI 就是它自己的 PI
        //   （见上方「顶层生产需求的 PI 身份 = 该行挂着的 PI」口径）。
        // 下阶拿不到「自己物料的 PI」时**保守置 null**（无 PI 需求），不猜、不平均切（PM C-Q4）。
        var demandProductionInstructionNo =
            materialId == order.MaterialId ? order.MTS_InstructionNo : null;

        // 贪婪扣减：使用原子Allocation机制，确保供需扣减、Lock校验、AllocationSequence生成的原子性
        foreach (var entry in supplyPool.GetEntries(materialCode, factoryId, isPurchased, includeInventory, includePi))
        {
            if (demand.RemainingQty <= 0m) break;

            var result = TryAtomicAllocation(
                supply: entry,
                demand: demand,
                bomLevel: bomLevel,
                voucher: voucher,
                requestedQty: demand.RemainingQty);

            if (!result.Success)
            {
                // 原子分配失败（Lock冲突、余额不足等），跳过此供给，尝试下一个
                // 失败原因：result.FailureReason（可在调试时输出）
                continue;
            }

            // 原子分配成功，demand.RemainingQty已在TryAtomicAllocation中扣减

            // R03-02：本 Domain 的合法消耗计入 Run 级账本，后续 Domain 入池时扣减
            supplyPool.RecordPhysicalConsumption(entry.PhysicalSourceKey, result.AllocatedQty);

            // V1.2：判断是否需要生产，生成LogicalProductionDemand
            if (result.Record != null && result.Record.RequiresProduction)
            {
                voucher.LogicalProductionDemands.Add(BuildLogicalProductionDemand(
                    allocation: result.Record,
                    demandKey: demand.DemandKey,
                    orderId: order.OrderId,
                    productionInstructionNo: demandProductionInstructionNo,
                    materialId: materialId,
                    factoryId: factoryId,
                    materialCode: materialCode,
                    planningYieldPercent: supplyPool.PlanningYieldPercent(materialCode),
                    requiredTime: order.DueDate,
                    demandSequence: demandSequence,
                    bomLevel: bomLevel,
                    voucher: voucher));
            }
        }

        // ── 跨厂出荷指示（SH级）绑定消费：三类通用排序扣减后仍短缺时，按 AvailableAt 升序消费 SH 供给 ──
        // SH 保持单一 Supply 身份（冻结规则清单 v1.9 §十六 X-002；Pegging v1.6 §6.3）：Transit/Received 是其
        // 履行状态两段，PhysicalSourceKey=SH No，由红线⑤⑥ 校验「同 SH 匹配不串 SH / 份额不重复计量」。
        // TryAtomicAllocation 落 ShippingInstructionNo=SH No。
        // **本条循环允许一个需求跨多条 SH 取满** —— 冻结正文逐字「第一层普通Demand可由一张或多张SH承接，
        // 普通Demand↔SH允许多对多」（Pegging v1.6 §12.1；业务基线 v1.13 §40.1），故不设「只从一条取」约束。
        if (demand.RemainingQty > 0m)
        {
            foreach (var shEntry in supplyPool.GetInterFactoryEntries(materialCode, factoryId))
            {
                if (demand.RemainingQty <= 0m) break;

                var shResult = TryAtomicAllocation(
                    supply: shEntry,
                    demand: demand,
                    bomLevel: bomLevel,
                    voucher: voucher,
                    requestedQty: demand.RemainingQty);

                if (!shResult.Success)
                {
                    // 原子分配失败（Lock冲突、余额不足等），跳过此 SH 供给，尝试下一个
                    continue;
                }

                // R03-02：SH 供给同样计入跨域账本（PhysicalSourceKey = 出荷指示号）
                supplyPool.RecordPhysicalConsumption(shEntry.PhysicalSourceKey, shResult.AllocatedQty);
            }
        }

        if (demand.RemainingQty <= 0m) return null;

        // V1.2：缺口处理 - 根据IsPurchased区分采购件/自制件
        if (isPurchased)
        {
            // 采购件：生成 Planning-only Purchase Placeholder（§9.3）
            // 特征：仅内存、ESTIMATED、NOT_COMMITTED、不生成采购单、不生成Task、不可作为CTP承诺
            // 不生成LogicalProductionDemand，不触发Task生成
            supplyPool.Add(
                materialCode: materialCode,
                materialId: materialId,
                factoryId: factoryId,
                qty: demand.RemainingQty,
                // P0-07：规划采购占位 AvailableTime = DataCutoffTime + 冻结 DefaultPurchaseLT（不再 DueDate-7 天倒推）
                availableAt: (supplyPool.DataCutoffTime == default ? DateTime.Now : supplyPool.DataCutoffTime)
                    .AddDays(supplyPool.DefaultPurchaseLtDays),
                sourceType: Core.Enum.SupplySourceType.PLANNING_PURCHASE_PLACEHOLDER,
                sourceRef: $"PLANNING_PLACEHOLDER_{voucher.PlanVersionId}_{materialCode}_{Guid.NewGuid():N}",
                factoryCode: factoryCode,
                confidence: SupplyConfidence.ESTIMATED,
                commitment: SupplyCommitment.NOT_COMMITTED);

            voucher.ShortageQuantity += demand.RemainingQty;
            return null;
        }

        // 自制件：生成 NEW_REQUIREMENT（缺口）虚拟供给，通过原子Allocation流程处理
        // 符合§5.3和§5.4要求：必须经过完整的10步原子校验，AllocationSequence在成功时生成

        // 添加虚拟 NEW_REQUIREMENT（缺口）供给到SupplyPool（返回引用直接分配；NEW_REQUIREMENT 属缺口结果，
        // 不参与 GetEntries 三类排序，故不能依赖 .Last() 取回）
        var virtualSupply = supplyPool.Add(
            materialCode: materialCode,
            materialId: materialId,
            factoryId: factoryId,
            qty: demand.RemainingQty,
            availableAt: order.DueDate,
            sourceType: Core.Enum.SupplySourceType.NEW_REQUIREMENT,
            sourceRef: $"NEW_REQ_{voucher.PlanVersionId}_{materialCode}_{Guid.NewGuid():N}",
            factoryCode: factoryCode);
        var productionResult = TryAtomicAllocation(
            supply: virtualSupply,
            demand: demand,
            bomLevel: bomLevel,
            voucher: voucher,
            requestedQty: demand.RemainingQty);

        if (!productionResult.Success)
        {
            voucher.ShortageQuantity += demand.RemainingQty;
            return null;
        }

        string? producerLogicalDemandKey = null;
        if (productionResult.Record != null && productionResult.Record.RequiresProduction)
        {
            producerLogicalDemandKey = $"{voucher.PlanVersionId}_{productionResult.Record.AllocationSequence}";
            voucher.LogicalProductionDemands.Add(BuildLogicalProductionDemand(
                allocation: productionResult.Record,
                demandKey: demand.DemandKey,
                orderId: order.OrderId,
                productionInstructionNo: demandProductionInstructionNo,
                materialId: materialId,
                factoryId: factoryId,
                materialCode: materialCode,
                planningYieldPercent: supplyPool.PlanningYieldPercent(materialCode),
                requiredTime: order.DueDate,
                demandSequence: demandSequence,
                bomLevel: bomLevel,
                voucher: voucher));

            // 任务喂任务血缘（R2 / PM 2026-09-10）：本节点是子件且确有生产缺口时，
            // 产出「父需求 → 子需求」MaterialRequirementLink，1号位据此生成真实 TaskDependency。
            if (!string.IsNullOrEmpty(consumerLogicalDemandKey))
            {
                voucher.MaterialRequirementLinks.Add(new Core.Dto.MaterialRequirementLink
                {
                    ConsumerLogicalDemandKey   = consumerLogicalDemandKey!,
                    ProducerLogicalDemandKey   = producerLogicalDemandKey!,
                    ConsumerMaterialId         = consumerMaterialId,
                    ProducerMaterialId         = materialId,
                    RequiredQty                = demandQty,
                    ProducerAllocationSequence = productionResult.Record.AllocationSequence
                });
            }
        }

        return new NetAllocationOutcome
        {
            ProducedQty = productionResult.AllocatedQty,
            ProducerLogicalDemandKey = producerLogicalDemandKey,
            ProducerAllocationSequence = productionResult.AllocationSequence
        };
    }

    /// <summary>
    /// P0-04：构造 CandidateContext（FULL 返回 null）。
    /// Base 锚点 = request.BasePlanVersionId（3号位创建 Run 时冻结当前 ACTIVE；2号位运行期不得再查「此刻最新 ACTIVE」）。
    /// ChangeSeed = Candidate 重新 Pegging 相对 Base ACTIVE 的真实逻辑生产需求差异集（新增/减少/数量变化），不是新订单 ID。
    /// ExternalDomainResourceBlocks = 编排层已装载的其它 Domain ACTIVE 共享资源占用（§11，Immutable=true）。
    /// </summary>
    private async Task<CandidateContext?> BuildCandidateContextAsync(
        PeggingExecutionRequest request,
        PeggingResultVoucher voucher)
    {
        if (!request.IsCandidate)
            return null;

        var changeSeedKeys = request.BasePlanVersionId.HasValue
            ? await ComputeChangeSeedKeysAsync(request.BasePlanVersionId.Value, voucher)
            : Array.Empty<string>();

        return new CandidateContext
        {
            BasePlanVersionId            = request.BasePlanVersionId,
            ChangeSeedKeys               = changeSeedKeys,
            ExternalDomainResourceBlocks = request.ExternalDomainResourceBlocks ?? Array.Empty<ResourceBlock>()
        };
    }

    /// <summary>
    /// **T-04 锚点载荷**：一次装载同时产出**两条契约**。
    ///   · <see cref="Constraints"/>（**契约①**）= 硬锚点，**锁物理位置** —— Frozen（窗口层＋执行层）与执行事实；
    ///   · <see cref="Protections"/>（**契约②**）= 订单保护事实，**不锁位置** —— Firm。
    /// </summary>
    private sealed record AnchorPayload(
        IReadOnlyList<ExecutionConstraint> Constraints,
        IReadOnlyList<OrderProtection> Protections)
    {
        public static readonly AnchorPayload Empty =
            new(Array.Empty<ExecutionConstraint>(), Array.Empty<OrderProtection>());
    }

    /// <summary>
    /// **T-04**：把「上一 ACTIVE 版本的锚点事实」装载成 1号位 的两条契约。
    ///
    /// 责任链（`PM回复0907.md` §三.1）：上一 ACTIVE 仍有效的 Frozen ⇒ 2号位 继承；§三.2 明令
    /// 「当前 `ExecutionConstraints = Array.Empty&lt;...&gt;()` **不能作为正式V1实现保留**」。
    ///
    /// 资格判定见 <see cref="V1AnchorQualifier"/>（**Frozen 窗口** 或 **执行事实**；Firm **不在**契约①）；
    /// 窗口公式见 <see cref="V1FenceCalculator"/>、参数解析见 <see cref="FenceConfigResolver"/>；
    /// 连接/去重/歧义处理见 <see cref="ExecutionConstraintProjector"/>。
    ///
    /// 【早退两条（与改造前 `Array.Empty` 等价 ⇒ 零回归）】
    ///   ① 非 Candidate；② 无 `BasePlanVersionId`。任何一条不静默：均落日志。
    ///
    /// 【⚠ 范围边界：FULL 侧不在此处】`SchedulingOrchestrator` 的 FULL 路径
    ///   `sourcePlanVersionId = null` ⇒ `BasePlanVersionId = null` ⇒ 本方法恒早退。
    ///   而 `PM回复0908.md` §四/§十八末 与《…业务裁决建议稿 v1.0 20260908》§7.1「**每次** ScheduleRun 运行时
    ///   读取冻结参数快照、计算上一 ACTIVE Task 属于 Frozen/Firm/Movable」覆盖**夜间 FULL**。
    ///   ⇒ 该项属**「跨版本排程连续性」专项**（该建议稿的主题），**不在 T-04 首步激活范围内**；
    ///   本方法已按同一套 <see cref="V1FenceCalculator"/> 落定口径，FULL 侧接线时**直接复用**即可，不需改判据。
    ///
    /// 【⚠ 数据现状】`FenceConfig` 实库 **0 行** ⇒ Fence 参数解析不出 ⇒ 窗口分支一律 Movable
    ///   （**不自造天数**，见 <see cref="V1FenceCalculator"/>）。此情形**显式告警**，不静默。
    /// </summary>
    private async Task<AnchorPayload> BuildAnchorPayloadAsync(
        PeggingExecutionRequest request,
        PeggingResultVoucher voucher)
    {
        if (!request.IsCandidate || !request.BasePlanVersionId.HasValue)
            return AnchorPayload.Empty;

        var basePlanVersionId = request.BasePlanVersionId.Value;

        // FenceBasisTime = 本次 Run 的排程基准时刻。
        // 取值 = PeggingExecutionRequest.SnapshotAt（ScheduleRun 冻结的事实时点，建 Run 处写为
        //   context.DataCutoffTime ?? now），缺省回退 DateTime.Now。
        // ⚠ 只借它的**时刻**当"基准"，**绝不**把它旁边的 FrozenWindowEnd(= now+2h 滑动窗) 当"天数窗"用。
        var fenceBasisTime = request.SnapshotAt == default ? DateTime.Now : request.SnapshotAt;

        var baseRows = (await _connectionManager.QueryAsync<BasePlanAnchorRowDto>(
            @"SELECT t.Id AS TaskId, t.OrderId, t.MaterialId, t.StageCode, t.OperationCode,
                     t.ResourceId, t.PlannedStartTime, t.PlannedEndTime,
                     t.Quantity, t.PlannedProcessQty, t.TaskNo,
                     t.MESWorkOrderNo, t.Status,
                     o.ProductFamilyId, o.FactoryId
              FROM [Task] t
              LEFT JOIN [Order] o ON o.Id = t.OrderId
              WHERE t.PlanVersionId = @BasePlanVersionId",
            new { BasePlanVersionId = basePlanVersionId },
            db: DatabaseId.APS)).ToList();

        if (baseRows.Count == 0)
        {
            _logger.LogInformation(
                "[Pegging][T-04] Base 版本无 Task 行（BasePlanVersionId={Base}）⇒ 两条契约均为空",
                basePlanVersionId);
            return AnchorPayload.Empty;
        }

        // Fence 参数（3号位 治理、2号位 只读）：整表一次读入，再按 (ProductFamilyId, FactoryId) 逐行解析。
        var fenceRows = (await _connectionManager.QueryAsync<FenceConfigRow>(
            @"SELECT Id, ProductFamilyId, FactoryId, ProcessType, FrozenDays, FirmDays, EffectiveFrom, EffectiveTo
              FROM FenceConfig",
            null,
            db: DatabaseId.APS)).ToList();

        var fenceCache = new Dictionary<(int PfId, int? FactoryId), FenceResolution>();
        FenceResolution ResolveFence(int pfId, int? factoryId)
        {
            if (!fenceCache.TryGetValue((pfId, factoryId), out var res))
                fenceCache[(pfId, factoryId)] = res = FenceConfigResolver.Resolve(fenceRows, fenceBasisTime, pfId, factoryId);
            return res;
        }

        if (fenceRows.Count == 0)
        {
            _logger.LogWarning(
                "[Pegging][T-04] FenceConfig **0 行** ⇒ 无 Fence 参数（基准={Basis:yyyy-MM-dd HH:mm}）⇒ " +
                "Frozen/Firm 窗口判定退化为 Movable，本轮只可能产出「执行事实」锚点。" +
                "窗口参数归 3号位 治理，铺数后自动生效（不自造天数）。",
                fenceBasisTime);
        }

        // ── 契约①：硬锚点候选（Frozen 窗口 或 执行事实） ───────────────────────────────
        var candidates = new List<BasePlanAnchorRow>();
        var qualifiedReason = new Dictionary<long, OrderProtectionReason>();
        var bandByTaskId = new Dictionary<long, FenceBand>();

        foreach (var r in baseRows)
        {
            var res = r.OrderId is null
                ? new FenceResolution(null, FenceConfigResolver.StatusScopeNotFound, "Task 无 OrderId ⇒ 无法定位产品族/工厂")
                : ResolveFence(r.ProductFamilyId ?? 0, r.FactoryId);

            var band = V1FenceCalculator.Classify(r.PlannedStartTime ?? fenceBasisTime, res.Parameters);
            bandByTaskId[r.TaskId] = band;

            if (!V1AnchorQualifier.IsHardAnchor(r.MESWorkOrderNo, r.Status, band, out var constraintType, out var reason))
                continue;

            qualifiedReason[r.TaskId] = reason;
            candidates.Add(new BasePlanAnchorRow
            {
                TaskId            = r.TaskId,
                OrderId           = r.OrderId,
                MaterialId        = r.MaterialId,
                StageCode         = r.StageCode,
                OperationCode     = r.OperationCode,
                ResourceId        = r.ResourceId,
                PlannedStartTime  = r.PlannedStartTime,
                PlannedEndTime    = r.PlannedEndTime,
                Quantity          = r.Quantity,
                PlannedProcessQty = r.PlannedProcessQty,
                TaskNo            = r.TaskNo,
                MESWorkOrderNo    = r.MESWorkOrderNo,
                Status            = r.Status,
                ConstraintType    = constraintType
            });
        }

        var projection = ExecutionConstraintProjector.Project(voucher.LogicalProductionDemands, candidates);

        _logger.LogInformation(
            "[Pegging][T-04] 契约① ExecutionConstraints 投影: Base={Base}, 基座行={Rows}, 合格={Qualified}, 产出={Out} " +
            "(未命中={Unmatched}, 歧义={Ambiguous}, 撞键={Duplicate}, 不完整={Incomplete})",
            basePlanVersionId, baseRows.Count, candidates.Count, projection.Constraints.Count,
            projection.UnmatchedCount, projection.AmbiguousCount, projection.DuplicateKeyCount, projection.IncompleteRowCount);

        foreach (var d in projection.Diagnostics.Take(50))
            _logger.LogWarning("[Pegging][T-04] 锚点未产出 TaskId={TaskId} 原因={Reason}: {Detail}", d.TaskId, d.Reason, d.Detail);

        // ── 契约②：订单保护事实（Firm / Frozen；**不锁位置**，只通报） ──────────────────
        var protections = BuildOrderProtections(
            baseRows, bandByTaskId, qualifiedReason, voucher.LogicalProductionDemands);

        _logger.LogInformation(
            "[Pegging][T-04] 契约② OrderProtections: 产出={Out}（Firm={Firm}, Frozen={Frozen}）",
            protections.Count,
            protections.Count(p => p.ProtectionType == OrderProtectionType.Firm),
            protections.Count(p => p.ProtectionType == OrderProtectionType.Frozen));

        return new AnchorPayload(projection.Constraints, protections);
    }

    /// <summary>
    /// **契约②：订单级保护事实**（`PM回复0923-1.md` §七「识别在**订单级**、Task 继承」）。
    ///
    /// 【口径】逐订单取其上一 ACTIVE 全部 `[Task]` 的**最早 `PlannedStartTime`** 判 Fence 带
    ///   （最早者落在 Frozen ⇒ 该单在窗口中最靠前，整单即 Frozen）；执行事实**覆盖**窗口判定
    ///   （§20「`Execution &gt; Frozen &gt; Firm &gt; Movable`」；`PM回复0923-1` §三.3 亦把「已经下发」
    ///   列入 Frozen 的成因）。
    ///
    /// 【只投"有保护"的】（收敛回执 §五：`ProtectionType = None` 不投影）
    /// 【只投本次 Run 仍在的需求订单】1号位 按 `OrderId` 在自己的内存需求集里找；投本次求解之外的单是噪声。
    /// 【投结论、不投判据】1号位 纯内存、不持有 Base 旧计划 ⇒ 窗口判定一律在 2号位 算完
    ///   （`APS_V1_白天候选各号位配合事项_1号位致2号位_回执_v1.0_20260924` §三 预提2）。
    /// </summary>
    private static IReadOnlyList<OrderProtection> BuildOrderProtections(
        IReadOnlyList<BasePlanAnchorRowDto> baseRows,
        IReadOnlyDictionary<long, FenceBand> bandByTaskId,
        IReadOnlyDictionary<long, OrderProtectionReason> qualifiedReason,
        IReadOnlyList<LogicalProductionDemand> demands)
    {
        var liveOrderIds = demands
            .Where(d => d.OrderId.HasValue)
            .Select(d => d.OrderId!.Value)
            .ToHashSet();

        if (liveOrderIds.Count == 0)
            return Array.Empty<OrderProtection>();

        var protections = new List<OrderProtection>();

        foreach (var g in baseRows
                     .Where(r => r.OrderId.HasValue && liveOrderIds.Contains(r.OrderId!.Value))
                     // 已取消行是终态、非事实 ⇒ 不参与带位判定（否则一张取消的 Frozen 窗内 Task 会把整单标成 Frozen）。
                     .Where(r => !V1AnchorQualifier.IsCancelled(r.Status))
                     .GroupBy(r => r.OrderId!.Value))
        {
            // 执行事实优先（覆盖窗口判定）：已开工/已完成 > 已下发 > Frozen 窗 > Firm 窗。
            var reason = OrderProtectionReason.None;
            if (g.Any(r => qualifiedReason.TryGetValue(r.TaskId, out var q) && q == OrderProtectionReason.IN_EXECUTION))
                reason = OrderProtectionReason.IN_EXECUTION;
            else if (g.Any(r => qualifiedReason.TryGetValue(r.TaskId, out var q) && q == OrderProtectionReason.MES_DISPATCHED))
                reason = OrderProtectionReason.MES_DISPATCHED;
            else if (g.Any(r => bandByTaskId.TryGetValue(r.TaskId, out var b) && b == FenceBand.Frozen))
                reason = OrderProtectionReason.FROZEN_WINDOW;
            else if (g.Any(r => bandByTaskId.TryGetValue(r.TaskId, out var b) && b == FenceBand.Firm))
                reason = OrderProtectionReason.FIRM_WINDOW;

            var type = reason switch
            {
                OrderProtectionReason.IN_EXECUTION  => OrderProtectionType.Frozen,
                OrderProtectionReason.MES_DISPATCHED => OrderProtectionType.Frozen,  // §三.3「已经下发」属 Frozen 成因
                OrderProtectionReason.FROZEN_WINDOW => OrderProtectionType.Frozen,
                OrderProtectionReason.FIRM_WINDOW   => OrderProtectionType.Firm,
                _                                   => OrderProtectionType.None
            };

            // `None` 不投影（收敛回执 §五）。
            if (type == OrderProtectionType.None)
                continue;

            protections.Add(new OrderProtection
            {
                OrderId          = g.Key,
                ProtectionType   = type,
                ProtectionReason = reason
            });
        }

        // 稳定输出顺序（诊断/测试可比对；不影响语义）。
        return protections.OrderBy(p => p.OrderId).ToList();
    }

    /// <summary>`[Task]` 锚点候选行（Dapper 映射用；可写属性，避免 init-only 映射歧义）。</summary>
    private sealed class BasePlanAnchorRowDto
    {
        public long TaskId { get; set; }
        public long? OrderId { get; set; }
        public int MaterialId { get; set; }
        public string? StageCode { get; set; }
        public string? OperationCode { get; set; }
        public int? ResourceId { get; set; }
        public DateTime? PlannedStartTime { get; set; }
        public DateTime? PlannedEndTime { get; set; }
        public decimal Quantity { get; set; }
        public decimal? PlannedProcessQty { get; set; }
        public string? TaskNo { get; set; }
        public string? MESWorkOrderNo { get; set; }
        public string? Status { get; set; }

        /// <summary>`[Order].ProductFamilyId` —— `FenceConfig` 作用域轴之一。`OrderId` 为空时为 null。</summary>
        public int? ProductFamilyId { get; set; }

        /// <summary>`[Order].FactoryId` —— `FenceConfig` 作用域轴之一（NULL 行 = 全厂通配）。</summary>
        public int? FactoryId { get; set; }
    }

    /// <summary>
    /// P0-04 §2.2：ChangeSeed = Candidate Pegging 相对 Base ACTIVE 的差异集。
    /// Base 侧事实源 = AllocationTaskShare（全库唯一持久化 DemandKey 的分配分享表），按 DemandKey 聚合 ShareQty；
    /// Candidate 侧 = 本次 voucher.SupplyAllocations（含 NEW_REQUIREMENT 缺口）按 DemandKey 聚合 AllocatedQuantity。
    /// 差异判定：新增（候选有基无）/减少（基有候选无）/数量变化（|Δ| > 1e-4）。键值为 DemandKey，1号位 PhaseFour 按其匹配。
    /// </summary>
    private async Task<IReadOnlyList<string>> ComputeChangeSeedKeysAsync(
        int basePlanVersionId,
        PeggingResultVoucher voucher)
    {
        // PM《回复0928-2》§六 裁决：ChangeSeed **读取前**必须先校验 Base 侧份额无重复 ——
        // 本方法的 `SUM(ShareQty) BY DemandKey` 是 Candidate 变化基线的唯一来源，膨胀即误判。
        await ValidateStoredShareInvariantsAsync(basePlanVersionId);

        var baseRows = await _connectionManager.QueryAsync<DemandKeyQtyRow>(
            @"SELECT DemandKey, SUM(ShareQty) AS Qty
              FROM AllocationTaskShare
              WHERE PlanVersionId = @PlanVersionId
              GROUP BY DemandKey",
            new { PlanVersionId = basePlanVersionId },
            db: DatabaseId.APS);

        var baseMap = baseRows.ToDictionary(r => r.DemandKey, r => r.Qty, StringComparer.Ordinal);

        var candMap = voucher.SupplyAllocations
            .GroupBy(a => a.DemandKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.AllocatedQuantity), StringComparer.Ordinal);

        var seeds = new HashSet<string>(StringComparer.Ordinal);

        // 新增 / 减少
        foreach (var k in candMap.Keys)
            if (!baseMap.ContainsKey(k)) seeds.Add(k);
        foreach (var k in baseMap.Keys)
            if (!candMap.ContainsKey(k)) seeds.Add(k);

        // 数量变化（两侧都存在，但本次分配量发生显著变化）
        foreach (var (k, candQty) in candMap)
            if (baseMap.TryGetValue(k, out var baseQty) && Math.Abs(candQty - baseQty) > 0.0001m)
                seeds.Add(k);

        return seeds.ToList();
    }

    private IReadOnlyList<AllocationLineage> BuildAllocationLineage(PeggingResultVoucher voucher)
    {
        var lineage = new List<AllocationLineage>();

        foreach (var alloc in voucher.SupplyAllocations)
        {
            lineage.Add(new AllocationLineage
            {
                AllocationSequence = alloc.AllocationSequence,
                DemandKey = alloc.DemandKey,
                MaterialId = alloc.SupplyMaterialId,
                SupplyType = alloc.SourceType.ToString(),
                SupplyKey = alloc.SourceReference ?? alloc.SupplySourceId?.ToString() ?? "",
                Quantity = alloc.AllocatedQuantity,
                AvailableTime = alloc.AvailableAt
            });
        }

        return lineage;
    }

    private IReadOnlyList<MaterialAvailabilitySlice> BuildMaterialConstraints(PeggingResultVoucher voucher)
    {
        var constraints = new List<MaterialAvailabilitySlice>();

        foreach (var alloc in voucher.SupplyAllocations)
        {
            // ── NEW_REQUIREMENT（自制缺口虚拟供给）**不是物料可用性**，不得进 `MaterialConstraints` ──
            //   ① 语义：`MaterialAvailability` 是「物料最早到手时间」，供 1号位 `GetMaterialEarliestTime` 作
            //      **正/倒排起始下界**（P0-05）；NEW_REQUIREMENT 是「这笔需求要靠自己生产补上」的**缺口标记**，
            //      `PeggingResultVoucher` 契约明示其**不写 PeggingSupplyAllocation**（同 `:1330` / `:9412`
            //      统计真实供给时的同一排除口径）。
            //   ② 事实：该虚拟供给的 `AvailableAt` = `order.DueDate`（`TryExpandSelfMadeNode` 内 `availableAt:
            //      order.DueDate`）。把它当可用性 ⇒ 每个自制需求的**最早开工时间恒等于自己的交期** ⇒ 正排只能
            //      排在交期之后，无交期之后的产能即整域 0 Task（2026-10-19 受控回归实证：四例 FinalTask=0）。
            //   ③ 真供给（库存／采购在途／生产指示／采购占位）**照旧保留** —— 只有缺口标记被剔除；
            //      缺口对应的**生产需求**本身已由 `LogicalProductionDemand` 承载，不依赖本条提供"可用量"。
            if (alloc.SourceType == Core.Enum.SupplySourceType.NEW_REQUIREMENT)
                continue;

            if (alloc.AvailableAt.HasValue)
            {
                var factoryId = 0;
                if (int.TryParse(alloc.FactoryCode, out var fid))
                    factoryId = fid;

                constraints.Add(new MaterialAvailabilitySlice
                {
                    AllocationSequence = alloc.AllocationSequence,
                    MaterialId = alloc.SupplyMaterialId,
                    FactoryId = factoryId,
                    Quantity = alloc.AllocatedQuantity,
                    AvailableTime = alloc.AvailableAt.Value,
                    SourceType = alloc.SourceType.ToString(),
                    SourceKey = alloc.SourceReference ?? alloc.SupplySourceId?.ToString() ?? "",
                    Commitment = null,
                    Confidence = null
                });
            }
        }

        return constraints;
    }

    /// <summary>
    /// 构建 (PI × 物料 × Stage) 级「**Stage 入口基线自由量**」投影（1号位 2026-10-10 §1.4(a)／06 接口 §257-262，
    /// 2026-10-12 落码；**2号位/1号位 数量真相域**，5号位 2026-10-10 五轮回执 §三@1 已交还）。
    ///
    /// 【为什么必须有】1号位 B-009 合法量门禁（<c>PhaseTwoInitialScheduler.ApplyPiLegalQuantityGate</c>）取
    ///   <c>cap = min(PI Original, PI Remaining, StageFreeEligibleQty − 同PI同Stage已占)</c>。
    ///   后两项**只有装载层能给**：1号位 消费形状 <c>PiStageQuantityContext</c> 的调用点当前传 <c>null</c>
    ///   ⇒ 门禁生产态不可达（1号位 已如实声明，不宣称 B-009 达标）。本方法即补该缺口的**上游产物**。
    ///
    /// 【口径（冻结，非我方发明）】业务基线 v1.10 §1231（同句重复于 03 v1.8 §43／06 v1.35 §257／07 v5.1.10 §4590／
    ///   10 v1.7 §48／**11 v2.1 §50**）逐字：`StageFreeEligibleQty`＝「PI Position/NextOperationContext、A/B已执行、
    ///   Q→A/B/C 及已被新增批占用事实限定的当前Stage自由可新成批量」
    ///   ⇒ 1号位 §1.4(a) 两问（是否含 A/B 已执行／是否按 NextOperationContext 限定）＝ **是／是**。
    ///   上界**公式**本身无条文，归我方（5号位 只给数据语义、明示不在计算器侧定义 C 桶上限）。
    ///
    /// 【公式与理由（逐项对条文）】
    ///   · **毛量** <c>StageFreeEligibleQty = min(位置量, 起点量)</c>：
    ///       位置量 = 该 Stage 上 <c>FIRST_STAGE_PENDING</c>／<c>STAGE_WAITING</c> 的 <c>PositionSlice.Quantity</c> 之和
    ///         （对「PI Position」限定；<c>XC</c>／<c>INTERPLANT_TRANSIT</c>／<c>UNLOCATED</c> 不在 Stage 上，不计入）；
    ///       起点量 = <c>NextOperationContexts</c> 中同 (物料, Stage) 的 <c>SliceQty</c> 之和（对「NextOperationContext」限定）。
    ///       二者是**同一物理池的两个视图**——5号位 §三@1 明示「位置量与工单执行剩余量**可能重叠**」，
    ///       ⇒ **取小、不做加法**（加法会静默放大可用量，正是 F-02 警示的失效形态）。
    ///   · **已占** <c>AlreadyCommittedQty = Σ 既有 MES 执行连续份额(A/B) 在该 Stage 的剩余量</c>：
    ///       逐片口径优先（<c>ExistingExecutionContextDto.Slices[].SliceQty</c>，T5-02 的「MES 工单执行剩余切片量」），
    ///       无片时退**工单级兼容投影**（<c>DerivedRemainingQty</c>，N=1 时二者镜像一致，见该 DTO 注释）。
    ///       按 <c>MaterialCode</c> 匹配物料身份，避免同 PI 跨物料串味（T5-03）。
    ///
    /// 【fail-closed 与不变量（三条，任一不成立即不产该行，**绝不伪造**）】
    ///   ① 缺 <c>PI Position</c> 事实 或 该 Stage 毛量 ≤ 0 ⇒ **不产行**（1号位 记「缺 Stage 合法自由量上下文」显式未满足）；
    ///   ② **绝不回退 <c>PiRemainingFact.PiRemainingQty</c>** 充当本量（F-02 红线）；
    ///   ③ <c>AlreadyCommittedQty</c> clamp 到 <c>[0, 毛量]</c>——重叠时溢出会让门禁取负而**错误放行**。
    ///
    /// 【分工切分】本方法只给「**入口**基线」，**不含**本轮新形成的批——那由 1号位 <c>PiStageCommitLedger</c>
    ///   在同 Run 内逐批累加后并入（1号位 §1.4(a)：2号位 认领入口基线、1号位 认领本轮增量累计）。
    ///
    /// ⚠ 已知未接：**跨 Run 的既存新增批**（前序 Run 已形成、尚未完成的新增批）未计入已占——
    ///   实库当前 0 ACTIVE 样本，无可核事实（同 `ExecutionConstraints` 接线债）。此处留痕不静默。
    /// </summary>
    private IReadOnlyList<PiStageQuantityBaseline> BuildPiStageQuantityBaselines(
        IReadOnlyList<LogicalProductionDemand> demands,
        ContinuityFacts continuityFacts)
    {
        // 只为 **C 桶（非连续份额）＋ 已验证 MTS PI ＋ 非 UNLOCATED** 的需求产行：
        //   · A/B 连续份额不参加「新增 Stage 执行批」⇒ B-009 门禁不适用（V1_4 NEW-02）；
        //   · 无 PI 号 = 非 MTS PI 来源 ⇒ 不落入 §257 该段落（消费端不闭合，见 1号位 §1.4(e)）；
        //   · 🔴 **「PI 号非空」≠「已验证 MTS PI」**（2026-10-12 收紧，PM 三问 §四）：
        //     客户订单行（`OrderType='SALES_ORDER'`）同样挂着 PI 号，但已被 `LoadPiRemainingFactsAsync`
        //     的 B-01 业务身份闸门挡在 **PI 权威事实集之外**（不辨身份即收，会让门禁分母虚增）。
        //     ⇒ 必须校验「该 PI 落在权威事实集内」，否则候选键会把**无 PI 来源**的需求计进门禁分母。
        //     PV2 实测：原谓词 21,787 ⇒ 收紧后 9,324（差的 12,463 全是订单行派生、事实侧本就不认）。
        //     ⚠ 本项**不改变送 1号位 的载荷**：那 12,463 条在原实现里同样在下一处 `continue` 掉、从不产行；
        //     收紧只让候选键计数与日志如实（产行数不变）。
        //   · UNLOCATED ⇒ 位置不可信，按 §259「不得伪造可行」不产行 —— 但**不靠 `d.IsUnlocated`**：
        //     该标记在装载层是 `// INTEGRATION TODO` 占位、恒 false（空过滤，不能当自证），
        //     真实排除靠下方**公式内的位置类型过滤**（`UNLOCATED` 不计入毛量 ⇒ `stageFree ≤ 0`）。
        var verifiedPiSet = continuityFacts.PiRemainingFacts
            .Select(f => f.ProductionInstructionNo?.Trim() ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);

        var keys = demands
            .Where(d => !d.IsContinuation
                        && !string.IsNullOrWhiteSpace(d.ProductionInstructionNo)
                        && verifiedPiSet.Contains(d.ProductionInstructionNo!.Trim())
                        && !string.IsNullOrWhiteSpace(d.StartStageCode))
            .Select(d => (Pi: d.ProductionInstructionNo!.Trim(),
                          MaterialId: d.MaterialId,
                          Stage: d.StartStageCode.Trim()))
            .Distinct()
            .ToList();

        // 诊断：被 B-01 身份闸门挡下的候选需求数（挂了 PI 号但该 PI 不在权威事实集内）。
        // 留这条是为了下次「候选键数对不上」时一眼看出是身份闸门而非位置/数量问题。
        var piIdentityGatedOut = demands.Count(d =>
            !d.IsContinuation
            && !string.IsNullOrWhiteSpace(d.ProductionInstructionNo)
            && !verifiedPiSet.Contains(d.ProductionInstructionNo!.Trim()));

        if (keys.Count == 0)
        {
            _logger.LogInformation(
                "[Pegging] §1.4(a) Stage 入口基线投影：候选键 0（B-01 身份闸门另挡下 {GatedOut} 条）⇒ 不产行",
                piIdentityGatedOut);
            return Array.Empty<PiStageQuantityBaseline>();
        }

        // (PI, 物料) → 物料编码：ExistingExecutionContext 只带 MaterialCode，须按物料身份隔离（T5-03）。
        var materialCodeByKey = continuityFacts.PiRemainingFacts
            .GroupBy(f => (f.ProductionInstructionNo, f.MaterialId))
            .ToDictionary(g => g.Key, g => g.First().MaterialCode);

        var baselines = new List<PiStageQuantityBaseline>(keys.Count);

        // ④要求「A/B 已占与 C 自由量不重复扣减」的**矛盾显式记账**（0号位 2026-10-12 §四.3 逐字：
        //   「发现数据矛盾须显式记录，**不能以取小值掩盖**」）。
        //   算子本身已保证「位置量 / 起点量取小已扣掉 A/B 已执行」的前提成立时不需要再减；
        //   若 `已占 > 毛量`，说明 A/B 已执行份额**不在**该 Stage 自由池视图内（前提不成立）——
        //   此时 clamp 是防门禁出负而错误放行的**安全兜底**，但**不是答案**，必须留痕否则被静默掩盖
        //   （旧实现只 clamp、不计数 ⇒ 下游审计 `AlreadyCommittedQty <= StageFreeEligibleQty` 恒真、查不出来）。
        var overlapContradictions = new List<string>();

        foreach (var (pi, materialId, stage) in keys)
        {
            // ① 缺位置事实 ⇒ 不产行（1号位 显式未满足；绝不回退 PiRemainingQty —— F-02）
            //   ⚠ 收紧后本分支**近乎不可达**（`keys` 先按 `PiRemainingFacts` 事实集过滤，而 `PiPositions` 同源产出）——
            //   保留作**纵深防御**：万一将来「事实集」与「位置快照」出现来源分叉，此处仍 fail-closed 而非静默产行。
            if (!continuityFacts.PiPositions.TryGetValue(pi, out var position) || position is null)
            {
                _logger.LogWarning(
                    "[Pegging] §1.4(a) Stage 入口基线：PI={Pi} × 物料 {MaterialId} × Stage {Stage} 缺 PI Position 事实 ⇒ 不产行（1号位 将 Fail Closed，不以 PI Remaining 冒充）",
                    pi, materialId, stage);
                continue;
            }

            // ── 毛量：同一物理池的两个视图取小（保守，不做加法；5号位 2026-10-10 五轮 §三@1「可能重叠」）──
            var positionQty = position.Positions
                .Where(p => (p.PositionType == PositionType.FIRST_STAGE_PENDING
                             || p.PositionType == PositionType.STAGE_WAITING)
                            && string.Equals(p.StageCode, stage, StringComparison.Ordinal))
                .Sum(p => p.Quantity);

            var nextOpQty = position.NextOperationContexts
                .Where(c => c.MaterialId == materialId
                            && string.Equals(c.StartStageCode, stage, StringComparison.Ordinal))
                .Sum(c => c.SliceQty);

            var stageFree = Math.Min(positionQty, nextOpQty);

            // ① 毛量 ≤ 0 ⇒ 该 (PI×物料×Stage) 无可新成批自由量 ⇒ 不产行
            if (stageFree <= 0m)
            {
                _logger.LogWarning(
                    "[Pegging] §1.4(a) Stage 入口基线：PI={Pi} × 物料 {MaterialId} × Stage {Stage} 可新成批自由量 ≤ 0（位置量 {PositionQty}／起点量 {NextOpQty}）⇒ 不产行",
                    pi, materialId, stage, positionQty, nextOpQty);
                continue;
            }

            // ── 入口已占：既有 MES 执行连续份额（A/B）在该 Stage 的执行剩余量（逐片优先，缺片退工单级兼容投影）──
            var committed = 0m;
            if (materialCodeByKey.TryGetValue((pi, materialId), out var materialCode))
            {
                foreach (var execution in position.ExistingExecutionContexts)
                {
                    if (!string.Equals(execution.MaterialCode, materialCode, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (execution.Slices.Count > 0)
                    {
                        committed += execution.Slices
                            .Where(s => string.Equals(s.StartStageCode, stage, StringComparison.Ordinal))
                            .Sum(s => s.SliceQty);
                    }
                    else if (string.Equals(execution.StartStageCode, stage, StringComparison.Ordinal))
                    {
                        // 兼容投影：旧单值字段（N=1 时与唯一 Slice 镜像一致，见 ExistingExecutionContextDto 注释）
                        committed += execution.DerivedRemainingQty;
                    }
                }
            }

            // ③ clamp：位置量与执行量可能重叠，已占溢出会让门禁出负而**错误放行** ⇒ 上限取毛量。
            //   钳制前后差值**逐条记账**（不静默）：负值 = 计算/事实异常；溢出 = 「A/B 已占 ⊄ 该 Stage 自由池」，
            //   属 0号位 §四.3 ④ 要求证明的那条关系被反例推翻，须交给 5号位 核 位置量与执行剩余量的重叠口径。
            if (committed < 0m || committed > stageFree)
            {
                overlapContradictions.Add(
                    $"PI={pi}/物料{materialId}/Stage={stage}: 已占 {committed} vs 毛量 {stageFree}（差 {committed - stageFree}）");
            }
            if (committed < 0m) committed = 0m;
            if (committed > stageFree) committed = stageFree;

            baselines.Add(new PiStageQuantityBaseline
            {
                ProductionInstructionNo = pi,
                MaterialId              = materialId,
                StageCode               = stage,
                StageFreeEligibleQty    = stageFree,
                AlreadyCommittedQty     = committed,
            });
        }

        _logger.LogInformation(
            "[Pegging] §1.4(a) Stage 入口基线投影：候选键 {KeyCount}（B-01 身份闸门另挡下 {GatedOut} 条「挂了 PI 号但该 PI 不在权威事实集内」的订单行需求）⇒ 产行 {RowCount}（差额 = 自由量 ≤ 0，1号位 将 Fail Closed）",
            keys.Count, piIdentityGatedOut, baselines.Count);

        if (overlapContradictions.Count > 0)
        {
            // 只打印前 20 条样本，避免日志爆量；总数始终给出（下游按总数判断严重度）。
            _logger.LogWarning(
                "[Pegging] §1.4(a) Stage 入口基线：「A/B 已占 ⊄ 该 Stage 自由量」矛盾 {Count} 条（已 clamp 到 [0,毛量] 防门禁出负，但**矛盾本身待 5号位 核口径**）样本：\n{Samples}",
                overlapContradictions.Count,
                string.Join("\n", overlapContradictions.Take(20)));
        }
        else
        {
            _logger.LogInformation(
                "[Pegging] §1.4(a) Stage 入口基线：A/B 已占 ⊄ 毛量 的矛盾 0 条（§四.3 ④ 该轮无反例）");
        }

        return baselines;
    }

    /// <summary>
    /// ScopeJsonV2 → RunScope 投影（M2）。从 ScheduleRun.ScopeJson 反序列化，按 1号位 内存键体系投影。
    /// OrderCanonicalId / TaskId 在投影层终止，不进 Solver 契约。null = FULL 语义。
    /// </summary>
    private async Task<RunScope?> BuildRunScopeAsync(
        PeggingExecutionRequest request,
        PeggingResultVoucher voucher,
        int? basePlanVersionId,
        CancellationToken ct)
    {
        var scheduleRunId = request.SchedulingContext?.ScheduleRunId;
        if (scheduleRunId == null || scheduleRunId <= 0)
            return null;

        var scopeJson = await _connectionManager.QueryFirstOrDefaultAsync<string>(
            "SELECT ScopeJson FROM ScheduleRun WHERE Id = @Id",
            new { Id = scheduleRunId.Value }, db: DatabaseId.APS);

        if (string.IsNullOrWhiteSpace(scopeJson))
            return null;

        var scope = JsonSerializer.Deserialize<ScopeJsonV2>(scopeJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        if (scope == null)
            return null;

        // 1号位 §三 补充1：Trigger 零值防御（八码从 1 起，非法值 fail-closed）
        if (!Enum.IsDefined(scope.Trigger) || (int)scope.Trigger is < 1 or > 8)
        {
            throw new InvalidOperationException(
                $"ScopeJsonV2.Trigger 非法值: {(int)scope.Trigger}（不在八码 1~8 范围内）");
        }

        // DueDateOverrides：OrderCanonicalId → LogicalDemandKey（仅 ManualTargetDueDate 非空行投影）
        IReadOnlyList<DueDateOverride> dueDateOverrides = Array.Empty<DueDateOverride>();
        if (scope.OrderTargets is { Count: > 0 })
        {
            var targets = scope.OrderTargets
                .Where(t => t.ManualTargetDueDate.HasValue)
                .ToList();
            if (targets.Count > 0)
            {
                var ocIds = targets.Select(t => t.OrderCanonicalId).Distinct().ToList();
                // 分片：`ocIds` = ScopeJson.OrderTargets 里带人工目标交期的订单（批量插单/改期可上千）
                //   ⇒ 裸 `IN @OcIds` 会撞 2100，整个 DueDateOverride 投影静默落空。走统一帮手。
                var orderRows = await QueryChunkedInAsync(ocIds, chunk =>
                    _connectionManager.QueryAsync<dynamic>(
                        @"SELECT obl.OrderId, obl.OrderCanonicalId
                          FROM OrderBomRequestLink obl
                          WHERE obl.OrderCanonicalId IN @OcIds AND obl.PlanVersionId = @PvId",
                        new { OcIds = chunk, PvId = request.PlanVersionId }, db: DatabaseId.APS));
                var orderIdByOcId = orderRows
                    .ToDictionary(r => (long)r.OrderCanonicalId, r => (long)r.OrderId);

                // DueDateOverride 的键是 LogicalDemandKey（Run 内语义，非跨版本比对键）。
                // 跨版本比对键统一用 DemandKey（1号位 回执 20260923 §2.4），此变量勿与 DemandKey 字段混淆。
                var logicalDemandKeyByOrderId = voucher.LogicalProductionDemands
                    .Where(d => d.OrderId.HasValue)
                    .GroupBy(d => d.OrderId!.Value)
                    .ToDictionary(g => g.Key, g => g.First().LogicalDemandKey);

                var list = new List<DueDateOverride>(targets.Count);
                foreach (var t in targets)
                {
                    if (orderIdByOcId.TryGetValue(t.OrderCanonicalId, out var orderId)
                        && logicalDemandKeyByOrderId.TryGetValue(orderId, out var logicalDemandKey))
                    {
                        list.Add(new DueDateOverride
                        {
                            LogicalDemandKey = logicalDemandKey,
                            ManualTargetDueDate = t.ManualTargetDueDate!.Value
                        });
                    }
                }
                dueDateOverrides = list;
            }
        }

        // TaskTargetOverrides：TaskId → (DraftId, OperationCode)（Pegging 结果回查）
        IReadOnlyList<TaskTargetOverride> taskTargetOverrides = Array.Empty<TaskTargetOverride>();
        if (scope.TaskTargets is { Count: > 0 } && basePlanVersionId.HasValue)
        {
            var taskIds = scope.TaskTargets.Select(t => t.TaskId).Distinct().ToList();
            // 分片：`taskIds` = ScopeJson.TaskTargets 的人工指定任务（批量调整可上千）⇒ 同 DueDateOverride，走统一帮手。
            var taskRows = await QueryChunkedInAsync(taskIds, chunk =>
                _connectionManager.QueryAsync<dynamic>(
                    @"SELECT Id, SourceDraftId, OperationCode
                      FROM [Task]
                      WHERE Id IN @Ids AND PlanVersionId = @PvId",
                    new { Ids = chunk, PvId = basePlanVersionId.Value }, db: DatabaseId.APS));
            var taskByKey = taskRows
                .ToDictionary(r => (long)r.Id, r => (
                    DraftId: (string?)r.SourceDraftId,
                    OpCode: (string?)r.OperationCode));

            var list = new List<TaskTargetOverride>(scope.TaskTargets.Count);
            var missCount = 0;
            foreach (var t in scope.TaskTargets)
            {
                if (taskByKey.TryGetValue(t.TaskId, out var row))
                {
                    list.Add(new TaskTargetOverride
                    {
                        DraftId = row.DraftId ?? string.Empty,
                        OperationCode = row.OpCode ?? string.Empty,
                        TargetTime = t.TargetTime
                    });
                }
                else
                {
                    // 3号位 协助项（2026-09-21）：TaskId 未命中 (DraftId, OperationCode) 时不再静默跳过，
                    // 计数告警（TaskId 自增键漂移/归档会导致软目标静默丢失，车间无感）。
                    missCount++;
                }
            }
            if (missCount > 0)
            {
                _logger.LogWarning(
                    "[Pegging][ScopeJsonV2] TaskTargets 投影未命中 {MissCount} 行（TaskId 漂移或已归档，软目标未下达 Solver）",
                    missCount);
            }
            taskTargetOverrides = list;
        }

        return new RunScope
        {
            Trigger = scope.Trigger,
            PriorityMode = scope.PriorityMode,
            InScopeLogicalDemandKeys = Array.Empty<string>(),  // 冻结 v1.7 §69：影响范围由 1号位 从变化 Seed 动态传播推导；2号位 只转 ChangeSeed/TaskTarget/ChangedResourceIds，不预填可移动集合
            DueDateOverrides = dueDateOverrides,
            TaskTargetOverrides = taskTargetOverrides,
            ChangedResourceIds = scope.ChangedResourceIds ?? Array.Empty<int>(),
            // BusinessScope：3号位 生成 → 2号位 原样透传（不判权限）；null 交 1号位 无授权 fail-safe。
            BusinessScope = scope.BusinessScope
        };
    }

    private IReadOnlyList<ResourceDefinition> BuildResourceDefinitions(Core.Models.Scheduling.SchedulingContext? context)
    {
        if (context == null || context.Resources.Count == 0)
            return Array.Empty<ResourceDefinition>();

        var resources = new List<ResourceDefinition>();
        var invalidRidCount = 0;
        var invalidRidSamples = new List<string>(5);

        foreach (var res in context.Resources)
        {
            // G-16：ResourceId 解析失败回落 0（0 不与任何真实设备撞号，实测 Resource.Id 最小 6,444）⇒ 不暴露即静默。
            //       仅告警，**不剔行、不改返回值**。
            // 【2026-10-17 J14-07 改造】**无效 ResourceId 不得进入 Solver 设备评分**。
            //   原实现把解析失败回落成 `0` 并照常 Add（注释自认「不暴露即静默」）—— 但 0 会作为真实设备参与
            //   容量/换型/日历匹配，等于凭空造出一台编号 0 的机器；实测 `Resource.Id` 最小 6,444，0 永远不是真设备。
            //   ⇒ 非数字 / `"0"` / 负数一律**不 Add**，计数并留样本（不静默）。
            if (!int.TryParse(res.ResourceId, out var rid) || rid <= 0)
            {
                invalidRidCount++;
                if (invalidRidSamples.Count < 5)
                    invalidRidSamples.Add($"{res.ResourceCode}({res.ResourceId})@{res.FactoryId}");
                continue;
            }

            resources.Add(new ResourceDefinition
            {
                ResourceId = rid,
                ResourceCode = res.ResourceCode,
                FactoryCode = res.FactoryId,
                Capacity = res.CapacityFactor
            });
        }

        if (invalidRidCount > 0)
        {
            _logger.LogWarning(
                "[Pegging] 无效 ResourceId 已剔除（不入 Solver 资源集，J14-07）：{Count}/{Total} 条资源编码无法映射为正整数，样本：{Samples}",
                invalidRidCount, context.Resources.Count, string.Join("; ", invalidRidSamples));
        }

        return resources;
    }

    private IReadOnlyList<ResourceCalendarSlot> BuildResourceCalendarSlots(Core.Models.Scheduling.SchedulingContext? context)
    {
        if (context == null || context.ResourceCalendars.Count == 0)
            return Array.Empty<ResourceCalendarSlot>();

        var slots = new List<ResourceCalendarSlot>();

        foreach (var (resourceIdStr, timeWindows) in context.ResourceCalendars)
        {
            // J14-07：与 `BuildResourceDefinitions` 同口径 —— 非正数（非数字 / "0" / 负数）不是真实设备，
            //   不得为其建日历槽（否则 0 号「幽灵资源」会拿到可用工时）。
            if (!int.TryParse(resourceIdStr, out var resourceId) || resourceId <= 0)
                continue;

            foreach (var window in timeWindows)
            {
                slots.Add(new ResourceCalendarSlot
                {
                    ResourceId = resourceId,
                    Start = window.Start,
                    End = window.End,
                    IsAvailable = true
                });
            }
        }

        return slots;
    }
}
