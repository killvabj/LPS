#!/usr/bin/env node
/**
 * 自证脚本：GT69 / GT70 两条探针逐条反向破坏自证
 *
 * 背景：2026-10-31 甘特页功能验收发现的两个前端缺口
 *       ① GT69 维度泳道（阶段/工厂/生产部门）空值兜底必须中文（未分配资源 / 未配置），
 *          且**泳道 id（getTaskGroupKey）与泳道行（lanes）必须同源** ——
 *          两边算法不一致 ⇒ 空维度任务的 parent 匹配不到任何泳道行，任务条直接消失。
 *       ② GT70 白天人工调整 4 个入口的默认域必须是「页面当前看得到的那个域」
 *          （entryDomainKey ?? currentDomainKey），预填对象自身域优先于页面兜底。
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
const BACKUP = join(ROOT, '.selfverify-bak-gantt-lane-domain')

const FILES = [
  'src/views/Aps/Gantt.vue',
  'src/views/Aps/components/GanttAdjustDialog.vue',
  'src/views/Aps/components/ResourceRescheduleDialog.vue'
]

const MUTATIONS = [
  {
    target: 'GT69',
    desc: '阶段泳道标签改回英文兜底（r?.stage ?? "UNKNOWN"）',
    file: 'src/views/Aps/Gantt.vue',
    apply: (s) => s.replace('const stage = dimValueOf(r, r?.stage)', "const stage = r?.stage ?? 'UNKNOWN'")
  },
  {
    target: 'GT69',
    desc: '工厂泳道标签改回英文兜底（r?.factoryName ?? "UNKNOWN"）',
    file: 'src/views/Aps/Gantt.vue',
    apply: (s) =>
      s.replace('const name = dimValueOf(r, r?.factoryName)', "const name = r?.factoryName ?? 'UNKNOWN'")
  },
  {
    target: 'GT69',
    desc: '部门泳道标签改回英文兜底（r?.productionDepartmentName ?? "UNKNOWN"）',
    file: 'src/views/Aps/Gantt.vue',
    apply: (s) =>
      s.replace(
        'const name = dimValueOf(r, r?.productionDepartmentName)',
        "const name = r?.productionDepartmentName ?? 'UNKNOWN'"
      )
  },
  {
    target: 'GT69',
    desc: '泳道 id（getTaskGroupKey）改回英文兜底 ⇒ 与 lanes 的 id 不同源，空维度任务条落不进任何泳道行',
    file: 'src/views/Aps/Gantt.vue',
    apply: (s) =>
      s.replace(
        `    case 'stage': {
      const r = resourceMap.get(task.resourceId ?? -1)
      return \`lane-stage-\${dimValueOf(r, r?.stage)}\`
    }
    case 'factory': {
      const r = resourceMap.get(task.resourceId ?? -1)
      return \`lane-factory-\${dimValueOf(r, r?.factoryName)}\`
    }
    case 'department': {
      const r = resourceMap.get(task.resourceId ?? -1)
      return \`lane-dept-\${dimValueOf(r, r?.productionDepartmentName)}\`
    }`,
        `    case 'stage': {
      const r = resourceMap.get(task.resourceId ?? -1)
      return \`lane-stage-\${r?.stage ?? 'UNKNOWN'}\`
    }
    case 'factory': {
      const r = resourceMap.get(task.resourceId ?? -1)
      return \`lane-factory-\${r?.factoryName ?? 'UNKNOWN'}\`
    }
    case 'department': {
      const r = resourceMap.get(task.resourceId ?? -1)
      return \`lane-dept-\${r?.productionDepartmentName ?? 'UNKNOWN'}\`
    }`
      )
  },
  {
    target: 'GT69',
    desc: '删掉中文兜底常量（改回硬编码英文标签）',
    file: 'src/views/Aps/Gantt.vue',
    apply: (s) =>
      s.replace(
        `const DIM_UNASSIGNED_LABEL = '未分配资源'
const DIM_UNCONFIGURED_LABEL = '未配置'`,
        `const DIM_UNASSIGNED_LABEL = 'UNKNOWN'
const DIM_UNCONFIGURED_LABEL = 'UNKNOWN'`
      )
  },
  {
    target: 'GT70',
    desc: '工具栏入口不再回落到页面当前域（4 个绑定退成 entryDomainKey ⇒ 弹窗退到 domainOptions[0]）',
    file: 'src/views/Aps/Gantt.vue',
    apply: (s) =>
      s.replaceAll(
        ':default-domain-key="entryDomainKey ?? currentDomainKey"',
        ':default-domain-key="entryDomainKey"'
      )
  },
  {
    target: 'GT70',
    desc: 'currentDomainKey 不再取当前版本的域（改成永不命中的比较）⇒ 页面域兜底失效',
    file: 'src/views/Aps/Gantt.vue',
    apply: (s) =>
      s.replace(
        '(v: PlanVersionSummaryDto) => v.id === currentVersionId.value',
        '(v: PlanVersionSummaryDto) => v.id === -1'
      )
  },
  {
    target: 'GT70',
    desc: '甘特图调整弹窗的预填域优先级颠倒（页面兜底压过预填对象自身域）',
    file: 'src/views/Aps/components/GanttAdjustDialog.vue',
    apply: (s) =>
      s.replace(
        'domainKey.value = preTask?.domainKey ?? props.defaultDomainKey ?? props.domainOptions[0]',
        'domainKey.value = props.defaultDomainKey ?? preTask?.domainKey ?? props.domainOptions[0]'
      )
  },
  {
    target: 'GT70',
    desc: '资源重排弹窗的预填域优先级颠倒（页面兜底压过预填对象自身域）',
    file: 'src/views/Aps/components/ResourceRescheduleDialog.vue',
    apply: (s) =>
      s.replace(
        'domainKey.value = preRes?.domainKey ?? props.defaultDomainKey ?? props.domainOptions[0]',
        'domainKey.value = props.defaultDomainKey ?? preRes?.domainKey ?? props.domainOptions[0]'
      )
  },
  {
    target: 'GT71',
    desc: '甘特图调整弹窗表格退回整表渲染（:data="rows"）⇒ 5953 行一次渲染，打开弹窗即卡死页面',
    file: 'src/views/Aps/components/GanttAdjustDialog.vue',
    apply: (s) => s.replace(':data="pagedRows"', ':data="rows"')
  },
  {
    target: 'GT71',
    desc: '甘特图调整弹窗去掉分页器（ElPagination）⇒ 整表渲染回来了',
    file: 'src/views/Aps/components/GanttAdjustDialog.vue',
    apply: (s) =>
      s.replace(
        /<ElPagination[\s\S]*?\/>\n/,
        ''
      )
  },
  {
    target: 'GT71',
    desc: '甘特图调整弹窗去掉跨页保留勾选（reserve-selection）⇒ 翻页后已选丢失',
    file: 'src/views/Aps/components/GanttAdjustDialog.vue',
    apply: (s) => s.replace('<ElTableColumn type="selection" width="44" reserve-selection />', '<ElTableColumn type="selection" width="44" />')
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
          .filter((l) => l.includes('GT69') || l.includes('GT70'))
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
