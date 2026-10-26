using System.Data;
using LPS.APS.Application.Services.Dto;
using LPS.APS.Core.Authorization;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;

namespace LPS.APS.Application.Services;

/// <summary>
/// 资源主表查询服务（5号位；G1 设备下拉数据源）
/// 数据源：dbo.Resource（只读，不改资源主档）
/// </summary>
public interface IResourceService
{
    /// <summary>
    /// 资源主表列表（可按部门/类型过滤；含 hasCalendar 日历状态）。
    /// actorUserId 用于业务范围收窄（S5-01 补漏，4号位 10-23 R1）；无效值 0 → 解析为拒绝全部（安全默认）。
    /// </summary>
    Task<List<ResourceListItemDto>> GetListAsync(int actorUserId, int? departmentId = null, string? resourceType = null, bool includeInactive = false, CancellationToken ct = default);
}

public class ResourceService : IResourceService
{
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly IDataScopeService _dataScopeService;

    public ResourceService(DatabaseConnectionManager connectionManager, IDataScopeService dataScopeService)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _dataScopeService = dataScopeService ?? throw new ArgumentNullException(nameof(dataScopeService));
    }

    public async Task<List<ResourceListItemDto>> GetListAsync(int actorUserId, int? departmentId = null, string? resourceType = null, bool includeInactive = false, CancellationToken ct = default)
    {
        // R1（4号位 10-23）：本端点是 S5-01 批唯一漏网（只挂权限码、无 DataScope）⇒ 列表全厂可见，
        // 点进日历又被 403——"列表里有、点进去打不开"。此处与日历读路径 EnsureTargetScopeAsync
        // （ResourceCalendarService.cs:341-352）**同源同判定**：
        //   Global → 全放行（无 scope 谓词）
        //   非 Global → Factory 命中 ∧（资源 DeptCode 为空 或 Department 命中）；不满足者**从列表剔除**
        // 判定维度用码（与读路径 scope.Allows(Factory/Department, code) 一致），不做 Id 换算，防两径分叉。
        var scope = await _dataScopeService.ResolveScopeAsync(actorUserId, ct);

        string scopeSql = "";
        IReadOnlySet<string>? allowedFactories = null;
        IReadOnlySet<string>? allowedDepts = null;

        if (!scope.IsGlobal)
        {
            allowedFactories = scope.GetValues(DataScopeTypes.Factory);
            if (allowedFactories is null or { Count: 0 })
                return new List<ResourceListItemDto>();   // 非 Global 无工厂授权 ⇒ fail-closed 空列表（与读路径一致，非全量）

            allowedDepts = scope.GetValues(DataScopeTypes.Department);
            scopeSql = "\n  AND f.Code IN @AllowedFactories";
            scopeSql += allowedDepts is null or { Count: 0 }
                ? "\n  AND pd.DeptCode IS NULL"           // 无部门授权：仅无部门归属资源可过（读路径 deptCode 空→跳过部门检查；当前 dev 0 条，等效空列表）
                : "\n  AND (pd.DeptCode IS NULL OR pd.DeptCode IN @AllowedDepts)";
        }

        // Dapper 纪律：列表参数只在 IN 内出现一次；空列表分支的 SQL 片段已按集合是否为空条件拼接。
        var sql = @"
SELECT r.Id AS ResourceId, r.ResourceCode, r.ResourceName, r.ResourceType,
       r.ProductionDepartmentId, pd.DeptName AS ProductionDepartmentName,
       f.Code AS FactoryCode, f.Name AS FactoryName, r.IsActive,
       CASE WHEN EXISTS(SELECT 1 FROM dbo.ResourceCalendarSlot rc WHERE rc.ResourceId = r.Id)
            THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END AS HasCalendar
FROM dbo.Resource r
LEFT JOIN dbo.ProductionDepartment pd ON pd.Id = r.ProductionDepartmentId
LEFT JOIN dbo.Factory f ON f.Id = r.FactoryId
WHERE (@Dept IS NULL OR r.ProductionDepartmentId = @Dept)
  AND (@Type IS NULL OR r.ResourceType = @Type)
  AND (@IncludeInactive = 1 OR r.IsActive = 1)"
            + scopeSql +
            "\nORDER BY r.ResourceCode;";

        var rows = await _connectionManager.QueryAsync<ResourceListItemDto>(
            sql,
            new
            {
                Dept = departmentId,
                Type = resourceType,
                IncludeInactive = includeInactive ? 1 : 0,
                AllowedFactories = allowedFactories,
                AllowedDepts = allowedDepts,
            },
            CommandType.Text, DatabaseId.APS, 60);

        return rows.ToList();
    }
}
