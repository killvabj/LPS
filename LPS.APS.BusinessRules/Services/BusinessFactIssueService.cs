using LPS.APS.BusinessRules.Repositories;
using LPS.APS.Core.Dto;

namespace LPS.APS.BusinessRules.Services;

/// <summary>
/// ODS/复杂事实Issue业务服务（5号位提供给4号位）
///
/// 用于Explanation辅助、事实异常展示
/// 统一聚合来自多个5号位事实源的Issue
/// </summary>
public class BusinessFactIssueService
{
    private readonly IBusinessFactIssueRepository _repository;

    public BusinessFactIssueService(IBusinessFactIssueRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <summary>
    /// 聚合查询所有ODS/复杂事实Issues
    /// </summary>
    public async Task<List<BusinessFactIssueDto>> QueryAllAsync(
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
        IReadOnlySet<string>? allowedFactories = null)
    {
        // 越界夹上限不静默回落（4号位 10-19 R2；同 PI/订单/甘特页口径，全项目已扫净）。
        if (take <= 0) take = 100;
        if (take > 500) take = 500;

        return await _repository.QueryAllAsync(
            source, materialCode, factoryCode, severity, reviewStatus,
            issueType, keyword, skip, take, ct, allowedFactories);
    }

    /// <summary>
    /// 聚合命中总数（分页前，供 PageResult.Total）。
    /// 11-11（4号位件）：**与 QueryAllAsync 同参数集**——旧签名缺 factoryCode/issueType/keyword，
    /// 带这三个筛选时 total 会大于列表实际口径（total/列表分叉缺陷同型清扫）。
    /// </summary>
    public async Task<int> CountAllAsync(
        string? source = null,
        string? materialCode = null,
        string? factoryCode = null,
        string? severity = null,
        string? reviewStatus = null,
        string? issueType = null,
        string? keyword = null,
        CancellationToken ct = default,
        IReadOnlySet<string>? allowedFactories = null)
    {
        return await _repository.CountAllAsync(
            source, materialCode, factoryCode, severity, reviewStatus,
            issueType, keyword, ct, allowedFactories);
    }
}
