using LPS.APS.Core.Dto;

namespace LPS.APS.BusinessRules.Repositories;

/// <summary>
/// ODS/复杂事实Issue查询Repository接口
/// 统一聚合来自多个5号位事实源的Issue
/// </summary>
public interface IBusinessFactIssueRepository
{
    /// <summary>
    /// 查询BOM Workset Issues
    /// （11-11 4号位件：+issueType 等值 / +keyword 模糊 / +factoryCode 用户筛选——前两个新参，第三个修"参数收而不用"缺陷）
    /// </summary>
    Task<List<BusinessFactIssueDto>> QueryBomWorksetIssuesAsync(
        string? batchNo = null,
        string? materialCode = null,
        string? severity = null,
        string? reviewStatus = null,
        string? issueType = null,
        string? keyword = null,
        string? factoryCode = null,
        int skip = 0,
        int take = 50,
        CancellationToken ct = default,
        IReadOnlySet<string>? allowedFactories = null);

    /// <summary>
    /// 查询MaterialStageDeptContext Issues
    /// （11-11 同上新增三参；本源无单据号列，keyword 只打 Detail/MaterialCode——两源各查各的，不跨源取数）
    /// </summary>
    Task<List<BusinessFactIssueDto>> QueryMaterialStageContextIssuesAsync(
        string? batchNo = null,
        string? materialCode = null,
        string? severity = null,
        string? reviewStatus = null,
        string? issueType = null,
        string? keyword = null,
        string? factoryCode = null,
        int skip = 0,
        int take = 50,
        CancellationToken ct = default,
        IReadOnlySet<string>? allowedFactories = null);

    /// <summary>
    /// 聚合查询所有ODS/复杂事实Issues
    /// </summary>
    Task<List<BusinessFactIssueDto>> QueryAllAsync(
        string? source = null,
        string? materialCode = null,
        string? factoryCode = null,
        string? severity = null,
        string? reviewStatus = null,
        string? issueType = null,
        string? keyword = null,
        int skip = 0,
        int take = 100,
        CancellationToken ct = default,
        IReadOnlySet<string>? allowedFactories = null);

    /// <summary>聚合命中总数（分页前，BOM Workset Issues + MaterialStageDeptContext Issues；11-11 起与 QueryAllAsync 同参数集——旧版缺 factoryCode/issueType/keyword 致 total 与列表分叉）</summary>
    Task<int> CountAllAsync(
        string? source = null,
        string? materialCode = null,
        string? factoryCode = null,
        string? severity = null,
        string? reviewStatus = null,
        string? issueType = null,
        string? keyword = null,
        CancellationToken ct = default,
        IReadOnlySet<string>? allowedFactories = null);
}
