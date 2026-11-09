/**
 * APS 资源「编码 + 名称」的显示片段（排产总览页 / 甘特页共用一套口径）。
 *
 * Why：dev 种子数据里资源的 `resourceName` 就等于 `resourceCode`（设备名直接用的编码）
 *   ⇒ 直接拼会渲染成「763-2 | 763-2」，看着像重复渲染。
 *   名称与编码**相同时只保留一段**，不同时两段都给（编码是排产员认的东西，名称是补充）。
 *
 * 抽成公共模块的原因：下拉、泳道标题、总览资源表三处都要用同一套取值，
 *   复制成三份内联逻辑必然漂移（甘特页 2026-11-03 已因两处内联返工过一次）。
 */

export interface ResourceCodeNameLike {
  resourceCode?: string | null
  resourceName?: string | null
}

/** 返回 [编码, 名称] 中需要展示的片段；两者相同或其一为空时只返回非空的一段 */
export function resourceCodeNameParts(r: ResourceCodeNameLike): string[] {
  const code = (r.resourceCode ?? '').trim()
  const name = (r.resourceName ?? '').trim()
  if (name && name !== code) return [code, name].filter(Boolean)
  return code ? [code] : name ? [name] : []
}
