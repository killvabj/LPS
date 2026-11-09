/**
 * APS V1 4号位 — 需求优先级段位（Priority Segment）解析与中文字典
 *
 * 后端契约（`lps/LPS.APS.Core/Dto/FrozenStrategySnapshot.cs`）：
 *  - `PrioritySegment`（:95）段内键**只有 6 个**：
 *    `SegmentOrder / SegmentName / IsEnabled / MatchConditions / SortFields / StableTieBreakFields`
 *    + 可选的 `CompetitionRound`（v1.2 B3 新增）
 *    + 可选的 `MatchConditionGroups`（2026-11-06 DNF 条件组；运行侧尚未接线，编辑器暂不开放）
 *  - 该类带 `[JsonUnmappedMemberHandling(Disallow)]` ⇒ 键名写错后端反序列化直接失败
 *  - `SegmentMatchCondition`（:117）= `{Field: DemandField(枚举), Operator: ConditionOperator(枚举), Value: any}`
 *  - `SegmentSortField`（:123）= `{Field: DemandField(枚举), Direction: SortDirection(枚举)}`
 *
 * ⚠️ 枚举是**数字**（后端以 int 落 JSON）。下面三张表的序号必须与后端枚举声明顺序逐字一致，
 *    改动前先核对 `FrozenStrategySnapshot.cs:108-125`。
 *
 * 冻结红线：U21 —— 页面**不得**出现 PriorityScore（内部优先级评分不对页面开放）。
 */

import type {
  PrioritySegmentDto,
  SegmentMatchConditionDto,
  SegmentSortFieldDto
} from './types/rule'

/** DemandField（后端 `FrozenStrategySnapshot.cs:174` `enum DemandField`，共 17 项） */
export const DEMAND_FIELDS: ReadonlyArray<{ value: number; label: string }> = [
  { value: 0, label: '剩余时间' },
  { value: 1, label: '延期状态' },
  { value: 2, label: '客户等级' },
  { value: 3, label: '订单类型' },
  { value: 4, label: 'PMC 人工强保护' },
  { value: 5, label: '业务优先等级' },
  { value: 6, label: '统一交期' },
  { value: 7, label: '订单发行日期' },
  { value: 8, label: '客户区分' },
  { value: 9, label: '出荷指示入物流日' },
  { value: 10, label: '运输方式' },
  { value: 11, label: '销售类别' },
  { value: 12, label: '需求成熟度' },
  { value: 13, label: '库存消耗日' },
  { value: 14, label: '非在库出荷区分' },
  { value: 15, label: '物料编码' },
  { value: 16, label: '数量' }
]

/** ConditionOperator（后端 `enum ConditionOperator`，共 8 项） */
export const CONDITION_OPERATORS: ReadonlyArray<{ value: number; label: string }> = [
  { value: 0, label: '等于' },
  { value: 1, label: '不等于' },
  { value: 2, label: '小于' },
  { value: 3, label: '小于等于' },
  { value: 4, label: '大于' },
  { value: 5, label: '大于等于' },
  { value: 6, label: '属于' },
  { value: 7, label: '包含（前缀匹配）' }
]

/** SortDirection（后端 `enum SortDirection`） */
export const SORT_DIRECTIONS: ReadonlyArray<{ value: number; label: string }> = [
  { value: 0, label: '升序' },
  { value: 1, label: '降序' }
]

/** CompetitionRound（后端 `enum CompetitionRound`；1/2/3） */
export const COMPETITION_ROUNDS: ReadonlyArray<{ value: number; label: string }> = [
  { value: 1, label: '第 1 轮（顶层独立需求）' },
  { value: 2, label: '第 2 轮（SH 源厂履约）' },
  { value: 3, label: '第 3 轮（孤儿与下阶）' }
]

function labelOf(table: ReadonlyArray<{ value: number; label: string }>, value: number): string {
  return table.find((t) => t.value === value)?.label ?? `未知(${value})`
}

/** 字段名 → 中文 */
export function demandFieldLabel(value: number): string {
  return labelOf(DEMAND_FIELDS, value)
}

/** 操作符 → 中文 */
export function conditionOperatorLabel(value: number): string {
  return labelOf(CONDITION_OPERATORS, value)
}

/** 排序方向 → 中文 */
export function sortDirectionLabel(value: number): string {
  return labelOf(SORT_DIRECTIONS, value)
}

/** 竞争轮次 → 中文（未声明时返回「未声明」） */
export function competitionRoundLabel(value?: number | null): string {
  if (value === null || value === undefined) return '未声明'
  return labelOf(COMPETITION_ROUNDS, value)
}

/** 命中条件的值 → 可读文本（Operator=In 时是数组） */
export function conditionValueText(value: unknown): string {
  if (value === null || value === undefined) return '—'
  if (Array.isArray(value)) return value.map((v) => String(v)).join('、')
  if (typeof value === 'object') return JSON.stringify(value)
  return String(value)
}

/** 一条命中条件 → 大白话（如「延期状态 等于 Delayed」） */
export function conditionText(c: SegmentMatchConditionDto): string {
  return `${demandFieldLabel(c.field)} ${conditionOperatorLabel(c.operator)} ${conditionValueText(c.value)}`
}

/** 一条排序键 → 大白话（如「统一交期 升序」） */
export function sortFieldText(s: SegmentSortFieldDto): string {
  return `${demandFieldLabel(s.field)} ${sortDirectionLabel(s.direction)}`
}

/** 后端 demandPriorityJson 原文（PascalCase） */
interface RawPrioritySegment {
  SegmentOrder?: number
  SegmentName?: string
  IsEnabled?: boolean
  CompetitionRound?: number | null
  MatchConditions?: RawMatchCondition[]
  SortFields?: RawSortField[]
  StableTieBreakFields?: string[]
}

interface RawMatchCondition {
  Field?: number
  Operator?: number
  Value?: unknown
}

interface RawSortField {
  Field?: number
  Direction?: number
}

/**
 * 解析 demandPriorityJson → 段位数组
 *
 *  - 段内键名严格 PascalCase（后端 Disallow 语义 ⇒ 键名不能猜）
 *  - 解析失败 / 结构不符 → 返回空数组（fail-open：页面显示「该版本没有需求优先级段位」，
 *    绝不编造段位）
 *  - `CompetitionRound` 缺省 = 未声明（保留 undefined，页面显示「未声明」）
 */
export function parsePrioritySegments(demandPriorityJson?: string | null): PrioritySegmentDto[] {
  if (!demandPriorityJson) return []
  let doc: { Segments?: RawPrioritySegment[] }
  try {
    doc = JSON.parse(demandPriorityJson) as { Segments?: RawPrioritySegment[] }
  } catch {
    return []
  }
  const segments = doc?.Segments
  if (!Array.isArray(segments)) return []
  return segments
    .map((s, idx) => ({
      orderInSegment: s?.SegmentOrder ?? idx + 1,
      segmentName: s?.SegmentName ?? '',
      isEnabled: s?.IsEnabled ?? false,
      competitionRound: s?.CompetitionRound ?? undefined,
      matchConditions: Array.isArray(s?.MatchConditions)
        ? s.MatchConditions.map((c) => ({
            field: c?.Field ?? -1,
            operator: c?.Operator ?? -1,
            value: c?.Value ?? null
          }))
        : [],
      sortFields: Array.isArray(s?.SortFields)
        ? s.SortFields.map((f) => ({ field: f?.Field ?? -1, direction: f?.Direction ?? -1 }))
        : [],
      stableTieBreakFields: Array.isArray(s?.StableTieBreakFields) ? s.StableTieBreakFields : []
    }))
    .sort((a, b) => a.orderInSegment - b.orderInSegment)
}
