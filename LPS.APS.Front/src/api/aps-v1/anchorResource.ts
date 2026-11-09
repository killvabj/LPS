/**
 * APS V1 4号位 — 锚点资源编码（AnchorResourceCode）录入落点
 *
 * 依据（3号位 2026-10-25《锚点资源编码补录入自由文本 · 数据侧定调回执 v1.0》）：
 *  - 数据模型字段**早已存在**（`FrozenStrategySnapshot.cs:269`），本件**零 DDL、零后端代码改动**
 *  - 后端对「无键 / 空串 / null / 有值」四态全部接受（回执 §二 尾注）⇒ 落点在**前端写侧带键**
 *  - 页面给一个**自由文本**录入项（回执 §一 采纳方案 (a)）；下拉候选清单后置到 5号位 scope 谓词到位
 *  - 运行期事实（回执 §三）：`PhaseOneConstraintBuilder.cs:1220` 建 Code→id 反查表，
 *    `PhaseThreeDiagnostics.cs:137-138` 用 `TryGetValue(AnchorResourceCode, out anchorId)` 反查；
 *    编码无效 ⇒ anchorResourceId 为 null ⇒ 三个非 Auto 分支 no-op ≡ Auto（fail-closed，不崩不误）
 */

import type { BlockKey, ParameterSetBlocks } from './types/rule'

/** 块内键名（与后端 DTO 属性名逐字一致；后端 `PropertyNameCaseInsensitive = true`） */
export const ANCHOR_RESOURCE_CODE_KEY = 'AnchorResourceCode'

/**
 * 通用参数表里要剔除的行 —— 锚点编码有**专属录入项**（中文标签 + 说明），
 * 留在通用表里会变成一行裸英文键名，且与专属输入框显示同一份数据。
 */
export function isAnchorResourceCodeRow(block: BlockKey, path: readonly string[]): boolean {
  return block === 'solverStrategy' && path.length === 1 && path[0] === ANCHOR_RESOURCE_CODE_KEY
}

/** 读锚点资源编码（缺键 / null / 非字符串 ⇒ 空串；字符串按 trim 读） */
export function readAnchorResourceCode(solverStrategy: unknown): string {
  if (!solverStrategy || typeof solverStrategy !== 'object') return ''
  const ss = solverStrategy as Record<string, unknown>
  const raw = ss[ANCHOR_RESOURCE_CODE_KEY]
  return typeof raw === 'string' ? raw.trim() : ''
}

/**
 * 写锚点资源编码（trim 后写回）。
 * trim 是必要的：运行期反查是**精确匹配**（`ResourceIdsByCode.TryGetValue`），
 * 首尾空格会让编码查不到 ⇒ 静默退化成 Auto，而用户以为自己配了锚点。
 */
export function writeAnchorResourceCode(
  solverStrategy: Record<string, unknown>,
  code: string
): void {
  solverStrategy[ANCHOR_RESOURCE_CODE_KEY] = String(code ?? '').trim()
}

/**
 * 补键：让「求解器策略」块恒带 `AnchorResourceCode` 键（无值则空串）。
 *
 * ⚠️ **只在块非空时补**：
 *   空块保持空 ⇒ 不会被写进 `solverStrategyJson` ⇒ 原本被后端 `EMPTY_SOLVER_STRATEGY`
 *   （`GovernanceVersionService.cs:1095-1104`）拦住的「没配求解器策略」参数集仍然发不出去。
 *   若对空块也补键，块就变成 `{"AnchorResourceCode":""}` ⇒ 后端反序列化成一份带默认值的
 *   SolverStrategyBlock（Mode/BottleneckMode 取枚举 0 号）⇒ **前端擅自把「没配」变成了「按默认配」**，
 *   等于替用户改了发布语义。
 *
 * ⚠️ 已有键不覆盖（含值为 null 的情况）：后端四态都接受，用户存过的值不能被前端抹平。
 */
export function ensureAnchorResourceCodeKey(blocks: ParameterSetBlocks): void {
  const ss = blocks?.solverStrategy
  if (!ss || typeof ss !== 'object') return
  if (Object.keys(ss).length === 0) return
  if (ANCHOR_RESOURCE_CODE_KEY in ss) return
  ss[ANCHOR_RESOURCE_CODE_KEY] = ''
}

/**
 * 块里**只剩锚点编码的空值**时，把这个键摘掉 ⇒ 块回到真空 ⇒ PUT 不带该块 ⇒ 仍被 `EMPTY_*` 拦。
 *
 * 场景：用户把求解器策略块里的字段逐个删光，但读路径补出来的 `AnchorResourceCode: ""` 还留着
 *   ⇒ 块非空 ⇒ 后端反序列化成一份**全默认**的 SolverStrategyBlock
 *   ⇒ 绕过 `EMPTY_SOLVER_STRATEGY` ⇒ **前端擅自把「没配」变成了「按默认配」**
 *   （3号位 2026-10-25 §C-AN-3 明确不许）。
 *
 * 边界：锚点编码有**真实值**时保留（用户确实配了锚点，块非空是合理的）。
 */
export function pruneAnchorOnlyBlock(blocks: ParameterSetBlocks, block: BlockKey): void {
  if (block !== 'solverStrategy') return
  const ss = blocks?.solverStrategy
  if (!ss || typeof ss !== 'object') return
  const keys = Object.keys(ss)
  if (keys.length !== 1 || keys[0] !== ANCHOR_RESOURCE_CODE_KEY) return
  const v = ss[ANCHOR_RESOURCE_CODE_KEY]
  if (v !== '' && v !== null) return
  delete ss[ANCHOR_RESOURCE_CODE_KEY]
}
