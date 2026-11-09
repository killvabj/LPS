/**
 * APS V1 4号位 — 参数集「采购良率」（Procurement.PlanningYields）读写与校验（纯函数）
 *
 * 真源（只读引用；4号位 不改 lps/）：
 *   lps/LPS.APS.Core/Dto/FrozenStrategySnapshot.cs:253-258
 *     PlanningYieldRule { string MaterialId; string? StageCode; decimal YieldPercent }
 *   lps/LPS.APS.Application/Services/GovernanceVersionService.cs:1299-1328
 *     发布校验：0 < YieldPercent <= 100（否则 INVALID_YIELD）；
 *              (MaterialId, StageCode) 不得重复（否则 DUPLICATE_PLANNING_YIELD）
 *   lps/LPS.APS.Application/Services/PlanningYieldResolver.cs:62,78
 *     运行期按 **物料编码** 匹配（OrdinalIgnoreCase）⇒ MaterialId 存的是 MaterialCode
 *
 * 🔴 **MaterialId 是编码，不是数值主键 Id**
 *   DTO 注释（FrozenStrategySnapshot.cs:255）写的是「Material 主表唯一 Id（数值主键）」，
 *   但运行期 `PlanningYieldResolver.ResolveWithSource` 用
 *   `string.Equals(rule.MaterialId, materialCode, OrdinalIgnoreCase)` 匹配 ——
 *   传数值 Id 永远匹配不上真实物料 ⇒ 该规则运行时永不生效。
 *   ⇒ 以**运行期**为准（能生效的那份）：写数值 Id 会永不命中、良率静默恒 100%。
 *   ✅ 2026-10-26 3号位 裁定 A 已定 = **MaterialCode**（回执「裁定回执A_编码_v1.0_20261026」）：
 *     后端仅更正 DTO 注释（FrozenStrategySnapshot.cs:255），运行代码零改动；
 *     同块 `PurchaseLtRule.MaterialId` **仍是数值主键 Id**（:237），两字段身份不同，勿统一。
 *
 * ⚠️ StageCode 为空 = 物料级默认（DTO 注释 + 运行期 `isStageLevel` 判定，两处一致）。
 * ⚠️ 键名一律 PascalCase（后端 `PropertyNameCaseInsensitive = true`，写错大小写只静默取默认值）。
 */

import type { BlockKey, ParameterSetBlocks } from './types/rule'
import { readObjectArray, writeObjectArray } from './warehouseRefs'

/** 采购参数块 → 计划良率（块内路径） */
export const PLANNING_YIELDS_PATH = ['PlanningYields'] as const

/** 良率项（与后端 `PlanningYieldRule` 对齐） */
export interface PlanningYieldItem {
  /** 物料**编码**（运行期按编码匹配） */
  MaterialId?: string
  /** 工序（大工艺）码；空 = 物料级默认 */
  StageCode?: string | null
  /** 良率百分数（0 < p <= 100） */
  YieldPercent?: number
}

/* ===== 读写 ===== */

/** 读当前编辑态里的良率行（缺失 / 类型不符 ⇒ 空数组） */
export function readPlanningYields(
  blocks: ParameterSetBlocks | null | undefined
): PlanningYieldItem[] {
  return readObjectArray<PlanningYieldItem>(blocks, 'procurement', PLANNING_YIELDS_PATH)
}

/** 写良率行（整份数组覆盖） */
export function writePlanningYields(blocks: ParameterSetBlocks, items: PlanningYieldItem[]): void {
  writeObjectArray(blocks, 'procurement', PLANNING_YIELDS_PATH, items)
}

/**
 * 通用参数表里要剔除这一行（交给专属编辑器）。
 *
 * 与 `warehouseRefs.isWarehouseRefRow` 同款判定：块 + 点分路径精确匹配，不用子串。
 */
export function isPlanningYieldRow(block: BlockKey, path: readonly string[]): boolean {
  return block === 'procurement' && path.join('.') === PLANNING_YIELDS_PATH.join('.')
}

/**
 * 复合键：物料 + 工序（与后端一致：大小写不敏感；工序空/空白 = 物料级默认）。
 * 后端键是 `$"{rule.MaterialId}|{rule.StageCode}"`，null 与 "" 都插值成空串 ⇒ 同一键。
 */
export function planningYieldKey(item: PlanningYieldItem): string {
  const material = String(item?.MaterialId ?? '')
    .trim()
    .toLowerCase()
  const stage = String(item?.StageCode ?? '')
    .trim()
    .toLowerCase()
  return `${material}|${stage}`
}

/* ===== 校验（口径 = 后端发布校验）===== */

/**
 * 校验良率行（口径逐条对齐后端 `ValidateProcurementJson`）。
 *
 * 返回大白话问题清单（空数组 = 通过）。**不阻断保存** —— 只提示，
 * 真闸门在后端发布校验（INVALID_YIELD / DUPLICATE_PLANNING_YIELD）。
 */
export function validatePlanningYields(items: PlanningYieldItem[]): string[] {
  const problems: string[] = []
  const seen = new Map<string, number>()
  items.forEach((item, i) => {
    const no = i + 1
    const material = String(item?.MaterialId ?? '').trim()
    const stage = String(item?.StageCode ?? '').trim()
    const percent = Number(item?.YieldPercent)
    if (!material) {
      problems.push(`第 ${no} 行：还没选物料`)
    }
    if (!Number.isFinite(percent) || percent <= 0 || percent > 100) {
      problems.push(`第 ${no} 行：良率要大于 0、不超过 100`)
    }
    const key = planningYieldKey(item)
    const prev = seen.get(key)
    if (prev !== undefined) {
      problems.push(
        `第 ${no} 行：物料 ${material || '（没选）'} 工序 ${stage || '（物料级）'} 和第 ${prev} 行重复了`
      )
    } else {
      seen.set(key, no)
    }
  })
  return problems
}
