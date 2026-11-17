using System.Net;
using System.Text;
using System.Text.Json;
using LPS.APS.BusinessRules.Services;
using LPS.APS.Core.Constants;
using LPS.APS.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace LPS.APS.BusinessRules.Tests.Services;

/// <summary>
/// MesDispatchAdapter 纯单测（无网络）：路由、报文、三态映射、NOT_CONFIGURED。
/// 传输经 StubAdapter 覆写 SendAsync 替身注入。
/// 契约：11-09 FULL-07 定稿 + 11-12 统一出站/唯一写回分工（致2号位增补函 §三）。
/// </summary>
[TestFixture]
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
        string? workOrderType = null, string? instructionType = null) => new(
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
        WorkOrderType: workOrderType);

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // ---------------------------------------------------------------- 配置/路由

    [Test]
    public async Task 未配置端点_不外呼_返回Unknown_NOT_CONFIGURED()
    {
        var stub = new StubAdapter(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MesDispatch:Assembly:BaseUrl"] = "" })
            .Build());
        var r = await stub.CreateWorkOrderAsync(Cmd(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(r.Kind, Is.EqualTo(MesDispatchKind.Unknown));
            Assert.That(r.FailureReason, Does.Contain("NOT_CONFIGURED"));
            Assert.That(stub.Calls, Is.Empty, "未配置严禁外呼");
        });
    }

    [Test]
    public async Task CN_ASSY_路由组装端点_且报文字段齐备可识别()
    {
        var stub = new StubAdapter(Cfg());
        await stub.CreateWorkOrderAsync(Cmd(), CancellationToken.None);

        Assert.That(stub.Calls, Has.Count.EqualTo(1));
        Assert.That(stub.Calls[0].Url, Is.EqualTo("http://mes.test/assembly/create"));

        using var doc = JsonDocument.Parse(stub.Calls[0].Json);
        var root = doc.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(root.ValueKind, Is.EqualTo(JsonValueKind.Array), "v1.1：单组单呼包为单元素数组（MES 端口数组进数组出）");
            Assert.That(root.GetArrayLength(), Is.EqualTo(1));
        });
        var p = root[0];
        Assert.Multiple(() =>
        {
            Assert.That(p.GetProperty("interfaceVersion").GetString(), Is.EqualTo("1.1"));
            Assert.That(p.GetProperty("instructionType").GetString(), Is.EqualTo("PRODUCTION_INSTRUCTION"), "Command 无类型时 V1 兜底");
            Assert.That(p.GetProperty("workOrderModule").GetString(), Is.EqualTo("ASSEMBLY"));
            Assert.That(p.GetProperty("referenceNo").GetString(), Is.EqualTo("PI8899|CN_ASSY|999423"), "参考号必须透传 Compute 组合键");
            Assert.That(p.GetProperty("productionInstructionNo").GetString(), Is.EqualTo("PI8899"));
            Assert.That(p.GetProperty("stageCode").GetString(), Is.EqualTo("CN_ASSY"));
            Assert.That(p.GetProperty("sourcePlanVersionId").GetInt32(), Is.EqualTo(999423));
            Assert.That(p.GetProperty("materialCode").GetString(), Is.EqualTo("M100001"));
            Assert.That(p.GetProperty("quantity").GetDecimal(), Is.EqualTo(70m), "组合定容量原样透传");
            Assert.That(p.GetProperty("uom").GetString(), Is.EqualTo("PS"));
            Assert.That(p.GetProperty("factoryCode").GetString(), Is.EqualTo("CN"));
            Assert.That(p.GetProperty("resourceCode").GetString(), Is.EqualTo("EQ-A12"));
            Assert.That(p.GetProperty("plannedStartTime").GetString(), Is.EqualTo("2026-11-13 08:00:00"), "naive 墙钟无时区后缀");
            Assert.That(p.GetProperty("plannedEndTime").GetString(), Is.EqualTo("2026-11-13 10:30:00"));
            Assert.That(p.TryGetProperty("sourceTaskIds", out _), Is.False, "APS 内部行键不出网");
            Assert.That(p.TryGetProperty("productionDepartmentCode", out _), Is.False,
                "待2号位 Command 补列——未供值即省略；组装 MES 联调前必须补，否则对端 DEPT_NOT_MAPPED 拒绝");
        });
    }

    // 11-12 收窄：内置表只认末段 MACH/ASSY（Owner 口径"加工 MES 只取 _MACH"，经 2号位 回执 §1.2 确认）。
    [TestCase("CN_MACH", "http://mes.test/machining/create", "MACHINING")]
    [TestCase("TJ_MACH", "http://mes.test/machining/create", "MACHINING")]
    [TestCase("CN6_ASSY", "http://mes.test/assembly/create", "ASSEMBLY")]
    public async Task 内置路由表_按Stage后缀类别分模块(string stage, string expectUrl, string expectModule)
    {
        var stub = new StubAdapter(Cfg());
        await stub.CreateWorkOrderAsync(Cmd(stage), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(stub.Calls[0].Url, Is.EqualTo(expectUrl));
            using var doc = JsonDocument.Parse(stub.Calls[0].Json);
            Assert.That(doc.RootElement[0].GetProperty("workOrderModule").GetString(), Is.EqualTo(expectModule));
        });
    }

    [Test]
    public async Task 未收录类别_不猜测_判Unknown保持PENDING_不外呼不落FAILED()
    {
        // 11-12 语义变更：FORGE/CAST/MOLD/型材归哪条 MES 链路"回头再研究"（2号位 WorkOrderType 亦留 NULL）。
        // 路由未收录 = 我方配置缺口，非 MES 确定拒绝 ⇒ Unknown(保持 PENDING)，补 StageOverrides 后下一轮自然放行。
        // 若判 Rejected 落 FAILED，会把"待裁定"误成"定死"，人工得重开整组。
        var stub = new StubAdapter(Cfg());
        var r = await stub.CreateWorkOrderAsync(Cmd("BJ_EXTRU"), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(r.Kind, Is.EqualTo(MesDispatchKind.Unknown), "未收录=配置缺口，不落 FAILED");
            Assert.That(r.FailureCode, Is.Null, "配置缺口不借九码，防污染 MES_TRANSPORT 统计");
            Assert.That(r.FailureReason, Does.Contain("ROUTE_UNRESOLVED"));
            Assert.That(r.FailureReason, Does.Contain("BJ_EXTRU"));
            Assert.That(stub.Calls, Is.Empty, "不猜就不外呼");
        });
    }

    [Test]
    public async Task FORGE_CAST_SURF_MOLD_未内置_一律Unknown不外呼()
    {
        // 11-12 收窄后这四个后缀退出内置表；各造一例证明"不猜"仍成立。
        var stub = new StubAdapter(Cfg());
        foreach (var stage in new[] { "CN_FORGE", "TJ_CAST", "CN_SURF", "BJ_MOLD" })
        {
            var r = await stub.CreateWorkOrderAsync(Cmd(stage), CancellationToken.None);
            Assert.That(r.Kind, Is.EqualTo(MesDispatchKind.Unknown), $"{stage} 应未命中→Unknown");
        }
        Assert.That(stub.Calls, Is.Empty, "四类均未外呼");
    }

    [Test]
    public async Task StageOverrides配置_可把待裁定类别人工归机()
    {
        // 人工裁定后（如型材归加工）经配置精确放行，无需改代码；未配置前保持 Unknown 攒着。
        var stub = new StubAdapter(Cfg(("MesDispatch:StageOverrides:BJ_EXTRU", "MACHINING")));
        var r = await stub.CreateWorkOrderAsync(Cmd("BJ_EXTRU"), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(stub.Calls[0].Url, Is.EqualTo("http://mes.test/machining/create"));
            using var doc = JsonDocument.Parse(stub.Calls[0].Json);
            Assert.That(doc.RootElement[0].GetProperty("workOrderModule").GetString(), Is.EqualTo("MACHINING"));
            Assert.That(r.Kind, Is.EqualTo(MesDispatchKind.Accepted));
        });
    }

    // ---------------------------------------------------------------- v1.1 消费序/透传

    [Test]
    public async Task WorkOrderType显式列优先于内置表()
    {
        // 11-12 晚复函 §一：消费序=显式列>StageOverrides>内置。CN6_ASSY 内置本判组装，显式 MACHINING 说了算。
        var stub = new StubAdapter(Cfg());
        await stub.CreateWorkOrderAsync(Cmd("CN6_ASSY", workOrderType: "MACHINING"), CancellationToken.None);

        Assert.That(stub.Calls[0].Url, Is.EqualTo("http://mes.test/machining/create"));
    }

    [Test]
    public async Task WorkOrderType非法值_记警回落不采信()
    {
        // 显式列在场但值域外：不猜、回落配置/内置；BJ_FORGE 内置未收录 ⇒ Unknown 不外呼。
        var stub = new StubAdapter(Cfg());
        var r = await stub.CreateWorkOrderAsync(Cmd("BJ_FORGE", workOrderType: "ASSEMBLY_MES"), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(r.Kind, Is.EqualTo(MesDispatchKind.Unknown));
            Assert.That(r.FailureReason, Does.Contain("ROUTE_UNRESOLVED"));
            Assert.That(stub.Calls, Is.Empty);
        });
    }

    [Test]
    public async Task InstructionType有值即透传_不再恒V1兜底()
    {
        var stub = new StubAdapter(Cfg());
        await stub.CreateWorkOrderAsync(Cmd(instructionType: "SHIPPING_INSTRUCTION"), CancellationToken.None);

        using var doc = JsonDocument.Parse(stub.Calls[0].Json);
        Assert.That(doc.RootElement[0].GetProperty("instructionType").GetString(), Is.EqualTo("SHIPPING_INSTRUCTION"),
            "V1 现况无 SH Task，本例证明透传通道就绪（Owner 情形B 启动即可用）");
    }

    // ---------------------------------------------------------------- v1.1 应答三形

    [Test]
    public async Task result信封数组_取本组条目受理()
    {
        var stub = new StubAdapter(Cfg())
        {
            Responder = () => Json(HttpStatusCode.OK,
                """{"result":[{"success":true,"mesWorkOrderNo":"WO-88001","referenceNo":"PI8899|CN_ASSY|999423"}]}"""),
        };
        var r = await stub.CreateWorkOrderAsync(Cmd(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(r.Kind, Is.EqualTo(MesDispatchKind.Accepted));
            Assert.That(r.MesWorkOrderNo, Is.EqualTo("WO-88001"));
        });
    }

    [Test]
    public async Task 多条目应答_按referenceNo匹配_他组定罪不殃本组()
    {
        // 将来跨组打包的解析自证：别的组被拒不能替本组定罪。
        var stub = new StubAdapter(Cfg())
        {
            Responder = () => Json(HttpStatusCode.OK,
                """{"result":[{"success":false,"errorCode":"STATE_CONFLICT","referenceNo":"PI0001|CN_ASSY|999423","message":"别组已存在"},{"success":true,"mesWorkOrderNo":"WO-99","referenceNo":"PI8899|CN_ASSY|999423"}]}"""),
        };
        var r = await stub.CreateWorkOrderAsync(Cmd(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(r.Kind, Is.EqualTo(MesDispatchKind.Accepted));
            Assert.That(r.MesWorkOrderNo, Is.EqualTo("WO-99"));
        });
    }

    [Test]
    public async Task 单元素但referenceNo不匹配_绝不采信归Unknown()
    {
        // 串包比缺包更危险：唯一条目回显了别人的号 ⇒ 无确证，重试兜住。
        var stub = new StubAdapter(Cfg())
        {
            Responder = () => Json(HttpStatusCode.OK,
                """[{"success":true,"mesWorkOrderNo":"WO-WRONG","referenceNo":"PIOTHER|CN_ASSY|999423"}]"""),
        };
        var r = await stub.CreateWorkOrderAsync(Cmd(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(r.Kind, Is.EqualTo(MesDispatchKind.Unknown));
            Assert.That(r.FailureReason, Does.Contain("referenceNo"));
        });
    }

    [Test]
    public async Task 旧10单对象应答_兼容期内仍受理()
    {
        var stub = new StubAdapter(Cfg())
        {
            Responder = () => Json(HttpStatusCode.OK, """{"success":true,"mesWorkOrderNo":"WO-OLD"}"""),
        };
        var r = await stub.CreateWorkOrderAsync(Cmd(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(r.Kind, Is.EqualTo(MesDispatchKind.Accepted));
            Assert.That(r.MesWorkOrderNo, Is.EqualTo("WO-OLD"));
        });
    }

    [Test]
    public async Task ABP异常信封200加error_判Unknown不打死()
    {
        // MES 契约：IT接口/DB故障=抛异常（5xx=Unknown）。若宿主包成 200+{"error":…}，
        // 误判 Rejected 会把"临时故障"打成 FAILED 人工台账——本例钉死归 Unknown。
        var stub = new StubAdapter(Cfg())
        {
            Responder = () => Json(HttpStatusCode.OK, """{"error":{"code":500,"message":"IT生管指示接口不可用"}}"""),
        };
        var r = await stub.CreateWorkOrderAsync(Cmd(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(r.Kind, Is.EqualTo(MesDispatchKind.Unknown));
            Assert.That(r.FailureReason, Does.Contain("ABP_ERROR"));
        });
    }

    // ---------------------------------------------------------------- 三态映射

    [Test]
    public async Task 受理含号_Accepted_并透传工单号()
    {
        var stub = new StubAdapter(Cfg())
        {
            Responder = () => Json(HttpStatusCode.OK, """{"success":true,"data":{"mesWorkOrderNo":"WO-77001"}}"""),
        };
        var r = await stub.CreateWorkOrderAsync(Cmd(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(r.Kind, Is.EqualTo(MesDispatchKind.Accepted));
            Assert.That(r.MesWorkOrderNo, Is.EqualTo("WO-77001"));
        });
    }

    [Test]
    public async Task success但无号_不伪造受理_归Unknown()
    {
        var stub = new StubAdapter(Cfg())
        {
            Responder = () => Json(HttpStatusCode.OK, """{"success":true}"""),
        };
        var r = await stub.CreateWorkOrderAsync(Cmd(), CancellationToken.None);

        Assert.That(r.Kind, Is.EqualTo(MesDispatchKind.Unknown));
    }

    [Test]
    public async Task 确定拒绝_同义码映射九码_并留原文()
    {
        var stub = new StubAdapter(Cfg())
        {
            Responder = () => Json(HttpStatusCode.OK, """{"success":false,"errorCode":"QTY_EXCEED","message":"超可开量，当前可开30"}"""),
        };
        var r = await stub.CreateWorkOrderAsync(Cmd(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(r.Kind, Is.EqualTo(MesDispatchKind.Rejected));
            Assert.That(r.FailureCode, Is.EqualTo(TaskDispatchFailureCode.QtyMismatch));
            Assert.That(r.FailureReason, Does.Contain("当前可开30"));
            Assert.That(TaskDispatchFailureCode.IsDeterministicRejection(r.FailureCode), Is.True);
        });
    }

    [Test]
    public async Task 未知拒绝码_落MES_REJECT_UNMAPPED攒样本()
    {
        var stub = new StubAdapter(Cfg())
        {
            Responder = () => Json(HttpStatusCode.OK, """{"success":false,"errorCode":"WEIRD_9527","message":"……"}"""),
        };
        var r = await stub.CreateWorkOrderAsync(Cmd(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(r.FailureCode, Is.EqualTo(TaskDispatchFailureCode.MesRejectUnmapped));
            Assert.That(r.FailureReason, Does.Contain("WEIRD_9527"));
        });
    }

    [Test]
    public async Task 配置ErrorCodes可扩充映射_优先于内置()
    {
        var stub = new StubAdapter(Cfg(("MesDispatch:ErrorCodes:WEIRD_9527", "INVALID_PI")))
        {
            Responder = () => Json(HttpStatusCode.OK, """{"success":false,"errorCode":"WEIRD_9527","message":"指示号无效"}"""),
        };
        var r = await stub.CreateWorkOrderAsync(Cmd(), CancellationToken.None);

        Assert.That(r.FailureCode, Is.EqualTo(TaskDispatchFailureCode.InvalidPi));
    }

    [TestCaseSource(nameof(TransportFailureCases))]
    public async Task 传输类故障_一律Unknown不落FAILED(Func<HttpResponseMessage> responder, Exception? thrower, string expectReasonPart)
    {
        var stub = new StubAdapter(Cfg()) { Responder = responder, ThrowOnSend = thrower };
        var r = await stub.CreateWorkOrderAsync(Cmd(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(r.Kind, Is.EqualTo(MesDispatchKind.Unknown));
            Assert.That(r.FailureCode, Is.EqualTo(TaskDispatchFailureCode.MesTransport));
            Assert.That(r.FailureReason, Does.Contain(expectReasonPart));
        });
    }

    // NUnit 4：FromArguments 已删除，直接构造 TestCaseData。
    private static readonly TestCaseData[] TransportFailureCases =
    {
        new TestCaseData(
            (Func<HttpResponseMessage>)(() => Json(HttpStatusCode.InternalServerError, "boom")),
            null,
            "HTTP 500"),
        new TestCaseData(
            (Func<HttpResponseMessage>)(() => Json((HttpStatusCode)429, "busy")),
            null,
            "HTTP 429"),
        new TestCaseData(
            (Func<HttpResponseMessage>?)null,
            new TaskCanceledException("timeout"),
            "TIMEOUT"),
        new TestCaseData(
            (Func<HttpResponseMessage>?)null,
            new HttpRequestException("connection refused"),
            "MES_TRANSPORT"),
    };

    [Test]
    public async Task 鉴权类4xx_判确定拒绝_重试不可解()
    {
        var stub = new StubAdapter(Cfg())
        {
            Responder = () => Json(HttpStatusCode.Unauthorized, "bad key"),
        };
        var r = await stub.CreateWorkOrderAsync(Cmd(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(r.Kind, Is.EqualTo(MesDispatchKind.Rejected));
            Assert.That(r.FailureCode, Is.EqualTo(TaskDispatchFailureCode.MesRejectUnmapped));
            Assert.That(r.FailureReason, Does.Contain("401"));
        });
    }

    [Test]
    public async Task 调用方主动取消_原样上抛_不吞()
    {
        var stub = new StubAdapter(Cfg()) { ThrowOnSend = new OperationCanceledException() };
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync(); // ct 已取消 ⇒ when(!ct.IsCancellationRequested) 不成立 ⇒ 上抛

        Assert.ThrowsAsync<OperationCanceledException>(
            async () => await stub.CreateWorkOrderAsync(Cmd(), cts.Token));
    }

    [Test]
    public void Adapter在扫描命名空间且非record_可被Scrutor注册()
    {
        // 防呆：若被挪出 Services 命名空间或改 record，2号位 消费环将注入不到（NOT_CONFIGURED 空转）。
        Assert.Multiple(() =>
        {
            Assert.That(typeof(MesDispatchAdapter).Namespace, Is.EqualTo("LPS.APS.BusinessRules.Services"));
            Assert.That(typeof(MesDispatchAdapter).IsAssignableTo(typeof(IMesDispatchAdapter)), Is.True);
            Assert.That(typeof(MesDispatchAdapter).GetProperty("EqualityContract",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic), Is.Null);
        });
    }
}
