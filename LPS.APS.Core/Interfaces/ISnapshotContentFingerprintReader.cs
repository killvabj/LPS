namespace LPS.APS.Core.Interfaces;

/// <summary>
/// 策略包绑定内容指纹读取器（2026-10-30，2号位；B-6「装载侧受控失效」的取数侧）。
///
/// **解决的问题**：快照缓存原本以 <c>StrategyProfileVersionId</c> 为唯一键。该键在版本表里不会变，
/// 而「已发布版本的内容被原地改」（直连 SQL 绕过 <c>GovernanceVersionService.EnsureUpdatable</c>）
/// 现网已发生三次（<c>AuditLog</c> 8391 / 8462 / 9486，版本号一次没变）⇒ 同进程内重取恒返回旧内容、
/// 静默无痕。缓存键补上内容维度后，「内容变了」必然换键，无需重启即可见到新值。
///
/// **为什么必须是库内哈希而不是回读正文**：回读正文（<c>RuleSetVersion</c> + <c>ParameterSetVersion</c>
/// 两个 <c>ContentSnapshotJson</c>，现网合计约 17KB）的成本与「完全重新装载」同量级，缓存将失去意义。
/// 本方法在库内做 <c>HASHBYTES('SHA2_256')</c>，只回传 64 位十六进制串。
///
/// **为什么不复用 <c>UpdatedAt</c>**：<c>RuleSetVersion</c> 478 的 <c>UpdatedAt</c> 为 NULL
/// （历史直写不留痕），元数据列发现不了原地改，只有内容哈希能发现。
/// </summary>
/// <remarks>开发者：2号位</remarks>
public interface ISnapshotContentFingerprintReader
{
    /// <summary>
    /// 取某策略包版本所绑定内容（规则集 + 参数集两个快照正文）的指纹。
    /// 语义：<c>SHA2_256( RuleSetVersion.ContentSnapshotJson ‖ ParameterSetVersion.ContentSnapshotJson )</c>，
    /// 库内计算，返回 64 位大写十六进制串。
    /// </summary>
    /// <returns>
    /// 指纹串；<b>策略包版本本身或其绑定的子版本不存在时返回 <c>null</c></b>
    /// （调用方据此跳过缓存、走完整装载路径，由装载路径抛出具体错误——不在此处吞掉）。
    /// </returns>
    /// <remarks>
    /// 只读、幂等、无副作用；不校验状态（PUBLISHED 校验仍归装载路径）。
    /// 注意：返回值反映的是**调用时刻**的库内内容，与随后装载到的正文之间存在极短的时间差；
    /// 若此间恰好被改，本条缓存会以旧指纹存一次，下次调用指纹不同即自然换键（自愈，不残留）。
    /// </remarks>
    Task<string?> GetStrategyProfileContentFingerprintAsync(
        long strategyProfileVersionId,
        CancellationToken ct = default);
}
