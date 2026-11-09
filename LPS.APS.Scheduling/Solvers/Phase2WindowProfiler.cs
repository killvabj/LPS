using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace LPS.APS.Scheduling.Solvers;

/// <summary>
/// 【Step 1 · 低频分桶画像】**纯观测**，默认关闭，开启后每 N 条需求落一行 CSV。
///
/// <para><b>为什么要有它</b>：此前只能给出「batch_trial 占 40%」这类聚合百分比，
/// 回答不了「为什么第 10 分钟每条需求的成本比第 1 分钟高 7 倍」。
/// 本类按**已处理需求数**等距分桶（缺省 5000 条一桶），把每桶的墙钟、分配量、GC、
/// 以及各热点计数落成表 ⇒ 可直接画「单桶耗时 vs 当时任务表长度」的曲线。</para>
///
/// <para><b>零成本契约</b>：`APS_P2_BUCKET_CSV` 未设置时，静态构造只做一次环境变量读取，
/// `Tick` 第一行即返回 —— 不分配、不读时钟、不访问计数器。</para>
///
/// <para><b>不改任何判定</b>：本类只读 <see cref="SolverDiagnostics.LiveCounters"/> 与
/// <c>GC</c> 统计，从不写它们，也从不参与任何分支。</para>
///
/// <para><b>开门</b>：</para>
/// <code>APS_P2_BUCKET_CSV=D:\tmp\p2buckets.csv dotnet test ... --filter FullyQualifiedName~SolverReplayTest</code>
/// 可选：`APS_P2_BUCKET_SIZE=5000` 改桶宽。
/// </summary>
internal static class Phase2WindowProfiler
{
    private const string PathEnvVar = "APS_P2_BUCKET_CSV";
    private const string SizeEnvVar = "APS_P2_BUCKET_SIZE";

    private static readonly bool Enabled;
    private static readonly int WindowSize;
    private static readonly string? CsvPath;

    private static StreamWriter? _writer;
    private static bool _headerWritten;

    private static long _lastTs;
    private static int _lastProcessed;
    private static long _lastAllocated;
    private static int _lastGc2;
    private static long _lastRunBatchPlan;
    private static long _lastBatchTrial;
    private static long _lastCloneTask;
    private static long _lastCloneOcc;
    private static long _lastMergeScan;
    private static long _lastSlotSearch;
    private static double _lastFindFwdMs;
    private static double _lastLandingMs;
    private static double _lastSchedOpsMs;
    private static double _lastHasConflictMs;
    private static double _lastOccInsertMs;
    private static double _lastTimelineMs;
    private static long _lastTimelinePlaceCalls;
    private static long _lastTimelineCompactRuns;
    private static readonly long[] _lastPlaceBySource = new long[8];
    private static int _windowIndex;

    // ── 候选形状直方图（Q4 / Step 1）：候选数 0 / 1 / 2 / 3 / 4+ ──
    private static readonly long[] CandidateHistogram = new long[5];
    private static long _candidateSamples;

    static Phase2WindowProfiler()
    {
        var path = Environment.GetEnvironmentVariable(PathEnvVar);
        Enabled = !string.IsNullOrWhiteSpace(path);
        CsvPath = path;

        var raw = Environment.GetEnvironmentVariable(SizeEnvVar);
        WindowSize = int.TryParse(raw, out var size) && size > 0 ? size : 5000;
    }

    /// <summary>
    /// 登记一次「候选枚举结果」。**只累加计数**，不做任何写盘 —— 直方图随每个桶行一起落盘，
    /// 因此最后一行携带的是全量累计值（无需额外的收尾钩子）。
    /// </summary>
    public static void RecordCandidateShape(int candidateCount)
    {
        if (!Enabled)
        {
            return;
        }

        var idx = candidateCount <= 0 ? 0 : (candidateCount >= 4 ? 4 : candidateCount);
        Interlocked.Increment(ref CandidateHistogram[idx]);
        Interlocked.Increment(ref _candidateSamples);
    }

    /// <summary>
    /// Phase2 主循环每处理一条需求调一次。未到桶宽时**第二行即返回**（只做一次减法比较）。
    /// </summary>
    public static void Tick(int processed)
    {
        if (!Enabled || processed - _lastProcessed < WindowSize)
        {
            return;
        }

        try
        {
            var counters = SolverDiagnostics.LiveCounters;
            if (counters is null)
            {
                return;
            }

            var nowTs = System.Diagnostics.Stopwatch.GetTimestamp();
            var wallMs = _lastTs == 0L
                ? 0L
                : (long)((nowTs - _lastTs) * 1000d / System.Diagnostics.Stopwatch.Frequency);

            var allocated = GC.GetTotalAllocatedBytes(precise: false);
            var gc2 = GC.CollectionCount(2);

            var allocMb = _lastAllocated == 0L ? 0L : (allocated - _lastAllocated) / (1024L * 1024L);
            var gc2Delta = _lastGc2 == 0 ? 0 : gc2 - _lastGc2;

            var runBatchPlan = counters.RunBatchPlanInvocations;
            var batchTrial = counters.BatchPlanCandidateTrials;
            var cloneTask = counters.CloneTaskList;
            var cloneOcc = counters.CloneOccupancy;
            var mergeScan = counters.MergeScanCalls;
            var slotSearch = counters.SlotSearches;

            // 每桶增量：把「同样的活变贵了」钉到具体子阶段（累计值做差）
            var findFwdMs = counters.HotspotMs(SolverDiagnostics.Hotspot.FindForwardSlot);
            var landingMs = counters.HotspotMs(SolverDiagnostics.Hotspot.LandingRun);
            var schedOpsMs = counters.HotspotMs(SolverDiagnostics.Hotspot.ScheduleDemandOps);
            var hasConflictMs = counters.HotspotMs(SolverDiagnostics.Hotspot.HasConflict);
            var occInsertMs = counters.HotspotMs(SolverDiagnostics.Hotspot.OccInsert);
            var timelineMs = counters.HotspotMs(SolverDiagnostics.Hotspot.TimelinePlace);
            var timelinePlaceCalls = counters.TimelinePlaceCalls;
            var timelineCompactRuns = counters.TimelineCompactRuns;

            var pbs = counters.TimelinePlaceBySource;
            var pbs0 = pbs[0] - _lastPlaceBySource[0];   // Unknown
            var pbs1 = pbs[1] - _lastPlaceBySource[1];   // Phase2ScheduleA
            var pbs2 = pbs[2] - _lastPlaceBySource[2];   // Phase2ScheduleB
            var pbs3 = pbs[3] - _lastPlaceBySource[3];   // Phase2MergeDetach
            var pbs4 = pbs[4] - _lastPlaceBySource[4];   // Phase2Rebuild

            // 合批擂台按 PI 分类（累计值，最后一行的值即为全量）
            var mps = counters.MergePiStats;

            var row = string.Join(",",
                (_windowIndex + 1).ToString(CultureInfo.InvariantCulture),
                processed.ToString(CultureInfo.InvariantCulture),
                wallMs.ToString(CultureInfo.InvariantCulture),
                allocMb.ToString(CultureInfo.InvariantCulture),
                gc2Delta.ToString(CultureInfo.InvariantCulture),
                counters.CloneTaskListLenMax.ToString(CultureInfo.InvariantCulture),
                counters.CloneOccupancyKeyMax.ToString(CultureInfo.InvariantCulture),
                (runBatchPlan - _lastRunBatchPlan).ToString(CultureInfo.InvariantCulture),
                (batchTrial - _lastBatchTrial).ToString(CultureInfo.InvariantCulture),
                (cloneTask - _lastCloneTask).ToString(CultureInfo.InvariantCulture),
                counters.HotspotMs(SolverDiagnostics.Hotspot.CloneTaskList).ToString(CultureInfo.InvariantCulture),
                (cloneOcc - _lastCloneOcc).ToString(CultureInfo.InvariantCulture),
                counters.HotspotMs(SolverDiagnostics.Hotspot.CloneOccupancy).ToString(CultureInfo.InvariantCulture),
                (mergeScan - _lastMergeScan).ToString(CultureInfo.InvariantCulture),
                (slotSearch - _lastSlotSearch).ToString(CultureInfo.InvariantCulture),
                (findFwdMs - _lastFindFwdMs).ToString(CultureInfo.InvariantCulture),
                (landingMs - _lastLandingMs).ToString(CultureInfo.InvariantCulture),
                (schedOpsMs - _lastSchedOpsMs).ToString(CultureInfo.InvariantCulture),
                (hasConflictMs - _lastHasConflictMs).ToString(CultureInfo.InvariantCulture),
                (occInsertMs - _lastOccInsertMs).ToString(CultureInfo.InvariantCulture),
                (timelineMs - _lastTimelineMs).ToString(CultureInfo.InvariantCulture),
                (timelinePlaceCalls - _lastTimelinePlaceCalls).ToString(CultureInfo.InvariantCulture),
                (timelineCompactRuns - _lastTimelineCompactRuns).ToString(CultureInfo.InvariantCulture),
                pbs0.ToString(CultureInfo.InvariantCulture),
                pbs1.ToString(CultureInfo.InvariantCulture),
                pbs2.ToString(CultureInfo.InvariantCulture),
                pbs3.ToString(CultureInfo.InvariantCulture),
                pbs4.ToString(CultureInfo.InvariantCulture),
                mps[0].ToString(CultureInfo.InvariantCulture),   // 同PI 锚批
                mps[1].ToString(CultureInfo.InvariantCulture),   // 同PI 试排
                mps[2].ToString(CultureInfo.InvariantCulture),   // 同PI ticks
                mps[3].ToString(CultureInfo.InvariantCulture),   // 同PI 胜出
                mps[4].ToString(CultureInfo.InvariantCulture),   // 跨PI 锚批
                mps[5].ToString(CultureInfo.InvariantCulture),   // 跨PI 试排
                mps[6].ToString(CultureInfo.InvariantCulture),   // 跨PI ticks
                mps[7].ToString(CultureInfo.InvariantCulture),   // 跨PI 胜出
                mps[8].ToString(CultureInfo.InvariantCulture),   // PI缺失 锚批
                mps[9].ToString(CultureInfo.InvariantCulture),   // PI缺失 试排
                mps[10].ToString(CultureInfo.InvariantCulture),  // PI缺失 ticks
                mps[11].ToString(CultureInfo.InvariantCulture),  // PI缺失 胜出
                Interlocked.Read(ref CandidateHistogram[0]).ToString(CultureInfo.InvariantCulture),
                Interlocked.Read(ref CandidateHistogram[1]).ToString(CultureInfo.InvariantCulture),
                Interlocked.Read(ref CandidateHistogram[2]).ToString(CultureInfo.InvariantCulture),
                Interlocked.Read(ref CandidateHistogram[3]).ToString(CultureInfo.InvariantCulture),
                Interlocked.Read(ref CandidateHistogram[4]).ToString(CultureInfo.InvariantCulture),
                Interlocked.Read(ref _candidateSamples).ToString(CultureInfo.InvariantCulture));

            WriteRow(row);

            _lastProcessed = processed;
            _lastTs = nowTs;
            _lastAllocated = allocated;
            _lastGc2 = gc2;
            _lastRunBatchPlan = runBatchPlan;
            _lastBatchTrial = batchTrial;
            _lastCloneTask = cloneTask;
            _lastCloneOcc = cloneOcc;
            _lastMergeScan = mergeScan;
            _lastSlotSearch = slotSearch;
            _lastFindFwdMs = findFwdMs;
            _lastLandingMs = landingMs;
            _lastSchedOpsMs = schedOpsMs;
            _lastHasConflictMs = hasConflictMs;
            _lastOccInsertMs = occInsertMs;
            _lastTimelineMs = timelineMs;
            _lastTimelinePlaceCalls = timelinePlaceCalls;
            _lastTimelineCompactRuns = timelineCompactRuns;
            for (var i = 0; i < _lastPlaceBySource.Length && i < pbs.Length; i++)
            {
                _lastPlaceBySource[i] = pbs[i];
            }
            _windowIndex++;
        }
        catch
        {
            // 观测失败绝不能影响跑批 —— 与 `SolverDiagnostics` 同口径。
        }
    }

    private static void WriteRow(string row)
    {
        if (_writer is null)
        {
            if (string.IsNullOrWhiteSpace(CsvPath))
            {
                return;
            }

            var dir = Path.GetDirectoryName(CsvPath!);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            _writer = new StreamWriter(CsvPath!, append: false, Encoding.UTF8) { AutoFlush = true };
        }

        if (!_headerWritten)
        {
            _writer.WriteLine(
                "window,processed,wall_ms,alloc_mb,gc2,task_len_max,occ_key_max,"
                + "run_batch_plan,batch_trial,clone_task_n,clone_task_ms,clone_occ_n,clone_occ_ms,"
                + "merge_scan,slot_search,find_fwd_ms,landing_ms,sched_ops_ms,has_conflict_ms,occ_insert_ms,timeline_ms,"
                + "timeline_place_n,timeline_compact_n,"
                + "place_unknown,place_schedA,place_schedB,place_merge,place_rebuild,"
                + "pi_same_anchor,pi_same_trial,pi_same_ticks,pi_same_adopt,"
                + "pi_cross_anchor,pi_cross_trial,pi_cross_ticks,pi_cross_adopt,"
                + "pi_miss_anchor,pi_miss_trial,pi_miss_ticks,pi_miss_adopt,"
                + "cand0,cand1,cand2,cand3,cand4plus,cand_samples");
            _headerWritten = true;
        }

        _writer.WriteLine(row);
    }
}