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
 *
 * 2026-11-19：新增**跨厂LT 标准参数**两个错误码的中文翻译
 *   （依据：用户指示「保存草稿不拦、校验/发布才拦」；后端 `GovernanceVersionService.cs:1538-1570`）：
 *   - `CROSS_FACTORY_LT_BLOCK_EMPTY`（键存在但零行）⇒ 提示「别把行删光，关掉生效即可」；
 *   - `INVALID_CROSS_FACTORY_LT`（逐条校验不过）⇒ 把 `FromFactoryCode` 等字段名翻成中文。
 *   ⚠️ 这两个码**只在「校验 / 发布」期出现**（保存期后端 PUT 透传不拦，V6 裁定），
 *     文案一律写成「怎么改」，不写成「保存失败」。
 *
 * 2026-11-19（第二封）：3号位《跨厂LT零行保存期不拦认可_键删不掉裁甲维持现状_回执_v1.0_20261119》
 *   —— **前端零行为改动**，该函 §一 认可本文件这两个错误码的中文翻译；口径钉死如下：
 *   - 这两个码**只在「校验 / 发布」期出现**（保存期后端 PUT 透传不拦）⇒ 文案一律写「怎么改」，
 *     **不写「保存失败」**。
 *   - 「键删不掉」是**既定契约形态**（该函 §二 裁甲维持现状、§二.3 乙不采）
 *     ⇒ 本层**不得**新增「删除这一块 / 清空该块」类译文或提示。
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
 * 跨厂LT 标准参数（顶层 `$.CrossFactoryLeadTimeRules`）的两个错误码
 *
 * 后端 `GovernanceVersionService.cs:1538-1570`（校验期 / 发布期同一套）：
 *  - `CROSS_FACTORY_LT_BLOCK_EMPTY`：键存在但零行 `[]` = 配置形态非法（缺块反而合法）；
 *  - `INVALID_CROSS_FACTORY_LT`：逐条校验不过（工厂没填 / 同厂 / 小时数为负 / 重复键）。
 *
 * ⚠️ 2026-11-19：这两条**只在「校验 / 发布」期出现**（保存期后端透传不拦），
 *   所以文案要写成「怎么改」而不是「你保存失败了」。
 */
const CROSS_FACTORY_LT_EMPTY_CODE = 'CROSS_FACTORY_LT_BLOCK_EMPTY'
const CROSS_FACTORY_LT_INVALID_CODE = 'INVALID_CROSS_FACTORY_LT'

/** 后端原文：`CrossFactoryLeadTimeRules[FROM→TO]: ...` */
const LT_RULE_HEAD_RE = /CrossFactoryLeadTimeRules\[([^\]]*)\]/
/** 后端原文：`CrossFactoryLeadTimeRules 重复键：... (From=A, To=B, ProductFamily=(全族))` */
const LT_DUP_KEY_RE = /重复键：[^(]*\(From=([^,]*),\s*To=([^,]*),\s*ProductFamily=([^)]*)\)/

/**
 * 把后端的跨厂LT 逐条错误翻成大白话
 *
 * 后端原文形如：
 *   `CrossFactoryLeadTimeRules[A→B]: FromFactoryCode 不能为空`
 *   `CrossFactoryLeadTimeRules[A→B]: FromFactoryCode 必须 ≠ ToFactoryCode（跨厂LT 是厂到厂，非同厂）`
 *   `CrossFactoryLeadTimeRules[A→B]: CrossFactoryLeadTimeHours 不能为负（当前：-1）`
 *   `CrossFactoryLeadTimeRules 重复键：同一有效参数版本内 (From=A, To=B, ProductFamily=(全族)) 最多一条有效规则`
 */
function translateCrossFactoryLtMessage(raw: string): string {
  const dup = LT_DUP_KEY_RE.exec(raw)
  if (dup) {
    const family = dup[3].trim()
    const familyText = family && family !== '(全族)' ? `（产品族 ${family}）` : ''
    return `跨厂LT 标准参数：${dup[1].trim()} → ${dup[2].trim()}${familyText} 有两条生效规则 —— 同一个工厂对 + 产品族只能有一条生效的`
  }
  const head = LT_RULE_HEAD_RE.exec(raw)
  const prefix = head ? `跨厂LT 标准参数（${head[1]}）：` : '跨厂LT 标准参数：'
  const body = raw
    .replace(/^CrossFactoryLeadTimeRules(\[[^\]]*\])?:\s*/, '')
    .replace(/FromFactoryCode 必须 ≠ ToFactoryCode（[^）]*）?/, '起始工厂和到达工厂不能是同一个厂')
    .replace(/FromFactoryCode 不能为空/g, '起始工厂没填')
    .replace(/ToFactoryCode 不能为空/g, '到达工厂没填')
    .replace(/CrossFactoryLeadTimeHours 不能为负/g, '跨厂提前期小时数不能为负')
  return prefix + body
}

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

  // 跨厂LT 标准参数：键存在但零行（配置形态非法）⇒ 说清「怎么改」
  if (code === CROSS_FACTORY_LT_EMPTY_CODE) {
    return '跨厂LT 标准参数这一块一行都没有，这个形态不合法（校验和发布都会被拦）：要停用某条规则请把那一行的「生效」关掉（别把行删光）。'
  }

  // 跨厂LT 标准参数：逐条校验不过 ⇒ 把字段名翻成中文
  if (code === CROSS_FACTORY_LT_INVALID_CODE) {
    return translateCrossFactoryLtMessage(raw)
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
