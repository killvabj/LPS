using System.Threading.Tasks;
using LPS.APS.Engine.Data;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// 集成测试「自己造出来的 Run 壳」回收器（O36，2026-11-07）。
/// </summary>
/// <remarks>
/// 【缺陷】两个集成测试都自己 `INSERT ScheduleRun(Status='RUNNING')` + `INSERT PlanVersion(Status='BUILDING')`，
///   但收尾只删业务行（`[Task]` / `PeggingSupplyAllocation` / `[Order]` …），**从不删壳** ⇒
///   每跑一次测试就永久留一个 `RUNNING` 的 Run 和一张 `BUILDING` 的 PV。
///   后果不是"看着脏"：任何「现在有几个 Run 在跑」「这张 PV 是不是还在算」的读数都会被残壳污染。
/// 【口径】只删**调用方传进来的、测试自己创建**的 Id —— **绝不按状态/时间批量删**。
///   生产库上 `RUNNING` 里同时躺着真实夜间 Run（如 999401 自 2026-11-05 起），按状态删会误伤。
/// 【为什么要显式列依赖表】全库 47 个外键里**没有任何一个**指向 `PlanVersion`，
///   唯一涉及 Run 的是 `ScheduleExplanationFact.ScheduleRunId → ScheduleRun` ⇒
///   数据库不会替我们挡，依赖行必须逐表清，否则删完 PV 留下一地孤儿。
/// 【顺序】先子后父。无 FK 强制，顺序只为「中途失败也不留半截引用」。
/// </remarks>
internal static class TestRunArtifactCleaner
{
    /// <summary>(表名, 版本 Id 列名)。覆盖全部以 PlanVersionId / SourcePlanVersionId 为分区键的表。</summary>
    private static readonly (string Table, string Column)[] PlanVersionOwnedTables =
    [
        ("TaskDispatch",                         "SourcePlanVersionId"),
        ("ExplainTrace",                         "PlanVersionId"),
        ("AllocationTaskShare",                  "PlanVersionId"),
        ("PeggingSupplyAllocation",              "PlanVersionId"),
        ("Pegging",                              "PlanVersionId"),
        ("ScheduleExplanationFact",              "PlanVersionId"),
        ("FrozenZoneSnapshot",                   "PlanVersionId"),
        ("ProductionInstructionPositionSnapshot", "PlanVersionId"),
        ("OrderScheduleSummary",                 "PlanVersionId"),
        ("OrderLoadRejection",                   "PlanVersionId"),
        ("OrderBomRequestLink",                  "PlanVersionId"),
        ("[Order]",                              "PlanVersionId"),
        ("ComputeStageProgress",                 "PlanVersionId"),
        ("DemandSupplyHardLock",                 "SourcePlanVersionId"),
        ("[Task]",                               "PlanVersionId"),
    ];

    /// <summary>
    /// 按 PlanVersionId 清全部依赖行，再删 `PlanVersion` 本身。返回删除的 PV 行数（正常 0 或 1）。
    /// </summary>
    public static async Task<int> PurgePlanVersionAsync(DatabaseConnectionManager cm, int planVersionId)
    {
        if (planVersionId <= 0)
        {
            return 0;
        }

        foreach (var (table, column) in PlanVersionOwnedTables)
        {
            await cm.ExecuteAsync(
                $"DELETE FROM {table} WHERE {column} = @PlanVersionId",
                new { PlanVersionId = planVersionId },
                db: DatabaseId.APS);
        }

        return await cm.ExecuteAsync(
            "DELETE FROM PlanVersion WHERE Id = @PlanVersionId",
            new { PlanVersionId = planVersionId },
            db: DatabaseId.APS);
    }

    /// <summary>
    /// 清该 Run 的 `ScheduleExplanationFact`（唯一有外键的依赖），再删 `ScheduleRun` 本身。
    /// 返回删除的 Run 行数（正常 0 或 1）。
    /// </summary>
    public static async Task<int> PurgeScheduleRunAsync(DatabaseConnectionManager cm, long scheduleRunId)
    {
        if (scheduleRunId <= 0)
        {
            return 0;
        }

        await cm.ExecuteAsync(
            "DELETE FROM ScheduleExplanationFact WHERE ScheduleRunId = @Run",
            new { Run = scheduleRunId },
            db: DatabaseId.APS);

        return await cm.ExecuteAsync(
            "DELETE FROM ScheduleRun WHERE Id = @Run",
            new { Run = scheduleRunId },
            db: DatabaseId.APS);
    }
}
