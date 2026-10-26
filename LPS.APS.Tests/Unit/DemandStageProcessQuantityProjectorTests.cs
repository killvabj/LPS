using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// G-1「逐 Stage 计划加工量」投影语义钉死测试（1号位 2026-10-25 核证回执 §五 点名）。
///
/// 【为什么必须有这组测试】这张表直接决定每道 Stage 报多少加工量；错一次就是产能负载错一次
///   （既有缺陷正是「一道 Stage 的量全 Stage 复用」＋「净量只除一次良率」）。
///   本组逐条钉住：①逐段由后向前反算、上段净产出 = 下段投入；②终点 = `RequiredStageCode`，解析不出即不出行；
///   ③起点 = `StartStageCode`，之前的段不报；④键是需求键（同物料两片不得合并）；⑤无良率规则不放大。
/// </summary>
public class DemandStageProcessQuantityProjectorTests
{
    private const int MaterialId = 5001;

    private static LogicalProductionDemand Demand(
        string key, decimal netQty, string? startStage = null, string? requiredStage = null,
        int materialId = MaterialId, bool isContinuation = false)
        => new()
        {
            LogicalDemandKey = key,
            MaterialId = materialId,
            NetOutputQty = netQty,
            StartStageCode = startStage ?? string.Empty,
            RequiredStageCode = requiredStage,
            IsContinuation = isContinuation
        };

    private static StageSequenceChain Chain(params (string Code, int Seq)[] steps)
        => new()
        {
            MaterialId = MaterialId,
            Stages = steps.Select(s => new StageSequenceStep { StageCode = s.Code, StageSeq = s.Seq }).ToList()
        };

    private static FunctionFactory Yields(params (string Code, decimal Pct)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Code, p => p.Pct, StringComparer.OrdinalIgnoreCase);
        return new FunctionFactory(map);
    }

    /// <summary>把 (StageCode → 良率) 包成投影要的委托；未配置的 Stage 返回 100（不放大），来源记「缺配」。</summary>
    private sealed class FunctionFactory
    {
        private readonly Dictionary<string, decimal> _map;
        public FunctionFactory(Dictionary<string, decimal> map) => _map = map;

        /// <summary>命中表里有的 Stage ⇒ 视为精确命中；表里没有 ⇒ 100 ＋「缺配」。</summary>
        public YieldResolution Invoke(int materialId, string stageCode)
            => _map.TryGetValue(stageCode, out var pct)
                ? new YieldResolution(pct, YieldMatchLevel.STAGE_EXACT)
                : new YieldResolution(PlanningYieldResolver.NoLoss, YieldMatchLevel.NO_CONFIG);
    }

    private static DemandStageProcessQuantityProjector.Result Project(
        IReadOnlyList<LogicalProductionDemand> demands,
        IReadOnlyList<StageSequenceChain> chains,
        FunctionFactory? yields = null)
        => DemandStageProcessQuantityProjector.Project(
            demands, chains, yields is null ? null : yields.Invoke);

    private static Dictionary<string, decimal> ByStage(
        DemandStageProcessQuantityProjector.Result r, string demandKey)
        => r.Rows.Where(x => x.LogicalDemandKey == demandKey)
            .ToDictionary(x => x.StageCode, x => x.PlannedProcessQty);

    // ── ① 单 Stage：投入 = 净量 ÷ 该段良率 ───────────────────────────────

    [Fact]
    public void 单Stage链_投入等于净量除该段良率()
    {
        // ⚠ 本条**不**断言「与既有 PlannedProcessQty 同值」：夹具是**按 Stage 给良率**的，
        //   而需求级 `PlannedProcessQty` 走的 `SupplyPool.PlanningYieldPercent` 会跳过 Stage 级规则、
        //   返回 100 ⇒ 旧值是 100，与本条的 102.0408 不同。
        //   「同式同舍入」恒真、「同值」只在「无 Stage 级规则且物料级规则无冲突重复」时成立
        //   （1号位 2026-10-25 G-1 回执 §三）。同值那半条由 `单Stage链_仅物料级良率_与需求级口径同值` 钉。
        var r = Project(
            new[] { Demand("D1", 100m) },
            new[] { Chain(("CN_MACH", 1)) },
            Yields(("CN_MACH", 98m)));

        ByStage(r, "D1")["CN_MACH"].Should().Be(102.0408m, "100 × 100 ÷ 98，4 位小数");
        r.Rows.Should().HaveCount(1);
        r.MultiStageDemandCount.Should().Be(0);
    }

    [Fact]
    public void 单Stage链_仅物料级良率_与需求级口径同值()
    {
        // 「同值」的真回归：良率**只配物料级**（无 Stage 级规则、无重复）⇒ 本投影与需求级
        //   `PlannedProcessQty` 的算式/取值**应当一致**。需求级口径见 `PeggingOrchestrator.PlanningYieldPercent`：
        //   `Math.Round(净量 × 100 / 良率, 4)`，良率 >= 100 不放大。
        //   （`SupplyPool` 是 `PeggingOrchestrator` 的私有嵌套类，单测无法直接调它，故按其口径重算后比对。）
        const decimal netQty = 100m;
        const decimal materialYield = 94m;

        var r = Project(
            new[] { Demand("D1", netQty) },
            new[] { Chain(("CN_MACH", 1)) },
            Yields(("CN_MACH", materialYield)));

        var expected = Math.Round(netQty * 100m / materialYield, 4);
        ByStage(r, "D1")["CN_MACH"].Should().Be(expected, "无 Stage 级规则时与需求级 PlannedProcessQty 同式同值");
    }

    // ── ② 多 Stage：逐段反算，上段净产出＝下段投入 ─────────────────────────

    [Fact]
    public void 两级链_逐段反算_首段投入等于净量除两段良率连乘()
    {
        // 净 100，终点 CN_SURF 良率 90%，前段 CN_MACH 良率 95%
        //   CN_SURF 投入 = 100 ÷ 0.90 = 111.1111；CN_MACH 净产出 = 111.1111，投入 = 111.1111 ÷ 0.95 = 116.9591
        var r = Project(
            new[] { Demand("D1", 100m) },
            new[] { Chain(("CN_MACH", 1), ("CN_SURF", 2)) },
            Yields(("CN_MACH", 95m), ("CN_SURF", 90m)));

        var qty = ByStage(r, "D1");
        qty["CN_SURF"].Should().Be(111.1111m);
        qty["CN_MACH"].Should().Be(116.9591m);
        r.MultiStageDemandCount.Should().Be(1);
        // 诊断值保留原始精度（日志里再舍入），此处按 6 位小数比对
        r.MaxInflationRatio.Should().BeApproximately(1.169591m, 0.000001m,
            "首段投入 116.9591 ÷ 净量 100");
    }

    [Fact]
    public void 三段链_不得只折一次良率()
    {
        var r = Project(
            new[] { Demand("D1", 100m) },
            new[] { Chain(("S1", 1), ("S2", 2), ("S3", 3)) },
            Yields(("S1", 90m), ("S2", 90m), ("S3", 90m)));

        var qty = ByStage(r, "D1");
        qty["S3"].Should().Be(111.1111m);
        qty["S2"].Should().Be(123.4568m, "111.1111 ÷ 0.9");
        qty["S1"].Should().Be(137.1742m, "123.4568 ÷ 0.9");
        qty["S1"].Should().NotBe(111.1111m, "只折一次良率正是本轮要修的缺陷");
    }

    // ── ③ 终点 = RequiredStageCode ──────────────────────────────────────

    [Fact]
    public void 有供给阈值Stage_只出到该段且以该段为终点()
    {
        var r = Project(
            new[] { Demand("D1", 100m, requiredStage: "CN_SURF") },
            new[] { Chain(("CN_MACH", 1), ("CN_SURF", 2), ("CN_ASSY", 3)) },
            Yields(("CN_SURF", 80m), ("CN_ASSY", 50m)));

        var qty = ByStage(r, "D1");
        qty.Keys.Should().BeEquivalentTo(new[] { "CN_MACH", "CN_SURF" }, "做到 CN_SURF 即终点，不含 CN_ASSY");
        qty["CN_SURF"].Should().Be(125m, "100 ÷ 0.8；终点段的良率要折，下一段的不折");
    }

    [Fact]
    public void 阈值Stage不在链上_不出行且计数_不退回链尾把量算大()
    {
        var r = Project(
            new[] { Demand("D1", 100m, requiredStage: "NOT_IN_CHAIN") },
            new[] { Chain(("CN_MACH", 1), ("CN_SURF", 2)) },
            Yields(("CN_MACH", 90m), ("CN_SURF", 90m)));

        r.Rows.Should().BeEmpty("解析不出终点 ⇒ 宁可不出行，也不按链尾算");
        r.DemandsUnresolvedRange.Should().Be(1);
        r.DemandsWithChain.Should().Be(1);
    }

    // ── ④ 起点 = StartStageCode ─────────────────────────────────────────

    [Fact]
    public void 起点在中段_之前的Stage不出行()
    {
        var r = Project(
            new[] { Demand("D1", 100m, startStage: "CN_SURF") },
            new[] { Chain(("CN_MACH", 1), ("CN_SURF", 2), ("CN_ASSY", 3)) },
            Yields(("CN_SURF", 100m)));

        var qty = ByStage(r, "D1");
        qty.Keys.Should().BeEquivalentTo(new[] { "CN_SURF", "CN_ASSY" });
        qty["CN_ASSY"].Should().Be(100m);
        qty["CN_SURF"].Should().Be(100m);
    }

    [Fact]
    public void 起点晚于终点_不出行且计数()
    {
        var r = Project(
            new[] { Demand("D1", 100m, startStage: "CN_ASSY", requiredStage: "CN_SURF") },
            new[] { Chain(("CN_MACH", 1), ("CN_SURF", 2), ("CN_ASSY", 3)) });

        r.Rows.Should().BeEmpty();
        r.DemandsUnresolvedRange.Should().Be(1);
    }

    // ── ④b 起点落在链中段的诊断（既有 StartStageCode 口径风险，只数不改） ──

    [Fact]
    public void 非续排需求起点在链中段_计数()
    {
        var r = Project(
            new[] { Demand("D1", 100m, startStage: "CN_SURF") },
            new[] { Chain(("CN_MACH", 1), ("CN_SURF", 2), ("CN_ASSY", 3)) });

        r.NonContinuationMidChainDemandCount.Should().Be(1,
            "新单本应从链首起算；落到中段说明 StartStageCode 取值可疑，须能被数出来");
    }

    [Fact]
    public void 续排需求起点在链中段_不计入该诊断()
    {
        var r = Project(
            new[] { Demand("D1", 100m, startStage: "CN_SURF", isContinuation: true) },
            new[] { Chain(("CN_MACH", 1), ("CN_SURF", 2), ("CN_ASSY", 3)) });

        r.NonContinuationMidChainDemandCount.Should().Be(0, "续排本来就从中段起，不是异常");
    }

    // ── ⑤ 键是需求键：同物料两片不合并 ──────────────────────────────────

    [Fact]
    public void 同物料两个需求片_各自成组不得合并成一个量()
    {
        var r = Project(
            new[] { Demand("D1", 100m), Demand("D2", 40m) },
            new[] { Chain(("CN_MACH", 1), ("CN_SURF", 2)) },
            Yields(("CN_MACH", 90m), ("CN_SURF", 90m)));

        r.Rows.Should().HaveCount(4);
        ByStage(r, "D1")["CN_MACH"].Should().Be(123.4568m);
        ByStage(r, "D2")["CN_MACH"].Should().Be(49.3827m, "按各自净量算，不合并成 140 的一段");
    }

    // ── ⑥ 无链 / 净量≤0 ────────────────────────────────────────────────

    [Fact]
    public void 无Stage链的需求_不出行且计数()
    {
        var r = Project(
            new[] { Demand("D1", 100m, materialId: 9999) },
            new[] { Chain(("CN_MACH", 1)) });

        r.Rows.Should().BeEmpty("采购件 / 无 StagePath ⇒ 没有逐 Stage 量可给，调用侧沿用需求级值");
        r.DemandsWithoutChain.Should().Be(1);
        r.DemandsWithChain.Should().Be(0);
    }

    [Fact]
    public void 净量非正_不出行且计数()
    {
        var r = Project(
            new[] { Demand("D1", 0m) },
            new[] { Chain(("CN_MACH", 1), ("CN_SURF", 2)) });

        r.Rows.Should().BeEmpty("不造零量 Stage");
        r.DemandsNonPositiveQty.Should().Be(1);
    }

    // ── ⑦ 良率未配置 / 越界：不放大 ──────────────────────────────────────

    [Fact]
    public void 无任何良率规则_各段投入等于净量_不放大()
    {
        var r = Project(
            new[] { Demand("D1", 100m) },
            new[] { Chain(("CN_MACH", 1), ("CN_SURF", 2)) });

        var qty = ByStage(r, "D1");
        qty["CN_MACH"].Should().Be(100m);
        qty["CN_SURF"].Should().Be(100m);
        r.MaxInflationRatio.Should().Be(1m);
    }

    [Fact]
    public void 良率越界_按不放大处理()
    {
        var r = Project(
            new[] { Demand("D1", 100m) },
            new[] { Chain(("CN_MACH", 1)) },
            Yields(("CN_MACH", 0m)));

        ByStage(r, "D1")["CN_MACH"].Should().Be(100m);
    }

    // ── ⑧ 同物料多链：只取第一条 ────────────────────────────────────────

    [Fact]
    public void 同物料出现两条链_取第一条并计数()
    {
        var second = new StageSequenceChain
        {
            MaterialId = MaterialId,
            Stages = new[] { new StageSequenceStep { StageCode = "OTHER", StageSeq = 1 } }
        };

        var r = Project(
            new[] { Demand("D1", 100m) },
            new[] { Chain(("CN_MACH", 1)), second });

        ByStage(r, "D1").Keys.Should().BeEquivalentTo(new[] { "CN_MACH" });
        r.DuplicateChainMaterialCount.Should().Be(1);
    }

    [Fact]
    public void 链内乱序时按StageSeq排序()
    {
        var r = Project(
            new[] { Demand("D1", 100m, requiredStage: "CN_SURF") },
            new[] { Chain(("CN_SURF", 2), ("CN_MACH", 1)) },
            Yields(("CN_MACH", 50m)));

        var qty = ByStage(r, "D1");
        qty["CN_SURF"].Should().Be(100m);
        qty["CN_MACH"].Should().Be(200m, "上段净产出 100 ÷ 0.5");
    }

    // ── ⑨ 良率来源标记（R04 Y-02／Y-03） ────────────────────────────────

    [Fact]
    public void 良率来源_四类逐Stage行计数且合计等于行数()
    {
        var r = DemandStageProcessQuantityProjector.Project(
            new[] { Demand("D1", 100m), Demand("D2", 100m) },
            new[] { Chain(("S1", 1), ("S2", 2)) },
            (_, stageCode) => stageCode switch
            {
                "S1" => new YieldResolution(95m, YieldMatchLevel.STAGE_EXACT),
                "S2" => new YieldResolution(90m, YieldMatchLevel.MATERIAL_DEFAULT),
                "S3" => new YieldResolution(100m, YieldMatchLevel.OUT_OF_RANGE),
                _ => new YieldResolution(100m, YieldMatchLevel.NO_CONFIG)
            });

        r.Rows.Should().HaveCount(4);
        r.YieldStageExactRowCount.Should().Be(2, "两条需求的 S1 都是精确命中");
        r.YieldMaterialDefaultRowCount.Should().Be(2, "两条需求的 S2 都回退到物料级默认");
        r.YieldOutOfRangeRowCount.Should().Be(0);
        r.YieldNoConfigRowCount.Should().Be(0);

        (r.YieldStageExactRowCount + r.YieldMaterialDefaultRowCount
            + r.YieldOutOfRangeRowCount + r.YieldNoConfigRowCount)
            .Should().Be(r.Rows.Count, "四类合计必须等于行数，不等就是有行没被归类（日志会漏真相）");
    }

    [Fact]
    public void 良率来源_回调给出越界值_按100并记为越界而非精确命中()
    {
        var r = DemandStageProcessQuantityProjector.Project(
            new[] { Demand("D1", 100m) },
            new[] { Chain(("S1", 1)) },
            (_, _) => new YieldResolution(0m, YieldMatchLevel.STAGE_EXACT));

        ByStage(r, "D1")["S1"].Should().Be(100m, "越界不放大，与旧行为一致");
        r.YieldOutOfRangeRowCount.Should().Be(1,
            "取值不能用时来源标记不能继续写「精确命中」，否则日志会替一条坏规则背书");
        r.YieldStageExactRowCount.Should().Be(0);
    }

    [Fact]
    public void 良率来源_非精确命中留样例_精确命中的不入样例()
    {
        var r = Project(
            new[] { Demand("D1", 100m) },
            new[] { Chain(("S1", 1), ("S2", 2)) },
            Yields(("S1", 95m)));

        r.YieldFallbackSamples.Should().ContainSingle("只有 S2 不是精确命中");
        var sample = r.YieldFallbackSamples[0];
        sample.LogicalDemandKey.Should().Be("D1");
        sample.StageCode.Should().Be("S2");
        sample.Level.Should().Be(YieldMatchLevel.NO_CONFIG);
        sample.YieldPercent.Should().Be(100m);
    }

    [Fact]
    public void 良率来源_样例有上限但计数是全量()
    {
        var demands = Enumerable.Range(0, 20).Select(i => Demand($"D{i}", 100m)).ToArray();

        var r = Project(demands, new[] { Chain(("S1", 1)) });

        r.YieldFallbackSamples.Should().HaveCount(DemandStageProcessQuantityProjector.MaxYieldFallbackSamples,
            "样例只留痕，不能跟着需求数无限长");
        r.YieldNoConfigRowCount.Should().Be(20, "计数是逐 Stage 行全量，不受样例上限影响");
        r.Rows.Should().HaveCount(20);
    }

    // ── 行数上限（1号位 2026-10-25 提请：撞上限后要继续空转计数，把真实行数打出来）──

    [Fact]
    public void 未截断时_真实行数等于载荷行数()
    {
        var r = DemandStageProcessQuantityProjector.Project(
            new[] { Demand("D1", 100m), Demand("D2", 100m) },
            new[] { Chain(("S1", 1), ("S2", 2)) },
            null);

        r.Truncated.Should().BeFalse();
        r.Rows.Should().HaveCount(4);
        r.TotalRowCount.Should().Be(4, "未截断时两者必须恒等，否则日志里的「真实应有」会无故虚高");
    }

    [Fact]
    public void 撞上限时_载荷被截断但真实行数照实数完()
    {
        // 4 条需求 × 2 段 = 应有 8 行，上限设 3 ⇒ 载荷 3 行、真实 8 行。
        var demands = Enumerable.Range(0, 4).Select(i => Demand($"D{i}", 100m)).ToArray();

        var r = DemandStageProcessQuantityProjector.Project(
            demands, new[] { Chain(("S1", 1), ("S2", 2)) }, null, maxRowCount: 3);

        r.Truncated.Should().BeTrue();
        r.Rows.Should().HaveCount(3, "载荷仍严格封顶，不得因为要计数就把行加进来");
        r.TotalRowCount.Should().Be(8,
            "旧实现在撞上限时直接 break，真实行数无从判断 —— 这正是本次要钉住的行为");
    }

    [Fact]
    public void 撞上限后_截断点之后的需求仍被计数()
    {
        // 上限 1 ⇒ D0 入载荷、D1 被截。若实现仍在撞上限后 break，
        //   排在 D1 之后的「无链 / 净量≤0 / 范围解析不出」三类计数都会停，诊断就会少报。
        var demands = new[]
        {
            Demand("D0", 100m),                          // 入载荷 1 行（S1）
            Demand("D1", 100m),                          // 同链第 2 行 ⇒ 撞上限被截
            Demand("MISSING", 100m, materialId: 9999),   // 无链
            Demand("NEG", 0m),                           // 净量 ≤ 0
            Demand("BAD", 100m, requiredStage: "NOPE")   // 范围解析不出
        };

        var r = DemandStageProcessQuantityProjector.Project(
            demands, new[] { Chain(("S1", 1)) }, null, maxRowCount: 1);

        r.Truncated.Should().BeTrue();
        r.Rows.Should().HaveCount(1);
        r.TotalRowCount.Should().Be(2);
        r.DemandsWithoutChain.Should().Be(1, "撞上限不该中断后面的诊断计数");
        r.DemandsNonPositiveQty.Should().Be(1);
        r.DemandsUnresolvedRange.Should().Be(1);
    }

    [Fact]
    public void 撞上限时_良率来源四类合计仍等于载荷行数()
    {
        // 上限 2：D0 两段都入载荷（S1 精确、S2 缺配），D1 一行都不入。
        var demands = new[] { Demand("D0", 100m), Demand("D1", 100m) };

        var r = DemandStageProcessQuantityProjector.Project(
            demands,
            new[] { Chain(("S1", 1), ("S2", 2)) },
            Yields(("S1", 95m)).Invoke,
            maxRowCount: 2);

        r.Rows.Should().HaveCount(2);
        (r.YieldStageExactRowCount + r.YieldMaterialDefaultRowCount
            + r.YieldOutOfRangeRowCount + r.YieldNoConfigRowCount)
            .Should().Be(2, "「四类合计 = 载荷行数」这条既有不变量不得因截断而破");
        r.YieldFallbackSamples.Should().OnlyContain(s => s.LogicalDemandKey == "D0",
            "未入载荷的行不得留样例，否则样例会指认一条根本没有交付的 Stage 量");
    }
}

/// <summary>
/// 分 Stage 良率三级解析（`PlanningYieldResolver.Resolve`）—— G-1 唯一的参数取值口径。
/// 3号位 2026-10-23 五项会签回执第 2 项：精确 → 物料级默认 → 100；重复键运行时取最低。
///
/// 【类名曾是 `ResolvePlanningYieldTests`】那个名字与**不存在的方法** `SupplyPool.ResolvePlanningYield` 同名，
///   会把读者引到 `SupplyPool.PlanningYieldPercent` 去 —— 那套**跳过 Stage 级、命中第一条即返回**，
///   正好丢掉 G-1 最核心的那一半（1号位 2026-10-25 G-1 回执 §二）。
/// </summary>
public class PlanningYieldResolverTests
{
    private static PlanningYieldRule Rule(string material, string? stage, decimal pct)
        => new() { MaterialId = material, StageCode = stage, YieldPercent = pct };

    [Fact]
    public void 精确Stage规则优先于物料级默认()
    {
        var yields = new[] { Rule("M1", null, 95m), Rule("M1", "CN_SURF", 80m) };

        PlanningYieldResolver.Resolve(yields, "M1", "CN_SURF").Should().Be(80m);
        PlanningYieldResolver.Resolve(yields, "M1", "CN_MACH").Should().Be(95m, "其它段落物料级默认");
        PlanningYieldResolver.Resolve(yields, "M1", null).Should().Be(95m);
    }

    [Fact]
    public void 都没配置_按100不放大()
    {
        PlanningYieldResolver.Resolve(Array.Empty<PlanningYieldRule>(), "M1", "CN_SURF").Should().Be(100m);
        PlanningYieldResolver.Resolve(null, "M1", "CN_SURF").Should().Be(100m);
        PlanningYieldResolver.Resolve(new[] { Rule("M2", null, 90m) }, "M1", "CN_SURF").Should().Be(100m,
            "规则属于别的物料，不得串用");
    }

    [Fact]
    public void 重复键_取最低良率兜底()
    {
        var yields = new[] { Rule("M1", "CN_SURF", 90m), Rule("M1", "CN_SURF", 70m) };

        PlanningYieldResolver.Resolve(yields, "M1", "CN_SURF").Should().Be(70m);
    }

    [Fact]
    public void 越界值当作未配置_不得静默放大且不得覆盖合法规则()
    {
        var yields = new[] { Rule("M1", "CN_SURF", 0m), Rule("M1", "CN_SURF", 120m) };

        PlanningYieldResolver.Resolve(yields, "M1", "CN_SURF").Should().Be(100m);
    }

    [Fact]
    public void 物料编码比较忽略大小写()
    {
        PlanningYieldResolver.Resolve(new[] { Rule("abc123", null, 90m) }, "ABC123", "S1").Should().Be(90m);
    }

    // ── 来源等级（R04 Y-02／Y-03）：数值不变，只多一个「这数从哪来」 ─────────

    [Fact]
    public void 来源_精确命中Stage级_标为STAGE_EXACT()
    {
        var yields = new[] { Rule("M1", null, 95m), Rule("M1", "CN_SURF", 80m) };

        var res = PlanningYieldResolver.ResolveWithSource(yields, "M1", "CN_SURF");

        res.Percent.Should().Be(80m);
        res.Level.Should().Be(YieldMatchLevel.STAGE_EXACT);
    }

    [Fact]
    public void 来源_只命中物料级_标为MATERIAL_DEFAULT()
    {
        var res = PlanningYieldResolver.ResolveWithSource(new[] { Rule("M1", null, 95m) }, "M1", "CN_SURF");

        res.Percent.Should().Be(95m);
        res.Level.Should().Be(YieldMatchLevel.MATERIAL_DEFAULT,
            "Y-02 要求显性标出「这条是回退来的」，否则分不清业务规则与兜底");
    }

    [Fact]
    public void 来源_规则在但取值越界_标为OUT_OF_RANGE且取100()
    {
        var res = PlanningYieldResolver.ResolveWithSource(
            new[] { Rule("M1", "CN_SURF", 0m), Rule("M1", "CN_SURF", 120m) }, "M1", "CN_SURF");

        res.Percent.Should().Be(100m);
        res.Level.Should().Be(YieldMatchLevel.OUT_OF_RANGE,
            "「配了但不能用」与「没配」是两回事，前者要回查发布侧");
    }

    [Fact]
    public void 来源_越界规则不得掩盖同键合法规则()
    {
        var yields = new[] { Rule("M1", "CN_SURF", 120m), Rule("M1", "CN_SURF", 88m) };

        var res = PlanningYieldResolver.ResolveWithSource(yields, "M1", "CN_SURF");

        res.Percent.Should().Be(88m, "有合法行时按合法行取最低");
        res.Level.Should().Be(YieldMatchLevel.STAGE_EXACT);
    }

    [Fact]
    public void 来源_该物料一条规则都没有_标为NO_CONFIG()
    {
        PlanningYieldResolver.ResolveWithSource(Array.Empty<PlanningYieldRule>(), "M1", "S1")
            .Level.Should().Be(YieldMatchLevel.NO_CONFIG);
        PlanningYieldResolver.ResolveWithSource(null, "M1", "S1")
            .Level.Should().Be(YieldMatchLevel.NO_CONFIG);
        PlanningYieldResolver.ResolveWithSource(new[] { Rule("M2", null, 90m) }, "M1", "S1")
            .Level.Should().Be(YieldMatchLevel.NO_CONFIG, "规则属于别的物料 ⇒ 对本物料等同缺配");
    }

    [Fact]
    public void 来源_物料编码为空_标为NO_CONFIG()
    {
        PlanningYieldResolver.ResolveWithSource(new[] { Rule("M1", null, 90m) }, null, "S1")
            .Level.Should().Be(YieldMatchLevel.NO_CONFIG);
    }
}
