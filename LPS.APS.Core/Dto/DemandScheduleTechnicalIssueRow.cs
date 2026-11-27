using System;

namespace LPS.APS.Core.Dto;

/// <summary>
/// A35 落库行：<c>dbo.APS_DemandScheduleTechnicalIssue</c>
/// （唯一键 = <c>PlanVersionId + LogicalDemandKey + BindingKey + IssueCode</c>）。
///
/// <para><b>定位</b>：把**技术性异常**按「真实需求／真实边」精确留证，供事后追溯。
/// 它与业务异常是两回事——见下方两条硬边界。</para>
///
/// <para><b>硬边界（0号位 2026-11-24 §二 ＋ 2026-11-25 v1.10 §5）</b></para>
/// <list type="bullet">
/// <item>技术码**不进** <c>ScheduleExplanationFact</c> 的正式 15 个业务原因码；</item>
/// <item>技术码**不进** <c>Task.Status</c>；</item>
/// <item>未闭合净量**只引用**既有唯一权威账，本表不复制新的数量账；</item>
/// <item>只阻断真实歧义的需求／边，不得把「不同订单的合法不同链」报成异常。</item>
/// </list>
///
/// <para><b>IssueStatus</b>：0号位 已裁显式写 <see cref="DemandScheduleIssueStatus.Detected"/>，
/// 维持 <c>NVARCHAR(20) NOT NULL</c>、**不加 CHECK、不加默认值**，不建人工关闭/审批状态机。</para>
/// </summary>
public sealed class DemandScheduleTechnicalIssueRow
{

    public int PlanVersionId { get; init; }
    public int ScheduleRunId { get; init; }
    public string DomainKey { get; init; } = string.Empty;

    /// <summary>需求键。**唯一键成分**。</summary>
    public string LogicalDemandKey { get; init; } = string.Empty;

    /// <summary>
    /// 绑定键 <c>{ROOT|EDGE}|{BOMNO}|{RepWorksetId}</c>（与 <c>DemandStageChainBindings[].BindingKey</c> 同值）。
    /// **唯一键成分**（可空：无链需求没有绑定）。
    /// </summary>
    public string? BindingKey { get; init; }

    public string? DemandKey { get; init; }
    public long? OrderId { get; init; }
    public int? MaterialId { get; init; }
    public string? StageScopeType { get; init; }
    public string? ResolvedBOMNO { get; init; }
    public long? RepWorksetId { get; init; }

    /// <summary>技术异常码，见 <see cref="DemandScheduleIssueCode"/>。**唯一键成分**。</summary>
    public string IssueCode { get; init; } = string.Empty;

    /// <summary>严重度（<c>ERROR</c> / <c>WARN</c>）。</summary>
    public string Severity { get; init; } = "WARN";

    /// <summary>状态，见 <see cref="DemandScheduleIssueStatus"/>。</summary>
    public string IssueStatus { get; init; } = DemandScheduleIssueStatus.Detected;

    /// <summary>证据 JSON（候选链签名、原因、原始键等）。</summary>
    public string? EvidenceJson { get; init; }

    /// <summary>创建时间（列默认 <c>DEFAULT GETDATE()</c>；BulkCopy 未映射列取不到默认值 ⇒ 调用方显式赋值）。</summary>
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// 技术异常码取值。
///
/// <para>⚠ 这里只登记**已实现且能取证**的码。2026-11-27 起 <c>MATERIAL_IDENTITY_AMBIGUOUS_CAST_MACH</c>
/// 已按 11-24 v1.2 §6 判据实现：当**同一物料的候选链分别以 CAST 阶段与 MACH 阶段为首**（= 同一 ERP 物料号
/// 同时表达 CAST 半成品与 MACH 完成品）而身份不可唯一解析时记录该码。判据只看候选链首阶段族，
/// 不凭猜测打码；无此形态时不产生该码。</para>
/// </summary>
public static class DemandScheduleIssueCode
{
    /// <summary>链路身份歧义：非请求级身份且候选链内容不一致（J16-01 准入）。</summary>
    public const string StageChainIdentityAmbiguous = "STAGE_CHAIN_IDENTITY_AMBIGUOUS";

    /// <summary>链路角色冲突：同物料在 ROOT／EDGE 两种角色下链内容冲突。</summary>
    public const string StageChainRoleConflict = "STAGE_CHAIN_ROLE_CONFLICT";

    /// <summary>本请求/本角色下无链（有 StagePath 事实，但不属于本需求）。</summary>
    public const string StageChainScopeMissing = "STAGE_CHAIN_SCOPE_MISSING";

    /// <summary>本批 StagePath 无该物料任何行 ⇒ 无法限定 Stage（数据缺口／采购件，合法）。</summary>
    public const string StageChainNoRows = "STAGE_CHAIN_NO_ROWS";

    /// <summary>
    /// 同型号实体歧义（0号位 11-24 v1.2 §6）：同一 ERP 物料号的候选链**分别以 CAST 与 MACH 阶段为首**
    /// ⇒ 同时表达 CAST 半成品与 MACH 完成品，身份不可唯一解析。
    /// 仅在候选不一致（即已判为身份歧义）且呈现上述形态时记录；不覆盖角色冲突。
    /// </summary>
    public const string MaterialIdentityAmbiguousCastMach = "MATERIAL_IDENTITY_AMBIGUOUS_CAST_MACH";
}

/// <summary><c>APS_DemandScheduleTechnicalIssue.IssueStatus</c> 取值（0号位 已裁）。</summary>
public static class DemandScheduleIssueStatus
{
    /// <summary>本 PV 发现该技术异常。写入方**显式赋值**，不靠列默认值。</summary>
    public const string Detected = "DETECTED";
}