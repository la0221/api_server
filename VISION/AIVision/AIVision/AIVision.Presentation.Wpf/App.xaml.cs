using System;
using System.IO;
using System.Windows;
using AIVision.Application.Configuration;
using AIVision.Application.Inspection.Commands;
using AIVision.Application.Ports.Devices;
using AIVision.Application.Ports.ImageBatch;
using AIVision.Application.Ports.ProductionStats;
using AIVision.Application.Ports.Persistence;
using AIVision.Application.Ports.History;
using AIVision.Application.Services;
using AIVision.Infrastructure.AiService;
using AIVision.Infrastructure.Adapters.AiInference;
using AIVision.Infrastructure.Configs;
using AIVision.Infrastructure.DependencyInjection;
using AIVision.Infrastructure.Devices;
using AIVision.Application.Ports.Services;
using AIVision.Infrastructure.Devices.Camera.Ids;
using AIVision.Infrastructure.Devices.Plc;
using AIVision.Infrastructure.Devices.Plc.Communication;
using AIVision.Infrastructure.Devices.Plc.DependencyInjection;
using AIVision.Infrastructure.ConfigurationValidators;
using AIVision.Infrastructure.Devices.Light;
using AIVision.Infrastructure.MoldCode;
using AIVision.Infrastructure.Persistence;
using AIVision.Presentation.Wpf.Adapters.ProductionStats;
using AIVision.Presentation.Wpf.Adapters.ImageBatch;
using AIVision.Presentation.Wpf.Services;
using AIVision.Presentation.Wpf.Services.Navigation;
using AIVision.Presentation.Wpf.Services.ProductionStats;
using AIVision.Presentation.Wpf.ViewModels;
using AIVision.Presentation.Wpf.Views;
using CommunityToolkit.Mvvm.Messaging;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Linq;
using AIVision.Application.Ports.Models;
using AIVision.Infrastructure.Services;
using AIVision.Presentation.Wpf.Logging;
using AIVision.Application.MoldCode;
using AIVision.Application.Ports.MoldCode;
using AIVision.MoldCode.Onnx;

namespace AIVision.Presentation.Wpf;

public partial class App : System.Windows.Application
{
    private readonly IHost _host;

    public App()
    {
        _host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(cfg =>
            {
                cfg.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                cfg.AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true);
                cfg.AddEnvironmentVariables();
            })
            .ConfigureLogging((context, logging) =>
            {
                // 取得執行檔所在目錄
                var exeDir = AppDomain.CurrentDomain.BaseDirectory;
                var logDir = Path.Combine(exeDir, "logs");

                // 加入檔案日誌 (Debug 級別以上)
                logging.AddFile(logDir, LogLevel.Debug);
            })
            .ConfigureServices(ConfigureServices)
            .Build();
    }

    private void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        // 讀取 Workflow 配置（決定使用本機 EdgeHub 還是遠端 AINAVI）
        var workflowSection = context.Configuration.GetSection("Workflow");
        var workflowEnabled = workflowSection.GetValue<bool>("Enabled");

        // 配置 AI 推論設定（圖片點檢）
        services.Configure<AiSettings>(context.Configuration.GetSection("AiInference"));

        // 註冊 HttpClient + 推論實作（分類模式）
        services.AddHttpClient<HttpClassificationInferencePort>();

        // 註冊 HttpClient + 推論實作（分割模式）
        services.AddHttpClient<HttpSegmentationInferencePort>();

        // 圖片點檢 AI 推論一律走本地 ONNX 模號辨識器(使用目前選定的本地模型),
        // 不再呼叫 HTTP 遠端(無伺服器時不會 hang)。HTTP 分類/分割 Port 仍註冊供其他相依解析。
        services.AddTransient<AIVision.Application.Ports.ImageBatch.IAiInferencePort>(sp =>
            new LocalMoldCodeBatchInferencePort(
                sp.GetRequiredService<IMoldCodeRecognizerPort>(),
                sp.GetRequiredService<IMoldCodeModelSwitch>()));

        // 註冊 Overlay 繪製器（分割專用）
        services.AddTransient<IOverlayRendererPort, SegOverlayRenderer>();

        // 註冊批量推論專用的 HTTP 推論 Port
        services.AddHttpClient<HttpBatchInferencePort>((sp, client) =>
        {
            var cfg = sp.GetRequiredService<IOptions<AiSettings>>().Value;
            // 批量推論使用固定的 /inference 端點
            var baseUrl = cfg.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                baseUrl = "http://192.168.1.95:8001/inference";
            }
            // 確保 BaseUrl 包含 /inference
            if (!baseUrl.EndsWith("/inference", StringComparison.OrdinalIgnoreCase))
            {
                if (!baseUrl.EndsWith("/"))
                {
                    baseUrl += "/";
                }
                baseUrl += "inference";
            }
            client.BaseAddress = new Uri(baseUrl);
            client.Timeout = TimeSpan.FromSeconds(cfg.Http.TimeoutSeconds);
        });

        // 批量推論 ViewModel（本地 ONNX 模號辨識；所有依賴由 DI 自動解析）
        services.AddTransient<BatchInferenceViewModel>();

        // 註冊設定驗證器
        services.AddSingleton<IValidateOptions<AiServiceOptions>, AiServiceOptionsValidator>();
        services.AddSingleton<IValidateOptions<PlcConnectionOptions>, PlcConnectionOptionsValidator>();
        services.AddSingleton<IValidateOptions<LightDeviceOptions>, LightDeviceOptionsValidator>();

        services.AddOptions<AiServiceOptions>()
            .Bind(context.Configuration.GetSection("Devices:Ai"))
            .ValidateOnStart();

        services.AddOptions<PlcConnectionOptions>()
            .Bind(context.Configuration.GetSection("Devices:PlcConnection"))
            .ValidateOnStart();

        services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<StartInspectionCycleCommand>());

        // ========== 模號核對(本地 ONNX YOLO)服務 ==========
        // 辨識器走獨立 port(IMoldCodeRecognizerPort),不經舊 IAiInferencePort(OCR≠瑕疵)。
        // VerifyMoldCodeCycleCommandHandler 由上方 MediatR 自動註冊(同 Application 組件)。
        services.Configure<MoldCodeOnnxOptions>(context.Configuration.GetSection(MoldCodeOnnxOptions.SectionName));
        // 可抽換辨識器(Singleton):同一實例對外既是 IMoldCodeRecognizerPort(辨識週期)
        // 也是 IMoldCodeModelSwitch(UI 執行期切換)。InferenceSession 非執行緒安全 → 內部上鎖。
        // baseline(MoldCodeOnnx)提供 Imgsz/UseBlackhat/UseLocator/OuterFactor/CodePrefix 預設 + 預載模型。
        services.AddSingleton<SwitchableMoldCodeRecognizer>(sp =>
            new SwitchableMoldCodeRecognizer(
                sp.GetRequiredService<IOptions<MoldCodeOnnxOptions>>(),
                sp.GetService<ILogger<SwitchableMoldCodeRecognizer>>()));
        services.AddSingleton<IMoldCodeRecognizerPort>(sp => sp.GetRequiredService<SwitchableMoldCodeRecognizer>());
        services.AddSingleton<IMoldCodeModelSwitch>(sp => sp.GetRequiredService<SwitchableMoldCodeRecognizer>());
        // 週期門檻走 config;ClassSet 由 MoldCodeOnnx 衍生(避免同值重複設定)。
        services.AddOptions<MoldCodeCycleOptions>()
            .Bind(context.Configuration.GetSection(MoldCodeCycleOptions.SectionName))
            .PostConfigure<IOptions<MoldCodeOnnxOptions>>((cycle, onnx) =>
            {
                if (cycle.ClassSet is null || cycle.ClassSet.Count == 0)
                {
                    var o = onnx.Value;
                    cycle.ClassSet = o.ClassNames.Select(c => $"{o.CodePrefix}/{c}").ToList();
                }
            });
        // 模號運轉服務(取代舊 AutoRunService 角色);實機觸發由其訂閱 PLC 握手
        services.AddSingleton<AreaRunService>();

        // ========== 雙 head warpPolar/annulus 辨識(V6.7.1：模號+穴號各一本地 ONNX) ==========
        // 與上方單 head blackhat 路徑並存(可抽換);前處理 = warpPolar+annulus(與訓練對齊)。
        // VerifyMoldCodePairCycleCommandHandler 由上方 MediatR 自動註冊(同 Application 組件)。
        services.Configure<MoldCodeWarpPolarOptions>(
            context.Configuration.GetSection(MoldCodeWarpPolarOptions.SectionName));
        // 可切換雙 head 辨識器(Singleton):對外同時是 IMoldCodePairRecognizerPort(辨識端)
        // 與 IMoldCodePairModelSwitch(雙軸模型管理頁執行期切換版本)。baseline 走 appsettings 的
        // MoldCodeWarpPolar;baseline 缺檔時不拋(app 照常啟動),改由 UI 載入版本後再用。
        services.AddSingleton<SwitchableTwoHeadRecognizer>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<MoldCodeWarpPolarOptions>>().Value;
            return new SwitchableTwoHeadRecognizer(
                o.MohaoModelPath, o.XuehaoModelPath, o.Preprocess, o.Passes,
                sp.GetService<ILogger<SwitchableTwoHeadRecognizer>>());
        });
        services.AddSingleton<IMoldCodePairRecognizerPort>(sp =>
            sp.GetRequiredService<SwitchableTwoHeadRecognizer>());
        services.AddSingleton<IMoldCodePairModelSwitch>(sp =>
            sp.GetRequiredService<SwitchableTwoHeadRecognizer>());
        services.AddOptions<MoldCodePairCycleOptions>()
            .Bind(context.Configuration.GetSection(MoldCodePairCycleOptions.SectionName));

        // ========== 中央推論(API server)遠端辨識器 ==========
        // 階段1(整合設計書 2026-07-15_edge_server_integration.md)：只註冊、不接生產熱迴圈。
        // 生產辨識仍走上方本機 ONNX(IMoldCodePairRecognizerPort → SwitchableTwoHeadRecognizer)；
        // 本類別目前僅供「測試中央推論」驗收按鈕以具體型別注入使用。
        // 待驗收通過 + 手動開關(階段3)才會由來源選擇器接管 port。
        services.Configure<InferenceServerOptions>(
            context.Configuration.GetSection(InferenceServerOptions.SectionName));
        services.AddHttpClient<RemotePairRecognizer>((sp, client) =>
        {
            // 逐次呼叫另用 CTS 控制逾時(推論/健檢各有預算)；此處給一個寬鬆上限即可。
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        // 批量/離線測試頁的「測試資料夾」下拉選項（UI 便利，不影響任何辨識邏輯）。
        services.Configure<Models.TestImageFolderOptions>(
            context.Configuration.GetSection(Models.TestImageFolderOptions.SectionName));
        // 系統選單「API 伺服器設定」：執行期切換中央推論位址 + 測試連線。
        // 清單/最後套用位址持久化於 %LocalAppData%\AIVision（勿寫 bin appsettings——rebuild 會蓋掉）。
        services.AddSingleton<Services.InferenceServerListStore>();
        services.AddTransient<ServerSettingsViewModel>();
        services.AddTransient<Views.ServerSettingsView>();

        // 模型倉庫客戶端（列版本/下載同步/上架發布）＋「模型發布」頁（工程師以上，選單把關）。
        services.AddHttpClient<ModelHubClient>((sp, client) =>
        {
            // 上傳/下載走大檔：逾時交由各方法內的 CTS 控制，這裡放寬上限。
            client.Timeout = TimeSpan.FromMinutes(10);
        });
        services.AddTransient<ModelPublishViewModel>();
        services.AddTransient<Views.ModelPublishView>();

        // CRNN 中央推論客戶端 + 測試頁（引擎並行期 2026-08-04：CRNN 逐步取代雙 head）。
        services.AddHttpClient<CrnnInferClient>((sp, client) =>
        {
            // 冷啟可達 90 秒：逾時由客戶端內部 CTS 控制，這裡放寬上限。
            client.Timeout = TimeSpan.FromMinutes(5);
        });
        services.AddTransient<CrnnBatchViewModel>();
        services.AddTransient<Views.CrnnBatchView>();

        // 讀取光源控制器配置
        var lightSection = context.Configuration.GetSection("Devices:Light");
        var listenIp = lightSection.GetValue<string>("ListenIp") ?? "0.0.0.0";
        var listenPort = lightSection.GetValue<int>("ListenPort");
        if (listenPort == 0) listenPort = 8000;
        var channelCount = lightSection.GetValue<int>("ChannelCount");
        if (channelCount == 0) channelCount = 2;
        var timeoutMs = lightSection.GetValue<int>("TimeoutMs");
        if (timeoutMs == 0) timeoutMs = 1000;

        services.Configure<LightDeviceOptions>(lightSection);

        // 先添加 Fake 基礎設施（相機）
        services.AddSingleton<ICameraPort, FakeCameraPort>();
        services.AddSingleton<ICameraDiscoveryPort, FakeCameraDiscovery>();
        services.TryAddSingleton<ICameraControlPort, NullCameraControlPort>();

        // 添加 PLC 服務（根據設定切換實作）
        services.AddPlcServices(context.Configuration);

        // 根據設定決定使用 Fake 或真實 PLC
        var plcSection = context.Configuration.GetSection("Devices:PlcConnection");
        var plcType = context.Configuration.GetValue<string>("Devices:Plc:Type") ?? "Fake";

        if (plcType.Equals("Modbus", StringComparison.OrdinalIgnoreCase))
        {
            // 使用真實 Modbus PLC（透過 IPlcSignalMapper）
            services.AddSingleton<IPlcPort, ModbusPlcPort>();
        }
        else
        {
            // 使用 Fake PLC（開發/測試用）
            services.AddSingleton<IPlcPort, FakePlcPort>();
        }

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

        // 註：IAiInferencePort 的註冊已移至下方使用 SwitchableAiInferencePort
        // 支援運行時動態切換單一模型/Workflow 模式

        // 配置 SQLite 數據庫連接
        var databasePath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIVision",
            "aivision.db"
        );

        // 確保目錄存在
        var dbDirectory = System.IO.Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dbDirectory))
        {
            System.IO.Directory.CreateDirectory(dbDirectory);
        }

        // 創建並初始化數據庫連接工廠
        var connectionFactory = new AIVision.Infrastructure.Persistence.SQLite.SqliteDatabaseConnectionFactory(databasePath);

        // 註冊連接工廠
        services.AddSingleton<AIVision.Infrastructure.Persistence.SQLite.IDatabaseConnectionFactory>(connectionFactory);

        // 註冊 SQLite Repository（替換 InMemory）
        services.AddSingleton<IInspectionRepository, AIVision.Infrastructure.Persistence.SQLite.SqliteInspectionRepository>();
        services.AddSingleton<IWorkOrderRepository, AIVision.Infrastructure.Persistence.SQLite.SqliteWorkOrderRepository>();

        // 保留 InMemory Repository 作為備用（註釋掉）
        // services.AddSingleton<IInspectionRepository, InMemoryInspectionRepository>();
        // services.AddSingleton<IWorkOrderRepository, InMemoryWorkOrderRepository>();

        // 註冊歷史查詢服務
        services.AddSingleton<AIVision.Application.Ports.History.IInspectionHistoryQuery, AIVision.Infrastructure.Persistence.SQLite.SqliteInspectionHistoryQuery>();

        // 註冊工單管理服務
        services.AddSingleton<AIVision.Application.Services.IWorkOrderManagementService, AIVision.Application.Services.WorkOrderManagementService>();

        // 註冊圖片保存服務
        services.AddSingleton<AIVision.Application.Services.IInspectionImageService, AIVision.Application.Services.InspectionImageService>();

        // 使用 ASCII 協定連接光源控制器（設備主動撥入）
        services.AddLtsAsciiLightController(listenIp, listenPort, channelCount, timeoutMs);

        var cameraSection = context.Configuration.GetSection("Devices:Camera");
        var cameraType = cameraSection.GetValue<string>("Type") ?? "Fake";

        switch (cameraType.ToLowerInvariant())
        {
            case "hik":
                services.AddOptions<HikCameraOptions>()
                    .Bind(cameraSection)
                    .Validate(o => string.Equals(o.Type, "Hik", StringComparison.OrdinalIgnoreCase), "Devices:Camera:Type 必須為 Hik")
                    .ValidateOnStart();
                services.AddHikVisionCamera();
                break;
            case "idspeak":
                services.AddOptions<IdsCameraOptions>()
                    .Bind(cameraSection)
                    .Validate(o => string.Equals(o.Type, "IdsPeak", StringComparison.OrdinalIgnoreCase), "Devices:Camera:Type 必須為 IdsPeak")
                    .ValidateOnStart();
                services.AddIdsPeakCamera();
                // 線掃服務/模擬器/AutoRun 已移除(模號站固定面掃);面掃取像由 ShellViewModel.StartAreaScanModeAsync 走 ICameraPort
                break;
        }

        services.AddSingleton<IMessenger, WeakReferenceMessenger>();

        services.AddTransient<IFolderPickerPort, FolderPickerPort>();
        services.AddTransient<IImageEnumeratorPort, FileSystemImageEnumerator>();
        services.AddTransient<IImageLoaderPort, WpfImageLoader>();
        // IOverlayRendererPort 已由 SegOverlayRenderer 在 AI 推論配置中註冊
        services.AddTransient<IImageWriterPort, WpfImageWriter>();

        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<PairWorkflowState>();
        services.AddTransient<IProductionStatsExportService, ProductionStatsExportService>();

        // ========== 模型掃描服務配置 ==========
        // 配置 ModelScanOptions
        services.Configure<ModelScanOptions>(context.Configuration.GetSection(ModelScanOptions.SectionName));

        // 註冊模型發現服務(本地 ONNX:列舉 *.onnx,類別由 .names.json 推得)
        // LocalModelDiscoveryService(AINAVI 資料夾掃描)保留類別但不再註冊。
        services.AddSingleton<IModelDiscoveryService, OnnxModelDiscoveryService>();

        // 註冊模型配置服務（整合自動掃描）
        services.AddSingleton<ModelConfigService>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<ModelConfigService>>();
            var discoveryService = sp.GetRequiredService<IModelDiscoveryService>();
            var scanOptions = sp.GetRequiredService<IOptions<ModelScanOptions>>();

            // Log DI 註冊狀態
            var options = scanOptions.Value;
            if (options.AutoScan)
            {
                logger.LogInformation("[DI] 註冊 OnnxModelDiscoveryService - ScanFolder: {Folder}", options.ScanFolder);
            }

            return new ModelConfigService(logger, discoveryService, scanOptions, "models.json");
        });

        // 註冊 AinaviAiInferencePort（專門給 Offline 測試使用）
        services.AddHttpClient<AinaviAiInferencePort>((sp, client) =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddSingleton<AinaviOptions>(sp =>
        {
            var ainaviSection = context.Configuration.GetSection("Ainavi");
            return new AinaviOptions
            {
                Host = "http://192.168.1.95",
                EdgeHubPort = 5001,
                DefaultModelPort = ainaviSection.GetValue<int>("DefaultPort") > 0
                    ? ainaviSection.GetValue<int>("DefaultPort")
                    : 8001
            };
        });
        services.AddSingleton(sp => Microsoft.Extensions.Options.Options.Create(sp.GetRequiredService<AinaviOptions>()));

        // ========== Workflow 服務配置 ==========
        // 配置 WorkflowOptions
        services.Configure<WorkflowOptions>(context.Configuration.GetSection("Workflow"));

        // 配置 Workflow Overlay Options
        services.Configure<OverlayOptions>(context.Configuration.GetSection("Workflow:Overlay"));

        // 註冊 Contour Overlay Renderer
        services.AddSingleton<IContourOverlayRenderer, ContourOverlayRenderer>();

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

        // 註冊 SwitchableAiInferencePort:仍保留註冊,因為部分 ViewModel/Service 以具體型別注入
        // (OfflineTestViewModel / OnlineModelManagementViewModel / ProjectInitializationService);
        // 但它已不再綁定為 Devices.IAiInferencePort —— 推論一律走本地 ONNX。
        services.AddSingleton<SwitchableAiInferencePort>();

        // 設備推論 port 一律走本地 ONNX 模號辨識器(使用目前選定的本地模型);
        // OfflineInspectionService / ShellViewModel.ExecuteInspectionAsync / ProjectInit 皆零改動即本地化,
        // 無遠端伺服器時不會 hang。
        services.AddSingleton<AIVision.Application.Ports.Devices.IAiInferencePort>(sp =>
            new LocalMoldCodeInferencePort(
                sp.GetRequiredService<IMoldCodeRecognizerPort>(),
                sp.GetRequiredService<IMoldCodeModelSwitch>()));

        // 註冊 AINAVI API Client
        services.AddTransient<AinaviApiClient>(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            var logger = sp.GetService<ILogger<AinaviApiClient>>();
            return new AinaviApiClient(configuration, logger);
        });

        services.AddSingleton<ShellViewModel>();
        services.AddTransient<CameraViewModel>();
        services.AddTransient<HistoryViewModel>();
        services.AddTransient<LoginViewModel>();
        services.AddTransient<ModelSelectorViewModel>(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            var logger = sp.GetService<ILogger<ModelSelectorViewModel>>();
            return new ModelSelectorViewModel(configuration, logger);
        });
        services.AddTransient<OfflineModelManagementViewModel>();
        // OfflineModelEditViewModel/View 以命令內直接 new 建構(需傳入所選 ModelConfig + baseline),不走 DI。
        services.AddTransient<OnlineModelManagementViewModel>();
        services.AddTransient<ModelSelectViewModel>();
        services.AddTransient<ModelEditViewModel>();
        // IoPanelViewModel 必須是 Singleton，才能讓 ShellViewModel 控制其輪詢狀態
        // (Auto Run 時停止輪詢，避免 PLC 連線競爭條件)
        services.AddSingleton<IoPanelViewModel>();
        services.AddTransient<ImageBatchViewModel>();
        // BatchInferenceViewModel 已在上面单独注册（使用专用的推論Port）
        services.AddSingleton<IProductionStatsConfigProvider, ProductionStatsConfigProvider>();
        // 使用真實的 SQLite 生產統計查詢（替換 FakeProductionStatsQuery）
        services.AddTransient<IProductionStatsQuery, AIVision.Infrastructure.Persistence.SQLite.SqliteProductionStatsQuery>();
        services.AddTransient<ProductionStatsViewModel>();
        services.AddTransient<CameraTestViewModel>();
        services.AddTransient<LightControlViewModel>();
        services.AddTransient<LightDeviceScanViewModel>();

        // RS232 光源控制器（獨立於 TCP 版本）
        services.Configure<LightSerialDeviceOptions>(context.Configuration.GetSection(LightSerialDeviceOptions.SectionName));
        services.AddSingleton<LtsSerialLightPort>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<LightSerialDeviceOptions>>().Value;
            var logger = sp.GetRequiredService<ILogger<LtsSerialLightPort>>();
            // 傳入亮度控制選項
            return new LtsSerialLightPort(logger, options.ChannelCount, options.TimeoutMs, options.CommandIntervalMs, options.BrightnessControl);
        });
        // 註冊為 ILightPort，供 AutoRunService 使用
        services.AddSingleton<ILightPort>(sp => sp.GetRequiredService<LtsSerialLightPort>());
        services.AddTransient<LightSerialControlViewModel>();
        services.AddTransient<WorkOrderManagementViewModel>();
        services.AddTransient<WorkOrderInputViewModel>();
        services.AddTransient<MoldCodeBatchViewModel>();
        services.AddTransient<MoldCodePairBatchViewModel>();

        // 註冊 Offline 測試服務
        services.AddSingleton<IOfflineInspectionService, AIVision.Infrastructure.Services.OfflineInspectionService>();
        services.AddTransient<OfflineTestViewModel>();
        services.AddTransient<OfflineTestView>();

        // ========== 專案管理服務配置 ==========
        // 註冊 IModelConfigProvider 適配器（讓 ProjectInitializationService 能夠存取模型配置）
        services.AddSingleton<IModelConfigProvider, ModelConfigProviderAdapter>();
        services.AddProjectServices();

        // ========== 瑕疵過濾服務配置 ==========
        services.Configure<DefectFilteringOptions>(context.Configuration.GetSection(DefectFilteringOptions.SectionName));
        services.AddDefectFilteringService();

        // ========== 認證服務 ==========
        services.AddAuthService();

        // 專案相關 ViewModels
        services.AddTransient<ProjectSelectViewModel>();
        services.AddTransient<ProjectEditViewModel>();
        services.AddTransient<ProjectLoadingViewModel>();

        // 專案相關 Views
        services.AddTransient<ProjectSelectWindow>();
        services.AddTransient<ProjectEditWindow>();
        services.AddTransient<ProjectLoadingWindow>();

        services.AddSingleton<ShellView>();
        services.AddTransient<CameraView>();
        services.AddTransient<HistoryView>();
        services.AddTransient<LoginView>();
        services.AddTransient<ModelSelectorView>();
        services.AddTransient<OfflineModelManagementView>();
        services.AddTransient<OnlineModelManagementView>();
        services.AddTransient<ModelSelectView>();
        services.AddTransient<ModelEditView>();
        services.AddTransient<IoPanelView>();
        services.AddTransient<ImageBatchView>();
        services.AddTransient<BatchInferenceView>();
        services.AddTransient<ProductionStatsView>();
        services.AddTransient<CameraTestView>();
        services.AddTransient<LightControlView>();
        services.AddTransient<LightDeviceScanView>();
        services.AddTransient<LightSerialControlView>();
        services.AddTransient<WorkOrderManagementView>();
        services.AddTransient<WorkOrderInputView>();
        services.AddTransient<MoldCodeBatchView>();
        services.AddTransient<MoldCodePairBatchView>();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        var logger = _host.Services.GetService<ILogger<App>>();
        logger?.LogInformation("[App] 開始啟動程序");

        // 顯示 Splash Screen
        var splashLogger = _host.Services.GetService<ILogger<SplashWindow>>();
        var splash = new SplashWindow(splashLogger);
        splash.Show();

        try
        {
            // 初始化 Host
            splash.UpdateStatus("正在初始化服務...");
            await _host.StartAsync();
            logger?.LogInformation("[App] Host 啟動完成");

            // 還原使用者最後套用的中央推論 server 位址（「API 伺服器設定」持久化；無檔案則維持 appsettings 值）。
            try
            {
                var serverStore = _host.Services.GetRequiredService<Services.InferenceServerListStore>();
                var savedServers = serverStore.Load();
                if (!string.IsNullOrWhiteSpace(savedServers?.ActiveBaseUrl))
                {
                    var inferOpts = _host.Services
                        .GetRequiredService<Microsoft.Extensions.Options.IOptions<AIVision.Infrastructure.MoldCode.InferenceServerOptions>>();
                    inferOpts.Value.BaseUrl = savedServers!.ActiveBaseUrl!;
                    logger?.LogInformation("[App] 已還原中央推論位址: {Url}", savedServers.ActiveBaseUrl);
                }
            }
            catch (Exception srvEx)
            {
                logger?.LogWarning(srvEx, "[App] 還原中央推論位址失敗，沿用 appsettings 值");
            }

            // 初始化數據庫
            splash.UpdateStatus("正在連接資料庫...");
            try
            {
                var connectionFactory = _host.Services.GetRequiredService<AIVision.Infrastructure.Persistence.SQLite.IDatabaseConnectionFactory>();
                await connectionFactory.InitializeDatabaseAsync();
                logger?.LogInformation("[App] 資料庫初始化完成");
            }
            catch (Exception dbEx)
            {
                logger?.LogWarning(dbEx, "[App] 資料庫初始化失敗，繼續使用內存存儲");
            }

            // 依「目前模型」載入模號 ONNX 辨識模型(讓辨識器一啟動就指向使用者選的本地模型)。
            // 失敗不可阻斷啟動;若無 current model,Phase 2 的 baseline 預載已覆蓋。
            splash.UpdateStatus("正在載入模號模型...");
            try
            {
                var modelConfigService = _host.Services.GetRequiredService<ModelConfigService>();
                var modelSwitch = _host.Services.GetService<IMoldCodeModelSwitch>();
                var onnxBaseline = _host.Services.GetRequiredService<IOptions<MoldCodeOnnxOptions>>().Value;

                var current = await modelConfigService.GetCurrentModelAsync();
                if (modelSwitch != null && current != null &&
                    !string.IsNullOrWhiteSpace(current.ModelPath) &&
                    current.ModelPath.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
                {
                    modelSwitch.LoadModel(current.ModelPath, current.ResultTypes, onnxBaseline.CodePrefix);
                    logger?.LogInformation("[App] 已載入目前模號模型: {Path}", current.ModelPath);
                }
                else
                {
                    logger?.LogInformation("[App] 無 .onnx 目前模型,沿用 baseline 預載模型");
                }
            }
            catch (Exception modelEx)
            {
                logger?.LogWarning(modelEx, "[App] 載入目前模號模型失敗，沿用 baseline");
            }

            // 載入設定
            splash.UpdateStatus("正在載入設定...");
            await Task.Delay(300); // 最小顯示時間

            // 顯示主視窗
            splash.UpdateStatus("正在啟動主介面...");
            await Task.Delay(200);

            var mainWindow = _host.Services.GetRequiredService<ShellView>();

            // 設定主視窗（ShutdownMode="OnMainWindowClose" 需要此設定）
            MainWindow = mainWindow;

            // 關閉 Splash 並顯示主視窗
            splash.Close();
            mainWindow.Show();

            logger?.LogInformation("[App] 主視窗已顯示");
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "[App] 啟動失敗");
            splash.Close();
            System.Windows.MessageBox.Show(
                $"應用程式啟動失敗：{ex.Message}",
                "錯誤",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }

        base.OnStartup(e);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("[App] OnExit - 開始關閉 Host...");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // 使用超時確保不會無限等待
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            // 停止 Host（會觸發所有 IHostedService 的 StopAsync）
            await _host.StopAsync(cts.Token);
            System.Diagnostics.Debug.WriteLine($"[App] OnExit - Host 已停止，耗時 {sw.ElapsedMilliseconds}ms");
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("[App] OnExit - Host 停止超時 (15秒)");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] OnExit - Host 停止時發生錯誤: {ex.Message}");
        }
        finally
        {
            _host.Dispose();
            System.Diagnostics.Debug.WriteLine($"[App] OnExit - Host 已釋放，總耗時 {sw.ElapsedMilliseconds}ms");
        }

        base.OnExit(e);
    }
}
