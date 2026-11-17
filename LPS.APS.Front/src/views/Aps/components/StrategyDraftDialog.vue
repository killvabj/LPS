<!--
  APS V1 4号位 — 策略 DRAFT 创建/编辑 Dialog

  复用 Rules.vue Publish Dialog 模式（changeReason + ElAlert + 二次确认），扩展为完整 DRAFT 表单
  字段（与后端 StrategyProfileVersion 实体对齐）：
    - strategyProfileId（隐藏，外部传）
    - versionCode        必填
    - ruleSetVersionId   必填，int —— 通过 RuleSet → RuleSetVersion 双级 ElSelect 选择
    - parameterSetVersionId 必填，int —— 通过 ParameterSet → ParameterSetVersion 双级 ElSelect 选择
    - effectiveFrom      可选（预设生效起；留空 = 发布时后端自动记发布时间）
    - effectiveTo        可选（预设生效止；留空 = 停用时后端自动记停用时刻）
    - IsDefault 不在本表单内：Create 强制 false、Update 冻结既有值（C-7），
      默认只能对 PUBLISHED 版本在列表点「设为默认」显式置位

  v1.6 4号位 补登（历史引用 v1.4 §十八.10）修复 P1-12 反馈 500 Internal Server Error：
    原 ElInputNumber 让用户自由填 ID；FK_StrategyProfileVersion_RuleSetVersion 不通过即 SqlException → 500。
    现改 ElSelect 双级级联：必须先选主表 → 才能选版本；版本必须存在，避免 FK 违反。

  模式：
    - mode=create：创建 DRAFT，提交后仅调 POST /version
    - mode=edit：  编辑 DRAFT，提交后仅调 PUT /version/{id}

  提交成功通过 emit('submit') 抛回 StrategyVersionDraftInput，由父组件决定调 store.createDraft / updateDraft

  10-14（3号位 A组 A5）：编辑态一级下拉不再逐个主表串行拉版本反查父 Id（dev 实测 698 主表 → 698 次请求）。
    改读 versions 列表 DTO 自带的 ruleSetId / parameterSetId，直接赋值给 form，watch 照旧拉该主表的版本列表。
    版本加载器改为「已选版本仍属该主表则保留」——编辑态回填因此不会丢选中值。

  10-15：一级「规则集」下拉只列已配版本的（原来列全量，选中后二级下拉空——dev 698 条里 695 条无版本行）。
    取数不再带查询参数（带 pageSize 时后端硬顶 500 且顺序不保证，有版本的 id 1 / 538 被截掉）。

  10-15（3号位 Q1 落码后）：一级「参数集」下拉与规则集同款处理——
    ① 取数去掉 pageSize（硬顶 500，dev 实测有版本的 PS_DEMO(286) 排在第 571 位会被截掉）；
    ② 只列 status 非空者（722 条里 719 条无版本行，选中后二级下拉必空）。
    判「配没配版本」的依据是后端新加的版本摘要字段 status，dev 已实测生效。

  10-15（用户报「选参数集后版本接口报错」）：PS-PROD(1305) 的 versions 端点稳定 37s
    （后端 SELECT * 读大 LOB，见 listParameterSetVersions 注释与致 3号位 函），
    30s 默认超时下必然报 timeout —— 该调用单独放宽到 60s。四处加载失败提示一并去 axios 原文。

  11-11（3号位 甲回执「补回填已落码」）：后端 PUT 读改写回填 EffectiveFrom/EffectiveTo，
    编辑态改窗口会被后端丢弃（静默无效）⇒ 编辑态禁用两个日期选择器并加说明，
    窗口只能在新建时确定。
  11-11（0号位/PM 再裁定，经 3号位《生效窗写入口径升级_发布写From停用写To_更正乙裁_回执》）：
    生效窗改为**生命周期窗口** —— 发布时后端写 EffectiveFrom（发布时间）、
    停用时写 EffectiveTo（停用时刻）；3号位 10-11 的「NULL = 常开窗」口径作废。
    ⇒ 留空不再是「不限 / 永久」，而是「发布时自动记发布时间 / 停用时自动记停用时刻」。

  11-11（用户拍板）：策略包草稿的「规则集版本 / 参数集版本」下拉**只列已发布版本**——
    发布策略包版本时后端校验绑定版本必须 PUBLISHED（P0-06 引用合法性），
    把草稿/已停用版摆出来只是让人选一个发不出去的东西。
    例外：编辑态当前绑的那一版若不是已发布（当年绑了草稿版、或后来被停用），
    仍留在列表里并显示真实状态，避免保存时把它悄悄解绑。
-->
<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import {
  ElAlert,
  ElButton,
  ElDatePicker,
  ElDialog,
  ElForm,
  ElFormItem,
  ElInput,
  ElMessage,
  ElOption,
  ElSelect
} from 'element-plus'
import {
  strategyProfileApi,
  type ParameterSetDto,
  type ParameterSetVersionDto,
  type RuleSetDto,
  type RuleSetVersionDto,
  type StrategyProfileDto,
  type StrategyProfileVersionDto,
  type StrategyVersionDraftInput,
  type StrategyVersionStatus
} from '@/api/aps-v1'
import { formatEffectiveFrom, formatEffectiveTo } from '@/utils/effectiveWindow'

const props = defineProps<{
  visible: boolean
  mode: 'create' | 'edit'
  /** 当前选中的 Profile（Dialog 上下文） */
  profile: StrategyProfileDto | null
  /** 编辑模式时：当前版本（创建模式可空） */
  version?: StrategyProfileVersionDto | null
  /** 提交中（父组件 loading 状态） */
  submitting?: boolean
}>()

const emit = defineEmits<{
  'update:visible': [v: boolean]
  submit: [input: StrategyVersionDraftInput]
}>()

interface DraftForm {
  versionCode: string
  ruleSetId: number | undefined
  ruleSetVersionId: number | undefined
  parameterSetId: number | undefined
  parameterSetVersionId: number | undefined
  effectiveFrom: string | undefined
  effectiveTo: string | undefined
}

const form = reactive<DraftForm>({
  versionCode: '',
  ruleSetId: undefined,
  ruleSetVersionId: undefined,
  parameterSetId: undefined,
  parameterSetVersionId: undefined,
  effectiveFrom: undefined,
  effectiveTo: undefined
})

const formRef = ref<InstanceType<typeof ElForm> | null>(null)

/* ===== 下拉源数据 ===== */
const ruleSets = ref<RuleSetDto[]>([])
const ruleSetVersions = ref<RuleSetVersionDto[]>([])
const parameterSets = ref<ParameterSetDto[]>([])
const parameterSetVersions = ref<ParameterSetVersionDto[]>([])

const ruleSetsLoading = ref(false)
const ruleSetVersionsLoading = ref(false)
const parameterSetsLoading = ref(false)
const parameterSetVersionsLoading = ref(false)

async function loadRuleSets(): Promise<void> {
  ruleSetsLoading.value = true
  try {
    // 10-15：不下任何查询参数。两个原因——
    //   ① 带 pageSize 时后端硬顶 500 条且顺序不保证，dev 实测 698 条里只回 id 629~1802，
    //      唯一有版本的 RS_DEMO(538) 和 TEST-RS(1) 都被截掉，策略包永远绑不上；不带参数回全量 698。
    //   ② 只列「已配版本」的：dev 698 条里 695 条根本没有版本行，选中后第二级下拉必空，
    //      用户无从预判。判定口径同 rule.ts 的 hasVersion——status 在不在就是唯一事实。
    const all: RuleSetDto[] = await strategyProfileApi.listRuleSets()
    ruleSets.value = all.filter((r) => r.isActive && r.status != null && r.status !== '')
  } catch {
    // 10-15：不再把 axios 原文（如 timeout of 30000ms exceeded）抛给用户，大白话即可；
    // 原始错误在 F12 Network 里查。
    ElMessage.error('加载规则集主表失败，请稍后重试')
  } finally {
    ruleSetsLoading.value = false
  }
}

async function loadRuleSetVersions(ruleSetId: number | undefined): Promise<void> {
  const prev = form.ruleSetVersionId
  ruleSetVersions.value = []
  if (ruleSetId === undefined) {
    form.ruleSetVersionId = undefined
    return
  }
  ruleSetVersionsLoading.value = true
  try {
    const list: RuleSetVersionDto[] = await strategyProfileApi.listRuleSetVersions(ruleSetId)
    // 11-11：下拉只列已发布版本——策略包版本发布时后端校验绑定版本必须 PUBLISHED（P0-06），
    // 把草稿/已停用版摆出来只是让人选一个发不出去的东西。
    // 例外：编辑态当前绑的那一版若不是已发布（当年绑了草稿版、或后来被停用），
    // 仍留在列表里并显示真实状态，避免保存时把它悄悄解绑。
    ruleSetVersions.value = list.filter((v) => v.status === 'PUBLISHED' || v.id === prev)
    // 已选版本仍属该主表则保留（编辑态回填走这条）；否则清空——版本必须与主表匹配，否则 FK 违反
    form.ruleSetVersionId =
      prev != null && ruleSetVersions.value.some((v) => v.id === prev) ? prev : undefined
  } catch {
    ElMessage.error('加载规则集版本失败，请稍后重试')
    form.ruleSetVersionId = undefined
  } finally {
    ruleSetVersionsLoading.value = false
  }
}

async function loadParameterSets(): Promise<void> {
  parameterSetsLoading.value = true
  try {
    // 10-15：与 loadRuleSets 同款两条处理。
    //   ① 不带 pageSize：后端 pageSize 硬顶 500，dev 实测 722 条里带版本的 PS_DEMO(286) 排在第 571 位，
    //      带上 500 会被截掉——与 10-15 规则集下拉同一个坑（当时 RS_DEMO(538) 就是这么丢的）。
    //   ② 只列「已配版本」的：722 条里 719 条根本没有版本行，选中后第二级下拉必空，用户无从预判。
    const all: ParameterSetDto[] = await strategyProfileApi.listParameterSets({ activeOnly: true })
    parameterSets.value = all.filter((p) => p.isActive && p.status != null && p.status !== '')
  } catch {
    ElMessage.error('加载参数集主表失败，请稍后重试')
  } finally {
    parameterSetsLoading.value = false
  }
}

async function loadParameterSetVersions(parameterSetId: number | undefined): Promise<void> {
  const prev = form.parameterSetVersionId
  parameterSetVersions.value = []
  if (parameterSetId === undefined) {
    form.parameterSetVersionId = undefined
    return
  }
  parameterSetVersionsLoading.value = true
  try {
    const list: ParameterSetVersionDto[] =
      await strategyProfileApi.listParameterSetVersions(parameterSetId)
    // 同 loadRuleSetVersions：只列已发布版本（发布策略包版本时后端要求绑定版本为 PUBLISHED），
    // 例外保留当前绑的那一版，避免编辑时把它悄悄解绑。
    parameterSetVersions.value = list.filter((v) => v.status === 'PUBLISHED' || v.id === prev)
    form.parameterSetVersionId =
      prev != null && parameterSetVersions.value.some((v) => v.id === prev) ? prev : undefined
  } catch {
    ElMessage.error('加载参数集版本失败，请稍后重试；若一直失败，请联系系统管理员')
    form.parameterSetVersionId = undefined
  } finally {
    parameterSetVersionsLoading.value = false
  }
}

onMounted(() => {
  loadRuleSets()
  loadParameterSets()
})

/** 选 RuleSet 时加载其下属版本（loader 负责清掉不再匹配的旧版本） */
watch(
  () => form.ruleSetId,
  (newId, oldId) => {
    if (newId === oldId) return
    void loadRuleSetVersions(newId)
  }
)

/** 选 ParameterSet 时清空 ParameterSetVersion 并加载下属版本 */
watch(
  () => form.parameterSetId,
  (newId, oldId) => {
    if (newId === oldId) return
    void loadParameterSetVersions(newId)
  }
)

/** 打开 / 切换模式时重置表单 */
watch(
  () => [props.visible, props.mode, props.version?.id],
  async ([vis]) => {
    if (!vis) return
    if (props.mode === 'edit' && props.version) {
      form.versionCode = props.version.versionCode
      form.ruleSetVersionId = props.version.ruleSetVersionId
      form.parameterSetVersionId = props.version.parameterSetVersionId
      form.effectiveFrom = props.version.effectiveFrom ?? undefined
      form.effectiveTo = props.version.effectiveTo ?? undefined
      // 10-14（A5）：列表 DTO 已带 ruleSetId / parameterSetId，一级下拉直接定位父主表，
      // 由 watch 触发版本加载（loader 会保留仍属该主表的已选版本）——零额外请求。
      if (props.version.ruleSetId != null) form.ruleSetId = props.version.ruleSetId
      if (props.version.parameterSetId != null) form.parameterSetId = props.version.parameterSetId
    } else {
      form.versionCode = ''
      form.ruleSetId = undefined
      form.ruleSetVersionId = undefined
      form.parameterSetId = undefined
      form.parameterSetVersionId = undefined
      form.effectiveFrom = undefined
      form.effectiveTo = undefined
      ruleSetVersions.value = []
      parameterSetVersions.value = []
    }
  },
  { immediate: true }
)

/** 当前选中的两个版本里，哪些还不是「已发布」（发布策略版本时会被后端拦） */
const unpublishedSelection = computed<string[]>(() => {
  const out: string[] = []
  const rv = ruleSetVersions.value.find((v) => v.id === form.ruleSetVersionId)
  if (rv && rv.status !== 'PUBLISHED')
    out.push(`规则集版本「${rv.versionCode}」是${versionStatusLabel[rv.status] ?? rv.status}`)
  const pv = parameterSetVersions.value.find((v) => v.id === form.parameterSetVersionId)
  if (pv && pv.status !== 'PUBLISHED')
    out.push(`参数集版本「${pv.versionCode}」是${versionStatusLabel[pv.status] ?? pv.status}`)
  return out
})

const rules = computed(() => ({
  versionCode: [
    { required: true, message: '请输入版本号', trigger: 'blur' },
    { min: 3, max: 64, message: '长度 3-64 字符', trigger: 'blur' }
  ],
  ruleSetId: [{ required: true, message: '请选择规则集', trigger: 'change' }],
  ruleSetVersionId: [{ required: true, message: '请选择规则集版本', trigger: 'change' }],
  parameterSetId: [{ required: true, message: '请选择参数集', trigger: 'change' }],
  parameterSetVersionId: [{ required: true, message: '请选择参数集版本', trigger: 'change' }]
}))

const title = computed<string>(() =>
  props.mode === 'create' ? '新建草稿版本' : `编辑草稿版本 ${props.version?.versionCode ?? ''}`
)

function close(): void {
  emit('update:visible', false)
}

const versionStatusLabel: Record<StrategyVersionStatus, string> = {
  DRAFT: '草稿',
  SUBMITTED: '已提交',
  APPROVED: '已批准',
  PUBLISHED: '已发布',
  DISABLED: '已退役',
  ARCHIVED: '已归档'
}

async function onSubmit(): Promise<void> {
  if (!formRef.value) return
  const valid = await formRef.value.validate().catch(() => false)
  if (!valid) return
  if (!props.profile) return
  const input: StrategyVersionDraftInput = {
    strategyProfileId: props.profile.id,
    versionCode: form.versionCode.trim(),
    ruleSetVersionId: form.ruleSetVersionId!,
    parameterSetVersionId: form.parameterSetVersionId!,
    effectiveFrom: form.effectiveFrom ?? null,
    effectiveTo: form.effectiveTo ?? null
  }
  emit('submit', input)
}
</script>

<template>
  <ElDialog
    :model-value="visible"
    :title="title"
    width="640px"
    :close-on-click-modal="false"
    :close-on-press-escape="!submitting"
    @update:model-value="(v) => emit('update:visible', v)"
  >
    <ElForm ref="formRef" :model="form" :rules="rules" label-width="140px" label-position="right">
      <ElFormItem label="所属策略包">
        <span class="d-val">{{ profile?.strategyProfileCode ?? '—' }}</span>
        <span class="d-sub">（{{ profile?.strategyProfileName }}）</span>
      </ElFormItem>

      <ElFormItem label="版本号" prop="versionCode">
        <ElInput
          v-model="form.versionCode"
          placeholder="如 v1.2.0 或 v1.2.0-draft"
          :disabled="mode === 'edit'"
        />
      </ElFormItem>

      <ElFormItem label="规则集" prop="ruleSetId">
        <ElSelect
          v-model="form.ruleSetId"
          placeholder="先选规则集主表"
          no-data-text="还没有配过版本的规则集，请先到「规则维护」页新建草稿"
          filterable
          :loading="ruleSetsLoading"
          style="width: 100%"
        >
          <ElOption
            v-for="rs in ruleSets"
            :key="rs.id"
            :value="rs.id"
            :label="`${rs.ruleSetCode} — ${rs.ruleSetName}`"
          />
        </ElSelect>
      </ElFormItem>

      <ElFormItem label="规则集版本" prop="ruleSetVersionId">
        <ElSelect
          v-model="form.ruleSetVersionId"
          placeholder="先选规则集再选版本"
          no-data-text="这个规则集还没有已发布版本，请先到「规则维护」页发布一版"
          filterable
          :loading="ruleSetVersionsLoading"
          :disabled="form.ruleSetId === undefined"
          style="width: 100%"
        >
          <ElOption
            v-for="v in ruleSetVersions"
            :key="v.id"
            :value="v.id"
            :label="
              v.status === 'PUBLISHED'
                ? v.versionCode
                : `${v.versionCode}（${versionStatusLabel[v.status] ?? v.status}）`
            "
          />
        </ElSelect>
      </ElFormItem>

      <ElFormItem label="参数集" prop="parameterSetId">
        <ElSelect
          v-model="form.parameterSetId"
          placeholder="先选参数集主表"
          no-data-text="还没有配过版本的参数集，请先到「规则维护」页新建草稿"
          filterable
          :loading="parameterSetsLoading"
          style="width: 100%"
        >
          <ElOption
            v-for="ps in parameterSets"
            :key="ps.id"
            :value="ps.id"
            :label="`${ps.parameterSetCode} — ${ps.parameterSetName}`"
          />
        </ElSelect>
      </ElFormItem>

      <ElFormItem label="参数集版本" prop="parameterSetVersionId">
        <ElSelect
          v-model="form.parameterSetVersionId"
          placeholder="先选参数集再选版本"
          no-data-text="这个参数集还没有已发布版本，请先到「规则维护」页发布一版"
          filterable
          :loading="parameterSetVersionsLoading"
          :disabled="form.parameterSetId === undefined"
          style="width: 100%"
        >
          <ElOption
            v-for="v in parameterSetVersions"
            :key="v.id"
            :value="v.id"
            :label="
              v.status === 'PUBLISHED'
                ? v.versionCode
                : `${v.versionCode}（${versionStatusLabel[v.status] ?? v.status}）`
            "
          />
        </ElSelect>
      </ElFormItem>

      <ElFormItem label="生效起">
        <ElDatePicker
          v-if="mode === 'create'"
          v-model="form.effectiveFrom"
          type="datetime"
          placeholder="可选（留空 = 发布时自动记发布时间）"
          format="YYYY-MM-DD HH:mm:ss"
          value-format="YYYY-MM-DDTHH:mm:ss[Z]"
          style="width: 100%"
        />
        <span v-else class="d-val">{{ formatEffectiveFrom(props.version?.effectiveFrom) }}</span>
      </ElFormItem>

      <ElFormItem label="生效止">
        <ElDatePicker
          v-if="mode === 'create'"
          v-model="form.effectiveTo"
          type="datetime"
          placeholder="可选（留空 = 停用时自动记停用时刻）"
          format="YYYY-MM-DD HH:mm:ss"
          value-format="YYYY-MM-DDTHH:mm:ss[Z]"
          style="width: 100%"
        />
        <span v-else class="d-val">{{
          formatEffectiveTo(props.version?.effectiveTo, props.version?.status)
        }}</span>
      </ElFormItem>

      <!-- 11-11（3号位 甲回执）：PUT 读改写回填既有生效窗 ⇒ 编辑态改了也不落库。
           与其让人填了以为改成功，不如直接只读并说清楚。窗口只能新建时定。 -->
      <ElAlert v-if="mode === 'edit'" type="info" :closable="false" show-icon class="d-alert">
        <template #title>生效窗口在版本创建后不可更改</template>
        生效窗 =
        这一版的生命周期：发布时后端自动写入「生效起」（发布时间），停用时自动写入「生效止」（停用时刻）。保存草稿时后端会保留这个版本原有的窗口，此处改不动；要换窗口请新建一个版本。
      </ElAlert>

      <!-- 绑定未发布版本：保存能过，但发布策略版本时会被后端拦下（P0-06 引用合法性）
           ⇒ 提前说清楚，别让用户到发布那一步才发现 -->
      <ElAlert
        v-if="unpublishedSelection.length > 0"
        type="warning"
        :closable="false"
        show-icon
        class="d-alert"
      >
        <template #title>所选版本尚未发布</template>
        {{
          unpublishedSelection.join('；')
        }}。下拉里一般只列已发布版本，出现这一条说明它是在你选完之后才被停用或退回草稿的。
        可以先存草稿，但发布策略包版本前必须先把它恢复成「已发布」，否则发布校验会被拦下（绑定版本必须为已发布）。
      </ElAlert>

      <ElAlert type="info" :closable="false" show-icon class="d-alert">
        <template #title>状态流转</template>
        {{ mode === 'create' ? '创建后状态固定为「草稿」' : '仅「草稿」状态可编辑' }}；
        发布后版本变为「已发布」， 但不会自动成为默认 ——
        需要运行侧使用它时，请在版本列表点「设为默认」。
      </ElAlert>
    </ElForm>
    <template #footer>
      <ElButton :disabled="submitting" @click="close">取消</ElButton>
      <ElButton type="primary" :loading="submitting" @click="onSubmit">
        {{ mode === 'create' ? '创建草稿' : '保存' }}
      </ElButton>
    </template>
  </ElDialog>
</template>

<style scoped>
.d-val {
  font-weight: 600;
  margin-right: 6px;
}
.d-sub {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
.d-alert {
  margin-top: 12px;
}
</style>
