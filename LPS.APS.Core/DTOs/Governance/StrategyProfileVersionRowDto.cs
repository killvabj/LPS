namespace LPS.APS.Core.DTOs.Governance;

/// <summary>
/// 策略包「主子表联查·一版一行」DTO（2026-11-11，路线 A：策略包维护页新列表源）。
/// 每行 = 一条 StrategyProfile + 其一个 StrategyProfileVersion（版本行），主表字段冗余随行。
/// 策略包版本本身无 RunType 列（P0-06 契约），RunType 取自父表；且版本通过
/// RuleSetVersionId + ParameterSetVersionId 绑定规则集/参数集已发布版本，一并展现在行内供前端直显。
/// 设计动机：旧列表（<see cref="StrategyProfile"/> 纯主表）不含任何版本状态/绑定信息，前端无法从列表看出各版本状态与绑定；
/// 本行列「一版一行」使各版本独立可读（策略包缺陷 2026-11-11 定位——补版本行视图）。
/// </summary>
/// <remarks>开发者：3号位</remarks>
public sealed class StrategyProfileVersionRowDto
{
    // —— 主表列（冗余随行）——
    public long StrategyProfileId { get; init; }
    public string StrategyProfileCode { get; init; } = string.Empty;
    public string StrategyProfileName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? RunType { get; init; }
    public bool? IsActive { get; init; }
    public DateTime? StrategyProfileCreatedAt { get; init; }

    // —— 策略包版本绑定列（一版一行）——
    public long VersionId { get; init; }
    public string VersionCode { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public long RuleSetVersionId { get; init; }
    public long ParameterSetVersionId { get; init; }
    public bool IsDefault { get; init; }
    public DateTime? EffectiveFrom { get; init; }
    public DateTime? EffectiveTo { get; init; }
    public DateTime? PublishedAt { get; init; }
    public string? PublishedBy { get; init; }
    public DateTime? CreatedAt { get; init; }
}