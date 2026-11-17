namespace LPS.APS.Core.DTOs.Governance;

/// <summary>
/// ScheduleRun 运行历史**服务端分页**响应容器（R4，4号位 2026-11-11 运行与下发页改版：runs 列表分页 + total）。
/// 与 <see cref="SetupUncoveredStatsPage"/> 同形（items/total/page/pageSize，前端渲染 ElPagination），
/// 数据沿用 <see cref="ScheduleRunGov"/>（冻结只读列，不替代 2号位 运行模型）。
/// Total = 过滤后全量运行数（经窗口函数 COUNT(*) OVER() 与列表同 WHERE，一次查询取得；优于分裂两次 COUNT）。
/// </summary>
/// <remarks>开发者：3号位；边界同 <see cref="ScheduleRunGov"/>：只读冻结列，不改 2号位 运行结果语义。</remarks>
public sealed class ScheduleRunGovPage
{
    /// <summary>当前页运行行</summary>
    public IReadOnlyList<ScheduleRunGov> Items { get; set; } = Array.Empty<ScheduleRunGov>();

    /// <summary>运行总数（过滤后全量，前端 total 联动）</summary>
    public int Total { get; set; }

    /// <summary>当前页码（1 基）</summary>
    public int Page { get; set; }

    /// <summary>每页条数</summary>
    public int PageSize { get; set; }
}