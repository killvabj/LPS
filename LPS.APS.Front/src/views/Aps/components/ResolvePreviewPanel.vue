<!--
  APS V1 4号位 — 三级合并后可排时段 可视区（设备 / 人工槽共用）

  依据：
    - 冻结 13 v1.8:31：资源日历必须明文展示「继承上级 / 本级明确工作 / 本级明确不工作」及**来源授权**
    - 冻结 v1.4 §17/§19/§23（覆盖 v1.3 §九）：本级无窗口 ≠ 不可排，本级 MISSING 允许继承上级已认证窗口
    - 后端 DTO：`ResourceCalendarResolvePreviewDto` / `ManualCapacityResolvePreviewDto`
      （lps/.../Dto/ResourceCalendarDtos.cs:143 / :179）—— 两者同构，故共用本组件
    - 后端端点：GET /api/resource-calendar/resolve-preview（设备）
                GET /api/manual-capacity/resolve-preview（人工槽，5号位 10-23 落码 / 10-26 上 dev）

  三态（与后端 Status 一一对应，不自造）：
    WORK    有合法窗口（每片 source 说明继承自工厂/部门还是本级）
    OFF     本级明确停机（显式关闭/维修，上级 WORK 也不复活）
    MISSING 本级没登记，也没从上级解析出窗口

  ⚠️ 只读：预览不写库、不改任何日历数据。
-->
<script setup lang="ts">
import { computed } from 'vue'
import { ElButton, ElDatePicker, ElEmpty, ElTable, ElTableColumn, ElTag } from 'element-plus'
import type { ResolveStatus, ResolveWindowSource, ResolvedWindowDto } from '@/api/aps-v1'

const props = defineProps<{
  /** 预览结果；null = 还没查过 / 查不到（越界被 fail-closed 剔除） */
  preview: {
    status: ResolveStatus
    hasAnyWindow: boolean
    windows: ResolvedWindowDto[]
  } | null
  loading?: boolean
  /** 查询区间（YYYY-MM-DD ~ YYYY-MM-DD，endDate 含末日） */
  range: [string, string] | undefined
  /** 未查证时的空态文案（区分设备 / 人工槽） */
  emptyText: string
}>()

const emit = defineEmits<{
  'update:range': [v: [string, string] | undefined]
  refresh: []
}>()

const STATUS_META: Record<
  ResolveStatus,
  { label: string; tag: 'success' | 'danger' | 'info'; desc: string }
> = {
  WORK: { label: '可排', tag: 'success', desc: '三级合并后存在合法可排时段' },
  OFF: {
    label: '本级明确停机',
    tag: 'danger',
    desc: '本级显式关闭/维修，上级开放也不会复活'
  },
  MISSING: {
    label: '未登记',
    tag: 'info',
    desc: '本级没登记，也没从工厂/部门继承到窗口'
  }
}

const SOURCE_META: Record<ResolveWindowSource, { label: string; tag: 'warning' | 'primary' }> = {
  Factory: { label: '继承自工厂', tag: 'warning' },
  Department: { label: '继承自部门', tag: 'warning' },
  Resource: { label: '本级', tag: 'primary' }
}

/** 同一日内只显示 "08:00 ~ 17:00"；跨日才显示完整时间（窗口本不应跨天，兜底但不误导） */
function windowRange(w: ResolvedWindowDto): string {
  if (!w?.start || !w?.end) return '—'
  const sameDay = w.start.slice(0, 10) === w.end.slice(0, 10)
  return sameDay
    ? `${w.start.slice(11, 16)} ~ ${w.end.slice(11, 16)}`
    : `${w.start.slice(0, 16).replace('T', ' ')} ~ ${w.end.slice(0, 16).replace('T', ' ')}`
}

/** 预计算渲染行：表格用 prop 直接取字段，不依赖单元格里调用函数/查表 */
const rows = computed(() =>
  (props.preview?.windows ?? []).map((w, i) => {
    const src = SOURCE_META[w.source]
    return {
      key: i,
      date: w.start ? w.start.slice(0, 10) : '—',
      timeLabel: windowRange(w),
      sourceLabel: src?.label ?? w.source ?? '—',
      sourceTag: src?.tag ?? ('info' as const),
      authorized: w.isAuthorized === true
    }
  })
)

function onRangeChange(v: [string, string] | null | undefined): void {
  emit('update:range', v ?? undefined)
}
</script>

<template>
  <div class="rc-resolve">
    <div class="rc-resolve-head">
      <ElTag v-if="preview" :type="STATUS_META[preview.status].tag" size="small" effect="light">
        {{ STATUS_META[preview.status].label }}
      </ElTag>
      <span class="rc-resolve-title">三级合并后可排时段</span>
      <span v-if="preview?.hasAnyWindow" class="rc-resolve-count">
        {{ preview.windows.length }} 段
      </span>

      <span class="rc-resolve-spacer"></span>

      <ElDatePicker
        :model-value="range"
        class="rc-resolve-range"
        type="daterange"
        value-format="YYYY-MM-DD"
        range-separator="~"
        start-placeholder="开始"
        end-placeholder="结束"
        :clearable="false"
        size="small"
        @change="onRangeChange"
      />
      <ElButton size="small" text :loading="loading" @click="emit('refresh')"> 刷新 </ElButton>
    </div>

    <div v-loading="loading" class="rc-resolve-body">
      <ElEmpty v-if="!preview" :description="emptyText" :image-size="50" />
      <div v-else-if="!preview.hasAnyWindow" class="rc-resolve-empty">
        <ElTag :type="preview.status === 'OFF' ? 'danger' : 'info'" size="small" effect="plain">
          {{ STATUS_META[preview.status].label }}
        </ElTag>
        <span>
          {{
            preview.status === 'OFF'
              ? '本级已显式关闭／维修，即使工厂或部门开放也不会被复活。'
              : '本级没登记，工厂和部门也没开放窗口 —— 不补默认 8 小时。'
          }}
        </span>
      </div>
      <template v-else>
        <div class="rc-resolve-scroll">
          <ElTable :data="rows" size="small" border row-key="key" style="width: 100%">
            <ElTableColumn prop="date" label="日期" width="120" />
            <ElTableColumn prop="timeLabel" label="时段" min-width="140" />
            <ElTableColumn prop="sourceLabel" label="来源" width="120">
              <template #default="{ row }">
                <ElTag :type="row.sourceTag" size="small" effect="plain">
                  {{ row.sourceLabel }}
                </ElTag>
              </template>
            </ElTableColumn>
            <ElTableColumn label="授权" width="110" align="center">
              <template #default="{ row }">
                <ElTag v-if="row.authorized" type="warning" size="small" effect="plain">
                  授权加班
                </ElTag>
                <span v-else class="rc-resolve-plain">常规</span>
              </template>
            </ElTableColumn>
          </ElTable>
        </div>
        <div class="rc-resolve-hint">
          继承自工厂 / 部门的时段要去「上级工作日历」改；标记「本级」的可在上方窗口表调整。
          带「授权加班」的是上级审批过的加班例外。
        </div>
      </template>
    </div>
  </div>
</template>

<style scoped>
.rc-resolve {
  margin-top: 14px;
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 6px;
  overflow: hidden;
}
.rc-resolve-head {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  padding: 8px 12px;
  background: var(--el-fill-color-lighter);
  border-bottom: 1px solid var(--el-border-color-lighter);
}
.rc-resolve-title {
  font-size: 13px;
  font-weight: 600;
}
.rc-resolve-count {
  font-size: 12px;
  color: var(--el-text-color-secondary);
}
.rc-resolve-spacer {
  flex: 1;
}
.rc-resolve-range {
  flex: 0 0 240px;
  width: 240px;
}
.rc-resolve-body {
  padding: 10px 12px;
  min-height: 56px;
}
/* 表格高度由外层控制（不用 ElTable 的 max-height —— 其内部高度计算在部分浏览器会塌成一行） */
.rc-resolve-scroll {
  max-height: 260px;
  overflow: auto;
}
.rc-resolve-empty {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 0;
  font-size: 13px;
  color: var(--el-text-color-regular);
}
.rc-resolve-hint {
  margin-top: 8px;
  font-size: 12px;
  color: var(--el-text-color-secondary);
}
.rc-resolve-plain {
  font-size: 12px;
  color: var(--el-text-color-secondary);
}
</style>
