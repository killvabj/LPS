using System.Reflection;
using LPS.APS.Core.Dto;
using Xunit;

namespace LPS.APS.Tests.Position5.Contracts;

/// <summary>
/// 函码一致性护栏（R3′，11-18 第四次"函说已补、码没落全"之后新增）。
/// 根因复盘：SQL 选列、DTO 无属性 ⇒ Dapper 按属性名静默丢列 ⇒ 响应体键恒不出现；
/// 我方 DB 级预跑对这种"映射层丢列"完全免疫失效。**预跑只能证 DB 有值，证不了返回体带键。**
/// 本测试把「历次知会函承诺过的返回键」钉在类型层——属性缺失=编译期即绿不了、运行期直接红。
/// 规则：以后每封"已落码"函发出前，新增承诺键必须先落进本表（与 4号位 TS 类型逐字对齐 camelCase 后名）。
///
/// ── 缺键语义表（4号位 §四.4 建议采纳，11-18：类型钉键名，本表钉语义——"键在、语义歪"两头都夹）──
/// | 键 | 宿主 | 缺键/null 语义 | 禁 Interpretation |
/// |---|---|---|---|
/// | mesDispatchStatus | A4/A5 行 | 不在下发队列 | ≠「未下发」≠「无需 MES」（Q-a/Q-d） |
/// | dispatchStatus | gantt 行 | 同 mesDispatchStatus（R3″ 键①；统一归 V1.1 窗） | 同上 |
/// | dispatchStatus | mes/status **P11 工序行** | **非空 string——行源即 TaskDispatch，缺键=该行不存在**（与键①②"队列外 Task 缺键"差一拍；行在则值必在）（R3″ 键③，v1.2 抓漏认账） | 同上；三键同源同值域 |
/// | hasPlanningTasks | pool 行 | 新单行缺键=不适用 | ≠「无排产任务」（那是老单 false） |
/// | ownShareQty | 树节点 | 无直接归属记录（组件链常态） | ≠ 0 ≠ 不占资源 |
/// | shareRootCount | 树节点 | 键缺=旧版后端未提供 | 勿拿 0 当"无人共享" |
/// | supplyBatchPresent | trace summary | 未判定（探测失败/空授权） | ≠ false（源批缺） |
/// | setupHours / setupLossRatePct | resource-load | ③未具备数据条件（Solver 产物缺持久列，Q-b） | ≠ 0%、≠"无换型" |
/// | calendarAvailableHours | resource-load 行 | 分母未判定/无合法日历 | 禁按 8h 或全天假设；有值零=OFF 日 |
/// | targetPercent | delivery-kpi | 待 3号位 合同结论（Q-c） | 禁前端写死 95 |
/// | plannedCompletionSource | delivery-kpi | "readmodel_task_max"=我方计算读模型；值变"formal"=2号位列已落库 | 切换须用户可感知 |
/// | commitmentStatus | pool/树 demands | 全史 NULL=写侧未落值（D05-01） | 显示「未提供」，≠「未承诺」 |
/// | isMatched | manual-eta 行 | 未判定（withMatched 未开/探测失败） | ≠ false（未命中） |
/// | reported | mes/status 行 | false=快照无行=未判定 | ≠「零报工」≠「未开工」 |
/// | mesRequirementCategory | mes/status 行 | 恒 null=分类权威未建（Q-a） | 禁由工单号空值反推 |
/// （新键入表时**必须同时登记语义行**，否则该键不得进"已落码"函。）
/// </summary>
public class FrontendContractGuardTests
{
    private static PropertyInfo? Prop(Type t, string name)
        => t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);

    [Theory]  // N2（r14264）+ R3′（本批）：A4/A5 行承诺键
    [InlineData("ResourceCode")]
    [InlineData("MesDispatchStatus")]
    [InlineData("MesWorkOrderNo")]
    [InlineData("SetupSource")]
    public void TaskListItemDto_HasPromisedKeys(string key)
    {
        Assert.NotNull(Prop(typeof(TaskListItemDto), key));
    }

    [Fact]
    public void TaskListItemDto_FullPromisedKeySet()
    {
        var t = typeof(TaskListItemDto);
        foreach (var key in new[] { "ResourceId", "ResourceCode", "ResourceName",
                                    "ManualSlotId", "ManualSlotCode",
                                    "ProductionDepartmentId", "ProductionDepartmentName",
                                    "MesWorkOrderNo", "MesDispatchStatus", "SetupSource",
                                    "PlannedStartTime", "PlannedEndTime", "Duration" })
        {
            Assert.NotNull(Prop(t, key));
        }
        // A5 详情行=列表行子类（TaskDetailRow 在 Application 内，DTO 侧以 TaskListItemDto 为契约面）
    }

    [Fact]
    public void OrderPoolItemDto_HasPlanningTasks_MustBeNullableBool()
    {
        // R2 裁 (a)：非空 bool 会把 SQL 的 NULL 塌成 false（键恒 false≠键省略）——类型钉死。
        var p = Prop(typeof(OrderPoolItemDto), "HasPlanningTasks");
        Assert.NotNull(p);
        Assert.Equal(typeof(bool?), p!.PropertyType);
    }

    [Fact]
    public void OrderTreeNodeDto_SevenProjectionColumns()
    {
        // R1（11-18 认账补齐）：树节点三组七列与 A4 行逐字同命名。
        var t = typeof(OrderTreeNodeDto);
        foreach (var key in new[] { "ProductionDepartmentId", "ProductionDepartmentName",
                                    "ResourceId", "ResourceCode", "ResourceName",
                                    "ManualSlotId", "ManualSlotCode",
                                    "OwnShareQty", "ShareRootCount" })
        {
            Assert.NotNull(Prop(t, key));
        }
    }

    [Fact]
    public void ResourceLoadRowDto_PromisedKeys()
    {
        // A6（r14239）：知会函承诺的负荷率键面。
        var t = typeof(ResourceLoadRowDto);
        foreach (var key in new[] { "ScheduledHours", "CalendarAvailableHours", "CalendarSource",
                                    "CalendarLoadRatePct", "Fixed16LoadRatePct", "SetupLossRatePct",
                                    "NotApplicableReason" })
        {
            Assert.NotNull(Prop(t, key));
        }
        // 顶层位：SetupHoursAvailable 为"全表统一 false=③未具备数据条件"的显式位（存在即可，值归联测）
        Assert.NotNull(Prop(typeof(ResourceLoadResultDto), "SetupHoursAvailable"));
    }

    [Fact]
    public void PeggingTraceSummaryDto_BatchFlag_ThreeState()
    {
        // Q3（r14240/41）：三态布尔必须可空（null=未判定，禁塌 bool）。
        Assert.Equal(typeof(bool?), Prop(typeof(PeggingTraceSummaryDto), "SupplyBatchPresent")!.PropertyType);
    }

    [Fact]
    public void MesStatusBatchRowDto_ThreeLayerKeys()
    {
        // B6（r14259）：三层状态分列的键面承诺。
        var t = typeof(MesStatusBatchRowDto);
        foreach (var key in new[] { "CreatedCount", "PendingCount", "FailedCount", "OtherCount",
                                    "WorkOrderStatus", "WorkOrderStatusMixed", "Reported",
                                    "GoodQty", "RemainingQty", "LastReportTime", "BatchQty",
                                    "MesRequirementCategory" })
        {
            Assert.NotNull(Prop(t, key));
        }
    }

    [Fact]
    public void DeliveryKpiRowDto_CaliberKeys()
    {
        // B2（r14250）：KPI 行六数＋顶层三件口径位。
        var r = typeof(DeliveryKpiRowDto);
        foreach (var key in new[] { "TotalOrders", "OnTimeOrders", "LateOrders", "UndeterminedOrders",
                                    "OnTimeRatePct", "LateRatePct" })
        {
            Assert.NotNull(Prop(r, key));
        }
        var t = typeof(DeliveryKpiResultDto);
        Assert.NotNull(Prop(t, "Caliber"));
        Assert.NotNull(Prop(t, "PlannedCompletionSource"));
        Assert.NotNull(Prop(t, "TargetPercent"));
    }
}
