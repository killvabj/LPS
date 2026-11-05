using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **Phase5 快照重放**（2026-10-28，用户要求：「保留进 Phase5 的数据直接测，不用从头再来」）。
///
/// 背景：PV190 一轮全量 ≈ 71 分钟，其中 Phase1(3.6s)+**Phase2(39min)**+Phase3+Phase4(0.5s) ≈ 43 分钟
/// 只是在**生产 Phase5 的入参**。Phase5 本身 21 分钟。改一行 Phase5 代码就要再等 43 分钟才验得上。
///
/// 做法：
///   · 生产侧：`PhaseFiveCompression.SnapshotPhase5Inputs`，只在设了 `APS_PHASE5_SNAPSHOT_DIR` 时落两份盘 ——
///     `request.json`（Phase1 原始入参）＋ `tasks.json`（Phase4 结束后的全部已排 Task）。
///   · 本文件：读这两份，用**同一份**生产代码重放 ——
///     `PhaseOneConstraintBuilder.BuildConstraints`（重建 `ConstraintContext`，实测 3.6 秒，
///     不必序列化 30 多个内部类型的上下文）→ `CompactGaps` → `GenerateTaskDependencies`
///     → `CheckTaskDependencies`（= `ValidateHardResult` 第 7 项）。
///   · 想更快：设 `APS_PHASE5_REPLAY_SLICE_DEMAND=<LogicalDemandKey>` 只重放一条需求，几秒一轮。
///
/// ⚠ 环境变量未设 ⇒ 本测试**直接返回**（手上没有快照）。xUnit 没有运行时 skip，
///   所以这种情况下「绿」**不代表任何事** —— 报告时必须按「未执行」记，不许算进通过数。
/// ⚠ 全程不连库、不起 Solver：只读两份本地 JSON。
/// </summary>
public class PhaseFiveReplayTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public PhaseFiveReplayTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    private const string EnvSnapshotDir = "APS_PHASE5_REPLAY_DIR";
    private const string EnvSliceDemand = "APS_PHASE5_REPLAY_SLICE_DEMAND";
    /// <summary>只跑前 N 条 Task（冒烟用）。与按需求切片不同：**会切断跨需求依赖**，故不能用来证「全局无越序」。</summary>
    private const string EnvMaxTasks = "APS_PHASE5_REPLAY_MAX_TASKS";

    /// <summary>按需求键切片 → 再按条数截断。两个开关都可单用。</summary>
    private static List<FinalTaskDraft> ApplySlice(List<FinalTaskDraft> tasks)
    {
        var slice = Environment.GetEnvironmentVariable(EnvSliceDemand);
        if (!string.IsNullOrWhiteSpace(slice))
        {
            tasks = tasks.Where(t => t.SourceDraftId == slice).ToList();
        }

        var maxText = Environment.GetEnvironmentVariable(EnvMaxTasks);
        if (int.TryParse(maxText, out var max) && max > 0 && tasks.Count > max)
        {
            // 取**前 N 条**：`tasks.json` 的原始顺序就是 Phase4 交付顺序，切片不重排 ⇒ 同一份快照两次跑一致。
            tasks = tasks.Take(max).ToList();
        }

        return tasks;
    }

    private static T Deserialize<T>(string path)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        var value = JsonSerializer.Deserialize<T>(fs, options);
        Assert.NotNull(value);
        return value!;
    }

    /// <summary>
    /// **差分对照**：「同资源连续数组扫描」vs「整改前对象扫描」，同一份快照跑两遍，要求**结果逐位相同**。
    ///
    /// 为什么单列一个测试：2026-10-28 的提速改动（把散在堆上 4 万个对象的两次属性读取换成两条连续
    /// `long[]`）属于纯性能优化，红线是「**结果逐位相同**」。判据同源不等于结果同源 —— 必须实跑对比，
    /// 不靠推理。开关是生产代码里的 `APS_PHASE5_LEGACY_RESOURCE_SCAN`（只切「5) 保序界」的取数方式）。
    ///
    /// 设 `APS_PHASE5_DIFF_SCAN=1` 才执行：一次要跑两遍 `CompactGaps`，不做差分时不必付这个时间。
    /// ⚠ 指纹只落**时间戳**（`FinalDraftId` 排序后逐条 `start/end` ticks），不含任何业务数量字段。
    /// </summary>
    [Fact]
    public void 连续数组扫描与原对象扫描结果逐位相同()
    {
        var dir = Environment.GetEnvironmentVariable(EnvSnapshotDir);
        if (string.IsNullOrWhiteSpace(dir)
            || Environment.GetEnvironmentVariable("APS_PHASE5_DIFF_SCAN") != "1")
        {
            // 未设快照目录、或没显式要求做差分 ⇒ 本测试**未执行**（见类注释）。
            return;
        }

        var request = Deserialize<DomainSolveRequest>(Path.Combine(dir, "request.json"));
        var tasks = Deserialize<List<FinalTaskDraft>>(Path.Combine(dir, "tasks.json"));

        tasks = ApplySlice(tasks);

        Assert.NotEmpty(tasks);

        // `CompactGaps` 只**替换列表元素**、不修改对象本身（`FinalTaskDraft` 是 init-only）
        // ⇒ 留一份原引用清单即可还原，不需要深拷贝。
        var original = new List<FinalTaskDraft>(tasks);

        static string Fingerprint(List<FinalTaskDraft> list)
            => string.Join(
                "\n",
                list.OrderBy(t => t.FinalDraftId, StringComparer.Ordinal)
                    .Select(t => $"{t.FinalDraftId}|{t.PlannedStartTime.Ticks}|{t.PlannedEndTime.Ticks}"));

        string RunOnce(bool legacy, out long elapsedMs)
        {
            tasks.Clear();
            tasks.AddRange(original);

            var constraints = BuildConstraints(request, dir);

            Environment.SetEnvironmentVariable(
                "APS_PHASE5_LEGACY_RESOURCE_SCAN", legacy ? "1" : null);

            var sw = Stopwatch.StartNew();
            try
            {
                PhaseFiveCompression.CompactGapsForTest(tasks, request, constraints);
            }
            finally
            {
                sw.Stop();
                Environment.SetEnvironmentVariable("APS_PHASE5_LEGACY_RESOURCE_SCAN", null);
            }

            elapsedMs = sw.ElapsedMilliseconds;
            return Fingerprint(tasks);
        }

        var legacyPrint = RunOnce(true, out var legacyMs);
        var packedPrint = RunOnce(false, out var packedMs);

        // 耗时读数必须在**通过**时也能看到（断言消息只在失败时显示）⇒ 走 ITestOutputHelper
        _output.WriteLine($"扫描等价性：Task {tasks.Count} 条；原对象扫描 {legacyMs}ms → 连续数组 {packedMs}ms");

        Assert.True(
            string.Equals(legacyPrint, packedPrint, StringComparison.Ordinal),
            $"两种扫描方式结果不同（Task {tasks.Count} 条）：原对象扫描 {legacyMs}ms，连续数组 {packedMs}ms。"
            + Environment.NewLine
            + "首个差异行："
            + Environment.NewLine
            + FirstDiff(legacyPrint, packedPrint));

        // 顺带把提速读数带出来（不做阈值断言 —— 机器噪声下不设门槛）
        Assert.True(true, $"原对象扫描 {legacyMs}ms → 连续数组 {packedMs}ms（Task {tasks.Count} 条）");
    }

    private static string FirstDiff(string a, string b)
    {
        var la = a.Split('\n');
        var lb = b.Split('\n');
        for (var i = 0; i < Math.Min(la.Length, lb.Length); i++)
        {
            if (!string.Equals(la[i], lb[i], StringComparison.Ordinal))
            {
                return $"第 {i + 1} 行：A={la[i]} / B={lb[i]}";
            }
        }

        return la.Length == lb.Length ? "（无差异）" : $"行数不同：A={la.Length} / B={lb.Length}";
    }

    /// <summary>
    /// 重建 `ConstraintContext`：Phase1 重建 + **把 Phase2/Phase4 运行期登记的六张表写回去**。
    ///
    /// 只调 Phase1 是不够的 —— `ResolvedDirections`/`ResolvedBatchDirections`（压实门控）、
    /// `ChosenRoutePaths`/`ChosenBatchRoutePaths`（取图链）、`ExecutionBatchDraftKeys`/`ExecutionBatchPlans`
    /// 全是 **Phase2 跑的时候才写进去的**，`PhaseOneConstraintBuilder` 不含它们。漏了这六张表，
    /// 重放建出来的依赖边就可能和生产不一样，结论也就不成立。
    /// `context.json` 缺失 ⇒ 退回「只有 Phase1」的老口径，并在输出里明说。
    /// </summary>
    private ConstraintContext BuildConstraints(DomainSolveRequest request, string dir)
    {
        var constraints = PhaseFiveCompression.BuildConstraintsForTest(request);

        var contextPath = Path.Combine(dir, "context.json");
        if (!File.Exists(contextPath))
        {
            _output.WriteLine($"⚠ 快照缺 context.json（{contextPath}）⇒ 本次重放**只含 Phase1 上下文**，方向/取图口径可能与生产不符。");
            return constraints;
        }

        var snapshot = Deserialize<PhaseFiveCompression.Phase5ContextSnapshot>(contextPath);
        PhaseFiveCompression.ApplyContextSnapshotForTest(constraints, snapshot);

        _output.WriteLine(
            $"上下文补丁：方向 {snapshot.ResolvedDirections.Count} 条 / 批方向 {snapshot.ResolvedBatchDirections.Count} 条 / "
            + $"需求路径 {snapshot.ChosenRoutePaths.Count} 条 / 批路径 {snapshot.ChosenBatchRoutePaths.Count} 条 / "
            + $"批键表 {snapshot.ExecutionBatchDraftKeys.Count} 条 / 批计划 {snapshot.ExecutionBatchPlans.Count} 条");

        return constraints;
    }

    /// <summary>
    /// **定点探针**：查一对具体需求的跨物料父子关系在 `ConstraintContext` 里到底在不在。
    ///
    /// 为什么需要：2026-10-28 全量重放证明「压实前就已越序」，且越序的那对是
    ///   上游 `190_14375`（M102 子件）、下游 `190_14374`（M201 父件）——
    ///   两个**不同需求**，所以边只能来自 `GenerateCrossMaterialDependencies`（跨物料血缘边）。
    /// Phase2 对父件设的动态物料下界（`GetDynamicMaterialFloor`）与这条边**同源**
    ///   （都读 `MaterialRequirementLinks`），所以只要 Phase2 真的算过下界，
    ///   父件就不可能被排到子件完工之前。⇒ 先查表，判断是「表里没有」还是「表里有但没生效」。
    ///
    /// 设 `APS_PHASE5_PAIR_PROBE=<父需求键>` 才执行；子件键由 `APS_PHASE5_PAIR_PROBE_CHILD` 给。
    /// 只读 `request.json`（不读 952 MB 的 tasks.json），一轮约 1~2 分钟。
    /// </summary>
    [Fact]
    public void 跨物料父子表是否覆盖指定的一对需求()
    {
        var dir = Environment.GetEnvironmentVariable(EnvSnapshotDir);
        var parentKey = Environment.GetEnvironmentVariable("APS_PHASE5_PAIR_PROBE");
        if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(parentKey))
        {
            return;   // 未执行（见类注释）
        }

        var childKey = Environment.GetEnvironmentVariable("APS_PHASE5_PAIR_PROBE_CHILD") ?? string.Empty;

        var request = Deserialize<DomainSolveRequest>(Path.Combine(dir, "request.json"));
        var constraints = PhaseFiveCompression.BuildConstraintsForTest(request);

        // ① 运行期表（Phase2/Phase4 取下界用的那张）
        var inTable = constraints.CrossMaterialParentToChildren.TryGetValue(parentKey, out var children)
            ? children
            : new List<string>();

        // ② 原始输入（Phase5 建边用的那个源）
        var links = request.MaterialRequirementLinks ?? new List<MaterialRequirementLink>();
        var hitLinks = links
            .Where(l => l.ConsumerLogicalDemandKey == parentKey
                        && (string.IsNullOrEmpty(childKey) || l.ProducerLogicalDemandKey == childKey))
            .ToList();

        // ③ 拓扑序里的位置（Phase2 按此顺序排，子先父后）
        var order = constraints.CrossMaterialOrder;
        var parentPos = order.IndexOf(parentKey);
        var childPos = string.IsNullOrEmpty(childKey) ? -1 : order.IndexOf(childKey);

        _output.WriteLine($"父需求 {parentKey} 的直接子件数 = {inTable.Count}；子件键 {childKey} 在表内 = {inTable.Contains(childKey)}");
        _output.WriteLine($"原始 MaterialRequirementLinks 命中 = {hitLinks.Count} 条："
            + string.Join("；", hitLinks.Select(l =>
            {
                var lag = constraints.CrossMaterialLagMinutes.TryGetValue(
                    (l.ConsumerLogicalDemandKey, l.ProducerLogicalDemandKey ?? string.Empty), out var lg) ? lg : (decimal?)null;
                return $"生产者={l.ProducerLogicalDemandKey ?? "null"} 消费量={l.RequiredQty} Lag={lag?.ToString() ?? "未登记"}";
            })));
        _output.WriteLine($"CrossMaterialOrder 长度 = {order.Count}；父件位置 = {parentPos}；子件位置 = {childPos}；有环 = {constraints.CrossMaterialHasCycle}");
        _output.WriteLine($"表内全部子件：{string.Join(",", inTable)}");
    }

    /// <summary>
    /// **落盘顺序指纹**：`tasks.json` = `ScheduledTasks`（Phase2）在前 + `RepairedTasks`（Phase4）在后，
    /// 中间那个分界点就是我们要找的「Phase2 / Phase4 分界线」。
    ///
    /// 判据：Phase2 按 `CrossMaterialOrder` 顺序逐需求排、逐需求追加 ⇒ 前段需求在拓扑序里的位置**严格递增**；
    /// Phase4 按修复单元（`affectedDemands` + 未排需求）走，**不保证递增** ⇒ 第一次「位置回退」就是分界。
    ///
    /// 设 `APS_PHASE5_ORDER_PROBE=1` 才执行。只流式扫 `tasks.json` 抽 `SourceDraftId`（不整体反序列化），
    /// 约 1 分钟。`APS_PHASE5_ORDER_PROBE_KEYS` 可给逗号分隔的需求键，额外报它们落在分界的哪一侧。
    /// </summary>
    [Fact]
    public void 落盘顺序指纹_定位Phase2与Phase4分界()
    {
        var dir = Environment.GetEnvironmentVariable(EnvSnapshotDir);
        if (string.IsNullOrWhiteSpace(dir)
            || Environment.GetEnvironmentVariable("APS_PHASE5_ORDER_PROBE") != "1")
        {
            return;   // 未执行（见类注释）
        }

        var request = Deserialize<DomainSolveRequest>(Path.Combine(dir, "request.json"));
        var constraints = PhaseFiveCompression.BuildConstraintsForTest(request);

        var position = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < constraints.CrossMaterialOrder.Count; i++)
        {
            position[constraints.CrossMaterialOrder[i]] = i;
        }

        // ── 流式抽 `SourceDraftId`，按出现顺序去连续重复 ──
        const string marker = "\"SourceDraftId\":\"";
        var sequence = new List<string>();
        var lastKey = string.Empty;
        var sb = new StringBuilder();
        var matchPos = 0;

        using (var reader = new StreamReader(
                   Path.Combine(dir, "tasks.json"), Encoding.UTF8, false, 1 << 20))
        {
            var chunk = new char[1 << 16];
            int read;
            while ((read = reader.Read(chunk, 0, chunk.Length)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    var c = chunk[i];
                    if (matchPos < marker.Length)
                    {
                        if (c == marker[matchPos]) { matchPos++; }
                        else { matchPos = c == marker[0] ? 1 : 0; }
                    }
                    else if (c == '"')
                    {
                        var key = sb.ToString();
                        sb.Clear();
                        matchPos = 0;
                        if (!string.Equals(key, lastKey, StringComparison.Ordinal))
                        {
                            sequence.Add(key);
                            lastKey = key;
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
            }
        }

        // ── 找第一次位置回退 ──
        var descents = new List<int>();
        for (var i = 1; i < sequence.Count; i++)
        {
            var prev = position.TryGetValue(sequence[i - 1], out var pPrev) ? pPrev : int.MinValue;
            var cur = position.TryGetValue(sequence[i], out var pCur) ? pCur : int.MinValue;
            if (cur >= 0 && prev >= 0 && cur < prev)
            {
                descents.Add(i);
            }
        }

        _output.WriteLine($"落盘需求段数 = {sequence.Count}；拓扑序长度 = {constraints.CrossMaterialOrder.Count}；"
            + $"位置回退次数 = {descents.Count}");

        for (var k = 0; k < Math.Min(5, descents.Count); k++)
        {
            var i = descents[k];
            var a = position[sequence[i - 1]];
            var b = position[sequence[i]];
            _output.WriteLine($"  第 {k + 1} 次回退在段 {i}/{sequence.Count}：{sequence[i - 1]}({a}) → {sequence[i]}({b})");
        }

        var probeKeys = (Environment.GetEnvironmentVariable("APS_PHASE5_ORDER_PROBE_KEYS") ?? string.Empty)
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var key in probeKeys)
        {
            var idx = sequence.IndexOf(key);
            var pos = position.TryGetValue(key, out var p) ? p : -1;
            var firstDescent = descents.Count > 0 ? descents[0] : -1;
            var side = idx < 0 ? "不在文件里" : idx < firstDescent ? "Phase2 段" : "Phase4 段";
            _output.WriteLine($"  键 {key}：落盘段序 {idx}，拓扑序 {pos}，位于 {side}（首次回退在段 {firstDescent}）");
        }
    }

    /// <summary>
    /// **跨物料下界越界统计**（不建依赖边、只按 `CrossMaterialParentToChildren` 逐需求对账）：
    ///   对每个父需求，`floor = max(直接子件已排最晚完工 + Lag)`，
    ///   父需求所有 Task 里最早的那个 `PlannedStartTime` 若早于 `floor` ⇒ 记一条越界。
    ///
    /// 用途：`CheckTaskDependencies` 只报**第一条**就越界返回，看不出规模。
    ///   规模决定修法：几条 ⇒ 定点补；成片 ⇒ 下界在某条路径上整体没生效。
    ///
    /// 设 `APS_PHASE5_FLOOR_AUDIT=1` 才执行。读 `request.json` + `tasks.json`，约 2 分钟。
    /// </summary>
    [Fact]
    public void 跨物料下界越界统计()
    {
        var dir = Environment.GetEnvironmentVariable(EnvSnapshotDir);
        if (string.IsNullOrWhiteSpace(dir)
            || Environment.GetEnvironmentVariable("APS_PHASE5_FLOOR_AUDIT") != "1")
        {
            return;   // 未执行（见类注释）
        }

        var request = Deserialize<DomainSolveRequest>(Path.Combine(dir, "request.json"));
        var tasks = Deserialize<List<FinalTaskDraft>>(Path.Combine(dir, "tasks.json"));
        var constraints = PhaseFiveCompression.BuildConstraintsForTest(request);

        var byDemand = tasks
            .GroupBy(t => t.SourceDraftId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (MinStart: g.Min(t => t.PlannedStartTime), MaxEnd: g.Max(t => t.PlannedEndTime), Count: g.Count()),
                StringComparer.Ordinal);

        var parents = 0;
        var judged = 0;
        var childMissing = 0;
        var violations = new List<(string Parent, TimeSpan Late, int TaskCount, string Child)>();
        var checkedPairs = 0;

        foreach (var kv in constraints.CrossMaterialParentToChildren)
        {
            parents++;
            if (!byDemand.TryGetValue(kv.Key, out var parentTask))
            {
                continue;   // 父需求未排成
            }

            var floor = DateTime.MinValue;
            var worstChild = string.Empty;
            var missing = false;
            foreach (var childKey in kv.Value)
            {
                checkedPairs++;
                if (!byDemand.TryGetValue(childKey, out var childTask))
                {
                    missing = true;
                    continue;
                }

                var childEnd = childTask.MaxEnd;
                if (constraints.CrossMaterialLagMinutes.TryGetValue((kv.Key, childKey), out var lag))
                {
                    childEnd = childEnd.AddMinutes((double)lag);
                }

                if (childEnd > floor) { floor = childEnd; worstChild = childKey; }
            }

            if (missing) { childMissing++; }

            if (floor == DateTime.MinValue)
            {
                continue;
            }

            judged++;
            if (parentTask.MinStart < floor)
            {
                violations.Add((kv.Key, floor - parentTask.MinStart, parentTask.Count, worstChild));
            }
        }

        var ordered = violations.OrderByDescending(v => v.Late).ToList();
        _output.WriteLine($"跨物料父需求 {parents} 个；父件已排成 {judged} 个（其中有子件未排成的 {childMissing} 个）；"
            + $"子件边核对 {checkedPairs} 对；**越界父需求 {violations.Count} 个**");

        for (var i = 0; i < Math.Min(8, ordered.Count); i++)
        {
            var v = ordered[i];
            constraints.ResolvedDirections.TryGetValue(v.Parent, out var parentDirection);
            _output.WriteLine($"  越界 #{i + 1}：父 {v.Parent}（{v.TaskCount} 条 Task）比下界早 {v.Late.TotalMinutes:F1} 分钟；下界来自子件 {v.Child}；方向={parentDirection ?? "(无)"}");

            // 越界的是**哪一条** Task？决定「非资源工序未被下界钳住」还是「整条需求都没吃到下界」。
            var parentTasks = tasks
                .Where(t => string.Equals(t.SourceDraftId, v.Parent, StringComparison.Ordinal))
                .OrderBy(t => t.PlannedStartTime)
                .ToList();
            foreach (var pt in parentTasks)
            {
                _output.WriteLine(
                    $"      {(pt.PlannedStartTime == parentTasks[0].PlannedStartTime ? "*" : " ")} "
                    + $"{pt.StageCode}/{pt.OperationCode} 资源={pt.ResourceId?.ToString() ?? "null"}({pt.ResourceCode}) "
                    + $"{pt.PlannedStartTime:yyyy-MM-dd HH:mm} → {pt.PlannedEndTime:yyyy-MM-dd HH:mm} "
                    + $"类型={pt.TaskType}");
            }
        }

        // ── 有 `floor_trace.txt` 时逐条对账（决定「算错 / 没传 / 被顶掉」）──
        //   · `P2_DONE|子件`  <  子件 Task 真实最晚完工 ⇒ **下界本身算低了**（demandCompletion 少算）
        //   · `P2_FLOOR|父件` <  `P2_DONE|子件`      ⇒ **算出来了但没传进父件**
        //   · `P2_FLOOR|父件` ≥  `P2_DONE|子件` 且 父件 MinStart < floor ⇒ **传到了但被别的路径顶掉**
        var tracePath = Path.Combine(dir, "floor_trace.txt");
        if (!File.Exists(tracePath))
        {
            _output.WriteLine("（快照里没有 floor_trace.txt ⇒ 未做「算错/没传/被顶掉」对账）");
            return;
        }

        var trace = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(tracePath))
        {
            var parts = line.Split('|');
            if (parts.Length < 3) { continue; }
            trace[parts[0] + "|" + parts[1]] = parts[2] + (parts.Length > 3 ? "|" + parts[3] : string.Empty);
        }

        _output.WriteLine($"—— 逐条对账（trace 行 {trace.Count}）——");
        foreach (var v in ordered)
        {
            trace.TryGetValue("P2_FLOOR|" + v.Parent, out var parentFloor);
            trace.TryGetValue("P4_FLOOR|" + v.Parent, out var parentFloorP4);
            trace.TryGetValue("P2_DONE|" + v.Child, out var childDoneP2);
            trace.TryGetValue("P4_DONE|" + v.Child, out var childDoneP4);
            var childTruth = byDemand.TryGetValue(v.Child, out var ct) ? ct.MaxEnd : DateTime.MinValue;
            var parentTruth = byDemand.TryGetValue(v.Parent, out var pt) ? pt.MinStart : DateTime.MinValue;

            _output.WriteLine($"  父 {v.Parent}：P2_FLOOR={parentFloor ?? "(无)"}｜P4_FLOOR={parentFloorP4 ?? "(无)"}");
            _output.WriteLine($"      子 {v.Child}：P2_DONE={childDoneP2 ?? "(无)"}｜P4_DONE={childDoneP4 ?? "(无)"}｜Task真实最晚完工={childTruth:yyyy-MM-dd HH:mm:ss.fff}");
            _output.WriteLine($"      父件 Task 最早开工={parentTruth:yyyy-MM-dd HH:mm:ss.fff}（早于真实下界 {v.Late.TotalMinutes:F1} 分钟）");
        }
    }

    [Fact]
    public void 重放Phase5_压实后TaskDependency不得越序()
    {
        var dir = Environment.GetEnvironmentVariable(EnvSnapshotDir);
        if (string.IsNullOrWhiteSpace(dir))
        {
            // 没有快照 ⇒ 本测试**未执行**（见类注释）。
            return;
        }

        var requestPath = Path.Combine(dir, "request.json");
        var tasksPath = Path.Combine(dir, "tasks.json");
        Assert.True(File.Exists(requestPath), $"快照缺 request.json：{requestPath}");
        Assert.True(File.Exists(tasksPath), $"快照缺 tasks.json：{tasksPath}");

        var request = Deserialize<DomainSolveRequest>(requestPath);
        var tasks = Deserialize<List<FinalTaskDraft>>(tasksPath);

        // ── 切片（可选）：按需求键切 / 按条数截断，见 `ApplySlice` ──
        //   按 `SourceDraftId` 切（Phase5 里 `demandByKey` 的键就是 `LogicalDemandKey`，
        //   而 `CompactGaps` 用 `task.SourceDraftId` 去查它 ⇒ 两者同值域）。
        tasks = ApplySlice(tasks);

        Assert.NotEmpty(tasks);

        var swBuild = Stopwatch.StartNew();
        var constraints = BuildConstraints(request, dir);
        swBuild.Stop();

        var compressor = new PhaseFiveCompression();

        // ── 压实**前**基线 ──
        //   如果这里就已经越序，说明违规不是压实引入的，而是 Phase2/Phase4 交过来的
        //   ⇒ 归因完全不同（不许把账记到 Phase5 头上）。
        var beforeDeps = compressor.GenerateTaskDependenciesForTest(tasks, request, constraints);
        var beforeViolation = PhaseFiveCompression.CheckTaskDependenciesForTest(tasks, beforeDeps);

        var startBefore = tasks.ToDictionary(t => t.FinalDraftId, t => t.PlannedStartTime);

        // ── 生产同序：先压实，再段内 Setup 序列优化（`Compress` 里两者都在硬校验之前）──
        var swCompact = Stopwatch.StartNew();
        PhaseFiveCompression.CompactGapsForTest(tasks, request, constraints);
        PhaseFiveCompression.OptimizeSetupSequencesForTest(tasks, request, constraints);
        swCompact.Stop();

        var moved = tasks.Count(t =>
            startBefore.TryGetValue(t.FinalDraftId, out var s) && s != t.PlannedStartTime);

        // ── 压实后按生产口径重新建边并校验 ──
        var afterDeps = compressor.GenerateTaskDependenciesForTest(tasks, request, constraints);
        var afterViolation = PhaseFiveCompression.CheckTaskDependenciesForTest(tasks, afterDeps);

        _output.WriteLine(
            $"重放读数：Task {tasks.Count} 条（移动 {moved} 条）；"
            + $"依赖边 压实前 {beforeDeps.Count} → 压实后 {afterDeps.Count}；"
            + $"BuildConstraints {swBuild.ElapsedMilliseconds}ms，CompactGaps+SetupSeq {swCompact.ElapsedMilliseconds}ms；"
            + $"压实后越序：{afterViolation ?? "无"}");

        Assert.True(
            afterViolation == null,
            $"压实后出现 TaskDependency 越序：{Environment.NewLine}"
            + $"  {afterViolation}{Environment.NewLine}"
            + $"压实前基线：{beforeViolation ?? "无越序"}{Environment.NewLine}"
            + $"统计：Task {tasks.Count} 条（移动 {moved} 条）；依赖边 压实前 {beforeDeps.Count} → 压实后 {afterDeps.Count}；"
            + $"BuildConstraints {swBuild.ElapsedMilliseconds}ms，CompactGaps {swCompact.ElapsedMilliseconds}ms");
    }
}
