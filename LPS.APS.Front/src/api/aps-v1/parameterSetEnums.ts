/**
 * APS V1 4号位 — 参数集内容层枚举 / 数值区间（前端下拉与输入边界的唯一真源）
 *
 * 背景（2026-10-22 3号位《新增规则参数内容可选值与枚举字典 v1.0》）：
 * 内容层**没有字典表、没有下拉接口**，唯一真源 = C# DTO 硬编码枚举。
 *
 * 🔴 块 JSON 里存的是**数字序号**，不是枚举名：
 *   后端反序列化块 JSON 用 `JsonSerializerOptions { PropertyNameCaseInsensitive = true }`
 *   （`GovernanceVersionService.cs:1319-1322`），**没有** JsonStringEnumConverter
 *   ⇒ System.Text.Json 按序号读写。dev 实测 `PS_DEMO` 也是 `Mode: 0` / `BottleneckMode: 0`。
 *   前端若写字符串 "Forward"，发布校验会直接判 `INVALID_JSON` —— 所以下拉的 value 必须是数字。
 *
 * 真源（只读引用）：
 *  - `lps/LPS.APS.Core/Dto/FrozenStrategySnapshot.cs:203-209`
 *      `PiSortParams { PiSortBy SortBy; bool UseStablePiNoTieBreak }`
 *      `enum PiSortBy { IssueDateAsc, CreatedAtAsc, StablePiNoAsc }`
 *  - `lps/LPS.APS.Core/Dto/FrozenStrategySnapshot.cs:285`
 *      `enum SolverStrategyMode { Forward, Backward, Mixed, Auto }`
 *  - `lps/LPS.APS.Core/Dto/FrozenStrategySnapshot.cs:286`
 *      `enum DynamicBottleneckMode { Auto, PreferAnchor, ForceAnchor, NotAnchor }`
 *  - `lps/LPS.APS.Application/Services/SolverStrategyValidator.cs:46-112`
 *      数值区间（发布前校验）
 */

import type { BlockKey } from './types/rule'

export interface ParameterEnumOption {
  /** 中文展示名（用户看到的就是这个） */
  label: string
  /** 写回 JSON 的数字序号（必须与 C# 枚举声明顺序一致） */
  value: number
}

export interface ParameterNumberLimits {
  min?: number
  max?: number
}

/* ===== 三枚枚举（键 = 块内点分路径） ===== */

const PI_SORT_BY_OPTIONS: readonly ParameterEnumOption[] = [
  { label: '按发行日期升序', value: 0 }, // IssueDateAsc
  { label: '按创建时间升序', value: 1 }, // CreatedAtAsc
  { label: '按 PI 号稳定升序', value: 2 } // StablePiNoAsc
]

const SOLVER_MODE_OPTIONS: readonly ParameterEnumOption[] = [
  { label: '正向排产（从前往后）', value: 0 }, // Forward
  { label: '逆向排产（从后往前）', value: 1 }, // Backward
  { label: '混合方向', value: 2 }, // Mixed
  { label: '自动（由求解器判断）', value: 3 } // Auto
]

const BOTTLENECK_MODE_OPTIONS: readonly ParameterEnumOption[] = [
  { label: '自动判定', value: 0 }, // Auto
  { label: '优先指定锚点', value: 1 }, // PreferAnchor
  { label: '强制指定锚点', value: 2 }, // ForceAnchor
  { label: '排除指定锚点', value: 3 } // NotAnchor
]

const ENUM_OPTIONS: Partial<Record<BlockKey, Record<string, readonly ParameterEnumOption[]>>> = {
  supply: {
    'PiSort.SortBy': PI_SORT_BY_OPTIONS
  },
  solverStrategy: {
    Mode: SOLVER_MODE_OPTIONS,
    BottleneckMode: BOTTLENECK_MODE_OPTIONS
  }
}

/* ===== 字符串值域（值本身是英文码，不是数字序号）=====
 * 与上面的数字枚举是两回事：这些字段在块 JSON 里存的就是字符串 / 字符串数组
 * （如 `Trigger.ProtectedOrderTypes: ["SALES_ORDER"]`）。
 *
 * 为什么必须由前端兜住值域：后端**发布校验不拦**这些值，只在运行期按 Ordinal 精确比
 * （`DemandProtectionTriggerEvaluator.cs:170` 客户等级等值 / `:184` 订单类型集合包含
 *  / `:197` 成熟度集合包含）⇒ 填错一个字母、填中文、填旧别名，都是「配了但不命中」，
 * 而且**没有任何报错**。自由文本框挡不住 ⇒ 页面只给下拉。
 */

/** 字符串值域的候选项 */
export interface ParameterStringOption {
  /** 中文展示名（用户看到的就是这个） */
  label: string
  /** 写回 JSON 的英文码（必须与实库原值逐字相同） */
  value: string
}

export interface ParameterStringDomain {
  /** 候选项 */
  options: readonly ParameterStringOption[]
  /** 允许手填（值域可能随源字典扩展；手填的值照样写回 JSON） */
  allowCustom?: boolean
}

/**
 * 订单类型（实库 `Order.OrderType` 原值，`OrderQueryDtos.cs:24`）
 *
 * ⚠️ 旧别名 `SO` / `WO` **不在值域里**：规则集侧为兼容存量把它们放行
 * （`prioritySegmentEditor.ts:67`），但需求保护是 Ordinal 精确匹配 ⇒ 填 SO 永不命中。
 */
const ORDER_TYPE_OPTIONS: readonly ParameterStringOption[] = [
  { value: 'SALES_ORDER', label: '客户订单（SALES_ORDER）' },
  { value: 'PRODUCTION_INSTRUCTION', label: '生产指示（PRODUCTION_INSTRUCTION）' }
]

/** 客户等级（2号位 2026-11-06 校准收敛为 VIP / GENERAL；旧 A/B/C、KEY_ACCOUNT/STANDARD 不再是合法维护值） */
const CUSTOMER_TIER_OPTIONS: readonly ParameterStringOption[] = [
  { value: 'VIP', label: 'VIP' },
  { value: 'GENERAL', label: '一般（GENERAL）' }
]

/** 需求成熟度（`FrozenStrategySnapshot.cs:193` 枚举：事前确认 PRE_CONFIRMED / 预测 FORECAST） */
const DEMAND_MATURITY_OPTIONS: readonly ParameterStringOption[] = [
  { value: 'PRE_CONFIRMED', label: '事前确认（PRE_CONFIRMED）' },
  { value: 'FORECAST', label: '预测（FORECAST）' }
]

const STRING_DOMAINS: Partial<Record<BlockKey, Record<string, ParameterStringDomain>>> = {
  lock: {
    'Trigger.VipTierValue': { options: CUSTOMER_TIER_OPTIONS, allowCustom: true },
    'Trigger.ProtectedOrderTypes': { options: ORDER_TYPE_OPTIONS },
    'Trigger.MaturityStatusValues': { options: DEMAND_MATURITY_OPTIONS }
  }
}

/* ===== 数值区间（SolverStrategyValidator 发布前校验口径） ===== */

const NUMBER_LIMITS: Partial<Record<BlockKey, Record<string, ParameterNumberLimits>>> = {
  solverStrategy: {
    'OnTimeTarget.TargetPercent': { min: 0, max: 100 },
    'Setup.SetupSearchBudget': { min: 100, max: 5000 },
    'Setup.SetupMaxNeighborhoodTries': { min: 10, max: 500 }
  }
}

/* ===== 查询 ===== */

export function lookupParameterEnum(
  block: BlockKey,
  path: readonly string[]
): readonly ParameterEnumOption[] | undefined {
  return ENUM_OPTIONS[block]?.[path.join('.')]
}

export function lookupParameterLimits(
  block: BlockKey,
  path: readonly string[]
): ParameterNumberLimits | undefined {
  return NUMBER_LIMITS[block]?.[path.join('.')]
}

/** 字符串值域（字段有值域 ⇒ 页面渲染下拉，不给自由文本） */
export function lookupParameterStringDomain(
  block: BlockKey,
  path: readonly string[]
): ParameterStringDomain | undefined {
  return STRING_DOMAINS[block]?.[path.join('.')]
}
