/**
 * 扫「DTO 有字段但页面没显示」——给《请求5号位核对的字段与接口清单》做机械比对
 *
 * 用法：node scripts/scan-dto-undisplayed.mjs
 * 输出：每个页面 × 它引用的 DTO 接口 × 该接口中「字段名在该 .vue 里一次都没出现」的字段
 *
 * 判定是**保守**的（只报「完全没出现」的）：
 *   - 出现了 ⇒ 可能显示（需人工确认是不是真显示）
 *   - 没出现 ⇒ 一定没显示（页面全文无该标识符）
 */
import { readFileSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const SRC = join(ROOT, 'src')
const TYPES = join(SRC, 'api/aps-v1/types')
const VIEWS = join(SRC, 'views/Aps')

/** 页面 → 关注的 DTO 类型文件 */
const PAGES = {
  'Gantt.vue': ['schedule.ts'],
  'Order.vue': ['order.ts', 'pi.ts'],
  'Pi.vue': ['pi.ts'],
  'PeggingTrace.vue': ['peggingTrace.ts'],
  'Overview.vue': ['overview.ts'],
  'Explanation.vue': ['explanation.ts'],
  'Ctp.vue': ['ctp.ts'],
  'Candidate.vue': ['candidate.ts'],
  'Run.vue': ['run.ts'],
  'SetupExact.vue': ['setup.ts'],
  'SetupDefault.vue': ['setup.ts'],
  'SetupUncovered.vue': ['setup.ts'],
  'ProductionWorkCalendar.vue': ['productionWorkCalendar.ts']
}

/** 从 types/*.ts 里抽出 interface 名 → 字段名[] */
function parseInterfaces(file) {
  const src = readFileSync(join(TYPES, file), 'utf8')
  const out = {}
  const lines = src.split(/\r?\n/)
  let cur = null
  let depth = 0
  for (const line of lines) {
    const m = line.match(/^export\s+(?:interface|type)\s+([A-Za-z0-9_]+)/)
    if (m) {
      cur = m[1]
      out[cur] = []
      depth = 0
      continue
    }
    if (!cur) continue
    depth += (line.match(/{/g) || []).length - (line.match(/}/g) || []).length
    if (depth < 0) {
      cur = null
      continue
    }
    const f = line.match(/^\s{2}([A-Za-z_][A-Za-z0-9_]*)\??\s*:/)
    if (f && out[cur]) out[cur].push(f[1])
    if (/^\s*}/.test(line) && depth === 0) cur = null
  }
  return out
}

/** 该页面引用了哪些类型名（import 段 + 全文出现） */
function usedTypeNames(pageSrc, ifaces) {
  return Object.keys(ifaces).filter((n) => pageSrc.includes(n))
}

function main() {
  for (const [page, typeFiles] of Object.entries(PAGES)) {
    const pageSrc = readFileSync(join(VIEWS, page), 'utf8')
    const ifaces = {}
    for (const tf of typeFiles) Object.assign(ifaces, parseInterfaces(tf))
    const used = usedTypeNames(pageSrc, ifaces)
    console.log('\n================ ' + page + ' ================')
    for (const t of used) {
      const fields = ifaces[t]
      if (!fields || fields.length === 0) continue
      const missing = fields.filter((f) => !new RegExp('\\b' + f + '\\b').test(pageSrc))
      if (missing.length === 0) continue
      console.log(`  ${t}（共 ${fields.length} 字段）未出现在页面：`)
      console.log('    ' + missing.join(', '))
    }
  }
}

main()
