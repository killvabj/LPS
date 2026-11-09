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
