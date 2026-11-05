/**
 * APS V1 4号位 — 策略配置 Pinia store（独立模块，避免 rules store 膨胀）
 *
 * 状态：
 *  - profiles：Profile 列表
 *  - selectedProfileId：当前选中 Profile
 *  - versions：当前 Profile 的版本列表
 *  - selectedVersionId：当前选中版本
 *  - currentDiff：版本 Diff（base → target）
 *  - publishValidation：发布前校验结果
 *  - runTrace：Run 引用追溯
 *  - loading 系列：UI 态
 *
 * 权限（前后端一致，DDL 真值见 lps/LPS.APS.Core/Authorization/PermissionCodes.cs:54-58）：
 *  - aps.strategy.view     → 查看（列表 / 详情 / Diff / Trace / Default / Published）
 *  - aps.strategy.edit     → 编辑（创建 DRAFT / 更新 DRAFT / Disable）
 *  - aps.strategy.publish  → 发布（POST /publish）
 *  - 注：后端 .NET 常量名是 StrategyView / StrategyEdit / StrategyPublish（PascalCase），
 *    但 DDL PermissionCode 字符串值与前端 aps.strategy.* 三码完全一致（10-10 审查
 *    确认非错位；store 历史注释的「RuleMaintain/RulePublish」实为常量名误读）
 *
 * 红线：
 *  - 已发布/退役/归档版本不可编辑（前端按钮 disabled + 后端 400 双保险）
 *  - IsDefault 不在创建/更新入参内（C-7：Create 强制 false、Update 冻结），
 *    只能对 PUBLISHED 版本走 set-default 显式置位；发布≠默认
 *  - 发布前必须先看 diff（UI 流程：选 DRAFT → 看 diff(base, draft) → validate → publish）
 */

import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import {
  strategyProfileApi,
  ApiError,
  type StrategyProfileDto,
  type StrategyProfileVersionDto,
  type StrategyVersionDraftInput,
  type PublishStrategyVersionRequest,
  type DisableStrategyVersionRequest,
  type PublishValidationResultDto,
  type StrategyVersionDiffDto,
  type StrategyRunTraceDto,
  type StrategyVersionStatus,
  STRATEGY_STATUS_META,
  STRATEGY_EDITABLE_STATUSES,
  STRATEGY_PUBLISHABLE_STATUSES,
  STRATEGY_DISABLEABLE_STATUSES
} from '@/api/aps-v1'
import { useApsAuthStore } from './auth'

export const useStrategyProfileStore = defineStore('aps.strategyProfile', () => {
  // ===== state =====
  const profiles = ref<StrategyProfileDto[]>([])
  const selectedProfileId = ref<number | null>(null)

  const versions = ref<StrategyProfileVersionDto[]>([])
  const selectedVersionId = ref<number | null>(null)

  const currentDiff = ref<StrategyVersionDiffDto | null>(null)
  const publishValidation = ref<PublishValidationResultDto | null>(null)
  const runTrace = ref<StrategyRunTraceDto | null>(null)

  const loading = ref(false)
  const listLoading = ref(false)
  const versionsLoading = ref(false)
  const actionRunning = ref(false)
  const validating = ref(false)
  const error = ref<string | null>(null)

  // ===== auth =====
  const apsAuth = useApsAuthStore()

  // ===== getters =====

  /** 当前选中的 Profile（null = 列表页） */
  const currentProfile = computed<StrategyProfileDto | null>(() => {
    if (selectedProfileId.value === null) return null
    return profiles.value.find((p) => p.id === selectedProfileId.value) ?? null
  })

  /** 当前选中的版本 */
  const currentVersion = computed<StrategyProfileVersionDto | null>(() => {
    if (selectedVersionId.value === null) return null
    return versions.value.find((v) => v.id === selectedVersionId.value) ?? null
  })

  /** 当前版本的 status */
  const currentStatus = computed<StrategyVersionStatus | null>(
    () => currentVersion.value?.status ?? null
  )

  /** 是否可编辑（仅 DRAFT） */
  const canEdit = computed<boolean>(
    () => currentStatus.value !== null && STRATEGY_EDITABLE_STATUSES.includes(currentStatus.value)
  )

  /** 是否可发布（DRAFT/SUBMITTED/APPROVED） */
  const canPublishAction = computed<boolean>(
    () =>
      currentStatus.value !== null && STRATEGY_PUBLISHABLE_STATUSES.includes(currentStatus.value)
  )

  /** 是否可 Disable（仅 PUBLISHED） */
  const canRetire = computed<boolean>(
    () =>
      currentStatus.value !== null && STRATEGY_DISABLEABLE_STATUSES.includes(currentStatus.value)
  )

  /** 任意写权限 */
  const canWrite = computed<boolean>(() => apsAuth.has('aps.strategy.edit'))

  /** 发布权限 */
  const canPublishPerm = computed<boolean>(() => apsAuth.has('aps.strategy.publish'))

  /** 当前 Profile 是否已有任何 PUBLISHED 版本（IsDefault 勾选框显隐依据） */
  const hasPublished = computed<boolean>(() => versions.value.some((v) => v.status === 'PUBLISHED'))

  /** 当前选中 Profile 的 status 分布（顶部 KPI 用） */
  const statusCounts = computed<Record<StrategyVersionStatus, number>>(() => {
    const c: Record<StrategyVersionStatus, number> = {
      DRAFT: 0,
      SUBMITTED: 0,
      APPROVED: 0,
      PUBLISHED: 0,
      DISABLED: 0,
      ARCHIVED: 0
    }
    versions.value.forEach((v) => {
      c[v.status] = (c[v.status] ?? 0) + 1
    })
    return c
  })

  // ===== actions =====

  function selectProfile(profileId: number | null): void {
    selectedProfileId.value = profileId
    selectedVersionId.value = null
    versions.value = []
    currentDiff.value = null
    publishValidation.value = null
    runTrace.value = null
  }

  function selectVersion(versionId: number | null): void {
    selectedVersionId.value = versionId
    currentDiff.value = null
    publishValidation.value = null
    runTrace.value = null
  }

  async function loadProfiles(): Promise<void> {
    listLoading.value = true
    error.value = null
    try {
      profiles.value = await strategyProfileApi.listProfiles()
    } catch (err) {
      handleError(err)
    } finally {
      listLoading.value = false
    }
  }

  /**
   * 新建策略包主表（POST /strategy-profile）。
   *
   * 与参数集不同：策略包首版**必须绑定规则集版本 + 参数集版本**，没有「空白草稿」可自动跟建
   * ⇒ 这里只建壳并选中，首版由用户点「新建草稿」时选择两个版本。
   *
   * 写接口报错不等于没写成：失败时回列表核对同编码，防「其实已建成、只是响应报错」把用户卡死。
   */
  async function createProfile(input: {
    strategyProfileCode: string
    strategyProfileName: string
    description?: string
    runType?: string | null
  }): Promise<number | null> {
    if (!canWrite.value) {
      error.value = '当前操作者无写权限（需权限码 aps.strategy.edit）'
      return null
    }
    const code = input.strategyProfileCode.trim()
    const name = input.strategyProfileName.trim()
    if (!code || !name) {
      error.value = '策略包编码和名称都必须填写'
      return null
    }
    if (
      profiles.value.some((p) => p.strategyProfileCode?.trim().toLowerCase() === code.toLowerCase())
    ) {
      error.value = `策略包编码已存在：${code}`
      return null
    }
    actionRunning.value = true
    error.value = null
    try {
      const created = await strategyProfileApi.createProfile({
        strategyProfileCode: code,
        strategyProfileName: name,
        description: input.description?.trim() || undefined,
        runType: input.runType || null
      })
      await loadProfiles()
      return created.id
    } catch (err) {
      await loadProfiles()
      const hit = profiles.value.find(
        (p) => p.strategyProfileCode?.trim().toLowerCase() === code.toLowerCase()
      )
      if (hit) {
        console.warn('[策略包] 新建接口报错，但回列表发现同编码策略包已存在，按已建成继续', err)
        return hit.id
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
   * 更新策略包主表（PUT /strategy-profile/{id}）：名称 / 描述 / RunType / 启停。
   *
   * 编码原样回传（后端拒绝变更）；停用只关主表，不级联版本状态（存在版本时页面禁止新建草稿）。
   * 写接口报错不等于没写成：失败时回列表核对真值，已是新值就按成功继续。
   */
  async function updateProfile(input: {
    id: number
    strategyProfileCode: string
    strategyProfileName: string
    description?: string | null
    runType?: string | null
    isActive: boolean
  }): Promise<boolean> {
    if (!canWrite.value) {
      error.value = '当前操作者无写权限（需权限码 aps.strategy.edit）'
      return false
    }
    const name = input.strategyProfileName.trim()
    if (!name) {
      error.value = '策略包名称必须填写'
      return false
    }
    actionRunning.value = true
    error.value = null
    try {
      await strategyProfileApi.updateProfile({
        id: input.id,
        strategyProfileCode: input.strategyProfileCode,
        strategyProfileName: name,
        description: input.description?.trim() || null,
        runType: input.runType || null,
        isActive: input.isActive
      })
      await loadProfiles()
      return true
    } catch (err) {
      await loadProfiles()
      const row = profiles.value.find((p) => p.id === input.id)
      const saved =
        row != null &&
        row.strategyProfileName === name &&
        row.isActive === input.isActive &&
        (row.runType ?? null) === (input.runType || null)
      if (saved) {
        console.warn('[策略包] 保存接口报错，但回列表核对已是新值，按已保存继续', err)
        return true
      }
      handleError(err)
      const httpStatus = err instanceof ApiError ? err.code : undefined
      error.value =
        `保存没成功${httpStatus ? `（服务器返回 ${httpStatus}）` : ''}。` +
        '请刷新列表核对名称/运行类型/启用状态是否已生效。'
      return false
    } finally {
      actionRunning.value = false
    }
  }

  /**
   * 删除策略包主表（DELETE /strategy-profile/{id}，3号位 2026-10-26 同 R1 口径）
   *
   *  - 权限码沿用主表写端点码 `aps.strategy.edit`（3号位：不新增权限码）
   *  - 前端先拦「已发布过不可删」：后端必拒，前端先拦是为了让用户看到原因而不是撞 400
   *  - 后端还有一条前端拿不到的前置条件（版本被 ScheduleRun 引用 ⇒ 400），
   *    页面不预判，失败时如实回显后端原文
   *  - 写接口报错≠没写成 ⇒ 失败后回列表核对：行已不在 ⇒ 按已删除继续
   */
  async function removeStrategyProfile(profileId: number): Promise<boolean> {
    if (!canWrite.value) {
      error.value = '当前操作者无删除权限（需权限码 aps.strategy.edit）'
      return false
    }
    const row = profiles.value.find((p) => p.id === profileId)
    if (!row) {
      error.value = '策略包未选中'
      return false
    }
    // versions 只装当前选中策略包的版本 ⇒ 带上 profileId 判定，避免删 A 时用上 B 的状态
    const hasPublished = versions.value.some(
      (v) => v.strategyProfileId === profileId && v.status === 'PUBLISHED'
    )
    if (hasPublished) {
      error.value = '该策略包已发布过，不能删除（请先停用版本）'
      return false
    }
    actionRunning.value = true
    error.value = null
    try {
      await strategyProfileApi.deleteStrategyProfile(profileId)
      selectProfile(null)
      await loadProfiles()
      return true
    } catch (err) {
      await loadProfiles()
      const stillThere = profiles.value.some((p) => p.id === profileId)
      if (!stillThere) {
        console.warn('[策略包] 删除接口报错，但回列表发现该策略包已不存在，按已删除继续', err)
        selectProfile(null)
        return true
      }
      handleError(err)
      return false
    } finally {
      actionRunning.value = false
    }
  }

  async function loadVersions(profileId: number): Promise<void> {
    versionsLoading.value = true
    error.value = null
    try {
      versions.value = await strategyProfileApi.listVersions(profileId)
    } catch (err) {
      handleError(err)
      versions.value = []
    } finally {
      versionsLoading.value = false
    }
  }

  async function loadDiff(sourceVersionId: number, targetVersionId: number): Promise<void> {
    if (sourceVersionId === targetVersionId) {
      currentDiff.value = null
      return
    }
    error.value = null
    try {
      currentDiff.value = await strategyProfileApi.diffVersions(sourceVersionId, targetVersionId)
    } catch (err) {
      handleError(err)
      currentDiff.value = null
    }
  }

  /** 发布前校验（结果回写到 publishValidation） */
  async function validateDraft(versionId: number): Promise<boolean> {
    validating.value = true
    error.value = null
    try {
      publishValidation.value = await strategyProfileApi.validateVersion(versionId)
      return publishValidation.value.isValid
    } catch (err) {
      handleError(err)
      publishValidation.value = null
      return false
    } finally {
      validating.value = false
    }
  }

  /** 创建 DRAFT（需 edit 权限） */
  async function createDraft(
    input: StrategyVersionDraftInput
  ): Promise<StrategyProfileVersionDto | null> {
    if (!canWrite.value) {
      error.value = '当前操作者无创建权限（需权限码 aps.strategy.edit）'
      return null
    }
    actionRunning.value = true
    error.value = null
    try {
      const v = await strategyProfileApi.createVersion(input)
      if (selectedProfileId.value === input.strategyProfileId) {
        await loadVersions(input.strategyProfileId)
      }
      return v
    } catch (err) {
      handleError(err)
      return null
    } finally {
      actionRunning.value = false
    }
  }

  /** 更新 DRAFT（需 edit 权限） */
  async function updateDraft(
    versionId: number,
    input: StrategyVersionDraftInput
  ): Promise<boolean> {
    if (!canWrite.value) {
      error.value = '当前操作者无更新权限（需权限码 aps.strategy.edit）'
      return false
    }
    actionRunning.value = true
    error.value = null
    try {
      await strategyProfileApi.updateVersion(versionId, input)
      if (selectedProfileId.value !== null) {
        await loadVersions(selectedProfileId.value)
      }
      return true
    } catch (err) {
      handleError(err)
      return false
    } finally {
      actionRunning.value = false
    }
  }

  /** 发布（需 publish 权限） */
  async function publish(
    versionId: number,
    request: PublishStrategyVersionRequest
  ): Promise<boolean> {
    if (!canPublishPerm.value) {
      error.value = '当前操作者无发布权限（需权限码 aps.strategy.publish）'
      return false
    }
    actionRunning.value = true
    error.value = null
    try {
      await strategyProfileApi.publishVersion(versionId, request)
      if (selectedProfileId.value !== null) {
        await loadVersions(selectedProfileId.value)
      }
      return true
    } catch (err) {
      handleError(err)
      return false
    } finally {
      actionRunning.value = false
    }
  }

  /** 退役（需 edit 权限） */
  async function disable(
    versionId: number,
    request: DisableStrategyVersionRequest
  ): Promise<boolean> {
    if (!canWrite.value) {
      error.value = '当前操作者无退役权限（需权限码 aps.strategy.edit）'
      return false
    }
    actionRunning.value = true
    error.value = null
    try {
      await strategyProfileApi.disableVersion(versionId, request)
      if (selectedProfileId.value !== null) {
        await loadVersions(selectedProfileId.value)
      }
      return true
    } catch (err) {
      handleError(err)
      return false
    } finally {
      actionRunning.value = false
    }
  }

  /**
   * 设为默认（需 edit 权限；仅 PUBLISHED 可置位，D-3）
   *
   * - 后端只允许经该显式操作置位：Create 强制 IsDefault=false、Update 冻结既有值
   * - 同策略包旧默认由后端清；跨策略包生效窗重叠会被后端拒（DEFAULT_CONFLICT），前端不预判
   * - 写接口报错≠没写成 ⇒ 失败后重载版本，目标已是默认即按成功继续
   */
  async function setDefault(versionId: number): Promise<boolean> {
    if (!canWrite.value) {
      error.value = '当前操作者无设为默认权限（需权限码 aps.strategy.edit）'
      return false
    }
    const target = versions.value.find((v) => v.id === versionId)
    if (!target) {
      error.value = '策略包版本未找到，请刷新列表'
      return false
    }
    if (target.status !== 'PUBLISHED') {
      error.value = `仅已发布版本可设为默认（当前状态：${STRATEGY_STATUS_META[target.status].label}）`
      return false
    }
    actionRunning.value = true
    error.value = null
    try {
      await strategyProfileApi.setDefaultVersion(versionId)
      if (selectedProfileId.value !== null) {
        await loadVersions(selectedProfileId.value)
      }
      return true
    } catch (err) {
      if (selectedProfileId.value !== null) {
        await loadVersions(selectedProfileId.value)
      }
      const after = versions.value.find((v) => v.id === versionId)
      if (after?.isDefault) {
        console.warn('[策略包] 设为默认接口报错，但回列表核对已是默认，按成功继续', err)
        return true
      }
      handleError(err)
      return false
    } finally {
      actionRunning.value = false
    }
  }

  /** Run 引用追溯 */
  async function loadTrace(versionId: number): Promise<void> {
    error.value = null
    try {
      runTrace.value = await strategyProfileApi.getRunTrace(versionId)
    } catch (err) {
      handleError(err)
      runTrace.value = null
    }
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
    profiles.value = []
    selectedProfileId.value = null
    versions.value = []
    selectedVersionId.value = null
    currentDiff.value = null
    publishValidation.value = null
    runTrace.value = null
    error.value = null
  }

  return {
    // state
    profiles,
    selectedProfileId,
    versions,
    selectedVersionId,
    currentDiff,
    publishValidation,
    runTrace,
    loading,
    listLoading,
    versionsLoading,
    actionRunning,
    validating,
    error,
    // getters
    currentProfile,
    currentVersion,
    currentStatus,
    canEdit,
    canPublishAction,
    canRetire,
    canWrite,
    canPublishPerm,
    hasPublished,
    statusCounts,
    // actions
    selectProfile,
    selectVersion,
    loadProfiles,
    createProfile,
    updateProfile,
    removeStrategyProfile,
    loadVersions,
    loadDiff,
    validateDraft,
    createDraft,
    updateDraft,
    publish,
    disable,
    setDefault,
    loadTrace,
    reset,
    handleError
  }
})
