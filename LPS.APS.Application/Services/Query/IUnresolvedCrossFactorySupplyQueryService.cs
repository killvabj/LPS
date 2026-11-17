using LPS.APS.Application.Services.Query.Dto;

namespace LPS.APS.Application.Services.Query;

/// <summary>
/// 跨厂未闭合净缺口**只读**查询服务（CFV1-08，2026-11-13）。
///
/// <para><b>在冻结链路中的位置</b>（`05 数据架构 v1.51 §7`）：
/// <c>4号位 →HTTP→ 5号位 Controller → 5号位 Scope 授权 ↔ 本服务 → APS 运行真相</c>。</para>
/// <list type="bullet">
///   <item>**HTTP 不由本服务提供**——4号位 不得直接调用 2号位 HTTP（`13 v1.9`）；
///         对外暴露与 Scope 二次校验在 5号位。</item>
///   <item>本服务**只读**，不重算 Pegging 数量、不推断源厂、不生成虚拟 SH/PI。</item>
/// </list>
///
/// <para><b>为什么单独一个服务而不并入 <c>IScheduleQueryService</c></b>：
/// 后者当前由他号位在改（SVN 单写）；本服务为本批新增能力独立成件，
/// 复用同一套查询层约定（Dapper 直读 `APS_Production`、无业务推算）。</para>
/// </summary>
public interface IUnresolvedCrossFactorySupplyQueryService
{
    /// <summary>
    /// 按 Run/PV 及可选维度查询未闭合结果。字段**原样返回**，不做推算。
    /// </summary>
    /// <param name="scheduleRunId">Run 号（与 <paramref name="planVersionId"/> 一起构成必填口径）。</param>
    /// <param name="planVersionId">计划版本号。</param>
    /// <param name="domainKey">可选：按 Domain 过滤。</param>
    /// <param name="rootDemandKey">可选：按根需求键过滤。</param>
    /// <param name="materialCode">可选：按物料码过滤（精确等值，不用 LIKE）。</param>
    /// <param name="targetFactoryId">可选：按目标厂过滤。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>
    /// 永不因查询异常而返回「空结果」——异常时返回
    /// <see cref="UnresolvedCrossFactorySupplyQueryDto.ResultState"/> = <c>QUERY_FAILED</c>，
    /// 使调用方能把「可信零缺口」与「查询失败」区分开（`06 v1.42` 第 5 条、`13 v1.9`）。
    /// </returns>
    Task<UnresolvedCrossFactorySupplyQueryDto> QueryAsync(
        int scheduleRunId,
        int planVersionId,
        string? domainKey = null,
        string? rootDemandKey = null,
        string? materialCode = null,
        int? targetFactoryId = null,
        CancellationToken cancellationToken = default);
}