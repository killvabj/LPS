/**
 * APS V1 4号位 — 仓库字典 API（主数据字典）
 *
 * 后端端点：GET /api/governance-query/warehouses（可选 ?search=）
 *   - 治理域只读查询，**类级权限 aps.plan.view**（GovernanceQueryController.cs:28）
 *   - SQL：`GROUP BY processcode` 去重 + `MIN(processname/ERPProperty/WarehouseRole/FactoryCode)`
 *   - `search` 只匹配**编码或名称**（不匹配 factoryCode）——前端本地过滤语义须与之一致
 *
 * 依据（3号位 2026-10-25《参数集仓库编码校验死路处置·验收回执 v1.0》§三/§四）：
 *  - §三 权限定案 = 方案 A：端点保持 aps.plan.view 不动，由 3号位 给参数集维护账号补绑该码
 *    ⇒ 前端**不回退**到其它端点，也不写死字典；无权限时如实提示，由调用方兜底。
 *  - §四 下拉 = 搜索下拉 + 回显码/名称；**不硬编码**夹具码（如 PS-PROD/PS_DEMO/TEST-PS-*）到前端。
 */

import { apsHttp } from './http'

/** 仓库字典项（后端匿名对象，camelCase） */
export interface WarehouseDictItem {
  /** 仓库编码（= 参数集里配置的 WarehouseCode） */
  code: string
  /** 仓库名称 */
  name?: string | null
  /** ERP 属性 */
  erpProperty?: string | null
  /** 仓库角色 */
  warehouseRole?: string | null
  /** 所属工厂编码 */
  factoryCode?: string | null
}

export const warehouseDictionaryApi = {
  /**
   * 拉仓库字典（GET /api/governance-query/warehouses）
   *
   *  - 不传 search ⇒ 返回全量（dev 实测 258 条，一次拉完比每次按键请求更稳）
   *  - 失败**如实抛错**（含 403）：调用方按「无权限 / 拉取失败」分别兜底，不静默吞成空字典
   */
  async listWarehouses(search?: string): Promise<WarehouseDictItem[]> {
    const q = (search ?? '').trim()
    const raw = await apsHttp.get<WarehouseDictItem[]>({
      url: '/api/governance-query/warehouses',
      params: q ? { search: q } : undefined
    })
    return Array.isArray(raw) ? raw.filter((w) => !!w?.code) : []
  }
}
