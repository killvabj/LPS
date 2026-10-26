using LPS.APS.Core.Entities.APS;

namespace LPS.APS.Core.Interfaces;

/// <summary>
/// 策略包主表仓储接口（P0-06：3号位治理域）
/// 对应表：APS_Production.dbo.StrategyProfile
/// RunType 匹配语义（C2-3）：StrategyProfileVersion 无 RunType 列，须经父表 StrategyProfile.RunType 匹配。
/// </summary>
/// <remarks>开发者：3号位</remarks>
public interface IStrategyProfileRepository
{
    /// <summary>获取策略包主表记录</summary>
    System.Threading.Tasks.Task<StrategyProfile?> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// 按 RunType 获取启用的策略包（RunType 匹配候选）
    /// 返回列表而非单对象（红线 #4：禁止盲目 First()），由 Application 层结合版本状态判定。
    /// </summary>
    System.Threading.Tasks.Task<IReadOnlyList<StrategyProfile>> GetByRunTypeAsync(string runType, CancellationToken ct = default);

    /// <summary>
    /// 策略包列表查询（3-4联调接口 G1/A12：4号位策略包列表页）
    /// 支持 启停过滤 + 编码/名称关键字 + 分页。
    /// </summary>
    System.Threading.Tasks.Task<IReadOnlyList<StrategyProfile>> GetListAsync(
        bool? activeOnly = null,
        string? keyword = null,
        int? skip = null,
        int? take = null,
        CancellationToken ct = default);

    // ==================== 主表写路径（2026-10-25：补齐 4号位 缺口「策略包缺主表写端点」，照 rule-set 10-22 范式） ====================

    /// <summary>
    /// 新建策略包主表（POST /strategy-profile）：INSERT 并回带自增 Id（SCOPE_IDENTITY）。
    /// StrategyProfileCode 由用户填写，创建时唯一校验；编码一经创建不可变更（对齐无重命名语义）。
    /// Q2：**只建壳，不能自动跟建版本**（StrategyProfileVersion.RuleSetVersionId/ParameterSetVersionId 均必填 FK，
    /// 没有「空白首版」合法状态 ⇒ 首版由用户在「新建草稿」里选两个已发布版本）。
    /// </summary>
    System.Threading.Tasks.Task<StrategyProfile> CreateAsync(StrategyProfile entity, CancellationToken ct = default);

    /// <summary>
    /// 更新策略包主表（PUT /strategy-profile/{id}）：仅改 StrategyProfileName / Description / RunType / IsActive / UpdatedAt / UpdatedBy，
    /// StrategyProfileCode 冻结不可改；IsActive=0 仅关主表不级联版本（Q4，运行时装载判主表归 2号位/1号位 转达）。
    /// </summary>
    System.Threading.Tasks.Task UpdateAsync(StrategyProfile entity, CancellationToken ct = default);

    /// <summary>
    /// 策略包编码唯一性校验（Q1：大小写不敏感去重；excludeId 更新时排除自身）。
    /// </summary>
    System.Threading.Tasks.Task<bool> StrategyProfileCodeExistsAsync(string code, long? excludeId = null, CancellationToken ct = default);

    /// <summary>
    /// 物理删除策略包整族（2026-10-26 同 R1 口径）：单事务内「删 StrategyProfileVersion → 删 StrategyProfile」。
    /// 调用方（服务层）已保证：无任何 PUBLISHED（已发布）版本 + 任一版本未被 ScheduleRun（运行）引用（FK 反查阻断先行）。
    /// </summary>
    System.Threading.Tasks.Task DeleteFamilyAsync(long id, CancellationToken ct = default);
}
