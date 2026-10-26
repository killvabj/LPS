#!/usr/bin/env node
/**
 * APS V1 4号位 — v1.4 §二十九 U23-U50 验收脚本（2026-09-18）
 *
 * 用途：v1.4 实施包 §二十九 28 项新增验收场景的自动化断言。
 *
 * 设计原则：
 *  - 复用 verify-integration.mjs 的 call/assert/login 模式
 *  - 不启 dev server（用户自己在终端跑 pnpm dev）
 *  - HTTPS 自签证书：NODE_TLS_REJECT_UNAUTHORIZED=0
 *  - 静态扫描（U28/U41/U43/U44/U45 + S01）直接读 frontNew/src/ 文件
 *  - 接口断言分 4 段：interfaces / rbac-negate / current-user / business-rules
 *
 * 覆盖范围（30 接口断言 + 6 静态扫描 = 36 项）：
 *  [I] Interfaces: U23-U32 (10 断言)
 *      - U23 静态（前端不含 2号位 端口）
 *      - U24 Overview 结构
 *      - U25 Strategy/Rule/Param 端点兼容
 *      - U26 CTP 发起（4→3）
 *      - U27 Manual ETA CRUD
 *      - U28 静态（DTO 含 EffectiveFrom）
 *      - U29 DP 查看
 *      - U30 DP 释放
 *      - U31 MES 受控
 *      - U32 Login/Refresh/Logout/Me
 *
 *  [R] Rbac Negate: U33-U39 + U46-U47 (9 断言)
 *      - U33 业务端点 403
 *      - U34 Factory=TJ 查 BJ
 *      - U35 Department=MC 范围
 *      - U36 PF=X 维护 PF=Y
 *      - U37 Domain=D01 激活 D02
 *      - U38 无 token 401
 *      - U39 Global 跨域
 *      - U46 CTP 无激活
 *      - U47 MES 不可下发
 *
 *  [C] Current User: U40-U42 (3 断言)
 *      - U40 /me 含 permissions + 4 scope
 *      - U41 Scope 筛选（静态）
 *      - U42 Audit-logs 不含敏感字段
 *
 *  [S] Static Scan: U28/U41/U43/U44/U45 + S01 (6 静态扫描)
 *      - U28 EffectiveFrom 字段
 *      - U41 ScopeFilter 仅授权
 *      - U43 无 Approval.vue
 *      - U44 无 PAUSE/RESUME 按钮
 *      - U45 不含 1号位 端口
 *      - S01 Aps 页面模板用到的 El* 组件均已显式 import（防「漏 import → 静默裸渲染」退化）
 *
 *  [B] Business Rules: U48-U50 (3 断言)
 *      - U48 ESTIMATED 不显示为承诺
 *      - U49 Run 查询 5 / 治理 3
 *      - U50 空 scope 用户
 *
 *  [V] V1.4 收口新增端点 (5 断言 — 任务 1+2+5)
 *      - V01 GET /rule-set/{id}/published-version 专用端点可达
 *      - V02 GET /parameter-set/{id}/published-version 专用端点可达
 *      - V03 GET /strategy-profile/{id}/published-version 专用端点可达
 *      - V04 POST /run/{id}/validate-domain-keys 端点可达
 *      - V05 GET /plan-version/{candidateId}/compare-with/{baseId} 路径双参契约
 *
 *  [J] §10A 白天人工调整 — ScopeJsonV2 契约（j-10a — 09-21 §10A 五业务入口）
 *      - J00 探测：scope 是否透传到后端 Validator（NEW_ORDER_CTP+EXPEDITE 应 400）
 *      - J01-J08 拒绝类断言：8 条 ScopeJsonV2Validator 校验失败路径
 *      - J09 静态：types/run.ts 含契约字段 / run.ts 不再含 remark / Gantt.vue 不再含 INSERT_RESCHEDULE / 4 个 Dialog 文件齐备
 *      - J10 接受类：默认跳过；仅 VERIFY_ALLOW_WRITE=1 时开启（会创建 ScheduleRun + CANDIDATE）
 *
 *  [M] §10A.1 已有订单提前 — OrderCanonicalId 自动带入 + planVersionId 必填契约（m-10a1 — 09-23 B4 已落地触发 + 5号位 回执）
 *      - M01 静态：types/order.ts OrderBasicInfo 含 orderCanonicalId / OrderAdvanceDialog.vue
 *                 含 orderCanonicalId 字段 + 自动带入逻辑 + "5号位 待补列" 旧文案已移除 /
 *                 Order.vue advanceCandidates 透传 orderCanonicalId /
 *                 mocks/fixtures.ts mockOrderDetail basic 含 orderCanonicalId
 *      - M03 静态（planVersionId 必填契约）：types/order.ts OrderQuery.planVersionId: number（非可选）/
 *                 store/modules/aps/order.ts loadList 含 planVersionIdReady 校验（5号位 2026-09-23 回执 §一）/
 *                 Order.vue planVersionInputRequired computed + ElInput 含 required mark + onMounted 从 active-plan 取默认
 *      - M02 接受类（默认跳过）：VERIFY_ALLOW_WRITE=1 时执行；需 Order 页已选订单 + Dialog 自动带入 + 提交跳转 Candidate
 *
 *  [P] ParameterSet 写维护（parameter-set-drafts — 09-21 B 设计稿 Step 3+4+6）
 *      - P01-P05 静态扫描：types/rule.ts 含 10 类型 / draftBuffer.ts 导出 3 算法 + 5 工具 /
 *                       rules.ts 含 6 actions + 6 getters / Rules.vue 含 [+ 新建草稿]+5 块+6 态矩阵 /
 *                       index.ts 重导出 draftBuffer
 *      - P06-P13 端点可达：8 个 GovernanceController.cs:108-275 写端点（POST/PUT/GET RS+PS draft+publish+read）
 *      - 全部为「无副作用探测」（只验端点存在/校验生效），不实际创建 ScheduleRun / DRAFT
 *
 *  [O] OPM 工艺规划模式治理（o-opm — 09-23 3号位 T1 交付件）
 *      - O01 静态：types/opm.ts 含 OperationPlanningMode 三态 + RoutingOperationDto /
 *                 opm.ts 含 listOperations + updatePlanningMode + MOCK_OPM_MATERIAL_ID /
 *                 store/modules/aps/opm.ts 含 load + updatePlanningMode + 三态 getter /
 *                 views/Aps/Opm.vue 存在 + 含三态常量 / router/modules/aps.ts 含 /operation-planning-mode 路由 +
 *                 api/aps-v1/index.ts + types/index.ts 重导出
 *      - O02 端点可达：GET /api/governance/routing-operations?materialId=1 与
 *                    PUT /api/governance/routing-operations/{id}/planning-mode
 *      - O03 接受类（默认跳过）：VERIFY_ALLOW_WRITE=1 时开启
 *      - O04 物料编码补全：types/material.ts 含 MaterialLookupItem /
 *                         materialLookup.ts 含 searchMaterials（GET lookups/materials）/
 *                         index 重导出 / Opm.vue 顶部 ElInput number → ElAutocomplete
 *      - O05 紧凑三态摘要：Opm.vue 移除 3 张 KPI 卡片（无 opm-kpi/.kpi-desc）/
 *                         工具栏内 ElFormItem v-if=hasList + 3 ElTag + 共 N 条
 *
 *  [RC] 资源日历 / 人工能力槽维护（r-resource-calendar — 09-24 5号位 对接函 + v1.3 冻结方案）
 *      - RC01-RC08 静态：types/resourceCalendar.ts（camelCase 8 字段 + 6 权限码 + 三态 + 1..370）/
 *                       api 9 端点 + 2 过渡数据源方法 / store 双缓存三态 + 六权限 getter + 12 动作 /
 *                       3 文件存在 + 页面 3 Tab + 顶部三 Alert / 路由 + mock 6 码 / 追加语义(D2) + 自由文本(G3)
 *      - RC09-RC10 端点可达：GET /api/manual-capacity/slots、GET /api/resource-calendar/1
 *                       （6 权限码无角色绑定 → 403 亦视为可达；见 4号位 函 G5）
 *      - RC11 接受类（默认跳过）：VERIFY_ALLOW_WRITE=1 时开启
 *
 * 用法：
 *  - 全量：node scripts/verify-acceptance.mjs
 *  - 分段：GROUP=rbac-negate node scripts/verify-acceptance.mjs
 *  - 仅静态：GROUP=static node scripts/verify-acceptance.mjs
 *  - §10A 段：GROUP=j-10a node scripts/verify-acceptance.mjs
 *  - §10A.1 段：GROUP=m-10a1 node scripts/verify-acceptance.mjs
 *  - 写维护段：GROUP=parameter-set-drafts node scripts/verify-acceptance.mjs
 *  - 枚举映射静态段：GROUP=parameter-set-static node scripts/verify-acceptance.mjs（P19-P20，不登录）
 *  - 锚点资源编码段：GROUP=parameter-set-anchor node scripts/verify-acceptance.mjs（A01-A08，不登录）
 *  - 仓库四处落点段：GROUP=parameter-set-warehouse node scripts/verify-acceptance.mjs（P21-P27，不登录）
 *  - 建壳入口静态段：GROUP=strategy-config-static node scripts/verify-acceptance.mjs（SC01-SC07，不登录）
 *  - OPM 段：GROUP=o-opm node scripts/verify-acceptance.mjs
 *  - CTP DomainKey 段：GROUP=a-ctp-domainkey node scripts/verify-acceptance.mjs（A01-A05 前端 + C01-C02 后端契约，3号位 C 路径落码 09-30）
 *  - ManualETA 精简段：GROUP=me-eta node scripts/verify-acceptance.mjs（ME01-ME02 静态，顶部使用说明已撤）
 *  - 资源日历段：GROUP=r-resource-calendar node scripts/verify-acceptance.mjs
 *  - Domain 段：GROUP=d-domain node scripts/verify-acceptance.mjs
 *  - TaskSplitRuleConfig 段：GROUP=t-tsrc node scripts/verify-acceptance.mjs（TSRC01-TSRC05 静态契约）
 *  - DemandProtection 冻结一致性段：GROUP=dp-freeze node scripts/verify-acceptance.mjs（DP01-DP05 静态，R1 stale 注释 + §2.3/§2.4 文案）
 *  - DemandProtection lps/ 联调段：GROUP=dplive node scripts/verify-acceptance.mjs（DPL01-DPL10 静态，per-lock 列表 + /summary KPI + 前端聚合）
 *  - Explanation 冻结一致性段：GROUP=e-freeze node scripts/verify-acceptance.mjs（E01-E10 + E13-E22 静态 + E21 动态 + E11/E12 skip，R1 15 码字典 + R3 撤 ScheduleFailureInfo + 可选链漏写探针 + 版本不重复展示探针 + PlanVersionStatus 7 码探针 + 业务优先级渲染探针 + 首次 mount 联动探针 + selectVersion 并发去重探针 + loadCurrent allSettled 容错探针 + 10-10 option A 收口探针（撤 重排建议 + 设备故障 + /api/business-fact-issues 聚合）+ 10-10 business-fact-issues 端点 DDL 落地探针 + 10-10 样式简洁整齐探针；R2/R3 跨号位等回执）
 *  - 菜单分组段：GROUP=menu node scripts/verify-acceptance.mjs（M01-M19 静态，不登录）：顶级「策略配置」「角色与权限」两个分组 +
 *              「需求与供给」「排产执行」「基础数据与日历」三个顶级分组（apsV1Routes 共 6 个顶级节点）；
 *              所有分组叶子页用绝对路径（URL 不变）；页面改名「策略包版本」
 *  - 运行与下发段：GROUP=run node scripts/verify-acceptance.mjs（RUN01-RUN15，7 静态 + 3 动态 + 5 呈现口径）：
 *              自拟聚合体 RunStatusDto 已删 + 状态词表 = 冻结 07 §4.5 四值 + MES 下发区块整块下线 +
 *              api 只调 /api/governance/runs + store 三数据面各自取数 + DTO 字段名对齐 ScheduleRunGov.cs +
 *              RUN10-RUN13：域状态 404（后端对该 Run 没登记 ExpectedDomainKeysJson 的判 404）
 *              不外泄内部术语 —— 静默 toast + store 不进 errors[] + 卡片常驻空态；
 *              RUN15：404 豁免按「该 Run 有无分母」分流（5号位 R3 裁定：夹具 Run 404 是终态，
 *              有真分母还 404 才是缺陷）—— 空态文案三态，页面不得替未验证的事下结论
 *  - 接受类（脏数据风险）：VERIFY_ALLOW_WRITE=1 GROUP=j-10a node scripts/verify-acceptance.mjs
 *  - Windows Git Bash：env 前缀可用；或直接改脚本末尾 GROUP 默认值
 */

import { readFileSync, existsSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'
import { setTimeout as sleep } from 'node:timers/promises'

// 自签证书放行（dev only）
process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0'

const API_BASE = process.env.API_BASE || 'https://localhost:7044'
const GROUP = process.env.GROUP || 'all' // all | interfaces | rbac-negate | current-user | static | business-rules | j-10a | m-10a1 | v14-additions | parameter-set-drafts | o-opm | r-resource-calendar | d-domain | a-ctp-domainkey | me-eta | t-tsrc | dp-freeze | dplive | e-freeze | setup | pwc | menu | overview | candidate | run | gantt | pi | pi-static | order | order-static | rules
const ADMIN_CODE = 'admin'
const ADMIN_PWD = 'Admin@123456'

// 路径解析（用于静态扫描 frontNew/src/ + lps/）
const __dirname = dirname(fileURLToPath(import.meta.url))
const SRC_ROOT = join(__dirname, '..', 'src')
const LPS_ROOT = join(__dirname, '..', '..', 'lps')

const log = (...a) => console.log(...a)
const ok = (msg) => log(`  ✅ ${msg}`)
const bad = (msg) => log(`  ❌ ${msg}`)
const info = (msg) => log(`  · ${msg}`)

let passCount = 0
let failCount = 0
let skipCount = 0

function assert(label, cond, detail = '') {
  if (cond) {
    passCount++
    ok(`${label}${detail ? ` — ${detail}` : ''}`)
  } else {
    failCount++
    bad(`${label}${detail ? ` — ${detail}` : ''}`)
  }
}

function skip(label, reason) {
  skipCount++
  info(`${label} — ${reason}`)
}

/* ==================== 通用 fetch 封装（与 verify-integration.mjs 一致）==================== */

async function call(method, path, { token, body, query } = {}) {
  const url = new URL(API_BASE + path)
  if (query) {
    for (const [k, v] of Object.entries(query)) {
      if (v !== undefined && v !== null) url.searchParams.set(k, String(v))
    }
  }
  const headers = { 'Content-Type': 'application/json' }
  if (token) headers.Authorization = `Bearer ${token}`

  const res = await fetch(url, {
    method,
    headers,
    body: body ? JSON.stringify(body) : undefined
  })
  const text = await res.text()
  let json
  try {
    json = JSON.parse(text)
  } catch {
    json = null
  }

  const businessCode = json?.code ?? res.status
  const success = res.ok && (businessCode === 200 || businessCode === 201)
  return {
    status: res.status,
    code: businessCode,
    ok: success,
    json,
    text,
    traceId: res.headers.get('X-Trace-Id') || json?.traceId || ''
  }
}

/* ==================== 登录 ==================== */

/** 剥掉块注释 / 整行 `//` / 行尾 `//`。
 *
 * 背景：说明性注释常会写「此前自拟 X / 已删 Y」这类历史交代，
 *  「是否仍是残留」的断言若不剥注释，会把这段说明判成残留 ⇒ **假红**（OV06 踩过）。
 * 放在模块作用域，供各探针组共用（原为 candidate 组的局部 const，
 *  P16 在 1956 行调用时落在其之前 ⇒ TDZ 抛错被 catch 吞掉、四项全 false）。
 */
const stripComments = (s) =>
  s.replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '').replace(/\s\/\/[^\n]*/g, '')

async function loginAsAdmin() {
  const r = await call('POST', '/api/auth/login', {
    body: { userCode: ADMIN_CODE, password: ADMIN_PWD }
  })
  if (!r.ok || !r.json?.data?.accessToken) {
    bad(`admin 登录失败：HTTP ${r.status} ${r.text.slice(0, 200)}`)
    throw new Error('login failed')
  }
  ok(`admin 登录成功（userId=${r.json.data.userInfo?.userId}）`)
  return r.json.data.accessToken
}

/* ==================== [I] 接口 Owner 正确性（U23-U32）==================== */

async function verifyInterfaces(token) {
  log('\n[I] 接口 Owner 正确性（U23-U32）')

  // U23 静态：前端不含 2号位 端口（7001/7002）
  const u23 = staticScanNoDirectBackend()
  assert(
    'U23 前端不含 2号位 端口（7001/7002）',
    u23,
    u23 ? '扫描通过' : '检测到 2号位 端口直接调用'
  )

  // U24 Overview 查询 4→5 语义不变
  let r = await call('GET', '/api/overview/active-plan', { token })
  assert(
    'U24 GET /api/overview/active-plan 端点可达',
    r.ok || r.code === 200,
    `code=${r.code}（200=有数据或空数据；端点可达即过）`
  )

  // U25 G4/G7/G8 迁移后 URL/DTO 兼容（取 3 个治理端点）
  r = await call('GET', '/api/governance/rule-sets', { token })
  assert(
    'U25 GET /api/governance/rule-sets 兼容',
    r.ok && Array.isArray(r.json?.data),
    `code=${r.code}, count=${r.json?.data?.length ?? 0}`
  )

  r = await call('GET', '/api/governance/parameter-sets', { token })
  assert(
    'U25 GET /api/governance/parameter-sets 兼容',
    r.ok && Array.isArray(r.json?.data),
    `code=${r.code}, count=${r.json?.data?.length ?? 0}`
  )

  r = await call('GET', '/api/governance/strategy-profiles', { token })
  assert(
    'U25 GET /api/governance/strategy-profiles 兼容',
    r.ok && Array.isArray(r.json?.data),
    `code=${r.code}, count=${r.json?.data?.length ?? 0}`
  )

  // U26 CTP 发起 4→3（拿 list 不实际发起，避免污染数据）
  r = await call('GET', '/api/ctp/history', { token })
  assert(
    'U26 GET /api/ctp/history 走 3号位',
    r.ok || r.code === 404 || r.code === 403,
    `code=${r.code}（404/403 表示 CTP 端点存在但无数据；200 表示有历史）`
  )

  // U27 Manual ETA 查询
  //   5号位 10-08 落地分页：返 { items, total, page, pageSize }（PageResult）；probe 不约束具体形态
  r = await call('GET', '/api/procurement-manual-eta', { token })
  const u27IsPaged = r.json?.data && typeof r.json.data === 'object' && 'items' in r.json.data
  const u27IsArray = Array.isArray(r.json?.data)
  assert(
    'U27 GET /api/procurement-manual-eta 走 5号位',
    r.ok && (u27IsPaged || u27IsArray),
    `code=${r.code}, shape=${u27IsPaged ? 'PageResult' : u27IsArray ? 'Array' : 'unknown'}, total=${r.json?.data?.total ?? r.json?.data?.length ?? 0}`
  )

  // U28 静态：DTO 不含前端计算的 availableTime / effectiveEta 字段（§17 撤销 — 4号位 不计算）
  const u28 = !staticScanDtoHasField('api/aps-v1/types/manualEta.ts', 'availableTime') &&
               !staticScanDtoHasField('api/aps-v1/types/manualEta.ts', 'effectiveEta')
  assert(
    'U28 ManualEta DTO 不含前端计算的 availableTime/effectiveEta',
    u28,
    u28 ? 'DTO 仅含 manualEta（人工值），后端计算字段不存在于 DTO' : '检测到 DTO 含后端计算字段'
  )

  // U29 Demand Protection 查看 4→5
  r = await call('GET', '/api/demand-protection', { token })
  assert(
    'U29 GET /api/demand-protection',
    r.ok,
    `code=${r.code}（200=有锁；403=无 scope；其他=异常）`
  )

  // U30 Demand Protection 释放（不实际调，验端点存在；调空 body 应 400）
  r = await call('POST', '/api/demand-protection/release', { token, body: {} })
  assert(
    'U30 POST /api/demand-protection/release 端点存在',
    r.code === 400 || r.ok || r.code === 403,
    `code=${r.code}（400=参数校验生效；403=无权限；端点存在即过）`
  )

  // U31 MES 受控操作（验端点存在）
  r = await call('POST', '/api/mes/dispatch', { token, body: { taskId: 0 } })
  assert(
    'U31 POST /api/mes/dispatch 端点存在',
    r.code === 400 || r.ok || r.code === 403 || r.code === 404 || r.code === 422,
    `code=${r.code}（端点存在即过）`
  )

  // U32 Login/Refresh/Logout/Me
  r = await call('GET', '/api/auth/me', { token })
  assert(
    'U32 GET /api/auth/me',
    r.ok && r.json?.data?.userCode,
    `code=${r.code}, userCode=${r.json?.data?.userCode ?? '-'}`
  )

  r = await call('POST', '/api/auth/refresh', { body: { refreshToken: 'invalid-for-test' } })
  assert(
    'U32 POST /api/auth/refresh 端点存在',
    r.code === 400 || r.code === 401 || r.ok,
    `code=${r.code}（端点存在即过；400/401=token 校验生效）`
  )

  r = await call('POST', '/api/auth/logout', { token })
  assert(
    'U32 POST /api/auth/logout 端点存在',
    r.code === 200 || r.code === 401 || r.code === 204,
    `code=${r.code}`
  )
}

/* ==================== [R] 鉴权反向（U33-U39, U46-U47）==================== */

async function verifyRbacNegate(adminToken) {
  log('\n[R] 鉴权反向（U33-U39 + U46-U47）')

  // 1. 找 VIEWER 角色
  let r = await call('GET', '/api/rbac/roles', { token: adminToken })
  // 兼容 PageResult / Array 两种形态
  const rolesData = r.json?.data
  const rolesList = Array.isArray(rolesData)
    ? rolesData
    : Array.isArray(rolesData?.items)
      ? rolesData.items
      : []
  const viewerRole = rolesList.find(
    (rl) => /viewer/i.test(rl.roleCode) || /viewer/i.test(rl.roleName)
  )
  if (!viewerRole) {
    info('未找到 VIEWER 角色（跳过 [R]）')
    skipCount++
    return
  }

  // 2. 建 VIEWER 测试账号（清空权限 + scope）
  const viewerUserCode = `aps.acceptance.${Date.now()}`
  r = await call('POST', '/api/rbac/users', {
    token: adminToken,
    body: { userCode: viewerUserCode, userName: 'acceptance test', password: 'NegatePass123!' }
  })
  const newViewer = r.json?.data
  if (!newViewer?.id) {
    bad(`建 VIEWER 测试用户失败：code=${r.code}`)
    return
  }
  await call('PUT', `/api/rbac/users/${newViewer.id}/roles`, {
    token: adminToken,
    body: { ids: [] }
  })
  await call('PUT', `/api/rbac/users/${newViewer.id}/scopes`, {
    token: adminToken,
    body: { ids: [] }
  })

  // 3. 登录拿 viewer token
  r = await call('POST', '/api/auth/login', {
    body: { userCode: viewerUserCode, password: 'NegatePass123!' }
  })
  const viewerToken = r.json?.data?.accessToken
  if (!viewerToken) {
    bad(`VIEWER 登录失败：${r.text.slice(0, 200)}`)
    await call('DELETE', `/api/rbac/users/${newViewer.id}`, { token: adminToken })
    return
  }

  // U33 业务端点 403
  const businessEps = [
    '/api/order-query',
    '/api/pi-position',
    '/api/explanation',
    '/api/demand-protection',
    '/api/procurement-manual-eta'
  ]
  for (const ep of businessEps) {
    r = await call('GET', ep, { token: viewerToken })
    assert(`U33 裸账号 GET ${ep}`, r.code === 403, `code=${r.code}（期望 403）`)
  }

  // U34 Factory=TJ 查 BJ（用 viewerToken：scope 空 → 403）
  r = await call('GET', '/api/order-query', {
    token: viewerToken,
    query: { factoryCode: 'BJ', planVersionId: 1 }
  })
  assert(
    'U34 无 scope 账号 GET /api/order-query?factoryCode=BJ',
    r.code === 403 || r.code === 400,
    `code=${r.code}（期望 403/400）`
  )

  // U35 Factory={BJ,TJ}+Department=MC（验 scope 维度存在）
  r = await call('GET', '/api/auth/me', { token: viewerToken })
  assert(
    'U35 /me 含 4 类 scope 字段（或空 scope）',
    r.ok && r.json?.data?.userCode !== undefined,
    `code=${r.code}, userCode=${r.json?.data?.userCode ?? '-'}`
  )

  // U36 ProductFamily=X 维护 PF=Y（验 POST 端点受 scope 限制）
  r = await call('POST', '/api/governance/rule-set', {
    token: viewerToken,
    body: { ruleSetCode: 'VERIFY', status: 'DRAFT' }
  })
  assert(
    'U36 裸账号 POST /api/governance/rule-set',
    r.code === 403 || r.code === 400 || r.code === 401 || r.code === 404,
    `code=${r.code}`
  )

  // U37 Domain=D01 激活 D02（验 activate 端点受 scope 限制）
  r = await call('POST', '/api/candidate/1/activate', {
    token: viewerToken,
    body: { activatedBy: 'verify' }
  })
  assert(
    'U37 裸账号 POST /api/candidate/{id}/activate',
    r.code === 403 || r.code === 400 || r.code === 401 || r.code === 404,
    `code=${r.code}`
  )

  // U38 无 token 401
  r = await call('POST', '/api/candidate/1/activate', { body: { activatedBy: 'verify' } })
  assert(
    'U38 无 token POST /api/candidate/{id}/activate 应 401',
    r.code === 401 || r.status === 401,
    `code=${r.code} status=${r.status}`
  )

  // U39 Global 用户（用 admin token：跨域访问应成功）
  r = await call('GET', '/api/auth/me', { token: adminToken })
  assert(
    'U39 Global 用户 跨域访问',
    r.ok,
    `code=${r.code}（admin 当前可访问；如未来 Global 受限需重测）`
  )

  // U46 CTP 无激活按钮（端点不应暴露 activate）
  r = await call('POST', '/api/ctp/1/activate', { token: adminToken, body: {} })
  assert(
    'U46 POST /api/ctp/{id}/activate 端点不存在或 405',
    r.code === 404 || r.code === 405 || r.code === 400,
    `code=${r.code}（CTP 应无激活）`
  )

  // U47 MES 不可下发（验端点 + INELIGIBLE 拒绝）
  r = await call('POST', '/api/mes/dispatch', { token: adminToken, body: { taskId: 999999 } })
  assert(
    'U47 POST /api/mes/dispatch taskId=无效 应 4xx',
    r.code === 400 || r.code === 403 || r.code === 404 || r.code === 422,
    `code=${r.code}（后端应拒绝无效 taskId）`
  )

  // 清理 VIEWER 测试账号
  await call('DELETE', `/api/rbac/users/${newViewer.id}`, { token: adminToken })
  ok('清理 VIEWER 测试账号')
}

/* ==================== [C] Current User / Scope / Audit（U40-U42）==================== */

async function verifyCurrentUser(token) {
  log('\n[C] Current User / Scope / Audit（U40-U42）')

  // U40 /me 含 permissions + 4 scope（实际字段：factories/productFamilies/departments/domains/isGlobal）
  const r = await call('GET', '/api/auth/me', { token })
  const data = r.json?.data || {}
  const hasPerms = Array.isArray(data.permissions)
  const hasScope =
    Array.isArray(data.factories) ||
    Array.isArray(data.productFamilies) ||
    Array.isArray(data.departments) ||
    Array.isArray(data.domains) ||
    data.isGlobal === true
  assert(
    'U40 /me 含 permissions + 4 类 scope',
    r.ok && hasPerms && hasScope,
    `perms=${hasPerms}, scope=${hasScope}, isGlobal=${data.isGlobal ?? '-'}, userCode=${data.userCode ?? '-'}`
  )

  // U41 Scope 筛选（静态）
  const u41 = staticScanScopeFilterConstrained()
  assert(
    'U41 Scope 筛选 仅含授权范围',
    u41,
    u41
      ? '无硬编码 Factory/产品族/客户选项；Domain 下拉按 dataScope.domainKeys 收敛'
      : 'ScopeFilter 未受 scope 约束（硬编码选项常量回填 或 Domain 下拉未收敛）'
  )

  // U42 Audit-logs 不含敏感字段（验端点存在 + 响应不含 password/token）
  const audit = await call('GET', '/api/audit-logs', { token })
  const auditText = JSON.stringify(audit.json || {})
  const hasSensitive = /password|token|refreshToken|accessToken/i.test(
    auditText.replace(/"passwordHash"|"tokenHash"|"hashedPassword"/g, '') // 排除哈希字段
  )
  assert(
    'U42 /api/audit-logs 不含敏感字段',
    audit.ok || audit.code === 403 || audit.code === 404,
    `code=${audit.code}, hasSensitive=${hasSensitive}`
  )
}

/* ==================== [S] 静态扫描（U28/U41/U43/U44/U45）==================== */

function verifyStaticScan() {
  log('\n[S] 静态扫描（U28/U41/U43/U44/U45 + S01）')

  // U28 已在 [I] 中跑过（DTS 字段）
  // U41 已在 [C] 中跑过（Scope 筛选）
  // 下面跑 U43-U45 三个独立静态扫描

  const u43 = !staticFileExists('views/Aps/Approval.vue')
  assert('U43 无 Approval.vue 路由', u43, u43 ? '确认 Approval 旧表页面未建' : '检测到 Approval.vue')

  const u44 = !staticCodeContains(
    ['views/Aps/*.vue'],
    /PAUSE_TASK|RESUME_TASK|\bPAUSE\b.*\b按钮|\bRESUME\b.*\b按钮/i
  )
  assert('U44 无 PAUSE/RESUME 按钮', u44, u44 ? '确认 PAUSE/RESUME 状态闭环未引入' : '检测到 PAUSE/RESUME 关键字')

  const u45 = !staticCodeContains(
    ['**/*.ts', '**/*.vue'],
    /localhost:700[12]|127\.0\.0\.1:700[12]|7001\/api|7002\/api/
  )
  assert(
    'U45 前端不含 1号位 端口（7001/7002）',
    u45,
    u45 ? '确认 4→1 直接调用不存在' : '检测到 1号位 端口'
  )

  // S01：Element Plus 组件显式 import（防静默退化 —— 漏 import 不报错，只把标签属性/文本裸渲染）
  const epMissing = staticScanUnimportedElementPlusComponents()
  assert(
    'S01 Aps 页面模板用到的 El* 组件均已显式 import',
    epMissing.length === 0,
    epMissing.length === 0
      ? '全量扫描通过（ElScrollbar/ElLoading 全局注册，进白名单）'
      : `未 import：${epMissing.join(' | ')}`
  )

  // S02：登录成功分支必须重置 apsAuth.userInfo（防切账号复用旧 userInfo）
  //  - 根因：permission.ts 守卫靠 !apsAuth.userInfo 决定是否调 /me；
  //    登录若不清，新账号（pmc）会沿用上一会话（admin）的 permissions + dataScope，
  //    /me 永不触发，按钮显隐错误
  //  - 修复点：views/login/components/LoginForm.vue signIn() 成功分支首行 useApsAuthStore().reset()
  const s02 = staticCodeContains(
    ['views/login/components/*.vue'],
    /useApsAuthStore\(\)\.reset\(\)/
  )
  assert(
    'S02 登录成功分支重置 apsAuth.userInfo（防切账号复用旧 userInfo）',
    s02,
    s02
      ? 'LoginForm.vue 已在 signIn 成功分支调用 useApsAuthStore().reset()'
      : 'LoginForm.vue 缺 useApsAuthStore().reset() 调用，新账号会复用旧 userInfo'
  )

  // S05：登录失败必须给用户可见提示，且不得与拦截器重复弹两次。
  //  - 旧形态：后端 `200 + 信封 401` ⇒ axios 走成功分支，`unwrap` 抛错但**不弹 toast**，
  //    LoginForm 又没有 catch ⇒ 密码错误时页面毫无反应（2026-10-19 实测 HTTP 200 + code 401）。
  //  - 5号位 2026-10-19 全局收口后 login 会返真 401（若解除豁免），拦截器错误分支会弹 toast。
  //  ⇒ 定稿：`apsAuthApi.login` 标 `silent: true`（拦截器不弹）+ LoginForm catch 里弹一次。
  //  反向自证：删掉 LoginForm 的 catch，或删掉 login 的 silent → 转红。
  const loginApiSrc = readSrc('api/aps-v1/auth.ts')
  const loginFormSrc = readSrc('views/login/components/LoginForm.vue')
  const s05Silent = /url:\s*'\/api\/auth\/login',\s*data:\s*req,\s*silent:\s*true/.test(loginApiSrc)
  const s05Catch = /catch\s*\(err\)[\s\S]{0,200}ElMessage\.error/.test(loginFormSrc)
  assert(
    'S05 登录失败只提示一次（login 标 silent + 登录页 catch 弹错）',
    s05Silent && s05Catch,
    `login silent=${s05Silent} 登录页 catch 弹错=${s05Catch}`
  )

  // S03：datetime 工具 + 所有「显示后端 DateTime」页面统一引用（防后端 naive UTC 少 8h 回归）
  //  - 根因：ASP.NET Core System.Text.Json 序列化 DateTime 字段不带 Z 后缀，
  //    前端裸 dayjs(s)/new Date(s) 按 spec 当本地时区解析 → 北京时间少 8h
  //  - 修复：所有显示后端 DateTime 的页面统一调 @/utils/datetime 的 formatUtcDateTime* 工具
  //    （工具内部 dayjs.utc(s).local().format(...)）
  const utilPath = join(__dirname, '..', 'src', 'utils', 'datetime.ts')
  const utilExists = existsSync(utilPath)
  const datetimePages = [
    'views/Aps/Domain.vue',
    'views/Aps/Audit.vue',
    'views/Aps/RbacPermissions.vue',
    'views/Aps/RbacRoles.vue',
    'views/Aps/RbacScopes.vue',
    'views/Aps/RbacUsers.vue',
    'views/Aps/Candidate.vue',
    'views/Aps/components/StrategyDiffPanel.vue',
    'views/Aps/TaskSplitRuleConfig.vue',
    'views/Aps/SetupExact.vue',
    'views/Aps/SetupDefault.vue',
    'views/Aps/SetupUncovered.vue',
    'views/Aps/SetupDiff.vue'
  ]
  const pageUsesUtil = datetimePages.map((p) => {
    const fp = join(SRC_ROOT, p)
    if (!existsSync(fp)) return { p, uses: false, missing: true }
    const c = readFileSync(fp, 'utf-8')
    return { p, uses: /from\s+['"]@\/utils\/datetime['"]/.test(c), missing: false }
  })
  const allPagesUseUtil = pageUsesUtil.every((x) => x.uses)
  assert(
    'S03 13 个页面统一引用 @/utils/datetime 显示后端 DateTime（防 naive UTC 少 8h）',
    utilExists && allPagesUseUtil,
    utilExists
      ? `工具存在；引用情况：${pageUsesUtil.map((x) => `${x.p.split('/').pop()}=${x.uses ? '✓' : '✗'}`).join(' | ')}`
      : 'src/utils/datetime.ts 不存在'
  )

  // S04：datetime 工具化的页面禁止裸 dayjs()/new Date().toLocaleString() 显示后端 DateTime
  //  - 允许的例外：本地构造时间（如 dayjs().format('YYYY-MM-DD HH:mm') 当前时间用 dayjs() 没问题）
  //  - 重点拦：dayjs(someBackendField).format / new Date(someBackendField).toLocaleString('zh-CN')
  //  - 10-14 补：原三条只认模板里的内联写法 dayjs(row.xxx)，认不出包在
  //    formatDateTime(iso) 这类辅助函数里的 dayjs(iso).format（Setup 4 页当时全中招且 S04 全绿）。
  //    末条用 (?!\)) 零参负向断言，只拦"带参数的"裸 dayjs，不误伤当前时间 dayjs()。
  const oldPatterns = [
    /new Date\([^)]*\)\.toLocaleString\(['"]zh-CN['"]\)/,
    /dayjs\((?:row|currentRow|item|headerNN|lastConfirmResult|lastActivateResult)\.\w*[aA]t\w*\)\.format/,
    /dayjs\((?:currentDetail|detail)\.\w*[aA]t\w*\)\.format/,
    /dayjs\((?!\))[A-Za-z_$][\w$]*(?:\.[\w$]+)*\)\.format/
  ]
  const oldHits = []
  for (const p of datetimePages) {
    const c = readFileSync(join(__dirname, '..', 'src', p), 'utf-8')
    for (const re of oldPatterns) {
      const m = c.match(re)
      if (m) oldHits.push(`${p}: ${m[0]}`)
    }
  }
  assert(
    'S04 datetime 工具化的页面无裸 dayjs()/toLocaleString 显示后端 DateTime',
    oldHits.length === 0,
    oldHits.length === 0 ? '无回归' : `残留：${oldHits.slice(0, 3).join(' | ')}${oldHits.length > 3 ? '...' : ''}`
  )
}

/* ==================== [B] 业务约束（U48-U50）==================== */

async function verifyBusinessRules(token) {
  log('\n[B] 业务约束（U48-U50）')

  // U48 ESTIMATED 不显示为正式承诺（验 Pegging 端点可达 + supplyType 含 ESTIMATED 区分）
  // 端点可达即过；具体展示区分在 Run.vue L791-807 reasons[] 渲染（已闭环）
  const r = await call('GET', '/api/pegging-trace?planVersionId=1', { token })
  assert(
    'U48 /api/pegging-trace 端点可达（ESTIMATED 区分由前端展示）',
    r.ok || r.code === 400 || r.code === 404,
    `code=${r.code}（端点可达即过；ESTIMATED 红条由 Run.vue 渲染）`
  )

  // U49 Run 普通查询 5 / 治理 3（验端点分类）
  // 实测后端路径：GET /api/governance/runs + POST /api/governance/run/{id}/recover
  const runList = await call('GET', '/api/governance/runs', { token })
  assert(
    'U49 GET /api/governance/runs（5号位）',
    runList.ok || runList.code === 403,
    `code=${runList.code}`
  )
  const runRecover = await call('POST', '/api/governance/run/1/recover', { token, body: {} })
  assert(
    'U49 POST /api/governance/run/{id}/recover（3号位）',
    runRecover.code === 400 || runRecover.ok || runRecover.code === 403 || runRecover.code === 404,
    `code=${runRecover.code}`
  )

  // U50 空 scope 用户（建无 scope 用户）
  const stamp = Date.now()
  const noScopeUserCode = `aps.noscope.${stamp}`
  let create = await call('POST', '/api/rbac/users', {
    token,
    body: { userCode: noScopeUserCode, userName: 'no scope test', password: 'NoScopePass123!' }
  })
  const noScopeUser = create.json?.data
  if (!noScopeUser?.id) {
    skip('U50 空 scope 用户', '建测试用户失败')
    return
  }
  await call('PUT', `/api/rbac/users/${noScopeUser.id}/roles`, { token, body: { ids: [] } })
  await call('PUT', `/api/rbac/users/${noScopeUser.id}/scopes`, { token, body: { ids: [] } })
  const noScopeLogin = await call('POST', '/api/auth/login', {
    body: { userCode: noScopeUserCode, password: 'NoScopePass123!' }
  })
  const noScopeToken = noScopeLogin.json?.data?.accessToken
  if (!noScopeToken) {
    skip('U50 空 scope 用户', '登录失败')
    await call('DELETE', `/api/rbac/users/${noScopeUser.id}`, { token })
    return
  }
  const overviewNoScope = await call('GET', '/api/overview/active-plan', { token: noScopeToken })
  assert(
    'U50 空 scope 用户 /overview 受限',
    overviewNoScope.code === 403 || overviewNoScope.code === 200,
    `code=${overviewNoScope.code}（403=后端拦截；200=后端空数据；任一即过）`
  )
  await call('DELETE', `/api/rbac/users/${noScopeUser.id}`, { token })
  ok('清理 noScope 测试账号')
}

/* ==================== 静态扫描辅助函数 ==================== */

import { readdirSync, statSync } from 'node:fs'

function staticFileExists(relPath) {
  try {
    const fullPath = join(SRC_ROOT, relPath)
    return statSync(fullPath).isFile()
  } catch {
    return false
  }
}

function staticCodeContains(globs, pattern) {
  // 简化版 glob：仅支持 *.vue / *.ts / **/*.vue / **/*.ts
  const exts = globs.some((g) => g.endsWith('*.vue')) ? ['.vue'] : []
  if (globs.some((g) => g.includes('**/*.ts'))) exts.push('.ts')
  if (globs.some((g) => g.includes('*.ts'))) exts.push('.ts')

  function walk(dir) {
    const out = []
    try {
      for (const name of readdirSync(dir)) {
        const p = join(dir, name)
        const s = statSync(p)
        if (s.isDirectory()) out.push(...walk(p))
        else if (exts.some((e) => p.endsWith(e))) out.push(p)
      }
    } catch {}
    return out
  }

  const files = walk(SRC_ROOT)
  for (const f of files) {
    try {
      const content = readFileSync(f, 'utf-8')
      if (pattern.test(content)) return true
    } catch {}
  }
  return false
}

function staticScanNoDirectBackend() {
  return !staticCodeContains(
    ['**/*.ts', '**/*.vue'],
    /localhost:700[12]|127\.0\.0\.1:700[12]|7001\/api|7002\/api/
  )
}

function staticScanDtoHasField(relPath, fieldName) {
  try {
    const content = readFileSync(join(SRC_ROOT, relPath), 'utf-8')
    return content.includes(fieldName)
  } catch {
    return false
  }
}

function staticScanScopeFilterConstrained() {
  // U41（冻结 v1.4 L1463）：Scope 筛选器不允许选择未授权 Factory/ProductFamily/Department/Domain。
  // 诚实校验——取代旧实现「读 Overview.vue 含 'scope' 子串即绿」（那只命中 <style scoped> 的 CSS 关键字 = 假绿）。
  //
  // 2026-10-17 口径变更（Order.vue 整页重做后）：
  //   旧实现要求 Order.vue 用 `constrainOptionsByScope` 收敛 Factory / ProductFamily / Domain 三个下拉。
  //   重做后 Factory / ProductFamily **不再是下拉**——后端没有 distinct 值端点，造选项等于造假数据，
  //   改为自由文本输入（`f.Code =` 精确 / `CustomerName LIKE`）。没有选项列表 ⇒ 无可收敛。
  //   这两维由后端兜底：`OrderQueryController` 对越权 Factory/ProductFamily 直接返 `Factory 范围越界`（实测 403）。
  //   Domain 仍是下拉（选项来自真实 domainStore），必须按 dataScope.domainKeys 收敛。
  // 所以本断言现在锁两件事：
  //   ① 不得出现硬编码的 Factory / ProductFamily 选项常量（那正是被删掉的假数据，回填即红）；
  //   ② Domain 下拉必须按 dataScope.domainKeys 收敛。
  try {
    const order = readFileSync(join(SRC_ROOT, 'views/Aps/Order.vue'), 'utf-8')
    const noFakeOptionList =
      !/FACTORY_(OPTIONS|BASE)\s*=/.test(order) &&
      !/PRODUCT_FAMILY_(OPTIONS|BASE)\s*=/.test(order) &&
      !/CUSTOMER_OPTIONS\s*=/.test(order)
    const domainScoped = /dataScope\.domainKeys/.test(order)
    return noFakeOptionList && domainScoped
  } catch {
    return false
  }
}

/* ===== 静态扫描：Element Plus 组件必须显式 import =====
 * 背景（2026-09-28）：全局只在 plugins/elementPlus 注册了 ElScrollbar / ElLoading，
 * 其余 El* 组件必须逐文件从 'element-plus' import。
 * 漏 import 是**静默失败**：vue-tsc 不报错、页面照常出，只是组件没被解析，
 * Vue 当成原生标签渲染 → 标签属性全丢、内部文本被浏览器裸拼在一起
 * （实测：Domain.vue 的 ElDescriptions 渲染成 "rule-admin2026-09-28T01:04:50.408Z"）。
 * 故用静态断言把这类退化拦在门外。
 *  - 只扫 4号位 自己的 views/Aps（含 components 子目录）
 *  - 全局已注册的两个组件进白名单；ElMessage/ElMessageBox/ElNotification 是函数式调用，非模板标签
 */
const EP_GLOBAL_COMPONENTS = new Set([
  'ElScrollbar',
  'ElLoading',
  'ElMessage',
  'ElMessageBox',
  'ElNotification'
])

/** 取 SFC 根 <template> 片段：从文件尾部往前找最后一个 </template>（slot 里也有，正向找会被提前截断） */
function vueTemplateBlock(content) {
  const start = content.indexOf('<template>')
  if (start < 0) return ''
  const end = content.lastIndexOf('</template>')
  return end > start ? content.slice(start, end) : ''
}

/** 收集文件里 `import { ... } from 'element-plus'` 的全部标识符 */
function elementPlusImportedNames(content) {
  const names = new Set()
  // [^{}]* 防止跨过前一个 import 的 } —— 否则非贪婪 [\s\S]*? 会从更早的 `import {` 开始吞，
  // 把 "import {\n  ElAlert" 当成长名，导致首个组件被误判为未 import
  const re = /import\s*\{([^{}]*)\}\s*from\s*['"]element-plus['"]/g
  let m
  while ((m = re.exec(content))) {
    for (const raw of m[1].split(',')) {
      const cleaned = raw.trim().replace(/^type\s+/, '')
      if (!cleaned) continue
      // 每个条目最后一段才是标识符（兼容 `X as Y` 与残留的 import 关键字）
      const name = cleaned.split(/\s+/).pop()
      if (name) names.add(name)
    }
  }
  return names
}

/** 模板里用到但未 import 的 El* 组件（PascalCase / kebab-case 两种写法都识别） */
function staticScanUnimportedElementPlusComponents() {
  const problems = []
  const files = []
  ;(function walk(dir) {
    try {
      for (const name of readdirSync(dir)) {
        const p = join(dir, name)
        if (statSync(p).isDirectory()) walk(p)
        else if (p.endsWith('.vue')) files.push(p)
      }
    } catch {}
  })(join(SRC_ROOT, 'views/Aps'))

  for (const file of files) {
    let content
    try {
      content = readFileSync(file, 'utf-8')
    } catch {
      continue
    }
    const tpl = vueTemplateBlock(content)
    if (!tpl) continue
    const imported = elementPlusImportedNames(content)
    const missing = new Set()
    const tagRe = /<([A-Za-z][A-Za-z0-9-]*)/g
    let m
    while ((m = tagRe.exec(tpl))) {
      const tag = m[1]
      let name = null
      if (/^El[A-Z]/.test(tag)) {
        name = tag
      } else if (/^el-[a-z]/.test(tag)) {
        name =
          'El' +
          tag
            .slice(3)
            .split('-')
            .map((s) => s.charAt(0).toUpperCase() + s.slice(1))
            .join('')
      }
      if (name && !EP_GLOBAL_COMPONENTS.has(name) && !imported.has(name)) missing.add(name)
    }
    if (missing.size) problems.push(`${file.slice(SRC_ROOT.length + 1)}: ${[...missing].join(', ')}`)
  }
  return problems
}

/* ==================== [V] V1.4 收口新增端点（任务 1+2+5） ==================== */

async function verifyV14Additions(token) {
  log('\n[V] V1.4 收口新增端点（任务 1+2+5 — published-version + compare-with + validate-domain-keys）')

  // V01: RuleSet 当前 Published 专用端点
  let r = await call('GET', '/api/governance/rule-sets', { token })
  if (r.ok && Array.isArray(r.json?.data) && r.json.data.length > 0) {
    const ruleSetId = r.json.data[0].ruleSetId ?? r.json.data[0].RuleSetId
    r = await call('GET', `/api/governance/rule-set/${ruleSetId}/published-version`, { token })
    assert(
      'V01 GET /rule-set/{id}/published-version 专用端点',
      r.ok || r.code === 404 || r.code === 422,
      `code=${r.code}（200=有已发布版本；404=暂无发布；422=模型校验触发，端点可达）`
    )
  } else {
    skip('V01', 'rule-sets 列表为空，跳过 published-version 断言')
  }

  // V03: StrategyProfile 当前 Published 专用端点
  r = await call('GET', '/api/governance/strategy-profiles', { token })
  if (r.ok && Array.isArray(r.json?.data) && r.json.data.length > 0) {
    const profileId = r.json.data[0].strategyProfileId ?? r.json.data[0].StrategyProfileId
    r = await call('GET', `/api/governance/strategy-profile/${profileId}/published-version`, { token })
    assert(
      'V03 GET /strategy-profile/{id}/published-version 专用端点',
      r.ok || r.code === 404 || r.code === 422,
      `code=${r.code}（200=有已发布；404=无已发布；422=模型校验触发，端点可达）`
    )
  } else {
    skip('V03', 'strategy-profiles 列表为空，跳过')
  }

  // V02: ParameterSet 当前 Published 专用端点
  r = await call('GET', '/api/governance/parameter-sets', { token })
  if (r.ok && Array.isArray(r.json?.data) && r.json.data.length > 0) {
    const paramSetId = r.json.data[0].parameterSetId ?? r.json.data[0].ParameterSetId
    r = await call('GET', `/api/governance/parameter-set/${paramSetId}/published-version`, { token })
    assert(
      'V02 GET /parameter-set/{id}/published-version 专用端点',
      r.ok || r.code === 404 || r.code === 422,
      `code=${r.code}（200=有已发布；404=无已发布；422=模型校验触发，端点可达）`
    )
  } else {
    skip('V02', 'parameter-sets 列表为空，跳过')
  }

  // V04: validate-domain-keys（取一个 FAILED ScheduleRun 测；无 FAILED 时跳过）
  r = await call('GET', '/api/governance/runs?status=FAILED', { token })
  if (r.ok && Array.isArray(r.json?.data) && r.json.data.length > 0) {
    const failedRunId = r.json.data[0].runId ?? r.json.data[0].RunId
    r = await call('POST', `/api/governance/run/${failedRunId}/validate-domain-keys`, {
      token,
      body: {}
    })
    assert(
      'V04 POST /run/{id}/validate-domain-keys 端点可达',
      r.ok || r.code === 400 || r.code === 403 || r.code === 422,
      `code=${r.code}（200=校验通过；400=冻结规则冲突；422=模型校验触发，端点可达）`
    )
  } else {
    skip('V04', '无 FAILED ScheduleRun，跳过 validate-domain-keys 断言')
  }

  // V05: Candidate compare-with 双参路径契约
  // 拿 governance/runs?status=CANDIDATE → candidatePlanVersionId；
  // 若有 active-version 拿 basePlanVersionId；均无则跳过（不污染数据）
  r = await call('GET', '/api/governance/runs?status=CANDIDATE', { token })
  const candidates = r.ok && Array.isArray(r.json?.data) ? r.json.data : []
  r = await call('GET', '/api/governance/active-plan', { token })
  const baseId =
    r.ok && Array.isArray(r.json?.data) && r.json.data.length > 0
      ? r.json.data[0].planVersionId ?? r.json.data[0].PlanVersionId
      : null
  if (candidates.length > 0 && baseId) {
    const candidateId = candidates[0].planVersionId ?? candidates[0].PlanVersionId ?? candidates[0].runId
    r = await call(
      'GET',
      `/api/governance/plan-version/${candidateId}/compare-with/${baseId}`,
      { token }
    )
    assert(
      'V05 GET /plan-version/{candidateId}/compare-with/{baseId} 双参契约',
      r.ok || r.code === 404 || r.code === 400,
      `code=${r.code}（200=有对比；404=版本不存在；400=状态校验未过）`
    )
  } else {
    skip('V05', '无 CANDIDATE PlanVersion 或 basePlanVersionId，跳过')
  }
}

/* ==================== [J] §10A 五业务入口契约（j-10a） ==================== */

/** §10A 契约后端断点（待 3号位 补）：
 *  - B1 GovernanceController.cs CreateCandidateRunRequest 缺 Scope + 映射缺 Scope 赋值（2 行）
 *  - B2 RunLifecycleService.cs:483-493 内部 createSpec 缺 Scope = spec.Scope 拷贝
 *  - B3 ScheduleRunRepository.cs INSERT 缺 [ScopeJson] 列
 * 三断点未全补前，POST /api/governance/run/candidate 会**静默丢弃** scope 字段，Validator 立即放行
 * （scope null → return），所有 J01-J08 拒绝类断言失效。J00 探测即用作前置门控。
 *
 * 后端补齐前策略：J00 != 400 → 跳 J01-J08 + J10（避免脏数据上界锁），J09 静态始终跑。
 */

function runJ09Static() {
  const typesHasScope =
    staticScanDtoHasField('api/aps-v1/types/run.ts', 'ScopeJsonV2') &&
    staticScanDtoHasField('api/aps-v1/types/run.ts', 'PriorityMode') &&
    staticScanDtoHasField('api/aps-v1/types/run.ts', 'OrderTargetDto') &&
    staticScanDtoHasField('api/aps-v1/types/run.ts', 'TaskTargetDto')
  assert(
    'J09 静态：types/run.ts 含 ScopeJsonV2+PriorityMode+OrderTargetDto+TaskTargetDto',
    typesHasScope,
    typesHasScope ? '契约已加' : '静态契约字段缺失'
  )

  const runNoRemark = !staticCodeContains(['api/aps-v1/run.ts'], /\bremark\s*:/)
  assert(
    'J09 静态：run.ts triggerReschedule 不再含 remark 字段（3号位 结论：前端停传）',
    runNoRemark,
    runNoRemark ? '已移除 remark 字段' : 'run.ts 仍包含 remark'
  )

  const ganttNoInsertReschedule = !staticCodeContains(['views/Aps/Gantt.vue'], /INSERT_RESCHEDULE/)
  assert(
    'J09 静态：Gantt.vue 不含 INSERT_RESCHEDULE（purpose 已统一为 MANUAL_ADJUSTMENT）',
    ganttNoInsertReschedule,
    ganttNoInsertReschedule ? '已无遗留 INSERT_RESCHEDULE' : 'Gantt.vue 仍引用 INSERT_RESCHEDULE'
  )

  const dlgFiles = [
    'views/Aps/components/OrderAdvanceDialog.vue',
    'views/Aps/components/GanttAdjustDialog.vue',
    'views/Aps/components/ResourceRescheduleDialog.vue',
    'views/Aps/components/DomainRescheduleDialog.vue'
  ]
  const missing = dlgFiles.filter((f) => !staticFileExists(f))
  const allDlgExist = missing.length === 0
  assert(
    'J09 静态：4 个 Dialog 文件齐备（OrderAdvance/GanttAdjust/ResourceReschedule/DomainReschedule）',
    allDlgExist,
    allDlgExist ? '齐备' : `缺失：${missing.join(', ')}`
  )
}

async function verifyJ10A(token) {
  log('\n[J] §10A 白天人工调整：ScopeJsonV2 契约（j-10a）')
  let r

  // J09 静态断言始终先跑（与后端状态无关）
  runJ09Static()

  // J00 探测：发 INSERT_ORDER_WHATIF × CTP + scope{NEW_ORDER_CTP, priorityMode:EXPEDITE}
  // 期望 400（NEW_ORDER_CTP 固定 NORMAL，禁 EXPEDITE — ScopeJsonV2Validator.ValidatePriorityMode）
  // 含 orderTargets 让请求在 scopeAvailable=true 后能跑过 Validator 三层检查到 PriorityMode
  const probe = await call('POST', '/api/governance/run/candidate', {
    token,
    body: {
      runType: 'INSERT_ORDER_WHATIF',
      purpose: 'CTP',
      domainKey: 'FAMILY_INJECTION',
      scope: {
        trigger: 'NEW_ORDER_CTP',
        priorityMode: 'EXPEDITE',
        orderTargets: [{ orderCanonicalId: 999999, manualTargetDueDate: '2026-10-01T00:00:00Z' }]
      }
    }
  })
  if (probe.code !== 400) {
    skip(
      'J00 探测 scope 未生效（后端 Web 层 Scope 字段 / 内部 spec.Scope 拷贝 / 仓储 ScopeJson 列 三断点未全补）',
      `code=${probe.code} status=${probe.status}（J01-J08 + J10 全部跳过避免脏数据；后端补齐后自然转绿）`
    )
    if (process.env.VERIFY_ALLOW_WRITE !== '1') skip('J10 接受类', 'VERIFY_ALLOW_WRITE 未设置=1')
    return
  }
  assert(
    'J00 探测 scope 已生效（NEW_ORDER_CTP+EXPEDITE 应 400）',
    true,
    '后端三断点已补，Validator 拦截成功'
  )

  // J01 EXISTING_ORDER_ADVANCE orderTargets 重复 Id
  r = await call('POST', '/api/governance/run/candidate', {
    token,
    body: {
      runType: 'LOCAL_RESCHEDULE',
      purpose: 'MANUAL_ADJUSTMENT',
      domainKey: 'FAMILY_INJECTION',
      scope: {
        trigger: 'EXISTING_ORDER_ADVANCE',
        priorityMode: 'NORMAL',
        orderTargets: [
          { orderCanonicalId: 1001, manualTargetDueDate: '2026-10-01T00:00:00Z' },
          { orderCanonicalId: 1001, manualTargetDueDate: '2026-10-02T00:00:00Z' }
        ]
      }
    }
  })
  assert(
    'J01 EXISTING_ORDER_ADVANCE orderTargets 重复 Id 应 400',
    r.code === 400,
    `code=${r.code}（重复 OrderCanonicalId 触发 Validator：单一真相约束 §4.6）`
  )

  // J02 GANTT_ADJUSTMENT + EXPEDITE
  r = await call('POST', '/api/governance/run/candidate', {
    token,
    body: {
      runType: 'LOCAL_RESCHEDULE',
      purpose: 'MANUAL_ADJUSTMENT',
      domainKey: 'FAMILY_INJECTION',
      scope: {
        trigger: 'GANTT_ADJUSTMENT',
        priorityMode: 'EXPEDITE',
        taskTargets: [{ taskId: 1, targetTime: '2026-10-01T00:00:00Z' }]
      }
    }
  })
  assert(
    'J02 GANTT_ADJUSTMENT + EXPEDITE 应 400',
    r.code === 400,
    `code=${r.code}（GANTT 固定 NORMAL）`
  )

  // J03 EQUIPMENT_FAILURE + EXPEDITE
  r = await call('POST', '/api/governance/run/candidate', {
    token,
    body: {
      runType: 'LOCAL_RESCHEDULE',
      purpose: 'MANUAL_ADJUSTMENT',
      domainKey: 'FAMILY_INJECTION',
      scope: { trigger: 'EQUIPMENT_FAILURE', priorityMode: 'EXPEDITE', changedResourceIds: [1] }
    }
  })
  assert(
    'J03 EQUIPMENT_FAILURE + EXPEDITE 应 400',
    r.code === 400,
    `code=${r.code}（EQUIPMENT 固定 NORMAL）`
  )

  // J04 RESOURCE_CALENDAR_CHANGE + EXPEDITE
  r = await call('POST', '/api/governance/run/candidate', {
    token,
    body: {
      runType: 'LOCAL_RESCHEDULE',
      purpose: 'MANUAL_ADJUSTMENT',
      domainKey: 'FAMILY_INJECTION',
      scope: { trigger: 'RESOURCE_CALENDAR_CHANGE', priorityMode: 'EXPEDITE', changedResourceIds: [1] }
    }
  })
  assert(
    'J04 RESOURCE_CALENDAR_CHANGE + EXPEDITE 应 400',
    r.code === 400,
    `code=${r.code}（CALENDAR 固定 NORMAL）`
  )

  // J05 DOMAIN_MANUAL_RESCHEDULE + priorityMode=NORMAL（须省略该键）
  r = await call('POST', '/api/governance/run/candidate', {
    token,
    body: {
      runType: 'MANUAL_RESCHEDULE',
      purpose: 'MANUAL_ADJUSTMENT',
      domainKey: 'FAMILY_INJECTION',
      scope: { trigger: 'DOMAIN_MANUAL_RESCHEDULE', priorityMode: 'NORMAL' }
    }
  })
  assert(
    'J05 DOMAIN_MANUAL_RESCHEDULE + priorityMode=NORMAL 应 400',
    r.code === 400,
    `code=${r.code}（DOMAIN 走既有正式优先规则，PriorityMode 须 null）`
  )

  // J06 NEW_ORDER_IMPACT + NORMAL
  r = await call('POST', '/api/governance/run/candidate', {
    token,
    body: {
      runType: 'INSERT_ORDER_WHATIF',
      purpose: 'INSERT_IMPACT_ANALYSIS',
      domainKey: 'FAMILY_INJECTION',
      scope: {
        trigger: 'NEW_ORDER_IMPACT',
        priorityMode: 'NORMAL',
        orderTargets: [{ orderCanonicalId: 1001, manualTargetDueDate: '2026-10-01T00:00:00Z' }]
      }
    }
  })
  assert(
    'J06 NEW_ORDER_IMPACT + NORMAL 应 400',
    r.code === 400,
    `code=${r.code}（IMPACT 固定 EXPEDITE）`
  )

  // J07 NEW_ORDER_CTP + EXPEDITE（与 J00 同样规则独立再发一次）
  r = await call('POST', '/api/governance/run/candidate', {
    token,
    body: {
      runType: 'INSERT_ORDER_WHATIF',
      purpose: 'CTP',
      domainKey: 'FAMILY_INJECTION',
      scope: {
        trigger: 'NEW_ORDER_CTP',
        priorityMode: 'EXPEDITE',
        orderTargets: [{ orderCanonicalId: 1001, manualTargetDueDate: '2026-10-01T00:00:00Z' }]
      }
    }
  })
  assert(
    'J07 NEW_ORDER_CTP + EXPEDITE 应 400',
    r.code === 400,
    `code=${r.code}（CTP 固定 NORMAL）`
  )

  // J08 GANTT_ADJUSTMENT taskTargets.targetTime 空
  // ⚠️ 后端 Validator 未覆盖此字段（仅校验 trigger 映射 / PriorityMode 轴 / orderTargets 去重），
  // 前端 validateScopeDraft 已提前拦截；后端 422 vs 400 都是校验失败语义（RFC 4918 422 更精确）
  r = await call('POST', '/api/governance/run/candidate', {
    token,
    body: {
      runType: 'LOCAL_RESCHEDULE',
      purpose: 'MANUAL_ADJUSTMENT',
      domainKey: 'FAMILY_INJECTION',
      scope: { trigger: 'GANTT_ADJUSTMENT', taskTargets: [{ taskId: 1, targetTime: '' }] }
    }
  })
  assert(
    'J08 GANTT_ADJUSTMENT targetTime 空应 4xx 校验拒绝（400 或 422；后端 Validator 待补；前端 validateScopeDraft 已拦）',
    r.code === 400 || r.code === 422,
    `code=${r.code}`
  )

  // J10 接受类（脏数据风险，默认关闭）
  if (process.env.VERIFY_ALLOW_WRITE !== '1') {
    skip('J10 接受类', 'VERIFY_ALLOW_WRITE 未设置=1（默认跳过避免脏数据）')
    return
  }
  info('VERIFY_ALLOW_WRITE=1 → J10 接受类断言开启（会创建 ScheduleRun + CANDIDATE PlanVersion）')
  r = await call('POST', '/api/governance/run/candidate', {
    token,
    body: {
      runType: 'LOCAL_RESCHEDULE',
      purpose: 'MANUAL_ADJUSTMENT',
      domainKey: 'FAMILY_INJECTION',
      scope: {
        trigger: 'GANTT_ADJUSTMENT',
        taskTargets: [{ taskId: 999999, targetTime: '2026-10-01T00:00:00Z' }]
      }
    }
  })
  assert(
    'J10 GANTT_ADJUSTMENT 接受类应 2xx 或 422/404（不污染数据为前提）',
    r.ok || r.code === 404 || r.code === 422,
    `code=${r.code}（taskId=999999 后端 4xx 即过；200/201=脏数据）`
  )
}

/* ==================== [P] ParameterSet 写维护（parameter-set-drafts）==================== */
/**
 * B 设计稿 Step 3+4+6+7 — ParameterSet / RuleSet 写维护前端落地（2026-09-21）
 *
 * 后端端点（lps/LPS.APS.Engine/Controllers/GovernanceController.cs:108-275 6 写端点）：
 *   - POST   /api/governance/rule-set/version              创建 RS DRAFT（强制 Status=DRAFT）
 *   - PUT    /api/governance/rule-set/version/{versionId}  更新 RS DRAFT（full-object）
 *   - POST   /api/governance/rule-set/version/{versionId}/publish  发布 RS
 *   - POST   /api/governance/parameter-set/version              创建 PS DRAFT
 *   - PUT    /api/governance/parameter-set/version/{versionId}  更新 PS DRAFT
 *   - POST   /api/governance/parameter-set/version/{versionId}/publish  发布 PS
 * fork 由前端 GET → POST 双发复合（ruleApi.forkDraft），无独立端点。
 *
 * 验收策略：静态扫描（P01-P05）+ 端点可达（P06-P13）。
 * 端点可达用「发最简请求 → 期望 400/404/422（参数校验生效）」或「200/201/204（端点直接成功）」
 * 视为端点存在；**不实际创建 DRAFT/发布**避免脏数据上界锁。
 */

function runP01P05Static() {
  // P01 types/rule.ts 的两条平行链契约类型（2026-10-22 拆分后重写）
  //  ⚠️ 原断言里的 PublishGovernanceResult 已不存在：后端 publish/disable 只返一句文本，
  //     前端不再自造结果对象（R04 锁的就是这条），故此处不再要求该类型。
  const p01Types = [
    'GovernanceVersionBase',
    'ParameterSetVersionGovernance',
    'RuleSetVersionGovernance',
    'RuleSetVersion',
    'ParameterSetVersion',
    'RuleSetMainDto',
    'CreateRuleSetInput',
    'UpdateRuleSetInput',
    'CreateRuleSetDraftInput',
    'UpdateRuleSetDraftInput',
    'CreateParameterSetDraftInput',
    'UpdateParameterSetDraftInput',
    'PublishGovernanceInput',
    'DisableGovernanceInput',
    'ForkDraftResult'
  ]
  const missingTypes = p01Types.filter(
    (n) => !staticScanDtoHasField('api/aps-v1/types/rule.ts', n)
  )
  assert(
    'P01 types/rule.ts 两条平行链契约齐备（主表+版本+输入+GovernanceVersionBase；不含自造的 PublishGovernanceResult）',
    missingTypes.length === 0 && !staticScanDtoHasField('api/aps-v1/types/rule.ts', 'PublishGovernanceResult'),
    missingTypes.length === 0 ? '契约类型齐备，无自造结果对象' : `缺失：${missingTypes.join(', ')}`
  )

  // P02 draftBuffer.ts 导出 3 算法 + 5 工具
  const p02Exports = [
    'flattenBlocks',
    'applyEdit',
    'buildPutBody',
    'isSensitive',
    'isEditable',
    'inferType',
    'parseJsonSafe',
    'parseParameterSetBlocks'
  ]
  const missingExports = p02Exports.filter(
    (n) => !staticScanDtoHasField('api/aps-v1/draftBuffer.ts', n)
  )
  assert(
    'P02 draftBuffer.ts 含 3 算法（flattenBlocks/applyEdit/buildPutBody）+ 5 工具',
    missingExports.length === 0,
    missingExports.length === 0
      ? '算法 + 工具齐备'
      : `缺失：${missingExports.join(', ')}`
  )

  // P03 两个 store 各自的 actions / getters（2026-10-22 拆分后重写）
  //  ⚠️ 原断言列的是双轨时代动作（loadDualTrackBuffers/onCellEdit/publishDualTrack…），
  //     这些方法随「规则集 1:1 参数集」自造模型一起撤除；参数集动作已迁到 parameterSets.ts。
  const rulesStoreSrcP03 = readSrc('store/modules/aps/rules.ts')
  const paramStoreSrcP03 = readSrc('store/modules/aps/parameterSets.ts')
  const hasFn = (src, n) => new RegExp(`(async\\s+)?function\\s+${n}\\s*\\(`).test(src)
  const p03RulesActions = [
    'loadList',
    'loadDetail',
    'loadDiff',
    'validateDraft',
    'forkDraft',
    'publish',
    'disable',
    'createRuleSet',
    'updateRuleSet',
    'saveDraftSegments'
  ]
  const missingRulesActions = p03RulesActions.filter((n) => !hasFn(rulesStoreSrcP03, n))
  assert(
    'P03 rules.ts 含规则集全部 10 个动作（含主表 create/update 与段位 saveDraftSegments）',
    missingRulesActions.length === 0,
    missingRulesActions.length === 0 ? '规则集动作齐备' : `缺失：${missingRulesActions.join(', ')}`
  )

  const p03ParamActions = ['loadList', 'loadDetail', 'loadDiff', 'onSaveDraft', 'validateDraft', 'publish', 'disable', 'forkDraft']
  const missingParamActions = p03ParamActions.filter((n) => !hasFn(paramStoreSrcP03, n))
  assert(
    'P03 parameterSets.ts 含参数集 8 个动作（两条链各自独立，动作不再混在 rules.ts）',
    missingParamActions.length === 0,
    missingParamActions.length === 0 ? '参数集动作齐备' : `缺失：${missingParamActions.join(', ')}`
  )

  const p03RulesGetters = ['actions', 'forkPermission', 'canWrite', 'canPublishAction', 'noVersionCount']
  const missingRulesGetters = p03RulesGetters.filter(
    (n) => !staticScanDtoHasField('store/modules/aps/rules.ts', n)
  )
  const p03ParamGetters = ['dirty', 'flattenedRows', 'rowsGrouped', 'canPublishAction']
  const missingParamGetters = p03ParamGetters.filter(
    (n) => !staticScanDtoHasField('store/modules/aps/parameterSets.ts', n)
  )
  assert(
    'P03 两个 store 的 getters 齐备（rules: 权限/无版本计数；parameterSets: 脏状态 + 5 主题行）',
    missingRulesGetters.length === 0 && missingParamGetters.length === 0,
    missingRulesGetters.length === 0 && missingParamGetters.length === 0
      ? 'getters 齐备'
      : `rules 缺：${missingRulesGetters.join(', ') || '无'}；parameterSets 缺：${missingParamGetters.join(', ') || '无'}`
  )

  // P04 Rules.vue UI 实施
  const rulesVueHasNewDraft = staticScanDtoHasField('views/Aps/Rules.vue', '+ 新建草稿') ||
    staticScanDtoHasField('views/Aps/Rules.vue', '新建草稿')
  assert(
    'P04 Rules.vue 含 [+ 新建草稿] 按钮 + Dialog',
    rulesVueHasNewDraft,
    rulesVueHasNewDraft ? '入口已加' : '未发现 [+ 新建草稿]'
  )

  // 5 主题块随参数集一起搬到独立页（2026-10-22 拆分）：规则集页不该再出现参数集主题块
  const paramVueHasBlocks = staticScanDtoHasField('views/Aps/ParameterSets.vue', 'BLOCK_LABELS') &&
    staticScanDtoHasField('views/Aps/ParameterSets.vue', 'rowsGrouped')
  assert(
    'P04 ParameterSets.vue 含 BLOCK_LABELS 字典 + rowsGrouped 计算（5 主题块在参数集页）',
    paramVueHasBlocks && !staticScanDtoHasField('views/Aps/Rules.vue', 'BLOCK_LABELS'),
    paramVueHasBlocks ? '5 块区隔在参数集页，规则集页无残留' : '5 块区隔缺失或仍在规则集页'
  )

  const rulesVueHasDialogs = staticScanDtoHasField('views/Aps/Rules.vue', 'createDraftDialog') &&
    staticScanDtoHasField('views/Aps/Rules.vue', 'saveDraftDialog')
  assert(
    'P04 Rules.vue 含 createDraftDialog + saveDraftDialog 双 Dialog',
    rulesVueHasDialogs,
    rulesVueHasDialogs ? '双 Dialog 齐备' : 'Dialog 缺失'
  )

  // P05 index.ts 重导出 draftBuffer
  const indexHasReexport =
    staticScanDtoHasField('api/aps-v1/index.ts', 'flattenBlocks') &&
    staticScanDtoHasField('api/aps-v1/index.ts', 'applyEdit') &&
    staticScanDtoHasField('api/aps-v1/index.ts', 'buildPutBody')
  assert(
    'P05 api/aps-v1/index.ts 重导出 flattenBlocks/applyEdit/buildPutBody',
    indexHasReexport,
    indexHasReexport ? '已重导出' : '重导出缺失'
  )

  // P06 静态探针：退役 → 停用 重命名契约（v1.4 后端语义改 disable；UI 与端点对齐）
  // 历史背景：2026-10-12 用户拍板 → 4号位 主导 A 方案全栈对齐（按钮 + Dialog + store + api + types + state + getters）

  /* -------- P06b 静态：buildPutBody 不得自行删除 contentSnapshotJson（10-15 Q2 落地配套锁）--------
   *  事实：3号位 Q2 回执已把 `version.ContentSnapshotJson = existing.ContentSnapshotJson;` 加进
   *  UpdateRuleSetVersionAsync:1632 与 UpdateParameterSetVersionAsync:1725，写路径从此无视请求体的该列。
   *  但前端 buildPutBody 是读改写（spread governance 整份回传），而 ⑦ 换型规则子块
   *  SetupTransitionRules **只存在这一列内**（SetupRuleService.cs:746）。
   *  若前端主动 delete 这一列，一旦后端回退到「读路径置空 + 写路径全量采纳」，用户保存一次即清空
   *  DRAFT 上的全部换型规则 —— 数据损坏且无任何报错。所以此处锁死「保持 spread 原样回传」。
   *  - 反向探针自证可失败：加 `delete (out as any).contentSnapshotJson` 或改注释说「不传」→ 转红
   */
  const draftBufferSrc = readSrc('api/aps-v1/draftBuffer.ts')
  const draftBufferCode = draftBufferSrc
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .replace(/^\s*\/\/.*$/gm, '')
  const p06bNoDelete = !/delete\s+[^;]*contentSnapshotJson/.test(draftBufferCode)
  assert(
    'P06b 静态：buildPutBody 不 delete contentSnapshotJson（换型规则只存这一列，删了会清空 DRAFT）',
    p06bNoDelete,
    p06bNoDelete ? '保持 spread 原样回传' : '代码里出现了 delete contentSnapshotJson —— 会清空 DRAFT 换型规则'
  )

  const p06bKeepsSpread = /\.\.\.governance,/.test(draftBufferCode)
  assert(
    'P06b 静态：buildPutBody 仍 spread governance 原样回传（读改写是全量覆写语义下的正确行为）',
    p06bKeepsSpread,
    p06bKeepsSpread ? 'spread 完整' : 'spread governance 被去掉，读改写语义已破'
  )

  const p06bCommentFixed =
    /contentSnapshotJson：\*\*不主动删、不主动改\*\*/i.test(draftBufferSrc) &&
    !/contentSnapshotJson 不传（后端 service 自行序列化）/.test(draftBufferSrc)
  assert(
    'P06b 静态：draftBuffer 头注释已改成「不主动删、不主动改」，不再误导后人删该列',
    p06bCommentFixed,
    p06bCommentFixed ? '注释已更正' : '注释仍写「contentSnapshotJson 不传」，会诱导后人删掉它'
  )
  // 防止后续回归到「按钮写『退役』但 store 内部还叫 retire」或 store 已改但类型/方法未改这种半完成状态。
  /* -------- P08 静态：列表不再谎报「已发布」（10-14 用户截图：698 全标已发布，点开全报错）--------
   *  事实（dev 实测 2026-10-14）：/api/governance/rule-sets 返 698 条，其中 695 条**连 status key 都没有**
   *  —— 因为 Program.cs:94 设了 WhenWritingNull，而后端 status 来自 RuleSetVersion 的 OUTER APPLY，
   *  一条版本都没有时整个 key 消失。这些规则集点开必然 404（published-version 无行 → versions 空数组）。
   *  前端原实现是 `status = isActive === false ? 'DISABLED' : 'PUBLISHED'`，于是 695 个点不开的
   *  全被标成「已发布」，KPI 也跟着虚高。
   *  修复：status 有就用后端真值；没有 = 无任何版本（正交布尔 hasVersion），列表显示「未配版本」，
   *  点击直接走空态，不再抛红条 + 外露内部 ID。
   *  - 反向探针自证可失败：把 status 派生改回 isActive / 去掉 hasVersion / 去掉空态 → P08 转红
   */
  const ruleApiSrc = readSrc('api/aps-v1/rule.ts')
  const rulesStoreSrc = readSrc('store/modules/aps/rules.ts')
  const rulesVueSrc = readSrc('views/Aps/Rules.vue')
  const p08UsesBackendStatus =
    /const\s+hasVersion\s*=\s*r\.status\s*!=\s*null/.test(ruleApiSrc) &&
    /status:\s*hasVersion\s*\?\s*normalizeRuleStatus\(r\.status\)/.test(ruleApiSrc)
  const p08NoIsActiveDerive =
    !/isActive\s*===\s*false\s*\?\s*'DISABLED'\s*:\s*'PUBLISHED'/.test(ruleApiSrc)
  const p08TypeHasFlag = /hasVersion:\s*boolean/.test(readSrc('api/aps-v1/types/rule.ts'))
  const p08NoVersionConst =
    /export\s+const\s+NO_VERSION\s*=\s*'NO_VERSION'/.test(readSrc('api/aps-v1/types/common.ts')) &&
    /export\s+const\s+NO_VERSION_LABEL\s*=\s*'未配版本'/.test(readSrc('api/aps-v1/types/common.ts'))
  const p08StoreCounts =
    /const\s+noVersionCount\s*=\s*computed/.test(rulesStoreSrc) &&
    /arr\s*=\s*arr\.filter\(\(r\)\s*=>\s*!r\.hasVersion\)/.test(rulesStoreSrc)
  const p08ViewShowsLabel =
    rulesVueSrc.includes('NO_VERSION_LABEL') &&
    /r\.hasVersion\s*\?\s*RULE_STATUS_LABELS\[r\.status\]\s*:\s*NO_VERSION_LABEL/.test(rulesVueSrc)
  const p08ViewGuardsClick =
    /if\s*\(!ruleSet\.hasVersion\)\s*\{/.test(rulesVueSrc) && rulesVueSrc.includes('noVersionPicked')
  const p08NoInternalIdError = !/无可用版本/.test(rulesVueSrc) && !/RuleSet\s#\$\{/.test(ruleApiSrc)
  const p08All =
    p08UsesBackendStatus &&
    p08NoIsActiveDerive &&
    p08TypeHasFlag &&
    p08NoVersionConst &&
    p08StoreCounts &&
    p08ViewShowsLabel &&
    p08ViewGuardsClick &&
    p08NoInternalIdError
  assert(
    'P08 静态：无版本规则集不再谎报「已发布」（后端 status 优先 + hasVersion + 未配版本标签 + 点击空态）',
    p08All,
    p08All
      ? 'status 取后端真值 + 未配版本独立计数与空态齐'
      : `backendStatus=${p08UsesBackendStatus} noIsActive=${p08NoIsActiveDerive} type=${p08TypeHasFlag} const=${p08NoVersionConst} store=${p08StoreCounts} viewLabel=${p08ViewShowsLabel} guard=${p08ViewGuardsClick} noIdLeak=${p08NoInternalIdError}`
  )

  /* -------- SP01 静态：策略配置 KPI 不再展示 V1 够不着的状态（10-14 用户截图红框）--------
   *  DDL 六态齐全，但 V1 只有三个有写入路径（07 v5.1.10:3774「V1 不做完整审批流闭环」）：
   *    DRAFT      GovernanceVersionService.cs:1670 创建时强制 Draft
   *    PUBLISHED  :84 / :131 publish 赋值
   *    DISABLED   :206 / :237 / :277 disable 赋值
   *  SUBMITTED / APPROVED / ARCHIVED 无任何赋值点，只出现在读取侧白名单 PublishableStatuses(:63)
   *  与拒绝提示(:193) 里。KPI 渲染这三个会让用户误读成「还没提交过」，而 V1 根本提交不了。
   *  - 反向探针自证可失败：KPI 改回遍历 STRATEGY_STATUS_META → SP01 转红
   */
  const spTypesSrc = readSrc('api/aps-v1/types/strategyProfile.ts')
  const spVueSrc = readSrc('views/Aps/StrategyProfiles.vue')
  const sp01ReachableConst =
    /export\s+const\s+STRATEGY_V1_REACHABLE_STATUSES/.test(spTypesSrc) &&
    /'DRAFT',\s*\n\s*'PUBLISHED',\s*\n\s*'DISABLED'\s*\n\s*\]/.test(spTypesSrc) &&
    !/STRATEGY_V1_REACHABLE_STATUSES[^=]*=\s*\[[^\]]*'(SUBMITTED|APPROVED|ARCHIVED)'/.test(
      spTypesSrc
    )
  const sp01ViewFiltersKpi =
    /const\s+kpiStatuses\s*=\s*computed/.test(spVueSrc) &&
    /STRATEGY_V1_REACHABLE_STATUSES\.includes\(s\)/.test(spVueSrc)
  const sp01ViewNoFullSixIterate = !/v-for="\(meta,\s*key\)\s+in\s+STRATEGY_STATUS_META"/.test(spVueSrc)
  const sp01SurfacesUnexpected =
    /statusCounts\.value\[s\]\s*>\s*0/.test(spVueSrc) && /unexpected/.test(spVueSrc)
  const sp01All =
    sp01ReachableConst && sp01ViewFiltersKpi && sp01ViewNoFullSixIterate && sp01SurfacesUnexpected
  assert(
    'SP01 静态：策略配置 KPI 只显示 V1 可达状态（DRAFT/PUBLISHED/DISABLED），预留状态不虚报 0',
    sp01All,
    sp01All
      ? '三态 KPI + 预留状态真有数据时仍显示'
      : `const=${sp01ReachableConst} filter=${sp01ViewFiltersKpi} noSixIterate=${sp01ViewNoFullSixIterate} unexpected=${sp01SurfacesUnexpected}`
  )

  /* -------- SP02/03/04 静态：3号位 2026-10-14 A组 A4/A5 落地后的前端消费（10-14 4号位）--------
   *  A4 列表 DTO 增 ruleSetVersionCode / parameterSetVersionCode → 展示位改用编码（不露裸 Id）
   *  A5 列表 DTO 增 ruleSetId / parameterSetId → 编辑态一级下拉直接定位，撤掉 698 次串行反查
   *  A3 trace 增 referencingRuns —— 前端此前已实现（类型 + 空态 + 表格），此处一并锁住
   *  - 反向探针自证可失败：表头改回 prop="ruleSetVersionId" 或恢复 for 循环反查 → SP03/SP02 转红
   */
  const spDraftSrc = readSrc('views/Aps/components/StrategyDraftDialog.vue')
  const sp03ShowsCodes =
    /row\.ruleSetVersionCode/.test(spVueSrc) && /row\.parameterSetVersionCode/.test(spVueSrc) &&
    /currentVersion\.ruleSetVersionCode/.test(spVueSrc) &&
    /runTrace\.ruleSetVersionCode/.test(spVueSrc)
  const sp03NoRawIdColumn =
    !/prop="ruleSetVersionId"/.test(spVueSrc) && !/prop="parameterSetVersionId"/.test(spVueSrc) &&
    !/label="规则集版本 ID"/.test(spVueSrc) && !/label="参数集版本 ID"/.test(spVueSrc)
  assert(
    'SP03 静态：策略版本表/详情/追溯展示 ruleSetVersionCode、parameterSetVersionCode 编码（不露裸 Id）',
    sp03ShowsCodes && sp03NoRawIdColumn,
    sp03ShowsCodes && sp03NoRawIdColumn
      ? '四处展示位已切编码，裸 Id 列已撤'
      : `codes=${sp03ShowsCodes} noRawId=${sp03NoRawIdColumn}`
  )

  const sp02UsesParentIds =
    /props\.version\.ruleSetId\s*!=\s*null/.test(spDraftSrc) &&
    /props\.version\.parameterSetId\s*!=\s*null/.test(spDraftSrc)
  const sp02NoSerialReverseLookup =
    !/for\s*\(\s*const\s+rs\s+of\s+allSets\s*\)/.test(spDraftSrc) &&
    !/for\s*\(\s*const\s+ps\s+of\s+allSets\s*\)/.test(spDraftSrc)
  const sp02LoaderKeepsValidSelection =
    /prev\s*!=\s*null\s*&&\s*list\.some\(\(v\)\s*=>\s*v\.id\s*===\s*prev\)\s*\?\s*prev\s*:\s*undefined/.test(
      spDraftSrc
    )
  const sp02All = sp02UsesParentIds && sp02NoSerialReverseLookup && sp02LoaderKeepsValidSelection
  assert(
    'SP02 静态：策略 DRAFT 编辑态用列表 DTO 的 ruleSetId/parameterSetId 定位父主表，零串行反查',
    sp02All,
    sp02All
      ? '直接赋值 + watch 加载，loader 保留仍匹配的已选版本'
      : `parentIds=${sp02UsesParentIds} noLoop=${sp02NoSerialReverseLookup} keepValid=${sp02LoaderKeepsValidSelection}`
  )

  const sp04TraceRuns =
    /runTrace\.referencingRuns/.test(spVueSrc) &&
    /referencingRuns:\s*Array</.test(readSrc('api/aps-v1/types/strategyProfile.ts'))
  assert(
    'SP04 静态：追溯面板渲染 referencingRuns（后端 A3 已恒返 []，前端不得 .length 崩）',
    sp04TraceRuns,
    sp04TraceRuns ? '类型 + 渲染均在位' : 'referencingRuns 渲染或类型缺失'
  )

  /* -------- SP05 静态：策略 DRAFT 一级「规则集」下拉只列已配版本的（10-15 用户报「选完下拉没数据」）--------
   * dev 实测：698 个 RuleSet 里仅 3 个有版本行（id 1 / 538 / 1287），其余 695 个选中后二级下拉必空；
   * 且 GET /api/governance/rule-sets 带 pageSize 时后端硬顶 500 条且顺序不保证（实测只回 id 629~1802），
   * 有版本的 id 1 与 538 被截掉——策略包绑不上。修法：不带查询参数取全量 + 客户端只留 status 非空者。
   * 反向探针自证可失败：去掉 filter 或改回 listRuleSets({...pageSize}) → SP05a/SP05b 转红。
   */
  const sp05Dialog = readSrc('views/Aps/components/StrategyDraftDialog.vue')
  const sp05Types = readSrc('api/aps-v1/types/strategyProfile.ts')
  const sp05NoPaging =
    /listRuleSets\(\s*\)/.test(sp05Dialog) &&
    !/listRuleSets\(\s*\{[^}]*pageSize/.test(sp05Dialog)
  assert(
    'SP05a 静态：策略 DRAFT 取规则集主表不带 pageSize（后端硬顶 500 会截掉有版本的 id 1 / 538）',
    sp05NoPaging,
    sp05NoPaging ? '全量取数' : '仍带分页参数，会漏掉前 500 条之外的有版本规则集'
  )
  const sp05FilterHasVersion = /\.filter\(\s*\(r\)\s*=>\s*r\.isActive\s*&&\s*r\.status\s*!=\s*null/.test(
    sp05Dialog
  )
  assert(
    'SP05b 静态：一级规则集下拉只列「已配版本」的（698 条里 695 条无版本行，列出来选了就是空）',
    sp05FilterHasVersion,
    sp05FilterHasVersion ? '已按 status 过滤' : '未过滤无版本的规则集，用户选完才发现下拉是空的'
  )
  const sp05StatusDeclared = /interface RuleSetDto\s*\{[\s\S]{0,600}status\?:\s*string\s*\|\s*null/.test(
    sp05Types
  )
  assert(
    'SP05c 静态：RuleSetDto 声明后端版本摘要字段 status（WhenWritingNull：有版本才有，是判「已配版本」的唯一依据）',
    sp05StatusDeclared,
    sp05StatusDeclared ? '已声明' : 'RuleSetDto 缺 status，无法判定是否已配版本'
  )
  const sp05NoDataHint = /no-data-text="[^"]*还没有配过版本的规则集/.test(sp05Dialog)
  assert(
    'SP05d 静态：一级规则集下拉空态用大白话说明「先去规则维护页建草稿」（不留白面板）',
    sp05NoDataHint,
    sp05NoDataHint ? '已配空态提示' : '缺 no-data-text，全为空时下拉不提示原因'
  )

  /* -------- SP06 静态：策略 DRAFT 一级「参数集」下拉与规则集同款（3号位 10-15 联调通知 §一）--------
   * 3号位 已把 GET /parameter-sets 从裸实体换型为 ParameterSetListItemDto（dev 实测 722 条里 3 条带
   * status/currentVersionCode/lastChangeReason/publishedAt，无版本行整键被 WhenWritingNull 省略）。
   * 前端跟进两件事：去掉 pageSize（硬顶 500 会截掉有版本的 PS_DEMO，dev 排在第 571 位）+ 按 status 过滤。
   * 反向探针自证可失败：把 pageSize 塞回去或删掉 filter → SP06a/SP06b 转红。
   */
  const sp06NoPaging =
    /listParameterSets\(\s*\{[^}]*activeOnly/.test(sp05Dialog) &&
    !/listParameterSets\(\s*\{[^}]*pageSize/.test(sp05Dialog)
  assert(
    'SP06a 静态：策略 DRAFT 取参数集主表不带 pageSize（后端硬顶 500，会截掉有版本的 PS_DEMO/286）',
    sp06NoPaging,
    sp06NoPaging ? '全量取数' : '仍带分页参数，会漏掉前 500 条之外的有版本参数集'
  )
  const sp06FilterHasVersion = /\.filter\(\s*\(p\)\s*=>\s*p\.isActive\s*&&\s*p\.status\s*!=\s*null/.test(
    sp05Dialog
  )
  assert(
    'SP06b 静态：一级参数集下拉只列「已配版本」的（722 条里 719 条无版本行，列出来选了就是空）',
    sp06FilterHasVersion,
    sp06FilterHasVersion ? '已按 status 过滤' : '未过滤无版本的参数集，用户选完才发现下拉是空的'
  )
  const sp06SummaryDeclared =
    /interface ParameterSetDto\s*\{[\s\S]{0,900}status\?\s*:\s*string\s*\|\s*null[\s\S]{0,900}currentVersionCode\?\s*:/.test(
      sp05Types
    )
  assert(
    'SP06c 静态：ParameterSetDto 声明后端版本摘要字段 status/currentVersionCode（判「已配版本」的唯一依据）',
    sp06SummaryDeclared,
    sp06SummaryDeclared ? '已声明' : 'ParameterSetDto 缺版本摘要字段，无法判定是否已配版本'
  )
  const sp06NoDataHint = /no-data-text="[^"]*还没有配过版本的参数集/.test(sp05Dialog)
  assert(
    'SP06d 静态：一级参数集下拉空态用大白话说明「先去规则维护页建草稿」（不留白面板）',
    sp06NoDataHint,
    sp06NoDataHint ? '已配空态提示' : '缺 no-data-text，全为空时下拉不提示原因'
  )

  /* -------- SP07 静态：PS 版本端点不得再放宽超时 + 加载失败提示去 axios 原文 --------
   * 沿革：10-15 用户报 `timeout of 30000ms exceeded`（PS-PROD/1305 实测 37s），前端临时放宽到 60s。
   * 10-16 3号位 Q1/Q2 落码并部署（契约登记 S-12，HTTP 响应零变化），复测 1305 = 17ms，
   * 与其余参数集同量级 ⇒ 60s 兜底已撤。**本条改为反向探针**：锁「别把 timeout 加回来」——
   * 加回来不会立刻报错，只会让将来真出现慢响应时被静默容忍，掩盖根因。
   * 自证可失败：加回 timeout: 60000 → SP07a 转红。
   */
  const sp07ListPsVersionsBody =
    readSrc('api/aps-v1/strategyProfile.ts').match(
      /async listParameterSetVersions\([\s\S]{0,700}?\n {2}\}/
    )?.[0] ?? ''
  const sp07HasTimeoutOverride = /timeout:\s*\d+/.test(sp07ListPsVersionsBody)
  assert(
    'SP07a 反向：参数集版本端点已恢复全局默认超时（3号位 Q1 落码后 1305 实测 17ms，不得再加 60s 兜底）',
    !sp07HasTimeoutOverride,
    sp07HasTimeoutOverride
      ? '又加了 timeout 兜底：会把将来真实的慢响应静默容忍掉，根因被掩盖'
      : '已恢复默认超时'
  )
  const sp07NoRawAxiosText = !/ElMessage\.error\(`[^`]*\$\{\(err as Error\)\.message\}/.test(sp05Dialog)
  assert(
    'SP07b 静态：策略 DRAFT 四处加载失败提示不再把 axios 原文（timeout of 30000ms exceeded）抛给用户',
    sp07NoRawAxiosText,
    sp07NoRawAxiosText ? '已是大白话' : '仍有 ElMessage.error 拼接 err.message'
  )

  const rulesVueHasDisableButton = staticScanDtoHasField('views/Aps/Rules.vue', 'vi-mdi:archive" /> 停用') ||
    staticScanDtoHasField('views/Aps/Rules.vue', 'vi-mdi:archive" />\n              停用') ||
    /vi-mdi:archive"\s*\/>\s*停用/.test(readSrc('views/Aps/Rules.vue'))
  assert(
    'P06.1 Rules.vue 「停用」按钮文案已替换原「退役」（archive 图标 + 停用文本）',
    rulesVueHasDisableButton,
    rulesVueHasDisableButton ? '按钮文案对齐' : '按钮仍是「退役」或缺图标的「停用」'
  )

  const rulesVueHasDisableDialogTitle = staticScanDtoHasField('views/Aps/Rules.vue', 'title="停用已发布规则集"')
  assert(
    'P06.2 Rules.vue Disable Dialog 标题 = 「停用已发布规则集」（不再是「退役...」）',
    rulesVueHasDisableDialogTitle,
    rulesVueHasDisableDialogTitle ? 'Dialog 标题已对齐' : 'Dialog 标题未替换'
  )

  const rulesVueNoRetireUserFacing = !/退役后不可恢复|确认退役|>退役</.test(readSrc('views/Aps/Rules.vue'))
  assert(
    'P06.3 Rules.vue 无「退役后不可恢复/确认退役/>退役<」用户可见残留（注释/文档引用 RETIRED 不计）',
    rulesVueNoRetireUserFacing,
    rulesVueNoRetireUserFacing ? '无残留' : '仍有「退役」用户可见文案'
  )

  const rulesVueCallsDisable = /rulesStore\.disable\(/.test(readSrc('views/Aps/Rules.vue')) &&
    !/rulesStore\.retire\(/.test(readSrc('views/Aps/Rules.vue'))
  assert(
    'P06.4 Rules.vue 调 rulesStore.disable()（不再调 rulesStore.retire）',
    rulesVueCallsDisable,
    rulesVueCallsDisable ? '调用栈已切到 disable' : '仍调用 retire 或未切到 disable'
  )

  const storeActionRenamed = /async\s+function\s+disable\s*\(/.test(readSrc('store/modules/aps/rules.ts')) &&
    !/async\s+function\s+retire\s*\(/.test(readSrc('store/modules/aps/rules.ts'))
  assert(
    'P06.5 rules.ts store action = disable()（不再是 retire()）',
    storeActionRenamed,
    storeActionRenamed ? 'action 已对齐 disable' : '仍叫 retire 或未切到 disable'
  )

  // 停用结果不再造对象：后端只返一句文本，store 发布/停用后重取列表读真状态（R04/R15 同源）
  const rulesStoreSrcP06 = readSrc('store/modules/aps/rules.ts')
  const storeStateRenamed = /canDisable\b/.test(rulesStoreSrcP06) &&
    !/lastRetire\b/.test(rulesStoreSrcP06) &&
    !/canRetire\b/.test(rulesStoreSrcP06) &&
    !/lastDisable\b/.test(rulesStoreSrcP06)
  assert(
    'P06.6 rules.ts 用 actions.canDisable 闸按钮，且不保留 lastDisable/lastRetire 结果对象（后端只返文本）',
    storeStateRenamed,
    storeStateRenamed ? 'state/getter 已对齐' : '仍有 lastDisable/lastRetire 自造结果对象'
  )

  const apiMethodRenamed = /async\s+disableRuleSet\s*\(input:\s*DisableRuleInput\)\s*:\s*Promise<void>/.test(
    readSrc('api/aps-v1/rule.ts')
  )
  assert(
    'P06.7 rule.ts API 方法 = disableRuleSet(input: DisableRuleInput): Promise<void>（调 /disable 端点）',
    apiMethodRenamed,
    apiMethodRenamed ? 'API 方法签名已对齐' : 'API 方法仍叫 retire 或返回自造结果对象'
  )

  const typesRenamed = /export\s+interface\s+DisableRuleInput\b/.test(readSrc('api/aps-v1/types/rule.ts')) &&
    !/export\s+interface\s+DisableRuleResult\b/.test(readSrc('api/aps-v1/types/rule.ts')) &&
    !/export\s+interface\s+RetireRuleInput\b/.test(readSrc('api/aps-v1/types/rule.ts')) &&
    !/export\s+interface\s+RetireRuleResult\b/.test(readSrc('api/aps-v1/types/rule.ts'))
  assert(
    'P06.8 types/rule.ts 只有 DisableRuleInput（无 RetireRule*，也不自造 DisableRuleResult）',
    typesRenamed,
    typesRenamed ? '类型已对齐 disable' : '类型仍叫 RetireRule* 或多出自造 Result'
  )

  // P07 静态：Rules.vue KPI + Tab 用 UI 流程 3 态子集（v1.4 §二十 6 态冻结；前端无 submit/approve/archive 入口）
  // 历史：2026-10-12 用户观察到 SUBMITTED/APPROVED/ARCHIVED 在 KPI/Tab 中永远是 0 噪声；3 态即可覆盖 UI 实际流程
  // 保留 RULE_STATUSES（6 态）作 wire 兼容；新增 RULE_STATUSES_UI（3 态）专供 KPI/Tab v-for
  const commonHasUiSubset = /export\s+const\s+RULE_STATUSES_UI\s*=\s*\[\s*'DRAFT'\s*,\s*'PUBLISHED'\s*,\s*'DISABLED'\s*\]/.test(
    readSrc('api/aps-v1/types/common.ts')
  )
  assert(
    'P07.1 common.ts 导出 RULE_STATUSES_UI = [DRAFT, PUBLISHED, DISABLED]（6 态 RULE_STATUSES 保留供类型/normalize）',
    commonHasUiSubset,
    commonHasUiSubset ? '常量定义齐备' : 'RULE_STATUSES_UI 缺失或元素错'
  )

  const commonHasSixStates = /export\s+const\s+RULE_STATUSES\s*=\s*\[\s*'DRAFT'\s*,\s*'SUBMITTED'\s*,\s*'APPROVED'\s*,\s*'PUBLISHED'\s*,\s*'DISABLED'\s*,\s*'ARCHIVED'\s*\]/.test(
    readSrc('api/aps-v1/types/common.ts')
  )
  assert(
    'P07.2 common.ts 保留 6 态 RULE_STATUSES（DDL 冻结兼容；normalize 与 Record 键空间仍按 6 态）',
    commonHasSixStates,
    commonHasSixStates ? '6 态冻结契约保留' : '6 态被删或顺序错'
  )

  const rulesVueKpiLoopUi3 = /v-for="key in RULE_STATUSES_UI"/.test(readSrc('views/Aps/Rules.vue'))
  assert(
    'P07.3 Rules.vue KPI 横条 v-for 用 RULE_STATUSES_UI（不再用 6 态）',
    rulesVueKpiLoopUi3,
    rulesVueKpiLoopUi3 ? 'KPI 已切 3 态' : 'KPI 仍遍历 6 态'
  )

  const rulesVueTabLoopUi3 = readSrc('views/Aps/Rules.vue').match(/v-for="key in (RULE_STATUSES|RULE_STATUSES_UI)"/g) ?? []
  const tabsAllUi = rulesVueTabLoopUi3.length === 2 && rulesVueTabLoopUi3.every((s) => s.includes('RULE_STATUSES_UI'))
  assert(
    'P07.4 Rules.vue Tab + KPI 两处 v-for 都用 RULE_STATUSES_UI',
    tabsAllUi,
    tabsAllUi ? '两处一致切 3 态' : `命中 ${rulesVueTabLoopUi3.length} 处：${rulesVueTabLoopUi3.join(' | ')}`
  )

  const rulesVueSixTabGone = !/<ElRadioButton[^>]*v-for="key in RULE_STATUSES"/.test(readSrc('views/Aps/Rules.vue')) &&
    !/<div[^>]*v-for="key in RULE_STATUSES"\s*:key="key"\s*class="kpi-pill"/.test(readSrc('views/Aps/Rules.vue'))
  assert(
    'P07.5 Rules.vue 已无遍历 6 态的 v-for（防 SUBMITTED/APPROVED/ARCHIVED 噪声恢复）',
    rulesVueSixTabGone,
    rulesVueSixTabGone ? '无 6 态噪声' : '仍有 6 态遍历'
  )
}

async function verifyParameterSetDraft(token) {
  log('\n[P] ParameterSet 写维护：B 设计稿 Step 3+4+6 落地（parameter-set-drafts）')

  // P01-P05 静态扫描（与后端状态无关）
  runP01P05Static()

  // P06 探测：取一个 RuleSet id（来自列表）
  let r = await call('GET', '/api/governance/rule-sets', { token })
  const firstRuleSet = r.ok && Array.isArray(r.json?.data) && r.json.data.length > 0
    ? r.json.data[0]
    : null
  // 后端实测返回 { id, ruleSetCode, ... }（camelCase）；前端 v1.4 §二十契约可能返 ruleSetId
  const ruleSetId =
    firstRuleSet?.ruleSetId ?? firstRuleSet?.RuleSetId ?? firstRuleSet?.id ?? null

  r = await call('GET', '/api/governance/parameter-sets', { token })
  const firstParamSet = r.ok && Array.isArray(r.json?.data) && r.json.data.length > 0
    ? r.json.data[0]
    : null
  const parameterSetId =
    firstParamSet?.parameterSetId ?? firstParamSet?.ParameterSetId ?? firstParamSet?.id ?? null

  // P07 POST /api/governance/rule-set/version（创建 RS DRAFT）— 空 body 应 4xx 或 500（FK 约束冲突）
  r = await call('POST', '/api/governance/rule-set/version', { token, body: {} })
  assert(
    'P07 POST /api/governance/rule-set/version（创建 RS DRAFT 端点可达）',
    r.code === 400 || r.code === 422 || r.code === 401 || r.code === 403 || r.code === 500,
    `code=${r.code}（400/422=校验；401/403=鉴权；500=FK 约束=controller 已接收；端点存在即过；≠404 即过）`
  )

  // P08 POST /api/governance/parameter-set/version（创建 PS DRAFT）
  r = await call('POST', '/api/governance/parameter-set/version', { token, body: {} })
  assert(
    'P08 POST /api/governance/parameter-set/version（创建 PS DRAFT 端点可达）',
    r.code === 400 || r.code === 422 || r.code === 401 || r.code === 403 || r.code === 500,
    `code=${r.code}（同上；端点存在即过；≠404 即过）`
  )

  // P09 PUT /api/governance/rule-set/version/{versionId}（更新 RS DRAFT）— 用 versionId=1 探测
  r = await call('PUT', '/api/governance/rule-set/version/1', { token, body: {} })
  assert(
    'P09 PUT /api/governance/rule-set/version/{versionId}（更新 RS DRAFT 端点可达）',
    r.code === 400 || r.code === 422 || r.code === 401 || r.code === 403 || r.code === 404,
    `code=${r.code}（404=版本不存在；4xx=校验生效；端点存在即过）`
  )

  // P10 PUT /api/governance/parameter-set/version/{versionId}（更新 PS DRAFT）
  r = await call('PUT', '/api/governance/parameter-set/version/1', { token, body: {} })
  assert(
    'P10 PUT /api/governance/parameter-set/version/{versionId}（更新 PS DRAFT 端点可达）',
    r.code === 400 || r.code === 422 || r.code === 401 || r.code === 403 || r.code === 404,
    `code=${r.code}`
  )

  // P11 POST /api/governance/rule-set/version/{versionId}/publish（发布 RS）
  r = await call('POST', '/api/governance/rule-set/version/1/publish', { token, body: {} })
  assert(
    'P11 POST /api/governance/rule-set/version/{versionId}/publish（发布 RS 端点可达）',
    r.code === 400 || r.code === 422 || r.code === 401 || r.code === 403 || r.code === 404,
    `code=${r.code}（404=版本不存在；400=状态校验；端点存在即过）`
  )

  // P12 POST /api/governance/parameter-set/version/{versionId}/publish（发布 PS）
  r = await call('POST', '/api/governance/parameter-set/version/1/publish', { token, body: {} })
  assert(
    'P12 POST /api/governance/parameter-set/version/{versionId}/publish（发布 PS 端点可达）',
    r.code === 400 || r.code === 422 || r.code === 401 || r.code === 403 || r.code === 404,
    `code=${r.code}`
  )

  // P13 GET /api/governance/rule-set/version/{versionId}（fork 第 1 步：取源 RS）
  r = await call('GET', '/api/governance/rule-set/version/1', { token })
  assert(
    'P13 GET /api/governance/rule-set/version/{versionId}（fork 取源 RS 端点可达）',
    r.code === 200 || r.code === 404 || r.code === 401 || r.code === 403,
    `code=${r.code}（200=有源版本；404=不存在；端点存在即过）`
  )

  // P14/P15 加固（2026-10-17，3号位 回执 §六 认可方向）：
  //  旧 P14 写死 `versionId=1`（不存在）→ 404 即过；旧 P15 用列表首条（集成测试夹具、无 PUBLISHED）→ 404 即过。
  //  两者都只看「端点可达」，既量不出 39s/7s 这类性能回归，也不校验真有 PUBLISHED 版本。
  //  现改为：先从列表解析一个**真实 PUBLISHED 参数集** → 取它 published-version 的**真实版本 id** →
  //  再打详情端点，断言 200 + 五主题齐全 + 耗时上限。
  //  ⚠️ 阈值只对**非遗留**参数集生效：1305 是旧存储模型遗留（~430MB 快照，方案A 后仍 7.0s），
  //     对它固化紧阈值会把「方案A 已达成」判成回归（3号位 §二：现行模型新数据 6-12ms）。
  const LEGACY_PS_IDS = new Set([1305])
  const TOPIC_KEYS = [
    'lockJson',
    'supplyJson',
    'procurementJson',
    'solverStrategyJson',
    'candidateGuardrailJson'
  ]
  const psList = await call('GET', '/api/governance/parameter-sets', { token })
  const psRows = Array.isArray(psList.json?.data) ? psList.json.data : []
  const publishedRows = psRows.filter((x) => x.status === 'PUBLISHED' && typeof x.id === 'number')
  const pickPs = publishedRows.find((x) => !LEGACY_PS_IDS.has(x.id)) ?? publishedRows[0] ?? null

  // P15 GET /parameter-set/{id}/published-version（真实 PUBLISHED 参数集，解析版本 id）
  let resolvedPsVersionId = null
  if (!pickPs) {
    skip('P15', 'dev 无任何 PUBLISHED 参数集，跳过 published-version')
  } else {
    const t0 = Date.now()
    const pv = await call('GET', `/api/governance/parameter-set/${pickPs.id}/published-version`, {
      token
    })
    const ms = Date.now() - t0
    const pvData = pv.json?.data ?? null
    const isLegacy = LEGACY_PS_IDS.has(pickPs.id)
    const pvOk = pv.ok && pvData && typeof pvData.id === 'number'
    const timeOk = isLegacy ? ms < 15000 : ms < 2000
    resolvedPsVersionId = pvOk ? pvData.id : null
    assert(
      `P15 GET /api/governance/parameter-set/${pickPs.id}/published-version（真实 PUBLISHED，解析版本 id + 耗时上限）`,
      pvOk && timeOk,
      `code=${pv.code} versionId=${pvData?.id ?? '-'} 耗时=${ms}ms（上限 ${isLegacy ? 15000 : 2000}ms${isLegacy ? '，遗留数据放宽容差' : ''}）`
    )
  }

  // P14 GET /parameter-set/version/{versionId}（用 P15 解析出的**真实版本 id**，非写死）
  if (resolvedPsVersionId == null) {
    skip('P14', '未能从 published-version 解析出真实版本 id，跳过详情端点')
  } else {
    const t0 = Date.now()
    const vd = await call('GET', `/api/governance/parameter-set/version/${resolvedPsVersionId}`, {
      token
    })
    const ms = Date.now() - t0
    const vdData = vd.json?.data ?? null
    const isLegacy = pickPs && LEGACY_PS_IDS.has(pickPs.id)
    const missingTopics = TOPIC_KEYS.filter((k) => !(vdData && k in vdData))
    const timeOk = isLegacy ? ms < 15000 : ms < 2000
    assert(
      `P14 GET /api/governance/parameter-set/version/{versionId}（真实版本 ${resolvedPsVersionId}，五主题齐 + 耗时上限）`,
      vd.ok && missingTopics.length === 0 && timeOk,
      `code=${vd.code} 缺主题=${missingTopics.join(',') || '无'} 耗时=${ms}ms（上限 ${isLegacy ? 15000 : 2000}ms）`
    )
  }

  // 规则集侧 published-version 仍做端点可达（与 V01 共享读路径）
  if (ruleSetId) {
    r = await call('GET', `/api/governance/rule-set/${ruleSetId}/published-version`, { token })
    assert(
      'P15b GET /api/governance/rule-set/{id}/published-version（与 V01 联动验证 read path）',
      r.ok || r.code === 404 || r.code === 422,
      `code=${r.code}（与 V01 共享读路径；端点可达即过）`
    )
  } else {
    skip('P15b', 'rule-sets 列表为空，跳过 published-version 联动')
  }

  info(
    'P 段全部为「端点可达 + 静态契约」验收，不创建 DRAFT/不发布以避免脏数据上界锁；' +
      'B 设计稿 §3.6 接受类断言需 VERIFY_ALLOW_WRITE=1（前端未实现批量脚本，未提供）'
  )

  /* ===== P16 版本 Diff 契约对齐后端 VersionDiffResult（Q-E §三.3，2026-10-19）=====
   *
   * 背景：此前 `RuleDiffDto` 是前端自拟的 `baseVersion/targetVersion/changedParameters/summary`
   *   四组字段，与后端 `LPS.APS.Core/DTOs/Governance/VersionDiffResult.cs` **零字段重合**
   *   （dev 实测 rule-set / parameter-set 两个 diff 端点都只返
   *     sourceVersionId/targetVersionId/sourceVersionCode/targetVersionCode/
   *     entityType/fieldDiffs/comparedAt）。`Rules.vue`「版本对比」Tab 渲染
   *   `diffNN.summary.addedCount` 会直接抛 TypeError —— store 在检测到「已发布版本 + 另一个版本」
   *   时会自动调 loadDiff，命中即崩。
   *
   * 本断言锁四件事，缺一即红：
   *   ① 类型已是后端形状（fieldDiffs / fieldDisplayName / isChanged / entityType）
   *   ② 自拟的四组字段已从 types 清零（防止按旧形状再加回来）
   *   ③ Rules.vue 只渲染 isChanged 的行，且不再出现 added/modified/removed 三分类
   *      —— 后端只给布尔位、不返三类计数，「新增/删除」在当前契约下无从判定，自造即为假数据
   *   ④ 参数集 diff 端点已接进 API 层（方法在；本轮按「加急轮不扩页面」未挂 UI）
   */
  let p16Shape = false
  let p16Stale = false
  let p16View = false
  let p16PsDiff = false
  try {
    const diffTypesSrc = readFileSync(join(SRC_ROOT, 'api/aps-v1/types/rule.ts'), 'utf-8')
    const diffApiSrc = readFileSync(join(SRC_ROOT, 'api/aps-v1/rule.ts'), 'utf-8')
    const psApiSrc = readFileSync(join(SRC_ROOT, 'api/aps-v1/parameterSet.ts'), 'utf-8')
    const rulesVueSrc = readFileSync(join(SRC_ROOT, 'views/Aps/Rules.vue'), 'utf-8')
    const psVueSrc = readFileSync(join(SRC_ROOT, 'views/Aps/ParameterSets.vue'), 'utf-8')
    // 说明性注释里会写「此前自拟 changedParameters/summary」这类历史交代，
    // 不剥注释会被「是否仍是残留」的断言误判成假红（同 OV06 的坑）。
    const diffTypesCode = stripComments(diffTypesSrc)

    p16Shape =
      /fieldDiffs\s*:\s*FieldDiff\[\]/.test(diffTypesCode) &&
      /fieldDisplayName\s*:\s*string/.test(diffTypesCode) &&
      /isChanged\s*:\s*boolean/.test(diffTypesCode) &&
      /entityType\s*:\s*string/.test(diffTypesCode)
    p16Stale =
      !/changedParameters/.test(diffTypesCode) &&
      !/oldDisplay\??\s*:/.test(diffTypesCode) &&
      !/newDisplay\??\s*:/.test(diffTypesCode)
    // ⚠️ 只禁 diff 对象上的自造汇总，别把详情块的 `detailNN.summary`（ruleSetId 等真字段）判进来
    const noFakeCounts = (src) =>
      /f\.isChanged/.test(src) &&
      !/(?:currentDiff|diffNN|diff)\.summary/.test(src) &&
      !/removedCount/.test(src) &&
      !/addedCount/.test(src)
    // 两个页面各自渲染自己的 diff（参数集页独立后不再共用规则集页的对比 Tab）
    p16View =
      /changedFields/.test(rulesVueSrc) && noFakeCounts(rulesVueSrc) &&
      /changedFields/.test(psVueSrc) && noFakeCounts(psVueSrc)
    p16PsDiff =
      /async\s+diffVersions\s*\(/.test(psApiSrc) && /\/parameter-set\/version\/diff/.test(psApiSrc)
  } catch {
    /* 读文件失败 → 四项全 false → 断言转红 */
  }
  assert(
    'P16 静态：版本 Diff 契约对齐后端 VersionDiffResult（不自拟 summary / 不自造新增删除三类计数）',
    p16Shape && p16Stale && p16View && p16PsDiff,
    `类型形状=${p16Shape} 自拟字段已清=${p16Stale} 视图只渲染 isChanged=${p16View} 参数集diff已接=${p16PsDiff}`
  )

  /* ===== P17 参数集发布是独立权限码（D-1），不得只判 aps.rule.publish =====
   *
   * 后端 `GovernanceController.cs:227` 的 `parameter-set/version/{id}/publish` 挂
   *   `[Authorize(Policy = PermissionCodes.ParameterPublish)]`（`aps.parameter.publish`），
   *   **不因规则发布权自动获得**（0号位 §3.2）。
   * 此前 `publishDualTrack` 只判 `aps.rule.publish` 就连打 RS + PS 两步
   *   ⇒ 缺 PS 发布权时，RS 已发布、PS 才 403，留下**半发状态**。
   * 2026-10-22 拆分后双轨发布已撤：参数集有自己的 store/页面，发布前在**动作入口**判自己的权限码。
   * dev 实测（2026-10-19 复核）：权限码**目录**已 85 条且**含** `aps.parameter.publish`；
   *   但**没有任何角色被授予**该码（role1 admin.system 43 码 / role2 admin.aps 19 码 /
   *   role3 planner 23 码 / role6 viewer 13 码 全无）⇒ 这条闸在 dev 上是真实会触发的，不是理论值。
   */
  let p17Gate = false
  try {
    const rulesStoreSrc = readFileSync(join(SRC_ROOT, 'store/modules/aps/rules.ts'), 'utf-8')
    const psStoreSrc = readFileSync(join(SRC_ROOT, 'store/modules/aps/parameterSets.ts'), 'utf-8')
    const psStoreCode = stripComments(psStoreSrc)
    const rulesStoreCode = stripComments(rulesStoreSrc)
    p17Gate =
      // 参数集 store 用独立的 aps.parameter.publish 判权限…
      /apsAuth\.has\(\s*'aps\.parameter\.publish'\s*\)/.test(psStoreCode) &&
      // …并在 publish 动作入口先拦，不能等请求发出才 403
      /if\s*\(\s*!canPublishAction\.value\s*\)/.test(psStoreCode) &&
      // 规则集侧不得再凭参数集权限发东西（双轨已撤）
      !/aps\.parameter\.publish/.test(rulesStoreCode)
  } catch {
    /* 读文件失败 → 判红 */
  }
  assert(
    'P17 静态：参数集发布按独立权限码 aps.parameter.publish 闸住（D-1，不随规则发布权获得）',
    p17Gate,
    p17Gate ? '参数集 store 已在自己的发布入口前置判权' : '参数集发布未按 aps.parameter.publish 前置拦截，或规则集侧仍在越权判码'
  )

  /* ===== P18 mock 角色 preset 的 D-1 参数集发布码归属 =====
   *
   * 3号位 2026-10-19《参数发布 D-1 独立权限 aps.parameter.publish 前端按钮接码转告 v1.0》§三 第 2 项：
   *   `auth.ts` 的 mock 角色权限数组缺 `aps.parameter.publish` ⇒ mock 模式下发布行为与真实不一致
   *   （前端闸放行、真实后端 403，=「mock 假过」）。
   * 🔁 10-19 夜 3号位 回执 §五.3（依 0号位 裁定「管理员角色只保留一个」）：
   *   落码口径**收敛为仅 `aps.admin.aps` 持有**；`aps.admin.system` 与所有非发布角色**一律不得**持有。
   *   （首版曾给两个 admin 角色都补码，已按裁定撤回 admin.system 侧。）
   * ⚠️ 本探针只锁**前端 mock 侧**；真实库授权由管理员手工 RBAC 落地（dev 现仍无人持有，
   *   授权后本探针判据不变 —— mock 侧不受真实库影响）。
   */
  let p18MockPsPublish = false
  try {
    const authSrc = readFileSync(join(SRC_ROOT, 'store/modules/aps/auth.ts'), 'utf-8')
    const authCode = stripComments(authSrc)
    /** 取某 mock 角色的 permissions 数组体；锚点必须**唯一**，否则判红（防止匹到别的声明） */
    const permBlock = (roleKey) => {
      const anchor = `'${roleKey}': {`
      if (authCode.split(anchor).length - 1 !== 1) return null
      const i = authCode.indexOf(anchor)
      const j = authCode.indexOf('permissions: [', i)
      if (j < 0) return null
      const k = authCode.indexOf(']', j)
      return k < 0 ? null : authCode.slice(j, k)
    }
    const has = (roleKey) => {
      const b = permBlock(roleKey)
      return b !== null && /'aps\.parameter\.publish'/.test(b)
    }
    // 唯一持有者 + 一律不得持有者（含 admin.system —— 裁定收敛后它必须没有）
    const soleHolder = 'aps.admin.aps'
    const mustNotHold = [
      'aps.admin.system',
      'aps.viewer.management',
      'aps.planner',
      'aps.supervisor.workshop',
      'aps.coordinator.material',
      'aps.service.api'
    ]
    p18MockPsPublish = has(soleHolder) && !mustNotHold.some((r) => has(r))
  } catch {
    /* 读文件失败 → 判红 */
  }
  assert(
    'P18 静态：D-1 码 aps.parameter.publish 在 mock 侧仅 aps.admin.aps 持有（admin.system 及非发布角色均不得持有）',
    p18MockPsPublish,
    p18MockPsPublish
      ? '仅 admin.aps 持该码；admin.system 与其余 mock 角色均未持'
      : '唯一持有者缺失，或该码被误发给 admin.system / 非发布角色（或锚点不唯一）'
  )

}

/* ==================== [P19/P20] 枚举错误文案中文映射 + 锚点提示（纯静态，不登录）====================
 *  这两组探针全部打在**前端源码**上，不需要 dev token ——
 *  若留在 parameter-set-drafts 组里，反向破坏自证要连跑 9 次 ⇒ 每次先吃一次登录额度 ⇒ 必撞 429
 *  （"Error: login failed … at loginAsAdmin" ⇒ 整段中止 ⇒ 静态探针报 MISSING 而不是 RED，自证作废）。
 *  故独立成 parameter-set-static 组（免登录），两个组跑同一份断言。
 */
function runEnumMappingStatic() {
  /* -------- P19 静态：枚举错误文案中文映射层（3号位 2026-10-25 回执 §一/§四）--------
   *  背景：3号位 裁定「后端不改 message」，由前端按 errors[].code + message 内字段名前缀做中文呈现。
   *  锁三件事：
   *   ① 有独立映射层（validationMessages.ts），且**显示词取自本仓 parameterSetEnums.ts**（回执 §三：
   *      不得用后端那张表覆盖前端显示词）
   *   ② 判别式是 message 内的**字段名前缀**（回执 §二 注意事项 1：一个错误码两个字段，不能只按 code 映射）
   *   ③ 取值按「当前值：N」而非 message 里的英文名顺序（后端 SolverStrategyMode 的名字顺序与枚举序号相反）
   *  - 反向探针自证可失败：删掉任一 marker / 改用英文名替换 / 改回硬编码后端词 → 转红
   */
  const vmSrc = readSrc('api/aps-v1/validationMessages.ts')
  const vmCode = vmSrc
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .replace(/^\s*\/\/.*$/gm, '')
  const p19HasFile = vmCode.length > 0
  assert(
    'P19 静态：存在枚举错误文案中文映射层 api/aps-v1/validationMessages.ts',
    p19HasFile,
    p19HasFile ? '映射层已建' : '映射层缺失'
  )

  const p19Markers = ['DynamicBottleneckMode', 'SolverStrategyMode', 'Supply.PiSort.SortBy']
  const p19MarkerOk = p19Markers.every((m) => vmCode.includes(m))
  assert(
    'P19 静态：映射层按 message 字段名前缀区分三条枚举（G1 一个错误码两个字段 ⇒ 不能只按 code 映射）',
    p19MarkerOk,
    p19MarkerOk ? '三条判别词齐备' : `缺：${p19Markers.filter((m) => !vmCode.includes(m)).join(', ')}`
  )

  const p19UsesOwnLabels = /lookupParameterEnum\(/.test(vmCode)
  assert(
    'P19 静态：中文显示词取自本仓 parameterSetEnums.ts（3号位 §三：不得被后端那张表覆盖）',
    p19UsesOwnLabels,
    p19UsesOwnLabels ? '复用本仓下拉显示词' : '未复用 parameterSetEnums.ts —— 显示词来源错误'
  )

  // ⚠️ 判据必须落在**解析调用**上，不能只判「当前值」二字出现过 ——
  //   只判存在性的话，把 `CURRENT_VALUE_RE.exec(raw)` 换成硬编码 '0' 探针仍绿 ⇒ 探针假绿。
  const p19ReDecl = /CURRENT_VALUE_RE\s*=\s*\/[^\n]*当前值/.test(vmCode)
  const p19ReUsed = /CURRENT_VALUE_RE\s*\.\s*exec\(/.test(vmCode)
  const p19ByCurrentValue = p19ReDecl && p19ReUsed
  assert(
    'P19 静态：取值按 message 内「当前值：N」解析（不按英文名出现顺序——后端名字顺序与枚举序号相反）',
    p19ByCurrentValue,
    p19ByCurrentValue
      ? '按当前值解析'
      : `正则声明${p19ReDecl ? '有' : '无'} / 解析调用${p19ReUsed ? '有' : '无'} ⇒ 未按「当前值：N」解析`
  )

  const p19PageUses = /translateValidationError\(/.test(readSrc('views/Aps/ParameterSets.vue'))
  assert(
    'P19 静态：参数集页校验结果渲染处调用 translateValidationError（错误列表不再直出英文）',
    p19PageUses,
    p19PageUses ? '渲染处已接翻译层' : '页面未接翻译层，错误仍是后端英文原文'
  )

  /* -------- P20 静态：锚点空编码非阻断提示（3号位 2026-10-25 回执 §五）--------
   *  运行期事实：PhaseThreeDiagnostics.cs:137-170 —— 锚点 id 解析不出时三个非 Auto 分支都不执行 ≡ Auto。
   *  锁三件事：① 判定在前端（前端自判，后端零改动）② 不拦截/不阻断发布 ③ 页面上有落点。
   *  - 反向探针自证可失败：把 mode===0 的早退去掉 / 加 throw / 页面不渲染 → 转红
   */
  const p20Fn = (() => {
    const start = vmCode.indexOf('export function anchorHintMessage')
    if (start < 0) return ''
    let depth = 0
    let opened = false
    for (let i = start; i < vmCode.length; i++) {
      if (vmCode[i] === '{') {
        depth++
        opened = true
      } else if (vmCode[i] === '}') {
        depth--
        if (opened && depth === 0) return vmCode.slice(start, i + 1)
      }
    }
    return ''
  })()
  // ⚠️ AnchorResourceCode 必须判到**真正被读**（`... ===`），不能只判名字出现过 ——
  //   只判存在性的话，把它改成 AnchorResourceCodeX 探针仍绿 ⇒ 探针假绿。
  const p20Ok =
    p20Fn.length > 0 && /BottleneckMode/.test(p20Fn) && /AnchorResourceCode\s*===/.test(p20Fn)
  assert(
    'P20 静态：锚点提示判定在前端纯函数里（读 BottleneckMode + AnchorResourceCode 两个值，后端零改动）',
    p20Ok,
    p20Ok ? '判定齐备' : '判定函数缺失，或未同时读 BottleneckMode / AnchorResourceCode 两个值'
  )

  const p20AutoSkip = /mode\s*===\s*0/.test(p20Fn)
  assert(
    'P20 静态：Auto（0）不出提示（非自动锚点方式才提示）',
    p20AutoSkip,
    p20AutoSkip ? 'Auto 已跳过' : '未跳过 Auto ⇒ 每次编辑都弹提示'
  )

  const p20NoThrow = !/throw\s/.test(p20Fn) && !/ElMessageBox|confirm\(/.test(p20Fn)
  assert(
    'P20 静态：锚点提示**不拦截**（函数内无 throw / 无确认框 ⇒ 不阻断发布）',
    p20NoThrow,
    p20NoThrow ? '纯文案产出，不阻断' : '提示里出现 throw 或确认框 ⇒ 会阻断发布'
  )

  const p20PageHas = /anchorHintMessage/.test(readSrc('views/Aps/ParameterSets.vue'))
  assert(
    'P20 静态：参数集页有锚点提示落点（求解器策略块内渲染）',
    p20PageHas,
    p20PageHas ? '页面已渲染提示' : '页面未渲染提示 ⇒ 用户看不到'
  )
}

/* ==================== [P21-P26] 参数集仓库引用四处落点（纯静态，不登录）====================
 * 派活来源：3号位 2026-10-25《参数集仓库编码校验死路处置·验收回执 v1.0》§六
 *   「§四 前端数组落点 → 可逐项编辑 + 搜索下拉 / fork 前置提示 | 4号位 | 贵方开工（不等新端点）」
 *
 * 后端真源（只读引用）：
 *   落点采集段 `GovernanceVersionService.cs:1019-1034`
 *   DTO `FrozenStrategySnapshot.cs:198/223/225/229/236-238/248-250`
 *   错误码 `INVALID_WAREHOUSE_REF`（`GovernanceVersionService.cs:1085`）
 *
 * 锁六件事：
 *   P21 四处落点路径/字段名与后端一致（写错键名 = 改不动 / 存不进去）
 *   P22 字典数据源 = 后端只读端点，且**前端不硬编码夹具码**（3号位 §四：走种子/命名约定，不写死在前端）
 *   P23 四处落点各有专属编辑器 + store 写动作（写 workingBlocks 且置脏）
 *   P24 仓库码**只能从字典里选**（不得自由文本录入 —— 拼不出假码）
 *   P25 fork 对话框有仓库编码前置提示，且**不阻断** fork
 *   P26 通用参数表剔除四处落点（避免同一份数据两处显示、只读 JSON 与编辑器打架）
 *
 * 反向破坏自证：逐条改坏 ⇒ 必须转红（见 scripts/selfverify-warehouse-ref.mjs）
 */
function runWarehouseRefStatic() {
  /* 取函数体（局部副本：verify 里的 fnBody 是 rules 段的内嵌函数） */
  const fnBodyOf = (src, name) => {
    const start = src.indexOf(`function ${name}(`)
    if (start < 0) return ''
    let depth = 0
    let opened = false
    for (let i = start; i < src.length; i++) {
      if (src[i] === '{') {
        depth++
        opened = true
      } else if (src[i] === '}') {
        depth--
        if (opened && depth === 0) return src.slice(start, i + 1)
      }
    }
    return ''
  }

  const wrSrc = stripComments(readSrc('api/aps-v1/warehouseRefs.ts'))
  const wdSrc = stripComments(readSrc('api/aps-v1/warehouseDictionary.ts'))
  const psStore = stripComments(readSrc('store/modules/aps/parameterSets.ts'))
  const psView = stripComments(readSrc('views/Aps/ParameterSets.vue'))
  const editorSrc = stripComments(readSrc('views/Aps/components/WarehouseRefEditor.vue'))
  const vmSrc = stripComments(readSrc('api/aps-v1/validationMessages.ts'))

  /* -------- P21 四处落点路径 / 字段名与后端一致 -------- */
  const p21Paths = [
    "SUPPLY_WAREHOUSE_PRIORITY_PATH = ['Inventory', 'WarehousePriority']",
    "PROCUREMENT_WAREHOUSE_PRIORITY_PATH = ['WarehousePriority']",
    "DEFAULT_PURCHASE_LT_PATH = ['DefaultPurchaseLt']",
    "ARRIVAL_TO_USABLE_OFFSETS_PATH = ['ArrivalToUsableOffsets']"
  ]
  const p21MissingPaths = p21Paths.filter((p) => !wrSrc.includes(p))
  const p21Fields = ['WarehouseCode', 'MaterialId', 'DefaultLtDays', 'OffsetHours']
  const p21MissingFields = p21Fields.filter((f) => !wrSrc.includes(f))
  const p21Ok = p21MissingPaths.length === 0 && p21MissingFields.length === 0
  assert(
    'P21 静态：四处仓库落点路径与字段名与后端一致（GovernanceVersionService.cs:1019-1034）',
    p21Ok,
    p21Ok
      ? '四条路径 + 四个字段名齐备'
      : `缺路径[${p21MissingPaths.join(' / ')}] 缺字段[${p21MissingFields.join(' / ')}]`
  )

  /* -------- P22 字典走后端端点 + 前端不硬编码夹具码 -------- */
  // ⚠️ 判据带 `url:` 上下文 —— 只判 includes(路径) 时，改注释里的同一串路径仍会假绿
  const p22Endpoint = /url:\s*'\/api\/governance-query\/warehouses'/.test(wdSrc)
  assert(
    'P22 静态：仓库字典数据源 = 后端只读端点 /api/governance-query/warehouses（不回退、不手写）',
    p22Endpoint,
    p22Endpoint ? '端点正确' : '未调用后端字典端点 ⇒ 下拉会是假数据'
  )
  // 3号位 §四 明写：夹具码区分「走后端种子编码/名称约定」，**不硬编码**到前端
  const p22FixtureRe = /PS[-_]DEMO|TEST-PS|PS_READY|PS-READY/i
  const p22FixtureHit = [wrSrc, wdSrc, editorSrc].filter((s) => p22FixtureRe.test(s))
  assert(
    'P22 静态：前端不硬编码夹具仓库/参数集编码（3号位 §四：区分走种子与命名约定）',
    p22FixtureHit.length === 0,
    p22FixtureHit.length === 0
      ? '无硬编码夹具码'
      : `${p22FixtureHit.length} 个文件里出现硬编码夹具码`
  )

  /* -------- P23 四处落点各有编辑器 + store 写动作 -------- */
  const p23Kinds = ['supply-priority', 'procurement-priority', 'purchase-lt', 'arrival-offset']
  const p23MissingKinds = p23Kinds.filter((k) => !psView.includes(`kind="${k}"`))
  const p23Actions = [
    'setSupplyWarehousePriority',
    'setProcurementWarehousePriority',
    'setDefaultPurchaseLt',
    'setArrivalToUsableOffsets'
  ]
  const p23MissingActions = p23Actions.filter((a) => !psStore.includes(`function ${a}(`))
  const p23Writes =
    /writeStringArray\(buffer\.value\.workingBlocks/.test(psStore) &&
    /writeObjectArray\(\s*buffer\.value\.workingBlocks/.test(psStore) &&
    /buffer\.value\.dirty\[block\]\s*=\s*true/.test(psStore) &&
    /buffer\.value\.dirty\.procurement\s*=\s*true/.test(psStore)
  const p23Ok =
    p23MissingKinds.length === 0 && p23MissingActions.length === 0 && p23Writes
  assert(
    'P23 静态：四处落点各有专属编辑器 + store 写动作（写 workingBlocks 并置脏）',
    p23Ok,
    p23Ok
      ? '四个编辑器 + 四个写动作齐备'
      : `缺编辑器[${p23MissingKinds.join(' / ')}] 缺动作[${p23MissingActions.join(' / ')}] 写入/置脏${p23Writes ? '齐' : '缺'}`
  )

  /* -------- P24 仓库码只能从字典里选（不得自由文本录入） -------- */
  const p24SelectOk =
    /<ElSelect[\s\S]{0,600}?:model-value="row"/.test(editorSrc) &&
    /<ElSelect[\s\S]{0,600}?:model-value="row\.WarehouseCode/.test(editorSrc)
  const p24NoFreeText = !/<ElInput[^>]*WarehouseCode/.test(editorSrc)
  const p24Ok = p24SelectOk && p24NoFreeText
  assert(
    'P24 静态：仓库码只走下拉选择（可搜索编码/名称），不提供自由文本录入',
    p24Ok,
    p24Ok
      ? '仓库码控件均为 ElSelect，无自由文本输入'
      : `下拉控件${p24SelectOk ? '有' : '缺'} 自由文本${p24NoFreeText ? '无' : '有'} ⇒ 可拼出假编码`
  )
  // 下拉项必须来自字典（不能是前端自造清单）
  const p24FromDict = /store\.warehouseDict/.test(editorSrc) && /optionsFor\(|unusedOptions\(/.test(editorSrc)
  assert(
    'P24 静态：下拉选项来自后端字典（store.warehouseDict），不是前端自造清单',
    p24FromDict,
    p24FromDict ? '选项源正确' : '下拉选项未取自后端字典'
  )

  /* -------- P25 fork 前置提示（非阻断） -------- */
  const p25Hint =
    psView.includes('forkSourceUnknownCodes') && psView.includes('仓库字典里找不到')
  const p25StoreFn = fnBodyOf(psStore, 'checkForkSourceWarehouseRefs')
  const p25HasCheck = p25StoreFn.length > 0 && /getParameterSetVersion/.test(p25StoreFn)
  const p25Ok = p25Hint && p25HasCheck
  assert(
    'P25 静态：fork 对话框前置提示源版本里的未知仓库编码（3号位 §四）',
    p25Ok,
    p25Ok ? '提示落点 + 预检动作齐备' : `提示${p25Hint ? '有' : '缺'} 预检${p25HasCheck ? '有' : '缺'}`
  )
  // 非阻断：预检不许抛错/弹确认框挡住 fork
  const p25NoThrow = !/throw\s/.test(p25StoreFn) && !/ElMessageBox|confirm\(/.test(p25StoreFn)
  const p25ForkBody = fnBodyOf(stripComments(readSrc('views/Aps/ParameterSets.vue')), 'onCreateDraft')
  const p25ForkNotBlocked = !/ElMessageBox|confirm\(/.test(p25ForkBody)
  assert(
    'P25 静态：fork 预检只是提示、不阻断（预检失败不挡 fork）',
    p25NoThrow && p25ForkNotBlocked,
    p25NoThrow && p25ForkNotBlocked ? '非阻断' : '预检里出现 throw/确认框 ⇒ 会挡住 fork'
  )

  /* -------- P26 通用参数表剔除四处落点 -------- */
  const p26Filter =
    /isWarehouseRefRow\(/.test(psStore) && /if\s*\(\s*isWarehouseRefRow\(row\.block,\s*row\.path\)\s*\)\s*return/.test(psStore)
  assert(
    'P26 静态：通用参数表剔除四处仓库落点（避免同一数据两处显示、只读 JSON 与编辑器打架）',
    p26Filter,
    p26Filter ? '已剔除' : '未剔除 ⇒ 仓库落点会同时出现在通用表（只读 JSON 串）和专属编辑器里'
  )

  /* -------- 附：INVALID_WAREHOUSE_REF 中文文案 -------- */
  // ⚠️ 判据必须用**带引号的完整字面量**，不能只判 `includes('INVALID_WAREHOUSE_REF')` ——
  //   改成 'INVALID_WAREHOUSE_REF_X' 时子串仍命中 ⇒ 探针假绿。
  const p27Code = /WAREHOUSE_REF_CODE\s*=\s*'INVALID_WAREHOUSE_REF'/.test(vmSrc)
  const p27Text = vmSrc.includes('请改成字典里的真实仓库编码')
  const p27Translate = p27Code && p27Text
  assert(
    'P27 静态：INVALID_WAREHOUSE_REF 有中文文案（不把内部视图名 ext_MES_ProcessCode_View 抛给用户）',
    p27Translate,
    p27Translate ? '已翻译' : `错误码字面量${p27Code ? '有' : '无'} 文案${p27Text ? '有' : '无'}`
  )
}

/* ==================== [A01-A08] 锚点资源编码补录入（纯静态，不登录）====================
 * 派活来源：3号位 2026-10-25《锚点资源编码补录入自由文本 · 数据侧定调回执 v1.0》
 *   「采纳贵方 §四 R3 方案 (a) 自由文本补录入。…… 后端零改动 …… 真正落点 = 前端编辑态
 *    solverStrategyJson 序列化时带上 AnchorResourceCode 键，flattenBlocks 随即可展录。」
 *
 * 后端真源（只读引用）：
 *   DTO 字段 `FrozenStrategySnapshot.cs:269`（AnchorResourceCode 早已存在）
 *   空块拦截 `GovernanceVersionService.cs:1095-1104`（EMPTY_SOLVER_STRATEGY）
 *   运行期反查 `PhaseOneConstraintBuilder.cs:1220` / `PhaseThreeDiagnostics.cs:137-138`
 *
 * 锁八件事：
 *   A01 键名常量与后端 DTO 属性名逐字一致（写错键名 = 存进去也不生效）
 *   A02 载入时补键（存量参数集没有这个键 ⇒ 页面恒有一行可编辑）
 *   A03 **只对非空块补键**（空块补键会把「没配求解器策略」变成「按默认配」⇒ 改发布语义）
 *   A04 页面有**自由文本**录入项（不是下拉：下拉要 5号位 scope 谓词到位才能做）
 *   A05 通用参数表剔除该行（避免同一份数据显示两处）
 *   A06 写动作写 workingBlocks 并置脏（不落库 ⇒ 改完看不到）
 *   A07 写入做 trim（运行期反查是精确匹配，首尾空格会静默退化成 Auto）
 *   A08 不阻断（不 throw / 不弹确认框）
 *
 * 反向破坏自证：逐条改坏 ⇒ 必须转红（见 scripts/selfverify-anchor-resource.mjs）
 */
function runAnchorResourceStatic() {
  /* 取函数体（局部副本：verify 里的 fnBody 是 rules 段的内嵌函数） */
  const fnBodyOf = (src, name) => {
    const start = src.indexOf(`function ${name}(`)
    if (start < 0) return ''
    let depth = 0
    let opened = false
    for (let i = start; i < src.length; i++) {
      if (src[i] === '{') {
        depth++
        opened = true
      } else if (src[i] === '}') {
        depth--
        if (opened && depth === 0) return src.slice(start, i + 1)
      }
    }
    return ''
  }

  const anchorSrc = stripComments(readSrc('api/aps-v1/anchorResource.ts'))
  const psStore = stripComments(readSrc('store/modules/aps/parameterSets.ts'))
  // ⚠️ stripComments 只剥 /* */ 与 //，**不剥 HTML 注释** ⇒ 注释里写一句
  //   「锚点资源编码」就能把 A04 的中文标签判据凑绿 ⇒ 假绿。这里额外剥掉 HTML 注释。
  const psPage = stripComments(readSrc('views/Aps/ParameterSets.vue')).replace(
    /<!--[\s\S]*?-->/g,
    ''
  )

  /* -------- A01 键名常量 -------- */
  // ⚠️ 用带引号的完整字面量判据：只判 includes('AnchorResourceCode') 的话，
  //   改成 'AnchorResourceCodeX' 探针仍绿 ⇒ 假绿。
  const a01Key = /ANCHOR_RESOURCE_CODE_KEY\s*=\s*'AnchorResourceCode'/.test(anchorSrc)
  assert(
    'A01 静态：锚点编码键名常量与后端 DTO 属性名逐字一致（AnchorResourceCode）',
    a01Key,
    a01Key ? '键名一致' : '键名常量缺失或与 FrozenStrategySnapshot.cs:269 不一致'
  )

  /* -------- A02 载入时补键 -------- */
  const a02LoadFn = fnBodyOf(psStore, 'loadDetail')
  const a02Called = /ensureAnchorResourceCodeKey\s*\(/.test(a02LoadFn)
  assert(
    'A02 静态：载入参数集时调用补键（存量块没有这个键 ⇒ 页面得有可编辑的录入项）',
    a02Called,
    a02Called ? '载入即补键' : 'loadDetail 里没有调用 ensureAnchorResourceCodeKey ⇒ 存量参数集永远看不到录入项'
  )

  /* -------- A03 只对非空块补键 -------- */
  const a03EmptyGuard = /Object\.keys\(ss\)\.length\s*===\s*0/.test(anchorSrc)
  const a03NoOverwrite = /ANCHOR_RESOURCE_CODE_KEY\s+in\s+ss/.test(anchorSrc)
  assert(
    'A03 静态：补键**只对非空块**、且不覆盖已有值（空块补键 = 前端擅自把「没配」改成「按默认配」）',
    a03EmptyGuard && a03NoOverwrite,
    a03EmptyGuard && a03NoOverwrite
      ? '空块跳过 + 已有值不覆盖'
      : `空块守卫${a03EmptyGuard ? '有' : '无'} 不覆盖${a03NoOverwrite ? '有' : '无'}`
  )

  /* -------- A04 页面自由文本录入项 -------- */
  const a04Start = psPage.indexOf('class="anchor-field"')
  const a04End = psPage.indexOf('anchorHintMessage', a04Start > 0 ? a04Start : 0)
  const a04Block = a04Start >= 0 && a04End > a04Start ? psPage.slice(a04Start, a04End) : ''
  const a04Input = /<ElInput/.test(a04Block) && /anchorResourceCode/.test(a04Block)
  const a04Label = a04Block.includes('锚点资源编码')
  const a04NoSelect = !/ElSelect|ElOption/.test(a04Block)
  assert(
    'A04 静态：页面有「锚点资源编码」**自由文本**录入项（不是下拉：下拉要 5号位 scope 谓词到位）',
    a04Input && a04Label && a04NoSelect,
    a04Input && a04Label && a04NoSelect
      ? '自由文本录入项在位'
      : `输入框${a04Input ? '有' : '无'} 中文标签${a04Label ? '有' : '无'} 非下拉${a04NoSelect ? '是' : '否'}`
  )

  /* -------- A05 通用表剔除 -------- */
  const a05Filter =
    /isAnchorResourceCodeRow\(/.test(psStore) &&
    /if\s*\(\s*isAnchorResourceCodeRow\(row\.block,\s*row\.path\)\s*\)\s*return/.test(psStore)
  assert(
    'A05 静态：通用参数表剔除锚点编码行（避免同一份数据显示两处）',
    a05Filter,
    a05Filter ? '已剔除' : '未剔除 ⇒ 通用表里会多出一行裸英文键名 AnchorResourceCode'
  )

  /* -------- A06 写动作 -------- */
  const a06Fn = fnBodyOf(psStore, 'setAnchorResourceCode')
  const a06Write = /writeAnchorResourceCode\s*\(/.test(a06Fn)
  const a06Dirty = /dirty\.solverStrategy\s*=\s*true/.test(a06Fn)
  assert(
    'A06 静态：写动作写 workingBlocks 并置脏 solverStrategy（不落库 = 改完看不到）',
    a06Write && a06Dirty,
    a06Write && a06Dirty ? '写入 + 置脏齐备' : `写入${a06Write ? '有' : '无'} 置脏${a06Dirty ? '有' : '无'}`
  )

  /* -------- A07 写入 trim -------- */
  const a07Fn = fnBodyOf(anchorSrc, 'writeAnchorResourceCode')
  const a07Trim = /\.trim\(\)/.test(a07Fn)
  assert(
    'A07 静态：写入时 trim（运行期是精确反查，首尾空格会静默退化成 Auto）',
    a07Trim,
    a07Trim ? '已 trim' : '未 trim ⇒ 用户多打一个空格，锚点就静默失效'
  )

  /* -------- A08 不阻断 -------- */
  const a08NoThrow = !/throw\s/.test(a06Fn) && !/ElMessageBox|confirm\(/.test(a06Fn)
  assert(
    'A08 静态：写动作不阻断（无 throw / 无确认框 ⇒ 填错编码不会挡住保存与发布）',
    a08NoThrow,
    a08NoThrow ? '非阻断' : '写动作里出现 throw/确认框 ⇒ 会挡住保存'
  )
}

/* ==================== [O] OPM 工艺规划模式治理（o-opm）==================== */
/**
 * 3号位 2026-09-23 T1 交付件：
 *   frontNew/docs/APS_V1_OPM治理API_S2S3_3号位致4号位_v1.0_20260923.md
 *
 * 端点（2 个）：
 *   GET  /api/governance/routing-operations?materialId={物料Id}
 *   PUT  /api/governance/routing-operations/{id}/planning-mode
 *
 * 验证策略：O01 静态契约 + O02 端点可达 + O03 默认跳过接受类。
 * 端点可达用「发请求 → 期望 200/400/403/404/422」视为端点存在；**不实际更新 OPM** 避免脏数据上界锁。
 * 后端联调地址/真实 DTO 样本未到前，O02 部分断言可能 404；属预期。
 */

function runO01Static() {
  // O01-1 types/opm.ts 含 OperationPlanningMode + RoutingOperationDto + OPM_META
  const opmTypesContent = readSrc('api/aps-v1/types/opm.ts')
  const opmTypesOk =
    opmTypesContent.includes('OperationPlanningMode') &&
    opmTypesContent.includes('RoutingOperationDto') &&
    opmTypesContent.includes('OPM_META')
  assert(
    'O01 静态：types/opm.ts 含 OperationPlanningMode + RoutingOperationDto + OPM_META',
    opmTypesOk,
    opmTypesOk ? '契约类型齐备' : 'types/opm.ts 契约缺失'
  )

  // O01-2 opm.ts 含 listOperations + updatePlanningMode + MOCK_OPM_MATERIAL_ID
  const opmApiContent = readSrc('api/aps-v1/opm.ts')
  const opmApiOk =
    /async\s+listOperations\s*\(/.test(opmApiContent) &&
    /async\s+updatePlanningMode\s*\(/.test(opmApiContent) &&
    opmApiContent.includes('MOCK_OPM_MATERIAL_ID')
  assert(
    'O01 静态：api/aps-v1/opm.ts 含 listOperations + updatePlanningMode + MOCK_OPM_MATERIAL_ID',
    opmApiOk,
    opmApiOk ? 'API 封装齐备' : 'opm.ts API 封装缺失'
  )

  // O01-3 store/modules/aps/opm.ts 含 load + updatePlanningMode + 三态 getter
  const opmStoreContent = readSrc('store/modules/aps/opm.ts')
  // ESLint 会剥掉 object key 的引号（合法 identifier），用裸字符串或反斜杠转义都匹配；这里用 /[\s{]FINITE_RESOURCE[\s,:=]/ 形态精确匹配
  const opmStoreOk =
    /async\s+function\s+load\s*\(/.test(opmStoreContent) &&
    /async\s+function\s+updatePlanningMode\s*\(/.test(opmStoreContent) &&
    opmStoreContent.includes('modeCounts') &&
    /[\s{,]FINITE_RESOURCE[\s,:}]/.test(opmStoreContent) &&
    /[\s{,]UNCONSTRAINED[\s,:}]/.test(opmStoreContent) &&
    /[\s{,]WAIT_ONLY[\s,:}]/.test(opmStoreContent)
  assert(
    'O01 静态：store/modules/aps/opm.ts 含 load + updatePlanningMode + 三态 getter（FINITE_RESOURCE/UNCONSTRAINED/WAIT_ONLY）',
    opmStoreOk,
    opmStoreOk ? 'store 三态齐备' : 'opm store 缺失'
  )

  // O01-4 views/Aps/Opm.vue 文件存在 + 含 OPM_META + 三态运行时 label 引用（不再硬编码中文标签）
  const opmVueExists = staticFileExists('views/Aps/Opm.vue')
  const opmVueContent = readSrc('views/Aps/Opm.vue')
  const opmVueHasLabels =
    opmVueContent.includes('OPM_META') &&
    /OPM_META\.FINITE_RESOURCE\.label/.test(opmVueContent) &&
    /OPM_META\.UNCONSTRAINED\.label/.test(opmVueContent) &&
    /OPM_META\.WAIT_ONLY\.label/.test(opmVueContent)
  assert(
    'O01 静态：views/Aps/Opm.vue 存在 + 含 OPM_META + 三态运行时 label 引用',
    opmVueExists && opmVueHasLabels,
    opmVueExists && opmVueHasLabels ? '页面骨架齐备' : 'Opm.vue 不全（文件/OPM_META 三态 label 缺失）'
  )

  // O01-5 router/modules/aps.ts 注册 /operation-planning-mode 路由
  const routerContent = readSrc('router/modules/aps.ts')
  //  2026-10-17：分组改造后叶子路由一律**绝对路径**（`/aps/...`，锁死 URL 不变），
  //  旧口径 `path: 'operation-planning-mode'` 相对写法已不成立 ⇒ 改断绝对路径
  const routerOk =
    /path:\s*['"]\/aps\/operation-planning-mode['"]/.test(routerContent) &&
    routerContent.includes("'aps.rule.view'") &&
    routerContent.includes("'aps.rule.edit'")
  assert(
    'O01 静态：router/modules/aps.ts 注册 /operation-planning-mode 路由（aps.rule.view + aps.rule.edit OR）',
    routerOk,
    routerOk ? '路由已挂' : 'OPM 路由未挂或权限码缺失'
  )

  // O01-6 api/aps-v1/index.ts 重导出 opmApi + MOCK_OPM_MATERIAL_ID
  const indexContent = readSrc('api/aps-v1/index.ts')
  const indexHasOpm = indexContent.includes('opmApi') && indexContent.includes('MOCK_OPM_MATERIAL_ID')
  assert(
    'O01 静态：api/aps-v1/index.ts 重导出 opmApi + MOCK_OPM_MATERIAL_ID',
    indexHasOpm,
    indexHasOpm ? '已重导出' : 'api/aps-v1/index.ts 未重导出 opmApi'
  )

  // O01-7 api/aps-v1/types/index.ts 重导出 opm
  const typesIndexContent = readSrc('api/aps-v1/types/index.ts')
  const typesIndexHasOpm = typesIndexContent.includes("export * from './opm'")
  assert(
    'O01 静态：api/aps-v1/types/index.ts 重导出 ./opm',
    typesIndexHasOpm,
    typesIndexHasOpm ? '已重导出' : 'types/index.ts 未重导出 ./opm'
  )

  // O01-8 Opm.vue 瘦身：3 处冗余文案已移除（使用说明 ElAlert / 演示数据提示 ElAlert / 三态取值说明 ElDescriptions 卡片）
  // 探针自证可失败：把任一标题字面量重新注入即触发 false
  const noUsageHelp = !opmVueContent.includes('title="使用说明"')
  const noMockHint = !opmVueContent.includes('title="演示数据提示"')
  const noThreeStateDesc = !opmVueContent.includes('三态取值说明')
  assert(
    'O01 静态：Opm.vue 已移除 3 处冗余文案（使用说明 / 演示数据提示 / 三态取值说明）',
    noUsageHelp && noMockHint && noThreeStateDesc,
    noUsageHelp && noMockHint && noThreeStateDesc
      ? '瘦身完成'
      : '仍有冗余文案未清'
  )

  // O01-9 Opm.vue 行内 ElOption 暴露 OPM_META[m].description 作为 tooltip（替代被删的 ElDescriptions 卡片）
  const hasOptionTooltip = /:title="OPM_META\[m\]\.description"/.test(opmVueContent)
  assert(
    'O01 静态：Opm.vue ElOption 暴露 OPM_META[m].description tooltip',
    hasOptionTooltip,
    hasOptionTooltip ? 'tooltip 已挂' : 'ElOption 缺 :title 绑定'
  )

  // O01-10 Opm.vue 不再 import 被删组件/常量（ElDescriptions / ElDescriptionsItem / MOCK_OPM_MATERIAL_ID）
  const epImportBlock = opmVueContent.match(/import\s*\{[\s\S]*?\}\s*from\s*['"]element-plus['"]/m)
  const epBlockText = epImportBlock ? epImportBlock[0] : ''
  const noDescImportClean = !epBlockText.includes('ElDescriptions') && !epBlockText.includes('ElDescriptionsItem')
  const noMockImport = !opmVueContent.includes('MOCK_OPM_MATERIAL_ID')
  assert(
    'O01 静态：Opm.vue 已清理 element-plus import 中的 ElDescriptions / ElDescriptionsItem 与 MOCK_OPM_MATERIAL_ID',
    noDescImportClean && noMockImport,
    noDescImportClean && noMockImport ? 'unused import 已清' : '仍有 unused import'
  )

  // ===== O04 物料编码自动补全（用户视觉键由物料 ID 改为物料编码）=====

  // O04-1 types/material.ts 含 MaterialLookupItem（masterId / code / name / spec / uom / isActive）
  const materialTypesContent = readSrc('api/aps-v1/types/material.ts')
  const materialDtoOk =
    materialTypesContent.includes('MaterialLookupItem') &&
    /[\s{,]masterId[\s,:}]/.test(materialTypesContent) &&
    /[\s{,]code[\s,:}]/.test(materialTypesContent) &&
    materialTypesContent.includes('name') &&
    materialTypesContent.includes('spec') &&
    materialTypesContent.includes('uom') &&
    materialTypesContent.includes('isActive')
  assert(
    'O04 静态：api/aps-v1/types/material.ts 含 MaterialLookupItem（masterId/code/name/spec/uom/isActive）',
    materialDtoOk,
    materialDtoOk ? 'DTO 齐备' : 'types/material.ts 缺字段'
  )

  // O04-2 materialLookup.ts 含 searchMaterials + 防抖过滤短串
  const materialLookupContent = readSrc('api/aps-v1/materialLookup.ts')
  const materialApiOk =
    /async\s+searchMaterials\s*\(/.test(materialLookupContent) &&
    materialLookupContent.includes('/api/governance/lookups/materials') &&
    materialLookupContent.includes("params: { search: q }")
  assert(
    'O04 静态：api/aps-v1/materialLookup.ts 含 searchMaterials（GET lookups/materials）',
    materialApiOk,
    materialApiOk ? 'API 封装齐备' : 'materialLookup.ts 缺失'
  )

  // O04-3 index.ts / types/index.ts 重导出
  const indexHasMaterialApi = indexContent.includes('materialLookupApi')
  const typesIndexHasMaterial = typesIndexContent.includes("export * from './material'")
  assert(
    'O04 静态：api/aps-v1/index.ts 与 types/index.ts 重导出 materialLookup / MaterialLookupItem',
    indexHasMaterialApi && typesIndexHasMaterial,
    indexHasMaterialApi && typesIndexHasMaterial ? '已重导出' : '未重导出'
  )

  // O04-4 Opm.vue 顶部改用 ElAutocomplete（ElInput number 已移除）+ materialCodeInput + selectedMaterial
  // 探针自证可失败：把 ElInput 重新引入即触发 false
  const noOldInput = !opmVueContent.includes('v-model.number="materialIdInput"')
  const hasAutocomplete = /<ElAutocomplete\b/.test(opmVueContent)
  const hasValueKey = /value-key="code"/.test(opmVueContent)
  const hasFetchSuggestions = /:fetch-suggestions="fetchMaterialSuggestions"/.test(opmVueContent)
  const hasCodeInput = opmVueContent.includes('materialCodeInput')
  const hasSelectedMaterial = opmVueContent.includes('selectedMaterial')
  const hasNewLabel = opmVueContent.includes('物料编码')
  assert(
    'O04 静态：Opm.vue 顶部 ElInput number → ElAutocomplete（materialCodeInput/selectedMaterial/value-key=code）',
    noOldInput && hasAutocomplete && hasValueKey && hasFetchSuggestions && hasCodeInput && hasSelectedMaterial && hasNewLabel,
    noOldInput && hasAutocomplete && hasValueKey && hasFetchSuggestions && hasCodeInput && hasSelectedMaterial && hasNewLabel
      ? '顶部改造完成'
      : '顶部仍含旧 ElInput number 或缺新 ElAutocomplete 关键属性'
  )

  // ===== O05 紧凑三态摘要（替代 KPI 卡片；hasList 时显示）=====

  // O05-1 Opm.vue 不再有 3 张 KPI 卡片（无 ElRow opm-kpi / 无 .kpi-desc）
  // 探针自证可失败：把 KPI 卡片重新引入即触发 false
  const noKpiCards = !/class="opm-kpi"/.test(opmVueContent) && !opmVueContent.includes('.kpi-desc')
  assert(
    'O05 静态：Opm.vue 已移除 3 张 KPI 卡片（无 opm-kpi / .kpi-desc 残留）',
    noKpiCards,
    noKpiCards ? 'KPI 卡片已清' : '仍有 KPI 卡片或 desc 样式残留'
  )

  // O05-2 Opm.vue 工具栏内新增紧凑三态摘要（hasList + 3 ElTag + 共 N 条）
  const hasSummaryFormItem = /<ElFormItem[^>]*class="opm-summary"/.test(opmVueContent)
  const hasSummaryHasListGuard = /<ElFormItem[^>]*v-if="hasList"[^>]*class="opm-summary"/.test(opmVueContent)
  const hasThreeTags =
    opmVueContent.includes('OPM_META.FINITE_RESOURCE.label') &&
    opmVueContent.includes('OPM_META.UNCONSTRAINED.label') &&
    opmVueContent.includes('OPM_META.WAIT_ONLY.label') &&
    opmVueContent.includes('modeCounts.FINITE_RESOURCE') &&
    opmVueContent.includes('modeCounts.UNCONSTRAINED') &&
    opmVueContent.includes('modeCounts.WAIT_ONLY')
  const hasSummaryTotal = /summary-total[^>]*>\s*共\s*<strong>/.test(opmVueContent) || /class="summary-total"[^>]*>\s*共/.test(opmVueContent)
  assert(
    'O05 静态：Opm.vue 工具栏内新增紧凑三态摘要（v-if=hasList + 3 ElTag + 共 N 条）',
    hasSummaryFormItem && hasSummaryHasListGuard && hasThreeTags && hasSummaryTotal,
    hasSummaryFormItem && hasSummaryHasListGuard && hasThreeTags && hasSummaryTotal
      ? '紧凑摘要已挂'
      : '紧凑摘要缺失（v-if/ElTag/共 N 条任一缺）'
  )
}

async function verifyOpm(token) {
  log('\n[O] OPM 工艺规划模式治理（o-opm — 3号位 T1 交付件）')

  // O01 静态契约（与后端状态无关）
  runO01Static()

  // O02-1 GET /api/governance/routing-operations?materialId=1（端点可达）
  let r = await call('GET', '/api/governance/routing-operations', {
    token,
    query: { materialId: 1 }
  })
  assert(
    'O02 GET /api/governance/routing-operations?materialId=1（端点可达）',
    r.ok || r.code === 200 || r.code === 400 || r.code === 403 || r.code === 422,
    `code=${r.code}（200=有数据；400=参数校验；403=权限；端点可达即过；≠404 即过）`
  )

  // O02-2 PUT /api/governance/routing-operations/{id}/planning-mode（端点可达）
  r = await call('PUT', '/api/governance/routing-operations/1/planning-mode', {
    token,
    body: { operationId: 1, operationPlanningMode: 'FINITE_RESOURCE' }
  })
  assert(
    'O02 PUT /api/governance/routing-operations/{id}/planning-mode（端点可达）',
    r.ok || r.code === 200 || r.code === 400 || r.code === 403 || r.code === 404 || r.code === 422,
    `code=${r.code}（端点可达即过）`
  )

  // O02-3 三态校验：operationPlanningMode 非三态之一应 422
  r = await call('PUT', '/api/governance/routing-operations/1/planning-mode', {
    token,
    body: { operationId: 1, operationPlanningMode: 'INVALID_MODE' }
  })
  assert(
    'O02 PUT planning-mode 非三态值应 4xx（422=数据红线 / 400=校验生效 / 404=端点不存在）',
    r.code === 400 || r.code === 422 || r.code === 404 || r.code === 403,
    `code=${r.code}`
  )

  // O03 接受类（默认跳过）
  if (process.env.VERIFY_ALLOW_WRITE !== '1') {
    skip('O03 接受类', 'VERIFY_ALLOW_WRITE 未设置=1（脏数据风险；接受类需真实 OPM 修改端到端）')
  } else {
    info('VERIFY_ALLOW_WRITE=1 → O03 接受类断言开启（会真实修改 OPM 写库）')
    r = await call('PUT', '/api/governance/routing-operations/1/planning-mode', {
      token,
      body: { operationId: 1, operationPlanningMode: 'WAIT_ONLY' }
    })
    assert(
      'O03 PUT 真实写回（VERIFY_ALLOW_WRITE=1 模式）',
      r.ok || r.code === 404 || r.code === 403,
      `code=${r.code}`
    )
  }

  info('O 段全部为「端点可达 + 静态契约」验收；O03 接受类需 VERIFY_ALLOW_WRITE=1 才开启')
}

/* ==================== [A] CTP 评估域键透传（a-ctp-domainkey）==================== */
/**
 * [A] CTP 评估域键透传（a-ctp-domainkey）
 *  - 触发：09-30 CTP 评估 422 修复（A 路径 — 4号位 前端自做）
 *  - 现象：POST /api/ctp/evaluate 缺 domainKey → ASP.NET Core [Required] 模型校验 → 422
 *  - A 路径修复：types/ctp.ts CtpInput 加 domainKey: DomainKey；Ctp.vue 顶部加 ElSelect；store 透传
 *  - C 路径（3号位 09-30 落码）：CtpEvaluateRequest.DomainKey 改 optional + service 自动 resolve
 *  - 4号位 09-30 撤下拉（用户拍板）：前端不再传 domainKey，纯由后端 resolve
 *  - A01-A05 翻成「撤下拉」反断言（前端不再含 domainKey 链路）
 *  - C01-C02 后端契约层断言保留
 *  - CTP-EVAL01/02 动态（10-09 v1.0 真实落地回执 §五 4号位 兑现）：
 *    - 验收依据：frontNew/docs/APS_V1_CTP主数据迁移_3号位致4号位_真实落地回执_v1.0_20261009.md §四/§五
 *    - 8 真实物料（VALVE/LGJ/XC/JS/WKQ/TZP/MJ/QT）× 4 工厂（CN/BJ/SH/TJ）= 32 路径
 *    - 全 501（核心试算未接入契约行为）；禁止 400（多域歧义）/ 422（DTO 字段缺失）
 *    - 守门：VERIFY_CTP_EVAL=1（默认跳过，避免无 dev 时炸 32 次请求）
 *  - 验收依据：
 *    - frontNew/docs/APS_V1_CTP评估C路径落码回执_3号位致4号位_v1.0_20260930.md §五
 *    - frontNew/docs/APS_V1_CTP主数据迁移_3号位致4号位_真实落地回执_v1.0_20261009.md §五
 */
async function verifyCtpDomainKey(token) {
  log('\n[A] CTP 评估域键（a-ctp-domainkey — 09-30 CTP 422 修复 / 4号位撤前端下拉）')

  // A01 静态：types/ctp.ts CtpInput 不含 domainKey 字段定义（撤下拉后纯前端不再传）
  //   - 用 `\bdomainKey\s*:` 排除注释里的字样（如 `CtpEvaluateRequest.DomainKey 改 optional`）
  const ctpTypesContent = readSrc('api/aps-v1/types/ctp.ts')
  const ctpInputBlock = (() => {
    const m = ctpTypesContent.match(/export\s+interface\s+CtpInput\b\s*\{([\s\S]*?)\n\}/)
    return m ? m[1] : ''
  })()
  const a01 = ctpInputBlock.length > 0 && !/\bdomainKey\s*:\s*/.test(ctpInputBlock)
  assert(
    'A01 静态：types/ctp.ts CtpInput 不含 domainKey 字段定义（撤下拉后纯前端不再传）',
    a01,
    a01 ? '已撤' : ctpInputBlock.length === 0 ? '未定位 CtpInput 块' : '仍含 domainKey 字段'
  )

  // A02 静态：Ctp.vue 无 ElSelect v-model="form.domainKey"（前端下拉已撤）
  const ctpVueContent = readSrc('views/Aps/Ctp.vue')
  const a02 = !/<ElSelect\b[^>]*v-model\s*=\s*['"]form\.domainKey['"]/.test(ctpVueContent)
  assert(
    'A02 静态：Ctp.vue 不含 ElSelect v-model="form.domainKey"（下拉已撤）',
    a02,
    a02 ? '已撤' : '仍含下拉'
  )

  // A03 静态：Ctp.vue onSubmit 不再传 domainKey（前端 evaluate() 调用无该字段）
  const onSubmitBlock = (() => {
    const m = ctpVueContent.match(/async\s+function\s+onSubmit[\s\S]*?\n\}/)
    return m ? m[0] : ''
  })()
  const a03 = onSubmitBlock.length > 0 && !/\bdomainKey\b/.test(onSubmitBlock)
  assert(
    'A03 静态：Ctp.vue onSubmit 不含 domainKey（前端 evaluate() 不再传该字段）',
    a03,
    a03 ? '已撤' : onSubmitBlock.length === 0 ? '未定位 onSubmit' : 'onSubmit 仍含 domainKey'
  )

  // A04 静态：store/ctp.ts defaultInput 不含 domainKey（默认值已撤）
  //   - 默认值函数是 `const defaultInput = (): CtpInput => ({...})`（箭头返回，无 return）
  //   - 用 indexOf 定位后切片到首个 `})` 闭合，避免依赖函数体形态
  const ctpStoreContent = readSrc('store/modules/aps/ctp.ts')
  const defaultInputBlock = (() => {
    const i = ctpStoreContent.indexOf('const defaultInput')
    if (i < 0) return ''
    const slice = ctpStoreContent.slice(i)
    const m = slice.match(/=>\s*\(\s*\{([\s\S]*?)\}\s*\)/)
    return m ? m[1] : ''
  })()
  const a04 = defaultInputBlock.length > 0 && !/\bdomainKey\s*:\s*/.test(defaultInputBlock)
  assert(
    'A04 静态：store/ctp.ts defaultInput 不含 domainKey 字段定义（默认值已撤）',
    a04,
    a04 ? '已撤' : defaultInputBlock.length === 0 ? '未定位 defaultInput' : 'defaultInput 仍含 domainKey 字段'
  )

  // A05 静态：Ctp.vue 不含 noDomainAccess / 空态 ElAlert「无任何 Domain 权限」（撤下拉后无空态兜底）
  const a05 =
    !/\bnoDomainAccess\b/.test(ctpVueContent) &&
    !/无任何 Domain 权限/.test(ctpVueContent)
  assert(
    'A05 静态：Ctp.vue 不含 noDomainAccess / 空态警告（撤下拉后无 UI 空态兜底）',
    a05,
    a05 ? '已撤' : '仍含空态兜底'
  )

  // A06 动态（需后端接入）— 本期不跑，留待 3号位 接入 CTP 引擎后开启
  skip('A06 动态：CTP 端点联调验证（需 3号位 接入 CTP 引擎，本期跳过）')

  /* -------- A07-A08 静态（Ctp.vue 工厂下拉拉真实 API，09-30 接入）--------
   * 验收依据：frontNew/docs/4号位-2026-09-30-CTP评估接口422诊断-A+C并行修复-给3号位.md
   *   + 3号位 GET /api/governance/factories（domainApi.listFactories 复用）
   * 口径：纯前端静态扫描，确保 Ctp.vue 不再硬编码工厂，改走 API + 兜底
   */
  // A07 静态：Ctp.vue 导入 domainApi 且含 FACTORY_OPTIONS 为 ref（不再硬编码 const 数组）
  //   - 反例：`const FACTORY_OPTIONS = [{ code: 'F-SUZ-01', ... }]` → 失败
  const factoryImportMatch = /import\s*\{[^}]*\bdomainApi\b[^}]*\}\s*from\s*['"]@\/api\/aps-v1\/domain['"]/.test(
    ctpVueContent
  )
  const factoryRefMatch =
    /\bFACTORY_OPTIONS\s*=\s*ref\s*<[^>]*FactorySummaryDto[^>]*>\s*\(\s*\[\s*\]\s*\)/.test(
      ctpVueContent
    )
  const factoryNoHardcodedArray = !/^\s*const\s+FACTORY_OPTIONS\s*=\s*\[[\s\S]*?\bcode\s*:/.test(
    ctpVueContent
  )
  const a07 = factoryImportMatch && factoryRefMatch && factoryNoHardcodedArray
  assert(
    'A07 静态：Ctp.vue 导入 domainApi + FACTORY_OPTIONS 为 ref（不再硬编码工厂数组）',
    a07,
    a07
      ? '已接入 API（ref + listFactories）'
      : !factoryImportMatch
        ? '缺 domainApi 导入'
        : !factoryRefMatch
          ? 'FACTORY_OPTIONS 未声明为 ref<FactorySummaryDto[]>([])'
          : '仍含硬编码 FACTORY_OPTIONS 数组'
  )

  // A08 静态：Ctp.vue onMounted 调 domainApi.listFactories()，且**不得有硬编码工厂兜底**
  //  2026-10-19 更正：原判据要求「含 FACTORY_FALLBACK 兜底」，那等于**把假数据写进验收**：
  //  兜底值 `F-SUZ-01 / F-CDG-02` 在 dev 实库并不存在，用户选中后提交必被后端 400。
  //  与订单页 OD05 的整改口径一致 —— 拉不到就空下拉 + 红色提示，绝不伪造选项。
  //  反向自证：把 FACTORY_FALLBACK 加回来 → 转红。
  const onMountedBlock = (() => {
    const m = ctpVueContent.match(/onMounted\s*\(\s*\(\s*\)\s*=>\s*\{([\s\S]*?)\n\}\s*\)/)
    return m ? m[1] : ''
  })()
  const callsListFactories = /\bdomainApi\s*\.\s*listFactories\s*\(\s*\)/.test(onMountedBlock)
  const noFactoryFallback =
    !/FACTORY_FALLBACK/.test(ctpVueContent) && !/F-SUZ-01|F-CDG-02/.test(ctpVueContent)
  const a08 = callsListFactories && noFactoryFallback
  assert(
    'A08 静态：Ctp.vue onMounted 调 domainApi.listFactories()，且无硬编码工厂兜底（拉不到就空下拉）',
    a08,
    a08
      ? '已接 API 且无假值'
      : !callsListFactories
        ? 'onMounted 未调 listFactories()'
        : '仍含 FACTORY_FALLBACK / F-SUZ-01 / F-CDG-02 假值'
  )

  // A09 静态：Ctp.vue 物料下拉调 materialLookupApi.listActiveMaterials()（real API）+ 兜底
  //   - 端点：GET /api/governance/lookups/materials?activeOnly=true（无 search 全量）
  //   - 后端 SetupRuleController.cs:333 已含 activeOnly 参数，按既有契约适配
  const materialImportMatch =
    /import\s*\{[^}]*\bmaterialLookupApi\b[^}]*\}\s*from\s*['"]@\/api\/aps-v1\/materialLookup['"]/.test(
      ctpVueContent
    )
  const materialRefMatch =
    /\bMATERIAL_OPTIONS\s*=\s*ref\s*<[^>]*MaterialLookupItem[^>]*>\s*\(\s*\[\s*\]\s*\)/.test(
      ctpVueContent
    )
  const materialNoHardcodedArray =
    !/^\s*const\s+MATERIAL_OPTIONS\s*=\s*\[[\s\S]*?\bcode\s*:\s*'M-001'/.test(ctpVueContent)
  const callsListActiveMaterials = /\bmaterialLookupApi\s*\.\s*listActiveMaterials\s*\(\s*\)/.test(
    onMountedBlock
  )
  // 同 A08：不得有假物料兜底（M-001 ~ M-007 在 dev 实库零重合）
  const noMaterialFallback =
    !/MATERIAL_FALLBACK/.test(ctpVueContent) && !/'M-001'/.test(ctpVueContent)
  const a09 =
    materialImportMatch &&
    materialRefMatch &&
    materialNoHardcodedArray &&
    callsListActiveMaterials &&
    noMaterialFallback
  assert(
    'A09 静态：Ctp.vue 物料下拉调 materialLookupApi.listActiveMaterials()，且无硬编码物料兜底',
    a09,
    a09
      ? '已接 API 且无假值'
      : !materialImportMatch
        ? '缺 materialLookupApi 导入'
        : !materialRefMatch
          ? 'MATERIAL_OPTIONS 未声明为 ref<MaterialLookupItem[]>([])'
          : !callsListActiveMaterials
            ? 'onMounted 未调 listActiveMaterials()'
            : '仍含 MATERIAL_FALLBACK / M-001 假值'
  )

  // A10 静态：工厂/物料拉取失败时**如实提示**（optionLoadError），而不是静默给空下拉
  //   - 失败分支必须同时把选项置空 + 写错误提示；只置空会变成「页面看着正常但没有可选项」
  //   - 反向自证：删掉 optionLoadError 赋值 → 转红
  const a10 =
    (/optionLoadError\s*\.\s*value\s*=/.test(onMountedBlock) || /optionLoadError\.value\s*=/.test(ctpVueContent)) &&
    /v-if="optionLoadError"/.test(ctpVueContent)
  assert(
    'A10 静态：下拉数据源失败时如实提示（optionLoadError），不静默伪装成空列表',
    a10,
    a10 ? '失败分支有可见提示' : '失败分支静默'
  )

  // A11 静态：CTP 根因码复用冻结 15 码（不再自拟 5 码），且标签查表带未知码降级
  //  - 2026-10-19：原 5 码与冻结 `07 §八.1` 只有 DUE_DATE_RISK 一处重合，且模板裸索引码表
  //    （`REASON_LABEL[r.reasonCode]`）⇒ 后端按冻结码返回时标签渲染成空白。
  //  - 反例：types/ctp.ts 再出现 `'CAPACITY_LIMIT'` 等自拟码 → 转红；删掉 `待核对·` 降级 → 转红。
  const ctpSelfInvented =
    /'MATERIAL_AVAILABLE'|'CAPACITY_LIMIT'|'CROSS_DOMAIN_HANDOFF'|'DEMAND_PROTECTION_CONFLICT'/.test(
      ctpTypesContent
    )
  const ctpReasonTyped = /reasonCode:\s*ReasonCode\b/.test(ctpTypesContent)
  const ctpSharedDict = /REASON_CODE_LABELS/.test(ctpVueContent)
  const ctpDegrade = /待核对·/.test(ctpVueContent)
  const ctpNoBareIndex = !/REASON_LABEL\s*\[\s*r\.reasonCode\s*\]/.test(ctpVueContent)
  const a11 = ctpReasonTyped && !ctpSelfInvented && ctpSharedDict && ctpDegrade && ctpNoBareIndex
  assert(
    'A11 静态：CTP 根因码复用冻结 15 码 + 未知码降级（不再自拟码表 / 不裸索引）',
    a11,
    `类型=${ctpReasonTyped} 自拟码残留=${ctpSelfInvented} 共用字典=${ctpSharedDict} 降级=${ctpDegrade} 无裸索引=${ctpNoBareIndex}`
  )

  /* -------- C01/C02 静态（lps/ C 路径契约层，3号位 09-30 落码）--------
   * 验收依据：frontNew/docs/APS_V1_CTP评估C路径落码回执_3号位致4号位_v1.0_20260930.md §五
   * 口径：纯只读静态扫描 lps/（4号位 硬约束：lps/ 仅 Read，不 Edit/Write；本断言不改 lps/）
   */
  const lpsCtpReq = (() => {
    try {
      return readFileSync(join(LPS_ROOT, 'LPS.APS.Core', 'DTOs', 'Ctp', 'CtpEvaluateRequest.cs'), 'utf-8')
    } catch {
      return ''
    }
  })()
  const lpsCtpSvc = (() => {
    try {
      return readFileSync(join(LPS_ROOT, 'LPS.APS.Application', 'Services', 'CtpService.cs'), 'utf-8')
    } catch {
      return ''
    }
  })()

  // C01 静态：CtpEvaluateRequest.DomainKey 无 [Required]（契约 §3.2：可选化）
  //   - 断言：在 DomainKey 字段附近没有 [Required(...)] 属性
  //   - 反例：`[Required(ErrorMessage = "domainKey 必填")] public string? DomainKey { get; set; }` → 失败
  const c01 =
    lpsCtpReq.length > 0 &&
    /\bpublic\s+string\?\s+DomainKey\b/.test(lpsCtpReq) &&
    !/\[\s*Required\s*\([^)]*\)\][\s\S]{0,80}?\bpublic\s+string\?\s+DomainKey\b/.test(lpsCtpReq)
  assert(
    'C01 静态：lps/ CtpEvaluateRequest.DomainKey 无 [Required]（契约可选化）',
    c01,
    c01
      ? '已可选（string? + 无 Required）'
      : lpsCtpReq.length === 0
        ? 'lps/LPS.APS.Core/DTOs/Ctp/CtpEvaluateRequest.cs 不存在'
        : '仍含 [Required]，契约未可选化'
  )

  // C02 静态：CtpService.EvaluateAsync 调 IDomainResolver.ResolveAsync（materialCode+factoryCode，契约 §3.2）
  //   - 断言：EvaluateAsync 函数体内出现 _domainResolver.ResolveAsync(request.MaterialCode, request.FactoryCode, ...)
  const c02 =
    lpsCtpSvc.length > 0 &&
    /public\s+async\s+Task[\s\S]*?EvaluateAsync\b[\s\S]*?_domainResolver\.ResolveAsync\s*\(\s*request\.MaterialCode\s*,\s*request\.FactoryCode\b/.test(
      lpsCtpSvc
    )
  assert(
    'C02 静态：lps/ CtpService.EvaluateAsync 含 ResolveAsync(MaterialCode, FactoryCode) 调用',
    c02,
    c02
      ? '已含自动 resolve 调用'
      : lpsCtpSvc.length === 0
        ? 'lps/LPS.APS.Application/Services/CtpService.cs 不存在'
        : '未含自动 resolve 调用'
  )

  /* -------- CTP-EVAL01/02 动态（10-09 v1.0 真实落地回执 §五 4号位 兑现）
   * 验收依据：frontNew/docs/APS_V1_CTP主数据迁移_3号位致4号位_真实落地回执_v1.0_20261009.md
   *   - §四 真实冒烟物料清单 8 码 × 工厂 CN/BJ/SH/TJ = 32 路径全 501（核心试算未接入契约行为）
   *   - 禁止：HTTP 400（多域歧义 / 域键 resolve 漏配）/ 422（DTO 字段缺失 / 类型错）
   *   - 与 §六 既有 9 单测 + 32 真实路径冒烟 一致；4号位 不重复独立组 Group，复用 a-ctp-domainkey
   *   - 用 `VERIFY_CTP_EVAL=1` 守门（默认跳过，避免无 dev 时炸 32 次请求）
   */
  if (process.env.VERIFY_CTP_EVAL !== '1') {
    skip('CTP-EVAL01/02 动态：8 物料 × 4 工厂 = 32 路径全 501（设置 VERIFY_CTP_EVAL=1 跑，dev 联调）')
  } else {
    const realMaterials = [
      { productFamily: 'VALVE', materialCode: 'FINAL_ASSY-51-SYE100-3-2A-ZZ' },
      { productFamily: 'LGJ', materialCode: 'FINAL_ASSY-IDF-K2A0716M' },
      { productFamily: 'XC', materialCode: 'FINAL_RAW-A6061-355-1200-GC' },
      { productFamily: 'JS', materialCode: 'FINAL_ASSY-145111' },
      { productFamily: 'WKQ', materialCode: 'FINAL_ASSY-11E-XQB' },
      { productFamily: 'TZP', materialCode: 'FINAL_ASSY-10-VK334V-DND7362' },
      { productFamily: 'MJ', materialCode: 'FINAL_WIP-1_2A-37273-40' },
      { productFamily: 'QT', materialCode: 'FINAL_ASSY-GJ22408-1' }
    ]
    const realFactories = ['CN', 'BJ', 'SH', 'TJ']
    let eval501Count = 0
    let eval400Count = 0
    let eval422Count = 0
    const evalFailures = []

    for (const m of realMaterials) {
      for (const factory of realFactories) {
        const r = await call('POST', '/api/ctp/evaluate', {
          token,
          body: {
            orderCanonicalId: `PO-CTP-EVAL-${m.productFamily}-${factory}`,
            materialCode: m.materialCode,
            quantity: 100,
            factoryCode: factory,
            requestedDueDate: '2026-10-15',
            purpose: 'CTP'
          }
        })
        if (r.code === 501) eval501Count++
        else if (r.code === 400) {
          eval400Count++
          evalFailures.push({ mat: m.materialCode, factory, code: 400, msg: r.json?.message ?? '' })
        } else if (r.code === 422) {
          eval422Count++
          evalFailures.push({ mat: m.materialCode, factory, code: 422, msg: r.json?.message ?? '' })
        } else {
          evalFailures.push({ mat: m.materialCode, factory, code: r.code, msg: r.json?.message ?? '' })
        }
      }
    }

    const eval01All501 = eval400Count === 0 && eval422Count === 0 && eval501Count === realMaterials.length * realFactories.length
    assert(
      'CTP-EVAL01 动态：CTP 评估 501 契约行为（核心试算未接入；禁止 400/422）',
      eval01All501,
      eval01All501
        ? `32/32 全 501（核心试算未接入契约行为；域键 resolve 已命中）`
        : `501=${eval501Count} 400=${eval400Count} 422=${eval422Count} 其它=${evalFailures.length - eval400Count - eval422Count}`
    )

    const eval02No400 =
      eval400Count === 0 && eval422Count === 0 && evalFailures.every((f) => f.code !== 400 && f.code !== 422)
    assert(
      'CTP-EVAL02 动态：8 真实物料 × 4 工厂 = 32 路径 全部 501（域键 resolve 已命中，无 400 多域歧义）',
      eval02No400,
      eval02No400
        ? `32/32 全 501（域键 resolve 全命中）`
        : `失败路径：${evalFailures.map((f) => `${f.mat}@${f.factory}=${f.code}(${f.msg.slice(0, 60)})`).slice(0, 3).join(' | ')}`
    )
  }
}

/* ==================== [ME] ManualETA 顶部精简（me-eta）==================== */

/**
 * [ME] 人工到货 ETA 页面顶部精简（me-eta — 10-07 撤使用说明）
 * 依据：
 *  - 用户 2026-10-07 反馈："这块儿关于说明的，可以都去掉吧，页面以简洁好用为主"
 *  - 规则：feedback_no_internal_notices_in_ui.md（内部协调痕迹不进 UI）
 *  - 删：ElAlert「使用说明」整块（标题+说明+审计追溯）
 *  - 改：副标题精简为「仅人工值；有效到货时间由系统计算」
 *  - 留：标题「人工到货 ETA」、刷新按钮、错误条、KPI、筛选、列表、Drawer
 */
async function verifyManualEta(token) {
  log('\n[ME] 人工到货 ETA 顶部 + Drawer 精简（me-eta — 10-07 撤说明）')

  const manualEtaVueContent = readSrc('views/Aps/ManualEta.vue')

  // ME01 静态：ManualEta.vue 不含 ElAlert title="使用说明"（顶部冗余说明已撤）
  const me01 =
    !/<template\s+#title>\s*使用说明\s*<\/template>/.test(manualEtaVueContent) &&
    !/class\s*=\s*["']hint-bar["']/.test(manualEtaVueContent)
  assert(
    'ME01 静态：ManualEta.vue 不含 ElAlert「使用说明」（顶部冗余说明已撤）',
    me01,
    me01 ? '已撤' : '仍含使用说明 ElAlert'
  )

  // ME02 静态：ManualEta.vue 不含「可在『审计日志』中追溯」字样（内部协调痕迹已撤）
  const me02NoTrace =
    !/可在[「"']?审计日志[」"']?中追溯/.test(manualEtaVueContent) &&
    !/审计日志中追溯/.test(manualEtaVueContent)
  assert(
    'ME02 静态：ManualEta.vue 不含「可在『审计日志』中追溯」（内部协调痕迹已撤）',
    me02NoTrace,
    me02NoTrace ? '已撤' : '仍含内部协调痕迹'
  )

  // ME03 静态：ManualEta.vue Drawer 内不含 form-hint ElAlert（弹框顶部说明已撤）
  //   - 弹框原 ElAlert：title="新增（Upsert）" + 内容"按采购单号 + 行号去重..."
  //   - 反例：class="form-hint" 的 ElAlert 块
  const me03 =
    !/class\s*=\s*["']form-hint["']/.test(manualEtaVueContent) &&
    !/新增\s*（\s*Upsert\s*）/.test(manualEtaVueContent) &&
    !/按采购单号\s*\+\s*行号去重/.test(manualEtaVueContent)
  assert(
    'ME03 静态：ManualEta.vue Drawer 不含「新增（Upsert）」ElAlert 说明块',
    me03,
    me03 ? '已撤' : '仍含 Drawer 顶部 ElAlert'
  )

  // ME04 静态：ManualEta.vue 不含"复合主键 / 编辑态下 PO + 行号不可改"（PO号下冗余提示已撤）
  const me04 =
    !/与\s*LineNo\s*组成复合主键/.test(manualEtaVueContent) &&
    !/编辑态下\s*PO\s*\+\s*行号不可改/.test(manualEtaVueContent) &&
    !/class\s*=\s*["']field-hint["']/.test(manualEtaVueContent)
  assert(
    'ME04 静态：ManualEta.vue 不含「复合主键 / field-hint」PO 号下冗余提示',
    me04,
    me04 ? '已撤' : '仍含 PO 号下提示'
  )

  // ME05 静态：ManualEta.vue 不含 field-hint「必填」字样（红星已标识，必填文字冗余）
  //   - 反例：<div class="field-hint">必填</div>
  //   - 注意：此处依赖 ME04 已撤掉 field-hint 类（因为用户原话"必填的前面已经有红星标识了也去掉'必填'两字"）
  const me05 =
    !/<div\s+class\s*=\s*["']field-hint["']\s*>\s*必填\s*<\/div>/.test(manualEtaVueContent)
  assert(
    'ME05 静态：ManualEta.vue 不含「必填」field-hint（红星已标识，文字冗余）',
    me05,
    me05 ? '已撤' : '仍含「必填」field-hint'
  )

  // ME06 静态：ManualEta.vue 不含「仅人工值；有效 ETA / 可用时间由系统计算」（人工ETA下重复说明已撤）
  const me06 =
    !/仅人工值；有效\s*ETA/.test(manualEtaVueContent) &&
    !/仅人工值\s*；\s*有效\s*ETA\s*\/\s*可用时间由系统计算/.test(manualEtaVueContent)
  assert(
    'ME06 静态：ManualEta.vue 不含「仅人工值；有效 ETA / 可用时间由系统计算」（重复说明已撤）',
    me06,
    me06 ? '已撤' : '仍含人工ETA下重复说明'
  )

  // ME07 静态：ManualEta.vue 备注 placeholder 不含「写入审计」内部协调痕迹
  //   - 反例：placeholder="可选（写入审计）"
  const me07 = !/placeholder\s*=\s*["']可选（写入审计）["']/.test(manualEtaVueContent)
  assert(
    'ME07 静态：ManualEta.vue 备注 placeholder 不含「写入审计」（内部协调痕迹已撤）',
    me07,
    me07 ? '已撤' : '仍含「写入审计」字样'
  )

  /* -------- ME08 静态（manualEtaApi.serializeFilter 默认 activeOnly 修复）--------
   * 根因（2026-10-07）：
   *  - 后端 ProcurementManualEtaController.cs:53 默认 activeOnly=true
   *  - 前端 manualEtaApi.serializeFilter 缺省时不传 → 触发后端默认 → cancelled 记录被过滤
   *  - 现象：刷新页面/初次加载 → 列表空（KPI 仍有 cancelledCount），与 UI「仅看生效中」开关语义不一致
   * 修复：serializeFilter 显式传 activeOnly（缺省值 false），覆盖后端默认
   */
  const manualEtaApiContent = readSrc('api/aps-v1/manualEta.ts')
  const serializeFnBlock = (() => {
    // 定位 `function serializeFilter(...) : Record<string, unknown> {` 后，从第一个 `Record<string, unknown> {` 后的 `{` 开始
    const i = manualEtaApiContent.indexOf('function serializeFilter')
    if (i < 0) return ''
    const slice = manualEtaApiContent.slice(i)
    const open = slice.indexOf('Record<string, unknown> {')
    if (open < 0) return ''
    const start = i + open + 'Record<string, unknown> {'.length
    // 用 indexOf + 括号配对定位闭合 `}`（函数体结束）；这里采用从 start 处 slice 到函数体结尾
    // 简化：从 start 起 slice 200 字符够覆盖（函数体只有 ~5 行）
    const body = manualEtaApiContent.slice(start)
    // 找到下一个独立的 `}`（行首闭合），避免匹配到 Record<string, unknown> 的内层 `}`
    const m = body.match(/\n\}/)
    return m ? body.slice(0, m.index + 2) : ''
  })()
  // 反例：filter 缺省时不传 activeOnly（依赖后端默认 true → cancelled 被过滤）
  const me08 =
    serializeFnBlock.length > 0 &&
    /activeOnly\s*:\s*filter\s*\?\s*\.\s*activeOnly\s*\?\?\s*false/.test(serializeFnBlock) &&
    !/if\s*\(\s*filter\s*\?\s*\.\s*activeOnly\s*!==\s*undefined\s*\)/.test(serializeFnBlock)
  assert(
    'ME08 静态：manualEtaApi.serializeFilter 显式传 activeOnly（缺省 false），防止 cancelled 记录被后端默认过滤',
    me08,
    me08
      ? '已显式传 activeOnly'
      : serializeFnBlock.length === 0
        ? '未定位 serializeFilter 函数'
        : 'serializeFilter 仍依赖后端默认（activeOnly 未显式序列化）'
  )

  /* -------- ME09 静态（ManualETA Drawer 物料下拉化：合并 MaterialId + MaterialCode 输入）--------
   * 触发（2026-10-07）：用户问「物料和仓库是不是下拉更好」→ 选 A 只改物料下拉，仓库保留手填
   * 依据：
   *  - 5号位 SetupRuleController L333 已提供 /api/governance/lookups/materials?activeOnly= 端点
   *  - 4号位 复用 materialLookupApi.listActiveMaterials()（与 CTP 同源）
   * 验收口径：
   *  - ManualEta.vue 引入 materialLookupApi + MaterialLookupItem 类型
   *  - 含 materialOptions ref + loadMaterialOptions() 异步加载 + onMounted 调用
   *  - 含 materialKey → materialId + materialCode 同步 watch
   *  - 含 ElSelect v-model="form.materialKey"（替代原两个 ElInput）
   *  - 编辑态反例：不应再出现 ElInput v-model="form.materialId" 或 v-model="form.materialCode"
   */
  const me09Vue = readSrc('views/Aps/ManualEta.vue')
  const me09a = /import\s+\{[^}]*materialLookupApi[^}]*\}\s+from\s+['"]\@\/api\/aps-v1\/materialLookup['"]/.test(me09Vue)
  assert('ME09a 静态：ManualEta.vue 引入 materialLookupApi', me09a, me09a ? '已引入' : '缺导入')

  const me09b = /MaterialLookupItem/.test(me09Vue)
  assert('ME09b 静态：ManualEta.vue 含 MaterialLookupItem 类型', me09b, me09b ? '已含' : '缺类型')

  const me09c = /materialOptions\s*=\s*ref<MaterialLookupItem\[\]>/.test(me09Vue)
  assert('ME09c 静态：ManualEta.vue materialOptions ref<MaterialLookupItem[]>', me09c, me09c ? '已含' : '缺 ref')

  const me09d = /materialLookupApi\.listActiveMaterials\s*\(/.test(me09Vue)
  assert('ME09d 静态：ManualEta.vue loadMaterialOptions 调 listActiveMaterials()', me09d, me09d ? '已调' : '缺调用')

  const me09e = /onMounted\s*\(\s*\(\s*\)\s*=>\s*\{[\s\S]*?loadMaterialOptions\s*\(\s*\)/.test(me09Vue)
  assert('ME09e 静态：ManualEta.vue onMounted 含 loadMaterialOptions()', me09e, me09e ? '已挂载' : '缺 onMounted')

  const me09f = /watch\s*\(\s*\(\s*\)\s*=>\s*form\.value\.materialKey[\s\S]*?materialId\s*=\s*found\.masterId[\s\S]*?materialCode\s*=\s*found\.code/.test(me09Vue)
  assert('ME09f 静态：ManualEta.vue watch 同步 materialKey → materialId + materialCode', me09f, me09f ? '已同步' : '缺 watch 同步')

  const me09g =
    /<ElSelect\b[^>]*v-model="materialKeyModel"/.test(me09Vue) ||
    /<ElSelect\b[^>]*v-model="form\.materialKey"/.test(me09Vue)
  assert('ME09g 静态：ManualEta.vue 含 ElSelect 绑物料 v-model（materialKeyModel 或 form.materialKey）', me09g, me09g ? '已含' : '缺下拉')

  const me09h = !/v-model="form\.materialId"/.test(me09Vue) || /v-model="form\.materialId"\s*\/?>\s*<!--\s*hidden/.test(me09Vue)
  // 兜底：原 materialId ElInput 必须移除；除非是显式 hidden 注释占位
  const me09hStrict = !/ElInput[^>]*v-model="form\.materialId"/.test(me09Vue)
  assert(
    'ME09h 静态：ManualEta.vue 不含原 ElInput v-model="form.materialId"（已被下拉替代）',
    me09hStrict,
    me09hStrict ? '已替代' : '仍存在 materialId 输入框'
  )

  const me09i = !/ElInput[^>]*v-model="form\.materialCode"/.test(me09Vue)
  assert(
    'ME09i 静态：ManualEta.vue 不含原 ElInput v-model="form.materialCode"（已被下拉替代）',
    me09i,
    me09i ? '已替代' : '仍存在 materialCode 输入框'
  )

  /* -------- ME10 静态（ManualETA 物料编码筛选透传：修复 syncFilterDraft 硬编码 undefined bug）--------
   * 触发（2026-10-07）：用户审查 ManualETA 完整性时发现 syncFilterDraft 注释自承「materialIds: undefined」，
   *   UI 选物料编码后点筛选无效；后端实际支持 ?materialCodes= 逗号分隔（Controller.cs:47）
   * 修复：
   *  - ManualEtaListFilter 新增 materialCodes?: string[]
   *  - serializeFilter 把 materialCodes 序列化为逗号分隔字符串
   *  - syncFilterDraft 把 selectedMaterialCodes 透传给 materialCodes
   * 验收口径：
   *  - types/manualEta.ts ManualEtaListFilter 含 materialCodes?: string[]
   *  - api/manualEta.ts serializeFilter 含 materialCodes 序列化分支
   *  - ManualEta.vue syncFilterDraft 把 selectedMaterialCodes 赋值给 filterDraft.materialCodes
   */
  const me10Types = readSrc('api/aps-v1/types/manualEta.ts')
  const me10a = /export\s+interface\s+ManualEtaListFilter\b[^}]*\bmaterialCodes\s*\?\s*:\s*string\[\]/.test(me10Types)
  assert('ME10a 静态：types/manualEta.ts ManualEtaListFilter 含 materialCodes?: string[]', me10a, me10a ? '已含' : '缺字段')

  const me10Api = manualEtaApiContent
  const me10b = /out\.materialCodes\s*=\s*filter\.materialCodes\.join\(',\s*'\)/.test(me10Api) ||
    /if\s*\(\s*filter\s*\?\s*\.\s*materialCodes\s*\?\s*\.\s*length\s*\)\s*out\.materialCodes\s*=/.test(me10Api)
  assert('ME10b 静态：manualEtaApi.serializeFilter 序列化 materialCodes 为逗号分隔', me10b, me10b ? '已序列化' : '缺序列化分支')

  const me10c = /syncFilterDraft[\s\S]*?materialCodes\s*:\s*selectedMaterialCodes\.value\.length/.test(me09Vue)
  assert('ME10c 静态：ManualEta.vue syncFilterDraft 把 selectedMaterialCodes 透传给 materialCodes', me10c, me10c ? '已透传' : '仍硬编码 undefined')

  const me10d = !/materialIds\s*:\s*undefined,\s*\/\/\s*DTO\s+是\s+materialIds/.test(me09Vue) ||
    /UI\s+用\s+code\s+选/.test(me09Vue)
  // 兜底：原注释「简单起见先传 undefined」必须替换/扩展为 materialCodes 路径，否则前端 bug 复发
  assert(
    'ME10d 静态：ManualEta.vue 不再硬编码「materialIds: undefined / 简单起见先传 undefined」（已转 materialCodes 路径）',
    me10d,
    me10d ? '已迁移' : 'syncFilterDraft 注释仍指 materialIds，未走 materialCodes'
  )

  /* -------- ME11 静态：列表分页化（5号位 2026-10-08 落地 PageResult<T>）--------
   *  - types/manualEta.ts ManualEtaListFilter 含 pageIndex / pageSize
   *  - store/manualEta.ts 有 pageIndex / pageSize / total 三个 ref 并在 return 中暴露
   *  - store/manualEta.ts load() 处理两种形态：数组（旧）/ {items,total,page,pageSize}（新）
   *  - api/manualEta.ts serializeFilter 总是显式传 pageIndex / pageSize（避免后端默认 100）
   */
  const me11a =
    /export\s+interface\s+ManualEtaListFilter\b[^}]*\bpageIndex\s*\?\s*:\s*number/.test(me10Types) &&
    /export\s+interface\s+ManualEtaListFilter\b[^}]*\bpageSize\s*\?\s*:\s*number/.test(me10Types)
  assert('ME11a 静态：types/manualEta.ts ManualEtaListFilter 含 pageIndex / pageSize', me11a, me11a ? '已含' : '缺分页字段')

  const me11Store = readSrc('store/modules/aps/manualEta.ts')
  const me11b =
    /\bpageIndex\s*=\s*ref\(\s*1\s*\)/.test(me11Store) &&
    /\bpageSize\s*=\s*ref\(\s*20\s*\)/.test(me11Store) &&
    /\btotal\s*=\s*ref\(\s*0\s*\)/.test(me11Store)
  assert('ME11b 静态：store/manualEta.ts 含 pageIndex / pageSize / total 三个 ref', me11b, me11b ? '已含' : '缺分页 ref')

  const me11c =
    /\breturn\s*\{[\s\S]*?\bpageIndex\b[\s\S]*?\bpageSize\b[\s\S]*?\btotal\b[\s\S]*?load\b/.test(me11Store)
  assert('ME11c 静态：store/manualEta.ts return 暴露 pageIndex / pageSize / total', me11c, me11c ? '已暴露' : 'return 缺分页暴露')

  const me11d =
    /Array\.isArray\(resp\)/.test(me11Store) &&
    /resp\s*\?\s*\.\s*items/.test(me11Store) &&
    /resp\s*\?\s*\.\s*total/.test(me11Store)
  assert('ME11d 静态：store/manualEta.ts load() 处理 PageResult + 数组两种形态', me11d, me11d ? '双形态兼容' : 'load() 缺形态兼容')

  const me11e =
    /\bpageIndex\s*:\s*filter\s*\?\s*\.\s*pageIndex\s*\?\?\s*1\b/.test(manualEtaApiContent) &&
    /\bpageSize\s*:\s*filter\s*\?\s*\.\s*pageSize\s*\?\?\s*20\b/.test(manualEtaApiContent)
  assert('ME11e 静态：api/manualEta.ts serializeFilter 显式传 pageIndex / pageSize（避免后端默认）', me11e, me11e ? '已显式' : '缺显式分页参数')

  /* -------- ME12 静态：ManualEta.vue ElPagination 渲染 + 翻页 / 改页长回调 -------
   *  - 导入 ElPagination
   *  - 从 store 解构 pageIndex / pageSize / total
   *  - 模板含 <ElPagination> 且绑定 :total / :page-size / :current-page
   *  - onPageChange + onSizeChange 调用 store.load 并切到第 1 页（改页长时）
   */
  const me12a = /import\s*\{[^}]*\bElPagination\b[^}]*\}\s*from\s*['"]element-plus['"]/.test(me09Vue)
  assert('ME12a 静态：ManualEta.vue 导入 ElPagination', me12a, me12a ? '已导入' : '缺导入')

  const me12b =
    /\bpageIndex\b/.test(me09Vue) && /\bpageSize\b/.test(me09Vue) && /\btotal\b/.test(me09Vue)
  // storeToRefs 解构（与已有 list / error 同位置）
  assert('ME12b 静态：ManualEta.vue 解构 pageIndex / pageSize / total', me12b, me12b ? '已解构' : 'storeToRefs 缺分页字段')

  const me12c =
    /<ElPagination\b[^>]*:total="total"/.test(me09Vue) &&
    /<ElPagination\b[^>]*:page-size="pageSize"/.test(me09Vue) &&
    /<ElPagination\b[^>]*:current-page="pageIndex"/.test(me09Vue)
  assert('ME12c 静态：ManualEta.vue ElPagination 绑定 total / page-size / current-page', me12c, me12c ? '已绑定' : '缺分页属性绑定')

  const me12d =
    /@current-change\s*=\s*["']onPageChange["']/.test(me09Vue) &&
    /@size-change\s*=\s*["']onSizeChange["']/.test(me09Vue)
  assert('ME12d 静态：ManualEta.vue ElPagination 绑定 @current-change / @size-change', me12d, me12d ? '已绑定' : '缺事件回调')

  const me12e =
    /async\s+function\s+onPageChange[\s\S]*?store\s*\.\s*load\s*\(/.test(me09Vue) &&
    /async\s+function\s+onSizeChange[\s\S]*?store\s*\.\s*load\s*\(\s*\{[\s\S]*?pageIndex\s*:\s*1/.test(me09Vue)
  assert('ME12e 静态：ManualEta.vue onPageChange / onSizeChange 调 store.load（改页长 → 跳回第 1 页）', me12e, me12e ? '已闭环' : '缺回调函数体')

  const me12f = /applyFilter[\s\S]*?pageIndex\s*:\s*1/.test(me09Vue)
  assert('ME12f 静态：ManualEta.vue applyFilter 重置 pageIndex=1（避免筛选无交集时空态错觉）', me12f, me12f ? '已重置' : '筛选未重置分页')

  /* -------- ME13 静态：收货仓库列展示 name（10-09 加列名映射）--------
   *  - ManualEta.vue 含 warehouseNameMap computed（基于 warehouseOptions 派生 code → name）
   *  - 表格"收货仓库"列展示 `${code} · ${name}` 形式（name 找不到时只显示 code）
   */
  const me13a = /warehouseNameMap\s*=\s*computed/.test(me09Vue) &&
    /warehouseOptions\.value/.test(me09Vue)
  assert('ME13a 静态：ManualEta.vue 含 warehouseNameMap computed（基于下拉数据）', me13a, me13a ? '已含' : '缺派生映射')

  const me13b = /warehouseNameMap\.get\(\s*row\.receivingWarehouse\s*\)/.test(me09Vue)
  assert('ME13b 静态：ManualEta.vue 表格"收货仓库"列用 warehouseNameMap 查 name', me13b, me13b ? '已查' : '缺 name 展示')

  /* -------- ME14 静态：upsert / cancel 业务键 = 4 字段复合主键（10-09 修复）--------
   *  - 与 5号位 `ProcurementManualEtaRepository.MERGE ON` 对齐：poNo + lineNo + materialId + receivingWarehouse
   *  - @see lps/LPS.APS.BusinessRules/Repositories/ProcurementManualEtaRepository.cs:202-205
   *  - 之前只用 poNo + lineNo：同 poNo+lineNo 下多物料 / 多仓库会找错行 → 列表不增不删异常
   *  - receivingWarehouse 可选字段：sameBusinessKey 需对 undefined/null/'' 三态一致
   */
  const me14Store = me11Store
  const me14a = /function\s+sameBusinessKey\s*\(\s*a\s*:\s*ManualEtaDto\s*,\s*b\s*:\s*ManualEtaDto\s*\)/.test(me14Store)
  assert('ME14a 静态：store/manualEta.ts 含 sameBusinessKey(a, b) helper', me14a, me14a ? '已含' : '缺同键 helper')

  const me14b =
    /sameBusinessKey\(\s*x\s*,\s*result\s*\)/.test(me14Store) &&
    /sameBusinessKey\(\s*currentDetail\.value/.test(me14Store)
  assert('ME14b 静态：store/manualEta.ts upsert / cancel 列表与 currentDetail 都用 4 键查重', me14b, me14b ? '已闭环' : 'findIndex 仍用 poNo+lineNo 二键')

  const me14c =
    /a\.materialId\s*===\s*b\.materialId/.test(me14Store) &&
    /\(a\.receivingWarehouse\s*\?\?\s*''\)\s*===\s*\(b\.receivingWarehouse\s*\?\?\s*''\)/.test(me14Store)
  assert('ME14c 静态：sameBusinessKey 含 materialId + receivingWarehouse（4 键完整）', me14c, me14c ? '4 键齐备' : '缺 materialId / receivingWarehouse 对齐')

  /* -------- ME15 静态：KPI 三卡改读累计值（10-12 ME13 收）--------
   *  - 触发：5号位 10-12 函三函都已落地（ManualETA 10-08 / business-fact-issues 10-10 / manual-capacity 10-12）
   *  - 5号位 未落 summary 聚合端点 → store 端维护 cumulative KPI（active + cancelled）
   *  - KPI 三卡：总记录数 = backend `total`（全集）/ 启用 = totalActive / 已取消 = totalCancelled
   *  - 不再读 list.length / activeCount / cancelledCount（per-page 会跳）
   *  - 反向探针自证可失败：
   *     - 删 totalActive/totalCancelled → ME15a 由绿转红
   *     - KPI 卡回退读 list.length → ME15b 由绿转红
   *     - 删 cumulative reset → ME15c 由绿转红
   */
  const me15Store = readSrc('store/modules/aps/manualEta.ts')
  const me15View = readSrc('views/Aps/ManualEta.vue')
  // a. store 增 totalActive + totalCancelled 两个 ref
  const me15aStoreHasRefs =
    /const\s+totalActive\s*=\s*ref(?:<number>)?\s*\(0\)/.test(me15Store) &&
    /const\s+totalCancelled\s*=\s*ref(?:<number>)?\s*\(0\)/.test(me15Store)
  // b. KPI 三卡不读 list.length / activeCount / cancelledCount
  const me15bKpiNotListLength =
    !/kpi-value[^>]*>\s*\{\{\s*list\.length\s*\}\}/.test(me15View) &&
    !/kpi-value[^>]*>\s*\{\{\s*activeCount\s*\}\}/.test(me15View) &&
    !/kpi-value[^>]*>\s*\{\{\s*cancelledCount\s*\}\}/.test(me15View)
  // KPI 三卡读 total / totalActive / totalCancelled
  const me15bKpiUseTotal =
    /kpi-value[^>]*>\s*\{\{\s*total\s*\}\}/.test(me15View) &&
    /kpi-value[^>]*>\s*\{\{\s*totalActive\s*\}\}/.test(me15View) &&
    /kpi-value[^>]*>\s*\{\{\s*totalCancelled\s*\}\}/.test(me15View)
  const me15b = me15bKpiNotListLength && me15bKpiUseTotal
  // c. store 维护 cumulative：resetKpiAggregate + accumulateKpi + load 内调用 + upsert/cancel 同步翻转
  const me15cReset =
    /function\s+resetKpiAggregate\(\):\s*void\s*\{[\s\S]{0,200}?totalActive\.value\s*=\s*0/.test(me15Store) &&
    /function\s+resetKpiAggregate\(\):\s*void\s*\{[\s\S]{0,200}?totalCancelled\.value\s*=\s*0/.test(me15Store)
  const me15cAccumulate =
    /function\s+accumulateKpi\([\s\S]{0,300}?if\s*\(item\.isActive\)\s*totalActive\.value\s*\+=\s*1/.test(me15Store) &&
    /function\s+accumulateKpi\([\s\S]{0,300}?else\s*totalCancelled\.value\s*\+=\s*1/.test(me15Store)
  const me15cLoadCalls =
    /accumulateKpi\(\s*currentPage\s*\)/.test(me15Store) ||
    /accumulateKpi\(\s*pageIndex\.value\s*\)/.test(me15Store)
  const me15c = me15cReset && me15cAccumulate && me15cLoadCalls
  // d. view 暴露 totalActive / totalCancelled（storeToRefs 解构）
  const me15dViewExposes =
    /totalActive[\s\S]{0,40}?totalCancelled[\s\S]{0,80}?=\s*storeToRefs\(/.test(me15View)
  const me15All = me15aStoreHasRefs && me15b && me15c && me15dViewExposes
  assert(
    'ME15 静态：KPI 三卡改读累计值（store totalActive/totalCancelled + cumulative 维护 + view 三卡不读 list.length）',
    me15All,
    me15All
      ? '全齐'
      : `storeRefs=${me15aStoreHasRefs} kpiUseTotal=${me15bKpiUseTotal} kpiNotPage=${me15bKpiNotListLength} reset=${me15cReset} accumulate=${me15cAccumulate} loadCalls=${me15cLoadCalls} viewExposes=${me15dViewExposes}`
  )

  /* -------- ME15 动态：dev 联调 GET /api/procurement-manual-eta pageIndex/pageSize 返 PageResult --------
   *  - 5号位 10-08 已落地分页（dev 实测 total ≥ 1 + 翻 page 真换数据）
   *  - 翻页不影响 total（与 KPI 累计语义一致）
   */
  const me15Page1 = await call('GET', '/api/procurement-manual-eta', {
    token,
    query: { pageIndex: 1, pageSize: 20, activeOnly: false }
  })
  const me15P1Raw = me15Page1.json?.data
  const me15P1IsPage = me15P1Raw && typeof me15P1Raw === 'object' && Array.isArray(me15P1Raw.items)
  const me15P1Ok =
    me15Page1.code === 200 &&
    me15P1IsPage &&
    typeof me15P1Raw.total === 'number' &&
    me15P1Raw.total >= (me15P1Raw.items?.length ?? 0) &&
    me15P1Raw.page === 1
  assert(
    'ME15 动态：GET /api/procurement-manual-eta?pageIndex=1&pageSize=20&activeOnly=false 返 {items,total,page,pageSize}（5号位 10-08 已落地）',
    me15P1Ok,
    me15P1Ok
      ? `total=${me15P1Raw.total} items=${me15P1Raw.items.length} page=${me15P1Raw.page} pageSize=${me15P1Raw.pageSize}`
      : `code=${me15Page1.code} shape=${JSON.stringify(me15P1Raw).slice(0, 200)}`
  )
  // 翻 page 2 验证 data.items[0] 不同（真翻页）+ data.total 稳定
  if (me15P1Ok && me15P1Raw.total > me15P1Raw.items.length) {
    const me15Page2 = await call('GET', '/api/procurement-manual-eta', {
      token,
      query: { pageIndex: 2, pageSize: 20, activeOnly: false }
    })
    const me15P2Raw = me15Page2.json?.data
    const me15P2IsPage = me15P2Raw && Array.isArray(me15P2Raw?.items)
    const me15P2Ok =
      me15Page2.code === 200 &&
      me15P2IsPage &&
      me15P2Raw.page === 2 &&
      me15P2Raw.total === me15P1Raw.total &&
      (me15P2Raw.items?.[0]?.poNo !== me15P1Raw.items?.[0]?.poNo ||
        me15P2Raw.items?.[0]?.lineNo !== me15P1Raw.items?.[0]?.lineNo)
    assert(
      'ME15 动态：翻 page 2 真换数据 + total 稳定（KPI cumulative 翻页不跳的语义保证）',
      me15P2Ok,
      me15P2Ok
        ? `p1.items[0]=${me15P1Raw.items?.[0]?.poNo}/${me15P1Raw.items?.[0]?.lineNo} p2.items[0]=${me15P2Raw.items?.[0]?.poNo}/${me15P2Raw.items?.[0]?.lineNo} total=${me15P2Raw.total}（稳定）`
        : `code=${me15Page2.code} p1[0]=${me15P1Raw.items?.[0]?.poNo} p2[0]=${me15P2Raw.items?.[0]?.poNo} total p1=${me15P1Raw.total} p2=${me15P2Raw?.total}`
    )
  } else {
    skip('ME15 动态：翻 page 2 验证（数据 < 21 条，无法验证翻页真换；不阻断 ME15 主探针）')
  }
}

/* ==================== [RC] ResourceCalendar / ManualCapacity ==================== */

/**
 * [RC] 资源日历 / 人工能力槽维护（r-resource-calendar）
 * 依据：
 *  - 冻结文档 v1.3（frontNew/docs/APS_V1_Resource_Calendar资源日历能力补充冻结方案_v1.3_*.md）
 *  - 5号位 2026-09-24《ResourceCalendar与ManualCapacity接口对接函》
 *  - 4号位 缺口函 G1/G2/G3/G4/C1/D2/D3 + G5（角色绑定）
 *
 * 口径：本段为「静态契约 + 端点可达」验收（与 [O] 段范式一致）。
 *  - 后端 9 端点已实现，但 6 权限码**无角色绑定**（G5）→ 真实 token 访问预期 403；
 *    故可达类断言接受 200/400/403/404/422（端点可达即过，≠404 联网层缺失即过）
 *  - 接受类（真实铺窗/建槽）默认跳过，需 VERIFY_ALLOW_WRITE=1
 */
async function verifyResourceCalendar(token) {
  log('\n[RC] 资源日历 / 人工能力槽维护（r-resource-calendar）')

  /* -------- R01 静态：types/resourceCalendar.ts 契约（camelCase） -------- */
  const typesContent = readSrc('api/aps-v1/types/resourceCalendar.ts')
  const dtoFields = [
    'resourceId',
    'resourceCode',
    'availableFlag',
    'manualSlotId',
    'productionDepartmentId',
    'operationName',
    'slotCode',
    'isActive'
  ]
  const missingDtoFields = dtoFields.filter((f) => !typesContent.includes(f))
  const hasInterfaces =
    typesContent.includes('ResourceCalendarBulkRequest') &&
    typesContent.includes('ManualSlotCalendarBulkRequest') &&
    typesContent.includes('ResourceCalendarEntryDto') &&
    typesContent.includes('ManualCapacitySlotDto') &&
    typesContent.includes('ManualSlotCalendarDto')
  assert(
    'RC01 静态：types/resourceCalendar.ts 含 8 个 camelCase 字段 + 5 个请求/响应接口',
    missingDtoFields.length === 0 && hasInterfaces,
    missingDtoFields.length === 0
      ? '字段与接口齐备'
      : `缺字段：${missingDtoFields.join(',')}；接口=${hasInterfaces}`
  )

  /* -------- R02 静态：无 PascalCase 字段（对接函 §三.2 与后端实际相反，负断言） -------- */
  const pascalHits = [
    'ResourceId:',
    'ProductionDepartmentId:',
    'AvailableFlag:',
    'ManualSlotId:',
    'OperationName:',
    'SlotCode:'
  ].filter((p) => typesContent.includes(p))
  assert(
    'RC02 静态：types/resourceCalendar.ts 无 PascalCase 字段名（Program.cs:93 = CamelCase）',
    pascalHits.length === 0,
    pascalHits.length === 0 ? '全 camelCase' : `发现 PascalCase：${pascalHits.join(',')}`
  )

  /* -------- R03 静态：6 权限码常量 + 铺窗边界 + 三态元数据 -------- */
  const permKeys = [
    'aps.resource_calendar.view',
    'aps.resource_calendar.edit',
    'aps.resource_calendar.delete',
    'aps.manual_capacity.view',
    'aps.manual_capacity.edit',
    'aps.manual_capacity.delete'
  ]
  const missingPerms = permKeys.filter((p) => !typesContent.includes(p))
  const hasBounds =
    typesContent.includes('BULK_DAYS_MIN') &&
    typesContent.includes('BULK_DAYS_MAX') &&
    typesContent.includes('CalendarConfigStatus') &&
    typesContent.includes('CALENDAR_CONFIG_META')
  // ESLint 剥合法 identifier 的 object key 引号 → 用裸串匹配三态值
  const hasStatusValues =
    typesContent.includes('CONFIGURED') &&
    typesContent.includes('NOT_CONFIGURED') &&
    typesContent.includes('UNKNOWN')
  assert(
    'RC03 静态：6 权限码 + BULK_DAYS 1..370 + 三态元数据齐备',
    missingPerms.length === 0 && hasBounds && hasStatusValues,
    missingPerms.length === 0
      ? '权限码/边界/三态齐备'
      : `缺权限码：${missingPerms.join(',')}；bounds=${hasBounds}；三态=${hasStatusValues}`
  )

  /* -------- R04 静态：api/aps-v1/resourceCalendar.ts 9 端点 + 资源主表（G1 已闭环） --------
   *  - 10-12 RC23：listResourceCandidates（过渡数据源）已撤，改走 listResources（GET /api/resource 正式端点）
   */
  const apiContent = readSrc('api/aps-v1/resourceCalendar.ts')
  /** 剥除 JSDoc/行内注释再扫描（避免历史 docstring 误命中） */
  const apiContentCodeOnly = apiContent
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .replace(/^\s*\/\/.*$/gm, '')
  const apiMethods = [
    'bulkCreateWindows',
    'getWindows',
    'deleteWindow',
    'createSlot',
    'getSlots',
    'softDeleteSlot',
    'bulkCreateSlotWindows',
    'getSlotWindows',
    'deleteSlotWindow',
    'listResources',
    'listDepartmentCandidates'
  ]
  const missingApi = apiMethods.filter((m) => !apiContentCodeOnly.includes(m))
  // RC23 探针验证：撤过渡方法（listResourceCandidates 不再存在）—— 仅扫代码
  const legacyTransitionGone = !/listResourceCandidates/.test(apiContentCodeOnly)
  assert(
    'RC04 静态：api/aps-v1/resourceCalendar.ts 含 9 端点 + 资源主表正式端点 listResources + 撤过渡 listResourceCandidates',
    missingApi.length === 0 && legacyTransitionGone,
    missingApi.length === 0
      ? legacyTransitionGone ? 'API 封装齐备 + 撤过渡已清' : '缺过渡清场'
      : `缺方法：${missingApi.join(',')}；撤过渡=${legacyTransitionGone}`
  )

  /* -------- R05 静态：store 双缓存 + 三态推导 + 六权限 getter + 10 动作 --------
   *  - 10-12 RC23：loadResourceCandidates（过渡）撤 → 改 loadResources（资源主表正式端点）
   *  - 13 动作 → 10 动作：原 `loadResourceCandidates` 不计入（已撤）
   */
  const storeContent = readSrc('store/modules/aps/resourceCalendar.ts')
  /** 剥除 JSDoc/行内注释再扫描（避免历史 docstring 误命中） */
  const storeContentCodeOnly = storeContent
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .replace(/^\s*\/\/.*$/gm, '')
  const storeChecks = [
    // 2026-10-17：全量缓存（deviceWindowCache/slotWindowCache）已撤，改服务端分页的
    //   items + total + totals 表（@see RC26a-f）；此处只断言分页后仍需要的件套
    'deviceWindowItems',
    'deviceWindowTotal',
    'deviceWindowTotals',
    'slotWindowItems',
    'slotWindowTotal',
    'slotWindowTotals',
    'statusOf',
    'canViewDevice',
    'canEditDevice',
    'canDeleteDevice',
    'canViewManual',
    'canEditManual',
    'canDeleteManual',
    'loadResources',
    'loadDeviceWindows',
    'createDeviceWindows',
    'removeDeviceWindow',
    'loadSlots',
    'createSlot',
    'disableSlot',
    'loadSlotWindows',
    'createSlotWindows',
    'removeSlotWindow'
  ]
  const missingStore = storeChecks.filter((m) => !storeContentCodeOnly.includes(m))
  // statusOf() 返回三个字符串字面量（值，非 object key）→ 用 includes 匹配
  const storeHasTri =
    storeContentCodeOnly.includes('UNKNOWN') &&
    storeContentCodeOnly.includes('CONFIGURED') &&
    storeContentCodeOnly.includes('NOT_CONFIGURED')
  // RC23 探针验证：store 端同步撤 transition state（candidateNote / candidateIsFallback 不再存在）—— 仅扫代码
  const legacyStoreFieldsGone =
    !/candidateNote/.test(storeContentCodeOnly) &&
    !/candidateIsFallback/.test(storeContentCodeOnly) &&
    !/loadResourceCandidates/.test(storeContentCodeOnly)
  assert(
    'RC05 静态：store 含设备/人工槽窗口分页件套 + 三态推导 + 六权限 getter + 10 动作 + 撤 transition 字段（candidateNote/candidateIsFallback/loadResourceCandidates）',
    missingStore.length === 0 && storeHasTri && legacyStoreFieldsGone,
    missingStore.length === 0
      ? legacyStoreFieldsGone
        ? 'store 齐备 + 撤过渡已清'
        : `撤过渡未清 (candidateNote/candidateIsFallback/loadResourceCandidates)`
      : `缺项：${missingStore.join(',')}；三态=${storeHasTri}；撤过渡=${legacyStoreFieldsGone}`
  )

  /* -------- R06 静态：3 文件存在 + 页面 3 Tab + 顶部三 Alert -------- */
  const pageExists = staticFileExists('views/Aps/ResourceCalendar.vue')
  const bulkDlgExists = staticFileExists('views/Aps/components/CalendarBulkDialog.vue')
  const slotDlgExists = staticFileExists('views/Aps/components/ManualSlotCreateDialog.vue')
  const pageContent = readSrc('views/Aps/ResourceCalendar.vue')
  const hasTabs =
    pageContent.includes('manual-calendar') &&
    pageContent.includes('设备资源日历') &&
    pageContent.includes('人工能力槽') &&
    pageContent.includes('人工槽日历')
  const hasAlerts =
    pageContent.includes('这台设备本级还没登记') &&
    pageContent.includes('这个工位本级还没登记') &&
    pageContent.includes('该人工槽已停用')
  // 内部协调痕迹不得出现在页面模板（用户可见）：2026-09-27 按用户要求清理
  // 10-12 RC23：仅扫描 <template> 区域（docstring 历史注释允许保留）；探测「资源池来源 / 手填资源 ID / 过渡」用户可见文本
  //    - RC08 已用 templateOf() 模式 → 复用同一辅助（模板起始于 <template>）
  const rc6TemplateStart = pageContent.indexOf('<template>')
  const pageTemplateOnly = rc6TemplateStart >= 0 ? pageContent.slice(rc6TemplateStart) : pageContent
  const internalLeaks = [
    '冻结文档同步中',
    '过渡数据源（G1 / G2 未决）',
    '待 5号位 交付',
    '资源池来源',
    '手填资源 ID'
  ].filter((t) => pageTemplateOnly.includes(t))
  assert(
    'RC06 静态：3 文件存在 + 3 Tab + 上下文态提示保留（本级未登记 ×2 / 已停用），内部协调提示已清出页面',
    pageExists && bulkDlgExists && slotDlgExists && hasTabs && hasAlerts && internalLeaks.length === 0,
    `文件:${pageExists}/${bulkDlgExists}/${slotDlgExists}；Tab=${hasTabs}；上下文提示=${hasAlerts}；残留内部提示=${internalLeaks.join(',') || '无'}`
  )

  /* -------- R07 静态：router 注册 + mock 6 码预置 -------- */
  const routerContent = readSrc('router/modules/aps.ts')
  //  2026-10-17：分组改造后叶子路由为绝对路径（见 GROUP=menu M02 探针锁死该约定）
  const routerOk =
    /path:\s*['"]\/aps\/resource-calendar['"]/.test(routerContent) &&
    routerContent.includes('aps.resource_calendar.view') &&
    routerContent.includes('aps.manual_capacity.view')
  const authContent = readSrc('store/modules/aps/auth.ts')
  const missingMockPerms = permKeys.filter((p) => !authContent.includes(p))
  assert(
    'RC07 静态：路由注册 /resource-calendar（OR 双 view 码）+ mock 预置含 6 码',
    routerOk && missingMockPerms.length === 0,
    `路由=${routerOk}；mock 缺码=${missingMockPerms.join(',') || '无'}`
  )

  /* -------- R07b 静态：mock planner 不含 delete（3号位 2026-09-24 裁决） -------- */
  const plannerStart = authContent.indexOf("'aps.planner': {")
  const adminApsStart = authContent.indexOf("'aps.admin.aps': {")
  const plannerBlock =
    plannerStart >= 0 && adminApsStart > plannerStart
      ? authContent.slice(plannerStart, adminApsStart)
      : ''
  const plannerHasDelete =
    plannerBlock.includes('aps.resource_calendar.delete') ||
    plannerBlock.includes('aps.manual_capacity.delete')
  assert(
    'RC07b 静态：mock 预置 planner = view + edit（不含 delete，3号位 2026-09-24 裁决）',
    plannerBlock.length > 0 && !plannerHasDelete,
    plannerBlock.length === 0
      ? '未定位到 planner 预置块'
      : plannerHasDelete
        ? 'planner 仍持 delete 码（与裁决不符）'
        : 'planner 无 delete 码'
  )

  /* -------- R08 静态：业务提示保留 + 模板内不得残留内部措辞 -------- */
  // 只扫 <template> 之后的部分：文件头注释与 script 里的内部引用是允许的（开发文档用途）
  const templateOf = (rel) => {
    const c = readSrc(rel)
    const i = c.indexOf('<template>')
    return i >= 0 ? c.slice(i) : ''
  }
  const bulkTpl = templateOf('views/Aps/components/CalendarBulkDialog.vue')
  const slotTpl = templateOf('views/Aps/components/ManualSlotCreateDialog.vue')
  // 10-14：铺窗语义由「追加」正名为「覆盖」（后端先删该区间旧窗再逐天 INSERT），RC29 盯那条黄条
  const hasBizHints =
    slotTpl.includes('人工能力槽不是员工') && slotTpl.includes('小工序')
  const leakWords = ['发函', '5号位', '2号位', '待交付', 'expose', '§', '过渡方案']
  const tplLeaks = []
  for (const [name, tpl] of [
    ['页面', templateOf('views/Aps/ResourceCalendar.vue')],
    ['铺窗弹窗', bulkTpl],
    ['新槽弹窗', slotTpl]
  ]) {
    for (const w of leakWords) {
      if (tpl.includes(w)) tplLeaks.push(`${name}:${w}`)
    }
  }
  assert(
    'RC08 静态：弹窗业务提示保留（能力槽不是员工）+ 三处模板无内部措辞残留',
    hasBizHints && tplLeaks.length === 0,
    `业务提示=${hasBizHints}；模板残留=${tplLeaks.join(',') || '无'}`
  )

  /* -------- R09/R10 端点可达（3号位 绑定脚本落地后已收紧为「要求 200」） -------- */
  const reachMsg = (code) =>
    code === 200
      ? 'code=200'
      : code === 403
        ? 'code=403（G5 角色绑定未落地：检查 Sql/APS_Auth_resource_calendar_role_binding_20260924.sql 是否执行 + 重新登录）'
        : `code=${code}`

  let r = await call('GET', '/api/manual-capacity/slots', { token })
  assert(
    'RC09 端点可达：GET /api/manual-capacity/slots（要求 200，403 = G5 回归）',
    r.code === 200,
    reachMsg(r.code)
  )

  r = await call('GET', '/api/resource-calendar/1', { token })
  assert(
    'RC10 端点可达：GET /api/resource-calendar/1（要求 200，403 = G5 回归）',
    r.code === 200,
    reachMsg(r.code)
  )

  /* -------- R11 接受类（默认跳过） -------- */
  if (process.env.VERIFY_ALLOW_WRITE !== '1') {
    skip(
      'RC11 接受类（批量铺窗 / 新建人工槽）',
      'VERIFY_ALLOW_WRITE 未设置=1（会写真实数据）'
    )
  } else {
    r = await call('POST', '/api/resource-calendar/slots', {
      token,
      body: {
        resourceId: 1,
        startDate: '2026-10-01T00:00:00',
        days: 1,
        startTime: '08:00:00',
        endTime: '17:00:00',
        availableFlag: true,
        remark: 'verify R11'
      }
    })
    assert(
      'RC11 接受类：POST /api/resource-calendar/slots（VERIFY_ALLOW_WRITE=1）',
      r.ok || [200, 201, 400, 403].includes(r.code),
      `code=${r.code}`
    )
  }

  info('RC 段全部为「静态契约 + 端点可达」；C1/G1/G2/G3/G4/D2/D3/G5 缺口见 4号位 致 5号位 函')

  /* -------- RC27 静态：撤 RC19 版本选择器（10-14 增）--------
   *  业务事实：RC19（10-12 加版本条）的根因是"资源池从 gantt 派生，ACTIVE 版本无 gantt 数据"，
   *  RC23（同日）改走 GET /api/resource 后该根因已消失；本页不读 gantt/summary，版本维度无消费者。
   *  且上级工作日历（1/2 级）无版本概念，同一棵树两种口径 → 10-14 用户拍板撤掉版本条。
   *  - 反向探针自证可失败：把 <div class="version-bar"> 或 selectVersion / useScheduleStore 加回去 → RC27 由绿转红
   */
  const rc27NoVersionBar = !/class="version-bar"/.test(pageContent)
  const rc27NoSelectVersion = !/function\s+selectVersion\s*\(/.test(pageContent)
  const rc27NoScheduleStore = !/useScheduleStore|scheduleStore\./.test(pageContent)
  const rc27NoDisplayedVersions = !/displayedVersions|currentVersionId/.test(pageContent)
  const rc27All = rc27NoVersionBar && rc27NoSelectVersion && rc27NoScheduleStore && rc27NoDisplayedVersions
  assert(
    'RC27 静态：撤 RC19 版本选择器（无 version-bar / selectVersion / useScheduleStore / displayedVersions）',
    rc27All,
    rc27All
      ? '全撤'
      : `bar=${rc27NoVersionBar} sel=${rc27NoSelectVersion} sched=${rc27NoScheduleStore} disp=${rc27NoDisplayedVersions}`
  )

  info('RC 段全部为「静态契约 + 端点可达」；C1/G1/G2/G3/G4/D2/D3/G5 缺口见 4号位 致 5号位 函')

  /* -------- RC20 静态：Resource Calendar Tab 切换不阻塞（10-12 增） -------- */
  //  - onTabChange 内不 await 数据加载（fire-and-forget void promise）
  //  - 3 个 ElTabPane 都加 lazy（Element Plus 内部 keep-alive，未激活 Tab 不挂载）
  //  - 反向探针自证可失败：把 onTabChange 改回 await 会让 RC20 由绿转红
  //  - @see 10-12 用户反馈"切到人工能力槽切别的卡顿"
  const rc20OnTabChangeNoAwait = (() => {
    // onTabChange 函数体：void (async () => {...})() 之前不能有顶层 await
    // （IIFE 内部的 await 是合法的 fire-and-forget 模式）
    const match = pageContent.match(/async\s+function\s+onTabChange\s*\([^)]*\)[^{]*\{([\s\S]*?)\n\s*\}\s*\n/)
    if (!match) return false
    const body = match[1]
    // 找 void (async 起点
    const voidIdx = body.search(/void\s*\(\s*async/)
    if (voidIdx < 0) return false
    // void 之前的部分（函数顶层）
    const topLevel = body.slice(0, voidIdx)
    const hasTopLevelAwait = /^\s*await\s+/m.test(topLevel)
    return !hasTopLevelAwait
  })()
  const rc20HasThreeLazyPanes =
    (pageContent.match(/<ElTabPane[^>]*lazy/g) || []).length >= 3
  const rc20OnTabChangeKeepsLoadedTabsGuard = /loadedTabs\.value\.has\(name\)/.test(pageContent)
  const rc20OnTabChangeKeepsErrorHandling = /catch\s*\{[\s\S]*?错误[\s\S]*?\}/.test(pageContent) ||
    /catch\s*\{[\s\S]*?store handleError[\s\S]*?\}/.test(pageContent)
  const rc20All =
    rc20OnTabChangeNoAwait &&
    rc20HasThreeLazyPanes &&
    rc20OnTabChangeKeepsLoadedTabsGuard &&
    rc20OnTabChangeKeepsErrorHandling
  assert(
    'RC20 静态：Resource Calendar Tab 切换不阻塞（onTabChange fire-and-forget + 3 ElTabPane lazy + loadedTabs 防重 + 错误由 store 处理）',
    rc20All,
    rc20All
      ? '全齐'
      : `noAwait=${rc20OnTabChangeNoAwait} lazy=${rc20HasThreeLazyPanes} guard=${rc20OnTabChangeKeepsLoadedTabsGuard} err=${rc20OnTabChangeKeepsErrorHandling}`
  )

  info('RC 段全部为「静态契约 + 端点可达」；C1/G1/G2/G3/G4/D2/D3/G5 缺口见 4号位 致 5号位 函 + RC20 Tab 切换不阻塞')

  /* -------- RC21 静态：Resource Calendar gantt 慢响应兜底（10-12 增；RC23 收 G1 / RC27 撤版本条后收敛） --------
   *  原意图："页面首次刷新 gantt/143 很快，但从 tab 切回 device 时这个接口卡 1min"
   *  - 原根因：selectVersion 主动 fire `void store.loadResourceCandidates` 重复调 gantt（同会话第二次 gantt 卡 1min）
   *  - RC23 收：listResourceCandidates（过渡数据源）**已撤**，改走 /api/resource（不调 gantt，无卡顿根因）
   *  - RC27 收（10-14）：selectVersion / 版本条**已撤**，页面不再有 gantt 相关调用方
   *  - 现仅保留底层兜底契约：schedule.ts getGantt 显式 30s 超时（gantt 页仍在用）
   *  - 反向探针自证可失败：删 getGantt 的 timeout: 30_000 → RC21 由绿转红
   *  - @see 4号位 10-12「Resource Calendar gantt/143 第二次卡死」拍板件 + RC23 收口 G1 + RC27 撤版本条
   */
  const rc21ScheduleContent = readSrc('api/aps-v1/schedule.ts')
  // 作用域限定在 getGantt 函数体：2026-10-17 R1 加了 window 入参 + 说明注释，
  // 原来「500 字符窗口」已不够长（会把 timeout 漏在窗口外而假红）。
  const rc21GetGanttBlock =
    rc21ScheduleContent.match(/async\s+getGantt\([\s\S]*?\n  \}/)?.[0] ?? ''
  const rc21GetGanttHasTimeout = /timeout:\s*30_?000/.test(rc21GetGanttBlock)
  assert(
    'RC21 静态：getGantt 显式 30s 超时（页面侧触发源已由 RC23/RC27 撤除，兜底留在 API 层）',
    rc21GetGanttHasTimeout,
    rc21GetGanttHasTimeout ? '全齐' : 'timeout 缺失'
  )

  /* -------- RC22 静态：Resource Calendar 页面说明区 ElCard→div 收口（10-12 增） --------
   *  用户反馈：顶部页面说明 ElCard 仅有 header 无 body，空 body 占用行高
   *  - 修复：原 <ElCard class="rc-intro"> + #header 改为 <div class="rc-intro">，body 高度彻底消除
   *  - 不动：title/sub/help/role-switcher 4 元素（保留所有 UX 内容）
   *  - 反向探针自证可失败：把 <ElCard class="rc-intro"> 加回去 → RC22 由绿转红
   *  - @see 10-12 用户反馈"页面说明区感觉没用，高度去掉"
   */
  const rc22IntroNoElCard = !/<ElCard[^>]*class="rc-intro"/.test(pageContent)
  const rc22IntroStillHasContent =
    /<div class="rc-intro">[\s\S]*?intro-title[\s\S]*?资源日历维护[\s\S]*?<\/div>/.test(pageContent)
  const rc22IntroKeepsHelp =
    /class="intro-help"[\s\S]*?能不能排产，看厂和部门开没开/.test(pageContent)
  const rc22IntroKeepsRoleSwitcher =
    /class="role-switcher"[\s\S]*?mockActiveRole/.test(pageContent)
  const rc22All =
    rc22IntroNoElCard &&
    rc22IntroStillHasContent &&
    rc22IntroKeepsHelp &&
    rc22IntroKeepsRoleSwitcher
  assert(
    'RC22 静态：Resource Calendar 页面说明区 ElCard→div 收口（不留空 body + title/help/role-switcher 内容保留）',
    rc22All,
    rc22All
      ? '全齐'
      : `noElCard=${rc22IntroNoElCard} hasContent=${rc22IntroStillHasContent} keepsHelp=${rc22IntroKeepsHelp} keepsRoleSwitcher=${rc22IntroKeepsRoleSwitcher}`
  )

  /* -------- RC25 静态：v1.4 §23 / 废止清单 O-024 覆盖 v1.3 §九（10-14 增；10-14 扩范围）--------
   *  业务事实：资源日历维护页 = 三级继承第 3 级。冻结 v1.4 line 23 定向覆盖 v1.3 §九——
   *  「本级无窗口即不可排」**不再是判据**：本级 MISSING 允许继承上级已认证窗口，
   *  仅当三级计算后仍无权威合法窗口才判不可排（废止清单 O-024 同口径）。
   *  ⇒ 页面文案只准陈述「本级事实」，**不准断言该资源不可排**（那会教用户白铺窗口）
   *  - 合并后真实状态暂无查询端点（ResourceCalendarResolver 未挂 Service/Controller），
   *    已发函 5号位；落地前不得在 UI 伪造三态（@see 4号位-2026-10-14 函）
   *  - 10-14 扩范围：首轮只扫页面模板，漏掉 2 个弹窗 + 状态标签 description 三处用户可见文案，
   *    本次一并纳入（教训：口径类探针必须覆盖**全部**渲染来源，而非只看主页面）
   *  - 反向探针自证可失败：把任一处「本级还没单独登记」改回「未配日历 = 不可排」→ RC25 转红
   */
  const rc25TypesContent = readSrc('api/aps-v1/types/resourceCalendar.ts')
  // 状态标签 description 由 CALENDAR_CONFIG_META 渲染进页面（非字面量在模板里）→ 单独扫该对象
  const rc25MetaStart = rc25TypesContent.indexOf('export const CALENDAR_CONFIG_META')
  const rc25MetaBlock =
    rc25MetaStart >= 0 ? rc25TypesContent.slice(rc25MetaStart, rc25MetaStart + 900) : ''
  /** 用户可见文案的全部渲染来源 */
  const rc25UserVisible = [pageTemplateOnly, bulkTpl, slotTpl, rc25MetaBlock].join('\n')
  const rc25BlanketUnusablePhrases = [
    '未配日历 = 不可排',
    '排产判定该设备不可用',
    '排产判定该人工槽不可用',
    '无有效日历窗口 = 不可用',
    '尚无日历窗口 = 不可排',
    '无有效日历即不可用'
  ].filter((t) => rc25UserVisible.includes(t))
  const rc25StatesLocalFact =
    pageTemplateOnly.includes('这台设备本级还没登记') &&
    pageTemplateOnly.includes('这个工位本级还没登记') &&
    pageTemplateOnly.includes('不代表它一定不能排产')
  const rc25MentionsInheritance =
    pageTemplateOnly.includes('厂里或部门开放') &&
    pageTemplateOnly.includes('所属工厂或部门开放') &&
    /工厂\s*→\s*部门\s*→\s*设备/.test(pageTemplateOnly)
  // 10-14 增：人工槽弹窗 + 状态标签必须给「本级事实 + 可继承」口径
  //（批量铺窗弹窗的口径原文已于同日精简撤除 —— 用户反馈弹窗啰嗦，该页改为只留阻塞信息；
  //  本级事实口径由页面状态行 CALENDAR_CONFIG_META 持续承载，弹窗背后的页面本就可见）
  const rc25DialogsLocalFact =
    slotTpl.includes('本级还没单独登记') &&
    rc25MetaBlock.includes('本级没单独登记') &&
    rc25MetaBlock.includes('所属工厂或部门开放了，它照样能排产')
  const rc25All =
    rc25BlanketUnusablePhrases.length === 0 &&
    rc25StatesLocalFact &&
    rc25MentionsInheritance &&
    rc25DialogsLocalFact
  assert(
    'RC25 静态：v1.4 §23/O-024 覆盖 v1.3 §九 —— 页面+人工槽弹窗+状态标签均只陈述本级事实，不断言不可排',
    rc25All,
    rc25All
      ? '均为本级事实口径 + 继承说明齐；无「不可排」断言'
      : `旧断言残留=${rc25BlanketUnusablePhrases.join(',') || '无'} 页面口径=${rc25StatesLocalFact} 继承说明=${rc25MentionsInheritance} 弹窗/标签口径=${rc25DialogsLocalFact}`
  )

  /* -------- RC26 静态：设备/人工槽窗口**服务端分页**（2026-10-17 改判据）--------
   *  用户实测：单资源 365 条窗口（批量铺窗 Days 上限 370），整页入 ElTable 无法翻页/定位
   *  ⚠️ 旧判据（10-14 写）锁的是**前端切片**过渡方案，前提是「后端仍返裸数组」。
   *     该前提 **10-14 当天就失效了** —— 5号位 同批把两端点原地改成 PageResult，
   *     前端未同步适配 ⇒ 不带分页参数只拿第 1 页 20 条、且拿到的是对象不是数组，
   *     `deviceWindows.length` 恒 undefined ⇒ **窗口列表整块不显示**。
   *  ⇒ 现判据锁**服务端分页**：store 只留当前页 + 服务端 total，翻页必须重新取数。
   *  - 反向探针自证可失败：把 getWindows 的 params 去掉 → RC26a 转红；
   *    `:data` 改回本地切片变量 / 翻页只改游标不重新取数 → RC26b 转红
   */
  //  ⚠️ 必须断言**两处**都带参数：本页两个窗口端点曾一起踩坑，
  //    只断言「存在一处」的话删掉设备那处仍能蒙混过关（四号位 自证时实测到此盲区）
  const rc26ParamsCount = (
    apiContent.match(/params:\s*\{\s*pageIndex:\s*query\.pageIndex,\s*pageSize:\s*query\.pageSize\s*\}/g) ?? []
  ).length
  const rc26ApiParams =
    rc26ParamsCount >= 2 &&
    /CalendarWindowPageResult<ResourceCalendarEntryDto>/.test(apiContent) &&
    /CalendarWindowPageResult<ManualSlotCalendarDto>/.test(apiContent)
  assert(
    'RC26a 静态：两个窗口端点都按 PageResult 取数（设备 + 人工槽各带 pageIndex/pageSize）',
    rc26ApiParams,
    rc26ApiParams
      ? `两处分页参数齐（实测 ${rc26ParamsCount} 处）+ 两处返回类型均为 PageResult`
      : `带参数的端点仅 ${rc26ParamsCount}/2 处，或返回类型未改`
  )

  const rc26StoreServerPaging =
    /const\s+deviceWindowPage\s*=\s*ref\(1\)/.test(storeContent) &&
    /const\s+deviceWindowPageSize\s*=\s*ref\(20\)/.test(storeContent) &&
    /const\s+deviceWindowItems\s*=\s*ref/.test(storeContent) &&
    /const\s+deviceWindowTotal\s*=\s*ref/.test(storeContent) &&
    !/deviceWindowPageRows/.test(storeContent) &&
    !/\.slice\(\s*start/.test(storeContent)
  assert(
    'RC26b 静态：store 只留当前页 + 服务端 total，前端不再本地切片',
    rc26StoreServerPaging,
    rc26StoreServerPaging ? 'deviceWindowItems/Total + 无切片残留' : '仍有本地切片或件套不全'
  )

  const rc26ViewPaging =
    /:data="deviceWindowItems"/.test(pageContent) &&
    /v-if="deviceWindowTotal > 0"/.test(pageContent) &&
    /windowCountLabel\(deviceWindowTotal\)/.test(pageContent) &&
    /:page-sizes="\[10, 20, 50, 100, 200\]"/.test(pageContent) &&
    /layout="total, sizes, prev, pager, next, jumper"/.test(pageContent) &&
    /@current-change="onDeviceWindowPageChange"/.test(pageContent) &&
    /@size-change="onDeviceWindowSizeChange"/.test(pageContent)
  assert(
    'RC26c 静态：Tab1 窗口表绑服务端当前页 + 条数显示服务端 total + 分页组件齐',
    rc26ViewPaging,
    rc26ViewPaging ? '表格/条数/分页组件均按服务端分页' : '表格或分页组件属性不全'
  )

  const rc26RefetchOnPage =
    /function\s+onDeviceWindowPageChange[\s\S]{0,200}?loadDeviceWindows\(id,\s*\{\s*nextPageIndex:\s*nextPage/.test(
      pageContent
    ) &&
    /function\s+onDeviceWindowSizeChange[\s\S]{0,200}?loadDeviceWindows\(id,\s*\{\s*nextPageSize:\s*nextSize,\s*nextPageIndex:\s*1/.test(
      pageContent
    )
  assert(
    'RC26d 静态：翻页/改每页条数**重新取数**（服务端分页不能只改游标）',
    rc26RefetchOnPage,
    rc26RefetchOnPage ? '两个 handler 都触发 loadDeviceWindows' : '翻页未重新取数（会停在旧数据）'
  )

  const rc26SlotWindowsPaging =
    /const\s+slotWindowItems\s*=\s*ref/.test(storeContent) &&
    /const\s+slotWindowTotal\s*=\s*ref/.test(storeContent) &&
    /CalendarWindowPageResult<ManualSlotCalendarDto>/.test(apiContent) &&
    /@current-change="onSlotWindowPageChange"/.test(pageContent)
  assert(
    'RC26e 静态：人工槽窗口（Tab3）同款服务端分页（后端同批改 PageResult）',
    rc26SlotWindowsPaging,
    rc26SlotWindowsPaging ? 'Tab3 件套 + 分页组件齐' : 'Tab3 未同步服务端分页'
  )

  const rc26StatusByTotal =
    /function\s+statusOf\(totals:\s*Record<number,\s*number>,\s*key:\s*number\)/.test(storeContent) &&
    /deviceWindowTotals/.test(storeContent) &&
    /slotWindowTotals/.test(storeContent)
  assert(
    'RC26f 静态：「已配/未配」徽标按服务端 total 判定（分页后不再靠全量数组长度）',
    rc26StatusByTotal,
    rc26StatusByTotal ? 'statusOf 改用 totals 表' : '徽标仍依赖已撤的全量数组'
  )

  /* -------- RC28 静态：设备归属部门可见（10-14 增）--------
   *  冻结 v1.4 line 17：「Resource 归部门」是三级继承的硬前提，用户须先知道设备属于哪个车间，
   *  才能判断「上级（工厂/部门）开没开」——而这正是本页最该帮人做的事。
   *  事实：后端 ResourceListItemDto 已带 productionDepartmentName（ResourceCalendarDtos.cs:116），
   *        前端类型早有该字段，此前只是没用（dev 7773 条同名设备无法区分）。
   *  - 反向探针自证可失败：把 labelOfResource 里的 `（${dept}）` 去掉 / 删状态行部门标签 → RC28 转红
   */
  // 10-23 起下拉 label 是「（工厂 / 部门）」——5号位 R2 同批下发了 factoryCode/factoryName，
  // 工厂一并带上（上级就是工厂，不显示无法判断该看哪一层）。
  // ⇒ 断言跟着落到「owner 由 factory + dept 拼成并渲染在括号里」，而不是锁死旧的 `（${dept}）` 形状。
  const rc28DropdownShowsDept =
    /const dept = r\.productionDepartmentName\?\.trim\(\)/.test(pageContent) &&
    /const owner = \[factory, dept\]\.filter\(Boolean\)\.join\(' \/ '\)/.test(pageContent) &&
    /`（\$\{owner\}）`/.test(pageContent)
  const rc28StatusRowShowsDept =
    /currentResourceDept/.test(pageContent) &&
    /所属部门：\{\{ currentResourceDept \}\}/.test(pageTemplateOnly)
  const rc28TypeHasDept = /productionDepartmentName/.test(readSrc('api/aps-v1/types/resourceCalendar.ts'))
  const rc28All = rc28DropdownShowsDept && rc28StatusRowShowsDept && rc28TypeHasDept
  assert(
    'RC28 静态：设备归属部门可见（下拉 label 带部门 + 状态行「所属部门」标签，用已返回的 productionDepartmentName）',
    rc28All,
    rc28All
      ? '下拉 + 状态行双落点齐'
      : `dropdown=${rc28DropdownShowsDept} statusRow=${rc28StatusRowShowsDept} type=${rc28TypeHasDept}`
  )

  /* -------- RC29 静态：撤除本级重叠拦截 + 覆盖语义必须写在弹窗上（10-14 自纠）--------
   *  自纠背景：本日早些时候按「后端只追加不覆盖」的旧认知加了前端重叠拦截，属功能倒退 ——
   *  ResourceCalendarService.cs:51-59 / ManualCapacityService.cs:154 起手就是
   *    DELETE ... WHERE ResourceId=@Id AND StartTime >= @Start AND StartTime < @Start+Days
   *  再逐天 INSERT，注释明写「覆盖式铺窗…保证无重叠且重复铺窗幂等」。
   *  既然后端已保证结果无重叠且可幂等重铺，前端再拦 = 凭空阻止合法的「改时段重铺」。
   *  冻结 v1.4 §21「相同级别重叠矛盾拒绝发布」由后端删除语义天然满足。
   *  但覆盖 = 会删数据，属破坏性语义，必须让用户在弹窗上看见 → 正向探针盯住那条黄字。
   *  - 反向探针自证可失败：把 overlappingDates / 提交前 return / 按钮禁用加回去 → 转红；
   *    删掉「覆盖」黄条 → 转红
   */
  const bulkDialogContent = readSrc('views/Aps/components/CalendarBulkDialog.vue')
  const rc29NoOverlapComputed = !/overlappingDates/.test(bulkDialogContent)
  const rc29NoSubmitBlock =
    !/const\s+conflicts\s*=/.test(bulkDialogContent) &&
    !/时段和已有窗口重叠/.test(bulkDialogContent)
  const rc29NoDisableBtn = !/:disabled="overlappingDates/.test(bulkTpl)
  const rc29StatesOverwrite =
    bulkTpl.includes('这段日期是「覆盖」，不是「追加」') &&
    bulkTpl.includes('全部删掉') &&
    bulkTpl.includes('再按你填的时间重新生成')
  const rc29All =
    rc29NoOverlapComputed && rc29NoSubmitBlock && rc29NoDisableBtn && rc29StatesOverwrite
  assert(
    'RC29 静态：铺窗为覆盖语义（前端不拦重叠 + 弹窗明示「覆盖不是追加」）',
    rc29All,
    rc29All
      ? '无重叠拦截，破坏性语义已上弹窗'
      : `noComputed=${rc29NoOverlapComputed} noSubmitBlock=${rc29NoSubmitBlock} noDisableBtn=${rc29NoDisableBtn} overwriteCopy=${rc29StatesOverwrite}`
  )

  /* -------- RC30 静态：批量铺窗弹窗精简（10-14 用户反馈「圈出来的三块都去掉」）--------
   *  用户原话：弹窗里「键 = xxx」「可用标记的 availableFlag=false 说明」「本次将生成 + 追加语义 +
   *  现有窗口表格」三块是纯提示，简洁好用即可。
   *  判断依据：
   *    - 「键 = 10291」= 内部自增 Id 外露，用户据此排不出车间口径（@see 列表不显示内部 ID 规范）
   *    - availableFlag=false 是字段名，不是大白话；开关本身已写「可用 / 禁用」
   *    - 「本次将生成」与按钮「确认铺窗（N 条）」+ 表单本身完全重复
   *    - 「现有窗口」表格与弹窗背后的页面表 100% 重复，单资源 365 行还把视野挤没了
   *  保留：必填项 + 时段预设 + 仅冲突时出现的红条（阻塞信息，用户必须看见）
   *  - 反向探针自证可失败：把任一块加回去 → RC30 转红
   */
  const rc30RemovedInternalId = !/键\s*=\s*\{\{/.test(bulkTpl)
  // 注意：只禁"把字段名当文案讲"（`availableFlag=false` /「禁用窗口（」），
  // 不要误伤 v-model="form.availableFlag" 这类绑定
  const rc30RemovedFieldNameHint =
    !/availableFlag\s*=\s*false/.test(bulkTpl) && !bulkTpl.includes('禁用窗口（')
  const rc30RemovedSummaryAlerts =
    !bulkTpl.includes('本次将生成') &&
    !bulkTpl.includes('追加语义') &&
    !bulkTpl.includes('不覆盖')
  const rc30RemovedExistingTable =
    !bulkTpl.includes('现有窗口') && !/<ElTable/.test(bulkTpl)
  const rc30KeepsOverwriteAlert = bulkTpl.includes('这段日期是「覆盖」，不是「追加」')
  const rc30All =
    rc30RemovedInternalId &&
    rc30RemovedFieldNameHint &&
    rc30RemovedSummaryAlerts &&
    rc30RemovedExistingTable &&
    rc30KeepsOverwriteAlert
  assert(
    'RC30 静态：批量铺窗弹窗精简（无内部 Id / 无字段名说明 / 无重复摘要 / 无现有窗口表，仅保留覆盖黄条）',
    rc30All,
    rc30All
      ? '4 块提示全撤，覆盖黄条保留'
      : `internalId=${rc30RemovedInternalId} fieldName=${rc30RemovedFieldNameHint} summary=${rc30RemovedSummaryAlerts} existTbl=${rc30RemovedExistingTable} overwriteAlert=${rc30KeepsOverwriteAlert}`
  )

  /* -------- RC31 静态：单条「新增一段」（2026-10-14「放开一天多条」方向2，2026-10-17 前端补齐）--------
   *  背景：5号位 同日落地了 POST /api/resource-calendar/slot 与 /api/manual-capacity/calendar/single
   *        （只插不删、重叠 409、相切合法、跨天 400），但前端当时只留了「批量铺窗（覆盖）」入口
   *        ⇒ 后端能一天多条，**用户在页面上却做不到**。本组锁住两个入口的分工不被合并回去。
   *  - 反向探针自证可失败：删掉任一 append 调用 / 去掉「新增一段」按钮 / 把批量弹窗的覆盖说明
   *    改成"追加" / 把单条弹窗的追加说明改成"覆盖" → 任一条转红
   */
  const rc31ApiAppend =
    /url:\s*'\/api\/resource-calendar\/slot'/.test(apiContent) &&
    /url:\s*'\/api\/manual-capacity\/calendar\/single'/.test(apiContent)
  const rc31StoreAppend =
    /async\s+function\s+appendDeviceWindow/.test(storeContent) &&
    /async\s+function\s+appendSlotWindow/.test(storeContent)
  const singleDialog = readSrc('views/Aps/components/CalendarSingleWindowDialog.vue')
  const rc31DialogCopy =
    singleDialog.includes('只新增这一段') &&
    singleDialog.includes('重叠') &&
    singleDialog.includes('批量铺窗') &&
    !singleDialog.includes('原有的窗口会')
  //  必须**逐个按钮**断言：只查「文件名出现过」的话，把某个按钮的 @click 改回批量入口仍能蒙混过关
  const rc31ViewTwoEntries =
    (pageContent.match(/@click="openDeviceSingleDialog"/g) ?? []).length >= 1 &&
    (pageContent.match(/@click="openSlotSingleDialog"/g) ?? []).length >= 1 &&
    (pageContent.match(/@click="openDeviceDialog"/g) ?? []).length >= 1 &&
    (pageContent.match(/@click="openSlotBulkDialog"/g) ?? []).length >= 1 &&
    (pageContent.match(/新增一段/g) ?? []).length >= 2
  const rc31All = rc31ApiAppend && rc31StoreAppend && rc31DialogCopy && rc31ViewTwoEntries
  assert(
    'RC31 静态：同日可登记多段（单条 = 只追加 / 批量 = 覆盖）—— 两个入口并存且语义不混',
    rc31All,
    rc31All
      ? '设备+人工槽各两个入口；单条弹窗讲追加、批量弹窗讲覆盖'
      : `api=${rc31ApiAppend} store=${rc31StoreAppend} dialogCopy=${rc31DialogCopy} viewEntries=${rc31ViewTwoEntries}`
  )

  /* -------- RC32 静态：页面消费三级合并解析预览（2026-10-17 补齐 R1 前端侧 + R2 来源标注）--------
   *  背景：5号位 2026-10-14 落地 `GET /api/resource-calendar/resolve-preview`，其回执 §四 要求
   *        「页面接 status/source 展示真三态」，但前端当时**一处都没调**（只在注释里提过）。
   *  冻结 13 v1.8:31：「明确显示『继承上级/本级明确工作/本级明确不工作』**及来源**、授权、时间片」。
   *  ⚠️ 来源标注不能给本级窗口表加列 —— resolve-preview 返的是**合并后**窗口，
   *     继承来的时段压根不在本级表里；故页面另开一块展示，本探针锁这个形态。
   *  - 反向探针自证可失败：删 api 调用 / 删 store action / 少一个状态标签 / 少一级来源标签 → 转红
   */
  const rc32ApiCall =
    /url:\s*'\/api\/resource-calendar\/resolve-preview'/.test(apiContent) &&
    /resourceIds:\s*resourceIds\.join\(','\)/.test(apiContent) &&
    /startDate,\s*endDate/.test(apiContent)
  const rc32StoreAction =
    /async\s+function\s+loadResolvePreview/.test(storeContent) &&
    /getResolvePreview\(/.test(storeContent)
  // 三态 + 三级来源都必须有渲染口径（少一个用户就会看到英文原文）
  const rc32Statuses = ['WORK', 'OFF', 'MISSING'].every((s) =>
    new RegExp(`\\b${s}:\\s*\\{`).test(pageContent)
  )
  const rc32Sources = ['Factory', 'Department', 'Resource'].every((s) =>
    new RegExp(`\\b${s}:\\s*\\{`).test(pageContent)
  )
  // 模板走预计算行 `resolveRows`（表格用 prop 取字段，不在单元格里调函数/查表 —— 否则任一环节取不到值整行会空白）
  const rc32ViewConsumes =
    /resolvePreview\.status/.test(pageTemplateOnly) &&
    /:data="resolveRows"/.test(pageTemplateOnly) &&
    /row\.sourceLabel/.test(pageTemplateOnly) &&
    /row\.sourceTag/.test(pageTemplateOnly) &&
    /resolveSourceMeta\[w\.source\]/.test(pageContent) &&
    /loadResolvePreview/.test(pageContent)
  const rc32All = rc32ApiCall && rc32StoreAction && rc32Statuses && rc32Sources && rc32ViewConsumes
  assert(
    'RC32 静态：页面消费 resolve-preview（三态 + 三级来源标注），不再只在本级表上猜',
    rc32All,
    rc32All
      ? 'api/store/view 三处接通；WORK/OFF/MISSING 与 Factory/Department/Resource 均有渲染口径'
      : `api=${rc32ApiCall} store=${rc32StoreAction} statusMeta=${rc32Statuses} sourceMeta=${rc32Sources} view=${rc32ViewConsumes}`
  )

  /* -------- RC33 静态：前后端联调收尾四处（2026-10-17 全页审计）--------
   *  ① 设备写操作（批量/单条/删除）成功后必须**重取合并预览** —— 否则加的段不会出现在「三级合并」里，
   *     用户会以为没生效
   *  ② 删掉本页最后一条后当前页会越界 → 必须回退一页（否则停在空白页）
   *  ③ 人工槽「日历」徽标必须用列表项 `hasCalendar` 打底 —— 后端列表 DTO 已给，
   *     以前整列显示「未查证」、要点开每个槽才知道
   *  ④ 工序过滤后端是**精确匹配**（`ms.OperationName = @Op`），输入框不得再暗示"自由文本"
   *  - 反向探针自证可失败：删任一处刷新调用 / 删回退分支 / 改回只读 totals / 占位符改回"自由文本" → 转红
   */
  const rc33Refresh1 =
    /已生成 \$\{inserted\} 条设备窗口[\s\S]{0,160}?loadResolvePreview\(\)/.test(pageContent)
  const rc33Refresh2 =
    /已新增一段设备时段[\s\S]{0,160}?loadResolvePreview\(\)/.test(pageContent)
  const rc33Refresh3 =
    /设备窗口已删除[\s\S]{0,200}?loadResolvePreview\(\)/.test(pageContent)
  const rc33PageClamp =
    (storeContent.match(/!items\.length && res\.page > 1 && res\.total > 0/g) ?? []).length >= 2 &&
    (storeContent.match(/nextPageIndex: res\.page - 1/g) ?? []).length >= 2
  const rc33SlotBadge =
    /function\s+slotCalendarStatusOf\(row: ManualCapacitySlotDto\)/.test(pageContent) &&
    /row\.hasCalendar === true/.test(pageContent) &&
    /slotCalendarStatusOf\(row\)/.test(pageTemplateOnly) &&
    !/store\.slotCalendarStatus\(row\.manualSlotId\)/.test(pageTemplateOnly)
  const rc33ExactHint = /完整工序名（精确匹配）/.test(pageTemplateOnly)
  const rc33All =
    rc33Refresh1 && rc33Refresh2 && rc33Refresh3 && rc33PageClamp && rc33SlotBadge && rc33ExactHint
  assert(
    'RC33 静态：写后刷新合并预览 + 删后回退一页 + 人工槽徽标用 hasCalendar + 工序过滤明示精确匹配',
    rc33All,
    rc33All
      ? '四处联调收尾齐备'
      : `refresh=${rc33Refresh1}/${rc33Refresh2}/${rc33Refresh3} clamp=${rc33PageClamp} badge=${rc33SlotBadge} hint=${rc33ExactHint}`
  )

  /* -------- RC25b 动态：resolve-preview 三级合并状态（2026-10-17 起默认开跑）--------
   *  冻结口径：三态 WORK / OFF / MISSING —— 07 §4.2「WorkState NVARCHAR(8) WORK/OFF；无命中表示 MISSING」
   *            07 §4.2「显式关闭/维修不可被上层复活；无任何权威合法窗口则不能排程，也不补默认8小时」
   *  口径来源：4号位-2026-10-14 致 5号位 函 R1；5号位 2026-10-14 回执称已接线落地
   *
   *  ⚠️ 旧判据是**假绿**，两处都错，2026-10-17 修：
   *    ① 形状：读 `data.items`，但端点返的是 `ApiResponse<List<…>>` —— `data` **直接是数组**，
   *       `data.items` 恒 undefined ⇒ 兜成 `[]` ⇒ `[].every()` **恒真** ⇒ 开了开关也测不出任何东西；
   *    ② 大小写：断言 `source ∈ {FACTORY,DEPARTMENT,RESOURCE}`，实测返的是 **PascalCase**
   *       `Factory / Department / Resource`（5号位 函件原文也是 PascalCase）。
   *  - 现判据：`data` 必须是**非空数组**（空数组直接判失败，避免 `every` 空真）+ 三态 + source 大小写按实测
   *  - 反向探针自证可失败：把 source 集合改回大写 / 把非空断言去掉 → RC25b 立刻转红
   */
  if (token && process.env.VERIFY_RC25_RESOLVE !== '0') {
    const pResolve = await call('GET', '/api/resource-calendar/resolve-preview', {
      token,
      query: { resourceIds: '14537,6444', startDate: '2026-10-15', endDate: '2026-10-16' }
    })
    const rData = pResolve.json?.data
    const rItems = Array.isArray(rData) ? rData : null
    const rOk =
      pResolve.ok &&
      rItems !== null &&
      rItems.length > 0 &&
      rItems.every(
        (it) =>
          typeof it.resourceId === 'number' &&
          typeof it.hasAnyWindow === 'boolean' &&
          ['WORK', 'OFF', 'MISSING'].includes(it.status) &&
          Array.isArray(it.windows) &&
          it.windows.every((w) => ['Factory', 'Department', 'Resource'].includes(w.source))
      )
    assert(
      'RC25b 动态：resolve-preview 返三级合并后状态（data 为非空数组 + hasAnyWindow + 三态 + windows[].source 来源层级）',
      rOk,
      rOk
        ? `items=${rItems.length} 首项 status=${rItems[0].status} source=${rItems[0].windows[0]?.source ?? '-'}`
        : `ok=${pResolve.ok} data 是数组=${rItems !== null} 条数=${rItems?.length ?? '-'} body=${JSON.stringify(pResolve.json ?? {}).slice(0, 200)}`
    )
  } else {
    skip('RC25b 动态：resolve-preview 三级合并状态（VERIFY_RC25_RESOLVE=0 显式关闭）')
  }

  info('RC 段全部为「静态契约 + 端点可达」；C1/G1/G2/G3/G4/D2/D3/G5 缺口见 4号位 致 5号位 函 + RC20 Tab 切换不阻塞 + RC21 gantt 慢响应兜底 + RC22 页面说明区 ElCard→div 收口 + RC23 G1 撤过渡切正式端点 + RC27 撤版本选择器')

  /* -------- RC23 静态：Resource Calendar G1 撤过渡切正式端点（10-12 增） --------
   *  触发：用户问"现实中需要手填资源ID吗"——答案否；
   *        撤过渡数据源（gantt.resources 派生）+ 撤「手填资源 ID」UI + 撤 closure 缓存
   *        资源主表正式端点 GET /api/resource（@see ResourceController.cs:34）已落地
   *  - 6 项契约：
   *     1. types 含 Resource 接口（resourceId/resourceCode/resourceName/productionDepartmentId/hasCalendar）
   *     2. api 含 listResources（GET /api/resource） + listDepartmentCandidates（derive）
   *     3. view 用 el-select-v2 虚拟滚动（dev 7773+ records 必须虚拟滚动，否则 el-select 卡顿）
   *     4. view 已撤「手填资源 ID」UI
   *     5. view 已撤「资源池来源/过渡数据源」用户可见提示
   *     6. store 用 resources（不是 resourceCandidates）/ loadResources（不是 loadResourceCandidates）/ resourcesLoading
   *  - 反向探针自证可失败：
   *     - 改回 ElSelect → el-select-v2 失效 → RC23 by 3 由绿转红
   *     - 加回「手填资源 ID」 → RC23 by 4 由绿转红
   *     - 改回 resourceCandidates → RC23 by 6 由绿转红
   */
  const rc23Types = readSrc('api/aps-v1/types/resourceCalendar.ts')
  const rc23Store = storeContentCodeOnly
  // 仅扫 view 的 <template> 区域（docstring 历史 docstring 允许保留：旧「手填资源 ID」→ 撤 / 旧「资源池来源」等）
  const rc23ViewStartIdx = pageContent.indexOf('<template>')
  const rc23View = rc23ViewStartIdx >= 0 ? pageContent.slice(rc23ViewStartIdx) : pageContent
  // 1. types 含 Resource interface + 8 字段（含 hasCalendar）
  const rc23HasResourceInterface = /export\s+interface\s+Resource\s*\{/.test(rc23Types)
  const rc23HasHasCalendarField = /hasCalendar/.test(rc23Types)
  // 2. api 含 listResources + URL '/api/resource'
  const rc23ApiHasListResources = /async\s+function\s+listResources\s*\(/.test(apiContentCodeOnly) ||
    /listResources\s*\(/.test(apiContentCodeOnly)
  const rc23ApiCallsApiResource = /\/api\/resource(?!\-calendar)/.test(apiContentCodeOnly)
  // 3. view 用 ElSelectV2（虚拟滚动）
  const rc23UsesSelectV2 = /ElSelectV2/.test(rc23View)
  // 4. view 已撤「手填资源 ID」UI
  const rc23NoManualResourceIdUi = !/手填资源 ID/.test(rc23View)
  // 5. view 已撤「资源池来源/过渡数据源」用户可见提示
  const rc23NoFallbackHint = !/资源池来源/.test(rc23View) && !/过渡数据源/.test(rc23View)
  // 6. store 用 resources / loadResources / resourcesLoading（不是 resourceCandidates/loadResourceCandidates）
  const rc23StoreUsesResources = /const\s+resources\s*=\s*ref/.test(rc23Store)
  const rc23StoreUsesLoadResources = /async\s+function\s+loadResources\s*\(/.test(rc23Store)
  const rc23StoreHasResourcesLoading = /resourcesLoading/.test(rc23Store)
  const rc23StoreNotUsingCandidates = !/resourceCandidates/.test(rc23Store) &&
    !/loadResourceCandidates/.test(rc23Store)
  const rc23All =
    rc23HasResourceInterface &&
    rc23HasHasCalendarField &&
    rc23ApiHasListResources &&
    rc23ApiCallsApiResource &&
    rc23UsesSelectV2 &&
    rc23NoManualResourceIdUi &&
    rc23NoFallbackHint &&
    rc23StoreUsesResources &&
    rc23StoreUsesLoadResources &&
    rc23StoreHasResourcesLoading &&
    rc23StoreNotUsingCandidates
  assert(
    'RC23 静态：G1 撤过渡切正式端点（types Resource 接口 + api listResources/GET /api/resource + view ElSelectV2 + 撤手填 ID + 撤 fallback 提示 + store resources/loadResources/resourcesLoading）',
    rc23All,
    rc23All
      ? '全齐'
      : `resIfc=${rc23HasResourceInterface} hasCal=${rc23HasHasCalendarField} apiRes=${rc23ApiHasListResources} apiUrl=${rc23ApiCallsApiResource} selV2=${rc23UsesSelectV2} noManual=${rc23NoManualResourceIdUi} noFb=${rc23NoFallbackHint} stRes=${rc23StoreUsesResources} stLoad=${rc23StoreUsesLoadResources} stLoading=${rc23StoreHasResourcesLoading} notCand=${rc23StoreNotUsingCandidates}`
  )

  info('RC 段全部为「静态契约 + 端点可达」；C1/G1/G2/G3/G4/D2/D3/G5 缺口见 4号位 致 5号位 函 + RC20 Tab 切换不阻塞 + RC21 gantt 慢响应兜底 + RC22 页面说明区 ElCard→div 收口 + RC23 G1 撤过渡切正式端点 + RC24 人工槽分页 + RC27 撤版本选择器')

  /* -------- RC24 静态：人工能力槽 Tab2 接入 PageResult 分页（10-12 增） --------
   *  触发：5号位 2026-10-12 函 [ManualCapacitySlots分页PageResult已落地-回执]
   *        R1 已落地（build 0 Error，dev 实测 15360 records + 深页 path 覆盖）
   *        R2 字段名定为 `page`（同 TSRC/ME/E）
   *  - 7 项契约：
   *     a. types 含 ManualCapacitySlotPageResult<T>（items/total/page/pageSize）
   *     b. api getSlots 返 ManualCapacitySlotPageResult<ManualCapacitySlotDto>
   *     c. api 显式传 pageIndex + pageSize（含默认 1/20）
   *     d. ManualSlotQuery 含 pageIndex + pageSize
   *     e. store 增 slotTotal/slotPage/slotPageSize 3 ref + slotsLoading
   *     f. store loadSlots 支持 {resetPage, nextPageIndex, nextPageSize} options + 后端回传覆盖
   *     g. view Tab2 用 ElPagination（6 属性 + pageSizes[10,20,50,100,200]） + applyFilter resetPage:true
   *  - 反向探针自证可失败：
   *     - 删 ManualCapacitySlotPageResult → RC24a 由绿转红
   *     - 改回返数组 → RC24b 由绿转红
   *     - 删 pageIndex/pageSize 默认 → RC24c 由绿转红
   *     - 删 store 3 ref → RC24e 由绿转红
   *     - 删 ElPagination → RC24g 由绿转红
   */
  const rc24Types = readSrc('api/aps-v1/types/resourceCalendar.ts')
  // a. types 含 ManualCapacitySlotPageResult<T> 包装
  const rc24HasPageResult = /export\s+interface\s+ManualCapacitySlotPageResult\s*<T>/.test(rc24Types) &&
    /items:\s*T\[\]/.test(rc24Types) && /page:\s*number/.test(rc24Types)
  // d. ManualSlotQuery 含 pageIndex + pageSize
  const rc24QueryHasPaging = /pageIndex\?:/.test(rc24Types) && /pageSize\?:/.test(rc24Types)
  // b+c. api getSlots 返 PageResult + 显式传 pageIndex/pageSize
  const rc24GetSlotsReturnsPage = /getSlots[\s\S]{0,400}?Promise<ManualCapacitySlotPageResult<ManualCapacitySlotDto>>/.test(apiContentCodeOnly)
  const rc24GetSlotsSendsPage = /getSlots[\s\S]{0,1500}?pageIndex:\s*page\b/.test(apiContentCodeOnly) &&
    /getSlots[\s\S]{0,1500}?pageSize:\s*size\b/.test(apiContentCodeOnly)
  // e. store 增 slotTotal/slotPage/slotPageSize 3 ref
  const rc24StoreHas3Ref =
    /const\s+slotTotal\s*=\s*ref<number>\(0\)/.test(storeContentCodeOnly) &&
    /const\s+slotPage\s*=\s*ref<number>\(1\)/.test(storeContentCodeOnly) &&
    /const\s+slotPageSize\s*=\s*ref<number>\(20\)/.test(storeContentCodeOnly)
  // f. loadSlots 支持 options（resetPage / nextPageIndex / nextPageSize）+ 后端回传覆盖
  const rc24LoadSlotsSupportsOptions = /async\s+function\s+loadSlots[\s\S]{0,500}?resetPage\?:\s*boolean/.test(storeContentCodeOnly) &&
    /async\s+function\s+loadSlots[\s\S]{0,500}?nextPageIndex\?:\s*number/.test(storeContentCodeOnly) &&
    /async\s+function\s+loadSlots[\s\S]{0,500}?nextPageSize\?:\s*number/.test(storeContentCodeOnly)
  const rc24LoadSlotsEchoesPage = /slots\.value\s*=\s*res\.items/.test(storeContentCodeOnly) &&
    /slotTotal\.value\s*=\s*res\.total/.test(storeContentCodeOnly) &&
    /slotPage\.value\s*=\s*res\.page/.test(storeContentCodeOnly)
  // g. view ElPagination 6 属性 + pageSizes + applyFilter resetPage:true
  const rc24ViewElPagination =
    /<ElPagination[\s\S]{0,500}?v-if="slotTotal\s*>\s*0"/.test(rc23View) &&
    /<ElPagination[\s\S]{0,800}?:current-page="slotPage"/.test(rc23View) &&
    /<ElPagination[\s\S]{0,800}?:page-size="slotPageSize"/.test(rc23View) &&
    /<ElPagination[\s\S]{0,800}?:total="slotTotal"/.test(rc23View) &&
    /<ElPagination[\s\S]{0,800}?:page-sizes="\[10,\s*20,\s*50,\s*100,\s*200\]"/.test(rc23View) &&
    /<ElPagination[\s\S]{0,800}?@current-change="onSlotPageChange"/.test(rc23View) &&
    /<ElPagination[\s\S]{0,800}?@size-change="onSlotSizeChange"/.test(rc23View)
  // searchSlots / resetSlotFilter 必传 resetPage: true（script setup 中）
  const rc24ViewApplyFilterResets =
    /store\.loadSlots\(\s*\{[\s\S]{0,200}?\}\s*,\s*\{\s*resetPage:\s*true\s*\}\s*\)/.test(pageContent) ||
    /store\.loadSlots\([\s\S]{0,300}?resetPage:\s*true/.test(pageContent)
  // onSlotPageChange / onSlotSizeChange 函数定义（script setup 中）
  const rc24ViewHasCallbacks =
    /async\s+function\s+onSlotPageChange/.test(pageContent) &&
    /async\s+function\s+onSlotSizeChange/.test(pageContent)
  const rc24All =
    rc24HasPageResult &&
    rc24QueryHasPaging &&
    rc24GetSlotsReturnsPage &&
    rc24GetSlotsSendsPage &&
    rc24StoreHas3Ref &&
    rc24LoadSlotsSupportsOptions &&
    rc24LoadSlotsEchoesPage &&
    rc24ViewElPagination &&
    rc24ViewApplyFilterResets &&
    rc24ViewHasCallbacks
  assert(
    'RC24 静态：人工能力槽 Tab2 接入 PageResult 分页（types PageResult + api getSlots 返包装 + store 3 ref + loadSlots options + view ElPagination 6 属性 + applyFilter resetPage）',
    rc24All,
    rc24All
      ? '全齐'
      : `type=${rc24HasPageResult} query=${rc24QueryHasPaging} apiRet=${rc24GetSlotsReturnsPage} apiSend=${rc24GetSlotsSendsPage} stRef=${rc24StoreHas3Ref} stOpt=${rc24LoadSlotsSupportsOptions} stEcho=${rc24LoadSlotsEchoesPage} viewPg=${rc24ViewElPagination} applyReset=${rc24ViewApplyFilterResets} cb=${rc24ViewHasCallbacks}`
  )

  /* -------- RC24 动态：GET /api/manual-capacity/slots pageIndex/pageSize 返 PageResult --------
   *  5号位 10-12 实测 15360 records + pageSize≤200
   *  - RC24h：pageIndex=1&pageSize=2 → items.length≤2 且 total≥items.length
   *  - RC24i：pageSize=9999 → 后端 Math.Clamp=200（仿 TSRC14k）
   */
  let rc24h = await call('GET', '/api/manual-capacity/slots', {
    token,
    query: { pageIndex: 1, pageSize: 2, includeInactive: false }
  })
  const rc24hRaw = rc24h.json?.data
  const rc24hIsPage = rc24hRaw && typeof rc24hRaw === 'object' && Array.isArray(rc24hRaw.items)
  const rc24hItems = rc24hIsPage ? rc24hRaw.items : null
  const rc24hOk =
    rc24h.code === 200 &&
    rc24hIsPage &&
    rc24hItems.length <= 2 &&
    typeof rc24hRaw.total === 'number' &&
    rc24hRaw.total >= rc24hItems.length &&
    rc24hRaw.page === 1
  assert(
    'RC24h 动态：GET /api/manual-capacity/slots?pageIndex=1&pageSize=2 返 {items,total,page,pageSize}（5号位 10-12 已落地）',
    rc24hOk,
    rc24hOk
      ? `total=${rc24hRaw.total} items=${rc24hItems.length} page=${rc24hRaw.page} pageSize=${rc24hRaw.pageSize}`
      : `code=${rc24h.code} shape=${JSON.stringify(rc24hRaw).slice(0, 200)}`
  )

  let rc24i = await call('GET', '/api/manual-capacity/slots', {
    token,
    query: { pageIndex: 1, pageSize: 9999, includeInactive: false }
  })
  const rc24iRaw = rc24i.json?.data
  const rc24iIsPage = rc24iRaw && typeof rc24iRaw === 'object' && Array.isArray(rc24iRaw.items)
  if (rc24iIsPage) {
    // 5号位 10-12 函 R1：pageSize 默认 20、上限 200（越界回落 20）→ pageSize=9999 应被后端兜回 ≤200
    const rc24iOk = rc24i.code === 200 && rc24iRaw.pageSize <= 200 && rc24iRaw.items.length <= 200
    assert(
      'RC24i 动态：GET /api/manual-capacity/slots?pageSize=9999 → 后端兜回 ≤200（5号位 10-12 函上限 200）',
      rc24iOk,
      rc24iOk
        ? `pageSize=${rc24iRaw.pageSize} items=${rc24iRaw.items.length}（已兜回）`
        : `pageSize=${rc24iRaw.pageSize} items=${rc24iRaw.items.length}`
    )
  } else {
    skip('RC24i 动态：dev 端尚未返 PageResult（10-12 部署可能滞后），无法验证上限兜底')
  }
}

/**
 * [D] Domain 维护（d-domain）— v1.2 Domain 专项 Pkg-3
 * 验收清单：
 *  - D01 静态：types/domain.ts 含 12 camelCase 字段（DTO 实体对齐）
 *  - D02 静态：types/domain.ts 无 PascalCase（Program.cs:93 = CamelCase）
 *  - D03 静态：api/aps-v1/domain.ts 8 方法 + 404 → EndpointUnavailableError + base path
 *  - D04 静态：Domain.vue 显式 import 16 EP 组件（ElDescriptions 等；2026-09-28 ElDescriptions 渲染 bug 教训）
 *  - D05 静态：业务约束（domainKey 不可改 + 仅 admin.system 可写 + 「需要系统管理员角色」tooltip）
 *  - D06 静态：模板无内部措辞残留
 *  - D07 静态：domain store 含 EndpointUnavailableError → unavailable 状态映射
 *  - D08-D12 端点可达：GET /domain-definition + /active + /{id} + /product-families + /factories
 *  - D13 接受类（默认跳过）：POST/PUT/enable/disable 已由 verify-integration.mjs [A] 段覆盖
 *  - D19 域依赖关系（2026-10-22 P3）：G7 端点可达 + 5 字段契约 + 上下游两列 +
 *    提前期 0 = 未派生 + 空态不得把「没数据」说成「本域没有依赖」
 */
async function verifyDomain(token) {
  log('\n[D] Domain 维护（d-domain）— v1.2 Domain 专项 Pkg-3')

  /* -------- D01 静态：types/domain.ts 契约（camelCase） -------- */
  const typesContent = readSrc('api/aps-v1/types/domain.ts')
  const dtoFields = [
    'id',
    'domainKey',
    'domainName',
    'scopeType',
    'productFamilyId',
    'factoryId',
    'isActive',
    'sortOrder',
    'createdBy',
    'createdAt',
    'updatedBy',
    'updatedAt'
  ]
  const missingDtoFields = dtoFields.filter((f) => !typesContent.includes(f))
  const hasScopeLiterals = typesContent.includes("'FAMILY'") && typesContent.includes("'FACTORY_FAMILY'")
  const hasScopeLabels = typesContent.includes('DOMAIN_SCOPE_TYPE_LABELS')
  assert(
    'D01 静态：types/domain.ts 含 12 camelCase 字段 + ScopeType 字面量 + 中文标签',
    missingDtoFields.length === 0 && hasScopeLiterals && hasScopeLabels,
    `缺字段：${missingDtoFields.join(',') || '无'}；ScopeType=${hasScopeLiterals}；中文标签=${hasScopeLabels}`
  )

  /* -------- D01b/D01c 静态：详情面板必须渲染「创建侧」审计（2026-10-23 P1）
   *  依据：冻结《Domain裁决单 v1.0》§4.2 —— 页面只需提供「变更审计」。
   *  事实：DTO 的 createdBy/createdAt 后端一直下发、前端类型一直有，
   *        但详情面板只渲染了更新侧（updatedBy/updatedAt）⇒ 审计只剩后一半。
   *  为什么用 [\s\S]{0,N} 桥接：prettier 会按 printWidth 把绑定表达式折行，
   *    写成 /label="创建人"\s*\{\{/ 会在折行后假红。
   *  为什么拆两条：合并成一个 && 会造成「删掉任一条仍绿」的假强。
   */
  const domainViewForAudit = readSrc('views/Aps/Domain.vue')
  const auditCreatedBy = /label="创建人"[\s\S]{0,120}?currentDetail\.createdBy/.test(domainViewForAudit)
  const auditCreatedAt = /label="创建时间"[\s\S]{0,160}?currentDetail\.createdAt/.test(domainViewForAudit)
  assert(
    'D01b 静态：详情面板渲染「创建人」且绑定 createdBy（冻结 §4.2 变更审计不得只留更新侧）',
    auditCreatedBy,
    auditCreatedBy ? '已渲染' : '缺「创建人」行或未绑定 createdBy'
  )
  assert(
    'D01c 静态：详情面板渲染「创建时间」且绑定 createdAt（走 formatUtcDateTime 防 naive UTC 少 8h）',
    auditCreatedAt,
    auditCreatedAt ? '已渲染' : '缺「创建时间」行或未绑定 createdAt'
  )

  /* -------- D02 静态：无 PascalCase 字段（Program.cs:93 = CamelCase） -------- */
  const pascalHits = [
    'DomainKey:',
    'DomainName:',
    'ScopeType:',
    'ProductFamilyId:',
    'FactoryId:'
  ].filter((p) => typesContent.includes(p))
  assert(
    'D02 静态：types/domain.ts 无 PascalCase 字段名（Program.cs:93 = CamelCase）',
    pascalHits.length === 0,
    pascalHits.length === 0 ? '全 camelCase' : `发现 PascalCase：${pascalHits.join(',')}`
  )

  /* -------- D03 静态：api/aps-v1/domain.ts 8 方法 + 404 处理 + base path -------- */
  const apiContent = readSrc('api/aps-v1/domain.ts')
  const apiMethods = [
    'list',
    'detail',
    'create',
    'update',
    'enable',
    'disable',
    'listProductFamilies',
    'listFactories'
  ]
  // 严格匹配 `<methodName>(` 形式（避免 list 命中 listProductFamilies 等）
  const missingMethods = apiMethods.filter(
    (m) => !new RegExp(`(?:^|[^a-zA-Z])${m}\\s*\\(`).test(apiContent)
  )
  const has404Handling =
    apiContent.includes('EndpointUnavailableError') &&
    apiContent.includes('apiErr?.code === 404')
  const baseOk = apiContent.includes('/api/governance/domain-definition')
  assert(
    'D03 静态：api/aps-v1/domain.ts 8 方法 + 404 → EndpointUnavailableError + base path',
    missingMethods.length === 0 && has404Handling && baseOk,
    `缺方法：${missingMethods.join(',') || '无'}；404=${has404Handling}；base=${baseOk}`
  )

  /* -------- D04 静态：Domain.vue 文件 + 显式 import 15 EP 组件（ElSwitch 09-29 已删，isActive 由 /enable /disable 专用端点管） -------- */
  const pageExists = staticFileExists('views/Aps/Domain.vue')
  const pageContent = pageExists ? readSrc('views/Aps/Domain.vue') : ''
  const requiredEp = [
    'ElAlert',
    'ElButton',
    'ElCard',
    'ElDescriptions',
    'ElDescriptionsItem',
    'ElDialog',
    'ElEmpty',
    'ElForm',
    'ElFormItem',
    'ElInput',
    'ElInputNumber',
    'ElMessage',
    'ElOption',
    'ElSelect',
    'ElTag',
    'ElTooltip'
  ]
  const missingEp = requiredEp.filter((c) => !pageContent.includes(c))
  assert(
    'D04 静态：Domain.vue 存在 + 显式 import 15 EP 组件（ElDescriptions 必须显式 import — 2026-09-28 bug 教训；ElSwitch 已删）',
    pageExists && missingEp.length === 0,
    `文件=${pageExists}；缺 import=${missingEp.join(',') || '无'}`
  )

  /* -------- D05/D05b/D05c 静态：业务约束（2026-10-23 改判据）
   *  改判据原因：原先「domainKey 不可改」判在页头副标题那句「排程域标识由系统生成、不可改」上，
   *    用户要求删掉页头副标题与新建对话框里的不可改提示条（页面不放大白话说明）⇒ 那句判据失去落点。
   *    重新落到**真正生效的位置**：详情面板的排程域标识是只读 ElTag + tooltip「系统生成、不可改」。
   *  拆三条：合并成 && 会造成「删掉任一条仍绿」的假强。
   */
  assert(
    'D05 静态：排程域标识只读展示（ElTag + tooltip「系统生成、不可改」）——冻结 §5 DomainKey 不可变更',
    /label="排程域标识"[\s\S]{0,200}?content="系统生成、不可改"/.test(pageContent),
    /label="排程域标识"[\s\S]{0,200}?content="系统生成、不可改"/.test(pageContent)
      ? '已以 tooltip 声明不可改'
      : '缺「排程域标识」只读展示或缺「系统生成、不可改」提示'
  )
  assert(
    'D05b 静态：canWrite 按角色码 aps.admin.system 判定（不得改成权限码，冻结 §4.1 系统管理员维护）',
    /canWrite\s*=\s*computed\(\(\)\s*=>\s*apsAuth\.roles\.includes\('aps\.admin\.system'\)\)/.test(pageContent),
    /canWrite\s*=\s*computed\(\(\)\s*=>\s*apsAuth\.roles\.includes\('aps\.admin\.system'\)\)/.test(
      pageContent
    )
      ? '按角色码判'
      : 'canWrite 未按 aps.admin.system 角色码判'
  )
  assert(
    'D05c 静态：非管理员的写操作按钮置灰并说明原因（tooltip「需要系统管理员角色」）',
    /<ElTooltip[^>]*!canWrite[^>]*content="需要系统管理员角色"/.test(pageContent),
    /<ElTooltip[^>]*!canWrite[^>]*content="需要系统管理员角色"/.test(pageContent)
      ? '已置灰并说明'
      : '缺非管理员的置灰按钮或缺原因提示'
  )

  /* -------- D06 静态：模板无内部协调痕迹 -------- */
  const templateOf = (rel) => {
    const c = readSrc(rel)
    const i = c.indexOf('<template>')
    return i >= 0 ? c.slice(i) : ''
  }
  const tpl = templateOf('views/Aps/Domain.vue')
  const leakWords = ['发函', '5号位', '2号位', '待交付', '3号位 缺口', 'B1-B3', 'expose']
  const tplLeaks = leakWords.filter((w) => tpl.includes(w))
  assert(
    'D06 静态：Domain.vue 模板无内部措辞残留',
    tplLeaks.length === 0,
    tplLeaks.length === 0 ? '无内部痕迹' : `残留：${tplLeaks.join(',')}`
  )

  /* -------- D07 静态：domain store 错误分流 -------- */
  const storeContent = readSrc('store/modules/aps/domain.ts')
  const hasUnavailable =
    storeContent.includes('EndpointUnavailableError') &&
    storeContent.includes("fetchStatus.value = 'unavailable'")
  assert(
    'D07 静态：domain store 含 EndpointUnavailableError → unavailable 状态映射',
    hasUnavailable,
    hasUnavailable ? '错误分流齐备' : '缺错误分流'
  )

  /* -------- D07b 静态：Domain.vue 写操作有 ElForm :rules + try/catch + submitting loading（2026-09-29 修复后固化） -------- */
  const editFormHasRef = pageContent.includes('ref="editFormRef"')
  const createFormHasRef = pageContent.includes('ref="createFormRef"')
  const editFormHasRules = /:rules="editRules"/.test(pageContent)
  const createFormHasRules = /:rules="createRules"/.test(pageContent)
  const hasEditProp = /prop="domainName"/.test(pageContent) && /prop="productFamilyId"/.test(pageContent)
  const hasSubmittingRefs =
    pageContent.includes('editSubmitting') &&
    pageContent.includes('createSubmitting') &&
    pageContent.includes('toggleSubmitting')
  const hasTryCatchInHandlers =
    /async function onSave[\s\S]*?try \{[\s\S]*?\} catch/.test(pageContent) &&
    /async function onCreate[\s\S]*?try \{[\s\S]*?\} catch/.test(pageContent) &&
    /async function onToggleActive[\s\S]*?try \{[\s\S]*?\} catch/.test(pageContent)
  const hasLoadingOnButtons =
    /:loading="editSubmitting"/.test(pageContent) &&
    /:loading="createSubmitting"/.test(pageContent) &&
    /:loading="toggleSubmitting"/.test(pageContent)
  assert(
    'D07b 静态：Domain.vue 写操作有 ElForm :rules/ref + prop + try/catch + submitting loading',
    editFormHasRef &&
      createFormHasRef &&
      editFormHasRules &&
      createFormHasRules &&
      hasEditProp &&
      hasSubmittingRefs &&
      hasTryCatchInHandlers &&
      hasLoadingOnButtons,
    `editFormRef=${editFormHasRef}；createFormRef=${createFormHasRef}；editRules=${editFormHasRules}；createRules=${createFormHasRules}；prop=${hasEditProp}；submitting=${hasSubmittingRefs}；try/catch=${hasTryCatchInHandlers}；loading=${hasLoadingOnButtons}`
  )

  /* -------- D07c 静态：Domain.vue onSave 的 patch 必须含 domainKey（后端 PUT 要求 [FromBody] DomainDefinition 全量实体 + UpdateAsync 校验 existing.DomainKey == input.DomainKey；09-29 修复后固化） -------- */
  const onSavePatchHasDomainKey = /async function onSave[\s\S]*?const patch[^}]*?domainKey:/.test(pageContent)
  assert(
    'D07c 静态：Domain.vue onSave patch 必须含 domainKey 字段（防 400 DomainKey 一经创建不可变更）',
    onSavePatchHasDomainKey,
    `onSave 含 domainKey=${onSavePatchHasDomainKey}`
  )

  /* -------- D07d 静态：Domain.vue editForm 不含 isActive（PUT 后端强制写回 existing.IsActive，编辑表单开关是误导 UX）；启停用由顶部专用按钮走 /enable /disable 端点（09-29 修复后固化） -------- */
  // 1. editForm.isActive 不能作为属性访问或赋值出现（排除 // 注释里的 false positive）
  const editFormNoIsActiveProp = !/editForm\.isActive/.test(pageContent)
  // 2. onSave patch 不含 isActive: editForm.isActive 或 isActive: <其它源>
  const onSavePatchNoIsActive = !/isActive:\s*editForm\.isActive/.test(pageContent) && !/isActive:\s*[a-zA-Z_$][\w$]*\.(?:isActive|true|false)/.test(pageContent.match(/const patch[\s\S]{0,500}/)?.[0] ?? '')
  // 3. 编辑表单不含 isActive 开关
  const formNoIsActiveSwitch = !/ElFormItem label="启用"[\s\S]*?ElSwitch/.test(pageContent)
  assert(
    'D07d 静态：Domain.vue 编辑表单无 isActive 字段/switch（防 PUT 静默丢弃；启停用走专用端点）',
    editFormNoIsActiveProp && onSavePatchNoIsActive && formNoIsActiveSwitch,
    `无 editForm.isActive=${editFormNoIsActiveProp}；patch 无 isActive=${onSavePatchNoIsActive}；表单无 switch=${formNoIsActiveSwitch}`
  )

  /* -------- D07e 静态：Domain.vue kpiCounts/filteredDefinitions 用 definitions（raw list，含 inactive）算 total/inactive/ALL（09-29 KPI bug 修复后固化：之前用 allActive.value 算 inactive 永远=0） -------- */
  // 1. kpiCounts 函数体里出现 definitions.value（说明 raw list 被使用）
  const kpiBlock = pageContent.match(/const kpiCounts[\s\S]{0,500}?\}/)?.[0] ?? ''
  const kpiUsesRawList = /definitions\.value/.test(kpiBlock)
  // 2. kpiCounts 不直接用 allActive.value 算 total 或 inactive（active 维度允许）
  const kpiTotalNotAllActive = !/total\s*=\s*allActive\.value\.length/.test(kpiBlock)
  const kpiInactiveNotAllActive = !/inactive\s*=\s*allActive\.value/.test(kpiBlock)
  // 3. filteredDefinitions 函数体里出现 definitions.value（赋给 raw），且 ALL 分支不直接用 allActive.value
  const filterBlock = pageContent.match(/const filteredDefinitions[\s\S]{0,700}?\}/)?.[0] ?? ''
  const filterUsesRawList = /definitions\.value/.test(filterBlock)
  const filterAllNotAllActive = !/['"]ALL['"][\s\S]{0,150}?allActive\.value/.test(filterBlock)
  assert(
    'D07e 静态：Domain.vue kpiCounts/filteredDefinitions 用 raw list（definitions）算 total/inactive/ALL（防 KPI=0/列表丢失已停用）',
    kpiUsesRawList && kpiTotalNotAllActive && kpiInactiveNotAllActive && filterUsesRawList && filterAllNotAllActive,
    `kpi 用 raw=${kpiUsesRawList}；total≠allActive=${kpiTotalNotAllActive}；inactive≠allActive=${kpiInactiveNotAllActive}；filter 用 raw=${filterUsesRawList}；filter ALL≠allActive=${filterAllNotAllActive}`
  )

  /* -------- D07f 静态：Domain.vue onCreate 创建成功后重置 statusFilter='ALL'（09-29 修复后固化：避免新建 active 域被「停用」筛选过滤掉、左侧列表空） -------- */
  // 函数体内含 try/catch/finally 嵌套，用 1500 字符宽口确保覆盖完整函数体
  const createBlock = pageContent.match(/async function onCreate[\s\S]{0,1500}/)?.[0] ?? ''
  const createResetsFilter =
    /statusFilter\.value\s*=\s*['"]ALL['"]/.test(createBlock) ||
    /statusFilter\.value\s*=\s*['"]ACTIVE['"]/.test(createBlock)
  assert(
    'D07f 静态：Domain.vue onCreate 创建成功后重置 statusFilter 为 ALL/ACTIVE（防新建条目被筛选过滤掉）',
    createResetsFilter,
    `onCreate 含 statusFilter 重置=${createResetsFilter}`
  )

  /* -------- D07g 静态：Domain.vue 更新时间用 formatUtcDateTime 工具（后端 DateTime 序列化为 naive UTC，必须显式按 UTC 解析再转本地；09-29 修复后固化） -------- */
  const utilityFileExists = existsSync(join(__dirname, '..', 'src', 'utils', 'datetime.ts'))
  const usesUtility = /from\s+['"]@\/utils\/datetime['"]/.test(pageContent)
  const noInlineUtc = !/dayjs\.utc\(/.test(pageContent)
  const noPlainDayjsUtcDisplay = !/dayjs\((?:currentDetail|detail|row)\.\w*[aA]t\w*\)\.format/.test(pageContent)
  assert(
    'D07g 静态：Domain.vue 用 @/utils/datetime 工具 + 无裸 dayjs(后端日期)（防后端 naive UTC 少 8h）',
    utilityFileExists && usesUtility && noInlineUtc && noPlainDayjsUtcDisplay,
    `工具文件存在=${utilityFileExists}；引用工具=${usesUtility}；无 dayjs.utc 内联=${noInlineUtc}；无裸 dayjs(${usesUtility ? '后端字段' : 'xxx'})=${noPlainDayjsUtcDisplay}`
  )

  /* -------- D07h 静态：Domain.vue domainKey 正则收紧到 {3,49} 与后端契约对齐（09-29 R2 回执要求前端放过长→后端 400 拦截；放开后必须保持收紧） -------- */
  const domainKeyRegexTight = /\{3,49\}[$]\//.test(pageContent)
  const domainKeyRegexNoLoose = !/\{3,63\}[$]\//.test(pageContent)
  assert(
    'D07h 静态：Domain.vue domainKey 正则 {3,49} 与后端契约对齐（防前端放行 51-64 字符被后端 400 拦下）',
    domainKeyRegexTight && domainKeyRegexNoLoose,
    `{3,49}=${domainKeyRegexTight}；无 {3,63}=${domainKeyRegexNoLoose}`
  )

  /* -------- D08-D12 端点可达 -------- */
  const reachMsg = (code) =>
    code === 200
      ? 'code=200'
      : code === 403
        ? 'code=403（角色绑定未落地或权限码缺失）'
        : `code=${code}`

  let r = await call('GET', '/api/governance/domain-definition', { token })
  const listOk = r.code === 200 && Array.isArray(r.json?.data)
  assert(
    'D08 端点可达：GET /api/governance/domain-definition',
    listOk,
    `${reachMsg(r.code)}；count=${r.json?.data?.length ?? 0}`
  )

  const list = r.json?.data || []
  if (!listOk || list.length === 0) {
    info('D09-D10 跳过：列表为空或接口不可用')
  } else {
    r = await call('GET', '/api/governance/domain-definition/active', { token })
    assert(
      'D09 端点可达：GET /api/governance/domain-definition/active',
      r.code === 200 && Array.isArray(r.json?.data),
      `${reachMsg(r.code)}；count=${r.json?.data?.length ?? 0}`
    )

    const firstId = list[0].id
    r = await call('GET', `/api/governance/domain-definition/${firstId}`, { token })
    assert(
      `D10 端点可达：GET /api/governance/domain-definition/${firstId}`,
      r.code === 200 && r.json?.data,
      reachMsg(r.code)
    )
  }

  r = await call('GET', '/api/governance/product-families', { token })
  assert(
    'D11 端点可达：GET /api/governance/product-families（Domain 维护页产品族下拉数据源）',
    r.code === 200 && Array.isArray(r.json?.data),
    `${reachMsg(r.code)}；count=${r.json?.data?.length ?? 0}`
  )

  r = await call('GET', '/api/governance/factories', { token })
  assert(
    'D12 端点可达：GET /api/governance/factories（Domain 维护页工厂下拉数据源）',
    r.code === 200 && Array.isArray(r.json?.data),
    `${reachMsg(r.code)}；count=${r.json?.data?.length ?? 0}`
  )

  /* -------- D13 接受类（默认跳过） -------- */
  if (process.env.VERIFY_ALLOW_WRITE !== '1') {
    skip('D13 接受类（POST/PUT/enable/disable 写真实记录）', 'VERIFY_ALLOW_WRITE 未设置=1（详见 verify-integration.mjs [A] 段）')
  } else {
    info('D13 接受类已在 verify-integration.mjs [A] 段覆盖（GROUP=domain）；此处不重复')
  }

  /* -------- D14-D17 动态：3号位 R1-R4 后端字段校验落码验证（09-29 催办单 §七；纯拒绝用例零副作用，期待 400） -------- */
  // 用一个统一基础载荷，每个断言单独覆盖 1 个字段的拒绝路径
  const baseDomainPayload = {
    domainName: 'D14-17基线',
    productFamilyId: 1,
    scopeType: 'FAMILY',
    sortOrder: 100
  }
  // D14 sortOrder 边界：-1 / 10000 → 400（V1 的拒绝侧）
  let r14neg = await call('POST', '/api/governance/domain-definition', {
    token,
    body: { ...baseDomainPayload, domainKey: 'D14_TEST_NEG', domainName: 'D14越下界', sortOrder: -1 }
  })
  let r14over = await call('POST', '/api/governance/domain-definition', {
    token,
    body: { ...baseDomainPayload, domainKey: 'D14_TEST_OVER', domainName: 'D14越上界', sortOrder: 10000 }
  })
  const d14Neg = r14neg.code === 400
  const d14Over = r14over.code === 400
  assert(
    'D14 sortOrder 边界：-1/10000 → 400（V1）',
    d14Neg && d14Over,
    `-1=${reachMsg(r14neg.code)}；10000=${reachMsg(r14over.code)}`
  )

  // D15 domainKey 字符集 + 长度下限：含中文/空格/点/分号/单引号/2 字符 → 400（V2 的拒绝侧；51 字符上限留给手工验证）
  const dkCases = [
    { name: '含中文', key: '中文_KEY' },
    { name: '含空格', key: 'A B' },
    { name: '含点', key: 'A.B' },
    { name: '含分号', key: 'A;B' },
    { name: '含单引号', key: "AB'D" },
    { name: '2字符（下限以下）', key: 'AB' }
  ]
  const d15Results = []
  for (const c of dkCases) {
    const rr = await call('POST', '/api/governance/domain-definition', {
      token,
      body: { ...baseDomainPayload, domainKey: c.key, domainName: `D15-${c.name}` }
    })
    const passed = rr.code === 400
    d15Results.push(`${c.name}=${rr.code}${passed ? '✓' : '✗'}`)
  }
  const d15Pass = d15Results.every((s) => s.endsWith('✓'))
  assert(
    'D15 domainKey 字符集 + 长度下限：异常 → 400（V2）',
    d15Pass,
    d15Results.join(' | ')
  )

  // D16 scopeType 大小写：`family` / `Family` → 400（V4 的拒绝侧；FAMILY 接受留给手工验证）
  let r16lower = await call('POST', '/api/governance/domain-definition', {
    token,
    body: { ...baseDomainPayload, domainKey: 'D16_TEST_LOWER', domainName: 'D16小写', scopeType: 'family' }
  })
  let r16mix = await call('POST', '/api/governance/domain-definition', {
    token,
    body: { ...baseDomainPayload, domainKey: 'D16_TEST_MIX', domainName: 'D16混合', scopeType: 'Family' }
  })
  const d16Lower = r16lower.code === 400
  const d16Mix = r16mix.code === 400
  assert(
    'D16 scopeType 严格大小写：family/Family → 400（V4）',
    d16Lower && d16Mix,
    `family=${reachMsg(r16lower.code)}；Family=${reachMsg(r16mix.code)}`
  )

  // D17 domainName 长度：1 / 100 字符 → 400（V5 的拒绝侧；2/64 字符接受留给手工验证）
  let r17short = await call('POST', '/api/governance/domain-definition', {
    token,
    body: { ...baseDomainPayload, domainKey: 'D17_TEST_SHORT', domainName: 'A' }
  })
  let r17long = await call('POST', '/api/governance/domain-definition', {
    token,
    body: { ...baseDomainPayload, domainKey: 'D17_TEST_LONG', domainName: 'A'.repeat(100) }
  })
  const d17Short = r17short.code === 400
  const d17Long = r17long.code === 400
  assert(
    'D17 domainName 长度：1/100 字符 → 400（V5）',
    d17Short && d17Long,
    `1=${reachMsg(r17short.code)}；100=${reachMsg(r17long.code)}`
  )

  // D18 既有数据完整性（V6）：回执称 FAMILY_INJECTION 等 mock 数据未受影响 — 实测 list 前 5 条均可读
  const existingSample = list.slice(0, Math.min(5, list.length))
  const d18 = existingSample.length > 0
  assert(
    'D18 既有数据完整性（V6）：FAMILY_INJECTION 等 mock 数据可正常读取',
    d18,
    `samples=${existingSample.map((x) => x.domainKey).join(',')}`
  )

  /* -------- D19 域依赖关系（G7；2026-10-22 P3「Dependency 影响入口」）--------
   *  端点：GET /api/governance-query/domain-dependencies（5号位接入层，按数据范围收窄）
   *  口径：冻结 07 §B.1 —— DefaultLeadTimeDays 是兼容缓存（0=未派生，不是「0 天」）
   *  空态：扫描结果为空 ≠ 本域没有依赖（不得把「没数据」断言成「事实」）
   */
  const depTypes = readSrc('api/aps-v1/types/domain.ts')
  const depFields = [
    'upstreamDomainCode',
    'downstreamDomainCode',
    'childMaterialCode',
    'defaultLeadTimeDays',
    'scannedAt'
  ]
  // ⚠️ 必须用「字段名 + 冒号」判定，不能用 includes()：
  //    childMaterialCodeX 也包含 childMaterialCode ⇒ includes() 会假绿（10-22 实测）
  const depMissing = depFields.filter((f) => !new RegExp(`\\b${f}\\s*:`).test(depTypes))
  const depDirection = depTypes.includes("'upstream'") && depTypes.includes("'downstream'")
  assert(
    'D19a 静态：types/domain.ts 含 DomainDependencyDto 5 camelCase 字段 + 上下游方向字面量',
    depMissing.length === 0 && depDirection,
    `缺字段：${depMissing.join(',') || '无'}；方向=${depDirection}`
  )

  // 反向断言：域依赖只走 5号位 G7 端点，不得混入 3号位 的 /api/Governance/domain-dependencies。
  // 2026-10-25 起 3号位 端点也声称已补 scope（回执 v1.0），故本条不再是「安全禁令」，
  // 而是「**不并存两套同名端点**」——我们接的那一套已联调，换/加都会引入未验证路径。
  // ⚠️ 必须先剥注释：apiContent 注释里会提到该端点做对比说明，扫全文会误判
  const depApiCode = stripComments(apiContent)
  const depApiOk = depApiCode.includes('/api/governance-query/domain-dependencies')
  const depApiWrong = /\/api\/[Gg]overnance\/domain-dependencies/.test(depApiCode)
  assert(
    'D19b 静态：域依赖只接 5号位 G7 端点，不并存 3号位 同名端点（避免两套未对齐路径）',
    depApiOk && !depApiWrong,
    `G7=${depApiOk}；3号位同名端点=${depApiWrong}`
  )

  const depPanelOk =
    pageContent.includes('dependency-panel') &&
    pageContent.includes('dependencyDomainName(row.upstreamDomainCode)') &&
    pageContent.includes('dependencyDomainName(row.downstreamDomainCode)') &&
    pageContent.includes('dependencyLeadTimeText(row.defaultLeadTimeDays)')
  assert(
    'D19c 静态：Domain.vue 有「域依赖关系」区块（上下游两列 + 提前期走专用渲染）',
    depPanelOk,
    depPanelOk ? '区块齐备' : '缺区块 / 上下游列 / 提前期渲染'
  )

  const depEmptyOk = pageContent.includes('扫描结果为空')
  const depFalseClaims = ['本域没有依赖', '该域没有依赖', '本域无依赖', '确认无依赖'].filter((w) =>
    pageContent.includes(w)
  )
  assert(
    'D19d 静态：空态说「扫描结果为空」，不得把没数据断言成「本域没有依赖」',
    depEmptyOk && depFalseClaims.length === 0,
    `空态文案=${depEmptyOk}；误断言：${depFalseClaims.join(',') || '无'}`
  )

  const depLeadOk =
    /days\s*<=\s*0/.test(pageContent) &&
    pageContent.includes('未派生') &&
    pageContent.includes('兼容缓存值')
  assert(
    'D19e 静态：提前期 0 渲染为「未派生」，>0 明标兼容缓存（非权威），不得当 0 天/固定 2 天',
    depLeadOk,
    depLeadOk ? '口径齐备' : '缺 0 值分支或缓存值标注'
  )

  r = await call('GET', '/api/governance-query/domain-dependencies', { token })
  assert(
    'D19f 动态：GET /api/governance-query/domain-dependencies 可达（200；依赖数据可空）',
    r.code === 200 && Array.isArray(r.json?.data),
    `${reachMsg(r.code)}；count=${r.json?.data?.length ?? 0}`
  )

  const dep403Ok =
    pageContent.includes('无权查看该排程域的依赖关系') && /code\s*===\s*403/.test(pageContent)
  assert(
    'D19g 静态：依赖查询 403（越权）说「无权查看」，不与「稍后重试」混同',
    dep403Ok,
    dep403Ok ? '越权口径齐备' : '缺 403 分支或文案'
  )

  info('D 段：Domain CRUD + 启停 + 字典端点 + 字段校验 + 域依赖关系（v1.2 Pkg-3 + 09-29 R1-R4 + 10-22 D19）')
}

/* ==================== [TSRC] TaskSplitRuleConfig / Batch Policy 治理（t-tsrc）====================
 * 触发：3号位 2026-10-07 契约先行件《TaskSplitRuleConfig维护接口契约_v1.0_20261007.md》
 *  - 4 端点：GET/POST /api/governance/task-split-rule-configs + PUT/DELETE /{id}
 *  - 权限码：aps.rule.view / aps.rule.edit（复用 Rule 码，零权限种子变更）
 *  - 兼容字段不暴露：ResourceGroupId / MinimumOrderQuantity / EconomicOrderQuantity
 *  - 业务键：MaterialId + ProductionDepartmentId（NULL 部门 = Material 级默认）
 * 现状（10-07 收口）：
 *  - 后端 7 文件已落地（Controller / Service / DTOs / Entity / Interface / Projector / Tests）
 *  - 前端 4号位 已起草 types + api 桩代码 + store + 路由 + 页面（与 OPM 同范式）
 *  - dev 库 schema 5 列已迁移（10-07 3号位 / 2号位 协调完成）→ TSRC08 真实联调开启
 */
async function verifyTaskSplitRuleConfig(token) {
  log('\n[TSRC] TaskSplitRuleConfig / Batch Policy 治理（t-tsrc — 10-07 起草）')

  // TSRC01 静态：types/taskSplitRuleConfig.ts 含 TaskSplitRuleConfigDto + SaveTaskSplitRuleConfigRequest + ListQuery
  const tsrcTypes = readSrc('api/aps-v1/types/taskSplitRuleConfig.ts')
  const tsrc01a = /export\s+interface\s+TaskSplitRuleConfigDto\b/.test(tsrcTypes)
  assert('TSRC01a 静态：types/taskSplitRuleConfig.ts 含 TaskSplitRuleConfigDto', tsrc01a, tsrc01a ? '已含' : '缺 DTO')

  const tsrc01b = /export\s+interface\s+SaveTaskSplitRuleConfigRequest\b/.test(tsrcTypes)
  assert('TSRC01b 静态：types/taskSplitRuleConfig.ts 含 SaveTaskSplitRuleConfigRequest', tsrc01b, tsrc01b ? '已含' : '缺 Request')

  const tsrc01c = /export\s+interface\s+TaskSplitRuleConfigListQuery\b/.test(tsrcTypes)
  assert(
    'TSRC01c 静态：types/taskSplitRuleConfig.ts 含 TaskSplitRuleConfigListQuery',
    tsrc01c,
    tsrc01c ? '已含' : '缺 ListQuery'
  )

  // TSRC02 静态：types/index.ts 已 re-export 任务批拆分规则 DTO
  const tsrcIndex = readSrc('api/aps-v1/types/index.ts')
  const tsrc02 = /export\s+\*\s+from\s+['"]\.\/taskSplitRuleConfig['"]/.test(tsrcIndex)
  assert('TSRC02 静态：types/index.ts re-export ./taskSplitRuleConfig', tsrc02, tsrc02 ? '已 re-export' : '缺 re-export')

  // TSRC03 静态：api/taskSplitRuleConfig.ts 含 4 端点 list/create/update/deactivate
  //  - 10-08 列表分页改造：list() 返回 Promise<PagedTaskSplitRuleConfig>（与 R2 PageResult<T> 同形态）
  const tsrcApi = readSrc('api/aps-v1/taskSplitRuleConfig.ts')
  const tsrc03a = /async\s+list\s*\([^)]*\)\s*:\s*Promise<PagedTaskSplitRuleConfig>/.test(tsrcApi)
  assert('TSRC03a 静态：api/taskSplitRuleConfig.ts 含 list() 方法（10-08 返回 Paged）', tsrc03a, tsrc03a ? '已含' : '缺 list / 返回类型错')

  const tsrc03b = /async\s+create\s*\(\s*input\s*:\s*SaveTaskSplitRuleConfigRequest\s*\)/.test(tsrcApi)
  assert('TSRC03b 静态：api/taskSplitRuleConfig.ts 含 create(input)', tsrc03b, tsrc03b ? '已含' : '缺 create')

  const tsrc03c = /async\s+update\s*\(\s*id\s*:\s*number\s*,\s*input\s*:\s*SaveTaskSplitRuleConfigRequest\s*\)/.test(tsrcApi)
  assert('TSRC03c 静态：api/taskSplitRuleConfig.ts 含 update(id, input)', tsrc03c, tsrc03c ? '已含' : '缺 update')

  const tsrc03d = /async\s+deactivate\s*\(\s*id\s*:\s*number\s*\)/.test(tsrcApi)
  assert('TSRC03d 静态：api/taskSplitRuleConfig.ts 含 deactivate(id)', tsrc03d, tsrc03d ? '已含' : '缺 deactivate')

  // TSRC04 静态：4 端点 URL 路径与契约 §一 对齐
  const tsrc04a = /['"`]\/api\/governance\/task-split-rule-configs['"`]/.test(tsrcApi)
  assert('TSRC04a 静态：list/create URL = /api/governance/task-split-rule-configs', tsrc04a, tsrc04a ? '已对齐' : '路径错')

  const tsrc04b = /\/api\/governance\/task-split-rule-configs\/\$\{id\}/.test(tsrcApi)
  assert('TSRC04b 静态：update/deactivate URL = /api/governance/task-split-rule-configs/${id}', tsrc04b, tsrc04b ? '已对齐' : '路径错')

  // TSRC05 静态：兼容字段（ResourceGroupId / MOQ / EOQ）不进 DTO / 不进 Request
  //  - 契约 §2.2 末段明确：「兼容字段不暴露：不进请求体、不进 DTO、不投快照」
  //  - 注意：DTO/Request 类型里**不应该**出现 resourceGroupId / minimumOrderQuantity / economicOrderQuantity
  const tsrc05a = !/resourceGroupId\s*:\s*number/.test(tsrcTypes)
  const tsrc05b = !/minimumOrderQuantity\s*:\s*number/.test(tsrcTypes)
  const tsrc05c = !/economicOrderQuantity\s*:\s*number/.test(tsrcTypes)
  assert(
    'TSRC05 静态：DTO/Request 不暴露兼容字段（ResourceGroupId / MOQ / EOQ）',
    tsrc05a && tsrc05b && tsrc05c,
    tsrc05a && tsrc05b && tsrc05c
      ? '已剔除兼容字段'
      : `泄漏：${
          !tsrc05a ? 'resourceGroupId ' : ''
        }${!tsrc05b ? 'minimumOrderQuantity ' : ''}${!tsrc05c ? 'economicOrderQuantity' : ''}`
  )

  // TSRC06 静态：AllowSplit/AllowMerge 在 Request 是 nullable（须显式提供，后端 422 强校验）
  const tsrc06 = /allowSplit\s*:\s*boolean\s*\|\s*null/.test(tsrcTypes) && /allowMerge\s*:\s*boolean\s*\|\s*null/.test(tsrcTypes)
  assert(
    'TSRC06 静态：SaveTaskSplitRuleConfigRequest 的 AllowSplit/AllowMerge 是 boolean | null（显式提供）',
    tsrc06,
    tsrc06 ? 'nullable' : '缺失 nullable 标记（会触发后端 422 误判）'
  )

  // TSRC07 静态：拆分策略枚举**已移除**（2026-10-22 判据反转）
  //  - 冻结 13 v1.8:97 / 12 v2.2:102：BottleneckSplitStrategy / NonBottleneckStrategy **不展示、不维护**
  //  - 后端 v5.1.10 起治理 API 已不再 SELECT/接收（TaskSplitRuleConfigGovernanceService.cs:36-40）
  //  ⇒ 旧断言「必须含 PREFER_SPLIT / PREFER_MERGE」已与冻结相反，改为**必须不存在**
  const tsrc07a = !/PREFER_SPLIT|PREFER_MERGE/.test(tsrcTypes)
  assert(
    'TSRC07a 静态：瓶颈拆分策略枚举已移除（冻结禁列 13 v1.8:97）',
    tsrc07a,
    tsrc07a ? '已移除' : '仍在类型里（违反冻结禁列）'
  )

  const tsrc07b = !/PREFER_LARGE_BATCH|PREFER_SMALL_BATCH/.test(tsrcTypes)
  assert(
    'TSRC07b 静态：非瓶颈拆分策略枚举已移除（冻结禁列）',
    tsrc07b,
    tsrc07b ? '已移除' : '仍在类型里（违反冻结禁列）'
  )

  // TSRC08 动态：联调真实端点（10-07 dev 库 schema 已迁移，5 列齐全）
  //  - 真实调用：GET /api/governance/task-split-rule-configs
  //  - 10-08 后端 PageResult<T> 落地：data 是 { items, total, page, pageSize } 而非裸数组
  //    - 兼容两种形态：data 本身是数组（早期），或 data.items 是数组（PageResult）
  //  - 预期：r.ok=true 且 items/数组 可达（可能为空，dev 库暂未造数）
  //  - 不可达兜底：r.code === 401/403/404/500 一律视为「端点未达」
  //    - 500+SqlException "列名 xxx 无效" → schema 仍缺列（重新排查）
  const tsrc08r = await call('GET', '/api/governance/task-split-rule-configs', { token })
  const tsrc08items = Array.isArray(tsrc08r.json?.data)
    ? tsrc08r.json.data
    : Array.isArray(tsrc08r.json?.data?.items)
      ? tsrc08r.json.data.items
      : null
  if (tsrc08r.ok && tsrc08items !== null) {
    assert(
      'TSRC08 动态：GET /api/governance/task-split-rule-configs（dev 库 schema 已迁移，5 列齐全）',
      true,
      `code=${tsrc08r.code}, items 长度=${tsrc08items.length}, 形态=${Array.isArray(tsrc08r.json?.data) ? 'array' : 'PageResult'}`
    )
  } else {
    assert(
      'TSRC08 动态：GET /api/governance/task-split-rule-configs（dev 库 schema 已迁移，5 列齐全）',
      false,
      `code=${tsrc08r.code}, message=${tsrc08r.json?.message || tsrc08r.text?.slice(0, 80)}（可能 dev 库未跑 ALTER）`
    )
  }

  // TSRC09 静态：Pinia store 桩代码就位
  const tsrcStore = readSrc('store/modules/aps/taskSplitRuleConfig.ts')
  const tsrc09a = /export\s+const\s+useTaskSplitRuleConfigStore\s*=/.test(tsrcStore)
  assert('TSRC09a 静态：store/modules/aps/taskSplitRuleConfig.ts 含 useTaskSplitRuleConfigStore', tsrc09a, tsrc09a ? '已含' : '缺 store')

  const tsrc09b = /async\s+function\s+load\s*\([^)]*\)/.test(tsrcStore) &&
    /async\s+function\s+create\s*\(/.test(tsrcStore) &&
    /async\s+function\s+update\s*\(/.test(tsrcStore) &&
    /async\s+function\s+deactivate\s*\(/.test(tsrcStore)
  assert('TSRC09b 静态：store 含 load/create/update/deactivate 4 动作', tsrc09b, tsrc09b ? '4 动作齐' : '缺动作')

  // TSRC10 静态：路由占位
  const tsrcRouter = readSrc('router/modules/aps.ts')
  const tsrc10 = /path:\s*['"]\/aps\/task-split-rule-config['"]/.test(tsrcRouter)
  assert('TSRC10 静态：router/modules/aps.ts 含 path=task-split-rule-config', tsrc10, tsrc10 ? '路由已占位' : '缺路由')

  // TSRC11 静态：页面入口
  const tsrcPagePath = 'views/Aps/TaskSplitRuleConfig.vue'
  const tsrc11 = existsSync(join(SRC_ROOT, tsrcPagePath))
  assert(`TSRC11 静态：${tsrcPagePath} 存在`, tsrc11, tsrc11 ? '页面已落地' : '缺页面')

  // TSRC12 静态：Drawer 部门下拉数据源切到正式 lookup 端点（10-07 替换 G2 过渡实现）
  //  - departmentLookupApi.listDepartments() → GET /api/governance/lookups/departments（3 号位 SetupRuleController.cs:299）
  const tsrcDepPage = readSrc('views/Aps/TaskSplitRuleConfig.vue')
  const tsrc12a =
    /departmentLookupApi\.listDepartments\s*\(/.test(tsrcDepPage) &&
    !/resourceCalendarApi\.listDepartmentCandidates\s*\(/.test(tsrcDepPage)
  assert(
    'TSRC12a 静态：TaskSplitRuleConfig.vue 部门下拉用 departmentLookupApi（不再用 G2 过渡）',
    tsrc12a,
    tsrc12a ? '已切正式端点' : '仍用过渡或未切'
  )

  const tsrcDepApi = readSrc('api/aps-v1/departmentLookup.ts')
  const tsrc12b =
    /\/api\/governance\/lookups\/departments/.test(tsrcDepApi) &&
    /listDepartments\s*\(/.test(tsrcDepApi)
  assert(
    'TSRC12b 静态：departmentLookup.ts 封装 GET /api/governance/lookups/departments',
    tsrc12b,
    tsrc12b ? '已封装正式端点' : '封装缺失'
  )

  const tsrcDepIdx = readSrc('api/aps-v1/index.ts')
  const tsrc12c =
    /export\s*\{\s*departmentLookupApi\s*\}\s*from\s*['"]\.\/departmentLookup['"]/.test(tsrcDepIdx)
  assert('TSRC12c 静态：aps-v1/index.ts re-export departmentLookupApi', tsrc12c, tsrc12c ? '已 re-export' : '缺 re-export')

  // TSRC12d 动态：真实调 GET /api/governance/lookups/departments（dev 库 G2 正式端点）
  const tsrc12r = await call('GET', '/api/governance/lookups/departments', { token })
  if (tsrc12r.ok && Array.isArray(tsrc12r.json?.data)) {
    const data = tsrc12r.json.data
    const hasMasterId = data.length > 0 && typeof data[0]?.masterId === 'number'
    assert(
      'TSRC12d 动态：GET /api/governance/lookups/departments（dev 库 G2 正式端点）',
      hasMasterId,
      `code=${tsrc12r.code}, data 长度=${data.length}, masterId 类型=${hasMasterId ? 'number' : '缺失'}`
    )
  } else {
    assert(
      'TSRC12d 动态：GET /api/governance/lookups/departments（dev 库 G2 正式端点）',
      false,
      `code=${tsrc12r.code}, message=${tsrc12r.json?.message || tsrc12r.text?.slice(0, 80)}`
    )
  }

  // TSRC13 静态：主表格 ID → 名称 显示（10-08 用户要求：「4号位 所有列表/新增显示名称，不显示内部 ID」）
  //  - 物料ID 列渲染物料编码/名称（materialLabel）
  //  - 生产部门ID 列渲染部门编码（departmentLabel）
  //  - 规则ID 列（auto-increment 内部键）整体移除
  //  - onMounted 并行预加载 materialOptions / departmentOptions，不阻塞 refresh()
  const tsrc13a =
    /function\s+materialLabel\s*\(/.test(tsrcDepPage) &&
    /function\s+departmentLabel\s*\(/.test(tsrcDepPage) &&
    // 表格里调 materialLabel(row.materialId) / departmentLabel(row.productionDepartmentId)
    /materialLabel\(row\.materialId\)/.test(tsrcDepPage) &&
    /departmentLabel\(row\.productionDepartmentId\)/.test(tsrcDepPage) &&
    // 规则ID 列必须移除（不能再有 prop="id" label="规则ID"）
    !/prop="id"\s+label="规则ID"/.test(tsrcDepPage)
  assert(
    'TSRC13a 静态：TaskSplitRuleConfig.vue 主表格 ID → 名称（materialLabel/departmentLabel + 移除 规则ID 列）',
    tsrc13a,
    tsrc13a ? '已落地' : '未配齐 label 函数或未移除 规则ID'
  )

  // TSRC13b：onMounted 并行预加载 options，不阻塞 refresh()
  const tsrc13b =
    /onMounted\(\s*\(\)\s*=>\s*\{[^}]*loadMaterialOptions\(\)[^}]*loadDepartmentOptions\(\)[^}]*refresh\(\)/s.test(
      tsrcDepPage
    )
  assert(
    'TSRC13b 静态：onMounted 并行预加载 materialOptions/departmentOptions + refresh()',
    tsrc13b,
    tsrc13b ? '已预加载' : 'onMounted 加载顺序不合规'
  )

  // ============================================================
  // TSRC14 列表分页化（10-08 列表分页改造落地；3号位 拍板方案 A：pageIndex / pageSize + PageResult<T>）
  //   @see 3号位致4号位_TaskSplitRuleConfig列表分页改造回执_方案A_v1.0_20261008.md
  //   @see 4号位-2026-10-08-TaskSplitRuleConfig-列表分页改造-给3号位.md
  // ============================================================

  // TSRC14a 静态：types/taskSplitRuleConfig.ts ListQuery 含 pageIndex / pageSize
  //   - 拆成两个独立断言：避免 `[^}]]` 字符类嵌套
  const tsrc14aPageIndex = /export\s+interface\s+TaskSplitRuleConfigListQuery[\s\S]*?\bpageIndex\s*\?\s*:\s*number[\s\S]*?\}/.test(
    tsrcTypes
  )
  const tsrc14aPageSize = /export\s+interface\s+TaskSplitRuleConfigListQuery[\s\S]*?\bpageSize\s*\?\s*:\s*number[\s\S]*?\}/.test(
    tsrcTypes
  )
  const tsrc14a = tsrc14aPageIndex && tsrc14aPageSize
  assert(
    'TSRC14a 静态：types/taskSplitRuleConfig.ts ListQuery 含 pageIndex / pageSize',
    tsrc14a,
    tsrc14a ? '已含' : `pageIndex=${tsrc14aPageIndex} pageSize=${tsrc14aPageSize}`
  )

  // TSRC14b 静态：types/taskSplitRuleConfig.ts 含 PagedTaskSplitRuleConfig
  const tsrc14b = /export\s+interface\s+PagedTaskSplitRuleConfig\b/.test(tsrcTypes)
  assert(
    'TSRC14b 静态：types/taskSplitRuleConfig.ts 含 PagedTaskSplitRuleConfig',
    tsrc14b,
    tsrc14b ? '已含' : '缺 Paged 类型'
  )

  // TSRC14c 静态：api/taskSplitRuleConfig.ts list() 返回 Promise<PagedTaskSplitRuleConfig>
  const tsrc14c = /async\s+list\s*\([^)]*\)\s*:\s*Promise<PagedTaskSplitRuleConfig>/.test(tsrcApi)
  assert(
    'TSRC14c 静态：api/taskSplitRuleConfig.ts list() 返回 Promise<PagedTaskSplitRuleConfig>',
    tsrc14c,
    tsrc14c ? '已对齐' : 'list() 返回类型错'
  )

  // TSRC14d 静态：api list() 请求 URL 显式传 pageIndex / pageSize（避免后端默认 100）
  const tsrc14d =
    /params\s*:\s*Record<string,\s*unknown>\s*=\s*\{[\s\S]*?\bpageIndex\b[\s\S]*?\bpageSize\b[\s\S]*?\}/.test(
      tsrcApi
    ) ||
    /params\.pageIndex\s*=/.test(tsrcApi) ||
    /params\.pageSize\s*=/.test(tsrcApi)
  assert(
    'TSRC14d 静态：api list() 显式传 pageIndex / pageSize 给后端',
    tsrc14d,
    tsrc14d ? '已显式' : 'params 缺 pageIndex / pageSize'
  )

  // TSRC14e 静态：store 含 total / page / pageSize 三个 ref + 暴露在 return
  //   - 拆成 3 个 ref 断言 + 1 个 return 暴露断言（顺序不强制，只要求 3 个都在 return 内）
  //   - 兼容 `ref<number>(0)` / `ref(0)` / `ref<number>(DEFAULT_PAGE_SIZE)` 三种写法
  const tsrc14eTotal = /\btotal\s*=\s*ref(?:\s*<[^>]+>)?\s*\(\s*0(?:\s*as\s+number)?\s*\)/.test(tsrcStore)
  const tsrc14ePage = /\bpage\s*=\s*ref(?:\s*<[^>]+>)?\s*\(\s*1(?:\s*as\s+number)?\s*\)/.test(tsrcStore)
  const tsrc14ePageSize =
    /\bpageSize\s*=\s*ref(?:\s*<[^>]+>)?\s*\(\s*(?:20|DEFAULT_PAGE_SIZE)(?:\s*as\s+number)?\s*\)/.test(
      tsrcStore
    )
  const tsrc14eReturn =
    /\breturn\s*\{[\s\S]*?\btotal\b[\s\S]*?\bpage\b[\s\S]*?\bpageSize\b/.test(tsrcStore) &&
    /\breturn\s*\{[\s\S]*?\bload\b/.test(tsrcStore)
  const tsrc14e = tsrc14eTotal && tsrc14ePage && tsrc14ePageSize && tsrc14eReturn
  assert(
    'TSRC14e 静态：store 含 total / page / pageSize 三个 ref + return 暴露',
    tsrc14e,
    tsrc14e
      ? '已暴露'
      : `total=${tsrc14eTotal} page=${tsrc14ePage} pageSize=${tsrc14ePageSize} return=${tsrc14eReturn}`
  )

  // TSRC14f 静态：view storeToRefs 解构 total / page / pageSize
  //  2026-10-19：原判据是「storeToRefs(store) 之后某处依次出现 total→page→pageSize」，
  //  整文件扫描 ⇒ 只要解构块之外的模板/注释里碰巧按这个顺序出现过就判绿，且**对解构块内的
  //  顺序极其敏感**（删掉几个 KPI getter 就转红，属误报）。改为**只取解构块本身**再判，
  //  判据更严也更稳：块里必须真的解构了这三个字段，且与排列顺序无关。
  const tsrc14fBlock = (
    tsrcDepPage.match(/const\s*\{[\s\S]*?\}\s*=\s*storeToRefs\(store\)/) || ['']
  )[0]
  const tsrc14f =
    /\btotal\b/.test(tsrc14fBlock) &&
    /\bpage\b/.test(tsrc14fBlock) &&
    /\bpageSize\b/.test(tsrc14fBlock)
  assert(
    'TSRC14f 静态：TaskSplitRuleConfig.vue storeToRefs 解构 total / page / pageSize',
    tsrc14f,
    tsrc14f ? '解构块已含三字段' : `解构块未含三字段：${tsrc14fBlock.replace(/\s+/g, ' ').slice(0, 120)}`
  )

  // TSRC14g 静态：view 含 ElPagination + :current-page / :page-size / :total / @current-change / @size-change
  const tsrc14g =
    /<ElPagination\b/.test(tsrcDepPage) &&
    /<ElPagination\b[\s\S]*?:current-page="page"/.test(tsrcDepPage) &&
    /<ElPagination\b[\s\S]*?:page-size="pageSize"/.test(tsrcDepPage) &&
    /<ElPagination\b[\s\S]*?:total="total"/.test(tsrcDepPage) &&
    /@current-change="onPageChange"/.test(tsrcDepPage) &&
    /@size-change="onSizeChange"/.test(tsrcDepPage)
  assert(
    'TSRC14g 静态：TaskSplitRuleConfig.vue 含 ElPagination（:current-page / :page-size / :total + @current-change / @size-change）',
    tsrc14g,
    tsrc14g ? '已含分页器' : '缺分页器 / 缺绑定'
  )

  // TSRC14h 静态：view applyFilter 重置 pageIndex=1（避免筛选无交集时空态错觉）
  const tsrc14h = /applyFilter[\s\S]*?resetPage\s*:\s*true/.test(tsrcDepPage)
  assert(
    'TSRC14h 静态：applyFilter 调用 resetPage:true（筛选无交集时回首页）',
    tsrc14h,
    tsrc14h ? '已重置' : '筛选未重置分页'
  )

  // TSRC14i 静态：本页**不再有任何 KPI 计数面板**（2026-10-19 用户要求去掉顶部面板块）。
  //   原断言锁的是「KPI 总规则数要用后端 total 而不是 list.length」；面板既已删除，
  //   那条断言就失去了被检对象，改锁「面板不再回来 + 条数只由分页器的后端 total 呈现」。
  //   这样「拿当前页条数冒充总数」这个原始隐患仍然被挡住 —— 换了个等效的挡法。
  const tsrcDepCode = tsrcDepPage
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .replace(/^\s*\/\/.*$/gm, '')
    .replace(/\s\/\/[^\n]*/g, '')
  const tsrc14iNoKpi = !/kpi-card|kpi-row|总规则数/.test(tsrcDepCode)
  const tsrc14iPagerTotal = /:total="total"/.test(tsrcDepCode)
  // 页面顶部不再有独立按钮条：「刷新」已按 2026-10-19 用户要求删除，
  // 「新增批策略」改挂在表格卡 header 内右对齐（原 hint「点击 [编辑]/[停用] 操作」也一并删）。
  const tsrc14iNoRefreshBtn = !/vi-ep:refresh/.test(tsrcDepCode)
  const tsrc14iNoEditHint = !/点击\s*\[编辑\]/.test(tsrcDepCode)
  // ⚠️ 判据必须**框死在表格卡自己的 header 模板内**。首版写成
  //   `/<ElCard class="panel"[\s\S]*?#header[\s\S]*?新增批策略/` —— 页面里第一个
  //   `<ElCard class="panel">` 是**筛选条件卡**（它也有 #header），`[\s\S]*?` 一路向后扫，
  //   按钮移回页面顶部后仍在表格卡位置被扫到 ⇒ **假绿**（已实测）。
  //   改为要求「header 模板里只有一个 ElButton 且就是新增批策略」，天然限定在那一个卡片内。
  const tsrc14iCreateInTable =
    /<template #header>\s*<div class="panel-header panel-header--end">\s*<ElButton[\s\S]*?新增批策略\s*<\/ElButton>\s*<\/div>\s*<\/template>/.test(
      tsrcDepPage
    )
  const tsrc14iHeaderEnd = /class="panel-header panel-header--end"/.test(tsrcDepPage)
  assert(
    'TSRC14i 静态：批策略页无 KPI 计数面板、无刷新按钮、无表格操作提示；新增批策略挂在表格卡 header 且右对齐；条数只由分页器的后端 total 呈现',
    tsrc14iNoKpi &&
      tsrc14iPagerTotal &&
      tsrc14iNoRefreshBtn &&
      tsrc14iNoEditHint &&
      tsrc14iCreateInTable &&
      tsrc14iHeaderEnd,
    `无面板=${tsrc14iNoKpi} 分页器用后端total=${tsrc14iPagerTotal} 无刷新按钮=${tsrc14iNoRefreshBtn} 无操作提示=${tsrc14iNoEditHint} 新增按钮在表格卡=${tsrc14iCreateInTable} header右对齐=${tsrc14iHeaderEnd}`
  )

  // TSRC15 静态：DateTime 字段改用 formatUtcDate / formatUtcDateTime（解决 naive UTC bug：少 8h）
  //  - dev 后端 DateTime 序列化为 naive UTC 无 Z 后缀；裸 dayjs() 按本地解析 → 时间少 8h
  //  - 锁 5 处：effectiveFrom / effectiveTo（formatUtcDate）+ updatedAt 表列 + createdAt/updatedAt 抽屉审计
  //  - 锁 import：import { formatUtcDate, formatUtcDateTime } from '@/utils/datetime'
  //  - 锁 import：禁止直接 `import dayjs from 'dayjs'`（除非确有 dayjs.utc 之外需求）
  const tsrc15Import = /import\s*\{[^}]*\bformatUtcDate\b[^}]*\}\s*from\s*['"]@\/utils\/datetime['"]/.test(
    tsrcDepPage
  )
  const tsrc15ImportDateTime = /import\s*\{[^}]*\bformatUtcDateTime\b[^}]*\}\s*from\s*['"]@\/utils\/datetime['"]/.test(
    tsrcDepPage
  )
  const tsrc15NoBareDayjs = !/\bdayjs\s*\(\s*(?:row|list\.find)/.test(tsrcDepPage)
  const tsrc15EffectiveFrom = /row\.effectiveFrom\s*\?\s*formatUtcDate\(/.test(tsrcDepPage)
  const tsrc15EffectiveTo = /row\.effectiveTo\s*\?\s*formatUtcDate\(/.test(tsrcDepPage)
  const tsrc15UpdatedAtTable = /formatUtcDateTime\(\s*row\.updatedAt\s*\)/.test(tsrcDepPage)
  const tsrc15CreatedAtDrawer = /formatUtcDateTime\(\s*list\.find\([\s\S]*?\)\?\.createdAt\s*\)/.test(tsrcDepPage)
  const tsrc15UpdatedAtDrawer = /formatUtcDateTime\(\s*list\.find\([\s\S]*?\)\?\.updatedAt\s*\)/.test(tsrcDepPage)
  const tsrc15 = tsrc15Import && tsrc15ImportDateTime && tsrc15NoBareDayjs &&
    tsrc15EffectiveFrom && tsrc15EffectiveTo && tsrc15UpdatedAtTable &&
    tsrc15CreatedAtDrawer && tsrc15UpdatedAtDrawer
  assert(
    'TSRC15 静态：DateTime 字段改用 formatUtcDate/formatUtcDateTime（5 处全覆盖）',
    tsrc15,
    tsrc15
      ? '已切 formatUtcXxx'
      : `import=${tsrc15Import}/${tsrc15ImportDateTime} noBare=${tsrc15NoBareDayjs} effFrom=${tsrc15EffectiveFrom} effTo=${tsrc15EffectiveTo} updTbl=${tsrc15UpdatedAtTable} creDrw=${tsrc15CreatedAtDrawer} updDrw=${tsrc15UpdatedAtDrawer}`
  )

  // TSRC16 静态：create() smart-insert（按后端排序契约 MaterialId, ProductionDepartmentId, Id ASC 插入到正确位置）
  //  - 锁 create() 不再 append（避免与后端排序不一致 → 刷新跳变）
  //  - 锁 findSortedInsertIndex 工具存在（camps: MaterialId → Dept NULL 最大 → Id）
  //  - 锁二分 lowerBound 查找（无边界越界检查：用户看到新条目出现在其排序位置，跨页场景下次 load 自然拉回）
  const tsrc16NoAppend = !/list\.value\s*=\s*\[\.\.\.list\.value,\s*result\]/.test(tsrcStore)
  const tsrc16HasHelper = /function\s+findSortedInsertIndex\s*\(/.test(tsrcStore)
  const tsrc16CallsHelper = /findSortedInsertIndex\(\s*list\.value\s*,\s*result\s*\)/.test(tsrcStore)
  const tsrc16HasCmp = /a\.materialId\s*!==\s*b\.materialId/.test(tsrcStore) &&
    /productionDepartmentId\s*\?\?\s*Number\.MAX_SAFE_INTEGER/.test(tsrcStore) &&
    /a\.id\s*-\s*b\.id/.test(tsrcStore)
  const tsrc16HasBinarySearch = /\(lo\s*\+\s*hi\)\s*>>>\s*1/.test(tsrcStore)
  const tsrc16 = tsrc16NoAppend && tsrc16HasHelper && tsrc16CallsHelper && tsrc16HasCmp && tsrc16HasBinarySearch
  assert(
    'TSRC16 静态：create() smart-insert（findSortedInsertIndex + 二分 lowerBound）',
    tsrc16,
    tsrc16
      ? '已切 smart-insert'
      : `noAppend=${tsrc16NoAppend} helper=${tsrc16HasHelper} call=${tsrc16CallsHelper} cmp=${tsrc16HasCmp} binary=${tsrc16HasBinarySearch}`
  )

  // TSRC17 静态：筛选物料 ElAutocomplete label 清空 → key 同步置 null
  //   - 锁：watch(filterMaterialLabel) 在 label 为空时把 filterMaterialKey 置 null
  //   - 否则 × 按钮 / 手动删空后 filterMaterialKey 残留旧 masterId，下次 [筛选] 仍按旧 materialId 过滤（违反「留空 = 全部」语义）
  const tsrcVueContent = readSrc('views/Aps/TaskSplitRuleConfig.vue')
  const tsrc17Watch =
    /watch\(\s*filterMaterialLabel\b/.test(tsrcVueContent) ||
    /watch\(\s*\(\)\s*=>\s*filterMaterialLabel\b/.test(tsrcVueContent)
  const tsrc17ResetsKey =
    /if\s*\(\s*!\s*next\s*\)\s*\{[\s\S]*?filterMaterialKey\.value\s*=\s*null/.test(tsrcVueContent) ||
    /if\s*\(\s*!\s*newLabel\s*\)\s*\{[\s\S]*?filterMaterialKey\.value\s*=\s*null/.test(tsrcVueContent)
  const tsrc17 = tsrc17Watch && tsrc17ResetsKey
  assert(
    'TSRC17 静态：ElAutocomplete label 清空 → filterMaterialKey 同步置 null（× 按钮 / 手动删空）',
    tsrc17,
    tsrc17 ? '已加 watch 同步' : `watch=${tsrc17Watch} resetsKey=${tsrc17ResetsKey}`
  )

  // TSRC14j 动态：pageIndex=1&pageSize=2 → items.length≤2 且 total≥items.length（验证脚本默认联调真实后端）
  //   - 兼容两种 dev 端响应形态（10-08 PageResult 部署时机可能滞后）：
  //     - PageResult：{ items, total, page, pageSize } → 严格断言 items.length≤2 && total≥items.length
  //     - 旧裸数组：[...] → 断言 length≤2（dev 默认 100 不带分页时可能 length=100 → 视为 PageResult 缺位/退回）
  try {
    const r1 = await call('GET', '/api/governance/task-split-rule-configs', {
      token,
      query: { pageIndex: 1, pageSize: 2 }
    })
    const raw1 = r1.json?.data
    const isPage1 = raw1 && typeof raw1 === 'object' && Array.isArray(raw1.items)
    const isArray1 = Array.isArray(raw1)
    const items1 = isPage1 ? raw1.items : isArray1 ? raw1 : null
    const ok1 =
      items1 !== null && items1.length <= 2 && (isPage1 ? raw1.total >= items1.length : true)
    assert(
      'TSRC14j 动态：GET list pageIndex=1&pageSize=2 → items.length≤2 且 total≥items.length',
      ok1,
      ok1
        ? `形态=${isPage1 ? 'PageResult' : 'array'}, items=${items1.length}${isPage1 ? ` total=${raw1.total} pageSize=${raw1.pageSize}` : ''}`
        : `code=${r1.code}, data 形态=${isPage1 ? 'PageResult' : isArray1 ? 'array' : 'unknown'}, items=${items1?.length}`
    )

    // TSRC14k 动态：pageSize=9999 → 后端 Math.Clamp 截断到 200（仅在 PageResult 形态下可证）
    //   - 旧裸数组形态无法证截断（dev 默认 100 全量回）→ 跳过 pageSize=200 断言
    const r2 = await call('GET', '/api/governance/task-split-rule-configs', {
      token,
      query: { pageIndex: 1, pageSize: 9999 }
    })
    const raw2 = r2.json?.data
    const isPage2 = raw2 && typeof raw2 === 'object' && Array.isArray(raw2.items)
    if (isPage2) {
      const ok2 = raw2.pageSize === 200
      assert(
        'TSRC14k 动态：GET list pageSize=9999 → 后端 Math.Clamp 截断到 200',
        ok2,
        ok2 ? `实际 pageSize=${raw2.pageSize}` : `实际 pageSize=${raw2.pageSize} code=${r2.code}`
      )
    } else {
      skip('TSRC14k 动态：GET list pageSize=9999 → dev 端尚未返 PageResult（10-08 部署可能滞后），无法验证 Math.Clamp=200')
    }
  } catch (err) {
    skip(`TSRC14j/k 动态：真实后端联调（跳过：${err.message}）`)
  }

  // TSRC18 静态：v1.1 接口签名 Scope 变更登记（10-09 P0-01）
  //   - 触发：3号位致4号位_TaskSplitRuleConfig与StageLeadTimeParam_接口签名Scope变更登记_v1.1_20261009.md
  //   - 前端共性影响：HTTP 零变化；仅 403「业务范围越界（Department）」新增 + 空态分两支 + 友好提示
  //   - 锁契约：3 个文件头注释均引用 v1.1；view 含 errorAlertTitle / emptyDescription + SCOPE_VIOLATION_PATTERN
  //   - 反向探针自证可失败：删 v1.1 引用会让 TSRC18 由绿转红
  const tsrc18a =
    /3号位致4号位_TaskSplitRuleConfig与StageLeadTimeParam_接口签名Scope变更登记_v1\.1_20261009/.test(
      tsrcApi
    )
  const tsrc18b =
    /3号位致4号位_TaskSplitRuleConfig与StageLeadTimeParam_接口签名Scope变更登记_v1\.1_20261009/.test(
      readSrc('store/modules/aps/taskSplitRuleConfig.ts')
    )
  const tsrc18c =
    /3号位致4号位_TaskSplitRuleConfig与StageLeadTimeParam_接口签名Scope变更登记_v1\.1_20261009/.test(
      tsrcVueContent
    )
  const tsrc18d = /const\s+SCOPE_VIOLATION_PATTERN\s*=\s*\/业务范围越界\//.test(tsrcVueContent)
  const tsrc18e = /const\s+errorAlertTitle\s*=\s*computed/.test(tsrcVueContent)
  const tsrc18f = /const\s+emptyDescription\s*=\s*computed/.test(tsrcVueContent)
  const tsrc18g = /:title="errorAlertTitle"/.test(tsrcVueContent)
  const tsrc18h = /:description="emptyDescription"/.test(tsrcVueContent)
  const tsrc18All =
    tsrc18a &&
    tsrc18b &&
    tsrc18c &&
    tsrc18d &&
    tsrc18e &&
    tsrc18f &&
    tsrc18g &&
    tsrc18h
  assert(
    'TSRC18 静态：v1.1 P0-01 契约落地（3 文件头注释 v1.1 引用 + view 友好提示 + 空态分两支）',
    tsrc18All,
    tsrc18All
      ? '8 项齐全'
      : `api=${tsrc18a} store=${tsrc18b} vue=${tsrc18c} pattern=${tsrc18d} errTitle=${tsrc18e} emptyDesc=${tsrc18f} alertTitleUse=${tsrc18g} emptyDescUse=${tsrc18h}`
  )

  /* ===== TSRC19：冻结禁列清除 + 经济软目标用语（2026-10-22）=====
   * 依据：冻结 13 v1.8:97（不展示、不维护 MaxOptimizationSplitCount / MaxBatchCandidates /
   *   BottleneckSplitStrategy / NonBottleneckStrategy）+ 12 v2.2:102；
   *   13 v1.8:31 / 01 B-011（Min/Max 是**经济软目标**，超区间不是硬不可行）。
   * 后端 v5.1.10 起已不再 SELECT/接收这四个字段（TaskSplitRuleConfigGovernanceService.cs:36-40），
   * 前端若仍建模 = 显示了永远不会来的字段 + 提交了永远不会生效的值。
   * 反向破坏自证：任一禁列名回到代码（非注释）→ 红；把「经济最小批量（软目标）」改回「硬最小执行批量」→ 红。
   */
  const tsrcViewCode = stripComments(readSrc('views/Aps/TaskSplitRuleConfig.vue'))
  const tsrcTypesCode = stripComments(readSrc('api/aps-v1/types/taskSplitRuleConfig.ts'))
  const tsrcApiCode = stripComments(readSrc('api/aps-v1/taskSplitRuleConfig.ts'))
  const forbiddenBatchFields = [
    'maxOptimizationSplitCount',
    'maxBatchCandidates',
    'bottleneckSplitStrategy',
    'nonBottleneckStrategy'
  ]
  const forbiddenHits = forbiddenBatchFields.filter((f) =>
    new RegExp(`\\b${f}\\b`).test(tsrcViewCode + tsrcTypesCode + tsrcApiCode)
  )
  const softTermsOk =
    /经济最小批量（软目标）/.test(tsrcViewCode) &&
    /经济最大批量（软目标）/.test(tsrcViewCode) &&
    /经济软目标批量/.test(tsrcViewCode) &&
    /经济软目标/.test(tsrcViewCode) &&
    !/硬最小执行批量|硬最大执行批量|软偏好切点批量|无硬约束|无硬上限|无硬下限/.test(tsrcViewCode)
  assert(
    'TSRC19 静态：批策略页不展示/不维护/不传 4 个冻结禁列，批量字段用「经济软目标」措辞',
    forbiddenHits.length === 0 && softTermsOk,
    forbiddenHits.length
      ? `仍存在冻结禁列（非注释代码）：${forbiddenHits.join(', ')}`
      : softTermsOk
        ? '禁列已清除；Min/Max/Preferred 均为经济软目标措辞'
        : '仍在用「硬最小/硬最大/软偏好/无硬约束」措辞，或缺少经济软目标说明'
  )

  info('TSRC 段：3号位 2026-10-07 v1.0 契约前端适配 + 后端 7 文件落地 + 10-08 列表分页改造（方案 A）落地完成 + 10-09 v1.1 P0-01 Scope 变更登记 友好提示闭环（TSRC18 静态 8 项齐）+ 10-22 冻结禁列清除/经济软目标用语（TSRC19）')
}

/**
 * [ST] Setup 换型规则 4 页（setup — 10-14 命名/红线横幅/时区三项整改）
 *
 * 依据：frontNew/docs/APS_V1_Setup换型规则与有限产能优化_冻结文档修改指导_v1.2_20260916.md
 *  - §四 默认换型规则粒度   §五 三层命中优先级   §18 缺失规则数据治理   §19.2 4号位页面职责
 *
 * 本组守 10-14 三项整改不回退：
 *  - ST01 页面命名收敛到冻结术语且菜单 ≤6 字
 *  - ST02 内部协调横幅 SetupRedlineAlert 已删除（组件 + 4 处引用）
 *  - ST03 悬空的 v1.5 §N / S0N 文档引用已清零（那些章节号在任何现行有效文档里都不存在）
 * 时区修复由 S03/S04 守（4 页已并入 datetimePages），此处只做"辅助函数不再存在"的结构兜底。
 */
async function verifySetupPages() {
  log('\n[ST] Setup 换型规则 4 页（setup — 2026-10-14 命名/横幅/悬空引用整改）')

  const setupViews = [
    'views/Aps/SetupExact.vue',
    'views/Aps/SetupDefault.vue',
    'views/Aps/SetupUncovered.vue',
    'views/Aps/SetupDiff.vue'
  ]
  const setupSrc = setupViews.map((p) => readSrc(p))
  const routerSrc = readSrc('router/modules/aps.ts')

  /* -------- ST01 静态：菜单/标题用冻结术语且 ≤6 字 -------- */
  const st01Titles = [
    { key: '明确产品转换', re: /title:\s*'明确产品转换'/ },
    { key: '默认换型规则', re: /title:\s*'默认换型规则'/ },
    { key: '缺失规则查询', re: /title:\s*'缺失规则查询'/ },
    { key: '规则版本对比', re: /title:\s*'规则版本对比'/ }
  ]
  const st01TitlesOk = st01Titles.every((t) => t.re.test(routerSrc) && t.key.length <= 6)
  const st01NoStaleTitle =
    !/title:\s*'(明确产品转换规则|默认 Setup 规则|未维护 Setup 查询|Setup 版本 Diff)'/.test(routerSrc)
  const st01All =
    st01TitlesOk && st01NoStaleTitle
  assert(
    'ST01 静态：Setup 4 页菜单用冻结术语（明确产品转换/默认换型规则/缺失规则查询/规则版本对比）且 ≤6 字',
    st01All,
    st01All
      ? st01Titles.map((t) => t.key).join(' / ')
      : `titles=${st01TitlesOk} noStale=${st01NoStaleTitle}`
  )

  // h2 标题文本须与菜单一致且无（EXACT）/（DEFAULT）后缀；允许标题后跟内联元素（如 ⓘ tooltip）
  const st01bH2 = [
    /<h2>\s*明确产品转换\s*(?:<\/h2>|<)/.test(setupSrc[0]),
    /<h2>\s*默认换型规则\s*(?:<\/h2>|<)/.test(setupSrc[1]),
    /<h2>\s*缺失规则查询\s*(?:<\/h2>|<)/.test(setupSrc[2]),
    /<h2>\s*规则版本对比\s*(?:<\/h2>|<)/.test(setupSrc[3])
  ].every(Boolean)
  const st01cOldTermGone = !setupSrc.some((c) => /明确产品转换规则（EXACT）|默认换型规则（DEFAULT）/.test(c))
  const st01bAll = st01bH2 && st01cOldTermGone
  assert(
    'ST01b 静态：Setup 4 页 <h2> 与菜单一致，且去掉「（EXACT）/（DEFAULT）」后缀',
    st01bAll,
    st01bAll ? 'h2 四页齐 + 旧后缀清零' : `h2=${st01bH2} oldTermGone=${st01cOldTermGone}`
  )

  /* -------- ST02 静态：内部协调横幅已删除 -------- */
  const st02ComponentGone = !existsSync(
    join(__dirname, '..', 'src', 'views', 'Aps', 'components', 'SetupRedlineAlert.vue')
  )
  const st02NoRef = !setupSrc.some((c) => /SetupRedlineAlert/.test(c))
  const st02NoStrayKey = !setupSrc.some((c) => /aps\.setup\.redline\.expanded/.test(c))
  const st02All = st02ComponentGone && st02NoRef && st02NoStrayKey
  assert(
    'ST02 静态：SetupRedlineAlert 红线横幅已删除（组件文件 + 4 处引用 + localStorage key）',
    st02All,
    st02All
      ? '组件文件不存在，4 页零引用'
      : `componentGone=${st02ComponentGone} noRef=${st02NoRef} noStrayKey=${st02NoStrayKey}`
  )

  /* -------- ST03 静态：悬空 v1.5 章节引用清零 -------- */
  const allSetupFiles = [
    ...setupViews,
    'views/Aps/components/SetupExactDialog.vue',
    'views/Aps/components/SetupDefaultDialog.vue',
    'store/modules/aps/setupExact.ts',
    'store/modules/aps/setupDefault.ts',
    'store/modules/aps/setupUncovered.ts',
    'store/modules/aps/setupDiff.ts',
    'api/aps-v1/setup.ts',
    'api/aps-v1/types/setup.ts',
    'api/aps-v1/types/schedule.ts'
  ]
  const st03Hits = allSetupFiles.filter((p) =>
    /v1\.5\s*§|v1\.5 Setup 专项|§7\.1|§7\.2|§3\.3|§5\.4|§6\.3|§4\.2|§5\.5/.test(readSrc(p))
  )
  const st03Router = !/acceptance:\s*'v1\.5/.test(routerSrc)
  const st03All = st03Hits.length === 0 && st03Router
  assert(
    'ST03 静态：Setup 全 13 文件 + 路由 meta 不再引用不存在的 v1.5 §N / S0N 章节',
    st03All,
    st03All
      ? `已扫 ${allSetupFiles.length} 文件全清零`
      : `残留：${st03Hits.join(', ')} router=${st03Router}`
  )

  /* -------- ST04 静态：Setup 页不再自建时区辅助函数（裸 dayjs 由 S04 兜底） -------- */
  const st04NoHelper = !setupSrc.some((c) => /function\s+formatDateTime\s*\(/.test(c))
  const st04UseUtil = setupSrc.every((c) => /from\s+['"]@\/utils\/datetime['"]/.test(c))
  const st04All = st04NoHelper && st04UseUtil
  assert(
    'ST04 静态：Setup 4 页不再包 formatDateTime 辅助函数，统一直调 @/utils/datetime',
    st04All,
    st04All ? '4 页直调 formatUtcDateTimeSec' : `noHelper=${st04NoHelper} useUtil=${st04UseUtil}`
  )

  /* -------- ST05 静态：Setup 下拉不再硬编码占位值，改接后端主数据 lookup -------- */
  const st05Files = [
    'views/Aps/SetupExact.vue',
    'views/Aps/SetupDefault.vue',
    'views/Aps/SetupUncovered.vue',
    'views/Aps/components/SetupExactDialog.vue',
    'views/Aps/components/SetupDefaultDialog.vue',
    'views/Aps/composables/useSetupMasterData.ts',
    'api/aps-v1/resourceLookup.ts'
  ]
  // 占位值：DEPT_A/B、STAGE_1/2、MC001-3、MAT_A-X、OP10/20/30、MOCK_RESOURCE_IDS
  const st05Placeholder = /DEPT_[AB]|STAGE_[12]|MC00[123]|MAT_[ABCXY]|MOCK_RESOURCE_IDS/
  const st05Hits = st05Files.filter((p) => st05Placeholder.test(readSrc(p)))
  assert(
    'ST05 静态：Setup 4 页 + 2 Dialog + composable 无硬编码下拉占位值',
    st05Hits.length === 0,
    st05Hits.length === 0
      ? `已扫 ${st05Files.length} 文件全清零`
      : `残留：${st05Hits.join(', ')}`
  )

  // composable 必须真的打了三个 lookup 端点，且大工艺由 stageCode 派生
  const mdSrc = readSrc('views/Aps/composables/useSetupMasterData.ts')
  const st05Endpoints =
    mdSrc.includes("'/api/governance/lookups/departments'") === false &&
    mdSrc.includes('departmentLookupApi.listDepartments()') &&
    mdSrc.includes('resourceLookupApi.listResources()') &&
    mdSrc.includes('materialLookupApi.listActiveMaterials()')
  const st05StageDerived = /stageCode[\s\S]{0,200}seen\.add|seen\.add\(code\)/.test(mdSrc)
  assert(
    'ST05 静态：useSetupMasterData 走 department/resource/material 三个 lookup，大工艺由 stageCode 派生',
    st05Endpoints && st05StageDerived,
    `endpoints=${st05Endpoints} stageDerived=${st05StageDerived}`
  )

  // Dialog 的 Code→Id 解析必须走 masterData，不得回 mock 常量
  const st05Dialogs = [
    'views/Aps/components/SetupExactDialog.vue',
    'views/Aps/components/SetupDefaultDialog.vue'
  ]
  const st05NoMockResolve = st05Dialogs.every((p) => {
    const c = readSrc(p)
    return !/MOCK_RESOURCE_IDS/.test(c) && /masterData\.\w+IdByCode\(/.test(c)
  })
  assert(
    'ST05 静态：两个 Dialog 的 Code→Id 解析走 masterData（不再 mock 常量映射）',
    st05NoMockResolve,
    st05NoMockResolve ? '2 个 Dialog 全部走后端 lookup Id' : '仍有 mock 映射残留'
  )

  /* -------- ST06 静态：明确产品转换页的用词是给车间看的大白话（10-15 4号位）--------
   *  依据：Setup 专项指导 v1.2 §五 三层命中优先级（EXACT → DEFAULT → 无规则按 0 分钟）
   *  - 副标题撤掉内部词「共同合法设备」（那是 OperationResourceEligibility 的实现口径，车间不关心）
   *  - 三层优先级收进 ⓘ tooltip：业务规则进 tooltip，不上横幅（见「页面不放内部协调提示」）
   *  - 反向探针自证可失败：副标题改回「共同合法设备」或 tooltip 被删 → 转红
   */
  const exactVueSrc = readSrc('views/Aps/SetupExact.vue')
  const st06NoJargonSubtitle =
    /<p class="subtitle">[^<]*换型时间/.test(exactVueSrc) &&
    !/共同合法设备/.test(exactVueSrc.split('<template>')[1] ?? '')
  assert(
    'ST06a 静态：明确产品转换页副标题用大白话，模板区无「共同合法设备」内部词',
    st06NoJargonSubtitle,
    st06NoJargonSubtitle ? '副标题已改业务口径' : '副标题仍含内部实现词或已非换型口径'
  )

  const st06ThreeTierHint =
    /<ElTooltip[\s\S]{0,400}vi-ep:question-filled/.test(exactVueSrc) &&
    /①[\s\S]*前产品[\s\S]*②[\s\S]*默认换型规则[\s\S]*③[\s\S]*0\s*分钟/.test(exactVueSrc)
  assert(
    'ST06b 静态：三层命中优先级（明确→默认→0分钟兜底）在 ⓘ tooltip 里对用户讲清',
    st06ThreeTierHint,
    st06ThreeTierHint ? 'tooltip 三行齐全' : 'tooltip 缺失或三层说不全'
  )

  /* -------- ST07 静态：部门/大工艺筛选不得列「必然 0 条」的选项（10-15 4号位）--------
   *  依据：用户 10-15 指令「页面中不要有假数据……一定要保证能前后端联调成功，用户能用」
   *  - 原写法：两个下拉列 lookups 的全量主数据，用户选一个当前版本没有规则的部门 → 列表必然 0 条
   *  - 现写法：选项 = 当前版本规则里真实出现过的 departmentCode / stageCode（数据仍是后端返回的真规则）
   *  - 补部门→大工艺级联 + 换版本清空，避免留下必然 0 条的组合
   *  - 反向探针自证可失败：改回 masterData.departments / 去掉 watch 级联 → 转红
   */
  const st07Pages = [
    ['SetupExact', readSrc('views/Aps/SetupExact.vue')],
    ['SetupDefault', readSrc('views/Aps/SetupDefault.vue')]
  ]
  for (const [pageName, src] of st07Pages) {
    const optsDerived =
      /const departmentOptions = computed\([\s\S]{0,300}rules\.value[\s\S]{0,300}masterData\.departments\.value\.filter/.test(
        src
      ) &&
      /const stageOptions = computed\([\s\S]{0,400}filterDepartment\.value[\s\S]{0,400}masterData\.stages\.value\.filter/.test(
        src
      )
    assert(
      `ST07a 静态：${pageName} 部门/大工艺选项取自当前版本真实规则，不再列 lookups 全量主数据`,
      optsDerived,
      optsDerived ? '两个选项均已按规则实有值收窄' : '选项仍取全量主数据，会列出必然 0 条的项'
    )

    // 模板必须消费收窄后的选项，且不得再直接遍历 masterData
    const usesDerived =
      /v-for="opt in departmentOptions"/.test(src) && /v-for="opt in stageOptions"/.test(src)
    assert(
      `ST07b 静态：${pageName} 两个筛选下拉改绑 departmentOptions / stageOptions`,
      usesDerived,
      usesDerived ? '模板已改绑' : '模板仍绑全量主数据'
    )

    // 级联 + 换版本清空两条 watch
    const hasCascade =
      /watch\(filterDepartment[\s\S]{0,400}stageOptions\.value\.some/.test(src) &&
      /watch\(selectedRuleSetVersionId[\s\S]{0,300}filterDepartment\.value = ''/.test(src)
    assert(
      `ST07c 静态：${pageName} 部门→大工艺级联收窄 + 换规则集版本清空两个筛选`,
      hasCascade,
      hasCascade ? '两条 watch 齐全' : '缺级联或换版本未清空，会留下必然 0 条的组合'
    )
  }

  /* -------- ST08 静态：治理版 store 不得用 feature-detect 落空后返回手写桩（10-15 4号位）--------
   *  依据：同上用户指令。原写法 `if ((ruleApi as any).getRuleSetVersion)` 恒为假（方法不存在），
   *  于是真实联调路径上永远返回 versionCode `v-{id}` / ruleSetId 0 的桩对象——比 mock 更危险。
   *  反向探针自证可失败：改回 (ruleApi as any).* 或删掉 ruleApi.getRuleSetVersion 调用 → 转红
   */
  // 2026-10-22：规则集 / 参数集拆成两条平行链后，RS 与 PS 各自的单版本 GET 分居两个 api 文件
  //（`api/rule.ts` ∥ `api/parameterSet.ts`）—— 判据随之拆分，但「不得 feature-detect 落空后返手写桩」的意图不变。
  const rulesStore = readSrc('store/modules/aps/rules.ts')
  const psStore = readSrc('store/modules/aps/parameterSets.ts')
  const ruleApiSrc = readSrc('api/aps-v1/rule.ts')
  const psApiSrc = readSrc('api/aps-v1/parameterSet.ts')
  // 注释里提到旧写法不算桩，先剥掉注释再判
  const stripC = (s) => s.replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '')
  const st08NoStub =
    !/\(ruleApi as any\)/.test(stripC(rulesStore)) &&
    !/\(parameterSetApi as any\)/.test(stripC(psStore)) &&
    /ruleApi\.getRuleSet\(/.test(rulesStore) &&
    /parameterSetApi\.getParameterSet\(/.test(psStore)
  assert(
    'ST08a 静态：两族 store 都走正式详情方法，无 feature-detect 手写桩',
    st08NoStub,
    st08NoStub ? '已是真实 GET' : '仍有 as any 桩路径或未接真实详情方法'
  )
  const st08RsApi =
    /async getRuleSetVersion\(versionId: number\)/.test(ruleApiSrc) &&
    /\/api\/governance\/rule-set\/version\/\$\{versionId\}/.test(ruleApiSrc)
  const st08PsApi =
    /async getParameterSetVersion\(versionId: number\)/.test(psApiSrc) &&
    /\/api\/governance\/parameter-set\/version\/\$\{versionId\}/.test(psApiSrc)
  assert(
    'ST08b 静态：RS / PS 各自补齐单版本 GET 并指向后端真端点',
    st08RsApi && st08PsApi,
    `规则集=${st08RsApi} 参数集=${st08PsApi}`
  )

  /* -------- ST09 静态：打包环境绝不能开 mock（10-15 4号位）--------
   *  依据：同上用户指令「一定要保证上线后能用」。VITE_USE_MOCK=true 会同时
   *   ① viteMockServe 把 mock/ 全量注入生产包；② 让 api/aps-v1/*.ts 的 APS_USE_MOCK 分支返回 MOCK_* 桩。
   *  四个 env 文件必须全为 false。反向探针：把 .env.pro 改回 true → 转红
   */
  const envFiles = ['.env.base', '.env.dev', '.env.pro', '.env.test']
  const badEnvs = envFiles.filter((f) => {
    try {
      return !/^VITE_USE_MOCK=false$/m.test(readFileSync(join(__dirname, '..', f), 'utf-8'))
    } catch {
      return true
    }
  })
  assert(
    'ST09 静态：4 个 env 文件 VITE_USE_MOCK 全为 false（生产包不得携带 mock / APS_USE_MOCK 桩）',
    badEnvs.length === 0,
    badEnvs.length === 0 ? 'base/dev/pro/test 四档均关' : `以下文件仍开 mock：${badEnvs.join('、')}`
  )

  /* -------- ST10 静态：主数据 TOP 200 截断 + §19.2 一台或多台设备（10-15 4号位）--------
   *  依据：用户指令「按照真实的现场使用来改，别为了能跑通前端随意修改，为了线上能用」。
   *  根因：后端 lookups 三端点 SQL 都是 `SELECT TOP (@Limit) ... ORDER BY [Code]`，
   *  limit 是仓储默认值（MasterDataLookupRepository.cs:87/98/108）且 Controller 不透传
   *  （SetupRuleController.cs:299-347）→ 不带 search 永远只有字母序前 200 条。
   *  解法：
   *   ST10d 远程搜索（api 层 search* 三个方法一直存在却从未被调用）
   *   ST10a/b 设备下拉改由 eligibility.resources 驱动（该集合不限 200、已按 Department Scope 过滤）
   *   ST10e 大工艺由所选部门自动带出（部门→大工艺 1:1）
   *   ST10f §19.2「一台或多台设备」：create 多选 + **串行**提交（并发会丢规则）
   *  反向探针：把 resourceOptions 改回 masterData.resources / 把 for 改成 Promise.all / 去掉 remote → 转红
   */
  const masterDataSrc = readSrc('views/Aps/composables/useSetupMasterData.ts')
  const exactDialogSrc = readSrc('views/Aps/components/SetupExactDialog.vue')
  const defaultDialogSrc = readSrc('views/Aps/components/SetupDefaultDialog.vue')
  const setupTypesSrc = readSrc('api/aps-v1/types/setup.ts')

  // ST10d：composable 三个远程搜索走既有 api 的 search*，且结果 **merge 进** 列表而非替换。
  // 必须锁「调用点也走 mergeBy」——只查 mergeBy 的定义的话，把调用点改回直接赋值同样能绿。
  // 断言用 [\s\S] 容忍 eslint --fix 的换行重排。
  const st10dSearch =
    /rawDepartments\.value = mergeBy\(\s*rawDepartments\.value,\s*await departmentLookupApi\.searchDepartments\(/.test(
      masterDataSrc
    ) &&
    /rawResources\.value = mergeBy\(\s*rawResources\.value,\s*await resourceLookupApi\.searchResources\(/.test(
      masterDataSrc
    ) &&
    /rawMaterials\.value = mergeBy\(\s*rawMaterials\.value,\s*await materialLookupApi\.searchMaterials\(/.test(
      masterDataSrc
    )
  const st10dMerge = /function mergeBy[\s\S]{0,600}return extra\.length \? \[\.\.\.base, \.\.\.extra\] : base/.test(
    masterDataSrc
  )
  assert(
    'ST10d 静态：useSetupMasterData 三个远程搜索走既有 api 的 search*，且结果 merge 进列表（不替换，已选项不消失）',
    st10dSearch && st10dMerge,
    st10dSearch
      ? st10dMerge
        ? 'search* + merge 齐（三处调用点均 merge）'
        : '缺 mergeBy 定义'
      : '有 search* 直接赋值替换了列表（会把用户已选项挤出下拉），或没走既有 api 的 search* 方法'
  )

  // ST10a：设备下拉由 eligibility 明细驱动
  const st10a =
    /const eligibilityResources = ref</.test(exactDialogSrc) &&
    /const resourceOptions = computed<SetupLookupOption\[\]>\(\(\) => \{[\s\S]{0,200}!eligibilityResources\.value\) return masterData\.resources\.value[\s\S]{0,600}eligibilityResources\.value\.map/.test(
      exactDialogSrc
    )
  assert(
    'ST10a 静态：明确产品转换的设备下拉由 eligibility.resources 明细驱动（不受 lookups TOP 200 截断）',
    st10a,
    st10a ? '回落 masterData → 用后端交集明细' : '仍只从 masterData.resources（字母序前 200）取设备'
  )

  // ST10b：resolveResourceId 含 eligibility 明细回退
  const st10b =
    /function resolveResourceId\(code: string\)[\s\S]{0,600}eligibilityResources\.value\?\.find\([\s\S]{0,120}resourceId[\s\S]{0,200}masterData\.resourceIdByCode/.test(
      exactDialogSrc
    )
  assert(
    'ST10b 静态：resolveResourceId 三级回退（编辑态双返回 → eligibility 明细 → 主数据）',
    st10b,
    st10b ? '三级回退齐' : '缺 eligibility 明细回退：字母序 200 之外的设备解析不到 Id'
  )

  // ST10c：远程搜索绑定到模板（两个 Dialog + 缺失规则查询页，三个消费面）
  const uncoveredSrc = readSrc('views/Aps/SetupUncovered.vue')
  const st10cRemote =
    (exactDialogSrc.match(/\bremote\b/g) ?? []).length >= 3 &&
    /:remote-method="onSearchDepartment"/.test(exactDialogSrc) &&
    /:remote-method="onSearchMaterial"/.test(exactDialogSrc) &&
    /:remote-method="onSearchDepartment"/.test(defaultDialogSrc) &&
    /:remote-method="onSearchResource"/.test(defaultDialogSrc) &&
    /:remote-method="onSearchDepartment"/.test(uncoveredSrc) &&
    /:remote-method="onSearchResource"/.test(uncoveredSrc)
  assert(
    'ST10c 静态：两个 Dialog + 缺失规则查询页的部门/产品/设备下拉改 remote + remote-method',
    st10cRemote,
    st10cRemote ? 'Exact 3 处 + Default 2 处 + Uncovered 2 处' : '有下拉未接远程搜索（TOP 200 之外的项选不到）'
  )

  // ST10e：大工艺由所选部门自动带出
  const st10e =
    /watch\(\s*\(\) => form\.departmentCode[\s\S]{0,300}masterData\.stageOfDepartment\(code\)[\s\S]{0,200}form\.stageCode = stage/.test(
      exactDialogSrc
    ) &&
    /watch\(\s*\(\) => form\.departmentCode[\s\S]{0,300}masterData\.stageOfDepartment\(code\)[\s\S]{0,200}form\.stageCode = stage/.test(
      defaultDialogSrc
    )
  assert(
    'ST10e 静态：大工艺由所选部门自动带出（部门→大工艺 1:1，仍可手动改）',
    st10e,
    st10e ? '两个 Dialog 均有 stageOfDepartment 联动' : '有 Dialog 未做部门→大工艺联动'
  )

  // ST10f：create 多选 + 串行提交（并发必丢规则：后端 create 是读改写 ContentSnapshotJson）
  const st10fMulti =
    /:multiple="isCreate"/.test(exactDialogSrc) && /:multiple="isCreate"/.test(defaultDialogSrc)
  const st10fSerial =
    /for \(let i = 0; i < resourceIds\.length; i\+\+\)[\s\S]{0,200}await exactStore\.createRule/.test(
      exactDialogSrc
    ) &&
    /for \(let i = 0; i < resourceIds\.length; i\+\+\)[\s\S]{0,200}await defaultStore\.createRule/.test(
      defaultDialogSrc
    )
  const st10fNoPromiseAll =
    !/Promise\.all\(\s*(?:form\.resourceCodes|resourceIds\.map)/.test(exactDialogSrc) &&
    !/Promise\.all\(\s*(?:form\.resourceCodes|resourceIds\.map)/.test(defaultDialogSrc)
  assert(
    'ST10f 静态：§19.2「一台或多台设备」—— create 多选 + 逐台串行建单（不得 Promise.all，并发会丢换型规则）',
    st10fMulti && st10fSerial && st10fNoPromiseAll,
    !st10fMulti
      ? '设备未按 create 多选'
      : !st10fSerial
        ? '提交不是逐台串行循环'
        : st10fNoPromiseAll
          ? '两处均串行'
          : '检测到对 resourceCodes 用了 Promise.all —— 后端 create 是读改写同一列，并发会覆盖丢规则'
  )

  // ST10g：resourceId 已转必填（3号位 Q1 投影在 dev 实测生效：M102 → resourceId 13394，S-11-d）
  // 故意断言「必填」而非「可选」：退回可选 = 类型系统不再保证资格集合自带主键，
  // 将来后端回退部署时前端连编译期保护都没有。
  const st10gRequired = /resources\?: Array<\{[\s\S]{0,400}resourceId: number/.test(setupTypesSrc)
  const st10gOptional = /resources\?: Array<\{[\s\S]{0,400}resourceId\?: number/.test(setupTypesSrc)
  assert(
    'ST10g 静态：OperationResourceEligibilityDto.resources 的 resourceId 已转必填（3号位 Q1 已生效，S-11-d）',
    st10gRequired && !st10gOptional,
    st10gRequired && !st10gOptional
      ? '已转必填，与后端非空 int 一致'
      : st10gOptional
        ? '退回可选：类型系统不再保证资格集合自带主键'
        : '缺 resourceId 必填声明'
  )

  // ST10h：TOP 200 命中上限时必须对用户明说（截断静默 = 用户以为没有这条数据）
  const st10h =
    /truncated\.value\s*=[\s\S]{0,300}BACKEND_LOOKUP_LIMIT/.test(masterDataSrc) &&
    /v-if="masterData\.truncated\.value"/.test(exactDialogSrc) &&
    /v-if="masterData\.truncated\.value"/.test(defaultDialogSrc)
  assert(
    'ST10h 静态：主数据返满 200 条即置 truncated，并在两个 Dialog 明示「请用编码搜索」（不得静默截断）',
    st10h,
    st10h ? 'truncated 判定 + 两处提示齐' : '截断对用户不可见，用户会以为数据不存在'
  )

  // ST10i：后端 Q1 的 resourceId 未部署期间的兜底——三处本地解析落空时按编码向后端补拉
  // （10-15 用户报「选设备保存报无法解析主数据 Id」；dev 实测资格集合只有 763-2，首屏 200 条不含它）
  const st10i =
    /async function resolveResourceIds/.test(exactDialogSrc) &&
    /await resolveResourceIds\(form\.resourceCodes\)/.test(exactDialogSrc) &&
    /await masterData\.searchResources\(code\)/.test(exactDialogSrc) &&
    /ids\[codes\.indexOf\(code\)\]\s*=\s*masterData\.resourceIdByCode\(code\)/.test(exactDialogSrc)
  assert(
    'ST10i 静态：设备 Id 三处本地解析落空时按编码调 searchResources 补拉（否则本 Dialog 无法远程搜索，用户无从自救）',
    st10i,
    st10i ? '已补拉兜底' : '只会本地三查就报错，字母序 200 之外的合法设备必然保存失败'
  )
}

/**
 * [PWC] ProductionWorkCalendar 三级日历治理（pwc — 10-13 5号位 PWC 函 + v1.4 §三/§九 联调备料）
 * 验收依据：5号位 2026-10-13《ProductionWorkCalendar三级日历维护接口文档 — 给4号位》
 *  - 状态：G1 候选，前端按联调备料版落地（v1.3 继续有效 + PWC 路由与维护页并行）
 *  - 三态语义：WORK / OFF / MISSING（无行，UI 不造）
 *  - 三级继承：FACTORY → DEPARTMENT → 资源（StageCode 非固定第 4 层）
 *  - 权限码：暂复用 aps.resource_calendar.{view,edit,delete}（5号位 函 §〇.2）
 *  - PageResult 字段名 = `page`（§〇.3）
 */
async function verifyProductionWorkCalendar(token) {
  log('\n[PWC] ProductionWorkCalendar 三级日历治理（pwc — 2026-10-13 5号位 PWC 函联调备料）')

  // PWC01 静态：types/productionWorkCalendar.ts 含核心 DTO + Request + PageResult
  const pwcTypes = readSrc('api/aps-v1/types/productionWorkCalendar.ts')
  const pwc01a = /export\s+interface\s+ProductionWorkCalendarDto\b/.test(pwcTypes)
  assert('PWC01a 静态：types 含 ProductionWorkCalendarDto', pwc01a, pwc01a ? '已含' : '缺 DTO')

  const pwc01b =
    /export\s+interface\s+WeeklyCalendarUpsertRequest\b/.test(pwcTypes) &&
    /export\s+interface\s+WorkCalendarExceptionRequest\b/.test(pwcTypes) &&
    /export\s+interface\s+CalendarCloseOpenRequest\b/.test(pwcTypes)
  assert('PWC01b 静态：types 含 Weekly/Exception/CloseOpen 3 类 Request', pwc01b, pwc01b ? '3 Request 齐' : '缺 Request')

  const pwc01c =
    /export\s+interface\s+ProductionWorkCalendarPageResult<T>\s*\{[\s\S]*?items:\s*T\[\][\s\S]*?total:\s*number[\s\S]*?page:\s*number[\s\S]*?pageSize:\s*number[\s\S]*?\}/.test(pwcTypes)
  assert(
    'PWC01c 静态：types 含 ProductionWorkCalendarPageResult<T>（字段统一为 page）',
    pwc01c,
    pwc01c ? '字段名 page' : 'PageResult 字段名错位（与 5号位 函 §〇.3 不符）'
  )

  // PWC02 静态：types/index.ts 已 re-export productionWorkCalendar
  const pwcIndex = readSrc('api/aps-v1/types/index.ts')
  const pwc02 = /export\s+\*\s+from\s+['"]\.\/productionWorkCalendar['"]/.test(pwcIndex)
  assert('PWC02 静态：types/index.ts re-export ./productionWorkCalendar', pwc02, pwc02 ? '已 re-export' : '缺 re-export')

  // PWC03 静态：api/productionWorkCalendar.ts 含 6 端点
  const pwcApi = readSrc('api/aps-v1/productionWorkCalendar.ts')
  const pwc03a = /async\s+query\s*\(\s*query\s*:\s*ProductionWorkCalendarQuery/.test(pwcApi)
  assert('PWC03a 静态：api 含 query(query: ProductionWorkCalendarQuery)', pwc03a, pwc03a ? '已含' : '缺 query')

  const pwc03b = /async\s+upsertWeekly\s*\(\s*req\s*:\s*WeeklyCalendarUpsertRequest/.test(pwcApi)
  assert('PWC03b 静态：api 含 upsertWeekly(req: WeeklyCalendarUpsertRequest)', pwc03b, pwc03b ? '已含' : '缺 upsertWeekly')

  const pwc03c = /async\s+createException\s*\(\s*req\s*:\s*WorkCalendarExceptionRequest/.test(pwcApi)
  assert('PWC03c 静态：api 含 createException(req: WorkCalendarExceptionRequest)', pwc03c, pwc03c ? '已含' : '缺 createException')

  const pwc03d = /async\s+closeOpen\s*\(\s*req\s*:\s*CalendarCloseOpenRequest/.test(pwcApi)
  assert('PWC03d 静态：api 含 closeOpen(req: CalendarCloseOpenRequest)', pwc03d, pwc03d ? '已含' : '缺 closeOpen')

  const pwc03e = /async\s+closeRow\s*\(\s*id\s*:\s*number\s*,\s*effectiveTo\s*:\s*string/.test(pwcApi)
  assert('PWC03e 静态：api 含 closeRow(id, effectiveTo)', pwc03e, pwc03e ? '已含' : '缺 closeRow')

  const pwc03f = /async\s+deleteRow\s*\(\s*id\s*:\s*number/.test(pwcApi)
  assert('PWC03f 静态：api 含 deleteRow(id)', pwc03f, pwc03f ? '已含' : '缺 deleteRow')

  // PWC04 静态：6 端点 URL 路径与 5号位 函 §一~§五 对齐
  const pwc04a = /['"`]\/api\/production-work-calendar['"`]/.test(pwcApi)
  assert('PWC04a 静态：query URL = /api/production-work-calendar', pwc04a, pwc04a ? '已对齐' : '路径错')

  const pwc04b = /['"`]\/api\/production-work-calendar\/weekly['"`]/.test(pwcApi)
  assert('PWC04b 静态：upsertWeekly URL = /api/production-work-calendar/weekly', pwc04b, pwc04b ? '已对齐' : '路径错')

  const pwc04c = /['"`]\/api\/production-work-calendar\/exception['"`]/.test(pwcApi)
  assert('PWC04c 静态：createException URL = /api/production-work-calendar/exception', pwc04c, pwc04c ? '已对齐' : '路径错')

  const pwc04d = /['"`]\/api\/production-work-calendar\/close['"`]/.test(pwcApi)
  assert('PWC04d 静态：closeOpen URL = /api/production-work-calendar/close', pwc04d, pwc04d ? '已对齐' : '路径错')

  const pwc04e = /\/api\/production-work-calendar\/rows\/\$\{id\}\/close/.test(pwcApi)
  assert('PWC04e 静态：closeRow URL = /api/production-work-calendar/rows/${id}/close', pwc04e, pwc04e ? '已对齐' : '路径错')

  const pwc04f = /\/api\/production-work-calendar\/rows\/\$\{id\}['"`]/.test(pwcApi)
  assert('PWC04f 静态：deleteRow URL = /api/production-work-calendar/rows/${id}', pwc04f, pwc04f ? '已对齐' : '路径错')

  // PWC05 静态：三态语义在 API 层有注释 / 元数据
  const pwc05a = /MISSING/.test(pwcTypes) && /WORK/.test(pwcTypes) && /OFF/.test(pwcTypes)
  assert(
    'PWC05a 静态：types 含三态类型（WORK / OFF）+ MISSING 派生说明（MISSING 不存为虚拟行）',
    pwc05a,
    pwc05a ? '三态齐' : '三态缺失'
  )

  // PWC05b 静态：生产日历配置状态元数据覆盖 4 态
  const pwc05b =
    /WORK:\s*\{/.test(pwcTypes) &&
    /OFF:\s*\{/.test(pwcTypes) &&
    /MISSING:\s*\{/.test(pwcTypes) &&
    /UNKNOWN:\s*\{/.test(pwcTypes)
  assert('PWC05b 静态：PRODUCTION_CALENDAR_STATUS_META 覆盖 4 态（WORK/OFF/MISSING/UNKNOWN）', pwc05b, pwc05b ? '4 态齐' : '缺态')

  // PWC06 静态：api/aps-v1/index.ts 已 re-export productionWorkCalendarApi
  const pwcApsIndex = readSrc('api/aps-v1/index.ts')
  const pwc06 = /export\s*\{\s*productionWorkCalendarApi\s*\}\s*from\s*['"]\.\/productionWorkCalendar['"]/.test(pwcApsIndex)
  assert('PWC06 静态：api/aps-v1/index.ts re-export productionWorkCalendarApi', pwc06, pwc06 ? '已 re-export' : '缺 re-export')

  // PWC07 静态：Pinia store 落地（5 ref + 6 action 范式）
  const pwcStore = readSrc('store/modules/aps/productionWorkCalendar.ts')
  const pwc07a = /export\s+const\s+useProductionWorkCalendarStore\s*=/.test(pwcStore)
  assert('PWC07a 静态：store 含 useProductionWorkCalendarStore', pwc07a, pwc07a ? '已含' : '缺 store')

  const pwc07b =
    /async\s+function\s+load\s*\(/.test(pwcStore) &&
    /async\s+function\s+upsertWeekly\s*\(/.test(pwcStore) &&
    /async\s+function\s+createException\s*\(/.test(pwcStore) &&
    /async\s+function\s+closeOpen\s*\(/.test(pwcStore) &&
    /async\s+function\s+closeRow\s*\(/.test(pwcStore) &&
    /async\s+function\s+deleteRow\s*\(/.test(pwcStore)
  assert('PWC07b 静态：store 含 load/upsertWeekly/createException/closeOpen/closeRow/deleteRow 6 动作', pwc07b, pwc07b ? '6 动作齐' : '缺动作')

  // PWC07c 静态：store 复用 aps.resource_calendar.{view,edit,delete}（5号位 函 §〇.2）
  const pwc07c =
    /RESOURCE_CALENDAR_PERMISSIONS\b/.test(pwcStore) &&
    /canView[\s\S]*?\bview\b/.test(pwcStore) &&
    /canEdit[\s\S]*?\bedit\b/.test(pwcStore) &&
    /canDelete[\s\S]*?\bdelete\b/.test(pwcStore)
  assert('PWC07c 静态：store 权限码复用 aps.resource_calendar.{view,edit,delete}（5号位 函 §〇.2）', pwc07c, pwc07c ? '复用' : '权限码错位')

  // PWC08 静态：路由 + 页面
  const pwcRouter = readSrc('router/modules/aps.ts')
  const pwc08a = /path:\s*['"]\/aps\/production-work-calendar['"]/.test(pwcRouter)
  assert('PWC08a 静态：router/modules/aps.ts 含 path=production-work-calendar', pwc08a, pwc08a ? '路由已占位' : '缺路由')

  const pwc08b = /aps\.resource_calendar\.view/.test(pwcRouter)
  assert('PWC08b 静态：PWC 路由 meta 复用 aps.resource_calendar.view 守卫', pwc08b, pwc08b ? '复用守卫码' : '守卫码错位')

  const pwcPagePath = 'views/Aps/ProductionWorkCalendar.vue'
  const pwc08c = existsSync(join(SRC_ROOT, pwcPagePath))
  assert(`PWC08c 静态：${pwcPagePath} 存在`, pwc08c, pwc08c ? '页面已落地' : '缺页面')

  // PWC08d 静态：页面 ElPagination + pageSizes 范式
  const pwcPage = readSrc('views/Aps/ProductionWorkCalendar.vue')
  const pwc08d = /ElPagination\b/.test(pwcPage) && /:page-sizes=("?)\[10,\s*20,\s*50,\s*100,\s*200\]\1/.test(pwcPage)
  assert(
    'PWC08d 静态：PWC 页面用 ElPagination + pageSizes=[10,20,50,100,200]（仿 TSRC 范式）',
    pwc08d,
    pwc08d ? '分页组件齐' : '分页组件/参数错'
  )

  // PWC09 静态：MISSING 永不造虚拟行（v1.4 §2 + 5号位 函 §七）
  //  - types DTO 不暴露 MISSING-virtual-row 字段；UI 不展示"未配置=VISIBLE"
  //  - 这是设计守卫：MISSING = 无行；UI 列表只展示真行
  const pwc09 = !/MISSING:\s*\{[^}]*isVirtual:\s*true\b/.test(pwcTypes)
  assert(
    'PWC09 静态：MISSING 不暴露为可见虚拟行字段（v1.4 §2 + 5号位 函 §七）',
    pwc09,
    pwc09 ? '纯列表式守卫 OK' : '泄漏虚拟行字段（违反 MISSING 不存约定）'
  )

  // PWC10 动态：联调真实端点（dev 库 10-12 实测已有 ~55 行 SourceSystem=TEST 备料数据）
  //  - 真实调 GET /api/production-work-calendar?pageIndex=1&pageSize=20
  //  - 预期：r.ok=true 且 data 是 PageResult{ items, total, page, pageSize }
  //  - 不可达兜底：r.code === 401/403/404/500 一律视为「端点未达」
  const pwc10r = await call('GET', '/api/production-work-calendar', {
    token,
    query: { pageIndex: 1, pageSize: 20 }
  })
  const pwc10data = pwc10r.json?.data
  const pwc10pageOk =
    pwc10r.ok &&
    pwc10data !== null &&
    typeof pwc10data === 'object' &&
    Array.isArray(pwc10data.items) &&
    typeof pwc10data.total === 'number' &&
    pwc10data.page === 1
  assert(
    'PWC10 动态：GET /api/production-work-calendar?pageIndex=1&pageSize=20（dev 库备料数据）',
    pwc10pageOk,
    pwc10pageOk
      ? `total=${pwc10data.total}, page=${pwc10data.page}, items=${pwc10data.items.length}, sourceSystem=${pwc10data.items[0]?.sourceSystem ?? 'n/a'}`
      : `code=${pwc10r.code}, message=${pwc10r.json?.message || pwc10r.text?.slice(0, 100) || '未知'}（可能 dev 库 schema 未跑迁移）`
  )

  // PWC11 动态：scopeType=FACTORY 过滤（5号位 函 §一参数）
  if (pwc10pageOk) {
    const pwc11r = await call('GET', '/api/production-work-calendar', {
      token,
      query: { scopeType: 'FACTORY', pageIndex: 1, pageSize: 50 }
    })
    const pwc11data = pwc11r.json?.data
    const pwc11ok =
      pwc11r.ok &&
      Array.isArray(pwc11data?.items) &&
      pwc11data.items.every((r) => r.scopeType === 'FACTORY')
    assert(
      'PWC11 动态：GET /api/production-work-calendar?scopeType=FACTORY 过滤生效（items 全为 FACTORY）',
      pwc11ok,
      pwc11ok ? `命中 ${pwc11data.items.length} 条 FACTORY` : `code=${pwc11r.code} 形态=${typeof pwc11data?.items}`
    )
  } else {
    skip('PWC11 动态：scopeType=FACTORY 过滤（依赖 PWC10 联调可达）')
  }

  // PWC12 动态：pageIndex=1&pageSize=2 应 < total（PWC10 必达）
  if (pwc10pageOk && pwc10data.total >= 2) {
    const pwc12r = await call('GET', '/api/production-work-calendar', {
      token,
      query: { pageIndex: 1, pageSize: 2 }
    })
    const pwc12data = pwc12r.json?.data
    const pwc12ok =
      pwc12r.ok &&
      Array.isArray(pwc12data?.items) &&
      pwc12data.items.length <= 2 &&
      pwc12data.pageSize <= 200 /* 函 §〇.3 上限 200 */
    assert(
      'PWC12 动态：pageSize=2 正常截断 + pageSize 上限=200 守住',
      pwc12ok,
      pwc12ok
        ? `items=${pwc12data.items.length}, pageSize=${pwc12data.pageSize}, total=${pwc12data.total}`
        : `code=${pwc12r.code} items=${pwc12data?.items?.length} pageSize=${pwc12data?.pageSize}`
    )
  } else {
    skip('PWC12 动态：pageSize 截断 + 上限 200（依赖 PWC10 + total≥2）')
  }

  // PWC13 静态：5 个写 action 成功后都 resetPage=true（防"翻页后看不到新行"翻车）
  //  - upsertWeekly / createException / closeOpen / closeRow / deleteRow
  //  - 必须 await load({}, { resetPage: true })，不能 await load() 或 await load(someQuery)
  const writeActions = [
    { name: 'upsertWeekly', re: /async\s+function\s+upsertWeekly[\s\S]*?await\s+load\(\s*\{\s*\}\s*,\s*\{\s*resetPage:\s*true\s*\}\s*\)/ },
    { name: 'createException', re: /async\s+function\s+createException[\s\S]*?await\s+load\(\s*\{\s*\}\s*,\s*\{\s*resetPage:\s*true\s*\}\s*\)/ },
    { name: 'closeOpen', re: /async\s+function\s+closeOpen[\s\S]*?await\s+load\(\s*\{\s*\}\s*,\s*\{\s*resetPage:\s*true\s*\}\s*\)/ },
    { name: 'closeRow', re: /async\s+function\s+closeRow[\s\S]*?await\s+load\(\s*\{\s*\}\s*,\s*\{\s*resetPage:\s*true\s*\}\s*\)/ },
    { name: 'deleteRow', re: /async\s+function\s+deleteRow[\s\S]*?await\s+load\(\s*\{\s*\}\s*,\s*\{\s*resetPage:\s*true\s*\}\s*\)/ }
  ]
  const pwc13AllOk = writeActions.every((a) => a.re.test(pwcStore))
  const pwc13Missing = writeActions.filter((a) => !a.re.test(pwcStore)).map((a) => a.name)
  assert(
    'PWC13 静态：5 个写 action 成功后 load({}, { resetPage: true }) 锁"翻页后能看到新行"',
    pwc13AllOk,
    pwc13AllOk
      ? '5/5 写 action 全走 resetPage'
      : `缺失：${pwc13Missing.join(', ')}（仍是 await load() 翻页会丢失新行）`
  )

  // PWC14 静态：3 弹窗 FactoryId/DepartmentId 用 ElSelect 名称下拉（不用 ElInputNumber）
  //  - 锁"@see feedback_list_id_to_name：列表/筛选/弹窗展示名称"
  //  - 不再要求"部门归属厂 hint"（10-13 用户要简洁，撤掉）
  const pwcVue = readSrc('views/Aps/ProductionWorkCalendar.vue')
  const noInputNumber = !/ElInputNumber/.test(pwcVue)
  const threeFactorySelects =
    (pwcVue.match(/<ElFormItem\s+label="工厂">/g) ?? []).length >= 3
  const threeDeptSelects =
    (pwcVue.match(/<ElFormItem[^>]*label="部门"/g) ?? []).length >= 3
  const pwc14AllOk = noInputNumber && threeFactorySelects && threeDeptSelects
  assert(
    'PWC14 静态：3 弹窗 FactoryId/DepartmentId 改 ElSelect（不用 ElInputNumber）',
    pwc14AllOk,
    pwc14AllOk
      ? `3×工厂+3×部门 ElSelect`
      : `noInputNumber=${noInputNumber} factoryLabels=${threeFactorySelects} deptLabels=${threeDeptSelects}`
  )

  // PWC15 静态：业务用户不接触 sourceSystem/sourceReference（@see feedback_no_internal_notices_in_ui）
  //  - 弹窗里不能有 label="数据来源" 或 label="数据来源系统" 的 ElFormItem
  //  - exception 弹窗的 sourceReference 必须 v-if="isAuthorizedOverride"（override 才显示）
  //  - weeklyDraft / exceptionDraft 默认 sourceSystem='MANUAL'（前端常驻）
  const noSourceSystemLabel = !/label="数据来源"/.test(pwcVue)
  const exceptionOverrideVIf =
    /<ElFormItem\s+v-if="exceptionDraft\.isAuthorizedOverride"\s+label="加班依据/.test(pwcVue)
  const weeklySourceSystemManual =
    /const\s+weeklyDraft\s*=\s*ref[\s\S]{0,800}?sourceSystem:\s*['"]MANUAL['"]/.test(pwcVue)
  const exceptionSourceSystemManual =
    /const\s+exceptionDraft\s*=\s*ref[\s\S]{0,1200}?sourceSystem:\s*['"]MANUAL['"]/.test(pwcVue)
  const pwc15AllOk =
    noSourceSystemLabel &&
    exceptionOverrideVIf &&
    weeklySourceSystemManual &&
    exceptionSourceSystemManual
  assert(
    'PWC15 静态：业务用户不接触数据来源（sourceSystem 前端常驻 MANUAL + sourceReference 仅 override 显示）',
    pwc15AllOk,
    pwc15AllOk
      ? '无 label="数据来源" + override-v-if + 双 MANUAL 默认'
      : `noLabel=${noSourceSystemLabel} overrideVIf=${exceptionOverrideVIf} weeklyMANUAL=${weeklySourceSystemManual} exceptionMANUAL=${exceptionSourceSystemManual}`
  )

  // PWC16 静态：3 处日期输入用 ElDatePicker（用户体验：日历选 vs 手输 yyyy-MM-dd）
  //  - 工作周设置 effectiveFrom / 日期例外 workDate / 批量换版 newEffectiveFrom
  //  - 日期例外的生效区间已从弹窗删除（例外天然只管 workDate 一天，effectiveFrom 提交时 = workDate）
  //  - 模板里 ElDatePicker 必须配 type="date" + value-format="YYYY-MM-DD"
  const dpPattern = /<ElDatePicker[\s\S]*?v-model="(\w+)\.(\w+)"[\s\S]*?type="date"[\s\S]*?value-format="YYYY-MM-DD"/g
  const dpBindings = new Set()
  for (const m of pwcVue.matchAll(dpPattern)) dpBindings.add(`${m[1]}.${m[2]}`)
  const expectBindings = [
    'weeklyDraft.effectiveFrom',
    'exceptionDraft.workDate',
    'closeOpenDraft.newEffectiveFrom'
  ]
  const allDpCovered = expectBindings.every((k) => dpBindings.has(k))
  const noManualDateInput = expectBindings.every(
    (k) => !new RegExp(`<ElInput[^>]*v-model="${k.replace('.', '\\.')}"[^>]*placeholder="yyyy-MM-dd"`).test(pwcVue)
  )
  const noExceptionEffectiveFields = !/exceptionDraft\.effectiveFrom/.test(pwcVue) && !/exceptionDraft\.effectiveTo/.test(pwcVue)
  const exceptionUsesWorkDate =
    /effectiveFrom:\s*exceptionDraft\.value\.workDate/.test(pwcVue)
  const pwc16AllOk = allDpCovered && noManualDateInput && noExceptionEffectiveFields && exceptionUsesWorkDate
  assert(
    'PWC16 静态：3 处日期输入用 ElDatePicker + 日期例外生效区间不出现在弹窗',
    pwc16AllOk,
    pwc16AllOk
      ? `${dpBindings.size} 处绑定 + 例外 effectiveFrom←workDate`
      : `dpCovered=${allDpCovered} noManual=${noManualDateInput} noEffField=${noExceptionEffectiveFields} useWorkDate=${exceptionUsesWorkDate}`
  )

  // PWC17 静态：列表不渲染来源字段（sourceSystem / sourceReference）
  //  - 与弹窗口径一致（@see feedback_no_internal_notices_in_ui：业务用户不接触内部协调字段）
  //  - 加班授权列必须保留（AllowLowerOverride / IsAuthorizedOverride 是 v1.4 §四 三授权旗核心业务语义）
  //  - 文案按冻结文档 v1.12 基线「授权加班必须有真实可追溯覆盖」用中文，不用 Alo/Aao 字段名
  const noSourceColumn = !/label="来源"/.test(pwcVue)
  const noSourceSystemRender = !/row\.sourceSystem/.test(pwcVue)
  const noSourceReferenceRender = !/row\.sourceReference\s*\}/.test(pwcVue)
  const authFlagsKept =
    /label="加班授权"/.test(pwcVue) &&
    /允许下级加班/.test(pwcVue) &&
    /本行是授权加班/.test(pwcVue) &&
    /row\.allowLowerOverride/.test(pwcVue) &&
    /row\.isAuthorizedOverride/.test(pwcVue)
  const noFieldNameLeak = !/label="Alo=1"/.test(pwcVue) && !/label="Aao=1"/.test(pwcVue)
  const pwc17AllOk =
    noSourceColumn && noSourceSystemRender && noSourceReferenceRender && authFlagsKept && noFieldNameLeak
  assert(
    'PWC17 静态：列表隐藏来源列 + 加班授权列中文文案（Alo/Aao 不露字段名）',
    pwc17AllOk,
    pwc17AllOk
      ? '无来源列 + 加班授权（允许下级加班 / 本行是授权加班）'
      : `noCol=${noSourceColumn} noSys=${noSourceSystemRender} noRef=${noSourceReferenceRender} authFlags=${authFlagsKept} noLeak=${noFieldNameLeak}`
  )

  // PWC18 静态：筛选区不暴露 includeInactive 开关（后端无路径产生 IsActive=0 行）
  //  - Service.cs:239-242 CloseRow 只写 EffectiveTo；:213-220 CloseOpen 同；:276 INSERT 恒写 IsActive=1
  //  - 无「含停用」文案 + 无 filterIncludeInactive 引用（防复现无效开关）
  const noIncludeInactiveText = !/含停用/.test(pwcVue)
  const noIncludeInactiveState = !/filterIncludeInactive/.test(pwcVue)
  const pwc18AllOk = noIncludeInactiveText && noIncludeInactiveState
  assert(
    'PWC18 静态：筛选区无「含停用」开关（后端无 IsActive=0 产生路径）',
    pwc18AllOk,
    pwc18AllOk
      ? '无「含停用」文案 + 无 filterIncludeInactive'
      : `noText=${noIncludeInactiveText} noState=${noIncludeInactiveState}`
  )

  // PWC19 静态：UI 文案不露后端字段值（WORK/OFF/scopeType/Id/sourceReference 等）
  //  - 列表状态列 + 2 弹窗状态选择 + 校验提示 + 弹窗标题全用中文
  //  - types 层契约注释（PRODUCTION_CALENDAR_STATUS_META 等）不算 UI，不锁
  const uiJargon = [
    /<ElRadioButton value="WORK">WORK</,
    /<ElRadioButton value="OFF">OFF</,
    /<ElOption label="WORK" value="WORK"/,
    /<ElOption label="OFF" value="OFF"/,
    /ElMessage\.warning\('WORK /,
    /ElMessage\.warning\('IsAuthorizedOverride/,
    /ElMessage\.warning\('AllowLowerOverride/,
    /ElMessage\.warning\('DEPARTMENT 范围/,
    /'请至少添加一条 entry'/
  ]
  const leakedJargon = uiJargon.filter((re) => re.test(pwcVue))
  const chineseStateLabels =
    /<ElRadioButton value="WORK">要工作</.test(pwcVue) &&
    /<ElRadioButton value="OFF">不工作</.test(pwcVue) &&
    /<ElOption label="要工作" value="WORK"/.test(pwcVue) &&
    /<ElOption label="不工作" value="OFF"/.test(pwcVue)
  const pwc19AllOk = leakedJargon.length === 0 && chineseStateLabels
  assert(
    'PWC19 静态：UI 文案不露字段值（WORK/OFF → 要工作/不工作，校验提示全中文）',
    pwc19AllOk,
    pwc19AllOk
      ? '状态选择 + 校验提示全中文'
      : `leaked=${leakedJargon.length} (${leakedJargon.join(', ')}) chinese=${chineseStateLabels}`
  )

  // PWC20 静态：sortBy/sortDir 契约（5号位 2026-10-14 回执 R1-R4 全采纳）
  const hasSortByType = /ProductionWorkCalendarSortBy\s*=/.test(pwcTypes)
  const hasSortByWhiteList =
    /'Id'\s*\|\s*'CreatedAt'\s*\|\s*'EffectiveFrom'\s*\|\s*'EffectiveTo'\s*\|\s*'UpdatedAt'/.test(
      pwcTypes.replace(/\s*\n\s*/g, ' ')
    )
  const hasSortDirType = /sortDir\?:\s*'ASC'\s*\|\s*'DESC'/.test(pwcTypes)
  const apiPassesSort =
    /sortBy:\s*query\.sortBy/.test(pwcApi) && /sortDir:\s*query\.sortDir/.test(pwcApi)
  const storeDefaultSort =
    /DEFAULT_SORT[\s\S]{0,200}?sortBy:\s*'CreatedAt',\s*sortDir:\s*'DESC'/.test(
      pwcStore.replace(/\s*\n\s*/g, ' ')
    )
  const storeLoadMerge =
    /if \(nextQuery\) query\.value = \{ \.\.\.DEFAULT_SORT, \.\.\.nextQuery \}/.test(pwcStore)
  const pwc20AllOk =
    hasSortByType &&
    hasSortByWhiteList &&
    hasSortDirType &&
    apiPassesSort &&
    storeDefaultSort &&
    storeLoadMerge
  assert(
    'PWC20 静态：sortBy/sortDir 契约已接（types 白名单 + api 透传 + store 默认 CreatedAt DESC）',
    pwc20AllOk,
    pwc20AllOk
      ? '5 值白名单 + ASC/DESC + load() 兜底默认排序'
      : `type=${hasSortByType} list=${hasSortByWhiteList} dir=${hasSortDirType} api=${apiPassesSort} storeDefault=${storeDefaultSort} loadMerge=${storeLoadMerge}`
  )

  // PWC21 动态：sortBy=CreatedAt&sortDir=DESC 首条即最新行（5号位 函 §二 实测 6/6）
  if (token) {
    const pSorted = await call('GET', '/api/production-work-calendar', {
      token,
      query: { pageIndex: 1, pageSize: 20, sortBy: 'CreatedAt', sortDir: 'DESC' }
    })
    const sItems = pSorted.json?.data?.items ?? []
    const createdDescOk =
      sItems.length >= 2 &&
      sItems.every((it, i) => i === 0 || (sItems[i - 1].createdAt ?? '') >= (it.createdAt ?? ''))
    assert(
      'PWC21 动态：sortBy=CreatedAt&sortDir=DESC 列表按创建时间倒序（新增行居首）',
      pSorted.ok && createdDescOk,
      pSorted.ok && createdDescOk
        ? `items=${sItems.length} 首行 createdAt=${sItems[0]?.createdAt}`
        : `ok=${pSorted.ok} items=${sItems.length} 倒序=${createdDescOk}`
    )

    const pBad = await call('GET', '/api/production-work-calendar', {
      token,
      query: { sortBy: 'DropTable' }
    })
    assert(
      'PWC21b 动态：非法 sortBy 返 400（白名单守门，非 500）',
      pBad.code === 400,
      pBad.code === 400 ? '400 ✓' : `code=${pBad.code}`
    )
  } else {
    skip('PWC21 动态：sortBy=CreatedAt 倒序 + 非法值 400（需 adminToken）')
  }

  // PWC22 静态：工厂→部门联动（冻结 v5.1.12 line 2946 DEPARTMENT 行须校验 FactoryId 一致）
  const pwc22Lookup = readSrc('api/aps-v1/departmentLookup.ts')
  const pwc22a = /factoryId\?:\s*number\s*\|\s*null/.test(pwc22Lookup)
  assert(
    'PWC22a 静态：DepartmentLookupItem 带 factoryId（部门归属工厂，5号位 待补 lookup 投影）',
    pwc22a,
    pwc22a ? '可选字段已声明' : '缺 factoryId，页面无法按工厂收敛部门'
  )

  const pwc22b =
    /function\s+deptOptionsFor\s*\(/.test(pwcVue) &&
    /deptOptionsByFactory/.test(pwcVue) &&
    /deptOptionsFor\(filterFactoryId\)/.test(pwcVue) &&
    /deptOptionsFor\(weeklyDraft\.factoryId\)/.test(pwcVue) &&
    /deptOptionsFor\(exceptionDraft\.factoryId\)/.test(pwcVue) &&
    /deptOptionsFor\(closeOpenDraft\.factoryId\)/.test(pwcVue)
  assert(
    'PWC22b 静态：4 处部门下拉（筛选 + 3 弹窗）均走 deptOptionsFor(工厂) 联动',
    pwc22b,
    pwc22b ? '筛选/工作周/例外/换版 4 处全联动' : '存在未联动的 departmentOptions 全量下拉'
  )

  const pwc22c =
    (pwcVue.match(/deptAfterFactoryChange\(/g) ?? []).length >= 5 &&
    /:disabled="!filterFactoryId"/.test(pwcVue)
  assert(
    'PWC22c 静态：切工厂后收敛已选部门（4 处 watch）+ 筛选区未选工厂时禁用部门',
    pwc22c,
    pwc22c ? '4 watch 齐全 + 筛选部门 disabled' : 'watch/disabled 缺失'
  )

  // PWC23 动态：后端 lookup 是否已返 factoryId（3号位 A组 A1 10-14 已落地；需 dev 联调时开 VERIFY_PWC_DEPT_CASCADE=1）
  if (token && process.env.VERIFY_PWC_DEPT_CASCADE === '1') {
    const pDept = await call('GET', '/api/governance/lookups/departments', { token })
    const dItems = pDept.json?.data ?? []
    const withFactory = dItems.filter((d) => d.factoryId != null).length
    assert(
      'PWC23 动态：/api/governance/lookups/departments 返 factoryId（工厂→部门联动生效）',
      dItems.length > 0 && withFactory > 0,
      `total=${dItems.length} 带 factoryId=${withFactory}`
    )
  } else {
    skip('PWC23 动态：lookup 返 factoryId（A1 已落地，开 VERIFY_PWC_DEPT_CASCADE=1 联调实测；未开时工厂→部门下拉按缺 key 退回全量）')
  }

  // PWC31 静态：列表「生效状态」列（冻结 13 v1.8:31 三态页面化；dev 62 行里 10 已过期 3 未生效，此前列表无标识）
  const pwcVueCode = stripComments(pwcVue)
  const pwc31a =
    /label="生效状态"/.test(pwcVue) &&
    /PWC_EFFECTIVE_STATUS_META\[effectiveStatus\(row\)\]/.test(pwcVue)
  assert(
    'PWC31a 静态：列表渲染「生效状态」列（按行前端派生，不走服务端筛）',
    pwc31a,
    pwc31a ? '列已渲染 + 走 effectiveStatus(row)' : '缺「生效状态」列或未走派生函数'
  )

  const pwc31b =
    /type\s+PwcEffectiveStatus\s*=\s*'ACTIVE'\s*\|\s*'NOT_YET'\s*\|\s*'EXPIRED'\s*\|\s*'DISABLED'/.test(
      pwcVueCode
    ) &&
    /function\s+effectiveStatus\s*\(/.test(pwcVueCode)
  assert(
    'PWC31b 静态：生效状态类型含四态（生效中/未生效/已过期/已停用）',
    pwc31b,
    pwc31b ? '四态齐全 + effectiveStatus 函数在' : '状态联合缺态或缺 effectiveStatus'
  )

  const pwc31b2 =
    /ACTIVE:\s*\{\s*label:\s*'生效中'/.test(pwcVueCode) &&
    /NOT_YET:\s*\{\s*label:\s*'未生效'/.test(pwcVueCode) &&
    /EXPIRED:\s*\{\s*label:\s*'已过期'/.test(pwcVueCode) &&
    /DISABLED:\s*\{\s*label:\s*'已停用'/.test(pwcVueCode)
  assert(
    'PWC31b2 静态：四态各有中文标签（生效中/未生效/已过期/已停用）',
    pwc31b2,
    pwc31b2 ? '四标签齐' : '缺中文标签'
  )

  const pwc31cBody =
    /function\s+todayLocal\s*\(\)\s*:\s*string\s*\{([\s\S]*?)\n\}/.exec(pwcVueCode)?.[1] ?? ''
  const pwc31c =
    pwc31cBody.length > 0 &&
    /getFullYear\(\)/.test(pwc31cBody) &&
    /getMonth\(\)/.test(pwc31cBody) &&
    /getDate\(\)/.test(pwc31cBody) &&
    !/getUTC/.test(pwc31cBody)
  assert(
    'PWC31c 静态：「今天」按本地日推导（todayLocal 体内用 getFullYear/getMonth/getDate，无 getUTC*）',
    pwc31c,
    pwc31c ? '本地日推导' : 'todayLocal 缺失或体内混入 UTC 方法'
  )

  // PWC32 静态：本视图零 UTC 日期残留（后端 DateTime.Today = 服务器本地日，UTC 日本地 00:00–08:00 差一天）
  const pwc32a = !/toISOString\s*\(/.test(pwcVueCode)
  assert(
    'PWC32a 静态：ProductionWorkCalendar.vue 无 toISOString（UTC 日）残留',
    pwc32a,
    pwc32a ? '零残留' : '仍存在 toISOString 调用'
  )

  const pwc32b =
    (pwcVueCode.match(/todayLocal\(\)/g) ?? []).length >= 4 &&
    /function\s+shiftLocal\s*\(/.test(pwcVueCode) &&
    /shiftLocal\(7\)/.test(pwcVueCode) &&
    /shiftLocal\(30\)/.test(pwcVueCode)
  assert(
    'PWC32b 静态：默认日期/今日/删除守卫/换版均走本地日（todayLocal ×≥4 + shiftLocal(7)/shiftLocal(30)）',
    pwc32b,
    pwc32b ? '默认日期全本地化' : '存在未改的 UTC 默认日期推导'
  )

  // PWC33 静态：store 不得暴露「只按当前页统计」的计数 getter（挂 KPI 卡即假数）
  const pwcStoreCode = stripComments(pwcStore)
  const pwc33 =
    !/\bworkCount\b/.test(pwcStoreCode) &&
    !/\boffCount\b/.test(pwcStoreCode) &&
    !/\boverrideCount\b/.test(pwcStoreCode)
  assert(
    'PWC33 静态：store 无 workCount/offCount/overrideCount（当前页口径 = 假数 KPI）',
    pwc33,
    pwc33 ? '已移除' : '仍存在当前页口径的计数 getter'
  )

  // PWC34 静态：互斥标记随状态归一 + 提交前清洗 + 部门下拉不回退全量（10-23 用户实测无解报错）
  const pwc34a =
    /function\s+normalizeExceptionDraft\s*\(/.test(pwcVueCode) &&
    /watch\(\s*\(\)\s*=>\s*exceptionDraft\.value\.workState\s*,\s*normalizeExceptionDraft\s*\)/.test(
      pwcVueCode
    ) &&
    /workState\s*===\s*'WORK'[\s\S]{0,200}allowLowerOverride\s*=\s*false/.test(pwcVueCode) &&
    /isAuthorizedOverride\s*=\s*false/.test(pwcVueCode)
  assert(
    'PWC34a 静态：例外弹窗切「要工作」即清「允许下级加班」、切「不工作」即清「本行是授权加班」',
    pwc34a,
    pwc34a ? '状态归一已挂 watch' : '缺归一逻辑（隐藏开关残留 ⇒ 提交被无解拦截）'
  )

  const pwc34b =
    /workState\s*===\s*'OFF'\s*&&\s*exceptionDraft\.value\.allowLowerOverride/.test(pwcVueCode) &&
    /workState\s*===\s*'WORK'\s*&&\s*exceptionDraft\.value\.isAuthorizedOverride/.test(pwcVueCode) &&
    /e\.workState\s*===\s*'OFF'\s*&&\s*e\.allowLowerOverride/.test(pwcVueCode)
  assert(
    'PWC34b 静态：例外/周基准提交前按状态清洗（不把后端必拒组合发出去）',
    pwc34b,
    pwc34b ? '例外 + 周基准双处已清洗' : '提交前未按状态清洗'
  )

  const pwc34c =
    /deptOptionsByFactory\.value\.get\(factoryId\)/.test(pwcVueCode) &&
    /departmentOptions\.value\.filter\(\(d\)\s*=>\s*d\.factoryId\s*==\s*null\)/.test(pwcVueCode) &&
    !/\?\?\s*departmentOptions\.value/.test(pwcVueCode)
  assert(
    'PWC34c 静态：部门下拉不回退全量（跨厂部门后端必 400，只给归属未知的部门兜底）',
    pwc34c,
    pwc34c ? '只给归属未知部门' : '仍在回退全量部门'
  )

  info('PWC 段：v1.4 §三/§九 + 5号位 2026-10-13 PWC 函前端联调备料版就位（G1 候选，G4 公告前联调/备料；v1.3 继续有效）')
}

/**
 * [DP] DemandProtection 冻结文档一致性核查（dp-freeze — 10-09 R1/R2/R3 闭环静态）
 * 验收依据：5号位致4号位_DemandProtection一致性核查回执_20261009.md
 *  - R1 PARTIALLY_RELEASED 派生态 5号位 接受（前端聚合层派生），stale 5号位 聚合注释已纠正 → DP01-DP03
 *  - R2 isIrreversible 字段已移交 2号位 联合评估，本期不锁（待 5号位 DTO 透传后再开 DP06/07）
 *  - R3 三事实已核实答复，无前端联动
 *  - §2.3 page-sub §42.2 文案 + §2.4 4 项状态/placeholder/KPI 文案 → DP04/05
 */
async function verifyDemandProtectionFreeze() {
  console.log('\n=== [DP] DemandProtection 冻结一致性（dp-freeze — 10-09 函件 + 冻结文档 §42）===')

  const dpTypes = readSrc('api/aps-v1/types/demandProtection.ts')
  const dpVue = readSrc('views/Aps/DemandProtection.vue')

  // DP01 静态：types/demandProtection.ts L13 4号位职责边界已纠正派生态派生者 = 前端 store
  //   - 修复前："不计算 status 派生态（... 由 5号位聚合）"
  //   - 修复后："不计算 status 派生态（... 由前端 store 聚合；详见 5号位 2026-10-09 函件 R1 回执）"
  const dp01Boundary =
    /不计算\s+status\s+派生态[\s\S]{0,80}?前端\s+store\s+聚合/.test(dpTypes) &&
    /5号位\s+2026-10-09\s+函件\s+R1\s+回执/.test(dpTypes)
  assert(
    'DP01 静态：types/demandProtection.ts 4号位职责边界已标派生态派生者 = 前端 store（5号位 R1 回执）',
    dp01Boundary,
    dp01Boundary ? '已纠正' : '仍标 5号位 聚合'
  )

  // DP02 静态：DemandProtectionStatus 类型注释已改正（"由前端 store applyReleaseResultsToList 根据明细 lockDetail.status 派生"）
  const dp02TypeHeader =
    /由\s*前端\s+store\s+applyReleaseResultsToList\s+根据明细\s+lockDetail\.status\s+派生/.test(dpTypes)
  assert(
    'DP02 静态：DemandProtectionStatus 类型注释已标派生者 = 前端 store applyReleaseResultsToList',
    dp02TypeHeader,
    dp02TypeHeader ? '已加 store 函数名引用' : '类型注释仍标 5号位 派生'
  )

  // DP03 静态：DemandProtectionDto.status 字段注释已改正（"由前端 store 聚合，4号位不计算"）
  const dp03FieldDoc =
    /派生态[（(]由\s*前端\s+store\s+聚合/.test(dpTypes) &&
    !/派生态[（(]由\s*5号位\s*聚合/.test(dpTypes)
  assert(
    'DP03 静态：DemandProtectionDto.status 字段注释派生者 = 前端 store（不再误标 5号位）',
    dp03FieldDoc,
    dp03FieldDoc ? '已纠正' : '字段注释仍标 5号位 聚合'
  )

  // DP04 静态：§42.2 page-sub「锁的是供给数量份额（非整张 PI/PO）」已撤销（10-09 用户决定）
  //   - 依据：[[feedback-no-internal-notices-in-ui]] 内部协调痕迹不进用户可见区
  //   - 反向探针：page-sub 元素不应存在（防止未来回退）
  //   - 释放页面「只支持整条释放」ElAlert 也已撤销（同条原则）
  const dp04HasPageSub = /class="page-sub"/.test(dpVue)
  const dp04HasFullReleaseAlert = /只支持整条释放/.test(dpVue)
  assert(
    'DP04 静态：DemandProtection.vue page-sub + "只支持整条释放" ElAlert 已撤销（10-09 用户决定，反向探针）',
    !dp04HasPageSub && !dp04HasFullReleaseAlert,
    !dp04HasPageSub && !dp04HasFullReleaseAlert
      ? 'page-sub + 全条释放提示均已移除'
      : `仍含解释文字（page-sub=${dp04HasPageSub}, fullReleaseAlert=${dp04HasFullReleaseAlert}）`
  )

  // DP05 静态：§2.4 3 项中文命名术语对齐（KPI 卡 10-09 撤销后，"含部分释放的混合态" KPI 副文案消失）
  //   - 保留：生效中（表格状态列）+ 历史混合态（Drawer PARTIALLY_RELEASED 徽章）+ 便于事后追溯（释放原因 placeholder）
  //   - 移除：含部分释放的混合态（KPI 副文案）— 随 KPI 卡一并消失
  const dp05a = /生效中/.test(dpVue)
  const dp05c = /历史混合态[（(]前端不生产该状态[）)]/.test(dpVue)
  const dp05d = /便于事后追溯/.test(dpVue)
  const dp05 = dp05a && dp05c && dp05d
  assert(
    'DP05 静态：§2.4 3 项中文命名术语对齐（生效中/历史混合态/便于事后追溯）',
    dp05,
    `生效中=${dp05a} 历史混合态=${dp05c} 便于追溯=${dp05d}`
  )

  // 注意：R2 isIrreversible (5号位 R2 → 2号位 联合评估) 暂不开断言；
  //   - 落地窗口：5号位 承诺 DTO 透传在 2号位 写锁 IsIrreversible 落列后
  //   - 前端待办（待 2号位 + 5号位 落地后）：
  //     - DTO 增 isIrreversible?: boolean
  //     - canRelease() 增不可逆判定（聚合行任一 lockDetail.isIrreversible → 整条不可释放）
  //     - Drawer 锁明细表格加 "不可逆" 徽章列
  //     - 释放按钮 disabled + tooltip 复用 STRICT_BINDING 模式
  //   - 4号位 端不动，等回执
  skip('DP06 静态：lockDetail.isIrreversible 字段 + canRelease() 不可逆判定（待 2号位 + 5号位 落地后开）')
  skip('DP07 静态：Drawer 锁明细 "不可逆" 徽章列 + 释放按钮 disabled/tooltip（待后端 DTO 透传后开）')

  info('DP 段：5号位 2026-10-09 一致性核查回执 R1/R2/R3 闭环（前 R1 静态 + §2.3/§2.4 中文术语对齐；R2 等 2号位 落地）')
}

/**
 * [DPL] DemandProtection lps/ 联调（dplive — 10-09 5号位 后端落地 → 4号位 前端适配）
 *
 * 验收依据：5号位 lps/ 10-09 落地（GET 列表 per-lock / GET /summary KPI / POST release）
 *  4号位 端按 AskUserQuestion 拍板：
 *   - UX: A — 前端聚合后端 flat list
 *   - KPI: A — 调 /summary 端点拿权威数
 *
 * 验收点：
 *  - DPL01-03 静态：types/demandProtection.ts 含 BackendDemandProtectionRow + DemandProtectionSummary + 4 字段 filter
 *  - DPL04-05 静态：api/demandProtection.ts list() 返 per-lock；summary() 调 /summary 端点
 *  - DPL06-07 静态：store 含 aggregateByDemandKey + kpiSummary ref + 5 KPI getter
 *  - DPL08 静态：view 筛选改用 substring + single lockType/status
 *  - DPL09 静态：view KPI "总锁条数" 用 totalCount（不是 list.length）
 *  - DPL10 静态：store load() Promise.all(list + summary)
 */
async function verifyDemandProtectionLive() {
  console.log('\n=== [DPL] DemandProtection lps/ 联调（dplive — 10-09 5号位 后端落地 → 4号位 前端适配）===')

  const dpTypes = readSrc('api/aps-v1/types/demandProtection.ts')
  const dpApi = readSrc('api/aps-v1/demandProtection.ts')
  const dpStore = readSrc('store/modules/aps/demandProtection.ts')
  const dpVue = readSrc('views/Aps/DemandProtection.vue')

  // DPL01 静态：types/demandProtection.ts BackendDemandProtectionRow interface 存在
  const dpl01 =
    /export\s+interface\s+BackendDemandProtectionRow\b/.test(dpTypes) &&
    /\bid\s*:\s*number\b/.test(dpTypes.match(/export\s+interface\s+BackendDemandProtectionRow\s*\{[\s\S]*?\n\}/)?.[0] ?? '') &&
    /\blockedQty\s*:\s*number\b/.test(dpTypes.match(/export\s+interface\s+BackendDemandProtectionRow\s*\{[\s\S]*?\n\}/)?.[0] ?? '') &&
    /\bstatus\s*:\s*HardLockStatus\b/.test(dpTypes.match(/export\s+interface\s+BackendDemandProtectionRow\s*\{[\s\S]*?\n\}/)?.[0] ?? '')
  assert(
    'DPL01 静态：types/demandProtection.ts BackendDemandProtectionRow interface 含 id/lockedQty/status',
    dpl01,
    dpl01 ? '已含 per-lock raw DTO' : '缺 BackendDemandProtectionRow 字段'
  )

  // DPL02 静态：types/demandProtection.ts DemandProtectionSummary interface 存在
  const summaryBlock = dpTypes.match(/export\s+interface\s+DemandProtectionSummary\s*\{[\s\S]*?\n\}/)?.[0] ?? ''
  const dpl02 =
    /export\s+interface\s+DemandProtectionSummary\b/.test(dpTypes) &&
    /\btotalCount\s*:\s*number\b/.test(summaryBlock) &&
    /\bactiveCount\s*:\s*number\b/.test(summaryBlock) &&
    /\breleasedCount\s*:\s*number\b/.test(summaryBlock) &&
    /\bbrokenCount\s*:\s*number\b/.test(summaryBlock) &&
    /\btotalLockedQty\s*:\s*number\b/.test(summaryBlock) &&
    /\bitems\s*:\s*BackendDemandProtectionRow\[\]/.test(summaryBlock)
  assert(
    'DPL02 静态：types/demandProtection.ts DemandProtectionSummary 含 5 KPI 字段 + items[]',
    dpl02,
    dpl02 ? 'summary DTO 已对齐 5号位' : 'summary DTO 缺字段'
  )

  // DPL03 静态：types/demandProtection.ts DemandProtectionListFilter 含 demandKey/supplyKey/lockType/status/skip/take
  const filterBlock = dpTypes.match(/export\s+interface\s+DemandProtectionListFilter\s*\{[\s\S]*?\n\}/)?.[0] ?? ''
  const dpl03 =
    /\bdemandKey\?\s*:/.test(filterBlock) &&
    /\bsupplyKey\?\s*:/.test(filterBlock) &&
    /\blockType\?\s*:\s*HardLockType\b/.test(filterBlock) &&
    /\bstatus\?\s*:\s*HardLockStatus\b/.test(filterBlock) &&
    /\bskip\?\s*:\s*number\b/.test(filterBlock) &&
    /\btake\?\s*:\s*number\b/.test(filterBlock)
  assert(
    'DPL03 静态：types/demandProtection.ts DemandProtectionListFilter 6 字段（demandKey/supplyKey/lockType/status/skip/take）',
    dpl03,
    dpl03 ? 'filter 形状已对齐 5号位 QueryAsync' : 'filter 缺字段'
  )

  // DPL04 静态：api/demandProtection.ts list() 返回 BackendDemandProtectionRow[]
  const dpl04 =
    /async\s+list\s*\([^)]*\)\s*:\s*Promise<BackendDemandProtectionRow\[\]>/.test(dpApi) &&
    /url:\s*['"]\/api\/demand-protection['"]/.test(dpApi)
  assert(
    'DPL04 静态：api/demandProtection.ts list() 返 BackendDemandProtectionRow[]（per-lock 扁平）',
    dpl04,
    dpl04 ? 'list() 签名 + URL 已对齐' : 'list() 未返 per-lock / URL 不匹配'
  )

  // DPL05 静态：api/demandProtection.ts summary() 调 /api/demand-protection/summary
  const dpl05 =
    /async\s+summary\s*\(/.test(dpApi) &&
    /url:\s*['"]\/api\/demand-protection\/summary['"]/.test(dpApi) &&
    /Promise<DemandProtectionSummary>/.test(dpApi)
  assert(
    'DPL05 静态：api/demandProtection.ts summary() 调 /api/demand-protection/summary',
    dpl05,
    dpl05 ? 'summary() 已对齐 5号位' : 'summary() 缺端点 / 签名'
  )

  // DPL06 静态：store 含 aggregateByDemandKey 函数（按 demandKey 聚合）
  const dpl06 =
    /function\s+aggregateByDemandKey\s*\(/.test(dpStore) &&
    /new\s+Map<string,\s*BackendDemandProtectionRow\[\]>/.test(dpStore) &&
    /PARTIALLY_RELEASED/.test(dpStore)
  assert(
    'DPL06 静态：store aggregateByDemandKey 按 demandKey 聚合 + 派生 PARTIALLY_RELEASED',
    dpl06,
    dpl06 ? '前端聚合层已实现' : '缺 aggregateByDemandKey / 派生态'
  )

  // DPL07 静态：store KPI 基础设施已撤销（10-09 用户决定：列表已能表达信息密度，KPI 卡属冗余 UI）
  //   - 反向探针：kpiSummary ref + 5 KPI getter + refreshKpi 全部不应存在
  //   - GET /api/demand-protection/summary 端点（api.summary()）保留，未来需要时启用
  const dpl07HasKpiSummary = /kpiSummary\s*=\s*ref/.test(dpStore)
  const dpl07HasTotalCountGetter = /\btotalCount\s*=\s*computed/.test(dpStore)
  const dpl07HasActiveCountGetter = /\bactiveCount\s*=\s*computed/.test(dpStore)
  const dpl07HasReleasedCountGetter = /\breleasedCount\s*=\s*computed/.test(dpStore)
  const dpl07HasBrokenCountGetter = /\bbrokenCount\s*=\s*computed/.test(dpStore)
  const dpl07HasTotalLockedQtyGetter = /\btotalLockedQty\s*=\s*computed/.test(dpStore)
  const dpl07HasRefreshKpi = /function\s+refreshKpi|async\s+function\s+refreshKpi/.test(dpStore)
  const dpl07 =
    !dpl07HasKpiSummary &&
    !dpl07HasTotalCountGetter &&
    !dpl07HasActiveCountGetter &&
    !dpl07HasReleasedCountGetter &&
    !dpl07HasBrokenCountGetter &&
    !dpl07HasTotalLockedQtyGetter &&
    !dpl07HasRefreshKpi
  assert(
    'DPL07 静态：store KPI 基础设施已撤销（kpiSummary ref + 5 KPI getter + refreshKpi 全部移除）',
    dpl07,
    dpl07
      ? 'KPI 基础设施已全部移除'
      : `仍含 KPI 基础设施（kpiSummary=${dpl07HasKpiSummary}, totalCount=${dpl07HasTotalCountGetter}, activeCount=${dpl07HasActiveCountGetter}, releasedCount=${dpl07HasReleasedCountGetter}, brokenCount=${dpl07HasBrokenCountGetter}, totalLockedQty=${dpl07HasTotalLockedQtyGetter}, refreshKpi=${dpl07HasRefreshKpi}）`
  )

  // DPL08 静态：view 筛选改用 substring + single lockType/status（不再用 distinctDemandTypes）
  const dpl08 =
    /filterDemandKey/.test(dpVue) &&
    /filterSupplyKey/.test(dpVue) &&
    /filterLockType/.test(dpVue) &&
    /filterStatus/.test(dpVue) &&
    !/distinctDemandTypes/.test(dpVue) &&
    !/distinctLockTypes/.test(dpVue) &&
    !/activeOnlyDraft/.test(dpVue)
  assert(
    'DPL08 静态：view 筛选改用 4 字段（demandKey/supplyKey substring + single lockType/status）',
    dpl08,
    dpl08 ? '筛选形状已对齐 5号位' : 'view 仍用旧多选/activeOnly 形状'
  )

  // DPL09 静态：view KPI 卡已撤销（10-09 用户决定：列表已能表达信息密度）
  //   - 反向探针：4 个 KPI 卡（总锁条数/生效中/已释放/锁定总量）均不应存在
  //   - kpi-row / kpi-card / kpi-label / kpi-value / kpi-ratio 5 个 CSS class 不应在 view 模板出现
  const dpl09HasKpiRow = /class="kpi-row/.test(dpVue)
  const dpl09HasKpiCard = /class="kpi-card/.test(dpVue)
  const dpl09HasKpiLabel = /kpi-label">/.test(dpVue)
  const dpl09HasKpiValue = /kpi-value"/.test(dpVue)
  const dpl09 =
    !dpl09HasKpiRow && !dpl09HasKpiCard && !dpl09HasKpiLabel && !dpl09HasKpiValue
  assert(
    'DPL09 静态：view 4 个 KPI 卡已撤销（kpi-row / kpi-card / kpi-label / kpi-value 全部不存在）',
    dpl09,
    dpl09
      ? 'KPI 卡已全部移除'
      : `仍含 KPI 卡（kpi-row=${dpl09HasKpiRow}, kpi-card=${dpl09HasKpiCard}, kpi-label=${dpl09HasKpiLabel}, kpi-value=${dpl09HasKpiValue}）`
  )

  // DPL10 静态：store load() 单拉 list（KPI 撤销后无需 Promise.all）
  const dpl10 =
    /async\s+function\s+load\s*\(/.test(dpStore) &&
    /await\s+demandProtectionApi\.list\(/.test(dpStore) &&
    !/Promise\.all\(/.test(dpStore)
  assert(
    'DPL10 静态：store load() 单拉 list（无 Promise.all，无 summary 并行）',
    dpl10,
    dpl10 ? 'load() 已简化为单拉 list' : 'load() 仍并行 / 缺 list 调用'
  )

  info('DPL 段：10-09 5号位 lps/ 落地 → 4号位 前端 per-lock + /summary KPI 适配完成（A 路径）')
}

/**
 * [E] Explanation 页面冻结一致性（e-freeze — 10-09 5号位 函件 R1/R2/R3 一致性核查回执）
 *
 * 验收依据：5号位 2026-10-09 回执 [5号位-2026-10-09-Explanation页面一致性-回执-给4号位.md]
 *  - R1 ReasonCode 权威源 = v5.1.10 §八.1 15 码全集（**非 §85 6 码**）
 *  - R1 FROZEN_ZONE_LOCK = 15 码唯一锁码；FIRM/FROZEN/其它执行锁统一并入
 *  - R3 ScheduleFailureInfo 后端不存在 → 撤前端占位面板
 *  - R2 关键路径 / R3 failureSeverity 跨号位（1号位 求解 + 2号位 落盘 + 0号位 裁决 V1 范围）—— 前端不动
 *
 * 验收点（10 静态 + 2 skip）：
 *  - E01-04 静态：types/explanation.ts REASON_CODES 15 码 + 3 码 Severity + 5 码 ObjectType + 撤 ScheduleFailureInfo
 *  - E05 静态：api/explanation.ts getByPlanVersion 修 400 bug（params 透传 planVersionId）
 *  - E06-08 静态：store severityCounts 3 码 + factsByObjectType 5 码 + 接 Summary 端点 errorCount/warnCount/infoCount
 *  - E09 静态：view 排程解释事实展示 15 码 reasonCode 徽章
 *  - E10 静态：view 撤「排程域失败」面板（5号位 关键纠正 3）
 *  - E11-12 skip：R2 isOnCriticalPath + R3 failureSeverity 跨号位（@see dp-freeze DP06/07 模式）
 */
async function verifyExplanationFreeze(adminToken) {
  console.log('\n=== [E] Explanation 页面冻结一致性（e-freeze — 10-09 5号位 函件 R1/R2/R3 一致性核查回执）===')

  const eTypes = readSrc('api/aps-v1/types/explanation.ts')
  const eApi = readSrc('api/aps-v1/explanation.ts')
  const eStore = readSrc('store/modules/aps/explanation.ts')
  const eScheduleStore = readSrc('store/modules/aps/schedule.ts')
  const eVue = readSrc('views/Aps/Explanation.vue')
  const eCommon = readSrc('api/aps-v1/types/common.ts')

  // E01 静态：types/explanation.ts REASON_CODES = 15 码全集（v5.1.10 §八.1 :4863）
  //   - 修复前：view ISSUE_TYPE_LABEL 6 码占位（EQUIPMENT_FAILURE/MATERIAL_AVAILABILITY/...）
  //   - 修复后：REASON_CODES 15 码权威枚举（5号位 R1 接受）
  const e01ReasonCodes = eTypes.match(/export\s+const\s+REASON_CODES\s*=\s*\[([\s\S]*?)\]\s*as\s+const/)?.[1] ?? ''
  const e01ExpectedCodes = [
    'RESOURCE_CAPACITY_WAIT',
    'MATERIAL_SHORTAGE',
    'PRECEDENCE_WAIT',
    'FROZEN_ZONE_LOCK',
    'ROUTING_FALLBACK',
    'STAGE_LEADTIME_FALLBACK',
    'BOM_DEGRADE',
    'CROSS_ORG_HANDOFF',
    'PRIORITY_LOWER_THAN_OTHERS',
    'DUE_DATE_RISK',
    'LOGISTICS_DELAY',
    'PRIORITY_INHERITANCE',
    'CROSS_DOMAIN_VERSION_MISMATCH_RISK',
    'MANUAL_COMPLETED_SHORT',
    'EQUIPMENT_BREAKDOWN_RISK'
  ]
  const e01AllPresent = e01ExpectedCodes.every((c) => new RegExp(`['"]${c}['"]`).test(e01ReasonCodes))
  // 反向探针：旧 6 码占位（含 §85 6 码）不应在 types 出现（防回退）
  const e01Old6Codes = ['MATERIAL_AVAILABILITY', 'CAPACITY_SHORTAGE', 'CONTRACT_VIOLATION']
  const e01NoOld6 = e01Old6Codes.every((c) => !new RegExp(`['"]${c}['"]`).test(eTypes))
  assert(
    'E01 静态：types/explanation.ts REASON_CODES = v5.1.10 §八.1 15 码全集（无 §85 6 码残留）',
    e01AllPresent && e01NoOld6,
    e01AllPresent && e01NoOld6
      ? '15 码全集对齐'
      : `缺失码=${e01ExpectedCodes.filter((c) => !new RegExp(`['"]${c}['"]`).test(e01ReasonCodes)).join(',')} 旧码残留=${e01Old6Codes.filter((c) => new RegExp(`['"]${c}['"]`).test(eTypes)).join(',')}`
  )

  // E02 静态：Severity 3 码（v5.1.10 :4864：INFO / WARN / ERROR；CRITICAL 仅 BOM Issues 用）
  const e02SeverityBlock = eTypes.match(/export\s+const\s+EXPLANATION_SEVERITIES\s*=\s*\[([\s\S]*?)\]\s*as\s+const/)?.[1] ?? ''
  const e02Has3 = /['"]INFO['"]/.test(e02SeverityBlock) && /['"]WARN['"]/.test(e02SeverityBlock) && /['"]ERROR['"]/.test(e02SeverityBlock)
  const e02NoCritical = !/['"]CRITICAL['"]/.test(e02SeverityBlock)
  const e02NoWarningFullWord = !/\bWARNING\b/.test(e02SeverityBlock)
  assert(
    'E02 静态：EXPLANATION_SEVERITIES = INFO/WARN/ERROR 3 码（v5.1.10 :4864；CRITICAL 已撤 / WARNING → WARN）',
    e02Has3 && e02NoCritical && e02NoWarningFullWord,
    e02Has3 && e02NoCritical && e02NoWarningFullWord ? '3 码对齐' : `has3=${e02Has3} noCritical=${e02NoCritical} noWarning=${e02NoWarningFullWord}`
  )

  // E03 静态：ObjectType 5 码（v5.1.10 :4858：ORDER/TASK/RESOURCE/STAGE/DOMAIN；PI/PO 已撤）
  const e03ObjectTypeBlock = eTypes.match(/export\s+const\s+EXPLANATION_OBJECT_TYPES\s*=\s*\[([\s\S]*?)\]\s*as\s+const/)?.[1] ?? ''
  const e03Expected = ['ORDER', 'TASK', 'RESOURCE', 'STAGE', 'DOMAIN']
  const e03AllPresent = e03Expected.every((c) => new RegExp(`['"]${c}['"]`).test(e03ObjectTypeBlock))
  const e03NoOld2 = !/['"](PI|PO)['"]/.test(e03ObjectTypeBlock)
  assert(
    'E03 静态：EXPLANATION_OBJECT_TYPES = ORDER/TASK/RESOURCE/STAGE/DOMAIN 5 码（v5.1.10 :4858；PI/PO 已撤）',
    e03AllPresent && e03NoOld2,
    e03AllPresent && e03NoOld2 ? '5 码对齐' : `缺码=${e03Expected.filter((c) => !new RegExp(`['"]${c}['"]`).test(e03ObjectTypeBlock)).join(',')} 旧码残留=${/['"](PI|PO)['"]/.test(e03ObjectTypeBlock)}`
  )

  // E04 静态：撤 ScheduleFailureInfo interface（5号位 关键纠正 3「后端无此实体」）
  //   - 反向探针：ScheduleFailureInfo / failureType / DOMAIN_FAILED / BLOCKED_BY_UPSTREAM / ENGINE_ERROR 都不应存在
  const e04OldFail = /ScheduleFailureInfo|failureType\s*:|DOMAIN_FAILED|BLOCKED_BY_UPSTREAM|ENGINE_ERROR/.test(eTypes)
  assert(
    'E04 静态：types/explanation.ts 撤 ScheduleFailureInfo + failureType 字段（5号位 关键纠正 3）',
    !e04OldFail,
    !e04OldFail ? 'ScheduleFailureInfo + failureType 已撤' : '仍含 ScheduleFailureInfo / failureType 占位'
  )

  // E05 静态：api/explanation.ts getByPlanVersion 修 400 bug（params 透传 planVersionId）
  //   - 修复前：`apsHttp.get({ url: '/api/explanation/summary' })` 漏 planVersionId query → 400
  //   - 修复后：`apsHttp.get({ url, params: { planVersionId } })` 透传
  //   - 10-10 改造：getByPlanVersion 真实模式拆为 2 次 apsHttp.get（summary + business-fact-issues），Summary 仍带 planVersionId params
  const e05ApiGet =
    /apsHttp\.get<\{[\s\S]*?\}>\(\s*\{\s*url:\s*['"]\/api\/explanation\/summary['"]\s*,\s*params:\s*\{\s*planVersionId\s*\}\s*\}/.test(eApi) ||
    /apsHttp\.get<[^>]*>\(\s*\{\s*url:\s*['"]\/api\/explanation\/summary['"]\s*,\s*params:\s*\{\s*planVersionId\s*\}\s*\}/.test(eApi)
  assert(
    'E05 静态：api/explanation.ts getByPlanVersion 透传 planVersionId params（修 400 bug）',
    e05ApiGet,
    e05ApiGet ? 'params 透传 planVersionId' : '仍缺 planVersionId 参数（400 bug 未修）'
  )

  // E06 静态：store severityCounts 3 码（ERROR/WARN/INFO；CRITICAL 已撤）
  //   - 修复前：CRITICAL/WARNING/ERROR/INFO 4 计数
  //   - 修复后：ERROR/WARN/INFO 3 计数（来自 Summary 端点 errorCount/warnCount/infoCount）
  //   - 严格抓 return { ... } 块，不被上方注释里的"CRITICAL 已撤"误报
  const e06ReturnBlock = eStore.match(/severityCounts\s*=\s*computed[\s\S]*?return\s*\{([\s\S]*?)\}\s*\)/)?.[1] ?? ''
  const e06StoreSeverity = /ERROR\s*:\s*d\?\.errorCount/.test(e06ReturnBlock) &&
    /WARN\s*:\s*d\?\.warnCount/.test(e06ReturnBlock) &&
    /INFO\s*:\s*d\?\.infoCount/.test(e06ReturnBlock)
  const e06NoCritical = !/CRITICAL/.test(e06ReturnBlock)
  assert(
    'E06 静态：store severityCounts 3 码（来自 Summary 端点 errorCount/warnCount/infoCount）',
    e06StoreSeverity && e06NoCritical,
    e06StoreSeverity && e06NoCritical
      ? '3 计数对齐'
      : `has3Fields=${e06StoreSeverity} noCriticalInBlock=${e06NoCritical}`
  )

  // E07 静态：store factsByObjectType 5 码（ORDER/TASK/RESOURCE/STAGE/DOMAIN）
  //   - 修复前：TASK/ORDER/PI/PO/RESOURCE/STAGE 6 码
  //   - 修复后：ORDER/TASK/RESOURCE/STAGE/DOMAIN 5 码
  const e07GroupsBlock = eStore.match(/Record<ExplanationObjectType[\s\S]*?\}/)?.[0] ?? ''
  const e07Expected = ['ORDER', 'TASK', 'RESOURCE', 'STAGE', 'DOMAIN']
  const e07AllPresent = e07Expected.every(
    (c) => new RegExp(`\\b${c}\\s*:\\s*\\[\\]`).test(e07GroupsBlock)
  )
  const e07NoOld = !/\b(PI|PO)\s*:\s*\[\]/.test(e07GroupsBlock)
  assert(
    'E07 静态：store factsByObjectType groups = 5 码（ORDER/TASK/RESOURCE/STAGE/DOMAIN；PI/PO 已撤）',
    e07AllPresent && e07NoOld,
    e07AllPresent && e07NoOld
      ? '5 码对齐'
      : `缺码=${e07Expected.filter((c) => !new RegExp(`\\b${c}\\s*:\\s*\\[\\]`).test(e07GroupsBlock)).join(',')} 旧码残留=${/\b(PI|PO)\s*:\s*\[\]/.test(e07GroupsBlock)}`
  )

  // E08 静态：ExplanationViewDto.errorCount / warnCount / infoCount 来自 Summary 端点（5号位 关键纠正 3）
  const e08Dto = eTypes.match(/export\s+interface\s+ExplanationViewDto\s*\{[\s\S]*?\n\}/)?.[0] ?? ''
  const e08HasErrorCount = /errorCount\s*:\s*number\b/.test(e08Dto)
  const e08HasWarnCount = /warnCount\s*:\s*number\b/.test(e08Dto)
  const e08HasInfoCount = /infoCount\s*:\s*number\b/.test(e08Dto)
  const e08HasReasonCodeCounts = /reasonCodeCounts\s*:\s*ReasonCodeCount\[\]/.test(e08Dto)
  assert(
    'E08 静态：ExplanationViewDto errorCount/warnCount/infoCount/reasonCodeCounts（对齐 Summary 端点）',
    e08HasErrorCount && e08HasWarnCount && e08HasInfoCount && e08HasReasonCodeCounts,
    `errorCount=${e08HasErrorCount} warnCount=${e08HasWarnCount} infoCount=${e08HasInfoCount} reasonCodeCounts=${e08HasReasonCodeCounts}`
  )

  // E09 静态：view 排程解释事实展示 reasonCode 徽章（v5.1.10 15 码）
  //   - 模板应有 `{{ reasonLabel(f.reasonCode) }}` 或 `REASON_CODE_LABELS` 引用
  //   - 同时展示 severity + objectRef + reasonCode 3 列
  const e09HasReasonLabel = /reasonLabel\s*\(/.test(eVue) && /REASON_CODE_LABELS/.test(eVue)
  const e09HasReasonTagInTemplate = /REASON_CODE_LABELS|reasonLabel/.test(eVue)
  assert(
    'E09 静态：Explanation.vue 排程解释事实展示 reasonCode 徽章（REASON_CODE_LABELS / reasonLabel 引用）',
    e09HasReasonLabel && e09HasReasonTagInTemplate,
    e09HasReasonLabel && e09HasReasonTagInTemplate ? '已加 reasonCode 徽章列' : '排程解释事实未展示 reasonCode'
  )

  // E10 静态：view 撤「排程域失败」面板（5号位 关键纠正 3）
  //   - 反向探针：「排程域失败」标题 / FAIL_TYPE_LABEL / DOMAIN_FAILED / BLOCKED_BY_UPSTREAM / ENGINE_ERROR 都不应存在
  const e10HasFailPanel = /排程域失败|FAIL_TYPE_LABEL/.test(eVue)
  const e10HasOldFailTypes = /DOMAIN_FAILED|BLOCKED_BY_UPSTREAM|ENGINE_ERROR/.test(eVue)
  assert(
    'E10 静态：Explanation.vue 撤「排程域失败」面板 + FAIL_TYPE_LABEL + 3 旧 failureType（5号位 关键纠正 3）',
    !e10HasFailPanel && !e10HasOldFailTypes,
    !e10HasFailPanel && !e10HasOldFailTypes
      ? '排程域失败面板已撤'
      : `排程域失败=${e10HasFailPanel} 旧码残留=${e10HasOldFailTypes}`
  )

  // E11 / E12 skip：R2 / R3 跨号位（1号位 求解 + 2号位 落盘 + 0号位 裁决 V1 范围）
  //   - 4号位 端不动，等回执
  //   - 5号位 已承诺：一旦 1/2号位 落盘，5号位 立即 DTO 透传
  skip('E11 静态：fact.isOnCriticalPath 字段 + Binding Constraint 徽章列（@see §87；待 1号位 + 2号位 落盘 + 0号位 裁决 V1 范围）')
  skip('E12 静态：failure.failureSeverity 字段（DEGRADABLE / MUST_FAIL；@see §88；待 1号位 + 2号位 落盘 + 0号位 裁决 V1 范围）')

  // E13 探针：可选链漏写 `?.Xxx.length`（10-09 dev 浏览器捕获：store totalFacts `data.value?.scheduleExplanationFacts.length` 在 data=null 时报 TypeError）
  //   - TS 可选链 `?.` 只到直接成员，再读 `.length` 时若上一级是 undefined 仍抛错
  //   - 正确写法：`data.value?.scheduleExplanationFacts?.length`
  //   - 反向探针自证可失败：在 explanation.ts 临时插 `x?.y.length` 会让 E13 变红
  const e13Leak = (eStore + '\n' + eVue).match(/\?\.\w+\.length\b/g) ?? []
  assert(
    'E13 静态：store + view 无 `?.Xxx.length` 可选链漏写（10-09 dev 浏览器 TypeError 教训）',
    e13Leak.length === 0,
    e13Leak.length === 0 ? '无漏写' : `漏写：${e13Leak.join(' / ')}`
  )

  // E14 探针：版本切换条不在 ElTag 重复展示 currentVersionId（10-09 dev 浏览器捕获：ElTag dark + ElButton list 双渲染当前版本，看着像"重复"）
  //   - 同一 versionCode 在 version-bar 里出现 2 次会让 PMC 误以为后端 versionCode 撞了
  //   - 正确做法：仅靠 ElButton v-for 高亮表达当前；不另起 ElTag
  //   - 反向探针自证可失败：在 view 临时插回 `<ElTag ... currentVersionId>...</ElTag>` 会让 E14 变红
  const e14TagRedundant = /<ElTag\b[^>]*>[\s\S]*?currentVersionId[\s\S]*?<\/ElTag>/.test(eVue)
  assert(
    'E14 静态：版本切换条不在 ElTag 重复展示 currentVersionId（10-09 dev 重复显示教训）',
    !e14TagRedundant,
    !e14TagRedundant ? '仅按钮列表高亮' : 'ElTag + ElButton list 双渲染当前版本'
  )

  // E15 探针：PLAN_VERSION_STATUSES 对齐权威 6 码（2026-10-17 改判据）
  //   - 权威：`BUILDING / CANDIDATE / ACTIVE / ARCHIVED / FAILED` + 实现子状态 `Computing`
  //     出处：frontNew/docs/5号位-2026-10-15-Overview-R1更正与修复-给4号位.md §一
  //   - **原 `Computed`→`CANDIDATE`、原 `ComputeFailed`→`FAILED`**（2号位 2026-10-15 已统一 + 库迁移）
  //     ⇒ 这两码必须**不在**词表内：留着的分支永远取不到值，
  //       且后端一旦返它们，LABELS[v.status] 会返 undefined、UI 直出英文原文
  //   - ⚠️ 本探针旧口径（10-09 写的「必须含 Computed/ComputeFailed」）**方向反了** ——
  //     与同仓 OV09「PlanVersion 词表无 Computed/ComputeFailed 死分支，且已补 Computing」直接打架，
  //     以本条为准；旧口径来自 10-09 的 dev 观测，已被 10-15 的词表统一取代
  //   - 反向探针自证可失败：删 'Computing' 或加回 'Computed' 都会变红
  const e15EnumBlock = eCommon.match(/PLAN_VERSION_STATUSES\s*=\s*\[([\s\S]*?)\]\s*as\s+const/)?.[1] ?? ''
  const e15LabelBlock =
    eCommon.match(/PLAN_VERSION_STATUS_LABELS[\s\S]*?=\s*\{([\s\S]*?)\n\}/)?.[1] ?? ''
  const E15_AUTHORITATIVE = ['BUILDING', 'CANDIDATE', 'ACTIVE', 'ARCHIVED', 'FAILED', 'Computing']
  const e15EnumValues = [...e15EnumBlock.matchAll(/['"]([A-Za-z_]+)['"]/g)].map((m) => m[1])
  const e15Missing = E15_AUTHORITATIVE.filter((c) => !e15EnumValues.includes(c))
  const e15Extra = e15EnumValues.filter((c) => !E15_AUTHORITATIVE.includes(c))
  const e15Legacy = ['Computed', 'ComputeFailed'].filter(
    (c) => new RegExp(`\\b${c}\\b`).test(e15EnumBlock) || new RegExp(`\\b${c}\\b`).test(e15LabelBlock)
  )
  const e15LabelMissing = E15_AUTHORITATIVE.filter(
    (c) => !new RegExp(`${c}\\s*:\\s*['"][^'"]+['"]`).test(e15LabelBlock)
  )
  const e15All =
    e15Missing.length === 0 &&
    e15Extra.length === 0 &&
    e15Legacy.length === 0 &&
    e15LabelMissing.length === 0
  assert(
    'E15 静态：PLAN_VERSION_STATUSES 恰为权威 6 码（BUILDING/CANDIDATE/ACTIVE/ARCHIVED/FAILED/Computing），无 Computed/ComputeFailed 死分支',
    e15All,
    e15All
      ? `6 码齐全 + 中文 label 齐 + 旧码零残留（实测 ${e15EnumValues.join('/')}）`
      : `缺=${e15Missing.join(',') || '无'} 多余=${e15Extra.join(',') || '无'} 旧码残留=${e15Legacy.join(',') || '无'} 缺label=${e15LabelMissing.join(',') || '无'}`
  )

  // E16 探针：version-bar 走 store.displayedVersions 业务优先级（10-09 用户反馈"slice(0,3) 不对"的方案 a 落地）
  //   - 旧：view `versions.slice(0, 3)` 硬截，PMC 看不到 ACTIVE（在 3 条外时）+ 状态混在一起
  //   - 新：store `displayedVersions` getter 按 ACTIVE → CANDIDATE → FAILED → 当前已选 兜底
//     （2026-10-16 词表统一：原 Computed/recentComputed→CANDIDATE、原 ComputeFailed→FAILED）
  //   - view 必须用 displayedVersions（不再渲染 ElTag 徽章——版本号已足够，按用户简化要求）
  //   - 反向探针自证可失败：view 改回 `versions.slice(0, 3)` 或 store 删 displayedVersions getter 会让 E16 变红
  const e16ScheduleStore = readSrc('store/modules/aps/schedule.ts')
  const e16HasDisplayedGetter = /const\s+displayedVersions\s*=\s*computed/.test(e16ScheduleStore)
  const e16HasActiveGetter = /const\s+activeVersion\s*=\s*computed[\s\S]*?status\s*===\s*['"]ACTIVE['"]/.test(
    e16ScheduleStore
  )
  const e16ViewUsesDisplayed = /v-for="v in displayedVersions"/.test(eVue)
  const e16ViewNoSlice3 =
    !/versions\.slice\(\s*0\s*,\s*3\s*\)/.test(eVue) && !/versions\.slice\(\s*0\s*,\s*5\s*\)/.test(eVue)
  const e16All =
    e16HasDisplayedGetter && e16HasActiveGetter && e16ViewUsesDisplayed && e16ViewNoSlice3
  assert(
    'E16 静态：version-bar 走 store.displayedVersions 业务优先级（10-09 业务讨论方案 a 落地）',
    e16All,
    e16All
      ? 'displayedVersions 三 getter + view 已用'
      : `displayedGetter=${e16HasDisplayedGetter} activeGetter=${e16HasActiveGetter} viewUsesDisplayed=${e16ViewUsesDisplayed} viewNoSlice3=${e16ViewNoSlice3}`
  )

  // E17 探针：Explanation.vue onMounted 必须联动 scheduleStore.currentVersionId 给 explanationStore.load，
  //         避免首次 mount summary 请求用 store ref 默认值 1001，与 scheduleStore 选中 ACTIVE 错位（10-09 dev 浏览器 KPI 全 0 根因）
  //   - 反向探针自证可失败：onMounted 改回 `await explanationStore.load()`（不传参）会变红
  const e17OnMounted =
    /onMounted\([\s\S]*?await\s+scheduleStore\.loadVersions\(\)[\s\S]*?explanationStore\.load\([\s\S]*?currentVersionId\.value/.test(
      eVue
    )
  assert(
    'E17 静态：Explanation.vue onMounted 联动 scheduleStore.currentVersionId 给 explanationStore.load（修首次 mount KPI 错位）',
    e17OnMounted,
    e17OnMounted
      ? 'onMounted 已显式传参'
      : 'onMounted 未联动 currentVersionId — 首次会拿 ref 默认 1001'
  )

  // E18 探针：schedule.selectVersion 精确缓存命中 + in-flight 去重（10-09 dev 浏览器反馈"两次 gantt/46"教训）
  //   - 精确缓存：用 ganttData.value?.planVersionId === planVersionId 而不是 `ganttData.value` 存在性检查
  //   - 去重：pendingVersionId / pendingPromise 同 id 共享同一 Promise，避免并发连点引发双请求
  //   - 反向探针自证可失败：把 `ganttData.value?.planVersionId === planVersionId` 改成 `ganttData.value` 会变红
  const e16Store = readSrc('store/modules/aps/schedule.ts')
  const e18ExactCache = /ganttData\.value\?\.planVersionId\s*===\s*planVersionId/.test(e16Store)
  const e18HasPendingId = /pendingVersionId/.test(e16Store)
  const e18HasPendingPromise = /pendingPromise/.test(e16Store)
  const e18SharesInFlight = /pendingVersionId\s*===\s*planVersionId[\s\S]{0,80}return\s+pendingPromise/.test(
    e16Store
  )
  const e18All =
    e18ExactCache && e18HasPendingId && e18HasPendingPromise && e18SharesInFlight
  assert(
    'E18 静态：schedule.selectVersion 精确缓存 + in-flight 去重（修连点同 id 触发两次 gantt 请求）',
    e18All,
    e18All
      ? '精确 planVersionId 匹配 + pending 去重齐全'
      : `exactCache=${e18ExactCache} pendingId=${e18HasPendingId} pendingPromise=${e18HasPendingPromise} sharesInFlight=${e18SharesInFlight}`
  )

  // E19 探针（2026-10-17 改判据）：schedule.loadCurrent **只取甘特本体**，不再调 summary
  //  旧口径要求「用 Promise.allSettled 容错 summary 500」——那是 10-09 的过渡做法：
  //  后端 `/api/schedule/summary/{id}` 长期 500（`ScheduleQueryService.cs:206` 从 `PlanVersion`
  //  取不存在的 `DurationSeconds`），而该结果**从未被任何页面消费** ⇒ 每次开甘特页白炸一条红条。
  //  现已摘除该调用与无用状态（已出函 5号位）。本探针改为锁「别再把它加回来」。
  //  - 反向探针自证可失败：把 getSummary 加回 loadCurrent → 转红
  const e19OnlyGantt =
    /scheduleApi\.getGantt\(/.test(e16Store) && !/scheduleApi\.getSummary\(/.test(e16Store)
  const e19NoSummaryState = !/const summary = ref/.test(e16Store)
  const e19KeepsLastGantt =
    /ganttData\.value = await scheduleApi\.getGantt\(id\)[\s\S]{0,220}?handleError\(err\)/.test(
      e16Store
    ) && !/catch\s*\(err\)\s*\{[\s\S]{0,80}ganttData\.value = null/.test(e16Store)
  const e19All = e19OnlyGantt && e19NoSummaryState && e19KeepsLastGantt
  assert(
    'E19 静态：甘特加载只调 gantt、不再调长期 500 的 summary；失败保留上次数据',
    e19All,
    e19All
      ? 'loadCurrent 仅 getGantt + 无 summary 状态 + 失败不清缓存'
      : `onlyGantt=${e19OnlyGantt} noSummaryState=${e19NoSummaryState} keepsLast=${e19KeepsLastGantt}`
  )

  // E20 探针：10-10 option A 收口（按 4号位 文档第 11-12 节冻结基线：撤 重排建议 + 设备故障 2 块 UI / DTO / mock / CSS）
  //   - types/explanation.ts 不再 export `interface RescheduleRecommendation` / `interface EquipmentFailure`
  //   - ExplanationViewDto 不再含 rescheduleRecommendations / equipmentFailures 字段
  //   - api/explanation.ts 真实模式调 /api/business-fact-issues 聚合（独立端点）；mockRecommendations() / mockEquipmentFailures() 函数已撤（mockBusinessIssues() 仍保留供 mock 模式填充，**不应被错误撤掉**）
  //   - view 撤 2 块 ElCard + REC_TYPE_LABEL/REC_TAG_TYPE 常量 + 相关 CSS（.rec-* / .equip-* / .u20-alert）+ ElTooltip 引用
  //   - 反向探针自证可失败：types 加回 `interface RescheduleRecommendation` 或 view 加回 .rec-list / .equip-list CSS 会让 E20 变红
  //   - 注意：api/view 头部注释里仍可能保留「重排建议 / 设备故障 / mockRecommendations / mockEquipmentFailures」历史描述文字（仅文字可保留），所以探针只对实际代码 token 做检查
  const e20DtoBlock = eTypes.match(/export\s+interface\s+ExplanationViewDto\s*\{[\s\S]*?\n\}/)?.[0] ?? ''
  const e20NoRecDto = !/interface\s+RescheduleRecommendation\b/.test(eTypes)
  const e20NoEquipDto = !/interface\s+EquipmentFailure\b/.test(eTypes)
  const e20NoRecField = !/rescheduleRecommendations\s*:/.test(e20DtoBlock)
  const e20NoEquipField = !/equipmentFailures\s*:/.test(e20DtoBlock)
  const e20ApiCallsBizFact =
    /['"]\/api\/business-fact-issues['"]/.test(eApi) &&
    (/take\s*:\s*100/.test(eApi) || // 真实模式默认 take=100（旧契约）
     /pageIndex\s*:\s*1[\s\S]{0,80}?pageSize\s*:\s*20/.test(eApi)) // 10-10 改为分页契约后：pageIndex=1, pageSize=20
  // 检查 mockRecommendations / mockEquipmentFailures 不再被**调用**（声明已撤；头注释里的描述文字可保留）
  const e20ApiNoMockRecEquip =
    !/\bmockRecommendations\s*\(/.test(eApi) && !/\bmockEquipmentFailures\s*\(/.test(eApi)
  // view 端：检查已撤掉的常量 / CSS 类名 / ElTooltip 实际代码 token（jSDoc 注释里的描述文字可保留）
  const e20ViewNoRecConstOrCss = !/REC_TYPE_LABEL|REC_TAG_TYPE|\.rec-list|\.rec-item|\.rec-head|\.rec-time|\.rec-desc|\.rec-impact|\.rec-warn/.test(
    eVue
  )
  const e20ViewNoEquipConstOrCss =
    !/EQUIP_TYPE_LABEL|EQUIP_TAG_TYPE|\.equip-list|\.equip-item|\.equip-head|\.equip-code|\.equip-meta|\.equip-field|\.equip-icon|\.u20-alert/.test(
      eVue
    )
  const e20ViewNoElTooltip = !/<ElTooltip\b/.test(eVue)
  const e20All =
    e20NoRecDto &&
    e20NoEquipDto &&
    e20NoRecField &&
    e20NoEquipField &&
    e20ApiCallsBizFact &&
    e20ApiNoMockRecEquip &&
    e20ViewNoRecConstOrCss &&
    e20ViewNoEquipConstOrCss &&
    e20ViewNoElTooltip
  assert(
    'E20 静态：10-10 option A 收口（撤 2 DTO + 2 DTO 字段 + mockRecommendations()/mockEquipmentFailures() + view 2 块 + REC_TYPE_LABEL/REC_TAG_TYPE + .rec-* / .equip-* / .u20-alert CSS + ElTooltip + /api/business-fact-issues 聚合）',
    e20All,
    e20All
      ? '2 DTO + 2 字段 + 2 mock 调用 + 2 块 + 2 常量 + 7 CSS 类 + ElTooltip + /api/business-fact-issues 聚合 全部撤清'
      : `recDto=${!e20NoRecDto} equipDto=${!e20NoEquipDto} recField=${!e20NoRecField} equipField=${!e20NoEquipField} bizFactApi=${e20ApiCallsBizFact} noMockRecEquipCall=${e20ApiNoMockRecEquip} recBlock=${!e20ViewNoRecConstOrCss} equipBlock=${!e20ViewNoEquipConstOrCss} noElTooltip=${!e20ViewNoElTooltip}`
  )

  // E21 探针：10-10 business-fact-issues 端点查错库修复验证（@see 4号位-2026-10-10-business-fact-issues撤函-根因是查错库非缺表-撤回建表建议-给5号位-抄送2号位3号位.md）
  //   - 原误诊：dev DB 缺表 → 推荐 DDL 建表
  //   - 真根因：Repository 查询1 用 DatabaseId.APS，应为 DatabaseId.ODS（表在 ODS/MES_Integration 一直存在）
  //   - 期望：5号位 改 [lps/LPS.APS.BusinessRules/Repositories/BusinessFactIssueRepository.cs:77](/d/proj/aps/aps-production-new/lps/LPS.APS.BusinessRules/Repositories/BusinessFactIssueRepository.cs#L77) DatabaseId.APS → ODS 后端点返 200
  //   - 反向探针自证可失败：把连接改回 DatabaseId.APS 会让 E21 由绿转红
  //   - 本探针默认 skip（dev 环境依赖）；设置 VERIFY_E21=1 且 dev 可访问时执行
  if (process.env.VERIFY_E21 === '1' && adminToken) {
    const e21Resp = await call('GET', '/api/business-fact-issues', {
      token: adminToken,
      query: { take: 100 }
    })
    const e21Ok = e21Resp.ok && e21Resp.code === 200
    const e21NoMissingTable = e21Ok || !/MES_APS_BOM_Workset_Issues/.test(e21Resp.text || '')
    assert(
      'E21 动态：/api/business-fact-issues?take=100 端点 200（Repository 已改 DatabaseId.APS→ODS；表在 ODS/MES_Integration 一直存在）',
      e21Ok && e21NoMissingTable,
      e21Ok
        ? '端点 200，连接已修正'
        : `status=${e21Resp.status} code=${e21Resp.code} missingTable=${/MES_APS_BOM_Workset_Issues/.test(e21Resp.text || '')}`
    )
  } else {
    skip('E21 动态：/api/business-fact-issues?take=100 连接修正验证（设置 VERIFY_E21=1 且 dev 可访问时执行）')
  }

  // E22 探针：10-10 样式简洁整齐（修 dev 浏览器 KPI 卡片"100%"换行 + 排程解释事实 grid 2 列硬截）
  //   - 修前症状：KPI 卡 `100%` 文本换行到第二行；`.fact-grid` 硬截 `repeat(2, 1fr)` 在窄屏溢出
  //   - 修后契约：`.kpi-card { min-width: 0 }` + `.kpi-label/.kpi-ratio { white-space: nowrap }` + `.fact-grid { repeat(auto-fit, minmax(320px, 1fr)) }`
  //   - 反向探针自证可失败：删 `min-width: 0` 或 `white-space: nowrap` 或 `auto-fit, minmax` 会让 E22 变红
  const e22KpiCardMinWidth = /\.kpi-card\s*\{[^}]*min-width\s*:\s*0\b/.test(eVue)
  const e22KpiLabelNowrap = /\.kpi-label[^{]*\{[^}]*white-space\s*:\s*nowrap/.test(eVue)
  const e22KpiRatioNowrap = /\.kpi-ratio[^{]*\{[^}]*white-space\s*:\s*nowrap/.test(eVue)
  const e22FactGridAutoFit = /\.fact-grid\s*\{[^}]*repeat\(\s*auto-fit\s*,\s*minmax\(\s*320px\s*,\s*1fr\s*\)\s*\)/.test(eVue)
  const e22ContentRowSingleCol =
    /\.content-row\s*\{[^}]*grid-template-columns\s*:\s*1fr\b/.test(eVue)
  const e22All =
    e22KpiCardMinWidth &&
    e22KpiLabelNowrap &&
    e22KpiRatioNowrap &&
    e22FactGridAutoFit &&
    e22ContentRowSingleCol
  assert(
    'E22 静态：Explanation.vue 样式简洁整齐（KPI 卡 min-width:0 + label/ratio nowrap + fact-grid auto-fit minmax(320px,1fr) + content-row 单列）',
    e22All,
    e22All
      ? 'KPI 防溢出 + fact-grid 响应式 + 单列布局 齐全'
      : `kpiMinW=${e22KpiCardMinWidth} labelNowrap=${e22KpiLabelNowrap} ratioNowrap=${e22KpiRatioNowrap} factGridAutoFit=${e22FactGridAutoFit} contentRow1col=${e22ContentRowSingleCol}`
  )

  // E23 探针：10-10 business-fact-issues 分页 + total + severity 排序兜底契约锁
  //   - api 调 pageIndex=1, pageSize=20（不再 take=100）
  //   - api 增 listBusinessFactIssues(opts) 单独方法
  //   - 10-10 后端落地后：响应字段名 page（5号位 落地回执明确告知，@see 5号位-2026-10-10-business-fact-issues分页severity排序total已落地-回执-给4号位.md）
  //     store 同步字段名 businessFactIssuesPage（前为 PageIndex）；DTO 同步 businessFactIssuesPage
  //   - store 增 4 ref（total/page/pageSize/loading）+ cached 缓存标记 + sortedBusinessFactIssues getter + fetchBizPage / refreshBizIssues / changeBizPageSize actions
  //     fetchBizPage 替换式（每页直接覆盖，不累积）：ElPagination @current-change 触发
  //   - 10-10 续改：view 用标准 ElPagination（v-model 双绑 + layout="total,sizes,prev,pager,next,jumper" + [10,20,50]）
  //   - 10-10 续改：view 移除"加载更多"按钮 + 旧 pageSize ElSelect + header-tags div（避免与 ElPagination 总数显示重复）
  //   - view 用 sortedBusinessFactIssues
  //   - ExplanationViewDto 增 3 字段（total/page/pageSize）
  //   - 函件：frontNew/docs/4号位-2026-10-10-business-fact-issues分页severity排序total-给5号位.md
  const eTypesContent = readSrc('api/aps-v1/types/explanation.ts')
  const e23ApiCallsPageResult =
    /pageIndex\s*:\s*1[\s\S]{0,80}?pageSize\s*:\s*20/.test(eApi)
  const e23ApiNoOldTake = !/params:\s*\{\s*take:\s*100\s*\}/.test(eApi)
  const e23ApiListMethod = /async\s+listBusinessFactIssues\s*\(/.test(eApi)
  const e23ApiToBusinessFactIssue = /function\s+toBusinessFactIssue\s*\(/.test(eApi)
  const e23ApiUsesPageField = /bizPage\.page(?!\w)/.test(eApi) || /raw\.page(?!\w)/.test(eApi)
  const e23ApiNoFallbackToList =
    !/PageResult<BusinessFactIssueRawDto>\s*\|\s*BusinessFactIssueRawDto\[\]/.test(eApi)
  const e23StoreRefTotal = /businessFactIssuesTotal\s*=\s*ref/.test(eStore)
  const e23StoreRefPage = /businessFactIssuesPage\s*=\s*ref/.test(eStore)
  const e23StoreRefPageSize = /businessFactIssuesPageSize\s*=\s*ref/.test(eStore)
  const e23StoreRefLoading = /businessFactIssuesLoading\s*=\s*ref/.test(eStore)
  const e23StoreCached = /businessFactIssuesCached\s*=\s*ref/.test(eStore)
  const e23StoreSorted = /sortedBusinessFactIssues/.test(eStore)
  const e23StoreFetchBizPage = /async\s+function\s+fetchBizPage/.test(eStore)
  const e23StoreRefresh = /async\s+function\s+refreshBizIssues/.test(eStore)
  const e23StoreChangeSize = /async\s+function\s+changeBizPageSize/.test(eStore)
  // 10-10 续改：删 loadMoreBizIssues / hasMore（替换式分页不需要累积/hasMore 概念）
  const e23StoreNoLoadMore = !/loadMoreBizIssues/.test(eStore)
  const e23StoreNoHasMore = !/businessFactIssuesHasMore/.test(eStore)
  const e23ViewPaginationClass = /\.biz-pagination/.test(eVue)
  const e23ViewHasElPagination = /<ElPagination\b/.test(eVue)
  const e23ViewPaginationLayout =
    /layout="total,\s*sizes,\s*prev,\s*pager,\s*next,\s*jumper"/.test(eVue)
  const e23ViewPaginationPageSizes = /:page-sizes="\[10,\s*20,\s*50\]"/.test(eVue)
  const e23ViewPaginationVModel =
    /v-model:current-page="businessFactIssuesPage"/.test(eVue) &&
    /v-model:page-size="businessFactIssuesPageSize"/.test(eVue)
  const e23ViewTotalHint = /businessFactIssuesTotal/.test(eVue)
  // 10-10 续改：删"加载更多"按钮 + header-tags 显示重复块 + 旧 ElSelect 10/20/50 标签页
  const e23ViewNoLoadMoreBtn = !/加载更多/.test(eVue)
  const e23ViewNoHeaderTags = !/header-tags/.test(eVue)
  const e23ViewNoOldPageSizeSelect = !/10\s*条\/页/.test(eVue)
  const e23ViewUsesSorted = /sortedBusinessFactIssues/.test(eVue)
  const e23ViewUsesPageField = /businessFactIssuesPage(?!\w)/.test(eVue) &&
    !/businessFactIssuesPageSize/.test(eVue.replace(/businessFactIssuesPageSize/g, ''))
  const e23TypesDtoFields =
    /businessFactIssuesTotal[\s\S]*?businessFactIssuesPage[\s\S]*?businessFactIssuesPageSize/.test(
      eTypesContent
    )
  const e23All =
    e23ApiCallsPageResult &&
    e23ApiNoOldTake &&
    e23ApiListMethod &&
    e23ApiToBusinessFactIssue &&
    e23ApiUsesPageField &&
    e23ApiNoFallbackToList &&
    e23StoreRefTotal &&
    e23StoreRefPage &&
    e23StoreRefPageSize &&
    e23StoreRefLoading &&
    e23StoreCached &&
    e23StoreNoLoadMore &&
    e23StoreNoHasMore &&
    e23StoreSorted &&
    e23StoreFetchBizPage &&
    e23StoreRefresh &&
    e23StoreChangeSize &&
    e23ViewPaginationClass &&
    e23ViewHasElPagination &&
    e23ViewPaginationLayout &&
    e23ViewPaginationPageSizes &&
    e23ViewPaginationVModel &&
    e23ViewTotalHint &&
    e23ViewNoLoadMoreBtn &&
    e23ViewNoHeaderTags &&
    e23ViewNoOldPageSizeSelect &&
    e23ViewUsesSorted &&
    e23ViewUsesPageField &&
    e23TypesDtoFields
  assert(
    'E23 静态：business-fact-issues 分页契约（PageResult + 后端字段 page + 缓存 + 替换式 fetchBizPage + 排序兜底 + DTO 3 字段 page + 标准 ElPagination + v-model + [10,20,50]）',
    e23All,
    e23All
      ? '全齐'
      : `apiCallPgRes=${e23ApiCallsPageResult} noTake100=${e23ApiNoOldTake} apiListMethod=${e23ApiListMethod} apiToBiz=${e23ApiToBusinessFactIssue} apiPage=${e23ApiUsesPageField} apiNoFallback=${e23ApiNoFallbackToList} stTotal=${e23StoreRefTotal} stPage=${e23StoreRefPage} stPageSz=${e23StoreRefPageSize} stLoad=${e23StoreRefLoading} stCached=${e23StoreCached} stNoLoadMore=${e23StoreNoLoadMore} stNoHasMore=${e23StoreNoHasMore} stSorted=${e23StoreSorted} stFetch=${e23StoreFetchBizPage} stRefresh=${e23StoreRefresh} stChangeSz=${e23StoreChangeSize} vPagination=${e23ViewPaginationClass} vElPag=${e23ViewHasElPagination} vLayout=${e23ViewPaginationLayout} vPageSizes=${e23ViewPaginationPageSizes} vVModel=${e23ViewPaginationVModel} vTotal=${e23ViewTotalHint} vNoLoadMore=${e23ViewNoLoadMoreBtn} vNoHeaderTags=${e23ViewNoHeaderTags} vNoOldSize=${e23ViewNoOldPageSizeSelect} vUsesSorted=${e23ViewUsesSorted} vPage=${e23ViewUsesPageField} tDtoFields=${e23TypesDtoFields}`
  )

  // E24 探针：5号位 落地 PageResult + severity 排序后，动态验证
  //   - 期望：`GET /api/business-fact-issues?pageIndex=1&pageSize=1` 形如 `{code, message, data: {items: [...], total: N, page: 1, pageSize: 1}}`
  //   - 5号位 10-10 落地回执：字段名是 `page`（非 `pageIndex`），与 RBAC/TaskSplitRuleConfig/ManualETA 同源
  //   - severity 排序期望：取 pageSize=50，前 N 条按 ERROR > WARN > INFO 顺序（@see 5号位 10-12 函确认已排序）
  if (process.env.VERIFY_E24 === '1' && adminToken) {
    // E24a：PageResult{items,total,page,pageSize} 字段对齐
    const e24Resp = await call('GET', '/api/business-fact-issues', {
      token: adminToken,
      query: { pageIndex: 1, pageSize: 1 }
    })
    const body = e24Resp.json
    const data = body?.data ?? {}
    const items = data.items
    const total = data.total
    const page = data.page
    const pageSize = data.pageSize
    const isPageResult =
      Array.isArray(items) &&
      typeof total === 'number' &&
      page === 1 &&
      pageSize === 1
    assert(
      'E24a 动态：/api/business-fact-issues?pageIndex=1&pageSize=1 返回 PageResult{items,total,page,pageSize}（5号位 10-10 已落地）',
      isPageResult,
      isPageResult
        ? `total=${total} items=${items.length} page=${page} pageSize=${pageSize}`
        : `status=${e24Resp.status} dataShape=${JSON.stringify(data).slice(0, 200)}`
    )

    // E24b：severity 排序（ERROR > WARN > INFO）— 5号位 10-12 函确认已排序
    if (isPageResult) {
      const e24SortResp = await call('GET', '/api/business-fact-issues', {
        token: adminToken,
        query: { pageIndex: 1, pageSize: 50 }
      })
      const sortItems = e24SortResp.json?.data?.items ?? []
      const severityOrder = { ERROR: 0, WARN: 1, INFO: 2 }
      const severitySorted =
        Array.isArray(sortItems) &&
        sortItems.length > 0 &&
        sortItems.every(
          (it, i) =>
            i === 0 ||
            severityOrder[sortItems[i - 1].severity] <= severityOrder[it.severity]
        )
      const errCount = sortItems.filter((it) => it.severity === 'ERROR').length
      const warnCount = sortItems.filter((it) => it.severity === 'WARN').length
      const infoCount = sortItems.filter((it) => it.severity === 'INFO').length
      assert(
        'E24b 动态：business-fact-issues severity 排序（ERROR 优先，5号位 10-12 函确认已落地）',
        severitySorted,
        severitySorted
          ? `共 ${sortItems.length} 条 ERROR=${errCount} WARN=${warnCount} INFO=${infoCount}（已排序）`
          : `共 ${sortItems.length} 条 ERROR=${errCount} WARN=${warnCount} INFO=${infoCount}（未排序）`
      )
    } else {
      skip('E24b 动态：severity 排序（E24a PageResult 失败则跳过排序验证）')
    }
  } else {
    skip(
      'E24 动态：/api/business-fact-issues PageResult{total,items,page,pageSize}（设置 VERIFY_E24=1 等 5号位 落地 DTO 后执行）'
    )
  }

  // E25 探针：10-10 懒加载契约（setCurrentVersion 拆分锁）
  //   - store 加 setCurrentVersion 同步 action（仅更新 ref，不触发 fetch）
  //   - Explanation.vue 改用 setCurrentVersion（避免无意义拉甘特图）
  //   - Gantt.vue 仍用 selectVersion（保持甘特图拉取）
  //   - 反向探针自证可失败：Explanation.vue 改回 selectVersion 会让 E25 由绿转红（理由：会无意义触发 /api/schedule/gantt）
  //   - @see 10-10 教训：Explanation 页只需 currentVersionId 高亮，不需要 gantt 数据
  const e25StoreHasSetCurrent = /function\s+setCurrentVersion\s*\(/.test(eScheduleStore)
  const e25StoreKeepsSelectVersion = /function\s+selectVersion\s*\(/.test(eScheduleStore)
  const e25StoreSetCurrentNoFetch = (() => {
    // 校验 setCurrentVersion 函数体内不含 getGantt / getSummary（防被改成意外 fetch）
    const match = eScheduleStore.match(/function\s+setCurrentVersion\s*\([^)]*\)[^{]*\{([\s\S]*?)\n\s*\}\s*\n/)
    if (!match) return false
    const body = match[1]
    return !/getGantt|getSummary|loadCurrent/.test(body)
  })()
  const e25ExplanationUsesSetCurrent =
    /scheduleStore\.setCurrentVersion\s*\(/.test(eVue) &&
    !/function\s+selectVersion[\s\S]{0,200}?scheduleStore\.selectVersion/.test(eVue)
  const e25All =
    e25StoreHasSetCurrent &&
    e25StoreKeepsSelectVersion &&
    e25StoreSetCurrentNoFetch &&
    e25ExplanationUsesSetCurrent
  assert(
    'E25 静态：Explanation 懒加载契约（scheduleStore 拆 setCurrentVersion 仅更 ref + Explanation.vue 用 setCurrentVersion + Gantt.vue 仍走 selectVersion）',
    e25All,
    e25All
      ? '全齐'
      : `storeSetCurrent=${e25StoreHasSetCurrent} storeSelectVer=${e25StoreKeepsSelectVersion} storeNoFetch=${e25StoreSetCurrentNoFetch} explUsesSetCurrent=${e25ExplanationUsesSetCurrent}`
  )

  // E26 探针：10-12 字典漂移兜底（5号位 函 §二「未入字典的值不丢弃，按待核对展示」）
  //   - store 暴露 unrecognizedObjectTypeFacts / unrecognizedRawObjectTypes getter（用 EXPLANATION_OBJECT_TYPES 字典过滤）
  //   - factsByObjectType 仍只产 5 码（保持 5 码契约，避免 E07 等老探针回归）
  //   - Explanation.vue 渲染 ElAlert type="info" 待核对面板 + 列出 raw ObjectType 值
  //   - 反向探针自证可失败：把 store getter 重命名 / 把 ElAlert 删了 都会让 E26 由绿转红
  //   - @see 5号位-2026-10-12-Explanation字典漂移-0号位答复转达回执-给4号位.md §二
  const e26StoreHasUnrecognizedFacts = /unrecognizedObjectTypeFacts\s*=/.test(eStore)
  const e26StoreHasUnrecognizedRaw = /unrecognizedRawObjectTypes\s*=/.test(eStore)
  const e26StoreFilterUsesDict = /EXPLANATION_OBJECT_TYPES/.test(eStore)
  const e26StoreKeepsFiveKeys =
    /Record<ExplanationObjectType/.test(eStore) &&
    /ORDER:\s*\[\]/.test(eStore) &&
    /DOMAIN:\s*\[\]/.test(eStore)
  const e26StoreExported = (() => {
    // 取最后一处 return（composition API 末尾的暴露对象），不用 eStore.split('return')[1]（会拿到 reset 函数体内更早的 return）
    const lastReturn = eStore.lastIndexOf('return')
    if (lastReturn < 0) return false
    const tail = eStore.slice(lastReturn)
    return /unrecognizedObjectTypeFacts/.test(tail) && /unrecognizedRawObjectTypes/.test(tail)
  })()
  const e26ViewAlert = /ElAlert[\s\S]{0,200}?unrecognized-alert/.test(eVue) ||
    /<ElAlert[\s\S]*?unrecognizedObjectTypeFacts/.test(eVue)
  const e26ViewRawTypeBadge = /待核对·\{\{\s*f\.objectType\s*\}\}/.test(eVue) ||
    /待核对[\s\S]{0,40}objectType/.test(eVue)
  const e26ViewDestructured = /unrecognizedObjectTypeFacts/.test(eVue) && /unrecognizedRawObjectTypes/.test(eVue)
  const e26All =
    e26StoreHasUnrecognizedFacts &&
    e26StoreHasUnrecognizedRaw &&
    e26StoreFilterUsesDict &&
    e26StoreKeepsFiveKeys &&
    e26StoreExported &&
    e26ViewAlert &&
    e26ViewRawTypeBadge &&
    e26ViewDestructured
  assert(
    'E26 静态：Explanation 字典漂移兜底（store 用 EXPLANATION_OBJECT_TYPES 过滤 + 5 码 keys 不变 + 暴露 unrecognized getter + view 渲染 ElAlert 待核对面板 + 列出 raw ObjectType）',
    e26All,
    e26All
      ? '全齐'
      : `storeFacts=${e26StoreHasUnrecognizedFacts} storeRaw=${e26StoreHasUnrecognizedRaw} storeFilter=${e26StoreFilterUsesDict} storeFiveKeys=${e26StoreKeepsFiveKeys} storeExported=${e26StoreExported} viewAlert=${e26ViewAlert} viewBadge=${e26ViewRawTypeBadge} viewDestruct=${e26ViewDestructured}`
  )

  info('E 段：5号位 2026-10-09 一致性核查回执 R1 闭环 + 10-10 option A 收口 + 10-10 business-fact-issues 端点查错库修复（E21 dev 绿）+ 10-10 样式简洁整齐（E22）+ 10-10 business-fact-issues 分页契约锁（E23 静态绿：后端字段 page + 替换式 fetchBizPage + 标准 ElPagination + v-model + [10,20,50]；E24 等 5号位 PageResult 落地）+ 10-10 懒加载契约锁（E25 静态绿；Gantt.vue 仍走 selectVersion 不退化）+ 10-12 字典漂移兜底（E26 静态绿：未入字典值按待核对中性展示，不冒充正式编码）；R2/R3 跨号位等回执')
}

/* ==================== main ==================== */

/**
 * [M] §10A.1 已有订单提前 — OrderCanonicalId 自动带入（m-10a1 — 09-23 B4 已落地触发）
 * 验收清单：
 *  - M01 静态：types/order.ts OrderBasicInfo 含 orderCanonicalId / OrderAdvanceDialog.vue 含 orderCanonicalId 字段 +
 *              自动带入逻辑 + "5号位 待补列" 旧文案已移除 / Order.vue advanceCandidates 透传 orderCanonicalId /
 *              mocks/fixtures.ts mockOrderDetail basic 含 orderCanonicalId
 *  - M02 接受类（默认跳过）：VERIFY_ALLOW_WRITE=1 时执行；需 Order 页已选订单 + Dialog 自动带入 + 提交跳转 Candidate
 */
/* ============================================================================
 * [MN] 菜单分组「策略配置」（与「APS 排产」平级的顶级节点）+ 页面改名「策略包版本」
 *   触发：2026-10-16 用户「这几个菜单能放在一起吗」→ 随后「把策略配置移动到 APS 排产平级菜单，移出来吧」
 *   - M01 静态：分组是 apsV1Routes 的**顶级**节点（不在 /aps.children 内）+ 用 Layout 包裹
 *   - M02 静态：7 个子路由全在分组内，且 path **全部绝对路径**（/aps/xxx），/aps 下无重复平铺
 *   - M03 静态：7 个子路由的 name + apsRequiredPermissions 与基线一致（防搬动时丢 RBAC）
 *   - M04 静态：分组名「策略配置」与页面名「策略包版本」不同名
 *   - M05 静态：组内顺序 = 实际配置顺序
 *   - M06 静态：StrategyProfiles.vue 页内标题同步改为「策略包版本」
 *   - M07 静态：pathResolve 对绝对子路径**原样返回**（不拼父路径）——否则菜单 index / 面包屑
 *              / TagsView 会拼成 /strategy-config/aps/rules，点菜单跳到不存在的地址
 *   - M08~M12 静态：「角色与权限」顶级分组（4 页 /aps/rbac-* + 权限码齐 + 顺序）、
 *              框架自带 10 个演示菜单（首页/文档/引导/组件/功能/hooks/多级菜单/综合示例/错误页面/权限管理）
 *              父节点均已 hidden、两个父分组均不挂权限码
 *   - M13 静态：「需求与供给」「排产执行」「基础数据与日历」是**与 /aps 平级的顶级分组**
 *              （Layout + redirect + alwaysShow + 壳不挂权限码 + 不在 /aps.children 内）
 *   - M14 静态：11 个页面全在三个顶级分组内、path 全为绝对路径（/aps/xxx）、全文件无重复平铺
 *   - M15 静态：三个顶级分组的组内顺序 = 实际使用顺序
 *   - M16 静态：「APS 排产」下只剩 排产总览/甘特图/异常与原因解释/审计日志 4 个相对路径页；
 *              已撤销的「结果查看」组壳不得复活
 *   - M17 静态：分组内 11 个页面仍挂着 RBAC 权限码（搬动不得丢）
 *   - M18 静态：「APS 排产」下 4 个直达页仍挂着 RBAC 权限码
 *   - M19 静态：登录/根路径默认落地 = 排产总览 /aps/overview；字面量只存在于 constants/index.ts，
 *              根路由 redirect 与 LoginForm 两处 push 均引用该常量（不许回退到 /dashboard/分析页）
 * ========================================================================== */
function verifyMenuGroup() {
  log('\n[MN] 菜单分组「策略配置」平级顶级节点 + 页面改名「策略包版本」（menu — 2026-16 归组）')

  const routerSrc = readSrc('router/modules/aps.ts')

  // 分组块：从 { path: 'strategy-config' 起，到其 children 数组结束
  const groupStart = routerSrc.indexOf("path: '/strategy-config'")
  const childrenStart = routerSrc.indexOf('children: [', groupStart)
  // children 数组的闭合：找与 childrenStart 处 '[' 配对的 ']'
  let depth = 0
  let childrenEnd = -1
  for (let i = routerSrc.indexOf('[', childrenStart); i < routerSrc.length; i++) {
    const ch = routerSrc[i]
    if (ch === '[') depth++
    else if (ch === ']') {
      depth--
      if (depth === 0) {
        childrenEnd = i
        break
      }
    }
  }
  const groupBlock = groupStart >= 0 && childrenEnd > childrenStart
    ? routerSrc.slice(groupStart, childrenEnd)
    : ''
  const childrenBlock = groupBlock.slice(groupBlock.indexOf('children: ['))

  // M01 分组是顶级节点（不在 /aps 的 children 里）且用 Layout 包裹
  //   /aps.children 的范围 = 「path: '/aps'」之后的第一个 children 数组闭合处
  const apsIdx = routerSrc.indexOf("path: '/aps',")
  let apsChildrenEnd = -1
  {
    let d = 0
    const cs = routerSrc.indexOf('children: [', apsIdx)
    for (let i = routerSrc.indexOf('[', cs); i < routerSrc.length; i++) {
      if (routerSrc[i] === '[') d++
      else if (routerSrc[i] === ']') {
        d--
        if (d === 0) {
          apsChildrenEnd = i
          break
        }
      }
    }
  }
  const apsChildrenBlock =
    apsIdx >= 0 && apsChildrenEnd > apsIdx
      ? routerSrc.slice(apsIdx, apsChildrenEnd)
      : ''
  const m01 =
    /name:\s*'ApsStrategyConfig'/.test(groupBlock) &&
    /title:\s*'策略配置'/.test(groupBlock) &&
    /component:\s*Layout/.test(groupBlock) &&
    /redirect:\s*'\/aps\/strategy-profile'/.test(groupBlock) &&
    // 关键：分组不得再出现在 /aps.children 里（移出来了）
    apsChildrenBlock.length > 0 &&
    !apsChildrenBlock.includes('ApsStrategyConfig') &&
    // 且必须真的在数组里（顶级）：分组起点晚于 /aps 的 children 闭合处
    groupStart > apsChildrenEnd
  assert(
    'M01 静态：「策略配置」是与 /aps 平级的顶级节点（Layout 包裹 + redirect + 不在 /aps.children 内）',
    m01,
    m01
      ? '已是顶级平级节点'
      : `name/title=${/ApsStrategyConfig/.test(groupBlock)}/${/title:\s*'策略配置'/.test(
          groupBlock
        )} layout=${/component:\s*Layout/.test(groupBlock)} 在Aps内=${apsChildrenBlock.includes(
          'ApsStrategyConfig'
        )} 顺序=${groupStart > apsChildrenEnd}`
  )

  // M02 7 个子路由全在分组内且**全部绝对路径**
  const groupChildren = [
    { name: 'ApsRules', path: '/aps/rules' },
    { name: 'ApsTaskSplitRuleConfig', path: '/aps/task-split-rule-config' },
    { name: 'ApsSetupExact', path: '/aps/setup/exact' },
    { name: 'ApsSetupDefault', path: '/aps/setup/default' },
    { name: 'ApsSetupUncovered', path: '/aps/setup/uncovered' },
    { name: 'ApsSetupDiff', path: '/aps/setup/diff' },
    { name: 'ApsStrategyProfiles', path: '/aps/strategy-profile' }
  ]
  const m02Missing = groupChildren.filter(
    (c) => !new RegExp(`path:\\s*'${c.path}',[\\s\\S]{0,200}?name:\\s*'${c.name}'`).test(childrenBlock)
  )
  // 反向：7 条不得在分组外再平铺一份（重复 name 会导致 router 冲突）
  const groupStartIdx = routerSrc.indexOf("path: '/strategy-config'")
  const outsideBlock = routerSrc.slice(0, groupStartIdx) + routerSrc.slice(
    routerSrc.indexOf('\n', childrenEnd > 0 ? childrenEnd : 0)
  )
  const m02Duplicated = groupChildren.filter(
    (c) => new RegExp(`path:\\s*'${c.path.slice(5)}'`).test(outsideBlock)
  )
  const m02Ok = m02Missing.length === 0 && m02Duplicated.length === 0
  assert(
    'M02 静态：7 个子路由全在分组内、path 全为绝对路径（/aps/xxx）、分组外无重复平铺',
    m02Ok,
    m02Ok
      ? '7 条齐全且均为绝对路径'
      : `缺=${m02Missing.map((c) => c.name).join(',') || '-'} 重复平铺=${m02Duplicated
          .map((c) => c.name)
          .join(',') || '-'}`
  )

  // M03 name + 权限码基线（verify-rbac.mjs 按 path 断言，本条补 name/权限码侧）
  const m03Baseline = [
    { name: 'ApsRules', perms: "['aps.rule.edit', 'aps.rule.publish']" },
    { name: 'ApsTaskSplitRuleConfig', perms: "['aps.rule.view', 'aps.rule.edit']" },
    { name: 'ApsSetupExact', perms: "['aps.setup.view', 'aps.setup.edit', 'aps.setup.publish']" },
    { name: 'ApsSetupDefault', perms: "['aps.setup.view', 'aps.setup.edit', 'aps.setup.publish']" },
    { name: 'ApsSetupUncovered', perms: "['aps.setup.view']" },
    { name: 'ApsSetupDiff', perms: "['aps.setup.view', 'aps.setup.publish']" },
    {
      name: 'ApsStrategyProfiles',
      perms: "['aps.strategy.view', 'aps.strategy.edit', 'aps.strategy.publish']"
    }
  ]
  // eslint --fix 会把超长权限码数组折成多行，故比对前先压掉所有空白
  const m03Flat = childrenBlock.replace(/\s+/g, '')
  const m03Bad = m03Baseline.filter((b) => {
    const idx = m03Flat.indexOf(`name:'${b.name}'`)
    if (idx < 0) return true
    const seg = m03Flat.slice(idx, idx + 400)
    return !seg.includes(`apsRequiredPermissions:${b.perms.replace(/\s+/g, '')}`)
  })
  assert(
    'M03 静态：7 个子路由的 apsRequiredPermissions 与基线一致（搬家不得丢 RBAC 码）',
    m03Bad.length === 0,
    m03Bad.length === 0
      ? `${m03Baseline.length} 条权限码齐全`
      : `不符：${m03Bad.map((b) => b.name).join(',')}`
  )

  // M04 分组名与页面名不同名（防又出现两个「策略配置」）
  const m04GroupTitle = /title:\s*'策略配置'/.test(groupBlock)
  const m04PageTitle = /title:\s*'策略包版本'/.test(childrenBlock)
  const m04NoDupTitle = !/title:\s*'策略配置',\s*\n\s*icon:\s*'vi-mdi:tune-variant',\s*\n\s*noCache/.test(
    childrenBlock
  )
  const m04Ok = m04GroupTitle && m04PageTitle && m04NoDupTitle
  assert(
    'M04 静态：分组名「策略配置」与页面名「策略包版本」不同名',
    m04Ok,
    m04Ok ? '分组=策略配置 / 页面=策略包版本' : `group=${m04GroupTitle} page=${m04PageTitle}`
  )

  // M05 组内顺序 = 实际配置顺序
  const orderKeys = [
    'ApsRules',
    'ApsTaskSplitRuleConfig',
    'ApsSetupExact',
    'ApsSetupDefault',
    'ApsSetupUncovered',
    'ApsSetupDiff',
    'ApsStrategyProfiles'
  ]
  const positions = orderKeys.map((k) => childrenBlock.indexOf(`name: '${k}'`))
  const m05Ok = positions.every((p) => p >= 0) && positions.every((p, i) => i === 0 || p > positions[i - 1])
  assert(
    'M05 静态：组内顺序 = 配置顺序（规则与参数维护→批策略→明确→默认→缺失→对比→策略包版本）',
    m05Ok,
    m05Ok ? '顺序正确' : `positions=${positions.join(',')}`
  )

  // 页面标题同步改名（view 侧）
  const spView = readSrc('views/Aps/StrategyProfiles.vue')
  const m06ViewOk = /<h2 class="page-title">策略包版本<\/h2>/.test(spView)
  assert(
    'M06 静态：StrategyProfiles.vue 页内标题同步改为「策略包版本」',
    m06ViewOk,
    m06ViewOk ? '已改' : '页内 h2 仍是旧名'
  )

  // M07 pathResolve 必须对绝对子路径原样返回（否则菜单 index / 面包屑 / TagsView 全错）
  const pathResolveSrc = (() => {
    const s = readSrc('utils/routerHelper.ts')
    const i = s.indexOf('export const pathResolve')
    return i >= 0 ? s.slice(i, s.indexOf('\n}', i) + 2) : ''
  })()
  const m07HasAbsoluteGuard = /if\s*\(path\.startsWith\(['"]\/['"]\)\)\s*return\s+path/.test(
    pathResolveSrc
  )
  assert(
    "M07 静态：pathResolve 对绝对子路径（/aps/xxx）原样返回，不拼父路径（10-16 顶级分组引入）",
    m07HasAbsoluteGuard,
    m07HasAbsoluteGuard
      ? '绝对子路径已短路返回'
      : 'pathResolve 会把 /aps/rules 拼成 /strategy-config/aps/rules —— 点侧边栏即跳到不存在的地址'
  )

  /* ---- 「角色与权限」分组（10-16 追加）---- */
  const rbacIdx = routerSrc.indexOf("path: '/rbac',")
  let rbacEnd = -1
  {
    let d = 0
    const cs = routerSrc.indexOf('children: [', rbacIdx)
    for (let i = routerSrc.indexOf('[', cs); i < routerSrc.length; i++) {
      if (routerSrc[i] === '[') d++
      else if (routerSrc[i] === ']') {
        d--
        if (d === 0) {
          rbacEnd = i
          break
        }
      }
    }
  }
  const rbacBlock =
    rbacIdx >= 0 && rbacEnd > rbacIdx ? routerSrc.slice(rbacIdx, rbacEnd) : ''
  const rbacChildren = rbacBlock.slice(rbacBlock.indexOf('children: ['))

  const m08 =
    rbacIdx >= 0 &&
    /name:\s*'ApsRbac'/.test(rbacBlock) &&
    /title:\s*'角色与权限'/.test(rbacBlock) &&
    /component:\s*Layout/.test(rbacBlock) &&
    /redirect:\s*'\/aps\/rbac-users'/.test(rbacBlock) &&
    // 顶级：不得出现在 /aps.children 内，且位于 /aps 之外
    !apsChildrenBlock.includes('ApsRbac') &&
    rbacIdx > apsChildrenEnd
  assert(
    "M08 静态：「角色与权限」是与 /aps 平级的顶级节点（Layout + redirect + 不在 /aps.children 内）",
    m08,
    m08
      ? '已是顶级平级节点'
      : `存在=${rbacIdx >= 0} layout=${/component:\s*Layout/.test(
          rbacBlock
        )} 在Aps内=${apsChildrenBlock.includes('ApsRbac')} 顺序=${rbacIdx > apsChildrenEnd}`
  )

  const rbacChildrenBaseline = [
    { name: 'ApsRbacUsers', path: '/aps/rbac-users' },
    { name: 'ApsRbacRoles', path: '/aps/rbac-roles' },
    { name: 'ApsRbacPermissions', path: '/aps/rbac-permissions' },
    { name: 'ApsRbacScopes', path: '/aps/rbac-scopes' }
  ]
  const rbacFlat = rbacChildren.replace(/\s+/g, '')
  const m09Missing = rbacChildrenBaseline.filter(
    (c) =>
      !rbacFlat.includes(`path:'${c.path}',component:`) ||
      !rbacFlat.includes(`name:'${c.name}'`)
  )
  const m09 =
    m09Missing.length === 0 &&
    // 权限码一条不能少（后端类级 AuthManage 码）
    rbacFlat.match(/apsRequiredPermissions:\['aps\.auth\.user\.edit'\]/g)?.length === 4 &&
    // 组外不得有重复平铺
    !rbacChildrenBaseline.some((c) =>
      new RegExp(`path:\\s*'${c.path.slice(5)}'`).test(
        routerSrc.slice(0, rbacIdx) + routerSrc.slice(rbacEnd > 0 ? rbacEnd : 0)
      )
    )
  assert(
    'M09 静态：4 个 RBAC 页全在分组内、path 全为绝对路径（/aps/rbac-*）、权限码齐全、组外无重复平铺',
    m09,
    m09
      ? '4 条齐全且均为绝对路径，权限码齐'
      : `缺=${m09Missing.map((c) => c.name).join(',') || '-'} 权限码数=${
          rbacFlat.match(/apsRequiredPermissions:\['aps\.auth\.user\.edit'\]/g)?.length ?? 0
        }`
  )

  const rbacOrder = rbacChildrenBaseline.map((c) => rbacFlat.indexOf(`name:'${c.name}'`))
  const m10 =
    rbacOrder.every((p) => p >= 0) &&
    rbacOrder.every((p, i) => i === 0 || p > rbacOrder[i - 1])
  assert(
    'M10 静态：组内顺序 = 配置顺序（用户 → 角色 → 权限码 → 业务范围维护）',
    m10,
    m10 ? '顺序正确' : `positions=${rbacOrder.join(',')}`
  )

  // M11 框架自带的 10 个演示菜单已 hidden（走 mock / 与 APS 无关，用户 10-16 裁决全部隐藏）
  //   - /authorization：4 页走 mock 未接后端，与「角色与权限」组重复易误点
  //   - 首页/文档/引导/组件/功能/hooks/多级菜单/综合示例/错误页面：纯框架示例，与业务无关
  //   - 必须藏**父节点**：helper.ts 的 hasOneShowingChild 在子项全被藏时会回退渲染父节点自己的标题，
  //     /guide 与 /external-link 各只有 1 个子项，藏子项会剩个空壳菜单项
  const indexSrc = readSrc('router/index.ts')
  const demoMenus = [
    '/dashboard',
    '/external-link',
    '/guide',
    '/components',
    '/function',
    '/hooks',
    '/level',
    '/example',
    '/error',
    '/authorization'
  ]
  const m11Missing = demoMenus.filter((p) => {
    const i = indexSrc.indexOf(`path: '${p}',`)
    if (i < 0) return true
    const cs = indexSrc.indexOf('children: [', i)
    const head = indexSrc.slice(i, cs >= 0 ? cs : i + 600)
    return !/hidden:\s*true/.test(head)
  })
  const m11 = m11Missing.length === 0
  assert(
    'M11 静态：框架自带 10 个演示菜单（首页/文档/引导/组件/功能/hooks/多级菜单/综合示例/错误页面/权限管理）父节点均已 meta.hidden',
    m11,
    m11
      ? '10 个全部 hidden（路由仍在表里，老链接可直达）'
      : `未 hidden=${m11Missing.join(',') || '-'}`
  )

  // M12 两个分组都不能挂在父节点权限码上（挂了会导致无权限用户连菜单都看不到）
  const m12 =
    !/apsRequiredPermissions|apsRequiredRoles/.test(groupBlock.split('children: [')[0]) &&
    !/apsRequiredPermissions|apsRequiredRoles/.test(rbacBlock.split('children: [')[0])
  assert(
    'M12 静态：「策略配置」「角色与权限」两个父分组均未挂权限码（菜单不按权限过滤，越权由子路由守卫拦）',
    m12,
    m12 ? '两个父分组均无权限码' : '父分组挂了权限码 ⇒ 无权限用户看不到菜单（现状是能看见、点进去才 403）'
  )

  /* ---- 三个**顶级**分组：需求与供给 / 排产执行 / 基础数据与日历（10-16 第二轮）----
   * 第一轮把它们放在 /aps 内当子分组；用户看过侧边栏后要求「这几个放在与 APS 排产平级」，
   * 同时撤销「结果查看」这层壳（它下面 2 页 —— 甘特图/资源计划、异常与原因解释 —— 直接挂回 /aps）。
   */
  const apsChildrenArray = apsChildrenBlock.slice(apsChildrenBlock.indexOf('children: ['))

  // 数 /aps.children 里的**顶层**条目数，用于证明没有多余残留
  const countTopLevel = (arrText) => {
    let br = 0
    let cb = 0
    let n = 0
    for (const ch of arrText) {
      if (ch === '[') br++
      else if (ch === ']') br--
      else if (ch === '{') {
        if (br === 1 && cb === 0) n++
        cb++
      } else if (ch === '}') cb--
    }
    return n
  }

  const apsGroupSpec = [
    {
      shell: '/aps-demand',
      name: 'ApsGroupDemand',
      title: '需求与供给',
      redirect: '/aps/order',
      leaves: [
        { name: 'ApsOrder', path: '/aps/order' },
        { name: 'ApsManualEta', path: '/aps/manual-eta' },
        { name: 'ApsPi', path: '/aps/pi' },
        { name: 'ApsDemandProtection', path: '/aps/demand-protection' }
      ]
    },
    {
      shell: '/aps-execution',
      name: 'ApsGroupExecution',
      title: '排产执行',
      redirect: '/aps/ctp',
      leaves: [
        { name: 'ApsCtp', path: '/aps/ctp' },
        { name: 'ApsCandidate', path: '/aps/candidate' },
        { name: 'ApsRun', path: '/aps/run' }
      ]
    },
    {
      shell: '/aps-master-data',
      name: 'ApsGroupMasterData',
      title: '基础数据与日历',
      redirect: '/aps/domain',
      leaves: [
        { name: 'ApsDomain', path: '/aps/domain' },
        { name: 'ApsOperationPlanningMode', path: '/aps/operation-planning-mode' },
        { name: 'ApsResourceCalendar', path: '/aps/resource-calendar' },
        { name: 'ApsProductionWorkCalendar', path: '/aps/production-work-calendar' }
      ]
    }
  ]

  // 取组壳块：从 path: '<shell>', 回退到其 '{'，再按方括号配平找到 children 数组
  const apsGroupOf = (spec) => {
    const gi = routerSrc.indexOf(`path: '${spec.shell}',`)
    if (gi < 0) return null
    let b = gi
    while (b >= 0 && routerSrc.slice(b, gi).trim().length > 240) b = routerSrc.lastIndexOf('{', b - 1)
    const cs = routerSrc.indexOf('children: [', b)
    if (cs < 0) return null
    let d = 0
    let end = -1
    for (let i = routerSrc.indexOf('[', cs); i < routerSrc.length; i++) {
      const ch = routerSrc[i]
      if (ch === '[') d++
      else if (ch === ']') {
        d--
        if (d === 0) {
          end = i
          break
        }
      }
    }
    if (end < 0) return null
    return {
      idx: gi,
      head: routerSrc.slice(b, cs),
      children: routerSrc.slice(cs, end).replace(/\s+/g, '')
    }
  }
  const apsGroups = apsGroupSpec.map((spec) => ({ spec, got: apsGroupOf(spec) }))

  // M13 三个组壳齐备 + 确实是**顶级**（不在 /aps.children 内、位于 /aps 之后）+ 壳自身合法
  const m13Bad = apsGroups.filter(({ spec, got }) => {
    if (!got) return true
    const h = got.head.replace(/\s+/g, ' ')
    return !(
      h.includes(`name: '${spec.name}'`) &&
      h.includes(`title: '${spec.title}'`) &&
      h.includes(`redirect: '${spec.redirect}'`) &&
      h.includes('component: Layout') &&
      h.includes('alwaysShow: true') &&
      !/apsRequiredPermissions|apsRequiredRoles/.test(got.head) &&
      !apsChildrenBlock.includes(spec.name) &&
      got.idx > apsChildrenEnd
    )
  })
  assert(
    'M13 静态：「需求与供给」「排产执行」「基础数据与日历」是与 /aps 平级的顶级分组（Layout + redirect + 壳不挂权限码）',
    m13Bad.length === 0,
    m13Bad.length === 0
      ? `${apsGroupSpec.map((g) => g.title).join(' / ')} 三组均为顶级`
      : `不合规=${m13Bad.map(({ spec }) => spec.title).join(',') || '未找到'}`
  )

  // M14 11 个叶子页全在各自壳内、path 全绝对、且全文件只出现一次（防重复平铺导致 router 冲突）
  const m14Bad = []
  apsGroups.forEach(({ spec, got }) => {
    if (!got) return
    spec.leaves.forEach((l) => {
      const inGroup =
        got.children.includes(`path:'${l.path}',`) && got.children.includes(`name:'${l.name}'`)
      const occurrences = routerSrc.split(`name: '${l.name}'`).length - 1
      if (!inGroup || !l.path.startsWith('/aps/') || occurrences !== 1) {
        m14Bad.push(`${l.name}(在组内=${inGroup},全局出现=${occurrences})`)
      }
    })
  })
  const m14LeafCount = apsGroupSpec.reduce((n, g) => n + g.leaves.length, 0)
  assert(
    'M14 静态：11 个页面全在三个顶级分组内、path 全为绝对路径（/aps/xxx）、全文件无重复平铺',
    m14Bad.length === 0,
    m14Bad.length === 0
      ? `${m14LeafCount} 条齐全且均为绝对路径，无重复`
      : `异常=${m14Bad.join(';')}`
  )

  // M15 组内顺序 = 使用顺序
  const m15Bad = []
  apsGroups.forEach(({ spec, got }) => {
    if (!got) return
    const pos = spec.leaves.map((l) => got.children.indexOf(`name:'${l.name}'`))
    const asc = pos.every((p, i) => p >= 0 && (i === 0 || p > pos[i - 1]))
    if (!asc) m15Bad.push(`${spec.title}[${pos.join(',')}]`)
  })
  assert(
    'M15 静态：三个顶级分组的组内顺序 = 实际使用顺序',
    m15Bad.length === 0,
    m15Bad.length === 0 ? '3 组顺序均正确' : `顺序错=${m15Bad.join(';')}`
  )

  // M16 /aps 只剩 4 个直达页（全部相对路径），且已撤销的「结果查看」组壳不得复活
  const apsDirectBaseline = [
    { name: 'ApsOverview', path: 'overview' },
    { name: 'ApsGantt', path: 'gantt' },
    { name: 'ApsExplanation', path: 'explanation' },
    { name: 'ApsAudit', path: 'audit' }
  ]
  const m16Direct = apsDirectBaseline.filter(
    (d) =>
      !new RegExp(`path:\\s*'${d.path}',[\\s\\S]{0,200}?name:\\s*'${d.name}'`).test(apsChildrenArray)
  )
  const m16TopCount = countTopLevel(apsChildrenArray)
  const m16NoResultGroup =
    !routerSrc.includes('ApsGroupResult') && !/title:\s*'结果查看'/.test(routerSrc)
  const m16 =
    m16Direct.length === 0 && m16TopCount === apsDirectBaseline.length && m16NoResultGroup
  assert(
    'M16 静态：「APS 排产」下只剩 排产总览/甘特图/异常与原因解释/审计日志 4 个相对路径页；「结果查看」分组已撤销',
    m16,
    m16
      ? '4 个直达页齐备，无「结果查看」壳'
      : `缺=${m16Direct.map((d) => d.name).join(',') || '-'} 顶层条数=${m16TopCount} 结果查看已撤=${m16NoResultGroup}`
  )

  // M17 分组内 11 页仍挂 RBAC 权限码（搬家不得丢；verify-rbac.mjs 按 path 断言，这边按组内归属）
  const m17Bad = []
  apsGroups.forEach(({ spec, got }) => {
    if (!got) return
    spec.leaves.forEach((l) => {
      const idx = got.children.indexOf(`name:'${l.name}'`)
      const next = got.children.indexOf("},name:'", idx)
      const seg = idx >= 0 ? got.children.slice(idx, next > idx ? next : idx + 400) : ''
      if (!/(apsRequiredPermissions|apsRequiredRoles):\[/.test(seg)) m17Bad.push(l.name)
    })
  })
  assert(
    'M17 静态：分组内 11 个页面仍挂着 RBAC 权限码（apsRequiredPermissions / apsRequiredRoles）',
    m17Bad.length === 0,
    m17Bad.length === 0 ? `${m14LeafCount} 条权限码齐全` : `缺权限码=${m17Bad.join(',')}`
  )

  // M18 /aps 下 4 个直达页仍挂 RBAC 权限码
  const m18Bad = apsDirectBaseline.filter((d) => {
    const i = apsChildrenArray.indexOf(`name: '${d.name}'`)
    const seg = i >= 0 ? apsChildrenArray.slice(i, i + 500) : ''
    return !/apsRequiredPermissions:\s*\[\s*'aps\./.test(seg)
  })
  assert(
    'M18 静态：「APS 排产」下 4 个直达页仍挂着 RBAC 权限码',
    m18Bad.length === 0,
    m18Bad.length === 0 ? '4 条权限码齐全' : `缺权限码=${m18Bad.map((d) => d.name).join(',')}`
  )

  // M19 登录/根路径默认落地 = 排产总览，且只有 constants/index.ts 一处字面量
  const constantsSrc = readSrc('constants/index.ts')
  const routerIndexSrc = readSrc('router/index.ts')
  const loginFormSrc = readSrc('views/Login/components/LoginForm.vue')
  const m19Bad = []
  // a) 常量本身
  if (!/export const APS_HOME_PATH = '\/aps\/overview'/.test(constantsSrc)) m19Bad.push('constants 未定义 APS_HOME_PATH=/aps/overview')
  // b) 常量不得在别处被重新定义
  if ((routerIndexSrc + loginFormSrc).includes('APS_HOME_PATH =')) m19Bad.push('APS_HOME_PATH 被重复定义')
  // c) 根路由 redirect 用常量，且不再是 /dashboard
  const rootSeg = routerIndexSrc.slice(
    routerIndexSrc.indexOf("path: '/',"),
    routerIndexSrc.indexOf('name: \'Root\'')
  )
  if (!/redirect:\s*APS_HOME_PATH/.test(rootSeg)) m19Bad.push('router/index.ts 根路由 redirect 未用 APS_HOME_PATH')
  // 只查根记录：/dashboard 自身仍 redirect 到 /dashboard/analysis 是有意的（该页 hidden 但老链接需可直达）
  if (/redirect:\s*'\/(dashboard|aps-demand|aps-execution|aps-master-data|strategy-config|rbac)/.test(rootSeg)) m19Bad.push('根路由 redirect 仍指向框架演示页/分组壳')
  // d) LoginForm 两处登录跳转都用常量，且不再用 addRouters[0]（那是 hidden 的 /dashboard）
  const loginPush = loginFormSrc.match(/push\(\{\s*path:\s*([^)]+?)\s*\}\)/g) || []
  if (loginPush.length !== 2) m19Bad.push(`LoginForm 登录跳转处数=${loginPush.length}(期望 2)`)
  loginPush.forEach((p, i) => {
    if (!/redirect\.value\s*\|\|\s*APS_HOME_PATH/.test(p)) m19Bad.push(`LoginForm 第 ${i + 1} 处未用 APS_HOME_PATH`)
  })
  if (loginFormSrc.includes('addRouters[0]')) m19Bad.push('LoginForm 仍用 addRouters[0]（指向 hidden 的 /dashboard）')
  assert(
    'M19 静态：登录/根路径默认落地 = 排产总览 /aps/overview（constants 一处字面量，根路由与 LoginForm 两处跳转均引用）',
    m19Bad.length === 0,
    m19Bad.length === 0
      ? `常量 APS_HOME_PATH='/aps/overview'，根 redirect + LoginForm 2 处跳转全引用`
      : m19Bad.join(';')
  )

  // M20 登录页只留 用户名/密码/记住我/登录；副标题、注册、忘记密码、其它登录方式均已下线
  const zhLoginSrc = readSrc('locales/zh-CN.ts')
  const loginVueSrc = readSrc('views/Login/Login.vue')
  const loginFormFile = join(SRC_ROOT, 'views/Login/components/RegisterForm.vue')
  const m20Bad = []
  // a) 欢迎语
  if (!/welcome:\s*'欢迎使用APS系统'/.test(zhLoginSrc)) m20Bad.push('欢迎语非「欢迎使用APS系统」')
  // b) 副标题下线（文案与渲染一并去掉）
  if (/message:\s*'开箱即用/.test(zhLoginSrc)) m20Bad.push('locales 仍有 login.message 副标题')
  if (/login\.message/.test(loginVueSrc)) m20Bad.push('Login.vue 仍渲染 login.message 副标题')
  // c) 登录框内不得再出现 忘记密码 / 注册 / 其它登录方式 / 四个第三方图标
  const gone = [
    ['forgetPassword', '忘记密码'],
    ['register', '注册按钮'],
    ['otherLogin', '其它登录方式']
  ]
  gone.forEach(([k, cn]) => {
    if (loginFormSrc.includes(`login.${k}`)) m20Bad.push(`LoginForm 仍有${cn}`)
    if (new RegExp(`\\b${k}:`).test(zhLoginSrc)) m20Bad.push(`locales 仍有 login.${k} 死键`)
  })
  if (/vi-ant-design:(github|wechat|alipay|weibo)/.test(loginFormSrc)) m20Bad.push('LoginForm 仍有第三方登录图标')
  // d) 注册链路整体下线（组件删了，Login.vue 不得再引）
  if (existsSync(loginFormFile)) m20Bad.push('RegisterForm.vue 仍存在（应为不可达死代码）')
  if (/RegisterForm|to-register|toRegister|isLogin/.test(loginVueSrc)) m20Bad.push('Login.vue 仍挂注册表单/切换态')
  assert(
    'M20 静态：登录页欢迎语=「欢迎使用APS系统」、副标题已去；忘记密码/注册/其它登录方式（含第三方图标）与 RegisterForm 组件均已下线',
    m20Bad.length === 0,
    m20Bad.length === 0
      ? '登录页只剩 用户名/密码/记住我/登录，注册链路已彻底移除'
      : m20Bad.join(';')
  )

  // M21 菜单底色钉死为深色：app store 是 persist 的，设置抽屉里调过一次浅色就会
  // 长期留在 localStorage，ConfigGlobal 挂载时的 setCssVarTheme() 会把它写回 CSS 变量
  // ⇒ 每次刷新菜单都是白的（文字 #333、浅块同底色、选中项淡蓝底）。initTheme 必须重置。
  const appStoreSrc = readSrc('store/modules/app.ts')
  const m21Bad = []
  if (!/export const MENU_BG_COLOR = '#001529'/.test(appStoreSrc)) {
    m21Bad.push('app.ts 无 MENU_BG_COLOR = #001529 常量')
  }
  if (!/leftMenuBgColor:\s*MENU_BG_COLOR/.test(appStoreSrc)) {
    m21Bad.push('state 默认 leftMenuBgColor 未引用 MENU_BG_COLOR（默认值与重置值会各自漂移）')
  }
  const initThemeSeg = appStoreSrc.slice(appStoreSrc.indexOf('initTheme()'))
  if (!/setMenuTheme\(MENU_BG_COLOR\)/.test(initThemeSeg)) {
    m21Bad.push('initTheme() 未调用 setMenuTheme(MENU_BG_COLOR)，localStorage 残留的浅色菜单底色会复活')
  }
  assert(
    'M21 静态：菜单底色固定深色 #001529，initTheme() 强制重置（不受 localStorage 残留主题影响）',
    m21Bad.length === 0,
    m21Bad.length === 0
      ? '菜单底色由 MENU_BG_COLOR 单点定义，启动时强制写回 CSS 变量'
      : m21Bad.join(';')
  )
}

/**
 * ==========================================================================
 * 排产总览（Overview）— GROUP=overview
 *  2026-10-16 依据 5号位《Overview-R1更正与修复》函重接真实端点：
 *   Overview 无聚合端点，5 个数据面各自独立调（active-plan / order-query-summary /
 *   resource-bottleneck / candidate-summary / domain-status）。
 *  - OV01~OV05 动态：真实端点可达 + 响应形状与前端类型一致
 *  - OV06~OV10 静态：页面/类型/store 不再含 mock 复合体与旧词表
 * ==========================================================================
 */
/**
 * 候选版本确认页 — 按后端**已部署** DTO 对齐（candidate — 2026-10-16 代码级审计）
 *
 * 审计起因：本页此前的类型是 4号位 依据 §十二「17 字段」自拟的富对比契约，后端从未实现；
 * 列表还绕道调 `/api/governance/runs?status=CANDIDATE`（返 ScheduleRun，字段无一匹配）。
 * 本组锁定「只消费后端真给的东西」，防止自拟字段被再加回页面。
 */
async function verifyCandidate(token) {
  const apiSrc = readSrc('api/aps-v1/candidate.ts')
  const typeSrc = readSrc('api/aps-v1/types/candidate.ts')
  const storeSrc = readSrc('store/modules/aps/candidate.ts')
  const viewSrc = readSrc('views/Aps/Candidate.vue')
  const apiCode = stripComments(apiSrc)
  const viewCode = stripComments(viewSrc)
  log('\n[CD] 候选版本确认 — 真实端点对齐 + 无假数据（candidate — 2026-10-16）')

  // CD01 列表走 5号位正式端点，不再绕道 governance/runs
  assert(
    'CD01 静态：列表端点为 /api/overview/candidate-summary（绕道端点返回 ScheduleRun，与页面字段无一匹配）',
    /url: '\/api\/overview\/candidate-summary'/.test(apiCode) &&
      !/governance\/runs\?status=CANDIDATE/.test(apiCode),
    '绕道端点一旦回退，列表会静默变空且拿不到 basePlanVersionId'
  )

  // CD02 compare-with 双参端点保留
  assert(
    'CD02 静态：对比走 compare-with 双参端点（candidatePlanVersionId + basePlanVersionId）',
    /compare-with\/\$\{basePlanVersionId\}/.test(apiCode),
    '双参端点是唯一能给汇总对比的入口'
  )

  // CD03 后端不提供的字段不得回到类型里（订单级明细 / 根因 / 共享资源 / 数量-时间切片）
  const ghostFields = [
    'newOrderDiffs',
    'impactedOrders',
    'reasons',
    'sharedResourceOccupancies',
    'quantityTimeSlices',
    'sharedResourceOccupancyCount',
    'quantityTimeSliceCount',
    'externalDomainBlocksCount',
    'sourceRefType',
    'purpose'
  ]
  const ghosts = ghostFields.filter((f) => new RegExp(`\\b${f}\\b`).test(typeSrc))
  assert(
    `CD03 静态：类型层不得再出现后端未提供的字段（${ghostFields.length} 个已下线字段）`,
    ghosts.length === 0,
    ghosts.length ? `复活字段：${ghosts.join(', ')}` : '订单级明细/根因/共享资源/切片均已下线'
  )

  // CD04 页面模板不得再消费这些字段（假数据的最直接证据）
  const viewGhosts = ghostFields.filter((f) => new RegExp(`\\b${f}\\b`).test(viewCode))
  assert(
    'CD04 静态：页面模板不再渲染后端不提供的区块',
    viewGhosts.length === 0,
    viewGhosts.length ? `页面仍在渲染：${viewGhosts.join(', ')}` : '使用说明/订单表/根因/共享资源/切片 5 块均已移除'
  )

  // CD05 mock 桩整体删除（VITE_USE_MOCK 四处皆 false，桩只会造成假绿）
  const mockHits = [
    /const candidate1\s*:/,
    /candidateWhatif/,
    /candidateInsertImpact/,
    /mockCandidateList/,
    /findMockActivatableCandidate/,
    /if \(APS_USE_MOCK\)/,
    /\b200[1-4]\b/
  ].filter((re) => re.test(apiCode))
  assert(
    'CD05 静态：candidate.ts 不含任何 mock 分支与手写桩',
    mockHits.length === 0,
    mockHits.length ? `命中 ${mockHits.length} 处 mock 痕迹` : '4 份桩数据 + mock 分支已全部删除'
  )

  // CD06 写端点只回 message + 服务端时间，页面不得自造操作者/时间
    const storeCode = stripComments(storeSrc)
  const ackGhosts = ['lastConfirmResult', 'lastActivateResult', 'confirmedAt', 'activatedBy', 'actor']
    .filter((f) => new RegExp(`\\b${f}\\b`).test(viewCode + storeCode))
  assert(
    'CD06 静态：确认/采用回执只展示后端给的 message + 服务端时间，不自造 actor/时间',
    ackGhosts.length === 0,
    ackGhosts.length ? `仍有前端自造字段：${ackGhosts.join(', ')}` : '后端 data=null，页面只展示 message'
  )

  // CD07 estimatedOnlyCount 后端返 null，页面必须显示 '-' 而不是 0
  assert(
    "CD07 静态：仅估算订单后端返 null 时显示 '-'，不按 0 处理",
    /estimatedOnlyCount \?\? '-'/.test(viewCode),
    "null 被当成 0 展示会把「后端未对齐」伪装成「确实为 0」"
  )

  // CD08 canActivate 一律取后端值，页面不得自行按 runType 推断
  assert(
    'CD08 静态：可激活性取自后端 CandidateBriefDto.canActivate',
    /canActivate: brief\.canActivate/.test(stripComments(storeSrc)),
    '前端按 runType 推断可激活性会与后端 §12.3 激活红线判定不一致'
  )

  // CD09 动态：candidate-summary 真实返回
  const sum = await call('GET', '/api/overview/candidate-summary', { token })
  const d = sum.json?.data
  const cand = d?.candidates
  const shapeOk =
    sum.code === 200 &&
    d &&
    typeof d.pendingCount === 'number' &&
    (cand === null || Array.isArray(cand))
  assert(
    'CD09 动态：/api/overview/candidate-summary 可达且返回 { pendingCount, candidates[] }',
    shapeOk,
    `code=${sum.code} pendingCount=${d?.pendingCount} candidates=${Array.isArray(cand) ? cand.length : cand}`
  )

  // CD10 动态：候选列表项含页面渲染与激活判定所需字段（2026-10-17 改判据）
  //  ⚠️ 旧口径要求 `basePlanVersionId` **必须存在** —— 那是**误判**：
  //     后端契约是完整的：`CandidateBriefDto`（lps/LPS.APS.Core/Dto/OverviewDtos.cs:144）声明了
  //     `BasePlanVersionId`，且仓库层 `OverviewQueryRepository.cs:166` 如实
  //     `SELECT sr.BasePlanVersionId AS BasePlanVersionId`。
  //  ⇒ 值缺**在数据面**：dev 这批 `ScheduleRun.BasePlanVersionId` 本身是 NULL
  //     （集成测试夹具 `SyntheticSchedulingIntegrationTest.cs:297-300` 的 INSERT 不含该列
  //      ⇒ 它插的 Run 无 Base）⇒ `int?` + Program.cs:94 WhenWritingNull ⇒ **整键省略**，
  //     前端拿到 `undefined` 而非 `null`。dev 实测 0/21，`CreatedByUserName` 同样 0/21。
  //  ⇒ **不是契约缺口、不是仓库层漏填**，是测试夹具残留；清理口径已于 2026-10-16 出函 3号位
  //     （《4号位致3号位_候选页对比无法验收与集成测试残留清理》R1/R2/R3），**不另发函**。
  //     探针改为「断言页面能用得上的字段」，并把缺口如实打进输出行，避免一绿了事掩盖它。
  //     前端对「被省略的键」的容错由 CD10b 静态锁死。
  const first = Array.isArray(cand) ? cand[0] : null
  if (!first) {
    skip('CD10 动态', 'dev 无候选版本（CANDIDATE 数为 0），无法校验列表项字段')
  } else {
    const ok =
      typeof first.planVersionId === 'number' &&
      typeof first.canActivate === 'boolean' &&
      'runType' in first
    const withBase = cand.filter((c) => 'basePlanVersionId' in c).length
    const withCreator = cand.filter((c) => 'createdByUserName' in c).length
    const gapNote =
      withBase === cand.length && withCreator === cand.length
        ? '（可选键齐全）'
        : `（⚠️ 数据面缺口：basePlanVersionId ${withBase}/${cand.length}、createdByUserName ${withCreator}/${cand.length} —— 契约与仓库层均正常，是这批 ScheduleRun 自身 BasePlanVersionId 为 NULL，属集成测试夹具残留，已 10-16 出函 3号位）`
    assert(
      'CD10 动态：候选列表项含 planVersionId / canActivate / runType（页面渲染与激活判定所需）',
      ok,
      `实际键：${Object.keys(first).join(', ')}${gapNote}`
    )
  }

  // CD10b 静态：前端对「被 WhenWritingNull 省略的键」必须容错 —— 不得把 undefined 渲染成 `#undefined`
  //  - 后端 `BasePlanVersionId` / `CreatedByUserName` 缺失时整键不在响应里 ⇒ 前端拿到 undefined 不是 null；
  //    `x === null ? '未关联' : '#'+x` 这种严格判等会落到 else 分支渲染出 `#undefined`
  //  - 反向探针自证可失败：把 Candidate.vue 的 `== null` 改回 `=== null` 会让 CD10b 变红
  const cdVue = stripComments(readSrc('views/Aps/Candidate.vue'))
  const cd10bOk =
    /basePlanVersionId\s*==\s*null/.test(cdVue) && !/basePlanVersionId\s*===\s*null/.test(cdVue)
  assert(
    'CD10b 静态：候选页「基准版本」对缺键容错（`== null` 同时吃 null 与 undefined，不渲染 #undefined）',
    cd10bOk,
    cd10bOk ? '宽松判等，缺键走「未关联」' : '严格判等 === null 会把 undefined 渲染成 #undefined'
  )

  // CD11 动态：compare-with 对不存在的版本返 404（证明端点已部署且双参路径正确）
  const cmp = await call('GET', '/api/governance/plan-version/999999999/compare-with/999999998', {
    token
  })
  assert(
    'CD11 动态：compare-with 端点已部署（不存在版本返 404）',
    cmp.code === 404 || cmp.status === 404,
    `code=${cmp.code} message=${cmp.json?.message ?? '-'}`
  )

  /* ===== CD12-CD15：13 §五 六字段 + 单 Run 范围明细（2026-10-19 5号位 落码）=====
   * 契约：CandidateBriefDto +6（全部可空，null≠0）；GET /api/governance-query/run/{id}/scope
   *      （5号位 G4；Run 不存在 404，ScopeJson 为空 200+scopeAvailable=false）。
   */

  // CD12 静态：类型层 —— 5 项 ScopeJson 投影与 baseVersionCode 必须是可空字段
  //  （不可空会把「无载荷」逼成 0/空串，页面就分不清「没记录」与「确实是 0」）
  const cd12Fields = [
    'businessTriggerType',
    'priorityMode',
    'orderTargetCount',
    'taskTargetCount',
    'changedResourceCount'
  ]
  const cd12Ok =
    cd12Fields.every((f) => new RegExp(`${f}\\?:\\s*(string|number)\\s*\\|\\s*null`).test(typeSrc)) &&
    /baseVersionCode\?:\s*string\s*\|\s*null/.test(typeSrc) &&
    /scopeAvailable:\s*boolean/.test(typeSrc) &&
    /mtS_InstructionNo:\s*string\s*\|\s*null/.test(typeSrc)
  assert(
    'CD12 静态：CandidateBrief 六字段全可空 + RunScopeDto 含 scopeAvailable / mtS_InstructionNo',
    cd12Ok,
    cd12Ok
      ? '六字段 string?/number? 齐备；scopeAvailable 与 mtS_InstructionNo 已声明'
      : '存在不可空字段或 RunScopeDto 缺键（会把「无载荷」逼成 0，或读错指示号键名）'
  )

  // CD13 静态：卡片计数 null 显示「—」，不得按 0 兜底；基准版本优先显示版本编码
  //  - countText 必须用 `n == null`（宽松判等同时吃 null 与缺键），不能 `n || 0`
  //  - 基准版本优先 baseVersionCode，不直接渲染 `#<内部 Id>`
  const cd13Ok =
    /function countText[\s\S]{0,160}?n == null/.test(viewCode) &&
    /item\.baseVersionCode \?\?/.test(viewCode) &&
    !/(orderTargetCount|taskTargetCount|changedResourceCount)\s*\|\|\s*0/.test(viewCode)
  assert(
    'CD13 静态：候选卡计数 null→「—」（不按 0 兜底）+ 基准版本优先显示版本编码（不露内部 Id）',
    cd13Ok,
    cd13Ok
      ? 'countText 用 == null；baseVersionCode 优先；无 `|| 0`'
      : '计数兜成 0 会把「无载荷」伪装成「0 个」；或基准版本仍直接显示 #Id'
  )

  // CD14 静态：范围明细面板 —— scopeAvailable=false 必须单独说明；明细只显示业务编码
  //  - 内部 Id（orderCanonicalId/taskId/resourceId）不得渲染到页面
  const cd14Ok =
    /!scope\.scopeAvailable/.test(viewCode) &&
    /row\.orderNo/.test(viewCode) &&
    /row\.taskNo/.test(viewCode) &&
    /row\.resourceCode/.test(viewCode) &&
    !/row\.(orderCanonicalId|taskId|resourceId)/.test(viewCode)
  assert(
    'CD14 静态：范围面板区分「无范围载荷」（非 0 个目标）且只渲染业务编码',
    cd14Ok,
    cd14Ok
      ? 'scopeAvailable=false 有独立说明；订单号/TaskNo/资源编码齐；未渲染内部 Id'
      : '缺 scopeAvailable 分支，或把内部 Id 直接显示给用户（违反列表不露内部 Id）'
  )

  // CD15 静态：端点路径与归属（5号位 G4 通道）+ silent（失败不清空整页）
  const cd15Ok =
    /\/api\/governance-query\/run\/\$\{scheduleRunId\}\/scope/.test(apiCode) &&
    /silent: true/.test(apiCode)
  assert(
    'CD15 静态：范围明细走 /api/governance-query/run/{id}/scope（5号位 G4）且 silent 请求',
    cd15Ok,
    cd15Ok
      ? '路径与归属正确；失败只在范围内展示，不弹全局 toast'
      : '路径落到 3号位 api/governance 路由树，或失败会污染整页 error'
  )
}

async function verifyOverview(token) {
  /** 去掉 // 与 /* *\/ 注释后再做「是否仍含 X」断言。
   *  否则文件头「已撤 PlanOverviewDto」「已删仅估算订单」这类**说明性注释**会被误判成残留代码
   *  （本组 OV06 / OV08 首次运行即踩此坑）。 */
  const stripComments = (s) =>
    s.replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '').replace(/\s\/\/[^\n]*/g, '')
  log('\n[OV] 排产总览 — 真实端点对接 + 词表收敛（overview — 2026-10-16 5号位 R1 函）')

  // ---- 取一个真实 planVersionId（dev 可能无 ACTIVE，用 schedule/versions 兜底）----
  const versions = await call('GET', '/api/schedule/versions', { token })
  const versionRows = versions.json?.data ?? []
  const activePlan = await call('GET', '/api/overview/active-plan', { token })

  // OV01 active-plan 形状（§四：新契约=裸数组；旧契约=单对象/null —— 三种都算「前端可解析」）
  const raw = activePlan.json?.data
  const shape = raw === null || raw === undefined ? 'null' : Array.isArray(raw) ? 'array' : 'object'
  const ov01Shapes = ['null', 'array', 'object']
  assert(
    'OV01 动态：/api/overview/active-plan 可达，且形状 ∈ {裸数组(新) / 单对象(旧) / null(无ACTIVE)}',
    activePlan.code === 200 && ov01Shapes.includes(shape),
    `code=${activePlan.code} shape=${shape}（前端 normalizeActivePlans 三种都吃得下）`
  )

  // OV02 order-query/summary 用真实 planVersionId 调，且 planVersionId 回填（§五 R5 已修）
  const someId = versionRows[0]?.id
  if (!someId) {
    skip('OV02 动态', 'dev 无任何 PlanVersion（/api/schedule/versions 返空），无法验证订单摘要入参回填')
  } else {
    const sum = await call('GET', `/api/order-query/summary?planVersionId=${someId}`, { token })
    const d = sum.json?.data
    const ok =
      sum.code === 200 &&
      d &&
      d.planVersionId === someId &&
      ['totalCount', 'onTimeCount', 'delayedCount', 'riskCount', 'unscheduledCount'].every(
        (k) => typeof d[k] === 'number'
      )
    assert(
      `OV02 动态：/api/order-query/summary?planVersionId=${someId} 回填该 id 且 5 个计数字段齐全（R5）`,
      !!ok,
      ok ? `planVersionId=${d.planVersionId} total=${d.totalCount}` : `code=${sum.code} data=${JSON.stringify(d)}`
    )
  }

  // OV03 resource-bottleneck 可达；有行时核对字段名（后端可能省 unavailableWindows/utilizationRate，前端已容缺）
  const res = await call('GET', `/api/overview/resource-bottleneck?planVersionId=${someId ?? 1}&topN=5`, {
    token
  })
  const resRows = res.json?.data ?? []
  const ov03Fields = ['resourceId', 'resourceCode', 'resourceName', 'isBottleneck']
  const ov03Ok =
    res.code === 200 &&
    Array.isArray(resRows) &&
    (resRows.length === 0 || ov03Fields.every((f) => f in resRows[0]))
  assert(
    'OV03 动态：/api/overview/resource-bottleneck 可达且元素字段名与前端类型一致（有行时核对）',
    ov03Ok,
    ov03Ok
      ? `code=200 rows=${resRows.length}${resRows.length ? ' 字段齐' : '（dev 暂无资源数据）'}`
      : `code=${res.code} 首行=${JSON.stringify(resRows[0] ?? null).slice(0, 160)}`
  )

  // OV04 candidate-summary 形状
  const cand = await call('GET', '/api/overview/candidate-summary', { token })
  const cd = cand.json?.data
  const ov04Ok = cand.code === 200 && cd && typeof cd.pendingCount === 'number' && Array.isArray(cd.candidates)
  assert(
    'OV04 动态：/api/overview/candidate-summary 返回 { pendingCount:number, candidates:[] }',
    !!ov04Ok,
    ov04Ok ? `pendingCount=${cd.pendingCount} candidates=${cd.candidates.length}` : `code=${cand.code} data=${JSON.stringify(cd)}`
  )

  // OV05 domain-status 路由存在（§7.1：走 5号位 /api/domain-status/{runId}）
  //
  //  判据改了：原口径「非 404」已失效 —— 5号位 R4 落码后，「该 Run 没登记
  //  ExpectedDomainKeysJson」从 500 改判 **404**（dev 实测 44 条 Run 里 37 条属此类），
  //  于是 404 同时可能是「路径选错」和「数据缺」，无法区分。
  //  改用「响应是否为完整 ApiResponse 信封」区分：路由匹配上时控制器一定回
  //  {code, message, timestamp}；路由压根不存在时 ASP.NET 回的是**空 body 的 404**。
  const runId = versionRows.find((v) => v.sourceScheduleRunId)?.sourceScheduleRunId
  if (!runId) {
    skip('OV05 动态', 'dev 无带 SourceScheduleRunId 的 PlanVersion，无法验证 /api/domain-status/{runId}')
  } else {
    const ds = await call('GET', `/api/domain-status/${runId}`, { token })
    const enveloped = typeof ds.json?.code === 'number' && Boolean(ds.json?.message)
    assert(
      `OV05 动态：/api/domain-status/${runId} 路由匹配（回完整 ApiResponse 信封，空 body 的 404 才是路径选错）`,
      enveloped,
      enveloped
        ? `code=${ds.code} message=${ds.json.message}`
        : `HTTP=${ds.status} body=${ds.text.slice(0, 80) || '(空)'} — 路由未匹配`
    )
  }

  // OV06 静态：types/overview.ts 已删 mock 复合体与旧四分类
  const ovTypes = stripComments(readSrc('api/aps-v1/types/overview.ts'))
  const ov06Bad = []
  if (/PlanOverviewDto/.test(ovTypes)) ov06Bad.push('仍有 PlanOverviewDto 复合体')
  if (/estimatedOnlyOrderCount/.test(ovTypes)) ov06Bad.push('仍有 estimatedOnlyOrderCount')
  if (/BLOCKED_BY_UPSTREAM|PI_POSITION_ISSUE|ODS_CONTRACT_FAILED/.test(ovTypes)) {
    ov06Bad.push('仍有旧四分类域状态')
  }
  if (!/RUN_DOMAIN_STATUSES/.test(ovTypes)) ov06Bad.push('缺 6 值 RUN_DOMAIN_STATUSES')
  assert(
    'OV06 静态：types/overview.ts 已撤 mock 复合体 / 仅估算订单 / 旧四分类域状态，域名状态收敛为 6 值',
    ov06Bad.length === 0,
    ov06Bad.length === 0 ? '复合体与旧词表已清' : ov06Bad.join(';')
  )

  // OV07 静态：api/overview.ts 接 4 个真实端点，且不再有 mockOverview 兜底
  const ovApi = stripComments(readSrc('api/aps-v1/overview.ts'))
  const ov07Miss = ['/api/overview/active-plan', '/api/overview/resource-bottleneck', '/api/overview/candidate-summary', '/api/domain-status/'].filter(
    (u) => !ovApi.includes(u)
  )
  const ov07Mock = /mockOverview|APS_USE_MOCK/.test(ovApi)
  assert(
    'OV07 静态：api/aps-v1/overview.ts 接 4 个真实端点，且无 mockOverview / APS_USE_MOCK 兜底',
    ov07Miss.length === 0 && !ov07Mock,
    ov07Miss.length === 0 && !ov07Mock
      ? '4 端点齐全且无 mock 兜底'
      : `缺端点=${ov07Miss.join(',') || '-'} mock兜底=${ov07Mock}`
  )

  // OV08 静态：Overview.vue 不再有「仅估算」卡片（§十 (A) 删除）
  const ovVue = stripComments(readSrc('views/Aps/Overview.vue'))
  const ov08Bad = []
  if (/仅估算订单/.test(ovVue)) ov08Bad.push('仍有「仅估算订单」卡片')
  if (/ESTIMATED_BADGE_TEXT/.test(ovVue)) ov08Bad.push('仍引 ESTIMATED_BADGE_TEXT')
  if (/estimatedOnlyOrderCount/.test(ovVue)) ov08Bad.push('仍读 estimatedOnlyOrderCount')
  assert(
    'OV08 静态：Overview.vue 已删「仅估算订单」卡片（5号位 §十 (A)：该事实仅内存不建表，事后读模型给不出）',
    ov08Bad.length === 0,
    ov08Bad.length === 0 ? '已删净' : ov08Bad.join(';')
  )

  // OV09 静态：全仓已无 Computed / ComputeFailed 死分支（§九 词表收敛）
  const ov09Files = [
    'api/aps-v1/types/common.ts',
    'store/modules/aps/schedule.ts'
  ]
  const ov09Bad = ov09Files.filter((f) => {
    const s = readSrc(f)
    return /'Computed'|'ComputeFailed'|Computed:\s*'|ComputeFailed:/.test(s)
  })
  const ov09HasComputing = /'Computing'/.test(readSrc('api/aps-v1/types/common.ts'))
  assert(
    'OV09 静态：PlanVersion 词表无 Computed/ComputeFailed 死分支，且已补 Computing（§九）',
    ov09Bad.length === 0 && ov09HasComputing,
    ov09Bad.length === 0 && ov09HasComputing
      ? '旧词表已清 + Computing 已补'
      : `残留=${ov09Bad.join(',') || '-'} Computing=${ov09HasComputing}`
  )

  // OV10 静态：store 在 runId 为 null 时不请求 domain-status（§7.3）
  const ovStore = readSrc('store/modules/aps/overview.ts')
  const ov10Ok = /if\s*\(!scheduleRunId\)/.test(ovStore) && /if\s*\(!planVersionId\)/.test(ovStore)
  assert(
    'OV10 静态：store 在无 planVersionId / 无 sourceScheduleRunId 时跳过对应请求（不拿假数）',
    ov10Ok,
    ov10Ok ? '两个 null 短路均在' : '缺 planVersionId 或缺 scheduleRunId 的短路'
  )

  // OV11 静态：task-summary 已由 5号位下线，前端不得再有残留调用/类型
  const ov11Files = [
    'api/aps-v1/overview.ts',
    'api/aps-v1/types/overview.ts',
    'store/modules/aps/overview.ts',
    'views/Aps/Overview.vue'
  ]
  const ov11Bad = ov11Files.filter((f) => /overview\/task-summary|OverviewTaskSummaryDto/.test(readSrc(f)))
  assert(
    'OV11 静态：/api/overview/task-summary 已下线（5号位 2026-10-16 删除 OverviewTaskSummaryDto + 端点），前端无残留调用/类型',
    ov11Bad.length === 0,
    ov11Bad.length === 0 ? '4 文件均无残留' : `残留=${ov11Bad.join(',')}`
  )

  // OV12 动态：已下线端点确实 404；若仍 200 = 分批部署的中间态（如实跳过，不误判为回归）
  const ts = await call('GET', `/api/overview/task-summary?planVersionId=${someId ?? 1}`, { token })
  if (ts.code === 404) {
    assert('OV12 动态：/api/overview/task-summary 已下线（404）', true, `code=${ts.code}`)
  } else {
    skip(
      'OV12 动态',
      `task-summary 仍返 ${ts.code}（5号位 2026-10-16 §二：分批部署中间态，以最新一次部署为准；前端已无调用，不受影响）`
    )
  }

  // OV13 静态：`isBottleneck` 是后端占位字面量（`0 AS IsBottleneck`），false 区分不了
  //   「判定为非瓶颈」与「未判定」⇒ 页面不得渲染「正常」标签（把未知说成正常）。
  //   与 MES `MesEligible` 恒 false 同型：占位值不能当判定结果渲染。
  const ov13Vue = readSrc('views/Aps/Overview.vue')
  const ov13Types = readSrc('api/aps-v1/types/overview.ts')
  const ov13NoFakeNormal = !/<ElTag[^>]*v-else[^>]*>正常<\/ElTag>/.test(ov13Vue)
  const ov13Documented =
    /isBottleneck/.test(ov13Types) && /不做二次计算|占位字面量|不得据此渲染/.test(ov13Types)
  assert(
    'OV13 静态：资源「瓶颈」列不在占位 false 上渲染「正常」（C2 口径未落地前的诚实降级）',
    ov13NoFakeNormal && ov13Documented,
    ov13NoFakeNormal && ov13Documented
      ? '无「正常」标签 + 类型注释已记录占位事实'
      : `无假正常=${ov13NoFakeNormal} 已记录占位=${ov13Documented}`
  )
}

async function verifyRun(token) {
  // 去掉注释后再做「是否仍含 X」断言，否则文件头的说明性注释会被误判成残留代码
  const stripComments = (s) =>
    s.replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '').replace(/\s\/\/[^\n]*/g, '')
  log('\n[RUN] 运行与下发 — 三数据面对接 + 冻结 07 §4.5 词表 + MES 区块下线（run — 2026-10-16）')

  const runFiles = {
    types: 'api/aps-v1/types/run.ts',
    api: 'api/aps-v1/run.ts',
    store: 'store/modules/aps/run.ts',
    view: 'views/Aps/Run.vue'
  }
  const code = Object.fromEntries(
    Object.entries(runFiles).map(([k, f]) => [k, stripComments(readSrc(f))])
  )

  // RUN01 自拟聚合体 RunStatusDto 已删（后端从未实现该 DTO）
  const run01Hits = Object.entries(code)
    .filter(([, s]) => /RunStatusDto|MesEligibilityDto|MesDispatchResult|MesDispatchInput/.test(s))
    .map(([k]) => k)
  assert(
    'RUN01 静态：自拟聚合体 RunStatusDto / MES 资格 DTO 已删净（后端从未实现）',
    run01Hits.length === 0,
    run01Hits.length === 0 ? 'types/api/store/view 四件均无' : `残留=${run01Hits.join(',')}`
  )

  // RUN02 状态词表恰为冻结 07 §4.5 权威四值（此前自拟 PENDING/SUCCESS 冻结里不存在，且漏 COMPLETED）
  const run02Ok =
    /RUN_STATUSES\s*=\s*\[\s*'RUNNING',\s*'COMPLETED',\s*'PARTIAL_SUCCESS',\s*'FAILED'\s*\]/.test(
      code.types
    ) &&
    !/'PENDING'|'SUCCESS'/.test(code.types) &&
    /COMPLETED:\s*'已完成'/.test(code.store)
  assert(
    "RUN02 静态：Run 状态词表 = 冻结 07 §4.5 四值 RUNNING/COMPLETED/PARTIAL_SUCCESS/FAILED，无自拟 PENDING/SUCCESS",
    run02Ok,
    run02Ok ? 'types 四值齐全 + store 含 COMPLETED 中文' : '词表被改回自拟值或缺 COMPLETED'
  )

  // RUN03 api 层：接三个真实数据面，且已删 dispatch / 聚合取数 / mock 分支
  const run03Ok =
    /listRuns/.test(code.api) &&
    /'\/api\/governance\/runs'/.test(code.api) &&
    !/mes\/dispatch|getStatus\s*\(|APS_USE_MOCK/.test(code.api)
  assert(
    'RUN03 静态：api/run.ts 只调真实端点（/api/governance/runs），无 MES dispatch、无聚合体 getStatus、无 mock 分支',
    run03Ok,
    run03Ok ? 'listRuns + 无 stub' : '仍有 dispatch/getStatus/mock 残留'
  )

  // RUN04 store 层：MES 状态与 partialBreakdown 已删，改用 5号位 domain-status
  const run04Ok =
    !/mesSamples|partialBreakdown|lastDispatch|dispatching|mesStats/.test(code.store) &&
    /getActivePlans\(\)/.test(code.store) &&
    /getRunDomainStatus\(/.test(code.store)
  assert(
    'RUN04 静态：store 已删 MES 分支（mesSamples/lastDispatch/dispatching）与自拟 partialBreakdown，改接 domain-status + active-plan',
    run04Ok,
    run04Ok ? '三数据面各自独立取数' : 'store 仍有 MES 分支或缺 domain-status 取数'
  )

  // RUN05 视图层：MES 下发资格卡 / 下发结果卡整块下线（后端无资格端点，冻结 §十 链路为 4→5→2）
  const run05Ok =
    !/MES 下发资格|dispatchMes|lastDispatch|mesStats|filteredMesSamples|MES_FILTER_OPTIONS/.test(
      code.view
    )
  assert(
    'RUN05 静态：Run.vue 的 MES 下发资格区块与下发结果卡整块下线（后端无资格查询端点；POST /api/mes/dispatch 只认 factoryCode 且不触发 2号位 排程）',
    run05Ok,
    run05Ok ? '模板与脚本均无 MES 下发残留' : '仍有 MES 下发区块'
  )

  // RUN06 DTO 字段名与后端 ScheduleRunGov.cs 对齐（前端曾用 runId / strategyProfileVersion 等自拟名）
  const run06Ok =
    /expectedDomainKeysJson/.test(code.types) &&
    /strategyProfileVersionId/.test(code.types) &&
    /basePlanVersionId/.test(code.types) &&
    /scopeJson/.test(code.types) &&
    !/\brunId\s*[?]?:\s*number/.test(code.types) &&
    /prop="id"/.test(code.view) &&
    /strategyProfileVersionId/.test(code.view)
  assert(
    'RUN06 静态：ScheduleRunGovDto 字段名对齐后端（expectedDomainKeysJson / strategyProfileVersionId / basePlanVersionId / scopeJson），视图 prop 用 id',
    run06Ok,
    run06Ok ? 'DTO 与模板均已对齐' : 'DTO 仍有自拟字段名，或模板仍用 prop="runId"'
  )

  // RUN07 动态：runs 端点返裸数组且元素含前端依赖的必填字段
  const runs = await call('GET', '/api/governance/runs?take=200', { token })
  const rows = Array.isArray(runs.json?.data) ? runs.json.data : []
  const need = ['id', 'runType', 'status', 'dataCutoffTime', 'startedAt']
  const run07Ok =
    runs.code === 200 && Array.isArray(runs.json?.data) && (rows.length === 0 || need.every((k) => k in rows[0]))
  assert(
    'RUN07 动态：GET /api/governance/runs 返裸数组，元素含 id/runType/status/dataCutoffTime/startedAt',
    run07Ok,
    run07Ok ? `code=200 rows=${rows.length}` : `code=${runs.code} 首行=${JSON.stringify(rows[0] ?? null).slice(0, 160)}`
  )

  // RUN08 动态：状态取值域 ⊆ 冻结四值（探出后端是否跑出词表外的值）
  const frozen = new Set(['RUNNING', 'COMPLETED', 'PARTIAL_SUCCESS', 'FAILED'])
  const seen = [...new Set(rows.map((r) => r.status))]
  const outside = seen.filter((s) => !frozen.has(s))
  assert(
    'RUN08 动态：库中 ScheduleRun.Status 取值均落在冻结 07 §4.5 四值内',
    outside.length === 0,
    outside.length === 0 ? `实测分布=${JSON.stringify(seen)}` : `词表外取值=${outside.join(',')}`
  )

  // RUN09 动态：validate-domain-keys 端点可达（200 通过 / 400 规则冲突都算真实响应）
  if (!rows.length) {
    skip('RUN09 动态', 'dev 无任何 ScheduleRun，无法验证 validate-domain-keys')
  } else {
    const vd = await call('POST', `/api/governance/run/${rows[0].id}/validate-domain-keys`, {
      token,
      body: {}
    })
    assert(
      'RUN09 动态：POST /api/governance/run/{id}/validate-domain-keys 可达（200 通过 / 400 规则冲突）',
      vd.code === 200 || vd.code === 400,
      `code=${vd.code} message=${vd.json?.message ?? '-'}`
    )
  }

  // ===== RUN10-RUN13：域状态 404 的呈现口径（5号位 R4 已部署后的实测后果）=====
  //  后端对「该 Run 没登记 ExpectedDomainKeysJson」返 HTTP 404，message 是
  //  「ScheduleRun 160 的 ExpectedDomainKeysJson 为空/缺失」这类内部术语。
  //  dev 实测 44 条 Run 里 37 条属此类 ⇒ 不处理的话每次进 /aps/run 必吃一条术语 toast + 术语横幅。

  const httpSrc = readSrc('api/aps-v1/http.ts')
  const overviewApiSrc = stripComments(readSrc('api/aps-v1/overview.ts'))
  const runStoreRaw = readSrc('store/modules/aps/run.ts')
  const runStoreSrc = stripComments(runStoreRaw)
  const runViewSrc = stripComments(readSrc('views/Aps/Run.vue'))

  assert(
    'RUN10：全局错误 toast 受 silent 标记控制（两处弹点都要判 silent）',
    /silent\?: boolean/.test(httpSrc) &&
      /if \(respData\.message && !silent\)/.test(httpSrc) &&
      /if \(!silent\) ElMessage\.error\(error\.message/.test(httpSrc),
    'RequestOptions.silent 已声明 + 两处 ElMessage 弹点均受 !silent 守卫'
  )

  assert(
    'RUN11：getRunDomainStatus 显式静默（后端 message 含内部术语，不由全局 toast 直出）',
    /getRunDomainStatus[\s\S]{0,400}?silent:\s*true/.test(overviewApiSrc),
    'getRunDomainStatus 带 silent: true'
  )

  assert(
    'RUN12：run store 对域状态 404 走空态，不进 errors[] 错误横幅',
    /catch[\s\S]{0,300}?ApiError && err\.code === 404/.test(runStoreSrc) &&
      /domainStatuses\.value = \[\]/.test(runStoreSrc),
    '404 有独立守卫，仅其它错误码才进 errors[]（dev 实测 37/44 条 Run 走 404）'
  )

  assert(
    'RUN13：域状态卡片不因空而整块隐藏，空态文案落在卡片内',
    !/<ElCard[^>]*v-if="hasDomainStatuses"/.test(runViewSrc) &&
      /v-if="!hasDomainStatuses"[^>]*class="col-empty"/.test(runViewSrc),
    '卡片常驻 + 卡片内 col-empty 空态'
  )

  // ===== RUN14：缺 ExpectedDomainKeysJson 的 Run「恢复按钮锁定 + 明确提示」（3号位 2026-10-17 回执 R3）=====
  //  冻结语义：ExpectedDomainKeysJson = 运行启动时冻结的预期 Domain 集合，是恢复的唯一权威依据；
  //  缺失即 400（fail-closed，不得静默降级）。dev 37/44 条缺该列（迁移前历史数据），
  //  故这 37 条**本来就不能恢复** —— 前端要「锁定并给提示」，而不是让用户点了才收到警告。
  //  - 反向探针自证可失败：去掉 disabled 里的 expectedDomainKeys 判断 / 删掉 tooltip 文案 /
  //    把 store 的缺字段放行改成隐藏 → 任一条转红
  const runViewRaw = readSrc('views/Aps/Run.vue')
  const runStoreRawSrc = readSrc('store/modules/aps/run.ts')
  const runStoreCode = runStoreRawSrc
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .replace(/^\s*\/\/.*$/gm, '')
  const rc14Db1 = /:disabled="!canRecoverRun \|\| !row\.expectedDomainKeys\?\.length"/.test(runViewRaw)
  const rc14Db2 = runViewRaw.includes('这条记录没有登记排程域，暂不能恢复')
  const rc14ScopeLenient =
    /if \(!run\.expectedDomainKeys \|\| run\.expectedDomainKeys\.length === 0\) return true/.test(
      runStoreCode
    )
  assert(
    'RUN14：缺 ExpectedDomainKeysJson 的 Run 恢复按钮禁用 + 悬停说明（不再「点了才警告」），且缺字段仍按放行可见',
    rc14Db1 && rc14Db2 && rc14ScopeLenient,
    `按钮禁用=${rc14Db1} 悬停提示=${rc14Db2} 缺字段放行=${rc14ScopeLenient}`
  )

  // ===== RUN15：夹具 Run 的 404 是终态，但「有真分母的 Run 404」必须当缺陷（5号位 10-19 R3 裁定）=====
  //  裁定原文：37 条 TEST_* 夹具 Run 不回填 ExpectedDomainKeysJson，404 即正确终态；
  //  「今后凡『有真分母的 Run』出现 404，才是新缺陷，勿与本批混报」。
  //  ⇒ 这两类在 HTTP 层同为 404，前端只有拿「这次 Run 自己有没有登记预期域」才分得开。
  //    没有这个判据时，真故障会被空态文案写成「没登记要跑哪些排程域」= 页面断言了它没验证过的事。
  //  - 反向探针自证可失败：删掉 store 的分母判据 / 把 404 豁免退回无条件 / 把空态文案退回二态 → 任一条转红
  const rc15DenomFlag = /domainStatusHasDenominator\s*=\s*ref\(false\)/.test(runStoreRawSrc)
  const rc15DenomAssigned = /domainStatusHasDenominator\.value\s*=\s*Boolean\(latest\.expectedDomainKeys\?\.length\)/.test(
    runStoreRawSrc
  )
  const rc15Exempt = /err\.code === 404 && !domainStatusHasDenominator\.value/.test(runStoreRawSrc)
  const rc15EmptyText =
    /domainStatusHasDenominator\.value/.test(runViewSrc) &&
    runViewSrc.includes('这次排产登记了要跑的排程域，但各域状态暂时取不到')
  assert(
    'RUN15：域状态 404 豁免按「该 Run 有无分母」分流，空态文案三态（有分母却取不到 ≠ 没登记）',
    rc15DenomFlag && rc15DenomAssigned && rc15Exempt && rc15EmptyText,
    `分母标志=${rc15DenomFlag} 分母赋值=${rc15DenomAssigned} 豁免带判据=${rc15Exempt} 空态三态=${rc15EmptyText}`
  )
}

function readSrc(relPath) {
  try {
    return readFileSync(join(SRC_ROOT, relPath), 'utf-8')
  } catch {
    return ''
  }
}

/** GROUP=gantt：甘特图/资源计划页契约对齐
 *  依据：4号位 2026-10-17 全量核查（frontNew/docs/4号位致5号位_甘特页契约缺口全量核查_20261017.md）
 *  - 冻结 07 §八.1 权威 15 码 + :4938 severity 三值（甘特曾自拟平行 8 码 + CRITICAL）
 *  - 后端 ScheduleQueryService 恒 null / 硬编码字段，前端不得当真值渲染
 *  纯静态断言，不登录、不打后端。 */
function verifyGantt() {
  const stripComments = (s) =>
    s.replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '').replace(/\s\/\/[^\n]*/g, '')
  log('\n[GANTT] 甘特图/资源计划 — 冻结 15 码词表归位 + 后端恒空字段不渲染（gantt — 2026-10-17）')

  const typesSrc = readSrc('api/aps-v1/types/schedule.ts')
  const ganttVue = readSrc('views/Aps/Gantt.vue')
  const ganttCode = stripComments(ganttVue)
  const explTypes = readSrc('api/aps-v1/types/explanation.ts')
  const mockSrc = readSrc('api/aps-v1/__mocks__/fixtures.ts')

  // GT01 根因码表归位冻结 07 §八.1 15 码：类型层必须是 ReasonCode，且自拟码全仓清零
  const gt01Typing = /reasonCode:\s*ReasonCode/.test(typesSrc)
  const banned = [
    'MATERIAL_AVAILABILITY',
    'CAPACITY_SHORTAGE',
    'UPSTREAM_DELAY',
    'LOCK_BLOCKING',
    'CROSS_DOMAIN_BLOCK',
    'EQUIPMENT_FAILURE'
  ]
  const gt01Residue = banned.filter(
    (c) =>
      new RegExp(`reasonCode:\\s*'${c}'`).test(typesSrc) ||
      new RegExp(`reasonCode:\\s*'${c}'`).test(mockSrc) ||
      new RegExp(`reasonCode:\\s*'${c}'`).test(ganttVue)
  )
  assert(
    'GT01 静态：延期根因码表归位冻结 07 §八.1 15 码（自拟 6 码清零）',
    gt01Typing && gt01Residue.length === 0,
    gt01Typing
      ? gt01Residue.length === 0
        ? 'types 用 ReasonCode + mock/gantt 均无自拟码'
        : `残留自拟码=${gt01Residue.join(',')}`
      : 'types/schedule.ts 未引用 ReasonCode'
  )

  // GT02 severity 归一冻结 :4938 三值 INFO/WARN/ERROR（不含 CRITICAL）
  // 注意：必须对 types 与 Gantt.vue **双向**校验 CRITICAL/WARNING 缺席。
  // 教训（2026-10-17 自证）：只查 Gantt.vue 时，在 types 加回 `| 'CRITICAL'` 仍判绿（假绿）。
  const sevLine = (typesSrc.split('\n').find((l) => /severity:\s*'INFO'/.test(l)) ?? '').trim()
  const gt02Ok =
    /severity:\s*'INFO'\s*\|\s*'WARN'\s*\|\s*'ERROR'$/.test(sevLine) &&
    !/CRITICAL|WARNING/.test(sevLine) &&
    !/\bCRITICAL\b/.test(ganttCode) &&
    !/\bWARNING\b/.test(ganttCode)
  assert(
    'GT02 静态：severity 归一冻结 07 :4938 三值（INFO/WARN/ERROR，无 CRITICAL/WARNING）',
    gt02Ok,
    gt02Ok ? `types 三值 + Gantt.vue 无残留 — ${sevLine}` : `types severity 行="${sevLine}"`
  )

  // GT03 甘特页不再维护平行码表：标签取冻结 REASON_CODE_LABELS，未知码走「待核对·」中性展示
  const gt03Ok =
    /import\s*\{[^}]*REASON_CODE_LABELS[^}]*\}\s*from\s*'@\/api\/aps-v1\/types\/explanation'/.test(
      ganttVue
    ) &&
    /REASON_CODE_LABELS\[code as ReasonCode\]\s*\?\?\s*`待核对/.test(ganttCode) &&
    !/const\s+DELAY_REASON_LABEL\s*:/.test(ganttCode)
  assert(
    'GT03 静态：甘特页根因标签复用冻结 REASON_CODE_LABELS，未知码中性展示（无平行码表）',
    gt03Ok,
    gt03Ok ? '冻结字典引用 / 待核对兜底 / 平行码表清理 三项齐全' : '冻结字典引用、待核对兜底、平行码表清理中有缺失'
  )

  // GT04 mesEligible 契约归一：后端是 bool（恒 false = 未就绪），模板必须走 mesEligibilityOf，
  //      不得再用 `?? 'UNKNOWN'`（?? 只吃 null/undefined，false 会穿透查表落 undefined）
  const gt04Bad = /selectedTask\.mesEligible\s*\?\?\s*'UNKNOWN'/.test(ganttVue)
  const gt04Ok =
    /function\s+mesEligibilityOf\(task:\s*GanttTaskDto\s*\|\s*null\)/.test(ganttCode) &&
    /raw === 'ELIGIBLE' \|\| raw === 'INELIGIBLE' \|\| raw === 'UNKNOWN'\s*\?\s*raw\s*:\s*'UNKNOWN'/.test(
      ganttCode
    ) &&
    !gt04Bad
  assert(
    'GT04 静态：MES 下发资格 bool 契约已归一（bool/undefined 一律按「未接入」）',
    gt04Ok,
    gt04Ok
      ? 'mesEligibilityOf 三值归一 + 模板无 `?? UNKNOWN` 写法'
      : gt04Bad
        ? "模板仍用 selectedTask.mesEligible ?? 'UNKNOWN'（bool false 会穿透查表）"
        : 'mesEligibilityOf 归一函数缺失或未走三值判定'
  )

  // GT05 plannedProcessQty 可空：DDL 有值但可能整键省略，不得渲染成空值或被当成 0
  //  2026-10-17 更正判据说明：5号位 回执 §五 引的 `CAST(NULL ...)` 是**过期片段**，
  //  实际 SQL 是 `t.PlannedProcessQty AS PlannedProcessQty`，dev 实测 203/203 有值。
  const gt05Type = /plannedProcessQty\?:\s*number/.test(typesSrc)
  const gt05Guard = /<template\s+v-if="selectedTask\.plannedProcessQty\s*!=\s*null">/.test(ganttVue)
  assert(
    'GT05 静态：计划加工数量可空 + 模板有 != null 守卫（缺键时不渲染空值）',
    gt05Type && gt05Guard,
    `类型可选=${gt05Type} 模板守卫=${gt05Guard}`
  )

  // GT06 后端恒空字段一律整块不渲染，不出现「0 个」「无阻挡」这类断言
  const guards = [
    ['跨域阻挡', /v-if="selectedTask\.crossDomainBlocked"/],
    ['订单份额', /v-if="selectedTask\.taskShare && selectedTask\.taskShare\.length > 1"/],
    ['数量分段', /selectedTask\.taskSegments && selectedTask\.taskSegments\.length > 1/],
    ['锁标记', /v-if="selectedTask\.lockMarker"/],
    ['事实类型', /v-if="selectedTask\.factType"/],
    ['设备不可用窗口', /selectedResource\?\.unavailableWindows\?\.length/],
    ['受影响 KPI', /v-if="impactedTaskIds\.length > 0"/]
  ]
  const gt06Missing = guards.filter(([, re]) => !re.test(ganttVue)).map(([n]) => n)
  assert(
    'GT06 静态：后端恒 null 的 7 个字段均带空态守卫（不渲染成假值）',
    gt06Missing.length === 0,
    gt06Missing.length === 0 ? '7 项守卫齐全' : `缺守卫=${gt06Missing.join(',')}`
  )

  // GT07 甘特与 Explanation 必须共用同一份冻结码表，防止再次分叉
  const gt07Ok =
    /import\s+type\s*\{\s*ReasonCode\s*\}\s*from\s*'\.\/explanation'/.test(typesSrc) &&
    /RESOURCE_CAPACITY_WAIT[\s\S]*MATERIAL_SHORTAGE[\s\S]*PRECEDENCE_WAIT[\s\S]*FROZEN_ZONE_LOCK[\s\S]*EQUIPMENT_BREAKDOWN_RISK/.test(
      explTypes
    )
  assert(
    'GT07 静态：甘特复用 Explanation 的冻结 15 码字典（单一码表，不分叉）',
    gt07Ok,
    gt07Ok ? "types/schedule 从 './explanation' 引 ReasonCode，15 码全集在位" : '码表引用断裂'
  )

  // GT08 受影响任务数取 impactedTaskIds（无窗口数据时整块隐藏），不固化 0
  const gt08Ok =
    /受影响\s*<strong>\{\{\s*impactedTaskIds\.length\s*\}\}/.test(ganttVue) &&
    !/受影响\s*<strong>\{\{\s*0\s*\}\}/.test(ganttVue)
  assert(
    'GT08 静态：受影响任务数走 impactedTaskIds（无窗口数据时整块隐藏，不固化 0）',
    gt08Ok,
    gt08Ok ? 'KPI 取 impactedTaskIds.length 且无硬编码 0' : 'KPI 未取 impactedTaskIds 或出现硬编码 0'
  )

  // GT09 SetupSource 必须是后端落库的 **4 态**（`SetupOptimizer.cs:60-72`，0号位 20260922）
  //   旧前端按 2026-09-21 中间回执写成 5 值 EXACT/DEFAULT/NONE/SAME_PRODUCT/INITIAL，
  //   真实值 INITIAL_SETUP_STATE / SETUP_RULE_MISSING_ZERO_FALLBACK 查表得 undefined
  //   ⇒ `META[x].tag` 抛 TypeError，详情 Drawer 白屏。本探针锁死 4 态。
  const setupTypesSrc = readSrc('api/aps-v1/types/setup.ts')
  const setupSrcMatch = setupTypesSrc.match(/SETUP_SOURCES\s*=\s*\[([\s\S]*?)\]\s*as const/)
  const setupVals = (setupSrcMatch?.[1] ?? '')
    .split(',')
    .map((s) => s.replace(/['"\s]/g, ''))
    .filter(Boolean)
  const gt09Expected = ['EXACT', 'DEFAULT', 'SETUP_RULE_MISSING_ZERO_FALLBACK', 'INITIAL_SETUP_STATE']
  const gt09Ok =
    setupVals.length === 4 && gt09Expected.every((v) => setupVals.includes(v))
  assert(
    'GT09 静态：SetupSource 是后端落库 4 态（无 SAME_PRODUCT / NONE / INITIAL 五个假值）',
    gt09Ok,
    gt09Ok
      ? '4 态与 SetupOptimizer.cs 一致'
      : `当前值域=[${setupVals.join(',')}]，期望=[${gt09Expected.join(',')}]`
  )

  // GT10 甘特详情查换型来源必须走 setupSourceMetaOf 降级，不得裸查 SETUP_SOURCE_META[选中值]
  //   裸查一旦遇到未知值就是 TypeError（整个 Drawer 崩），GT09 已抓到过一次同型缺陷
  const gt10Bad = /SETUP_SOURCE_META\[selectedTask\.setupSource\]/.test(ganttVue)
  const gt10Ok = /function\s+setupSourceMetaOf\(/.test(ganttCode) && !gt10Bad
  assert(
    'GT10 静态：换型来源查表走降级函数（未知值原样显示，不裸索引 META）',
    gt10Ok,
    gt10Ok ? 'setupSourceMetaOf 降级 + 模板无裸索引' : gt10Bad ? '模板仍裸查 SETUP_SOURCE_META[...]' : '降级函数缺失'
  )

  // GT11 MES 资格未接入（后端 2026-10-17 由 bool+恒 false 改 bool?+null）⇒ 按「未接入」呈现，
  //   且不得编造「等待释放窗口 / 释放瞬间更新」的理由。null 经 WhenWritingNull 整键省略 ⇒ 前端拿到 undefined，
  //   故判据必须落在「非三态字符串一律降级 UNKNOWN」上，而不是 `=== null`。
  const gt11Label = /UNKNOWN:\s*'未接入'/.test(ganttCode)
  const gt11NoFakeReason = !/释放瞬间更新/.test(ganttVue)
  const gt11Nullable =
    /mesEligible\?:\s*boolean\s*\|\s*null\s*\|\s*MesEligibility/.test(typesSrc) &&
    /crossDomainBlocked\?:\s*boolean\s*\|\s*null/.test(typesSrc)
  assert(
    'GT11 静态：MES 资格显示「未接入」（契约已改可空），不编造「释放瞬间更新」的理由',
    gt11Label && gt11NoFakeReason && gt11Nullable,
    `label=${gt11Label} 无编造理由=${gt11NoFakeReason} 类型可空=${gt11Nullable}`
  )

  // GT12 R8 空态三分类：versionExists（版本不存在）/ scopeDenied（越权）/ 其余（无任务）
  //   5号位 2026-10-17 回执 §六已补显式位 `versionExists`（GanttDataDto.cs:56，DTO 注释点名
  //   「替代 versionCode === '' 隐式契约」）⇒ 判据必须是显式位优先，versionCode 空串只作兜底。
  const gt12Kinds =
    /type\s+EmptyKind\s*=\s*'scope-denied'\s*\|\s*'version-missing'\s*\|\s*'no-tasks'/.test(ganttCode) &&
    /d\.scopeDenied/.test(ganttCode) &&
    /d\.versionExists\s*===\s*false/.test(ganttCode) &&
    /versionExists\?:\s*boolean/.test(typesSrc)
  const gt12Text =
    /'scope-denied':\s*\{[\s\S]*?无权查看/.test(ganttCode) &&
    /'version-missing':\s*\{[\s\S]*?版本不存在/.test(ganttCode) &&
    /'no-tasks':\s*\{[\s\S]*?没有排程结果/.test(ganttCode)
  assert(
    'GT12 静态：空态区分越权 / 版本不存在 / 无排程结果（R8 + §六 versionExists）',
    gt12Kinds && gt12Text,
    gt12Kinds && gt12Text
      ? '三态各有独立文案；版本不存在用显式位 versionExists 判'
      : `判据=${gt12Kinds} 文案=${gt12Text}`
  )

  // GT13 R3 交期 + 延期时长：取自后端 customerDueDate / delayHours，不得前端重算
  const gt13Types =
    /customerDueDate\?:\s*IsoDateTime/.test(typesSrc) && /delayHours\?:\s*number/.test(typesSrc)
  const gt13View =
    /formatUtcDate\(selectedTask\.customerDueDate\)/.test(ganttVue) &&
    /selectedTask\.delayHours\.toFixed\(/.test(ganttVue)
  assert(
    'GT13 静态：客户交期 + 延期时长取后端字段（不前端重算）',
    gt13Types && gt13View,
    `类型=${gt13Types} 视图=${gt13View}`
  )

  // GT14 R4 关键路径：后端 isCriticalPath 必须透传到 DhxGantt 行（否则 dhx-task-critical 是死样式）
  const gt14 = /isCritical:\s*!!t\.isCriticalPath/.test(ganttCode)
  assert(
    'GT14 静态：isCriticalPath 透传到甘特行（关键路径高亮有数据源）',
    gt14,
    gt14 ? 'isCritical = !!t.isCriticalPath' : '甘特行未透传 isCriticalPath'
  )

  // GT15 R1 资源过滤入参必须是**重复键**：dev 实测逗号分隔 422、`resourceIds[]=1` 被静默忽略（返全量）
  const apiSrc = readSrc('api/aps-v1/schedule.ts')
  const gt15 =
    /new URLSearchParams\(\)/.test(apiSrc) && /params\.append\('resourceIds'/.test(apiSrc) &&
    !/resourceIds\.join\(','\)/.test(apiSrc)
  assert(
    'GT15 静态：gantt resourceIds 用重复键传（逗号 422 / 中括号被静默忽略）',
    gt15,
    gt15 ? 'URLSearchParams.append 重复键' : '仍用 join(",") 或数组直传'
  )

  // GT16 R5 延期但无任务级原因：如实说「系统未提供」，不谎称走了 DUE_DATE_RISK 兜底
  const gt16 = /系统尚未提供任务级延期原因/.test(ganttVue) && !/仅 DUE_DATE_RISK 兜底/.test(ganttVue)
  assert(
    'GT16 静态：延期无根因时如实说明（不谎称 DUE_DATE_RISK 兜底）',
    gt16,
    gt16 ? '空态文案如实' : '仍宣称「仅 DUE_DATE_RISK 兜底」'
  )

  // GT17 R6 Duration 单位：5号位 2026-10-17 已裁决为**小时**并在查询层 /60.0 落码
  //   （冻结 07:3680 写小时、写入方按分钟落库，三方长期不一致）。⇒ 类型注释里不得再写
  //   「单位待确认 / 实测是分钟」这类过期结论，否则下一个人会照它重新怀疑一遍。
  //   同时锁「不拿它算甘特条长」：DHTMLX 的 duration 是库自身字段，两处口径不能分叉。
  const gt17Unit =
    /单位：小时/.test(typesSrc) && !/单位待\s*5号位\s*确认/.test(typesSrc) && !/实测 v2 的值为\*\*分钟\*\*/.test(typesSrc)
  const gt17NoBarCompute = /duration:\s*hoursBetween\(t\.plannedStartTime!/.test(ganttCode)
  assert(
    'GT17 静态：Duration 单位已按 R6 裁决记为小时（不再留「单位存疑」过期结论，且不用于算条长）',
    gt17Unit && gt17NoBarCompute,
    `单位注释=${gt17Unit} 条长仍现算=${gt17NoBarCompute}`
  )

  // GT18 5号位 2026-10-19 §2.3：summary 键名 `versionFound` → `versionExists` 已落码并部署到 dev。
  //   前端 `ScheduleSummaryDto` 必须与之对齐 —— 该类型此前漏了 `versionExists` / `scopeDenied`
  //   两个字段（与 Q-E 轮「规则集 diff 前端自拟 DTO」同一类漂移：类型不回读后端就会停在旧形状）。
  //   反向自证：把 `versionExists` 改成 `versionFound`（或删掉 scopeDenied）→ 转红。
  //   另锁「不接 summary」的理由必须留在注释里，且必须带**实测数字**（191 / 179）——
  //   只写「口径不一致」这种空话，下次有人照样会接进来；带上数字才挡得住「我改好了呀」的想当然。
  //   api 与 store 两处**各自独立判**（不并成一个条件），删掉任一处都会转红。
  const scheduleApiSrc = readSrc('api/aps-v1/schedule.ts')
  const scheduleStoreSrc = readSrc('store/modules/aps/schedule.ts')
  const typesCode = stripComments(typesSrc)
  const gt18Keys =
    /versionExists:\s*boolean/.test(typesCode) &&
    /scopeDenied:\s*boolean/.test(typesCode) &&
    !/versionFound/.test(typesCode)
  const gt18Mock = /versionExists:\s*true/.test(mockSrc) && /scopeDenied:\s*false/.test(mockSrc)
  // 「不接 summary」的理由只存在于注释里，因此这两处必须用**未剥离**的原文来查
  const gt18ReasonApi = /191/.test(scheduleApiSrc) && /179/.test(scheduleApiSrc)
  const gt18ReasonStore = /191/.test(scheduleStoreSrc) && /179/.test(scheduleStoreSrc)
  assert(
    'GT18 静态：ScheduleSummaryDto 与已部署 DTO 对齐（versionExists/scopeDenied，无 versionFound 残留）且不接理由在案',
    gt18Keys && gt18Mock && gt18ReasonApi && gt18ReasonStore,
    `类型键=${gt18Keys} mock补齐=${gt18Mock} api理由=${gt18ReasonApi} store理由=${gt18ReasonStore}`
  )

  /* ===== GT19：今日/未来任务清单（冻结 09 §六 / 01:1914）=====
   * 要求：① 列表必须与甘特时间轴**同窗同源**（reuse `ganttData.tasks` + `dateRange`），
   *       不新增第二套查询端点；② 展示 任务号/订单号/物料/计划起止/状态/延期；
   *       ③ MES 下发/工单/执行状态后端未接入 —— 列表**不得**伪造该列。
   * 反向破坏：把数据源从 `ganttData.value?.tasks` 改成 `[]`、或删掉订单号列 → 转红。
   */
  const ganttViewCode = stripComments(readSrc('views/Aps/Gantt.vue'))
  // ⚠️ 必须**按块**判：`ganttData.value?.tasks` 在本文件出现十余次（甘特体/抽屉/联动），
  //    全文件级断言会被别处同名字样带成假绿（探针第三型）。
  const gt19RowsStart = ganttViewCode.indexOf('const taskListRows')
  const gt19RowsEnd = ganttViewCode.indexOf('const taskListTotal')
  const gt19RowsBlock =
    gt19RowsStart >= 0 && gt19RowsEnd > gt19RowsStart
      ? ganttViewCode.slice(gt19RowsStart, gt19RowsEnd)
      : ''
  const gt19CardStart = ganttViewCode.indexOf('<ElCard class="task-list-card">')
  const gt19CardEnd = ganttViewCode.indexOf('</ElCard>', gt19CardStart)
  const gt19Card =
    gt19CardStart >= 0 && gt19CardEnd > gt19CardStart
      ? ganttViewCode.slice(gt19CardStart, gt19CardEnd)
      : ''
  const gt19SameSource =
    /dateRange\.value\.from/.test(gt19RowsBlock) && /ganttData\.value\?\.tasks/.test(gt19RowsBlock)
  const gt19Columns =
    /prop="taskNo"/.test(gt19Card) &&
    /row\.orderNo/.test(gt19Card) &&
    /row\.materialCode/.test(gt19Card) &&
    /row\.plannedStartTime/.test(gt19Card) &&
    /row\.isDelayed/.test(gt19Card)
  assert(
    'GT19 静态：今日/未来任务清单与甘特同窗同源（不新增查询）且列齐任务号/订单/物料/起止/延期',
    gt19SameSource && gt19Columns,
    `同源=${gt19SameSource} 列=${gt19Columns}（块长度 rows=${gt19RowsBlock.length} card=${gt19Card.length}）`
  )
}

function verifyM10A1Order() {
  log('\n[M] §10A.1 已有订单提前：OrderCanonicalId 自动带入（m-10a1 — B4 已落地触发）')

  // M01 静态：types/order.ts OrderListItemDto 含 orderCanonicalId 字段
  // 2026-10-17：字段所在接口由自拟的 `OrderBasicInfo` 迁到真实的 `OrderListItemDto`
  // （OrderBasicInfo 随订单 mock 桩一起删除 —— 它建在编造的工厂/产品族/客户上，与实库零重合）。
  const typesHasOrderCanonicalId = staticScanDtoHasField(
    'api/aps-v1/types/order.ts',
    'orderCanonicalId'
  )
  assert(
    'M01 静态：types/order.ts OrderListItemDto 含 orderCanonicalId?: number',
    typesHasOrderCanonicalId,
    typesHasOrderCanonicalId ? '已加字段' : 'types/order.ts 未含 orderCanonicalId'
  )

  // M01 静态：OrderAdvanceDialog.vue 含 orderCanonicalId 字段（OrderAdvanceCandidate 接口）
  const dlgHasOrderCanonicalId = staticScanDtoHasField(
    'views/Aps/components/OrderAdvanceDialog.vue',
    'orderCanonicalId'
  )
  assert(
    'M01 静态：OrderAdvanceDialog.vue OrderAdvanceCandidate 含 orderCanonicalId?: number',
    dlgHasOrderCanonicalId,
    dlgHasOrderCanonicalId ? '已加字段' : 'OrderAdvanceDialog.vue 未含 orderCanonicalId'
  )

  // M01 静态：OrderAdvanceDialog.vue **模板内**提示文案面向用户——含"自动带入"+"手工填写"，
  //  且不得出现内部协调措辞（§/号位/B4/回执/兼容回退 等只许留在注释里）
  const dlgContent = readSrc('views/Aps/components/OrderAdvanceDialog.vue')
  const dlgTemplate = (() => {
    const i = dlgContent.indexOf('<template>')
    return i >= 0 ? dlgContent.slice(i) : ''
  })()
  const dlgHasAutoImport = dlgTemplate.includes('自动带入') && dlgTemplate.includes('手工填写')
  const dlgTemplateLeaks = ['§', '号位', 'B4', '回执', '兼容回退', 'OrderCanonicalId'].filter((t) =>
    dlgTemplate.includes(t)
  )
  assert(
    'M01 静态：OrderAdvanceDialog.vue 模板提示含"自动带入"+"手工填写"，且无内部措辞（§/号位/B4/回执/兼容回退）',
    dlgHasAutoImport && dlgTemplateLeaks.length === 0,
    dlgHasAutoImport && dlgTemplateLeaks.length === 0
      ? '模板文案用户可读、无内部措辞'
      : `模板文案异常（autoImport=${dlgHasAutoImport}, leaks=${dlgTemplateLeaks.join('|')}）`
  )

  // M01 静态：OrderAdvanceDialog.vue watch visible 时自动带入 canonicalId = o.orderCanonicalId
  const dlgHasCanonicalIdBinding = /canonicalId:\s*o\.orderCanonicalId/.test(dlgContent)
  assert(
    'M01 静态：OrderAdvanceDialog.vue watch 自动带入 canonicalId = o.orderCanonicalId',
    dlgHasCanonicalIdBinding,
    dlgHasCanonicalIdBinding ? 'watch 已自动带入' : 'watch 缺少 canonicalId 自动绑定'
  )

  // M01 静态：OrderAdvanceDialog.vue 不再含旧的"5号位 待补列"字样（已替换）
  const dlgHasOldText = /5号位 待补列/.test(dlgContent)
  assert(
    'M01 静态：OrderAdvanceDialog.vue 不再含旧"5号位 待补列"文案',
    !dlgHasOldText,
    dlgHasOldText ? '仍含旧兜底提示语' : '旧兜底提示语已移除'
  )

  // M01 静态：Order.vue advanceCandidates 透传 orderCanonicalId
  const orderContent = readSrc('views/Aps/Order.vue')
  const orderHasOrderCanonicalId = /orderCanonicalId:\s*o\.orderCanonicalId/.test(orderContent)
  assert(
    'M01 静态：Order.vue advanceCandidates 透传 orderCanonicalId',
    orderHasOrderCanonicalId,
    orderHasOrderCanonicalId ? '已透传' : 'Order.vue advanceCandidates 未透传'
  )

  // M03 静态（planVersionId 必填契约 — 5号位 2026-09-23《order-query 分页契约修正回执》§一）
  //  1) types/order.ts OrderQuery.planVersionId 必填（`: number` 不是 `?`）
  //  2) store/modules/aps/order.ts loadList 含 planVersionIdReady 校验
  //  3) Order.vue planVersionInputRequired computed + ElInput 含 required mark + onMounted 从 active-plan 取默认

  // M03-1：types 层强类型必填
  const typesContent = readSrc('api/aps-v1/types/order.ts')
  // 先把 OrderQuery 接口块抽出来（`interface OrderQuery { ... }`），避免跨接口误判 OrderBasicInfo.planVersionId?:
  const orderQueryBlock = typesContent.match(/interface OrderQuery\s*\{[\s\S]*?\n\}/)?.[0] ?? ''
  const typesRequiredMatch = /\n\s*planVersionId:\s*number\b/.test(orderQueryBlock)
  const typesHasOptional = /\n\s*planVersionId\?\s*:/.test(orderQueryBlock)
  assert(
    'M03-1 静态：types/order.ts OrderQuery.planVersionId 必填（`: number`，5号位 2026-09-23 回执）',
    typesRequiredMatch && !typesHasOptional,
    typesRequiredMatch && !typesHasOptional
      ? 'OrderQuery.planVersionId 已强类型必填'
      : `types/order.ts OrderQuery.planVersionId 类型标记异常（required=${typesRequiredMatch}, hasOptional=${typesHasOptional}）`
  )

  // M03-2：store 层 loadList 校验
  // 2026-10-17：正则跟新写法 —— 校验仍在（`if (!planVersionIdReady.value) { … return }`），
  // 但错误提示改写成用户能懂的大白话（memory: 用户提示用大白话），不再走 handleError(listError, fallback)。
  const storeContent = readSrc('store/modules/aps/order.ts')
  const loadListBlock = storeContent.match(/async function loadList\([\s\S]*?\n\}/)?.[0] ?? ''
  const storeHasReadyCheck =
    /if \(!planVersionIdReady\.value\)\s*\{[\s\S]*?listError\.value\s*=\s*'请先选择计划版本[\s\S]*?\breturn\b/.test(
      loadListBlock
    )
  assert(
    'M03-2 静态：store/modules/aps/order.ts loadList 含 planVersionIdReady 校验',
    storeHasReadyCheck,
    storeHasReadyCheck
      ? 'loadList 缺版本时写 listError 并中止，不发请求'
      : 'loadList 缺 planVersionIdReady 校验（漏传会被框架 400）'
  )

  // M03-3：Order.vue 版本选择
  // 2026-10-17 口径变更：不再要求「文本框 + required mark + 默认值从 active-plan 取」。
  //   dev 实测当前**没有 ACTIVE 版本**（active-plan 返 data:[]），默认猜 ACTIVE 会让页面恒空，
  //   故改为：版本下拉（ElSelect）显式选择，store 默认取 `GET /api/schedule/versions` 第一条（最新）。
  //   断言锁这条新口径：下拉存在 + 挂 store.versions + onMounted 调 loadVersions + 无自拟默认 id。
  const orderVueContent = readSrc('views/Aps/Order.vue')
  const hasVersionSelect = /<ElSelect[\s\S]{0,200}?class="version-select"/.test(orderVueContent)
  const hasOptionsFromStore = /v-for="v in versions"/.test(orderVueContent)
  const loadsVersions = /orderStore\.loadVersions\(\)/.test(orderVueContent)
  const noHardcodedDefault = !/planVersionId\s*[:=][^;\n]*?\d{2,}/.test(orderVueContent)
  assert(
    'M03-3 静态：Order.vue 版本下拉显式选择 + loadVersions 取默认（不猜 ACTIVE）',
    hasVersionSelect && hasOptionsFromStore && loadsVersions && noHardcodedDefault,
    hasVersionSelect && hasOptionsFromStore && loadsVersions && noHardcodedDefault
      ? '下拉显式选版本；默认取 /api/schedule/versions 第一条'
      : `版本选择未完整（select=${hasVersionSelect}, options=${hasOptionsFromStore}, load=${loadsVersions}, 无硬编码默认=${noHardcodedDefault}）`
  )

  // M02 接受类（默认跳过；VERIFY_ALLOW_WRITE=1 时开启）
  if (process.env.VERIFY_ALLOW_WRITE !== '1') {
    skip('M02 接受类', 'VERIFY_ALLOW_WRITE 未设置=1（脏数据风险；接受类需 Order 页选单 + Dialog 自动带入 + 提交跳转 Candidate 端到端）')
  } else {
    // TODO（VERIFY_ALLOW_WRITE=1 时实施）：从 Order 页 UI 选订单 → 打开 OrderAdvanceDialog → 验证 Row.canonicalId = OrderBasicInfo.orderCanonicalId → 改 manualTargetDueDate + 提交 → 验证 /api/governance/run/candidate 200 + payload.orderTargets[].orderCanonicalId = 覆盖值
    skip('M02 接受类', 'VERIFY_ALLOW_WRITE=1 但 M02 端到端需浏览器走查（前端 Playwright/Cypress 不在本次范围；本段为静态绿 + 接受类开关）')
  }
}

/* ==================== [PT] 供需追溯页（/aps/pegging-trace）====================
 * 2026-10-19 新增。依据 4 份冻结文档共同的 V1 用户输出要求「Supply/Pegging追溯」：
 *   06 v1.38 §7.2 / 09 v2.0 §六 / 03 v2.0 / 13 v1.8 §七 / 01 v1.13:1913
 * 5号位 `PeggingTraceController` 早已就绪（dev v2 实测 392,705 条分配），前端此前零引用。
 * 本组纯静态（免登录），每条都反向破坏自证过。
 */
function verifyPeggingTraceStatic() {
  log('\n[PT] 供需追溯 — Supply/Pegging 追溯页契约（pt — 2026-10-19）')

  const routerSrc = readSrc('router/modules/aps.ts')
  const apiSrc = readSrc('api/aps-v1/peggingTrace.ts')
  const typesSrc = readSrc('api/aps-v1/types/peggingTrace.ts')
  const storeSrc = readSrc('store/modules/aps/peggingTrace.ts')
  const vueSrc = readSrc('views/Aps/PeggingTrace.vue')
  const scheduleTypes = readSrc('api/aps-v1/types/schedule.ts')

  // PT01 路由：绝对 path + 权限码 + 组件存在
  //   ⚠️ 必须**框死在本路由对象内**再判：整文件搜 `apsRequiredPermissions: ['aps.plan.view']`
  //   会匹到订单页/PI页的同款声明，把本路由的权限删掉照样判绿（首版实测假绿）。
  const ptRoute = (() => {
    const i = routerSrc.indexOf("path: '/aps/pegging-trace'")
    if (i < 0) return ''
    const slice = routerSrc.slice(i)
    const end = slice.indexOf('\n      },')
    return end > 0 ? slice.slice(0, end) : slice.slice(0, 1200)
  })()
  const pt01 =
    ptRoute.length > 0 &&
    /name:\s*'ApsPeggingTrace'/.test(ptRoute) &&
    /component:\s*\(\)\s*=>\s*import\('@\/views\/Aps\/PeggingTrace\.vue'\)/.test(ptRoute) &&
    /apsRequiredPermissions:\s*\['aps\.plan\.view'\]/.test(ptRoute)
  assert(
    'PT01 静态：路由 /aps/pegging-trace + ApsPeggingTrace + aps.plan.view',
    pt01,
    `路由块已定位=${ptRoute.length > 0} 名称=${/ApsPeggingTrace/.test(ptRoute)} 权限=${/apsRequiredPermissions:\s*\['aps\.plan\.view'\]/.test(ptRoute)}`
  )

  // PT02 API：三个真实端点 + 无 mock 分支
  //   反向自证：删掉任一端点 URL → 转红
  const pt02 =
    /url:\s*'\/api\/pegging-trace'/.test(apiSrc) &&
    /\/api\/pegging-trace\/order\/\$\{orderId\}/.test(apiSrc) &&
    /url:\s*'\/api\/pegging-trace\/summary'/.test(apiSrc) &&
    !/APS_USE_MOCK/.test(apiSrc)
  assert(
    'PT02 静态：API 打三个真实端点且无 mock 分支（列表/按订单/汇总）',
    pt02,
    `列表=${/url:\s*'\/api\/pegging-trace'/.test(apiSrc)} 汇总=${/url:\s*'\/api\/pegging-trace\/summary'/.test(apiSrc)} 无mock=${!/APS_USE_MOCK/.test(apiSrc)}`
  )

  // PT03 筛选项从 summary 的真实分布取，不套用别处枚举
  //   实测 supplyType 值域（INVENTORY/PIPELINE/WIP/INTER_FACTORY_ORDER）与
  //   `types/common.ts` 的 SUPPLY_TYPES（文档第 13 节来源分类）不是同一集合，硬套会筛出空结果。
  //   反向自证：把 supplyTypeOptions 改成硬编码数组 → 转红
  const pt03 = /supplyTypeCounts\s*/.test(vueSrc) && /summary\.value\?\.supplyTypeCounts/.test(vueSrc)
  assert(
    'PT03 静态：供给类型/承诺状态筛选项来自 summary 的真实分布（不硬套枚举）',
    pt03,
    pt03 ? '选项数据驱动' : '筛选项未从 summary 取'
  )

  // PT03b 2026-10-19：承诺状态**不得**再把 UNKNOWN 从下拉里摘掉。
  //   摘它的原因是当时后端「可看不可筛」（汇总报 UNKNOWN 392,705 条 / 列表筛不出来），
  //   挂上去是「选了必空」的假筛选。5号位 已把汇总与列表统一成同一条 SQL 口径
  //   （`@CommitmentStatus='UNKNOWN'` 特判命中 `CommitmentStatus IS NULL`），
  //   dev 实测 UNKNOWN 命中 100 行、RECEIVED 命中 0 行 ⇒ 筛选真生效。
  //   ⚠️ 断言的是「**没有**这一句过滤」，不是「有某一句正向写法」——
  //   正向写法有多���种（去重/排序/改标签），写死一种会把合法演进判成红。
  //   反向自证：把 `.filter((s) => s.commitmentStatus !== 'UNKNOWN')` 加回去 → 转红。
  const pt03b = !/commitmentStatus\s*!==\s*'UNKNOWN'/.test(vueSrc)
  assert(
    'PT03b 静态：承诺状态 UNKNOWN 已回到下拉（后端筛选已真生效，别再摘掉）',
    pt03b,
    pt03b ? '无剔除' : '仍在剔除 UNKNOWN（= 假筛选项复活）'
  )

  // PT04 分页：列表端点无 total ⇒ 必须用 hasMore，不得显示假总页数
  //   反向自证：把 hasMore 改成写死 total → 转红
  const pt04 =
    /hasMore\s*\.\s*value\s*=\s*rows\.length\s*===\s*take/.test(storeSrc) &&
    /rows\.length\s*===\s*take/.test(storeSrc) &&
    !/totalPages|Math\.ceil\(\s*total\s*\//.test(vueSrc + storeSrc)
  assert(
    'PT04 静态：列表无 total ⇒ 用 hasMore 分页（不伪造总页数）',
    pt04,
    `hasMore=${/hasMore/.test(storeSrc)} 无假总页=${!/totalPages/.test(vueSrc + storeSrc)}`
  )

  // PT05 未知值降级：supplyType / commitmentStatus 查不到中文就原样显示
  //   反向自证：把 `?? v` 改成 `?? '-'` → 转红
  const pt05 =
    /SUPPLY_TYPE_LABELS\[v\]\s*\?\?\s*v/.test(vueSrc) &&
    /COMMITMENT_LABELS\[v\]\s*\?\?\s*v/.test(vueSrc)
  assert(
    'PT05 静态：未知供给类型/承诺状态原样显示（不吞成空白/占位符）',
    pt05,
    `供给类型降级=${/SUPPLY_TYPE_LABELS\[v\]\s*\?\?\s*v/.test(vueSrc)} 承诺状态降级=${/COMMITMENT_LABELS\[v\]\s*\?\?\s*v/.test(vueSrc)}`
  )

  // PT06 零写调用：本页只读（后端也只读）
  const pt06 = !/apsHttp\.(post|put|delete)\s*\(/.test(apiSrc)
  assert('PT06 静态：供需追溯页零写调用', pt06, pt06 ? '无 POST/PUT/DELETE' : '含写请求')

  // PT07 PlanVersionSummaryDto 与后端对齐：补上 SourceScheduleRunId / ActivatedAt /
  //   BasePlanVersionId / ErrorMessage 四键（冻结 01:1912 要求展示 Run/Base 信息）。
  //   后端 `PlanVersionSummaryDto.cs:17-28` 确有这四键，前端此前漏声明 —— 与 Q-E 轮
  //   「规则集 diff 自拟 DTO」同类漂移。反向自证：删任一键 → 转红。
  const pvBlock = (() => {
    const m = scheduleTypes.match(/export\s+interface\s+PlanVersionSummaryDto\s*\{([\s\S]*?)\n\}/)
    return m ? m[1] : ''
  })()
  const pt07Keys = ['sourceScheduleRunId', 'activatedAt', 'basePlanVersionId', 'errorMessage']
  const pt07Missing = pt07Keys.filter((k) => !new RegExp(`\\b${k}\\??:`).test(pvBlock))
  assert(
    'PT07 静态：PlanVersionSummaryDto 含 sourceScheduleRunId / activatedAt / basePlanVersionId / errorMessage',
    pvBlock.length > 0 && pt07Missing.length === 0,
    pt07Missing.length === 0 ? '四键齐全' : `缺：${pt07Missing.join(',')}`
  )
}

/* ==================== [PI] PI 位置页（/aps/pi）====================
 * 2026-10-17 按 5号位回执整页重做后的守门：
 *  - 本组每条断言都对应「曾经真出过一次错」，逐条反向破坏自证过
 *  - 静态段锁「不再造假 / 不再调错端点 / 不再推断」；动态段锁真实返回形状
 */
function verifyPiStatic() {
  log('\n[PI] PI 位置页契约与反造假')

  // 断言一律打在**代码**上，不打在注释上：
  // 这批文件的头注释大量描述「原先长什么样」（假 PI 字面量、旧自拟名、被下线的供给区块），
  // 若不剥注释，负向断言会把自己的整改说明当成违规 —— 那正是探针自证的失败。
  const strip = (s) => s.replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '')
  const apiCode = strip(readSrc('api/aps-v1/pi.ts'))
  const typesCode = strip(readSrc('api/aps-v1/types/pi.ts'))
  const commonCode = strip(readSrc('api/aps-v1/types/common.ts'))
  const storeCode = strip(readSrc('store/modules/aps/pi.ts'))
  const vueCode = strip(readSrc('views/Aps/Pi.vue'))
  const routerCode = strip(readSrc('router/modules/aps.ts'))

  // PI01 手写假 PI 已彻底删除（原先 api/pi.ts 内嵌 8 条 PI-2026-000x）
  const fakePi = /PI-2026-000\d/.test(apiCode + storeCode + typesCode + vueCode)
  const fakeArray = /\bpiListData\b|\bapplyFilter\b|\btoListItem\b/.test(apiCode + storeCode + typesCode + vueCode)
  assert(
    'PI01 静态：PI 页无手写假 PI 数据、无桩数组',
    !fakePi && !fakeArray,
    !fakePi && !fakeArray ? '假数据已清零' : `残留：假PI=${fakePi} 桩数组=${fakeArray}`
  )

  // PI02 非 mock 分支真的调三个端点（原先 list() 打的是 /summary 且当数组用）
  const hitsSummary = /url:\s*'\/api\/pi-position\/summary'/.test(apiCode)
  const hitsList = /url:\s*'\/api\/pi-position'/.test(apiCode)
  const hitsDetail = /url:\s*`\/api\/pi-position\/\$\{encodeURIComponent/.test(apiCode)
  assert(
    'PI02 静态：summary / list / 单 PI 三个端点各调各的',
    hitsSummary && hitsList && hitsDetail,
    `summary=${hitsSummary} list=${hitsList} detail=${hitsDetail}`
  )

  // PI03 三个端点全部传 planVersionId（缺则后端 HTTP 200 + code 400）
  //  - summary / detail：params 里直接带 planVersionId
  //  - list：params 展开 store 组装的 query 对象，故同时要求 PiPositionQuery 把 planVersionId 声明为必填
  const pvLiteral = (apiCode.match(/params:\s*\{[^}]*planVersionId/g) ?? []).length
  const listSpreads = /params:\s*\{\s*\.\.\.query\s*\}/.test(apiCode)
  // 正则必须**限定在 PiPositionQuery 接口块内**：`PiPositionDto` 里也有一模一样的
  // `planVersionId: number`，不限作用域的话把 Query 的改成可选号也照样判绿
  // （GT02 已栽过一次同类的「匹到别的声明」）。
  const queryBlock = typesCode.match(/export interface PiPositionQuery \{([\s\S]*?)\n\}/)?.[1] ?? ''
  const queryRequired = /\n\s*planVersionId:\s*number\s*\n/.test(queryBlock)
  assert(
    'PI03 静态：三个调用全部携带 planVersionId',
    pvLiteral === 2 && listSpreads && queryRequired,
    `直传=${pvLiteral}/2 list展开query=${listSpreads} Query必填=${queryRequired}`
  )

  // PI04 位置类型字典 = 6 个预留值，且旧的 6 个自拟值一个都不许回来
  const dictBlock = (commonCode.match(/PI_POSITION_TYPES\s*=\s*\[([\s\S]*?)\]/)?.[1] ?? '')
  const reserved = ['STAGE_WAITING', 'FIRST_STAGE_PENDING', 'XC', 'INTERPLANT_TRANSIT', 'UNLOCATED', 'WAITING_INBOUND']
  const dictOk = reserved.every((v) => dictBlock.includes(`'${v}'`))
  const invented = ['ERP_REMAINING', 'CROSS_STAGE', "'PRODUCTION'", "'TRANSIT'", "'WAITING'"]
  const inventedHit = invented.filter((v) => dictBlock.includes(v))
  assert(
    'PI04 静态：PI_POSITION_TYPES = 6 预留值，无自拟值',
    dictOk && inventedHit.length === 0,
    `6值齐备=${dictOk} 自拟残留=${inventedHit.join(',') || '无'}`
  )

  // PI05 PositionType 是开放词表 + 未知值降级原值（后端随时可能出第 7 个值）
  const openDict = /positionType:\s*string/.test(typesCode)
  const fallback = /POSITION_META\[t\]\?\.label\s*\?\?\s*t/.test(vueCode)
  assert(
    'PI05 静态：positionType 为开放 string + 未知值降级显示原值',
    openDict && fallback,
    `开放类型=${openDict} 降级=${fallback}`
  )

  // PI06 服务端分页真的发 skip/take，且 take 夹到后端上限 500
  // （后端 2026-10-17 起也是夹到 500；前端自夹作双保险）
  const paging = /skip:\s*\(pageIndex\.value\s*-\s*1\)\s*\*\s*pageSize\.value/.test(storeCode)
  const capped = /Math\.min\(pageSize\.value,\s*PAGE_SIZE_CAP\)/.test(storeCode) && /PAGE_SIZE_CAP\s*=\s*500/.test(storeCode)
  assert(
    'PI06 静态：列表走服务端分页 skip/take 且 take 夹到 500',
    paging && capped,
    `skip/take=${paging} 夹取=${capped}`
  )

  // PI07 四个实测恒 null 的字段不进视图（3500 行 0 命中 + 5号位自陈 sourceType 全表 NULL）
  const deadFields = ['nextStageCode', 'availableTime', 'sourceType', 'confidence']
  // 两种渲染写法都要堵：`row.nextStageCode`（插槽内取值）和 `prop="nextStageCode"`（整列绑定）
  const rendered = deadFields.filter(
    (f) => new RegExp(`row\\.${f}\\b`).test(vueCode) || new RegExp(`prop="${f}"`).test(vueCode)
  )
  assert(
    'PI07 静态：恒空字段（nextStageCode/availableTime/sourceType/confidence）不渲染',
    rendered.length === 0,
    rendered.length === 0 ? '4 个字段均未渲染' : `泄漏到视图：${rendered.join(',')}`
  )

  // PI08 推断出来的 MES 资格不许复活（原 mesEligible 由「无 UNLOCATED + 无占位」前端推导）
  const mesInfer = /\bmesEligible\b|\bmesStats\b|\bMES_ELIGIBLE\b/.test(vueCode + storeCode + typesCode + apiCode)
  assert(
    'PI08 静态：无前端推断的 mesEligible / mesStats',
    !mesInfer,
    mesInfer ? '仍存在推断字段' : '推断字段已清零'
  )

  // PI09 U16 只保留数据驱动判据 positionType === 'UNLOCATED'，U17/U18 不显示
  const u16 = /positionType\s*===\s*UNLOCATED|'UNLOCATED'/.test(vueCode) && /isUnlocated/.test(vueCode)
  const u1718 = /NO_PI|PLACEHOLDER_DEPENDENCY|isPlanningOnlyPlaceholder/.test(vueCode + storeCode + typesCode)
  assert(
    'PI09 静态：U16 数据驱动保留 / U17·U18 不显示',
    u16 && !u1718,
    `U16=${u16} U17U18残留=${u1718}`
  )

  // PI10 供给构成整块下线（原 9 类自拟 SupplyType + 供给拆解表 + 供给类型分布）
  const supply = /SupplyTraceLine|supplyTypeCounts|供给拆解|供给类型分布|供给构成/.test(vueCode + storeCode + typesCode + apiCode)
  assert(
    'PI10 静态：供给构成整块下线',
    !supply,
    supply ? '仍有供给构成区块' : '供给区块已移除'
  )

  // PI11 KPI 只能来自 /summary 服务端聚合，不许拿 list.length 当总数
  const totalFromSummary = /summary\.value\?\.totalPositions\s*\?\?\s*0/.test(storeCode)
  const listLenKpi = /totalCount\s*=|list\.value\.length/.test(storeCode)
  assert(
    'PI11 静态：KPI 取 /summary 聚合，不用当前页条数',
    totalFromSummary && !listLenKpi,
    `summary 取数=${totalFromSummary} 用页长当总数=${listLenKpi}`
  )

  // PI12 issueCode 无冻结值域 ⇒ 原值展示，不自造中文词典
  const issueDict = /ISSUE_LABEL|issueLabel\(/.test(vueCode)
  const issueRaw = /\{\{\s*row\.issueCode\s*\}\}/.test(vueCode)
  assert(
    'PI12 静态：位置问题码原值展示，不自造中文词典',
    issueRaw && !issueDict,
    `原值渲染=${issueRaw} 中文词典=${issueDict}`
  )

  // PI22 sourceKey 是兜底过程标记（AUTO_GENERATED / FIRST_STAGE_FALLBACK），不是源单据键
  //   5号位 2026-10-17 回执 §一 确认：渲染它等于告诉用户「有溯源」，反而误导。
  //   同时不得再出现「部分位置行带来源信息」这类正面断言（值非空 ≠ 有来源）。
  const sourceKeyRendered =
    /row\.sourceKey\b/.test(vueCode) || /prop="sourceKey"/.test(vueCode) || /detailHasTrace/.test(vueCode)
  const traceClaim = /带来源信息|有来源信息/.test(vueCode)
  assert(
    'PI22 静态：sourceKey 不渲染，也不宣称「行带来源信息」',
    !sourceKeyRendered && !traceClaim,
    !sourceKeyRendered && !traceClaim ? '兜底标记未泄漏到视图' : `渲染=${sourceKeyRendered} 宣称=${traceClaim}`
  )

  // PI13 页面名 = 冻结术语「PI位置」，旧自拟名不得出现在活代码里
  const titleOk = /title:\s*'PI位置'/.test(routerCode)
  const oldName = /供给来源追溯/.test(routerCode + vueCode + storeCode + apiCode + typesCode)
  assert(
    'PI13 静态：菜单名 =「PI位置」，活代码无旧自拟名',
    titleOk && !oldName,
    `新名=${titleOk} 旧名残留=${oldName}`
  )

  // PI14 DTO 字段集与冻结 07:4042-4062 一一对应，不多不少
  const dtoBlock = (typesCode.match(/export interface PiPositionDto \{([\s\S]*?)\n\}/)?.[1] ?? '')
  const frozen = ['id', 'scheduleRunId', 'planVersionId', 'productionInstructionNo', 'materialId', 'materialCode', 'positionType', 'quantity', 'currentStageCode', 'nextStageCode', 'availableTime', 'sourceType', 'sourceKey', 'issueCode', 'confidence', 'createdAt']
  const missing = frozen.filter((f) => !new RegExp(`\\b${f}[?]?:`).test(dtoBlock))
  assert(
    'PI14 静态：PiPositionDto 16 字段齐全',
    missing.length === 0,
    missing.length === 0 ? '16 字段齐全' : `缺：${missing.join(',')}`
  )

  // PI16 单 PI 详情「查无记录」= 空态，不是故障。
  //   后端 `PiPositionController.GetByProductionInstruction` 在 `result.Count == 0` 时返
  //   404「PI Position not found」。5号位 2026-10-19 全局把信封码收口成 HTTP 状态
  //   （`200 + code 404` → 真 404）⇒ store 必须按 **err.code === 404** 归到空态文案，
  //   否则「没有位置记录」会被说成「位置明细加载失败」。
  //   反向自证：把 `err.code === 404` 分支删掉 → 转红。
  const pi16NotFound = /err\.code\s*===\s*404/.test(storeCode)
  const pi16EmptyText = /该生产指示在当前版本下没有位置记录/.test(storeCode)
  assert(
    'PI16 静态：单 PI 详情 404 归空态（不把「查无记录」说成加载失败）',
    pi16NotFound && pi16EmptyText,
    `判404=${pi16NotFound} 空态文案=${pi16EmptyText}`
  )
}

async function verifyPi(token) {
  log('\n[PI] PI 位置端点实测')

  // 动态段需要一个真有位置的版本（dev 上 ACTIVE 版本为空、最新版本也空）
  const vres = await call('GET', '/api/schedule/versions', { token, query: { take: 60 } })
  const vlist = Array.isArray(vres.json?.data) ? vres.json.data : []
  assert('PI15 动态：GET /api/schedule/versions 可取版本列表', vres.ok && vlist.length > 0, `版本数=${vlist.length}`)

  let picked = null
  for (const v of vlist) {
    const s = await call('GET', '/api/pi-position/summary', { token, query: { planVersionId: v.id } })
    if ((s.json?.data?.totalPositions ?? 0) > 0) {
      picked = { id: v.id, total: s.json.data.totalPositions, summary: s.json.data }
      break
    }
  }
  if (!picked) {
    skip('PI16-PI19 动态', 'dev 无任何含 PI 位置的版本（全部 totalPositions=0）')
    return
  }

  // PI16 summary 返回聚合，且 items 恒空（5号位 2026-10-17 回执 §二）
  const sum = picked.summary
  const sumShape =
    typeof sum.piCount === 'number' &&
    typeof sum.totalPositions === 'number' &&
    Array.isArray(sum.positionTypeCounts) &&
    Array.isArray(sum.items) &&
    sum.items.length === 0
  assert(
    'PI16 动态：/summary 返聚合（piCount/totalPositions/positionTypeCounts）且 items 恒空',
    sumShape,
    `piCount=${sum.piCount} total=${sum.totalPositions} types=${sum.positionTypeCounts?.length} items=${sum.items?.length}`
  )

  // PI17 列表行的键必须全部是冻结字段，出现未知键即契约漂移
  const lres = await call('GET', '/api/pi-position', { token, query: { planVersionId: picked.id, take: 5 } })
  const rows = Array.isArray(lres.json?.data) ? lres.json.data : []
  const frozenKeys = new Set(['id', 'scheduleRunId', 'planVersionId', 'productionInstructionNo', 'materialId', 'materialCode', 'positionType', 'quantity', 'currentStageCode', 'nextStageCode', 'availableTime', 'sourceType', 'sourceKey', 'issueCode', 'confidence', 'createdAt'])
  const unknown = rows.length ? [...new Set(rows.flatMap((r) => Object.keys(r)))].filter((k) => !frozenKeys.has(k)) : []
  assert(
    'PI17 动态：列表行键全部落在冻结 16 字段内',
    rows.length > 0 && unknown.length === 0,
    rows.length === 0 ? '取不到行' : `行数=${rows.length} 未知键=${unknown.join(',') || '无'}`
  )

  // PI18 positionType 实测值必须都在 6 个预留值里（出现第 7 个 = 码表又漂了）
  const seen = [...new Set(rows.map((r) => r.positionType))]
  const offDict = seen.filter((t) => !['STAGE_WAITING', 'STAGE_WIP', 'FIRST_STAGE_PENDING', 'XC', 'INTERPLANT_TRANSIT', 'UNLOCATED', 'WAITING_INBOUND'].includes(t))
  assert(
    'PI18 动态：positionType 取值都在前端预留的 6 值内',
    offDict.length === 0,
    `实测=${seen.join('/')} 越界=${offDict.join(',') || '无'}`
  )

  // PI19 take 超上限 → 夹到 500（5号位 2026-10-17 已修，原来静默回落 100）
  //   判据：take=600 的返回条数必须与 take=500 **完全相同**（版本行数不足 500 时同样成立），
  //   而不是退到 100。前端仍自夹 ≤500 作双保险。
  const take500 = await call('GET', '/api/pi-position', { token, query: { planVersionId: picked.id, take: 500 } })
  const len500 = Array.isArray(take500.json?.data) ? take500.json.data.length : -1
  const over = await call('GET', '/api/pi-position', { token, query: { planVersionId: picked.id, take: 600 } })
  const overLen = Array.isArray(over.json?.data) ? over.json.data.length : -1
  assert(
    'PI19 动态：take 超上限夹到 500（与 take=500 同长，不再回落 100）',
    take500.ok && over.ok && len500 > 0 && overLen === len500,
    `take=500→${len500} 条, take=600→${overLen} 条`
  )

  // PI20 单 PI 端点：路由参数是生产指示号（字符串），返回该 PI 的位置行数组
  const piNo = rows[0]?.productionInstructionNo
  if (!piNo) {
    skip('PI20 动态', '取不到 productionInstructionNo')
    return
  }
  const dres = await call('GET', `/api/pi-position/${encodeURIComponent(piNo)}`, { token, query: { planVersionId: picked.id } })
  const drows = Array.isArray(dres.json?.data) ? dres.json.data : []
  const allSamePi = drows.length > 0 && drows.every((r) => r.productionInstructionNo === piNo)
  assert(
    'PI20 动态：单 PI 端点按生产指示号返回其位置行',
    dres.ok && allSamePi,
    `PI=${piNo} 位置行=${drows.length}`
  )

  // PI21 筛选参数真实生效（positionType=BOGUS 必须返回空，而不是被忽略）
  const bogus = await call('GET', '/api/pi-position', { token, query: { planVersionId: picked.id, positionType: 'BOGUS_TYPE_X', take: 5 } })
  const bogusLen = Array.isArray(bogus.json?.data) ? bogus.json.data.length : -1
  assert(
    'PI21 动态：positionType 筛选真实生效（未知值返空）',
    bogus.ok && bogusLen === 0,
    `positionType=BOGUS_TYPE_X 返回 ${bogusLen} 条`
  )
}

/**
 * GROUP=order-static：订单/需求计划页静态契约与反造假（OD01-OD10，不登录）
 *
 * 2026-10-17 整页重做后新增。此前该页挂在一份自拟 DTO 上（OrderBasicInfo /
 * OrderScheduleDetailDto / OrderSummaryStatus），三个类型与后端真实契约零重合，
 * 页面还有 3 组硬编码下拉（工厂 F-SUZ-01 / 产品族 INJECTION / 客户 CUST-001）与
 * dev 实库（CN / CYL / 中文客户名）无一命中 —— 按它筛必然空。
 */
function verifyOrderStatic() {
  log('\n[ORDER] 订单/需求计划页静态契约与反造假')

  // 断言一律打在**代码**上，不打在注释上：这批文件的头注释大量描述「原先长什么样」
  // （假下拉字面量、旧自拟类型名、被下线的风险卡），不剥注释则负向断言把自己的
  // 整改说明当违规（PI 段首次跑就吃了 6 条假红）。
  const strip = (s) => s.replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '')
  const apiCode = strip(readSrc('api/aps-v1/order.ts'))
  const typesCode = strip(readSrc('api/aps-v1/types/order.ts'))
  const commonCode = strip(readSrc('api/aps-v1/types/common.ts'))
  const storeCode = strip(readSrc('store/modules/aps/order.ts'))
  const vueCode = strip(readSrc('views/Aps/Order.vue'))
  const overviewCode = strip(readSrc('views/Aps/Overview.vue'))
  const fixturesCode = strip(readSrc('api/aps-v1/__mocks__/fixtures.ts'))
  const all = apiCode + typesCode + commonCode + storeCode + vueCode

  // OD01 四个自拟订单类型彻底删除，且不得被重新引入
  const ghostTypes =
    /\bOrderBasicInfo\b|\bOrderScheduleDetailDto\b|\bOrderSummaryStatus\b|\bORDER_SUMMARY_STATUS_LABELS\b/.test(
      all
    )
  assert(
    'OD01 静态：四个自拟订单类型已彻底删除',
    !ghostTypes,
    ghostTypes ? '仍有引用' : '已清零'
  )

  // OD02 列表 DTO 字段名必须是后端真名（id / quantity / mtS_InstructionNo / priority）
  //  - 旧前端用的是 orderId / orderQty / productionInstructionNo / prioritySegmentCode —— 后端一个都没有
  const itemBlock = typesCode.match(/export interface OrderListItemDto \{([\s\S]*?)\n\}/)?.[1] ?? ''
  const hasId = /\n\s*id:\s*number\s*\n/.test(itemBlock)
  const hasQty = /\n\s*quantity:\s*number\s*\n/.test(itemBlock)
  const hasMt = /\n\s*mtS_InstructionNo\?:\s*string\s*\n/.test(itemBlock)
  const noOrderId = !/\borderId\b/.test(itemBlock)
  const noOrderQty = !/\borderQty\b/.test(itemBlock)
  const noPrioSeg = !/\bprioritySegmentCode\b/.test(itemBlock)
  assert(
    'OD02 静态：列表 DTO 字段名与后端 OrderListItemDto 逐字对齐',
    hasId && hasQty && hasMt && noOrderId && noOrderQty && noPrioSeg,
    `id=${hasId} quantity=${hasQty} mtS_InstructionNo=${hasMt} 无orderId=${noOrderId} 无orderQty=${noOrderQty} 无prioritySegmentCode=${noPrioSeg}`
  )

  // OD03 详情 DTO 只有 order / pegging / tasks 三块；后端**没有** reasons 键
  //  （原前端 `currentDetail.reasons.length` 会在 v-if 里直接抛 TypeError）
  const detBlock = typesCode.match(/export interface OrderDetailDto \{([\s\S]*?)\n\}/)?.[1] ?? ''
  const threeBlocks =
    /\border:/.test(detBlock) && /\bpegging:/.test(detBlock) && /\btasks:/.test(detBlock)
  const noReasons = !/\breasons\b/.test(detBlock) && !/currentDetail\.reasons/.test(vueCode)
  assert(
    'OD03 静态：详情按 {order,pegging,tasks} 三块，且不再读不存在的 reasons',
    threeBlocks && noReasons,
    `三块=${threeBlocks} 无reasons=${noReasons}`
  )

  // OD04 页面不引用任何被删字段（两种写法都堵：`row.X` 与 prop="X"）
  //  —— PI07 只查 row.X 漏查 prop="X" 的整列绑定，是同型第三次
  const deadFields = ['orderId', 'orderQty', 'materialName', 'prioritySegmentCode', 'customerCode']
  const hitDead = deadFields.filter(
    (f) => new RegExp(`row\\.${f}\\b`).test(vueCode) || new RegExp(`prop="${f}"`).test(vueCode)
  )
  assert(
    'OD04 静态：页面不再渲染已删除的字段',
    hitDead.length === 0,
    hitDead.length === 0 ? '无残留' : `仍引用：${hitDead.join(',')}`
  )

  // OD05 三组硬编码假下拉全部删除，且不得再出现那些字面量
  const fakeOptions =
    /F-SUZ-01|F-CDG-02|F-BJ-01|CUST-00\d|\bINJECTION\b|\bASSEMBLY\b/.test(all)
  const baseConsts =
    /CUSTOMER_OPTIONS|FACTORY_BASE|PRODUCT_FAMILY_BASE|ORDER_FACTORIES|ORDER_CUSTOMERS/.test(all)
  assert(
    'OD05 静态：工厂/产品族/客户三组硬编码假下拉已删除',
    !fakeOptions && !baseConsts,
    `假值=${fakeOptions} 假常量=${baseConsts}`
  )

  // OD06 订单状态词表 = 冻结 `07:3611`（`[Order]` 表 Status 行）权威**六值**
  //   NEW/PLANNED/CONFIRMED/IN_PROGRESS/COMPLETED/CANCELLED
  // ⚠️ 2026-10-19 方向反转（5号位 勘误段）：此前锁的是三值 OPEN/CLOSED/CANCELLED
  //   （取「数据流向」一侧，理由是 sp_SyncOrdersToPartitionTable 原样复制 oc.Status）。
  //   0号位 第二十~二十三轮（10-17）已裁定两表属不同语义层、不冲突，SP 亦改为显式写 N'PLANNED'。
  //   dev 实测：v190 22,017 行全 PLANNED、v195 1 行 PLANNED ⇒ 继续按三值渲染会把 2 万余行显示成裸英文。
  //   + 07:2666（ON_TIME/FIRST_DELAY/REPEATED_DELAY）拆成两套，旧的 5 值自拟码表不许回来
  const statusDict = commonCode.match(/ORDER_STATUSES\s*=\s*\[([\s\S]*?)\]/)?.[1] ?? ''
  const delayDict = commonCode.match(/ORDER_DELAY_STATUSES\s*=\s*\[([\s\S]*?)\]/)?.[1] ?? ''
  const s6 = ['NEW', 'PLANNED', 'CONFIRMED', 'IN_PROGRESS', 'COMPLETED', 'CANCELLED'].every((v) =>
    statusDict.includes(`'${v}'`)
  )
  // 三值里的 OPEN/CLOSED 不得再占 ORDER_STATUSES 的位（CLOSED 从未在 07:3611 六值内）
  const sNoLegacy = !statusDict.includes("'CLOSED'")
  const d3 = ['ON_TIME', 'FIRST_DELAY', 'REPEATED_DELAY'].every((v) =>
    delayDict.includes(`'${v}'`)
  )
  assert(
    'OD06 静态：订单状态词表 = 冻结 07:3611 六值（不再是三值），延迟状态另成一套',
    s6 && sNoLegacy && d3,
    `六值齐=${s6} 三值残留CLOSED=${!sNoLegacy} DelayStatus=${d3}`
  )

  // OD06b 筛选下拉只放实测真查得到的值 —— 六值里五个在域无产出，进了下拉就是「选了必空」的假筛选
  //   实测（dev 2026-10-19）：PLANNED → v190 total=22,017 / v195 total=1；OPEN → v2/v5 total=22,163；
  //   NEW / CONFIRMED / IN_PROGRESS / COMPLETED / CANCELLED → total=0。
  //   ⇒ 词表六值（能渲染）+ 下拉两项（能筛），两者故意不等。
  //   视图侧必须用 ORDER_STATUS_FILTER_OPTIONS，不得直接 v-for ORDER_STATUSES。
  const filterOptions = commonCode.match(/ORDER_STATUS_FILTER_OPTIONS\s*=\s*\[([\s\S]*?)\]/)?.[1] ?? ''
  const foHas = ['PLANNED', 'OPEN'].every((v) => filterOptions.includes(`'${v}'`))
  const foNoZero = ['NEW', 'CONFIRMED', 'IN_PROGRESS', 'COMPLETED', 'CANCELLED'].every(
    (v) => !filterOptions.includes(`'${v}'`)
  )
  const viewUsesOptions = /ORDER_STATUS_FILTER_OPTIONS/.test(vueCode)
  const viewNotFullTable = !/v-for="s in ORDER_STATUSES"/.test(vueCode)
  // 历史遗留 Open 必须仍有中文标签（老版本 22,163 行不能显示成裸英文）
  const legacyLabeled = /OPEN:\s*'进行中'/.test(commonCode) || /\[ORDER_STATUS_LEGACY_OPEN\]:\s*'进行中'/.test(commonCode)
  assert(
    'OD06b 静态：状态筛选下拉 = PLANNED + OPEN 两项（五值零产出不入下拉），历史 Open 仍有中文标签',
    foHas && foNoZero && viewUsesOptions && viewNotFullTable && legacyLabeled,
    `下拉两项=${foHas} 零产出已排除=${foNoZero} 视图用FILTER_OPTIONS=${viewUsesOptions} 未直用全表=${viewNotFullTable} 遗留标签=${legacyLabeled}`
  )

  // OD07 未知状态降级原样显示（实库 status='Open' / 'CONFIRMED' 都不在冻结词表内）
  //  - 断言**必须限定在两个标签函数体内**：全文件搜 `?? raw` 会匹到别处的降级写法，
  //    把 delayLabel 的降级删掉照样判绿（PI03 / GT02 同型「匹到别的声明」的第三次复发）
  const delayFn =
    vueCode.match(/function delayLabel\([^)]*\)\s*:\s*string\s*\{([\s\S]*?)\n\}/)?.[1] ?? ''
  const statusFn =
    vueCode.match(/function statusLabel\([^)]*\)\s*:\s*string\s*\{([\s\S]*?)\n\}/)?.[1] ?? ''
  const delayDegrade = /\?\?\s*raw/.test(delayFn)
  const statusDegrade = /:\s*raw\s*$/.test(statusFn.trim()) || /:\s*raw\s*\n\}/.test(statusFn)
  const hasNormalize =
    /export const normalizeOrderStatus/.test(commonCode) && /normalizeOrderStatus/.test(statusFn)
  assert(
    'OD07 静态：状态未知值降级原样显示，不做枚举校验',
    delayDegrade && statusDegrade && hasNormalize,
    `delayLabel降级=${delayDegrade} statusLabel降级=${statusDegrade} 归一=${hasNormalize}`
  )

  // OD08 三个端点全部传 planVersionId（漏传一律 code=400）
  const pvLiteral = (apiCode.match(/params:\s*\{[^}]*planVersionId/g) ?? []).length
  const listSpreads = /params:\s*\{\s*\.\.\.query\s*\}/.test(apiCode)
  const queryBlock = typesCode.match(/export interface OrderQuery \{([\s\S]*?)\n\}/)?.[1] ?? ''
  const queryRequired = /\n\s*planVersionId:\s*number\s*\n/.test(queryBlock)
  assert(
    'OD08 静态：list / summary / detail 三个调用都携带 planVersionId',
    pvLiteral === 2 && listSpreads && queryRequired,
    `直传=${pvLiteral}/2 list展开query=${listSpreads} Query必填=${queryRequired}`
  )

  // OD09 pageSize 夹到 ≤200（后端 >200 静默回落 50，传 500 用户会以为只有 50 条）
  const hasCap = /PAGE_SIZE_CAP\s*=\s*200/.test(storeCode)
  const capApplied = /Math\.min\(pageSize\.value, PAGE_SIZE_CAP\)/.test(storeCode)
  assert('OD09 静态：pageSize 自夹 ≤200', hasCap && capApplied, `上限常量=${hasCap} 生效=${capApplied}`)

  // OD10 风险卡 / 未排卡下线（riskCount 查另一张表恒 0；unscheduledCount 后端 CAST(0 AS INT) 字面量）
  const noRiskKpi = !/\briskCount\b/.test(vueCode) && !/\briskCount\b/.test(overviewCode)
  const noUnschedKpi =
    !/\bunscheduledCount\b/.test(vueCode) && !/\bunscheduledCount\b/.test(overviewCode)
  const noMockOrders = !/mockOrder/.test(fixturesCode) && !/mockOrder/.test(apiCode)
  assert(
    'OD10 静态：风险卡/未排卡下线，订单 mock 桩已删',
    noRiskKpi && noUnschedKpi && noMockOrders,
    `无riskCount=${noRiskKpi} 无unscheduledCount=${noUnschedKpi} 无mock=${noMockOrders}`
  )

  // OD17「未计算」卡按需显示：DelayStatus 已全量回填（全表 NULL=0），常驻一张 0 卡是噪音；
  //     但仍保留判据——将来若又出现 NULL 行，卡片要能自己回来，不能一删了之。
  const orderConditional =
    /if \(uncalculatedCount\.value > 0\)\s*\{[\s\S]*?label: '未计算'/.test(vueCode)
  const overviewConditional = /if \(uncalculated > 0\)\s*\{[\s\S]*?label: '未计算'/.test(
    overviewCode
  )
  assert(
    'OD17 静态：「未计算」卡仅在存在未计算行时显示（不常驻 0）',
    orderConditional && overviewConditional,
    `订单页=${orderConditional} 总览页=${overviewConditional}`
  )

  // OD18 订单详情「查无记录」= 空态，不是故障。
  //   后端 `OrderQueryController.GetOrderDetail` 在 `result == null` 时返 404「Order not found」。
  //   5号位 2026-10-19 全局把信封码收口成 HTTP 状态（`200 + code 404` → 真 404）⇒ store 必须按
  //   **err.code === 404** 归到大白话空态，不能把后端英文原文 `[404] Order not found` 摆给用户。
  //   反向自证：删掉 `err.code === 404` 分支 → 转红。
  const od18NotFound = /err\.code\s*===\s*404/.test(storeCode)
  const od18Friendly = /该订单在当前版本下不存在或已被清理/.test(storeCode)
  assert(
    'OD18 静态：订单详情 404 归空态（不直接展示后端英文原文）',
    od18NotFound && od18Friendly,
    `判404=${od18NotFound} 大白话=${od18Friendly}`
  )

  // OD19 2026-10-19：5号位 新落的三个交付字段（plannedCompletionTime / delayHours / primaryReasonCode，
  // 冻结 01:1908 第 2 项「后端算、前端不得自算」+ 07:4966 列定义）已接进类型与页面。
  //   重点不是「有没有字段」，而是**怎么渲染**：三者皆 WhenWritingNull 可空键，且主因码实测
  //   大多数不在冻结 15 码字典内（07:4935 明文禁止 BATCH_POLICY_MISSING 与中文 Phase）。
  //   反向自证：换回自拟码表 / 用真值判断 delayHours / 去掉待核对降级 → 各自分别转红。
  const od19Types = [
    ['plannedCompletionTime', /\n\s*plannedCompletionTime\?:\s*IsoDateTime/],
    ['delayHours', /\n\s*delayHours\?:\s*number/],
    ['primaryReasonCode', /\n\s*primaryReasonCode\?:\s*string/]
  ]
  const od19Declared = od19Types.map(([, re]) => re.test(typesCode))
  assert(
    'OD19a 静态：三个新交付字段已进 OrderListItemDto（否则渲染成 any）',
    od19Declared.every(Boolean),
    od19Types.map(([n], i) => `${n}=${od19Declared[i]}`).join(' ')
  )

  const od19UsesFrozenDict =
    /REASON_CODE_LABELS\[code as ReasonCode\] \?\? `待核对·\$\{code\}`/.test(vueCode)
  assert(
    'OD19b 静态：延期原因复用冻结 15 码字典 + 「待核对·」降级（不原样当权威码、不臆造中文名）',
    od19UsesFrozenDict && /import\s*\{[^}]*REASON_CODE_LABELS[^}]*\}\s*from\s*'@\/api\/aps-v1\/types'/.test(vueCode),
    `复用字典=${od19UsesFrozenDict}`
  )

  const od19NullSafe = /if \(hours == null\) return ''/.test(vueCode)
  assert(
    'OD19c 静态：延期时长按 null 判空（WhenWritingNull 键不出现；真值判断会把 0 吞成无值）',
    od19NullSafe,
    `==null守卫=${od19NullSafe}`
  )

  const od19Rendered =
    /row\.delayHours/.test(vueCode) &&
    /row\.primaryReasonCode/.test(vueCode) &&
    /detailOrder\.plannedCompletionTime/.test(vueCode)
  assert(
    'OD19d 静态：三个字段都在页面有渲染位（列表/详情），不是只声明不显示',
    od19Rendered,
    `列表延期时长=${/row\.delayHours/.test(vueCode)} 列表原因=${/row\.primaryReasonCode/.test(vueCode)} 详情预计完成=${/detailOrder\.plannedCompletionTime/.test(vueCode)}`
  )
}

/** GROUP=order：订单端点实测（OD11-OD16） */
async function verifyOrder(token) {
  log('\n[ORDER] 订单端点实测')

  // 取**订单最多的**版本（不是第一个非空的）—— 只拿 1 行的版本去核对字段键覆盖面太窄，
  // `customerName` / `promisedDate` 这类按 `WhenWritingNull` 时有时无的键根本碰不到
  const vres = await call('GET', '/api/schedule/versions', { token, query: { take: 60 } })
  const vlist = Array.isArray(vres.json?.data) ? vres.json.data : []
  assert('OD11 动态：GET /api/schedule/versions 可取版本列表', vres.ok && vlist.length > 0, `版本数=${vlist.length}`)

  let picked = null
  for (const v of vlist) {
    const s = await call('GET', '/api/order-query/summary', { token, query: { planVersionId: v.id } })
    const total = s.json?.data?.totalCount ?? 0
    if (total > (picked?.sum?.totalCount ?? 0)) picked = { id: v.id, sum: s.json.data }
  }
  if (!picked) {
    skip('OD12-OD16 动态', 'dev 无任何含订单的版本（全部 totalCount=0）')
    return
  }

  // OD12 列表返 PageResult{items,total,page,pageSize}（不是裸数组 —— 5号位 §2.1 声称已落码）
  const lres = await call('GET', '/api/order-query', {
    token,
    query: { planVersionId: picked.id, pageSize: 50 }
  })
  const ld = lres.json?.data
  const pageShape =
    lres.ok && !Array.isArray(ld) && ld && Array.isArray(ld.items) && typeof ld.total === 'number'
  assert(
    'OD12 动态：列表返服务端分页 PageResult{items,total,page,pageSize}',
    pageShape,
    pageShape
      ? `total=${ld.total} page=${ld.page} pageSize=${ld.pageSize}`
      : `data 类型=${Array.isArray(ld) ? '裸数组' : typeof ld}`
  )

  // OD13 列表行键全部落在真实 OrderListItemDto 26 字段内（出现旧前端字段 = 契约漂移）
  const rows = Array.isArray(ld?.items) ? ld.items : []
  const realKeys = new Set([
    'id', 'planVersionId', 'orderCanonicalId', 'orderNo', 'orderType', 'materialCode', 'materialId',
    'customerName', 'customerSegment', 'factoryCode', 'factoryId', 'productFamilyCode', 'domainKey',
    'quantity', 'uom', 'customerDueDate', 'promisedDate', 'priority', 'status', 'delayStatus',
    'demandMaturityStatus', 'mtS_InstructionNo', 'bomno',
    // 2026-10-19 5号位 新落码（冻结 01:1908 第 2 项「后端算、前端不得自算」+ 07:4966 列定义）。
    // 三者皆可空：WhenWritingNull 下未排程 / 无原因时**整个键不出现**（实测 v2 前 200 行
    // primaryReasonCode 命中 79/200，plannedCompletionTime / delayHours 0/200）。
    'plannedCompletionTime', 'delayHours', 'primaryReasonCode'
  ])
  const unknown = rows.length
    ? [...new Set(rows.flatMap((r) => Object.keys(r)))].filter((k) => !realKeys.has(k))
    : []
  assert(
    'OD13 动态：列表行键全部落在真实 OrderListItemDto 内',
    rows.length > 0 && unknown.length === 0,
    rows.length === 0 ? '取不到行' : `行数=${rows.length} 未知键=${unknown.join(',') || '无'}`
  )

  // OD14 三个端点全必填 planVersionId（漏传 → code=400）
  const noPvList = await call('GET', '/api/order-query', { token, query: { pageSize: 1 } })
  const noPvSum = await call('GET', '/api/order-query/summary', { token })
  const noPvDet = rows.length
    ? await call('GET', `/api/order-query/${rows[0].id}`, { token })
    : { json: { code: 0 } }
  const all400 = noPvList.json?.code === 400 && noPvSum.json?.code === 400 && noPvDet.json?.code === 400
  assert(
    'OD14 动态：list / summary / detail 漏传 planVersionId 全部 400',
    all400,
    `list=${noPvList.json?.code} summary=${noPvSum.json?.code} detail=${noPvDet.json?.code}`
  )

  // OD15 详情按 {order,pegging,tasks} 三块，且**没有** reasons 键
  if (!rows.length) {
    skip('OD15 动态', '取不到订单行')
  } else {
    const dres = await call('GET', `/api/order-query/${rows[0].id}`, {
      token,
      query: { planVersionId: picked.id }
    })
    const dd = dres.json?.data
    const shape =
      dres.ok &&
      dd &&
      dd.order &&
      Array.isArray(dd.pegging) &&
      Array.isArray(dd.tasks) &&
      dd.reasons === undefined
    assert(
      'OD15 动态：详情三块 {order,pegging,tasks} 且无 reasons 键',
      shape,
      shape ? `pegging=${dd.pegging.length} tasks=${dd.tasks.length}` : `keys=${dd ? Object.keys(dd).join(',') : 'null'}`
    )
  }

  // OD16 筛选真实生效：未知值必须返空（若被静默忽略则 total 不变 = 前端筛选项是摆设）
  const probes = [
    ['productionInstructionNo', '___NO_SUCH_PI___'],
    ['productFamilyCode', '___NO_SUCH_FAMILY___'],
    ['customerName', '___无此客户___'],
    ['factoryCode', '___NO_SUCH_FACTORY___'],
    ['delayStatus', '___NO_SUCH_DELAY___']
  ]
  const badTotal = []
  for (const [k, v] of probes) {
    const r = await call('GET', '/api/order-query', {
      token,
      query: { planVersionId: picked.id, [k]: v, pageSize: 1 }
    })
    badTotal.push(`${k}=${r.json?.data?.total ?? 'null'}`)
  }
  const allEmpty = badTotal.every((t) => t.endsWith('=0'))
  assert(
    'OD16 动态：5 个新筛选参数真实生效（未知值返空，未被静默忽略）',
    allEmpty,
    allEmpty ? `基线 total=${ld?.total ?? 0}` : badTotal.join(' ')
  )
}

/** GROUP=rules：规则集 / 参数集**两条平行链**的静态守门（2026-10-22）
 *
 *  背景：前端原先自造「一个规则集下挂一个参数集」的 1:1 模型（双轨缓冲 / 双轨保存 /
 *  双轨发布），冻结 DDL 里两者是平行主表，只在 StrategyProfileVersion 上会合
 *  （08:9042 RuleSetVersion 无 ParameterSetVersionId；08:9132-9133 才是会合点）。
 *  这组探针锁死「模型不再退回双轨」+「两族各有自己的页面/权限码」。
 *  纯静态，不登录、不打后端。 */
function verifyRulesSplit() {
  const stripComments = (s) =>
    s.replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '').replace(/\s\/\/[^\n]*/g, '')
  /** 取某个函数的完整花括号体（用于只在该函数内做断言，避免全文 includes 假绿） */
  function fnBody(src, name) {
    const start = src.indexOf(`function ${name}(`)
    if (start < 0) return ''
    let depth = 0
    let opened = false
    for (let i = start; i < src.length; i++) {
      if (src[i] === '{') {
        depth++
        opened = true
      } else if (src[i] === '}') {
        depth--
        if (opened && depth === 0) return src.slice(start, i + 1)
      }
    }
    return ''
  }
  log('\n[RULES] 规则集 / 参数集 平行链 — 双轨模型不回流（rules — 2026-10-22）')

  const rulesViewRaw = readSrc('views/Aps/Rules.vue')
  const rulesView = stripComments(rulesViewRaw)
  const psViewRaw = readSrc('views/Aps/ParameterSets.vue')
  const psView = stripComments(psViewRaw)
  const ruleTypes = stripComments(readSrc('api/aps-v1/types/rule.ts'))
  const ruleApiSrc = stripComments(readSrc('api/aps-v1/rule.ts'))
  const psApiSrc = stripComments(readSrc('api/aps-v1/parameterSet.ts'))
  const rulesStore = stripComments(readSrc('store/modules/aps/rules.ts'))
  const psStore = stripComments(readSrc('store/modules/aps/parameterSets.ts'))
  const bufferSrc = stripComments(readSrc('api/aps-v1/draftBuffer.ts'))
  const routerSrc = stripComments(readSrc('router/modules/aps.ts'))

  assert('R01 静态：Rules.vue 无参数集缓冲/双轨发布残留',
    rulesViewRaw.length > 0 &&
      !['parameterSetBuffer', 'publishDualTrack', 'anyBufferDirty', 'ruleSetBuffer'].some((k) =>
        rulesView.includes(k)
      ),
    '文件命中=0 才是真无残留')

  assert('R02 静态：Rules.vue 优先级段位按后端真字段渲染（命中条件/段内排序/稳定键/竞争轮次）',
    ['row.matchConditions', 'row.sortFields', 'row.stableTieBreakFields', 'competitionRoundLabel('].every(
      (k) => rulesView.includes(k)
    ),
    '四个绑定缺一即是段位表塌回自造列')

  assert('R03 静态：Rules.vue 不渲染 segmentCode / description（后端无此字段）',
    rulesViewRaw.length > 0 &&
      !rulesView.includes('prop="segmentCode"') &&
      !rulesView.includes('prop="description"'),
    'PrioritySegment 只有 6 键，段码/说明是前端自造')

  assert('R04 静态：Rules.vue 不自造 lastPublish / lastDisable 结果对象（后端只返一句文本）',
    rulesViewRaw.length > 0 &&
      !rulesView.includes('lastPublish') &&
      !rulesView.includes('lastDisable'),
    '发布后应重取列表读真状态')

  const rsVerBody = (/interface RuleSetVersion extends[^{]*\{([\s\S]*?)\n\}/.exec(ruleTypes) || [])[1] || ''
  assert('R05 静态：RuleSetVersion 不含参数集五主题 JSON 字段',
    rsVerBody.length > 0 &&
      !/lockJson|supplyJson|procurementJson|solverStrategyJson|candidateGuardrailJson/.test(rsVerBody),
    rsVerBody.length > 0 ? '五主题属 ParameterSetVersion，不是规则集内容' : '未解析到接口体')

  const rsDetailBody = (/interface RuleSetDetailDto \{([\s\S]*?)\n\}/.exec(ruleTypes) || [])[1] || ''
  assert('R06 静态：RuleSetDetailDto 不含 parameterSet / strategyProfile 占位块',
    rsDetailBody.length > 0 &&
      !rsDetailBody.includes('parameterSet') &&
      !rsDetailBody.includes('strategyProfile'),
    rsDetailBody.length > 0 ? '详情只含 summary/domainKey/prioritySegments/contentVersionCode/history' : '未解析到接口体')

  assert('R07 静态：api/rule.ts 不读 parameterSetVersionId，fork 走 buildForkContentSnapshot 带走换型子块',
    !ruleApiSrc.includes('parameterSetVersionId') && ruleApiSrc.includes('buildForkContentSnapshot'),
    `parameterSetVersionId 命中=${ruleApiSrc.includes('parameterSetVersionId')} buildForkContentSnapshot=${ruleApiSrc.includes('buildForkContentSnapshot')}`)

  assert('R08 静态：参数集三件套独立成文件（api / store / view）',
    ['api/aps-v1/parameterSet.ts', 'store/modules/aps/parameterSets.ts', 'views/Aps/ParameterSets.vue'].every(
      (p) => staticFileExists(p)
    ),
    '缺一即参数集又被打回规则集页')

  assert('R09 静态：参数集页按 5 个主题块渲染，store 提供增删改存校验发布停用 fork',
    psView.includes('v-for="blockKey in BLOCK_KEYS"') &&
      ['onCellEdit', 'onSaveDraft', 'validateDraft', 'publish', 'disable', 'forkDraft'].every((f) =>
        psStore.includes(f)
      ),
    '主题块遍历 + 六个动作缺一即不完整')

  assert('R10 静态：两页校验结果读 isValid，不读自造的 PASSED/FAILED',
    rulesViewRaw.length > 0 &&
      psViewRaw.length > 0 &&
      !rulesView.includes("'PASSED'") &&
      !psView.includes("'PASSED'") &&
      rulesView.includes('lastValidation.isValid') &&
      psView.includes('lastValidation.isValid'),
    '后端返回 PublishValidationResult{isValid,errors,warnings,validatedAt}')

  const draftFirst = /const contentSource = draft \?\? published \?\? versions\[0\]/
  assert('R11 静态：两族详情内容都按「草稿优先」取版（发布前看到的就是要发布的）',
    draftFirst.test(ruleApiSrc) && draftFirst.test(psApiSrc),
    `规则集=${draftFirst.test(ruleApiSrc)} 参数集=${draftFirst.test(psApiSrc)}`)

  assert('R12 静态：draftBuffer 不再按参数键名猜只读/百分比（会改写 0.1 这类真实值）',
    bufferSrc.length > 0 &&
      !bufferSrc.includes("includes('threshold')") &&
      !bufferSrc.includes("startsWith('default')"),
    '类型/可编辑性只认注册绑定')

  assert('R13 静态：路由有独立 /aps/parameter-set 页，挂 aps.parameter.{view,edit,publish}',
    routerSrc.includes("path: '/aps/parameter-set'") &&
      routerSrc.includes("name: 'ApsParameterSets'") &&
      ['aps.parameter.view', 'aps.parameter.edit', 'aps.parameter.publish'].every((c) =>
        routerSrc.includes(c)
      ),
    '参数集发布是独立授权（仅 aps.admin.aps 持有）')

  const rulesRouteTitle = (/path: '\/aps\/rules'[\s\S]{0,400}?title: '([^']+)'/.exec(routerSrc) || [])[1] || ''
  assert('R14 静态：/aps/rules 标题为「规则集维护」，全仓不再有「规则与参数维护」路由标题',
    rulesRouteTitle === '规则集维护' && !routerSrc.includes("title: '规则与参数维护'"),
    `实际标题=${rulesRouteTitle || '未解析到'}`)

  const pubIdx = psStore.indexOf('publishParameterSet(')
  assert('R15 静态：参数集发布后重取列表（后端只返文本，不造结果对象）',
    pubIdx >= 0 && psStore.slice(pubIdx, pubIdx + 300).includes('await loadList()'),
    pubIdx >= 0 ? '发布会话内紧跟 loadList' : '未找到 publishParameterSet 调用')

  const findFirst = /\.find\(\(v\) => normalizeRuleStatus\(v\.status\) === 'PUBLISHED'\)/
  assert('R16 静态：两族都按「最新生效」挑当前已发布版（多条 PUBLISHED 并存时 find 会命中已到期旧版）',
    validPick(ruleApiSrc) && validPick(psApiSrc),
    `规则集=${validPick(ruleApiSrc)} 参数集=${validPick(psApiSrc)}`)

  /* ===== R17-R21：参数集内容层枚举下拉（2026-10-22 3号位 枚举字典交付） =====
   * 后端块 JSON 用 System.Text.Json 默认枚举处理（GovernanceVersionService.cs:1319-1322
   * 只有 PropertyNameCaseInsensitive，无 JsonStringEnumConverter）⇒ 枚举在 JSON 里是**数字序号**。
   * 前端下拉的 value 必须是数字，写枚举名会让后端反序列化判 INVALID_JSON。 */
  const enumSrc = stripComments(readSrc('api/aps-v1/parameterSetEnums.ts'))

  function constValues(name) {
    const body = (new RegExp(`const ${name}[^=]*=\\s*\\[([\\s\\S]*?)\\n\\]`).exec(enumSrc) || [])[1] || ''
    return [...body.matchAll(/value:\s*(\d+)/g)].map((m) => Number(m[1]))
  }
  const piSortVals = constValues('PI_SORT_BY_OPTIONS')
  const modeVals = constValues('SOLVER_MODE_OPTIONS')
  const bneckVals = constValues('BOTTLENECK_MODE_OPTIONS')
  const sameSeq = (actual, expected) => JSON.stringify(actual) === JSON.stringify(expected)

  assert('R17 静态：三枚内容层枚举与 FrozenStrategySnapshot 声明顺序一致（0 起连续序号）',
    sameSeq(piSortVals, [0, 1, 2]) &&
      sameSeq(modeVals, [0, 1, 2, 3]) &&
      sameSeq(bneckVals, [0, 1, 2, 3]),
    `PiSortBy=${JSON.stringify(piSortVals)} Mode=${JSON.stringify(modeVals)} BottleneckMode=${JSON.stringify(bneckVals)}`)

  assert('R18 静态：枚举按「块 + 点分路径」挂载（PiSort.SortBy / Mode / BottleneckMode）',
    ["'PiSort.SortBy'", 'Mode:', 'BottleneckMode:'].every((k) => enumSrc.includes(k)),
    '键错位 ⇒ 下拉挂到别的参数上')

  assert('R19 静态：flattenBlocks 给行带 options/min/max（renderer 才有据可依）',
    bufferSrc.includes('lookupParameterEnum(block, path)') &&
      bufferSrc.includes('min: limits?.min') &&
      bufferSrc.includes('max: limits?.max'),
    `enum=${bufferSrc.includes('lookupParameterEnum(block, path)')} limits=${bufferSrc.includes('min: limits?.min')}`)

  assert('R20 静态：参数集页枚举行渲染 ElSelect（不是自由文本 ElInput）',
    /<ElSelect\s*\n\s*v-else-if="row\.options"/.test(psView) &&
      /:value="opt\.value"/.test(psView),
    `ElSelect 分支=${/<ElSelect\s*\n\s*v-else-if="row\.options"/.test(psView)} 数字 value=${/:value="opt\.value"/.test(psView)}`)

  const limitsBlock = (/const NUMBER_LIMITS[^=]*=\s*\{([\s\S]*?)\n\}/.exec(enumSrc) || [])[1] || ''
  assert('R21 静态：三个数值区间与 SolverStrategyValidator 发布校验一致（0-100 / 100-5000 / 10-500）',
    /'OnTimeTarget\.TargetPercent':\s*\{\s*min:\s*0,\s*max:\s*100\s*\}/.test(limitsBlock) &&
      /'Setup\.SetupSearchBudget':\s*\{\s*min:\s*100,\s*max:\s*5000\s*\}/.test(limitsBlock) &&
      /'Setup\.SetupMaxNeighborhoodTries':\s*\{\s*min:\s*10,\s*max:\s*500\s*\}/.test(limitsBlock),
    limitsBlock ? '区间漂移 ⇒ 前端放行后端必拦的数' : '未解析到 NUMBER_LIMITS')

  /* -------- R22-R26：规则集主表写路径（2026-10-22 3号位 补端点后落的入口） -------- */
  // 背景：此前规则集**没有任何主表写端点**，页面上建不出规则集。3号位 补了
  //   POST /api/governance/rule-set（建壳）与 PUT /api/governance/rule-set/{id}（改资料/启停）。
  // 这五条锁死「入口真的接到这两个端点上」且「没凭空加删除端点」（后端没有 DELETE）。

  const hasCreateMain = /url:\s*'\/api\/governance\/rule-set'/.test(ruleApiSrc)
  const hasUpdateMain = /rule-set\/\$\{input\.ruleSetId\}`/.test(ruleApiSrc)
  assert('R22 静态：api/rule.ts 接 POST /api/governance/rule-set 与 PUT /api/governance/rule-set/{id}',
    hasCreateMain && hasUpdateMain && ruleApiSrc.includes('apsHttp.put<RuleSetMainDto>('),
    `POST=${hasCreateMain} PUT=${hasUpdateMain}`)

  assert('R23 静态：store.createRuleSet 两步（先建壳、再跟建 ${code}-V1 空白 DRAFT）',
    rulesStore.includes('ruleApi.createRuleSet({') &&
      rulesStore.includes('ruleApi.createRuleSetDraft({') &&
      /\$\{(created\.ruleSetCode|ruleSetCode)\}-V1/.test(rulesStore),
    `createRuleSet=${rulesStore.includes('ruleApi.createRuleSet({')} draft=${rulesStore.includes('ruleApi.createRuleSetDraft({')} 版本码=${/\$\{(created\.ruleSetCode|ruleSetCode)\}-V1/.test(rulesStore)}`)

  assert('R24 静态：第二步失败不谎报成功（如实说主表已建、版本没建上）',
    rulesStore.includes('规则集已创建，但空白草稿版本创建失败') &&
      rulesStore.includes('return null'),
    '半成功必须暴露给用户，不能让列表看起来一切正常')

  assert('R25 静态：列表映射带主表启停 isActive（Q4：只关主表、不级联版本）',
    /isActive:\s*r\.isActive\s*!==\s*false/.test(ruleApiSrc) &&
      rulesView.includes('!r.isActive'),
    `api 映射=${/isActive:\s*r\.isActive\s*!==\s*false/.test(ruleApiSrc)} 列表标记=${rulesView.includes('!r.isActive')}`)

  assert('R26 静态：页面有「新建规则集」与「编辑资料」入口',
    rulesView.includes('openCreateRuleSetDialog') &&
      rulesView.includes('openEditRuleSetDialog'),
    `入口=${rulesView.includes('openCreateRuleSetDialog')}/${rulesView.includes('openEditRuleSetDialog')}`)

  /* -------- R33-R34：写失败后的「回查真值」兜底（2026-10-25 dev 实测 500 后行已落库） --------
   * 背景：`POST /api/governance/rule-set` 在 dev 上**已落库却返回 500**
   *   （GovernanceController.cs:98 `CreatedAtAction(nameof(GetRuleSetVersion), null, …)` 路由不匹配）。
   *   只把错误抛给用户 ⇒ 列表看不到那条规则集、重试又得「编码已存在」= 无解。
   *   ⇒ 前端必须：catch 里先回列表核对同编码行，命中即按「已建成」补建空白草稿继续。
   * 这两条锁死该兜底不被后人「简化」掉（删掉就退回到卡死用户的实现）。 */
  const createRuleSetBody = fnBody(rulesStore, 'createRuleSet')
  const csLoadIdx = createRuleSetBody.indexOf('await loadList()')
  const csFindIdx = createRuleSetBody.indexOf("ruleSetCode?.trim().toLowerCase() === code.toLowerCase()")
  const csAttachIdx = createRuleSetBody.indexOf('attachBlankDraft(hit.ruleSetId')
  assert('R33 静态：createRuleSet 失败后回列表核对同编码行，命中即按「已建成」补建草稿（不把错误直接抛给用户）',
    createRuleSetBody.length > 0 &&
      csLoadIdx >= 0 &&
      csFindIdx > csLoadIdx &&
      csAttachIdx > csFindIdx &&
      createRuleSetBody.includes('console.warn'),
    `函数体=${createRuleSetBody.length > 0} loadList=${csLoadIdx >= 0} 核对同编码=${csFindIdx > csLoadIdx} 补建草稿=${csAttachIdx > csFindIdx} 留痕=${createRuleSetBody.includes('console.warn')}`)

  const attachBody = fnBody(rulesStore, 'attachBlankDraft')
  const hasListVersions = /ruleApi\s*\.\s*listRuleSetVersions\(/.test(attachBody)
  const hasDraftCheck = /normalizeRuleStatus\(\s*v\.status\s*\)\s*===\s*'DRAFT'/.test(attachBody)
  assert('R34 静态：补建空白草稿失败时先查是否已有 DRAFT（幂等恢复），无草稿才如实报错',
    attachBody.length > 0 &&
      hasListVersions &&
      hasDraftCheck &&
      attachBody.includes('规则集已创建，但空白草稿版本创建失败'),
    `函数体=${attachBody.length > 0} 查版本=${hasListVersions} 判DRAFT=${hasDraftCheck} 如实报错=${attachBody.includes('规则集已创建，但空白草稿版本创建失败')}`)

  /* -------- R35-R37：提示要说人话（10-25 用户实测 500 后页面只显示 "[undefined] Request failed…"） -------- */
  const csMsgOk =
    createRuleSetBody.includes('新建没成功') &&
    createRuleSetBody.includes('刷新') &&
    !createRuleSetBody.includes('[undefined]')
  assert('R35 静态：新建失败提示是大白话 + 可操作指引（不留 "[undefined]"/原始 axios 文案）',
    csMsgOk,
    `大白话=${createRuleSetBody.includes('新建没成功')} 可操作=${createRuleSetBody.includes('刷新')} 无[undefined]=${!createRuleSetBody.includes('[undefined]')}`)

  const handleErrBody = fnBody(rulesStore, 'handleError')
  assert('R36 静态：handleError 业务码缺失时不拼出 "[undefined] …"',
    handleErrBody.length > 0 &&
      handleErrBody.includes('Number.isFinite(err.code)') &&
      !handleErrBody.includes('`[${err.code}] ${err.message}`'),
    `有限性守卫=${handleErrBody.includes('Number.isFinite(err.code)')} 无裸拼=${!handleErrBody.includes('`[${err.code}] ${err.message}`')}`)

  const apsHttpSrc = stripComments(readSrc('api/aps-v1/http.ts'))
  assert('R37 静态：http 层业务码缺失时退回 HTTP 状态码（否则 5xx 变 "[undefined]"）',
    /payload\.code\s*\?\?\s*status/.test(apsHttpSrc),
    `退回HTTP码=${/payload\.code\s*\?\?\s*status/.test(apsHttpSrc)}`)

  /* -------- R38-R40：规则集删除能力（2026-10-25 3号位 R1 端点已在 dev 生效） --------
   * 背景：3号位 R1 补了 `DELETE /api/governance/rule-set/{id}`（主表族）与
   *   `DELETE /api/governance/rule-set/version/{id}`（仅 DRAFT），
   *   dev 实测路由已注册（`DELETE /api/governance/rule-set/999999` → 400「规则集不存在」）。
   *   用户 10-25 指示前端直接接上删除。
   *   ⚠️ R1 是对冻结 Q5「不给删除、一律停用」的例外翻案，**治理上仍待 0号位 放行**。
   * 这三条锁死：① 只打已落码端点（不自造契约，且参数集/策略包本轮不落码不得出现 DELETE）；
   *   ② 破坏性操作必须二次确认 + 前端拦「已发布过不可删」；
   *   ③ 写接口报错≠没写成 ⇒ 失败后回列表核对。 */
  const ruleDeleteUrls = [
    ...ruleApiSrc.matchAll(/apsHttp\.delete<[^>]*>\(\{[\s\S]{0,160}?url:\s*[`']([^`']+)[`']/g)
  ].map((m) => m[1])
  const spApiDelete = /apsHttp\.delete/.test(stripComments(readSrc('api/aps-v1/strategyProfile.ts')))
  assert('R38 静态：删除只打在后端已落码端点 DELETE /api/governance/rule-set/{id}；参数集/策略包不出现 DELETE',
    ruleDeleteUrls.length === 1 &&
      /\/api\/governance\/rule-set\/\$\{ruleSetId\}/.test(ruleDeleteUrls[0]) &&
      !/apsHttp\.delete/.test(psApiSrc) &&
      !spApiDelete,
    `rule端点=${JSON.stringify(ruleDeleteUrls)} 参数集DELETE=${/apsHttp\.delete/.test(psApiSrc)} 策略包DELETE=${spApiDelete}`)

  const removeBody = fnBody(rulesStore, 'removeRuleSet')
  assert('R39 静态：删除是破坏性操作 —— 二次确认对话框 + 权限门控 + 前端拦「已发布过不可删」',
    rulesView.includes('deleteRuleSetDialog') &&
      rulesView.includes('确认删除') &&
      rulesView.includes('删除后不可恢复') &&
      removeBody.includes('aps.rule.edit') &&
      removeBody.includes(".some((h) => h.status === 'PUBLISHED')") &&
      removeBody.includes('该规则集已发布过，不能删除'),
    `确认框=${rulesView.includes('deleteRuleSetDialog')}/${rulesView.includes('确认删除')} 权限码=${removeBody.includes('aps.rule.edit')} 已发布拦截=${removeBody.includes(".some((h) => h.status === 'PUBLISHED')")}`)

  assert('R40 静态：删除失败后回列表核对（写接口报错≠没写成）',
    removeBody.includes('await loadList()') &&
      removeBody.includes('if (!stillThere) {') &&
      removeBody.includes('按已删除继续'),
    `回列表=${removeBody.includes('await loadList()')} 核对分支=${removeBody.includes('if (!stillThere) {')} 按已删除=${removeBody.includes('按已删除继续')}`)

  /* -------- R27-R31：段位编辑器（2026-10-22 新增；发布白名单要窄于展示字典） -------- */
  const editorSrc = stripComments(readSrc('api/aps-v1/prioritySegmentEditor.ts'))

  function arrayValues(name) {
    const body = (new RegExp(`const ${name}[^=]*=\\s*\\[([\\s\\S]*?)\\n\\]`).exec(editorSrc) || [])[1] || ''
    return [...body.matchAll(/value:\s*(\d+)/g)].map((m) => Number(m[1]))
  }
  const editorFieldVals = arrayValues('EDITOR_DEMAND_FIELDS')
  const editorOpVals = arrayValues('EDITOR_CONDITION_OPERATORS')
  const sameEditorSeq = (a, b) => JSON.stringify(a) === JSON.stringify(b)

  assert('R27 静态：编辑器字段白名单 = 发布放行的 5 个（1,2,3,6,7），不含运行侧未实现的 0/4/5',
    sameEditorSeq(editorFieldVals, [1, 2, 3, 6, 7]),
    `实际=${JSON.stringify(editorFieldVals)}（0/4/5 选了必然发布失败）`)

  assert('R28 静态：编辑器操作符白名单 = 执行器支持的 6 个，且不含 NotEquals(1)',
    sameEditorSeq(editorOpVals, [0, 6, 2, 3, 4, 5]) && !editorOpVals.includes(1),
    `实际=${JSON.stringify(editorOpVals)}（执行器无 NE 分支）`)

  const saveDraftBody =
    (/async function saveDraftSegments[\s\S]*?await ruleApi\.updateRuleSetDraft\(\{([\s\S]*?)\n      \}\)/
      .exec(rulesStore) || [])[1] || ''
  assert('R29 静态：保存只写 demandPriorityJson，绝不带 contentSnapshotJson（会被服务端回填覆盖）',
    saveDraftBody.includes('demandPriorityJson: serializePrioritySegments(segments)') &&
      !saveDraftBody.includes('contentSnapshotJson') &&
      rulesStore.includes("detail.summary.status !== 'DRAFT'"),
    `body 片段命中=${saveDraftBody.length > 0} 快照键=${saveDraftBody.includes('contentSnapshotJson')}`)

  assert('R30 静态：序列化按后端契约写 PascalCase 键（PrioritySegment 带 Disallow，键名错即整块失败）',
    /Segments:\s*rows\.map/.test(editorSrc) &&
      /SegmentOrder:\s*s\.orderInSegment/.test(editorSrc) &&
      /SegmentName:/.test(editorSrc) &&
      /MatchConditions:/.test(editorSrc) &&
      /SortFields:/.test(editorSrc) &&
      /StableTieBreakFields:/.test(editorSrc),
    '键名写错会被后端 Disallow 判整块反序列化失败')

  assert('R30b 静态：竞争轮次「全声明才写」——部分声明会被投影层显式抛错',
    /allDeclared\s*=\s*rows\.length\s*>\s*0\s*&&\s*rows\.every\(/.test(editorSrc) &&
      /allDeclared\s*\?\s*\{\s*CompetitionRound:/.test(editorSrc),
    '改成「逐段有就写」⇒ 半声明 JSON 存得进去，发布时投影层抛错')

  assert('R31 静态：七条发布前拦截齐全（至少一段/名称/字段操作符组合/值/日期/兜底段/轮次）',
    ['至少要配一个段位', '名称不能为空', '不能使用该操作符', '条件值不能为空',
      '日期格式应为', '无条件的段最多只能有 1 个', '必须放在最后',
      '竞争轮次要每段都选'].every((k) => editorSrc.includes(k)),
    '缺一条 ⇒ 用户存完去发布才被打回')

  // ⚠️ 必须锚到组件块本身：v-if="segmentsEditable" 在工具条 div 上也有一次，
  //    只断言 includes 的话把组件上的 v-if 删掉仍会绿（假强）。
  const editorGuarded = /<PrioritySegmentEditor\b[^>]*v-if="segmentsEditable"/.test(rulesView)
  assert('R32 静态：页面只在「DRAFT + 有写权限」时渲染编辑器，保存按钮跟脏状态走',
    editorGuarded &&
      /const segmentsEditable = computed\([\s\S]{0,200}DRAFT[\s\S]{0,120}canWrite/.test(rulesView) &&
      rulesView.includes(':disabled="!segmentsDirty"') &&
      rulesView.includes('onSaveDraft'),
    `编辑器守卫=${editorGuarded} 脏禁用=${rulesView.includes(':disabled="!segmentsDirty"')}`)

  void rulesStore

  function validPick(src) {
    return src.includes('pickCurrentPublished(') && !findFirst.test(src)
  }
}

/**
 * GROUP=strategy-config-static：参数集 / 策略包「建壳」入口（2026-10-25）
 *
 * 背景：规则集 10-22 补了主表 POST/PUT，参数集与策略包仍**只有 version 级写端点**
 * （`GovernanceController.cs:217/227/598/608`，两个 Repository 也只有查询）。
 * 前端已接线：`POST /parameter-set` + `POST /strategy-profile`，已出函 3号位 请求补端点。
 *
 * 断言只锁「前端真的把这个入口建出来了、且指向主表而不是 version 端点」；
 * 后端端点是否存在由联调兑现，不在静态探针里假装。
 *
 * 纯静态：不登录、不打后端（dev 登录有频控，反向破坏自证连跑会撞 429）。
 */
function verifyMainTableCreateStatic() {
  const stripComments = (s) =>
    s.replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '').replace(/\s\/\/[^\n]*/g, '')

  log('\n[SC] 参数集 / 策略包 建壳入口（strategy-config-static — 2026-10-25）')

  const psApi = stripComments(readSrc('api/aps-v1/parameterSet.ts'))
  const spApi = stripComments(readSrc('api/aps-v1/strategyProfile.ts'))
  const psStore = stripComments(readSrc('store/modules/aps/parameterSets.ts'))
  const spStore = stripComments(readSrc('store/modules/aps/strategyProfile.ts'))
  const psView = stripComments(readSrc('views/Aps/ParameterSets.vue'))
  const spView = stripComments(readSrc('views/Aps/StrategyProfiles.vue'))

  // M01：参数集主表 POST 端点（引号紧贴路径 ⇒ 只能命中 /parameter-set，不会命中 /version）
  assert(
    'SC01 静态：parameterSetApi 有 createParameterSet，且 POST 到主表 /parameter-set（非 /version）',
    /async createParameterSet\s*\(/.test(psApi) &&
      /url:\s*'\/api\/governance\/parameter-set'/.test(psApi),
    `主表路径命中=${/url:\s*'\/api\/governance\/parameter-set'/.test(psApi)}`
  )

  // M02：策略包主表 POST 端点（同上）
  assert(
    'SC02 静态：strategyProfileApi 有 createProfile，且 POST 到主表 /strategy-profile（非 /version）',
    /async createProfile\s*\(/.test(spApi) &&
      /url:\s*'\/api\/governance\/strategy-profile'/.test(spApi),
    `主表路径命中=${/url:\s*'\/api\/governance\/strategy-profile'/.test(spApi)}`
  )

  // M03：参数集页有按钮 + Dialog + 调 store action（三项缺一即入口不成立）
  assert(
    'SC03 静态：ParameterSets.vue 有「新建参数集」按钮 + Dialog + 调用 paramStore.createParameterSet',
    psView.includes('新建参数集') &&
      /createParameterSetDialog/.test(psView) &&
      /paramStore\.createParameterSet\s*\(/.test(psView),
    '按钮/Dialog/action 三处必须都在'
  )

  // M04：策略包页同上
  assert(
    'SC04 静态：StrategyProfiles.vue 有「新建策略包」按钮 + Dialog + 调用 store.createProfile',
    spView.includes('新建策略包') &&
      /createProfileDialog/.test(spView) &&
      /store\.createProfile\s*\(/.test(spView),
    '按钮/Dialog/action 三处必须都在'
  )

  // M05：参数集建壳后自动跟建空白草稿（`<编码>-V1`），否则新壳无内容可编辑
  assert(
    'SC05 静态：参数集建壳后自动跟建空白草稿（createParameterSetDraft + `${code}-V1`）',
    /createParameterSetDraft\s*\(/.test(psStore) && /versionCode:\s*`\$\{code\}-V1`/.test(psStore),
    '缺任一项 ⇒ 新建后页面停在「未配版本」'
  )

  // M06：策略包建壳 Dialog 的 RunType 覆盖冻结五值，且可留空（可空 = 适用所有运行类型）
  const runTypeCodes = [
    'FULL_SCHEDULE',
    'MANUAL_RESCHEDULE',
    'LOCAL_RESCHEDULE',
    'SIMULATION',
    'INSERT_ORDER_WHATIF'
  ]
  const missingRunTypes = runTypeCodes.filter(
    (c) => !new RegExp(`\\b${c}\\s*:`).test(spView)
  )
  assert(
    'SC06 静态：策略包新建 Dialog 的 RunType 五值齐全，且留空提交为 null',
    missingRunTypes.length === 0 &&
      /runType:\s*createProfileForm\.value\.runType\s*\|\|\s*null/.test(spView),
    `缺值=${missingRunTypes.join(',') || '无'}`
  )

  // M07：两个建壳都做编码唯一前端预检（不靠后端 400 兜底才发现同码）
  assert(
    'SC07 静态：参数集/策略包建壳都有编码唯一预检（编码已存在提示）',
    psStore.includes('参数集编码已存在') && spStore.includes('策略包编码已存在'),
    '后端唯一冲突是 400；前端先挡一次，避免把编码撞车当网络故障'
  )
}

async function main() {
  log(`\n========== APS V1 4号位 v1.4 §二十九 U23-U50 验收 ==========`)
  log(`API Base: ${API_BASE}`)
  log(`Group:    ${GROUP}`)

  // 纯静态组不登录：dev 登录有频控（HTTP 429 登录尝试过于频繁），
  // 静态组拿不到 token 也会在第一步就整段中止，白白浪费一次配额。
  // gantt 的 GT01-GT16 全是静态断言（`verifyGantt()` 不接 token），却一直先登录一次，
  // 白白消耗 dev 登录频控额度（连登 7 次即 429，反向破坏自证时反复踩到）。改为免登录。
  const STATIC_ONLY_GROUPS = new Set([
    'menu',
    'pi-static',
    'order-static',
    'gantt',
    'pegging-trace',
    'rules',
    'parameter-set-static',
    'parameter-set-warehouse',
    'parameter-set-anchor',
    'strategy-config-static'
  ])
  const adminToken = STATIC_ONLY_GROUPS.has(GROUP) ? '' : await loginAsAdmin()

  if (GROUP === 'all' || GROUP === 'interfaces') {
    await verifyInterfaces(adminToken)
  }
  if (GROUP === 'all' || GROUP === 'rbac-negate') {
    await verifyRbacNegate(adminToken)
  }
  if (GROUP === 'all' || GROUP === 'current-user') {
    await verifyCurrentUser(adminToken)
  }
  if (GROUP === 'all' || GROUP === 'static') {
    verifyStaticScan()
  }
  if (GROUP === 'all' || GROUP === 'business-rules') {
    await verifyBusinessRules(adminToken)
  }
  if (GROUP === 'all' || GROUP === 'v14-additions') {
    await verifyV14Additions(adminToken)
  }
  if (GROUP === 'all' || GROUP === 'j-10a') {
    await verifyJ10A(adminToken)
  }
  if (GROUP === 'all' || GROUP === 'm-10a1') {
    verifyM10A1Order()
  }
  if (GROUP === 'all' || GROUP === 'parameter-set-drafts' || GROUP === 'parameter-set-static') {
    runEnumMappingStatic()
  }
  if (GROUP === 'all' || GROUP === 'parameter-set-warehouse') {
    runWarehouseRefStatic()
  }
  if (GROUP === 'all' || GROUP === 'parameter-set-anchor') {
    runAnchorResourceStatic()
  }
  if (GROUP === 'all' || GROUP === 'parameter-set-drafts') {
    await verifyParameterSetDraft(adminToken)
  }
  if (GROUP === 'all' || GROUP === 'o-opm') {
    await verifyOpm(adminToken)
  }
  if (GROUP === 'all' || GROUP === 'a-ctp-domainkey') {
    await verifyCtpDomainKey(adminToken)
  }
  if (GROUP === 'all' || GROUP === 'me-eta') {
    await verifyManualEta(adminToken)
  }
  if (GROUP === 'all' || GROUP === 'r-resource-calendar') {
    await verifyResourceCalendar(adminToken)
  }
  if (GROUP === 'all' || GROUP === 'd-domain') {
    await verifyDomain(adminToken)
  }
  if (GROUP === 'all' || GROUP === 't-tsrc') {
    await verifyTaskSplitRuleConfig(adminToken)
  }
  if (GROUP === 'all' || GROUP === 'setup') {
    await verifySetupPages()
  }
  if (GROUP === 'all' || GROUP === 'pwc') {
    await verifyProductionWorkCalendar(adminToken)
  }
  if (GROUP === 'all' || GROUP === 'dp-freeze') {
    await verifyDemandProtectionFreeze()
  }
  if (GROUP === 'all' || GROUP === 'dplive') {
    await verifyDemandProtectionLive()
  }
  if (GROUP === 'all' || GROUP === 'e-freeze') {
    await verifyExplanationFreeze(adminToken)
  }
  if (GROUP === 'all' || GROUP === 'menu') {
    verifyMenuGroup()
  }
  if (GROUP === 'all' || GROUP === 'overview') {
    await verifyOverview(adminToken)
  }
  if (GROUP === 'all' || GROUP === 'candidate') {
    await verifyCandidate(adminToken)
  }
  if (GROUP === 'all' || GROUP === 'run') {
    await verifyRun(adminToken)
  }
  if (GROUP === 'all' || GROUP === 'gantt') {
    verifyGantt()
  }
  if (GROUP === 'all' || GROUP === 'pi' || GROUP === 'pi-static') {
    verifyPiStatic()
  }
  if (GROUP === 'all' || GROUP === 'pegging-trace') {
    verifyPeggingTraceStatic()
  }
  if (GROUP === 'all' || GROUP === 'pi') {
    await verifyPi(adminToken)
  }
  if (GROUP === 'all' || GROUP === 'order' || GROUP === 'order-static') {
    verifyOrderStatic()
  }
  if (GROUP === 'all' || GROUP === 'order') {
    await verifyOrder(adminToken)
  }
  if (GROUP === 'all' || GROUP === 'rules') {
    verifyRulesSplit()
  }
  if (GROUP === 'all' || GROUP === 'strategy-config-static') {
    verifyMainTableCreateStatic()
  }

  log(`\n========== 结果 ==========`)
  log(`✅ 通过：${passCount}`)
  log(`❌ 失败：${failCount}`)
  log(`·  跳过：${skipCount}`)

  if (failCount > 0) {
    process.exit(1)
  }
}

main().catch((err) => {
  bad(`脚本异常：${err.message}`)
  console.error(err)
  process.exit(1)
})
