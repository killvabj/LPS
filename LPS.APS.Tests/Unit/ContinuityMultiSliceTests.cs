using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 多片执行起点（`ExistingExecutionContextDto.Slices` N&gt;1）的分桶语义（2026-11-26）。
///
/// <para><b>背景</b>：5号位 `r14476` 起，一个 MES 工单可产出多片（C13 实测 `[{J101,81},{J102,3}]`），
/// 并按「N=1 镜像唯一片、N&gt;1 工单级起点置 null」做兼容投影。
/// 若我方仍只读工单级单值 ⇒ N&gt;1 时起点为 null ⇒ 整单塌成一片：
/// 要么被第二趟回填成第一片（84 全挂 NC），要么留 null 被 1号位整单 Fail Closed。两种都不对。</para>
///
/// <para><b>本类钉死三件事</b>：①多片必须逐片产出、每片带自己的起点与量；
/// ②各片量之和恒等于该工单分到的份额（不放大不丢失）；③单片行为逐字不变（键与量都不变，零回归）。</para>
/// </summary>
public class ContinuityMultiSliceTests
{
    private static LogicalProductionDemand Demand(string key, decimal netQty, string? pi = "PI1")
        => new()
        {
            LogicalDemandKey        = key,
            PlanVersionId           = 999,
            DomainKey               = "FAMILY_FRL",
            AllocationSequence      = 1,
            DemandKey               = "D1",
            OrderId                 = 100L,
            MaterialId              = 998211,
            FactoryId               = 3,
            FactoryCode             = "BJ",
            StartStageCode          = "BJ_MACH",
            NetOutputQty            = netQty,
            PlannedProcessQty       = netQty,
            ProductionInstructionNo = pi,
            IsContinuation          = false
        };

    private static ExistingExecutionSliceDto Slice(string stage, string? opCode, decimal qty, string? opName = null)
        => new() { StartStageCode = stage, StartOperationCode = opCode, StartOperationName = opName, SliceQty = qty };

    private static ExistingExecutionContextDto Ctx(
        string wo, decimal remaining, IReadOnlyList<ExistingExecutionSliceDto>? slices,
        string? startOp = null, string? startOpName = null, string stage = "BJ_MACH")
        => new()
        {
            ProductionInstructionNo = "PI1",
            MESWorkOrderNo          = wo,
            WorkOrderStatus         = "IN_PROGRESS",
            StartStageCode          = stage,
            StartOperationCode      = startOp,
            StartOperationName      = startOpName,
            RouteCode               = "NC,清洗,精修",
            PathId                  = 1,
            DerivedRemainingQty     = remaining,
            DataCutoffTime          = DateTime.Now,
            Slices                  = slices ?? Array.Empty<ExistingExecutionSliceDto>()
        };

    // ── ① C13 形态：两片必须各自成条，起点各自带自己的 ─────────────────────────
    [Fact]
    public void 多片工单_逐片产出_起点与量各自独立()
    {
        var ctx = Ctx("WO1", 84m,
            new[] { Slice("BJ_MACH", "J101", 81m), Slice("BJ_MACH", "J102", 3m) });
        var byPi = new Dictionary<string, IReadOnlyList<ExistingExecutionContextDto>>(StringComparer.Ordinal)
        {
            ["PI1"] = new[] { ctx }
        };

        var r = PeggingOrchestrator.BucketContinuityShares(
            new List<LogicalProductionDemand> { Demand("SRC", 84m) }, byPi);

        r.Where(d => d.IsContinuation).Should().HaveCount(2,
            "一个工单两片 ⇒ 必须两条连续份额；塌成一条就会让其中一批件挂错工序");
        r.Select(d => d.StartOperationCode).Should().Equal("J101", "J102");
        r.Select(d => d.NetOutputQty).Should().Equal(new[] { 81m, 3m }, "每片各自的数量");
        r.Sum(d => d.NetOutputQty).Should().Be(84m, "各片之和必须等于该工单分到的份额");
    }

    // ── ② 多片之间键要能区分，否则下游按 LogicalDemandKey 做身份会撞 ──────────────
    [Fact]
    public void 多片_需求键带片后缀且互不相同()
    {
        var ctx = Ctx("WO1", 84m,
            new[] { Slice("BJ_MACH", "J101", 81m), Slice("BJ_MACH", "J102", 3m) });
        var byPi = new Dictionary<string, IReadOnlyList<ExistingExecutionContextDto>>(StringComparer.Ordinal)
        {
            ["PI1"] = new[] { ctx }
        };

        var r = PeggingOrchestrator.BucketContinuityShares(
            new List<LogicalProductionDemand> { Demand("SRC", 84m) }, byPi)
            .Where(d => d.IsContinuation).ToList();

        r.Select(d => d.LogicalDemandKey).Should().BeEquivalentTo("SRC/WO:WO1#S1", "SRC/WO:WO1#S2");
        r.Select(d => d.LogicalDemandKey).Distinct().Should().HaveCount(2, "同一需求下两片不得共用一个键");
    }

    // ── ③ 同工单的所有片共享同一个 ContinuationKey（06 §18.3）───────────────────
    [Fact]
    public void 多片_共享同一ContinuationKey()
    {
        var ctx = Ctx("WO1", 84m,
            new[] { Slice("BJ_MACH", "J101", 81m), Slice("BJ_MACH", "J102", 3m) });
        var byPi = new Dictionary<string, IReadOnlyList<ExistingExecutionContextDto>>(StringComparer.Ordinal)
        {
            ["PI1"] = new[] { ctx }
        };

        var r = PeggingOrchestrator.BucketContinuityShares(
            new List<LogicalProductionDemand> { Demand("SRC", 84m) }, byPi)
            .Where(d => d.IsContinuation).ToList();

        r.Should().OnlyContain(d => !string.IsNullOrEmpty(d.ContinuationKey));
        r.Select(d => d.ContinuationKey).Distinct().Should().HaveCount(1,
            "同一 MESWorkOrderNo 的所有 Slices 共享同一 ContinuationKey，不得因分片而裂开");
    }

    // ── ④ 单片：逐字旧行为（键不带后缀、量不变）────────────────────────────────
    [Fact]
    public void 单片工单_行为逐字不变()
    {
        var ctx = Ctx("WO1", 50m, new[] { Slice("BJ_MACH", "J101", 50m) }, startOp: "J101");
        var byPi = new Dictionary<string, IReadOnlyList<ExistingExecutionContextDto>>(StringComparer.Ordinal)
        {
            ["PI1"] = new[] { ctx }
        };

        var r = PeggingOrchestrator.BucketContinuityShares(
            new List<LogicalProductionDemand> { Demand("SRC", 50m) }, byPi)
            .Where(d => d.IsContinuation).ToList();

        r.Should().ContainSingle();
        r[0].LogicalDemandKey.Should().Be("SRC/WO:WO1", "单片沿用原键，不得引入 #S 后缀");
        r[0].NetOutputQty.Should().Be(50m);
        r[0].StartOperationCode.Should().Be("J101");
    }

    // ── ⑤ 无 Slices（旧载荷）：仍按工单级单值出片 ────────────────────────────────
    [Fact]
    public void 无Slices数组_退回工单级兼容投影()
    {
        var ctx = Ctx("WO1", 30m, slices: null, startOp: "J101");
        var byPi = new Dictionary<string, IReadOnlyList<ExistingExecutionContextDto>>(StringComparer.Ordinal)
        {
            ["PI1"] = new[] { ctx }
        };

        var r = PeggingOrchestrator.BucketContinuityShares(
            new List<LogicalProductionDemand> { Demand("SRC", 30m) }, byPi)
            .Where(d => d.IsContinuation).ToList();

        r.Should().ContainSingle();
        r[0].LogicalDemandKey.Should().Be("SRC/WO:WO1");
        r[0].StartOperationCode.Should().Be("J101");
    }

    // ── ⑥ 片量之和与工单份额不等时，按片比例分摊且合计不放大 ────────────────────
    [Fact]
    public void 片量基数与份额不等_按比例分摊且合计等于份额()
    {
        // 上下文给 84，但片量写的是 162/6（比如上游口径变化）⇒ 我方必须按比例收敛到 84，不得输出 168
        var ctx = Ctx("WO1", 84m,
            new[] { Slice("BJ_MACH", "J101", 162m), Slice("BJ_MACH", "J102", 6m) });
        var byPi = new Dictionary<string, IReadOnlyList<ExistingExecutionContextDto>>(StringComparer.Ordinal)
        {
            ["PI1"] = new[] { ctx }
        };

        var r = PeggingOrchestrator.BucketContinuityShares(
            new List<LogicalProductionDemand> { Demand("SRC", 84m) }, byPi)
            .Where(d => d.IsContinuation).ToList();

        r.Sum(d => d.NetOutputQty).Should().Be(84m, "无论片量基数如何，合计恒等于该工单分到的份额");
        r.Should().HaveCount(2);
    }

    // ── ⑦ 工单号提取必须剥离片后缀，否则 TaskNo 绑定拿到非法号 ───────────────────
    [Theory]
    [InlineData("SRC/WO:15326666123027", "15326666123027")]
    [InlineData("SRC/WO:15326666123027#S2", "15326666123027")]
    [InlineData("SRC/WO:WO1#S1", "WO1")]
    [InlineData("SRC/FREE", null)]
    [InlineData(null, null)]
    public void 工单号提取_剥离片后缀(string? key, string? expected)
    {
        PeggingOrchestrator.ExtractMesWorkOrderNo(key).Should().Be(expected);
    }
}
