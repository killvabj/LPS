/**
 * APS V1 4号位 — 物料主数据 lookup API
 *
 * 后端端点：GET /api/governance/lookups/materials?search={编码片段}
 *   - 由 5号位 / 3号位 在 lps/LPS.APS.Web/Controllers/SetupRuleController.cs:333 等处落地
 *   - WHERE IsActive=1 AND (Code LIKE '%search%' OR Name LIKE '%search%')
 *   - 后端 SQL 已处理同名/相似排序，前端按返回顺序展示即可
 *   - 权限：与 Setup 同包治理，继承 aps.rule.view（Opm.vue 既有）
 *
 * 用途：
 *   - Opm.vue 顶部输入框由「物料 ID」改为「物料编码」自动补全
 *   - 用户输入编码片段 → fetch-suggestions 拉候选 → 选中后取 masterId 调业务端点
 *
 * 设计要点：
 *   - 不修改后端（4号位 硬约束），仅前端封装既有端点
 *   - mock 模式暂未启用（生产演示主要走真实后端；4号位 UI 默认 VITE_USE_MOCK=false）
 *   - 上层组件调 materialLookupApi.searchMaterials() 即可；debounce 由调用方决定
 */

import { apsHttp } from './http'
import type { MaterialLookupItem } from './types/material'

/** fetch-suggestions 输入校验（Element Plus 会自动 trim；这里给最低门槛） */
const MIN_SEARCH_LEN = 1

export const materialLookupApi = {
  /** 模糊匹配物料主数据（GET）
   *  - 入参 `search`：物料编码或名称片段（≥ 1 字符；后端上限由 SQL NVARCHAR(50) 决定）
   *  - 返回：MaterialLookupItem[]（已按 IsActive=1 过滤）
   *  - 失败：返回空数组（前端兜底，避免输入框抖动）
   */
  async searchMaterials(search: string): Promise<MaterialLookupItem[]> {
    const q = String(search ?? '').trim()
    if (q.length < MIN_SEARCH_LEN) return []
    try {
      return await apsHttp.get<MaterialLookupItem[]>({
        url: '/api/governance/lookups/materials',
        params: { search: q }
      })
    } catch {
      return []
    }
  },

  /** 全量活跃物料（GET，无 search，activeOnly=true）
   *  - 后端端点：GET /api/governance/lookups/materials?activeOnly=true
   *  - SQL：`WHERE (@ActiveOnly = 1 AND [IsActive] = 1)` + LIMIT 200
   *  - 用途：CTP 等页面「物料下拉」拉真实数据源（替代前端硬编码 7 条）
   *  - 不动后端：activeOnly 参数已存在（SetupRuleController.cs:334），按既有契约适配
   *  - 失败：返回空数组（前端兜底，下拉空态由调用方处理）
   */
  async listActiveMaterials(): Promise<MaterialLookupItem[]> {
    try {
      return await apsHttp.get<MaterialLookupItem[]>({
        url: '/api/governance/lookups/materials',
        params: { activeOnly: true }
      })
    } catch {
      return []
    }
  }
}
