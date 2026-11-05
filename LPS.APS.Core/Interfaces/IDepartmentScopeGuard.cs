using LPS.APS.Core.Authorization;

namespace LPS.APS.Core.Interfaces;

/// <summary>
/// 部门维度业务范围守卫（队列3，3号位 治理域）
/// 时点 A：每域 pegging 完成、Solve 之前，由装载编排侧（2号位）以本域<b>真实 D</b> 调一次；
/// 装载侧零判定逻辑、零新查询，只做一行投影 + 一次调用。
/// </summary>
/// <remarks>
/// 语义（范围C + 时点A）：
/// 1) 主体<b>只认冻结的 ScheduleRun.TriggeredBy</b>（登录名），绝不读取当前请求上下文 ⇒ 同一 Run 任何人重跑结论恒定；
/// 2) Department 维度<b>整体未配置</b> ⇒ 不构成拒绝理由（放行）；
/// 3) <c>D = ∅</c> ⇒ 本域无部门可碰，不是越权（数据缺陷走既有隔离通道，<b>绝不落越权码</b>）；
/// 4) <c>ScheduleRunId = null</c> 或 <c>0</c>（手动/联调入口无 Run；<b>0 是本仓既有成文哨兵</b>）⇒ 无冻结主体可判，
///    <b>跳过判定并记 INFO</b>（已登记残差：该入口准入由既有功能权限码把关，不冒充已 enforce）；
///    负值或越出 <c>int</c> 值域 ⇒ <b>不跳过</b>，按主体未解析 fail-closed（调用方缺陷，静默跳过会掩盖问题）；
/// 5) 已配置 ⇒ 经主档 Id→DeptCode 转换后判 <c>S ∩ D ≠ ∅</c>，不满足即 fail-closed 抛异常。
/// 边界：不新增表、不新增状态值、不新增审计表；判定纯内存，不写库。
/// </remarks>
public interface IDepartmentScopeGuard
{
    /// <summary>
    /// 断言本域部门集未越界；越界或主体无法解析时抛 <see cref="ScopeViolationException"/>（不做软失败）。
    /// </summary>
    /// <param name="scheduleRunId">
    /// Run 主键（对齐 <c>DomainSolveRequest.ScheduleRunId</c> 的 <c>long?</c> 形态）。
    /// <c>null</c> = 手动/联调入口无 Run ⇒ 跳过判定（规则见类型注释 4）。
    /// </param>
    /// <param name="domainKey">当前域键（仅用于日志与异常定位，不参与判定）</param>
    /// <param name="departmentIds">本域真实 D：<c>MaterialStageDepartmentContexts</c> 的 <c>ProductionDepartmentId</c> 去重投影</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <exception cref="ScopeViolationException">
    /// Code = <c>DEPT_SCOPE_VIOLATION</c>（部门越界 / Id 无法解析的 fail-closed）或
    /// <c>DEPT_SCOPE_ACTOR_UNRESOLVED</c>（Run 缺失、TriggeredBy 无法解析为有效用户）。
    /// 编排层据此中止整轮（A 案），而不是记成单域计算失败。
    /// </exception>
    Task EnsureAsync(
        long? scheduleRunId,
        string domainKey,
        IReadOnlyCollection<int> departmentIds,
        CancellationToken cancellationToken = default);
}
