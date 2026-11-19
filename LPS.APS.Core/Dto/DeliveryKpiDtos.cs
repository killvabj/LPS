namespace LPS.APS.Core.Dto;

/// <summary>
/// P02 交付 KPI 分区聚合（**B2，11-17；0号位 Q-c 裁后读模型**）：一行=一个统计分区。
/// 判定**唯一复用订单页 `RiskCaseExpr`**（计划派生、日期级、当天完成=按期——冻结 01 §64/07 §八.2 同族，
/// 非 summary 的 ERP `DelayStatus` 桶、非1号位内存 OnTimeRate；`OrderScheduleSummary` 空表不当硬依赖）。
/// 分母恒=分区内全部授权去重根订单（与列表同 WHERE；**未判定不剔出分母**，Q-c/§P02"不得偷偷换分母"）。
/// 计划按期率＋计划延期率＋未判定占比三者相加=100%（率由 5号位算子给出，前端不再自算）。
/// </summary>
public sealed class DeliveryKpiResultDto
{
    public int PlanVersionId { get; init; }
    public string? DomainKey { get; init; }
    public bool VersionExists { get; init; } = true;
    public bool ScopeDenied { get; init; }

    /// <summary>判定口径回显（页面须展示：让人知道这是"计划预计按期率"非"实际准交率"）。</summary>
    public string Caliber { get; init; } = string.Empty;
    /// <summary>计划完成时刻来源=同 PV Task MAX(PlannedEndTime)（读模型计算）；2号位正式列落库后此位切 formal。</summary>
    public string PlannedCompletionSource { get; init; } = "readmodel_task_max";
    /// <summary>FG/WIP 维度拆分当前不具备权威映射（ProductFamily 无该属性、订单物料含 SEMI_FINISHED、前缀禁作判据）——true 才出该层分区。</summary>
    public bool FgWipDimensionAvailable { get; init; }
    /// <summary>可配置目标准交率：待 3号位 合同核对结论（Q-c 禁我方自选配置源）——现为 null 占位。</summary>
    public decimal? TargetPercent { get; init; }

    public List<DeliveryKpiRowDto> Rows { get; init; } = new();
}

/// <summary>
/// 分区行。<c>OrderType</c>=null ⇒ 该 PV 汇总行（GROUPING SETS 顶层）；否则 SALES_ORDER / PRODUCTION_INSTRUCTION。
/// 计数三态互斥全覆盖：OnTime＋Late＋Undetermined＝TotalOrders（预跑逐位自证）。
/// </summary>
public sealed class DeliveryKpiRowDto
{
    public string? OrderType { get; init; }

    /// <summary>分母=去重根订单总数（含未判定）。</summary>
    public int TotalOrders { get; init; }
    /// <summary>计划按期：MAX(Task.PlannedEnd) 日期级 ≤ CustomerDueDate。</summary>
    public int OnTimeOrders { get; init; }
    /// <summary>计划延期：日期级晚于交期。</summary>
    public int LateOrders { get; init; }
    /// <summary>未判定：无 Task 行／完成时刻缺／交期缺——**不等于 0 也不剔除**（§P02"没 Task 不可直接判未知→单列"）。</summary>
    public int UndeterminedOrders { get; init; }

    /// <summary>计划按期率%（onTime/total）；total=0 ⇒ null。</summary>
    public decimal? OnTimeRatePct { get; init; }
    /// <summary>计划延期率%（late/total）；total=0 ⇒ null。</summary>
    public decimal? LateRatePct { get; init; }
}
