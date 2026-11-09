using System.Data;
using System.Text.Json;
using Dapper;
using LPS.APS.Application.Services.Dto;
using LPS.APS.BusinessRules.Loaders;
using LPS.APS.BusinessRules.Models;
using LPS.APS.BusinessRules.Repositories;
using LPS.APS.Core.Authorization;
using LPS.APS.Core.Dto;
using LPS.APS.Core.DTOs.Governance;
using LPS.APS.Core.Models;
using LPS.APS.Core.Rules;
using LPS.APS.Core.Interfaces;
using LPS.APS.Core.Models.Scheduling;
using LPS.APS.Engine.Data;
using LPS.APS.Scheduling.Algorithms;
using LPS.APS.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
// ⚠ 用别名而非 `using LPS.APS.Core.Entities.APS;`：该命名空间里有实体 `Task`，
//   整体引入会与 `System.Threading.Tasks.Task` 二义（CS0104），本文件多处 async 方法签名会炸。
using ProductionWorkCalendarSlot = LPS.APS.Core.Entities.APS.ProductionWorkCalendarSlot;
// ⚠ 同上：别名引入 PlanVersion 实体（`ReplaceActiveAsync` 的入参类型），避免整体引入该命名空间。
using PlanVersionEntity = LPS.APS.Core.Entities.APS.PlanVersion;

namespace LPS.APS.Application.Services;

/// <summary>
/// 排程编排器（2号位职责 — §2.5.1 排程发令枪）
/// 
/// 每日02:00由Hangfire触发，完整编排流程：
///   阶段1: 装载排程沙盘（从APS库读取订单/BOM/物料/设备/库存 → SchedulingContext）
///   阶段2: Pegging（2号位PeggingOrchestrator生成Task + Allocation）
///   阶段3: 调用 FiniteCapacitySolver.Solve()（纯内存计算）
///   阶段4: 排程结果落盘
///   阶段5: PlanVersion 状态更新
///   阶段6: 快照封存（§2.6 SchedulingContext → .json.gz）
///
/// 架构位置：Application 层（桥接 Engine 数据层 + Scheduling 算法层）
/// </summary>
public class SchedulingOrchestrator : ISchedulingOrchestrator
{
    /// <summary>
    /// Dapper 把 `IN @List` 展开成**逐个值一个参数**，SQL Server 单次 RPC 参数上限 2100 ⇒ 取 2000 留余量。
    /// 与 `PeggingOrchestrator.SqlServerInParameterLimit` 同口径（两个类各持一份，避免跨类耦合）。
    /// </summary>
    private const int SqlServerInParameterLimit = 2000;

    // ══ PlanVersion 生命周期状态（**冻结词表** 07 §4.2:3532：BUILDING / CANDIDATE / ACTIVE / ARCHIVED / FAILED）══
    // 【2026-10-15 词表对齐】原 D7 词表 Created / Computed / ComputeFailed 按冻结文档改名：
    //   Created → BUILDING、Computed → CANDIDATE、ComputeFailed → FAILED。
    //   `Computing` 冻结未细分，但本仓**必须保留**（它是发令枪的领取闸门，见 RunSchedulingAutoAsync 注释），
    //   视为 BUILDING 期间的子状态。详见台账 §T-1037。
    // 顺带修掉一个真 bug：本类原写 `ComputeFailed`，而 RunLifecycleService 的域级判定读 `FAILED`
    //   ⇒ 失败域永远判不出来（落到 `_ => RUNNING`）。改名后写读同值，该 bug 自愈。
    /// <summary>壳已建、待发令枪领取。</summary>
    private const string PlanVersionBuildingStatus = "BUILDING";
    /// <summary>本域已被领取、正在计算（BUILDING 的子状态；**不可与 BUILDING 合并**）。</summary>
    private const string PlanVersionComputingStatus = "Computing";
    /// <summary>计算成功、待采用（白天候选的终态；夜间全量成功直接跳到 ACTIVE）。</summary>
    private const string PlanVersionCandidateStatus = "CANDIDATE";
    /// <summary>已正式采用（每域单一 ACTIVE，唯一索引 UQ_PlanVersion_OneActivePerDomain 兜底）。</summary>
    private const string PlanVersionActiveStatus = "ACTIVE";
    /// <summary>被同域新版本顶替。</summary>
    private const string PlanVersionArchivedStatus = "ARCHIVED";
    /// <summary>计算失败。</summary>
    private const string PlanVersionFailedStatus = "FAILED";
    /// <summary>夜间全量运行类型 —— **唯一**可自动正式采用（Status→ACTIVE）的 RunType。</summary>
    private const string FullScheduleRunType = "FULL_SCHEDULE";

    private readonly DatabaseConnectionManager _connectionManager;
    private readonly ISnapshotService _snapshotService;
    private readonly IPeggingOrchestrator _peggingOrchestrator;
    private readonly IScheduleRunService _scheduleRunService;
    private readonly LPS.APS.Engine.Services.Sync.IOrderLoadingService _orderLoadingService;
    private readonly LPS.APS.Engine.Services.Sync.IBomRealtimeExpandService _bomRealtimeExpandService;
    /// <summary>
    /// 【2026-10-15 新增】夜间全量成功自动采用（Status→ACTIVE）复用 <c>ReplaceActiveAsync</c> ——
    /// 「同域旧 ACTIVE 归档 + 本版本置 ACTIVE」的单事务单点实现，与 3号位 人工激活**同一条路径**，
    /// 避免在本类里另写一份归档 SQL 造成口径漂移（唯一索引 UQ_PlanVersion_OneActivePerDomain 由它兜底）。
    /// </summary>
    private readonly IPlanVersionRepository _planVersionRepository;
    // 1.5b 管道供给装载与 Pegging 同源同口径：同一个 5号位 装载器 + 同一个三级链计算器 + 同一份冻结策略快照。
    private readonly ITimedSupplyFactLoader _timedSupplyFactLoader;
    private readonly IProcurementManualEtaRepository _procurementManualEtaRepo;
    private readonly IFrozenStrategySnapshotProvider _frozenStrategySnapshotProvider;
    // D1~D4 保留期清理：夜间 FULL 成功后独立小批维护。**不参与任何事务、失败不反推排程失败、不新增 Cron**。
    private readonly IRetentionMaintenanceService _retentionMaintenance;
    /// <summary>
    /// 保留期清理开关，配置键 <c>Retention:Enabled</c>，**默认 false**。
    /// false ⇒ 每轮只在日志里出 dry-run 测算数，一行不删；true ⇒ 才真删。
    /// D4「受控的小批维护」的「受控」= 这一行配置，改它需人工授权，不随部署自动开。
    /// </summary>
    private readonly bool _retentionDeleteEnabled;
    private readonly ILogger<SchedulingOrchestrator> _logger;

    public SchedulingOrchestrator(
        DatabaseConnectionManager connectionManager,
        ISnapshotService snapshotService,
        IPeggingOrchestrator peggingOrchestrator,
        IScheduleRunService scheduleRunService,
        LPS.APS.Engine.Services.Sync.IOrderLoadingService orderLoadingService,
        LPS.APS.Engine.Services.Sync.IBomRealtimeExpandService bomRealtimeExpandService,
        IPlanVersionRepository planVersionRepository,
        ITimedSupplyFactLoader timedSupplyFactLoader,
        IProcurementManualEtaRepository procurementManualEtaRepo,
        IFrozenStrategySnapshotProvider frozenStrategySnapshotProvider,
        IRetentionMaintenanceService retentionMaintenance,
        IConfiguration configuration,
        ILogger<SchedulingOrchestrator> logger)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _snapshotService = snapshotService ?? throw new ArgumentNullException(nameof(snapshotService));
        _peggingOrchestrator = peggingOrchestrator ?? throw new ArgumentNullException(nameof(peggingOrchestrator));
        _scheduleRunService = scheduleRunService ?? throw new ArgumentNullException(nameof(scheduleRunService));
        _orderLoadingService = orderLoadingService ?? throw new ArgumentNullException(nameof(orderLoadingService));
        _bomRealtimeExpandService = bomRealtimeExpandService ?? throw new ArgumentNullException(nameof(bomRealtimeExpandService));
        _planVersionRepository = planVersionRepository ?? throw new ArgumentNullException(nameof(planVersionRepository));
        _timedSupplyFactLoader = timedSupplyFactLoader ?? throw new ArgumentNullException(nameof(timedSupplyFactLoader));
        _procurementManualEtaRepo = procurementManualEtaRepo ?? throw new ArgumentNullException(nameof(procurementManualEtaRepo));
        _frozenStrategySnapshotProvider = frozenStrategySnapshotProvider ?? throw new ArgumentNullException(nameof(frozenStrategySnapshotProvider));
        _retentionMaintenance = retentionMaintenance ?? throw new ArgumentNullException(nameof(retentionMaintenance));
        _retentionDeleteEnabled = configuration?.GetValue<bool>("Retention:Enabled", false) ?? false;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<SchedulingRunResult> RunSchedulingAutoAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("排程发令枪：自动查找待排 FULL_SCHEDULE ScheduleRun（RUNNING + 有 BUILDING PlanVersion）");

        // 原子领取：领取粒度从「单个 PlanVersion」上移为「ScheduleRun」。
        // 分域后一个 ScheduleRun 对应多个 PlanVersion（每 Domain 一个，由 NightlyBatch Step3 创建），
        // 此处锁定 RUNNING 且仍有 BUILDING PlanVersion 的 ScheduleRun，避免多 Worker 重复领取同一 Run。
        // RunType='FULL_SCHEDULE' 过滤：仅领夜间正式排程；白天候选（MANUAL_RESCHEDULE 等）由 3号位 显式触发，不得被发令枪误领。
        //
        // ⚠️ 为什么 EXISTS 判的是 `Status='BUILDING'`（冻结词表）而不是别的值 —— 这不是进度显示，是**领取闸门**：
        //   · ScheduleRun 在跑的过程中 `Status` 恒为 'RUNNING'（ScheduleRunService 只写 COMPLETED/PARTIAL_SUCCESS/FAILED 三个终态）
        //     ⇒ 光靠 `sr.Status='RUNNING'` **挡不住**第二个 Worker 领走同一个 Run。
        //   · `WITH (UPDLOCK, READPAST, ROWLOCK)` 只在领取事务期间互斥，事务一提交锁就放。
        //   ⇒ 真正的防重复领取，靠的就是「域一旦开跑就不再是 BUILDING」这一条：
        //     ExecuteDomainAsync 领到域后立刻把它写成 `Computing`，EXISTS 随即转假。
        //   ⚠️ 因此 `Computing` 这个值**不可与 BUILDING 合并**（冻结词表未细分它，但本仓必须保留）——
        //     合并后已开跑的 Run 的 EXISTS 恒真，多 Worker 会重复领取并重跑整个 Run。详见台账 §T-1037。
        var scheduleRun = await _connectionManager.QueryFirstOrDefaultAsync<ScheduleRunQueryDto>(
            @"SELECT TOP 1 sr.Id, sr.DataCutoffTime, sr.StrategyProfileVersionId
              FROM ScheduleRun sr WITH (UPDLOCK, READPAST, ROWLOCK)
              WHERE sr.Status = 'RUNNING'
                AND sr.RunType = 'FULL_SCHEDULE'
                AND EXISTS (
                    SELECT 1 FROM PlanVersion pv
                    WHERE pv.SourceScheduleRunId = sr.Id AND pv.Status = 'BUILDING'
                )
              ORDER BY sr.CreatedAt DESC",
            db: DatabaseId.APS);

        if (scheduleRun == null)
        {
            _logger.LogInformation("未找到待排 FULL_SCHEDULE ScheduleRun（RUNNING + 有 BUILDING PlanVersion），跳过本次触发");
            return new SchedulingRunResult { IsSuccess = true, ErrorMessage = "无待排 ScheduleRun" };
        }

        _logger.LogInformation("成功领取 ScheduleRun: ScheduleRunId={RunId}", scheduleRun.Id);

        var runResult = await ExecuteRunAsync(scheduleRun.Id, cancellationToken);

        // ── D1~D4 保留期清理挂载点（0号位 2026-11-02 v2.0 §2.1 D4）──────────────
        // 位置：夜间 FULL Run 收口为 COMPLETED 之后（IsSuccess 且非越界中止）。
        // D4 的四条硬约束在此逐条兑现：
        //   ① 不在 Solver / 主落库事务中删 —— 此处所有落库已完成，本调用独立开连接、独立语句；
        //   ② 不新增 Hangfire Cron —— 挂在既有 02:00 发令枪尾部，不注册任何新任务；
        //   ③ 清理失败不反推正式排程失败 —— RunAsync 内部吞异常，本处返回的仍是 runResult；
        //   ④ 受控 —— Retention:Enabled 默认 false，不改配置就只出 dry-run 测算数。
        // ⚠ PARTIAL_SUCCESS / FAILED 一律不清理：失败诊断必须留（D1 保护集第②条）。
        if (runResult.IsSuccess)
            await RunRetentionMaintenanceAsync(cancellationToken);

        return runResult;
    }

    /// <summary>
    /// 保留期清理（D1~D4）。**任何情况下都不抛出**，只写日志——
    /// 清理是维护动作，不得影响排程结果（D4 原文「清理失败不反推正式排程失败」）。
    /// </summary>
    private async Task RunRetentionMaintenanceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _retentionMaintenance.RunAsync(dryRun: !_retentionDeleteEnabled, cancellationToken);

            if (result.Error != null)
            {
                _logger.LogWarning("保留期清理未完成（不影响排程）：{Error}", result.Error);
                return;
            }

            foreach (var item in result.Items)
            {
                _logger.LogInformation(
                    "保留期清理【{Rule}】{Table}：总 {Total} / 超窗 {Over} / 受保护 {Protected} / 可删 {Deletable}（{Mode}）",
                    item.Rule, item.Table, item.TotalRows, item.OverWindowRows,
                    item.ProtectedRows, item.DeletableRows,
                    result.DryRun ? "dry-run 未删" : "已删除");
            }

            _logger.LogInformation(
                "保留期清理结束：模式={Mode}, 实际删除={Deleted} 行（测算可删 {Planned} 行）。",
                result.DryRun ? "dry-run" : "执行", result.DeletedRows, result.TotalDeletable);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保留期清理异常（不影响排程结果）。");
        }
    }

    /// <inheritdoc />
    public async Task<SchedulingRunResult> ExecuteRunAsync(int scheduleRunId, CancellationToken cancellationToken = default)
    {
        // 【2026-09-29 P1-03】本方法体原属 RunSchedulingAutoAsync，抽出后由两个调用方共用：
        //   ① 夜间发令枪（RunSchedulingAutoAsync 领取后委托）② FAILED 恢复（3号位 建壳后直接调）。
        //   执行基线（DataCutoffTime / 策略包版本）由本方法**自取**而非调用方传参 ⇒ 两条路径的参数口径
        //   不可能漂移；调用方只负责判定「这个 Run 该不该跑」，本方法不重复校验 RunType/Status。
        var run = await _connectionManager.QueryFirstOrDefaultAsync<ScheduleRunQueryDto>(
            @"SELECT Id, DataCutoffTime, StrategyProfileVersionId
              FROM ScheduleRun WHERE Id = @Id",
            new { Id = scheduleRunId },
            db: DatabaseId.APS);

        if (run == null)
        {
            _logger.LogError("ScheduleRun {RunId} 不存在，无法执行", scheduleRunId);
            return new SchedulingRunResult { IsSuccess = false, ErrorMessage = $"ScheduleRun 不存在：{scheduleRunId}" };
        }

        await RefreshRunStartedAtAsync(scheduleRunId, cancellationToken);

        // 读冻结预期 Domain 集合（运行启动唯一权威来源；FULL_SCHEDULE 须 ≥1 Domain）
        IReadOnlyList<string> domainKeys;
        try
        {
            domainKeys = await _scheduleRunService.GetExpectedDomainKeysAsync(scheduleRunId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取 ExpectedDomainKeysJson 失败: ScheduleRunId={RunId}", scheduleRunId);
            await _scheduleRunService.FailAsync(scheduleRunId, 0, $"读取 ExpectedDomainKeysJson 失败: {ex.Message}", cancellationToken);
            return new SchedulingRunResult { IsSuccess = false, ErrorMessage = ex.Message };
        }

        if (domainKeys.Count == 0)
        {
            _logger.LogError("ScheduleRun {RunId} 的 ExpectedDomainKeysJson 解析结果为空 Domain 集合", scheduleRunId);
            await _scheduleRunService.FailAsync(scheduleRunId, 0, "ExpectedDomainKeysJson 解析结果为空 Domain 集合", cancellationToken);
            return new SchedulingRunResult { IsSuccess = false, ErrorMessage = "ExpectedDomainKeysJson 为空 Domain 集合" };
        }

        // 【2026-10-14 §T-1014q ① ②】Run 级收口兜底：进入执行区后，任何中断（异常 / 取消令牌穿出）都必须让 Run 落到终态，
        //   否则 Run 永久停在 RUNNING —— 发令枪只领「有 Created 域」的 Run、恢复端点只收 FAILED，卡在 Computing 的 Run
        //   两条路都领不走（同款自述见 RunLifecycleService 的 P1-03 §5.4 注释）。
        //   落终态一律用 finalizeToken(=None)：客户端断连 / 请求取消也能落库（同候选路径 RunLifecycleService.CreateCandidateRunAsync 的既有做法）。
        var finalizeToken = CancellationToken.None;
        var runStopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // 【2026-09-29 重入守卫，对应回执 §5.3】本方法现有两个调用方 ⇒ 同一 Run 可能被先后执行两次
            //   （例：发令枪已跑完，恢复又显式调一次）。若**全部**预期 Domain 的 PlanVersion 已达终态，
            //   则跳过整段 Domain 执行，只补一次收口 —— 这同时兜住「全部 Domain 跑完但收口前崩溃」导致
            //   Run 永久卡在 RUNNING 的情形（否则那种 Run 再也领不走）。
            //   · 为什么是「全终态才跳过」而不是「逐域跳过已达终态的 Domain」：逐域跳过会丢掉已成功域的
            //     sharedResourceOccupancy（FULL §9 跨域资源占用），后续 Domain 会排进已被占用的窗口 —— 那比
            //     重跑更糟。全终态跳过时一个 Domain 都不执行，该上下文无人消费，故无此副作用。
            //   · ⚠️ 边界：只覆盖「先后调用」，**不覆盖两个调用方同时在飞的并发**（那需要执行级锁，见回执 §5.3）。
            // 终态集合（冻结词表）与「只认预期 Domain」两条口径下沉到 ReadTerminalDomainOutcomesAsync，
            //   与单域入口（RunSchedulingAndFinalizeAsync）共用同一份判定。
            var (computedDomains, terminalFailed) =
                await ReadTerminalDomainOutcomesAsync(scheduleRunId, domainKeys, cancellationToken);

            if (computedDomains.Count + terminalFailed.Count >= domainKeys.Count)
            {
                // 从终态 PV 反推成功/失败数，走**同一个**收口方法 ⇒ 收口语义与正常路径单点、不会漂移
                _logger.LogWarning(
                    "ScheduleRun {RunId} 的全部 {Total} 个 Domain 已达终态（Computed={Computed}, 失败={Failed}），跳过重跑、仅补收口",
                    scheduleRunId, domainKeys.Count, computedDomains.Count, terminalFailed.Count);

                runStopwatch.Stop();
                var reentryDurationSeconds = (int)(runStopwatch.ElapsedMilliseconds / 1000);
                await CloseOutRunAsync(scheduleRunId, domainKeys.Count, computedDomains.Count, terminalFailed, reentryDurationSeconds, finalizeToken);

                return new SchedulingRunResult
                {
                    IsSuccess        = computedDomains.Count == domainKeys.Count,
                    ScheduledCount   = 0,
                    UnscheduledCount = terminalFailed.Count,
                    ElapsedMs        = runStopwatch.ElapsedMilliseconds,
                    ErrorMessage     = computedDomains.Count == domainKeys.Count
                        ? "该 ScheduleRun 的全部 Domain 已达终态，已跳过重跑"
                        : $"该 ScheduleRun 已达终态：成功 {computedDomains.Count}/{domainKeys.Count}，失败 Domain={string.Join(",", terminalFailed)}"
                };
            }

            // Run 可比性四元组（夜间批）：**在 Domain 循环之前只算一次**。
            // 不得下移到 ExecuteDomainAsync —— FULL 跑批 14 个 Domain，按域算会把 13 张表 COUNT(*) 放大 14 倍。
            // 策略版本：Run 冻结值优先；为 null 说明走运行时反查（见 ResolveDefaultStrategyVersionIdAsync），
            // 此时记 NULL 本身就是可比性信息（「这一轮没有钉死策略版本」）。
            await LogRunComparabilityAsync(planVersionId: null, run.StrategyProfileVersionId, scheduleRunId);

            var domainResults = new List<SchedulingRunResult>(domainKeys.Count);
            var succeededCount = 0;
            var failedDomainKeys = new List<string>();

            // 域间依赖（真实 DomainKey 口径，§5/§7/§15/§23）：依赖边 → 拓扑执行序 + 传递上游闭包。
            // Domain_Dependency 空（V1 现状）时两者均退化：执行序=冻结顺序、闭包为空（不阻断）。
            var dependencyEdges = await LoadDomainDependencyEdgesAsync(domainKeys, cancellationToken);
            var orderedDomainKeys = OrderDomainsTopologically(domainKeys, dependencyEdges);
            var upstreamClosure = BuildUpstreamClosure(dependencyEdges, domainKeys);

            // FULL §9：前序 Domain 成功后的共享 Resource 占用块（逐 Domain 累积，传给后续 Domain 作不可用时间窗）
            var sharedResourceOccupancy = new List<ResourceBlock>();

            // 壳盘点（**只作归因诊断，不参与执行决策**）。
            // 为何要单独盘点：缺壳的真实因由**随路径而异** ——
            //   夜间/候选路径：装载阶段就没建壳；恢复路径（P1-03）：3号位 建壳未按「一域一壳 + 与 Run 同事务」建全。
            // 原措辞一律记「装载缺口」，会把排查引向错误阶段（恢复缺壳与装载阶段无关）。见 §5.4 同族的 P1-03 接线。
            // 该查询同时暴露「同一 DomainKey 多个壳」：下面执行侧取 `TOP 1 … ORDER BY Id DESC`，
            // 多出来的壳不会被任何一次执行认领 ⇒ **永久停在 Created**（建壳若不幂等就会踩到）。
            var shellCounts = (await _connectionManager.QueryAsync<DomainShellCount>(
                @"SELECT DomainKey, COUNT(*) AS ShellCount
                  FROM PlanVersion
                  WHERE SourceScheduleRunId = @RunId
                  GROUP BY DomainKey",
                new { RunId = scheduleRunId },
                db: DatabaseId.APS)).ToList();

            var shelledDomainSet = new HashSet<string>(
                shellCounts.Select(s => s.DomainKey!).Where(k => !string.IsNullOrEmpty(k)),
                StringComparer.OrdinalIgnoreCase);

            var missingShellDomains = orderedDomainKeys.Where(d => !shelledDomainSet.Contains(d)).ToList();
            var duplicateShellDomains = shellCounts
                .Where(s => s.ShellCount > 1 && !string.IsNullOrEmpty(s.DomainKey))
                .Select(s => $"{s.DomainKey}×{s.ShellCount}")
                .ToList();

            if (missingShellDomains.Count > 0 || duplicateShellDomains.Count > 0)
            {
                _logger.LogError(
                    "[{RunId}] PlanVersion 壳盘点异常：预期 {Total} 个 Domain，壳行数={ShellRows}，缺壳={Missing}，重复壳={Dup}。" +
                    "请核对建壳步骤（一域一壳 + 与 Run 同事务）",
                    scheduleRunId, orderedDomainKeys.Count, shellCounts.Sum(s => s.ShellCount),
                    missingShellDomains.Count == 0 ? "无" : string.Join(",", missingShellDomains),
                    duplicateShellDomains.Count == 0 ? "无" : string.Join(",", duplicateShellDomains));
            }

            // R03-02 跨域守恒：Run 级物理供给消耗账本（Key=PhysicalSourceKey，Value=已消耗量）。
            // 只在内存中存在，不落库、不回写 InventoryBalance.AllocatedQty。
            // FULL 模式下前序**成功** Domain 的合法消耗由后续 Domain 继承；失败 Domain 的消耗按快照回滚，
            // 不得占用其它 Domain 的供给（失败域的分配结果本就不落库，账本必须与之一致）。
            var crossDomainPhysicalConsumed = new Dictionary<string, decimal>(StringComparer.Ordinal);

            // 逐 Domain 串行执行（依赖顺序 = Domain_Dependency 拓扑序；无依赖边时降级为 ExpectedDomainKeysJson 冻结顺序）
            //
            // 队列3（3号位 A 案）：部门范围越界＝**整轮中止**，不是单域计算失败。
            // 用带下标的 for 而非 foreach：越界时要知道「已提交到第几个」，才能把未执行域精确落 FAILED。
            string? scopeAbortMessage = null;

            for (var domainIndex = 0; domainIndex < orderedDomainKeys.Count; domainIndex++)
            {
                var domainKey = orderedDomainKeys[domainIndex];
                cancellationToken.ThrowIfCancellationRequested();

                var planVersion = await _connectionManager.QueryFirstOrDefaultAsync<PlanVersionInfoDto>(
                    @"SELECT TOP 1 Id, VersionCode, DomainKey, PlanHorizonStart, PlanHorizonEnd
                      FROM PlanVersion
                      WHERE SourceScheduleRunId = @RunId AND DomainKey = @DomainKey
                      ORDER BY Id DESC",
                    new { RunId = scheduleRunId, DomainKey = domainKey },
                    db: DatabaseId.APS);

                if (planVersion == null)
                {
                    // 预期 Domain 无对应 PlanVersion 壳。**不再武断归因为「装载缺口」** —— 因由随路径而异
                    // （装载阶段未建 / 恢复建壳未建全），上方壳盘点已给出该 Run 的权威诊断，这里只引用它。
                    _logger.LogError(
                        "[{RunId}] Domain {DomainKey} 无对应 PlanVersion 壳 ⇒ 计入失败（本 Run 缺壳={Missing}，诊断见上方壳盘点日志）",
                        scheduleRunId, domainKey,
                        missingShellDomains.Count == 0 ? "无" : string.Join(",", missingShellDomains));
                    failedDomainKeys.Add(domainKey);
                    continue;
                }

                // 依赖感知阻断：仅当本 Domain 直接/间接依赖某个已失败上游时才阻断（§5/§7/§15/§23），
                // 而非一刀切阻断所有后续 Domain。
                //
                // 【2026-11-09 O52】阻断前先算「真影响」，**受影响订单为 0 就不阻断**。
                //   实测教训（Run 999423）：`FAMILY_JS` 失败后按传递闭包连带打死 LGJ/TZP/VALVE/WKQ，
                //   4 域合计 30,385 单被阻断，而按一层 BOM 反查**真正引用 JS 那 14 个半成品的订单仅 127 单（0.42%）**，
                //   其中 `FAMILY_LGJ` 593 单**受影响 0**。域级闭包判定过粗，会把本来能排的域整域判死。
                //   口径：受影响 = 本域订单（`[Order]`）× 其直接 BOM 子件 × 失败上游在该 PV 产出的半成品。
                if (failedDomainKeys.Count > 0
                    && upstreamClosure.TryGetValue(domainKey, out var upstreams)
                    && upstreams.Overlaps(failedDomainKeys))
                {
                    var hitUpstreams = upstreams
                        .Where(u => failedDomainKeys.Contains(u, StringComparer.OrdinalIgnoreCase))
                        .OrderBy(u => u, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    var affectedOrders = await CountAffectedOrdersAsync(
                        planVersion.Id, hitUpstreams);

                    if (affectedOrders == 0)
                    {
                        // 上游失败但本域没有任何订单引用其产出 ⇒ 不阻断，正常排程（WARN 留痕，不静默）
                        _logger.LogWarning(
                            "[{RunId}] Domain {DomainKey} 的上游 {Upstream} 已失败，但本域**无订单引用其产出** ⇒ 不阻断，正常排程（UPSTREAM_FAILED_NO_IMPACT）",
                            scheduleRunId, domainKey, string.Join(",", hitUpstreams));
                    }
                    else
                    {
                        await BlockDomainAsync(planVersion.Id);
                        _logger.LogWarning(
                            "[{RunId}] Domain {DomainKey} 因上游失败被阻断: PlanVersionId={PlanVersionId}，失败上游={Upstream}，受影响订单={Affected}",
                            scheduleRunId, domainKey, planVersion.Id,
                            string.Join(",", hitUpstreams), affectedOrders);
                        domainResults.Add(new SchedulingRunResult
                        {
                            PlanVersionId = planVersion.Id,
                            VersionCode   = planVersion.VersionCode,
                            IsSuccess     = false,
                            ErrorMessage  = $"上游 Domain 失败被阻断（失败上游={string.Join(",", hitUpstreams)}；受影响订单={affectedOrders}）"
                        });
                        failedDomainKeys.Add(domainKey);
                        continue;
                    }
                }

                // 失败回滚锚点：本 Domain 的消耗若因失败不落库，账本也不能留下痕迹
                var consumptionSnapshot = new Dictionary<string, decimal>(
                    crossDomainPhysicalConsumed, StringComparer.Ordinal);

                SchedulingRunResult result;
                try
                {
                    result = await ExecuteDomainAsync(
                        planVersion.Id, scheduleRunId, run.DataCutoffTime, run.StrategyProfileVersionId,
                        sharedResourceOccupancy, sourcePlanVersionId: null, scope: null,
                        cancellationToken: cancellationToken,
                        crossDomainPhysicalConsumed: crossDomainPhysicalConsumed);
                }
                catch (ScopeViolationException sx)
                {
                    // 越界**不**写成「本域失败、继续下一域」：已提交域保留终态，未执行域全落 FAILED，Run 直接 FAILED。
                    scopeAbortMessage = await AbortRunForScopeViolationAsync(
                        scheduleRunId, orderedDomainKeys, domainIndex,
                        (int)(runStopwatch.ElapsedMilliseconds / 1000), sx, finalizeToken);
                    break;
                }
                domainResults.Add(result);

                if (result.IsSuccess)
                {
                    succeededCount++;
                    // FULL §9：成功 Domain 的 FinalTask 共享 Resource 占用区间，累积进 Run 级上下文
                    if (result.EmittedResourceBlocks.Count > 0)
                        sharedResourceOccupancy.AddRange(result.EmittedResourceBlocks);
                }
                else
                {
                    failedDomainKeys.Add(domainKey);

                    // R03-02：失败 Domain 的物理消耗回滚到本域执行前
                    crossDomainPhysicalConsumed.Clear();
                    foreach (var kv in consumptionSnapshot)
                        crossDomainPhysicalConsumed[kv.Key] = kv.Value;
                }
            }

            runStopwatch.Stop();
            var durationSeconds = (int)(runStopwatch.ElapsedMilliseconds / 1000);

            // 部门范围越界 ⇒ 整轮中止：FailAsync 已在 AbortRunForScopeViolationAsync 内落库，
            // **跳过** CloseOutRunAsync（越界不得计成 PARTIAL_SUCCESS）。
            if (scopeAbortMessage != null)
            {
                _logger.LogError(
                    "排程整轮中止（部门范围越界）: ScheduleRunId={RunId}, 成功域={Succeeded}/{Total}, 耗时={Elapsed}ms, 原因={Reason}",
                    scheduleRunId, succeededCount, domainKeys.Count, runStopwatch.ElapsedMilliseconds, scopeAbortMessage);

                return new SchedulingRunResult
                {
                    IsSuccess        = false,
                    ScheduledCount   = domainResults.Where(r => r.IsSuccess).Sum(r => r.ScheduledCount),
                    UnscheduledCount = domainResults.Count(r => !r.IsSuccess),
                    ElapsedMs        = runStopwatch.ElapsedMilliseconds,
                    ErrorMessage     = scopeAbortMessage
                };
            }

            // ScheduleRun 终态收口（与重入路径共用 CloseOutRunAsync ⇒ 三态语义单点，不会两处漂移）
            await CloseOutRunAsync(scheduleRunId, domainKeys.Count, succeededCount, failedDomainKeys, durationSeconds, finalizeToken);

            _logger.LogInformation(
                "排程执行完成: ScheduleRunId={RunId}, Domain={DomainCount}, 成功={Succeeded}/{Total}, 耗时={Elapsed}ms",
                scheduleRunId, domainKeys.Count, succeededCount, domainKeys.Count, runStopwatch.ElapsedMilliseconds);

            return new SchedulingRunResult
            {
                IsSuccess        = succeededCount == domainKeys.Count,
                ScheduledCount   = domainResults.Where(r => r.IsSuccess).Sum(r => r.ScheduledCount),
                UnscheduledCount = domainResults.Count(r => !r.IsSuccess),
                ElapsedMs        = runStopwatch.ElapsedMilliseconds,
                ErrorMessage     = succeededCount == domainKeys.Count ? null : $"部分 Domain 失败：成功 {succeededCount}/{domainKeys.Count}"
            };
        }
        catch (Exception ex)
        {
            // 落 FAILED（finalizeToken 不受请求取消影响）以便被既有恢复通道接住；
            // 落库失败不得掩盖原始异常（rollback/落库异常掩码是已知陷阱）。
            _logger.LogError(ex, "ScheduleRun {RunId} 执行中断，落 FAILED 供恢复：ElapsedMs={ElapsedMs}",
                scheduleRunId, runStopwatch.ElapsedMilliseconds);
            try
            {
                await _scheduleRunService.FailAsync(
                    scheduleRunId, (int)(runStopwatch.ElapsedMilliseconds / 1000),
                    $"执行中断（未走完收口）：{ex.Message}", CancellationToken.None);
            }
            catch (Exception landEx)
            {
                _logger.LogError(landEx, "ScheduleRun {RunId} 执行中断后落 FAILED 失败（原始异常见上）", scheduleRunId);
            }

            throw;
        }
    }

    /// <summary>O38 判据谓词（聚合与抽样两条查询共用，避免谓词各写一遍后漂移）。</summary>
    private const string StaleCreatedDispatchPredicate = @"
              FROM [TaskDispatch] d
              WHERE d.DispatchStatus = 'CREATED'
                AND d.MESWorkOrderNo IS NOT NULL
                AND d.CreatedAt < @PrevSnapshotAt
                AND NOT EXISTS (SELECT 1 FROM MESWorkOrderSnapshot s
                                WHERE s.ScheduleRunId = @Run AND s.MESWorkOrderNo = d.MESWorkOrderNo)
                AND NOT EXISTS (SELECT 1 FROM MESWorkOrderSnapshot p
                                WHERE p.ScheduleRunId = @PrevRun AND p.MESWorkOrderNo = d.MESWorkOrderNo)";

    /// <summary>O38：上一份可比 MES 快照所属的 Run 及其落库时刻。</summary>
    private sealed class PrevSnapshotRunRow
    {
        public int PrevRun { get; set; }
        public DateTime? PrevSnapshotAt { get; set; }
    }

    /// <summary>O38 聚合读数。</summary>
    private sealed class StaleCreatedDispatchStat
    {
        public int StaleRowCount { get; set; }
        public int WorkOrderCount { get; set; }
        public decimal Qty { get; set; }
    }

    /// <summary>O38 抽样行。</summary>
    private sealed class StaleCreatedDispatchSample
    {
        public string? MESWorkOrderNo { get; set; }
        public string? ProductionInstructionNo { get; set; }
        public int MaterialId { get; set; }
        public string? StageCode { get; set; }
        public decimal Quantity { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// O38（2026-11-07，5号位《五项答复》§六.2）：`CREATED` 下发行的「连续 2 个 Run 快照缺席」出口。
    /// </summary>
    /// <remarks>
    /// 【要解决什么】Stage 闸第三项会把这些 `CREATED` 行的量**永久扣减**（当作"我方已建、MES 证据未到"的欠账）。
    ///   若某张工单其实已被 MES 静默丢弃（取消了却没同步、或建单失败但号已落库），它**永不进快照** ⇒
    ///   那部分量被扣到天荒地老，PI 永远建不出新单。**5号位 明确不自动放**（放掉即双建风险）⇒
    ///   本方只能**出声**，交人工在 MES 侧确认后再走 A5（词表扩 `SNAPSHOT_MISSING` → `FAILED` → 重发）。
    /// 【为什么是「连续 2 个 Run」】单轮缺席是**正常**的——建单与快照之间本就有空窗（见第三项注释）。
    ///   只有「上一个快照里没有、这个快照里仍然没有」才足以说明不是时滞。
    /// 【凭什么是「2 个 Run」而不是「2 次同步」】快照按 `ScheduleRunId` 分区，一个 Run 一份快照 ⇒
    ///   缺席两个连续 Run＝缺席两份独立的快照，这正好也是 Stage 闸扣减发生两次的证据。
    /// 【为什么加 `CreatedAt &lt; @PrevSnapshotAt`】把「本轮/上轮才建、本来就还没轮到快照」的行排除掉；
    ///   不排除的话，今天新建的行当天就会因"两个快照都没有"被误报（它那时还不存在）。
    /// 【为什么只 WARN 不改状态】自动置 `FAILED` 会让 W5 重发 —— 若 MES 侧其实有这张单，就是**双建**。
    ///   本方法**只读不写**，任何状态变更都必须由人工在 MES 侧确认后发起。
    /// </remarks>
    private async Task WarnStaleCreatedDispatchRowsAsync(int scheduleRunId, CancellationToken cancellationToken)
    {
        if (scheduleRunId <= 0)
        {
            return;   // 手动单 PV 入口无 Run 上下文 ⇒ 无"连续两轮"可比
        }

        // 上一个**有快照的** Run：取快照表里比本 Run 小的最大 RunId，顺带取出它那份快照的落库时刻。
        var prev = await _connectionManager.QueryFirstOrDefaultAsync<PrevSnapshotRunRow>(
            @"SELECT TOP 1 ScheduleRunId AS PrevRun, MIN(CreatedAt) AS PrevSnapshotAt
              FROM MESWorkOrderSnapshot
              WHERE ScheduleRunId < @Run
              GROUP BY ScheduleRunId
              ORDER BY ScheduleRunId DESC",
            new { Run = scheduleRunId },
            db: DatabaseId.APS);

        if (prev?.PrevSnapshotAt == null)
        {
            _logger.LogInformation(
                "[O38] Run={Run} 没有可比的上一份 MES 快照（首轮或快照缺失）⇒ 本轮不做「连续两轮缺席」判定",
                scheduleRunId);
            return;
        }

        var prevRun = prev.PrevRun;
        var prevSnapshotAt = prev.PrevSnapshotAt.Value;

        var stat = await _connectionManager.QueryFirstOrDefaultAsync<StaleCreatedDispatchStat>(
            // 2026-11-07：列别名**不能叫 `RowCount`** —— `ROWCOUNT` 是 T-SQL 保留字（`SET ROWCOUNT n`），
            //   `AS RowCount` 报「关键字 'RowCount' 附近有语法错误」，连带把 `WHERE` 的 `AND` 也判错；
            //   真跑 Run 999423 收口时 `WarnStaleCreatedDispatchRowsAsync` 即因此抛错、整个 Run 落 FAILED。
            @"SELECT COUNT(*) AS StaleRowCount,
                     COUNT(DISTINCT d.MESWorkOrderNo) AS WorkOrderCount,
                     ISNULL(SUM(d.Quantity), 0) AS Qty"
            + StaleCreatedDispatchPredicate,
            new { Run = scheduleRunId, PrevRun = prevRun, PrevSnapshotAt = prevSnapshotAt },
            db: DatabaseId.APS);

        if (stat == null || stat.StaleRowCount == 0)
        {
            return;
        }

        var samples = (await _connectionManager.QueryAsync<StaleCreatedDispatchSample>(
            @"SELECT TOP 5 d.MESWorkOrderNo, d.ProductionInstructionNo, d.MaterialId,
                     d.StageCode, d.Quantity, d.CreatedAt"
            + StaleCreatedDispatchPredicate
            + @"
              ORDER BY d.CreatedAt",
            new { Run = scheduleRunId, PrevRun = prevRun, PrevSnapshotAt = prevSnapshotAt },
            db: DatabaseId.APS)).ToList();

        _logger.LogWarning(
            "[O38] CREATED 过期出口：{Rows} 行／{Wos} 张工单（ΣQty={Qty}）连续两个 Run 未出现在 MES 快照" +
            "（Run={Run} / 上一份快照 Run={PrevRun}），**已按第三项持续扣减**。" +
            "⇒ 请人工到 MES 侧确认这些工单是否确实不存在；确认不存在后走 A5（`SNAPSHOT_MISSING` → FAILED → 重发）。" +
            "**本方不自动释放，避免与 MES 双建。** 样本：{Sample}",
            stat.StaleRowCount,
            stat.WorkOrderCount,
            stat.Qty,
            scheduleRunId,
            prevRun,
            string.Join("；", samples.Select(x =>
                $"WO={x.MESWorkOrderNo}/PI={x.ProductionInstructionNo}/物料={x.MaterialId}/Stage={x.StageCode}" +
                $"/Qty={x.Quantity}/建于 {x.CreatedAt:yyyy-MM-dd HH:mm:ss}")));
    }

    /// <summary>
    /// ScheduleRun 终态收口（三态各一次）：全成功 COMPLETED / 部分成功 PARTIAL_SUCCESS / 全失败 FAILED。
    /// 抽为私有方法，供「正常执行完」与「重入全终态跳过」两条路径共用 —— 收口语义必须单点，
    /// 否则两条路径的成功判定口径会各自演化（这正是本次抽 ExecuteRunAsync 要消灭的那类漂移）。
    /// </summary>
    /// <param name="scheduleRunId">目标 ScheduleRun</param>
    /// <param name="totalDomainCount">预期 Domain 总数（= ExpectedDomainKeysJson 元素数，作分母）</param>
    /// <param name="succeededCount">成功 Domain 数</param>
    /// <param name="failedDomainKeys">失败/被阻断的 DomainKey（写入收口原因）</param>
    /// <param name="durationSeconds">本次执行耗时（秒）</param>
    private async Task CloseOutRunAsync(
        int scheduleRunId,
        int totalDomainCount,
        int succeededCount,
        IReadOnlyList<string> failedDomainKeys,
        int durationSeconds,
        CancellationToken cancellationToken)
    {
        // O38（2026-11-07）：收口前先出「CREATED 连续两轮快照缺席」的告警。
        //   放在收口点＝本 Run 的全部 TaskDispatch 已落库之后、且**每 Run 只跑一次**（不在各域重复算）。
        //   只读不写：过期判定交人工，本方不自动释放（5号位 §六.2：放掉即双建风险）。
        await WarnStaleCreatedDispatchRowsAsync(scheduleRunId, cancellationToken);

        if (succeededCount == totalDomainCount)
        {
            await _scheduleRunService.CompleteAsync(scheduleRunId, durationSeconds, cancellationToken);
        }
        else if (succeededCount > 0)
        {
            await _scheduleRunService.PartialSuccessAsync(
                scheduleRunId, durationSeconds,
                $"部分 Domain 失败/被阻断：成功 {succeededCount}/{totalDomainCount}，失败/阻断 Domain={string.Join(",", failedDomainKeys)}",
                cancellationToken);
        }
        else
        {
            await _scheduleRunService.FailAsync(
                scheduleRunId, durationSeconds,
                $"全部 Domain 失败/被阻断（{totalDomainCount} 个），失败/阻断 Domain={string.Join(",", failedDomainKeys)}",
                cancellationToken);
        }
    }

    /// <summary>
    /// 从「预期 Domain 的终态 PlanVersion」反推本轮成效（成功域 / 失败域）。
    /// **收口判定单点**：正常执行完、重入全终态跳过、单域入口复跑三条路径共用 ——
    /// 判定口径若各自演化，「这个 Domain 算不算成功」就会出现第三种说法。
    /// </summary>
    /// <remarks>
    /// 只认**预期 Domain** 的终态 PV（Run 上多余/陈旧 PV 不得据此判定「已跑完」）。
    /// 用 HashSet 在内存里取交集，不用 SQL 的 IN @list —— 规避装载层踩过的 2100 参数上限坑。
    /// 终态集合（冻结词表）：CANDIDATE=白天候选算成功、ACTIVE=夜间算成功且已自动采用、
    /// ARCHIVED=已被同域新版本顶替（曾成功）、FAILED=算失败。
    /// </remarks>
    private async Task<(List<string> Computed, List<string> Failed)> ReadTerminalDomainOutcomesAsync(
        int scheduleRunId,
        IReadOnlyList<string> expectedDomainKeys,
        CancellationToken cancellationToken)
    {
        // ⚠ P0「一个 Run 一个 PV」之后，本 Run 同一 Domain 可能**有多个 PV**（复跑各起一个新 PV，
        //   旧的 ACTIVE 仍留着）。不取最新会同时读到「旧 ACTIVE=成功」与「新 FAILED=失败」，
        //   同一域被同时计入成功集与失败集 ⇒ 成功数 + 失败数 > 预期总数。
        //   ⇒ 每域只认 **Id 最大（最新）** 那一个，与「一个 Domain 的当前成效只有一个」对齐。
        var terminalPlanVersions = (await _connectionManager.QueryAsync<PlanVersionInfoDto>(
            @"SELECT DomainKey, Status FROM (
                  SELECT DomainKey, Status,
                         ROW_NUMBER() OVER (PARTITION BY DomainKey ORDER BY Id DESC) AS rn
                  FROM PlanVersion
                  WHERE SourceScheduleRunId = @RunId
                    AND Status IN ('CANDIDATE', 'ACTIVE', 'ARCHIVED', 'FAILED')
              ) x
              WHERE x.rn = 1",
            new { RunId = scheduleRunId },
            db: DatabaseId.APS)).ToList();

        var expectedDomainSet = new HashSet<string>(expectedDomainKeys, StringComparer.OrdinalIgnoreCase);
        var computed = terminalPlanVersions
            .Where(p => (p.Status == PlanVersionCandidateStatus
                         || p.Status == PlanVersionActiveStatus
                         || p.Status == PlanVersionArchivedStatus)
                        && expectedDomainSet.Contains(p.DomainKey))
            .Select(p => p.DomainKey).Distinct().ToList();
        var failed = terminalPlanVersions
            .Where(p => p.Status == PlanVersionFailedStatus && expectedDomainSet.Contains(p.DomainKey))
            .Select(p => p.DomainKey).Distinct().ToList();

        return (computed, failed);
    }

    /// <summary>
    /// 单域入口的 Run 级收口判定：以「预期 Domain 的终态 PV」为底，再用**本次执行域的实际结果**覆盖该域。
    /// 返回 (预期总数, 成功数, 失败域清单)，交由 <see cref="CloseOutRunAsync"/> 落终态。
    /// </summary>
    /// <remarks>
    /// 为什么要用本次结果覆盖：本域刚跑完，PV 可能因快照/统计失败停在 Running（成功却非终态），
    /// 或因异常中断停在 Created（失败却非终态）—— 两种情形终态 PV 都读不出来，必须以本次实测为准。
    /// </remarks>
    private async Task<(int Total, int Succeeded, List<string> Failed)> ResolveRunOutcomeAsync(
        int scheduleRunId,
        int planVersionId,
        bool executedDomainSucceeded,
        IReadOnlyList<string> expectedDomainKeys,
        CancellationToken cancellationToken)
    {
        var (computedDomains, terminalFailed) =
            await ReadTerminalDomainOutcomesAsync(scheduleRunId, expectedDomainKeys, cancellationToken);

        var succeededSet = new HashSet<string>(computedDomains, StringComparer.OrdinalIgnoreCase);
        var failedSet = new HashSet<string>(terminalFailed, StringComparer.OrdinalIgnoreCase);

        var executedDomainKey = await _connectionManager.QueryFirstOrDefaultAsync<string>(
            "SELECT DomainKey FROM PlanVersion WHERE Id = @Id",
            new { Id = planVersionId },
            db: DatabaseId.APS);

        if (!string.IsNullOrWhiteSpace(executedDomainKey))
        {
            if (executedDomainSucceeded)
            {
                succeededSet.Add(executedDomainKey);
                failedSet.Remove(executedDomainKey);
            }
            else
            {
                failedSet.Add(executedDomainKey);
                succeededSet.Remove(executedDomainKey);
            }
        }

        return (expectedDomainKeys.Count, succeededSet.Count, failedSet.ToList());
    }

    /// <inheritdoc />
    public async Task<SchedulingRunResult> RunSchedulingAsync(int planVersionId, CancellationToken cancellationToken = default)
    {
        // Run 可比性四元组：本入口是单 PV 手动/联调跑批 ⇒ 调用即「一轮」。
        await LogRunComparabilityAsync(planVersionId, strategyProfileVersionId: null, scheduleRunId: null);
        return await ExecuteDomainAsync(planVersionId, scheduleRunId: 0, dataCutoffTime: null, strategyProfileVersionId: null, upstreamResourceBlocks: null, sourcePlanVersionId: null, scope: null, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// 手动/联调入口：显式指定策略包版本（测试/联调场景，绕过 RunSchedulingAutoAsync 的自动领取与版本绑定）。
    /// </summary>
    public async Task<SchedulingRunResult> RunSchedulingAsync(int planVersionId, long strategyProfileVersionId, CancellationToken cancellationToken = default)
    {
        // Run 可比性四元组：本入口策略版本由调用方钉死，正是最能排除「策略换了」的对照跑法。
        await LogRunComparabilityAsync(planVersionId, strategyProfileVersionId, scheduleRunId: null);
        return await ExecuteDomainAsync(planVersionId, scheduleRunId: 0, dataCutoffTime: null, strategyProfileVersionId, upstreamResourceBlocks: null, sourcePlanVersionId: null, scope: null, cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SchedulingRunResult> RunSchedulingAndFinalizeAsync(
        int planVersionId,
        int scheduleRunId,
        long strategyProfileVersionId,
        CancellationToken cancellationToken = default)
    {
        // 反查 Run 冻结基线（§5.3.1）：候选需求侧订单数据源钉 BasePlanVersionId，不随执行时刻 ACTIVE 漂移。
        // StrategyProfileVersionId 以 Run 冻结值优先，入参 strategyProfileVersionId 作回退。
        var run = await _connectionManager.QueryFirstOrDefaultAsync<ScheduleRunQueryDto>(
            @"SELECT Id, DataCutoffTime, StrategyProfileVersionId, BasePlanVersionId, ScopeJson
              FROM ScheduleRun WHERE Id = @Id",
            new { Id = scheduleRunId },
            db: DatabaseId.APS);

        if (run == null)
            throw new InvalidOperationException($"ScheduleRun 不存在: ScheduleRunId={scheduleRunId}");

        // 【2026-10-14 §T-1014q ① ②】同 ExecuteRunAsync：本入口（白天候选 / 联调真跑）进入执行区后也必须让 Run 落终态，
        //   否则 Run 永久 RUNNING（恢复端点只收 FAILED、发令枪只领 FULL_SCHEDULE 且有 Created 域的 Run，两条路都接不着）。
        var finalizeToken = CancellationToken.None;
        try
        {
            // 【P0「一个 Run 一个 PV」】入参 planVersionId 若**已经装过结果**，本方法不再往里写第二轮，
            //   改为新分配一个 PV（详见 EnsureFreshPlanVersionForRunAsync 的说明）。
            //   放在订单装载之前：新 PV 必须自己装一套订单，否则按 planVersionId 分区取需求会取到空集。
            planVersionId = await EnsureFreshPlanVersionForRunAsync(planVersionId, scheduleRunId, cancellationToken);

            // 【续跑刷新 StartedAt】本入口同样是执行起点：FAMILY_X 单域复跑等场景会让 Run 跨天，
            //   不刷则 CompletedAt − StartedAt 与 DurationSeconds 各说各话（实测 29.9h vs 63.9min）。
            await RefreshRunStartedAtAsync(scheduleRunId, cancellationToken);

            // M4：反查 Run 冻结 ScopeJson（白天候选局部重排范围载荷），反序列化为强类型 ScopeJsonV2。
            // null/空 = FULL 语义（旧调用兼容，不校验）；序列化口径 = 字符串码（JsonStringEnumConverter），3号位 写入。
            var scope = string.IsNullOrWhiteSpace(run.ScopeJson)
                ? null
                : JsonSerializer.Deserialize<ScopeJsonV2>(run.ScopeJson);

            // 【白天候选】订单池准备（2026-09-28）：Candidate 壳（RunLifecycleService.CreateCandidateRunAsync）
            // 只建 ScheduleRun + PlanVersion、**不装订单**；而 ExecuteDomainAsync 是按本 planVersionId 装载订单的，
            // 故跑之前必须把活跃订单压进本 Candidate PlanVersion（否则订单为空 → 空结果）。
            // 幂等：sp_SyncOrdersToPartitionTable 有 NOT EXISTS(OrderNo, PlanVersionId) 保护，重复调用只补增量。
            // 失败不静默：记错误日志后继续（下游会因无订单产出空结果，日志可定位）。
            try
            {
                var loadedOrders = await _orderLoadingService.LoadOrdersToPartitionTableAsync(planVersionId, cancellationToken);
                _logger.LogInformation(
                    "白天候选订单池准备：PlanVersionId={PlanVersionId}，本次装载 {Loaded} 条订单",
                    planVersionId, loadedOrders);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "白天候选订单池准备失败：PlanVersionId={PlanVersionId}（候选将因无订单而产出空结果，请检查该 PV 的 DomainKey/DomainDefinition）",
                    planVersionId);
            }

            // 【白天候选】BOM 实时展开接线（2026-10-08，2号位）。
            //   口径 = 5号位 回执 v1.0（2026-09-24）+ 本号位《白天BOM实时展开方案致5号位》§一：
            //     ① 范围 = ScopeJson.OrderTargets[].OrderCanonicalId（本次局部重排的对象集），**只补缺口**
            //        （服务内部先按白天天次查 MES_API_BOM_Request_Detail、已存在的跳过 ⇒ 幂等）；
            //     ② 5号位 加护1：`sp_ExpandBOMRealtime_vNext` 前置校验 `MES_BOM_Edge_RefreshLog` 非 COMPLETED 会 RAISERROR ——
            //        **必须捕获转可解释结果、不得硬失败** ⇒ 此处 try/catch 记日志后继续（Pegging 仍可用夜间存量 BOM）。
            //   ⚠️ 边界：本方法仅为「白天候选」入口；夜间正式排程的 BOM 由 NightlyBatchOrchestrator 走接货链，不经此处。
            try
            {
                var targetOrderCanonicalIds = scope?.OrderTargets
                    .Select(t => t.OrderCanonicalId)
                    .Where(id => id > 0)
                    .Distinct()
                    .ToList() ?? new List<long>();

                if (targetOrderCanonicalIds.Count > 0)
                {
                    var expandResult = await _bomRealtimeExpandService.EnsureExpandedAsync(targetOrderCanonicalIds, cancellationToken);
                    _logger.LogInformation(
                        "白天候选 BOM 实时展开：目标订单={Requested}，补写 Detail={Inserted}，展开={Expanded}，结果行={Rows}，Issue={Issues}",
                        expandResult.RequestedOrders, expandResult.DetailInserted,
                        expandResult.ExpandedOrders, expandResult.ExpandedRowCount, expandResult.Issues.Count);
                    if (expandResult.Issues.Count > 0)
                        _logger.LogWarning("白天候选 BOM 实时展开 Issue（不阻断）：{Issues}", string.Join(" | ", expandResult.Issues));
                }
                else
                {
                    _logger.LogInformation(
                        "白天候选 BOM 实时展开：ScopeJson.OrderTargets 为空（非局部重排场景），跳过实时展开、沿用夜间已展开 BOM。PlanVersionId={PlanVersionId}",
                        planVersionId);
                }
            }
            catch (Exception ex)
            {
                // 加护1：不得硬失败 —— 实时展开失败不阻断候选（沿用夜间存量 BOM），记错便于定位。
                _logger.LogError(ex, "白天候选 BOM 实时展开失败（不阻断候选，沿用夜间已展开 BOM）。PlanVersionId={PlanVersionId}", planVersionId);
            }

            // Run 可比性四元组：白天候选/联调真跑，本 Run 一轮一次。
            await LogRunComparabilityAsync(
                planVersionId, run.StrategyProfileVersionId ?? strategyProfileVersionId, scheduleRunId);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var result = await ExecuteDomainAsync(
                planVersionId, scheduleRunId, run.DataCutoffTime, run.StrategyProfileVersionId ?? strategyProfileVersionId,
                upstreamResourceBlocks: null, sourcePlanVersionId: run.BasePlanVersionId, scope, cancellationToken);
            stopwatch.Stop();
            var durationSeconds = (int)(stopwatch.ElapsedMilliseconds / 1000);

            // 【2026-11-02 修·红线「失败不伪成功」】单域入口**不得替整轮背书**。
            //   本方法只执行一个 Domain（入参 planVersionId 对应的那一个），原实现无论 Run 上冻结了几个
            //   预期 Domain，都直接 Complete/Fail 整个 ScheduleRun。实证：Run 999334 冻结 12 个预期 Domain，
            //   2026-10-31 16:55:40 经 FAMILY_X 单域复跑被置 COMPLETED —— 而同一 Run 下 8 个 Domain 的 PV
            //   是 FAILED（BJ_FAMILY_Y / FAMILY_LGJ / FAMILY_MJ / FAMILY_VALVE / FAMILY_WKQ / FAMILY_XC /
            //   JUAN1023 / TJ_FAMILY_Y），只有 4 个 ACTIVE。
            //   ⇒ Run 级终态改为「按预期 Domain 的终态 PV 重算 + 本次执行域覆盖」，与 ExecuteRunAsync 走
            //   **同一个** CloseOutRunAsync；单域 Run（预期集合 ≤1，白天候选路径的常态）行为不变。
            var expectedDomainKeys = await _scheduleRunService.GetExpectedDomainKeysAsync(scheduleRunId, finalizeToken);
            if (expectedDomainKeys.Count <= 1)
            {
                if (result.IsSuccess)
                {
                    await _scheduleRunService.CompleteAsync(scheduleRunId, durationSeconds, finalizeToken);
                }
                else
                {
                    await _scheduleRunService.FailAsync(scheduleRunId, durationSeconds, result.ErrorMessage ?? "排程失败", finalizeToken);
                }
            }
            else
            {
                var (totalDomains, succeededDomains, failedDomains) = await ResolveRunOutcomeAsync(
                    scheduleRunId, planVersionId, result.IsSuccess, expectedDomainKeys, finalizeToken);
                await CloseOutRunAsync(scheduleRunId, totalDomains, succeededDomains, failedDomains, durationSeconds, finalizeToken);

                _logger.LogWarning(
                    "单域入口复跑已按整轮重算 Run 终态（不再单域即 COMPLETED）：ScheduleRunId={RunId}, PlanVersionId={PlanVersionId}, 本次域成功={Success}, 整轮成功 {Succeeded}/{Total}, 失败域={Failed}",
                    scheduleRunId, planVersionId, result.IsSuccess, succeededDomains, totalDomains,
                    failedDomains.Count == 0 ? "无" : string.Join(",", failedDomains));
            }

            _logger.LogInformation(
                "白天候选执行收口: ScheduleRunId={RunId}, PlanVersionId={PlanVersionId}, IsSuccess={Success}, 耗时={Elapsed}ms",
                scheduleRunId, planVersionId, result.IsSuccess, stopwatch.ElapsedMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "候选/联调运行执行中断，落 FAILED 供恢复：ScheduleRunId={RunId} PlanVersionId={PlanVersionId}",
                scheduleRunId, planVersionId);
            try
            {
                // 同上方成功路径：单域 Run 落 FAILED；多域 Run 只把**本次执行域**记为失败、
                //   其余域按其终态 PV 重算 —— 否则一个域抛异常会把整轮写成「全失败」，
                //   与「失败不伪成功」同源的反向伪判（伪失败）同样不可接受。
                var expectedDomainKeys = await _scheduleRunService.GetExpectedDomainKeysAsync(scheduleRunId, CancellationToken.None);
                if (expectedDomainKeys.Count <= 1)
                {
                    await _scheduleRunService.FailAsync(
                        scheduleRunId, 0, $"执行中断（未走完收口）：{ex.Message}", CancellationToken.None);
                }
                else
                {
                    var (totalDomains, succeededDomains, failedDomains) = await ResolveRunOutcomeAsync(
                        scheduleRunId, planVersionId, executedDomainSucceeded: false, expectedDomainKeys, CancellationToken.None);
                    await CloseOutRunAsync(scheduleRunId, totalDomains, succeededDomains, failedDomains, 0, CancellationToken.None);

                    _logger.LogWarning(
                        "单域入口中断已按整轮重算 Run 终态：ScheduleRunId={RunId}, PlanVersionId={PlanVersionId}, 整轮成功 {Succeeded}/{Total}, 失败域={Failed}",
                        scheduleRunId, planVersionId, succeededDomains, totalDomains,
                        failedDomains.Count == 0 ? "无" : string.Join(",", failedDomains));
                }
            }
            catch (Exception landEx)
            {
                _logger.LogError(landEx, "落终态失败（原始异常见上）：ScheduleRunId={RunId}", scheduleRunId);
            }

            throw;
        }
    }

    /// <summary>
    /// **P0「一个 Run 一个 PV」**：单域/手动入口收到的 <paramref name="planVersionId"/> 若**已经装过结果**，
    /// 不再往里写第二轮，改为**新分配一个 PV** 并把本次结果写进新 PV；旧 PV 原样保留作历史。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **为什么必须这么做**（不是洁癖，是三条实测后果）：
    /// ① 复用 PV ⇒ 同一 PV 上攒多轮产物，`[Task]` / `Pegging` / `ScheduleExplanationFact` 读数＝本轮＋历次残留，
    ///    「本轮产出多少」无法读表得出（PV 190 实测攒 374,022 行、跨多轮叠加），排障与验收失去唯一口径；
    /// ② 为压住①，`ExecuteDomainAsync` 跑前有六条无条件 DELETE —— 其中 `ScheduleExplanationFact` / `ExplainTrace`
    ///    两条被 0号位 2026-10-31《致2号位_四项清理提议一次性执行回复》§二「特别暂停」明确禁止上线，
    ///    **新 PV 天然是空的、不需要清理**，本方法是撤掉那两条的前提（方案《写入表清理原子安全门禁与无损容量方案》§3.2/§3.4）；
    /// ③ 复用还会**覆盖旧正式结果**：旧 PV 若是当前 ACTIVE，第二轮直接写进去，成功的旧结果与新结果混在同一 PV 上。
    ///    新 PV 起于 `BUILDING`，旧 ACTIVE 在新 PV 算完并被采用之前保持不变 ⇒ 与 0号位「新的 Run 对应新的 PV，
    ///    不覆盖旧正式结果」一致。
    /// </para>
    /// <para>
    /// **判「已经装过结果」的两条充要条件**（任一成立即视为用过）：
    /// ① 状态已离开 `BUILDING`（`Computing` 卡住的崩溃轮同样算用过 —— 那一轮可能已写了一半）；
    /// ② 已有 `[Task]` 行（防「状态没来得及改、产物已落库」）。
    /// 只判状态不判行数会漏②；只判行数会漏「跑失败后无产物但状态已被改为 FAILED」的本轮 —— 两者都要。
    /// </para>
    /// <para>
    /// **没被用过的 PV 原样返回**：白天候选入口 `RunLifecycleService` 传进来的就是刚建的 `BUILDING` 空壳，
    /// 走的就是这一支，行为与本改动前完全一致。
    /// </para>
    /// <para>
    /// ⚠ **不在这里做「同 PV 清理」**：本方法只负责换一个干净的靶子，不删任何历史行。
    /// </para>
    /// </remarks>
    private async Task<int> EnsureFreshPlanVersionForRunAsync(
        int planVersionId,
        int scheduleRunId,
        CancellationToken cancellationToken)
    {
        var source = await _connectionManager.QueryFirstOrDefaultAsync<PlanVersionReuseProbe>(
            @"SELECT pv.Id, pv.VersionCode, pv.VersionCategory, pv.DomainKey,
                     pv.PlanHorizonStart, pv.PlanHorizonEnd, pv.ComputeMode, pv.BatchNo, pv.Status,
                     CASE WHEN pv.Status <> 'BUILDING' THEN 1
                          WHEN EXISTS (SELECT 1 FROM [Task] t WHERE t.PlanVersionId = pv.Id) THEN 1
                          ELSE 0 END AS IsAlreadyUsed
              FROM PlanVersion pv
              WHERE pv.Id = @Id",
            new { Id = planVersionId },
            db: DatabaseId.APS);

        // 查不到 PV 不是本方法的责任：交给 ExecuteDomainAsync 按原路径抛「计划版本不存在」。
        if (source == null || source.IsAlreadyUsed != 1)
            return planVersionId;

        // VersionCode 上限 nvarchar(100)：前缀 4 + 域名截断 40 + 分隔 1 + Run 号 10 + 分隔 1 + 时间戳 17 = 73。
        var domainPart = string.IsNullOrWhiteSpace(source.DomainKey)
            ? "NODOMAIN"
            : (source.DomainKey.Length > 40 ? source.DomainKey[..40] : source.DomainKey);

        // Run 号 + 毫秒时间戳：既满足方案 §3.2「VersionCode 带 Run 号或尝试序号」，又不会与旧编码撞唯一键。
        var versionCode = $"PVR_{domainPart}_{scheduleRunId}_{DateTime.Now:yyyyMMddHHmmssfff}";

        var newPlanVersionId = await _connectionManager.QueryFirstOrDefaultAsync<int>(
            @"INSERT INTO PlanVersion (
                  VersionCode, VersionCategory, PlanHorizonStart, PlanHorizonEnd,
                  ComputeMode, Status, BatchNo, SourceScheduleRunId, DomainKey, CreatedBy, CreatedAt)
              OUTPUT INSERTED.Id
              VALUES (
                  @VersionCode, @VersionCategory, @PlanHorizonStart, @PlanHorizonEnd,
                  @ComputeMode, 'BUILDING', @BatchNo, @SourceScheduleRunId, @DomainKey, @CreatedBy, GETDATE());",
            new
            {
                VersionCode = versionCode,
                VersionCategory = source.VersionCategory,
                source.PlanHorizonStart,
                source.PlanHorizonEnd,
                source.ComputeMode,
                source.BatchNo,
                SourceScheduleRunId = scheduleRunId,
                source.DomainKey,
                CreatedBy = "P0-FRESH-PV",
            },
            db: DatabaseId.APS);

        if (newPlanVersionId <= 0)
            throw new InvalidOperationException(
                $"P0 新分配 PlanVersion 未返回 Id：来源 PlanVersionId={planVersionId}, RunId={scheduleRunId}, VersionCode={versionCode}");

        _logger.LogWarning(
            "P0「一个 Run 一个 PV」：来源 PV 已有结果，本次改为写新 PV。" +
            "来源 PlanVersionId={SourcePvId}({SourceCode}, Status={SourceStatus}) → 新 PlanVersionId={NewPvId}({NewCode}), RunId={RunId}, Domain={DomainKey}",
            planVersionId, source.VersionCode, source.Status, newPlanVersionId, versionCode, scheduleRunId, source.DomainKey);

        return newPlanVersionId;
    }

    /// <summary>
    /// **续跑刷新起点**：每次进入执行区，把 `ScheduleRun.StartedAt` 刷成本次尝试的开始时刻（库端时钟）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **为什么必须刷**（2026-10-31 实测 `ScheduleRun 999334`）：该 Run 是 10-30 起跑、10-31 续跑，
    /// `StartedAt` 停在 10-30 11:01:16，`CompletedAt` 写的是 10-31 16:55:40
    /// ⇒ 两列相减 **107,664 s（29.9 h）**，而 `DurationSeconds` / 日志 `Domain 性能指标 … Total=`
    /// 都是 **3,831 s（63.9 min）**。同一件事三个读数两个口径，读表的人必然被带偏。
    /// </para>
    /// <para>
    /// **口径定义**：`StartedAt` 取「本次执行尝试开始」，不是「本 Run 首次创建」（创建时刻在 `CreatedAt`，
    /// 两者都要留才读得出「这个 Run 一共拖了多久、最后这轮真正算多久」）。因此 `CompletedAt − StartedAt`
    /// 应当 ≈ `DurationSeconds`，差值与两个 UPDATE 之间的网络/收尾开销同量级（秒级）。
    /// </para>
    /// <para>
    /// **只刷起点、不动状态**：状态机仍由 `ScheduleRunService` 的三态收口独占，本方法不得写 `Status`。
    /// </para>
    /// <para>
    /// ⚠ 这是**留痕字段**，不是闸门字段 —— 无任何逻辑分支读 `StartedAt` 做判定，故刷新它不会改变跑批行为。
    /// </para>
    /// </remarks>
    private async Task RefreshRunStartedAtAsync(int scheduleRunId, CancellationToken cancellationToken)
    {
        if (scheduleRunId <= 0)
            return;

        try
        {
            await _connectionManager.ExecuteAsync(
                "UPDATE ScheduleRun SET StartedAt = GETDATE() WHERE Id = @Id",
                new { Id = scheduleRunId },
                db: DatabaseId.APS);
        }
        catch (Exception ex)
        {
            // 留痕字段刷新失败不得升级为跑批失败（与 LogRunComparabilityAsync 同一纪律）。
            _logger.LogWarning(ex, "刷新 ScheduleRun.StartedAt 失败（不阻断跑批）：ScheduleRunId={RunId}", scheduleRunId);
        }
    }

    /// <summary><see cref="EnsureFreshPlanVersionForRunAsync"/> 的复用探测读数。</summary>
    private sealed class PlanVersionReuseProbe
    {
        public int Id { get; set; }
        public string VersionCode { get; set; } = string.Empty;
        public string? VersionCategory { get; set; }
        public string? DomainKey { get; set; }
        public DateTime PlanHorizonStart { get; set; }
        public DateTime PlanHorizonEnd { get; set; }
        public string? ComputeMode { get; set; }
        public string? BatchNo { get; set; }
        public string? Status { get; set; }
        public int IsAlreadyUsed { get; set; }
    }

    /// <summary>
    /// Run 可比性四元组的一行原始读数（表名 / 行数 / 最后写入时点）。
    /// </summary>
    private sealed record RunComparabilityRow(string TableName, long RowCount, DateTime? LastWriteAt);

    /// <summary>
    /// **Run 可比性四元组** —— 每次跑批落一条，供事后排除「输入换了」这个变量。
    ///
    /// <para>
    /// 请求方：1号位 2026-10-27《量纲与行号双认账 · Run 可比性四元组与时点表》§4.4 逐字：
    /// 「请在每次跑批的第一行落一条『Run 可比性四元组』：`PlanVersionId, StrategyVersionId,
    /// 各输入表 MAX(最后写入时点), 关键表行数`」。
    /// </para>
    ///
    /// <para>
    /// **为什么必须记**（不是理论，是他当天用 20 分钟一次完整跑批换来的）：
    /// ① 主档 / 在途 / 部门上下文 / 库存均可被**手工重同步**，换过之后两次 Run 的结论不可叠；
    /// ② `ScheduleRun.StrategyProfileVersionId` 在部分入口是 `ResolveDefaultStrategyVersionIdAsync`
    ///    **运行时反查**的结果，不是钉死的 —— 两次 Run 之间默认版本一变，等于在比两套策略；
    /// ③ 有这一行，排除变量的成本从「一次完整跑批」降到「看一眼日志」。
    /// </para>
    ///
    /// <para>
    /// **口径约束（重要）**：整轮**只算一次**，**不得按 Domain 重复算** —— FULL 跑批有 14 个 Domain，
    /// 按域算会把 13 张表的 `COUNT(*)` 放大 14 倍（`APS_BOM_RAW` 419 万行），等于把性能问题重新引进来。
    /// 所以本方法只在「Run 级入口」调用（夜间 `ExecuteRunAsync` / 白天候选 `RunSchedulingAndFinalizeAsync`
    /// / 手动联调两个 `RunSchedulingAsync` 重载），不在 `ExecuteDomainAsync` 里调。
    /// </para>
    ///
    /// <para>
    /// **失败不阻断**：本行纯留痕，取不到就记 WARN 后照常跑批 —— 留痕失败不得升级为跑批失败。
    /// </para>
    ///
    /// <para>
    /// 表的选取与「最后写入列」逐表实查过（不用猜）：`InventoryBalance.LastUpdatedAt` /
    /// `Material.UpdatedAt` / `MaterialMapping.UpdatedAt` / `MaterialSupplyContext.UpdatedAt` /
    /// `InventoryFact_PiXc.CapturedAt` / `Order.UpdatedAt` / `RoutingDependency.UpdatedAt` /
    /// `SupplyFact_Pipeline.SyncedAt` / `MaterialStageDeptContext.UpdatedAt` / `Order_Canonical.UpdatedAt` /
    /// `APS_BOM_RAW.SyncedAt` / `APS_BOM_STAGE_PATH_RAW.SyncedAt` / `Factory.UpdatedAt`。
    /// </para>
    ///
    /// <para>
    /// ⚠ **别动 `[RowCount]` 的方括号**：`ROWCOUNT` 是 T-SQL 关键字，写成裸 `AS RowCount` 会报
    /// `Msg 156 关键字 'RowCount' 附近有语法错误`；而本方法**失败即 WARN 不阻断**，
    /// 该错误会以「每次都记一条 WARN、永远不出数」的形式长期潜伏 ⇒ 方括号是必需的，不是风格。
    /// （2026-10-27 实跑本 SQL 时踩到，14 行已逐行回数确认。）
    /// </para>
    ///
    /// <para>
    /// ⚠ 额外一行 `Order(本PV)`：`[Order]` 是按 `PlanVersionId` 分区的，全表行数变动**可能来自别的 PV**，
    /// 不代表本 Run 的需求输入变了 ⇒ 本 PV 的行数单列，才是真正可比的那个数。
    /// </para>
    ///
    /// <para>
    /// **无取消令牌**：`DatabaseConnectionManager.QueryAsync` 没有接收 <see cref="CancellationToken"/> 的重载，
    /// 传进来也丢不掉，故本方法不声明该形参（不做一个看起来可取消、实际不可取消的签名）。
    /// </para>
    /// </summary>
    private async Task LogRunComparabilityAsync(
        int? planVersionId,
        long? strategyProfileVersionId,
        int? scheduleRunId)
    {
        try
        {
            var rows = (await _connectionManager.QueryAsync<RunComparabilityRow>(
                @"SELECT N'InventoryBalance' AS TableName, COUNT_BIG(*) AS [RowCount], MAX(LastUpdatedAt) AS LastWriteAt FROM dbo.InventoryBalance
                  UNION ALL SELECT N'Material',                COUNT_BIG(*), MAX(UpdatedAt)  FROM dbo.Material
                  UNION ALL SELECT N'MaterialMapping',         COUNT_BIG(*), MAX(UpdatedAt)  FROM dbo.MaterialMapping
                  UNION ALL SELECT N'MaterialSupplyContext',   COUNT_BIG(*), MAX(UpdatedAt)  FROM dbo.MaterialSupplyContext
                  UNION ALL SELECT N'InventoryFact_PiXc',      COUNT_BIG(*), MAX(CapturedAt) FROM dbo.InventoryFact_PiXc
                  UNION ALL SELECT N'Order(全表)',              COUNT_BIG(*), MAX(UpdatedAt)  FROM dbo.[Order]
                  UNION ALL SELECT N'Order(本PV)',              COUNT_BIG(*), MAX(UpdatedAt)  FROM dbo.[Order] WHERE PlanVersionId = @PlanVersionId
                  UNION ALL SELECT N'RoutingDependency',       COUNT_BIG(*), MAX(UpdatedAt)  FROM dbo.RoutingDependency
                  UNION ALL SELECT N'SupplyFact_Pipeline',     COUNT_BIG(*), MAX(SyncedAt)   FROM dbo.SupplyFact_Pipeline
                  UNION ALL SELECT N'MaterialStageDeptContext',COUNT_BIG(*), MAX(UpdatedAt)  FROM dbo.MaterialStageDeptContext
                  UNION ALL SELECT N'Order_Canonical',         COUNT_BIG(*), MAX(UpdatedAt)  FROM dbo.Order_Canonical
                  UNION ALL SELECT N'APS_BOM_RAW',             COUNT_BIG(*), MAX(SyncedAt)   FROM dbo.APS_BOM_RAW
                  UNION ALL SELECT N'APS_BOM_STAGE_PATH_RAW',  COUNT_BIG(*), MAX(SyncedAt)   FROM dbo.APS_BOM_STAGE_PATH_RAW
                  UNION ALL SELECT N'Factory',                 COUNT_BIG(*), MAX(UpdatedAt)  FROM dbo.Factory",
                new { PlanVersionId = planVersionId ?? -1 },
                db: DatabaseId.APS,
                commandTimeout: 120)).ToList();

            // 一行落全：前缀固定 `[RunComparability]`，事后 grep 这一个串即可取出历次跑批的指纹。
            // 表序固定（上面的 UNION ALL 顺序），同一环境两次跑批可直接逐字比。
            // 夜间批是多 Domain（各自一个 PV），没有单一 PlanVersionId ⇒ 记「整批」，此时 `Order(本PV)` 恒 0 是**预期**，不是缺数。
            var fingerprint = string.Join("; ", rows.Select(r =>
                $"{r.TableName}={r.RowCount}@{r.LastWriteAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "NULL"}"));

            _logger.LogInformation(
                "[RunComparability] PlanVersionId={PlanVersionId} | StrategyVersionId={StrategyVersionId} | ScheduleRunId={RunId} | 输入表={TableCount} | 指纹: {Fingerprint}",
                planVersionId?.ToString() ?? "整批(多Domain)",
                strategyProfileVersionId?.ToString() ?? "NULL",
                scheduleRunId?.ToString() ?? "无Run行(手动/联调入口)",
                rows.Count,
                fingerprint);
        }
        catch (Exception ex)
        {
            // 留痕失败不得阻断跑批：记 WARN，继续。
            _logger.LogWarning(ex,
                "[RunComparability] 输入指纹取数失败（不阻断跑批）: PlanVersionId={PlanVersionId}, StrategyVersionId={StrategyVersionId}, ScheduleRunId={RunId}",
                planVersionId, strategyProfileVersionId, scheduleRunId);
        }
    }

    /// <summary>
    /// 单 Domain 执行单元（分域求解边界）。
    /// 职责：装载本 Domain PlanVersion 的沙盘 → Pegging → 1号位求解 → 结果落盘 → PlanVersion 终态（Computed/ComputeFailed）。
    /// 边界：不处理 ScheduleRun 终态（COMPLETED/PARTIAL_SUCCESS/FAILED），终态由 RunSchedulingAutoAsync 逐 Domain 循环后统一收口；
    ///       失败时返回 IsSuccess=false（不向上抛），供上层做「上游失败阻断」传播。
    /// </summary>
    private async Task<SchedulingRunResult> ExecuteDomainAsync(
        int planVersionId,
        int scheduleRunId,
        DateTime? dataCutoffTime,
        long? strategyProfileVersionId,
        IReadOnlyList<ResourceBlock>? upstreamResourceBlocks,
        int? sourcePlanVersionId = null,
        ScopeJsonV2? scope = null,
        CancellationToken cancellationToken = default,
        IDictionary<string, decimal>? crossDomainPhysicalConsumed = null)
    {
        _logger.LogInformation("排程开始: PlanVersionId={PlanVersionId}, ScheduleRunId={RunId}",
            planVersionId, scheduleRunId);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // 获取计划版本信息（含 VersionCategory 与来源 Run 的 RunType —— 后者是「夜间自动采用」的唯一判据）
            var planVersion = await _connectionManager.QueryFirstOrDefaultAsync<PlanVersionInfoDto>(
                @"SELECT pv.Id, pv.VersionCode, pv.DomainKey, pv.PlanHorizonStart, pv.PlanHorizonEnd,
                         pv.VersionCategory, sr.RunType
                  FROM PlanVersion pv
                  LEFT JOIN ScheduleRun sr ON sr.Id = pv.SourceScheduleRunId
                  WHERE pv.Id = @Id",
                new { Id = planVersionId },
                db: DatabaseId.APS);

            if (planVersion == null)
                throw new InvalidOperationException($"计划版本不存在: PlanVersionId={planVersionId}");

            // 状态 → Computing（冻结词表里 BUILDING 期间的子状态）。
            // ⚠️ 这一步同时是**发令枪领取闸门**的关闸动作：本域一旦变成 Computing，
            //    RunSchedulingAutoAsync 的 `EXISTS(... Status='BUILDING')` 立即转假，别的 Worker 不会再领走同一 Run。
            //    ⇒ 该值不可省、不可与 BUILDING 合并（合并后已开跑的 Run 的 EXISTS 恒真 ⇒ 重复领取并重跑）。
            await _connectionManager.ExecuteAsync(
                "UPDATE PlanVersion SET Status = 'Computing' WHERE Id = @Id",
                new { Id = planVersionId },
                db: DatabaseId.APS);

            // 幂等清理：删除上次运行留下的**计算产物**（按 FK 依赖顺序）。
            // 四张＝本 Run 自身的排程产物；`ProductionInstructionPositionSnapshot` 归 5号位，不在本事务。
            //
            // ⚠️ `ScheduleExplanationFact` / `ExplainTrace` 两条**已于 2026-11-02 撤除**（不再清理）。
            //    依据：0号位 2026-10-31《四项清理提议一次性执行回复》§二「特别暂停」——
            //    「这两条**不得直接上线**，也不得以『与原四条同事务』作为安全证明……本文不授权任何新增无条件 DELETE」；
            //    0号位 2026-11-02 v3.1 §3.3 重申：「新增两条 `DELETE ... WHERE PlanVersionId=@Id`
            //    未获生产执行许可；**应随 P0 从上线版本移除/禁用**」。
            //    否决的不是保留期数值，是这两条语句本身：08 DDL 允许 `ScheduleExplanationFact` 把跨 Run 风险
            //    挂在未参加本次 Run 的下游现行 ACTIVE PV 上，按 `PlanVersionId` 不分来源清会抹掉合法历史风险；
            //    01 §20 还要求 FAILED 历史永久保留、重算新建 Run。
            //
            //    ⇒ 立即可见的代价（已知、已接受）：同一 PV 重跑后这两张表的读数＝本轮＋历次残留，
            //      「本轮产出多少」不能直接从表里读出来（实测 PV 190 攒 374,022 行、跨多轮叠加）。
            //      **替代方向不是再加 DELETE**，而是 P0「一个 Run 一个 PV」：新 Run 起新 PV，天然不叠加。
            //      若日后 0号位 给出带来源约束的方案（候选 `PlanVersionId=@Id AND ScheduleRunId=@RunId`，
            //      且须先证明 `ScheduleRunId IS NULL` 的来源不明记录不被误删），再按其方案加回。
            await _connectionManager.ExecuteAsync(
                @"DELETE FROM PeggingSupplyAllocation   WHERE PlanVersionId = @Id;
                  DELETE FROM [Pegging]                 WHERE PlanVersionId = @Id;
                  DELETE FROM AllocationTaskShare       WHERE PlanVersionId = @Id;
                  DELETE FROM [Task]                    WHERE PlanVersionId = @Id;",
                new { Id = planVersionId },
                db: DatabaseId.APS);

            // ═══════════════════════════════════════════
            // 阶段1: 装载排程沙盘
            // ═══════════════════════════════════════════
            _logger.LogInformation("[{PlanVersionId}] 阶段1: 装载排程沙盘", planVersionId);
            var contextStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var context = await LoadSchedulingContextAsync(planVersion, scheduleRunId, cancellationToken);
            context.StrategyProfileVersionId = strategyProfileVersionId;
            // P0-03：DataCutoffTime 是本 Run 冻结事实时点（ScheduleRun.DataCutoffTime），落到 context 供下游
            // Pegging 供给快照 + DomainSolveRequest.DataCutoffTime 使用；手动入口为 null 时下游回退 Now。
            context.DataCutoffTime = dataCutoffTime;
            if (strategyProfileVersionId.HasValue)
                await LoadStrategyConfigAsync(context, strategyProfileVersionId.Value, cancellationToken);
            // 1.5b 管道供给：**必须放在策略配置之后** —— 三级链的 DefaultPurchaseLt / ArrivalToUsableOffsets
            //   来自冻结策略快照，快照未装载时算不出 AvailableTime（见 LoadPipelineSupplyAsync 注释）。
            await LoadPipelineSupplyAsync(context, cancellationToken);
            contextStopwatch.Stop();
            _logger.LogInformation(
                "[{PlanVersionId}] 沙盘装载完成: Tasks={TaskCount}, Resources={ResourceCount}, MESKeys={MESCount}, 耗时={ContextMs}ms",
                planVersionId, context.Tasks.Count, context.Resources.Count, context.MESRemainingQty.Count, contextStopwatch.ElapsedMilliseconds);

            // ═══════════════════════════════════════════
            // 阶段2: Pegging — 供需挂钩 + 冻结区保护
            // ═══════════════════════════════════════════
            _logger.LogInformation("[{PlanVersionId}] 阶段2: Pegging 供需挂钩", planVersionId);
            // 事项四（§5.3.1 基线快照）：候选需求侧订单数据源钉 BasePlanVersionId（sourcePlanVersionId），
            // 不随执行时刻 ACTIVE 漂移；FULL 场景 sourcePlanVersionId=null，回退为自身 planVersionId。
            var demandSourcePlanVersionId = sourcePlanVersionId ?? planVersionId;
            // 需求根集合 = 本域订单 − 厂间出荷指示（SH）行。
            // 冻结正文逐字（Pegging v1.6 §4.1/§12.1；业务基线 v1.13 §40.1）：
            //   「SH订单本身不作为普通顶层Demand重复进入第一层」「它在目标侧作为SH单一Supply」。
            // 排除判据与供给侧入池判据**同源**（`LoadInterFactoryShipmentsAsync` 的谓词：
            //   `CustomerSegment=N'INTER_FACTORY' AND OrderType='SALES_ORDER'`），避免两侧口径漂移。
            var allOrderIds = (await _connectionManager.QueryAsync<long>(
                $@"SELECT Id FROM [Order]
                  WHERE PlanVersionId = @PlanVersionId
                    AND NOT (ISNULL(OrderType, N'') = N'SALES_ORDER' AND ISNULL(CustomerSegment, N'') = N'{CustomerSegmentCodes.InterFactory}')",
                new { PlanVersionId = demandSourcePlanVersionId },
                db: DatabaseId.APS)).ToList();

            // M4：OrderTargets[].OrderCanonicalId ↔ 本 Run 装载订单集（demand 侧 [Order].OrderCanonicalId）一致性校验。
            // 任一目标订单不在装载集内 → fail-closed 拒绝执行，不静默降级。
            if (scope?.OrderTargets is { Count: > 0 } orderTargets)
            {
                var loadedOrderCanonicalIds = (await _connectionManager.QueryAsync<long>(
                    "SELECT OrderCanonicalId FROM [Order] WHERE PlanVersionId = @PlanVersionId AND OrderCanonicalId IS NOT NULL",
                    new { PlanVersionId = demandSourcePlanVersionId },
                    db: DatabaseId.APS)).ToHashSet();
                ScopeJsonV2Validator.ValidateOrderTargetsConsistency(orderTargets, loadedOrderCanonicalIds);
            }

            // P0-04：Candidate 判定 = 本次 Run 冻结了 BasePlanVersionId（sourcePlanVersionId != null）。
            // Candidate 场景装载「其它 Domain 当前 ACTIVE 在共享 Resource 上的占用」作为 ExternalDomainResourceBlocks（§11）。
            var isCandidate = sourcePlanVersionId.HasValue;
            IReadOnlyList<ResourceBlock>? externalDomainResourceBlocks = null;
            IDictionary<string, decimal>? externalDomainInventoryOccupation = null;
            if (isCandidate)
            {
                externalDomainResourceBlocks = await LoadExternalDomainResourceBlocksAsync(
                    planVersion.DomainKey, context, sourcePlanVersionId!.Value, planVersionId, cancellationToken);
                _logger.LogInformation(
                    "[{PlanVersionId}] Candidate 外部 Domain 占用块: {Count} 条", planVersionId, externalDomainResourceBlocks.Count);

                // R03-02：候选不得释放其它 Domain 现行 ACTIVE 的**库存**占用。
                // 与上一行同源同谓词（Status='ACTIVE'、排除自身与 Base、排除本域），只是取的是量不是时间。
                externalDomainInventoryOccupation = await LoadExternalDomainInventoryOccupationAsync(
                    planVersion.DomainKey, sourcePlanVersionId!.Value, planVersionId, cancellationToken);
                _logger.LogInformation(
                    "[{PlanVersionId}] Candidate 外部 Domain 库存占用: 聚合键 {KeyCount} 个 / Σ{Qty}",
                    planVersionId, externalDomainInventoryOccupation.Count,
                    externalDomainInventoryOccupation.Values.Sum());
            }

            var peggingRequest = BuildPeggingRequest(
                planVersionId, planVersion.DomainKey, allOrderIds, context, upstreamResourceBlocks,
                isCandidate, sourcePlanVersionId, externalDomainResourceBlocks,
                crossDomainPhysicalConsumed, externalDomainInventoryOccupation);
            var peggingResults = (await _peggingOrchestrator.ExecuteBatchPeggingWorkflowAsync(
                peggingRequest, cancellationToken)).ToList();

            var peggingFailed = peggingResults.Where(r => !r.IsSuccess).ToList();
            if (peggingFailed.Count > 0)
            {
                _logger.LogWarning(
                    "[{PlanVersionId}] Pegging 部分失败: {FailCount}/{Total}，继续排程",
                    planVersionId, peggingFailed.Count, peggingResults.Count);
                foreach (var f in peggingFailed)
                    _logger.LogWarning("[{PlanVersionId}] Pegging 失败 OrderId={OrderId}: {Err}",
                        planVersionId, f.OrderId, f.ErrorMessage);
            }

            var totalTasks   = peggingResults.Sum(r => r.GeneratedTasks.Count);
            var totalAlloc   = peggingResults.Sum(r => r.SupplyAllocationCount);
            var logicalProductionDemandCount = peggingResults.Sum(r => r.Voucher?.LogicalProductionDemands?.Count ?? 0);
            var peggingMs    = peggingResults.Sum(r => r.PeggingMs);
            var solverMs     = peggingResults.Sum(r => r.SolverMs);
            var persistMs    = peggingResults.Sum(r => r.PersistMs);
            _logger.LogInformation(
                "[{PlanVersionId}] Pegging 完成: 生成Task={Tasks}, 分配={Alloc}",
                planVersionId, totalTasks, totalAlloc);

            // V1.2：Pegging已完成Task生成并持久化，无需回填context或再次调用Solver
            // 阶段3已在PeggingOrchestrator内部完成：LogicalProductionDemands → TaskDrafts → SolveAsync → FinalTasks
            _logger.LogInformation("[{PlanVersionId}] 阶段3: 有限产能排程已在Pegging阶段完成", planVersionId);
            _logger.LogInformation("[{PlanVersionId}] 阶段4: 排程结果已在Pegging阶段持久化", planVersionId);

            // ═══════════════════════════════════════════
            // 阶段5: 更新PlanVersion状态 + 更新 ScheduleRun
            // ═══════════════════════════════════════════
            var isSuccess = peggingFailed.Count == 0;
            // 【2026-10-15 词表对齐 + 夜间自动采用】冻结 07 §4.5:3744/3745 —— 域成功终态按 RunType 分：
            //   · 夜间全量 FULL_SCHEDULE ⇒ ACTIVE（**自动正式采用**，无需人工确认）
            //   · 白天候选（MANUAL_RESCHEDULE / LOCAL_RESCHEDULE / INSERT_ORDER_WHATIF）⇒ CANDIDATE（等 3号位 人工确认后激活）
            //   · 失败 ⇒ FAILED（与 RunLifecycleService 的域级判定同值，此前写 ComputeFailed 导致失败域永远判不出来）
            var isNightlyFullRun = string.Equals(planVersion.RunType, FullScheduleRunType, StringComparison.Ordinal);
            var autoActivate = isSuccess && isNightlyFullRun;

            if (autoActivate)
            {
                // 先落算完时间与统计（**不动 Status**）—— 若先置 ACTIVE 再归档，中途会出现两行同域 ACTIVE 撞唯一索引。
                //
                // 【2026-10-19 补齐 TotalOrders/TotalTasks】冻结 07 §4.2：`TotalOrders`=本次排程处理的订单数、
                //   `TotalTasks`=本次排程生成的任务数。此前**全仓无任何写入点** ⇒ 两列恒 NULL，而
                //   `OverviewQueryRepository`「各 Domain 生效版本一览」直接 `ISNULL(TotalTasks,0)` 读它们 ⇒ 恒 0。
                //   口径与 `ScheduleQueryService` 汇总端点**同一式**：按 `PlanVersionId` 计数两张业务表
                //   （不用内存 `totalTasks`：那是跨域累计值，且失败域拿不到）。计数放在状态迁移之前，
                //   保证版本一旦可见（ACTIVE/CANDIDATE/FAILED）统计即已就位。
                await _connectionManager.ExecuteAsync(
                    @"UPDATE PlanVersion
                         SET ComputedAt  = GETDATE(),
                             TotalTasks  = (SELECT COUNT(1) FROM [Task]  WHERE PlanVersionId = @Id),
                             TotalOrders = (SELECT COUNT(1) FROM [Order] WHERE PlanVersionId = @Id)
                       WHERE Id = @Id",
                    new { Id = planVersionId },
                    db: DatabaseId.APS);

                // 再交给 ReplaceActiveAsync 完成「同域旧 ACTIVE 归档 + 本行置 ACTIVE」——
                // 单事务单点，与 3号位 人工激活走**同一条路径**，唯一索引 UQ_PlanVersion_OneActivePerDomain 兜底。
                await _planVersionRepository.ReplaceActiveAsync(
                    new PlanVersionEntity { Id = planVersionId, DomainKey = planVersion.DomainKey },
                    actor: "SYSTEM",
                    activatedAt: DateTime.Now,
                    cancellationToken);

                _logger.LogInformation(
                    "[{PlanVersionId}] 夜间全量（RunType={RunType}）域 {DomainKey} 计算成功 ⇒ 自动正式采用（Status=ACTIVE）",
                    planVersionId, planVersion.RunType, planVersion.DomainKey);
            }
            else
            {
                var finalStatus = isSuccess ? PlanVersionCandidateStatus : PlanVersionFailedStatus;
                // 统计同 autoActivate 分支（口径见上）：失败域也落真实计数，便于「排了但没成功」的版本被查到量。
                await _connectionManager.ExecuteAsync(
                    @"UPDATE PlanVersion
                         SET Status      = @Status,
                             ComputedAt  = GETDATE(),
                             TotalTasks  = (SELECT COUNT(1) FROM [Task]  WHERE PlanVersionId = @Id),
                             TotalOrders = (SELECT COUNT(1) FROM [Order] WHERE PlanVersionId = @Id)
                       WHERE Id = @Id",
                    new { Id = planVersionId, Status = finalStatus },
                    db: DatabaseId.APS);
            }

            // ═══════════════════════════════════════════
            // 阶段6: 快照封存（§2.6）
            // ═══════════════════════════════════════════
            _logger.LogInformation("[{PlanVersionId}] 阶段6: 快照封存", planVersionId);
            try
            {
                var snapshotInfo = await _snapshotService.SaveAsync(context, planVersionId, cancellationToken);
                _logger.LogInformation(
                    "[{PlanVersionId}] 快照封存完成: 压缩后={CompressedMB:F1}MB, SHA256={Hash}",
                    planVersionId, snapshotInfo.CompressedSize / 1048576.0, snapshotInfo.FileHash[..12] + "...");
            }
            catch (Exception snapshotEx)
            {
                _logger.LogWarning(snapshotEx, "[{PlanVersionId}] 快照封存失败（非致命，不影响排程结果）", planVersionId);
            }

            stopwatch.Stop();

            await LogETLAsync(planVersion.VersionCode, "Scheduling",
                $"排程完成 | 已排:{totalTasks} | 失败:{peggingFailed.Count} | 耗时:{stopwatch.ElapsedMilliseconds}ms",
                isSuccess ? "SUCCESS" : "PARTIAL");

            var result = new SchedulingRunResult
            {
                PlanVersionId    = planVersionId,
                VersionCode      = planVersion.VersionCode,
                IsSuccess        = isSuccess,
                // 【2026-09-29 修复】此处原**不设 `ErrorMessage`** ⇒ 「优雅失败」（Pegging 未抛异常、由 `isSuccess =
                //   peggingFailed.Count == 0` 判出的 false）返回的 `ErrorMessage` 恒为 null。
                //   后果链：`RunSchedulingAndFinalizeAsync` 拿 `result.ErrorMessage ?? "排程失败"` 回写 `ScheduleRun.ErrorMessage`
                //   ⇒ **库里只留「排程失败」四个字**，真实原因（例：「DemandPriority 配置错误：未知字段 'OrderId'」）
                //   只存在于 `_logger.LogWarning`（见上方 409 行同源），**失败现场无法从库自证**。
                //   2026-09-29 首次真跑（PV632/Run818）即栽在此：IsSuccess=False 且 ErrorMessage 为空，只能挖日志。
                //   现把 Pegging 失败原因**摘要**带上（前 5 条含 OrderId，超出部分指向日志），与日志同源、不新增取值路径。
                ErrorMessage     = isSuccess
                    ? null
                    : $"Pegging 失败 {peggingFailed.Count}/{peggingResults.Count}："
                      + string.Join("；", peggingFailed.Take(5).Select(f => $"OrderId={f.OrderId}:{f.ErrorMessage}"))
                      + (peggingFailed.Count > 5 ? $"…（其余 {peggingFailed.Count - 5} 条见日志）" : ""),
                ScheduledCount   = totalTasks,
                UnscheduledCount = peggingFailed.Count,
                ElapsedMs        = stopwatch.ElapsedMilliseconds,
                // FULL §9：成功 Domain 的 FinalTask 共享 Resource 占用区间，供后续 Domain 作不可用时间窗
                EmittedResourceBlocks = isSuccess ? ExtractResourceOccupancyBlocks(peggingResults) : new List<ResourceBlock>(),
                // §16/§21.6/D18：Domain 级性能埋点
                Metrics = new DomainPerformanceMetrics
                {
                    DomainKey                    = planVersion.DomainKey,
                    DemandCount                  = allOrderIds.Count,
                    LogicalProductionDemandCount = logicalProductionDemandCount,
                    FinalTaskCount               = totalTasks,
                    ContextBuildMs               = contextStopwatch.ElapsedMilliseconds,
                    PeggingMs                    = peggingMs,
                    SolverMs                     = solverMs,
                    PersistMs                    = persistMs,
                    TotalMs                      = stopwatch.ElapsedMilliseconds
                }
            };

            _logger.LogInformation(
                "[{PlanVersionId}] Domain 性能指标: DomainKey={DomainKey}, Demand={Demand}, LPD={Lpd}, FinalTask={FinalTask}, ContextBuild={ContextBuildMs}ms, Pegging={PeggingMs}ms, Solver={SolverMs}ms, Persist={PersistMs}ms, Total={TotalMs}ms",
                planVersionId, planVersion.DomainKey, allOrderIds.Count, logicalProductionDemandCount, totalTasks,
                contextStopwatch.ElapsedMilliseconds, peggingMs, solverMs, persistMs, stopwatch.ElapsedMilliseconds);

            _logger.LogInformation(
                "排程完成: PlanVersionId={PlanVersionId}, 已排={Scheduled}, 未排={Unscheduled}, 耗时={Elapsed}ms",
                planVersionId, totalTasks, peggingFailed.Count, stopwatch.ElapsedMilliseconds);

            return result;
        }
        catch (ScopeViolationException ex)
        {
            // 队列3（3号位 A 案）：业务范围越界**不吞**成本域失败。
            // 本域 PV 仍落 FAILED（与真实计算失败同终态，便于按 PV 查），随后**原样上抛** ——
            // 由 Run 层中止整轮（未执行域一并落 FAILED、Run=FAILED、跳过 CloseOutRunAsync）。
            stopwatch.Stop();
            _logger.LogError(ex,
                "部门范围越界，中止本域并上抛: PlanVersionId={PlanVersionId}, Code={Code}",
                planVersionId, ex.Code);

            try
            {
                await _connectionManager.ExecuteAsync(
                    "UPDATE PlanVersion SET Status = 'FAILED' WHERE Id = @Id",
                    new { Id = planVersionId },
                    db: DatabaseId.APS);

                await LogETLAsync($"PV-{planVersionId}", "Scheduling",
                    $"部门范围越界中止: {ex.Code}: {ex.Message}", "FAILED");
            }
            catch (Exception logEx)
            {
                _logger.LogWarning(logEx, "范围越界后回写状态异常（非致命）");
            }

            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "排程失败: PlanVersionId={PlanVersionId}", planVersionId);

            try
            {
                await _connectionManager.ExecuteAsync(
                    "UPDATE PlanVersion SET Status = 'FAILED' WHERE Id = @Id",
                    new { Id = planVersionId },
                    db: DatabaseId.APS);

                await LogETLAsync($"PV-{planVersionId}", "Scheduling",
                    $"排程失败: {ex.Message}", "FAILED");
            }
            catch (Exception logEx)
            {
                _logger.LogWarning(logEx, "排程失败后回写状态异常（非致命）");
            }

            // 单 Domain 失败不向上抛：返回失败结果，交由 RunSchedulingAutoAsync 做「上游失败阻断」+ ScheduleRun 终态收口
            return new SchedulingRunResult
            {
                PlanVersionId = planVersionId,
                IsSuccess     = false,
                ErrorMessage  = ex.Message,
                ElapsedMs     = stopwatch.ElapsedMilliseconds
            };
        }
    }

    /// <summary>
    /// 将本 Domain 的 PlanVersion 标记为「因上游失败被阻断」（FAILED，与真实计算失败同终态；阻断原因记录在日志与 ScheduleRun.ErrorMessage）。
    /// </summary>
    /// <summary>
    /// 队列3（3号位 A 案）：部门范围越界时**中止整轮**。
    /// <para>① 已提交域（<paramref name="violatingIndex"/> 之前）保留各自终态、**不回滚** —— 其结果已落库并收口；</para>
    /// <para>② 未执行域（含越界域本身）一并落 FAILED。判据＝中断后**不得有一条停在 <c>BUILDING</c>**，
    ///         否则发令枪 <c>EXISTS(Status='BUILDING')</c> 会把同一 Run 重复领取；</para>
    /// <para>③ 走 <c>FailAsync</c> 而**不**走 <c>CloseOutRunAsync</c>：越界不是「排了但没排好」，不得计成 PARTIAL_SUCCESS。</para>
    /// </summary>
    /// <returns>已写进 <c>ScheduleRun.ErrorMessage</c> 的 Run 级原因串（调用方原样回填结果）。</returns>
    private async Task<string> AbortRunForScopeViolationAsync(
        int scheduleRunId,
        IReadOnlyList<string> orderedDomainKeys,
        int violatingIndex,
        int elapsedSeconds,
        ScopeViolationException ex,
        CancellationToken cancellationToken)
    {
        var committedDomainKeys = orderedDomainKeys.Take(violatingIndex).ToList();
        var pendingDomainKeys   = orderedDomainKeys.Skip(violatingIndex).ToList();

        if (pendingDomainKeys.Count > 0)
        {
            await _connectionManager.ExecuteAsync(
                @"UPDATE PlanVersion
                     SET Status = 'FAILED'
                   WHERE SourceScheduleRunId = @RunId
                     AND DomainKey IN @Keys
                     AND Status NOT IN ('FAILED', 'ACTIVE')",
                new { RunId = scheduleRunId, Keys = pendingDomainKeys },
                db: DatabaseId.APS);
        }

        // 自证判据：中断后本 Run 不得留下 BUILDING 壳（留下就是发令枪重复领取的隐患，必须人工核查）
        var leftoverBuilding = await _connectionManager.QueryFirstOrDefaultAsync<int>(
            "SELECT COUNT(1) FROM PlanVersion WHERE SourceScheduleRunId = @RunId AND Status = 'BUILDING'",
            new { RunId = scheduleRunId },
            db: DatabaseId.APS);
        if (leftoverBuilding > 0)
        {
            _logger.LogError(
                "部门范围越界中止后仍有 {Count} 个 PlanVersion 停在 BUILDING（RunId={RunId}）—— 发令枪可能重复领取同一 Run，须人工核查",
                leftoverBuilding, scheduleRunId);
        }

        // 取码用 ex.Code，不解文案（3号位 §2.4）
        var message = $"部门范围越界中止（{ex.Code}）：已提交域=[{string.Join(",", committedDomainKeys)}]; "
                    + $"未执行域=[{string.Join(",", pendingDomainKeys)}]; {ex.Message}";

        await _scheduleRunService.FailAsync(scheduleRunId, elapsedSeconds, message, cancellationToken);

        _logger.LogError(
            "部门范围越界，整轮中止: ScheduleRunId={RunId}, 越界域={DomainKey}, 已提交域={Committed}, 未执行域={Pending}, Code={Code}, 遗留BUILDING={Leftover}",
            scheduleRunId, orderedDomainKeys[violatingIndex], committedDomainKeys, pendingDomainKeys, ex.Code, leftoverBuilding);

        return message;
    }

    private async Task BlockDomainAsync(int planVersionId)
    {
        await _connectionManager.ExecuteAsync(
            "UPDATE PlanVersion SET Status = 'FAILED' WHERE Id = @Id",
            new { Id = planVersionId },
            db: DatabaseId.APS);
    }

    /// <summary>
    /// 统计「本域有多少订单真正引用了失败上游的产出」（O52，2026-11-09）。
    ///
    /// 口径：`[Order]`（本 PV） × `APS_BOM_RAW` **直接子件** × `Domain_Dependency` 中这些上游域的边料。
    /// 只算**一层** BOM：多层展开会把量放大、且单域十万单级别成本高；一层足以区分
    /// 「整域无影响」（可放行）与「确有影响」（需阻断），这正是本判据要做的二选一。
    ///
    /// 实测校准（Run 999423 / `FAMILY_JS` 失败）：LGJ 593 单 → **0**；TZP 20,782 → **16**；
    /// VALVE 8,884 → **38**；WKQ 126 → **73**。合计 30,385 单中真受影响仅 127 单。
    /// </summary>
    private async Task<int> CountAffectedOrdersAsync(
        int planVersionId,
        IReadOnlyCollection<string> upstreamDomainKeys)
    {
        if (upstreamDomainKeys.Count == 0) return 0;

        var json = System.Text.Json.JsonSerializer.Serialize(upstreamDomainKeys);
        return await _connectionManager.QueryFirstOrDefaultAsync<int>(
            @"SELECT COUNT(DISTINCT o.OrderNo)
              FROM [Order] o
              WHERE o.PlanVersionId = @PlanVersionId
                AND EXISTS (
                    SELECT 1
                    FROM APS_BOM_RAW b
                    INNER JOIN Domain_Dependency d ON d.ChildMaterialCode = b.ChildMaterialCode
                    WHERE b.ParentMaterialCode = o.MaterialCode
                      AND d.UpstreamDomainCode IN (SELECT [value] FROM OPENJSON(@Domains)))",
            new { PlanVersionId = planVersionId, Domains = json },
            db: DatabaseId.APS,
            commandTimeout: 300);
    }

    /// <summary>
    /// 阶段1: 装载排程沙盘（从APS库读取数据组装 SchedulingContext）
    /// 
    /// 子步骤（2号位端到端通路，**2026-10-13 按代码更正**）：
    ///   1.1~1.3 订单 / BOM / 物料属性 —— **不在本方法内**：订单集由阶段2 直接 SQL 读取
    ///                            （`SELECT Id FROM [Order] WHERE PlanVersionId=…`）；
    ///                            `context.Orders` 全仓无赋值点；BOM 与物料属性由 Pegging 侧装载
    ///   1.4 资源 + 日历        → context.Resources / context.ResourceCalendars
    ///                            （⚠ 原注「日历 V1 默认 7x24」**已作废**，且与本文件下方 `:803-806` 自相矛盾：
    ///                             现只加载 `ResourceCalendarSlot` 真实可用窗口，**无日历行＝该资源不可用**）
    ///   1.5 库存装载           → context.InventorySupplies（InventoryBalance 一手库存）
    ///   1.5b 管道供给          → context.InventorySupplies（SupplyFact_Pipeline 追加）
    ///                            ⚠ **不在本方法内**：三级链要用冻结策略快照，故由调用方在
    ///                            `LoadStrategyConfigAsync` 之后调 `LoadPipelineSupplyAsync`（见其注释）
    ///   1.6 Task 拆批          → **已废弃、代码显式跳过**（`:759-761`）：不再预拆批，
    ///                            改由 PeggingOrchestrator 在 1号位 求解后按 `solveResult.FinalTasks` 落库
    ///                            （⚠ 原注「按 RoutingOperation 拆为 N 个 SchedulingTask ＋ 批量 INSERT 到 [Task] 表」
    ///                             **与代码不符，已改**）
    ///   1.7 MES 进度           → context.MESRemainingQty（按 ScheduleRunId 读 StageProgressSnapshot）
    /// 
    /// 【历史块已删·2026-10-13】原「V1 保守策略（无真 Pegging／无 BOM 下钻／无批量切分／默认资源指派）」
    ///   描述的是 Pegging 接入**之前**的过渡态；Pegging 已实装（`PeggingOrchestrator`），该块整体作废。
    /// </summary>
    private async Task<SchedulingContext> LoadSchedulingContextAsync(
        PlanVersionInfoDto planVersion,
        int scheduleRunId,
        CancellationToken cancellationToken)
    {
        var context = new SchedulingContext
        {
            PlanVersionId    = planVersion.Id.ToString(),
            ScheduleRunId    = scheduleRunId,
            PlanHorizonStart = planVersion.PlanHorizonStart,
            PlanHorizonEnd   = planVersion.PlanHorizonEnd
        };

        // 1.4 资源 + 日历
        await LoadResourcesAndCalendarAsync(context, cancellationToken);
        _logger.LogInformation("[{PlanVersionId}] 1.4 资源加载: {Count}", planVersion.Id, context.Resources.Count);

        // 1.5 一手库存装载（§2.5.2）
        await LoadInventoryAsync(context, cancellationToken);
        _logger.LogInformation("[{PlanVersionId}] 1.5 库存加载: {Count} 个(物料+产品族+工厂) 组合",
            planVersion.Id, context.InventorySupplies.Count);

        // 1.6 Task 拆批 → V1.2 已废弃：Task由2号位Pegging在供需挂钩后生成，不再预拆批
        // v5.1.2冻结设计（§3.1）：批次拆分由1号位IFiniteCapacityScheduler执行
        _logger.LogInformation("[{PlanVersionId}] 1.6 跳过预拆批（Task将由Pegging生成）", planVersion.Id);

        // 1.7 MES 进度快照装载（源 Run 由 SnapshotRunResolver 解析，不再用本 Run 号裸读）
        // 白天候选 / FAILED 恢复路径传 scheduleRunId=0，裸读恒 0 行 ⇒ MESRemainingQty 恒空。
        await LoadMESProgressAsync(context, planVersion.Id, cancellationToken);
        _logger.LogInformation("[{PlanVersionId}] 1.7 MES进度装载: {Count} 条",
            planVersion.Id, context.MESRemainingQty.Count);

        return context;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 阶段1 各子步骤实现
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 1.7 从 StageProgressSnapshot 装载 MES 进度
    /// 1号位用 MESRemainingQty 决定每道工序实际还需排多少量
    ///
    /// 读哪一批快照由 <see cref="SnapshotRunResolver"/> 决定（0号位《PM回复1009-1》§三 A-Q1/A-Q2/A-Q3）：
    /// 夜间 FULL = 本 Run；白天候选 / FAILED 恢复 = 沿 Base 链继承「有三类完整快照」的源 Run。
    /// 与 PeggingOrchestrator 的 StageProgressSnapshot 读点同一解析器；断链 / 成环 / Base 缺失由解析器 fail-closed。
    /// 调用方传入的 scheduleRunId 不参与选源（候选路径本来就是 0）。
    /// </summary>
    private async Task LoadMESProgressAsync(SchedulingContext context, int planVersionId, CancellationToken ct)
    {
        var snapshotRunId = await SnapshotRunResolver.ResolveSnapshotRunIdAsync(
            _connectionManager, planVersionId, _logger, ct);

        var rows = await _connectionManager.QueryAsync<MESProgressLoadDto>(
            @"SELECT ProductionInstructionNo, MaterialCode, StageCode, RemainingQty
              FROM StageProgressSnapshot
              WHERE ScheduleRunId = @ScheduleRunId
                AND RemainingQty  > 0",
            new { ScheduleRunId = snapshotRunId },
            db: DatabaseId.APS);

        foreach (var r in rows)
        {
            var key = SchedulingContext.BuildMESKey(r.ProductionInstructionNo, r.MaterialCode, r.StageCode);
            context.MESRemainingQty[key] = r.RemainingQty;
        }
    }


    /// <summary>
    /// 1.4 加载资源 + 日历
    ///
    /// ⚠ 旧注释「V1 日历策略：7x24 连续可用（计划期全覆盖）」**已作废**，与下方实现直接矛盾：
    ///   PM 0923 §九 已取消 730 天 7×24 合成，改为**只加载 `ResourceCalendarSlot` 真实可用窗口、无日历行=该资源不可用**。
    ///   《APS V1｜2026-10-12 冻结文档修改指导 v1.1》§1.1.6 进一步**取消**「日历缺段一律按 8 小时工作日」的独立兜底：
    ///   **不强制 8 小时、不猜 08:00—16:00、不按自然日一律 8 小时或 7×24 补产能**。
    ///   目标形态（§1.1.5／§3.1）＝**三级继承：工厂实际工作日历 → 生产部门日历 → 设备资源／人工能力槽日历**；
    ///   显式停机/禁用/休息/维修**不得**被上级继承复活；**三级均无可信来源 ⇒ 记录覆盖不足、不产生 Slot**。
    ///   ✅ **三级继承已实现**（2026-10-13 接入 `ResourceCalendarResolver`，见本方法 `:1185` 起的三级合成段）；
    ///   本行原写「尚未实现（当前只读资源级窗口）」为接入前状态，**已失效**（2026-10-28 核对代码后更正）。
    /// </summary>
    /// <remarks>
    /// 2026-10-13：由 `private` 改 **`internal`**，**仅供 `LPS.APS.Tests`（`InternalsVisibleTo`）复用**。
    ///   动机：`LoadToPosition1AuditTest` 自建 `SchedulingContext` 时只赋了 StrategyProfileVersionId/DataCutoffTime/PlanHorizon*，
    ///   导致装给 1号位 的 `Resources`/`CalendarSlots` **恒为 0**（而生产非 0）⇒ 探针在资源轴上不具代表性。
    ///   让探针**调用本方法（生产同一份代码）**而不是自己复刻一份查询，避免又造一个漂移源。
    ///   **不改任何行为**：签名语义、SQL、日志逐字不变。
    /// </remarks>
    internal async Task LoadResourcesAndCalendarAsync(SchedulingContext context, CancellationToken ct)
    {
        var resources = await _connectionManager.QueryAsync<ResourceLoadDto>(
            @"SELECT
                r.Id              AS ResourceId,
                r.ResourceCode,
                r.ResourceName,
                r.FactoryId,
                r.ProductionDepartmentId,
                r.CapacityFactor,
                ISNULL(rpc.DispatchPriority, 100) AS DispatchPriority,
                ISNULL(rpc.LocalDisableFlag, 0)   AS LocalDisableFlag
              FROM Resource r
              LEFT JOIN ResourcePlanningContext rpc
                     ON rpc.ResourceId = r.Id
                    AND (rpc.EffectiveTo   IS NULL OR rpc.EffectiveTo   >= @CutoffDate)
                    -- ⚠️ 2026-11-03：① 判据由 `CAST(GETDATE() AS DATE)` 改为**本次 Run 的数据截点**
                    --    （1号位 2026-11-02 件 §3.3：GETDATE 会让同一份输入不同时刻装载结果不同）；
                    --    ② EffectiveFrom 补 `IS NULL OR` 兜底 —— 原写法无 NULL 处理，NULL 比较返回 UNKNOWN
                    --    会把整行滤掉、静默回落到 DispatchPriority=100／LocalDisableFlag=0。
                    --    当前 `EffectiveFrom` 是 NOT NULL 故无实际影响；补上是为了防止日后改成可空时重演
                    --    `StageLeadTimeParam` 那次「282 行被静默滤光」的事故（该处注释有记录）。
                    AND (rpc.EffectiveFrom IS NULL OR rpc.EffectiveFrom <= @CutoffDate)
              WHERE r.IsActive = 1
                -- ⚠️ 2026-11-03 口径变更（0号位 裁定，用户转达）：「**载荷带资源状态不考虑，就当所有设备都可以用**」。
                --    原 `AND r.Status = 'AVAILABLE'` 把维修中（MAINTENANCE）设备整台排除 ⇒
                --    全库 14,775 个「物料×部门×工序」组合因「资格资源无一 AVAILABLE」而无任何可用资源，
                --    其中 3,639 个还叠加了「该（部门,工序名）无人工槽」，造成硬性未排。
                --    现改为 **AVAILABLE ＋ MAINTENANCE 都可用**（=1,166 台维修设备放回池子）。
                --    **DECOMMISSIONED（133 台已报废）仍排除** —— 报废设备排上去是物理上不可能的活；
                --    用户 2026-11-03 选「乙」：先放开维修、保留报废过滤，后续资源问题再议。
                --    ⚠️ 副作用：MAINTENANCE 设备的 365 行日历会进载荷且被当可用，
                --       若某台实际长期停机，其产能会被虚占 —— 这部分风险由源侧状态维护保证，装载侧不再判。
                AND r.Status IN ('AVAILABLE', 'MAINTENANCE')",
            new { CutoffDate = (context.DataCutoffTime ?? DateTime.Now).Date },
            db: DatabaseId.APS);

        // 1号位 回执 §四 提请：S4 取消 730 天合成后，真实日历覆盖 = 正排物理上限。
        // 软边界（正排不受 PlanningEnd 约束）要求日历铺到「兜底日期 = PlanHorizonEnd + 180 天」。
        // 常量已提到 `SchedulingHorizonPolicy`（G-2 的工厂/部门 WORK 窗口必须用**同一个值**，见该类注释）。

        // ── 三级日历·上两级（工厂 / 生产部门）—— 2026-10-13 接入；此前 `ProductionWorkCalendarSlot` 在 .cs 里**零引用** ──
        // ⚠ 2026-10-13 实测更正：本表**已不是 0 行**。5号位 当日 13:48/13:52/13:58（本地）三批共落 47 行，
        //   `SourceSystem='TEST'`：工厂 2/3/4/5/6 的「周一至周五 WORK 08:00–17:00 / 周六日 OFF」各 7 行、
        //   部门 963 同型 7 行，另 5 行是按 `WorkDate` 的节假日/调休 fixture（含 `AllowLowerOverride`／`IsAuthorizedOverride`），
        //   全部 `EffectiveFrom=2026-10-13`。
        //   ⇒ `ResourceCalendarResolver` 的**等价快路径（`UpperLevelFactsPresent=false`）自即日起不再命中**，
        //     三级继承在真实数据上**已生效**：生效日及以后资源日历被上级裁剪为周一至周五，生效日之前不裁剪。
        //     量级对齐（PV2 探针 `CalendarSlots`=4,423,641）：按「生效日前全留 + 生效日起只留周一至周五」建模 = 4,422,856，
        //     差 785 与上述 5 行按日 fixture（补一个周六、一个周日）同量级 ⇒ 数字可解释，非装载异常。
        //   ⚠ 本表**直接喂给 1号位 的载荷**（`ResourceCalendars` → `CalendarSlots`），增删行会改变排程输入。
        //     表主 = 5号位；我方只加载、不治理。
        //   ⚠ 另注：该批行 `CreatedAt/UpdatedAt` 为 **UTC**（05:48/05:52/05:58，较本地早 8 小时），
        //     与 APS「时间戳一律本地、禁 UTC」的约定不一致 —— 属 5号位 铺数侧口径，仅记录。
        var upperCalendarRows = await _connectionManager.QueryAsync<ProductionWorkCalendarSlot>(
            @"SELECT Id, ScopeType, FactoryId, ProductionDepartmentId, WorkDate, WeekdayIso,
                     StartAt, EndAt, WorkState, AllowLowerOverride, IsAuthorizedOverride,
                     SourceSystem, SourceReference, EffectiveFrom, EffectiveTo, IsActive
                FROM ProductionWorkCalendarSlot
               WHERE IsActive = 1",
            db: DatabaseId.APS);

        // PM 0923 §九：取消 730 天 7×24 合成；只加载真实窗口，无日历行=该资源不可用。
        // ⚠ 2026-10-13 改：**不再只取 `AvailableFlag=1`**。`AvailableFlag=0` 是「资源级显式 OFF（停机/维修）」，
        //   属 RC-006「资源自身明确 OFF 为硬排除」，必须交给解析器作**例外裁剪**，不能在这里静默丢掉。
        //   （实测全表 `AvailableFlag=0` 为 0 行 ⇒ 本改动当前不改变任何结果，只把语义摆正。）
        var calendarRows = await _connectionManager.QueryAsync<ResourceCalendarSlotLoadDto>(
            @"SELECT ResourceId, StartTime, EndTime, AvailableFlag
                FROM ResourceCalendarSlot",
            db: DatabaseId.APS);
        var ownCalendars = calendarRows
            .GroupBy(c => c.ResourceId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(c => new ResourceCalendarResolver.LevelWindow
                {
                    Start = c.StartTime,
                    End = c.EndTime,
                    IsAvailable = c.AvailableFlag
                }).ToList());

        // 部门 → 工厂：人工能力槽主档只有 ProductionDepartmentId，要解析工厂级日历必须再上溯一层。
        var departmentFactoryMap = (await _connectionManager.QueryAsync<DepartmentFactoryLoadDto>(
            @"SELECT Id, FactoryId FROM ProductionDepartment",
            db: DatabaseId.APS))
            .ToDictionary(x => x.Id, x => x.FactoryId);

        var calendarInputs = new List<ResourceCalendarResolver.ResourceCalendarInput>();

        foreach (var r in resources)
        {
            if (r.LocalDisableFlag) continue;

            var resIdStr = r.ResourceId.ToString();
            context.Resources.Add(new SchedulingResource
            {
                ResourceId       = resIdStr,
                ResourceName     = r.ResourceName,
                ResourceCode     = r.ResourceCode,
                FactoryId        = r.FactoryId.ToString(),
                ProductionDepartmentId = r.ProductionDepartmentId,
                CapacityFactor   = r.CapacityFactor,
                DispatchPriority = r.DispatchPriority,
                IsAvailable      = true
            });

            // PM 0923 §九：无有效日历=不可用（设备+人工统一）；铺天数=5号位，2号位 只加载不合成。
            // 2026-10-13：本级窗口（含 OFF）交给三级解析器统一合成，不再在此直接写 ResourceCalendars。
            calendarInputs.Add(new ResourceCalendarResolver.ResourceCalendarInput
            {
                Key = resIdStr,
                FactoryId = r.FactoryId,
                DepartmentId = r.ProductionDepartmentId,
                OwnWindows = ownCalendars.TryGetValue(r.ResourceId, out var windows)
                    ? windows
                    : Array.Empty<ResourceCalendarResolver.LevelWindow>()
            });
        }

        // ── 人工能力槽装载 + 运行时投影（PM 0923 资源模型裁决）──
        // ManualCapacitySlot 不进入正式 Resource 主数据；装载时投影为合成 SolverResource
        //（合成 ResourceId = ManualSlotResourceOffset + ManualSlotId，运行时映射键，不落库、不暴露负号）。
        // PM §十二：1 槽=1 并行能力单元，CapacityFactor 固定 1（不引入 CapacityFactor=人数）。
        var manualSlots = await _connectionManager.QueryAsync<ManualCapacitySlotLoadDto>(
            @"SELECT ManualSlotId, ProductionDepartmentId, OperationName, SlotCode
                FROM ManualCapacitySlot
               WHERE IsActive = 1",
            db: DatabaseId.APS);

        // 同上：人工槽 `AvailableFlag=0` 同样是「本级显式 OFF」，交给解析器裁剪，不在此丢弃。
        var manualSlotCalendarRows = await _connectionManager.QueryAsync<ManualCapacitySlotCalendarLoadDto>(
            @"SELECT ManualSlotId, StartTime, EndTime, AvailableFlag
                FROM ManualCapacitySlotCalendar",
            db: DatabaseId.APS);
        var manualCalendars = manualSlotCalendarRows
            .GroupBy(c => c.ManualSlotId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(c => new ResourceCalendarResolver.LevelWindow
                {
                    Start = c.StartTime,
                    End = c.EndTime,
                    IsAvailable = c.AvailableFlag
                }).ToList());

        var rejectedManualSlots = 0;
        var rejectedManualSlotSamples = new List<string>(5);

        foreach (var slot in manualSlots)
        {
            // 【2026-10-17 J14-08】越界槽**不入 Solver**：`1e9 + ManualSlotId` 溢出后是负数，
            //   而落库拆键判据 `IsManualSlotId(id) = id >= 1e9` 对负数恒假 ⇒ 负数会被当作真实设备写进
            //   `Task.ResourceId`（死分支）。原「仅告警、照常合成」正是把这个死分支造出来 ⇒ 改为跳过。
            if (!SolverResourceProjection.IsWithinManualSlotRange(slot.ManualSlotId))
            {
                rejectedManualSlots++;
                if (rejectedManualSlotSamples.Count < 5)
                    rejectedManualSlotSamples.Add($"{slot.ManualSlotId}@{slot.ProductionDepartmentId}/{slot.OperationName}");
                continue;
            }

            var syntheticIdStr = (SolverResourceProjection.ManualSlotResourceOffset + slot.ManualSlotId).ToString();

            context.Resources.Add(new SchedulingResource
            {
                ResourceId       = syntheticIdStr,
                ResourceName     = SolverResourceProjection.ManualSlotResourceCodePrefix + slot.OperationName + ":" + slot.SlotCode,
                ResourceCode     = SolverResourceProjection.ManualSlotResourceCodePrefix + slot.OperationName + ":" + slot.SlotCode,
                FactoryId        = string.Empty,
                ProductionDepartmentId = slot.ProductionDepartmentId,
                CapacityFactor   = 1.0m,
                DispatchPriority = 100,
                IsAvailable      = true
            });

            // 无 ManualCapacitySlotCalendar 行 = 不可用（与设备同口径，PM §九）。
            // 2026-10-13：同上，改交三级解析器；人工槽的工厂由「部门→工厂」上溯得到。
            calendarInputs.Add(new ResourceCalendarResolver.ResourceCalendarInput
            {
                Key = syntheticIdStr,
                FactoryId = departmentFactoryMap.TryGetValue(slot.ProductionDepartmentId, out var deptFactoryId)
                    ? deptFactoryId
                    : (int?)null,
                DepartmentId = slot.ProductionDepartmentId,
                OwnWindows = manualCalendars.TryGetValue(slot.ManualSlotId, out var windows)
                    ? windows
                    : Array.Empty<ResourceCalendarResolver.LevelWindow>()
            });
        }

        if (rejectedManualSlots > 0)
        {
            _logger.LogWarning(
                "[Scheduling] 越界人工槽已拒绝入 Solver（J14-08）：{Count}/{Total} 个槽的 ManualSlotId 越界，"
                + "合成 ResourceId 会溢出 int ⇒ 不投影资源、不建日历。样本：{Samples}",
                rejectedManualSlots,
                manualSlots.Count(),
                string.Join("; ", rejectedManualSlotSamples));
        }

        // ── 三级继承合成（工厂 → 生产部门 → 设备/人工槽）：逐片「交集 / 例外裁剪」，绝不时段相加 ──
        // 口径：规则清单 v1.7 RC-001/RC-002/RC-006/RC-007；日历专项 v1.4 §2/§4/§5；06 接口 §18。
        var calendarResolution = ResourceCalendarResolver.Resolve(
            upperCalendarRows.ToList(),
            calendarInputs,
            context.PlanHorizonStart,
            context.PlanHorizonEnd.AddDays(SchedulingHorizonPolicy.SoftBoundaryCalendarBufferDays));

        foreach (var (resourceKey, windows) in calendarResolution.Calendars)
            context.ResourceCalendars[resourceKey] = windows.Select(w => new TimeWindow(w.Start, w.End)).ToList();

        if (!calendarResolution.UpperLevelFactsPresent)
        {
            _logger.LogInformation(
                "[ResourceCalendar] 上级日历（ProductionWorkCalendarSlot）无事实行 ⇒ 按资源级口径解析（等价于两级全 MISSING 的三级结果）。"
                + "日历资源数={WithCalendar}，无窗口={Without}。工厂/部门 OFF 与继承将在 5号位 铺数后自动生效。",
                calendarResolution.Calendars.Count, calendarResolution.ResourcesWithoutAnyWindow);
        }
        else
        {
            _logger.LogInformation(
                "[ResourceCalendar] 三级继承已生效：日历资源数={WithCalendar}（其中继承上级={Inherited}），无窗口={Without}；"
                + "窗口来源片数 工厂={FromFactory}／部门={FromDept}／资源={FromResource}。",
                calendarResolution.Calendars.Count, calendarResolution.ResourcesWithInheritedWindow,
                calendarResolution.ResourcesWithoutAnyWindow,
                calendarResolution.WindowCountFromFactory, calendarResolution.WindowCountFromDepartment,
                calendarResolution.WindowCountFromResource);
        }

        if (calendarResolution.ConflictDayCount > 0)
            _logger.LogWarning(
                "[ResourceCalendar] 同层日历矛盾被整日拒绝 {Days} 个（资源×日）：WORK↔WORK 或 OFF↔OFF 部分重叠。"
                + "按 RC-006 拒绝形成无证据窗口，不猜；请 3号位 治理侧核对发布校验。",
                calendarResolution.ConflictDayCount);

        if (calendarResolution.ResourcesWithUnreliableOwnership > 0)
            _logger.LogWarning(
                "[ResourceCalendar] {Count} 个资源的工厂/部门归属不可靠（缺 FactoryId 或 ProductionDepartmentId），"
                + "其上级日历无法解析 —— 按专项 v1.4 §1「归属不可靠时拒绝形成无证据的合法窗口」只按本级口径处理，并留此诊断。",
                calendarResolution.ResourcesWithUnreliableOwnership);

        if (calendarResolution.ResourcesWithoutAnyWindow > 0)
            _logger.LogWarning(
                "[ResourceCalendar] {Count} 个资源三级均无合法可排窗口 ⇒ 不产生 Slot（RC-007：绝不补 8h / 7×24）。",
                calendarResolution.ResourcesWithoutAnyWindow);

        // 1号位 回执 §四 提请：S4 取消 730 天合成后，真实日历覆盖 = 正排物理上限。
        // 软边界（正排不受 PlanningEnd 约束）要求日历铺到「兜底日期 = PlanHorizonEnd + 180 天」；
        // 覆盖不足则正排末端找不到槽 → 静默落 Unscheduled。此处只打诊断日志（非硬报错），
        // 铺天数属 5号位 数据维护（PM 0923 §九/§十），2号位 只加载 + 出证据。
        // （常量已提到 `SchedulingHorizonPolicy`，与 G-2 的工厂/部门 WORK 窗口共用同一个值。）
        var horizonFloor = context.PlanHorizonEnd.AddDays(SchedulingHorizonPolicy.SoftBoundaryCalendarBufferDays);
        var allCalendarWindows = context.ResourceCalendars.Values.SelectMany(w => w).ToList();
        if (allCalendarWindows.Count == 0)
        {
            _logger.LogWarning(
                "[ResourceCalendar] 本次运行无任何资源日历可用窗口（设备 + 人工均空），正排将无处放槽、可能全部落 Unscheduled。铺天数=5号位 数据维护，2号位 只加载不合成（PM 0923 §九）。");
        }
        else
        {
            var maxCalEnd = allCalendarWindows.Max(w => w.End);
            if (maxCalEnd < context.PlanHorizonEnd)
            {
                _logger.LogWarning(
                    "[ResourceCalendar] 资源日历最大末端 {MaxCalEnd:O} 未覆盖计划期结束 {PlanHorizonEnd:O}，窗口内任务即可能无槽；请 5号位 核对铺天数。",
                    maxCalEnd, context.PlanHorizonEnd);
            }
            else if (maxCalEnd < horizonFloor)
            {
                _logger.LogWarning(
                    "[ResourceCalendar] 资源日历最大末端 {MaxCalEnd:O} 未覆盖软边界兜底日期 {HorizonFloor:O}（PlanHorizonEnd+{BufferDays} 天，正排不受 PlanningEnd 约束）；可能静默落 Unscheduled（1号位 回执 §四）。请 5号位 核对铺天数。",
                    maxCalEnd, horizonFloor, SchedulingHorizonPolicy.SoftBoundaryCalendarBufferDays);
            }
        }
    }

    /// <summary>
    /// 1.5 从 InventoryBalance 装载一手库存到 SchedulingContext.InventorySupplies
    ///
    /// 装载内容（2号位职责）：
    ///   INVENTORY：从 InventoryBalance 读取现有库存（ERP + MES 合并后的可用量）
    ///
    /// ⚠ 管道供给（SupplyFact_Pipeline）**不在本方法内** —— 见 <see cref="LoadPipelineSupplyAsync"/>：
    ///   它要算 AvailableTime 三级链，依赖冻结策略快照，必须排在 LoadStrategyConfigAsync 之后。
    ///
    /// ⚠️ 【待对齐 5号位 — B 项越界】
    ///   当前 sp_SyncInventorySnapshot 里硬编码了"双源互斥判定 + InventoryAvailabilityRule 筛选"，
    ///   这部分是 5号位业务规则引擎的职责。后续应改为：
    ///     - 2号位 SP 只做 L2→L3→L4 管道流转（读规则表 + JOIN 应用）
    ///     - 5号位负责 InventoryAvailabilityRule 表内容维护 + Pegging 时的运行时判定
    ///   V1.2: 5号位接入前，SP 里的规则逻辑保持不动。
    /// </summary>
    private async Task LoadInventoryAsync(SchedulingContext context, CancellationToken ct)
    {
        var balances = await _connectionManager.QueryAsync<InventoryLoadDto>(
            @"SELECT MaterialCode, ProductFamilyId, FactoryId, AvailableQty
              FROM InventoryBalance
              WHERE AvailableQty > 0",
            db: DatabaseId.APS);

        // 同键后写覆盖前写 = 静默丢量。现网实测 `(MaterialCode, ProductFamilyId, FactoryId)` 零重复，
        // 故本计数今日恒 0、写入语义与改动前逐字等价（仍后写覆盖，不改累加）。
        // 加它只为「源表一旦出现同键多行」时不再无声吞掉 —— 与 B-01／族闸门同一类静默丢弃缺陷。
        var duplicateKeyCount = 0;
        foreach (var b in balances)
        {
            var key = SchedulingContext.BuildInventoryKey(b.MaterialCode, b.ProductFamilyId, b.FactoryId);
            if (context.InventorySupplies.ContainsKey(key)) duplicateKeyCount++;
            context.InventorySupplies[key] = b.AvailableQty;
        }

        if (duplicateKeyCount > 0)
        {
            _logger.LogWarning(
                "[{PlanVersionId}] 1.5 库存装载：{DupCount} 行与已有键重合（同物料+产品族+厂出现多行），按后写覆盖前写，前者数量已丢弃。",
                context.PlanVersionId, duplicateKeyCount);
        }
    }

    /// <summary>
    /// 1.5b 管道供给装载（SupplyFact_Pipeline）→ 并入 SchedulingContext.InventorySupplies。
    ///
    /// 【为什么单独一个方法、为什么必须排在策略配置之后】
    ///   到货时点不再直接用原始 `sfp.ETA`，而是走与 Pegging **完全同一套**口径：
    ///     5号位 `ITimedSupplyFactLoader`（同一 SQL、同一 SupplyType 白名单）
    ///     → `AvailableTimeCalculator`（3号位 EtaInvariant 三级链 Manual &gt; ERP &gt; ReleaseDate+DefaultLT，
    ///        再加 Arrival-to-Usable Offset）
    ///   三级链的两项参数（`Procurement.DefaultPurchaseLt` / `ArrivalToUsableOffsets`）来自**冻结策略快照**，
    ///   快照由 `LoadStrategyConfigAsync` 装载 ⇒ 本方法必须在它之后调用，否则算不出 AvailableTime。
    ///
    /// 【行为差异（有意）】
    ///   · 旧口径：`ETA IS NULL` 的行**无条件放行**（现网 18,291 行），且不叠 Arrival-to-Usable Offset；
    ///   · 新口径：`ETA` 为空的行回落到 `ReleaseDate + DefaultPurchaseLt`，并按算出的 AvailableTime 与
    ///     `PlanHorizonEnd` 比较 ⇒ 计划期外的管道供给**不再计入**本次排程的可用量。
    ///   三级链**全空**（无 Manual、无 ETA、无 LT 规则或无 ReleaseDate）时仍以 null 计入 —— 这与 Pegging
    ///   现行处置一致（0号位 2026-10-16 §三-1 已明确「是否改为不可承诺」属待裁语义，本轮不改）。
    ///
    /// 【无冻结策略版本时】
    ///   直接跳过并不回退到原始 ETA：Pegging 在 `StrategyProfileVersionId` 为空时本就 fail-fast 抛错
    ///   （`PeggingOrchestrator` 步骤1），回退只会让两条路径口径分叉、且这次运行最终仍会失败。
    /// </summary>
    private async Task LoadPipelineSupplyAsync(SchedulingContext context, CancellationToken ct)
    {
        var profileVersionId = context.StrategyProfileVersionId;
        if (profileVersionId is not { } versionId || versionId <= 0)
        {
            _logger.LogWarning(
                "[{PlanVersionId}] 1.5b 管道供给跳过：SchedulingContext.StrategyProfileVersionId 为空，" +
                "无法解析 DefaultPurchaseLt / ArrivalToUsableOffsets ⇒ 不回退原始 sfp.ETA，" +
                "管道供给本次不计入 InventorySupplies（Pegging 同条件下会 fail-fast）",
                context.PlanVersionId);
            return;
        }

        var frozenSnapshot = await _frozenStrategySnapshotProvider.GetFrozenStrategySnapshotAsync(versionId, ct);
        var manualEtaMap = AvailableTimeCalculator.BuildManualEtaMap(
            await _procurementManualEtaRepo.GetActiveOverridesAsync(ct));

        // 空 scope = 不按物料/工厂收窄（与 Pegging 同一调用形状）。`DataCutoffTime` 该装载器不参与过滤，
        // 期界判定统一由下面的 AvailableTime vs PlanHorizonEnd 完成。
        var rawFacts = await _timedSupplyFactLoader.LoadRawFactsAsync(new SupplyFactScope(), ct);

        var admitted = 0;
        var outOfHorizon = 0;
        var forbidden = 0;
        var noTime = 0;
        var admittedOnNullEta = 0;

        foreach (var fact in rawFacts)
        {
            // 同一道身份闸：冻结禁令类型不得作为独立 Supply（判据与 Pegging 同一函数，不另写一份）。
            if (PeggingOrchestrator.IsForbiddenStandaloneSupplyType(fact.SupplyType))
            {
                forbidden++;
                continue;
            }

            // 逾期重估基准 = 本 Run 数据截止时点（ScheduleRun.DataCutoffTime，编排层已落到 context）。
            // 与下面 PlanHorizonEnd 的期界过滤是**两件事**：前者判「是否逾期」，后者判「落在计划窗内否」。
            var availableAt = AvailableTimeCalculator.Compute(
                fact, manualEtaMap, frozenSnapshot, context.DataCutoffTime ?? DateTime.Now);
            if (availableAt.HasValue && availableAt.Value > context.PlanHorizonEnd)
            {
                outOfHorizon++;
                continue;
            }
            if (!availableAt.HasValue) noTime++;
            if (!fact.Eta.HasValue) admittedOnNullEta++;

            // ⚠ ProductFamilyId 固定 0，与改动前**逐键等价**：旧 SQL 直接取 `sfp.ProductFamilyId`，
            //   而该列现网 382,939 行**100% 为 NULL**，Dapper 映射到 int 即 0。
            //   ⚠ 另注（已登记、本轮不改）：一手库存键用的是真实 ProductFamilyId（InventoryBalance 0 空、10 个族），
            //   所以管道供给落在 `code|0|factory`，与库存键**不重合** —— 是否应改为按物料真实族拼键属语义变更，
            //   会直接改变 1号位 可拿到的供给量，须先量后裁，不得在本轮顺手改。
            var key = SchedulingContext.BuildInventoryKey(fact.MaterialCode, 0, fact.FactoryId);
            context.InventorySupplies[key] = context.InventorySupplies.GetValueOrDefault(key) + fact.RemainingQty;
            admitted++;
        }

        _logger.LogInformation(
            "[{PlanVersionId}] 1.5b 管道供给装载: 原始 {Raw} 条 / 入池 {Admitted} 条 / " +
            "超计划期剔除 {OutOfHorizon} / 禁令类型拦截 {Forbidden} / 三级链全空 {NoTime} / 其中原始ETA为空 {NullEta}",
            context.PlanVersionId, rawFacts.Count, admitted, outOfHorizon, forbidden, noTime, admittedOnNullEta);

        if (forbidden > 0)
            _logger.LogWarning(
                "[{PlanVersionId}] 1.5b 管道供给: {Count} 条 SupplyType 属冻结禁令（INTERPLANT_IN_TRANSIT），已拦截不入池",
                context.PlanVersionId, forbidden);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // v5.1.2 架构变更说明（**2026-10-13 按代码更正**）：
    // `GenerateAndPersistTasksAsync` / `BulkInsertTasksAsync` **两个方法已从本文件删除**
    //   （不是「保留待废弃」——全仓已无这两个标识符，仅本注释曾提及）。
    //
    // 原因：Task 表的写入职责已整体转移至 PeggingOrchestrator（2号位）
    //       - PeggingOrchestrator 在 1号位 求解**之后**、按 `solveResult.FinalTasks` 落库；
    //         Task 的时间 / 资源 / 数量随 INSERT **一次写入**（`PeggingOrchestrator.cs:677-683`）
    //       - ⚠ 本文件对 `[Task]` **只有跑前幂等清理 DELETE（`:505-511`），没有任何 INSERT / UPDATE**。
    //         原注「SchedulingOrchestrator 只负责 UPDATE Task 表（填充时间/资源分配结果）」
    //         **与代码不符（全文件无 `UPDATE [Task]`），已删**。
    //       - 这样避免了重复落库，保证 Task 与 PeggingSupplyAllocation 的事务一致性
    //
    // 详见：`PeggingOrchestrator.PersistDomainAndPeggingInTransactionAsync`
    //       （`LPS.APS.Application/Services/PeggingOrchestrator.cs:541`；
    //         ⚠ 原注「line 238-280」为行号漂移，已改）
    // ═══════════════════════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════════════════════
    // 阶段4 结果落盘 — 已废弃：Task 落盘已移入 Pegging 阶段（PersistDomainAndPeggingInTransactionAsync）
    // ═══════════════════════════════════════════════════════════════════════════

    private async Task LoadStrategyConfigAsync(SchedulingContext context, long strategyProfileVersionId, CancellationToken ct)
    {
        var rows = (await _connectionManager.QueryAsync<StrategyConfigLoadDto>(
            @"SELECT rs.RuleSetCode, rs.RuleSetName, rsv.VersionCode AS RuleSetVersionCode,
                     ps.ParameterSetCode, ps.ParameterSetName, psv.VersionCode AS ParameterSetVersionCode,
                     spv.RuleSetVersionId, spv.ParameterSetVersionId
              FROM StrategyProfileVersion spv
              JOIN RuleSetVersion     rsv ON rsv.Id = spv.RuleSetVersionId
              JOIN RuleSet            rs  ON rs.Id  = rsv.RuleSetId
              JOIN ParameterSetVersion psv ON psv.Id = spv.ParameterSetVersionId
              JOIN ParameterSet        ps  ON ps.Id  = psv.ParameterSetId
              WHERE spv.Id = @Id",
            new { Id = strategyProfileVersionId },
            db: DatabaseId.APS)).ToList();

        if (rows.Count == 0) return;

        context.RuleConfigs = rows.Select(r => new RuleConfig
        {
            RuleSetVersionId = r.RuleSetVersionId,
            RuleSetCode      = r.RuleSetCode,
            RuleSetName      = r.RuleSetName,
            VersionCode      = r.RuleSetVersionCode
        }).ToList();

        context.SchedulingParamsList = rows.Select(r => new SchedulingParamConfig
        {
            ParameterSetVersionId = r.ParameterSetVersionId,
            ParameterSetCode      = r.ParameterSetCode,
            ParameterSetName      = r.ParameterSetName,
            VersionCode           = r.ParameterSetVersionCode
        }).ToList();

        _logger.LogInformation("策略配置加载完成: StrategyProfileVersionId={Id}, RuleConfigs={RC}, Params={PC}",
            strategyProfileVersionId, context.RuleConfigs.Count, context.SchedulingParamsList.Count);
    }

    /// <summary>
    /// 记录 ETL 日志
    /// </summary>
    private async Task LogETLAsync(string batchNo, string step, string message, string status)
    {
        try
        {
            await _connectionManager.ExecuteAsync(
                @"INSERT INTO APS_ETL_Log (BatchNo, Step, Message, Status, CreatedAt)
                  VALUES (@BatchNo, @Step, @Message, @Status, GETDATE())",
                new { BatchNo = batchNo, Step = step, Message = message, Status = status },
                db: DatabaseId.APS);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入ETL日志失败（非致命）");
        }
    }

    /// <summary>
    /// 构建 PeggingExecutionRequest（从 SchedulingContext 提取必要字段）
    ///
    /// 冻结区规则（文档 §2.3）：
    ///   当前时间起的 2 小时为滑动冻结窗口；MES 已下发任务不可重排
    ///
    /// 虚拟库存：
    ///   从 context.InventorySupplies 里提取（当前 V1 不含跨域供给，由后续3号位扫描补充）
    /// </summary>
    private static PeggingExecutionRequest BuildPeggingRequest(
        int planVersionId,
        string domainKey,
        List<long> allOrderIds,
        SchedulingContext context,
        IReadOnlyList<ResourceBlock>? upstreamResourceBlocks,
        bool isCandidate,
        int? basePlanVersionId,
        IReadOnlyList<ResourceBlock>? externalDomainResourceBlocks,
        IDictionary<string, decimal>? crossDomainPhysicalConsumed = null,
        IDictionary<string, decimal>? externalDomainInventoryOccupation = null)
    {
        var now = DateTime.Now;
        var orderIds = allOrderIds;

        return new PeggingExecutionRequest
        {
            PlanVersionId     = planVersionId,
            DomainKey         = domainKey,
            OrderIds          = orderIds,
            // P0-03：SnapshotAt（供给快照基准）改用 ScheduleRun 冻结 DataCutoffTime，不再 DateTime.Now；
            // 手动入口 DataCutoffTime 为 null 时回退 now。
            SnapshotAt        = context.DataCutoffTime ?? now,
            FrozenWindowStart = now,
            FrozenWindowEnd   = now.AddHours(2),   // §2.3 滑动冻结窗口
            AllowCrossFactory = false,
            DefaultStrategy   = Core.Enum.PeggingStrategyType.FIFO,
            ProductFamilyIds  = context.Tasks
                .Select(t => int.TryParse(t.MaterialId, out var mid) ? mid : 0)
                .Where(id => id > 0)
                .Distinct()
                .ToList(),
            UpstreamResourceBlocks = upstreamResourceBlocks,
            // R03-02：跨域物理消耗账本（FULL 逐域串行时透传；Candidate / 单域入口为 null）
            UpstreamPhysicalConsumption = crossDomainPhysicalConsumed,
            // R03-02：Candidate 侧跨 Run 的库存占用（聚合键；FULL / 单域入口为 null）
            UpstreamAggregateConsumption = externalDomainInventoryOccupation,
            MaxBomDepth       = 10,
            TimeoutSeconds    = 300,
            // P0-04：Candidate 模式（sourcePlanVersionId != null）→ CANDIDATE；否则 FULL_RUN。
            ExecutionMode     = isCandidate ? "CANDIDATE" : "FULL_RUN",
            IsCandidate       = isCandidate,
            BasePlanVersionId = basePlanVersionId,
            ExternalDomainResourceBlocks = externalDomainResourceBlocks,
            SchedulingContext = context  // V1.2：传递完整沙盘上下文供1号位使用
        };
    }

    /// <summary>
    /// FULL §9：从已排 Task 提取共享 Resource 占用区间（ResourceId + 起止时间）。
    /// 只收 ResourceId 与 PlannedStart/End 均齐全的 FinalTask；占用块累积后作为后续 Domain 的不可用时间窗。
    /// 不在此区分「共享 vs 本域独享」——把全部占用区间传给后续 Domain，后续 Domain 只用其中自己真正使用的 Resource，
    /// 语义上与 Candidate 的 ExternalDomainResourceBlocks 一致（§11）。
    /// </summary>
    private static List<ResourceBlock> ExtractResourceOccupancyBlocks(IEnumerable<PeggingOrchestrationResult> peggingResults)
    {
        var blocks = new List<ResourceBlock>();
        foreach (var r in peggingResults)
        {
            foreach (var t in r.GeneratedTasks)
            {
                if (t.ResourceId.HasValue && t.PlannedStartTime.HasValue && t.PlannedEndTime.HasValue)
                {
                    blocks.Add(new ResourceBlock
                    {
                        ResourceId = t.ResourceId.Value,
                        StartTime  = t.PlannedStartTime.Value,
                        EndTime    = t.PlannedEndTime.Value,
                        Reason     = "UPSTREAM_DOMAIN_FULL"
                    });
                }
            }
        }
        return blocks;
    }

    /// <summary>
    /// P0-04 §11：装载「其它 Domain 当前 ACTIVE 在共享 Resource 上的不可移动占用」作为 Candidate 的 ExternalDomainResourceBlocks。
    /// 语义与 FULL 的 UpstreamDomainResourceBlocks 不同：这里不看 Quantity-Time，只看已排 Task 的 ResourceId + 起止时间。
    /// 排除自身候选 PlanVersion（尚未生成终态）与 Base PlanVersion（比较基线，非外部 Domain）。
    /// </summary>
    private async Task<IReadOnlyList<ResourceBlock>> LoadExternalDomainResourceBlocksAsync(
        string currentDomainKey,
        SchedulingContext context,
        int basePlanVersionId,
        int currentPlanVersionId,
        CancellationToken cancellationToken)
    {
        var resourceIds = context.Resources.Select(r => r.ResourceId).Distinct().ToList();
        if (resourceIds.Count == 0)
            return Array.Empty<ResourceBlock>();

        // 口径回退（2026-10-15，B）：正式采用记在 Status='ACTIVE'（冻结 DDL 08:8723 逐字／08:4057 唯一索引谓词同列）。
        //
        // 【2026-09-29 分片】`resourceIds` = 本 Domain 的资源清单。FACTORY_FAMILY 域可覆盖到接近 `Resource` 全表量级
        //   （实测 `Resource` 共 7,768 行）⇒ 远超 2100。Dapper 把 `IN @ResourceIds` 展开成**逐个参数**，
        //   SQL Server 单次 RPC 上限 2100 ⇒ 不分片就是运行时硬失败（不是慢，是报错）。
        //   分片键 = ResourceId：各批结果取并集与单条 `IN` 等价（无聚合、无跨行 ORDER BY 语义）。
        var rows = new List<ExternalDomainBlockRow>();
        for (var offset = 0; offset < resourceIds.Count; offset += SqlServerInParameterLimit)
        {
            var chunk = resourceIds.Skip(offset).Take(SqlServerInParameterLimit).ToList();
            rows.AddRange(await _connectionManager.QueryAsync<ExternalDomainBlockRow>(
                @"SELECT t.ResourceId, t.PlannedStartTime, t.PlannedEndTime, pv.DomainKey, pv.Id AS SourcePlanVersionId
                  FROM [Task] t
                  JOIN PlanVersion pv ON pv.Id = t.PlanVersionId
                  WHERE pv.Status = 'ACTIVE'
                    AND pv.Id NOT IN (@CurrentPlanVersionId, @BasePlanVersionId)
                    AND pv.DomainKey <> @CurrentDomainKey
                    AND t.ResourceId IN @ResourceIds
                    AND t.PlannedStartTime IS NOT NULL
                    AND t.PlannedEndTime IS NOT NULL",
                new
                {
                    CurrentPlanVersionId = currentPlanVersionId,
                    BasePlanVersionId    = basePlanVersionId,
                    CurrentDomainKey     = currentDomainKey,
                    ResourceIds          = chunk,
                },
                db: DatabaseId.APS));
        }

        // D8/R17/T18 双保险：SQL WHERE 已排除本域/自身版本/基线版本，此处再以纯函数兜底过滤，
        // 保证「同TaskNo/本域旧块」在任何情况下都不作为外部 ResourceBlock 阻挡自己（供 T18 单测覆盖）。
        return rows
            .Where(r => !IsSelfOrBaselineExternalBlock(
                r.DomainKey,
                r.SourcePlanVersionId,
                currentDomainKey,
                currentPlanVersionId,
                basePlanVersionId))
            .Select(r => new ResourceBlock
            {
                ResourceId          = r.ResourceId,
                StartTime           = r.PlannedStartTime,
                EndTime             = r.PlannedEndTime,
                Reason              = "EXTERNAL_DOMAIN_ACTIVE",
                SourceDomainKey     = r.DomainKey,
                SourcePlanVersionId = r.SourcePlanVersionId,
                Immutable           = true,
            })
            .ToList();
    }

    /// <summary>
    /// D8/R17/T18（同TaskNo自阻挡）：判定某外部 ACTIVE 块是否为「本域 / 自身版本 / 基线版本」旧块。
    /// 三类必须从 ExternalDomainResourceBlocks 排除——本域旧块应走锚点（ExecutionConstraint）而非外部阻挡，
    /// 自身/基线版本是当前求解与比较基线、非外部域。返回 true = 排除（不得作为外部块）。
    /// </summary>
    internal static bool IsSelfOrBaselineExternalBlock(
        string blockDomainKey, int blockSourcePlanVersionId,
        string currentDomainKey, int currentPlanVersionId, int basePlanVersionId)
    {
        if (string.Equals(blockDomainKey, currentDomainKey, StringComparison.OrdinalIgnoreCase))
            return true;
        if (blockSourcePlanVersionId == currentPlanVersionId || blockSourcePlanVersionId == basePlanVersionId)
            return true;
        return false;
    }

    /// <summary>
    /// R03-02（Candidate 侧，「甲」口径）：装载「其它 Domain 当前 ACTIVE 计划版本已在**库存**上占用的总量」。
    ///
    /// 与 <see cref="LoadExternalDomainResourceBlocksAsync"/> 同源同谓词（`pv.Status='ACTIVE'`、排除自身与
    /// Base 计划版本、排除本域），只是取的是**量**不是时间窗。
    ///
    /// 为什么只能按 `(物料, 工厂, 类型)` 聚合、不能按批次：
    ///   `PeggingSupplyAllocation` 没有物理键列，库存行 `SupplyWarehouseCode`/`SupplyDocumentNo` 实测全为空，
    ///   `SupplySourceId` 跨批会变 ⇒ 跨 Run 无法把「别的域吃了哪一批」回连出来。聚合后**总量守恒**成立，
    ///   批次归属不精确（详见 <see cref="PeggingExecutionRequest.UpstreamAggregateConsumption"/>）。
    ///
    /// 只装 `INVENTORY`：库存各批可用时间同为「立即可用」，聚合扣减不改可行性；采购/在途逐批时点不同，
    /// 按聚合扣会把 A 批的占用挪到 B 批的时点上。
    /// </summary>
    private async Task<IDictionary<string, decimal>> LoadExternalDomainInventoryOccupationAsync(
        string currentDomainKey,
        int basePlanVersionId,
        int currentPlanVersionId,
        CancellationToken cancellationToken)
    {
        // 不按 resourceIds 分片：分组维度是物料×工厂×类型，参数只有 3 个标量。
        // 行数 = 其它 ACTIVE 域的库存分配聚合组数，实测（2026-10-25）全库 ACTIVE 分配仅 16 行 / 16 组。
        var rows = await _connectionManager.QueryAsync<ExternalDomainInventoryRow>(
            @"SELECT pv.DomainKey,
                     pv.Id        AS SourcePlanVersionId,
                     a.MaterialCode,
                     a.SupplyFactoryCode,
                     a.SupplyType,
                     SUM(a.AllocatedQty) AS OccupiedQty
              FROM PeggingSupplyAllocation a
              JOIN PlanVersion pv ON pv.Id = a.PlanVersionId
              WHERE pv.Status = 'ACTIVE'
                AND pv.Id NOT IN (@CurrentPlanVersionId, @BasePlanVersionId)
                AND pv.DomainKey <> @CurrentDomainKey
                AND a.SupplyType = N'INVENTORY'
                AND a.AllocatedQty > 0
              GROUP BY pv.DomainKey, pv.Id, a.MaterialCode, a.SupplyFactoryCode, a.SupplyType",
            new
            {
                CurrentPlanVersionId = currentPlanVersionId,
                BasePlanVersionId    = basePlanVersionId,
                CurrentDomainKey     = currentDomainKey,
            },
            db: DatabaseId.APS);

        // 双保险：SQL 已排除本域/自身版本/基线版本，此处再以纯函数兜底（与外部资源块同一判据）。
        var occupation = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (IsSelfOrBaselineExternalBlock(
                    r.DomainKey, r.SourcePlanVersionId, currentDomainKey, currentPlanVersionId, basePlanVersionId))
                continue;

            if (!Enum.TryParse<Core.Enum.SupplySourceType>(r.SupplyType, ignoreCase: true, out var sourceType))
                continue;   // 词表外的类型不参与扣减（宁可少扣也不误扣到别的类型）

            var key = PeggingOrchestrator.BuildAggregateConsumptionKey(
                r.MaterialCode, r.SupplyFactoryCode, sourceType);

            occupation.TryGetValue(key, out var current);
            occupation[key] = current + r.OccupiedQty;
        }

        return occupation;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 域间依赖拓扑（§5/§7/§15/§23 — 读 Domain_Dependency 表，真实 DomainKey 口径）
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 从 Domain_Dependency 读取跨域血缘边（真实 DomainKey 口径，不再 JOIN ProductFamily.Code 冒充）。
    /// 只保留两端均落在本次 Run 的 DomainKey 集合内的边；表空（V1 现状）返回空集合。
    /// </summary>
    private async Task<List<(string Upstream, string Downstream)>> LoadDomainDependencyEdgesAsync(
        IReadOnlyList<string> domainKeys,
        CancellationToken cancellationToken)
    {
        var rows = (await _connectionManager.QueryAsync<DomainDependencyRow>(
            @"SELECT UpstreamDomainCode, DownstreamDomainCode
              FROM Domain_Dependency
              WHERE UpstreamDomainCode <> DownstreamDomainCode",
            db: DatabaseId.APS)).ToList();

        if (rows.Count == 0)
            return new List<(string, string)>();

        var keySet = domainKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return rows
            .Where(r => keySet.Contains(r.UpstreamDomainCode) && keySet.Contains(r.DownstreamDomainCode))
            .Select(r => (r.UpstreamDomainCode, r.DownstreamDomainCode))
            .ToList();
    }

    /// <summary>
    /// 对本次 Run 的 DomainKey 集合做 Kahn 拓扑排序（依赖边 = Domain_Dependency，真实 DomainKey 口径）。
    /// 无有效依赖边（V1 现状）或检测到环时，降级为 ExpectedDomainKeysJson 冻结顺序（数组序）并记日志。
    /// </summary>
    private List<string> OrderDomainsTopologically(
        IReadOnlyList<string> domainKeys,
        List<(string Upstream, string Downstream)> edges)
    {
        if (edges.Count == 0)
        {
            _logger.LogInformation(
                "Domain_Dependency 无本次 Run 内有效依赖边，按 ExpectedDomainKeysJson 冻结顺序执行（{Count} 域）",
                domainKeys.Count);
            return domainKeys.ToList();
        }

        var nodes = domainKeys.ToList();
        var edgePairs = edges.Select(e => (From: e.Upstream, To: e.Downstream)).ToList();

        List<List<string>> layers;
        try
        {
            layers = TopologicalSort.SortByLayers<string>(nodes, edgePairs);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Domain_Dependency 存在循环依赖，拓扑排序失败，降级为 ExpectedDomainKeysJson 冻结顺序");
            return domainKeys.ToList();
        }

        // 冻结顺序索引（DomainKey → 数组下标），分层内稳定排序用
        var frozenIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < domainKeys.Count; i++)
            frozenIndex[domainKeys[i]] = i;

        var ordered = new List<string>(domainKeys.Count);
        foreach (var layer in layers)
        {
            ordered.AddRange(layer.OrderBy(d => frozenIndex[d]));
        }

        // 防御：Kahn 未覆盖的孤立节点（nodes 已含全部 domainKeys，理论上不会发生）补到末尾
        foreach (var dk in domainKeys)
        {
            if (!ordered.Contains(dk))
                ordered.Add(dk);
        }

        return ordered;
    }

    /// <summary>
    /// 构建每个 Domain 的传递上游闭包（直接 + 间接上游），用于失败阻断按依赖边传播。
    /// 无依赖边时返回空字典（不阻断，全部按冻结顺序执行）。
    /// </summary>
    /// <summary>
    /// Tarjan 强连通分量（O52，2026-11-09）。
    ///
    /// 为什么需要：`Domain_Dependency` 的边按「半成品物料在域间流动」聚合，A 供 B、B 也供 A ⇒ 必然成环。
    /// 实测（2026-11-09）26 条边里至少四组互为上下游：CYL↔TZP／QT↔XC／CYL↔VALVE／TZP↔LGJ。
    /// 环内按传递闭包互相阻断 ⇒ 任一域失败会连坐整环。
    ///
    /// 规模：节点＝本 Run 的域数（实测 11），递归深度安全。
    /// </summary>
    internal static Dictionary<string, int> ComputeStronglyConnectedComponents(
        List<(string Upstream, string Downstream)> edges,
        IReadOnlyList<string> domainKeys)
    {
        var adj = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var dk in domainKeys) adj[dk] = new List<string>();
        foreach (var (up, dn) in edges)
        {
            if (adj.TryGetValue(up, out var list) && adj.ContainsKey(dn)) list.Add(dn);
        }

        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var low = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        var component = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var nextIndex = 0;
        var nextComponent = 0;

        void StrongConnect(string v)
        {
            index[v] = nextIndex;
            low[v] = nextIndex;
            nextIndex++;
            stack.Push(v);
            onStack.Add(v);

            foreach (var w in adj[v])
            {
                if (!index.ContainsKey(w))
                {
                    StrongConnect(w);
                    low[v] = Math.Min(low[v], low[w]);
                }
                else if (onStack.Contains(w))
                {
                    low[v] = Math.Min(low[v], index[w]);
                }
            }

            if (low[v] != index[v]) return;

            string popped;
            do
            {
                popped = stack.Pop();
                onStack.Remove(popped);
                component[popped] = nextComponent;
            } while (!string.Equals(popped, v, StringComparison.OrdinalIgnoreCase));
            nextComponent++;
        }

        foreach (var dk in domainKeys)
        {
            if (!index.ContainsKey(dk)) StrongConnect(dk);
        }

        return component;
    }

    internal static Dictionary<string, HashSet<string>> BuildUpstreamClosure(
        List<(string Upstream, string Downstream)> edges,
        IReadOnlyList<string> domainKeys)
    {
        var closure = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (edges.Count == 0)
            return closure;

        foreach (var dk in domainKeys)
            closure[dk] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 【2026-11-09 O52】先做 SCC 缩点：**同一强连通分量内的域不互相阻断**。
        //   边按「半成品在域间流动」聚合，双向流动必然成环（实测 CYL↔TZP／QT↔XC／CYL↔VALVE／TZP↔LGJ 四组）；
        //   环内若按传递闭包互相阻断，任一域失败会把整环连坐。
        //   缩点后：同分量内不连边（各自担自己的失败），环外真上下游照旧传播。
        var scc = ComputeStronglyConnectedComponents(edges, domainKeys);

        var adjacency = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var dk in domainKeys)
            adjacency[dk] = new List<string>();
        foreach (var (up, dn) in edges)
        {
            // 两端都必须在本 Run 的域集合内，否则 BFS 走到域外节点会 KeyNotFound
            if (!adjacency.TryGetValue(up, out var edgeList) || !adjacency.ContainsKey(dn)) continue;
            if (scc.TryGetValue(up, out var c1) && scc.TryGetValue(dn, out var c2) && c1 == c2)
                continue;   // 同分量：不连边，避免环内互相阻断
            edgeList.Add(dn);
        }

        // 对每个源节点 BFS，把源标记为其所有可达下游的传递上游
        foreach (var dk in domainKeys)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            queue.Enqueue(dk);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                foreach (var next in adjacency[cur])
                {
                    if (visited.Add(next))
                    {
                        closure[next].Add(dk);
                        queue.Enqueue(next);
                    }
                }
            }
        }

        return closure;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 内部 DTO（仅本类 Dapper 投影使用，不对外暴露）
    // ═══════════════════════════════════════════════════════════════════════════

    private class ResourceLoadDto
    {
        public int ResourceId { get; set; }
        public string ResourceCode { get; set; } = string.Empty;
        public string ResourceName { get; set; } = string.Empty;
        public int FactoryId { get; set; }
        public int ProductionDepartmentId { get; set; }
        public decimal CapacityFactor { get; set; }
        public int DispatchPriority { get; set; }
        public bool LocalDisableFlag { get; set; }
    }

    private class ResourceCalendarSlotLoadDto
    {
        public int ResourceId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public bool AvailableFlag { get; set; }
    }

    /// <summary>部门 → 工厂 上溯（人工能力槽只有 ProductionDepartmentId，解析工厂级日历需再上一层）。</summary>
    private class DepartmentFactoryLoadDto
    {
        public int Id { get; set; }
        public int FactoryId { get; set; }
    }

    private class ManualCapacitySlotLoadDto
    {
        public int ManualSlotId { get; set; }
        public int ProductionDepartmentId { get; set; }
        public string OperationName { get; set; } = string.Empty;
        public string SlotCode { get; set; } = string.Empty;
    }

    private class ManualCapacitySlotCalendarLoadDto
    {
        public int ManualSlotId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public bool AvailableFlag { get; set; }
    }

    private class InventoryLoadDto
    {
        public string MaterialCode { get; set; } = string.Empty;
        public int ProductFamilyId { get; set; }
        public int FactoryId { get; set; }
        public decimal AvailableQty { get; set; }
    }

    // 【已删除 · 2026-10-25】`InTransitLoadDto`：管道供给改走 5号位 ITimedSupplyFactLoader（RawProcurementFact），
    //   本类原有的 `ProductFamilyId` 直取已无消费点；固定资产键的说明见 LoadPipelineSupplyAsync。

    private class OperationEligibilityDto
    {
        public int MaterialId { get; set; }
        public string OperationCode { get; set; } = string.Empty;
        public int ResourceId { get; set; }
        public int Priority { get; set; }
    }

    private class TaskInsertDto
    {
        public int PlanVersionId { get; set; }
        public string TaskNo { get; set; } = string.Empty;
        public long OrderId { get; set; }
        public int MaterialId { get; set; }
        public int OperationSeq { get; set; }
        public string OperationCode { get; set; } = string.Empty;
        public int? ResourceId { get; set; }
        public string RouteCode { get; set; } = "DEFAULT";
        public int PathId { get; set; } = 1;
        public decimal Quantity { get; set; }
        public string UOM { get; set; } = string.Empty;
        public decimal? Duration { get; set; }
        public string Status { get; set; } = "Pending";
        public string TaskType { get; set; } = "PRODUCTION";
    }

    private class ScheduleRunQueryDto
    {
        public int Id { get; set; }
        public DateTime DataCutoffTime { get; set; }
        public long? StrategyProfileVersionId { get; set; }
        public int? BasePlanVersionId { get; set; }
        public string? ScopeJson { get; set; }
    }

    private class DomainDependencyRow
    {
        public string UpstreamDomainCode { get; set; } = string.Empty;
        public string DownstreamDomainCode { get; set; } = string.Empty;
    }

    /// <summary>壳盘点投影：每个 DomainKey 有几个 PlanVersion 壳（见 ExecuteRunAsync 的壳盘点注释）。</summary>
    private class DomainShellCount
    {
        public string? DomainKey { get; set; }
        public int ShellCount { get; set; }
    }

    private class ExternalDomainBlockRow
    {
        public int ResourceId { get; set; }
        public DateTime PlannedStartTime { get; set; }
        public DateTime PlannedEndTime { get; set; }
        public string DomainKey { get; set; } = string.Empty;
        public int SourcePlanVersionId { get; set; }
    }

    /// <summary>R03-02（Candidate）：其它 ACTIVE 域在某个「物料×工厂×供给类型」上已占用的量。</summary>
    private class ExternalDomainInventoryRow
    {
        public string DomainKey { get; set; } = string.Empty;
        public int SourcePlanVersionId { get; set; }
        public string MaterialCode { get; set; } = string.Empty;
        public string? SupplyFactoryCode { get; set; }
        public string SupplyType { get; set; } = string.Empty;
        public decimal OccupiedQty { get; set; }
    }

    private class MESProgressLoadDto
    {
        public string ProductionInstructionNo { get; set; } = string.Empty;
        public string MaterialCode { get; set; } = string.Empty;
        public string StageCode { get; set; } = string.Empty;
        public decimal RemainingQty { get; set; }
    }

    private class StrategyConfigLoadDto
    {
        public long RuleSetVersionId { get; set; }
        public string RuleSetCode { get; set; } = string.Empty;
        public string RuleSetName { get; set; } = string.Empty;
        public string RuleSetVersionCode { get; set; } = string.Empty;
        public long ParameterSetVersionId { get; set; }
        public string ParameterSetCode { get; set; } = string.Empty;
        public string ParameterSetName { get; set; } = string.Empty;
        public string ParameterSetVersionCode { get; set; } = string.Empty;
    }
}
