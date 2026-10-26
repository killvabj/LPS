using LPS.APS.Core.DTOs.Governance;
using ParameterSet = LPS.APS.Core.Entities.APS.ParameterSet;
using Task = System.Threading.Tasks.Task;

namespace LPS.APS.Core.Interfaces;

/// <summary>
/// 参数集主表仓储接口（3号位治理，对应 APS_Production.dbo.ParameterSet）
/// 3-4联调接口 G1（A8）：4号位参数集列表页调用。
/// 红线：物料映射类查询返回列表而非单对象（架构红线#4），本接口全部返回列表。
/// </summary>
/// <remarks>开发者：3号位</remarks>
public interface IParameterSetRepository
{
    /// <summary>
    /// 参数集列表查询（支持 启停过滤 + 编码/名称关键字 + 分页）
    /// </summary>
    /// <param name="activeOnly">true=仅启用（IsActive=1）；null=不区分</param>
    /// <param name="keyword">模糊关键字，匹配 ParameterSetCode 或 ParameterSetName（null=不过滤）</param>
    /// <param name="skip">跳过行数（分页 offset；null=不分页）</param>
    /// <param name="take">返回行数（分页 size；null=不分页）</param>
    /// <param name="ct">取消令牌</param>
    Task<IReadOnlyList<ParameterSet>> GetListAsync(
        bool? activeOnly = null,
        string? keyword = null,
        int? skip = null,
        int? take = null,
        CancellationToken ct = default);

    /// <summary>
    /// 参数集列表查询（含最新版本摘要，Q1 对齐 rule-sets，2026-10-15）
    /// 与 <see cref="GetListAsync"/> 同过滤/分页语义，另 LEFT JOIN 最新版本
    /// （CurrentVersionId/CurrentVersionCode、DraftVersionId/DraftVersionCode、
    ///  Status/LastChangeReason/PublishedAt）。
    /// 注：CurrentVersionId/DraftVersionId 为 C-4 身份补全（准确 VersionId，2026-10-17 落码）。
    /// 真源：版本摘要取自 ParameterSetVersion（VersionCode 字符串；无版本行则版本摘要为 null）。
    /// </summary>
    /// <param name="activeOnly">true=仅启用（IsActive=1）；null=不区分</param>
    /// <param name="keyword">模糊关键字，匹配 ParameterSetCode 或 ParameterSetName（null=不过滤）</param>
    /// <param name="skip">跳过行数（分页 offset；null=不分页）</param>
    /// <param name="take">返回行数（分页 size；null=不分页）</param>
    /// <param name="ct">取消令牌</param>
    Task<IReadOnlyList<ParameterSetListItemDto>> GetListWithVersionAsync(
        bool? activeOnly = null,
        string? keyword = null,
        int? skip = null,
        int? take = null,
        CancellationToken ct = default);

    // ==================== 主表写路径（2026-10-25：补齐 4号位 缺口「参数集缺主表写端点」，照 rule-set 10-22 范式） ====================

    /// <summary>
    /// 按主键读单条参数集主表（Update 存在性 + 编码不可变校验用；不存在返回 null）。
    /// </summary>
    Task<ParameterSet?> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// 新建参数集主表（POST /parameter-set）：INSERT 并回带自增 Id（SCOPE_IDENTITY）。
    /// ParameterSetCode 由用户填写，创建时唯一校验；编码一经创建不可变更（对齐无重命名语义）。
    /// Q2：主表仅建壳（默认 IsActive=true），第一版由前端随后自动跟建空白 DRAFT 版本承载。
    /// </summary>
    Task<ParameterSet> CreateAsync(ParameterSet entity, CancellationToken ct = default);

    /// <summary>
    /// 更新参数集主表（PUT /parameter-set/{id}）：仅改 ParameterSetName / Description / IsActive / UpdatedAt / UpdatedBy，
    /// ParameterSetCode 冻结不可改；IsActive=0 仅关主表不级联版本（Q4，运行时装载判主表归 2号位/1号位 转达）。
    /// </summary>
    Task UpdateAsync(ParameterSet entity, CancellationToken ct = default);

    /// <summary>
    /// 参数集编码唯一性校验（Q1：大小写不敏感去重；excludeId 更新时排除自身）。
    /// </summary>
    Task<bool> ParameterSetCodeExistsAsync(string code, long? excludeId = null, CancellationToken ct = default);

    /// <summary>
    /// 物理删除参数集整族（2026-10-26 同 R1 口径）：单事务内「删 ParameterSetVersion → 删 ParameterSet」。
    /// 调用方（服务层）已保证：无任何 PUBLISHED（已发布）版本 + 任一版本未被策略包版本引用（FK 反查阻断先行）。
    /// </summary>
    Task DeleteFamilyAsync(long id, CancellationToken ct = default);
}
