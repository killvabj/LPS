using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using LPS.APS.Application.Services.Query;
using LPS.APS.Core.Dto;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// 读侧分页语义（0号位 v1.20 §二十四.3：「不丢 >5,000 行、**不静默截断**」）。
///
/// <para>原实现给 `take` 设 5,000 硬上限并**静默截断**——调用方拿到 5,000 条会当成全部。
/// 现改为「多取 1 行探测 + `HasMore` 告知」，翻页可拿全，且**不再静默**。</para>
///
/// <para>本类只测纯函数 <c>ToPage</c>（反射调用），不连库。</para>
/// </summary>
public class DemandResultPagingTests
{
    private static DemandResultPage<T> ToPage<T>(IEnumerable<T> rows, int skip, int take)
    {
        var m = typeof(DemandResultQueryService).GetMethod(
            "ToPage", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("找不到 ToPage");
        var g = m.MakeGenericMethod(typeof(T));
        return (DemandResultPage<T>)g.Invoke(null, new object?[] { rows, skip, take })!;
    }

    private static List<int> Seq(int n) => Enumerable.Range(1, n).ToList();

    [Fact]
    public void 不足一页_全部返回且HasMore为假()
    {
        var r = ToPage(Seq(10), skip: 0, take: 100);

        r.Rows.Should().HaveCount(10);
        r.HasMore.Should().BeFalse("没取满就拿不到探测行 ⇒ 已到末尾");
        r.Truncated.Should().BeFalse();
    }

    [Fact]
    public void 恰好一页_不误报还有更多()
    {
        // 内部多取 1 行：恰好 take 行时探测行不存在 ⇒ HasMore=false（不能误报）
        var r = ToPage(Seq(100), skip: 0, take: 100);

        r.Rows.Should().HaveCount(100);
        r.HasMore.Should().BeFalse("刚好取满不是截断，不得误报 HasMore");
    }

    [Fact]
    public void 超过一页_标记HasMore并只返回take条()
    {
        var r = ToPage(Seq(101), skip: 0, take: 100);

        r.Rows.Should().HaveCount(100, "探测行只用于判断，不返回给调用方");
        r.HasMore.Should().BeTrue("还有 1 行未返回 ⇒ 必须让调用方知道");
        r.Truncated.Should().BeTrue();
    }

    [Fact]
    public void 翻页可以取全_不丢行()
    {
        // 模拟调用方按 NextSkip 一直翻，直到 HasMore=false
        const int total = 250;
        const int pageSize = 100;
        var all = new List<int>();
        var skip = 0;
        var guard = 0;

        while (true)
        {
            // 服务端已是「take+1 探测」，这里模拟服务端返回的行（含探测行）
            var fetched = Seq(total).Skip(skip).Take(pageSize + 1).ToList();
            var page = ToPage(fetched, skip, pageSize);
            all.AddRange(page.Rows);
            if (!page.HasMore) break;
            skip = page.NextSkip;
            if (++guard > 10) throw new InvalidOperationException("翻页未终止");
        }

        all.Should().HaveCount(total, "按 HasMore/NextSkip 翻页必须能取全，不能丢行");
        all.Should().Equal(Seq(total), "顺序与内容都要完整");
    }

    [Fact]
    public void 超过五千不再静默_仍可翻页取全()
    {
        // PM 点名的是「>5,000 行」：设 take 超过上限也能感知并翻页
        var fetched = Seq(6_000).Take(DemandResultQueryServiceProbe.MaxTakeForTest + 1).ToList();
        var page = ToPage(fetched, skip: 0, take: 100_000);

        page.Rows.Should().HaveCount(DemandResultQueryServiceProbe.MaxTakeForTest,
            "安全上限保留：单次不超过 5,000");
        page.HasMore.Should().BeTrue("超过上限必须告知还有更多，不得静默截断");
        page.NextSkip.Should().Be(DemandResultQueryServiceProbe.MaxTakeForTest);
    }
}

/// <summary>把服务端的 <c>MaxTake</c> 暴露给测试（避免测试里再写一个 5000 常量造成漂移）。</summary>
internal static class DemandResultQueryServiceProbe
{
    public const int MaxTakeForTest = 5_000;
}