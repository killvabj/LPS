using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **R11 第 4 条：超时／人工取消／硬不可行 三类分类实测**（2026-10-29）。
///
/// <para><b>三类的落点（刻意不都用同一种载体）</b>：</para>
/// <list type="number">
///   <item><b>超时 / 人工取消</b> —— 求解器内部**同形**（都是 `OperationCanceledException`），
///     `CancellationToken` 不携带来源，所以「是超时还是人工取消」**只有调用方的 token 源知道**。
///     本号位只做能确定的那一半：`SolverDiagnostics.RecordCancellation` 记下**在哪一阶段**被中断，
///     异常**原样上抛**（不吞、不改对外契约、不伪造 `Success=false`）。</item>
///   <item><b>硬不可行</b> —— 走 `DomainSolveResult.Success = false`，`ErrorMessage` 带
///     `[HARD_INFEASIBLE]` 机器可读前缀，`SolveTraceNotes` 带 `HARD_INFEASIBLE_*` 的 Error 级痕迹，
///     且 `FinalTasks` 必须为空（**不伪造任何排程结果**）。</item>
/// </list>
///
/// <para><b>为什么不替调用方猜「超时 vs 人工取消」</b>：猜错会把「用户主动停」记成「算不完」，
/// 或反过来把「算不完」记成「用户放弃」—— 两种都会让运维做反方向的处置。宁可只报可确知的部分。</para>
/// </summary>
public class SolverFailureClassificationTests
{
    // ════════════════════════════════════════════════════════════════════
    // T1：取消（超时/人工取消的公共形态）
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 已取消的 token ⇒ 求解器在 Phase1 边界就抛出 `OperationCanceledException`，
    /// **不返回任何结果**（不像硬不可行那样给 `Success=false`），并留下「在哪一阶段被中断」的记录。
    /// </summary>
    [Fact]
    public async Task T1_已取消的token_原样抛OCE并记录中断阶段()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FiniteCapacitySolver().SolveAsync(new DomainSolveRequest(), cts.Token));

        var record = SolverDiagnostics.LastCancellation;
        Assert.NotNull(record);
        Assert.False(string.IsNullOrWhiteSpace(record!.Phase));

        // 时间戳要是「刚刚」，防止读到别的用例留下的旧记录。
        Assert.True(DateTime.UtcNow - record.ObservedAtUtc < TimeSpan.FromMinutes(5),
            $"LastCancellation 时间戳过旧：{record.ObservedAtUtc:O}");
    }

    // ════════════════════════════════════════════════════════════════════
    // T2：硬不可行 —— 机器可读前缀 + Error 留痕 + 无伪造结果
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 同一 `(DraftId, StageCode, OperationCode)` 出现两条 `ExecutionConstraint`
    /// ⇒ 输入契约无法唯一标识锚点 ⇒ 受控 Fail Closed（`PhaseOneConstraintBuilder.cs:1530`）。
    /// 断言三件：`[HARD_INFEASIBLE]` 前缀、`HARD_INFEASIBLE_INPUT_CONTRACT` Error 痕、`FinalTasks` 为空。
    /// </summary>
    [Fact]
    public async Task T2_锚点键域冲突_硬不可行带机器可读类且不伪造结果()
    {
        var request = new DomainSolveRequest
        {
            PlanVersionId = 1,
            ScheduleRunId = 42,
            DomainKey = "DOMAIN",
            PlanningStart = new DateTime(2026, 9, 1),
            PlanningEnd = new DateTime(2026, 9, 30),
            ExecutionConstraints = new[]
            {
                new ExecutionConstraint { DraftId = "D1", StageCode = "STAGE1", OperationCode = "OP10" },
                new ExecutionConstraint { DraftId = "D1", StageCode = "STAGE1", OperationCode = "OP10" }
            }
        };

        var result = await new FiniteCapacitySolver().SolveAsync(request);

        Assert.False(result.Success);
        Assert.StartsWith("[HARD_INFEASIBLE]", result.ErrorMessage);

        var note = Assert.Single(result.SolveTraceNotes,
            n => n.ReasonCode == "HARD_INFEASIBLE_INPUT_CONTRACT");
        Assert.Equal("Error", note.Level);

        // 硬不可行**不许**顺手给一份"看起来能跑"的排程。
        Assert.Empty(result.FinalTasks);
    }

    // ════════════════════════════════════════════════════════════════════
    // T3：对照 —— 可解输入不带硬不可行前缀
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 正常可解的最小输入 ⇒ `Success=true`，`ErrorMessage` 不带 `[HARD_INFEASIBLE]`。
    /// 与 T2 合起来说明该前缀确实只标识硬不可行，不是到处都有。
    /// </summary>
    [Fact]
    public async Task T3_对照_可解输入不带硬不可行前缀()
    {
        var request = new DomainSolveRequest
        {
            PlanVersionId = 1,
            ScheduleRunId = 42,
            DomainKey = "DOMAIN",
            PlanningStart = new DateTime(2026, 9, 1),
            PlanningEnd = new DateTime(2026, 9, 30)
        };

        var result = await new FiniteCapacitySolver().SolveAsync(request);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.DoesNotContain("[HARD_INFEASIBLE]", result.ErrorMessage ?? string.Empty);
        Assert.DoesNotContain(result.SolveTraceNotes,
            n => n.ReasonCode.StartsWith("HARD_INFEASIBLE", StringComparison.Ordinal));
    }
}
