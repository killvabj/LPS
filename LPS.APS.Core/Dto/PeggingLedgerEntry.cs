using LPS.APS.Core.Enum;

namespace LPS.APS.Core.Dto;

/// <summary>
/// Pegging 内存账本条目（纯内存中间态，不落库）
/// 5号位 BOM 遍历过程中记录供需扣减过程，算完后打包进 PeggingResultVoucher
/// </summary>
public class PeggingLedgerEntry
{
    /// <summary>
    /// 需求侧订单ID
    /// </summary>
    public long OrderId { get; set; }

    /// <summary>
    /// 需求侧物料ID
    /// </summary>
    public int DemandMaterialId { get; set; }

    /// <summary>
    /// 需求数量
    /// </summary>
    public decimal DemandQuantity { get; set; }

    /// <summary>
    /// 供应侧物料ID
    /// </summary>
    public int SupplyMaterialId { get; set; }

    /// <summary>
    /// 已分配数量
    /// </summary>
    public decimal AllocatedQuantity { get; set; }

    /// <summary>
    /// 供应来源类型
    /// </summary>
    public SupplySourceType SourceType { get; set; }

    /// <summary>
    /// 供应来源ID
    /// </summary>
    public long? SourceId { get; set; }

    /// <summary>
    /// BOM 层级（0=成品，1=半成品...）
    /// </summary>
    public int BomLevel { get; set; }

    /// <summary>
    /// 工厂代码
    /// </summary>
    public string FactoryCode { get; set; } = string.Empty;

    /// <summary>
    /// 产品族ID
    /// </summary>
    public int ProductFamilyId { get; set; }

    /// <summary>
    /// 是否在冻结区
    /// </summary>
    public bool IsInFrozenZone { get; set; }

    /// <summary>
    /// Pegging 策略
    /// </summary>
    public PeggingStrategyType Strategy { get; set; }

    /// <summary>
    /// 供应可用时间。
    ///
    /// <para><b>2026-11-13 改为可空</b>（0号位 实施回复 §4、1号位 五问答复）：本字段位于**审计账本**
    /// （`voucher.LedgerEntries`），**不是 Solver 输入**（Solver 读 `voucher.SupplyAllocations`）。
    /// 原写法 `supply.AvailableAt ?? DateTime.Now` 把「时间未知」硬写成「现在」，
    /// 是 0号位 明令要删的错误。改为原样透传：未知即 <c>null</c>，
    /// **不得**用当前时刻、`CreatedAt` 或订单交期顶替。</para>
    /// </summary>
    public DateTime? AvailableAt { get; set; }
}
