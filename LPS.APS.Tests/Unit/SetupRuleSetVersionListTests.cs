using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Xunit;
using LPS.APS.Application.Services;
using LPS.APS.Core.DTOs.Setup;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Core.Interfaces;
// 消歧：APS 命名空间存在实体 Task，别名定向 System.Threading.Tasks.Task
using Task = System.Threading.Tasks.Task;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// P2（2026-10-28）规则集版本列表 #11 RuleSetCode/RuleSetName 回带测试。
/// 背景：aps.planner 仅持 aps.setup.view 不持 aps.rule.view（4号位 dev 实测），
/// 前端 Setup 三页需在 #11（挂 aps.setup.view）直接分辨所属规则集，无需扩权调 GET /rule-sets。
/// 服务层经 IMasterDataLookupRepository.GetRuleSetsAsync 批查 RuleSet 主表回带 Code/Name。
/// </summary>
public class SetupRuleSetVersionListTests
{
    private readonly Mock<IRuleSetVersionRepository> _versionRepo = new();
    private readonly Mock<IMasterDataLookupRepository> _lookup = new();
    private readonly SetupRuleService _service;

    public SetupRuleSetVersionListTests()
    {
        _service = new SetupRuleService(
            _versionRepo.Object,
            _lookup.Object,
            Mock.Of<IAuditLogRepository>(),
            Mock.Of<IGovernanceVersionService>(),
            Mock.Of<ISetupUncoveredStatRepository>(),
            Mock.Of<IDataScopeService>());
    }

    [Fact]
    public async Task ListRuleSetVersionsAsync_回带RuleSetCode与RuleSetName()
    {
        _lookup.Setup(r => r.GetAllRuleSetVersionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RuleSetVersion>
            {
                new() { Id = 11, RuleSetId = 1, VersionCode = "V1", Status = "PUBLISHED" },
                new() { Id = 12, RuleSetId = 2, VersionCode = "V1", Status = "DRAFT" },
            });
        _lookup.Setup(r => r.GetRuleSetsAsync(It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RuleSet>
            {
                new() { Id = 1, RuleSetCode = "RS-ALPHA", RuleSetName = "铝合金机加规则集" },
                new() { Id = 2, RuleSetCode = "RS-BETA", RuleSetName = "铸造规则集" },
            });

        var result = await _service.ListRuleSetVersionsAsync(null, null, CancellationToken.None);

        Assert.Equal(2, result.Count);
        // 同 VersionCode=V1 但分属两套规则集——前端凭 RuleSetCode/Name 可直接分辨
        var alpha = result.Single(v => v.RuleSetId == 1);
        Assert.Equal("RS-ALPHA", alpha.RuleSetCode);
        Assert.Equal("铝合金机加规则集", alpha.RuleSetName);
        var beta = result.Single(v => v.RuleSetId == 2);
        Assert.Equal("RS-BETA", beta.RuleSetCode);
        Assert.Equal("铸造规则集", beta.RuleSetName);
    }

    [Fact]
    public async Task ListRuleSetVersionsAsync_未命中主表时Code为空不抛()
    {
        // 版本表与主表数据不一致（RuleSet 主表缺行）→ Code 兜底空串，不抛、不影响其余字段
        _lookup.Setup(r => r.GetAllRuleSetVersionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RuleSetVersion>
            {
                new() { Id = 11, RuleSetId = 999, VersionCode = "V1", Status = "DRAFT" },
            });
        _lookup.Setup(r => r.GetRuleSetsAsync(It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RuleSet>());

        var result = await _service.ListRuleSetVersionsAsync(null, null, CancellationToken.None);

        var item = Assert.Single(result);
        Assert.Equal(string.Empty, item.RuleSetCode);
        Assert.Null(item.RuleSetName);
        Assert.Equal("V1", item.VersionCode);
    }

    [Fact]
    public async Task ListRuleSetVersionsAsync_无版本返回空且不查主表()
    {
        _lookup.Setup(r => r.GetAllRuleSetVersionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RuleSetVersion>());

        var result = await _service.ListRuleSetVersionsAsync(null, null, CancellationToken.None);

        Assert.Empty(result);
        _lookup.Verify(r => r.GetRuleSetsAsync(It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}