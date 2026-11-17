/**
 * APS V1 4号位 — 生产部门主数据 lookup API
 *
 * 后端端点：GET /api/governance/lookups/departments?search={编码片段}
 *   - 由 3 号位 在 lps/LPS.APS.Web/Controllers/SetupRuleController.cs:299-313 落地
 *   - 3 号位 直接读源表 ProductionDepartment（红线 #4：主数据全量返回）
 *   - 权限：aps.setup.view（与 Setup 同包共享）
 *
 * 用途：
 *   - TaskSplitRuleConfig.vue / Drawer「生产部门」下拉真实数据源
 *     （替代 G2 未决时 4 号位 临时从 /api/schedule/gantt/{id} resources[].productionDepartmentId 去重的过渡实现）
 *   - Opm / ManualEta 等页面部门筛选（如需）
 *
 * 设计要点：
 *   - 不修改后端（4 号位 硬约束），仅前端封装既有端点
 *   - 不动 mock（生产演示主要走真实后端；4 号位 UI 默认 VITE_USE_MOCK=false）
 */

import { apsHttp } from './http'

/** 部门 lookup 项（与 Setup 共享 DTO DepartmentLookupItem） */
export interface DepartmentLookupItem {
  masterId: number
  code: string
  stageCode: string
  sourceDeptCode: string | null
  holon: string | null
  /**
   * 部门归属工厂（= ProductionDepartment.FactoryId）
   *  - 冻结 v5.1.12 §1.11 line 3284：INT FK → Factory.Id，**可空**（兼容早期未明确归厂的部门）
   *  - 冻结 v5.1.12 line 2946：PWC 的 DEPARTMENT 行「须校验其 FactoryId 一致」——后端写入时已 400 拦截
   *
   * 10-14 已落地：3号位 A组 A1 补齐（DepartmentLookupItem 增字段 + MasterDataLookupRepository
   *   LookupDepartmentsAsync 增 [FactoryId] 投影），dev 实测 200/200 条均有值。
   *   消费方仍按"可空"处理（历史行可能为空）。
   */
  factoryId?: number | null
}

/** 生产部门项（GET /api/production-departments；5号位 ProductionDepartmentController 出参） */
export interface ProductionDepartmentItem {
  /** 生产部门 Id（= RoutingOperation.ProductionDepartmentId 的 FK 值） */
  id: number
  /** 部门编码（如 BJ_ASSY_涂布组） */
  deptCode: string
  /** 部门名称（如 BJ组立_涂布组）—— 用户展示口径 */
  deptName: string
  /** 是否启用 */
  isActive: boolean
}

export const departmentLookupApi = {
  /**
   * 全量活跃部门（GET）
   *  - 后端端点：GET /api/governance/lookups/departments
   *  - SQL：直接读源表 ProductionDepartment（红线 #4 全量返回）
   *  - 用途：Drawer 部门下拉真实数据源（替代 G2 过渡）
   *  - `limit`：后端缺省 200，dev 实库 287 个部门 ⇒ 需要「部门→工厂」全量映射时传 500
   *  - 失败：返回空数组（前端兜底，UI 显示空态）
   */
  async listDepartments(limit = 200): Promise<DepartmentLookupItem[]> {
    try {
      return await apsHttp.get<DepartmentLookupItem[]>({
        url: '/api/governance/lookups/departments',
        params: { limit }
      })
    } catch {
      return []
    }
  },

  /**
   * 生产部门全量（GET，**带部门名称**）
   *  - 后端端点：GET /api/production-departments（5号位 ProductionDepartmentController）
   *  - 出参：{ id, deptCode, deptName, isActive }；dev 实测 288 行（无分页）
   *  - 用途：把「生产部门 Id」翻译成「部门名称」展示（如 OPM 工序列表的部门列）
   *  - ⚠️ 权限：aps.resourcecalendar.view（资源日历域）⇒ 只持 aps.rule.* 的角色会 403
   *  - 严格版（失败抛错）：页面据此区分「取不到部门名」与「没有部门」
   */
  async listProductionDepartmentsOrThrow(): Promise<ProductionDepartmentItem[]> {
    return apsHttp.get<ProductionDepartmentItem[]>({ url: '/api/production-departments' })
  },

  /**
   * 模糊搜索部门（GET，search=DeptCode）
   *  - 与 listDepartments() 同源；保留以备 ElAutocomplete 场景
   *  - 失败：返回空数组
   */
  async searchDepartments(search: string): Promise<DepartmentLookupItem[]> {
    const q = String(search ?? '').trim()
    if (q.length < 1) return []
    try {
      return await apsHttp.get<DepartmentLookupItem[]>({
        url: '/api/governance/lookups/departments',
        params: { search: q }
      })
    } catch {
      return []
    }
  }
}
