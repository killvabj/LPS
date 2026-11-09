<!--
  APS V1 4号位 — 单条窗口追加 Dialog（设备资源日历 / 人工槽日历 共用）

  依据：
    - 5号位 2026-10-14《放开一天多条已落地-更正前封口径回执》§二/§五
    - 冻结 v1.4 §21「相同级别重叠矛盾拒绝发布」→ 重叠由后端 409 拒绝，前端不预判

  与「批量铺窗」的分工（两个入口语义相反，文案必须让用户看见）：
    - 批量铺窗 = **覆盖**该日期区间：原有窗口先删后按新时段重建
    - 本弹窗   = **只追加**这一段：不动当天已有窗口；与已有窗口重叠会被拒绝

  契约：
    - 设备：POST /api/resource-calendar/slot          body { resourceId, startTime, endTime, availableFlag, remark }
    - 人工：POST /api/manual-capacity/calendar/single body { manualSlotId, ... }
    - startTime/endTime 为**完整时间** `2026-11-02T08:00:00`（后端 DateTime，非 TimeSpan）
    - 不跨天；相切（12:00 收尾接 12:00 起）合法
-->
<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import {
  ElAlert,
  ElButton,
  ElDatePicker,
  ElDialog,
  ElForm,
  ElFormItem,
  ElInput,
  ElMessage,
  ElSwitch,
  ElTag
} from 'element-plus'
import type { FormRules } from 'element-plus'

const props = defineProps<{
  visible: boolean
  submitting?: boolean
  /** DEVICE=设备资源日历 / MANUAL_SLOT=人工能力槽日历 */
  mode: 'DEVICE' | 'MANUAL_SLOT'
  /** 对象键（resourceId / manualSlotId） */
  targetId: number | null
  /** 对象展示名（如 "MC01 注塑机-01" / "精修01（精修）"） */
  targetLabel?: string
  /** 当前对象窗口总条数（用于提示「已登记 N 段」） */
  existingTotal?: number
}>()

const emit = defineEmits<{
  'update:visible': [v: boolean]
  submit: [payload: { startTime: string; endTime: string; availableFlag: boolean; remark?: string }]
}>()

/** 时段常用预设（夜班跨天会被后端拒，故不列） */
const TIME_PRESETS = [
  { label: '白班 08:00-17:00', start: '08:00:00', end: '17:00:00' },
  { label: '上午 08:00-12:00', start: '08:00:00', end: '12:00:00' },
  { label: '下午 13:00-17:00', start: '13:00:00', end: '17:00:00' }
]

const TIME_RE = /^([01]\d|2[0-3]):[0-5]\d:[0-5]\d$/

const formRef = ref<InstanceType<typeof ElForm> | null>(null)

const form = ref<{
  date: string
  startTime: string
  endTime: string
  availableFlag: boolean
  remark: string
}>({
  date: '',
  startTime: '08:00:00',
  endTime: '12:00:00',
  availableFlag: true,
  remark: ''
})

const rules = computed<FormRules>(() => ({
  date: [{ required: true, message: '请选择日期', trigger: 'change' }],
  startTime: [
    {
      required: true,
      validator: (_r, v, cb) => {
        if (!TIME_RE.test(String(v))) {
          cb(new Error('时间格式须为 HH:mm:ss（如 08:00:00）'))
          return
        }
        cb()
      },
      trigger: 'blur'
    }
  ],
  endTime: [
    {
      required: true,
      validator: (_r, v, cb) => {
        const end = String(v)
        if (!TIME_RE.test(end)) {
          cb(new Error('时间格式须为 HH:mm:ss（如 12:00:00）'))
          return
        }
        if (end <= form.value.startTime) {
          cb(new Error(`结束时间须晚于开始时间（${form.value.startTime}）；一个窗口不能跨天`))
          return
        }
        cb()
      },
      trigger: 'blur'
    }
  ]
}))

const modeLabel = computed(() => (props.mode === 'DEVICE' ? '设备资源日历' : '人工能力槽日历'))

function applyPreset(preset: (typeof TIME_PRESETS)[number]): void {
  form.value.startTime = preset.start
  form.value.endTime = preset.end
}

watch(
  () => props.visible,
  (vis) => {
    if (!vis) return
    form.value = {
      date: '',
      startTime: '08:00:00',
      endTime: '12:00:00',
      availableFlag: true,
      remark: ''
    }
    formRef.value?.clearValidate()
  },
  { immediate: true }
)

function close(): void {
  emit('update:visible', false)
}

async function onSubmit(): Promise<void> {
  if (props.targetId == null || props.targetId <= 0) {
    ElMessage.error('对象无效（未选中）')
    return
  }
  if (!formRef.value) return
  const valid = await formRef.value.validate().catch(() => false)
  if (!valid) {
    ElMessage.warning('表单未通过校验，请检查红色提示项')
    return
  }
  emit('submit', {
    startTime: `${form.value.date}T${form.value.startTime}`,
    endTime: `${form.value.date}T${form.value.endTime}`,
    availableFlag: form.value.availableFlag,
    remark: form.value.remark?.trim() ? form.value.remark.trim() : undefined
  })
}
</script>

<template>
  <ElDialog
    :model-value="visible"
    :title="`新增一段时段 —— ${modeLabel}`"
    width="720px"
    :close-on-click-modal="false"
    :close-on-press-escape="!submitting"
    @update:model-value="(v) => emit('update:visible', v)"
  >
    <ElForm ref="formRef" :model="form" :rules="rules" label-width="120px" label-position="right">
      <ElFormItem label="对象">
        <ElTag type="primary" effect="plain">{{ targetLabel || '（未选择）' }}</ElTag>
        <span class="field-hint">
          已登记 {{ existingTotal ?? 0 }} 段（同一天可以有多个不重叠时段）
        </span>
      </ElFormItem>

      <ElFormItem label="日期" prop="date" required>
        <ElDatePicker
          v-model="form.date"
          type="date"
          value-format="YYYY-MM-DD"
          placeholder="选择日期"
          style="width: 220px"
        />
      </ElFormItem>

      <ElFormItem label="时段">
        <div class="preset-row">
          <ElButton
            v-for="p in TIME_PRESETS"
            :key="p.label"
            size="small"
            plain
            @click="applyPreset(p)"
          >
            {{ p.label }}
          </ElButton>
        </div>
      </ElFormItem>

      <ElFormItem label="开始时间" prop="startTime" required>
        <ElInput v-model="form.startTime" placeholder="08:00:00" style="width: 160px" />
      </ElFormItem>

      <ElFormItem label="结束时间" prop="endTime" required>
        <ElInput v-model="form.endTime" placeholder="12:00:00" style="width: 160px" />
        <span class="field-hint">须晚于开始时间；一个窗口不能跨天</span>
      </ElFormItem>

      <ElFormItem label="可用标记">
        <ElSwitch v-model="form.availableFlag" active-text="可用" inactive-text="禁用" />
      </ElFormItem>

      <ElFormItem label="备注">
        <ElInput
          v-model="form.remark"
          placeholder="如：上午班 / 加班 / 临时维护"
          maxlength="200"
          show-word-limit
          style="width: 420px"
        />
      </ElFormItem>

      <ElAlert type="success" :closable="false" show-icon class="d-alert">
        <template #title>只新增这一段，不改动当天其他时段</template>
        与当天已有时间段<strong>重叠</strong>会被拒绝（首尾正好相接是允许的）。
        如需把整天改为另一个时段，请用「批量铺窗」。
      </ElAlert>
    </ElForm>

    <template #footer>
      <ElButton :disabled="submitting" @click="close">取消</ElButton>
      <ElButton type="primary" :loading="submitting" @click="onSubmit">新增这一段</ElButton>
    </template>
  </ElDialog>
</template>

<style scoped>
.field-hint {
  margin-left: 10px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
.d-alert {
  margin-top: 12px;
}
</style>
