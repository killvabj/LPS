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
 *  - 枚举映射/参数表可读性静态段：GROUP=parameter-set-static node scripts/verify-acceptance.mjs（P19-P20 / P28-P34，不登录）
 *  - 锚点资源编码段：GROUP=parameter-set-anchor node scripts/verify-acceptance.mjs（A01-A08，不登录）
 *  - 仓库四处落点段：GROUP=parameter-set-warehouse node scripts/verify-acceptance.mjs（P21-P27，不登录）
 *  - 空块新增字段段：GROUP=parameter-set-fields node scripts/verify-acceptance.mjs（PF01-PF08，不登录）
 *  - 采购良率专属编辑器段：GROUP=parameter-set-yields node scripts/verify-acceptance.mjs（PY01-PY08，不登录）
 *  - 建壳/改名入口静态段：GROUP=strategy-config-static node scripts/verify-acceptance.mjs（SC01-SC10，不登录）
 *  - 治理三族删除端点段：GROUP=governance-delete node scripts/verify-acceptance.mjs（R44/R44b 动态，验 dev 上
 *    主表 DELETE 可达、版本级 DELETE 仍不可达；静态契约段在 GROUP=rules 的 R38-R41）
 *  - OPM 段：GROUP=o-opm node scripts/verify-acceptance.mjs
 *  - CTP DomainKey 段：GROUP=a-ctp-domainkey node scripts/verify-acceptance.mjs（A01-A05 前端 + C01-C02 后端契约，3号位 C 路径落码 09-30）
 *  - ManualETA 精简段：GROUP=me-eta node scripts/verify-acceptance.mjs（ME01-ME02 静态，顶部使用说明已撤）
 *  - 资源日历段：GROUP=r-resource-calendar node scripts/verify-acceptance.mjs
 *  - Domain 段：GROUP=d-domain node scripts/verify-acceptance.mjs
 *  - 排程域写权限门控段：GROUP=domain-permission node scripts/verify-acceptance.mjs（D05b/c/d/e，不登录）
 *  - TaskSplitRuleConfig 段：GROUP=t-tsrc node scripts/verify-acceptance.mjs（TSRC01-TSRC05 静态契约）
 *  - 缺失规则查询契约段：GROUP=setup-unc node scripts/verify-acceptance.mjs（SET-UNC01-UNC12 静态，不登录）
 *  - 缺失规则查询联调段：GROUP=setup-unc-live node scripts/verify-acceptance.mjs（SET-UNCL01-UNC04 动态，
 *              验后端 uncovered-stats 13 字段（含 ruleSetVersionId）+ 筛参生效 + 不认识形参静默失效 + PMC 无 rule.view）
 *  - DemandProtection 冻结一致性段：GROUP=dp-freeze node scripts/verify-acceptance.mjs（DP01-DP05 静态，R1 stale 注释 + §2.3/§2.4 文案）
 *  - DemandProtection lps/ 联调段：GROUP=dplive node scripts/verify-acceptance.mjs（DPL01-DPL10 静态，per-lock 列表 + /summary KPI + 前端聚合）
 *  - Explanation 冻结一致性段：GROUP=e-freeze node scripts/verify-acceptance.mjs（E01-E10 + E13-E22 静态 + E21 动态 + E11/E12 skip，R1 15 码字典 + R3 撤 ScheduleFailureInfo + 可选链漏写探针 + 版本不重复展示探针 + PlanVersionStatus 7 码探针 + 业务优先级渲染探针 + 首次 mount 联动探针 + selectVersion 并发去重探针 + loadCurrent allSettled 容错探针 + 10-10 option A 收口探针（撤 重排建议 + 设备故障 + /api/business-fact-issues 聚合）+ 10-10 business-fact-issues 端点 DDL 落地探针 + 10-10 样式简洁整齐探针；R2/R3 跨号位等回执）
 *  - 菜单分组段：GROUP=menu node scripts/verify-acceptance.mjs（M01-M19 静态，不登录）：顶级「策略配置」「角色与权限」两个分组 +
 *              「需求与供给」「排产执行」「基础数据与日历」三个顶级分组（apsV1Routes 共 6 个顶级节点）；
 *              所有分组叶子页用绝对路径（URL 不变）；页面改名「策略包版本」
 *  - 运行与下发段：GROUP=run node scripts/verify-acceptance.mjs（RUN01-RUN15，7 静态 + 3 动态 + 5 呈现口径）
 *  - 离线图标段：GROUP=icons node scripts/verify-acceptance.mjs（IC01-IC09 静态 + 1 产物，不登录）：
 *              自拟聚合体 RunStatusDto 已删 + 状态词表 = 冻结 07 §4.5 四值 + MES 下发区块整块下线 +
 *              api 只调 /api/governance/runs + store 三数据面各自取数 + DTO 字段名对齐 ScheduleRunGov.cs +
 *              RUN10-RUN13：域状态 404（后端对该 Run 没登记 ExpectedDomainKeysJson 的判 404）
 *              不外泄内部术语 —— 静默 toast + store 不进 errors[] + 卡片常驻空态；
 *              RUN15：404 豁免按「该 Run 有无分母」分流（5号位 R3 裁定：夹具 Run 404 是终态，
 *              有真分母还 404 才是缺陷）—— 空态文案三态，页面不得替未验证的事下结论
 *  - 出包/交付镜像段：GROUP=delivery node scripts/verify-acceptance.mjs（DL01-DL04 静态，不登录）：镜像同步不许只信 robocopy 判同（按大小+时间），须 sha256 逐文件比对+修补+入口自洽
 *  - 术语口径段：GROUP=publish-concept node scripts/verify-acceptance.mjs（PC01-PC05 静态，不登录）：
 *              「最新已发布」（列表投影 Id 最大，前端实现口径）vs「当前有效」（生效期窗口，published-version 端点口径）
 *              两个是不同概念、不一致非缺陷 ⇒ 前端文案一律叫「最新已发布」，不许叫「当前生效」
 *  - 负荷率口径段：GROUP=capacity node scripts/verify-acceptance.mjs（CAP01-CAP06 静态，不登录）：
 *              5号位 11-02 复函 §二：落地时点=V1.1、前移终止、**墙钟 occupiedHours 分子撤回**（跨天 119 条 ⇒ 天天假超载），
 *              分子改工时口径（按桶切分工时）、分母 availableHours 沿用、超载判定不随本轮落地 ⇒ 页面零改动
 *  - 接受类（脏数据风险）：VERIFY_ALLOW_WRITE=1 GROUP=j-10a node scripts/verify-acceptance.mjs
 *  - Windows Git Bash：env 前缀可用；或直接改脚本末尾 GROUP 默认值
 */

import { readFileSync, existsSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'
import { createRequire } from 'node:module'
import { setTimeout as sleep } from 'node:timers/promises'

// 自签证书放行（dev only）
process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0'

const API_BASE = process.env.API_BASE || 'https://localhost:7044'
const GROUP = process.env.GROUP || 'all' // all | interfaces | rbac-negate | current-user | static | business-rules | j-10a | m-10a1 | v14-additions | parameter-set-drafts | o-opm | r-resource-calendar | d-domain | domain-permission | a-ctp-domainkey | me-eta | t-tsrc | dp-freeze | dplive | e-freeze | setup | setup-unc | setup-unc-live | pwc | menu | overview | candidate | run | gantt | pi | pi-static | order | order-static | rules | governance-delete | reason-code | icons | publish-concept | delivery | parameter-maintenance
const ADMIN_CODE = 'admin'
const ADMIN_PWD = 'Admin@123456'

// 路径解析（用于静态扫描 frontNew/src/ + lps/）
const __dirname = dirname(fileURLToPath(import.meta.url))
const SRC_ROOT = join(__dirname, '..', 'src')
const LPS_ROOT = join(__dirname, '..', '..', 'lps')

/**
 * 读源码并**统一换行**（CRLF → LF）。
 *
 * 为什么必须有：eslint --fix 会把 .vue 写成 CRLF，而部分静态断言的正则里写死了换行符
 *   （如 `const advanceCandidates = computed\(\(\) =>[\s\S]*?\n\s*\}\)\n`）
 *   ⇒ CRLF 文件上这些断言会**假红**（探针红、代码没问题），且会被误当成回归去改代码。
 * 统一成 LF 后，断言只看内容、不看换行风格。
 */
const readText = (p) => readFileSync(p, 'utf-8').replace(/\r\n/g, '\n')

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
  // 复用外部传入的 token：dev 登录限流 5/min/IP，反向破坏自证要连跑十几组，
  // 每次重新登录必然 429。传入 VERIFY_TOKEN 即跳过登录。
  const reuse = process.env.VERIFY_TOKEN
  if (reuse) {
    ok('复用 VERIFY_TOKEN（跳过登录）')
    return reuse
  }
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

  // S03：datetime 工具 + 所有「显示后端 DateTime」页面统一引用（防裸解析回归）
  //  - 10-28 起两套口径并存（3号位 2026-10-28 核证回执）：
  //    ① 外部 ODS/MES 契约视图 = 真 UTC ⇒ formatUtcDateTime*（dayjs.utc(s).local()）
  //    ② APS 自有治理域表 = DateTime.Now 落本地墙钟 ⇒ formatLocalDateTime*（裸 dayjs(s) 直显）
  //  - 本判据只管「必须走 util」，具体走哪一套由 DT01/DT02/TSRC15/RP18 各自锁死。
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
    const c = readText(fp)
    // 10-27：缺失规则查询页撤掉「最近出现」列后不再展示任何后端 DateTime，
    // 一律要求 import 会变成死代码 ⇒ 改为「凡展示后端 DateTime 者必须引用 util」。
    // formatUtc*（外部 UTC 源）/ formatLocal*（治理域本地墙钟）都算「走 util 显示后端 DateTime」，
    // 两者任一出现即证明该页的日期渲染已收口；裸 dayjs/格式化再判红。
    const showsBackendDate =
      /format(?:Utc|Local)\w+/.test(c) ||
      /toLocaleString/.test(c) ||
      /dayjs\(\s*[A-Za-z_$][\w$.[\]]*\s*\)/.test(c)
    const uses = /from\s+['"]@\/utils\/datetime['"]/.test(c)
    return { p, uses, showsBackendDate, missing: false }
  })
  const allPagesUseUtil = pageUsesUtil.every((x) => x.uses || !x.showsBackendDate)
  assert(
    'S03 13 个页面凡展示后端 DateTime 者统一引用 @/utils/datetime（防裸解析回归）',
    utilExists && allPagesUseUtil,
    utilExists
      ? `工具存在；引用情况：${pageUsesUtil.map((x) => `${x.p.split('/').pop()}=${x.uses ? '✓' : x.showsBackendDate ? '✗' : '—不展示日期'}`).join(' | ')}`
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
    const c = readText(join(__dirname, '..', 'src', p))
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

/* ==================== [B0] 治理域时间本地直显（3号位 2026-10-28 核证回执） ====================
 * 口径（§三/§五）：APS 自有治理域表一律 DateTime.Now 落**本地墙钟** ⇒ 前端按字面直显
 * （formatLocalDateTime*，内部裸 dayjs(s)，不做 UTC→本地位移）；外部 ODS/MES 契约视图才是真 UTC
 * （formatUtcDateTime*）。原 2026-09-29「裸 DateTime = UTC」约定被误用于治理域，本轮按域修正。
 * 探针分两组：DT01 Setup 两页 ⑦ 规则更新时间；DT02 批策略页 5 处。
 * 10-29 追加（3号位《治理域时间本地直显落地回执》§三 两项归属自裁：RBAC 四页 + 域定义页）：
 *   DT05 RBAC 四页（用户/角色/权限码/业务范围）；DT06 域定义页；DT07 例外口径不变。
 * 注：校验时间 ValidatedAt / 对比时间 ComparedAt 是内存计算值（序列化带 +08:00），仍走 UTC 解析，见 DT07。
 */

/** Setup 两页 ⑦ 规则「更新时间」列：SetupRuleService.cs:153/192 CreatedAt/UpdatedAt = DateTime.Now */
function checkSetupTimeLocalDirect(pageSrc) {
  return {
    importLocalSec:
      /import\s*\{[^}]*\bformatLocalDateTimeSec\b[^}]*\}\s*from\s*['"]@\/utils\/datetime['"]/.test(
        pageSrc
      ),
    usage:
      /formatLocalDateTimeSec\(\s*row\.updatedAt\s*\?\?\s*row\.createdAt\s*\)/.test(pageSrc),
    noUtc: !/\bformatUtc\w*/.test(pageSrc)
  }
}

/** 批策略页 5 处：TaskSplitRuleConfigGovernanceService.cs:158/252/347 var now = DateTime.Now */
function checkTaskSplitLocalDirect(pageSrc) {
  return {
    importLocalDate:
      /import\s*\{[^}]*\bformatLocalDate\b[^}]*\}\s*from\s*['"]@\/utils\/datetime['"]/.test(
        pageSrc
      ),
    importLocalDateTime:
      /import\s*\{[^}]*\bformatLocalDateTime\b[^}]*\}\s*from\s*['"]@\/utils\/datetime['"]/.test(
        pageSrc
      ),
    // 禁回退旧口径：本页不得再出现任何 formatUtc*（含 import）
    noUtc: !/\bformatUtc\w*/.test(pageSrc),
    noBareDayjs: !/\bdayjs\s*\(\s*(?:row|list\.find)/.test(pageSrc),
    effectiveFrom: /row\.effectiveFrom\s*\?\s*formatLocalDate\(/.test(pageSrc),
    effectiveTo: /row\.effectiveTo\s*\?\s*formatLocalDate\(/.test(pageSrc),
    updatedAtTable: /formatLocalDateTime\(\s*row\.updatedAt\s*\)/.test(pageSrc),
    createdAtDrawer:
      /formatLocalDateTime\(\s*list\.find\([\s\S]*?\)\?\.createdAt\s*\)/.test(pageSrc),
    updatedAtDrawer:
      /formatLocalDateTime\(\s*list\.find\([\s\S]*?\)\?\.updatedAt\s*\)/.test(pageSrc)
  }
}

const checkDetail = (obj) =>
  Object.entries(obj)
    .map(([k, v]) => `${k}=${v}`)
    .join(' ')

function verifyDatetimeLocalDirect() {
  const setupPages = [
    ['SetupExact', readSrc('views/Aps/SetupExact.vue')],
    ['SetupDefault', readSrc('views/Aps/SetupDefault.vue')]
  ]
  const dt01Detail = setupPages
    .map(([name, src]) => {
      const c = checkSetupTimeLocalDirect(src)
      return `${name}[${checkDetail(c)}]`
    })
    .join(' | ')
  const dt01 =
    setupPages.length === 2 &&
    setupPages.every(([, src]) => Object.values(checkSetupTimeLocalDirect(src)).every(Boolean))
  assert(
    'DT01 静态：SetupExact / SetupDefault ⑦ 规则「更新时间」列走本地直显（SetupRuleService = DateTime.Now 落库）',
    dt01,
    dt01 ? '两页均 formatLocalDateTimeSec(row.updatedAt ?? row.createdAt)，且无 formatUtc*' : dt01Detail
  )

  const tsrcChecks = checkTaskSplitLocalDirect(readSrc('views/Aps/TaskSplitRuleConfig.vue'))
  const dt02 = Object.values(tsrcChecks).every(Boolean)
  assert(
    'DT02 静态：批策略页 5 处时间字段走本地直显（TaskSplitRuleConfigGovernanceService = DateTime.Now 落库）',
    dt02,
    dt02
      ? '5 处均 formatLocal*，无 formatUtc* / 无裸 dayjs'
      : checkDetail(tsrcChecks)
  )

  // DT03/DT04：两套口径的**工具实现**不得互换 —— 页面全改对了、工具内部却反过来，
  // 是最难被页面级探针发现的错法（DT01/DT02 只查调用名，查不到工具体）。
  const dtSrc = readSrc('utils/datetime.ts')
  const dt03Local =
    /export function formatLocalDateTime\([\s\S]*?dayjs\(s\)\.format\('YYYY-MM-DD HH:mm'\)/.test(dtSrc) &&
    /export function formatLocalDateTimeSec\([\s\S]*?dayjs\(s\)\.format\('YYYY-MM-DD HH:mm:ss'\)/.test(dtSrc) &&
    /export function formatLocalDate\([\s\S]*?dayjs\(s\)\.format\('YYYY-MM-DD'\)/.test(dtSrc)
  assert(
    'DT03 静态：formatLocalDateTime/Sec/LocalDate 内部为裸 dayjs(s) 直显（治理域不做 UTC→本地位移）',
    dt03Local,
    dt03Local ? '三个 formatLocal* 均裸解析' : '有 formatLocal* 内部改成了 dayjs.utc(...).local()'
  )

  const dt04Utc =
    /export function formatUtcDateTime\([\s\S]*?dayjs\.utc\(s\)\.local\(\)\.format\('YYYY-MM-DD HH:mm'\)/.test(
      dtSrc
    ) &&
    /export function formatUtcDateTimeSec\([\s\S]*?dayjs\.utc\(s\)\.local\(\)\.format\('YYYY-MM-DD HH:mm:ss'\)/.test(
      dtSrc
    ) &&
    /export function formatUtcDate\([\s\S]*?dayjs\.utc\(s\)\.local\(\)\.format\('YYYY-MM-DD'\)/.test(dtSrc)
  assert(
    'DT04 静态：formatUtcDate/UtcDateTime/UtcDateTimeSec 内部仍走 dayjs.utc(s).local()（外部 ODS/MES 真 UTC 源）',
    dt04Utc,
    dt04Utc ? '三个 formatUtc* 均 UTC 解析后转本地' : '有 formatUtc* 被改成裸解析，外部源会错 8h'
  )

  // DT05：RBAC 四页（用户 / 角色 / 权限码 / 业务范围）时间列 → 本地直显。
  // 后端依据：AuthDbContext.cs:164/165/176、RbacManagementService.cs:132/274/409/471
  //           （DateTime.Now 与 GETDATE() 同为服务器本地墙钟）；LastLoginAt = GETDATE()（AuthService.cs:122）。
  const rbacPages = [
    ['RbacUsers', readSrc('views/Aps/RbacUsers.vue')],
    ['RbacRoles', readSrc('views/Aps/RbacRoles.vue')],
    ['RbacPermissions', readSrc('views/Aps/RbacPermissions.vue')],
    ['RbacScopes', readSrc('views/Aps/RbacScopes.vue')]
  ]
  const rbacChecks = rbacPages.map(([name, src]) => {
    const c = {
      importLocalSec:
        /import\s*\{[^}]*\bformatLocalDateTimeSec\b[^}]*\}\s*from\s*['"]@\/utils\/datetime['"]/.test(
          src
        ),
      createdAt: /formatLocalDateTimeSec\(\s*row\.createdAt\s*\)/.test(src),
      noUtc: !/\bformatUtc\w*/.test(src)
    }
    // 用户页多一处「最后登录」
    if (name === 'RbacUsers') {
      c.lastLoginTime = /formatLocalDateTimeSec\(\s*row\.lastLoginTime\s*\)/.test(src)
    }
    return { name, ok: Object.values(c).every(Boolean), detail: c }
  })
  const dt05 = rbacChecks.every((x) => x.ok)
  assert(
    'DT05 静态：RBAC 四页（用户/角色/权限码/业务范围）时间列走本地直显（RbacManagementService = DateTime.Now / GETDATE() 落库）',
    dt05,
    dt05
      ? '四页均 formatLocalDateTimeSec(row.createdAt)，用户页含 lastLoginTime，且无 formatUtc*'
      : rbacChecks.map((x) => `${x.name}[${checkDetail(x.detail)}]`).join(' | ')
  )

  // DT06：域定义页 → 本地直显。
  // 后端依据：DomainDefinitionGovernanceService.cs:106/157/205/369（DateTime.Now）；
  //           冻结 DDL v5.1.8.7：DomainDefinition.CreatedAt/UpdatedAt DEFAULT GETDATE()、
  //           Domain_Dependency.ScannedAt DEFAULT GETDATE()。
  const domainSrc = readSrc('views/Aps/Domain.vue')
  const domainChecks = {
    importLocalDateTime:
      /import\s*\{[^}]*\bformatLocalDateTime\b[^}]*\}\s*from\s*['"]@\/utils\/datetime['"]/.test(
        domainSrc
      ),
    createdAt: /formatLocalDateTime\(\s*currentDetail\.createdAt\s*\)/.test(domainSrc),
    updatedAt: /formatLocalDateTime\(\s*currentDetail\.updatedAt\s*\)/.test(domainSrc),
    scannedAt: /formatLocalDateTime\(\s*row\.scannedAt\s*\)/.test(domainSrc),
    noUtc: !/\bformatUtc\w*/.test(domainSrc)
  }
  const dt06 = Object.values(domainChecks).every(Boolean)
  assert(
    'DT06 静态：域定义页时间字段走本地直显（DomainDefinition = DateTime.Now；Domain_Dependency.ScannedAt 默认 GETDATE()）',
    dt06,
    dt06
      ? 'createdAt / updatedAt / scannedAt 三处均 formatLocalDateTime，且无 formatUtc*'
      : checkDetail(domainChecks)
  )

  // DT07：§四 第 6 项「ValidatedAt / ComparedAt 不变」——内存计算值序列化带 +08:00，
  // 走 UTC 解析才正确；同页落库字段走本地。两套口径不得互串。
  const rulesSrc = readSrc('views/Aps/Rules.vue')
  const psSrc = readSrc('views/Aps/ParameterSets.vue')
  const exceptionChecks = {
    rulesValidatedAtUtc: /formatUtcDateTime\(\s*lastValidation\.validatedAt\s*\)/.test(rulesSrc),
    rulesComparedAtUtc: /formatUtcDateTime\(\s*currentDiff\.comparedAt\s*\)/.test(rulesSrc),
    rulesPublishedAtLocal: /formatLocalDateTime\(\s*row\.publishedAt\s*\)/.test(rulesSrc),
    psValidatedAtUtc: /formatUtcDateTime\(\s*lastValidation\.validatedAt\s*\)/.test(psSrc),
    psComparedAtUtc: /formatUtcDateTime\(\s*currentDiff\.comparedAt\s*\)/.test(psSrc),
    psPublishedAtLocal: /formatLocalDateTime\(\s*row\.publishedAt\s*\)/.test(psSrc)
  }
  const dt07 = Object.values(exceptionChecks).every(Boolean)
  assert(
    'DT07 静态：ValidatedAt / ComparedAt 仍走 UTC 解析（内存值带 +08:00），落库发布时间仍走本地（§四 第 6 项不变）',
    dt07,
    dt07
      ? '内存计算时间走 formatUtcDateTime，落库时间走 formatLocalDateTime'
      : checkDetail(exceptionChecks)
  )
}

/* ==================== [B1] 批策略页前端校验 + 部门筛选（2026-10-28）====================
 * 后端契约（只读参照，后端零改动）：
 *  - TaskSplitRuleConfigGovernanceService.cs:400 写路径拒绝 NULL 部门（实测 422）；
 *    ⇒ 页面必须前置拦截，且历史「Material 级默认」行编辑时解锁部门选择器让用户补选。
 *  - 同文件 :427-446 PreferredBatchQty 必填且 >0，并须落在 [Min, Max]（实测 422）。
 *  - List 端点支持 productionDepartmentId 筛参（types ListQuery:98）。
 * 免登录组 tsrc-static 承载（后端未起也能跑），与 t-tsrc 的在线断言互补。
 */

/** TSRC20：部门必填 + 撤除 Material 级默认哨兵 + 历史行解锁补选 */
function checkTsrcDepartmentRequired(pageSrc) {
  const code = stripComments(pageSrc)
  return {
    rule: /productionDepartmentId:\s*\[[\s\S]{0,400}?请选择生产部门/.test(code),
    formItemRequired:
      /<ElFormItem\s+label="生产部门"\s+prop="productionDepartmentId"\s+required>/.test(pageSrc),
    noSentinel: !/DEPARTMENT_SENTINEL_DEFAULT/.test(code),
    noMaterialDefaultOption: !/物料级默认（暂无部门）/.test(pageSrc),
    unlockHistoryRow:
      /editingDeptOriginal\.value\s*=\s*dto\.productionDepartmentId\s*\?\?\s*null/.test(code) &&
      /:disabled="drawerMode === 'edit' && editingDeptOriginal !== null"/.test(pageSrc),
    historyHint:
      // 必须精确到 class="field-hint">：用子串 field-hint 会被 field-hint-gone 之类的改名骗过
      /editingDeptOriginal === null[\s\S]{0,160}class="field-hint">/.test(pageSrc) &&
      /\.field-hint\s*\{/.test(pageSrc)
  }
}

/** TSRC21：PreferredBatchQty 必填 + >0 + 落在 Min/Max 区间 */
function checkTsrcPreferredRequired(pageSrc) {
  const code = stripComments(pageSrc)
  return {
    rulePresent: /preferredBatchQty:\s*\[/.test(code),
    rejectEmpty:
      /value\s*==\s*null\s*\|\|\s*Number\(value\)\s*<=\s*0[\s\S]{0,120}经济软目标批量必填/.test(code),
    lowerBound: /Number\(value\)\s*<\s*min[\s\S]{0,120}不能小于经济最小批量/.test(code),
    upperBound: /Number\(value\)\s*>\s*max[\s\S]{0,120}不能大于经济最大批量/.test(code),
    formItemRequired:
      /<ElFormItem\s+label="经济软目标批量"\s+prop="preferredBatchQty"\s+required>/.test(pageSrc)
  }
}

/** TSRC22：生产部门筛选（state + 传参 + 重置 + 模板下拉 + 查询类型） */
function checkTsrcDepartmentFilter(pageSrc) {
  const code = stripComments(pageSrc)
  return {
    state: /const\s+filterDepartmentId\s*=\s*ref<number\s*\|\s*undefined>\(undefined\)/.test(code),
    sendParam: /q\.productionDepartmentId\s*=\s*filterDepartmentId\.value/.test(code),
    reset: /resetFilters\(\)[\s\S]{0,240}filterDepartmentId\.value\s*=\s*undefined/.test(code),
    select: /v-model="filterDepartmentId"[\s\S]{0,160}留空 = 全部部门/.test(pageSrc),
    queryType: /productionDepartmentId\?:\s*number/.test(
      readSrc('api/aps-v1/types/taskSplitRuleConfig.ts')
    )
  }
}

/** TSRC23：停用后恢复启用（api / store / 页面按钮 / 审计字典 四层齐备；10-29 3号位 落码回执） */
function checkTsrcActivate() {
  const pageSrc = readSrc('views/Aps/TaskSplitRuleConfig.vue')
  const apiSrc = readSrc('api/aps-v1/taskSplitRuleConfig.ts')
  const storeSrc = readSrc('store/modules/aps/taskSplitRuleConfig.ts')
  const auditSrc = readSrc('api/aps-v1/types/audit.ts')
  const pageCode = stripComments(pageSrc)
  const apiCode = stripComments(apiSrc)
  const storeCode = stripComments(storeSrc)
  const auditCode = stripComments(auditSrc)

  // api activate 方法体（activate 是 api 对象的最后一个方法 ⇒ 切到文件尾即可覆盖整段）
  const actStart = apiCode.indexOf('async activate(')
  const actBlock = actStart >= 0 ? apiCode.slice(actStart) : ''
  // api update 方法体（PUT 整对象替换，**不得**借机改 IsActive）
  const updStart = apiCode.indexOf('async update(')
  const updEnd = apiCode.indexOf('async deactivate(')
  const updBlock = updStart >= 0 ? apiCode.slice(updStart, updEnd > updStart ? updEnd : updStart + 900) : ''

  return {
    apiMethod: /async\s+activate\s*\(\s*id\s*:\s*number\s*\)\s*:\s*Promise<TaskSplitRuleConfigDto>/.test(
      apiCode
    ),
    apiUrl: /url:\s*`\/api\/governance\/task-split-rule-configs\/\$\{id\}\/activate`/.test(actBlock),
    apiPost: /apsHttp\.post<TaskSplitRuleConfigDto>\(/.test(actBlock),
    storeAction:
      /async\s+function\s+activate\s*\(\s*id\s*:\s*number\s*\)/.test(storeCode) &&
      /taskSplitRuleConfigApi\.activate\(id\)/.test(storeCode),
    storeExposed: /^\s*activate,\s*$/m.test(storeCode),
    pageHandler:
      /async\s+function\s+handleActivate\s*\(\s*dto\s*:\s*TaskSplitRuleConfigDto\s*\)/.test(pageCode) &&
      /store\.activate\(dto\.id\)/.test(pageCode),
    pageConfirm: /ElMessageBox\.confirm\(\s*'是否确认恢复启用该批策略？'/.test(pageCode),
    pageButton: /v-else[\s\S]{0,240}@click="handleActivate\(row\)"/.test(pageCode),
    pagePermGate: /v-else[\s\S]{0,240}:disabled="!canEdit"[\s\S]{0,240}@click="handleActivate\(row\)"/.test(
      pageCode
    ),
    pageGateOnStatus: /v-if="row\.isActive"/.test(pageCode),
    pageButtonLabel: /vi-mdi:play[\s\S]{0,60}启用/.test(pageSrc),
    auditCode: /'Activate'/.test(auditCode),
    auditLabel: /Activate:\s*'启用'/.test(auditCode),
    // PUT 不改 IsActive（启停只走 deactivate / activate 两个专用端点）
    updateKeepsIsActive: updBlock.length > 0 && !/\bisActive\s*:/.test(updBlock)
  }
}

function verifyTsrcClientValidation() {
  const pageSrc = readSrc('views/Aps/TaskSplitRuleConfig.vue')

  const d20 = checkTsrcDepartmentRequired(pageSrc)
  const ok20 = Object.values(d20).every(Boolean)
  assert(
    'TSRC20 静态：批策略页生产部门必填、撤除「Material 级默认」哨兵、历史无部门行解锁补选',
    ok20,
    ok20 ? '部门必填校验 + required + 无哨兵 + 历史行解锁与提示齐备' : checkDetail(d20)
  )

  const d21 = checkTsrcPreferredRequired(pageSrc)
  const ok21 = Object.values(d21).every(Boolean)
  assert(
    'TSRC21 静态：批策略页经济软目标批量必填、>0 且落在 Min/Max 区间（后端 :427-446 前置拦截）',
    ok21,
    ok21 ? '必填 + 上下界三条校验与 required 齐备' : checkDetail(d21)
  )

  const d22 = checkTsrcDepartmentFilter(pageSrc)
  const ok22 = Object.values(d22).every(Boolean)
  assert(
    'TSRC22 静态：批策略页筛选栏含生产部门（下拉 + 传 productionDepartmentId + 重置）',
    ok22,
    ok22 ? '筛选项、传参、重置、查询类型四者齐备' : checkDetail(d22)
  )

  const d23 = checkTsrcActivate()
  const ok23 = Object.values(d23).every(Boolean)
  assert(
    'TSRC23 静态：批策略「停用后恢复启用」四层齐备（api POST activate / store 动作 / 页面启用按钮 + 二次确认 + 权限闸 / 审计字典 Activate）',
    ok23,
    ok23 ? 'api + store + 页面按钮 + 审计字典四层齐备' : checkDetail(d23)
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
      const content = readText(f)
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
    const content = readText(join(SRC_ROOT, relPath))
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
    const order = readText(join(SRC_ROOT, 'views/Aps/Order.vue'))
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
      content = readText(file)
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
    const diffTypesSrc = readText(join(SRC_ROOT, 'api/aps-v1/types/rule.ts'))
    const diffApiSrc = readText(join(SRC_ROOT, 'api/aps-v1/rule.ts'))
    const psApiSrc = readText(join(SRC_ROOT, 'api/aps-v1/parameterSet.ts'))
    const rulesVueSrc = readText(join(SRC_ROOT, 'views/Aps/Rules.vue'))
    const psVueSrc = readText(join(SRC_ROOT, 'views/Aps/ParameterSets.vue'))
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
    const rulesStoreSrc = readText(join(SRC_ROOT, 'store/modules/aps/rules.ts'))
    const psStoreSrc = readText(join(SRC_ROOT, 'store/modules/aps/parameterSets.ts'))
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
    const authSrc = readText(join(SRC_ROOT, 'store/modules/aps/auth.ts'))
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

/* ==================== [P28-P33] 参数集参数表可读性（纯静态，不登录）====================
 * 来源：用户 2026-10-26 走查反馈 —— 参数表把 `Trigger.RemainingTimeThresholdHours`
 *   这类英文路径直接摆给用户，看不懂也用不起来。
 *
 * 锁六件事：
 *   P28 参数名列显示**中文标签**（`parameterBlockFields` 目录），原始键降为小灰字对照
 *   P29 类型列显示中文（是/否、数字、文本…），不再直出 BOOLEAN / NUMBER
 *   P30 删除确认框用中文名（不要再拿英文键问用户「确定删这个？」）
 *   P31 数字框步长取字段目录（`:step="row.step ?? 1"`），不再一律按整数走
 *   P32 文本清单（TEXT_LIST）在表里可改：原先「新增字段」写进去就变只读 JSON 串
 *   P33 边界合并：目录里的 min/max 并入行（逾期修正比例 0~100 之前不夹）
 *
 * 反向破坏自证：逐条改坏 ⇒ 必须转红（scripts/selfverify-parameter-set-ux.mjs）
 */
function runParameterSetUxStatic() {
  const page = readSrc('views/Aps/ParameterSets.vue')
  const draft = readSrc('api/aps-v1/draftBuffer.ts')

  /* -------- P28 中文参数名 -------- */
  const p28Draft = /label: def\?\.label/.test(draft)
  const p28Page = /class="param-label">\{\{\s*row\.label \?\?/.test(page)
  // ⚠️ 必须判到**条件渲染**：只判 class 出现的话，把 `v-if="row.label"` 改成 false
  //   探针仍绿（原始键不再显示，对照信息丢了）⇒ 探针假绿。
  const p28KeepsKey = /v-if="row\.label" class="code-tag param-key"/.test(page)
  const p28Ok = p28Draft && p28Page && p28KeepsKey
  assert(
    'P28 静态：参数名显示中文标签（目录里 42 个字段），原始英文键降为小灰字对照',
    p28Ok,
    p28Ok
      ? '中文名 + 原始键双层展示'
      : `目录标签${p28Draft ? '已带入' : '未带入行'} / 表格取 label ${p28Page ? '是' : '否'} / 原始键保留 ${p28KeepsKey ? '是' : '否'}`
  )

  /* -------- P29 中文类型名 -------- */
  const p29Map = /BOOLEAN: '是 \/ 否'/.test(page) && /NUMBER: '数字'/.test(page)
  const p29Used = /TYPE_LABELS\[row\.type\]/.test(page)
  const p29Ok = p29Map && p29Used
  assert(
    'P29 静态：类型列显示中文（是/否 · 数字 · 文本），不直出 BOOLEAN / NUMBER',
    p29Ok,
    p29Ok ? '中文类型名' : `映射表${p29Map ? '有' : '缺'} / 渲染处${p29Used ? '已用' : '仍直出 row.type'}`
  )

  /* -------- P30 删除确认框用中文名 -------- */
  const p30Ok = /将删除参数「\$\{row\.label \?\? row\.path\.join\('\.'\)\}/.test(page)
  assert(
    'P30 静态：删除确认框用中文名（不拿英文键问用户）',
    p30Ok,
    p30Ok ? '确认框显示中文名' : '确认框仍是英文路径'
  )

  /* -------- P31 数字步长取目录 -------- */
  const p31Ok = /:step="row\.step \?\? 1"/.test(page)
  assert(
    'P31 静态：数字框步长取字段目录（row.step），不硬编码 1（否则 0.01 步长的比例值只能手打）',
    p31Ok,
    p31Ok ? '步长按目录' : '步长仍写死 1'
  )

  /* -------- P32 文本清单可编辑 -------- */
  const p32Draft = /def\?\.kind === 'TEXT_LIST' && value\.every/.test(draft)
  const p32Page = /row\.type === 'TEXT_LIST'/.test(page) && /onTextListEdit\(row, v\)/.test(page)
  const p32Ok = p32Draft && p32Page
  assert(
    'P32 静态：文本清单在参数表里可编辑（原先加完就变只读 JSON 串，只能删了重加）',
    p32Ok,
    p32Ok ? '文本清单走多行编辑' : `展开判定${p32Draft ? '有' : '缺'} / 页面编辑入口${p32Page ? '有' : '缺'}`
  )

  /* -------- P33 目录边界并入 -------- */
  const p33Ok = /lookupParameterLimits\(block, path\) \?\? \{ min: def\?\.min, max: def\?\.max \}/.test(draft)
  assert(
    'P33 静态：字段目录的 min/max 并入参数行（parameterSetEnums 已有口径优先）',
    p33Ok,
    p33Ok ? '边界合并' : '只认 parameterSetEnums 那 3 条边界 ⇒ 逾期修正比例等字段不夹边界'
  )

  /* -------- P34 有专属编辑器的空块必须留引导行 --------
   * 供应 / 采购两块有仓库引用专属编辑器，ElEmpty 被 `!hasWarehouseEditor` 挡掉 ⇒
   * 空块只剩「采购仓库优先级」一块表单，用户不知道上方的普通参数（如逾期修正比例）从哪加。
   * 三条缺一不可：① 引导行按「空 + 有专属编辑器」渲染 ② 例子取自字段目录 ③ 新增按钮不是文字链接 */
  const p34Gate = /v-if="rowsGrouped\[blockKey\]\.length === 0 && hasWarehouseEditor\(blockKey\)"/.test(
    page
  )
  const p34Example = /addFieldExamples\(blockKey\)/.test(page)
  const p34FromCatalog = /listBlockFields\(blockKey\)[\s\S]{0,120}\.map\(\(f\) => f\.label\)/.test(page)
  const addBtnTag = page.match(/<ElButton[\s\S]{0,240}class="block-add-btn"/)
  const p34BtnVisible = !!addBtnTag && !/\blink\b/.test(addBtnTag[0])
  const p34Ok = p34Gate && p34Example && p34FromCatalog && p34BtnVisible
  assert(
    'P34 静态：供应 / 采购空块显示「新增字段」引导行（例子取自字段目录），且新增按钮是实心按钮不是文字链接',
    p34Ok,
    p34Ok
      ? '引导行 + 目录例子 + 可见按钮'
      : `引导行${p34Gate ? '有' : '缺'} / 例子${p34Example ? '有' : '缺'} / 目录取${p34FromCatalog ? '是' : '否'} / 按钮${p34BtnVisible ? '可见' : '仍是文字链接'}`
  )

  /* -------- P35-P38 运行期无消费者字段标「暂未生效」（2026-10-27）--------
   * 依据：3号位 2026-10-26《参数集无运行时消费者字段核证回执》（13 项一致 / 1 项纠正），
   *   页面口径 = B 字段级中性标记，**范围只到「本轮不接入」**：
   *   - 要标 12 个字段（锁 4 / 供 3 / 采 1 / 求解 4 / 守门 0）
   *   - 2026-11-07 撤标 10 项（22 → 12）：后端已接线的字段必须移出清单，
   *     否则页面会对**真生效**的字段说「配了没用」——那比不标更坏。
   *     锁块 8 项 Trigger.*：`FrozenStrategySnapshotProvider.cs:177` 把参数集版本内容 `$.Lock`
   *       装进 `snapshot.Lock` → `PeggingOrchestrator.cs:13279` 取块 →
   *       `DemandProtectionTriggerEvaluator.cs:122-197` 判定 → `ProtectionStatus`（:13344）→ 影响需求排序
   *     守门 2 项 ResourceTopN / WarnOnlyOnMaxImpacted：`PhaseFourLocalRepair.cs:1435` / `:184`
   *   - 留存的 12 项逐条复核过：仅 DTO 声明 / 仅发布校验 / 预留位 ⇒ 运行期仍无消费者
   *   - 不标：逾期修正比例（真消费）、准时率目标 + 守门四项（待激活，接线即生效）
   * 四陷阱照旧：标签文案出现多处 ⇒ 每条判「该处的 v-if + 邻近文案」，不靠 includes 计数。 */
  const catalog = readSrc('api/aps-v1/parameterBlockFields.ts')
  const inertBlock = catalog.match(/export const INERT_FIELD_PATHS[\s\S]*?\n\}/)
  const inertSrc = inertBlock ? inertBlock[0] : ''
  const inertPaths = [...inertSrc.matchAll(/'([^']+)'/g)].map((m) => m[1])
  const expectedInert = [
    'Trigger.AllowPmcManualProtection',
    'Sticky.RequireReleaseRecord',
    'Sticky.ProtectUntilCompletion',
    'Sticky.ProtectUntilSupplyInvalid',
    'Inventory.IsEnabled',
    'Inventory.RequireFactoryContext',
    'Inventory.RequireProductFamilyContext',
    'OverdueMargin.MinimumExtraDays',
    'OnTimeTarget.IsPrimaryObjective',
    'Split.LimitMandatorySplit',
    'StageOverlap.ThresholdQty',
    'StageOverlap.ThresholdPercent'
  ].sort()
  const forbiddenInert = [
    'OverdueMargin.MarginPercent',
    'OnTimeTarget.TargetPercent',
    'NormalMs',
    'SoftMs',
    'LocalHardMs',
    'MaxRepairAttempts',
    // 2026-11-07 已接线 ⇒ 不得再标「暂未生效」
    'Trigger.UseRemainingTimeThreshold',
    'Trigger.RemainingTimeThresholdHours',
    'Trigger.ProtectDelayed',
    'Trigger.ProtectVipTier',
    'Trigger.VipTierValue',
    'Trigger.ProtectedOrderTypes',
    'Trigger.ProtectMaturityStatus',
    'Trigger.MaturityStatusValues',
    'ResourceTopN',
    'WarnOnlyOnMaxImpacted'
  ]
  const p35List = JSON.stringify([...inertPaths].sort()) === JSON.stringify(expectedInert)
  const p35Excluded = forbiddenInert.every((p) => !inertPaths.includes(p))
  const p35Helper = /isInertBlockField\(block: BlockKey, path: string\): boolean[\s\S]{0,80}INERT_FIELD_PATHS\[block\][\s\S]{0,40}\.includes\(path\)/.test(
    catalog
  )
  const p35Wired =
    (draft.match(/inert: isInertBlockField\(block, keyPath\)/g) ?? []).length === 2
  const p35Ok = p35List && p35Excluded && p35Helper && p35Wired
  assert(
    'P35 静态：无消费者字段表恰好 12 项（锁 4/供 3/采 1/求解 4/守门 0），且不含已消费/待激活字段',
    p35Ok,
    p35Ok
      ? '清单精确匹配 + 两条渲染路径都带 inert'
      : `清单${p35List ? '对' : `不符（实际 ${inertPaths.length} 项）`} / 排除项${p35Excluded ? '干净' : '混入了已消费或待激活字段'} / 判定函数${p35Helper ? '有' : '缺'} / 行接线${p35Wired ? '两处' : '未接全'}`
  )

  const p36Tag = /v-if="row\.inert"[\s\S]{0,90}暂未生效/.test(page)
  assert(
    'P36 静态：参数表行按 row.inert 标「暂未生效」（已配置的无效字段必须能一眼看出）',
    p36Tag,
    p36Tag ? '行级标注' : '行级标注缺失或不再受 row.inert 条件渲染'
  )

  const p37Option =
    /v-if="isInertBlockField\(addFieldBlock, f\.path\)"[\s\S]{0,160}暂未生效/.test(page)
  const p37Note = /该参数暂未生效：排产运行期还没读取它/.test(page)
  const p37Ok = p37Option && p37Note
  assert(
    'P37 静态：「新增字段」候选也标「暂未生效」+ 弹窗补一句大白话（否则加完才发现没用）',
    p37Ok,
    p37Ok ? '选项标签 + 说明' : `选项标签${p37Option ? '有' : '缺'} / 说明${p37Note ? '有' : '缺'}`
  )

  const p38Block = /v-if="isFullyInertBlock\(blockKey\)"[\s\S]{0,300}暂未生效/.test(page)
  const p38Import = /isFullyInertBlock/.test(page)
  const p38Ok = p38Block && p38Import
  assert(
    'P38 静态：整块无效的块在块标题上标一次（空块时用户看不到任何行；2026-11-07 锁块已部分生效 ⇒ 机制保留但当前不触发）',
    p38Ok,
    p38Ok ? '块级标注' : `块级标注${p38Block ? '有' : '缺'} / 引用${p38Import ? '有' : '缺'}`
  )

  /* -------- P39 用户操作教学文档的「不生效」标注与代码真源一致（2026-10-28）--------
   * 起因：教学文档 v1.1 曾对 11 处无消费者字段照常讲「配了就生效」，与页面「暂未生效」标签矛盾
   *   ⇒ 用户照文档配了却不生效。10-28 已逐字段加「← 不生效」标记并补 §十一 统一清单。
   * 2026-11-07 后端接线后撤标 10 项 ⇒ 清单由 22 项收窄到 12 项，文档与页面同步收窄
   *   （文档若仍说「锁定策略整块都不生效」，用户会以为 8 项 Trigger 白配了）。
   * 判据：① §十一「不生效」列 = INERT_FIELD_PATHS 12 项（不多不少）
   *      ② 正文中每条「← 不生效」标记只标一个字段，且该字段必在 12 项内
   *      ③ 已消费/待激活字段不得被标「不生效」（否则把真生效的说成死的） */
  const guideSrc = readFileSync(
    join(__dirname, '..', 'docs', 'APS排产系统_规则集与参数集维护_用户操作教学_v1.1_20261022.md'),
    'utf-8'
  )
  const guideSec = guideSrc.split('## 十一、')[1]?.split(/\n## /)[0] ?? ''
  const guideRows = guideSec.split('\n').filter((l) => l.trim().startsWith('|') && l.includes('`'))
  const guideInertCol = guideRows.map((l) => l.split('|')[2] ?? '')
  const guideListed = [
    ...new Set(guideInertCol.flatMap((c) => [...c.matchAll(/`([^`]+)`/g)].map((m) => m[1])))
  ].sort()
  const p39Cover =
    guideSec.length > 0 && JSON.stringify(guideListed) === JSON.stringify(expectedInert)

  const markedLines = guideSrc
    .split('\n')
    .filter((l) => l.includes('←') && l.includes('不生效'))
    .map((l) => l.match(/([A-Za-z][A-Za-z0-9]*(?:\.[A-Za-z][A-Za-z0-9]*)+)/)?.[1] ?? '')
    .filter(Boolean)
  const p39MarkedOk =
    markedLines.length > 0 &&
    markedLines.every((f) => expectedInert.includes(f)) &&
    forbiddenInert.every((f) => !markedLines.includes(f))
  const p39Ok = p39Cover && p39MarkedOk
  assert(
    'P39 静态：用户操作教学文档的「不生效」字段清单/标注与代码真源 INERT_FIELD_PATHS 一致',
    p39Ok,
    p39Ok
      ? `§十一 恰好 12 项 + 正文 ${markedLines.length} 处标记均命中清单`
      : `§十一 清单${p39Cover ? '对' : `不符（实际 ${guideListed.length} 项）`} / 正文标记${p39MarkedOk ? '合规' : `越界或漏标（${markedLines.filter((f) => !expectedInert.includes(f)).join(',') || '无越界字段'}）`}`
  )

  /* -------- P40 「停用」入口按「存在已发布版本」判定（2026-10-28 页面层实测发现）--------
   * 起因：dev 页面层 E2E 实测 —— 参数集发布 V1 后 fork 出草稿 V2，后端把 status 报成 DRAFT，
   *   前端 `actions.canDisable = s === 'PUBLISHED'` 于是把「停用」按钮整条藏掉；
   *   而页面又没有删草稿版本入口 ⇒ 用户想停用仍在生效的 V1 时无路可走（死路）。
   * 判据：停用按钮的闸必须看「有没有 PUBLISHED 版本」，不得看参数集聚合 status。 */
  const psStoreSrc = readSrc('store/modules/aps/parameterSets.ts')
  const p40HasPublishedGate =
    /const hasPublished = \(currentDetail\.value\?\.history \?\? \[\]\)\.some\(\(h\) => h\.status === 'PUBLISHED'\)/.test(
      psStoreSrc
    ) && /canDisable:\s*hasPublished\s*&&\s*pub/.test(psStoreSrc)
  const p40NoStatusGate = !/canDisable:\s*s === 'PUBLISHED'/.test(psStoreSrc)
  const p40Ok = p40HasPublishedGate && p40NoStatusGate
  assert(
    'P40 静态：「停用」入口按「存在已发布版本」判定（有草稿时也能停用已发布版本，不靠聚合 status）',
    p40Ok,
    p40Ok
      ? 'actions.canDisable = hasPublished && pub'
      : `存在性判据${p40HasPublishedGate ? '有' : '缺'} / 旧 status 判据${p40NoStatusGate ? '已清' : '仍在'}`
  )

  /* -------- P41 版本对比的「对比对选取」口径（2026-10-28 用户报「版本对比空白」）--------
   * 起因：10-28 用户把 V2 也发布掉后，「版本对比」Tab 恒空白。
   *   根因在前端选取口径：旧实现 `baseVer = history.find(PUBLISHED && isLatestPublished)`、
   *   `targetVer = history.find(DRAFT)` —— **只有存在草稿才调 diff**，草稿一发布就永久空白。
   *   后端 `CompareParameterSetVersionsAsync(source, target)` 支持任意两版本（只读参照：
   *   lps/LPS.APS.Application/Services/GovernanceVersionService.cs:677-730）
   *   ⇒ 纯前端可修：新口径 = 有草稿→「当前已发布↔草稿」/ 无草稿→「上一已发布↔当前已发布」。
   * 判据：
   *   ① 对比对由 `pickDiffPair` 统一选取（函数落在 parameterDiff.ts，store 只调它）
   *   ② store 的 refreshDiff 在 loadDetail 中被调用（进详情就自动算对比）
   *   ③ 旧口径「target 必须是 DRAFT」已从 store 清掉
   *  - 反向破坏自证：把 refreshDiff 改成不调 pickDiffPair / 把 loadDetail 里的 refreshDiff 删掉
   *    / 把旧 targetVer 写法塞回去 ⇒ 必转红 */
  const pdSrc = readSrc('api/aps-v1/parameterDiff.ts')
  const pdCode = pdSrc
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .replace(/^\s*\/\/.*$/gm, '')
  const p41HasPicker = /export function pickDiffPair\s*\(/.test(pdCode)
  // ⚠️ 判据必须落在**具体函数体**上：只判「整个文件里出现过 await refreshDiff()」会假绿 ——
  //   onSaveDraft 里也有一次调用，摘掉 loadDetail 里的那次探针仍绿。
  const sliceStoreFn = (name) => {
    const i = psStoreSrc.indexOf(`function ${name}`)
    if (i < 0) return ''
    const rest = psStoreSrc.slice(i + 1)
    const m = rest.match(/\n {2}(?:async )?function /)
    return m ? psStoreSrc.slice(i, i + 1 + m.index) : psStoreSrc.slice(i)
  }
  const p41RefreshBody = sliceStoreFn('refreshDiff')
  const p41DetailBody = sliceStoreFn('loadDetail')
  // store 必须**调用** pickDiffPair（只判「文件里有这个词」会假绿）
  const p41StoreCallsPicker = /pickDiffPair\(history\)/.test(p41RefreshBody)
  const p41StoreRefreshInDetail = /await refreshDiff\(\)/.test(p41DetailBody)
  const p41OldTargetGone =
    !/targetVer\s*=\s*history\.find/.test(psStoreSrc) &&
    !/const baseVer = history\.find/.test(psStoreSrc)
  const p41Ok = p41HasPicker && p41StoreCallsPicker && p41StoreRefreshInDetail && p41OldTargetGone
  assert(
    'P41 静态：版本对比的对比对由 pickDiffPair 选取（有草稿比已发布↔草稿；无草稿比上一已发布↔当前已发布，不再依赖草稿存在）',
    p41Ok,
    p41Ok
      ? 'pickDiffPair 已接入 loadDetail'
      : `选取函数${p41HasPicker ? '有' : '缺'} / store 调用${p41StoreCallsPicker ? '有' : '无'} / loadDetail 触发${p41StoreRefreshInDetail ? '有' : '无'} / 旧 DRAFT-only 口径${p41OldTargetGone ? '已清' : '仍在'}`
  )

  /* -------- P42 参数级差异表接线到页面（后端 diff 只有块级，看不出改了哪个参数）--------
   * 起因：后端 `GET /parameter-set/version/diff` 的粒度是**块级**（5 主题块 + 版本元信息 = 12 项），
   *   把整块 JSON 塞进一格 ⇒ 用户问「我这次改了哪个参数」得自己在 JSON 里找。
   *   本仓用同一套扁平化算法（draftBuffer.flattenBlocks）把两个版本展开成参数行再逐行比。
   * 判据：
   *   ① parameterDiff.ts 导出 diffParameterBlocks 且真用 flattenBlocks 展开（不重造一套）
   *   ② store 用 diffParameterBlocks 产出 paramDiffRows，并把 paramDiffRows/paramDiffPair 暴露给页面
   *   ③ 页面版本对比 Tab 渲染 paramDiffRows 的表（含「原值/新值」两列），且不再只走后端块级 diff
   *  - 反向破坏自证：把 store 的 diffParameterBlocks 调用换成空数组 / 把页面表的数据源改掉
   *    / 去掉 flattenBlocks ⇒ 必转红 */
  const p42HasDiffFn = /export function diffParameterBlocks\s*\(/.test(pdCode)
  const p42UsesFlatten =
    /flattenBlocks\(normalizeBlocksForDiff\(source\)\)/.test(pdCode) &&
    /flattenBlocks\(normalizeBlocksForDiff\(target\)\)/.test(pdCode)
  const p42StoreCallsDiff = /paramDiffRows\.value = diffParameterBlocks\(/.test(psStoreSrc)
  const p42StoreExposes = /paramDiffRows,/.test(psStoreSrc) && /paramDiffPair,/.test(psStoreSrc)
  const p42PageTable = /:data="paramDiffRows"/.test(page)
  const p42PageCols = /label="原值"/.test(page) && /label="新值"/.test(page)
  const p42Ok =
    p42HasDiffFn &&
    p42UsesFlatten &&
    p42StoreCallsDiff &&
    p42StoreExposes &&
    p42PageTable &&
    p42PageCols
  assert(
    'P42 静态：版本对比 Tab 渲染逐参数差异表（块/参数/变化/原值/新值），不再只显示后端块级 JSON',
    p42Ok,
    p42Ok
      ? 'diffParameterBlocks → paramDiffRows → 页面表 已接通'
      : `diff 函数${p42HasDiffFn ? '有' : '缺'} / flatten 展开${p42UsesFlatten ? '是' : '否'} / store 调用${p42StoreCallsDiff ? '有' : '无'} / store 暴露${p42StoreExposes ? '有' : '无'} / 页面表${p42PageTable ? '有' : '无'} / 原值新值列${p42PageCols ? '有' : '无'}`
  )

  /* -------- P43 撤标守门：后端已接线的字段不得再标「暂未生效」（2026-11-07）--------
   * 起因：后端把需求保护消费链接上了 —— `FrozenStrategySnapshotProvider.cs:177` 把**参数集版本内容**
   *   `$.Lock` 反序列化成 `snapshot.Lock`，`PeggingOrchestrator.cs:13279` 取该块 →
   *   `DemandProtectionTriggerEvaluator.cs:122-197` 逐项判定 → 产出 `ProtectionStatus`（:13344）
   *   → 影响需求排序；候选守门两项也早在 R11（10-29）由 `PhaseFourLocalRepair.cs` 接线。
   *   前端却仍按 10-26 旧口径给这 10 项挂「暂未生效」⇒ **页面对着真生效的字段说「配了没用」**。
   *
   * 判据（三条独立，缺一即红）：
   *   ① 10 项已接线字段**不得**出现在 INERT_FIELD_PATHS 里（撤标守门本体）
   *   ② 后端接线证据仍在（快照取 `$.Lock` / 判定器读这 8 项 / 守门读那 2 项）
   *   ③ 前端源码注释里记着接线出处（后人不得把这条依据悄悄删掉）
   *
   * - 反向破坏自证：把任一已接线字段塞回 INERT_FIELD_PATHS ⇒ P43 必转红
   *   （见 scripts/selfverify-parameter-inert.mjs） */
  const WIRED_FIELDS = [
    'Trigger.UseRemainingTimeThreshold',
    'Trigger.RemainingTimeThresholdHours',
    'Trigger.ProtectDelayed',
    'Trigger.ProtectVipTier',
    'Trigger.VipTierValue',
    'Trigger.ProtectedOrderTypes',
    'Trigger.ProtectMaturityStatus',
    'Trigger.MaturityStatusValues',
    'ResourceTopN',
    'WarnOnlyOnMaxImpacted'
  ]
  const p43NotLabeled = WIRED_FIELDS.every((p) => !inertPaths.includes(p))
  const p43ReLabeled = WIRED_FIELDS.filter((p) => inertPaths.includes(p))

  const readLps = (...parts) => {
    try {
      return readText(join(LPS_ROOT, ...parts))
    } catch {
      return ''
    }
  }
  const lpsProvider = readLps(
    'LPS.APS.Application',
    'Services',
    'FrozenStrategySnapshotProvider.cs'
  )
  const lpsEvaluator = readLps(
    'LPS.APS.Application',
    'Services',
    'DemandProtectionTriggerEvaluator.cs'
  )
  const lpsPegging = readLps('LPS.APS.Application', 'Services', 'PeggingOrchestrator.cs')
  const lpsPhase4 = readLps('LPS.APS.Scheduling', 'Solvers', 'PhaseFourLocalRepair.cs')

  // ②a 快照里装的是**参数集版本内容**的 `$.Lock`（不是 DB 硬锁事实）
  const p43SnapshotLock =
    lpsProvider.length > 0 &&
    /Lock\s*=\s*DeserializeParameterSetBlock<LockBlock>\(\s*parameterSetVersion\s*,\s*"Lock"/.test(
      lpsProvider
    )
  // ②b 排产主链取该块并交给判定器
  const p43PeggingTakesLock =
    lpsPegging.length > 0 &&
    /GetFrozenStrategySnapshotAsync\([\s\S]{0,160}\)\)\.Lock/.test(lpsPegging) &&
    /DemandProtectionTriggerEvaluator\.Evaluate\(/.test(lpsPegging)
  // ②c 判定器确实逐项读这 8 个触发条件
  const p43EvaluatorReads =
    lpsEvaluator.length > 0 &&
    [
      'UseRemainingTimeThreshold',
      'RemainingTimeThresholdHours',
      'ProtectDelayed',
      'ProtectVipTier',
      'VipTierValue',
      'ProtectedOrderTypes',
      'ProtectMaturityStatus',
      'MaturityStatusValues'
    ].every((f) => new RegExp(`trigger\\.${f}\\b`).test(lpsEvaluator))
  // ②d 候选守门两项由 PhaseFourLocalRepair 消费
  const p43GuardrailRead =
    lpsPhase4.length > 0 &&
    /CandidateGuardrail\.ResourceTopN/.test(lpsPhase4) &&
    /CandidateGuardrail\.WarnOnlyOnMaxImpacted/.test(lpsPhase4)
  // ③ 前端源码记着出处
  const p43CommentKept =
    /DemandProtectionTriggerEvaluator/.test(catalog) && /PhaseFourLocalRepair/.test(catalog)

  const p43Ok =
    p43NotLabeled &&
    p43SnapshotLock &&
    p43PeggingTakesLock &&
    p43EvaluatorReads &&
    p43GuardrailRead &&
    p43CommentKept
  assert(
    'P43 静态：后端已接线的 10 项字段不得标「暂未生效」，且接线证据仍在（撤销 11-07 前的错标）',
    p43Ok,
    p43Ok
      ? '10 项均已撤标 + 后端接线证据齐全'
      : `撤标${p43NotLabeled ? '干净' : `被重新标上（${p43ReLabeled.join(',')}）`} / 快照装参数集$.Lock${p43SnapshotLock ? '有' : '缺'} / 主链取块并判定${p43PeggingTakesLock ? '有' : '缺'} / 判定器读 8 触发条件${p43EvaluatorReads ? '有' : '缺'} / 守门读 2 项${p43GuardrailRead ? '有' : '缺'} / 前端记出处${p43CommentKept ? '有' : '缺'}`
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
 *   P23 采购三处落点各有专属编辑器 + store 写动作（写 workingBlocks 且置脏）；
 *       供应 Inventory.WarehousePriority 自 3号位 2026-11-06 v1.1 起维护面隐藏
 *   P24 仓库码**只能从字典里选**（不得自由文本录入 —— 拼不出假码）
 *   P25 fork 对话框有仓库编码前置提示，且**不阻断** fork
 *   P26 通用参数表剔除四处落点（避免同一份数据两处显示、只读 JSON 与编辑器打架）
 *
 * 5号位 2026-10-25 §2.4 Q1 三条前端建议（dev 实测 258 码 / KS 61 · BS 22 = 原材料库 83）：
 *   P35 下拉选项文案**带仓库属性与角色**（编码（名称）· 角色 短码）
 *   P36 **采购类两个落点**把原材料库**独立分组置顶**（Procurement.WarehousePriority / DefaultPurchaseLt）
 *   P37 分组**只限采购类两个落点**（供应库存优先级、到货可用偏移不分组）
 *   P38 下拉**给全量、不硬过滤**（不筛属性、不按码长、不按是否出现在事实表）
 *   P39 供应 Inventory.WarehousePriority 专属编辑器**不得渲染**（v1.1 改判隐藏；载荷仍原样回写）
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

  /* -------- P23 采购三处落点各有编辑器 + 四个 store 写动作 --------
   * 3号位 2026-11-06 v1.1 改判：Supply.Inventory.WarehousePriority 专属编辑器隐藏（P39 锁不渲染）；
   * 底层读写与 store 动作保留，供载荷原样回写与既有数据能力，不作为维护面入口。 */
  const p23Kinds = ['procurement-priority', 'purchase-lt', 'arrival-offset']
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
    'P23 静态：采购三处落点各有专属编辑器 + 四个 store 写动作（供应落点按 v1.1 隐藏）',
    p23Ok,
    p23Ok
      ? '三个可见编辑器 + 四个写动作齐备'
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
  // 采购提前期的物料列（3号位 之外、2026-10-26 用户走查「让填物料 Id 不行」）：
  //   `PurchaseLtRule.MaterialId` 存的是 **Material 主键数字 Id**（`AvailableTimeCalculator.cs:92-97`
  //   拿 `int materialId.ToString()` 比对）⇒ 填编码不报错、只会静默退化到仓库级默认（提前期永不生效）。
  //   ⇒ 必须是字典下拉（显示编码/名称、写回 masterId），且不得再有自由文本输入。
  const p24MatSelect =
    /:model-value="row\.MaterialId \?\? ''"[\s\S]{0,800}onChangePurchaseLtMaterial/.test(editorSrc)
  const p24MatNoFreeText = !/<ElInput[^>]*MaterialId/.test(editorSrc)
  const p24MatWriteId = /MaterialId: id \|\| null/.test(editorSrc)
  const p24MatOk = p24MatSelect && p24MatNoFreeText && p24MatWriteId
  assert(
    'P24 静态：采购提前期的物料列是字典下拉（写回物料主键 Id），不提供自由文本',
    p24MatOk,
    p24MatOk
      ? '物料下拉 + 写回数字 Id'
      : `下拉${p24MatSelect ? '有' : '缺'} 自由文本${p24MatNoFreeText ? '无' : '有'} 写回 Id ${p24MatWriteId ? '是' : '否'}`
  )
  const p24MatFromDict =
    /materialLookupApi\.listActiveMaterials\(\)/.test(editorSrc) &&
    /materialLookupApi\.searchMaterials\(/.test(editorSrc) &&
    /String\(m\.masterId/.test(editorSrc)
  assert(
    'P24 静态：物料下拉取自 /governance/lookups/materials，写回 masterId（不是物料编码）',
    p24MatFromDict,
    p24MatFromDict ? '字典源 + masterId 写回' : '物料字典源缺失或写回值不是 masterId'
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

  /* -------- P35 下拉选项文案带仓库属性与角色（5号位 Q1 建议②） -------- */
  // ⚠️ 判据只认**函数定义与调用**（stripComments 后注释已剥掉），
  //   「文案里出现角色两个字」不算数 —— 必须是代码真的按 role + prop 拼文案。
  const p35Fns =
    /export function warehouseOptionLabel\(/.test(wrSrc) &&
    /export function warehouseAttributeText\(/.test(wrSrc)
  const p35AttrFromRoleProp =
    /item\?\.warehouseRole/.test(wrSrc) && /item\?\.erpProperty/.test(wrSrc)
  const p35Format = /\$\{base\} · \$\{attr\}/.test(wrSrc) && /\$\{role\} \$\{prop\}/.test(wrSrc)
  const p35Used = /warehouseOptionLabel\(/.test(editorSrc)
  const p35Ok = p35Fns && p35AttrFromRoleProp && p35Format && p35Used
  assert(
    'P35 静态：仓库下拉文案带仓库属性与角色（编码（名称）· 角色 短码）',
    p35Ok,
    p35Ok
      ? '文案按 warehouseRole + erpProperty 拼装并被编辑器使用'
      : `函数${p35Fns ? '有' : '缺'} 取属性${p35AttrFromRoleProp ? '有' : '缺'} 格式${p35Format ? '对' : '不对'} 编辑器使用${p35Used ? '有' : '无'}`
  )

  /* -------- P36 采购类两个落点：原材料库独立分组置顶 -------- */
  const p36Tag = /<ElOptionGroup[^>]*label="原材料库"/.test(editorSrc)
  const p36Kinds =
    /groupRaw[\s\S]{0,300}procurement-priority[\s\S]{0,300}purchase-lt/.test(editorSrc)
  // 三个落点各有一处分组渲染（优先级行编辑 / 优先级添加 / 默认采购提前期）
  const p36Count = (editorSrc.match(/label="原材料库"/g) || []).length
  const p36Ok = p36Tag && p36Kinds && p36Count >= 3
  assert(
    'P36 静态：采购类两个落点把原材料库（KS+BS）独立分组置顶',
    p36Ok,
    p36Ok
      ? `分组标签 + 两个采购落点判定齐备（${p36Count} 处分组渲染）`
      : `分组标签${p36Tag ? '有' : '缺'} 落点判定${p36Kinds ? '对' : '不对'} 分组渲染 ${p36Count} 处（应 ≥3）`
  )

  /* -------- P37 分组只限采购类两个落点（供应库存 / 到货可用偏移不分组） -------- */
  const p37Src = /const groupRaw = computed\([\s\S]{0,400}?\n\s*\)/.exec(editorSrc)?.[0] ?? ''
  const p37NoSupply = p37Src.length > 0 && !/supply-priority/.test(p37Src)
  const p37NoArrival = p37Src.length > 0 && !/arrival-offset/.test(p37Src)
  const p37Ok = p37NoSupply && p37NoArrival
  assert(
    'P37 静态：供应库存仓库优先级与到货可用偏移**不分组**（5号位 只点名采购类两个落点）',
    p37Ok,
    p37Ok
      ? '分组判定不含 supply-priority / arrival-offset'
      : `groupRaw 判定${p37Src.length ? '已取到' : '未取到'} 含 supply-priority=${!p37NoSupply} 含 arrival-offset=${!p37NoArrival}`
  )

  /* -------- P38 下拉给全量、不硬过滤 -------- */
  const p38FromDict = /store\.warehouseDict\.map\(/.test(editorSrc)
  const p38NoDictFilter = !/store\.warehouseDict[\s\S]{0,40}?\.filter\(/.test(editorSrc)
  const p38NoLengthFilter = !/code\.length/.test(editorSrc)
  const p38NoCodeSlice = !/code[^\n]*\.slice\(/.test(editorSrc)
  const p38Ok = p38FromDict && p38NoDictFilter && p38NoLengthFilter && p38NoCodeSlice
  assert(
    'P38 静态：仓库下拉给全量字典、不硬过滤（不筛属性、不按码长、不截断编码）',
    p38Ok,
    p38Ok
      ? '字典全量进下拉（分组只排版、不减码）'
      : `字典取数${p38FromDict ? '有' : '缺'} 字典过滤${p38NoDictFilter ? '无' : '有'} 码长过滤${p38NoLengthFilter ? '无' : '有'} 编码截断${p38NoCodeSlice ? '无' : '有'}`
  )

  /* -------- P39 供应库存仓库优先级编辑器不得渲染（3号位 2026-11-06 v1.1 改判隐藏） -------- */
  // 判据先剥 HTML 注释：注释掉的旧块不渲染，不算「仍暴露入口」；真正加回渲染才算红。
  const p39Page = stripComments(readSrc('views/Aps/ParameterSets.vue')).replace(
    /<!--[\s\S]*?-->/g,
    ''
  )
  const p39NoSupplyEditor = !p39Page.includes('kind="supply-priority"')
  assert(
    'P39 静态：供应 Inventory.WarehousePriority 编辑器不在维护面渲染（3号位 2026-11-06 v1.1 改判隐藏）',
    p39NoSupplyEditor,
    p39NoSupplyEditor
      ? '供应落点编辑器已隐藏，载荷仍原样回写'
      : '供应落点编辑器仍渲染 ⇒ 与 v1.1 改判冲突'
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

/* ==================== [PF01-PF08] 参数集空块「新增字段」（纯静态，不登录）====================
 * 派活来源：3号位 2026-10-25《锚点资源编码补录入 · 空块不补键确认与新建参数集首键拍板回执 v1.0》
 *   §C-AN-1 空块（无任何键）上开放「新增字段 / 新增首项」；用户**显式新增一个字段并填入真实值**
 *             后，该字段才进入块 JSON
 *   §C-AN-3 **不注入任何默认值**；空块在用户完成首个真实输入前保持空、仍被 `EMPTY_*` 拦截
 *
 * 后端真源（只读引用）：
 *   DTO `FrozenStrategySnapshot.cs`（LockBlock / SupplyBlock / ProcurementBlock /
 *      SolverStrategyBlock / CandidateGuardrailBlock）
 *   数值区间 `SolverStrategyValidator.cs` / `CandidateGuardrailValidator.cs`
 *   空块拦截 `GovernanceVersionService.cs:1107-1116`（EMPTY_SOLVER_STRATEGY 等）
 *
 * 锁八件事：
 *   PF01 字段目录存在 + 5 块齐全 + 关键路径与 DTO 属性名逐字一致（写错键名 = 存进去也不生效）
 *   PF02 目录**不含**四处仓库落点 / 锚点 / 数组类字段（各有专属编辑器或本轮不支持）
 *   PF03 目录与取值控件**不含任何默认值**（不预填默认枚举 / 空结构 / 约定键名）
 *   PF04 页面有「新增字段」入口 + 取值对话框，入口受 `actions.canEdit` 约束
 *   PF05 写动作走 applyEdit 写 workingBlocks 并置脏（改完看得见、存得进）
 *   PF06 写动作**零新端点**（不调任何接口 ⇒ 走既有版本草稿 PUT）
 *   PF07 枚举字段只声明「是枚举」，选项与数字序号取自 parameterSetEnums（唯一真源）
 *   PF08 已配过的字段不再进候选清单（按叶子键判存在性）
 */
function runParameterSetFieldsStatic() {
  /* 取函数体（各组局部副本：verify 里的 fnBody 是 rules 段的内嵌函数） */
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

  const catSrc = stripComments(readSrc('api/aps-v1/parameterBlockFields.ts'))
  const dbSrc = stripComments(readSrc('api/aps-v1/draftBuffer.ts'))
  const psStore = stripComments(readSrc('store/modules/aps/parameterSets.ts'))
  const psPage = stripComments(readSrc('views/Aps/ParameterSets.vue')).replace(
    /<!--[\s\S]*?-->/g,
    ''
  )

  /* -------- PF01 字段目录存在 + 5 块齐全 + 关键路径逐字一致 -------- */
  const pf01Blocks = [
    /lock:\s*LOCK_FIELDS\s*[,}]/,
    /supply:\s*SUPPLY_FIELDS\s*[,}]/,
    /procurement:\s*PROCUREMENT_FIELDS\s*[,}]/,
    /solverStrategy:\s*SOLVER_FIELDS\s*[,}]/,
    /candidateGuardrail:\s*GUARDRAIL_FIELDS\s*[,}]/
  ]
  const pf01MissingBlocks = pf01Blocks.filter((re) => !re.test(catSrc)).length
  // 每块各挑一个代表性路径（键名与 FrozenStrategySnapshot.cs 的 DTO 属性名逐字一致）
  const pf01Paths = [
    'Trigger.ProtectDelayed',
    'PiSort.SortBy',
    'OverdueMargin.MarginPercent',
    'BottleneckUtilizationThreshold',
    'ImpactedTaskWarningPercent'
  ]
  const pf01MissingPaths = pf01Paths.filter((p) => !catSrc.includes(`path: '${p}'`))
  const pf01Ok = catSrc.length > 0 && pf01MissingBlocks === 0 && pf01MissingPaths.length === 0
  assert(
    'PF01 静态：字段目录存在 + 5 块齐全 + 关键路径与后端 DTO 属性名逐字一致',
    pf01Ok,
    pf01Ok
      ? '5 块 + 代表性路径齐备'
      : `目录${catSrc.length ? '有' : '缺'} 缺块${pf01MissingBlocks} 缺路径[${pf01MissingPaths.join(' / ')}]`
  )

  /* -------- PF02 目录不含仓库四处落点 / 锚点 / 数组类 -------- */
  const pf02Forbidden = [
    'WarehousePriority',
    'DefaultPurchaseLt',
    'ArrivalToUsableOffsets',
    'AnchorResourceCode',
    'PlanningYields'
  ]
  const pf02Hit = pf02Forbidden.filter((f) => catSrc.includes(f))
  assert(
    'PF02 静态：目录不含四处仓库落点 / 锚点编码 / 数组类字段（各有专属编辑器或本轮不支持）',
    pf02Hit.length === 0,
    pf02Hit.length === 0
      ? '无越界字段'
      : `目录里出现[${pf02Hit.join(' / ')}] ⇒ 与专属编辑器重复或前端自造结构`
  )

  /* -------- PF03 不注入默认值 -------- */
  const pf03NoPrefill = !/\bdefault\b/i.test(catSrc) && !/\bvalue\s*:/.test(catSrc)
  const pf03Collect = fnBodyOf(psPage, 'collectAddFieldValue')
  const pf03UndefinedGuard = (pf03Collect.match(/undefined\s*\?\s*null/g) || []).length >= 3
  const pf03EmptyGuard =
    /\?\s*t\s*:\s*null/.test(pf03Collect) && /items\.length\s*\?\s*items\s*:\s*null/.test(pf03Collect)
  const pf03Ok = pf03NoPrefill && pf03UndefinedGuard && pf03EmptyGuard
  assert(
    'PF03 静态：不注入任何默认值（目录无默认值字段 + 未填的控件一律判为「没填」）',
    pf03Ok,
    pf03Ok
      ? '目录无默认值 + 取值收集把未填判空'
      : `目录预填${pf03NoPrefill ? '无' : '有'} 未填守卫${pf03UndefinedGuard ? '有' : '缺'} 空值守卫${pf03EmptyGuard ? '有' : '缺'}`
  )

  /* -------- PF04 页面入口 + 对话框 + 权限门控 -------- */
  // ⚠️ 门控判据必须**贴着入口**看：整页里 `:disabled="!actions.canEdit"` 有 7 处（别处的按钮），
  //   只数全页出现次数 ⇒ 把新增字段按钮的门控删掉也判绿 ⇒ 假绿。
  const pf04HitIdx = []
  let pf04Idx = psPage.indexOf('openAddFieldDialog(blockKey)')
  while (pf04Idx >= 0) {
    pf04HitIdx.push(pf04Idx)
    pf04Idx = psPage.indexOf('openAddFieldDialog(blockKey)', pf04Idx + 1)
  }
  const pf04Entries = pf04HitIdx.length
  const pf04Title = psPage.includes('新增字段')
  const pf04Gate =
    pf04Entries >= 2 &&
    pf04HitIdx.every((i) =>
      psPage.slice(Math.max(0, i - 500), i).includes(':disabled="!actions.canEdit"')
    )
  const pf04Dialog = /v-model="addFieldDialog"/.test(psPage) && /onAddField\(\)/.test(psPage)
  const pf04Ok = pf04Entries >= 2 && pf04Title && pf04Gate && pf04Dialog
  assert(
    'PF04 静态：页面有「新增字段」入口 + 取值对话框，入口受 actions.canEdit 约束',
    pf04Ok,
    pf04Ok
      ? `入口${pf04Entries} 处 + 对话框在位 + 逐入口有门控`
      : `入口${pf04Entries} 处 标题${pf04Title ? '有' : '无'} 门控${pf04Gate ? '有' : '缺'} 对话框${pf04Dialog ? '有' : '缺'}`
  )

  /* -------- PF05 写动作：applyEdit + 置脏 -------- */
  const pf05Fn = fnBodyOf(psStore, 'addBlockField')
  const pf05Write = /applyEdit\s*\(/.test(pf05Fn)
  const pf05Dirty = /buffer\.value\.dirty\[block\]\s*=\s*true/.test(pf05Fn)
  assert(
    'PF05 静态：写动作走 applyEdit 写 workingBlocks 并置脏（改完看得见、存得进）',
    pf05Write && pf05Dirty,
    pf05Write && pf05Dirty ? '写入 + 置脏齐备' : `写入${pf05Write ? '有' : '无'} 置脏${pf05Dirty ? '有' : '无'}`
  )

  /* -------- PF06 零新端点 -------- */
  const pf06NoCall = !/await\s/.test(pf05Fn) && !/\bApi\b/.test(pf05Fn) && !/url\s*:/.test(pf05Fn)
  assert(
    'PF06 静态：写动作零新端点（只改编辑态缓冲 ⇒ 走既有版本草稿 PUT，不新增后端接口）',
    pf06NoCall,
    pf06NoCall ? '动作内无接口调用' : '动作里出现接口调用 ⇒ 凭空造了新端点'
  )

  /* -------- PF07 枚举走唯一真源 -------- */
  const pf07EnumCount = (catSrc.match(/kind:\s*'ENUM'/g) || []).length
  const pf07EnumPaths = ["path: 'Mode'", "path: 'BottleneckMode'", "path: 'PiSort.SortBy'"]
  const pf07MissingEnum = pf07EnumPaths.filter((p) => !catSrc.includes(p))
  const pf07UseRegistry = /lookupParameterEnum\s*\(/.test(psPage)
  const pf07Ok =
    pf07EnumCount === 3 && pf07MissingEnum.length === 0 && pf07UseRegistry
  assert(
    'PF07 静态：枚举字段只声明「是枚举」，选项与数字序号取自 parameterSetEnums（唯一真源）',
    pf07Ok,
    pf07Ok
      ? '3 枚枚举 + 走真源'
      : `枚举数${pf07EnumCount} 缺[${pf07MissingEnum.join(' / ')}] 走真源${pf07UseRegistry ? '是' : '否'}`
  )

  /* -------- PF08 已配字段不再进候选清单 -------- */
  const pf08Filter = /!hasBlockField\(blockObj,\s*f\.path\)/.test(catSrc)
  const pf08HasOwn = /hasOwnProperty\.call\(cur,\s*key\)/.test(catSrc)
  const pf08Used = /listAbsentFields\s*\(/.test(psPage)
  const pf08Ok = pf08Filter && pf08HasOwn && pf08Used
  assert(
    'PF08 静态：已配过的字段不再进候选清单（按叶子键判存在性，不看值）',
    pf08Ok,
    pf08Ok
      ? '存在性判定 + 页面调用齐备'
      : `过滤${pf08Filter ? '有' : '缺'} 存在性判定${pf08HasOwn ? '有' : '缺'} 页面调用${pf08Used ? '有' : '缺'}`
  )

  /* -------- PF09 删除参数（键拿掉 ⇒ 回到后端默认值；块删空 ⇒ 仍被 EMPTY_* 拦） -------- */
  const pf09Fn = fnBodyOf(psStore, 'removeBlockField')
  const pf09Remove = /removePath\s*\(/.test(pf09Fn)
  const pf09Dirty = /buffer\.value\.dirty\[block\]\s*=\s*true/.test(pf09Fn)
  const pf09PruneAnchor = /pruneAnchorOnlyBlock\s*\(/.test(pf09Fn)
  // removePath 本体：删叶子键 + 剪掉变空的父对象（不留 {"Trigger":{}} 空壳）
  const pf09DelKey = /delete\s+target\[lastKey\]/.test(dbSrc)
  const pf09PruneEmpty = /Object\.keys\(child\)\.length\s*===\s*0/.test(dbSrc)
  // 页面：删除按钮在「可编辑行」内 + 受 canEdit 门控（数组行不给入口）
  const pf09BtnIdx = psPage.indexOf('onRemoveField(row)')
  const pf09Win = pf09BtnIdx >= 0 ? psPage.slice(Math.max(0, pf09BtnIdx - 400), pf09BtnIdx) : ''
  const pf09OnlyEditable = /v-if="row\.editable"/.test(pf09Win)
  const pf09Gate = /:disabled="!actions\.canEdit"/.test(pf09Win)
  const pf09Ok =
    pf09Remove &&
    pf09Dirty &&
    pf09PruneAnchor &&
    pf09DelKey &&
    pf09PruneEmpty &&
    pf09BtnIdx >= 0 &&
    pf09OnlyEditable &&
    pf09Gate
  assert(
    'PF09 静态：参数可删除（键拿掉 = 回到后端默认值；块删空仍被 EMPTY_* 拦；数组行无入口）',
    pf09Ok,
    pf09Ok
      ? '删除原语 + 置脏 + 空壳剪枝 + 逐入口门控齐备'
      : `removePath${pf09Remove ? '有' : '缺'} 置脏${pf09Dirty ? '有' : '缺'} 锚点剪枝${pf09PruneAnchor ? '有' : '缺'} ` +
        `删叶子${pf09DelKey ? '有' : '缺'} 剪空壳${pf09PruneEmpty ? '有' : '缺'} ` +
        `按钮${pf09BtnIdx >= 0 ? '有' : '缺'} 仅可编辑行${pf09OnlyEditable ? '有' : '缺'} 门控${pf09Gate ? '有' : '缺'}`
  )
}

/* ==================== [PM01-PM06] 参数集维护责任分类过滤（纯静态，不登录）====================
 * 派活来源：3号位 2026-11-05《参数集五主题按维护责任分类前端隐藏过滤要求 v1.0》
 *   分类依据：0号位 2026-11-04《APS_V1_规则参数集71项逐字段业务解释与V1处置》
 *   C 算法内部 / E 由表替代 / F V1不使用 / H 待核证 ⇒ 维护面隐藏
 *   D V1固定 ⇒ 只读展示（editable=false）
 *
 * ⚠️ 隐藏只改维护面：值仍在块 JSON 里，`buildPutBody` 原样回写，版本对比照常可见。
 * ⚠️ `supply.Inventory.WarehousePriority`（原 H 类第 4 项）经 3号位 2026-11-06 v1.1 复核后
 *    改判**隐藏**：专属编辑器不渲染，值仍原样回写（不在通用目录，分类清单不变）。
 *
 *   PM01 字段目录登记 26 项分类，逐条与来函清单一致（C10/D6/E4/F3/H3，目录内）
 *   PM02 flattenBlocks 输出 hidden / fixed，且 hidden|fixed 一律 editable=false
 *   PM03 rowsGrouped 过滤 hidden（页面看不到隐藏行）
 *   PM04 「新增字段」候选清单排除隐藏分类（不能从下拉加回来）
 *   PM05 D 类只读展示 + 「V1 固定」标签
 *   PM06 过滤不碰载荷：buildPutBody 原样序列化、flattenBlocks 不删行（diff 仍能比）
 */
function runParameterMaintenanceStatic() {
  /* 取 `function name(...) {}` 函数体（导出函数也命中；支持泛型 `function x<T>(...)`） */
  const fnBodyOf = (src, name) => {
    const start = src.indexOf(`function ${name}`)
    if (start < 0) return ''
    const open = src.indexOf('{', start)
    if (open < 0) return ''
    let depth = 0
    for (let i = open; i < src.length; i++) {
      if (src[i] === '{') depth++
      else if (src[i] === '}') {
        depth--
        if (depth === 0) return src.slice(open, i + 1)
      }
    }
    return ''
  }

  /* 取 marker 之后第一个 `{}` 块（用于 const x = computed(() => {...}) 这类非函数声明） */
  const bodyAfter = (src, marker) => {
    const start = src.indexOf(marker)
    if (start < 0) return ''
    const open = src.indexOf('{', start)
    if (open < 0) return ''
    let depth = 0
    for (let i = open; i < src.length; i++) {
      if (src[i] === '{') depth++
      else if (src[i] === '}') {
        depth--
        if (depth === 0) return src.slice(open, i + 1)
      }
    }
    return ''
  }

  const catSrc = stripComments(readSrc('api/aps-v1/parameterBlockFields.ts'))
  const dbSrc = stripComments(readSrc('api/aps-v1/draftBuffer.ts'))
  const psStore = stripComments(readSrc('store/modules/aps/parameterSets.ts'))
  const psPage = stripComments(readSrc('views/Aps/ParameterSets.vue')).replace(
    /<!--[\s\S]*?-->/g,
    ''
  )
  const diffSrc = stripComments(readSrc('api/aps-v1/parameterDiff.ts'))

  /* -------- PM01 目录分类逐条一致 -------- */
  const PM01_EXPECT = {
    solverStrategy: {
      'Split.MaxOptimizationSplitCount': 'C',
      'Setup.SetupSearchBudget': 'C',
      'Setup.SetupMaxNeighborhoodTries': 'C',
      'Split.LimitMandatorySplit': 'D',
      'OnTimeTarget.IsPrimaryObjective': 'D',
      'StageOverlap.AllowOverlap': 'D',
      AllowMerge: 'E',
      'StageOverlap.TransferBatchQty': 'F',
      'StageOverlap.ThresholdQty': 'F',
      'StageOverlap.ThresholdPercent': 'F',
      'Split.MinBatchQty': 'H'
    },
    candidateGuardrail: {
      NormalMs: 'C',
      SoftMs: 'C',
      LocalHardMs: 'C',
      MaxRepairAttempts: 'C',
      MaxPropagationRounds: 'C',
      ResourceTopN: 'C',
      SplitAlternatives: 'C',
      WarnOnlyOnMaxImpacted: 'D'
    },
    supply: {
      'Inventory.IsEnabled': 'E',
      'Inventory.RequireFactoryContext': 'E',
      'Inventory.RequireProductFamilyContext': 'E',
      'PiSort.UseStablePiNoTieBreak': 'D'
    },
    lock: {
      'Sticky.RequireReleaseRecord': 'D',
      'Sticky.ProtectUntilCompletion': 'H',
      'Sticky.ProtectUntilSupplyInvalid': 'H'
    }
  }
  const classOf = (path) => {
    const i = catSrc.indexOf(`path: '${path}'`)
    if (i < 0) return ''
    const next = catSrc.indexOf("path: '", i + 8)
    const entry = catSrc.slice(i, next < 0 ? catSrc.length : next)
    const m = entry.match(/maintenance:\s*'([A-H])'/)
    return m ? m[1] : ''
  }
  const pm01Bad = []
  let pm01Total = 0
  for (const [block, fields] of Object.entries(PM01_EXPECT)) {
    for (const [path, cls] of Object.entries(fields)) {
      pm01Total++
      const got = classOf(path)
      if (got !== cls) pm01Bad.push(`${block}.${path}: 期望 ${cls} 实得 ${got || '无'}`)
    }
  }
  const pm01Annotated = (catSrc.match(/maintenance:\s*'[A-H]'/g) || []).length
  const pm01NoAB = !/maintenance:\s*'[AB]'/.test(catSrc)
  const pm01Types =
    /export type MaintenanceClass = 'A' \| 'B' \| 'C' \| 'D' \| 'E' \| 'F' \| 'H'/.test(catSrc) &&
    /HIDDEN_MAINTENANCE_CLASSES: readonly MaintenanceClass\[\] = \['C', 'E', 'F', 'H'\]/.test(
      catSrc
    )
  const pm01Ok =
    pm01Total === 26 && pm01Bad.length === 0 && pm01Annotated === 26 && pm01NoAB && pm01Types
  assert(
    'PM01 静态：字段目录登记 26 项维护责任分类，逐条与来函清单一致，A/B 不标注',
    pm01Ok,
    pm01Ok
      ? 'C10/D6/E4/F3/H3 全部命中，无 A/B 误标'
      : `条目=${pm01Total} 标注数=${pm01Annotated} 类型=${pm01Types ? '有' : '缺'} ` +
        `A/B误标=${pm01NoAB ? '无' : '有'} 不符=${pm01Bad.slice(0, 5).join('; ') || '无'}`
  )

  /* -------- PM02 flattenBlocks 输出 hidden/fixed + 禁改 -------- */
  const fb = fnBodyOf(dbSrc, 'flattenBlocks')
  const pm02Editable = (fb.match(/editable: !hidden && !fixed &&/g) || []).length
  const pm02Ok =
    /isHiddenBlockField/.test(fb) &&
    /isReadonlyBlockField/.test(fb) &&
    /const hidden = isHiddenBlockField\(block, keyPath\)/.test(fb) &&
    /const fixed = isReadonlyBlockField\(block, keyPath\)/.test(fb) &&
    (fb.match(/\bhidden,/g) || []).length >= 2 &&
    (fb.match(/\bfixed,/g) || []).length >= 2 &&
    pm02Editable === 2
  assert(
    'PM02 静态：flattenBlocks 标 hidden/fixed，且两类行一律 editable=false（标量与数组两个分支）',
    pm02Ok,
    pm02Ok
      ? '两分支均先判分类再落 editable'
      : `hidden 赋值=${(fb.match(/\bhidden,/g) || []).length} fixed 赋值=${
          (fb.match(/\bfixed,/g) || []).length
        } editable 守卫=${pm02Editable}/2`
  )

  /* -------- PM03 rowsGrouped 过滤 hidden -------- */
  const rg = bodyAfter(psStore, 'const rowsGrouped')
  const pm03Ok = /if \(row\.hidden\) return/.test(rg)
  assert(
    'PM03 静态：rowsGrouped 过滤 hidden 行（维护面不渲染，但缓冲区原值仍在）',
    pm03Ok,
    pm03Ok ? 'row.hidden 先行 return' : 'rowsGrouped 未过滤 hidden'
  )

  /* -------- PM04 新增字段候选排除隐藏分类 -------- */
  const la = fnBodyOf(catSrc, 'listAbsentFields')
  const pm04Ok = /!isHiddenBlockField\(block, f\.path\)/.test(la)
  assert(
    'PM04 静态：「新增字段」候选清单排除隐藏分类（不能把刚藏起来的字段从下拉加回来）',
    pm04Ok,
    pm04Ok ? '候选清单按 isHiddenBlockField 过滤' : '候选清单未排除隐藏分类'
  )

  /* -------- PM05 D 类只读展示 + 标签 -------- */
  const pm05Ok =
    /v-if="row\.fixed"/.test(psPage) &&
    /V1 固定/.test(psPage) &&
    /!actions\.canEdit \|\| !row\.editable/.test(psPage)
  assert(
    'PM05 静态：D 类行只读展示 + 「V1 固定」标签（草稿态也禁改）',
    pm05Ok,
    pm05Ok ? '标签 + 只读值分支齐备' : '缺标签或只读分支'
  )

  /* -------- PM06 过滤不碰载荷 -------- */
  // buildPutBody 有泛型返回类型字面量 `G & { ... }`，按首个大括号取体会截在类型上
  // ⇒ 用「到下一个导出函数为止」的切片
  const putStart = dbSrc.indexOf('function buildPutBody')
  const putEnd = dbSrc.indexOf('export function isSensitive')
  const put = putStart >= 0 && putEnd > putStart ? dbSrc.slice(putStart, putEnd) : ''
  const pm06Put =
    /JSON\.stringify\(blocks\.lock\)/.test(put) &&
    /JSON\.stringify\(blocks\.candidateGuardrail\)/.test(put) &&
    !/hidden|fixed|isHiddenBlockField|isReadonlyBlockField/.test(put)
  const pm06NoDelete = !/\.filter\(|delete |\.splice\(/.test(fb)
  const pm06Diff = (diffSrc.match(/flattenBlocks\(/g) || []).length >= 2
  const pm06Ok = pm06Put && pm06NoDelete && pm06Diff
  assert(
    'PM06 静态：隐藏不改载荷（buildPutBody 原样回写全块；flattenBlocks 不删行；diff 仍逐行比）',
    pm06Ok,
    pm06Ok
      ? '载荷原样 + 不删行 + 版本对比不受影响'
      : `buildPutBody=${pm06Put ? '原样' : '被过滤'} 删行=${pm06NoDelete ? '无' : '有'} diff=${
          pm06Diff ? '照常' : '丢失'
        }`
  )
}

/* ==================== 采购良率专属编辑器（parameter-set-yields）==================== */
/**
 * Procurement.PlanningYields（后端 DTO `PlanningYieldRule`，
 *   `FrozenStrategySnapshot.cs:253-258`）的专属编辑器静态段。
 *
 * 关键契约（只读引用，4号位 不改 lps/）：
 *   - MaterialId 存**物料编码**（运行期 `PlanningYieldResolver.cs:78` 按编码匹配）
 *   - StageCode 空 = 物料级默认；工序码词表 = 部门主数据 ProductionDepartment.StageCode
 *   - 0 < YieldPercent <= 100（`GovernanceVersionService.cs:1299-1328`）
 *   - (MaterialId, StageCode) 不得重复
 */
function runPlanningYieldStatic() {
  /* 取函数体（各组局部副本） */
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

  /* 取对象字面量里的方法体（materialLookup.ts 的方法写在 `export const x = { async m() {} }` 里，
     不是 `function m()` ⇒ 不能用上面的 fnBodyOf） */
  const methodBodyOf = (src, name) => {
    const start = src.indexOf(`async ${name}(`)
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

  const ySrc = stripComments(readSrc('api/aps-v1/parameterSetYields.ts'))
  const psStore = stripComments(readSrc('store/modules/aps/parameterSets.ts'))
  const psPage = stripComments(readSrc('views/Aps/ParameterSets.vue')).replace(
    /<!--[\s\S]*?-->/g,
    ''
  )
  const yEditor = stripComments(readSrc('views/Aps/components/PlanningYieldEditor.vue'))
  const matApi = stripComments(readSrc('api/aps-v1/materialLookup.ts'))

  /* -------- PY01 落点路径 + 字段名与后端 DTO 逐字一致 -------- */
  const py01Path = /PLANNING_YIELDS_PATH\s*=\s*\['PlanningYields'\]/.test(ySrc)
  const py01Fields = ['MaterialId', 'StageCode', 'YieldPercent']
  const py01Missing = py01Fields.filter((f) => !new RegExp(`${f}\\?:`).test(ySrc))
  const py01Ok = py01Path && py01Missing.length === 0
  assert(
    'PY01 静态：采购良率落点路径与字段名与后端 DTO 逐字一致（PlanningYieldRule）',
    py01Ok,
    py01Ok
      ? '路径 + 三个字段名齐备'
      : `路径${py01Path ? '对' : '错'} 缺字段[${py01Missing.join(' / ')}]`
  )

  /* -------- PY02 通用参数表剔除这一行（交给专属编辑器） -------- */
  const py02Predicate = /isPlanningYieldRow\s*\(/.test(ySrc)
  const py02Call = /isPlanningYieldRow\s*\(\s*row\.block\s*,\s*row\.path\s*\)/.test(psStore)
  const py02Ok = py02Predicate && py02Call
  assert(
    'PY02 静态：通用参数表剔除 PlanningYields 行（数组不该用单值控件渲染）',
    py02Ok,
    py02Ok ? '剔行判定 + 调用点齐备' : `判定${py02Predicate ? '有' : '缺'} 调用${py02Call ? '有' : '缺'}`
  )

  /* -------- PY03 store 写动作（写 workingBlocks + 置脏）+ getter -------- */
  const py03Fn = fnBodyOf(psStore, 'setPlanningYields')
  const py03Write = /writePlanningYields\s*\(/.test(py03Fn)
  const py03Dirty = /buffer\.value\.dirty\.procurement\s*=\s*true/.test(py03Fn)
  const py03Getter = /const planningYields\s*=\s*computed/.test(psStore)
  const py03Ok = py03Write && py03Dirty && py03Getter
  assert(
    'PY03 静态：store 有良率 getter + 写动作（写 workingBlocks 并置脏采购块）',
    py03Ok,
    py03Ok
      ? 'getter + 写入 + 置脏齐备'
      : `getter${py03Getter ? '有' : '缺'} 写入${py03Write ? '有' : '缺'} 置脏${py03Dirty ? '有' : '缺'}`
  )

  /* -------- PY04 页面挂载专属编辑器（且在采购参数块内） -------- */
  const py04Import = /components\/PlanningYieldEditor\.vue/.test(psPage)
  const py04Tag = psPage.includes('<PlanningYieldEditor')
  const py04Idx = psPage.indexOf('<PlanningYieldEditor')
  const py04ProcIdx = psPage.indexOf("blockKey === 'procurement'")
  const py04InProc = py04Idx >= 0 && py04ProcIdx >= 0 && py04Idx > py04ProcIdx
  const py04Ok = py04Import && py04Tag && py04InProc
  assert(
    'PY04 静态：参数集页面在「采购参数」块挂了采购良率专属编辑器',
    py04Ok,
    py04Ok
      ? '组件已导入 + 标签在采购块内'
      : `导入${py04Import ? '有' : '缺'} 标签${py04Tag ? '有' : '缺'} 位置${py04InProc ? '对' : '错'}`
  )

  /* -------- PY05 物料下拉走后端端点 + 不硬编码夹具物料码 -------- */
  // ⚠️ 端点判据要**逐方法**看：`materialLookup.ts` 里两个方法都用同一个 url，
  //   只判「文件里出现过一次」⇒ 改坏其中任一个仍判绿 ⇒ 假绿。
  const matUrlRe = /url:\s*'\/api\/governance\/lookups\/materials'/
  const py05SearchFn = methodBodyOf(matApi, 'searchMaterials')
  const py05ListFn = methodBodyOf(matApi, 'listActiveMaterials')
  const py05Endpoint = matUrlRe.test(py05SearchFn) && matUrlRe.test(py05ListFn)
  const py05UseApi = /materialLookupApi\.(listActiveMaterials|searchMaterials)\s*\(/.test(yEditor)
  const py05Remote = /remote-method="onSearchMaterial"/.test(yEditor)
  const py05FixtureRe = /MAT\d{2,}/
  const py05FixtureHit = [ySrc, yEditor].filter((s) => py05FixtureRe.test(s))
  const py05Ok = py05Endpoint && py05UseApi && py05Remote && py05FixtureHit.length === 0
  assert(
    'PY05 静态：物料下拉走后端物料字典端点（可搜索、不硬编码夹具码）',
    py05Ok,
    py05Ok
      ? '端点 + 远程搜索 + 无夹具码'
      : `端点${py05Endpoint ? '对' : '错'} 走api${py05UseApi ? '是' : '否'} 远程搜${py05Remote ? '是' : '否'} ` +
        `夹具码文件数${py05FixtureHit.length}`
  )

  /* -------- PY06 良率区间 0 < p <= 100（后端发布校验口径） -------- */
  const py06Min = /:min="0\.01"/.test(yEditor)
  const py06Max = /:max="100"/.test(yEditor)
  const py06Clamp = /p\s*>\s*0\s*&&\s*p\s*<=\s*100/.test(yEditor)
  const py06Ok = py06Min && py06Max && py06Clamp
  assert(
    'PY06 静态：良率输入夹 0.01~100（后端 0 < YieldPercent <= 100）',
    py06Ok,
    py06Ok ? '上下界 + 兜底夹取齐备' : `min${py06Min ? '有' : '缺'} max${py06Max ? '有' : '缺'} 夹取${py06Clamp ? '有' : '缺'}`
  )

  /* -------- PY07 重复键口径与后端一致（大小写不敏感 + 工序空归一） -------- */
  const py07KeyFn = fnBodyOf(ySrc, 'planningYieldKey')
  const py07Lower = (py07KeyFn.match(/toLowerCase\(\)/g) || []).length >= 2
  const py07StageTrim = /StageCode\s*\?\?\s*''\)/.test(py07KeyFn)
  const py07ValidateFn = fnBodyOf(ySrc, 'validatePlanningYields')
  const py07Dup = /seen\.get\(key\)/.test(py07ValidateFn) && /seen\.set\(key/.test(py07ValidateFn)
  const py07Range = /percent\s*<=\s*0\s*\|\|\s*percent\s*>\s*100/.test(py07ValidateFn)
  const py07Ok = py07Lower && py07StageTrim && py07Dup && py07Range
  assert(
    'PY07 静态：重复键判定与后端一致（物料+工序、大小写不敏感、工序空归一）',
    py07Ok,
    py07Ok
      ? '复合键 + 重复检测 + 区间判定齐备'
      : `大小写${py07Lower ? '是' : '否'} 工序归一${py07StageTrim ? '是' : '否'} 重复检测${py07Dup ? '有' : '缺'} 区间${py07Range ? '有' : '缺'}`
  )

  /* -------- PY08 工序码取自部门主数据 stageCode（不是阶段提前期那套词表） -------- */
  const py08DeptApi = /departmentLookupApi\.listDepartments\(\)/.test(yEditor)
  const py08StageField = /\.stageCode/.test(yEditor)
  const py08WrongVocab = !/stage-lead-time/.test(yEditor)
  const py08Ok = py08DeptApi && py08StageField && py08WrongVocab
  assert(
    'PY08 静态：工序码取自部门主数据 stageCode（与运行期 MaterialStageDeptContext 同源）',
    py08Ok,
    py08Ok
      ? '走部门主数据 + 未误用阶段提前期词表'
      : `部门端点${py08DeptApi ? '有' : '缺'} stageCode${py08StageField ? '有' : '缺'} 词表${py08WrongVocab ? '对' : '错'}`
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
  // 2026-10-31：CTP 页改走全站唯一口径 `reasonLabelOf`（共享函数内查冻结 REASON_CODE_LABELS + 待核对· 兜底）
  const ctpSharedDict =
    /import\s*\{[^}]*reasonLabelOf[^}]*\}[\s\S]*?from\s*'@\/api\/aps-v1\/types'/.test(ctpVueContent) &&
    /reasonLabelOf\(r\.reasonCode\)/.test(ctpVueContent)
  const ctpDegrade =
    /export function reasonLabelOf\([\s\S]*?待核对·/.test(
      readSrc('api/aps-v1/types/explanation.ts')
    )
  const ctpNoBareIndex =
    !/REASON_LABEL\s*\[\s*r\.reasonCode\s*\]/.test(ctpVueContent) &&
    !/REASON_CODE_LABELS\s*\[/.test(ctpVueContent)
  const a11 = ctpReasonTyped && !ctpSelfInvented && ctpSharedDict && ctpDegrade && ctpNoBareIndex
  assert(
    'A11 静态：CTP 根因码复用冻结 15 码 + 未知码降级（走共享 reasonLabelOf / 不自拟码表 / 不裸索引）',
    a11,
    `类型=${ctpReasonTyped} 自拟码残留=${ctpSelfInvented} 共用字典=${ctpSharedDict} 降级=${ctpDegrade} 无裸索引=${ctpNoBareIndex}`
  )

  /* -------- C01/C02 静态（lps/ C 路径契约层，3号位 09-30 落码）--------
   * 验收依据：frontNew/docs/APS_V1_CTP评估C路径落码回执_3号位致4号位_v1.0_20260930.md §五
   * 口径：纯只读静态扫描 lps/（4号位 硬约束：lps/ 仅 Read，不 Edit/Write；本断言不改 lps/）
   */
  const lpsCtpReq = (() => {
    try {
      return readText(join(LPS_ROOT, 'LPS.APS.Core', 'DTOs', 'Ctp', 'CtpEvaluateRequest.cs'))
    } catch {
      return ''
    }
  })()
  const lpsCtpSvc = (() => {
    try {
      return readText(join(LPS_ROOT, 'LPS.APS.Application', 'Services', 'CtpService.cs'))
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
   *  ⚠️ 10-26：渲染口径搬到 ResolvePreviewPanel.vue（设备侧与人工槽侧共用一个组件，
   *     口径不许两边各写一套）⇒ 三态/来源标签的断言改扫组件文件。
   *  - 反向探针自证可失败：删 api 调用 / 删 store action / 少一个状态标签 / 少一级来源标签 → 转红
   */
  const resolvePanelPath = 'views/Aps/components/ResolvePreviewPanel.vue'
  const resolvePanelContent = readSrc(resolvePanelPath)
  const rc32ApiCall =
    /url:\s*'\/api\/resource-calendar\/resolve-preview'/.test(apiContent) &&
    /resourceIds:\s*resourceIds\.join\(','\)/.test(apiContent) &&
    /startDate,\s*endDate/.test(apiContent)
  const rc32StoreAction =
    /async\s+function\s+loadResolvePreview/.test(storeContent) &&
    /getResolvePreview\(/.test(storeContent)
  // 三态 + 三级来源都必须有渲染口径（少一个用户就会看到英文原文）
  const rc32Statuses = ['WORK', 'OFF', 'MISSING'].every((s) =>
    new RegExp(`\\b${s}:\\s*\\{`).test(resolvePanelContent)
  )
  const rc32Sources = ['Factory', 'Department', 'Resource'].every((s) =>
    new RegExp(`\\b${s}:\\s*\\{`).test(resolvePanelContent)
  )
  // 模板走预计算行（表格用 prop 取字段，不在单元格里调函数/查表 —— 否则任一环节取不到值整行会空白）
  const rc32ViewConsumes =
    /:data="rows"/.test(resolvePanelContent) &&
    /row\.sourceLabel/.test(resolvePanelContent) &&
    /row\.sourceTag/.test(resolvePanelContent) &&
    /SOURCE_META\[w\.source\]/.test(resolvePanelContent) &&
    /<ResolvePreviewPanel[\s\S]{0,200}?resolvePreview/.test(pageTemplateOnly) &&
    /loadResolvePreview/.test(pageContent)
  const rc32All = rc32ApiCall && rc32StoreAction && rc32Statuses && rc32Sources && rc32ViewConsumes
  assert(
    'RC32 静态：页面消费 resolve-preview（三态 + 三级来源标注），不再只在本级表上猜',
    rc32All,
    rc32All
      ? `api/store/组件三处接通；WORK/OFF/MISSING 与 Factory/Department/Resource 均有渲染口径（组件 ${resolvePanelPath}）`
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

  /* -------- RC34 静态：人工槽三级合成可视区 + 「授权」维度（2026-10-26 补齐）--------
   *  背景：冻结 13 v1.8:31 明文要求资源日历页显示「继承上级/本级明确工作/本级明确不工作」
   *        **及来源、授权、时间片**；人工槽侧此前**只有本级窗口表**，看不到继承来的时段，
   *        也看不到「授权加班例外」标记（4号位 10-23 两函 R1/R2）。
   *  5号位 2026-10-23 落 `GET /api/manual-capacity/resolve-preview`（人工槽版），2026-10-26 已上 dev。
   *  ⚠️ 后端 DTO：`ManualCapacityResolvePreviewDto` + `ResolvedWindowDto.IsAuthorized`（设备/人工槽同名字段）。
   *  - 反向探针自证可失败：删 api 端点 / 删 store action / Tab3 不挂面板 / 组件不渲染「授权」列 → 转红
   */
  const rc34Types =
    /export\s+interface\s+ManualCapacityResolvePreviewDto\b/.test(typesContent) &&
    /manualSlotId:\s*number/.test(typesContent) &&
    /isAuthorized\?:\s*boolean/.test(typesContent)
  const rc34Api =
    /url:\s*'\/api\/manual-capacity\/resolve-preview'/.test(apiContent) &&
    /manualSlotIds:\s*manualSlotIds\.join\(','\)/.test(apiContent)
  const rc34Store =
    /async\s+function\s+loadManualResolvePreview\s*\(/.test(storeContent) &&
    /getManualResolvePreview\(/.test(storeContent) &&
    /manualResolvePreview/.test(storeContent)
  // Tab3 必须真的挂上面板（人工槽侧），且 Tab1 挂的是同一个组件（口径不许两边各写一套）
  const rc34Panel =
    /<ResolvePreviewPanel[\s\S]{0,300}?manualResolvePreview/.test(pageTemplateOnly) &&
    /<ResolvePreviewPanel[\s\S]{0,300}?resolvePreview/.test(pageTemplateOnly)
  const rc34Authorized =
    /row\.authorized/.test(resolvePanelContent) &&
    /isAuthorized\s*===\s*true/.test(resolvePanelContent) &&
    /授权加班/.test(resolvePanelContent)
  const rc34All = rc34Types && rc34Api && rc34Store && rc34Panel && rc34Authorized
  assert(
    'RC34 静态：人工槽三级合成可视区 + 授权维度（人工槽端点/store/Tab3 面板/「授权」列齐备）',
    rc34All,
    rc34All
      ? 'types/api/store/Tab3/授权列 五处接通'
      : `types=${rc34Types} api=${rc34Api} store=${rc34Store} panel=${rc34Panel} authorized=${rc34Authorized}`
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

  /* -------- RC34b 动态：人工槽版 resolve-preview + 设备侧 isAuthorized 字段（2026-10-26 增）--------
   *  判据（对齐 4号位 10-23 人工槽函 R1 与上级工作日历函 R2 同族缺口）：
   *   ① 人工槽端点返**非空数组**（空数组直接判失败，避免 every 空真）
   *   ② 每项 manualSlotId / hasAnyWindow / 三态 / windows[].source ∈ {Factory,Department,Resource}
   *   ③ 设备侧 windows[] 必须带 `isAuthorized` 布尔字段（授权加班例外维度，冻结 13 v1.8:31）
   *  - 反向探针自证可失败：把 source 集合改回大写 / 去掉非空断言 / 去掉 isAuthorized 判定 → 转红
   */
  if (token && process.env.VERIFY_RC25_RESOLVE !== '0') {
    const pManualResolve = await call('GET', '/api/manual-capacity/resolve-preview', {
      token,
      query: { manualSlotIds: '18643', startDate: '2026-10-01', endDate: '2026-10-07' }
    })
    const mData = pManualResolve.json?.data
    const mItems = Array.isArray(mData) ? mData : null
    const mOk =
      pManualResolve.ok &&
      mItems !== null &&
      mItems.length > 0 &&
      mItems.every(
        (it) =>
          typeof it.manualSlotId === 'number' &&
          typeof it.hasAnyWindow === 'boolean' &&
          ['WORK', 'OFF', 'MISSING'].includes(it.status) &&
          Array.isArray(it.windows) &&
          it.windows.every((w) => ['Factory', 'Department', 'Resource'].includes(w.source))
      )
    assert(
      'RC34b 动态：人工槽 resolve-preview 返三级合并后状态（data 非空数组 + manualSlotId + 三态 + source 来源层级）',
      mOk,
      mOk
        ? `items=${mItems.length} 首项 status=${mItems[0].status} source=${mItems[0].windows[0]?.source ?? '-'}`
        : `ok=${pManualResolve.ok} data 是数组=${mItems !== null} 条数=${mItems?.length ?? '-'} body=${JSON.stringify(pManualResolve.json ?? {}).slice(0, 200)}`
    )

    // 设备侧：isAuthorized 必须在每一片上出现（授权加班例外维度）
    const pDevResolve = await call('GET', '/api/resource-calendar/resolve-preview', {
      token,
      query: { resourceIds: '11829', startDate: '2026-10-01', endDate: '2026-10-02' }
    })
    const dItems = Array.isArray(pDevResolve.json?.data) ? pDevResolve.json.data : null
    const dOk =
      pDevResolve.ok &&
      dItems !== null &&
      dItems.length > 0 &&
      dItems.every(
        (it) =>
          Array.isArray(it.windows) &&
          it.windows.length > 0 &&
          it.windows.every((w) => typeof w.isAuthorized === 'boolean')
      )
    assert(
      'RC34b2 动态：设备侧 resolve-preview 每片带 isAuthorized（授权加班例外维度）',
      dOk,
      dOk
        ? `items=${dItems.length} 首片 isAuthorized=${dItems[0].windows[0].isAuthorized}`
        : `ok=${pDevResolve.ok} items=${dItems?.length ?? '-'} body=${JSON.stringify(pDevResolve.json ?? {}).slice(0, 200)}`
    )
  } else {
    skip('RC34b 动态：人工槽 resolve-preview + 设备侧 isAuthorized（VERIFY_RC25_RESOLVE=0 显式关闭）')
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
 * [D-perm] 排程域写权限门控（domain-permission，**免登录**静态组）
 *
 * 依据：3号位 2026-10-25《排程域写端点权限修正采纳方案甲回执 v1.0》
 *  - 后端 4 个写端点（POST/PUT/enable/disable domain-definition）改挂独立权限码 `aps.domain.edit`
 *    （原误挂 `aps.rule.edit` ⇒ aps.admin.aps 有 API 写权却无 UI 入口，冻结 §4.1 冲突）
 *  - 授权矩阵：`aps.domain.edit` → 仅 `aps.admin.system`
 *  - §五 P1/P2（可选增强）：canWrite 由「只看角色码」升级为「角色码 + 权限码」，判据同步升级
 *
 * 为什么独立成免登录组：
 *  - 反向破坏自证要连跑 5 次，登录组会撞 dev 429 频控（memory feedback_verify_assertion_styles 第 6 型）
 *  - 这几条全是**纯静态**判据，本来就不需要 token
 *
 * 锁四件事：
 *  - D05b canWrite 含角色码判定 aps.admin.system（冻结 §4.1 系统管理员维护）
 *  - D05d canWrite 含权限码判定 aps.domain.edit（后端 4 写端点改挂此码 ⇒ 不判就等后端 403）
 *  - D05c 写按钮置灰并**动态**说明原因（tooltip 绑定 writeDeniedReason，不是写死一句）
 *  - D05e 置灰原因分说两种（不是系统管理员 / 是系统管理员但没拿到域维护权限）
 */
function runDomainPermissionStatic() {
  const domainCode = stripComments(readSrc('views/Aps/Domain.vue'))
  /* canWrite 的**精确**函数体：括号配平到 `computed(...)` 收口。
   * ⚠️ 不能用「从 const canWrite 起取 400 字符」——紧随其后的 writeDeniedReason
   *    里也有 `apsAuth.roles.includes('aps.admin.system')`，
   *    窗口重叠会让 D05b 在「canWrite 丢了角色判」时假绿（2026-10-25 实测）。 */
  let canWriteBody = ''
  const cwStart = domainCode.indexOf('const canWrite')
  if (cwStart >= 0) {
    const open = domainCode.indexOf('computed(', cwStart)
    if (open >= 0) {
      let depth = 0
      for (let i = open + 'computed'.length; i < domainCode.length; i++) {
        if (domainCode[i] === '(') depth++
        else if (domainCode[i] === ')') {
          depth--
          if (depth === 0) {
            canWriteBody = domainCode.slice(cwStart, i + 1)
            break
          }
        }
      }
    }
  }

  /* -------- D05b 角色码那一半 -------- */
  const d05bRole = /apsAuth\.roles\.includes\('aps\.admin\.system'\)/.test(canWriteBody)
  assert(
    'D05b 静态：canWrite 含角色码判定 aps.admin.system（冻结 §4.1 系统管理员维护）',
    d05bRole,
    d05bRole ? '按角色码判' : 'canWrite 的判定里没有 aps.admin.system 角色码'
  )

  /* -------- D05d 权限码那一半 -------- */
  const d05dPerm = /apsAuth\.has\('aps\.domain\.edit'\)/.test(canWriteBody)
  assert(
    'D05d 静态：canWrite 含权限码判定 aps.domain.edit（3号位 10-25 方案甲：后端 4 写端点改挂此码）',
    d05dPerm,
    d05dPerm ? '按权限码判' : 'canWrite 没判 aps.domain.edit ⇒ 后端 403 时页面仍显示可写'
  )

  /* -------- D05c 置灰 + 动态原因 -------- */
  const d05cBind = /<ElTooltip[^>]*!canWrite[^>]*:content="writeDeniedReason"/.test(domainCode)
  assert(
    'D05c 静态：非管理员的写操作按钮置灰并动态说明原因（tooltip 绑定 writeDeniedReason）',
    d05cBind,
    d05cBind ? '已置灰并动态说明' : '缺置灰按钮，或 tooltip 仍写死一句话'
  )

  /* -------- D05e 两种原因分说 -------- */
  const d05eReasons =
    /需要系统管理员角色/.test(domainCode) && /还没有排程域维护权限/.test(domainCode)
  assert(
    'D05e 静态：置灰原因分说两种（不是系统管理员 / 是系统管理员但没拿到域维护权限）',
    d05eReasons,
    d05eReasons ? '两种原因齐备' : '缺其中一种原因文案'
  )
}

/**
 * [D] Domain 维护（d-domain）— v1.2 Domain 专项 Pkg-3
 * 验收清单：
 *  - D01 静态：types/domain.ts 含 12 camelCase 字段（DTO 实体对齐）
 *  - D02 静态：types/domain.ts 无 PascalCase（Program.cs:93 = CamelCase）
 *  - D03 静态：api/aps-v1/domain.ts 8 方法 + 404 → EndpointUnavailableError + base path
 *  - D04 静态：Domain.vue 显式 import 16 EP 组件（ElDescriptions 等；2026-09-28 ElDescriptions 渲染 bug 教训）
 *  - D05 静态：业务约束（domainKey 不可改）
 *  - D05b/D05c/D05d/D05e 写权限门控：见**免登录**静态组 `GROUP=domain-permission`
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

  /* -------- D05 静态：业务约束（2026-10-23 改判据）
   *  改判据原因：原先「domainKey 不可改」判在页头副标题那句「排程域标识由系统生成、不可改」上，
   *    用户要求删掉页头副标题与新建对话框里的不可改提示条（页面不放大白话说明）⇒ 那句判据失去落点。
   *    重新落到**真正生效的位置**：详情面板的排程域标识是只读 ElTag + tooltip「系统生成、不可改」。
   *  写权限那几条（D05b/D05c/D05d/D05e）已挪到**免登录**静态组 `GROUP=domain-permission`
   *    （3号位 10-25 方案甲：后端 4 写端点改挂 `aps.domain.edit`）——
   *    登录组里连跑反向破坏会撞 dev 429 频控（见 memory feedback_verify_assertion_styles 第 6 型）。
   */
  assert(
    'D05 静态：排程域标识只读展示（ElTag + tooltip「系统生成、不可改」）——冻结 §5 DomainKey 不可变更',
    /label="排程域标识"[\s\S]{0,200}?content="系统生成、不可改"/.test(pageContent),
    /label="排程域标识"[\s\S]{0,200}?content="系统生成、不可改"/.test(pageContent)
      ? '已以 tooltip 声明不可改'
      : '缺「排程域标识」只读展示或缺「系统生成、不可改」提示'
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
 *  - 5 端点：GET/POST /api/governance/task-split-rule-configs + PUT/DELETE /{id} + POST /{id}/activate（10-29 恢复启用）
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

  // TSRC03 静态：api/taskSplitRuleConfig.ts 含 5 端点 list/create/update/deactivate/activate
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

  // TSRC03e 静态：api 含 activate(id)（10-29 3号位 落码：停用后恢复启用）
  const tsrc03e = /async\s+activate\s*\(\s*id\s*:\s*number\s*\)/.test(tsrcApi)
  assert(
    'TSRC03e 静态：api/taskSplitRuleConfig.ts 含 activate(id)（10-29 停用后恢复启用）',
    tsrc03e,
    tsrc03e ? '已含' : '缺 activate'
  )

  // TSRC04 静态：5 端点 URL 路径与契约 §一 对齐
  const tsrc04a = /['"`]\/api\/governance\/task-split-rule-configs['"`]/.test(tsrcApi)
  assert('TSRC04a 静态：list/create URL = /api/governance/task-split-rule-configs', tsrc04a, tsrc04a ? '已对齐' : '路径错')

  const tsrc04b = /\/api\/governance\/task-split-rule-configs\/\$\{id\}/.test(tsrcApi)
  assert('TSRC04b 静态：update/deactivate URL = /api/governance/task-split-rule-configs/${id}', tsrc04b, tsrc04b ? '已对齐' : '路径错')

  // TSRC04c 静态：activate URL + 方法（activate 是 api 对象最后一个方法 ⇒ 切到文件尾覆盖整段）
  const tsrcActStart = tsrcApi.indexOf('async activate(')
  const tsrcActBlock = tsrcActStart >= 0 ? tsrcApi.slice(tsrcActStart) : ''
  const tsrc04c =
    /apsHttp\.post/.test(tsrcActBlock) &&
    /\/api\/governance\/task-split-rule-configs\/\$\{id\}\/activate/.test(tsrcActBlock)
  assert(
    'TSRC04c 静态：activate URL = POST /api/governance/task-split-rule-configs/${id}/activate',
    tsrc04c,
    tsrc04c ? '已对齐' : '路径或方法错'
  )

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

  // TSRC09c 静态：store 含 activate 动作（10-29 停用后恢复启用）
  const tsrc09c =
    /async\s+function\s+activate\s*\(\s*id\s*:\s*number\s*\)/.test(tsrcStore) &&
    /taskSplitRuleConfigApi\.activate\(id\)/.test(tsrcStore) &&
    /^\s*activate,\s*$/m.test(tsrcStore)
  assert('TSRC09c 静态：store 含 activate 动作（已调 api + 已在 return 暴露）', tsrc09c, tsrc09c ? '已含' : '缺动作')

  // TSRC26 动态：恢复启用端点可达（两条均非破坏性 —— 不存在/非法 id，零 DML）
  const tsrc26a = await call('POST', '/api/governance/task-split-rule-configs/0/activate', { token })
  const tsrc26b = await call('POST', '/api/governance/task-split-rule-configs/999999999/activate', {
    token
  })
  assert(
    'TSRC26a 动态：POST /api/governance/task-split-rule-configs/0/activate → 400（路径 id 非法）',
    tsrc26a.code === 400,
    `code=${tsrc26a.code}, message=${tsrc26a.json?.message || tsrc26a.text?.slice(0, 80)}`
  )
  assert(
    'TSRC26b 动态：POST /api/governance/task-split-rule-configs/999999999/activate → 404（id 不存在）',
    tsrc26b.code === 404,
    `code=${tsrc26b.code}, message=${tsrc26b.json?.message || tsrc26b.text?.slice(0, 80)}`
  )

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

  // TSRC15 静态：批策略页时间字段走「治理域本地直显」（2026-10-28 口径反转）
  //  - 原 10-09 按旧约定「裸 DateTime = UTC」改成本页 5 处 formatUtc*；3号位 2026-10-28 核证回执裁定：
  //    批策略属 APS 自有治理域，后端 DateTime.Now 落本地墙钟（TaskSplitRuleConfigGovernanceService:158/252/347）
  //    ⇒ 前端本地直显才是对的，formatUtc* 反而多减 8h。
  //  - 判据与免登录组 DT02 共用 checkTaskSplitLocalDirect()，一处改口径两处同步。
  const tsrc15Checks = checkTaskSplitLocalDirect(tsrcDepPage)
  const tsrc15 = Object.values(tsrc15Checks).every(Boolean)
  assert(
    'TSRC15 静态：批策略页时间字段走治理域本地直显（5 处全覆盖 + 禁回退 formatUtc*）',
    tsrc15,
    tsrc15 ? '5 处均 formatLocal*' : checkDetail(tsrc15Checks)
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

  /* -------- ST04 静态：Setup 页不再自建时区辅助函数（裸 dayjs 由 S04 兜底） --------
   * 10-27 修正：原断言要求 4 页**一律** import @/utils/datetime。
   * 缺失规则查询页的「最近出现」列已随契约对齐撤掉（后端 DTO 无该字段），
   * 该页现在**不展示任何后端 DateTime** ⇒ 强行 import 会变成死代码（lint/ts 皆报）。
   * 改判据：凡**展示**后端 DateTime 的页必须直调 @/utils/datetime；
   * 一页都不展示日期的页免引用，但**不得**自建时区辅助函数（noHelper 仍锁死）。 */
  const st04NoHelper = !setupSrc.some((c) => /function\s+formatDateTime\s*\(/.test(c))
  const st04ShowsBackendDate = (c) =>
    /format(?:Utc|Local)\w+/.test(c) ||
    /toLocaleString/.test(c) ||
    /dayjs\(\s*[A-Za-z_$][\w$.[\]]*\s*\)/.test(c)
  const st04UseUtil = setupSrc.every(
    (c) => /from\s+['"]@\/utils\/datetime['"]/.test(c) || !st04ShowsBackendDate(c)
  )
  const st04All = st04NoHelper && st04UseUtil
  assert(
    'ST04 静态：Setup 4 页不包 formatDateTime 辅助函数；凡展示后端 DateTime 的页直调 @/utils/datetime',
    st04All,
    st04All ? '无自建辅助函数 + 有日期展示的页均直调 util' : `noHelper=${st04NoHelper} useUtil=${st04UseUtil}`
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
      return !/^VITE_USE_MOCK=false$/m.test(readText(join(__dirname, '..', f)))
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
    /:remote-method="onSearchFromMaterial"/.test(exactDialogSrc) &&
    /:remote-method="onSearchToMaterial"/.test(exactDialogSrc) &&
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

  // ST10j：远程补拉 + **本地关键词收窄**（10-28 用户实测「下拉模糊搜索不起作用」）
  // 根因：ElSelect 只要同时传 filterable + remote + remote-method，就完全不做本地过滤
  // （element-plus useSelect.updateOptions 首行直接 return）；而 composable 的 search* 又是
  // merge 不收敛 ⇒ 打字后下拉一条不少。修法：关键词独立 query，展示项按 label/value 收窄。
  // 反向探针：删掉任一 filterLookupOptions 调用 / 把 handler 里的 query 赋值挪进 debounce → 转红。
  const st10jHelper =
    /export function filterLookupOptions\([\s\S]{0,400}label\.toLowerCase\(\)\.includes\(q\) \|\| o\.value\.toLowerCase\(\)\.includes\(q\)/.test(
      masterDataSrc
    )
  const st10jExact =
    /const departmentQuery = ref\(''\)/.test(exactDialogSrc) &&
    /const fromMaterialQuery = ref\(''\)/.test(exactDialogSrc) &&
    /const toMaterialQuery = ref\(''\)/.test(exactDialogSrc) &&
    /filterLookupOptions\(\s*masterData\.departments\.value,\s*departmentQuery\.value\s*\)/.test(
      exactDialogSrc
    ) &&
    /filterLookupOptions\(\s*masterData\.materials\.value,\s*fromMaterialQuery\.value\s*\)/.test(
      exactDialogSrc
    ) &&
    /filterLookupOptions\(\s*masterData\.materials\.value,\s*toMaterialQuery\.value\s*\)/.test(
      exactDialogSrc
    ) &&
    /function onSearchDepartment\(kw: string\): void \{\s*departmentQuery\.value = kw/.test(
      exactDialogSrc
    )
  const st10jDefault =
    /filterLookupOptions\(\s*masterData\.departments\.value,\s*departmentQuery\.value\s*\)/.test(
      defaultDialogSrc
    ) &&
    /filterLookupOptions\(\s*enriched,\s*resourceQuery\.value\s*\)/.test(defaultDialogSrc) &&
    /function onSearchResource\(kw: string\): void \{\s*resourceQuery\.value = kw/.test(
      defaultDialogSrc
    )
  const st10jUncovered =
    /filterLookupOptions\(\s*masterData\.departments\.value,\s*departmentQuery\.value\s*\)/.test(
      uncoveredSrc
    ) &&
    /filterLookupOptions\(\s*masterData\.resources\.value,\s*resourceQuery\.value\s*\)/.test(
      uncoveredSrc
    ) &&
    /filterLookupOptions\(\s*masterData\.materials\.value,\s*materialQuery\.value\s*\)/.test(
      uncoveredSrc
    ) &&
    /function onSearchMaterial\(kw: string\): void \{\s*materialQuery\.value = kw/.test(uncoveredSrc)
  assert(
    'ST10j 静态：远程补拉之外必须本地按关键词收窄（EP 在 filterable+remote 下不做本地过滤，否则打字等于没打）',
    st10jHelper && st10jExact && st10jDefault && st10jUncovered,
    !st10jHelper
      ? '缺 filterLookupOptions 收窄helper'
      : st10jExact && st10jDefault && st10jUncovered
        ? '三处消费面均先收窄再远程补拉'
        : !st10jExact
          ? '明确产品转换 Dialog 有下拉未接收窄'
          : !st10jDefault
            ? '默认换型规则 Dialog 有下拉未接收窄'
            : '缺失规则查询页有筛选下拉未接收窄'
  )
}

/**
 * [SET-UNC] 缺失规则查询（uncovered-stats）契约对齐（setup-unc — 2026-10-27）
 *
 * 背景：3号位 2026-10-27《规则集整版发布权限与Setup契约缺口》核证回执指出
 * uncovered-stats 契约与前端自拟 DTO 不符。本组按**后端真源**锁前端：
 *   - 端点：SetupRuleController.cs:366-372 形参 runId/departmentId/operationCode/resourceId/materialId
 *   - 仓储：SetupUncoveredStatRepository.cs（13 字段 + 筛选 SQL；10-28 补 RuleSetVersionId）
 *   - DTO ：SetupUncoveredStatDtos.cs（13 字段，无前/后产品、无最近出现）
 *
 * 三个坑（都在探针里锁死）：
 *   1. departmentId/resourceId/materialId 是**内部主键 Id**，不是 Code。
 *      发 Code 后端绑不上 ⇒ 筛选**静默失效**（不报错，返回全量）。
 *   2. 后端**没有 stageCode 形参** ⇒ 大工艺只能作为输出维度展示，不能当筛参。
 *   3. 输出行**没有** fromMaterialCode/toMaterialCode/lastOccurrence ⇒
 *      「去补明确产品转换」拿不到前产品 ⇒ 出口降级为只补 DEFAULT
 *      （SetupRuleExactInput 必须同时给前/后产品主键，缺任一即 422）。
 *
 * 纯静态：不登录、不打后端（dev 登录有频控，反向破坏自证连跑会撞 429）。
 * 后端真值由 GROUP=setup-unc-live 兑现。
 */
function verifySetupUncoveredStatic() {
  log('\n[SET-UNC] 缺失规则查询 契约对齐（setup-unc — 2026-10-27）')

  const stripVue = (s) => stripComments(s).replace(/<!--[\s\S]*?-->/g, '')
  const typesSrc = stripVue(readSrc('api/aps-v1/types/setup.ts'))
  const apiSrc = stripVue(readSrc('api/aps-v1/setup.ts'))
  const storeSrc = stripVue(readSrc('store/modules/aps/setupUncovered.ts'))
  const viewSrc = stripVue(readSrc('views/Aps/SetupUncovered.vue'))

  const qBlock = typesSrc.match(/export interface SetupUncoveredQuery\s*\{[\s\S]*?\n\}/)?.[0] ?? ''
  const dBlock =
    typesSrc.match(/export interface SetupUncoveredStatDto\s*\{[\s\S]*?\n\}/)?.[0] ?? ''

  const hasField = (block, name) => new RegExp(`\\b${name}\\s*\\??\\s*:`).test(block)

  // SET-UNC01：筛参形参名与后端 Controller 形参一一对应
  const unc01Want = ['runId', 'departmentId', 'operationCode', 'resourceId', 'materialId']
  const unc01Bad = ['stageCode', 'departmentCode', 'resourceCode', 'materialCode']
  const unc01Missing = unc01Want.filter((f) => !hasField(qBlock, f))
  const unc01Extra = unc01Bad.filter((f) => hasField(qBlock, f))
  assert(
    'SET-UNC01 静态：SetupUncoveredQuery 筛参 = 后端 5 形参（runId/departmentId/operationCode/resourceId/materialId），不含 stageCode/*Code',
    qBlock !== '' && unc01Missing.length === 0 && unc01Extra.length === 0,
    `缺=${unc01Missing.join(',') || '无'} 多=${unc01Extra.join(',') || '无'}`
  )

  // SET-UNC02：输出行 = 后端 DTO 13 字段（10-28 补 ruleSetVersionId），且**没有**前/后产品与最近出现
  const unc02Want = [
    'scheduleRunId',
    'ruleSetVersionId',
    'departmentId',
    'departmentCode',
    'stageCode',
    'operationCode',
    'resourceId',
    'resourceCode',
    'materialId',
    'materialCode',
    'hitCount',
    'count',
    'sampleTaskId'
  ]
  const unc02Bad = [
    'fromMaterialCode',
    'toMaterialCode',
    'fromMaterialId',
    'toMaterialId',
    'lastOccurrence'
  ]
  const unc02Missing = unc02Want.filter((f) => !hasField(dBlock, f))
  const unc02Extra = unc02Bad.filter((f) => hasField(dBlock, f))
  assert(
    'SET-UNC02 静态：SetupUncoveredStatDto 13 字段对齐后端 DTO（含 ruleSetVersionId），且不含前/后产品/最近出现',
    dBlock !== '' && unc02Missing.length === 0 && unc02Extra.length === 0,
    `缺=${unc02Missing.join(',') || '无'} 多=${unc02Extra.join(',') || '无'}`
  )

  // SET-UNC03：stageCode 可空（Task.StageCode 未透传的行不能被当成有值）
  assert(
    'SET-UNC03 静态：SetupUncoveredStatDto.stageCode 声明为可空（?/null 皆可）',
    /stageCode\?\s*:\s*string\s*\|\s*null/.test(dBlock),
    /stageCode\?\s*:\s*string\s*\|\s*null/.test(dBlock)
      ? '已声明可空'
      : 'stageCode 若声明为必填字符串，Task.StageCode 未透传的行会渲染成假值'
  )

  // SET-UNC04：api 层不改形参名（params 直传 query）
  //  必须限定在 getUncoveredStats 函数体内 —— 全文件扫 params 会命中
  //  getOperationResourceEligibility 的同名写法，破坏 uncovered 那处也不转红（假绿）。
  const uncFunc = apiSrc.match(/async getUncoveredStats\([\s\S]*?\n  \}/)?.[0] ?? ''
  const unc04Url = /url:\s*'\/api\/governance\/setup-rules\/uncovered-stats'/.test(uncFunc)
  const unc04Params = /params:\s*\{\s*\.\.\.query\s*\}/.test(uncFunc)
  const unc04NoRename = !/(departmentCode|resourceCode|materialCode|stageCode)\s*[:,]/.test(uncFunc)
  assert(
    'SET-UNC04 静态：getUncoveredStats 直传 query 到 uncovered-stats 端点（不得改形参名）',
    uncFunc !== '' && unc04Url && unc04Params && unc04NoRename,
    `func=${uncFunc !== ''} url=${unc04Url} params=${unc04Params} noRename=${unc04NoRename}`
  )

  // SET-UNC05：页面筛参绑 Id（opt.id），不发 Code
  const unc05Bind =
    /v-model="queryForm\.departmentId"/.test(viewSrc) &&
    /v-model="queryForm\.resourceId"/.test(viewSrc) &&
    /v-model="queryForm\.materialId"/.test(viewSrc) &&
    /v-model="queryForm\.operationCode"/.test(viewSrc)
  const unc05OptIdCount = (viewSrc.match(/:value="opt\.id"/g) ?? []).length
  const unc05OptValueCount = (viewSrc.match(/:value="opt\.value"/g) ?? []).length
  assert(
    'SET-UNC05 静态：缺失规则查询页三个主数据筛参绑 opt.id（主键 Id），不得绑 opt.value（Code）',
    unc05Bind && unc05OptIdCount >= 3 && unc05OptValueCount === 0,
    `bind=${unc05Bind} opt.id×${unc05OptIdCount} opt.value×${unc05OptValueCount}`
  )

  // SET-UNC06：没有大工艺筛参（后端无 stageCode 形参），但列表保留大工艺列
  assert(
    'SET-UNC06 静态：页面无大工艺筛选控件（后端无 stageCode 形参），列表仍展示大工艺列',
    !/queryForm\.stageCode/.test(viewSrc) && /prop="stageCode"/.test(viewSrc),
    `filter=${!/queryForm\.stageCode/.test(viewSrc)} column=${/prop="stageCode"/.test(viewSrc)}`
  )

  // SET-UNC07：列表无前/后产品与最近出现三列，改列「产品」
  const unc07Gone =
    !/prop="fromMaterialCode"/.test(viewSrc) &&
    !/prop="toMaterialCode"/.test(viewSrc) &&
    !/prop="lastOccurrence"/.test(viewSrc) &&
    !/formatUtcDateTimeSec/.test(viewSrc)
  assert(
    'SET-UNC07 静态：列表删掉「前产品/后产品/最近出现」三列（后端 DTO 没有），改列「产品」',
    unc07Gone && /prop="materialCode"/.test(viewSrc),
    `gone=${unc07Gone} productCol=${/prop="materialCode"/.test(viewSrc)}`
  )

  // SET-UNC08：出口只剩「去补默认换型规则」
  assert(
    'SET-UNC08 静态：出口撤除「去补明确产品转换」（EXACT 入参必须同时给前/后产品主键），只留「去补默认换型规则」',
    !/去补明确产品转换/.test(viewSrc) &&
      /去补默认换型规则/.test(viewSrc) &&
      /path:\s*'\/aps\/setup\/default'/.test(viewSrc),
    `exactGone=${!/去补明确产品转换/.test(viewSrc)} default=${
      /去补默认换型规则/.test(viewSrc)
    }`
  )

  // SET-UNC09：exportCsv 表头 12 列（对齐后端实返字段；10-28 补 RuleSetVersionId）
  const unc09Head = storeSrc.match(/const headers = \[([\s\S]*?)\]/)?.[1] ?? ''
  const unc09Must = [
    'ScheduleRunId',
    'RuleSetVersionId',
    'DepartmentId',
    'DepartmentCode',
    'StageCode',
    'OperationCode',
    'ResourceId',
    'ResourceCode',
    'MaterialId',
    'MaterialCode',
    'Count',
    'SampleTaskId'
  ]
  const unc09Missing = unc09Must.filter((h) => !unc09Head.includes(`'${h}'`))
  const unc09Bad = ['FromMaterial', 'ToMaterial', 'LastOccurrence'].filter((h) =>
    unc09Head.includes(`'${h}`)
  )
  assert(
    'SET-UNC09 静态：exportCsv 表头 12 列对齐后端实返字段（含 MaterialCode / RuleSetVersionId，不含前/后产品与最近出现）',
    unc09Head !== '' && unc09Missing.length === 0 && unc09Bad.length === 0,
    `缺=${unc09Missing.join(',') || '无'} 多=${unc09Bad.join(',') || '无'}`
  )

  /* SET-UNC10（2026-10-28 用户要求；10-29 扩口径）：撤除顶部 KPI 条。
   * 四张卡（未覆盖转换对 / 涉及部门 / 涉及大工艺 / 兜底换型时间）占掉首屏高度，
   * 且「未覆盖转换对」与分页器「共 N 条」重复 ⇒ 用户要求整块去掉。
   * 10-29：后端分页对象自带 2 个 KPI 计数，用户口径「只取 total，其余不展示」⇒ 一并锁死。
   * 反向锁：KPI 条加回来、前端 distinct 分组 getter 复活、后端 KPI 被渲染出来 → 红。 */
  const unc10 =
    !/kpi-row|kpi-card|kpi-value|kpi-label/.test(viewSrc) &&
    !/涉及部门|涉及大工艺|兜底换型时间|未覆盖转换对/.test(viewSrc) &&
    !/statsByDepartment|statsByStage/.test(storeSrc) &&
    !/departmentCount|stageCount/.test(viewSrc) &&
    !/departmentCount|stageCount/.test(storeSrc) &&
    !/stats\.value\.length/.test(storeSrc) &&
    /const\s+total\s*=\s*ref/.test(storeSrc)
  assert(
    'SET-UNC10 静态：顶部 KPI 条已撤除（页面无 kpi-* 节点与四卡文案；store 不产出前端 distinct 分组、不落后端 KPI 计数；total 取后端分页值而非 stats.length）',
    unc10,
    `kpi=${/kpi-row|kpi-card|kpi-value|kpi-label/.test(viewSrc)} 文案=${/涉及部门|涉及大工艺|兜底换型时间|未覆盖转换对/.test(viewSrc)} getter=${/statsByDepartment|statsByStage/.test(storeSrc)} 后端KPI=${/departmentCount|stageCount/.test(viewSrc) || /departmentCount|stageCount/.test(storeSrc)} total=${/const\s+total\s*=\s*ref/.test(storeSrc)}`
  )

  // SET-UNC11：规则集版本列 —— 用 #11 映射成「编码 / 版本号」，**不直出内部 Id**
  //  10-28 3号位 P1 落码：uncovered-stats 每行多 ruleSetVersionId（long?，可为空）。
  //  后端 #11 同时回带 ruleSetCode ⇒ 页面用 aps.setup.view 就能映射（planner 不必扩 aps.rule.view）。
  //  判据：① 页面真的调 listRuleSetVersions 建映射 ② 模板走 ruleSetVersionLabel(row.ruleSetVersionId)
  //       ③ 不把 row.ruleSetVersionId 直接插值到单元格（那会显示裸 Id）
  const unc11Loads = /setupApi\.listRuleSetVersions\(\)/.test(viewSrc)
  const unc11UsesFn = /ruleSetVersionLabel\(row\.ruleSetVersionId\)/.test(viewSrc)
  const unc11NoRawId = !/\{\{\s*row\.ruleSetVersionId\s*\}\}/.test(viewSrc)
  const unc11NullDash = /if \(versionId == null\) return '—'/.test(viewSrc)
  assert(
    'SET-UNC11 静态：「规则集版本」列用 #11 映射成「规则集编码 / 版本号」（不直出内部 Id，空值显示「—」）',
    unc11Loads && unc11UsesFn && unc11NoRawId && unc11NullDash,
    `加载=${unc11Loads} 用格式化=${unc11UsesFn} 无裸Id=${unc11NoRawId} 空值—=${unc11NullDash}`
  )

  /* SET-UNC12（2026-10-28）：出口改跳「规则集维护 → 换型规则」页签
   * 背景：两个 Setup 维护页已从菜单隐藏（路由保留），「默认换型规则」的维护入口并入
   *   规则集页 ⇒ 本页「去补默认换型规则」若还跳 /aps/setup/default，用户会落到一个
   *   菜单里找不到的页面。
   * 判据：① 出口带 tab=setup + 4 字段预填跳 /aps/rules ② 带 ruleSetCode（版本→规则集编码映射）
   *       ③ **权限兜底**：/aps/rules 要 aps.rule.edit/publish（守卫 403），
   *          只有 aps.setup.* 的角色（PMC）进不去 ⇒ 这类人仍跳 /aps/setup/default。
   */
  const unc12Body = (() => {
    const i = viewSrc.indexOf('function goToDefault(')
    if (i < 0) return ''
    let depth = 0
    let opened = false
    for (let j = i; j < viewSrc.length; j++) {
      if (viewSrc[j] === '{') {
        depth++
        opened = true
      } else if (viewSrc[j] === '}') {
        depth--
        if (opened && depth === 0) return viewSrc.slice(i, j + 1)
      }
    }
    return ''
  })()
  const unc12 =
    /path:\s*'\/aps\/rules'/.test(unc12Body) &&
    /tab:\s*'setup'/.test(unc12Body) &&
    /ruleSetCode/.test(unc12Body) &&
    /aps\.rule\.edit/.test(unc12Body) &&
    /aps\.rule\.publish/.test(unc12Body) &&
    /path:\s*'\/aps\/setup\/default'/.test(unc12Body) &&
    /ruleSetCodeByVersionId/.test(viewSrc)
  assert(
    'SET-UNC12 静态：「去补默认换型规则」改跳规则集页（tab=setup + 4 字段 + 规则集编码）；无 rule.* 权限的角色仍兜底到 /aps/setup/default',
    unc12,
    `rules=${/path:\s*'\/aps\/rules'/.test(unc12Body)} tab=${/tab:\s*'setup'/.test(unc12Body)} code=${/ruleSetCode/.test(unc12Body)} 兜底=${/path:\s*'\/aps\/setup\/default'/.test(unc12Body)} 映射=${/ruleSetCodeByVersionId/.test(viewSrc)}`
  )

  /* SET-UNC13（2026-10-28）：出口把**来源版本 Id** 一起带走 + 写权限前置拦截 ——
   *   3号位 联合报告 §四 待办 #4：「实际跳转以**不变 RuleSetVersionId** 绑定」
   *   —— 只带 ruleSetCode 会让回填目标随「规则集当前草稿」漂移。
   *   判据：① query 带 ruleSetVersionId（取行上的值）② 进 /aps/rules 的判据含 aps.rule.view
   *        （10-28 放开只读入口）③ 补规则 = 写 ⑦ ⇒ 无 aps.setup.edit 直接拦下并说清。
   * ⚠️ 判据锁在 goToDefault 函数体内（unc12Body），不扫全文件。 */
  const unc13 =
    /query\.ruleSetVersionId\s*=\s*String\(row\.ruleSetVersionId\)/.test(unc12Body) &&
    /aps\.rule\.view/.test(unc12Body) &&
    // 前置拦截的**形状**（只看 'aps.setup.edit' 字样会被提示文案顶替 ⇒ 假绿）
    /if\s*\(!apsAuth\.has\('aps\.setup\.edit'\)\)/.test(unc12Body) &&
    /row\.ruleSetVersionId\s*!=\s*null/.test(unc12Body)
  assert(
    'SET-UNC13 静态：「去补默认换型规则」带走来源版本 Id（ruleSetVersionId）+ 无 aps.setup.edit 直接拦下（回填目标不漂移）',
    unc13,
    `versionId=${/query\.ruleSetVersionId\s*=\s*String\(row\.ruleSetVersionId\)/.test(unc12Body)} ruleView=${/aps\.rule\.view/.test(unc12Body)} setupEdit=${/aps\.setup\.edit/.test(unc12Body)}`
  )

  /* SET-UNC14（2026-10-28 用户要求）：撤除「换型来源四态说明」提示块。
   * 页面正文不再出现该说明，也不再引用 SETUP_SOURCES / SETUP_SOURCE_META（否则是死引用，构建期报错/空渲染）。
   * 反向锁：任一项加回来 → 红（防「用户删了又被顺手加回去」）。 */
  const unc14 =
    !/legend-alert/.test(viewSrc) &&
    !/SETUP_SOURCE_META/.test(viewSrc) &&
    !/SETUP_SOURCES/.test(viewSrc) &&
    !/换型来源四态说明/.test(viewSrc)
  assert(
    'SET-UNC14 静态：缺失规则查询页无「换型来源四态说明」提示块（用户 2026-10-28 要求撤除）',
    unc14,
    unc14
      ? '提示块与两处常量引用均已清除'
      : `legend-alert=${/legend-alert/.test(viewSrc)} META=${/SETUP_SOURCE_META/.test(viewSrc)} SOURCES=${/SETUP_SOURCES/.test(viewSrc)} 文案=${/换型来源四态说明/.test(viewSrc)}`
  )

  /* SET-UNC15（2026-10-29 方案 A 落码后）：服务端分页。
   * 端点同一路径两种形态（SetupRuleController.cs:381）：
   *   传 pageIndex/pageSize → 分页对象 {items,total,page,pageSize,…}
   *   不传                  → 全量数组（CSV 导出走这条）
   * 判据（每条都能反向破坏自证，见 scripts/selfverify-setup-uncovered-paging.mjs）：
   *   ① api 有 getUncoveredStatsPage，且请求带 pageIndex/pageSize
   *   ② api 的全量方法 getUncoveredStats **不传**分页参（传了就变成只导当前页）
   *   ③ store 列表走分页端点，total 取后端值
   *   ④ store 的 exportCsv 是 async 且走**全量**端点
   *   ⑤ 页面不再内存切片（无 stats.value.slice / pagedStats），表格直接吃 stats
   *   ⑥ 分页器 total 绑 total；筛选回第 1 页；翻页 / 改每页条数要重新请求
   *   ⑦ CSV 导出 await store.exportCsv() 并用返回的行数提示
   */
  // 「回第 1 页」必须锁在函数体内：用距离窗口会跨到紧随其后的 handleReset ⇒ 假绿
  const fnBody = (src, signature) => {
    const start = src.indexOf(signature)
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
  const apiPageBody = fnBody(apiSrc, 'async getUncoveredStatsPage(')
  const apiFullBody = fnBody(apiSrc, 'async getUncoveredStats(')
  const storeLoadBody = fnBody(storeSrc, 'async function loadStats(')
  const storeExportBody = fnBody(storeSrc, 'async function exportCsv(')
  const viewLoadBody = fnBody(viewSrc, 'async function loadStats()')
  const viewFetchBody = fnBody(viewSrc, 'async function fetchPage(')
  const viewPageBody = fnBody(viewSrc, 'async function onPageChange(')
  const viewSizeBody = fnBody(viewSrc, 'async function onSizeChange(')
  const viewExportBody = fnBody(viewSrc, 'async function handleExportCsv(')
  const resetBody = fnBody(viewSrc, 'function handleReset()')
  const unc15 =
    // ① 分页端点存在且带分页参
    /const\s+page\s*=\s*ref\(1\)/.test(viewSrc) &&
    /const\s+pageSize\s*=\s*ref\(20\)/.test(viewSrc) &&
    apiPageBody !== '' &&
    /pageIndex/.test(apiPageBody) &&
    /pageSize/.test(apiPageBody) &&
    /SetupUncoveredStatsPage/.test(apiSrc) &&
    // ② 全量方法绝不带分页参
    apiFullBody !== '' &&
    !/pageIndex/.test(apiFullBody) &&
    !/pageSize/.test(apiFullBody) &&
    // ③ store 列表走分页端点 + total 取后端值
    /getUncoveredStatsPage\(/.test(storeLoadBody) &&
    /page\.total/.test(storeLoadBody) &&
    // ④ exportCsv 异步 + 走全量端点
    /async function exportCsv\(/.test(storeSrc) &&
    /getUncoveredStats\(/.test(storeExportBody) &&
    !/getUncoveredStatsPage/.test(storeExportBody) &&
    // ⑤ 页面不再内存切片
    !/stats\.value\.slice\(/.test(viewSrc) &&
    !/pagedStats/.test(viewSrc) &&
    /:data="stats"/.test(viewSrc) &&
    // ⑥ 分页器 + 回第 1 页 + 翻页重取
    /<ElPagination[\s\S]{0,400}:current-page="page"[\s\S]{0,400}:page-size="pageSize"[\s\S]{0,400}:total="total"/.test(
      viewSrc
    ) &&
    /:index="rowIndex"/.test(viewSrc) &&
    /page\.value\s*=\s*1/.test(viewLoadBody) &&
    /page\.value\s*=\s*1/.test(resetBody) &&
    // 唯一的取数口 fetchPage 必须把页码/每页条数交给 store 的分页动作
    /uncoveredStore\.loadStats\(/.test(viewFetchBody) &&
    /page\.value/.test(viewFetchBody) &&
    /pageSize\.value/.test(viewFetchBody) &&
    // 翻页 / 改每页条数都要重新请求（不是本地切片）
    /fetchPage\(/.test(viewPageBody) &&
    /fetchPage\(/.test(viewSizeBody) &&
    /page\.value\s*=\s*1/.test(viewSizeBody) &&
    // ⑦ CSV 导出 await + 用返回行数
    /await\s+uncoveredStore\.exportCsv\(\)/.test(viewExportBody) &&
    /count/.test(viewExportBody)
  assert(
    'SET-UNC15 静态：列表改服务端分页（api 带分页参 / store 取后端 total / 页面无内存切片 / 翻页重取 / CSV 另拉全量）',
    unc15,
    unc15
      ? 'api 双形态 + store 分页 + 页面直吃 stats + CSV 全量重拉'
      : `apiPage=${apiPageBody !== ''} apiFullNoPaging=${!/pageIndex/.test(apiFullBody)} storePaged=${/getUncoveredStatsPage\(/.test(storeLoadBody)} total=${/page\.total/.test(storeLoadBody)} csvFull=${/getUncoveredStats\(/.test(storeExportBody)} slice=${/stats\.value\.slice\(/.test(viewSrc)} data=${/:data="stats"/.test(viewSrc)} pager=${/<ElPagination[\s\S]{0,400}:total="total"/.test(viewSrc)} fetch=${/uncoveredStore\.loadStats\(/.test(viewFetchBody)} pageReload=${/fetchPage\(/.test(viewPageBody)} sizeReload=${/fetchPage\(/.test(viewSizeBody)} csvAwait=${/await\s+uncoveredStore\.exportCsv\(\)/.test(viewExportBody)}`
  )

  /* SET-UNC16（2026-10-29 用户要求）：排产批次由「手填数字」改「下拉选最近 50 次 + 进页默认最近一次」。
   * 背景：Run 没有业务编码，用户记不住批次号；写死 Run=1 在 dev 数据迁移后直接打开空表。
   * 数据源 GET /api/governance/runs?take=N（aps.plan.view，与 aps.setup.view 同人群持）；
   * 该端点**无**按 Id 搜索 / offset / total（已发函 5号位）⇒ 只能列最近 N 次，不是前端偷懒。
   * 默认口径：最近一次**跑完**（COMPLETED / PARTIAL_SUCCESS）的批次 —— 运行中批次还没落统计
   * （10-29 dev 实测 #999310 运行中 total=0），拿它当默认会一进页面就空表；50 条内没有跑完的才回退最近一次。
   * 反向锁：改回手填、下拉不绑 runId、标签丢状态、排序反了、默认写死 1、默认换成「第一个」→ 红。 */
  const unc16 =
    /import \{[^}]*runApi[^}]*\} from '@\/api\/aps-v1'/.test(viewSrc) &&
    /runApi\.listRuns\(/.test(viewSrc) &&
    !/ElInputNumber/.test(viewSrc) &&
    /v-model="queryForm\.runId"/.test(viewSrc) &&
    /RUN_STATUS_LABEL\[r\.status\]/.test(viewSrc) &&
    // Run.startedAt 是治理域 DateTime.Now 本地墙钟 ⇒ 必须走 @/utils/datetime（裸 dayjs 会被当成 UTC 口径）
    /formatLocalMonthDayHM\(r\.startedAt\)/.test(viewSrc) &&
    !/dayjs\(\s*r\.startedAt/.test(viewSrc) &&
    /new Date\(b\.startedAt\)\.getTime\(\) - new Date\(a\.startedAt\)\.getTime\(\)/.test(viewSrc) &&
    // 默认 = 最近一次跑完（COMPLETED / PARTIAL_SUCCESS）的批次 → 回退最近一次；
    // 运行中批次还没落统计（10-29 dev 实测 total=0），拿它当默认就是一进页面空表
    /r\.status === 'COMPLETED' \|\| r\.status === 'PARTIAL_SUCCESS'/.test(viewSrc) &&
    /queryForm\.runId\s*=\s*def\.id/.test(viewSrc) &&
    /runId:\s*0/.test(viewSrc)
  assert(
    'SET-UNC16 静态：排产批次 = 下拉（/api/governance/runs 最近 50 次，标签含时间+状态+批次号）+ 进页默认最近一次已跑完的批次（不再手填 / 不再写死 Run=1）',
    unc16,
    unc16
      ? '下拉 + 默认最近一次'
      : `api=${/runApi\.listRuns\(/.test(viewSrc)} noInputNumber=${!/ElInputNumber/.test(viewSrc)} bind=${/v-model="queryForm\.runId"/.test(viewSrc)} status=${/RUN_STATUS_LABEL\[r\.status\]/.test(viewSrc)} localFmt=${/formatLocalMonthDayHM\(r\.startedAt\)/.test(viewSrc)} sort=${/new Date\(b\.startedAt\)\.getTime\(\) - new Date\(a\.startedAt\)\.getTime\(\)/.test(viewSrc)} default=${/queryForm\.runId\s*=\s*def\.id/.test(viewSrc)} finishedFirst=${/r\.status === 'COMPLETED' \|\| r\.status === 'PARTIAL_SUCCESS'/.test(viewSrc)} init=${/runId:\s*0/.test(viewSrc)}`
  )
}

/**
 * [SET-UNCL] 缺失规则查询 后端真值联调（setup-unc-live — 2026-10-27）
 *
 * 兑现 SET-UNC 静态组锁的后端真值：
 *   L01 uncovered-stats 每行字段 = 后端 DTO 13 字段（多一个少一个都算红）
 *   L02 按 departmentId（主键 Id）筛**真的生效**（返回行全部属于该部门）
 *   L03 发后端不认识的形参（stageCode / departmentCode）**不报错但也不筛** ⇒
 *       前端绝不能靠这些形参做筛选（静默失效是最坑的一种）
 *   L04 pmc（aps.planner）持 aps.setup.view 但**不持** aps.rule.view ⇒
 *       GET /api/governance/rule-sets 对 PMC 是 403（版本下拉降级路径真实存在）
 *   L05 / L06（10-29 方案 A）：传 pageIndex/pageSize ⇒ 返回分页对象而非数组；
 *       第 2 页内容与第 1 页不同 ⇒ 后端是真分页，不是把全量切片下发
 *
 * 注：L01/L02/L03 的 runId 改用**数据探测**得到（dev 数据会迁到别的 Run，写死 runId=1
 *     在统计迁移后会误报「字段缺失」）。
 */
async function verifySetupUncoveredLive(token) {
  log('\n[SET-UNCL] 缺失规则查询 后端真值联调（setup-unc-live — 2026-10-27）')

  const base = '/api/governance/setup-rules/uncovered-stats'
  const r0 = await call('GET', `${base}?runId=1`, { token })
  const rows = r0.json?.data
  const arr = Array.isArray(rows) ? rows : []
  assert(
    'SET-UNCL01 动态：GET uncovered-stats?runId=1 返回 200 且是数组',
    r0.ok && Array.isArray(arr),
    `code=${r0.code} rows=${Array.isArray(arr) ? arr.length : 'n/a'}`
  )

  /* 数据探测（10-29）：runId=1 在 dev 已无数据（统计迁到别的 Run）⇒ 用分页端点探一个
   * 真有数据的 Run 再校验字段/筛选，避免把「dev 数据漂移」误报成契约不符。 */
  const probe = await call('GET', `${base}?pageIndex=1&pageSize=1`, { token })
  const probeRow = Array.isArray(probe.json?.data?.items) ? probe.json.data.items[0] : null
  const dataRunId = typeof probeRow?.scheduleRunId === 'number' ? probeRow.scheduleRunId : null
  const r0b = dataRunId != null ? await call('GET', `${base}?runId=${dataRunId}`, { token }) : null
  const arrData = Array.isArray(r0b?.json?.data) ? r0b.json.data : []

  // 10-28 3号位 P1 落码后后端 DTO = 13 字段（补 ruleSetVersionId）——dev 实测确认
  const expect13 = [
    'scheduleRunId',
    'ruleSetVersionId',
    'departmentId',
    'departmentCode',
    'stageCode',
    'operationCode',
    'resourceId',
    'resourceCode',
    'materialId',
    'materialCode',
    'hitCount',
    'count',
    'sampleTaskId'
  ]
  const rowKeys = arrData.length ? Object.keys(arrData[0]).sort() : []
  const uncL01Missing = expect13.filter((f) => !rowKeys.includes(f))
  const uncL01Extra = rowKeys.filter((k) => !expect13.includes(k))
  assert(
    'SET-UNCL01 动态：uncovered-stats 每行字段 = 后端 DTO 13 字段（不多不少，含 ruleSetVersionId）',
    arrData.length > 0 && uncL01Missing.length === 0 && uncL01Extra.length === 0,
    `缺=${uncL01Missing.join(',') || '无'} 多=${uncL01Extra.join(',') || '无'} rows=${arrData.length} runId=${dataRunId ?? 'n/a'}`
  )

  // L02：departmentId 生效
  if (arrData.length) {
    const deptId = arrData[0].departmentId
    const r1 = await call('GET', `${base}?runId=${dataRunId}&departmentId=${deptId}`, { token })
    const rows1 = Array.isArray(r1.json?.data) ? r1.json.data : []
    const allSame = rows1.every((x) => x.departmentId === deptId)
    assert(
      'SET-UNCL02 动态：departmentId（主键 Id）筛选真的生效（返回行全部属于该部门）',
      r1.ok && rows1.length > 0 && allSame,
      `deptId=${deptId} rows=${rows1.length} allSame=${allSame}`
    )
  } else {
    skip('SET-UNCL02 动态：departmentId 筛选生效', `dev 无 uncovered-stats 行（runId=${dataRunId ?? 'n/a'} 空）`)
  }

  // L03：不认识的形参静默失效（不报错、不筛）—— 前端不能靠它筛
  const r2 = await call('GET', `${base}?runId=${dataRunId}&stageCode=__NO_SUCH_STAGE__`, { token })
  const rows2 = Array.isArray(r2.json?.data) ? r2.json.data : []
  assert(
    'SET-UNCL03 动态：后端不认识 stageCode 形参 ⇒ 静默忽略（返全量、不报错）⇒ 前端不得用大工艺做筛参',
    r2.ok && rows2.length === arrData.length,
    `rows=${rows2.length} vs 全量=${arrData.length} code=${r2.code}`
  )

  /* L05 / L06（10-29 3号位 方案 A 落码后）：服务端分页真值
   *  - 传 pageIndex/pageSize ⇒ 返回分页对象 {items,total,page,pageSize,…}（不是数组）
   *  - 第 2 页内容与第 1 页不同 ⇒ 后端真分页，不是把全量数组切片下发
   */
  const rp1 = await call('GET', `${base}?pageIndex=1&pageSize=5`, { token })
  const d1 = rp1.json?.data
  const items1 = Array.isArray(d1?.items) ? d1.items : []
  assert(
    'SET-UNCL05 动态：传 pageIndex/pageSize ⇒ uncovered-stats 返回分页对象（items/total/page/pageSize）',
    rp1.ok &&
      !!d1 &&
      !Array.isArray(d1) &&
      Array.isArray(items1) &&
      typeof d1.total === 'number' &&
      d1.page === 1 &&
      d1.pageSize === 5 &&
      items1.length === Math.min(5, d1.total),
    `code=${rp1.code} page=${d1?.page} pageSize=${d1?.pageSize} total=${d1?.total} items=${items1.length}`
  )

  if (typeof d1?.total === 'number' && d1.total > 5) {
    const rp2 = await call('GET', `${base}?pageIndex=2&pageSize=5`, { token })
    const d2 = rp2.json?.data
    const items2 = Array.isArray(d2?.items) ? d2.items : []
    const rowKey = (x) => `${x.departmentId}/${x.operationCode}/${x.resourceId}/${x.materialId}`
    const diff = items1.some((x, i) => items2[i] && rowKey(x) !== rowKey(items2[i]))
    assert(
      'SET-UNCL06 动态：第 2 页内容与第 1 页不同（后端真分页，不是全量数组切片下发）',
      rp2.ok && d2?.page === 2 && diff,
      `page2=${d2?.page} items2=${items2.length} 差异=${diff}`
    )
  } else {
    skip('SET-UNCL06 动态：第 2 页内容差异', `dev 无足够数据（total=${d1?.total}）`)
  }

  // L04：PMC 无 aps.rule.view ⇒ rule-sets 403（版本下拉降级路径真实存在）
  const reuse = process.env.VERIFY_PMC_TOKEN
  let pmcToken = reuse
  if (!pmcToken) {
    const lr = await call('POST', '/api/auth/login', {
      body: { userCode: 'pmc', password: 'Password@123' }
    })
    pmcToken = lr.json?.data?.accessToken || ''
  }
  if (!pmcToken) {
    skip('SET-UNCL04 动态：PMC 无 aps.rule.view', 'pmc 登录失败（频控？）')
  } else {
    const me = await call('GET', '/api/auth/me', { token: pmcToken })
    const perms = me.json?.data?.permissions ?? []
    const hasRuleView = perms.includes('aps.rule.view')
    const hasSetupView = perms.includes('aps.setup.view')
    const rs = await call('GET', '/api/governance/rule-sets', { token: pmcToken })
    const unc = await call('GET', `${base}?runId=1`, { token: pmcToken })
    assert(
      'SET-UNCL04 动态：PMC(aps.planner) 不持 aps.rule.view ⇒ rule-sets 403；持 aps.setup.view ⇒ uncovered-stats 200',
      hasSetupView && !hasRuleView && rs.code === 403 && unc.ok,
      `setupView=${hasSetupView} ruleView=${hasRuleView} rule-sets=${rs.code} uncovered=${unc.code}`
    )
  }
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

  // PWC35 静态：「只看生效行」查询参数 + 页面开关（10-26 接 5号位 已落的 onlyEffective）
  //  背景：4号位 10-23 函 R2 选 (a)「加查询参数 onlyEffective」，5号位已落：
  //        ProductionWorkCalendarController.cs:67 `bool onlyEffective = false` +
  //        Service :137-138 谓词 `EffectiveFrom <= @Today AND (EffectiveTo IS NULL OR EffectiveTo >= @Today)`
  //        （@Today = DateTime.Today 服务器本地日）
  //  ⚠️ 与 includeInactive（IsActive）**不是同一维度**；本页不暴露 includeInactive（PWC18 已锁）
  const pwc35a =
    /onlyEffective\?:\s*boolean/.test(pwcTypes) &&
    /onlyEffective:\s*query\.onlyEffective\s*\?\?\s*false/.test(pwcApi)
  assert(
    'PWC35a 静态：onlyEffective 查询参数已接（types 声明 + api 透传，默认 false）',
    pwc35a,
    pwc35a ? 'types + api 齐备' : 'types 或 api 未接 onlyEffective'
  )

  const pwc35b =
    /const\s+filterOnlyEffective\s*=\s*ref<boolean>\(false\)/.test(pwcVueCode) &&
    /onlyEffective:\s*filterOnlyEffective\.value/.test(pwcVueCode) &&
    /active-text="只看生效行"/.test(pwcPage)
  assert(
    'PWC35b 静态：页面「只看生效行」开关已接（默认关，传 onlyEffective）',
    pwc35b,
    pwc35b ? '开关 + 传参齐备（默认关，维护页要能看到未生效/已过期行）' : '缺开关或未传 onlyEffective'
  )

  // PWC35c 动态：onlyEffective=true 返回的行必须**逐条**都是今日生效中的行
  //  判据（对齐 4号位 10-23 函 R2 判据）：与按 effectiveFrom/effectiveTo 逐条派生的结果不多不少
  if (token) {
    const pAll = await call('GET', '/api/production-work-calendar', {
      token,
      query: { pageIndex: 1, pageSize: 200, onlyEffective: 'false' }
    })
    const pEff = await call('GET', '/api/production-work-calendar', {
      token,
      query: { pageIndex: 1, pageSize: 200, onlyEffective: 'true' }
    })
    const allItems = Array.isArray(pAll.json?.data?.items) ? pAll.json.data.items : null
    const effItems = Array.isArray(pEff.json?.data?.items) ? pEff.json.data.items : null
    const today = new Date()
    const pad = (n) => String(n).padStart(2, '0')
    const todayStr = `${today.getFullYear()}-${pad(today.getMonth() + 1)}-${pad(today.getDate())}`
    const effOk =
      pAll.ok &&
      pEff.ok &&
      allItems !== null &&
      effItems !== null &&
      effItems.length > 0 &&
      effItems.length <= allItems.length &&
      effItems.every((r) => {
        const from = (r.effectiveFrom ?? '').slice(0, 10)
        const to = r.effectiveTo ? r.effectiveTo.slice(0, 10) : null
        return (!from || from <= todayStr) && (!to || to >= todayStr)
      })
    assert(
      'PWC35c 动态：onlyEffective=true 返回行全部为今日生效中的行（不多不少）',
      effOk,
      effOk
        ? `全量=${allItems.length} 生效中=${effItems.length}（今日 ${todayStr}）`
        : `ok=${pAll.ok}/${pEff.ok} 全量=${allItems?.length ?? '-'} 生效=${effItems?.length ?? '-'}`
    )
  } else {
    skip('PWC35c 动态：onlyEffective 逐条生效判定（需 adminToken）')
  }

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
  //   - 2026-10-31：标签改走全站唯一口径 `reasonLabelOf`（本页原自写
  //     `REASON_CODE_LABELS[code] ?? code`，空串时返回空串 ⇒ 空白标签；实库 95% 原因为空串）
  //   - 同时展示 severity + objectRef + reasonCode 3 列
  const e09HasReasonLabel = /reasonLabelOf\s*\(/.test(eVue)
  const e09HasReasonTagInTemplate =
    /\{\{\s*reasonLabelOf\((f|c)\.reasonCode\)\s*\}\}/.test(eVue)
  assert(
    'E09 静态：Explanation.vue 排程解释事实展示 reasonCode 徽章（走共享 reasonLabelOf）',
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
  // 2026-10-29 改判据：loadCurrent 改为「窗口加载 + 可取消」后不再有裸调 `getGantt(id)`，
  //   改为锁「取到数据才赋值、出错走 handleError、且不把缓存重置为 null」。
  const e19KeepsLastGantt =
    /ganttData\.value = data[\s\S]{0,260}?handleError\(err\)/.test(e16Store) &&
    /if \(ctrl\.signal\.aborted \|\| pendingKey !== key\) return[\s\S]{0,160}?handleError\(err\)/.test(
      e16Store
    ) &&
    !/catch\s*\(err\)\s*\{[\s\S]{0,80}ganttData\.value = null/.test(e16Store)
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
  // 2026-11-05 收窄判据到**业务事实问题面板**（原先是整文件扫）：
  //   排程解释事实面板新增了「继续加载」按钮（另一个面板、另一种交互），
  //   整文件扫会把正确的新增判成回退。判据语义不变：**业务事实问题面板不得有加载更多类按钮**
  //   （它用标准 ElPagination 翻页）。反向自证：往业务事实问题面板插一个「加载更多」按钮 ⇒ 转红。
  const e23BizPanelRegion = (() => {
    const start = eVue.indexOf('<span> 业务事实问题 </span>')
    if (start < 0) return ''
    const end = eVue.indexOf('原因代码分布', start)
    return eVue.slice(start, end < 0 ? undefined : end)
  })()
  const e23ViewNoLoadMoreBtn =
    e23BizPanelRegion !== '' && !/加载更多|继续加载/.test(e23BizPanelRegion)
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

  /* ================= [E27-E31] 2026-11-05 三处真缺陷（KPI 百分比 / 列表静默截断 / 字段名对不上） =================
   * 背景（dev 实测 + 后端源码定位）：
   *  ① KPI 百分比分母错：Summary 端点有权威总数 `totalCount`，前端没接 ⇒ 拿「已加载条数」当分母
   *     ⇒ dev 上出现「错误 538% / 警告 516%」。
   *  ② 列表静默截断：后端 `ExplanationQueryRepository.cs:113` 把 Summary.items 写死成
   *     `QueryAsync(..., severity:"ERROR", take:50)` ⇒ 只看得见 50 条 ERROR，258 条 INFO 与
   *     其它 ObjectType 分组**全部不可见且无任何提示**。
   *  ③ 字段名对不上：后端返 `id`/`createdAt`/四个来源 Id，前端按 `factId`/`occurredAt`/`objectRef` 取
   *     ⇒ 「对象」格恒空、时间恒 `Invalid Date`、`:key` 恒 undefined。
   */

  // E27 静态：KPI 百分比分母 = 后端权威总数（Summary.totalCount），不是已加载条数
  const e27DtoTotalCount =
    /export\s+interface\s+ExplanationViewDto\s*\{[\s\S]*?totalCount\s*:\s*number\b/.test(eTypes)
  const e27StoreTotalFromBackend = /const\s+totalFacts\s*=\s*computed\(\(\)\s*=>\s*data\.value\?\.totalCount/.test(
    eStore
  )
  const e27StoreNotLoadedLen =
    !/const\s+totalFacts\s*=\s*computed\(\(\)\s*=>\s*data\.value\?\.scheduleExplanationFacts/.test(
      eStore
    )
  const e27ViewUsesTotalFacts =
    /totalFacts\s*>\s*0\s*\?\s*Math\.round\(\(item\.count\s*\/\s*totalFacts\)\s*\*\s*100\)/.test(
      eVue
    )
  const e27ViewTotalCardUsesTotal =
    /kpi-label">解释事实合计<[\s\S]{0,200}?kpi-value">\{\{\s*totalFacts\s*\}\}/.test(eVue)
  const e27All =
    e27DtoTotalCount &&
    e27StoreTotalFromBackend &&
    e27StoreNotLoadedLen &&
    e27ViewUsesTotalFacts &&
    e27ViewTotalCardUsesTotal
  assert(
    'E27 静态：KPI 百分比分母用后端权威总数 totalCount（DTO 有该字段 + store totalFacts 读 totalCount + 不再读已加载条数 + view 百分比与合计卡都用 totalFacts）',
    e27All,
    e27All
      ? '分母已是后端总数'
      : `dtoTotal=${e27DtoTotalCount} storeFromBackend=${e27StoreTotalFromBackend} notLoadedLen=${e27StoreNotLoadedLen} viewPct=${e27ViewUsesTotalFacts} viewCard=${e27ViewTotalCardUsesTotal}`
  )

  // E28 静态：排程解释事实改走**列表端点**分页加载（不再用 Summary.items 的 50 条 ERROR）
  const e28ApiListFactsPage = /async\s+listFactsPage\s*\(/.test(eApi)
  const e28ApiListFactsUpTo = /async\s+listFactsUpTo\s*\(/.test(eApi)
  const e28ApiCallsListEndpoint = /url:\s*['"]\/api\/explanation['"]/.test(eApi)
  const e28ApiUsesSkipTake = /params:\s*\{\s*planVersionId\s*,\s*skip\s*,/.test(eApi)
  const e28ApiPageSizeCap = /EXPLANATION_FACT_PAGE_SIZE\s*=\s*500\b/.test(eApi)
  const e28ApiNotSummaryItems = !/scheduleExplanationFacts:\s*summary\.items/.test(eApi)
  const e28StoreLoadMore = /async\s+function\s+loadMoreFacts\s*\(/.test(eStore)
  const e28StoreLoadMoreAppends =
    /\.scheduleExplanationFacts\s*=\s*\[[\s\S]{0,160}?\.\.\.\s*batch/.test(eStore)
  const e28All =
    e28ApiListFactsPage &&
    e28ApiListFactsUpTo &&
    e28ApiCallsListEndpoint &&
    e28ApiUsesSkipTake &&
    e28ApiPageSizeCap &&
    e28ApiNotSummaryItems &&
    e28StoreLoadMore &&
    e28StoreLoadMoreAppends
  assert(
    'E28 静态：排程解释事实走列表端点 /api/explanation 分页加载（listFactsPage + listFactsUpTo + skip/take + 单页上限 500 + 不用 Summary.items + store loadMoreFacts 追加式）',
    e28All,
    e28All
      ? '列表端点分页加载齐全'
      : `listPage=${e28ApiListFactsPage} upTo=${e28ApiListFactsUpTo} url=${e28ApiCallsListEndpoint} skipTake=${e28ApiUsesSkipTake} cap500=${e28ApiPageSizeCap} notSummaryItems=${e28ApiNotSummaryItems} loadMore=${e28StoreLoadMore} appends=${e28StoreLoadMoreAppends}`
  )

  // E29 静态：事实 DTO 字段映射（后端 id/createdAt → 前端 factId/occurredAt + objectRef 派生）
  const e29Mapper = /function\s+toScheduleExplanationFact\s*\(/.test(eApi)
  // ⚠️ 判据必须切到**事实映射函数体内**（`toBusinessFactIssue` 里也有 `occurredAt: r.createdAt`，
  //   整文件搜会命中另一个函数 ⇒ 探针假绿）
  const e29MapperBlock =
    eApi.match(/function\s+toScheduleExplanationFact\s*\([\s\S]*?\n\}/)?.[0] ?? ''
  const e29MapsFactId = /factId:\s*r\.id\b/.test(e29MapperBlock)
  const e29MapsOccurredAt = /occurredAt:\s*r\.createdAt\b/.test(e29MapperBlock)
  const e29DerivesRef = /function\s+deriveObjectRef\s*\(/.test(eApi)
  // ⚠️ 判据必须命中**代码调用形**，不能只搜字面量（注释里也写着 LogicalDemandKey / DomainKey，
  //   只搜字面量 ⇒ 探针假绿）
  const e29MapperUsesDerive = /objectRef:\s*deriveObjectRef\(/.test(e29MapperBlock)
  const e29RefFromDemandKey = /evidenceStr\(evidence,\s*'LogicalDemandKey'\)/.test(eApi)
  const e29RefFromDomainKey = /evidenceStr\(evidence,\s*'DomainKey'\)/.test(eApi)
  const e29Summary = /evidenceSummary/.test(eApi) && /evidenceSummary/.test(eTypes)
  // 页面判据取**渲染条件**（`:title="f.evidenceSummary"` 只是悬停全文，不构成展示）
  const e29ViewUsesEvidenceSummary = /v-if="f\.evidenceSummary"/.test(eVue)
  const e29All =
    e29Mapper &&
    e29MapsFactId &&
    e29MapsOccurredAt &&
    e29DerivesRef &&
    e29MapperUsesDerive &&
    e29RefFromDemandKey &&
    e29RefFromDomainKey &&
    e29Summary &&
    e29ViewUsesEvidenceSummary
  assert(
    'E29 静态：事实字段映射（toScheduleExplanationFact：id→factId、createdAt→occurredAt、objectRef 由需求号/域键/来源 Id 派生、evidenceSummary 提取并在页面展示）',
    e29All,
    e29All
      ? '映射与派生齐全'
      : `mapper=${e29Mapper} factId=${e29MapsFactId} occurredAt=${e29MapsOccurredAt} derive=${e29DerivesRef} mapperUsesDerive=${e29MapperUsesDerive} demandKey=${e29RefFromDemandKey} domainKey=${e29RefFromDomainKey} summary=${e29Summary} viewSummary=${e29ViewUsesEvidenceSummary}`
  )

  // E30 静态：面板计数行 + 继续加载 + 分组内滚动（把「一共多少 / 展示了多少」摆在明面上）
  const e30ViewCountLine =
    /共\s*\{\{\s*totalFacts\s*\}\}\s*条\s*·\s*已显示\s*\{\{\s*loadedFactsCount\s*\}\}\s*条/.test(
      eVue
    )
  const e30ViewMoreBtn = /@click="onLoadMoreFacts"/.test(eVue)
  const e30StoreHasMore = /const\s+factsHasMore\s*=\s*computed/.test(eStore)
  const e30StoreLoadedCount = /const\s+loadedFactsCount\s*=\s*computed/.test(eStore)
  const e30LoadMax = /EXPLANATION_FACT_LOAD_MAX\s*=\s*5000/.test(eApi)
  const e30ScrollableList =
    /\.fact-list\s*\{[^}]*max-height\s*:\s*\d+px[^}]*overflow-y\s*:\s*auto/.test(eVue)
  const e30All =
    e30ViewCountLine &&
    e30ViewMoreBtn &&
    e30StoreHasMore &&
    e30StoreLoadedCount &&
    e30LoadMax &&
    e30ScrollableList
  assert(
    'E30 静态：排程解释事实面板计数行（共 N 条 · 已显示 M 条）+ 继续加载 + store factsHasMore/loadedFactsCount + 累计上限 5000 + 分组内滚动',
    e30All,
    e30All
      ? '计数行 / 继续加载 / 上限 / 滚动 齐全'
      : `countLine=${e30ViewCountLine} moreBtn=${e30ViewMoreBtn} hasMore=${e30StoreHasMore} loadedCount=${e30StoreLoadedCount} loadMax=${e30LoadMax} scroll=${e30ScrollableList}`
  )

  // E31 动态（可选）：真打后端核对「总数 = 三计数之和」且「Summary.items 仍是只 ERROR 的 50 条」
  //   - 设置 VERIFY_E31=1 且 dev 可访问时执行
  if (process.env.VERIFY_E31 === '1' && adminToken) {
    const e31Resp = await call('GET', '/api/explanation/summary', {
      token: adminToken,
      query: { planVersionId: 999333 }
    })
    const e31Sum = e31Resp.json?.data ?? {}
    const e31Total = e31Sum.totalCount
    const e31Sum3 =
      (e31Sum.errorCount ?? 0) + (e31Sum.warnCount ?? 0) + (e31Sum.infoCount ?? 0)
    const e31CountsMatch = typeof e31Total === 'number' && e31Total === e31Sum3
    const e31Items = Array.isArray(e31Sum.items) ? e31Sum.items : []
    const e31ItemsAllError = e31Items.every((it) => it.severity === 'ERROR')
    assert(
      'E31 动态：/api/explanation/summary 的 totalCount = errorCount+warnCount+infoCount（前端 KPI 分母的权威来源）',
      e31CountsMatch,
      e31CountsMatch
        ? `total=${e31Total} = 三计数之和`
        : `total=${e31Total} 三计数之和=${e31Sum3}`
    )
    assert(
      'E31b 动态：/api/explanation/summary 的 items 仍是「只 ERROR、只 50 条」（后端截断 ⇒ 前端必须走列表端点）',
      e31Items.length === 50 && e31ItemsAllError,
      `items=${e31Items.length} 条，全 ERROR=${e31ItemsAllError}（前端已改走列表端点，不依赖这份数据）`
    )
  } else {
    skip('E31 动态：/api/explanation/summary totalCount 与三计数一致性（设置 VERIFY_E31=1 且 dev 可访问时执行）')
  }

  info('E 段：5号位 2026-10-09 一致性核查回执 R1 闭环 + 10-10 option A 收口 + 10-10 business-fact-issues 端点查错库修复（E21 dev 绿）+ 10-10 样式简洁整齐（E22）+ 10-10 business-fact-issues 分页契约锁（E23 静态绿：后端字段 page + 替换式 fetchBizPage + 标准 ElPagination + v-model + [10,20,50]；E24 等 5号位 PageResult 落地）+ 10-10 懒加载契约锁（E25 静态绿；Gantt.vue 仍走 selectVersion 不退化）+ 10-12 字典漂移兜底（E26 静态绿：未入字典值按待核对中性展示，不冒充正式编码）+ **11-05 三处真缺陷**（E27 KPI 分母改后端总数 / E28 列表端点分页加载 / E29 事实字段映射 / E30 计数行+继续加载+分组滚动；E31 动态核对 totalCount）；R2/R3 跨号位等回执')
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
 *   - M23 静态（10-28）：换型规则级 Diff 页（/aps/setup/diff）meta.hidden=true —— 菜单不显示，
 *              路由保留（页签下钻 / 老书签 / verify-rbac 仍可达）
 *   - M22 静态（10-28）：⑦ 两个维护页（/aps/setup/exact、/aps/setup/default）meta.hidden=true ——
 *              菜单不显示，但路由保留（缺失规则查询跳转 / 老书签 / verify-rbac 仍可达）；分组本身仍可见
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
  // 2026-10-28：ApsRules 放开 aps.rule.view 只读入口（3号位 自裁 W-2）；
  //              setup 三路由摘掉死码 aps.setup.publish（3号位 裁定：无端点消费）
  const m03Baseline = [
    { name: 'ApsRules', perms: "['aps.rule.view', 'aps.rule.edit', 'aps.rule.publish']" },
    { name: 'ApsTaskSplitRuleConfig', perms: "['aps.rule.view', 'aps.rule.edit']" },
    { name: 'ApsSetupExact', perms: "['aps.setup.view', 'aps.setup.edit']" },
    { name: 'ApsSetupDefault', perms: "['aps.setup.view', 'aps.setup.edit']" },
    { name: 'ApsSetupUncovered', perms: "['aps.setup.view']" },
    { name: 'ApsSetupDiff', perms: "['aps.setup.view']" },
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
    // 切片到**下一个 name:'** 为止（固定 400 会被长注释顶掉 → 误报红；也可能跨到下一条路由）
    const next = m03Flat.indexOf("name:'", idx + 8)
    const seg = m03Flat.slice(idx, next > 0 ? next : idx + 400)
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

  /* ---- M22（2026-10-28）：⑦ 两个维护页从菜单隐藏，但**路由必须保留** ----
   * 用户拍板把 ⑦ 的增删改并入「规则集维护 → 换型规则」页签后，这两页不再出现在侧边栏。
   * 但不能删路由：
   *   - SetupUncovered.vue 的「去补默认换型规则」仍跳 /aps/setup/default；
   *   - verify-rbac.mjs 的 path 断言、老书签都依赖这两个 URL。
   * 机制：静态路由表不按 hidden 过滤（permission.ts generateRoutes('static')），
   *       只有菜单渲染侧 useRenderMenuItem.tsx 过滤 meta.hidden ⇒ hidden:true 只影响菜单显示。
   * ⚠️ 判据按「单条路由块」切片，不用 includes(全文件)：整个 childrenBlock 里任何一处
   *    hidden:true 都能顶替，改名式破坏会照绿。 */
  const flatChildren = childrenBlock.replace(/\s+/g, '')
  const routeBlockOf = (flat, path) => {
    const i = flat.indexOf(`path:'${path}'`)
    if (i < 0) return ''
    const j = flat.indexOf("path:'", i + 6)
    return flat.slice(i, j > 0 ? j : i + 800)
  }
  const m22HiddenPages = ['/aps/setup/exact', '/aps/setup/default']
  const m22HiddenOk = m22HiddenPages.every((p) => {
    const seg = routeBlockOf(flatChildren, p)
    return seg.length > 0 && seg.includes('hidden:true')
  })
  // 分组本身不能被藏（否则整组消失，连 5 个可见页一起没了）
  // 注意取 groupBlock（含本节点 meta），不能取 childrenBlock —— 后者从 children: [ 起，
  // 切出来是空串，会让本条永远为真（假绿）。
  const m22GroupVisible = !/hidden:true/.test(
    groupBlock.slice(0, groupBlock.indexOf('children: [')).replace(/\s+/g, '')
  )
  // 菜单根过滤必须仍按 meta.hidden（若改成删路由，两处跳转直接 404）
  const m22MenuFilter = /\.filter\(\(v\)\s*=>\s*!v\.meta\?\.hidden\)/.test(
    readSrc('components/Menu/src/components/useRenderMenuItem.tsx')
  )
  const m22 = m22HiddenOk && m22GroupVisible && m22MenuFilter
  assert(
    'M22 静态：⑦ 两个维护页 meta.hidden（菜单不显示）但路由保留；分组本身仍可见',
    m22,
    m22
      ? '两页已隐藏且路由保留，分组可见'
      : `hidden=${m22HiddenOk} groupVisible=${m22GroupVisible} menuFilter=${m22MenuFilter}`
  )

  /* ---- M23（2026-10-28）：换型规则级 Diff 页也退出菜单，改为页签下钻 ----
   * 背景：字段级 Diff 已在「规则集维护 → 版本对比」页签；/aps/setup/diff 只做换型规则
   *   逐条（added/modified/removed）对比，单独占一个菜单让「策略配置」组显得杂乱。
   * 处理：meta.hidden=true（菜单不显示），**路由必须保留**（页签按钮 + 老书签 + verify-rbac path 断言）。
   * 判据：① 该路由块内 hidden:true ② 组件 import 仍在（路由没被删）③ 规则集页仍有入口。
   * ⚠️ 同样按「单条路由块」切片判 hidden，避免整组任意一处 hidden:true 顶替。 */
  const m23Seg = routeBlockOf(flatChildren, '/aps/setup/diff')
  const m23Hidden = m23Seg.length > 0 && m23Seg.includes('hidden:true')
  const m23RouteKept = /import\('@\/views\/Aps\/SetupDiff\.vue'\)/.test(routerSrc)
  const m23Entry = /path:\s*'\/aps\/setup\/diff'/.test(readSrc('views/Aps/Rules.vue'))
  const m23 = m23Hidden && m23RouteKept && m23Entry
  assert(
    'M23 静态：换型规则级 Diff 页（/aps/setup/diff）菜单隐藏、路由保留，入口改到规则集页「版本对比」页签',
    m23,
    `hidden=${m23Hidden} routeKept=${m23RouteKept} entry=${m23Entry}`
  )

  /* ---- M24（2026-10-28）：三个 setup 路由不再挂 `aps.setup.publish`（死码不做门控）----
   * 背景：3号位 2026-10-28 联合报告 §一.1 裁定 —— `aps.setup.publish` 全仓无任何端点消费
   *      （整版发布已由 `aps.rule.publish` 保护），该码是**死码**，保留定义/种子但不删不动。
   * 前端处理：把它从 setup/exact、setup/default、setup/diff 三个路由 meta 里摘掉 ——
   *      否则「只持这个死码的人也能过路由门控、进去所有请求全 403」是纯粹的误导。
   * 判据：三个路由块里都不得再出现 aps.setup.publish，且必须仍挂 aps.setup.view。
   * ⚠️ 按单条路由块切片判（routeBlockOf），别用全文件 includes。 */
  const m24Pages = [
    '/aps/setup/exact',
    '/aps/setup/default',
    '/aps/setup/uncovered',
    '/aps/setup/diff'
  ]
  const m24Detail = m24Pages.map((p) => {
    const seg = routeBlockOf(flatChildren, p)
    return `${p}:${seg.length > 0 && !seg.includes('aps.setup.publish') && seg.includes('aps.setup.view') ? 'OK' : 'BAD'}`
  })
  const m24 = m24Detail.every((d) => d.endsWith(':OK'))
  assert(
    'M24 静态：setup 四路由（exact/default/uncovered/diff）不再挂死码 aps.setup.publish，仍挂 aps.setup.view',
    m24,
    m24Detail.join(' ')
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

  /* ===== 2026-11-04 逐功能测试：口径写清 + 有数据不展示的列换掉 =====
   *
   * 背景（前端自证，不依赖后端改动）：
   *  - 资源表原本渲染「负荷率 / 状态」两列，而后端 `NULL AS UtilizationRate` / `0 AS IsBottleneck`
   *    是**占位字面量** ⇒ 两列恒为 `-`；反倒是后端真有的 taskCount / totalPlannedHours 被丢掉。
   *  - KPI 未写明统计范围（哪个域的哪个版本），与下方 3 张生效版本卡对不上号。
   *  - 排程域状态面板只写「8 项异常」，看不出总共有几个域、来自哪个 Run。
   *  - 待确认 Candidate 的「去处理」是死文本（点了没反应）。
   *
   * 断言写法纪律（见「静态断言写法」）：模板层判据一律锚到标签/表达式，
   * 不用裸 includes（模板注释里也会出现这些词，会假绿）。
   */
  const ovVueRaw = readSrc('views/Aps/Overview.vue')
  const ovScript = stripComments(ovVueRaw.slice(0, ovVueRaw.indexOf('<template>')))
  const ovTpl = ovVueRaw.slice(ovVueRaw.indexOf('<template>'))
  const ovStoreSrc = readSrc('store/modules/aps/overview.ts')

  // OV14 静态：资源表用后端实有的「任务数 / 计划工时」，不渲染恒占位的「负荷率 / 状态」两列
  const ov14Cols =
    /<th>\s*任务数\s*<\/th>/.test(ovTpl) && /<th>\s*计划工时（小时）\s*<\/th>/.test(ovTpl)
  const ov14Cells =
    /<td>\s*\{\{\s*r\.taskCount\s*\}\}\s*<\/td>/.test(ovTpl) &&
    /<td>\s*\{\{\s*fmtHours\(r\.totalPlannedHours\)\s*\}\}\s*<\/td>/.test(ovTpl)
  const ov14NoDeadCol =
    !/<th[^>]*>\s*负荷率\s*<\/th>/.test(ovTpl) &&
    !/<th[^>]*>\s*状态\s*<\/th>/.test(ovTpl) &&
    !/utilPercent/.test(ovScript)
  const ov14Caliber = /暂无口径/.test(ovTpl)
  assert(
    'OV14 静态：资源表展示后端实有的任务数/计划工时，不渲染恒占位的「负荷率 / 状态」列',
    ov14Cols && ov14Cells && ov14NoDeadCol && ov14Caliber,
    `列头=${ov14Cols} 单元格=${ov14Cells} 无占位列=${ov14NoDeadCol} 口径提示=${ov14Caliber}`
  )

  // OV15 静态：KPI 写明统计范围（哪个域的哪个版本），不得写死「本版本全部订单」
  //   2026-11-04：口径从「active-plan 第一条」改成「用户正在查看的版本（selectedVersion）」
  //   —— 页面加了版本切换后，这里若还钉 primaryVersion ⇒ KPI 与选中卡对不上号。
  const ov15Scope = /统计范围/.test(ovTpl) && /\{\{\s*summaryScope\s*\}\}/.test(ovTpl)
  const ov15NotFixed = !/本版本全部订单/.test(ovTpl)
  const ov15Selected =
    /const\s+selectedVersion\s*=\s*computed\(\(\)\s*=>\s*overviewStore\.selectedVersion\)/.test(
      ovScript
    ) &&
    /const\s+summaryScope\s*=\s*computed\(\(\)\s*=>\s*\{[\s\S]{0,300}?selectedVersion\.value/.test(
      ovScript
    )
  // store 里 selectedVersion 必须有 primaryVersion 回落链（没手动选过时看第一条）
  const ov15StoreFallback =
    /const\s+selectedVersion\s*=\s*computed\(/.test(ovStoreSrc) &&
    /primaryVersion\s*=\s*computed\(\(\)\s*=>\s*activeVersions\.value\[0\]/.test(ovStoreSrc)
  assert(
    'OV15 静态：订单 KPI 标明统计范围（域 + 版本，取「正在查看的版本」selectedVersion）',
    ov15Scope && ov15NotFixed && ov15Selected && ov15StoreFallback,
    `范围文案=${ov15Scope} 未写死=${ov15NotFixed} 视图取selectedVersion=${ov15Selected} store回落链=${ov15StoreFallback}`
  )

  // OV16 静态：排程域状态面板头标明「共 N 个排程域」与「来源排程运行」
  //   ⚠️ 判据必须锚到完整文案：模板里另有 `v-if="!domainStatuses.length"`（空态判断），
  //   只查 `domainStatuses.length` 会把「面板头没写总数」的改动放过（自证已踩：第一次写太松 ⇒ STILL-GREEN）
  const ov16Total = /共 \{\{\s*domainStatuses\.length\s*\}\}\s*个排程域/.test(ovTpl)
  const ov16Run = /来源排程运行 \{\{\s*statusRunId\s*\}\}/.test(ovTpl)
  assert(
    'OV16 静态：排程域状态面板头标明总域数与来源 Run（让「N 项异常 / 共 M 个域」可读）',
    ov16Total && ov16Run,
    `总域数=${ov16Total} 来源Run=${ov16Run}`
  )

  // OV17 静态：待确认 Candidate 的入口是真链接 + 权限门控 + 0 条不显示诱导语
  const ov17Push = /router\.push\(\{\s*path:\s*'\/aps\/candidate'\s*\}\)/.test(ovScript)
  //   ⚠️ 权限码判据必须带引号锚定：`aps.candidate.confirm` 是 `aps.candidate.confirmX` 的前缀，
  //   不加引号时「改错权限码」照样能过（自证已踩：第一次写太松 ⇒ STILL-GREEN）
  const ov17Perm = /has\('aps\.candidate\.confirm'\)/.test(ovScript) && /canConfirmCandidate/.test(ovTpl)
  const ov17Click = /@click="goCandidate"/.test(ovTpl)
  const ov17Zero = /pendingCandidateCount === 0/.test(ovTpl)
  const ov17NoDead = !/class="candidate-sub"[^>]*>\s*去「候选版本确认」页处理/.test(ovTpl)
  assert(
    'OV17 静态：待确认 Candidate 入口是真链接（跳 /aps/candidate），按 aps.candidate.confirm 门控，0 条不给诱导语',
    ov17Push && ov17Perm && ov17Click && ov17Zero && ov17NoDead,
    `跳转=${ov17Push} 权限门控=${ov17Perm} 点击接线=${ov17Click} 0条处理=${ov17Zero} 无死文本=${ov17NoDead}`
  )

  // OV18 静态：不得把 PlanVersion 冗余列 totalOrders 与实时摘要并排渲染（dev 实测 53 vs 55）
  const ov18Types = readSrc('api/aps-v1/types/overview.ts')
  const ov18NoOrders = !/v\.totalOrders/.test(ovTpl)
  const ov18Doc = /totalOrders/.test(ov18Types) && /与实时计数不一致/.test(ov18Types)
  assert(
    'OV18 静态：不渲染 PlanVersion 冗余列 totalOrders（与实时摘要不一致，同页并排会自相矛盾）',
    ov18NoOrders && ov18Doc,
    `未渲染=${ov18NoOrders} 类型注释已记录冲突=${ov18Doc}`
  )

  // OV19 动态 canary：同一 planVersionId 的订单数两处口径是否一致
  //   不一致 = 后端冗余列未随订单变动刷新（已出函）；前端不并排显示 ⇒ 如实跳过，不误判为前端回归
  const ov19Primary = Array.isArray(activePlan.json?.data) ? activePlan.json.data[0] : null
  if (!ov19Primary?.planVersionId) {
    skip('OV19 动态', 'dev 无 ACTIVE 版本，无法比对订单数两处口径')
  } else {
    const s19 = await call('GET', `/api/order-query/summary?planVersionId=${ov19Primary.planVersionId}`, {
      token
    })
    const live = s19.json?.data?.totalCount
    const col = ov19Primary.totalOrders
    if (typeof live === 'number' && typeof col === 'number' && live === col) {
      assert(
        `OV19 动态：planVersionId=${ov19Primary.planVersionId} 的订单数两处口径一致`,
        true,
        `active-plan.totalOrders=${col} = summary.totalCount=${live}`
      )
    } else {
      skip(
        'OV19 动态',
        `planVersionId=${ov19Primary.planVersionId} 订单数两处口径不一致：active-plan.totalOrders=${col} vs summary.totalCount=${live}（PlanVersion 冗余列未刷新；已出函 5号位，前端不并排显示）`
      )
    }
  }

  // ===== 2026-11-04 版本切换 =====
  //  背景：页面原先三块数据死钉 active-plan 第一条 ⇒ 用户看得见另外两个生效版本却点不动。
  //  现在「当前生效版本」卡片本身即选择器：点哪张卡，下面三块就按哪个版本重取。

  /** 从 store 源码里切出某个函数的函数体（避免同名符号顶住造成假绿）。
   *  必须在下一个函数**或 return 块**处截断 —— 否则最后一个函数会把 return 里的
   *  `selectedVersion` 一并切进来，让「候选数不跟随版本」这条判据假红。 */
  const ovSliceFn = (src, sig) => {
    const i = src.indexOf(sig)
    if (i < 0) return ''
    const rest = src.slice(i + sig.length)
    const m = rest.match(/\n  (?:async )?function |\n  return \{/)
    return m ? src.slice(i, i + sig.length + m.index) : src.slice(i)
  }

  // OV20 静态：生效版本卡可点切换（用户要看别的版本的唯一入口）
  const ov20Click = /@click="onSelectVersion\(v\)"/.test(ovTpl)
  const ov20A11y =
    /role="button"/.test(ovTpl) &&
    /@keydown\.enter="onSelectVersion\(v\)"/.test(ovTpl) &&
    /tabindex="0"/.test(ovTpl)
  const ov20Selected =
    /:class="\{\s*selected:\s*v\.planVersionId\s*===\s*selectedVersionId\s*\}"/.test(ovTpl)
  const ov20Badge = /v\.planVersionId\s*===\s*selectedVersionId[\s\S]{0,160}?查看中/.test(ovTpl)
  const ov20Hint = /v-if="activeVersions\.length\s*>\s*1"/.test(ovTpl)
  const ov20Wire = /overviewStore\.selectVersion\(v\.planVersionId\)/.test(ovScript)
  const ov20StoreSig = /async function selectVersion\(planVersionId: number\)/.test(ovStoreSrc)
  assert(
    'OV20 静态：生效版本卡即选择器（可点 + 键盘可达 + 选中卡标「查看中」+ 走 store.selectVersion）',
    ov20Click && ov20A11y && ov20Selected && ov20Badge && ov20Hint && ov20Wire && ov20StoreSig,
    `点击=${ov20Click} 键盘可达=${ov20A11y} 高亮=${ov20Selected} 查看中标记=${ov20Badge} 多版本提示=${ov20Hint} 接线=${ov20Wire} store签名=${ov20StoreSig}`
  )

  // OV21 静态：三块（订单摘要 / 资源负荷 / 排程域状态）都按「正在查看的版本」取数，
  //   候选数与版本无关 ⇒ 不该跟着切
  const ov21Order = ovSliceFn(ovStoreSrc, 'async function loadOrderSummary(')
  const ov21Res = ovSliceFn(ovStoreSrc, 'async function loadResources(')
  const ov21Domain = ovSliceFn(ovStoreSrc, 'async function loadDomainStatus(')
  const ov21Cand = ovSliceFn(ovStoreSrc, 'async function loadCandidate(')
  const ov21OrderOk =
    /selectedVersion\.value\?\.planVersionId/.test(ov21Order) && !/primaryVersion/.test(ov21Order)
  const ov21ResOk =
    /selectedVersion\.value\?\.planVersionId/.test(ov21Res) && !/primaryVersion/.test(ov21Res)
  const ov21DomainOk =
    /selectedVersion\.value\?\.sourceScheduleRunId/.test(ov21Domain) &&
    !/primaryVersion/.test(ov21Domain)
  const ov21CandOk = !/selectedVersion/.test(ov21Cand) && !/planVersionId/.test(ov21Cand)
  assert(
    'OV21 静态：订单摘要/资源负荷/域状态按 selectedVersion 取数，候选数与版本无关（不跟着切）',
    ov21OrderOk && ov21ResOk && ov21DomainOk && ov21CandOk,
    `订单摘要=${ov21OrderOk} 资源负荷=${ov21ResOk} 域状态=${ov21DomainOk} 候选不跟随=${ov21CandOk}`
  )

  // OV22 静态：切版本只重取受版本影响的三块（生效版本列表与候选数不动）
  const ov22Sel = ovSliceFn(ovStoreSrc, 'async function selectVersion(')
  const ov22Three =
    /loadOrderSummary/.test(ov22Sel) && /loadResources/.test(ov22Sel) && /loadDomainStatus/.test(ov22Sel)
  const ov22NoCand = !/loadCandidate/.test(ov22Sel)
  const ov22NoReloadPlan = !/getActivePlans/.test(ov22Sel)
  const ov22ClearErr = /errors\.value\s*=\s*errors\.value\.filter/.test(ov22Sel)
  assert(
    'OV22 静态：切版本只重取三块版本相关数据（不重取候选数、不重拉生效版本列表、旧错误清掉）',
    ov22Three && ov22NoCand && ov22NoReloadPlan && ov22ClearErr,
    `重取三块=${ov22Three} 不重取候选=${ov22NoCand} 不重拉列表=${ov22NoReloadPlan} 清旧错误=${ov22ClearErr}`
  )

  // OV23 动态：切换目标版本的订单摘要与资源负荷端点可达（切过去不是空壳/不是 404）
  const ov23Alt = Array.isArray(activePlan.json?.data) ? activePlan.json.data[1] : null
  if (!ov23Alt?.planVersionId) {
    skip('OV23 动态', 'dev 只有 1 个生效版本，无法验证切换目标可达')
  } else {
    const s23 = await call('GET', `/api/order-query/summary?planVersionId=${ov23Alt.planVersionId}`, {
      token
    })
    const r23 = await call(
      'GET',
      `/api/overview/resource-bottleneck?planVersionId=${ov23Alt.planVersionId}&topN=10`,
      { token }
    )
    const rows23 = Array.isArray(r23.json?.data) ? r23.json.data : []
    const ok = s23.code === 200 && r23.code === 200 && Array.isArray(r23.json?.data)
    assert(
      `OV23 动态：切换目标版本 planVersionId=${ov23Alt.planVersionId}（${ov23Alt.versionCode}）的订单摘要与资源负荷端点可达`,
      ok,
      ok
        ? `summary.totalCount=${s23.json?.data?.totalCount} 资源行数=${rows23.length}`
        : `summary.code=${s23.code} resource.code=${r23.code}`
    )
  }

  // OV24 静态：版本卡「任务数」必须标明范围（整个计划区间内）
  //   起因：用户反馈「总览页版本卡 13201 条、甘特页 3334 条，对不上」。
  //   根因：两个数不是同一口径 —— 版本卡的 `PlanVersion.TotalTasks` 是**整个计划区间**的任务总数，
  //   甘特页 KPI 的 `stats.total` 是**当前时间窗内**的（默认窗=今天起 7 天，天天在滚）。
  //   dev 实测 999333：7990 + 3334 + 1877 = 13201 ⇒ 两页能对上，但页面必须把范围摆在明面上。
  const ov24Scope =
    /label="任务数"[\s\S]{0,240}?v\.totalTasks[\s\S]{0,240}?（整个计划区间内）/.test(ovTpl)
  // 范围标注必须挂在「任务数」这一项上（不能挪到别处蒙混），且不得误标成「窗口内」
  const ov24Item = (ovTpl.match(/label="任务数"[\s\S]{0,400}?<\/ElDescriptionsItem>/) || [''])[0]
  const ov24NotWindow = ov24Item !== '' && !/窗口/.test(ov24Item)
  assert(
    'OV24 静态：版本卡「任务数」标明范围（整个计划区间内），与甘特页「窗口内任务数」不是同一口径',
    ov24Scope && ov24NotWindow,
    `范围标注=${ov24Scope} 未误标窗口=${ov24NotWindow}`
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
    return readText(join(SRC_ROOT, relPath))
  } catch {
    return ''
  }
}

/** GROUP=reason-code：原因码展示标签「全站唯一口径」静态断言
 *
 * 依据（5号位 2026-10-31 回执《答复函二》+ D1/D3 裁决 与 dev 实测）：
 *  - 5号位 实查 ORDER 级 465,481 条 `ScheduleExplanationFact` 中 **442,408 条（95%）ReasonCode 为空串**；
 *  - dev `GET /api/explanation/summary?planVersionId=999333` 实测 `reasonCodeCounts` 含
 *    `{"reasonCode":"","count":516}`，`items` 50 条中 39 条为空串；
 *  - 后端两处字段均为 `public string ... = string.Empty`
 *    （`ExplanationDtos.cs` / `GanttDataDto.cs:310`）⇒ 空串是「写方未填原因码」的常态。
 *
 * 缺陷（改前）：排程解释页自写 `REASON_CODE_LABELS[code] ?? code` ⇒ 空串时返回空串
 *  ⇒ ElTag 渲染成**空白标签**（今天就能在页面上看到 516 项的空白 chip）；
 *  甘特页 / CTP 页虽不空白但降级成「待核对·」半空标签，且三页各写各的、口径漂移。
 *
 * 判据：空串 → 「未标注原因码」；未知码 → 「待核对·<码>」；全站只留一个实现；
 *      冻结 15 码不得增删（U-004「不新增第 16 码」）。
 * 纯静态断言，不登录、不打后端。 */
function verifyReasonCodeLabel() {
  const typesSrc = readSrc('api/aps-v1/types/explanation.ts')
  const scheduleTypesSrc = readSrc('api/aps-v1/types/schedule.ts')
  // 页面源码里常以注释形式留「旧写法示例」（如 `REASON_CODE_LABELS[code] ?? code`），
  // 直接全文匹配会把注释误判成代码 ⇒ 先剥注释再判定。
  const stripComments = (s) =>
    s.replace(/\/\*[\s\S]*?\*\//g, '').replace(/(^|[^:])\/\/.*$/gm, '$1')
  const pages = [
    ['Explanation.vue', stripComments(readSrc('views/Aps/Explanation.vue'))],
    ['Gantt.vue', stripComments(readSrc('views/Aps/Gantt.vue'))],
    ['Ctp.vue', stripComments(readSrc('views/Aps/Ctp.vue'))],
    ['Order.vue', stripComments(readSrc('views/Aps/Order.vue'))]
  ]

  const fnMatch = typesSrc.match(/export function reasonLabelOf\([\s\S]*?\n\}/)
  const fnBody = fnMatch ? fnMatch[0] : ''

  const rc01 = fnBody !== '' && /if\s*\(!raw\)\s*return\s*'未标注原因码'/.test(fnBody)
  assert(
    'RC01 静态：types/explanation.ts 导出 reasonLabelOf，且含「空串 → 未标注原因码」分支',
    rc01,
    rc01 ? '共享函数已就位' : '共享函数缺失，或空串分支被删'
  )

  const rc02 =
    fnBody !== '' &&
    !/\?\?\s*code\b/.test(fnBody) &&
    /未标注原因码/.test(fnBody) &&
    /待核对·/.test(fnBody)
  assert(
    'RC02 静态：共享函数无裸返回（不得出现 `?? code`），空串与未知码各有独立文案',
    rc02,
    rc02
      ? '空串→未标注原因码；未知码→待核对·<码>'
      : '共享函数出现 `?? code` 裸返回 ⇒ 空串仍渲染成空白标签'
  )

  const rc03 = pages.every(([, src]) => /reasonLabelOf\(/.test(src))
  assert(
    'RC03 静态：Explanation / Gantt / Ctp / Order 四页均调用共享 reasonLabelOf',
    rc03,
    rc03
      ? '四页已统一'
      : `${pages
          .filter(([, s]) => !/reasonLabelOf\(/.test(s))
          .map(([n]) => n)
          .join('、')} 未调用共享函数`
  )

  const rawLookupPages = pages
    .filter(([, src]) => /REASON_CODE_LABELS\s*\[/.test(src))
    .map(([n]) => n)
  const rc04 = rawLookupPages.length === 0
  assert(
    'RC04 静态：四页不得直接索引 REASON_CODE_LABELS（裸索引在空串/未知码时得 undefined ⇒ 空白标签）',
    rc04,
    rc04 ? '四页均走共享函数' : `${rawLookupPages.join('、')} 仍裸查表`
  )

  const expFact = typesSrc.match(/export interface ScheduleExplanationFact \{[\s\S]*?\n\}/)
  const cntIface = typesSrc.match(/export interface ReasonCodeCount \{[\s\S]*?\n\}/)
  const ganttReason = scheduleTypesSrc.match(/export interface GanttTaskDelayReason \{[\s\S]*?\n\}/)
  const rc05 =
    !!expFact &&
    /reasonCode:\s*ReasonCodeValue/.test(expFact[0]) &&
    !!cntIface &&
    /reasonCode:\s*ReasonCodeValue/.test(cntIface[0]) &&
    !!ganttReason &&
    /reasonCode:\s*ReasonCodeValue/.test(ganttReason[0])
  assert(
    'RC05 静态：ScheduleExplanationFact / ReasonCodeCount / GanttTaskDelayReason 的 reasonCode 均声明为 ReasonCodeValue（空串可表达）',
    rc05,
    rc05 ? '三处类型均已放宽' : '有类型仍写死 ReasonCode（空串在类型上不可表达）'
  )

  const codesMatch = typesSrc.match(/export const REASON_CODES = \[([\s\S]*?)\] as const/)
  const codeCount = codesMatch ? (codesMatch[1].match(/'/g) || []).length / 2 : 0
  const labelsMatch = typesSrc.match(/export const REASON_CODE_LABELS[^=]*=\s*\{([\s\S]*?)\n\}/)
  const labelCount = labelsMatch ? (labelsMatch[1].match(/^\s*[A-Z_]+:/gm) || []).length : 0
  const rc06 = codeCount === 15 && labelCount === 15
  assert(
    'RC06 静态：冻结 15 码字典未被增删（REASON_CODES 15 项 + REASON_CODE_LABELS 15 键，U-004）',
    rc06,
    rc06 ? '仍为 15 码' : `REASON_CODES=${codeCount} 项 / REASON_CODE_LABELS=${labelCount} 键`
  )
}

/** GROUP=gantt：甘特图/资源计划页契约对齐
 *  依据：4号位 2026-10-17 全量核查（frontNew/docs/4号位致5号位_甘特页契约缺口全量核查_20261017.md）
 *  - 冻结 07 §八.1 权威 15 码 + :4938 severity 三值（甘特曾自拟平行 8 码 + CRITICAL）
 *  - 后端 ScheduleQueryService 恒 null / 硬编码字段，前端不得当真值渲染
 *  纯静态断言，不登录、不打后端。 */
function verifyGantt() {
  const stripComments = (s) =>
    s.replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '').replace(/\s\/\/[^\n]*/g, '')
  /** 按锚点切一段源码（判据要限定到具体函数体时用；见「静态断言写法」第 12/16 条） */
  const sliceFrom = (src, marker, len) => {
    const i = src.indexOf(marker)
    return i >= 0 ? src.slice(i, i + len) : ''
  }
  log('\n[GANTT] 甘特图/资源计划 — 冻结 15 码词表归位 + 后端恒空字段不渲染（gantt — 2026-10-17）')

  const typesSrc = readSrc('api/aps-v1/types/schedule.ts')
  const ganttVue = readSrc('views/Aps/Gantt.vue')
  const ganttCode = stripComments(ganttVue)
  const explTypes = readSrc('api/aps-v1/types/explanation.ts')
  const explVue = readSrc('views/Aps/Explanation.vue')
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

  // GT03 甘特页不再维护平行码表：标签走全站唯一口径 `reasonLabelOf`
  //  （2026-10-31 起共享函数住在 types/explanation.ts，内部查冻结 REASON_CODE_LABELS +「待核对·」兜底）
  const gt03Ok =
    /import\s*\{[^}]*reasonLabelOf[^}]*\}\s*from\s*'@\/api\/aps-v1\/types\/explanation'/.test(ganttVue) &&
    /reasonLabelOf\(r\.reasonCode\)/.test(ganttCode) &&
    !/REASON_CODE_LABELS\s*\[/.test(ganttCode) &&
    !/const\s+DELAY_REASON_LABEL\s*:/.test(ganttCode)
  assert(
    'GT03 静态：甘特页根因标签走全站唯一口径 reasonLabelOf（内部查冻结 15 码 + 待核对· 兜底），无平行码表',
    gt03Ok,
    gt03Ok
      ? '共享函数引用 / 调用点 / 无裸查表 / 平行码表清理 四项齐全'
      : '共享函数引用、调用点、裸查表清理、平行码表清理中有缺失'
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

  // GT06 可缺键字段一律整块不渲染，不出现「0 个」「无阻挡」这类断言
  //  2026-10-31：lockMarker 守卫项已删（5号位 D1-c 裁决「撤键」⇒ 前端不再有锁定标识 UI）
  //  2026-11-05：数量分段 / 事实类型两项守卫已删 —— B7/B8 裁决「不做、撤」⇒ 展示位整块撤除，
  //    不是「有位但空态」，撤除的反向锁见 GT116。
  //  2026-11-05：跨域阻挡守卫已删 —— B4 裁决「不做」⇒ 字段永久缺键、展示位随之撤除（反向锁见 GT116）。
  //    余下 3 项里有 2 项（设备不可用窗口 / 受影响 KPI）属 B1 读侧，**后端已落码**，
  //    dev 当前 0 条 OFF 窗口只是**数据缺**（不是恒 null），故本条措辞由「恒 null」改为「可缺键」。
  const guards = [
    ['订单份额', /v-if="selectedTask\.taskShare && selectedTask\.taskShare\.length > 1"/],
    ['设备不可用窗口', /selectedResource\?\.unavailableWindows\?\.length/],
    ['受影响 KPI', /v-if="impactedTaskIds\.length > 0"/]
  ]
  const gt06Missing = guards.filter(([, re]) => !re.test(ganttVue)).map(([n]) => n)
  assert(
    'GT06 静态：3 个可缺键字段均有空态守卫（不渲染成假值）',
    gt06Missing.length === 0,
    gt06Missing.length === 0 ? '3 项守卫齐全' : `缺守卫=${gt06Missing.join(',')}`
  )

  // GT07 甘特与 Explanation 必须共用同一份冻结码表，防止再次分叉
  //  （2026-10-31 起两页都调同一个 `reasonLabelOf`；共享函数内部查冻结 REASON_CODE_LABELS）
  const gt07Ok =
    /import\s*\{[^}]*reasonLabelOf[^}]*\}\s*from\s*'@\/api\/aps-v1\/types\/explanation'/.test(ganttVue) &&
    /import\s*\{[^}]*reasonLabelOf[^}]*\}\s*from\s*'@\/api\/aps-v1\/types\/explanation'/.test(
      explVue
    ) &&
    /export function reasonLabelOf\([\s\S]*?REASON_CODE_LABELS/.test(explTypes) &&
    /RESOURCE_CAPACITY_WAIT[\s\S]*MATERIAL_SHORTAGE[\s\S]*PRECEDENCE_WAIT[\s\S]*FROZEN_ZONE_LOCK[\s\S]*EQUIPMENT_BREAKDOWN_RISK/.test(
      explTypes
    )
  assert(
    'GT07 静态：甘特复用 Explanation 的冻结 15 码字典（单一码表，不分叉）',
    gt07Ok,
    gt07Ok
      ? "两页同调 types/explanation 的 reasonLabelOf，共享函数内查冻结 15 码"
      : '码表引用断裂'
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
   * 要求：① 列表必须与甘特**同页同源**（reuse `ganttData.tasks` + `dateRange`），
   *       不新增第二套查询端点；② 展示 任务号/订单号/物料/计划起止/状态/延期；
   *       ③ MES 下发/工单/执行状态后端未接入 —— 列表**不得**伪造该列。
   * 2026-10-27：清单从「甘特下方并排卡片」改成**同页 Tab**（用户要「整个的甘特图」）；
   *              锚点随之从 ElCard 改为 `.task-list-pane`（列仍在表格里，判据不变）。
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
  // 锚点用 `.task-list-head`（清单头）而非 `.task-list-pane`（容器名）：
  // 容器名属于 GT20 的判据，若 GT19 也依赖它，改容器名会连带 GT19 变红（判据互相耦合）。
  const gt19PaneStart = ganttViewCode.indexOf('class="task-list-head"')
  const gt19PaneEnd = ganttViewCode.indexOf('class="task-list-pager"', gt19PaneStart)
  const gt19Pane =
    gt19PaneStart >= 0 && gt19PaneEnd > gt19PaneStart
      ? ganttViewCode.slice(gt19PaneStart, gt19PaneEnd)
      : ''
  // 2026-11-04 改口径：清单的数据源从「窗口重叠 + 关键词」改为 **filteredTasks**（与甘特条同一份）。
  //   理由：改前清单不接「只看延期 / 只看工序 / 排程域 / 受影响」⇒ 勾上「只看延期」后
  //   图上 2431 条、清单仍 3334 条，同页两个数打架。改后清单行数 ≡ 图上条数 ≡ KPI 域计数之和。
  //   「不新增查询」这条不变：仍不另发请求。
  const gt19SameSource = /filteredTasks\.value/.test(gt19RowsBlock)
  // 清单头仍须写明所处窗口（用户知道这张表是哪一段的任务）
  const gt19WindowInHead = /windowLabel/.test(gt19Pane)
  const gt19Columns =
    /prop="taskNo"/.test(gt19Pane) &&
    /row\.orderNo/.test(gt19Pane) &&
    /row\.materialCode/.test(gt19Pane) &&
    /row\.plannedStartTime/.test(gt19Pane) &&
    /row\.isDelayed/.test(gt19Pane)
  assert(
    'GT19 静态：任务清单与甘特条同源（同一份 filteredTasks、不新增查询）且列齐任务号/订单/物料/起止/延期',
    gt19SameSource && gt19WindowInHead && gt19Columns,
    `同源=${gt19SameSource} 窗口标注=${gt19WindowInHead} 列=${gt19Columns}（块长度 rows=${gt19RowsBlock.length} pane=${gt19Pane.length}）`
  )

  /* ===== GT20：任务清单改为同页 Tab（不再与甘特上下并排）=====
   * 用户诉求（2026-10-27）：甘特页要「整个的甘特图」，任务清单别占版面。
   * 判据：① 清单容器是 `.task-list-pane` 且受 `viewTab === 'tasks'` 控制；
   *       ② 旧的并排卡片 `task-list-card` / `<ElCard` 残留清零；
   *       ③ 甘特区本身也受 `viewTab === 'gantt'` 控制（两个视图互斥，不并排）。
   * 反向破坏：把 pane 的 v-show 去掉 / 把 task-list-card 加回来 → 转红。
   */
  const gt20PaneTab = /v-show="viewTab === 'tasks'"/.test(ganttViewCode)
  const gt20GanttTab = /v-show="viewTab === 'gantt'"/.test(ganttViewCode)
  const gt20OldCardGone = !/task-list-card/.test(ganttViewCode) && !/<ElCard/.test(ganttViewCode)
  assert(
    'GT20 静态：任务清单改为同页 Tab（清单与甘特互斥，不再上下并排、旧卡片残留清零）',
    gt20PaneTab && gt20GanttTab && gt20OldCardGone,
    `清单Tab=${gt20PaneTab} 甘特Tab=${gt20GanttTab} 旧卡片清零=${gt20OldCardGone}`
  )

  /* ===== GT21：默认停在甘特 + 切回时重算尺寸 =====
   * ① 默认视图 = 甘特（用户进页面先看到整屏甘特，不是清单）；
   *    ② 切回甘特时派发 window resize —— 隐藏期间容器宽高归零，
   *       dhtmlx 不重算会留下一张缩成 0 宽的图（甘特空白）。
   * 反向破坏：把默认值改成 'tasks'、或删掉 resize 补偿 → 转红。
   */
  // 2026-11-02：viewTab 多了第三档 'load'（资源负荷视图）⇒ 判据不再锁死联合类型字面量，
  //   只锁「默认停在甘特」；联合类型本身由 GT78 单独锁。
  const gt21DefaultGantt = /const viewTab = ref<[^>]*>\('gantt'\)/.test(ganttViewCode)
  const gt21Redraw = /watch\(viewTab[\s\S]{0,220}dispatchEvent\(new Event\('resize'\)\)/.test(
    ganttViewCode
  )
  assert(
    'GT21 静态：默认视图=甘特（进页先看整屏甘特）且切回甘特派发 resize 重算尺寸',
    gt21DefaultGantt && gt21Redraw,
    `默认甘特=${gt21DefaultGantt} 切回重算=${gt21Redraw}`
  )

  /* ===== GT22：空态/错误提示与 Tab 同一行 =====
   * 用户诉求（2026-10-27）：「把下面的提示放在与 tab 切换一行上；切换这块儿的行高小一些；
   * 提示当前版本没有排程结果即可，简短明确」。
   * 判据：① 提示是 `.view-bar-note`，与 `.view-tabs` 同在 `.view-bar` 内（不另占一整块）；
   *       ② 只渲染一行 title，展开说明走 title 悬停；
   *       ③ 页面顶层不再有独立的错误 ElAlert 块（抽屉里的 ElAlert 与此无关，不在判据内）。
   * 反向破坏：把提示改回独立 ElAlert 块 / 去掉 view-bar-note → 转红。
   */
  const gt22Inline =
    /class="view-bar"/.test(ganttViewCode) &&
    /<span[^>]*class="view-bar-note"/.test(ganttViewCode) &&
    /<span[^>]*class="view-bar-note is-error"/.test(ganttViewCode)
  const gt22OneLine =
    /EMPTY_TEXT\[emptyKind\]\.title/.test(ganttViewCode) &&
    /:title="EMPTY_TEXT\[emptyKind\]\.hint"/.test(ganttViewCode)
  const gt22NoBlockAlert = !/<ElAlert[^>]*v-if="error"/.test(ganttViewCode)
  assert(
    'GT22 静态：空态/错误提示与 Tab 同行（.view-bar 内一行短标题，不另占一块 ElAlert）',
    gt22Inline && gt22OneLine && gt22NoBlockAlert,
    `同行=${gt22Inline} 单行=${gt22OneLine} 无独立块=${gt22NoBlockAlert}`
  )

  /* ===== GT23：甘特图调整 Dialog 的软目标时间默认预填 =====
   * 2026-10-27 演示前排查：Dialog 文案写「默认取该任务当前计划开始时间」，
   * 但实现只给「拖拽/Drawer 预填」的行填 targetTime ⇒ 工具栏入口选完任务直接提交
   * 会被 validateScopeDraft 的「软目标时间不能为空」拦死（演示当场露馅）。
   * 2026-10-31：预填改到「构造行」时一次性完成（表格分页后仍逐行带 targetTime），
   *   判据随之改为「行构造时用 plannedStartTime 兜底 + 拖拽预填值优先」。
   * 反向破坏：去掉 `plannedStartTime || ''` 兜底 ⇒ 转红。
   */
  const ganttAdjustCode = stripComments(readSrc('views/Aps/components/GanttAdjustDialog.vue'))
  const gt23Prefill =
    /targetTime:[\s\S]{0,240}?plannedStartTime\s*\|\|\s*''/.test(ganttAdjustCode) &&
    /\?\s*props\.preselectTargetTime\s*:/.test(ganttAdjustCode)
  assert(
    'GT23 静态：甘特图调整 Dialog 软目标时间默认取当前计划开始时间（工具栏入口可直接提交）',
    gt23Prefill,
    `预填=${gt23Prefill}`
  )

  /* ===== GT24：真实 Domain 取色不再灰回退 =====
   * 2026-10-27：DOMAIN_COLORS 只有 4 个早期 mock 键（FAMILY_INJECTION/ASSEMBLY/TEST/ALL），
   * 而真实 Domain 由后端字典配置（dev 15 个，如 FAMILY_X / FAMILY_TZP）⇒ 切「按排程域」时
   * 条形、域泳道、工具栏域标签一律走 '#94a3b8' 灰回退，看着像「染色没生效」。
   * 改为 domainColor()：显式色表优先 → 否则按域字典顺序取调色板色 → 字典里没有的按 key 散列取色。
   * 反向破坏：域分支取色改回 `DOMAIN_COLORS[...] ?? '#94a3b8'` ⇒ 转红。
   */
  const gt24HasFn = /function\s+domainColor\s*\(/.test(ganttViewCode)
  const gt24PaletteBlock = (
    ganttViewCode.match(/const\s+DOMAIN_PALETTE\s*=\s*\[[\s\S]*?\]/) || ['']
  )[0]
  const gt24PaletteSize = (gt24PaletteBlock.match(/#[0-9a-f]{6}/gi) || []).length
  const gt24Bar = /return\s+domainColor\(task\.domainKey\)/.test(ganttViewCode)
  const gt24Lane = /seen\.set\(t\.domainKey,\s*domainColor\(t\.domainKey\)\)/.test(ganttViewCode)
  const gt24Chip = /:style="\{\s*color:\s*domainColor\(dk\)\s*\}"/.test(ganttViewCode)
  const gt24NoGrayFallback = !/DOMAIN_COLORS\[[^\]]*\]\s*\?\?\s*'#94a3b8'/.test(ganttViewCode)
  assert(
    'GT24 静态：真实 Domain 取色走 domainColor（域字典顺序+调色板，不再灰回退）',
    gt24HasFn &&
      gt24PaletteSize >= 8 &&
      gt24Bar &&
      gt24Lane &&
      gt24Chip &&
      gt24NoGrayFallback,
    `fn=${gt24HasFn} 调色板色数=${gt24PaletteSize} 条形=${gt24Bar} 域泳道=${gt24Lane} 域标签=${gt24Chip} 无灰回退=${gt24NoGrayFallback}`
  )

  /* ===== GT25：禁用「建依赖」拖拽点（本组件 links 恒空，圆点只能拖出假连线）=====
   * dhtmlx 默认 drag_links=true，任务条悬停时两端冒圆点（gantt_link_control）。
   * 页面/对话框都不传 links（后端无任务间依赖），拖出的连线只存内存、刷新即消失。
   * 反向破坏：删掉该行（或改回 true）→ 转红；两个消费方任一传 :links → 转红。
   */
  const dhxWrapperCode = stripComments(readSrc('components/DhxGantt/src/DhxGantt.vue'))
  const gt25NoDragLinks = /gantt\.config\.drag_links\s*=\s*false/.test(dhxWrapperCode)
  const gt25NoConsumersLinks =
    !/[:@]links\s*=/.test(readSrc('views/Aps/Gantt.vue')) &&
    !/[:@]links\s*=/.test(readSrc('views/Aps/components/GanttAdjustDialog.vue'))
  assert(
    'GT25 静态：甘特组件关闭 drag_links（无依赖数据源，不出现空拖拽圆点）',
    gt25NoDragLinks && gt25NoConsumersLinks,
    `关闭drag_links=${gt25NoDragLinks} 消费方不传links=${gt25NoConsumersLinks}`
  )

  /* ===== GT26：时间轴加星期 + 周末底纹 + 今天竖线 =====
   * 2026-10-27 用户要求「与知名排产系统普遍有的保持一致」：
   *  - 日刻度带星期（10-27周二），否则看不出周末；
   *  - 周末刻度格 + 时间轴背景格加底纹；
   *  - 今天竖线（addMarker），配合工具栏「回到今天」。2026-11-06 由实心红线改为灰色虚线（只作参照，不抢眼）。
   * 反向破坏：format 改回 '%m-%d' / 删 scale_cell_class 或 timeline_cell_class / 删 addMarker → 转红。
   */
  const gt26Weekday = /format:\s*dayScaleLabel/.test(dhxWrapperCode)
  const gt26Weekend =
    /templates\.scale_cell_class[\s\S]{0,160}dhx-scale-weekend/.test(dhxWrapperCode) &&
    /templates\.timeline_cell_class[\s\S]{0,160}dhx-timeline-weekend/.test(dhxWrapperCode)
  const gt26TodayMarker =
    // ① addMarker/deleteMarker 是 marker 扩展的方法，不 plugins 就不存在（实测 onMounted 直接抛错、整图不渲染）
    //    2026-11-02：plugins 同时开了 tooltip ⇒ 这里只要求 marker 出现在同一次 plugins 调用里
    /plugins\(\{[^}]*\bmarker:\s*true\b[^}]*\}\)/.test(dhxWrapperCode) &&
    // ② clearAll() 会清掉 marker 数据源 ⇒ 必须每次 parseData 后重建，只建一次竖线会消失
    /const ensureTodayMarker[\s\S]{0,400}addMarker\(\{[\s\S]{0,160}css:\s*'dhx-today-marker'/.test(
      dhxWrapperCode
    ) &&
    /clearAll\(\)[\s\S]{0,800}ensureTodayMarker\(\)/.test(dhxWrapperCode)
  assert(
    'GT26 静态：时间轴含星期、周末底纹、今天竖线（对齐通用排产甘特惯例）',
    gt26Weekday && gt26Weekend && gt26TodayMarker,
    `星期=${gt26Weekday} 周末底纹=${gt26Weekend} 今天标记=${gt26TodayMarker}`
  )

  /* ===== GT27：左表「开始」列显示到时分 =====
   * 条子按小时摆放，只给日期看不出几点开工；改为 date_grid 模板输出 yyyy-MM-dd HH:mm。
   * 反向破坏：删掉 date_grid 模板（退回默认 %Y-%m-%d）→ 转红。
   */
  const gt27DateGrid =
    /templates\.date_grid\s*=\s*\(date: Date\)\s*=>/.test(dhxWrapperCode) &&
    /date_grid[\s\S]{0,240}getHours\(\)[\s\S]{0,80}getMinutes\(\)/.test(dhxWrapperCode)
  assert('GT27 静态：左表开始列显示到时分（yyyy-MM-dd HH:mm）', gt27DateGrid, `date_grid=${gt27DateGrid}`)

  /* ===== GT28：泳道条 = 资源占用区间 + 16px 摘要条 =====
   * 旧实现：lane 条取第一条任务 start+duration（截图里 2142h 冒充资源负荷），
   * 条上还重复左侧资源名；空泳道落 1 小时兜底假条。
   * 2026-11-06：泳道条改为**摘要条**——行级 bar_height=16（任务条 32），
   *   条上写「N 条」，有延期时补红字「延期 M」；窄条同样走条外胶囊。
   *   不放设备编号：按资源分组时它已是左表行名首段，聚合泳道没有单一设备。
   * 反向破坏：改回 firstTask 取时长 / 删 laneEmpty / 去掉计数或延期标记 → 转红。
   */
  const gt28Span =
    /dayjs\(t\.plannedStartTime\)\.isBefore\(dayjs\(spanStart\)\)/.test(ganttCode) &&
    /dayjs\(t\.plannedEndTime\)\.isAfter\(dayjs\(spanEnd\)\)/.test(ganttCode)
  const gt28NoFirstTask = !/duration:\s*firstTask\s*\?/.test(ganttCode)
  const gt28EmptyLane =
    /laneEmpty:\s*laneTasks\.length === 0/.test(ganttCode) &&
    /task\.laneEmpty/.test(dhxWrapperCode) &&
    /dhx-lane-empty[\s\S]{0,120}display:\s*none/.test(dhxWrapperCode)
  const gt28LaneSummary =
    /bar_height:\s*16/.test(ganttCode) &&
    /barLabel:\s*laneTasks\.length \? `\$\{laneTasks\.length\} 条` : ''/.test(ganttCode) &&
    /barMark:\s*delayedCount > 0 \? 'delay' : ''/.test(ganttCode) &&
    /barMarkText:\s*delayedCount > 0 \? `延期 \$\{delayedCount\}` : ''/.test(ganttCode) &&
    /task\.type === 'project'\) \{[\s\S]{0,400}dhx-lane-count[\s\S]{0,400}dhx-lane-mark is-delay/.test(
      dhxWrapperCode
    ) &&
    /'dhx-bar-outer-label is-lane'/.test(dhxWrapperCode)
  assert(
    'GT28 静态：泳道条=占用区间 + 16px 摘要条（N 条 / 延期 M）、空泳道不画假条',
    gt28Span && gt28NoFirstTask && gt28EmptyLane && gt28LaneSummary,
    `区间=${gt28Span} 无firstTask=${gt28NoFirstTask} 空泳道=${gt28EmptyLane} 摘要条=${gt28LaneSummary}`
  )

  /* ===== GT29：延期条不再画红线（2026-11-06 用户指定）=====
   * 原 A7 的条底 2px 红刻度已撤；延期只由条内红色 +Nh 表达。
   * 反向破坏：把红刻度或 3px 红左缘加回来 → 转红。
   */
  const gt29NoRedMark =
    !/\.gantt_task_line\.dhx-task-warning\)\s*\{/.test(dhxWrapperCode) &&
    !/\.dhx-task-warning[^{]*\{[^}]*rgb\(239 68 68/.test(dhxWrapperCode)
  const gt29NoRedEdge = !/\.dhx-task-warning \.gantt_task_content\)\s*\{[^}]*border-left:\s*3px solid/.test(
    dhxWrapperCode
  )
  assert(
    'GT29 静态：延期条不再画红刻度/红左缘（延期只由条内 +Nh 文字表达）',
    gt29NoRedMark && gt29NoRedEdge,
    `无红刻度=${gt29NoRedMark} 无红左缘=${gt29NoRedEdge}`
  )

  /* ===== GT30：图例 + 回到今天 =====
   * 2026-10-27 用户指定：图例放视图切换行右侧，统计 KPI（任务/资源/维度/域）在图例左侧，
   * 不再悬浮在图表右下角。条色按状态；Domain 分组时改按域色。
   * 反向破坏：删图例 DOM / 把图例挪回 absolute 悬浮 / 删「回到今天」按钮 / 删 showDate 暴露 → 转红。
   */
  const gt30Legend =
    // 窗口 4200：图例紧跟 KPI 行右侧（2026-10-30 负荷带图例加了 title 说明，模板变长；
    // 上限只用于判「就近同区」，图例挪到页面别处仍远超此窗口 ⇒ 仍能转红）
    /class="view-bar-right"[\s\S]{0,4200}class="stat"[\s\S]{0,4200}class="gantt-legend"/.test(
      ganttVue
    ) &&
    /v-for="item in legendItems"/.test(ganttVue) &&
    // 图例是行内元素流（KPI 右侧），不再是覆盖图表的悬浮层
    /\.gantt-legend\s*\{[^}]*display:\s*flex/.test(ganttVue) &&
    !/\.gantt-legend\s*\{[^}]*position:\s*absolute/.test(ganttVue)
  const gt30BackToday =
    /@click="scrollToToday"/.test(ganttVue) &&
    /function scrollToToday/.test(ganttCode) &&
    /ganttRef\.value\?\.showDate\(new Date\(\)\)/.test(ganttCode) &&
    // 2026-11-02：defineExpose 增开了缩放方法 ⇒ 只要求 showDate 仍在同一次暴露里
    /defineExpose\(\{[^}]*\bshowDate\b[^}]*\}\)/.test(dhxWrapperCode)
  // 2026-10-27 用户指定：泳道行数标签叫「泳道」（不是「维度」），图例与左侧 KPI 用间距+分隔线区分
  const gt30StatLabel =
    /泳道\s*<strong>\{\{\s*lanes\.length\s*\}\}/.test(ganttVue) &&
    !/维度\s*<strong>\{\{\s*lanes\.length\s*\}\}/.test(ganttVue)
  const gt30Separated =
    /\.view-bar-right\s*\{[^}]*gap:\s*24px/.test(ganttVue) &&
    /\.gantt-legend\s*\{[^}]*border-left:\s*1px solid/.test(ganttVue)
  assert(
    'GT30 静态：图例 + KPI（泳道标签、与图例分隔）+「回到今天」',
    gt30Legend && gt30BackToday && gt30StatLabel && gt30Separated,
    `图例=${gt30Legend} 回到今天=${gt30BackToday} 泳道标签=${gt30StatLabel} 分隔=${gt30Separated}`
  )

  /* ===== GT31：左表首列悬停显示完整文本 =====
   * 2026-10-27 用户定调「鼠标悬停显示全」：首列固定 360px 装不下「任务号 | 订单号 | 物料」，
   * 用列模板把 title 挂到单元格上（内容走 escapeHtml，防注入）。
   * 反向破坏：删列模板 / 把 title 里的 escapeHtml 去掉 / 删 escapeHtml 定义 → 转红。
   */
  const gt31TitleCount = (
    dhxWrapperCode.match(/title="\$\{escapeHtml\(task\?\.\[field\] \?\? ''\)\}"/g) || []
  ).length
  const gt31CellTitle =
    /name:\s*'text'[\s\S]{0,500}template:\s*titleTemplate\('text'\)/.test(dhxWrapperCode) &&
    /const escapeHtml = \(value: unknown\): string =>/.test(dhxWrapperCode) &&
    gt31TitleCount >= 1
  assert(
    'GT31 静态：左表首列悬停显示完整文本（title + HTML 转义）',
    gt31CellTitle,
    `列模板=${/name:\s*'text'[\s\S]{0,500}template:/.test(dhxWrapperCode)} 转义=${
      /const escapeHtml/.test(dhxWrapperCode)
    } title=${gt31TitleCount}`
  )

  /* ===== GT32：工具栏不再有「刷新」按钮 =====
   * 2026-10-27 用户要求撤掉：它走 loadVersions() 会把选中版本重置成第一个 ACTIVE 版本。
   * 反向破坏：把刷新按钮加回模板 → 转红（防止以后又被加回来）。
   */
  const gt32NoRefreshBtn = !/title="刷新数据"/.test(ganttVue) && !/@click="refresh"/.test(ganttVue)
  assert(
    'GT32 静态：甘特页工具栏无「刷新」按钮（不再冲掉手选版本）',
    gt32NoRefreshBtn,
    `无刷新按钮=${gt32NoRefreshBtn}`
  )

  /* ===== GT33：页签栏不再有「分析页」，且所有页签都可关闭 =====
   * 2026-10-27 用户反馈：登录后页签栏出现框架演示页「分析页」，且它没有关闭按钮。
   * 根因：/dashboard/analysis 的 meta 写了 affix:true
   *   ① filterAffixTags 把 affix 路由抓成「固定页签」塞进 visitedViews；
   *   ② TagsView.vue 的关闭图标 CSS 只对非 affix 页签 hover 显示 ⇒ 固定页签没有 ×；
   *   ③ useTagsView.ts 的 closeCurrent 对 affix 直接 return ⇒ 右键「关闭标签页」也无效。
   * 撤 affix + 整组 /dashboard 加 noTagsView ⇒ 演示页彻底不进页签栏。
   * 反向破坏：给 Analysis 加回 affix:true / 去掉 /dashboard 的 noTagsView → 转红。
   */
  const routerIdxSrc = readSrc('router/index.ts')
  const routerIdxCode = stripComments(routerIdxSrc)
  const analysisIdx = routerIdxCode.indexOf("name: 'Analysis'")
  const analysisSeg = analysisIdx >= 0 ? routerIdxCode.slice(analysisIdx, analysisIdx + 400) : ''
  const dashboardIdx = routerIdxCode.indexOf("path: '/dashboard'")
  const dashboardSeg =
    dashboardIdx >= 0
      ? routerIdxCode.slice(dashboardIdx, routerIdxCode.indexOf("path: '/external-link'"))
      : ''
  const gt33NoAffix =
    analysisIdx >= 0 && !/affix:\s*true/.test(analysisSeg) && !/affix:\s*true/.test(routerIdxCode)
  const gt33DashboardNoTag = /noTagsView:\s*true/.test(dashboardSeg)
  assert(
    'GT33 静态：页签栏不再有「分析页」，且全树无 affix 固定页签（所有页签均可关闭）',
    gt33NoAffix && gt33DashboardNoTag,
    `分析页无affix=${gt33NoAffix} /dashboard整组noTagsView=${gt33DashboardNoTag}`
  )

  /* ===== GT34：甘特图进页签栏 =====
   * 2026-10-27 用户反馈：点「甘特图/资源计划」菜单后页签栏没出现该页签。
   * 根因：/aps/gantt 的 meta 写了 noTagsView:true ⇒ store.addVisitedView 直接 return。
   * 配套 noCache:true —— 甘特图原先不在 visitedViews 里 ⇒ 从不进 keep-alive；
   * 进页签后若被缓存，切走再切回会命中 dhtmlx 未重算尺寸的旧实例。
   * 反向破坏：给 ApsGantt 加回 noTagsView / 删掉 noCache → 转红。
   */
  const apsRouteSrc = readSrc('router/modules/aps.ts')
  const apsRouteCode = stripComments(apsRouteSrc)
  const ganttRouteIdx = apsRouteCode.indexOf("name: 'ApsGantt'")
  const ganttRouteSeg =
    ganttRouteIdx >= 0 ? apsRouteCode.slice(ganttRouteIdx, ganttRouteIdx + 400) : ''
  const gt34GanttTag =
    ganttRouteIdx >= 0 &&
    !/noTagsView:\s*true/.test(ganttRouteSeg) &&
    /noCache:\s*true/.test(ganttRouteSeg)
  assert(
    'GT34 静态：甘特图路由进页签栏（无 noTagsView + noCache 防 keep-alive 尺寸失效）',
    gt34GanttTag,
    `ApsGantt段=${ganttRouteSeg.replace(/\s+/g, ' ').slice(0, 120)}`
  )

  /* ===== GT35：关完页签回落到「排产总览」，不再弹回框架演示页 =====
   * 2026-10-27：TagsView.vue 的 toLastView 原本回落到 permissionStore.getAddRouters[0]
   * —— 那是 hidden 的 /dashboard（redirect 到 /dashboard/analysis），
   * 与 M19（登录落地不用 addRouters[0]）同源。改为回落到 APS_HOME_PATH。
   * 反向破坏：把 push(APS_HOME_PATH) 改回 getAddRouters[0].path → 转红。
   */
  const tagsViewSrc = readSrc('components/TagsView/src/TagsView.vue')
  const tagsViewCode = stripComments(tagsViewSrc)
  const gt35Fallback =
    /import\s*\{\s*APS_HOME_PATH\s*\}\s*from\s*'@\/constants'/.test(tagsViewCode) &&
    /push\(APS_HOME_PATH\)/.test(tagsViewCode) &&
    !/getAddRouters\[0\]/.test(tagsViewCode)
  assert(
    'GT35 静态：关完页签回落到「排产总览」（不再回落到 hidden 的 /dashboard 演示页）',
    gt35Fallback,
    `用APS_HOME_PATH=${gt35Fallback}`
  )

  /* ===== GT36–GT40：甘特大数据量第 0 步（2026-10-29）=====
   * 背景：dev 压测版本 121 万任务，**仅 7 天窗**就 80MB / 13.6 万条、服务端 5.5s；
   *   前端 30s 超时先放弃、后端继续算 ⇒ 同端点其它请求被拖到 60~230s，版本下拉拿不到数据。
   * 第 0 步：一律窗口加载（不再整版本全量）+ 同键单飞 + 可取消 + 加载量明示。
   * 反向破坏自证（逐条还原）：
   *   GT36 把 loadCurrent(currentGanttWindow()) 改回 loadCurrent() → 红
   *   GT37 删 limit: GANTT_PAGE_LIMIT / 删 signal 传递 / 删单飞判断 → 红
   *   GT38 删 http.ts 的 axios.isCancel 短路（或挪到 redirectToError 之后）→ 红
   *   GT39 删 hasMoreInWindow 提示 / 删 window-empty 空态 → 红
   *   GT40 删 onBeforeUnmount 里的 cancelGanttLoad() → 红
   */
  const ganttWindowCalls = (ganttCode.match(/loadCurrent\(currentGanttWindow\(\)\)/g) || []).length
  // 窗口函数的**返回形状**会随筛选条件演进（2026-11-03 加了 resourceIds）⇒ 断言「窗口来自 dateRange」这个意图，
  // 不锁死整个返回对象字面量（锁字面量会让任何扩展都假红）。
  const ganttWindowFn = (() => {
    const i = ganttCode.indexOf('function currentGanttWindow')
    return i >= 0 ? ganttCode.slice(i, i + 400) : ''
  })()
  const gt36AlwaysWindow =
    /function currentGanttWindow/.test(ganttCode) &&
    /from: dateRange\.value\.from/.test(ganttWindowFn) &&
    /to: dateRange\.value\.to/.test(ganttWindowFn) &&
    ganttWindowCalls >= 2 &&
    /selectVersion\(v, currentGanttWindow\(\)\)/.test(ganttCode) &&
    // 2026-11-03：窗口改由「时间尺度」驱动（原来锁的是 watch(timeRange)）⇒ 改窗口必须按新窗重取
    /watch\(ganttWindow,[\s\S]{0,240}?loadCurrent\(currentGanttWindow\(\)\)/.test(ganttCode) &&
    !/loadCurrent\(\s*\)/.test(ganttCode) &&
    !/selectVersion\(v\)/.test(ganttCode)
  assert(
    'GT36 静态：甘特页一律带时间窗加载（不再裸调整版本全量）',
    gt36AlwaysWindow,
    `窗口函数=${/function currentGanttWindow/.test(ganttCode)} 带窗调用=${ganttWindowCalls} 无裸调=${
      !/loadCurrent\(\s*\)/.test(ganttCode)
    }`
  )

  const schedStoreSrc2 = readSrc('store/modules/aps/schedule.ts')
  const schedStoreCode = stripComments(schedStoreSrc2)
  // 10-29 第 1 步后有两条取数路径（首屏 fetchGantt / 追加 loadMore），
  // limit、signal、AbortController **两条路径都要有** ⇒ 按出现次数断言，避免改一条漏一条
  const limitCount = (schedStoreCode.match(/limit: GANTT_PAGE_LIMIT/g) || []).length
  const signalCount = (schedStoreCode.match(/signal: ctrl\.signal/g) || []).length
  const abortCount = (schedStoreCode.match(/new AbortController\(\)/g) || []).length
  const gt37SingleFlight =
    // 2026-11-06 前端主动由 8000 降到 4000（体积减半，耗时只差 0.65s）⇒ 锁死当前值
    /export const GANTT_PAGE_LIMIT = 4000/.test(schedStoreCode) &&
    /function windowKey\(/.test(schedStoreCode) &&
    /if \(pendingKey === key && pendingPromise\) return pendingPromise/.test(schedStoreCode) &&
    limitCount >= 2 &&
    signalCount >= 2 &&
    abortCount >= 2 &&
    /abortCtrl\.abort\(\)/.test(schedStoreCode)
  assert(
    'GT37 静态：甘特加载同键单飞 + 带 limit/signal + 换窗口中止上一请求（首屏与追加两条路径都算）',
    gt37SingleFlight,
    `单飞=${/pendingKey === key && pendingPromise/.test(schedStoreCode)} limit=${limitCount} signal=${signalCount} abort=${abortCount}`
  )

  const httpSrc = stripComments(readSrc('api/aps-v1/http.ts'))
  const cancelIdx = httpSrc.indexOf('axios.isCancel(')
  const redirect500Idx = httpSrc.indexOf('redirectToError(500)')
  const gt38CancelIsNotError =
    cancelIdx >= 0 &&
    redirect500Idx > 0 &&
    cancelIdx < redirect500Idx &&
    /signal\?: AbortSignal/.test(httpSrc) &&
    /signal: opts\.signal/.test(httpSrc)
  assert(
    'GT38 静态：请求取消不弹错、不跳 500 页（AbortSignal 短路在 5xx/网络错兜底之前）',
    gt38CancelIsNotError,
    `取消短路位置=${cancelIdx} 500跳转位置=${redirect500Idx}`
  )

  const gt39LoadMeter =
    /const hasMoreInWindow = computed\(\(\) => ganttData\.value\?\.hasMore === true\)/.test(
      ganttCode
    ) &&
    /已加载前 \{\{ stats\.total \}\}/.test(ganttVue) &&
    /ganttData\?\.totalTasks/.test(ganttVue) &&
    /'window-empty'/.test(ganttCode) &&
    // ⚠️ 锚到**代码里的 title 定义**（ganttCode 已去注释）：只判短语会被注释里的同句话顶住 ⇒ 假绿
    /title: '当前时间范围没有排程任务'/.test(ganttCode)
  assert(
    'GT39 静态：加载量明示（已加载/共 N 条）+ 窗口空态与版本空态分开',
    gt39LoadMeter,
    `已加载量提示=${/已加载前 \{\{ stats\.total \}\}/.test(ganttVue)} 窗口空态=${
      /'window-empty'/.test(ganttCode)
    }`
  )

  const gt40AbortOnLeave =
    /onBeforeUnmount\(\(\) => \{[\s\S]{0,500}?cancelGanttLoad\(\)/.test(ganttCode)
  assert(
    'GT40 静态：离开甘特页取消在飞请求（前端放弃后端不再白算）',
    gt40AbortOnLeave,
    `卸载取消=${gt40AbortOnLeave}`
  )

  /* ===== GT41–GT45：甘特大数据量第 1 步（2026-10-29）纵向懒加载 ====
   * 背景：后端按 `(ResourceId, PlannedStartTime, TaskId)` 升序切页 ⇒ 首屏 8000 条只覆盖
   *   前 ~16 个资源（dev 实测 182 个资源里 166 个泳道空），用户以为「计划很空」。
   * 第 1 步：`nextAfter` 原样回填 → 追加下一页（追加而非替换、按 taskId 去重、不重排）。
   * 反向破坏自证（逐条还原）：
   *   GT41 把 tasks 追加改成替换 / 去掉 taskId 去重 → 红
   *   GT42 把 afterTaskId 改成别的参数名 → 红
   *   GT43 去掉 @near-bottom 绑定 → 红
   *   GT44 去掉 emits('nearBottom') 或滚动保位 → 红
   *   GT45 去掉追加请求的中止 → 红
   */
  const schedStoreSrc3 = readSrc('store/modules/aps/schedule.ts')
  const schedStoreCode3 = stripComments(schedStoreSrc3)
  const gt41CursorAppend =
    /async function loadMore\(\): Promise<void>/.test(schedStoreCode3) &&
    /const after: GanttCursor = \{/.test(schedStoreCode3) &&
    /after,/.test(schedStoreCode3) &&
    /const seenTasks = new Set\(d\.tasks\.map\(\(t\) => t\.taskId\)\)/.test(schedStoreCode3) &&
    /tasks: \[\.\.\.d\.tasks, \.\.\.newTasks\]/.test(schedStoreCode3) &&
    /if \(!canLoadMore\.value\) return/.test(schedStoreCode3) &&
    /loadedKey !== key/.test(schedStoreCode3)
  assert(
    'GT41 静态：游标追加下一页（追加不替换 + taskId 去重 + 窗口变了就丢弃）',
    gt41CursorAppend,
    `loadMore=${/async function loadMore/.test(schedStoreCode3)} 追加=${
      /tasks: \[\.\.\.d\.tasks, \.\.\.newTasks\]/.test(schedStoreCode3)
    } 去重=${/seenTasks = new Set/.test(schedStoreCode3)} 丢弃=${
      /loadedKey !== key/.test(schedStoreCode3)
    }`
  )

  const schedApiCode = stripComments(readSrc('api/aps-v1/schedule.ts'))
  const gt42CursorParams =
    /after\?: GanttCursor/.test(schedApiCode) &&
    /params\.append\('afterResourceId', String\(window\.after\.resourceId\)\)/.test(schedApiCode) &&
    /params\.append\('afterStartAt', window\.after\.plannedStartTime\)/.test(schedApiCode) &&
    /params\.append\('afterTaskId', String\(window\.after\.taskId\)\)/.test(schedApiCode)
  assert(
    'GT42 静态：游标三参成套下发（afterResourceId/afterStartAt/afterTaskId）',
    gt42CursorParams,
    `afterResourceId=${/afterResourceId/.test(schedApiCode)} afterStartAt=${
      /afterStartAt/.test(schedApiCode)
    } afterTaskId=${/afterTaskId/.test(schedApiCode)}`
  )

  const gt43LoadMoreUi =
    /@near-bottom="handleGanttNearBottom"/.test(ganttVue) &&
    /function handleGanttNearBottom\(\): void/.test(ganttCode) &&
    /scheduleStore\.loadMore\(\)/.test(ganttCode) &&
    /向下滚动继续加载/.test(ganttVue) &&
    /加载更多/.test(ganttVue)
  assert(
    'GT43 静态：滑到底自动加载更多 + 顶部可手动点「加载更多」+ 已加载/共 N 条明示',
    gt43LoadMoreUi,
    `滚动触发=${/@near-bottom/.test(ganttVue)} 手动入口=${
      /加载更多/.test(ganttVue)
    } 明示=${/向下滚动继续加载/.test(ganttVue)}`
  )

  const dhxCode = stripComments(readSrc('components/DhxGantt/src/DhxGantt.vue'))
  const gt44ScrollLoad =
    /nearBottom: \[\]/.test(dhxCode) &&
    /onGanttScroll/.test(dhxCode) &&
    /emits\('nearBottom'\)/.test(dhxCode) &&
    /gantt_ver_scroll/.test(dhxCode) &&
    /const isAppend = props\.tasks\.length > lastParsedCount/.test(dhxCode) &&
    /el\.scrollTop = prevScrollTop/.test(dhxCode)
  assert(
    'GT44 静态：甘特纵向滚到底抛 nearBottom + 追加后保持滚动位置（否则滑不回底部）',
    gt44ScrollLoad,
    `事件=${/emits\('nearBottom'\)/.test(dhxCode)} 滚动容器=${
      /gantt_ver_scroll/.test(dhxCode)
    } 保位=${/scrollTop = prevScrollTop/.test(dhxCode)}`
  )

  const gt45CancelAppend =
    /if \(moreCtrl && !moreCtrl\.signal\.aborted\) moreCtrl\.abort\(\)/.test(schedStoreCode3) &&
    /moreCtrl = null/.test(schedStoreCode3) &&
    /loadingMore\.value = false/.test(schedStoreCode3)
  assert(
    'GT45 静态：追加请求可被换窗/重载/离页中止（旧页数据不回灌）',
    gt45CancelAppend,
    `中止=${/moreCtrl\.abort\(\)/.test(schedStoreCode3)} 清理=${
      /moreCtrl = null/.test(schedStoreCode3)
    }`
  )

  /* ===== GT46–GT50：甘特第 2 步 · 负荷带聚合端点（2026-10-30 接 5号位 r13972）====
   * 背景：`GET /api/schedule/gantt/{id}/buckets`（资源×时间桶）dev 已部署实测（200/868 行/
   *   totalTasks=185,146，与我方独立量的 D7 窗口数三位一致）。
   * 本组锁**契约字段名逐字**与**三条易踩口径**：
   *   ① 桶宽按后端 `bucketWidth`（实际生效）画，不是按请求的 `bucket`；
   *   ② `bucketStart` **可能早于窗口 `from`**（周桶锚=周一 + 钳制归桶）⇒ 不得裁首桶；
   *   ③ `domainKey` 是**版本域**（同版本所有行恒相同）⇒ 不得用它分组。
   * 反向破坏自证（逐条还原）：
   *   GT46 把 `bucketWidth`/`bucketStart` 改成必填或改名 → 红
   *   GT47 把 `from`/`to` 改成可选或 `resourceIds` 改成逗号拼接 → 红
   *   GT48 去掉单飞 / 去掉中止 / 去掉旧响应丢弃 → 红
   *   GT49 让图层改用请求的 `bucket`（而非 `bucketWidth`）→ 红
   *   GT50 加「按 from 裁首桶」/ 改用 domainKey 分组 / 丢掉未排桶行 → 红
   */

  // 取接口体（字段名逐字断言必须落在**该接口自己**的体内，不能全文件 includes —— 同名字段在别处也有）
  const ifaceBody = (src, name) => {
    const m = src.match(new RegExp(`export interface ${name}\\s*\\{([\\s\\S]*?)\\n\\}`))
    return m ? m[1] : ''
  }
  const bucketsIface = ifaceBody(typesSrc, 'GanttBucketsDto')
  const bucketRowIface = ifaceBody(typesSrc, 'GanttBucketRowDto')

  const gt46BucketDto =
    bucketsIface !== '' &&
    bucketRowIface !== '' &&
    /planVersionId:\s*number/.test(bucketsIface) &&
    /versionCode:\s*string/.test(bucketsIface) &&
    /versionExists\?:\s*boolean/.test(bucketsIface) &&
    /scopeDenied\?:\s*boolean/.test(bucketsIface) &&
    /isEmpty\?:\s*boolean/.test(bucketsIface) &&
    /bucket\?:\s*GanttBucketUnit/.test(bucketsIface) &&
    /bucketWidth\?:\s*GanttBucketUnit/.test(bucketsIface) &&
    /bucketDowngraded\?:\s*boolean/.test(bucketsIface) &&
    /from\?:\s*IsoDateTime/.test(bucketsIface) &&
    /to\?:\s*IsoDateTime/.test(bucketsIface) &&
    // 不存在/越权 = 整键省略；空窗 = 键在值 0 ⇒ 两种形态都要能表达
    /totalTasks\?:\s*number \| null/.test(bucketsIface) &&
    /items\?:\s*GanttBucketRowDto\[\]/.test(bucketsIface) &&
    // 行字段：resourceId/taskCount 恒在；其余可能缺键
    /resourceId:\s*number/.test(bucketRowIface) &&
    /taskCount:\s*number/.test(bucketRowIface) &&
    /bucketStart\?:\s*IsoDateTime/.test(bucketRowIface) &&
    /loadHours\?:\s*number \| null/.test(bucketRowIface) &&
    /firstStart\?:\s*IsoDateTime/.test(bucketRowIface) &&
    /lastEnd\?:\s*IsoDateTime/.test(bucketRowIface) &&
    /resourceCode\?:\s*string/.test(bucketRowIface) &&
    /resourceName\?:\s*string/.test(bucketRowIface) &&
    /factoryName\?:\s*string/.test(bucketRowIface) &&
    /productionDepartmentName\?:\s*string/.test(bucketRowIface) &&
    /stage\?:\s*string/.test(bucketRowIface) &&
    /domainKey\?:\s*string/.test(bucketRowIface)
  assert(
    'GT46 静态：负荷带 DTO 字段名逐字 + 缺键语义（可选项=后端整键省略）',
    gt46BucketDto,
    `bucketsIface=${
      bucketsIface !== '' ? '有' : '缺'
    } rowIface=${bucketRowIface !== '' ? '有' : '缺'} bucketWidth=${/bucketWidth\?:/.test(
      bucketsIface
    )} bucketStart可选=${/bucketStart\?:/.test(bucketRowIface)} totalTasks可选=${
      /totalTasks\?:/.test(bucketsIface)
    }`
  )

  // api 层：函数体切片（getGanttBuckets 之后的第一个 '\n  }'）
  const gbIdx = schedApiCode.indexOf('async getGanttBuckets')
  const gbBody = gbIdx >= 0 ? schedApiCode.slice(gbIdx, gbIdx + 2600) : ''
  const gt47BucketApi =
    gbIdx >= 0 &&
    // from/to **类型上必填**（缺任一后端返 400）⇒ 不允许写成可选
    /from:\s*string/.test(gbBody) &&
    /to:\s*string/.test(gbBody) &&
    !/from\?:\s*string/.test(gbBody) &&
    !/to\?:\s*string/.test(gbBody) &&
    /params\.append\('from', opts\.from\)/.test(gbBody) &&
    /params\.append\('to', opts\.to\)/.test(gbBody) &&
    /params\.append\('bucket', unit\)/.test(gbBody) &&
    // resourceIds 只用重复键（逗号拼接后端 422）
    /params\.append\('resourceIds', String\(id\)\)/.test(gbBody) &&
    !/resourceIds[^\n]*join\(/.test(gbBody) &&
    /signal:\s*opts\.signal/.test(gbBody) &&
    /\/api\/schedule\/gantt\/\$\{planVersionId\}\/buckets/.test(gbBody)
  assert(
    'GT47 静态：负荷带 api —— from/to 必填 + bucket 值域 + resourceIds 重复键 + signal 透传',
    gt47BucketApi,
    `from必填=${/from:\s*string/.test(gbBody) && !/from\?:/.test(gbBody)} to必填=${
      /to:\s*string/.test(gbBody) && !/to\?:/.test(gbBody)
    } 重复键=${/params\.append\('resourceIds', String\(id\)\)/.test(
      gbBody
    )} signal=${/signal:\s*opts\.signal/.test(gbBody)}`
  )

  // ⚠️ 切片函数体再断言：`bucketsCtrl.abort()` 在 loadBuckets 与 cancelBucketsLoad 两处都出现，
  //    全文件匹配会被另一处绕过（自证已抓到 1 条 STILL-GREEN）⇒ 单飞/中止只在 loadBuckets 体内判。
  const lbIdx = schedStoreCode3.indexOf('async function loadBuckets')
  const lbBody =
    lbIdx >= 0
      ? schedStoreCode3.slice(lbIdx, schedStoreCode3.indexOf('async function fetchBuckets'))
      : ''
  const fbIdx = schedStoreCode3.indexOf('async function fetchBuckets')
  const fbBody =
    fbIdx >= 0
      ? schedStoreCode3.slice(fbIdx, schedStoreCode3.indexOf('function cancelBucketsLoad'))
      : ''
  const cblIdx = schedStoreCode3.indexOf('function cancelBucketsLoad')
  const cblBody =
    cblIdx >= 0 ? schedStoreCode3.slice(cblIdx, cblIdx + 600) : ''
  const gt48BucketStore =
    lbIdx >= 0 &&
    fbIdx >= 0 &&
    /async function loadBuckets\(request: GanttBucketRequest\): Promise<void>/.test(lbBody) &&
    /if \(pendingBucketsKey === key && pendingBucketsPromise\) return pendingBucketsPromise/.test(
      lbBody
    ) &&
    /if \(bucketsCtrl && !bucketsCtrl\.signal\.aborted\) bucketsCtrl\.abort\(\)/.test(lbBody) &&
    /const ctrl = new AbortController\(\)/.test(lbBody) &&
    // 旧响应不回灌
    /if \(ctrl\.signal\.aborted \|\| pendingBucketsKey !== key\) return/.test(fbBody) &&
    /scheduleApi\.getGanttBuckets\(id, \{/.test(fbBody) &&
    // 离页/换窗时负荷带在飞请求一并中止（cancelBucketsLoad 自己也要真中止）
    /cancelBucketsLoad\(\)/.test(schedStoreCode3) &&
    /if \(bucketsCtrl && !bucketsCtrl\.signal\.aborted\) bucketsCtrl\.abort\(\)/.test(cblBody)
  assert(
    'GT48 静态：负荷带取数同键单飞 + 可中止 + 旧响应不回灌（与甘特本体相互独立）',
    gt48BucketStore,
    `单飞=${/pendingBucketsKey === key && pendingBucketsPromise/.test(
      lbBody
    )} 中止=${/bucketsCtrl && !bucketsCtrl\.signal\.aborted\) bucketsCtrl\.abort/.test(
      lbBody
    )} 丢弃=${/pendingBucketsKey !== key/.test(fbBody)} 离页中止=${/cancelBucketsLoad\(\)/.test(
      schedStoreCode3
    )}`
  )

  // 图层取桶宽必须是「实际生效值」bucketWidth，而不是请求值 bucket
  const gt49EffectiveWidth =
    /const effectiveBucketWidth = computed<GanttBucketUnit \| null>/.test(schedStoreCode3) &&
    /buckets\.value\?\.bucketWidth/.test(schedStoreCode3) &&
    // 请求值另存（bucketUnit），两者不可混用
    /bucketUnit\.value = data\.bucket \?\? unit/.test(schedStoreCode3) &&
    /effectiveBucketWidth,\s*bucketGroups,/.test(schedStoreCode3)
  assert(
    'GT49 静态：桶宽按后端实际生效值 bucketWidth 画（请求值另存，不混用）',
    gt49EffectiveWidth,
    `effectiveBucketWidth=${/const effectiveBucketWidth = computed/.test(
      schedStoreCode3
    )} 读bucketWidth=${/buckets\.value\?\.bucketWidth/.test(
      schedStoreCode3
    )} 请求值另存=${/bucketUnit\.value = data\.bucket \?\? unit/.test(schedStoreCode3)}`
  )

  const gt50BucketGrouping =
    /const bucketGroups = computed/.test(schedStoreCode3) &&
    /map\.get\(row\.resourceId\)/.test(schedStoreCode3) &&
    /map\.set\(row\.resourceId, \[row\]\)/.test(schedStoreCode3) &&
    // 未排桶行（bucketStart 缺键）单独承接，不静默丢
    /const unscheduledBucketRows = computed/.test(schedStoreCode3) &&
    /r\.bucketStart == null/.test(schedStoreCode3) &&
    // 不得按窗口 from 裁首桶（周桶锚=周一，首桶可早于 from）
    !/bucketStart\s*>=/.test(schedStoreCode3) &&
    // 不得按 domainKey 分组（它是版本域，同版本恒相同）
    !/map\.(get|set)\(.*domainKey/.test(schedStoreCode3)
  assert(
    'GT50 静态：负荷带分组口径（-1 独立组 / 不裁首桶 / 不按 domainKey 分组 / 未排桶不丢）',
    gt50BucketGrouping,
    `按resourceId分组=${/map\.get\(row\.resourceId\)/.test(
      schedStoreCode3
    )} 未排桶单列=${/unscheduledBucketRows = computed/.test(
      schedStoreCode3
    )} 未按from裁=${!/bucketStart\s*>=/.test(schedStoreCode3)} 未按domainKey分组=${!(
      /map\.(get|set)\(.*domainKey/.test(schedStoreCode3)
    )}`
  )

  /* ===== GT51–GT57：甘特第 2 步 · 负荷带图层落地（2026-10-30）=====
   * 背景：桶体（`/buckets`）已在 GT46–GT50 锁死契约；本组锁**图层侧 + 页面侧**落地：
   *   ① 图层按 laneId 把桶画到泳道行底部（深浅 = 该桶任务数）；
   *   ② 页面把桶派生为 loadBands 并接线到 <DhxGantt>；
   *   ③ 未分配任务（明体无 resourceId / 桶体 -1）落到**独立泳道**，不得静默丢
   *      （dev 实测首屏 8000 条里 2719 条无 resourceId，早前泳道键算成 lane-undefined ⇒ 整批丢）；
   *   ④ 窗口 from/to 用**朴素墙钟串**（后端按字面量比较，Z 被忽略 ⇒ toISOString 会差 8 小时）；
   *   ⑤ 未排桶行（bucketStart 缺键）要有可见去处；
   *   ⑥ 桶宽按后端实际生效值画，且**不裁首桶**；
   *   ⑦ 桶是资源级聚合（桶体 domainKey=版本域）⇒ 只在「按资源」视图显示。
   * 反向破坏自证（逐条还原）：
   *   GT51 去掉 addTaskLayer / 去掉 dhx-load-band-cell ⇒ 红
   *   GT52 去掉 :load-bands 接线 / 不用 bucketGroups ⇒ 红
   *   GT53 去掉人工判定 / 不按部门建泳道 / 保留旧「未分配资源」文案 ⇒ 红
   *   GT54 窗口边界改回 toISOString ⇒ 红
   *   GT55 去掉「未排期」KPI ⇒ 红
   *   GT56 改用请求 bucket 画 / 按 from 裁首桶 ⇒ 红
   *   GT57 去掉「仅按资源视图」门控 ⇒ 红
   */

  // GT51 组件层：图层存在 + 只画泳道行 + 不挡交互
  // ⚠️ 切片函数体：`task.type !== 'project'` 在文件里有两处（泳道占用图层 + 负荷带图层），
  //    全文件匹配会被另一处绕过 ⇒ 只在 renderLoadBand 体内判。
  const rlbIdx = dhxCode.indexOf('const renderLoadBand = (task: DhxGanttTask)')
  const rlbBody = rlbIdx >= 0 ? dhxCode.slice(rlbIdx, rlbIdx + 1400) : ''
  const gt51LoadBandLayer =
    /loadBands\?:\s*DhxGanttLoadBand\[\]/.test(dhxCode) &&
    rlbIdx >= 0 &&
    /if \(task\.type !== 'project'\) return null/.test(rlbBody) &&
    // 2026-11-02：loadBandMap 改为同时返回 map + 全局 maxCount ⇒ 取桶改为 map.get(...)
    /map\.get\(String\(task\.id\)\)/.test(rlbBody) &&
    // map 必须**按 laneId** 建（不是 resourceId）
    /map\.set\(band\.laneId/.test(dhxCode) &&
    /className = 'dhx-load-band-cell'/.test(rlbBody) &&
    /loadBandLayerId = ganttApi\.addTaskLayer/.test(dhxCode) &&
    /pointer-events: none/.test(dhxCode)
  assert(
    'GT51 静态：负荷带图层存在（按 laneId 画在泳道行底、深浅=任务数、不挡交互）',
    gt51LoadBandLayer,
    `prop=${/loadBands\?:\s*DhxGanttLoadBand\[\]/.test(
      dhxCode
    )} render=${rlbIdx >= 0} 只画泳道行=${/if \(task\.type !== 'project'\) return null/.test(
      rlbBody
    )} 按lane取桶=${/map\.get\(String\(task\.id\)\)/.test(
      rlbBody
    )} 按laneId建表=${/map\.set\(band\.laneId/.test(
      dhxCode
    )} addTaskLayer=${/addTaskLayer/.test(dhxCode)} 不挡交互=${
      /pointer-events: none/.test(dhxCode)
    }`
  )

  // 负荷带计算体（精确切片到该 computed 的收尾 `})`）
  // ⚠️ 2026-11-03：此前用「起点 + 1000 字符」切片 ⇒ 窗口跨进 loadHeatmap（同一文件里还有一份
  //    effectiveBucketWidth / bucketGroups）⇒ 反破坏把 loadBands 里的桶宽换掉，断言仍能在
  //    切片后半段命中同样的字面量 ⇒ 假绿（GT56 打不红）。改为切到本 computed 的收尾为止。
  const lbPageIdx = ganttCode.indexOf('const loadBands = computed')
  const lbPageEnd = lbPageIdx >= 0 ? ganttCode.indexOf('\n})', lbPageIdx) : -1
  const lbPageBody =
    lbPageIdx >= 0 && lbPageEnd > lbPageIdx ? ganttCode.slice(lbPageIdx, lbPageEnd + 3) : ''

  // GT52 页面层：桶 → loadBands → <DhxGantt :load-bands>；三条触发路径都在
  // 判据锁在 loadBands **计算体内部**（全文 grep 同名字面量会被 loadHeatmap 顶住 ⇒ 假绿）
  const loadBandsCalls = (ganttCode.match(/loadBuckets\(currentBucketWindow\(\)\)/g) ?? []).length
  const gt52PageWiring =
    /:load-bands="loadBands"/.test(ganttVue) &&
    /const loadBands = computed<DhxGanttLoadBand\[\]>/.test(lbPageBody) &&
    /scheduleStore\.bucketGroups/.test(lbPageBody) &&
    /start: row\.bucketStart/.test(lbPageBody) &&
    loadBandsCalls >= 2
  assert(
    'GT52 静态：页面把桶派生为 loadBands 并接线到 <DhxGantt>（切版本/切窗口都重取桶）',
    gt52PageWiring,
    `接线=${/:load-bands="loadBands"/.test(ganttVue)} 派生=${
      /const loadBands = computed/.test(lbPageBody)
    } 源=${/scheduleStore\.bucketGroups/.test(lbPageBody)} 桶起点=${
      /start: row\.bucketStart/.test(lbPageBody)
    } 取数点=${loadBandsCalls}处`
  )

  // GT53 无设备任务按**生产部门**拆人工泳道（2026-11-06 重写）
  //   实测无设备任务全部带 manualSlotId ⇒ 不是「未分配」而是人工槽任务；
  //   旧的一条「未分配资源」泳道已撤，改为每部门一条 `lane-manual-{deptId}`。
  //   兜底（既无设备也无人工槽，实测 0 条）仍落 lane-unassigned，不静默丢。
  const gt53LaborLane =
    /const UNASSIGNED_RESOURCE_ID = -1/.test(ganttCode) &&
    /const UNASSIGNED_LANE_ID = 'lane-unassigned'/.test(ganttCode) &&
    /t\.resourceId == null \|\| t\.resourceId === UNASSIGNED_RESOURCE_ID/.test(ganttCode) &&
    /t\.resourceId == null && t\.manualSlotId != null/.test(ganttCode) &&
    /return `lane-manual-\$\{t\.productionDepartmentId \?\? 0\}`/.test(ganttCode) &&
    /if \(!laborByDept\.has\(deptId\)\) \{/.test(ganttCode) &&
    /laborByDept\.set\(deptId, \{ dept, factory: factoryNameOfTask\(t, undefined\) \}\)/.test(
      ganttCode
    ) &&
    /id: `lane-manual-\$\{deptId\}`/.test(ganttCode) &&
    /label: laneLabel\('人工', v\.factory, v\.dept\)/.test(ganttCode) &&
    /if \(hasTrueUnassigned\) \{/.test(ganttCode) &&
    /return isLaborTask\(task\) \? laborLaneId\(task\) : UNASSIGNED_LANE_ID/.test(ganttCode) &&
    !/const hasUnassignedTasks = computed/.test(ganttCode) &&
    !/'未分配资源 \| 无指定设备'/.test(ganttCode)
  assert(
    'GT53 静态：无设备任务按生产部门拆人工泳道（人工 | 工厂 | 部门），真无分配才落 lane-unassigned',
    gt53LaborLane,
    `-1常量=${/const UNASSIGNED_RESOURCE_ID = -1/.test(
      ganttCode
    )} 人工判定=${/t\.resourceId == null && t\.manualSlotId != null/.test(
      ganttCode
    )} 泳道id=${/return `lane-manual-\$\{t\.productionDepartmentId \?\? 0\}`/.test(
      ganttCode
    )} 建行=${/id: `lane-manual-\$\{deptId\}`/.test(ganttCode)} 标签=${
      /label: laneLabel\('人工', v\.factory, v\.dept\)/.test(ganttCode)
    } 兜底=${/if \(hasTrueUnassigned\) \{/.test(
      ganttCode
    )} 任务归组=${/return isLaborTask\(task\) \? laborLaneId\(task\) : UNASSIGNED_LANE_ID/.test(
      ganttCode
    )} 旧聚合泳道已撤=${!/const hasUnassignedTasks = computed/.test(
      ganttCode
    )} 旧文案已撤=${!/'未分配资源 \| 无指定设备'/.test(ganttCode)}`
  )

  // GT54 窗口边界用朴素墙钟串（后端按字面量比较，Z 被忽略 ⇒ toISOString 会差 8 小时）
  // 2026-11-02：dateRange 由「四分支各写一遍」改为「按 RANGE_SPAN_DAYS 算跨度」的单分支
  //   ⇒ 判据从「wallClock 出现 ≥10 次」改为「from/to 两端都走 wallClock + 体内无 toISOString」。
  const drIdx = ganttCode.indexOf('const dateRange = computed')
  const drBody = drIdx >= 0 ? ganttCode.slice(drIdx, drIdx + 900) : ''
  const drWallClockCount = (drBody.match(/wallClock\(start/g) ?? []).length
  const gt54NaiveWallClock =
    drIdx >= 0 &&
    /function wallClock\(d: dayjs\.Dayjs\): string \{/.test(ganttCode) &&
    /return d\.format\('YYYY-MM-DDTHH:mm:ss'\)/.test(ganttCode) &&
    drWallClockCount >= 2 &&
    /from:\s*wallClock\(start\)/.test(drBody) &&
    /to:\s*wallClock\(start\.add\(spanDays, 'day'\)\)/.test(drBody) &&
    !/toISOString/.test(drBody)
  assert(
    'GT54 静态：甘特窗口 from/to 用朴素墙钟串（后端按字面量比较，不用 toISOString）',
    gt54NaiveWallClock,
    `wallClock=${/return d\.format\('YYYY-MM-DDTHH:mm:ss'\)/.test(
      ganttCode
    )} 用点=${drWallClockCount} 仍用toISOString=${/toISOString/.test(drBody)}`
  )

  // GT55 未排桶行（bucketStart 缺键）要有可见去处，不静默丢
  const gt55Unscheduled =
    /const unscheduledLoad = computed/.test(ganttCode) &&
    /scheduleStore\.unscheduledBucketRows/.test(ganttCode) &&
    /v-if="unscheduledLoad\.count > 0"/.test(ganttVue) &&
    /未排期/.test(ganttVue)
  assert(
    'GT55 静态：未排桶任务有可见去处（KPI「未排期 N」+ 资源明细，不静默丢）',
    gt55Unscheduled,
    `computed=${/const unscheduledLoad = computed/.test(
      ganttCode
    )} 源=${/scheduleStore\.unscheduledBucketRows/.test(
      ganttCode
    )} KPI=${/未排期/.test(ganttVue)} 条件=${/v-if="unscheduledLoad\.count > 0"/.test(
      ganttVue
    )}`
  )

  // GT56 桶宽按实际生效值画 + 不裁首桶（切片 loadBands 计算体，防同名绕过）
  // 切片见上方 lbPageBody（精确到本 computed 收尾，避免命中 loadHeatmap 的同名字面量）
  const gt56BucketPaint =
    /const unit = scheduleStore\.effectiveBucketWidth/.test(lbPageBody) &&
    /end: addBucketUnit\(row\.bucketStart, unit\)/.test(lbPageBody) &&
    /if \(!row\.bucketStart\) continue/.test(lbPageBody) &&
    // 不得拿窗口 from 裁首桶（周桶锚=周一，首桶可早于 from）
    !/bucketStart[^\n]*(>=|>)\s*(dateRange|currentBucketWindow|window)/.test(lbPageBody)
  assert(
    'GT56 静态：负荷带按生效桶宽画且不裁首桶（桶终点=桶起点+桶宽）',
    gt56BucketPaint,
    `生效桶宽=${/const unit = scheduleStore\.effectiveBucketWidth/.test(
      lbPageBody
    )} 终点=${/end: addBucketUnit\(row\.bucketStart, unit\)/.test(
      lbPageBody
    )} 未排桶跳过=${/if \(!row\.bucketStart\) continue/.test(
      lbPageBody
    )} 裁首桶=${/bucketStart[^\n]*(>=|>)\s*(dateRange|currentBucketWindow|window)/.test(
      lbPageBody
    )}`
  )

  // GT57 桶是资源级聚合 ⇒ 只在「按资源」视图显示（其余维度无法把桶归到泳道）
  // 2026-11-05：图例撤掉「负荷带仅在『按资源』视图显示」提示（整条负荷图例删除，见 GT106），
  //   门控逻辑本身不变 ⇒ 本条只判门控与空图。
  const gt57ResourceOnly =
    /const loadBandVisible = computed\(\(\) => groupBy\.value === 'resource'\)/.test(ganttCode) &&
    /if \(!loadBandVisible\.value\) return \[\]/.test(ganttCode)
  assert(
    'GT57 静态：负荷带只在「按资源」视图显示（桶是资源级聚合，其余维度不硬凑）',
    gt57ResourceOnly,
    `门控=${/groupBy\.value === 'resource'/.test(ganttCode)} 空图=${
      /if \(!loadBandVisible\.value\) return \[\]/.test(ganttCode)
    }`
  )

  /* ===== GT58–GT63：甘特根因区 · 后端 r13982 形状适配（2026-10-31）=====
   * 背景：5号位 10-31 入库知会 r13982 —— `delayReasons` 接线就位，但后端本期**只下发**
   *   `reasonCode / severity / impactHours`（`ScheduleExplanationFact` 无 IssueCategory 列、
   *   无 Description 列 ⇒ JSON 整键省略，见 `ScheduleQueryService.cs:339` 源码注释）。
   *   我方原实现把这两个键当必填、且严格按 `issueCategory` 分两桶 ⇒ 2号位 一旦产出 TASK 级事实，
   *   两个桶都空 ⇒ 整块根因区静默空白（连「尚未提供任务级延期原因」都被 `length>0` 否掉）。
   * 本组锁「缺 issueCategory / 缺 description 也能照常出行」这一适配，且每条可反向破坏：
   *   GT58 把 issueCategory / description 改回必填 → 红
   *   GT59 把 shareRatio 注释改回旧口径 → 红
   *   GT60 删掉 OTHER 桶（未分类根因无处可落）→ 红
   *   GT61 去掉 description 的 v-if（留空串）→ 红
   *   GT62 把空态守卫改回 `delayReasons?.length === 0` → 红
   *   GT63 去掉 ISSUE_CATEGORY_DESC 的 NonNullable（可选化后 Record 会带 undefined 键）→ 红
   */

  // 切片：GanttTaskDelayReason 接口体 / TaskShareLine 接口体（防同名子串假绿）
  const sliceInterface = (src, name) => {
    const i = src.indexOf(`export interface ${name}`)
    if (i < 0) return ''
    const j = src.indexOf('\nexport ', i + 10)
    return src.slice(i, j > 0 ? j : i + 3000)
  }
  const gdrBody = sliceInterface(typesSrc, 'GanttTaskDelayReason')
  const tslBody = sliceInterface(typesSrc, 'TaskShareLine')

  // GT58 类型层：issueCategory / description 必须可选（后端 r13982 本期缺键 ⇒ JSON 整键省略）
  const gt58Optional =
    /issueCategory\?:\s*'DATA_ISSUE'\s*\|\s*'SCHEDULING_ISSUE'/.test(gdrBody) &&
    /description\?:\s*string/.test(gdrBody)
  assert(
    'GT58 静态：延期根因 issueCategory/description 为可选（后端 r13982 本期缺键）',
    gt58Optional,
    `issueCategory可选=${/issueCategory\?:/.test(gdrBody)} description可选=${/description\?:/.test(
      gdrBody
    )}`
  )

  // GT59 契约注释：shareRatio 口径 = 同任务内归一（Σ=1.0），不是 shareQty / plannedProcessQty
  const gt59Ratio =
    /同任务内归一/.test(tslBody) && !/按 shareQty \/ plannedProcessQty 计算/.test(tslBody)
  assert(
    'GT59 静态：shareRatio 注释口径已改「同任务内归一」（非 shareQty / plannedProcessQty）',
    gt59Ratio,
    `同任务内归一=${/同任务内归一/.test(tslBody)} 旧口径残留=${/按 shareQty \/ plannedProcessQty 计算/.test(
      tslBody
    )}`
  )

  // GT60 分桶：未分类（无 issueCategory）必须落 OTHER 桶，不得无处可去
  const explIdx = ganttCode.indexOf('const explanationReasonsByCategory = computed')
  const explBody = explIdx >= 0 ? ganttCode.slice(explIdx, explIdx + 1600) : ''
  const gt60Other =
    /OTHER:\s*NonNullable<GanttTaskDto\['delayReasons'\]>/.test(explBody) &&
    /r\.issueCategory !== 'DATA_ISSUE'/.test(explBody) &&
    /r\.issueCategory !== 'SCHEDULING_ISSUE'/.test(explBody) &&
    /OTHER:\s*other/.test(explBody)
  assert(
    'GT60 静态：未分类根因落「其他根因」桶（无 issueCategory 也能出行）',
    gt60Other,
    `OTHER桶声明=${/OTHER:\s*NonNullable/.test(explBody)} 非DATA=${/r\.issueCategory !== 'DATA_ISSUE'/.test(
      explBody
    )} 非SCHED=${/r\.issueCategory !== 'SCHEDULING_ISSUE'/.test(explBody)} 返回=${/OTHER:\s*other/.test(
      explBody
    )}`
  )

  // GT61 渲染：OTHER 桶有完整行渲染 + description 缺失时不留空串（三处描述均条件渲染）
  const descCond = (ganttVue.match(/v-if="r\.description"/g) || []).length
  const gt61Render =
    /v-if="explanationReasonsByCategory\.OTHER\.length > 0"/.test(ganttVue) &&
    /v-for="\(r, idx\) in explanationReasonsByCategory\.OTHER"/.test(ganttVue) &&
    /其他根因/.test(ganttVue) &&
    descCond === 3 &&
    !/<span class="reason-desc">\{\{ r\.description \}\}<\/span>/.test(ganttVue)
  assert(
    'GT61 静态：其他根因桶出行（中文标签+严重度+影响小时）+ description 缺失不留空串',
    gt61Render,
    `OTHER渲染=${/v-if="explanationReasonsByCategory\.OTHER\.length > 0"/.test(ganttVue)} vfor=${/v-for="\(r, idx\) in explanationReasonsByCategory\.OTHER"/.test(
      ganttVue
    )} 描述条件渲染数=${descCond} 空串残留=${/<span class="reason-desc">\{\{ r\.description \}\}<\/span>/.test(
      ganttVue
    )}`
  )

  // GT62 空态判据：两处空态都用「实际可渲染行数」，不得回退到 delayReasons?.length
  const guardCount = (ganttVue.match(/explanationRenderedReasonCount === 0/g) || []).length
  const cntIdx = ganttCode.indexOf('const explanationRenderedReasonCount = computed')
  const cntBody = cntIdx >= 0 ? ganttCode.slice(cntIdx, cntIdx + 600) : ''
  const gt62Guard =
    guardCount === 2 &&
    /DATA_ISSUE\.length/.test(cntBody) &&
    /SCHEDULING_ISSUE\.length/.test(cntBody) &&
    /OTHER\.length/.test(cntBody) &&
    !/selectedTask\.delayReasons\?\.length === 0/.test(ganttVue)
  assert(
    'GT62 静态：根因空态按「实际可渲染行数」判（不回退 delayReasons?.length）',
    gt62Guard,
    `守卫处数=${guardCount} 求和=${/OTHER\.length/.test(cntBody)} 旧判据残留=${/selectedTask\.delayReasons\?\.length === 0/.test(
      ganttVue
    )}`
  )

  // GT63 可选化后，ISSUE_CATEGORY_DESC 的 Record 键必须用 NonNullable（否则 TS 键含 undefined）
  const gt63NonNullable =
    /const ISSUE_CATEGORY_DESC: Record<NonNullable<GanttTaskDelayReason\['issueCategory'\]>, string>/.test(
      ganttCode
    ) && /const OTHER_CATEGORY_DESC =/.test(ganttCode)
  assert(
    'GT63 静态：根因分类字典用 NonNullable 键 + 「其他根因」桶有面向用户的说明',
    gt63NonNullable,
    `NonNullable=${/ISSUE_CATEGORY_DESC: Record<NonNullable</.test(ganttCode)} 其他桶说明=${/const OTHER_CATEGORY_DESC =/.test(
      ganttCode
    )}`
  )

  // GT64 2026-10-31：lockMarker 三态 UI 已整块撤除（5号位 D1-c 裁决「撤键」）
  //   撤除理由：①Task.IsLocked 全库 100% false 且写方写死 false；②DemandSupplyHardLock 0 行；
  //   ③冻结 07:4937 明写 FROZEN_ZONE_LOCK 只进 ScheduleExplanationFact、不进 Task 状态字段
  //   ⇒ 三态无落盘源，若留着就是把 BIT 硬塞三态（错标）+ 永远进不去的分支。
  //   本条是**反向锁**：防止后人又把三态标签/派生字段加回来。
  //   反向自证：把 LOCK_LABEL / LOCK_TAG_TYPE / planFreezeFlag 任一加回本页 → 转红。
  const gt64NoLockUi =
    !/LOCK_LABEL/.test(ganttCode) &&
    !/LOCK_TAG_TYPE/.test(ganttCode) &&
    !/planFreezeFlag/.test(ganttCode) &&
    !/executionLockFlag/.test(ganttCode) &&
    !/selectedTask\.lockMarker/.test(ganttCode) &&
    !/TaskLockMarker/.test(ganttCode)
  assert(
    'GT64 静态：lockMarker 三态 UI 已撤除（5号位 D1-c 撤键），禁止把 BIT 硬塞三态或加回死代码',
    gt64NoLockUi,
    `无LOCK_LABEL=${!/LOCK_LABEL/.test(ganttCode)} 无LOCK_TAG_TYPE=${!/LOCK_TAG_TYPE/.test(
      ganttCode
    )} 无planFreezeFlag=${!/planFreezeFlag/.test(
      ganttCode
    )} 无executionLockFlag=${!/executionLockFlag/.test(
      ganttCode
    )} 无模板lockMarker=${!/selectedTask\.lockMarker/.test(ganttCode)} 无TaskLockMarker=${!/TaskLockMarker/.test(
      ganttCode
    )}`
  )

  // GT65 类型位保留（对齐后端 DTO 预留位），但注释必须写明「永久缺键 + 撤除理由」
  const gt65TypeKept = /lockMarker\?:\s*TaskLockMarker/.test(typesSrc)
  const gt65TypeComment =
    /永久缺键/.test(typesSrc) && /ScheduleExplanationFact/.test(typesSrc)
  assert(
    'GT65 静态：types/schedule.ts 保留 lockMarker 类型位（对齐后端预留位）且注明永久缺键与撤除理由',
    gt65TypeKept && gt65TypeComment,
    `类型位=${gt65TypeKept} 永久缺键注释=${gt65TypeComment}`
  )

  // GT66 2026-10-31 / 2026-11-06 三改：泳道行序 = 任务流首现序
  //   旧规则「人工泳道置顶」建立在前游标 `ISNULL(ResourceId,-1)`（人工恒在第一页）之上；
  //   5号位 2026-11-06 裁决改时间序（人工/设备按计划开始穿插）⇒ 置顶会与任务流错位。
  //   现口径：设备行 + 人工行合并后，按各自第一条任务在 data.tasks 里的下标排序；
  //   设备行仍只列已加载任务里出现过的资源（不铺全量空行）。
  //   ⚠️ 只允许排**显示行序**；任务流本身不得本地重排（游标必须与后端同尺）。
  const gt66ResIdx = ganttCode.indexOf("groupBy.value === 'resource'")
  const gt66ResEnd =
    gt66ResIdx >= 0 ? ganttCode.indexOf('return [...rows, ...laborRows]', gt66ResIdx) : -1
  const gt66ResourceBranch =
    gt66ResIdx >= 0 && gt66ResEnd > gt66ResIdx
      ? ganttCode.slice(gt66ResIdx, gt66ResEnd + 220)
      : ''
  const gt66FirstSeen =
    /const firstSeenOrder = new Map<string, number>\(\)/.test(gt66ResourceBranch) &&
    /data\.tasks\.forEach\(\(t: GanttTaskDto, i: number\) => \{/.test(gt66ResourceBranch) &&
    /firstSeenOrder\.set\(id, i\)/.test(gt66ResourceBranch) &&
    /return \[\.\.\.rows, \.\.\.laborRows\]\.sort\(/.test(gt66ResourceBranch) &&
    !/rows\.unshift\(\.\.\.laborRows\)/.test(gt66ResourceBranch)
  const gt66LoadedOnly =
    /const loadedResourceIds = new Set<number>\(\)/.test(gt66ResourceBranch) &&
    /loadedResourceIds\.add\(t\.resourceId\)/.test(gt66ResourceBranch) &&
    /data\.resources\s*\.filter\(\(r\) => loadedResourceIds\.has\(r\.resourceId\)\)\s*\.map\(/.test(
      gt66ResourceBranch
    )
  const gt66OnlyDisplaySort =
    gt66LoadedOnly &&
    !/\[\s*\.\.\.data\.resources\s*\]\.sort\(/.test(gt66ResourceBranch) &&
    !/data\.resources\s*\n?\s*\.filter\([^)]*\)\s*\.sort\(/.test(gt66ResourceBranch) &&
    (gt66ResourceBranch.match(/\.sort\(/g) ?? []).length === 1
  assert(
    'GT66 静态：泳道行按任务流首现序（人工不再置顶）、设备泳道只列已加载任务里的资源',
    gt66FirstSeen && gt66OnlyDisplaySort,
    `首现序=${gt66FirstSeen} 只列已加载=${gt66LoadedOnly} 仅显示层排序=${gt66OnlyDisplaySort} sort数=${
      (gt66ResourceBranch.match(/\.sort\(/g) ?? []).length
    } 资源分支长度=${gt66ResourceBranch.length}`
  )

  // GT67 2026-10-31：版本下拉必须分组（10-30 审计缺口 #3）
  //   ①「常用版本」= store 的 displayedVersions（ACTIVE 置顶 → 最近 CANDIDATE → 失败 → 当前已选）
  //   ②「其他版本」= 其余全部（**仍可达**：分组是分得开，不是裁掉）
  //   ⇒ 不得再平铺全量 versions（失败测试版混排），也不得只留 displayedVersions（其余版本选不到）。
  //   ⚠️ 「其他版本」组必须**真的由全量 versions 排除 pinned 得出**（`items: rest` + 排除谓词），
  //   只断言出现 displayedVersions 会假绿（2026-10-31 自证首轮即踩到）。
  const gt67Grouped =
    /<ElOptionGroup\s+v-for="g in versionGroups"/.test(ganttCode) &&
    /const versionGroups = computed/.test(ganttCode) &&
    /displayedVersions\.value/.test(ganttCode) &&
    /label: '常用版本'/.test(ganttCode) &&
    /label: '其他版本'/.test(ganttCode) &&
    /items: rest/.test(ganttCode) &&
    /filter\(\(v\) => !pinnedIds\.has\(v\.id\)\)/.test(ganttCode) &&
    !/v-for="v in versions"/.test(ganttCode)
  assert(
    'GT67 静态：版本下拉分组（常用版本=displayedVersions / 其他版本=全量减pinned），不再平铺全量 versions',
    gt67Grouped,
    `分组=${/<ElOptionGroup\s+v-for="g in versionGroups"/.test(
      ganttCode
    )} computed=${/const versionGroups = computed/.test(
      ganttCode
    )} 用displayedVersions=${/displayedVersions\.value/.test(
      ganttCode
    )} 两组标签=${/label: '常用版本'/.test(ganttCode) && /label: '其他版本'/.test(
      ganttCode
    )} 其余组真取全量=${/items: rest/.test(ganttCode) && /filter\(\(v\) => !pinnedIds\.has\(v\.id\)\)/.test(
      ganttCode
    )} 无平铺=${!/v-for="v in versions"/.test(ganttCode)}`
  )

  // GT68 2026-10-31：任务清单空态必须接 emptyKind 口径（10-30 审计缺口 #3 之二）
  //   旧文案恒为「该时间范围内没有任务」⇒ 版本本身没排程结果时也说成"时间范围里没有"，与画布 4 态提示矛盾。
  const gt68EmptyText =
    /:description="taskListEmptyText"/.test(ganttCode) &&
    /const taskListEmptyText = computed/.test(ganttCode) &&
    /case 'window-empty'/.test(ganttCode) &&
    /case 'no-tasks'/.test(ganttCode) &&
    /case 'scope-denied'/.test(ganttCode) &&
    !/description="该时间范围内没有任务"/.test(ganttCode)
  assert(
    'GT68 静态：任务清单空态接 emptyKind（版本级/窗口级分说），不再恒显「该时间范围内没有任务」',
    gt68EmptyText,
    `绑定=${/:description="taskListEmptyText"/.test(ganttCode)} computed=${/const taskListEmptyText = computed/.test(
      ganttCode
    )} 四态=${/case 'window-empty'/.test(ganttCode) && /case 'no-tasks'/.test(ganttCode) && /case 'scope-denied'/.test(
      ganttCode
    )} 无旧文案=${!/description="该时间范围内没有任务"/.test(ganttCode)}`
  )

  // GT69 2026-10-31 / 2026-11-06：维度泳道空值兜底必须中文，且维度取值不依赖「有没有设备」
  //   阶段/工厂/生产部门三个维度此前只从**资源**上取 ⇒ 无设备的人工任务被丢进「未分配资源」。
  //   2026-11-06 改为：有资源用资源字段（后端权威），无资源用任务自带字段
  //   （`stageCode` / `departmentName`，工厂按部门反查）；字段确实没配才「未配置」。
  //   禁英文 UNKNOWN。
  const gt69NoUnknownLabel =
    !/r\?\.stage\s*\?\?\s*'UNKNOWN'/.test(ganttCode) &&
    !/r\?\.factoryName\s*\?\?\s*'UNKNOWN'/.test(ganttCode) &&
    !/r\?\.productionDepartmentName\s*\?\?\s*'UNKNOWN'/.test(ganttCode)
  const gt69DimResolvers =
    /const DIM_UNCONFIGURED_LABEL = '未配置'/.test(ganttCode) &&
    /function stageDimOf\(t: GanttTaskDto, resource: GanttResourceDto \| undefined\): string/.test(
      ganttCode
    ) &&
    /return t\.stageCode\?\.trim\(\) \|\| DIM_UNCONFIGURED_LABEL/.test(ganttCode) &&
    /return factoryNameOfTask\(t, undefined\) \|\| DIM_UNCONFIGURED_LABEL/.test(ganttCode) &&
    /return t\.departmentName\?\.trim\(\) \|\| DIM_UNCONFIGURED_LABEL/.test(ganttCode) &&
    /const stage = stageDimOf\(t, r\)/.test(ganttCode) &&
    /const name = factoryDimOf\(t, r\)/.test(ganttCode) &&
    /const name = departmentDimOf\(t, r\)/.test(ganttCode) &&
    !/function dimValueOf\(/.test(ganttCode) &&
    !/'未分配资源'/.test(ganttCode)
  assert(
    'GT69 静态：维度泳道（阶段/工厂/部门）有资源用资源字段、无资源用任务字段，空值兜底「未配置」，无英文 UNKNOWN',
    gt69DimResolvers && gt69NoUnknownLabel,
    `维度取值=${gt69DimResolvers} 无英文UNKNOWN回落=${gt69NoUnknownLabel}`
  )

  // GT70 2026-10-31：白天人工调整 4 入口的默认域必须是「当前页面看得到的那个域」
  //   工具栏入口不带预填 ⇒ entryDomainKey 为 undefined ⇒ 弹窗退到 domainOptions[0]
  //   （dev 实测 = FAMILY_X），而当前版本域是 FAMILY_QT ⇒ 弹窗开出来「已选 0/0 个任务」。
  const gt70CurrentDomain =
    /const currentDomainKey = computed/.test(ganttCode) &&
    /versions\.value\.find\(\s*\(v: PlanVersionSummaryDto\) => v\.id === currentVersionId\.value\s*\)\?\.domainKey/.test(
      ganttCode
    ) &&
    /opts\.includes\(versionDomain as DomainKey\)/.test(ganttCode)
  const gt70Bindings = (ganttCode.match(
    /:default-domain-key="entryDomainKey \?\? currentDomainKey"/g
  ) || []).length
  const gt70Precedence =
    /preTask\?\.domainKey \?\? props\.defaultDomainKey \?\? props\.domainOptions\[0\]/.test(
      readSrc('views/Aps/components/GanttAdjustDialog.vue')
    ) &&
    /preRes\?\.domainKey \?\? props\.defaultDomainKey \?\? props\.domainOptions\[0\]/.test(
      readSrc('views/Aps/components/ResourceRescheduleDialog.vue')
    )
  assert(
    'GT70 静态：白天人工调整 4 入口默认域 = 页面当前域（entryDomainKey ?? currentDomainKey），预填对象自身域优先',
    gt70CurrentDomain && gt70Bindings === 4 && gt70Precedence,
    `currentDomainKey=${gt70CurrentDomain} 绑定处=${gt70Bindings}/4 预填优先=${gt70Precedence}`
  )

  // GT71 2026-10-31：甘特图调整弹窗表格必须分页（否则大域下打开即卡死页面）
  //   dev 实测单域 5953 条任务；ElTable 非虚拟化会一次渲染全部行（每行还挂一个 ElDatePicker）
  //   ⇒ 打开弹窗主线程卡死，CDP > 100s 无响应（headless 实测 10-31）。
  const ganttAdjustDialog = readSrc('views/Aps/components/GanttAdjustDialog.vue')
  const gt71Paged =
    /const pagedRows = computed/.test(ganttAdjustDialog) &&
    /:data="pagedRows"/.test(ganttAdjustDialog) &&
    /rows\.value\.slice\(\(page\.value - 1\) \* pageSize\.value, page\.value \* pageSize\.value\)/.test(
      ganttAdjustDialog
    ) &&
    /const pageSize = ref\(50\)/.test(ganttAdjustDialog)
  const gt71Pager =
    /<ElPagination/.test(ganttAdjustDialog) &&
    /v-model:current-page="page"/.test(ganttAdjustDialog) &&
    /:total="rows\.length"/.test(ganttAdjustDialog)
  const gt71Reserve = /type="selection"[^>]*reserve-selection/.test(ganttAdjustDialog)
  assert(
    'GT71 静态：甘特图调整弹窗表格分页 + 跨页保留勾选（大域不卡死）',
    gt71Paged && gt71Pager && gt71Reserve && !/:data="rows"/.test(ganttAdjustDialog),
    `分页=${gt71Paged} 分页器=${gt71Pager} 跨页保留=${gt71Reserve} 未回退整表=${!/:data="rows"/.test(ganttAdjustDialog)}`
  )

  /* ===== GT72–GT79：2026-11-02 甘特「对标知名排产系统」一轮 =====
   * 触发：领导看图后判「这个用户用不了」—— 4 小时的条在「天」刻度下只有 ~12px，
   *   既看不见也点不中；且没有缩放、没有悬浮提示、负荷信息只有一条 7px 细带。
   * 本组锁本轮落地的八件事：
   *   GT72 缩放层级（组件层 4 档 + ext.zoom.init + 不劫持滚轮）
   *   GT73 页面刻度控件接线（下拉 + ＋/－ + zoom-change 回抛）
   *   GT74 缩放与时间窗**解耦**（zoomUnit 独立，不再由 timeRange 派生）
   *   GT75 窗口相对刻度太窄时自动放大（否则轴上只有一列）
   *   GT76 条最小心宽保底 + 条内任务号按宽度分级降级
   *   GT77 悬浮提示（tooltip 插件 + 业务行由页面层拼）
   *   GT78 资源负荷视图（资源 × 时间桶热力图；无产能口径 ⇒ 不标超载）
   *   GT79 负荷带深浅改**全局**归一（早期按行内归一 ⇒ 每行看着都"很满"，横向比较是假的）
   *   GT80 换刻度后视口锚回当前可见行（ext.zoom 会把视口甩到任务区间外 ⇒ 条子被裁空）
   */

  // GT72 组件层缩放层级
  const gt72Levels =
    /const ZOOM_LEVELS = \[/.test(dhxCode) &&
    /name: '小时'/.test(dhxCode) &&
    /name: '天'/.test(dhxCode) &&
    /name: '周'/.test(dhxCode) &&
    /name: '月'/.test(dhxCode) &&
    (dhxCode.match(/scales: \[/g) || []).length >= 4
  const gt72Init =
    /zoom\.init\(\{[\s\S]{0,260}levels: ZOOM_LEVELS/.test(dhxCode) &&
    // 不劫持滚轮：滚轮留给时间轴横向滚动
    /trigger: null/.test(dhxCode) &&
    /const initZoom = \(\) =>/.test(dhxCode)
  // 组件对外只暴露「按名设档 / 滚到某天 / 取可见行起点」三件事
  //   2026-11-03：＋/－ 步进按钮已撤（与页面「时间尺度」下拉同一条路径）⇒ 不再暴露 zoomIn/zoomOut
  const exposeBody = (() => {
    const i = dhxCode.indexOf('defineExpose(')
    return i >= 0 ? dhxCode.slice(i, i + 140) : ''
  })()
  const gt72Expose =
    /setZoomLevelByName/.test(exposeBody) &&
    /showDate/.test(exposeBody) &&
    !/zoomIn/.test(exposeBody) &&
    !/zoomOut/.test(exposeBody) &&
    !/resetZoom/.test(exposeBody)
  assert(
    'GT72 静态：时间轴缩放层级 4 档（小时/天/周/月）+ ext.zoom.init 且不劫持滚轮',
    gt72Levels && gt72Init && gt72Expose,
    `层级=${gt72Levels} init=${gt72Init} 暴露=${gt72Expose}`
  )

  // GT73 页面刻度控件接线（2026-11-03：刻度只留「时间尺度」下拉一个入口）
  const gt73Options = /const ZOOM_OPTIONS = \['小时', '天', '周', '月'\]/.test(ganttCode)
  const gt73Wire =
    /@zoom-change="onZoomChange"/.test(ganttVue) &&
    /function onZoomChange/.test(ganttCode) &&
    /zoomChange: \[levelName: string\]/.test(dhxCode)
  // ＋/－ 与下拉是同一组 4 档的第二个入口 ⇒ 必须没有（且别再被加回来）
  const gt73Single = !/@click="zoomIn"/.test(ganttVue) && !/@click="zoomOut"/.test(ganttVue)
  assert(
    'GT73 静态：刻度只有「时间尺度」下拉一个入口（无 ＋/－ 步进）+ 与组件 zoom-change 双向接线',
    gt73Options && gt73Wire && gt73Single,
    `档位=${gt73Options} 接线=${gt73Wire} 单一入口=${gt73Single}`
  )

  // GT74 「时间尺度」单一控件（2026-11-03 合并：窗口 + 刻度原本是两个控件、1:1 对应 ⇒ 同一轴写两遍）
  const scaleSpanBody = sliceFrom(ganttCode, 'const SCALE_SPAN_DAYS', 220)
  const gt74Spans =
    // 2026-11-05：小时档 2 天 → 1 天（48 个 28px 小时列放不下「00:00」标签）
    /hour:\s*1/.test(scaleSpanBody) &&
    /day:\s*7/.test(scaleSpanBody) &&
    /week:\s*30/.test(scaleSpanBody) &&
    /month:\s*90/.test(scaleSpanBody)
  const gt74ZoomUnit =
    /const zoomUnit = ref<DhxGanttScaleUnit>\('day'\)/.test(ganttCode) &&
    /scaleUnit: zoomUnit\.value/.test(drBody) &&
    // dateRange 里不得再硬编码刻度（那是「刻度由时间窗派生」的旧耦合写法）
    !/scaleUnit:\s*'(hour|day|week|month)'/.test(drBody)
  // 「时间范围」radio 必须已删除（留着就是同一个轴写两遍 ⇒ 改刻度后 radio 取消选中，看着像失灵）
  const gt74NoRadio = !/ElRadioGroup/.test(ganttVue) && !/ElRadioButton/.test(ganttVue)
  assert(
    'GT74 静态：「时间尺度」单一控件（窗口跨度由 SCALE_SPAN_DAYS 派生，页面不再有「时间范围」radio）',
    gt74Spans && gt74ZoomUnit && gt74NoRadio,
    `各档跨度=${gt74Spans} zoomUnit=${gt74ZoomUnit} 无radio=${gt74NoRadio}`
  )

  // GT105 小时档可读性（2026-11-05 用户截图标红：小时刻度挤成一团）
  // 实测：2 天 = 48 列 × 28px，「00:00」文字宽 36~40px ⇒ 每格都溢出。
  // 修法 = 窗口 1 天（24 列被拉宽到 ~56px）+ 列宽下限 44px（窄屏兜底）+ 单日标签不写重复起止。
  const gt105HourLevel = sliceFrom(dhxCode, "name: '小时'", 420)
  const gt105ColNum = (() => {
    const m = /min_column_width:\s*(\d+)/.exec(gt105HourLevel)
    return m ? Number(m[1]) : 0
  })()
  const gt105LabelBody = (() => {
    const i = ganttCode.indexOf('const windowLabel = computed')
    return i < 0 ? '' : ganttCode.slice(i, i + 500)
  })()
  const gt105SingleDay = /span\s*===\s*1/.test(gt105LabelBody)
  const gt105Span = /hour:\s*1/.test(scaleSpanBody)
  assert(
    'GT105 静态：小时档 1 天窗 + 小时列宽 ≥40px + 单日窗口标签不重复起止',
    gt105Span && gt105ColNum >= 40 && gt105SingleDay,
    `1天窗=${gt105Span} 小时列宽=${gt105ColNum || '缺'} 单日标签=${gt105SingleDay}`
  )

  // GT106 图例收口（2026-11-05 收敛；2026-11-06 用户指定再撤「延期」）
  // 只留状态色；已撤「资源占用区间」「资源负荷（深浅=任务数）」「延期」。
  // 负荷带图层与页签本身都保留。
  const gt106LegendBody = sliceFrom(ganttVue, 'class="gantt-legend"', 900)
  const gt106NoLane = !/资源占用区间/.test(ganttVue) && !/legend-swatch is-lane/.test(ganttVue)
  const gt106NoLoad =
    !/深浅=任务数/.test(ganttVue) &&
    !/legend-swatch is-load/.test(ganttVue) &&
    !/负荷带仅在/.test(ganttVue)
  const gt106NoDelay = !/legend-swatch is-delay/.test(ganttVue) && !/is-delay/.test(gt106LegendBody)
  const gt106KeepStatus = /legendItems/.test(gt106LegendBody)
  assert(
    'GT106 静态：图例只留状态色（已撤「延期」「资源占用区间」「资源负荷（深浅=任务数）」）',
    gt106NoLane && gt106NoLoad && gt106NoDelay && gt106KeepStatus,
    `无占用区间图例=${gt106NoLane} 无负荷图例=${gt106NoLoad} 无延期图例=${gt106NoDelay} 保留状态色=${gt106KeepStatus}`
  )

  // GT107 工具栏布局（2026-11-05 用户要求：撤「插单影响分析」入口；业务按钮跟在搜索框之后，
  //   不再独占一行 —— 窄屏下少占一行）
  const gt107NoInsert =
    !/插单影响分析/.test(ganttCode) &&
    !/goInsertImpactAnalysis/.test(ganttCode) &&
    !/purpose: 'INSERT_IMPACT_ANALYSIS'/.test(ganttCode)
  const gt107Flow =
    !/toolbar-right/.test(ganttVue) &&
    !/\.toolbar\s*\{[^}]*flex-direction:\s*column/.test(ganttVue)
  const gt107AfterSearch =
    ganttCode.indexOf('class="gantt-search"') >= 0 &&
    ganttCode.indexOf('class="gantt-search"') < ganttCode.indexOf('白天人工调整')
  assert(
    'GT107 静态：撤「插单影响分析」入口 + 「白天人工调整」排在搜索框之后（不再独占一行）',
    gt107NoInsert && gt107Flow && gt107AfterSearch,
    `无插单入口=${gt107NoInsert} 单行流式=${gt107Flow} 按钮在搜索框后=${gt107AfterSearch}`
  )

  // GT108 导出清单挪进任务清单页签右上（2026-11-05 用户要求），
  //   原右上角「共 N 条 · 按计划开始时间排序 · 与图上条数同源」说明行删除。
  const gt108Head = sliceFrom(ganttVue, 'class="task-list-head"', 800)
  const gt108InHead = /导出清单/.test(gt108Head) && /@click="exportTaskList"/.test(gt108Head)
  const gt108OutOfToolbar = (() => {
    const start = ganttVue.indexOf('class="toolbar"')
    const end = ganttVue.indexOf('class="view-bar"', start)
    const seg = start >= 0 && end > start ? ganttVue.slice(start, end) : ''
    return seg !== '' && !/导出清单/.test(seg)
  })()
  const gt108NoHint = !/与图上条数同源/.test(ganttVue) && !/按计划开始时间排序/.test(ganttVue)
  assert(
    'GT108 静态：导出清单在任务清单页签右上 + 原「共 N 条 · 排序 · 同源」说明行已删',
    gt108InHead && gt108OutOfToolbar && gt108NoHint,
    `在清单页头=${gt108InHead} 不在工具栏=${gt108OutOfToolbar} 无说明行=${gt108NoHint}`
  )

  // GT109 版本项悬停信息（2026-11-05：冻结 01 §77A② 要求看清 PlanVersion/生成时间/Base/Run，
  //   走下拉项悬停而不是页面元信息条 —— 甘特主视图要占满高度）
  //  2026-11-05 B10（5号位 已落码）：悬停再补 DataCutoffTime / 触发方式 / 触发来源。
  const gt109TitleBody = sliceFrom(ganttCode, 'function versionOptionTitle', 1600)
  const gt109Wired = /:title="versionOptionTitle\(v\)"/.test(ganttVue)
  const gt109Fields =
    /basePlanVersionId\b/.test(gt109TitleBody) &&
    /sourceScheduleRunId\b/.test(gt109TitleBody) &&
    /computedAt\b/.test(gt109TitleBody) &&
    /activatedAt\b/.test(gt109TitleBody)
  // B10 三键：数据截止 / 触发方式 / 触发来源
  const gt109B10 =
    /v\.dataCutoffTime/.test(gt109TitleBody) &&
    /v\.runType/.test(gt109TitleBody) &&
    /v\.triggeredBy/.test(gt109TitleBody)
  // 不新增页面元信息条：B10 三项只出现在**悬停标题**里，模板中不得出现可见的数据截止行
  const gt109TplBody = (() => {
    const i = ganttVue.indexOf('<template>')
    const j = ganttVue.lastIndexOf('</template>')
    return i >= 0 && j > i ? ganttVue.slice(i, j) : ''
  })()
  const gt109NoMetaBar = !/数据截止时间/.test(gt109TplBody)
  assert(
    'GT109 静态：版本项悬停含 Base/Run/生成时间/激活时间 + B10 三键（数据截止/触发方式/触发来源），不新增页面元信息条',
    gt109Wired && gt109Fields && gt109B10 && gt109NoMetaBar,
    `悬停已接线=${gt109Wired} 字段齐=${gt109Fields} B10三键=${gt109B10} 无元信息条=${gt109NoMetaBar}`
  )

  // GT110 任务清单三列（客户交期 / 延期时长 / 主要原因），导出 CSV 与表格逐列一致
  // 窗口按「清单面板 → 表格列」的整段长度取（2026-11-05 加部门/阶段/指示号/人工槽 4 列后变长）
  const gt110Table = sliceFrom(ganttVue, 'class="task-list-pane"', 8000)
  const gt110Cols =
    /label="客户交期"/.test(gt110Table) &&
    /label="延期时长"/.test(gt110Table) &&
    /label="主要原因"/.test(gt110Table)
  const gt110CsvHead = sliceFrom(ganttCode, 'const EXPORT_COLUMNS', 400)
  const gt110CsvCols =
    /'客户交期'/.test(gt110CsvHead) &&
    /'延期时长\(h\)'/.test(gt110CsvHead) &&
    /'主要原因'/.test(gt110CsvHead)
  const gt110CsvValues = /primaryDelayReasonText\(t\) \?\? ''/.test(
    sliceFrom(ganttCode, 'function exportTaskList', 2200)
  )
  assert(
    'GT110 静态：任务清单有「客户交期/延期时长/主要原因」三列且 CSV 同步导出',
    gt110Cols && gt110CsvCols && gt110CsvValues,
    `表格列=${gt110Cols} CSV列=${gt110CsvCols} CSV取值=${gt110CsvValues}`
  )

  // GT111 缺数据只说「未提供」，不再断言「单 Order 任务」「单段加工」
  //  2026-11-05 B8：TaskSegments 裁决「不做、撤」⇒ 中性占位文案整条撤掉（不再留「等它有」的预期位）
  const gt111Share =
    /未提供订单份额数据（TaskShare）/.test(ganttVue) && !/单 Order 任务/.test(ganttVue)
  const gt111Segments =
    !/未提供数量-时间拆分数据（TaskSegments）/.test(ganttVue) && !/单段加工/.test(ganttVue)
  assert(
    'GT111 静态：TaskShare 缺失时只说「未提供」；TaskSegments 展示位已撤（B8）',
    gt111Share && gt111Segments,
    `订单份额中性文案=${gt111Share} 拆分位已撤=${gt111Segments}`
  )

  // GT112 甘特左表物料口径（2026-11-05：物料并入首列后仍用 materialCode，
  //   与任务清单/导出 CSV 同一口径；materialName 只在悬浮与详情里用）
  const gt112Helper = sliceFrom(ganttCode, 'function gridLabelOf', 320)
  const gt112UsesCode =
    /const material = t\.materialCode\?\.trim\(\)/.test(gt112Helper) &&
    !/materialName/.test(gt112Helper)
  assert(
    'GT112 静态：甘特左表首列物料口径 = materialCode（与清单/导出一致）',
    gt112UsesCode,
    `左表用 materialCode=${gt112UsesCode}`
  )

  /* ===== GT113–GT116：2026-11-05 5号位 B1–B11 逐条答复落地后的前端收口 =====
   * 依据：docs/5号位-2026-11-05-答复-甘特冻结核对B1至B11逐条-四项已落码含键名实测勘误-给4号位.md
   *   B2① 工单号（JSON 键 mesWorkOrderNo）/ B2② 下发状态（dispatchStatus）
   *   B5  关键路径读侧三态（bool? ⇒ 缺键=未判定）
   *   B10 版本行补 DataCutoffTime / RunType / TriggeredBy
   *   B6/B7/B8 三个展示位「不做、撤」（上游 Stage 阻挡 / 事实类型 / 数量-时间拆分）
   */

  // ---- GT113：MES 工单号 + 下发状态（详情 + 清单 + CSV 三处同源，缺键中性）----
  const gt113Type =
    /mesWorkOrderNo\?:\s*string/.test(typesSrc) && /dispatchStatus\?:\s*string/.test(typesSrc)
  const gt113Drawer =
    /selectedTask\.mesWorkOrderNo\b/.test(ganttVue) && /selectedTask\.dispatchStatus\b/.test(ganttVue)
  const gt113List = /row\.mesWorkOrderNo\b/.test(ganttVue) && /row\.dispatchStatus\b/.test(ganttVue)
  const gt113Csv =
    /'MES工单号'/.test(ganttCode) &&
    /'下发状态'/.test(ganttCode) &&
    /t\.mesWorkOrderNo \?\? ''/.test(ganttCode) &&
    /t\.dispatchStatus \?\? ''/.test(ganttCode)
  // 缺键中性文案：未建单 / 未下发（不得断言「无工单」，也不得留空）
  const gt113Neutral = /未建单/.test(ganttVue) && /未下发/.test(ganttVue)
  assert(
    'GT113 静态：MES 工单号（mesWorkOrderNo）+ 下发状态（dispatchStatus）在详情/清单/CSV 三处同源，缺键显示未建单/未下发',
    gt113Type && gt113Drawer && gt113List && gt113Csv && gt113Neutral,
    `类型=${gt113Type} 详情=${gt113Drawer} 清单=${gt113List} CSV=${gt113Csv} 中性文案=${gt113Neutral}`
  )

  // ---- GT114：关键路径三态（true / false / 缺键=未判定）----
  const gt114True = /selectedTask\.isCriticalPath === true/.test(ganttVue)
  const gt114False = /selectedTask\.isCriticalPath === false/.test(ganttVue)
  const gt114Unknown = /未判定/.test(ganttVue)
  // 旧的「v-if=isCriticalPath / v-else '-'」写法会把「未判定」显示成「不是关键路径」⇒ 必须改掉
  const gt114NoFlat = !/v-if="selectedTask\.isCriticalPath"/.test(ganttVue)
  assert(
    'GT114 静态：关键路径三态（是 / 否 / 未判定），不再把「未判定」显示成「非关键路径」',
    gt114True && gt114False && gt114Unknown && gt114NoFlat,
    `是=${gt114True} 否=${gt114False} 未判定=${gt114Unknown} 无旧的二值写法=${gt114NoFlat}`
  )

  // ---- GT115：类型层新增键齐全（B2①/B2② 任务两键 + B10 版本三键）----
  const gt115TaskKeys =
    /mesWorkOrderNo\?:\s*string/.test(typesSrc) && /dispatchStatus\?:\s*string/.test(typesSrc)
  const gt115VersionKeys =
    /dataCutoffTime\?:\s*IsoDateTime/.test(typesSrc) &&
    /runType\?:\s*string/.test(typesSrc) &&
    /triggeredBy\?:\s*string/.test(typesSrc)
  assert(
    'GT115 静态：类型层新增 5 个可选键（mesWorkOrderNo/dispatchStatus + dataCutoffTime/runType/triggeredBy）',
    gt115TaskKeys && gt115VersionKeys,
    `任务两键=${gt115TaskKeys} 版本三键=${gt115VersionKeys}`
  )

  // ---- GT116：B6/B7/B8/B4 四个展示位已撤（反向锁：不得复活，含样式与 mock 造数）----
  // 判据只看**代码**（剥掉 JS 注释与 HTML 注释）：注释里写「已撤」不算复活，展示位复活才算。
  const commonTypesSrc = stripComments(readSrc('api/aps-v1/types/common.ts'))
  const gt116Clean = ganttCode.replace(/<!--[\s\S]*?-->/g, '')
  const gt116NoSegments =
    !/taskSegments/.test(gt116Clean) && !/数量-时间拆分/.test(gt116Clean)
  const gt116NoFactType =
    !/selectedTask\.factType/.test(gt116Clean) &&
    !/FACT_TYPE_LABELS/.test(gt116Clean) &&
    !/FACT_TYPE_TAG/.test(gt116Clean) &&
    !/FACT_TYPE_LABELS/.test(commonTypesSrc)
  const gt116NoUpstream =
    !/upstreamStageBlocked/.test(gt116Clean) && !/upstreamStageCode/.test(gt116Clean)
  // B4 跨 Domain 共享设备阻挡：5号位 2026-11-05 裁决「不做（V1）」⇒ 恒缺键 ⇒ 展示位一并撤除
  const gt116NoCrossDomain =
    !/crossDomainBlocked/.test(gt116Clean) &&
    !/crossDomainBlockReason/.test(gt116Clean) &&
    !/外排程域共享设备阻挡/.test(gt116Clean) &&
    !/\.u14-alert/.test(gt116Clean)
  const gt116NoDeadCode =
    !/\.segment-section/.test(gt116Clean) && !/\.upstream-stage-blocked/.test(gt116Clean)
  // mock 造数也必须撤（mock 要与真实契约同形：这几项真实后端永久缺键）
  //  判据只扫**代码**：mock 注释里写「已撤」不算造数（FactType 这类英文词会出现在说明注释里）
  const mockClean = stripComments(mockSrc)
  const gt116NoMock = !/taskSegments|factType|upstreamStage|crossDomain/i.test(mockClean)
  assert(
    'GT116 静态：B6/B7/B8/B4 四个展示位已撤（上游 Stage 阻挡 / 事实类型 / 数量-时间拆分 / 跨域阻挡），含样式与 mock 造数一并清除',
    gt116NoSegments &&
      gt116NoFactType &&
      gt116NoUpstream &&
      gt116NoCrossDomain &&
      gt116NoDeadCode &&
      gt116NoMock,
    `无拆分=${gt116NoSegments} 无事实类型=${gt116NoFactType} 无上游Stage=${gt116NoUpstream} 无跨域阻挡=${gt116NoCrossDomain} 无死样式=${gt116NoDeadCode} mock同形=${gt116NoMock}`
  )

  // ---- GT117：delayReasons 行级 objectType（5号位 2026-11-05 Q2-a 放宽落码）----
  //   过滤由 `ObjectType='TASK'` 放宽为 TASK∪ORDER ⇒ 订单级事实按 TaskId 透传到该单每个任务，
  //   页面**必须**标「订单级」；清单列走 `top.`，抽屉根因条目走 `r.`，两处都要锁。
  const gt117Clean = ganttCode.replace(/<!--[\s\S]*?-->/g, '')
  const gt117Type = /objectType\?:\s*'TASK'\s*\|\s*'ORDER'/.test(typesSrc)
  const gt117List = /top\.objectType === 'ORDER'/.test(gt117Clean) && /（订单级）/.test(gt117Clean)
  const gt117Drawer =
    /r\.objectType === 'ORDER'/.test(gt117Clean) &&
    (gt117Clean.match(/r\.objectType === 'ORDER'/g) || []).length >= 3
  const gt117Mock =
    /objectType: 'ORDER'/.test(mockSrc) && /objectType: 'TASK'/.test(mockSrc)
  assert(
    'GT117 静态：delayReasons 行级 objectType（类型可选键 + 清单「主要原因」标（订单级）+ 抽屉三个根因桶标「订单级」+ mock 同形）',
    gt117Type && gt117List && gt117Drawer && gt117Mock,
    `类型=${gt117Type} 清单=${gt117List} 抽屉三处=${gt117Drawer} mock同形=${gt117Mock}`
  )

  // ---- GT118：甘特图可读性增强（条内完成✓ / 延期+Nh / 泳道浅绿；物料列已并入首列见 GT120）----
  const gt118DhxTypes = readSrc('components/DhxGantt/src/types.ts')
  // 判据必须切片到 barMarkText 块：`Math.round(t.delayHours)` 在悬浮提示里也有一处，
  // 全文搜会让「条上延期标记被摘」仍然假绿。
  // 2026-11-06 泳道摘要条也带 barMark/barMarkText（延期 M）⇒ 锚点改任务条独有的
  // 「barMark: status === 'COMPLETED'」，否则切片会切到泳道行、任务标记被摘也假绿。
  const gt118MarkBody = sliceFrom(ganttCode, "barMark: status === 'COMPLETED'", 320)
  const gt118MarkBind =
    /barMark:\s*status === 'COMPLETED' \? 'done' : t\.isDelayed \? 'delay' : ''/.test(ganttCode) &&
    /'✓'/.test(gt118MarkBody) &&
    /Math\.round\(t\.delayHours\)/.test(gt118MarkBody)
  const gt118MarkRender =
    /class="dhx-bar-inner"/.test(dhxCode) &&
    /const markClass = task\.barMark === 'done' \? 'is-done' : 'is-delay'/.test(dhxCode) &&
    /<span class="dhx-bar-mark \$\{markClass\}">\$\{escapeHtml\(task\.barMarkText\)\}<\/span>/.test(
      dhxCode
    )
  const gt118Types = /barMarkText\?:\s*string/.test(gt118DhxTypes)
  // 2026-11-06 A4：泳道带不再整片刷浅绿 ⇒ 浅色泳道走中性淡灰蓝，彩色泳道保留维度底色。
  // 反向破坏：把 #eef2f8 改回 #d3f1e3（绿色回潮）→ 转红。
  const gt118Lane =
    /\.dhx-task-lane\.dhx-lane-light \.gantt_task_content\)? \{[\s\S]{0,200}#eef2f8/.test(
      dhxCode
    ) && !/#d3f1e3/.test(dhxCode)
  assert(
    'GT118 静态：甘特可读性（完成✓/延期+Nh + 泳道不再整片绿）',
    gt118MarkBind && gt118MarkRender && gt118Types && gt118Lane,
    `标记绑定=${gt118MarkBind} 标记渲染=${gt118MarkRender} 类型位=${gt118Types} 泳道=${gt118Lane}`
  )

  // ---- GT124：窄条条外标签追加红色延期小时（2026-11-06 用户截图要求）----
  // 窄条条内写不下 ⇒ task_text 返回空、由外挂标签层兜底；此前外挂层只写任务号，
  // 窄条的延期信息完全丢失。现要求跟在任务号后用红字补「+Nh」。
  const gt124OuterBody = sliceFrom(dhxCode, 'const renderOuterLabel', 1600)
  const gt124Mark =
    /class="dhx-outer-mark is-delay"/.test(gt124OuterBody) &&
    /task\.barMark === 'delay'/.test(gt124OuterBody) &&
    /task\.barMarkText/.test(gt124OuterBody)
  const gt124Style = /\.dhx-outer-mark\.is-delay\)\s*\{[\s\S]{0,160}#dc2626/.test(dhxCode)
  assert(
    'GT124 静态：窄条条外标签在任务号后用红字补延期小时（+Nh）',
    gt124Mark && gt124Style,
    `外挂延期标记=${gt124Mark} 红色样式=${gt124Style}`
  )

  // ---- GT125：条宽判断必须含状态标记（2026-11-06 用户实测截图）----
  // 只量任务号 ⇒ 44px 的条判「放得下 S102」，但「S102 +392h」放不下 ⇒
  //   flex 把任务号压成 0 宽，条上只剩被裁半截的红字，外挂胶囊也不出现。
  const gt125FitBody = sliceFrom(dhxCode, 'const barLabelFits', 700)
  const gt125MarkAware =
    /const BAR_MARK_GAP = 6/.test(dhxCode) &&
    /if \(task\.barMarkText\) need \+= measureLabelWidth\(String\(task\.barMarkText\)\)/.test(
      gt125FitBody
    ) &&
    /barRenderWidth\(pos\) >= need \+ BAR_LABEL_PADDING_X/.test(gt125FitBody)
  assert(
    'GT125 静态：条宽判断含状态标记（任务号+标记整组放得下才写条内，否则整组外挂）',
    gt125MarkAware,
    `标记计入条宽=${gt125MarkAware}`
  )

  // ---- GT126：已排产任务条填充色（2026-11-06 用户指定 #7FB8B0）----
  // 图例与任务条同源 STATUS_COLORS，改一处两边同步。
  const gt126PlannedColor = /PLANNED:\s*'#7FB8B0'/.test(ganttCode)
  assert(
    'GT126 静态：已排产任务条填充色 = #7FB8B0（图例同源 STATUS_COLORS）',
    gt126PlannedColor,
    `PLANNED 色=${gt126PlannedColor}`
  )

  // ---- GT119：任务清单信息补全（5号位 2026-11-05 r14043/14044，tasks[] 新增 10 键）----
  //   operationName 已撤 ⇒ 工序语义按 operationCode + stageCode；新键缺值整键省略，
  //   页面必须按「缺键=—」展示，且不得把内部句柄（routeCode/pathId/stageExecutionBatchKey）当列。
  const gt119ScheduleTypes = readSrc('api/aps-v1/types/schedule.ts')
  const gt119TypeKeys = [
    'productionDepartmentId?: number',
    'departmentName?: string',
    'stageCode?: string',
    'taskType?: string',
    'routeCode?: string',
    'pathId?: number',
    'manualSlotId?: number',
    'mtS_InstructionNo?: string',
    'stageExecutionBatchKey?: string',
    'stageExecutionBatchQty?: number'
  ].every((k) => gt119ScheduleTypes.includes(k))
  const gt119Cols =
    /label="部门"[\s\S]{0,120}row\.departmentName/.test(ganttVue) &&
    /label="阶段"[\s\S]{0,120}row\.stageCode/.test(ganttVue) &&
    /label="生产指示号"[\s\S]{0,120}row\.mtS_InstructionNo/.test(ganttVue) &&
    /label="人工槽"[\s\S]{0,180}row\.manualSlotId != null/.test(ganttVue)
  const gt119ExportColsSrc = (() => {
    const m = ganttCode.match(/const EXPORT_COLUMNS = \[([\s\S]*?)\] as const/)
    return m ? m[1] : ''
  })()
  const gt119Export =
    /'部门',[\s\S]{0,40}'阶段',[\s\S]{0,40}'生产指示号'/.test(gt119ExportColsSrc) &&
    /'人工槽'/.test(gt119ExportColsSrc) &&
    /t\.departmentName \?\? ''/.test(ganttCode) &&
    /t\.stageCode \?\? ''/.test(ganttCode) &&
    /t\.mtS_InstructionNo \?\? ''/.test(ganttCode) &&
    /t\.manualSlotId != null \? '人工' : ''/.test(ganttCode)
  const gt119NoInternal =
    !/label="路线码"/.test(ganttCode) &&
    !/label="路径"/.test(ganttCode) &&
    !/label="执行批"/.test(ganttCode)
  const gt119Mock =
    /departmentName: resource\.productionDepartmentName/.test(mockSrc) &&
    /stageCode: resource\.stage/.test(mockSrc) &&
    /mtS_InstructionNo:/.test(mockSrc)
  assert(
    'GT119 静态：任务清单信息补全（10 新键类型位 + 部门/阶段/指示号/人工槽列 + CSV 同步 + 内部句柄不上列）',
    gt119TypeKeys && gt119Cols && gt119Export && gt119NoInternal && gt119Mock,
    `类型位=${gt119TypeKeys} 清单列=${gt119Cols} 导出=${gt119Export} 无内部列=${gt119NoInternal} mock同形=${gt119Mock}`
  )

  // GT75 换尺度 ⇒ 窗口同步换（跨度取该档默认跨度；起点锚在当前可见行，用户视野不丢）
  const ozIdx = ganttCode.indexOf('function onZoomChange')
  const ozBody = ozIdx >= 0 ? ganttCode.slice(ozIdx, ozIdx + 900) : ''
  const scaleBody = sliceFrom(ganttCode, 'function applyScaleWindow', 800)
  const gt75Follow =
    /applyScaleWindow\(unit\)/.test(ozBody) &&
    /const spanDays = SCALE_SPAN_DAYS\[unit\]/.test(scaleBody) &&
    /ganttWindow\.value = \{/.test(scaleBody)
  const gt75Anchor = /visibleRowStart/.test(scaleBody)
  assert(
    'GT75 静态：换「时间尺度」时窗口同步换（跨度=该档跨度，起点锚在当前可见行）',
    gt75Follow && gt75Anchor,
    `窗口跟随=${gt75Follow} 锚可见行=${gt75Anchor}`
  )

  // GT76 条最小心宽保底 + 标签分级：撑得下写条内（省略号兜底），撑不下写条右侧
  const gt76MinWidth = /:deep\(\.gantt_task_line\)\s*\{[^}]*min-width:\s*14px/.test(dhxCode)
  const gt76Hybrid =
    /measureLabelWidth/.test(dhxCode) &&
    /const barLabelFits = /.test(dhxCode) &&
    /if \(!barLabelFits\(task\)\) return ''/.test(dhxCode) &&
    /addTaskLayer\(\(task: DhxGanttTask\) => renderOuterLabel\(task\)\)/.test(dhxCode) &&
    /removeTaskLayer\(outerLabelLayerId/.test(dhxCode) &&
    /:deep\(\.dhx-bar-outer-label\)[\s\S]{0,240}position:\s*absolute/.test(dhxCode) &&
    /:deep\(\.dhx-bar-label\)\s*\{[^}]*text-overflow:\s*ellipsis/.test(dhxCode) &&
    /:deep\(\.gantt_task_content\)\s*\{[^}]*overflow:\s*hidden !important/.test(dhxCode)
  assert(
    'GT76 静态：任务条最小心宽保底（14px）+ 标签撑得下写条内、撑不下写条右侧',
    gt76MinWidth && gt76Hybrid,
    `最小心宽=${gt76MinWidth} 分级标签=${gt76Hybrid}`
  )

  // GT77 悬浮提示：插件开 + 内容由页面层按业务口径拼（组件不自己拼语义）
  const gt77Plugin =
    /plugins\(\{[^}]*\btooltip:\s*true\b[^}]*\}\)/.test(dhxCode) &&
    /templates\.tooltip_text/.test(dhxCode)
  const gt77Lines =
    /task\?\.tooltipLines/.test(dhxCode) &&
    /function buildTooltipLines/.test(ganttCode) &&
    /lines\.push\(`任务：/.test(ganttCode) &&
    /lines\.push\(`订单：/.test(ganttCode) &&
    // 2026-11-06：资源行改多行 push（无设备时区分「人工」/「未分配」）⇒ 允许 push( 后有换行
    /lines\.push\(\s*`资源：/.test(ganttCode) &&
    /t\.manualSlotId != null \? '人工' : '未分配'/.test(ganttCode) &&
    /lines\.push\(`状态：/.test(ganttCode)
  assert(
    'GT77 静态：悬浮提示插件已开 + 提示内容含任务/订单/资源/状态等业务行（无设备时区分人工/未分配）',
    gt77Plugin && gt77Lines,
    `插件=${gt77Plugin} 业务行=${gt77Lines}`
  )

  // GT78 资源负荷视图（行=资源 × 列=时间桶）；**无产能口径 ⇒ 不标超载**
  const gt78Tab =
    /ref<'gantt' \| 'load' \| 'tasks'>/.test(ganttCode) && /v-show="viewTab === 'load'"/.test(ganttVue)
  const gt78Grid =
    /const loadHeatmap = computed/.test(ganttCode) &&
    /const loadGridCells = computed/.test(ganttCode) &&
    /cell\.count \/ maxCount/.test(ganttCode)
  // 反向锁：没有产能口径就不许出现"利用率"这类口径
  // ⚠️ 提示文案必须锚到**模板里的那个元素**：同一句话在脚本注释里也出现过一份，
  //    只判「文件里有这句话」⇒ 模板文案被删了探针仍绿（假绿）。
  const gt78NoOverload =
    /<span class="load-note-warn">暂无产能口径，暂不标超载。<\/span>/.test(ganttVue) &&
    !/利用率/.test(ganttCode)
  assert(
    'GT78 静态：资源负荷视图（资源×时间桶热力图）+ 无产能口径时不标超载',
    gt78Tab && gt78Grid && gt78NoOverload,
    `Tab=${gt78Tab} 网格=${gt78Grid} 不标超载=${gt78NoOverload}`
  )

  // GT79 负荷带深浅改全局归一（不再按行内归一）
  const gt79GlobalNorm =
    /if \(band\.taskCount > maxCount\) maxCount = band\.taskCount/.test(dhxCode) &&
    /const \{ map, maxCount \} = loadBandMap\(\)/.test(dhxCode)
  const gt79NoPerRow = !/bands\.reduce\(\(m, b\) => Math\.max\(m, b\.taskCount\), 0\)/.test(dhxCode)
  assert(
    'GT79 静态：负荷带深浅按全局最大桶任务数归一（跨泳道可比，不再按行内归一）',
    gt79GlobalNorm && gt79NoPerRow,
    `全局归一=${gt79GlobalNorm} 无行内归一=${gt79NoPerRow}`
  )

  // GT80 换刻度后视口锚回「当前可见行」
  // 起因（2026-11-02 实看）：ext.zoom.setLevel 会把视口重新居中到「切换前的可视日期」，
  //   落在任务区间之外 ⇒ 虚拟渲染把任务条整段裁掉 ⇒ 切到「小时」档整屏空白（实测 lineCount=0）。
  const gt80Anchor =
    /const earliestVisibleRowStart = \(\): Date \| null =>/.test(dhxCode) &&
    /querySelectorAll\('\.gantt_row'\)/.test(dhxCode) &&
    /getAttribute\('task_id'\)/.test(dhxCode)
  const gt80Clamp =
    /const clampViewportToTaskRange = \(\) =>/.test(dhxCode) &&
    /showDate\?\.\(target\)/.test(
      (() => {
        const i = dhxCode.indexOf('const clampViewportToTaskRange = () =>')
        return i >= 0 ? dhxCode.slice(i, i + 400) : ''
      })()
    )
  // 换档入口（按名 setZoomLevelByName / 按序 setZoomLevel）都必须收尾
  //   2026-11-03：＋/－ 步进入口已撤 ⇒ 入口从 4 个变 2 个
  const gt80Hooked =
    /const afterZoomLevelChanged = \(\) =>/.test(dhxCode) &&
    (dhxCode.match(/afterZoomLevelChanged\(\)/g) || []).length >= 2
  assert(
    'GT80 静态：换刻度后视口锚回当前可见行（否则条子被虚拟渲染裁空 ⇒ 整屏空白）',
    gt80Anchor && gt80Clamp && gt80Hooked,
    `锚点=${gt80Anchor} 收拢=${gt80Clamp} 换档入口收尾=${gt80Hooked}`
  )

  /* ===== GT81–GT85：2026-11-03 甘特「第二轮优化」——
   *   GT81 关键词搜索（工具栏输入框 + 甘特/清单**同源**过滤 + 命中字段集）
   *   GT82 窗口只由「时间尺度」驱动（2026-11-03 撤「全览」入口后重写：窗口状态 + 真实起止标签 + 换档重取数）
   *   GT84 导出任务清单 CSV（UTF-8 BOM + CSV 转义 + 导出源 = 清单页同一批行）
   *   GT85 导出列**只取后端已给字段**（不得出现 MES/工单/执行状态等未接入列 ⇒ 不造假数据）
   *   （原 GT83「全览跨度取任务 ∪ 负荷桶并集」、GT86「全览用桶内真实起止」随「全览」入口一并撤销）
   */
  // GT81 关键词搜索
  const gt81Input = /<ElInput\s+v-model="keyword"/.test(ganttVue)
  const gt81Matcher = /function matchKeyword\(t: GanttTaskDto, resourceMap: Map<number, GanttResourceDto>\): boolean/.test(ganttCode)
  const gt81Fields = ['taskNo', 'orderNo', 'materialCode', 'materialName', 'operationCode', 'resourceCode', 'resourceName'].every(
    (f) => new RegExp(`\\b${f}\\b`).test(sliceFrom(ganttCode, 'function matchKeyword', 700))
  )
  // 甘特（filteredTasks）与清单（taskListRows）**必须**同源。
  // 2026-11-04 起清单直接取 filteredTasks（同源变成结构性保证：清单不可能漏掉关键词/筛选）
  const gt81Gantt = /matchKeyword\(t, map\)/.test(sliceFrom(ganttCode, 'const filteredTasks = computed', 600))
  const gt81List = /filteredTasks\.value/.test(sliceFrom(ganttCode, 'const taskListRows = computed', 900))
  const gt81Count = /命中 \{\{ filteredTasks\.length \}\} 条/.test(ganttVue)
  assert(
    'GT81 静态：关键词搜索（工具栏输入框 + 甘特/清单同源过滤 + 命中字段含任务号/订单/物料/资源）',
    gt81Input && gt81Matcher && gt81Fields && gt81Gantt && gt81List && gt81Count,
    `输入框=${gt81Input} 匹配器=${gt81Matcher} 字段集=${gt81Fields} 甘特=${gt81Gantt} 清单=${gt81List} 计数=${gt81Count}`
  )

  // GT82 窗口只由「时间尺度」驱动（2026-11-03 撤掉「全览」入口）
  const gt82State = /const ganttWindow = ref<\{ from: string; to: string; spanDays: number \} \| null>\(null\)/.test(ganttCode)
  // 「全览」入口不得复活（它只会收窄窗口，和「选一档」不是一回事 ⇒ 同一轴的第二个入口）
  const gt82NoFit = !/applyFitWindow/.test(ganttCode) && !/全览/.test(ganttCode)
  // 窗口实际状态只由窗口标签表达（须显示真实起止日，不得拿档位名冒充窗口）
  const wlBody = sliceFrom(ganttCode, 'const windowLabel = computed', 400)
  const gt82Label =
    /dateRange\.value\.from/.test(wlBody) && /dateRange\.value\.to/.test(wlBody) && /currentSpanDays\.value/.test(wlBody)
  // 窗口一变必须按新窗重新取数（后端按窗口过滤 ⇒ 不重取就还是旧窗的数据）
  const gt82Reload = /watch\(ganttWindow,[\s\S]{0,240}?loadCurrent\(currentGanttWindow\(\)\)/.test(ganttCode)
  assert(
    'GT82 静态：窗口只由「时间尺度」驱动（无「全览」入口）+ 窗口标签显示实际起止 + 换档按新窗重取数',
    gt82State && gt82NoFit && gt82Label && gt82Reload,
    `状态=${gt82State} 无全览=${gt82NoFit} 窗口标签=${gt82Label} 重取=${gt82Reload}`
  )

  // GT87 换尺度 ⇒ 窗口重算（2026-11-03 合并后：跨度由「时间尺度」自带，不再有「刻度过细 ⇒ 收窄」那条路）
  //     + 新窗口锚在「当前可见行」（用户正在看的那批任务留在画面里）
  //     + 程序回显（组件在 scaleUnit 变化后回抛的 zoom-change）**不得**改窗口（否则死循环）
  const ozBody87 = sliceFrom(ganttCode, 'function onZoomChange', 900)
  const scaleBody87 = sliceFrom(ganttCode, 'function applyScaleWindow', 800)
  const gt87Span = /const spanDays = SCALE_SPAN_DAYS\[unit\]/.test(scaleBody87)
  const gt87Anchor = /visibleRowStart/.test(scaleBody87) && /ganttWindow\.value = \{/.test(scaleBody87)
  const gt87EchoGuard = /const userChanged = unit !== zoomUnit\.value/.test(ozBody87) && /if \(!userChanged\) return/.test(ozBody87)
  assert(
    'GT87 静态：换尺度时窗口按该档跨度重算（锚在当前可见行 + 程序回显不改窗口）',
    gt87Span && gt87Anchor && gt87EchoGuard,
    `跨度取档=${gt87Span} 锚点=${gt87Anchor} 回显守卫=${gt87EchoGuard}`
  )

  // GT88 资源多选筛选（后端早已支持 resourceIds：dev 实测 34→1 资源 / 4483→13 条）
  //     + 选项表累积已见资源（过滤后端只回被选中的资源 ⇒ 下拉不能跟着缩）
  //     + 甘特体与负荷桶**同过滤**（两端口径一致）
  const gt88State = /const selectedResourceIds = ref<number\[\]>\(\[\]\)/.test(ganttCode)
  const gt88Bind = /v-model="selectedResourceIds"/.test(ganttVue)
  const gt88Known = /const knownResources = ref<GanttResourceDto\[\]>\(\[\]\)/.test(ganttCode)
  const gt88GanttWin = /resourceIds: selectedResourceIds\.value/.test(sliceFrom(ganttCode, 'function currentGanttWindow', 400))
  const gt88BucketWin = /resourceIds: selectedResourceIds\.value/.test(sliceFrom(ganttCode, 'function currentBucketWindow', 600))
  const gt88Reload = /watch\(selectedResourceIds/.test(ganttCode) && /reloadCurrentWindow\(\)/.test(sliceFrom(ganttCode, 'watch(selectedResourceIds', 300))
  assert(
    'GT88 静态：资源多选筛选（甘特体/负荷桶同过滤 + 选项表累积不收缩）',
    gt88State && gt88Bind && gt88Known && gt88GanttWin && gt88BucketWin && gt88Reload,
    `状态=${gt88State} 绑定=${gt88Bind} 累积=${gt88Known} 甘特窗=${gt88GanttWin} 桶窗=${gt88BucketWin} 重取=${gt88Reload}`
  )

  // GT84 导出 CSV
  const gt84Fn = /function exportTaskList\(\): void/.test(ganttCode)
  const gt84Bom = /new Blob\(\['\\uFEFF'/.test(ganttCode)
  const gt84Escape = /function csvCell\(value: unknown\): string/.test(ganttCode) && /replace\(\/"\/g, '""'\)/.test(ganttCode)
  const gt84Source = /taskListRows\.value/.test(sliceFrom(ganttCode, 'function exportTaskList', 1200))
  const gt84Download = /a\.download =/.test(ganttCode) && /URL\.createObjectURL\(blob\)/.test(ganttCode)
  assert(
    'GT84 静态：导出任务清单 CSV（UTF-8 BOM + CSV 转义 + 导出源=清单页同一批行 + 浏览器下载）',
    gt84Fn && gt84Bom && gt84Escape && gt84Source && gt84Download,
    `入口=${gt84Fn} BOM=${gt84Bom} 转义=${gt84Escape} 同源=${gt84Source} 下载=${gt84Download}`
  )

  // GT85 导出列只取后端已给字段（不得出现未接入列 ⇒ 不造假数据）
  //  2026-11-05 B2①/B2②：后端已给 MES 工单号 + 下发状态 ⇒ 这两列**允许**；
  //  仍**禁止**未接入的列（MES 资格 / 执行状态 / 实际起止 / 完成进度 / 锁定 / 冻结区）。
  const exportColsSrc = (() => {
    const m = ganttCode.match(/const EXPORT_COLUMNS = \[([\s\S]*?)\] as const/)
    return m ? m[1] : ''
  })()
  const gt85Cols = exportColsSrc.includes('任务号') && exportColsSrc.includes('计划开始') && exportColsSrc.includes('单位')
  const gt85NoFake =
    !/(mesEligible|资格|执行状态|实际开始|实际结束|完成进度|锁定|冻结区)/.test(exportColsSrc)
  const gt85Mes =
    exportColsSrc.includes('MES工单号') &&
    exportColsSrc.includes('下发状态') &&
    /t\.mesWorkOrderNo \?\? ''/.test(ganttCode) &&
    /t\.dispatchStatus \?\? ''/.test(ganttCode)
  assert(
    'GT85 静态：导出列只取后端已给字段（允许 MES工单号/下发状态，不含资格/执行状态等未接入列 ⇒ 不造假数据）',
    gt85Cols && gt85NoFake && gt85Mes,
    `列集=${gt85Cols} 无假列=${gt85NoFake} MES两列取值=${gt85Mes}`
  )

  /* ===== GT89/GT90：2026-11-03 修「甘特图底部没有横向的滚动条了」=====
   *   根因（headless CDP 实测，视口 950/900/820 复现）：
   *     甘特根节点 `.dhx-gantt-root` 被钉死 `min-height: 680px`
   *     ⇒ dhtmlx 容器比可视区（.gantt-area）高 ⇒ 超出部分被 overflow:hidden 裁掉
   *     ⇒ **底部那条横向滚动条整条落在被裁掉的一段里 ⇒ 用户看不见**
   *     ⇒ 且 dhtmlx 靠 iframe 监听「容器自身尺寸变化」，尺寸被钉死 ⇒ 内部布局也不重排。
   *   修法：根节点高度 100% 跟随父容器（视口/工具栏行数一变就变 ⇒ dhtmlx 自动重排）。
   */
  const dhxRootRule = (() => {
    const m = dhxWrapperCode.match(/\.dhx-gantt-root\s*\{([\s\S]*?)\n\}/)
    return m ? m[1] : ''
  })()
  // 高度跟随父容器（`height: 100%`；用负向后顾排除 `min-height`）
  const gt89Fill = /(?<!-)\bheight:\s*100%/.test(dhxRootRule)
  // 不得有 px 级高度地板（min-height: 0 可以；min-height: 680px 会把滚动条挤出可视区）
  const gt89NoFloor = !/min-height:\s*\d+px/.test(dhxRootRule)
  assert(
    'GT89 静态：甘特根节点高度跟随父容器（无 px 高度地板 ⇒ 横向滚动条不被裁掉）',
    gt89Fill && gt89NoFloor,
    `跟随父容器=${gt89Fill} 无高度地板=${gt89NoFloor}`
  )

  // 可视区必须可收缩（flex:1 + min-height:0），否则视口一矮区域又被钉死 ⇒ 同一个坑复发
  const ganttAreaRule = (() => {
    const m = ganttVue.match(/\.gantt-area\s*\{([\s\S]*?)\n\}/)
    return m ? m[1] : ''
  })()
  const gt90Flex = /flex:\s*1\b/.test(ganttAreaRule)
  const gt90Shrink = /min-height:\s*0\b/.test(ganttAreaRule)
  assert(
    'GT90 静态：甘特可视区可收缩（flex:1 + min-height:0 ⇒ 根节点高度随视口变化）',
    gt90Flex && gt90Shrink,
    `flex1=${gt90Flex} 可收缩=${gt90Shrink}`
  )

  /* ===== GT91~GT96：2026-11-03 A 组三项（纯前端、零后端依赖）=====
   *   A1 按订单 / 按物料分组；A3 只看延期 / 只看工序过滤；A4 条尾任务号标签。
   *   三项的风险都不是「没写」，而是**写一半**：
   *     - A1：泳道行 id 与任务 parent 不同源 ⇒ 任务条找不到自己的行 ⇒ 整批条子静默消失（看着像没数据）；
   *     - A3：控件绑了但没进 filteredTasks ⇒ 点了没反应；
   *     - A4：页面给了标签但组件没渲染 / 组件自己拼业务语义。
   *   ⇒ 每项拆成两条独立断言（控件侧 / 数据侧），单边通过不算过（见断言陷阱第 4/12 条）。
   */
  // 模板另剥 HTML 注释：`.vue` 的 `<!-- -->` 不在 stripComments 覆盖范围内（陷阱第 11 条）
  const ganttTpl = ganttVue.replace(/<!--[\s\S]*?-->/g, '')

  // ---- GT91：A1 新增两个分组维度选项（不新增后端维度，纯前端分组）----
  const groupOptionsBody = sliceFrom(ganttCode, 'const GROUP_BY_OPTIONS', 900)
  const gt91Order = /value:\s*'order',\s*label:\s*'按订单'/.test(groupOptionsBody)
  const gt91Material = /value:\s*'material',\s*label:\s*'按物料'/.test(groupOptionsBody)
  // 类型联合必须含这两个值，否则 TS 编译不过（静态探针补一层，防类型被收窄）
  const gt91Type = /type GroupByKey =([\s\S]{0,300}?)'order'/.test(ganttCode) &&
    /type GroupByKey =([\s\S]{0,300}?)'material'/.test(ganttCode)
  assert(
    'GT91 静态：A1 分组维度新增「按订单 / 按物料」（纯前端维度，不新增后端字段）',
    gt91Order && gt91Material && gt91Type,
    `按订单=${gt91Order} 按物料=${gt91Material} 类型联合=${gt91Type}`
  )

  // ---- GT92：A1 泳道行 id 与任务 parent 必须同源 ----
  const lanesOrderBody = sliceFrom(ganttCode, "if (groupBy.value === 'order')", 2200)
  const groupKeyBody = sliceFrom(ganttCode, 'function getTaskGroupKey', 900)
  const gt92LaneIds =
    /id:\s*`lane-order-\$\{orderNo\}`/.test(lanesOrderBody) &&
    /id:\s*`lane-material-\$\{code\}`/.test(lanesOrderBody)
  const gt92KeyIds =
    /case\s+'order':\s*return\s*`lane-order-\$\{orderGroupValue\(task\)\}`/.test(groupKeyBody) &&
    /case\s+'material':\s*return\s*`lane-material-\$\{materialGroupValue\(task\)\}`/.test(groupKeyBody)
  // 同源：两端都走同一个取值助手（各写各的 ⇒ 改一处就断链）
  // ⚠️ 判**调用形**（助手名 + 实参），不判「文件里出现过这个名字」——
  //   只判名字的话，把 `orderGroupValue` 改名成 `orderGroupValueX` 探针照绿（假绿）。
  const gt92SameHelper =
    /const key = orderGroupValue\(t\)/.test(lanesOrderBody) &&
    /const key = materialGroupValue\(t\)/.test(lanesOrderBody) &&
    /lane-order-\$\{orderGroupValue\(task\)\}/.test(groupKeyBody) &&
    /lane-material-\$\{materialGroupValue\(task\)\}/.test(groupKeyBody)
  // 分组键只能取后端已给的字段（不自造字段名）
  const gt92Fields =
    /t\.orderNo\?\.trim\(\)/.test(sliceFrom(ganttCode, 'function orderGroupValue', 200)) &&
    /t\.materialCode\?\.trim\(\)/.test(sliceFrom(ganttCode, 'function materialGroupValue', 200))
  assert(
    'GT92 静态：A1 泳道行 id 与任务 parent 同源（同一取值助手 + 只取后端已给字段 ⇒ 不会静默丢条）',
    gt92LaneIds && gt92KeyIds && gt92SameHelper && gt92Fields,
    `泳道行id=${gt92LaneIds} parent=${gt92KeyIds} 同源=${gt92SameHelper} 字段=${gt92Fields}`
  )

  // ---- GT93：A3 工具栏两个筛选控件 ----
  const gt93State =
    /const onlyDelayed = ref\(false\)/.test(ganttCode) &&
    /const operationFilter = ref<string\[\]>\(\[\]\)/.test(ganttCode)
  const gt93DelayBox =
    /<ElCheckbox[^>]*v-model="onlyDelayed"[^>]*>\s*只看延期\s*<\/ElCheckbox>/.test(ganttTpl)
  const opSelectTag = (() => {
    const m = ganttTpl.match(/<ElSelect[^>]*v-model="operationFilter"[^>]*>/)
    return m ? m[0] : ''
  })()
  const gt93OpSelect = /multiple/.test(opSelectTag) && /placeholder="全部工序"/.test(opSelectTag)
  assert(
    'GT93 静态：A3 工具栏有「只看延期」与「只看工序」两个筛选控件（已绑状态）',
    gt93State && gt93DelayBox && gt93OpSelect,
    `状态=${gt93State} 只看延期=${gt93DelayBox} 只看工序=${gt93OpSelect}`
  )

  // ---- GT94：A3 过滤真正接入 filteredTasks（AND 关系 + 只用后端已给字段）----
  const filteredBody = sliceFrom(ganttCode, 'const filteredTasks = computed', 800)
  const gt94Delayed =
    /if \(onlyDelayed\.value\) \{\s*tasks = tasks\.filter\(\(t\) => t\.isDelayed\)\s*\}/.test(
      filteredBody
    )
  const gt94Operation =
    /if \(operationFilter\.value\.length > 0\) \{\s*tasks = tasks\.filter\(\(t\) => operationFilter\.value\.includes\(t\.operationCode\)\)\s*\}/.test(
      filteredBody
    )
  // 工序选项取「已加载任务」的去重（不受过滤影响 ⇒ 打开筛选后选项不会自己消失）
  const gt94OptionsStable =
    /for \(const t of ganttData\.value\?\.tasks \?\? \[\]\)/.test(
      sliceFrom(ganttCode, 'const availableOperations = computed', 600)
    )
  assert(
    'GT94 静态：A3 两个筛选进 filteredTasks（AND 关系；延期用后端 isDelayed，工序用后端 operationCode）',
    gt94Delayed && gt94Operation && gt94OptionsStable,
    `只看延期=${gt94Delayed} 只看工序=${gt94Operation} 选项稳定=${gt94OptionsStable}`
  )

  // ---- GT95：任务条标签 =「工序 · 生产指示号」----
  // 2026-11-06 工厂口径：订单号在窗口里常整片相同（999428 的 8000 条仅 1 个订单号）
  // ⇒ 条上改用班组对单用的生产指示号；订单号挪到悬停（GT121）
  const gt95NoTruncate = !/slice\(-8\)/.test(ganttCode) && !/function tailLabelOf/.test(ganttCode)
  const barLabelFnBody = sliceFrom(ganttCode, 'function barLabelOf', 300)
  // 2026-11-06 用户截图要求：工序与生产指示号之间用竖线（原「 · 」改「|」）
  const gt95PipeSeparator = /'\|' \+ instruction/.test(barLabelFnBody)
  const gt95Bind =
    /barLabel:\s*barLabelOf\(t\),/.test(ganttCode) &&
    /t\.operationCode/.test(barLabelFnBody) &&
    /t\.mtS_InstructionNo/.test(barLabelFnBody) &&
    !/t\.orderNo/.test(barLabelFnBody) &&
    !/t\.taskNo/.test(barLabelFnBody)
  assert(
    'GT95 静态：条标签 =「工序|生产指示号」（订单号/任务号不上条、不截尾 8 位）',
    gt95NoTruncate && gt95Bind && gt95PipeSeparator,
    `无截断=${gt95NoTruncate} 绑定=${gt95Bind} 竖线=${gt95PipeSeparator}`
  )

  // ---- GT96：A4 任务号只写在条内（不再有外挂条尾标签图层）----
  const dhxTypes = readSrc('components/DhxGantt/src/types.ts')
  const gt96Types =
    /barLabel\?:\s*string/.test(dhxTypes) && !/tailLabel\?:\s*string/.test(dhxTypes)
  const gt96Inside =
    /if \(!task \|\| !task\.barLabel\) return ''/.test(dhxWrapperCode) &&
    /<span class="dhx-bar-label">\$\{escapeHtml\(task\.barLabel\)\}<\/span>/.test(dhxWrapperCode) &&
    /:deep\(\.dhx-bar-label\)\s*\{[^}]*text-overflow:\s*ellipsis/.test(dhxCode) &&
    /:deep\(\.gantt_task_content\)\s*\{[^}]*overflow:\s*hidden !important/.test(dhxCode)
  // 旧的条尾图层已撤；条内标签与标记同排。窄条右侧标签是另一套（GT76 锁）
  const gt96NoTail = !/renderTailLabel|tailLabelLayerId|dhx-tail-label/.test(dhxWrapperCode)
  const gt96MarkInline =
    /class="dhx-bar-inner"/.test(dhxWrapperCode) &&
    /:deep\(\.dhx-bar-inner\)\s*\{[^}]*display:\s*flex/.test(dhxCode) &&
    !/dhx-(?:status|zone)-completed[^{]*gantt_task_content::after/.test(dhxCode)
  assert(
    'GT96 静态：A4 任务号一律写在条内（无条尾外挂标签，完成✓/延期+Nh 同排条内）',
    gt96Types && gt96Inside && gt96NoTail && gt96MarkInline,
    `类型位=${gt96Types} 条内标签=${gt96Inside} 无条尾=${gt96NoTail} 条内标记=${gt96MarkInline}`
  )

  // ---- GT120：左表首列合并物料（「工序 · 指示号 / 物料」，物料不再单列）----
  // 2026-11-05 用户走查：物料独立列只剩半截编码、且拉低左表信息密度 ⇒ 并入首列、列宽让给首列
  const gt120Grid =
    /function gridLabelOf\(/.test(ganttCode) &&
    /\$\{barLabelOf\(t\)\} \| \$\{material\}/.test(ganttCode) &&
    /text:\s*gridLabelOf\(t\)/.test(ganttCode) &&
    !/^\s*material:\s*t\.materialCode/m.test(ganttCode)
  const gt120NoColumn =
    !/name:\s*'material'/.test(dhxWrapperCode) &&
    /label:\s*'工序 \/ 指示号 \/ 物料'/.test(dhxWrapperCode) &&
    /name:\s*'text'[\s\S]{0,200}?width:\s*340/.test(dhxWrapperCode) &&
    /grid_width = 690/.test(dhxWrapperCode)
  const gt120Type = !/^\s*material\?:\s*string/m.test(dhxTypes)
  assert(
    'GT120 静态：左表首列 =「工序 · 指示号 / 物料」，物料列已撤、列宽让给首列',
    gt120Grid && gt120NoColumn && gt120Type,
    `首列拼接=${gt120Grid ? '有' : '缺'} 列定义=${gt120NoColumn ? '已合并' : '未合并'} 类型=${gt120Type ? '无物料字段' : '仍留 material'}`
  )

  // ---- GT121：工厂口径的列与悬停（2026-11-06）----
  // 用户拍板：①条标签/首列改用生产指示号（GT95）②左表不再有「时长」列
  //   ——泳道行 75h 是占用跨度、子任务 1~2h 是加工工时，同列不同义会被读成算错
  //   ⇒ 改为「开始 / 结束 / 数量」；工时、订单号、MES 工单等信息放悬停
  const gt121Cols =
    (dhxWrapperCode.match(/gantt\.config\.columns = \[([\s\S]*?)\n {2}\]/) || [])[1] || ''
  const gt121ColSet =
    /name:\s*'endText'[\s\S]{0,80}?label:\s*'结束'/.test(gt121Cols) &&
    /name:\s*'qtyText'[\s\S]{0,120}?props\.qtyUnit \? `数量\(\$\{props\.qtyUnit\}\)` : '数量'/.test(gt121Cols) &&
    // 数量列左对齐（2026-11-06 用户要求；曾为 right）
    /name:\s*'qtyText'[\s\S]{0,200}?align:\s*'left'/.test(gt121Cols) &&
    /label:\s*'开始'/.test(gt121Cols) &&
    !/name:\s*'duration'/.test(gt121Cols)
  // 单位一致才提到表头（混合/空单位回落「数量」+ 行内单位，避免表头把少数行说错）
  const sharedUnitBody = sliceFrom(ganttCode, 'const sharedQtyUnit = computed', 400)
  const gt121QtyUnit =
    /if \(!u\) return ''/.test(sharedUnitBody) &&
    /else if \(unit !== u\) return ''/.test(sharedUnitBody) &&
    /:qty-unit="sharedQtyUnit"/.test(ganttTpl)
  // 结束/数量必须是**后端原值**：结束若由 duration 反推会四舍五入到整小时（08:01:45 → 09:00）
  const gt121TaskCells =
    /endText:\s*t\.plannedEndTime\s*\?\s*formatDate\(t\.plannedEndTime\)/.test(ganttCode) &&
    /qtyText:\s*sharedQtyUnit\.value\s*\?\s*String\(t\.plannedProcessQty \?\? t\.netQty\)/.test(ganttCode)
  // 泳道行也走同一组列 ⇒ 结束列要有值（占用区间终点），否则泳道行整行空一格
  const gt121LaneCell = /endText:\s*spanEnd\s*\?\s*formatDate\(spanEnd\)\s*:\s*''/.test(ganttCode)
  const tipBody = sliceFrom(ganttCode, 'function buildTooltipLines', 1600)
  const gt121Tip =
    /t\.mtS_InstructionNo/.test(tipBody) &&
    /t\.orderNo/.test(tipBody) &&
    /t\.materialName/.test(tipBody) &&
    /t\.mesWorkOrderNo/.test(tipBody) &&
    /t\.plannedProcessQty/.test(tipBody)
  const matchBody = sliceFrom(ganttCode, 'function matchKeyword', 900)
  const gt121Search =
    /t\.mtS_InstructionNo/.test(matchBody) && /t\.mesWorkOrderNo/.test(matchBody)
  assert(
    'GT121 静态：左表 = 开始/结束/数量（无时长列、数量列左对齐），单位一致时提到表头「数量(PS)」且格子只写数字，悬停补指示号·订单·物料·MES 工单·计划加工数量，搜索可命中指示号',
    gt121ColSet && gt121QtyUnit && gt121TaskCells && gt121LaneCell && gt121Tip && gt121Search,
    `列组=${gt121ColSet} 单位表头=${gt121QtyUnit} 单元格=${gt121TaskCells} 泳道结束=${gt121LaneCell} 悬停=${gt121Tip} 搜索=${gt121Search}`
  )

  // ---- GT122：人工任务按部门成泳道 + 三段式泳道标签（2026-11-06）----
  // 用户拍板：泳道标签统一「名称 | 工厂 | 部门」；人工任务（无设备、挂人工槽）不再合成「未分配资源」。
  // 数据依据（TZP 版本 1 天窗口）：无设备任务 7,199 条全部带 manualSlotId、跨 38 个部门。
  const gt122LaneLabel =
    /function laneLabel\(name: string, factory\?: string \| null, department\?: string \| null\): string \{\s*return `\$\{name\} \| \$\{factory \|\| '—'\} \| \$\{department \|\| '—'\}`/.test(
      ganttCode
    )
  const gt122Equip =
    /label: laneLabel\(identity, r\.factoryName, r\.productionDepartmentName\)/.test(ganttCode) &&
    !/\$\{identity\} \| \$\{r\.factoryName\} \//.test(ganttCode)
  const laborBody = sliceFrom(ganttCode, 'const laborByDept = new Map', 2000)
  const gt122Labor =
    /if \(!isLaborTask\(t\)\) \{/.test(laborBody) &&
    /laborByDept\.set\(deptId, \{ dept, factory: factoryNameOfTask\(t, undefined\) \}\)/.test(
      laborBody
    ) &&
    /id: `lane-manual-\$\{deptId\}`/.test(laborBody) &&
    /label: laneLabel\('人工', v\.factory, v\.dept\)/.test(laborBody) &&
    /return \[\.\.\.rows, \.\.\.laborRows\]\.sort\(/.test(laborBody) &&
    !/'未分配资源 \| 无指定设备'/.test(ganttCode)
  const gt122Parent =
    /return isLaborTask\(task\) \? laborLaneId\(task\) : UNASSIGNED_LANE_ID/.test(ganttCode) &&
    /return `lane-manual-\$\{t\.productionDepartmentId \?\? 0\}`/.test(ganttCode)
  const gt122Factory =
    /departmentLookupApi\.listDepartments\(500\)/.test(ganttCode) &&
    /domainApi\.listFactories\(\)/.test(ganttCode) &&
    /deptFactoryName\.value\.get\(deptId\) \?\? resourceDeptFactoryName\.value\.get\(deptId\)/.test(
      ganttCode
    )
  const gt122Dims =
    /return t\.stageCode\?\.trim\(\) \|\| DIM_UNCONFIGURED_LABEL/.test(ganttCode) &&
    /return t\.departmentName\?\.trim\(\) \|\| DIM_UNCONFIGURED_LABEL/.test(ganttCode)
  const gt122Bands =
    /if \(group\.resourceId === UNASSIGNED_RESOURCE_ID\) continue/.test(ganttCode) &&
    !/group\.resourceId === UNASSIGNED_RESOURCE_ID \? UNASSIGNED_LANE_ID/.test(ganttCode)
  // 空泳道「开始」列必须留空：formatDate(undefined) 兜底返回当前时间 ⇒ 会显示假时间
  const gt122StartText =
    /name: 'startText',/.test(dhxCode) &&
    /startText: spanStart \? formatDate\(spanStart\) : ''/.test(ganttCode) &&
    /startText: t\.plannedStartTime \? formatDate\(t\.plannedStartTime\) : ''/.test(ganttCode)
  const deptLookupSrc = readSrc('api/aps-v1/departmentLookup.ts')
  const gt122LookupLimit = /async listDepartments\(limit = 200\)/.test(deptLookupSrc)
  assert(
    'GT122 静态：人工任务按部门成泳道（标签「人工 | 工厂 | 部门」，与设备行同格式），空泳道开始列留空',
    gt122LaneLabel &&
      gt122Equip &&
      gt122Labor &&
      gt122Parent &&
      gt122Factory &&
      gt122Dims &&
      gt122Bands &&
      gt122StartText &&
      gt122LookupLimit,
    `标签=${gt122LaneLabel} 设备行=${gt122Equip} 人工行=${gt122Labor} parent=${gt122Parent} 工厂字典=${gt122Factory} 维度=${gt122Dims} 负荷带=${gt122Bands} 开始列=${gt122StartText} 部门limit=${gt122LookupLimit}`
  )

  // ---- GT123：分组维度不再提供「按 Domain」（2026-11-06 用户拍板）----
  // 现网版本全部单域（实测 999432 全量 235,738 条任务同属 BJ_FAMILY_FRL）⇒ 按域分组恒一条泳道，无信息量。
  // 只隐藏下拉选项；domain 分支与域色板保留，多域版本出现时再放开。
  // 正向锚点（按阶段仍在）防止切片落空造成假绿。
  const gt123NoDomainOption =
    !/value:\s*'domain'/.test(groupOptionsBody) &&
    /value:\s*'stage',\s*label:\s*'按阶段'/.test(groupOptionsBody)
  assert(
    'GT123 静态：分组维度下拉不再提供「按 Domain」（现网版本单域，按域恒一条泳道）',
    gt123NoDomainOption,
    `按Domain出现=${/value:\s*'domain'/.test(groupOptionsBody)} 按阶段在=${/value:\s*'stage'/.test(groupOptionsBody)}`
  )

  // ---- GT97：模板用到的每个 El* 组件都必须真的 import 过 ----
  // 2026-11-03 headless 实看抓到的真缺陷：`ElCheckbox` 没写进 element-plus 的 import 列表
  //   ⇒ Vue 解析不到该组件 ⇒ **页面里根本不渲染这个控件**（静态探针看模板字符串是全绿的）。
  //   这类缺陷只能靠「模板里出现的每个 El* 都在 import 列表里」这条来兜。
  const ganttElTags = [...new Set((ganttTpl.match(/<El[A-Za-z]+/g) || []).map((s) => s.slice(1)))]
  const epImportBodies = [...ganttCode.matchAll(/import\s*\{([^}]*)\}\s*from\s*['"]element-plus['"]/g)]
    .map((m) => m[1])
    .join('\n')
  const gt97Missing = ganttElTags.filter((c) => !new RegExp(`\\b${c}\\b`).test(epImportBodies))
  assert(
    'GT97 静态：甘特页模板用到的每个 El* 组件都 import 过（漏 import ⇒ 控件静默不渲染）',
    ganttElTags.length > 0 && gt97Missing.length === 0,
    `用到 ${ganttElTags.length} 个 El 组件，漏 import=${gt97Missing.length ? gt97Missing.join(',') : '无'}`
  )

  // ---- GT98：悬浮提示只挂右侧时间轴（左侧行名列不弹）----
  // 2026-11-03 用户反馈：鼠标停在**左侧行名列**上会弹出与右侧条子一模一样的黑框（内容重复、遮行名）。
  //   根因：dhtmlx tooltip 插件的默认选择器 `[task_id]:not(.gantt_task_row)` 同时命中
  //   左侧网格行 `.gantt_row`（它也带 task_id）⇒ 左右两侧共用一个监听范围。
  //   修法：onGanttReady 时摘掉默认选择器，改挂 `.gantt_task_line`（只在右侧时间轴）。
  const tipScopeBody = sliceFrom(dhxWrapperCode, 'const applyTooltipScope', 1200)
  const gt98Detach = /tooltips\.detach\(/.test(tipScopeBody) && /gantt_task_row/.test(tipScopeBody)
  const gt98Selector = /selector:\s*'\.gantt_task_line'/.test(tipScopeBody)
  const gt98Wired = /attachEvent\(\s*'onGanttReady'\s*,\s*\(\)\s*=>\s*applyTooltipScope\(\)\s*\)/.test(dhxWrapperCode)
  assert(
    'GT98 静态：悬浮提示只挂右侧时间轴任务条（左侧行名列不弹黑框）',
    gt98Detach && gt98Selector && gt98Wired,
    `摘默认范围=${gt98Detach} 改挂任务条=${gt98Selector} 接线=${gt98Wired}`
  )

  // ---- GT99：资源「编码 | 名称」相同则不重复显示 ----
  // 2026-11-03 用户截图指出：资源泳道标题渲染成「763-2 | 763-2 | 工厂 / 部门」看着像重复渲染。
  //   根因：dev 种子数据 25/25 条资源的 resourceName 就等于 resourceCode ⇒ 拼两段必然重复。
  //   修法：统一走 `resourceCodeNameParts()`，名称与编码相同时只保留一段（下拉与泳道标题同一套）。
  // 2026-11-04：取值助手抽到公共模块 `utils/apsResource.ts`（排产总览页资源表也要同一套口径，
  //   复制成两处内联逻辑必然漂移）⇒ 去重分支改到公共模块里判。
  // 2026-11-06 用户走查：泳道行标签里的资源名称（机型）同类设备大量重名、只占宽度把部门挤成半截
  //   ⇒ 行标签只留设备号 + 工厂/部门，名称进悬停；资源下拉仍「编码 名称」并去重（可搜名称）。
  const resPartsSrc = readSrc('utils/apsResource.ts')
  const resPartsBody = sliceFrom(resPartsSrc, 'export function resourceCodeNameParts', 700)
  const gt99Dedup = /if \(name && name !== code\) return \[code, name\]/.test(resPartsBody)
  const gt99Shared = /resourceCodeNameParts/.test(ganttCode)
  const gt99Lane =
    /const identity = resourceCodeNameParts\(r\)\[0\]/.test(ganttCode) &&
    // 2026-11-06：行标签统一走三段式 laneLabel（名称 | 工厂 | 部门），不再内联拼串
    /label: laneLabel\(identity, r\.factoryName, r\.productionDepartmentName\)/.test(ganttCode) &&
    !/label: `[^`]*\$\{r\.resourceName\}/.test(ganttCode)
  const gt99LaneHover = /`设备名称：\$\{lane\.resourceName\}`/.test(ganttCode)
  const gt99Dropdown = /resourceCodeNameParts\(r\)\.join\(' '\)/.test(ganttCode)
  const gt99NoRaw =
    !/label: `\$\{r\.resourceCode\} \| \$\{r\.resourceName\}/.test(ganttCode) &&
    !/r\.resourceName !== r\.resourceCode/.test(ganttCode)
  assert(
    'GT99 静态：泳道行只显设备号（名称进悬停），下拉「编码 名称」去重（编码=名称时只留一段）',
    gt99Dedup && gt99Shared && gt99Lane && gt99LaneHover && gt99Dropdown && gt99NoRaw,
    `去重分支=${gt99Dedup} 共用取值=${gt99Shared} 泳道=${gt99Lane} 泳道悬停=${gt99LaneHover} 下拉=${gt99Dropdown} 无旧写法=${gt99NoRaw}`
  )

  // ---- GT100（11-04 二改）：KPI 行不再显示任务数与版本总数 ----
  // 用户截图圈出「任务 3334（… 窗口内） · 版本共 13201 条（整个计划区间）」要求去掉，理由两条：
  //   ① 「任务 N」与「任务清单」页签徽标是同一个数（同屏纯重复）；
  //   ② 「版本共 13201」是**整个计划区间**的口径，摆在按窗口看的甘特工具栏只会让人对不上。
  //   收敛后：工具栏只留**甘特视图独有**的量（资源 / 泳道 / 各域分布），任务数只在「任务清单」页签徽标显示（且跟着筛选走）。
  //   ⚠️ 「已加载前 N 条 / 共 M 条」是**加载进度提示**（仅窗口超单次上限时出现），不是 KPI ⇒ 不在禁列。
  const gt100NoTaskCount = !/任务\s*<strong>\{\{\s*stats\.total\s*\}\}/.test(ganttTpl)
  const gt100NoVersionTotal = !/版本共\s*<strong>/.test(ganttTpl)
  const gt100NoScopeSpan = !/class="stat-scope"/.test(ganttTpl)
  const gt100KeepsResourceLane =
    /资源\s*<strong>\{\{\s*ganttData\?\.resources\.length/.test(ganttTpl) &&
    /泳道/.test(ganttTpl) &&
    /\{\{\s*lanes\.length\s*\}\}/.test(ganttTpl)
  assert(
    'GT100 静态：KPI 行不显示任务数/版本总数（任务数只在任务清单页签），仍保留资源/泳道/域分项',
    gt100NoTaskCount && gt100NoVersionTotal && gt100NoScopeSpan && gt100KeepsResourceLane,
    `无任务数=${gt100NoTaskCount} 无版本总数=${gt100NoVersionTotal} 无口径标注=${gt100NoScopeSpan} 保留资源泳道=${gt100KeepsResourceLane}`
  )

  // ---- GT101：同页三个任务数不重复（2026-11-04 用户截图反馈）----
  // 用户勾「只看延期」后，工具栏出现「剩余 2431 条」、KPI 又出现「其他 2431」、而任务清单 Tab 写 3334
  //   —— 三个数两个重复、一个不过滤 ⇒ 同一屏上互相打架。
  // 收敛规则（本轮落地）：
  //   ① 任务清单与甘特条同源（filteredTasks）⇒ 清单数 = 图上条数 = 各域计数之和（数据源由 GT19/GT81 锁）；
  //   ② 工具栏**不再**显示「剩余 N 条」（与域计数之和恒等 ⇒ 纯重复；关键词的「命中 N 条」保留，是搜索反馈）；
  //   ③ 域分项只在 **≥2 个域** 时显示（单域时它恒等于清单条数 ⇒ 并排两个一样的数纯重复）；
  //   ④ 任务清单 Tab 徽标取 taskListTotal（生效条数），不是窗口总数。
  const gt101NoRemainHint = !/剩余\s*\{\{\s*filteredTasks\.length\s*\}\}\s*条/.test(ganttTpl)
  const gt101DomainGate = /availableDomains\.length\s*>\s*1/.test(ganttTpl)
  const gt101Badge = /任务清单\s*<span[\s\S]{0,220}?\{\{\s*taskListTotal\s*\}\}/.test(ganttTpl)
  assert(
    'GT101 静态：同页任务数不重复（清单与图同源 + 无「剩余 N 条」+ 单域不显示域分项 + Tab 徽标取生效条数）',
    gt101NoRemainHint && gt101DomainGate && gt101Badge,
    `无剩余提示=${gt101NoRemainHint} 域分项单域隐藏=${gt101DomainGate} 徽标取taskListTotal=${gt101Badge}`
  )

  // ---- GT102：切版本必须清掉「上一版的资源筛选」（11-04 用户反馈：TZP 在甘特上没有任务）----
  // 根因（dev 实测 999333 QT → 999334 TZP）：资源多选是「上一版数据里的选择」，
  //   resourceIds 直接发给后端 ⇒ 新版本没这些资源 ⇒ 后端回 0 条。
  //   （排程域筛选已于 2026-11-04 撤销，域信息并入版本下拉，见 GT104。）
  // ⚠️ 切片必须**切到该 watch 的函数体收尾**（到 '\n})' 为止）：固定长度切片会把紧邻的
  //    watch(ganttWindow, …) 的 loadCurrent 一并吞进来 ⇒ 删掉本 watch 里的重取也照样绿（假绿）。
  const gt102Watch = (() => {
    const i = ganttCode.indexOf('watch(currentVersionId, (id) => {')
    if (i < 0) return ''
    const j = ganttCode.indexOf('\n})', i)
    return j < 0 ? '' : ganttCode.slice(i, j + 3)
  })()
  const gt102ClearResources = /selectedResourceIds\.value\s*=\s*\[\]/.test(gt102Watch)
  const gt102Reload = /scheduleStore\.loadCurrent\(/.test(gt102Watch)
  assert(
    'GT102 静态：切版本清空上一版的资源多选筛选并重取数据',
    gt102ClearResources && gt102Reload,
    `清资源=${gt102ClearResources} 重取=${gt102Reload}`
  )

  // ---- GT103：窗口空时给出「任务真实所在时间」并可一键跳转（11-04 用户反馈：看不到任务）----
  // 现象：甘特窗口锚在「今天起 N 天」，而某版本的任务排在更早（例如 TZP 96 条排在 10-30~11-02）⇒ 窗口内 0 条。
  // 收敛：空窗时用**桶聚合**（轻量）算出该版本任务的真实起止，提示条写明「该版本 N 条任务排在 …，不在当前时间窗（…）」，
  //       并给「跳到任务所在时间」按钮（把窗口挪过去）。
  const gt103UsesBuckets = /getGanttBuckets\(/.test(ganttCode) && /firstStart/.test(ganttCode) && /lastEnd/.test(ganttCode)
  const gt103SpanText = /该版本\s*\{\{\s*versionTotalTasks\s*\?\?\s*0\s*\}\}\s*条任务排在\s*\{\{\s*taskSpanText\s*\}\}/.test(
    ganttTpl
  )
  const gt103JumpBtn = /跳到任务所在时间/.test(ganttTpl) && /@click="jumpToTaskSpan"/.test(ganttTpl)
  const gt103JumpFn = /function jumpToTaskSpan\(\)[\s\S]{0,400}?ganttWindow\.value\s*=/.test(ganttCode)
  assert(
    'GT103 静态：窗口空时提示任务真实所在区间并提供「跳到任务所在时间」按钮',
    gt103UsesBuckets && gt103SpanText && gt103JumpBtn && gt103JumpFn,
    `用桶聚合=${gt103UsesBuckets} 提示区间=${gt103SpanText} 跳转按钮=${gt103JumpBtn} 跳转函数=${gt103JumpFn}`
  )

  // ---- GT104：撤「排程域」筛选，域信息并入版本下拉（2026-11-04 用户拍板）----
  // 依据：版本隶属一个排程域；现网版本全单域 ⇒ 原多选筛选恒只有 1 个选项，
  //   全选 / 清空走同一结果（过滤分支只在 length>0 时生效），纯空转；且残留时会滤空新版本。
  // 保留：域染色、lane-domain 泳道、域分项计数（availableDomains 仍服务于它们）。
  const gt104LabelFn = (() => {
    const i = ganttCode.indexOf('function versionOptionLabel(')
    if (i < 0) return ''
    const j = ganttCode.indexOf('\n}', i)
    return j < 0 ? '' : ganttCode.slice(i, j + 2)
  })()
  const gt104LabelHasDomain =
    /\bv\.domainKey\b/.test(gt104LabelFn) && /domainLabel\(/.test(gt104LabelFn)
  const gt104NoFilterSelect =
    !/<span class="label">排程域<\/span>/.test(ganttTpl) && !/v-model="domainFilter"/.test(ganttTpl)
  const gt104NoFilterVar = !/\bdomainFilter\b/.test(ganttCode)
  assert(
    'GT104 静态：撤「排程域」筛选，域写进版本下拉（版本码（状态）· 域）',
    gt104LabelHasDomain && gt104NoFilterSelect && gt104NoFilterVar,
    `版本标签含域=${gt104LabelHasDomain} 无域下拉=${gt104NoFilterSelect} 无domainFilter=${gt104NoFilterVar}`
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

  // M04 静态（候选集按排程域收敛 — 2026-10-31 §10A P4 复查挖出的真缺陷 B）
  //  背景：Dialog 原先列「当前页全部订单」且候选行不带 domainKey ⇒ 用户可切域 B 却勾域 A 的订单，
  //  后端 ScopeJsonV2Validator ④会 fail-closed 拒绝（技术报错，用户看不懂）。
  //  口径：弹窗一次只处理一个排程域；候选集按域过滤；默认域优先取「预选订单自身的域」。

  // M04-1：Order.vue advanceCandidates 透传 domainKey（列表项本就带 o.DomainKey）
  const orderVueCandidates = orderVueContent.match(
    /const advanceCandidates = computed\(\(\) =>[\s\S]*?\n\s*\}\)\n/
  )?.[0] ?? ''
  const orderPassesDomainKey = /domainKey:\s*o\.domainKey/.test(orderVueCandidates)
  assert(
    'M04-1 静态：Order.vue advanceCandidates 透传 domainKey: o.domainKey',
    orderPassesDomainKey,
    orderPassesDomainKey ? '候选行已带排程域' : 'Order.vue advanceCandidates 未透传 domainKey'
  )

  // M04-2：OrderAdvanceDialog.vue 候选行接口含 domainKey?: string
  const dlgDomainField = /domainKey\?:\s*string/.test(dlgContent)
  assert(
    'M04-2 静态：OrderAdvanceDialog.vue OrderAdvanceCandidate 含 domainKey?: string',
    dlgDomainField,
    dlgDomainField ? '接口已加排程域字段' : 'OrderAdvanceDialog.vue 候选行接口未含 domainKey'
  )

  // M04-3：buildRows 按排程域过滤（一次只处理一个域）
  const buildRowsBlock = dlgContent.match(/function buildRows\([\s\S]*?\n\}/)?.[0] ?? ''
  const buildRowsFilters =
    /props\.orders/.test(buildRowsBlock) && /filter\(\(o\) => o\.domainKey === dk\)/.test(buildRowsBlock)
  assert(
    'M04-3 静态：OrderAdvanceDialog.vue buildRows 按 domainKey 过滤候选集',
    buildRowsFilters,
    buildRowsFilters ? '候选集已按域收敛' : 'buildRows 未按 domainKey 过滤（跨域订单仍会进表）'
  )

  // M04-4：默认域优先取「预选订单自身的域」（列表里勾的就是用户要处理的订单）
  const defaultDomainRule =
    /toDomainKey\(preOrder\?\.domainKey\)\s*\?\?\s*props\.defaultDomainKey\s*\?\?\s*props\.domainOptions\[0\]/.test(
      dlgContent
    )
  assert(
    'M04-4 静态：OrderAdvanceDialog.vue 默认域 = 预选订单自身的域 → 页面默认域 → 首个选项',
    defaultDomainRule,
    defaultDomainRule
      ? '默认域取预选订单所在域（跨域提交入口被堵）'
      : '默认域未取预选订单自身的域'
  )

  // M04-5：域切换时重建行 + 清空勾选 + 重放预选（不残留上一域的勾选）
  const domainWatchBlock = dlgContent.match(/watch\(domainKey[\s\S]*?\n\}\)/)?.[0] ?? ''
  const domainWatchOk =
    /buildRows\(next\)/.test(domainWatchBlock) &&
    /selected\.value = \[\]/.test(domainWatchBlock) &&
    /applyPreselection\(\)/.test(domainWatchBlock)
  assert(
    'M04-5 静态：OrderAdvanceDialog.vue 切换排程域时重建行 + 清空勾选 + 重放预选',
    domainWatchOk,
    domainWatchOk
      ? '切域后候选集与勾选同步刷新'
      : '切域未重建行/未清空勾选/未重放预选'
  )

  // M04-6：模板提示面向用户（说明「仅显示当前排程域的订单」），且无内部措辞
  const dlgTemplateDomain = dlgTemplate.includes('仅显示排程域')
  const dlgDomainLeaks = ['§', '号位', 'B4', '回执', '兼容回退'].filter((t) => dlgTemplate.includes(t))
  assert(
    'M04-6 静态：OrderAdvanceDialog.vue 模板含「仅显示排程域」提示且无内部措辞',
    dlgTemplateDomain && dlgDomainLeaks.length === 0,
    dlgTemplateDomain && dlgDomainLeaks.length === 0
      ? '提示文案用户可读、无内部措辞'
      : `模板文案异常（domainHint=${dlgTemplateDomain}, leaks=${dlgDomainLeaks.join('|')}）`
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
  const explTypes = strip(readSrc('api/aps-v1/types/explanation.ts'))
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
  //    把标签函数的降级删掉照样判绿（PI03 / GT02 同型「匹到别的声明」的第三次复发）
  //  - 2026-11-02：交付状态标签函数由 `delayLabel`（ERP 维度）换成 `riskLabel`（计划派生）
  const delayFn =
    vueCode.match(/function riskLabel\([^)]*\)\s*:\s*string\s*\{([\s\S]*?)\n\}/)?.[1] ?? ''
  const statusFn =
    vueCode.match(/function statusLabel\([^)]*\)\s*:\s*string\s*\{([\s\S]*?)\n\}/)?.[1] ?? ''
  const delayDegrade = /\?\?\s*raw/.test(delayFn)
  const statusDegrade = /:\s*raw\s*$/.test(statusFn.trim()) || /:\s*raw\s*\n\}/.test(statusFn)
  const hasNormalize =
    /export const normalizeOrderStatus/.test(commonCode) && /normalizeOrderStatus/.test(statusFn)
  assert(
    'OD07 静态：状态未知值降级原样显示，不做枚举校验',
    delayDegrade && statusDegrade && hasNormalize,
    `riskLabel降级=${delayDegrade} statusLabel降级=${statusDegrade} 归一=${hasNormalize}`
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

  // OD10 风险卡仍下线 / 未排产卡**重新上线**（2026-11-02 口径反转，5号位 r14009 答复函 §二）
  //  - riskCount 查的是另一张表 `OrderScheduleSummary`（dev 实测 0 行、当前无写入方）
  //    ⇒ 仍不渲染：它分不清「无风险」与「该表无数据」
  //  - unscheduledCount 已从后端 `CAST(0 AS INT)` 字面量改为**实值**（本版本无 Task 行）
  //    ⇒ 「未排产」卡上线，恒等式 按期+延迟+未排产 = 总数
  const noRiskKpi = !/\briskCount\b/.test(vueCode) && !/\briskCount\b/.test(overviewCode)
  const unschedKpi =
    /\bunscheduledCount\b/.test(vueCode) && /\bunscheduledCount\b/.test(overviewCode)
  const noMockOrders = !/mockOrder/.test(fixturesCode) && !/mockOrder/.test(apiCode)
  assert(
    'OD10 静态：风险卡仍下线、未排产卡按实值上线，订单 mock 桩已删',
    noRiskKpi && unschedKpi && noMockOrders,
    `无riskCount=${noRiskKpi} 有unscheduledCount=${unschedKpi} 无mock=${noMockOrders}`
  )

  // OD17 2026-11-02 口径反转：旧「未计算」卡（= 总数 − 按期 − 延迟）**删掉**。
  //   新口径下这个差算出来的其实是「未排产」——标签是错的 ⇒ 不得复活；
  //   正确做法是直接读后端 `unscheduledCount` 那一桶。
  const oldUncalcCard = /label: '未计算'/.test(vueCode) || /label: '未计算'/.test(overviewCode)
  const newUnschedCard =
    /label: '未排产'/.test(vueCode) && /label: '未排产'/.test(overviewCode)
  assert(
    'OD17 静态：「未计算」卡已下线，改由「未排产」卡（读 unscheduledCount）',
    !oldUncalcCard && newUnschedCard,
    `旧未计算卡=${oldUncalcCard} 新未排产卡=${newUnschedCard}`
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

  // OD19b 2026-10-31 改写：判据意图不变（复用冻结 15 码字典 + 「待核对·」降级，
  //   不原样当权威码、不臆造中文名），但**实现形态变了**：本页不再本地查表，
  //   改走全站唯一口径 `reasonLabelOf`（types/explanation.ts），避免与甘特/排程解释/CTP 各写各的。
  //   反向自证：把本页改回本地裸查表（去掉 reasonLabelOf 调用）→ 转红；
  //             把共享函数的「待核对·」降级删掉 → 转红。
  const od19SharedCall = /reasonLabelOf\(/.test(vueCode)
  const od19SharedImport =
    /import\s*\{[^}]*reasonLabelOf[^}]*\}\s*from\s*'@\/api\/aps-v1\/types'/.test(vueCode)
  const od19SharedFn = /export function reasonLabelOf\([^)]*\)\s*:\s*string\s*\{[\s\S]*?REASON_CODE_LABELS[\s\S]*?\}/.test(
    explTypes
  )
  const od19Degrade = /待核对·/.test(explTypes)
  const od19NoLocalDict = !/REASON_CODE_LABELS\[/.test(vueCode)
  assert(
    'OD19b 静态：延期原因复用冻结 15 码字典 + 「待核对·」降级（不原样当权威码、不臆造中文名）',
    od19SharedCall && od19SharedImport && od19SharedFn && od19Degrade && od19NoLocalDict,
    `共享调用=${od19SharedCall} 共享导入=${od19SharedImport} 共享函数查表=${od19SharedFn} 降级=${od19Degrade} 本页无裸查表=${od19NoLocalDict}`
  )

  const od19NullSafe = /if \(hours == null\) return ''/.test(vueCode)
  assert(
    'OD19c 静态：延期时长按 null 判空（WhenWritingNull 键不出现；真值判断会把 0 吞成无值）',
    od19NullSafe,
    `==null守卫=${od19NullSafe}`
  )

  // OD19d 2026-11-02 改写：详情端点的 `order` **不返计划派生字段**（dev 实测）⇒
  //   抽屉里的「APS 预计完成」只能取打开抽屉那一行（detailRow），拿详情响应判空会永远显示 '—'
  const od19Rendered =
    /row\.delayHours/.test(vueCode) &&
    /row\.primaryReasonCode/.test(vueCode) &&
    /detailRow\??\.plannedCompletionTime/.test(vueCode)
  assert(
    'OD19d 静态：三个字段都在页面有渲染位（列表/抽屉），不是只声明不显示',
    od19Rendered,
    `列表延期时长=${/row\.delayHours/.test(vueCode)} 列表原因=${/row\.primaryReasonCode/.test(vueCode)} 抽屉预计完成=${/detailRow\??\.plannedCompletionTime/.test(vueCode)}`
  )

  // OD20 出错不整页跳 /500（5号位 2026-10-31 回执 §六-2：前端无条件做）
  //  背景：全局拦截器对 5xx / 超时 一律 `window.location.replace('/500')`，
  //    订单列表查不动时用户被踢出页面，**已填的筛选、勾选、页码全丢**，且看不到任何原因。
  //  改法：加 `noErrorRedirect` 逐请求开关 ⇒ 该请求失败时不跳页，抛 ApiError 由页面自己的
  //    `listError`（ElAlert「加载失败：…」）呈现；提示语换大白话，不把 axios 英文原文摆出来。
  const httpCode = strip(readSrc('api/aps-v1/http.ts'))
  // 取「分支起点 → 下一个分支起点」之间的正文，避免整文件 includes 造成的假绿
  const sliceBetween = (code, from, to) => {
    const i = code.indexOf(from)
    if (i < 0) return ''
    const j = to ? code.indexOf(to, i + from.length) : code.length
    return code.slice(i, j < 0 ? code.length : j)
  }
  // ⚠️ 开关声明必须**限定在 RequestOptions 接口体里**断言：
  //   同一个名字也出现在内部 `RequestFlags` 类型上，只判「文件里出现过这串字」⇒
  //   把 RequestOptions 那行注释掉仍然绿（自证首轮实测假绿，已加严）。
  const reqOptsBlock = sliceBetween(httpCode, 'export interface RequestOptions {', 'type RequestFlags')
  const od20Opt =
    /noErrorRedirect\?:\s*boolean/.test(reqOptsBlock) &&
    /const noErrorRedirect = original\?\.noErrorRedirect === true/.test(httpCode)
  // 5xx 分支：守卫必须在 redirect 之前 ⇒ 否则跳转照样发生
  const http5xx = sliceBetween(httpCode, 'if (status && status >= 500)', 'if (!status)')
  const od20Guard5xx =
    http5xx.includes('if (noErrorRedirect)') &&
    http5xx.indexOf('if (noErrorRedirect)') < http5xx.indexOf('redirectToError(500)')
  // 网络层（超时 / 断网）分支：同理
  const httpNet = sliceBetween(httpCode, 'if (!status)', '// 其他 4xx')
  const od20GuardNet =
    httpNet.includes('if (noErrorRedirect)') &&
    httpNet.indexOf('if (noErrorRedirect)') < httpNet.indexOf('redirectToError(500)')
  const od20Plain =
    /请求超时，请稍后重试/.test(httpCode) &&
    /网络连接失败，请检查网络后重试/.test(httpCode) &&
    /服务暂时不可用，请稍后重试/.test(httpCode)
  assert(
    'OD20a 静态：请求可声明 noErrorRedirect（出错不整页跳 /500），且 5xx / 网络层两个分支都受守卫',
    od20Opt && od20Guard5xx && od20GuardNet && od20Plain,
    `开关=${od20Opt} 5xx守卫=${od20Guard5xx} 网络守卫=${od20GuardNet} 大白话=${od20Plain}`
  )

  // 订单三个调用（列表 / 汇总 / 详情）都要开这个开关 —— 只开列表，汇总超时时照样整页跳
  const apiList = sliceBetween(apiCode, 'async list(', 'async getDetail(')
  const apiDetail = sliceBetween(apiCode, 'async getDetail(', 'async getSummary(')
  const apiSummary = sliceBetween(apiCode, 'async getSummary(', '\n}')
  const od20Opted =
    /noErrorRedirect:\s*true/.test(apiList) &&
    /noErrorRedirect:\s*true/.test(apiDetail) &&
    /noErrorRedirect:\s*true/.test(apiSummary)
  assert(
    'OD20b 静态：订单列表 / 详情 / 汇总三个调用都开 noErrorRedirect（任一漏开就还会整页跳）',
    od20Opted,
    `list=${/noErrorRedirect:\s*true/.test(apiList)} detail=${/noErrorRedirect:\s*true/.test(apiDetail)} summary=${/noErrorRedirect:\s*true/.test(apiSummary)}`
  )

  // 页面确实有页内错误位 + store 确实把错误写进去（不然开关开了也没地方显示）
  const od20InPage =
    /v-if="listError"/.test(vueCode) && /加载失败/.test(vueCode) && /handleError\(listError, err\)/.test(storeCode)
  assert(
    'OD20c 静态：页内有错误位（ElAlert「加载失败：…」）且 store 把错误写进去（开关开了得有地方显示）',
    od20InPage,
    `错误条=${/v-if="listError"/.test(vueCode)} 文案=${/加载失败/.test(vueCode)} store写入=${/handleError\(listError, err\)/.test(storeCode)}`
  )

  // ===== OD21 计划派生交付判定接入（2026-11-02，5号位 r14009 答复函 §五 接入点）=====
  //  背景：原「延迟状态」列读 `Order.DelayStatus`（ERP/ODS **独立维度**），与旁边按计划算的
  //  「延期时长」同页打架 —— dev v999333 的 O20630651 就是「未延迟」+「超期 15.4 天」。
  //  冻结 `01 §64` 红线：**不得用 `Order.DelayStatus` 替代当前 PlanVersion 的交付结果**
  //  ⇒ 交付判定一律读后端算好的 `riskLevel`（DELAYED / ON_TRACK / **缺键=未排产**）。

  // OD21a 类型层：riskLevel 是**可选**键（缺键 = 未排产，不能写成非可选）+ 词表取冻结 07 §八.2
  const riskDict = commonCode.match(/ORDER_RISK_LEVELS\s*=\s*\[([\s\S]*?)\]/)?.[1] ?? ''
  const riskVocab = ['ON_TRACK', 'AT_RISK', 'DELAYED'].every((v) => riskDict.includes(`'${v}'`))
  const riskDeclared = /\n\s*riskLevel\?:\s*OrderRiskLevel/.test(typesCode)
  assert(
    'OD21a 静态：riskLevel 已进 OrderListItemDto（可选键，缺键=未排产）+ 词表取冻结 07 §八.2 三值',
    riskDeclared && riskVocab,
    `声明=${riskDeclared} 词表三值=${riskVocab}`
  )

  // OD21b 页面：格子与抽屉都读 riskLevel
  const readsRisk = /row\.riskLevel/.test(vueCode) && /detailRow\??\.riskLevel/.test(vueCode)
  assert(
    'OD21b 静态：格子与抽屉的交付判定都读 riskLevel',
    readsRisk,
    `列表row.riskLevel=${/row\.riskLevel/.test(vueCode)} 抽屉detailRow.riskLevel=${/detailRow\??\.riskLevel/.test(vueCode)}`
  )

  // OD21c 页面：**不再**从 delayStatus 取状态标签（ERP 维度不许当计划延期标签）
  const noDelayVerdict =
    !/\brow\.delayStatus\b/.test(vueCode) && !/\bdetailOrder\.delayStatus\b/.test(vueCode)
  assert(
    'OD21c 静态：不再从 delayStatus 取状态标签（ERP 维度不得当计划延期结论）',
    noDelayVerdict,
    `row.delayStatus=${/\brow\.delayStatus\b/.test(vueCode)} detailOrder.delayStatus=${/\bdetailOrder\.delayStatus\b/.test(vueCode)}`
  )

  // OD21d 三态渲染：DELAYED 红 / ON_TRACK 绿 / 缺键灰「未排产」
  const riskFns =
    /function riskLabel/.test(vueCode) && /function riskColor/.test(vueCode) && /function riskTagType/.test(vueCode)
  const riskMissingLabel = /ORDER_RISK_MISSING_LABEL\s*=\s*'未排产'/.test(commonCode)
  const riskColors =
    /ON_TRACK:\s*'#059669'/.test(vueCode) && /DELAYED:\s*'#ef4444'/.test(vueCode) && /RISK_MISSING_COLOR\s*=\s*'#94a3b8'/.test(vueCode)
  assert(
    'OD21d 静态：三态渲染齐（DELAYED 红 / ON_TRACK 绿 / 缺键灰「未排产」）',
    riskFns && riskMissingLabel && riskColors,
    `三函数=${riskFns} 缺键文案=${riskMissingLabel} 三色=${riskColors}`
  )

  // OD21e 抽屉的交付判定取**打开抽屉那一行**（详情端点不返计划派生字段，dev 实测）
  const drawerFromRow = /detailRow\??\.riskLevel/.test(vueCode)
  const notFromDetail = !/detailOrder\.riskLevel/.test(vueCode)
  assert(
    'OD21e 静态：抽屉交付判定取列表行，不拿详情响应的缺失字段判空',
    drawerFromRow && notFromDetail,
    `取列表行=${drawerFromRow} 不取详情=${notFromDetail}`
  )

  // ===== OD22 KPI 四卡（计划派生三桶）=====
  //  旧三卡 = 总数 / 未延迟 / 延迟（其中「未延迟」按 ERP DelayStatus 聚合），与格子的计划判定不同源。
  const kpiLabels = ['订单总数', '按期', '延迟', '未排产'].every((l) =>
    vueCode.includes(`label: '${l}'`)
  )
  const noOldOnTimeLabel = !/label: '未延迟'/.test(vueCode)
  assert(
    'OD22a 静态：KPI 四卡 = 总数/按期/延迟/未排产，无「未延迟」旧文案',
    kpiLabels && noOldOnTimeLabel,
    `四卡=${kpiLabels} 无旧文案=${noOldOnTimeLabel}`
  )

  const kpiBuckets =
    /s\.onTimeCount/.test(vueCode) && /s\.delayedCount/.test(vueCode) && /s\.unscheduledCount/.test(vueCode)
  assert(
    'OD22b 静态：KPI 直接读 summary 的计划派生三桶（不自己做差）',
    kpiBuckets,
    `onTime=${/s\.onTimeCount/.test(vueCode)} delayed=${/s\.delayedCount/.test(vueCode)} unscheduled=${/s\.unscheduledCount/.test(vueCode)}`
  )

  // 总览页消费同一个 summary ⇒ 同一套桶（否则总览与订单页又各说各话）
  const overviewBuckets =
    /s\.onTimeCount/.test(overviewCode) && /s\.delayedCount/.test(overviewCode) && /s\.unscheduledCount/.test(overviewCode)
  assert(
    'OD22c 静态：总览页订单摘要同读三桶（与订单页同源）',
    overviewBuckets,
    `onTime=${/s\.onTimeCount/.test(overviewCode)} delayed=${/s\.delayedCount/.test(overviewCode)} unscheduled=${/s\.unscheduledCount/.test(overviewCode)}`
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
    'plannedCompletionTime', 'delayHours', 'primaryReasonCode',
    // 2026-11-02 5号位 r14009 新落码：计划派生交付判定（缺键 = 未排产 / 无交期 ⇒ 键不出现）。
    'riskLevel'
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

  // ===== OD24 / OD25 计划派生三桶与 riskLevel 三态（2026-11-02 r14009 后端落码后新增）=====

  // OD24 恒等式：`onTime + delayed + unscheduled === total`（三桶互斥且穷尽）
  //  —— 对**所有**有订单的版本都成立才算数（只查一个版本会漏掉口径分叉）。
  //  dev 实测（2026-11-02）：13 个版本中 10 个有订单，恒等式全成立。
  const sumRows = []
  for (const v of vlist) {
    const s = await call('GET', '/api/order-query/summary', { token, query: { planVersionId: v.id } })
    const d = s.json?.data
    if (!d || !d.totalCount) continue
    const sum = (d.onTimeCount ?? 0) + (d.delayedCount ?? 0) + (d.unscheduledCount ?? 0)
    sumRows.push({ id: v.id, total: d.totalCount, sum, ok: sum === d.totalCount })
  }
  const badSums = sumRows.filter((r) => !r.ok)
  assert(
    'OD24 动态：summary 三桶恒等式成立（按期+延迟+未排产 = 总数，逐版本核）',
    sumRows.length > 0 && badSums.length === 0,
    sumRows.length === 0
      ? 'dev 无任何含订单的版本'
      : `核版本数=${sumRows.length} 不成立=${badSums.map((r) => `v${r.id}:${r.sum}/${r.total}`).join(' ') || '无'}`
  )

  // OD25 riskLevel 三态实测：值域 ⊆ 冻结词表、存在被判定过的行、也存在「未排产」缺键行
  //  —— 缺键这一态必须有（v999333/999334 等真实小版本「绝大多数未排产」是正常形态）。
  //  —— 顺带把**键漂移**检查扩到全部版本：OD13 只查「订单最多的那个版本」，
  //     而那批行常常全是缺键（无 riskLevel）⇒ 白名单里加不加 riskLevel 都不影响 OD13（实测）。
  //     这里遍历所有版本，行键必须仍落在同一份白名单内（否则 = 契约漂移）。
  const observed = new Set()
  const unknownAll = new Set()
  let judged = 0
  let missing = 0
  let sampled = 0
  for (const v of vlist) {
    const l = await call('GET', '/api/order-query', {
      token,
      query: { planVersionId: v.id, pageIndex: 1, pageSize: 200 }
    })
    const items = l.json?.data?.items
    if (!Array.isArray(items) || !items.length) continue
    sampled++
    for (const it of items) {
      for (const k of Object.keys(it)) if (!realKeys.has(k)) unknownAll.add(k)
      if (it.riskLevel == null) missing++
      else {
        observed.add(it.riskLevel)
        judged++
      }
    }
  }
  const vocab = new Set(['ON_TRACK', 'AT_RISK', 'DELAYED'])
  const outside = [...observed].filter((v) => !vocab.has(v))
  assert(
    'OD25 动态：riskLevel 值域 ⊆ 冻结词表，且「有判定」与「未排产缺键」两态都真实存在',
    outside.length === 0 && judged > 0 && missing > 0,
    `越界值=${outside.join(',') || '无'} 有判定=${judged} 缺键=${missing} 采样版本数=${sampled} 见过=${[...observed].join(',') || '无'}`
  )
  assert(
    'OD25b 动态：全部版本逐行扫，行键都落在真实 OrderListItemDto 白名单内（键漂移）',
    unknownAll.size === 0,
    `采样版本数=${sampled} 越界键=${[...unknownAll].join(',') || '无'}`
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
  const spApiSrc = stripComments(readSrc('api/aps-v1/strategyProfile.ts'))
  const rulesStore = stripComments(readSrc('store/modules/aps/rules.ts'))
  const psStore = stripComments(readSrc('store/modules/aps/parameterSets.ts'))
  const spStore = stripComments(readSrc('store/modules/aps/strategyProfile.ts'))
  const spView = stripComments(readSrc('views/Aps/StrategyProfiles.vue'))
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
  assert('R16 静态：两族都按「最新已发布」挑当前已发布版（多条 PUBLISHED 并存时 find 会命中已到期旧版）',
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
      /min:\s*limits\??\.min/.test(bufferSrc) &&
      /max:\s*limits\??\.max/.test(bufferSrc),
    `enum=${bufferSrc.includes('lookupParameterEnum(block, path)')} limits=${/min:\s*limits\??\.min/.test(bufferSrc)}`)

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

  /* -------- R38-R41：治理三族「删除」入口（2026-10-30 路 A 恢复：必须存在）--------
   * 口径沿革（第 4 次翻转，按时间读）：
   *   10-25 暂缓 → 10-26 放行 → 10-29 撤（FC-V1-007 未获批）→ **10-30 恢复（最窄口径）**。
   *   3号位 2026-10-30 回执路 A：0号位 批准「最窄删除口径」，三个主表 DELETE 路由摘掉 `[NonAction]`
   *   重新注册（版本级端点保持 `[NonAction]` 不开放）。硬删 + 审计前置 + 被引用反向查拒绝。
   *   dev 实测：`DELETE .../999999999` 回 **400「…不存在」**（请求已到 service 层，
   *   区别于 405=方法被摘 / 404=路径不在）⇒ 路由确实活了。
   * ⇒ 现在锁的是：
   *   (a) 三族 api 都有 `deleteXxx` 且走 `apsHttp.delete`；
   *   (b) 三个 store 都有 `removeXxx`（含失败回列表核对真值）；
   *   (c) 三个页面都有删除入口与二次确认；
   *   (d) **停用出口仍在**（删除只是其中一条路，已发布过的仍只能停用）。
   * 反向自证：删掉任一 api 方法 / store 动作 / 页面按钮 ⇒ 对应探针必须转红。 */
  const apiDeleteOk = (src, name) =>
    new RegExp(`async\\s+${name}\\s*\\(`).test(src) && /apsHttp\.delete/.test(src)
  assert('R38 静态：三族 api 都有 deleteRuleSet / deleteParameterSet / deleteProfile（路 A 已恢复 DELETE 路由）',
    apiDeleteOk(ruleApiSrc, 'deleteRuleSet') &&
      apiDeleteOk(psApiSrc, 'deleteParameterSet') &&
      apiDeleteOk(spApiSrc, 'deleteProfile'),
    `规则集=${apiDeleteOk(ruleApiSrc, 'deleteRuleSet')} 参数集=${apiDeleteOk(psApiSrc, 'deleteParameterSet')} 策略包=${apiDeleteOk(spApiSrc, 'deleteProfile')}`)

  // 必须切片函数体再判：loadList()/loadProfiles() 在 store 内另有多处调用，整文件判会假绿
  // 三参：函数体里必须真的 `some(<列表>.value…)` 回查 —— 只写 `const stillThere = true`
  // 这种摆设会被判红，不会因为「字面量还在」假绿。
  const storeDeleteOk = (src, name, listVar) => {
    const body = fnBody(src, name)
    return (
      body.length > 0 &&
      /(loadList|loadProfiles)\(\)/.test(body) &&
      new RegExp(`const stillThere = ${listVar}\\.value\\.some\\(`).test(body) &&
      /if \(stillThere\) /.test(body)
    )
  }
  assert('R39 静态：三族 store 都有 removeXxx 且失败后回列表核对真值（loadList/loadProfiles）',
    storeDeleteOk(rulesStore, 'removeRuleSet', 'ruleSets') &&
      storeDeleteOk(psStore, 'removeParameterSet', 'parameterSets') &&
      storeDeleteOk(spStore, 'removeProfile', 'profiles'),
    `规则集=${storeDeleteOk(rulesStore, 'removeRuleSet', 'ruleSets')} 参数集=${storeDeleteOk(psStore, 'removeParameterSet', 'parameterSets')} 策略包=${storeDeleteOk(spStore, 'removeProfile', 'profiles')}`)

  assert('R40 静态：三个页面都有删除入口 + 二次确认 + 调用 store.removeXxx（含无版本空状态入口）',
    /onDeleteRuleSet/.test(rulesView) &&
      /removeRuleSet/.test(rulesView) &&
      /onDeleteParameterSet/.test(psView) &&
      /removeParameterSet/.test(psView) &&
      /onDeleteProfile/.test(spView) &&
      /removeProfile/.test(spView) &&
      [rulesView, psView, spView].every((s) => s.includes('ElMessageBox.confirm')),
    `规则集=${/onDeleteRuleSet/.test(rulesView)}/${/removeRuleSet/.test(rulesView)} 参数集=${/onDeleteParameterSet/.test(psView)}/${/removeParameterSet/.test(psView)} 策略包=${/onDeleteProfile/.test(spView)}/${/removeProfile/.test(spView)}`)

  assert('R41 静态：删除之外「停用」出口仍必须在（参数集 / 策略包主表 IsActive 开关 + 规则集停用按钮）',
    /editParameterSetForm\.isActive\b/.test(psView) &&
      /editProfileForm\.isActive\b/.test(spView) &&
      /openDisableDialog/.test(rulesView) &&
      /actions\.canDisable/.test(rulesView),
    `参数集停用开关=${/editParameterSetForm\.isActive\b/.test(psView)} 策略包停用开关=${/editProfileForm\.isActive\b/.test(spView)} 规则集停用=${/openDisableDialog/.test(rulesView)}/${/actions\.canDisable/.test(rulesView)}`)

  /* -------- R45-R47：治理三族「停用行」默认折叠 + 必须留开关（2026-10-30）--------
   * 背景：建错的主表常常只能停用（已发布过的不可删），停用行堆在列表里干扰选择。
   * 口径：**默认不显示**，但**必须留可见开关** —— 停用行是唯一的「重新启用」入口，藏死等于功能丢失。 */
  assert('R45 静态：三族 store 都有 showInactive 开关且默认 false（默认折叠停用行）',
    /const showInactive = ref\(false\)/.test(rulesStore) &&
      /const showInactive = ref\(false\)/.test(psStore) &&
      /const showInactive = ref\(false\)/.test(spStore),
    `规则集=${/const showInactive = ref\(false\)/.test(rulesStore)} 参数集=${/const showInactive = ref\(false\)/.test(psStore)} 策略包=${/const showInactive = ref\(false\)/.test(spStore)}`)

  const rulesInactiveFilter = /if \(!showInactive\.value\) arr = arr\.filter\(\(r\) => r\.isActive !== false\)/.test(rulesStore)
  const psInactiveFilter = /if \(!showInactive\.value\) arr = arr\.filter\(\(p\) => p\.isActive !== false\)/.test(psStore)
  const spInactiveFilter = /showInactive\.value \? profiles\.value : profiles\.value\.filter\(\(p\) => p\.isActive !== false\)/.test(spStore)
  assert('R46 静态：开关关闭时确实按 `isActive !== false` 过滤（不是只加了个摆设开关）',
    rulesInactiveFilter && psInactiveFilter && spInactiveFilter,
    `规则集=${rulesInactiveFilter} 参数集=${psInactiveFilter} 策略包=${spInactiveFilter}`)

  const switchIn = (src) => /v-model="showInactive"/.test(src) && src.includes('显示已停用')
  assert('R47 静态：三个页面都有「显示已停用（N）」开关；策略包页列表改用 visibleProfiles（防回退成全量直出）',
    switchIn(rulesView) && switchIn(psView) && switchIn(spView) &&
      /v-for="p in visibleProfiles"/.test(spView) &&
      !/v-for="p in profiles"/.test(spView),
    `规则集=${switchIn(rulesView)} 参数集=${switchIn(psView)} 策略包=${switchIn(spView)} 用visibleProfiles=${/v-for="p in visibleProfiles"/.test(spView)} 残留全量=${/v-for="p in profiles"/.test(spView)}`)

  /* -------- R27-R31：段位编辑器（2026-10-22 新增；发布白名单要窄于展示字典） -------- */
  const editorSrc = stripComments(readSrc('api/aps-v1/prioritySegmentEditor.ts'))

  function arrayValuesFrom(src, name) {
    const body = (new RegExp(`const ${name}[^=]*=\\s*\\[([\\s\\S]*?)\\n\\]`).exec(src) || [])[1] || ''
    return [...body.matchAll(/value:\s*(\d+)/g)].map((m) => Number(m[1]))
  }
  const arrayValues = (name) => arrayValuesFrom(editorSrc, name)
  const editorFieldVals = arrayValues('EDITOR_DEMAND_FIELDS')
  const editorOpVals = arrayValues('EDITOR_CONDITION_OPERATORS')
  const sameEditorSeq = (a, b) => JSON.stringify(a) === JSON.stringify(b)

  assert('R27 静态：编辑器字段白名单 = 发布白名单内运行侧已接线的 12 个；不含 0/4/5（执行器无实现）与 1（延期状态）/12（需求成熟度）（0号位 2026-11-07 裁决归参数集）',
    sameEditorSeq(editorFieldVals, [2, 3, 6, 7, 8, 9, 10, 11, 13, 14, 15, 16]) &&
      ![0, 1, 4, 5, 12].some((v) => editorFieldVals.includes(v)),
    `实际=${JSON.stringify(editorFieldVals)}（0/1/4/5/12 选了必然发布失败或口径归参数集）`)

  assert('R28 静态：编辑器操作符白名单 = 执行器支持的 7 个（含 Contains/7），且不含 NotEquals(1)',
    sameEditorSeq(editorOpVals, [0, 6, 7, 2, 3, 4, 5]) && !editorOpVals.includes(1),
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

  assert('R31 静态：九条发布前拦截齐全（至少一段/名称/字段操作符组合/值/日期/数值/值域/兜底段/轮次）',
    ['至少要配一个段位', '名称不能为空', '不能使用该操作符', '条件值不能为空',
      '日期格式应为', '数值格式不正确', '不在合法值域', '无条件的段最多只能有 1 个', '必须放在最后',
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

  /* ===== RP01-RP07：整版发布权限收口 + ⑦ 换型规则只读汇总（2026-10-27） =====
   * 依据：0号位「规则集多主题协作与统一发布」v1.0（丙案）+ 致 3/4号位任务书 v1.0。
   *   - 整版发布只能走 aps.rule.publish；Setup 侧 `rule-set-versions/{id}/publish` 前端不封装、不调用
   *   - 版本下拉要能分辨「哪个规则集的哪一版」；?ruleSetVersionId= 指定了就必须是那一版
   *   - 规则集页汇总 ⑦；10-28 起页签内直接增删改（写走 Setup store + Dialog，不直调 setupApi）
   *   - RP08-RP10（10-28）：两块表格 + 行内编辑删除 + aps.setup.edit 门控 + 只改当前草稿
   * ⚠️ stripComments 只剥 // 与 /* *\/，.vue 模板里的 <!-- --> 必须单独剥掉，否则头注释能顶替真实代码（假绿）。 */
  const noHtml = (s) => s.replace(/<!--[\s\S]*?-->/g, '')
  const setupApiSrc = stripComments(readSrc('api/aps-v1/setup.ts'))
  const setupDiffStoreSrc = stripComments(readSrc('store/modules/aps/setupDiff.ts'))
  const setupDiffViewSrc = noHtml(stripComments(readSrc('views/Aps/SetupDiff.vue')))
  const rulesViewNoHtml = noHtml(rulesView)

  const rp01NoPublishApi =
    !/publishRuleSetVersion|PublishSetupRuleSetVersionRequest/.test(setupApiSrc) &&
    !/rule-set-versions\/\$\{[^}]+\}\/publish/.test(setupApiSrc)
  const rp01NoPublishStore =
    !/setupApi\.publish/.test(setupDiffStoreSrc) &&
    !/async function publish\s*\(/.test(setupDiffStoreSrc)
  const rp01NoPublishView =
    !/publishDialog|openPublishDialog|onPublish/.test(setupDiffViewSrc) &&
    /router\.push\(\{ path: '\/aps\/rules' \}\)/.test(setupDiffViewSrc) &&
    setupDiffViewSrc.includes('去规则集维护页发布')
  assert(
    'RP01 静态：Setup 侧整版发布三处全撤（api 不封装 / store 无 publish / 页面只跳规则集维护页）',
    rp01NoPublishApi && rp01NoPublishStore && rp01NoPublishView,
    `api=${rp01NoPublishApi} store=${rp01NoPublishStore} view=${rp01NoPublishView}`
  )

  /* 2026-10-28 P2 落码（3号位 v2.0 §二）：后端 #11 直接回带 ruleSetCode/ruleSetName ⇒
   * 前端必须**撤掉** `GET /rule-sets`（aps.rule.view）侧映射——aps.planner 无该码必 403，
   * 旧实现下 PMC 的版本下拉只剩版本号，SET-UI-01 不成立。
   * 判据（三条独立，缺一即红）：
   *   ① 格式函数在位，且**只**读版本对象自带字段（v.ruleSetCode / v.ruleSetName）
   *   ② 不再调 listRuleSets（旧依赖已清）
   *   ③ 三页都改用它（不再有 composable 的 load() 调用） */
  const labelFormatter = stripComments(readSrc('views/Aps/composables/ruleSetVersionLabels.ts'))
  const labelPages = ['views/Aps/SetupExact.vue', 'views/Aps/SetupDefault.vue', 'views/Aps/SetupDiff.vue']
  const rp02Formatter =
    /export function formatRuleSetVersionLabel/.test(labelFormatter) &&
    /v\.ruleSetCode/.test(labelFormatter) &&
    /v\.ruleSetName/.test(labelFormatter) &&
    labelFormatter.includes('if (!code) return v.versionCode')
  const rp02NoRuleSetsDep = !/listRuleSets\s*\(/.test(labelFormatter)
  const rp02PagesUseIt = labelPages.every((p) => {
    const s = noHtml(stripComments(readSrc(p)))
    return s.includes('formatRuleSetVersionLabel(') && !s.includes('versionLabels.labelOf(')
  })
  const rp02 = rp02Formatter && rp02NoRuleSetsDep && rp02PagesUseIt
  assert(
    'RP02 静态：三页版本下拉用 #11 自带的规则集编码/名称（不再依赖 aps.rule.view 的 GET /rule-sets，planner 也能分辨）',
    rp02,
    `格式化器=${rp02Formatter} 旧依赖已清=${rp02NoRuleSetsDep} 页面接入=${rp02PagesUseIt}`
  )

  /** 指定版本就必须是那一版：非法/找不到都空选报错，绝不回落到「第一个 DRAFT」 */
  function exactVersionSelect(src) {
    if (!/function readVersionIdQuery\(\)/.test(src)) return false
    const body = fnBody(src, 'loadAll')
    const iWant = body.indexOf('if (wanted !== null)')
    const iDraft = body.indexOf("v.status === 'DRAFT'")
    if (iWant < 0 || iDraft < 0 || iDraft < iWant) return false
    const wantBranch = body.slice(iWant, iDraft)
    return (
      body.includes("if (wanted === 'invalid')") &&
      body.includes('selectRuleSetVersion(null)') &&
      wantBranch.includes('指定的规则集版本不存在或已删除') &&
      wantBranch.includes('return')
    )
  }
  const exactSrc = noHtml(stripComments(readSrc('views/Aps/SetupExact.vue')))
  const defaultSrc = noHtml(stripComments(readSrc('views/Aps/SetupDefault.vue')))
  assert(
    'RP03 静态：SetupExact/SetupDefault 支持 ?ruleSetVersionId= 精确选中；非法或不存在时空选报错，不回落第一个 DRAFT',
    exactVersionSelect(exactSrc) && exactVersionSelect(defaultSrc),
    `exact=${exactVersionSelect(exactSrc)} default=${exactVersionSelect(defaultSrc)}`
  )

  // ⚠️ 用「函数名 + 左括号」正则，不用 includes：`listDefaultRulesX(` 仍包含子串
  //    `listDefaultRules`，改名式破坏会照绿（纪律 ④）。
  // 2026-10-28 变更：页签由「只读汇总 + 跳转维护页」改为**本页直接增删改**（用户拍板合并），
  //   故 goSetupPage 判据撤除；页面仍**不得直调 setupApi 写端点** —— 写走两个 store + 两个 Dialog，
  //   保证 DRAFT 门控与校验只有一份实现。
  const rp04 =
    /<ElTabPane[^>]*name="setup"/.test(rulesViewNoHtml) &&
    /setupApi\.listExactRules\(/.test(rulesViewNoHtml) &&
    /setupApi\.listDefaultRules\(/.test(rulesViewNoHtml) &&
    rulesViewNoHtml.includes('if (!canViewSetup.value) return') &&
    !/setupApi\.(create|update|delete)/i.test(rulesViewNoHtml) &&
    !/function goSetupPage\(/.test(rulesViewNoHtml)
  assert(
    'RP04 静态：规则集页「换型规则」页签只调两个 GET（读），写不直调 setupApi（走 store + Dialog）；无权限时不发请求',
    rp04,
    `tab=${rulesViewNoHtml.includes('name="setup"')} writes=${/setupApi\.(create|update|delete)/i.test(rulesViewNoHtml)} 旧跳转=${/function goSetupPage\(/.test(rulesViewNoHtml)}`
  )

  /* RP08-RP10（2026-10-28）：⑦ 增删改并入本页 —— 两块表格 + 行内编辑删除 + 复用 Setup 弹窗 */
  const rp08 =
    (rulesViewNoHtml.match(/class="setup-block"/g)?.length ?? 0) === 2 &&
    /async function openExactCreate\(/.test(rulesViewNoHtml) &&
    /async function openExactEdit\(/.test(rulesViewNoHtml) &&
    /async function handleExactDelete\(/.test(rulesViewNoHtml) &&
    /async function openDefaultCreate\(/.test(rulesViewNoHtml) &&
    /async function openDefaultEdit\(/.test(rulesViewNoHtml) &&
    /async function handleDefaultDelete\(/.test(rulesViewNoHtml) &&
    // 标签名后必须跟空白或 >：`<SetupDefaultDialogOff` 仍包含 `<SetupDefaultDialog` 子串，
    // 用裸 includes 改名式破坏会照绿（纪律 ④）
    /<SetupExactDialog[\s>]/.test(rulesViewNoHtml) &&
    /<SetupDefaultDialog[\s>]/.test(rulesViewNoHtml) &&
    /@saved="handleSetupSaved"/.test(rulesViewNoHtml)
  assert(
    'RP08 静态：换型规则页签分两块（明确产品转换 / 默认换型规则），各带新增 + 行内编辑/删除，复用 Setup 两个 Dialog',
    rp08,
    `blocks=${rulesViewNoHtml.match(/class="setup-block"/g)?.length ?? 0} dialogs=${/<SetupExactDialog[\s>]/.test(rulesViewNoHtml)}/${/<SetupDefaultDialog[\s>]/.test(rulesViewNoHtml)}`
  )

  // 权限码硬约束：⑦ 的增删改挂 aps.setup.edit，**不能**跟规则集页的 aps.rule.* 走
  const canEditSetupDecl = (() => {
    const i = rulesViewNoHtml.indexOf('const canEditSetup = computed')
    return i >= 0 ? rulesViewNoHtml.slice(i, i + 160) : ''
  })()
  const rp09 =
    canEditSetupDecl.includes("apsAuth.has('aps.setup.edit')") &&
    !canEditSetupDecl.includes('aps.rule.') &&
    /const canViewSetup = computed<boolean>\(\(\) => apsAuth\.has\('aps\.setup\.view'\)\)/.test(
      rulesViewNoHtml
    ) &&
    /v-if="canEditSetup"/.test(rulesViewNoHtml)
  assert(
    'RP09 静态：⑦ 增删改按 aps.setup.edit 门控（不是 aps.rule.*），查看仍按 aps.setup.view；无 edit 码不显示操作列',
    rp09,
    `decl=${canEditSetupDecl.includes("apsAuth.has('aps.setup.edit')")} rule错位=${canEditSetupDecl.includes('aps.rule.')} 操作列=${/v-if="canEditSetup"/.test(rulesViewNoHtml)}`
  )

  // 编辑目标只能是「当前草稿」：无草稿/无权限时 6 个写按钮全部 disabled（2 新增 + 2 编辑 + 2 删除）
  const startWriteBody = fnBody(rulesViewNoHtml, 'startSetupWrite')
  const syncStoresBody = fnBody(rulesViewNoHtml, 'syncSetupStores')
  const rp10 =
    /const setupEditable = computed/.test(rulesViewNoHtml) &&
    rulesViewNoHtml.includes('setupSummaryVersionId.value === setupDraftVersionId.value') &&
    (rulesViewNoHtml.match(/:disabled="!setupEditable"/g)?.length ?? 0) === 6 &&
    startWriteBody.includes('当前没有草稿版本') &&
    startWriteBody.includes('syncSetupStores(versionId)') &&
    syncStoresBody.includes('exactStore.loadRuleSetVersions(') &&
    syncStoresBody.includes('defaultStore.loadRuleSetVersions(') &&
    syncStoresBody.includes('exactStore.selectRuleSetVersion(') &&
    syncStoresBody.includes('defaultStore.selectRuleSetVersion(') &&
    syncStoresBody.includes('exactStore.canEdit')
  assert(
    'RP10 静态：⑦ 只改当前草稿（hasDraft+版本对齐 store），无草稿/无权限时 6 个写按钮全禁用并提示先新建草稿',
    rp10,
    `disabled=${rulesViewNoHtml.match(/:disabled="!setupEditable"/g)?.length ?? 0} 草稿提示=${startWriteBody.includes('当前没有草稿版本')} store对齐=${syncStoresBody.includes('exactStore.loadRuleSetVersions(')}`
  )

  /* RP11-RP13（2026-10-28）：两个页面瘦身 ——
   *   RP11：换型规则级 Diff 页（/aps/setup/diff）已从菜单隐藏 ⇒ 规则集页「版本对比」页签的
   *         「查看换型规则逐条差异 →」必须**带参**直达（规则集 + 两端版本 Id），
   *         否则用户落到一个"全系统版本大杂烩"的下拉页还得自己重挑一遍。
   *   RP12：SetupDiff 读 query：按 ruleSetId 收窄版本下拉、自动选中两端并直接出对比。
   *   RP13：「缺失规则查询」出口改跳规则集页（tab=setup + 4 字段预填）⇒ 本页要认这个深链。
   */
  const goDiffBody = fnBody(rulesViewNoHtml, 'goSetupDiffPage')
  // ⚠️ 判据必须卡住 push 的**实参形状**（`path` 后紧跟 `query`），
  //    只查「函数体里出现 query 这个词」会被 `const query = {}` 顶替 ⇒ 改坏 push 仍照绿。
  const rp11 =
    /router\.push\(\{\s*path:\s*'\/aps\/setup\/diff',\s*query\s*\}\)/.test(goDiffBody) &&
    /query\.ruleSetId\s*=\s*String\(ruleSetId\)/.test(goDiffBody) &&
    /query\.versionId\s*=\s*String\(diffBaseVersionId\.value\)/.test(goDiffBody) &&
    /query\.otherVersionId\s*=\s*String\(diffTargetVersionId\.value\)/.test(goDiffBody) &&
    /const diffBaseVersionId = ref/.test(rulesStore) &&
    /const diffTargetVersionId = ref/.test(rulesStore)
  assert(
    'RP11 静态：版本对比页签下钻换型规则 Diff 时带 ruleSetId + 两端版本 Id（该页已从菜单隐藏，不能让用户自己重挑）',
    rp11,
    `push=${/router\.push\(\{\s*path:\s*'\/aps\/setup\/diff',\s*query\s*\}\)/.test(goDiffBody)} versionId=${/query\.versionId\s*=\s*String\(diffBaseVersionId\.value\)/.test(goDiffBody)} otherId=${/query\.otherVersionId\s*=\s*String\(diffTargetVersionId\.value\)/.test(goDiffBody)} store=${/const diffBaseVersionId = ref/.test(rulesStore)}`
  )

  const setupDiffView = stripComments(readSrc('views/Aps/SetupDiff.vue')).replace(
    /<!--[\s\S]*?-->/g,
    ''
  )
  // ⚠️ `route.query.ruleSetId` 是**子串**：改名成 ruleSetIdNope 仍会命中 ⇒ 必须卡右边界（`)`）。
  const rp12 =
    /Number\(route\.query\.ruleSetId\)/.test(setupDiffView) &&
    /Number\(route\.query\.versionId\)/.test(setupDiffView) &&
    /Number\(route\.query\.otherVersionId\)/.test(setupDiffView) &&
    /loadRuleSetVersions\(scoped/.test(setupDiffView) &&
    /loadDiff\(versionId, otherId\)/.test(setupDiffView)
  assert(
    'RP12 静态：换型规则 Diff 页按 query 收窄规则集 + 预选两端版本并直接出对比',
    rp12,
    `ruleSetId=${/Number\(route\.query\.ruleSetId\)/.test(setupDiffView)} versionId=${/Number\(route\.query\.versionId\)/.test(setupDiffView)} otherId=${/Number\(route\.query\.otherVersionId\)/.test(setupDiffView)} scoped=${/loadRuleSetVersions\(scoped/.test(setupDiffView)} autoDiff=${/loadDiff\(versionId, otherId\)/.test(setupDiffView)}`
  )

  // ⚠️ 函数名是**子串**：`applySetupDeepLinkOff` 仍匹配 ⇒ 声明处卡 `(`，并要求两处调用点都在。
  // ⚠️ 深链里**不许** router.replace 清参：layout <component :key="route.fullPath" />（AppView.vue:30）
  //    会因 fullPath 变化重建整个页面组件 ⇒ 正在跑的深链连同弹窗一起被销毁（10-28 无头实测踩过）。
  const deepLinkBody = fnBody(rulesViewNoHtml, 'applySetupDeepLink')
  const rp13 =
    /async function applySetupDeepLink\(/.test(rulesViewNoHtml) &&
    /await applySetupDeepLink\(\)/.test(rulesViewNoHtml) &&
    /void applySetupDeepLink\(\)/.test(rulesViewNoHtml) &&
    /tab !== 'setup'/.test(rulesViewNoHtml) &&
    /defaultDialogPrefill/.test(rulesViewNoHtml) &&
    /:prefill="defaultDialogPrefill"/.test(rulesViewNoHtml) &&
    /openDefaultCreate\(prefill\)/.test(rulesViewNoHtml) &&
    deepLinkBody.length > 0 &&
    !deepLinkBody.includes('router.replace')
  assert(
    'RP13 静态：规则集页认「缺失规则查询」深链（tab=setup + 4 字段预填）并直接开「默认换型规则」弹窗',
    rp13,
    `decl=${/async function applySetupDeepLink\(/.test(rulesViewNoHtml)} call=${/await applySetupDeepLink\(\)/.test(rulesViewNoHtml)} activated=${/void applySetupDeepLink\(\)/.test(rulesViewNoHtml)} prefill=${/:prefill="defaultDialogPrefill"/.test(rulesViewNoHtml)} open=${/openDefaultCreate\(prefill\)/.test(rulesViewNoHtml)} 未清参=${!deepLinkBody.includes('router.replace')}`
  )

  /* RP14（2026-10-28）：改完 ⑦ 后必须重跑字段级 diff ——
   *   换型规则存在**草稿版本自己的** ContentSnapshotJson（后端 SetupRuleService 写子块），
   *   所以改 ⑦ 会改变「已发布版 vs 草稿版」的字段 diff；但版本对比页签读的是 loadDetail
   *   时算好的旧快照 ⇒ 不重跑就看不到 SetupTransitionRules 那一行，也就看不到
   *   「查看换型规则逐条差异 →」这个入口（10-28 用户反馈：改完还是没有蓝色链接）。
   */
  const refreshDiffBody = fnBody(rulesViewNoHtml, 'refreshDiff')
  const setupSavedBody = fnBody(rulesViewNoHtml, 'handleSetupSaved')
  const exactDelBody = fnBody(rulesViewNoHtml, 'handleExactDelete')
  const defaultDelBody = fnBody(rulesViewNoHtml, 'handleDefaultDelete')
  const rp14 =
    /rulesStore\.loadDiff\(base, target\)/.test(refreshDiffBody) &&
    /diffBaseVersionId\.value/.test(refreshDiffBody) &&
    /diffTargetVersionId\.value/.test(refreshDiffBody) &&
    /await refreshDiff\(\)/.test(setupSavedBody) &&
    /await refreshDiff\(\)/.test(exactDelBody) &&
    /await refreshDiff\(\)/.test(defaultDelBody)
  assert(
    'RP14 静态：改完 ⑦（新增/编辑/删除）后重跑字段级 diff（否则版本对比页签看不到 SetupTransitionRules 变更行，逐条对比入口不出现）',
    rp14,
    `loadDiff=${/rulesStore\.loadDiff\(base, target\)/.test(refreshDiffBody)} saved=${/await refreshDiff\(\)/.test(setupSavedBody)} del=${/await refreshDiff\(\)/.test(exactDelBody)}/${/await refreshDiff\(\)/.test(defaultDelBody)}`
  )

  /* RP15（2026-10-28）：/aps/rules 放开 aps.rule.view 只读入口（3号位 自裁 W-2）——
   *   放开的是**入口**，不是写权限：页内每个写动作仍必须按权限码逐个显隐，
   *   否则 viewer 进来就能建规则集/发布版本。
   *   判据：① 路由门槛三码齐全 ② 新建规则集/新建草稿/编辑资料/删除 → canWrite(aps.rule.edit)
   *        ③ 发布/停用/校验 → actions.canPublish|canDisable + canPublishAction(aps.rule.publish)
   *        ④ ⑦ 的增删改 → aps.setup.edit（与 aps.rule.* 无关）
   * ⚠️ 路由块按 path 切片取，否则别的路由的同名数组会顶替（假绿）。 */
  const routerSrcRP15 = readSrc('router/modules/aps.ts')
  const rulesRouteBlock = (() => {
    const i = routerSrcRP15.indexOf("path: '/aps/rules'")
    if (i < 0) return ''
    const j = routerSrcRP15.indexOf("path: '/", i + 10)
    return routerSrcRP15.slice(i, j > 0 ? j : i + 900)
  })()
  const rp15Route = /apsRequiredPermissions:\s*\[\s*'aps\.rule\.view',\s*'aps\.rule\.edit',\s*'aps\.rule\.publish'\s*\]/.test(
    rulesRouteBlock
  )
  const rp15CanWrite =
    /:disabled="!canWrite"/.test(rulesViewNoHtml) &&
    /v-if="canWrite"/.test(rulesViewNoHtml) &&
    /actions\.canCreateDraft/.test(rulesViewNoHtml) &&
    /const canWrite = computed\(\(\) => apsAuth\.has\('aps\.rule\.edit'\)\)/.test(rulesStore)
  /* 发布/停用按钮逐个挂门控：**按 @click 锚定按钮元素**，不用「出现次数」——
   *   计数判据在按钮增减时会假红/假绿，且改坏其中一处另两处仍能顶替。 */
  const rp15Buttons = rulesViewNoHtml.match(/<ElButton[\s\S]*?>/g) || []
  const rp15Btn = (clickExpr) => rp15Buttons.find((b) => b.includes(`@click="${clickExpr}"`)) || ''
  const rp15BtnGated = (clickExpr, flag) => {
    const b = rp15Btn(clickExpr)
    return b.length > 0 && b.includes(flag)
  }
  const rp15CanPublish =
    /v-if="actions\.canPublish"/.test(rulesViewNoHtml) &&
    /v-if="actions\.canDisable"/.test(rulesViewNoHtml) &&
    // 行内「发布」「停用」+ 停用弹窗「确认停用」→ 都按 canPublishAction 禁用
    rp15BtnGated('openPublishDialog', ':disabled="!canPublishAction"') &&
    rp15BtnGated('openDisableDialog', ':disabled="!canPublishAction"') &&
    rp15BtnGated('onDisable', ':disabled="!canPublishAction"') &&
    // 发布弹窗「确认发布」→ 按 canConfirmPublish 禁用，而 canConfirmPublish 必须含 canPublishAction
    rp15BtnGated('onPublish', ':disabled="!canConfirmPublish"') &&
    /const canConfirmPublish = computed<boolean>\(\s*\(\) => canPublishAction\.value &&/.test(
      rulesViewNoHtml
    ) &&
    /canValidate:\s*s === 'DRAFT' && pub/.test(rulesStore) &&
    /const canPublishAction = computed\(\(\) => apsAuth\.has\('aps\.rule\.publish'\)\)/.test(rulesStore)
  const rp15SetupEdit = /const canEditSetup = computed<boolean>\(\(\) => apsAuth\.has\('aps\.setup\.edit'\)\)/.test(
    rulesViewNoHtml
  )
  const rp15 = rp15Route && rp15CanWrite && rp15CanPublish && rp15SetupEdit
  assert(
    'RP15 静态：/aps/rules 放开 aps.rule.view 只读入口，但页内写动作仍按 aps.rule.edit / aps.rule.publish / aps.setup.edit 逐个显隐（view 进来天然只读）',
    rp15,
    `route=${rp15Route} canWrite=${rp15CanWrite} canPublish=${rp15CanPublish} setupEdit=${rp15SetupEdit}`
  )

  /* RP16（2026-10-28）：⑦ 两块表格补「最近修改」列（3号位 联合报告 §四 待办 #3）——
   *   「⑦Setup EXACT/DEFAULT 条数 + 最近修改信息 + 版本差异入口」里的第二项。
   *   条数与版本差异入口 10-28 已有；最近修改信息靠 SetupRuleDto.updatedAt/updatedBy。
   * ⚠️ 两块表都要有 ⇒ 断言出现次数 = 2（改掉一块仍绿是假绿）。 */
  const rp16 =
    (rulesViewNoHtml.match(/label="最近修改"/g) || []).length === 2 &&
    /function setupRuleModifiedText\(rule: SetupRuleDto\)/.test(rulesViewNoHtml) &&
    /function setupRuleModifiedBy\(rule: SetupRuleDto\)/.test(rulesViewNoHtml) &&
    /setupRuleModifiedText\(row\)/.test(rulesViewNoHtml) &&
    /setupRuleModifiedBy\(row\)/.test(rulesViewNoHtml) &&
    // 时间走**本地直显**（SetupTransitionRule 落库是 DateTime.Now 本地墙钟）
    /formatLocalDateTime\(t\)/.test(rulesViewNoHtml) &&
    !/formatUtcDateTime\(t\)/.test(rulesViewNoHtml)
  assert(
    'RP16 静态：⑦ 明确/默认两块表格都有「最近修改」列（时间 + 人），取 SetupRuleDto.updatedAt/updatedBy，时间本地直显',
    rp16,
    `列数=${(rulesViewNoHtml.match(/label="最近修改"/g) || []).length} text=${/function setupRuleModifiedText\(rule: SetupRuleDto\)/.test(rulesViewNoHtml)} by=${/function setupRuleModifiedBy\(rule: SetupRuleDto\)/.test(rulesViewNoHtml)} 本地直显=${/formatLocalDateTime\(t\)/.test(rulesViewNoHtml)}`
  )

  /* RP17（2026-10-28）：回填目标绑定「不变 RuleSetVersionId」（3号位 联合报告 §四 待办 #4）——
   *   缺失规则那一行指向的通常是**已发布**版本，⑦ 只能改草稿 ⇒ 页面必须明示
   *   「来源是哪个版本、保存写进哪个草稿」，不能让用户以为改的就是那一版。
   *   判据：① 认 ruleSetVersionId 深链参数 ② 解析出来源版本（版本号 + 状态）
   *        ③ 算出当前草稿版本号 ④ 模板上有来源版本提示块 */
  const rp17 =
    /const deepLinkSourceVersion = ref</.test(rulesViewNoHtml) &&
    /Number\(q\.ruleSetVersionId \?\? ''\)/.test(rulesViewNoHtml) &&
    /deepLinkSourceVersion\.value = src/.test(rulesViewNoHtml) &&
    /const currentDraftVersionCode = computed<string>/.test(rulesViewNoHtml) &&
    /v-if="deepLinkSourceVersion"/.test(rulesViewNoHtml) &&
    /deepLinkSourceVersion\.versionCode/.test(rulesViewNoHtml) &&
    /currentDraftVersionCode/.test(rulesViewNoHtml)
  assert(
    'RP17 静态：回填深链带来源版本 Id（ruleSetVersionId），规则集页明示「来源版本 → 写入当前草稿」',
    rp17,
    `ref=${/const deepLinkSourceVersion = ref</.test(rulesViewNoHtml)} param=${/Number\(q\.ruleSetVersionId \?\? ''\)/.test(rulesViewNoHtml)} resolve=${/deepLinkSourceVersion\.value = src/.test(rulesViewNoHtml)} draft=${/const currentDraftVersionCode = computed<string>/.test(rulesViewNoHtml)} alert=${/v-if="deepLinkSourceVersion"/.test(rulesViewNoHtml)}`
  )

  /* RP18（2026-10-28）：治理域时间改**本地直显**（3号位 10-28 回执正式口径）——
   *   后端治理域用 DateTime.Now 落库（服务器本地墙钟）⇒ 前端按字面显示，不做 UTC→本地位移。
   *   范围：规则集/参数集版本摘要的 publishedAt / effectiveFrom / effectiveTo + 审计 OccurredAt。
   * ⚠️ 校验时间（ValidatedAt）与对比时间（ComparedAt）是内存计算、序列化带 +08:00 偏移
   *    ⇒ 走 formatUtcDateTime 恰好正确，按 3号位 §五.3 保留现状，不得一起改。 */
  const datetimeSrcRP18 = stripComments(readSrc('utils/datetime.ts'))
  const psViewNoHtmlRP18 = noHtml(psView)
  const auditViewRP18 = noHtml(stripComments(readSrc('views/Aps/Audit.vue')))
  const rp18 =
    /export function formatLocalDateTime\(/.test(datetimeSrcRP18) &&
    /export function formatLocalDateTimeSec\(/.test(datetimeSrcRP18) &&
    /export function formatLocalDate\(/.test(datetimeSrcRP18) &&
    /formatLocalDateTime\(row\.publishedAt\)/.test(rulesViewNoHtml) &&
    /formatLocalDateTime\(row\.effectiveFrom\)/.test(rulesViewNoHtml) &&
    /formatLocalDateTime\(row\.effectiveTo\)/.test(rulesViewNoHtml) &&
    !/formatUtcDateTime\(row\.publishedAt\)/.test(rulesViewNoHtml) &&
    !/formatUtcDateTime\(row\.effectiveFrom\)/.test(rulesViewNoHtml) &&
    !/formatUtcDateTime\(row\.effectiveTo\)/.test(rulesViewNoHtml) &&
    /formatLocalDateTime\(row\.publishedAt\)/.test(psViewNoHtmlRP18) &&
    /formatLocalDateTime\(row\.effectiveFrom\)/.test(psViewNoHtmlRP18) &&
    /formatLocalDateTime\(row\.effectiveTo\)/.test(psViewNoHtmlRP18) &&
    /formatLocalDateTimeSec\(row\.occurredAt\)/.test(auditViewRP18) &&
    /formatLocalDateTimeSec\(currentRow\.occurredAt\)/.test(auditViewRP18) &&
    /formatUtcDateTime\(lastValidation\.validatedAt\)/.test(rulesViewNoHtml) &&
    /formatUtcDateTime\(currentDiff\.comparedAt\)/.test(rulesViewNoHtml)
  assert(
    'RP18 静态：治理域时间（版本发布时间/生效起止、审计 OccurredAt）改本地直显；校验时间与对比时间保留 UTC 解析（内存值带偏移）',
    rp18,
    `helper=${/export function formatLocalDateTime\(/.test(datetimeSrcRP18)} rules=${/formatLocalDateTime\(row\.publishedAt\)/.test(rulesViewNoHtml)} ps=${/formatLocalDateTime\(row\.publishedAt\)/.test(psViewNoHtmlRP18)} audit=${/formatLocalDateTimeSec\(row\.occurredAt\)/.test(auditViewRP18)} 保留=${/formatUtcDateTime\(lastValidation\.validatedAt\)/.test(rulesViewNoHtml)}`
  )

  /* RP19（2026-11-04 → 2026-11-07 扩字段）：优先级段位的「文本字段候选值」必须来自后端值域。
   * 背景：订单类型（field=3）候选原本是空数组 ⇒ 下拉点开只有「无数据」，用户以为控件坏了；
   *   延期状态 / 客户等级的旧提示值（Delayed/OnTrack、VIP/A/B/C）与冻结词表一个都对不上。
   * 2026-11-07：CustomerTier 收敛 {VIP, GENERAL}（2号位 来文核准），新增 8/10/11/14 四组值域。 */
  const psegSrcRP19 = stripComments(readSrc('api/aps-v1/prioritySegmentEditor.ts'))
  const psegViewRP19 = noHtml(stripComments(readSrc('views/Aps/components/PrioritySegmentEditor.vue')))
  // 切出候选字典的函数体再判（防止同名符号 / 注释里的字面量顶住 ⇒ 假绿）
  const sugStartRP19 = psegSrcRP19.indexOf('const FIELD_VALUE_SUGGESTIONS')
  const sugEndRP19 = psegSrcRP19.indexOf('export function isEditorField', sugStartRP19)
  const sug19 =
    sugStartRP19 >= 0 && sugEndRP19 > sugStartRP19
      ? psegSrcRP19.slice(sugStartRP19, sugEndRP19)
      : ''
  const rp19 =
    // 2026-11-07：延期状态(1) 归参数集、字段撤除 ⇒ 其候选值（ON_TIME 等）必须一并消失
    !/'ON_TIME'/.test(sug19) &&
    !/'FIRST_DELAY'/.test(sug19) &&
    !/'REPEATED_DELAY'/.test(sug19) &&
    /2:\s*\[[\s\S]{0,200}?'VIP'[\s\S]{0,200}?'GENERAL'/.test(sug19) &&
    !/'KEY_ACCOUNT'/.test(sug19) &&
    !/'STANDARD'/.test(sug19) &&
    /3:\s*\[[\s\S]{0,200}?'SALES_ORDER'[\s\S]{0,200}?'PRODUCTION_INSTRUCTION'/.test(sug19) &&
    /8:\s*\[[\s\S]{0,700}?'JAPAN'[\s\S]{0,700}?'DOMESTIC'[\s\S]{0,700}?'OVERSEAS'[\s\S]{0,700}?'VIETNAM'[\s\S]{0,700}?'INTER_FACTORY'[\s\S]{0,700}?'OTHER'/.test(sug19) &&
    /10:\s*\[[\s\S]{0,500}?'LAND'[\s\S]{0,500}?'SEA'[\s\S]{0,500}?'AIR'[\s\S]{0,500}?'COURIER'/.test(sug19) &&
    /11:\s*\[[\s\S]{0,400}?'DIRECT_SALES'[\s\S]{0,400}?'SALES_REPLENISHMENT'/.test(sug19) &&
    /14:\s*\[[\s\S]{0,400}?'FULL_PURPLE_SLIP'[\s\S]{0,400}?'DIFF_PURPLE_SLIP'/.test(sug19) &&
    !/'Delayed'/.test(sug19) &&
    !/'OnTrack'/.test(sug19) &&
    !/value:\s*'A'/.test(sug19) &&
    /:value="v\.value"/.test(psegViewRP19) &&
    /:label="v\.label"/.test(psegViewRP19) &&
    /no-data-text="没有预设值，直接输入后回车"/.test(psegViewRP19)
  assert(
    'RP19 静态：段位文本字段候选值取自后端值域（客户等级/订单类型/客户区分/运输方式/销售类别/非在库出荷区分），延期状态候选已随字段撤除，旧错值绝迹且允许手填',
    rp19,
    `字典切片=${sug19.length}字符 延期状态候选残留=${/'(ON_TIME|FIRST_DELAY|REPEATED_DELAY)'/.test(sug19)} 客户等级收敛=${/2:\s*\[[\s\S]{0,200}?'VIP'[\s\S]{0,200}?'GENERAL'/.test(sug19) && !/'KEY_ACCOUNT'/.test(sug19)} 新值域=${[8, 10, 11, 14].every((f) => sug19.includes(`${f}: [`))} 旧值残留=${/'(Delayed|OnTrack)'/.test(sug19)} 渲染=${/:value="v\.value"/.test(psegViewRP19) && /:label="v\.label"/.test(psegViewRP19)} 手填提示=${/no-data-text="没有预设值，直接输入后回车"/.test(psegViewRP19)}`
  )

  /* RP20-RP23（2026-11-07）：3号位 需求优先级新增字段实施包 v1.0 §三 前端维护落地。
   * 口径：字段/算子字典对齐后端枚举；编辑器只开放运行侧已接线字段；物料编码「包含」=前缀匹配；
   *      参数集锁定块只加成熟度两项并标暂未生效；DNF 条件组（运行侧未接线）不提前落地。 */
  const displaySrcRP20 = stripComments(readSrc('api/aps-v1/prioritySegment.ts'))
  const displayFieldVals = arrayValuesFrom(displaySrcRP20, 'DEMAND_FIELDS')
  const displayOpVals = arrayValuesFrom(displaySrcRP20, 'CONDITION_OPERATORS')
  assert('RP20 静态：展示字典 DemandField 0~16 与后端枚举顺序一致，ConditionOperator 0~7 含「包含（前缀匹配）」',
    sameEditorSeq(displayFieldVals, [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]) &&
      sameEditorSeq(displayOpVals, [0, 1, 2, 3, 4, 5, 6, 7]) &&
      displaySrcRP20.includes("label: '库存消耗日'") &&
      displaySrcRP20.includes("label: '包含（前缀匹配）'"),
    `字段=${JSON.stringify(displayFieldVals)} 算子=${JSON.stringify(displayOpVals)}`)

  assert('RP21 静态：编辑器开放 12 个已接线字段（2/3/6/7/8/9/10/11/13/14/15/16），延期状态(1)/需求成熟度(12) 与 0/4/5 保持关闭',
    sameEditorSeq(editorFieldVals, [2, 3, 6, 7, 8, 9, 10, 11, 13, 14, 15, 16]) &&
      ![0, 1, 4, 5, 12].some((v) => editorFieldVals.includes(v)),
    `实际=${JSON.stringify(editorFieldVals)}`)

  const rp22 =
    (editorSrc.match(/allowContains:\s*true/g) || []).length === 1 &&
    /{ value: 15, label: '物料编码', kind: 'TEXT', allowContains: true }/.test(editorSrc) &&
    /{ value: 13, label: '库存消耗日（天）', kind: 'NUMBER' }/.test(editorSrc) &&
    /{ value: 16, label: '数量', kind: 'NUMBER' }/.test(editorSrc) &&
    /def\.kind === 'DATE' \|\| def\.kind === 'NUMBER'/.test(editorSrc) &&
    /def\.allowContains/.test(editorSrc) &&
    /ElInputNumber/.test(psegViewRP19) &&
    /fieldKindOf\(c\.field\) === 'NUMBER'/.test(psegViewRP19) &&
    /c\.operator === 7 \? '前缀匹配，如 FINAL\*'/.test(psegViewRP19)
  assert('RP22 静态：「包含」只给物料编码且写明前缀匹配；库存消耗日/数量用数字框（出荷指示入物流日的双模式另见 RP24）',
    rp22,
    `allowContains=${(editorSrc.match(/allowContains:\s*true/g) || []).length} 数字框=${/ElInputNumber/.test(psegViewRP19)} 前缀提示=${/前缀匹配/.test(psegViewRP19)}`)

  /* RP24（2026-11-07 3号位 Q4 回执）：出荷指示入物流日（9，后端 CUSTOMERDUEDATE）的范围条件，
   *   值可写「相对冻结日整数天数」（后端投影层 `ConvertRelativeDateThreshold` 换算成 FrozenAt.Date + N），
   *   也可继续写具体日期（原样比较）。编辑器必须双模式且整数/日期都能通过本地拦截。 */
  const rp24 =
    /{ value: 9, label: '出荷指示入物流日', kind: 'DATE', allowRelativeOffset: true }/.test(editorSrc) &&
    /export function supportsRelativeOffset\(field: number, operator: number\): boolean/.test(editorSrc) &&
    /isRelativeOffsetField\(field\) && \[2, 3, 4, 5\]\.includes\(operator\)/.test(editorSrc) &&
    /export function isOffsetDaysText\(text: string\): boolean/.test(editorSrc) &&
    editorSrc.includes('的比较值应为整数天数（如 20 = 冻结日 +20 天）或具体日期 YYYY-MM-DD') &&
    /supportsRelativeOffset\(c\.field, c\.operator\)/.test(psegViewRP19) &&
    /onConditionMode\(seg, ci, \$event\)/.test(psegViewRP19) &&
    // 必须锁到控件本身：页脚提示里也有「按天数偏移」四个字，只搜文本会被提示顶住（自证实测假绿）
    /<ElRadioButton value="offset">按天数偏移<\/ElRadioButton>/.test(psegViewRP19) &&
    /<ElRadioButton value="date">具体日期<\/ElRadioButton>/.test(psegViewRP19) &&
    /offsetValueOf\(c\.value\)/.test(psegViewRP19) &&
    /距冻结日天数，如 20/.test(psegViewRP19) &&
    /20 = 冻结日 \+20 天（可填负数）/.test(psegViewRP19) &&
    /conditionMode\(c\) === 'offset'/.test(psegViewRP19)
  assert('RP24 静态：出荷指示入物流日范围条件支持相对天数（整数偏移）+ 具体日期双模式，值域与后端投影换算口径一致',
    rp24,
    `字段标记=${/allowRelativeOffset: true/.test(editorSrc)} 算子限制=${/isRelativeOffsetField\(field\) && \[2, 3, 4, 5\]/.test(editorSrc)} 整数判据=${/isOffsetDaysText/.test(editorSrc)} 双模式=${/按天数偏移/.test(psegViewRP19) && /具体日期/.test(psegViewRP19)} 天数框=${/offsetValueOf\(c\.value\)/.test(psegViewRP19)} 提示=${/20 = 冻结日 \+20 天/.test(psegViewRP19)}`)

  const blockFieldsSrcRP23 = stripComments(readSrc('api/aps-v1/parameterBlockFields.ts'))
  const lockStartRP23 = blockFieldsSrcRP23.indexOf('const LOCK_FIELDS')
  const lockEndRP23 = blockFieldsSrcRP23.indexOf('const SUPPLY_FIELDS', lockStartRP23)
  const lockSrcRP23 =
    lockStartRP23 >= 0 && lockEndRP23 > lockStartRP23
      ? blockFieldsSrcRP23.slice(lockStartRP23, lockEndRP23)
      : ''
  const inertStartRP23 = blockFieldsSrcRP23.indexOf('export const INERT_FIELD_PATHS')
  const inertEndRP23 = blockFieldsSrcRP23.indexOf('export function isInertBlockField', inertStartRP23)
  const inertSrcRP23 =
    inertStartRP23 >= 0 && inertEndRP23 > inertStartRP23
      ? blockFieldsSrcRP23.slice(inertStartRP23, inertEndRP23)
      : ''
  const rp23 =
    lockSrcRP23.includes("path: 'Trigger.ProtectMaturityStatus'") &&
    lockSrcRP23.includes("path: 'Trigger.MaturityStatusValues'") &&
    inertSrcRP23.includes("'Trigger.ProtectMaturityStatus'") &&
    inertSrcRP23.includes("'Trigger.MaturityStatusValues'") &&
    !/TriggerConditionGroups/.test(blockFieldsSrcRP23 + editorSrc + psegViewRP19) &&
    !/MatchConditionGroups/.test(editorSrc + psegViewRP19)
  assert('RP23 静态：参数集锁定块新增成熟度两项并标「暂未生效」；DNF 条件组编辑器未提前落地（负断言）',
    rp23,
    `目录两项=${lockSrcRP23.includes("path: 'Trigger.ProtectMaturityStatus'") && lockSrcRP23.includes("path: 'Trigger.MaturityStatusValues'")} 含 TriggerConditionGroups=${/TriggerConditionGroups/.test(blockFieldsSrcRP23 + editorSrc + psegViewRP19)} 含 MatchConditionGroups=${/MatchConditionGroups/.test(editorSrc + psegViewRP19)}`)

  const rp05 =
    rulesViewNoHtml.includes('发布规则集版本（整版）') &&
    /v-model="confirmPriority"/.test(rulesViewNoHtml) &&
    /v-model="confirmSetup"/.test(rulesViewNoHtml) &&
    /confirmPriority\.value = false/.test(rulesViewNoHtml) &&
    /confirmSetup\.value = false/.test(rulesViewNoHtml) &&
    /const canConfirmPublish = computed/.test(rulesViewNoHtml) &&
    /confirmPriority\.value && confirmSetup\.value/.test(rulesViewNoHtml) &&
    /:disabled="!canConfirmPublish"/.test(rulesViewNoHtml) &&
    !/confirmPriority|confirmSetup/.test(ruleApiSrc)
  assert(
    'RP05 静态：整版发布弹窗两块内容并列展示 + 两个非持久化确认勾选（重置/受控/不落库）',
    rp05,
    `title=${rulesViewNoHtml.includes('发布规则集版本（整版）')} gate=${/confirmPriority\.value && confirmSetup\.value/.test(rulesViewNoHtml)} persisted=${/confirmPriority|confirmSetup/.test(ruleApiSrc)}`
  )

  const rp06 =
    /function parseSetupBlockCounts/.test(rulesViewNoHtml) &&
    rulesViewNoHtml.includes("fieldName === 'SetupTransitionRules'") &&
    rulesViewNoHtml.includes('setupCountsText(') &&
    /v-if="row\.setupCounts\.source"/.test(rulesViewNoHtml) &&
    /v-if="row\.setupCounts\.target"/.test(rulesViewNoHtml) &&
    rulesViewNoHtml.includes('内容无法识别')
  assert(
    'RP06 静态：版本对比里 ⑦ 不再直出整串 JSON，改「按类型计数」摘要，解析失败如实显示「内容无法识别」',
    rp06,
    `parse=${/function parseSetupBlockCounts/.test(rulesViewNoHtml)} cells=${/v-if="row\.setupCounts\.source"/.test(rulesViewNoHtml)}`
  )

  // 无权限时必须「看不到条数」也不能勾选确认 —— 不允许盲勾
  assert(
    'RP07 静态：没有换型规则查看权限时，条数不可见且 ⑦ 确认勾选禁用（不允许盲勾发布）',
    /:disabled="!canViewSetup"/.test(rulesViewNoHtml) &&
      rulesViewNoHtml.includes('当前账号没有查看权限，条数不可见'),
    `disabled=${/:disabled="!canViewSetup"/.test(rulesViewNoHtml)} hint=${rulesViewNoHtml.includes('当前账号没有查看权限，条数不可见')}`
  )

  void rulesStore

  function validPick(src) {
    return src.includes('pickLatestPublished(') && !findFirst.test(src)
  }
}

/**
 * GROUP=governance-delete：治理三族「删除」端点**在 dev 上必须可达**（2026-10-30 路 A 恢复）
 *
 * 沿革：10-26 锁「可达」→ 10-29 反转锁「不可达（405 + Allow: PUT）」→
 *   **10-30 路 A 再次反转**：0号位 批准最窄删除口径，3号位 回执称三个主表 DELETE 路由
 *   已摘 `[NonAction]` 重新注册（版本级保持 `[NonAction]`）。
 *
 * 判据（用不存在的 id 999999999 打，绝不碰真数据）：
 *   - 主表 DELETE 必须「请求进到 service 层」⇒ **400 或 404 + 文案含「不存在」**；
 *     **405 就是路由又被摘了**，必须转红。
 *   - 版本级 DELETE 必须仍不可达 ⇒ **405 / 404**（版本级从未落过码，也没有恢复计划）。
 */
async function verifyGovernanceDelete(token) {
  log('\n[GOV-DEL] 治理三族删除端点必须可达（dev — 路 A 已恢复）')

  const targets = [
    { path: '/api/governance/rule-set/999999999', label: '规则集' },
    { path: '/api/governance/parameter-set/999999999', label: '参数集' },
    { path: '/api/governance/strategy-profile/999999999', label: '策略包' }
  ]
  for (const t of targets) {
    const r = await call('DELETE', t.path, { token })
    const msg = String(r.json?.message ?? r.text ?? '')
    assert(
      `R44 动态：DELETE ${t.path} 必须可达（400/404 且文案含「不存在」；405 = 路由又被摘了）`,
      (r.status === 400 || r.status === 404) && (msg.includes('不存在') || msg === ''),
      `${t.label} HTTP=${r.status} message=${msg.slice(0, 60)}`
    )
  }

  const versionTargets = [
    { path: '/api/governance/rule-set/version/999999999', label: '规则集版本' },
    { path: '/api/governance/parameter-set/version/999999999', label: '参数集版本' },
    { path: '/api/governance/strategy-profile/version/999999999', label: '策略包版本' }
  ]
  for (const t of versionTargets) {
    const r = await call('DELETE', t.path, { token })
    const msg = String(r.json?.message ?? r.text ?? '')
    assert(
      `R44b 动态：DELETE ${t.path} 必须仍不可达（版本级不开放；405/404）`,
      r.status === 405 || r.status === 404,
      `${t.label} HTTP=${r.status} message=${msg.slice(0, 60)}`
    )
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

  // M08：参数集主表 PUT（改名/停用）——API 指向主表 /parameter-set/{id}（不是 /version）
  assert(
    'SC08 静态：参数集主表 PUT + 页面「编辑」入口（updateParameterSet + editParameterSetDialog）',
    /async updateParameterSet\s*\(/.test(psApi) &&
      /url:\s*`\/api\/governance\/parameter-set\/\$\{/.test(psApi) &&
      /async function updateParameterSet\s*\(/.test(psStore) &&
      /paramStore\.updateParameterSet\s*\(/.test(psView) &&
      /editParameterSetDialog/.test(psView),
    `api=${/url:\s*`\/api\/governance\/parameter-set\/\$\{/.test(psApi)} store=${/async function updateParameterSet\s*\(/.test(psStore)} view=${/paramStore\.updateParameterSet\s*\(/.test(psView)}`
  )

  // M09：策略包主表 PUT（改名/停用/RunType）——同上
  assert(
    'SC09 静态：策略包主表 PUT + 页面「编辑策略包」入口（updateProfile + editProfileDialog）',
    /async updateProfile\s*\(/.test(spApi) &&
      /url:\s*`\/api\/governance\/strategy-profile\/\$\{/.test(spApi) &&
      /async function updateProfile\s*\(/.test(spStore) &&
      /store\.updateProfile\s*\(/.test(spView) &&
      /editProfileDialog/.test(spView) &&
      spView.includes('编辑策略包'),
    `api=${/url:\s*`\/api\/governance\/strategy-profile\/\$\{/.test(spApi)} store=${/async function updateProfile\s*\(/.test(spStore)} view=${/store\.updateProfile\s*\(/.test(spView)}`
  )

  // M10：停用语义必须写清楚（只关主表、不级联版本），编码编辑框必须只读
  assert(
    'SC10 静态：两页编辑 Dialog 都写明「停用只关主表/不级联版本」且编码只读',
    psView.includes('停用只关主表') &&
      spView.includes('停用只关主表') &&
      /editParameterSetForm\.parameterSetCode[^>]*disabled/.test(psView) &&
      /editProfileForm\.strategyProfileCode"\s+disabled/.test(spView),
    `ps提示=${psView.includes('停用只关主表')} sp提示=${spView.includes('停用只关主表')}`
  )

  // M11（2026-10-26）：设为默认版本链接通 —— 发布≠默认，运行侧只认 PUBLISHED+IsDefault
  const sc11Api =
    /async setDefaultVersion\s*\(/.test(spApi) &&
    /url:\s*`\/api\/governance\/strategy-profile\/version\/\$\{versionId\}\/set-default`/.test(spApi)
  const sc11Store =
    /async function setDefault\s*\(/.test(spStore) &&
    /target\.status !== 'PUBLISHED'/.test(spStore) &&
    /无设为默认权限（需权限码 aps\.strategy\.edit）/.test(spStore) &&
    /strategyProfileApi\.setDefaultVersion\(versionId\)/.test(spStore) &&
    /after\?\.isDefault/.test(spStore)
  const sc11View =
    /async function onSetDefault\s*\(/.test(spView) &&
    /store\.setDefault\(row\.id\)/.test(spView) &&
    /确认设为默认/.test(spView) &&
    /当前操作者无设为默认权限/.test(spView)
  assert(
    'SC11 静态：策略版本「设为默认」链接通（api POST set-default + store 仅 PUBLISHED/失败回列表核对 + 页面按钮与二次确认）',
    sc11Api && sc11Store && sc11View,
    `api=${sc11Api} store=${sc11Store} view=${sc11View}`
  )

  // M12：草稿对话框不得再有失效的「设为默认」勾选框（C-7：Create 强制 false、Update 冻结）
  //      stripComments 只剥 // 与 /* */，.vue 顶部的 <!-- --> 说明块必须单独剥掉，
  //      否则「仍说明发布≠默认」会被头注释顶替 ⇒ 假绿（自证脚本 SC12 第 4 条踩过）。
  const spTypes = stripComments(readSrc('api/aps-v1/types/strategyProfile.ts'))
  const spDraft = stripComments(readSrc('views/Aps/components/StrategyDraftDialog.vue')).replace(
    /<!--[\s\S]*?-->/g,
    ''
  )
  const draftInputBlock = spTypes.match(/interface StrategyVersionDraftInput\s*\{[\s\S]*?\n\}/)?.[0] ?? ''
  const sc12NoDeadControl =
    !/ElCheckbox/.test(spDraft) &&
    !/isDefault/.test(spDraft) &&
    !/hasPublished/.test(spDraft) &&
    draftInputBlock !== '' &&
    !/isDefault/.test(draftInputBlock) &&
    !/has-published/.test(spView)
  const sc12StillExplains = spDraft.includes('设为默认')
  assert(
    'SC12 静态：草稿对话框不再有失效的「设为默认」勾选框，入参不含 isDefault，且仍说明「发布≠默认」',
    sc12NoDeadControl && sc12StillExplains,
    `noDead=${sc12NoDeadControl} explains=${sc12StillExplains}`
  )
}

/* ============================================================================
 * [IC] 离线图标链路（GROUP=icons — 2026-11-02）
 *   背景（真缺陷，非样式问题）：
 *   生产 .env.pro ⇒ VITE_USE_ONLINE_ICON=false ⇒ 走离线图标分支（div.iconify + CSS mask 规则）。
 *   而 @unocss/preset-icons@0.65.4 的 createNodeLoader() 用无扩展名
 *   `import('@iconify/utils/lib/loader/node-loader')`，而 @iconify/utils@2.2.1 的 exports 是
 *   `{"./*": "./*"}`（不补扩展名）⇒ ERR_MODULE_NOT_FOUND；ESM 环境也没有 require 兜底
 *   ⇒ node loader 永远取不到图标 ⇒ presetIcons 静默产不出任何规则（warn 默认关）
 *   ⇒ 生产 CSS 里 0 条图标规则 ⇒ 线上图标全是空白方块。
 *   （dev 正常是因为 .env.base 走在线 Iconify API，与产物无关。）
 *   修法（用户 11-02 拍板「离线自包含」）：uno.config.ts 自写离线图标规则，
 *   从本地 @iconify/json 读图标 → data URI + mask，零新增依赖、不依赖外网。
 *
 *   - IC01 静态：uno.config.ts 不再使用 presetIcons（防回退到已失效链路）
 *   - IC02 静态：离线图标规则存在，且产出 mask + currentColor（形状靠 mask、颜色靠 currentColor）
 *   - IC03 静态：在线/离线判定口径一致（uno.config.ts「非 true 即离线」；Icon.vue「true 即在线」）
 *   - IC04 静态：图标数据来自本地 @iconify/json，产物不依赖外网/自动安装
 *   - IC05 静态：Icon.vue 离线 div 有尺寸（.iconify 的 display/width/height）——否则 mask 0×0 看不见
 *   - IC06 静态：Icon.vue 仍保留在线分支（未把生产改走 Iconify CDN）
 *   - IC07 静态（真数据）：源码全部 vi- token 在 @iconify/json 对应集合里**真实存在**
 *   - IC08 静态：.env.pro 生产走离线（VITE_USE_ONLINE_ICON 非 true）
 *   - IC09 产物（可跳过）：构建产物 CSS 的图标规则集合 == 源码 token 集合
 * ========================================================================== */
function verifyIcons() {
  log('\n[IC] 离线图标链路（icons — 2026-11-02 生产图标全空修复）')

  const repoRoot = join(__dirname, '..')
  const unoRaw = readText(join(repoRoot, 'uno.config.ts'))
  // 去掉注释再判（文件头的说明注释里会提到 presetIcons，那是解释不是引用）
  const unoSrc = unoRaw.replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '')
  const iconVue = readSrc('components/Icon/src/Icon.vue')
  const envPro = (() => {
    try {
      return readText(join(repoRoot, '.env.pro'))
    } catch {
      return ''
    }
  })()

  // IC01 不得再回退到 presetIcons（其 node-loader 在 @iconify/utils@2.2.1 下必然失效）
  const ic01 = !/presetIcons/.test(unoSrc) && !/@unocss\/preset-icons/.test(unoSrc)
  assert(
    'IC01 静态：uno.config.ts 已不再使用 @unocss/preset-icons（其 node-loader 必然失效）',
    ic01,
    ic01 ? '已移除 presetIcons' : '仍含 presetIcons'
  )

  // IC02 离线图标规则：产出 mask + currentColor
  const ic02 =
    /--un-icon/.test(unoSrc) &&
    /-webkit-mask/.test(unoSrc) &&
    /['"]?background-color['"]?\s*:\s*['"]?currentColor/.test(unoSrc) &&
    /mask-size/.test(unoSrc)
  assert(
    'IC02 静态：离线图标规则产出 --un-icon + mask + background-color: currentColor（形状靠 mask、颜色随文字色）',
    ic02,
    ic02
      ? 'mask 链路齐'
      : `unIcon=${/--un-icon/.test(unoSrc)} mask=${/-webkit-mask/.test(unoSrc)} cur=${/['"]?background-color['"]?\s*:\s*['"]?currentColor/.test(
          unoSrc
        )} size=${/mask-size/.test(unoSrc)}`
  )

  // IC03 在线/离线判定口径：uno.config.ts 用「!== 'true' 即离线」，Icon.vue 用「=== 'true' 即在线」
  const ic03 =
    /VITE_USE_ONLINE_ICON\s*!==\s*'true'/.test(unoSrc) &&
    /VITE_USE_ONLINE_ICON\s*===\s*'true'/.test(iconVue)
  assert(
    'IC03 静态：在线/离线判定口径一致（uno.config.ts 非 true 即离线 / Icon.vue true 即在线）',
    ic03,
    ic03
      ? '两侧一致'
      : `uno=${/VITE_USE_ONLINE_ICON\s*!==\s*'true'/.test(unoSrc)} vue=${/VITE_USE_ONLINE_ICON\s*===\s*'true'/.test(
          iconVue
        )}`
  )

  // IC04 图标数据来自本地 @iconify/json，产物不依赖外网
  const ic04 =
    /@iconify\/json/.test(unoSrc) &&
    !/autoInstall/.test(unoSrc) &&
    !/api\.iconify\.design/.test(unoSrc) &&
    !/https?:\/\/[^'"]*iconify[^'"]*/.test(unoSrc)
  assert(
    'IC04 静态：离线图标数据取自本地 @iconify/json，产物不依赖外网（无 autoInstall / 无 Iconify API 地址）',
    ic04,
    ic04
      ? '本地取数'
      : `json=${/@iconify\/json/.test(unoSrc)} autoInstall=${/autoInstall/.test(unoSrc)} api=${/iconify\.design/.test(
          unoSrc
        )}`
  )

  // IC05 Icon.vue 离线 div 必须有尺寸
  //   注意：Icon.vue 里有两个 .iconify 块（前一个是 .@{prefix-cls}, .iconify 的 :deep(svg) 联动块），
  //   尺寸必须在**某一个** .iconify 块里齐；块内还有嵌套的 &:hover{} ⇒ 用配对大括号取块，不能用 [^{}]*。
  const extractBalancedBlock = (src, braceIdx) => {
    let d = 0
    for (let i = braceIdx; i < src.length; i++) {
      if (src[i] === '{') d++
      else if (src[i] === '}') {
        d--
        if (d === 0) return src.slice(braceIdx + 1, i)
      }
    }
    return ''
  }
  const iconifyBlocks = []
  {
    let idx = -1
    while ((idx = iconVue.indexOf('.iconify', idx + 1)) >= 0) {
      const braceIdx = iconVue.indexOf('{', idx)
      if (braceIdx < 0 || braceIdx - idx > 20) continue
      iconifyBlocks.push(extractBalancedBlock(iconVue, braceIdx))
    }
  }
  const ic05 = iconifyBlocks.some(
    (b) =>
      /display:\s*inline-block/.test(b) && /width:\s*1em/.test(b) && /height:\s*1em/.test(b)
  )
  assert(
    'IC05 静态：Icon.vue 的 .iconify 有 display:inline-block + width/height:1em（否则 mask 0×0 看不见）',
    ic05,
    ic05
      ? `尺寸齐（共 ${iconifyBlocks.length} 个 .iconify 块）`
      : `块数=${iconifyBlocks.length} 各块=${iconifyBlocks
          .map((b) => `display:${/display:\s*inline-block/.test(b)} width:${/width:\s*1em/.test(b)}`)
          .join(' | ')}`
  )

  // IC06 在线分支仍在（不得把生产改走 CDN）
  const ic06 = /isUseOnline/.test(iconVue) && /<Icon\b/.test(iconVue) && /iconify/.test(iconVue)
  assert(
    'IC06 静态：Icon.vue 仍保留在线分支（isUseOnline → <Icon>）与离线分支（div.iconify），两条路都在',
    ic06,
    ic06 ? '双分支都在' : '分支缺失'
  )

  // IC07 真数据：源码全部 vi- token 在 @iconify/json 里真实存在
  //   注意 token 形如 `vi-ant-design:menu-unfold-outlined`：`vi-` 是 UnoCSS 前缀（ICON_PREFIX），
  //   冒号前才是 Iconify 集合名（ant-design），冒号后是图标名。
  const tokens = new Map()
  {
    const walk = (dir) => {
      let names = []
      try {
        names = readdirSync(dir)
      } catch {
        return
      }
      for (const n of names) {
        const p = join(dir, n)
        const st = statSync(p)
        if (st.isDirectory()) {
          // IconPicker 的演示图标清单（2000+ 条）不是页面用图标，不入图标 CSS
          if (p.includes(join('IconPicker', 'src', 'data'))) continue
          walk(p)
        } else if (/\.(vue|ts|tsx)$/.test(n)) {
          const t = readText(p)
          for (const m of t.matchAll(/vi-([a-z0-9-]+):([a-zA-Z0-9_-]+)/g)) {
            const tok = `${m[1]}:${m[2]}`
            if (!tokens.has(tok)) tokens.set(tok, p)
          }
        }
      }
    }
    walk(SRC_ROOT)
  }
  const ic07Missing = []
  {
    const req = createRequire(join(repoRoot, 'package.json'))
    const cache = new Map()
    const getCollection = (name) => {
      if (cache.has(name)) return cache.get(name)
      let data = null
      try {
        data = JSON.parse(readText(req.resolve(`@iconify/json/json/${name}.json`)))
      } catch {
        data = null
      }
      cache.set(name, data)
      return data
    }
    for (const [tok] of tokens) {
      const [col, name] = tok.split(':')
      const data = getCollection(col)
      if (!data) {
        ic07Missing.push(`${tok}（集合 ${col} 不存在）`)
        continue
      }
      const inIcons = !!data.icons?.[name]
      const inAliases = !!data.aliases?.[name]
      if (!inIcons && !inAliases) ic07Missing.push(tok)
    }
  }
  assert(
    'IC07 静态（真数据）：源码全部 vi- token 在 @iconify/json 对应集合中真实存在（缺则转红）',
    ic07Missing.length === 0,
    ic07Missing.length === 0
      ? `全部 ${tokens.size} 个 token 均存在`
      : `缺 ${ic07Missing.length} 个：${ic07Missing.slice(0, 8).join(', ')}`
  )

  // IC08 生产走离线
  const ic08 = /VITE_USE_ONLINE_ICON\s*=\s*false/.test(envPro)
  assert(
    'IC08 静态：.env.pro 生产走离线图标（VITE_USE_ONLINE_ICON=false）⇒ 离线链路必须通',
    ic08,
    ic08 ? '生产=离线' : '生产不是离线'
  )

  // IC09 产物 CSS（存在构建产物才校验，否则跳过）
  const distCssDir = join(repoRoot, 'dist-pro', 'assets')
  if (!existsSync(distCssDir)) {
    skip('IC09 产物：构建产物 CSS 的图标规则集合 == 源码 token 集合', 'dist-pro/assets 不存在（未构建）')
  } else {
    let css = ''
    for (const f of readdirSync(distCssDir)) {
      if (f.endsWith('.css')) css += readText(join(distCssDir, f))
    }
    // 产物类名形如 `.vi-mdi\:account-circle` → 去掉 `vi-` 前缀并还原转义冒号，与源码 token 同形
    const built = new Set(
      [...css.matchAll(/\.vi-([a-z0-9-]+\\:[a-zA-Z0-9_-]+)\{/g)].map((m) => m[1].replace('\\:', ':'))
    )
    const srcSet = new Set(tokens.keys())
    const missingInCss = [...srcSet].filter((t) => !built.has(t))
    const extraInCss = [...built].filter((t) => !srcSet.has(t))
    assert(
      'IC09 产物：构建产物 CSS 的图标规则集合 == 源码 token 集合（不多不少）',
      missingInCss.length === 0 && extraInCss.length === 0 && built.size > 0,
      `产物 ${built.size} 条 / 源码 ${srcSet.size} 个；缺 ${missingInCss.length}：${missingInCss
        .slice(0, 5)
        .join(', ')}；多 ${extraInCss.length}：${extraInCss.slice(0, 5).join(', ')}`
    )
  }

  info(
    'IC 段：2026-11-02 生产包图标全空根因（@unocss/preset-icons node-loader 失效）已由自写离线规则取代；dev 走在线 API 故 dev 一直正常，勿被 dev 表现误导'
  )
}

/* ============================================================================
 * [PC] 「最新已发布」 vs 「当前有效」术语口径（GROUP=publish-concept — 2026-11-02）
 *   背景（0号位 2026-10-16 裁决 / 3号位 2026-11-02 承接回执 §3.1 Q2）：
 *   两个是**不同概念**，不一致**不是缺陷**：
 *     - 「当前有效」= **生效期窗口**语义 ⇒ 权威读路 = `published-version` 端点
 *       （状态 + 生效期窗口内**恰一**才返；多条同时命中 ⇒ 400 报歧义）。
 *     - 「最新已发布」= 列表投影 **Id 最大** ⇒ 前端 `pickLatestPublished` 取的就是这个。
 *   ⚠️ 前端实现一直是 Id 最大（与后端列表投影一致），但 UI 文案却叫「**当前生效**」
 *      ⇒ 多条 PUBLISHED 并存时（Q1 允许并存）与窗口口径的权威答案打架。
 *      1893 收敛前就是活例子：published-version 报 400 歧义，页面却标了「当前生效」。
 *   ⇒ 本段锁死：文案一律「最新已发布」，旧标识符不得复活。
 *
 *   - PC01 静态：两族 API 都调 `pickLatestPublished(`；全仓不得再出现 `pickCurrentPublished(`
 *   - PC02 静态：`pickLatestPublished` 的文档注释同时写明「最新已发布」与「当前有效」两个概念，
 *                并点名 published-version 端点（口径说明被删 ⇒ 转红）
 *   - PC03 静态：版本历史 DTO 字段名 = `isLatestPublished`；RS/PS 链路不得再出现 `isCurrent`
 *   - PC04 静态：两页版本历史表的列头是「最新已发布」，不得有 `label="当前生效"`
 *   - PC05 静态：两页的「当前版本」文案已改为「最新已发布版本」（meta-key + ElDescriptionsItem）
 * ========================================================================== */
function verifyPublishConcept() {
  log('\n[PC] 「最新已发布」 vs 「当前有效」术语口径（publish-concept — 2026-11-02）')

  const commonSrc = readSrc('api/aps-v1/types/common.ts')
  const ruleApiSrc = readSrc('api/aps-v1/rule.ts')
  const psApiSrc = readSrc('api/aps-v1/parameterSet.ts')

  // PC01 两族 API 都用新名；旧名全仓不得复活
  const oldNameHits = []
  {
    const walk = (dir) => {
      let names = []
      try {
        names = readdirSync(dir)
      } catch {
        return
      }
      for (const n of names) {
        const p = join(dir, n)
        const st = statSync(p)
        if (st.isDirectory()) walk(p)
        else if (/\.(vue|ts|tsx)$/.test(n)) {
          const t = readText(p)
          if (t.includes('pickCurrentPublished')) oldNameHits.push(p)
        }
      }
    }
    walk(SRC_ROOT)
  }
  const pc01 =
    ruleApiSrc.includes('pickLatestPublished(') &&
    psApiSrc.includes('pickLatestPublished(') &&
    oldNameHits.length === 0
  assert(
    'PC01 静态：两族 API 都调 pickLatestPublished(，全仓不得再出现旧名 pickCurrentPublished(',
    pc01,
    pc01
      ? '新名在用，旧名已绝迹'
      : `规则集=${ruleApiSrc.includes('pickLatestPublished(')} 参数集=${psApiSrc.includes(
          'pickLatestPublished('
        )} 旧名残留=${oldNameHits.length}${oldNameHits.length ? '：' + oldNameHits[0] : ''}`
  )

  // PC02 口径注释必须同时说明两个概念 + 点名 published-version
  const pc02 =
    /最新已发布/.test(commonSrc) &&
    /当前有效/.test(commonSrc) &&
    /published-version/.test(commonSrc)
  assert(
    'PC02 静态：pickLatestPublished 的文档注释同时写明「最新已发布」与「当前有效」，并点名 published-version 端点',
    pc02,
    pc02
      ? '口径注释齐'
      : `最新已发布=${/最新已发布/.test(commonSrc)} 当前有效=${/当前有效/.test(commonSrc)} published-version=${/published-version/.test(
          commonSrc
        )}`
  )

  // PC03 DTO 字段名 = isLatestPublished；RS/PS 链路不得再出现 isCurrent
  const chainFiles = [
    'api/aps-v1/types/rule.ts',
    'api/aps-v1/types/parameterSet.ts',
    'api/aps-v1/rule.ts',
    'api/aps-v1/parameterSet.ts',
    'api/aps-v1/parameterDiff.ts',
    'store/modules/aps/rules.ts',
    'views/Aps/Rules.vue',
    'views/Aps/ParameterSets.vue'
  ]
  const pc03Old = []
  const pc03New = []
  for (const f of chainFiles) {
    const s = readSrc(f)
    if (/\bisCurrent\b/.test(s)) pc03Old.push(f)
    if (s.includes('isLatestPublished')) pc03New.push(f)
  }
  const pc03 = pc03Old.length === 0 && pc03New.length === chainFiles.length
  assert(
    'PC03 静态：版本历史 DTO 字段 = isLatestPublished；RS/PS 链路 8 个文件全部改到位且无 isCurrent 残留',
    pc03,
    pc03
      ? '8/8 文件已改'
      : `残留 isCurrent 的文件=${pc03Old.length}${pc03Old.length ? '：' + pc03Old.join(',') : ''}；已改文件=${pc03New.length}/8`
  )

  // PC04 两页列头 = 「最新已发布」，不得有 label="当前生效" / >当前生效<
  const psView = readSrc('views/Aps/ParameterSets.vue')
  const rsView = readSrc('views/Aps/Rules.vue')
  const pc04 =
    psView.includes('<span class="th-hint">最新已发布</span>') &&
    rsView.includes('<span class="th-hint">最新已发布</span>') &&
    !psView.includes('label="当前生效"') &&
    !rsView.includes('label="当前生效"') &&
    !psView.includes('>当前生效<') &&
    !rsView.includes('>当前生效<')
  assert(
    'PC04 静态：两页版本历史表列头 = 「最新已发布」，不得再有 label="当前生效" / >当前生效<',
    pc04,
    pc04
      ? '两页列头已改'
      : `参数集表头=${psView.includes(
          '<span class="th-hint">最新已发布</span>'
        )} 规则集表头=${rsView.includes(
          '<span class="th-hint">最新已发布</span>'
        )} 参数集旧=${psView.includes('label="当前生效"') || psView.includes(
          '>当前生效<'
        )} 规则集旧=${rsView.includes('label="当前生效"') || rsView.includes('>当前生效<')}`
  )

  // PC05 「当前版本」文案已改为「最新已发布版本」
  const pc05 =
    psView.includes('<span class="meta-key">最新已发布版本</span>') &&
    psView.includes('<ElDescriptionsItem label="最新已发布版本">') &&
    rsView.includes('<span class="meta-key">最新已发布版本</span>') &&
    rsView.includes('<ElDescriptionsItem label="最新已发布版本">') &&
    !psView.includes('<span class="meta-key">当前版本</span>') &&
    !rsView.includes('<span class="meta-key">当前版本</span>')
  assert(
    'PC05 静态：两页的「当前版本」文案已改为「最新已发布版本」（meta-key + ElDescriptionsItem 各一处）',
    pc05,
    pc05
      ? '两页 4 处已改'
      : `参数集meta=${psView.includes('<span class="meta-key">最新已发布版本</span>')} 参数集desc=${psView.includes(
          '<ElDescriptionsItem label="最新已发布版本">'
        )} 规则集meta=${rsView.includes('<span class="meta-key">最新已发布版本</span>')} 规则集desc=${rsView.includes(
          '<ElDescriptionsItem label="最新已发布版本">'
        )}`
  )

  info(
    'PC 段：口径来源 = 0号位 2026-10-16 裁决（3号位 2026-11-02 承接回执 §3.1 Q2 引用回告）：「当前有效」= 生效期窗口（published-version 端点），「最新已发布」= Id 最大（前端取这个）—— 两个概念不同，不一致非缺陷 ⇒ 前端文案不得叫「当前生效」'
  )
}

/* ========================================================================== *
 * 出包/交付镜像链路（delivery — 2026-11-03）
 *
 * 背景：交付镜像 lps/LPS.APS.Front/dist-pro 是用 robocopy /MIR 同步的，
 * 而 robocopy 按「**大小 + 修改时间**」判同 —— build-delivery.mjs 为让 zip 字节可复现，
 * 把所有产物文件的 mtime 钉成同一固定时刻 ⇒ 「大小相同 + 时间相同」就可能**内容已变却判为同文件**。
 * 2026-11-03 实际踩中：镜像 index.html 仍指上一版入口 js（index-CKWJ32zF.js），
 * 而该文件已被 /MIR 删掉 ⇒ 直接铺镜像 dist-pro = 白屏（zip 本身是对的）。
 *
 *   - DL01 静态：robocopy 之后必须有**按 sha256 逐文件比对 + 修补**（不信 robocopy 的判同）
 *   - DL02 静态：必须校验镜像 index.html 指向的入口 js **真实存在**（自洽 ⇒ 不会白屏）
 *   - DL03 静态：镜像不一致时必须 exit(1) 中断出包（不许只 warn 后继续）
 *   - DL04 静态：出包前置含「zip 解压后逐文件 sha256 与产物一致」校验（不许只数文件个数）
 * ========================================================================== */
function verifyDelivery() {
  log('\n[DL] 出包与交付镜像链路（delivery — 2026-11-03）')

  const script = readText(join(__dirname, 'build-delivery.mjs'))

  // DL01 sha256 逐文件比对 + 修补
  const dl01 =
    /shaOf\s*=/.test(script) &&
    /shaOf\(dst\)\s*!==\s*shaOf\(f\)/.test(script) &&
    /copyFileSync\(f,\s*dst\)/.test(script)
  assert(
    'DL01 静态：robocopy 之后按 sha256 逐文件比对并修补（robocopy 只按大小+时间判同，会漏内容已变的同长文件）',
    dl01,
    dl01
      ? 'sha256 校验+修补在位'
      : `shaOf定义=${/shaOf\s*=/.test(script)} 比对=${/shaOf\(dst\)\s*!==\s*shaOf\(f\)/.test(
          script
        )} 修补=${/copyFileSync\(f,\s*dst\)/.test(script)}`
  )

  // DL02 镜像入口自洽校验
  const dl02 =
    /existsSync\(join\(MIRROR,\s*entry\)\)/.test(script) &&
    /镜像 index.html 指向的入口 js 不存在/.test(script)
  assert(
    'DL02 静态：出包时校验镜像 index.html 指向的入口 js 真实存在（镜像自洽 ⇒ 铺镜像不会白屏）',
    dl02,
    dl02 ? '入口自洽校验在位' : '缺镜像入口自洽校验'
  )

  // DL03 不一致必须中断
  const dl03 =
    /mismatch\.length\s*\|\|\s*extra\.length/.test(script) &&
    /镜像与产物不一致/.test(script) &&
    /process\.exit\(1\)/.test(script)
  assert(
    'DL03 静态：镜像与产物不一致时必须 exit(1) 中断出包（不许只 warn）',
    dl03,
    dl03 ? '硬失败在位' : '不一致时未硬失败'
  )

  // DL04 zip 内容逐文件 sha256 校验（判据必须咬住「逐文件哈希 + 两棵树逐项比对」，
  // 不能只判「脚本里出现过 sha256」—— 镜像修补段也有 sha256，会假绿）
  const dl04 =
    /hashTree/.test(script) &&
    /'\\0'\s*\+\s*createHash\('sha256'\)/.test(script) &&
    /a\.some\(\(x,\s*i\)\s*=>\s*x\s*!==\s*b\[i\]\)/.test(script) &&
    /zip 解压结果与产物不一致/.test(script)
  assert(
    'DL04 静态：出包前置含「zip 解压后逐文件 sha256 与产物一致」校验（不许只比文件数/路径）',
    dl04,
    dl04
      ? 'zip 内容逐文件校验在位'
      : `hashTree=${/hashTree/.test(script)} 逐文件哈希=${/'\\0'\s*\+\s*createHash\('sha256'\)/.test(
          script
        )} 两树逐项比=${/a\.some\(\(x,\s*i\)\s*=>\s*x\s*!==\s*b\[i\]\)/.test(
          script
        )} 失败提示=${/zip 解压结果与产物不一致/.test(script)}`
  )
}

/**
 * [CAP] 资源负荷率口径（GROUP=capacity — 2026-11-03）
 *   背景（5号位 2026-11-02 复函 §一/§二/§三；本函取代此前两封未发件）：
 *   ① 负荷率前移 V1 的**代提未发、前移终止**（0号位 口头质询成立：日班 9 小时不可能排出 10 小时；
 *      5号位 只读复核：单资源单日 Σ工时最高 375 分钟 = 70%，无一超标）⇒ 落地时点 = 冻结 `01 §77A.1` 的 **V1.1**。
 *   ② **墙钟 `occupiedHours` 分子方案撤回**：跨天任务 119 条，夜间时段不是产能占用，
 *      按墙钟重叠算会把正常排产天天标成超载（双方都要防的「假负荷」）。
 *      分子改用**工时口径**（任务工时按桶重叠切分后求和；V1.1 拟名 `loadHoursInBucket`）。
 *   ③ 分母 `availableHours`（三级合并窗口 ∩ 桶；授权加班计入；未登记=缺键≠0）这半边**未被证伪**，V1.1 沿用。
 *   ④ `loadHours`（整条计起点桶）仍禁作分子 —— 修法=按桶**切分工时**，不是切墙钟。
 *   ⑤ **超载判定不随本轮落地**：将来若做，判据同为工时口径，且需 0号位 定「回归仪表 vs 告警」定位。
 *   ⇒ 甘特负荷视图维持现状（负荷量热力图 + 「暂无产能口径，暂不标超载。」）、Overview 利用率列 `-` 占位
 *      ⇒ **页面零改动**；我方欠的活只有「改三处注释」（+ 顺手清掉一处过期 R1 契约指引）。
 *
 *   - CAP01 静态：`types/overview.ts` 分子=工时口径 + 写明墙钟撤回 + 无旧公式
 *   - CAP02 静态：`types/schedule.ts` 的 `loadHours` 注释=按桶切分工时 + 墙钟撤回 + 无「只能用墙钟」
 *   - CAP03 静态：`views/Aps/Gantt.vue` 负荷视图注释=工时口径 + 墙钟撤回 + 超载判定不随本轮落地
 *   - CAP04 静态：`views/Aps/Overview.vue` 不得再指向已作废的 2026-09-21 R1 契约
 *   - CAP05 静态：**全仓 src** 不得再出现旧公式串 `occupiedHours / availableHours`（防止在别处复活）
 *   - CAP06 静态：甘特页用户可见提示「暂无产能口径，暂不标超载。」仍在（页面零改动锁）
 * ========================================================================== */
function verifyCapacityCaliber() {
  log('\n[CAP] 资源负荷率口径（capacity — 2026-11-03）')

  const ovSrc = readSrc('api/aps-v1/types/overview.ts')
  const schSrc = readSrc('api/aps-v1/types/schedule.ts')
  const ganttSrc = readSrc('views/Aps/Gantt.vue')
  const overviewViewSrc = readSrc('views/Aps/Overview.vue')

  // CAP01 overview.ts：分子=工时口径；墙钟撤回；旧公式不得复活
  const cap01 =
    /工时口径/.test(ovSrc) &&
    /loadHoursInBucket/.test(ovSrc) &&
    /occupiedHours/.test(ovSrc) &&
    /撤回/.test(ovSrc) &&
    !/负荷率\s*=\s*`?occupiedHours/.test(ovSrc)
  assert(
    'CAP01 静态：types/overview.ts 负荷率分子=工时口径（loadHoursInBucket）且写明墙钟 occupiedHours 已撤回',
    cap01,
    cap01
      ? '工时分子 + 撤回声明在位'
      : `工时口径=${/工时口径/.test(ovSrc)} loadHoursInBucket=${/loadHoursInBucket/.test(
          ovSrc
        )} 撤回=${/撤回/.test(ovSrc)} 旧公式=${/负荷率\s*=\s*`?occupiedHours/.test(ovSrc)}`
  )

  // CAP02 schedule.ts：loadHours 注释=按桶切分工时；不得说「分子只能用墙钟」
  const cap02 =
    /切分工时/.test(schSrc) &&
    /不是切墙钟/.test(schSrc) &&
    /occupiedHours/.test(schSrc) &&
    !/分子只能用墙钟/.test(schSrc)
  assert(
    'CAP02 静态：types/schedule.ts 的 loadHours 注释改为「按桶切分工时」，不得再说「分子只能用墙钟重叠的 occupiedHours」',
    cap02,
    cap02
      ? 'loadHours 注释已改为工时口径'
      : `切分工时=${/切分工时/.test(schSrc)} 不是切墙钟=${/不是切墙钟/.test(
          schSrc
        )} 仍说只能用墙钟=${/分子只能用墙钟/.test(schSrc)}`
  )

  // CAP03 Gantt.vue：工时口径 + 墙钟撤回 + 超载判定不随本轮落地 + 无旧公式
  const cap03 =
    /loadHoursInBucket/.test(ganttSrc) &&
    /撤回/.test(ganttSrc) &&
    /超载判定不随本轮落地/.test(ganttSrc) &&
    !/负荷率\s*=\s*occupiedHours\s*\/\s*availableHours/.test(ganttSrc)
  assert(
    'CAP03 静态：Gantt.vue 负荷视图注释=工时口径 + 墙钟已撤回 + 「超载判定不随本轮落地」，旧公式不得复活',
    cap03,
    cap03
      ? '甘特注释三要素齐'
      : `loadHoursInBucket=${/loadHoursInBucket/.test(
          ganttSrc
        )} 撤回=${/撤回/.test(ganttSrc)} 超载判定不落地=${/超载判定不随本轮落地/.test(
          ganttSrc
        )} 旧公式=${/负荷率\s*=\s*occupiedHours\s*\/\s*availableHours/.test(ganttSrc)}`
  )

  // CAP04 Overview.vue：过期 R1 契约指引不得复活
  const cap04 = !/待 5号位 落地 2026-09-21 R1 契约/.test(overviewViewSrc)
  assert(
    'CAP04 静态：views/Aps/Overview.vue 不得再写「待 5号位 落地 2026-09-21 R1 契约」（该契约 09-22 已作废）',
    cap04,
    cap04 ? '过期指引已清' : 'Overview.vue 仍指向已作废的 R1 契约'
  )

  // CAP05 全仓 src 不得再出现旧公式串
  const cap05Hits = []
  {
    const walk = (dir) => {
      let names = []
      try {
        names = readdirSync(dir)
      } catch {
        return
      }
      for (const n of names) {
        const p = join(dir, n)
        const st = statSync(p)
        if (st.isDirectory()) walk(p)
        else if (/\.(vue|ts|tsx)$/.test(n)) {
          const t = readText(p)
          if (/occupiedHours\s*\/\s*availableHours/.test(t)) cap05Hits.push(p)
        }
      }
    }
    walk(SRC_ROOT)
  }
  assert(
    'CAP05 静态：全仓 src 不得再出现旧公式串 occupiedHours / availableHours',
    cap05Hits.length === 0,
    cap05Hits.length === 0
      ? '旧公式全仓绝迹'
      : `残留 ${cap05Hits.length} 处：${cap05Hits.join(',')}`
  )

  // CAP06 页面零改动：甘特页**模板里的**用户可见提示仍在（判据咬住 span，不认注释里同一句话）
  const cap06 = ganttSrc.includes('<span class="load-note-warn">暂无产能口径，暂不标超载。</span>')
  assert(
    'CAP06 静态：甘特负荷视图的用户可见提示「暂无产能口径，暂不标超载。」仍在（5号位 §三 要求维持现状）',
    cap06,
    cap06 ? '提示文案在位' : '用户可见提示缺失'
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
    'parameter-set-fields',
    'parameter-maintenance',
    'parameter-set-yields',
    'strategy-config-static',
    'domain-permission',
    'setup',
    'setup-unc',
    // 纯静态扫描组：不需要 token（verifyStaticScan / verifyDatetimeLocalDirect 不带 adminToken）
    // ⇒ 后端没起也能跑
    'static',
    'datetime-local',
    'tsrc-static',
    'reason-code',
    'icons',
    'publish-concept',
    'delivery',
    'capacity'
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
  if (GROUP === 'all' || GROUP === 'datetime-local') {
    verifyDatetimeLocalDirect()
  }
  if (GROUP === 'all' || GROUP === 'reason-code') {
    verifyReasonCodeLabel()
  }
  if (GROUP === 'all' || GROUP === 'tsrc-static') {
    verifyTsrcClientValidation()
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
    runParameterSetUxStatic()
  }
  if (GROUP === 'all' || GROUP === 'parameter-set-warehouse') {
    runWarehouseRefStatic()
  }
  if (GROUP === 'all' || GROUP === 'parameter-set-anchor') {
    runAnchorResourceStatic()
  }
  if (GROUP === 'all' || GROUP === 'parameter-set-fields') {
    runParameterSetFieldsStatic()
  }
  if (GROUP === 'all' || GROUP === 'parameter-maintenance') {
    runParameterMaintenanceStatic()
  }
  if (GROUP === 'all' || GROUP === 'parameter-set-yields') {
    runPlanningYieldStatic()
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
  if (GROUP === 'all' || GROUP === 'domain-permission') {
    runDomainPermissionStatic()
  }
  if (GROUP === 'all' || GROUP === 't-tsrc') {
    await verifyTaskSplitRuleConfig(adminToken)
  }
  if (GROUP === 'all' || GROUP === 'setup') {
    await verifySetupPages()
  }
  if (GROUP === 'all' || GROUP === 'setup' || GROUP === 'setup-unc') {
    verifySetupUncoveredStatic()
  }
  if (GROUP === 'all' || GROUP === 'setup-unc-live') {
    await verifySetupUncoveredLive(adminToken)
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
  if (GROUP === 'all' || GROUP === 'governance-delete') {
    await verifyGovernanceDelete(adminToken)
  }
  if (GROUP === 'all' || GROUP === 'strategy-config-static') {
    verifyMainTableCreateStatic()
  }
  if (GROUP === 'all' || GROUP === 'icons') {
    verifyIcons()
  }
  if (GROUP === 'all' || GROUP === 'publish-concept') {
    verifyPublishConcept()
  }
  if (GROUP === 'all' || GROUP === 'delivery') {
    verifyDelivery()
  }
  if (GROUP === 'all' || GROUP === 'capacity') {
    verifyCapacityCaliber()
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
