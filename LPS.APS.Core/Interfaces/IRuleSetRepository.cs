using LPS.APS.Core.DTOs.Governance;
using RuleSet = LPS.APS.Core.Entities.APS.RuleSet;
using Task = System.Threading.Tasks.Task;

namespace LPS.APS.Core.Interfaces;

/// <summary>
/// 规则集主表仓储接口（3号位治理，对应 APS_Production.dbo.RuleSet）
/// 3-4联调接口 G1（A1）：4号位规则集列表页调用。
/// 红线：物料映射类查询返回列表而非单对象（架构红线#4），本接口全部返回列表。
/// </summary>
/// <remarks>开发者：3号位</remarks>
public interface IRuleSetRepository
{
    /// <summary>
    /// 规则集列表查询（支持 启停过滤 + 编码/名称关键字 + 分页）
    /// </summary>
    /// <param name="activeOnly">true=仅启用（IsActive=1）；null=不区分</param>
    /// <param name="keyword">模糊关键字，匹配 RuleSetCode 或 RuleSetName（null=不过滤）</param>
    /// <param name="skip">跳过行数（分页 offset；null=不分页）</param>
    /// <param name="take">返回行数（分页 size；null=不分页）</param>
    /// <param name="ct">取消令牌</param>
    Task<IReadOnlyList<RuleSet>> GetListAsync(
        bool? activeOnly = null,
        string? keyword = null,
        int? skip = null,
        int? take = null,
        CancellationToken ct = default);

    /// <summary>
    /// 规则集列表查询（含最新版本摘要，R2 扩展：催办单 2026-09-22）
    /// 与 <see cref="GetListAsync"/> 同过滤/分页语义，另 LEFT JOIN 最新版本
    /// （CurrentVersionId/CurrentVersionCode、DraftVersionId/DraftVersionCode、
    ///  Status/LastChangeReason/PublishedAt/RetiredAt）。
    /// 注：CurrentVersionId/DraftVersionId 为 C-4 身份补全（准确 VersionId，2026-10-17 落码）。
    /// 真源：版本摘要取自 RuleSetVersion（VersionCode 字符串；DomainKey 无真源未纳入）。
    /// </summary>
    /// <param name="activeOnly">true=仅启用（IsActive=1）；null=不区分</param>
    /// <param name="keyword">模糊关键字，匹配 RuleSetCode 或 RuleSetName（null=不过滤）</param>
    /// <param name="skip">跳过行数（分页 offset；null=不分页）</param>
    /// <param name="take">返回行数（分页 size；null=不分页）</param>
    /// <param name="ct">取消令牌</param>
    Task<IReadOnlyList<RuleSetListItemDto>> GetListWithVersionAsync(
        bool? activeOnly = null,
        string? keyword = null,
        int? skip = null,
        int? take = null,
        CancellationToken ct = default);

    // ==================== 主表写路径（2026-10-22：补齐 4号位 缺口「主表零写端点」，3号位 治理） ====================

    /// <summary>
    /// 按主键读单条规则集主表（Update 存在性 + 编码不可变校验用；不存在返回 null）。
    /// </summary>
    Task<RuleSet?> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// 新建规则集主表（POST /rule-set）：INSERT 并回带自增 Id（SCOPE_IDENTITY）。
    /// RuleSetCode 由用户填写，创建时唯一校验；编码一经创建不可变更（对齐无重命名语义）。
    /// </summary>
    Task<RuleSet> CreateAsync(RuleSet entity, CancellationToken ct = default);

    /// <summary>
    /// 更新规则集主表（PUT /rule-set/{id}）：仅改 RuleSetName / Description / IsActive / UpdatedAt / UpdatedBy，
    /// RuleSetCode 冻结不可改；IsActive=0 仅关主表不级联版本（Q4，运行时装载判主表归 2号位/1号位 转达）。
    /// </summary>
    Task UpdateAsync(RuleSet entity, CancellationToken ct = default);

    /// <summary>
    /// 规则集编码唯一性校验（Q1：大小写不敏感去重；excludeId 更新时排除自身）。
    /// </summary>
    Task<bool> RuleSetCodeExistsAsync(string code, long? excludeId = null, CancellationToken ct = default);

    /// <summary>
    /// 物理删除规则集整族（DELETE /rule-set/{id}，R1 2026-10-25）：
    /// 单事务内先删其全部 RuleSetVersion、再删主表行。调用方须先保证：
    /// 无任何 PUBLISHED 版本（历史不可变）、且版本均未被 StrategyProfileVersion 引用（否则 FK 违约）。</summary>
    Task DeleteFamilyAsync(long id, CancellationToken ct = default);
}
