import dayjs from 'dayjs'
import utc from 'dayjs/plugin/utc'

dayjs.extend(utc)

/**
 * ⚠️ 两种解析口径的分界（2026-09-29 约定在 10-28 被 3号位 收窄，勿混用）：
 *
 *  ① **外部源 UTC**（ODS / MES 契约视图）：后端存的是真 UTC ⇒ 用 `formatUtcDateTime` 等
 *     （`dayjs.utc(s).local()`，做 UTC→本地位移）。
 *  ② **APS 自有治理域本地**（RuleSet / RuleSetVersion / ParameterSet* / StrategyProfile* /
 *     SetupTransitionRule / AuditLog 等由 `DateTime.Now` 落库的表）：后端存的是**服务器本地墙钟**
 *     ⇒ 用 `formatLocalDateTime` 等（裸解析，字面值即本地时间，**不做位移**）。
 *
 * 依据：0号位 2026-10-13「时间戳一律本地（DateTime.Now / GETDATE()），禁 UTC」；
 *      3号位 2026-10-28 回执《治理版本时间戳+8h核证回执_本地口径前端本地直显_v1.0》——
 *      方案 A（后端改 UTC）不采纳，前端治理域本地直显为**正式口径**。
 *
 * 另注：治理域里**内存计算**出来的时间（`ValidatedAt` / `ComparedAt`，`DateTime.Now` Kind=Local）
 * 序列化时带 `+08:00` 偏移 ⇒ 走 ① 的 `formatUtcDateTime` 恰好显示正确；
 * 而从库里读回的 `PublishedAt` / `EffectiveFrom` / `EffectiveTo` / `OccurredAt` 是 Unspecified 无偏移
 * ⇒ 走 ② 才正确。
 */

/** 外部源 UTC → 本地（ODS / MES 契约视图） */
export function formatUtcDate(s: string | null | undefined): string {
  return s ? dayjs.utc(s).local().format('YYYY-MM-DD') : '—'
}

export function formatUtcDateTime(s: string | null | undefined): string {
  return s ? dayjs.utc(s).local().format('YYYY-MM-DD HH:mm') : '—'
}

export function formatUtcDateTimeSec(s: string | null | undefined): string {
  return s ? dayjs.utc(s).local().format('YYYY-MM-DD HH:mm:ss') : '—'
}

export function formatUtcMonthDay(s: string | null | undefined): string {
  return s ? dayjs.utc(s).local().format('MM-DD') : '—'
}

export function formatUtcMonthDayHM(s: string | null | undefined): string {
  return s ? dayjs.utc(s).local().format('MM-DD HH:mm') : '—'
}

/* ===== 治理域：本地直显（后端 DateTime.Now 落库，字面值即本地时间，不做位移）===== */

/** 治理域时间（分） */
export function formatLocalDateTime(s: string | null | undefined): string {
  return s ? dayjs(s).format('YYYY-MM-DD HH:mm') : '—'
}

/** 治理域时间（秒） */
export function formatLocalDateTimeSec(s: string | null | undefined): string {
  return s ? dayjs(s).format('YYYY-MM-DD HH:mm:ss') : '—'
}

/** 治理域日期 */
export function formatLocalDate(s: string | null | undefined): string {
  return s ? dayjs(s).format('YYYY-MM-DD') : '—'
}

/** 治理域月日时分（窄位展示，如批次下拉） */
export function formatLocalMonthDayHM(s: string | null | undefined): string {
  return s ? dayjs(s).format('MM-DD HH:mm') : '—'
}
