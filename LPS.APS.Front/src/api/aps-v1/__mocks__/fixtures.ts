/**
 * APS V1 4号位 — Mock 假数据
 *
 * 用途：
 *  - 后端接口未就绪时，前端按 4号位文档定义 DTO 后用本 fixture 推进
 *  - 后端就绪后切换 VITE_USE_MOCK=false 即走真实 API
 *
 * 字段名严格使用 camelCase（前端 TS 风格），与 4号位文档第 19 节一致
 */

import type {
  GanttDataDto,
  GanttResourceDto,
  GanttTaskDto,
  PlanVersionSummaryDto,
  ScheduleSummaryDto
} from '../types'
import type { TaskStatus } from '../types'
import { TASK_STATUSES } from '../types'

/* ========== 时间工具 ========== */
const now = new Date()
const iso = (d: Date) => d.toISOString()
const dayOffset = (days: number, hour = 8) => {
  const d = new Date(now)
  d.setDate(d.getDate() + days)
  d.setHours(hour, 0, 0, 0)
  return iso(d)
}
const hourOffset = (hours: number) => {
  const d = new Date(now)
  d.setHours(d.getHours() + hours)
  return iso(d)
}

/* ========== Schedule（与后端对齐，可直接对接） ========== */

const RESOURCES: GanttResourceDto[] = [
  {
    resourceId: 1,
    resourceCode: 'MC01',
    resourceName: '注塑机-01',
    domainKey: 'FAMILY_INJECTION',
    factoryId: 1,
    factoryName: '苏州厂',
    productionDepartmentId: 1,
    productionDepartmentName: '注塑车间',
    stage: 'STG-INJ',
    // Section 十二 / U20：MC01 维护窗口 + 故障提示
    unavailableWindows: [
      { from: dayOffset(2, 8), to: dayOffset(2, 14), reason: '计划维护（季度保养）' },
      {
        from: dayOffset(5, 10),
        to: dayOffset(5, 18),
        reason: '设备故障 - 液压泵异常（Section 十二）'
      }
    ]
  },
  {
    resourceId: 2,
    resourceCode: 'MC02',
    resourceName: '注塑机-02',
    domainKey: 'FAMILY_INJECTION',
    factoryId: 1,
    factoryName: '苏州厂',
    productionDepartmentId: 1,
    productionDepartmentName: '注塑车间',
    stage: 'STG-INJ'
  },
  {
    resourceId: 3,
    resourceCode: 'ASM01',
    resourceName: '装配线-01',
    domainKey: 'FAMILY_ASSEMBLY',
    factoryId: 1,
    factoryName: '苏州厂',
    productionDepartmentId: 2,
    productionDepartmentName: '装配车间',
    stage: 'STG-ASM'
  },
  {
    resourceId: 4,
    resourceCode: 'ASM02',
    resourceName: '装配线-02',
    domainKey: 'FAMILY_ASSEMBLY',
    factoryId: 1,
    factoryName: '苏州厂',
    productionDepartmentId: 2,
    productionDepartmentName: '装配车间',
    stage: 'STG-ASM'
  },
  {
    resourceId: 5,
    resourceCode: 'TST01',
    resourceName: '测试台-01',
    domainKey: 'FAMILY_TEST',
    factoryId: 1,
    factoryName: '苏州厂',
    productionDepartmentId: 3,
    productionDepartmentName: '测试车间',
    stage: 'STG-TEST'
  },
  {
    resourceId: 6,
    resourceCode: 'MC03',
    resourceName: '注塑机-03',
    domainKey: 'FAMILY_INJECTION',
    factoryId: 2,
    factoryName: '成都厂',
    productionDepartmentId: 1,
    productionDepartmentName: '注塑车间',
    stage: 'STG-INJ'
  },
  {
    resourceId: 7,
    resourceCode: 'ASM03',
    resourceName: '装配线-03',
    domainKey: 'FAMILY_ASSEMBLY',
    factoryId: 2,
    factoryName: '成都厂',
    productionDepartmentId: 2,
    productionDepartmentName: '装配车间',
    stage: 'STG-ASM'
  },
  {
    resourceId: 8,
    resourceCode: 'TST02',
    resourceName: '测试台-02',
    domainKey: 'FAMILY_TEST',
    factoryId: 1,
    factoryName: '苏州厂',
    productionDepartmentId: 3,
    productionDepartmentName: '测试车间',
    stage: 'STG-TEST'
  }
]

/** 在合法状态中循环，确保只有 5 种枚举值出现（4号位文档第 6 节） */
const pickStatus = (seed: number): TaskStatus => TASK_STATUSES[seed % TASK_STATUSES.length]

const TASKS: GanttTaskDto[] = (() => {
  const list: GanttTaskDto[] = []
  const orders = [
    {
      orderNo: 'ORD-2026-0001',
      material: '电机壳体-A / 铝合金ADC12 / 6.5kg / 静电喷涂黑',
      orderId: 101
    },
    {
      orderNo: 'ORD-2026-0002',
      material: '机柜上盖-B / 镀锌钢板1.2mm / 600×400mm / 折弯成型',
      orderId: 102
    },
    {
      orderNo: 'ORD-2026-0003',
      material: '控制箱-C / 钢结构壳体 / 0.8mm冷板 / 喷塑RAL7035',
      orderId: 103
    },
    {
      orderNo: 'ORD-2026-0004',
      material: '标准泵-D / 铸铁HT200 / DN80 / 法兰连接',
      orderId: 104
    },
    {
      orderNo: 'ORD-2026-0005',
      material: '包装套件-E / 蜂窝纸板+PE膜 / 850×600×400 / VCI防锈',
      orderId: 105
    },
    {
      orderNo: 'ORD-2026-0006',
      material: '机架焊接件-F / Q235B型材 / 1200×800×1800 / 二保焊',
      orderId: 106
    }
  ]
  const operations = [
    { code: 'OP10', seq: 10, name: '切割下料' },
    { code: 'OP20', seq: 20, name: '焊接成型' },
    { code: 'OP30', seq: 30, name: '表面处理' },
    { code: 'OP40', seq: 40, name: '总装调试' },
    { code: 'OP50', seq: 50, name: '出厂测试' }
  ]
  let taskId = 1000
  for (let i = 0; i < 60; i++) {
    const order = orders[i % orders.length]
    const op = operations[i % operations.length]
    const resource = RESOURCES[i % RESOURCES.length]
    const startDays = (i % 14) - 3 // -3 ~ 10
    const status = pickStatus(i)
    const plannedStart = dayOffset(startDays, 8 + (i % 8))
    // 时长多样化：4h ~ 32h，按 i 分段（OP10 切割 4-8h / OP20 焊接 8-16h / OP30 喷涂 6-12h
    //   / OP40 总装 10-20h / OP50 测试 4-8h；少量跨日 24-32h）
    const opDurations: Record<string, number[]> = {
      OP10: [4, 6, 8, 4, 6],
      OP20: [10, 12, 14, 16, 8],
      OP30: [6, 8, 10, 12, 6],
      OP40: [12, 16, 20, 14, 10],
      OP50: [4, 6, 8, 6, 4]
    }
    const opDur = opDurations[op.code] ?? [4, 6, 8]
    let durationH = opDur[i % opDur.length]
    // 偶发跨日长 Task（i%11===2 → 24-32h，跨 2-3 天；让甘特条更长更好看）
    if (i % 11 === 2) durationH = 24 + (i % 3) * 4 // 24/28/32
    const plannedEndHours = 8 + (i % 8) + durationH
    const plannedEnd = dayOffset(startDays + Math.floor(plannedEndHours / 24), plannedEndHours % 24)
    const isDelayed = i % 7 === 0 // 与下方根因类型选择器 i % 5 解耦，确保 5 类根因都能展开

    // U05：净需求 + 计划加工（含良率补偿）
    const netQty = 50 + i * 3
    const plannedProcessQty = Math.ceil(netQty / 0.95)

    // U04：每 7 条任务中挑 1 条做 TaskShare（同一 Task 承接多 Order）
    let taskShare: GanttTaskDto['taskShare']
    if (i % 7 === 3) {
      const secondOrder = orders[(i + 2) % orders.length]
      const sharedQty = Math.floor(plannedProcessQty * 0.4)
      const mainQty = plannedProcessQty - sharedQty
      taskShare = [
        {
          orderId: order.orderId,
          orderNo: order.orderNo,
          shareQty: mainQty,
          shareRatio: mainQty / plannedProcessQty
        },
        {
          orderId: secondOrder.orderId,
          orderNo: secondOrder.orderNo,
          shareQty: sharedQty,
          shareRatio: sharedQty / plannedProcessQty
        }
      ]
    }

    // U14：每 13 条任务中挑 1 条做跨 Domain 阻挡（资源被外 Domain 占用）
    const crossDomainBlocked = i % 13 === 5
    const crossDomainBlockReason = crossDomainBlocked
      ? `外 Domain 共享设备 ${resource.resourceCode} 被 ${resource.domainKey === 'FAMILY_TEST' ? 'FAMILY_INJECTION' : 'FAMILY_TEST'} 域占用至 ${dayOffset(startDays + 2, 18).slice(0, 10)}`
      : undefined

    // U06：每 17 条任务中挑 1 条做数量-时间拆分（产能不足导致）
    let taskSegments: GanttTaskDto['taskSegments']
    if (i % 17 === 4) {
      const seg1Qty = Math.ceil(plannedProcessQty * 0.6)
      const seg2Qty = plannedProcessQty - seg1Qty
      const seg1Start = plannedStart
      // 段 1 占用 60% 时长（向上取整），段 2 补剩余 40%
      const seg1Hours = Math.ceil(durationH * 0.6)
      const seg1End = dayOffset(
        startDays + Math.floor((8 + (i % 8) + seg1Hours) / 24),
        (8 + (i % 8) + seg1Hours) % 24
      )
      const seg2Start = seg1End
      const seg2End = plannedEnd
      taskSegments = [
        {
          segmentSeq: 1,
          startTime: seg1Start,
          endTime: seg1End,
          qty: seg1Qty,
          qtyRatio: seg1Qty / plannedProcessQty,
          reason: 'MC01 主产能段'
        },
        {
          segmentSeq: 2,
          startTime: seg2Start,
          endTime: seg2End,
          qty: seg2Qty,
          qtyRatio: seg2Qty / plannedProcessQty,
          reason: '次日加班段补齐'
        }
      ]
    }

    // Section 二.2：FactType（默认 RESULT；部分任务带其它 4 类）
    let factType: GanttTaskDto['factType']
    if (crossDomainBlocked) {
      factType = 'FACT' // 资源被外 Domain 占用是当下事实
    } else if (i % 19 === 7) {
      factType = 'ESTIMATED' // 占位未承诺供应
    } else if (i % 23 === 11 && isDelayed) {
      factType = 'RECOMMENDATION' // 建议发起重排
    } else {
      factType = 'RESULT'
    }

    // Section 十一：根因（4号位文档第 11 节硬约束：必须优先展示根因，不能只丢 DUE_DATE_RISK）
    // 文档第 11 节原文示例：采购料9月3日才可用、MC01无产能、上游Stage完成晚、Firm任务占关键窗口
    const delayReasons: NonNullable<GanttTaskDto['delayReasons']> = []
    let upstreamStageBlocked: boolean | undefined
    let upstreamStageCode: string | undefined
    if (crossDomainBlocked) {
      delayReasons.push({
        reasonCode: 'CROSS_DOMAIN_VERSION_MISMATCH_RISK',
        issueCategory: 'SCHEDULING_ISSUE',
        severity: 'ERROR',
        description: '外 Domain 共享设备占用，本 Domain 任务无法按时开工',
        relatedObjectRef: resource.resourceCode,
        impactHours: 8
      })
    }
    if (isDelayed) {
      if (i % 5 === 0) {
        delayReasons.push({
          reasonCode: 'RESOURCE_CAPACITY_WAIT',
          issueCategory: 'SCHEDULING_ISSUE',
          severity: 'WARN',
          description: `设备 ${resource.resourceCode} 产能不足，无法在本时段内完工`,
          relatedObjectRef: resource.resourceCode,
          impactHours: 4
        })
      } else if (i % 5 === 1) {
        delayReasons.push({
          reasonCode: 'MATERIAL_SHORTAGE',
          issueCategory: 'DATA_ISSUE',
          severity: 'WARN',
          description: `PO-${String(1000 + i).padStart(4, '0')} 物料预计 ${dayOffset(
            startDays + 3,
            14
          )
            .slice(0, 10)
            .replace(/-/g, '/')} 才到货`,
          relatedObjectRef: `PO-${String(1000 + i).padStart(4, '0')}`,
          impactHours: 24
        })
      } else if (i % 5 === 2) {
        upstreamStageBlocked = true
        upstreamStageCode = 'STG-AS'
        delayReasons.push({
          reasonCode: 'PRECEDENCE_WAIT',
          issueCategory: 'SCHEDULING_ISSUE',
          severity: 'WARN',
          description: `上游 ${upstreamStageCode} 完成晚，预计延误 6h 后才能开始本工序`,
          relatedObjectRef: upstreamStageCode,
          impactHours: 6
        })
      } else if (i % 5 === 3) {
        delayReasons.push({
          reasonCode: 'FROZEN_ZONE_LOCK',
          issueCategory: 'SCHEDULING_ISSUE',
          severity: 'INFO',
          description: '关键窗口已被 Firm/FROZEN 任务占用，本任务只能后置',
          relatedObjectRef: 'FIRM-WINDOW',
          impactHours: 3
        })
      } else {
        delayReasons.push({
          reasonCode: 'EQUIPMENT_BREAKDOWN_RISK',
          issueCategory: 'DATA_ISSUE',
          severity: 'ERROR',
          description: `${resource.resourceCode} 在本时段发生设备故障，预计 ${dayOffset(
            startDays,
            18
          ).slice(11, 16)} 恢复`,
          relatedObjectRef: resource.resourceCode,
          impactHours: 12
        })
      }
    }

    // Section 十六：MES 下发资格
    // 典型不可下发：Candidate / UNLOCATED / 无 PI / 仍依赖 Placeholder / 窗口外 / 已取消
    let mesEligible: GanttTaskDto['mesEligible']
    const mesIneligibleReasons: string[] = []
    if (status === 'CANCELLED') {
      mesEligible = 'INELIGIBLE'
      mesIneligibleReasons.push('Task 已取消')
    } else if (status === 'COMPLETED') {
      mesEligible = 'INELIGIBLE'
      mesIneligibleReasons.push('Task 已完成')
    } else if (status === 'PLANNED') {
      mesEligible = 'UNKNOWN'
      mesIneligibleReasons.push('未达下发窗口，等待释放判定')
    } else if (crossDomainBlocked) {
      mesEligible = 'INELIGIBLE'
      mesIneligibleReasons.push('外 Domain 共享设备阻挡，未开始加工（U14）')
    } else if (factType === 'ESTIMATED') {
      mesEligible = 'INELIGIBLE'
      mesIneligibleReasons.push('依赖 PLANNING_PURCHASE_PLACEHOLDER，无正式承诺（U18）')
    } else if (i % 9 === 4) {
      mesEligible = 'INELIGIBLE'
      mesIneligibleReasons.push('PI 存在 UNLOCATED 数量，未下推具体位置（U16）')
    } else if (i % 11 === 7) {
      mesEligible = 'INELIGIBLE'
      mesIneligibleReasons.push('无对应 PI 规划 Task（U17）')
    } else {
      mesEligible = 'ELIGIBLE'
    }

    list.push({
      taskId: taskId++,
      taskNo: `T${taskId}`,
      orderId: order.orderId,
      orderNo: order.orderNo,
      domainKey: resource.domainKey,
      materialId: 200 + (i % orders.length),
      materialCode: `M-${(i % 9) + 1}`,
      materialName: order.material,
      resourceId: resource.resourceId,
      operationCode: op.code,
      operationSeq: op.seq,
      netQty,
      plannedProcessQty,
      quantity: plannedProcessQty,
      uom: 'PCS',
      plannedStartTime: plannedStart,
      plannedEndTime: plannedEnd,
      status,
      isDelayed,
      lockMarker: i % 7 === 0 ? 'EXECUTION' : i % 11 === 0 ? 'FIRM' : undefined,
      taskShare,
      crossDomainBlocked: crossDomainBlocked || undefined,
      crossDomainBlockReason,
      taskSegments,
      factType,
      mesEligible,
      mesIneligibleReasons: mesIneligibleReasons.length > 0 ? mesIneligibleReasons : undefined,
      delayReasons: delayReasons.length > 0 ? delayReasons : undefined,
      upstreamStageBlocked,
      upstreamStageCode
    })
  }
  return list
})()

export const mockVersions: PlanVersionSummaryDto[] = [
  {
    id: 1001,
    versionCode: 'V-2026-001',
    versionCategory: 'FULL',
    planHorizonStart: dayOffset(-7),
    planHorizonEnd: dayOffset(83),
    status: 'ACTIVE',
    computedAt: hourOffset(-2),
    totalTasks: TASKS.length,
    createdAt: hourOffset(-2)
  },
  {
    id: 1000,
    versionCode: 'V-2026-000',
    versionCategory: 'FULL',
    planHorizonStart: dayOffset(-7),
    planHorizonEnd: dayOffset(83),
    status: 'ARCHIVED',
    computedAt: hourOffset(-26),
    totalTasks: 58,
    createdAt: hourOffset(-26)
  },
  {
    id: 999,
    versionCode: 'C-2026-003',
    versionCategory: 'CANDIDATE',
    planHorizonStart: dayOffset(-7),
    planHorizonEnd: dayOffset(83),
    status: 'CANDIDATE',
    computedAt: hourOffset(-1),
    totalTasks: 62,
    createdAt: hourOffset(-1)
  }
]

export const mockSummary = (planVersionId: number): ScheduleSummaryDto => {
  const scheduled = TASKS.filter((t) => t.status !== 'CANCELLED').length
  const delayed = TASKS.filter((t) => t.isDelayed).length
  return {
    planVersionId,
    versionCode: mockVersions.find((v) => v.id === planVersionId)?.versionCode ?? 'UNKNOWN',
    status: 'ACTIVE',
    totalTasks: TASKS.length,
    scheduledTasks: scheduled,
    unscheduledTasks: 0,
    delayedTasks: delayed,
    totalOrders: 6,
    delayedOrders: Math.round(delayed / 5),
    firstTaskStart: TASKS[0]?.plannedStartTime,
    lastTaskEnd: TASKS[TASKS.length - 1]?.plannedEndTime,
    computeDurationSeconds: 18,
    versionExists: true,
    scopeDenied: false
  }
}

export const mockGantt = (planVersionId: number): GanttDataDto => ({
  planVersionId,
  versionCode: mockVersions.find((v) => v.id === planVersionId)?.versionCode ?? 'UNKNOWN',
  planHorizonStart: dayOffset(-7),
  planHorizonEnd: dayOffset(83),
  resources: RESOURCES,
  tasks: TASKS
})

/* ========== Orders（页面 2 订单/需求计划） ========== */

/** 订单静态维度表（mock 用，10 个物料 × 5 个客户 × 2 个工厂） */
/* 订单 mock 桩已删（2026-10-17）
 * 原因：整块建立在一份自拟 DTO（OrderBasicInfo / OrderScheduleDetailDto / OrderSummaryStatus）上，
 * 那三个类型与后端真实契约零重合；且工厂 `F-SUZ-01`、产品族 `INJECTION`、客户 `CUST-001` 全是编的，
 * 与 dev 实库（`CN` / `CYL` / 中文客户名）无一命中 —— mock 分支会把假数据端上页面。
 * 前端 mock 模式下订单页直接空态，与 candidate 页 10-16 的同类处置一致。 */
