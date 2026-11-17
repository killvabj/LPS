using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnresolvedCrossFactorySupplyResult = LPS.APS.Core.Entities.APS.UnresolvedCrossFactorySupplyResult;

namespace LPS.APS.Core.Interfaces;

/// <summary>
/// 跨厂未闭合净缺口结果仓储（CFV1-08，2026-11-13）。
///
/// <para><b>口径</b>：一个 Run/PV 只有一套真相 —— 写入按 Run+PV **先替换后写入**（幂等），
/// 重跑不得留伪历史；不得把结果写进 Supply / Allocation / BOM Issue / 订单主档。</para>
/// </summary>
public interface IUnresolvedCrossFactorySupplyResultRepository
{
    /// <summary>
    /// 用 <paramref name="rows"/> **整体替换**该 Run+PV 的未闭合结果（先删本 Run/PV 旧行，再批量插入）。
    /// <paramref name="rows"/> 为空 ⇒ 只执行删除（用于「本 Run 已全部闭合、须清理异常候选」的终态保护）。
    /// </summary>
    Task ReplaceForRunAsync(
        int scheduleRunId,
        int planVersionId,
        IReadOnlyList<UnresolvedCrossFactorySupplyResult> rows,
        CancellationToken cancellationToken = default);

    /// <summary>只读：按 Run/PV 取全部结果（供查询侧与探针核对）。</summary>
    Task<IReadOnlyList<UnresolvedCrossFactorySupplyResult>> GetByRunAsync(
        int scheduleRunId,
        int planVersionId,
        CancellationToken cancellationToken = default);
}
