using LPS.APS.Core.Authorization;

namespace LPS.APS.Core.Interfaces;

/// <summary>
/// 域内实体级业务范围<b>边界</b>守卫（缺口 A 第二层，2026-10-29 0号位 裁定增补）
/// </summary>
/// <remarks>
/// 与 <see cref="IDepartmentScopeGuard"/> 的分工（<b>两层缺一不可</b>）：
/// 1) <b>入口闸</b>（<see cref="IDepartmentScopeGuard.EnsureAsync"/>，时点 A，交集 <c>S ∩ D ≠ ∅</c>）
///    回答「本用户能不能进本域」；
/// 2) <b>边界校验</b>（本接口，装配侧同一时点调用）回答「进来之后能碰哪些实体」——
///    要求本域真实受影响的部门集 <c>D</c> 与工厂集 <c>F</c> <b>整体落入</b>授权集 <c>S</c>
///    （<c>D ⊆ S</c> 且 <c>F ⊆ S</c>），任一条越界即 fail-closed 中止整轮。
/// <para>
/// <b>为什么不是交集</b>：只判交集会把「部分授权」放行成「全域写入」——
/// 仅持 <c>BJ115</c> 的用户启动含多部门的域，排程结果会写到其无权部门上。
/// 2026-10-29 0号位 明确不允许该升级，故边界层采用<b>强校验</b>（全集子集），而不是「严格过滤」。
/// </para>
/// <para>
/// <b>保留语义</b>：
/// 1) 维度整体未配 ⇒ 不参与（范围C），与入口闸同解；
/// 2) <c>IsGlobal</c> ⇒ 放行；
/// 3) 无 Run（<c>null</c>／<c>0</c> 哨兵，手动/联调入口）⇒ 跳过并记 INFO（与入口闸同源，<b>不冒充已 enforce</b>）；
/// 4) 目标 Id 解析不出编码 ⇒ fail-closed（不放过无法证明的值）。
/// </para>
/// <para>
/// <b>边界</b>：不新增表／状态值／审计表，判定纯内存、不写库；
/// <b>不修改</b> <see cref="IDepartmentScopeGuard"/> 既有签名（红线 #5）。
/// </para>
/// </remarks>
public interface IDomainBoundaryScopeGuard
{
    /// <summary>
    /// 断言本域真实受影响的部门集与工厂集<b>整体</b>在授权范围内；任一越界抛 <see cref="ScopeViolationException"/>。
    /// </summary>
    /// <param name="scheduleRunId">Run 主键（<c>null</c>／<c>0</c>＝无冻结主体，跳过判定）</param>
    /// <param name="domainKey">当前域键（仅日志与异常定位，不参与判定）</param>
    /// <param name="departmentIds">本域真实 D：<c>MaterialStageDepartmentContexts</c> 的 <c>ProductionDepartmentId</c> 去重投影</param>
    /// <param name="factoryIds">
    /// 本域真实 F：本域实际涉及工厂 Id 去重投影。
    /// <b><c>FAMILY</c> 域不得因 <c>FactoryId=NULL</c> 传空集冒充全放行</b>——域跨全厂是数据形状，不是授权授予。
    /// </param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <exception cref="ScopeViolationException">
    /// Code = <c>DEPT_SCOPE_BOUNDARY_VIOLATION</c>／<c>FACTORY_SCOPE_BOUNDARY_VIOLATION</c>（实体越界或 Id 无法解析）、
    /// <c>DEPT_SCOPE_ACTOR_UNRESOLVED</c>（Run 缺失／<c>TriggeredBy</c> 无法解析为有效用户）。
    /// </exception>
    Task EnsureBoundaryAsync(
        long? scheduleRunId,
        string domainKey,
        IReadOnlyCollection<int> departmentIds,
        IReadOnlyCollection<int> factoryIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按<b>用途</b>拆分的边界断言（0号位 2026-10-29《01》`:16`＋`:18`）：需求归属工厂、可写生产工厂、
    /// 采购只读工厂<b>分别</b>校验，任一组越界即抛 <see cref="ScopeViolationException"/>。
    /// </summary>
    /// <remarks>
    /// <b>为什么不拍平成一个 <c>factoryIds</c></b>：拍平后「需求厂越界」与「生产厂越界」无法区分，
    /// 「采购只读厂」会丢掉整个维度——这正是 `01 :16` 禁止的做法。
    /// <para>
    /// <b>判据与 <see cref="EnsureBoundaryAsync"/> 完全相同</b>（全集子集、fail-closed、范围C、Global、无 Run 跳过）；
    /// 差别只在输入形状与时点。<b>不新增错误码</b>，用途写进异常文案与日志。
    /// </para>
    /// <para>
    /// <b>不修改</b> <see cref="EnsureBoundaryAsync"/> 既有签名（红线 #5）；两方法并存，由调用方按时点选用。
    /// </para>
    /// </remarks>
    /// <param name="scheduleRunId">Run 主键（<c>null</c>／<c>0</c>＝无冻结主体，跳过判定）</param>
    /// <param name="domainKey">当前域键（仅日志与异常定位，不参与判定）</param>
    /// <param name="phase">校验时点（创建装载／提交激活发布前），只影响日志文案，不影响判据</param>
    /// <param name="departmentIds">本域真实 D（生产部门）</param>
    /// <param name="demandFactoryIds">需求归属工厂：Order.SourceFactoryId 等需求侧来源，<b>不得由已装载任务反推</b></param>
    /// <param name="writableFactoryIds">可写生产工厂：Task.ProductionDepartmentId → ProductionDepartment.FactoryId</param>
    /// <param name="readOnlySupplyFactoryIds">采购收货只读工厂：只读供给不构成写权限</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <exception cref="ScopeViolationException">
    /// Code 仍只有三个：<c>DEPT_SCOPE_BOUNDARY_VIOLATION</c>／<c>FACTORY_SCOPE_BOUNDARY_VIOLATION</c>／<c>DEPT_SCOPE_ACTOR_UNRESOLVED</c>
    /// </exception>
    Task EnsureBoundaryByPurposeAsync(
        long? scheduleRunId,
        string domainKey,
        LPS.APS.Core.Enum.ScopeCheckPhase phase,
        IReadOnlyCollection<int> departmentIds,
        IReadOnlyCollection<int> demandFactoryIds,
        IReadOnlyCollection<int> writableFactoryIds,
        IReadOnlyCollection<int> readOnlySupplyFactoryIds,
        CancellationToken cancellationToken = default);
}
