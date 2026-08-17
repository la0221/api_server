using System.Diagnostics;
using System.Text.Json;
using AIVision.Application.Configuration;
using AIVision.Application.Models;
using AIVision.Application.Ports.Devices;
using AIVision.Application.Ports.Services;
using AIVision.Application.Services;
using AIVision.Infrastructure.AiService;
using AIVision.Infrastructure.Devices.Light;
using Microsoft.Extensions.Logging;

namespace AIVision.Infrastructure.Services;

/// <summary>
/// 專案初始化服務實作 - 根據專案設定並行初始化所有硬體設備
/// </summary>
public sealed class ProjectInitializationService : IProjectInitializationService
{
    private readonly IPlcCommunicationPort _plcCommunication;
    private readonly ICameraPort _camera;
    private readonly ILightPort? _light;
    private readonly LtsSerialLightPort? _serialLight;
    private readonly IAiInferencePort _aiInference;
    private readonly SwitchableAiInferencePort? _switchablePort;
    private readonly IWorkflowService? _workflowService;
    private readonly ILineScanService? _lineScanService;
    private readonly IDefectFilteringService? _defectFilteringService;
    private readonly IModelConfigProvider? _modelConfigProvider;
    private readonly ILogger<ProjectInitializationService> _logger;

    private ProjectConfig? _currentProject;
    private ModelConfigInfo? _loadedModelConfig;

    public ProjectInitializationStatus Status { get; } = new();

    public event EventHandler<ProjectInitializationStatus>? StatusChanged;

    public ProjectInitializationService(
        IPlcCommunicationPort plcCommunication,
        ICameraPort camera,
        IAiInferencePort aiInference,
        ILogger<ProjectInitializationService> logger,
        ILightPort? light = null,
        LtsSerialLightPort? serialLight = null,
        SwitchableAiInferencePort? switchablePort = null,
        IWorkflowService? workflowService = null,
        ILineScanService? lineScanService = null,
        IDefectFilteringService? defectFilteringService = null,
        IModelConfigProvider? modelConfigProvider = null)
    {
        _plcCommunication = plcCommunication;
        _camera = camera;
        _aiInference = aiInference;
        _logger = logger;
        _light = light;
        _serialLight = serialLight;
        _switchablePort = switchablePort;
        _workflowService = workflowService;
        _lineScanService = lineScanService;
        _defectFilteringService = defectFilteringService;
        _modelConfigProvider = modelConfigProvider;
    }

    /// <inheritdoc />
    public async Task<ProjectInitializationResult> InitializeAsync(ProjectConfig project, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        _currentProject = project;

        _logger.LogInformation("===== 開始初始化專案: {ProjectName} =====", project.Name);

        // 重置所有狀態
        Status.ProjectName = project.Name;
        Status.Plc.Reset();
        Status.Camera.Reset();
        Status.Light.Reset();
        Status.AiModel.Reset();
        Status.ProgressPercent = 0;
        Status.CurrentTask = "準備初始化...";
        NotifyStatusChanged();

        // 並行初始化所有設備
        var tasks = new[]
        {
            InitializePlcAsync(project, ct),
            InitializeCameraAsync(project, ct),
            InitializeLightAsync(project, ct),
            InitializeAiModelAsync(project, ct)
        };

        await Task.WhenAll(tasks);

        // Line Scan 初始化（相機初始化完成後才能執行）
        if (Status.Camera.IsSuccess)
        {
            await InitializeLineScanAsync(project, ct);
        }

        // 瑕疵過濾配置載入
        InitializeDefectFiltering(project);

        sw.Stop();

        // 建立結果
        var result = new ProjectInitializationResult
        {
            AllSuccess = Status.IsAllSuccess,
            FailedDevices = Status.GetFailedDevices().ToList(),
            ErrorMessages = Status.GetErrorMessages().ToList(),
            TotalElapsedMs = sw.ElapsedMilliseconds
        };

        UpdateStatus(s =>
        {
            s.ProgressPercent = 100;
            s.CurrentTask = result.AllSuccess ? "初始化完成" : "初始化完成（部分失敗）";
        });

        _logger.LogInformation("===== 專案初始化完成，成功: {AllSuccess}，耗時: {ElapsedMs}ms =====",
            result.AllSuccess, sw.ElapsedMilliseconds);

        return result;
    }

    /// <inheritdoc />
    public async Task RetryFailedDevicesAsync(CancellationToken ct = default)
    {
        if (_currentProject == null)
        {
            _logger.LogWarning("沒有載入的專案，無法重試");
            return;
        }

        _logger.LogInformation("重試失敗的設備...");

        var tasks = new List<Task>();

        if (Status.Plc.IsError)
        {
            Status.Plc.Reset();
            tasks.Add(InitializePlcAsync(_currentProject, ct));
        }

        if (Status.Camera.IsError)
        {
            Status.Camera.Reset();
            tasks.Add(InitializeCameraAsync(_currentProject, ct));
        }

        if (Status.Light.IsError)
        {
            Status.Light.Reset();
            tasks.Add(InitializeLightAsync(_currentProject, ct));
        }

        if (Status.AiModel.IsError)
        {
            Status.AiModel.Reset();
            tasks.Add(InitializeAiModelAsync(_currentProject, ct));
        }

        NotifyStatusChanged();

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks);
        }

        _logger.LogInformation("重試完成，成功: {AllSuccess}", Status.IsAllSuccess);
    }

    /// <inheritdoc />
    public async Task StopAllAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("停止所有設備連線...");

        try
        {
            // 斷開 PLC
            if (_plcCommunication.IsConnected)
            {
                await _plcCommunication.DisconnectAsync();
                _logger.LogInformation("PLC 已斷開連線");
            }

            // 關閉相機
            if (_camera.IsOpen)
            {
                await _camera.DisposeAsync();
                _logger.LogInformation("相機已關閉");
            }

            // 停止光源
            if (_light is IAsyncDisposable disposableLight)
            {
                await disposableLight.DisposeAsync();
                _logger.LogInformation("光源已停止");
            }

            // 重置狀態
            Status.Plc.State = DeviceInitState.Pending;
            Status.Camera.State = DeviceInitState.Pending;
            Status.Light.State = DeviceInitState.Pending;
            Status.AiModel.State = DeviceInitState.Pending;
            NotifyStatusChanged();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "停止設備時發生錯誤");
        }
    }

    #region Private Methods

    private async Task InitializePlcAsync(ProjectConfig project, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        UpdateStatus(s =>
        {
            s.Plc.State = DeviceInitState.InProgress;
            s.CurrentTask = "正在連接 PLC...";
            s.ProgressPercent = 10;
        });

        try
        {
            var plcConfig = project.Plc;
            if (plcConfig == null)
            {
                _logger.LogInformation("專案未配置 PLC，跳過");
                UpdateStatus(s =>
                {
                    s.Plc.State = DeviceInitState.Skipped;
                    s.Plc.CompletedAt = DateTime.Now;
                });
                return;
            }

            _logger.LogInformation("連接 PLC: {Host}:{Port}", plcConfig.Host, plcConfig.Port);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            await _plcCommunication.ConnectAsync(plcConfig.Host, plcConfig.Port, timeoutCts.Token);

            if (!_plcCommunication.IsConnected)
            {
                throw new InvalidOperationException("PLC 連線建立失敗");
            }

            sw.Stop();

            UpdateStatus(s =>
            {
                s.Plc.State = DeviceInitState.Success;
                s.Plc.CompletedAt = DateTime.Now;
                s.Plc.ElapsedMs = sw.ElapsedMilliseconds;
                s.ProgressPercent = Math.Max(s.ProgressPercent, 25);
            });

            _logger.LogInformation("✓ PLC 連線成功，耗時: {ElapsedMs}ms", sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogWarning("PLC 初始化已取消");
            UpdateStatus(s =>
            {
                s.Plc.State = DeviceInitState.Error;
                s.Plc.ErrorMessage = "已取消";
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PLC 連線失敗");
            UpdateStatus(s =>
            {
                s.Plc.State = DeviceInitState.Error;
                s.Plc.ErrorMessage = ex.Message;
                s.Plc.CompletedAt = DateTime.Now;
                s.Plc.ElapsedMs = sw.ElapsedMilliseconds;
            });
        }
    }

    private async Task InitializeCameraAsync(ProjectConfig project, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        UpdateStatus(s =>
        {
            s.Camera.State = DeviceInitState.InProgress;
            s.CurrentTask = "正在初始化相機...";
            s.ProgressPercent = Math.Max(s.ProgressPercent, 30);
        });

        try
        {
            var cameraConfig = project.Camera;
            var userSet = cameraConfig?.UserSet ?? "UserSet0";

            _logger.LogInformation("初始化相機，UserSet: {UserSet}", userSet);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

            // 開啟相機
            if (!_camera.IsOpen)
            {
                // 使用空字串作為 deviceId 表示開啟第一台相機
                await _camera.OpenAsync(string.Empty, timeoutCts.Token);
            }

            // 載入 UserSet（如果相機支援）
            // 注意：具體實作需要根據 ICameraPort 的 API
            // await _camera.LoadUserSetAsync(userSet, timeoutCts.Token);

            sw.Stop();

            UpdateStatus(s =>
            {
                s.Camera.State = DeviceInitState.Success;
                s.Camera.CompletedAt = DateTime.Now;
                s.Camera.ElapsedMs = sw.ElapsedMilliseconds;
                s.ProgressPercent = Math.Max(s.ProgressPercent, 50);
            });

            _logger.LogInformation("✓ 相機初始化成功，UserSet: {UserSet}，耗時: {ElapsedMs}ms",
                userSet, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogWarning("相機初始化已取消");
            UpdateStatus(s =>
            {
                s.Camera.State = DeviceInitState.Error;
                s.Camera.ErrorMessage = "已取消";
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "相機初始化失敗");
            UpdateStatus(s =>
            {
                s.Camera.State = DeviceInitState.Error;
                s.Camera.ErrorMessage = ex.Message;
                s.Camera.CompletedAt = DateTime.Now;
                s.Camera.ElapsedMs = sw.ElapsedMilliseconds;
            });
        }
    }

    private async Task InitializeLightAsync(ProjectConfig project, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        UpdateStatus(s =>
        {
            s.Light.State = DeviceInitState.InProgress;
            s.CurrentTask = "正在連接光源...";
            s.ProgressPercent = Math.Max(s.ProgressPercent, 55);
        });

        try
        {
            var lightConfig = project.Light;
            if (lightConfig == null)
            {
                _logger.LogInformation("專案未配置光源，跳過");
                UpdateStatus(s =>
                {
                    s.Light.State = DeviceInitState.Skipped;
                    s.Light.CompletedAt = DateTime.Now;
                });
                return;
            }

            _logger.LogInformation("連接光源: {Interface}, PortName: {PortName}",
                lightConfig.Interface, lightConfig.PortName);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            ILightPort? activeLightPort = null;

            // 根據介面類型選擇光源控制器
            if (lightConfig.IsSerial)
            {
                // RS232 串口模式
                if (_serialLight == null)
                {
                    throw new InvalidOperationException("RS232 光源控制器未註冊");
                }

                var portName = lightConfig.PortName ?? "COM1";
                _logger.LogInformation("使用 RS232 光源控制器: {PortName}", portName);

                var connected = await _serialLight.ConnectAsync(portName, 19200, timeoutCts.Token);
                if (!connected)
                {
                    throw new InvalidOperationException($"無法連接到串口 {portName}");
                }

                activeLightPort = _serialLight;
            }
            else
            {
                // TCP 模式
                if (_light == null)
                {
                    throw new InvalidOperationException("TCP 光源控制器未註冊");
                }

                _logger.LogInformation("使用 TCP 光源控制器");
                activeLightPort = _light;
            }

            // 讀取光源狀態以確認連線
            var lightState = await activeLightPort.GetStateAsync(timeoutCts.Token);
            if (!lightState.IsConnected)
            {
                throw new InvalidOperationException("光源未連線");
            }

            // 設定各通道亮度（如果專案有配置）
            if (lightConfig.ChannelBrightness != null)
            {
                // 收集每個通道的亮度設定
                var channelBrightnessMap = new Dictionary<int, int>();

                foreach (var (channelStr, brightness) in lightConfig.ChannelBrightness)
                {
                    if (int.TryParse(channelStr, out var channel))
                    {
                        await activeLightPort.SetIntensityAsync(channel, brightness, timeoutCts.Token);
                        _logger.LogInformation("設定光源通道 {Channel} 亮度: {Brightness}", channel, brightness);

                        // 記錄每個通道的亮度
                        channelBrightnessMap[channel] = brightness;
                    }
                }

                // ===== 關鍵：更新 LtsSerialLightPort 的亮度控制設定 =====
                // 讓 Auto Run 使用專案配置的亮度，而非 appsettings.json 的預設值
                if (_serialLight != null && channelBrightnessMap.Count > 0)
                {
                    _serialLight.UpdateBrightnessControl(channelBrightnessMap, idleBrightness: 0);
                    _logger.LogInformation("✓ Auto Run 亮度控制已同步專案設定");
                }
            }

            sw.Stop();

            UpdateStatus(s =>
            {
                s.Light.State = DeviceInitState.Success;
                s.Light.CompletedAt = DateTime.Now;
                s.Light.ElapsedMs = sw.ElapsedMilliseconds;
                s.ProgressPercent = Math.Max(s.ProgressPercent, 75);
            });

            _logger.LogInformation("✓ 光源連線成功 ({Interface})，耗時: {ElapsedMs}ms",
                lightConfig.Interface, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogWarning("光源初始化已取消");
            UpdateStatus(s =>
            {
                s.Light.State = DeviceInitState.Error;
                s.Light.ErrorMessage = "已取消";
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "光源連線失敗");
            UpdateStatus(s =>
            {
                s.Light.State = DeviceInitState.Error;
                s.Light.ErrorMessage = ex.Message;
                s.Light.CompletedAt = DateTime.Now;
                s.Light.ElapsedMs = sw.ElapsedMilliseconds;
            });
        }
    }

    private async Task InitializeAiModelAsync(ProjectConfig project, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        UpdateStatus(s =>
        {
            s.AiModel.State = DeviceInitState.InProgress;
            s.CurrentTask = "正在載入 AI 模型...";
            s.ProgressPercent = Math.Max(s.ProgressPercent, 80);
        });

        try
        {
            // ===== 新架構：優先從 ModelName 載入模型配置 =====
            ModelConfigInfo? modelConfig = null;

            if (!string.IsNullOrEmpty(project.ModelName) && _modelConfigProvider != null)
            {
                _logger.LogInformation("從模型管理載入模型: {ModelName}", project.ModelName);
                modelConfig = await _modelConfigProvider.GetModelByNameAsync(project.ModelName);

                if (modelConfig != null)
                {
                    _logger.LogInformation("✓ 找到模型配置: {Name}, 類型: {Type}",
                        modelConfig.Name, modelConfig.InferenceType);
                    _loadedModelConfig = modelConfig;
                }
                else
                {
                    _logger.LogWarning("找不到模型: {ModelName}，嘗試使用專案內建設定 (Fallback)", project.ModelName);
                }
            }

            // ===== Fallback：使用專案內建的 AiModel 設定 =====
            string edgeHubHost;
            int edgeHubPort;
            int workflowPort;
            int inferencePort;
            string? workflowSettingPath;
            string? modelPath;
            bool isWorkflow;

            if (modelConfig != null)
            {
                // 使用模型管理的設定
                edgeHubHost = modelConfig.EdgeHubHost;
                edgeHubPort = modelConfig.EdgeHubPort;
                workflowPort = modelConfig.WorkflowPort;
                inferencePort = modelConfig.InferencePort;
                workflowSettingPath = modelConfig.WorkflowSettingPath;
                modelPath = modelConfig.ModelPath;
                isWorkflow = modelConfig.IsWorkflow;

                _logger.LogInformation("使用模型管理設定: Host={Host}, Port={Port}",
                    edgeHubHost, isWorkflow ? workflowPort : inferencePort);
            }
            else if (project.AiModel != null)
            {
                // Fallback: 使用專案內建設定
                var aiConfig = project.AiModel;
                edgeHubHost = aiConfig.EdgeHubHost;
                edgeHubPort = aiConfig.EdgeHubPort;
                workflowPort = aiConfig.WorkflowPort;
                inferencePort = aiConfig.InferencePort;
                workflowSettingPath = aiConfig.WorkflowSettingPath;
                modelPath = aiConfig.ModelPath;
                isWorkflow = aiConfig.IsWorkflow;

                _logger.LogInformation("使用專案內建 AI 模型設定 (Fallback 模式)");
            }
            else
            {
                _logger.LogInformation("專案未配置 AI 模型，跳過");
                UpdateStatus(s =>
                {
                    s.AiModel.State = DeviceInitState.Skipped;
                    s.AiModel.CompletedAt = DateTime.Now;
                });
                return;
            }

            _logger.LogInformation("載入 AI 模型: {Type}", isWorkflow ? "Workflow" : "SingleModel");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(60)); // AI 模型載入可能較慢

            // 根據類型載入 Workflow 或 SingleModel
            if (isWorkflow)
            {
                _logger.LogInformation("載入 Workflow: Host={Host}, Port={Port}, Path={Path}",
                    edgeHubHost, workflowPort, workflowSettingPath);

                // 使用 WorkflowService 呼叫 EdgeHub API 載入 Workflow
                if (_workflowService != null && !string.IsNullOrWhiteSpace(workflowSettingPath))
                {
                    // 先關閉現有服務（與模型管理一致）
                    _logger.LogInformation("載入新 Workflow 前，先關閉現有服務...");
                    var stopResult = await _workflowService.StopAsync(timeoutCts.Token);
                    if (stopResult)
                    {
                        _logger.LogInformation("現有服務已關閉");
                    }
                    else
                    {
                        _logger.LogWarning("關閉現有服務失敗（繼續載入）");
                    }

                    // 等待一下讓服務完全關閉
                    await Task.Delay(500, timeoutCts.Token);

                    // 載入 Workflow
                    var loadResult = await _workflowService.LoadWorkflowAsync(
                        edgeHubHost,
                        edgeHubPort,
                        workflowPort,
                        workflowSettingPath,
                        timeoutCts.Token);

                    if (!loadResult)
                    {
                        throw new InvalidOperationException($"Workflow 載入失敗: {workflowSettingPath}");
                    }

                    _logger.LogInformation("✓ Workflow 已透過 EdgeHub API 載入成功");
                }
                else if (_workflowService == null)
                {
                    _logger.LogWarning("WorkflowService 未註冊，跳過 EdgeHub API 呼叫（模擬模式）");
                }

                // 同步推論端點到 SwitchableAiInferencePort
                if (_switchablePort != null)
                {
                    _switchablePort.WorkflowPort.SetEndpoint(edgeHubHost, workflowPort);
                    _switchablePort.SwitchTo(InferenceType.Workflow);
                    _logger.LogInformation("✓ 已同步 Workflow 端點: {Host}:{Port}",
                        edgeHubHost, workflowPort);
                }
            }
            else
            {
                _logger.LogInformation("載入單一模型: Host={Host}, Port={Port}, Path={Path}",
                    edgeHubHost, inferencePort, modelPath);

                // 單一模型模式目前暫不透過專案載入呼叫 API（需要 uuid）
                // 使用者應透過 ModelManagementView 載入
                _logger.LogWarning("單一模型模式需透過「模型管理」手動載入（需要 UUID）");

                // 同步推論端點到 SwitchableAiInferencePort
                if (_switchablePort != null)
                {
                    _switchablePort.SwitchTo(InferenceType.SingleModel);
                    _logger.LogInformation("✓ 已切換到單一模型模式");
                }
            }

            sw.Stop();

            UpdateStatus(s =>
            {
                s.AiModel.State = DeviceInitState.Success;
                s.AiModel.CompletedAt = DateTime.Now;
                s.AiModel.ElapsedMs = sw.ElapsedMilliseconds;
                s.ProgressPercent = Math.Max(s.ProgressPercent, 95);
            });

            _logger.LogInformation("✓ AI 模型載入成功，類型: {Type}，耗時: {ElapsedMs}ms",
                isWorkflow ? "Workflow" : "SingleModel", sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogWarning("AI 模型載入已取消");
            UpdateStatus(s =>
            {
                s.AiModel.State = DeviceInitState.Error;
                s.AiModel.ErrorMessage = "已取消";
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI 模型載入失敗");
            UpdateStatus(s =>
            {
                s.AiModel.State = DeviceInitState.Error;
                s.AiModel.ErrorMessage = ex.Message;
                s.AiModel.CompletedAt = DateTime.Now;
                s.AiModel.ElapsedMs = sw.ElapsedMilliseconds;
            });
        }
    }

    private async Task InitializeLineScanAsync(ProjectConfig project, CancellationToken ct)
    {
        if (_lineScanService == null)
        {
            _logger.LogInformation("Line Scan 服務未註冊，跳過初始化");
            return;
        }

        _logger.LogInformation("初始化 Line Scan 設定...");

        try
        {
            // 1. 載入 Line Scan 配置（從專案設定或預設值）
            var lineScanSettings = await LoadLineScanSettingsAsync(project, ct);

            if (lineScanSettings != null)
            {
                // 如果載入的設定沒有 UserSet，使用專案配置
                if (string.IsNullOrEmpty(lineScanSettings.UserSetName) || lineScanSettings.UserSetName == "Linescan")
                {
                    lineScanSettings = lineScanSettings with
                    {
                        UserSetName = project.Camera?.UserSet ?? "UserSet0"
                    };
                    _logger.LogInformation("  設定未包含 UserSet，使用專案配置: {UserSet}", lineScanSettings.UserSetName);
                }

                _logger.LogInformation("  找到 Line Scan 設定: Width={Width}, Height={Height}, LineRate={LineRate}, UserSet={UserSet}",
                    lineScanSettings.Width, lineScanSettings.TargetHeight, lineScanSettings.LineRate, lineScanSettings.UserSetName);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

                // 2. 配置並啟動 Line Scan（不實際觸發取像）
                await _lineScanService.ConfigureAndStartAsync(lineScanSettings, timeoutCts.Token);

                _logger.LogInformation("✓ Line Scan 初始化成功");
            }
            else
            {
                _logger.LogWarning("未找到 Line Scan 設定，使用預設配置");

                // 使用預設設定，但 UserSet 從專案配置讀取
                var defaultSettings = new LineScanRoiSettings
                {
                    OffsetX = 0,
                    OffsetY = 0,
                    Width = 1200,
                    TargetHeight = 2000,
                    LineRate = 1000,
                    UserSetName = project.Camera?.UserSet ?? "UserSet0"  // 從專案配置讀取 UserSet
                };

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

                await _lineScanService.ConfigureAndStartAsync(defaultSettings, timeoutCts.Token);
                _logger.LogInformation("✓ Line Scan 使用預設設定初始化成功 (UserSet: {UserSet})", defaultSettings.UserSetName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Line Scan 初始化失敗，稍後可手動設定");
            // 不拋出異常，允許專案繼續載入
        }
    }

    private async Task<LineScanRoiSettings?> LoadLineScanSettingsAsync(ProjectConfig project, CancellationToken ct)
    {
        // 方案 1: 從專案配置載入（未來擴充）
        // if (project.LineScanSettings != null)
        // {
        //     return project.LineScanSettings;
        // }

        // 方案 2: 從獨立的設定檔載入
        // 先嘗試從桌面的 Portable 資料夾載入
        var portablePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "AIVision-Portable121507",
            "linescan-settings.json");

        if (File.Exists(portablePath))
        {
            return await LoadLineScanSettingsFromFileAsync(portablePath, ct);
        }

        // 再嘗試從本機 AppData 載入
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIVision",
            "linescan-settings.json");

        if (File.Exists(appDataPath))
        {
            return await LoadLineScanSettingsFromFileAsync(appDataPath, ct);
        }

        // 嘗試從當前專案目錄載入
        if (project.Name != null)
        {
            var projectPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "AIVision-Portable121507",
                "Projects",
                project.Name,
                "linescan-settings.json");

            if (File.Exists(projectPath))
            {
                return await LoadLineScanSettingsFromFileAsync(projectPath, ct);
            }
        }

        return null;
    }

    private async Task<LineScanRoiSettings?> LoadLineScanSettingsFromFileAsync(string filePath, CancellationToken ct)
    {
        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct);
            var settings = JsonSerializer.Deserialize<LineScanRoiSettings>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            _logger.LogInformation("  從檔案載入 Line Scan 設定: {Path}", filePath);
            return settings;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "載入 Line Scan 設定檔失敗: {Path}", filePath);
            return null;
        }
    }

    /// <summary>
    /// 初始化瑕疵過濾配置
    /// </summary>
    private void InitializeDefectFiltering(ProjectConfig project)
    {
        if (_defectFilteringService == null)
        {
            _logger.LogInformation("瑕疵過濾服務未註冊，跳過配置");
            return;
        }

        DefectFilteringOptions? options = null;

        // ===== 優先從模型配置載入瑕疵過濾設定 =====
        if (_loadedModelConfig != null && _loadedModelConfig.DefectFilteringEnabled)
        {
            _logger.LogInformation("從模型 {ModelName} 載入瑕疵過濾設定", _loadedModelConfig.Name);

            options = new DefectFilteringOptions
            {
                Enabled = _loadedModelConfig.DefectFilteringEnabled,
                PixelAreaMm2 = _loadedModelConfig.PixelAreaMm2,
                PixelSizeMm = _loadedModelConfig.PixelSizeMm,
                MinimumAreaMm2 = _loadedModelConfig.MinimumAreaMm2,
                MediumAreaMm2 = _loadedModelConfig.MediumAreaMm2,
                CloseDistanceMm = _loadedModelConfig.CloseDistanceMm,
                CriticalClasses = _loadedModelConfig.CriticalClasses.ToList()
            };
        }
        // ===== Fallback：使用專案內建設定 =====
        else if (project.DefectFiltering != null)
        {
            _logger.LogInformation("使用專案內建瑕疵過濾設定");
            options = project.DefectFiltering.ToOptions();
        }

        if (options == null)
        {
            _logger.LogInformation("專案未配置瑕疵過濾規則，使用預設值");
            return;
        }

        try
        {
            _defectFilteringService.UpdateOptions(options);

            _logger.LogInformation("✓ 瑕疵過濾配置已載入: Enabled={Enabled}, MinArea={MinArea}mm², MediumArea={MediumArea}mm², CloseDistance={Distance}mm",
                options.Enabled, options.MinimumAreaMm2, options.MediumAreaMm2, options.CloseDistanceMm);

            if (options.CriticalClasses.Count > 0)
            {
                _logger.LogInformation("  關鍵瑕疵類別: {Classes}",
                    string.Join(", ", options.CriticalClasses));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "載入瑕疵過濾配置失敗");
        }
    }

    private void UpdateStatus(Action<ProjectInitializationStatus> update)
    {
        update(Status);
        NotifyStatusChanged();
    }

    private void NotifyStatusChanged()
    {
        StatusChanged?.Invoke(this, Status);
    }

    #endregion
}
