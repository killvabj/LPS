namespace LPS.APS.Engine.Services.Sync.Dto;

/// <summary>
/// 一个「物料 × 阶段」定位键。对应 MaterialStageDeptContext 的物理键 (MaterialId, StageCode)。
/// 注意：该表**没有 DomainKey**，键是全局的 —— 定向补缺按 Domain 圈定范围，
/// 但写进去的行对所有 Domain 生效，所以范围必须来自调用方，不能由本服务猜。
/// </summary>
public class MaterialStageKey
{
    public int MaterialId { get; set; }

    public string StageCode { get; set; } = string.Empty;
}

/// <summary>
/// sp_RebuildMaterialStageDeptContext_Targeted 的执行结果
/// （与 FULL 版 MaterialStageDeptContextRebuildResultDto 分开：本结果带分层计数与 DryRun 标记）
/// </summary>
public class TargetedMaterialStageDeptContextResultDto
{
    /// <summary>批次号（MSC_CTX_TGT_yyyyMMdd_HHmmss）</summary>
    public string BatchNo { get; set; } = string.Empty;

    /// <summary>true = 只计算未写入（默认）；false = 已写库</summary>
    public bool DryRun { get; set; } = true;

    /// <summary>圈定的目标键个数</summary>
    public int TargetKeys { get; set; }

    /// <summary>新增的 Context 版本行数（DryRun 下恒 0）</summary>
    public int RowsInserted { get; set; }

    /// <summary>因部门变化被关闭的旧版本行数（DryRun 下恒 0）</summary>
    public int RowsExpired { get; set; }

    /// <summary>其中走 ACTIVITY=0 定向兜底补上的键数</summary>
    public int RowsFallback { get; set; }

    /// <summary>本轮登记/将登记的 Issue 条数</summary>
    public int IssueCount { get; set; }

    /// <summary>第②层命中数（人工 Override）</summary>
    public int LayerOverride { get; set; }

    /// <summary>第③层命中数（ACTIVITY=1 唯一推导）</summary>
    public int LayerActivity1 { get; set; }

    /// <summary>第④层命中数（ACTIVITY=0 定向兜底）</summary>
    public int LayerActivity0 { get; set; }

    // ---- Issue 按类型拆开（SP 返回列同名映射，DryRun 下也能拿到）----

    /// <summary>第③层多部门冲突（冲突不猜，且不退到 ACTIVITY=0）</summary>
    public int IssueAct1MultiDept { get; set; }

    /// <summary>第④层多部门冲突</summary>
    public int IssueAct0MultiDept { get; set; }

    /// <summary>Override 本身不合规（Model 多解 / 部门不在字典 / 阶段不符）</summary>
    public int IssueOverrideInvalid { get; set; }

    /// <summary>现行 Context 是人工来源且部门不同 ⇒ 按 §2.2② 不改写</summary>
    public int IssueManualProtected { get; set; }

    /// <summary>三层均无合法唯一部门（A 类：字典里没有该阶段的生产部门）</summary>
    public int IssueNoDept { get; set; }

    /// <summary>耗时（毫秒）</summary>
    public long ElapsedMs { get; set; }

    /// <summary>错误信息（null 表示成功）</summary>
    public string? ErrorMessage { get; set; }

    public bool IsSuccess => string.IsNullOrEmpty(ErrorMessage);

    /// <summary>
    /// 未解析出的键数 = 目标键 − 三层命中。
    /// 注意它**同时含冲突键与无证据键**，两者要分开看须读 MaterialStageDeptContext_Issues
    /// （IssueType = MULTI_DEPT_CONFLICT_FOR_STAGE 的是冲突，NO_DEPT_ANY_LAYER 的是无证据）。
    /// </summary>
    public int UnresolvedKeys => Math.Max(0, TargetKeys - LayerOverride - LayerActivity1 - LayerActivity0);
}
