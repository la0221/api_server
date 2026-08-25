using System;
using AIVision.Application.Configuration;
using AIVision.Application.Inspection.Commands;
using AIVision.Application.Ports.MoldCode;
using AIVision.Infrastructure.DependencyInjection;
using AIVision.Infrastructure.Devices.Camera.Ids;
using AIVision.MoldCode.Onnx;
using MediatR;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<StartInspectionCycleCommand>());
builder.Services.AddOptions<AiServiceOptions>()
    .Bind(builder.Configuration.GetSection("Devices:Ai"))
    .Validate(o => !o.IsHttpEnabled || (o.BaseUrl is not null && Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out _)), "AiService BaseUrl must be absolute.")
    .ValidateOnStart();

// 註冊 AINAVI 服務（需在 AddFakeInfrastructure 之前）
builder.Services.AddOptions<AinaviOptions>()
    .Bind(builder.Configuration.GetSection("Ainavi"))
    .ValidateOnStart();
builder.Services.AddAinaviServices();

builder.Services.AddFakeInfrastructure();

// ========== 模號辨識器（本地 ONNX）：解 DI 缺註冊 + 供中央推論 ==========
// MediatR 掃 Application 組件會自動註冊單/雙 head 核對 handler，它們依賴這兩個 port。
// 過去 API 從沒註冊 → Development 的 ValidateOnBuild 啟動即崩（見 HANDOFF_API §3）。
// 這裡註冊真辨識器（同 WPF App.xaml.cs 範式；不含 UI-only 的執行期切換 port）。
// 兩者建構子皆「缺模型檔只記錄、不拋」→ 未配模型也能啟動，Recognize 回 fail-closed。

// 單 head（blackhat）：Inspection 週期 handler 依賴 IMoldCodeRecognizerPort。
builder.Services.Configure<MoldCodeOnnxOptions>(
    builder.Configuration.GetSection(MoldCodeOnnxOptions.SectionName));
builder.Services.AddSingleton<IMoldCodeRecognizerPort>(sp =>
    new SwitchableMoldCodeRecognizer(
        sp.GetRequiredService<IOptions<MoldCodeOnnxOptions>>(),
        sp.GetService<ILogger<SwitchableMoldCodeRecognizer>>()));

// 雙 head（warpPolar）：中央推論 POST /api/infer/pair 用；亦供 Pair 週期 handler。
builder.Services.Configure<MoldCodeWarpPolarOptions>(
    builder.Configuration.GetSection(MoldCodeWarpPolarOptions.SectionName));
builder.Services.AddSingleton<IMoldCodePairRecognizerPort>(sp =>
{
    var o = sp.GetRequiredService<IOptions<MoldCodeWarpPolarOptions>>().Value;
    // Resolved*：設定值不存在時回退 <程式目錄>\models\...（跨機部署免手改 appsettings；需求 4）
    return new SwitchableTwoHeadRecognizer(
        o.ResolvedMohaoModelPath, o.ResolvedXuehaoModelPath, o.Preprocess, o.Passes,
        sp.GetService<ILogger<SwitchableTwoHeadRecognizer>>());
});

// 模型登錄服務：GET /api/models（列版本/下載）+ 指定版本推論的按版本辨識器快取（隔離試模，主項1/2）。
builder.Services.Configure<AIVision.Api.Services.ModelRegistryOptions>(
    builder.Configuration.GetSection(AIVision.Api.Services.ModelRegistryOptions.SectionName));
builder.Services.AddSingleton<AIVision.Api.Services.ModelRegistryService>();

// 最近辨識紀錄（記憶體環狀緩衝）：GET /api/infer/recent —— 父端監控畫面的「有沒有收到」看板。
// 沒有這個，父端就算真的收到圖也照不出來，現場只能翻 console（2026-08-19 現場反映）。
builder.Services.AddSingleton<AIVision.Api.Services.RecentInferenceStore>();

// 收到影像的留存（**預設關閉**）：原圖本來就留在站端，父端只收前處理小圖；
// 要不要再留一份是選項（父端畫面可即時開關）——2026-08-19 使用者要求「讓我有選項可以選擇父是否要收圖片」。
builder.Services.Configure<AIVision.Api.Services.ReceivedImageOptions>(
    builder.Configuration.GetSection(AIVision.Api.Services.ReceivedImageOptions.SectionName));
builder.Services.AddSingleton<AIVision.Api.Services.ReceivedImageStore>();

// 自我強化訓練（跑在中央推論機：GPU／python／模型登錄庫都在這台）。
// 產線抓到的混料圖自帶正解 → 回頭補強模型；**過驗證閘門才算候選、使用者按上架才生效**。
// 預設 Enabled=false，未配置不影響任何既有功能。
builder.Services.Configure<AIVision.Api.Services.TrainingOptions>(
    builder.Configuration.GetSection(AIVision.Api.Services.TrainingOptions.SectionName));
builder.Services.AddSingleton<AIVision.Api.Services.TrainingService>();

// CRNN sidecar（路線 C）：POST /api/infer/ocr_crnn 轉發給 OCR_demo 的 --serve 子行程。
builder.Services.Configure<AIVision.Api.Services.CrnnSidecarOptions>(
    builder.Configuration.GetSection(AIVision.Api.Services.CrnnSidecarOptions.SectionName));
builder.Services.AddSingleton<AIVision.Api.Services.CrnnSidecarService>();

var cameraSection = builder.Configuration.GetSection("Devices:Camera");
var cameraType = cameraSection.GetValue<string>("Type") ?? "Fake";

switch (cameraType.ToLowerInvariant())
{
    case "hik":
        builder.Services.AddOptions<HikCameraOptions>()
            .Bind(cameraSection)
            .Validate(o => string.Equals(o.Type, "Hik", StringComparison.OrdinalIgnoreCase), "Devices:Camera:Type 必須為 Hik")
            .ValidateOnStart();
        builder.Services.AddHikVisionCamera();
        break;
    case "idspeak":
        builder.Services.AddOptions<IdsCameraOptions>()
            .Bind(cameraSection)
            .Validate(o => string.Equals(o.Type, "IdsPeak", StringComparison.OrdinalIgnoreCase), "Devices:Camera:Type 必須為 IdsPeak")
            .ValidateOnStart();
        builder.Services.AddIdsPeakCamera();
        break;
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();
app.MapFallback(() => Results.NotFound());

// ========== 綁定位址自我揭露（2026-08-19 需求 5）==========
// 踩過的坑：預設 launchSettings 是 http://localhost:5030，**localhost 只綁 loopback**，
// 外機一律連不進來。站端當時顯示「全部本機備援」，看起來像網路/防火牆問題，
// 實際上服務有在跑、埠也開著，只是綁錯介面——症狀會把排查方向帶歪，現場浪費了大半天。
// 所以啟動時把「實際綁到哪」印在最顯眼的位置，並在只綁 loopback 時明講跨機要怎麼改。
app.Lifetime.ApplicationStarted.Register(() =>
{
    var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AIVision.Api.Bind");
    var addresses = app.Services
        .GetService<Microsoft.AspNetCore.Hosting.Server.IServer>()?
        .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?
        .Addresses;
    var list = addresses is { Count: > 0 } ? string.Join("、", addresses) : "(未知)";
    log.LogInformation("[Bind] 實際綁定位址：{Addresses}", list);

    bool loopbackOnly = addresses is { Count: > 0 } && addresses.All(a =>
        a.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
        a.Contains("127.0.0.1", StringComparison.Ordinal) ||
        a.Contains("[::1]", StringComparison.Ordinal));
    if (loopbackOnly)
        log.LogWarning(
            "[Bind] ⚠ 目前只接受本機連線（loopback）。跨機送檢會全部失敗，且站端只會顯示「本機備援」看不出原因。" +
            "跨機請改綁 0.0.0.0：appsettings.json 的 \"Urls\" 設 http://0.0.0.0:5030，" +
            "或用 AIVision.Api.exe --urls http://0.0.0.0:5030 啟動，並確認防火牆已放行該埠入站。");
});

app.Run();
