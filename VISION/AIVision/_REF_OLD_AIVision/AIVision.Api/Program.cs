using System;
using AIVision.Application.Configuration;
using AIVision.Application.Inspection.Commands;
using AIVision.Infrastructure.DependencyInjection;
using AIVision.Infrastructure.Devices.Camera.Ids;
using MediatR;

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
