namespace LPS.APS.Core.Dto;

/// <summary>
/// 上游需求实体（用于排序的业务对象）
///
/// 包含排序所需的业务字段（OrderType, DelayStatus, DueDate等）
/// 排序后由 IDemandPriorityExecutor 赋值 DemandSequence，用于创建 LogicalProductionDemand。
///
/// 职责边界：
/// - 2号位将订单/需求转换为本 DTO，交由 IDemandPriorityExecutor 消费 3号位策略排序
/// - DemandSequence 是"计算层 → Priority Segment → 段内排序"的最终业务顺序（1,2,3...）
/// </summary>
public sealed class UpstreamDemand
{
    public string DemandKey { get; init; } = default!;
    public string? OrderType { get; init; }
    public string? DelayStatus { get; init; }
    public string? CustomerTier { get; init; }
    public DateTime? DueDate { get; init; }
    public DateTime? IssueDate { get; init; }
    public string? ProtectionStatus { get; init; }

    // ── 2026-11-06 V1.1 新增（3号位 需求优先级新增字段四段贯通实施包 v1.0 §四.1）──────────
    //   9 个新增字段现已全部装入并接线执行器白名单（防空转满足，2026-11-06/07 已放行）。
    //   `StockConsumeDay`（库存消耗日）＝ `IAD.AverageDailyConsumption` 派生的 CoverageDays **小数天**（非日期、非页面维护）；
    //   `NonStockShipCategory`（非在库出荷区分）＝ `[Order].ZPQF`（1/2/0）装载（非页面维护）。
    //   ⚠ 2026-11-07 更正早期暂态表述：二者均**非**「页面维护、无载体待 4号位 前端入口补录」，勿据此造页面录入。

    /// <summary>客户区分（JAPAN/DOMESTIC/OVERSEAS/VIETNAM/INTER_FACTORY/OTHER）。源：`[Order].CustomerSegment`。</summary>
    public string? CustomerSegment { get; init; }

    /// <summary>
    /// 出荷指示入物流日（3号位 定义为「独立新增字段，与 <see cref="DueDate"/> 并列」）。
    /// ⚠ 当前装载源与 <see cref="DueDate"/> 同值（`[Order].CustomerDueDate`，订单表无第二个「入物流日」列）；
    ///   待 PM 的 `TODAY+20`/OffsetDays 口径落地后，二者才会真正分叉。
    /// </summary>
    public DateTime? CustomerDueDate { get; init; }

    /// <summary>运输方式（LAND/SEA/AIR/COURIER）。源：`[Order].TransportMode`。</summary>
    public string? TransportMode { get; init; }

    /// <summary>销售类别（DIRECT_SALES/SALES_REPLENISHMENT）。源：`[Order].SalesOrderCategory`。</summary>
    public string? SalesOrderCategory { get; init; }

    /// <summary>需求成熟度（PRE_CONFIRMED/FORECAST）。源：`[Order].DemandMaturityStatus`（2026-11-06 起由备注/合同号推导）。</summary>
    public string? DemandMaturityStatus { get; init; }

    /// <summary>
    /// 非在库出荷区分（`FULL_PURPLE_SLIP` 全额紫票 / `DIFF_PURPLE_SLIP` 差额紫票 / NULL 在库出荷）。
    /// 源：`[Order].NonStockShipmentType` ← `ZPQF`（1=全额 / 2=差额 / 0=在库出荷不产生产需求，装载即被拒）。
    /// 2026-11-06 用户确认：该字段不再是"页面维护、无载体"，规则就在 ZPQF 上。
    /// </summary>
    public string? NonStockShipmentType { get; init; }

    /// <summary>物料编码（供 `Contains` 模糊匹配，值如 `FINAL*`）。源：`[Order].MaterialCode`。</summary>
    public string? MaterialCode { get; init; }

    /// <summary>
    /// 订单数量（**原始订单量**，非 PI 扣减后的 `DemandQty`）。源：`[Order].Quantity`。
    /// 3号位 定义该字段参与【段内排序】，故取订单量而非剩余量。
    /// </summary>
    public decimal? Quantity { get; init; }

    /// <summary>
    /// 库存消耗日（CoverageDays，单位「天」，小数）。**数值越小越紧急**。
    /// <para>口径（0号位 2026-11-06 补充裁决 ＋ 用户定盘）：</para>
    /// <para>· 仓库范围 **ZP / AC / M**（AC 必须纳入）；</para>
    /// <para>· `CoverageDays = 三类仓内该物料的合资格实物库存 ÷ 该物料的日均消耗`；</para>
    /// <para>· 库存与日均**均按 `(厂,仓,料)` 去重后再求和**——同一三键在
    ///   `InventoryAvailableSupplyDetail` 里会因多条可用性规则各生成一行（实测 4 行、Quantity 完全相同），
    ///   按明细行直接求和会成倍放大；</para>
    /// <para>· `null` = **无可信日均**（ODS 无该料行，或明确日均 = 0）⇒ 排序时必须置末，
    ///   **不得按 0 处理**（否则把「无法判定」排成最紧急）；</para>
    /// <para>· `0` = **日均 &gt; 0 但库存为 0**（`ZERO_STOCK`）⇒ 真·最紧急。</para>
    /// </summary>
    public decimal? StockConsumeDay { get; init; }

    /// <summary>
    /// 订单规范 Id（EXPEDITE 前置竞争层身份键 = ScopeJsonV2.OrderTargets.OrderCanonicalId，S5/PM 0923）。
    /// 普通 FULL 排程可空；EXPEDITE 触发时用于识别「提前」需求。
    /// </summary>
    public long? OrderCanonicalId { get; init; }

    /// <summary>
    /// 排序后的业务顺序（1 起），由 IDemandPriorityExecutor.ExecutePrioritySort 赋值。
    /// </summary>
    public int DemandSequence { get; set; }

    /// <summary>
    /// 原始需求对象引用（供2号位排序后回溯到订单行，如 OrderPeggingRow）。
    /// </summary>
    public object? SourceDemand { get; init; }
}
