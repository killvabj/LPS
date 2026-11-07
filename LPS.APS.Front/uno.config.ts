import { defineConfig, toEscapedSelector as e, presetUno } from 'unocss'
import transformerVariantGroup from '@unocss/transformer-variant-group'
import { loadEnv } from 'vite'
import { createRequire } from 'node:module'
import { readFileSync } from 'node:fs'
import path from 'node:path'
import { ICON_PREFIX } from './src/constants'

const root = process.cwd()

/* ------------------------------------------------------------------
 * 离线图标规则（生产走这条：.env.pro 里 VITE_USE_ONLINE_ICON=false）
 *
 * 为什么不用 presetIcons：
 *   @unocss/preset-icons@0.65.4 的 createNodeLoader() 用
 *   import('@iconify/utils/lib/loader/node-loader')（无扩展名）加载本地图标集，
 *   而 @iconify/utils@2.2.x 的 exports 是 {"./*": "./*"}（不做扩展名补全），
 *   ⇒ 这个 import 必然 ERR_MODULE_NOT_FOUND；ESM 里 require 兜底也不存在
 *   ⇒ node loader 永远拿不到，presetIcons 静默地一个图标都产不出（warn 默认关）
 *   ⇒ 生产包 CSS 里 0 条图标规则 ⇒ 线上所有图标是空白方块。
 *
 * 这里自己实现：直接读已安装的 @iconify/json 里的集合 JSON，
 * 按 Iconify 的 alias / 翻转 / 旋转语义渲染成 mask CSS（与 presetIcons 产物同构）。
 * 数据来源与在线模式同一个 Iconify 官方集合，图形不会走样。
 * ------------------------------------------------------------------ */

interface IconItem {
  body?: string
  width?: number
  height?: number
  left?: number
  top?: number
  rotate?: number
  hFlip?: boolean
  vFlip?: boolean
}

interface IconCollection {
  width: number
  height: number
  icons: Record<string, IconItem>
  aliases?: Record<string, IconItem & { parent: string }>
}

const requireFromRoot = createRequire(path.resolve(root, 'package.json'))
const collectionCache = new Map<string, IconCollection | null>()

function loadCollection(name: string): IconCollection | null {
  if (collectionCache.has(name)) return collectionCache.get(name) ?? null
  let collection: IconCollection | null = null
  try {
    const jsonPath = requireFromRoot.resolve(`@iconify/json/json/${name}.json`)
    collection = JSON.parse(readFileSync(jsonPath, 'utf8')) as IconCollection
  } catch {
    collection = null
  }
  collectionCache.set(name, collection)
  return collection
}

/** Iconify 的 mergeIconData：变换取 XOR / 叠加，其余属性近者优先 */
function mergeIconProps(parent: IconItem, child: IconItem): IconItem {
  const result: IconItem = {}
  if (!parent.hFlip !== !child.hFlip) result.hFlip = true
  if (!parent.vFlip !== !child.vFlip) result.vFlip = true
  const rotate = ((parent.rotate || 0) + (child.rotate || 0)) % 4
  if (rotate) result.rotate = rotate
  const keys: (keyof IconItem)[] = ['body', 'width', 'height', 'left', 'top']
  keys.forEach((key) => {
    if (child[key] !== undefined) result[key] = child[key]
    else if (parent[key] !== undefined) result[key] = parent[key]
  })
  return result
}

/** 取图标：沿 aliases 链向上找到本体，再从本体往回逐层合并（近者优先） */
function resolveIcon(collection: IconCollection, name: string): IconItem | undefined {
  const chain: IconItem[] = []
  const seen = new Set<string>()
  let current: string | undefined = name
  while (current && !seen.has(current)) {
    seen.add(current)
    const direct = collection.icons[current]
    if (direct) {
      chain.push(direct)
      break
    }
    const alias = collection.aliases?.[current]
    if (!alias) return undefined
    chain.push(alias)
    current = alias.parent
  }
  if (!chain.length) return undefined
  let acc: IconItem = {}
  chain.forEach((item) => {
    acc = mergeIconProps(item, acc)
  })
  return mergeIconProps({ width: collection.width, height: collection.height }, acc)
}

/** 渲染成 SVG 字符串（对齐 Iconify 的 iconToSVG） */
function renderIconSvg(item: IconItem): string {
  const box = {
    left: item.left ?? 0,
    top: item.top ?? 0,
    width: item.width ?? 16,
    height: item.height ?? 16
  }
  let body = item.body ?? ''
  const transformations: string[] = []
  let rotation = item.rotate ?? 0
  if (item.hFlip) {
    if (item.vFlip) {
      rotation += 2
    } else {
      transformations.push(`translate(${box.width + box.left} ${0 - box.top})`)
      transformations.push('scale(-1 1)')
      box.top = box.left = 0
    }
  } else if (item.vFlip) {
    transformations.push(`translate(${0 - box.left} ${box.height + box.top})`)
    transformations.push('scale(1 -1)')
    box.top = box.left = 0
  }
  if (rotation < 0) rotation -= Math.floor(rotation / 4) * 4
  rotation = rotation % 4
  if (rotation === 1) {
    const pivot = box.height / 2 + box.top
    transformations.unshift(`rotate(90 ${pivot} ${pivot})`)
  } else if (rotation === 2) {
    transformations.unshift(`rotate(180 ${box.width / 2 + box.left} ${box.height / 2 + box.top})`)
  } else if (rotation === 3) {
    const pivot = box.width / 2 + box.left
    transformations.unshift(`rotate(-90 ${pivot} ${pivot})`)
  }
  if (rotation % 2 === 1) {
    if (box.left !== box.top) {
      const swap = box.left
      box.left = box.top
      box.top = swap
    }
    if (box.width !== box.height) {
      const swap = box.width
      box.width = box.height
      box.height = swap
    }
  }
  if (transformations.length) {
    body = `<g transform="${transformations.join(' ')}">${body}</g>`
  }
  const viewBox = `${box.left} ${box.top} ${box.width} ${box.height}`
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="${viewBox}" width="${box.width}" height="${box.height}">${body}</svg>`
}

/** data URI 编码（顺序要紧：% 必须最先替换） */
function encodeSvgForCss(svg: string): string {
  return svg
    .replace(/%/g, '%25')
    .replace(/#/g, '%23')
    .replace(/</g, '%3C')
    .replace(/>/g, '%3E')
    .replace(/"/g, "'")
    .replace(/\s+/g, ' ')
    .trim()
}

/** 是否生成离线图标 CSS：在线图标模式（dev/.env.test）不生成 */
function isOfflineIconMode(): boolean {
  const argv = process.argv
  const flagIndex = argv.findIndex((item) => item === '--mode' || item.startsWith('--mode='))
  const mode = flagIndex >= 0 ? argv[flagIndex].split('=')[1] || argv[flagIndex + 1] : ''
  const env = mode ? loadEnv(mode, root) : ({} as Record<string, string>)
  return env.VITE_USE_ONLINE_ICON !== 'true'
}

const createOfflineIconRule = () => {
  if (!isOfflineIconMode()) return []
  return [
    [
      new RegExp(`^${ICON_PREFIX}([a-z0-9-]+):([a-zA-Z0-9_-]+)$`),
      async (matcher: RegExpExecArray) => {
        const collection = loadCollection(matcher[1])
        if (!collection) return
        const item = resolveIcon(collection, matcher[2])
        if (!item) return
        const url = `url("data:image/svg+xml;utf8,${encodeSvgForCss(renderIconSvg(item))}")`
        return {
          '--un-icon': url,
          '-webkit-mask': 'var(--un-icon) no-repeat',
          mask: 'var(--un-icon) no-repeat',
          '-webkit-mask-size': '100% 100%',
          'mask-size': '100% 100%',
          'background-color': 'currentColor',
          color: 'inherit'
        }
      }
    ]
  ]
}

export default defineConfig({
  // ...UnoCSS options
  rules: [
    [
      /^overflow-ellipsis$/,
      ([], { rawSelector }) => {
        const selector = e(rawSelector)
        return `
${selector} {
  text-overflow: ellipsis;
}
`
      }
    ],
    [
      /^custom-hover$/,
      ([], { rawSelector }) => {
        const selector = e(rawSelector)
        return `
${selector} {
  display: flex;
  height: 100%;
  padding: 1px 10px 0;
  cursor: pointer;
  align-items: center;
  transition: background var(--transition-time-02);
}
/* you can have multiple rules */
${selector}:hover {
  background-color: var(--top-header-hover-color);
}
.dark ${selector}:hover {
  background-color: var(--el-bg-color-overlay);
}
`
      }
    ],
    [
      /^layout-border__left$/,
      ([], { rawSelector }) => {
        const selector = e(rawSelector)
        return `
${selector}:before {
  content: "";
  position: absolute;
  top: 0;
  left: 0;
  width: 1px;
  height: 100%;
  background-color: var(--el-border-color);
  z-index: 3;
}
`
      }
    ],
    [
      /^layout-border__right$/,
      ([], { rawSelector }) => {
        const selector = e(rawSelector)
        return `
${selector}:after {
  content: "";
  position: absolute;
  top: 0;
  right: 0;
  width: 1px;
  height: 100%;
  background-color: var(--el-border-color);
  z-index: 3;
}
`
      }
    ],
    [
      /^layout-border__top$/,
      ([], { rawSelector }) => {
        const selector = e(rawSelector)
        return `
${selector}:before {
  content: "";
  position: absolute;
  top: 0;
  left: 0;
  width: 100%;
  height: 1px;
  background-color: var(--el-border-color);
  z-index: 3;
}
`
      }
    ],
    [
      /^layout-border__bottom$/,
      ([], { rawSelector }) => {
        const selector = e(rawSelector)
        return `
${selector}:after {
  content: "";
  position: absolute;
  bottom: 0;
  left: 0;
  width: 100%;
  height: 1px;
  background-color: var(--el-border-color);
  z-index: 3;
}
`
      }
    ],
    ...createOfflineIconRule()
  ],
  presets: [presetUno({ dark: 'class', attributify: false })],
  transformers: [transformerVariantGroup()],
  content: {
    pipeline: {
      include: [/\.(vue|svelte|[jt]sx|mdx?|astro|elm|php|phtml|html|ts)($|\?)/],
      // IconPicker 演示组件的图标清单（ant-design 830 + tdesign 1203 条）不入图标 CSS：
      // 它们只是模板演示页的数据表，全量内联会让产物 CSS 凭空多 ~1.8MB。
      // 该演示页在菜单里已隐藏，不影响 APS 业务页。
      exclude: [/[\\/]components[\\/]IconPicker[\\/]src[\\/]data[\\/]/]
    }
  }
})
