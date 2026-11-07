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
    resourceCode: 'M-000',
    resourceName: '设备_0',
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
    resourceCode: 'M-001',
    resourceName: '设备_1',
    domainKey: 'FAMILY_INJECTION',
    factoryId: 1,
    factoryName: '苏州厂',
    productionDepartmentId: 1,
    productionDepartmentName: '注塑车间',
    stage: 'STG-INJ'
  },
  {
    resourceId: 3,
    resourceCode: 'M-002',
    resourceName: '设备_2',
    domainKey: 'FAMILY_ASSEMBLY',
    factoryId: 1,
    factoryName: '苏州厂',
    productionDepartmentId: 2,
    productionDepartmentName: '装配车间',
    stage: 'STG-ASM'
  },
  {
    resourceId: 4,
    resourceCode: 'M-003',
    resourceName: '设备_3',
    domainKey: 'FAMILY_ASSEMBLY',
    factoryId: 1,
    factoryName: '苏州厂',
    productionDepartmentId: 2,
    productionDepartmentName: '装配车间',
    stage: 'STG-ASM'
  },
  {
    resourceId: 5,
    resourceCode: 'M-004',
    resourceName: '设备_4',
    domainKey: 'FAMILY_TEST',
    factoryId: 1,
    factoryName: '苏州厂',
    productionDepartmentId: 3,
    productionDepartmentName: '测试车间',
    stage: 'STG-TEST'
  },
  {
    resourceId: 6,
    resourceCode: 'M-005',
    resourceName: '设备_5',
    domainKey: 'FAMILY_INJECTION',
    factoryId: 2,
    factoryName: '成都厂',
    productionDepartmentId: 1,
    productionDepartmentName: '注塑车间',
    stage: 'STG-INJ'
  },
  {
    resourceId: 7,
    resourceCode: 'M-006',
    resourceName: '设备_6',
    domainKey: 'FAMILY_ASSEMBLY',
    factoryId: 2,
    factoryName: '成都厂',
    productionDepartmentId: 2,
    productionDepartmentName: '装配车间',
    stage: 'STG-ASM'
  },
  {
    resourceId: 8,
    resourceCode: 'M-007',
    resourceName: '设备_7',
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
  // 物料列短名（对齐主流排产系统左表：物料只放一个短名，长描述进 materialName 走悬浮）
  const orders = [
    { orderNo: 'SO240301-021', materialCode: '包装套件', material: '包装套件', orderId: 101 },
    { orderNo: 'SO240302-007', materialCode: '塑料齿轮', material: '塑料齿轮', orderId: 102 },
    { orderNo: 'SO240302-008', materialCode: '金属支架', material: '金属支架', orderId: 103 },
    { orderNo: 'SO240302-012', materialCode: '机架焊接件', material: '机架焊接件', orderId: 104 },
    { orderNo: 'SO240303-013', materialCode: '电机壳体', material: '电机壳体', orderId: 105 },
    { orderNo: 'SO240301-009', materialCode: '机柜上盖', material: '机柜上盖', orderId: 106 },
    { orderNo: 'SO240303-014', materialCode: '控制箱', material: '控制箱', orderId: 107 },
    { orderNo: 'SO240303-015', materialCode: '标准泵', material: '标准泵', orderId: 108 }
  ]
  // 工序：接口只有 operationCode（无名称字段）⇒ mock 直接把中文工序名当代码，
  //   这样甘特条/左表「工序 | 订单号」读起来就是 焊接 | SO240301-021。
  const operations = [
    { code: '切割', seq: 10, name: '切割' },
    { code: '焊接', seq: 20, name: '焊接' },
    { code: '喷涂', seq: 30, name: '喷涂' },
    { code: '总装', seq: 40, name: '总装' },
    { code: '测试', seq: 50, name: '测试' }
  ]
  // 批次码（截图里形如 LOT-G6 / LOT-U20；i ≥ 批次数量时补 A/B 后缀保证任务号唯一）
  const lotCodes = ['G6', 'U20', 'M12', 'N13', 'L14', 'J11', 'T15', 'K10', 'H8', 'V21']
  let taskId = 1000
  for (let i = 0; i < 60; i++) {
    const order = orders[i % orders.length]
    const op = operations[i % operations.length]
    const resource = RESOURCES[i % RESOURCES.length]
    const startDays = (i % 14) - 3 // -3 ~ 10
    const status = pickStatus(i)
    // 开工时刻：06:00~22:00 每 2h 一档（多班制）
    //  - 2026-10-27 演示调整：原先固定 08:00~15:00，每天的前 1/3 天列恒为空，
    //    7 天视图看着很空；铺开到 6~22 点后条形能填满各天列。
    const startHour = 6 + (i % 9) * 2
    const plannedStart = dayOffset(startDays, startHour)
    // 时长 44~72h（约 2~3 天）：mock 是演示数据，条太短时「工序 | 订单号」写不全、
    //   视觉上也散 —— 2026-11-05 用户两次要求加长，按当前 7 天窗口取 2~3 天。
    const opDurations: Record<string, number[]> = {
      切割: [44, 48, 56, 44, 64],
      焊接: [56, 72, 48, 64, 72],
      喷涂: [44, 56, 48, 64, 48],
      总装: [64, 56, 72, 64, 60],
      测试: [44, 48, 56, 44, 64]
    }
    const opDur = opDurations[op.code] ?? [20, 28, 36]
    const durationH = opDur[i % opDur.length]
    const plannedEndHours = startHour + durationH
    const plannedEnd = dayOffset(startDays + Math.floor(plannedEndHours / 24), plannedEndHours % 24)
    // 已完成的任务不标延期（一根条上同时出现「✓ 和 +Nh」会自相矛盾）
    const isDelayed = status !== 'COMPLETED' && i % 7 === 0 // 与下方根因类型选择器 i % 5 解耦，确保 5 类根因都能展开
    const delayHours = isDelayed ? 1 + (i % 5) : undefined

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

    // 注：U06「数量-时间拆分」、Section 二.2「FactType」、Section 十一「上游 Stage 阻挡」、
    //   U14「跨 Domain 共享设备阻挡」四处 mock 造数已随展示位一并撤除
    //   （5号位 2026-11-05 B6/B7/B8「不做、撤」+ B4「不做」）——
    //   mock 必须与真实契约同形（这四项真实后端永久缺键），否则离线 mock 会造出线上不存在的数据。

    // Section 十一：根因（4号位文档第 11 节硬约束：必须优先展示根因，不能只丢 DUE_DATE_RISK）
    // 文档第 11 节原文示例：采购料9月3日才可用、MC01无产能、上游Stage完成晚、Firm任务占关键窗口
    const delayReasons: NonNullable<GanttTaskDto['delayReasons']> = []
    if (isDelayed) {
      if (i % 5 === 0) {
        delayReasons.push({
          reasonCode: 'RESOURCE_CAPACITY_WAIT',
          // mock 与真契约同形：这码在 dev 上就是订单级（5号位 11-05 Q2-a 放宽后实测 4/4 条 objectType=ORDER）
          objectType: 'ORDER',
          issueCategory: 'SCHEDULING_ISSUE',
          severity: 'WARN',
          description: `设备 ${resource.resourceCode} 产能不足，无法在本时段内完工`,
          relatedObjectRef: resource.resourceCode,
          impactHours: 4
        })
      } else if (i % 5 === 1) {
        delayReasons.push({
          reasonCode: 'MATERIAL_SHORTAGE',
          objectType: 'TASK',
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
        delayReasons.push({
          reasonCode: 'PRECEDENCE_WAIT',
          objectType: 'TASK',
          issueCategory: 'SCHEDULING_ISSUE',
          severity: 'WARN',
          description: `上游 STG-AS 完成晚，预计延误 6h 后才能开始本工序`,
          relatedObjectRef: 'STG-AS',
          impactHours: 6
        })
      } else if (i % 5 === 3) {
        delayReasons.push({
          reasonCode: 'FROZEN_ZONE_LOCK',
          objectType: 'TASK',
          issueCategory: 'SCHEDULING_ISSUE',
          severity: 'INFO',
          description: '关键窗口已被 Firm/FROZEN 任务占用，本任务只能后置',
          relatedObjectRef: 'FIRM-WINDOW',
          impactHours: 3
        })
      } else {
        delayReasons.push({
          reasonCode: 'EQUIPMENT_BREAKDOWN_RISK',
          objectType: 'TASK',
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
    } else if (i % 19 === 7) {
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

    // 批次后缀：i 超过批次码数量后补 A/B…，保证 60 条任务号互不重复
    const lot =
      lotCodes[i % lotCodes.length] +
      (i >= lotCodes.length ? String.fromCharCode(65 + Math.floor(i / lotCodes.length)) : '')

    list.push({
      taskId: taskId++,
      taskNo: `${op.name}-LOT-${lot}`,
      orderId: order.orderId,
      orderNo: order.orderNo,
      domainKey: resource.domainKey,
      materialId: 200 + (i % orders.length),
      materialCode: order.materialCode,
      materialName: order.material,
      resourceId: resource.resourceId,
      operationCode: op.code,
      operationSeq: op.seq,
      // 任务清单信息补全（5号位 2026-11-05 r14043/14044）：部门/阶段取该任务资源的部门，
      // 生产指示号少数缺键（真实覆盖 2904/2910），人工槽在设备任务上恒缺键（真实 0/2910）。
      productionDepartmentId: resource.productionDepartmentId,
      departmentName: resource.productionDepartmentName,
      stageCode: resource.stage,
      taskType: 'PRODUCTION',
      routeCode: 'DEFAULT',
      pathId: 1,
      manualSlotId: undefined,
      mtS_InstructionNo: i % 50 === 0 ? undefined : `PI-${String(240000 + i)}`,
      stageExecutionBatchKey: `SEB-${String(240000 + i)}`,
      stageExecutionBatchQty: plannedProcessQty,
      netQty,
      plannedProcessQty,
      quantity: plannedProcessQty,
      uom: 'PCS',
      plannedStartTime: plannedStart,
      plannedEndTime: plannedEnd,
      status,
      isDelayed,
      delayHours,
      lockMarker: i % 7 === 0 ? 'EXECUTION' : i % 11 === 0 ? 'FIRM' : undefined,
      taskShare,
      // MES 工单号 / 下发状态（5号位 2026-11-05 B2①/B2② 契约）：
      //   已建单且已下发的任务带这两个键；其余任务缺键（=未建单 / 未下发），与真实后端同形。
      mesWorkOrderNo: i % 5 === 0 ? `WO-${String(700000 + i)}` : undefined,
      dispatchStatus: i % 5 === 0 ? 'DISPATCHED' : undefined,
      mesEligible,
      mesIneligibleReasons: mesIneligibleReasons.length > 0 ? mesIneligibleReasons : undefined,
      delayReasons: delayReasons.length > 0 ? delayReasons : undefined
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
    // 数据截止 / 触发方式 / 触发来源（5号位 2026-11-05 B10 契约）：无来源 Run 的版本这三项缺键
    dataCutoffTime: hourOffset(-3),
    runType: 'FULL_SCHEDULE',
    triggeredBy: 'mock',
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
    dataCutoffTime: hourOffset(-27),
    runType: 'FULL_SCHEDULE',
    triggeredBy: 'mock',
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
    dataCutoffTime: hourOffset(-2),
    runType: 'MANUAL_RESCHEDULE',
    triggeredBy: 'mock',
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
