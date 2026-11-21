/**
 * APS V1 4号位 — 参数集（ParameterSet）维护 Pinia store
 *
 * ⚠️ 2026-10-22 新增：参数集从「规则与参数维护」页里独立出来。
 *   冻结模型里参数集是**独立主表**（DDL 08:9064 / 08:9081），与规则集平行，
 *   只在 StrategyProfileVersion 上会合 —— 原先放在规则集页里、并与规则集 1:1 绑定的模型不成立。
 *
 * 内容模型：
 *  - 一个参数集版本的内容 = 5 个主题 JSON（lock / supply / procurement / solverStrategy /
 *    candidateGuardrail），后端以**字符串**返回，前端反序列化成对象编辑、PUT 时序列化回去
 *  - 缓冲三态：originalBlocks（后端真值）/ workingBlocks（编辑态）/ dirty（逐块标记）
 *
 * 权限：aps.parameter.view / aps.parameter.edit / aps.parameter.publish
 *  （⚠️ 参数集发布是**独立权限**，`aps.rule.publish` 不覆盖它 —— 后端 Controller:228 挂 ParameterPublish）
 *
 * 2026-11-18：跨厂LT 标准参数改走**通用参数集版本接口**（第 6 号内容子块 `crossFactoryLeadTimeRulesJson`）；
 *   专属读写端点已删除（调用会 404）⇒ 读通用版本详情、写通用版本 PUT，写完**回读核对**。
 *   —— 依据 3号位《跨厂LT 走通用参数接口与快照保留_回执 v1.1_20261117》。
 *
 * 2026-11-18（第二封）：3号位《跨厂LT早退吞块修复与停用整块口径_回执 v1.0_20261118》
 *   —— 回读核对**保留**（3号位 §三 R3：跨层正确性的最终防线）；**前端零行为改动**：
 *   - 「吞块」的根因已由后端查明并修复（`EnsureParameterSetNormalized` 在「五主题皆空」时早退，
 *     早退先于本块归一化 ⇒ 提交值无处落、读回是旧值/空）；3号位已把早退条件并入本块判空。
 *   - 「本版不用本块」的唯一表达 = 全部行 `IsActive=false`（裁甲）⇒ 零行**不能发**，
 *     停用请关掉那一行的「生效」（提示文案已如此，见 `crossFactoryLtError`）。
 *
 * 2026-11-19：3号位 r14280 合批**已部署到 dev**（跨厂LT 子块 + 个人中心三端点），
 *   联调实测（4号位 × 3号位 V1~V8）：该块 PUT 带回读一致 ⇒ **不再是「读不到/写不进」**。
 *   ⇒ 写后回读不通过时的提示**不再断言「子块尚未部署」**（那已是过时的原因），
 *     改成中性说法：读回来还是原内容 ⇒ 没存上，让用户重试或找管理员。
 *   —— 依据 3号位《两封回执认账收讫_合批已提交SVN_r14280_请按V1至V8与V1至V10联调回告_回执 v1.0_20261119》。
 *
 * 2026-11-19：3号位《两清单联调全通核证归档_口径补正认账_V6保持现行契约_归档回执 v1.0_20261119》
 *   —— **前端零行为改动**；两条已裁定口径钉进注释（防后人误改）：
 *   - **V6 拦截阶段＝发布期／装载期，PUT 期透传不拦**（该函 §二：「保持现行契约、不采 PUT 期拦截」）：
 *     后端 PUT `[]` 会 200 写入并原样回读；`CROSS_FACTORY_LT_BLOCK_EMPTY` 只在**发布/校验期**与
 *     **装载期** fail-closed ⇒ **不要**把文案或逻辑改成「保存时后端会拒 `[]`」——
 *     保存期后端是**透传**的，错误出现在**校验/发布期**。
 *     ⚠️ 2026-11-19（用户指示）本 store 的「零行不发」源头纪律**已作废**，见下条。
 *   - **V3b 判据改写**（该函 §一 1.1 认账）：版本 GET 不回快照顶层键
 *     ⇒「回读得到该块」＝「快照顶层已有该键」；写后回读核对仍是唯一可观测判据。
 *
 * 2026-11-19（用户指示，本轮）：跨厂LT 的拦截**从「保存草稿」挪到「校验草稿 / 发布」** ——
 *   - 「保存草稿」**不再**因跨厂LT 被拦（行没填全、一行都没有，先都存下来）；
 *   - 发送口径：本块**被改过**才带这个键（不带＝保留既有块）；带就以编辑态为准 ——
 *     **零行也照发 `[]`**（后端 PUT 期透传 ⇒ 存下来了，服务器状态＝编辑态）；
 *   - 拦截落在后端**校验/发布**期：`CROSS_FACTORY_LT_BLOCK_EMPTY`（键存在但零行）/
 *     `INVALID_CROSS_FACTORY_LT`（逐条校验不过）⇒ 前端只做中文翻译（validationMessages.ts）；
 *   - 另加一道**发布前**零行守卫（说人话，避免用户点到发布才吃技术错误）；
 *   - 校验期若本地还有未保存改动，补一条提示「这次校验的是已保存的内容」。
 *   ⚠️ 连带后果（已在编辑器里写进提示）：**这一块的键一旦存在就删不掉** ——
 *   后端 `TryAddBlock` 空白即跳过 + 兜底保留既有快照块 ⇒ 零行存下去后，
 *   只能加回一行（把「生效」关掉）才能恢复成可发布状态。
 *
 * 2026-11-19（用户指示，第二封）：**保存草稿不再弹「变更原因」框，点一下直接存**。
 *   依据：后端保存草稿的 UPDATE 语句**没有 `Remarks` 列**（`ParameterSetVersionRepository.UpdateAsync`），
 *   审计 `Remark` 写死「参数集版本更新」⇒ 用户填的变更原因**后端既不收也不存** ⇒ 弹框撤除。
 *   ⚠️ **发布**的原因仍然要填：publish 端点把 `changeReason` 写进审计 `Remark`。
 *
 * 2026-11-19（第三封）：3号位《跨厂LT零行保存期不拦认可_键删不掉裁甲维持现状_回执_v1.0_20261119》
 *   —— **前端零行为改动**（该函 §一 三块全部认可：保存期不拦照发 `[]`、发布前零行守卫 +
 *     `validateDraft()` 的「校验的是已保存内容」提示 + 两个错误码汉化、`crossFactoryLtKeyPresent`
 *     区分缺块 / 键存在零行）；本轮只把两条**已裁定**口径钉进注释（防后人误改）：
 *   - **裁甲（维持现状）**：「键删不掉」是 11-18 R2 裁甲的**既定契约形态**、**非实现缺口**
 *     （该函 §2.1 实读 `GovernanceVersionService.cs:1868-1900`：`TryAddBlock` 空白即跳过 +
 *     兜底保留既有块 ⇒ 永不删除）⇒ 前端**不提供删键表达**（专属端点 / 传 `null` /
 *     通用接口显式清空标记一律不做，沿续 R2 已否决方向；该函 §二.3 **乙不采**）。
 *   - **前端体验定稿**：行删光时的**非阻断**提示「删掉最后一行没法撤回（这一块的键删不掉），
 *     只能加回一行并把『生效』关掉」＝ 采纳 4号位 甲案，**即定稿**（该函 §2.2 第 3 条）。
 *     ⚠️ 提示归提示，**保存期仍不拦**（拦截在「校验 / 发布」这一步）。
 *   ⚠️ 若再遇用户「想删掉这一块」诉求：按「加回一行＋关生效」口径引导，**不另建表达**（该函 §四）。
 */

import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import {
  parameterSetApi,
  ApiError,
  type ParameterSetListRowDto,
  type ParameterSetDetailDto,
  type VersionDiffResultDto,
  type RuleStatus,
  type RoleKey,
  type ParameterSetVersion,
  type PublishValidationResultDto,
  type BlockKey,
  type ParameterSetBlocks,
  type BackendParameterSetVersion,
  BLOCK_KEYS,
  NO_VERSION
} from '@/api/aps-v1'
import {
  flattenBlocks,
  applyEdit,
  removePath,
  buildPutBody,
  parseParameterSetBlocks,
  type ParameterRow,
  type JsonValue
} from '@/api/aps-v1/draftBuffer'
import { warehouseDictionaryApi, type WarehouseDictItem } from '@/api/aps-v1/warehouseDictionary'
import { lookupParameterEnum } from '@/api/aps-v1/parameterSetEnums'
import {
  diffParameterBlocks,
  pickDiffPair,
  type ParameterDiffRow
} from '@/api/aps-v1/parameterDiff'
import {
  readAnchorResourceCode,
  writeAnchorResourceCode,
  ensureAnchorResourceCodeKey,
  pruneAnchorOnlyBlock,
  isAnchorResourceCodeRow
} from '@/api/aps-v1/anchorResource'
import {
  SUPPLY_WAREHOUSE_PRIORITY_PATH,
  PROCUREMENT_WAREHOUSE_PRIORITY_PATH,
  DEFAULT_PURCHASE_LT_PATH,
  ARRIVAL_TO_USABLE_OFFSETS_PATH,
  isWarehouseRefRow,
  readStringArray,
  writeStringArray,
  readObjectArray,
  writeObjectArray,
  collectWarehouseRefs,
  unknownWarehouseCodes,
  type PurchaseLtItem,
  type ArrivalOffsetItem,
  type WarehouseRefGroups
} from '@/api/aps-v1/warehouseRefs'
import {
  readPlanningYields,
  writePlanningYields,
  isPlanningYieldRow,
  type PlanningYieldItem
} from '@/api/aps-v1/parameterSetYields'
import {
  parseCrossFactoryLeadTimeRules,
  serializeCrossFactoryLeadTimeRules,
  type CrossFactoryLeadTimeItem
} from '@/api/aps-v1/crossFactoryLeadTime'
import { mockActor } from '@/api/aps-v1/rule'
import { normalizeRuleStatus } from '@/api/aps-v1/types/common'
import { anchorHintMessage as buildAnchorHintMessage } from '@/api/aps-v1/validationMessages'
import { useApsAuthStore } from './auth'

/** 脏标记：5 块各自独立标记 */
type DirtyFlags = Record<BlockKey, boolean>

/** 列表排序方式（与规则集页同口径，2026-11-11 筛选条件） */
export type ParameterListSort = 'DEFAULT' | 'PUBLISHED_DESC' | 'CREATED_DESC' | 'CODE_ASC'

/** 排序选项文案（页面下拉与本表共用一份，避免两处漂移） */
export const PARAMETER_LIST_SORT_LABELS: Record<ParameterListSort, string> = {
  DEFAULT: '默认顺序',
  PUBLISHED_DESC: '最近发布',
  CREATED_DESC: '最近创建',
  CODE_ASC: '编码升序'
}

const emptyDirtyFlags = (): DirtyFlags => ({
  lock: false,
  supply: false,
  procurement: false,
  solverStrategy: false,
  candidateGuardrail: false
})

const emptyBufferState = () => ({
  governance: null as ParameterSetVersion | null,
  originalBlocks: null as ParameterSetBlocks | null,
  workingBlocks: null as ParameterSetBlocks | null,
  dirty: emptyDirtyFlags(),
  saving: false
})

export const useParameterSetsStore = defineStore('aps.parameterSets', () => {
  // ===== state =====
  /**
   * 参数集列表行（**一版一行**，2026-11-11 列表源切换）
   *
   * ⚠️ 一行 = 一个版本（`versionId` / `versionCode` / `versionStatus` 齐备），
   *   另有「无版本主表」行（hasVersion=false）。
   *   同族多个版本（已发布 + 草稿）各占一行、状态各自真实，不再互相覆盖。
   */
  const parameterSets = ref<ParameterSetListRowDto[]>([])
  const currentDetail = ref<ParameterSetDetailDto | null>(null)
  const currentDiff = ref<VersionDiffResultDto | null>(null)
  /**
   * 参数级差异（前端逐参数比对两个版本的内容）
   *
   * 后端 diff 的粒度是**块级**（5 个主题块各塞一整块 JSON），用户看不出「改了哪个参数」；
   * 这里用同一套扁平化算法逐参数比，页面「版本对比」Tab 的主表用它。
   */
  const paramDiffRows = ref<ParameterDiffRow[]>([])
  /** 本次对比的两个版本（源 → 目标） */
  const paramDiffPair = ref<{
    sourceCode: string
    targetCode: string
    sourceStatus: RuleStatus
    targetStatus: RuleStatus
  } | null>(null)
  const lastValidation = ref<PublishValidationResultDto | null>(null)
  const statusFilter = ref<RuleStatus | typeof NO_VERSION | 'ALL'>('ALL')
  /**
   * 列表筛选条件（2026-11-11 用户拍板，与规则集页同口径）
   *
   * 原「显示已停用」开关已撤：停用行**默认就显示**（「全部」页签含停用），
   * 顶部 KPI 横条也撤了（各状态计数改为直接落在页签上）。
   */
  const listKeyword = ref('')
  /** 主表启停筛选（与版本状态正交：主表停用 ≠ 版本 DISABLED） */
  const listActiveFilter = ref<'ALL' | 'ACTIVE' | 'INACTIVE'>('ALL')
  /** 列表排序 */
  const listSort = ref<ParameterListSort>('DEFAULT')
  const loading = ref(false)
  const listLoading = ref(false)
  const actionRunning = ref(false)
  const validating = ref(false)
  const error = ref<string | null>(null)

  /**
   * 仓库字典（主数据）
   *
   * 来源：`GET /api/governance-query/warehouses`（类级 `aps.plan.view`）
   * —— 3号位 2026-10-25 §三 定案=方案 A（端点不动，给参数集维护账号补绑该码）。
   * ⇒ 拉不到（403/网络）时**如实提示、不静默吞成空字典**，页面降级为「只能看已有编码」。
   */
  const warehouseDict = ref<WarehouseDictItem[]>([])
  const warehouseDictLoaded = ref(false)
  const warehouseDictError = ref<string | null>(null)

  /** 5 块缓冲（编辑态） */
  const buffer = ref(emptyBufferState())

  /* ===== 跨厂LT 标准参数（参数集版本的第 6 号内容子块）===== */
  /**
   * 跨厂LT 规则行（编辑态）
   *
   * ⚠️ 2026-11-18 改口径（3号位《跨厂LT 走通用参数接口与快照保留_回执 v1.1_20261117》）：
   *   这一块在快照**顶层**、不在 5 主题 JSON 里，但后端已把它并进**通用归一化**
   *   ⇒ 读写**都走通用参数集版本接口**（`GET/PUT …/version/{id}` 的
   *   `crossFactoryLeadTimeRulesJson` 字段），专属端点已删除（调用会 404）。
   *   它仍有自己的编辑态与 dirty 标记（不占 5 主题的 dirty 位）。
   */
  const crossFactoryLtRows = ref<CrossFactoryLeadTimeItem[]>([])
  /** 上次已保存的快照指纹（判断这块有没有未保存改动） */
  const crossFactoryLtSavedJson = ref('[]')
  /**
   * 快照顶层**有没有**这个键（`$.CrossFactoryLeadTimeRules`）
   *
   * ⚠️ 2026-11-19：区分两种「0 条」——
   *   - 缺块（键不存在）＝ 这一版没配跨厂LT ＝ 合法，后端校验不阻断；
   *   - 键存在但零行（`[]`）＝ 配置形态非法，后端校验/发布期报 `CROSS_FACTORY_LT_BLOCK_EMPTY`。
   *   两者在前端都显示「0 条」，只有本标记能区分 ⇒ 发布前的零行拦截靠它。
   */
  const crossFactoryLtKeyPresent = ref(false)
  const crossFactoryLtLoading = ref(false)
  const crossFactoryLtError = ref<string | null>(null)

  /**
   * 跨厂LT 那一块有没有未保存的改动
   *
   * ⚠️ 2026-11-19：原先这个标记只喂给「离开页面」的拦截（不喂页面的 dirty），
   *   因为那时块内还有独立的「保存这块」按钮；该按钮撤掉后（跨厂LT 只随「保存草稿」提交），
   *   本标记**并入**页面的 `dirty` ⇒ 改了这一块，「保存草稿」就能点、也标「未保存」。
   *
   * ⚠️ 两边都取**序列化后**的形态再比：`parse` 出的行键序（From/To/族/工时/生效）
   *   与 `serialize` 出的键序（From/To/工时/生效/族）**不一样**，
   *   直接比 `JSON.stringify(rows)` 会让「刚存完也显示未保存」。
   */
  const crossFactoryLtDirty = computed(
    () =>
      JSON.stringify(serializeCrossFactoryLeadTimeRules(crossFactoryLtRows.value)) !==
      crossFactoryLtSavedJson.value
  )

  const actor = ref(mockActor.name)
  const apsAuth = useApsAuthStore()
  const actorRoles = computed<RoleKey[]>(() =>
    apsAuth.roles.length > 0 ? apsAuth.roles : mockActor.roles
  )

  // ===== getters =====
  /**
   * 列表基础集：**关键词 + 主表状态**过滤后（页签过滤之前）
   *
   *  - 关键词：参数集编码 / 名称 / 版本码 模糊匹配（不区分大小写）。
   *  - 主表状态：主表停用（isActive=false）的行**默认也显示**
   *    （2026-11-11 用户拍板：「全部」页签即显示全部，含停用 ⇒ 原「显示已停用」开关已撤）。
   */
  const scopedParameterSets = computed(() => {
    let arr = parameterSets.value
    const kw = listKeyword.value.trim().toLowerCase()
    if (kw) {
      arr = arr.filter(
        (r) =>
          r.parameterSetCode.toLowerCase().includes(kw) ||
          r.parameterSetName.toLowerCase().includes(kw) ||
          (r.versionCode ?? '').toLowerCase().includes(kw)
      )
    }
    if (listActiveFilter.value === 'ACTIVE') {
      arr = arr.filter((r) => r.isActive !== false)
    } else if (listActiveFilter.value === 'INACTIVE') {
      arr = arr.filter((r) => r.isActive === false)
    }
    return arr
  })

  /**
   * 过滤后的参数集列表行（页签状态过滤 + 排序）
   *
   *  - 状态过滤按**版本**真实状态；无版本的行只在「全部」「未配版本」两个页签出现
   *    （点「草稿」不该捞出几百个根本没版本的行）。
   */
  const filteredParameterSets = computed(() => {
    let arr = scopedParameterSets.value
    if (statusFilter.value === NO_VERSION) {
      arr = arr.filter((r) => !r.hasVersion)
    } else if (statusFilter.value !== 'ALL') {
      arr = arr.filter((r) => r.hasVersion && r.versionStatus === statusFilter.value)
    }
    return sortParameterSetRows(arr)
  })

  /**
   * 页签计数（2026-11-11：顶部 KPI 横条撤除后，计数直接落在页签上）
   *
   *  - ALL = 当前筛选下会显示的**行数**（含停用主表的行）
   *  - DRAFT / PUBLISHED / DISABLED = 该**版本状态**的行数
   *  - NO_VERSION = 没有版本的行数（「未配版本」页签）
   * 计数随关键词 / 主表状态筛选实时变化。
   */
  const tabCounts = computed(() => {
    const c = { ALL: 0, DRAFT: 0, PUBLISHED: 0, DISABLED: 0, NO_VERSION: 0 }
    scopedParameterSets.value.forEach((r) => {
      c.ALL += 1
      if (!r.hasVersion) {
        c.NO_VERSION += 1
        return
      }
      if (r.versionStatus === 'DRAFT') c.DRAFT += 1
      else if (r.versionStatus === 'PUBLISHED') c.PUBLISHED += 1
      else if (r.versionStatus === 'DISABLED') c.DISABLED += 1
    })
    return c
  })

  /** 参数集（主表）总数 —— 一版一行后列表行数 ≠ 参数集数（「全部」页签的悬停说明用） */
  const parameterSetTotal = computed(
    () => new Set(parameterSets.value.map((r) => r.parameterSetId)).size
  )

  const currentStatus = computed(() => currentDetail.value?.summary.status ?? null)

  /** 是否有写权限 —— aps.parameter.edit */
  const canWrite = computed(() => apsAuth.has('aps.parameter.edit'))

  /** 是否有发布权限 —— aps.parameter.publish（独立权限码，不随规则发布权获得） */
  const canPublishAction = computed(() => apsAuth.has('aps.parameter.publish'))

  const actions = computed(() => {
    const s = currentStatus.value
    const edit = apsAuth.has('aps.parameter.edit')
    const pub = apsAuth.has('aps.parameter.publish')
    // 「停用」的对象是**已发布版本**，不是参数集整体状态：
    // 后端在有草稿时把 status 报成 DRAFT，若拿 s 当判据，一旦 fork 出草稿
    // 就再也停不掉已发布版本（页面无删草稿入口 ⇒ 死路）。
    const hasPublished = (currentDetail.value?.history ?? []).some((h) => h.status === 'PUBLISHED')
    return {
      canCreateDraft: edit,
      /** 编辑/保存：仅 DRAFT */
      canEdit: s === 'DRAFT' && edit,
      canValidate: s === 'DRAFT' && pub,
      canPublish: s === 'DRAFT' && pub,
      canDisable: hasPublished && pub
    }
  })

  /**
   * 当前缓冲是否脏
   *
   * ⚠️ 2026-11-19：跨厂LT 那一块（第 6 号内容子块）**并入**本标记 ——
   *   该块原有的块内「保存这块」入口已撤（跨厂LT 只随「保存草稿」一起提交），
   *   若不并入，用户改了这一块却看不到「未保存」、「保存草稿」按钮仍是灰的 ⇒ 改动无处可存。
   */
  const dirty = computed(
    () => BLOCK_KEYS.some((b) => buffer.value.dirty[b]) || crossFactoryLtDirty.value
  )

  /** 扁平化参数行（表格渲染用） */
  const flattenedRows = computed<ParameterRow[]>(() => {
    const wb = buffer.value.workingBlocks
    if (!wb) return []
    return flattenBlocks(wb)
  })

  /**
   * 按块分组的扁平行
   *
   * ⚠️ 四处「仓库引用」落点被**剔除**出通用表：它们不是标量，有专属编辑器
   *   （有序码列表 / 对象数组），通用行的单值控件表达不了。
   *   剔除清单与后端校验采集段一一对应（`GovernanceVersionService.cs:1019-1034`）。
   * ⚠️ 「锚点资源编码」也剔除：它有专属输入框（中文标签 + 说明），
   *   留在通用表里会多出一行裸英文键名，同一份数据显示两处。
   * ⚠️ 「采购良率」（Procurement.PlanningYields）也剔除：它是对象数组，
   *   通用单值控件表达不了，有专属编辑器（物料 / 工序 / 良率）。
   */
  const rowsGrouped = computed<Record<BlockKey, ParameterRow[]>>(() => {
    const grouped: Record<BlockKey, ParameterRow[]> = {
      lock: [],
      supply: [],
      procurement: [],
      solverStrategy: [],
      candidateGuardrail: []
    }
    flattenedRows.value.forEach((row) => {
      // 维护责任分类 C/E/F/H ⇒ 维护面隐藏（值仍在快照里，保存原样回写）
      if (row.hidden) return
      if (isWarehouseRefRow(row.block, row.path)) return
      if (isPlanningYieldRow(row.block, row.path)) return
      if (isAnchorResourceCodeRow(row.block, row.path)) return
      grouped[row.block].push(row)
    })
    return grouped
  })

  /** 仓库字典里的编码集合（大小写不敏感，与后端 `OrdinalIgnoreCase` 对齐） */
  const warehouseDictCodes = computed<string[]>(() =>
    warehouseDict.value.map((w) => String(w.code ?? '').trim()).filter(Boolean)
  )

  /** 仓库编码 → 名称（回显用；字典里没有的编码查不到 ⇒ 显示编码本身） */
  const warehouseNameMap = computed<Map<string, string>>(() => {
    const m = new Map<string, string>()
    for (const w of warehouseDict.value) {
      const code = String(w.code ?? '').trim()
      if (code) m.set(code.toLowerCase(), String(w.name ?? '').trim())
    }
    return m
  })

  /** ① 供应参数 → 仓库优先级（有序码列表） */
  const supplyWarehousePriority = computed<string[]>(() =>
    readStringArray(buffer.value.workingBlocks, 'supply', SUPPLY_WAREHOUSE_PRIORITY_PATH)
  )
  /** ② 采购参数 → 仓库优先级（有序码列表） */
  const procurementWarehousePriority = computed<string[]>(() =>
    readStringArray(buffer.value.workingBlocks, 'procurement', PROCUREMENT_WAREHOUSE_PRIORITY_PATH)
  )
  /** ③ 采购参数 → 默认采购提前期 */
  const defaultPurchaseLt = computed<PurchaseLtItem[]>(() =>
    readObjectArray<PurchaseLtItem>(
      buffer.value.workingBlocks,
      'procurement',
      DEFAULT_PURCHASE_LT_PATH
    )
  )
  /** ④ 采购参数 → 到货可用偏移 */
  const arrivalToUsableOffsets = computed<ArrivalOffsetItem[]>(() =>
    readObjectArray<ArrivalOffsetItem>(
      buffer.value.workingBlocks,
      'procurement',
      ARRIVAL_TO_USABLE_OFFSETS_PATH
    )
  )
  /** ⑤ 采购参数 → 计划良率（专属编辑器用） */
  const planningYields = computed<PlanningYieldItem[]>(() =>
    readPlanningYields(buffer.value.workingBlocks)
  )
  /** ⑥ 跨厂LT 标准参数（顶层独立块 `$.CrossFactoryLeadTimeRules`；专属编辑器用） */
  const crossFactoryLtRules = computed<CrossFactoryLeadTimeItem[]>(() => crossFactoryLtRows.value)

  /**
   * 非阻断提示：选了非自动锚点方式、却没填锚点资源编码 ⇒ 运行时按「自动判定」处理。
   * 判定与文案都在 `validationMessages.ts` 的纯函数里（可单测），此处只取编辑态缓冲喂给它。
   */
  const anchorHintMessage = computed<string | null>(() =>
    buildAnchorHintMessage(buffer.value.workingBlocks?.solverStrategy)
  )

  /** 锚点资源编码当前值（自由文本；3号位 10-25 定调方案 (a)） */
  const anchorResourceCode = computed<string>(() =>
    readAnchorResourceCode(buffer.value.workingBlocks?.solverStrategy)
  )

  /**
   * 「求解器策略」块是否已有内容。
   *
   * ⚠️ 空块不呈现锚点录入项：空块意味着「求解器策略还没配」，此时填进去的锚点编码
   *   会被后端反序列化成默认 Mode/BottleneckMode（枚举 0 号）⇒ 锚点根本不生效，
   *   而块却从「发不出去」变成「发得出去」⇒ 前端替用户改了发布语义。
   */
  const solverStrategyHasContent = computed<boolean>(() => {
    const ss = buffer.value.workingBlocks?.solverStrategy
    return !!ss && Object.keys(ss).length > 0
  })

  // ===== actions =====
  async function loadList(): Promise<void> {
    listLoading.value = true
    error.value = null
    try {
      // 新端点（一版一行）+ 旧端点的「无版本主表」行，合并见 parameterSetApi.listParameterSetRows
      parameterSets.value = await parameterSetApi.listParameterSetRows()
    } catch (err) {
      handleError(err)
    } finally {
      listLoading.value = false
    }
  }

  /**
   * 载入参数集详情
   *
   * @param parameterSetId 参数集（主表）Id
   * @param versionId 要展示的版本 Id（列表点哪一行就传哪一版；不传 = 草稿优先）
   */
  async function loadDetail(parameterSetId: number, versionId?: number): Promise<void> {
    loading.value = true
    error.value = null
    currentDiff.value = null
    lastValidation.value = null
    buffer.value = emptyBufferState()
    try {
      const detail = await parameterSetApi.getParameterSet(parameterSetId, versionId)
      // 参数集的代码/名称只在主表列表里（详情端点只给版本内容）⇒ 用列表行回填
      const row =
        parameterSets.value.find(
          (r) =>
            r.parameterSetId === parameterSetId && (versionId == null || r.versionId === versionId)
        ) ?? parameterSets.value.find((r) => r.parameterSetId === parameterSetId)
      detail.summary.parameterSetCode = row?.parameterSetCode ?? detail.summary.parameterSetCode
      detail.summary.parameterSetName = row?.parameterSetName ?? detail.summary.parameterSetName
      detail.summary.description = row?.description
      detail.summary.lastChangeReason = row?.lastChangeReason
      // 主表启用位只存在于列表行（详情聚合按版本拼装，默认 true）
      detail.summary.isActive = row?.isActive ?? detail.summary.isActive
      currentDetail.value = detail

      // 列表行同步刷新（该版本的状态可能已被别处改动）
      if (row) {
        const idx = parameterSets.value.findIndex((r) => r.rowKey === row.rowKey)
        if (idx >= 0) {
          parameterSets.value[idx] = {
            ...parameterSets.value[idx],
            versionStatus: detail.summary.status,
            currentVersionCode: detail.summary.currentVersionCode,
            draftVersionCode: detail.summary.draftVersionCode,
            publishedAt: detail.summary.publishedAt
          }
        }
      }

      // 载入 5 块缓冲（编辑对象 = 后端真值的深拷贝）
      if (detail.version) {
        const blocks = parseParameterSetBlocks(detail.version)
        // 锚点资源编码补键（3号位 10-25 定调：后端零改动，落点在前端写侧带键）
        //  放在深拷贝**之前** ⇒ originalBlocks / workingBlocks 同时带键 ⇒「取消未保存」后
        //  录入项仍在，不会出现「一取消这行就消失」。
        ensureAnchorResourceCodeKey(blocks)
        buffer.value = {
          governance: detail.version,
          originalBlocks: JSON.parse(JSON.stringify(blocks)),
          workingBlocks: blocks,
          dirty: emptyDirtyFlags(),
          saving: false
        }
      }

      // 版本对比：对比对由 `pickDiffPair` 统一选取
      //  ① 有草稿 ⇒ 当前已发布 ↔ 草稿（回答「我这次改了什么」）
      //  ② 无草稿 ⇒ 上一个已发布 ↔ 当前已发布（回答「上一版到这一版改了什么」）
      await refreshDiff()
    } catch (err) {
      handleError(err)
      currentDetail.value = null
    } finally {
      loading.value = false
    }
  }

  /**
   * 新建参数集主表，并跟建一条空白草稿（`<编码>-V1`）。
   *
   * 两步口径沿用 10-22 规则集裁决 Q2（先建壳、前端随后自动跟建空白 DRAFT）；
   * 空白草稿合法：`EnsureParameterSetNormalized` 在五主题全空时直接返回，
   * 发布前校验会报 EMPTY 直到用户填内容，与规则集新壳一致。
   *
   * 写接口报错不等于没写成：失败时回列表核对同编码 —— 防「其实已建成、只是响应报错」把用户卡在
   * 「列表看不到 + 重试又说编码已存在」的死路（规则集 10-25 已踩过）。
   */
  async function createParameterSet(input: {
    parameterSetCode: string
    parameterSetName: string
    description?: string
  }): Promise<number | null> {
    if (!canWrite.value) {
      error.value = '当前操作者无写权限（需权限码 aps.parameter.edit）'
      return null
    }
    const code = input.parameterSetCode.trim()
    const name = input.parameterSetName.trim()
    if (!code || !name) {
      error.value = '参数集编码和名称都必须填写'
      return null
    }
    if (
      parameterSets.value.some(
        (p) => p.parameterSetCode?.trim().toLowerCase() === code.toLowerCase()
      )
    ) {
      error.value = `参数集编码已存在：${code}`
      return null
    }
    actionRunning.value = true
    error.value = null
    try {
      const created = await parameterSetApi.createParameterSet({
        parameterSetCode: code,
        parameterSetName: name,
        description: input.description?.trim() || undefined
      })
      return await attachBlankDraft(created.id, created.parameterSetCode || code)
    } catch (err) {
      await loadList()
      const hit = parameterSets.value.find(
        (p) => p.parameterSetCode?.trim().toLowerCase() === code.toLowerCase()
      )
      if (hit) {
        console.warn('[参数集] 新建接口报错，但回列表发现同编码参数集已存在，按已建成继续', err)
        return await attachBlankDraft(hit.parameterSetId, hit.parameterSetCode)
      }
      handleError(err)
      const httpStatus = err instanceof ApiError ? err.code : undefined
      error.value =
        `新建没成功${httpStatus ? `（服务器返回 ${httpStatus}）` : ''}。` +
        '也可能是「其实已经建好了，只是返回时出错」——请刷新一下列表，看这个编码在不在。'
      return null
    } finally {
      actionRunning.value = false
    }
  }

  /**
   * 更新参数集主表（PUT /parameter-set/{id}）：名称 / 说明 / 启停。
   *
   * 编码原样回传（后端拒绝变更）；停用只关主表，不级联已有版本状态。
   * 写接口报错不等于没写成：失败时回列表核对真值，已是新值就按成功继续。
   */
  async function updateParameterSet(input: {
    parameterSetId: number
    parameterSetCode: string
    parameterSetName: string
    description?: string
    isActive: boolean
  }): Promise<boolean> {
    if (!canWrite.value) {
      error.value = '当前操作者无写权限（需权限码 aps.parameter.edit）'
      return false
    }
    const name = input.parameterSetName.trim()
    if (!name) {
      error.value = '参数集名称必须填写'
      return false
    }
    actionRunning.value = true
    error.value = null
    try {
      await parameterSetApi.updateParameterSet({
        id: input.parameterSetId,
        parameterSetCode: input.parameterSetCode,
        parameterSetName: name,
        description: input.description?.trim() || undefined,
        isActive: input.isActive
      })
      await loadList()
      if (currentDetail.value?.summary.parameterSetId === input.parameterSetId) {
        // 仍停在这一版（改的是主表字段，版本内容没变）
        await loadDetail(input.parameterSetId, currentDetail.value?.version?.id)
      }
      return true
    } catch (err) {
      await loadList()
      const row = parameterSets.value.find((p) => p.parameterSetId === input.parameterSetId)
      const saved =
        row != null && row.parameterSetName === name && (row.isActive !== false) === input.isActive
      if (saved) {
        console.warn('[参数集] 保存接口报错，但回列表核对已是新值，按已保存继续', err)
        return true
      }
      handleError(err)
      const httpStatus = err instanceof ApiError ? err.code : undefined
      error.value =
        `保存没成功${httpStatus ? `（服务器返回 ${httpStatus}）` : ''}。` +
        '请刷新列表核对名称/启用状态是否已生效。'
      return false
    } finally {
      actionRunning.value = false
    }
  }

  /** 主表建成后补一条空白草稿；失败只如实提示「壳建好了、版本没建上」，不吞成功 */
  async function attachBlankDraft(parameterSetId: number, code: string): Promise<number> {
    let draftId: number | undefined
    try {
      const created = await parameterSetApi.createParameterSetDraft({
        parameterSetId,
        versionCode: `${code}-V1`,
        remarks: '新建参数集时自动创建的空白草稿'
      })
      draftId = created?.id
    } catch (err) {
      const versions = await parameterSetApi
        .listParameterSetVersions(parameterSetId)
        .catch((): BackendParameterSetVersion[] => [])
      const hasDraft = versions.some((v) => normalizeRuleStatus(v.status) === 'DRAFT')
      await loadList()
      if (!hasDraft) {
        handleError(err)
        error.value = `参数集已创建，但空白草稿版本创建失败：${error.value}`
      }
      return parameterSetId
    }
    await loadList()
    // 落点切到刚建出来的那条空白草稿（一版一行后，列表里它就是新的一行）
    await loadDetail(parameterSetId, draftId)
    return parameterSetId
  }

  async function loadDiff(base: number, target: number): Promise<void> {
    try {
      currentDiff.value = await parameterSetApi.diffVersions(base, target)
    } catch (err) {
      handleError(err)
      currentDiff.value = null
    }
  }

  /**
   * 刷新版本对比（两个来源并行取，谁先成算谁的）
   *
   *  - 后端 diff ⇒ **版本元信息**（版本码 / 状态 / 生效窗 / 发布时间 / 发布人 / 备注）
   *  - 前端 diff ⇒ **参数内容**（逐参数；后端只有块级 JSON，看不出改了哪个参数）
   *
   * 两边互不依赖：后端 diff 挂了就只少一张元信息表，参数差异照常显示。
   */
  async function refreshDiff(): Promise<void> {
    paramDiffRows.value = []
    paramDiffPair.value = null
    currentDiff.value = null
    const history = currentDetail.value?.history ?? []
    const pair = pickDiffPair(history)
    if (!pair) return
    const sourceId = pair.source.versionId
    const targetId = pair.target.versionId
    if (!sourceId || !targetId || sourceId === targetId) return

    // 元信息对比（失败不阻断参数级对比）
    try {
      currentDiff.value = await parameterSetApi.diffVersions(sourceId, targetId)
    } catch (err) {
      console.warn('[参数集] 版本元信息对比失败（不影响参数级差异）', err)
    }

    // 参数内容对比（逐参数）
    try {
      const [src, tgt] = await Promise.all([
        parameterSetApi.getParameterSetVersion(sourceId),
        parameterSetApi.getParameterSetVersion(targetId)
      ])
      paramDiffRows.value = diffParameterBlocks(
        parseParameterSetBlocks(src),
        parseParameterSetBlocks(tgt)
      )
      paramDiffPair.value = {
        sourceCode: pair.source.versionCode,
        targetCode: pair.target.versionCode,
        sourceStatus: pair.source.status,
        targetStatus: pair.target.status
      }
    } catch (err) {
      handleError(err)
    }
  }

  /** 编辑一个参数值（只改内存，不落库） */
  function onCellEdit(row: ParameterRow, newValue: JsonValue): void {
    if (!buffer.value.workingBlocks || !buffer.value.governance) {
      error.value = '参数集草稿未载入'
      return
    }
    try {
      applyEdit(buffer.value.workingBlocks, row, newValue)
      buffer.value.dirty[row.block] = true
    } catch (err) {
      handleError(err)
    }
  }

  /**
   * 空块加第一个字段（3号位 2026-10-25 §C-AN-1/§C-AN-3 拍板）
   *
   *  - 只写用户**显式选中**的那一个路径；不预填默认枚举 / 空结构 / 约定键名
   *    ⇒ 空块在用户完成首个真实输入前保持空，仍被后端 `EMPTY_*` 拦（发布语义不被前端改写）
   *  - 走**既有**的版本草稿 PUT（`PUT /api/governance/parameter-set/version/{id}`），
   *    本动作只改编辑态缓冲，**不调任何接口** ⇒ 零新端点
   *  - 枚举字段只接受后端枚举序号（`applyEdit` 会校验），写字符串会被后端判 INVALID_JSON
   */
  function addBlockField(block: BlockKey, path: string[], value: JsonValue): boolean {
    if (!buffer.value.workingBlocks || !buffer.value.governance) {
      error.value = '参数集草稿未载入'
      return false
    }
    if (!canWrite.value) {
      error.value = '当前操作者无写权限（需权限码 aps.parameter.edit）'
      return false
    }
    try {
      applyEdit(
        buffer.value.workingBlocks,
        {
          block,
          path,
          value: null,
          type: 'STRING',
          options: lookupParameterEnum(block, path),
          sensitive: false,
          editable: true
        },
        value
      )
      buffer.value.dirty[block] = true
      return true
    } catch (err) {
      handleError(err)
      return false
    }
  }

  /**
   * 删除块 JSON 里的一个字段（把键拿掉）。
   *
   * 口径：
   *  - 语义 = **回到后端 DTO 默认值**（键不在 JSON 里 ⇒ 后端反序列化取默认值）
   *  - 删完父对象空了 ⇒ 父对象一并删掉；块根删空 ⇒ 块保持 `{}` ⇒ PUT 不带该块
   *    ⇒ 仍被 `EMPTY_*` 拦（「没配」不会被悄悄变成「按默认配」）
   *  - 数组行（editable=false）不走这里（页面不给删除入口）
   */
  function removeBlockField(block: BlockKey, path: string[]): boolean {
    if (!buffer.value.workingBlocks || !buffer.value.governance) {
      error.value = '参数集草稿未载入'
      return false
    }
    if (!canWrite.value) {
      error.value = '当前操作者无写权限（需权限码 aps.parameter.edit）'
      return false
    }
    try {
      removePath(buffer.value.workingBlocks, block, path)
      // 块里只剩锚点编码空值 ⇒ 视同空块（否则会绕过 EMPTY_*，等于替用户按默认配）
      pruneAnchorOnlyBlock(buffer.value.workingBlocks, block)
      buffer.value.dirty[block] = true
      return true
    } catch (err) {
      handleError(err)
      return false
    }
  }

  /* ===== 锚点资源编码（专属输入项；3号位 2026-10-25 定调：自由文本，后端零改动）===== */

  /**
   * 写锚点资源编码（自由文本）。
   *
   * 口径：
   *  - 写 `workingBlocks.solverStrategy.AnchorResourceCode`（trim 后），并置脏
   *  - 值没变 ⇒ 不置脏（避免「点了一下又不动」把草稿标成已修改）
   *  - 不校验编码是否存在：后端发布校验不查这个字段的取值域（3号位 §五 验收 #4），
   *    无效编码在运行期 no-op ≡ Auto ⇒ 前端也不拦
   */
  function setAnchorResourceCode(code: string): void {
    if (!buffer.value.workingBlocks || !buffer.value.governance) {
      error.value = '参数集草稿未载入'
      return
    }
    if (!canWrite.value) {
      error.value = '当前操作者无写权限（需权限码 aps.parameter.edit）'
      return
    }
    const ss = buffer.value.workingBlocks.solverStrategy
    const next = String(code ?? '').trim()
    if (readAnchorResourceCode(ss) === next) return
    writeAnchorResourceCode(ss, next)
    buffer.value.dirty.solverStrategy = true
  }

  /* ===== 仓库引用四处落点（专属编辑器用；3号位 2026-10-25 §四）===== */

  /** 拉仓库字典（一次拉全量，页面本地过滤；语义与端点 search 一致：编码或名称） */
  async function loadWarehouseDict(force = false): Promise<void> {
    if (warehouseDictLoaded.value && !force) return
    try {
      const list = await warehouseDictionaryApi.listWarehouses()
      warehouseDict.value = list
      warehouseDictLoaded.value = true
      warehouseDictError.value = null
    } catch (err) {
      warehouseDict.value = []
      warehouseDictLoaded.value = false
      warehouseDictError.value =
        err instanceof ApiError && err.code === 403
          ? '当前账号没有读取仓库字典的权限（需权限码 aps.plan.view），暂时只能看已配置的编码、不能从字典里选。'
          : `仓库字典没拉下来：${err instanceof ApiError ? err.message : String(err)}`
    }
  }

  /** 写仓库优先级的公共实现（只改内存 + 置脏） */
  function writeWarehouseArray(block: BlockKey, path: readonly string[], values: string[]): void {
    if (!buffer.value.workingBlocks || !buffer.value.governance) {
      error.value = '参数集草稿未载入'
      return
    }
    if (!canWrite.value) {
      error.value = '当前操作者无写权限（需权限码 aps.parameter.edit）'
      return
    }
    writeStringArray(buffer.value.workingBlocks, block, path, values)
    buffer.value.dirty[block] = true
  }

  /** ① 供应参数 → 库存可用 → 仓库优先级（有序，顺序即优先级） */
  function setSupplyWarehousePriority(codes: string[]): void {
    writeWarehouseArray('supply', SUPPLY_WAREHOUSE_PRIORITY_PATH, codes)
  }

  /** ② 采购参数 → 仓库优先级（有序，顺序即优先级） */
  function setProcurementWarehousePriority(codes: string[]): void {
    writeWarehouseArray('procurement', PROCUREMENT_WAREHOUSE_PRIORITY_PATH, codes)
  }

  /** ③ 采购参数 → 默认采购提前期 */
  function setDefaultPurchaseLt(items: PurchaseLtItem[]): void {
    if (!buffer.value.workingBlocks || !buffer.value.governance) {
      error.value = '参数集草稿未载入'
      return
    }
    if (!canWrite.value) {
      error.value = '当前操作者无写权限（需权限码 aps.parameter.edit）'
      return
    }
    writeObjectArray(buffer.value.workingBlocks, 'procurement', DEFAULT_PURCHASE_LT_PATH, items)
    buffer.value.dirty.procurement = true
  }

  /** ④ 采购参数 → 到货可用偏移 */
  function setArrivalToUsableOffsets(items: ArrivalOffsetItem[]): void {
    if (!buffer.value.workingBlocks || !buffer.value.governance) {
      error.value = '参数集草稿未载入'
      return
    }
    if (!canWrite.value) {
      error.value = '当前操作者无写权限（需权限码 aps.parameter.edit）'
      return
    }
    writeObjectArray(
      buffer.value.workingBlocks,
      'procurement',
      ARRIVAL_TO_USABLE_OFFSETS_PATH,
      items
    )
    buffer.value.dirty.procurement = true
  }

  /** ⑤ 采购参数 → 计划良率（专属编辑器写入口；零新端点，仍走版本草稿 PUT） */
  function setPlanningYields(items: PlanningYieldItem[]): void {
    if (!buffer.value.workingBlocks || !buffer.value.governance) {
      error.value = '参数集草稿未载入'
      return
    }
    if (!canWrite.value) {
      error.value = '当前操作者无写权限（需权限码 aps.parameter.edit）'
      return
    }
    writePlanningYields(buffer.value.workingBlocks, items)
    buffer.value.dirty.procurement = true
  }

  /* ===== 跨厂LT 标准参数（参数集版本第 6 号子块：走通用版本 GET/PUT）===== */

  /**
   * 读当前版本的跨厂LT 规则
   *
   * ⚠️ 2026-11-18：读**通用版本详情**的 `crossFactoryLeadTimeRulesJson`
   *   （3号位《跨厂LT 走通用参数接口与快照保留_回执 v1.1_20261117》）。
   *   缺块 / 空 ⇒ `null` ⇒ 这一版**没配**跨厂LT（不是「读不到」，两者业务含义不同）。
   *   缓冲里已经是同一版时直接复用，不再单独发一次请求。
   */
  async function loadCrossFactoryLeadTimeRules(versionId?: number): Promise<void> {
    crossFactoryLtLoading.value = true
    crossFactoryLtError.value = null
    if (!versionId) {
      crossFactoryLtRows.value = []
      crossFactoryLtSavedJson.value = '[]'
      crossFactoryLtKeyPresent.value = false
      crossFactoryLtLoading.value = false
      return
    }
    try {
      const buffered = buffer.value.governance
      const raw =
        buffered && buffered.id === versionId
          ? (buffered.crossFactoryLeadTimeRulesJson ?? null)
          : ((await parameterSetApi.getParameterSetVersion(versionId))
              .crossFactoryLeadTimeRulesJson ?? null)
      const rows = parseCrossFactoryLeadTimeRules(raw)
      crossFactoryLtRows.value = rows
      // 指纹取**序列化后**的形态（与 dirty 判据、写入载荷同形 ⇒ 存完必然「已保存」）
      crossFactoryLtSavedJson.value = JSON.stringify(serializeCrossFactoryLeadTimeRules(rows))
      // 键在不在快照顶层：`null`/空 ⇒ 缺块（这一版没配跨厂LT，合法）
      crossFactoryLtKeyPresent.value =
        raw !== null && raw !== undefined && String(raw).trim() !== ''
    } catch (err) {
      crossFactoryLtRows.value = []
      crossFactoryLtSavedJson.value = '[]'
      crossFactoryLtKeyPresent.value = false
      crossFactoryLtError.value =
        err instanceof ApiError ? err.message : '读取跨厂LT 标准参数失败，请稍后重试'
    } finally {
      crossFactoryLtLoading.value = false
    }
  }

  /**
   * 写完核对：重新读通用版本详情，看这一块是不是真的落下去了
   *
   * ⚠️ 为什么要有这一步（3号位 §三 R3：认收并要求**保留**）：
   *   通用 PUT 是**请求回显**（200 不等于已落库），且后端曾有「五主题皆空 ⇒ 早退 ⇒
   *   本块被静默吞掉、库里还是旧值/空」的真缺口（3号位 R1 已修）。
   *   读回来比对不一致 ⇒ 如实说「没存上」，不假装成功。
   *   读不回来（网络/报错）⇒ **不臆断**成失败，返回 true（没证据说没存上）。
   */
  async function crossFactoryBlockPersisted(
    versionId: number,
    sent: CrossFactoryLeadTimeItem[]
  ): Promise<boolean> {
    try {
      const version = await parameterSetApi.getParameterSetVersion(versionId)
      const now = parseCrossFactoryLeadTimeRules(version.crossFactoryLeadTimeRulesJson ?? null)
      return (
        JSON.stringify(serializeCrossFactoryLeadTimeRules(now)) ===
        JSON.stringify(serializeCrossFactoryLeadTimeRules(sent))
      )
    } catch {
      return true
    }
  }

  /** 本地编辑跨厂LT 规则行（只改内存，不落库） */
  function setCrossFactoryLeadTimeRules(items: CrossFactoryLeadTimeItem[]): void {
    crossFactoryLtRows.value = items
  }

  /** 当前编辑态里引用到的仓库编码（按 Supply / Procurement 分组） */
  const currentWarehouseRefs = computed<WarehouseRefGroups>(() =>
    collectWarehouseRefs(buffer.value.workingBlocks)
  )

  /** 当前编辑态里字典中不存在的编码（发布校验会报 INVALID_WAREHOUSE_REF） */
  const currentUnknownWarehouseCodes = computed<string[]>(() =>
    unknownWarehouseCodes(
      [...currentWarehouseRefs.value.supply, ...currentWarehouseRefs.value.procurement],
      warehouseDictCodes.value
    )
  )

  /* ===== fork 前置预检（源版本里引用了字典中不存在的仓库编码）===== */

  const forkSourceRefs = ref<WarehouseRefGroups | null>(null)
  const forkSourceUnknownCodes = ref<string[]>([])

  /**
   * 预检 fork 源版本里的仓库引用
   *
   * fork = GET 源版本 → POST 新版本（5 主题原样带走），所以**源版本里坏掉的仓库编码会被原样继承**，
   * 新草稿发布时才报 INVALID_WAREHOUSE_REF ⇒ 提前在 fork 对话框里说清（3号位 §四）。
   */
  async function checkForkSourceWarehouseRefs(sourceVersionId: number): Promise<void> {
    forkSourceRefs.value = null
    forkSourceUnknownCodes.value = []
    if (!sourceVersionId) return
    try {
      const src = await parameterSetApi.getParameterSetVersion(sourceVersionId)
      const blocks = parseParameterSetBlocks(src)
      const groups = collectWarehouseRefs(blocks)
      forkSourceRefs.value = groups
      forkSourceUnknownCodes.value = unknownWarehouseCodes(
        [...groups.supply, ...groups.procurement],
        warehouseDictCodes.value
      )
    } catch (err) {
      // 预检失败**不阻断** fork（预检只是提示，不是校验闸门）
      console.warn('[参数集] fork 预检：读取源版本失败，跳过仓库编码提示', err)
    }
  }

  /** 放弃未保存修改 */
  function onCancelDirty(): void {
    if (buffer.value.originalBlocks) {
      buffer.value.workingBlocks = JSON.parse(JSON.stringify(buffer.value.originalBlocks))
    }
    buffer.value.dirty = emptyDirtyFlags()
    // 跨厂LT 那一块也一并还原（2026-11-19：撤掉块内「保存这块」后，这一块只能随草稿保存）
    if (crossFactoryLtSavedJson.value) {
      crossFactoryLtRows.value = parseCrossFactoryLeadTimeRules(crossFactoryLtSavedJson.value)
    }
    crossFactoryLtError.value = null
  }

  /**
   * 保存草稿（PUT /api/governance/parameter-set/version/{id}）
   *  - full-object PUT：5 个主题 JSON 必须全量回传（后端五主题归一化会重建快照）
   *  - 后端 409（OCC 冲突）→ 提示用户刷新，**不丢**未保存状态
   *
   * 2026-11-18：跨厂LT 子块（第 6 号内容块）**随同一次 PUT 提交**
   *   —— 3号位《跨厂LT 走通用参数接口与快照保留_回执 v1.1_20261117》。
   *
   * 2026-11-19（用户指示）：跨厂LT 的拦截**从「保存」挪到「校验/发布」** ——
   *   ① 保存草稿**不再**因这一块被拦（行没填全、一行都没有，先都存下来）；
   *   ② 发送口径：本块**被改过**才带这个键（不带＝保留既有块）；带就以编辑态为准 ——
   *      **零行也照发 `[]`**（后端 PUT 期透传不拦；「键存在但零行」由后端在**校验/发布**期
   *      报 `CROSS_FACTORY_LT_BLOCK_EMPTY` 拒掉 ⇒ 拦截落在用户要的那一步）；
   *   ③ 「零行」由发布前的 `publish()` 守卫（说人话）+ 后端校验共同兜住，
   *      保存期不再拦 —— 别让用户以为行删掉了却其实没存上。
   *
   * 2026-11-19（用户指示）：**保存草稿不再弹「变更原因」框，点一下直接存**。
   *   依据：后端保存草稿的 UPDATE 语句里**没有 `Remarks` 列**
   *   （`ParameterSetVersionRepository.UpdateAsync`），审计 `Remark` 写死「参数集版本更新」
   *   ⇒ 用户填的变更原因**后端既不收也不存**，弹框纯属仪式 ⇒ 直接去掉。
   *   ⚠️ **发布**那一步的原因仍然要填：publish 端点把 `changeReason` 写进审计 `Remark`。
   */
  async function onSaveDraft(changeReason = ''): Promise<boolean> {
    const buf = buffer.value
    if (!buf.governance || !buf.workingBlocks) {
      error.value = '参数集草稿未载入'
      return false
    }
    const versionId = buf.governance.id
    if (!versionId) {
      error.value = '版本标识缺失，无法保存草稿'
      return false
    }
    buf.saving = true
    error.value = null
    try {
      const ltRows = serializeCrossFactoryLeadTimeRules(crossFactoryLtRows.value)
      // 本块被改过才带这个键：带＝以编辑态为准（零行也照发 `[]`，由后端在校验/发布期判非法）；
      // 不带＝本次不维护本块 ⇒ 后端保留既有快照值。
      const ltJson = crossFactoryLtDirty.value ? JSON.stringify(ltRows) : undefined
      const putBody = buildPutBody(buf.governance, buf.workingBlocks, ltJson)
      // 变更原因：后端保存草稿不落 Remarks 列（UPDATE 无该列）⇒ 默认不带；
      // 万一调用方仍传了原因就带上（不影响后端，但保持载荷语义完整）。
      if (changeReason.trim()) putBody.remarks = changeReason
      const result = await parameterSetApi.updateParameterSetDraft({
        versionId,
        body: putBody as ParameterSetVersion
      })
      buf.governance = result
      buf.originalBlocks = JSON.parse(JSON.stringify(buf.workingBlocks))
      buf.dirty = emptyDirtyFlags()
      if (ltJson !== undefined) {
        // 这一块随草稿一起提交了 ⇒ 写完核对一次（读回来的内容与提交的不一致 ⇒ 这一块没存上）
        const persisted = await crossFactoryBlockPersisted(versionId, ltRows)
        if (persisted) {
          crossFactoryLtRows.value = ltRows
          crossFactoryLtSavedJson.value = JSON.stringify(ltRows)
          // 键已经存在于快照顶层（哪怕零行）⇒ 发布期会按「键存在但零行」判非法
          crossFactoryLtKeyPresent.value = true
        } else {
          crossFactoryLtError.value =
            '跨厂LT 那一块没存上（读回来的还是原来的内容），其余改动已保存。'
        }
      }
      // 草稿内容已落库 ⇒ 版本对比要按新内容重算（否则「我这次改了什么」还是旧值）
      await refreshDiff()
      return true
    } catch (err) {
      if (err instanceof ApiError && err.code === 409) {
        error.value = `版本已被他人修改（冲突）：${err.message}。请刷新页面后再编辑。`
      } else {
        handleError(err)
      }
      return false
    } finally {
      buf.saving = false
    }
  }

  /**
   * 当前展示的那一版（**一版一行**后「校验草稿 / 发布」都作用在这一版上）
   *
   * 显示的是草稿 ⇒ 用它；否则回落到历史里最后一个草稿（兼容不传版本的旧调用）。
   */
  function displayedDraftVersionId(): number | undefined {
    const detail = currentDetail.value
    if (!detail) return undefined
    if (detail.summary.status === 'DRAFT' && detail.version?.id) return detail.version.id
    return (detail.history ?? []).filter((h) => h.status === 'DRAFT').pop()?.versionId
  }

  /**
   * 当前展示的那一版若是已发布 ⇒ 用它；否则回落到历史里最后一个已发布版本
   *
   * ⚠️ 与 `displayedDraftVersionId` 对称：停用对象是**已发布版本**，
   *   不是参数集整体状态（停用主表 ≠ 停用版本）。
   */
  function displayedPublishedVersionId(): number | undefined {
    const detail = currentDetail.value
    if (!detail) return undefined
    if (detail.summary.status === 'PUBLISHED' && detail.version?.id) return detail.version.id
    return (detail.history ?? []).filter((h) => h.status === 'PUBLISHED').pop()?.versionId
  }

  /** 校验草稿（后端返回 PublishValidationResult：isValid / errors / warnings） */
  async function validateDraft(): Promise<boolean> {
    validating.value = true
    error.value = null
    try {
      const versionId = displayedDraftVersionId()
      if (!versionId) {
        error.value = '当前没有草稿版本可校验'
        return false
      }
      const result = await parameterSetApi.validateDraft(versionId)
      // 校验的是**已保存的草稿内容**（后端读快照）⇒ 有未保存改动时补一句提示，
      // 免得用户拿「编辑中」的内容去对「已保存」的校验结果。
      if (dirty.value) {
        result.warnings = [
          ...(result.warnings ?? []),
          {
            code: 'UNSAVED_LOCAL_CHANGES',
            message: '当前还有未保存的修改，这次校验的是「已保存」的草稿内容。'
          }
        ]
      }
      lastValidation.value = result
      return result.isValid
    } catch (err) {
      handleError(err)
      return false
    } finally {
      validating.value = false
    }
  }

  /** 发布（需 aps.parameter.publish）；后端返回一句文本，发布后重新拉取读真状态 */
  async function publish(changeReason: string): Promise<boolean> {
    if (!canPublishAction.value) {
      error.value = '当前操作者无参数集发布权限（需权限码 aps.parameter.publish）'
      return false
    }
    if (dirty.value) {
      error.value = '存在未保存的修改，请先保存草稿再发布'
      return false
    }
    // 跨厂LT 零行（键在快照顶层、但一行都没有）＝ 配置形态非法，后端**校验 / 发布**期会拒
    // （CROSS_FACTORY_LT_BLOCK_EMPTY）⇒ 这里先拦一道并说人话。
    // ⚠️ 保存期**不拦**（2026-11-19 用户指示）：拦截落在「校验 / 发布」这一步。
    if (crossFactoryLtKeyPresent.value && crossFactoryLtRows.value.length === 0) {
      error.value =
        '跨厂LT 标准参数这一块一行都没有，这个形态不合法（校验和发布都会被拦）：要停用某条规则请把那一行的「生效」关掉（别把行删光）。'
      return false
    }
    const versionId = displayedDraftVersionId()
    if (!versionId) {
      error.value = '当前没有草稿版本可发布'
      return false
    }
    actionRunning.value = true
    error.value = null
    try {
      await parameterSetApi.publishParameterSet({ versionId, changeReason, actor: actor.value })
      const id = currentDetail.value?.summary.parameterSetId
      await loadList()
      // 一版一行后仍停在这一版（它已变成已发布），列表随后整体刷新
      if (id) await loadDetail(id, versionId)
      return true
    } catch (err) {
      handleError(err)
      return false
    } finally {
      actionRunning.value = false
    }
  }

  /** 停用（后端 disable 挂 aps.parameter.edit；页面按「停用 = 发布动作」口径要发布权） */
  async function disable(reason: string): Promise<boolean> {
    if (!canPublishAction.value) {
      error.value = '当前操作者无停用权限（需权限码 aps.parameter.publish）'
      return false
    }
    const versionId = displayedPublishedVersionId()
    if (!versionId) {
      error.value = '当前没有已发布版本可停用'
      return false
    }
    actionRunning.value = true
    error.value = null
    try {
      await parameterSetApi.disableParameterSet({ versionId, reason })
      const id = currentDetail.value?.summary.parameterSetId
      await loadList()
      // 停用后仍停在这一版（它已变成已停用）
      if (id) await loadDetail(id, versionId)
      return true
    } catch (err) {
      handleError(err)
      return false
    } finally {
      actionRunning.value = false
    }
  }

  /**
   * 物理删除参数集整族（DELETE /parameter-set/{id}，权限 aps.parameter.edit）
   *
   *  最窄口径：无任何已发布版本才可硬删（连带删草稿）；含 PUBLISHED、或被策略包版本引用，
   *  后端 400 并给原文（前端不做二次翻译，也不预判引用关系）。
   *
   *  ⚠️ 写接口报错 ≠ 没写成：失败后回列表核对，行已不在即按已删除继续。
   */
  async function removeParameterSet(parameterSetId: number): Promise<boolean> {
    if (!canWrite.value) {
      error.value = '当前操作者无删除权限（需权限码 aps.parameter.edit）'
      return false
    }
    actionRunning.value = true
    error.value = null
    let failedMsg: string | null = null
    try {
      await parameterSetApi.deleteParameterSet(parameterSetId)
    } catch (err) {
      failedMsg = (err as Error)?.message ?? '删除请求失败'
    }
    try {
      await loadList()
    } catch {
      /* loadList 内部已 handleError */
    }
    const stillThere = parameterSets.value.some((p) => p.parameterSetId === parameterSetId)
    actionRunning.value = false
    if (stillThere) {
      if (failedMsg) handleError(new Error(failedMsg))
      return false
    }
    if (currentDetail.value?.summary.parameterSetId === parameterSetId) {
      currentDetail.value = null
    }
    return true
  }

  /**
   * 新建草稿（fork）
   *  只 fork **参数集**（5 个主题 JSON 原样带走）。
   *  后端无 fork 端点 ⇒ GET 源版本 → POST 新版本。
   */
  async function forkDraft(
    sourceParameterSetVersionId: number,
    newVersionCode: string,
    changeReason?: string
  ): Promise<boolean> {
    if (!canWrite.value) {
      error.value = '当前操作者无写权限（需权限码 aps.parameter.edit）'
      return false
    }
    if (!sourceParameterSetVersionId) {
      error.value = '找不到可作为起点的版本'
      return false
    }
    actionRunning.value = true
    error.value = null
    try {
      const created = await parameterSetApi.forkDraft(
        sourceParameterSetVersionId,
        newVersionCode,
        changeReason
      )
      await loadList()
      const id = currentDetail.value?.summary.parameterSetId
      // 落点切到**刚建出来的那条草稿**（一版一行后，列表里它就是新的一行）
      if (id) await loadDetail(id, created?.id)
      return true
    } catch (err) {
      handleError(err)
      return false
    } finally {
      actionRunning.value = false
    }
  }

  function setStatusFilter(filter: RuleStatus | typeof NO_VERSION | 'ALL'): void {
    statusFilter.value = filter
  }

  /**
   * 排序（2026-11-11 筛选条件）
   *
   * DEFAULT = 保持后端返回顺序；其余按时间/编码排序。
   * 时间字段可能为空（无版本行没有发布时间）⇒ 空值排在最后（降序时也是最后）。
   */
  function sortParameterSetRows(rows: ParameterSetListRowDto[]): ParameterSetListRowDto[] {
    const list = [...rows]
    switch (listSort.value) {
      case 'PUBLISHED_DESC':
        return list.sort((a, b) => (b.publishedAt ?? '').localeCompare(a.publishedAt ?? ''))
      case 'CREATED_DESC':
        return list.sort((a, b) => (b.createdAt ?? '').localeCompare(a.createdAt ?? ''))
      case 'CODE_ASC':
        return list.sort((a, b) =>
          a.parameterSetCode.localeCompare(b.parameterSetCode, 'zh-Hans-CN')
        )
      default:
        return list
    }
  }

  function setListKeyword(keyword: string): void {
    listKeyword.value = keyword
  }

  function setListActiveFilter(filter: 'ALL' | 'ACTIVE' | 'INACTIVE'): void {
    listActiveFilter.value = filter
  }

  function setListSort(sort: ParameterListSort): void {
    listSort.value = sort
  }

  /** 一键清空筛选条件（关键词 / 主表状态 / 排序） */
  function resetListFilters(): void {
    listKeyword.value = ''
    listActiveFilter.value = 'ALL'
    listSort.value = 'DEFAULT'
  }

  function handleError(err: unknown): void {
    if (err instanceof ApiError) {
      error.value =
        `[${err.code}] ${err.message}` + (err.traceId ? ` (traceId=${err.traceId})` : '')
    } else {
      error.value = (err as Error)?.message ?? '未知错误'
    }
  }

  function reset(): void {
    currentDetail.value = null
    currentDiff.value = null
    lastValidation.value = null
    buffer.value = emptyBufferState()
    error.value = null
    crossFactoryLtRows.value = []
    crossFactoryLtSavedJson.value = '[]'
    crossFactoryLtError.value = null
  }

  return {
    // state
    parameterSets,
    currentDetail,
    currentDiff,
    paramDiffRows,
    paramDiffPair,
    lastValidation,
    statusFilter,
    listKeyword,
    listActiveFilter,
    listSort,
    loading,
    listLoading,
    actionRunning,
    validating,
    error,
    actor,
    actorRoles,
    buffer,
    warehouseDict,
    warehouseDictLoaded,
    warehouseDictError,
    // getters
    filteredParameterSets,
    scopedParameterSets,
    tabCounts,
    parameterSetTotal,
    currentStatus,
    canWrite,
    canPublishAction,
    actions,
    dirty,
    flattenedRows,
    rowsGrouped,
    anchorHintMessage,
    anchorResourceCode,
    solverStrategyHasContent,
    warehouseDictCodes,
    warehouseNameMap,
    supplyWarehousePriority,
    procurementWarehousePriority,
    defaultPurchaseLt,
    arrivalToUsableOffsets,
    planningYields,
    crossFactoryLtRules,
    crossFactoryLtDirty,
    crossFactoryLtLoading,
    crossFactoryLtError,
    crossFactoryLtKeyPresent,
    currentWarehouseRefs,
    currentUnknownWarehouseCodes,
    forkSourceRefs,
    forkSourceUnknownCodes,
    // actions
    loadList,
    loadDetail,
    createParameterSet,
    updateParameterSet,
    loadDiff,
    refreshDiff,
    onCellEdit,
    addBlockField,
    removeBlockField,
    setAnchorResourceCode,
    loadWarehouseDict,
    setSupplyWarehousePriority,
    setProcurementWarehousePriority,
    setDefaultPurchaseLt,
    setArrivalToUsableOffsets,
    setPlanningYields,
    loadCrossFactoryLeadTimeRules,
    setCrossFactoryLeadTimeRules,
    checkForkSourceWarehouseRefs,
    onCancelDirty,
    onSaveDraft,
    validateDraft,
    publish,
    disable,
    removeParameterSet,
    forkDraft,
    setStatusFilter,
    setListKeyword,
    setListActiveFilter,
    setListSort,
    resetListFilters,
    reset,
    handleError
  }
})
