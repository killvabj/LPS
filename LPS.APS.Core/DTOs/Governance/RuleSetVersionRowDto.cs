namespace LPS.APS.Core.DTOs.Governance;

/// <summary>
/// 规则集「主子表联查·一版一行」DTO（2026-11-11，路线 A：规则集维护页新列表源）。
/// 每行 = 一条 RuleSet + 其一个 RuleSetVersion（版本行），主表字段冗余随行，
/// 前端一行即可直接渲染某版本状态，无需二次 join。
/// 设计动机：旧列表（<see cref="RuleSetListItemDto"/>，一主表带最新概要）把
/// Status/PublishedAt/RetiredAt 取「最新创建版本」，同族既有生效 PUBLISHED 又新增草稿时，
/// 状态被草稿覆盖（规则集缺陷 2026-11-11 定位）。本行形态「一版一行」使各版本状态独立可读、不再互相覆盖。
/// </summary>
/// <remarks>开发者：3号位</remarks>
public sealed class RuleSetVersionRowDto
{
    // —— 主表列（冗余随行）——
    public long RuleSetId { get; init; }
    public string RuleSetCode { get; init; } = string.Empty;
    public string RuleSetName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public bool? IsActive { get; init; }
    public DateTime? RuleSetCreatedAt { get; init; }

    // —— 版本列（一版一行）——
    public long VersionId { get; init; }
    public string VersionCode { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTime? EffectiveFrom { get; init; }
    public DateTime? EffectiveTo { get; init; }
    public DateTime? PublishedAt { get; init; }
    public string? PublishedBy { get; init; }
    public DateTime? CreatedAt { get; init; }
}