namespace LPS.APS.Core.Dto;

/// <summary>
/// 生产指示位置计算输入（2号位提供给5号位的完整事实包）
///
/// 按照2↔5接口冻结文档，2号位负责装载本次ScheduleRun/DataCutoffTime的所有相关事实，
/// 5号位只接收这个事实包进行纯计算，不直接访问数据库。
/// </summary>
public sealed class ProductionInstructionPositionInput
{
    /// <summary>
    /// 生产指示号
    /// </summary>
    public string ProductionInstructionNo { get; init; } = string.Empty;

    /// <summary>
    /// 物料ID
    /// </summary>
    public int MaterialId { get; init; }

    /// <summary>
    /// 物料编码
    /// </summary>
    public string MaterialCode { get; init; } = string.Empty;

    /// <summary>
    /// 工厂ID
    /// </summary>
    public int FactoryId { get; init; }

    /// <summary>
    /// 工厂编码
    /// </summary>
    public string FactoryCode { get; init; } = string.Empty;

    /// <summary>
    /// ERP剩余数量（该PI尚未最终进入目标M库的全部剩余数量）
    /// 这是总量红线，所有Position必须闭合到这个数量
    /// </summary>
    public decimal ErpRemainingQty { get; init; }

    /// <summary>
    /// Stage进度事实列表（2号位从快照中提取）
    /// </summary>
    public IReadOnlyList<StageProgressFact> StageProgress { get; init; } = Array.Empty<StageProgressFact>();

    /// <summary>
    /// 工序进度事实列表
    ///
    /// ⚠ 入参口径（2026-10-26 裁定，见 5号位致2号位《工序进度入参两项均判须全量》）：
    /// 本字段语义 = **该 PI 全部工序进度的累计完成证据账**，按「该 PI 名下全部行」装载，
    /// **不按工单状态预筛**（CLOSED/RELEASED/无对应工单的行同样必须保留）。
    /// 消费点依赖完成历史：①`SplitByOperationProgress`→DAG 前沿的 completed 集合（收窄⇒起点回退⇒已完工序重排）；
    /// ②首 Stage 兜底门禁 `All(GoodQty<=0)`（收窄⇒开工证据丢失⇒误判从未开工）。
    /// 状态过滤的正确落点是 <see cref="WorkOrders"/>（仅 IN_PROGRESS，"在执行窗口"语义），两者各司其职。
    /// 提速只做列裁剪/分块/索引，不做行集收窄；「无对应工单」行亦须保留。
    /// </summary>
    public IReadOnlyList<OperationProgressFact> OperationProgress { get; init; } = Array.Empty<OperationProgressFact>();

    /// <summary>
    /// PI级库存事实
    /// </summary>
    public IReadOnlyList<PiInventoryFact> PiInventories { get; init; } = Array.Empty<PiInventoryFact>();

    /// <summary>
    /// XC（线边仓）事实
    /// </summary>
    public IReadOnlyList<XcFact> XcFacts { get; init; } = Array.Empty<XcFact>();

    /// <summary>
    /// 厂间在途事实
    /// </summary>
    public IReadOnlyList<InterplantTransitFact> TransitFacts { get; init; } = Array.Empty<InterplantTransitFact>();

    /// <summary>
    /// Stage路径事实（定义该PI的加工路径）
    /// </summary>
    public IReadOnlyList<StagePathFact> StagePath { get; init; } = Array.Empty<StagePathFact>();

    /// <summary>
    /// 跨厂边事实（定义PI路径中的跨厂转移边）
    /// </summary>
    public IReadOnlyList<CrossFactoryEdgeFact> CrossFactoryEdges { get; init; } = Array.Empty<CrossFactoryEdgeFact>();

    /// <summary>
    /// 强事实（Received等有明确单据支撑的事实）
    /// </summary>
    public IReadOnlyList<ReceivedFact> StrongFacts { get; init; } = Array.Empty<ReceivedFact>();

    /// <summary>
    /// 工单快照事实列表（2号位从MESWorkOrderSnapshot提取，仅IN_PROGRESS工单）
    ///
    /// 用途：5号位结合工单身份（MESWorkOrderNo/WorkOrderStatus）+ 工序进度 + PI Position
    /// 输出标准化既存执行上下文（ExistingExecutionContext），供2号位 Continuation 分桶。
    ///
    /// ⚠️ 只传IN_PROGRESS工单；RELEASED/CLOSED/DELETED不传。
    /// </summary>
    public IReadOnlyList<WorkOrderSnapshotFact> WorkOrders { get; init; } = Array.Empty<WorkOrderSnapshotFact>();

    /// <summary>
    /// Routing 工序节点事实（按物料+部门过滤后的该 PI 子集；5号位 DAG 拓扑前沿算法输入）
    /// </summary>
    public IReadOnlyList<RoutingOperationFact> RoutingOperations { get; init; } = Array.Empty<RoutingOperationFact>();

    /// <summary>
    /// Routing 依赖边事实（同上物料+部门范围；激活条件：Count > 0 即走 DAG 拓扑前沿）
    /// </summary>
    public IReadOnlyList<RoutingDependencyFact> RoutingDependencies { get; init; } = Array.Empty<RoutingDependencyFact>();

    /// <summary>
    /// 本次计算使用的冻结参数快照ID（可选）
    /// </summary>
    public long? FrozenParameterSnapshotId { get; init; }

    /// <summary>
    /// PI 级 XC 源不可用标记（2026-10-25，2号位《PI级XC缺源时Position侧表达位询问》）
    ///
    /// 2号位 装载侧闸门（表缺失／无刷新记录／非 SUCCESS-WARN／超 24h）不过时，**不再拒绝整轮 Pegging**，
    /// 改为：送空 <see cref="XcFacts"/> ＋ 置本标记为 true。
    ///
    /// 5号位 计算器在 true 时的处理（缺源不得按 XC=0 出数、也不得用普通库存填补）：
    /// ① 每 PI 产 `PI_XC_SOURCE_UNAVAILABLE`（WARN）Issue 留痕；
    /// ② **抑制 ERP 兜底**（`erpFallbackTotal = 0`），防止未知 XC 份额经「剩余量 − 已定位量」流入
    ///    A/B 连续性片造成过排（否则已在线边的份额会被当成该工单还要做）；
    /// ③ UNLOCATED 仍按闭合规则产出（Σ Position = ERP Remaining 不破）；
    /// ④ 不再为 UNLOCATED 产 NextOperationContext（避免误导性的「最早可信 Stage」起点）。
    /// 默认 false ⇒ 未设置时行为与既有完全一致。
    /// </summary>
    public bool PiXcSourceUnavailable { get; init; }
}
