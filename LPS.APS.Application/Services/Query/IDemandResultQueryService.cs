using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LPS.APS.Core.Dto;

namespace LPS.APS.Application.Services.Query;

/// <summary>
/// A34／A35 两张结果表的**读侧**（0号位 总表 §二：「两表同 PV 原子写／**Query**／清理」、
/// 「批量 Query→5号位」；§〇 职责分工：「2号位负责自身写侧、**Query** 和 MES MACH 业务」）。
///
/// <para><b>没有读侧会怎样</b>：数据落了库却无人可取——与改造前 `DemandCompletions` 只在内存、
/// 零消费是同一个后果。故 Query 是这两张表合同的组成部分，不是可选加分项。</para>
/// </summary>
public interface IDemandResultQueryService
{
    /// <summary>
    /// 按 PlanVersionId 查 A34 需求级时间结果。
    /// </summary>
    /// <param name="planVersionId">**必填**。跨 PV 取数会把不同 Run 的时间混在一起，故不提供"全部 PV"。</param>
    /// <param name="logicalDemandKey">可选：精确到某个需求。</param>
    /// <param name="materialId">可选：精确到某个物料。</param>
    /// <param name="resultType">可选：<c>TASK_WITH_TIMING</c> / <c>TIMING_ONLY</c>。</param>
    /// <param name="timeStatus">可选：<c>CERTAIN</c> / <c>UNCERTAIN</c> / <c>UNKNOWN</c>。</param>
    /// <param name="usableOnly">true ⇒ 只返回可当承诺用的行（`CompletionTimeCertain = 1`）。</param>
    /// <remarks>
    /// **分页语义（0号位 v1.20 §二十四.3）**：返回 <see cref="DemandResultPage{T}"/>，
    /// `HasMore = true` 表示还有下一批；**不得把"本页 5,000 条"当成全部**。
    /// 内部多探测 1 行，不跑全量 `COUNT(*)`。
    /// </remarks>
    Task<DemandResultPage<DemandStageTimingResultView>> QueryStageTimingAsync(
        int planVersionId,
        string? logicalDemandKey = null,
        int? materialId = null,
        string? resultType = null,
        string? timeStatus = null,
        bool usableOnly = false,
        int skip = 0,
        int take = 500,
        CancellationToken ct = default);

    /// <summary>
    /// 按 PlanVersionId 查 A35 技术异常。
    /// </summary>
    /// <param name="planVersionId">**必填**，理由同上。</param>
    /// <param name="logicalDemandKey">可选。</param>
    /// <param name="issueCode">可选：技术异常码。</param>
    /// <param name="materialId">可选。</param>
    Task<DemandResultPage<DemandScheduleTechnicalIssueView>> QueryTechnicalIssuesAsync(
        int planVersionId,
        string? logicalDemandKey = null,
        string? issueCode = null,
        int? materialId = null,
        int skip = 0,
        int take = 500,
        CancellationToken ct = default);
}