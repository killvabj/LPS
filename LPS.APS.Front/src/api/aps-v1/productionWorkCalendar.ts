/**
 * APS V1 4号位 — Production Work Calendar（工厂/部门上级工作日历）API（6 端点）
 *
 * 依据：
 *  - 冻结文档 v1.4《Resource Calendar资源日历能力补充冻结方案 v1.4（2026-10-12 三级继承最小增量候选）》
 *  - 5号位 2026-10-13《ProductionWorkCalendar三级日历维护接口文档 — 给4号位》
 *
 * 后端（lps/** 只读参照）：
 *  - Controller  lps/LPS.APS.Web/Controllers/ProductionWorkCalendarController.cs:31
 *      GET    /                       aps.resource_calendar.view
 *      POST   /weekly                 aps.resource_calendar.edit
 *      POST   /exception              aps.resource_calendar.edit
 *      POST   /close                  aps.resource_calendar.edit
 *      POST   /rows/{id}/close?effectiveTo= aps.resource_calendar.edit
 *      DELETE /rows/{id}              aps.resource_calendar.delete（仅未生效行）
 *  - Service     lps/LPS.APS.Application/Services/ProductionWorkCalendarService.cs
 *  - DTO         lps/LPS.APS.Application/Services/Dto/ProductionWorkCalendarDtos.cs
 *  - Entity      lps/LPS.APS.Core/Entities/Aps/ProductionWorkCalendarSlot.cs
 *
 * 治理：
 *  - 条款=G1 候选；G4 公告前本模块供联调/备料，真实班次数据写入等公告
 *  - 5号位 函 §〇.2 权限码：暂复用 aps.resource_calendar.{view,edit,delete}（G4 公告前；
 *    若 0号位 批独立码 aps.production_work_calendar.* 则仅换码不改契约）
 *  - 字段命名 camelCase（后端 Program.cs:93 PropertyNamingPolicy=CamelCase；5号位 函 §一明确）
 *
 * 已知缺口（v1.4 G1 候选期）：
 *  - D-CAL1~3 裁决关联（v1.4 顶部 §G1 审核边界声明"三级 Calendar 上级持久化为待审最小物理增量"）
 *  - 0 行时 1号位 ResourceCalendarResolver 走等价快路径（@see lps/.../ProductionWorkCalendarSlot.cs:27-29）
 *  - dev 现有 55 行 SourceSystem='TEST'（5号位 函 §六）：FACTORY 周基准 × 5厂 + 部门 963 + 例外夹具 5 条
 */

import { apsHttp, APS_USE_MOCK, ApiError } from './http'
import type {
  CalendarCloseOpenRequest,
  ProductionWorkCalendarDto,
  ProductionWorkCalendarPageResult,
  ProductionWorkCalendarQuery,
  WeeklyCalendarUpsertRequest,
  WorkCalendarExceptionRequest
} from './types/productionWorkCalendar'

/* ==================== Mock Fixtures（仅离线 UI 演示 / 联调备料）====================
 *  - 5号位 函 §六：dev 现有 55 行 SourceSystem='TEST'（FACTORY 周基准 + 部门 963 + 例外夹具 5 条）
 *  - 此处 mock 精简到 8 条代表性行（5 厂周基准抽 1 + 部门 963 + 2 例外夹具），供 UI 渲染
 */

const MOCK_PWC_ROWS: ProductionWorkCalendarDto[] = [
  // 工厂 2 (CN) 周一 WORK 08-17
  {
    id: 900001,
    scopeType: 'FACTORY',
    factoryId: 2,
    factoryCode: 'CN',
    weekdayIso: 1,
    workState: 'WORK',
    startAt: '08:00:00',
    endAt: '17:00:00',
    allowLowerOverride: false,
    isAuthorizedOverride: false,
    sourceSystem: 'TEST',
    effectiveFrom: '2026-10-13T00:00:00',
    isActive: true,
    createdAt: '2026-10-13T08:00:00',
    updatedAt: '2026-10-13T08:00:00'
  },
  // 工厂 2 周日 OFF 全天
  {
    id: 900002,
    scopeType: 'FACTORY',
    factoryId: 2,
    factoryCode: 'CN',
    weekdayIso: 7,
    workState: 'OFF',
    startAt: undefined,
    endAt: undefined,
    allowLowerOverride: false,
    isAuthorizedOverride: false,
    sourceSystem: 'TEST',
    effectiveFrom: '2026-10-13T00:00:00',
    isActive: true,
    createdAt: '2026-10-13T08:00:00',
    updatedAt: '2026-10-13T08:00:00'
  },
  // 部门 963 周一~四小夜
  {
    id: 900010,
    scopeType: 'DEPARTMENT',
    factoryId: 2,
    factoryCode: 'CN',
    productionDepartmentId: 963,
    departmentName: 'CN机加_制造2-1课731HL',
    weekdayIso: 1,
    workState: 'WORK',
    startAt: '17:00:00',
    endAt: '23:59:59',
    allowLowerOverride: false,
    isAuthorizedOverride: false,
    sourceSystem: 'TEST',
    effectiveFrom: '2026-10-13T00:00:00',
    isActive: true,
    createdAt: '2026-10-13T08:00:00',
    updatedAt: '2026-10-13T08:00:00'
  },
  // 部门 963 大夜（周二~五 00:00-08:00）
  {
    id: 900011,
    scopeType: 'DEPARTMENT',
    factoryId: 2,
    factoryCode: 'CN',
    productionDepartmentId: 963,
    departmentName: 'CN机加_制造2-1课731HL',
    weekdayIso: 2,
    workState: 'WORK',
    startAt: '00:00:00',
    endAt: '08:00:00',
    allowLowerOverride: false,
    isAuthorizedOverride: false,
    sourceSystem: 'TEST',
    effectiveFrom: '2026-10-13T00:00:00',
    isActive: true,
    createdAt: '2026-10-13T08:00:00',
    updatedAt: '2026-10-13T08:00:00'
  },
  // 部门 963 例外：11-18 部门停机
  {
    id: 900020,
    scopeType: 'DEPARTMENT',
    factoryId: 2,
    factoryCode: 'CN',
    productionDepartmentId: 963,
    departmentName: 'CN机加_制造2-1课731HL',
    workDate: '2026-11-18T00:00:00',
    workState: 'OFF',
    startAt: undefined,
    endAt: undefined,
    allowLowerOverride: false,
    isAuthorizedOverride: false,
    sourceSystem: 'TEST',
    sourceReference: '设备检修-2026-11-18',
    effectiveFrom: '2026-10-13T00:00:00',
    isActive: true,
    createdAt: '2026-10-13T08:00:00',
    updatedAt: '2026-10-13T08:00:00'
  },
  // 工厂 2 例外：11-22 厂休 + Alo=1（允许下级授权加班）
  {
    id: 900030,
    scopeType: 'FACTORY',
    factoryId: 2,
    factoryCode: 'CN',
    workDate: '2026-11-22T00:00:00',
    workState: 'OFF',
    startAt: undefined,
    endAt: undefined,
    allowLowerOverride: true,
    isAuthorizedOverride: false,
    sourceSystem: 'TEST',
    effectiveFrom: '2026-10-13T00:00:00',
    isActive: true,
    createdAt: '2026-10-13T08:00:00',
    updatedAt: '2026-10-13T08:00:00'
  },
  // 部门 963 例外：11-22 部门授权加班 WORK + Aao=1 + OA 证据
  {
    id: 900031,
    scopeType: 'DEPARTMENT',
    factoryId: 2,
    factoryCode: 'CN',
    productionDepartmentId: 963,
    departmentName: 'CN机加_制造2-1课731HL',
    workDate: '2026-11-22T00:00:00',
    workState: 'WORK',
    startAt: '08:00:00',
    endAt: '17:00:00',
    allowLowerOverride: false,
    isAuthorizedOverride: true,
    sourceSystem: 'TEST',
    sourceReference: 'OA加班单-20261122-001',
    effectiveFrom: '2026-10-13T00:00:00',
    isActive: true,
    createdAt: '2026-10-13T08:00:00',
    updatedAt: '2026-10-13T08:00:00'
  },
  // 工厂 2 11-16 厂休
  {
    id: 900040,
    scopeType: 'FACTORY',
    factoryId: 2,
    factoryCode: 'CN',
    workDate: '2026-11-16T00:00:00',
    workState: 'OFF',
    startAt: undefined,
    endAt: undefined,
    allowLowerOverride: false,
    isAuthorizedOverride: false,
    sourceSystem: 'TEST',
    effectiveFrom: '2026-10-13T00:00:00',
    isActive: true,
    createdAt: '2026-10-13T08:00:00',
    updatedAt: '2026-10-13T08:00:00'
  }
]

let mockPwcRowSeq = 900100

const delay = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms))

/** mock 专用：服务器本地日（YYYY-MM-DD）；与后端 `DateTime.Today` 同口径，勿用 UTC */
function localToday(): string {
  const d = new Date()
  const pad = (n: number): string => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`
}

/** mock 校验：周基准批内窗口重叠（同 weekdayIso） */
function assertWeeklyEntriesValid(entries: WeeklyCalendarUpsertRequest['entries']): void {
  for (const e of entries) {
    if (e.weekdayIso < 1 || e.weekdayIso > 7) {
      throw new ApiError(`WeekdayIso 须 1..7：${e.weekdayIso}`, 400)
    }
    if (e.workState === 'WORK') {
      if (!e.startAt || !e.endAt) {
        throw new ApiError(`WORK 必须带 startAt+endAt（weekdayIso=${e.weekdayIso}）`, 400)
      }
      if (e.startAt >= e.endAt) {
        throw new ApiError(`WORK endAt 必须晚于 startAt：${e.startAt} → ${e.endAt}`, 400)
      }
    } else if (e.workState === 'OFF') {
      if (e.allowLowerOverride !== true) {
        // OFF 默认 allowLowerOverride=false（DB CK：仅 OFF 可 true）
      }
    } else {
      throw new ApiError(`workState 必须 WORK / OFF：${e.workState}`, 400)
    }
  }
  // 同 weekdayIso 窗口重叠检查
  const byWd = new Map<number, WeeklyCalendarUpsertRequest['entries']>()
  for (const e of entries) {
    if (!byWd.has(e.weekdayIso)) byWd.set(e.weekdayIso, [])
    byWd.get(e.weekdayIso)!.push(e)
  }
  for (const [wd, items] of byWd) {
    for (let i = 0; i < items.length; i += 1) {
      for (let j = i + 1; j < items.length; j += 1) {
        const a = items[i]
        const b = items[j]
        if (
          a.workState === 'WORK' &&
          b.workState === 'WORK' &&
          a.startAt &&
          b.startAt &&
          a.endAt &&
          b.endAt
        ) {
          if (a.startAt < b.endAt && b.startAt < a.endAt) {
            throw new ApiError(
              `星期 ${wd} 批内窗口重叠：WORK ${a.startAt}-${a.endAt} vs WORK ${b.startAt}-${b.endAt}`,
              400
            )
          }
        }
      }
    }
  }
}

/** mock 校验：日期例外（IsAuthorizedOverride 必须 WORK+sourceReference） */
function assertExceptionValid(req: WorkCalendarExceptionRequest): void {
  if (req.workState === 'WORK') {
    if (!req.startAt || !req.endAt) {
      throw new ApiError('WORK 必须带 startAt+endAt', 400)
    }
    if (req.startAt >= req.endAt) {
      throw new ApiError(`WORK endAt 必须晚于 startAt：${req.startAt} → ${req.endAt}`, 400)
    }
  }
  if (req.isAuthorizedOverride && !req.sourceReference) {
    throw new ApiError('IsAuthorizedOverride=1 必须携 SourceReference（审批单号等证据）', 400)
  }
  if (req.allowLowerOverride && req.workState !== 'OFF') {
    throw new ApiError('AllowLowerOverride=1 仅 OFF 行有效', 400)
  }
}

/* ==================== API ==================== */

export const productionWorkCalendarApi = {
  /* ---------- 模块 A：查询 ---------- */

  /** 分页查询（GET /api/production-work-calendar，5号位 函 §一）
   *  - PageResult<T> = { items, total, page, pageSize }（字段 `page`）
   *  - MISSING = 无行；接口不返虚拟行（v1.4 §2） */
  async query(
    query: ProductionWorkCalendarQuery = {}
  ): Promise<ProductionWorkCalendarPageResult<ProductionWorkCalendarDto>> {
    const page = query.pageIndex ?? 1
    const size = query.pageSize ?? 20
    if (APS_USE_MOCK) {
      await delay(200)
      const today = localToday()
      const all = MOCK_PWC_ROWS.filter((r) => {
        if (query.scopeType && r.scopeType !== query.scopeType) return false
        if (query.factoryId != null && r.factoryId !== query.factoryId) return false
        if (query.departmentId != null && r.productionDepartmentId !== query.departmentId)
          return false
        if (!query.includeInactive && !r.isActive) return false
        // onlyEffective = 按生效日期判「当前生效」，与 IsActive 不同维度（后端口径对齐）
        if (query.onlyEffective) {
          const from = (r.effectiveFrom ?? '').slice(0, 10)
          const to = r.effectiveTo ? r.effectiveTo.slice(0, 10) : null
          if (from && from > today) return false
          if (to && to < today) return false
        }
        return true
      })
      return {
        items: all.slice((page - 1) * size, page * size),
        total: all.length,
        page,
        pageSize: size
      }
    }
    return apsHttp.get<ProductionWorkCalendarPageResult<ProductionWorkCalendarDto>>({
      url: '/api/production-work-calendar',
      params: {
        scopeType: query.scopeType,
        factoryId: query.factoryId,
        departmentId: query.departmentId,
        includeInactive: query.includeInactive ?? false,
        pageIndex: page,
        pageSize: size,
        sortBy: query.sortBy,
        sortDir: query.sortDir,
        onlyEffective: query.onlyEffective ?? false
      }
    })
  },

  /* ---------- 模块 B：周基准铺设 ---------- */

  /** 周基准铺设（POST /api/production-work-calendar/weekly，5号位 函 §二）
   *  - 同范围同星期：未到生效期旧行覆盖删、已到期的闭区间换版
   *  - 同星期可多条（白班+小夜+大夜），批内窗口重叠 → 400
   *  - 幂等可重铺（漏带=删掉该星期旧窗口）
   *  - 返 插入行数(int) */
  async upsertWeekly(req: WeeklyCalendarUpsertRequest): Promise<number> {
    if (APS_USE_MOCK) {
      await delay(300)
      assertWeeklyEntriesValid(req.entries)
      let inserted = 0
      const eff = req.effectiveFrom.slice(0, 10)
      for (const e of req.entries) {
        MOCK_PWC_ROWS.push({
          id: (mockPwcRowSeq += 1),
          scopeType: req.scopeType,
          factoryId: req.factoryId,
          productionDepartmentId: req.productionDepartmentId,
          weekdayIso: e.weekdayIso,
          workState: e.workState,
          startAt: e.startAt,
          endAt: e.endAt,
          allowLowerOverride: e.allowLowerOverride ?? false,
          isAuthorizedOverride: false,
          sourceSystem: req.sourceSystem,
          sourceReference: req.sourceReference,
          effectiveFrom: `${eff}T00:00:00`,
          isActive: true,
          createdAt: new Date().toISOString(),
          updatedAt: new Date().toISOString()
        })
        inserted += 1
      }
      return inserted
    }
    return apsHttp.post<number>({ url: '/api/production-work-calendar/weekly', data: req })
  },

  /* ---------- 模块 C：日期例外 ---------- */

  /** 日期例外铺设（POST /api/production-work-calendar/exception，5号位 函 §三）
   *  - 节假日 OFF / 补班 WORK / 授权加班（IsAuthorizedOverride=1 + SourceReference）
   *  - 同级同日窗口重叠 → 409
   *  - 返 新行 id(long) */
  async createException(req: WorkCalendarExceptionRequest): Promise<number> {
    if (APS_USE_MOCK) {
      await delay(300)
      assertExceptionValid(req)
      // 同级同日重叠检查
      const conflict = MOCK_PWC_ROWS.find(
        (r) =>
          r.scopeType === req.scopeType &&
          r.factoryId === req.factoryId &&
          r.productionDepartmentId === req.productionDepartmentId &&
          r.workDate?.slice(0, 10) === req.workDate.slice(0, 10) &&
          r.isActive
      )
      if (conflict) {
        throw new ApiError(
          `与已有例外行(Id=${conflict.id}, ${conflict.workState}) 时间重叠，同级同日冲突`,
          409
        )
      }
      const id = (mockPwcRowSeq += 1)
      MOCK_PWC_ROWS.push({
        id,
        scopeType: req.scopeType,
        factoryId: req.factoryId,
        productionDepartmentId: req.productionDepartmentId,
        workDate: `${req.workDate.slice(0, 10)}T00:00:00`,
        workState: req.workState,
        startAt: req.startAt,
        endAt: req.endAt,
        allowLowerOverride: req.allowLowerOverride ?? false,
        isAuthorizedOverride: req.isAuthorizedOverride ?? false,
        sourceSystem: req.sourceSystem,
        sourceReference: req.sourceReference,
        effectiveFrom: `${req.effectiveFrom.slice(0, 10)}T00:00:00`,
        effectiveTo: req.effectiveTo ? `${req.effectiveTo.slice(0, 10)}T00:00:00` : undefined,
        isActive: true,
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString()
      })
      return id
    }
    return apsHttp.post<number>({ url: '/api/production-work-calendar/exception', data: req })
  },

  /* ---------- 模块 D：批量闭区间换版 ---------- */

  /** 批量闭区间换版（POST /api/production-work-calendar/close，5号位 函 §四）
   *  - 把该范围全部未闭（EffectiveTo NULL）行闭到 newEffectiveFrom-1
   *  - 返 被闭行数(int) */
  async closeOpen(req: CalendarCloseOpenRequest): Promise<number> {
    if (APS_USE_MOCK) {
      await delay(300)
      const closeTo = `${req.newEffectiveFrom.slice(0, 10)}T00:00:00`
      let n = 0
      for (const r of MOCK_PWC_ROWS) {
        if (
          r.scopeType === req.scopeType &&
          r.factoryId === req.factoryId &&
          r.productionDepartmentId === req.productionDepartmentId &&
          r.isActive &&
          !r.effectiveTo
        ) {
          r.effectiveTo = closeTo
          r.updatedAt = new Date().toISOString()
          n += 1
        }
      }
      return n
    }
    return apsHttp.post<number>({ url: '/api/production-work-calendar/close', data: req })
  },

  /* ---------- 模块 E：行级闭区间 ---------- */

  /** 单行闭区间（POST /api/production-work-calendar/rows/{id}/close?effectiveTo=，5号位 函 §五行级）
   *  - 撤销已生效例外行的正规姿势：历史保留、审计完整
   *  - 不可重复闭；404 无此行；409 已闭/已停用；400 effectiveTo 早于该行 effectiveFrom */
  async closeRow(id: number, effectiveTo: string): Promise<boolean> {
    if (APS_USE_MOCK) {
      await delay(200)
      const r = MOCK_PWC_ROWS.find((x) => x.id === id)
      if (!r) throw new ApiError(`无此行 id=${id}`, 404)
      if (!r.isActive) throw new ApiError(`行已停用 id=${id}`, 409)
      if (r.effectiveTo) throw new ApiError(`行已闭 id=${id}`, 409)
      r.effectiveTo = `${effectiveTo.slice(0, 10)}T00:00:00`
      r.updatedAt = new Date().toISOString()
      return true
    }
    return apsHttp.post<boolean>({
      url: `/api/production-work-calendar/rows/${id}/close`,
      params: { effectiveTo: `${effectiveTo.slice(0, 10)}T00:00:00` }
    })
  },

  /* ---------- 模块 F：物理删除（仅未生效行）---------- */

  /** 物理删除（DELETE /api/production-work-calendar/rows/{id}，5号位 函 §五行级）
   *  - 仅限未生效行（EffectiveFrom > 今天）= 纯纠错通道
   *  - 已生效行 409 提示走 close */
  async deleteRow(id: number): Promise<boolean> {
    if (APS_USE_MOCK) {
      await delay(200)
      const idx = MOCK_PWC_ROWS.findIndex((x) => x.id === id)
      if (idx < 0) throw new ApiError(`无此行 id=${id}`, 404)
      const r = MOCK_PWC_ROWS[idx]
      const today = new Date().toISOString().slice(0, 10)
      if (r.effectiveFrom.slice(0, 10) <= today) {
        throw new ApiError(
          `行已生效（effectiveFrom=${r.effectiveFrom.slice(0, 10)}），请走 close`,
          409
        )
      }
      MOCK_PWC_ROWS.splice(idx, 1)
      return true
    }
    return apsHttp.delete<boolean>({ url: `/api/production-work-calendar/rows/${id}` })
  }
}
