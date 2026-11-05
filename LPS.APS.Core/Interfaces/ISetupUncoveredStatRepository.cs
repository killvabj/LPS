using LPS.APS.Core.DTOs.Setup;

namespace LPS.APS.Core.Interfaces;

/// <summary>
/// Setup 规则缺失（uncovered-stats）只读聚合查询仓储契约。
/// 数据源 = [dbo].[Task].[SetupSource] = 'SETUP_RULE_MISSING_ZERO_FALLBACK' 的命中频次聚合
/// （0 分钟规则缺失兜底，v1.4 §十一 KPI 监控；runId 经 PlanVersion.SourceScheduleRunId JOIN）。
/// 只读查询，无任何写操作；红线 #6 不涉及（不建表不改 DDL）。3号位 依 G4 只读查询归属自写。
/// </summary>
public interface ISetupUncoveredStatRepository
{
    /// <summary>
    /// 按四级维度（runId → 部门 → 工序 → 设备 → 产品）聚合规则缺失命中分布。
    /// 全部过滤参数可选；未传该级即不筛。返回按维度分组聚合行（含 Code 回带 + 首条示例 TaskId）。
    /// 只读查询；空结果返回空列表（非 null）。
    /// </summary>
    Task<IReadOnlyList<SetupUncoveredStatDto>> GetUncoveredAsync(
        int? runId,
        int? departmentId,
        string? operationCode,
        int? resourceId,
        int? materialId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按四级维度（runId → 部门 → 工序 → 设备 → 产品）聚合规则缺失命中分布的**服务端分页**版（方案 A 新增，2026-10-28）。
    /// 与 <see cref="GetUncoveredAsync"/> 同一 WHERE / GROUP BY / ORDER，仅加 OFFSET/FETCH；Total 经窗口函数 COUNT(*) OVER() 同查询取得。
    /// 另带 KPI summary（DepartmentCount/StageCount，distinct，与列表同 WHERE）——服务端分页后列表仅当页、三张 KPI 卡须由后端给全量口径。
    /// 只读查询；空结果返回空 Items + Total 0 + 各计数 0。pageIndex/pageSize 须已由调用方规范化（≥1 且 pageSize ≤ 上限）。
    /// </summary>
    /// <param name="runId">排程 Run Id（可选）</param>
    /// <param name="departmentId">生产部门 Id（可选）</param>
    /// <param name="operationCode">工序编码（可选）</param>
    /// <param name="resourceId">资源 Id（可选）</param>
    /// <param name="materialId">物料 Id（可选）</param>
    /// <param name="pageIndex">页码（1 基，调用方已 ≥1）</param>
    /// <param name="pageSize">每页条数（调用方已规范化）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>分页容器（Items/Total/Page/PageSize + DepartmentCount/StageCount）</returns>
    Task<SetupUncoveredStatsPage> GetUncoveredPageAsync(
        int? runId,
        int? departmentId,
        string? operationCode,
        int? resourceId,
        int? materialId,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default);
}