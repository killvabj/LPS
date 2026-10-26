using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.BusinessRules.Models;
using LPS.APS.Core.Dto;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// AvailableTime 纯函数计算测试（2号位阶段 3）
/// 契约：AvailableTime = EtaInvariant.Resolve(Manual, ERP, ReleaseDate+DefaultLT) + ArrivalToUsableOffset。
/// 本测试用「模拟数据」验证三级优先级链与 Offset 修正，不依赖 5号位真实数据源
/// （当前 ODS 未透出 ReleaseDate、Pipeline 空转，故以构造的假事实驱动逻辑验证）。
/// </summary>
public class AvailableTimeCalculatorTests
{
    /// <summary>
    /// 默认截止时点：早于下面所有用例的 ETA ⇒ 三级链用例全部落在「未逾期」分支，
    /// 与加重估之前的期望值逐位一致（重估只对逾期生效，不得改动既有未逾期结果）。
    /// </summary>
    private static readonly DateTime Cutoff = new(2026, 9, 1);

    private static FrozenStrategySnapshot Snapshot(
        PurchaseLtRule[]? ltRules = null,
        WarehouseOffsetRule[]? offsets = null,
        decimal marginPercent = 0m)
        => new()
        {
            Procurement = new ProcurementBlock
            {
                DefaultPurchaseLt = ltRules ?? [],
                ArrivalToUsableOffsets = offsets ?? [],
                OverdueMargin = new OverdueMarginParams { MarginPercent = marginPercent }
            }
        };

    private static RawProcurementFact Fact(
        string lineNo = "10",
        string storageCode = "WH-A",
        DateTime? eta = null,
        DateTime? releaseDate = null,
        int materialId = 100,
        string supplyType = "PURCHASE_IN_TRANSIT")
        => new()
        {
            SupplyType = supplyType,
            MaterialId = materialId,
            MaterialCode = "MAT-100",
            FactoryId = 1,
            FactoryCode = "F1",
            StorageCode = storageCode,
            RemainingQty = 50m,
            Eta = eta,
            ReleaseDate = releaseDate,
            SourceDocumentNo = "PO-001",
            SourceDocumentLineNo = lineNo
        };

    [Fact]
    public void Compute_ManualEta优先_并加Offset()
    {
        var snapshot = Snapshot(
            ltRules: new[] { new PurchaseLtRule { WarehouseCode = "WH-A", DefaultLtDays = 7 } },
            offsets: new[] { new WarehouseOffsetRule { WarehouseCode = "WH-A", OffsetHours = 2 } });
        var map = new Dictionary<(string, int, int, string), DateTime>
        {
            [("PO-001", 10, 100, "WH-A")] = new DateTime(2026, 9, 10, 8, 0, 0)
        };
        var fact = Fact(eta: new DateTime(2026, 9, 5), releaseDate: new DateTime(2026, 9, 1));

        var result = AvailableTimeCalculator.Compute(fact, map, snapshot, Cutoff);

        // Manual(09-10 08:00) 覆盖 ERP(09-05)，再加 2h Offset
        result.Should().Be(new DateTime(2026, 9, 10, 10, 0, 0));
    }

    [Fact]
    public void Compute_Manual为空_回落ERP_并加Offset()
    {
        var snapshot = Snapshot(offsets: new[] { new WarehouseOffsetRule { WarehouseCode = "WH-A", OffsetHours = 3 } });
        var map = new Dictionary<(string, int, int, string), DateTime>();
        var fact = Fact(eta: new DateTime(2026, 9, 5, 0, 0, 0), releaseDate: new DateTime(2026, 9, 1));

        var result = AvailableTimeCalculator.Compute(fact, map, snapshot, Cutoff);

        result.Should().Be(new DateTime(2026, 9, 5, 3, 0, 0));
    }

    [Fact]
    public void Compute_Manual与ERP为空_回落ReleaseDate加DefaultLT()
    {
        var snapshot = Snapshot(
            ltRules: new[] { new PurchaseLtRule { WarehouseCode = "WH-A", DefaultLtDays = 7 } });
        var map = new Dictionary<(string, int, int, string), DateTime>();
        var fact = Fact(eta: null, releaseDate: new DateTime(2026, 9, 1));

        var result = AvailableTimeCalculator.Compute(fact, map, snapshot, Cutoff);

        result.Should().Be(new DateTime(2026, 9, 8));
    }

    [Fact]
    public void Compute_全部为空_返回null()
    {
        var snapshot = Snapshot();
        var map = new Dictionary<(string, int, int, string), DateTime>();
        var fact = Fact(eta: null, releaseDate: null);

        var result = AvailableTimeCalculator.Compute(fact, map, snapshot, Cutoff);

        result.Should().BeNull();
    }

    [Fact]
    public void Compute_DefaultLT_优先Material级规则_再回落Warehouse级默认()
    {
        // Material 级 LT(14 天) 应优先于 Warehouse 级默认(7 天)
        var snapshot = Snapshot(ltRules: new[]
        {
            new PurchaseLtRule { WarehouseCode = "WH-A", MaterialId = null, DefaultLtDays = 7 },
            new PurchaseLtRule { WarehouseCode = "WH-A", MaterialId = "100", DefaultLtDays = 14 }
        });
        var map = new Dictionary<(string, int, int, string), DateTime>();
        var fact = Fact(eta: null, releaseDate: new DateTime(2026, 9, 1), materialId: 100);

        var result = AvailableTimeCalculator.Compute(fact, map, snapshot, Cutoff);

        result.Should().Be(new DateTime(2026, 9, 15));
    }

    [Fact]
    public void Compute_LineNo无法解析_Manual不命中_回落ERP()
    {
        var snapshot = Snapshot();
        var map = new Dictionary<(string, int, int, string), DateTime>
        {
            [("PO-001", 10, 100, "WH-A")] = new DateTime(2026, 9, 10)
        };
        // SourceDocumentLineNo 非数字 → int.TryParse 失败 → Manual 不命中
        var fact = Fact(lineNo: "N/A", eta: new DateTime(2026, 9, 5), releaseDate: null);

        var result = AvailableTimeCalculator.Compute(fact, map, snapshot, Cutoff);

        result.Should().Be(new DateTime(2026, 9, 5));
    }

    // ── Explain：带因留痕（与 Compute 同源，不得漂移） ─────────────────────

    [Fact]
    public void Explain_三级皆空_回报全空但区分出ReleaseDate缺失()
    {
        var snapshot = Snapshot();
        var map = new Dictionary<(string, int, int, string), DateTime>();
        var fact = Fact(eta: null, releaseDate: null);

        var d = AvailableTimeCalculator.Explain(fact, map, snapshot, Cutoff);

        d.AvailableAt.Should().BeNull();
        d.HasManualEta.Should().BeFalse();
        d.HasErpEta.Should().BeFalse();
        d.HasPurchaseLtRule.Should().BeFalse();
        d.HasReleaseDate.Should().BeFalse("无 ReleaseDate 时 DefaultLT 连算都算不了，这一点要与「LT 规则没配」分开报");
        AvailableTimeCalculator.Compute(fact, map, snapshot, Cutoff).Should().Be(d.AvailableAt, "两个入口必须同源");
    }

    [Fact]
    public void Explain_有ReleaseDate但规则仓码对不上_兜底链走不通()
    {
        // 实库口径复刻：规则配的是 WH01，而供给事实的收货仓是 980105 这类数字仓码。
        var snapshot = Snapshot(ltRules: new[]
        {
            new PurchaseLtRule { WarehouseCode = "WH01", DefaultLtDays = 3 }
        });
        var map = new Dictionary<(string, int, int, string), DateTime>();
        var fact = Fact(eta: null, storageCode: "980105", releaseDate: new DateTime(2026, 9, 1));

        var d = AvailableTimeCalculator.Explain(fact, map, snapshot, Cutoff);

        d.AvailableAt.Should().BeNull("仓码不匹配 ⇒ 无 DefaultLT ⇒ 三级全空");
        d.HasPurchaseLtRule.Should().BeFalse("这一格是「规则没命中」，责任在参数治理，不在 ODS");
        d.HasReleaseDate.Should().BeTrue("ReleaseDate 是有的，缺的是规则");
    }

    [Fact]
    public void Explain_规则命中但缺ReleaseDate_只缺参考日()
    {
        var snapshot = Snapshot(ltRules: new[]
        {
            new PurchaseLtRule { WarehouseCode = "WH-A", DefaultLtDays = 7 }
        });
        var map = new Dictionary<(string, int, int, string), DateTime>();
        var fact = Fact(eta: null, releaseDate: null);

        var d = AvailableTimeCalculator.Explain(fact, map, snapshot, Cutoff);

        d.AvailableAt.Should().BeNull();
        d.HasPurchaseLtRule.Should().BeTrue("规则在，缺的是 ReleaseDate");
        d.HasReleaseDate.Should().BeFalse();
    }

    [Fact]
    public void Explain_ERP有值_回报ERP标志且可用时点非空()
    {
        var snapshot = Snapshot();
        var map = new Dictionary<(string, int, int, string), DateTime>();
        var fact = Fact(eta: new DateTime(2026, 9, 5));

        var d = AvailableTimeCalculator.Explain(fact, map, snapshot, Cutoff);

        d.AvailableAt.Should().Be(new DateTime(2026, 9, 5));
        d.HasErpEta.Should().BeTrue();
        d.HasManualEta.Should().BeFalse();
    }

    // ── 逾期重估（0号位 2026-10-25 统一算法 §2）──────────────────────────────

    /// <summary>0号位 件里的示例原样复刻：R=10-25、L=20、p=0.10、H=4h ⇒ ETA*=10-27、AvailableTime=10-27 04:00。</summary>
    [Fact]
    public void Explain_在途逾期_按截止时点加ceilL乘P重估并留痕()
    {
        var snapshot = Snapshot(
            ltRules: new[] { new PurchaseLtRule { WarehouseCode = "WH-A", DefaultLtDays = 20 } },
            offsets: new[] { new WarehouseOffsetRule { WarehouseCode = "WH-A", OffsetHours = 4 } },
            marginPercent: 0.10m);
        var map = new Dictionary<(string, int, int, string), DateTime>();
        var fact = Fact(eta: new DateTime(2026, 10, 10), releaseDate: null, supplyType: "PURCHASE_IN_TRANSIT");
        var cutoff = new DateTime(2026, 10, 25);

        var d = AvailableTimeCalculator.Explain(fact, map, snapshot, cutoff);

        d.Overdue.Should().Be(OverdueEtaOutcome.Recalculated);
        d.RecalculatedExtraDays.Should().Be(2, "ceil(20 × 0.10) = 2 天");
        d.RiskEta.Should().Be(new DateTime(2026, 10, 27), "ETA* = R + 2 天，不含 Offset");
        d.AvailableAt.Should().Be(new DateTime(2026, 10, 27, 4, 0, 0), "Offset 只在 ETA* 之后加一次");
        d.SelectedEta.Should().Be(new DateTime(2026, 10, 10), "WARN 要带原日期");
        d.AppliedMarginPercent.Should().Be(0.10m);
        d.Chain.LegalLeadTimeDays.Should().Be(20);
    }

    [Fact]
    public void Explain_已到厂未入库_逾期不套在途重估_保留原ETA()
    {
        // ARRIVED_NOT_RECEIVED 的 ETA 早于截止时点往往反映「已经发生的到货」，不是「迟迟不来」。
        var snapshot = Snapshot(
            ltRules: new[] { new PurchaseLtRule { WarehouseCode = "WH-A", DefaultLtDays = 20 } },
            marginPercent: 0.10m);
        var map = new Dictionary<(string, int, int, string), DateTime>();
        var fact = Fact(eta: new DateTime(2026, 10, 10), supplyType: "ARRIVED_NOT_RECEIVED");

        var d = AvailableTimeCalculator.Explain(fact, map, snapshot, new DateTime(2026, 10, 25));

        d.Overdue.Should().Be(OverdueEtaOutcome.NotInTransitKeptOriginal);
        d.AvailableAt.Should().Be(new DateTime(2026, 10, 10), "过去时点即视为已可用，不加余裕");
        d.RiskEta.Should().BeNull();
    }

    [Fact]
    public void Explain_在途逾期但缺合法同仓LT_不给日期()
    {
        // 规则配的是 WH01，事实收货仓是 980105 —— 不得用别仓的 LT 顶替，也不得填今天/0 天。
        var snapshot = Snapshot(
            ltRules: new[] { new PurchaseLtRule { WarehouseCode = "WH01", DefaultLtDays = 20 } },
            marginPercent: 0.10m);
        var map = new Dictionary<(string, int, int, string), DateTime>();
        var fact = Fact(eta: new DateTime(2026, 10, 10), storageCode: "980105");

        var d = AvailableTimeCalculator.Explain(fact, map, snapshot, new DateTime(2026, 10, 25));

        d.Overdue.Should().Be(OverdueEtaOutcome.NoLegalLeadTime);
        d.AvailableAt.Should().BeNull("没有可计算的重估日期 ⇒ 不给时点");
        d.UnavailableReason.Should().Be(EtaUnavailableReason.OverdueNoLegalLeadTime,
            "与「三级链全空」分开报：这个要找 3号位 补 LT");
    }

    [Fact]
    public void Explain_双ETA为空_默认推算逾期_同式重估()
    {
        var snapshot = Snapshot(
            ltRules: new[] { new PurchaseLtRule { WarehouseCode = "WH-A", DefaultLtDays = 20 } },
            marginPercent: 0.10m);
        var map = new Dictionary<(string, int, int, string), DateTime>();
        // ReleaseDate=09-01 + 20 天 = 09-21 < R=10-25 ⇒ 默认推算已过期，走同一条重估公式
        var fact = Fact(eta: null, releaseDate: new DateTime(2026, 9, 1));

        var d = AvailableTimeCalculator.Explain(fact, map, snapshot, new DateTime(2026, 10, 25));

        d.Source.Should().Be(LPS.APS.Core.Rules.EtaSource.DefaultLt);
        d.Overdue.Should().Be(OverdueEtaOutcome.Recalculated);
        d.AvailableAt.Should().Be(new DateTime(2026, 10, 27));
    }

    [Fact]
    public void Explain_人工ETA逾期_不回落ERP_只重估人工()
    {
        var snapshot = Snapshot(
            ltRules: new[] { new PurchaseLtRule { WarehouseCode = "WH-A", DefaultLtDays = 20 } },
            marginPercent: 0.10m);
        var map = new Dictionary<(string, int, int, string), DateTime>
        {
            [("PO-001", 10, 100, "WH-A")] = new DateTime(2026, 10, 1)   // 人工 ETA 已逾期
        };
        var fact = Fact(eta: new DateTime(2026, 11, 20));               // ERP ETA 未逾期

        var d = AvailableTimeCalculator.Explain(fact, map, snapshot, new DateTime(2026, 10, 25));

        d.Source.Should().Be(LPS.APS.Core.Rules.EtaSource.Manual, "来源优先级永远先行，过期也不自动切 ERP");
        d.SelectedEta.Should().Be(new DateTime(2026, 10, 1));
        d.AvailableAt.Should().Be(new DateTime(2026, 10, 27));
    }

    [Fact]
    public void Explain_未逾期一律不加余裕_逐位不变()
    {
        var snapshot = Snapshot(
            ltRules: new[] { new PurchaseLtRule { WarehouseCode = "WH-A", DefaultLtDays = 20 } },
            marginPercent: 0.50m);
        var map = new Dictionary<(string, int, int, string), DateTime>();
        var fact = Fact(eta: new DateTime(2026, 11, 20));

        var d = AvailableTimeCalculator.Explain(fact, map, snapshot, new DateTime(2026, 10, 25));

        d.Overdue.Should().Be(OverdueEtaOutcome.None);
        d.AvailableAt.Should().Be(new DateTime(2026, 11, 20), "MarginPercent 再大也只作用于逾期供给");
    }

    [Fact]
    public void Explain_余裕率为零_按公式得截止时点本身()
    {
        // 公式的字面结果：p=0 ⇒ ETA* = R。参数取非零业务值由 3号位 发布侧负责，本类不改写公式。
        var snapshot = Snapshot(
            ltRules: new[] { new PurchaseLtRule { WarehouseCode = "WH-A", DefaultLtDays = 20 } },
            marginPercent: 0m);
        var map = new Dictionary<(string, int, int, string), DateTime>();
        var fact = Fact(eta: new DateTime(2026, 10, 10));

        var d = AvailableTimeCalculator.Explain(fact, map, snapshot, new DateTime(2026, 10, 25));

        d.RecalculatedExtraDays.Should().Be(0);
        d.AvailableAt.Should().Be(new DateTime(2026, 10, 25));
    }

    [Fact]
    public void IsInTransitSupplyType_只认尚未实际到货的两类()
    {
        AvailableTimeCalculator.IsInTransitSupplyType("PURCHASE_IN_TRANSIT").Should().BeTrue();
        AvailableTimeCalculator.IsInTransitSupplyType("OPEN_PO_REMAINING").Should().BeTrue();
        AvailableTimeCalculator.IsInTransitSupplyType("purchase_in_transit").Should().BeTrue("词表比对不区分大小写");

        AvailableTimeCalculator.IsInTransitSupplyType("ARRIVED_NOT_RECEIVED").Should().BeFalse("已到厂");
        AvailableTimeCalculator.IsInTransitSupplyType("VMI_ONSITE").Should().BeFalse("在场寄售");
        AvailableTimeCalculator.IsInTransitSupplyType("INTERPLANT_IN_TRANSIT").Should().BeFalse("厂间在途另按冻结禁令拦截");
        AvailableTimeCalculator.IsInTransitSupplyType("").Should().BeFalse();
        AvailableTimeCalculator.IsInTransitSupplyType(null).Should().BeFalse();
    }

    [Fact]
    public void BuildManualEtaMap_过滤未激活与默认值()
    {
        var overrides = new List<ProcurementManualEtaOverride>
        {
            new() { PONo = "PO-001", LineNo = 10, MaterialId = 100, ReceivingWarehouse = "WH-A",
                    ManualEta = new DateTime(2026, 9, 10), IsActive = true },
            new() { PONo = "PO-002", LineNo = 11, MaterialId = 100, ReceivingWarehouse = "WH-A",
                    ManualEta = new DateTime(2026, 9, 11), IsActive = false },   // 已取消，应被过滤
            new() { PONo = "PO-003", LineNo = 12, MaterialId = 100, ReceivingWarehouse = "WH-A",
                    ManualEta = default, IsActive = true }                       // default(DateTime) 视为无值，应被过滤
        };

        var map = AvailableTimeCalculator.BuildManualEtaMap(overrides);

        map.Should().HaveCount(1);
        map.Should().ContainKey(("PO-001", 10, 100, "WH-A"));
        map.Should().NotContainKey(("PO-002", 11, 100, "WH-A"));
        map.Should().NotContainKey(("PO-003", 12, 100, "WH-A"));
    }
}
