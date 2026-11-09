using System.Collections.Generic;
using System.Linq;
using LPS.APS.Application.Services;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 域间依赖阻断的两条判据（2026-11-09 O52）：
///
///  A. **SCC 缩点** —— 同一强连通分量内的域不互相阻断。
///     背景：`Domain_Dependency` 的边按「半成品物料在域间流动」聚合，双向流动必然成环
///     （实测 2026-11-09：CYL↔TZP／QT↔XC／CYL↔VALVE／TZP↔LGJ 四组）。
///     环内若按传递闭包互相阻断，任一域失败会把整环连坐。
///
///  B. **真影响判据**（受影响订单为 0 则不阻断）走真实库查询，不在此单测覆盖；
///     其实测校准见 Run 999423：LGJ 593→0／TZP 20,782→16／VALVE 8,884→38／WKQ 126→73。
/// </summary>
public class DependencyBlockingTests
{
    private static List<(string Upstream, string Downstream)> Edges(params (string, string)[] pairs)
        => pairs.Select(p => (p.Item1, p.Item2)).ToList();

    [Fact(DisplayName = "无环依赖：上游入闭包，下游不被反向污染")]
    public void 无环依赖_闭包方向正确()
    {
        var domains = new[] { "A", "B" };
        var edges = Edges(("A", "B"));

        var closure = SchedulingOrchestrator.BuildUpstreamClosure(edges, domains);

        Assert.Contains("A", closure["B"]);      // B 的上游含 A
        Assert.Empty(closure["A"]);              // A 无上游
    }

    [Fact(DisplayName = "二节点成环：环内互不阻断（缩点后不连边）")]
    public void 二节点成环_环内互不阻断()
    {
        var domains = new[] { "CYL", "TZP" };
        var edges = Edges(("CYL", "TZP"), ("TZP", "CYL"));

        var closure = SchedulingOrchestrator.BuildUpstreamClosure(edges, domains);

        Assert.Empty(closure["CYL"]);
        Assert.Empty(closure["TZP"]);   // 关键：缩点前这里会含 CYL，环内一失败就连坐
    }

    [Fact(DisplayName = "环外上游仍传播：JS→(LGJ↔TZP) 时 LGJ/TZP 的上游含 JS，但 CYL 不含")]
    public void 环外上游仍传播_环内不传染()
    {
        // 复刻实测形状：JS 指向 LGJ 与 TZP；LGJ↔TZP 成环；CYL↔TZP 成环 ⇒ {LGJ,TZP,CYL} 同一分量
        var domains = new[] { "JS", "LGJ", "TZP", "CYL" };
        var edges = Edges(
            ("JS", "LGJ"), ("JS", "TZP"),
            ("LGJ", "TZP"), ("TZP", "LGJ"),
            ("CYL", "TZP"), ("TZP", "CYL"));

        var closure = SchedulingOrchestrator.BuildUpstreamClosure(edges, domains);

        Assert.Contains("JS", closure["LGJ"]);   // 跨分量 ⇒ 传播
        Assert.Contains("JS", closure["TZP"]);   // 跨分量 ⇒ 传播
        Assert.DoesNotContain("TZP", closure["LGJ"]);  // 同分量 ⇒ 不传染
        Assert.DoesNotContain("LGJ", closure["TZP"]);
        Assert.DoesNotContain("JS", closure["CYL"]);   // CYL 只经环内到 TZP，缩点后断开
    }

    [Fact(DisplayName = "三节点成环：三者同一分量，闭包全空")]
    public void 三节点成环_闭包全空()
    {
        var domains = new[] { "X", "Y", "Z" };
        var edges = Edges(("X", "Y"), ("Y", "Z"), ("Z", "X"));

        var closure = SchedulingOrchestrator.BuildUpstreamClosure(edges, domains);

        Assert.Empty(closure["X"]);
        Assert.Empty(closure["Y"]);
        Assert.Empty(closure["Z"]);
    }

    [Fact(DisplayName = "边端点不在本 Run 域集合内：忽略该边，不抛异常")]
    public void 域集合外的边被忽略()
    {
        var domains = new[] { "A", "B" };
        var edges = Edges(("A", "B"), ("B", "OUTSIDE"), ("OUTSIDE", "A"));

        var closure = SchedulingOrchestrator.BuildUpstreamClosure(edges, domains);

        Assert.Contains("A", closure["B"]);
        Assert.Empty(closure["A"]);
    }

    [Fact(DisplayName = "无依赖边：闭包为空（V1 降级为冻结顺序，不阻断）")]
    public void 无依赖边_不阻断()
    {
        var domains = new[] { "A", "B", "C" };

        var closure = SchedulingOrchestrator.BuildUpstreamClosure(
            new List<(string, string)>(), domains);

        Assert.Empty(closure);   // 与既有契约一致：edges 为空即返回空字典
    }
}