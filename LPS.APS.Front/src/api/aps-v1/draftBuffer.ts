/**
 * APS V1 4号位 — ParameterSet / RuleSet 草稿缓冲算法（B 设计稿 §2）
 *
 * 设计原则：
 *  - pure functions（无副作用、不接触 store），便于单测（§2.3 US1-US16）
 *  - dirty / workingBuffer 在 store 层处理（§2.4 store action 包装）
 *  - 5 主题 JSON 块（lock / supply / procurement / solverStrategy / candidateGuardrail）
 *
 * 用法：
 *   import { flattenBlocks, applyEdit, buildPutBody } from '@/api/aps-v1/draftBuffer'
 *
 * 历史：v1.4 §二十 新增；B 设计稿 2026-09-21 落定
 */

import type {
  BlockKey,
  GovernanceStatus,
  ParameterSetBlocks,
  GovernanceVersionBase
} from './types/rule'
import { lookupBinding, hasBinding } from './parameterSetBindings'
import {
  lookupParameterEnum,
  lookupParameterLimits,
  type ParameterEnumOption
} from './parameterSetEnums'

/* ===== 类型 ===== */

export type JsonValue = string | number | boolean | null | JsonObject | JsonArray
export interface JsonObject {
  [key: string]: JsonValue
}
export type JsonArray = JsonValue[]

/** 扁平化参数行（表格渲染用） */
export interface ParameterRow {
  /** 5 块之一 */
  block: BlockKey
  /** 路径（如 ['defaultPurchaseLT'] 或 ['policies', 'priority', 'segments']） */
  path: string[]
  /** 当前值（primitive） */
  value: JsonValue
  /** 启发式推断的类型（用于 ElInput/ElSwitch/ElSelect 渲染） */
  type: 'NUMBER' | 'BOOLEAN' | 'STRING' | 'PERCENT' | 'DURATION'
  /** 内容层枚举可选项（有值时渲染下拉；value 是写回 JSON 的数字序号） */
  options?: readonly ParameterEnumOption[]
  /** 数值边界（后端发布校验口径；有值时 ElInputNumber 夹住） */
  min?: number
  max?: number
  /** 启发式推断是否敏感 */
  sensitive: boolean
  /** 启发式推断是否可编辑 */
  editable: boolean
}

/* ===== 算法 1: flattenBlocks — 5 块展开成行 ===== */

/**
 * 把 5 主题 JSON 块展开成扁平参数行数组
 *  - 嵌套对象递归展开（path-based）
 *  - 数组类型标记为只读（不展开内容，避免误改）
 *  - 空对象/空数组 → 返回空数组
 */
export function flattenBlocks(blocks: ParameterSetBlocks): ParameterRow[] {
  const rows: ParameterRow[] = []
  for (const block of Object.keys(blocks) as BlockKey[]) {
    const obj = (blocks[block] ?? {}) as JsonObject
    walkObject(obj, [], (path, value) => {
      // 数组只读：标记一整行（用户可在 UI 看到，但不可编辑）
      if (Array.isArray(value)) {
        rows.push({
          block,
          path,
          value: JSON.stringify(value),
          type: 'STRING',
          sensitive: isSensitive(path.join('.')),
          editable: false
        })
        return
      }
      // primitive 行
      const limits = lookupParameterLimits(block, path)
      rows.push({
        block,
        path,
        value,
        type: inferType(path[path.length - 1] ?? '', value),
        options: lookupParameterEnum(block, path),
        min: limits?.min,
        max: limits?.max,
        sensitive: isSensitive(path.join('.')),
        editable: isEditable(path.join('.'))
      })
    })
  }
  return rows
}

function walkObject(
  obj: JsonObject,
  prefix: string[],
  onLeaf: (path: string[], value: JsonValue) => void
): void {
  for (const key of Object.keys(obj)) {
    const value = obj[key]
    const path = [...prefix, key]
    if (value !== null && typeof value === 'object' && !Array.isArray(value)) {
      walkObject(value as JsonObject, path, onLeaf)
    } else {
      onLeaf(path, value as JsonValue)
    }
  }
}

/* ===== 算法 2: applyEdit — path-based local mutation ===== */

/**
 * 按 path 修改 blocks 中指定字段
 *  - 中间节点缺失（undefined）→ 自动创建空对象（B 设计稿 §2.1 Issue 7 闭环）
 *  - 中间节点是 primitive（null/boolean/number/string）或 array → 抛错
 *    （**修复 Issue 7**：原版 `target[key] = {}` 无脑覆盖 primitive 中间节点，
 *    会丢值如 `policies: 5` 被改 `policies: {}`）
 *  - row.editable=false → 抛错
 *  - row.path.length===0 → 抛错
 *  - 注：调用方（store action）需在调用前确保 row.editable
 */
export function applyEdit(
  blocks: ParameterSetBlocks,
  row: ParameterRow,
  newValue: JsonValue
): void {
  if (row.path.length === 0) {
    throw new Error('[applyEdit] invalid row.path: empty')
  }
  if (!row.editable) {
    throw new Error(`[applyEdit] row not editable: ${row.path.join('.')}`)
  }
  // 枚举行只接受后端枚举序号（写字符串会让后端反序列化判 INVALID_JSON）
  if (row.options && !row.options.some((o) => o.value === newValue)) {
    throw new Error(
      `[applyEdit] value not in enum options: ${row.path.join('.')}=${String(newValue)}`
    )
  }
  // 沿 path 钻到末级父节点
  let target: JsonObject = blocks[row.block] as JsonObject
  for (let i = 0; i < row.path.length - 1; i++) {
    const key = row.path[i]
    const next = target[key]
    if (next === undefined) {
      // 中间节点缺失 → 创建空对象（合法）
      target[key] = {}
    } else if (next === null || typeof next !== 'object' || Array.isArray(next)) {
      // primitive/array 中间节点 → 抛错（Issue 7：禁止覆盖现有数据）
      throw new Error(
        `[applyEdit] intermediate node is not an object at "${row.path.slice(0, i + 1).join('.')}" ` +
          `(value type: ${Array.isArray(next) ? 'array' : typeof next})`
      )
    }
    target = target[key] as JsonObject
  }
  const lastKey = row.path[row.path.length - 1]
  target[lastKey] = newValue
}

/* ===== 算法 3: buildPutBody — full-object PUT（显式 DRAFT） ===== */

/**
 * 构造 PUT 请求体
 *  - 治理字段：spread governance（不变）
 *  - 显式置 status='DRAFT'（B 设计稿 §2.1 Round 1：代码自证 + 防后端改动连锁）
 *  - 5 JSON 字段：序列化 workingBlocks 各块（空块 → undefined 不报错）
 *  - contentSnapshotJson：**不主动删、不主动改**。10-15 Q2 落地后后端
 *    `UpdateRuleSetVersionAsync` / `UpdateParameterSetVersionAsync` 已把这列纳入既有回填清单
 *    （`version.ContentSnapshotJson = existing.ContentSnapshotJson;`），请求体带不带都安全；
 *    而 ⑦ 换型规则子块（SetupTransitionRules）**只存在这一列里**，若前端把它删掉又碰上后端
 *    回退到「读路径置空 + 写路径全量采纳」的旧实现，用户保存一次就会清空 DRAFT 上的全部换型规则。
 *    所以这里保持 spread 原样回传，是当前唯一正确的行为。
 *  - 泛型：G 继承 GovernanceVersionBase（RuleSetVersionGovernance / ParameterSetVersionGovernance 各自适用）
 */
export function buildPutBody<G extends GovernanceVersionBase>(
  governance: Readonly<G>,
  blocks: ParameterSetBlocks
): G & {
  lockJson?: string
  supplyJson?: string
  procurementJson?: string
  solverStrategyJson?: string
  candidateGuardrailJson?: string
} {
  return {
    ...governance,
    // 显式置 DRAFT（不依赖后端 service 入参忽略）
    status: 'DRAFT' as GovernanceStatus,
    // 5 JSON 字段：可能为空（governance 原始 undefined 也维持 undefined 不报错）
    lockJson:
      blocks.lock && Object.keys(blocks.lock).length > 0 ? JSON.stringify(blocks.lock) : undefined,
    supplyJson:
      blocks.supply && Object.keys(blocks.supply).length > 0
        ? JSON.stringify(blocks.supply)
        : undefined,
    procurementJson:
      blocks.procurement && Object.keys(blocks.procurement).length > 0
        ? JSON.stringify(blocks.procurement)
        : undefined,
    solverStrategyJson:
      blocks.solverStrategy && Object.keys(blocks.solverStrategy).length > 0
        ? JSON.stringify(blocks.solverStrategy)
        : undefined,
    candidateGuardrailJson:
      blocks.candidateGuardrail && Object.keys(blocks.candidateGuardrail).length > 0
        ? JSON.stringify(blocks.candidateGuardrail)
        : undefined
  }
}

/* ===== Heuristic（§2.2）：sensitive / editable / type 推断 ===== */

/**
 * 启发式推断是否敏感字段
 *  - 优先用真实 binding（parameterSetBindings.ts 注册）
 *  - fallback：key 含 buffer / secret / confidential
 *  - 联调时（Step D）用真实后端 schema 覆盖
 */
export function isSensitive(keyPath: string): boolean {
  if (hasBinding(keyPath)) {
    const b = lookupBinding(keyPath)
    if (b?.sensitive !== undefined) return b.sensitive
  }
  const s = keyPath.toLowerCase()
  return s.includes('buffer') || s.includes('secret') || s.includes('confidential')
}

/**
 * 推断是否可编辑
 *  - 优先用真实 binding（有 schema 时才可信）
 *  - 无 binding 时**一律可编辑** —— 后端 PUT 是 full-object，五主题不改语义
 *
 * ⚠️ 2026-10-22 更正：原 fallback 把 `default*` 开头的键判为「出厂锁定不可编辑」，
 *   该规则是前端自造的（冻结文档无「只读参数」概念，grep 无命中）⇒ 已撤，不再按名字猜。
 */
export function isEditable(keyPath: string): boolean {
  if (hasBinding(keyPath)) {
    const b = lookupBinding(keyPath)
    if (b?.editable !== undefined) return b.editable
  }
  return true
}

/**
 * 推断参数类型（决定渲染什么控件）
 *  - 优先用真实 binding（有 schema 时才可信）
 *  - 无 binding 时**按值本身的 JS 类型**推断，不按键名猜
 *
 * ⚠️ 2026-10-22 更正：原 fallback 按键名猜 PERCENT / DURATION
 *   （`*threshold*`→PERCENT）—— `RemainingTimeThresholdHours: 24` 是**小时**不是百分比，
 *   猜错会把控件标成百分比 ⇒ 已改为纯值驱动。PERCENT 只可能来自登录过的 binding。
 */
export function inferType(
  key: string,
  value: JsonValue
): 'NUMBER' | 'BOOLEAN' | 'STRING' | 'PERCENT' | 'DURATION' {
  if (hasBinding(key)) {
    const b = lookupBinding(key)
    if (b?.type) return b.type
  }
  if (typeof value === 'boolean') return 'BOOLEAN'
  if (typeof value === 'string') return 'STRING'
  if (typeof value === 'number') return 'NUMBER'
  return 'STRING'
}

/* ===== 工具：从 JSON 字符串解析 5 块 ===== */

/**
 * 安全解析 JSON 字符串（用于 GET 返回的 lockJson 等）
 *  - 空 / 解析失败 → 返回空对象
 */
export function parseJsonSafe(json: string | undefined | null): JsonObject {
  if (!json) return {}
  try {
    const parsed = JSON.parse(json)
    if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
      return parsed as JsonObject
    }
  } catch {
    // 解析失败兜底
  }
  return {}
}

/** 5 块 JSON 字符串 → 解析后的对象（用于 store buffer 初始化；后端列可空，接受 null） */
export function parseParameterSetBlocks(input: {
  lockJson?: string | null
  supplyJson?: string | null
  procurementJson?: string | null
  solverStrategyJson?: string | null
  candidateGuardrailJson?: string | null
}): ParameterSetBlocks {
  return {
    lock: parseJsonSafe(input.lockJson),
    supply: parseJsonSafe(input.supplyJson),
    procurement: parseJsonSafe(input.procurementJson),
    solverStrategy: parseJsonSafe(input.solverStrategyJson),
    candidateGuardrail: parseJsonSafe(input.candidateGuardrailJson)
  }
}
