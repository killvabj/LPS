/**
 * APS V1 4号位 — PI 位置 API（端点归 5号位）
 *
 * 2026-10-17 重做：本文件原先内嵌 8 条手写 PI（`PI-2026-0001`…`0008`）冒充列表数据，
 * 非 mock 分支却去请求 `/api/pi-position/summary` 并当成 `PiListItem[]` 用 —— 三个端点一个都没调对。
 * 现按 5号位 2026-10-17《供给来源追溯页契约缺口与PositionType码表》回执 + dev 实测逐条对齐。
 *
 * 三个端点，`planVersionId` 均必填：
 *  - `GET /api/pi-position`            列表（裸数组，无 total；take 上限 500）
 *  - `GET /api/pi-position/summary`    聚合（items 恒空，只用 piCount / totalPositions / positionTypeCounts）
 *  - `GET /api/pi-position/{piNo}`     单 PI 全部位置行（无匹配返 404）
 *
 * 全部 `silent: true`：错误文案由页面自己改写成大白话，不把后端 message 直抛给用户。
 */

import { apsHttp } from './http'
import type { PiPositionDto, PiPositionQuery, PiPositionSummaryDto } from './types'

export const piApi = {
  /** 聚合 KPI（PI 数 / 位置条数 / 按位置类型的条数与数量合计） */
  async summary(planVersionId: number): Promise<PiPositionSummaryDto> {
    return apsHttp.get<PiPositionSummaryDto>({
      url: '/api/pi-position/summary',
      params: { planVersionId },
      silent: true
    })
  },

  /** 位置明细（一个版本全量可达 20 万+ 条，必须服务端分页） */
  async list(query: PiPositionQuery): Promise<PiPositionDto[]> {
    return apsHttp.get<PiPositionDto[]>({
      url: '/api/pi-position',
      params: { ...query },
      silent: true
    })
  },

  /** 单个 PI 的全部位置行（跨工序会返回多行） */
  async getByPi(productionInstructionNo: string, planVersionId: number): Promise<PiPositionDto[]> {
    return apsHttp.get<PiPositionDto[]>({
      url: `/api/pi-position/${encodeURIComponent(productionInstructionNo)}`,
      params: { planVersionId },
      silent: true
    })
  }
}
