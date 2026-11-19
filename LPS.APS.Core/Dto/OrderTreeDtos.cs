namespace LPS.APS.Core.Dto;

/// <summary>
/// P02 行展开 / P03 深链共用「订单完整树」（**B3/B4，11-17**；v0.2 §P02 展开工作区三段式）。
/// 一次请求给全：交付身份头 + BOM/Demand 结构（Pegging 图边闭包） + 供需数量账（PSA 行，含 Stage 链列）。
///
/// **数据真相分界（不做第二本账）**：
/// · 结构/边=2号位 Pegging 图（`Pegging` 表按 PV 隔离，Task 对+物料对+承接量/类型/提前期/跨域位）——
///   自根订单 Task 集（Task.OrderId 直挂 ∪ AllocationTaskShare.RootOrderId）沿**上游方向**闭包展开=组件树；
/// · 数量账=PSA（DemandQty/AllocatedQty/Gap + 供给段原值）；
/// · **内制 Q/S/U 账不在本响应重复**——走既有 `GET /api/explanation/demand-reconciliation`（同一事实唯一出口，
///   v0.2 §2.3.3 两本账禁互加）；
/// · StageTimingNode（无 Routing 但有 Stage LT 的时间节点）当前**不可证可查**（§八 Q03 待 2号位）⇒
///   <see cref="StageTimingNote"/> 恒为"未提供"，页面在完整链对应位如实标注，**禁前端自造节点**。
///
/// 截断三态：Nodes/Edges/Demands 各有上限+truncated 位（"已加载数/总数"红线，v0.2 §P02.2）。
/// 越权/不存在分态：found=false（订单不在该 PV）vs scopeDenied=true（域外/工厂外）。
/// </summary>
public sealed class OrderTreeResultDto
{
    public bool VersionExists { get; init; } = true;
    public bool ScopeDenied { get; init; }
    public bool Found { get; init; }

    public int PlanVersionId { get; init; }
    public string? DomainKey { get; init; }
    public string? PlanVersionStatus { get; init; }
    public DateTime? DataCutoffTime { get; init; }

    // ── 根订单身份（交付结论列沿用列表行口径，此处只给头） ──
    public long OrderId { get; init; }
    public string? OrderNo { get; init; }
    public string? OrderType { get; init; }
    public string? MaterialCode { get; init; }
    public DateTime? CustomerDueDate { get; init; }
    public string? Status { get; init; }

    public List<OrderTreeNodeDto> Nodes { get; init; } = new();
    public List<OrderTreeEdgeDto> Edges { get; init; } = new();
    public List<OrderTreeDemandDto> Demands { get; init; } = new();

    public bool NodesTruncated { get; init; }
    public bool EdgesTruncated { get; init; }
    public bool DemandsTruncated { get; init; }

    /// <summary>无 Routing 的 Stage 时间节点的当前可证性说明（固定"未提供"直到 Q03 核证）。</summary>
    public string? StageTimingNote { get; init; }

    /// <summary>根订单无任何 Task/份额行时的如实位（树空=未排/无承接，≠接口失败）。</summary>
    public bool RootHasOwnTasks { get; init; }
}

/// <summary>树节点=一个 Operation Task（P06 详情可直接以 taskId 跳）。</summary>
public sealed class OrderTreeNodeDto
{
    public long TaskId { get; init; }
    public string? TaskNo { get; init; }
    public int MaterialId { get; init; }
    public string? MaterialCode { get; init; }
    public string? MaterialName { get; init; }
    public string? StageCode { get; init; }
    public int OperationSeq { get; init; }
    public string? OperationName { get; init; }

    // R1（11-18 4号位件·我方 A2 函overstated 认账）：三组投影补齐，命名与 A4 任务清单行逐字一致
    // ——「订单→Stage→部门→Task」三级分组的数据前提；同 Task 表同 JOIN，零 DDL。
    /// <summary>部门（v0.2 原文列；Task.ProductionDepartmentId 直投，缺⇒null）。</summary>
    public int? ProductionDepartmentId { get; init; }
    public string? ProductionDepartmentName { get; init; }
    /// <summary>设备（与人工槽互斥补全，同 A4 语义）。</summary>
    public int? ResourceId { get; init; }
    public string? ResourceCode { get; init; }
    public string? ResourceName { get; init; }
    public int? ManualSlotId { get; init; }
    public string? ManualSlotCode { get; init; }
    public decimal Quantity { get; init; }
    public string Uom { get; init; } = string.Empty;
    public DateTime? PlannedStart { get; init; }
    public DateTime? PlannedEnd { get; init; }
    public string Status { get; init; } = string.Empty;
    public bool IsLocked { get; init; }
    public string? MesWorkOrderNo { get; init; }
    /// <summary>Task.OrderId 直挂本根订单（出口组装合法=出荷指示身份；true≠唯一需求来源——共享 Task 见 edges）。</summary>
    public bool OwnRootOrder { get; init; }
    /// <summary>闭包种子层成员（根订单自身 Task 集）。</summary>
    public bool InSeed { get; init; }
    /// <summary>距种子层最小上游深度（0=种子）。</summary>
    public int MinDepth { get; init; }

    /// <summary>
    /// A2（11-18）本订单在该 Task 的份额量合计（AllocationTaskShare，RootOrderId=本订单）——
    /// **组件链中间 Task 常无份额行 ⇒ null=无直接归属记录（不是 0，更不是不占用）**；
    /// 共享 Task 的"不重复占资源"解释= ownShareQty ≤ quantity，页面按份额占比标注本订单条。
    /// </summary>
    public decimal? OwnShareQty { get; init; }
    /// <summary>该 Task 被几个根订单的份额引用（&gt;1=跨订单共享 Task——999473 实测 33/9,276 例；0=无份额行）。</summary>
    public int ShareRootCount { get; init; }
}

/// <summary>树边=一条 Pegging 承接（父=上游物料 Task，子=下游消费 Task——前端按父子组树）。</summary>
public sealed class OrderTreeEdgeDto
{
    public long ParentTaskId { get; init; }
    public long ChildTaskId { get; init; }
    public int Depth { get; init; }
    public decimal Quantity { get; init; }
    public string? Uom { get; init; }
    public string? PeggingType { get; init; }
    public int? LeadTimeDays { get; init; }
    public bool IsCrossDomain { get; init; }
    public string? AllocationReason { get; init; }
}

/// <summary>
/// Demand 行=PSA 一条分配切片（选中节点的"供需数量账"数据源）：
/// NeedBy 列当前**不存在**（PSA 无需用日期列——如实缺位，前端显示"未提供"，禁拿 ETA 顶替）。
/// GapQty = DemandQty − AllocatedQty（后端算式单点；禁 min/max 掩盖超排——D05 FULL-01 读侧纪律）。
/// </summary>
public sealed class OrderTreeDemandDto
{
    public long AllocationSequence { get; init; }
    public int MaterialId { get; init; }
    public string? MaterialCode { get; init; }
    public string? DemandStageCode { get; init; }
    public decimal DemandQty { get; init; }
    public decimal AllocatedQty { get; init; }
    public decimal GapQty { get; init; }

    public string? SupplyType { get; init; }
    public string? SupplyDocumentNo { get; init; }
    public string? SupplyFactoryCode { get; init; }
    public string? SupplyMode { get; init; }
    public string? CommitmentStatus { get; init; }
    public DateTime? Eta { get; init; }
    public DateTime? KnownAvailableTime { get; init; }

    // Stage 链三段（有则给，PIPELINE 类全空=事实缺，非缺陷——D05-04 实测）
    public string? AttachStageCode { get; init; }
    public string? CompletedStageCode { get; init; }
    public string? NextRequiredStageCode { get; init; }
    /// <summary>剩余路径 JSON 原文透传（结构归 2号位 写侧；前端如需展开按原文解析，后端不二次定义）。</summary>
    public string? RemainingStagePathJson { get; init; }
}
