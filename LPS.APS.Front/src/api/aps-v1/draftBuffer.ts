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
import {
  findBlockField,
  isInertBlockField,
  isHiddenBlockField,
  isReadonlyBlockField
} from './parameterBlockFields'

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
  type: 'NUMBER' | 'BOOLEAN' | 'STRING' | 'PERCENT' | 'DURATION' | 'TEXT_LIST'
  /** 中文标签（来自 `parameterBlockFields` 字段目录；目录外老键为 undefined） */
  label?: string
  /** 一句话业务说明（字段目录口径；表格里做参数名悬停提示） */
  hint?: string
  /** 内容层枚举可选项（有值时渲染下拉；value 是写回 JSON 的数字序号） */
  options?: readonly ParameterEnumOption[]
  /** 数值边界（后端发布校验口径；有值时 ElInputNumber 夹住） */
  min?: number
  max?: number
  /** 数字步长（字段目录口径；缺省按 1） */
  step?: number
  /** 运行期无消费者（3号位 2026-10-26 核证口径）⇒ 行上标「暂未生效」 */
  inert?: boolean
  /** 维护面隐藏（C/E/F/H 类）⇒ 页面不渲染；**值仍在快照里，保存原样回写** */
  hidden?: boolean
  /** D 类 V1 固定 ⇒ 只读展示（editable 同时为 false） */
  fixed?: boolean
  /** 启发式推断是否敏感 */
  sensitive: boolean
  /** 启发式推断是否可编辑 */
  editable: boolean
}

/* ===== 算法 1: flattenBlocks — 5 块展开成行 ===== */

/**
 * 把 5 主题 JSON 块展开成扁平参数行数组
 *  - 嵌套对象递归展开（path-based）
 *  - 数组默认整体只读（不展开内容，避免误改）；**例外**：字段目录声明为
 *    `TEXT_LIST` 且确是字符串数组时，保留原数组并标 `TEXT_LIST`（表格里按「每行一项」编辑）
 *  - 顺带带上字段目录里的中文标签 / 步长 / 边界 ⇒ 表格不必再自己查目录
 *  - 空对象/空数组 → 返回空数组
 */
export function flattenBlocks(blocks: ParameterSetBlocks): ParameterRow[] {
  const rows: ParameterRow[] = []
  for (const block of Object.keys(blocks) as BlockKey[]) {
    const obj = (blocks[block] ?? {}) as JsonObject
    walkObject(obj, [], (path, value) => {
      const keyPath = path.join('.')
      const def = findBlockField(block, keyPath)
      // 维护责任分类只影响维护面：隐藏行不渲染，D 类只读；载荷始终原样回写。
      const hidden = isHiddenBlockField(block, keyPath)
      const fixed = isReadonlyBlockField(block, keyPath)
      // 数组：只有字段目录声明为「文本清单」且确是字符串数组时才可编辑，
      //   其余数组（对象数组等）保持只读一行 JSON —— 通用单值控件表达不了。
      if (Array.isArray(value)) {
        const isTextList = def?.kind === 'TEXT_LIST' && value.every((v) => typeof v === 'string')
        rows.push({
          block,
          path,
          value: isTextList ? value : JSON.stringify(value),
          type: isTextList ? 'TEXT_LIST' : 'STRING',
          label: def?.label,
          hint: def?.hint,
          inert: isInertBlockField(block, keyPath),
          hidden,
          fixed,
          sensitive: isSensitive(keyPath),
          editable: !hidden && !fixed && isTextList && isEditable(keyPath)
        })
        return
      }
      // 边界：发布校验口径（parameterSetEnums）优先，字段目录补其余字段的 min/max/step
      const limits = lookupParameterLimits(block, path) ?? { min: def?.min, max: def?.max }
      rows.push({
        block,
        path,
        value,
        type: inferType(path[path.length - 1] ?? '', value),
        label: def?.label,
        hint: def?.hint,
        options: lookupParameterEnum(block, path),
        min: limits.min,
        max: limits.max,
        step: def?.step,
        inert: isInertBlockField(block, keyPath),
        hidden,
        fixed,
        sensitive: isSensitive(keyPath),
        editable: !hidden && !fixed && isEditable(keyPath)
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

/* ===== 算法 2b: removePath — 按 path 删除字段 ===== */

/**
 * 按 path 删除 blocks 中指定字段（把键从块 JSON 里拿掉）
 *  - **语义 = 回到后端 DTO 默认值**：键不在 JSON 里 ⇒ 后端反序列化时取 DTO 属性默认值
 *    （不是「置空」，也不是「这条配置失效」）
 *  - 删完父对象若空了 ⇒ 父对象一并删掉（不留 `{"Trigger":{}}` 这种空壳）
 *  - 块根删空 ⇒ 块保持 `{}`；`buildPutBody` 会把空块发成 `undefined` ⇒ 该块不进快照
 *    ⇒ **仍被 `EMPTY_*` 拦**（保住 3号位「没配 = 空块 = 拦发布」的不变量）
 *  - path 为空 / 中间节点不是对象 / 叶子键不存在 → 抛错（不做静默兜底）
 */
export function removePath(blocks: ParameterSetBlocks, block: BlockKey, path: string[]): void {
  if (path.length === 0) {
    throw new Error('[removePath] invalid path: empty')
  }
  const root = blocks[block] as JsonObject
  // 沿 path 钻到末级父节点，同时记下每一级（供删完后剪空壳）
  const chain: Array<{ container: JsonObject; key: string }> = []
  let target: JsonObject = root
  for (let i = 0; i < path.length - 1; i++) {
    const key = path[i]
    const next = target[key]
    if (next === undefined || next === null || typeof next !== 'object' || Array.isArray(next)) {
      throw new Error(
        `[removePath] intermediate node is not an object at "${path.slice(0, i + 1).join('.')}"`
      )
    }
    chain.push({ container: target, key })
    target = next as JsonObject
  }
  const lastKey = path[path.length - 1]
  if (!Object.prototype.hasOwnProperty.call(target, lastKey)) {
    throw new Error(`[removePath] key not found: ${path.join('.')}`)
  }
  delete target[lastKey]
  // 自底向上剪掉变空的父对象（块根保留：块根空了就是空块）
  for (let i = chain.length - 1; i >= 0; i--) {
    const { container, key } = chain[i]
    const child = container[key] as JsonObject
    if (Object.keys(child).length === 0) {
      delete container[key]
    } else {
      break
    }
  }
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
