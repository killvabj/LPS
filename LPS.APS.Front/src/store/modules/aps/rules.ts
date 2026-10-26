/**
 * APS V1 4号位 — 规则集（RuleSet）维护 Pinia store
 *
 * ⚠️ 2026-10-22 结构性重写：**只管规则集**。
 *   原 store 有「双轨」结构（ruleSetBuffer + parameterSetBuffer，1:1 关联），
 *   那是前端自造的模型 —— 冻结 DDL 里规则集与参数集是**两条平行链**，
 *   只在 StrategyProfileVersion 上会合（DDL 08:9132-9133）。
 *   ⇒ 双轨缓冲、双轨保存、双轨发布（先 RS 后 PS）全部撤除；参数集移到
 *     `./parameterSets.ts`（自己的 store、自己的页面）。
 *
 * 状态：
 *  - ruleSets：列表
 *  - currentDetail：当前选中规则集的详情（段位 + 历史）
 *  - currentDiff：两个版本的字段差异
 *  - lastValidation：最近一次「校验草稿」的后端结果（PublishValidationResult）
 *  - statusFilter：列表过滤
 *
 * 权限（v1.2 §23.1 权限码门控）：
 *  - aps.rule.view / aps.rule.edit / aps.rule.publish
 */

import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import {
  ruleApi,
  mockActor,
  ApiError,
  canCreateDraftForStatus,
  normalizeRuleStatus,
  type RuleSetSummaryDto,
  type RuleSetDetailDto,
  type BackendRuleSetVersion,
  type VersionDiffResultDto,
  type RuleStatus,
  type RoleKey,
  type PublishValidationResultDto,
  type CreateRuleSetInput,
  type UpdateRuleSetInput,
  type PrioritySegmentDto,
  NO_VERSION
} from '@/api/aps-v1'
import {
  serializePrioritySegments,
  validatePrioritySegments
} from '@/api/aps-v1/prioritySegmentEditor'
import { useApsAuthStore } from './auth'
import { assertScope, ScopeViolationError, filterByScope } from './scope'

export const useRulesStore = defineStore('aps.rules', () => {
  // ===== state =====
  const ruleSets = ref<RuleSetSummaryDto[]>([])
  const currentDetail = ref<RuleSetDetailDto | null>(null)
  const currentDiff = ref<VersionDiffResultDto | null>(null)
  const lastValidation = ref<PublishValidationResultDto | null>(null)
  const statusFilter = ref<RuleStatus | typeof NO_VERSION | 'ALL'>('ALL')
  const loading = ref(false)
  const listLoading = ref(false)
  const actionRunning = ref(false)
  const validating = ref(false)
  const saving = ref(false)
  const error = ref<string | null>(null)

  /** 当前操作者（真实模式由 JWT 注入；此处为展示兜底） */
  const actor = ref(mockActor.name)
  const apsAuth = useApsAuthStore()
  const actorRoles = computed<RoleKey[]>(() =>
    apsAuth.roles.length > 0 ? apsAuth.roles : mockActor.roles
  )

  // ===== getters =====
  /**
   * 过滤后的 RuleSet 列表
   *  - 状态过滤 + 业务范围（domainKey）过滤；domainKey='ALL' 表示跨域，始终放行
   *  - ⚠️ 当前后端 RuleSet 表**无 Domain 外键**（DDL 08:9025），domainKey 恒为 'ALL'
   *    ⇒ 这条业务范围预过滤目前**不生效**；已致函 3号位 追问后端是否二次校验（R1）。
   */
  const filteredRuleSets = computed(() => {
    let arr = ruleSets.value
    if (statusFilter.value === NO_VERSION) {
      arr = arr.filter((r) => !r.hasVersion)
    } else if (statusFilter.value !== 'ALL') {
      arr = arr.filter((r) => r.status === statusFilter.value)
    }
    return filterByScope(arr, apsAuth.dataScope, (r) =>
      r.domainKey === 'ALL' ? {} : { domainKey: r.domainKey }
    )
  })

  /** 状态分布（顶部 KPI 用）—— v1.4 §二十 6 态 */
  const statusCounts = computed(() => {
    const c: Record<RuleStatus, number> = {
      DRAFT: 0,
      SUBMITTED: 0,
      APPROVED: 0,
      PUBLISHED: 0,
      DISABLED: 0,
      ARCHIVED: 0
    }
    ruleSets.value.forEach((r) => {
      c[r.status] = (c[r.status] ?? 0) + 1
    })
    return c
  })

  /**
   * 无任何版本的规则集数量（10-14 增）
   *
   * dev 实测 698 个里 695 个属于此类 —— 它们点开必然加载不出内容，
   * 所以必须单独成列，否则「已发布」KPI 会把点不开的条目算成已发布。
   * 这是**正交于 6 态的计数**，不并进 statusCounts（v1.4 §二十 的冻结状态语义不动）。
   */
  const noVersionCount = computed(() => ruleSets.value.filter((r) => !r.hasVersion).length)

  /** 当前选中 RuleSet 的 status */
  const currentStatus = computed(() => currentDetail.value?.summary.status ?? null)

  /** 是否有发布权限 —— aps.rule.publish */
  const canPublishAction = computed(() => apsAuth.has('aps.rule.publish'))

  /** 是否有写权限 —— aps.rule.edit */
  const canWrite = computed(() => apsAuth.has('aps.rule.edit'))

  /** 按钮可见性矩阵 */
  const actions = computed(() => {
    const s = currentStatus.value
    const edit = apsAuth.has('aps.rule.edit')
    const pub = apsAuth.has('aps.rule.publish')
    return {
      /** [+ 新建草稿]：任意状态可建（实际能否 fork 由 forkPermission 决定） */
      canCreateDraft: edit,
      /** [校验]：仅 DRAFT + 有发布权限 */
      canValidate: s === 'DRAFT' && pub,
      /** [发布]：仅 DRAFT + 有发布权限 */
      canPublish: s === 'DRAFT' && pub,
      /** [停用]：仅 PUBLISHED + 有发布权限 */
      canDisable: s === 'PUBLISHED' && pub
    }
  })

  /** 当前 RuleSet 是否允许 fork 新 DRAFT */
  const forkPermission = computed(() => {
    const s = currentStatus.value
    if (!s) return { allowed: false, reason: '未选中 RuleSet' }
    return canCreateDraftForStatus(s)
  })

  /**
   * 当前选中 RuleSet 是否**可删**（3号位 R1 口径）
   *
   * 判据 = 历史版本里**没有 PUBLISHED**（已发布过的只能停用，历史不可变守恒）。
   * 与后端 `DeleteRuleSetAsync:113-117` 同源；后端另有一条前端看不到的
   * 拒删条件（被策略包引用）⇒ 真被拒时由后端 message 如实回显。
   */
  const currentDeletable = computed(() => {
    const d = currentDetail.value
    if (!d) return false
    return !(d.history ?? []).some((h) => h.status === 'PUBLISHED')
  })

  // ===== actions =====
  async function loadList(): Promise<void> {
    listLoading.value = true
    error.value = null
    try {
      ruleSets.value = await ruleApi.listRuleSets()
    } catch (err) {
      handleError(err)
    } finally {
      listLoading.value = false
    }
  }

  async function loadDetail(ruleSetId: number): Promise<void> {
    loading.value = true
    error.value = null
    currentDiff.value = null
    lastValidation.value = null
    try {
      const detail = await ruleApi.getRuleSet(ruleSetId)
      // 规则集的代码/名称只在主表列表里（详情端点只给版本内容）⇒ 用列表行回填
      const row = ruleSets.value.find((r) => r.ruleSetId === ruleSetId)
      detail.summary.ruleSetCode = row?.ruleSetCode ?? detail.summary.ruleSetCode
      detail.summary.ruleSetName = row?.ruleSetName ?? detail.summary.ruleSetName
      detail.summary.lastChangeReason = row?.lastChangeReason
      // 主表启停 / 说明同样只在列表里（Q4：与版本状态是两件事）
      detail.summary.isActive = row?.isActive
      detail.summary.description = row?.description
      currentDetail.value = detail

      // 列表行同步刷新（状态 / 版本码可能已被别处改动）
      if (row) {
        const idx = ruleSets.value.findIndex((r) => r.ruleSetId === ruleSetId)
        if (idx >= 0) {
          ruleSets.value[idx] = {
            ...ruleSets.value[idx],
            status: detail.summary.status,
            currentVersionCode: detail.summary.currentVersionCode,
            draftVersionCode: detail.summary.draftVersionCode,
            publishedAt: detail.summary.publishedAt
          }
        }
      }

      // 默认对比：已发布版本 ↔ 草稿版本（两者都存在才有意义）
      const history = detail.history
      const baseVer = history.find((h) => h.status === 'PUBLISHED' && h.isCurrent)?.versionId
      const targetVer = history.find((h) => h.status === 'DRAFT')?.versionId
      if (baseVer && targetVer && baseVer !== targetVer) {
        await loadDiff(baseVer, targetVer)
      } else {
        currentDiff.value = null
      }
    } catch (err) {
      handleError(err)
      currentDetail.value = null
    } finally {
      loading.value = false
    }
  }

  async function loadDiff(base: number, target: number): Promise<void> {
    try {
      currentDiff.value = await ruleApi.diffVersions(base, target)
    } catch (err) {
      handleError(err)
      currentDiff.value = null
    }
  }

  /** 校验草稿（U22：发布前必走）
   *
   *  后端返回 `PublishValidationResult`：isValid + errors[] + warnings[] + validatedAt。
   *  ⚠️ 原前端自造 `{status:'PASSED'|'FAILED', message}` —— 那两个字段后端没有，
   *    页面上的「校验状态」标签因此永远是错的。
   */
  async function validateDraft(): Promise<boolean> {
    validating.value = true
    error.value = null
    try {
      const detail = currentDetail.value
      const versionId = (detail?.history ?? []).filter((h) => h.status === 'DRAFT').pop()?.versionId
      if (!versionId) {
        error.value = '当前没有草稿版本可校验'
        return false
      }
      const result = await ruleApi.validateDraft(versionId)
      lastValidation.value = result
      return result.isValid
    } catch (err) {
      handleError(err)
      return false
    } finally {
      validating.value = false
    }
  }

  /** 发布（需权限码 aps.rule.publish）
   *
   *  ⚠️ 后端 publish 返回**一句文本**，没有 newVersion / historicalVersions ⇒
   *    前端不造结果对象，发布后重新拉取列表读真状态。
   */
  async function publish(ruleSetId: number, changeReason: string): Promise<boolean> {
    if (!canPublishAction.value) {
      error.value = '当前操作者无发布权限（需权限码 aps.rule.publish）'
      return false
    }
    const detail = currentDetail.value
    if (!detail || detail.summary.ruleSetId !== ruleSetId || detail.summary.status !== 'DRAFT') {
      error.value = '当前规则集不是草稿状态，无法发布'
      return false
    }
    if (detail.summary.domainKey !== 'ALL') {
      try {
        assertScope(apsAuth.dataScope, { domainKey: detail.summary.domainKey })
      } catch (err) {
        if (err instanceof ScopeViolationError) {
          error.value = err.message
          return false
        }
        throw err
      }
    }
    const versionId = (detail.history ?? []).filter((h) => h.status === 'DRAFT').pop()?.versionId
    if (!versionId) {
      error.value = '当前草稿没有可用的版本标识，无法发布'
      return false
    }
    actionRunning.value = true
    error.value = null
    try {
      await ruleApi.publishRuleSet({ versionId, changeReason, actor: actor.value })
      await loadDetail(ruleSetId)
      await loadList()
      return true
    } catch (err) {
      handleError(err)
      return false
    } finally {
      actionRunning.value = false
    }
  }

  /** 停用（需权限码 aps.rule.publish）
   *
   *  后端 disable 挂 `aps.rule.edit`（GovernanceController.cs:254），但页面按
   *  「停用 = 发布动作」的口径要求发布权，缺权时给出明确提示。
   */
  async function disable(ruleSetId: number, reason: string): Promise<boolean> {
    if (!canPublishAction.value) {
      error.value = '当前操作者无停用权限（需权限码 aps.rule.publish）'
      return false
    }
    const detail = currentDetail.value
    if (!detail || detail.summary.ruleSetId !== ruleSetId) {
      error.value = '规则集未选中'
      return false
    }
    if (detail.summary.domainKey !== 'ALL') {
      try {
        assertScope(apsAuth.dataScope, { domainKey: detail.summary.domainKey })
      } catch (err) {
        if (err instanceof ScopeViolationError) {
          error.value = err.message
          return false
        }
        throw err
      }
    }
    const versionId = (detail.history ?? [])
      .filter((h) => h.status === 'PUBLISHED')
      .pop()?.versionId
    if (!versionId) {
      error.value = '当前没有已发布版本可停用'
      return false
    }
    actionRunning.value = true
    error.value = null
    try {
      await ruleApi.disableRuleSet({
        ruleSetId,
        versionId,
        reason,
        actor: actor.value,
        actorRoles: actorRoles.value
      })
      await loadDetail(ruleSetId)
      await loadList()
      return true
    } catch (err) {
      handleError(err)
      return false
    } finally {
      actionRunning.value = false
    }
  }

  /**
   * 新建草稿（fork）
   *
   *  ⚠️ 只 fork **规则集**。原先此处还顺带 POST 了一条参数集版本（自造的 1:1 模型），
   *    且读的是不存在的 `sourceRs.parameterSetVersionId` ⇒ dev 实测 422，100% 失败。
   *    参数集请到「参数集维护」页建自己的草稿。
   */
  async function forkDraft(
    sourceRuleSetVersionId: number,
    newVersionCode: string,
    changeReason?: string
  ): Promise<boolean> {
    if (!canWrite.value) {
      error.value = '当前操作者无写权限（需权限码 aps.rule.edit）'
      return false
    }
    if (!forkPermission.value.allowed) {
      error.value = forkPermission.value.reason ?? '当前状态不允许新建草稿'
      return false
    }
    if (!sourceRuleSetVersionId) {
      error.value = '找不到可作为起点的版本'
      return false
    }
    actionRunning.value = true
    error.value = null
    try {
      await ruleApi.forkDraft(sourceRuleSetVersionId, newVersionCode, changeReason)
      await loadList()
      const ruleSetId = currentDetail.value?.summary.ruleSetId
      if (ruleSetId) await loadDetail(ruleSetId)
      return true
    } catch (err) {
      handleError(err)
      return false
    } finally {
      actionRunning.value = false
    }
  }

  /**
   * 新建规则集（两步，3号位 Q2 方案 A）
   *
   *  1. `POST /rule-set` 建主表壳（编码用户填、全库唯一、创建后不可改）
   *  2. `POST /rule-set/version` 跟建一个空白 DRAFT —— 主表本身没有内容，
   *     不跟建版本的话列表只会显示「未配版本」、点开无内容
   *
   * 返回新规则集 Id（失败返回 null）。第二步失败时主表已存在，如实报错并刷新列表。
   */
  async function createRuleSet(input: CreateRuleSetInput): Promise<number | null> {
    if (!canWrite.value) {
      error.value = '当前操作者无写权限（需权限码 aps.rule.edit）'
      return null
    }
    const code = input.ruleSetCode.trim()
    const name = input.ruleSetName.trim()
    if (!code || !name) {
      error.value = '规则集编码和名称都必须填写'
      return null
    }
    actionRunning.value = true
    error.value = null
    try {
      const created = await ruleApi.createRuleSet({
        ruleSetCode: code,
        ruleSetName: name,
        description: input.description?.trim() || undefined
      })
      return await attachBlankDraft(created.id, created.ruleSetCode)
    } catch (err) {
      /**
       * 新建主表可能「已落库但返回 5xx」（dev 实测 2026-10-25：POST /rule-set 的
       * `CreatedAtAction(nameof(GetRuleSetVersion), null, …)` 路由不匹配 ⇒ INSERT 已提交、
       * 响应阶段抛 `No route matches the supplied values` ⇒ 500）。
       *
       * 只报错会把用户卡死：列表看不到那条规则集，重试又得「编码已存在」——
       * 看得见的失败 + 改不掉的失败 = 无解。
       * ⇒ 回列表核对：同编码行已存在 ⇒ 实际已建成，补上空白草稿继续走。
       */
      await loadList()
      const hit = ruleSets.value.find(
        (r) => r.ruleSetCode?.trim().toLowerCase() === code.toLowerCase()
      )
      if (hit) {
        console.warn('[规则集] 新建接口报错，但回列表发现同编码规则集已存在，按已建成继续', err)
        return await attachBlankDraft(hit.ruleSetId, hit.ruleSetCode)
      }
      // 列表里也没有 ⇒ 确实没建出来。把技术报错换成大白话，并给出下一步该点什么。
      handleError(err)
      const httpStatus = err instanceof ApiError ? err.code : undefined
      error.value =
        `新建没成功${httpStatus ? `（服务器返回 ${httpStatus}）` : ''}。` +
        '也可能是「其实已经建好了，只是返回时出错」——请刷新一下列表看这个编码在不在；' +
        '在的话点进去点「新建草稿」补一条空白草稿就能用。'
      return null
    } finally {
      actionRunning.value = false
    }
  }

  /**
   * 主表建好后跟建一个空白 DRAFT 版本（主表只是壳，没版本点开无内容）
   *
   * 失败时先看该规则集是否**已经有草稿**（重复提交 / 恢复路径）：
   * 有 ⇒ 目标已达成，直接返回；没有 ⇒ 如实告知「壳建好了、版本没建上」。
   */
  async function attachBlankDraft(ruleSetId: number, ruleSetCode: string): Promise<number | null> {
    try {
      await ruleApi.createRuleSetDraft({
        ruleSetId,
        versionCode: `${ruleSetCode}-V1`,
        remarks: '新建规则集时自动创建的空白草稿'
      })
    } catch (err) {
      const versions: BackendRuleSetVersion[] = await ruleApi
        .listRuleSetVersions(ruleSetId)
        .catch((): BackendRuleSetVersion[] => [])
      if (versions.some((v) => normalizeRuleStatus(v.status) === 'DRAFT')) {
        await loadList()
        return ruleSetId
      }
      // 主表已落库：如实告知「壳建好了、版本没建上」，并让列表反映真实状态
      await loadList()
      handleError(err)
      error.value = `规则集已创建，但空白草稿版本创建失败：${error.value}`
      return null
    }
    await loadList()
    return ruleSetId
  }

  /**
   * 删除规则集（DELETE /api/governance/rule-set/{id}，3号位 R1）
   *
   * 口径与后端 `RuleSetGovernanceService.DeleteRuleSetAsync:113-125` 逐字一致：
   *  - 该族**存在任何 PUBLISHED 版本** ⇒ 拒删（"历史不可变守恒"），只能停用
   *  - 任一版本**被策略包引用** ⇒ 拒删（FK 无级联，硬删会违约）
   *  - 否则事务内连带删全部 DRAFT 版本 + 主表行
   *
   * ⚠️ 破坏性操作：调用方必须先弹二次确认。
   * ⚠️ 写接口报错≠没写成 ⇒ 失败后回列表核对：行已不在 ⇒ 按已删除处理。
   */
  async function removeRuleSet(ruleSetId: number): Promise<boolean> {
    if (!canWrite.value) {
      error.value = '当前操作者无删除权限（需权限码 aps.rule.edit）'
      return false
    }
    const detail = currentDetail.value
    const row = ruleSets.value.find((r) => r.ruleSetId === ruleSetId)
    // 「未配版本」的规则集没有详情（loadDetail 加载不出内容）⇒ 用列表行兜底
    if (!row && (!detail || detail.summary.ruleSetId !== ruleSetId)) {
      error.value = '规则集未选中'
      return false
    }
    const hasPublished = detail
      ? (detail.history ?? []).some((h) => h.status === 'PUBLISHED')
      : row?.status === 'PUBLISHED'
    if (hasPublished) {
      error.value = '该规则集已发布过，不能删除（请先停用版本）'
      return false
    }
    actionRunning.value = true
    error.value = null
    try {
      await ruleApi.deleteRuleSet(ruleSetId)
      currentDetail.value = null
      currentDiff.value = null
      await loadList()
      return true
    } catch (err) {
      /**
       * 删除失败有两种可能：真没删掉；或**已经删掉了但响应阶段出错**。
       * 后者若直接报错，用户会看到"删除失败"却发现列表里那条真的没了 ⇒ 困惑。
       * ⇒ 回列表核对：行已不在 ⇒ 按已删除继续。
       */
      await loadList()
      const stillThere = ruleSets.value.some((r) => r.ruleSetId === ruleSetId)
      if (!stillThere) {
        console.warn('[规则集] 删除接口报错，但回列表发现该规则集已不存在，按已删除继续', err)
        currentDetail.value = null
        currentDiff.value = null
        return true
      }
      handleError(err)
      return false
    } finally {
      actionRunning.value = false
    }
  }

  /** 编辑规则集主表（改名 / 改说明 / 启停；编码不可改） */
  async function updateRuleSet(input: UpdateRuleSetInput): Promise<boolean> {
    if (!canWrite.value) {
      error.value = '当前操作者无写权限（需权限码 aps.rule.edit）'
      return false
    }
    const name = input.ruleSetName.trim()
    if (!name) {
      error.value = '规则集名称不能为空'
      return false
    }
    actionRunning.value = true
    error.value = null
    try {
      await ruleApi.updateRuleSet({ ...input, ruleSetName: name })
      await loadList()
      // 详情端点是「版本内容」，不含主表字段 ⇒ 用列表行回填，不重取详情
      if (currentDetail.value?.summary.ruleSetId === input.ruleSetId) {
        const row = ruleSets.value.find((r) => r.ruleSetId === input.ruleSetId)
        if (row) {
          currentDetail.value.summary.ruleSetName = row.ruleSetName
          currentDetail.value.summary.isActive = row.isActive
          currentDetail.value.summary.description = row.description
        }
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
   * 保存需求优先级段位到当前展示的**草稿版本**
   *
   *  - 只有 DRAFT 能改（后端 `EnsureUpdatable`）：PUBLISHED / DISABLED / ARCHIVED 一律拒绝
   *  - PUT body 是整条 RuleSetVersion：带 versionCode + demandPriorityJson + remarks；
   *    **不带 contentSnapshotJson**（后端会强制回填库内既有快照，传了也无效，
   *    换型规则子块靠这个机制保留）
   *  - 保存前先跑前端拦截（与后端发布校验同口径），避免用户存完去发布才被打回
   */
  async function saveDraftSegments(
    segments: PrioritySegmentDto[],
    changeReason?: string
  ): Promise<boolean> {
    if (!canWrite.value) {
      error.value = '当前操作者无写权限（需权限码 aps.rule.edit）'
      return false
    }
    const detail = currentDetail.value
    if (!detail) {
      error.value = '规则集未选中'
      return false
    }
    if (detail.summary.status !== 'DRAFT') {
      error.value = '只有草稿版本可以修改段位；已发布版本请先新建草稿'
      return false
    }
    if (!detail.contentVersionId) {
      error.value = '找不到当前展示的版本，请重新打开该规则集'
      return false
    }
    const localErrors = validatePrioritySegments(segments)
    if (localErrors.length > 0) {
      error.value = localErrors.join('；')
      return false
    }
    saving.value = true
    error.value = null
    try {
      await ruleApi.updateRuleSetDraft({
        versionId: detail.contentVersionId,
        body: {
          id: detail.contentVersionId,
          ruleSetId: detail.summary.ruleSetId,
          versionCode: detail.contentVersionCode,
          status: 'DRAFT',
          demandPriorityJson: serializePrioritySegments(segments),
          remarks: changeReason?.trim() || undefined
        }
      })
      await loadDetail(detail.summary.ruleSetId)
      await loadList()
      return true
    } catch (err) {
      handleError(err)
      return false
    } finally {
      saving.value = false
    }
  }

  function setStatusFilter(filter: RuleStatus | typeof NO_VERSION | 'ALL'): void {
    statusFilter.value = filter
  }

  function handleError(err: unknown): void {
    if (err instanceof ApiError) {
      // 业务码可能整个缺失（如后端 5xx 返 text/plain 异常文本）⇒ 别拼出 "[undefined] …"
      const code = Number.isFinite(err.code) ? `[${err.code}] ` : ''
      error.value = `${code}${err.message}` + (err.traceId ? ` (traceId=${err.traceId})` : '')
    } else {
      error.value = (err as Error)?.message ?? '未知错误'
    }
  }

  function reset(): void {
    currentDetail.value = null
    currentDiff.value = null
    lastValidation.value = null
    error.value = null
  }

  return {
    // state
    ruleSets,
    currentDetail,
    currentDiff,
    lastValidation,
    statusFilter,
    loading,
    listLoading,
    actionRunning,
    validating,
    saving,
    error,
    actor,
    actorRoles,
    // getters
    filteredRuleSets,
    statusCounts,
    noVersionCount,
    currentStatus,
    canWrite,
    canPublishAction,
    actions,
    forkPermission,
    currentDeletable,
    // actions
    loadList,
    loadDetail,
    loadDiff,
    validateDraft,
    saveDraftSegments,
    publish,
    disable,
    forkDraft,
    createRuleSet,
    updateRuleSet,
    removeRuleSet,
    setStatusFilter,
    reset,
    handleError
  }
})
