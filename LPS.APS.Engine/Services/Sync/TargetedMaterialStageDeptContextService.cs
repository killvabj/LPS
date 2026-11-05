using System.Data;
using System.Text.Json;
using Dapper;
using LPS.APS.Engine.Data;
using LPS.APS.Engine.Services.Sync.Dto;
using Microsoft.Extensions.Logging;

namespace LPS.APS.Engine.Services.Sync;

/// <summary>
/// 物料×阶段→默认生产部门 定向补缺服务实现（乙方案，0号位 2026-11-02 v3.1 §2.2）
///
/// 四层顺序由 SP 落实，本服务只负责三件事：
///   1. 圈定目标键（第①层：由调用方给定，本服务从 Domain 的订单 BOM 展开，不做全库推导）
///   2. 把目标键序列化成 SP 约定的 JSON（键名必须恰好是 m / s）
///   3. 调 SP 并把结果搬回 DTO
///
/// 默认 dryRun = true：任何一处没显式传 false，都不会写库。
/// </summary>
public class TargetedMaterialStageDeptContextService : ITargetedMaterialStageDeptContextService
{
    private const string SpName = "sp_RebuildMaterialStageDeptContext_Targeted";

    /// <summary>
    /// 目标键推导：该 Domain 全部订单的 BOMNO → BOM 阶段路径 → (物料, 阶段)。
    ///
    /// 取 ChildMaterialCode 而不是 ParentMaterialCode：APS_BOM_STAGE_PATH_RAW 里
    /// ROOT 行（ParentMaterialCode IS NULL）的 ChildMaterialCode 才是「该阶段的生产对象」，
    /// EDGE 行的 ChildMaterialCode 是子件。两者都以 ChildMaterialCode 为物料，故统一取它。
    /// 不按 BatchNo 过滤：该表当前单批次全量重写，多批次并存时取并集只会放大范围、不会写错行。
    ///
    /// ⚠ 范围口径（照实记，别当成完全满足 0号位 v3.1 §2.2①）：
    ///   · 本查询 = 「本 Domain 订单需求沿 BOM 阶段路径的全展开」，是**待排需求的超集**：
    ///     同一 BOMNO 里本轮并不投产的旁支子件也会被圈进来。
    ///   · §2.2① 另两条收缩本查询做不到，因为库里没有判据：
    ///       ①「采购等非生产阶段不补」—— 没有生产/采购阶段的权威标记；
    ///          `Material.IsPurchased` 全库 0 行 = 1（死列），`ProductionDepartment` 只有 24 个
    ///          阶段值且缺 FINAL/OUTS，拿它当白名单会把 Z6 争议阶段一并误剔。
    ///       ②「BOM 仅出现但本轮无实际生产需求」—— 求解前不存在 Task/Pegging 行，
    ///          唯一可用的需求源就是 [Order]，本身无法区分「出现」与「需要」。
    ///   需要更窄的范围时，调用 `RebuildAsync(keys)` 由调用方显式给键（例如编排器按本轮
    ///   实际需求集给），本服务不替调用方猜。
    /// </summary>
    private const string DeriveTargetKeysSql = @"
SELECT DISTINCT mat.Id AS MaterialId, r.StageCode
FROM APS_BOM_STAGE_PATH_RAW r
JOIN (
        SELECT DISTINCT BOMNO
        FROM [Order]
        WHERE DomainKey = @DomainKey
          AND BOMNO IS NOT NULL AND BOMNO <> ''
     ) b ON b.BOMNO = r.BOMNO
JOIN Material mat ON mat.MaterialCode = r.ChildMaterialCode
WHERE r.StageCode IS NOT NULL AND r.StageCode <> ''
ORDER BY mat.Id, r.StageCode;";

    private readonly DatabaseConnectionManager _connectionManager;
    private readonly ILogger<TargetedMaterialStageDeptContextService> _logger;

    public TargetedMaterialStageDeptContextService(
        DatabaseConnectionManager connectionManager,
        ILogger<TargetedMaterialStageDeptContextService> logger)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MaterialStageKey>> DeriveTargetKeysAsync(
        string domainKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(domainKey))
        {
            throw new ArgumentException("domainKey 不能为空", nameof(domainKey));
        }

        var rows = await _connectionManager.QueryAsync<MaterialStageKey>(
            DeriveTargetKeysSql,
            new { DomainKey = domainKey },
            CommandType.Text,
            DatabaseId.APS,
            commandTimeout: 600);

        return rows.ToList();
    }

    /// <inheritdoc />
    public async Task<TargetedMaterialStageDeptContextResultDto> RebuildForDomainAsync(
        string domainKey,
        bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "定向补缺：开始圈定 Domain={DomainKey} 的目标键（DryRun={DryRun}）", domainKey, dryRun);

        var keys = await DeriveTargetKeysAsync(domainKey, cancellationToken);

        _logger.LogInformation(
            "定向补缺：Domain={DomainKey} 圈定目标键 {Count} 个", domainKey, keys.Count);

        return await RebuildAsync(keys, dryRun, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<TargetedMaterialStageDeptContextResultDto> RebuildAsync(
        IReadOnlyCollection<MaterialStageKey> keys,
        bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        var batchNo = $"MSC_CTX_TGT_{DateTime.Now:yyyyMMdd_HHmmss}";

        if (keys is null || keys.Count == 0)
        {
            _logger.LogWarning("定向补缺：目标键为空，未调用 {Sp}（BatchNo={BatchNo}）", SpName, batchNo);
            return new TargetedMaterialStageDeptContextResultDto
            {
                BatchNo = batchNo,
                DryRun = dryRun,
                ErrorMessage = "目标键集合为空：Domain 没有可用 BOMNO，或 BOM 阶段路径未装载"
            };
        }

        // 键名必须是 m / s —— 与 SP 内 JSON_VALUE(v.value,'$.m'/'$.s') 一一对应。
        // 改了这里就等于改了 SP 契约，会静默解析出空集合（SP 会以「目标键集合为空」报错，不会写脏数据）。
        var payload = keys
            .Select(k => new { m = k.MaterialId, s = k.StageCode })
            .ToList();
        var targetKeysJson = JsonSerializer.Serialize(payload);

        _logger.LogInformation(
            "定向补缺开始: BatchNo={BatchNo}, 目标键={Count}, JSON={JsonBytes}B, DryRun={DryRun}",
            batchNo, keys.Count, System.Text.Encoding.UTF8.GetByteCount(targetKeysJson), dryRun);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var spParams = new DynamicParameters();
            spParams.Add("@BatchNo", batchNo);
            spParams.Add("@TargetKeysJson", targetKeysJson);
            spParams.Add("@DryRun", dryRun);
            spParams.Add("@RowsInserted", dbType: DbType.Int32, direction: ParameterDirection.Output);
            spParams.Add("@RowsExpired", dbType: DbType.Int32, direction: ParameterDirection.Output);
            spParams.Add("@RowsFallback", dbType: DbType.Int32, direction: ParameterDirection.Output);
            spParams.Add("@IssueCount", dbType: DbType.Int32, direction: ParameterDirection.Output);
            spParams.Add("@ErrorMessage", dbType: DbType.String, size: 4000, direction: ParameterDirection.Output);

            var row = await _connectionManager.QueryFirstOrDefaultAsync<TargetedMaterialStageDeptContextResultDto>(
                SpName,
                spParams,
                CommandType.StoredProcedure,
                DatabaseId.APS,
                commandTimeout: 600);

            stopwatch.Stop();

            var result = row ?? new TargetedMaterialStageDeptContextResultDto();
            result.BatchNo = batchNo;
            result.DryRun = dryRun;
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
            result.ErrorMessage = spParams.Get<string?>("@ErrorMessage");

            if (result.IsSuccess)
            {
                _logger.LogInformation(
                    "定向补缺完成: BatchNo={BatchNo}, DryRun={DryRun}, 目标={Target}, " +
                    "Override={L2}, ACTIVITY=1={L3}, ACTIVITY=0兜底={L4}, 未解析={Unresolved}, " +
                    "写入={Ins}, 失效={Exp}, Issue={Iss}(冲突1层={I1}/冲突0层={I0}/Override不合规={IOv}/人工保护={IMan}/无部门={INo}), " +
                    "耗时={Elapsed}ms",
                    batchNo, dryRun, result.TargetKeys,
                    result.LayerOverride, result.LayerActivity1, result.LayerActivity0, result.UnresolvedKeys,
                    result.RowsInserted, result.RowsExpired, result.IssueCount,
                    result.IssueAct1MultiDept, result.IssueAct0MultiDept,
                    result.IssueOverrideInvalid, result.IssueManualProtected, result.IssueNoDept,
                    result.ElapsedMs);
            }
            else
            {
                _logger.LogError(
                    "定向补缺 SP 返回错误: BatchNo={BatchNo}, Error={ErrorMessage}",
                    batchNo, result.ErrorMessage);
            }

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "定向补缺异常: BatchNo={BatchNo}", batchNo);
            throw;
        }
    }
}
