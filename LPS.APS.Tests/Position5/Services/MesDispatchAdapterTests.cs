using System.Net;
using System.Text;
using System.Text.Json;
using LPS.APS.BusinessRules.Services;
using LPS.APS.Core.Constants;
using LPS.APS.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LPS.APS.Tests.Position5.Services;

/// <summary>
/// MesDispatchAdapter 纯单测（无网络）：路由、报文、三态映射、NOT_CONFIGURED。
/// 传输经 StubAdapter 覆写 SendAsync 替身注入。
/// 契约：11-09 FULL-07 定稿 + 11-12 统一出站/唯一写回分工（致2号位增补函 §三）。
/// </summary>
[Collection("Position5")]
public class MesDispatchAdapterTests
{
    private sealed class StubAdapter : MesDispatchAdapter
    {
        public List<(string Url, string Json)> Calls { get; } = new();
        public Func<HttpResponseMessage>? Responder { get; set; }
        public Exception? ThrowOnSend { get; set; }

        public StubAdapter(IConfiguration config) : base(config) { }

        // CS0507 教训：跨程序集覆写 protected internal 虚方法必须收窄为 protected。
        protected override Task<HttpResponseMessage> SendAsync(string baseUrl, string json, CancellationToken ct)
        {
            Calls.Add((baseUrl, json));
            if (ThrowOnSend is not null) throw ThrowOnSend;
            return Task.FromResult(Responder?.Invoke()
                ?? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    // v1.1 缺省应答：裸数组单元素、无 referenceNo 回显（宽松旧形兼容面）。
                    Content = new StringContent("""[{"success":true,"mesWorkOrderNo":"WO-TEST"}]""", Encoding.UTF8, "application/json"),
                });
        }
    }

    private static IConfiguration Cfg(params (string Key, string Value)[] pairs)
    {
        var dict = new Dictionary<string, string?>
        {
            ["MesDispatch:Assembly:BaseUrl"] = "http://mes.test/assembly/create",
            ["MesDispatch:Machining:BaseUrl"] = "http://mes.test/machining/create",
        };
        foreach (var (k, v) in pairs) dict[k] = v;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static MesDispatchCommand Cmd(string stage = "CN_ASSY", decimal? qty = null,
        string? workOrderType = null, string? instructionType = null, string? dept = null) => new(
        ReferenceNo: MesDispatchReference.Compute("PI8899", stage, 999423),
        ProductionInstructionNo: "PI8899",
        StageCode: stage,
        SourcePlanVersionId: 999423,
        MaterialCode: "M100001",
        Quantity: qty ?? 70m,
        Uom: "PS",
        FactoryCode: "CN",
        ResourceCode: "EQ-A12",
        PlannedStartTime: new DateTime(2026, 11, 13, 8, 0, 0),
        PlannedEndTime: new DateTime(2026, 11, 13, 10, 30, 0),
        SourceTaskIds: new long[] { 11, 12 },
        InstructionType: instructionType,
        WorkOrderType: workOrderType,
        ProductionDepartmentCode: dept);

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // ---------------------------------------------------------------- 配置/路由

    [Fact]
    public async Task 未配置端点_不外呼_返回Unknown_NOT_CONFIGURED()
    {
        var stub = new StubAdapter(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MesDispatch:Assembly:BaseUrl"] = "" })
            .Build());
        var r = await stub.CreateWorkOrderAsync(Cmd(), CancellationToken.None);

        {
            Assert.Equal(MesDispatchKind.Unknown, r.Kind);
            Assert.Contains("NOT_CONFIGURED", r.FailureReason);
            Assert.Empty(stub.Calls); // 未配置严禁外呼
        }
    }

    [Fact]
    public async Task CN_ASSY_路由组装端点_且报文字段齐备可识别()
    {
        var stub = new StubAdapter(Cfg());
        await stub.CreateWorkOrderAsync(Cmd(), CancellationToken.None);

        Assert.Equal(1, stub.Calls.Count);
        Assert.Equal("http://mes.test/assembly/create", stub.Calls[0].Url);

        using var doc = JsonDocument.Parse(stub.Calls[0].Json);
        var root = doc.RootElement;
        {
            Assert.Equal(JsonValueKind.Array, root.ValueKind); // v1.1：单组单呼包为单元素数组（MES 端口数组进数组出）
            Assert.Equal(1, root.GetArrayLength());
        }
        var p = root[0];
        {
            Assert.Equal("1.1", p.GetProperty("interfaceVersion").GetString());
            Assert.Equal("PRODUCTION_INSTRUCTION", p.GetProperty("instructionType").GetString()); // Command 无类型时 V1 兜底
            Assert.Equal("ASSEMBLY", p.GetProperty("workOrderModule").GetString());
            Assert.Equal("PI8899|CN_ASSY|999423", p.GetProperty("referenceNo").GetString()); // 参考号必须透传 Compute 组合键
            Assert.Equal("PI8899", p.GetProperty("productionInstructionNo").GetString());
            Assert.Equal("CN_ASSY", p.GetProperty("stageCode").GetString());
            Assert.Equal(999423, p.GetProperty("sourcePlanVersionId").GetInt32());
            Assert.Equal("M100001", p.GetProperty("materialCode").GetString());
            Assert.Equal(70m, p.GetProperty("quantity").GetDecimal()); // 组合定容量原样透传
            Assert.Equal("PS", p.GetProperty("uom").GetString());
            Assert.Equal("CN", p.GetProperty("factoryCode").GetString());
            Assert.Equal("EQ-A12", p.GetProperty("resourceCode").GetString());
            Assert.Equal("2026-11-13 08:00:00", p.GetProperty("plannedStartTime").GetString()); // naive 墙钟无时区后缀
            Assert.Equal("2026-11-13 10:30:00", p.GetProperty("plannedEndTime").GetString());
            Assert.False(p.TryGetProperty("sourceTaskIds", out _)); // APS 内部行键不出网
            Assert.False(p.TryGetProperty("productionDepartmentCode", out _)); // 第三列未供值（null）即省略——2号位 r14248 口径空串归一 NULL 同归此路
        }
    }

    [Fact]
    public async Task 第三列有值即出网_productionDepartmentCode透传()
    {
        var stub = new StubAdapter(Cfg());
        await stub.CreateWorkOrderAsync(Cmd(dept: "CN_ASSY_制造3-2课761HL"), CancellationToken.None);

        using var doc = JsonDocument.Parse(stub.Calls[0].Json);
        Assert.Equal("CN_ASSY_制造3-2课761HL", doc.RootElement[0].GetProperty("productionDepartmentCode").GetString()); // MES 端 Holon 唯一来源，原样透传不加工
    }

    [Fact]
    public async Task 第三列空白串_归一省略不下发()
    {
        var stub = new StubAdapter(Cfg());
        await stub.CreateWorkOrderAsync(Cmd(dept: "   "), CancellationToken.None);

        using var doc = JsonDocument.Parse(stub.Calls[0].Json);
        Assert.False(doc.RootElement[0].TryGetProperty("productionDepartmentCode", out _)); // 空串按2号位归一口径视同缺列，省略出网
    }

    // 11-12 收窄：内置表只认末段 MACH/ASSY（Owner 口径"加工 MES 只取 _MACH"，经 2号位 回执 §1.2 确认）。
    // 11-17 xunit 迁移注：原 NUnit 特性序为 [TestCase]×3 在前、[Test] 在后，转换后统一挂 [Theory]。
    [Theory]
    [InlineData("CN_MACH", "http://mes.test/machining/create", "MACHINING")]
    [InlineData("TJ_MACH", "http://mes.test/machining/create", "MACHINING")]
    [InlineData("CN6_ASSY", "http://mes.test/assembly/create", "ASSEMBLY")]
    public async Task 内置路由表_按Stage后缀类别分模块(string stage, string expectUrl, string expectModule)
    {
        var stub = new StubAdapter(Cfg());
        await stub.CreateWorkOrderAsync(Cmd(stage), CancellationToken.None);

        {
            Assert.Equal(expectUrl, stub.Calls[0].Url);
            using var doc = JsonDocument.Parse(stub.Calls[0].Json);
            Assert.Equal(expectModule, doc.RootElement[0].GetProperty("workOrderModule").GetString());
        }
    }

    [Fact]
    public async Task 未收录类别_不猜测_判Unknown保持PENDING_不外呼不落FAILED()
    {
        // 11-12 语义变更：FORGE/CAST/MOLD/型材归哪条 MES 链路"回头再研究"（2号位 WorkOrderType 亦留 NULL）。
        // 路由未收录 = 我方配置缺口，非 MES 确定拒绝 ⇒ Unknown(保持 PENDING)，补 StageOverrides 后下一轮自然放行。
        // 若判 Rejected 落 FAILED，会把"待裁定"误成"定死"，人工得重开整组。
        var stub = new StubAdapter(Cfg());
        var r = await stub.CreateWorkOrderAsync(Cmd("BJ_EXTRU"), CancellationToken.None);

        {
            Assert.Equal(MesDispatchKind.Unknown, r.Kind); // 未收录=配置缺口，不落 FAILED
            Assert.Null(r.FailureCode); // 配置缺口不借九码，防污染 MES_TRANSPORT 统计
            Assert.Contains("ROUTE_UNRESOLVED", r.FailureReason);
            Assert.Contains("BJ_EXTRU", r.FailureReason);
            Assert.Empty(stub.Calls); // 不猜就不外呼
        }
    }

    [Fact]
    public async Task FORGE_CAST_SURF_MOLD_未内置_一律Unknown不外呼()
    {
        // 11-12 收窄后这四个后缀退出内置表；各造一例证明"不猜"仍成立。
        var stub = new StubAdapter(Cfg());
        foreach (var stage in new[] { "CN_FORGE", "TJ_CAST", "CN_SURF", "BJ_MOLD" })
        {
            var r = await stub.CreateWorkOrderAsync(Cmd(stage), CancellationToken.None);
            Assert.Equal(MesDispatchKind.Unknown, r.Kind);
        }
        Assert.Empty(stub.Calls); // 四类均未外呼
    }

    [Fact]
    public async Task StageOverrides配置_可把待裁定类别人工归机()
    {
        // 人工裁定后（如型材归加工）经配置精确放行，无需改代码；未配置前保持 Unknown 攒着。
        var stub = new StubAdapter(Cfg(("MesDispatch:StageOverrides:BJ_EXTRU", "MACHINING")));
        var r = await stub.CreateWorkOrderAsync(Cmd("BJ_EXTRU"), CancellationToken.None);

        {
            Assert.Equal("http://mes.test/machining/create", stub.Calls[0].Url);
            using var doc = JsonDocument.Parse(stub.Calls[0].Json);
            Assert.Equal("MACHINING", doc.RootElement[0].GetProperty("workOrderModule").GetString());
            Assert.Equal(MesDispatchKind.Accepted, r.Kind);
        }
    }

    // ---------------------------------------------------------------- v1.1 消费序/透传

    [Fact]
    public async Task WorkOrderType显式列优先于内置表()
    {
        // 11-12 晚复函 §一：消费序=显式列>StageOverrides>内置。CN6_ASSY 内置本判组装，显式 MACHINING 说了算。
        var stub = new StubAdapter(Cfg());
        await stub.CreateWorkOrderAsync(Cmd("CN6_ASSY", workOrderType: "MACHINING"), CancellationToken.None);

        Assert.Equal("http://mes.test/machining/create", stub.Calls[0].Url);
    }

    [Fact]
    public async Task WorkOrderType非法值_记警回落不采信()
    {
        // 显式列在场但值域外：不猜、回落配置/内置；BJ_FORGE 内置未收录 ⇒ Unknown 不外呼。
        var stub = new StubAdapter(Cfg());
        var r = await stub.CreateWorkOrderAsync(Cmd("BJ_FORGE", workOrderType: "ASSEMBLY_MES"), CancellationToken.None);

        {
            Assert.Equal(MesDispatchKind.Unknown, r.Kind);
            Assert.Contains("ROUTE_UNRESOLVED", r.FailureReason);
            Assert.Empty(stub.Calls);
        }
    }

    [Fact]
    public async Task InstructionType有值即透传_不再恒V1兜底()
    {
        var stub = new StubAdapter(Cfg());
        await stub.CreateWorkOrderAsync(Cmd(instructionType: "SHIPPING_INSTRUCTION"), CancellationToken.None);

        using var doc = JsonDocument.Parse(stub.Calls[0].Json);
        Assert.Equal("SHIPPING_INSTRUCTION", doc.RootElement[0].GetProperty("instructionType").GetString()); // V1 现况无 SH Task，本例证明透传通道就绪（Owner 情形B 启动即可用）
    }

    // ---------------------------------------------------------------- v1.1 应答三形

    [Fact]
    public async Task result信封数组_取本组条目受理()
    {
        var stub = new StubAdapter(Cfg())
        {
            Responder = () => Json(HttpStatusCode.OK,
                """{"result":[{"success":true,"mesWorkOrderNo":"WO-88001","referenceNo":"PI8899|CN_ASSY|999423"}]}"""),
        };
        var r = await stub.CreateWorkOrderAsync(Cmd(), CancellationToken.None);

        {
            Assert.Equal(MesDispatchKind.Accepted, r.Kind);
            Assert.Equal("WO-88001", r.MesWorkOrderNo);
        }
    }

    [Fact]
    public async Task 多条目应答_按referenceNo匹配_他组定罪不殃本组()
    {
        // 将来跨组打包的解析自证：别的组被拒不能替本组定罪。
        var stub = new StubAdapter(Cfg())
        {
            Responder = () => Json(HttpStatusCode.OK,
                """{"result":[{"success":false,"errorCode":"STATE_CONFLICT","referenceNo":"PI0001|CN_ASSY|999423","message":"别组已存在"},{"success":true,"mesWorkOrderNo":"WO-99","referenceNo":"PI8899|CN_ASSY|999423"}]}"""),
        };
        var r = await stub.CreateWorkOrderAsync(Cmd(), CancellationToken.None);

        {
            Assert.Equal(MesDispatchKind.Accepted, r.Kind);
            Assert.Equal("WO-99", r.MesWorkOrderNo);
        }
    }

    [Fact]
    public async Task 单元素但referenceNo不匹配_绝不采信归Unknown()
    {
        // 串包比缺包更危险：唯一条目回显了别人的号 ⇒ 无确证，重试兜住。
        var stub = new StubAdapter(Cfg())
        {
            Responder = () => Json(HttpStatusCode.OK,
                """[{"success":true,"mesWorkOrderNo":"WO-WRONG","referenceNo":"PIOTHER|CN_ASSY|999423"}]"""),
        };
        var r = await stub.CreateWorkOrderAsync(Cmd(), CancellationToken.None);

        {
            Assert.Equal(MesDispatchKind.Unknown, r.Kind);
            Assert.Contains("referenceNo", r.FailureReason);
        }
    }

    [Fact]
    public async Task 旧10单对象应答_兼容期内仍受理()
    {
        var stub = new StubAdapter(Cfg())
        {
            Responder = () => Json(HttpStatusCode.OK, """{"success":true,"mesWorkOrderNo":"WO-OLD"}"""),
        };
        var r = await stub.CreateWorkOrderAsync(Cmd(), CancellationToken.None);

        {
            Assert.Equal(MesDispatchKind.Accepted, r.Kind);
            Assert.Equal("WO-OLD", r.MesWorkOrderNo);
        }
    }

    [Fact]
    public async Task ABP异常信封200加error_判Unknown不打死()
    {
        // MES 契约：IT接口/DB故障=抛异常（5xx=Unknown）。若宿主包成 200+{"error":…}，
        // 误判 Rejected 会把"临时故障"打成 FAILED 人工台账——本例钉死归 Unknown。
        var stub = new StubAdapter(Cfg())
        {
            Responder = () => Json(HttpStatusCode.OK, """{"error":{"code":500,"message":"IT生管指示接口不可用"}}"""),
        };
        var r = await stub.CreateWorkOrderAsync(Cmd(), CancellationToken.None);

        {
            Assert.Equal(MesDispatchKind.Unknown, r.Kind);
            Assert.Contains("ABP_ERROR", r.FailureReason);
        }
    }

    // ---------------------------------------------------------------- 三态映射

    [Fact]
    public async Task 受理含号_Accepted_并透传工单号()
    {
        var stub = new StubAdapter(Cfg())
        {
            Responder = () => Json(HttpStatusCode.OK, """{"success":true,"data":{"mesWorkOrderNo":"WO-77001"}}"""),
        };
        var r = await stub.CreateWorkOrderAsync(Cmd(), CancellationToken.None);

        {
            Assert.Equal(MesDispatchKind.Accepted, r.Kind);
            Assert.Equal("WO-77001", r.MesWorkOrderNo);
        }
    }

    [Fact]
    public async Task success但无号_不伪造受理_归Unknown()
    {
        var stub = new StubAdapter(Cfg())
        {
            Responder = () => Json(HttpStatusCode.OK, """{"success":true}"""),
        };
        var r = await stub.CreateWorkOrderAsync(Cmd(), CancellationToken.None);

        Assert.Equal(MesDispatchKind.Unknown, r.Kind);
    }

    [Fact]
    public async Task 确定拒绝_同义码映射九码_并留原文()
    {
        var stub = new StubAdapter(Cfg())
        {
            Responder = () => Json(HttpStatusCode.OK, """{"success":false,"errorCode":"QTY_EXCEED","message":"超可开量，当前可开30"}"""),
        };
        var r = await stub.CreateWorkOrderAsync(Cmd(), CancellationToken.None);

        {
            Assert.Equal(MesDispatchKind.Rejected, r.Kind);
            Assert.Equal(TaskDispatchFailureCode.QtyMismatch, r.FailureCode);
            Assert.Contains("当前可开30", r.FailureReason);
            Assert.True(TaskDispatchFailureCode.IsDeterministicRejection(r.FailureCode));
        }
    }

    [Fact]
    public async Task 未知拒绝码_落MES_REJECT_UNMAPPED攒样本()
    {
        var stub = new StubAdapter(Cfg())
        {
            Responder = () => Json(HttpStatusCode.OK, """{"success":false,"errorCode":"WEIRD_9527","message":"……"}"""),
        };
        var r = await stub.CreateWorkOrderAsync(Cmd(), CancellationToken.None);

        {
            Assert.Equal(TaskDispatchFailureCode.MesRejectUnmapped, r.FailureCode);
            Assert.Contains("WEIRD_9527", r.FailureReason);
        }
    }

    [Fact]
    public async Task 配置ErrorCodes可扩充映射_优先于内置()
    {
        var stub = new StubAdapter(Cfg(("MesDispatch:ErrorCodes:WEIRD_9527", "INVALID_PI")))
        {
            Responder = () => Json(HttpStatusCode.OK, """{"success":false,"errorCode":"WEIRD_9527","message":"指示号无效"}"""),
        };
        var r = await stub.CreateWorkOrderAsync(Cmd(), CancellationToken.None);

        Assert.Equal(TaskDispatchFailureCode.InvalidPi, r.FailureCode);
    }

    [Theory]
    [MemberData(nameof(TransportFailureCases))]
    public async Task 传输类故障_一律Unknown不落FAILED(Func<HttpResponseMessage> responder, Exception? thrower, string expectReasonPart)
    {
        var stub = new StubAdapter(Cfg()) { Responder = responder, ThrowOnSend = thrower };
        var r = await stub.CreateWorkOrderAsync(Cmd(), CancellationToken.None);

        {
            Assert.Equal(MesDispatchKind.Unknown, r.Kind);
            Assert.Equal(TaskDispatchFailureCode.MesTransport, r.FailureCode);
            Assert.Contains(expectReasonPart, r.FailureReason);
        }
    }

    // 11-17 xunit：MemberData 数据源=object[]（原 NUnit TestCaseData 系）。
    public static IEnumerable<object[]> TransportFailureCases =>
        new List<object[]>
    {
        new object[] { (Func<HttpResponseMessage>)(() => Json(HttpStatusCode.InternalServerError, "boom")), null, "HTTP 500" },
        new object[] { (Func<HttpResponseMessage>)(() => Json((HttpStatusCode)429, "busy")), null, "HTTP 429" },
        new object[] { (Func<HttpResponseMessage>?)null, new TaskCanceledException("timeout"), "TIMEOUT" },
        new object[] { (Func<HttpResponseMessage>?)null, new HttpRequestException("connection refused"), "MES_TRANSPORT" },
    };

    [Fact]
    public async Task 鉴权类4xx_判确定拒绝_重试不可解()
    {
        var stub = new StubAdapter(Cfg())
        {
            Responder = () => Json(HttpStatusCode.Unauthorized, "bad key"),
        };
        var r = await stub.CreateWorkOrderAsync(Cmd(), CancellationToken.None);

        {
            Assert.Equal(MesDispatchKind.Rejected, r.Kind);
            Assert.Equal(TaskDispatchFailureCode.MesRejectUnmapped, r.FailureCode);
            Assert.Contains("401", r.FailureReason);
        }
    }

    [Fact]
    public async Task 调用方主动取消_原样上抛_不吞()
    {
        var stub = new StubAdapter(Cfg()) { ThrowOnSend = new OperationCanceledException() };
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync(); // ct 已取消 ⇒ when(!ct.IsCancellationRequested) 不成立 ⇒ 上抛

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await stub.CreateWorkOrderAsync(Cmd(), cts.Token));
    }

    [Fact]
    public void Adapter在扫描命名空间且非record_可被Scrutor注册()
    {
        // 防呆：若被挪出 Services 命名空间或改 record，2号位 消费环将注入不到（NOT_CONFIGURED 空转）。
        {
            Assert.Equal("LPS.APS.BusinessRules.Services", typeof(MesDispatchAdapter).Namespace);
            Assert.True(typeof(MesDispatchAdapter).IsAssignableTo(typeof(IMesDispatchAdapter)));
            Assert.Null(typeof(MesDispatchAdapter).GetProperty("EqualityContract",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
        }
    }
}
