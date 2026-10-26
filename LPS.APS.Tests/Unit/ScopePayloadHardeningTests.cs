using LPS.APS.Application.Services.Query;
using LPS.APS.BusinessRules.Repositories;
using LPS.APS.BusinessRules.Services;
using LPS.APS.Core.DTOs.Governance;
using Moq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// ScopeJson 显式 null 载荷加固回归（4号位 10-19《CandidateBrief六字段源码复核》§二/§三 两个漏口的失败注入）。
/// 漏口A（🔴）：`"orderTargets": null` 等显式 null 列表会覆盖 STJ [] 初始值 → NRE/ArgumentNullException
///   不是 JsonException、降级闸接不住 → 整 endpoint 500。修复=显式归空（?. ?? / ?? []）。
/// 漏口B（🟡）：载荷缺 trigger → 枚举默认值 0（BusinessTriggerType 无 0 成员）→ EnumLiteral 透出 "0"。
///   修复=Enum.IsDefined 判不了就 null。
/// 语义红线不变：无载荷(ScopeJson NULL/坏)=投影 null 不是 0；载荷存在但列表 null=「没有目标」=0。
/// </summary>
public class ScopePayloadHardeningTests
{
    private static GovernanceQueryService BuildScopeService(string scopeJson)
    {
        var repo = new Mock<IGovernanceQueryRepository>(MockBehavior.Loose);
        repo.Setup(r => r.GetRunScopeRawAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RunScopeRawRow
            {
                Id = 1,
                RunType = "LOCAL_RESCHEDULE",
                ScopeJson = scopeJson,
                BasePlanVersionId = 7,
                BaseVersionCode = "PV-BASE-007",
            });
        repo.Setup(r => r.GetOrderCanonicalMapAsync(It.IsAny<IReadOnlyList<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<long, (string? OrderNo, string? MTS_InstructionNo)>());
        repo.Setup(r => r.GetTaskMapAsync(It.IsAny<IReadOnlyList<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<long, (string? TaskNo, string? OperationCode)>());
        repo.Setup(r => r.GetResourceMapAsync(It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, (string? ResourceCode, string? ResourceName)>());
        return new GovernanceQueryService(repo.Object, null);
    }

    // ── 漏口A · scope 端点：显式 null 三列表 → 不 500，计数 0、明细空 ──
    [Fact]
    public async Task scope端点_显式null列表_不抛NRE且计数为0()
    {
        const string json = """
            {"schemaVersion":2,"trigger":"NEW_ORDER_INSERT","priorityMode":"NORMAL",
             "orderTargets":null,"taskTargets":null,"changedResourceIds":null}
            """;
        var svc = BuildScopeService(json);

        var dto = await svc.GetRunScopeAsync(1);   // 修复前：ArgumentNullException/NRE 直冒（闸只接 JsonException）

        Assert.NotNull(dto);
        Assert.True(dto!.ScopeAvailable);
        Assert.Equal(0, dto.Counts!.OrderTargets);
        Assert.Equal(0, dto.Counts!.TaskTargets);
        Assert.Equal(0, dto.Counts!.ChangedResources);
        Assert.Empty(dto.OrderTargets!);
        Assert.Empty(dto.TaskTargets!);
        Assert.Empty(dto.ChangedResources!);
        Assert.Equal("NEW_ORDER_INSERT", dto.BusinessTriggerType);
    }

    // ── 漏口B · scope 端点：缺 trigger → null，不透出 "0" ──
    [Fact]
    public async Task scope端点_缺trigger_投影null不透出0()
    {
        const string json = """
            {"schemaVersion":2,"orderTargets":[],"taskTargets":[],"changedResourceIds":[]}
            """;
        var svc = BuildScopeService(json);

        var dto = await svc.GetRunScopeAsync(1);

        Assert.NotNull(dto);
        Assert.True(dto!.ScopeAvailable);
        Assert.Null(dto.BusinessTriggerType);        // 修复前 = "0"
        Assert.Null(dto.PriorityMode);               // null 直传纪律不受影响
    }

    // ── 原语义回归：无载荷（ScopeJson NULL）≠ 0，投影全 null ──
    [Fact]
    public async Task scope端点_ScopeJson为NULL_ScopeAvailableFalse且投影null不是0()
    {
        var svc = BuildScopeService(null!);

        var dto = await svc.GetRunScopeAsync(1);

        Assert.NotNull(dto);
        Assert.False(dto!.ScopeAvailable);
        Assert.Null(dto.Counts);
        Assert.Null(dto.BusinessTriggerType);
    }

    // ── 漏口A/B · candidate-summary 投影（OverviewQueryRepository.MapCandidateBrief，反射触达私有映射块，不改生产码可见性）──
    private static object InvokeMapCandidateBrief(string? scopeJsonRaw)
    {
        var repoType = typeof(OverviewQueryRepository);
        var rowType = repoType.GetNestedType("CandidateBriefRow", BindingFlags.NonPublic)!;
        var row = Activator.CreateInstance(rowType)!;
        rowType.GetProperty("PlanVersionId")!.SetValue(row, 101);
        rowType.GetProperty("VersionCode")!.SetValue(row, "PV-TEST");
        rowType.GetProperty("ScopeJsonRaw")!.SetValue(row, scopeJsonRaw);

        // 绕过构造（DatabaseConnectionManager 不参与本映射），_logger 字段保持 null（映射内为 _logger?. 安全调用）
        var instance = RuntimeHelpers.GetUninitializedObject(repoType);
        var map = repoType.GetMethod("MapCandidateBrief", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return map.Invoke(instance, new[] { row })!;
    }

    [Fact]
    public void Brief投影_显式null列表_计数0不抛()
    {
        const string json = """
            {"schemaVersion":2,"trigger":"GANTT_ADJUSTMENT","priorityMode":"EXPEDITE",
             "orderTargets":null,"taskTargets":null,"changedResourceIds":null}
            """;
        var dto = (LPS.APS.Core.Dto.CandidateBriefDto)InvokeMapCandidateBrief(json);

        Assert.Equal(0, dto.OrderTargetCount);       // 修复前=NRE→500（闸接不住，整页19卡全没）
        Assert.Equal(0, dto.TaskTargetCount);
        Assert.Equal(0, dto.ChangedResourceCount);
        Assert.Equal("GANTT_ADJUSTMENT", dto.BusinessTriggerType);
        Assert.Equal("EXPEDITE", dto.PriorityMode);
    }

    [Fact]
    public void Brief投影_缺trigger_null且无载荷行投影保持null不是0()
    {
        var missingTrigger = (LPS.APS.Core.Dto.CandidateBriefDto)InvokeMapCandidateBrief(
            """{"schemaVersion":2,"orderTargets":[],"taskTargets":[],"changedResourceIds":[]}""");
        Assert.Null(missingTrigger.BusinessTriggerType);   // 修复前="0"

        var noPayload = (LPS.APS.Core.Dto.CandidateBriefDto)InvokeMapCandidateBrief(null);
        Assert.Null(noPayload.BusinessTriggerType);        // 无载荷= null（与显式null列表的 0 语义分界）
        Assert.Null(noPayload.OrderTargetCount);
        Assert.Null(noPayload.TaskTargetCount);
        Assert.Null(noPayload.ChangedResourceCount);
    }
}
