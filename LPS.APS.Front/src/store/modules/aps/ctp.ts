/**
 * APS V1 4号位 — CTP / 插单评估 Pinia store（页面 4）
 *
 * 状态：
 *  - input：评估输入（orderCanonicalId / materialCode / quantity / factoryCode / requestedDueDate / purpose）
 *  - result：评估结果
 *  - history：本会话内的历史评估（最多保留 5 条，方便切换对比）
 *  - loading / error：UI 态
 *
 * 注意：
 *  - 2026-09-30：前端撤事业部下拉，domainKey 不再由前端传入；后端按 materialCode+factoryCode 自动 resolve（契约登记 v1.0 §3.2）
 *  - 4号位文档第 7 节 / U11：CTP 不暴露"采用"按钮，只展示结果
 *  - 写入通过 Controller（ctpApi.evaluate）发起，不直接改库
 */

import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { ctpApi, ApiError, type CtpInput, type CtpResultDto } from '@/api/aps-v1'

const defaultInput = (): CtpInput => ({
  orderCanonicalId: '',
  materialCode: '',
  quantity: 100,
  factoryCode: 'F-SUZ-01',
  requestedDueDate: new Date(Date.now() + 10 * 86400_000).toISOString().slice(0, 10),
  purpose: 'CTP'
})

export interface CtpHistoryEntry {
  id: number
  input: CtpInput
  result: CtpResultDto
  evaluatedAt: string
}

let historySeq = 1

export const useCtpStore = defineStore('aps.ctp', () => {
  // ===== state =====
  const input = ref<CtpInput>(defaultInput())
  const result = ref<CtpResultDto | null>(null)
  const history = ref<CtpHistoryEntry[]>([])
  const loading = ref(false)
  /** 错误正文（后端 message 原样；页面再翻成大白话） */
  const error = ref<string | null>(null)
  /** 错误业务码（501 = 试算能力未接入；页面据此给固定话术，不把内部编号甩给用户） */
  const errorCode = ref<number | null>(null)
  /** traceId（排障用，页面只放在次要位置） */
  const errorTraceId = ref<string | null>(null)
  /** 本次结果的真实评估时刻（不是渲染时刻；载入历史时也用它） */
  const evaluatedAt = ref<string | null>(null)

  // ===== getters =====
  const hasResult = computed(() => result.value !== null)
  const isOverThreshold = computed(
    () =>
      result.value !== null &&
      result.value.impactedOrderCount > result.value.maxImpactedOrdersThreshold
  )

  // ===== actions =====
  async function evaluate(next?: Partial<CtpInput>): Promise<void> {
    if (next) input.value = { ...input.value, ...next }
    loading.value = true
    error.value = null
    errorCode.value = null
    errorTraceId.value = null
    try {
      const dto = await ctpApi.evaluate(input.value)
      result.value = dto
      evaluatedAt.value = new Date().toISOString()
      history.value.unshift({
        id: historySeq++,
        input: { ...input.value },
        result: dto,
        evaluatedAt: evaluatedAt.value
      })
      if (history.value.length > 5) history.value = history.value.slice(0, 5)
    } catch (err) {
      // 失败时清掉旧结论：否则页面会同时挂着「本次输入的报错」和「上一轮的结论」，
      // 用户会把旧结论当成这次的答案。旧结果在历史里，载回即可。
      result.value = null
      evaluatedAt.value = null
      handleError(err)
    } finally {
      loading.value = false
    }
  }

  function loadFromHistory(entry: CtpHistoryEntry): void {
    input.value = { ...entry.input }
    result.value = entry.result
    evaluatedAt.value = entry.evaluatedAt
  }

  function reset(): void {
    input.value = defaultInput()
    result.value = null
    error.value = null
    errorCode.value = null
    errorTraceId.value = null
    evaluatedAt.value = null
  }

  function clearHistory(): void {
    history.value = []
  }

  function handleError(err: unknown): void {
    if (err instanceof ApiError) {
      error.value = err.message
      errorCode.value = err.code
      errorTraceId.value = err.traceId ?? null
    } else {
      error.value = (err as Error)?.message ?? '未知错误'
      errorCode.value = null
      errorTraceId.value = null
    }
  }

  return {
    input,
    result,
    history,
    loading,
    error,
    errorCode,
    errorTraceId,
    evaluatedAt,
    hasResult,
    isOverThreshold,
    evaluate,
    loadFromHistory,
    reset,
    clearHistory,
    handleError
  }
})
