<script setup lang="ts">
/**
 * APS V1 4号位 — 参数集「仓库引用」逐项编辑器
 *
 * 覆盖后端校验采集段里的**四个落点**（`GovernanceVersionService.cs:1019-1034`）：
 *   ① Supply.Inventory.WarehousePriority    有序仓库码（顺序即优先级）
 *   ② Procurement.WarehousePriority          有序仓库码（顺序即优先级）
 *   ③ Procurement.DefaultPurchaseLt[]        {WarehouseCode, MaterialId?, DefaultLtDays}
 *   ④ Procurement.ArrivalToUsableOffsets[]   {WarehouseCode, OffsetHours}
 *
 * ⚠️ ① 自 3号位 2026-11-06 v1.1 起维护面**隐藏**（页面不再渲染）；组件保留该 kind，
 *    仅为维持底层读写与载荷原样回写，不作为维护入口。
 *
 * 交互口径（3号位 2026-10-25 §四：认可「搜索下拉 + 回显码/名称」、不硬编码夹具码）：
 *  - 仓库码**只能从字典里选**（下拉可搜索编码/名称），不提供自由文本录入 ⇒ 拼不出假码
 *  - 选项文案 = `编码 名称`（回显码 + 名称）
 *  - 库里已有、但字典里查不到的编码**照常显示**（标「仓库字典里没有」），允许改或删
 *    —— 否则用户连「删掉一个坏编码」都做不到
 *
 * 展示层口径（5号位 2026-10-25 §2.4 Q1 三条建议，dev 实测 258 码）：
 *  - 选项文案追加**仓库属性**：`编码（名称）· 角色 短码`（角色中文、ERP 短码），让用户自行识别码性质
 *  - **采购类两个落点**（采购仓库优先级 / 默认采购提前期）把**原材料库（KS+BS）单独分组置顶**
 *    —— 依据：采购管道事实落在字典内的码 100% 是原材料库（KS 56 + BS 19）
 *  - 供应库存仓库优先级**不分组**（5号位 未列入）；到货可用偏移未被提及 ⇒ 保持平铺，不自造口径
 *  - 下拉**给全量、不硬过滤**（不按属性/码长/是否出现在事实表筛）；分组只是排版，一码不少
 *
 * 数据流：读 store getter（编辑态缓冲）→ 改动整份数组 → store 写动作（置脏）
 *
 * ⚠️ ③ 的物料列（2026-10-26 用户走查「让填物料 Id 不行」）：
 *   `PurchaseLtRule.MaterialId` 存 **Material 主键数字 Id 的字符串**，不是物料编码 ——
 *   `AvailableTimeCalculator.cs:92-97` 拿入参 `int materialId` 的 `.ToString()` 与之比对。
 *   所以列上是**物料字典下拉**（显示 `编码（名称）`，写回 `masterId`），不是自由文本；
 *   填编码不会报错、只会静默退化到仓库级默认（提前期永远不生效）。
 *   与采购良率相反（`PlanningYieldRule.MaterialId` 存编码，按编码比对），两处不可统一。
 */

import { computed, onMounted, ref } from 'vue'
import {
  ElButton,
  ElInputNumber,
  ElOption,
  ElOptionGroup,
  ElSelect,
  ElTable,
  ElTableColumn,
  ElTooltip
} from 'element-plus'
import { useParameterSetsStore } from '@/store/modules/aps/parameterSets'
import { materialLookupApi } from '@/api/aps-v1/materialLookup'
import type { MaterialLookupItem } from '@/api/aps-v1/types/material'
import {
  isRawMaterialWarehouse,
  splitWarehouseOptions,
  warehouseOptionLabel,
  type PurchaseLtItem,
  type ArrivalOffsetItem,
  type WarehouseOptionSections
} from '@/api/aps-v1/warehouseRefs'

type Kind = 'supply-priority' | 'procurement-priority' | 'purchase-lt' | 'arrival-offset'

const props = defineProps<{
  /** 四处落点之一 */
  kind: Kind
  /** 区块标题 */
  title: string
  /** 只读（非草稿态 / 无编辑权限） */
  disabled?: boolean
}>()

const store = useParameterSetsStore()

/** 下拉选项（编码 + 名称 + 属性文案） */
interface WarehouseOption {
  code: string
  label: string
  /** 是否来自权威仓库字典 */
  known: boolean
  /** 是否原材料库（ERPProperty ∈ KS/BS）—— 只决定分组，不决定是否给用户 */
  raw: boolean
}

const dictOptions = computed<WarehouseOption[]>(() =>
  store.warehouseDict.map((w) => {
    const code = String(w.code ?? '').trim()
    return {
      code,
      label: warehouseOptionLabel(w),
      known: true,
      raw: isRawMaterialWarehouse(w)
    }
  })
)

/** 字典里查不到的编码选项（仅用于把已有值显示出来） */
function unknownOption(code: string): WarehouseOption {
  return { code, label: `${code}（仓库字典里没有）`, known: false, raw: false }
}

/**
 * 哪些落点把「原材料库」单独分组置顶。
 *
 * 5号位 Q1 建议③：**采购类两个落点**（Procurement.WarehousePriority / Procurement.DefaultPurchaseLt）
 * 分组置顶；`Supply.Inventory.WarehousePriority` 明确不分组；到货可用偏移未被提及 ⇒ 保持平铺。
 */
const groupRaw = computed(
  () => props.kind === 'procurement-priority' || props.kind === 'purchase-lt'
)

/** 下拉两段：原材料库分组 + 其余平铺 */
type OptionSections = WarehouseOptionSections<WarehouseOption>

function splitSections(options: WarehouseOption[]): OptionSections {
  return splitWarehouseOptions(options, groupRaw.value)
}

/** 当前落点的值（按 kind 分派） */
const priorityCodes = computed<string[]>(() =>
  props.kind === 'supply-priority'
    ? store.supplyWarehousePriority
    : props.kind === 'procurement-priority'
      ? store.procurementWarehousePriority
      : []
)

const purchaseLtRows = computed<PurchaseLtItem[]>(() => store.defaultPurchaseLt)
const arrivalOffsetRows = computed<ArrivalOffsetItem[]>(() => store.arrivalToUsableOffsets)

const isPriority = computed(() => props.kind.endsWith('priority'))

/** 某行可选项 = 字典全部 + 该行自己的编码（字典里没有时补一个） */
function optionsFor(current: string | undefined): WarehouseOption[] {
  const opts = [...dictOptions.value]
  const cur = (current ?? '').trim()
  if (cur && !opts.some((o) => o.code.toLowerCase() === cur.toLowerCase())) {
    opts.push(unknownOption(cur))
  }
  return opts
}

/** 还没用过的字典编码（添加用） */
function unusedOptions(used: string[]): WarehouseOption[] {
  const taken = used.map((c) => c.trim().toLowerCase())
  return dictOptions.value.filter((o) => !taken.includes(o.code.toLowerCase()))
}

/**
 * 行编辑下拉的分段（按行缓存；字典或落点变化时整表重算）。
 *
 * 缓存只为少渲染开销，**不缓存判定结果** —— 字典是后端拉来的，变了就得重算。
 */
const rowSections = computed(() => {
  const map = new Map<string, OptionSections>()
  return (current: string | undefined): OptionSections => {
    const key = (current ?? '').trim()
    let hit = map.get(key)
    if (!hit) {
      hit = splitSections(optionsFor(current))
      map.set(key, hit)
    }
    return hit
  }
})

/** 添加下拉的分段（还没用过的码） */
function addSections(used: string[]): OptionSections {
  return splitSections(unusedOptions(used))
}

/* ===== 物料下拉（③ DefaultPurchaseLt.MaterialId 专用）=====
 * 身份口径（勿改）：`PurchaseLtRule.MaterialId` 存的是 **Material 主键数字 Id**，
 * 不是物料编码 —— `AvailableTimeCalculator.cs:92-97` 拿入参 `int materialId` 的
 * `.ToString()` 与它比对。写成编码**不报错**、只是永远匹配不上 ⇒ 静默退化到仓库级默认。
 * 所以这里必须让用户按「编码（名称）」挑，写回 `masterId`。
 * 注意与采购良率相反：`PlanningYieldRule.MaterialId` 存的是**编码**（按编码比对），两处不可混用。
 */
interface MaterialOption {
  /** 写回 MaterialId 的值（物料主键数字 Id 的字符串形式） */
  id: string
  label: string
}

const rawMaterials = ref<MaterialLookupItem[]>([])
const materialSearching = ref(false)
const materialDictFailed = ref(false)

const materialOptions = computed<MaterialOption[]>(() =>
  rawMaterials.value.map((m) => {
    const id = String(m.masterId ?? '').trim()
    const code = String(m.code ?? '').trim()
    const name = String(m.name ?? '').trim()
    return { id, label: code && name ? `${code}（${name}）` : code || name || `物料 Id ${id}` }
  })
)

/** 某行可选项 = 字典全部 + 该行自己的 Id（字典里没有时补一个，否则连老值都删不掉） */
function materialOptionsFor(current: string | null | undefined): MaterialOption[] {
  const opts = [...materialOptions.value]
  const cur = String(current ?? '').trim()
  if (cur && !opts.some((o) => o.id === cur)) {
    opts.push({ id: cur, label: `物料 Id ${cur}（物料字典里没有）` })
  }
  return opts
}

async function loadMaterials(): Promise<void> {
  const materials = await materialLookupApi.listActiveMaterials()
  rawMaterials.value = materials
  // 拉空 = 没权限或被限流，如实提示；不静默变成「下拉里什么都没有」
  materialDictFailed.value = materials.length === 0
}

/** 远程搜索（后端 lookup 只返 TOP 200 ⇒ 必须能按关键词补搜） */
async function onSearchMaterial(query: string): Promise<void> {
  const kw = String(query ?? '').trim()
  if (!kw) return
  materialSearching.value = true
  try {
    const found = await materialLookupApi.searchMaterials(kw)
    const seen = new Set(rawMaterials.value.map((m) => String(m.masterId ?? '').trim()))
    const extra = found.filter((m) => {
      const id = String(m.masterId ?? '').trim()
      if (!id || seen.has(id)) return false
      seen.add(id)
      return true
    })
    if (extra.length) rawMaterials.value = [...rawMaterials.value, ...extra]
  } finally {
    materialSearching.value = false
  }
}

onMounted(() => {
  void loadMaterials()
})

/* ===== 有序码列表（①②）===== */

const pendingCode = ref('')

function onAddPriority(code: string): void {
  pendingCode.value = ''
  const c = (code ?? '').trim()
  if (!c) return
  const next = [...priorityCodes.value, c]
  if (props.kind === 'supply-priority') store.setSupplyWarehousePriority(next)
  else store.setProcurementWarehousePriority(next)
}

function onChangePriority(index: number, code: string): void {
  const next = [...priorityCodes.value]
  if (index < 0 || index >= next.length) return
  next[index] = code
  if (props.kind === 'supply-priority') store.setSupplyWarehousePriority(next)
  else store.setProcurementWarehousePriority(next)
}

function onRemovePriority(index: number): void {
  const next = [...priorityCodes.value]
  if (index < 0 || index >= next.length) return
  next.splice(index, 1)
  if (props.kind === 'supply-priority') store.setSupplyWarehousePriority(next)
  else store.setProcurementWarehousePriority(next)
}

function onMovePriority(index: number, delta: number): void {
  const next = [...priorityCodes.value]
  const target = index + delta
  if (index < 0 || index >= next.length || target < 0 || target >= next.length) return
  const [row] = next.splice(index, 1)
  next.splice(target, 0, row)
  if (props.kind === 'supply-priority') store.setSupplyWarehousePriority(next)
  else store.setProcurementWarehousePriority(next)
}

/* ===== 对象数组（③④）===== */

function onAddPurchaseLt(): void {
  store.setDefaultPurchaseLt([...purchaseLtRows.value, { WarehouseCode: '', DefaultLtDays: 0 }])
}

function onChangePurchaseLt(index: number, patch: Partial<PurchaseLtItem>): void {
  const next = purchaseLtRows.value.map((r, i) => (i === index ? { ...r, ...patch } : { ...r }))
  store.setDefaultPurchaseLt(next)
}

/** 物料选中/清空：写回数字 Id 字符串；清空 ⇒ null（= 仓库级默认） */
function onChangePurchaseLtMaterial(index: number, materialId: unknown): void {
  const id = String(materialId ?? '').trim()
  onChangePurchaseLt(index, { MaterialId: id || null })
}

function onRemovePurchaseLt(index: number): void {
  store.setDefaultPurchaseLt(purchaseLtRows.value.filter((_, i) => i !== index))
}

function onAddArrivalOffset(): void {
  store.setArrivalToUsableOffsets([
    ...arrivalOffsetRows.value,
    { WarehouseCode: '', OffsetHours: 0 }
  ])
}

function onChangeArrivalOffset(index: number, patch: Partial<ArrivalOffsetItem>): void {
  const next = arrivalOffsetRows.value.map((r, i) => (i === index ? { ...r, ...patch } : { ...r }))
  store.setArrivalToUsableOffsets(next)
}

function onRemoveArrivalOffset(index: number): void {
  store.setArrivalToUsableOffsets(arrivalOffsetRows.value.filter((_, i) => i !== index))
}
</script>

<template>
  <div class="wh-editor">
    <div class="wh-head">
      <span class="wh-title">{{ title }}</span>
      <span class="wh-count"
        >{{
          isPriority
            ? priorityCodes.length
            : kind === 'purchase-lt'
              ? purchaseLtRows.length
              : arrivalOffsetRows.length
        }}
        项</span
      >
    </div>

    <!-- ===== 有序仓库码（①②）===== -->
    <template v-if="isPriority">
      <ElTable v-if="priorityCodes.length" :data="priorityCodes" size="small" border>
        <ElTableColumn label="顺序" width="70" align="center">
          <template #default="{ $index }">{{ $index + 1 }}</template>
        </ElTableColumn>
        <ElTableColumn label="仓库" min-width="260">
          <template #default="{ row, $index }">
            <ElSelect
              :model-value="row"
              filterable
              size="small"
              style="width: 100%"
              placeholder="搜索仓库编码、名称或属性"
              :disabled="disabled"
              @change="(v) => onChangePriority($index, String(v))"
            >
              <ElOptionGroup v-if="rowSections(row).raw.length" label="原材料库">
                <ElOption
                  v-for="opt in rowSections(row).raw"
                  :key="opt.code"
                  :label="opt.label"
                  :value="opt.code"
                />
              </ElOptionGroup>
              <ElOption
                v-for="opt in rowSections(row).flat"
                :key="opt.code"
                :label="opt.label"
                :value="opt.code"
              />
            </ElSelect>
          </template>
        </ElTableColumn>
        <ElTableColumn label="操作" width="150" align="center">
          <template #default="{ $index }">
            <ElTooltip content="上移一位（靠前 = 优先级更高）" placement="top">
              <ElButton
                size="small"
                text
                :disabled="disabled || $index === 0"
                @click="onMovePriority($index, -1)"
              >
                <Icon icon="vi-mdi:arrow-up" />
              </ElButton>
            </ElTooltip>
            <ElTooltip content="下移一位" placement="top">
              <ElButton
                size="small"
                text
                :disabled="disabled || $index === priorityCodes.length - 1"
                @click="onMovePriority($index, 1)"
              >
                <Icon icon="vi-mdi:arrow-down" />
              </ElButton>
            </ElTooltip>
            <ElTooltip content="移除" placement="top">
              <ElButton
                size="small"
                text
                type="danger"
                :disabled="disabled"
                @click="onRemovePriority($index)"
              >
                <Icon icon="vi-mdi:delete-outline" />
              </ElButton>
            </ElTooltip>
          </template>
        </ElTableColumn>
      </ElTable>
      <div v-else class="wh-empty">还没有配仓库。</div>

      <div v-if="!disabled" class="wh-add">
        <ElSelect
          v-model="pendingCode"
          filterable
          clearable
          size="small"
          style="width: 300px"
          placeholder="搜索并选择要加入的仓库"
          @change="onAddPriority"
        >
          <ElOptionGroup v-if="addSections(priorityCodes).raw.length" label="原材料库">
            <ElOption
              v-for="opt in addSections(priorityCodes).raw"
              :key="opt.code"
              :label="opt.label"
              :value="opt.code"
            />
          </ElOptionGroup>
          <ElOption
            v-for="opt in addSections(priorityCodes).flat"
            :key="opt.code"
            :label="opt.label"
            :value="opt.code"
          />
        </ElSelect>
        <span class="wh-tip">从上往下 = 优先级从高到低。</span>
        <span v-if="groupRaw" class="wh-tip">原材料库已排在最前。</span>
      </div>
    </template>

    <!-- ===== 默认采购提前期（③）===== -->
    <template v-else-if="kind === 'purchase-lt'">
      <!-- 物料字典拉不下来时如实说：下拉变空会让用户以为「没有物料可选」 -->
      <div v-if="!disabled && materialDictFailed" class="wh-dict-warn">
        物料字典没拉下来：已配的物料照常显示，暂时不能从列表里新选。稍后刷新页面再试。
      </div>
      <ElTable v-if="purchaseLtRows.length" :data="purchaseLtRows" size="small" border>
        <ElTableColumn label="收货仓库" min-width="220">
          <template #default="{ row, $index }">
            <ElSelect
              :model-value="row.WarehouseCode ?? ''"
              filterable
              size="small"
              style="width: 100%"
              placeholder="搜索仓库编码、名称或属性"
              :disabled="disabled"
              @change="(v) => onChangePurchaseLt($index, { WarehouseCode: String(v) })"
            >
              <ElOptionGroup v-if="rowSections(row.WarehouseCode).raw.length" label="原材料库">
                <ElOption
                  v-for="opt in rowSections(row.WarehouseCode).raw"
                  :key="opt.code"
                  :label="opt.label"
                  :value="opt.code"
                />
              </ElOptionGroup>
              <ElOption
                v-for="opt in rowSections(row.WarehouseCode).flat"
                :key="opt.code"
                :label="opt.label"
                :value="opt.code"
              />
            </ElSelect>
          </template>
        </ElTableColumn>
        <ElTableColumn label="物料（可留空）" min-width="240">
          <template #default="{ row, $index }">
            <ElSelect
              :model-value="row.MaterialId ?? ''"
              filterable
              remote
              clearable
              :remote-method="onSearchMaterial"
              :loading="materialSearching"
              size="small"
              style="width: 100%"
              placeholder="留空 = 仓库级默认"
              :disabled="disabled"
              @change="(v) => onChangePurchaseLtMaterial($index, v)"
            >
              <ElOption
                v-for="opt in materialOptionsFor(row.MaterialId)"
                :key="opt.id"
                :label="opt.label"
                :value="opt.id"
              />
            </ElSelect>
          </template>
        </ElTableColumn>
        <ElTableColumn label="默认采购提前期（天）" width="180">
          <template #default="{ row, $index }">
            <ElInputNumber
              :model-value="row.DefaultLtDays ?? 0"
              :step="1"
              :precision="2"
              size="small"
              style="width: 100%"
              :disabled="disabled"
              @change="(v) => onChangePurchaseLt($index, { DefaultLtDays: Number(v) || 0 })"
            />
          </template>
        </ElTableColumn>
        <ElTableColumn label="操作" width="80" align="center">
          <template #default="{ $index }">
            <ElButton
              size="small"
              text
              type="danger"
              :disabled="disabled"
              @click="onRemovePurchaseLt($index)"
            >
              <Icon icon="vi-mdi:delete-outline" />
            </ElButton>
          </template>
        </ElTableColumn>
      </ElTable>
      <div v-else class="wh-empty">还没有配默认采购提前期。</div>

      <div v-if="!disabled" class="wh-add">
        <ElButton size="small" @click="onAddPurchaseLt">
          <Icon icon="vi-mdi:plus" /> 添加一行
        </ElButton>
        <span class="wh-tip">按收货仓库定采购提前期；物料留空 = 该仓库统一用这个天数。</span>
        <span v-if="groupRaw" class="wh-tip">原材料库已排在最前。</span>
      </div>
    </template>

    <!-- ===== 到货可用偏移（④）===== -->
    <template v-else>
      <ElTable v-if="arrivalOffsetRows.length" :data="arrivalOffsetRows" size="small" border>
        <ElTableColumn label="收货仓库" min-width="220">
          <template #default="{ row, $index }">
            <ElSelect
              :model-value="row.WarehouseCode ?? ''"
              filterable
              size="small"
              style="width: 100%"
              placeholder="搜索仓库编码、名称或属性"
              :disabled="disabled"
              @change="(v) => onChangeArrivalOffset($index, { WarehouseCode: String(v) })"
            >
              <ElOption
                v-for="opt in rowSections(row.WarehouseCode).flat"
                :key="opt.code"
                :label="opt.label"
                :value="opt.code"
              />
            </ElSelect>
          </template>
        </ElTableColumn>
        <ElTableColumn label="到货到可用偏移（小时）" width="200">
          <template #default="{ row, $index }">
            <ElInputNumber
              :model-value="row.OffsetHours ?? 0"
              :step="1"
              :precision="2"
              size="small"
              style="width: 100%"
              :disabled="disabled"
              @change="(v) => onChangeArrivalOffset($index, { OffsetHours: Number(v) || 0 })"
            />
          </template>
        </ElTableColumn>
        <ElTableColumn label="操作" width="80" align="center">
          <template #default="{ $index }">
            <ElButton
              size="small"
              text
              type="danger"
              :disabled="disabled"
              @click="onRemoveArrivalOffset($index)"
            >
              <Icon icon="vi-mdi:delete-outline" />
            </ElButton>
          </template>
        </ElTableColumn>
      </ElTable>
      <div v-else class="wh-empty">还没有配到货可用偏移。</div>

      <div v-if="!disabled" class="wh-add">
        <ElButton size="small" @click="onAddArrivalOffset">
          <Icon icon="vi-mdi:plus" /> 添加一行
        </ElButton>
        <span class="wh-tip">到货时间之后还要等这么久，货才算可用。</span>
      </div>
    </template>
  </div>
</template>

<style lang="less" scoped>
.wh-editor {
  margin-top: 8px;
  padding: 10px 12px;
  border: 1px dashed #cbd5e1;
  border-radius: 6px;
  background: #f8fafc;
}

.wh-head {
  display: flex;
  align-items: baseline;
  gap: 8px;
  margin-bottom: 8px;
}

.wh-title {
  font-size: 13px;
  font-weight: 600;
  color: #1e293b;
}

.wh-count {
  font-size: 12px;
  color: #94a3b8;
}

.wh-empty {
  padding: 10px 0;
  font-size: 13px;
  color: #94a3b8;
}

.wh-add {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-top: 8px;
  flex-wrap: wrap;
}

.wh-tip {
  font-size: 12px;
  color: #64748b;
}

.wh-dict-warn {
  margin-bottom: 8px;
  padding: 6px 10px;
  font-size: 12px;
  color: #b45309;
  background: #fffbeb;
  border: 1px solid #fde68a;
  border-radius: 4px;
}
</style>
