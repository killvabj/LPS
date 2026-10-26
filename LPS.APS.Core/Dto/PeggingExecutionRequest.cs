using LPS.APS.Core.Enum;

namespace LPS.APS.Core.Dto;

/// <summary>
/// Pegging 执行请求 DTO
/// 2号位（编排层）传递给 5号位（业务规则层）的输入参数
/// 对应文档：步骤2.1 的输入
/// </summary>
public class PeggingExecutionRequest
{
    /// <summary>
    /// 计划版本ID
    /// </summary>
    public int PlanVersionId { get; set; }

    /// <summary>
    /// 真实 DomainKey（= 本 PlanVersion 的 DomainKey，由编排层从 PlanVersion.DomainKey 读取传入）。
    /// 禁止用 PlanVersionId / ScheduleRunId / FACTORY_{factoryId} 冒充（PM 终裁 2026-09-02）。
    /// </summary>
    public string DomainKey { get; set; } = string.Empty;

    /// <summary>
    /// 订单ID列表（批量 Pegging）
    /// </summary>
    public List<long> OrderIds { get; set; } = new();

    /// <summary>
    /// 快照时间戳（库存快照的基准时间）
    /// </summary>
    public DateTime SnapshotAt { get; set; }

    /// <summary>
    /// 冻结区窗口起始时间
    /// </summary>
    public DateTime FrozenWindowStart { get; set; }

    /// <summary>
    /// 冻结区窗口结束时间（通常为 FrozenWindowStart + 2小时）
    /// </summary>
    public DateTime FrozenWindowEnd { get; set; }

    /// <summary>
    /// 是否允许跨工厂 Pegging
    /// </summary>
    public bool AllowCrossFactory { get; set; }

    /// <summary>
    /// 跨工厂模式
    /// </summary>
    public CrossFactoryMode? CrossFactoryMode { get; set; }

    /// <summary>
    /// 默认 Pegging 策略
    /// </summary>
    public PeggingStrategyType DefaultStrategy { get; set; } = PeggingStrategyType.FIFO;

    /// <summary>
    /// 产品族ID列表（用于虚拟库存传递）
    /// </summary>
    public List<int> ProductFamilyIds { get; set; } = new();

    /// <summary>
    /// 前序 Domain 成功后的共享 Resource 占用块（FULL §9）。
    /// 由编排层在逐 Domain 串行时累积传入，最终透传至 DomainSolveRequest.UpstreamDomainResourceBlocks。
    /// </summary>
    public IReadOnlyList<ResourceBlock>? UpstreamResourceBlocks { get; set; }

    /// <summary>
    /// Run 级跨 Domain 物理供给消耗账本（R03-02 跨域守恒）。
    /// Key = <c>SupplyLedgerEntry.PhysicalSourceKey</c>；Value = 已被**前序成功 Domain** 合法消耗的累计量。
    /// 编排层逐 Domain 串行时持有、透传；本 Domain 入池时按此账本扣减可用量（不重复计入同一批物理供给）。
    /// 语义约束：FULL 模式下前序 Domain 的占用不得被后域释放；本账本只在内存中存在，**不落库**、
    /// 不写 <c>InventoryBalance.AllocatedQty</c>（R03-02 明文禁止运行期永久回写）。
    /// Candidate 模式为 null（Candidate 的外部占用走 <see cref="ExternalDomainResourceBlocks"/>，不在此扣减）。
    /// </summary>
    public IDictionary<string, decimal>? UpstreamPhysicalConsumption { get; set; }

    /// <summary>
    /// Candidate 专用：**其它 Domain 当前 ACTIVE 计划版本已在库存上占用的总量**（R03-02 跨域守恒的跨 Run 侧）。
    /// Key = <c>(物料, 供给工厂, 供给类型)</c> 聚合键（见 <c>PeggingOrchestrator.BuildAggregateConsumptionKey</c>）；
    /// Value = 已被其它 ACTIVE 域累计占用的量。本 Domain 入池时在同键各批之间**顺着扣、扣完为止**。
    ///
    /// 为什么是聚合键、不是 <see cref="UpstreamPhysicalConsumption"/> 那种物理键：
    ///   物理键（来源系统 + ERP/MES 主档号 + 仓码 + 厂）**只存在于内存**，`PeggingSupplyAllocation`
    ///   没有该列、库存行的 `SupplyWarehouseCode`/`SupplyDocumentNo` 实测全为空、`SupplySourceId`
    ///   跨批会变 ⇒ 跨 Run 无法把「别的域吃了哪一批」回连出来。
    ///   取舍：**总量守恒成立**（候选不会看见已被别的域吃掉的量），批次归属不精确。
    ///
    /// 只装 <c>INVENTORY</c>：库存各批可用时间同为「立即可用」，聚合扣减不改可行性；
    /// 采购/在途供给的可用时间逐批不同，按聚合键扣会把 A 批的占用挪到 B 批的时点上，故不纳入。
    /// </summary>
    public IDictionary<string, decimal>? UpstreamAggregateConsumption { get; set; }

    /// <summary>
    /// 是否强制重新 Pegging（忽略冻结区）
    /// </summary>
    public bool ForceRePegging { get; set; }

    /// <summary>
    /// 最大 BOM 遍历深度（防止循环依赖）
    /// </summary>
    public int MaxBomDepth { get; set; } = 10;

    /// <summary>
    /// 超时时间（秒）
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// 执行模式：FULL_RUN | DRY_RUN | INCREMENTAL | CANDIDATE
    /// </summary>
    public string ExecutionMode { get; set; } = "FULL_RUN";

    /// <summary>
    /// 是否为 Candidate 模式（编排层判定 sourcePlanVersionId != null，即本 Run 由 3号位 冻结了 BasePlanVersionId）。
    /// PM 2026-09-07 P0-04：Candidate 由 3号位激活；2号位据此构造 DomainSolveRequest.CandidateContext（非 null）。
    /// </summary>
    public bool IsCandidate { get; set; }

    /// <summary>
    /// Base 稳定锚点 = ScheduleRun.BasePlanVersionId（Candidate 专用；FULL 为 null）。
    /// 2号位运行期必须始终使用此值，不得中途再查「此刻最新 ACTIVE」替换基座。
    /// </summary>
    public int? BasePlanVersionId { get; set; }

    /// <summary>
    /// 其它 Domain 当前 ACTIVE 在共享 Resource 上的不可移动占用块（Candidate 专用；FULL 为 null）。
    /// 透传至 DomainSolveRequest.CandidateContext.ExternalDomainResourceBlocks。
    /// </summary>
    public IReadOnlyList<ResourceBlock>? ExternalDomainResourceBlocks { get; set; }

    /// <summary>
    /// 排程沙盘上下文（包含Resources、Calendar等1号位所需数据）
    /// V1.2：用于传递给1号位IFiniteCapacityScheduler的完整上下文
    /// </summary>
    public Models.Scheduling.SchedulingContext? SchedulingContext { get; set; }
}
