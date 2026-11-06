namespace LPS.APS.Engine.Services.Sync.Dto;

/// <summary>
/// ERP订单DTO（对应 ODS.ext_v_APS_SalesOrder 视图输出）
/// </summary>
public class ERPOrderDto
{
    public string SourceOrderId { get; set; } = string.Empty;
    public string OrderNo { get; set; } = string.Empty;
    public string OrderType { get; set; } = string.Empty;
    public string MaterialCode { get; set; } = string.Empty;
    public string BOMNO { get; set; } = string.Empty;
    public string FactoryCode { get; set; } = string.Empty;
    public string? SourceWarehouseProcessCode { get; set; }
    public decimal Quantity { get; set; }
    public string UOM { get; set; } = string.Empty;
    public DateTime DueDate { get; set; }
    public DateTime? OriginalDueDate { get; set; }
    public decimal? ReceivedQty { get; set; }
    public int Priority { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? SourceSystem { get; set; }
    public int? SourceMasterID { get; set; }

    // v5.0.3 源事实字段（来自 ERP 3 表）
    public string? TransportMode { get; set; }
    public string? CustomerName { get; set; }
    public string? MTS_InstructionNo { get; set; }

    // v5.0.24 原始字段（视图输出ERP原值，由 sp_ValidateAndPromoteOrders 派生标准化）
    public string? CustomerCode { get; set; }
    public string? JPOrderNo { get; set; }
    public string? SalesOrderCategory { get; set; }
    public string? DemandMaturityStatus { get; set; }
    /// <summary>2026-11-06 新增：合同号，需求成熟度 SHIKOMI 判据（有值且<>'准备订单' ⇒ FORECAST）。</summary>
    public string? ContractNo { get; set; }

    /// <summary>
    /// 2026-11-06 新增：ERP 原始型号（冻结 07 v5.1.16 §3494 / §3652、08 DDL :4162 要求
    /// `Order_Canonical.SourceModel` 与 `[Order].SourceModel` 逐级透传）。
    /// <para>⚠ 与 `MaterialCode` **同值但不是替代关系**：视图里 `MaterialCode = Cast(Model AS NVARCHAR(100))`，
    /// 两者当前相等；`SourceModel` 只作 ERP 原始型号追溯，不参与主料号判定。</para>
    /// </summary>
    public string? SourceModel { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
