/**
 * APS V1 4号位 — 参数集（ParameterSet）维护 Pinia store
 *
 * ⚠️ 2026-10-22 新增：参数集从「规则与参数维护」页里独立出来。
 *   冻结模型里参数集是**独立主表**（DDL 08:9064 / 08:9081），与规则集平行，
 *   只在 StrategyProfileVersion 上会合 —— 原先放在规则集页里、并与规则集 1:1 绑定的模型不成立。
 *
 * 内容模型：
 *  - 一个参数集版本的内容 = 5 个主题 JSON（lock / supply / procurement / solverStrategy /
 *    candidateGuardrail），后端以**字符串**返回，前端反序列化成对象编辑、PUT 时序列化回去
 *  - 缓冲三态：originalBlocks（后端真值）/ workingBlocks（编辑态）/ dirty（逐块标记）
 *
 * 权限：aps.parameter.view / aps.parameter.edit / aps.parameter.publish
 *  （⚠️ 参数集发布是**独立权限**，`aps.rule.publish` 不覆盖它 —— 后端 Controller:228 挂 ParameterPublish）
 */

import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import {
  parameterSetApi,
  ApiError,
  type ParameterSetSummaryDto,
  type ParameterSetDetailDto,
  type VersionDiffResultDto,
  type RuleStatus,
  type RoleKey,
  type ParameterSetVersion,
  type PublishValidationResultDto,
  type BlockKey,
  type ParameterSetBlocks,
  type BackendParameterSetVersion,
  BLOCK_KEYS,
  NO_VERSION
} from '@/api/aps-v1'
import {
  flattenBlocks,
  applyEdit,
  removePath,
  buildPutBody,
  parseParameterSetBlocks,
  type ParameterRow,
  type JsonValue
} from '@/api/aps-v1/draftBuffer'
import { warehouseDictionaryApi, type WarehouseDictItem } from '@/api/aps-v1/warehouseDictionary'
import { lookupParameterEnum } from '@/api/aps-v1/parameterSetEnums'
import {
  diffParameterBlocks,
  pickDiffPair,
  type ParameterDiffRow
} from '@/api/aps-v1/parameterDiff'
import {
  readAnchorResourceCode,
  writeAnchorResourceCode,
  ensureAnchorResourceCodeKey,
  pruneAnchorOnlyBlock,
  isAnchorResourceCodeRow
} from '@/api/aps-v1/anchorResource'
import {
  SUPPLY_WAREHOUSE_PRIORITY_PATH,
  PROCUREMENT_WAREHOUSE_PRIORITY_PATH,
  DEFAULT_PURCHASE_LT_PATH,
  ARRIVAL_TO_USABLE_OFFSETS_PATH,
  isWarehouseRefRow,
  readStringArray,
  writeStringArray,
  readObjectArray,
  writeObjectArray,
  collectWarehouseRefs,
  unknownWarehouseCodes,
  type PurchaseLtItem,
  type ArrivalOffsetItem,
  type WarehouseRefGroups
} from '@/api/aps-v1/warehouseRefs'
import {
  readPlanningYields,
  writePlanningYields,
  isPlanningYieldRow,
  type PlanningYieldItem
} from '@/api/aps-v1/parameterSetYields'
import { mockActor } from '@/api/aps-v1/rule'
import { normalizeRuleStatus } from '@/api/aps-v1/types/common'
import { anchorHintMessage as buildAnchorHintMessage } from '@/api/aps-v1/validationMessages'
import { useApsAuthStore } from './auth'

/** 脏标记：5 块各自独立标记 */
type DirtyFlags = Record<BlockKey, boolean>

const emptyDirtyFlags = (): DirtyFlags => ({
  lock: false,
  supply: false,
  procurement: false,
  solverStrategy: false,
  candidateGuardrail: false
})

const emptyBufferState = () => ({
  governance: null as ParameterSetVersion | null,
  originalBlocks: null as ParameterSetBlocks | null,
  workingBlocks: null as ParameterSetBlocks | null,
  dirty: emptyDirtyFlags(),
  saving: false
})

export const useParameterSetsStore = defineStore('aps.parameterSets', () => {
  // ===== state =====
  const parameterSets = ref<ParameterSetSummaryDto[]>([])
  const currentDetail = ref<ParameterSetDetailDto | null>(null)
  const currentDiff = ref<VersionDiffResultDto | null>(null)
  /**
   * 参数级差异（前端逐参数比对两个版本的内容）
   *
   * 后端 diff 的粒度是**块级**（5 个主题块各塞一整块 JSON），用户看不出「改了哪个参数」；
   * 这里用同一套扁平化算法逐参数比，页面「版本对比」Tab 的主表用它。
   */
  const paramDiffRows = ref<ParameterDiffRow[]>([])
  /** 本次对比的两个版本（源 → 目标） */
  const paramDiffPair = ref<{
    sourceCode: string
    targetCode: string
    sourceStatus: RuleStatus
    targetStatus: RuleStatus
  } | null>(null)
  const lastValidation = ref<PublishValidationResultDto | null>(null)
  const statusFilter = ref<RuleStatus | typeof NO_VERSION | 'ALL'>('ALL')
  /** 是否显示「主表停用」的行（默认不显示；停用行是唯一的重新启用入口，故必须留开关） */
  const showInactive = ref(false)
  const loading = ref(false)
  const listLoading = ref(false)
  const actionRunning = ref(false)
  const validating = ref(false)
  const error = ref<string | null>(null)

  /**
   * 仓库字典（主数据）
   *
   * 来源：`GET /api/governance-query/warehouses`（类级 `aps.plan.view`）
   * —— 3号位 2026-10-25 §三 定案=方案 A（端点不动，给参数集维护账号补绑该码）。
   * ⇒ 拉不到（403/网络）时**如实提示、不静默吞成空字典**，页面降级为「只能看已有编码」。
   */
  const warehouseDict = ref<WarehouseDictItem[]>([])
  const warehouseDictLoaded = ref(false)
  const warehouseDictError = ref<string | null>(null)

  /** 5 块缓冲（编辑态） */
  const buffer = ref(emptyBufferState())

  const actor = ref(mockActor.name)
  const apsAuth = useApsAuthStore()
  const actorRoles = computed<RoleKey[]>(() =>
    apsAuth.roles.length > 0 ? apsAuth.roles : mockActor.roles
  )

  // ===== getters =====
  const filteredParameterSets = computed(() => {
    let arr = parameterSets.value
    if (!showInactive.value) arr = arr.filter((p) => p.isActive !== false)
    if (statusFilter.value === NO_VERSION) {
      arr = arr.filter((p) => !p.hasVersion)
    } else if (statusFilter.value !== 'ALL') {
      // 版本状态只对**有版本**的行成立：无版本的行后端 status 缺失，
      // 前端为了列表展示兜底成 DRAFT ⇒ 若不排除，点「草稿」会捞出几百个根本没版本的行。
      arr = arr.filter((p) => p.hasVersion && p.status === statusFilter.value)
    }
    return arr
  })

  /** 版本状态分布（顶部 KPI）—— 只数**有版本**的行
   *
   *  无版本行后端 status 缺失、前端兜底成 DRAFT，计进去会让「草稿」虚高
   *  （dev 实测 2026-11-07：真草稿 3 个，页面曾显示 886）。
   */
  const statusCounts = computed(() => {
    const c: Record<RuleStatus, number> = {
      DRAFT: 0,
      SUBMITTED: 0,
      APPROVED: 0,
      PUBLISHED: 0,
      DISABLED: 0,
      ARCHIVED: 0
    }
    parameterSets.value.forEach((p) => {
      if (!p.hasVersion) return
      c[p.status] = (c[p.status] ?? 0) + 1
    })
    return c
  })

  const noVersionCount = computed(() => parameterSets.value.filter((p) => !p.hasVersion).length)

  /** 主表停用的行数（「显示已停用」开关的计数标签） */
  const inactiveCount = computed(
    () => parameterSets.value.filter((p) => p.isActive === false).length
  )

  const currentStatus = computed(() => currentDetail.value?.summary.status ?? null)

  /** 是否有写权限 —— aps.parameter.edit */
  const canWrite = computed(() => apsAuth.has('aps.parameter.edit'))

  /** 是否有发布权限 —— aps.parameter.publish（独立权限码，不随规则发布权获得） */
  const canPublishAction = computed(() => apsAuth.has('aps.parameter.publish'))

  const actions = computed(() => {
    const s = currentStatus.value
    const edit = apsAuth.has('aps.parameter.edit')
    const pub = apsAuth.has('aps.parameter.publish')
    // 「停用」的对象是**已发布版本**，不是参数集整体状态：
    // 后端在有草稿时把 status 报成 DRAFT，若拿 s 当判据，一旦 fork 出草稿
    // 就再也停不掉已发布版本（页面无删草稿入口 ⇒ 死路）。
    const hasPublished = (currentDetail.value?.history ?? []).some((h) => h.status === 'PUBLISHED')
    return {
      canCreateDraft: edit,
      /** 编辑/保存：仅 DRAFT */
      canEdit: s === 'DRAFT' && edit,
      canValidate: s === 'DRAFT' && pub,
      canPublish: s === 'DRAFT' && pub,
      canDisable: hasPublished && pub
    }
  })

  /** 当前缓冲是否脏 */
  const dirty = computed(() => BLOCK_KEYS.some((b) => buffer.value.dirty[b]))

  /** 扁平化参数行（表格渲染用） */
  const flattenedRows = computed<ParameterRow[]>(() => {
    const wb = buffer.value.workingBlocks
    if (!wb) return []
    return flattenBlocks(wb)
  })

  /**
   * 按块分组的扁平行
   *
   * ⚠️ 四处「仓库引用」落点被**剔除**出通用表：它们不是标量，有专属编辑器
   *   （有序码列表 / 对象数组），通用行的单值控件表达不了。
   *   剔除清单与后端校验采集段一一对应（`GovernanceVersionService.cs:1019-1034`）。
   * ⚠️ 「锚点资源编码」也剔除：它有专属输入框（中文标签 + 说明），
   *   留在通用表里会多出一行裸英文键名，同一份数据显示两处。
   * ⚠️ 「采购良率」（Procurement.PlanningYields）也剔除：它是对象数组，
   *   通用单值控件表达不了，有专属编辑器（物料 / 工序 / 良率）。
   */
  const rowsGrouped = computed<Record<BlockKey, ParameterRow[]>>(() => {
    const grouped: Record<BlockKey, ParameterRow[]> = {
      lock: [],
      supply: [],
      procurement: [],
      solverStrategy: [],
      candidateGuardrail: []
    }
    flattenedRows.value.forEach((row) => {
      // 维护责任分类 C/E/F/H ⇒ 维护面隐藏（值仍在快照里，保存原样回写）
      if (row.hidden) return
      if (isWarehouseRefRow(row.block, row.path)) return
      if (isPlanningYieldRow(row.block, row.path)) return
      if (isAnchorResourceCodeRow(row.block, row.path)) return
      grouped[row.block].push(row)
    })
    return grouped
  })

  /** 仓库字典里的编码集合（大小写不敏感，与后端 `OrdinalIgnoreCase` 对齐） */
  const warehouseDictCodes = computed<string[]>(() =>
    warehouseDict.value.map((w) => String(w.code ?? '').trim()).filter(Boolean)
  )

  /** 仓库编码 → 名称（回显用；字典里没有的编码查不到 ⇒ 显示编码本身） */
  const warehouseNameMap = computed<Map<string, string>>(() => {
    const m = new Map<string, string>()
    for (const w of warehouseDict.value) {
      const code = String(w.code ?? '').trim()
      if (code) m.set(code.toLowerCase(), String(w.name ?? '').trim())
    }
    return m
  })

  /** ① 供应参数 → 仓库优先级（有序码列表） */
  const supplyWarehousePriority = computed<string[]>(() =>
    readStringArray(buffer.value.workingBlocks, 'supply', SUPPLY_WAREHOUSE_PRIORITY_PATH)
  )
  /** ② 采购参数 → 仓库优先级（有序码列表） */
  const procurementWarehousePriority = computed<string[]>(() =>
    readStringArray(buffer.value.workingBlocks, 'procurement', PROCUREMENT_WAREHOUSE_PRIORITY_PATH)
  )
  /** ③ 采购参数 → 默认采购提前期 */
  const defaultPurchaseLt = computed<PurchaseLtItem[]>(() =>
    readObjectArray<PurchaseLtItem>(
      buffer.value.workingBlocks,
      'procurement',
      DEFAULT_PURCHASE_LT_PATH
    )
  )
  /** ④ 采购参数 → 到货可用偏移 */
  const arrivalToUsableOffsets = computed<ArrivalOffsetItem[]>(() =>
    readObjectArray<ArrivalOffsetItem>(
      buffer.value.workingBlocks,
      'procurement',
      ARRIVAL_TO_USABLE_OFFSETS_PATH
    )
  )
  /** ⑤ 采购参数 → 计划良率（专属编辑器用） */
  const planningYields = computed<PlanningYieldItem[]>(() =>
    readPlanningYields(buffer.value.workingBlocks)
  )

  /**
   * 非阻断提示：选了非自动锚点方式、却没填锚点资源编码 ⇒ 运行时按「自动判定」处理。
   * 判定与文案都在 `validationMessages.ts` 的纯函数里（可单测），此处只取编辑态缓冲喂给它。
   */
  const anchorHintMessage = computed<string | null>(() =>
    buildAnchorHintMessage(buffer.value.workingBlocks?.solverStrategy)
  )

  /** 锚点资源编码当前值（自由文本；3号位 10-25 定调方案 (a)） */
  const anchorResourceCode = computed<string>(() =>
    readAnchorResourceCode(buffer.value.workingBlocks?.solverStrategy)
  )

  /**
   * 「求解器策略」块是否已有内容。
   *
   * ⚠️ 空块不呈现锚点录入项：空块意味着「求解器策略还没配」，此时填进去的锚点编码
   *   会被后端反序列化成默认 Mode/BottleneckMode（枚举 0 号）⇒ 锚点根本不生效，
   *   而块却从「发不出去」变成「发得出去」⇒ 前端替用户改了发布语义。
   */
  const solverStrategyHasContent = computed<boolean>(() => {
    const ss = buffer.value.workingBlocks?.solverStrategy
    return !!ss && Object.keys(ss).length > 0
  })

  // ===== actions =====
  async function loadList(): Promise<void> {
    listLoading.value = true
    error.value = null
    try {
      parameterSets.value = await parameterSetApi.listParameterSets()
    } catch (err) {
      handleError(err)
    } finally {
      listLoading.value = false
    }
  }

  async function loadDetail(parameterSetId: number): Promise<void> {
    loading.value = true
    error.value = null
    currentDiff.value = null
    lastValidation.value = null
    buffer.value = emptyBufferState()
    try {
      const detail = await parameterSetApi.getParameterSet(parameterSetId)
      const row = parameterSets.value.find((p) => p.parameterSetId === parameterSetId)
      detail.summary.parameterSetCode = row?.parameterSetCode ?? detail.summary.parameterSetCode
      detail.summary.parameterSetName = row?.parameterSetName ?? detail.summary.parameterSetName
      detail.summary.description = row?.description
      detail.summary.lastChangeReason = row?.lastChangeReason
      // 主表启用位只存在于列表行（详情聚合按版本拼装，默认 true）
      detail.summary.isActive = row?.isActive ?? detail.summary.isActive
      currentDetail.value = detail

      if (row) {
        const idx = parameterSets.value.findIndex((p) => p.parameterSetId === parameterSetId)
        if (idx >= 0) {
          parameterSets.value[idx] = {
            ...parameterSets.value[idx],
            status: detail.summary.status,
            currentVersionCode: detail.summary.currentVersionCode,
            draftVersionCode: detail.summary.draftVersionCode,
            publishedAt: detail.summary.publishedAt
          }
        }
      }

      // 载入 5 块缓冲（编辑对象 = 后端真值的深拷贝）
      if (detail.version) {
        const blocks = parseParameterSetBlocks(detail.version)
        // 锚点资源编码补键（3号位 10-25 定调：后端零改动，落点在前端写侧带键）
        //  放在深拷贝**之前** ⇒ originalBlocks / workingBlocks 同时带键 ⇒「取消未保存」后
        //  录入项仍在，不会出现「一取消这行就消失」。
        ensureAnchorResourceCodeKey(blocks)
        buffer.value = {
          governance: detail.version,
          originalBlocks: JSON.parse(JSON.stringify(blocks)),
          workingBlocks: blocks,
          dirty: emptyDirtyFlags(),
          saving: false
        }
      }

      // 版本对比：对比对由 `pickDiffPair` 统一选取
      //  ① 有草稿 ⇒ 当前已发布 ↔ 草稿（回答「我这次改了什么」）
      //  ② 无草稿 ⇒ 上一个已发布 ↔ 当前已发布（回答「上一版到这一版改了什么」）
      await refreshDiff()
    } catch (err) {
      handleError(err)
      currentDetail.value = null
    } finally {
      loading.value = false
    }
  }

  /**
   * 新建参数集主表，并跟建一条空白草稿（`<编码>-V1`）。
   *
   * 两步口径沿用 10-22 规则集裁决 Q2（先建壳、前端随后自动跟建空白 DRAFT）；
   * 空白草稿合法：`EnsureParameterSetNormalized` 在五主题全空时直接返回，
   * 发布前校验会报 EMPTY 直到用户填内容，与规则集新壳一致。
   *
   * 写接口报错不等于没写成：失败时回列表核对同编码 —— 防「其实已建成、只是响应报错」把用户卡在
   * 「列表看不到 + 重试又说编码已存在」的死路（规则集 10-25 已踩过）。
   */
  async function createParameterSet(input: {
    parameterSetCode: string
    parameterSetName: string
    description?: string
  }): Promise<number | null> {
    if (!canWrite.value) {
      error.value = '当前操作者无写权限（需权限码 aps.parameter.edit）'
      return null
    }
    const code = input.parameterSetCode.trim()
    const name = input.parameterSetName.trim()
    if (!code || !name) {
      error.value = '参数集编码和名称都必须填写'
      return null
    }
    if (
      parameterSets.value.some(
        (p) => p.parameterSetCode?.trim().toLowerCase() === code.toLowerCase()
      )
    ) {
      error.value = `参数集编码已存在：${code}`
      return null
    }
    actionRunning.value = true
    error.value = null
    try {
      const created = await parameterSetApi.createParameterSet({
        parameterSetCode: code,
        parameterSetName: name,
        description: input.description?.trim() || undefined
      })
      return await attachBlankDraft(created.id, created.parameterSetCode || code)
    } catch (err) {
      await loadList()
      const hit = parameterSets.value.find(
        (p) => p.parameterSetCode?.trim().toLowerCase() === code.toLowerCase()
      )
      if (hit) {
        console.warn('[参数集] 新建接口报错，但回列表发现同编码参数集已存在，按已建成继续', err)
        return await attachBlankDraft(hit.parameterSetId, hit.parameterSetCode)
      }
      handleError(err)
      const httpStatus = err instanceof ApiError ? err.code : undefined
      error.value =
        `新建没成功${httpStatus ? `（服务器返回 ${httpStatus}）` : ''}。` +
        '也可能是「其实已经建好了，只是返回时出错」——请刷新一下列表，看这个编码在不在。'
      return null
    } finally {
      actionRunning.value = false
    }
  }

  /**
   * 更新参数集主表（PUT /parameter-set/{id}）：名称 / 说明 / 启停。
   *
   * 编码原样回传（后端拒绝变更）；停用只关主表，不级联已有版本状态。
   * 写接口报错不等于没写成：失败时回列表核对真值，已是新值就按成功继续。
   */
  async function updateParameterSet(input: {
    parameterSetId: number
    parameterSetCode: string
    parameterSetName: string
    description?: string
    isActive: boolean
  }): Promise<boolean> {
    if (!canWrite.value) {
      error.value = '当前操作者无写权限（需权限码 aps.parameter.edit）'
      return false
    }
    const name = input.parameterSetName.trim()
    if (!name) {
      error.value = '参数集名称必须填写'
      return false
    }
    actionRunning.value = true
    error.value = null
    try {
      await parameterSetApi.updateParameterSet({
        id: input.parameterSetId,
        parameterSetCode: input.parameterSetCode,
        parameterSetName: name,
        description: input.description?.trim() || undefined,
        isActive: input.isActive
      })
      await loadList()
      if (currentDetail.value?.summary.parameterSetId === input.parameterSetId) {
        await loadDetail(input.parameterSetId)
      }
      return true
    } catch (err) {
      await loadList()
      const row = parameterSets.value.find((p) => p.parameterSetId === input.parameterSetId)
      const saved =
        row != null && row.parameterSetName === name && (row.isActive !== false) === input.isActive
      if (saved) {
        console.warn('[参数集] 保存接口报错，但回列表核对已是新值，按已保存继续', err)
        return true
      }
      handleError(err)
      const httpStatus = err instanceof ApiError ? err.code : undefined
      error.value =
        `保存没成功${httpStatus ? `（服务器返回 ${httpStatus}）` : ''}。` +
        '请刷新列表核对名称/启用状态是否已生效。'
      return false
    } finally {
      actionRunning.value = false
    }
  }

  /** 主表建成后补一条空白草稿；失败只如实提示「壳建好了、版本没建上」，不吞成功 */
  async function attachBlankDraft(parameterSetId: number, code: string): Promise<number> {
    try {
      await parameterSetApi.createParameterSetDraft({
        parameterSetId,
        versionCode: `${code}-V1`,
        remarks: '新建参数集时自动创建的空白草稿'
      })
    } catch (err) {
      const versions = await parameterSetApi
        .listParameterSetVersions(parameterSetId)
        .catch((): BackendParameterSetVersion[] => [])
      const hasDraft = versions.some((v) => normalizeRuleStatus(v.status) === 'DRAFT')
      await loadList()
      if (!hasDraft) {
        handleError(err)
        error.value = `参数集已创建，但空白草稿版本创建失败：${error.value}`
      }
      return parameterSetId
    }
    await loadList()
    await loadDetail(parameterSetId)
    return parameterSetId
  }

  async function loadDiff(base: number, target: number): Promise<void> {
    try {
      currentDiff.value = await parameterSetApi.diffVersions(base, target)
    } catch (err) {
      handleError(err)
      currentDiff.value = null
    }
  }

  /**
   * 刷新版本对比（两个来源并行取，谁先成算谁的）
   *
   *  - 后端 diff ⇒ **版本元信息**（版本码 / 状态 / 生效窗 / 发布时间 / 发布人 / 备注）
   *  - 前端 diff ⇒ **参数内容**（逐参数；后端只有块级 JSON，看不出改了哪个参数）
   *
   * 两边互不依赖：后端 diff 挂了就只少一张元信息表，参数差异照常显示。
   */
  async function refreshDiff(): Promise<void> {
    paramDiffRows.value = []
    paramDiffPair.value = null
    currentDiff.value = null
    const history = currentDetail.value?.history ?? []
    const pair = pickDiffPair(history)
    if (!pair) return
    const sourceId = pair.source.versionId
    const targetId = pair.target.versionId
    if (!sourceId || !targetId || sourceId === targetId) return

    // 元信息对比（失败不阻断参数级对比）
    try {
      currentDiff.value = await parameterSetApi.diffVersions(sourceId, targetId)
    } catch (err) {
      console.warn('[参数集] 版本元信息对比失败（不影响参数级差异）', err)
    }

    // 参数内容对比（逐参数）
    try {
      const [src, tgt] = await Promise.all([
        parameterSetApi.getParameterSetVersion(sourceId),
        parameterSetApi.getParameterSetVersion(targetId)
      ])
      paramDiffRows.value = diffParameterBlocks(
        parseParameterSetBlocks(src),
        parseParameterSetBlocks(tgt)
      )
      paramDiffPair.value = {
        sourceCode: pair.source.versionCode,
        targetCode: pair.target.versionCode,
        sourceStatus: pair.source.status,
        targetStatus: pair.target.status
      }
    } catch (err) {
      handleError(err)
    }
  }

  /** 编辑一个参数值（只改内存，不落库） */
  function onCellEdit(row: ParameterRow, newValue: JsonValue): void {
    if (!buffer.value.workingBlocks || !buffer.value.governance) {
      error.value = '参数集草稿未载入'
      return
    }
    try {
      applyEdit(buffer.value.workingBlocks, row, newValue)
      buffer.value.dirty[row.block] = true
    } catch (err) {
      handleError(err)
    }
  }

  /**
   * 空块加第一个字段（3号位 2026-10-25 §C-AN-1/§C-AN-3 拍板）
   *
   *  - 只写用户**显式选中**的那一个路径；不预填默认枚举 / 空结构 / 约定键名
   *    ⇒ 空块在用户完成首个真实输入前保持空，仍被后端 `EMPTY_*` 拦（发布语义不被前端改写）
   *  - 走**既有**的版本草稿 PUT（`PUT /api/governance/parameter-set/version/{id}`），
   *    本动作只改编辑态缓冲，**不调任何接口** ⇒ 零新端点
   *  - 枚举字段只接受后端枚举序号（`applyEdit` 会校验），写字符串会被后端判 INVALID_JSON
   */
  function addBlockField(block: BlockKey, path: string[], value: JsonValue): boolean {
    if (!buffer.value.workingBlocks || !buffer.value.governance) {
      error.value = '参数集草稿未载入'
      return false
    }
    if (!canWrite.value) {
      error.value = '当前操作者无写权限（需权限码 aps.parameter.edit）'
      return false
    }
    try {
      applyEdit(
        buffer.value.workingBlocks,
        {
          block,
          path,
          value: null,
          type: 'STRING',
          options: lookupParameterEnum(block, path),
          sensitive: false,
          editable: true
        },
        value
      )
      buffer.value.dirty[block] = true
      return true
    } catch (err) {
      handleError(err)
      return false
    }
  }

  /**
   * 删除块 JSON 里的一个字段（把键拿掉）。
   *
   * 口径：
   *  - 语义 = **回到后端 DTO 默认值**（键不在 JSON 里 ⇒ 后端反序列化取默认值）
   *  - 删完父对象空了 ⇒ 父对象一并删掉；块根删空 ⇒ 块保持 `{}` ⇒ PUT 不带该块
   *    ⇒ 仍被 `EMPTY_*` 拦（「没配」不会被悄悄变成「按默认配」）
   *  - 数组行（editable=false）不走这里（页面不给删除入口）
   */
  function removeBlockField(block: BlockKey, path: string[]): boolean {
    if (!buffer.value.workingBlocks || !buffer.value.governance) {
      error.value = '参数集草稿未载入'
      return false
    }
    if (!canWrite.value) {
      error.value = '当前操作者无写权限（需权限码 aps.parameter.edit）'
      return false
    }
    try {
      removePath(buffer.value.workingBlocks, block, path)
      // 块里只剩锚点编码空值 ⇒ 视同空块（否则会绕过 EMPTY_*，等于替用户按默认配）
      pruneAnchorOnlyBlock(buffer.value.workingBlocks, block)
      buffer.value.dirty[block] = true
      return true
    } catch (err) {
      handleError(err)
      return false
    }
  }

  /* ===== 锚点资源编码（专属输入项；3号位 2026-10-25 定调：自由文本，后端零改动）===== */

  /**
   * 写锚点资源编码（自由文本）。
   *
   * 口径：
   *  - 写 `workingBlocks.solverStrategy.AnchorResourceCode`（trim 后），并置脏
   *  - 值没变 ⇒ 不置脏（避免「点了一下又不动」把草稿标成已修改）
   *  - 不校验编码是否存在：后端发布校验不查这个字段的取值域（3号位 §五 验收 #4），
   *    无效编码在运行期 no-op ≡ Auto ⇒ 前端也不拦
   */
  function setAnchorResourceCode(code: string): void {
    if (!buffer.value.workingBlocks || !buffer.value.governance) {
      error.value = '参数集草稿未载入'
      return
    }
    if (!canWrite.value) {
      error.value = '当前操作者无写权限（需权限码 aps.parameter.edit）'
      return
    }
    const ss = buffer.value.workingBlocks.solverStrategy
    const next = String(code ?? '').trim()
    if (readAnchorResourceCode(ss) === next) return
    writeAnchorResourceCode(ss, next)
    buffer.value.dirty.solverStrategy = true
  }

  /* ===== 仓库引用四处落点（专属编辑器用；3号位 2026-10-25 §四）===== */

  /** 拉仓库字典（一次拉全量，页面本地过滤；语义与端点 search 一致：编码或名称） */
  async function loadWarehouseDict(force = false): Promise<void> {
    if (warehouseDictLoaded.value && !force) return
    try {
      const list = await warehouseDictionaryApi.listWarehouses()
      warehouseDict.value = list
      warehouseDictLoaded.value = true
      warehouseDictError.value = null
    } catch (err) {
      warehouseDict.value = []
      warehouseDictLoaded.value = false
      warehouseDictError.value =
        err instanceof ApiError && err.code === 403
          ? '当前账号没有读取仓库字典的权限（需权限码 aps.plan.view），暂时只能看已配置的编码、不能从字典里选。'
          : `仓库字典没拉下来：${err instanceof ApiError ? err.message : String(err)}`
    }
  }

  /** 写仓库优先级的公共实现（只改内存 + 置脏） */
  function writeWarehouseArray(block: BlockKey, path: readonly string[], values: string[]): void {
    if (!buffer.value.workingBlocks || !buffer.value.governance) {
      error.value = '参数集草稿未载入'
      return
    }
    if (!canWrite.value) {
      error.value = '当前操作者无写权限（需权限码 aps.parameter.edit）'
      return
    }
    writeStringArray(buffer.value.workingBlocks, block, path, values)
    buffer.value.dirty[block] = true
  }

  /** ① 供应参数 → 库存可用 → 仓库优先级（有序，顺序即优先级） */
  function setSupplyWarehousePriority(codes: string[]): void {
    writeWarehouseArray('supply', SUPPLY_WAREHOUSE_PRIORITY_PATH, codes)
  }

  /** ② 采购参数 → 仓库优先级（有序，顺序即优先级） */
  function setProcurementWarehousePriority(codes: string[]): void {
    writeWarehouseArray('procurement', PROCUREMENT_WAREHOUSE_PRIORITY_PATH, codes)
  }

  /** ③ 采购参数 → 默认采购提前期 */
  function setDefaultPurchaseLt(items: PurchaseLtItem[]): void {
    if (!buffer.value.workingBlocks || !buffer.value.governance) {
      error.value = '参数集草稿未载入'
      return
    }
    if (!canWrite.value) {
      error.value = '当前操作者无写权限（需权限码 aps.parameter.edit）'
      return
    }
    writeObjectArray(buffer.value.workingBlocks, 'procurement', DEFAULT_PURCHASE_LT_PATH, items)
    buffer.value.dirty.procurement = true
  }

  /** ④ 采购参数 → 到货可用偏移 */
  function setArrivalToUsableOffsets(items: ArrivalOffsetItem[]): void {
    if (!buffer.value.workingBlocks || !buffer.value.governance) {
      error.value = '参数集草稿未载入'
      return
    }
    if (!canWrite.value) {
      error.value = '当前操作者无写权限（需权限码 aps.parameter.edit）'
      return
    }
    writeObjectArray(
      buffer.value.workingBlocks,
      'procurement',
      ARRIVAL_TO_USABLE_OFFSETS_PATH,
      items
    )
    buffer.value.dirty.procurement = true
  }

  /** ⑤ 采购参数 → 计划良率（专属编辑器写入口；零新端点，仍走版本草稿 PUT） */
  function setPlanningYields(items: PlanningYieldItem[]): void {
    if (!buffer.value.workingBlocks || !buffer.value.governance) {
      error.value = '参数集草稿未载入'
      return
    }
    if (!canWrite.value) {
      error.value = '当前操作者无写权限（需权限码 aps.parameter.edit）'
      return
    }
    writePlanningYields(buffer.value.workingBlocks, items)
    buffer.value.dirty.procurement = true
  }

  /** 当前编辑态里引用到的仓库编码（按 Supply / Procurement 分组） */
  const currentWarehouseRefs = computed<WarehouseRefGroups>(() =>
    collectWarehouseRefs(buffer.value.workingBlocks)
  )

  /** 当前编辑态里字典中不存在的编码（发布校验会报 INVALID_WAREHOUSE_REF） */
  const currentUnknownWarehouseCodes = computed<string[]>(() =>
    unknownWarehouseCodes(
      [...currentWarehouseRefs.value.supply, ...currentWarehouseRefs.value.procurement],
      warehouseDictCodes.value
    )
  )

  /* ===== fork 前置预检（源版本里引用了字典中不存在的仓库编码）===== */

  const forkSourceRefs = ref<WarehouseRefGroups | null>(null)
  const forkSourceUnknownCodes = ref<string[]>([])

  /**
   * 预检 fork 源版本里的仓库引用
   *
   * fork = GET 源版本 → POST 新版本（5 主题原样带走），所以**源版本里坏掉的仓库编码会被原样继承**，
   * 新草稿发布时才报 INVALID_WAREHOUSE_REF ⇒ 提前在 fork 对话框里说清（3号位 §四）。
   */
  async function checkForkSourceWarehouseRefs(sourceVersionId: number): Promise<void> {
    forkSourceRefs.value = null
    forkSourceUnknownCodes.value = []
    if (!sourceVersionId) return
    try {
      const src = await parameterSetApi.getParameterSetVersion(sourceVersionId)
      const blocks = parseParameterSetBlocks(src)
      const groups = collectWarehouseRefs(blocks)
      forkSourceRefs.value = groups
      forkSourceUnknownCodes.value = unknownWarehouseCodes(
        [...groups.supply, ...groups.procurement],
        warehouseDictCodes.value
      )
    } catch (err) {
      // 预检失败**不阻断** fork（预检只是提示，不是校验闸门）
      console.warn('[参数集] fork 预检：读取源版本失败，跳过仓库编码提示', err)
    }
  }

  /** 放弃未保存修改 */
  function onCancelDirty(): void {
    if (buffer.value.originalBlocks) {
      buffer.value.workingBlocks = JSON.parse(JSON.stringify(buffer.value.originalBlocks))
    }
    buffer.value.dirty = emptyDirtyFlags()
  }

  /**
   * 保存草稿（PUT /api/governance/parameter-set/version/{id}）
   *  - full-object PUT：5 个主题 JSON 必须全量回传（后端五主题归一化会重建快照）
   *  - 后端 409（OCC 冲突）→ 提示用户刷新，**不丢**未保存状态
   */
  async function onSaveDraft(changeReason: string): Promise<boolean> {
    const buf = buffer.value
    if (!buf.governance || !buf.workingBlocks) {
      error.value = '参数集草稿未载入'
      return false
    }
    if (!changeReason.trim()) {
      error.value = '请填写变更原因'
      return false
    }
    const versionId = buf.governance.id
    if (!versionId) {
      error.value = '版本标识缺失，无法保存草稿'
      return false
    }
    buf.saving = true
    error.value = null
    try {
      const putBody = buildPutBody(buf.governance, buf.workingBlocks)
      putBody.remarks = changeReason
      const result = await parameterSetApi.updateParameterSetDraft({
        versionId,
        body: putBody as ParameterSetVersion
      })
      buf.governance = result
      buf.originalBlocks = JSON.parse(JSON.stringify(buf.workingBlocks))
      buf.dirty = emptyDirtyFlags()
      // 草稿内容已落库 ⇒ 版本对比要按新内容重算（否则「我这次改了什么」还是旧值）
      await refreshDiff()
      return true
    } catch (err) {
      if (err instanceof ApiError && err.code === 409) {
        error.value = `版本已被他人修改（冲突）：${err.message}。请刷新页面后再编辑。`
      } else {
        handleError(err)
      }
      return false
    } finally {
      buf.saving = false
    }
  }

  /** 校验草稿（后端返回 PublishValidationResult：isValid / errors / warnings） */
  async function validateDraft(): Promise<boolean> {
    validating.value = true
    error.value = null
    try {
      const versionId = (currentDetail.value?.history ?? [])
        .filter((h) => h.status === 'DRAFT')
        .pop()?.versionId
      if (!versionId) {
        error.value = '当前没有草稿版本可校验'
        return false
      }
      const result = await parameterSetApi.validateDraft(versionId)
      lastValidation.value = result
      return result.isValid
    } catch (err) {
      handleError(err)
      return false
    } finally {
      validating.value = false
    }
  }

  /** 发布（需 aps.parameter.publish）；后端返回一句文本，发布后重新拉取读真状态 */
  async function publish(changeReason: string): Promise<boolean> {
    if (!canPublishAction.value) {
      error.value = '当前操作者无参数集发布权限（需权限码 aps.parameter.publish）'
      return false
    }
    if (dirty.value) {
      error.value = '存在未保存的修改，请先保存草稿再发布'
      return false
    }
    const versionId = (currentDetail.value?.history ?? [])
      .filter((h) => h.status === 'DRAFT')
      .pop()?.versionId
    if (!versionId) {
      error.value = '当前没有草稿版本可发布'
      return false
    }
    actionRunning.value = true
    error.value = null
    try {
      await parameterSetApi.publishParameterSet({ versionId, changeReason, actor: actor.value })
      const id = currentDetail.value?.summary.parameterSetId
      await loadList()
      if (id) await loadDetail(id)
      return true
    } catch (err) {
      handleError(err)
      return false
    } finally {
      actionRunning.value = false
    }
  }

  /** 停用（后端 disable 挂 aps.parameter.edit；页面按「停用 = 发布动作」口径要发布权） */
  async function disable(reason: string): Promise<boolean> {
    if (!canPublishAction.value) {
      error.value = '当前操作者无停用权限（需权限码 aps.parameter.publish）'
      return false
    }
    const versionId = (currentDetail.value?.history ?? [])
      .filter((h) => h.status === 'PUBLISHED')
      .pop()?.versionId
    if (!versionId) {
      error.value = '当前没有已发布版本可停用'
      return false
    }
    actionRunning.value = true
    error.value = null
    try {
      await parameterSetApi.disableParameterSet({ versionId, reason })
      const id = currentDetail.value?.summary.parameterSetId
      await loadList()
      if (id) await loadDetail(id)
      return true
    } catch (err) {
      handleError(err)
      return false
    } finally {
      actionRunning.value = false
    }
  }

  /**
   * 物理删除参数集整族（DELETE /parameter-set/{id}，权限 aps.parameter.edit）
   *
   *  最窄口径：无任何已发布版本才可硬删（连带删草稿）；含 PUBLISHED、或被策略包版本引用，
   *  后端 400 并给原文（前端不做二次翻译，也不预判引用关系）。
   *
   *  ⚠️ 写接口报错 ≠ 没写成：失败后回列表核对，行已不在即按已删除继续。
   */
  async function removeParameterSet(parameterSetId: number): Promise<boolean> {
    if (!canWrite.value) {
      error.value = '当前操作者无删除权限（需权限码 aps.parameter.edit）'
      return false
    }
    actionRunning.value = true
    error.value = null
    let failedMsg: string | null = null
    try {
      await parameterSetApi.deleteParameterSet(parameterSetId)
    } catch (err) {
      failedMsg = (err as Error)?.message ?? '删除请求失败'
    }
    try {
      await loadList()
    } catch {
      /* loadList 内部已 handleError */
    }
    const stillThere = parameterSets.value.some((p) => p.parameterSetId === parameterSetId)
    actionRunning.value = false
    if (stillThere) {
      if (failedMsg) handleError(new Error(failedMsg))
      return false
    }
    if (currentDetail.value?.summary.parameterSetId === parameterSetId) {
      currentDetail.value = null
    }
    return true
  }

  /**
   * 新建草稿（fork）
   *  只 fork **参数集**（5 个主题 JSON 原样带走）。
   *  后端无 fork 端点 ⇒ GET 源版本 → POST 新版本。
   */
  async function forkDraft(
    sourceParameterSetVersionId: number,
    newVersionCode: string,
    changeReason?: string
  ): Promise<boolean> {
    if (!canWrite.value) {
      error.value = '当前操作者无写权限（需权限码 aps.parameter.edit）'
      return false
    }
    if (!sourceParameterSetVersionId) {
      error.value = '找不到可作为起点的版本'
      return false
    }
    actionRunning.value = true
    error.value = null
    try {
      await parameterSetApi.forkDraft(sourceParameterSetVersionId, newVersionCode, changeReason)
      await loadList()
      const id = currentDetail.value?.summary.parameterSetId
      if (id) await loadDetail(id)
      return true
    } catch (err) {
      handleError(err)
      return false
    } finally {
      actionRunning.value = false
    }
  }

  function setStatusFilter(filter: RuleStatus | typeof NO_VERSION | 'ALL'): void {
    statusFilter.value = filter
  }

  function handleError(err: unknown): void {
    if (err instanceof ApiError) {
      error.value =
        `[${err.code}] ${err.message}` + (err.traceId ? ` (traceId=${err.traceId})` : '')
    } else {
      error.value = (err as Error)?.message ?? '未知错误'
    }
  }

  function reset(): void {
    currentDetail.value = null
    currentDiff.value = null
    lastValidation.value = null
    buffer.value = emptyBufferState()
    error.value = null
  }

  return {
    // state
    parameterSets,
    currentDetail,
    currentDiff,
    paramDiffRows,
    paramDiffPair,
    lastValidation,
    statusFilter,
    showInactive,
    loading,
    listLoading,
    actionRunning,
    validating,
    error,
    actor,
    actorRoles,
    buffer,
    warehouseDict,
    warehouseDictLoaded,
    warehouseDictError,
    // getters
    filteredParameterSets,
    statusCounts,
    noVersionCount,
    inactiveCount,
    currentStatus,
    canWrite,
    canPublishAction,
    actions,
    dirty,
    flattenedRows,
    rowsGrouped,
    anchorHintMessage,
    anchorResourceCode,
    solverStrategyHasContent,
    warehouseDictCodes,
    warehouseNameMap,
    supplyWarehousePriority,
    procurementWarehousePriority,
    defaultPurchaseLt,
    arrivalToUsableOffsets,
    planningYields,
    currentWarehouseRefs,
    currentUnknownWarehouseCodes,
    forkSourceRefs,
    forkSourceUnknownCodes,
    // actions
    loadList,
    loadDetail,
    createParameterSet,
    updateParameterSet,
    loadDiff,
    refreshDiff,
    onCellEdit,
    addBlockField,
    removeBlockField,
    setAnchorResourceCode,
    loadWarehouseDict,
    setSupplyWarehousePriority,
    setProcurementWarehousePriority,
    setDefaultPurchaseLt,
    setArrivalToUsableOffsets,
    setPlanningYields,
    checkForkSourceWarehouseRefs,
    onCancelDirty,
    onSaveDraft,
    validateDraft,
    publish,
    disable,
    removeParameterSet,
    forkDraft,
    setStatusFilter,
    reset,
    handleError
  }
})
