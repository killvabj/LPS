using LPS.APS.Core.Dto;

namespace LPS.APS.Core.Interfaces;

/// <summary>
/// 写入表保留期清理（2号位职责）。规则来源：0号位 2026-11-02
/// 《致2号位_三份回执全部问题一体化正式答复_v2.0》§2.1 的 D1~D4。
/// </summary>
/// <remarks>
/// <para>
/// <b>调用时机（D4 原文）</b>：<i>「夜间 FULL 成功后设独立、受控的小批维护步骤；不在 Solver 或主落库事务中删除；
/// 不新增 Hangfire Cron；同时提供授权人工补跑入口。」</i>
/// ⇒ 本服务<u>不</u>自建定时器。<see cref="RunAsync"/> 由排程编排器在整轮 Run 收口为
/// <c>COMPLETED</c> 之后调用一次；也可由人工入口直接调用（授权补跑）。
/// </para>
/// <para>
/// <b>默认不动数据</b>：<see cref="PlanAsync"/> 纯只读；<see cref="RunAsync"/> 默认
/// <c>dryRun = true</c>，只测算不删。真删必须显式传 <c>false</c>。
/// </para>
/// <para>
/// <b>失败隔离</b>：清理异常只记录、不抛出到排程链路——「清理失败不反推正式排程失败」（D4 原文）。
/// </para>
/// </remarks>
public interface IRetentionMaintenanceService
{
    /// <summary>
    /// 只读测算：按 D1~D4 逐条算出「总行数／超窗／受保护／可删」。**不写库。**
    /// </summary>
    Task<IReadOnlyList<RetentionPlanItem>> PlanAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 执行：先测算，再按 <paramref name="dryRun"/> 决定是否真删。
    /// </summary>
    /// <param name="dryRun">
    /// <c>true</c>（默认）＝只测算、一行都不删；<c>false</c> ＝按测算结果分批删除。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<RetentionRunResult> RunAsync(bool dryRun = true, CancellationToken cancellationToken = default);
}
