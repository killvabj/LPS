/**
 * APS V1 4号位 — 参数集**参数级**差异比对（版本对比 Tab 的真源）
 *
 * 起因（2026-10-28 页面层实测）：后端 `GET /parameter-set/version/diff` 的字段粒度是
 *   **块级**（5 个主题块 + 7 个版本元信息 = 12 项），把整块 JSON 塞进一格 ⇒
 *   用户问「我这次改了哪个参数」时得自己在 JSON 里找。
 *   本模块用**同一套扁平化算法**（`draftBuffer.flattenBlocks`）把两个版本展开成参数行再逐行比，
 *   输出「块 / 参数 / 变化 / 旧值 / 新值」——**纯前端、零后端改动**。
 *
 * 与后端 diff 的分工：
 *   - 后端 diff ⇒ **版本元信息**（版本码 / 状态 / 生效窗 / 发布时间 / 发布人 / 备注）
 *   - 本模块   ⇒ **参数内容**（逐参数）
 *
 * ⚠️ 归一化口径（两边都做，否则会出假差异）：
 *   锚点资源编码 `AnchorResourceCode` 是前端写侧补的键（`ensureAnchorResourceCodeKey`），
 *   老版本存的内容里**没有**这个键 ⇒ 直接比会多出一条「新增：锚点资源编码 =（空）」的假变更。
 *   ⇒ 两边统一「块非空就补空串、块只剩空串就摘掉」，空串与缺键视为同一个状态。
 */

import { ensureAnchorResourceCodeKey, pruneAnchorOnlyBlock } from './anchorResource'
import { flattenBlocks, type JsonValue, type ParameterRow } from './draftBuffer'
import type { RuleStatus } from './types/common'
import type { BlockKey, ParameterSetBlocks } from './types/rule'

/** 差异种类 */
export type ParameterDiffKind = 'added' | 'removed' | 'changed'

/** 一条参数级差异 */
export interface ParameterDiffRow {
  /** 所属块 */
  block: BlockKey
  /** 块内点分路径（如 `Split.MaxOptimizationSplitCount`） */
  path: string
  /** 中文名（字段目录命中时给中文，否则给路径末段） */
  label: string
  /** 变化类型 */
  kind: ParameterDiffKind
  /** 旧值（展示用文本；新增时为空串） */
  oldValue: string
  /** 新值（展示用文本；删除时为空串） */
  newValue: string
}

/** 5 块的中文名（与页面 `BLOCK_LABELS` 同口径） */
export const PARAM_BLOCK_LABELS: Record<BlockKey, string> = {
  lock: '锁定策略',
  supply: '供应参数',
  procurement: '采购参数',
  solverStrategy: '求解器策略',
  candidateGuardrail: '候选版本守门'
}

/**
 * 归一化一块 JSON（两边同口径）
 *
 *  - 求解器策略块：先摘掉「只剩空串锚点键」的空块，再补锚点键（块非空才补）
 *  - 其余块：不动
 */
export function normalizeBlocksForDiff(blocks: ParameterSetBlocks): ParameterSetBlocks {
  const clone: ParameterSetBlocks = {
    lock: { ...(blocks.lock ?? {}) },
    supply: { ...(blocks.supply ?? {}) },
    procurement: { ...(blocks.procurement ?? {}) },
    solverStrategy: { ...(blocks.solverStrategy ?? {}) },
    candidateGuardrail: { ...(blocks.candidateGuardrail ?? {}) }
  }
  pruneAnchorOnlyBlock(clone, 'solverStrategy')
  ensureAnchorResourceCodeKey(clone)
  return clone
}

/** 参数值 → 展示文本（枚举给中文名、布尔给是/否、数组给顿号串） */
export function formatDiffValue(row: ParameterRow, value: JsonValue | undefined): string {
  if (value === undefined || value === null) return '（未设置）'
  if (row.options) {
    const hit = row.options.find((o) => o.value === value)
    if (hit) return hit.label
  }
  if (typeof value === 'boolean') return value ? '是' : '否'
  if (Array.isArray(value)) {
    return value.length
      ? value.map((x) => (typeof x === 'string' ? x : JSON.stringify(x))).join('、')
      : '（空列表）'
  }
  if (typeof value === 'object') return JSON.stringify(value)
  const text = String(value)
  // 非文本清单的数组在扁平化时被 JSON.stringify 成字符串 ⇒ 这里还原成顿号串
  const trimmed = text.trim()
  if (trimmed.startsWith('[') || trimmed.startsWith('{')) {
    try {
      const parsed: unknown = JSON.parse(trimmed)
      if (Array.isArray(parsed)) {
        return parsed.length
          ? parsed.map((x) => (typeof x === 'string' ? x : JSON.stringify(x))).join('、')
          : '（空列表）'
      }
      return JSON.stringify(parsed)
    } catch {
      return text
    }
  }
  return text
}

/** 行键（块 + 路径） */
function rowKey(row: ParameterRow): string {
  return `${row.block}::${row.path.join('.')}`
}

/** 展示名：字段目录中文名优先，否则用路径末段 */
function rowLabel(row: ParameterRow): string {
  return row.label ?? row.path[row.path.length - 1] ?? '（未命名）'
}

/**
 * 两个版本的 5 块 → 参数级差异行
 *
 *  - 新增：目标版本有、源版本没有的路径
 *  - 删除：源版本有、目标版本没有的路径
 *  - 变更：两边都有、值不同
 * 排序：按 5 块固定顺序 → 路径字典序（保证同一对版本每次渲染顺序一致）
 */
export function diffParameterBlocks(
  source: ParameterSetBlocks,
  target: ParameterSetBlocks
): ParameterDiffRow[] {
  const srcRows = flattenBlocks(normalizeBlocksForDiff(source))
  const tgtRows = flattenBlocks(normalizeBlocksForDiff(target))
  const srcMap = new Map<string, ParameterRow>(srcRows.map((r) => [rowKey(r), r]))
  const tgtMap = new Map<string, ParameterRow>(tgtRows.map((r) => [rowKey(r), r]))

  const out: ParameterDiffRow[] = []
  for (const [key, tgt] of tgtMap) {
    const src = srcMap.get(key)
    if (!src) {
      out.push({
        block: tgt.block,
        path: tgt.path.join('.'),
        label: rowLabel(tgt),
        kind: 'added',
        oldValue: '',
        newValue: formatDiffValue(tgt, tgt.value)
      })
      continue
    }
    const oldText = formatDiffValue(src, src.value)
    const newText = formatDiffValue(tgt, tgt.value)
    if (oldText !== newText) {
      out.push({
        block: tgt.block,
        path: tgt.path.join('.'),
        label: rowLabel(tgt),
        kind: 'changed',
        oldValue: oldText,
        newValue: newText
      })
    }
  }
  for (const [key, src] of srcMap) {
    if (tgtMap.has(key)) continue
    out.push({
      block: src.block,
      path: src.path.join('.'),
      label: rowLabel(src),
      kind: 'removed',
      oldValue: formatDiffValue(src, src.value),
      newValue: ''
    })
  }

  const blockOrder: BlockKey[] = [
    'lock',
    'supply',
    'procurement',
    'solverStrategy',
    'candidateGuardrail'
  ]
  return out.sort((a, b) => {
    const d = blockOrder.indexOf(a.block) - blockOrder.indexOf(b.block)
    return d !== 0 ? d : a.path.localeCompare(b.path)
  })
}

/* ===== 对比对选取（版本对比 Tab 拿哪两个版本比）===== */

/** 参与选取的历史版本条目（只需这几个字段） */
export interface DiffCandidateVersion {
  versionId?: number
  versionCode: string
  status: RuleStatus
  /** 是否为「最新已发布」版本（不是「当前有效」——后者按生效期窗口判定） */
  isLatestPublished: boolean
}

/** 选出来的对比对 */
export interface DiffPair {
  source: DiffCandidateVersion
  target: DiffCandidateVersion
}

/**
 * 选对比对
 *
 * 规则（2026-10-28 页面层实测后修订）：
 *  ① **有草稿** ⇒ 比「当前已发布 ↔ 草稿」——回答「我这次改了什么」
 *  ② **无草稿**（草稿已发布掉）⇒ 比「上一个已发布 ↔ 当前已发布」——回答「上一版到这一版改了什么」
 *
 * ⚠️ 旧实现只认 ①，且 `target` 必须是 DRAFT ⇒ 草稿一发布，版本对比 Tab 就永久空白
 *   （后端 diff 端点本身支持任意两版本，纯前端选取口径问题）。
 */
export function pickDiffPair(history: readonly DiffCandidateVersion[]): DiffPair | null {
  const draft = history.find((h) => h.status === 'DRAFT')
  const published = history
    .filter((h) => h.status === 'PUBLISHED')
    .sort((a, b) => (a.versionId ?? 0) - (b.versionId ?? 0))
  if (!published.length) return null
  const current = published.find((h) => h.isLatestPublished) ?? published[published.length - 1]
  if (draft) return { source: current, target: draft }
  const idx = published.findIndex((h) => h.versionId === current.versionId)
  const prev = idx > 0 ? published[idx - 1] : null
  if (!prev) return null
  return { source: prev, target: current }
}
