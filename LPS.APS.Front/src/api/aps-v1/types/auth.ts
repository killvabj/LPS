/**
 * 认证 DTO（v1.2 后端对齐版）
 *
 * 来源：lps/LPS.APS.Web/Dto/Auth/
 *  - LoginRequestDto.cs           (UserCode / Password)
 *  - LoginResponseDto.cs          (AccessToken / RefreshToken / ExpiresAt / UserId / UserCode / UserName / Roles)
 *  - RefreshTokenRequestDto.cs    (AccessToken / RefreshToken)
 *  - UserInfoDto.cs               (UserId / UserCode / UserName / Roles / Permissions / IsGlobal / Factories / ProductFamilies / Departments / Domains / ResourceOrgGroups)
 *
 * 字段命名按 0号位《Auth库DDL缺陷与真源裁决》R1=A′ 收口：
 *  - 后端 PascalCase → 前端 camelCase（API 层做映射）
 *  - 业务范围扁平 5 维度 + IsGlobal（Global 时为全放行）
 *  - PermissionCode 不在 TS 侧枚举（后端 34 码随 v1.2 冻结，前端只读不强制）
 *
 * Phase A 范围：仅 DTO 类型对齐。消费方（store/permission.ts/views/Login）暂未切换，
 *   旧字段（username / displayName / dataScope.* 嵌套 / expiresIn）保留 @deprecated 别名，
 *   后续 Phase B（权限码迁移）+ Phase C（scope 扁平化）再统一替换。
 *
 * 2026-11-18：补「改自己资料 / 改密码」两组 DTO
 *   （后端 `UpdateProfileRequest` / `UpdateProfileResponse` / `ChangePasswordRequest`）
 *   —— 依据 3号位《个人中心改资料与改密码端点落码_回执 v1.0_20261117》。
 *
 * 2026-11-18（第二封）：`UserInfo`（= `GET /api/auth/me` 回包）补 `email` / `phoneNumber`
 *   —— 依据 3号位《个人中心me补邮箱手机号与显示名读库_回执 v1.0_20261118》R1。
 *   · 后端 `UserInfoDto` 增 `Email` / `PhoneNumber` 两个**可空**字段（库里空给 `null`）；
 *   · 同一笔查询里**显示名也改读 DB**（`[User].DisplayName`）⇒ 消掉「改完资料姓名仍旧」的
 *     claim 延迟（`userName` 字段名不变，只是取值来源从 JWT claim 换成 DB 真值）。
 */

import type { DomainKey, RoleKey } from './common'

// ==================== 登录 ====================

/** 登录请求 — 对齐后端 LoginRequestDto */
export interface LoginRequest {
  /** 用户工号（后端字段 UserCode；前端旧名 username 已废弃） */
  userCode: string
  password: string
}

/** 登录响应 — 对齐后端 LoginResponseDto
 *  注：后端将 userId/userCode/userName/roles 放在顶层，不嵌套 userInfo。 */
export interface LoginResponse {
  accessToken: string
  refreshToken: string
  /** AccessToken 过期时间（绝对时间 ISO8601，对齐后端 ExpiresAt） */
  expiresAt: string
  userId: number
  /** 用户工号 */
  userCode: string
  /** 用户姓名 */
  userName: string
  /** 角色列表（后端为 string[]，前端强类型为 RoleKey[]） */
  roles: RoleKey[]
}

// ==================== 刷新 Token ====================

/** 刷新 token 请求 — 对齐后端 RefreshTokenRequestDto
 *  后端要求同时携带过期的 accessToken + 有效的 refreshToken。 */
export interface RefreshTokenRequest {
  accessToken: string
  refreshToken: string
}

// ==================== 当前用户信息 ====================

/** 用户信息 — 对齐后端 UserInfoDto */
export interface UserInfo {
  userId: number
  /** 用户工号（对齐后端 UserCode；旧名 username 已废弃） */
  userCode: string
  /**
   * 用户姓名（对齐后端 UserName；旧名 displayName 已废弃）
   *
   * ⚠️ **2026-11-18 起取值来源是 DB**（3号位 R1：`[User].DisplayName`），
   *   不再是 JWT claim ⇒ 改完资料**立即生效**，不必等 token 重签。
   */
  userName: string
  /**
   * 用户邮箱（后端 `UserInfoDto.Email`；3号位 2026-11-18 R1 已补）
   *
   * ⚠️ **缺键 / null 语义**：库里没填 ⇒ `null` ⇒ 页面显「未提供」，
   *   **禁**推断成「邮箱已清空」或「账号没邮箱」。
   */
  email?: string | null
  /**
   * 用户手机号（后端 `UserInfoDto.PhoneNumber`；3号位 2026-11-18 R1 已补）
   *
   * ⚠️ 缺键 / null 语义同 `email`。
   */
  phoneNumber?: string | null
  /** 角色列表（后端为 string[]，前端强类型为 RoleKey[]） */
  roles: RoleKey[]
  /**
   * 功能权限码列表（v1.2 §23.1：`aps.<module>.<action>` 层次码，统一 `aps.` 前缀）。
   * 后端签发 34 码（见 lps/LPS.APS.Core/Authorization/PermissionCodes.cs），前端不强制枚举以兼容后续扩展。
   * 前端比对字符串相等即可；`verifyPermission()` 提供归一化兜底（见 store/modules/aps/auth.ts）。
   */
  permissions: string[]
  /** 业务范围：是否全局放行（Global=true 时各维度集合为空） */
  isGlobal: boolean
  /** 业务范围：工厂范围值集合 */
  factories: string[]
  /** 业务范围：产品族范围值集合 */
  productFamilies: string[]
  /** 业务范围：部门范围值集合 */
  departments: string[]
  /** 业务范围：域范围值集合（DomainKey，例：FAMILY_INJECTION / BJ_FAMILY_INJECTION） */
  domains: DomainKey[]
  /** 业务范围：资源组织组范围值集合（兼容字段，V1 多为空） */
  resourceOrgGroups: string[]
}

// ==================== 改自己资料 / 改密码（2026-11-18） ====================

/**
 * 改自己资料请求 — 对齐后端 `LPS.APS.Web/Dto/Auth/UpdateProfileRequest.cs`
 *
 * ⚠️ 后端语义（`AuthService.UpdateProfileAsync`）：
 *  - `userName`：**空 / 空白 ⇒ 保持原值**（不是清空！）；trim 后长度 ≤ 50，超了报 400
 *    「姓名长度不能超过 50 位」；
 *  - `email` / `phoneNumber`：**空白 ⇒ 清空（存 null）**，不做格式校验；
 *  - 登录名 / 状态 / 角色 / 范围**不可自助改**（归 auth.manage 管理面）。
 *
 * 依据 3号位 2026-11-17《个人中心改资料与改密码端点落码_回执 v1.0_20261117》。
 */
export interface UpdateProfileRequest {
  /** 显示名；空 = 保持原值 */
  userName?: string | null
  /** 邮箱；空 = 清空 */
  email?: string | null
  /** 手机号；空 = 清空 */
  phoneNumber?: string | null
}

/** 改自己资料响应 — 对齐后端 `UpdateProfileResponse`（回显改后的三项） */
export interface UpdateProfileResult {
  userName: string
  email?: string | null
  phoneNumber?: string | null
}

/**
 * 修改密码请求 — 对齐后端 `ChangePasswordRequest`
 *
 * ⚠️ 后端规则（`AuthService.ChangePasswordAsync`）：
 *  - 旧密码必须正确（错 ⇒ 400「旧密码不正确」）；
 *  - 新密码 8~128 位（错 ⇒ 400「新密码长度须为 8~128 位」）；
 *  - 新密码 ≠ 旧密码、≠ 登录名；
 *  - 成功后**清 RefreshToken**（旧会话失效）⇒ 前端必须清本地 token 并跳登录页。
 */
export interface ChangePasswordRequest {
  oldPassword: string
  newPassword: string
}

// ==================== 兼容层（Phase B/C 期间临时保留） ====================

/**
 * @deprecated 自 v1.2 后端对齐后使用 userCode；旧 mock 数据可能仍含 username，Phase B 替换消费方后删除。
 */
export interface UserInfoLegacy {
  username: string
  displayName: string
  email?: string
  phone?: string
  avatar?: string
  dataScope: {
    factoryCodes: string[]
    productFamilyCodes: string[]
    departmentCodes?: string[]
    domainKeys?: DomainKey[]
  }
  factoryId?: number
  factoryCode?: string
  departmentId?: number
}
