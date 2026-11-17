// CTP 承诺交期评估结果契约 v1.0（2026-11-16 定稿，3号位）
// 业务依据：冻结《01 最终全部流程与业务基线 v1.15》§68.1/§68.2/§75/§76/§77/§78；
//           §77 一次插单 Candidate 至少返回 9 条；§76 跨域 Quantity-Time 必须保留分段。
// 落码方：3号位（薄层契约）；试算产出方：1号位/2号位（按本契约接入）。
// 对外唯一报文形状：POST /api/ctp/evaluate 返回 CtpEvaluateResultDto（含 400/403/500/501/504 错误映射）。
using System;
using System.Collections.Generic;

namespace LPS.APS.Core.DTOs.Ctp
{
    /// <summary>
    /// CTP/插单评估结果（承诺交期评估）。冻结 01 §77 一次插单至少返回 9 条；§75 跨域最终只回一个客户答案。
    /// </summary>
    public sealed class CtpEvaluateResultDto
    {
        /// <summary>是否满足客户请求交期（冻结 01 §77-1）</summary>
        public bool MeetsRequestedDueDate { get; init; }

        /// <summary>最早可承诺完成时间（冻结 01 §77-2，单域；跨域取各单域汇总后最早）</summary>
        public DateTime EarliestCompletion { get; init; }

        /// <summary>是否依赖 ESTIMATED 采购占位（冻结 01 §77-3；true 时页面须显「估算日期，非确定性承诺」）</summary>
        public bool DependsOnEstimatedSupply { get; init; }

        /// <summary>受影响订单总数（冻结 01 §77-4；= ImpactedOrders.Count，不受阈值截断，见 03 §27）</summary>
        public int ImpactedOrderCount { get; init; }

        /// <summary>提示/警戒阈值（冻结 03 §27：只作提示、不得作物理截止线；超过仅 Warning 不截断）</summary>
        public int MaxImpactedOrdersThreshold { get; init; }

        /// <summary>受影响订单明细（冻结 01 §77-5「哪些订单仍按期」+ §77-6「哪些订单延期及最大影响」）</summary>
        public IReadOnlyList<CtpImpactedOrderDto> ImpactedOrders { get; init; } = Array.Empty<CtpImpactedOrderDto>();

        /// <summary>Demand Protection 冲突清单（冻结 01 §77-7；空数组 = 未触碰任何受保护订单）</summary>
        public IReadOnlyList<CtpProtectionConflictDto> ProtectionConflicts { get; init; } = Array.Empty<CtpProtectionConflictDto>();

        /// <summary>主要瓶颈（冻结 01 §77-8，如「MC03 注塑机产能不足」）</summary>
        public string MainBottleneck { get; init; } = string.Empty;

        /// <summary>主要原因（冻结 01 §77-9；ReasonCode 取冻结 07 §八.1 权威 15 码，与甘特/异常解释页共用同一份码表）</summary>
        public IReadOnlyList<CtpReasonDto> Reasons { get; init; } = Array.Empty<CtpReasonDto>();

        /// <summary>是否单域结果（冻结 01 §75：跨域仍只回一个客户答案，非单域时不落单一 DomainResults）</summary>
        public bool IsSingleDomain { get; init; }

        /// <summary>是否跨域链式（冻结 01 §75：按 Domain_Dependency 拓扑串行多个单域 WHATIF 后汇总）</summary>
        public bool IsCrossDomainChained { get; init; }

        /// <summary>各单域试算结果（跨域时按拓扑顺序返回；结 01 §76 分段由此承载，顶层仍只回一个答案）</summary>
        public IReadOnlyList<CtpDomainResultDto> DomainResults { get; init; } = Array.Empty<CtpDomainResultDto>();
    }

    /// <summary>受影响订单（冻结 01 §77-5/§77-6）</summary>
    public sealed class CtpImpactedOrderDto
    {
        /// <summary>内部订单 Id（仅内部关联，不作展示）</summary>
        public int OrderId { get; init; }

        /// <summary>订单号（对外展示口径）</summary>
        public string OrderNo { get; init; } = string.Empty;

        /// <summary>原完成时间</summary>
        public DateTime OriginalCompletion { get; init; }

        /// <summary>新完成时间（试算后；未延期时与原完成时间相同）</summary>
        public DateTime? NewCompletion { get; init; }

        /// <summary>是否由按期变为延期（冻结 01 §77-5/§77-6 的分界）</summary>
        public bool BecomesDelayed { get; init; }

        /// <summary>是否触碰 Demand Protection</summary>
        public bool HasProtectionConflict { get; init; }
    }

    /// <summary>Demand Protection 冲突（冻结 01 §77-7）</summary>
    public sealed class CtpProtectionConflictDto
    {
        /// <summary>内部订单 Id</summary>
        public int OrderId { get; init; }

        /// <summary>订单号（对外展示口径）</summary>
        public string OrderNo { get; init; } = string.Empty;

        /// <summary>冲突类型（如 PROTECTION_OVERLAP）</summary>
        public string ConflictType { get; init; } = string.Empty;

        /// <summary>中文说明（页面直接展示，不做二次加工）</summary>
        public string Description { get; init; } = string.Empty;
    }

    /// <summary>主要原因（冻结 01 §77-9）</summary>
    public sealed class CtpReasonDto
    {
        /// <summary>原因码（冻结 07 §八.1 权威 15 码之一；不得自造同义码）</summary>
        public string ReasonCode { get; init; } = string.Empty;

        /// <summary>中文说明</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>是否 Estimated（冻结 01 §77：依赖规划性采购占位时须明确标「估算，非确定性承诺」）</summary>
        public bool IsEstimated { get; init; }

        /// <summary>关联对象引用（如资源号、采购单号）</summary>
        public string? RelatedRef { get; init; }
    }

    /// <summary>单域试算结果（跨域时按 Domain_Dependency 拓扑顺序返回）</summary>
    public sealed class CtpDomainResultDto
    {
        /// <summary>域键</summary>
        public string DomainKey { get; init; } = string.Empty;

        /// <summary>该域最早完成时间</summary>
        public DateTime EarliestCompletion { get; init; }

        /// <summary>该域主要瓶颈</summary>
        public string MainBottleneck { get; init; } = string.Empty;

        /// <summary>该域是否成功</summary>
        public bool Success { get; init; }

        /// <summary>失败原因</summary>
        public string? FailureReason { get; init; }

        /// <summary>数量—时间切片（冻结 01 §76：跨域必须保留分段，不得只汇总成一个时间点；供下游允许分批启动）</summary>
        public IReadOnlyList<CtpQuantityTimeSegmentDto> QuantityTimeSegments { get; init; } = Array.Empty<CtpQuantityTimeSegmentDto>();
    }

    /// <summary>数量-时间切片（冻结 01 §76 跨域 Quantity-Time 必须保留分段用）</summary>
    public sealed class CtpQuantityTimeSegmentDto
    {
        /// <summary>分段数量</summary>
        public decimal Quantity { get; init; }

        /// <summary>分段可用时间</summary>
        public DateTime AvailableTime { get; init; }
    }
}