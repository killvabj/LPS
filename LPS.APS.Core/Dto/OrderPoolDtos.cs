namespace LPS.APS.Core.Dto;

/// <summary>
/// P09 选单池（**C2，11-18**）：一个端点同时回答"哪些订单可进 P09、以哪个场景身份进"——
/// 判据 = **v0.2 §P09 原文「依据当前真实 FULL/ACTIVE 及合法根订单参与关系」**，具体化：
/// · 老订单（场景5/6 候选）= 该订单号存在于**当前 ACTIVE PV** 的 [Order] 装载集（参与关系权威=2号位装载）；
/// · 新订单（场景1/2 候选）= `Order_Canonical` 存在、**不在**该 ACTIVE PV。
/// **禁 CreatedAt/单号前缀猜新老**（v0.2 点名禁令）；域归属估算链 物料→ProductFamily→域
/// （实测 999473 PV 载体 10,247 行 100% 落在域族 ⇒ 估算=装载事实反证；仍标 `freshScopeBasis` 供核）。
/// </summary>
public sealed class OrderPoolResultDto
{
    public bool VersionExists { get; init; } = true;
    public bool ScopeDenied { get; init; }
    public int PlanVersionId { get; init; }
    public string? DomainKey { get; init; }
    /// <summary>域无 ACTIVE（CYL 态）⇒ 新老之分不可判：页面显示"该域当前无正式计划版本"，不是"全新"。</summary>
    public string? NoActiveNote { get; init; }

    /// <summary>新单归属口径注记（估算链声明；权威=2号位装载，Q10 契约化后此位更新）。</summary>
    public string FreshScopeBasis { get; init; } = "canonical→material→productFamily=域族 估算；PV 内载体 100% 同族反证(999473 实测)";

    public List<OrderPoolItemDto> Items { get; init; } = new();
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}

/// <summary>
/// 选单行。`participation`：SCHEDULED=在当前 ACTIVE PV（场景5/6）／NOT_IN_PLAN=不在（场景1/2）。
/// scheduledTime = 该订单号进入 ACTIVE PV 装载集的 UpdatedAt（**装载时刻，非承诺/确认时刻**——
/// "本次新进入"识别挂 D05-08/12 轻量两版 diff 契约，本列不得当首次关注时间用）。
/// 老单无 Task 行时 `hasPlanningTasks=false`（装载但未排=真实中间态，v0.2：未排≠新单）；新单= null 不适用（R2 裁 (a)）。
/// </summary>
public sealed class OrderPoolItemDto
{
    public string OrderNo { get; init; } = string.Empty;
    /// <summary>J1（11-20 4号位件）：`Order_Canonical.Id`——与 `[Order].Id` **非同一 ID 空间**（禁互相顶替）。
    /// 老单行取自 `[Order].OrderCanonicalId` 列（999473 实测 10,247/10,247 稠密）；新单行=canonical 行自身 Id。
    /// 场景 3/4「正式纳入」按此键指认目标订单；canonical 无该单号 ⇒ null=未提供。</summary>
    public long? OrderCanonicalId { get; init; }
    /// <summary>[Order].Id——**仅 ACTIVE 池行有**；纯新单返回 null（WHATIF 入口按 OrderNo+canonical 身份起，域内 Id 归 2号位 装载后才有）。</summary>
    public long? OrderId { get; init; }
    /// <summary>SALES_ORDER / PRODUCTION_INSTRUCTION（词表同 P02）。</summary>
    public string? OrderType { get; init; }
    public string? MaterialCode { get; init; }
    public string? MaterialName { get; init; }
    public decimal? Quantity { get; init; }
    public string? Uom { get; init; }
    /// <summary>要求完成日期（ACTIVE 行=CustomerDueDate；新单行=canonical DueDate——同源语义位）。</summary>
    public DateTime? DueDate { get; init; }
    public string? CustomerName { get; init; }
    /// <summary>ACTIVE 行带；新单行 null。</summary>
    public string? Status { get; init; }

    public string Participation { get; init; } = string.Empty;   // SCHEDULED / NOT_IN_PLAN
    /// <summary>R2（11-18 裁定 (a)）：老单=true有排产Task/false装载未排（真实中间态）；
    /// **新单=null 整键省略=不适用**（此前非空 bool 把 SQL 的 NULL 塌成 false，函与码不一致，认账修）。
    public bool? HasPlanningTasks { get; init; }
    public DateTime? ScheduledTime { get; init; }
}
