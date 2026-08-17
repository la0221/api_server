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
    return new SwitchableTwoHeadRecognizer(
        o.MohaoModelPath, o.XuehaoModelPath, o.Preprocess, o.Passes,
        sp.GetService<ILogger<SwitchableTwoHeadRecognizer>>());
});

// 模型登錄服務：GET /api/models（列版本/下載）+ 指定版本推論的按版本辨識器快取（隔離試模，主項1/2）。
builder.Services.Configure<AIVision.Api.Services.ModelRegistryOptions>(
    builder.Configuration.GetSection(AIVision.Api.Services.ModelRegistryOptions.SectionName));
builder.Services.AddSingleton<AIVision.Api.Services.ModelRegistryService>();

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

app.Run();
