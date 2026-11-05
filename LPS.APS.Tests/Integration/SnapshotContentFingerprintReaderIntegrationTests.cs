using FluentAssertions;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;
using LPS.APS.Engine.Repositories.Governance;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// <see cref="SnapshotContentFingerprintReader"/> 集成测试（B-6 内容指纹，2026-10-30）。
///
/// 【为什么要真库测】这条 SQL 是 B-6 唯一新增的库内语句，而它**每次取快照都会跑**；
/// 写错列名/写错库/Dapper 把 <c>CONVERT(varchar(64), …, 2)</c> 映射错，都会在
/// 快照装载时表现为「取指纹失败」（Provider 已降级为不复用缓存，不会挂）；
/// 单测用 mock 是照不到这一层的。
///
/// 【不绑死具体版本号】现网行会被删改，故一律「先查一个符合条件的版本再测」，
/// 查不到就跳过该断言，不做硬编码。
/// </summary>
public class SnapshotContentFingerprintReaderIntegrationTests
{
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly ISnapshotContentFingerprintReader _reader;

    public SnapshotContentFingerprintReaderIntegrationTests()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.Test.json", optional: false)
            .AddJsonFile("appsettings.Test.Local.json", optional: true)
            .Build();

        var dbOptions = configuration.GetSection("Database").Get<LPS.APS.Engine.Configuration.DatabaseOptions>()
            ?? throw new InvalidOperationException("Database configuration not found in appsettings.Test.json");

        _connectionManager = new DatabaseConnectionManager(Options.Create(dbOptions));
        _reader = new SnapshotContentFingerprintReader(_connectionManager, NullLogger<SnapshotContentFingerprintReader>.Instance);
    }

    /// <summary>取一个「绑定的规则集/参数集都有正文」的策略包版本 Id；没有则返回 null。</summary>
    private async Task<long?> PickUsableStrategyProfileVersionIdAsync()
        => await _connectionManager.QueryFirstOrDefaultAsync<long?>(
            @"SELECT TOP 1 spv.[Id]
              FROM [dbo].[StrategyProfileVersion] spv
              INNER JOIN [dbo].[RuleSetVersion] rsv ON rsv.[Id] = spv.[RuleSetVersionId]
              INNER JOIN [dbo].[ParameterSetVersion] psv ON psv.[Id] = spv.[ParameterSetVersionId]
              WHERE ISNULL(rsv.[ContentSnapshotJson], N'') <> N''
                AND ISNULL(psv.[ContentSnapshotJson], N'') <> N''
              ORDER BY spv.[Id] DESC");

    [Fact]
    public async System.Threading.Tasks.Task 取指纹_真实库_64位大写十六进制且同版本两次一致()
    {
        var spvId = await PickUsableStrategyProfileVersionIdAsync();
        if (spvId is null)
        {
            return; // 库里没有可用样本（空库/全空正文），该项无意义
        }

        var first = await _reader.GetStrategyProfileContentFingerprintAsync(spvId.Value);
        var second = await _reader.GetStrategyProfileContentFingerprintAsync(spvId.Value);

        first.Should().NotBeNullOrWhiteSpace();
        first!.Length.Should().Be(64, "SHA2_256 的十六进制表示恒为 64 位");
        first.Should().MatchRegex("^[0-9A-F]{64}$", "库内 CONVERT(..., 2) 输出大写十六进制");
        second.Should().Be(first, "内容没变 ⇒ 指纹必须稳定，否则缓存永远命中不了");
    }

    [Fact]
    public async System.Threading.Tasks.Task 取指纹_不存在的版本_返回null而不是抛异常()
    {
        // 刻意用负数：Identity 列不可能出现，且不会被别的测试造出来
        var fingerprint = await _reader.GetStrategyProfileContentFingerprintAsync(-20261030);

        fingerprint.Should().BeNull("取不到指纹要让调用方跳过缓存，不能在缓存这条路上新造一个抛出点");
    }

    [Fact]
    public async System.Threading.Tasks.Task 取指纹_同规则集不同参数集的两个版本_指纹必须不同()
    {
        // 指纹覆盖「规则集 + 参数集」两个维度；若只哈希了其中一个，这条会红
        var pair = await _connectionManager.QueryFirstOrDefaultAsync<long?>(
            @"SELECT TOP 1 b.[Id]
              FROM [dbo].[StrategyProfileVersion] a
              INNER JOIN [dbo].[StrategyProfileVersion] b
                      ON b.[RuleSetVersionId] = a.[RuleSetVersionId]
                     AND b.[ParameterSetVersionId] <> a.[ParameterSetVersionId]
              WHERE a.[Id] < b.[Id]
              ORDER BY a.[Id]");

        if (pair is null)
        {
            return; // 现网没有「同规则集、不同参数集」的版本对，该项无从比较
        }

        var sibling = await _connectionManager.QueryFirstOrDefaultAsync<long?>(
            @"SELECT TOP 1 a.[Id]
              FROM [dbo].[StrategyProfileVersion] a
              INNER JOIN [dbo].[StrategyProfileVersion] b
                      ON b.[Id] = @OtherId
                     AND b.[RuleSetVersionId] = a.[RuleSetVersionId]
                     AND b.[ParameterSetVersionId] <> a.[ParameterSetVersionId]
              WHERE a.[Id] < b.[Id]",
            new { OtherId = pair.Value });

        if (sibling is null)
        {
            return;
        }

        var fingerprintA = await _reader.GetStrategyProfileContentFingerprintAsync(sibling.Value);
        var fingerprintB = await _reader.GetStrategyProfileContentFingerprintAsync(pair.Value);

        fingerprintA.Should().NotBeNull();
        fingerprintB.Should().NotBeNull();
        fingerprintA.Should().NotBe(fingerprintB, "同规则集不同参数集的内容不同，指纹必须能区分");
    }
}
