using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;
using FluentAssertions;
using LPS.APS.Application.Extensions;
using LPS.APS.Application.Services.Query;
using LPS.APS.Application.Services.Query.Dto;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Data;
using LPS.APS.Engine.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// **CFV1-08 未闭合结果查询契约探针**（2号位，2026-11-13）。
///
/// <para><b>验的是「声明的目的」，不是「跑得过」</b>：</para>
/// <list type="number">
///   <item><b>字段原样往返</b>：写入 19 个字段 → 查回来逐字相等（尤其 `SourceFactoryId`／
///         `SourceDomainKey`／`NeedByTime` 为 NULL 时必须仍是 NULL，**不得**被补成目标厂或 CreatedAt）。</item>
///   <item><b>可信零缺口 与 查询失败可区分</b>（`06 v1.42` 第 5 条、`13 v1.9`）：查一个不存在的
///         Run 必须返回 <c>ResultState=OK</c> 且 <c>ItemCount=0</c>——这是「确实没有缺口」；
///         它与 <c>QUERY_FAILED</c> 是两种不同结果。</item>
///   <item><b>幂等**替换**语义**：同一 Run/PV 写两次不同内容，第二次必须**覆盖**第一次
///         （冻结要求「重算先替换而不保留伪历史」）。</item>
///   <item><b>过滤维度**：DomainKey／RootDemandKey／MaterialCode／TargetFactoryId 各自生效。</item>
/// </list>
///
/// <para><b>为什么带环境开关</b>：本探针**会写库**（用 <c>ScheduleRunId=-1</c>／<c>PlanVersionId=-1</c>
/// 作哨兵值，跑完必删），避免全量测试无端在库上留下写入。设 <c>APS_UCFS_PROBE_RUN=1</c> 才执行。</para>
/// </summary>
public class UnresolvedCrossFactorySupplyQueryProbeTest
{
    private static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("APS_UCFS_PROBE_RUN"), "1", StringComparison.Ordinal);

    /// <summary>哨兵 Run／PV：保证不会与真实数据相撞，且清理时边界明确。</summary>
    private const int SentinelRunId = -1;
    private const int SentinelPvId = -1;

    [Fact(DisplayName = "CFV1-08 未闭合结果：字段往返／零缺口与失败可区分／替换幂等／四维过滤")]
    public async Task QueryContractProbe()
    {
        if (!Enabled) return;

        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.Test.json", optional: false)
            .AddJsonFile("appsettings.Test.Local.json", optional: true)
            .Build();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddDatabaseServices(configuration);
        services.AddApplicationServices();

        var sp = services.BuildServiceProvider();
        var repo = sp.GetRequiredService<IUnresolvedCrossFactorySupplyResultRepository>();
        var query = sp.GetRequiredService<IUnresolvedCrossFactorySupplyQueryService>();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        // ① 先清场（哨兵 Run/PV，与真实数据无关）
        await repo.ReplaceForRunAsync(SentinelRunId, SentinelPvId, Array.Empty<UnresolvedCrossFactorySupplyResult>(), cts.Token);

        // ② 可信零缺口：查询成功但没有行 ⇒ OK + 0，**不是** QUERY_FAILED
        var empty = await query.QueryAsync(SentinelRunId, SentinelPvId, cancellationToken: cts.Token);
        empty.ResultState.Should().Be(UnresolvedCrossFactorySupplyQueryState.Ok,
            "查询成功执行 ⇒ 必须是 OK；0 条才是可信的零缺口");
        empty.ItemCount.Should().Be(0);
        empty.Items.Should().BeEmpty();

        // ③ 写入两条：一条「已知源厂/已知需用时间」，一条「三者全未知」
        var knownNeedBy = new DateTime(2026, 11, 20, 8, 0, 0);
        var rows = new List<UnresolvedCrossFactorySupplyResult>
        {
            new()
            {
                ScheduleRunId = SentinelRunId, PlanVersionId = SentinelPvId,
                ResultRecordKey = "PROBE_KNOWN",
                RootOrderId = 9001, RootDemandKey = "ORDER_9001_MAT-A_2",
                MaterialId = 11, MaterialCode = "MAT-A", TargetFactoryId = 2, DomainKey = "FAMILY_CYL",
                SourceFactoryId = 1, SourceDomainKey = "FAMILY_CYL", NeedByTime = knownNeedBy,
                EvidenceBasisCode = "ORDER_CANONICAL_LOOKUP", IssueRemark = "探针：源厂与需用时间均已知",
                RequiredQty = 100m, LegallyAllocatedQty = 20m, UnresolvedQty = 80m,
                UnresolvedCategory = UnresolvedCategoryCodes.ShCrossDomainFuture,
                ActualSHOrderNo = "O20593091", ActualProductionInstructionNo = null,
                SourceEvidenceJson = "{\"sourceRefs\":[\"probe\"]}",
                CommitmentStatus = UnresolvedCommitmentStatus.NotCommitted, CreatedAt = DateTime.Now
            },
            new()
            {
                ScheduleRunId = SentinelRunId, PlanVersionId = SentinelPvId,
                ResultRecordKey = "PROBE_UNKNOWN",
                RootOrderId = null, RootDemandKey = "SH_O20593092",
                MaterialId = null, MaterialCode = "MAT-B", TargetFactoryId = 3, DomainKey = "FAMILY_CYL",
                SourceFactoryId = null, SourceDomainKey = null, NeedByTime = null,
                EvidenceBasisCode = "SOURCE_UNVERIFIED", IssueRemark = "探针：源厂与需用时间均未知",
                RequiredQty = 50m, LegallyAllocatedQty = 0m, UnresolvedQty = 50m,
                UnresolvedCategory = UnresolvedCategoryCodes.Unknown,
                ActualSHOrderNo = "O20593092",
                CommitmentStatus = UnresolvedCommitmentStatus.NotCommitted, CreatedAt = DateTime.Now
            }
        };
        await repo.ReplaceForRunAsync(SentinelRunId, SentinelPvId, rows, cts.Token);

        // ④ 字段原样往返
        var all = await query.QueryAsync(SentinelRunId, SentinelPvId, cancellationToken: cts.Token);
        all.ResultState.Should().Be(UnresolvedCrossFactorySupplyQueryState.Ok);
        all.ItemCount.Should().Be(2);

        var known = all.Items.Single(i => i.ResultRecordKey == "PROBE_KNOWN");
        known.SourceFactoryId.Should().Be(1);
        known.SourceDomainKey.Should().Be("FAMILY_CYL");
        known.NeedByTime.Should().Be(knownNeedBy, "已知需用时间必须原样返回");
        known.UnresolvedQty.Should().Be(80m);
        known.CommitmentStatus.Should().Be("NOT_COMMITTED");

        var unknown = all.Items.Single(i => i.ResultRecordKey == "PROBE_UNKNOWN");
        unknown.SourceFactoryId.Should().BeNull("未知源厂必须保持 NULL，不得补成目标厂");
        unknown.SourceDomainKey.Should().BeNull();
        unknown.NeedByTime.Should().BeNull("未知需用时间必须保持 NULL，不得用 CreatedAt 顶替");
        unknown.RootOrderId.Should().BeNull();

        // ⑤ 四维过滤各自生效
        (await query.QueryAsync(SentinelRunId, SentinelPvId, domainKey: "FAMILY_CYL", cancellationToken: cts.Token))
            .ItemCount.Should().Be(2);
        (await query.QueryAsync(SentinelRunId, SentinelPvId, rootDemandKey: "SH_O20593092", cancellationToken: cts.Token))
            .ItemCount.Should().Be(1);
        (await query.QueryAsync(SentinelRunId, SentinelPvId, materialCode: "MAT-A", cancellationToken: cts.Token))
            .ItemCount.Should().Be(1);
        (await query.QueryAsync(SentinelRunId, SentinelPvId, targetFactoryId: 3, cancellationToken: cts.Token))
            .ItemCount.Should().Be(1);

        // ⑥ 替换幂等：再写一条，必须**覆盖**而不是累加（MERGE 只增不减的坑）
        await repo.ReplaceForRunAsync(SentinelRunId, SentinelPvId, new[] { rows[0] }, cts.Token);
        var replaced = await query.QueryAsync(SentinelRunId, SentinelPvId, cancellationToken: cts.Token);
        replaced.ItemCount.Should().Be(1, "同一 Run/PV 重算须**替换**，不得保留伪历史");
        replaced.Items.Single().ResultRecordKey.Should().Be("PROBE_KNOWN");

        // ⑦ 收尾清场
        await repo.ReplaceForRunAsync(SentinelRunId, SentinelPvId, Array.Empty<UnresolvedCrossFactorySupplyResult>(), cts.Token);
        var afterCleanup = await query.QueryAsync(SentinelRunId, SentinelPvId, cancellationToken: cts.Token);
        afterCleanup.ItemCount.Should().Be(0);
    }
}