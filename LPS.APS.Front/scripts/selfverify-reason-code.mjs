#!/usr/bin/env node
/**
 * 自证脚本（一次性，用完即删）：GROUP=reason-code 六条探针逐条反向破坏自证
 *
 * 做法：备份 5 个源文件 → 逐个施加一处破坏 → 跑 GROUP=reason-code →
 *       检查**目标探针**确实转红 → 还原 → 下一条。
 * 判据：每条破坏都必须把对应探针打红，否则该探针就是假绿。
 */
import { execFileSync } from 'node:child_process'
import { readFileSync, writeFileSync, existsSync, mkdirSync, rmSync } from 'node:fs'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

const __dirname = dirname(fileURLToPath(import.meta.url))
const ROOT = join(__dirname, '..')
const BACKUP = join(ROOT, '.selfverify-bak-reason-code')

const FILES = [
  'src/api/aps-v1/types/explanation.ts',
  'src/api/aps-v1/types/schedule.ts',
  'src/views/Aps/Explanation.vue',
  'src/views/Aps/Gantt.vue',
  'src/views/Aps/Ctp.vue',
  'src/views/Aps/Order.vue'
]

/** 每条破坏：target = 期望转红的探针编号；apply = 施加破坏（返回 false 表示不适用） */
const MUTATIONS = [
  {
    target: 'RC01',
    desc: '共享函数删掉「空串 → 未标注原因码」分支（改成返回空串）',
    file: 'src/api/aps-v1/types/explanation.ts',
    apply: (s) =>
      s.replace(
        "if (!raw) return '未标注原因码'",
        "if (!raw) return ''"
      )
  },
  {
    target: 'RC02',
    desc: '共享函数改回 `?? code` 裸返回（空码 → 空白标签）',
    file: 'src/api/aps-v1/types/explanation.ts',
    apply: (s) =>
      s.replace(
        "return (REASON_CODE_LABELS as Record<string, string>)[raw] ?? `待核对·${raw}`",
        "return (REASON_CODE_LABELS as Record<string, string>)[raw] ?? code"
      )
  },
  {
    target: 'RC03',
    desc: '排程解释页改回本地函数名（不再调用共享函数）',
    file: 'src/views/Aps/Explanation.vue',
    apply: (s) => s.replace(/reasonLabelOf\(/g, 'reasonLabel(')
  },
  {
    target: 'RC04',
    desc: '甘特页重新出现裸查表 REASON_CODE_LABELS[...]',
    file: 'src/views/Aps/Gantt.vue',
    apply: (s) =>
      s.replace(
        'import { reasonLabelOf } from',
        "import { REASON_CODE_LABELS, reasonLabelOf } from\nconst __rawLookup = REASON_CODE_LABELS['DUE_DATE_RISK']\nvoid __rawLookup"
      )
  },
  {
    target: 'RC03',
    desc: '订单页改回本地函数名（不再调用共享函数）',
    file: 'src/views/Aps/Order.vue',
    apply: (s) => s.replace(/reasonLabelOf\(/g, 'reasonLabel(')
  },
  {
    target: 'OD19b',
    group: 'order-static',
    desc: '订单页延期原因改回本地裸查表（不再走共享 reasonLabelOf）',
    file: 'src/views/Aps/Order.vue',
    apply: (s) => s.replace('return reasonLabelOf(code)', 'return code')
  },
  {
    target: 'RC05',
    desc: 'ScheduleExplanationFact.reasonCode 改回写死 ReasonCode（空串不可表达）',
    file: 'src/api/aps-v1/types/explanation.ts',
    apply: (s) => s.replace('reasonCode: ReasonCodeValue', 'reasonCode: ReasonCode')
  },
  {
    target: 'RC06',
    desc: '冻结 15 码字典被增删（删一个码 + 删一个标签键）',
    file: 'src/api/aps-v1/types/explanation.ts',
    apply: (s) =>
      s
        .replace("  'MANUAL_COMPLETED_SHORT',\n", '')
        .replace("  MANUAL_COMPLETED_SHORT: '手工完成不足',\n", '')
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

function runProbes(group) {
  const out = execFileSync(process.execPath, [join(ROOT, 'scripts/verify-acceptance.mjs')], {
    cwd: ROOT,
    env: { ...process.env, GROUP: group || 'reason-code' },
    encoding: 'utf-8'
  })
  return out
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
      out = runProbes(m.group)
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
          .filter((l) => l.includes('RC') || l.includes('OD19'))
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

console.log(allOk ? '\n自证完成：8/8 条破坏均把目标探针打红' : '\n自证失败：存在假绿探针')
process.exit(allOk ? 0 : 1)
