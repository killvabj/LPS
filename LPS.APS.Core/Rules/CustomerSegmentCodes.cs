namespace LPS.APS.Core.Rules;

/// <summary>
/// 客户分段（<c>CustomerSegment</c>）冻结词表。
///
/// 值域只有七个（冻结 07 v5.1.15 :2678 / :2760 / :3506；映射在表结构 08 v5.1.8.9 :4543
/// 已明文写出「跨厂 → INTER_FACTORY　其他 → OTHER」）。
/// 中文值（海外/国内/其他/越南/跨厂/日本/失效）是 **`CustomerCodeMap` 字典的历史漂移**，
/// 不是合法取值；派生规则见 <c>sp_ValidateAndPromoteOrders</c>：
/// Code 空 → NULL；有值但无 <c>IsActive=1</c> 匹配 → <see cref="Unknown"/> ＋ 诊断。
///
/// 收成常量的理由：SH 供给入池（<c>LoadInterFactoryShipmentsAsync</c>）与需求根排除
/// （<c>SchedulingOrchestrator</c>）必须**同一口径**，两处注释都写着「避免两侧口径漂移」——
/// 字面量散落时改一处漏一处不报错，只会让 SH 行静默进出集合。
/// </summary>
public static class CustomerSegmentCodes
{
    /// <summary>日本</summary>
    public const string Japan = "JAPAN";

    /// <summary>国内</summary>
    public const string Domestic = "DOMESTIC";

    /// <summary>海外</summary>
    public const string Overseas = "OVERSEAS";

    /// <summary>越南</summary>
    public const string Vietnam = "VIETNAM";

    /// <summary>跨厂（厂间出荷指示 SH 的客户分段）</summary>
    public const string InterFactory = "INTER_FACTORY";

    /// <summary>其他</summary>
    public const string Other = "OTHER";

    /// <summary>无法归入以上任何一类（含字典未命中）</summary>
    public const string Unknown = "UNKNOWN";
}
