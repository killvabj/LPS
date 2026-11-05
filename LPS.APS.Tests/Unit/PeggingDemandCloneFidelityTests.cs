using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **需求克隆保真**（2号位 辖区两处派生克隆）。
///
/// 【为什么有本文件】此前护栏只有 <c>ContinuityBucketingTests.切片与截小克隆_厂码随血缘继承_不丢成空串</c>，
/// 它经分桶路径只钉了 <c>FactoryCode</c> **一个**字段。**「钉一个」≠「逐字段」** ——
/// `LogicalProductionDemand` 再加字段时，只要克隆没拷，本用例之外没有任何东西会红，
/// 而漏拷是**静默错值**（比编译错难发现）：克隆出的需求带着默认值进入 Solver，
/// 消费方读到的是「没配」而不是「漏拷」。
///
/// 【做法】反射枚举 <c>LogicalProductionDemand</c> 的**全部公开属性**，逐个核克隆结果。
/// 以后 DTO 加字段 ⇒ **自动纳入**，不需要改本文件。
///
/// 【覆盖】我方两处派生克隆（都 `internal static`，`LPS.APS.Application` 对 `LPS.APS.Tests` 有
/// `InternalsVisibleTo`，故可直调，不必绕分桶路径）：
///   · <see cref="PeggingOrchestrator.CloneDemandRescaled"/> —— F-06 同键截小（只改数量）；
///   · <see cref="PeggingOrchestrator.CloneDemandSlice"/> —— 连续性分桶切片（身份/起点/路径按形参覆盖）。
///
/// 参照 1号位 2026-10-29 的 <c>Scheduling/DemandCloneFidelityTests</c> 同款思路（其覆盖
/// `CloneDemandForPartialLock`／`CloneDemandWithBatchQty`）；本文件独立一份，不引用、不改其文件。
///
/// 纯内存；不碰库、不跑 Solver、不改生产行为（仅两处克隆的可见性 `private`→`internal`）。
/// </summary>
public class PeggingDemandCloneFidelityTests
{
    /// <summary>截小克隆**唯一允许改**的字段（本批数量）。</summary>
    private static readonly string[] QuantityFields =
    {
        nameof(LogicalProductionDemand.NetOutputQty),
        nameof(LogicalProductionDemand.PlannedProcessQty)
    };

    /// <summary>
    /// 源需求：**每个字段都给一个「与类型默认值不同」的值** ——
    /// 否则「漏拷」与「拷了默认值」不可区分（<c>FactoryCode</c> 的 DTO 初值是 `string.Empty`，
    /// 源值若为空串，漏拷照样对得上 ⇒ 用例永远绿）。
    /// 这正是 1号位 那份同款用例的空档（其 <c>Source()</c> 未赋 <c>FactoryCode</c>）：**本文件不复制该空档**。
    /// </summary>
    private static LogicalProductionDemand Source() => new()
    {
        LogicalDemandKey        = "PV7_A-3",
        PlanVersionId           = 7,
        DomainKey               = "DOM-CN",
        AllocationSequence      = 3,
        DemandKey               = "ORD-9-3",
        OrderId                 = 9001,
        IsRootDemand            = true,
        BomNo                   = "BOM-X1",
        RequestDetailId         = 5501,
        RepWorksetId            = 6601,
        MaterialId              = 1001,
        FactoryId               = 5,
        FactoryCode             = "CN",
        StartStageCode          = "CN_PRE",
        RequiredStageCode       = "CN_FINAL",
        StartOperationCode      = "OP10",
        NetOutputQty            = 100m,
        PlannedProcessQty       = 120m,
        UOM                     = "EA",
        RequiredAvailableTime   = new DateTime(2026, 10, 20, 8, 0, 0),
        DemandSequence          = 42,
        ProductionInstructionNo = "PI-2026-0001",
        IsUnlocated             = true,
        PreferredResourceId     = 3101,
        FallbackResourceId      = 3102,
        IsContinuation          = true,
        ContinuationKey         = "CK-7-WO0001",
        RouteCode               = "R-CN-01",
        PathId                  = 9,
        NoSplitMerge            = true,
        PreferredResourceCode   = "EQ-77",
        DemandGoal              = DemandGoalValues.CustomerCommitment,
        QuantityAuthority       = QuantityAuthorityValues.OrderOnly
    };

    [Fact]
    public void 截小克隆_除数量外逐字段保真()
    {
        // F-06 同键截小：键/身份/起点全不变，只有两个数量被收敛到合法额度内。
        var src = Source();

        var clone = PeggingOrchestrator.CloneDemandRescaled(src, 30m, 36m);

        // 数量确实被形参覆盖（否则「保真」可能只是因为它压根没改数）
        clone.NetOutputQty.Should().Be(30m);
        clone.PlannedProcessQty.Should().Be(36m);

        AssertAllFieldsCopied(src, clone, QuantityFields);
    }

    [Fact]
    public void 切片克隆_形参全传源值时应逐字段保真()
    {
        // 切片克隆的身份字段（键/起点/路径/连续性）由形参覆盖 —— 界面上看它是「会改字段」的克隆。
        // 但**全部形参回传源值**时，产物必须与源**逐字段相同**：这既钉住保真度，
        // 又钉住「除形参明示覆盖的字段之外，一个字段都不许动」（含 FactoryCode、DemandGoal、
        // QuantityAuthority 这类当前有人读的，也含将来新加的）。
        var src = Source();

        var clone = PeggingOrchestrator.CloneDemandSlice(
            src,
            logicalDemandKey:   src.LogicalDemandKey,
            netQty:             src.NetOutputQty,
            processQty:         src.PlannedProcessQty,
            isContinuation:     src.IsContinuation,
            startStageCode:     src.StartStageCode,
            startOperationCode: src.StartOperationCode,
            continuationKey:    src.ContinuationKey,
            routeCode:          src.RouteCode,
            pathId:             src.PathId);

        // 无豁免：全部 33 个公开属性逐一相等。
        AssertAllFieldsCopied(src, clone, allowedToDiffer: Array.Empty<string>());
    }

    /// <summary>
    /// 反射逐字段比对。**新加字段自动纳入** —— 这是本文件相对「人肉清点」的全部意义。
    /// </summary>
    private static void AssertAllFieldsCopied(
        LogicalProductionDemand src,
        LogicalProductionDemand clone,
        params string[] allowedToDiffer)
    {
        var properties = typeof(LogicalProductionDemand)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead)
            .Where(p => !allowedToDiffer.Contains(p.Name, StringComparer.Ordinal))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

        // ── 自检 ①：属性清单非空且确实包含关键字段（防反射拿到空集导致「永远绿」）──
        properties.Should().NotBeEmpty("反射必须枚举到 LogicalProductionDemand 的公开属性");
        var names = properties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        names.Should().Contain(new[]
        {
            nameof(LogicalProductionDemand.FactoryCode),
            nameof(LogicalProductionDemand.DemandGoal),
            nameof(LogicalProductionDemand.QuantityAuthority),
            nameof(LogicalProductionDemand.IsRootDemand),
            nameof(LogicalProductionDemand.BomNo),
            nameof(LogicalProductionDemand.RequestDetailId),
            nameof(LogicalProductionDemand.RepWorksetId),
            nameof(LogicalProductionDemand.RouteCode),
            nameof(LogicalProductionDemand.PathId)
        });

        // ── 自检 ②：源值必须「非默认」，否则漏拷不可分辨 ──
        var indistinguishable = new List<string>();
        foreach (var property in properties)
        {
            var value = property.GetValue(src);
            if (property.PropertyType == typeof(string))
            {
                // 字符串特判：DTO 初值是 string.Empty，类型默认值却是 null ⇒ 空串等于「漏拷也看不出来」。
                if (string.IsNullOrEmpty((string?)value))
                {
                    indistinguishable.Add($"{property.Name}: 源值为 null/空串，与「漏拷成初值」同形");
                }

                continue;
            }

            var typeDefault = property.PropertyType.IsValueType
                ? Activator.CreateInstance(property.PropertyType)
                : null;
            if (Equals(value, typeDefault))
            {
                indistinguishable.Add($"{property.Name}: 源值 = 类型默认值 [{typeDefault ?? "null"}]");
            }
        }

        indistinguishable.Should().BeEmpty(
            "源需求必须给每个字段非默认值，否则「漏拷」与「拷了默认值」不可区分：" +
            Environment.NewLine + string.Join(Environment.NewLine, indistinguishable));

        // ── 正题：逐字段比对 ──
        var mismatched = new List<string>();
        foreach (var property in properties)
        {
            var expected = property.GetValue(src);
            var actual = property.GetValue(clone);
            if (!Equals(expected, actual))
            {
                mismatched.Add($"{property.Name}: 期望 [{expected ?? "null"}] 实得 [{actual ?? "null"}]");
            }
        }

        mismatched.Should().BeEmpty(
            "克隆漏拷（漏拷是静默错值，比编译错更难发现）：" +
            Environment.NewLine + string.Join(Environment.NewLine, mismatched));
    }
}
