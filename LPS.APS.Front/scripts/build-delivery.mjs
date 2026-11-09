#!/usr/bin/env node
/**
 * 发布出包：构建 pro 产物 → 逐项自查 → 打包 zip → 算 sha256 → 同步 SVN 交付镜像
 *
 * 用途：4号位 在本地给 2号位 出「服务器可直接铺站点根」的前端包（对应 2号位 10-31 来函第三节路线 B）。
 *
 * 为什么要有这个脚本（不是"顺手写的便利工具"）：
 *   ① 最容易出的错是**忘了注入 API 基址** —— `.env.pro` 里没有 `VITE_APS_API_BASE_URL`，
 *      漏注入时产物会静默回落到 `http://localhost:5163`（`src/api/aps-v1/http.ts:30` 的 fallback），
 *      构建**照样成功**，包看起来正常，但 PM 打开就是"打不通"。
 *      本脚本把注入和"注入后必须命中"的校验绑在一起，漏了就在出包前红。
 *   ② 打包 zip 在 git-bash 下没有 `zip` 命令；GNU tar 的 `-a` 只产 gz/xz/bz2 **不产 zip**；
 *      改用 .NET `ZipFile.CreateFromDirectory`（条目名用正斜杠、`index.html` 在根）。
 *   ③ 同步交付镜像要用 robocopy /MIR；在 git-bash 里 `/MIR` 会被 MSYS 路径转换成 `C:/Program Files/Git/MIR`，
 *      必须 `MSYS_NO_PATHCONV=1`；本脚本用 node spawn 传参数数组，**不经过 shell，无此问题**。
 *
 * 用法：
 *   node scripts/build-delivery.mjs                        # 基址取默认 http://10.116.2.75:8081
 *   node scripts/build-delivery.mjs --api http://10.116.2.75:8081
 *   node scripts/build-delivery.mjs --out 我的包.zip
 *   node scripts/build-delivery.mjs --no-mirror             # 不同步 lps/LPS.APS.Front/dist-pro
 *
 * 出包后建议再跑：node scripts/verify-delivery-e2e.mjs     （真包 + 真后端 + 生产同款 Origin 端到端）
 */
import { execFileSync } from 'node:child_process'
import { createHash } from 'node:crypto'
import { existsSync, readFileSync, readdirSync, statSync, copyFileSync, rmSync, utimesSync } from 'node:fs'
import { join, dirname, relative, sep } from 'node:path'
import { fileURLToPath } from 'node:url'
import os from 'node:os'

const __dirname = dirname(fileURLToPath(import.meta.url))
const ROOT = join(__dirname, '..')
const DIST = join(ROOT, 'dist-pro')
const MIRROR = join(ROOT, '..', 'lps', 'LPS.APS.Front', 'dist-pro')

const DEFAULT_API = 'http://10.116.2.75:8081'

/**
 * mock 检查分两档：
 *  - 硬项（MOCK_SERVER_TOKENS）：mock server 的痕迹。出现 ⇒ **mock server 被开过、假 API 数据在包里** ⇒ 阻断出包。
 *  - 软项（MOCK_LIB_TOKENS）：mockjs **库**本身。库在 ≠ 有假数据，但它是被模板演示页引进来的，
 *    会带 rollup 的 "Use of eval" 警告（mockjs 内部用 Function 构造器）。属提示项，不阻断，由人决定要不要摘。
 */
const MOCK_SERVER_TOKENS = ['example:dialog:create', 'viteMockServe']
const MOCK_LIB_TOKENS = ['cparagraph']

function arg(name, fallback) {
  const i = process.argv.indexOf('--' + name)
  return i > -1 && process.argv[i + 1] ? process.argv[i + 1] : fallback
}
const hasFlag = (name) => process.argv.includes('--' + name)

const API = arg('api', DEFAULT_API)
const SYNC_MIRROR = !hasFlag('no-mirror')
const OUT_OVERRIDE = arg('out', null)

function step(msg) {
  console.log('\n=== ' + msg + ' ===')
}
function fail(msg) {
  console.error('  [FAIL] ' + msg)
  process.exitCode = 1
}

/** 递归列出目录下所有文件的绝对路径。 */
function walk(dir, out = []) {
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    const p = join(dir, e.name)
    if (e.isDirectory()) walk(p, out)
    else out.push(p)
  }
  return out
}

/** 统计某字符串在目录内命中的文件数（注意：不能用 `grep | head` 判成败，管道退出码取的是 head 的）。 */
function countHits(dir, token) {
  const needle = Buffer.from(token, 'utf8')
  let n = 0
  for (const f of walk(dir)) {
    if (readFileSync(f).includes(needle)) n++
  }
  return n
}

// ---------------------------------------------------------------- 0. 前置检查
step('0. 前置检查')
const envPro = join(ROOT, '.env.pro')
if (!existsSync(envPro)) {
  console.error('  [FAIL] 找不到 .env.pro，无法确认构建模式')
  process.exit(1)
}
const envProText = readFileSync(envPro, 'utf8')
if (!/^\s*VITE_USE_MOCK\s*=\s*false\s*$/m.test(envProText)) {
  fail('.env.pro 里 VITE_USE_MOCK 不是 false —— 线上包不能带 mock，先改 .env.pro')
} else {
  console.log('  OK    .env.pro: VITE_USE_MOCK=false')
}
if (!existsSync(join(ROOT, 'node_modules'))) {
  fail('node_modules 不存在，先跑 pnpm install')
} else {
  console.log('  OK    node_modules 存在')
}
if (!/^https?:\/\/.+:\d+/.test(API)) {
  fail(`API 基址格式不对：${API}`)
} else {
  console.log('  API 基址 = ' + API)
}
if (process.exitCode) process.exit(1)

// ---------------------------------------------------------------- 1. 构建
step('1. 构建（pro 模式，注入 VITE_APS_API_BASE_URL）')
try {
  // Windows 上 pnpm 是 .cmd，node 不能直接 spawn .cmd（会 EINVAL）⇒ 经 cmd.exe 起。
  // 命令串是固定的，基址只走 env 传参，不进命令行 ⇒ 无注入面。
  execFileSync(process.env.ComSpec || 'cmd.exe', ['/d', '/s', '/c', 'pnpm build:pro'], {
    cwd: ROOT,
    stdio: 'inherit',
    env: { ...process.env, VITE_APS_API_BASE_URL: API }
  })
  console.log('  OK    build:pro 成功')
} catch (e) {
  console.error('  [FAIL] 构建失败：' + (e.message || e))
  process.exit(1)
}

// ---------------------------------------------------------------- 2. 自查
step('2. 产物自查（任一不过就不出包）')
const files = walk(DIST)
console.log(`  产物文件数 = ${files.length}`)

const hitsApi = countHits(DIST, API)
hitsApi > 0 ? console.log(`  OK    基址 ${API} 命中 ${hitsApi} 个文件`) : fail(`产物里找不到基址 ${API}`)

const hitsLocal = countHits(DIST, 'localhost:5163')
hitsLocal === 0 ? console.log('  OK    无 localhost:5163 回落残留') : fail(`产物里有 ${hitsLocal} 个文件含 localhost:5163`)

for (const t of MOCK_SERVER_TOKENS) {
  const n = countHits(DIST, t)
  n === 0 ? console.log(`  OK    无 mock server 痕迹「${t}」`) : fail(`产物里有 ${n} 个文件含 mock server 痕迹「${t}」`)
}
const mockLibFiles = []
for (const t of MOCK_LIB_TOKENS) {
  const n = countHits(DIST, t)
  if (n === 0) console.log(`  OK    无 mock 库特征串「${t}」`)
  else {
    mockLibFiles.push(t)
    console.log(`  [WARN] 产物里有 ${n} 个文件含 mock 库特征串「${t}」`)
  }
}
if (mockLibFiles.length) {
  console.log('         ⇒ 线上包带了 mockjs 库（模板演示页 src/views/Components/Waterfall.vue 直接 import 了它）。')
  console.log('         ⇒ 功能无害：mock server 没开，不会有任何假 API 响应；该演示页菜单里也看不到。')
  console.log('         ⇒ 若要彻底去除：把 Waterfall.vue 的 mockjs 用法改成固定数组即可。')
}

const indexHtml = join(DIST, 'index.html')
if (!existsSync(indexHtml)) {
  fail('产物根目录没有 index.html（zip 解压后必须直接铺站点根）')
} else {
  const html = readFileSync(indexHtml, 'utf8')
  const refs = [...html.matchAll(/(?:src|href)="(\/[^"]+)"/g)].map((m) => m[1])
  const missing = refs.filter((r) => !existsSync(join(DIST, r.slice(1))))
  missing.length === 0
    ? console.log(`  OK    index.html 引用的 ${refs.length} 个本地资源全部存在`)
    : fail(`index.html 引用了产物里没有的资源：${missing.join(', ')}`)
  const cdn = [...html.matchAll(/(?:src|href)="(https?:\/\/[^"]+)"/g)].map((m) => m[1])
  cdn.length === 0 ? console.log('  OK    index.html 无外部 CDN 依赖') : fail(`index.html 依赖外部 CDN：${cdn.join(', ')}`)
}

if (process.exitCode) {
  console.error('\n自查未通过，**不出包**（避免把基址错误/mock 的产物交给 2号位）')
  process.exit(1)
}

// ---------------------------------------------------------------- 3. 打包
step('3. 打包 zip（index.html 在根）')

/**
 * 内容指纹：**只由文件路径 + 文件内容**决定，不含时间戳。
 * 为什么还要它：zip 记录了文件修改时间 ⇒ 同一份源码重新构建，zip 的 sha256 必变，
 * 内容却可能完全一样。跟 2号位 对账时要比的是「内容指纹」，zip 的 sha256 只对「手上那一个 zip」有效。
 */
function contentFingerprint(dir) {
  const lines = walk(dir)
    .map((f) => {
      const rel = relative(dir, f).split(sep).join('/')
      const h = createHash('sha256').update(readFileSync(f)).digest('hex')
      return rel + '\0' + h
    })
    .sort()
  return createHash('sha256').update(lines.join('\n')).digest('hex')
}

/**
 * 包名内嵌**内容指纹前 8 位** ⇒ 同名即同内容，杜绝「同名不同内容」的张冠李戴。
 * 指纹不含时间戳 ⇒ 同一份源码重建后包名不变（会覆盖上一版同名包，目录里不会堆一串同日期包）。
 */
const fingerprint = contentFingerprint(DIST)
const now = new Date()
const date =
  String(now.getFullYear()) +
  String(now.getMonth() + 1).padStart(2, '0') +
  String(now.getDate()).padStart(2, '0')
const OUT = OUT_OVERRIDE || `APS-V1-Front-dist-pro-${date}-${fingerprint.slice(0, 8)}.zip`
const ZIP_PATH = join(ROOT, OUT)
console.log(`  包名    ${OUT}（含内容指纹前 8 位）`)

/**
 * 把产物里所有文件/目录的修改时间**统一钉成一个固定时刻**。
 * 为什么：zip 会把文件修改时间写进条目 ⇒ 同一份源码重建，zip 字节必变、sha256 必变，
 * 而内容一模一样。钉死时间后 zip **字节级可复现** ⇒ 同一份源码重建出来的是同一个包、
 * 同一个 sha256 ⇒ 交付时只需给一个哈希，不必再解释「zip 的 sha 会变、对账要用内容指纹」。
 */
const FIXED_TIME = new Date('2020-01-01T00:00:00Z')
function pinTimestamps(dir) {
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    const p = join(dir, e.name)
    if (e.isDirectory()) {
      pinTimestamps(p)
      utimesSync(p, FIXED_TIME, FIXED_TIME)
    } else {
      utimesSync(p, FIXED_TIME, FIXED_TIME)
    }
  }
}
pinTimestamps(DIST)

try {
  const ps = [
    'Add-Type -AssemblyName System.IO.Compression.FileSystem;',
    `$src='${DIST}';`,
    `$dst='${ZIP_PATH}';`,
    'if(Test-Path $dst){Remove-Item $dst -Force};',
    '[System.IO.Compression.ZipFile]::CreateFromDirectory($src,$dst)'
  ].join(' ')
  execFileSync('powershell.exe', ['-NoProfile', '-Command', ps], { stdio: 'inherit' })
  console.log('  OK    ' + OUT)
} catch (e) {
  console.error('  [FAIL] 打包失败：' + e.message)
  process.exit(1)
}

const zipBytes = statSync(ZIP_PATH).size
const sha = createHash('sha256').update(readFileSync(ZIP_PATH)).digest('hex')

// ---------------------------------------------------------------- 3b. 包完整性校验
step('3b. 校验 zip 与产物逐文件一致（解压后比对 sha256）')
{
  const TMP = join(os.tmpdir(), 'aps-delivery-check-' + Date.now())
  try {
    execFileSync(
      'powershell.exe',
      ['-NoProfile', '-Command', `Expand-Archive -LiteralPath '${ZIP_PATH}' -DestinationPath '${TMP}' -Force`],
      { stdio: 'inherit' }
    )
    const hashTree = (dir) =>
      walk(dir)
        .map((f) => {
          const rel = relative(dir, f).split(sep).join('/')
          return rel + '\0' + createHash('sha256').update(readFileSync(f)).digest('hex')
        })
        .sort()
    const a = hashTree(DIST)
    const b = hashTree(TMP)
    const diff = a.length !== b.length || a.some((x, i) => x !== b[i])
    if (diff) {
      console.error(`  [FAIL] zip 解压结果与产物不一致（产物 ${a.length} 项 / 解压 ${b.length} 项）`)
      process.exit(1)
    }
    console.log(`  OK    zip 解压后 ${a.length} 个文件与产物逐个 sha256 一致`)
  } catch (e) {
    console.error('  [FAIL] zip 完整性校验失败：' + (e.message || e))
    process.exit(1)
  } finally {
    rmSync(TMP, { recursive: true, force: true })
  }
}

// ---------------------------------------------------------------- 4. 同步交付镜像
if (SYNC_MIRROR) {
  step('4. 同步 SVN 交付镜像 lps/LPS.APS.Front/dist-pro')
  try {
    execFileSync(
      'robocopy',
      [DIST, MIRROR, '/MIR', '/NFL', '/NDL', '/NJH', '/NJS', '/R:1', '/W:1'],
      { stdio: 'ignore' }
    )
    console.log('  OK    已同步（robocopy /MIR，清掉了旧哈希文件）')
  } catch (e) {
    // robocopy 退出码 0-7 均为成功（1=有文件复制、2=有额外文件、3=两者），>=8 才是失败
    const rc = e.status
    if (typeof rc === 'number' && rc < 8) console.log('  OK    已同步（robocopy /MIR）')
    else {
      console.error('  [FAIL] 同步失败（robocopy 退出码 ' + rc + '）')
      process.exit(1)
    }
  }

  /**
   * robocopy 之后的**逐文件校验 + 修补**（2026-11-03 实踩后加）。
   *
   * 为什么必须加：robocopy 默认按「**大小 + 修改时间**」判同，跳过两者都一样的文件。
   * 本脚本 `pinTimestamps()` 把所有产物文件的 mtime 钉成同一个固定时刻（为了 zip 字节可复现），
   * 于是「大小相同 + 时间相同」就可能**内容已变却判为同文件** ⇒ robocopy 跳过不拷。
   * 典型受害者是 `index.html`：它只含一个入口 js 哈希名，新旧哈希名**长度相同** ⇒ 文件大小一模一样，
   * 时间戳又被钉死 ⇒ robocopy 认定"没变" ⇒ **镜像里留着上一版的 index.html**，
   * 而上一版的入口 js 已被 /MIR 删掉 ⇒ 直接铺镜像的 dist-pro ⇒ 白屏。
   * （2026-11-03 实际就发生了：镜像 index.html 仍指 index-CKWJ32zF.js，该文件已被删。）
   *
   * 做法：不依赖 robocopy 的判同，直接按 **sha256** 逐文件比，缺/不一致就重拷，最后再验一遍。
   */
  const shaOf = (f) => createHash('sha256').update(readFileSync(f)).digest('hex')
  const repaired = []
  for (const f of walk(DIST)) {
    const rel = relative(DIST, f)
    const dst = join(MIRROR, rel)
    if (!existsSync(dst) || shaOf(dst) !== shaOf(f)) {
      copyFileSync(f, dst)
      utimesSync(dst, FIXED_TIME, FIXED_TIME)
      repaired.push(rel.split(sep).join('/'))
    }
  }
  if (repaired.length) {
    console.log(`  OK    修补 ${repaired.length} 个 robocopy 判同但内容已变的文件：${repaired.join(', ')}`)
  } else {
    console.log('  OK    镜像与产物逐文件 sha256 一致（无需修补）')
  }
  // 复验：修补后必须**逐字节**一致，且不得有镜像里多出来的文件
  const mirrorFiles = walk(MIRROR)
  const distSet = new Set(walk(DIST).map((f) => relative(DIST, f)))
  const extra = mirrorFiles.filter((f) => !distSet.has(relative(MIRROR, f))).map((f) => relative(MIRROR, f))
  const mismatch = mirrorFiles.filter((f) => distSet.has(relative(MIRROR, f)) && shaOf(f) !== shaOf(join(DIST, relative(MIRROR, f))))
  if (mismatch.length || extra.length) {
    console.error('  [FAIL] 镜像与产物不一致：不一致 ' + mismatch.length + ' 个、多出 ' + extra.length + ' 个')
    if (mismatch.length) console.error('         不一致：' + mismatch.slice(0, 5).join(', '))
    if (extra.length) console.error('         多出：' + extra.slice(0, 5).join(', '))
    process.exit(1)
  }
  console.log(`  OK    复验通过：镜像 ${mirrorFiles.length} 个文件与产物逐个 sha256 一致`)
  // 入口一致性：镜像 index.html 必须指向镜像里**真实存在**的入口 js
  const entry = (readFileSync(join(MIRROR, 'index.html'), 'utf8').match(/assets\/index-[A-Za-z0-9_-]+\.js/) || [])[0]
  if (!entry || !existsSync(join(MIRROR, entry))) {
    console.error('  [FAIL] 镜像 index.html 指向的入口 js 不存在：' + (entry || '(未解析到)'))
    process.exit(1)
  }
  console.log('  OK    镜像入口自洽：' + entry)

  // zip 也放进交付镜像，2号位 从仓库/镜像里就能取到同一个包并核 sha256
  const mirrorZip = join(MIRROR, '..', OUT)
  try {
    copyFileSync(ZIP_PATH, mirrorZip)
    console.log('  OK    包已复制到交付镜像 ' + OUT)
  } catch (e) {
    console.error('  [WARN] 包复制到镜像失败（不影响出包）：' + (e.message || e))
  }
} else {
  step('4. 跳过镜像同步（--no-mirror）')
}

// ---------------------------------------------------------------- 5. 交付摘要
step('5. 交付摘要（可直接抄给 2号位）')
console.log(`  包名        ${OUT}`)
console.log(`  体积        ${zipBytes} 字节（约 ${(zipBytes / 1048576).toFixed(2)} MB）`)
console.log(`  条目数      ${files.length}`)
console.log(`  zip sha256  ${sha}`)
console.log(`  内容指纹    ${fingerprint}   ← 与 2号位 对账用这个（不含时间戳，重建后仍可比）`)
console.log(`  构建模式    pro（pnpm build:pro）`)
console.log(`  API 基址    ${API}（构建时环境变量注入，未改库内 .env.pro）`)
console.log(`  mock        mock server 未启用（产物无 mock 桩数据）${mockLibFiles.length ? '；注意：产物仍含 mockjs 库，见步骤 2 的 [WARN]' : '，产物亦无 mockjs 库'}`)
console.log(`  部署方式    解压到站点根（如 F:\\APS\\Front），index.html 直接在根，不套子目录`)
console.log(`  路由        hash 模式 ⇒ IIS 无需 URL Rewrite`)
console.log('')
console.log('  部署后建议再跑一次端到端校验：node scripts/verify-delivery-e2e.mjs')
