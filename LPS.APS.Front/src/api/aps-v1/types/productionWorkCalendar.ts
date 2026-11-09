/**
 * APS V1 4号位 — Production Work Calendar（工厂/部门上级工作日历）DTO
 *
 * 依据：
 *  - 冻结文档 v1.4《Resource Calendar资源日历能力补充冻结方案 v1.4（2026-10-12 三级继承最小增量候选）》
 *    §三/§九 三级继承 + 三态（MISSING/WORK/OFF）
 *  - 5号位 2026-10-13《ProductionWorkCalendar三级日历维护接口文档 — 给4号位》
 *  - 后端 lps/LPS.APS.Application/Services/Dto/ProductionWorkCalendarDtos.cs
 *        lps/LPS.APS.Core/Entities/Aps/ProductionWorkCalendarSlot.cs
 *
 * 治理：
 *  - 条款=G1 候选；G4 公告前本模块供联调/备料，真实班次数据写入等公告
 *  - 5号位 业务 Owner + 2号位 表模型/解析 Owner；4号位 仅维护页面
 *  - 物理表 = dbo.ProductionWorkCalendarSlot（DDL v5.1.8.5）
 *
 * 三态语义（v1.4 §2）：
 *  - WORK：本层明确可用窗口
 *  - OFF ：本层明确休息/维修/停机/禁用（绝不被上级 WORK 复活）
 *  - MISSING：本层该日无有效行 → 允许继承上级已认证窗口
 *    ※ MISSING **不存**为虚拟行（接口不返，UI 也不造；表达"今天休息"必须显式 OFF 行）
 *
 * 关键校验（5号位 函 §〇 + 后端 DDL CK）：
 *  - 同级同日窗口重叠 → 400（weekly）/ 409（exception）
 *  - IsAuthorizedOverride=1 必须 WORK + SourceReference 证据 → 否则 400
 *  - AllowLowerOverride=1 仅 OFF 行有效，挂 WORK → 400
 *  - DEPARTMENT 行部门归属 Factory 不符 → 400（以库内事实为准）
 *  - 已生效行**永不物理改/删**：闭旧 effectiveTo + 开新行（审计要求）
 *  - 物理删除仅限未生效行（EffectiveFrom > 今天），已生效走 close
 *
 * 时间窗口：
 *  - 半开 [startAt, endAt)，相切（17:00 收尾接 17:00 起）合法，重叠拒绝
 *  - OFF 全天 = startAt/endAt 均 null（00:00-24:00 语义）
 *  - OFF 也允许带时段（午休 12:00-13:00 这种）
 *  - 跨午夜必须拆两行（DB CHECK startAt<endAt）：小夜 17:00-23:59:59 + 大夜 00:00-08:00 挂次日
 *
 * 字段命名：**camelCase**（后端 Program.cs:93 PropertyNamingPolicy=CamelCase；5号位 函 §一明确）
 */

/* ==================== 枚举 ==================== */

/** 范围类型（DDL CK_PWCS_Scope） */
export type ProductionWorkCalendarScope = 'FACTORY' | 'DEPARTMENT'

/** 三态语义：WORK / OFF / MISSING
 *  - WORK / OFF 存于 dbo.ProductionWorkCalendarSlot.WorkState
 *  - MISSING 由"无行"推导（v1.4 §2）；不存为虚拟行 */
export type ProductionWorkState = 'WORK' | 'OFF'

/** 页面级日历配置状态（v1.4 §2 + 兼容 v1.3 三态）
 *  - 设备/人工槽原 v1.3 状态仍保留（CONFIGURED/NOT_CONFIGURED/UNKNOWN）
 *  - PWC 自身三态：WORK / OFF / MISSING（无行）/ UNKNOWN（未查证）
 *  - 资源级（设备/人工槽）页内合并 PWC 时扩展使用 */
export type ProductionCalendarConfigStatus = 'WORK' | 'OFF' | 'MISSING' | 'UNKNOWN'

/* ==================== DTO ==================== */

/** 单行（GET 返 / 表格展示 / 行级 close/delete） */
export interface ProductionWorkCalendarDto {
  /** dbo.ProductionWorkCalendarSlot.Id (long) */
  id: number
  scopeType: ProductionWorkCalendarScope
  factoryId: number
  factoryCode?: string
  /** DEPARTMENT 必填；FACTORY 必须为 null */
  productionDepartmentId?: number
  departmentName?: string
  /** 日期例外行有值；周基准行 null（DDL CK 保证二选一） */
  workDate?: string
  /** 周基准行 1=周一…7=周日（ISO）；日期例外行 null */
  weekdayIso?: number
  /** HH:mm:ss；OFF 全天时均 null */
  startAt?: string
  endAt?: string
  /** WORK / OFF（DDL CK_PWCS_WorkState） */
  workState: ProductionWorkState
  /** 仅 OFF 行可 true：允许下级 WORK 越权覆盖（须下级 IsAuthorizedOverride=1 + 证据） */
  allowLowerOverride: boolean
  /** 仅 WORK 行可 true：本条 WORK 是被授权的越权加班（须带 SourceReference） */
  isAuthorizedOverride: boolean
  sourceSystem?: string
  /** 证据（OA 加班单号等）；IsAuthorizedOverride=1 时必填 */
  sourceReference?: string
  /** 含端点；null = 长期 */
  effectiveFrom: string
  effectiveTo?: string
  isActive: boolean
  createdBy?: string
  createdAt: string
  updatedBy?: string
  updatedAt: string
}

/* ==================== 请求载荷 ==================== */

/** 列表排序白名单（5号位 2026-10-14 回执 R1：大小写不敏感，非法值 400） */
export type ProductionWorkCalendarSortBy =
  | 'Id'
  | 'CreatedAt'
  | 'EffectiveFrom'
  | 'EffectiveTo'
  | 'UpdatedAt'

/** GET 查询入参（5号位 函 §一 + 2026-10-14 sortBy/sortDir 回执） */
export interface ProductionWorkCalendarQuery {
  scopeType?: ProductionWorkCalendarScope
  factoryId?: number
  departmentId?: number
  /** 默认 false（不含停用行） */
  includeInactive?: boolean
  pageIndex?: number
  pageSize?: number
  /** 排序字段；不传 = 原 5 字段固定序（ScopeType, FactoryId, ProductionDepartmentId, WorkDate, WeekdayIso 全 ASC）
   *  - EffectiveTo 排序时 NULL 沉底（NULLS LAST）
   *  - 后端自动附 Id DESC tie-break 保翻页确定性（sortBy=Id 时不加） */
  sortBy?: ProductionWorkCalendarSortBy
  /** 排序方向；不传随 sortBy 默认（CreatedAt/UpdatedAt → DESC；Id/EffectiveFrom/EffectiveTo → ASC） */
  sortDir?: 'ASC' | 'DESC'
  /** 只看**当前生效**的行（按生效日期；与 `includeInactive` 的 IsActive 不是同一维度）
   *  - 后端谓词：`EffectiveFrom <= @Today AND (EffectiveTo IS NULL OR EffectiveTo >= @Today)`
   *  - `@Today` = **服务器本地日**（后端 `DateTime.Today`），与 CloseRow 同口径，勿用 UTC
   *  - 默认 false = 全量可见（维护页要能看到未生效 / 已过期行） */
  onlyEffective?: boolean
}

/** 周基准铺设单项：某星期几一条（白班+小夜+大夜多班制可多条） */
export interface WeeklyCalendarEntry {
  /** 1=周一…7=周日（ISO） */
  weekdayIso: number
  workState: ProductionWorkState
  /** WORK 必填；OFF 全天则两者均空 */
  startAt?: string
  endAt?: string
  /** 仅 OFF 行可 true */
  allowLowerOverride?: boolean
}

/** 周基准铺设计求（5号位 函 §二）
 *  - 同范围同星期：未到生效期的旧行覆盖删、已到期的闭区间换版
 *  - 同星期可多条（白班+小夜+大夜三班制），批内窗口重叠 → 400
 *  - 幂等可重铺（漏带=删掉该星期旧窗口） */
export interface WeeklyCalendarUpsertRequest {
  scopeType: ProductionWorkCalendarScope
  factoryId: number
  /** DEPARTMENT 必填；FACTORY 必须为 null */
  productionDepartmentId?: number
  effectiveFrom: string
  entries: WeeklyCalendarEntry[]
  sourceSystem?: string
  sourceReference?: string
}

/** 日期例外铺设计求（5号位 函 §三）
 *  - 节假日 OFF / 补班 WORK / 授权加班（IsAuthorizedOverride=1）
 *  - 同级同日窗口重叠 → 409
 *  - 日期例外优先于周基准（解析侧规则，前端展示无需特判） */
export interface WorkCalendarExceptionRequest {
  scopeType: ProductionWorkCalendarScope
  factoryId: number
  productionDepartmentId?: number
  workDate: string
  workState: ProductionWorkState
  startAt?: string
  endAt?: string
  /** 仅 OFF 行可 true */
  allowLowerOverride?: boolean
  /** 仅 WORK 行可 true；=1 时必须带 sourceReference 证据 */
  isAuthorizedOverride?: boolean
  sourceSystem?: string
  sourceReference?: string
  effectiveFrom: string
  effectiveTo?: string
}

/** 闭区间换版请求（5号位 函 §四）
 *  - 把该范围全部未闭（EffectiveTo NULL）行闭到 newEffectiveFrom-1
 *  - 供新版班制接力 */
export interface CalendarCloseOpenRequest {
  scopeType: ProductionWorkCalendarScope
  factoryId: number
  productionDepartmentId?: number
  /** 新版本起始日；旧开放行 EffectiveTo 闭到此日前一天 */
  newEffectiveFrom: string
}

/* ==================== 分页响应（模块级本地包装）====================
 *  - 字段名 `page`（不是 pageIndex），与 ManualETA / business-fact-issues / TaskSplitRuleConfig / manual-capacity 同款
 *  - 5号位 函 §〇.3 明确 PageResult<T> = { items, total, page, pageSize }
 */
export interface ProductionWorkCalendarPageResult<T> {
  items: T[]
  total: number
  page: number
  pageSize: number
}

/* ==================== 展示用派生元数据 ==================== */

export const PWC_SCOPE_META: Record<
  ProductionWorkCalendarScope,
  { label: string; tagType: 'success' | 'warning' | 'info' }
> = {
  FACTORY: { label: '工厂', tagType: 'success' },
  DEPARTMENT: { label: '部门', tagType: 'warning' }
}

export const PWC_WORK_STATE_META: Record<
  ProductionWorkState,
  { label: string; tagType: 'success' | 'danger' }
> = {
  WORK: { label: '要工作', tagType: 'success' },
  OFF: { label: '不工作', tagType: 'danger' }
}

/** 页面级日历配置状态元数据（与原 CalendarConfigStatus 兼容 + 扩展） */
export const PRODUCTION_CALENDAR_STATUS_META: Record<
  ProductionCalendarConfigStatus,
  { label: string; tagType: 'success' | 'danger' | 'info' | 'warning'; description: string }
> = {
  WORK: {
    label: 'WORK',
    tagType: 'success',
    description: '本层明确可用窗口（不被任何上级覆盖）'
  },
  OFF: {
    label: 'OFF',
    tagType: 'danger',
    description: '本层明确停机（绝不被上级 WORK 复活）'
  },
  MISSING: {
    label: 'MISSING',
    tagType: 'warning',
    description: '本层该日无有效行 → 允许继承上级已认证窗口'
  },
  UNKNOWN: {
    label: '未查证',
    tagType: 'info',
    description: '尚未查询该对象的日历状态'
  }
}

/** ISO 周几映射（前端展示用） */
export const WEEKDAY_ISO_META: Record<number, { short: string; long: string }> = {
  1: { short: '一', long: '周一' },
  2: { short: '二', long: '周二' },
  3: { short: '三', long: '周三' },
  4: { short: '四', long: '周四' },
  5: { short: '五', long: '周五' },
  6: { short: '六', long: '周六' },
  7: { short: '日', long: '周日' }
}

/** 班次词表（5号位 函 §〇.6 引用） */
export const PWC_SHIFT_PRESETS: Array<{
  label: string
  weekdayIso: number
  workState: ProductionWorkState
  startAt: string
  endAt: string
}> = [
  {
    label: '白班 08:00-17:00',
    weekdayIso: 1,
    workState: 'WORK',
    startAt: '08:00:00',
    endAt: '17:00:00'
  },
  {
    label: '小夜 17:00-24:00',
    weekdayIso: 1,
    workState: 'WORK',
    startAt: '17:00:00',
    endAt: '23:59:59'
  },
  {
    label: '大夜 00:00-08:00',
    weekdayIso: 2,
    workState: 'WORK',
    startAt: '00:00:00',
    endAt: '08:00:00'
  },
  { label: 'OFF 全天', weekdayIso: 7, workState: 'OFF', startAt: '', endAt: '' }
]
