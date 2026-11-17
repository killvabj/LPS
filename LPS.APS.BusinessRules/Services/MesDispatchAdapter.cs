using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LPS.APS.BusinessRules.Models;
using LPS.APS.Core.Constants;
using LPS.APS.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LPS.APS.BusinessRules.Services;

// ============================================================================
// MES 建单统一出站 Adapter（5号位，2026-11-12 落码）
//
// 分工（Owner 2026-11-11 裁定、11-12 按 11-09 FULL-07 定稿契约勘正）：
//   · 本类 = **统一出站口**：组装（ASSY→MES 组装模块）与加工（MACH 族→MES 加工模块）
//     都从这一个 CreateWorkOrderAsync 出去；两类工单号均由 MES 内部生成。
//   · 本类 **无状态、不落库**：TaskDispatch 的抢占与整组回写归 2号位 消费环
//     （Engine/Services/Sync/MesDispatchDrainService），此处不碰他人表。
//   · 三态红线：只有 MES **确定拒绝**才返回 Rejected；超时/断连/5xx/未配置一律 Unknown
//     （保持可重试）。**禁止把传输层故障抛成异常**——异常会打断消费环整组写成。
//
// URL 预留（配置键；Web 宿主 appsettings 或环境变量均可，环境变量用双下划线分层）：
//   MesDispatch:Assembly:BaseUrl    组装模块建单接口地址（未配置=不外呼→Unknown）
//   MesDispatch:Machining:BaseUrl   加工模块建单接口地址
//   MesDispatch:TimeoutSeconds      单次外呼超时，默认 30
//   MesDispatch:ApiKey              可选，非空时带 X-Api-Key 头
//   MesDispatch:StageOverrides:{STAGE_CODE} = ASSEMBLY|MACHINING   逐 Stage 覆盖内置路由表
//   MesDispatch:ErrorCodes:{MES码}          = 九码                 拒绝码映射表扩充
//
// 内置路由判据（**2026-11-12 Owner 口径确认、2号位 11-12 回执 §1.2/问1 落地**：
//   加工 MES 只取 StageCode 末段＝MACH；组装＝ASSY；余者归哪条 MES 链路「回头再研究」，
//   2号位 WorkOrderType 列同口径回填：_MACH→MACHINING 22,341／_ASSY→ASSEMBLY 153／NULL 66,767）：
//   ASSY→ASSEMBLY；MACH→MACHINING；
//   FORGE/CAST/SURF/MOLD/型材(EXTRU/DRAW)/FINAL/OUTS 等未收录类别 → **不猜、不外呼**：
//   判 Unknown(ROUTE_UNRESOLVED) 保持 PENDING 不落 FAILED（与 NOT_CONFIGURED 同构：
//   补 MesDispatch:StageOverrides 配置后下一轮自然放行；若落 FAILED 则把「待裁定」误成「定死」）。
//   注：11-05 方案里 `MACH/FORGE/CAST/SURF/MOLD=加工` 的旧判据已由 Owner 11-12 口径作废。
//
// ── v1.1 增量（2026-11-14/16，对应 MES 组装端口定稿；知会2号位函待发）────────────
//   ① 出网改**数组**（单元素）：MES 端口=数组进数组出、逐条独立成败；消费环仍逐组调，
//      本类包 `[payload]`，应答按 `referenceNo` 回显还原（将来跨组打包只扩元素、不改结构）。
//   ② 应答解析适配 ABP 信封：`{"result":[…]}`／裸数组／1.0 单对象**三形兼容**；
//      2xx+`{"error":{…}}` 异常信封＝无确认证据→Unknown 重试（防把"服务端暂错"误判 Rejected 打死）。
//   ③ 路由消费序：`command.WorkOrderType` 显式列 > `StageOverrides` 配置 > 内置收窄表 >
//      Unknown(ROUTE_UNRESOLVED)（11-12 晚复函 §一 第1条）。
//   ④ `instructionType` 消费 `command.InstructionType`（空则沿用 V1 恒 PRODUCTION_INSTRUCTION 兜底）。
//   ⑤ Payload 三新字段先占位省略：`productionDepartmentCode`（等2号位 Command 补列）、
//      `routeCode`（等 Task.RouteCode 真值链，现全表 'DEFAULT'）、`instructionNo`（出荷预留，V1 不产）。
// ============================================================================

/// <summary>MES 建单统一出站 Adapter：按 Stage 路由组装/加工模块接口，三态返回，不落库。</summary>
public class MesDispatchAdapter : IMesDispatchAdapter
{
    /// <summary>报文契约版本（1.1：数组形态＋WorkOrderType/instructionType 消费＋三新字段占位）。</summary>
    public const string ContractVersion = "1.1";

    private const string InstructionTypeProduction = "PRODUCTION_INSTRUCTION";
    private const string ModuleAssembly = "ASSEMBLY";
    private const string ModuleMachining = "MACHINING";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 内置 Stage 类别→模块路由表（后缀匹配；配置 StageOverrides 可逐码覆盖）。
    /// <para>11-12 收窄（Owner 口径经 2号位 回执 §1.2 确认）：加工 MES 末段只认 <c>MACH</c>，
    /// 组装只认 <c>ASSY</c>。<c>FORGE/CAST/SURF/MOLD/型材</c> 归哪条 MES 链路"回头再研究"
    /// （2号位 亦将此类行 WorkOrderType 留 NULL），故**不内置**——未收录即判 Unknown 不外呼，
    /// 待人工裁定后经 StageOverrides 精确归机，禁止在代码里替未决问题猜归属。</para>
    /// </summary>
    private static readonly Dictionary<string, string> BuiltinCategoryRoute = new(StringComparer.Ordinal)
    {
        ["ASSY"] = ModuleAssembly,
        ["MACH"] = ModuleMachining,
    };

    /// <summary>MES 拒绝码→九码内置同义表（配置 MesDispatch:ErrorCodes 可扩充；未命中一律 MES_REJECT_UNMAPPED）。</summary>
    private static readonly Dictionary<string, string> BuiltinRejectMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["INVALID_PI"] = TaskDispatchFailureCode.InvalidPi,
        ["PI_NOT_FOUND"] = TaskDispatchFailureCode.InvalidPi,
        ["PI_CANCELLED"] = TaskDispatchFailureCode.InvalidPi,
        ["QTY_MISMATCH"] = TaskDispatchFailureCode.QtyMismatch,
        ["QTY_EXCEED"] = TaskDispatchFailureCode.QtyMismatch,
        ["OVER_REMAIN"] = TaskDispatchFailureCode.QtyMismatch,
        ["ROUTING_NOT_FOUND"] = TaskDispatchFailureCode.RoutingNotFound,
        ["MATERIAL_NOT_FOUND"] = TaskDispatchFailureCode.MaterialNotFound,
        ["RESOURCE_NOT_FOUND"] = TaskDispatchFailureCode.ResourceNotFound,
        ["STATE_CONFLICT"] = TaskDispatchFailureCode.StateConflict,
        ["DUP_REFERENCE_CONFLICT"] = TaskDispatchFailureCode.DupReferenceConflict,
    };

    private static readonly HttpClient SharedHttp = new();

    private readonly IConfiguration _config;
    private readonly ILogger<MesDispatchAdapter>? _logger;

    public MesDispatchAdapter(IConfiguration config, ILogger<MesDispatchAdapter>? logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<MesDispatchResult> CreateWorkOrderAsync(MesDispatchCommand command, CancellationToken ct)
    {
        // 1) 路由：显式列 > 配置覆盖 > 内置表；查不到不猜（11-12 晚复函消费序）。
        var module = ResolveModule(command);
        if (module is null)
            // 路由未收录＝我方配置缺口（非 MES 确定拒绝、非传输故障），与 NOT_CONFIGURED 同构：
            // 判 Unknown 保持 PENDING、**不落 FAILED**，补 StageOverrides 后下一轮自然放行。
            // （Owner 11-12：FORGE/CAST/MOLD/型材等待裁定，若落 FAILED 则把"待研究"误成"定死"。）
            return RouteUnresolved(command.StageCode);

        var baseUrlKey = module == ModuleAssembly
            ? "MesDispatch:Assembly:BaseUrl"
            : "MesDispatch:Machining:BaseUrl";
        var baseUrl = _config[baseUrlKey];
        if (string.IsNullOrWhiteSpace(baseUrl))
            // 未配置 = NOT_CONFIGURED：不外呼、不伪造、保持可重试（消费环据此整组回置 PENDING）。
            return Unknown($"NOT_CONFIGURED:MES {module} 建单接口地址未配置（{baseUrlKey}）");

        // 2) 组报文。SourceTaskIds 只供 2号位 消费环回写映射，不出网。
        //    v1.1：MES 端口数组进数组出——本类单组单呼，包为单元素数组；应答按 referenceNo 还原。
        var request = BuildRequest(command, module);
        var json = JsonSerializer.Serialize(new[] { request }, JsonOpts);

        // 3) 外呼：传输层故障一律归 Unknown；仅确定 HTTP 应答进入三态判定。
        var timeoutSeconds = _config.GetValue<int?>("MesDispatch:TimeoutSeconds") is > 0 and var t ? t : 30;
        HttpResponseMessage response;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            response = await SendAsync(baseUrl, json, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 本地超时=结果未知，绝不判失败（可能已建单，重发由参考号幂等兜住）。
            _logger?.LogWarning("MES 建单外呼超时({Sec}s) ref={RefNo}", timeoutSeconds, command.ReferenceNo);
            return Unknown($"MES_TRANSPORT:TIMEOUT_{timeoutSeconds}s");
        }
        catch (OperationCanceledException)
        {
            throw; // 调用方（消费环）主动取消：原样上抛，由其决定回置语义
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "MES 建单外呼失败 ref={RefNo}", command.ReferenceNo);
            return Unknown($"MES_TRANSPORT:{ex.GetType().Name}:{Truncate(ex.Message, 500)}");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.RequestTimeout
                or (HttpStatusCode)429
                or >= HttpStatusCode.InternalServerError)
                // 408/429/5xx = 结果未知，保持重试，不落 FAILED（九码词表硬约束）。
                return Unknown($"MES_TRANSPORT:HTTP {(int)response.StatusCode}:{Truncate(body, 500)}");

            if (!response.IsSuccessStatusCode)
                // 其余 4xx（400 报文错 / 401·403 鉴权错）＝确定性失败，重试不可解：按拒绝归一、未映射兜底。
                return Rejected(MapRejectCode(null),
                    $"HTTP {(int)response.StatusCode}:{Truncate(body, 1000)}", body);

            // 4) 解析应答（v1.1：result 信封/裸数组/1.0 单对象三形兼容，按 referenceNo 还原本组）。
            return ParseAcceptedOrRejected(response.StatusCode, body, command.ReferenceNo);
        }
    }

    // ---------------------------------------------------------------- 路由/映射

    /// <summary>模块判据消费序（11-12 晚复函 §一）：`WorkOrderType` 显式列 &gt; `StageOverrides` 配置 &gt;
    /// 内置收窄表（后缀 CN6_ASSY→ASSY）。空串按 2号位 会签口径归一为"缺列"回落；非法值记警告后回落，不猜。</summary>
    private string? ResolveModule(MesDispatchCommand command)
    {
        var stageCode = command.StageCode;
        var wt = (command.WorkOrderType ?? "").Trim();
        if (wt.Length > 0)
        {
            if (string.Equals(wt, ModuleAssembly, StringComparison.OrdinalIgnoreCase)) return ModuleAssembly;
            if (string.Equals(wt, ModuleMachining, StringComparison.OrdinalIgnoreCase)) return ModuleMachining;
            _logger?.LogWarning("WorkOrderType={Value} 非 ASSEMBLY/MACHINING（ref={RefNo}），回落配置/内置表", wt, command.ReferenceNo);
        }

        var ov = _config[$"MesDispatch:StageOverrides:{stageCode}"];
        if (!string.IsNullOrWhiteSpace(ov))
        {
            var u = ov.Trim().ToUpperInvariant();
            if (u == ModuleAssembly || u == ModuleMachining) return u;
            _logger?.LogWarning("MesDispatch:StageOverrides:{Stage} 配置值 {Value} 非 ASSEMBLY/MACHINING，回落内置表", stageCode, ov);
        }

        var idx = stageCode.LastIndexOf('_');
        if (idx < 0 || idx + 1 >= stageCode.Length) return null;
        return BuiltinCategoryRoute.GetValueOrDefault(stageCode[(idx + 1)..]);
    }

    /// <summary>MES 拒绝码→九码：配置扩充优先，内置同义表其次，未命中 MES_REJECT_UNMAPPED（攒样本走归一）。</summary>
    private string MapRejectCode(string? mesCode)
    {
        if (!string.IsNullOrWhiteSpace(mesCode))
        {
            var fromConfig = _config[$"MesDispatch:ErrorCodes:{mesCode}"];
            if (!string.IsNullOrWhiteSpace(fromConfig) && TaskDispatchFailureCode.All.Contains(fromConfig))
                return fromConfig;
            if (BuiltinRejectMap.TryGetValue(mesCode, out var mapped))
                return mapped;
        }
        return TaskDispatchFailureCode.MesRejectUnmapped;
    }

    // ---------------------------------------------------------------- 报文/应答

    private static MesDispatchRequest BuildRequest(MesDispatchCommand c, string module) => new()
    {
        InterfaceVersion = ContractVersion,
        // 11-12 晚复函 §一第2条：消费 command.InstructionType（2号位 落行列）；空则沿用 V1 兜底恒生产。
        InstructionType = string.IsNullOrWhiteSpace(c.InstructionType)
            ? InstructionTypeProduction
            : c.InstructionType.Trim().ToUpperInvariant(),
        WorkOrderModule = module,
        ReferenceNo = c.ReferenceNo,
        ProductionInstructionNo = c.ProductionInstructionNo,
        StageCode = c.StageCode,
        SourcePlanVersionId = c.SourcePlanVersionId,
        MaterialCode = c.MaterialCode,
        Quantity = c.Quantity,
        Uom = c.Uom,
        FactoryCode = c.FactoryCode,
        ResourceCode = c.ResourceCode,
        // ── v1.1 占位（JsonOptions=WhenWritingNull，未供值时整字段省略，MES 侧宽松缺省）──
        // TODO(2号位会签)：MesDispatchCommand 尚无该列；须自 TaskDispatch.ProductionDepartmentCode 透传。
        // ⚠组装 MES 联调前必须补列：MES 端口缺该字段将以 DEPT_NOT_MAPPED 拒绝（Holon 唯一来源，不可猜）。
        ProductionDepartmentCode = null,
        // TODO(真值链)：Task.RouteCode 现全表 'DEFAULT'（11-05 §九 登记，1号位 回写待齐）；真值就绪后透传。
        RouteCode = null,
        // 出荷预留：SH号=Order.OrderNo（INTER_FACTORY）；V1 不产 SH Task（Owner 11-11 情形A）。
        InstructionNo = null,
        PlannedStartTime = c.PlannedStartTime.ToString("yyyy-MM-dd HH:mm:ss"),
        PlannedEndTime = c.PlannedEndTime.ToString("yyyy-MM-dd HH:mm:ss"),
        DispatchedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
    };

    private MesDispatchResult ParseAcceptedOrRejected(HttpStatusCode status, string body, string referenceNo)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(body);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            // 2xx 但报文不可解析＝无确认证据，归未知重试（防"已建未认"漂移）。
            return Unknown($"MES_TRANSPORT:应答非JSON(HTTP {(int)status}):{Truncate(body, 500)}");
        }

        // ABP 异常信封：2xx+{"error":{…}} 且无 success——MES 侧 IT 接口/DB 故障会按契约抛异常，
        // 若宿主把异常包成 200+error（部分 ABP 配置如此），必须归 Unknown 重试，不得误判 Rejected 打死整组。
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.Object
            && TryGetBool(root, "success") is null)
            return Unknown($"MES_TRANSPORT:ABP_ERROR信封:{Truncate(errEl.ToString(), 500)}");

        // 形状展开：result 信封 → 其载荷；裸数组；1.0 单对象。
        var node = root;
        if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty("result", out var inner))
            node = inner;

        if (node.ValueKind == JsonValueKind.Array)
        {
            // 数组进数组出：按 referenceNo 回显匹配本组条目；**回显存在却不匹配＝绝不采信**
            // （单元素也一样——串包比缺包更危险）。回显缺失（宽松旧形）仅单元素时可采信。
            var len = node.GetArrayLength();
            if (len == 0)
                return Unknown($"MES_TRANSPORT:应答数组为空:{Truncate(body, 500)}");
            JsonElement picked = default;
            var found = false;
            foreach (var el in node.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var refBack = (TryGetString(el, "referenceNo") ?? "").Trim();
                var ok = refBack.Length == 0
                    ? len == 1 // 无回显：只有全场唯一时才敢认
                    : string.Equals(refBack, referenceNo, StringComparison.OrdinalIgnoreCase);
                if (!ok) continue;
                picked = el;
                found = true;
                break;
            }
            if (!found)
                return Unknown($"MES_TRANSPORT:应答数组无referenceNo匹配条目(len={len}):{Truncate(body, 500)}");
            node = picked;
        }

        if (node.ValueKind != JsonValueKind.Object)
            return Unknown($"MES_TRANSPORT:应答形状不可解析(HTTP {(int)status}):{Truncate(body, 500)}");

        var success = TryGetBool(node, "success");
        var codeStr = TryGetString(node, "errorCode") ?? TryGetString(node, "code");
        var workOrderNo = FindWorkOrderNo(node);
        var message = TryGetString(node, "message") ?? TryGetString(node, "msg") ?? TryGetString(node, "errorMessage");

        if (success == true && !string.IsNullOrWhiteSpace(workOrderNo))
            // Accepted（含幂等命中返原单）：消费环据此整组落 CREATED。
            return new MesDispatchResult(MesDispatchKind.Accepted, workOrderNo, null, null, body);

        if (success == true)
            // 声称成功但无工单号＝无法确证，不伪造 Accepted。
            return Unknown($"MES_TRANSPORT:应答success但无工单号:{Truncate(body, 500)}");

        if (success == false)
            return Rejected(MapRejectCode(codeStr), $"{codeStr ?? "(无码)"}:{Truncate(message ?? string.Empty, 1000)}", body);

        // success 缺失：若携号则视为受理确证（兼容 MES 极简应答），否则未知。
        if (!string.IsNullOrWhiteSpace(workOrderNo))
            return new MesDispatchResult(MesDispatchKind.Accepted, workOrderNo, null, null, body);

        return Unknown($"MES_TRANSPORT:应答无success且无工单号(HTTP {(int)status}):{Truncate(body, 500)}");
    }

    private static string? FindWorkOrderNo(JsonElement root)
    {
        string? Probe(JsonElement el)
        {
            foreach (var name in new[] { "mesWorkOrderNo", "workOrderNo", "workNo", "orderNo" })
            {
                var v = TryGetString(el, name);
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            return null;
        }

        return Probe(root)
            ?? (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object ? Probe(data) : null);
    }

    private static string? TryGetString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in el.EnumerateObject())
        {
            if (!string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            return p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString(),
                JsonValueKind.Number => p.Value.ToString(),
                JsonValueKind.True or JsonValueKind.False => p.Value.ToString(),
                _ => null,
            };
        }
        return null;
    }

    private static bool? TryGetBool(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in el.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
                && p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return p.Value.GetBoolean();
        }
        return null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static MesDispatchResult Rejected(string code, string reason, string? rawResponse)
        => new(MesDispatchKind.Rejected, null, code, reason, rawResponse);

    private static MesDispatchResult Unknown(string reason)
        => new(MesDispatchKind.Unknown, null, TaskDispatchFailureCode.MesTransport, reason, null);

    /// <summary>路由未收录：Unknown 语义（不落 FAILED、保持 PENDING），但**不占九码**——
    /// 这是我方配置缺口而非 MES 传输故障，借用 MES_TRANSPORT 会污染 2号位失败统计。</summary>
    private static MesDispatchResult RouteUnresolved(string stageCode)
        => new(MesDispatchKind.Unknown, null, null,
            $"ROUTE_UNRESOLVED:STAGE_ROUTE_UNRESOLVED_{stageCode}（路由未收录该 Stage，未外呼；请补 MesDispatch:StageOverrides 或待 Owner 裁定后扩内置表）",
            null);

    // ---------------------------------------------------------------- 传输

    /// <summary>外呼原语：protected internal virtual 供单测替身覆写（生产路径走共享 HttpClient）。</summary>
    protected internal virtual async Task<HttpResponseMessage> SendAsync(string baseUrl, string json, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, baseUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        var apiKey = _config["MesDispatch:ApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey))
            msg.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
        return await SharedHttp.SendAsync(msg, ct).ConfigureAwait(false);
    }
}
