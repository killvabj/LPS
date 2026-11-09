/**
 * APS V1 4号位 — Setup 主数据下拉共享 composable
 *
 * 覆盖 Setup 4 页 + 2 个 Dialog 的部门/大工艺/设备/产品下拉。
 * 数据源全部为后端既有端点（4号位 不改后端）：
 *   - 部门    GET /api/governance/lookups/departments      → DepartmentLookupItem（含 stageCode）
 *   - 大工艺  无独立端点，从部门列表的 stageCode 去重派生
 *   - 设备    GET /api/governance/lookups/resources        → ResourceLookupItem
 *   - 产品    GET /api/governance/lookups/materials?activeOnly=true → MaterialLookupItem
 *
 * Id-口径 v1.1：写输入提交内部主键 Id（productionDepartmentId/resourceId/fromMaterialId/
 * toMaterialId），大工艺/工序传 Code。本 composable 同时提供 Code→Id 解析，
 * 使 4 号位 Setup 建单不再依赖 mock 映射。
 *
 * 10-15 4号位 —— 远程搜索（后端 TOP 200 截断的正解）：
 *   后端三个 lookup 的 SQL 都是 `SELECT TOP (@Limit) ... ORDER BY [Code]`，
 *   且 limit 是仓储方法默认值（MasterDataLookupRepository.cs:87/98/108）、
 *   Controller（SetupRuleController.cs:299-347）**不透传** —— 不带 search 时永远只返
 *   字母序前 200 条。dev 实测物料 A-Z 每个字母档都恰好 200 条（截断最严重）。
 *   所以 loadAll() 之后必须能按关键词补搜，否则用户配不出字母序 200 之外的设备/物料。
 *   search* 结果 **合并进 raw refs**（追加去重，不是替换）—— 否则已选项会从下拉消失。
 */

import { computed, ref, type ComputedRef, type Ref } from 'vue'
import {
  departmentLookupApi,
  materialLookupApi,
  resourceLookupApi,
  type DepartmentLookupItem,
  type MaterialLookupItem,
  type ResourceLookupItem
} from '@/api/aps-v1'

export interface SetupLookupOption {
  label: string
  value: string
  /** 内部主键 Id（写输入提交用）；产品/设备/部门有，大工艺无 */
  id?: number
  /** 部门归属大工艺（仅部门选项携带，用于大工艺级联收窄） */
  stageCode?: string
}

export interface SetupMasterData {
  loading: Ref<boolean>
  /** true = 至少一个主数据源失败或返回空，下拉可能不完整 */
  degraded: Ref<boolean>
  /** true = 后端首屏 TOP 200 已被吃满，主数据总数超过 200，必须靠搜索补 */
  truncated: Ref<boolean>
  departments: ComputedRef<SetupLookupOption[]>
  stages: ComputedRef<SetupLookupOption[]>
  resources: ComputedRef<SetupLookupOption[]>
  materials: ComputedRef<SetupLookupOption[]>
  loadAll: () => Promise<void>
  /** 远程搜索（按编码/名称片段补拉后端 TOP 200 之外的项） */
  searchDepartments: (keyword: string) => Promise<void>
  searchResources: (keyword: string) => Promise<void>
  searchMaterials: (keyword: string) => Promise<void>
  departmentIdByCode: (code: string) => number | undefined
  resourceIdByCode: (code: string) => number | undefined
  materialIdByCode: (code: string) => number | undefined
  /** 某部门归属的大工艺（部门→大工艺是 1:1，DepartmentLookupItem.stageCode 即真值） */
  stageOfDepartment: (departmentCode: string) => string | undefined
  /** 设备编码 → 归属部门编码（ResourceLookupItem 只有 productionDepartmentId，需反查） */
  departmentCodeOfResource: (resourceCode: string) => string | undefined
}

/** 后端 lookup 默认 TOP 200；返回条数达到该值即说明还有截断 */
const BACKEND_LOOKUP_LIMIT = 200

/**
 * 远程补拉 + 本地按关键词收窄。
 *
 * ElSelect 只要同时传 `filterable` + `remote` + `remote-method`，就**完全不做本地过滤**
 * （element-plus useSelect.updateOptions 首行直接 return）；而本 composable 的 search*
 * 是「合并补拉」（把 TOP 200 之外的项追加进列表），同样不收缩列表。两者叠加 ⇒ 用户
 * 打字后下拉一条不少，看起来像「模糊搜索不生效」。所以调用方必须自己按关键词收窄展示项，
 * 未输入时保持全量。
 */
export function filterLookupOptions(
  options: SetupLookupOption[],
  query: string
): SetupLookupOption[] {
  const q = query.trim().toLowerCase()
  if (!q) return options
  return options.filter(
    (o) => o.label.toLowerCase().includes(q) || o.value.toLowerCase().includes(q)
  )
}

function mergeBy<T extends { masterId: number }>(base: T[], incoming: T[]): T[] {
  if (!incoming.length) return base
  const seen = new Set(base.map((x) => x.masterId))
  const extra = incoming.filter((x) => !seen.has(x.masterId))
  return extra.length ? [...base, ...extra] : base
}

export function useSetupMasterData(): SetupMasterData {
  const loading = ref(false)
  const degraded = ref(false)
  const truncated = ref(false)

  const rawDepartments = ref<DepartmentLookupItem[]>([])
  const rawResources = ref<ResourceLookupItem[]>([])
  const rawMaterials = ref<MaterialLookupItem[]>([])

  /** 资源 Id → 部门编码（由部门 lookup 反建，供设备 label 显示真实所属部门） */
  const departmentCodeById = computed<Map<number, string>>(
    () => new Map(rawDepartments.value.map((d) => [d.masterId, d.code]))
  )

  const departments = computed<SetupLookupOption[]>(() =>
    rawDepartments.value.map((d) => ({
      label: d.holon ? `${d.code}（${d.holon}）` : d.code,
      value: d.code,
      id: d.masterId,
      stageCode: d.stageCode?.trim() || undefined
    }))
  )

  const stages = computed<SetupLookupOption[]>(() => {
    const seen = new Set<string>()
    const out: SetupLookupOption[] = []
    for (const d of rawDepartments.value) {
      const code = d.stageCode?.trim()
      if (!code || seen.has(code)) continue
      seen.add(code)
      out.push({ label: code, value: code })
    }
    return out.sort((a, b) => a.value.localeCompare(b.value))
  })

  const resources = computed<SetupLookupOption[]>(() =>
    rawResources.value.map((r) => ({
      label: r.name && r.name !== r.code ? `${r.code}（${r.name}）` : r.code,
      value: r.code,
      id: r.masterId
    }))
  )

  const materials = computed<SetupLookupOption[]>(() =>
    rawMaterials.value.map((m) => ({
      label: m.name ? `${m.code}（${m.name}）` : m.code,
      value: m.code,
      id: m.masterId
    }))
  )

  const departmentIdByCode = (code: string): number | undefined =>
    rawDepartments.value.find((d) => d.code === code)?.masterId
  const resourceIdByCode = (code: string): number | undefined =>
    rawResources.value.find((r) => r.code === code)?.masterId
  const materialIdByCode = (code: string): number | undefined =>
    rawMaterials.value.find((m) => m.code === code)?.masterId

  /** 部门→大工艺 1:1：直接取该部门自身的 stageCode，不依赖被截断的全局 stage 集合 */
  const stageOfDepartment = (departmentCode: string): string | undefined =>
    rawDepartments.value.find((d) => d.code === departmentCode)?.stageCode?.trim() || undefined

  /** 设备→部门编码：ResourceLookupItem 只给 productionDepartmentId，按 Id 反查 */
  const departmentCodeOfResource = (resourceCode: string): string | undefined => {
    const r = rawResources.value.find((x) => x.code === resourceCode)
    if (!r?.productionDepartmentId) return undefined
    return departmentCodeById.value.get(r.productionDepartmentId)
  }

  async function loadAll(): Promise<void> {
    loading.value = true
    degraded.value = false
    try {
      const [depts, res, mats] = await Promise.all([
        departmentLookupApi.listDepartments(),
        resourceLookupApi.listResources(),
        materialLookupApi.listActiveMaterials()
      ])
      rawDepartments.value = depts
      rawResources.value = res
      rawMaterials.value = mats
      // 三个 lookup 均走 try/catch 兜底返 []，任一为空即视为降级（页面据此提示）
      if (!depts.length || !res.length || !mats.length) degraded.value = true
      // 返满 200 = 后端 TOP 200 吃满，后面还有。部门/设备/产品任一命中即提示用户走搜索。
      truncated.value =
        depts.length >= BACKEND_LOOKUP_LIMIT ||
        res.length >= BACKEND_LOOKUP_LIMIT ||
        mats.length >= BACKEND_LOOKUP_LIMIT
    } finally {
      loading.value = false
    }
  }

  /** 空关键词 = 清掉搜索结果（回到首屏那一批），非空 = 合并进已有列表 */
  async function searchDepartments(keyword: string): Promise<void> {
    const kw = keyword.trim()
    if (!kw) return
    rawDepartments.value = mergeBy(
      rawDepartments.value,
      await departmentLookupApi.searchDepartments(kw)
    )
  }

  async function searchResources(keyword: string): Promise<void> {
    const kw = keyword.trim()
    if (!kw) return
    rawResources.value = mergeBy(rawResources.value, await resourceLookupApi.searchResources(kw))
  }

  async function searchMaterials(keyword: string): Promise<void> {
    const kw = keyword.trim()
    if (!kw) return
    rawMaterials.value = mergeBy(rawMaterials.value, await materialLookupApi.searchMaterials(kw))
  }

  return {
    loading,
    degraded,
    truncated,
    departments,
    stages,
    resources,
    materials,
    loadAll,
    searchDepartments,
    searchResources,
    searchMaterials,
    departmentIdByCode,
    resourceIdByCode,
    materialIdByCode,
    stageOfDepartment,
    departmentCodeOfResource
  }
}
