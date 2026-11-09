#!/usr/bin/env node
/**
 * 甘特「底部横向滚动条被裁掉」探针反向破坏自证（2026-11-03）
 *
 * 逐条破坏 ⇒ 对应 GT 断言必须转红 ⇒ 还原 ⇒ 全绿。
 *
 * 背景：根节点 `.dhx-gantt-root` 曾被钉死 `min-height: 680px`
 *      ⇒ dhtmlx 容器比可视区高 ⇒ 超出部分被 `.gantt-area` 的 overflow:hidden 裁掉
 *      ⇒ **底部横向滚动条整条不可见**（用户报「甘特图底部没有横向的滚动条了」）。
 *
 * 覆盖：
 *   GT89 甘特根节点高度跟随父容器（height:100% 且无 px 高度地板）
 *   GT90 甘特可视区可收缩（flex:1 + min-height:0）
 */
import { readFileSync, writeFileSync } from 'node:fs'
import { execFileSync } from 'node:child_process'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..')
const GANTT_VIEW = join(ROOT, 'src', 'views', 'Aps', 'Gantt.vue')
const DHX = join(ROOT, 'src', 'components', 'DhxGantt', 'src', 'DhxGantt.vue')

const ORIG = new Map()
for (const f of [GANTT_VIEW, DHX]) {
  ORIG.set(f, readFileSync(f, 'utf-8'))
}

function restore() {
  for (const [f, s] of ORIG) writeFileSync(f, s)
}
function run() {
  try {
    return execFileSync(process.execPath, ['scripts/verify-acceptance.mjs'], {
      cwd: ROOT,
      env: { ...process.env, GROUP: 'gantt' },
      encoding: 'utf-8'
    })
  } catch (e) {
    return (e.stdout || '') + (e.stderr || '')
  }
}
function state(out, id) {
  const line = out.split('\n').find((l) => l.includes(id + ' '))
  if (!line) return 'MISS'
  if (line.includes('✅')) return 'PASS'
  if (line.includes('❌')) return 'FAIL'
  return 'MISS'
}
const IDS = ['GT89', 'GT90']
function states(out) {
  return IDS.map((id) => state(out, id))
}

const CASES = [
  {
    name: 'GT89 破坏：根节点重新钉死高度地板 min-height: 680px（横向滚动条被裁掉的老坑）',
    expect: { GT89: 'FAIL' },
    break: (files) => {
      files.set(DHX, files.get(DHX).replace('  min-height: 0;', '  min-height: 680px;'))
    }
  },
  {
    name: 'GT89 破坏：根节点不再跟随父容器（height: 100% → 固定 680px）',
    expect: { GT89: 'FAIL' },
    break: (files) => {
      // ⚠️ DhxGantt.vue 是 CRLF，锚点里不要写 \n
      files.set(DHX, files.get(DHX).replace('  height: 100%;', '  height: 680px;'))
    }
  },
  {
    name: 'GT90 破坏：可视区不再可收缩（min-height: 0 → 680px）',
    expect: { GT90: 'FAIL' },
    break: (files) => {
      files.set(
        GANTT_VIEW,
        files
          .get(GANTT_VIEW)
          .replace(
            '.gantt-area {\n  position: relative;\n  flex: 1;\n  min-height: 0;',
            '.gantt-area {\n  position: relative;\n  flex: 1;\n  min-height: 680px;'
          )
      )
    }
  },
  {
    name: 'GT90 破坏：可视区不再随视口伸缩（flex: 1 → flex: none）',
    expect: { GT90: 'FAIL' },
    break: (files) => {
      files.set(
        GANTT_VIEW,
        files
          .get(GANTT_VIEW)
          .replace(
            '.gantt-area {\n  position: relative;\n  flex: 1;',
            '.gantt-area {\n  position: relative;\n  flex: none;'
          )
      )
    }
  }
]

let ok = 0
let bad = 0

restore()
{
  const out = run()
  const st = states(out)
  const green = st.every((s) => s === 'PASS')
  console.log((green ? '✅' : '❌') + ` 基线：破坏前 ${IDS.length} 条应全绿 → ${st.join(',')}`)
  green ? ok++ : (bad++, console.log(out))
}

for (const c of CASES) {
  restore()
  const files = new Map(ORIG)
  c.break(files)
  let applied = 0
  for (const [f, s] of files) {
    if (s !== ORIG.get(f)) {
      writeFileSync(f, s)
      applied++
    }
  }
  if (applied === 0) {
    console.log(`❌ ${c.name} —— 破坏未生效`)
    bad++
    continue
  }
  const out = run()
  const want = c.expect
  const pass = Object.entries(want).every(([id, v]) => state(out, id) === v)
  console.log(
    (pass ? '✅' : '❌') +
      ` ${c.name} ⇒ ${Object.entries(want)
        .map(([id, v]) => `${id}=${state(out, id)}（期望 ${v}）`)
        .join(' ')}`
  )
  if (!pass) {
    bad++
    console.log(out.split('\n').filter((l) => l.includes('GT8') || l.includes('GT9')).join('\n'))
  } else ok++
  restore()
}

restore()
{
  const out = run()
  const st = states(out)
  const green = st.every((s) => s === 'PASS')
  console.log((green ? '✅' : '❌') + ` 还原后：应回到全绿 → ${st.join(',')}`)
  green ? ok++ : bad++
}

console.log(`\n自证：通过 ${ok} / 失败 ${bad}`)
process.exit(bad > 0 ? 1 : 0)
