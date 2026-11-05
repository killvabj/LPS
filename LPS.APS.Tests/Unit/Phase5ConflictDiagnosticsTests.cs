using System;
using System.Collections.Generic;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **Phase5 硬互斥冲突消息的诊断能力**（1号位 2026-10-31，应 2号位
/// 《FAMILY_X 整域求解失败（Phase5 硬约束校验）证据与定位请求 v1.0》§9.1／§9.2）。
///
/// <para><b>为什么值得单开一个测试类</b>：那次定位卡住的**唯一**原因就是消息不可判 ——
/// `16:59:56 → 15:47:06` 只能确定「终点晚于起点 ⇒ 不同日」，而差一天 = 22h47m10s、
/// 差三天 = 70h47m10s，从文本上完全分不出来；并且 A 路会把整份 FinalTask 清空（Task 表零行），
/// 事后没有任何反查依据。这两条性质**只能靠断言守住** —— 消息里一旦没有日期、没有涉事任务属性，
/// 下一次同样的失败还是查不动。</para>
///
/// <para><b>同时钉死一条不许退化的边界</b>：**判据本身一字没改**。
/// 真重叠仍然红、不重叠仍然绿 —— 补诊断绝不能被写成「顺手放宽」。</para>
///
/// <para><b>构型来源</b>：照抄 2号位 件的 4.2／4.3 节（同一条单容量线 `Resource 13394`，
/// 一个跨日长块套住一条 30 秒小任务）。日期取 2027 年，与生产数据无关，只为让「跨日」在断言里可见。</para>
/// </summary>
public class Phase5ConflictDiagnosticsTests
{
    private const int ConflictedResourceId = 13394;

    // 跨日长块：起点 02-10 16:59:56，终点 02-11 15:47:06（跨度 1,367.2 分钟，有效加工 752 分钟）
    private static readonly DateTime LongBlockStart = new(2027, 2, 10, 16, 59, 56);
    private static readonly DateTime LongBlockEnd = new(2027, 2, 11, 15, 47, 6);

    // ════════════════════════════════════════════════════════════════════
    //  9.1 —— 日期
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void D_01_冲突消息必须带完整日期_不许只剩时分秒()
    {
        var (isValid, error) = Run(Context());

        Assert.False(isValid, "同资源跨日重叠必须判失败");

        // ★ 本测试的全部意义：旧格式 `HH:mm:ss` 下这两条断言必红。
        Assert.Contains("2027-02-10 16:59:56", error);
        Assert.Contains("2027-02-11 08:00:00", error);
    }

    // ════════════════════════════════════════════════════════════════════
    //  9.2 —— 涉事任务属性
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void D_02_冲突消息必须带两个涉事任务的属性()
    {
        var (isValid, error) = Run(Context());

        Assert.False(isValid);
        Assert.Contains("A[FinalDraftId=T1", error);
        Assert.Contains("B[FinalDraftId=T2", error);

        // 「有效加工」与「真实占用跨度」是两个数，必须都读得到 —— 这正是分不开
        // 「任务本身长」与「跨窗/跨非工作时段被拉长」的关键。
        Assert.Contains("EffProcMin=752.00", error);
        Assert.Contains("SpanMin=1367.2", error);

        // `AllocationSequence` 不是 `FinalTaskDraft` 的字段（已 grep 确认），
        // 按 `SourceDraftId` 回查需求得到 ⇒ 这里钉死回查确实接上了，不是恒写 `-`。
        Assert.Contains("AllocSeq=1", error);
    }

    [Fact]
    public void D_03_回查不到需求时_AllocSeq写横杠而不是编一个数()
    {
        var ctx = Context();
        ctx.Tasks[0] = Rebind(ctx.Tasks[0], sourceDraftId: "NOT_A_DEMAND");

        var (isValid, error) = Run(ctx);

        Assert.False(isValid);
        Assert.Contains("AllocSeq=-", error);
    }

    // ════════════════════════════════════════════════════════════════════
    //  判据不得放宽
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void D_04_判据不得放宽_真重叠仍然红()
    {
        var (isValid, _) = Run(Context(
            otherStart: new DateTime(2027, 2, 11, 8, 0, 0),
            otherEnd: new DateTime(2027, 2, 11, 8, 0, 30)));

        Assert.False(isValid);
    }

    [Fact]
    public void D_05_判据不得放宽_长块结束之后不重叠必须绿()
    {
        // 后继挪到长块结束（02-11 15:47:06）之后 ⇒ 半开区间下不重叠，必须通过。
        var (isValid, error) = Run(Context(
            otherStart: new DateTime(2027, 2, 11, 16, 0, 0),
            otherEnd: new DateTime(2027, 2, 11, 16, 0, 30)));

        Assert.True(isValid, error);
    }

    [Fact]
    public void D_06_长块起点之前不重叠也必须绿()
    {
        // 后继落在长块**起点之前**（02-10 08:00-08:00:30）⇒ 排序后它在前、长块在后，
        // 两者不重叠 ⇒ 必须通过。防「补了日期顺手把判据写宽」的反向退化。
        var (isValid, error) = Run(Context(
            otherStart: new DateTime(2027, 2, 10, 8, 0, 0),
            otherEnd: new DateTime(2027, 2, 10, 8, 0, 30)));

        Assert.True(isValid, error);
    }

    // ════════════════════════════════════════════════════════════════════
    //  夹具
    // ════════════════════════════════════════════════════════════════════

    private sealed class Fixture
    {
        public List<FinalTaskDraft> Tasks { get; init; } = new();
        public DomainSolveRequest Request { get; init; } = new();
        public ConstraintContext Constraints { get; init; } = new();
        public List<AllocationTaskShare> Shares { get; init; } = new();
    }

    private static (bool IsValid, string Error) Run(Fixture f)
        => PhaseFiveCompression.ValidateHardResultForTest(
            f.Tasks,
            f.Shares,
            new List<FinalTaskPeggingDraft>(),
            f.Request,
            f.Constraints,
            new HashSet<string>(),
            new Dictionary<string, List<(string DemandKey, decimal ShareQty)>>());

    /// <summary>
    /// 两条任务挂在**同一个需求** `D1`（`AllocationSequence = 1`）下，份额 6 + 4 = 10 = `NetOutputQty`
    /// ⇒ 第 1／3 项（份额闭合）通过，**只有第 4 项（资源硬互斥）**会被触发。
    /// </summary>
    private static Fixture Context(DateTime? otherStart = null, DateTime? otherEnd = null) => new()
    {
        Tasks = new List<FinalTaskDraft>
        {
            // 跨日长块
            new()
            {
                FinalDraftId = "T1",
                SourceDraftId = "D1",
                MaterialId = 1,
                FactoryId = 1,
                StageCode = "S1",
                OperationCode = "OP1",
                ResourceId = ConflictedResourceId,
                Quantity = 6m,
                PlannedProcessQty = 6m,
                EffectiveProcessMinutes = 752m,
                SetupTime = 0m,
                PlannedStartTime = LongBlockStart,
                PlannedEndTime = LongBlockEnd,
            },
            // 被套住的那条（或挪开后的对照）
            new()
            {
                FinalDraftId = "T2",
                SourceDraftId = "D1",
                MaterialId = 1,
                FactoryId = 1,
                StageCode = "S1",
                OperationCode = "OP1",
                ResourceId = ConflictedResourceId,
                Quantity = 4m,
                PlannedProcessQty = 4m,
                EffectiveProcessMinutes = 0.5m,
                SetupTime = 0m,
                PlannedStartTime = otherStart ?? new DateTime(2027, 2, 11, 8, 0, 0),
                PlannedEndTime = otherEnd ?? new DateTime(2027, 2, 11, 8, 0, 30),
            },
        },
        Request = new DomainSolveRequest
        {
            LogicalProductionDemands = new List<LogicalProductionDemand>
            {
                new()
                {
                    LogicalDemandKey = "D1",
                    PlanVersionId = 1L,
                    DomainKey = "DOMAIN",
                    AllocationSequence = 1L,
                    DemandKey = "D1",
                    MaterialId = 1,
                    FactoryId = 1,
                    NetOutputQty = 10m,
                    PlannedProcessQty = 10m,
                    DemandSequence = 1,
                    StartStageCode = "S1",
                    StartOperationCode = "OP1",
                },
            },
        },
        Shares = new List<AllocationTaskShare>
        {
            new() { FinalDraftId = "T1", AllocationSequence = 1L, ComponentQty = 6m },
            new() { FinalDraftId = "T2", AllocationSequence = 1L, ComponentQty = 4m },
        },
    };

    private static FinalTaskDraft Rebind(FinalTaskDraft t, string sourceDraftId) => new()
    {
        FinalDraftId = t.FinalDraftId,
        SourceDraftId = sourceDraftId,
        MaterialId = t.MaterialId,
        FactoryId = t.FactoryId,
        StageCode = t.StageCode,
        OperationCode = t.OperationCode,
        ResourceId = t.ResourceId,
        Quantity = t.Quantity,
        PlannedProcessQty = t.PlannedProcessQty,
        EffectiveProcessMinutes = t.EffectiveProcessMinutes,
        SetupTime = t.SetupTime,
        PlannedStartTime = t.PlannedStartTime,
        PlannedEndTime = t.PlannedEndTime,
    };
}
