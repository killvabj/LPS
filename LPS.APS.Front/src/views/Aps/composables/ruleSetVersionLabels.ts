/**
 * APS V1 4号位 — Setup 版本下拉显示「规则集身份」
 *
 * 背景：多个规则集的版本都叫 V1 时，下拉只剩版本号无法分辨（0号位 2026-10-26 任务书
 *   SET-UI-01 要求「同时显示 RuleSet 编码 + 版本号 + 状态」）。
 *
 * 10-28 P2 落码（3号位 v2.0 §二）：后端 #11 `GET /api/governance/rule-set-versions`
 *   已直接回带 `ruleSetCode` / `ruleSetName` ⇒ 不再调 `GET /rule-sets`
 *   （后者需 aps.rule.view，aps.planner 无该码必 403 ⇒ 旧实现下 PMC 的下拉只剩版本号）。
 */

/** 版本下拉需要的最小字段（后端 `SetupRuleSetVersionDto` 的子集） */
export interface RuleSetVersionLike {
  ruleSetId: number
  versionCode: string
  /** 所属规则集编码（#11 必返；未升级的后端缺该键时为 undefined） */
  ruleSetCode?: string
  /** 所属规则集名称（#11 可空：主表缺行时为空） */
  ruleSetName?: string | null
}

/** 「规则集编码 · 名称 / 版本号」；无规则集身份时退化为版本号（不编造名称） */
export function formatRuleSetVersionLabel(v: RuleSetVersionLike): string {
  const code = v.ruleSetCode
  if (!code) return v.versionCode
  return v.ruleSetName
    ? `${code} · ${v.ruleSetName} / ${v.versionCode}`
    : `${code} / ${v.versionCode}`
}
