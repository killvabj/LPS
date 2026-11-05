using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using LPS.APS.Application.Models;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Entities.APS;
using LPS.APS.Core.Exceptions;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// Setup 换型规则投影/裁剪纯函数测试（v1.2 §19.3 ①/② + §22 S01~S06 中 2号位 可覆盖的纯逻辑）。
/// 覆盖：实体→快照字段映射（去审计字段）、IsActive 过滤、EXACT/DEFAULT 分流（From/To null 语义）、
/// 按 Domain（ProductionDepartmentId + StageCode）裁剪。命中/兜底（EXACT 优先、无规则 0 分钟、
/// 禁止 SetupTime fallback）属 1号位 SetupOptimizer.ResolveSetup，不在此层。
/// </summary>
public class SetupTransitionRuleProjectionTests
{
    private static SetupTransitionRule Rule(
        int dept = 1, string stage = "S1", string op = "OP20", int resource = 10,
        int? from = null, int? to = null, string ruleType = SetupTransitionRuleType.Default,
        decimal minutes = 30m, bool isActive = true)
        => new SetupTransitionRule
        {
            Id = 0,
            RuleSetVersionId = 100,
            ProductionDepartmentId = dept,
            StageCode = stage,
            OperationCode = op,
            ResourceId = resource,
            FromMaterialId = from,
            ToMaterialId = to,
            RuleType = ruleType,
            SetupMinutes = minutes,
            IsActive = isActive
        };

    private static MaterialStageDepartmentContextDto Context(int dept, string stage)
        => new MaterialStageDepartmentContextDto { ProductionDepartmentId = dept, StageCode = stage };

    [Fact]
    public void Project_映射业务键_去审计字段_保留RuleType与分钟()
    {
        var rules = new List<SetupTransitionRule>
        {
            Rule(dept: 1, stage: "S1", op: "OP20", resource: 10, from: 101, to: 102, ruleType: SetupTransitionRuleType.Exact, minutes: 45m)
        };

        var snapshots = SetupTransitionRuleProjector.Project(rules);

        snapshots.Should().ContainSingle();
        var s = snapshots[0];
        s.ProductionDepartmentId.Should().Be(1);
        s.StageCode.Should().Be("S1");
        s.OperationCode.Should().Be("OP20");
        s.ResourceId.Should().Be(10);
        s.FromMaterialId.Should().Be(101);
        s.ToMaterialId.Should().Be(102);
        s.RuleType.Should().Be(SetupTransitionRuleType.Exact);
        s.SetupMinutes.Should().Be(45m);
    }

    [Fact]
    public void Project_筛选IsActive_排除无效规则()
    {
        var rules = new List<SetupTransitionRule>
        {
            Rule(ruleType: SetupTransitionRuleType.Exact, minutes: 10m, isActive: true),
            Rule(ruleType: SetupTransitionRuleType.Exact, minutes: 20m, isActive: false)
        };

        var snapshots = SetupTransitionRuleProjector.Project(rules);

        snapshots.Should().ContainSingle();
        snapshots[0].SetupMinutes.Should().Be(10m);
    }

    [Fact]
    public void Project_DEFAULT规则FromTo为null_EXACT有值()
    {
        var rules = new List<SetupTransitionRule>
        {
            Rule(ruleType: SetupTransitionRuleType.Default, from: null, to: null),
            Rule(ruleType: SetupTransitionRuleType.Exact, from: 1, to: 2)
        };

        var snapshots = SetupTransitionRuleProjector.Project(rules);

        snapshots.Should().HaveCount(2);
        snapshots.Single(s => s.RuleType == SetupTransitionRuleType.Default).FromMaterialId.Should().BeNull();
        snapshots.Single(s => s.RuleType == SetupTransitionRuleType.Default).ToMaterialId.Should().BeNull();
        snapshots.Single(s => s.RuleType == SetupTransitionRuleType.Exact).FromMaterialId.Should().Be(1);
        snapshots.Single(s => s.RuleType == SetupTransitionRuleType.Exact).ToMaterialId.Should().Be(2);
    }

    [Fact]
    public void ProjectFromSnapshot_子块JSON_映射快照_筛IsActive()
    {
        // S-9：子块承载 → 消费快照整链（S-8 便捷入口），IsActive=false 被过滤
        var rules = new List<SetupTransitionRule>
        {
            Rule(dept: 1, stage: "S1", op: "OP20", resource: 10, from: 101, to: 102, ruleType: SetupTransitionRuleType.Exact, minutes: 45m),
            Rule(dept: 1, stage: "S1", op: "OP20", resource: 10, from: 101, to: 102, ruleType: SetupTransitionRuleType.Exact, minutes: 20m, isActive: false)
        };
        var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [SetupTransitionRuleProjector.SetupTransitionRulesBlockName] = rules
        });

        var snapshots = SetupTransitionRuleProjector.ProjectFromSnapshot(json);

        snapshots.Should().ContainSingle();
        snapshots[0].RuleType.Should().Be(SetupTransitionRuleType.Exact);
        snapshots[0].SetupMinutes.Should().Be(45m);
        snapshots[0].FromMaterialId.Should().Be(101);
    }

    [Fact]
    public void ExtractRules_三态_缺失与空配置返回空列表_损坏抛红线异常()
    {
        // 2026-10-14 与 ⑧ 块同轨（1号位 2026-10-13《BatchPolicy三态载体择定…回执》§五「应同轨」，
        // 边界照该件 §5.3「仅 JSON 损坏抛异常；缺子块／null／显式 [] 仍空列表」）：
        // ① 缺子块 / null / 空串 → 空列表（该版本未配置换型规则，合法未配置，非装载失败）；
        // ② 显式空配置 "SetupTransitionRules":[] → 空列表（明确空配置，合法）；
        // ③ JSON 结构损坏 → 抛 SetupRuleDataRedLineException（显式失败，不再静默空列表）。
        SetupTransitionRuleProjector.ExtractRules(null).Should().BeEmpty();
        SetupTransitionRuleProjector.ExtractRules(string.Empty).Should().BeEmpty();
        SetupTransitionRuleProjector.ExtractRules("{\"DemandPriority\":{}}").Should().BeEmpty();
        SetupTransitionRuleProjector.ExtractRules("{\"SetupTransitionRules\":null}").Should().BeEmpty();
        SetupTransitionRuleProjector.ExtractRules("{\"SetupTransitionRules\":[]}").Should().BeEmpty();

        Action outer = () => SetupTransitionRuleProjector.ExtractRules("{ not-valid-json");
        outer.Should().Throw<SetupRuleDataRedLineException>();

        Action inner = () => SetupTransitionRuleProjector.ExtractRules("{\"SetupTransitionRules\": [broken}");
        inner.Should().Throw<SetupRuleDataRedLineException>();
    }

    [Fact]
    public void ProjectFromSnapshot_子块缺失或为空_返回空列表()
    {
        // 合法缺配——缺失子块 / null 子块 / 空字符串 → 空列表（无规则 DEFAULT 兜底，0 分钟口径不变）
        var noBlock = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["DemandPriority"] = new { }
        });

        SetupTransitionRuleProjector.ProjectFromSnapshot(noBlock).Should().BeEmpty();
        SetupTransitionRuleProjector.ProjectFromSnapshot(null).Should().BeEmpty();
        SetupTransitionRuleProjector.ProjectFromSnapshot(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void ProjectFromSnapshot_损坏JSON_抛红线异常_failFast()
    {
        // 装载链入口（FrozenStrategySnapshotProvider.DeserializeSetupTransitionRules → 第⑦块载荷）
        // 2026-10-14 由 fail-open 改 fail-fast：损坏版本**不得**再静默降级成「无规则」，
        // 否则 1号位 消费侧按 0 分钟排出看似成立、实则丢失换型时间的计划且全链无告警位。
        Action outer = () => SetupTransitionRuleProjector.ProjectFromSnapshot("{ not-valid-json");
        outer.Should().Throw<SetupRuleDataRedLineException>();

        Action inner = () => SetupTransitionRuleProjector.ProjectFromSnapshot("{\"SetupTransitionRules\": [broken}");
        inner.Should().Throw<SetupRuleDataRedLineException>();
    }

    [Fact]
    public void CropToDomain_仅保留本Domain部门阶段_跨域规则被裁掉()
    {
        var rules = new List<SetupTransitionRuleSnapshot>
        {
            new() { ProductionDepartmentId = 1, StageCode = "S1", RuleType = SetupTransitionRuleType.Default, SetupMinutes = 30m },
            new() { ProductionDepartmentId = 1, StageCode = "S2", RuleType = SetupTransitionRuleType.Default, SetupMinutes = 40m },
            new() { ProductionDepartmentId = 2, StageCode = "S1", RuleType = SetupTransitionRuleType.Default, SetupMinutes = 50m }
        };
        var contexts = new List<MaterialStageDepartmentContextDto>
        {
            Context(1, "S1"),
            Context(1, "S2")
        };

        var cropped = SetupTransitionRuleProjector.CropToDomain(rules, contexts);

        cropped.Should().HaveCount(2);
        cropped.Should().OnlyContain(r => r.ProductionDepartmentId == 1); // 部门 2 规则被裁掉
    }

    [Fact]
    public void CropToDomain_上下文为空_返回空()
    {
        var rules = new List<SetupTransitionRuleSnapshot>
        {
            new() { ProductionDepartmentId = 1, StageCode = "S1", RuleType = SetupTransitionRuleType.Default }
        };

        SetupTransitionRuleProjector.CropToDomain(rules, new List<MaterialStageDepartmentContextDto>())
            .Should().BeEmpty();
    }

    // ==================== 4号位 P0 Q1 修复（2026-10-28）：草稿副本 ⑦ 子块重编 ====================
    // 迭代：前 6 条坑描述 + 断言，第 7 条覆盖 4号位 验收口径「Id/10⁷ = 草稿Id 且 RuleSetVersionId=草稿Id」。
    // （原说明错位，顺序实为 5 条；均覆盖重编核心语义。）

    private static string BuildSourceBlock(params (int Seq, string RuleType, decimal Minutes)[] rows)
    {
        // 用投影器自身 SetRules 生成 ⑦ 子块 → 与生产线同一序列化口径（System.Text.Json 默认大小写敏感，
        // 手写 camelCase 匿名对象会使 ExtractRules 反序列化不到业务字段；此处直接以实体 + SetRules 产出真源）。
        var rules = rows.Select(r => new SetupTransitionRule
        {
            Id = r.Seq, // 前端 forkDraft 带进来的源 Id（仅为占位，重编后废弃）
            RuleSetVersionId = r.Seq,
            ProductionDepartmentId = 1,
            StageCode = "BJ_ASSY",
            OperationCode = $"OP{r.Seq}",
            ResourceId = 10 + r.Seq,
            FromMaterialId = r.RuleType == SetupTransitionRuleType.Exact ? 101 : null,
            ToMaterialId = r.RuleType == SetupTransitionRuleType.Exact ? 102 : null,
            RuleType = r.RuleType,
            SetupMinutes = r.Minutes,
            IsActive = true
        }).ToList();
        return SetupTransitionRuleProjector.SetRules(null, rules);
    }

    [Fact]
    public void RenumberToVersion_重编Id高位与RuleSetVersionId_业务字段保持()
    {
        var source = BuildSourceBlock((1, "DEFAULT", 30m), (2, "EXACT", 45m));

        var result = SetupTransitionRuleProjector.RenumberToVersion(source, 123456L);

        var extract = SetupTransitionRuleProjector.ExtractRules(result);
        extract.Should().HaveCount(2);
        extract[0].Id.Should().Be(1234560000001L);
        extract[0].RuleSetVersionId.Should().Be(123456L);
        extract[1].Id.Should().Be(1234560000002L);
        extract[1].RuleSetVersionId.Should().Be(123456L);
        extract[0].SetupMinutes.Should().Be(30m);
        extract[1].SetupMinutes.Should().Be(45m);
        extract[1].RuleType.Should().Be(SetupTransitionRuleType.Exact);
        extract[0].StageCode.Should().Be("BJ_ASSY");
        extract[0].OperationCode.Should().Be("OP1");
    }

    [Fact]
    public void RenumberToVersion_Id高位归新版本_使写路径parseRuleId命中草稿()
    {
        var source = BuildSourceBlock((7, "DEFAULT", 39m));

        var result = SetupTransitionRuleProjector.RenumberToVersion(source, 2962L); // 4号位 示例：V5 草稿 Id=2962

        var renumbered = SetupTransitionRuleProjector.ExtractRules(result).Single();
        renumbered.Id.Should().Be(29620000001L); // 低位序号按 1..N 重排：单继承行 → seq=1，高位=草稿 Id
        var (vsId, _) = SetupTransitionRuleProjector.ParseRuleId(renumbered.Id);
        vsId.Should().Be(2962L); // 写路径反查归草稿 → 422/400 消除
    }

    [Fact]
    public void RenumberToVersion_子块为空_原样返回()
    {
        string source = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["DemandPriority"] = new { },
            ["SetupTransitionRules"] = new object[] { }
        });

        var result = SetupTransitionRuleProjector.RenumberToVersion(source, 123456L);

        result.Should().Be(source); // 无继承行 → 不触发额外写
    }

    [Fact]
    public void RenumberToVersion_快照为null或空串_原样返回()
    {
        SetupTransitionRuleProjector.RenumberToVersion(null, 123456L).Should().BeNull();
        SetupTransitionRuleProjector.RenumberToVersion(string.Empty, 123456L).Should().Be(string.Empty);
    }

    [Fact]
    public void RenumberToVersion_子块JSON损坏_抛数据红线()
    {
        var malformed = "{\"SetupTransitionRules\": {\"bad\":true,"; // 损坏 JSON
        var act = () => SetupTransitionRuleProjector.RenumberToVersion(malformed, 123456L);
        act.Should().Throw<SetupRuleDataRedLineException>();
    }

    // ==================== 4号位 P0 Q2（2026-10-28）：⑦ 子块业务键对比口径 ====================
    // 版本 Diff 的 ⑦ 子块改按业务键对比（EXACT 七元组/DEFAULT 五元组），非原始 JSON 串；
    // 单源键 = SetupTransitionRuleProjector.BuildBusinessKey（SetupRuleService.BuildKey 委托此处防漂移）。

    private static SetupTransitionRule Rule2(
        string op = "OP20", int? from = null, int? to = null, string ruleType = SetupTransitionRuleType.Default,
        decimal minutes = 30m, long id = 0)
        => new SetupTransitionRule
        {
            Id = id,
            ProductionDepartmentId = 1,
            StageCode = "S1",
            OperationCode = op,
            ResourceId = 10,
            FromMaterialId = from,
            ToMaterialId = to,
            RuleType = ruleType,
            SetupMinutes = minutes,
            IsActive = true
        };

    [Fact]
    public void CompareBusinessContent_键相同仅Id或顺序变化_不判变更()
    {
        var src = new List<SetupTransitionRule> { Rule2(op: "OP10", minutes: 30m, id: 111L), Rule2(op: "OP20", minutes: 40m, id: 222L) };
        var tgt = new List<SetupTransitionRule> { Rule2(op: "OP20", minutes: 40m, id: 999L), Rule2(op: "OP10", minutes: 30m, id: 888L) };

        var diff = SetupTransitionRuleProjector.CompareBusinessContent(src, tgt);

        diff.IsChanged.Should().BeFalse(); // Q1 重编 Id 高位、行序翻转均非业务变更
        diff.AddedCount.Should().Be(0);
        diff.ModifiedCount.Should().Be(0);
        diff.RemovedCount.Should().Be(0);
    }

    [Fact]
    public void CompareBusinessContent_同键分钟变化_判Modified()
    {
        var src = new List<SetupTransitionRule> { Rule2(op: "OP10", minutes: 30m) };
        var tgt = new List<SetupTransitionRule> { Rule2(op: "OP10", minutes: 45m) };

        var diff = SetupTransitionRuleProjector.CompareBusinessContent(src, tgt);

        diff.IsChanged.Should().BeTrue();
        diff.ModifiedCount.Should().Be(1);
    }

    [Fact]
    public void CompareBusinessContent_键增删_判加删()
    {
        var src = new List<SetupTransitionRule> { Rule2(op: "OP10"), Rule2(op: "OP20") };
        var tgt = new List<SetupTransitionRule> { Rule2(op: "OP20"), Rule2(op: "OP30") };

        var diff = SetupTransitionRuleProjector.CompareBusinessContent(src, tgt);

        diff.AddedCount.Should().Be(1);   // OP30
        diff.RemovedCount.Should().Be(1); // OP10
        diff.ModifiedCount.Should().Be(0);
        diff.IsChanged.Should().BeTrue();
    }

    [Fact]
    public void CompareBusinessContent_空列表合法缺配_空对比()
    {
        var diff1 = SetupTransitionRuleProjector.CompareBusinessContent(new List<SetupTransitionRule>(), new List<SetupTransitionRule>());
        diff1.IsChanged.Should().BeFalse();

        var diff2 = SetupTransitionRuleProjector.CompareBusinessContent(
            new List<SetupTransitionRule> { Rule2(op: "OP10") }, new List<SetupTransitionRule>());
        diff2.RemovedCount.Should().Be(1);
        diff2.IsChanged.Should().BeTrue();
    }

    [Fact]
    public void BuildBusinessKey_单源_与GetDiff同口径()
    {
        // 与 SetupRuleService.BuildKey 同一实现（已委托到本方法），验证键含类型前缀 + 业务元组
        var exact = Rule2(op: "OP10", from: 101, to: 102, ruleType: SetupTransitionRuleType.Exact);
        var def = Rule2(op: "OP10", ruleType: SetupTransitionRuleType.Default);

        SetupTransitionRuleProjector.BuildBusinessKey(exact).Should().Be("E|1|S1|OP10|10|101|102");
        SetupTransitionRuleProjector.BuildBusinessKey(def).Should().Be("D|1|S1|OP10|10");
    }
}
