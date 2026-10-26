using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;
using LPS.APS.Tests.Integration;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 【2号位 · 求解器运行中采样器的自检】
///
/// <para><b>为什么必须测</b>：该采样器**吞掉一切异常**（观测失败不许搞挂跑批）。这是对的，
/// 但它同时意味着「采样线程根本没跑起来」和「求解器一直闲着」在文件层面长得一模一样 ——
/// 都是空 CSV。而它存在的唯一理由，正是在求解器卡死、拿不到任何汇总时留下证据。
/// 采不到又静默，等于白装。所以这里断言的是它的**声明目的**：每 N 秒真的多一行。</para>
///
/// <para><b>测什么</b>：① 表头与列数与 <c>SampleOnce</c> 拼出来的行严格一致（列漂了 CSV 就废了）；
/// ② 后台线程在采样窗口内**多次**落行（不是只有 <c>Stop</c> 的末尾那一拍）；
/// ③ `Counters` 的活值被如实写进对应列（相位、进度分子分母）；
/// ④ 进程侧四列可解析且非零（RSS／托管堆／CPU／线程数）。</para>
///
/// 开发者：2号位
/// </summary>
public class SolverLiveSamplerTests
{
    [Fact(DisplayName = "运行中采样器_按拍落盘_列数与活值一致且进程侧非零")]
    public void 运行中采样器_按拍落盘_列数与活值一致且进程侧非零()
    {
        var csvPath = Path.Combine(Path.GetTempPath(), $"aps-solver-live-test-{Guid.NewGuid():N}.csv");

        try
        {
            // Counters 是 1号位 的 internal 类型，本程序集经 InternalsVisibleTo 可见（见 Sampler 类注释）。
            var counters = new LPS.APS.Scheduling.Solvers.SolverDiagnostics.Counters
            {
                CurrentPhase = "Phase2",
                Phase2DemandsTotal = 100,
                Phase2DemandsProcessed = 42,
            };

            using (var sampler = new RealDomainFullRunTest.SolverLiveSampler(
                       counters, csvPath, TimeSpan.FromMilliseconds(200)))
            {
                sampler.StartCore();

                // 采样窗口：至少跨过 3 个间隔，让后台线程有机会落多拍。
                Thread.Sleep(750);

                // 中途改一次活值，证明采的是「实时」而不是启动瞬间的快照。
                counters.CurrentPhase = "Phase3";
                counters.Phase2DemandsProcessed = 77;
                Thread.Sleep(300);

                sampler.Stop();
            }

            Assert.True(File.Exists(csvPath), $"采样器没有建出 CSV：{csvPath}");

            var lines = File.ReadAllLines(csvPath).Where(l => l.Length > 0).ToArray();
            Assert.Equal(RealDomainFullRunTest.SolverLiveSampler.Header, lines[0]);

            var rows = lines.Skip(1).ToArray();

            // ② 必须是「后台线程落过行」，不能只有 Stop() 的末尾一拍。
            //    末尾一拍单独存在 = 采样线程压根没跑 ⇒ 这正是本测试要拦的静默失败。
            Assert.True(rows.Length >= 3,
                $"只落了 {rows.Length} 行；采样窗口 1.05s / 间隔 200ms，说明后台采样线程没有按拍执行。原始内容：\n"
                + string.Join("\n", lines));

            var columnCount = RealDomainFullRunTest.SolverLiveSampler.Header.Split(',').Length;
            foreach (var row in rows)
            {
                Assert.Equal(columnCount, row.Split(',').Length);
            }

            // ③ 活值如实入列：p2_done,p2_total 是第 7、8 列（0 基）。
            Assert.Contains(rows, r => r.Split(',')[7] == "42" && r.Split(',')[8] == "100");
            Assert.Contains(rows, r => r.Split(',')[7] == "77");

            // ④ 进程侧四列：rss_mb(19) / heap_mb(20) / cpu_ms(21) / threads(22)。
            var last = rows[^1].Split(',');
            var inv = CultureInfo.InvariantCulture;
            Assert.True(double.Parse(last[19], inv) > 0d, "rss_mb 必须为正");
            Assert.True(double.Parse(last[20], inv) > 0d, "heap_mb 必须为正");
            // cpu_ms 在 1 秒窗口里可能仍为 0（进程在本测试里几乎不耗 CPU），只断言可解析。
            Assert.True(long.Parse(last[21], inv) >= 0L, "cpu_ms 必须可解析且非负");
            Assert.True(int.Parse(last[22], inv) >= 1, "threads 至少 1");

            // 时间轴单调：末行 elapsed_s 应大于首行 —— 否则说明「最后才补写全部行」。
            Assert.True(
                double.Parse(last[0], inv) > double.Parse(rows[0][..rows[0].IndexOf(',')], inv),
                "elapsed_s 未递增：采样行不是按拍追加的");
        }
        finally
        {
            try
            {
                File.Delete(csvPath);
            }
            catch
            {
                // 清理失败不影响结论
            }
        }
    }
}
