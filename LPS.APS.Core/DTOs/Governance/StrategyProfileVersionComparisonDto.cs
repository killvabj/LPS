namespace LPS.APS.Core.DTOs.Governance;

/// <summary>
/// 策略包版本富对比结果（S12：06 §6.4.1「版本富对比（H1）」；0号位 VG-004 定 06 为准）。
/// </summary>
/// <remarks>
/// 开发者：3号位
/// <para>
/// <b>与既有 <c>strategy-profile/version/diff</c> 的分工</b>：diff 是**字段级差异列表**（SourceValue/TargetValue）；
/// 富对比（H1 形态）在字段差异之外补三样可判读的东西——**双方版本概要**、**绑定差异**、**变更汇总计数**，
/// 对齐 <c>plan-version/{a}/compare-with/{b}</c> 的 H1 读法，前端无需再自行拼装。
/// </para>
/// <para>
/// <b>契约边界</b>：06 对 S12 只冻结了「路径、a/b 为 VersionId、治理读、403」，
/// **未冻结字段清单** —— 本 DTO 为 3号位 登记的最小集（S-14）。
/// 不新增实体／表／字段／通用权限平台／错误码，不触《01》`:18` 四项枚举。
/// </para>
/// <para>
/// <b>参数方向</b>：与 <c>plan-version/{candidate}/compare-with/{base}</c> 同序 ——
/// 路径首段为<b>对比方</b>（a），次段为<b>基准方</b>（b）；DTO 内 <see cref="Compare"/> 对 a、<see cref="Base"/> 对 b。
/// </para>
/// </remarks>
public class StrategyProfileVersionComparisonDto
{
    /// <summary>基准方（路径次段 b）版本概要</summary>
    public StrategyProfileVersionBriefDto Base { get; set; } = new();

    /// <summary>对比方（路径首段 a）版本概要</summary>
    public StrategyProfileVersionBriefDto Compare { get; set; } = new();

    /// <summary>组合载体绑定差异（策略包版本的核心内容：RuleSet + ParameterSet 绑定）</summary>
    public StrategyProfileVersionBindingDiffDto Binding { get; set; } = new();

    /// <summary>变更汇总计数（H1：先给总数，再给明细）</summary>
    public StrategyProfileVersionChangeSummaryDto Summary { get; set; } = new();

    /// <summary>字段级差异明细（与 <c>strategy-profile/version/diff</c> 同口径、同结构，复用 <see cref="FieldDiff"/>></summary>
    public List<FieldDiff> FieldDiffs { get; set; } = new();

    /// <summary>对比时间（本地口径 <c>DateTime.Now</c>，不引入 UTC）</summary>
    public DateTime ComparedAt { get; set; }
}

/// <summary>
/// 策略包版本概要（富对比的「双方版本概要」块）
/// </summary>
/// <remarks>开发者：3号位</remarks>
public class StrategyProfileVersionBriefDto
{
    /// <summary>策略包版本 Id</summary>
    public long VersionId { get; set; }

    /// <summary>所属策略包 Id</summary>
    public long StrategyProfileId { get; set; }

    /// <summary>版本编码</summary>
    public string VersionCode { get; set; } = string.Empty;

    /// <summary>状态（DRAFT/SUBMITTED/APPROVED/PUBLISHED/DISABLED/ARCHIVED）</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>是否默认版本</summary>
    public bool IsDefault { get; set; }

    /// <summary>绑定的规则集版本 Id（组合载体之一）</summary>
    public long RuleSetVersionId { get; set; }

    /// <summary>绑定的规则集版本编码；<c>null</c>＝按 Id 查不到（绑定 Id 仍为准，不因此判对比失败）</summary>
    public string? RuleSetVersionCode { get; set; }

    /// <summary>绑定的参数集版本 Id（组合载体之二）</summary>
    public long ParameterSetVersionId { get; set; }

    /// <summary>绑定的参数集版本编码；<c>null</c>＝按 Id 查不到</summary>
    public string? ParameterSetVersionCode { get; set; }

    /// <summary>生效起始</summary>
    public DateTime? EffectiveFrom { get; set; }

    /// <summary>生效截止</summary>
    public DateTime? EffectiveTo { get; set; }

    /// <summary>发布时间</summary>
    public DateTime? PublishedAt { get; set; }

    /// <summary>发布人</summary>
    public string? PublishedBy { get; set; }
}

/// <summary>
/// 策略包版本绑定差异（组合载体：规则集版本 + 参数集版本）
/// </summary>
/// <remarks>开发者：3号位</remarks>
public class StrategyProfileVersionBindingDiffDto
{
    /// <summary>双方绑定的规则集版本是否不同</summary>
    public bool RuleSetChanged { get; set; }

    /// <summary>双方绑定的参数集版本是否不同</summary>
    public bool ParameterSetChanged { get; set; }

    /// <summary>基准方绑定的规则集版本 Id</summary>
    public long BaseRuleSetVersionId { get; set; }

    /// <summary>对比方绑定的规则集版本 Id</summary>
    public long CompareRuleSetVersionId { get; set; }

    /// <summary>基准方绑定的参数集版本 Id</summary>
    public long BaseParameterSetVersionId { get; set; }

    /// <summary>对比方绑定的参数集版本 Id</summary>
    public long CompareParameterSetVersionId { get; set; }

    /// <summary>任一绑定发生变化（规则集或参数集）</summary>
    public bool AnyChanged => RuleSetChanged || ParameterSetChanged;
}

/// <summary>
/// 策略包版本变更汇总（H1：计数先于明细）
/// </summary>
/// <remarks>开发者：3号位</remarks>
public class StrategyProfileVersionChangeSummaryDto
{
    /// <summary>参与对比的字段总数</summary>
    public int TotalFieldCount { get; set; }

    /// <summary>发生变化的字段数</summary>
    public int ChangedFieldCount { get; set; }

    /// <summary>发生变化的字段名（与 <see cref="FieldDiff.FieldName"/> 同源，供前端直接标注）</summary>
    public IReadOnlyList<string> ChangedFieldNames { get; set; } = Array.Empty<string>();

    /// <summary>状态是否变化</summary>
    public bool StatusChanged { get; set; }

    /// <summary>默认版本标记是否变化</summary>
    public bool DefaultFlagChanged { get; set; }

    /// <summary>生效窗（起／止任一）是否变化</summary>
    public bool EffectiveWindowChanged { get; set; }

    /// <summary>发布信息（发布时间／发布人任一）是否变化</summary>
    public bool PublishInfoChanged { get; set; }
}
