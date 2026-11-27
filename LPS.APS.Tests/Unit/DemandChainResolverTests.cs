using System;
using System.Collections.Generic;
using System.Linq;
using LPS.APS.Core.Dto;
using LPS.APS.Scheduling.Solvers;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **逐 Demand 取链的唯一入口**（0号位 2026-11-24 v1.2 §3、2026-11-25 联合验收；2号位 r14415/r14417）。
///
/// <para>改造前 1号位 有 5 处取链点、3 种口径（first-wins / last-wins / **并集**），
///   同一物料多链时会让不同需求拿到彼此的链且**不报错**。本组锁定收敛后的单一规则。</para>
///
/// <para>优先级（与 2号位 2026-11-25 答复一致）：
///   绑定可用 ⇒ 按 ChainRefId 取；绑定 NO_CHAIN/NO_ANCHOR/CONFLICT ⇒ **无链且不回退**；
///   无绑定行 ⇒ 回落物料级兼容载体；多绑定且链不一致 ⇒ 歧义、无链。</para>
///
/// ⚠ 纯内存：不读文件、不连库。
/// </summary>
public class DemandChainResolverTests
{
    private const int MatA = 1001;
    private const int MatB = 1002;

    private static IReadOnlyList<StageSequenceStep> Steps(params (int Seq, string Code)[] xs)
        => xs.Select(x => new StageSequenceStep { StageSeq = x.Seq, StageCode = x.Code }).ToList();

    private static LogicalProductionDemand Demand(string key, int material)
        => new() { LogicalDemandKey = key, MaterialId = material };

    private static DemandStageChainBinding Binding(
        string key, string chainRefId, string status = StageChainBindingStatus.Resolved,
        string bindingKey = "B1", string scopeType = "")
        => new()
        {
            LogicalDemandKey = key,
            BindingKey = bindingKey,
            ChainRefId = chainRefId,
            Status = status,
            StageScopeType = scopeType
        };

    private static StageChainDefinition Def(
        string chainRefId, IReadOnlyList<StageSequenceStep> stages, string scopeType = "")
        => new()
        {
            ChainRefId = chainRefId, ChainSignature = chainRefId, Stages = stages,
            StageScopeType = scopeType
        };

    private static DemandChainResolver.Index Index(
        IEnumerable<StageSequenceChain>? legacy = null,
        IEnumerable<StageChainDefinition>? defs = null,
        IEnumerable<DemandStageChainBinding>? binds = null)
        => DemandChainResolver.BuildIndex(new DomainSolveRequest
        {
            StageSequenceChains = legacy?.ToList() ?? new List<StageSequenceChain>(),
            StageChainDefinitions = defs?.ToList() ?? new List<StageChainDefinition>(),
            DemandStageChainBindings = binds?.ToList() ?? new List<DemandStageChainBinding>()
        });

    [Fact]
    public void 绑定可用时_按ChainRefId取链_而不是物料级兼容载体()
    {
        var idx = Index(
            legacy: new[] { new StageSequenceChain { MaterialId = MatA, Stages = Steps((10, "LEGACY")) } },
            defs: new[] { Def("ROOT:10:CN_MACH", Steps((10, "CN_MACH"))) },
            binds: new[] { Binding("D1", "ROOT:10:CN_MACH") });

        var ok = DemandChainResolver.TryResolve(idx, Demand("D1", MatA), out var stages, out var src);

        Assert.True(ok);
        Assert.Equal(DemandChainResolver.ChainSource.Binding, src);
        Assert.Equal("CN_MACH", Assert.Single(stages).StageCode);   // 不得取到 LEGACY
    }

    /// <summary>核心场景：同物料两需求分别绑到两条不同的合法链，各取各的。</summary>
    [Fact]
    public void 同物料两需求各绑不同链_各取各的不互相覆盖()
    {
        var idx = Index(
            // 旧载体只有一条（改造前 last-wins 会让两条需求都拿到它）
            legacy: new[] { new StageSequenceChain { MaterialId = MatA, Stages = Steps((10, "TJ_MACH")) } },
            defs: new[]
            {
                Def("ROOT:10:TJ_MACH",  Steps((10, "TJ_MACH"))),
                Def("ROOT:10:CN6_MACH", Steps((10, "CN6_MACH")))
            },
            binds: new[]
            {
                Binding("D1", "ROOT:10:TJ_MACH"),
                Binding("D2", "ROOT:10:CN6_MACH")
            });

        Assert.True(DemandChainResolver.TryResolve(idx, Demand("D1", MatA), out var s1, out _));
        Assert.True(DemandChainResolver.TryResolve(idx, Demand("D2", MatA), out var s2, out _));

        Assert.Equal("TJ_MACH", Assert.Single(s1).StageCode);
        Assert.Equal("CN6_MACH", Assert.Single(s2).StageCode);
    }

    [Theory]
    [InlineData(StageChainBindingStatus.NoChain)]
    [InlineData(StageChainBindingStatus.NoAnchor)]
    [InlineData(StageChainBindingStatus.Conflict)]
    public void 绑定为无链状态时_不回退物料级兼容载体(string status)
    {
        var idx = Index(
            legacy: new[] { new StageSequenceChain { MaterialId = MatA, Stages = Steps((10, "LEGACY")) } },
            defs: new[] { Def("X", Steps((10, "X"))) },
            binds: new[] { Binding("D1", "X", status) });

        var ok = DemandChainResolver.TryResolve(idx, Demand("D1", MatA), out var stages, out var src);

        Assert.False(ok);
        Assert.Empty(stages);
        Assert.Equal(DemandChainResolver.ChainSource.BindingNoChain, src);
    }

    [Fact]
    public void 无绑定行时_回落物料级兼容载体_行为等价改造前()
    {
        var idx = Index(legacy: new[] { new StageSequenceChain { MaterialId = MatA, Stages = Steps((10, "TA"), (20, "TB")) } });

        var ok = DemandChainResolver.TryResolve(idx, Demand("D1", MatA), out var stages, out var src);

        Assert.True(ok);
        Assert.Equal(DemandChainResolver.ChainSource.LegacyMaterial, src);
        Assert.Equal(new[] { "TA", "TB" }, stages.Select(s => s.StageCode));
    }

    [Fact]
    public void 同Ref多绑定但Status不一致_判冲突无链_不取首条()
    {
        // 0号位 v1.3 §4.5：「多绑定仅比 Ref 再首条 Status」是缺口。
        //   同一条链，一条说 RESOLVED、一条说 NO_CHAIN ⇒ 状态自相矛盾，不得取首条了事。
        var idx = Index(
            defs: new[] { Def("A", Steps((10, "A1"))) },
            binds: new[]
            {
                Binding("D1", "A", status: StageChainBindingStatus.Resolved, bindingKey: "K1"),
                Binding("D1", "A", status: StageChainBindingStatus.NoChain, bindingKey: "K2")
            });

        var ok = DemandChainResolver.TryResolve(idx, Demand("D1", MatA), out var stages, out var src);

        Assert.False(ok);
        Assert.Equal(DemandChainResolver.ChainSource.BindingStatusConflict, src);
        Assert.Empty(stages);
    }

    [Fact]
    public void 绑定角色与链定义角色不一致_判角色不匹配_不借另一角色的链()
    {
        // 0号位 v1.3 §4.5「错误角色不得借链」：ROOT 绑定指向 EDGE 链 ⇒ 无链。
        var idx = Index(
            defs: new[] { Def("A", Steps((10, "A1")), scopeType: "EDGE") },
            binds: new[] { Binding("D1", "A", scopeType: "ROOT") });

        var ok = DemandChainResolver.TryResolve(idx, Demand("D1", MatA), out var stages, out var src);

        Assert.False(ok);
        Assert.Equal(DemandChainResolver.ChainSource.BindingRoleMismatch, src);
        Assert.Empty(stages);
    }

    [Fact]
    public void 同ChainRefId重复定义但内容等价_去重放行不判冲突()
    {
        // §4.5：「仅经证实**等价**且契约允许才可追溯去重」
        var idx = Index(
            defs: new[]
            {
                Def("A", Steps((10, "A1"), (20, "A2"))),
                Def("A", Steps((10, "A1"), (20, "A2")))
            },
            binds: new[] { Binding("D1", "A") });

        var ok = DemandChainResolver.TryResolve(idx, Demand("D1", MatA), out var stages, out var src);

        Assert.True(ok);
        Assert.Equal(DemandChainResolver.ChainSource.Binding, src);
        Assert.Equal(new[] { "A1", "A2" }, stages.Select(s => s.StageCode));
    }

    [Fact]
    public void 同ChainRefId重复定义且内容不等价_判定义冲突_不任取其一()
    {
        var idx = Index(
            defs: new[]
            {
                Def("A", Steps((10, "A1"), (20, "A2"))),
                Def("A", Steps((10, "A1"), (20, "A9")))   // 同 Ref、不同内容
            },
            binds: new[] { Binding("D1", "A") });

        var ok = DemandChainResolver.TryResolve(idx, Demand("D1", MatA), out _, out var src);

        Assert.False(ok);
        Assert.Equal(DemandChainResolver.ChainSource.DefinitionConflict, src);
    }

    [Fact]
    public void 新契约在场但内容全无效_判BindingAbsent_不因漏装退回旧载体()
    {
        // §4.5：「新合同**不可因漏装退旧**」——
        //   上游发了 StageChainDefinitions 但每条都缺 ChainRefId ⇒ 索引后为空，
        //   若判据用「索引成功数」就会误判成老载荷并静默回落到物料级旧链。此处必须判为新契约在场。
        var idx = Index(
            legacy: new[] { new StageSequenceChain { MaterialId = MatA, Stages = Steps((10, "LEGACY")) } },
            defs: new[] { Def(string.Empty, Steps((10, "X1"))) });

        var ok = DemandChainResolver.TryResolve(idx, Demand("D1", MatA), out var stages, out var src);

        Assert.False(ok);
        Assert.Equal(DemandChainResolver.ChainSource.BindingAbsent, src);
        Assert.Empty(stages);
    }

    [Fact]
    public void 多父边同为PARENT_EDGE_SCOPED且同链_不得判歧义()
    {
        // §4.5：合法多父边不得误阻——同 Ref、不同 BindingKey、状态 PARENT_EDGE_SCOPED ⇒ 应放行。
        var idx = Index(
            defs: new[] { Def("A", Steps((10, "A1"))) },
            binds: new[]
            {
                Binding("D1", "A", status: StageChainBindingStatus.ParentEdgeScoped, bindingKey: "K1"),
                Binding("D1", "A", status: StageChainBindingStatus.ParentEdgeScoped, bindingKey: "K2")
            });

        var ok = DemandChainResolver.TryResolve(idx, Demand("D1", MatA), out var stages, out var src);

        Assert.True(ok);
        Assert.Equal(DemandChainResolver.ChainSource.Binding, src);
        Assert.Single(stages);
    }

    [Fact]
    public void 新契约已生效但本需求无绑定行_判BindingAbsent_不回落旧载体()
    {
        // 0号位 2026-11-25 v1.2 DTO-08：「不允许新合同缺失时静默旧字段兜底」。
        //   本请求已带链目录 + 另一需求的绑定 ⇒ 新契约在场；D1 **没有**绑定行 ⇒ 契约缺口，
        //   即使物料级兼容载体里有链，也**不得**拿来用（否则缺口被静默掩盖）。
        var idx = Index(
            legacy: new[] { new StageSequenceChain { MaterialId = MatA, Stages = Steps((10, "LEGACY")) } },
            defs: new[] { Def("A", Steps((10, "A1"))) },
            binds: new[] { Binding("D2", "A") });

        var ok = DemandChainResolver.TryResolve(idx, Demand("D1", MatA), out var stages, out var src);

        Assert.False(ok);
        Assert.Equal(DemandChainResolver.ChainSource.BindingAbsent, src);
        Assert.Empty(stages);
    }

    [Fact]
    public void 整个请求都没带新契约时_仍回落旧载体_合法兼容边界()
    {
        var idx = Index(legacy: new[] { new StageSequenceChain { MaterialId = MatA, Stages = Steps((10, "LEGACY")) } });

        var ok = DemandChainResolver.TryResolve(idx, Demand("D1", MatA), out var stages, out var src);

        Assert.True(ok);
        Assert.Equal(DemandChainResolver.ChainSource.LegacyMaterial, src);
        Assert.Equal(new[] { "LEGACY" }, stages.Select(s => s.StageCode));
    }

    [Fact]
    public void 绑定指向不存在的ChainRefId_判悬空无链_不猜()
    {
        var idx = Index(
            legacy: new[] { new StageSequenceChain { MaterialId = MatA, Stages = Steps((10, "LEGACY")) } },
            defs: Array.Empty<StageChainDefinition>(),
            binds: new[] { Binding("D1", "ROOT:10:X") });

        var ok = DemandChainResolver.TryResolve(idx, Demand("D1", MatA), out _, out var src);

        Assert.False(ok);
        Assert.Equal(DemandChainResolver.ChainSource.BindingDangling, src);
    }

    [Fact]
    public void 同需求多绑定且链不一致_判歧义无链_不任取()
    {
        var idx = Index(
            defs: new[] { Def("A", Steps((10, "A"))), Def("B", Steps((10, "B"))) },
            binds: new[]
            {
                Binding("D1", "A", bindingKey: "B1"),
                Binding("D1", "B", bindingKey: "B2")
            });

        var ok = DemandChainResolver.TryResolve(idx, Demand("D1", MatA), out _, out var src);

        Assert.False(ok);
        Assert.Equal(DemandChainResolver.ChainSource.BindingAmbiguous, src);
    }

    [Fact]
    public void 同需求多绑定但链相同_按该链取_不判歧义()
    {
        var idx = Index(
            defs: new[] { Def("A", Steps((10, "A"))) },
            binds: new[]
            {
                Binding("D1", "A", bindingKey: "B1"),
                Binding("D1", "A", bindingKey: "B2")
            });

        var ok = DemandChainResolver.TryResolve(idx, Demand("D1", MatB), out var stages, out var src);

        Assert.True(ok);
        Assert.Equal(DemandChainResolver.ChainSource.Binding, src);
        Assert.Equal("A", Assert.Single(stages).StageCode);
    }

    [Fact]
    public void 两者皆无_返回无链且来源为None()
    {
        var idx = Index();

        var ok = DemandChainResolver.TryResolve(idx, Demand("D9", MatA), out var stages, out var src);

        Assert.False(ok);
        Assert.Empty(stages);
        Assert.Equal(DemandChainResolver.ChainSource.None, src);
    }

    // ────────────────────────────────────────────────────────────────────
    // 2026-11-25（0号位 v1.3 §4.5 剩余组合）：错误 ROOT/EDGE、悬空+多父边、部分合法/部分非法
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void EDGE绑定指向ROOT链_同样判角色不匹配_不借链()
    {
        // 上一条只测了 ROOT 绑 EDGE 链；此处是**镜像方向**，两条都必须 fail closed。
        var idx = Index(
            defs: new[] { Def("A", Steps((10, "A1")), scopeType: "ROOT") },
            binds: new[] { Binding("D1", "A", scopeType: "EDGE") });

        var ok = DemandChainResolver.TryResolve(idx, Demand("D1", MatA), out var stages, out var src);

        Assert.False(ok);
        Assert.Equal(DemandChainResolver.ChainSource.BindingRoleMismatch, src);
        Assert.Empty(stages);
    }

    [Fact]
    public void 悬空Ref被同一需求多条父边绑定_仍判悬空而非歧义()
    {
        // 组合：多父边（三条绑定）**且**都指向同一个不存在的 ChainRefId。
        //   判据：Ref 一致 ⇒ 不是歧义；找不到定义 ⇒ 悬空。两个结论不能互相掩盖。
        var idx = Index(
            defs: new[] { Def("EXISTS", Steps((10, "X"))) },
            binds: new[]
            {
                Binding("D1", "MISSING", bindingKey: "P1|EDGE"),
                Binding("D1", "MISSING", bindingKey: "P2|EDGE"),
                Binding("D1", "MISSING", bindingKey: "P3|EDGE")
            });

        var ok = DemandChainResolver.TryResolve(idx, Demand("D1", MatA), out var stages, out var src);

        Assert.False(ok);
        Assert.Equal(DemandChainResolver.ChainSource.BindingDangling, src);
        Assert.Empty(stages);
    }

    [Fact]
    public void 多父边同Ref但定义重复且不等价_判定义冲突而非歧义()
    {
        // 组合：Ref 一致（非歧义）＋ 定义重复不等价 ⇒ 定义冲突；不得因「多绑定」误判成歧义。
        var idx = Index(
            defs: new[]
            {
                Def("A", Steps((10, "A1"), (20, "A2"))),
                Def("A", Steps((10, "A1"), (20, "A9")))
            },
            binds: new[]
            {
                Binding("D1", "A", bindingKey: "P1|EDGE"),
                Binding("D1", "A", bindingKey: "P2|EDGE")
            });

        var ok = DemandChainResolver.TryResolve(idx, Demand("D1", MatA), out _, out var src);

        Assert.False(ok);
        Assert.Equal(DemandChainResolver.ChainSource.DefinitionConflict, src);
    }

    [Fact]
    public void 部分需求合法部分悬空_同请求内互不污染()
    {
        // 边界：同一份索引里 D1 绑到存在的链、D2 绑到不存在的链。
        //   D1 必须照常取到链（不能因同批有坏绑定而整体降级），D2 必须悬空且**不回落旧载体**。
        var idx = Index(
            legacy: new[] { new StageSequenceChain { MaterialId = MatA, Stages = Steps((10, "LEGACY")) } },
            defs: new[] { Def("OK", Steps((10, "S1"), (20, "S2"))) },
            binds: new[]
            {
                Binding("D1", "OK"),
                Binding("D2", "MISSING")
            });

        var ok1 = DemandChainResolver.TryResolve(idx, Demand("D1", MatA), out var s1, out var src1);
        var ok2 = DemandChainResolver.TryResolve(idx, Demand("D2", MatA), out var s2, out var src2);

        Assert.True(ok1);
        Assert.Equal(DemandChainResolver.ChainSource.Binding, src1);
        Assert.Equal(new[] { "S1", "S2" }, s1.Select(s => s.StageCode));

        Assert.False(ok2);
        Assert.Equal(DemandChainResolver.ChainSource.BindingDangling, src2);
        Assert.Empty(s2);   // 不回落同物料的 LEGACY
    }
}
