#!/usr/bin/env node
/**
 * 自证脚本：GT66 / GT67 / GT68 三条探针逐条反向破坏自证
 *
 * 背景：2026-10-31 落 10-30 甘特审计的两个前端缺口
 *       ① GT66 未分配泳道必须排在所有资源泳道之前（游标键 `ISNULL(ResourceId,-1)` ⇒ 未分配恒在第一页；
 *          排最后 ⇒ 首屏几十条空资源泳道、任务条被顶到屏幕外）
 *       ② GT67 版本下拉必须分组（常用版本=displayedVersions / 其他版本=其余；不得平铺全量、
 *          也不得只留 displayedVersions 把其余版本裁掉）
 *       ③ GT68 任务清单空态必须接 emptyKind（版本级 / 窗口级分说）
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
const BACKUP = join(ROOT, '.selfverify-bak-gantt-lane-version')

const FILES = ['src/views/Aps/Gantt.vue']

const MUTATIONS = [
  {
    target: 'GT66',
    desc: '泳道行不再按任务流首现序排序（人工置顶的旧规则回归 ⇒ 与时间序任务流错位）',
    file: 'src/views/Aps/Gantt.vue',
    apply: (s) =>
      s.replace(
        '    return [...rows, ...laborRows].sort(',
        '    return [...rows, ...laborRows].slice().sort('
      )
  },
  {
    target: 'GT66',
    desc: '给资源泳道加本地排序（只允许按任务首现序排显示行，不得按 ResourceId 另立一套尺）',
    file: 'src/views/Aps/Gantt.vue',
    apply: (s) =>
      s.replace(
        '      .filter((r) => loadedResourceIds.has(r.resourceId))\n      .map((r) => {',
        '      .filter((r) => loadedResourceIds.has(r.resourceId))\n      .sort((a, b) => a.resourceId - b.resourceId)\n      .map((r) => {'
      )
  },
  {
    target: 'GT67',
    desc: '版本下拉退回平铺全量 versions（失败测试版与可用版本混排，分不出）',
    file: 'src/views/Aps/Gantt.vue',
    apply: (s) =>
      s.replace(
        `          <ElOptionGroup v-for="g in versionGroups" :key="g.key" :label="g.label">
            <ElOption
              v-for="v in g.items"
              :key="v.id"
              :label="versionOptionLabel(v)"
              :value="v.id"
              :title="versionOptionTitle(v)"
            />
          </ElOptionGroup>`,
        `          <ElOption
            v-for="v in versions"
            :key="v.id"
            :label="versionOptionLabel(v)"
            :value="v.id"
          />`
      )
  },
  {
    target: 'GT67',
    desc: '「其他版本」组改成复用常用版本（items: pinned）⇒ 其余版本不可达，能力回退',
    file: 'src/views/Aps/Gantt.vue',
    apply: (s) =>
      s.replace(
        `    { key: 'rest', label: '其他版本', items: rest }`,
        `    { key: 'rest', label: '其他版本', items: pinned }`
      )
  },
  {
    target: 'GT67',
    desc: '删掉「其他版本」整组（只剩常用版本 ⇒ 其余版本选不到）',
    file: 'src/views/Aps/Gantt.vue',
    apply: (s) =>
      s.replace(
        `    { key: 'rest', label: '其他版本', items: rest }`,
        ``
      )
  },
  {
    target: 'GT68',
    desc: '任务清单空态退回恒显「该时间范围内没有任务」（版本级/窗口级混说）',
    file: 'src/views/Aps/Gantt.vue',
    apply: (s) =>
      s.replace(
        '<ElEmpty v-if="!taskListTotal" :description="taskListEmptyText" :image-size="60" />',
        '<ElEmpty v-if="!taskListTotal" description="该时间范围内没有任务" :image-size="60" />'
      )
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

console.log(
  allOk ? '\n自证完成：6/6 条破坏均把目标探针打红' : '\n自证失败：存在假绿探针'
)
process.exit(allOk ? 0 : 1)
