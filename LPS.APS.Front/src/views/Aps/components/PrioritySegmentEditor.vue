<!--
  APS V1 4号位 — 需求优先级段位编辑器（规则集 DRAFT 专用）

  页面结构：**轮次在外、段在内**（轮 ⊃ 段）。
  冻结 01 v1.15 §16：「三轮不得混池；每一轮内继续依有序规则段 → 第一命中 → 段内排序」。
  后端消费顺序见 `PeggingOrchestrator.cs:13452-13457`：逐轮取 `Segments.Where(轮次相等)`，
  轮内再按 `SegmentOrder` 升序 First Match ⇒ 段序只在**本轮内**有效。

  规则红线（均来自后端契约，改动前先核对）：
   1. 字段按后端已接线范围开放（当前 12 个，见 `api/aps-v1/prioritySegmentEditor.ts`），
      操作符不能有「不等于」
   2. 段的顺序由数组位置决定，保存时统一重排为 1..N（SegmentOrder 必须唯一正整数）
   3. 空条件段＝无条件命中，只能有 1 个且必须在最后（后端发布校验会拒）
   4. 竞争轮次**每段必有一个**（1/2/3）：后端投影层对「部分声明」显式抛错，
      对「整块不声明」按第 1 轮处理（`DefaultCompetitionRound=1`）——
      与显式写第 1 轮等价 ⇒ 编辑器统一显式写，不给「不声明」这个看不出归属的选项
   5. 稳定排序键后端只有一个合法值 DEMANDKEY，且 0号位 2026-11-04 工作台第 56 条判为
      **D 类 V1 固定** ⇒ 页面只读展示，不给开关（可关 = 可写出不稳定排序，与 D 类相抵触）
   6. 「出荷指示入物流日」的范围条件写整数 = 相对冻结日的天数（后端换算成具体日期），
      写 YYYY-MM-DD = 具体日期；控件按值的形态自动切「相对天数 / 具体日期」

  说明：本组件只产出表单模型；JSON 序列化与发布前拦截在
  `prioritySegmentEditor.ts` 的 serialize / validate 里（store 保存时调用）。
-->
<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import {
  ElButton,
  ElDatePicker,
  ElEmpty,
  ElInput,
  ElInputNumber,
  ElOption,
  ElRadioButton,
  ElRadioGroup,
  ElSelect,
  ElSwitch,
  ElTag
} from 'element-plus'
import type { PrioritySegmentDto, SegmentMatchConditionDto } from '@/api/aps-v1/types/rule'
import {
  COMPETITION_ROUND_SHORT,
  EDITOR_DEMAND_FIELDS,
  FIXED_TIE_BREAK_FIELD,
  createEmptySegment,
  createEmptySortField,
  fieldKindOf,
  fieldLabelOf,
  groupSegmentsByRound,
  isOffsetDaysText,
  operatorsForField,
  supportsRelativeOffset,
  valueSuggestionsOf
} from '@/api/aps-v1/prioritySegmentEditor'

type Row = PrioritySegmentDto & { _k: number }

const props = defineProps<{ modelValue: PrioritySegmentDto[] }>()
const emit = defineEmits<{ (e: 'update:modelValue', value: PrioritySegmentDto[]): void }>()

let seq = 1

function cloneRows(src: PrioritySegmentDto[] | undefined): Row[] {
  return (src ?? []).map((s) => ({
    _k: seq++,
    orderInSegment: s.orderInSegment,
    segmentName: s.segmentName,
    isEnabled: s.isEnabled,
    competitionRound: s.competitionRound,
    matchConditions: (s.matchConditions ?? []).map((c) => ({ ...c })),
    sortFields: (s.sortFields ?? []).map((f) => ({ ...f })),
    stableTieBreakFields: [FIXED_TIE_BREAK_FIELD]
  }))
}

function plain(list: PrioritySegmentDto[]) {
  return list.map((s) => ({
    order: s.orderInSegment,
    name: s.segmentName,
    enabled: s.isEnabled,
    round: s.competitionRound ?? null,
    conds: s.matchConditions.map((c) => [c.field, c.operator, c.value]),
    sorts: s.sortFields.map((f) => [f.field, f.direction]),
    tie: s.stableTieBreakFields
  }))
}

const rows = ref<Row[]>([])

watch(
  () => props.modelValue,
  (v) => {
    // 父组件把我们 emit 的数组回灌时内容相同 —— 不要重建行（会丢焦点）
    if (JSON.stringify(plain(v ?? [])) === JSON.stringify(plain(rows.value))) return
    rows.value = cloneRows(v)
  },
  { immediate: true, deep: true }
)

/** 轮次分组：固定 第1/2/3 轮 三组，组内保持原顺序 */
const groups = computed(() => groupSegmentsByRound(rows.value))

/** 折叠状态（默认展开；按轮次记） */
const collapsed = ref<Record<string, boolean>>({})

function groupKey(round: number | null): string {
  return round === null ? 'none' : String(round)
}

function isCollapsed(round: number | null): boolean {
  return collapsed.value[groupKey(round)] === true
}

function toggleGroup(round: number | null): void {
  const k = groupKey(round)
  collapsed.value = { ...collapsed.value, [k]: !isCollapsed(round) }
}

/** 常规三轮（可加段、可归入）；越界/未声明组只读展示 */
function isStandardRound(round: number | null): boolean {
  return round === 1 || round === 2 || round === 3
}

function commit(): void {
  emit(
    'update:modelValue',
    rows.value.map(({ _k, ...rest }) => ({
      ...rest,
      matchConditions: rest.matchConditions.map((c) => ({ ...c })),
      sortFields: rest.sortFields.map((f) => ({ ...f })),
      stableTieBreakFields: [FIXED_TIE_BREAK_FIELD]
    }))
  )
}

function addSegment(round: number | null): void {
  if (round === null) return
  const list = rows.value
  const seg = cloneRows([createEmptySegment(list, round)])[0]
  // 插到该轮的最后一段之后（轮内顺序即段序）
  let last = -1
  for (let i = 0; i < list.length; i++) {
    if ((list[i].competitionRound ?? null) === round) last = i
  }
  list.splice(last + 1, 0, seg)
  commit()
}

function removeSegment(seg: Row): void {
  const i = rows.value.indexOf(seg)
  if (i < 0) return
  rows.value.splice(i, 1)
  commit()
}

/** 上移/下移**只在同轮内**（段序只在轮内有效，见文件头红线） */
function canMove(seg: Row, delta: number): boolean {
  const list = rows.value
  const i = list.indexOf(seg)
  if (i < 0) return false
  let j = i + delta
  while (j >= 0 && j < list.length && !sameRound(list[j], seg)) j += delta
  return j >= 0 && j < list.length
}

function moveSegment(seg: Row, delta: number): void {
  const list = rows.value
  const i = list.indexOf(seg)
  if (i < 0) return
  let j = i + delta
  while (j >= 0 && j < list.length && !sameRound(list[j], seg)) j += delta
  if (j < 0 || j >= list.length) return
  const [moved] = list.splice(i, 1)
  list.splice(j, 0, moved)
  commit()
}

function sameRound(a: Row, b: Row): boolean {
  return (a.competitionRound ?? null) === (b.competitionRound ?? null)
}

/** 改归属轮次：段从原轮摘出，插到目标轮的最后 */
function moveToRound(seg: Row, round: number): void {
  const list = rows.value
  const i = list.indexOf(seg)
  if (i < 0) return
  list.splice(i, 1)
  seg.competitionRound = round
  let last = -1
  for (let k = 0; k < list.length; k++) {
    if ((list[k].competitionRound ?? null) === round) last = k
  }
  list.splice(last + 1, 0, seg)
  commit()
}

function addCondition(seg: Row): void {
  // 延期状态(1) 已归参数集、编辑器关闭 ⇒ 默认客户等级(2)
  seg.matchConditions.push({ field: 2, operator: 0, value: '' })
  commit()
}

function removeCondition(seg: Row, idx: number): void {
  seg.matchConditions.splice(idx, 1)
  commit()
}

/** 换字段：操作符重置为该字段的第一个合法项，值按新类型清空 */
function onConditionField(seg: Row, idx: number, field: number): void {
  const c = seg.matchConditions[idx]
  c.field = field
  c.operator = operatorsForField(field)[0]?.value ?? 0
  c.value = c.operator === 6 ? [] : ''
  conditionModes.delete(c)
  commit()
}

/** 换操作符：等于 ↔ 属于 之间切换时重置值形态（字符串 / 数组） */
function onConditionOperator(seg: Row, idx: number, operator: number): void {
  const c = seg.matchConditions[idx]
  c.operator = operator
  if (operator === 6) {
    c.value = Array.isArray(c.value) ? c.value : []
  } else {
    c.value = Array.isArray(c.value) ? '' : c.value
  }
  // 双模式按当前值重新判定（日期保留日期模式、整数保留天数模式）
  conditionModes.delete(c)
  commit()
}

function onConditionValue(seg: Row, idx: number, value: unknown): void {
  const c = seg.matchConditions[idx]
  // 数字控件回填的是 number：相对天数按契约存整数串（如 "20"），清空统一成 ''
  let next: unknown = value === null || value === undefined ? '' : value
  if (supportsRelativeOffset(c.field, c.operator) && typeof next === 'number') {
    next = String(next)
  }
  c.value = next as string | string[]
  commit()
}

/** 「相对天数 / 具体日期」双模式：显式切换后记住；未切换时按值的形态判定。 */
type RelativeMode = 'offset' | 'date'
const conditionModes = new WeakMap<SegmentMatchConditionDto, RelativeMode>()

function conditionMode(c: SegmentMatchConditionDto): RelativeMode {
  const saved = conditionModes.get(c)
  if (saved) return saved
  return typeof c.value === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(c.value) ? 'date' : 'offset'
}

/** 切模式会清空当前值（整数与日期两种写法不能互转），避免留下看不出来的非法残值。 */
function onConditionMode(seg: Row, idx: number, mode: unknown): void {
  const c = seg.matchConditions[idx]
  conditionModes.set(c, mode === 'date' ? 'date' : 'offset')
  c.value = ''
  commit()
}

/** 相对天数输入框：整数（可负，后端按 FrozenAt.Date + N 换算） */
function offsetValueOf(value: unknown): number | undefined {
  if (typeof value !== 'string' && typeof value !== 'number') return undefined
  const text = String(value).trim()
  return isOffsetDaysText(text) ? Number(text) : undefined
}

function addSort(seg: Row): void {
  seg.sortFields.push(createEmptySortField())
  commit()
}

function removeSort(seg: Row, idx: number): void {
  seg.sortFields.splice(idx, 1)
  commit()
}

function moveSort(seg: Row, idx: number, delta: number): void {
  const target = idx + delta
  if (target < 0 || target >= seg.sortFields.length) return
  const [row] = seg.sortFields.splice(idx, 1)
  seg.sortFields.splice(target, 0, row)
  commit()
}

function onSortField(seg: Row, idx: number, field: number): void {
  seg.sortFields[idx].field = field
  commit()
}

function onSortDirection(seg: Row, idx: number, direction: number): void {
  seg.sortFields[idx].direction = direction
  commit()
}

function isIn(operator: number): boolean {
  return operator === 6
}

/** 数值框只吃 number；存回表单仍用字符串（序列化与后端比较都按字符串/数值均可） */
function numberValueOf(value: unknown): number | undefined {
  if (value === null || value === undefined || value === '') return undefined
  const n = Number(value)
  return Number.isFinite(n) ? n : undefined
}
</script>

<template>
  <div class="pseg-editor">
    <p class="editor-intro">
      每一轮内部按「段位 → 第一命中 → 段内排序」排：先按段的先后找第一条命中的段，命中后按这一段的排序键排。
      不同轮次不混池，供给池按轮次顺序消耗 —— 所以段是轮次里面的规则段，上移/下移只在本轮内有效。
    </p>

    <ElEmpty v-if="!rows.length" description="这个版本还没有段位" :image-size="70">
      <ElButton type="primary" @click="addSegment(1)">添加第一个段</ElButton>
    </ElEmpty>

    <div v-for="g in groups" :key="groupKey(g.round)" class="round-group">
      <div class="round-head">
        <button type="button" class="round-toggle" @click="toggleGroup(g.round)">
          <span class="round-caret">{{ isCollapsed(g.round) ? '▸' : '▾' }}</span>
          <span class="round-title">{{ g.label }}</span>
        </button>
        <ElTag size="small" type="info" effect="plain">本轮 {{ g.segments.length }} 段</ElTag>
        <ElTag v-if="!g.segments.length" size="small" type="warning" effect="plain">
          本轮暂无段位。若有需求落到本轮，将按需求键兜底排序。
        </ElTag>
      </div>

      <div v-show="!isCollapsed(g.round)" class="round-body">
        <p v-if="g.scope" class="round-scope">{{ g.scope }}</p>

        <div v-for="(seg, si) in g.segments" :key="seg._k" class="seg-card">
          <div class="seg-head">
            <span class="seg-no">{{ si + 1 }}</span>
            <ElInput
              v-model="seg.segmentName"
              class="seg-name"
              maxlength="50"
              placeholder="段位名称（如：延期订单优先）"
              @input="commit"
            />
            <ElSwitch v-model="seg.isEnabled" active-text="启用" @change="commit" />
            <div class="seg-ops">
              <span class="seg-move-label">归入</span>
              <ElSelect
                :model-value="seg.competitionRound ?? 1"
                class="seg-move"
                @update:model-value="moveToRound(seg, Number($event))"
              >
                <ElOption
                  v-for="r in COMPETITION_ROUND_SHORT"
                  :key="r.value"
                  :value="r.value"
                  :label="r.label"
                />
              </ElSelect>
              <ElButton size="small" :disabled="!canMove(seg, -1)" @click="moveSegment(seg, -1)">
                上移
              </ElButton>
              <ElButton size="small" :disabled="!canMove(seg, 1)" @click="moveSegment(seg, 1)">
                下移
              </ElButton>
              <ElButton size="small" type="danger" plain @click="removeSegment(seg)">删除段</ElButton>
            </div>
          </div>

          <div class="seg-section">
            <div class="seg-section-title">
              命中条件（全部满足才命中本段）
              <span v-if="!seg.matchConditions.length" class="muted">—— 无条件，命中全部</span>
            </div>
            <div v-for="(c, ci) in seg.matchConditions" :key="ci" class="cond-row">
              <ElSelect
                :model-value="c.field"
                class="c-field"
                @update:model-value="onConditionField(seg, ci, Number($event))"
              >
                <ElOption
                  v-for="f in EDITOR_DEMAND_FIELDS"
                  :key="f.value"
                  :value="f.value"
                  :label="f.label"
                />
              </ElSelect>
              <ElSelect
                :model-value="c.operator"
                class="c-op"
                @update:model-value="onConditionOperator(seg, ci, Number($event))"
              >
                <ElOption
                  v-for="o in operatorsForField(c.field)"
                  :key="o.value"
                  :value="o.value"
                  :label="o.label"
                />
              </ElSelect>

              <ElSelect
                v-if="isIn(c.operator)"
                :model-value="c.value as string[]"
                class="c-value"
                multiple
                filterable
                allow-create
                default-first-option
                placeholder="可填多个值（回车添加）"
                no-data-text="没有预设值，直接输入后回车"
                @update:model-value="onConditionValue(seg, ci, $event)"
              >
                <ElOption
                  v-for="v in valueSuggestionsOf(c.field)"
                  :key="v.value"
                  :value="v.value"
                  :label="v.label"
                />
              </ElSelect>
              <div v-else-if="supportsRelativeOffset(c.field, c.operator)" class="c-value c-relative">
                <ElRadioGroup
                  :model-value="conditionMode(c)"
                  size="small"
                  @update:model-value="onConditionMode(seg, ci, $event)"
                >
                  <ElRadioButton value="offset">按天数偏移</ElRadioButton>
                  <ElRadioButton value="date">具体日期</ElRadioButton>
                </ElRadioGroup>
                <ElInputNumber
                  v-if="conditionMode(c) === 'offset'"
                  :model-value="offsetValueOf(c.value)"
                  :step="1"
                  :precision="0"
                  controls-position="right"
                  placeholder="距冻结日天数，如 20"
                  @update:model-value="onConditionValue(seg, ci, $event)"
                />
                <ElDatePicker
                  v-else
                  :model-value="c.value as string"
                  type="date"
                  value-format="YYYY-MM-DD"
                  placeholder="选择具体日期"
                  @update:model-value="onConditionValue(seg, ci, $event)"
                />
                <span class="muted offset-hint">
                  {{
                    conditionMode(c) === 'offset' ? '20 = 冻结日 +20 天（可填负数）' : '按固定日期比较'
                  }}
                </span>
              </div>
              <ElDatePicker
                v-else-if="fieldKindOf(c.field) === 'DATE'"
                :model-value="c.value as string"
                class="c-value"
                type="date"
                value-format="YYYY-MM-DD"
                placeholder="选择日期"
                @update:model-value="onConditionValue(seg, ci, $event)"
              />
              <ElInputNumber
                v-else-if="fieldKindOf(c.field) === 'NUMBER'"
                :model-value="numberValueOf(c.value)"
                class="c-value"
                :min="0"
                :step="0.1"
                controls-position="right"
                placeholder="输入数值"
                @update:model-value="onConditionValue(seg, ci, $event)"
              />
              <ElSelect
                v-else
                :model-value="c.value as string"
                class="c-value"
                filterable
                allow-create
                default-first-option
                :placeholder="c.operator === 7 ? '前缀匹配，如 FINAL*' : `${fieldLabelOf(c.field)}的值`"
                no-data-text="没有预设值，直接输入后回车"
                @update:model-value="onConditionValue(seg, ci, $event)"
              >
                <ElOption
                  v-for="v in valueSuggestionsOf(c.field)"
                  :key="v.value"
                  :value="v.value"
                  :label="v.label"
                />
              </ElSelect>

              <ElButton size="small" text type="danger" @click="removeCondition(seg, ci)">
                移除
              </ElButton>
            </div>
            <ElButton size="small" @click="addCondition(seg)">+ 条件</ElButton>
          </div>

          <div class="seg-section">
            <div class="seg-section-title">
              段内排序（按顺序依次生效）
              <span v-if="!seg.sortFields.length" class="muted">—— 保持原顺序</span>
            </div>
            <div v-for="(f, fi) in seg.sortFields" :key="fi" class="sort-row">
              <span class="sort-no">{{ fi + 1 }}</span>
              <ElSelect
                :model-value="f.field"
                class="s-field"
                @update:model-value="onSortField(seg, fi, Number($event))"
              >
                <ElOption
                  v-for="opt in EDITOR_DEMAND_FIELDS"
                  :key="opt.value"
                  :value="opt.value"
                  :label="opt.label"
                />
              </ElSelect>
              <ElSelect
                :model-value="f.direction"
                class="s-dir"
                @update:model-value="onSortDirection(seg, fi, Number($event))"
              >
                <ElOption :value="0" label="升序" />
                <ElOption :value="1" label="降序" />
              </ElSelect>
              <ElButton size="small" :disabled="fi === 0" @click="moveSort(seg, fi, -1)">
                上移
              </ElButton>
              <ElButton
                size="small"
                :disabled="fi === seg.sortFields.length - 1"
                @click="moveSort(seg, fi, 1)"
              >
                下移
              </ElButton>
              <ElButton size="small" text type="danger" @click="removeSort(seg, fi)">移除</ElButton>
            </div>
            <ElButton size="small" @click="addSort(seg)">+ 排序键</ElButton>
          </div>

          <div class="seg-section tie-row">
            <span class="tie-label">并列时按需求键稳定排序</span>
            <ElTag size="small" effect="plain">{{ FIXED_TIE_BREAK_FIELD }}</ElTag>
            <ElTag size="small" type="warning" effect="plain">V1 固定</ElTag>
          </div>
        </div>

        <div v-if="isStandardRound(g.round)" class="add-row">
          <ElButton size="small" @click="addSegment(g.round)">+ 在本轮加一段</ElButton>
        </div>
      </div>
    </div>

    <p class="editor-hint">
      可选字段按后端已接线范围给出：交期类用日期框、数量类用数字框、枚举类可直接选英文码；
      「出荷指示入物流日」按天数偏移写整数（20 表示冻结日 +20 天，随 Run 日期滚动），
      也可切「具体日期」写固定日期；「包含」只用于物料编码，实际是前缀匹配 （填 FINAL* 表示以 FINAL
      开头）。改完记得点「保存段位」。
    </p>
  </div>
</template>

<style lang="less" scoped>
.pseg-editor {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.editor-intro {
  margin: 0;
  padding: 8px 10px;
  background: #f8fafc;
  border-radius: 4px;
  font-size: 12px;
  color: #475569;
  line-height: 1.7;
}

.round-group {
  border: 1px solid #e2e8f0;
  border-radius: 6px;
  background: #fff;
  overflow: hidden;
}

.round-head {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  padding: 10px 12px;
  background: #f8fafc;
  border-bottom: 1px solid #e2e8f0;
}

.round-toggle {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 0;
  border: none;
  background: none;
  cursor: pointer;
  font-size: 14px;
  font-weight: 600;
  color: #1e293b;
}

.round-caret {
  font-size: 12px;
  color: #64748b;
}

.round-body {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 12px;
}

.round-scope {
  margin: 0;
  font-size: 12px;
  color: #64748b;
  line-height: 1.6;
}

.seg-card {
  border: 1px solid #e2e8f0;
  border-radius: 6px;
  padding: 12px;
  background: #fff;
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.seg-head {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.seg-no {
  width: 24px;
  height: 24px;
  border-radius: 50%;
  background: #2563eb;
  color: #fff;
  font-size: 12px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  flex: none;
}

.seg-name {
  width: 240px;
}

.seg-move-label {
  font-size: 12px;
  color: #64748b;
  flex: none;
}

.seg-move {
  width: 100px;
}

.seg-ops {
  margin-left: auto;
  display: flex;
  align-items: center;
  gap: 4px;
}

.seg-section {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding-left: 32px;
}

.seg-section-title {
  font-size: 12px;
  color: #475569;
}

.cond-row,
.sort-row {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.c-field {
  width: 140px;
}

.c-op {
  width: 110px;
}

.c-value {
  flex: 1;
  min-width: 220px;
}

.c-relative {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.c-relative :deep(.el-input-number) {
  flex: 1;
  min-width: 140px;
}

.offset-hint {
  font-size: 12px;
}

.s-field {
  width: 150px;
}

.s-dir {
  width: 100px;
}

.sort-no {
  width: 18px;
  font-size: 12px;
  color: #64748b;
  text-align: center;
}

.tie-row {
  flex-direction: row;
  align-items: center;
  gap: 8px;
}

.add-row {
  padding-left: 32px;
}

.editor-hint {
  margin: 0;
  font-size: 12px;
  color: #64748b;
  line-height: 1.6;
}

.muted {
  color: #94a3b8;
}
</style>
