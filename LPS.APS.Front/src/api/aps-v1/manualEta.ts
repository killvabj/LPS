/**
 * APS V1 4号位 — 人工到货时间（Manual ETA）API
 *
 * @owner 5号位自属（Manual ETA 业务事实，4号位 → 5号位 链路）
 * @see 审核报告 §十六.4↔5 + §十一 P1-7
 *
 * @see 4号位文档第 11 节（页面 8 — Manual ETA）
 * @see 5号位 `lps/LPS.APS.Web/Controllers/ProcurementManualEtaController.cs`
 * 验收：P1-7（审核报告第 11 节）
 *
 * 接口路径（与 5 号位 Controller 一致）：
 *  - GET    /api/procurement-manual-eta                          列表（Query 筛选，含 bucket=overdue|dueSoon）
 *  - GET    /api/procurement-manual-eta/summary                  指标条五数聚合（11-13 落码，回带日界）
 *  - GET    /api/procurement-manual-eta/facets                   筛选候选（11-13 落码）
 *  - GET    /api/procurement-manual-eta/materials                抽屉物料候选（11-14 落码，可选 poNo/lineNo 收窄）
 *  - GET    /api/procurement-manual-eta/supply-combos           订单行供货组合（物料 × 存储地点**成对**，11-14 落码 r14214~r14218）
 *  - GET    /api/procurement-manual-eta/{poNo}/{lineNo}          单条
 *  - POST   /api/procurement-manual-eta                          Upsert
 *  - DELETE /api/procurement-manual-eta/{poNo}/{lineNo}?actor=   取消（isActive=false）
 *
 * 4号位职责边界：
 *  - 不计算 Effective ETA / AvailableTime
 *  - 取消走"软删除"isActive=false，不硬删
 *
 * ❌ 2026-09-15 撤销 DepartmentCode 维度（@see 4号位-2026-09-13-裁定回退清单.md）：
 *  - 9月13日 `未命名的Markdown文件.md` 实际是 0号位 出的业务裁决（程序有效）
 *  - DepartmentCode 整条撤销：mock 数据 + filter 块 + 前端 4 处 UI + verify-integration [F] 段
 *  - 当前状态：与 9月13日 撤销状态一致
 */

import { apsHttp, APS_USE_MOCK } from './http'
import type {
  ManualEtaDto,
  ManualEtaFacets,
  ManualEtaListFilter,
  ManualEtaSummary,
  ManualEtaSupplyCombo,
  ManualEtaUpsertInput,
  ManualEtaCancelInput,
  ManualEtaPageResult,
  WarehouseOption
} from './types/manualEta'
import type { MaterialLookupItem } from './types/material'

const iso = (ms: number) => new Date(Date.now() + ms).toISOString()
const ACTOR_MOCK = 'mock-pmc'

/* ===== Mock 数据：8 条记录（覆盖 INJECTION/MACHINING/ASSEMBLY 域 + 1 条已取消） ===== */
const MOCK_ETAS: ManualEtaDto[] = [
  {
    poNo: 'PO-2026-1001',
    lineNo: 1,
    materialId: 200,
    materialCode: 'M-001',
    materialName: '电机壳体-A',
    receivingWarehouse: 'WH-SUZ-01',
    manualEta: iso(5 * 86400_000),
    isActive: true,
    updatedBy: 'pmc01',
    updatedAt: iso(-2 * 86400_000),
    createdBy: 'pmc01',
    createdAt: iso(-30 * 86400_000),
    remark: '供应商口头承诺 9 月初'
  },
  {
    poNo: 'PO-2026-1001',
    lineNo: 2,
    materialId: 201,
    materialCode: 'M-002',
    materialName: '机柜上盖-B',
    receivingWarehouse: 'WH-SUZ-01',
    manualEta: iso(7 * 86400_000),
    isActive: true,
    updatedBy: 'pmc01',
    updatedAt: iso(-1 * 86400_000),
    remark: '二次确认 ETA'
  },
  {
    poNo: 'PO-2026-1002',
    lineNo: 1,
    materialId: 202,
    materialCode: 'M-003',
    materialName: '控制箱-C',
    receivingWarehouse: 'WH-CDG-02',
    manualEta: iso(-1 * 86400_000), // 已过期（演示异常态）
    isActive: true,
    updatedBy: 'pmc02',
    updatedAt: iso(-3 * 86400_000),
    remark: '供应商延迟发货，正在催交'
  },
  {
    poNo: 'PO-2026-1003',
    lineNo: 1,
    materialId: 203,
    materialCode: 'M-004',
    materialName: '标准泵-D',
    receivingWarehouse: 'WH-SUZ-01',
    manualEta: iso(10 * 86400_000),
    isActive: true,
    updatedBy: 'pmc01',
    updatedAt: iso(-5 * 86400_000),
    remark: ''
  },
  {
    poNo: 'PO-2026-1004',
    lineNo: 1,
    materialId: 204,
    materialCode: 'M-005',
    materialName: '包装套件-E',
    receivingWarehouse: 'WH-CDG-02',
    manualEta: iso(15 * 86400_000),
    isActive: false, // 已取消
    updatedBy: 'pmc02',
    updatedAt: iso(-1 * 86400_000),
    remark: '改用替代供应商，保留记录便于审计'
  },
  {
    poNo: 'PO-2026-1005',
    lineNo: 1,
    materialId: 205,
    materialCode: 'M-006',
    materialName: '机架焊接件-F',
    receivingWarehouse: 'WH-SUZ-01',
    manualEta: iso(3 * 86400_000),
    isActive: true,
    updatedBy: 'pmc01',
    updatedAt: iso(-12 * 3600_000),
    remark: '走加急通道'
  },
  {
    poNo: 'PO-2026-1006',
    lineNo: 1,
    materialId: 206,
    materialCode: 'M-007',
    materialName: '电路板组件-G',
    receivingWarehouse: 'WH-SUZ-01',
    manualEta: iso(20 * 86400_000),
    isActive: true,
    updatedBy: 'pmc02',
    updatedAt: iso(-7 * 86400_000),
    remark: ''
  },
  {
    poNo: 'PO-2026-1007',
    lineNo: 1,
    materialId: 207,
    materialCode: 'M-001',
    materialName: '电机壳体-A',
    receivingWarehouse: 'WH-CDG-02',
    manualEta: iso(8 * 86400_000),
    isActive: true,
    updatedBy: 'pmc01',
    updatedAt: iso(-4 * 86400_000),
    remark: '跨工厂调拨'
  }
]

/* ===== Mock 辅助：仓库下拉候选（5号位 ext_MES_ProcessCode_View 离线快照） ===== */
const MOCK_WAREHOUSES: WarehouseOption[] = [
  { code: 'WH-SUZ-01', name: '苏州主仓 1 号库' },
  { code: 'WH-SUZ-02', name: '苏州主仓 2 号库' },
  { code: 'WH-CDG-02', name: '成都工厂仓' }
]

/* ===== Mock 辅助：筛选 ===== */
function applyFilter(list: ManualEtaDto[], filter?: ManualEtaListFilter): ManualEtaDto[] {
  if (!filter) return [...list]
  let result = [...list]
  if (filter.activeOnly) result = result.filter((x) => x.isActive)
  if (filter.cancelledOnly) result = result.filter((x) => !x.isActive)
  if (filter.poNos?.length) result = result.filter((x) => filter.poNos!.includes(x.poNo))
  if (filter.materialIds?.length)
    result = result.filter((x) => filter.materialIds!.includes(x.materialId))
  if (filter.materialCodes?.length)
    result = result.filter((x) => filter.materialCodes!.includes(x.materialCode))
  if (filter.receivingWarehouses?.length)
    result = result.filter((x) => filter.receivingWarehouses!.includes(x.receivingWarehouse ?? ''))
  // 到货时间边界：与真实端点同一口径（闭区间，含两端）
  if (filter.etaAfter)
    result = result.filter(
      (x) => new Date(x.manualEta).getTime() >= new Date(filter.etaAfter!).getTime()
    )
  if (filter.etaBefore)
    result = result.filter(
      (x) => new Date(x.manualEta).getTime() <= new Date(filter.etaBefore!).getTime()
    )
  // 时间桶：mock 无服务器日界，按本机墙钟算与后端同一口径（overdue 严格早于今日00:00；dueSoon 半开 [今日00:00, +7d)）
  const bucket = filter.bucket
  if (bucket) {
    const day = new Date()
    day.setHours(0, 0, 0, 0)
    const soon = new Date(day)
    soon.setDate(soon.getDate() + 7)
    result = result.filter((x) => {
      if (!x.isActive) return false
      const t = new Date(x.manualEta).getTime()
      return bucket === 'overdue' ? t < day.getTime() : t >= day.getTime() && t < soon.getTime()
    })
  }
  return result
}

/** 真实模式序列化：把数组转成逗号分隔字符串（后端 Query 参数约定）
 *  @see lps/LPS.APS.Web/Controllers/ProcurementManualEtaController.cs L46-91
 *  后端用 [FromQuery] string? + Split(',') 拆，axios 默认会把数组序列化为 materialIds[]=1&materialIds[]=2 → 404
 *
 *  ⚠️ activeOnly 默认值修正（2026-10-07）：
 *   - 后端 controller 默认 activeOnly=true（只返回 isActive=true），过滤掉 cancelled
 *   - 前端 UI「仅看生效中」开关默认 false（看全部）；前端必须显式传 false 覆盖后端默认
 *   - 否则：刷新页面 / 初次加载 → cancelled 记录被静默过滤 → 列表显示空态（KPI 仍有 cancelledCount）
 *   - 修复：activeOnly 总被显式序列化（filter 缺省值 / undefined → false）
 */
function serializeFilter(filter?: ManualEtaListFilter): Record<string, unknown> {
  const out: Record<string, unknown> = {
    activeOnly: filter?.activeOnly ?? false,
    // 5号位 2026-10-08 分页：总是显式传 pageIndex / pageSize（缺省 1/20，后端硬上限 200）
    pageIndex: filter?.pageIndex ?? 1,
    pageSize: filter?.pageSize ?? 20
  }
  if (filter?.poNos?.length) out.poNos = filter.poNos.join(',')
  if (filter?.materialIds?.length) out.materialIds = filter.materialIds.join(',')
  if (filter?.materialCodes?.length) out.materialCodes = filter.materialCodes.join(',')
  // 11-12 工厂视角改版：接上后端**早就支持、但前端一直没用**的三个筛选参数
  //  - receivingWarehouses（收货仓库）/ etaAfter / etaBefore（到货时间区间）
  //  ⇒ 零后端改动就能让计划员按「哪批货、到哪个仓、什么时候到」挑单
  if (filter?.receivingWarehouses?.length)
    out.receivingWarehouses = filter.receivingWarehouses.join(',')
  if (filter?.etaAfter) out.etaAfter = filter.etaAfter
  if (filter?.etaBefore) out.etaBefore = filter.etaBefore
  // 11-13：5号位补「仅已取消」参数（cancelledOnly，与 activeOnly 互斥、cancelledOnly 优先）
  if (filter?.cancelledOnly) out.cancelledOnly = true
  // 11-13：5号位补排序参数（白名单 manualEta/updatedAt/poNo/materialCode；不传=原排序）
  //  ⚠️ 非法值后端真 400 ⇒ 前端只允许白名单值下发
  if (filter?.sortBy) out.sortBy = filter.sortBy
  if (filter?.sortDir) out.sortDir = filter.sortDir
  // 11-14：5号位 R6 补时间桶参数（SVN r14181～r14185 批次；桶判定式在 r14183 的 Repository 件）——
  //  日界由服务器 GETDATE() 说了算，与 /summary 的两个桶计数同一段 WHERE 文本 ⇒ 指标条数字与列表行数必然相等
  //  ⚠️ 非法值后端真 400（"非法 bucket"）；与 cancelledOnly 同传后端也真 400 ⇒ 前端不同时传
  //  ⚠️ 取值白名单现封 {overdue, dueSoon}（5号位 11-14 复函 Q4）——扩桶须先书面知会 + 前端白名单同步
  //  ⚠️ 口径（5号位 11-14 复函 Q2）：bucket 与 etaAfter/etaBefore 同传 = **保留 AND 交集、不 400**，
  //    但**分类点击态不得叠时间窗**——指标条的桶数出自 /summary（不叠列表时间窗），叠了就会与列表对不上；
  //    将来页面若要「桶上叠窗」，必须同时显示两个条件。
  if (filter?.bucket) out.bucket = filter.bucket
  // 11-14：5号位 v2.0 R2 取乙 → 列表可回带 isMatched 标注（后端默认 false，要标注须显式开）
  if (filter?.withMatched) out.withMatched = true
  return out
}

/**
 * summary 专用序列化（11-13：5号位 `GET /api/procurement-manual-eta/summary`）
 *  - **不传** activeOnly / cancelledOnly：summary 恒跨两态统计（后端短路状态开关），
 *    传了也只是被忽略，前端不传以免口径误解
 *  - **不传** pageIndex / pageSize：summary 返回的是聚合计数，不分页
 */
function serializeSummaryFilter(filter?: ManualEtaListFilter): Record<string, unknown> {
  const out: Record<string, unknown> = {}
  if (filter?.poNos?.length) out.poNos = filter.poNos.join(',')
  if (filter?.materialCodes?.length) out.materialCodes = filter.materialCodes.join(',')
  if (filter?.receivingWarehouses?.length)
    out.receivingWarehouses = filter.receivingWarehouses.join(',')
  if (filter?.etaAfter) out.etaAfter = filter.etaAfter
  if (filter?.etaBefore) out.etaBefore = filter.etaBefore
  return out
}

export const manualEtaApi = {
  /** 列表（支持 materialIds / poNos / activeOnly 筛选 + 5号位 2026-10-08 分页）
   *  - 真实端点返 ManualEtaPageResult<ManualEtaDto>（@see ProcurementManualEtaController.cs）
   *  - mock 阶段简化：直接返回数组（store 层兼容两种形态）
   */
  async list(
    filter?: ManualEtaListFilter
  ): Promise<ManualEtaDto[] | ManualEtaPageResult<ManualEtaDto>> {
    if (APS_USE_MOCK) {
      await new Promise((resolve) => setTimeout(resolve, 250))
      return applyFilter(MOCK_ETAS, filter)
    }
    return apsHttp.get<ManualEtaPageResult<ManualEtaDto>>({
      url: '/api/procurement-manual-eta',
      params: serializeFilter(filter)
    })
  },

  /**
   * 指标条五数（11-13：5号位落码 `GET /api/procurement-manual-eta/summary`）
   *  - 一次请求给全 全部 / 生效中 / 已取消 / 已过期 / 7 天内到期
   *    （旧口径是 1 列表 + 4 个 countOnly = 5 个请求凑这五个数）
   *  - WHERE 与列表**同常量单源** ⇒ 指标与列表不可能对不上
   *  - summary 恒跨两态统计 ⇒ 调用方不要传 activeOnly / cancelledOnly
   */
  async summary(filter?: ManualEtaListFilter): Promise<ManualEtaSummary> {
    if (APS_USE_MOCK) {
      await new Promise((resolve) => setTimeout(resolve, 120))
      const rows = applyFilter(MOCK_ETAS, filter)
      const today = new Date()
      today.setHours(0, 0, 0, 0)
      const active = rows.filter((x) => x.isActive)
      return {
        allCount: rows.length,
        activeCount: active.length,
        cancelledCount: rows.length - active.length,
        overdueCount: active.filter((x) => new Date(x.manualEta).getTime() < today.getTime())
          .length,
        dueSoonCount: active.filter((x) => {
          const t = new Date(x.manualEta).getTime()
          const in7 = today.getTime() + 7 * 86400000
          return t >= today.getTime() && t < in7
        }).length
      }
    }
    return apsHttp.get<ManualEtaSummary>({
      url: '/api/procurement-manual-eta/summary',
      params: serializeSummaryFilter(filter)
    })
  },

  /**
   * 筛选候选 facets（11-13：5号位落码 `GET /api/procurement-manual-eta/facets?search=`）
   *  - 旧实现靠「拉 pageSize=200 全量扫描自己派生」⇒ 超过 200 条的采购单在下拉里选不到
   *  - 新实现由后端出本表 distinct 候选（每维 TOP 500 + truncated 标志）
   *  - 数据源 = 本表（**不是**主数据全集）：候选的意义 = 能筛到记录
   */
  async facets(search?: string): Promise<ManualEtaFacets> {
    if (APS_USE_MOCK) {
      await new Promise((resolve) => setTimeout(resolve, 120))
      const term = (search ?? '').trim().toLowerCase()
      const hit = (s: string) => (term ? s.toLowerCase().includes(term) : true)
      return {
        poNos: Array.from(new Set(MOCK_ETAS.map((x) => x.poNo))).filter(hit),
        materialCodes: Array.from(new Set(MOCK_ETAS.map((x) => x.materialCode))).filter(hit),
        truncated: false
      }
    }
    return apsHttp.get<ManualEtaFacets>({
      url: '/api/procurement-manual-eta/facets',
      params: { search: search ?? '' }
    })
  },

  /**
   * 抽屉「选物料」候选：远程搜索（11-14 改走**乙端点**）
   *
   *  - 端点：`GET /api/procurement-manual-eta/materials?search=&poNo=&lineNo=&limit=`
   *    （5号位 11-14 落码，SVN r14206～r14208；挂**本域** `aps.manual_eta.view`
   *    ⇒ 本页不再依赖策略域的 `aps.setup.view`，3号位 11-13 裁乙的权限耦合已解）
   *  - 返回 `MaterialLookupItem`（masterId/code/name/spec/uom/isActive），与共用端点**同结构**
   *    ⇒ 前端接口层切换零形状变化。
   *  - `search` **可空**（空 = 不按关键字过滤）：
   *    dev 11-14 实测 `?search=&poNo=CC44390&limit=50` → 6 行（带中文名 + 规格 + 单位）；
   *    `?poNo=CC44390&lineNo=3` → 1 行；`?poNo=P01`（种子 PO）→ **200 + 0 行**（不报错）；
   *    `?search=ASSY` → 按码升序；`?search=铝&poNo=CC44390` → 5 行（search 与 poNo 是 AND）。
   *  - `poNo` 传 = 收窄到该采购订单的**物料集合**（供给事实 ∪ ERP 收货视图，按 materialId 去重）；
   *    `lineNo` 传 = 再收窄到该行。
   *    ⚠️ 端点**不按工厂范围裁剪**（5号位按我方 R1.1 落码）⇒ 受限账号不会被挡掉合法候选。
   *  - 查无 = **200 + 空集合**（不 400/404）⇒ 收窄**不阻断录入**，由前端负责逐级放宽。
   *  - ⚠️ 本函数**不会**在「无关键字、无 poNo」时去要全量物料（那等于把全库物料拉回来列全）：
   *    这种情况下直接返空，由视图提示用户去搜索。
   */
  async searchMaterials(
    search: string,
    limit = 50,
    scope?: { poNo?: string; lineNo?: number }
  ): Promise<MaterialLookupItem[]> {
    const q = String(search ?? '').trim()
    const poNo = String(scope?.poNo ?? '').trim()
    const lineNo = Number(scope?.lineNo)
    if (!q && !poNo) return []
    const params: Record<string, unknown> = { search: q, limit }
    if (poNo) params.poNo = poNo
    if (poNo && Number.isFinite(lineNo) && lineNo > 0) params.lineNo = lineNo
    return apsHttp.get<MaterialLookupItem[]>({
      url: '/api/procurement-manual-eta/materials',
      params,
      silent: true
    })
  },

  /**
   * 订单行供货组合（**11-14 接 5号位 v2.0 R1 落码，SVN r14214～r14218**）
   *  - 端点：`GET /api/procurement-manual-eta/supply-combos?poNo=&lineNo=&limit=`
   *    （挂本域 `aps.manual_eta.view`；`poNo` **必填**——缺 ⇒ 后端 400 + 中文原因；
   *      `lineNo` 可选；`limit` 默认 200、钳 [1,500]）
   *  - 一行 = 供给事实里一条 **(物料 × 存储地点)** 组合，**成对**给出
   *  - ⚠️ 为什么抽屉的「按订单收窄档」必须用它：人工到货时间是**对供给事实的覆盖**，
   *    覆盖键 `(PONo, LineNo, MaterialId, ReceivingWarehouse)` ≡ 供给事实
   *    `(SourceDocumentNo, SourceDocumentLineNo, MaterialId, StorageCode)`
   *    （@see AvailableTimeCalculator.cs:63-73,105-107）⇒ 物料与仓库各选各的必然拼不中，
   *    这条到货时间会**静默不生效**（不报错、不提示）。
   *  - 只含**能真正生效**的组合（与装载侧 TimedSupplyFactLoader 同一资格谓词：
   *    有效 + 数量>0 + 五类采购在途）；行号非整数的事实不返回（覆盖键要求 int 行号）。
   *  - 查无 = **200 + 空数组**（收窄不阻断录入）。
   */
  async searchSupplyCombos(
    poNo: string,
    lineNo?: number,
    limit = 200
  ): Promise<ManualEtaSupplyCombo[]> {
    const po = String(poNo ?? '').trim()
    if (!po) return []
    const params: Record<string, unknown> = { poNo: po, limit }
    const ln = Number(lineNo)
    if (Number.isFinite(ln) && ln > 0) params.lineNo = ln
    return apsHttp.get<ManualEtaSupplyCombo[]>({
      url: '/api/procurement-manual-eta/supply-combos',
      params,
      silent: true
    })
  },

  /**
   * 收货仓库下拉（5号位 ManualETA 控制器内置；端点：GET /api/procurement-manual-eta/warehouses?search=）
   *  - 偏离原 `/api/governance/lookups/warehouses` 提案；按 5号位 2026-10-08 落地回执对接
   *  - 复用 ManualEtaView 权限
   *  - 返回 `[{ code, name }]`；search 模糊 code/name
   */
  async listWarehouses(search?: string): Promise<WarehouseOption[]> {
    if (APS_USE_MOCK) {
      await new Promise((resolve) => setTimeout(resolve, 150))
      const term = search?.trim().toLowerCase()
      if (!term) return [...MOCK_WAREHOUSES]
      return MOCK_WAREHOUSES.filter(
        (w) => w.code.toLowerCase().includes(term) || w.name.toLowerCase().includes(term)
      )
    }
    return apsHttp.get<WarehouseOption[]>({
      url: '/api/procurement-manual-eta/warehouses',
      params: { search: search ?? '' }
    })
  },

  /** 单条：按 poNo + lineNo 复合主键 */
  async getByPoLine(poNo: string, lineNo: number): Promise<ManualEtaDto | null> {
    if (APS_USE_MOCK) {
      await new Promise((resolve) => setTimeout(resolve, 150))
      return MOCK_ETAS.find((x) => x.poNo === poNo && x.lineNo === lineNo) ?? null
    }
    return apsHttp.get<ManualEtaDto>({
      url: `/api/procurement-manual-eta/${encodeURIComponent(poNo)}/${lineNo}`
    })
  },

  /**
   * Upsert：poNo + lineNo 复合主键去重
   *  - 后端 ProcurementManualEtaController.Upsert 接收 [FromBody] ProcurementManualEtaOverride
   *    必填 MaterialId/ReceivingWarehouse/ManualEta/IsActive/UpdatedBy/CreatedBy/Remark
   *    （@see lps/LPS.APS.Web/Controllers/ProcurementManualEtaController.cs L150-169）
   *  - 前端 upsert payload 字段映射：
   *      actor → updatedBy
   *      新增时用 actor 作 createdBy（首次创建）
   *  - MaterialId/ReceivingWarehouse 由前端 Dialog 收集（已知 PO 行时直接取，已列表存在行时取列表值）
   */
  async upsert(input: ManualEtaUpsertInput): Promise<ManualEtaDto> {
    if (APS_USE_MOCK) {
      await new Promise((resolve) => setTimeout(resolve, 300))
      const idx = MOCK_ETAS.findIndex((x) => x.poNo === input.poNo && x.lineNo === input.lineNo)
      const now = new Date().toISOString()
      const next: ManualEtaDto = {
        poNo: input.poNo,
        lineNo: input.lineNo,
        materialId: input.materialId || 999,
        materialCode: input.materialCode ?? 'M-?',
        manualEta: input.manualEta,
        isActive: input.isActive ?? true,
        updatedBy: input.actor,
        updatedAt: now,
        remark: input.remark
      }
      if (idx >= 0) {
        // 保留 createdBy/createdAt/materialName
        next.materialCode = MOCK_ETAS[idx].materialCode
        next.materialName = MOCK_ETAS[idx].materialName
        next.receivingWarehouse =
          input.receivingWarehouse || MOCK_ETAS[idx].receivingWarehouse || ''
        next.createdBy = MOCK_ETAS[idx].createdBy
        next.createdAt = MOCK_ETAS[idx].createdAt
        MOCK_ETAS[idx] = next
      } else {
        next.receivingWarehouse = input.receivingWarehouse || ''
        next.createdBy = input.actor
        next.createdAt = now
        MOCK_ETAS.push(next)
      }
      return next
    }
    // 真实模式：直接传 ProcurementManualEtaOverride 形态（前后端 DTO 字段命名差异按 ASP.NET 默认约定）
    //  - 后端用 PascalCase 接收（ProcurementManualEtaOverride.PONo/LineNo/MaterialId/...）
    //  - axios 默认按字段名直接序列化（不改大小写），所以需要前端 payload 用 PascalCase key
    //    但 ManualEtaUpsertInput 是 camelCase；手动映射避免反序列化器属性丢失
    const payload = {
      PONo: input.poNo,
      LineNo: input.lineNo,
      MaterialId: input.materialId,
      MaterialCode: input.materialCode ?? '',
      ReceivingWarehouse: input.receivingWarehouse,
      ManualEta: input.manualEta,
      IsActive: input.isActive ?? true,
      UpdatedBy: input.actor,
      Remark: input.remark ?? ''
    }
    return apsHttp.post<ManualEtaDto>({
      url: '/api/procurement-manual-eta',
      data: payload
    })
  },

  /**
   * 取消：isActive=false（软删除）
   *  - 保留记录便于审计（4号位文档第 11 节硬约束）
   *  - 后端 Cancel 端点 [FromBody] CancelManualEtaRequest 必填 MaterialId/ReceivingWarehouse/UpdatedBy/Reason?
   *    （@see ProcurementManualEtaController.cs L175-237）
   *  - materialId/receivingWarehouse 由调用方从当前行数据传入（已在 list 中）
   */
  async cancel(input: ManualEtaCancelInput): Promise<ManualEtaDto> {
    if (APS_USE_MOCK) {
      await new Promise((resolve) => setTimeout(resolve, 250))
      const idx = MOCK_ETAS.findIndex((x) => x.poNo === input.poNo && x.lineNo === input.lineNo)
      if (idx < 0) {
        throw new Error(`未找到 Manual ETA：${input.poNo} / Line ${input.lineNo}`)
      }
      MOCK_ETAS[idx] = {
        ...MOCK_ETAS[idx],
        isActive: false,
        updatedBy: input.actor,
        updatedAt: new Date().toISOString()
      }
      return MOCK_ETAS[idx]
    }
    return apsHttp.delete<ManualEtaDto>({
      url: `/api/procurement-manual-eta/${encodeURIComponent(input.poNo)}/${input.lineNo}`,
      data: {
        MaterialId: input.materialId,
        ReceivingWarehouse: input.receivingWarehouse,
        UpdatedBy: input.actor
      }
    })
  }
}

/** 默认 actor（mock 阶段简化；真实接入时从 apsAuth.userInfo 取） */
export const mockManualEtaActor = ACTOR_MOCK
