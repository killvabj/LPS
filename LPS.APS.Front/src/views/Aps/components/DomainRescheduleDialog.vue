<!--
  APS V1 4号位 — §10A.5 整 Domain 人工重排（DOMAIN_MANUAL_RESCHEDULE）

  冻结依据：《APS_V1_4号位页面与业务操作开发实施包 v1.4》§十A（L595-672）
  契约：MANUAL_RESCHEDULE × MANUAL_ADJUSTMENT；PriorityMode **须省略该键**（传 NORMAL/EXPEDITE 均 400）
  载荷：无多对象载荷（范围 = 整 Domain 全部可移动计划）

  Dialog 只收集载荷；父组件持 loading 并调 triggerBusinessEntry。

  2026-11-20：按 v0.2 稿 §4 P09 §4.9.4 补「这个入口在做什么」业务解释，
    并把「不可移动计划（lockMarker）」旧措辞换成「全部允许移动的计划 + 执行锁/Firm/Frozen 由后端判定」
    （依据《APS_V1_P01-P13_全页面业务设计统一收口核对稿_v0.2_20261114_四项审核修订版.md》）。
-->
<script setup lang="ts">
import { ref, watch } from 'vue'
import {
  ElAlert,
  ElButton,
  ElCheckbox,
  ElDialog,
  ElForm,
  ElFormItem,
  ElMessage,
  ElOption,
  ElSelect,
  ElTag
} from 'element-plus'
import { TRIGGER_BUSINESS_EXPLAIN, TRIGGER_RUN_MATRIX, validateScopeDraft } from '@/api/aps-v1'
import type { BusinessEntrySubmission, DomainKey, ScopeDraft } from '@/api/aps-v1'
import { useDomainStore } from '@/store/modules/aps/domain'

const props = defineProps<{
  visible: boolean
  submitting?: boolean
  domainOptions: DomainKey[]
  defaultDomainKey?: DomainKey
}>()

const emit = defineEmits<{
  'update:visible': [v: boolean]
  submit: [payload: BusinessEntrySubmission]
}>()

const TRIGGER = 'DOMAIN_MANUAL_RESCHEDULE' as const
const spec = TRIGGER_RUN_MATRIX[TRIGGER]

const domainKey = ref<DomainKey | undefined>(undefined)
/** 域中文名（域字典名称表含已停用域；查不到时回退裸 key） */
const domainStore = useDomainStore()
const confirmed = ref(false)
const errors = ref<string[]>([])

watch(
  () => props.visible,
  (vis) => {
    if (!vis) return
    errors.value = []
    confirmed.value = false
    domainKey.value = props.defaultDomainKey ?? props.domainOptions[0]
  },
  { immediate: true }
)

function close(): void {
  emit('update:visible', false)
}

function onSubmit(): void {
  if (!domainKey.value) {
    ElMessage.warning('请选择排程域')
    return
  }
  if (!confirmed.value) {
    ElMessage.warning('请勾选确认后再发起')
    return
  }
  const draft: ScopeDraft = {}
  const errs = validateScopeDraft(TRIGGER, draft)
  errors.value = errs
  if (errs.length > 0) {
    ElMessage.error('校验未通过，请检查表单')
    return
  }
  emit('submit', { trigger: TRIGGER, domainKey: domainKey.value, draft })
}
</script>

<template>
  <ElDialog
    :model-value="visible"
    :title="spec.entryTitle"
    width="640px"
    :close-on-click-modal="false"
    :close-on-press-escape="!submitting"
    @update:model-value="(v) => emit('update:visible', v)"
  >
    <ElForm label-width="120px" label-position="right">
      <ElFormItem label="排程域" required>
        <ElSelect v-model="domainKey" placeholder="选择整个排程域" style="width: 260px">
          <ElOption v-for="d in domainOptions" :key="d" :label="domainStore.label(d)" :value="d" />
        </ElSelect>
        <span class="field-hint">范围 = 该排程域全部可移动计划</span>
      </ElFormItem>

      <ElFormItem label="优先模式">
        <ElTag type="info" effect="plain">不发送该字段</ElTag>
        <span class="field-hint">整排程域重排走既有正式优先规则，无需指定优先模式</span>
      </ElFormItem>

      <ElFormItem label="重排范围">
        <span class="field-hint no-margin">
          范围 = 该排程域当前全部允许移动的计划；执行锁、Firm/Frozen 等约束由后端判定，前端不计算。
        </span>
      </ElFormItem>

      <ElFormItem label="确认">
        <ElCheckbox v-model="confirmed">
          我确认对整排程域全部可移动计划发起重排（将创建新的 ScheduleRun + CANDIDATE
          PlanVersion，不可直接激活）
        </ElCheckbox>
      </ElFormItem>

      <ElAlert v-if="errors.length > 0" type="error" :closable="false" show-icon class="d-alert">
        <template #title>校验未通过（{{ errors.length }}）</template>
        <ul class="err-list">
          <li v-for="(e, i) in errors" :key="i">{{ e }}</li>
        </ul>
      </ElAlert>

      <ElAlert type="warning" :closable="false" show-icon class="d-alert">
        <template #title>影响面</template>
        整排程域重排范围最大——产生的 Candidate 需在 Candidate 页逐版本比较确认后才可能激活； WHATIF
        / 影响分析类运行永不可激活（本入口不属该类）。
      </ElAlert>

      <ElAlert type="info" :closable="false" show-icon class="d-alert">
        <template #title>这个入口在做什么</template>
        {{ TRIGGER_BUSINESS_EXPLAIN[TRIGGER] }}
      </ElAlert>

      <ElAlert type="info" :closable="false" show-icon class="d-alert">
        <template #title>提交后</template>
        将新建一次排产运行并生成候选版本，需在「候选版本」页比较确认后才可能采用。
      </ElAlert>
    </ElForm>

    <template #footer>
      <ElButton :disabled="submitting" @click="close">取消</ElButton>
      <ElButton type="danger" :loading="submitting" :disabled="!confirmed" @click="onSubmit">
        发起整排程域重排
      </ElButton>
    </template>
  </ElDialog>
</template>

<style scoped>
.field-hint {
  margin-left: 10px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
.no-margin {
  margin-left: 0;
}
.d-alert {
  margin-top: 12px;
}
.err-list {
  margin: 0;
  padding-left: 18px;
}
</style>
