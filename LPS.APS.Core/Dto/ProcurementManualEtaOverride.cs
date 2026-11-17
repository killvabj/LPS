namespace LPS.APS.Core.Dto;

/// <summary>
/// 采购人工ETA覆盖事实（从ProcurementManualEtaOverride表装载）
///
/// 【2026-08-26新基线职责边界】
/// - 5号位职责：提供ProcurementManualEtaOverride表和Repository接口，供4号位维护
/// - 2号位职责：读取Manual ETA并计算Effective ETA (ManualEta ?? ErpEta ?? ReleaseDate+DefaultLT)
/// - 2号位职责：计算AvailableTime (Effective ETA + ArrivalToUsableOffset)
/// - 取消时IsActive=0，2号位回退到ERP ETA或ReleaseDate+DefaultLT
///
/// 参考：APS_V1_5号位新基线增量整改开发包_v1.0_20260825.md
/// 参考：复审报告P1-02
/// </summary>
public sealed class ProcurementManualEtaOverride
{
    /// <summary>
    /// 采购订单号
    /// </summary>
    public string PONo { get; init; } = string.Empty;

    /// <summary>
    /// 行号
    /// </summary>
    public int LineNo { get; init; }

    /// <summary>
    /// 物料ID
    /// </summary>
    public int MaterialId { get; init; }

    /// <summary>
    /// 物料编码
    /// </summary>
    public string MaterialCode { get; init; } = string.Empty;

    /// <summary>
    /// 物料中文名（**R5，11-12 4号位件**：列表/单条/Upsert 返回经 LEFT JOIN Material 投影；
    /// 主数据无此物料 ⇒ null 整键省略，前端回退编码，**不编造名称**。写入时本字段被忽略——
    /// 权威名永远来自 Material 主数据，本表只存 MaterialId/MaterialCode 事实。）
    /// </summary>
    public string? MaterialName { get; init; }

    /// <summary>
    /// 接收仓库
    /// </summary>
    public string ReceivingWarehouse { get; init; } = string.Empty;

    /// <summary>
    /// 人工设定的到货预期时间
    /// </summary>
    public DateTime ManualEta { get; init; }

    /// <summary>
    /// 是否生效（0=取消，2号位回退ERP ETA）
    /// </summary>
    public bool IsActive { get; init; }

    /// <summary>
    /// 最后更新人
    /// </summary>
    public string UpdatedBy { get; init; } = string.Empty;

    /// <summary>
    /// 最后更新时间
    /// </summary>
    public DateTime UpdatedAt { get; init; }

    /// <summary>
    /// 创建人
    /// </summary>
    public string CreatedBy { get; init; } = string.Empty;

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// 备注
    /// </summary>
    public string Remark { get; init; } = string.Empty;

    /// <summary>
    /// **11-14（4号位 v2.0 §二.R2 取向乙）**：本条覆盖当前能否命中一条**能生效的**供给事实
    /// （四元组 `PONo+LineNo+MaterialId+ReceivingWarehouse` ≡ 供给事实 `SourceDocumentNo+LineNo+MaterialId+StorageCode`）。
    /// `true`=会参与排产；`false`=命不中，**静默不生效**（页面据此标注「未命中当前供货事实」）；
    /// `null`=**未判定**（标注用的供给事实取数失败；或该路径不做标注，如 `GetActiveOverridesAsync`
    /// 的装配侧装载）——不得把未判定当未命中显示。列表/详情/Upsert 回包（经详情查询）均带标注。
    /// 计算字段：不落库、不参与 MERGE；权威判定仍在装配侧 AvailableTimeCalculator。
    /// </summary>
    public bool? IsMatched { get; set; }
}

/// <summary>
/// 订单行供货组合（**11-14 4号位 v2.0 §二.R1**）：一行 = 供给事实里一条 **(物料 × 存储地点)** 组合，
/// 物料与仓库**成对取自同一条供给事实**。人工到货时间的覆盖键是四元组精确匹配，
/// 两字段各选各的必然拼出命不中的组合 ⇒ 抽屉必须用本响应同时锁定「物料 + 收货仓库」。
/// 只含**能生效**的组合（跟 2号位 TimedSupplyFactLoader 同一资格谓词：有效 + 数量&gt;0 + 五类采购在途），
/// 且行号为整数（非整数行号的事实永远覆盖不上，故不返回）。查无 = 空数组。
/// </summary>
public sealed class ProcurementManualEtaSupplyComboDto
{
    /// <summary>采购订单号（= 供给事实 SourceDocumentNo）。</summary>
    public string PONo { get; init; } = string.Empty;

    /// <summary>行号（= 供给事实 SourceDocumentLineNo 的整数形态，覆盖键的一部分）。</summary>
    public int LineNo { get; init; }

    /// <summary>物料ID（= 覆盖键 MaterialId）。</summary>
    public int MaterialId { get; init; }

    /// <summary>物料编码（主数据优先，主数据缺行回退事实码）。</summary>
    public string MaterialCode { get; init; } = string.Empty;

    /// <summary>物料名（主数据缺行 ⇒ null，前端回退编码）。</summary>
    public string? MaterialName { get; init; }

    public string? Spec { get; init; }

    public string? Uom { get; init; }

    /// <summary>存储地点（= 供给事实 StorageCode，**必须原样写入 ReceivingWarehouse**）。</summary>
    public string StorageCode { get; init; } = string.Empty;

    /// <summary>存储地点名（ext_MES_ProcessCode_View；视图无此码 ⇒ null，前端回退编码）。</summary>
    public string? StorageName { get; init; }
}

/// <summary>
/// 人工到货时间页指标条一次计数（**R3，11-12 4号位件**）：
/// 与列表同一 WHERE 单源、单批 SQL 一次给全五数，替代前端 5 个请求。
/// 「已过期/7 天内到期」按**服务器库内基准**（GETDATE()=工厂墙钟，本机实测 UTC+8）判日界，
/// 桶定义：overdue=生效中 且 ManualEta &lt; 今日00:00；dueSoon=生效中 且 今日00:00 ≤ ManualEta &lt; 今日+7天00:00（半开）。
/// active+cancelled=all 恒成立；三时间桶互不重叠且并集=active。
/// </summary>
public sealed class ProcurementManualEtaSummaryDto
{
    public int AllCount { get; init; }
    public int ActiveCount { get; init; }
    public int CancelledCount { get; init; }
    public int OverdueCount { get; init; }
    public int DueSoonCount { get; init; }

    /// <summary>本次统计实际使用的日界（服务端墙钟回带，11-13 函 §2.3 采纳）：overdue ⇒ `ManualEta &lt; overdueBefore`。</summary>
    public DateTime OverdueBefore { get; init; }
    /// <summary>dueSoon 下界（含）=今日00:00。</summary>
    public DateTime DueSoonFrom { get; init; }
    /// <summary>dueSoon 上界（不含）=今日+7天00:00。前端展示"截至"文案请以这三值为准，勿再自算今天。</summary>
    public DateTime DueSoonTo { get; init; }
}

/// <summary>
/// 人工到货时间筛选候选（**R4，11-12 4号位件**）：本表 distinct 值（"有 ETA 记录的采购单/物料"，非主数据全集），
/// 供下拉直出，替代前端 pageSize=200 扫描。**含已取消行**（候选存在的意义=能筛到记录）；
/// 每维按值升序截 500，Truncated=true 时提示"候选不全请改用搜索"（当前 dev：PONo 11 / 物料 7，远未触顶）。
/// 收货仓库候选仍走既有 /warehouses 端点，不在本响应内。
/// </summary>
public sealed class ProcurementManualEtaFacetsDto
{
    public List<string> PONos { get; init; } = new();
    public List<string> MaterialCodes { get; init; } = new();
    /// <summary>任一维触 500 顶 ⇒ true。</summary>
    public bool Truncated { get; init; }
}
