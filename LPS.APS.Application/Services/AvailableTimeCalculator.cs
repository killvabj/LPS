using LPS.APS.BusinessRules.Models;
using LPS.APS.Core.Dto;
using LPS.APS.Core.Rules;

namespace LPS.APS.Application.Services;

/// <summary>
/// AvailableTime 纯函数计算（2号位职责，阶段 3）。
/// 消费 5号位已交付的原始事实（<see cref="RawProcurementFact"/>）+ Manual ETA 覆盖，
/// 按 3号位 <see cref="EtaInvariant"/> 固化优先级链（Manual &gt; ERP &gt; ReleaseDate+DefaultLT）解析 Effective ETA，
/// 逾期在途按 0号位 2026-10-25 统一算法重估后，再加 Arrival-to-Usable Offset 得 AvailableTime。
/// 纯静态、无 I/O、确定性——便于单测。
///
/// <para>
/// <b>逾期重估（0号位《致2号位_采购ETA过期及无ETA统一算法执行指令 v1.0》§2）</b>：
/// 来源优先级永远先行（人工 &gt; ERP &gt; 默认推算），命中后再判是否逾期：
/// </para>
/// <list type="number">
///   <item>选中 ETA ≥ <c>DataCutoffTime</c> ⇒ 原值即用；</item>
///   <item>选中 ETA &lt; <c>DataCutoffTime</c> 且该供给仍在途（<see cref="InTransitSupplyTypes"/>）
///         且命中合法同仓 DefaultLT ⇒ <c>ETA* = DataCutoffTime + ceil(DefaultLtDays × MarginPercent)</c> 天，留 WARN；</item>
///   <item>同上但缺合法同仓 DefaultLT ⇒ **不给日期**（<c>AvailableAt = null</c>），留「无合法可用时点」诊断；
///         不得用别的仓的 LT、不得凭空填今天或 0 天；</item>
///   <item>选中 ETA &lt; <c>DataCutoffTime</c> 但该供给**已不在途**（如 <c>ARRIVED_NOT_RECEIVED</c>／<c>VMI_ONSITE</c>）⇒
///         **不套在途重估**，按原 ETA 算（过去时点即视为已可用），由质量/所有权资格各自门禁判定。</item>
/// </list>
/// <para>
/// <b>不消费 <c>OverdueMargin.MinimumExtraDays</c></b>（0号位 已同意后续受控废弃，历史已发布快照不篡改）。
/// <b>不得接通 5号位 <c>TimedSupplyFactCalculator.ApplyOverdueMargin</c></b>（旧单值全局 LT、丢仓维度）。
/// </para>
/// <para>
/// <b>Offset 只加一次</b>：本类只加 Run 冻结 JSON 的 <c>ArrivalToUsableOffsets</c>；事实表
/// <c>SupplyFact_Pipeline.AppliedLeadTimeOffset</c>／兼容规则 <c>SupplyAvailabilityRule.LeadTimeOffset</c>
/// 不得在此之外再叠一次。
/// </para>
/// </summary>
internal static class AvailableTimeCalculator
{
    /// <summary>
    /// 「尚未实际到货」的供给类型 —— 只有这些才适用在途逾期重估（0号位 §2「仍在途的正式采购供给」）。
    /// <para>
    /// 口径：<c>PURCHASE_IN_TRANSIT</c>（供应商已发货、尚未到厂）、<c>OPEN_PO_REMAINING</c>（PO 已下、未发完）。
    /// 明确**排除**：<c>ARRIVED_NOT_RECEIVED</c>（已到厂未入库，ETA 早于截止时点往往反映已发生的到货，按 §3 另判）、
    /// <c>VMI_ONSITE</c>（在场寄售）、<c>SHIPPING_INSTRUCTION</c>／<c>PRODUCTION_INSTRUCTION</c>（非采购）、
    /// <c>INTERPLANT_IN_TRANSIT</c>（厂间在途，已在入池前按冻结禁令单独拦截）。
    /// </para>
    /// </summary>
    public static readonly IReadOnlySet<string> InTransitSupplyTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PURCHASE_IN_TRANSIT",
            "OPEN_PO_REMAINING"
        };

    /// <summary>该供给类型是否「尚未实际到货」（决定逾期时套不套重估公式）。</summary>
    public static bool IsInTransitSupplyType(string? supplyType)
        => !string.IsNullOrWhiteSpace(supplyType) && InTransitSupplyTypes.Contains(supplyType);

    /// <summary>
    /// 构建 Manual ETA 覆盖查找表（键：PONo+LineNo+MaterialId+ReceivingWarehouse）。
    /// 仅保留 IsActive 且 ManualEta 非 default 的记录；default(DateTime) 按 EtaInvariant 约定规范化为无值。
    /// </summary>
    public static Dictionary<(string, int, int, string), DateTime> BuildManualEtaMap(
        IReadOnlyList<ProcurementManualEtaOverride> overrides)
    {
        var map = new Dictionary<(string, int, int, string), DateTime>();
        foreach (var o in overrides)
        {
            if (!o.IsActive || o.ManualEta == default)
                continue;
            map[(o.PONo, o.LineNo, o.MaterialId, o.ReceivingWarehouse)] = o.ManualEta;
        }
        return map;
    }

    /// <summary>
    /// 计算 AvailableTime = 解析/重估后的 ETA + ArrivalToUsableOffset。
    /// 三级来源均无有效 ETA、或逾期却无合法重估日期时返回 null（无可用时点）。
    /// 注意：5号位 ODS 未透出 ReleaseDate（恒 NULL）时 DefaultLT 兜底链走不通，但 Manual/ERP 两级不受影响。
    /// </summary>
    /// <param name="dataCutoffTime">本 Run 的数据截止时点（= <c>ScheduleRun.DataCutoffTime</c>），逾期判定与重估基准。</param>
    public static DateTime? Compute(
        RawProcurementFact fact,
        IReadOnlyDictionary<(string, int, int, string), DateTime> manualEtaMap,
        FrozenStrategySnapshot snapshot,
        DateTime dataCutoffTime)
        => Explain(fact, manualEtaMap, snapshot, dataCutoffTime).AvailableAt;

    /// <summary>
    /// <see cref="Compute"/> 的**带因版本**：同样解析三级链并做逾期重估，额外回报「哪一级没给值」「是否重估」。
    /// 仅供装载侧留痕与诊断计数使用，不参与任何业务判定 —— 两者必须同源，否则日志会与真实结果漂移。
    /// </summary>
    /// <param name="fact">5号位 交付的原始采购事实（`SupplyType` / `Eta` / `ReleaseDate` / `StorageCode`）。</param>
    /// <param name="manualEtaMap">Manual ETA 覆盖表（<see cref="BuildManualEtaMap"/>）。</param>
    /// <param name="snapshot">冻结策略快照（提供 `DefaultPurchaseLt`、`OverdueMargin.MarginPercent` 与 `ArrivalToUsableOffsets`）。</param>
    /// <param name="dataCutoffTime">本 Run 的数据截止时点（= <c>ScheduleRun.DataCutoffTime</c>），逾期判定与重估基准。</param>
    public static EtaDiagnostics Explain(
        RawProcurementFact fact,
        IReadOnlyDictionary<(string, int, int, string), DateTime> manualEtaMap,
        FrozenStrategySnapshot snapshot,
        DateTime dataCutoffTime)
    {
        // 1) Manual ETA（最高优先，来源 ProcurementManualEtaOverride 表）
        DateTime? manualEta = null;
        if (int.TryParse(fact.SourceDocumentLineNo, out var lineNo)
            && manualEtaMap.TryGetValue((fact.SourceDocumentNo, lineNo, fact.MaterialId, fact.StorageCode), out var manual))
        {
            manualEta = manual;
        }

        // 2) ERP ETA
        var erpEta = fact.Eta;

        // 3) DefaultLT 推算：ReleaseDate + DefaultPurchaseLt（按 Warehouse + Material 维度）
        var ltRule = ResolvePurchaseLtRule(snapshot.Procurement.DefaultPurchaseLt, fact.StorageCode, fact.MaterialId);
        DateTime? defaultLtEta = null;
        if (ltRule != null && fact.ReleaseDate.HasValue)
            defaultLtEta = fact.ReleaseDate.Value.AddDays(ltRule.DefaultLtDays);

        // 来源优先级永远先行：人工 ETA 过期也不自动切 ERP，只保留当前来源再重估
        var resolution = EtaInvariant.Resolve(manualEta, erpEta, defaultLtEta);

        // 4) Arrival-to-Usable Offset（按 Receiving Warehouse，只加一次）
        var offsetHours = ResolveArrivalOffset(snapshot.Procurement.ArrivalToUsableOffsets, fact.StorageCode);

        var chain = new EtaChain(
            HasManualEta: manualEta.HasValue,
            HasErpEta: erpEta.HasValue,
            HasPurchaseLtRule: ltRule != null,
            HasReleaseDate: fact.ReleaseDate.HasValue,
            LegalLeadTimeDays: ltRule?.DefaultLtDays,
            AppliedOffsetHours: offsetHours);

        if (!resolution.HasEta)
        {
            return new EtaDiagnostics(
                AvailableAt: null, Chain: chain,
                Source: EtaSource.None, SelectedEta: null,
                UnavailableReason: EtaUnavailableReason.NoEtaSource);
        }

        var selectedEta = resolution.EffectiveEta!.Value;

        // ① 未逾期：原值即用
        if (selectedEta >= dataCutoffTime)
        {
            return new EtaDiagnostics(
                AvailableAt: selectedEta.AddHours(offsetHours), Chain: chain,
                Source: resolution.Source, SelectedEta: selectedEta);
        }

        // ② 已不在途（已到厂／在场寄售等）：不套在途重估，原 ETA 即合法可用时点，
        //    质量/所有权资格由各自门禁判定，不在此处放宽。
        if (!IsInTransitSupplyType(fact.SupplyType))
        {
            return new EtaDiagnostics(
                AvailableAt: selectedEta.AddHours(offsetHours), Chain: chain,
                Source: resolution.Source, SelectedEta: selectedEta,
                Overdue: OverdueEtaOutcome.NotInTransitKeptOriginal);
        }

        // ③ 在途且逾期，但无合法同仓 DefaultLT ⇒ 没有可计算的重估日期。
        //    不得用别仓 LT 代替、不得凭空填今天或 0 天 ⇒ 不给时点，交诊断。
        if (ltRule is null || ltRule.DefaultLtDays <= 0)
        {
            return new EtaDiagnostics(
                AvailableAt: null, Chain: chain,
                Source: resolution.Source, SelectedEta: selectedEta,
                Overdue: OverdueEtaOutcome.NoLegalLeadTime,
                UnavailableReason: EtaUnavailableReason.OverdueNoLegalLeadTime);
        }

        // ④ 在途逾期 + 合法 LT ⇒ ETA* = R + ceil(L × p) 天，并留 WARN。
        //    注：MarginPercent 为 0 时按公式得 R 本身（即「立即可用」）——这是已确认公式的字面结果，
        //    参数是否取得非零业务值由 3号位 发布侧负责，本类不擅自改写。
        var marginPercent = snapshot.Procurement.OverdueMargin?.MarginPercent ?? 0m;
        var extraDays = (int)Math.Ceiling(ltRule.DefaultLtDays * (double)marginPercent);
        var riskEta = dataCutoffTime.AddDays(extraDays);

        return new EtaDiagnostics(
            AvailableAt: riskEta.AddHours(offsetHours), Chain: chain,
            Source: resolution.Source, SelectedEta: selectedEta,
            Overdue: OverdueEtaOutcome.Recalculated,
            RiskEta: riskEta,
            RecalculatedExtraDays: extraDays,
            AppliedMarginPercent: marginPercent);
    }

    private static PurchaseLtRule? ResolvePurchaseLtRule(
        IReadOnlyList<PurchaseLtRule> rules, string warehouseCode, int materialId)
    {
        if (rules is null || rules.Count == 0) return null;
        var matId = materialId.ToString();

        // 优先 Material 级精确匹配
        var materialRule = rules.FirstOrDefault(r =>
            string.Equals(r.WarehouseCode, warehouseCode, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.MaterialId, matId, StringComparison.OrdinalIgnoreCase));
        if (materialRule != null) return materialRule;

        // 回退 Warehouse 级默认（MaterialId 为空）
        return rules.FirstOrDefault(r =>
            string.Equals(r.WarehouseCode, warehouseCode, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(r.MaterialId));
    }

    private static double ResolveArrivalOffset(IReadOnlyList<WarehouseOffsetRule> rules, string warehouseCode)
    {
        if (rules is null) return 0;
        var rule = rules.FirstOrDefault(r =>
            string.Equals(r.WarehouseCode, warehouseCode, StringComparison.OrdinalIgnoreCase));
        return rule?.OffsetHours ?? 0;
    }
}

/// <summary>逾期重估的处置结果（决定装载侧要不要打 WARN、要不要计入「无合法可用时点」）。</summary>
public enum OverdueEtaOutcome
{
    /// <summary>未逾期，或逾期但无需重估（不在途），原值即用。</summary>
    None,

    /// <summary>在途逾期，已按 <c>R + ceil(L × p)</c> 重估 —— 装载侧**必须**留 WARN。</summary>
    Recalculated,

    /// <summary>在途逾期但无合法同仓 DefaultLT ⇒ 不给日期。（<see cref="EtaDiagnostics.AvailableAt"/> 为 null）</summary>
    NoLegalLeadTime,

    /// <summary>逾期但该供给已不在途（已到厂／在场寄售等），按原 ETA 算，不重估。</summary>
    NotInTransitKeptOriginal
}

/// <summary>无可用时点的成因（只用于留痕与找谁补数据，不参与业务判定）。</summary>
public enum EtaUnavailableReason
{
    /// <summary>无成因（有可用时点）。</summary>
    None,

    /// <summary>Manual／ERP／DefaultLT 三级链全空。找 PMC（人工）／ODS 源（ERP）／3号位（LT 规则）。</summary>
    NoEtaSource,

    /// <summary>在途逾期且缺合法同仓 DefaultLT。找 3号位 补 `DefaultPurchaseLt`。</summary>
    OverdueNoLegalLeadTime
}

/// <summary>三级链的原始命中情况（与成因解耦，便于日志一次看全）。</summary>
/// <param name="HasManualEta">Manual ETA 覆盖命中（键 = PO+行号+物料+收货仓）。</param>
/// <param name="HasErpEta">ERP 侧给出 ETA（`RawProcurementFact.Eta`）。</param>
/// <param name="HasPurchaseLtRule">命中一条 `DefaultPurchaseLt` 规则（物料级精确 或 仓库级默认）。</param>
/// <param name="HasReleaseDate">有下单/发行日期（`ReleaseDate + DefaultLT` 推算的必要条件）。</param>
/// <param name="LegalLeadTimeDays">命中的合法同仓 DefaultLT 天数（未命中为 null）。</param>
/// <param name="AppliedOffsetHours">实际采用的仓级 Arrival-to-Usable 偏移小时（只加一次）。</param>
public sealed record EtaChain(
    bool HasManualEta,
    bool HasErpEta,
    bool HasPurchaseLtRule,
    bool HasReleaseDate,
    double? LegalLeadTimeDays,
    double AppliedOffsetHours);

/// <summary>
/// 单条采购事实的 ETA 解析留痕（<see cref="AvailableTimeCalculator.Explain"/> 的返回）。
///
/// 【为什么需要】三级链（Manual &gt; ERP &gt; ReleaseDate+DefaultLT）任一为空都会让 `AvailableAt` 变 null，
///   但「哪一级空、为什么空」直接决定该找谁补数据：Manual 空找 PMC、ERP 空找 ODS 源、
///   LT 规则没命中找参数治理（3号位）。只记「null」等于把三种完全不同的缺源混成一类。
///
/// 【口径提醒】`AvailableAt == null` 的**两种成因必须分开读**：
///   - <see cref="EtaUnavailableReason.NoEtaSource"/>：现行口径下仍会以 null 入池并被扣减，等价「立即可用」
///     （见 0号位《03_R04 2/5号位回执交叉核证》§三-1）；
///   - <see cref="EtaUnavailableReason.OverdueNoLegalLeadTime"/>：0号位 2026-10-25 §2 明确**不得**视作
///     Run 立即可用 —— 装载侧应据此拒绝入池，而不是当 0 天处理。
/// 本记录只做留痕，不改变可承接性；是否拒绝入池由装载侧按上句处置。
/// </summary>
/// <param name="AvailableAt">最终可用时点（null = 无合法可用时点，成因见 <see cref="UnavailableReason"/>）。</param>
/// <param name="Chain">三级链原始命中情况。</param>
/// <param name="Source">来源优先级链选中的那一级（None = 无源）。</param>
/// <param name="SelectedEta">重估**前**的选中 ETA（WARN 需带「原日期」）。</param>
/// <param name="Overdue">逾期处置结果。</param>
/// <param name="RiskEta">重估后的风险 ETA（<c>ETA*</c>，不含 Offset）。仅 <see cref="OverdueEtaOutcome.Recalculated"/> 有值。</param>
/// <param name="RecalculatedExtraDays">重估加的天数 <c>ceil(L × p)</c>。</param>
/// <param name="AppliedMarginPercent">本 Run 冻结的 <c>OverdueMargin.MarginPercent</c>。</param>
/// <param name="UnavailableReason">无可用时点的成因。</param>
public sealed record EtaDiagnostics(
    DateTime? AvailableAt,
    EtaChain Chain,
    EtaSource Source = EtaSource.None,
    DateTime? SelectedEta = null,
    OverdueEtaOutcome Overdue = OverdueEtaOutcome.None,
    DateTime? RiskEta = null,
    int? RecalculatedExtraDays = null,
    decimal? AppliedMarginPercent = null,
    EtaUnavailableReason UnavailableReason = EtaUnavailableReason.None)
{
    public bool HasManualEta => Chain.HasManualEta;
    public bool HasErpEta => Chain.HasErpEta;
    public bool HasPurchaseLtRule => Chain.HasPurchaseLtRule;
    public bool HasReleaseDate => Chain.HasReleaseDate;
}
