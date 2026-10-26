namespace LPS.APS.Core.DTOs.Governance;

/// <summary>
/// 治理侧仓库取值下拉项（GET /api/governance-query/warehouses；4号位 10-22 R2 建端点、10-25 Q1 补列）。
/// 数据源：APS_Production.dbo.ext_MES_ProcessCode_View（权威仓库/工序位置字典）。
/// 语义：**每一行都是一个仓库（工序位置）码**；ERPProperty 只是仓的属性分类，**不是"是否仓库"的过滤条件**。
/// 归并：按 ProcessCode 去重（视图存在重复码，如 51305101 两行同名仓不同写法），取 MIN 保证确定性。
/// 展示用字段：ErpProperty / WarehouseRole / FactoryCode —— 供前端让用户识别码的性质，**不据此过滤**；
/// 值域 M/XC/ZP/BP 在实库另有 KS/BS/AC/ST/NULL，漂移待 0号位 裁决（5号位 10-25 回执 Q1）。
/// </summary>
public sealed class GovernanceWarehouseItem
{
    /// <summary>仓库码（保留前导零）。JSON: code</summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>仓库名称。JSON: name</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>ERP 仓库属性（M/XC/ZP/BP/KS/BS/AC/ST，MES 原生码为 null，不按名称推导）。JSON: erpProperty</summary>
    public string? ErpProperty { get; init; }

    /// <summary>仓库角色（领料位 / ASSY品库 / 现场库 / 受托库 等）。JSON: warehouseRole</summary>
    public string? WarehouseRole { get; init; }

    /// <summary>代码所属工厂（CN/BJ/TJ/CN6/SH）。JSON: factoryCode</summary>
    public string? FactoryCode { get; init; }
}
