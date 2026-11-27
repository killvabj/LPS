using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using LPS.APS.Application.Services;
using LPS.APS.Core.Dto;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// A35「技术异常持久化」投影语义钉死测试（0号位 2026-11-24 裁决 v1.1 §3 / v1.2 §6）。
///
/// <para><b>核心风险：多报</b>。裁决逐字：「289 条 RoleConflict <b>不等于</b> 289 条同型号歧义。
/// 逐 Demand 按合法 Workset 选出链后应恢复正常需求；<b>只有真实身份不可判明的具体边</b>记技术 Issue。」
/// ⇒ 把数据缺口（无链、无锚点、无该物料行）误收进来，等于把「缺数据」永久钉成「出问题」。</para>
/// </summary>
public class DemandTechnicalIssueProjectorTests
{
    private const int Pv = 999634;
    private const int Run = 999539;
    private const string Domain = "FAMILY_FRL";

    private static LogicalProductionDemand Demand(string key, long? orderId = 7, int materialId = 5001)
        => new()
        {
            LogicalDemandKey = key,
            PlanVersionId = Pv,
            DomainKey = Domain,
            DemandKey = "ORDER_7_M_1",
            OrderId = orderId,
            IsRootDemand = true,
            BomNo = "B1",
            RepWorksetId = 11,
            MaterialId = materialId,
            NetOutputQty = 100m,
            StartStageCode = "TJ_MACH"
        };

    private static DemandStageChainBinding Binding(
        string key, string status, string? bindingKey = "ROOT|B1|11",
        IReadOnlyList<string>? candidates = null)
        => new()
        {
            LogicalDemandKey = key,
            BindingKey = bindingKey,
            ChainRefId = "ROOT:~PA~11~B1",   // 单边默认给一个含父件的实例键
            Status = status,
            StageScopeType = "ROOT",
            ResolvedBOMNO = "B1",
            RepWorksetId = 11,
            CandidateChainSignatures = candidates ?? new[] { "10:A|20:B", "10:A|20:C" },
            CandidateChainCount = candidates?.Count ?? 2
        };

    // ── ① 只收 CONFLICT（真身份不可判明）────────────────────────────────────
    [Fact]
    public void 真身份歧义_CONFLICT_落一行()
    {
        var r = DemandTechnicalIssueProjector.Project(
            Pv, Run, Domain, new[] { Demand("K1") },
            new[] { Binding("K1", StageChainBindingStatus.Conflict) });

        var row = r.Rows.Single();
        row.LogicalDemandKey.Should().Be("K1");
        row.IssueCode.Should().Be(DemandScheduleIssueCode.StageChainIdentityAmbiguous);
        row.Severity.Should().Be("ERROR", "Fail-Closed 的需求排不出来，WARN 会被当成可忽略");
        row.IssueStatus.Should().Be(DemandScheduleIssueStatus.Detected, "0号位 已裁显式 DETECTED，不靠列默认值");
        row.BindingKey.Should().Be("ROOT|B1|11");
    }

    // ── ② 其余状态一律不收（这是本表最大的误报风险）──────────────────────────
    [Theory]
    [InlineData(StageChainBindingStatus.NoChain)]        // 本请求/本角色下无链：事实缺口
    [InlineData(StageChainBindingStatus.NoAnchor)]       // 三键全空：无权威身份
    [InlineData(StageChainBindingStatus.UnverifiedIdentity)] // 有 BOMNO 缺二层锚点，但候选唯一：合法兼容
    [InlineData(StageChainBindingStatus.ParentEdgeScoped)]   // EDGE 仅父边收窄：口径上限，不是缺陷
    [InlineData(StageChainBindingStatus.Resolved)]           // 正常
    public void 非CONFLICT状态_一律不落(string status)
    {
        var r = DemandTechnicalIssueProjector.Project(
            Pv, Run, Domain, new[] { Demand("K1") }, new[] { Binding("K1", status) });

        r.Rows.Should().BeEmpty($"{status} 不是「真实身份不可判明」，收进来就是把缺数据/合法形态误标成异常");
    }

    // ── ③ 一条合法、一条歧义：只报歧义那条，合法单不受影响 ───────────────────
    [Fact]
    public void 只报歧义需求_合法邻单不受影响()
    {
        var r = DemandTechnicalIssueProjector.Project(
            Pv, Run, Domain,
            new[] { Demand("K1"), Demand("K2") },
            new[]
            {
                Binding("K1", StageChainBindingStatus.Resolved),
                Binding("K2", StageChainBindingStatus.Conflict)
            });

        r.Rows.Should().ContainSingle().Which.LogicalDemandKey.Should().Be("K2",
            "裁决要求「只隔离确有歧义的绑定/需求，保留合法邻单」");
    }

    // ── ④ 证据里给候选链，但不复制数量 ──────────────────────────────────────
    [Fact]
    public void 证据含候选链签名_但不含数量数值()
    {
        var r = DemandTechnicalIssueProjector.Project(
            Pv, Run, Domain, new[] { Demand("K1") },
            new[] { Binding("K1", StageChainBindingStatus.Conflict) });

        var ev = r.Rows.Single().EvidenceJson!;
        ev.Should().Contain("10:A|20:B").And.Contain("10:A|20:C", "候选链是歧义的证据本体");
        ev.Should().Contain("UncommittedQtyRef", "裁决要求引用数量出处");
        ev.Should().NotContain("100", "不得复制第二份权威数量（需求净量 100 不该出现）");
    }

    // ── ⑤ 唯一键去重 ───────────────────────────────────────────────────────
    [Fact]
    public void 同需求同绑定键重复_只落一行()
    {
        var r = DemandTechnicalIssueProjector.Project(
            Pv, Run, Domain, new[] { Demand("K1") },
            new[]
            {
                Binding("K1", StageChainBindingStatus.Conflict),
                Binding("K1", StageChainBindingStatus.Conflict)
            });

        r.Rows.Should().ContainSingle("唯一键 (PV,需求键,绑定键,技术码) 不允许重复");
        r.ConflictDemandCount.Should().Be(2, "冲突计数按绑定条数，不因去重而少报");
    }

    // ── ⑤c 共享供给批：同需求两条父边同形状键 ⇒ 一行保住、两条边全在证据里 ──────
    //
    // 0号位 v1.20 §二十五.3 场景：同一合法供给批、相同阈值与到达时点，
    // 父件 A 承接 100、父件 B 承接 200 —— 两条边必须**都可追溯**，不能因形状键相同而丢一条。
    [Fact]
    public void 同需求两条父边同形状键_不丢边_两条ChainRefId都在证据里()
    {
        // 形状键相同（同 scope|BOMNO|workset），但父件不同 ⇒ ChainRefId 不同
        var b1 = Binding("K1", StageChainBindingStatus.Conflict, "ROOT|B1|11", new[] { "10:A|20:B" });
        var b2 = new DemandStageChainBinding
        {
            LogicalDemandKey = "K1",
            BindingKey = "ROOT|B1|11",          // ← 与 b1 同形状键（不含父件）
            Status = StageChainBindingStatus.Conflict,
            StageScopeType = "ROOT",
            ResolvedBOMNO = "B1",
            RepWorksetId = 11,
            ChainRefId = "ROOT:~PA~11~B1",      // ← 父件进实例键 ⇒ 与 b1 不同
            CandidateChainSignatures = new[] { "10:A|20:C" },
            CandidateChainCount = 1
        };

        var r = DemandTechnicalIssueProjector.Project(
            Pv, Run, Domain, new[] { Demand("K1") }, new[] { b1, b2 });

        r.Rows.Should().ContainSingle("唯一键不含父件，拆两行会插入失败 ⇒ 必须一行");
        var ev = r.Rows.Single().EvidenceJson!;
        ev.Should().Contain("ROOT:~PA~11~B1", "第二条边的 ChainRefId 不得丢");
        ev.Should().Contain("\"EdgeCount\":2", "合并了几条边必须写进证据");
        r.MultiEdgeCollapsedCount.Should().Be(1, "发生合并要可察觉");
        r.CollapsedEdgeTotal.Should().Be(2, "合并的边总数=2，证明没丢");
    }

    [Fact]
    public void 单边场景_不产生合并计数()
    {
        var r = DemandTechnicalIssueProjector.Project(
            Pv, Run, Domain, new[] { Demand("K1") },
            new[] { Binding("K1", StageChainBindingStatus.Conflict) });

        r.MultiEdgeCollapsedCount.Should().Be(0);
        r.Rows.Single().EvidenceJson.Should().Contain("\"EdgeCount\":1");
    }

    // ── ⑤b 证据里必须带 ChainRefId（BindingKey 不含父件，单独它追溯不到边）────
    [Fact]
    public void 证据必须带ChainRefId否则追溯不到父子边()
    {
        var r = DemandTechnicalIssueProjector.Project(
            Pv, Run, Domain, new[] { Demand("K1") },
            new[] { Binding("K1", StageChainBindingStatus.Conflict,
                            candidates: new[] { "10:A|20:B" }) });

        var ev = r.Rows.Single().EvidenceJson!;
        ev.Should().Contain("ChainRefId",
            "BindingKey=作用域|BOMNO|RepWorksetId 不含父件；" +
            "EDGE 有 25.7% 的子件挂多父件 ⇒ 必须靠 ChainRefId 才能定位到底用了哪条边");
    }

    // ── ⑤d 需求级角色冲突：仅当同一需求挂了不同 StageScopeType 才记 ROLE_CONFLICT ──
    //
    // 0号位 v1.20 §二十四.3：真实**需求级**角色冲突按既有裁决实现；
    // 「合法不同订单不同链」**不得误标**（那是物料级多链，不是需求级冲突）。
    [Fact]
    public void 同一需求两条绑定scope不同_记角色冲突()
    {
        var b1 = Binding("K1", StageChainBindingStatus.Conflict, "ROOT|B1|11");
        var b2 = new DemandStageChainBinding
        {
            LogicalDemandKey = "K1",
            BindingKey = "ROOT|B1|11",
            Status = StageChainBindingStatus.Conflict,
            StageScopeType = "EDGE",          // ← 同需求却出现另一种角色
            ResolvedBOMNO = "B1",
            RepWorksetId = 11,
            ChainRefId = "EDGE:~PA~11~B1",
            CandidateChainSignatures = new[] { "10:A|20:C" },
            CandidateChainCount = 1
        };

        var r = DemandTechnicalIssueProjector.Project(
            Pv, Run, Domain, new[] { Demand("K1") }, new[] { b1, b2 });

        r.Rows.Single().IssueCode.Should().Be(DemandScheduleIssueCode.StageChainRoleConflict,
            "同需求不同角色 ⇒ 真实需求级角色冲突");
    }

    [Fact]
    public void 同需求同角色_仍记身份歧义而不是角色冲突()
    {
        var b1 = Binding("K1", StageChainBindingStatus.Conflict, "ROOT|B1|11");
        var b2 = new DemandStageChainBinding
        {
            LogicalDemandKey = "K1",
            BindingKey = "ROOT|B1|11",
            Status = StageChainBindingStatus.Conflict,
            StageScopeType = "ROOT",          // 同角色、不同父边
            ResolvedBOMNO = "B1",
            RepWorksetId = 11,
            ChainRefId = "ROOT:~PA~11~B1",
            CandidateChainSignatures = new[] { "10:A|20:C" },
            CandidateChainCount = 1
        };

        var r = DemandTechnicalIssueProjector.Project(
            Pv, Run, Domain, new[] { Demand("K1") }, new[] { b1, b2 });

        r.Rows.Single().IssueCode.Should().Be(DemandScheduleIssueCode.StageChainIdentityAmbiguous,
            "同角色多父边不是角色冲突；不得因多条边就误标成 ROLE_CONFLICT");
    }

    // ── ⑥ 绑定找不到需求 ⇒ 不产行但计数 ─────────────────────────────────────
    [Fact]
    public void 绑定无对应需求_不产行且计数()
    {
        var r = DemandTechnicalIssueProjector.Project(
            Pv, Run, Domain, Array.Empty<LogicalProductionDemand>(),
            new[] { Binding("K1", StageChainBindingStatus.Conflict) });

        r.Rows.Should().BeEmpty();
        r.ConflictWithoutDemand.Should().Be(1, "少写必须可数");
    }

    // ── ⑦ 空输入不炸 ───────────────────────────────────────────────────────
    [Fact]
    public void 无绑定_返回空且不抛()
    {
        var r = DemandTechnicalIssueProjector.Project(
            Pv, Run, Domain, new[] { Demand("K1") }, null);

        r.Rows.Should().BeEmpty();
        r.ConflictDemandCount.Should().Be(0);
    }

    // ── ⑧ CAST/MACH 同型号实体歧义（0号位 11-24 v1.2 §6 判据，2026-11-27 实现）──────
    //   判据：同一物料的候选链**分别以 CAST 阶段与 MACH 阶段为首** ⇒ 同一 ERP 物料号
    //   同时表达 CAST 半成品与 MACH 完成品，身份不可唯一解析。
    [Fact(DisplayName = "正例：候选链首分属 CAST 与 MACH 两族 ⇒ 记 CAST_MACH")]
    public void 候选链首分属CAST与MACH_记同型号歧义()
    {
        var r = DemandTechnicalIssueProjector.Project(
            Pv, Run, Domain, new[] { Demand("K1") },
            new[] { Binding("K1", StageChainBindingStatus.Conflict, candidates: new[] { "10:BJ_CAST", "10:BJ_MACH" }) });

        r.Rows.Single().IssueCode.Should().Be(DemandScheduleIssueCode.MaterialIdentityAmbiguousCastMach,
            "同一物料号既出 CAST 半成品链又出 MACH 完成品链 ⇒ 同型号实体歧义");
    }

    [Fact(DisplayName = "反例：候选链首同属 MACH 族 ⇒ 不得误标 CAST_MACH")]
    public void 候选链首同属MACH族_不记同型号歧义()
    {
        var r = DemandTechnicalIssueProjector.Project(
            Pv, Run, Domain, new[] { Demand("K1") },
            new[] { Binding("K1", StageChainBindingStatus.Conflict, candidates: new[] { "10:BJ_MACH", "10:CN_MACH" }) });

        r.Rows.Single().IssueCode.Should().Be(DemandScheduleIssueCode.StageChainIdentityAmbiguous,
            "两条都是 MACH 首 ⇒ 只是身份歧义，不得标成 CAST/MACH 实体歧义");
    }

    [Fact(DisplayName = "反例：单条链内部 CAST→MACH 是正常工艺顺序，不构成同型号歧义")]
    public void 单链内CAST到MACH_不记同型号歧义()
    {
        var r = DemandTechnicalIssueProjector.Project(
            Pv, Run, Domain, new[] { Demand("K1") },
            new[] { Binding("K1", StageChainBindingStatus.Conflict, candidates: new[] { "10:BJ_CAST|20:BJ_MACH" }) });

        r.Rows.Single().IssueCode.Should().Be(DemandScheduleIssueCode.StageChainIdentityAmbiguous,
            "单条链内 CAST 之后接 MACH 是工艺顺序，不是两种实体");
    }
}
