namespace LPS.APS.Core.DTOs.Setup;

/// <summary>
/// Setup 规则缺失（0 分钟兜底）命中频次聚合行（#10 uncovered-stats 端点响应元素）。
/// 数据源 = [Task].SetupSource = 'SETUP_RULE_MISSING_ZERO_FALLBACK' 的聚合；runId 经 PlanVersion.SourceScheduleRunId JOIN。
/// Code 回带：部门/设备/物料在聚合 SQL 内直接 JOIN 主数据表回带（DepartmentCode/ResourceCode/MaterialCode），前端零映射。
/// 四级下钻语义：runId → 部门 → 工序 → 设备 → 产品，后端按当前过滤维度返回聚合行（未传该级即不筛）。
/// </summary>
public sealed class SetupUncoveredStatDto
{
    /// <summary>排程 Run Id（经 PlanVersion.SourceScheduleRunId；无关联 Run 为 null）</summary>
    public int? ScheduleRunId { get; set; }

    /// <summary>
    /// 本次统计归属的规则集版本 Id（P1 2026-10-28 补：RuleSetVersionId 追溯链路）。
    /// 反查链：PlanVersion.SourceScheduleRunId → ScheduleRun.StrategyProfileVersionId → StrategyProfileVersion.RuleSetVersionId。
    /// 即该 Run 绑定的策略包版本所引规则集版本——回答「本统计属于哪套规则集版本」；
    /// LEFT JOIN（Run 无绑定策略包 / 关联链断链 时为空），其值不影响聚合分组。
    /// </summary>
    public long? RuleSetVersionId { get; set; }

    /// <summary>生产部门主键</summary>
    public int DepartmentId { get; set; }

    /// <summary>生产部门编码（Code 回带）</summary>
    public string DepartmentCode { get; set; } = string.Empty;

    /// <summary>
    /// 大工艺阶段码（P1 2026-10-27 补充：Task.StageCode 直取，= RoutingOperation.StageCode，1号位 透传落库）。
    /// 此前 DTO 缺此维度致前端"大工艺"列恒空；现装箱即回带。
    /// </summary>
    public string? StageCode { get; set; }

    /// <summary>工序编码</summary>
    public string OperationCode { get; set; } = string.Empty;

    /// <summary>资源主键</summary>
    public int ResourceId { get; set; }

    /// <summary>设备编码（Code 回带）</summary>
    public string ResourceCode { get; set; } = string.Empty;

    /// <summary>物料主键</summary>
    public int MaterialId { get; set; }

    /// <summary>物料编码（Code 回带）</summary>
    public string MaterialCode { get; set; } = string.Empty;

    /// <summary>该维度命中频次（Task 计数）。Hint: 前端沿用 <c>count</c> 命名则绑此值。</summary>
    public int HitCount { get; set; }

    /// <summary>命中频次兼容别名（P1 2026-10-27：前端页面绑定 <c>count</c>，与 HitCount 同值，避免改前端既有命名）。</summary>
    public int Count { get => HitCount; set => HitCount = value; }

    /// <summary>首条命中 Task Id（下钻定位用）</summary>
    public long? SampleTaskId { get; set; }
}

/// <summary>
/// Setup 规则缺失（uncovered-stats）**服务端分页**响应容器（方案 A：端点可选传 pageIndex/pageSize 时返回此形）。
/// 与 <see cref="PageResult{T}"/> 同形（items/total/page/pageSize，前端渲染 ElPagination），另带 KPI summary 三字段：
/// Total 即「未覆盖转换对总数」（分页命中聚合行总数，经窗口函数 COUNT(*) OVER() 与列表同 WHERE 同 GROUP，一次查询取得）。
/// DepartmentCount / StageCount = 涉及部门数 / 涉及大工艺数（distinct，与列表同一 WHERE 过滤），供三张 KPI 卡在服务端分页后仍全量正确。
/// </summary>
public sealed class SetupUncoveredStatsPage
{
    /// <summary>当前页聚合行</summary>
    public IReadOnlyList<SetupUncoveredStatDto> Items { get; set; } = Array.Empty<SetupUncoveredStatDto>();

    /// <summary>未覆盖转换对总数（过滤后全量聚合行数，前端 total 联动）</summary>
    public int Total { get; set; }

    /// <summary>当前页码（1 基）</summary>
    public int Page { get; set; }

    /// <summary>每页条数</summary>
    public int PageSize { get; set; }

    /// <summary>KPI：涉及部门数（distinct ProductionDepartmentId，与列表同一 WHERE）</summary>
    public int DepartmentCount { get; set; }

    /// <summary>KPI：涉及大工艺数（distinct StageCode，与列表同一 WHERE）</summary>
    public int StageCount { get; set; }
}