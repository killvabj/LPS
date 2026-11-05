using Hangfire;
using Hangfire.SqlServer;
using LPS.APS.Core.Interfaces;
using LPS.APS.Engine.Services.Sync;

namespace LPS.APS.Web.Extensions;

/// <summary>
/// Hangfire定时服务配置扩展
/// </summary>
public static class HangfireServiceExtensions
{
    /// <summary>
    /// 注册Hangfire服务（使用独立 APS_Hangfire 库存储Job数据）
    /// </summary>
    public static IServiceCollection AddHangfireServices(this IServiceCollection services, IConfiguration configuration)
    {
        var hangfireConnectionString = configuration["Database:Hangfire:ConnectionString"];
        
        if (string.IsNullOrWhiteSpace(hangfireConnectionString))
        {
            throw new InvalidOperationException("Hangfire需要独立数据库连接字符串（Database:Hangfire:ConnectionString）");
        }

        services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseSqlServerStorage(hangfireConnectionString, new SqlServerStorageOptions
            {
                CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
                SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
                QueuePollInterval = TimeSpan.Zero,
                UseRecommendedIsolationLevel = true,
                DisableGlobalLocks = true,
                SchemaName = "HangfireSchema"
            }));

        services.AddHangfireServer(options =>
        {
            // WorkerCount = 同时执行的最大任务数。
            // 原写 Environment.ProcessorCount * 2：部署机 96 逻辑核 ⇒ 192（Hangfire 自己在
            // HangfireSchema.Server.Data 里记的就是 192）。但本系统只有 17 个错开的周期任务、
            // 全仓无 BackgroundJob.Enqueue，这个上限永远用不到。
            // 而挂起的 worker 与 HTTP 请求共用同一个 .NET 线程池，且每个并发任务各占一条 SQL 连接
            //（APS 库 MaxPoolSize = 100）—— 是线程池与连接池的双重隐患。
            // 改为读配置，默认 16；Clamp 作保险，配置写错也不会失控。
            var workerCount = configuration.GetValue<int?>("Hangfire:WorkerCount") ?? 16;
            options.WorkerCount = Math.Clamp(workerCount, 1, Math.Max(1, Environment.ProcessorCount * 2));
            options.Queues = new[] { "critical", "default", "low" };
            options.ServerName = $"APS-{Environment.MachineName}";
        });

        // 全局禁用自动重试（失败即终止，不干扰调试）
        // 生产环境如需重试，可在具体 Job 方法上标注 [AutomaticRetry(Attempts = 3)]
        GlobalJobFilters.Filters.Add(new AutomaticRetryAttribute { Attempts = 0 });

        return services;
    }

    // 永不触发的 cron（2月31日不存在），开发环境使用
    // 任务仍注册在 Dashboard 的 Recurring Jobs 列表中，可点 "Trigger Now" 手动执行
    private const string NeverFireCron = "0 0 31 2 *";

    /// <summary>
    /// 注册所有Hangfire定时任务（在 app.Build() 之后调用）
    /// 开关：<c>Hangfire:CronEnabled</c>
    ///   · 未配置 ⇒ 沿用环境判据（开发＝只注册不触发、生产＝真定时），行为与改造前一致
    ///   · false  ⇒ 注册但一律写永不触发的cron，只能从 Dashboard 点 Trigger Now 手动跑
    ///   · true   ⇒ 写真实cron，到点自动执行
    /// 为什么要有这个开关：开发阶段后端虽然部署到生产机，但夜间流水线还不该自己跑起来。
    /// 注意 <c>APS_Hangfire</c> 库是**多实例共享**的，cron 值存在库里（不是各实例各一份）——
    /// 「哪个实例写了什么」= 最后启动的那个说的算，而到点触发则是**任何连着该库的实例都会做**。
    /// ⇒ 该开关只能保证「写进去的是永不触发」，并不能把另一台实例挡在外面；
    ///    开发机的 Hangfire 实例必须一并关掉，否则两台会互相覆盖、并可能出现双跑。
    /// </summary>
    public static WebApplication UseHangfireJobs(this WebApplication app)
    {
        var isDev = app.Environment.IsDevelopment();

        // null ⇒ 未配置，回落到原环境判据；显式 false 才禁
        var cronEnabled = app.Configuration.GetValue<bool?>("Hangfire:CronEnabled") ?? !isDev;
        var mute = !cronEnabled;

        // ERP订单增量同步（生产：每小时整点）
        RecurringJob.AddOrUpdate<IERPOrderSyncService>(
            "erp-order-incremental-sync",
            service => service.IncrementalSyncAsync(CancellationToken.None),
            mute ? NeverFireCron :"0 * * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });

        // BOM展开请求推送（生产：每日00:00）
        // 从 Order_Canonical 划定活跃根集合，推送 BOMNO 到 ODS 库
        // 后续：由下方 bom-batch-expand（00:05）调用 sp_ExpandBOMBatch_vNext 展开
        RecurringJob.AddOrUpdate<IBOMRequestService>(
            "bom-request-push",
            service => service.PushBOMRequestToODSAsync(CancellationToken.None),
            mute ? NeverFireCron :"0 0 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });

        // BOM批展开（生产：每日00:05）—— 调用 ODS 的 sp_ExpandBOMBatch_vNext，
        // 展开**本次夜间**的推送批次（批号取自 APS_ETL_Log 的 BomRequestPush SUCCESS 记录，
        // 恰好一条才算本次；0 条或多条一律 SKIPPED，不再扫描「最新 PENDING」猜选），供夜间链接货
        // ⚠️ 排在 bom-request-push（00:00）之后、nightly-batch（00:30）之前 —— 接货依赖本步产出的 READY
        // ⚠️ 调用具破坏性：该 SP 先清空四表再重建（2026-10-17 5号位 已将四条 TRUNCATE 移入 BEGIN TRAN）；
        //    我方在调用前另查明细行数，0 条则拒绝调用（否则会清空四表）。失败转 Issue，不硬断。
        // ⚠️ 该 SP 属 5号位 辖区，调用契约已于 2026-10-17 经其书面确认
        RecurringJob.AddOrUpdate<IBomBatchExpandService>(
            "bom-batch-expand",
            service => service.ExpandNightBatchAsync(CancellationToken.None),
            mute ? NeverFireCron :"5 0 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });

        // 主数据同步-ERP（生产：每日00:10）— §2.4.3
        // sp_SyncMasterData('ERP') → Material + MaterialMapping + MaterialSupplyContext
        RecurringJob.AddOrUpdate<IMasterDataSyncService>(
            "master-data-sync-erp",
            service => service.SyncERPMasterDataAsync(CancellationToken.None),
            mute ? NeverFireCron :"10 0 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });

        // 主数据同步-MES（生产：每日00:20）— §2.4.3
        // sp_SyncMasterData('MES') → Material + MaterialMapping + MaterialSupplyContext
        RecurringJob.AddOrUpdate<IMasterDataSyncService>(
            "master-data-sync-mes",
            service => service.SyncMESMasterDataAsync(CancellationToken.None),
            mute ? NeverFireCron :"20 0 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });

        // 资源主数据同步（生产：每日00:15）— v5.0.16 新增
        // ⚠️ 排在 master-data-sync-erp（00:10）之后（Factory 字典已就绪）
        // ⚠️ 排在 routing-sync（00:25）之前（Resource 表为 OperationResourceEligibility 的前置依赖）
        // sp_SyncResourceData('MES') → Resource（双字典映射：FactoryCode + ProductionDeptCode）
        RecurringJob.AddOrUpdate<IResourceSyncService>(
            "resource-sync",
            service => service.SyncAsync(CancellationToken.None),
            mute ? NeverFireCron :"15 0 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });

        // 工艺路线同步（生产：每日00:25）— 核心流程文档 §阶段1 00:15
        // ⚠️ 排在 master-data-sync-mes（00:20）之后，确保 MaterialMapping(MES) 已就绪供 MES_ID→MaterialId 映射
        // sp_SyncRoutingData → RoutingOperation + RoutingDependency + RoutingStage + OperationResourceEligibility
        RecurringJob.AddOrUpdate<IRoutingSyncService>(
            "routing-sync",
            service => service.SyncAllAsync(CancellationToken.None),
            mute ? NeverFireCron :"25 0 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });

        // 库存同步（生产：每日00:26）— 五层库存架构 L1→L4 全链路
        // ⚠️ 排在 master-data-sync-mes（00:20）之后，确保 MaterialMapping 就绪供 SourceID→MaterialCode 反查
        // ⚠️ **必须早于 nightly-batch（00:30）**：Run 的 DataCutoffTime 由 nightly-batch 用 GETDATE() 锁死，
        //   早于 cutoff 采集的库存/XC 才是「本 Run 截止时点之前的事实」；排在其后会让 Run 声明一个
        //   自己并未遵守的截止时点（旧顺序 00:35/00:37 就是这样，靠闸门宽容才没炸）。
        // sp_SyncInventory → InventoryFact_ERP/MES → sp_RefreshInventoryBalance → InventoryBalance
        RecurringJob.AddOrUpdate<IInventorySyncService>(
            "inventory-sync",
            service => service.SyncAsync(CancellationToken.None),
            mute ? NeverFireCron :"26 0 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });

        // PI 级 XC 快照同步（生产：每日00:28）— 冻结 `11 v2.5 §174/§175`、`08 §8a`
        // sp_SyncPiXcSnapshot：ODS PI 号别 XC 余额 → APS 本地 InventoryFact_PiXc（单批 TRUNCATE+INSERT）
        // ⚠️ 目的：Run 期读本批快照，**不再直连** `[mes]` 链接服务器（跨机超时/断链会拖垮整轮）
        // ⚠️ 失败不清表：保留上一批，由 Pegging 侧新鲜度闸门显式拦截，不把缺源当 XC=0
        // ⚠️ **必须早于 nightly-batch（00:30）**，理由同上：闸门按 `DataCutoffTime − 24h` 判新鲜度，
        //   快照若采在 cutoff 之后（旧值 00:37），闸门等于在用 7 分钟的时间差给「漏跑一晚」兜底 ——
        //   那不是设计，是巧合，且会让 Run 声明 00:30 截止却含 00:37 的切片。
        //   采在 cutoff 之前后，口径变成「本 Run 的 XC 一定是 cutoff 之前采的」，漏跑即拒（Fail Closed）。
        //   瞬时失败由服务内建的 3 次重试吸收，不靠闸门宽容。
        RecurringJob.AddOrUpdate<IPiXcSnapshotSyncService>(
            "pi-xc-snapshot-sync",
            service => service.SyncAsync(CancellationToken.None),
            mute ? NeverFireCron :"28 0 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });

        // 夜间批次管道（生产：每日00:30）
        // 步骤：全量订单同步 → 创建ScheduleRun（锁定DataCutoffTime）→ 创建PlanVersion → 订单装载 → BOM接货
        RecurringJob.AddOrUpdate<INightlyBatchOrchestrator>(
            "nightly-batch",
            service => service.RunAsync(CancellationToken.None),
            mute ? NeverFireCron :"30 0 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });

        // MES快照 Step1：工单级快照（生产：每日00:40）
        // ScheduleRun 已在 00:30 nightly-batch 中创建，此处直接读取
        // sp_SyncMESWorkOrderSnapshot → MESWorkOrderSnapshot
        RecurringJob.AddOrUpdate<IMESSnapshotSyncService>(
            "mes-snapshot-workorder",
            service => service.SyncWorkOrderAsync(CancellationToken.None),
            mute ? NeverFireCron :"40 0 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });

        // MES快照 Step2：工序进度快照（生产：每日00:45）
        // sp_SyncMESOperationProgressSnapshot → OperationProgressSnapshot
        RecurringJob.AddOrUpdate<IMESSnapshotSyncService>(
            "mes-snapshot-operation-progress",
            service => service.SyncOperationProgressAsync(CancellationToken.None),
            mute ? NeverFireCron :"45 0 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });

        // MES快照 Step3：大工艺进度快照（生产：每日00:50）
        // sp_SyncMESStageProgressSnapshot → StageProgressSnapshot
        // 1号位优先消费此表（粒度粗、性能好）
        RecurringJob.AddOrUpdate<IMESSnapshotSyncService>(
            "mes-snapshot-stage-progress",
            service => service.SyncStageProgressAsync(CancellationToken.None),
            mute ? NeverFireCron :"50 0 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });

        // 管道供给同步（生产：每日00:55）— §6.6 SupplyFact_Pipeline
        // ⚠️ 排在 inventory-sync（00:35）之后，确保 MaterialMapping 已就绪供 MaterialCode 映射
        // ⚠️ 排在 domain-dependency-scan（01:50）和 scheduling（02:00）之前
        // sp_SyncPipelineSupply → SupplyFact_Pipeline（全量装载真实采购在途 ＋ 已到未入库）
        RecurringJob.AddOrUpdate<IPipelineSupplySyncService>(
            "pipeline-supply-sync",
            service => service.SyncAsync(CancellationToken.None),
            mute ? NeverFireCron :"55 0 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });

        // 跨域依赖静态扫描（生产：每日01:50）— §阶段0.5 / 走查文档 v3.3
        // ⚠️ 排在 nightly-batch（00:30 完成 LLC）之后、scheduling（02:00）之前
        // 扫描 APS_BOM_RAW + Material + ProductFamily 的跨产品族血缘关系，
        // TRUNCATE + INSERT 全量刷新 Domain_Dependency 表，供 3 号位 Kahn 拓扑排序
        // sp_ScanDomainDependency（参见 APS_跨域依赖扫描DDL补充_v1.0.sql）
        RecurringJob.AddOrUpdate<IDomainDependencyScanService>(
            "domain-dependency-scan",
            service => service.ScanAsync(CancellationToken.None),
            mute ? NeverFireCron :"50 1 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });

        // 物料×阶段→默认生产部门 上下文重建（生产：每日01:55）— §4.3c MaterialStageDeptContext
        // ⚠️ 排在 domain-dependency-scan（01:50）之后、scheduling-trigger（02:00）之前
        // 全量重建 MaterialStageDeptContext（SCD Type 2），供 1号位排程按 (MaterialId,StageCode) 锁定 Routing 三件套
        // sp_RebuildMaterialStageDeptContext（@TriggerMode='FULL'；Database/Scripts/APS/ 独立脚本）
        RecurringJob.AddOrUpdate<IMaterialStageDeptContextRebuildService>(
            "material-stage-dept-context-rebuild",
            service => service.RebuildAsync(CancellationToken.None),
            mute ? NeverFireCron :"55 1 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });

        // 排程推演（生产：每日02:00）— §2.5.1 排程发令枪
        // 所有数据管道完成后，自动查找待排PlanVersion → 装载沙盘 → 求解 → 落盘
        RecurringJob.AddOrUpdate<ISchedulingOrchestrator>(
            "scheduling-trigger",
            service => service.RunSchedulingAutoAsync(CancellationToken.None),
            mute ? NeverFireCron :"0 2 * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Local });

        return app;
    }
}
