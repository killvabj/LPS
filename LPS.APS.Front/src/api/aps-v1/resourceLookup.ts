/**
 * APS V1 4号位 — 设备主数据 lookup API
 *
 * 后端端点：GET /api/governance/lookups/resources?search={编码或名称片段}
 *   - 由 3号位 在 lps/LPS.APS.Web/Controllers/SetupRuleController.cs:319-331 落地
 *   - 直接读源表 Resource（红线 #4：主数据全量返回）
 *   - 权限：aps.setup.view（与 Setup 同包共享）
 *
 * 用途：Setup 两个 Dialog 的「设备」下拉真实数据源
 *   - 同时提供 Resource.Id 供写输入提交（Id-口径 v1.1 resourceId）
 *
 * 设计要点：
 *   - 不修改后端（4号位 硬约束），仅前端封装既有端点
 *   - 失败返回空数组（调用方显示空态，不阻断页面）
 */

import { apsHttp } from './http'

/** 设备 lookup 项（与 Setup 共享 DTO ResourceLookupItem） */
export interface ResourceLookupItem {
  /** 设备主键 Resource.Id（写输入 resourceId） */
  masterId: number
  /** 设备编码（写路径不传，展示用） */
  code: string
  /** 设备名称 */
  name: string | null
  /** 归属生产部门 Id（ResourceCalendar 部门筛选用；可空） */
  productionDepartmentId: number | null
}

export const resourceLookupApi = {
  /**
   * 全量设备（GET，无 search）
   *  - 后端 LIMIT 200，dev 实测返回 200 条
   *  - 用途：Setup Dialog 设备下拉真实数据源
   *  - 失败：返回空数组
   */
  async listResources(): Promise<ResourceLookupItem[]> {
    try {
      return await apsHttp.get<ResourceLookupItem[]>({
        url: '/api/governance/lookups/resources'
      })
    } catch {
      return []
    }
  },

  /**
   * 模糊搜索设备（GET，search=ResourceCode/ResourceName 片段）
   *  - 保留以备 ElAutocomplete / 远程搜索场景；Setup Dialog 当前走全量
   *  - 失败：返回空数组
   */
  async searchResources(search: string): Promise<ResourceLookupItem[]> {
    const q = String(search ?? '').trim()
    if (q.length < 1) return []
    try {
      return await apsHttp.get<ResourceLookupItem[]>({
        url: '/api/governance/lookups/resources',
        params: { search: q }
      })
    } catch {
      return []
    }
  }
}
