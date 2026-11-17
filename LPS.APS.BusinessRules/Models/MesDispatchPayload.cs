using System.Text.Json.Serialization;

namespace LPS.APS.BusinessRules.Models;

// ============================================================================
// MES 建单接口 · 报文 DTO（5号位，2026-11-12 落码）
//
// 契约出处：
//   · 5号位 2026-11-09《FULL-07 出站通道答甲、接口签名定稿》§三（MesDispatchCommand 字段权威）；
//   · 5号位 2026-11-12 致2号位增补函 §三：统一出站口=5号位 Adapter；工单号（组装/加工）均由 MES 内部生成；
//   · 分工红线：SourceTaskIds 仅 2号位 消费环回写映射用，**不出网**（本报文不含 APS 内部行键）。
//
// 字段命名按"MES 端可自识别"原则：每个字段自带语义，不用缩写；
// 时间一律输出工厂墙钟 naive 字符串（无时区后缀），与 Command 注释口径一致。
// ============================================================================

/// <summary>出站建单请求（一组=一张新 MES 工单）。</summary>
internal sealed record MesDispatchRequest
{
    /// <summary>报文契约版本，MES 侧据此演进兼容。</summary>
    [JsonPropertyName("interfaceVersion")]
    public required string InterfaceVersion { get; init; }

    /// <summary>
    /// 指示类型：<c>PRODUCTION_INSTRUCTION</c>（生产/组装指示）/ <c>SHIPPING_INSTRUCTION</c>（厂间出荷指示）。
    /// V1 现况恒为生产指示（SH→Task 未启动，Owner 2026-11-11 裁情形A）；
    /// 字段先行预留，MES 据其选择工单类别与号段规则。
    /// </summary>
    [JsonPropertyName("instructionType")]
    public required string InstructionType { get; init; }

    /// <summary>
    /// 工单模块路由：<c>ASSEMBLY</c>=组装 MES、<c>MACHINING</c>=加工 MES。
    /// 由 Adapter 按 StageCode→StageCategory 判据推导（配置可覆盖），MES 据其落到对应模块。
    /// </summary>
    [JsonPropertyName("workOrderModule")]
    public required string WorkOrderModule { get; init; }

    /// <summary>幂等参考号 `{PI}|{Stage}|{PV}`（MesDispatchReference.Compute 唯一实现，禁止自拼）。</summary>
    [JsonPropertyName("referenceNo")]
    public required string ReferenceNo { get; init; }

    /// <summary>生产指示号（ERP 生产指示表 InstructionNo；≠OrderNo）。</summary>
    [JsonPropertyName("productionInstructionNo")]
    public required string ProductionInstructionNo { get; init; }

    /// <summary>Stage 编码（如 CN_ASSY）。工单不跨 Stage（T-003）。</summary>
    [JsonPropertyName("stageCode")]
    public required string StageCode { get; init; }

    /// <summary>来源 PlanVersion，APS 侧跨版本追溯用，原样透传。</summary>
    [JsonPropertyName("sourcePlanVersionId")]
    public required int SourcePlanVersionId { get; init; }

    /// <summary>物料编码。</summary>
    [JsonPropertyName("materialCode")]
    public required string MaterialCode { get; init; }

    /// <summary>
    /// 建单数量＝净合格口径（Task.Quantity），**组合级"同 PV 定容"载体量**——
    /// 不是行级求和；MES 同 PI 安全门按"已开未报占用＋本数 ≤ PI 剩余"校验。
    /// </summary>
    [JsonPropertyName("quantity")]
    public required decimal Quantity { get; init; }

    /// <summary>数量单位（如 PS）。</summary>
    [JsonPropertyName("uom")]
    public required string Uom { get; init; }

    /// <summary>工厂码（CN/TJ/BJ/CN6/SH 体系）。</summary>
    [JsonPropertyName("factoryCode")]
    public required string FactoryCode { get; init; }

    /// <summary>
    /// 设备编码；**人工槽场景为"部门码+槽码"组合身份**（FULL-07 §三定稿口径），
    /// 无资源时为 null，由 MES 侧按部门排到产线。
    /// </summary>
    [JsonPropertyName("resourceCode")]
    public string? ResourceCode { get; init; }

    /// <summary>
    /// 【v1.1·待2号位列】部门信息（源=TaskDispatch.ProductionDepartmentCode，如 CN_ASSY_制造3-2课761HL）。
    /// 组装 MES 端据此反查六位部门代码（Holon）——**联调硬前置**：缺此字段 MES 以 DEPT_NOT_MAPPED 拒绝。
    /// Command 补列（会签第三列）前恒 null 省略。
    /// </summary>
    [JsonPropertyName("productionDepartmentCode")]
    public string? ProductionDepartmentCode { get; init; }

    /// <summary>
    /// 【v1.1·待真值链】工艺路径名（源=Task.RouteCode）。MES 工艺匹配优先按此选路、匹配不上回落默认。
    /// Task.RouteCode 现全表 'DEFAULT'（11-05 §九 登记），真值回写就绪后在此透传。
    /// </summary>
    [JsonPropertyName("routeCode")]
    public string? RouteCode { get; init; }

    /// <summary>
    /// 【v1.1·预留】通用指示号：生产=MTS_InstructionNo；出荷=SH号（Order.OrderNo，INTER_FACTORY）。
    /// V1 生产场景以 productionInstructionNo 为权威、本字段省略；SH→Task 链路启动后启用。
    /// </summary>
    [JsonPropertyName("instructionNo")]
    public string? InstructionNo { get; init; }

    /// <summary>计划开工，工厂墙钟 naive，格式 yyyy-MM-dd HH:mm:ss。</summary>
    [JsonPropertyName("plannedStartTime")]
    public required string PlannedStartTime { get; init; }

    /// <summary>计划完工，同上格式。</summary>
    [JsonPropertyName("plannedEndTime")]
    public required string PlannedEndTime { get; init; }

    /// <summary>本次出站时间（排障/对账用，MES 不参与业务判断）。</summary>
    [JsonPropertyName("dispatchedAt")]
    public required string DispatchedAt { get; init; }
}
