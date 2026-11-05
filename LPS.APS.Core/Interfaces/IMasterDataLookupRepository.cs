using LPS.APS.Core.DTOs.Setup;
using LPS.APS.Core.Entities.APS;

namespace LPS.APS.Core.Interfaces;

/// <summary>
/// 主数据只读查询仓储契约（Setup 维护 API Code 回带 + #9/#11 查询用）。
/// 说明：Engine 仓储层此前无独立主数据读仓储（Material 仅 Sync 服务内临时查）；本接口由 3号位 依用户授权例外自写只读实现。
/// 权威映射（APS_Production，冻结 DDL v5.1.2）：ProductionDepartment.Id ↔ DeptCode；Resource.Id ↔ ResourceCode；Material.Id（masterId）↔ MaterialCode。
/// </summary>
public interface IMasterDataLookupRepository
{
    /// <summary>生产部门主键 → 部门编码（DeptCode）</summary>
    Task<string?> GetDepartmentCodeAsync(int departmentId, CancellationToken cancellationToken = default);

    /// <summary>资源主键 → 资源编码（ResourceCode）</summary>
    Task<string?> GetResourceCodeAsync(int resourceId, CancellationToken cancellationToken = default);

    /// <summary>物料主键（Material.Id）→ 物料编码（MaterialCode）</summary>
    Task<string?> GetMaterialCodeAsync(int materialId, CancellationToken cancellationToken = default);

    /// <summary>产品族主键 → 产品族编码（ProductFamily.Code）</summary>
    Task<string?> GetProductFamilyCodeAsync(int productFamilyId, CancellationToken cancellationToken = default);

    /// <summary>工厂主键 → 工厂编码（Factory.Code）</summary>
    Task<string?> GetFactoryCodeAsync(int factoryId, CancellationToken cancellationToken = default);

    /// <summary>工序资源资格：某 Operation 对某物料合法（IsActive）的全部资源主键（#9 交集计算用）</summary>
    Task<IReadOnlyList<int>> GetEligibleResourceIdsAsync(string operationCode, int materialId, CancellationToken cancellationToken = default);

    /// <summary>资源主键批查 → 资源主数据（#9 Code 回带 + 部门归属）</summary>
    Task<IReadOnlyList<ResourceLookupInfo>> GetResourceInfosAsync(IReadOnlyCollection<int> resourceIds, CancellationToken cancellationToken = default);

    /// <summary>部门主键批查 → 部门主数据（#9 DeptCode/StageCode 回带）</summary>
    Task<IReadOnlyList<DepartmentLookupInfo>> GetDepartmentInfosAsync(IReadOnlyCollection<int> departmentIds, CancellationToken cancellationToken = default);

    /// <summary>全量规则集版本（#11 无规则集过滤场景；避免修改治理契约接口 IRuleSetVersionRepository）</summary>
    Task<IReadOnlyList<RuleSetVersion>> GetAllRuleSetVersionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 规则集主表批查（#11 版本列表元素回带 RuleSetCode/RuleSetName，P2 2026-10-28 补：
    /// 前端 Setup 三页需在 aps.setup.view（planner 持）权限内于版本下拉直接分辨所属规则集，
    /// 避免回退调 GET /rule-sets（挂 aps.rule.view，planner 无 → 403）做侧映射）。
    /// 仅返回 Id/RuleSetCode/RuleSetName（版本列表展示所需，不取 Description/IsActive 等）。
    /// </summary>
    Task<IReadOnlyList<RuleSet>> GetRuleSetsAsync(IReadOnlyCollection<long> ruleSetIds, CancellationToken cancellationToken = default);

    /// <summary>Code→Id 读：部门下拉（search 模糊 DeptCode；masterId=Id，code=DeptCode；红线 #4 返回列表）</summary>
    Task<IReadOnlyList<DepartmentLookupItem>> LookupDepartmentsAsync(string? search, int limit = 200, CancellationToken cancellationToken = default);

    /// <summary>Code→Id 读：设备下拉（search 模糊 ResourceCode/ResourceName；masterId=Id，code=ResourceCode；红线 #4 返回列表）</summary>
    Task<IReadOnlyList<ResourceLookupItem>> LookupResourcesAsync(string? search, int limit = 200, CancellationToken cancellationToken = default);

    /// <summary>Code→Id 读：物料下拉（search 模糊 MaterialCode/MaterialName/Spec；activeOnly=true 仅活动；masterId=Material.Id，code=MaterialCode；红线 #4 返回列表）</summary>
    Task<IReadOnlyList<MaterialLookupItem>> LookupMaterialsAsync(string? search, bool activeOnly = false, int limit = 200, CancellationToken cancellationToken = default);
}
