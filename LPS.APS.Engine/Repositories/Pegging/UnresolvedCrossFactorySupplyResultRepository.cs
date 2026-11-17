using Dapper;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Engine.Repositories.Pegging;

/// <summary>
/// 跨厂未闭合净缺口结果仓储（CFV1-08，2026-11-13）。表定义见
/// `08 DDL v5.1.8.11` :9985-10050；建表脚本
/// `Database/Scripts/APS/03_SQL脚本/建表_APS_UnresolvedCrossFactorySupplyResult_20261113.sql`。
///
/// <para><b>为什么是「先删后插」而不是 MERGE</b>：冻结要求「重算先替换本 Run/PV 相关结果而不保留伪历史」
/// 且「同输入重跑必须幂等」。MERGE 只增不减会留下上一轮的脏结果（同族教训 `merge-no-delete-accumulates`）。</para>
///
/// <para><b>为什么用 <see cref="DatabaseConnectionManager.ExecuteInTransactionAsync{T}"/></b>：
/// `DatabaseConnectionManager` 的 APS 连接是**单例共享连接 + 信号量**（`GetConnectionAsync` 拿信号量、
/// `ReleaseConnection` 还）。直接 `GetConnectionAsync` 而忘记配对释放 ⇒ **第二次调用永久阻塞**。
/// 走统一的 `ExecuteInTransactionAsync` 即自动配对，且失败回滚拓扑也由它负责。</para>
/// </summary>
public class UnresolvedCrossFactorySupplyResultRepository : IUnresolvedCrossFactorySupplyResultRepository
{
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly ILogger<UnresolvedCrossFactorySupplyResultRepository> _logger;

    public UnresolvedCrossFactorySupplyResultRepository(
        DatabaseConnectionManager connectionManager,
        ILogger<UnresolvedCrossFactorySupplyResultRepository> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    private const string DeleteSql = @"
        DELETE FROM [dbo].[APS_UnresolvedCrossFactorySupplyResult]
         WHERE [ScheduleRunId] = @ScheduleRunId AND [PlanVersionId] = @PlanVersionId";

    private const string InsertSql = @"
        INSERT INTO [dbo].[APS_UnresolvedCrossFactorySupplyResult]
            ([ScheduleRunId], [PlanVersionId], [ResultRecordKey],
             [RootOrderId], [RootDemandKey], [MaterialId], [MaterialCode],
             [TargetFactoryId], [DomainKey], [SourceFactoryId], [SourceDomainKey], [NeedByTime],
             [EvidenceBasisCode], [IssueRemark],
             [RequiredQty], [LegallyAllocatedQty], [UnresolvedQty], [UnresolvedCategory],
             [ActualSHOrderNo], [ActualProductionInstructionNo],
             [SourceEvidenceJson], [CommitmentStatus], [CreatedAt])
        VALUES
            (@ScheduleRunId, @PlanVersionId, @ResultRecordKey,
             @RootOrderId, @RootDemandKey, @MaterialId, @MaterialCode,
             @TargetFactoryId, @DomainKey, @SourceFactoryId, @SourceDomainKey, @NeedByTime,
             @EvidenceBasisCode, @IssueRemark,
             @RequiredQty, @LegallyAllocatedQty, @UnresolvedQty, @UnresolvedCategory,
             @ActualSHOrderNo, @ActualProductionInstructionNo,
             @SourceEvidenceJson, @CommitmentStatus, @CreatedAt)";

    public async Task ReplaceForRunAsync(
        int scheduleRunId,
        int planVersionId,
        IReadOnlyList<UnresolvedCrossFactorySupplyResult> rows,
        CancellationToken cancellationToken = default)
    {
        // CreatedAt 由本方法统一打一次时刻，避免逐行飘（同 Run 同一批结果应同时刻）。
        // 注意：**不是**「当前时点可当 NeedByTime」——NeedByTime 只填真实可证值（冻结原文）。
        var now = DateTime.Now;   // 本地时间（项目铁律：全链路禁 UTC）

        await _connectionManager.ExecuteInTransactionAsync(async (conn, tx) =>
        {
            // DELETE **无条件执行** ⇒ 本轮已全部闭合时天然清理上一轮的异常候选
            // （CF-04 终态写入保护：业务成功闭合不得留下「时间未闭合」脏记录）。
            await conn.ExecuteAsync(new CommandDefinition(
                DeleteSql,
                new { ScheduleRunId = scheduleRunId, PlanVersionId = planVersionId },
                transaction: tx,
                cancellationToken: cancellationToken));

            if (rows.Count > 0)
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    InsertSql,
                    rows.Select(r => new
                    {
                        ScheduleRunId  = scheduleRunId,
                        PlanVersionId  = planVersionId,
                        r.ResultRecordKey,
                        r.RootOrderId,
                        r.RootDemandKey,
                        r.MaterialId,
                        r.MaterialCode,
                        r.TargetFactoryId,
                        r.DomainKey,
                        r.SourceFactoryId,
                        r.SourceDomainKey,
                        r.NeedByTime,
                        r.EvidenceBasisCode,
                        r.IssueRemark,
                        r.RequiredQty,
                        r.LegallyAllocatedQty,
                        r.UnresolvedQty,
                        r.UnresolvedCategory,
                        r.ActualSHOrderNo,
                        r.ActualProductionInstructionNo,
                        r.SourceEvidenceJson,
                        CommitmentStatus = UnresolvedCommitmentStatus.NotCommitted,
                        CreatedAt = now
                    }),
                    transaction: tx,
                    cancellationToken: cancellationToken));
            }

            return 0;
        });

        _logger.LogInformation(
            "[Pegging][CFV1-08] 未闭合结果替换完成：Run={RunId} PV={Pv} 行数={Count}（先删后插、同事务）",
            scheduleRunId, planVersionId, rows.Count);
    }

    public async Task<IReadOnlyList<UnresolvedCrossFactorySupplyResult>> GetByRunAsync(
        int scheduleRunId,
        int planVersionId,
        CancellationToken cancellationToken = default)
    {
        const string sql = @"
            SELECT * FROM [dbo].[APS_UnresolvedCrossFactorySupplyResult]
             WHERE [ScheduleRunId] = @ScheduleRunId AND [PlanVersionId] = @PlanVersionId
             ORDER BY [Id]";

        var rows = await _connectionManager.QueryAsync<UnresolvedCrossFactorySupplyResult>(
            sql,
            new { ScheduleRunId = scheduleRunId, PlanVersionId = planVersionId },
            db: DatabaseId.APS);

        return rows.ToList();
    }
}