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
  type RuleSetListRowDto,
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
// 对比对选取与参数集页共用一份（函数虽在 parameterDiff.ts 里，逻辑与治理族无关，两族同口径）
import { pickDiffPair } from '@/api/aps-v1/parameterDiff'
import { useApsAuthStore } from './auth'
import { assertScope, ScopeViolationError, filterByScope } from './scope'

/** 列表排序方式（2026-11-11 筛选条件） */
export type RuleListSort = 'DEFAULT' | 'PUBLISHED_DESC' | 'CREATED_DESC' | 'CODE_ASC'

/** 排序选项文案（页面下拉与本表共用一份，避免两处漂移） */
export const RULE_LIST_SORT_LABELS: Record<RuleListSort, string> = {
  DEFAULT: '默认顺序',
  PUBLISHED_DESC: '最近发布',
  CREATED_DESC: '最近创建',
  CODE_ASC: '编码升序'
}

export const useRulesStore = defineStore('aps.rules', () => {
  // ===== state =====
  /**
   * 规则集列表行（**一版一行**，2026-11-11 列表源切换）
   *
   * ⚠️ 一行 = 一个版本（`versionId` / `versionCode` / `versionStatus` 齐备），
   *   另有「无版本主表」行（hasVersion=false）。
   *   同族多个版本（已发布 + 草稿）各占一行、状态各自真实，不再互相覆盖。
   */
  const ruleSets = ref<RuleSetListRowDto[]>([])
  const currentDetail = ref<RuleSetDetailDto | null>(null)
  const currentDiff = ref<VersionDiffResultDto | null>(null)
  /** 当前字段级 Diff 的两端版本 Id（「查看换型规则逐条差异」下钻 SetupDiff 时带参用） */
  const diffBaseVersionId = ref<number | null>(null)
  const diffTargetVersionId = ref<number | null>(null)
  /**
   * 「版本对比」页签内的大白话提示（diff 算不出来时的原因说明）
   *
   * ⚠️ 2026-11-11：后端 diff 端点在「同一版本内两条换型规则业务键重复」时直接 400
   *   （.NET ToDictionary 重复键 ⇒ "An item with the same key has already been added"）。
   *   那是后端数据/实现问题，前端绕不过 ⇒ **不弹全局英文报错条**（用户看不懂、也挡着页面），
   *   改成在页签内说明「差异算不出来，但内容/历史版本/发布都照常用」。
   */
  const diffError = ref<string | null>(null)
  const lastValidation = ref<PublishValidationResultDto | null>(null)
  const statusFilter = ref<RuleStatus | typeof NO_VERSION | 'ALL'>('ALL')
  /**
   * 列表筛选条件（2026-11-11 用户拍板）
   *
   * 原「显示已停用」开关已撤：停用行**默认就显示**（「全部」页签含停用），
   * 顶部 KPI 横条也撤了（各状态计数改为直接落在页签上）。
   */
  const listKeyword = ref('')
  /** 主表启停筛选（与版本状态正交：主表停用 ≠ 版本 DISABLED） */
  const listActiveFilter = ref<'ALL' | 'ACTIVE' | 'INACTIVE'>('ALL')
  /** 列表排序 */
  const listSort = ref<RuleListSort>('DEFAULT')
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
   * 列表基础集：**业务范围 + 关键词 + 主表状态**过滤后（页签过滤之前）
   *
   *  - 业务范围（domainKey）过滤沿用原口径：domainKey='ALL' 表示跨域，始终放行。
   *    ⚠️ 当前后端 RuleSet 表**无 Domain 外键**（DDL 08:9025），domainKey 恒为 'ALL'
   *      ⇒ 这条业务范围预过滤目前**不生效**；已致函 3号位 追问后端是否二次校验（R1）。
   *  - 关键词：规则集编码 / 名称 / 版本码 模糊匹配（不区分大小写）。
   *  - 主表状态：主表停用（isActive=false）的行**默认也显示**
   *    （2026-11-11 用户拍板：「全部」页签即显示全部，含停用 ⇒ 原「显示已停用」开关已撤）。
   */
  const scopedRuleSets = computed(() => {
    let arr = ruleSets.value
    const kw = listKeyword.value.trim().toLowerCase()
    if (kw) {
      arr = arr.filter(
        (r) =>
          r.ruleSetCode.toLowerCase().includes(kw) ||
          r.ruleSetName.toLowerCase().includes(kw) ||
          (r.versionCode ?? '').toLowerCase().includes(kw)
      )
    }
    if (listActiveFilter.value === 'ACTIVE') {
      arr = arr.filter((r) => r.isActive !== false)
    } else if (listActiveFilter.value === 'INACTIVE') {
      arr = arr.filter((r) => r.isActive === false)
    }
    return filterByScope(arr, apsAuth.dataScope, (r) =>
      r.domainKey === 'ALL' ? {} : { domainKey: r.domainKey }
    )
  })

  /**
   * 过滤后的规则集列表行（页签状态过滤 + 排序）
   *
   *  - 状态过滤按**版本**真实状态；无版本的行只在「全部」「未配版本」两个页签出现
   *    （点「草稿」不该捞出几百个根本没版本的行）。
   */
  const filteredRuleSets = computed(() => {
    let arr = scopedRuleSets.value
    if (statusFilter.value === NO_VERSION) {
      arr = arr.filter((r) => !r.hasVersion)
    } else if (statusFilter.value !== 'ALL') {
      arr = arr.filter((r) => r.hasVersion && r.versionStatus === statusFilter.value)
    }
    return sortRuleSetRows(arr)
  })

  /**
   * 页签计数（2026-11-11：顶部 KPI 横条撤除后，计数直接落在页签上）
   *
   *  - ALL = 当前筛选下会显示的**行数**（含停用主表的行）
   *  - DRAFT / PUBLISHED / DISABLED = 该**版本状态**的行数
   *  - NO_VERSION = 没有版本的行数（「未配版本」页签）
   * 计数随关键词 / 主表状态筛选实时变化。
   */
  const tabCounts = computed(() => {
    const c = { ALL: 0, DRAFT: 0, PUBLISHED: 0, DISABLED: 0, NO_VERSION: 0 }
    scopedRuleSets.value.forEach((r) => {
      c.ALL += 1
      if (!r.hasVersion) {
        c.NO_VERSION += 1
        return
      }
      if (r.versionStatus === 'DRAFT') c.DRAFT += 1
      else if (r.versionStatus === 'PUBLISHED') c.PUBLISHED += 1
      else if (r.versionStatus === 'DISABLED') c.DISABLED += 1
    })
    return c
  })

  /** 规则集（主表）总数 —— 一版一行后列表行数 ≠ 规则集数（「全部」页签的悬停说明用） */
  const ruleSetTotal = computed(() => new Set(ruleSets.value.map((r) => r.ruleSetId)).size)

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

  /** 当前 RuleSet 是否允许 fork 新 DRAFT
   *
   * ⚠️ 2026-11-11（一版一行）：`currentStatus` 现在是**当前展示的那一版**的状态，
   *   而「这个规则集有没有草稿」是**主表级**事实 ⇒ 两件事要分开看：
   *   点「已发布」那一行时 currentStatus=PUBLISHED，但同族可能已经有草稿 ⇒
   *   此时再建草稿会与现有草稿冲突，必须拦。
   */
  const forkPermission = computed(() => {
    const detail = currentDetail.value
    if (!detail) return { allowed: false, reason: '未选中 RuleSet' }
    const hasDraft = (detail.history ?? []).some((h) => h.status === 'DRAFT')
    if (hasDraft) {
      return { allowed: false, reason: '该规则集已有草稿版本；请直接编辑现有草稿' }
    }
    const s = currentStatus.value
    if (!s) return { allowed: false, reason: '未选中 RuleSet' }
    return canCreateDraftForStatus(s)
  })

  // ===== actions =====
  async function loadList(): Promise<void> {
    listLoading.value = true
    error.value = null
    try {
      // 新端点（一版一行）+ 旧端点的「无版本主表」行，合并见 ruleApi.listRuleSetRows
      ruleSets.value = await ruleApi.listRuleSetRows()
    } catch (err) {
      handleError(err)
    } finally {
      listLoading.value = false
    }
  }

  /** 清空字段级 Diff 及其两端版本 Id（切规则集 / 删除 / 重新加载时） */
  function clearDiff(): void {
    currentDiff.value = null
    diffBaseVersionId.value = null
    diffTargetVersionId.value = null
    diffError.value = null
  }

  /**
   * 载入规则集详情
   *
   * @param ruleSetId 规则集（主表）Id
   * @param versionId 要展示的版本 Id（列表点哪一行就传哪一版；不传 = 草稿优先）
   */
  async function loadDetail(ruleSetId: number, versionId?: number): Promise<void> {
    loading.value = true
    error.value = null
    clearDiff()
    lastValidation.value = null
    try {
      const detail = await ruleApi.getRuleSet(ruleSetId, versionId)
      // 规则集的代码/名称只在主表列表里（详情端点只给版本内容）⇒ 用列表行回填
      const row =
        ruleSets.value.find(
          (r) => r.ruleSetId === ruleSetId && (versionId == null || r.versionId === versionId)
        ) ?? ruleSets.value.find((r) => r.ruleSetId === ruleSetId)
      detail.summary.ruleSetCode = row?.ruleSetCode ?? detail.summary.ruleSetCode
      detail.summary.ruleSetName = row?.ruleSetName ?? detail.summary.ruleSetName
      // 最近变更 = **本页展示的那一版**的变更原因（历史里有每版备注）
      detail.summary.lastChangeReason = detail.history.find(
        (h) => h.versionId === detail.contentVersionId
      )?.changeReason
      // 主表启停 / 说明同样只在列表里（Q4：与版本状态是两件事）
      detail.summary.isActive = row?.isActive
      detail.summary.description = row?.description
      currentDetail.value = detail

      // 列表行同步刷新（该版本的状态可能已被别处改动）
      if (row) {
        const idx = ruleSets.value.findIndex((r) => r.rowKey === row.rowKey)
        if (idx >= 0) {
          ruleSets.value[idx] = {
            ...ruleSets.value[idx],
            versionStatus: detail.summary.status,
            currentVersionCode: detail.summary.currentVersionCode,
            draftVersionCode: detail.summary.draftVersionCode,
            publishedAt: detail.summary.publishedAt
          }
        }
      }

      // 默认对比对：与参数集页同口径，统一走 `pickDiffPair`（真源在 api/aps-v1/parameterDiff.ts，
      // 10-28 参数集页先修过同一个坑；规则集页此前只认「有草稿」⇒ 草稿一发布就永久空白）
      //   ① 有草稿 → 当前已发布 ↔ 草稿（回答「我这次改了什么」）
      //   ② 无草稿 → 上一已发布 ↔ 当前已发布（回答「上一版到这一版改了什么」）
      //      例：CESHI1111 V1已停用 / V2已发布 / V3已发布 ⇒ 比 V2 → V3。
      const pair = pickDiffPair(detail.history)
      if (pair && pair.source.versionId != null && pair.target.versionId != null) {
        await loadDiff(pair.source.versionId, pair.target.versionId)
      } else {
        clearDiff()
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
      diffBaseVersionId.value = base
      diffTargetVersionId.value = target
      diffError.value = null
    } catch {
      // ⚠️ 不写全局 error、不弹英文原文（后端 400 的 message 是 .NET 异常文本，
      //   用户看不懂）。差异算不出来只影响「版本对比」这一个页签 ⇒ 页签内说明即可。
      clearDiff()
      diffError.value =
        '这两个版本的差异算不出来（后端对比换型规则时出错，多半是同一版本里有两条业务键重复的换型规则）。' +
        '版本内容、历史版本、校验与发布都不受影响，可先看「历史版本」。'
    }
  }

  /** 校验草稿（U22：发布前必走）
   *
   *  后端返回 `PublishValidationResult`：isValid + errors[] + warnings[] + validatedAt。
   *  ⚠️ 原前端自造 `{status:'PASSED'|'FAILED', message}` —— 那两个字段后端没有，
   *    页面上的「校验状态」标签因此永远是错的。
   *
   *  2026-11-11：保存段位不再拦「值没填」⇒ 这类问题挪到这一关提示。
   *  先跑本地拦截（能指名到第几段第几条条件，比后端文案准），本地不过就不再问后端；
   *  本地过了才走后端校验（后端才是发布的最终闸门）。
   */
  async function validateDraft(): Promise<boolean> {
    validating.value = true
    error.value = null
    try {
      const detail = currentDetail.value
      // 校验作用在**当前展示的那一版**（一版一行后，「校验草稿」按钮只在该行是草稿时出现）
      if (!detail || detail.summary.status !== 'DRAFT' || !detail.contentVersionId) {
        error.value = '当前没有草稿版本可校验'
        return false
      }
      const versionId = detail.contentVersionId
      // 本地拦截：条件值没填、段名没写、空条件组、兜底段位置不对…（与后端发布校验同口径）
      const localErrors = validatePrioritySegments(detail.prioritySegments)
      if (localErrors.length > 0) {
        lastValidation.value = {
          isValid: false,
          errors: localErrors.map((message) => ({ code: 'LOCAL', message })),
          warnings: [],
          validatedAt: new Date().toISOString()
        }
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
    const versionId = detail.contentVersionId
    if (!versionId) {
      error.value = '当前草稿没有可用的版本标识，无法发布'
      return false
    }
    actionRunning.value = true
    error.value = null
    try {
      await ruleApi.publishRuleSet({ versionId, changeReason, actor: actor.value })
      // 发布后仍停在这一版（它已变成已发布），列表随后整体刷新
      await loadDetail(ruleSetId, versionId)
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
    // 停用作用在**当前展示的那一版**（一版一行后，「停用」按钮只在该行是已发布时出现）
    if (detail.summary.status !== 'PUBLISHED') {
      error.value = '只有已发布版本可以停用'
      return false
    }
    const versionId = detail.contentVersionId
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
      await loadDetail(ruleSetId, versionId)
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
   * 物理删除规则集整族（DELETE /rule-set/{id}，权限 aps.rule.edit）
   *
   *  最窄口径：**没有任何已发布版本**的规则集才可硬删（连带删草稿）；含 PUBLISHED 后端 400。
   *  前端只做「明显不可删就提前拦」的提示，**不预判被引用**（拿不到引用关系），
   *  失败一律回显后端原文。
   *
   *  ⚠️ 写接口报错 ≠ 没写成：请求失败后**必须回列表核对真值** —— 行已不在即按已删除继续，
   *  不能把一次 500/超时直接抛给用户当成「删除失败」。
   */
  async function removeRuleSet(ruleSetId: number): Promise<boolean> {
    if (!canWrite.value) {
      error.value = '当前操作者无删除权限（需权限码 aps.rule.edit）'
      return false
    }
    actionRunning.value = true
    error.value = null
    let failedMsg: string | null = null
    try {
      await ruleApi.deleteRuleSet(ruleSetId)
    } catch (err) {
      failedMsg = (err as Error)?.message ?? '删除请求失败'
    }
    try {
      await loadList()
    } catch {
      /* loadList 内部已 handleError */
    }
    const stillThere = ruleSets.value.some((r) => r.ruleSetId === ruleSetId)
    actionRunning.value = false
    if (stillThere) {
      if (failedMsg) handleError(new Error(failedMsg))
      return false
    }
    // 行已不在：无论请求返回什么，事实是删掉了
    if (currentDetail.value?.summary.ruleSetId === ruleSetId) reset()
    return true
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
      const created = await ruleApi.forkDraft(sourceRuleSetVersionId, newVersionCode, changeReason)
      await loadList()
      const ruleSetId = currentDetail.value?.summary.ruleSetId
      // 落点切到**刚建出来的那条草稿**（一版一行后，列表里它就是新的一行）
      if (ruleSetId) await loadDetail(ruleSetId, created.ruleSetDraft.id)
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
   *  - ⚠️ 2026-11-11：**保存不再做内容校验**（值没填、段名没写、空条件组等一律放过）。
   *    草稿的用途就是「先存下来、回头再填」；内容合不合格由「校验草稿」那一关把关，
   *    发布仍由后端校验兜底。**校验不过 ≠ 不能存**。
   *    保留的拦截只有权限、版本状态、版本存在性这三条（与内容无关）。
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
      await loadDetail(detail.summary.ruleSetId, detail.contentVersionId)
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

  /**
   * 排序（2026-11-11 筛选条件）
   *
   * DEFAULT = 保持后端返回顺序；其余按时间/编码排序。
   * 时间字段可能为空（无版本行没有发布时间）⇒ 空值排在最后（降序时也是最后）。
   */
  function sortRuleSetRows(rows: RuleSetListRowDto[]): RuleSetListRowDto[] {
    const list = [...rows]
    switch (listSort.value) {
      case 'PUBLISHED_DESC':
        return list.sort((a, b) => (b.publishedAt ?? '').localeCompare(a.publishedAt ?? ''))
      case 'CREATED_DESC':
        return list.sort((a, b) => (b.createdAt ?? '').localeCompare(a.createdAt ?? ''))
      case 'CODE_ASC':
        return list.sort((a, b) => a.ruleSetCode.localeCompare(b.ruleSetCode, 'zh-Hans-CN'))
      default:
        return list
    }
  }

  function setListKeyword(keyword: string): void {
    listKeyword.value = keyword
  }

  function setListActiveFilter(filter: 'ALL' | 'ACTIVE' | 'INACTIVE'): void {
    listActiveFilter.value = filter
  }

  function setListSort(sort: RuleListSort): void {
    listSort.value = sort
  }

  /** 一键清空筛选条件（关键词 / 主表状态 / 排序） */
  function resetListFilters(): void {
    listKeyword.value = ''
    listActiveFilter.value = 'ALL'
    listSort.value = 'DEFAULT'
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
    clearDiff()
    lastValidation.value = null
    error.value = null
  }

  return {
    // state
    ruleSets,
    currentDetail,
    currentDiff,
    diffBaseVersionId,
    diffTargetVersionId,
    diffError,
    lastValidation,
    statusFilter,
    listKeyword,
    listActiveFilter,
    listSort,
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
    scopedRuleSets,
    tabCounts,
    ruleSetTotal,
    currentStatus,
    canWrite,
    canPublishAction,
    actions,
    forkPermission,
    // actions
    loadList,
    loadDetail,
    loadDiff,
    validateDraft,
    saveDraftSegments,
    publish,
    disable,
    removeRuleSet,
    forkDraft,
    createRuleSet,
    updateRuleSet,
    setStatusFilter,
    setListKeyword,
    setListActiveFilter,
    setListSort,
    resetListFilters,
    reset,
    handleError
  }
})
