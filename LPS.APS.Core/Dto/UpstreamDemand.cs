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
    /// <summary>
    /// 交期（排序/命中用）。**当前取值与 <see cref="CustomerDueDate"/> 同源同值**，二者皆取自
    /// `[Order].CustomerDueDate`（其上游是 ODS `v_APS_SalesOrder.DueDate` ← `V_AllSalesInstr.ReceiveDate`，
    /// 即**出荷指示入物流日**）。本链路上**没有独立的「客户要求交期」列** —— 命名易误读，详见
    /// <see cref="CustomerDueDate"/> 的说明。
    /// </summary>
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
    /// <para>**源链（2026-11-06 用户两次确认定盘，不要再当缺口重开）**：</para>
    /// <para>`V_AllSalesInstr.ReceiveDate`（ERP 出荷指示的入物流日，随返信/延迟变化）
    ///   → ODS `v_APS_SalesOrder.DueDate` → `[Order].CustomerDueDate` → 本字段。</para>
    /// <para>⚠ 两个容易误读的点，写死在此：</para>
    /// <para>① **ODS 视图里那列虽然叫 `DueDate`，装的正是入物流日**。本链路上**没有独立的「客户要求交期」列**，
    ///   所以本字段取到的就是真值，**不是「借 <see cref="DueDate"/> 近似」**。</para>
    /// <para>② 本字段与 <see cref="DueDate"/> 目前同值，是因为二者都取自 `[Order].CustomerDueDate`；
    ///   真正「分叉」的另一条路是 `[Order].OriginalDueDate`
    ///   （= `ODBC_V_U13_Requisition.ReceiveDate`，**原始**入物流日、永不变化，与现值实测差 5.87%），
    ///   而该列已被 3号位 实施包 §一.1 明确剔除、不进排序。</para>
    /// <para>⇒ 早期那句「待 PM 的 `TODAY+20`/OffsetDays 口径落地后二者才会真正分叉」**是暂态表述，已作废**：
    ///   `TODAY+20` 是**阈值换算**（冻结日 + 偏移天数，用于条件比较），与字段取值来源无关，不会让两列分叉。</para>
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
