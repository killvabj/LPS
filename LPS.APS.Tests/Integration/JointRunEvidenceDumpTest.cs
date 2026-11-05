using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using Xunit;
using Xunit.Abstractions;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// 【1号位 · 联验取证一条命令】吃一份**离线捕获的 `DomainSolveRequest` JSON**，
/// 纯内存重跑 Phase1~5，把 1号位 该交的三类证据<b>一次性写成文件</b>：
/// 未排需求键清单 / 求解器消费侧计数 / 诊断快照。
///
/// <para><b>为什么单开一个入口，不去改 `SolverReplayTest`</b>：那个入口的职责是「比快慢、看热点」，
/// 断言 `Success == true` 才算过。取证恰好相反 —— **`Success == false` 也是证据，不许被断言挡掉**。
/// 两者混在一起，跑批时就会既不敢断言又不敢不断言。</para>
///
/// <para><b>零读库</b>：只吃 JSON 文件，不连任何数据库、不碰 `RunPeggingOnlyAsync`、不起生产 Run。
/// 与《2026-11-02 回七节四条》§3.3「优先走第 1 条（完全不碰库）」对应。</para>
///
/// <para><b>开门</b>（不设变量则**跳过**，仓库里不硬编码本机路径）：</para>
/// <code>
/// APS_REPLAY_SOLVE_REQUEST=D:\tmp\PV190_solverequest_01.json \
/// APS_EVIDENCE_OUT=D:\tmp\evidence_20261104 \
/// dotnet test LPS.APS.Tests/LPS.APS.Tests.csproj -c Release --filter FullyQualifiedName~JointRunEvidenceDumpTest
/// </code>
///
/// <para><b>产出三件</b>（都落在 <c>APS_EVIDENCE_OUT</c>；不设则落 <c>%TEMP%\aps_evidence_&lt;时刻&gt;</c>）：</para>
/// <list type="bullet">
///   <item><c>evidence.md</c> —— 人读的全套：输入身份 / A-6 三件套 / 未排清单 / 硬失败三档 / 分段耗时 / 快照；</item>
///   <item><c>unscheduled.csv</c> —— 未排明细，逐行 <c>需求键,原因,未排量</c>，可直接进对账表；</item>
///   <item><c>evidence.json</c> —— 机读汇总（同 md 的数字部分），给 0号位 汇总用。</item>
/// </list>
///
/// <para><b>⚠ 保真度边界（必须随证据一起交，别把重放结果当全流程结果）</b>：
/// 捕获侧序列化器是 <c>ReferenceHandler.IgnoreCycles</c>，图状 DTO 上被判成环的那条引用写成 <c>null</c>，
/// 反序列化后环恢复不了、共享对象会变多份副本。⇒ 本入口的产物可用于
/// 「同一份 JSON 前后自比」「看未排键与原因分布」，**不可**用于「与全流程 FinalTask 逐位比对」。
/// md 头部会把这句话原样写进去，免得证据离开上下文之后被人当基准表。</para>
///
/// <para><b>不改生产代码</b>：只 <c>new FiniteCapacitySolver()</c> 调公开接口，`LPS.APS.Scheduling` 一行未改。</para>
/// </summary>
public class JointRunEvidenceDumpTest
{
    private const string ReplayPathEnvVar = "APS_REPLAY_SOLVE_REQUEST";
    private const string EvidenceOutEnvVar = "APS_EVIDENCE_OUT";

    private readonly ITestOutputHelper _output;

    public JointRunEvidenceDumpTest(ITestOutputHelper output) => _output = output;

    [SkippableFact(DisplayName = "1号位: 联验取证一条命令（离线 JSON → 全套证据文件，零读库）")]
    public async Task DumpJointRunEvidenceAsync()
    {
        var jsonPath = Environment.GetEnvironmentVariable(ReplayPathEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(jsonPath) || !File.Exists(jsonPath),
            $"未指定已存在的入参 JSON（{ReplayPathEnvVar}='{jsonPath ?? "(null)"}'）。"
            + " 先用 APS_CAPTURE_SOLVE_REQUEST=1 跑一次全流程把入参捕获下来。");

        var outDir = ResolveOutDir();
        Directory.CreateDirectory(outDir);

        var swLoad = Stopwatch.StartNew();
        var request = Load(jsonPath!);
        swLoad.Stop();

        var demandCount = request.LogicalProductionDemands?.Count ?? 0;
        Skip.If(demandCount == 0, "入参 JSON 里一条需求都没有 —— 不是可重放的输入。");

        // ── 求解：超时口径与全流程/重放同一套（APS_RUN_TIMEOUT_MIN）───────────────
        using var solverScope = SolverDiagnostics.BeginScope();
        using var runCts = RealDomainFullRunTest.CreateRunCancellationCore(
            Environment.GetEnvironmentVariable("APS_RUN_TIMEOUT_MIN"), out var timeoutMinutes);

        var sw = Stopwatch.StartNew();
        DomainSolveResult? result = null;
        var cancelled = false;
        try
        {
            result = await new FiniteCapacitySolver()
                .SolveAsync(request, runCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runCts.Token.IsCancellationRequested)
        {
            cancelled = true;
        }
        finally
        {
            sw.Stop();
        }

        var counters = solverScope.Counters;
        var unscheduled = result?.UnscheduledTasks ?? Array.Empty<UnscheduledTaskResult>();
        var batchPolicyLine = ExtractBatchPolicyLine(result?.ErrorMessage);

        var md = BuildMarkdown(
            jsonPath!,
            swLoad.ElapsedMilliseconds,
            request,
            result,
            counters,
            unscheduled,
            batchPolicyLine,
            sw.Elapsed,
            timeoutMinutes,
            cancelled,
            outDir);

        var mdPath = Path.Combine(outDir, "evidence.md");
        var csvPath = Path.Combine(outDir, "unscheduled.csv");
        var jsonOutPath = Path.Combine(outDir, "evidence.json");

        File.WriteAllText(mdPath, md, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(csvPath, BuildCsv(unscheduled), new UTF8Encoding(false));
        File.WriteAllText(jsonOutPath, BuildJson(request, result, counters, unscheduled), new UTF8Encoding(false));

        if (result?.ErrorMessage is { Length: > 0 } rawError)
        {
            File.WriteAllText(Path.Combine(outDir, "error_message.txt"), rawError, new UTF8Encoding(false));
        }

        _output.WriteLine($"[E] 证据已落盘：{outDir}");
        _output.WriteLine("[E]   evidence.md / unscheduled.csv / evidence.json");
        _output.WriteLine($"[E]   需求 {counters.DemandsReceived} 条 / 净量 {counters.DemandQtySum} / "
            + $"有 Task {counters.DemandsWithLandedTask} 条 / FinalTask {(result?.FinalTasks?.Count ?? 0)} 条");
        _output.WriteLine($"[E]   Success={result?.Success}，未排行 {unscheduled.Count} 条 / "
            + $"未排需求 {unscheduled.Select(u => u.DraftId).Distinct(StringComparer.Ordinal).Count()} 条");

        Skip.If(cancelled,
            $"求解在 {sw.Elapsed.TotalMinutes:0.#} 分钟处被取消（APS_RUN_TIMEOUT_MIN={timeoutMinutes:0.##}）。"
            + $" 已写出截至取消时刻的证据：{outDir}");
    }

    // ════════════════════════ 证据正文 ════════════════════════

    private static string BuildMarkdown(
        string jsonPath,
        long loadMs,
        DomainSolveRequest request,
        DomainSolveResult? result,
        SolverDiagnostics.Counters c,
        IReadOnlyList<UnscheduledTaskResult> unscheduled,
        string? batchPolicyLine,
        TimeSpan wall,
        double timeoutMinutes,
        bool cancelled,
        string outDir)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();

        sb.AppendLine("# 1号位 联验取证（离线重放）");
        sb.AppendLine();

        // ── 零、这份证据能干什么、不能干什么（写在最前面，防止脱离上下文被当基准表）──
        sb.AppendLine("## 零 边界");
        sb.AppendLine();
        sb.AppendLine("- 产出于：`JointRunEvidenceDumpTest`（1号位），纯内存重跑 Phase1~5，**零读库、零写入**。");
        sb.AppendLine("- ⚠ 捕获侧序列化器是 `ReferenceHandler.IgnoreCycles`：被判成环的引用写成 `null`，"
            + "反序列化后环恢复不了、共享对象会变多份副本。");
        sb.AppendLine("  ⇒ 本文件可用于「同一份 JSON 前后自比」「未排键与原因分布」；"
            + "**不可**用于与全流程 `FinalTask` 逐位比对。");
        sb.AppendLine("- 硬失败三档取自 `DomainSolveResult.ErrorMessage` 里那一行 `[BatchPolicy]`，"
            + "与 2号位 在 `APS_ETL_Log` 看到的**是同一段文本**（同源 `PhaseFiveCompression`），可直接对账。");
        sb.AppendLine($"- 输出目录：`{outDir}`");
        sb.AppendLine();

        // ── 一、输入身份 ──
        sb.AppendLine("## 一 输入身份");
        sb.AppendLine();
        var fi = new FileInfo(jsonPath);
        sb.AppendLine("| 项 | 值 |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| 入参 JSON | `{jsonPath}` |");
        sb.AppendLine($"| 文件大小 | {(fi.Length / 1048576d).ToString("F1", inv)} MB（反序列化 {loadMs} ms） |");
        sb.AppendLine($"| `ScheduleRunId` | {request.ScheduleRunId?.ToString(inv) ?? "(null)"} |");
        sb.AppendLine($"| `PlanVersionId` | {request.PlanVersionId.ToString(inv)} |");
        sb.AppendLine($"| `DomainKey` | `{request.DomainKey}` |");
        sb.AppendLine($"| `DataCutoffTime` | {request.DataCutoffTime?.ToString("yyyy-MM-dd HH:mm:ss", inv) ?? "(null)"} |");
        sb.AppendLine($"| 规划窗 | {request.PlanningStart.ToString("yyyy-MM-dd HH:mm", inv)} ~ "
            + $"{request.PlanningEnd.ToString("yyyy-MM-dd HH:mm", inv)} |");
        sb.AppendLine($"| 规模 | 需求 {request.LogicalProductionDemands?.Count ?? 0} / "
            + $"资源 {request.Resources?.Count ?? 0} / 日历槽 {request.CalendarSlots?.Count ?? 0} / "
            + $"路由工序 {request.RoutingOperations?.Count ?? 0} / 提前期事实 {request.StageLeadTimes?.Count ?? 0} |");
        sb.AppendLine();
        sb.AppendLine("> 规模若与捕获那一轮的日志不符，说明 JSON 已走形 ⇒ **先别拿这份证据对账**。");
        sb.AppendLine();

        // ── 二、求解器消费侧计数 ──
        sb.AppendLine("## 二 求解器消费侧计数");
        sb.AppendLine();
        sb.AppendLine("| 口径 | 值 | 读法 |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine($"| `DemandsReceived` | {c.DemandsReceived.ToString(inv)} | **需求条数**（按 `LogicalDemandKey` 去重），**不是** Task 条数 |");
        sb.AppendLine($"| `DemandQtySum` | {(c.DemandQtySum).ToString("0.####", inv)} | 需求侧**净量** `NetOutputQty` 之和，**不是**已排量 |");
        sb.AppendLine($"| `DemandsWithLandedTask` | {c.DemandsWithLandedTask.ToString(inv)} | 至少落了一条 `FinalTask` 的需求条数；锁定任务不计入 |");
        sb.AppendLine($"| `Summary.TotalDrafts` | {(result?.Summary?.TotalDrafts ?? 0).ToString(inv)} | **Task 条数**，与上面第一行不是一回事 |");
        sb.AppendLine($"| `FinalTasks.Count` | {(result?.FinalTasks?.Count ?? 0).ToString(inv)} | 实际返回的 Task 条数 |");
        sb.AppendLine($"| `Success` | {result?.Success.ToString() ?? "(未返回)"} | `false` **不是**本工具的失败 —— 它本身就是要交的证据 |");
        sb.AppendLine();

        // ── 三、未排需求键清单 ──
        var distinctDemandKeys = unscheduled
            .Select(u => u.DraftId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        sb.AppendLine("## 三 未排需求键清单");
        sb.AppendLine();
        sb.AppendLine($"- 未排明细行：**{unscheduled.Count.ToString(inv)}** 条（同一需求多 Stage 可多行）");
        sb.AppendLine($"- 未排需求：**{distinctDemandKeys.Count.ToString(inv)}** 条（按 `DraftId` 去重）");
        sb.AppendLine($"- 未排量合计：**{(unscheduled.Sum(u => u.UnscheduledQty)).ToString("0.####", inv)}**");
        sb.AppendLine("- ⚠ `UnscheduledTaskResult.DraftId` 装的是 **`LogicalDemandKey`**（不是 `FinalDraftId`），见 `DomainSolveResult.cs:218-222`。");
        sb.AppendLine("- 全量逐行见 `unscheduled.csv`（列：`DemandKey,Reason,UnscheduledQty`）。");
        sb.AppendLine();

        if (unscheduled.Count > 0)
        {
            sb.AppendLine("### 3.1 按原因聚合（前 20 组）");
            sb.AppendLine();
            sb.AppendLine("| 原因（原样） | 行数 | 未排量 |");
            sb.AppendLine("|---|---|---|");
            foreach (var g in unscheduled
                         .GroupBy(u => u.Reason ?? string.Empty, StringComparer.Ordinal)
                         .Select(g => (Reason: g.Key, Rows: g.Count(), Qty: g.Sum(x => x.UnscheduledQty)))
                         .OrderByDescending(x => x.Rows)
                         .Take(20))
            {
                sb.AppendLine($"| `{Escape(g.Reason)}` | {g.Rows.ToString(inv)} | {(g.Qty).ToString("0.####", inv)} |");
            }

            sb.AppendLine();
            sb.AppendLine("### 3.2 未排量最大的 20 条明细");
            sb.AppendLine();
            sb.AppendLine("| 需求键 | 未排量 | 原因 |");
            sb.AppendLine("|---|---|---|");
            foreach (var u in unscheduled.OrderByDescending(x => x.UnscheduledQty).Take(20))
            {
                sb.AppendLine($"| `{Escape(u.DraftId)}` | {(u.UnscheduledQty).ToString("0.####", inv)} | `{Escape(u.Reason)}` |");
            }

            sb.AppendLine();
        }

        // ── 四、硬失败三档 ──
        sb.AppendLine("## 四 批量策略硬失败三档");
        sb.AppendLine();
        if (batchPolicyLine is null)
        {
            sb.AppendLine("- 本轮 `ErrorMessage` 里**没有** `[BatchPolicy]` 段 ⇒ 硬失败需求 0 条。");
        }
        else
        {
            sb.AppendLine("原样照抄（与 `APS_ETL_Log` 同源）：");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(batchPolicyLine);
            sb.AppendLine("```");
        }

        if (!string.IsNullOrEmpty(result?.ErrorMessage))
        {
            sb.AppendLine();
            sb.AppendLine("- `ErrorMessage` 全文（含非 `[BatchPolicy]` 的其它段）已另存 `error_message.txt`。");
        }

        sb.AppendLine();

        // ── 五、分段耗时 ──
        var totalMs = c.Phase1Ms + c.Phase2Ms + c.Phase3Ms + c.Phase4Ms + c.Phase5Ms;
        sb.AppendLine("## 五 分段耗时");
        sb.AppendLine();
        sb.AppendLine("| Phase | 毫秒 | 分钟 |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine($"| P1 建约束 | {c.Phase1Ms.ToString(inv)} | {(c.Phase1Ms / 60000d).ToString("F2", inv)} |");
        sb.AppendLine($"| P2 排程 | {c.Phase2Ms.ToString(inv)} | {(c.Phase2Ms / 60000d).ToString("F2", inv)} |");
        sb.AppendLine($"| P3 诊断 | {c.Phase3Ms.ToString(inv)} | {(c.Phase3Ms / 60000d).ToString("F2", inv)} |");
        sb.AppendLine($"| P4 修复 | {c.Phase4Ms.ToString(inv)} | {(c.Phase4Ms / 60000d).ToString("F2", inv)} |");
        sb.AppendLine($"| P5 压缩 | {c.Phase5Ms.ToString(inv)} | {(c.Phase5Ms / 60000d).ToString("F2", inv)} |");
        sb.AppendLine($"| **合计** | **{totalMs.ToString(inv)}** | **{(totalMs / 60000d).ToString("F2", inv)}** |");
        sb.AppendLine($"| 墙钟 | {(wall.TotalMilliseconds).ToString("F0", inv)} | {(wall.TotalMinutes).ToString("F2", inv)} |");
        sb.AppendLine();
        sb.AppendLine($"- 超时口径：`APS_RUN_TIMEOUT_MIN` = {(timeoutMinutes).ToString("0.##", inv)} 分钟"
            + $"（{(cancelled ? "**本轮被取消**" : "本轮未触发")}）。");
        sb.AppendLine("- 分段耗时**属 1号位 辖区**；Pegging / 插库等库侧耗时属 2号位，不在此表。");
        sb.AppendLine();

        // ── 六、SolverDiagnostics.LastSnapshot ──
        sb.AppendLine("## 六 `SolverDiagnostics.LastSnapshot`");
        sb.AppendLine();
        var snap = SolverDiagnostics.LastSnapshot;
        if (snap is null)
        {
            sb.AppendLine("- 本轮未产出快照（`LastSnapshot` 为 null）。");
        }
        else
        {
            sb.AppendLine("| 字段 | 值 |");
            sb.AppendLine("|---|---|");
            sb.AppendLine($"| `CurrentPhase` | `{Escape(snap.CurrentPhase)}` |");
            sb.AppendLine($"| `Sequence` | {snap.Sequence.ToString(inv)} |");
            sb.AppendLine($"| `IsRunning` | {snap.IsRunning} |");
            sb.AppendLine($"| `CompletedAt` | {snap.CompletedAt?.ToString("yyyy-MM-dd HH:mm:ss", inv) ?? "(null)"} |");
            sb.AppendLine($"| `Phase2DemandsTotal` | {snap.Phase2DemandsTotal.ToString(inv)} |");
            sb.AppendLine($"| `Phase2DemandsProcessed` | {snap.Phase2DemandsProcessed.ToString(inv)} |");
            sb.AppendLine($"| `Phase4UnitsTotal` | {snap.Phase4UnitsTotal.ToString(inv)} |");
            sb.AppendLine($"| `Phase4UnitsProcessed` | {snap.Phase4UnitsProcessed.ToString(inv)} |");
            sb.AppendLine($"| `BatchPlanCandidateTrials` | {snap.BatchPlanCandidateTrials.ToString(inv)} |");
            sb.AppendLine($"| `CloneOccupancy` | {snap.CloneOccupancy.ToString(inv)} |");
            sb.AppendLine($"| `MergeScanCalls` | {snap.MergeScanCalls.ToString(inv)} |");
            sb.AppendLine($"| `OccupancyRollbackUnmatched` | {snap.OccupancyRollbackUnmatched.ToString(inv)} |");
            sb.AppendLine($"| `SlotSearches` | {snap.SlotSearches.ToString(inv)} |");
            sb.AppendLine($"| `SlotSearchAttempts` | {snap.SlotSearchAttempts.ToString(inv)} |");
            sb.AppendLine($"| `RunBatchPlanInvocations` | {snap.RunBatchPlanInvocations.ToString(inv)} |");
        }

        sb.AppendLine();

        // ── 七、完成时间可信度 ──
        var completions = result?.DemandCompletions ?? Array.Empty<DemandCompletionFact>();
        var uncertain = completions.Count(d => d.CompletionTimeCertain == false);
        var nullCertain = completions.Count(d => d.CompletionTimeCertain is null);
        sb.AppendLine("## 七 完成时间可信度（G-3）");
        sb.AppendLine();
        sb.AppendLine($"- `DemandCompletions` 条数：{completions.Count.ToString(inv)}");
        sb.AppendLine($"- `CompletionTimeCertain == false`：**{uncertain.ToString(inv)}** 条（时间不可信 ⇒ 不得当承诺/供给可用时间）");
        sb.AppendLine($"- `CompletionTimeCertain == null`：{nullCertain.ToString(inv)} 条（未标注，按现状）");
        sb.AppendLine();

        return sb.ToString();
    }

    // ════════════════════════ 三个附属产物 ════════════════════════

    private static string BuildCsv(IReadOnlyList<UnscheduledTaskResult> unscheduled)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("DemandKey,Reason,UnscheduledQty\n");
        foreach (var u in unscheduled)
        {
            sb.Append(CsvField(u.DraftId)).Append(',')
              .Append(CsvField(u.Reason)).Append(',')
              .Append((u.UnscheduledQty).ToString("0.####", inv))
              .Append('\n');
        }

        return sb.ToString();
    }

    private static string BuildJson(
        DomainSolveRequest request,
        DomainSolveResult? result,
        SolverDiagnostics.Counters c,
        IReadOnlyList<UnscheduledTaskResult> unscheduled)
    {
        var payload = new
        {
            inputIdentity = new
            {
                scheduleRunId = request.ScheduleRunId,
                planVersionId = request.PlanVersionId,
                domainKey = request.DomainKey,
                dataCutoffTime = request.DataCutoffTime,
                planningStart = request.PlanningStart,
                planningEnd = request.PlanningEnd,
                demandCount = request.LogicalProductionDemands?.Count ?? 0,
                resourceCount = request.Resources?.Count ?? 0,
                calendarSlotCount = request.CalendarSlots?.Count ?? 0,
                routingOperationCount = request.RoutingOperations?.Count ?? 0,
                stageLeadTimeFactCount = request.StageLeadTimes?.Count ?? 0,
            },
            demandAccounting = new
            {
                demandsReceived = c.DemandsReceived,
                demandQtySum = c.DemandQtySum,
                demandsWithLandedTask = c.DemandsWithLandedTask,
                summaryTotalDrafts = result?.Summary?.TotalDrafts ?? 0,
                finalTaskCount = result?.FinalTasks?.Count ?? 0,
                success = result?.Success,
            },
            unscheduled = new
            {
                rowCount = unscheduled.Count,
                demandCount = unscheduled.Select(u => u.DraftId).Distinct(StringComparer.Ordinal).Count(),
                qtySum = unscheduled.Sum(u => u.UnscheduledQty),
                byReason = unscheduled
                    .GroupBy(u => u.Reason ?? string.Empty, StringComparer.Ordinal)
                    .Select(g => new { reason = g.Key, rows = g.Count(), qty = g.Sum(x => x.UnscheduledQty) })
                    .OrderByDescending(x => x.rows)
                    .ToArray(),
            },
            phaseMs = new
            {
                phase1 = c.Phase1Ms,
                phase2 = c.Phase2Ms,
                phase3 = c.Phase3Ms,
                phase4 = c.Phase4Ms,
                phase5 = c.Phase5Ms,
                total = c.Phase1Ms + c.Phase2Ms + c.Phase3Ms + c.Phase4Ms + c.Phase5Ms,
            },
            batchPolicy = new
            {
                line = ExtractBatchPolicyLine(result?.ErrorMessage),
            },
            completionCertain = new
            {
                total = result?.DemandCompletions?.Count ?? 0,
                uncertain = result?.DemandCompletions?.Count(d => d.CompletionTimeCertain == false) ?? 0,
                unannotated = result?.DemandCompletions?.Count(d => d.CompletionTimeCertain is null) ?? 0,
            },
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    // ════════════════════════ 工具 ════════════════════════

    /// <summary>
    /// 从 `ErrorMessage` 里抽出 `[BatchPolicy] …` 那一段（段间分隔符是 `" | "`，见 `ComposeErrorMessage`）。
    /// 抽不出来 = 本轮没有硬失败需求，返回 <c>null</c>。
    /// </summary>
    private static string? ExtractBatchPolicyLine(string? errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage))
        {
            return null;
        }

        const string marker = "[BatchPolicy]";
        var start = errorMessage.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var nextSegment = errorMessage.IndexOf(" | ", start, StringComparison.Ordinal);
        var slice = nextSegment < 0 ? errorMessage[start..] : errorMessage[start..nextSegment];
        return slice.Trim();
    }

    private static string Escape(string? text)
        => (text ?? string.Empty).Replace('|', '｜').Replace("\r", " ").Replace("\n", " ");

    private static string CsvField(string? text)
    {
        var value = text ?? string.Empty;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string ResolveOutDir()
    {
        var configured = Environment.GetEnvironmentVariable(EvidenceOutEnvVar);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured!;
        }

        return Path.Combine(
            Path.GetTempPath(),
            "aps_evidence_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 流式反序列化（与 `SolverReplayTest.Load` 同一组选项：读写对称，避免「捕获能写、重放读不回」）。
    /// **不用 `Deserialize&lt;T&gt;(string)`** —— 会先把整份文件读成 string，PV190 量级是数百 MB 的 LOH。
    /// </summary>
    private static DomainSolveRequest Load(string jsonPath)
    {
        var options = new JsonSerializerOptions
        {
            ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles,
            MaxDepth = 256,
        };

        using var fs = new FileStream(jsonPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20);
        return JsonSerializer.Deserialize<DomainSolveRequest>(fs, options)
               ?? throw new InvalidOperationException($"入参 JSON 反序列化得到 null：{jsonPath}");
    }
}
