using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using LPS.APS.Engine.Data;
using LPS.APS.Engine.Extensions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LPS.APS.Tests.Integration;

/// <summary>
/// 批量落库助手（2号位，2026-10-29）：「先批量写、再拿回自增 Id」这条路的**机制级**验证。
/// </summary>
/// <remarks>
/// 【为什么单测覆盖不到】`BulkInsertToTransactionAsync` / `BulkInsertWithIdentityAsync` 的正确性
///   全靠「属性名→列名映射」「分块后 `__i` 用绝对下标」「`MERGE … OUTPUT INSERTED.Id, src.__i` 回传配对」
///   这三件运行时行为；编译通过、纯内存单测通过都证明不了它们。
/// 【本测试不碰任何生产行】目标表是**本连接内的 `#temp`**（`SELECT TOP 0 * INTO #T FROM [Task]`），
///   全程在显式事务里，结束一律 `Rollback`。生产 `[Task]` 只被读结构，不被读写一行数据。
/// 【测不到的】真实 `[Task]` 上的索引维护开销与 121 万行规模 —— 那只能靠真跑一轮 Run 验。
/// </remarks>
public class BulkInsertHelperIntegrationTests
{
    private readonly DatabaseConnectionManager _connectionManager;
    private readonly string _connectionString;

    public BulkInsertHelperIntegrationTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.Test.json", optional: false)
            .AddJsonFile("appsettings.Test.Local.json", optional: true)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddDatabaseServices(configuration);
        services.AddLogging();

        _connectionManager = services.BuildServiceProvider().GetRequiredService<DatabaseConnectionManager>();
        _connectionString = configuration["Database:APS:ConnectionString"]
            ?? throw new InvalidOperationException("缺少 Database:APS:ConnectionString 配置");
    }

    /// <summary>
    /// `[Task]` 的**全部 13 个非空列**（除自增 `Id`；2026-10-29 由 `sys.columns` 实测）。
    /// 为什么必须是全部 13 个而不是「9 个无默认值的」：暂存表由 `SELECT TOP 0 * INTO` 建，
    /// **SELECT INTO 不复制 DEFAULT 约束** ⇒ 生产表里靠默认值兜住的 `IsLocked`／`IsCriticalPath`／
    /// `CreatedAt`／`UpdatedAt` 在暂存表里都变成了「不可留空」。这条正是本测试第一版跑红的原因。
    /// 其余可空列不给，正好验证「未映射列落 NULL 不吃错」。
    /// </summary>
    private sealed class TaskRow
    {
        public int PlanVersionId { get; set; }
        public string TaskNo { get; set; } = string.Empty;
        public int MaterialId { get; set; }
        public int OperationSeq { get; set; }
        public string OperationCode { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public string UOM { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public bool IsLocked { get; set; }
        public bool IsCriticalPath { get; set; }
        public string TaskType { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>Dapper 返回类型：`(Id, TaskNo)` 两个标量（用类而不是 ValueTuple，避免依赖映射实现）。</summary>
    private sealed class IdTaskNo
    {
        public long Id { get; set; }
        public string TaskNo { get; set; } = string.Empty;
    }

    [Fact(DisplayName = "批量助手：BulkInsertWithIdentityAsync 的 Id 回传必须与源行一一对应（跨分块）")]
    public async Task BulkInsertWithIdentityAsync_MapsIdsToSourceRows_AcrossChunks()
    {
        // 1234 行 / 每块 500 ⇒ 3 块。**刻意让行数不是块大小的整数倍**：
        // 分块后 `__i` 若错用块内下标，第 2、3 块的行会互相覆盖，本断言必红。
        const int totalRows = 1234;
        const int chunkRows = 500;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        try
        {
            // 目标表＝临时表：结构逐列同源，但一行真实数据都不会被写
            await connection.ExecuteAsync("SELECT TOP 0 * INTO #T FROM [Task];", transaction: transaction);

            var rows = Enumerable.Range(0, totalRows)
                .Select(i => new TaskRow
                {
                    PlanVersionId = 999_328,
                    TaskNo        = $"TEST-BULK-{i:D5}",   // 唯一标记，用来反查 Id 配对
                    MaterialId    = 850_938,
                    OperationSeq  = i + 1,
                    OperationCode = "B101",
                    Quantity      = i,
                    UOM           = "EA",
                    Status        = "PLANNED",
                    IsLocked      = false,
                    IsCriticalPath = false,
                    TaskType      = "NORMAL",
                    CreatedAt     = new DateTime(2026, 10, 29),
                    UpdatedAt     = new DateTime(2026, 10, 29)
                })
                .ToList();

            var ids = await _connectionManager.BulkInsertWithIdentityAsync(
                rows, "#T", connection, transaction, chunkRows: chunkRows);

            Assert.Equal(totalRows, ids.Length);
            Assert.Equal(totalRows, ids.Distinct().Count());          // 没有重复 Id
            Assert.DoesNotContain(0L, ids);                            // 每行都真拿到了 Id

            // 落库行数必须等于源行数（分块不许丢行／不许重复插）
            var actualCount = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM #T;", transaction: transaction);
            Assert.Equal(totalRows, actualCount);

            // 逐行核对配对：ids[i] 指向的必须是第 i 行那条（按 TaskNo 标记反查）
            var idByTaskNo = (await connection.QueryAsync<IdTaskNo>(
                    "SELECT Id, TaskNo FROM #T;", transaction: transaction))
                .ToDictionary(r => r.TaskNo, r => r.Id, StringComparer.Ordinal);

            Assert.Equal(totalRows, idByTaskNo.Count);
            for (var i = 0; i < totalRows; i++)
            {
                Assert.Equal(idByTaskNo[$"TEST-BULK-{i:D5}"], ids[i]);
            }
        }
        finally
        {
            await transaction.RollbackAsync();   // 临时表随回滚消失，生产表全程未被读写
        }
    }

    /// <summary>
    /// 行对象只带 `TaskNo`——`[Task]` 的 13 个非空列里其余 12 个都没给。
    /// 专门用来证明「缺 NOT NULL 列预检」**真的会触发**：预检若写成永远返回空（比如对象名拼错），
    /// 这里就会退化成 tempdb 的原始报错或干脆灌进去，断言随即失败。
    /// </summary>
    private sealed class SparseTaskRow
    {
        public string TaskNo { get; set; } = string.Empty;
    }

    [Fact(DisplayName = "批量助手：行对象缺 NOT NULL 列时必须被预检点名拦下（不能靠 tempdb 报错）")]
    public async Task BulkInsertWithIdentityAsync_RejectsRowsMissingNotNullColumns()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        try
        {
            await connection.ExecuteAsync("SELECT TOP 0 * INTO #T FROM [Task];", transaction: transaction);

            var rows = new List<SparseTaskRow> { new() { TaskNo = "TEST-SPARSE-0" } };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _connectionManager.BulkInsertWithIdentityAsync(rows, "#T", connection, transaction));

            // 必须点名缺了哪几列，而不是只说「插入失败」
            Assert.Contains("NOT NULL", ex.Message, StringComparison.Ordinal);
            Assert.Contains("IsLocked", ex.Message, StringComparison.Ordinal);
            Assert.Contains("CreatedAt", ex.Message, StringComparison.Ordinal);
            // 已提供的列不许被误报
            Assert.DoesNotContain("TaskNo", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    [Fact(DisplayName = "批量助手：BulkInsertToTransactionAsync 与逐条写入结果必须逐行一致")]
    public async Task BulkInsertToTransactionAsync_WritesSameRowsAsPerRowInsert()
    {
        const int totalRows = 777;   // 同样不是块大小的整数倍
        const int chunkRows = 200;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        try
        {
            await connection.ExecuteAsync("SELECT TOP 0 * INTO #A FROM [Task];", transaction: transaction);
            await connection.ExecuteAsync("SELECT TOP 0 * INTO #B FROM [Task];", transaction: transaction);

            var rows = Enumerable.Range(0, totalRows)
                .Select(i => new TaskRow
                {
                    PlanVersionId = 999_328,
                    TaskNo        = $"TEST-CMP-{i:D5}",
                    MaterialId    = 850_938,
                    OperationSeq  = i + 1,
                    OperationCode = "B101",
                    Quantity      = i,
                    UOM           = "EA",
                    Status        = "PLANNED",
                    IsLocked      = false,
                    IsCriticalPath = false,
                    TaskType      = "NORMAL",
                    CreatedAt     = new DateTime(2026, 10, 29),
                    UpdatedAt     = new DateTime(2026, 10, 29)
                })
                .ToList();

            await _connectionManager.BulkInsertToTransactionAsync(
                rows, "#A", connection, transaction, chunkRows: chunkRows);

            foreach (var row in rows)
            {
                await connection.ExecuteAsync(
                    @"INSERT INTO #B (PlanVersionId, TaskNo, MaterialId, OperationSeq, OperationCode, Quantity, UOM, Status, IsLocked, IsCriticalPath, TaskType, CreatedAt, UpdatedAt)
                      VALUES (@PlanVersionId, @TaskNo, @MaterialId, @OperationSeq, @OperationCode, @Quantity, @UOM, @Status, @IsLocked, @IsCriticalPath, @TaskType, @CreatedAt, @UpdatedAt);",
                    row, transaction: transaction);
            }

            // 逐行比对（按 TaskNo 对齐），不只看行数
            var bulk = (await connection.QueryAsync<TaskRow>(
                    @"SELECT PlanVersionId, TaskNo, MaterialId, OperationSeq, OperationCode, Quantity, UOM, Status, TaskType
                      FROM #A ORDER BY TaskNo;", transaction: transaction)).ToList();
            var single = (await connection.QueryAsync<TaskRow>(
                    @"SELECT PlanVersionId, TaskNo, MaterialId, OperationSeq, OperationCode, Quantity, UOM, Status, TaskType
                      FROM #B ORDER BY TaskNo;", transaction: transaction)).ToList();

            Assert.Equal(totalRows, bulk.Count);
            Assert.Equal(single.Count, bulk.Count);
            for (var i = 0; i < bulk.Count; i++)
            {
                Assert.Equal(single[i].TaskNo, bulk[i].TaskNo);
                Assert.Equal(single[i].PlanVersionId, bulk[i].PlanVersionId);
                Assert.Equal(single[i].MaterialId, bulk[i].MaterialId);
                Assert.Equal(single[i].OperationSeq, bulk[i].OperationSeq);
                Assert.Equal(single[i].OperationCode, bulk[i].OperationCode);
                Assert.Equal(single[i].Quantity, bulk[i].Quantity);
                Assert.Equal(single[i].UOM, bulk[i].UOM);
                Assert.Equal(single[i].Status, bulk[i].Status);
                Assert.Equal(single[i].TaskType, bulk[i].TaskType);
            }
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }
}
