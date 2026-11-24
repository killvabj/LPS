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
    /// <summary>FG/WIP 维度=**已具备**（0号位 11-20 裁后按标准 MaterialType 出四组 GROUPING SETS：总计/SO-PI/类型/交叉）。</summary>
    public bool FgWipDimensionAvailable { get; init; }
    /// <summary>可配置目标准交率：待 3号位 合同核对结论（Q-c 禁我方自选配置源）——现为 null 占位。</summary>
    public decimal? TargetPercent { get; init; }

    // ── A7（11-21 4号位件）：交期三均值＝**候选公式**（0号位 明文"未证实公式不得先填假值"，
    // 本组值仅供已核实后展示；页面在 0号位 确认公式前不显数字）。
    /// <summary>平均客户期望交期天数 = CustomerDueDate − IssueDate（自然日）；日期缺则不计入分母。</summary>
    public decimal? AvgCustomerExpectDays { get; init; }
    /// <summary>平均APS排产交期天数 = 同PV Task MAX(PlannedEndTime) − IssueDate（自然日）；无任务不计。</summary>
    public decimal? AvgApsLeadDays { get; init; }
    /// <summary>平均延期天数 = 仅**延期订单**（MaxEnd 日期&gt;交期）计平均（自然日）。</summary>
    public decimal? AvgDelayDays { get; init; }

    public List<DeliveryKpiRowDto> Rows { get; init; } = new();
}

/// <summary>
/// 分区行。<c>OrderType</c>=null ⇒ 该 PV 汇总行（GROUPING SETS 顶层）；否则 SALES_ORDER / PRODUCTION_INSTRUCTION。
/// 计数三态互斥全覆盖：OnTime＋Late＋Undetermined＝TotalOrders（预跑逐位自证）。
/// </summary>
public sealed class DeliveryKpiRowDto
{
    public string? OrderType { get; init; }
    /// <summary>
    /// FG/WIP 分区维度=**标准 Material 分类**（`Material.MaterialType` 原值：FINISHED_GOOD/SEMI_FINISHED/ASSY/RAW_MATERIAL/MFG），
    /// 0号位 2026-11-20 裁：**不得按品番前缀/ERP工序属性/Routing 自行推断**；全部标准类型逐行列出、不合并、不隐藏
    /// （FG=FINISHED_GOOD、WIP=SEMI_FINISHED 为两个业务桶，其余类型以其标准类型名自成一行=如实）。
    /// null=该行不是类型维度行（总计行/仅 SO-PI 行）。实库核（11-20）：ACTIVE 六域全部根订单 0 例无法归类（MaterialId 孤儿 0）。
    /// </summary>
    public string? MaterialType { get; init; }

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
