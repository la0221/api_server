using System;
using System.Net.Http.Headers;
using AIVision.Application.Configuration;
using AIVision.Application.Ports.Devices;
using AIVision.Application.Ports.Persistence;
using AIVision.Application.Ports.Services;
using AIVision.Application.Services;
using AIVision.Infrastructure.AiService;
using AIVision.Infrastructure.Services;
using AIVision.Infrastructure.Devices;
using AIVision.Infrastructure.Devices.Camera.Hik;
using AIVision.Infrastructure.Devices.Camera.Ids;
using AIVision.Infrastructure.Devices.Light;
using AIVision.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AIVision.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFakeInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<ICameraPort, FakeCameraPort>();
        services.AddSingleton<ICameraDiscoveryPort, FakeCameraDiscovery>();
        // ⚠️ ILightPort 由 App.xaml.cs 根據配置決定是否使用 Fake 或真實實現
        // services.AddSingleton<ILightPort, FakeLightPort>();  ← 已移除
        services.AddSingleton<IPlcPort, FakePlcPort>();
        services.TryAddSingleton<ICameraControlPort, NullCameraControlPort>();

        services.AddHttpClient<HttpAiInferencePort>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<AiServiceOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(options.BaseUrl) &&
                Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri))
            {
                client.BaseAddress = uri;
            }

            var timeoutMs = Math.Clamp(options.TimeoutMs, 100, 60000);
            client.Timeout = TimeSpan.FromMilliseconds(timeoutMs);
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        });

        // 只有在沒有其他實作時才註冊 Fake
        services.TryAddSingleton<IAiInferencePort>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AiServiceOptions>>().Value;
            if (options.IsHttpEnabled)
            {
                return sp.GetRequiredService<HttpAiInferencePort>();
            }

            return new FakeAiInferencePort();
        });

        services.AddSingleton<IInspectionRepository, InMemoryInspectionRepository>();
        services.AddSingleton<IWorkOrderRepository, InMemoryWorkOrderRepository>();

        return services;
    }

    public static IServiceCollection AddHikVisionCamera(this IServiceCollection services)
    {
        services.AddTransient<ICameraDiscoveryPort, HikDiscoveryAdapter>();
        services.AddTransient<ICameraPort, HikCameraAdapter>();
        return services;
    }

    public static IServiceCollection AddIdsPeakCamera(this IServiceCollection services)
    {
        services.AddSingleton<IdsCameraControlPort>();
        services.AddSingleton<ICameraControlPort>(sp => sp.GetRequiredService<IdsCameraControlPort>());
        services.AddSingleton<ICameraDiscoveryPort, IdsCameraDiscoveryAdapter>();
        services.AddSingleton<ICameraPort, IdsCameraPort>();
        return services;
    }

    /// <summary>
    /// 添加 LTS 光源控制器（使用 ASCII 協定 - 推薦）
    /// 設備會主動撥入到本機 TCP Server
    /// </summary>
    /// <param name="services">服務集合</param>
    /// <param name="listenIp">監聽 IP（預設 0.0.0.0）</param>
    /// <param name="listenPort">監聽埠號（預設 8000）</param>
    /// <param name="channelCount">通道數（預設 2）</param>
    /// <param name="timeoutMs">超時毫秒（預設 1000）</param>
    /// <param name="commandIntervalMs">命令間隔毫秒（預設 20）</param>
    public static IServiceCollection AddLtsAsciiLightController(
        this IServiceCollection services,
        string listenIp = "0.0.0.0",
        int listenPort = 8000,
        int channelCount = 2,
        int timeoutMs = 1000,
        int commandIntervalMs = 20)
    {
        services.AddSingleton<ILightPort>(sp =>
        {
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LtsAsciiLightPort>>();
            return new LtsAsciiLightPort(logger, listenIp, listenPort, channelCount, timeoutMs, commandIntervalMs);
        });
        return services;
    }

    /// <summary>
    /// 添加 LTS 光源控制器（RS232 串口版本）
    /// </summary>
    /// <param name="services">服務集合</param>
    /// <param name="channelCount">通道數（預設 2）</param>
    /// <param name="timeoutMs">超時毫秒（預設 1000）</param>
    /// <param name="commandIntervalMs">命令間隔毫秒（預設 20）</param>
    /// <param name="brightnessControl">光源亮度控制選項（可選）</param>
    public static IServiceCollection AddLtsSerialLightController(
        this IServiceCollection services,
        int channelCount = 2,
        int timeoutMs = 1000,
        int commandIntervalMs = 20,
        LightBrightnessControlOptions? brightnessControl = null)
    {
        services.AddSingleton<LtsSerialLightPort>(sp =>
        {
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LtsSerialLightPort>>();
            return new LtsSerialLightPort(logger, channelCount, timeoutMs, commandIntervalMs, brightnessControl);
        });
        return services;
    }

    /// <summary>
    /// 添加專案管理服務
    /// </summary>
    public static IServiceCollection AddProjectServices(this IServiceCollection services)
    {
        // 專案設定服務
        services.AddSingleton<IProjectConfigService, ProjectConfigService>();

        // 專案初始化服務
        services.AddSingleton<IProjectInitializationService, ProjectInitializationService>();

        return services;
    }

    /// <summary>
    /// 添加瑕疵過濾服務
    /// </summary>
    public static IServiceCollection AddDefectFilteringService(this IServiceCollection services)
    {
        services.AddSingleton<IDefectFilteringService, DefectFilteringService>();
        return services;
    }

    /// <summary>
    /// 添加認證服務
    /// </summary>
    public static IServiceCollection AddAuthService(this IServiceCollection services)
    {
        services.AddSingleton<IAuthService, ConfigAuthService>();
        return services;
    }

    /// <summary>
    /// 添加 AINAVI EdgeHub AI 服務支援。
    /// </summary>
    /// <param name="services">服務集合</param>
    /// <param name="configureOptions">配置選項的動作（可選）</param>
    public static IServiceCollection AddAinaviServices(
        this IServiceCollection services,
        Action<AinaviOptions>? configureOptions = null)
    {
        // 註冊 AinaviOptions
        if (configureOptions != null)
        {
            services.Configure(configureOptions);
        }

        // 註冊 HttpClient for AinaviAiModelPort
        services.AddHttpClient<AinaviAiModelPort>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<AinaviOptions>>().Value;
            // 模型載入需要 15-20 秒，設定 30 秒超時
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // 註冊 HttpClient for AinaviAiInferencePort
        services.AddHttpClient<AinaviAiInferencePort>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<AinaviOptions>>().Value;
            var timeoutMs = Math.Clamp(10000, 2000, 60000); // 推論可能需要更長時間，預設 10 秒
            client.Timeout = TimeSpan.FromMilliseconds(timeoutMs);
        });

        // 註冊 Ports（使用 TryAdd 避免與 Fake 衝突）
        services.TryAddScoped<IAiModelPort, AinaviAiModelPort>();
        services.TryAddSingleton<IAiInferencePort, AinaviAiInferencePort>();

        // 註冊推論記錄服務
        services.AddSingleton<IInferenceLogService, JsonFileInferenceLogService>();

        return services;
    }

    /// <summary>
    /// 添加 AINAVI Workflow 服務支援。
    /// 用於本機 EdgeHub + Workflow 多步驟推論
    /// </summary>
    /// <param name="services">服務集合</param>
    /// <param name="configureOptions">配置選項的動作（可選）</param>
    public static IServiceCollection AddWorkflowServices(
        this IServiceCollection services,
        Action<WorkflowOptions>? configureOptions = null)
    {
        // 註冊 WorkflowOptions
        if (configureOptions != null)
        {
            services.Configure(configureOptions);
        }

        // 註冊 HttpClient for EdgeHubWorkflowService
        services.AddHttpClient<EdgeHubWorkflowService>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<WorkflowOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });

        // 註冊 HttpClient for WorkflowAiInferencePort
        services.AddHttpClient<WorkflowAiInferencePort>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<WorkflowOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });

        // 註冊 Workflow 服務管理
        services.AddSingleton<IWorkflowService, EdgeHubWorkflowService>();

        // 註冊推論記錄服務（如果還沒有）
        services.TryAddSingleton<IInferenceLogService, JsonFileInferenceLogService>();

        return services;
    }

    /// <summary>
    /// 根據配置決定使用 Workflow 或傳統 AINAVI 推論
    /// </summary>
    public static IServiceCollection AddAiInferenceWithWorkflowSupport(
        this IServiceCollection services)
    {
        // 使用工廠模式，根據配置決定使用哪個 Port
        services.AddSingleton<IAiInferencePort>(sp =>
        {
            var workflowOptions = sp.GetRequiredService<IOptions<WorkflowOptions>>().Value;
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("DI.AiInferenceFactory");

            if (workflowOptions.Enabled)
            {
                logger.LogInformation("[DI] 使用 Workflow 模式推論 (Port: {Port})", workflowOptions.WorkflowPort);
                return sp.GetRequiredService<WorkflowAiInferencePort>();
            }
            else
            {
                logger.LogInformation("[DI] 使用傳統 AINAVI 模式推論");
                return sp.GetRequiredService<AinaviAiInferencePort>();
            }
        });

        return services;
    }
}
