#!/usr/bin/env node
/**
 * 自证脚本：M04-1 ~ M04-6 六条探针逐条反向破坏自证
 *
 * 背景：2026-10-31 §10A P4 全站复查挖出的真缺陷 B ——
 *       §10A.1「已有订单提前」弹窗的候选集**不按排程域过滤**：
 *       弹窗表列「当前页全部订单」，候选行又不带 domainKey，
 *       用户可在弹窗里选域 B 却勾了域 A 的订单 ⇒ 载荷 (domainKey=B, orderCanonicalId=域A订单)，
 *       后端 ScopeJsonV2Validator ④ 会 fail-closed 拒绝（报技术错，用户看不懂）。
 *       前端修法：候选行带 domainKey ⇒ 弹窗按域收敛（一次只处理一个域），
 *       默认域优先取「预选订单自身的域」。
 *
 * 做法：备份源文件 → 逐个施加一处破坏 → 跑 GROUP=m-10a1 →
 *       检查**目标探针**确实转红 → 还原 → 下一条。
 */
import { execFileSync } from 'node:child_process'
import { readFileSync, writeFileSync, existsSync, mkdirSync, rmSync } from 'node:fs'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

const __dirname = dirname(fileURLToPath(import.meta.url))
const ROOT = join(__dirname, '..')
const BACKUP = join(ROOT, '.selfverify-bak-order-advance-domain')

// dev 登录限流 5 次/分钟 ⇒ 自证要连跑 8 组探针，每次重新登录必 429。
// 故本脚本自己登录一次拿到 token，后续探针进程全部复用（VERIFY_TOKEN）。
process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0'
const API = process.env.VERIFY_API || 'https://localhost:7044'
async function ensureToken() {
  if (process.env.VERIFY_TOKEN) return
  const r = await fetch(`${API}/api/auth/login`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ userCode: 'admin', password: 'Admin@123456' })
  })
  const j = await r.json().catch(() => null)
  const t = j?.data?.accessToken
  if (!t) throw new Error('登录失败：HTTP ' + r.status + ' ' + JSON.stringify(j).slice(0, 200))
  process.env.VERIFY_TOKEN = t
}

const FILES = ['src/views/Aps/Order.vue', 'src/views/Aps/components/OrderAdvanceDialog.vue']

const MUTATIONS = [
  {
    target: 'M04-1',
    desc: 'Order.vue advanceCandidates 不再透传 domainKey（候选行丢掉排程域）',
    file: 'src/views/Aps/Order.vue',
    apply: (s) => s.replace('    domainKey: o.domainKey,\n', '')
  },
  {
    target: 'M04-2',
    desc: 'OrderAdvanceDialog.vue 候选行接口去掉 domainKey?: string',
    file: 'src/views/Aps/components/OrderAdvanceDialog.vue',
    apply: (s) => s.replace('  domainKey?: string\n', '')
  },
  {
    target: 'M04-3',
    desc: 'buildRows 去掉按域过滤（候选集退回「当前页全部订单」）',
    file: 'src/views/Aps/components/OrderAdvanceDialog.vue',
    apply: (s) =>
      s.replace(
        `  return props.orders
    .filter((o) => o.domainKey === dk)
    .map((o) => ({`,
        `  return props.orders
    .map((o) => ({`
      )
  },
  {
    target: 'M04-4',
    desc: '默认域不再取预选订单自身的域（页面兜底压过预选订单）',
    file: 'src/views/Aps/components/OrderAdvanceDialog.vue',
    apply: (s) =>
      s.replace(
        'toDomainKey(preOrder?.domainKey) ?? props.defaultDomainKey ?? props.domainOptions[0]',
        'props.defaultDomainKey ?? props.domainOptions[0]'
      )
  },
  {
    target: 'M04-5',
    desc: '切域时不再清空勾选（残留上一域的勾选 ⇒ 跨域提交）',
    file: 'src/views/Aps/components/OrderAdvanceDialog.vue',
    apply: (s) =>
      s.replace(
        `  rows.value = buildRows(next)
  selected.value = []
  await nextTick()`,
        `  rows.value = buildRows(next)
  await nextTick()`
      )
  },
  {
    target: 'M04-5',
    desc: '切域时不再重建候选行（表格仍显示上一域的订单）',
    file: 'src/views/Aps/components/OrderAdvanceDialog.vue',
    apply: (s) =>
      s.replace(
        `  rows.value = buildRows(next)
  selected.value = []`,
        `  selected.value = []`
      )
  },
  {
    target: 'M04-5',
    desc: '切域时不再重放预选（预选订单在切域后不再勾上）',
    file: 'src/views/Aps/components/OrderAdvanceDialog.vue',
    apply: (s) =>
      s.replace(
        `  await nextTick()
  applyPreselection()
})

function close(): void {`,
        `  await nextTick()
})

function close(): void {`
      )
  },
  {
    target: 'M04-6',
    desc: '模板提示去掉「仅显示排程域」（用户看不到候选集已按域收敛）',
    file: 'src/views/Aps/components/OrderAdvanceDialog.vue',
    apply: (s) => s.replace('仅显示排程域', '仅显示订单')
  }
]

function backup() {
  rmSync(BACKUP, { recursive: true, force: true })
  mkdirSync(BACKUP, { recursive: true })
  for (const f of FILES) {
    const dst = join(BACKUP, f.replace(/[\\/]/g, '__'))
    writeFileSync(dst, readFileSync(join(ROOT, f), 'utf-8'), 'utf-8')
  }
}

function restore() {
  for (const f of FILES) {
    const src = join(BACKUP, f.replace(/[\\/]/g, '__'))
    if (existsSync(src)) writeFileSync(join(ROOT, f), readFileSync(src, 'utf-8'), 'utf-8')
  }
}

function runProbes() {
  return execFileSync(process.execPath, [join(ROOT, 'scripts/verify-acceptance.mjs')], {
    cwd: ROOT,
    env: { ...process.env, GROUP: 'm-10a1' },
    encoding: 'utf-8'
  })
}

function probeRed(output, probeId) {
  const line = output.split(/\r?\n/).find((l) => l.includes(probeId))
  return !!line && line.includes('❌')
}

backup()
await ensureToken()
let allOk = true
try {
  for (const m of MUTATIONS) {
    const path = join(ROOT, m.file)
    const original = readFileSync(path, 'utf-8')
    const mutated = m.apply(original)
    if (mutated === original) {
      console.log(`❌ ${m.target} 破坏未生效（apply 没匹配到目标文本）— ${m.desc}`)
      allOk = false
      continue
    }
    writeFileSync(path, mutated, 'utf-8')
    let red = false
    let out = ''
    try {
      out = runProbes()
      red = probeRed(out, m.target)
    } catch (e) {
      out = String(e.stdout || '') + String(e.message || '')
      red = probeRed(out, m.target)
    }
    writeFileSync(path, original, 'utf-8')
    if (red) {
      console.log(`✅ ${m.target} 已打红 — ${m.desc}`)
    } else {
      console.log(`❌ ${m.target} 未打红（假绿）— ${m.desc}`)
      console.log(
        out
          .split(/\r?\n/)
          .filter((l) => l.includes('M04'))
          .map((l) => `     ${l.trim()}`)
          .join('\n')
      )
      allOk = false
    }
  }
} finally {
  restore()
  rmSync(BACKUP, { recursive: true, force: true })
}

console.log(
  allOk
    ? `\n自证完成：${MUTATIONS.length}/${MUTATIONS.length} 条破坏均把目标探针打红`
    : '\n自证失败：存在假绿探针'
)
process.exit(allOk ? 0 : 1)
