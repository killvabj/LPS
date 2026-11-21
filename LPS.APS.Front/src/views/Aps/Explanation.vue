<script setup lang="ts">
/**
 * APS V1 4号位 — 异常与原因解释（页面 6 / U09 / U14 / U20）
 *
 * 4号位文档第 11-12 节约束：
 *  - 根因优先显示，不只 DUE_DATE_RISK（U09）
 *  - 设备故障：只提示影响/建议，不直接改 Task 状态（U20 / 4号位文档第 12 节；V1 不建设 PAUSE/RESUME 状态闭环）
 *  - 跨 Domain 共享设备阻挡原因清晰可见（U14）
 *
 * 写接口：本页面只读，0 个写调用（4号位文档第 2.1 节）
 *
 * 页面分区：
 *  - 顶部 3 个严重度 KPI（ERROR / WARN / INFO，v5.1.10 §八.1 :4864 3 码权威）
 *  - 排程解释事实（按 objectType 分组，5 码：ORDER/TASK/RESOURCE/STAGE/DOMAIN）
 *  - 业务事实问题（@see BusinessFactIssueDto）
 *  - 重排建议（含 estimatedImpact）
 *  - 设备故障（不直接改 Task 状态）
 *
 * 2026-10-09 改造（按 5号位 函件 R1 回执）：
 *  - R1 ReasonCode 字典对齐 v5.1.10 §八.1 15 码全集（@see types/explanation.ts REASON_CODES）
 *  - FROZEN_ZONE_LOCK：15 码唯一锁码；FIRM/FROZEN/其它执行锁统一并入；细分走 EvidenceJson.evidenceType = LOCK
 *  - 撤 5号位 关键纠正 3 占位面板（后端无对应实体）改用 Summary 端点的 errorCount / warnCount / infoCount 三计数 + reasonCodeCounts 分布
 *  - 撤 BusinessFactIssue.domainKey 4号位 占位字段（对齐 lps/LPS.APS.Core/Dto/BusinessFactIssueDto.cs 14 字段）
 *  - 修 planVersionId 必填 400 bug（store 调 getByPlanVersion 透传）
 *
 * R2 关键路径 / R3 failureSeverity 跨号位（1号位 求解 + 2号位 落盘 + 0号位 裁决 V1 范围）—— 前端不动
 *  - verify [E] 段新增 E11/E12 skip 占位
 *
 * 2026-10-10 改造（按 10-10 撤函回检发现的真问题）：
 *  - 业务事实问题分页 + total（@see 函件 frontNew/docs/4号位-2026-10-10-business-fact-issues分页severity排序total-给5号位.md）
 *  - store 层缓存：版本切换保留，仅"刷新"按钮失效（数据按设计跨版本）
 *  - view 层用 sortedBusinessFactIssues：severity desc + occurredAt desc 兜底排序（即便后端未落地 ORDER BY severity，关键 ERROR 在前）
 *  - ElCard body 末尾增分页区（10-10 首版：pageSize ElSelect）
 *
 * 2026-10-10 续改：标准 ElPagination 分页
 *  - store fetchBizPage(page, size) 替换式（不累积）；ElPagination v-model 双绑 page / pageSize
 *  - @current-change → store.fetchBizPage；@size-change → store.changeBizPageSize（自回第 1 页）
 *  - ElPagination layout="total, sizes, prev, pager, next, jumper" 一次给齐
 *  - view ElCard header 移除 "显示 N 条 / 共 M 条" 双 tag（与 ElPagination 总数显示重复）
 *  - verify E23 静态锁标准 ElPagination 契约
 *
 * 2026-11-05 修三处真缺陷（dev 实测 + 后端源码定位）：
 *  - KPI 百分比分母：改用后端权威总数 `totalFacts`（Summary.totalCount）
 *    （此前拿「已加载条数」当分母 ⇒ dev 出现「错误 538% / 警告 516%」）
 *  - 排程解释事实列表：改走列表端点 `/api/explanation` 分页加载
 *    （后端 Summary.items 写死 `severity:"ERROR", take:50` ⇒ 只看得见 50 条 ERROR，
 *      258 条 INFO 与其它 ObjectType 分组全部不可见且无任何提示 ⇒ 静默截断）
 *  - 事实字段映射：后端 `id`/`createdAt` → 前端 `factId`/`occurredAt`；`objectRef` 前端派生
 *    （此前「对象」格恒空、时间恒 `Invalid Date`）
 *  - 面板加「共 N 条 · 已显示 M 条」计数行 + 「继续加载」（大版本不一次拉完）
 *
 * 2026-11-09 加列（按 5号位 11-09 回执行 7「前端可加列」）：
 *  - 业务事实问题表新增「降级动作」列 = `degradeAction`
 *    （数据本来就返：@see src/api/aps-v1/explanation.ts:299 `degradeAction: r.degradeAction ?? undefined`）
 *  - 中文标签走 `degradeActionLabelOf`（types/explanation.ts），码表依据冻结 07:857/868-885 + 08 DDL
 *  - **不新增** `reviewedBy` / `reviewedAt` / `affectedQuantity` 三列：
 *    前两者实库 0 值、后者后端 SQL 硬编码 NULL ⇒ 加了就是空列（5号位 来函明确要求）
 *
 * 2026-11-12 加三个筛选（按 5号位 11-11 复函 §一/§二，后端已落码，dev 11-12 实测可用）：
 *  - 问题类型（等值，值域 = 封闭 10 码 @see types/explanation.ts BIZ_ISSUE_TYPE_CODES）
 *  - 关键词（模糊；BOM 源打 Detail/BOMNO/物料码，MSC 源只打 Detail/物料码）
 *  - 工厂（等值；此前后端「收而不用」= 传了等于没传，5号位 已一并修复）
 *  - 工厂下拉：拉 `/api/governance/factories` 后按 **5 码白名单**过滤
 *    （后端主数据表另有 44 条测试夹具 `TF_*`，直接铺全表会把测试厂推给用户）；
 *    拉不到就空下拉 + 如实报错，不硬编码兜底
 *  - 选中工厂时给一句口径提示：跨厂问题不归单一工厂，选了某个工厂就看不到它们
 *
 * 2026-11-21：按 v0.2 稿 §P08「计划异常与原因查询」逐条核对后落码
 *   （依据《APS_V1_P01-P13_全页面业务设计统一收口核对稿_v0.2_20261114_四项审核修订版.md》
 *    §二 P08 + §三 角色/权限一致 + §五 UI-15/Q09）
 *   - 「部分P08问题不能无依据呈现为0」：分组计数只统计**已加载**的事实（首屏最多 1000 条，
 *     点「继续加载」最多 5000 条）⇒ 未加载完时不再把 0 说成"这次排产没产生该类事实"，改显「未加载完」。
 *   - 「没有原因显示原因未提供/未判定」：订单级清单行在**后端没写原因码**时，行标签由「未归类」改「原因未提供」。
 *   - 「行内详情…可证证据」：订单级清单补「说明」列（证据 summary，后端真值，不编）。
 *   - 「导航：订单回P02/P03、供给回P07/采购协同、Run失败到P10（仅有管理员权限时）」：
 *     订单号可点跳订单页；业务事实问题按来源跳 PI位置 / 采购协同；跳 P10 只对管理员显示。
 *
 * 2026-11-21（续）：按 v0.2 稿 §P08「**仅一页，一张异常列表＋行内展开详情**」改页面结构
 *   （依据同上 §二 P08；用户 11-21 指令「先弄日期范围筛选 + 页面结构（单表 + 行内展开）」）
 *   - 排程解释事实**并成一张异常清单**：订单级 / 任务级 / 资源级 / 工段级 / 排程域级 /
 *     未入字典类型全在一张表里，用新增的「对象类型」列区分。
 *     ⇒ 原「任务 / 资源 / 工段级事实」三块小表**撤除**（11-10 用户「分别列出来」的口径由
 *     「对象类型」列 + 对象类型筛选下拉承接；本次按 11-14 稿的单表口径执行）。
 *   - 行内展开（ElTable type="expand"）：异常对象身份（对象类型/对象/物料/排程域/严重度/记录时间/
 *     技术标识）、数量与日期、可证证据、正式 Explanation（无原因显「原因未提供 / 未判定」）、
 *     相关影响对象、去哪处理（按对象类型落到 P02/P03、P04/P06、P07、P10）。
 *   - 业务事实问题表同样加行内展开：问题说明全文 + 来源/降级动作/单据/时间 + 去查。
 *   - 「待核对（未入字典 ObjectType）」不再另起一块清单，改成一句说明 + 条数；
 *     逐条仍在上面的清单里以「待核对·<原值>」标签露出（不丢弃）。
 *   - 「业务事实问题」仍是**另一张表**：端点不带 planVersionId ⇒ 跨版本汇总，与排程解释事实
 *     不是同一批（已在页面写明；统一清单端点已出函请 5号位）。
 *   - 日期范围筛选：**前端不做**（后端列表端点无时间参数）⇒ 已出函请 5号位加
 *     occurredFrom/occurredTo（@see docs/4号位致5号位_函_P08异常解释页_需日期范围参数与统一清单端点_请回执_v1.0_20261121.md）。
 */

import { computed, onMounted, reactive, ref, watch } from 'vue'
import dayjs from 'dayjs'
import { storeToRefs } from 'pinia'
import { useRoute, useRouter } from 'vue-router'
import { orderApi, domainApi } from '@/api/aps-v1'
import type { PlanVersionSummaryDto } from '@/api/aps-v1/types/schedule'
import { useExplanationStore } from '@/store/modules/aps/explanation'
import { useScheduleStore } from '@/store/modules/aps/schedule'
import { useDomainStore } from '@/store/modules/aps/domain'
import { useApsAuthStore } from '@/store/modules/aps/auth'

import {
  ElAlert,
  ElButton,
  ElCard,
  ElDescriptions,
  ElDescriptionsItem,
  ElEmpty,
  ElInput,
  ElOption,
  ElOptionGroup,
  ElPagination,
  ElSelect,
  ElTag,
  ElTable,
  ElTableColumn
} from 'element-plus'
import {
  EXPLANATION_REASON_META,
  EXPLANATION_SEVERITY_LABELS,
  EXPLANATION_SEVERITY_TAG,
  OBJECT_TYPE_LABELS,
  BIZ_ISSUE_TYPE_CODES,
  BIZ_ISSUE_FACTORY_CODES,
  bizIssueTypeLabelOf,
  bizSourceLabelOf,
  degradeActionLabelOf,
  reasonLabelOf,
  classifyExplanationReason,
  isConfigGapReasonCode,
  type ExplanationReasonClassKey,
  type ExplanationReasonMeta,
  type ExplanationSeverity,
  type ReasonCodeCount,
  type ScheduleExplanationFact
} from '@/api/aps-v1/types/explanation'
import { stageLabelOf } from '@/api/aps-v1/types/stage'
import { planVersionStatusLabel, planVersionStatusTag } from '@/api/aps-v1/types/common'

const router = useRouter()
/** 运行页「看异常解释」带过来的版本入参（?planVersionId=）；见 onMounted */
const route = useRoute()

const explanationStore = useExplanationStore()
const scheduleStore = useScheduleStore()
const domainStore = useDomainStore()
const apsAuth = useApsAuthStore()
/** domainKey → 中文域名称（后端主数据；取不到时回退原键，不猜） */
const domainLabelOf = (key?: string | null): string => domainStore.label(key as never)
/**
 * 能否跳「运行与下发」(P10)。
 *
 * v0.2 稿 §P08「导航」：*Run失败到P10（仅有管理员权限时）* ⇒ 非管理员不给这个入口。
 * P10 的日常管理角色是**系统管理员**（稿 §三 角色表「P10唯一管理角色」）。
 */
const canGoRun = computed(() => apsAuth.isAdmin)
const {
  data,
  loading,
  error,
  totalFacts,
  loadedFactsCount,
  factsHasMore,
  factsLoading,
  factsByObjectType,
  unrecognizedObjectTypeFacts,
  unrecognizedRawObjectTypes,
  businessFactIssues,
  businessFactIssuesTotal,
  businessFactIssuesPage,
  businessFactIssuesPageSize,
  businessFactIssuesLoading,
  bizSeverity,
  bizSource,
  bizIssueType,
  bizFactoryCode,
  sortedBusinessFactIssues
} = storeToRefs(explanationStore)
const { currentVersionId } = storeToRefs(scheduleStore)
/** 全量版本列表（解释页版本下拉用；与甘特页那套三档截断分开） */
const { allVersions, allVersionsLoading, allVersionsError } = storeToRefs(scheduleStore)
/** 各版本事实条数缓存 + 进度（懒取，见 explanation store） */
const { versionFactCounts, factCountPending, factCountErrors } = storeToRefs(explanationStore)
// actions 直接解构（Pinia 文档：storeToRefs 只包 state/getters；actions 直接解构）
const { fetchBizPage, refreshBizIssues, changeBizPageSize, setBizFilters, loadMoreFacts } =
  explanationStore
const { loadFactCounts } = explanationStore

/** 事实总数的显示文本：未取到（Summary 失败）显「未提供」，不显 0（§0.2 ②） */
const totalFactsText = computed(() =>
  totalFacts.value === null ? '未提供' : String(totalFacts.value)
)

/* ===== 事实行的大白话渲染（2026-11-10） =====
 * 背景：原先行里显示 `需求 999472_1` + 「未标注原因码」，工厂用户看不懂，也不知道下一步做什么。
 * 实测（planVersionId=999472，998 条）：顶层 reasonCode 全空，但证据 JSON 里有
 *   - AllocationSequence（需求片序号）
 *   - UpstreamReasonCode（真实原因，中英混用）
 *   - NetOutputQty / ScheduledShareQty / UnscheduledQty（未排数量）
 *   - DomainKey（域键，可翻中文名）
 * 这里把这些翻成人话；拿不到的分支保持原样，不编。 */

/** 五个对象分组的条数（含 0），让「分组是否齐全」一眼可见 */
const objectTypeCounts = computed<Array<{ key: string; label: string; count: number }>>(() => {
  const groups = factsByObjectType.value ?? {}
  return (Object.keys(OBJECT_TYPE_LABELS) as string[]).map((k) => ({
    key: k,
    label: OBJECT_TYPE_LABELS[k as keyof typeof OBJECT_TYPE_LABELS],
    count: (groups as Record<string, unknown[]>)[k]?.length ?? 0
  }))
})

/* ===== 事实清单：工厂视角重做（2026-11-10） =====
 *
 * 用户是工厂计划员，不是排产算法工程师。这块要回答 4 个问题：
 *   ① 这批排产整体成没成？       → 顶部结论条
 *   ② 哪些订单没排进去？         → 清单（订单号 + 物料 + 数量 + 交期）
 *   ③ 为什么没排、我该干什么？   → 分类卡（原因归类）+ 每行「处理入口」
 *   ④ 这批的账对不对、能不能信？ → 排程域体检
 *
 * 全部字段来自后端真实返回（dev planVersionId=999472 实测 998 条）：
 *   - 订单号 / 物料编码：列表端点**直返**（996/998 有值）⇒ 无需逐条查订单详情
 *   - 数量：证据 NetOutputQty / ScheduledShareQty / UnscheduledQty
 *   - 交期：订单列表端点 `GET /api/order-query` 一次性拉该版本订单，按订单号 join
 *   - 原因：证据 UpstreamReasonCode（3 个英文配置码 + 1 类中文句子）+ evidenceType
 */

/** 事实行视图模型（模板只认这个，不直接碰后端字段名） */
interface FactRow {
  factId: number
  orderNo: string
  materialCode: string
  dueDate?: string
  /** 需求量（证据 NetOutputQty） */
  demand?: number
  /** 已排份额（证据 ScheduledShareQty） */
  scheduled?: number
  /** 未排数量（常规需求行 = 证据 UnscheduledQty；生产指令超发行 = 超发量 ExcessQty） */
  unscheduled?: number
  /** 数量列三格的含义（PI 超发行的口径与常规需求行不同，逐行自带标签） */
  qtyLabels: { demand: string; scheduled: string; unscheduled: string }
  /** 是否「生产指令超发」行（这类行的三个数量不是需求/已排/未排） */
  isPi: boolean
  /**
   * 「排产决策原因」标签文本（空 = 这一行没有决策原因码）。
   *
   * 配置缺口码（批策略 / 生产部门 / 资源日历）**不算**决策原因 —— 它们由分类卡那一栏
   * （`classMeta.label`）表达；若在这里再显示一次，顶层 `reasonCode` 一旦被 0号位 回写
   * 成这些码，页面会出现裸英文码（且与分类标签重复）。
   */
  decisionCodeLabel: string
  classKey: ExplanationReasonClassKey
  classMeta: ExplanationReasonMeta
  /** 卡在哪个大工艺阶段（后端 stageCode；195/996 有值，其余空；展示走 stageLabelOf 翻中文名） */
  stageCode: string
  severity: ExplanationSeverity
  /**
   * 可证证据摘要（后端 `evidence.summary`，后端真值，不编）。
   * 2026-11-21（v0.2 稿 §P08「行内详情…可证证据」）：订单级清单补这一列
   * —— 此前证据只在任务/资源/工段级表和「待核对」清单里出现，订单级行看不到。
   */
  evidenceSummary: string
  /**
   * 这一行**没有正式原因**（后端没写原因码、证据里也没有可认的原因）。
   * v0.2 稿 §P08「没有原因显示原因未提供/未判定」⇒ 行标签显「原因未提供」，不拿「未归类」充数。
   */
  reasonMissing: boolean
  /* ---- 2026-11-21（v0.2 稿 §P08「一张异常列表＋行内展开详情」）：并表后补的字段 ---- */
  /** 对象类型原始码（ORDER / TASK / RESOURCE / STAGE / DOMAIN，或未入冻结字典的值） */
  objectType: string
  /** 对象类型中文标签（未入冻结字典 → 「待核对·<码>」） */
  objectTypeLabel: string
  /** 对象标识（后端派生：ORDER=需求号 / TASK=任务Id / RESOURCE=资源Id / STAGE=阶段码 / DOMAIN=域键） */
  objectRef: string
  /** 中文可读对象（订单号 / 阶段中文名 / 排程域名 …；取不到留空） */
  objectLabel: string
  /** 排程域中文名（ORDER / DOMAIN 级事实有；取不到留空） */
  domainName: string
  /** 记录时间（= 后端 createdAt；**不是**业务发生时间，别当交期用） */
  occurredAt: string
  /** 影响小时数（后端给的值；量纲不可信，只作参考） */
  impactHours?: number
  /** 订单 Id（ORDER 级；跳 P03 订单详情用） */
  orderId?: number
  /** 相关影响对象（证据里能取到的关联对象；取不到留空） */
  relatedObjects: string
}

/** 数值取真值（非有限数 / 空 → undefined，不显示 0 假值） */
function numOf(v: unknown): number | undefined {
  if (v === null || v === undefined || v === '') return undefined
  const n = Number(v)
  return Number.isFinite(n) ? n : undefined
}

/** 数量展示：整数不带小数点，小数保留 2 位；千分位（1685413 → 1,685,413，好读） */
function fmtQty(n?: number): string {
  if (n === undefined) return '—'
  const neg = n < 0
  const abs = Math.abs(n)
  const [intPart, decPart] = (Number.isInteger(abs) ? String(abs) : abs.toFixed(2)).split('.')
  const sep = intPart.replace(/\B(?=(\d{3})+$)/g, ',')
  return `${neg ? '-' : ''}${sep}${decPart ? '.' + decPart : ''}`
}

const orderFacts = computed(() => factsByObjectType.value.ORDER ?? [])
const domainFacts = computed(() => factsByObjectType.value.DOMAIN ?? [])

/** 对象类型 → 清单列用的中文标签（未入冻结字典 → 「待核对·<码>」）
 *
 *  2026-11-21（v0.2 稿 §P08「一张异常列表＋行内展开详情」）：订单级 / 任务级 / 资源级 /
 *  工段级 / 排程域级**同列一张异常清单**，用「对象类型」列区分、并提供对象类型筛选
 *  ⇒ 原先「任务 / 资源 / 工段级事实」三块小表撤除，类型口径保留在此表。
 */
const OBJECT_TYPE_LEVEL_LABELS: Record<string, string> = {
  ORDER: '订单级',
  TASK: '任务级',
  RESOURCE: '资源级',
  STAGE: '工段级',
  DOMAIN: '排程域级'
}

/** 对象类型 → 中文标签（未入冻结字典的按「待核对·<码>」如实露出，不丢弃） */
function objectTypeLabelOf(raw: string): string {
  const key = (raw ?? '').trim()
  if (!key) return '未提供'
  return OBJECT_TYPE_LEVEL_LABELS[key] ?? `待核对·${key}`
}

/** 分类卡展示顺序（固定；先"要你动手的"，后"别人处理/告知的"） */
const REASON_CLASS_ORDER: ExplanationReasonClassKey[] = [
  'BATCH_POLICY_MISSING',
  'BATCH_POLICY_CONFLICT',
  'MISSING_PRODUCTION_DEPARTMENT_CONTEXT',
  'CALENDAR_COVERAGE_INSUFFICIENT',
  'ENGINE',
  'PI_OVER_COMMIT',
  'DECISION',
  'OTHER'
]

/** 每类原因的「处理入口」（路由是前端页面，理由分类是业务字典 ⇒ 分开放） */
const REASON_ACTION: Partial<Record<ExplanationReasonClassKey, { text: string; to: string }>> = {
  BATCH_POLICY_MISSING: { text: '前往批策略维护', to: '/aps/task-split-rule-config' },
  BATCH_POLICY_CONFLICT: { text: '前往批策略维护', to: '/aps/task-split-rule-config' },
  CALENDAR_COVERAGE_INSUFFICIENT: { text: '前往资源日历维护', to: '/aps/resource-calendar' },
  ENGINE: { text: '查看排产运行记录', to: '/aps/run' }
}

/** 订单索引（订单号 → 交期等）；一次拉该版本订单，按订单号 join */
const orderIndex = reactive<Record<string, { dueDate?: string }>>({})
/**
 * 订单 Id → 订单（订单号 / 物料编码）。
 *
 * 后端 5号位 R3（`orderNo` / `materialCode`）落码**部署前**，事实行里这两个字段取不到；
 * 部署后 `materialCode` 仍约有 68.8% 是**设计性 NULL**（上游没匹配到需求行）。
 * ⇒ 用事实自带的 `orderId`（`[Order].Id`）反查**同一批**已拉到的订单，零新增请求，
 * 补回**真订单号 / 物料编码**。
 *
 * ⚠️ 不用需求内部键兜底：那是内部键，11-10 用户口径「内部键不进用户可见区」
 *    ⇒ 页面只显真实业务标识，取不到就显示 `—`。
 */
const orderById = reactive<Record<number, { orderNo?: string; materialCode?: string }>>({})
const orderIndexState = ref<'idle' | 'loading' | 'ready' | 'skipped' | 'failed'>('idle')
/** 订单数上限（超出就不拉交期：订单数大的版本拉几十页不划算，交期列显示 —） */
const ORDER_INDEX_MAX = 2000
const ORDER_INDEX_PAGE_SIZE = 200

async function loadOrderIndex(planVersionId: number): Promise<void> {
  orderIndexState.value = 'loading'
  for (const k of Object.keys(orderIndex)) delete orderIndex[k]
  for (const k of Object.keys(orderById)) delete orderById[k]
  try {
    const first = await orderApi.list({
      planVersionId,
      pageIndex: 1,
      pageSize: ORDER_INDEX_PAGE_SIZE
    })
    const total = first.total ?? 0
    if (total > ORDER_INDEX_MAX) {
      orderIndexState.value = 'skipped'
      return
    }
    const pages = Math.ceil(total / ORDER_INDEX_PAGE_SIZE)
    const rest = await Promise.all(
      Array.from({ length: Math.max(0, pages - 1) }, (_, i) =>
        orderApi.list({
          planVersionId,
          pageIndex: i + 2,
          pageSize: ORDER_INDEX_PAGE_SIZE
        })
      )
    )
    for (const o of [first, ...rest].flatMap((r) => r.items ?? [])) {
      if (!o.orderNo) continue
      orderIndex[o.orderNo] = { dueDate: o.customerDueDate }
      if (o.id != null) orderById[o.id] = { orderNo: o.orderNo, materialCode: o.materialCode }
    }
    orderIndexState.value = 'ready'
  } catch {
    // 取不到交期不影响主流程（清单照常用），只是交期列显示 —
    orderIndexState.value = 'failed'
  }
}

watch(
  () => currentVersionId.value,
  (id) => {
    if (id) void loadOrderIndex(id)
  },
  { immediate: true }
)

/** 清单行（订单级事实 → 一行一条没排进去的需求） */
const factRows = computed<FactRow[]>(() =>
  orderFacts.value.map((f) => {
    const e = f.evidence ?? {}
    const meta = classifyExplanationReason(f)
    // 后端 R3 未部署 / 上游没匹配到需求行时，事实里取不到订单号与物料编码
    // ⇒ 用事实自带的 orderId 反查已拉到的订单（同批数据、零新增请求）补回真值
    const byId = f.orderId != null ? orderById[f.orderId] : undefined
    const orderNo = (f.orderNo ?? '').trim() || (byId?.orderNo ?? '')
    const materialCode = (f.materialCode ?? '').trim() || (byId?.materialCode ?? '')
    const idx = orderNo ? orderIndex[orderNo] : undefined
    // 生产指令超发行的三个数量口径与常规需求行不同：指令数量 / 已承接 / 超发
    const isPi = meta.key === 'PI_OVER_COMMIT'
    const reasonCode = (f.reasonCode ?? '').trim()
    return {
      factId: f.factId,
      orderNo,
      materialCode,
      dueDate: idx?.dueDate,
      demand: isPi ? numOf(e.quantity) : numOf(e.netOutputQty),
      scheduled: isPi ? numOf(e.continuationQty) : numOf(e.scheduledShareQty),
      unscheduled: isPi ? numOf(e.excessQty) : numOf(e.unscheduledQty),
      qtyLabels: isPi
        ? { demand: '指令数量', scheduled: '已承接', unscheduled: '超发' }
        : { demand: '需求', scheduled: '已排', unscheduled: '未排' },
      isPi,
      // 配置缺口码由分类标签表达，不在此处重复显示（避免裸英文码）
      decisionCodeLabel:
        reasonCode && !isConfigGapReasonCode(reasonCode) ? reasonLabelOf(reasonCode) : '',
      classKey: meta.key,
      classMeta: meta,
      stageCode: (f.stageCode ?? '').trim(),
      severity: f.severity,
      evidenceSummary: (f.evidenceSummary ?? '').trim(),
      // 「未归类」= 后端既没写原因码、证据里也没有可认的原因 ⇒ 这一行**原因未提供**
      reasonMissing: meta.key === 'OTHER',
      // 2026-11-21 并表：订单级事实的对象类型固定是 ORDER
      objectType: 'ORDER',
      objectTypeLabel: OBJECT_TYPE_LEVEL_LABELS.ORDER,
      objectRef: (f.objectRef ?? '').trim(),
      objectLabel: orderNo,
      domainName: domainLabelOf(f.domainKey),
      occurredAt: f.occurredAt ?? '',
      impactHours: f.impactHours,
      orderId: f.orderId,
      relatedObjects: ''
    }
  })
)

/* ===== 异常清单：一张表承载所有对象类型（2026-11-21，v0.2 稿 §P08） =====
 *
 * 稿原文：「仅一页，一张异常列表＋行内展开详情」⇒ 订单级 / 任务级 / 资源级 / 工段级 /
 * 排程域级 / 未入字典的类型 全部并进同一张清单，用「对象类型」列区分，
 * 行内展开给身份 / 数量 / 日期 / 可证证据 / 正式 Explanation / 相关影响对象。
 *
 * ⚠️ 「业务事实问题」按设计是**跨版本**的另一批数据（后端端点不带 planVersionId），
 *    现在并不进来 ⇒ 它仍是另一张表，页面上写明口径（统一清单端点已出函请 5号位）。
 */

/** 已加载的全部事实（含未入冻结字典的对象类型） */
const allFacts = computed<ScheduleExplanationFact[]>(() => data.value?.scheduleExplanationFacts ?? [])

/** 证据里能取到的关联对象（域级事实带样例物料 / 需求键样例） */
function relatedObjectsOf(f: ScheduleExplanationFact): string {
  const e = f.evidence ?? {}
  const pick = (v: unknown): string[] => (Array.isArray(v) ? v.filter(Boolean).map(String) : [])
  const samples = [...pick(e.planningYieldMissingSamples), ...pick(e.uncertainSamples)]
  return samples.length ? samples.join('、') : ''
}

/** 非订单级事实 → 清单行（与订单级同一张表） */
function otherFactRow(f: ScheduleExplanationFact): FactRow {
  const meta = classifyExplanationReason(f)
  const rawType = (f.objectType ?? '').trim()
  const stageName = (f.stageCode ?? '').trim() ? stageLabelOf(f.stageCode) : ''
  const domainName = domainLabelOf(f.domainKey)
  const objectLabel =
    rawType === 'STAGE'
      ? stageName || '工段'
      : rawType === 'DOMAIN'
        ? domainName || '排程域'
        : rawType === 'TASK'
          ? stageName
            ? `任务 · ${stageName}`
            : '任务'
          : rawType === 'RESOURCE'
            ? '资源'
            : '其他对象'
  const reasonCode = (f.reasonCode ?? '').trim()
  return {
    factId: f.factId,
    orderNo: '',
    materialCode: (f.materialCode ?? '').trim(),
    dueDate: undefined,
    demand: undefined,
    scheduled: undefined,
    unscheduled: undefined,
    qtyLabels: { demand: '需求', scheduled: '已排', unscheduled: '未排' },
    isPi: false,
    decisionCodeLabel:
      reasonCode && !isConfigGapReasonCode(reasonCode) ? reasonLabelOf(reasonCode) : '',
    classKey: meta.key,
    classMeta: meta,
    stageCode: (f.stageCode ?? '').trim(),
    severity: f.severity,
    evidenceSummary: (f.evidenceSummary ?? '').trim(),
    reasonMissing: meta.key === 'OTHER',
    objectType: rawType,
    objectTypeLabel: objectTypeLabelOf(rawType),
    objectRef: (f.objectRef ?? '').trim(),
    objectLabel,
    domainName,
    occurredAt: f.occurredAt ?? '',
    impactHours: f.impactHours,
    orderId: undefined,
    relatedObjects: relatedObjectsOf(f)
  }
}

/** 异常清单（一张表）：订单级 + 任务/资源/工段/排程域级 + 未入字典类型 */
const anomalyRows = computed<FactRow[]>(() => {
  const others = allFacts.value
    .filter((f) => (f.objectType ?? '').trim() !== 'ORDER')
    .map(otherFactRow)
  return [...factRows.value, ...others]
})

/** 清单里出现过的对象类型（供对象类型筛选下拉；带条数） */
const objectTypeOptions = computed(() => {
  const counts = new Map<string, number>()
  for (const r of anomalyRows.value) counts.set(r.objectType, (counts.get(r.objectType) ?? 0) + 1)
  return [...counts.entries()].map(([key, count]) => ({
    key,
    label: objectTypeLabelOf(key),
    count
  }))
})

/** 分类卡（按原因归类聚合；只列有数的类） */
const reasonClassCards = computed(() => {
  const groups = new Map<ExplanationReasonClassKey, FactRow[]>()
  for (const r of anomalyRows.value) {
    const g = groups.get(r.classKey)
    if (g) g.push(r)
    else groups.set(r.classKey, [r])
  }
  return REASON_CLASS_ORDER.map((key) => {
    const rows = groups.get(key) ?? []
    const materials = new Set(rows.map((r) => r.materialCode).filter(Boolean))
    return {
      ...EXPLANATION_REASON_META[key],
      count: rows.length,
      materialCount: materials.size,
      qty: rows.reduce((s, r) => s + (r.unscheduled ?? 0), 0)
    }
  }).filter((c) => c.count > 0)
})

/* ===== 清单筛选与排序 ===== */
const keyword = ref('')
const filterClass = ref<ExplanationReasonClassKey | ''>('')
const filterSeverity = ref<ExplanationSeverity | ''>('')
/** 对象类型筛选（2026-11-21 并表后新增：一张表里按类型分开看） */
const filterObjectType = ref('')
const sortBy = ref<'qty' | 'due'>('qty')
const factPage = ref(1)
const factPageSize = ref(50)

/** 筛选后的清单 */
const filteredRows = computed<FactRow[]>(() => {
  const kw = keyword.value.trim().toLowerCase()
  const rows = anomalyRows.value.filter((r) => {
    if (filterClass.value && r.classKey !== filterClass.value) return false
    if (filterSeverity.value && r.severity !== filterSeverity.value) return false
    if (filterObjectType.value && r.objectType !== filterObjectType.value) return false
    if (kw) {
      const hit =
        r.orderNo.toLowerCase().includes(kw) ||
        r.materialCode.toLowerCase().includes(kw) ||
        r.objectLabel.toLowerCase().includes(kw)
      if (!hit) return false
    }
    return true
  })
  const byQty = (a: FactRow, b: FactRow) => {
    if ((b.unscheduled ?? 0) !== (a.unscheduled ?? 0)) {
      return (b.unscheduled ?? 0) - (a.unscheduled ?? 0)
    }
    if ((b.demand ?? 0) !== (a.demand ?? 0)) {
      return (b.demand ?? 0) - (a.demand ?? 0)
    }
    // 无数量的行（任务/资源/工段/排程域级）排在订单级之后，避免混进未排清单里
    const aOrder = a.objectType === 'ORDER' ? 0 : 1
    const bOrder = b.objectType === 'ORDER' ? 0 : 1
    if (aOrder !== bOrder) return aOrder - bOrder
    return (
      (b.occurredAt ?? '').localeCompare(a.occurredAt ?? '') ||
      a.objectLabel.localeCompare(b.objectLabel)
    )
  }
  const byDue = (a: FactRow, b: FactRow) => {
    const da = a.dueDate ?? ''
    const db = b.dueDate ?? ''
    if (da && db && da !== db) return da.localeCompare(db)
    if (da && !db) return -1
    if (!da && db) return 1
    return byQty(a, b)
  }
  return [...rows].sort(sortBy.value === 'due' ? byDue : byQty)
})

/** 当前页数据（前端分页：清单已全量在内存，不走后端分页） */
const pagedRows = computed(() => {
  const start = (factPage.value - 1) * factPageSize.value
  return filteredRows.value.slice(start, start + factPageSize.value)
})

/** 筛选一变就回第 1 页（否则会停在不存在的页码上） */
watch([keyword, filterClass, filterSeverity, filterObjectType, sortBy], () => {
  factPage.value = 1
})

/** 清单里"未排件数"合计（筛选后口径，与表格同源；PI 超发行的超发量不混进来） */
const filteredUnscheduledQty = computed(() =>
  filteredRows.value.reduce((s, r) => s + (r.isPi ? 0 : (r.unscheduled ?? 0)), 0)
)

/** 分类卡点击 = 按该类筛选（再点一次取消） */
function toggleClass(key: ExplanationReasonClassKey): void {
  filterClass.value = filterClass.value === key ? '' : key
}

/**
 * 这类问题有没有对应页面可去修。
 *
 * 2026-11-21（v0.2 稿 §P08 导航）：ENGINE 类跳「运行与下发」(P10) **只对管理员**给入口
 *   （稿原文：「Run失败到P10（仅有管理员权限时）」）⇒ 非管理员这一栏显示「—」。
 */
function reasonActionOf(key: ExplanationReasonClassKey): { text: string; to: string } | undefined {
  if (key === 'ENGINE' && !canGoRun.value) return undefined
  return REASON_ACTION[key]
}

/** 跳订单页（P02）：订单号是业务真标识，订单页支持 `?orderNo=` 深链 */
function goOrder(orderNo: string): void {
  const no = (orderNo ?? '').trim()
  if (!no) return
  router.push({ name: 'ApsOrder', query: { orderNo: no } })
}

/**
 * 能否跳「排程域配置」页。
 *
 * 该页路由门控是 `aps.admin.system` 角色（见 router/modules/aps.ts）⇒ 非系统管理员不给入口。
 */
const canGoDomain = computed(() => apsAuth.isAdmin)

/** 行内「去哪处理」的一个入口 */
interface NavItem {
  text: string
  /** 路由目标（深链用对象，普通页用路径） */
  to: string | { path: string; query?: Record<string, string> }
}

/**
 * 一行异常可以去哪些专业页（v0.2 稿 §P08 导航）。
 *
 * 稿原文：*订单回P02/P03、供给回P07/采购协同、任务到P04/P06、Run失败到P10（仅有管理员权限时）*。
 * ⇒ 订单级给「订单详情」（P03 订单结构）、任务/工段级给甘特图（P04/P06）、
 *    资源级给甘特图负荷图层（P12）、排程域级给域配置页（仅系统管理员）。
 *    配置缺口类的原因处理入口（批策略 / 资源日历）也一并挂在行上。
 */
function navsOf(row: FactRow): NavItem[] {
  const list: NavItem[] = []
  if (row.objectType === 'ORDER') {
    if (row.orderId != null) {
      list.push({
        text: '去订单详情',
        to: {
          path: '/aps/order',
          query: {
            orderId: String(row.orderId),
            planVersionId: String(currentVersionId.value ?? '')
          }
        }
      })
    }
  } else if (row.objectType === 'TASK' || row.objectType === 'STAGE') {
    list.push({ text: '去甘特图查任务', to: '/aps/gantt' })
  } else if (row.objectType === 'RESOURCE') {
    list.push({ text: '去甘特图查资源负荷', to: '/aps/gantt' })
  } else if (row.objectType === 'DOMAIN' && canGoDomain.value) {
    list.push({ text: '去排程域配置', to: '/aps/domain' })
  }
  const act = reasonActionOf(row.classKey)
  if (act) list.push({ text: act.text, to: act.to })
  return list
}

/** 跳到某个专业页 */
function goNav(item: NavItem): void {
  void router.push(item.to)
}

/**
 * 业务事实问题 → 对应专业页（2026-11-21；v0.2 稿 §P08「按真实结果回到原专业页面」）。
 *
 * 只挂**后端真实有来源**的两类：生产指示位置未定位 → PI位置页；人工到货时间 → 采购协同页。
 * BOM / 生产部门上下文这两类没有对应的专业页面 ⇒ 不给假入口。
 */
const BIZ_ISSUE_NAV: Record<string, { text: string; to: string }> = {
  PI_POSITION: { text: '去生产指示位置', to: '/aps/pi' },
  MANUAL_ETA: { text: '去采购协同', to: '/aps/procurement' }
}
function bizNavOf(source: string): { text: string; to: string } | undefined {
  return BIZ_ISSUE_NAV[(source ?? '').trim()]
}
function goBizNav(source: string): void {
  const n = bizNavOf(source)
  if (n) router.push(n.to)
}

/** 这类问题的「处理入口」按钮文案 */
function reasonActionText(key: ExplanationReasonClassKey): string {
  return REASON_ACTION[key]?.text ?? ''
}

/** 跳到对应配置页 */
function goReasonAction(key: ExplanationReasonClassKey): void {
  const a = REASON_ACTION[key]
  if (a) router.push(a.to)
}

/** 「问题」列的色调：配置缺口=黄（要动手），引擎/数据问题=蓝（别人处理），空=灰 */
const REASON_TAG_TYPE: Record<ExplanationReasonClassKey, 'primary' | 'warning' | 'info'> = {
  BATCH_POLICY_MISSING: 'warning',
  BATCH_POLICY_CONFLICT: 'warning',
  MISSING_PRODUCTION_DEPARTMENT_CONTEXT: 'warning',
  CALENDAR_COVERAGE_INSUFFICIENT: 'warning',
  ENGINE: 'info',
  PI_OVER_COMMIT: 'info',
  DECISION: 'primary',
  OTHER: 'info'
}

/* ===== 排程域体检（DOMAIN 级事实 → 体检报告，不是流水账） ===== */
interface DomainHealth {
  factId: number
  domainName: string
  typeLabel: string
  /** 关键指标行 */
  metrics: Array<{ label: string; value: string; tone?: 'danger' | 'warn' | 'info' }>
  /** 一句话结论 */
  summary: string
  /** 样例物料 */
  samples: string[]
  /** 要人做什么 */
  todo?: string
}

const DOMAIN_EVIDENCE_LABELS: Record<string, string> = {
  DOMAIN_QUANTITY_RECONCILIATION: '数量对账',
  DEMAND_COMPLETION_TIME: '完工时间确定性'
}

const domainHealth = computed<DomainHealth[]>(() =>
  domainFacts.value.map((f) => {
    const e = f.evidence ?? {}
    const n = (k: string) => numOf(e[k])
    const type = (e.evidenceType ?? '').trim()
    const metrics: DomainHealth['metrics'] = []
    const samples: string[] = []
    let summary = ''
    let todo: string | undefined

    if (type === 'DOMAIN_QUANTITY_RECONCILIATION') {
      const demand = n('demandCount')
      const scheduled = n('scheduledDemandCount')
      const unscheduled = n('unscheduledDemandCount')
      const missing = n('missingDemandCount')
      const delta = n('deltaQty')
      const yieldMissing = n('planningYieldMissingCount')
      const yieldQueried = n('planningYieldQueriedCount')
      metrics.push({ label: '需求', value: `${fmtQty(demand)} 条` })
      metrics.push({ label: '已排', value: `${fmtQty(scheduled)} 条` })
      metrics.push({
        label: '未排',
        value: `${fmtQty(unscheduled)} 条`,
        tone: (unscheduled ?? 0) > 0 ? 'danger' : 'info'
      })
      metrics.push({ label: '对不上的差额', value: `${fmtQty(delta)} 件`, tone: 'info' })
      if ((missing ?? 0) > 0) {
        metrics.push({ label: '缺失需求', value: `${fmtQty(missing)} 条`, tone: 'warn' })
      }
      if ((yieldMissing ?? 0) > 0) {
        metrics.push({
          label: '缺良率',
          value: `${fmtQty(yieldMissing)} 条（查了 ${fmtQty(yieldQueried)} 条）`,
          tone: 'warn'
        })
        samples.push(...(e.planningYieldMissingSamples ?? []))
        summary = `良率没补上之前，这个域算出来的产量只能当参考。`
        todo = '找工艺/主数据给这些物料补良率'
      } else {
        summary = '数量对得上，良率也齐了。'
      }
    } else {
      const total = n('factCount')
      const falseCnt = n('certainFalseCount')
      const trueCnt = n('certainTrueCount')
      const missingLead = n('missingStageLeadTimeSegmentCount')
      metrics.push({ label: '需求', value: `${fmtQty(total)} 条` })
      metrics.push({
        label: '完工时间可信',
        value: `${fmtQty(trueCnt)} 条`,
        tone: 'info'
      })
      metrics.push({
        label: '完工时间不可信',
        value: `${fmtQty(falseCnt)} 条`,
        tone: (falseCnt ?? 0) > 0 ? 'danger' : 'info'
      })
      if ((missingLead ?? 0) > 0) {
        metrics.push({
          label: '缺工段提前期',
          value: `${fmtQty(missingLead)} 段`,
          tone: 'warn'
        })
      }
      /* 「阶段时间节点」「没标注」都是推算过程的中间量：既不能派工、也不对应任何动作
       * ⇒ 不占指标位，也不再挂悬停说明（2026-11-11 用户口径：提示太长太乱，先撤掉）。 */
      summary =
        (falseCnt ?? 0) > 0
          ? `${fmtQty(falseCnt)} 条需求的完工时间不可信，不能拿来对客户承诺交期。`
          : `${fmtQty(trueCnt)} 条需求的完工时间可信，可以拿来对客户承诺交期。`
      todo =
        (missingLead ?? 0) > 0
          ? '给没配提前期的工段补上提前期（部门级/工厂级/全局级）'
          : (falseCnt ?? 0) > 0
            ? '核对工段提前期和部门/工厂工作日历的可用时段是否配齐'
            : undefined
    }

    return {
      factId: f.factId,
      domainName: domainLabelOf(f.domainKey),
      typeLabel: DOMAIN_EVIDENCE_LABELS[type] ?? (type || '域级体检'),
      metrics,
      summary,
      samples: samples.slice(0, 10),
      todo
    }
  })
)

/* ===== ReasonCode 标签：走全站唯一口径 `reasonLabelOf`（types/explanation.ts） =====
 * 2026-10-31：本页原先自写查表 `REASON_CODE_LABELS[code] ?? code` ——
 *   ① 空串时 `?? code` 返回空串 ⇒ ElTag 渲染成**空白标签**（实库 95% 的事实 ReasonCode 为空串，
 *      5号位 10-31 实查 442,408 / 465,481 条；dev 实测 reasonCodeCounts 含 {"reasonCode":"","count":516}）；
 *   ② 未知码显示成裸码值，与甘特 / CTP 页的「待核对·<码>」口径不一致。
 *   现统一走共享函数：空串 → 「未标注原因码」，未知码 → 「待核对·<码>」。 ===== */

/* ===== BusinessFactIssue 的中文标签（2026-11-10 按 dev 实测真值重做） =====
 *  - 类型 / 来源 / 降级动作三张码表已移到 `types/explanation.ts`（与 reasonLabelOf、degradeActionLabelOf 同口径，
 *    全站唯一）。本页此前自拟的 6 个 issueType 分类（EQUIPMENT_FAILURE 等）**后端一个都不产出**，
 *    dev 上 100% 的行都在显示裸英文码 ⇒ 换成实测的 8 个真码。 */

/** 严重度 → 中文（后端 4 码含 CRITICAL；裸索引未命中会渲染成空白格，故走函数） */
function bizSeverityLabel(severity?: string | null): string {
  const raw = (severity ?? '').trim()
  if (!raw) return '—'
  if (raw === 'CRITICAL') return '严重'
  return (EXPLANATION_SEVERITY_LABELS as Record<string, string>)[raw] ?? `待核对·${raw}`
}

/** 严重度 → ElTag 类型 */
function bizSeverityTag(severity?: string | null): 'info' | 'warning' | 'danger' {
  const raw = (severity ?? '').trim()
  if (raw === 'CRITICAL') return 'danger'
  return (EXPLANATION_SEVERITY_TAG as Record<string, 'info' | 'warning' | 'danger'>)[raw] ?? 'info'
}

/* ===== 业务事实问题筛选（2026-11-10 三项；2026-11-12 再加三项）=====
 * 工厂用户不想一页页翻（dev 合计 82,658 条）。六个筛选都走**后端已支持**的参数：
 *  - severity：ERROR / WARN / INFO（等值）
 *  - source：BOM_WORKSET / MATERIAL_STAGE_CONTEXT（等值）
 *  - materialCode：精确编码（等值）
 *  - issueType：等值（封闭 10 码，5号位 11-11 复函 §三）
 *  - keyword：模糊（BOM 源打 Detail/BOMNO/物料码；MSC 源只打 Detail/物料码）
 *  - factoryCode：等值（5号位 11-11 复函 §二，原先「收而不用」已一并修复） */
const materialInput = ref<string>('')
/** 关键词输入框（回车或点「查」才生效，避免每敲一个字打一次后端） */
const keywordInput = ref<string>('')

/** 严重度筛选
 *  ⚠️ ElSelect clearable 在 Element Plus 2.9.2 清空时 emit 的是 `undefined`（不是 ''），
 *    而 store `setBizFilters` 对 undefined 是**跳过**⇒ 清空会失效。这里统一归一成 ''。 */
function onBizSeverityChange(v: string | undefined): void {
  setBizFilters({ severity: (v ?? '').trim() })
  void fetchBizPage(1, businessFactIssuesPageSize.value)
}

/** 来源筛选 */
function onBizSourceChange(v: string | undefined): void {
  setBizFilters({ source: (v ?? '').trim() })
  void fetchBizPage(1, businessFactIssuesPageSize.value)
}

/** 物料编码：回车或点「查」才生效（避免每敲一个字打一次后端） */
function onBizMaterialSearch(): void {
  const v = materialInput.value.trim()
  materialInput.value = v
  setBizFilters({ materialCode: v })
  void fetchBizPage(1, businessFactIssuesPageSize.value)
}

/** 物料编码清空 */
function onBizMaterialClear(): void {
  materialInput.value = ''
  setBizFilters({ materialCode: '' })
  void fetchBizPage(1, businessFactIssuesPageSize.value)
}

/** 问题类型筛选（等值；值域 = 封闭 10 码） */
function onBizIssueTypeChange(v: string | undefined): void {
  setBizFilters({ issueType: (v ?? '').trim() })
  void fetchBizPage(1, businessFactIssuesPageSize.value)
}

/** 关键词：回车或点「查」才生效 */
function onBizKeywordSearch(): void {
  const v = keywordInput.value.trim()
  keywordInput.value = v
  setBizFilters({ keyword: v })
  void fetchBizPage(1, businessFactIssuesPageSize.value)
}

/** 关键词清空 */
function onBizKeywordClear(): void {
  keywordInput.value = ''
  setBizFilters({ keyword: '' })
  void fetchBizPage(1, businessFactIssuesPageSize.value)
}

/** 工厂筛选（等值） */
function onBizFactoryChange(v: string | undefined): void {
  setBizFilters({ factoryCode: (v ?? '').trim() })
  void fetchBizPage(1, businessFactIssuesPageSize.value)
}

/* ===== 工厂下拉（2026-11-12）=====
 * 选项来源：`GET /api/governance/factories`（后端主数据，名称取后端的，不自己写）
 * ⚠️ 后端该表有 **44 条测试夹具**（`TF_*` / "Test Factory …" / "S1~S4 Factory"，且 IsActive=1）
 *   ⇒ 直接铺全表会把测试厂推给用户；按 5号位 11-11 复函 §二 给的 5 个真业务码白名单过滤。
 * ⚠️ 拉取失败时给空下拉 + 如实提示，**不硬编码兜底**（假选项会让用户选到实库里不存在的工厂）。 */
const factoryOptions = ref<Array<{ code: string; name: string }>>([])
const factoryOptionsError = ref<string>('')

async function loadFactoryOptions(): Promise<void> {
  try {
    const list: Array<{ code: string; name: string }> = await domainApi.listFactories()
    const allow = new Set<string>(BIZ_ISSUE_FACTORY_CODES)
    factoryOptions.value = list
      .filter((f) => allow.has((f.code ?? '').trim()))
      .map((f) => ({ code: f.code, name: f.name }))
    factoryOptionsError.value = ''
  } catch (err) {
    factoryOptions.value = []
    factoryOptionsError.value = (err as Error)?.message ?? '未知错误'
  }
}

/** 工厂码 → 中文名（取不到回退原码，不裸奔英文码） */
function factoryLabel(code?: string | null): string {
  const raw = (code ?? '').trim()
  if (!raw) return '—'
  return factoryOptions.value.find((f) => f.code === raw)?.name ?? raw
}

/* ===== 排产决策原因（2026-11-10 替换原「原因代码分布」标签块） =====
 * 原块统计后端顶层 reasonCode，dev 实测 516/527 条是空串 ⇒ 一屏只有一个「未填原因码」标签，
 * 且自己写着「不用看这里的原始码」——自相矛盾、信息量为零。
 * 现在：只把**写了原因码**的按原因列成小表格（原因 + 条数），没写的条数单独一行说明。 */
const decisionReasonRows = computed(() =>
  (data.value?.reasonCodeCounts ?? [])
    .filter((c: ReasonCodeCount) => (c.reasonCode ?? '').trim() !== '')
    // 配置缺口码（批策略 / 生产部门 / 资源日历）**不是**排产决策取舍：
    // 0号位 若把这些码回写进冻结码表，它们会出现在这里 ⇒ 剔除，
    // 缺口由上方分类卡呈现（带处理入口），不混进「排产决策原因」小表。
    .filter((c: ReasonCodeCount) => !isConfigGapReasonCode(c.reasonCode))
    .map((c: ReasonCodeCount) => ({ label: reasonLabelOf(c.reasonCode), count: c.count }))
    .sort((a: { count: number }, b: { count: number }) => b.count - a.count)
)

/** 没写原因码的事实条数（单独一行说明，不进表格） */
const unlabeledReasonCount = computed(() =>
  (data.value?.reasonCodeCounts ?? [])
    .filter((c: ReasonCodeCount) => (c.reasonCode ?? '').trim() === '')
    .reduce((s: number, c: ReasonCodeCount) => s + (c.count ?? 0), 0)
)

/* ===== 版本下拉（2026-11-17 重做）=====
 * 用户口径：「为啥不列出来已经有的版本呢？需要哪个自己选的去看」⇒ 三件事一起做：
 *   ① 列出**全量**版本 —— 不再用甘特页那套「生效 + 最近 3 条待确认 + 最多 2 条失败」截断
 *      （那套截断是给甘特工具栏设计的；用它 ⇒ 失败批次里最有料的两版根本点不到）
 *   ② 按**来源排产批次**分组 —— 用户心里想的是「批次 #999436 那批」，不是版本号
 *   ③ 每版显示：排程域中文名 + 版本号 + 状态 + 事实条数
 *
 * ⚠️ 事实条数**懒取**（打开下拉才查、并发 3、查到即显示）：dev 实测 61 个版本一次拉完
 *   要 60~90s（零条版本也要 ~2s/个，是固定成本），预取会把首屏拖死；
 *   后端也**没有批量端点**（只有 /api/explanation、/summary、/demand-reconciliation）。
 */

/** 版本下拉的一行视图模型 */
interface VersionOptionVm {
  id: number
  versionCode: string
  /** 排程域中文名（域字典没登记的 key 回退原码，不猜） */
  domainLabel: string
  /** 原始状态值（判生效版本用）+ 中文名 + 标签色 */
  status: string
  statusLabel: string
  statusTag: 'success' | 'primary' | 'warning' | 'info' | 'danger'
  /** 来源排产批次 Id（null = 没有来源批次） */
  sourceRunId: number | null
  /** 事实条数（还没查到 = undefined，**不写 0**） */
  factCount?: number
}

/** URL 入参 ?planVersionId=（运行页「看异常解释」带过来的那一版） */
const incomingVersionId = computed<number | null>(() => {
  const raw = route.query.planVersionId
  const s = Array.isArray(raw) ? raw[0] : raw
  if (!s) return null
  const n = Number(s)
  return Number.isFinite(n) && n > 0 ? n : null
})

const versionOptions = computed<VersionOptionVm[]>(() =>
  allVersions.value.map((v: PlanVersionSummaryDto) => ({
    id: v.id,
    versionCode: v.versionCode,
    domainLabel: domainLabelOf(v.domainKey),
    status: v.status,
    statusLabel: planVersionStatusLabel(v.status),
    statusTag: planVersionStatusTag(v.status),
    sourceRunId: v.sourceScheduleRunId ?? null,
    factCount: versionFactCounts.value[v.id]
  }))
)

/** 版本下拉分组：按来源排产批次；批次内保持后端给的顺序（创建时间倒序） */
const versionGroups = computed<{ key: string; label: string; items: VersionOptionVm[] }[]>(() => {
  const map = new Map<string, { key: string; label: string; items: VersionOptionVm[] }>()
  for (const o of versionOptions.value) {
    const key = o.sourceRunId == null ? 'no-source' : `run-${o.sourceRunId}`
    let g = map.get(key)
    if (!g) {
      g = {
        key,
        label: o.sourceRunId == null ? '无来源批次' : `排产批次 #${o.sourceRunId}`,
        items: []
      }
      map.set(key, g)
    }
    g.items.push(o)
  }
  const groups = [...map.values()]
  // 运行页带过来的那一版：所在分组提到最前、版本提到组内最前（用户一眼看到自己落在哪一版）
  const pinned = incomingVersionId.value
  if (pinned != null) {
    const gi = groups.findIndex((g) => g.items.some((it) => it.id === pinned))
    if (gi > 0) {
      const [g] = groups.splice(gi, 1)
      groups.unshift(g)
    }
    for (const g of groups) {
      const i = g.items.findIndex((it) => it.id === pinned)
      if (i > 0) {
        const [it] = g.items.splice(i, 1)
        g.items.unshift(it)
      }
    }
  }
  return groups
})

/** 当前选中的版本（回显用：批次 / 状态 / 本版条数） */
const selectedVersionOption = computed<VersionOptionVm | null>(
  () => versionOptions.value.find((o) => o.id === currentVersionId.value) ?? null
)

/**
 * 运行页带来的版本不在可见列表里（超出 take 上限，或不在当前用户的域授权范围内）。
 * ⚠️ 此时**不静默改看别的版本**：页面要如实说「这一版看不到」，而不是让人以为看的就是它。
 */
const incomingVersionMissing = computed(
  () =>
    incomingVersionId.value != null &&
    !allVersionsError.value &&
    !versionOptions.value.some((o) => o.id === incomingVersionId.value)
)

/** 下拉里一行的文字（选中后也用它做回显） */
function versionOptionLabel(o: VersionOptionVm): string {
  return `${o.domainLabel} · ${o.versionCode}`
}

/** 打开下拉时按需查各版本事实条数（懒取、并发 3；查到即显示，不阻塞首屏） */
function onVersionDropdownVisible(visible: boolean): void {
  if (!visible) return
  loadFactCounts(versionOptions.value.map((o) => o.id))
}

/** 下拉选中变化 */
function onVersionChange(id: number): void {
  selectVersion(id)
}

/* ===== 加载入口 ===== */
function refresh(): void {
  explanationStore.load()
  // 顶部刷新按钮同时 invalidate business-fact-issues 缓存（@see store load() 缓存语义）
  void refreshBizIssues()
}

function selectVersion(id: number): void {
  if (id === currentVersionId.value) return
  // 10-10 懒加载：Explanation 页只用 scheduleStore 的 currentVersionId 高亮，不需 gantt 数据
  // 改用 setCurrentVersion(id)（仅 ref 更新），不再拖一份甘特图
  // Schedule / Gantt 页仍用 selectVersion(id)（更新 + fetch）
  scheduleStore.setCurrentVersion(id)
  explanationStore.load(id)
  // 版本切换保留 biz-issues 缓存（数据按设计跨版本，@see BusinessFactIssueController.cs 无 planVersionId 参数）
  // 不调用 refreshBizIssues，避免重复请求相同数据
}

/** 业务事实问题 pageSize 变更（10-10 增） */
function onBizSizeChange(size: number): void {
  void changeBizPageSize(size)
}

/** 排程解释事实「继续加载」（2026-11-05）：追加下一页，不覆盖已有 */
function onLoadMoreFacts(): void {
  void loadMoreFacts()
}

/** 业务事实问题分页跳转（10-10 续改：标准 ElPagination）
 *  - 用 v-model:current-page 双向绑定后，store businessFactIssuesPage 已被 ElPagination 同步更新
 *  - 这里仅触发拉取；fetchBizPage 入参沿用 store 当前值以避免竞态
 */
function onBizPageChange(p: number): void {
  void fetchBizPage(p, businessFactIssuesPageSize.value)
}

onMounted(async () => {
  // 域键 → 中文名（不阻塞主数据；取不到就回退原键）
  void domainStore.load()
  // 工厂下拉（白名单过滤，不阻塞主数据）
  void loadFactoryOptions()
  // 版本下拉要**全量**版本（用户 11-17 定调：已有版本都列出来，自己选）
  // ⇒ 走 allVersions（take=200），不走甘特页那套三档截断的 versions
  await scheduleStore.loadAllVersions()
  // 选中态优先级：① URL 入参（运行页带来的那一版）② store 里已有的当前版本 ③ 第一个生效版本
  const incoming = incomingVersionId.value
  if (incoming != null && versionOptions.value.some((o) => o.id === incoming)) {
    scheduleStore.setCurrentVersion(incoming)
  } else if (
    currentVersionId.value == null ||
    !versionOptions.value.some((o) => o.id === currentVersionId.value)
  ) {
    const active = versionOptions.value.find((o) => o.status === 'ACTIVE')
    scheduleStore.setCurrentVersion(active?.id ?? versionOptions.value[0]?.id ?? null)
  }
  // 联动：explanation 默认与 scheduleStore 的当前选中版本（一般是生效版本）保持一致，
  // 避免首次 mount summary 拿的是 store ref 默认值 1001，与 schedule 选中版本错位
  if (currentVersionId.value != null) await explanationStore.load(currentVersionId.value)
  // 业务事实问题走**独立端点** `/api/business-fact-issues`（Summary 端点不返这个数据，
  // dev 2026-11-10 实测响应只有 7 个字段）⇒ 这里必须单独拉第一页，否则表格永远是空的。
  void fetchBizPage(1, businessFactIssuesPageSize.value)
})

/**
 * 运行页在**本页**再点一次「看异常解释」（带另一个 ?planVersionId=）时，
 * 组件不会重新 mount ⇒ 这里盯着 query 变化重新选版本。
 */
watch(
  () => route.query.planVersionId,
  (raw) => {
    const s = Array.isArray(raw) ? raw[0] : raw
    const id = Number(s)
    if (!s || !Number.isFinite(id) || id <= 0) return
    if (id === currentVersionId.value) return
    if (!versionOptions.value.some((o) => o.id === id)) return
    scheduleStore.setCurrentVersion(id)
    void explanationStore.load(id)
  }
)
</script>

<template>
  <div class="aps-explanation">
    <!-- ===== 顶部 ===== -->
    <div class="page-header">
      <div>
        <h2 class="page-title">异常与原因解释</h2>
        <p class="page-sub">
          展示排程决策的真实根因与影响；设备故障仅提示影响评估，不会自动暂停任务
        </p>
      </div>
      <ElButton :loading="loading" @click="refresh"> <Icon icon="vi-ep:refresh" /> 刷新 </ElButton>
    </div>

    <!-- ===== 错误条 ===== -->
    <ElAlert v-if="error" type="error" :closable="false" show-icon :title="`加载失败：${error}`" />

    <!-- ===== 版本切换（2026-11-17 重做：全量版本下拉） =====
         · 列出**所有**已有版本，按来源排产批次分组（用户 11-17：「需要哪个自己选的去看」）
         · 替代旧的三按钮截断（ACTIVE + 最近 3 条待确认 + 最多 2 条失败）——
           那套是甘特工具栏的设计，失败批次里最有料的两版在它下面根本点不到
         · 运行页「看异常解释」带 ?planVersionId= 过来的，自动选中并置顶
    -->
    <div class="version-bar">
      <span class="bar-label">当前计划版本：</span>
      <ElSelect
        :model-value="currentVersionId ?? undefined"
        filterable
        size="small"
        class="version-select"
        placeholder="选择计划版本"
        :loading="allVersionsLoading"
        @visible-change="onVersionDropdownVisible"
        @change="onVersionChange"
      >
        <ElOptionGroup v-for="g in versionGroups" :key="g.key" :label="g.label">
          <ElOption v-for="v in g.items" :key="v.id" :label="versionOptionLabel(v)" :value="v.id">
            <div class="version-option">
              <span class="version-option-domain">{{ v.domainLabel }}</span>
              <span class="version-option-code">{{ v.versionCode }}</span>
              <ElTag size="small" :type="v.statusTag" effect="light">
                {{ v.statusLabel }}
              </ElTag>
              <span class="version-option-count">
                <template v-if="v.factCount !== undefined">事实 {{ v.factCount }} 条</template>
                <template v-else-if="factCountErrors[v.id]">条数查询失败</template>
              </span>
            </div>
          </ElOption>
        </ElOptionGroup>
      </ElSelect>
      <ElTag v-if="incomingVersionId !== null" size="small" type="warning" effect="plain">
        运行页带来的版本
      </ElTag>
      <span v-if="selectedVersionOption" class="muted small">
        来源批次
        {{
          selectedVersionOption.sourceRunId == null ? '无' : `#${selectedVersionOption.sourceRunId}`
        }}
        · 本版事实 {{ totalFactsText }} 条
      </span>
      <span v-if="factCountPending > 0" class="muted small">
        正在查各版本条数（还剩 {{ factCountPending }} 个）
      </span>
      <span v-if="incomingVersionMissing" class="muted small">
        运行页带来的版本 #{{ incomingVersionId }}
        不在可见版本列表里（可能超出显示上限或不在授权范围）
      </span>
    </div>
    <ElAlert
      v-if="allVersionsError"
      type="error"
      :closable="false"
      show-icon
      :title="`版本列表加载失败：${allVersionsError}`"
    />

    <!-- ===== 按对象分组（2026-11-10 用户口径：这条提到页面最上面，
         先回答"这次排产到底产生了哪几类事实"，再往下看订单清单） ===== -->
    <div class="group-summary">
      <span class="group-summary-label">按对象分组：</span>
      <ElTag
        v-for="g in objectTypeCounts"
        :key="g.key"
        size="small"
        :type="g.count > 0 ? 'primary' : 'info'"
        effect="plain"
        class="group-summary-tag"
      >
        {{ g.count > 0 ? g.count : factsHasMore ? '未加载完' : 0 }}
      </ElTag>
      <span class="group-summary-hint">
        {{
          factsHasMore
            ? `本版事实还没加载完（已显示 ${loadedFactsCount} 条 / 共 ${totalFactsText} 条）—— 标「未加载完」的分组不代表没产生，只是还没加载到`
            : '没有数据的分组显示 0 —— 代表这次排产没产生该类事实，不是漏了'
        }}
      </span>
    </div>

    <!-- ===== 排程解释事实（按 objectType 分组；5 码：v5.1.10 :4858） ===== -->
    <ElCard class="panel">
      <template #header>
        <div class="panel-header">
          <span> 排程解释事实 </span>
          <!-- 计数行（2026-11-05）：把「一共多少条 / 展示了多少条」摆在明面上，
               杜绝再出现「静默只显示 50 条」那种用户看不出被截断的情况 -->
          <div class="fact-count">
            <span class="hint-text">
              共 {{ totalFactsText }} 条 · 已显示 {{ loadedFactsCount }} 条
            </span>
            <ElButton
              v-if="factsHasMore"
              size="small"
              :loading="factsLoading"
              @click="onLoadMoreFacts"
            >
              继续加载
            </ElButton>
          </div>
        </div>
      </template>
      <div v-if="!data?.scheduleExplanationFacts?.length" class="panel-empty">
        <ElEmpty description="暂无解释事实" />
      </div>

      <!-- ② 分类卡：按"要人动手的几类"聚合；点卡片即筛选下方清单 -->
      <div v-if="reasonClassCards.length" class="class-cards">
        <div
          v-for="c in reasonClassCards"
          :key="c.key"
          class="class-card"
          :class="{ active: filterClass === c.key }"
          @click="toggleClass(c.key)"
        >
          <div class="class-card-top">
            <span class="class-card-name">{{ c.title }}</span>
            <span class="class-card-count">{{ c.count }}</span>
          </div>
          <div class="class-card-hint">{{ c.hint }}</div>
          <div class="class-card-meta">涉及 {{ c.materialCount }} 个物料</div>
          <div class="class-card-foot">
            <ElButton
              v-if="reasonActionOf(c.key)"
              link
              type="primary"
              size="small"
              @click.stop="goReasonAction(c.key)"
            >
              {{ reasonActionText(c.key) }}
            </ElButton>
            <span v-else class="class-card-note">
              {{ c.external ? '需人工跟进' : '知悉即可' }}
            </span>
          </div>
        </div>
      </div>

      <!-- ③ 筛选与排序 -->
      <div class="fact-filters">
        <ElInput
          v-model="keyword"
          placeholder="订单号 / 物料编码 / 对象"
          clearable
          size="small"
          class="filter-keyword"
        />
        <ElSelect
          v-model="filterSeverity"
          placeholder="级别"
          clearable
          size="small"
          class="filter-select"
        >
          <ElOption label="错误" value="ERROR" />
          <ElOption label="警告" value="WARN" />
          <ElOption label="提示" value="INFO" />
        </ElSelect>
        <!-- 对象类型筛选（2026-11-21 并表后新增）：一张清单里按类型分开看 -->
        <ElSelect
          v-model="filterObjectType"
          placeholder="对象类型"
          clearable
          size="small"
          class="filter-select"
        >
          <ElOption
            v-for="o in objectTypeOptions"
            :key="o.key"
            :label="`${o.label}（${o.count}）`"
            :value="o.key"
          />
        </ElSelect>
        <ElSelect v-model="sortBy" size="small" class="filter-select">
          <ElOption label="按未排数量从多到少" value="qty" />
          <ElOption label="按交期从早到晚" value="due" />
        </ElSelect>
        <ElButton v-if="filterClass" link type="primary" size="small" @click="filterClass = ''">
          清除分类筛选
        </ElButton>
        <span class="filter-stat">
          共 {{ filteredRows.length }} 条异常 · 未排合计 {{ fmtQty(filteredUnscheduledQty) }} 件
          <template v-if="orderIndexState === 'skipped'">（订单太多，未取交期）</template>
        </span>
      </div>

      <!-- ④ 未排需求清单 -->
      <!-- ④ 异常清单（一张表 + 行内展开详情；2026-11-21 v0.2 稿 §P08）
           订单级 / 任务级 / 资源级 / 工段级 / 排程域级 / 未入字典类型 全在一张表里，
           用「对象类型」列区分；点行首箭头展开看身份 / 数量 / 日期 / 可证证据 /
           正式 Explanation / 相关影响对象 / 去哪处理。 -->
      <ElTable v-if="pagedRows.length" :data="pagedRows" size="small" border class="fact-table">
        <ElTableColumn type="expand" width="36">
          <template #default="{ row }">
            <div class="anomaly-detail">
              <div class="ad-title">异常对象身份</div>
              <ElDescriptions :column="3" size="small" border class="ad-desc">
                <ElDescriptionsItem label="对象类型">
                  {{ row.objectTypeLabel }}
                </ElDescriptionsItem>
                <ElDescriptionsItem label="对象">
                  {{ row.objectLabel || '未提供' }}
                </ElDescriptionsItem>
                <ElDescriptionsItem label="物料编码">
                  {{ row.materialCode || '未提供' }}
                </ElDescriptionsItem>
                <ElDescriptionsItem label="排程域">
                  {{ row.domainName || '未提供' }}
                </ElDescriptionsItem>
                <ElDescriptionsItem label="严重度">
                  {{ EXPLANATION_SEVERITY_LABELS[row.severity] }}
                </ElDescriptionsItem>
                <ElDescriptionsItem label="记录时间">
                  {{
                    row.occurredAt ? dayjs(row.occurredAt).format('YYYY-MM-DD HH:mm') : '未提供'
                  }}
                </ElDescriptionsItem>
                <ElDescriptionsItem label="技术标识">
                  {{ row.objectRef || '未提供' }}
                </ElDescriptionsItem>
              </ElDescriptions>

              <div class="ad-title">数量与日期</div>
              <div class="ad-text">
                交期：{{ row.dueDate ? dayjs(row.dueDate).format('YYYY-MM-DD') : '未提供' }}
                <template v-if="row.demand !== undefined || row.unscheduled !== undefined">
                  ；数量：{{ row.qtyLabels.demand }} {{ fmtQty(row.demand) }} /
                  {{ row.qtyLabels.scheduled }} {{ fmtQty(row.scheduled) }} /
                  {{ row.qtyLabels.unscheduled }} {{ fmtQty(row.unscheduled) }}
                </template>
              </div>

              <div class="ad-title">可证证据</div>
              <div class="ad-text">{{ row.evidenceSummary || '未提供' }}</div>

              <div class="ad-title">正式 Explanation</div>
              <div class="ad-text">
                {{ row.decisionCodeLabel || '原因未提供 / 未判定' }}
              </div>

              <div class="ad-title">相关影响对象</div>
              <div class="ad-text">{{ row.relatedObjects || '未提供' }}</div>

              <div class="ad-title">去哪处理</div>
              <div class="ad-nav">
                <ElButton
                  v-for="n in navsOf(row)"
                  :key="n.text"
                  link
                  type="primary"
                  size="small"
                  @click="goNav(n)"
                >
                  {{ n.text }}
                </ElButton>
                <span v-if="!navsOf(row).length" class="muted-tag">—</span>
              </div>
            </div>
          </template>
        </ElTableColumn>
        <ElTableColumn label="对象类型" width="100">
          <template #default="{ row }">
            <!-- 未入冻结字典的对象类型如实露出原值（5号位 10-12 §二：不丢弃） -->
            <ElTag
              v-if="row.objectTypeLabel.startsWith('待核对')"
              size="small"
              type="warning"
              effect="plain"
            >
              待核对·{{ row.objectType }}
            </ElTag>
            <ElTag v-else size="small" effect="plain">{{ row.objectTypeLabel }}</ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn label="订单号" width="140">
          <template #default="{ row }">
            <a
              v-if="row.orderNo"
              href="javascript:;"
              class="cell-order"
              @click="goOrder(row.orderNo)"
              >{{ row.orderNo }}</a
            >
            <span v-else class="cell-order">—</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="物料编码" min-width="200" show-overflow-tooltip>
          <template #default="{ row }">{{ row.materialCode || '—' }}</template>
        </ElTableColumn>
        <ElTableColumn label="交期" width="120">
          <template #default="{ row }">
            {{ row.dueDate ? dayjs(row.dueDate).format('YYYY-MM-DD') : '—' }}
          </template>
        </ElTableColumn>
        <ElTableColumn label="数量（需求 / 已排 / 未排，单位：件）" width="240">
          <template #default="{ row }">
            <span v-if="row.isPi" class="qty-pi">
              {{ row.qtyLabels.demand }} {{ fmtQty(row.demand) }} / {{ row.qtyLabels.scheduled }}
              {{ fmtQty(row.scheduled) }} /
              <span class="qty-unsched"
                >{{ row.qtyLabels.unscheduled }} {{ fmtQty(row.unscheduled) }}</span
              >
            </span>
            <template v-else-if="row.demand !== undefined || row.unscheduled !== undefined">
              <span class="qty-demand">{{ fmtQty(row.demand) }}</span>
              <span class="qty-sep">/</span>
              <span class="qty-sched">{{ fmtQty(row.scheduled) }}</span>
              <span class="qty-sep">/</span>
              <span class="qty-unsched">{{ fmtQty(row.unscheduled) }}</span>
            </template>
            <span v-else class="muted-tag">—</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="卡住阶段" width="130">
          <template #default="{ row }">
            <div v-if="row.stageCode">{{ stageLabelOf(row.stageCode) }}</div>
            <span v-else class="muted-tag">—</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="问题" min-width="220">
          <template #default="{ row }">
            <div class="cell-reason">
              <!-- 冻结 15 码：有值时按全站唯一口径 reasonLabelOf 显示（缺了会丢排产决策的具体原因）。
                   配置缺口码不在此显示（由下面那个分类标签表达）⇒ 顶层 reasonCode 被 0号位
                   回写成缺口码时，页面不会出现裸英文码。 -->
              <ElTag v-if="row.decisionCodeLabel" size="small" type="primary" effect="plain">
                {{ row.decisionCodeLabel }}
              </ElTag>
              <ElTag size="small" :type="REASON_TAG_TYPE[row.classKey]" effect="plain">
                {{ row.reasonMissing ? '原因未提供' : row.classMeta.label }}
              </ElTag>
              <ElTag
                v-if="row.severity !== 'ERROR'"
                size="small"
                :type="EXPLANATION_SEVERITY_TAG[row.severity]"
                effect="plain"
              >
                {{ EXPLANATION_SEVERITY_LABELS[row.severity] }}
              </ElTag>
            </div>
          </template>
        </ElTableColumn>
        <!-- 可证证据（后端 evidence.summary；2026-11-21 v0.2 稿 §P08「行内详情…可证证据」） -->
        <ElTableColumn label="说明" min-width="220" show-overflow-tooltip>
          <template #default="{ row }">
            <span v-if="row.evidenceSummary">{{ row.evidenceSummary }}</span>
            <span v-else class="muted-tag">—</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="处理入口" width="160">
          <template #default="{ row }">
            <ElButton
              v-for="n in navsOf(row)"
              :key="n.text"
              link
              type="primary"
              size="small"
              @click="goNav(n)"
            >
              {{ n.text }}
            </ElButton>
            <span v-if="!navsOf(row).length" class="muted-tag">—</span>
          </template>
        </ElTableColumn>
      </ElTable>
      <div v-else-if="anomalyRows.length" class="panel-empty">
        <ElEmpty description="当前筛选条件下没有异常" />
      </div>

      <div v-if="filteredRows.length" class="fact-pagination">
        <ElPagination
          v-model:current-page="factPage"
          v-model:page-size="factPageSize"
          :total="filteredRows.length"
          :page-sizes="[20, 50, 100, 200]"
          layout="total, sizes, prev, pager, next, jumper"
          background
          :pager-count="5"
        />
      </div>

      <!-- ⑤ 排程域体检（DOMAIN 级事实 → 体检表；2026-11-10 用户口径"这块儿乱，改成表格"） -->
      <div v-if="domainHealth.length" class="domain-health">
        <div class="domain-health-title">排程域体检</div>
        <ElTable :data="domainHealth" size="small" border class="health-table">
          <ElTableColumn label="排程域" width="150">
            <template #default="{ row }">
              <span class="health-domain">{{ row.domainName }}</span>
            </template>
          </ElTableColumn>
          <ElTableColumn label="体检项" width="150">
            <template #default="{ row }">
              <ElTag size="small" effect="plain">{{ row.typeLabel }}</ElTag>
            </template>
          </ElTableColumn>
          <ElTableColumn label="指标" min-width="230">
            <template #default="{ row }">
              <div class="health-metrics">
                <div
                  v-for="m in row.metrics"
                  :key="m.label"
                  class="health-metric"
                  :class="m.tone ?? 'info'"
                >
                  <span class="hm-label">{{ m.label }}</span>
                  <span class="hm-value">{{ m.value }}</span>
                </div>
              </div>
            </template>
          </ElTableColumn>
          <ElTableColumn label="结论 / 要办的事" min-width="240">
            <template #default="{ row }">
              <div class="health-summary">{{ row.summary }}</div>
              <div v-if="row.todo" class="health-todo">要办：{{ row.todo }}</div>
            </template>
          </ElTableColumn>
          <ElTableColumn label="样例物料" min-width="180">
            <template #default="{ row }">
              <div v-if="row.samples.length" class="health-samples">
                <ElTag
                  v-for="s in row.samples"
                  :key="s"
                  size="small"
                  effect="plain"
                  class="sample-tag"
                >
                  {{ s }}
                </ElTag>
              </div>
              <span v-else class="muted-tag">—</span>
            </template>
          </ElTableColumn>
        </ElTable>
      </div>

      <!-- 待核对：未入字典的 ObjectType（5号位 10-12 §二；Q1 DEMAND 待 0号位 最终确认）
           2026-11-21（v0.2 稿 §P08「仅一页，一张异常列表＋行内展开详情」）：
           这些行已在上面那张异常清单里（「对象类型」列标「待核对·原值」）
           ⇒ 这里不再另起一块清单，只留一句说明 + 条数。 -->
      <ElAlert
        v-if="unrecognizedObjectTypeFacts.length"
        type="info"
        :closable="false"
        show-icon
        class="unrecognized-alert"
      >
        <template #title>
          <span>
            待核对：{{ unrecognizedObjectTypeFacts.length }} 条事实的对象类型未在冻结字典（{{
              unrecognizedRawObjectTypes.join('、')
            }}），已在上方清单里逐条标「待核对·原值」列出
          </span>
        </template>
      </ElAlert>
    </ElCard>

    <!-- ===== 业务事实问题 ===== -->
    <div class="content-row">
      <!-- 业务事实问题（@see BusinessFactIssueDto） -->
      <ElCard class="panel">
        <template #header>
          <div class="panel-header">
            <span> 业务事实问题 </span>
            <span class="hint-text"
              >主数据缺项 / 对不上的地方 —— 排产先按兜底规则走，这里是要人去补的</span
            >
          </div>
        </template>
        <!-- 口径提示（2026-11-21；v0.2 稿 §P08 查询条件 + 「只有数据确有可查询结果才展示」）：
             这一区的端点不带 planVersionId ⇒ 是跨版本的主数据问题汇总，
             与上面按计划版本筛的「排程解释事实」不是同一批，别当成同一件事看。 -->
        <div class="biz-filter-note">
          这一区是<strong>跨版本</strong>的主数据问题汇总（不受上面的计划版本限制）；
          上面「排程解释事实」才是当前所选版本的问题。两边不是同一批。
        </div>
        <!-- ===== 筛选（2026-11-10 增三项；2026-11-12 再加三项）：后端已支持的参数 ===== -->
        <div class="biz-filters">
          <ElSelect
            :model-value="bizSeverity"
            placeholder="严重度：全部"
            size="small"
            clearable
            class="biz-filter-item"
            @change="onBizSeverityChange"
          >
            <ElOption label="错误（必须先处理）" value="ERROR" />
            <ElOption label="警告（要留意）" value="WARN" />
            <ElOption label="提示（可先不管）" value="INFO" />
          </ElSelect>
          <ElSelect
            :model-value="bizSource"
            placeholder="来源：全部"
            size="small"
            clearable
            class="biz-filter-item"
            @change="onBizSourceChange"
          >
            <ElOption label="BOM 维护" value="BOM_WORKSET" />
            <ElOption label="工段生产部门" value="MATERIAL_STAGE_CONTEXT" />
          </ElSelect>
          <ElSelect
            :model-value="bizIssueType"
            placeholder="问题类型：全部"
            size="small"
            clearable
            class="biz-filter-item biz-filter-wide"
            @change="onBizIssueTypeChange"
          >
            <ElOption
              v-for="t in BIZ_ISSUE_TYPE_CODES"
              :key="t"
              :label="bizIssueTypeLabelOf(t)"
              :value="t"
            />
          </ElSelect>
          <ElSelect
            :model-value="bizFactoryCode"
            placeholder="工厂：全部"
            size="small"
            clearable
            class="biz-filter-item"
            @change="onBizFactoryChange"
          >
            <ElOption v-for="f in factoryOptions" :key="f.code" :label="f.name" :value="f.code" />
          </ElSelect>
          <ElInput
            v-model="materialInput"
            placeholder="物料编码（完整编码）"
            size="small"
            clearable
            class="biz-filter-item biz-filter-code"
            @keyup.enter="onBizMaterialSearch"
            @clear="onBizMaterialClear"
          >
            <template #append>
              <ElButton size="small" @click="onBizMaterialSearch">查</ElButton>
            </template>
          </ElInput>
          <ElInput
            v-model="keywordInput"
            placeholder="关键词（单据号 / 物料 / 说明文字）"
            size="small"
            clearable
            class="biz-filter-item biz-filter-code"
            @keyup.enter="onBizKeywordSearch"
            @clear="onBizKeywordClear"
          >
            <template #append>
              <ElButton size="small" @click="onBizKeywordSearch">查</ElButton>
            </template>
          </ElInput>
        </div>
        <!-- 工厂下拉拉取失败：如实报错，不给假选项（否则用户会选到实库里没有的工厂） -->
        <div v-if="factoryOptionsError" class="biz-filter-error">
          工厂列表取不到，暂时不能按工厂筛选：{{ factoryOptionsError }}
        </div>
        <!-- 选了工厂时的口径提示（跨厂问题不归属单一工厂 ⇒ 选了某个工厂就看不到它们） -->
        <div v-if="bizFactoryCode" class="biz-filter-note">
          只看{{
            factoryLabel(bizFactoryCode)
          }}的问题。跨厂的问题（牵扯到两个及以上工厂）不归任何一个工厂，不会出现在这里。
        </div>
        <div v-if="!businessFactIssues.length" class="panel-empty">
          <ElEmpty description="没有符合条件的问题" />
        </div>
        <ElTable v-else :data="sortedBusinessFactIssues" size="small" border>
          <!-- 行内展开详情（2026-11-21 v0.2 稿 §P08「一张异常列表＋行内展开详情」）：
               列表里已能看懂的字段不再重复展开，展开只放被省略的说明全文与处理入口。 -->
          <ElTableColumn type="expand" width="36">
            <template #default="{ row }">
              <div class="anomaly-detail">
                <div class="ad-title">问题说明</div>
                <div class="ad-text">{{ row.detail || '未提供' }}</div>
                <div class="ad-title">来源与降级</div>
                <ElDescriptions :column="3" size="small" border class="ad-desc">
                  <ElDescriptionsItem label="来源">
                    {{ bizSourceLabelOf(row.source) }}
                  </ElDescriptionsItem>
                  <ElDescriptionsItem label="问题类型">
                    {{ bizIssueTypeLabelOf(row.issueType) }}
                  </ElDescriptionsItem>
                  <ElDescriptionsItem label="降级动作">
                    {{ row.degradeAction ? degradeActionLabelOf(row.degradeAction) : '无降级' }}
                  </ElDescriptionsItem>
                  <ElDescriptionsItem label="单据号">
                    {{ row.documentNo || '未提供' }}
                  </ElDescriptionsItem>
                  <ElDescriptionsItem label="工段 / 工厂">
                    {{
                      row.stageCode
                        ? stageLabelOf(row.stageCode)
                        : row.factoryCode
                          ? factoryLabel(row.factoryCode)
                          : '未提供'
                    }}
                  </ElDescriptionsItem>
                  <ElDescriptionsItem label="发生时间">
                    {{ dayjs(row.occurredAt).format('YYYY-MM-DD HH:mm') }}
                  </ElDescriptionsItem>
                </ElDescriptions>
                <div class="ad-title">去哪处理</div>
                <div class="ad-nav">
                  <ElButton
                    v-if="bizNavOf(row.source)"
                    link
                    type="primary"
                    size="small"
                    @click="goBizNav(row.source)"
                  >
                    {{ bizNavOf(row.source)?.text }}
                  </ElButton>
                  <span v-else class="muted-tag">—</span>
                </div>
              </div>
            </template>
          </ElTableColumn>
          <ElTableColumn label="严重度" width="90">
            <template #default="{ row }">
              <ElTag size="small" :type="bizSeverityTag(row.severity)">
                {{ bizSeverityLabel(row.severity) }}
              </ElTag>
            </template>
          </ElTableColumn>
          <ElTableColumn label="问题类型" width="180">
            <template #default="{ row }">
              <div class="cell-issue-type">{{ bizIssueTypeLabelOf(row.issueType) }}</div>
              <div class="cell-issue-source">{{ bizSourceLabelOf(row.source) }}</div>
            </template>
          </ElTableColumn>
          <ElTableColumn label="物料编码" width="170">
            <template #default="{ row }">
              <span v-if="row.materialCode">{{ row.materialCode }}</span>
              <span v-else class="muted-tag">—</span>
            </template>
          </ElTableColumn>
          <ElTableColumn label="工段 / 工厂" width="140">
            <template #default="{ row }">
              <div v-if="row.stageCode">{{ stageLabelOf(row.stageCode) }}</div>
              <div v-else-if="row.factoryCode">{{ factoryLabel(row.factoryCode) }}</div>
              <span v-else class="muted-tag">—</span>
            </template>
          </ElTableColumn>
          <ElTableColumn label="单据号" width="150">
            <template #default="{ row }">
              <span v-if="row.documentNo">{{ row.documentNo }}</span>
              <span v-else class="muted-tag">—</span>
            </template>
          </ElTableColumn>
          <ElTableColumn prop="detail" label="问题说明" min-width="280" show-overflow-tooltip />
          <!-- 降级动作（冻结 07:857 字段名）：值域 = 冻结 6 码 + 08 DDL `USE_MAX_BOMNO` + `CROSS_FACTORY_EDGE_SKIP`
               ，实测另有中文自由文本 ⇒ 走 `degradeActionLabelOf`（未知值原样显示）。空 = 无降级。 -->
          <ElTableColumn label="降级动作" width="150">
            <template #default="{ row }">
              <span v-if="row.degradeAction">{{ degradeActionLabelOf(row.degradeAction) }}</span>
              <span v-else class="muted-tag">—</span>
            </template>
          </ElTableColumn>
          <ElTableColumn label="发生时间" width="120">
            <template #default="{ row }">
              {{ dayjs(row.occurredAt).format('MM-DD HH:mm') }}
            </template>
          </ElTableColumn>
          <!-- 去对应专业页（2026-11-21；v0.2 稿 §P08「按真实结果回到原专业页面」）：
               只挂后端真实有来源的两类；BOM / 生产部门上下文没有对应专业页 ⇒ 不给假入口 -->
          <ElTableColumn label="去查" width="130" align="center">
            <template #default="{ row }">
              <ElButton
                v-if="bizNavOf(row.source)"
                link
                type="primary"
                size="small"
                @click="goBizNav(row.source)"
              >
                {{ bizNavOf(row.source)?.text }}
              </ElButton>
              <span v-else class="muted-tag">—</span>
            </template>
          </ElTableColumn>
        </ElTable>
        <!-- ===== 分页（10-10 续改：标准 ElPagination） ===== -->
        <div v-if="businessFactIssuesTotal > 0" class="biz-pagination">
          <ElPagination
            v-model:current-page="businessFactIssuesPage"
            v-model:page-size="businessFactIssuesPageSize"
            :total="businessFactIssuesTotal"
            :page-sizes="[20, 50, 100, 200]"
            :disabled="businessFactIssuesLoading"
            layout="total, sizes, prev, pager, next, jumper"
            background
            :pager-count="5"
            @current-change="onBizPageChange"
            @size-change="onBizSizeChange"
          />
        </div>
      </ElCard>
    </div>

    <!-- ===== 排产决策原因（2026-11-10 替换原「原因代码分布」标签块） ===== -->
    <!-- ⚠️ 卡片显示条件含「没写原因码的条数」：dev ACTIVE 版本（NIGHTLY_20261107_FAMILY_XC）
         998 条事实的原因码**全是空串** ⇒ 若只按 decisionReasonRows.length 显示，整块会消失，
         用户以为功能坏了。空的时候给一句大白话，别藏。 -->
    <ElCard v-if="decisionReasonRows.length || unlabeledReasonCount > 0" class="panel">
      <template #header>
        <div class="panel-header">
          <span> 排产决策原因 </span>
          <span class="hint-text">排产时按规则做的取舍 —— 哪一类原因占得多，就先看那一类</span>
        </div>
      </template>
      <ElTable
        v-if="decisionReasonRows.length"
        :data="decisionReasonRows"
        size="small"
        border
        class="decision-reason-table"
      >
        <ElTableColumn prop="label" label="原因" min-width="220" />
        <ElTableColumn prop="count" label="条数" width="120" />
      </ElTable>
      <ElEmpty v-else description="这次排产没记录到具体的决策原因" :image-size="60" />
      <div v-if="unlabeledReasonCount > 0" class="code-hint">
        另有
        {{ unlabeledReasonCount }}
        条记录原因未提供，看不出属于哪一类取舍；逐条请看上面的「业务事实问题」清单。
      </div>
    </ElCard>
  </div>
</template>

<style lang="less" scoped>
.aps-explanation {
  display: flex;
  flex-direction: column;
  gap: 12px;
  min-height: calc(100vh - 100px);
  padding: 16px;
  background: #f5f7fa;
}

/* ===== 顶部 ===== */
.page-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding-bottom: 4px;

  .page-title {
    margin: 0;
    font-size: 18px;
    font-weight: 600;
    color: #1e293b;
  }

  .page-sub {
    margin: 4px 0 0;
    font-size: 12px;
    color: #94a3b8;
  }
}

/* ===== 版本切换条 ===== */
.version-bar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  background: #fff;
  border-radius: 6px;
  box-shadow: 0 1px 2px rgb(15 23 42 / 4%);

  .bar-label {
    margin-right: 4px;
    font-size: 12px;
    color: #64748b;
  }

  .version-code {
    font-size: 12px;
  }

  /* 版本下拉（2026-11-17） */
  .version-select {
    width: 340px;
  }
}

/* 下拉里的一行：域中文名 + 版本号 + 状态 + 条数 */
.version-option {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 12px;

  .version-option-domain {
    font-weight: 600;
    color: #1e293b;
  }

  .version-option-code {
    color: #64748b;
  }

  .version-option-count {
    margin-left: auto;
    color: #94a3b8;
  }
}

/* ===== 内容面板通用 ===== */
.panel {
  border-radius: 6px;

  :deep(.el-card__header) {
    padding: 12px 16px;
    background: #fafbfc;
    border-bottom: 1px solid #f1f5f9;
  }

  :deep(.el-card__body) {
    padding: 16px;
  }
}

.panel-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  font-size: 14px;
  font-weight: 600;
  color: #1e293b;

  .hint-text {
    font-size: 12px;
    font-weight: 400;
    color: #94a3b8;
  }
}

.panel-empty {
  padding: 24px 0;
  text-align: center;
}

/* 空值占位（表格里的「—」） */
.muted-tag {
  color: #cbd5e1;
}

/* ===== 排程解释事实面板头：计数行 + 继续加载（2026-11-05） ===== */
.fact-count {
  display: flex;
  align-items: center;
  gap: 8px;

  .hint-text {
    font-size: 12px;
    font-weight: 400;
    color: #94a3b8;
    white-space: nowrap;
  }
}

.content-row {
  display: grid;
  grid-template-columns: 1fr;
  gap: 12px;
}

/* ===== 分类卡（按原因归类；点卡片即筛选清单） ===== */
.class-cards {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: 10px;
  margin-bottom: 12px;
}

.class-card {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 12px;
  background: #fff;
  border: 1px solid #e2e8f0;
  border-left: 3px solid #cbd5e1;
  border-radius: 6px;
  cursor: pointer;
  transition:
    box-shadow 0.15s ease,
    border-color 0.15s ease;

  &:hover {
    box-shadow: 0 2px 6px rgb(15 23 42 / 8%);
  }

  &.active {
    border-color: #409eff;
    border-left-color: #409eff;
    background: #f0f7ff;
  }

  .class-card-top {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 8px;
  }

  .class-card-name {
    font-size: 13px;
    font-weight: 600;
    color: #1e293b;
  }

  .class-card-count {
    font-size: 20px;
    font-weight: 600;
    line-height: 1;
    color: #b91c1c;
  }

  .class-card-hint {
    font-size: 12px;
    line-height: 1.5;
    color: #475569;
  }

  .class-card-meta {
    font-size: 11px;
    color: #94a3b8;
  }

  .class-card-foot {
    margin-top: 2px;
    min-height: 20px;
  }

  .class-card-note {
    font-size: 11px;
    color: #94a3b8;
  }
}

/* 分组总览（五类对象分组 + 条数；0 也列出来，回答"分组齐不齐"）
   2026-11-10：提到页面最上面，做成一条独立横条（跟版本条同一视觉层） */
.group-summary {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  padding: 8px 12px;
  background: #fff;
  border-radius: 6px;
  box-shadow: 0 1px 2px rgb(15 23 42 / 4%);

  .group-summary-label {
    font-size: 12px;
    color: #64748b;
  }

  .group-summary-hint {
    font-size: 11px;
    color: #94a3b8;
  }
}

/* ===== 筛选与排序栏 ===== */
.fact-filters {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  padding: 10px 0;

  .filter-keyword {
    width: 220px;
  }

  .filter-select {
    width: 160px;
  }

  .filter-stat {
    margin-left: auto;
    font-size: 12px;
    color: #64748b;
  }
}

/* ===== 未排需求清单 ===== */
.fact-table {
  .cell-order {
    font-family: 'Courier New', monospace;
    font-size: 12px;
    font-weight: 600;
    color: #1e293b;
  }

  .cell-reason {
    display: flex;
    flex-wrap: wrap;
    gap: 4px;
  }

  .qty-demand {
    font-weight: 600;
    color: #1e293b;
  }

  .qty-sep {
    margin: 0 4px;
    color: #cbd5e1;
  }

  .qty-sched {
    color: #64748b;
  }

  .qty-unsched {
    font-weight: 600;
    color: #b91c1c;
  }

  /* 生产指令超发行：三个数量口径不同，逐格自带标签 */
  .qty-pi {
    color: #475569;
    font-size: 12px;

    .qty-unsched {
      font-weight: 600;
      color: #b91c1c;
    }
  }
}

.fact-pagination {
  display: flex;
  justify-content: flex-end;
  padding: 12px 0 0;

  :deep(.el-pagination__total) {
    margin-right: auto;
    font-size: 12px;
    color: #64748b;
  }
}

/* ===== 排程域体检（2026-11-10 改成表格：原来一张张卡片看着乱） ===== */
.domain-health {
  margin-top: 16px;
  padding-top: 12px;
  border-top: 1px dashed #e2e8f0;

  .domain-health-title {
    margin-bottom: 8px;
    font-size: 13px;
    font-weight: 600;
    color: #1e293b;
  }
}

.health-table {
  .health-domain {
    font-size: 13px;
    font-weight: 600;
    color: #1e293b;
  }

  .health-metrics {
    display: flex;
    flex-wrap: wrap;
    gap: 6px;
  }

  .health-metric {
    display: flex;
    flex-direction: column;
    gap: 2px;
    min-width: 92px;
    padding: 4px 8px;
    background: #fff;
    border: 1px solid #e2e8f0;
    border-radius: 4px;

    .hm-label {
      font-size: 11px;
      color: #94a3b8;
    }

    .hm-value {
      font-size: 13px;
      font-weight: 600;
      color: #1e293b;
    }

    &.danger {
      border-color: #fecaca;
      background: #fef2f2;

      .hm-value {
        color: #b91c1c;
      }
    }

    &.warn {
      border-color: #fde68a;
      background: #fffbeb;

      .hm-value {
        color: #b45309;
      }
    }
  }

  .health-summary {
    font-size: 12px;
    line-height: 1.6;
    color: #334155;
  }

  .health-todo {
    margin-top: 4px;
    font-size: 12px;
    font-weight: 600;
    color: #b45309;
  }

  .health-samples {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 4px;

    .sample-tag {
      font-family: 'Courier New', monospace;
      font-size: 11px;
    }
  }
}

/* ===== 待核对：未入字典 ObjectType（5号位 10-12 §二） ===== */
.unrecognized-alert {
  margin-top: 12px;
}

/* ===== 行内展开详情（2026-11-21 v0.2 稿 §P08「一张异常列表＋行内展开详情」） ===== */
.anomaly-detail {
  padding: 4px 8px 8px 8px;

  .ad-title {
    margin: 10px 0 6px;
    font-size: 12px;
    font-weight: 600;
    color: #475569;
  }

  .ad-text {
    font-size: 12px;
    line-height: 1.6;
    color: #334155;
    word-break: break-all;
  }

  .ad-desc {
    margin-top: 4px;
  }

  .ad-nav {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 12px;
  }
}

/* ===== 业务事实问题分页（10-10 续改：标准 ElPagination） ===== */
.biz-pagination {
  display: flex;
  justify-content: flex-end;
  padding: 12px 16px;
  border-top: 1px solid #f1f5f9;

  :deep(.el-pagination) {
    --el-pagination-bg-color: #fafbfc;
  }

  :deep(.el-pagination__total) {
    margin-right: auto;
    font-size: 12px;
    color: #64748b;
  }
}

/* ===== 业务事实问题筛选行（2026-11-10 增） ===== */
.biz-filters {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  margin-bottom: 12px;
}

.biz-filter-item {
  width: 190px;
}

.biz-filter-wide {
  width: 230px;
}

.biz-filter-code {
  width: 280px;
}

/* 工厂下拉取数失败提示（如实报错，不给假选项） */
.biz-filter-error {
  margin-bottom: 8px;
  font-size: 12px;
  color: #b91c1c;
}

/* 选了工厂后的口径提示（跨厂问题不归单一工厂） */
.biz-filter-note {
  margin-bottom: 8px;
  font-size: 12px;
  line-height: 1.6;
  color: #64748b;
}

/* ===== 问题类型列：中文名 + 来源小字 ===== */
.cell-issue-type {
  font-size: 13px;
  font-weight: 600;
  color: #1e293b;
  line-height: 1.4;
}

.cell-issue-source {
  margin-top: 2px;
  font-size: 11px;
  color: #94a3b8;
}

/* ===== 排产决策原因小表格 ===== */
.decision-reason-table {
  max-width: 560px;
}

.code-hint {
  margin-top: 10px;
  font-size: 12px;
  color: #94a3b8;
  line-height: 1.6;
}
</style>
