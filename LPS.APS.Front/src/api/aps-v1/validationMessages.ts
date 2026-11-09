/**
 * APS V1 4号位 — 发布校验错误文案的中文映射层
 *
 * 依据（3号位 2026-10-25《两处枚举错误文案中文映射与包装 · 映射交付回执 v1.0》）：
 *  - 后端**不改 message**（回执 §〇），前端在渲染处包一层翻译（回执 §四）。
 *  - 单一真源 = 后端枚举定义（回执 §一 映射总表）；**中文显示词以本仓 `parameterSetEnums.ts` 为准**
 *    （回执 §三 明写「请勿因本表覆盖你方 `parameterSetEnums.ts` 的显示词」）。
 *  - 判别式 = message 内的**字段名前缀**（回执 §二 注意事项 1：G1 是一个错误码两个字段，不能只按 code 映射）；
 *    取值 = message 里的「当前值：N」（回执 §二 注意事项 2）。
 *
 * ⚠️ 为什么按数值而不是按英文名映射：后端 message 里 `SolverStrategyMode` 的英文名列表顺序是
 *   `Auto/Forward/Backward/Mixed`，与枚举**序号**顺序（Forward=0 / Backward=1 / Mixed=2 / Auto=3）
 *   相反（`SolverStrategyValidator.cs:51`）⇒ 按名字出现顺序映射会串号。
 *
 * ⚠️ 越界值（如 BottleneckMode=5）不在任何下拉项里 ⇒ 译文只列**合法值**，当前值原样保留为数字。
 */

import { lookupParameterEnum } from './parameterSetEnums'
import type { BlockKey } from './types/rule'

/** 枚举类校验错误 → （message 判别词 → 本仓下拉项） */
interface EnumErrorHint {
  /** message 内必含的字段名前缀（判别用） */
  marker: string
  /** 本仓参数块（`parameterSetEnums.ts` 的块键） */
  block: BlockKey
  /** 块内点分路径 */
  path: readonly string[]
  /** 中文句式里的主语 */
  subject: string
}

/**
 * 判别表。
 * ⚠️ 顺序有意义：`DynamicBottleneckMode` 与 `SolverStrategyMode` 都包在 `INVALID_SOLVER_STRATEGY` 里，
 *   必须靠 marker 区分（回执 §二 注意事项 1）。两个 marker 互不为子串，顺序不影响结果。
 */
const ENUM_ERROR_HINTS: readonly EnumErrorHint[] = [
  {
    marker: 'DynamicBottleneckMode',
    block: 'solverStrategy',
    path: ['BottleneckMode'],
    subject: '瓶颈锚点方式'
  },
  {
    marker: 'SolverStrategyMode',
    block: 'solverStrategy',
    path: ['Mode'],
    subject: '排产方向'
  },
  {
    marker: 'Supply.PiSort.SortBy',
    block: 'supply',
    path: ['PiSort', 'SortBy'],
    subject: 'PI 排序方式'
  }
]

/** 需要做中文翻译的错误码 */
const ENUM_ERROR_CODES: readonly string[] = ['INVALID_SOLVER_STRATEGY', 'INVALID_SUPPLY_PI_SORT']

/**
 * 仓库引用错误码（0号位 裁决5 / 后端 `GovernanceVersionService.cs:1085`）
 *
 * 后端原文：`参数配置引用了不存在的仓库编码：{code}（权威仓库事实源：ext_MES_ProcessCode_View 仓库字典）`
 * ⇒ 前端译成「改哪、怎么改」的提示：内部视图名（ext_MES_ProcessCode_View）不暴露给业务用户。
 */
const WAREHOUSE_REF_CODE = 'INVALID_WAREHOUSE_REF'

/** 从后端 message 里取仓库编码（`不存在的仓库编码：` 之后、全角括号之前） */
const WAREHOUSE_REF_CODE_RE = /不存在的仓库编码：([^（(]+)/

/** message 里的当前值：`（当前值：5）` */
const CURRENT_VALUE_RE = /（当前值：(-?\d+)）/

/**
 * 把一条发布校验错误翻译成中文。
 *
 * 非枚举类错误（以及找不到对应下拉项的）**原样返回**后端 message —— 不猜、不改写。
 */
export function translateValidationError(code?: string | null, message?: string | null): string {
  const raw = message ?? ''
  if (!raw || !code) return raw

  // 仓库引用错误：单独一支（不是枚举错误码）
  if (code === WAREHOUSE_REF_CODE) {
    const hit = WAREHOUSE_REF_CODE_RE.exec(raw)?.[1]?.trim()
    return hit
      ? `仓库编码在仓库字典里不存在：${hit}（请改成字典里的真实仓库编码）`
      : '参数里引用了仓库字典里不存在的仓库编码（请改成字典里的真实仓库编码）'
  }

  if (!ENUM_ERROR_CODES.includes(code)) return raw

  const hint = ENUM_ERROR_HINTS.find((h) => raw.includes(h.marker))
  if (!hint) return raw

  const options = lookupParameterEnum(hint.block, hint.path)
  if (!options || options.length === 0) return raw

  const labels = options.map((o) => o.label).join(' / ')
  const current = CURRENT_VALUE_RE.exec(raw)?.[1]
  return current
    ? `${hint.subject}必须是「${labels}」之一（当前值：${current}）`
    : `${hint.subject}必须是「${labels}」之一`
}

/**
 * 非阻断提示：选了非自动锚点方式、却没填锚点资源编码 ⇒ 运行时按「自动判定」处理。
 *
 * 依据（3号位 2026-10-25 回执 §五，后端零改动、提示走前端）：
 *  - 运行期事实：`PhaseThreeDiagnostics.cs:137-170` —— 锚点 id 解析不出时，
 *    ForceAnchor / PreferAnchor / NotAnchor 三个分支都不执行 ≡ Auto。
 *  - 判定所需两个值同在 SolverStrategy 块内 ⇒ 前端自判，无需后端。
 *  - **不拦截、不阻断发布**（3号位 §五 定调）—— 本函数只产文案，不抛错、不改值。
 *
 * @param solverStrategy SolverStrategy 块对象（后端真值 / 前端编辑态皆可）
 * @returns 提示文案；不满足条件时返回 null
 */
export function anchorHintMessage(solverStrategy: unknown): string | null {
  if (!solverStrategy || typeof solverStrategy !== 'object') return null
  const ss = solverStrategy as Record<string, unknown>
  const mode = Number(ss.BottleneckMode ?? 0)
  if (!Number.isFinite(mode) || mode === 0) return null
  const code = typeof ss.AnchorResourceCode === 'string' ? ss.AnchorResourceCode.trim() : ''
  if (code) return null
  return '已指定非自动锚点方式，但没填锚点资源编码 —— 运行时按「自动判定」处理。'
}
