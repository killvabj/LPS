using LPS.APS.Engine.Services.Sync.Dto;

namespace LPS.APS.Engine.Services.Sync;

/// <summary>
/// PI 级 XC 夜间快照同步（2号位职责 — 每日 00:37）
///
/// 链路：
///   ODS `ODS_ProductionInstructionInventoryBalance`（5号位，8 列）
///     → APS 同义词 `ext_ODS_ProductionInstructionInventoryBalance`
///       → `sp_SyncPiXcSnapshot`（TRUNCATE + 全量 INSERT）
///         → `InventoryFact_PiXc`（本服务刷新，单批快照）
///
/// 冻结依据：`11 v2.5 §174/§175`、`05 v1.49`、`08 §8a`。
///
/// ⚠️ 失败时 SP **不清表**：保留上一批，由 Run 侧新鲜度闸门显式拦截，
///    绝不把「源不可用」伪装成「XC = 0」（`11 §174`、`06 :471`）。
/// </summary>
public interface IPiXcSnapshotSyncService
{
    /// <summary>
    /// 刷新 PI 级 XC 快照（单事务 TRUNCATE + INSERT）
    /// </summary>
    Task<PiXcSnapshotSyncResultDto> SyncAsync(CancellationToken cancellationToken = default);
}
