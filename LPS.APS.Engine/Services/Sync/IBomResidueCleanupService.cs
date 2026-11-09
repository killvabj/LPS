namespace LPS.APS.Engine.Services.Sync;

/// <summary>
/// BOM 请求残留清理（2号位值守 — 每日 01:00）。
///
/// 责任划分（5号位 2026-11-09《回两函》§四 确认，Owner 同日拍定）：
///   · **5号位** = 出 SP ＋ 管口径 ＋ 验收（SP 是本引擎合同对象，语义变更一律其改稿）；
///   · **2号位** = 每日 EXEC ＋ 排期 ＋ 值守；
///   · **Owner**  = 一次性授权 SP 注册入库（已于 2026-11-09 08:41:45 完成）。
///
/// SP：`MES_Integration.dbo.sp_CleanupBOMRequestResidue`（**无参数**）
///   段1 `PENDING ∧ 非RT ∧ CreatedAt&lt;当日`  → 先删明细后翻头 `SUPERSEDED`
///   段2 `SUPERSEDED/CONSUMED/REALTIME ∧ 超30天` → 先删残留明细再删头
///   段3 `RT:` 锚定头 — `REALTIME` 上线前不动
///   日志 `MES_API_BOM_Request_Log`，`BatchNo='CLEANUP_yyyyMMdd'`
///
/// 首跑（历史清欠）已于 2026-11-09 12:09:19 执行：51 批置 SUPERSEDED／删明细 9,688,854／
/// 段2 删头 18／删明细 1,088,819；清理后全库明细 11,997,887→1,220,214。
/// </summary>
public interface IBomResidueCleanupService
{
    /// <summary>执行一次清理，返回本次 `CLEANUP_yyyyMMdd` 日志行的报文；无日志行时返回 null。</summary>
    Task<string?> CleanupAsync(CancellationToken cancellationToken = default);
}