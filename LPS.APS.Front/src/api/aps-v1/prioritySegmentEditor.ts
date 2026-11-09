/**
 * APS V1 4号位 — 需求优先级段位**编辑器**（表单 ⇄ demandPriorityJson）
 *
 * 与 `prioritySegment.ts` 分工：
 *  - `prioritySegment.ts`：展示用字典（含 17 个 DemandField / 8 个操作符），**只读解析**
 *  - 本文件：编辑用的**发布白名单** + 无损序列化 + 前端拦截
 *
 * 为什么编辑器的枚举必须比展示字典窄（不是遗漏，是硬约束）：
 *  - `DemandPriorityValidator.cs:29-50` 规则集字段白名单共 **12 个**（0号位 2026-11-07 裁决：
 *    `DelayStatus`(1) 与 `DemandMaturityStatus`(12) 归参数集、已剔除）；
 *    其中 `RemainingTimeHours / IsPmcProtected / PriorityLevel` 运行侧执行器无实现
 *    ⇒ 选了必然发布失败，编辑器不开放（0/4/5）。
 *  - 段位编辑器因此开放 **12 个**字段；延期状态(1)、需求成熟度(12) 与 0/4/5 一并关闭
 *    （3号位 2026-11-07 Q1~Q4 回执 §二）。
 *  - `出荷指示入物流日`（9，`CustomerDueDate`）的范围条件支持**相对日阈值**：
 *    值填纯整数 N ⇒ 后端投影层在 Run 冻结时换算成 `FrozenAt.Date + N`
 *    （`DemandPriorityProjector.cs` `IsRelativeDateThreshold` / `ConvertRelativeDateThreshold`）；
 *    填 `YYYY-MM-DD` ⇒ 原样按具体日期比较。编辑器两种都收，控件是「相对天数 / 具体日期」双模式
 *    （见 `supportsRelativeOffset`）。
 *  - 同文件 `:101-110` 放行 7 个操作符，**没有 NotEquals**（执行器无 NE 分支）；
 *    `Contains` 仅为物料编码提供，且实际语义是**前缀匹配**（`StartsWith`，`FINAL*`≡`FINAL`）。
 *  - 字段/操作符的**合理组合**再收一层（见 `operatorsForField`）：
 *      文本型（客户等级/订单类型/各类枚举）：执行器按 OrdinalIgnoreCase 比字符串，
 *        `LT/GT` 是字典序比较、没有业务含义 ⇒ 只给「等于 / 属于」（物料编码另加「包含」）
 *      日期型（统一交期/发行日期/出荷指示入物流日）：执行器把字段转成 `ToString("O")` 再比较，
 *        `Equals` 拿 "2026-10-22" 去比 "2026-10-22T00:00:00.0000000" 恒不等 ⇒ 只给比较符
 *      数值型（库存消耗日/数量）：执行器用 `decimal`/`CompareNumericOrDate` 比较 ⇒ 只给比较符
 *  - 条件组（组间 OR）**编辑器不开放**：运行侧投影对非空 `MatchConditionGroups` 显式抛错，
 *    现在做编辑器会产出「发布能过、跑排程必炸」的配置（3号位 实施包 §五）。
 *
 * 落库格式（`FrozenStrategySnapshot.cs:34-37 / :55+`）：
 *  - 顶层 `{"Segments":[...]}`
 *  - 段内键名 **PascalCase**，且 `PrioritySegment` 带 `JsonUnmappedMemberHandling(Disallow)`
 *    ⇒ 键名写错后端反序列化整块失败（不是忽略）
 *  - 枚举一律写**数字序号**（后端无 JsonStringEnumConverter）
 *  - `CompetitionRound` 要么每段都写，要么整块都不写；部分写 ⇒ 运行期投影层显式抛错
 *  - `StableTieBreakFields` 是枚举，当前只有 `DEMANDKEY` 一个合法值
 */

import type {
  PrioritySegmentDto,
  SegmentMatchConditionDto,
  SegmentSortFieldDto
} from './types/rule'

/** 编辑器可选字段（发布白名单内运行侧已接线的 12 值；`DemandField` 序号） */
export interface EditorDemandFieldDef {
  value: number
  label: string
  kind: 'TEXT' | 'DATE' | 'NUMBER'
  /** 仅「物料编码」允许「包含」（后端实现为前缀匹配 StartsWith） */
  allowContains?: boolean
  /** 值可写「相对冻结日整数天数」，由后端投影层换算成具体日期（仅出荷指示入物流日） */
  allowRelativeOffset?: boolean
  /** 发布校验 `SupportedFieldValues` 的英文码值域；空 = 不拦（兼容存量自定义值） */
  valueDomain?: readonly string[]
}

export const EDITOR_DEMAND_FIELDS: ReadonlyArray<EditorDemandFieldDef> = [
  // 1 延期状态：2026-11-07 0号位 裁决归参数集（ProtectDelayed），规则集不再承载 ⇒ 关闭。
  { value: 2, label: '客户等级', kind: 'TEXT' },
  {
    value: 3,
    label: '订单类型',
    kind: 'TEXT',
    // 后端兼容存量旧词 SO/WO（等价 SALES_ORDER/PRODUCTION_INSTRUCTION），这里一并放行
    valueDomain: ['SALES_ORDER', 'PRODUCTION_INSTRUCTION', 'SO', 'WO']
  },
  { value: 6, label: '统一交期', kind: 'DATE' },
  { value: 7, label: '订单发行日期', kind: 'DATE' },
  // 2026-11-07 V1.1 新增字段（3号位 实施包 v1.0 §三；运行侧已接线）
  {
    value: 8,
    label: '客户区分',
    kind: 'TEXT',
    valueDomain: ['JAPAN', 'DOMESTIC', 'OVERSEAS', 'VIETNAM', 'INTER_FACTORY', 'OTHER']
  },
  { value: 9, label: '出荷指示入物流日', kind: 'DATE', allowRelativeOffset: true },
  {
    value: 10,
    label: '运输方式',
    kind: 'TEXT',
    valueDomain: ['LAND', 'SEA', 'AIR', 'COURIER']
  },
  {
    value: 11,
    label: '销售类别',
    kind: 'TEXT',
    valueDomain: ['DIRECT_SALES', 'SALES_REPLENISHMENT']
  },
  // 12 需求成熟度：DTO 注释称归参数集、发布白名单/执行器又认 ⇒ 3号位 口径确认前不开放
  { value: 13, label: '库存消耗日（天）', kind: 'NUMBER' },
  {
    value: 14,
    label: '非在库出荷区分',
    kind: 'TEXT',
    valueDomain: ['FULL_PURPLE_SLIP', 'DIFF_PURPLE_SLIP']
  },
  { value: 15, label: '物料编码', kind: 'TEXT', allowContains: true },
  { value: 16, label: '数量', kind: 'NUMBER' }
]

/** 条件操作符（执行器支持的 7 个；NotEquals 不放行） */
export const EDITOR_CONDITION_OPERATORS: ReadonlyArray<{ value: number; label: string }> = [
  { value: 0, label: '等于' },
  { value: 6, label: '属于' },
  { value: 7, label: '包含（前缀匹配）' },
  { value: 2, label: '小于' },
  { value: 3, label: '小于等于' },
  { value: 4, label: '大于' },
  { value: 5, label: '大于等于' }
]

export const EDITOR_COMPETITION_ROUNDS: ReadonlyArray<{ value: number; label: string }> = [
  { value: 1, label: '第 1 轮（顶层独立需求）' },
  { value: 2, label: '第 2 轮（SH 源厂履约）' },
  { value: 3, label: '第 3 轮（孤儿与下阶）' }
]

/** 轮次短名（段卡里「归入第 N 轮」用；组标题用上面的长名） */
export const COMPETITION_ROUND_SHORT: ReadonlyArray<{ value: number; label: string }> = [
  { value: 1, label: '第 1 轮' },
  { value: 2, label: '第 2 轮' },
  { value: 3, label: '第 3 轮' }
]

/**
 * 每一轮装的是哪一类需求 —— 冻结 01 v1.15 §16 原文：
 *   「Round1（客户订单及合法成品顶层MTS）、Round2（与既有上层履约链相连且具有真实SH身份的需求）、
 *     Round3（合法孤儿SH和孤儿MTS，孤儿SH先于孤儿MTS）。三轮不得混池；
 *     每一轮内继续依有序规则段→第一命中→段内排序。」
 */
export const COMPETITION_ROUND_SCOPE: Record<number, string> = {
  1: '客户订单，以及合法的成品顶层 MTS',
  2: '与既有上层履约链相连、且有真实 SH 身份的需求',
  3: '合法孤儿 SH 和孤儿 MTS（孤儿 SH 先于孤儿 MTS）'
}

/** 轮次分组（**轮 ⊃ 段**）：固定三组，组内保持原有先后 */
export interface SegmentRoundGroup<T extends PrioritySegmentDto = PrioritySegmentDto> {
  /** null = 未声明轮次（正常不会出现；出现即存量数据异常，界面显式标出，不静默归第 1 轮） */
  round: number | null
  label: string
  /** 这一轮装的是哪一类需求（冻结 01 v1.15 §16） */
  scope: string
  segments: T[]
}

/**
 * 按轮次分组。
 *
 * 为什么是「轮在外、段在内」：冻结 01 v1.15 §16 规定「三轮不得混池；每一轮内继续依
 * 有序规则段→第一命中→段内排序」⇒ 段是**轮内**的有序规则段，段序只在轮内有效。
 * 后端消费顺序见 `PeggingOrchestrator.cs:13452-13457`：逐轮取 `Segments.Where(轮次相等)`，
 * 轮内再按 `SegmentOrder` 升序 First Match。
 *
 * 泛型保留调用方的行类型（编辑器行 = `PrioritySegmentDto & { _k }`），免得分组后把行类型抹平。
 */
export function groupSegmentsByRound<T extends PrioritySegmentDto>(
  segments: T[]
): SegmentRoundGroup<T>[] {
  const groups: SegmentRoundGroup<T>[] = EDITOR_COMPETITION_ROUNDS.map((r) => ({
    round: r.value,
    label: r.label,
    scope: COMPETITION_ROUND_SCOPE[r.value] ?? '',
    segments: [] as T[]
  }))
  const extras = new Map<number, SegmentRoundGroup<T>>()
  for (const s of segments) {
    const g = groups.find((x) => x.round === s.competitionRound)
    if (g) {
      g.segments.push(s)
      continue
    }
    // 轮次缺失或越界：单开一组显式标出（不静默塞进第 1 轮 —— 后端投影层也是这个口径）
    const key = s.competitionRound ?? -1
    let eg = extras.get(key)
    if (!eg) {
      eg = {
        round: key < 0 ? null : key,
        label: key < 0 ? '未声明轮次' : `第 ${key} 轮（取值不在 1/2/3）`,
        scope: '',
        segments: [] as T[]
      }
      extras.set(key, eg)
    }
    eg.segments.push(s)
  }
  groups.push(...extras.values())
  return groups
}

/** 稳定 Tie-break：后端枚举只有 DEMANDKEY 一个值 */
export const EDITOR_TIE_BREAK_FIELDS: ReadonlyArray<{ value: string; label: string }> = [
  { value: 'DEMANDKEY', label: '按需求键稳定排序' }
]

/**
 * D 类「V1 固定」的稳定排序键 —— 唯一合法值。
 *
 * 0号位 2026-11-04 工作簿第 56 条把 `DemandPriority.Segments[].StableTieBreakFields[]`
 * 判为 **D V1固定**：「V1 沿冻结 DEMANDKEY，不开放配置其他随机或不稳定键」。
 * 后端 `TieBreakField` 枚举也只有 DEMANDKEY（`FrozenStrategySnapshot.cs:236-240`）。
 * ⇒ 前端**不给开关**（原开关可写空数组 = 关掉稳定排序，与 D 类相抵触），
 *   一律写 DEMANDKEY；存量空值在下一次保存时归一为 DEMANDKEY。
 */
export const FIXED_TIE_BREAK_FIELD = 'DEMANDKEY'

/**
 * 文本型字段的候选值（下拉提示；仍允许手填 —— 后端没有「取值」端点）。
 *
 * **值必须原样是英文码**：生产执行器 `DemandPriorityExecutor.cs:212-213` 拿
 * `Order.DelayStatus / CustomerTier / OrderType` 的原始字符串按 OrdinalIgnoreCase 比较，
 * 存中文或自造词只会让条件永远不命中。
 *
 * 值域依据冻结 `07 v5.1.16:2688 / :2687 / :637`：
 *  - CustomerTier：`VIP > KEY_ACCOUNT > STANDARD > GENERAL`（当前主要启用 VIP/GENERAL）
 *  - OrderType：`SALES_ORDER`（客户订单）/ `PRODUCTION_INSTRUCTION`（生产指示）
 *
 * DelayStatus 的候选（ON_TIME / FIRST_DELAY / REPEATED_DELAY）随字段 1 一并撤除：
 * 2026-11-07 0号位 裁决归参数集，规则集编辑器不再开放该字段。
 * 旧值 `Delayed/OnTrack`、`VIP/A/B/C` 与冻结词表**一个都对不上**（2026-11-04 修正），
 * 订单类型原先干脆是空数组 ⇒ 下拉显示「无数据」，用户以为控件坏了。
 */
export interface EditorValueSuggestion {
  value: string
  label: string
}

const FIELD_VALUE_SUGGESTIONS: Record<number, EditorValueSuggestion[]> = {
  // 2026-11-06 2号位 校准：CustomerTier 值域收敛 {VIP, GENERAL}
  // （来源=订单备注含 *VP*；旧 A/B/C、KEY_ACCOUNT/STANDARD 不再是合法维护值）。
  // 这里只收窄「候选」，仍允许手填 —— 存量已发布值不因前端候选变化而漂移。
  2: [
    { value: 'VIP', label: 'VIP' },
    { value: 'GENERAL', label: '一般（GENERAL）' }
  ],
  3: [
    { value: 'SALES_ORDER', label: '客户订单（SALES_ORDER）' },
    { value: 'PRODUCTION_INSTRUCTION', label: '生产指示（PRODUCTION_INSTRUCTION）' }
  ],
  8: [
    { value: 'JAPAN', label: '日本（JAPAN）' },
    { value: 'DOMESTIC', label: '国内（DOMESTIC）' },
    { value: 'OVERSEAS', label: '海外（OVERSEAS）' },
    { value: 'VIETNAM', label: '越南（VIETNAM）' },
    { value: 'INTER_FACTORY', label: '工厂间（INTER_FACTORY）' },
    { value: 'OTHER', label: '其他（OTHER）' }
  ],
  10: [
    { value: 'LAND', label: '陆运（LAND）' },
    { value: 'SEA', label: '海运（SEA）' },
    { value: 'AIR', label: '空运（AIR）' },
    { value: 'COURIER', label: '快递（COURIER）' }
  ],
  11: [
    { value: 'DIRECT_SALES', label: '直接销售（DIRECT_SALES）' },
    { value: 'SALES_REPLENISHMENT', label: '销售补货（SALES_REPLENISHMENT）' }
  ],
  14: [
    { value: 'FULL_PURPLE_SLIP', label: '全额紫票（FULL_PURPLE_SLIP）' },
    { value: 'DIFF_PURPLE_SLIP', label: '差额紫票（DIFF_PURPLE_SLIP）' }
  ]
}

export function isEditorField(field: number): boolean {
  return EDITOR_DEMAND_FIELDS.some((f) => f.value === field)
}

export function fieldKindOf(field: number): 'TEXT' | 'DATE' | 'NUMBER' {
  return EDITOR_DEMAND_FIELDS.find((f) => f.value === field)?.kind ?? 'TEXT'
}

export function fieldLabelOf(field: number): string {
  return EDITOR_DEMAND_FIELDS.find((f) => f.value === field)?.label ?? `未知(${field})`
}

/** 某字段下允许的操作符（文本 → 等于/属于，物料编码另加包含；日期/数值 → 四个比较符） */
export function operatorsForField(field: number): ReadonlyArray<{ value: number; label: string }> {
  const def = EDITOR_DEMAND_FIELDS.find((f) => f.value === field)
  if (!def) return []
  if (def.kind === 'DATE' || def.kind === 'NUMBER') {
    return EDITOR_CONDITION_OPERATORS.filter((o) => [2, 3, 4, 5].includes(o.value))
  }
  const base = EDITOR_CONDITION_OPERATORS.filter((o) => [0, 6].includes(o.value))
  return def.allowContains
    ? [...base, ...EDITOR_CONDITION_OPERATORS.filter((o) => o.value === 7)]
    : base
}

export function valueSuggestionsOf(field: number): EditorValueSuggestion[] {
  return FIELD_VALUE_SUGGESTIONS[field] ?? []
}

/**
 * 该字段是否允许「相对冻结日整数天数」（当前只有出荷指示入物流日）。
 * 后端换算点：`DemandPriorityProjector.IsRelativeDateThreshold`（只认 CUSTOMERDUEDATE + 范围算子）。
 */
export function isRelativeOffsetField(field: number): boolean {
  return EDITOR_DEMAND_FIELDS.find((f) => f.value === field)?.allowRelativeOffset === true
}

/** 该字段/操作符组合是否支持相对天数：仅范围算子（EQ/IN 必须写具体日期）。 */
export function supportsRelativeOffset(field: number, operator: number): boolean {
  return isRelativeOffsetField(field) && [2, 3, 4, 5].includes(operator)
}

/** 纯整数文本（可负）：相对天数写法，如 `20` / `0` / `-5`。 */
export function isOffsetDaysText(text: string): boolean {
  return /^-?\d+$/.test(text.trim())
}

function isInOperator(operator: number): boolean {
  return operator === 6
}

/**
 * 新建一个段，**归属指定轮次**（轮次是外层分组：段永远属于某一轮）。
 *
 * 段名沿用全局序号给默认名（历史口径不变）；真实先后由数组位置决定，
 * 保存时统一重排为 1..N（`SegmentOrder` 必须唯一正整数）。
 */
export function createEmptySegment(
  existing: PrioritySegmentDto[],
  round: number
): PrioritySegmentDto {
  return {
    orderInSegment: existing.length + 1,
    segmentName: `第 ${existing.length + 1} 段`,
    isEnabled: true,
    competitionRound: round,
    matchConditions: [],
    sortFields: [{ field: 6, direction: 0 }],
    stableTieBreakFields: ['DEMANDKEY']
  }
}

function normalizeValue(field: number, operator: number, value: unknown): string | string[] {
  if (isInOperator(operator)) {
    if (Array.isArray(value)) return value.map((v) => String(v))
    if (typeof value === 'string' && value.trim()) return [value]
    return []
  }
  if (value === null || value === undefined) return ''
  const text = String(value)
  // 日期字段：后端存 ISO 串，日期控件只认 yyyy-MM-dd；
  // 出荷指示入物流日的相对天数（"20" / "-5"）不受 slice 影响，原样保留。
  return fieldKindOf(field) === 'DATE' ? text.slice(0, 10) : text
}

/**
 * 后端解析结果 → 编辑器表单模型
 *  - 值统一成 `string` / `string[]`（控件只处理这两种）
 *  - 只读字段顺序保持后端给的（已按 SegmentOrder 排序）
 *  - `stableTieBreakFields` 恒为 `['DEMANDKEY']`（D 类 V1 固定，见 FIXED_TIE_BREAK_FIELD）
 *  - **轮次缺失 ⇒ 归第 1 轮**：后端投影层对「整块未声明轮次」的处理就是
 *    `DefaultCompetitionRound = 1`（`DemandPriorityProjector.cs:50/73-79`），
 *    语义完全等价；编辑器统一显式写 1，界面上就不会出现「看不出归属」的未声明状态。
 */
export function toEditableSegments(segments: PrioritySegmentDto[]): PrioritySegmentDto[] {
  return segments.map((s, idx) => ({
    orderInSegment: s.orderInSegment ?? idx + 1,
    segmentName: s.segmentName ?? '',
    isEnabled: s.isEnabled !== false,
    competitionRound: s.competitionRound ?? 1,
    matchConditions: (s.matchConditions ?? []).map((c) => ({
      field: c.field,
      operator: c.operator,
      value: normalizeValue(c.field, c.operator, c.value)
    })),
    sortFields: (s.sortFields ?? []).map((f) => ({ field: f.field, direction: f.direction })),
    stableTieBreakFields: [FIXED_TIE_BREAK_FIELD]
  }))
}

/** 顺序按当前数组重排（1..N），保证 SegmentOrder 唯一正整数 */
export function renumberSegments(segments: PrioritySegmentDto[]): PrioritySegmentDto[] {
  return segments.map((s, i) => ({ ...s, orderInSegment: i + 1 }))
}

/**
 * 表单模型 → demandPriorityJson（写入 `RuleSetVersion.DemandPriorityJson`）
 *
 * 轮次声明：全段都选了才写 `CompetitionRound`；一段没选就整块不写
 * （后端投影层对「部分声明」是显式抛错，不能靠 null 混过去）。
 */
export function serializePrioritySegments(segments: PrioritySegmentDto[]): string {
  const rows = renumberSegments(segments)
  const allDeclared = rows.length > 0 && rows.every((s) => s.competitionRound != null)
  return JSON.stringify({
    Segments: rows.map((s) => ({
      SegmentOrder: s.orderInSegment,
      SegmentName: (s.segmentName ?? '').trim(),
      IsEnabled: s.isEnabled !== false,
      ...(allDeclared ? { CompetitionRound: Number(s.competitionRound) } : {}),
      MatchConditions: (s.matchConditions ?? []).map((c) => ({
        Field: Number(c.field),
        Operator: Number(c.operator),
        Value: c.value
      })),
      SortFields: (s.sortFields ?? []).map((f) => ({
        Field: Number(f.field),
        Direction: Number(f.direction)
      })),
      StableTieBreakFields: [FIXED_TIE_BREAK_FIELD]
    }))
  })
}

function isDateText(v: string): boolean {
  return /^\d{4}-\d{2}-\d{2}$/.test(v) && !Number.isNaN(Date.parse(v))
}

function inValueDomain(
  def: EditorDemandFieldDef | undefined,
  values: readonly unknown[]
): string | null {
  if (!def?.valueDomain) return null
  const domain = def.valueDomain
  const bad = values.map(String).find((v) => !domain.includes(v))
  return bad === undefined ? null : `值「${bad}」不在合法值域（${domain.join(' / ')}）`
}

function conditionError(segNo: number, idx: number, c: SegmentMatchConditionDto): string | null {
  const where = `第 ${segNo} 段 条件 ${idx + 1}`
  if (!isEditorField(c.field)) return `${where}：字段不在可用范围`
  if (!operatorsForField(c.field).some((o) => o.value === c.operator)) {
    return `${where}：${fieldLabelOf(c.field)} 不能使用该操作符`
  }
  const def = EDITOR_DEMAND_FIELDS.find((f) => f.value === c.field)
  if (isInOperator(c.operator)) {
    const arr = Array.isArray(c.value) ? c.value : []
    if (arr.length === 0 || arr.some((v) => !String(v).trim())) {
      return `${where}：「属于」至少要填一个值`
    }
    const domainErr = inValueDomain(def, arr)
    return domainErr ? `${where}：${def?.label ?? ''}${domainErr}` : null
  }
  // 数字控件（库存消耗日/数量/相对天数）回填的是 number，不能只认 string，否则会误报「不能为空」。
  const text =
    c.value === null || c.value === undefined
      ? ''
      : typeof c.value === 'string'
        ? c.value.trim()
        : String(c.value).trim()
  if (!text) return `${where}：条件值不能为空`
  if (fieldKindOf(c.field) === 'DATE') {
    if (supportsRelativeOffset(c.field, c.operator)) {
      // 出荷指示入物流日：整数 N = 冻结日 +N 天（后端投影层换算），具体日期照旧按日比较。
      if (!isDateText(text) && !isOffsetDaysText(text)) {
        return `${where}：${fieldLabelOf(c.field)} 的比较值应为整数天数（如 20 = 冻结日 +20 天）或具体日期 YYYY-MM-DD`
      }
    } else if (!isDateText(text)) {
      return `${where}：日期格式应为 YYYY-MM-DD`
    }
  }
  if (def?.kind === 'NUMBER') {
    const n = Number(text)
    if (!Number.isFinite(n)) return `${where}：数值格式不正确`
    if (n < 0) return `${where}：${def.label} 不能小于 0`
  }
  if (c.operator === 0) {
    const domainErr = inValueDomain(def, [text])
    if (domainErr) return `${where}：${def?.label ?? ''}${domainErr}`
  }
  return null
}

/**
 * 前端拦截（与后端发布校验对齐，先在本地点出来再让用户去跑校验）
 * 返回空数组 = 可以保存。
 */
export function validatePrioritySegments(segments: PrioritySegmentDto[]): string[] {
  const errors: string[] = []
  if (segments.length === 0) return ['至少要配一个段位']

  const declared = segments.filter((s) => s.competitionRound != null).length
  if (declared !== 0 && declared !== segments.length) {
    errors.push('竞争轮次要每段都选，或者全部都不选（不能只选一部分）')
  }
  if (
    segments.some(
      (s) => s.competitionRound != null && ![1, 2, 3].includes(Number(s.competitionRound))
    )
  ) {
    errors.push('竞争轮次只能是第 1 / 2 / 3 轮')
  }

  segments.forEach((s, i) => {
    const no = i + 1
    if (!String(s.segmentName ?? '').trim()) errors.push(`第 ${no} 段：名称不能为空`)
    ;(s.matchConditions ?? []).forEach((c, ci) => {
      const err = conditionError(no, ci, c)
      if (err) errors.push(err)
    })
    ;(s.sortFields ?? []).forEach((f, fi) => {
      if (!isEditorField(f.field)) errors.push(`第 ${no} 段 排序 ${fi + 1}：字段不在可用范围`)
      if (![0, 1].includes(Number(f.direction))) {
        errors.push(`第 ${no} 段 排序 ${fi + 1}：方向只能是升序或降序`)
      }
    })
    ;(s.stableTieBreakFields ?? []).forEach((f) => {
      if (!EDITOR_TIE_BREAK_FIELDS.some((t) => t.value === f)) {
        errors.push(
          `第 ${no} 段：稳定排序键只能是 ${EDITOR_TIE_BREAK_FIELDS.map((t) => t.value).join('/')}`
        )
      }
    })
  })

  // 空条件段＝无条件命中，会吞掉其后所有段 ⇒ 只能有一段且必须末位
  const fallbackIdx = segments
    .map((s, i) => ((s.matchConditions ?? []).length === 0 ? i : -1))
    .filter((i) => i >= 0)
  if (fallbackIdx.length > 1) {
    errors.push('无条件的段最多只能有 1 个')
  } else if (fallbackIdx.length === 1 && fallbackIdx[0] !== segments.length - 1) {
    errors.push('无条件的段必须放在最后，否则它后面的段永远不会被用到')
  }

  return errors
}

/** 排序键行（编辑器新增一条时的默认值） */
export function createEmptySortField(): SegmentSortFieldDto {
  return { field: 6, direction: 0 }
}
