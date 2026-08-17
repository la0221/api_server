using System.Diagnostics;
using AIVision.Application.Configuration;
using AIVision.Application.Ports.Devices;
using AIVision.Application.Ports.MoldCode;
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
    private readonly IDefectFilteringService? _defectFilteringService;
    private readonly IModelConfigProvider? _modelConfigProvider;
    private readonly IMoldCodeModelSwitch? _moldCodeModelSwitch;
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
        IDefectFilteringService? defectFilteringService = null,
        IModelConfigProvider? modelConfigProvider = null,
        IMoldCodeModelSwitch? moldCodeModelSwitch = null)
    {
        _plcCommunication = plcCommunication;
        _camera = camera;
        _aiInference = aiInference;
        _logger = logger;
        _light = light;
        _serialLight = serialLight;
        _switchablePort = switchablePort;
        _workflowService = workflowService;
        _defectFilteringService = defectFilteringService;
        _modelConfigProvider = modelConfigProvider;
        _moldCodeModelSwitch = moldCodeModelSwitch;
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

            // 本站推論已全面本地化(本地 ONNX):專案載入時不再呼叫 EdgeHub(LoadWorkflow/StopAsync)
            // 也不再把端點推進 HTTP SwitchableAiInferencePort —— 避免無伺服器時阻塞專案載入。
            // 若模型路徑為本地 .onnx,直接切換本地辨識器(快速,fail-safe);否則僅記錄不做遠端動作。
            _ = isWorkflow;          // 保留語意:Workflow/SingleModel 在本地化後不影響推論路徑
            _ = workflowSettingPath; // 遠端 Workflow 設定在本地化後不使用
            _ = edgeHubPort;
            _ = workflowPort;
            _ = inferencePort;

            if (!string.IsNullOrWhiteSpace(modelPath) &&
                modelPath.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
            {
                if (_moldCodeModelSwitch != null)
                {
                    // classNames/codePrefix 留空 → 沿用 baseline(SwitchableMoldCodeRecognizer 內部處理)。
                    _moldCodeModelSwitch.LoadModel(modelPath, Array.Empty<string>(), string.Empty);
                    _logger.LogInformation("✓ 已切換本地 ONNX 模型: {Path}", modelPath);
                }
                else
                {
                    _logger.LogWarning("IMoldCodeModelSwitch 未註冊,跳過本地模型切換: {Path}", modelPath);
                }
            }
            else
            {
                _logger.LogInformation(
                    "模型非本地 .onnx(或未設定路徑),不做遠端載入,沿用啟動時載入的本地模型。Host={Host}",
                    edgeHubHost);
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
