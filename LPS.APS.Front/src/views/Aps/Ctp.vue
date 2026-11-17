<script setup lang="ts">
/**
 * APS V1 4号位 — CTP / 插单评估（页面 4 / U07 / U08 / U09 / U11 / U15）
 *
 * 4号位文档第 7-8 节约束：
 *  - CTP 仅展示评估结果，不暴露"采用"按钮（U11）
 *  - Estimated 供给必须显示徽章（U07）
 *  - 根因优先（U09），不只显示 DUE_DATE_RISK
 *  - impactedOrderCount 超阈值仅 Warning，不表示截断（U15）
 *  - 跨 Domain 时显示链式 WHATIF（V1 仅汇总展示）
 *
 * 2026-11-14 工厂视角改版：
 *  - 物料下拉改**远程搜索**：后端不带 search 时 LIMIT 200 ⇒ 200 条之外的物料在下拉里根本选不到
 *  - 工厂下拉只放 5 个真业务码（后端主数据混着 44 条 TF_* 测试夹具）
 *  - 后端 501（试算能力未接入）翻成大白话，不把内部归属说明甩给用户
 *  - 评估时间用**真实评估时刻**（不是渲染时刻）
 *  - 加「复制结论」：工厂用户要把结论直接发给客户
 *  - 撤页头大标题页头（2026-10-16 定调），标题进卡片头
 */

import { computed, onMounted, reactive, ref } from 'vue'
import dayjs from 'dayjs'
import { storeToRefs } from 'pinia'
import { useRoute } from 'vue-router'
import { useCtpStore } from '@/store/modules/aps/ctp'
import { useDomainStore } from '@/store/modules/aps/domain'
import { domainApi } from '@/api/aps-v1/domain'
import { materialLookupApi } from '@/api/aps-v1/materialLookup'
import type { MaterialLookupItem } from '@/api/aps-v1/types'
import {
  BIZ_ISSUE_FACTORY_CODES,
  ESTIMATED_BADGE_TEXT,
  reasonLabelOf,
  type CtpInput,
  type FactorySummaryDto
} from '@/api/aps-v1/types'

import {
  ElAlert,
  ElButton,
  ElCard,
  ElDatePicker,
  ElEmpty,
  ElInput,
  ElInputNumber,
  ElMessage,
  ElOption,
  ElSelect,
  ElTable,
  ElTableColumn,
  ElTag,
  ElTooltip
} from 'element-plus'

const ctpStore = useCtpStore()
const { result, history, loading, error, errorCode, errorTraceId, evaluatedAt, hasResult, isOverThreshold } =
  storeToRefs(ctpStore)

const domainStore = useDomainStore()
function humanLabel(domainKey: string | undefined | null): string {
  return domainStore.label(domainKey)
}

/** 模板内拿到的就一定是 NonNull 的（与 hasResult 配套使用） */
const resultNN = computed(() => result.value!)

/** 下拉数据源加载失败提示（不伪造选项，如实告诉用户） */
const optionLoadError = ref<string | null>(null)
/** 物料下拉提示（没搜到 / 查询失败） */
const materialNotice = ref<string | null>(null)

/* ===== 表单本地副本（输入体验用，提交时再写回 store）===== */
const form = reactive({
  orderCanonicalId: '',
  materialCode: '',
  quantity: 100,
  // 空默认：必须由用户从后端真实工厂列表里选，不预填任何硬编码代码
  factoryCode: '',
  requestedDueDate: new Date(Date.now() + 10 * 86400_000).toISOString().slice(0, 10),
  /** 业务用途标签（P0-04：审核报告第 8 节 — 页面提交必须真正传 Purpose） */
  purpose: 'CTP' as 'CTP' | 'INSERT_IMPACT_ANALYSIS'
})

/** 工厂下拉数据源（real API）
 *  - 端点：GET /api/governance/factories（domainApi.listFactories，3号位 落地）
 *  - 返回 FactorySummaryDto[]：id + code + name（后端已 IsActive 过滤）
 *  - ⚠️ 该表混着 44 条测试夹具（`TF_*` / "Test Factory…" / "S1~S4 Factory…"，且 IsActive=1）
 *    ⇒ 铺全表会把测试厂推给用户；按 5号位 11-11 复函 §二 的真业务码白名单过滤
 *    （与异常解释页同一份 BIZ_ISSUE_FACTORY_CODES，`CN6课` 就是正式码，别改写）。
 *  - 失败：空下拉 + 如实提示，**不硬编码兜底**。
 */
const FACTORY_OPTIONS = ref<FactorySummaryDto[]>([])

/** 物料下拉数据源（远程搜索）
 *  - 端点：GET /api/governance/lookups/materials（search=片段 / activeOnly=true）
 *  - ⚠️ 不带 search 时后端 LIMIT 200 ⇒ 200 条之外的物料用下拉根本选不到
 *    ⇒ 改远程搜索：用户敲编码/名称片段，后端 LIKE 查。
 *  - ⚠️ ElSelect 远程模式（filterable + remote）下 Element Plus 会跳过本地过滤，
 *    收窄只能由后端 search 参数完成（前端不再自己 filter）。
 */
const MATERIAL_OPTIONS = ref<MaterialLookupItem[]>([])
const materialLoading = ref(false)
/** 当前选中的物料（远程搜索换了列表后，ElSelect 找不到原选项就只显示编码） */
const selectedMaterial = ref<MaterialLookupItem | null>(null)
let materialSeq = 0

/** 把已选物料并回选项列表，保证下拉里显示的是「编码 名称」而不是裸编码 */
function withSelectedMaterial(list: MaterialLookupItem[]): MaterialLookupItem[] {
  const sel = selectedMaterial.value
  if (!sel || list.some((o) => o.code === sel.code)) return list
  return [sel, ...list]
}

async function loadMaterials(query: string): Promise<void> {
  const seq = ++materialSeq
  const q = String(query ?? '').trim()
  materialLoading.value = true
  try {
    const list = q
      ? await materialLookupApi.searchMaterialsOrThrow(q)
      : await materialLookupApi.listActiveMaterialsOrThrow()
    if (seq !== materialSeq) return
    MATERIAL_OPTIONS.value = withSelectedMaterial(list)
    materialNotice.value = q && !list.length ? `没有编码或名称里带「${q}」的物料` : null
  } catch (err) {
    if (seq !== materialSeq) return
    MATERIAL_OPTIONS.value = []
    materialNotice.value = `物料查询失败，请稍后重试（${(err as Error)?.message ?? '未知错误'}）`
  } finally {
    if (seq === materialSeq) materialLoading.value = false
  }
}

function onMaterialSearch(query: string): void {
  void loadMaterials(query)
}

function onMaterialChange(code: string): void {
  selectedMaterial.value = MATERIAL_OPTIONS.value.find((o) => o.code === code) ?? null
  materialNotice.value = null
}

/** P1-08 入口：Gantt/Workbench 可带 ?purpose=INSERT_IMPACT_ANALYSIS 跳转；Ctp 在挂载时读取并覆盖默认值 */
const route = useRoute()
onMounted(() => {
  if (route.query.purpose === 'INSERT_IMPACT_ANALYSIS') {
    form.purpose = 'INSERT_IMPACT_ANALYSIS'
  }
  // 工厂 / 物料下拉**只展示后端真实数据**。拉取失败时给空下拉 + 红色提示，绝不回退硬编码值 ——
  // 假选项会让用户选到实库里不存在的工厂/物料，提交后才被后端 400（订单页 OD05 同类整改）。
  domainApi
    .listFactories()
    .then((list: FactorySummaryDto[]) => {
      const allow = new Set<string>(BIZ_ISSUE_FACTORY_CODES)
      FACTORY_OPTIONS.value = list.filter((f) => allow.has((f.code ?? '').trim()))
      if (!FACTORY_OPTIONS.value.length) {
        optionLoadError.value = '没有可用的工厂，请联系管理员检查工厂主数据'
      }
    })
    .catch((err) => {
      FACTORY_OPTIONS.value = []
      optionLoadError.value = `工厂列表加载失败，请刷新重试（${(err as Error)?.message ?? '未知错误'}）`
    })
  void loadMaterials('')
})

/* ===== 字典 =====
 * 根因码复用冻结 `07 v5.1.13 §八.1` 权威 15 码（与甘特 / Explanation 页同一份
 * `REASON_CODE_LABELS`，不再维护第二套自拟码表）。
 * ⚠️ 查表必须走 `reasonLabelOf`（全站唯一口径，types/explanation.ts）：
 *    后端将来若返回 15 码之外的受控扩展值，裸索引会得到 `undefined`
 *    ⇒ ElTag 渲染成**空白标签**（甘特 GT02/GT10 踩过）；
 *    实库 95% 的 ReasonCode 是空串（写方未填），更不能裸索引。
 */
/** 根因码 → 标签配色：只看语义大类，未知码给中性 info */
const reasonTagTypeOf = (code: string): 'success' | 'warning' | 'danger' | 'info' => {
  if (code === 'EQUIPMENT_BREAKDOWN_RISK') return 'danger'
  if (code === 'DUE_DATE_RISK' || code === 'CROSS_DOMAIN_VERSION_MISMATCH_RISK') return 'warning'
  if (code === 'ROUTING_FALLBACK' || code === 'STAGE_LEADTIME_FALLBACK' || code === 'BOM_DEGRADE')
    return 'warning'
  return 'info'
}

/* ===== KPI: 评估结果核心指标 ===== */
const requestedDueMs = computed(() => new Date(form.requestedDueDate).getTime())
const earliestMs = computed(() =>
  result.value ? new Date(result.value.earliestCompletion).getTime() : 0
)
const delayDays = computed(() => {
  if (!result.value) return 0
  return Math.max(0, Math.round((earliestMs.value - requestedDueMs.value) / 86400_000))
})

/** P0-04：purpose-aware 文案（标题/说明/历史过滤）
 *  - CTP 给客户承诺
 *  - INSERT_IMPACT_ANALYSIS 给内部决策，仅试算不可激活
 */
const PURPOSE_TITLE: Record<'CTP' | 'INSERT_IMPACT_ANALYSIS', string> = {
  CTP: '插单评估',
  INSERT_IMPACT_ANALYSIS: '插单影响分析'
}
const PURPOSE_SUB: Record<'CTP' | 'INSERT_IMPACT_ANALYSIS', string> = {
  CTP: '填物料、数量、工厂和客户交期，判断这单能不能按期做完',
  INSERT_IMPACT_ANALYSIS: '评估这单插进来对其它订单/资源的影响（仅试算，不会改为正式计划）'
}
const PURPOSE_HISTORY_TAG: Record<'CTP' | 'INSERT_IMPACT_ANALYSIS', 'primary' | 'info'> = {
  CTP: 'primary',
  INSERT_IMPACT_ANALYSIS: 'info'
}
/** 历史/标签页展示用途用（避免前端暴露 raw enum "CTP" / "INSERT_IMPACT_ANALYSIS"） */
const PURPOSE_LABEL: Record<'CTP' | 'INSERT_IMPACT_ANALYSIS', string> = {
  CTP: '可靠交期判断',
  INSERT_IMPACT_ANALYSIS: '插单影响分析'
}
/** 需求保护冲突标签中文化（未知名词回退显示原值，避免漏翻译错位） */
const CONFLICT_TYPE_LABEL: Record<string, string> = {
  PROTECTION_OVERLAP: '保护重叠'
}
/** 模板可调用的安全查找（row.input.purpose 在模板里类型擦除为 string，不能用 as 强转） */
function purposeLabelOf(p: string): string {
  return PURPOSE_LABEL[p as keyof typeof PURPOSE_LABEL] ?? p
}
function purposeTagTypeOf(p: string): 'primary' | 'info' {
  return PURPOSE_HISTORY_TAG[p as keyof typeof PURPOSE_HISTORY_TAG] ?? 'info'
}
function conflictTypeLabelOf(t: string): string {
  return CONFLICT_TYPE_LABEL[t] ?? t
}

/** 按当前 purpose 过滤 HISTORY（CTP / INSERT_IMPACT_ANALYSIS 隔离回看） */
const filteredHistory = computed(() =>
  history.value.filter((h) => h.input.purpose === form.purpose)
)

/* ===== 错误 → 工厂用户看得懂的话 =====
 *  - 501：试算核心能力后端未接入（报文里带内部归属说明，不上屏）
 *  - 400 且提到 DomainKey：物料/工厂落不到同一个排程域
 *  - 其它：原样透出正文（traceId 放次要位置，不进标题）
 */
const friendlyError = computed(() => {
  const raw = error.value
  if (!raw) return null
  if (errorCode.value === 501) {
    return '交期试算服务还没开通，现在算不出结论。开通后这里会直接给出按期/延期判断。'
  }
  if (/domainkey/i.test(raw)) {
    return '这个物料在这个工厂下找不到对应的排程域，换个物料或工厂再试。'
  }
  return raw
})

/* ===== 结论复制（工厂用户要把结论直接发给客户/销售）===== */
const verdictText = computed(() => {
  const r = result.value
  if (!r) return ''
  const factoryName =
    FACTORY_OPTIONS.value.find((f) => f.code === form.factoryCode)?.name ?? form.factoryCode
  const materialName = selectedMaterial.value?.name
  return [
    `插单评估结论（${PURPOSE_LABEL[form.purpose]}）`,
    `物料：${materialName ? `${materialName}（${form.materialCode}）` : form.materialCode} × ${form.quantity}`,
    `工厂：${factoryName}`,
    `客户请求交期：${form.requestedDueDate}`,
    `判定：${r.meetsRequestedDueDate ? '可按期完成' : '无法按期完成'}`,
    `最早完成日期：${dayjs(r.earliestCompletion).format('YYYY-MM-DD')}${
      r.meetsRequestedDueDate ? '' : `（延期 ${delayDays} 天）`
    }`,
    `主要瓶颈：${r.mainBottleneck}`,
    `受影响订单：${r.impactedOrderCount} 个`
  ].join('\n')
})

async function copyVerdict(): Promise<void> {
  const text = verdictText.value
  if (!text) return
  try {
    await navigator.clipboard.writeText(text)
    ElMessage.success('结论已复制，可直接发给客户')
  } catch {
    ElMessage.warning('复制失败，请手动选中结论文字复制')
  }
}

/* ===== 操作 ===== */
async function onSubmit(): Promise<void> {
  // 兜底：必填校验防止 422（撤下拉后用户不再被「请选择事业部」卡住，会直接点评估）
  if (!form.materialCode || !form.factoryCode || !form.requestedDueDate) {
    ElMessage.warning('请填写物料、工厂、客户请求交期后再评估')
    return
  }
  await ctpStore.evaluate({
    // 订单号可留空：生成一个**人能读**的本次评估编号（原来的 PO-<时间戳> 看不出是哪天）
    orderCanonicalId:
      form.orderCanonicalId || `PO-${dayjs().format('YYYYMMDD-HHmm')}`,
    materialCode: form.materialCode,
    quantity: form.quantity,
    factoryCode: form.factoryCode,
    requestedDueDate: form.requestedDueDate,
    purpose: form.purpose
  })
}
function onReset(): void {
  Object.assign(form, {
    orderCanonicalId: '',
    materialCode: '',
    quantity: 100,
    factoryCode: '',
    requestedDueDate: new Date(Date.now() + 10 * 86400_000).toISOString().slice(0, 10),
    purpose: 'CTP'
  })
  selectedMaterial.value = null
  materialNotice.value = null
  ctpStore.reset()
}
function onLoadHistory(entryId: number): void {
  const entry = history.value.find((h) => h.id === entryId)
  if (!entry) return
  ctpStore.loadFromHistory(entry)
  // 兼容旧历史（撤下拉前的记录可能带 domainKey 字段，丢弃即可）
  const { domainKey: _drop, ...rest } = entry.input as CtpInput & { domainKey?: string }
  void _drop
  Object.assign(form, rest)
  // 历史里的物料可能不在当前选项列表里（远程搜索后列表会被替换）⇒ 找回来，否则下拉只显示裸编码
  selectedMaterial.value =
    MATERIAL_OPTIONS.value.find((o) => o.code === form.materialCode) ?? null
}
</script>

<template>
  <div class="aps-ctp">
    <!-- ===== 输入表单（2026-11-14：撤页头大标题页头，标题进卡片头）===== -->
    <ElCard class="panel">
      <template #header>
        <div class="panel-header">
          <span>
            {{ PURPOSE_TITLE[form.purpose] }}
            <ElTag
              v-if="form.purpose === 'INSERT_IMPACT_ANALYSIS'"
              size="small"
              effect="dark"
              type="info"
              style="margin-left: 6px"
            >
              试算
            </ElTag>
          </span>
          <span class="muted-tag">{{ PURPOSE_SUB[form.purpose] }}</span>
        </div>
      </template>
      <ElAlert
        v-if="optionLoadError"
        type="error"
        :closable="false"
        show-icon
        :title="optionLoadError"
        class="option-error"
      />
      <ElAlert
        v-if="materialNotice"
        type="warning"
        :closable="false"
        show-icon
        :title="materialNotice"
        class="option-error"
      />
      <div class="input-form">
        <div class="input-field">
          <span class="field-label">订单号</span>
          <ElInput
            v-model="form.orderCanonicalId"
            placeholder="可空，留空自动生成"
            size="default"
            class="field-input"
          />
        </div>
        <div class="input-field">
          <span class="field-label">
            物料<span class="required-mark">*</span>
          </span>
          <ElSelect
            v-model="form.materialCode"
            filterable
            remote
            clearable
            reserve-keyword
            :remote-method="onMaterialSearch"
            :loading="materialLoading"
            placeholder="输入物料编码或名称"
            class="field-input"
            @change="onMaterialChange"
          >
            <ElOption
              v-for="opt in MATERIAL_OPTIONS"
              :key="opt.masterId"
              :label="`${opt.code} ${opt.name}`"
              :value="opt.code"
            />
            <template #empty>
              <span class="opt-empty">{{ materialLoading ? '查询中…' : '没有匹配的物料' }}</span>
            </template>
          </ElSelect>
        </div>
        <div class="input-field">
          <span class="field-label">
            数量<span class="required-mark">*</span>
          </span>
          <ElInputNumber
            v-model="form.quantity"
            :min="1"
            :max="9999"
            :step="10"
            controls-position="right"
            class="field-input"
          />
        </div>
        <div class="input-field">
          <span class="field-label">
            工厂<span class="required-mark">*</span>
          </span>
          <ElSelect
            v-model="form.factoryCode"
            filterable
            placeholder="选择工厂"
            class="field-input"
          >
            <ElOption
              v-for="opt in FACTORY_OPTIONS"
              :key="opt.code"
              :label="`${opt.name}（${opt.code}）`"
              :value="opt.code"
            />
          </ElSelect>
        </div>
        <div class="input-field">
          <span class="field-label">
            客户请求交期<span class="required-mark">*</span>
          </span>
          <ElDatePicker
            v-model="form.requestedDueDate"
            type="date"
            value-format="YYYY-MM-DD"
            placeholder="选择日期"
            class="field-input"
          />
        </div>
        <div class="input-field">
          <span class="field-label">业务用途</span>
          <ElSelect v-model="form.purpose" class="field-input">
            <ElOption label="可靠交期判断" value="CTP" />
            <ElOption label="插单影响分析" value="INSERT_IMPACT_ANALYSIS" />
          </ElSelect>
        </div>
        <div class="input-actions">
          <span class="input-hint">物料、工厂、客户请求交期必填；订单号留空会自动生成</span>
          <div class="input-btns">
            <ElButton
              type="primary"
              :loading="loading"
              :disabled="!form.materialCode || !form.factoryCode || !form.requestedDueDate"
              @click="onSubmit"
            >
              <Icon icon="vi-ep:circle-check" /> 评估
            </ElButton>
            <ElButton @click="onReset">重置</ElButton>
          </div>
        </div>
      </div>
    </ElCard>

    <!-- ===== 错误条（后端报文翻成大白话；内部编号/traceId 只放次要位置）===== -->
    <ElAlert
      v-if="friendlyError"
      type="error"
      :closable="false"
      show-icon
      :title="friendlyError"
      class="eval-error"
    >
      <span v-if="errorCode" class="err-detail">
        技术信息：{{ errorCode }}{{ errorTraceId ? ' / ' + errorTraceId : '' }}
      </span>
    </ElAlert>

    <!-- ===== 结果区 ===== -->
    <div v-if="hasResult" class="result-area">
      <!-- 结果工具条：把结论复制走（工厂用户要直接发给客户/销售） -->
      <div class="result-toolbar">
        <span class="muted-tag">
          评估时刻：{{ evaluatedAt ? dayjs(evaluatedAt).format('YYYY-MM-DD HH:mm') : '-' }}
        </span>
        <ElButton size="small" @click="copyVerdict">
          <Icon icon="vi-ep:copy-document" /> 复制结论
        </ElButton>
      </div>
      <!-- 顶部判定卡 -->
      <div class="verdict-row">
        <div class="verdict-card" :class="resultNN.meetsRequestedDueDate ? 'pass' : 'fail'">
          <Icon
            class="verdict-icon"
            :icon="
              resultNN.meetsRequestedDueDate ? 'vi-mdi:check-decagram' : 'vi-mdi:alert-octagon'
            "
          />
          <div class="verdict-body">
            <div class="verdict-title">
              <template v-if="form.purpose === 'CTP'">
                {{ resultNN.meetsRequestedDueDate ? '可按期完成' : '无法按期完成' }}
              </template>
              <template v-else>
                {{
                  resultNN.meetsRequestedDueDate
                    ? `插单可插入（影响 ${resultNN.impactedOrderCount} 单）`
                    : `插单需权衡（影响 ${resultNN.impactedOrderCount} 单）`
                }}
              </template>
            </div>
            <div class="verdict-sub">
              {{
                form.purpose === 'CTP'
                  ? '按当前生效版本试算，不改动正式计划'
                  : '仅试算，不改为正式计划'
              }}
            </div>
          </div>
        </div>
        <div class="verdict-card stat">
          <div class="stat-label">客户请求交期</div>
          <div class="stat-val">{{ form.requestedDueDate }}</div>
        </div>
        <div class="verdict-card stat warn" v-if="!resultNN.meetsRequestedDueDate">
          <div class="stat-label">最早完成日期</div>
          <div class="stat-val">{{ dayjs(resultNN.earliestCompletion).format('YYYY-MM-DD') }}</div>
          <div class="stat-sub">延期 {{ delayDays }} 天</div>
        </div>
        <div class="verdict-card stat ok" v-else>
          <div class="stat-label">最早完成日期</div>
          <div class="stat-val">{{ dayjs(resultNN.earliestCompletion).format('YYYY-MM-DD') }}</div>
          <div class="stat-sub">准时</div>
        </div>
      </div>

      <!-- Estimated 徽章 (U07) -->
      <ElAlert
        v-if="resultNN.dependsOnEstimatedSupply"
        type="warning"
        :closable="false"
        show-icon
        class="est-alert"
      >
        <template #title>
          <ElTag effect="dark" size="small" style="background: #8b5cf6; border-color: #8b5cf6">
            估算值
          </ElTag>
          {{ ESTIMATED_BADGE_TEXT }}
        </template>
      </ElAlert>

      <!-- 受影响订单数超阈值（U15） -->
      <ElAlert v-if="isOverThreshold" type="warning" :closable="false" show-icon class="over-alert">
        <template #title>
          <ElTag effect="dark" size="small" type="warning">超出阈值</ElTag>
          评估影响 <strong>{{ resultNN.impactedOrderCount }}</strong> 个订单，已超过阈值
          <strong>{{ resultNN.maxImpactedOrdersThreshold }}</strong
          >，仅给出警示，不表示截断。
        </template>
      </ElAlert>

      <!-- 主要瓶颈 + 原因 -->
      <div class="content-row">
        <ElCard class="panel">
          <template #header>
            <div class="panel-header">
              <span> 主要瓶颈 </span>
              <span class="muted-tag">根因优先</span>
            </div>
          </template>
          <div class="bottleneck-box">
            <Icon class="bn-icon" icon="vi-mdi:chart-gantt" />
            <span>{{ resultNN.mainBottleneck }}</span>
          </div>
          <h4 class="reason-title">原因（按优先级排序）</h4>
          <div class="reason-list">
            <div
              v-for="(r, idx) in resultNN.reasons"
              :key="idx"
              class="reason-item"
              :class="{ est: r.isEstimated }"
            >
              <span class="reason-rank">{{ idx + 1 }}</span>
              <div class="reason-body">
                <div class="reason-head">
                  <ElTag size="small" :type="reasonTagTypeOf(r.reasonCode)" effect="light">
                    {{ reasonLabelOf(r.reasonCode) }}
                  </ElTag>
                  <ElTooltip v-if="r.isEstimated" :content="ESTIMATED_BADGE_TEXT" placement="top">
                    <ElTag
                      size="small"
                      effect="dark"
                      style="background: #8b5cf6; border-color: #8b5cf6"
                    >
                      估算值
                    </ElTag>
                  </ElTooltip>
                  <span v-if="r.relatedRef" class="reason-rel">
                    {{ r.relatedRef }}
                  </span>
                </div>
                <div class="reason-desc">{{ r.description }}</div>
              </div>
            </div>
          </div>
        </ElCard>

        <!-- 受影响订单 -->
        <ElCard class="panel">
          <template #header>
            <div class="panel-header">
              <span> 受影响订单 </span>
              <ElTag size="small" :type="isOverThreshold ? 'danger' : 'info'">
                {{ resultNN.impactedOrders.length }} / {{ resultNN.maxImpactedOrdersThreshold }}
              </ElTag>
            </div>
          </template>
          <div v-if="!resultNN.impactedOrders.length" class="panel-empty">
            <ElEmpty description="无受影响订单" />
          </div>
          <ElTable v-else :data="resultNN.impactedOrders" size="small" border>
            <ElTableColumn prop="orderNo" label="订单号" width="150" />
            <ElTableColumn label="原完成" width="140">
              <template #default="{ row }">
                {{ dayjs(row.originalCompletion).format('MM-DD') }}
              </template>
            </ElTableColumn>
            <ElTableColumn label="新完成" width="140">
              <template #default="{ row }">
                <span :class="{ del: row.becomesDelayed }">
                  {{ row.newCompletion ? dayjs(row.newCompletion).format('MM-DD') : '-' }}
                </span>
              </template>
            </ElTableColumn>
            <ElTableColumn label="延期" width="70" align="center">
              <template #default="{ row }">
                <ElTag size="small" :type="row.becomesDelayed ? 'danger' : 'success'">
                  {{ row.becomesDelayed ? '是' : '否' }}
                </ElTag>
              </template>
            </ElTableColumn>
            <ElTableColumn label="需求保护冲突" width="120" align="center">
              <template #default="{ row }">
                <ElTag v-if="row.hasProtectionConflict" size="small" type="warning"> 冲突 </ElTag>
                <span v-else class="muted">-</span>
              </template>
            </ElTableColumn>
          </ElTable>

          <!-- 需求保护冲突明细 -->
          <h4 v-if="resultNN.protectionConflicts.length" class="reason-title">
            需求保护冲突明细
          </h4>
          <div v-if="resultNN.protectionConflicts.length" class="conflict-list">
            <div v-for="(c, i) in resultNN.protectionConflicts" :key="i" class="conflict-item">
              <ElTag size="small" type="warning" effect="dark">
                {{ conflictTypeLabelOf(c.conflictType) }}
              </ElTag>
              <span class="conflict-msg">{{ c.description }}</span>
            </div>
          </div>
        </ElCard>
      </div>

      <!-- 跨 Domain 链式 WHATIF -->
      <ElCard v-if="resultNN.isCrossDomainChained && resultNN.domainResults?.length" class="panel">
        <template #header>
          <div class="panel-header">
            <span>
              跨排程域链式评估
              <ElTag size="small" type="info" effect="plain">阶段 B 跨域</ElTag>
            </span>
            <span class="muted-tag">
              {{
                form.purpose === 'INSERT_IMPACT_ANALYSIS'
                  ? 'INSERT链式：仅汇总影响，不展开递归'
                  : 'V1 仅汇总展示，不展开递归'
              }}
            </span>
            <ElTag
              v-if="form.purpose === 'INSERT_IMPACT_ANALYSIS'"
              size="small"
              type="info"
              effect="plain"
              style="margin-left: 8px"
            >
              插单影响分析
            </ElTag>
          </div>
        </template>
        <div class="domain-chain">
          <div
            v-for="(d, idx) in resultNN.domainResults"
            :key="d.domainKey"
            class="chain-item"
            :class="{ ok: d.success, fail: !d.success }"
          >
            <div class="chain-head">
              <ElTag size="small" :type="d.success ? 'success' : 'danger'" effect="dark">
                {{ humanLabel(d.domainKey) }}
              </ElTag>
              <span class="chain-time">
                最早完成 {{ dayjs(d.earliestCompletion).format('MM-DD') }}
              </span>
              <Icon
                v-if="idx < (resultNN.domainResults?.length ?? 0) - 1"
                class="chain-arrow"
                icon="vi-mdi:arrow-right-thick"
              />
            </div>
            <div class="chain-bn">{{ d.mainBottleneck }}</div>
            <div v-if="d.failureReason" class="chain-fail">{{ d.failureReason }}</div>
          </div>
        </div>
      </ElCard>

      <!-- 历史评估（P0-04：按 purpose 过滤，CTP / INSERT_IMPACT_ANALYSIS 隔离回看） -->
      <ElCard v-if="filteredHistory.length" class="panel">
        <template #header>
          <div class="panel-header">
            <span>
              本次会话评估历史
              <ElTag size="small" effect="plain" :type="purposeTagTypeOf(form.purpose)">
                {{ purposeLabelOf(form.purpose) }}
              </ElTag>
              （最多 5 条）
            </span>
            <ElButton size="small" link @click="ctpStore.clearHistory">清空</ElButton>
          </div>
        </template>
        <ElTable :data="filteredHistory" size="small" border>
          <ElTableColumn label="时间" width="140">
            <template #default="{ row }">
              {{ dayjs(row.evaluatedAt).format('MM-DD HH:mm:ss') }}
            </template>
          </ElTableColumn>
          <ElTableColumn label="用途" width="100">
            <template #default="{ row }">
              <ElTag size="small" effect="plain" :type="purposeTagTypeOf(row.input.purpose)">
                {{ purposeLabelOf(row.input.purpose) }}
              </ElTag>
            </template>
          </ElTableColumn>
          <ElTableColumn prop="input.orderCanonicalId" label="订单号" width="160" />
          <ElTableColumn prop="input.materialCode" label="物料" width="80" />
          <ElTableColumn prop="input.quantity" label="数量" width="80" />
          <ElTableColumn label="判定" width="100">
            <template #default="{ row }">
              <ElTag size="small" :type="row.result.meetsRequestedDueDate ? 'success' : 'danger'">
                {{ row.result.meetsRequestedDueDate ? '按期' : '延期' }}
              </ElTag>
            </template>
          </ElTableColumn>
          <ElTableColumn label="最早完成" width="140">
            <template #default="{ row }">
              {{ dayjs(row.result.earliestCompletion).format('YYYY-MM-DD') }}
            </template>
          </ElTableColumn>
          <ElTableColumn label="影响/阈值" width="100">
            <template #default="{ row }">
              {{ row.result.impactedOrderCount }} / {{ row.result.maxImpactedOrdersThreshold }}
            </template>
          </ElTableColumn>
          <ElTableColumn label="操作" width="80" align="center">
            <template #default="{ row }">
              <a href="javascript:;" @click="onLoadHistory(row.id)">载入</a>
            </template>
          </ElTableColumn>
        </ElTable>
      </ElCard>
    </div>

    <!-- ===== 空态 ===== -->
    <ElCard v-else class="empty-card">
      <ElEmpty description="填好上面的物料、数量、工厂和客户交期，点「评估」出结论">
        <template #image>
          <Icon style="font-size: 48px; color: #cbd5e1" icon="vi-mdi:target" />
        </template>
      </ElEmpty>
    </ElCard>
  </div>
</template>

<style lang="less" scoped>
.aps-ctp {
  display: flex;
  min-height: calc(100vh - 100px);
  padding: 16px;
  background: #f5f7fa;
  flex-direction: column;
  gap: 16px;
}

.panel {
  border-radius: 8px;
}

.panel-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  font-size: 14px;
  font-weight: 600;
  color: #1e293b;

  .muted-tag {
    font-size: 12px;
    font-weight: 400;
    color: #94a3b8;
  }
}

.panel-empty {
  padding: 20px 0;
}

.muted {
  color: #94a3b8;
}

/* 表单 */
.input-form {
  display: grid;
  grid-template-columns: repeat(2, 1fr);
  gap: 14px 20px;
}

.input-field {
  display: flex;
  align-items: center;
  gap: 12px;

  .field-label {
    width: 130px;
    font-size: 13px;
    color: #64748b;
    text-align: right;
    flex-shrink: 0;

    .required-mark {
      color: var(--el-color-danger);
      margin-left: 4px;
      font-weight: 600;
    }
  }

  .field-input {
    flex: 1;
  }
}

.input-actions {
  display: flex;
  padding-top: 14px;
  margin-top: 4px;
  border-top: 1px dashed #e2e8f0;
  grid-column: 1 / -1;
  align-items: center;
  justify-content: space-between;
  gap: 8px;

  .input-hint {
    font-size: 12px;
    color: #94a3b8;
  }

  .input-btns {
    display: flex;
    gap: 8px;
  }
}

.opt-empty {
  padding: 8px 12px;
  font-size: 12px;
  color: #94a3b8;
}

.eval-error {
  .err-detail {
    font-size: 11px;
    color: #94a3b8;
  }
}

/* 结果工具条 */
.result-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0 2px;
}

/* Verdict 判定 */
.verdict-row {
  display: grid;
  grid-template-columns: 1fr 1fr 1fr;
  gap: 14px;
}

.verdict-card {
  display: flex;
  padding: 16px 20px;
  background: #fff;
  border-left: 4px solid #94a3b8;
  border-radius: 8px;
  box-shadow: 0 1px 4px rgb(0 0 0 / 6%);
  align-items: center;
  gap: 14px;

  .verdict-icon {
    font-size: 36px;
  }

  .verdict-title {
    font-size: 16px;
    font-weight: 600;
    color: #1e293b;
  }

  .verdict-sub {
    margin-top: 4px;
    font-size: 11px;
    color: #94a3b8;
  }

  &.pass {
    border-left-color: #059669;

    .verdict-icon {
      color: #059669;
    }
  }

  &.fail {
    border-left-color: #ef4444;

    .verdict-icon {
      color: #ef4444;
    }
  }

  &.stat {
    flex-direction: column;
    align-items: flex-start;
    gap: 4px;

    .stat-label {
      font-size: 12px;
      color: #94a3b8;
    }

    .stat-val {
      font-size: 20px;
      font-weight: 600;
      color: #1e293b;
    }

    .stat-sub {
      font-size: 11px;
      color: #cbd5e1;
    }

    &.warn .stat-val {
      color: #ef4444;
    }

    &.ok .stat-val {
      color: #059669;
    }
  }
}

/* 受影响订单列 */
.content-row {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 16px;
}

/* 原因列表 */
.bottleneck-box {
  display: flex;
  padding: 12px 16px;
  margin-bottom: 16px;
  font-size: 14px;
  color: #1e293b;
  background: #fef3c7;
  border-left: 3px solid #f59e0b;
  border-radius: 4px;
  align-items: center;
  gap: 10px;

  .bn-icon {
    font-size: 22px;
    color: #f59e0b;
  }
}

.reason-title {
  margin: 16px 0 8px;
  font-size: 13px;
  font-weight: 600;
  color: #475569;
}

.reason-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.reason-item {
  display: flex;
  gap: 10px;
  padding: 10px 12px;
  background: #f8fafc;
  border-left: 3px solid #3b82f6;
  border-radius: 4px;

  &.est {
    background: #faf5ff;
    border-left-color: #8b5cf6;
  }

  .reason-rank {
    width: 22px;
    height: 22px;
    font-size: 12px;
    font-weight: 600;
    line-height: 22px;
    color: #fff;
    text-align: center;
    background: #3b82f6;
    border-radius: 50%;
    flex-shrink: 0;
  }

  .reason-body {
    flex: 1;
  }

  .reason-head {
    display: flex;
    align-items: center;
    gap: 6px;
    flex-wrap: wrap;
  }

  .reason-rel {
    font-family: 'Courier New', monospace;
    font-size: 12px;
    color: #64748b;
  }

  .reason-desc {
    margin-top: 6px;
    font-size: 13px;
    color: #475569;
  }
}

/* Protection 冲突 */
.conflict-list {
  display: flex;
  margin-top: 8px;
  flex-direction: column;
  gap: 6px;
}

.conflict-item {
  display: flex;
  padding: 8px 10px;
  font-size: 12px;
  color: #92400e;
  background: #fef3c7;
  border-radius: 4px;
  align-items: center;
  gap: 8px;
}

/* 表格列样式 */
.del {
  font-weight: 600;
  color: #ef4444;
}

/* 跨 Domain 链 */
.domain-chain {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  align-items: center;
}

.chain-item {
  flex: 1;
  min-width: 180px;
  padding: 10px 14px;
  background: #f0fdf4;
  border: 1px solid #bbf7d0;
  border-radius: 6px;

  &.fail {
    background: #fef2f2;
    border-color: #fecaca;
  }

  .chain-head {
    display: flex;
    align-items: center;
    gap: 8px;

    .chain-time {
      font-size: 12px;
      color: #64748b;
    }

    .chain-arrow {
      font-size: 18px;
      color: #94a3b8;
    }
  }

  .chain-bn {
    margin-top: 6px;
    font-size: 12px;
    color: #475569;
  }

  .chain-fail {
    margin-top: 4px;
    font-size: 11px;
    color: #b91c1c;
  }
}

/* ESTIMATED 顶部徽章 */
.est-alert {
  margin-bottom: 12px;
}

.over-alert {
  margin-bottom: 12px;
}

.option-error {
  margin-bottom: 12px;
}

/* 空态 */
.empty-card {
  background: transparent;
  border: none;
  border-radius: 8px;
  box-shadow: none;
}
</style>
