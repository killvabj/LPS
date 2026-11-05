<!--
  APS V1 4号位 — 缺失规则查询
  依据：frontNew/docs/APS_V1_Setup换型规则与有限产能优化_冻结文档修改指导_v1.2_20260916.md
   - §五 第三优先：无明确规则也无默认规则 → Setup = 0 分钟，并记录解释/数据质量事实
   - §18 缺失规则数据治理
   - §19.2 4号位「缺失规则查询」+「0 分钟兜底查询」

  职责：统计排产批次中所有未命中 EXACT / DEFAULT 规则的产品转换（0 分钟兜底）
  用途：
   - 排查 0 分钟兜底是否合理
   - 指导 DEFAULT 规则补全
   - 评估换型规则覆盖率

  输入：排产批次（必填）+ 部门/工序/设备/产品（可选筛选）
  输出：部门/大工艺/工序/设备/产品/出现次数
  出口：去补默认换型规则 / 导出 CSV
  统计口径：走后端聚合端点，不在前端二次聚合

  10-27 契约对齐（后端 SetupRuleController.cs:366-372 + SetupUncoveredStatRepository.cs:41-69）：
   - 筛参只有 runId / departmentId / operationCode / resourceId / materialId，
     且 departmentId/resourceId/materialId 都是**内部主键 Id**（不是 Code）——
     发 Code 后端绑不上，筛选会静默失效。
   - 大工艺（stageCode）只在输出行上回带，后端**没有**对应筛参 ⇒ 本页不提供大工艺筛选。
   - 输出行没有「前产品 / 后产品 / 最近出现」三列（后端 DTO 12 字段里没有）。
   - 出口降级：EXACT 规则入参必须同时给前/后产品主键（SetupRuleExactInput），
     而本统计给不出前产品 ⇒ 只能跳「默认换型规则」页。

  权限：aps.setup.view（只读页面，无写操作）
-->

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { storeToRefs } from 'pinia'
import dayjs from 'dayjs'
import {
  ElAlert,
  ElButton,
  ElCard,
  ElCol,
  ElEmpty,
  ElForm,
  ElFormItem,
  ElInput,
  ElInputNumber,
  ElMessage,
  ElOption,
  ElRow,
  ElSelect,
  ElTable,
  ElTableColumn,
  ElTag,
  ElTooltip
} from 'element-plus'
import { useSetupUncoveredStore } from '@/store/modules/aps/setupUncovered'
import { useApsAuthStore } from '@/store/modules/aps/auth'
import type { SetupUncoveredQuery } from '@/api/aps-v1'
import { roleLabelOf, setupApi, SETUP_SOURCES, SETUP_SOURCE_META } from '@/api/aps-v1'
import {
  filterLookupOptions,
  useSetupMasterData,
  type SetupLookupOption
} from './composables/useSetupMasterData'

defineOptions({ name: 'ApsSetupUncovered' })

const router = useRouter()
const uncoveredStore = useSetupUncoveredStore()
const apsAuth = useApsAuthStore()
const masterData = useSetupMasterData()

const { stats, listLoading, error, statCount } = storeToRefs(uncoveredStore)

// ===== 查询表单 =====
// 10-27：筛参全部改后端真实形参。departmentId / resourceId / materialId 走内部主键 Id
// （下拉选项的 opt.id 即 lookup 的 masterId，与后端 JOIN 的 Id 同源），
// operationCode 后端没有工序 lookup ⇒ 自由文本输入。
const queryForm = reactive<SetupUncoveredQuery>({
  runId: 1, // 默认 RunId=1（dev 演示用）
  departmentId: undefined,
  operationCode: undefined,
  resourceId: undefined,
  materialId: undefined
})

// ===== 下拉源（后端主数据 lookup） =====
// 10-15 4号位：下拉改远程搜索。后端 lookups/{departments,resources,materials} 的 SQL 都是
// `SELECT TOP (200) ... ORDER BY [Code]`、limit 写死且 Controller 不透传
// （MasterDataLookupRepository.cs:87-110 + SetupRuleController.cs:299-347）
// → 不带 search 时只有字母序前 200 条，用户筛不到后面的部门/设备/产品。
/** 每个筛选下拉各自的输入关键词 —— EP 在 remote 模式下不做本地过滤，靠它收窄展示项 */
const departmentQuery = ref('')
const resourceQuery = ref('')
const materialQuery = ref('')

const departmentOptions = computed(() =>
  filterLookupOptions(masterData.departments.value, departmentQuery.value)
)
const resourceOptions = computed(() =>
  filterLookupOptions(masterData.resources.value, resourceQuery.value)
)
const materialOptions = computed(() =>
  filterLookupOptions(masterData.materials.value, materialQuery.value)
)

/** 主键收窄：SetupLookupOption.id 是可选的（stages 没有主键），而 ElOption 的 value
 *  不接受 undefined；departments/resources/materials 三个 lookup 都带 masterId ⇒ 过滤掉无 id 的。 */
type LookupOptionWithId = SetupLookupOption & { id: number }
function onlyWithId(options: SetupLookupOption[]): LookupOptionWithId[] {
  return options.filter((o): o is LookupOptionWithId => typeof o.id === 'number')
}
const departmentOptionsWithId = computed(() => onlyWithId(departmentOptions.value))
const resourceOptionsWithId = computed(() => onlyWithId(resourceOptions.value))
const materialOptionsWithId = computed(() => onlyWithId(materialOptions.value))

const searching = reactive({ department: false, resource: false, material: false })

/** 300ms 防抖：连续打字只发最后一次 */
function debounce<A extends unknown[]>(fn: (...args: A) => void) {
  let timer: ReturnType<typeof setTimeout> | undefined
  return (...args: A) => {
    if (timer) clearTimeout(timer)
    timer = setTimeout(() => fn(...args), 300)
  }
}

const searchDepartment = debounce((kw: string) => {
  if (!kw.trim()) {
    searching.department = false
    return
  }
  searching.department = true
  void masterData.searchDepartments(kw).finally(() => (searching.department = false))
})
const searchResource = debounce((kw: string) => {
  if (!kw.trim()) {
    searching.resource = false
    return
  }
  searching.resource = true
  void masterData.searchResources(kw).finally(() => (searching.resource = false))
})
const searchMaterial = debounce((kw: string) => {
  if (!kw.trim()) {
    searching.material = false
    return
  }
  searching.material = true
  void masterData.searchMaterials(kw).finally(() => (searching.material = false))
})

/** 关键词立即落到展示项（收窄），后端补拉只负责追加 TOP 200 之外的项 */
function onSearchDepartment(kw: string): void {
  departmentQuery.value = kw
  searchDepartment(kw)
}
function onSearchResource(kw: string): void {
  resourceQuery.value = kw
  searchResource(kw)
}
function onSearchMaterial(kw: string): void {
  materialQuery.value = kw
  searchMaterial(kw)
}

// ===== 规则集版本映射 =====
// 10-28 3号位 P1 落码：uncovered-stats 每行回带 ruleSetVersionId（可为空）。
// 页面不直出内部 Id —— 用 #11 rule-set-versions（aps.setup.view 即可读）映射成「编码 / 版本号」。
const ruleSetVersionLabels = ref<Map<number, string>>(new Map())
/** 版本 Id → 所属规则集编码（出口跳「规则集维护」时定位规则集用；不直出内部 Id） */
const ruleSetCodeByVersionId = ref<Map<number, string>>(new Map())

async function loadRuleSetVersionLabels() {
  try {
    const versions = await setupApi.listRuleSetVersions()
    const map = new Map<number, string>()
    const codeMap = new Map<number, string>()
    for (const v of versions) {
      map.set(v.id, v.ruleSetCode ? `${v.ruleSetCode} / ${v.versionCode}` : v.versionCode)
      if (v.ruleSetCode) codeMap.set(v.id, v.ruleSetCode)
    }
    ruleSetVersionLabels.value = map
    ruleSetCodeByVersionId.value = codeMap
  } catch {
    // 取不到就显示「未能解析」，不编造版本号
    ruleSetVersionLabels.value = new Map()
    ruleSetCodeByVersionId.value = new Map()
  }
}

/** 空值 = 该 Run 未绑策略包版本；映射不到 = 版本列表里没有该 Id */
function ruleSetVersionLabel(versionId?: number | null): string {
  if (versionId == null) return '—'
  return ruleSetVersionLabels.value.get(versionId) ?? '（未能解析）'
}

// ===== 加载 =====
async function loadStats() {
  if (!queryForm.runId) {
    ElMessage.warning('请填写 RunId（必填）')
    return
  }
  await uncoveredStore.loadStats({ ...queryForm })
}

onMounted(() => {
  void masterData.loadAll()
  void loadRuleSetVersionLabels()
  // 默认加载一次（dev 演示用）
  void loadStats()
})

// ===== 出口操作 =====

/**
 * 去补默认换型规则 → 跳「规则集维护 → 换型规则」页签，预填 4 字段
 *
 * 2026-10-28：「默认换型规则」的维护入口已并入规则集页（两个 Setup 维护页从菜单隐藏），
 * 本页若还跳 /aps/setup/default，用户会落在一个菜单里找不到的页面 ⇒ 改跳规则集页。
 * ⚠️ /aps/rules 路由要 rule.view / rule.edit / rule.publish 任一（守卫 OR 语义）；
 *    只有 aps.setup.* 的角色（PMC）进不去 ⇒ 这类人仍跳隐藏但可用的 /aps/setup/default。
 *
 * 2026-10-28（#4 绑定口径）：出口把**这一行所属的规则集版本 Id** 一起带走
 * （ruleSetVersionId），规则集页据此明示「来源版本」⇒ 回填目标不再随版本漂移。
 */
function goToDefault(row: (typeof stats.value)[number]) {
  const prefill = {
    departmentCode: row.departmentCode,
    stageCode: row.stageCode ?? '',
    operationCode: row.operationCode,
    resourceCode: row.resourceCode
  }
  // 补规则 = 写 ⑦ ⇒ 必须持 aps.setup.edit；没有就直接说清，不做无效跳转
  if (!apsAuth.has('aps.setup.edit')) {
    ElMessage.warning('当前账号没有换型规则编辑权限（需权限码 aps.setup.edit），补不了规则')
    return
  }
  const canOpenRules =
    apsAuth.has('aps.rule.view') || apsAuth.has('aps.rule.edit') || apsAuth.has('aps.rule.publish')
  if (!canOpenRules) {
    void router.push({ path: '/aps/setup/default', query: prefill })
    return
  }
  const query: Record<string, string> = { tab: 'setup', ...prefill }
  const code =
    row.ruleSetVersionId != null
      ? ruleSetCodeByVersionId.value.get(row.ruleSetVersionId)
      : undefined
  if (code) query.ruleSetCode = code
  // 来源版本（不变 RuleSetVersionId 绑定）：规则集页用它明示「这条缺失来自哪个版本」
  if (row.ruleSetVersionId != null) query.ruleSetVersionId = String(row.ruleSetVersionId)
  void router.push({ path: '/aps/rules', query })
}

/** 导出 CSV */
function handleExportCsv() {
  const csv = uncoveredStore.exportCsv()
  const blob = new Blob(['﻿' + csv], { type: 'text/csv;charset=utf-8;' })
  const link = document.createElement('a')
  link.href = URL.createObjectURL(blob)
  link.download = `setup-uncovered-${dayjs().format('YYYYMMDD-HHmmss')}.csv`
  link.click()
  URL.revokeObjectURL(link.href)
  ElMessage.success(`已导出 ${statCount.value} 条记录`)
}
</script>

<template>
  <div class="setup-uncovered-page">
    <!-- 页面头部 -->
    <div class="page-header">
      <div class="header-left">
        <h2>缺失规则查询</h2>
        <p class="subtitle">查询未命中换型规则的产品转换（按 0 分钟兜底）</p>
      </div>
      <div class="header-right">
        <ElTooltip
          :content="`当前操作者：${apsAuth.userInfo?.userCode ?? '—'}（${apsAuth.roles.map(roleLabelOf).join('、')}）`"
        >
          <ElTag type="info" size="small">{{ apsAuth.userInfo?.userCode ?? '—' }}</ElTag>
        </ElTooltip>
      </div>
    </div>

    <!-- 错误提示 -->
    <ElAlert
      v-if="error"
      type="error"
      :title="error"
      :closable="false"
      show-icon
      class="error-alert"
    />

    <!-- 查询表单 -->
    <ElCard class="query-card" shadow="never">
      <ElForm :model="queryForm" inline>
        <ElFormItem label="排产批次" required>
          <ElInputNumber
            v-model="queryForm.runId"
            :min="1"
            :step="1"
            controls-position="right"
            style="width: 120px"
          />
        </ElFormItem>
        <ElFormItem label="部门">
          <ElSelect
            v-model="queryForm.departmentId"
            placeholder="全部（可输入编码搜索）"
            clearable
            filterable
            remote
            :remote-method="onSearchDepartment"
            :loading="searching.department"
            style="width: 190px"
          >
            <ElOption
              v-for="opt in departmentOptionsWithId"
              :key="opt.id"
              :label="opt.label"
              :value="opt.id"
            />
          </ElSelect>
        </ElFormItem>
        <ElFormItem label="工序">
          <ElInput
            v-model="queryForm.operationCode"
            placeholder="工序码（如 OP20）"
            clearable
            style="width: 160px"
          />
        </ElFormItem>
        <ElFormItem label="设备">
          <ElSelect
            v-model="queryForm.resourceId"
            placeholder="全部（可输入编码搜索）"
            clearable
            filterable
            remote
            :remote-method="onSearchResource"
            :loading="searching.resource"
            style="width: 190px"
          >
            <ElOption
              v-for="opt in resourceOptionsWithId"
              :key="opt.id"
              :label="opt.label"
              :value="opt.id"
            />
          </ElSelect>
        </ElFormItem>
        <ElFormItem label="产品">
          <ElSelect
            v-model="queryForm.materialId"
            placeholder="全部（可输入编码搜索）"
            clearable
            filterable
            remote
            :remote-method="onSearchMaterial"
            :loading="searching.material"
            style="width: 190px"
          >
            <ElOption
              v-for="opt in materialOptionsWithId"
              :key="opt.id"
              :label="opt.label"
              :value="opt.id"
            />
          </ElSelect>
        </ElFormItem>
        <ElFormItem>
          <ElButton type="primary" :loading="listLoading" @click="loadStats">
            <Icon icon="vi-ep:search" class="mr-4" />
            查询
          </ElButton>
          <ElButton @click="uncoveredStore.reset()">重置</ElButton>
        </ElFormItem>
      </ElForm>
    </ElCard>

    <!-- KPI 条 -->
    <ElRow :gutter="16" class="kpi-row">
      <ElCol :span="6">
        <ElCard shadow="never" class="kpi-card">
          <div class="kpi-value">{{ statCount }}</div>
          <div class="kpi-label">未覆盖转换对</div>
        </ElCard>
      </ElCol>
      <ElCol :span="6">
        <ElCard shadow="never" class="kpi-card">
          <div class="kpi-value">{{ Object.keys(uncoveredStore.statsByDepartment).length }}</div>
          <div class="kpi-label">涉及部门</div>
        </ElCard>
      </ElCol>
      <ElCol :span="6">
        <ElCard shadow="never" class="kpi-card">
          <div class="kpi-value">{{ Object.keys(uncoveredStore.statsByStage).length }}</div>
          <div class="kpi-label">涉及大工艺</div>
        </ElCard>
      </ElCol>
      <ElCol :span="6">
        <ElCard shadow="never" class="kpi-card">
          <div class="kpi-value danger">0 分钟</div>
          <div class="kpi-label">兜底换型时间</div>
        </ElCard>
      </ElCol>
    </ElRow>

    <!-- 换型来源 4 态说明（标签取自 SETUP_SOURCE_META，与 Gantt.vue Drawer 同一份） -->
    <ElAlert type="info" :closable="false" show-icon class="legend-alert">
      <template #title>
        换型来源四态说明（本页统计仅含「无规则兜底」：有前后产品但无规则 → 0 分钟兜底；
        「初始设备状态」：班头首单 / 冷启动，属正常生产事实，<strong>不进</strong>未覆盖统计）
      </template>
      <div class="legend-row">
        <ElTag
          v-for="s in SETUP_SOURCES"
          :key="s"
          size="small"
          :type="SETUP_SOURCE_META[s].tag"
          effect="dark"
        >
          {{ SETUP_SOURCE_META[s].label }}
        </ElTag>
      </div>
      <div class="legend-hint">
        本统计不记录前产品，所以行内只能跳「默认换型规则」页补规则；
        要补明确产品转换，请到「明确产品转换」页手工新增。
      </div>
    </ElAlert>

    <!-- 统计列表 -->
    <ElCard class="table-card" shadow="never">
      <template #header>
        <div class="card-header">
          <span>未覆盖的产品转换</span>
          <ElButton
            type="success"
            size="small"
            :disabled="statCount === 0"
            @click="handleExportCsv"
          >
            <Icon icon="vi-ep:download" class="mr-4" />
            导出 CSV
          </ElButton>
        </div>
      </template>

      <ElTable
        v-loading="listLoading"
        :data="stats"
        border
        stripe
        empty-text="无未覆盖的产品转换对（所有转换均已命中明确产品转换规则或默认换型规则）"
      >
        <ElTableColumn type="index" label="#" width="50" />
        <ElTableColumn prop="departmentCode" label="部门" width="120" />
        <ElTableColumn prop="stageCode" label="大工艺" width="120">
          <template #default="{ row }">
            {{ row.stageCode || '—' }}
          </template>
        </ElTableColumn>
        <ElTableColumn prop="operationCode" label="当前工序" width="120" />
        <ElTableColumn prop="resourceCode" label="设备" width="120" />
        <ElTableColumn prop="materialCode" label="产品" width="140" />
        <ElTableColumn label="规则集版本" min-width="190">
          <template #header>
            <ElTooltip
              placement="top"
              content="该批统计所属的规则集版本（来自 Run 绑定的策略包）；「—」= 未关联策略包版本或关联链已断"
            >
              <span class="th-hint">规则集版本</span>
            </ElTooltip>
          </template>
          <template #default="{ row }">
            <span :class="{ 'cell-muted': row.ruleSetVersionId == null }">
              {{ ruleSetVersionLabel(row.ruleSetVersionId) }}
            </span>
          </template>
        </ElTableColumn>
        <ElTableColumn prop="count" label="出现次数" width="100" align="right">
          <template #default="{ row }">
            <ElTag
              :type="row.count > 10 ? 'danger' : row.count > 5 ? 'warning' : 'info'"
              size="small"
            >
              {{ row.count }}
            </ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn label="操作" width="160" fixed="right">
          <template #default="{ row }">
            <ElButton type="warning" size="small" link @click="goToDefault(row)">
              去补默认换型规则
            </ElButton>
          </template>
        </ElTableColumn>
      </ElTable>

      <ElEmpty v-if="!listLoading && statCount === 0" description="无未覆盖的产品转换对" />
    </ElCard>
  </div>
</template>

<style scoped>
.setup-uncovered-page {
  padding: 16px;
}

.page-header {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  margin-bottom: 16px;
}

.header-left h2 {
  margin: 0 0 4px 0;
  font-size: 20px;
  font-weight: 600;
  color: var(--el-text-color-primary);
}

.header-left .subtitle {
  margin: 0;
  font-size: 13px;
  color: var(--el-text-color-secondary);
}

.error-alert {
  margin-bottom: 16px;
}

.query-card {
  margin-bottom: 16px;
}

.kpi-row {
  margin-bottom: 16px;
}

.kpi-card {
  text-align: center;
}

.th-hint {
  border-bottom: 1px dashed var(--el-text-color-placeholder);
  cursor: help;
}

.cell-muted {
  color: var(--el-text-color-placeholder);
}

.kpi-value {
  font-size: 28px;
  font-weight: 700;
  color: var(--el-color-primary);
  margin-bottom: 4px;
}

.kpi-value.danger {
  color: var(--el-color-danger);
}

.kpi-label {
  font-size: 13px;
  color: var(--el-text-color-secondary);
}

.table-card {
  margin-bottom: 16px;
}

.card-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.legend-hint {
  margin-top: 6px;
  font-size: 13px;
  color: var(--el-text-color-regular);
}

.mr-4 {
  margin-right: 4px;
}
</style>
