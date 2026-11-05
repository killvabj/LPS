using LPS.APS.Core.Enum;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Engine.Services.Sync;

/// <summary>
/// ScheduleRun 生命周期管理实现（Engine 层）
/// 原逻辑来自 NightlyBatchOrchestrator.CreateScheduleRunAsync（私有）
/// 和 MESSnapshotSyncService.CreateScheduleRunAsync（公开）的合并统一
/// </summary>
public class ScheduleRunService : IScheduleRunService
{
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly IDomainDefinitionService _domainDefinitionService;
    private readonly IStrategyProfileVersionRepository _strategyProfileVersionRepo;
    private readonly ILogger<ScheduleRunService> _logger;

    public ScheduleRunService(
        DatabaseConnectionManager connectionManager,
        IDomainDefinitionService domainDefinitionService,
        IStrategyProfileVersionRepository strategyProfileVersionRepo,
        ILogger<ScheduleRunService> logger)
    {
        _connectionManager      = connectionManager      ?? throw new ArgumentNullException(nameof(connectionManager));
        _domainDefinitionService = domainDefinitionService ?? throw new ArgumentNullException(nameof(domainDefinitionService));
        _strategyProfileVersionRepo = strategyProfileVersionRepo ?? throw new ArgumentNullException(nameof(strategyProfileVersionRepo));
        _logger                 = logger                 ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<int> CreateScheduleRunAsync(CancellationToken cancellationToken = default)
    {
        // 幂等：当日已有 RUNNING 记录则直接复用
        var existing = await _connectionManager.QueryFirstOrDefaultAsync<int>(
            @"SELECT TOP 1 Id FROM ScheduleRun
              WHERE Status = 'RUNNING'
                AND CAST(CreatedAt AS DATE) = CAST(GETDATE() AS DATE)
              ORDER BY CreatedAt DESC",
            db: DatabaseId.APS);

        if (existing > 0)
        {
            _logger.LogInformation("当日 ScheduleRun 已存在，复用: ScheduleRunId={Id}", existing);
            return existing;
        }

        // 冻结预期 Domain 集合（ExpectedDomainKeysJson）：运行启动唯一权威来源。
        // FULL_SCHEDULE 须 ≥1 Domain；DomainDefinition 为空 = 治理未配置，属配置错误，响亮失败不静默降级。
        var domains = await _domainDefinitionService.GetActiveDomainsAsync(cancellationToken);
        if (domains.Count == 0)
            throw new InvalidOperationException("DomainDefinition 无有效（IsActive=1）Domain，无法创建 FULL_SCHEDULE ScheduleRun（预期 Domain 数须 ≥1）");

        var expectedDomainKeysJson = System.Text.Json.JsonSerializer.Serialize(
            domains.Select(d => d.DomainKey).ToList());

        // 默认策略包版本（FULL_SCHEDULE 的 IsDefault=1 且 PUBLISHED，且生效窗覆盖当前时刻）：
        // 0 个 → 治理未配置 / 有默认但不在生效窗内，响亮失败不静默降级（否则 ScheduleRun 带 NULL 版本整晚空跑）；
        // 1 个 → 使用；多个 → 歧义报错（P0-06），不随机 TOP 1。
        //
        // 【2026-10-26 H-1 修复】原 SQL 裸取 IsDefault=1、不过滤生效窗，违反冻结 07 v5.1.15 §5.6 红线
        //   「正式运行只能选择 PUBLISHED 且在有效期内的版本」；跨包错峰默认（A 包上半年 / B 包下半年各持默认，
        //   冻结契约允许）时夜批会命中 2 行 → 抛歧义 → 整批 FAILED、当天无排产结果。
        //   口径与白天候选（RunLifecycleService.cs:521-540）及治理读侧一致：EffectiveFrom/EffectiveTo 为闭区间、
        //   端点相接视为重叠；NULL 视为该端不设限。
        //   @Now 取库端 GETDATE()（与同 Run 的 StartedAt/CreatedAt 同一时钟），不用应用机时钟，避免与库时钟漂移
        //   造成「窗口内被判窗口外」；生效窗为日期级粒度，同一语句内多次 GETDATE() 的微秒差不会改变边界判定。
        // 【2026-10-30 R12-O01 · 3号位 转办】原实现是本文件内联裸 SQL 的**硬等值**匹配
        //   （`p.RunType = 'FULL_SCHEDULE'`，无 NULL 兜底），与候选路径
        //   （`RunLifecycleService.cs:778` → 同一仓储方法）不一致：
        //   若组织只维护 `RunType IS NULL` 的通用策略包，候选能创建、夜间路径整批 FAILED。
        //   现改为调既有仓储 `GetDefaultByRunTypeOrFallbackAsync`（Engine/Repositories/Governance，
        //   2号位 属主）——「精确 RunType 优先 → 无精确生效默认才回退 NULL 通用包 → 两层不混合」，
        //   与候选/Manual/Local 同语义，不再有第二套 SQL。
        //   时钟：仍取**库端** GETDATE()（与同 Run 的 StartedAt/CreatedAt 同一时钟），不用应用机时钟，
        //   避免与库时钟漂移造成「窗口内被判窗口外」；与原先的区别只是「窗口判定」下沉到仓储 SQL。
        var asOfDbTime = await _connectionManager.QueryFirstOrDefaultAsync<DateTime>(
            "SELECT GETDATE()", db: DatabaseId.APS);
        var effectiveDefaults = await _strategyProfileVersionRepo.GetDefaultByRunTypeOrFallbackAsync(
            StrategyProfileRunType.FullSchedule, asOfDbTime, cancellationToken);
        var inWindowVersionIds = effectiveDefaults.Select(v => v.Id).ToList();

        // 【2026-10-26 H-2 修复】COUNT=0 必须区分「治理未配置默认」与「有默认但生效窗未覆盖当前时刻」，
        //   否则运维会照着「请先发布并标记默认版本」去查一个本来就存在的默认版本。
        if (inWindowVersionIds.Count == 0)
        {
            var outOfWindow = await _connectionManager.QueryFirstOrDefaultAsync<string>(
                @"SELECT STRING_AGG(
                             CAST(v.Id AS NVARCHAR(20))
                           + N'（生效窗 ' + ISNULL(CONVERT(NVARCHAR(19), v.EffectiveFrom, 120), N'未设起点')
                           + N' ~ ' + ISNULL(CONVERT(NVARCHAR(19), v.EffectiveTo, 120), N'未设终点') + N'）',
                           N'、')
                  FROM StrategyProfileVersion v
                  JOIN StrategyProfile p ON p.Id = v.StrategyProfileId
                  WHERE v.Status = 'PUBLISHED' AND v.IsDefault = 1
                    AND p.RunType = @RunType AND p.IsActive = 1",
                new { RunType = StrategyProfileRunType.FullSchedule },
                db: DatabaseId.APS);

            if (string.IsNullOrWhiteSpace(outOfWindow))
                throw new InvalidOperationException(
                    "无默认 FULL_SCHEDULE 策略包版本（IsDefault=1 且 PUBLISHED），且无 RunType IS NULL 的通用默认版本可兜底，无法创建 ScheduleRun —— 请先在治理侧发布并标记默认版本");

            throw new InvalidOperationException(
                $"FULL_SCHEDULE 默认策略包版本生效窗未覆盖当前时刻，且无生效窗内的 RunType IS NULL 通用默认可兜底（冻结 07 v5.1.15 §5.6 红线：正式运行只能选择 PUBLISHED 且在有效期内的版本）：{outOfWindow} —— 现有精确默认版本全部在生效窗外，请核对治理侧生效窗设置，不得以窗口外版本充任默认");
        }

        if (inWindowVersionIds.Count > 1)
            throw new InvalidOperationException(
                $"FULL_SCHEDULE 存在多个生效窗内的默认策略包版本（StrategyProfileVersionId = {string.Join(", ", inWindowVersionIds)}），默认版本必须唯一，无法创建 ScheduleRun");

        var strategyVersionId = inWindowVersionIds[0];

        // 【2026-10-26 B1】列集对齐白天候选（ScheduleRunRepository.CreateCandidateRunAsync:179-187）：
        //   显式写出 BasePlanVersionId / ScopeJson 两列，不再靠「不在 INSERT 列表里」隐式留 NULL。
        //   取值依据冻结 DDL（08 v5.1.8.9 §ScheduleRun）：
        //     · BasePlanVersionId —— 「Candidate创建时必须指向本Domain当时唯一ACTIVE版本…FULL可为空」；
        //       FULL_SCHEDULE 跨多个 Domain，不存在单一 Base，故**按契约写 NULL**（非漏写）。
        //     · ScopeJson —— 「版本化Candidate运行输入JSON…新白天Candidate使用SchemaVersion=2」，且
        //       「ExpectedDomainKeys仍只在独立字段，不写入ScopeJson」；FULL 无候选范围输入，故**按契约写 NULL**。
        //   即：FULL 路径的两列合规值就是 NULL，本次只把「隐式省略」改为「显式按契约赋值」，行为不变、可审计。
        var id = await _connectionManager.QueryFirstOrDefaultAsync<int>(
            @"INSERT INTO ScheduleRun
                (RunType, Status, TriggeredBy, DataCutoffTime, StartedAt, CreatedAt,
                 StrategyProfileVersionId, BasePlanVersionId, ScopeJson, ExpectedDomainKeysJson)
              OUTPUT INSERTED.Id
              VALUES (@RunType, 'RUNNING', 'Hangfire', GETDATE(), GETDATE(), GETDATE(),
                      @StrategyProfileVersionId, NULL, NULL, @ExpectedDomainKeysJson)",
            new
            {
                RunType = StrategyProfileRunType.FullSchedule,
                StrategyProfileVersionId = strategyVersionId,
                ExpectedDomainKeysJson = expectedDomainKeysJson
            },
            db: DatabaseId.APS);

        if (id <= 0)
            throw new InvalidOperationException("创建 ScheduleRun 失败");

        _logger.LogInformation("ScheduleRun 创建成功: ScheduleRunId={Id}, StrategyProfileVersionId={VersionId}",
            id, strategyVersionId);
        return id;
    }

    /// <inheritdoc />
    public async Task<ScheduleRunDto?> GetCurrentRunAsync(CancellationToken cancellationToken = default)
    {
        var row = await _connectionManager.QueryFirstOrDefaultAsync<ScheduleRunRow>(
            @"SELECT TOP 1 Id, DataCutoffTime, StrategyProfileVersionId FROM ScheduleRun
              WHERE Status = 'RUNNING'
                AND CAST(CreatedAt AS DATE) = CAST(GETDATE() AS DATE)
              ORDER BY CreatedAt DESC",
            db: DatabaseId.APS);

        return row is null ? null : new ScheduleRunDto(row.Id, row.DataCutoffTime, row.StrategyProfileVersionId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetExpectedDomainKeysAsync(int scheduleRunId, CancellationToken cancellationToken = default)
    {
        var json = await _connectionManager.QueryFirstOrDefaultAsync<string>(
            @"SELECT ExpectedDomainKeysJson FROM ScheduleRun WHERE Id = @Id",
            new { Id = scheduleRunId },
            db: DatabaseId.APS);

        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException($"ScheduleRun {scheduleRunId} 的 ExpectedDomainKeysJson 为空/缺失");

        try
        {
            var domains = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? [];
            return domains.Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidOperationException(
                $"ScheduleRun {scheduleRunId} 的 ExpectedDomainKeysJson 不是合法 JSON 数组：{ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public async Task CompleteAsync(int scheduleRunId, int durationSeconds, CancellationToken cancellationToken = default)
    {
        // 【2026-09-29 修复】原 SQL **不写 `ErrorMessage`** ⇒ 失败过一次、后来重跑成功的 Run 会把
        //   **上一次的失败原因留着**，形成自相矛盾的行：`Status='COMPLETED'` 但 `ErrorMessage='排程失败'`。
        //   实测受害者 = `ScheduleRun 818`（`MANUAL_20260929_FAMILY_X`）：2026-09-29 首跑优雅失败
        //   （当时 `ExecuteDomainAsync` 尚未设 ErrorMessage，回写兜底成「排程失败」），修完重跑成功后
        //   状态转为 COMPLETED，但错误栏没被清。
        //   为何必须在**成功**时清：COMPLETED 的语义就是「无错」；「成功但有瑕疵」另有
        //   `PartialSuccessAsync` 那一态承载。留着陈旧值会让 `GetRunReferenceTraceAsync`（照搬
        //   `run.ErrorMessage`）对运维显示「已完成 + 排程失败」，把人引向不存在的故障。
        //   （另一处消费点 `RunLifecycleService` 的域级 `Reason` 有 `pv.Status == FAILED` 守卫，
        //     不受影响 —— 这里修的是显示与语义，不是那个。）
        await _connectionManager.ExecuteAsync(
            @"UPDATE ScheduleRun
              SET Status          = 'COMPLETED',
                  CompletedAt     = GETDATE(),
                  DurationSeconds = @DurationSeconds,
                  ErrorMessage    = NULL
              WHERE Id = @Id",
            new { Id = scheduleRunId, DurationSeconds = durationSeconds },
            db: DatabaseId.APS);

        _logger.LogInformation("ScheduleRun 完成: ScheduleRunId={Id}", scheduleRunId);
    }

    /// <inheritdoc />
    public async Task PartialSuccessAsync(int scheduleRunId, int durationSeconds, string errorMessage, CancellationToken cancellationToken = default)
    {
        await _connectionManager.ExecuteAsync(
            @"UPDATE ScheduleRun
              SET Status          = 'PARTIAL_SUCCESS',
                  CompletedAt     = GETDATE(),
                  DurationSeconds = @DurationSeconds,
                  ErrorMessage    = @ErrorMessage
              WHERE Id = @Id",
            new { Id = scheduleRunId, DurationSeconds = durationSeconds, ErrorMessage = errorMessage },
            db: DatabaseId.APS);

        _logger.LogInformation("ScheduleRun 部分成功: ScheduleRunId={Id}", scheduleRunId);
    }

    /// <inheritdoc />
    public async Task FailAsync(int scheduleRunId, int durationSeconds, string errorMessage, CancellationToken cancellationToken = default)
    {
        await _connectionManager.ExecuteAsync(
            @"UPDATE ScheduleRun
              SET Status          = 'FAILED',
                  CompletedAt     = GETDATE(),
                  DurationSeconds = @DurationSeconds,
                  ErrorMessage    = @ErrorMessage
              WHERE Id = @Id",
            new { Id = scheduleRunId, DurationSeconds = durationSeconds, ErrorMessage = errorMessage },
            db: DatabaseId.APS);

        _logger.LogInformation("ScheduleRun 失败: ScheduleRunId={Id}", scheduleRunId);
    }


    private sealed class ScheduleRunRow
    {
        public int Id { get; set; }
        public DateTime DataCutoffTime { get; set; }
        public long? StrategyProfileVersionId { get; set; }
    }
}
