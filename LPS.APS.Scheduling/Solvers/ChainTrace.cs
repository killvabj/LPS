using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace LPS.APS.Scheduling.Solvers;

/// <summary>
/// **按需求键定向的链式探针**（2026-11-26，1号位；纯观测，零行为影响）。
///
/// <para><b>为什么要有它</b>：离线重放能拿到的现有出口（`APS_REPLAY_TASK_DUMP` /
/// `APS_REPLAY_DUMP` / `APS_REPLAY_FACTS_DUMP`）都只反映**最终结果**。
/// 而「一个执行批在 Phase4 里逐工序落到哪个资源、哪一刻、哪道工序断掉」这件事，
/// 在**整批作废**时（`TryResourceSwitchOnGraph` 返回空表）**不会留下任何 Task** ⇒ 现有出口看不见。
/// 结果是每查一个案例都要重新发明一次探针 —— 本类把它做成一次性、可复用的出口。</para>
///
/// <para><b>两个环境变量</b>：</para>
/// <list type="bullet">
///   <item>`APS_CHAIN_TRACE_PATH` = 输出 TSV 绝对路径（append；首次写表头）。未设 ⇒ 整个类**零开销**（只读一次静态字段）。</item>
///   <item>`APS_CHAIN_TRACE_DEMANDS` = 逗号分隔的 `LogicalDemandKey` 白名单。**只有白名单内**的需求才落行，
///         保证大域重放不会写出兆字节级日志。</item>
/// </list>
///
/// <para><b>纯观测</b>：本类不读库、不改任何判定/返回值/数量；写文件失败一律吞掉（诊断不得打挂求解）。</para>
/// </summary>
internal static class ChainTrace
{
    private static readonly string? Path =
        Environment.GetEnvironmentVariable("APS_CHAIN_TRACE_PATH");

    private static readonly HashSet<string>? Targets = ParseTargets(
        Environment.GetEnvironmentVariable("APS_CHAIN_TRACE_DEMANDS"));

    private static readonly object Gate = new();
    private static bool _headerWritten;

    private static HashSet<string>? ParseTargets(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in raw.Split(','))
        {
            var k = part.Trim();
            if (k.Length > 0) set.Add(k);
        }
        return set.Count > 0 ? set : null;
    }

    /// <summary>探针是否启用（未配两个环境变量 ⇒ `false`，调用方据此跳过一切拼接）。</summary>
    internal static bool Enabled => !string.IsNullOrEmpty(Path) && Targets is not null;

    /// <summary>该需求是否在本次白名单内 —— 未启用恒 `false`。</summary>
    internal static bool IsTarget(string? demandKey)
        => Enabled && !string.IsNullOrEmpty(demandKey) && Targets!.Contains(demandKey);

    /// <summary>
    /// 写一行。字段顺序固定：`key, batch, phase, op, stage, event, resource, a, b, note`。
    /// 任何异常（磁盘满/路径非法）一律吞掉 —— 诊断不得反向影响求解。
    /// </summary>
    internal static void Write(
        string? key, string? batch, string phase, string op, string? stage,
        string evt, string resource = "", string a = "", string b = "", string note = "")
    {
        if (!Enabled) return;
        try
        {
            var line = string.Join('\t',
                key ?? "", batch ?? "", phase, op, stage ?? "", evt, resource, a, b, note);
            lock (Gate)
            {
                using var w = new StreamWriter(Path!, append: true, System.Text.Encoding.UTF8);
                if (!_headerWritten && w.BaseStream.Length == 0)
                {
                    w.WriteLine("key\tbatch\tphase\top\tstage\tevent\tresource\ta\tb\tnote");
                }
                _headerWritten = true;
                w.WriteLine(line);
            }
        }
        catch
        {
            // 纯观测出口：写不出去就不写，绝不干扰求解。
        }
    }

    /// <summary>时间格式化：统一 `yyyy-MM-dd HH:mm:ss`，避免各调用点各写一套。</summary>
    internal static string T(DateTime value) => value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>时长格式化（分钟，两位小数）。</summary>
    internal static string M(double minutes) => minutes.ToString("0.##", CultureInfo.InvariantCulture);
}
