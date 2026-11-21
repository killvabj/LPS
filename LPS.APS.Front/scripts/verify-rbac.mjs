#!/usr/bin/env node
/**
 * APS V1 4号位 — 路由级 RBAC 离线验证脚本（v1.2 §23.1，Phase B 迁移至后端 34 码 + DDL 角色码）
 *
 * 用途：不启 dev server、不依赖浏览器，直接在 Node 里跑出
 *       "6 角色 × 29 路由" RBAC 矩阵，立刻看到红绿。
 *
 * 设计：复刻 src/router/guard.ts isRouteAllowed 逻辑 + MOCK_ROLE_PRESETS 权限码，
 *       与 router/modules/aps.ts 路由 meta 严格对齐。
 *
 * 用法：node scripts/verify-rbac.mjs
 *       （或 pnpm ts:check / lint 通过后任意时刻）
 *
 * 退出码：所有期望匹配则 0；不匹配则 1。
 *
 * 2026-11-21：两处对齐（依据 APS_V1_3号位致4号位_P01-P13收口v0.2回执_H1-H10逐条核证裁决_
 *   回执_v1.0_20261120.md H10② 及其「附带知悉」）：
 *   · planner 权限码表**收回** `aps.manual_eta.edit` / `aps.manual_eta.cancel`（留 view）；
 *   · `/aps/manual-eta` 路由 meta 追加 `apsRequiredPermissions: ['aps.manual_eta.view']`（按权限码二次收敛）。
 *   ⇒ 矩阵期望值不变（planner 仍可进页只读；admin.system 可写；viewer / admin.aps 仍 403）。
 *
 * 2026-11-21：**修假绿 + 加源一致性守卫**（既有漂移，非本轮回执引入）。
 *   · 假绿：`/aps/run` 本表手抄的 meta（aps.plan.view）与真实路由 11-20 的改动
 *     （apsRequiredRoles: ['aps.admin.system']，只限系统管理员进入）漂移 ⇒ 那 4 格全是假绿。
 *     现改为真实 meta，期望值随之翻转为「只有 aps.admin.system 进得来」。
 *   · 守卫①（路由 meta）：静态解析 src/router/modules/aps.ts（**先剥注释**）与本表逐条比对，
 *     路径缺 / meta 不同 ⇒ **判红退出**（不再输出误导性的矩阵）；真实有而本表没覆盖 ⇒ ⚠️ 提示。
 *   · 守卫②（权限码）：本表的 ROLE_PRESETS 原为**手抄副本**，与 auth.ts 真值漂移，且把手抄
 *     「admin.system 持 aps.parameter.publish」当真（0号位 10-19 裁定：**仅 aps.admin.aps** 持），
 *     另少 12 个码 ⇒ 现改为**直接解析 auth.ts 的 MOCK_ROLE_PRESETS**。
 *     ⇒ 复现检查：改 auth.ts 的权限码 ⇒ 矩阵立即跟着变，不会再出现「脚本说一套、页面是另一套」。
 *   · 改这两处来源后 88 格判定**逐格未变**（漂移涉及的码不出现在已覆盖 22 路由的 meta 里）。
 *
 * 2026-11-21：矩阵扩到 **6 角色 × 29 路由**（依据《APS_V1_P01-P13 全页面业务设计统一收口核对稿 v0.2》
 *   §3 九类角色表 + §4.9.1 场景表）：
 *   · 新增两个业务角色列：`aps.supervisor.workshop`（车间计划员）/ `aps.coordinator.material`（采购担当）。
 *   · 补进此前只在「覆盖缺口」里报警的 7 条路由（pegging-trace / procurement / operation-planning-mode /
 *     resource-calendar / production-work-calendar / parameter-set / task-split-rule-config）
 *     ⇒ 覆盖缺口归零，174 格全部有期望值。
 *   · 路由 meta 同步两处：`/aps/manual-eta` 加角色 `aps.coordinator.material`（采购担当维护人工ETA）；
 *     `/aps/explanation` 加角色门控 `['aps.planner','aps.admin.system']`（P08 = PMC＋管理员，
 *     车间计划员虽持 aps.plan.run 也进不来）。
 *   · 变异自证（改坏必转红）：① coordinator 去掉 `aps.manual_eta.view` ⇒ /aps/manual-eta 那格转红；
 *     ② 路由 meta 去掉 coordinator ⇒ 守卫① 判红退出；③ workshop 去掉 `aps.candidate.confirm`
 *     ⇒ /aps/candidate 那格转红。
 */

import { readFileSync } from 'node:fs'

/* ===== 22 路由 meta（与 router/modules/aps.ts 严格一致；v1.4 加 strategy-profile；v1.5 加 setup/* 4 条；09-20 加 audit）===== */
const ROUTES = [
  // 第一波（规则 / Domain）
  // 2026-10-28（3号位 自裁 W-2）：放开 aps.rule.view 只读入口 ⇒ viewer 可进 /aps/rules 只读
  {
    path: '/aps/rules',
    meta: { apsRequiredPermissions: ['aps.rule.view', 'aps.rule.edit', 'aps.rule.publish'] }
  },
  { path: '/aps/domain', meta: { apsRequiredRoles: ['aps.admin.system'] } },
  // 第二波（PMC 写操作）
  { path: '/aps/ctp', meta: { apsRequiredPermissions: ['aps.ctp.evaluate'] } },
  { path: '/aps/candidate', meta: { apsRequiredPermissions: ['aps.candidate.confirm'] } },
  {
    path: '/aps/manual-eta',
    // 2026-11-21（H10② 附带知悉）：按权限码二次收敛 —— 角色 + `aps.manual_eta.view`（AND）
    // 2026-11-21（v0.2 稿 §3 采购担当）：角色表加 `aps.coordinator.material`（采购担当持 edit/cancel）
    meta: {
      apsRequiredRoles: ['aps.planner', 'aps.admin.system', 'aps.coordinator.material'],
      apsRequiredPermissions: ['aps.manual_eta.view']
    }
  },
  {
    path: '/aps/demand-protection',
    meta: { apsRequiredRoles: ['aps.planner', 'aps.admin.system'] }
  },
  // 第三波（只读页 - 4 角色均持 aps.plan.view，全员放行）
  { path: '/aps/overview', meta: { apsRequiredPermissions: ['aps.plan.view'] } },
  { path: '/aps/order', meta: { apsRequiredPermissions: ['aps.plan.view'] } },
  { path: '/aps/gantt', meta: { apsRequiredPermissions: ['aps.plan.view'] } },
  // 2026-11-21（v0.2 稿 §P08 + §三 权限一致）：P08 收窄为 **Domain PMC + 系统管理员**
  //   ⇒ 门控码从 aps.plan.view（4 角色均持）改为 aps.plan.run（planner + admin.system）
  // 2026-11-21 再加角色门控：车间计划员拿到 aps.plan.run（场景7～9 端点码）后，
  //   仅凭权限码会把它放进 P08 ⇒ 稿 §9 勾稽「P08 PMC＋管理员」⇒ 角色 + 权限码（AND）
  {
    path: '/aps/explanation',
    meta: {
      apsRequiredRoles: ['aps.planner', 'aps.admin.system'],
      apsRequiredPermissions: ['aps.plan.run']
    }
  },
  // 2026-11-21：修**假绿** —— 本表原写 aps.plan.view，而真实路由 11-20 已改为
  //   「只限系统管理员进入」（apsRequiredRoles: ['aps.admin.system']）⇒ 期望值整行翻转。
  { path: '/aps/run', meta: { apsRequiredRoles: ['aps.admin.system'] } },
  { path: '/aps/pi', meta: { apsRequiredPermissions: ['aps.plan.view'] } },
  // 2026-11-21：补 7 条此前只在「覆盖缺口」里报警、没进矩阵的路由
  //   （采购担当/车间计划员这两个新角色常用其中 4 条 ⇒ 必须进矩阵才算验过）
  { path: '/aps/pegging-trace', meta: { apsRequiredPermissions: ['aps.plan.view'] } },
  { path: '/aps/procurement', meta: { apsRequiredPermissions: ['aps.plan.view'] } },
  {
    path: '/aps/operation-planning-mode',
    meta: { apsRequiredPermissions: ['aps.rule.view', 'aps.rule.edit'] }
  },
  {
    path: '/aps/resource-calendar',
    meta: {
      apsRequiredPermissions: ['aps.resource_calendar.view', 'aps.manual_capacity.view']
    }
  },
  {
    path: '/aps/production-work-calendar',
    meta: { apsRequiredPermissions: ['aps.resource_calendar.view'] }
  },
  {
    path: '/aps/parameter-set',
    meta: {
      apsRequiredPermissions: [
        'aps.parameter.view',
        'aps.parameter.edit',
        'aps.parameter.publish'
      ]
    }
  },
  {
    path: '/aps/task-split-rule-config',
    meta: { apsRequiredPermissions: ['aps.rule.view', 'aps.rule.edit'] }
  },
  // v1.4 §十八.10：策略配置（长期欠账收口；独立路由不混入 Rules.vue）
  // OR 语义：aps.strategy.view/edit/publish 任一即可（VIEWER 持 view 可进，admin.aps 持 view+edit，admin.system 持全部）
  {
    path: '/aps/strategy-profile',
    meta: { apsRequiredPermissions: ['aps.strategy.view', 'aps.strategy.edit', 'aps.strategy.publish'] }
  },
  // v1.5 Setup 专项：4 条 setup 路由（页面 2.1-2.4；2.5 红线为组件无路由）
  {
    path: '/aps/setup/exact',
    meta: { apsRequiredPermissions: ['aps.setup.view', 'aps.setup.edit'] }
  },
  {
    path: '/aps/setup/default',
    meta: { apsRequiredPermissions: ['aps.setup.view', 'aps.setup.edit'] }
  },
  {
    path: '/aps/setup/uncovered',
    meta: { apsRequiredPermissions: ['aps.setup.view'] }
  },
  {
    path: '/aps/setup/diff',
    meta: { apsRequiredPermissions: ['aps.setup.view'] }
  },
  // Pkg-8：RBAC 管理 UI（仅 aps.admin.system — 持 aps.auth.user.edit）
  { path: '/aps/rbac-users', meta: { apsRequiredPermissions: ['aps.auth.user.edit'] } },
  { path: '/aps/rbac-roles', meta: { apsRequiredPermissions: ['aps.auth.user.edit'] } },
  { path: '/aps/rbac-permissions', meta: { apsRequiredPermissions: ['aps.auth.user.edit'] } },
  { path: '/aps/rbac-scopes', meta: { apsRequiredPermissions: ['aps.auth.user.edit'] } },
  // 审计日志页（§22.6 / U42）：专属码 aps.audit.view，rbac.md §1.2 约定 4 角色均持
  { path: '/aps/audit', meta: { apsRequiredPermissions: ['aps.audit.view'] } }
]

/* ===== 4 角色权限码：来源 = src/store/modules/aps/auth.ts MOCK_ROLE_PRESETS（前端 mock 真值）=====
 *
 * 2026-11-21：原为**手抄副本**（11/17/14/37），与真值漂移，且含一处**实质错误**：
 *   手抄表给 `aps.admin.system` 加了 `aps.parameter.publish`，而 0号位 10-19 裁定
 *   「参数集发布独立码**仅 aps.admin.aps** 持有，admin.system 刻意不持」⇒ 手抄表在讲一个假口径。
 *   另少 12 个码（resource_calendar / manual_capacity 六个 + domain.edit 等）。
 *   现改为**直接解析 auth.ts**（前端 mock 模式下的运行时真值，也是本工具设计的声明来源，
 *   见 docs/rbac.md §4.1）⇒ 手抄漂移这类假绿不可能再发生。
 *   ⚠️ dev 真实库（scripts/seed-dev.mjs）另有自己的一套，**与 mock preset 不完全相同**：
 *      本矩阵校验的是「前端 mock 模式下的路由门控」，不是运行库授权现状。
 */
const AUTH_FILE = new URL('../src/store/modules/aps/auth.ts', import.meta.url)

/** 解析 auth.ts 的 MOCK_ROLE_PRESETS → { 角色码: string[] } */
function parseMockRolePresets(src) {
  const start = src.indexOf('const MOCK_ROLE_PRESETS')
  if (start < 0) throw new Error('auth.ts 里找不到 MOCK_ROLE_PRESETS')
  const text = stripComments(src.slice(start))
  const keyRe = /'(aps\.[a-z.]+)':\s*\{/g
  const hits = []
  let m
  while ((m = keyRe.exec(text))) hits.push({ key: m[1], at: m.index + m[0].length })
  const out = {}
  for (let i = 0; i < hits.length; i++) {
    const chunk = text.slice(hits[i].at, i + 1 < hits.length ? hits[i + 1].at : text.length)
    const pm = chunk.match(/permissions:\s*\[([\s\S]*?)\]/)
    if (!pm) continue
    out[hits[i].key] = [...pm[1].matchAll(/'([^']+)'/g)].map((x) => x[1])
  }
  return out
}

let ROLE_PRESETS = {}
try {
  ROLE_PRESETS = parseMockRolePresets(readFileSync(AUTH_FILE, 'utf8'))
} catch (e) {
  console.log('❌ 读不到 src/store/modules/aps/auth.ts，权限码真值无法加载：' + e.message)
  process.exit(1)
}

/** 矩阵只跑这 4 个 DDL 常用角色（auth.ts 里另有 3 个 dev 未 seed 的演示角色） */
const ROLES = [
  'aps.viewer.management',
  'aps.planner',
  'aps.admin.aps',
  'aps.admin.system',
  // 2026-11-21（v0.2 稿 §3）：两个业务角色进矩阵
  //  - aps.supervisor.workshop = 车间计划员（场景7～9 + 候选比较/确认）
  //  - aps.coordinator.material = 采购担当（只维护合法 Manual ETA）
  'aps.supervisor.workshop',
  'aps.coordinator.material'
]
for (const r of ROLES) {
  if (!ROLE_PRESETS[r]) {
    console.log(`❌ auth.ts 的 MOCK_ROLE_PRESETS 里没有角色 ${r}，矩阵无法执行`)
    process.exit(1)
  }
}

/* ===== 源一致性守卫：本表 ROUTES 必须与 src/router/modules/aps.ts 真值一致 =====
 *
 * 起因（2026-11-21）：本表是**手抄**的路由 meta，抄漏抄错不会报错 ⇒ 出过一次**假绿**：
 *   `/aps/run` 本表写 aps.plan.view，真实路由 11-20 已改「只限系统管理员进入」
 *   （apsRequiredRoles: ['aps.admin.system']）⇒ 矩阵那行全绿，实际 planner/viewer 进得去。
 *
 * 做法：静态解析真实路由文件（**先剥注释**），抽出每条带 RBAC meta 的路由，与本表逐条比对：
 *   - 路径缺 / meta 不同 / 本表有而真实没有 ⇒ ❌ 判红并退出（矩阵不再输出，避免误导）
 *   - 真实有而本表没覆盖 ⇒ ⚠️ 覆盖缺口（提示，不判红）
 *
 * ⚠️ 剥注释是必须的：aps.ts 里大量说明性注释写着 apsRequired* 字样（如 H1 甲案的
 *    「落码后改挂 apsRequiredPermissions: ['aps.admin.run']」），不剥会把注释读成真 meta。
 */
function stripComments(src) {
  return src
    .replace(/\/\*[\s\S]*?\*\//g, '') // 块注释（文件头说明段）
    .split('\n')
    .filter((l) => !l.trim().startsWith('//')) // 行注释（aps.ts 无行尾注释，已核）
    .join('\n')
}

/** 解析路由文件 → Map<fullPath, { roles: string[], perms: string[] }> */
function parseRouterMetas(src) {
  const text = stripComments(src)
  // 逐字符算花括号深度（字符串里无花括号，aps.ts 已核）
  const depthAt = new Int32Array(text.length + 1)
  let d = 0
  for (let i = 0; i < text.length; i++) {
    depthAt[i] = d
    if (text[i] === '{') d++
    else if (text[i] === '}') d--
  }
  depthAt[text.length] = d

  // 找出所有 path 出现处 + 其深度
  const entries = []
  const pathRe = /path:\s*'([^']+)'/g
  let m
  while ((m = pathRe.exec(text))) {
    entries.push({ raw: m[1], index: m.index, depth: depthAt[m.index] })
  }

  const result = new Map()
  for (let i = 0; i < entries.length; i++) {
    const e = entries[i]
    // 父路由 = 前面最后一个深度更小的 path
    let parentRaw = null
    for (let k = i - 1; k >= 0; k--) {
      if (entries[k].depth < e.depth) {
        parentRaw = entries[k]
        break
      }
    }
    // 全路径：绝对路径直接用；相对路径挂到父路由下
    let full
    if (e.raw.startsWith('/')) {
      full = e.raw
    } else if (parentRaw) {
      const parentFull = parentRaw.raw.startsWith('/')
        ? parentRaw.raw
        : (result.get(parentRaw.raw)?.full ?? '')
      full = `${parentFull}/${e.raw}`
    } else {
      full = e.raw
    }
    // 本路由的**自身 meta 属性**（不能取到子路由的 meta）：
    //   ① 路由对象字面量：开括号 = i 之前最后一个 depthAt === d-1 的 '{'；闭括号 = 之后第一个 depthAt === d 的 '}'
    //   ② 该对象里**直属**的 meta 属性：在对象内找 depthAt === d 的第一个 'meta:'（子路由的 meta 深度更大，会被跳过）
    //   ③ meta 属性的花括号块内才有 apsRequired* 字段
    let objStart = 0
    for (let j = e.index - 1; j >= 0; j--) {
      if (text[j] === '{' && depthAt[j] === e.depth - 1) {
        objStart = j
        break
      }
    }
    let objEnd = text.length
    for (let j = objStart + 1; j < text.length; j++) {
      if (text[j] === '}' && depthAt[j] === e.depth) {
        objEnd = j
        break
      }
    }
    const objText = text.slice(objStart, objEnd)
    let slice = ''
    const metaRe = /meta\s*:\s*\{/g
    let mm
    while ((mm = metaRe.exec(objText))) {
      if (depthAt[objStart + mm.index] === e.depth) {
        // 该 meta 属性的花括号块：开括号 depthAt === d，闭括号 depthAt === d+1
        const braceRel = mm.index + mm[0].length - 1
        let metaEnd = objText.length
        for (let j = braceRel + 1; j < objText.length; j++) {
          if (objText[j] === '}' && depthAt[objStart + j] === e.depth + 1) {
            metaEnd = j
            break
          }
        }
        slice = objText.slice(braceRel, metaEnd)
        break
      }
    }
    const rolesM = slice.match(/apsRequiredRoles:\s*\[([^\]]*)\]/)
    const permsM = slice.match(/apsRequiredPermissions:\s*\[([^\]]*)\]/)
    const parseList = (mm) =>
      mm ? mm[1].split(',').map((x) => x.trim().replace(/^'|'$/g, '')).filter(Boolean) : null
    const roles = parseList(rolesM)
    const perms = parseList(permsM)
    if (!roles && !perms) continue // 无 RBAC meta 的分组壳/普通路由 → 守卫不判
    result.set(full, { full, roles, perms })
  }
  return result
}

const ROUTER_FILE = new URL('../src/router/modules/aps.ts', import.meta.url)
let routerMetas = new Map()
try {
  routerMetas = parseRouterMetas(readFileSync(ROUTER_FILE, 'utf8'))
} catch (e) {
  console.log('❌ 读不到 src/router/modules/aps.ts，守卫无法执行：' + e.message)
  process.exit(1)
}

/** 比较两串数组（集合比较，忽略顺序） */
function sameSet(a, b) {
  const x = [...(a ?? [])].map((s) => s.trim()).sort()
  const y = [...(b ?? [])].map((s) => s.trim()).sort()
  return x.length === y.length && x.every((v, i) => v === y[i])
}

const driftErrors = []
const coverageGaps = []
for (const [path, real] of routerMetas) {
  const mine = ROUTES.find((r) => r.path === path)
  if (!mine) {
    coverageGaps.push(`${path}（角色 ${real.roles?.join('|') ?? '无'} / 权限码 ${real.perms?.join('|') ?? '无'}）`)
    continue
  }
  const mineRoles = mine.meta.apsRequiredRoles ?? null
  const minePerms = mine.meta.apsRequiredPermissions ?? null
  if (!sameSet(real.roles, mineRoles) || !sameSet(real.perms, minePerms)) {
    driftErrors.push(
      `${path}：本表 = 角色[${mineRoles?.join('|') ?? '无'}] 权限码[${minePerms?.join('|') ?? '无'}]` +
        ` ≠ 真实路由 = 角色[${real.roles?.join('|') ?? '无'}] 权限码[${real.perms?.join('|') ?? '无'}]`
    )
  }
}
for (const r of ROUTES) {
  if (!routerMetas.has(r.path)) {
    driftErrors.push(`${r.path}：本表列了，但真实路由文件里没有这条带 RBAC meta 的路由`)
  }
}

if (coverageGaps.length) {
  console.log('⚠️ 覆盖缺口（真实路由有 RBAC meta、但本矩阵未覆盖 —— 下列行的绿/红未验证）：')
  coverageGaps.forEach((g) => console.log('   ⚠️ ' + g))
  console.log('')
}
if (driftErrors.length) {
  console.log('❌ 路由 meta 与 src/router/modules/aps.ts 不一致（假绿风险）—— 矩阵不予输出：')
  driftErrors.forEach((d) => console.log('   ❌ ' + d))
  process.exit(1)
}

/* ===== isRouteAllowed（复刻 src/router/guard.ts:41-64）===== */
function isRouteAllowed(meta, ctx) {
  if (meta.apsRequiredRoles && meta.apsRequiredRoles.length > 0) {
    const hit = meta.apsRequiredRoles.some((r) => ctx.roles.includes(r))
    if (!hit) return { ok: false, reason: `缺角色 ${meta.apsRequiredRoles.join('/')}` }
  }
  if (meta.apsRequiredPermissions && meta.apsRequiredPermissions.length > 0) {
    const hit = meta.apsRequiredPermissions.some((p) => ctx.permissions.includes(p))
    if (!hit) return { ok: false, reason: `缺权限码 ${meta.apsRequiredPermissions.join('/')}` }
  }
  return { ok: true }
}

/* ===== 期望矩阵（绿色 ✅=应通过；红色 ❌=应被 /403 拦截）=====
 * 角色顺序固定 6 列：viewer / planner / admin.aps / admin.system / 车间计划员 / 采购担当
 */
const EXPECTED = {
  // 第一波（规则 / Domain）
  '/aps/rules': {
    // 2026-10-28（3号位 自裁 W-2）：放开 aps.rule.view 只读入口 ⇒ viewer 放行（页内只读）
    'aps.viewer.management': true,
    'aps.planner': false,
    'aps.admin.aps': true,
    'aps.admin.system': true,
    'aps.supervisor.workshop': false,
    'aps.coordinator.material': false
  },
  '/aps/domain': {
    'aps.viewer.management': false,
    'aps.planner': false,
    'aps.admin.aps': false,
    'aps.admin.system': true,
    'aps.supervisor.workshop': false,
    'aps.coordinator.material': false
  },
  // 第二波（PMC 写操作）
  '/aps/ctp': {
    // 场景1/2 = Domain PMC（v0.2 稿 §4.9.1）⇒ 车间计划员/采购担当都不进
    'aps.viewer.management': false,
    'aps.planner': true,
    'aps.admin.aps': false,
    'aps.admin.system': true,
    'aps.supervisor.workshop': false,
    'aps.coordinator.material': false
  },
  '/aps/candidate': {
    // 场景7～9 的候选比较/确认 ⇒ 车间计划员也进得来（持 aps.candidate.confirm）
    'aps.viewer.management': false,
    'aps.planner': true,
    'aps.admin.aps': false,
    'aps.admin.system': true,
    'aps.supervisor.workshop': true,
    'aps.coordinator.material': false
  },
  '/aps/manual-eta': {
    // 采购担当（aps.coordinator.material）持 view+edit+cancel ⇒ 进得来且可写
    'aps.viewer.management': false,
    'aps.planner': true,
    'aps.admin.aps': false,
    'aps.admin.system': true,
    'aps.supervisor.workshop': false,
    'aps.coordinator.material': true
  },
  '/aps/demand-protection': {
    'aps.viewer.management': false,
    'aps.planner': true,
    'aps.admin.aps': false,
    'aps.admin.system': true,
    'aps.supervisor.workshop': false,
    'aps.coordinator.material': false
  },
  // 第三波（只读页 - 持 aps.plan.view 者放行）
  '/aps/overview': {
    'aps.viewer.management': true,
    'aps.planner': true,
    'aps.admin.aps': true,
    'aps.admin.system': true,
    'aps.supervisor.workshop': true,
    'aps.coordinator.material': true
  },
  '/aps/order': {
    'aps.viewer.management': true,
    'aps.planner': true,
    'aps.admin.aps': true,
    'aps.admin.system': true,
    'aps.supervisor.workshop': true,
    'aps.coordinator.material': true
  },
  '/aps/gantt': {
    'aps.viewer.management': true,
    'aps.planner': true,
    'aps.admin.aps': true,
    'aps.admin.system': true,
    'aps.supervisor.workshop': true,
    'aps.coordinator.material': true
  },
  '/aps/explanation': {
    // 2026-11-21（v0.2 稿 §P08 + §9 勾稽）：P08 = Domain PMC + 系统管理员
    //   ⇒ 车间计划员虽持 aps.plan.run（场景7～9 端点码），仍因**角色**门控进不来
    'aps.viewer.management': false,
    'aps.planner': true,
    'aps.admin.aps': false,
    'aps.admin.system': true,
    'aps.supervisor.workshop': false,
    'aps.coordinator.material': false
  },
  '/aps/run': {
    // 2026-11-21：P10「只限系统管理员进入」⇒ 只有 aps.admin.system 进得来
    //   （11-20 起真实路由挂 apsRequiredRoles: ['aps.admin.system']）
    'aps.viewer.management': false,
    'aps.planner': false,
    'aps.admin.aps': false,
    'aps.admin.system': true,
    'aps.supervisor.workshop': false,
    'aps.coordinator.material': false
  },
  '/aps/pi': {
    'aps.viewer.management': true,
    'aps.planner': true,
    'aps.admin.aps': true,
    'aps.admin.system': true,
    'aps.supervisor.workshop': true,
    'aps.coordinator.material': true
  },
  '/aps/pegging-trace': {
    'aps.viewer.management': true,
    'aps.planner': true,
    'aps.admin.aps': true,
    'aps.admin.system': true,
    'aps.supervisor.workshop': true,
    'aps.coordinator.material': true
  },
  '/aps/procurement': {
    // 采购协同：PMC 只读、采购担当可维护人工ETA（页内按 aps.manual_eta.edit 显隐写入口）
    'aps.viewer.management': true,
    'aps.planner': true,
    'aps.admin.aps': true,
    'aps.admin.system': true,
    'aps.supervisor.workshop': true,
    'aps.coordinator.material': true
  },
  '/aps/operation-planning-mode': {
    'aps.viewer.management': true,
    'aps.planner': false,
    'aps.admin.aps': true,
    'aps.admin.system': true,
    'aps.supervisor.workshop': false,
    'aps.coordinator.material': false
  },
  '/aps/resource-calendar': {
    'aps.viewer.management': true,
    'aps.planner': true,
    'aps.admin.aps': true,
    'aps.admin.system': true,
    'aps.supervisor.workshop': false,
    'aps.coordinator.material': false
  },
  '/aps/production-work-calendar': {
    'aps.viewer.management': true,
    'aps.planner': true,
    'aps.admin.aps': true,
    'aps.admin.system': true,
    'aps.supervisor.workshop': false,
    'aps.coordinator.material': false
  },
  // v1.4 §十八.10：策略配置（OR 语义 — VIEWER 持 view / admin.aps 持 view+edit / admin.system 持全部）
  '/aps/strategy-profile': {
    'aps.viewer.management': true,
    'aps.planner': false,
    'aps.admin.aps': true,
    'aps.admin.system': true,
    'aps.supervisor.workshop': false,
    'aps.coordinator.material': false
  },
  // v1.5 Setup 专项：4 角色均持 aps.setup.view（planner/viewer 只读；admin.aps/admin.system 可写可发）
  '/aps/setup/exact': {
    'aps.viewer.management': true,
    'aps.planner': true,
    'aps.admin.aps': true,
    'aps.admin.system': true,
    'aps.supervisor.workshop': false,
    'aps.coordinator.material': false
  },
  '/aps/setup/default': {
    'aps.viewer.management': true,
    'aps.planner': true,
    'aps.admin.aps': true,
    'aps.admin.system': true,
    'aps.supervisor.workshop': false,
    'aps.coordinator.material': false
  },
  '/aps/setup/uncovered': {
    'aps.viewer.management': true,
    'aps.planner': true,
    'aps.admin.aps': true,
    'aps.admin.system': true,
    'aps.supervisor.workshop': false,
    'aps.coordinator.material': false
  },
  '/aps/setup/diff': {
    'aps.viewer.management': true,
    'aps.planner': true,
    'aps.admin.aps': true,
    'aps.admin.system': true,
    'aps.supervisor.workshop': false,
    'aps.coordinator.material': false
  },
  '/aps/parameter-set': {
    'aps.viewer.management': true,
    'aps.planner': false,
    'aps.admin.aps': true,
    'aps.admin.system': true,
    'aps.supervisor.workshop': false,
    'aps.coordinator.material': false
  },
  '/aps/task-split-rule-config': {
    // 批策略：v0.2 稿 §3 把规则/参数治理归「工厂参数维护员」⇒ 车间计划员暂不持 aps.rule.*
    'aps.viewer.management': true,
    'aps.planner': false,
    'aps.admin.aps': true,
    'aps.admin.system': true,
    'aps.supervisor.workshop': false,
    'aps.coordinator.material': false
  },
  // Pkg-8：RBAC 管理 UI（仅 aps.admin.system — 持 aps.auth.user.edit）
  '/aps/rbac-users': {
    'aps.viewer.management': false,
    'aps.planner': false,
    'aps.admin.aps': false,
    'aps.admin.system': true,
    'aps.supervisor.workshop': false,
    'aps.coordinator.material': false
  },
  '/aps/rbac-roles': {
    'aps.viewer.management': false,
    'aps.planner': false,
    'aps.admin.aps': false,
    'aps.admin.system': true,
    'aps.supervisor.workshop': false,
    'aps.coordinator.material': false
  },
  '/aps/rbac-permissions': {
    'aps.viewer.management': false,
    'aps.planner': false,
    'aps.admin.aps': false,
    'aps.admin.system': true,
    'aps.supervisor.workshop': false,
    'aps.coordinator.material': false
  },
  '/aps/rbac-scopes': {
    'aps.viewer.management': false,
    'aps.planner': false,
    'aps.admin.aps': false,
    'aps.admin.system': true,
    'aps.supervisor.workshop': false,
    'aps.coordinator.material': false
  },
  // 审计日志页（§22.6 / U42）：aps.audit.view 4 角色均持（rbac.md §1.2；09-20 mock 预设已对齐补码）
  // 注意与 rbac-* 四页（仅 admin.system）不同——审计是监督能力，管理层 viewer 也可查看
  '/aps/audit': {
    'aps.viewer.management': true,
    'aps.planner': true,
    'aps.admin.aps': true,
    'aps.admin.system': true,
    'aps.supervisor.workshop': true,
    'aps.coordinator.material': false
  }
}

/* ===== 跑矩阵 ===== */
let pass = 0
let fail = 0
const lines = []
lines.push('APS V1 RBAC 矩阵（v1.2 §23.1 + DDL 角色码；✅=放行 / ❌=403）')
lines.push('')

// 表头
const colW = 28
const cellW = 26
const header =
  '路由'.padEnd(colW) +
  ROLES.map((r) => r.padEnd(cellW)).join('')
console.log(header)
console.log('-'.repeat(header.length))

for (const route of ROUTES) {
  // 该路由 RBAC 期望配置
  const expectedSummary =
    (route.meta.apsRequiredRoles ? `角色 ${route.meta.apsRequiredRoles.join('|')}` : '') +
    (route.meta.apsRequiredPermissions
      ? `权限码 ${route.meta.apsRequiredPermissions.join('|')}`
      : '') +
    (!route.meta.apsRequiredRoles && !route.meta.apsRequiredPermissions
      ? '（无 RBAC）'
      : '')

  const cells = []
  for (const role of ROLES) {
    const ctx = { roles: [role], permissions: ROLE_PRESETS[role] }
    const actual = isRouteAllowed(route.meta, ctx).ok
    const expected = EXPECTED[route.path][role]
    const match = actual === expected
    if (match) pass++
    else fail++
    // 显示：actual 状态（✅/❌）+ 一致性标记（✓/✗）
    const actualMark = actual ? '✅ 放行' : '❌ 403'
    const matchMark = match ? '✓' : '✗'
    cells.push(`${actualMark} ${matchMark}`)
  }

  console.log(
    `${route.path.padEnd(colW - 8)}${expectedSummary.padEnd(8)}` +
      cells.map((c) => c.padEnd(cellW)).join('')
  )
}

console.log('')
console.log(
  '权限码来源：src/store/modules/aps/auth.ts MOCK_ROLE_PRESETS（前端 mock 真值）　' +
    ROLES.map((r) => `${r} ${ROLE_PRESETS[r].length} 码`).join('　')
)
console.log(`总组合数：${pass + fail}    ✅ 一致：${pass}    ❌ 不一致：${fail}`)
process.exit(fail > 0 ? 1 : 0)
