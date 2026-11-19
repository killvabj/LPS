/**
 * Manual ETA DTO（与 5 号位 `lps/LPS.APS.Core/Dto/ProcurementManualEtaOverride.cs` 对齐）
 *
 * 来源：5号位 `ProcurementManualEtaController`（4号位 → 5号位）
 *  - GET    /api/procurement-manual-eta        Query，支持 materialIds / poNos / activeOnly
 *  - GET    /api/procurement-manual-eta/{poNo}/{lineNo}
 *  - POST   /api/procurement-manual-eta        Upsert
 *  - DELETE /api/procurement-manual-eta/{poNo}/{lineNo}?actor=
 *
 * 4号位职责（@see 4号位文档第 11 节 / 审核报告 P1-7）：
 *  - 查询 / 新增 / 更新 / 取消
 *  - Active 状态 / UpdatedBy / UpdatedAt / Remark
 *  - 不计算 有效到货时间（Effective ETA）/ 最终可用时间（AvailableTime）
 *
 * 复合主键：poNo + lineNo
 *
 * ❌ 2026-09-15 撤销 DepartmentCode 维度（@see 4号位-2026-09-13-裁定回退清单.md）：
 *  - 9月13日 `未命名的Markdown文件.md` 实际是 0号位 出的业务裁决（程序有效）
 *  - DepartmentCode 整条撤销：DTO + 5号位 Repository/Controller/Scope + 前端 4 处 UI
 *  - 当前状态：与 9月13日 撤销状态一致
 */

import type { IsoDateTime } from './common'

/** Manual ETA 单条（与 5号位 `ProcurementManualEtaOverride` 字段一一对应） */
export interface ManualEtaDto {
  /** 采购订单号（主键之一） */
  poNo: string
  /** PO 行号（主键之一） */
  lineNo: number
  /** 物料 ID（5号位推断；前端展示用） */
  materialId: number
  /** 物料编码 */
  materialCode: string
  /** 物料名称（前端增强字段；后端可选） */
  materialName?: string
  /** 收货仓库（前端增强字段；后端可选） */
  receivingWarehouse?: string
  /**
   * 人工设定的到货预期时间（5号位 ManualEta 注释："人工设定的到货预期时间"）
   *  - 仅人工值；有效到货时间由 5号位计算（4号位不计算）
   */
  manualEta: IsoDateTime
  /** 是否激活；取消 = false */
  isActive: boolean
  updatedBy: string
  updatedAt: IsoDateTime
  createdBy?: string
  createdAt?: IsoDateTime
  remark?: string
  /**
   * 是否命中当前供货事实（5号位 11-14 落码 r14214～r14218，**计算字段、不落库**）
   *  - `true` = 会参与排产；`false` = 命不中，**静默不生效**；`null` / 缺省 = **未判定**
   *    （不得当「未命中」显示——判定取数失败时后端给 null）
   *  - 列表端需显式开 `withMatched=true` 才有；详情 / Upsert 回包自带
   */
  isMatched?: boolean | null
}

/**
 * 订单行供货组合（5号位 11-14 落码 r14214～r14218）
 *  端点：`GET /api/procurement-manual-eta/supply-combos?poNo=&lineNo=&limit=`
 *
 *  一行 = 供给事实里一条 **(物料 × 存储地点)** 组合，**成对**给出。
 *  Why 必须成对：人工到货时间是**对供给事实的覆盖**——覆盖键
 *  `(PONo, LineNo, MaterialId, ReceivingWarehouse)` ≡ 供给事实
 *  `(SourceDocumentNo, SourceDocumentLineNo, MaterialId, StorageCode)`
 *  （@see lps/LPS.APS.Application/Services/AvailableTimeCalculator.cs:63-73,105-107），
 *  匹配不上 ⇒ 这条到货时间静默不生效（不报错、不提示）。
 *  ⇒ 抽屉必须用本响应同时锁定「物料 + 收货仓库」。
 */
export interface ManualEtaSupplyCombo {
  /** 采购订单号（= 供给事实 SourceDocumentNo） */
  poNo: string
  /** 行号（= 供给事实 SourceDocumentLineNo 的整数形态） */
  lineNo: number
  /** 物料 ID（= 覆盖键 MaterialId） */
  materialId: number
  /** 物料编码（主数据优先，主数据缺行回退事实码） */
  materialCode: string
  /** 物料中文名（主数据缺行 ⇒ null，前端回退编码） */
  materialName?: string | null
  /** 规格 */
  spec?: string | null
  /** 单位 */
  uom?: string | null
  /** 存储地点（= 供给事实 StorageCode，**必须原样写入表单的「收货仓库」**） */
  storageCode: string
  /** 存储地点名（ext_MES_ProcessCode_View 无此码 ⇒ null，前端回退编码） */
  storageName?: string | null
}

/** 列表筛选条件（与 5号位 Controller Query 参数对齐） */
export interface ManualEtaListFilter {
  poNos?: string[]
  materialIds?: number[]
  /**
   * 物料编码筛选（10-07 ME10 修复：UI 物料下拉选 code → 后端 ?materialCodes= 逗号分隔）
   *  - 后端 `ProcurementManualEtaController.cs:47` 接受 `?materialCodes=` 逗号分隔字符串
   *  - 与 materialIds 二选一：UI 选 code（语义友好），materialIds 保留供后续按需扩展
   */
  materialCodes?: string[]
  /**
   * 收货仓库筛选（11-12 工厂视角改版接上既有后端参数）
   *  - 后端 `ProcurementManualEtaController.cs:50` 接受 `?receivingWarehouses=` 逗号分隔
   *  - 仓库候选来自 `GET /api/procurement-manual-eta/warehouses`（真实 ext_MES_ProcessCode_View）
   */
  receivingWarehouses?: string[]
  /**
   * 到货时间下界（含）：`ManualEta >= etaAfter`
   *  - 后端 `ProcurementManualEtaController.cs:51`
   *  - ⚠️ 传**本地墙钟**串（不带 Z / 不带时区偏移）：后端 ManualEta 落库就是无时区墙钟，
   *    带 Z 会被 ASP.NET 当 UTC 解释 ⇒ 与库里存的墙钟不是同一个基准。
   */
  etaAfter?: string
  /**
   * 到货时间上界（含）：`ManualEta <= etaBefore`
   *  - 后端 `ProcurementManualEtaController.cs:52`
   *  - 同样是本地墙钟串（不带 Z）。
   */
  etaBefore?: string
  /**
   * 时间桶（11-14 接 5号位 R6 落码，SVN r14181～r14185 批次）：`?bucket=overdue|dueSoon`
   *  - 日界 = **服务器 GETDATE() 日界**（工厂墙钟），两个桶的判定式与 `/summary` 的
   *    overdueCount / dueSoonCount **同一段 WHERE 文本**（后端类级常量 OverdueExpr / DueSoonExpr）
   *    ⇒ 指标条数字与列表行数必然相等（前端不再自己算日界）
   *  - 与 `cancelledOnly` 同传 ⇒ 后端真 400（桶自带 IsActive=1，与「只看已取消」矛盾）
   *  - 与 `etaAfter` / `etaBefore` 同传 = **保留 AND 交集、不 400**（5号位 11-14 复函 Q2）；
   *    但**分类点击态不得叠时间窗**（指标条桶数出自 /summary，不叠列表时间窗）
   *  - 取值白名单**现封两值**（5号位 11-14 复函 Q4）：扩桶须先书面知会 + 前端白名单同步
   */
  bucket?: ManualEtaTimeBucket
  /** 仅返回 isActive=true */
  activeOnly?: boolean
  /**
   * 仅返回 isActive=false（已取消）
   *  - 5号位 2026-11-12 落码（`cancelledOnly`）：与 `activeOnly` 互斥，**cancelledOnly 优先**
   *  - 语义 = 软删（保留备查），后端无物理删除路径
   */
  cancelledOnly?: boolean
  /**
   * 排序字段（5号位 2026-11-12 落码白名单）
   *  - 不传 = 保持后端原排序（`UpdatedAt DESC, PONo, [LineNo]`）逐字不变
   *  - 传非法值 ⇒ 后端真 400（服务层 + 仓储层双校验）
   */
  sortBy?: ManualEtaSortBy
  /** 排序方向 */
  sortDir?: 'asc' | 'desc'
  /** 分页：1 基页码（5号位 2026-10-08 分页落地） */
  pageIndex?: number
  /** 分页：每页条数；后端硬上限 200 */
  pageSize?: number
  /**
   * 是否回带 `isMatched` 标注（5号位 11-14 v2.0 R2 取乙；**后端默认 false**）
   *  - 标注要多扫一遍供给事实表（实库 42.7 万行、SourceDocumentNo 无索引，实测 ≈273ms）
   *  - 本页要展示「是否参与排产」⇒ 列表恒传 true
   */
  withMatched?: boolean
}

/**
 * 排序字段白名单（5号位 `ProcurementManualEtaService` 白名单，2026-11-12 落码）
 *  - manualEta：到货时间 · updatedAt：最后更新时间 · poNo：采购订单号 · materialCode：物料编码
 *  - 传白名单外的值 ⇒ 后端真 400
 */
export type ManualEtaSortBy = 'manualEta' | 'updatedAt' | 'poNo' | 'materialCode'

/**
 * 时间桶白名单（5号位 R6，2026-11-13 落码 / SVN r14181～r14185 批次）
 *  - overdue：生效中且到货时间**严格早于**服务器今日 00:00
 *  - dueSoon：生效中且 今日00:00 ≤ 到货时间 < 今日+7天00:00（半开）
 *  - 白名单**现封这两值**（5号位 11-14 复函 Q4：不会静默扩桶）
 */
export type ManualEtaTimeBucket = 'overdue' | 'dueSoon'

/**
 * Upsert 输入（POST /api/procurement-manual-eta）
 *  - 5号位通过 poNo + lineNo 复合主键判定 新增 / 更新
 *  - 后端实体 ProcurementManualEtaOverride 必填 MaterialId/ReceivingWarehouse（@see lps/LPS.APS.Core/Dto/ProcurementManualEtaOverride.cs）
 *  - isActive 默认 true（新增时），更新时若省略则保留原值
 *  - actor → 后端 UpdatedBy（新增时若空则用 actor 作为 CreatedBy）
 */
export interface ManualEtaUpsertInput {
  poNo: string
  lineNo: number
  /** 物料 ID（后端必填；前端从已存在记录取，或由 PO 主数据接口获取） */
  materialId: number
  /** 物料编码（前端展示用，后端必填但允许重复） */
  materialCode?: string
  /** 收货仓库（后端必填） */
  receivingWarehouse: string
  manualEta: IsoDateTime
  isActive?: boolean
  remark?: string
  /** 当前操作人（→ 后端 UpdatedBy） */
  actor: string
}

/** 取消 ETA 输入（DELETE /api/procurement-manual-eta/{poNo}/{lineNo}）
 *  - 后端 [FromBody] CancelManualEtaRequest（@see ProcurementManualEtaController.cs L175-237）必填 materialId/receivingWarehouse/updatedBy
 *  - 4号位不"硬删除"：通过 isActive=false 实现取消
 *  - materialId/receivingWarehouse 从 list 当前行数据拉取（无需额外 GET）
 */
export interface ManualEtaCancelInput {
  poNo: string
  lineNo: number
  materialId: number
  receivingWarehouse: string
  /** 当前操作人（→ 后端 updatedBy） */
  actor: string
}

/** 收货仓库下拉项（5号位 ManualEta 控制器 GET /api/procurement-manual-eta/warehouses）
 *  - 数据源 ext_MES_ProcessCode_View（processcode → code / processname → name）
 *  - 复用 ManualEtaView 权限；不带 ID
 */
export interface WarehouseOption {
  code: string
  name: string
}

/** ManualETA 列表分页响应（5号位 2026-10-08 落地；@see ProcurementManualEtaController.cs）
 *  - 注意：刻意不复用 common.ts 的 PageResult<T>——5号位 契约字段为 `page`（与 RbacPageResult 同模式）
 *  - page 从 1 起；pageSize 默认 20，硬上限 200（@see §一）
 */
export interface ManualEtaPageResult<T> {
  items: T[]
  total: number
  page: number
  pageSize: number
}

/**
 * 指标条计数（11-13 起：后端 `/summary` 一次给全五个数）
 *  - 全部与列表同口径：同一组筛选条件 + 同一条 WHERE 语义
 *  - 已取消 = 后端 `cancelledCount`（5号位 2026-11-12 已补 `cancelledOnly` 参数 + summary 桶）
 */
export interface ManualEtaCounts {
  /** 全部记录（含已取消） */
  all: number
  /** 生效中 */
  active: number
  /** 已取消 */
  cancelled: number
  /** 生效中且已过到期日（到货时间早于今天 00:00） */
  overdue: number
  /** 生效中且今天 ~ 今天+6 天内到货 */
  dueSoon: number
}

/**
 * 后端 summary 响应（5号位 2026-11-12 落码 `GET /api/procurement-manual-eta/summary`）
 *  - 桶定义（与后端 DTO 注释同款）：active+cancelled=all 恒成立；三时间桶互斥且并集=active
 *  - overdue = 生效中 且 ManualEta < 今日00:00（**严格早于**）
 *  - dueSoon = 生效中 且 今日00:00 ≤ ManualEta < 今日+7天00:00（半开）
 *  - summary 恒跨两态统计（active/cancelled 开关在 summary 短路）⇒ 调用时**不要**传状态开关
 *  - 11-14 起随计数回带实际日界（overdueBefore / dueSoonFrom / dueSoonTo）
 */
export interface ManualEtaSummary {
  allCount: number
  activeCount: number
  cancelledCount: number
  overdueCount: number
  dueSoonCount: number
  /**
   * 后端实际使用的日界（11-14 起随桶计数一起回带，R6 同源化）
   *  - overdueBefore = overdue 桶上界（严格小于它）
   *  - dueSoonFrom / dueSoonTo = dueSoon 桶的半开区间
   *  - **正式契约**（5号位 11-14 复函 Q3）：恒返回、字段名不变、值为工厂墙钟（无 Z / 无偏移）；
   *    变更走正式契约变更通知。⚠️ 只可作**展示 / 文案锚定**，判定一律走 `bucket`
   */
  overdueBefore?: string
  dueSoonFrom?: string
  dueSoonTo?: string
}

/**
 * 筛选候选 facets（5号位 2026-11-12 落码 `GET /api/procurement-manual-eta/facets?search=`）
 *  - 数据源 = 本表 distinct（"有 ETA 记录的采购单/物料"，含已取消行），**不是**主数据全集
 *  - 每维 TOP 500；`truncated=true` 表示被截断
 */
export interface ManualEtaFacets {
  poNos: string[]
  materialCodes: string[]
  truncated: boolean
}

/**
 * 指标条可点分类（单选）——决定列表取哪一批
 *  - all：全部（含已取消）
 *  - active：生效中
 *  - cancelled：仅已取消（5号位 2026-11-12 补 `cancelledOnly` 后可点）
 *  - overdue：生效中且已过期
 *  - dueSoon：生效中且 7 天内到期
 */
export type ManualEtaBucket = 'all' | 'active' | 'cancelled' | 'overdue' | 'dueSoon'
