/**
 * APS V1 4号位 — 认证 API
 *
 * @owner 3号位（认证 / 当前用户 / 角色 / 权限 / 业务范围）
 * @see 审核报告 §十六.4↔3 + §十七.17 + §二十（规则页面需真实 3号位认证上下文）
 *
 * 走真实后端（lps/LPS.APS.Web/Controllers/AuthController.cs）：
 *  - POST /api/auth/login    → LoginResponse（含顶层 userId/userCode/userName/roles）
 *  - POST /api/auth/refresh  → LoginResponse
 *  - GET  /api/auth/me       → UserInfo
 *  - POST /api/auth/logout   → void
 *
 * APS_USE_MOCK=true 时仍走 mock（本地离线验证用）；false 时走真实 http.ts。
 */

import { apsHttp, APS_USE_MOCK } from './http'
import type { LoginRequest, LoginResponse, RefreshTokenRequest, UserInfo } from './types'

/** mock 登录响应（与后端 LoginResponseDto 字段对齐）
 *  - 顶层 userId/userCode/userName/roles（不再嵌 userInfo）
 *  - expiresAt 给绝对时间（mock 阶段统一 1 小时后过期）
 *  - v1.2 DDL：roles 用后端系统角色码 aps.planner + aps.viewer.management
 */
const mockLoginResponse = (req: LoginRequest): LoginResponse => ({
  accessToken: 'mock-access-token',
  refreshToken: 'mock-refresh-token',
  expiresAt: new Date(Date.now() + 60 * 60 * 1000).toISOString(),
  userId: 1,
  userCode: req.userCode,
  userName: req.userCode,
  roles: ['aps.planner', 'aps.viewer.management']
})

export const apsAuthApi = {
  /**
   * APS 登录
   *
   * `silent: true` —— 登录失败由登录页自己提示，避免两条链路各弹一次：
   *  - 旧形态 `200 + 信封 401` 走 `unwrap` 抛错，**根本不会弹 toast**（登录失败曾经一声不吭）；
   *  - 5号位 2026-10-19 全局收口后 `401 + 信封 401` 会走拦截器错误分支，那里会弹 toast。
   * ⇒ 标 silent 后两种形态都只在登录页提示一次，行为与部署无关。
   */
  async login(req: LoginRequest): Promise<LoginResponse> {
    if (APS_USE_MOCK) return mockLoginResponse(req)
    return apsHttp.post<LoginResponse>({
      url: '/api/auth/login',
      data: req,
      silent: true
    })
  },

  /** 刷新 token */
  async refresh(req: RefreshTokenRequest): Promise<LoginResponse> {
    if (APS_USE_MOCK) return mockLoginResponse({ userCode: 'mock', password: '' })
    return apsHttp.post<LoginResponse>({
      url: '/api/auth/refresh',
      data: req
    })
  },

  /** 获取当前用户信息 */
  async getUserInfo(): Promise<UserInfo> {
    if (APS_USE_MOCK) {
      // mock 阶段直接返回 demo（store 会用 MOCK_ROLE_PRESETS 覆盖）
      return {
        userId: 1,
        userCode: 'pmc',
        userName: 'PMC 演示账号',
        roles: ['aps.planner'],
        permissions: [],
        isGlobal: true,
        factories: [],
        productFamilies: [],
        departments: [],
        domains: [],
        resourceOrgGroups: []
      }
    }
    return apsHttp.get<UserInfo>({ url: '/api/auth/me' })
  },

  /** 登出 */
  async logout(): Promise<void> {
    if (APS_USE_MOCK) return
    return apsHttp.post<void>({ url: '/api/auth/logout', data: {} })
  }
}
