using System;
using System.Collections.Generic;
using System.Text;
using Xunit;
using LPS.APS.Scheduling.Solvers;
using LPS.APS.Shared.Models;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// **V1_3 F-01 判别性单测**（0号位 2026-10-10《APS_V1_3_20261010.md》§3，REWORK/BLOCKED）。
///
/// 复审判词（§7「1号位必须整改」第 1 条，逐字）：
///   「修 F-01 水位回滚，提供**真正中间插入 + OP 下游失败**的判别性测试；
///     静态断言失败前后**实际有序占用窗集合**一致，而非仅 Task 指纹。」
///
/// 缺陷本体：旧 `RollbackOccupancyToWatermark` 按**每资源窗数**截尾
///   （`windows.RemoveRange(count, windows.Count - count)`）；而 `AddOccupancyWindow` 是
///   **有序二分插入** ⇒ 倒排 / 合批落定会把新窗插进列表**中间** ⇒ 截尾删掉的是**列表尾部的旧合法窗**，
///   试排新增的中间窗反而留下：既留幽灵占用（INV-CAL-001 未消），又**丢失既有合法占用**（后果更重）。
///
/// 本文件的判据 = **有序占用窗集合**（每个资源上 `(Start, End)` 序列**逐字相等**），
///   **不是** Task 指纹、**不是**窗数。这正是裁决要求的「实际有序占用窗集合一致」。
///
/// 全部纯内存，不触库（用户红线：Integration 直连生产库，只跑 Unit）。
/// </summary>
public class OccupancyRollbackIdentityTests
{
    /// <summary>夹具基准日 = 当日 00:00 ⇒ <see cref="At"/> 的小时数即钟点（全部 ≤23，不跨日）。</summary>
    private static readonly DateTime T0 = new(2026, 10, 1, 0, 0, 0);

    private static DateTime At(double hours) => T0.AddHours(hours);

    private static TimeWindow W(double fromH, double toH) => new(At(fromH), At(toH));

    private static Dictionary<int, List<TimeWindow>> Occupancy(params (int Rid, TimeWindow[] Wins)[] init)
    {
        var d = new Dictionary<int, List<TimeWindow>>();
        foreach (var (rid, wins) in init)
        {
            d[rid] = new List<TimeWindow>(wins);
        }
        return d;
    }

    /// <summary>某资源上的**有序占用窗集合**签名（逐字 `[HH:mm-HH:mm]` 串联）；资源不存在 ⇒ `"(缺失)"`。</summary>
    private static string Sig(Dictionary<int, List<TimeWindow>> occ, int rid)
    {
        if (!occ.TryGetValue(rid, out var wins)) return "(缺失)";
        var sb = new StringBuilder();
        foreach (var w in wins)
        {
            sb.Append('[').Append(w.Start.ToString("HH:mm")).Append('-').Append(w.End.ToString("HH:mm")).Append(']');
        }
        return sb.ToString();
    }

    /// <summary>全表签名（资源键升序；空列表显式标 `(空)`，与「键缺失」区分）。</summary>
    private static string SigAll(Dictionary<int, List<TimeWindow>> occ)
    {
        var keys = new List<int>(occ.Keys);
        keys.Sort();
        var sb = new StringBuilder();
        foreach (var rid in keys)
        {
            sb.Append('R').Append(rid).Append('=')
              .Append(occ[rid].Count == 0 ? "(空)" : Sig(occ, rid))
              .Append(' ');
        }
        return sb.ToString().TrimEnd();
    }

    // ══════════════════════════════════════════════════════════════════════════════════
    // ① 核心判别性用例：**中间插入 + OP 下游失败**
    // ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// **F-01 核心**：试排在**同一资源既有两窗之间**插入新窗（OP10 成功）→ 另一资源追加（OP20 成功）
    ///   → **OP30 无槽 ⇒ 下游工序失败**（`ScheduleForward`/`ScheduleBackward` 返回空集合）
    ///   ⇒ 回滚必须把该资源的有序窗集合**逐字还原**。
    ///
    /// 夹具刻意让新窗落在**中间**：
    ///   试排前 R1 = `[08-09][16-17]`；OP10 落在空档 `[12-13]` ⇒ 插入位置 = 下标 1（**中间**）。
    ///
    /// **判别性（静态自证，见 `Assert.NotEqual`）**：
    ///   若沿用旧「按窗数截尾」：R1 截到 2 条 ⇒ 得 `[08-09][12-13]`
    ///   （**幽灵 `12-13` 留下、合法窗 `16-17` 丢失**）≠ 期望 `[08-09][16-17]` ⇒ 本用例在旧实现下**必红**。
    /// </summary>
    [Fact]
    public void F01_中间插入加下游工序失败_回滚后有序占用窗集合逐字还原()
    {
        // 试排前的**真实**占用表（R1 两窗夹出一个中间空档；R2 另有一窗）
        var occ = Occupancy(
            (1, new[] { W(8, 9), W(16, 17) }),
            (2, new[] { W(20, 21) }));
        var baseline = PhaseTwoInitialScheduler.CaptureOccupancyResourceIds(occ);
        var log = new List<(int ResourceId, TimeWindow Window)>();

        // ── 试排写入序列（与生产同源：全部经 `AddOccupancyWindow`，逐次登记插入身份）──
        // OP10：落在 R1 的**中间**空档 ⇒ 有序插入到下标 1
        PhaseTwoInitialScheduler.AddOccupancyWindow(occ, pristine: null, resourceId: 1, window: W(12, 13), insertLog: log);
        // OP20：追加到 R2 末尾
        PhaseTwoInitialScheduler.AddOccupancyWindow(occ, pristine: null, resourceId: 2, window: W(22, 23), insertLog: log);
        // OP30：**下游工序无槽 ⇒ 失败**（生产里此处 `combinedTasks.Count == 0`，触发回滚）

        // 回滚前：中间插入确实发生（有序升序不变式保持）
        Assert.Equal("[08:00-09:00][12:00-13:00][16:00-17:00]", Sig(occ, 1));
        Assert.Equal("[20:00-21:00][22:00-23:00]", Sig(occ, 2));

        var unmatched = PhaseTwoInitialScheduler.RollbackInsertedOccupancyWindows(occ, baseline, log);

        // 回滚必须**逐条命中**（非 0 即代表有路径绕过 insertLog 直写占用表）
        Assert.Equal(0, unmatched);

        // ★ 判据 = **有序占用窗集合逐字还原**（非窗数、非 Task 指纹）
        Assert.Equal("[08:00-09:00][16:00-17:00]", Sig(occ, 1));
        Assert.Equal("[20:00-21:00]", Sig(occ, 2));

        // 判别性自证：旧「按窗数截尾」在本夹具上得到的是下面这个**错的**集合（幽灵留下 + 合法窗丢失）
        Assert.NotEqual("[08:00-09:00][12:00-13:00]", Sig(occ, 1));
        // 且 R1 的窗数在两种实现下**都是 2** ⇒ 只断言窗数**无法**判别本缺陷（故本用例断言集合）
        Assert.Equal(2, occ[1].Count);
    }

    // ══════════════════════════════════════════════════════════════════════════════════
    // ①b **实测对照**：把旧「按数量截尾」算法在同夹具上**真的跑一遍**
    // ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// **实测对照（不是静态推断）**：在同夹具上**真的执行**旧 `RollbackOccupancyToWatermark` 的语义
    ///   （逐资源记「试排前的窗数」，回滚时 `windows.RemoveRange(count, windows.Count - count)`），
    ///   并把它的**实际输出**与现实现对照。
    ///
    /// 实测结论（见断言）：
    ///   · 旧算法输出 `[08:00-09:00][12:00-13:00]` —— **幽灵 `12-13` 留下、合法窗 `16-17` 丢失**；
    ///   · 现实现输出 `[08:00-09:00][16:00-17:00]` —— 逐字还原；
    ///   · 二者**不等** ⇒ 本夹具**具有判别性**（旧实现在 ① 下必红），而非「两种实现都过」的伪反证。
    /// </summary>
    [Fact]
    public void F01_旧按数量截尾算法在同夹具上实测产出幽灵占用与合法窗丢失()
    {
        var occ = Occupancy(
            (1, new[] { W(8, 9), W(16, 17) }),
            (2, new[] { W(20, 21) }));
        var baseline = PhaseTwoInitialScheduler.CaptureOccupancyResourceIds(occ);

        // 旧算法记的「水位」= 试排前各资源的**窗数**
        var watermark = new Dictionary<int, int>();
        foreach (var rid in baseline) watermark[rid] = occ[rid].Count;   // {1:2, 2:1}

        var log = new List<(int ResourceId, TimeWindow Window)>();
        PhaseTwoInitialScheduler.AddOccupancyWindow(occ, null, 1, W(12, 13), log);   // 中间插入
        PhaseTwoInitialScheduler.AddOccupancyWindow(occ, null, 2, W(22, 23), log);

        LegacyRollbackByCount(occ, watermark);   // ← 旧算法：只截尾

        // 旧算法的**实测输出**（错的）：幽灵留下 + 合法窗丢失
        Assert.Equal("[08:00-09:00][12:00-13:00]", Sig(occ, 1));
        Assert.NotEqual("[08:00-09:00][16:00-17:00]", Sig(occ, 1));

        // 现实现的输出（对的）——同一夹具、同一写入序列
        var occ2 = Occupancy((1, new[] { W(8, 9), W(16, 17) }), (2, new[] { W(20, 21) }));
        var baseline2 = PhaseTwoInitialScheduler.CaptureOccupancyResourceIds(occ2);
        var log2 = new List<(int ResourceId, TimeWindow Window)>();
        PhaseTwoInitialScheduler.AddOccupancyWindow(occ2, null, 1, W(12, 13), log2);
        PhaseTwoInitialScheduler.AddOccupancyWindow(occ2, null, 2, W(22, 23), log2);
        PhaseTwoInitialScheduler.RollbackInsertedOccupancyWindows(occ2, baseline2, log2);

        Assert.Equal("[08:00-09:00][16:00-17:00]", Sig(occ2, 1));

        // ★ 判别性：两种算法在同夹具上**输出不同**
        Assert.NotEqual(Sig(occ, 1), Sig(occ2, 1));
    }

    /// <summary>
    /// 旧 `RollbackOccupancyToWatermark` 的**语义复刻**（仅用于对照实验，不参与生产）：
    /// 逐资源按「试排前的窗数」把列表**截尾**到该数量。
    /// </summary>
    private static void LegacyRollbackByCount(
        Dictionary<int, List<TimeWindow>> occ, Dictionary<int, int> watermarkCounts)
    {
        foreach (var (resourceId, count) in watermarkCounts)
        {
            if (!occ.TryGetValue(resourceId, out var windows)) continue;
            if (windows.Count > count)
            {
                windows.RemoveRange(count, windows.Count - count);
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════
    // ② 同一资源多次插入（含先后都在中间）—— 逆序回滚
    // ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 同一资源上**三次**插入（两次中间 + 一次末尾），其中后插的窗排在前插的窗**之前**
    ///   ⇒ 回滚必须**逆序**按值身份移除，逐字还原。
    /// 旧「按窗数截尾」会留下 `[10-11][12-13][18-19]`（全部幽灵）并丢掉 `[16-17]` ⇒ 必红。
    /// </summary>
    [Fact]
    public void F01_同资源多次插入_逆序回滚逐字还原()
    {
        var occ = Occupancy((1, new[] { W(8, 9), W(16, 17) }));
        var baseline = PhaseTwoInitialScheduler.CaptureOccupancyResourceIds(occ);
        var log = new List<(int ResourceId, TimeWindow Window)>();

        PhaseTwoInitialScheduler.AddOccupancyWindow(occ, null, 1, W(12, 13), log);   // 中间
        PhaseTwoInitialScheduler.AddOccupancyWindow(occ, null, 1, W(10, 11), log);   // 中间、且排在 12-13 **之前**
        PhaseTwoInitialScheduler.AddOccupancyWindow(occ, null, 1, W(18, 19), log);   // 末尾

        Assert.Equal("[08:00-09:00][10:00-11:00][12:00-13:00][16:00-17:00][18:00-19:00]", Sig(occ, 1));

        var unmatched = PhaseTwoInitialScheduler.RollbackInsertedOccupancyWindows(occ, baseline, log);

        Assert.Equal(0, unmatched);
        Assert.Equal("[08:00-09:00][16:00-17:00]", Sig(occ, 1));
    }

    // ══════════════════════════════════════════════════════════════════════════════════
    // ③ 未命中**不得静默**（不静默跳过、更不退化为「删别的窗来凑数」）
    // ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 日志里存在表中**不存在**的窗 ⇒ 返回未命中数（&gt;0），**且不误删任何既有窗**。
    /// 这是「有代码路径绕过 `insertLog` 直写占用表」的可观测信号（F-04 同族的可追溯性要求）。
    /// </summary>
    [Fact]
    public void F01_回滚未命中_上抛计数且不误删既有窗()
    {
        var occ = Occupancy((1, new[] { W(8, 9) }));
        var baseline = PhaseTwoInitialScheduler.CaptureOccupancyResourceIds(occ);
        var log = new List<(int ResourceId, TimeWindow Window)>
        {
            (1, W(12, 13)),   // 表中不存在 ⇒ 未命中
            (9, W(14, 15)),   // 资源键也不存在 ⇒ 未命中
        };

        var unmatched = PhaseTwoInitialScheduler.RollbackInsertedOccupancyWindows(occ, baseline, log);

        Assert.Equal(2, unmatched);
        // 未命中**不得**导致误删：既有窗原样保留
        Assert.Equal("[08:00-09:00]", Sig(occ, 1));
        Assert.True(occ.ContainsKey(1));
    }

    // ══════════════════════════════════════════════════════════════════════════════════
    // ④ 资源键清理语义：基线**外**新建键整键移除；基线**内**清空后**保留键**
    // ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ① 试排**新建**的资源键（不在基线键集内）在清空后必须**整键移除**（回到「该资源无占用」初态）；
    /// ② 基线**内**的资源即使被清空也必须**保留键**（调用方随后由 `RestoreStageBatchSnapshot`
    ///    把被移出的原批窗按原值加回 —— 提前删键会让恢复语义分叉）。
    /// </summary>
    [Fact]
    public void F01_基线外资源整键移除_基线内资源清空保留键()
    {
        var occ = Occupancy(
            (1, new[] { W(8, 9) }),      // 基线内，本用例不写入
            (5, new[] { W(10, 11) }),    // 基线内，试排会**清空**它
            (2, new[] { W(20, 21) }));   // 基线内，试排会追加
        var baseline = PhaseTwoInitialScheduler.CaptureOccupancyResourceIds(occ);   // {1, 5, 2}
        var log = new List<(int ResourceId, TimeWindow Window)>();

        PhaseTwoInitialScheduler.AddOccupancyWindow(occ, null, 2, W(22, 23), log);   // 追加
        // 模拟「试排时该资源键才出现」——生产里初始构建（`:800`）已为**全部**资源建键，
        //   故 `AddOccupancyWindow` 只做插入、不建键（`EnsureOwned` 对新键直接返回）。
        occ[3] = new List<TimeWindow>();
        PhaseTwoInitialScheduler.AddOccupancyWindow(occ, null, 3, W(4, 5), log);     // **基线外键 3**
        // 模拟「试排把 R5 上唯一的窗搬走」——直接移除该窗（生产里由 `TryMergeDemandIntoStageBatch`
        //   的「移出目标批」步骤完成），随后按身份回滚应把 R5 留在**空**状态而非删键。
        occ[5].RemoveAt(0);

        var unmatched = PhaseTwoInitialScheduler.RollbackInsertedOccupancyWindows(occ, baseline, log);

        Assert.Equal(0, unmatched);
        Assert.False(occ.ContainsKey(3));                 // 基线外、已清空 ⇒ 整键移除
        Assert.Equal("[20:00-21:00]", Sig(occ, 2));       // 追加的窗被撤销
        Assert.True(occ.ContainsKey(5));                  // 基线内 ⇒ 保留键
        Assert.Empty(occ[5]);                             // 且为空
        Assert.Equal("[08:00-09:00]", Sig(occ, 1));       // 未触及的资源原样
    }

    // ══════════════════════════════════════════════════════════════════════════════════
    // ⑤ 有序不变式：回滚**只做定位移除**，不得破坏 `Start` 升序
    // ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 回滚后全表必须**仍然按 `Start` 升序**（读端 `FindFirstAvailableSlot` / `HasConflict` 的二分前提）。
    /// 逐资源断言相邻窗 `Start` 非降。
    /// </summary>
    [Fact]
    public void F01_回滚后仍保持Start升序不变式()
    {
        var occ = Occupancy((1, new[] { W(8, 9), W(16, 17), W(22, 23) }));
        var baseline = PhaseTwoInitialScheduler.CaptureOccupancyResourceIds(occ);
        var log = new List<(int ResourceId, TimeWindow Window)>();

        PhaseTwoInitialScheduler.AddOccupancyWindow(occ, null, 1, W(20, 21), log);
        PhaseTwoInitialScheduler.AddOccupancyWindow(occ, null, 1, W(12, 13), log);
        PhaseTwoInitialScheduler.AddOccupancyWindow(occ, null, 1, W(10, 11), log);

        Assert.Equal("R1=[08:00-09:00][10:00-11:00][12:00-13:00][16:00-17:00][20:00-21:00][22:00-23:00]",
            SigAll(occ));

        PhaseTwoInitialScheduler.RollbackInsertedOccupancyWindows(occ, baseline, log);

        Assert.Equal("R1=[08:00-09:00][16:00-17:00][22:00-23:00]", SigAll(occ));
        var wins = occ[1];
        for (var i = 1; i < wins.Count; i++)
        {
            Assert.True(wins[i - 1].Start <= wins[i].Start,
                $"有序不变式被破坏：下标 {i - 1}/{i} 的 Start 逆序");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════
    // ⑥ **聚合压实 vs 逐条二分** 对拍（2026-10-26 A-4 第二刀）
    // ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// **判别器**：日志条数 &gt; 32 时走「按资源聚合、一次压实」；≤ 32 时走「逐条二分 + `RemoveAt`」。
    /// 同一夹具喂给**两条算法**（第二条在本文件内逐字复刻旧逐条语义），断言：
    ///   · 全表有序占用窗集合**逐字相同**；
    ///   · `unmatched` **数值相同**（含「值不存在」「键不存在」两类未命中）。
    ///
    /// **为什么要这个用例**：聚合路径（&gt;32）只在**生产**日志量下命中，1,472 个既有 Unit 覆盖不到；
    ///   靠人工读代码担保不够，必须有个能在两条路径间对拍的钉子。
    /// ⚠ 夹具窗铺在 `T0` 起 400 分钟内（同一自然日），签名用 `yyyyMMddHHmm`，
    ///   避免 `Sig` 的 `HH:mm` 在跨日夹具上出现**不同集合同签名**的假绿。
    /// </summary>
    [Fact]
    public void F01_聚合压实与大日志逐条回滚_全表与未命中数逐字一致()
    {
        var agg = BuildLargeFixture();
        var legacy = BuildLargeFixture();

        // 夹具自证：日志 42 条 > 聚合阈值 32 ⇒ 聚合路径真的被触发（不是又跑了一遍小路径）
        Assert.True(agg.Log.Count > 32, $"夹具日志仅 {agg.Log.Count} 条，未达聚合阈值");

        var aggUnmatched = PhaseTwoInitialScheduler.RollbackInsertedOccupancyWindows(agg.Occ, agg.Baseline, agg.Log);
        var legacyUnmatched = LegacyRollbackByEntry(legacy.Occ, legacy.Baseline, legacy.Log);

        // ① 未命中口径一致：1 条「值不存在」+ 1 条「键不存在」
        Assert.Equal(2, aggUnmatched);
        Assert.Equal(legacyUnmatched, aggUnmatched);

        // ② 全表逐字一致（聚合压实不得改动任何一条既有窗，也不得漏删任何一条试排窗）
        Assert.Equal(SigAllMinutes(legacy.Occ), SigAllMinutes(agg.Occ));

        // ③ 回滚后两条列表都回到试排前：R1 = 200 条、R2 = 40 条
        Assert.Equal(200, agg.Occ[1].Count);
        Assert.Equal(40, agg.Occ[2].Count);

        // ④ 有序不变式仍成立（读端二分前提）
        foreach (var rid in new[] { 1, 2 })
        {
            var list = agg.Occ[rid];
            for (var i = 1; i < list.Count; i++)
            {
                Assert.True(list[i - 1].Start < list[i].Start,
                    $"R{rid} 有序不变式被破坏：下标 {i - 1}/{i}");
            }
        }
    }

    /// <summary>R1 200 窗 + R2 40 窗；插入 40 条（R1 30／R2 10）；再加 2 条未命中 ⇒ 日志 42 条。</summary>
    private static (Dictionary<int, List<TimeWindow>> Occ, HashSet<int> Baseline, List<(int, TimeWindow)> Log)
        BuildLargeFixture()
    {
        var occ = new Dictionary<int, List<TimeWindow>>();
        for (var rid = 1; rid <= 2; rid++)
        {
            var count = rid == 1 ? 200 : 40;
            var list = new List<TimeWindow>();
            for (var i = 0; i < count; i++) list.Add(WM(2 * i, 2 * i + 1));
            occ[rid] = list;
        }

        var baseline = PhaseTwoInitialScheduler.CaptureOccupancyResourceIds(occ);
        var log = new List<(int, TimeWindow)>();
        for (var i = 0; i < 30; i++) InsertSorted(occ, 1, WM(2 * i + 1, 2 * i + 2), log);
        for (var i = 0; i < 10; i++) InsertSorted(occ, 2, WM(2 * i + 1, 2 * i + 2), log);

        // 未命中两类：值不存在（键在表里）／键不存在
        log.Add((1, WM(1000, 1001)));
        log.Add((99, WM(0, 1)));
        return (occ, baseline, log);
    }

    private static TimeWindow WM(int fromMinutes, int toMinutes) =>
        new(T0.AddMinutes(fromMinutes), T0.AddMinutes(toMinutes));

    /// <summary>按 `(Start, End)` 有序插入并登记身份（复刻 `AddOccupancyWindow` 的定位+插入，测试专用）。</summary>
    private static void InsertSorted(
        Dictionary<int, List<TimeWindow>> occ, int rid, TimeWindow window, List<(int, TimeWindow)> log)
    {
        if (!occ.TryGetValue(rid, out var list))
        {
            list = new List<TimeWindow>();
            occ[rid] = list;
        }

        var i = 0;
        while (i < list.Count
               && (list[i].Start < window.Start
                   || (list[i].Start == window.Start && list[i].End <= window.End)))
        {
            i++;
        }
        list.Insert(i, window);
        log.Add((rid, window));
    }

    /// <summary>
    /// **旧逐条语义复刻**（仅对拍用，不参与生产）：逆序逐条按值定位 + `RemoveAt`，再清空且非基线的键整键移除。
    /// 这是 2026-10-26 之前 `RollbackInsertedOccupancyWindows` 的实现形状。
    /// </summary>
    private static int LegacyRollbackByEntry(
        Dictionary<int, List<TimeWindow>> occ, HashSet<int> baseline, List<(int, TimeWindow)> log)
    {
        var unmatched = 0;
        for (var i = log.Count - 1; i >= 0; i--)
        {
            var (rid, window) = log[i];
            if (!occ.TryGetValue(rid, out var list))
            {
                unmatched++;
                continue;
            }

            var index = -1;
            for (var j = 0; j < list.Count; j++)
            {
                if (list[j].Start == window.Start && list[j].End == window.End)
                {
                    index = j;
                    break;
                }
            }

            if (index < 0)
            {
                unmatched++;
                continue;
            }

            list.RemoveAt(index);
        }

        foreach (var rid in new List<int>(occ.Keys))
        {
            if (occ[rid].Count == 0 && !baseline.Contains(rid)) occ.Remove(rid);
        }

        return unmatched;
    }

    // ══════════════════════════════════════════════════════════════════════════════════
    // ⑦ **生产形状**对拍：k 小（≤32）× W 大（≥128）⇒ 靠「探测窗表长」进聚合（2026-10-26 A-4 第三刀）
    // ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// **为什么必须补这一条（不是重复 ⑥）**：
    /// ⑥ 的夹具日志 42 条 &gt; 32，走的是「**条数**多」这个判据；而 PV190 现场实测是
    ///   `kMean = 4.3 / kMax = 10`、`wMax = 2,341` —— 条数**远小于** 32，
    ///   聚合路径是靠「被触及资源的窗表 ≥ 128」这个**探测**条件才进得去的。
    ///   ⇒ ⑥ 覆盖的是**另一条**入口，生产真正跑的那条它碰不到。本用例专门造小 k + 大 W。
    ///
    /// 对拍口径与 ⑥ 相同：同一夹具喂给「生产实现」与「本文件内逐字复刻的逐条语义」，
    /// 断言全表有序窗集合**逐字相同**、`unmatched` **数值相同**、有序不变式仍成立。
    ///
    /// ⚠ 本用例只证「结果逐位相同」；「工作量真降了」由 2026-10-26 的现场探针读数证
    ///   （单次 167 µs → 待复测），不能拿结果一致性当性能护栏。
    /// </summary>
    [Fact]
    public void F01_小日志大窗表_探测进聚合_与逐条回滚逐字一致()
    {
        var rng = new Random(20261026);
        var aggregateHits = 0;

        for (var iter = 0; iter < 400; iter++)
        {
            // 至少一个资源窗表 ≥ 128（探测条件成立）；其余资源随机大小
            var resourceCount = rng.Next(1, 4);
            var current = new Dictionary<int, List<TimeWindow>>();
            var log = new List<(int ResourceId, TimeWindow Window)>();
            var inserted = new HashSet<(int Rid, long S, long E)>();

            for (var r = 0; r < resourceCount; r++)
            {
                var rid = r + 1;
                var total = r == 0 ? rng.Next(128, 320) : rng.Next(0, 60);
                var all = new List<TimeWindow>(total);
                var t = 0;
                for (var i = 0; i < total; i++)
                {
                    var len = rng.Next(1, 5);
                    all.Add(WM(t, t + len));
                    t += len + rng.Next(0, 3);          // 间隙可 0 ⇒ 首尾相接，仍非重叠
                }

                current[rid] = new List<TimeWindow>(all);

                // 随机挑一部分当成「本次试排插入的」—— 生产里 k 很小，这里保持多数迭代 k ≤ 32
                var wantK = rng.Next(0, 8);
                for (var j = 0; j < wantK && all.Count > 0; j++)
                {
                    var pick = all[rng.Next(all.Count)];
                    if (!inserted.Add((rid, pick.Start.Ticks, pick.End.Ticks))) continue;
                    log.Add((rid, pick));
                }
            }

            // 加两类未命中：值不存在（键在表里）／资源键不存在 —— 与 ⑥ 同口径
            if (rng.Next(0, 2) == 0) log.Add((1, WM(-100, -99)));
            if (rng.Next(0, 2) == 0) log.Add((999, WM(0, 1)));

            if (log.Count == 0) continue;

            // 自证「本条真的测到了聚合分支」：复刻生产的两条判据（见 `RollbackInsertedOccupancyWindows`）。
            //   ⚠ 只断言结果一致、不断言分支命中，就有可能在**小路径**上跑 400 次而全程没碰到我改的那段。
            var goesAggregate = log.Count > 32;
            if (!goesAggregate && log.Count > 1)
            {
                var probeLimit = log.Count < 4 ? log.Count : 4;
                for (var i = 0; i < probeLimit; i++)
                {
                    if (current.TryGetValue(log[i].ResourceId, out var probe) && probe.Count >= 128)
                    {
                        goesAggregate = true;
                        break;
                    }
                }
            }
            if (goesAggregate) aggregateHits++;

            // 「试排前」的基线表 = 当前表 − 本次插入的窗
            var baselineOcc = new Dictionary<int, List<TimeWindow>>();
            foreach (var (rid, list) in current)
            {
                var kept = new List<TimeWindow>(list.Count);
                foreach (var w in list)
                {
                    if (!inserted.Contains((rid, w.Start.Ticks, w.End.Ticks))) kept.Add(w);
                }
                baselineOcc[rid] = kept;
            }

            var aggOcc = Clone(current);
            var legacyOcc = Clone(current);
            var aggBaseline = PhaseTwoInitialScheduler.CaptureOccupancyResourceIds(baselineOcc);
            var legacyBaseline = PhaseTwoInitialScheduler.CaptureOccupancyResourceIds(baselineOcc);
            var aggLog = new List<(int, TimeWindow)>(log);
            var legacyLog = new List<(int, TimeWindow)>(log);

            var aggUnmatched = PhaseTwoInitialScheduler.RollbackInsertedOccupancyWindows(aggOcc, aggBaseline, aggLog);
            var legacyUnmatched = LegacyRollbackByEntry(legacyOcc, legacyBaseline, legacyLog);

            Assert.Equal(legacyUnmatched, aggUnmatched);
            Assert.Equal(SigAllMinutes(legacyOcc), SigAllMinutes(aggOcc));

            foreach (var (rid, list) in aggOcc)
            {
                for (var i = 1; i < list.Count; i++)
                {
                    Assert.True(list[i - 1].Start < list[i].Start,
                        $"iter={iter} R{rid} 有序不变式被破坏：下标 {i - 1}/{i}");
                }
            }
        }

        Assert.True(aggregateHits >= 100,
            $"只有 {aggregateHits} 次进入聚合分支 —— 夹具没打到本刀改的那段，本用例等于没测");
    }

    private static Dictionary<int, List<TimeWindow>> Clone(Dictionary<int, List<TimeWindow>> src)
    {
        var d = new Dictionary<int, List<TimeWindow>>(src.Count);
        foreach (var (rid, list) in src) d[rid] = new List<TimeWindow>(list);
        return d;
    }

    /// <summary>全表签名（资源键升序、窗按分钟精度逐字串联；空列表标 `(空)`）。</summary>
    private static string SigAllMinutes(Dictionary<int, List<TimeWindow>> occ)
    {
        var keys = new List<int>(occ.Keys);
        keys.Sort();
        var sb = new StringBuilder();
        foreach (var rid in keys)
        {
            sb.Append('R').Append(rid).Append('=');
            var list = occ[rid];
            if (list.Count == 0)
            {
                sb.Append("(空)");
            }
            else
            {
                foreach (var w in list)
                {
                    sb.Append('[').Append(w.Start.ToString("yyyyMMddHHmm"))
                      .Append('-').Append(w.End.ToString("yyyyMMddHHmm")).Append(']');
                }
            }

            sb.Append(' ');
        }

        return sb.ToString().TrimEnd();
    }
}
