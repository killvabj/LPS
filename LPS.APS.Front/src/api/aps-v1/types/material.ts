/**
 * APS V1 4号位 — 物料主数据查询 DTO（lookup 端点）
 *
 * 后端：lps/LPS.APS.Web/Controllers/SetupRuleController.cs:333（GET lookups/materials）
 *       或 lps/LPS.APS.Web/Controllers/GovernanceController.cs（lookups 段，5号位 已落地）
 *
 * 用途：
 *   - Opm.vue / Domain.vue 等页面顶部输入框从「物料 ID」改为「物料编码」自动补全
 *   - 用户输入编码片段 → 拉取候选 → 选中后取 masterId 调后续业务端点
 *   - 不修改后端（4号位 硬约束），仅前端封装既有 lookups/materials 端点
 *
 * 设计要点：
 *   - 仅前端内部使用，**masterId 仍为后端真实主键**，传业务端点用 masterId
 *   - `code` 为物料业务编码（如 ASSY-10-AF20P-060S）；同名/同名相似场景由后端 SQL 排序处理
 *   - `isActive=false` 行**默认过滤**（后端 WHERE IsActive=1），前端不感知
 *   - 与既有 opm.ts / domain.ts 范式一致（readonly interface + Index re-export）
 */

export interface MaterialLookupItem {
  /** 物料主键（后端真实 Id，传业务端点用此字段） */
  masterId: number
  /** 物料业务编码（用户视觉唯一标识；如 ASSY-10-AF20P-060S） */
  code: string
  /** 物料中文名（如 滤芯） */
  name: string
  /** 规格（如 10-AF20P-060S） */
  spec: string
  /** 单位（如 PS） */
  uom: string
  /** 是否启用（后端 WHERE IsActive=1 过滤后此字段恒 true，前端用于兼容兜底） */
  isActive: boolean
}
