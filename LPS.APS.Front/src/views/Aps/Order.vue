<script setup lang="ts">
/**
 * APS V1 4号位 — 订单/需求计划（页面 2）
 *
 * 冻结依据：
 *  - `06 v1.38 §7.2`：5号位向4号位提供「订单/需求计划」查询
 *  - `13 v1.8 §七`：用户输出展示含「订单计划」；`§十一` 禁止页面内计算排程结果
 *  - Order.Status 值域取「数据流向」口径：`07:3474` 的 `Order_Canonical.Status`
 *    = `OPEN/CLOSED/CANCELLED`，`sp_SyncOrdersToPartitionTable` 原样复制进 `[Order].Status`
 *    （`07:3611` 另有 6 值写法，与复制逻辑冲突，待 0号位 澄清；见 `types/common.ts`）
 *  - `07:2666` DelayStatus 值域 `ON_TIME/FIRST_DELAY/REPEATED_DELAY`
 *    （与 Status 是两个独立维度，禁止混用）
 *
 * 2026-10-17 按 5号位 回执整页重做（此前页面挂在一份自拟 DTO 上）：
 *  - 列表字段按真实 `OrderListItemDto`（`id` / `quantity` / `mtS_InstructionNo` / `priority` …）
 *  - 详情按真实 `{order, pegging, tasks}`（**没有 reasons 键**）
 *  - 硬编码的工厂 / 产品族 / 客户下拉（`F-SUZ-01`、`INJECTION`、`CUST-001` 等）全部删除 ——
 *    dev 实库是 `CN` / `CYL` / 中文客户名，与那份列表**零重合**，按它筛必然空
 *  - 「风险」卡下线（`riskCount` 查的是另一张表，实测恒 0，区分不了「无风险」与「无数据」）
 *
 * 2026-11-02 按 5号位 答复函（r14009 已部署）接入**计划派生交付判定**：
 *  - 交付状态改读后端算好的 `riskLevel`（`DELAYED` 红 / `ON_TRACK` 绿 / **缺键=未排产** 灰）；
 *    `Order.DelayStatus` 是 ERP/ODS 独立维度，冻结 `01 §64` 红线禁止拿它当本版本交付结果
 *  - 「未排」卡重新上线：后端 `unscheduledCount` 已从 `CAST(0 AS INT)` 字面量改为**实值**
 *    （本版本无 Task 行）；旧「未计算」卡（= 总数 − 按期 − 延迟）口径已废，删掉
 *
 * 2026-11-17 按 5号位 复函（**B2**，r14250～r14255 已落码）**交付 KPI 换端点**：
 *  - KPI 从 `GET /api/order-query/summary` 三桶换成 `GET /api/order-query/delivery-kpi`
 *    的**分区行**：总计 / 生产指示 / 销售订单 三张卡
 *  - 判定式与列表 `riskLevel` **逐字同文**（后端类级常量 `RiskCaseExpr`）
 *    ⇒ 卡片与点开的行集逐位一致，不会出现「格子说按期、KPI 说延期」
 *  - 分母**随筛选走**（分母 = 当前筛选命中的订单数，含未判定不剔除）
 *  - 「日期/计划不可判定」**单列显示**，不并进按期或延期任一侧（后端不给它率）
 *  - summary 三桶**不再当 KPI 展示**（两者并排读会自相矛盾）；summary 只留
 *    「整版订单总数」与「整版都还没排产」两个非 KPI 用途
 *  - `targetPercent` 键缺 ⇒ 不出目标达成率；`fgWipDimensionAvailable=false` ⇒ 不出 FG/WIP 卡
 *
 * 2026-11-12 **工厂用户视角改版**（用户：站在工厂用户角度，页面怎么弄比较好用）：
 *
 *  ① 默认计划版本：旧规则取「最新一条」（`GET /api/schedule/versions` 最新优先）
 *     ⇒ dev 最新一条是 `FAILED` 的测试版本，用户一进页面看到的是「延迟 0 / 未排产 100%」的空版本，
 *     像是系统坏了。改由 store 的 `pickDefaultVersion()` 按「已生效(ACTIVE) 优先」挑
 *     （端点没给生效标识，仍是前端兜底 ⇒ 已出函 5号位要 `isDefault`，见函件 R5）。
 *  ② 交期快捷区间：后端本来就有 `dueDateFrom` / `dueDateTo`，页面却只有一个日期区间框，
 *     计划员每天要看的是「本周/近 4 周要交期的、已经逾期的」。现给 5 个一键区间
 *     （工作窗口 / 已逾期 / 近 4 周 / 近 13 周 / 全部交期），默认「工作窗口」。
 *  ③ 工厂 / 产品族：原来是**精确匹配的自由文本**（计划员不认编码，只认「北京工厂」），
 *     现改走主数据下拉（3号位 `GET /api/governance/factories` / `product-families`）：
 *     显示中文名、传编码，仍允许手输编码兜底。
 *  ④ 文本筛选 400ms 防抖：旧实现每敲一个字就打一次后端。
 *  ⑤ 表格瘦身：生产指示号并进订单号下方、延期原因并进「交付状态」列
 *     （原来 14 列，右边「操作」列常被横向滚动裁掉）。
 *  ⑥ KPI 口径标注：2026-11-17 起交付 KPI 走 `delivery-kpi`（**随筛选走**，口径原文回显）；
 *     「该版本共 N 单」仍是整版数量（summary 口径），两者不是同一个数，卡片下方已分别标注。
 *  ⑦ 版本状态大白话提示（失败 / 计算中 / 未发令 / 已被顶替 / 全版本都未排产）。
 *
 * 2026-11-18 **交付 KPI 条工厂视角再改版**（用户：以工厂用户角度优化，不该有的提示去掉）：
 *   - 撤下 `caliber` 原文回显与「口径来源：…（读模型）」角标（内部术语：0号位裁 / MAX(PlannedEndTime)
 *     读模型计算 / 分母=去重根订单含未判定 ⇒ 工厂用户读不懂）；
 *     口径**含义不丢** —— 改由标题「交付预测（按当前筛选）」+ 悬停说明承载（仍是预测，不是实际交付）。
 *   - `plannedCompletionSource` 的**行为保留**（后端切 `formal` 前端不改代码，5号位 B2 要求），
 *     只把文案换成短中文标签「计划完成时间取自排产任务 / 取自正式归集」；未知值原样透出。
 *   - 第三桶「日期/计划不可判定」→「**未判定**」+ 悬停说明（还没排产 / 缺交期 / 缺计划完成时间）。
 *     ⚠️ 不用列表行的「未排产」一词：那一桶还含「有 Task 但缺完成时刻」，说成未排产**不实**。
 *   - 分区卡竖条撤掉绿/红（原 PI 绿 / SO 红，与卡内绿「按期」红「延期」同色 ⇒ 会被读成好/坏），
 *     改中性灰蓝，只有「总计」深一档。
 *   - 排版：标题独占第一行（横线分隔），三张卡**等宽三列**、卡内可折行
 *     （1600 屏实测三卡内容合计 ≈1310px vs 容器内宽 1284px，差十几像素就会把「销售订单」
 *     整张甩到第二行左端 ⇒ 改成等宽列 + 卡内折行，窄屏也不甩卡）；
 *     订单数前的「共」去掉（卡名「总计」已含此义，省宽度）。
 *
 * 2026-11-18（5号位《复函V环境-更新重启归Owner执行…》§四 收讫条件）：
 *   `plannedCompletionSource` 的语义切换（`readmodel_task_max` → `formal`）**用户须可感知** ——
 *   两个已知值的中文标签**必须明显不同**（测算值 vs 正式结果），**禁静默升级**；
 *   未知值仍**原样透出**（不得回落成任一已知标签）。
 *   前端自证：`scripts/selfverify-order-kpi-source-label.mjs`（反向破坏 ⇒ 必须转红）。
 *
 * 写接口：本页查询部分只读；**唯一写入口 = §10A.1「已有订单提前」**
 * （EXISTING_ORDER_ADVANCE → POST /api/governance/run/candidate，Policy aps.plan.run）
 * @see 4号位实施包 v1.4 §十A
 *
 * 2026-11-20 按《APS_V1_P01-P13 全页面业务设计统一收口核对稿 v0.2》§4 **P02** 补默认列：
 *   - 新增 **排程域**（Domain 中文名）与 **订单类型（SO/PI）** —— 两项原 DTO 就有
 *     （`domainKey` / `orderType`），此前没成列；稿 P02 默认列序为
 *     「问题标记｜根订单号｜Domain｜SO/PI｜FG/WIP｜品番/品名｜客户/类别｜数量＋单位｜…」。
 *   - 新增 **要求生产完成日期（入物流仓日期）** 列 —— 后端**尚未投影**该字段
 *     （已出函 5号位 A1）⇒ 显「未提供」，**不用客户交期顶替**（两者是不同口径）。
 *   - **预计延期天数** 从「交付状态」列里**分列**出来（稿：预计延期天数与交付状态分列，
 *     且「不把 ERP 属性 DelayStatus 和 APS 预计延期混作一栏」）。
 *     后端给自然小时差 ⇒ 前端折算成天；口径（自然小时差 / 工作日差）待 5号位确认（致 5号位函 A4）。
 *   - 交付 KPI 区**不动**：三卡按 `delivery-kpi` 分区行（总计 / 生产指示 / 销售订单），
 *     同一分母 + 「未判定」单列，已符合稿「同分母 + 未判定单列」；FG/WIP 维度后端未给
 *     （`fgWipDimensionAvailable=false`）⇒ 该卡不出（已出函 A2）。
 *
 * 2026-11-20：接 5号位 2026-11-20 复函已落码的 **A2** 与 **D1** 两条
 *   （依据 5号位-2026-11-20-复函-P01-P13收口v0.2清单十七项逐条裁决_三条可投影已落码B1F1J1_其余不可原因与后移给足-给4号位抄3号位.md §一）：
 *   · **A2**（r14318）：`fgWipDimensionAvailable` 现为 **true** ⇒ 交付 KPI 区补
 *     **「按物料大分类分区（FG/WIP）」**（每类一张卡）与 **FG/WIP × SO/PI 交叉表**
 *     （判据 = 标准 `Material.MaterialType` 原值，禁由品番前缀推断）。
 *     ⚠️ **定位分区行必须同时看两个键**：FG/WIP 上线后「仅物料大分类」的行 `orderType` 也是 null
 *       ⇒ 只看 `orderType` 会把「组件」的数当成「总计」显示（已修）。
 *   · **D1**：Pegging 承接表的「来源仓库」**不再取 PSA 的 `supplyWarehouseCode`**
 *     （后端实测 99,564/99,564 **全 0 填充** ⇒ 拿它当仓库等于凭空断言），
 *     改走 `composables/useSupplyWarehouse.ts`（按 PO × 物料到供给事实行查仓码/仓名）。
 *     查不到 / 命中多仓 ⇒ 显「未提供」并给出说明，**不猜**。
 *
 * 2026-11-21：「已有订单提前」入口**按场景收窄到 Domain PMC**
 *   （依据《APS_V1_P01-P13 全页面业务设计统一收口核对稿 v0.2》§4.9.1 场景5/6 + §7 UI-20
 *    「车间计划员对 1～6 无权」）：
 *   · 门控由 `canPlanRun`（仅 `aps.plan.run`）改为 `canAdvance`
 *     = `aps.plan.run` **且** `aps.reschedule.local`（局部重排权）；
 *     车间计划员不持 `aps.reschedule.local` ⇒ 进本页也看不到该入口、勾选框同样禁用。
 *   · 其余行为不变：端点仍是 POST /api/governance/run/candidate（Policy `aps.plan.run`）。
 */

import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { storeToRefs } from 'pinia'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { useOrderStore } from '@/store/modules/aps/order'
import { useDomainStore } from '@/store/modules/aps/domain'
import { useApsAuthStore } from '@/store/modules/aps/auth'
import { domainApi, triggerBusinessEntry } from '@/api/aps-v1'
import {
  formatUtcDate,
  formatUtcDateTime,
  formatUtcMonthDay,
  formatUtcMonthDayHM
} from '@/utils/datetime'
import type { BusinessEntrySubmission, OrderListItemDto } from '@/api/aps-v1'
import {
  customerSegmentLabel,
  materialTypeLabel,
  orderTypeLabel,
  supplyTypeLabel
} from '@/api/aps-v1/codeLabels'
import OrderAdvanceDialog from './components/OrderAdvanceDialog.vue'
import OrderTreePanel from './components/OrderTreePanel.vue'
import { useSupplyWarehouse } from './composables/useSupplyWarehouse'
import {
  ORDER_STATUS_LABELS,
  ORDER_DELAY_STATUSES,
  ORDER_DELAY_STATUS_LABELS,
  ORDER_RISK_LEVEL_LABELS,
  ORDER_RISK_MISSING_LABEL,
  ORDER_STATUS_FILTER_OPTIONS,
  PLAN_VERSION_STATUS_LABELS,
  normalizeOrderStatus,
  planVersionStatusTag,
  reasonLabelOf,
  type DomainKey,
  type OrderDelayStatus,
  type OrderRiskLevel,
  type OrderStatusDisplay
} from '@/api/aps-v1/types'

import {
  ElAlert,
  ElButton,
  ElCard,
  ElDatePicker,
  ElDescriptions,
  ElDescriptionsItem,
  ElDrawer,
  ElEmpty,
  ElInput,
  ElOption,
  ElPagination,
  ElSelect,
  ElTable,
  ElTableColumn,
  ElTag,
  ElTooltip
} from 'element-plus'

const orderStore = useOrderStore()
const {
  list,
  listTotal,
  pageIndex,
  pageSize,
  listLoading,
  listError,
  summary,
  deliveryKpi,
  deliveryKpiLoading,
  deliveryKpiError,
  currentDetail,
  detailLoading,
  detailError,
  versions,
  versionsLoading,
  planVersionId
} = storeToRefs(orderStore)

const drawerVisible = ref(false)

// ===== 交付状态配色（计划派生 `riskLevel`；冻结 07 §八.2 三值 + 缺键态）=====
//
// ⚠️ 这里**不再**用 `Order.DelayStatus`（ERP/ODS 独立维度）渲染状态标签：
//   冻结 `01 §64` 红线 —— 不得用 `Order.DelayStatus` 替代当前 PlanVersion 的交付结果；
//   实测 v999333 的 O20630651 就是 `delayStatus=ON_TIME` + 计划超期 15.4 天，
//   两个结论同屏打架。现在交付判定一律读后端算好的 `riskLevel`。
const RISK_COLORS: Record<OrderRiskLevel, string> = {
  ON_TRACK: '#059669',
  AT_RISK: '#d97706',
  DELAYED: '#ef4444'
}
const RISK_TAG_TYPE: Record<OrderRiskLevel, 'success' | 'danger' | 'warning'> = {
  ON_TRACK: 'success',
  AT_RISK: 'warning',
  DELAYED: 'danger'
}
/** 缺键（未排产 / 无交期）的灰 —— 不用红色，避免把「没排」说成「出事」 */
const RISK_MISSING_COLOR = '#94a3b8'

/** 交付状态中文标签：缺键 ⇒「未排产」（**不得**落成「按期」/「未延迟」） */
function riskLabel(raw: string | undefined): string {
  if (!raw) return ORDER_RISK_MISSING_LABEL
  return ORDER_RISK_LEVEL_LABELS[raw as OrderRiskLevel] ?? raw
}
function riskColor(raw: string | undefined): string {
  if (!raw) return RISK_MISSING_COLOR
  return RISK_COLORS[raw as OrderRiskLevel] ?? RISK_MISSING_COLOR
}
function riskTagType(raw: string | undefined): 'success' | 'danger' | 'warning' | 'info' {
  if (!raw) return 'info'
  return RISK_TAG_TYPE[raw as OrderRiskLevel] ?? 'info'
}

/**
 * 主因代码标签 —— 复用冻结 `07 v5.1.13 §八.1` 的 15 码字典，与甘特页 / CTP 页同一口径
 * （不维护第二套自拟码表）。
 *
 * ⚠️ dev 2026-10-19 实测：绝大多数返回值**不在**冻结 15 码内（`BATCH_POLICY_MISSING`、中文散文等），
 * 而冻结 07:4935 明文禁止这两类。故未入字典一律降级成「待核对·<原值>」——
 * 摆出原值是诚实，臆造中文名才是造假（该漂移已出函 5号位，前端不替它兜底成正式码）。
 */
function delayReasonLabel(code: string | undefined): string {
  // 无延期原因 ⇒ '—'（该字段是**可空可选**字段，键不出现即「没有延期原因」）；
  // 有值 ⇒ 走全站唯一口径 `reasonLabelOf`（冻结 15 码 / 未入字典 →「待核对·<原值>」）。
  // 2026-10-31：原为本地查表 `REASON_CODE_LABELS[code as ReasonCode] ?? 待核对·${code}`，
  // 与甘特 / 排程解释 / CTP 页各写各的 ⇒ 收敛到共享函数，避免再次分叉。
  if (!code) return '—'
  return reasonLabelOf(code)
}
/** 延期时长 → 天/小时两态（后端给的是自然小时小数；`!= null` 而非真值判断，0 与「无值」是两态）
 *
 *  口径（5号位 2026-11-02 答复函 §三 Q3 定稿）：**按期不给 `delayHours`**（缺键 = 诚实，
 *  按期/提前无「延期时长」可言）。负值若出现属「提前」，按展示换算显示成「提前 N 天」——
 *  取绝对值会把「提前 20 天」写成「超期 20 天」，方向就反了。
 *  ⚠️ 提前与否的**判定**看 `plannedCompletionTime` 与交期之差，这里只做展示换算，不参与判定。 */
function delayHoursText(hours: number | undefined): string {
  if (hours == null) return ''
  const abs = Math.abs(hours)
  const label = hours < 0 ? '提前' : '超期'
  if (abs < 24) return `${label} ${abs.toFixed(1)} 小时`
  return `${label} ${(abs / 24).toFixed(1)} 天`
}

/**
 * 预计延期天数（列表独立列，稿 §4 P02「预计延期天数与交付状态」分列）。
 *
 * 后端给的是**自然小时差**（`delayHours`，只有延期才有值）⇒ 前端折算成天；
 * ⚠️ 口径（自然小时差 / 工作日差）待 5号位确认（致 5号位函 A4），前端不自行解释。
 * 负值 = 提前（正数提前天数）。
 */
function delayDaysText(hours: number | null | undefined): string {
  if (hours == null) return '—'
  const days = hours / 24
  const label = days > 0 ? '延期' : days < 0 ? '提前' : ''
  return `${label} ${Math.abs(days).toFixed(1)} 天`
}

/** 订单状态标签：按冻结 07:3611 六值词表渲染（实库 `Open` 归一回历史遗留 `OPEN`）；仍不认识则原样显示 */
function statusLabel(raw: string): string {
  const norm = normalizeOrderStatus(raw)
  return norm ? ORDER_STATUS_LABELS[norm] : raw
}
const ORDER_STATUS_TAG: Record<OrderStatusDisplay, 'success' | 'info' | 'warning' | 'primary'> = {
  NEW: 'info',
  PLANNED: 'primary',
  CONFIRMED: 'success',
  IN_PROGRESS: 'success',
  COMPLETED: 'info',
  CANCELLED: 'warning',
  OPEN: 'success'
}
function statusTagType(raw: string): 'success' | 'info' | 'warning' | 'primary' {
  const norm = normalizeOrderStatus(raw)
  return norm ? ORDER_STATUS_TAG[norm] : 'info'
}

// ===== Pegging 供应类型标签（冻结 07 的 9 类 SupplyType；未知值原样显示）=====
// 2026-11-18：搬进 codeLabels.ts 共用一份（订单树的数量账也读同一张表，禁各页各造）。
const supplyLabel = (raw: string | undefined): string => supplyTypeLabel(raw, '—')

/** 任务状态：冻结 TASK_STATUSES 5 态（common.ts），未知值原样显示 */
const TASK_STATUS_LABELS: Record<string, string> = {
  PLANNED: '已排产',
  RELEASED: '已释放',
  IN_PROGRESS: '执行中',
  COMPLETED: '已完成',
  CANCELLED: '已取消'
}

const apsAuth = useApsAuthStore()
const domainStore = useDomainStore()

/**
 * Domain 选项：从真实 domainStore 派生（非 mock 时即后端 Domain 列表），U41 按 dataScope 收敛
 *
 * ⚠️ **域字典漂移（5号位 2026-11-18 复函 §五）**：BJ_FAMILY_FRL / TJ_FAMILY_FRL 在域字典里
 *   是**停用态**却各带 ACTIVE 版本在产 ⇒ 若只列「启用域」，会把**有订单的域**从筛选下拉里抹掉。
 *   停用语义未定义（0号位 待裁）⇒ 前端不自行解释停用、也不隐藏 ⇒ 列**全部已知域（含停用）**
 *   并按业务范围收敛；显示名走 domainStore.label（名称表含已停用域，不会退成裸 key）。
 */
const DOMAIN_OPTIONS = computed<Array<{ value: DomainKey | 'ALL'; label: string }>>(() => {
  const list: Array<{ value: DomainKey; label: string }> = []
  for (const k of domainStore.scopedKnownDomainKeys) {
    list.push({ value: k as DomainKey, label: domainStore.label(k as DomainKey) })
  }
  return [{ value: 'ALL' as const, label: '全部' }, ...list]
})

// ===== 交期快捷区间（后端既有参数 dueDateFrom / dueDateTo，页面此前没给入口）=====
//
// 计划员每天要回答的问题是「这周/近 4 周有哪些订单要交、哪些已经逾期」，
// 而不是「按 2016 年的交期顺序翻 200 行」。故给 5 个一键区间。
//
// ⚠️ 日期基准用 **UTC 今天**：后端 `customerDueDate` 是 UTC 无时区字符串，
//    而「剩余 N 天」的显示（fmtDue）也是拿 UTC 零点与当前时刻比 —— 两处必须同基准，
//    否则芯片选中的区间和格子里的天数会对不上。
type RangeKey = 'work' | 'overdue' | 'w4' | 'w13' | 'all' | 'custom'

const RANGE_PRESETS: Array<{ key: RangeKey; label: string; desc: string }> = [
  {
    key: 'work',
    label: '近期订单',
    desc: '这批最该动手：近 28 天内要交货的，加上近 30 天内已经拖期的'
  },
  { key: 'overdue', label: '已逾期', desc: '交货日期已经过了今天的订单' },
  { key: 'w4', label: '4 周内到期', desc: '从今天起 4 周内要交货的订单' },
  { key: 'w13', label: '13 周内到期', desc: '从今天起 13 周内要交货的订单' },
  { key: 'all', label: '全部', desc: '不按交货日期挑单（含很早以前的历史积压单）' }
]

/** 相对今天偏移 n 天的 UTC 日期（YYYY-MM-DD） */
function dayOffset(offset: number): string {
  const d = new Date()
  d.setUTCDate(d.getUTCDate() + offset)
  return d.toISOString().slice(0, 10)
}

/** 区间 → 后端真实参数（dueDateFrom / dueDateTo） */
function rangeBounds(key: RangeKey): { from?: string; to?: string } {
  switch (key) {
    case 'work':
      return { from: dayOffset(-30), to: dayOffset(28) }
    case 'overdue':
      return { to: dayOffset(-1) }
    case 'w4':
      return { from: dayOffset(0), to: dayOffset(28) }
    case 'w13':
      return { from: dayOffset(0), to: dayOffset(91) }
    case 'all':
    case 'custom':
    default:
      return {}
  }
}

const activeRange = ref<RangeKey>('work')
const dueFrom = ref<string | undefined>()
const dueTo = ref<string | undefined>()
const rangeDesc = computed(
  () => RANGE_PRESETS.find((r) => r.key === activeRange.value)?.desc ?? '按下面自己填的日期过滤'
)

/** 当前生效的交货日期区间（写成真实日期，比写「4 周内」更好懂） */
const rangeDateText = computed(() => {
  if (activeRange.value === 'all') return '交货日期不限'
  const from = dueFrom.value
  const to = dueTo.value
  if (from && to) return `交货日期 ${from} ~ ${to}`
  if (to) return `交货日期早于 ${to}`
  if (from) return `交货日期 ${from} 之后`
  return '交货日期不限'
})

/** 落区间到后端参数（不重查 —— 供「先设好再统一刷一次」的场景用） */
function setRangeFilter(key: RangeKey): { from?: string; to?: string } {
  activeRange.value = key
  const { from, to } = rangeBounds(key)
  dueFrom.value = from
  dueTo.value = to
  orderStore.setFilter({ dueDateFrom: from, dueDateTo: to })
  return { from, to }
}

/** 落区间 + 重查列表与交付 KPI（KPI 分母随筛选走 ⇒ 两块一起刷） */
async function applyRange(key: RangeKey): Promise<void> {
  const { from, to } = setRangeFilter(key)
  await orderStore.applyFilter({ dueDateFrom: from, dueDateTo: to })
}

// ===== 筛选输入（全部对应后端真实 Query 参数）=====
const orderNoInput = ref('')
const piInput = ref('')
const materialInput = ref('')
/** 客户：无 distinct 端点，且后端是 `CustomerName LIKE %x%` ⇒ 自由文本，不造下拉选项 */
const customerNameInput = ref('')
/** 工厂 / 产品族：后端是**精确匹配**（`f.Code =` / `pf.Code =`）⇒ 走主数据下拉（显示中文名、传编码） */
const factoryCodeInput = ref<string | undefined>()
const productFamilyInput = ref<string | undefined>()
const delayStatusInput = ref<OrderDelayStatus | undefined>()
const statusInput = ref<OrderStatusDisplay | undefined>()

// ===== 主数据下拉（工厂 / 产品族）=====
//
// 来源：3号位 `GET /api/governance/factories` / `GET /api/governance/product-families`
//  （Domain 维护页同一份主数据，非本页自造 ⇒ 与「域定义」里选的工厂/产品族永远对得上）
const factoryOptions = ref<Array<{ code: string; name: string }>>([])
const familyOptions = ref<Array<{ code: string; name: string }>>([])
const dictError = ref<string>('')

/**
 * 工厂下拉按**业务范围**收窄。
 *
 * 后端 `OrderQueryController.QueryOrders` 对入参做范围校验：
 * `factoryCode` 不在授权范围内 ⇒ 直接 403「Factory 范围越界」。
 * ⇒ 下拉若把范围外的工厂摆出来，用户点一下就撞 403（自己找的坑）。
 * 全局范围（isGlobal）⇒ 全部可选；受限范围 ⇒ 只列授权内的。
 */
const visibleFactories = computed(() => {
  const allowed = apsAuth.isGlobalScope ? [] : (apsAuth.dataScope.factoryCodes ?? [])
  if (allowed.length === 0) return factoryOptions.value
  return factoryOptions.value.filter((f) => allowed.includes(f.code))
})

async function loadMasterData(): Promise<void> {
  try {
    const [factories, families] = await Promise.all([
      domainApi.listFactories(),
      domainApi.listProductFamilies()
    ])
    factoryOptions.value = factories.map((f) => ({ code: f.code, name: f.name }))
    familyOptions.value = families.map((f) => ({ code: f.code, name: f.name }))
    dictError.value = ''
  } catch (err) {
    factoryOptions.value = []
    familyOptions.value = []
    dictError.value = (err as Error)?.message ?? '加载工厂/产品族字典失败'
  }
}

/** 编码 → 中文名（字典里没有就原样显示编码，绝不臆造名字） */
function factoryLabel(code?: string | null): string {
  const raw = (code ?? '').trim()
  if (!raw) return '—'
  return factoryOptions.value.find((f) => f.code === raw)?.name ?? raw
}
function familyLabel(code?: string | null): string {
  const raw = (code ?? '').trim()
  if (!raw) return '—'
  return familyOptions.value.find((f) => f.code === raw)?.name ?? raw
}

// ===== 文本筛选：400ms 防抖 =====
//
// 旧实现 watch 六个输入框，每敲一个字就 `loadList` 一次（每个键一次后端请求）。
// 现在：文本框 400ms 防抖；下拉/日期即刻生效（选择本身就是一次明确动作）。
const TEXT_FILTER_DEBOUNCE_MS = 400
let textTimer: ReturnType<typeof setTimeout> | null = null
/** 重置时抑制一次 watch（否则重置后 400ms 又补打一次请求） */
const suppressTextWatch = ref(false)

watch([orderNoInput, piInput, materialInput, customerNameInput], () => {
  if (suppressTextWatch.value) {
    suppressTextWatch.value = false
    return
  }
  if (textTimer) clearTimeout(textTimer)
  textTimer = setTimeout(() => {
    void orderStore.applyFilter({
      orderNo: orderNoInput.value || undefined,
      productionInstructionNo: piInput.value || undefined,
      materialCode: materialInput.value || undefined,
      customerName: customerNameInput.value || undefined
    })
  }, TEXT_FILTER_DEBOUNCE_MS)
})

onUnmounted(() => {
  if (textTimer) clearTimeout(textTimer)
})

function onDueFromChange(v: string | null | undefined): void {
  dueFrom.value = v || undefined
  activeRange.value = 'custom'
  void orderStore.applyFilter({ dueDateFrom: dueFrom.value })
}

function onDueToChange(v: string | null | undefined): void {
  dueTo.value = v || undefined
  activeRange.value = 'custom'
  void orderStore.applyFilter({ dueDateTo: dueTo.value })
}

function onDelayStatusChange(v: OrderDelayStatus | undefined): void {
  delayStatusInput.value = v
  void orderStore.applyFilter({ delayStatus: v })
}

function onStatusChange(v: OrderStatusDisplay | undefined): void {
  statusInput.value = v
  void orderStore.applyFilter({ status: v })
}

function onFactoryChange(v: string | undefined): void {
  factoryCodeInput.value = v
  void orderStore.applyFilter({ factoryCode: v })
}

function onFamilyChange(v: string | undefined): void {
  productFamilyInput.value = v
  void orderStore.applyFilter({ productFamilyCode: v })
}

async function onVersionChange(v: number): Promise<void> {
  // store 切版本会清空全部筛选并重查（既有语义）；交期区间跟着版本走 ⇒ 再落一次区间并重查。
  await orderStore.setPlanVersion(v)
  await applyRange(activeRange.value)
}

async function onResetSearch(): Promise<void> {
  suppressTextWatch.value = true
  orderNoInput.value = ''
  piInput.value = ''
  materialInput.value = ''
  customerNameInput.value = ''
  factoryCodeInput.value = undefined
  productFamilyInput.value = undefined
  delayStatusInput.value = undefined
  statusInput.value = undefined
  orderStore.clearFilter()
  await applyRange('work')
  // watch 若这次没被触发（输入框本来就是空的），标记要在这里回收，否则下一次真输入会被吞掉
  suppressTextWatch.value = false
}

// ===== 版本下拉标签 =====
function versionLabel(id: number): string {
  const v = versions.value.find((x) => x.id === id)
  return v ? `${v.versionCode}（${PLAN_VERSION_STATUS_LABELS[v.status] ?? v.status}）` : String(id)
}

/** 顶部版本摘要（真实版本的时间窗 / 状态），别让用户对着一串裸 id 猜 */
const currentVersionLabel = computed(() => {
  const v = orderStore.currentVersion
  if (!v) return ''
  const s =
    v.planHorizonStart && v.planHorizonEnd
      ? ` ｜ 时间窗 ${formatUtcMonthDay(v.planHorizonStart)} ~ ${formatUtcMonthDay(v.planHorizonEnd)}`
      : ''
  return `${domainStore.label(v.domainKey)} ｜ 版本 #${v.id}${s}`
})

/**
 * 版本状态的大白话提示（2026-11-12）
 *
 * 工厂用户看到一版「全是未排产」的订单时，第一反应是「系统坏了」。
 * 其实多数情况是**这一版还没发令 / 算失败 / 已被顶替**——把状态翻译成人话即可。
 */
const versionNotice = computed<{ type: 'error' | 'warning' | 'info'; text: string } | null>(() => {
  const v = orderStore.currentVersion
  if (!v) return null
  switch (v.status) {
    case 'FAILED':
      return {
        type: 'error',
        text: '这一版排产失败，没有排产结果。订单本身还在，但看不到排产任务；换一版看。'
      }
    case 'Computing':
      return { type: 'warning', text: '这一版还在计算中，排产结果可能不完整，过一会儿再看。' }
    case 'BUILDING':
      return {
        type: 'warning',
        text: '这一版还没发令，订单暂时没有排产任务，所以都显示成「未排产」。'
      }
    case 'ARCHIVED':
      return { type: 'info', text: '这一版已经被更新的版本顶替，看到的是当时的排产结果。' }
    default:
      return null
  }
})

/** 全版本都未排产时的补充说明（ACTIVE 版也可能没排产任务） */
const allUnscheduledNotice = computed<string | null>(() => {
  const s = summary.value
  if (!s || !s.totalCount) return null
  if (s.unscheduledCount === s.totalCount) {
    return '这一版的订单都还没有排产任务（未排产），所以看不到「APS 预计完成时间」。'
  }
  return null
})

// ===== 交付 KPI（取 /api/order-query/delivery-kpi，5号位 2026-11-17 B2）=====
//
// ⚠️ 口径换源（5号位 11-17 复函 §五）：
//   交付 KPI 唯一口径 = 本端点（判定式与列表 `riskLevel` 逐字同文，且**随筛选走**）；
//   旧 summary 三桶（按期/延迟/未排产）**不再当 KPI 展示** —— 两者并排读会自相矛盾。
//   summary 只留两个非 KPI 用途：整版订单总数、整版「都还没排产」提示。
//
// 三张卡 = 总计 / 生产指示 / 销售订单（后端 GROUPING SETS 单扫出来的分区行）。
// ⚠️ 「日期/计划不可判定」**单列显示**，不并进按期或延期任一侧（未判定不占率）。
// ⚠️ 目标达成率：`targetPercent` 键缺 ⇒ **不渲染**（不显 0、不显「未提供」）。
// ⚠️ FG/WIP 分区：`fgWipDimensionAvailable=false` ⇒ **不出该卡**（不是出空卡）。
const totalCount = computed(() => summary.value?.totalCount ?? null)

/** 分区行 → 卡片（总计行 `orderType` 缺键 ⇒ 用 `== null` 认，勿用 `=== ''`）
 *
 * ⚠️ 2026-11-20（A2：FG/WIP 分区上线后）**定位必须同时看两个键**：
 *   后端现在有四组行（总计 / 仅 SO-PI / 仅物料大分类 / 交叉），
 *   「仅物料大分类」那组的 `orderType` **也是 null** ⇒ 只判 `orderType == null`
 *   会把「组件」那行误当总计 ⇒ 总计行 = 两个键都缺；SO-PI 行 = 有 orderType 且无 materialType。
 */
const kpiCards = computed(() => {
  const rows = deliveryKpi.value?.rows ?? []
  const total = rows.find((r) => r.orderType == null && r.materialType == null)
  const pi = rows.find((r) => r.orderType === 'PRODUCTION_INSTRUCTION' && r.materialType == null)
  const so = rows.find((r) => r.orderType === 'SALES_ORDER' && r.materialType == null)
  /**
   * 竖条颜色 2026-11-18 改中性：原 PI 绿 / SO 红，与卡内绿「按期」红「延期」同色，
   * 工厂用户会把竖条读成「这单类型好/坏」⇒ 只留深浅（总计深一档）区分，不带好坏语义。
   */
  const defs = [
    { key: 'total', label: '总计', color: '#64748b', row: total },
    { key: 'pi', label: '生产指示', color: '#cbd5e1', row: pi },
    { key: 'so', label: '销售订单', color: '#cbd5e1', row: so }
  ]
  return defs.filter((d) => d.row).map((d) => ({ ...d, row: d.row! }))
})

/** 率：后端给的是 %（分母为 0 ⇒ null）。**null 不显 0%**，显「—」。 */
function kpiRateText(v: number | null | undefined): string {
  if (v == null) return '—'
  return `${v}%`
}

/* ===== FG/WIP 分区（2026-11-20 补；A2：5号位 r14318 已落码）=====
 *
 * ✅ v0.2 P02：「分组层级 `DomainKey → FG/WIP → SALES_ORDER/PRODUCTION_INSTRUCTION`」、
 *   「**FG/WIP与SO/PI正交分组**」、「不同Domain、FG/WIP 的业务数量和比率**禁止混算**」。
 * ✅ 判据＝标准 `Material.MaterialType` 原值（0号位 11-20 裁）⇒ **禁**由品番前缀推断。
 * ✅ 全部标准类型**逐行独立成区、不合并不隐藏**（ASSY/RAW_MATERIAL 与 FG/WIP 平级）。
 * ⚠️ 行定位：`orderType == null && materialType != null` ⇒ **类型维度行**；
 *   `orderType != null && materialType != null` ⇒ **交叉行**（FG/WIP × SO/PI）。
 *   （两个键都缺 ⇒ 总计行；只有 orderType ⇒ 仅 SO-PI 行。）
 */
const fgWipAvailable = computed(() => deliveryKpi.value?.fgWipDimensionAvailable === true)

/** FG/WIP 分区卡（类型维度行） */
const fgWipCards = computed(() => {
  if (!fgWipAvailable.value) return []
  const rows = deliveryKpi.value?.rows ?? []
  return rows
    .filter((r) => r.orderType == null && r.materialType != null)
    .map((r) => ({
      key: r.materialType!,
      label: materialTypeLabel(r.materialType),
      row: r
    }))
    .sort((a, b) => b.row.totalOrders - a.row.totalOrders)
})

/** FG/WIP × SO/PI 交叉行（正交分组的叶子层） */
const fgWipCrossRows = computed(() => {
  if (!fgWipAvailable.value) return []
  const rows = deliveryKpi.value?.rows ?? []
  return rows
    .filter((r) => r.orderType != null && r.materialType != null)
    .map((r) => ({
      materialType: r.materialType!,
      materialLabel: materialTypeLabel(r.materialType),
      orderTypeLabel: orderTypeLabel(r.orderType, '未提供'),
      row: r
    }))
    .sort((a, b) => b.row.totalOrders - a.row.totalOrders)
})

/**
 * 计划完成时间的取数来源角标（后端 `plannedCompletionSource`）。
 *
 * 2026-11-18 用户口径：内部术语不上页面（原文「口径来源：按本版任务的最晚完成时间算（读模型）」），
 * 但 5号位 B2 要求的**行为**（后端切 `formal` 时前端不改代码）要保留 ⇒ 换成一句短中文。
 * ⚠️ 未知值**原样透出**（不臆造中文），与其它词典口径一致。
 *
 * 2026-11-18（5号位 复函V §四 收讫条件）：`readmodel_task_max → formal` 的语义切换
 *   **用户须可感知** ⇒ 两个已知值的中文标签**必须明显不同**（测算值 vs 正式结果），
 *   **禁静默升级**；该条件已进后端护栏语义表第 10 行，我方两头夹
 *   （前端自证：`scripts/selfverify-order-kpi-source-label.mjs`）。
 */
const kpiSourceText = computed(() => {
  const src = deliveryKpi.value?.plannedCompletionSource
  if (!src) return ''
  // ⚠️ 两个已知来源的标签**必须明显不同**（5号位 2026-11-18 复函V §四 条件）：
  //   值一变（读模型测算 → 正式归集结果）标签就换，**禁静默升级**（用户须可感知口径切换）。
  if (src === 'readmodel_task_max') return '计划完成时间取自排产任务（测算值）'
  if (src === 'formal') return '计划完成时间取自正式归集（正式结果）'
  // 未知值：原样透出后端码，**不得**回落成上面任一标签（否则等于假装口径没变）
  return `计划完成时间取自：${src}`
})

/** KPI 区的空态说明（版本不存在 / 越权 / 当前筛选无订单） */
const kpiEmptyText = computed<string | null>(() => {
  const k = deliveryKpi.value
  if (!k) return null
  if (k.versionExists === false) return '这一版不存在或已被清理，没有交付指标'
  if (k.scopeDenied) return '没有这一版的查看权限，看不到交付指标'
  if (!kpiCards.value.length) return '当前筛选下没有订单，没有交付指标'
  return null
})

// ===== 页码 =====
function onPageChange(p: number): void {
  orderStore.setPage({ pageIndex: p })
  void orderStore.loadList()
}
function onSizeChange(s: number): void {
  orderStore.setPage({ pageIndex: 1, pageSize: s })
  void orderStore.loadList()
}

// ===== 行点击：打开抽屉 =====
/**
 * 抽屉里的「交付状态 / 延期时长 / APS 预计完成」取**打开抽屉的那一行列表数据**。
 *
 * ⚠️ 原因（dev 实测 2026-11-02）：详情端点 `GET /api/order-query/{id}` 的 `order` 是**另一条 SQL**，
 *   **不返计划派生字段**（`riskLevel` / `delayHours` / `plannedCompletionTime` 三个键全缺）⇒
 *   若拿详情响应判空，一条「已超期 15.4 天」的订单会被显示成「未排产」。
 *   列表行是同一 PlanVersion 下的同一条数据（只有列表端点带富化字段），用它才是真的。
 */
const detailRow = ref<OrderListItemDto | null>(null)

async function openDetail(row: OrderListItemDto): Promise<void> {
  drawerVisible.value = true
  detailRow.value = row
  await orderStore.loadDetail(row.id)
}
function closeDetail(): void {
  drawerVisible.value = false
}
function onRowClick(row: OrderListItemDto, column?: { type?: string }): void {
  if (column?.type === 'selection') return
  void openDetail(row)
}

// ===== §10A.1 已有订单提前（EXISTING_ORDER_ADVANCE）=====
const router = useRouter()
const route = useRoute()
/**
 * 场景5/6（已有订单提前 = LOCAL_RESCHEDULE + MANUAL_ADJUSTMENT）= **Domain PMC**
 *  - `aps.plan.run`   ：端点 POST /api/governance/run/candidate 的权限码
 *  - `aps.reschedule.local`：局部重排权（LOCAL_RESCHEDULE）⇒ 车间计划员不持此码，
 *    故其进本页也看不到「已有订单提前」入口（v0.2 稿 §4.9.1 场景5/6 + §7 UI-20：
 *    「车间计划员对 1～6 无权」；车间侧的对应能力是场景7～9，在甘特页）。
 */
const canPlanRun = computed(() => apsAuth.has('aps.plan.run'))
const canAdvance = computed(() => canPlanRun.value && apsAuth.has('aps.reschedule.local'))

/**
 * 当前版本是否只读（不可写）
 *
 * §0.2 ⑤「历史 PV 一律只读」：已被顶替 / 排产失败 / 还没发令 / 计算中 的版本，
 * 写入口（「已有订单提前」）一律关掉并说清原因 —— 否则用户会在这类版本上
 * 对着一份不会变的排产结果做推进动作，写进去也没有意义。
 */
const versionReadOnlyReason = computed<string | null>(() => {
  const s = orderStore.currentVersion?.status
  switch (s) {
    case 'ARCHIVED':
      return '这一版已被新版本顶替，是历史版本，只读，不能推进订单，换一版看'
    case 'FAILED':
      return '这一版排产失败，只读，不能推进订单，换一版看'
    case 'BUILDING':
      return '这一版还没发令，只读，不能推进订单，换一版看'
    case 'Computing':
      return '这一版还在计算中，只读，不能推进订单，换一版看'
    default:
      return null
  }
})

const advanceVisible = ref(false)
const advanceSubmitting = ref(false)
const selectedOrders = ref<OrderListItemDto[]>([])
const tableRef = ref<InstanceType<typeof ElTable> | null>(null)

/** Dialog 候选行（仅取所需字段；orderCanonicalId 自动带入由 Dialog watch 处理）
 *  domainKey 一并带出：弹窗按排程域收敛候选集（一次只处理一个域） */
const advanceCandidates = computed(() =>
  list.value.map((o) => ({
    orderId: o.id,
    orderNo: o.orderNo,
    orderCanonicalId: o.orderCanonicalId,
    domainKey: o.domainKey,
    materialCode: o.materialCode,
    customerName: o.customerName,
    customerDueDate: o.customerDueDate
  }))
)

const advanceDomainOptions = computed<DomainKey[]>(() =>
  DOMAIN_OPTIONS.value.filter((o) => o.value !== 'ALL').map((o) => o.value as DomainKey)
)

/** 候选推进弹窗的默认域：取**选中订单自己的域**（一版一域 ⇒ 行内 domainKey 即该版所属域）；
 *  没勾选时退回第一个可选域。此前默认域来自「排程域」筛选（该筛选已撤），语义不变。 */
const advanceDefaultDomain = computed<DomainKey | undefined>(() => {
  const picked = selectedOrders.value.find((o) => o.domainKey)
  if (picked?.domainKey) return picked.domainKey
  return advanceDomainOptions.value[0]
})

function onOrderSelectionChange(rows: OrderListItemDto[]): void {
  selectedOrders.value = rows
}

function openAdvanceDialog(): void {
  if (selectedOrders.value.length === 0) {
    ElMessage.warning('请先在列表中勾选要提前的订单')
    return
  }
  advanceVisible.value = true
}

async function submitOrderAdvance(payload: BusinessEntrySubmission): Promise<void> {
  advanceSubmitting.value = true
  try {
    const result = await triggerBusinessEntry({
      trigger: payload.trigger,
      domainKey: payload.domainKey,
      draft: payload.draft,
      actor: apsAuth.userInfo?.userCode ?? 'unknown'
    })
    ElMessage.success(
      `已发起：排程运行 #${result.newRunId}，候选版本 #${result.candidatePlanVersionId}。跳转对比页…`
    )
    advanceVisible.value = false
    selectedOrders.value = []
    tableRef.value?.clearSelection()
    router.push({
      name: 'ApsCandidate',
      query: {
        candidatePlanVersionId: String(result.candidatePlanVersionId),
        basePlanVersionId: String(result.basePlanVersionId),
        source: 'order-advance'
      }
    })
  } catch (err) {
    ElMessage.error(`发起订单提前失败：${(err as Error)?.message ?? '未知错误'}`)
  } finally {
    advanceSubmitting.value = false
  }
}

/** 交期显示（后端 DateTime 无时区后缀 → 按 UTC 解析，见 utils/datetime） */
function fmtDue(due: string): string {
  const dueMs = new Date(`${due}Z`).getTime()
  if (Number.isNaN(dueMs)) return due
  const days = Math.round((dueMs - Date.now()) / 86400000)
  // 年月日（原来只显示月日 ⇒ 跨年订单看不出是哪一年，2024-03-14 和 2026-03-14 长得一样）
  const text = formatUtcDate(due)
  return days >= 0 ? `${text}（剩 ${days} 天）` : `${text}（逾期 ${-days} 天）`
}
const isOverdue = (due: string): boolean => new Date(`${due}Z`).getTime() < Date.now()

/**
 * 表头计数：三个口径分列，别混成一个数（§0.2 ③）
 *  - 服务端命中数＝当前筛选下后端返回的 total
 *  - 已加载数＝本页实际渲染的条数
 *  - 该版本总单数＝整版订单总数（另一套口径）
 */
const listCountLabel = computed(() => {
  const total = totalCount.value
  const filtered = listTotal.value
  const filteredText = filtered === null ? '未提供' : String(filtered)
  const base = `服务端命中 ${filteredText} 条 · 已显示 ${list.value.length} 条`
  return total === null ? base : `${base} · 该版本共 ${total} 单`
})

/** 空态文案：区分「这版没订单」「筛选太窄」「没取到」三种 */
const emptyText = computed(() => {
  if (listTotal.value === null) return '订单列表没取到，先看上面的错误提示'
  if (totalCount.value === 0) return '这一版没有订单'
  return '当前筛选条件下没有订单，换个交期区间或放宽条件再看看'
})

const detailOrder = computed(() => currentDetail.value?.order ?? null)
const detailPegging = computed(() => currentDetail.value?.pegging ?? [])
const detailTasks = computed(() => currentDetail.value?.tasks ?? [])

/**
 * 供给侧仓库解析（2026-11-20；D1）。
 *
 * ⚠️ PSA 的 `supplyWarehouseCode` 全 0 填充（5号位 11-20 复函 D1 实测 99,564/99,564 为空）
 *   ⇒ 承接表的「来源仓库」列**不再显示那一列**，改由供给事实行解析；查不到显「未提供」。
 */
const { resolve: resolveWarehouses, warehouseText, warehouseHint } = useSupplyWarehouse()
watch(
  detailPegging,
  (rows) => {
    if (!rows.length) return
    void resolveWarehouses(
      rows.map((r) => ({ poNo: r.supplyDocumentNo, materialCode: r.materialCode }))
    )
  },
  { immediate: true }
)

// ===== P02 行展开 / P03 深链（同一端点、同一组件）=====
/**
 * 订单结构（P02 行展开）＋ 深链定位（P03）
 *
 * 5号位 2026-11-17 复函B3/B4：`GET /api/order-query/{orderId}/tree` 一个端点两处用，
 * **不建第二套**（同一树、同一数据、同一正式 PV）⇒ 行展开与深链共用 `OrderTreePanel`。
 *
 * 深链参数留在地址栏（计划版本＋订单＋节点键），刷新/分享都能回到同一个位置；
 * 只改 query 不改路由记录 ⇒ 组件不重建、正在跑的逻辑不被打断。
 */
const treeDrawerVisible = ref(false)
const treeOrderId = ref<number | null>(null)
const treePlanVersionId = ref<number | null>(null)
const treeFocusTaskId = ref<number | null>(null)
const treeFocusMaterialId = ref<number | null>(null)

/** 地址栏参数 → 数字（非法/缺省 ⇒ null，不当 0） */
function numParam(v: unknown): number | null {
  const raw = Array.isArray(v) ? v[0] : v
  const n = Number(raw)
  return Number.isFinite(n) && n > 0 ? n : null
}

/** 读地址栏上的深链参数 → 打开订单结构面板 */
function applyDeepLink(): void {
  const orderId = numParam(route.query.orderId)
  if (!orderId) return
  treeOrderId.value = orderId
  treePlanVersionId.value = numParam(route.query.planVersionId) ?? planVersionId.value
  treeFocusTaskId.value = numParam(route.query.taskId)
  treeFocusMaterialId.value = numParam(route.query.materialId)
  treeDrawerVisible.value = true
}

/** 节点表点「深链」⇒ 参数写进地址栏（不跳页、不重建组件） */
function onTreeDeepLink(payload: { orderId: number; taskId: number; materialId: number }): void {
  void router.replace({
    path: route.path,
    query: {
      ...route.query,
      planVersionId: String(treePlanVersionId.value ?? planVersionId.value ?? ''),
      orderId: String(payload.orderId),
      taskId: String(payload.taskId),
      materialId: String(payload.materialId)
    }
  })
}

// 地址栏参数变化（含本页自己写入的深链）⇒ 落到面板上
watch(
  () => [route.query.orderId, route.query.taskId, route.query.materialId],
  () => applyDeepLink()
)

// ===== 加载入口 =====
onMounted(async () => {
  // 排程域字典：**「已有订单提前」候选弹窗**要用域下拉，而全项目只有异常解释页加载过
  // ⇒ 直接进本页时下拉是空的。这里补一次加载（store 内有并发守卫）。
  if (!domainStore.loadedAt) void domainStore.load()
  void loadMasterData()
  await orderStore.loadVersions()
  // P03 深链：地址栏带了计划版本 ⇒ 先切到那一版（订单树按 PV 隔离，换版必须重取）
  const pvFromUrl = numParam(route.query.planVersionId)
  if (pvFromUrl && pvFromUrl !== planVersionId.value) {
    await orderStore.setPlanVersion(pvFromUrl)
    await applyRange(activeRange.value)
  } else {
    // 默认交期区间随版本一起落上（否则第一屏是 2016 年的历史订单）；
    // 区间先落进 store，再统一刷列表 + KPI，避免连打两次列表。
    setRangeFilter(activeRange.value)
    await orderStore.refresh(true)
  }
  applyDeepLink()
})
</script>

<template>
  <div class="aps-order">
    <!-- ===== 版本选择 + 写入口 ===== -->
    <div class="toolbar">
      <ElSelect
        :model-value="planVersionId ?? undefined"
        placeholder="选择计划版本"
        class="version-select"
        filterable
        :loading="versionsLoading"
        @change="onVersionChange"
      >
        <ElOption v-for="v in versions" :key="v.id" :label="versionLabel(v.id)" :value="v.id">
          <div class="version-option">
            <span class="version-code">{{ v.versionCode }}</span>
            <ElTag size="small" :type="planVersionStatusTag(v.status)">
              {{ PLAN_VERSION_STATUS_LABELS[v.status] ?? v.status }}
            </ElTag>
            <span class="muted version-meta">
              {{ domainStore.label(v.domainKey) }} · {{ formatUtcMonthDay(v.planHorizonStart) }} ~
              {{ formatUtcMonthDay(v.planHorizonEnd) }}
            </span>
          </div>
        </ElOption>
      </ElSelect>
      <div class="toolbar-meta">
        <span v-if="currentVersionLabel" class="muted">{{ currentVersionLabel }}</span>
      </div>
      <!-- §10A.1 已有订单提前：5 个独立中文业务入口中的本页唯一入口
           2026-11-21：门控由 canPlanRun 收紧为 canAdvance（再加 aps.reschedule.local）
           ⇒ 场景5/6 只归 Domain PMC，车间计划员在本页看不到该入口（v0.2 稿 §7 UI-20） -->
      <ElButton
        type="warning"
        plain
        :disabled="!canAdvance || !!versionReadOnlyReason || selectedOrders.length === 0"
        :title="
          versionReadOnlyReason
            ? versionReadOnlyReason
            : canAdvance
              ? '已有订单提前 — 先在列表勾选订单'
              : '当前账号无排程执行权限'
        "
        @click="openAdvanceDialog"
      >
        <Icon icon="vi-mdi:calendar-arrow-right" />
        已有订单提前（{{ selectedOrders.length }}）
      </ElButton>
    </div>

    <!-- ===== 错误条 ===== -->
    <ElAlert
      v-if="listError"
      type="error"
      :closable="false"
      show-icon
      :title="`加载失败：${listError}`"
    />

    <!-- ===== 版本状态大白话提示（失败 / 计算中 / 未发令 / 已被顶替）===== -->
    <ElAlert
      v-if="versionNotice"
      :type="versionNotice.type"
      :closable="false"
      show-icon
      :title="versionNotice.text"
    />
    <ElAlert
      v-if="allUnscheduledNotice"
      type="info"
      :closable="false"
      show-icon
      :title="allUnscheduledNotice"
    />

    <!-- ===== 交付 KPI（取 /api/order-query/delivery-kpi；5号位 2026-11-17 B2）===== -->
    <!-- 2026-11-12 用户反馈：原先四张大卡片占掉大半屏首屏 ⇒ 保持一行紧凑指标条。
         2026-11-17：四卡（总数/按期/延迟/未排产）换成三张**分区卡**（总计/生产指示/销售订单），
         每个分区里「不可判定」单列，率由后端给，前端不自算。
         2026-11-18 用户反馈（工厂视角）：撤下内部术语（`caliber` 原文 + 「口径来源…（读模型）」），
         口径含义改由标题 + 悬停承载；第三桶文案改「未判定」；竖条改中性色。 -->
    <div v-if="kpiCards.length" class="kpi-row">
      <span class="kpi-title">
        交付预测（按当前筛选）
        <span v-if="kpiSourceText" class="kpi-source">{{ kpiSourceText }}</span>
        <ElTooltip
          content="按 APS 排产计划算的预计按期情况：拿计划完成时间和交期比一比。这里是预测，不是已经发生的实际交付结果。"
          placement="top"
        >
          <Icon class="kpi-title-q" icon="vi-ep:question-filled" />
        </ElTooltip>
      </span>
      <div v-for="c in kpiCards" :key="c.key" class="kpi-card">
        <span class="kpi-bar" :style="{ background: c.color }"></span>
        <span class="kpi-label">{{ c.label }}</span>
        <span class="kpi-total">{{ c.row.totalOrders }} 单</span>
        <span class="kpi-metric" :style="{ color: RISK_COLORS.ON_TRACK }">
          按期 {{ c.row.onTimeOrders }}<em>（{{ kpiRateText(c.row.onTimeRatePct) }}）</em>
        </span>
        <span class="kpi-metric" :style="{ color: RISK_COLORS.DELAYED }">
          延期 {{ c.row.lateOrders }}<em>（{{ kpiRateText(c.row.lateRatePct) }}）</em>
        </span>
        <ElTooltip content="还没排产，或缺交期/计划完成时间 ⇒ 算不出按期还是延期。" placement="top">
          <span class="kpi-metric" :style="{ color: RISK_MISSING_COLOR }">
            未判定 {{ c.row.undeterminedOrders }}
          </span>
        </ElTooltip>
      </div>
    </div>

    <!-- FG/WIP 分区（2026-11-20 补；A2：5号位 r14318 已落码 `fgWipDimensionAvailable=true`）
         v0.2 P02：「FG/WIP与SO/PI正交分组」＋「FG/WIP 的业务数量和比率禁止混算」
         ⇒ 每个物料大分类独立一区，不合并、不与 SO/PI 分区相加。 -->
    <div v-if="fgWipCards.length" class="kpi-row kpi-row-fgwip">
      <span class="kpi-title">
        按物料大分类分区（FG/WIP）
        <ElTooltip
          content="按物料的标准大分类（成品／半成品／组件／原材料）分开统计。各分类的订单数和比率各自独立，不能相加或互相代替。"
          placement="top"
        >
          <Icon class="kpi-title-q" icon="vi-ep:question-filled" />
        </ElTooltip>
      </span>
      <div v-for="c in fgWipCards" :key="c.key" class="kpi-card">
        <span class="kpi-bar" style="background: #cbd5e1"></span>
        <span class="kpi-label">{{ c.label }}</span>
        <span class="kpi-total">{{ c.row.totalOrders }} 单</span>
        <span class="kpi-metric" :style="{ color: RISK_COLORS.ON_TRACK }">
          按期 {{ c.row.onTimeOrders }}<em>（{{ kpiRateText(c.row.onTimeRatePct) }}）</em>
        </span>
        <span class="kpi-metric" :style="{ color: RISK_COLORS.DELAYED }">
          延期 {{ c.row.lateOrders }}<em>（{{ kpiRateText(c.row.lateRatePct) }}）</em>
        </span>
        <ElTooltip content="还没排产，或缺交期/计划完成时间 ⇒ 算不出按期还是延期。" placement="top">
          <span class="kpi-metric" :style="{ color: RISK_MISSING_COLOR }">
            未判定 {{ c.row.undeterminedOrders }}
          </span>
        </ElTooltip>
      </div>
    </div>

    <!-- FG/WIP × SO/PI 交叉分区（v0.2 P02 分组层级 Domain→FG/WIP→SO/PI 的叶子层） -->
    <ElTable
      v-if="fgWipCrossRows.length"
      :data="fgWipCrossRows"
      size="small"
      border
      class="fgwip-cross-table"
    >
      <ElTableColumn label="物料大分类" min-width="110" prop="materialLabel" />
      <ElTableColumn label="订单类型" min-width="110" prop="orderTypeLabel" />
      <ElTableColumn label="订单数" width="100" align="right">
        <template #default="{ row }">{{ row.row.totalOrders }}</template>
      </ElTableColumn>
      <ElTableColumn label="按期" width="110" align="right">
        <template #default="{ row }">
          {{ row.row.onTimeOrders }}（{{ kpiRateText(row.row.onTimeRatePct) }}）
        </template>
      </ElTableColumn>
      <ElTableColumn label="延期" width="110" align="right">
        <template #default="{ row }">
          {{ row.row.lateOrders }}（{{ kpiRateText(row.row.lateRatePct) }}）
        </template>
      </ElTableColumn>
      <ElTableColumn label="未判定" width="100" align="right">
        <template #default="{ row }">{{ row.row.undeterminedOrders }}</template>
      </ElTableColumn>
    </ElTable>
    <ElAlert
      v-else-if="deliveryKpiError"
      type="error"
      :closable="false"
      show-icon
      :title="`交付指标加载失败：${deliveryKpiError}`"
    />
    <div v-else-if="deliveryKpiLoading" class="kpi-loading">交付指标加载中…</div>
    <ElAlert
      v-else-if="kpiEmptyText"
      type="info"
      :closable="false"
      show-icon
      :title="kpiEmptyText"
    />

    <!-- ===== 过滤栏 ===== -->
    <ElCard class="filter-bar">
      <!-- 交货日期快捷区间：计划员每天最常用的一刀 -->
      <div class="range-row">
        <span class="range-label">按交货日期挑单</span>
        <ElTooltip v-for="r in RANGE_PRESETS" :key="r.key" :content="r.desc" placement="top">
          <ElButton
            size="small"
            :type="activeRange === r.key ? 'primary' : 'default'"
            :plain="activeRange !== r.key"
            @click="applyRange(r.key)"
          >
            {{ r.label }}
          </ElButton>
        </ElTooltip>
        <span class="range-desc">{{ rangeDateText }} · {{ rangeDesc }}</span>
      </div>

      <div class="filter-row">
        <ElInput v-model="orderNoInput" placeholder="订单号" clearable class="filter-sm" />
        <ElInput v-model="piInput" placeholder="生产指示号" clearable class="filter-sm" />
        <ElInput v-model="materialInput" placeholder="物料编码" clearable class="filter-sm" />
        <ElInput
          v-model="customerNameInput"
          placeholder="客户名称（模糊）"
          clearable
          class="filter-sm"
        />
      </div>

      <div class="filter-row">
        <ElSelect
          :model-value="factoryCodeInput"
          placeholder="工厂"
          clearable
          filterable
          allow-create
          default-first-option
          class="filter-select-md filter-factory"
          @change="onFactoryChange"
        >
          <ElOption
            v-for="f in visibleFactories"
            :key="f.code"
            :label="`${f.name}（${f.code}）`"
            :value="f.code"
          />
        </ElSelect>
        <ElSelect
          :model-value="productFamilyInput"
          placeholder="产品族"
          clearable
          filterable
          allow-create
          default-first-option
          class="filter-select-md filter-family"
          @change="onFamilyChange"
        >
          <ElOption
            v-for="f in familyOptions"
            :key="f.code"
            :label="`${f.name}（${f.code}）`"
            :value="f.code"
          />
        </ElSelect>
        <ElSelect
          :model-value="statusInput"
          placeholder="订单状态"
          clearable
          class="filter-select-sm"
          @change="onStatusChange"
        >
          <ElOption
            v-for="s in ORDER_STATUS_FILTER_OPTIONS"
            :key="s"
            :label="ORDER_STATUS_LABELS[s]"
            :value="s"
          />
        </ElSelect>
        <ElSelect
          :model-value="delayStatusInput"
          placeholder="ERP 延期标记"
          clearable
          class="filter-select-sm"
          @change="onDelayStatusChange"
        >
          <ElOption
            v-for="d in ORDER_DELAY_STATUSES"
            :key="d"
            :label="ORDER_DELAY_STATUS_LABELS[d]"
            :value="d"
          />
        </ElSelect>
        <ElDatePicker
          :model-value="dueFrom"
          type="date"
          placeholder="交期起"
          value-format="YYYY-MM-DD"
          class="filter-date"
          @change="onDueFromChange"
        />
        <ElDatePicker
          :model-value="dueTo"
          type="date"
          placeholder="交期止"
          value-format="YYYY-MM-DD"
          class="filter-date"
          @change="onDueToChange"
        />
        <ElButton @click="onResetSearch">重置</ElButton>
      </div>
      <div v-if="dictError" class="filter-error">
        工厂 / 产品族字典加载失败，暂时只能按编码手输：{{ dictError }}
      </div>
    </ElCard>

    <!-- ===== 订单表 ===== -->
    <ElCard class="table-card">
      <template #header>
        <div class="panel-header">
          <span>订单列表</span>
          <span class="header-total">{{ listCountLabel }}</span>
        </div>
      </template>
      <ElTable
        ref="tableRef"
        v-loading="listLoading"
        :data="list"
        stripe
        size="small"
        row-key="id"
        empty-text="暂无订单"
        @row-click="onRowClick"
        @selection-change="onOrderSelectionChange"
      >
        <ElTableColumn
          type="selection"
          width="44"
          :selectable="() => canAdvance && !versionReadOnlyReason"
        />
        <!-- ===== P02 行展开：订单结构（B3，5号位 2026-11-17 复函）=====
             与 P03 深链共用同一端点/同一组件（OrderTreePanel），不建第二套。 -->
        <ElTableColumn type="expand" width="40">
          <template #default="{ row }">
            <div class="row-expand">
              <OrderTreePanel
                v-if="planVersionId"
                :order-id="row.id"
                :plan-version-id="planVersionId"
                @deep-link="onTreeDeepLink"
              />
              <ElAlert
                v-else
                type="info"
                :closable="false"
                show-icon
                title="先选择计划版本，才能看这单的物料承接结构"
              />
            </div>
          </template>
        </ElTableColumn>
        <!-- 订单号 + 生产指示号（生产指示号并进订单号下方，省一列；原来 14 列把右边的操作列挤到横向滚动外） -->
        <ElTableColumn prop="orderNo" label="订单号" min-width="150" show-overflow-tooltip>
          <template #default="{ row }">
            <div class="cell-order-no">{{ row.orderNo }}</div>
            <div v-if="row.mtS_InstructionNo" class="muted cell-sub">
              指示号 {{ row.mtS_InstructionNo }}
            </div>
          </template>
        </ElTableColumn>
        <ElTableColumn prop="materialCode" label="物料编码" min-width="140" show-overflow-tooltip />
        <!-- 排程域 + 订单类型（SO/PI）：稿 §4 P02 建议默认列「根订单号｜Domain｜SO/PI｜…」
             —— 两者原 DTO 就有（domainKey / orderType），此前未成列。 -->
        <ElTableColumn label="排程域" width="120">
          <template #default="{ row }">
            <span v-if="row.domainKey">{{ domainStore.label(row.domainKey) }}</span>
            <span v-else class="muted">—</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="订单类型" width="104">
          <template #default="{ row }">{{ orderTypeLabel(row.orderType) }}</template>
        </ElTableColumn>
        <!-- 要求生产完成日期（入物流仓日期）：稿 §4 P02 默认列；后端**尚未投影**该字段
             （已出函 5号位 A1）⇒ 显「未提供」，**不用客户交期顶替**（两者口径不同）。 -->
        <ElTableColumn label="要求生产完成日期" width="140">
          <template #default>
            <span
              class="muted"
              title="后端尚未投影「要求生产完成日期（入物流仓日期）」，已出函 5号位核对"
              >未提供</span
            >
          </template>
        </ElTableColumn>
        <ElTableColumn label="客户" min-width="110" show-overflow-tooltip>
          <template #default="{ row }">
            <span v-if="row.customerName">{{ row.customerName }}</span>
            <span v-else class="muted">—</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="数量" width="84" align="right">
          <template #default="{ row }">
            <span>{{ row.quantity }}</span>
            <span class="muted"> {{ row.uom }}</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="客户交期" width="176">
          <template #default="{ row }">
            <span :class="{ overdue: isOverdue(row.customerDueDate) }">
              {{ fmtDue(row.customerDueDate) }}
            </span>
          </template>
        </ElTableColumn>
        <!-- APS 预计完成时间（审核 C-01：原先只在详情抽屉里有，业务用户得逐行点开才能核对两个日期）
             后端口径 = 该订单在当前版本全部 Task 的 MAX(PlannedEndTime)（后端算，前端不自算）；
             未排产（无 Task 行）⇒ 整键省略 ⇒ 显示「未排产」，与「交付状态」列同词。 -->
        <ElTableColumn label="APS 预计完成时间" width="150">
          <template #default="{ row }">
            <span v-if="row.plannedCompletionTime">
              {{ formatUtcDateTime(row.plannedCompletionTime) }}
            </span>
            <span v-else class="muted" title="该订单在当前版本没有排产任务">未排产</span>
          </template>
        </ElTableColumn>
        <!-- 交付状态 = 计划派生判定 + 主因；延期天数已按稿分列到「预计延期天数」列 -->
        <ElTableColumn label="交付状态" width="150">
          <template #default="{ row }">
            <ElTag
              size="small"
              effect="light"
              :type="riskTagType(row.riskLevel)"
              :style="{
                background: riskColor(row.riskLevel),
                color: '#fff',
                borderColor: riskColor(row.riskLevel)
              }"
            >
              {{ riskLabel(row.riskLevel) }}
            </ElTag>
            <div
              v-if="row.primaryReasonCode"
              class="reason-unchecked"
              :title="row.primaryReasonCode"
            >
              {{ delayReasonLabel(row.primaryReasonCode) }}
            </div>
          </template>
        </ElTableColumn>
        <!-- 预计延期天数：稿 §4 P02「预计延期天数与交付状态」**分列**（不混作一栏）。
             后端给的是自然小时差 ⇒ 前端折算成天；口径（自然小时差 / 工作日差）待 5号位确认（致 5号位函 A4）。 -->
        <ElTableColumn label="预计延期天数" width="110" align="right">
          <template #default="{ row }">
            <span v-if="row.delayHours != null">{{ delayDaysText(row.delayHours) }}</span>
            <span v-else class="muted">—</span>
          </template>
        </ElTableColumn>
        <ElTableColumn label="订单状态" width="88" align="center">
          <template #default="{ row }">
            <ElTag size="small" :type="statusTagType(row.status)">
              {{ statusLabel(row.status) }}
            </ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn label="工厂" width="96">
          <template #default="{ row }">{{ factoryLabel(row.factoryCode) }}</template>
        </ElTableColumn>
        <ElTableColumn label="产品族" width="96">
          <template #default="{ row }">{{ familyLabel(row.productFamilyCode) }}</template>
        </ElTableColumn>
        <ElTableColumn prop="priority" label="优先级" width="68" align="right" />
        <ElTableColumn label="操作" width="62" align="center" fixed="right">
          <template #default="{ row }">
            <a href="javascript:;" @click.stop="openDetail(row)">详情</a>
          </template>
        </ElTableColumn>
        <template #empty>
          <ElEmpty :description="emptyText" />
        </template>
      </ElTable>
      <div class="pager-row">
        <ElPagination
          v-model:current-page="pageIndex"
          v-model:page-size="pageSize"
          :total="listTotal ?? 0"
          :page-sizes="[20, 50, 100, 200]"
          layout="sizes, prev, pager, next, jumper"
          background
          @current-change="onPageChange"
          @size-change="onSizeChange"
        />
      </div>
    </ElCard>

    <!-- ===== 详情 Drawer ===== -->
    <ElDrawer v-model="drawerVisible" size="620px" direction="rtl" @close="closeDetail">
      <template #header>
        <div class="drawer-head">
          <span class="drawer-title">订单详情</span>
          <span v-if="detailOrder" class="drawer-sub">{{ detailOrder.orderNo }}</span>
        </div>
      </template>

      <div v-if="detailLoading" class="drawer-loading"></div>

      <template v-else-if="detailOrder">
        <!-- ===== 订单基本信息 ===== -->
        <div class="detail-section">
          <h4 class="section-title">基本信息</h4>
          <ElDescriptions :column="2" size="small" border>
            <ElDescriptionsItem label="订单号">{{ detailOrder.orderNo }}</ElDescriptionsItem>
            <ElDescriptionsItem label="生产指示号">
              {{ detailOrder.mtS_InstructionNo ?? '—' }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="物料编码">
              {{ detailOrder.materialCode }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="客户">
              {{ detailOrder.customerName ?? '—' }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="客户类别">
              {{ customerSegmentLabel(detailOrder.customerSegment) }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="工厂">
              {{ factoryLabel(detailOrder.factoryCode) }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="产品族">
              {{ familyLabel(detailOrder.productFamilyCode) }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="排程域">
              {{ domainStore.label(detailOrder.domainKey) }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="订单数量">
              {{ detailOrder.quantity }} {{ detailOrder.uom }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="优先级">
              {{ detailOrder.priority }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="客户交期">
              {{ formatUtcDateTime(detailOrder.customerDueDate) }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="承诺交期">
              {{ formatUtcDateTime(detailOrder.promisedDate) }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="订单状态">
              <ElTag size="small" :type="statusTagType(detailOrder.status)">
                {{ statusLabel(detailOrder.status) }}
              </ElTag>
            </ElDescriptionsItem>
            <ElDescriptionsItem label="交付状态">
              <ElTag v-if="detailRow" size="small" :type="riskTagType(detailRow.riskLevel)">
                {{ riskLabel(detailRow.riskLevel) }}
              </ElTag>
              <span v-else>—</span>
            </ElDescriptionsItem>
            <ElDescriptionsItem label="APS 预计完成">
              {{ formatUtcDateTime(detailRow?.plannedCompletionTime) }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="延期时长">
              {{ delayHoursText(detailRow?.delayHours) || '—' }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="延期原因" :span="2">
              <span
                :class="{
                  'reason-unchecked': !!(
                    detailRow?.primaryReasonCode ?? detailOrder.primaryReasonCode
                  )
                }"
                >{{
                  delayReasonLabel(detailRow?.primaryReasonCode ?? detailOrder.primaryReasonCode)
                }}</span
              >
            </ElDescriptionsItem>
            <ElDescriptionsItem label="订单类型">
              {{ orderTypeLabel(detailOrder.orderType) }}
            </ElDescriptionsItem>
            <ElDescriptionsItem label="BOM 版本">
              {{ detailOrder.bomno ?? '—' }}
            </ElDescriptionsItem>
          </ElDescriptions>
        </div>

        <!-- ===== Pegging 供应承接 ===== -->
        <div class="detail-section">
          <h4 class="section-title">Pegging 供应承接</h4>
          <ElTable :data="detailPegging" size="small" border>
            <ElTableColumn prop="allocationSequence" label="序号" width="60" align="right" />
            <ElTableColumn label="来源类型" min-width="110">
              <template #default="{ row }">{{ supplyLabel(row.supplyType) }}</template>
            </ElTableColumn>
            <ElTableColumn prop="materialCode" label="物料" min-width="140" show-overflow-tooltip />
            <ElTableColumn prop="allocatedQty" label="承接数量" width="90" align="right" />
            <ElTableColumn prop="supplyFactoryCode" label="来源工厂" width="90" />
            <!-- 来源仓库（2026-11-20；D1）：PSA 的 supplyWarehouseCode 全 0 填充
                 ⇒ 改走**供给事实行**（采购在途档案的存储地点），查不到显「未提供」 -->
            <ElTableColumn label="来源仓库" min-width="150" show-overflow-tooltip>
              <template #default="{ row }">
                <ElTooltip
                  :content="
                    warehouseHint({
                      poNo: row.supplyDocumentNo,
                      materialCode: row.materialCode
                    })
                  "
                  placement="top"
                >
                  <span>
                    {{
                      warehouseText({ poNo: row.supplyDocumentNo, materialCode: row.materialCode })
                    }}
                  </span>
                </ElTooltip>
              </template>
            </ElTableColumn>
            <ElTableColumn label="单据" min-width="120" show-overflow-tooltip>
              <template #default="{ row }">
                <span v-if="row.supplyDocumentNo">
                  {{ row.supplyDocumentNo }}
                  <span v-if="row.supplyDocumentType" class="muted">
                    / {{ row.supplyDocumentType }}</span
                  >
                </span>
                <span v-else class="muted">—</span>
              </template>
            </ElTableColumn>
            <ElTableColumn label="ETA" width="130">
              <template #default="{ row }">
                {{ formatUtcDateTime(row.eta) }}
              </template>
            </ElTableColumn>
            <ElTableColumn label="可用时间" width="130">
              <template #default="{ row }">
                {{ formatUtcDateTime(row.knownAvailableTime) }}
              </template>
            </ElTableColumn>
            <ElTableColumn prop="commitmentStatus" label="履约承诺" width="90" />
            <ElTableColumn prop="supplyMode" label="供应方式" width="90" />
            <template #empty>
              <ElEmpty description="该订单暂无 Pegging 承接记录" :image-size="60" />
            </template>
          </ElTable>
        </div>

        <!-- ===== 生产任务 ===== -->
        <div class="detail-section">
          <h4 class="section-title">生产任务</h4>
          <ElTable :data="detailTasks" size="small" border>
            <ElTableColumn prop="taskNo" label="任务号" width="110" show-overflow-tooltip />
            <ElTableColumn label="工序" width="120">
              <template #default="{ row }">
                {{ row.operationCode ?? '—' }} (Seq {{ row.operationSeq }})
              </template>
            </ElTableColumn>
            <ElTableColumn label="资源" min-width="120" show-overflow-tooltip>
              <template #default="{ row }">
                {{ row.resourceName ?? row.resourceCode ?? '—' }}
              </template>
            </ElTableColumn>
            <ElTableColumn prop="quantity" label="净产出" width="80" align="right" />
            <ElTableColumn label="计划加工" width="90" align="right">
              <template #default="{ row }">
                <span :class="{ 'hl-warn': row.plannedProcessQty !== row.quantity }">
                  {{ row.plannedProcessQty ?? '—' }}
                </span>
              </template>
            </ElTableColumn>
            <ElTableColumn label="开始" width="125">
              <template #default="{ row }">
                {{ formatUtcMonthDayHM(row.plannedStartTime) }}
              </template>
            </ElTableColumn>
            <ElTableColumn label="结束" width="125">
              <template #default="{ row }">
                {{ formatUtcMonthDayHM(row.plannedEndTime) }}
              </template>
            </ElTableColumn>
            <ElTableColumn label="状态" width="90">
              <template #default="{ row }">
                <ElTag size="small">{{ TASK_STATUS_LABELS[row.status] ?? row.status }}</ElTag>
              </template>
            </ElTableColumn>
            <ElTableColumn label="关键路径" width="80" align="center">
              <template #default="{ row }">
                <ElTag v-if="row.isCriticalPath" size="small" type="danger">关键</ElTag>
                <span v-else class="muted">—</span>
              </template>
            </ElTableColumn>
            <ElTableColumn label="锁定" width="60" align="center">
              <template #default="{ row }">
                <ElTag v-if="row.isLocked" size="small" type="warning">锁</ElTag>
                <span v-else class="muted">—</span>
              </template>
            </ElTableColumn>
            <template #empty>
              <ElEmpty description="该订单暂无生产任务" :image-size="60" />
            </template>
          </ElTable>
          <p class="u05-hint">
            净产出与计划加工是两个不同口径：计划加工含良率损耗，净产出为最终交付数量。
          </p>
        </div>
      </template>

      <ElEmpty v-else :description="detailError || '请选择订单查看详情'" />
    </ElDrawer>

    <!-- ===== P03 深链：订单结构抽屉（与 P02 行展开同一组件、同一端点）===== -->
    <ElDrawer
      v-model="treeDrawerVisible"
      size="72%"
      direction="rtl"
      title="订单结构"
      :close-on-click-modal="false"
    >
      <OrderTreePanel
        v-if="treeOrderId && treePlanVersionId"
        :order-id="treeOrderId"
        :plan-version-id="treePlanVersionId"
        :focus-task-id="treeFocusTaskId"
        :focus-material-id="treeFocusMaterialId"
        @deep-link="onTreeDeepLink"
      />
      <ElEmpty v-else description="地址栏里缺少订单号或计划版本，无法打开订单结构" />
    </ElDrawer>

    <!-- ===== §10A.1 已有订单提前 Dialog（EXISTING_ORDER_ADVANCE）===== -->
    <OrderAdvanceDialog
      :visible="advanceVisible"
      :submitting="advanceSubmitting"
      :domain-options="advanceDomainOptions"
      :orders="advanceCandidates"
      :preselected-order-ids="selectedOrders.map((o) => o.id)"
      :default-domain-key="advanceDefaultDomain"
      @update:visible="(v) => (advanceVisible = v)"
      @submit="submitOrderAdvance"
    />
  </div>
</template>

<style lang="less" scoped>
.aps-order {
  display: flex;
  min-height: calc(100vh - 100px);
  padding: 16px;
  background: #f5f7fa;
  flex-direction: column;
  gap: 16px;
}

.toolbar {
  display: flex;
  gap: 12px;
  align-items: center;

  .version-select {
    width: 380px;
  }

  .toolbar-meta {
    flex: 1;
    font-size: 12px;
  }
}

.version-option {
  display: flex;
  gap: 8px;
  align-items: center;

  .version-code {
    font-weight: 500;
    color: #1e293b;
  }

  .version-meta {
    font-size: 12px;
  }
}

.muted {
  color: #94a3b8;
}

.cell-order-no {
  font-weight: 500;
  color: #1e293b;
}

.cell-sub {
  font-size: 11px;
  line-height: 1.3;
}

.delay-hours {
  font-size: 12px;
  line-height: 1.4;
}

/* 未入冻结 15 码字典的主因码：中性灰，不与正式码同色，避免被当成已确认原因 */
.reason-unchecked {
  font-size: 12px;
  line-height: 1.4;
  color: #94a3b8;
  font-style: italic;
}

/* KPI：一行紧凑指标条（四项 + 口径说明同一行），不占首屏高度 */
.kpi-row {
  display: flex;
  flex-wrap: wrap;
  align-items: stretch;
  padding: 6px 14px;
  background: #f8fafc;
  border: 1px solid #e2e8f0;
  border-radius: 8px;
}

/* FG/WIP 分区行（2026-11-20；A2）：与上面一行同款，只是分区维度换成物料大分类 */
.kpi-row-fgwip {
  margin-top: 6px;
}

/* FG/WIP × SO/PI 交叉分区表（2026-11-20；A2）：v0.2 P02 分组层级最里层 */
.fgwip-cross-table {
  margin-top: 6px;
  width: 100%;
}

/* 交付 KPI 分区卡（2026-11-17 B2）：三张卡并排，每张卡一行读完
   —— 2026-11-18：三卡内容宽度合计 ~1300px，容器（1600 屏）内宽 ~1284px，
      差十几像素就会把「销售订单」整张甩到第二行左端（看着像断了）。
      ⇒ 改成**等宽三列**（flex: 1 1 0）+ 卡内可换行：窄了就卡内折行，绝不甩卡。 */
.kpi-card {
  display: flex;
  flex: 1 1 0;
  flex-wrap: wrap;
  align-content: center;
  align-items: center;
  gap: 2px 6px;
  min-width: 0;
  padding: 0 10px;
  border-right: 1px solid #e2e8f0;

  /* 卡内每个数不许自己折行（只允许在数与数之间折） */
  > span {
    white-space: nowrap;
  }

  &:last-of-type {
    border-right: none;
  }
}

/* 标题（2026-11-18 工厂视角）：一句话说清「这是预测、按当前筛选」
   —— 独占第一行（flex 1 1 100%）：三张分区卡的内容宽度接近容器宽，
   标题若与卡并排会把「销售订单」挤到第二行左端，看着像断了 ⇒ 标题单独一行更整齐。 */
.kpi-title {
  display: flex;
  align-items: center;
  gap: 6px;
  flex: 1 1 100%;
  padding-bottom: 4px;
  margin-bottom: 4px;
  font-size: 12px;
  font-weight: 600;
  color: #334155;
  white-space: nowrap;
  border-bottom: 1px solid #e2e8f0;
}

.kpi-source {
  font-size: 11px;
  font-weight: 400;
  color: #94a3b8;
}

.kpi-title-q {
  font-size: 13px;
  color: #94a3b8;
  cursor: help;
}

.kpi-total {
  font-size: 13px;
  font-weight: 600;
  color: #334155;
}

.kpi-metric {
  font-size: 12px;
  color: #475569;

  em {
    font-style: normal;
    font-size: 11px;
    opacity: 0.75;
  }
}

.kpi-bar {
  width: 3px;
  height: 12px;
  border-radius: 2px;
}

.kpi-label {
  font-size: 12px;
  color: #64748b;
}

/* 交付 KPI 加载中 */
.kpi-loading {
  padding: 6px 14px;
  font-size: 12px;
  color: #94a3b8;
}

.filter-bar {
  border-radius: 8px;
}

.range-row {
  display: flex;
  gap: 8px;
  align-items: center;
  flex-wrap: wrap;
  padding-bottom: 12px;
  margin-bottom: 12px;
  border-bottom: 1px dashed #e2e8f0;

  .range-label {
    font-size: 13px;
    color: #475569;
  }

  .range-desc {
    /* 说明整句另起一行（挤在按钮后面会被右边缘截断，看不全） */
    flex: 1 1 100%;
    font-size: 12px;
    color: #94a3b8;
  }
}

.filter-row {
  display: flex;
  gap: 12px;
  align-items: center;
  flex-wrap: wrap;

  & + .filter-row {
    margin-top: 12px;
  }
}

.filter-sm {
  width: 175px;
}

.filter-select-sm {
  width: 145px;
}

.filter-select-md {
  width: 190px;
}

.filter-date {
  width: 140px;
}

.filter-error {
  margin-top: 10px;
  font-size: 12px;
  color: #d97706;
}

.table-card {
  border-radius: 8px;
}

.panel-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  font-size: 14px;
  font-weight: 600;
  color: #1e293b;
}

.header-total {
  font-size: 12px;
  font-weight: 400;
  color: #94a3b8;
}

.pager-row {
  display: flex;
  margin-top: 12px;
  justify-content: flex-end;
}

.row-expand {
  padding: 10px 12px 12px 40px;
  background: #fbfdff;
}

.overdue {
  font-weight: 600;
  color: #ef4444;
}

.hl-warn {
  font-weight: 600;
  color: #f59e0b;
}

.u05-hint {
  padding: 8px 10px;
  margin: 8px 0 0;
  font-size: 12px;
  line-height: 1.6;
  color: #64748b;
  background: #f8fafc;
  border-left: 3px solid #3b82f6;
  border-radius: 4px;
}

.drawer-head {
  display: flex;
  gap: 12px;
  align-items: baseline;

  .drawer-title {
    font-size: 16px;
    font-weight: 600;
    color: #1e293b;
  }

  .drawer-sub {
    font-size: 12px;
    color: #94a3b8;
  }
}

.detail-section {
  margin-bottom: 18px;

  .section-title {
    display: flex;
    margin: 0 0 8px;
    font-size: 13px;
    font-weight: 600;
    color: #475569;
    align-items: center;
    gap: 8px;
  }
}

.drawer-loading {
  height: 200px;
}
</style>
