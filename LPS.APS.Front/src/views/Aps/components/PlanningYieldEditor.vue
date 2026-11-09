<script setup lang="ts">
/**
 * APS V1 4号位 — 参数集「采购良率」逐项编辑器
 *
 * 落点：Procurement.PlanningYields[]（后端 DTO `PlanningYieldRule`，
 *   `FrozenStrategySnapshot.cs:253-258`）。
 *
 * 一行 = 物料 + （可选）工序 + 良率百分数：
 *  - 物料：**下拉选**（`GET /api/governance/lookups/materials`），存的是物料**编码**
 *    （运行期 `PlanningYieldResolver.cs:78` 按 `OrdinalIgnoreCase` 等值比对 ⇒ 只能存编码）。
 *    ✅ 3号位 2026-10-26 裁定 A 确认此为 MaterialCode（原 DTO 注释「数值主键 Id」有误，后端已更正）。
 *  - 工序（大工艺）：下拉 = 部门主数据里的 stageCode 去重（与运行期
 *    `MaterialStageDeptContext.StageCode` 同源）；后端 lookup 只返 TOP 200 条
 *    ⇒ 列表可能不全，允许手工补一个真实工序码。
 *  - 良率：0 < p <= 100（后端 `ValidateProcurementJson` 越界报 INVALID_YIELD）。
 *
 * 交互口径：
 *  - 物料只能从字典里选 ⇒ 拼不出假编码；字典里查不到的老编码照常显示、允许改或删。
 *  - 重复（物料 + 工序）只**提示**不阻断（真闸门在后端发布校验 DUPLICATE_PLANNING_YIELD）。
 *
 * 数据流：读 store getter（编辑态缓冲）→ 改动整份数组 → store 写动作（置脏）
 */

import { computed, onMounted, ref } from 'vue'
import {
  ElAlert,
  ElButton,
  ElInputNumber,
  ElOption,
  ElSelect,
  ElTable,
  ElTableColumn
} from 'element-plus'
import { useParameterSetsStore } from '@/store/modules/aps/parameterSets'
import { departmentLookupApi, materialLookupApi } from '@/api/aps-v1'
import type { MaterialLookupItem } from '@/api/aps-v1/types/material'
import { validatePlanningYields, type PlanningYieldItem } from '@/api/aps-v1/parameterSetYields'

defineProps<{
  /** 只读（非草稿态 / 无编辑权限） */
  disabled?: boolean
}>()

const store = useParameterSetsStore()

/* ===== 主数据下拉 ===== */

interface MaterialOption {
  code: string
  label: string
  /** 是否来自物料主数据字典 */
  known: boolean
}

const rawMaterials = ref<MaterialLookupItem[]>([])
const materialSearching = ref(false)
const materialDictFailed = ref(false)
const stageCodes = ref<string[]>([])
const stageDictFailed = ref(false)

const materialOptions = computed<MaterialOption[]>(() =>
  rawMaterials.value.map((m) => {
    const code = String(m.code ?? '').trim()
    const name = String(m.name ?? '').trim()
    return { code, label: name ? `${code}（${name}）` : code, known: true }
  })
)

/**
 * 某行可选项 = 当前字典全部 + 该行自己的编码（字典里没有时补一个「字典里没有」项）。
 * 否则用户连「删掉一个老编码」都做不到。
 */
function materialOptionsFor(current?: string): MaterialOption[] {
  const opts = [...materialOptions.value]
  const cur = (current ?? '').trim()
  if (cur && !opts.some((o) => o.code.toLowerCase() === cur.toLowerCase())) {
    opts.push({ code: cur, label: `${cur}（物料字典里没有）`, known: false })
  }
  return opts
}

/** 工序可选项 = 部门主数据里的 stageCode 去重 + 该行自己的值 */
function stageOptionsFor(current?: string): string[] {
  const opts = [...stageCodes.value]
  const cur = (current ?? '').trim()
  if (cur && !opts.some((o) => o.toLowerCase() === cur.toLowerCase())) {
    opts.push(cur)
  }
  return opts
}

/** 拉主数据（物料 + 部门→工序码） */
async function loadLookups(): Promise<void> {
  const [materials, departments] = await Promise.all([
    materialLookupApi.listActiveMaterials(),
    departmentLookupApi.listDepartments()
  ])
  rawMaterials.value = materials
  materialDictFailed.value = materials.length === 0
  const seen = new Set<string>()
  const stages: string[] = []
  for (const d of departments) {
    const code = String(d.stageCode ?? '').trim()
    if (!code || seen.has(code)) continue
    seen.add(code)
    stages.push(code)
  }
  stageCodes.value = stages.sort((a, b) => a.localeCompare(b))
  stageDictFailed.value = stages.length === 0
}

/** 远程搜索物料（后端 lookup 只返 TOP 200 ⇒ 必须能按关键词补搜） */
async function onSearchMaterial(query: string): Promise<void> {
  const kw = String(query ?? '').trim()
  if (!kw) return
  materialSearching.value = true
  try {
    const found = await materialLookupApi.searchMaterials(kw)
    const seen = new Set(rawMaterials.value.map((m) => String(m.code ?? '').trim()))
    const extra = found.filter((m) => {
      const code = String(m.code ?? '').trim()
      if (!code || seen.has(code)) return false
      seen.add(code)
      return true
    })
    if (extra.length) rawMaterials.value = [...rawMaterials.value, ...extra]
  } finally {
    materialSearching.value = false
  }
}

onMounted(() => {
  void loadLookups()
})

/* ===== 行编辑 ===== */

const rows = computed<PlanningYieldItem[]>(() => store.planningYields)

/** 校验问题清单（口径 = 后端发布校验；只提示不阻断） */
const problems = computed<string[]>(() => validatePlanningYields(rows.value))

function patchRow(index: number, patch: Partial<PlanningYieldItem>): void {
  const next = rows.value.map((r, i) => (i === index ? { ...r, ...patch } : { ...r }))
  store.setPlanningYields(next)
}

function onChangeMaterial(index: number, code: string): void {
  patchRow(index, { MaterialId: String(code ?? '').trim() })
}

function onChangeStage(index: number, stage: string): void {
  const next = rows.value.map((r, i) => {
    if (i !== index) return { ...r }
    const out: PlanningYieldItem = { ...r }
    const s = String(stage ?? '').trim()
    // 工序为空 = 物料级默认（后端 string? 缺省即 null）⇒ 不写这个键
    if (s) out.StageCode = s
    else delete out.StageCode
    return out
  })
  store.setPlanningYields(next)
}

function onChangeYield(index: number, value: unknown): void {
  const p = Number(value)
  // 输入框已夹 0.01~100；这里再兜一层，避免写出 null / NaN 让后端整块反序列化失败
  const safe = Number.isFinite(p) && p > 0 && p <= 100 ? p : 100
  patchRow(index, { YieldPercent: safe })
}

function onAdd(): void {
  store.setPlanningYields([...rows.value, { MaterialId: '', YieldPercent: 100 }])
}

function onRemove(index: number): void {
  store.setPlanningYields(rows.value.filter((_, i) => i !== index))
}
</script>

<template>
  <div class="py-editor">
    <div class="py-head">
      <span class="py-title">采购良率</span>
      <span class="py-count">{{ rows.length }} 项</span>
    </div>

    <ElAlert v-if="problems.length" type="warning" :closable="false" show-icon class="py-alert">
      <template #title>有 {{ problems.length }} 处需要改</template>
      {{ problems.join('；') }}
    </ElAlert>

    <ElAlert v-if="materialDictFailed" type="info" :closable="false" show-icon class="py-alert">
      <template #title>物料字典没拉下来</template>
      现在只能看已配置的物料，不能从列表里新选。稍后刷新页面再试。
    </ElAlert>

    <ElTable v-if="rows.length" :data="rows" size="small" border>
      <ElTableColumn label="物料" min-width="240">
        <template #default="{ row, $index }">
          <ElSelect
            :model-value="row.MaterialId ?? ''"
            filterable
            remote
            :remote-method="onSearchMaterial"
            :loading="materialSearching"
            size="small"
            style="width: 100%"
            placeholder="搜索物料编码或名称"
            :disabled="disabled"
            @change="(v) => onChangeMaterial($index, String(v))"
          >
            <ElOption
              v-for="opt in materialOptionsFor(row.MaterialId)"
              :key="opt.code"
              :label="opt.label"
              :value="opt.code"
            />
          </ElSelect>
        </template>
      </ElTableColumn>
      <ElTableColumn label="工序（大工艺）" min-width="180">
        <template #default="{ row, $index }">
          <ElSelect
            :model-value="row.StageCode ?? ''"
            filterable
            allow-create
            clearable
            size="small"
            style="width: 100%"
            placeholder="留空 = 整个物料"
            :disabled="disabled"
            @change="(v) => onChangeStage($index, String(v ?? ''))"
          >
            <ElOption
              v-for="code in stageOptionsFor(row.StageCode)"
              :key="code"
              :label="code"
              :value="code"
            />
          </ElSelect>
        </template>
      </ElTableColumn>
      <ElTableColumn label="良率（%）" width="150">
        <template #default="{ row, $index }">
          <ElInputNumber
            :model-value="row.YieldPercent ?? 100"
            :min="0.01"
            :max="100"
            :step="0.5"
            :precision="2"
            size="small"
            style="width: 100%"
            :disabled="disabled"
            @change="(v) => onChangeYield($index, v)"
          />
        </template>
      </ElTableColumn>
      <ElTableColumn label="操作" width="70" align="center">
        <template #default="{ $index }">
          <ElButton size="small" text type="danger" :disabled="disabled" @click="onRemove($index)">
            <Icon icon="vi-mdi:delete-outline" />
          </ElButton>
        </template>
      </ElTableColumn>
    </ElTable>
    <div v-else class="py-empty">还没配采购良率。</div>

    <div v-if="!disabled" class="py-add">
      <ElButton size="small" @click="onAdd"> <Icon icon="vi-mdi:plus" /> 添加一行 </ElButton>
      <span class="py-tip"> 工序留空 = 这个物料所有工序都用这个良率；良率 100 = 不扣减。 </span>
      <span v-if="stageDictFailed" class="py-tip">工序码列表没拉下来，可以直接手填。</span>
    </div>
  </div>
</template>

<style lang="less" scoped>
.py-editor {
  margin-top: 8px;
  padding: 10px 12px;
  border: 1px dashed #cbd5e1;
  border-radius: 6px;
  background: #f8fafc;
}

.py-head {
  display: flex;
  align-items: baseline;
  gap: 8px;
  margin-bottom: 8px;
}

.py-title {
  font-size: 13px;
  font-weight: 600;
  color: #1e293b;
}

.py-count {
  font-size: 12px;
  color: #94a3b8;
}

.py-alert {
  margin-bottom: 8px;
}

.py-empty {
  padding: 10px 0;
  font-size: 13px;
  color: #94a3b8;
}

.py-add {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-top: 8px;
  flex-wrap: wrap;
}

.py-tip {
  font-size: 12px;
  color: #64748b;
}
</style>
