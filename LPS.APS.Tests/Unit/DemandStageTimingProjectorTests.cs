using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// A34「需求级时间结果落库」投影语义钉死测试（0号位 2026-11-24 裁决 v1.2 §二）。
///
/// <para><b>为什么必须有这组</b>：这张表决定「某需求哪天能交」被记成什么。判错不会编译报错，
/// 只会**多写／少写**：①把 <c>TIMING_ONLY</c> 的净量重复计进份额账；②把不可信时间标成可信；
/// ③造一个零时间行冒充「已算」。三条都在下面逐条钉住。</para>
/// </summary>
public class DemandStageTimingProjectorTests
{
    private const int Pv = 999634;
    private const int Run = 999539;
    private const string Domain = "FAMILY_FRL";

    private static LogicalProductionDemand Demand(
        string key, decimal netQty = 100m, bool isRoot = true,
        long? orderId = 7, string? bomNo = "B1", long? worksetId = 11, int materialId = 5001)
        => new()
        {
            LogicalDemandKey = key,
            PlanVersionId = Pv,
            DomainKey = Domain,
            DemandKey = "ORDER_7_M_1",
            OrderId = orderId,
            IsRootDemand = isRoot,
            BomNo = bomNo,
            RepWorksetId = worksetId,
            MaterialId = materialId,
            FactoryId = 6,
            NetOutputQty = netQty,
            StartStageCode = "TJ_MACH"
        };

    /// <summary>
    /// 构造时间事实。
    /// <para>2026-11-26：实库 `StageSegmentsJson` 为 **NOT NULL** 且「无段不产行」，
    /// 故默认**带一段**合法 Stage 时间；`noSegments: true` 才构出「无段」场景。</para>
    /// </summary>
    private static DemandCompletionFact Fact(
        string key, DateTime completion, bool? certain = true, string? requiredStage = null,
        bool noSegments = false)
        => new()
        {
            StageTimings = noSegments
                ? Array.Empty<StageTimingSegmentFact>()
                : new[]
                {
                    new StageTimingSegmentFact
                    {
                        StageCode = "TJ_MACH",
                        StartTime = completion,
                        EndTime = completion.AddHours(1),
                        LeadTimeHours = 1m,
                        MatchLevel = "DEPT_EXACT"
                    }
                },
            LogicalDemandKey = key,
            MaterialId = 5001,
            CompletionTime = completion,
            AvailableTime = completion,
            CompletionTimeCertain = certain,
            RequiredStageCode = requiredStage
        };

    private static readonly DateTime T0 = new(2026, 12, 1, 8, 0, 0);

    // ── ① 唯一键：每需求至多一行 ───────────────────────────────────────────
    [Fact]
    public void 同一需求键重复出现_只落一行()
    {
        var demands = new[] { Demand("K1"), Demand("K1"), Demand("K2") };
        var facts = new[] { Fact("K1", T0), Fact("K2", T0) };

        var r = DemandStageTimingProjector.Project(Pv, Run, Domain, demands, facts);

        r.Rows.Should().HaveCount(2, "UQ_ADSTR_PV_LogicalDemand 是 (PV, 需求键) 唯一，重复键不得撞");
        r.Rows.Select(x => x.LogicalDemandKey).Should().OnlyHaveUniqueItems();
    }

    // ── ② 结果类型判据只认「有无真实 Task」 ────────────────────────────────
    [Fact]
    public void 有真实Task_记TASK_WITH_TIMING且仅计时量为0()
    {
        var r = DemandStageTimingProjector.Project(
            Pv, Run, Domain, new[] { Demand("K1", netQty: 81m) }, new[] { Fact("K1", T0) },
            demandKeysWithTask: new HashSet<string>(StringComparer.Ordinal) { "K1" });

        var row = r.Rows.Single();
        row.ResultType.Should().Be(DemandStageTimingResultType.TaskWithTiming);
        row.TimingOnlyQty.Should().Be(0m, "份额已由 AllocationTaskShare 承载，此处再记就是重复计");
        row.DemandNetQty.Should().Be(81m);
    }

    [Fact]
    public void 无真实Task_记TIMING_ONLY且仅计时量等于净量()
    {
        var r = DemandStageTimingProjector.Project(
            Pv, Run, Domain, new[] { Demand("K1", netQty: 3m) }, new[] { Fact("K1", T0) },
            demandKeysWithTask: new HashSet<string>(StringComparer.Ordinal));

        var row = r.Rows.Single();
        row.ResultType.Should().Be(DemandStageTimingResultType.TimingOnly);
        row.TimingOnlyQty.Should().Be(3m, "无 Task 承接的那部分净量只能存在于本表的时间账");
    }

    // ── ③ 三态原样透传，不擅自升降级 ───────────────────────────────────────
    [Theory]
    [InlineData(true, DemandStageTimingTimeStatus.Certain)]
    [InlineData(false, DemandStageTimingTimeStatus.Uncertain)]
    [InlineData(null, DemandStageTimingTimeStatus.Unknown)]
    public void 完成时间可信度三态_原样映射(bool? certain, string expected)
    {
        DemandStageTimingProjector.MapTimeStatus(certain).Should().Be(expected);
    }

    [Fact]
    public void 未标注的完成时间_不得被降级成不可信()
    {
        var r = DemandStageTimingProjector.Project(
            Pv, Run, Domain, new[] { Demand("K1") }, new[] { Fact("K1", T0, certain: null) });

        r.Rows.Single().TimeStatus.Should().Be(DemandStageTimingTimeStatus.Unknown,
            "null = 旧载荷未标注，按现状透传；降成 UNCERTAIN 等于我方擅自改判");
        r.UnannotatedCount.Should().Be(1);
        r.UncertainCount.Should().Be(0);
    }

    [Fact]
    public void 不可信完成时间_计入UncertainCount供读侧拒用()
    {
        var r = DemandStageTimingProjector.Project(
            Pv, Run, Domain, new[] { Demand("K1") }, new[] { Fact("K1", T0, certain: false) });

        r.UncertainCount.Should().Be(1);
        r.Rows.Single().CompletionTimeCertain.Should().BeFalse();
    }

    // ── ④ 无时间事实 ⇒ 不产行（不造零时间冒充已算）─────────────────────────
    [Fact]
    public void 需求无对应时间事实_不产行且显性计数()
    {
        var r = DemandStageTimingProjector.Project(
            Pv, Run, Domain, new[] { Demand("K1"), Demand("K2") }, new[] { Fact("K1", T0) });

        r.Rows.Should().ContainSingle();
        r.Rows.Single().LogicalDemandKey.Should().Be("K1");
        r.DemandsWithoutFact.Should().Be(1, "少写必须可数，不能静默丢弃");
    }

    [Fact]
    public void 时间事实为默认值_完成时间留空而不是0001年()
    {
        var r = DemandStageTimingProjector.Project(
            Pv, Run, Domain, new[] { Demand("K1") },
            new[]
            {
                new DemandCompletionFact
                {
                    LogicalDemandKey = "K1",
                    CompletionTime = default,
                    // 带一段合法段：否则「无段不产行」会先把它挡掉，验不到本用例要验的「时间留空」
                    StageTimings = new[]
                    {
                        new StageTimingSegmentFact
                        {
                            StageCode = "TJ_MACH", StartTime = T0, EndTime = T0.AddHours(1),
                            LeadTimeHours = 1m, MatchLevel = "DEPT_EXACT"
                        }
                    }
                }
            });

        r.Rows.Single().PlannedCompletionTime.Should().BeNull(
            "把 default(DateTime) 落库会变成 0001-01-01，比留空更糟");
    }

    // ── ⑤ 不造计划开始时间 ────────────────────────────────────────────────
    [Fact]
    public void 计划开始时间恒为空_不用GETDATE补()
    {
        var r = DemandStageTimingProjector.Project(
            Pv, Run, Domain, new[] { Demand("K1") }, new[] { Fact("K1", T0) });

        r.Rows.Single().PlanningStartTime.Should().BeNull(
            "1号位 尚未给需求级计划开始；用 GETDATE 补计划时间＝造假");
    }

    // ── ⑥ 重复时间事实键：取首条并计数 ────────────────────────────────────
    [Fact]
    public void 同一需求多条时间事实_取首条并计数()
    {
        var second = T0.AddDays(1);
        var r = DemandStageTimingProjector.Project(
            Pv, Run, Domain, new[] { Demand("K1") },
            new[] { Fact("K1", T0), Fact("K1", second) });

        r.Rows.Single().PlannedCompletionTime.Should().Be(T0, "按输入顺序取首条，不静默覆盖");
        r.DuplicateFactKeys.Should().Be(1);
    }

    // ── ⑦ Stage 段：有段就按「时间＋顺序＋执行厂＋LT来源」拼，没有就 null ──────

    private static StageTimingSegmentFact Seg(string stage, DateTime st, DateTime et, decimal lt, string level = "DEPT_EXACT")
        => new() { StageCode = stage, StartTime = st, EndTime = et, LeadTimeHours = lt, MatchLevel = level };

    private static StageSequenceStep Step(string stage, int seq, int? factoryId = 3, string? factoryCode = "CN1")
        => new() { StageCode = stage, StageSeq = seq, StageFactoryId = factoryId, StageFactoryCode = factoryCode };

    [Fact]
    public void 有Stage段_拼出顺序时间执行厂与LT来源()
    {
        var fact = new DemandCompletionFact
        {
            LogicalDemandKey = "K1",
            CompletionTime = T0,
            CompletionTimeCertain = true,
            StageTimings = new[]
            {
                Seg("CN_ASSY",  T0, T0.AddHours(5), 5m),
                Seg("CN_FINAL", T0.AddHours(5), T0.AddHours(8), 3m, "FACTORY_STAGE_DEFAULT")
            }
        };

        var r = DemandStageTimingProjector.Project(
            Pv, Run, Domain, new[] { Demand("K1") }, new[] { fact },
            chainByDemandKey: new Dictionary<string, IReadOnlyList<StageSequenceStep>?>
            {
                ["K1"] = new[] { Step("CN_ASSY", 10), Step("CN_FINAL", 20) }
            });

        var json = r.Rows.Single().StageSegmentsJson!;
        json.Should().Contain("CN_ASSY").And.Contain("CN_FINAL");
        json.Should().Contain("\"StageSeq\":10").And.Contain("\"StageSeq\":20");
        json.Should().Contain("\"StageFactoryCode\":\"CN1\"");
        json.Should().Contain("DEPT_EXACT").And.Contain("FACTORY_STAGE_DEFAULT");
    }

    [Fact]
    public void 链上找不到该Stage_顺序与执行厂留null而不臆造()
    {
        var fact = new DemandCompletionFact
        {
            LogicalDemandKey = "K1",
            CompletionTime = T0,
            StageTimings = new[] { Seg("NOT_IN_CHAIN", T0, T0.AddHours(1), 1m) }
        };

        var r = DemandStageTimingProjector.Project(
            Pv, Run, Domain, new[] { Demand("K1") }, new[] { fact },
            chainByDemandKey: new Dictionary<string, IReadOnlyList<StageSequenceStep>?>
            {
                ["K1"] = new[] { Step("CN_ASSY", 10) }
            });

        var json = r.Rows.Single().StageSegmentsJson!;
        json.Should().Contain("\"StageSeq\":null");
        json.Should().Contain("\"StageFactoryId\":null");
    }

    // ── ⑦ Stage 段：无段 ⇒ 不产行（列是 NOT NULL，且不得伪造） ──────────────
    [Fact]
    public void 没有逐Stage段_不产行并计数_不得落null也不得塞空数组()
    {
        // 实库 `StageSegmentsJson` 是 **NOT NULL** ⇒ 落 null 会插入失败；
        // 0号位 口径「无合法 StageTiming 不伪造结果行」⇒ 正解是**不产行**。
        var r = DemandStageTimingProjector.Project(
            Pv, Run, Domain, new[] { Demand("K1") }, new[] { Fact("K1", T0, requiredStage: "TJ_OUTS", noSegments: true) });

        r.Rows.Should().BeEmpty("无段 ⇒ 不产行；塞 null 会撞 NOT NULL，塞 [] 属用空掩盖缺失");
        r.DemandsWithoutSegments.Should().Be(1, "少写必须可数，不能静默丢");
    }

    // ── ⑧ 角色 → scope ───────────────────────────────────────────────────
    [Theory]
    [InlineData(true, "ROOT")]
    [InlineData(false, "EDGE")]
    public void 需求角色映射到scope(bool isRoot, string expected)
    {
        var r = DemandStageTimingProjector.Project(
            Pv, Run, Domain, new[] { Demand("K1", isRoot: isRoot) }, new[] { Fact("K1", T0) });

        r.Rows.Single().StageScopeType.Should().Be(expected);
    }

    // ── ⑨ 键字段落库口径 ──────────────────────────────────────────────────
    [Fact]
    public void 空业务键落null而不是空串()
    {
        var r = DemandStageTimingProjector.Project(
            Pv, Run, Domain,
            new[] { Demand("K1", bomNo: null, orderId: null, worksetId: null) },
            new[] { Fact("K1", T0) });

        var row = r.Rows.Single();
        row.ResolvedBOMNO.Should().BeNull();
        row.OrderId.Should().BeNull();
        row.RepWorksetId.Should().BeNull();
    }
}