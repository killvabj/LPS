using Dapper;
using LPS.APS.Core.DTOs.Governance;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Engine.Repositories.Governance;

/// <summary>
/// ScheduleRun 治理仓储实现（Dapper + APS_Production）
/// 对应表：APS_Production.dbo.ScheduleRun（冻结 DDL v5.1.7 §3.1）
/// 边界：只读取冻结列；仅"FAILED 恢复新建"一条写入路径（IRunLifecycleService.RecoverFailedRunAsync 内部使用）；
///       不重写 2号位运行状态执行流转。
/// </summary>
/// <remarks>开发者：3号位</remarks>
public class ScheduleRunRepository : IScheduleRunRepository
{
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly ILogger<ScheduleRunRepository> _logger;

    public ScheduleRunRepository(
        DatabaseConnectionManager connectionManager,
        ILogger<ScheduleRunRepository> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    /// <summary>Run 列表查询上限（G4 防全表；与治理审计日志 take 上限一致）</summary>
    private const int MaxTake = 200;

    public async Task<ScheduleRunGov?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT [Id], [RunType], [Status], [TriggeredBy], [DataCutoffTime],
                   [StrategyProfileVersionId], [BasePlanVersionId], [ExpectedDomainKeysJson], [ScopeJson],
                   [StartedAt], [CompletedAt], [ErrorMessage]
            FROM [dbo].[ScheduleRun]
            WHERE [Id] = @Id";

        return await _connectionManager.QueryFirstOrDefaultAsync<ScheduleRunGov>(
            sql, new { Id = id }, db: DatabaseId.APS);
    }

    public async Task<IReadOnlyList<ScheduleRunGov>> GetListAsync(
        int? take = null,
        string? status = null,
        string? runType = null,
        IReadOnlyList<string>? allowedDomains = null,
        CancellationToken ct = default)
    {
        // 缺省 100、上限 200（G4：运行历史列表分页展示，防无界查询）
        var takeClamped = Math.Clamp(take ?? 100, 1, MaxTake);
        var allowedDomainsJson = BuildAllowedDomainsJson(allowedDomains);

        // 范围过滤（R1，4号位 2026-11-12 运行与下发页业务范围未生效）：
        //   仅当 allowedDomains != null（非 Global）才启用 EXISTS 精确过滤——批次登记域集合与该用户 Domain 授权交集非空才可见；
        //   空 JSON（[]）→ EXISTS 恒假 → 无 Domain 维度授权的受限用户看不到任何批次（fail-closed，与域状态端点一致）；
        //   批次 ExpectedDomainKeysJson 空/缺失（OPENJSON 空集）→ 受限用户不可见（Q2 裁定：无登记批次仅 Global 可见）。
        var allowScopeSql = allowedDomains is null
            ? ""
            : "  AND EXISTS (\n" +
              "        SELECT 1 FROM OPENJSON(@AllowedDomainsJson) _a\n" +
              "        WHERE EXISTS (SELECT 1 FROM OPENJSON(r.[ExpectedDomainKeysJson]) _d WHERE _d.value = _a.value))\n";

        var sql = $@"
            SELECT r.[Id], r.[RunType], r.[Status], r.[TriggeredBy], r.[DataCutoffTime],
                   r.[StrategyProfileVersionId], r.[BasePlanVersionId], r.[ExpectedDomainKeysJson], r.[ScopeJson],
                   r.[StartedAt], r.[CompletedAt], r.[ErrorMessage]
            FROM [dbo].[ScheduleRun] r
            WHERE (@Status IS NULL OR r.[Status] = @Status)
              AND (@RunType IS NULL OR r.[RunType] = @RunType)
{allowScopeSql}            ORDER BY r.[Id] DESC
            OFFSET 0 ROWS FETCH NEXT @Take ROWS ONLY";

        var rows = await _connectionManager.QueryAsync<ScheduleRunGov>(
            sql,
            new
            {
                Take = takeClamped,
                Status = string.IsNullOrWhiteSpace(status) ? null : status!.Trim(),
                RunType = string.IsNullOrWhiteSpace(runType) ? null : runType!.Trim(),
                AllowedDomainsJson = allowedDomainsJson,
            },
            db: DatabaseId.APS);

        return rows.ToList();
    }

    /// <summary>将允许的 Domain 集合序列化为 SQL `OPENJSON` 参数（null/Global 返回 null → SQL 跳过过滤；空集返回 `[]` → EXISTS 恒假 fail-closed）</summary>
    private static string? BuildAllowedDomainsJson(IReadOnlyList<string>? allowedDomains)
        => allowedDomains is null ? null : System.Text.Json.JsonSerializer.Serialize(allowedDomains);

    /// <inheritdoc />
    public async Task<ScheduleRunGovPage> GetPageAsync(
        int? skip = null,
        int? pageSize = null,
        string? status = null,
        string? runType = null,
        IReadOnlyList<string>? allowedDomains = null,
        CancellationToken ct = default)
    {
        // R4（4号位 2026-11-11 运行与下发页改版）：服务端分页 + total。
        // 与 GetListAsync 互补（该端点保留数组形状供既有消费方）；本方法按 [StartedAt] DESC, [Id] DESC 稳定排序
        // （Id ≠ 启动时间序——补录历史批次/并发写入可致不同序，服务端分页须按稳定键翻页避免重复/漏行）。
        // R1（4号位 2026-11-12 运行与下发页业务范围未生效）：加业务范围过滤。allowedDomains=null（Global）跳过过滤；
        // 空集（[]）→ EXISTS 恒假 → 无 Domain 维度授权 fail-closed 看不到任何批次；批次无登记域 → 受限用户不可见（Q2 仅 Global 可见）。
        // Total 与列表同 WHERE（含范围过滤），页码/总数对齐（4号位 §三 R1 要求 total 为过滤后全量）。
        var skipClamped = Math.Max(skip ?? 0, 0);
        var pageSizeClamped = Math.Clamp(pageSize ?? 100, 1, MaxTake);
        var allowedDomainsJson = BuildAllowedDomainsJson(allowedDomains);

        var whereBase = @"
            WHERE (@Status IS NULL OR r.[Status] = @Status)
              AND (@RunType IS NULL OR r.[RunType] = @RunType)";

        var scopeClause = allowedDomains is null
            ? whereBase
            : $@"{whereBase}
              AND EXISTS (
                    SELECT 1 FROM OPENJSON(@AllowedDomainsJson) _a
                    WHERE EXISTS (SELECT 1 FROM OPENJSON(r.[ExpectedDomainKeysJson]) _d WHERE _d.value = _a.value))";

        var fromSql = $@"FROM [dbo].[ScheduleRun] r";

        var sql = $@"SELECT r.[Id], r.[RunType], r.[Status], r.[TriggeredBy], r.[DataCutoffTime],
                   r.[StrategyProfileVersionId], r.[BasePlanVersionId], r.[ExpectedDomainKeysJson], r.[ScopeJson],
                   r.[StartedAt], r.[CompletedAt], r.[ErrorMessage]
            {fromSql}
            {scopeClause}
            ORDER BY r.[StartedAt] DESC, r.[Id] DESC
            OFFSET @Skip ROWS FETCH NEXT @PageSize ROWS ONLY";

        var rows = await _connectionManager.QueryAsync<ScheduleRunGov>(sql, new
        {
            Skip = skipClamped,
            PageSize = pageSizeClamped,
            Status = string.IsNullOrWhiteSpace(status) ? null : status!.Trim(),
            RunType = string.IsNullOrWhiteSpace(runType) ? null : runType!.Trim(),
            AllowedDomainsJson = allowedDomainsJson,
        }, db: DatabaseId.APS);

        // 过滤后全量运行数：独立 COUNT 与列表同 WHERE（含范围过滤），Total 为受限用户可见的过滤后全量，页码对齐。
        var countSql = $@"SELECT COUNT(1) {fromSql}
            {scopeClause}";
        var total = await _connectionManager.QueryFirstOrDefaultAsync<int>(countSql, new
        {
            Status = string.IsNullOrWhiteSpace(status) ? null : status!.Trim(),
            RunType = string.IsNullOrWhiteSpace(runType) ? null : runType!.Trim(),
            AllowedDomainsJson = allowedDomainsJson,
        }, db: DatabaseId.APS);

        return new ScheduleRunGovPage
        {
            Items = rows.ToList(),
            Total = total,
            Page = (skipClamped / pageSizeClamped) + 1,
            PageSize = pageSizeClamped,
        };
    }

    /// <inheritdoc />
    public async Task<int> InsertForRecoveryWithShellsAsync(
        ScheduleRunGov source,
        IReadOnlyList<RecoveryShellSpec> shells,
        string triggeredBy,
        CancellationToken ct = default)
    {
        if (shells is null || shells.Count == 0)
        {
            throw new InvalidOperationException("FAILED 恢复建壳列表为空（须至少一个预期 Domain 壳）");
        }

        var now = DateTime.Now;
        var ts = now.ToString("yyyyMMddHHmmss");

        return await _connectionManager.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            const string insertRunSql = @"
                INSERT INTO [dbo].[ScheduleRun]
                    ([RunType], [Status], [TriggeredBy], [DataCutoffTime],
                     [StrategyProfileVersionId], [ExpectedDomainKeysJson], [ScopeJson], [StartedAt], [CreatedAt])
                OUTPUT INSERTED.[Id]
                VALUES (@RunType, 'RUNNING', @TriggeredBy, GETDATE(),
                        @StrategyProfileVersionId, @ExpectedDomainKeysJson, @ScopeJson, @StartedAt, @StartedAt)";

            var runId = await connection.QueryFirstOrDefaultAsync<int>(insertRunSql,
                new
                {
                    source.RunType,
                    TriggeredBy = triggeredBy,
                    source.StrategyProfileVersionId,
                    source.ExpectedDomainKeysJson,
                    ScopeJson = source.ScopeJson,
                    StartedAt = now,
                },
                transaction);

            if (runId <= 0)
            {
                throw new InvalidOperationException($"FAILED 恢复新建 ScheduleRun 失败（源运行 {source.Id}）");
            }

            const string insertShellSql = @"
                INSERT INTO [dbo].[PlanVersion]
                    ([VersionCode], [VersionCategory], [DomainKey],
                     [PlanHorizonStart], [PlanHorizonEnd], [ComputeMode], [Status],
                     [SourceScheduleRunId], [CreatedBy], [CreatedAt])
                OUTPUT INSERTED.[Id]
                VALUES (@VersionCode, 'RECOVERY', @DomainKey,
                        @PlanHorizonStart, @PlanHorizonEnd, 'FULL', 'BUILDING',
                        @SourceScheduleRunId, @CreatedBy, @CreatedAt)";

            foreach (var shell in shells)
            {
                var versionCode = $"RECOVERY_{ts}_{shell.DomainKey}";
                var shellId = await connection.QueryFirstOrDefaultAsync<int>(insertShellSql,
                    new
                    {
                        VersionCode = versionCode,
                        shell.DomainKey,
                        shell.PlanHorizonStart,
                        shell.PlanHorizonEnd,
                        SourceScheduleRunId = runId,
                        CreatedBy = triggeredBy,
                        CreatedAt = now,
                    },
                    transaction);

                if (shellId <= 0)
                {
                    throw new InvalidOperationException($"FAILED 恢复建壳失败（RunId={runId}, VersionCode={versionCode}）");
                }
            }

            _logger.LogInformation(
                "ScheduleRun 恢复新建成功（含 {ShellCount} 个 RECOVERY 壳）：NewRunId={NewRunId}, SourceRunId={SourceRunId}, RunType={RunType}",
                shells.Count, runId, source.Id, source.RunType);

            return runId;
        }, db: DatabaseId.APS);
    }

    public async Task<CandidateRunCreatedResult> CreateCandidateRunAsync(
        CandidateRunCreateSpec spec,
        long strategyProfileVersionId,
        string triggeredBy,
        CancellationToken ct = default)
    {
        // B-1 白天候选运行创建：ScheduleRun + Candidate 壳 单事务原子写（沿用 ReplaceActiveAsync 事务模式）。
        // 任一失败整体回滚，不产生孤立 RUNNING 运行；触发 2号位 主流程不在本方法内（契约接缝，IRunLifecycleService）。
        var now = DateTime.Now;
        var expectedDomainKeysJson = System.Text.Json.JsonSerializer.Serialize(new[] { spec.DomainKey });
        // A 口径（2号位 2026-09-21 回执定案）：ScopeJsonV2 序列化为契约字符串码
        // （BusinessTriggerType/PriorityMode 类型级 EnumMemberJsonConverter 读 EnumMember，实测 STJ 内置 JsonStringEnumConverter 不读 EnumMember），
        // null = 旧调用兼容不承载结构化范围
        var scopeJson = spec.Scope is null
            ? null
            : System.Text.Json.JsonSerializer.Serialize(spec.Scope);
        var versionCode = $"CANDIDATE_{spec.DomainKey}_{now:yyyyMMddHHmmss}";

        return await _connectionManager.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            const string insertRunSql = @"
                INSERT INTO [dbo].[ScheduleRun]
                    ([RunType], [Status], [TriggeredBy], [DataCutoffTime],
                     [BasePlanVersionId], [StrategyProfileVersionId], [ExpectedDomainKeysJson], [ScopeJson],
                     [StartedAt], [CreatedAt])
                OUTPUT INSERTED.[Id]
                VALUES (@RunType, 'RUNNING', @TriggeredBy, @DataCutoffTime,
                        @BasePlanVersionId, @StrategyProfileVersionId, @ExpectedDomainKeysJson, @ScopeJson,
                        @StartedAt, @StartedAt)";

            var runId = await connection.QueryFirstOrDefaultAsync<int>(insertRunSql,
                new
                {
                    RunType = spec.RunType,
                    TriggeredBy = triggeredBy,
                    DataCutoffTime = spec.DataCutoffTime ?? now,
                    BasePlanVersionId = spec.BasePlanVersionId,
                    StrategyProfileVersionId = strategyProfileVersionId,
                    ExpectedDomainKeysJson = expectedDomainKeysJson,
                    ScopeJson = scopeJson,
                    StartedAt = now,
                },
                transaction);

            if (runId <= 0)
            {
                throw new InvalidOperationException(
                    $"白天候选运行创建失败：ScheduleRun 写入未返回 Id（RunType={spec.RunType}, Domain={spec.DomainKey}）");
            }

            const string insertShellSql = @"
                INSERT INTO [dbo].[PlanVersion]
                    ([VersionCode], [VersionCategory], [DomainKey],
                     [PlanHorizonStart], [PlanHorizonEnd], [ComputeMode], [Status],
                     [SourceScheduleRunId], [CreatedBy], [CreatedAt])
                OUTPUT INSERTED.[Id]
                VALUES (@VersionCode, 'CANDIDATE', @DomainKey,
                        @PlanHorizonStart, @PlanHorizonEnd, 'FULL', 'BUILDING',
                        @SourceScheduleRunId, @CreatedBy, @StartedAt)";

            var shellId = await connection.QueryFirstOrDefaultAsync<int>(insertShellSql,
                new
                {
                    VersionCode = versionCode,
                    DomainKey = spec.DomainKey,
                    PlanHorizonStart = spec.PlanHorizonStart ?? DateTime.Today,
                    PlanHorizonEnd = spec.PlanHorizonEnd ?? DateTime.Today.AddDays(90),
                    SourceScheduleRunId = runId,
                    CreatedBy = triggeredBy,
                    StartedAt = now,
                },
                transaction);

            if (shellId <= 0)
            {
                throw new InvalidOperationException(
                    $"白天候选运行创建失败：Candidate 壳写入未返回 Id（RunId={runId}, Domain={spec.DomainKey}）");
            }

            return new CandidateRunCreatedResult
            {
                NewScheduleRunId = runId,
                NewPlanVersionId = shellId,
            };
        }, db: DatabaseId.APS);
    }

    /// <inheritdoc />
    public async Task<int> CountReferencingStrategyProfileVersionAsync(IReadOnlyList<long> strategyProfileVersionIds, CancellationToken ct = default)
    {
        // 2026-10-26 同 R1 口径：策略包删除 FK 防护（ScheduleRun.StrategyProfileVersionId 反查），不区分状态
        // （硬删策略包会连带 FK 违约，任何运行引用都阻断）。
        if (strategyProfileVersionIds == null || strategyProfileVersionIds.Count == 0)
        {
            return 0;
        }

        const string sql = @"
            SELECT COUNT(1) FROM [dbo].[ScheduleRun]
            WHERE [StrategyProfileVersionId] IN @Ids";

        return await _connectionManager.QueryFirstOrDefaultAsync<int>(
            sql, new { Ids = strategyProfileVersionIds.ToArray() }, db: DatabaseId.APS);
    }
}
