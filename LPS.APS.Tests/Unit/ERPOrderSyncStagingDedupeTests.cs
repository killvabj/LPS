using FluentAssertions;
using LPS.APS.Engine.Configuration;
using LPS.APS.Engine.Data;
using LPS.APS.Engine.Services.Sync;
using LPS.APS.Engine.Services.Sync.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Data;
using Xunit;

namespace LPS.APS.Tests.Unit;

/// <summary>
/// ERP_Order_Staging 写入后收敛 —— 守卫「同单号不再堆积副本」且「不碰提升集合」。
///
/// 背景（2026-11-02 实测）：602,826 行 FAILED 只对应 130,953 个单号，平均一单 4.6 份。
/// 根因是收敛谓词写死 <c>SyncStatus='PENDING'</c>，行一旦被判 FAILED 就再也进不了收敛，
/// 每次全量同步又完整留一份 ⇒ 表随轮次线性膨胀。
///
/// 本组用例钉三条，缺一条都算实现跑偏：
/// ① 收敛集合同时覆盖 PENDING 与 FAILED（两处 IN 子句，漏一处处无效）；
/// ② 收敛仍以「同单号存在更新的一行」为前提 —— 只删被覆盖的旧副本，不做无差别全删；
/// ③ 写入抛异常时一条 DELETE 都不发（旧行原样保留，不丢事实）。
///
/// 与「不影响提升到 Order_Canonical」的关系：sp_ValidateAndPromoteOrders 的 PHASE 0 与 1g
/// 全部写死 <c>SyncStatus='PENDING'</c>，删除 FAILED 行与该 SP 的读写集合无交集。
/// 本条不变式在下面的 <see cref="收敛集覆盖PENDING与FAILED_但只删被更新的旧副本"/> 里以
/// 「谓词必须带 newer 相关系」的形式固化。
/// </summary>
public class ERPOrderSyncStagingDedupeTests
{
    /// <summary>把 protected 的 <see cref="ERPOrderSyncService.BulkWriteToStagingAsync"/> 暴露给测试。</summary>
    private sealed class ExposedSyncService : ERPOrderSyncService
    {
        public ExposedSyncService(DatabaseConnectionManager connectionManager)
            : base(connectionManager,
                   new Mock<IOrderPromotionService>().Object,
                   new Mock<IOrderLoadingService>().Object,
                   NullLogger<ERPOrderSyncService>.Instance)
        {
        }

        public Task RunBulkWriteAsync(List<ERPOrderDto> orders) => BulkWriteToStagingAsync(orders);
    }

    private static DatabaseOptions NewOptions() => new()
    {
        // 构造需要 IsValid() 通过；本用例不打开任何连接
        APS  = new DatabaseConnectionOptions { ConnectionString = "Server=(local);Database=APS" },
        ODS  = new DatabaseConnectionOptions { ConnectionString = "Server=(local);Database=ODS" },
        Auth = new DatabaseConnectionOptions { ConnectionString = "Server=(local);Database=Auth" }
    };

    private static List<ERPOrderDto> OneOrder() => new()
    {
        new ERPOrderDto
        {
        SourceOrderId = "O-1001",
        OrderNo       = "O1001",
        OrderType     = "MTS",
        MaterialCode  = "MAT-1",
        Quantity      = 10m,
        UOM           = "PCS",
        DueDate       = new DateTime(2026, 11, 10),
        CreatedAt     = new DateTime(2026, 11, 1, 8, 0, 0),
        UpdatedAt     = new DateTime(2026, 11, 2, 8, 0, 0)
        }
    };

    /// <summary>
    /// 造一个连接管理器 Mock：BulkInsert 直接成功，ExecuteAsync 捕获 SQL 并返回 0。
    /// </summary>
    private static (Mock<DatabaseConnectionManager> Mock, List<string> ExecutedSql) NewConnectionMock()
    {
        var mock = new Mock<DatabaseConnectionManager>(Options.Create(NewOptions()));

        mock.Setup(m => m.BulkInsertAsync(
                It.IsAny<DataTable>(), It.IsAny<string>(), It.IsAny<DatabaseId>()))
            .Returns(Task.CompletedTask);

        var sqls = new List<string>();
        mock.Setup(m => m.ExecuteAsync(
                It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CommandType>(),
                It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .Callback<string, object?, CommandType, DatabaseId, int?>(
                (sql, _, _, _, _) => sqls.Add(sql))
            .ReturnsAsync(0);

        return (mock, sqls);
    }

    [Fact]
    public async Task 收敛集覆盖PENDING与FAILED与PROCESSED_但只删被更新的旧副本()
    {
        var (mock, sqls) = NewConnectionMock();
        var sut = new ExposedSyncService(mock.Object);

        await sut.RunBulkWriteAsync(OneOrder());

        var dedupeSql = sqls.Should().ContainSingle(
            "一次批量写入只发一条收敛语句").Subject;

        // ① 三态都要覆盖：stale 侧与被关联的 newer 侧同样处理
        //   · FAILED：若不收敛，每次全量同步都在原处再留一份 FAILED 副本（原缺陷，2026-11-02 实测 602,826 行/130,953 单号）。
        //   · PROCESSED：若不收敛，同一单号每被重新同步一次就多一行流水行——**此前完全没有兜底**
        //     （唯一会删 PROCESSED 的是全量同步前的「删 7 天以上」清理，生产 Cron 关闭时长期失效），
        //     2026-11-02 实测膨胀到 653,262 行 / 259,586 单号（2.5 倍）。见 r14011。
        dedupeSql.Should().Contain("stale.SyncStatus IN ('PENDING', 'FAILED', 'PROCESSED')",
            "被删侧必须含 FAILED 与 PROCESSED，否则同单号旧副本永远收敛不掉");
        dedupeSql.Should().Contain("newer.SyncStatus IN ('PENDING', 'FAILED', 'PROCESSED')",
            "比较侧同样要含 FAILED 与 PROCESSED，否则「新行覆盖旧行」这条路径收敛不到");

        // ② 必须仍以「存在更新的同单号行」为前提 —— 不做无差别全删
        dedupeSql.Should().Contain("EXISTS",
            "只删被覆盖的旧副本");
        dedupeSql.Should().Contain("newer.OrderNo    = stale.OrderNo",
            "相关系必须落在 OrderNo 上，范围仅限本单号，不波及其它订单");
        dedupeSql.Should().NotContain("NOT EXISTS",
            "出现 NOT EXISTS 说明谓词被改写成了别的语义");

        // ③ 收敛不得触碰 VALIDATED（SP 内的瞬态；落到表上时已转 PROCESSED/FAILED）
        dedupeSql.Should().NotContain("VALIDATED");
    }

    [Fact]
    public async Task 批量写入抛异常_一条DELETE都不发()
    {
        var (mock, sqls) = NewConnectionMock();
        mock.Setup(m => m.BulkInsertAsync(
                It.IsAny<DataTable>(), It.IsAny<string>(), It.IsAny<DatabaseId>()))
            .ThrowsAsync(new InvalidOperationException("模拟：SqlBulkCopy 失败"));

        var sut = new ExposedSyncService(mock.Object);

        await sut.Invoking(s => s.RunBulkWriteAsync(OneOrder()))
            .Should().ThrowAsync<InvalidOperationException>();

        sqls.Should().BeEmpty(
            "写入失败时上一轮的行必须原样保留，收敛不得抢跑（否则清零而新行未就绪）");
    }

    [Fact]
    public async Task 写入在收敛之前发生()
    {
        var mock = new Mock<DatabaseConnectionManager>(Options.Create(NewOptions()));
        var calls = new List<string>();

        mock.Setup(m => m.BulkInsertAsync(
                It.IsAny<DataTable>(), It.IsAny<string>(), It.IsAny<DatabaseId>()))
            .Callback(() => calls.Add("insert"))
            .Returns(Task.CompletedTask);
        mock.Setup(m => m.ExecuteAsync(
                It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CommandType>(),
                It.IsAny<DatabaseId>(), It.IsAny<int?>()))
            .Callback(() => calls.Add("dedupe"))
            .ReturnsAsync(0);

        var sut = new ExposedSyncService(mock.Object);

        await sut.RunBulkWriteAsync(OneOrder());

        calls.Should().Equal(new[] { "insert", "dedupe" },
            "先写后收敛：任何时刻最新一条都在，否则中间态会出现「旧删了新的还没进」");
    }
}
