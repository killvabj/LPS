using LPS.APS.Engine.Services.Sync.Dto;

namespace LPS.APS.Engine.Services.Sync;

/// <summary>
/// 物料×阶段→默认生产部门 的**定向补缺**服务（2号位职责）
///
/// 与 <see cref="IMaterialStageDeptContextRebuildService"/> 的分工：
///   · RebuildAsync = FULL 全量重建，挂每日 01:55，改的是**全库**，无 DryRun。
///   · 本服务 = 乙方案（0号位 2026-11-02 v3.1 §2.2）的「圈定对象 → 分层解析 → 只补不改」，
///     范围由调用方给定（某个 Domain 或一批显式键），**默认 DryRun，一行不写**。
///
/// 为什么要有它：FULL 路径无法回答「本 Domain 真实待排的这些键到底补上了没有」，
/// 而 PM 要求的验收方式恰恰是「一个受控 Domain 真实跑一次、看前后差」。
///
/// SP 契约：sp_RebuildMaterialStageDeptContext_Targeted
///          （Database/Scripts/APS/03_SQL脚本/APS_add_sp_RebuildMaterialStageDeptContext_Targeted_20261102.sql）
///          该过程**不读 Routing/Route 表** —— 任何情况不允许用 Routing 部门兜底。
/// </summary>
public interface ITargetedMaterialStageDeptContextService
{
    /// <summary>
    /// 按 DomainKey 圈定目标键并定向补缺。
    /// 目标键 = 该 Domain 全部订单的 BOMNO 在 APS_BOM_STAGE_PATH_RAW 展开出的 (MaterialId, StageCode)。
    /// </summary>
    /// <param name="domainKey">域键，对应 [Order].DomainKey</param>
    /// <param name="dryRun">true（默认）= 只算不写</param>
    Task<TargetedMaterialStageDeptContextResultDto> RebuildForDomainAsync(
        string domainKey,
        bool dryRun = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按调用方显式给定的一批键定向补缺（不再从 Domain/BOM 推导）。
    /// </summary>
    /// <param name="keys">目标键集合，(MaterialId, StageCode)</param>
    /// <param name="dryRun">true（默认）= 只算不写</param>
    Task<TargetedMaterialStageDeptContextResultDto> RebuildAsync(
        IReadOnlyCollection<MaterialStageKey> keys,
        bool dryRun = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 只推导目标键、不调 SP —— 供「先看范围有多大」和单元测试使用。
    /// </summary>
    Task<IReadOnlyList<MaterialStageKey>> DeriveTargetKeysAsync(
        string domainKey,
        CancellationToken cancellationToken = default);
}
