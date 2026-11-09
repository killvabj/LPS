#!/usr/bin/env node
/**
 * 自证脚本：GT64 / GT65 两条「反向锁」探针逐条反向破坏自证
 *
 * 背景：2026-10-31 5号位 D1-c 裁决「撤掉 lockMarker 键」⇒ 前端把三态 UI（LOCK_LABEL /
 *       LOCK_TAG_TYPE / 抽屉锁定标识行 / 甘特条 planFreezeFlag·executionLockFlag·isLocked）
 *       整块删除。这两条探针的作用是**防止后人把它加回来**（尤其"拿 BIT 硬塞三态"的错标）。
 *
 * 做法：备份源文件 → 逐个施加一处破坏 → 跑 GROUP=gantt →
 *       检查**目标探针**确实转红 → 还原 → 下一条。
 */
import { execFileSync } from 'node:child_process'
import { readFileSync, writeFileSync, existsSync, mkdirSync, rmSync } from 'node:fs'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

const __dirname = dirname(fileURLToPath(import.meta.url))
const ROOT = join(__dirname, '..')
const BACKUP = join(ROOT, '.selfverify-bak-lockmarker')

const FILES = ['src/views/Aps/Gantt.vue', 'src/api/aps-v1/types/schedule.ts']

const MUTATIONS = [
  {
    target: 'GT64',
    desc: '把三态标签映射表 LOCK_LABEL 加回甘特页（复活已撤的锁定标识 UI）',
    file: 'src/views/Aps/Gantt.vue',
    apply: (s) =>
      s.replace(
        '/* ===== 文档第 2.2 节：四类事实标签 ===== */',
        "const LOCK_LABEL = { EXECUTION: '执行锁定' }\nvoid LOCK_LABEL\n/* ===== 文档第 2.2 节：四类事实标签 ===== */"
      )
  },
  {
    target: 'GT64',
    desc: '把 planFreezeFlag 派生字段加回甘特条行（拿 BIT 硬塞三态）',
    file: 'src/views/Aps/Gantt.vue',
    apply: (s) =>
      s.replace(
        "      laneId: t.resourceId ? String(t.resourceId) : '',",
        "      laneId: t.resourceId ? String(t.resourceId) : '',\n      planFreezeFlag: true,"
      )
  },
  {
    target: 'GT65',
    desc: 'types/schedule.ts 删掉 lockMarker 的「永久缺键」注释（只留类型位不说清口径）',
    file: 'src/api/aps-v1/types/schedule.ts',
    apply: (s) => s.replace('永久缺键', '缺键')
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
    env: { ...process.env, GROUP: 'gantt' },
    encoding: 'utf-8'
  })
}

function probeRed(output, probeId) {
  const line = output.split(/\r?\n/).find((l) => l.includes(probeId))
  return !!line && line.includes('❌')
}

backup()
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
          .filter((l) => l.includes('GT6'))
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

console.log(allOk ? '\n自证完成：3/3 条破坏均把目标探针打红' : '\n自证失败：存在假绿探针')
process.exit(allOk ? 0 : 1)
