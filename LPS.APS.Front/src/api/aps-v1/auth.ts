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
 *
 * 2026-11-18：新增「改自己资料 / 改密码」两个端点
 *   （PUT /api/auth/profile、POST /api/auth/change-password）
 *   —— 依据 3号位《个人中心改资料与改密码端点落码_回执 v1.0_20261117》。
 *
 * 2026-11-18（第二封）：`GET /api/auth/me` 回包补 `email` / `phoneNumber`
 *   —— 依据 3号位《个人中心me补邮箱手机号与显示名读库_回执 v1.0_20261118》R1。
 *   · 字段形状在 `types/auth.ts` 的 `UserInfo`（本文件不另立类型）；
 *   · mock 形状同步补两个 `null`（mock 不编造邮箱/手机号）。
 */

import { apsHttp, APS_USE_MOCK } from './http'
import type {
  ChangePasswordRequest,
  LoginRequest,
  LoginResponse,
  RefreshTokenRequest,
  UpdateProfileRequest,
  UpdateProfileResult,
  UserInfo
} from './types'

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
        // 邮箱/手机号：mock 不编造 ⇒ 显式 null（页面据此显「未提供」）
        email: null,
        phoneNumber: null,
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
  },

  /**
   * 改自己资料（PUT /api/auth/profile）
   *
   * ⚠️ 只能改三项：显示名 / 邮箱 / 手机号（登录名、状态、角色、范围归管理面，不可自助改）。
   * ⚠️ 后端语义：`userName` 空 ⇒ **保持原值**（不是清空）；email / phoneNumber 空 ⇒ **清空**。
   * ⚠️ `silent + noErrorRedirect`：错误文案由页面自己呈现（后端已是大白话），
   *   且**不跳 /500** —— 用户填好的表单不能因为一次校验失败就没了。
   */
  async updateProfile(req: UpdateProfileRequest): Promise<UpdateProfileResult> {
    if (APS_USE_MOCK) {
      return {
        userName: req.userName ?? '',
        email: req.email ?? null,
        phoneNumber: req.phoneNumber ?? null
      }
    }
    return apsHttp.put<UpdateProfileResult>({
      url: '/api/auth/profile',
      data: req,
      silent: true,
      noErrorRedirect: true
    })
  },

  /**
   * 修改密码（POST /api/auth/change-password）
   *
   * ⚠️ 成功后后端**清 RefreshToken**（旧会话失效）⇒ 调用方必须清本地 token 并跳登录页
   *   （3号位 回执 v1.0 Q1）。
   */
  async changePassword(req: ChangePasswordRequest): Promise<void> {
    if (APS_USE_MOCK) return
    await apsHttp.post<void>({
      url: '/api/auth/change-password',
      data: req,
      silent: true,
      noErrorRedirect: true
    })
  }
}
