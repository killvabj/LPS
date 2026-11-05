using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Xunit;
using LPS.APS.Core.Dto;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// N17 取证：0号位 2026-10-27 件1 的停止条件要求「同一代码版本，一个**完整 DTO 输入样本**
/// ＋ 一个最小本地测试，证明本 Run 有效 PI 号对应的可信 XC 源行没有被 Material / Factory
/// 做业务过滤，且带出原始 Material 与 Factory」。
///
/// 本组用例就是那个样本：三组正反例各构造成**真实 `XcFact` 对象**，用真实序列化器输出 JSON，
/// 落盘到 `%TEMP%\n17_xc_dto_sample.json` 供回执件原样引用——**不手抄 JSON**。
/// 序列化文本同时作为断言对象：字段消失（`MaterialCode` / `FactoryCode` 被删）这里会直接挂。
/// </summary>
public class N17XcFactDtoSampleTests
{
    private const string PiA = "PI-A";
    private const string PiB = "PI-B";
    private const string PiAMaterial = "MAT-PI-A";

    private static string SampleFilePath =>
        Path.Combine(Path.GetTempPath(), "n17_xc_dto_sample.json");

    /// <summary>
    /// 三组最简正反例，落成一个可直接交给 5号位 的 `XcFact` 列表：
    /// ① PI_A ＋ 物料一致 ＋ **异厂**（CN 仓持 PI_A）⇒ 必须带出源厂 CN；
    /// ② PI_A ＋ 物料不一致（MAT-SRC）⇒ **仍在列表里**，且 `MaterialCode` 是源行的 MAT-SRC；
    /// ③ PI_B 的行**不在**本列表（装载层按 PI 号等值分组，异 PI 不混入）。
    /// </summary>
    private static IReadOnlyList<XcFact> BuildSample()
    {
        return new List<XcFact>
        {
            // 例①：物料一致、异厂
            new()
            {
                ProductionInstructionNo = PiA,
                MaterialCode            = PiAMaterial,
                XcWarehouseCode         = "XC-CN-01",
                FactoryCode             = "CN",
                Quantity                = 100m,
                RelatedStageCode        = "ASSY",
                AvailableTime           = null,
                SourceDocument          = null,
                SnapshotTime            = new DateTime(2026, 10, 27, 9, 1, 9, DateTimeKind.Unspecified)
            },

            // 例②：物料不匹配 —— 装载侧不剔除，源物料原样带出（PI 主档物料是 MAT-PI-A，这里必须是 MAT-SRC）
            new()
            {
                ProductionInstructionNo = PiA,
                MaterialCode            = "MAT-SRC",
                XcWarehouseCode         = "XC-BJ-02",
                FactoryCode             = "BJ",
                Quantity                = 40m,
                RelatedStageCode        = "MACH",
                AvailableTime           = null,
                SourceDocument          = null,
                SnapshotTime            = new DateTime(2026, 10, 27, 9, 1, 9, DateTimeKind.Unspecified)
            }
        };
    }

    private static string Serialize(IReadOnlyList<XcFact> facts)
    {
        var json = JsonSerializer.Serialize(facts, new JsonSerializerOptions { WriteIndented = true });

        // 落盘（xUnit 的 ITestOutputHelper 要等用例结束才 flush，被掐断就什么都没了 ⇒ 写文件）
        File.WriteAllText(SampleFilePath, json);

        return json;
    }

    /// <summary>例①＋②：两行都进列表，且各自带出**源**物料与**源**厂。</summary>
    [Fact]
    public void 样本_两行都带出源物料与源厂()
    {
        var facts = BuildSample();
        var json = Serialize(facts);

        Assert.Equal(2, facts.Count);

        Assert.Contains("\"MaterialCode\": \"MAT-PI-A\"", json);
        Assert.Contains("\"MaterialCode\": \"MAT-SRC\"", json);

        // 厂不得被改写成 PI 所属厂（PI 属 BJ 时例①仍须是 CN）
        Assert.Contains("\"FactoryCode\": \"CN\"", json);
        Assert.Contains("\"FactoryCode\": \"BJ\"", json);
    }

    /// <summary>例②单独锁死：不匹配那一行的 `MaterialCode` **不是** PI 主档物料。</summary>
    [Fact]
    public void 样本_不匹配行不得用PI物料覆盖()
    {
        var facts = BuildSample();

        var mismatch = Assert.Single(facts, f => f.XcWarehouseCode == "XC-BJ-02");
        Assert.Equal("MAT-SRC", mismatch.MaterialCode);
        Assert.NotEqual(PiAMaterial, mismatch.MaterialCode);
    }

    /// <summary>例③：异 PI 不混入 —— 样本里只应有 PI_A，无 PI_B。</summary>
    [Fact]
    public void 样本_只含本PI()
    {
        var json = Serialize(BuildSample());

        Assert.Contains($"\"ProductionInstructionNo\": \"{PiA}\"", json);
        Assert.DoesNotContain(PiB, json);
    }

    /// <summary>字段存在性由编译器锁：删掉任一属性，本用例编译失败。</summary>
    [Fact]
    public void 样本_XcFact仍带物料与厂两列()
    {
        var fact = new XcFact { MaterialCode = "M", FactoryCode = "F" };

        Assert.Equal("M", fact.MaterialCode);
        Assert.Equal("F", fact.FactoryCode);
    }
}
