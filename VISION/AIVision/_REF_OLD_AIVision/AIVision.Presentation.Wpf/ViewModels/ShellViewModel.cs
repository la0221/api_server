using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIVision.Application.Configuration;
using AIVision.Application.Contracts.Camera;
using AIVision.Application.Contracts.WorkOrder;
using AIVision.Application.Inspection.Commands;
using AIVision.Application.Ports.Devices;
using AIVision.Application.Ports.Persistence;
using AIVision.Application.Ports.Services;
using AIVision.Application.Services;
using AIVision.Domain.AutoRun;
using AIVision.Domain.Entities;
using AIVision.Domain.Plc;
using AIVision.Domain.Shared;
using AIVision.Domain.User;
using AIVision.Presentation.Wpf.Models;
using AIVision.Presentation.Wpf.Services;
using AIVision.Presentation.Wpf.Services.Navigation;
using AIVision.Presentation.Wpf.Views;
using AIVision.Presentation.Wpf.Utilities;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AIVision.Presentation.Wpf.ViewModels;

public partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly ISender _sender;
    private readonly INavigationService _navigationService;
    private readonly IMessenger _messenger;
    private readonly ModelConfigService _modelConfigService;
    private readonly IWorkOrderManagementService _workOrderService;
    private readonly IInspectionRepository _inspectionRepository;
    private readonly IInspectionImageService _imageService;
    private readonly DispatcherTimer _clockTimer;
    private readonly Dispatcher _dispatcher;

    // PLC 相關服務
    private readonly IPlcCommunicationPort _plcCommunication;
    private readonly IPlcHandshakePort _plcHandshake;
    private readonly ICameraPort _camera;
    private readonly IAiInferencePort _aiInference;
    private readonly ILogger<ShellViewModel>? _logger;

    // Auto Run 服務 (Line Scan 模式)
    private readonly IAutoRunService? _autoRunService;
    private readonly ILineScanService? _lineScanService;

    // IoPanel 輪詢控制（Auto Run 時需停止輪詢避免 PLC 競爭條件）
    private readonly IoPanelViewModel? _ioPanelViewModel;

    // 光源控制器（需在關閉時釋放資源）
    private readonly ILightPort? _lightPort;

    // 專案初始化服務
    private readonly IProjectConfigService? _projectConfigService;
    private readonly IProjectInitializationService? _projectInitService;

    // Overlay 渲染器（用於 Area Scan 瑕疵標註）
    private readonly IContourOverlayRenderer? _overlayRenderer;

    // 瑕疵過濾服務
    private readonly IDefectFilteringService? _defectFilteringService;

    // 認證服務
    private readonly IAuthService _authService;

    // 設定
    private readonly IConfiguration _configuration;

    // PLC 模式控制
    private bool _isRunOnceMode;
    private bool _isPlcEventSubscribed;
    private bool _isAutoRunEventSubscribed;

    // Dispose 保護
    private bool _disposed;

    public ShellViewModel(
        ISender sender,
        INavigationService navigationService,
        IMessenger messenger,
        ModelConfigService modelConfigService,
        IWorkOrderManagementService workOrderService,
        IInspectionRepository inspectionRepository,
        IInspectionImageService imageService,
        IPlcCommunicationPort plcCommunication,
        IPlcHandshakePort plcHandshake,
        ICameraPort camera,
        IAiInferencePort aiInference,
        IConfiguration configuration,
        ILogger<ShellViewModel>? logger = null,
        IAutoRunService? autoRunService = null,
        ILineScanService? lineScanService = null,
        IoPanelViewModel? ioPanelViewModel = null,
        ILightPort? lightPort = null,
        IProjectConfigService? projectConfigService = null,
        IProjectInitializationService? projectInitService = null,
        IContourOverlayRenderer? overlayRenderer = null,
        IDefectFilteringService? defectFilteringService = null,
        IAuthService? authService = null)
    {
        _sender = sender;
        _navigationService = navigationService;
        _messenger = messenger;
        _modelConfigService = modelConfigService;
        _workOrderService = workOrderService;
        _inspectionRepository = inspectionRepository;
        _imageService = imageService;
        _plcCommunication = plcCommunication;
        _plcHandshake = plcHandshake;
        _camera = camera;
        _aiInference = aiInference;
        _configuration = configuration;
        _logger = logger;
        _autoRunService = autoRunService;
        _lineScanService = lineScanService;
        _ioPanelViewModel = ioPanelViewModel;
        _lightPort = lightPort;
        _projectConfigService = projectConfigService;
        _projectInitService = projectInitService;
        _overlayRenderer = overlayRenderer;
        _defectFilteringService = defectFilteringService;
        _authService = authService!;
        _dispatcher = Dispatcher.CurrentDispatcher;

        // 訂閱認證狀態變更
        if (_authService != null)
        {
            _authService.LoginStateChanged += OnLoginStateChanged;
            UpdateAuthDisplayProperties();
        }

        // 移除假數據，改為空集合
        Defects = new ObservableCollection<DefectItemViewModel>();

        _clockTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _clockTimer.Tick += (_, _) => OnPropertyChanged(nameof(CurrentTimeDisplay));
        _clockTimer.Start();

        // 注意：主頁的 LiveBitmap 現在由 IdsCameraPort.FrameReceived 事件更新
        // 不再訂閱 CameraTestView 的 CameraCaptureMessage

        // 訂閱工單變更訊息
        _messenger.Register<WorkOrderChangedMessage>(this, async (_, _) =>
        {
            await _dispatcher.InvokeAsync(async () =>
            {
                await LoadCurrentWorkOrderAsync();
            });
        });

        // 載入當前模型名稱
        _ = LoadCurrentModelAsync();

        // 載入當前工單
        _ = LoadCurrentWorkOrderAsync();

        // 初始化瑕疵統計（非同步）
        _ = InitializeDefectStatsAsync();

        // 訂閱 PLC 連線狀態變更（用於同步顯示，不控制連線）
        _plcCommunication.ConnectionStateChanged += OnPlcConnectionStateChanged;

        // 初始化 PLC 狀態顯示
        IsPlcConnected = _plcCommunication.IsConnected;
        PlcStatusText = _plcCommunication.IsConnected
            ? (_plcHandshake.IsRunning ? "PLC 已連線 (交握中)" : "PLC 已連線 (待命)")
            : "PLC 未連線";

        // 訂閱專案初始化狀態變更事件
        if (_projectInitService != null)
        {
            _projectInitService.StatusChanged += OnProjectInitStatusChanged;
        }

        // 訂閱專案變更事件
        if (_projectConfigService != null)
        {
            _projectConfigService.CurrentProjectChanged += OnCurrentProjectChanged;
        }

        // 初始化硬體狀態燈顏色
        InitializeDeviceStatusColors();
    }

    /// <summary>
    /// 相機幀接收處理（用於主頁即時預覽）
    /// </summary>
    private void OnCameraFrameReceived(object? sender, ImageData imageData)
    {
        _dispatcher.BeginInvoke(() =>
        {
            try
            {
                LiveBitmap = BitmapSourceFactory.FromImageData(imageData);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "更新預覽影像失敗");
            }
        });
    }

    /// <summary>
    /// PLC 連線狀態變更時更新顯示（由 IoPanelView 控制的連線）
    /// </summary>
    private void OnPlcConnectionStateChanged(object? sender, bool connected)
    {
        _dispatcher.BeginInvoke(() =>
        {
            IsPlcConnected = connected;

            if (!connected)
            {
                // Auto Run 期間的短暫斷線不立即停止，讓 PlcHandshakeService 嘗試重連
                // PlcHandshakeService 有內建的重連機制，會在 3 次重連失敗後才真正放棄
                if (IsRunning)
                {
                    _logger?.LogInformation("PLC 連線中斷，等待自動重連（不停止 Auto Run）");
                    PlcStatusText = "PLC 重連中...";
                }
                else
                {
                    PlcStatusText = "PLC 未連線";
                }
            }
            else
            {
                // 重連成功
                if (IsRunning)
                {
                    _logger?.LogInformation("PLC 重連成功，Auto Run 繼續運行");
                }
                PlcStatusText = _plcHandshake.IsRunning ? "PLC 已連線 (交握中)" : "PLC 已連線 (待命)";
            }
        });
    }

    private async System.Threading.Tasks.Task LoadCurrentModelAsync()
    {
        try
        {
            var config = await _modelConfigService.LoadAsync();
            CurrentModelName = config.CurrentModelName ?? "未選擇";
        }
        catch
        {
            CurrentModelName = "未選擇";
        }
    }

    public ObservableCollection<DefectItemViewModel> Defects { get; }

    [ObservableProperty]
    private string currentUserDisplay = "administrator";

    [ObservableProperty]
    private string productName = "未設定";

    [ObservableProperty]
    private string orderCode = "無工單";

    [ObservableProperty]
    private bool isModelLoaded = true;

    [ObservableProperty]
    private string? currentModelName;

    [ObservableProperty]
    private bool isRunning;

    [ObservableProperty]
    private bool isPlcConnected;

    [ObservableProperty]
    private string plcStatusText = "PLC 未連線";

    // Auto Run 相關屬性
    [ObservableProperty]
    private AutoRunState autoRunState = AutoRunState.Idle;

    [ObservableProperty]
    private string autoRunStateText = "待機";

    [ObservableProperty]
    private bool isLineScanMode = true;  // 專案只使用 Line Scan 模式

    [ObservableProperty]
    private int autoRunTotalCount;

    [ObservableProperty]
    private int autoRunOkCount;

    [ObservableProperty]
    private int autoRunNgCount;

    [ObservableProperty]
    private string autoRunYieldRate = "0.00%";

    [ObservableProperty]
    private string lastResultStatus = "OK";

    [ObservableProperty]
    private bool isLastResultOk = true;

    [ObservableProperty]
    private float? lastResultConfidence = 0.98f;

    [ObservableProperty]
    private int totalCount = 0;

    [ObservableProperty]
    private int ngCount = 0;

    [ObservableProperty]
    private BitmapSource? liveBitmap;

    // ===== 專案載入相關屬性 =====

    /// <summary>
    /// 目前載入的專案名稱
    /// </summary>
    [ObservableProperty]
    private string? currentProjectName;

    // ===== 硬體狀態燈相關屬性 =====

    /// <summary>
    /// PLC 狀態燈顏色
    /// </summary>
    [ObservableProperty]
    private System.Windows.Media.Brush plcStatusColor = System.Windows.Media.Brushes.Gray;

    /// <summary>
    /// PLC 狀態提示
    /// </summary>
    [ObservableProperty]
    private string plcStatusTooltip = "PLC: 未初始化";

    /// <summary>
    /// 相機狀態燈顏色
    /// </summary>
    [ObservableProperty]
    private System.Windows.Media.Brush cameraStatusColor = System.Windows.Media.Brushes.Gray;

    /// <summary>
    /// 相機狀態提示
    /// </summary>
    [ObservableProperty]
    private string cameraStatusTooltip = "相機: 未初始化";

    /// <summary>
    /// 光源狀態燈顏色
    /// </summary>
    [ObservableProperty]
    private System.Windows.Media.Brush lightStatusColor = System.Windows.Media.Brushes.Gray;

    /// <summary>
    /// 光源狀態提示
    /// </summary>
    [ObservableProperty]
    private string lightStatusTooltip = "光源: 未初始化";

    /// <summary>
    /// AI 模型狀態燈顏色
    /// </summary>
    [ObservableProperty]
    private System.Windows.Media.Brush aiModelStatusColor = System.Windows.Media.Brushes.Gray;

    /// <summary>
    /// AI 模型狀態提示
    /// </summary>
    [ObservableProperty]
    private string aiModelStatusTooltip = "AI 模型: 未初始化";

    public string YieldPercentage
    {
        get
        {
            if (TotalCount <= 0)
            {
                return "100%";
            }

            var ok = TotalCount - NgCount;
            var ratio = ok / (double)TotalCount;
            return ratio.ToString("P1", CultureInfo.InvariantCulture);
        }
    }

    public string ModelStateText => IsModelLoaded ? "Model Loaded" : "Model Offline";

    public string CurrentTimeDisplay => DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);

    partial void OnIsModelLoadedChanged(bool value) => OnPropertyChanged(nameof(ModelStateText));

    partial void OnTotalCountChanged(int value) => OnPropertyChanged(nameof(YieldPercentage));

    partial void OnNgCountChanged(int value) => OnPropertyChanged(nameof(YieldPercentage));

    // ===== 專案載入相關 Command =====

    /// <summary>
    /// 開啟專案選擇視窗
    /// </summary>
    [RelayCommand]
    private async Task OpenProjectSelectAsync()
    {
        var result = _navigationService.ShowDialog<ProjectSelectWindow>();
        if (result == true)
        {
            // 專案已選擇，開始載入
            var project = _projectConfigService?.CurrentProject;
            if (project != null)
            {
                await LoadProjectAsync(project);
            }
        }
    }

    /// <summary>
    /// 載入指定專案並初始化硬體
    /// </summary>
    public async Task LoadProjectAsync(ProjectConfig project)
    {
        if (_projectInitService == null)
        {
            _logger?.LogWarning("專案初始化服務未註冊");
            System.Windows.MessageBox.Show("專案初始化服務未設定", "錯誤", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        _logger?.LogInformation("開始載入專案: {ProjectName}", project.Name);

        // 顯示載入進度視窗
        var loadingWindow = _navigationService.CreateWindow<ProjectLoadingWindow>();
        if (loadingWindow?.DataContext is ProjectLoadingViewModel loadingVm)
        {
            loadingVm.SetProject(project);
            loadingVm.RequestClose += (success) =>
            {
                // 使用 Dispatcher.BeginInvoke 確保在 UI 執行緒上執行，
                // 並在 ShowDialog() 完全初始化後才設定 DialogResult
                loadingWindow.Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        loadingWindow.DialogResult = success;
                        loadingWindow.Close();
                    }
                    catch (InvalidOperationException ex)
                    {
                        _logger?.LogWarning(ex, "設定 DialogResult 時發生錯誤（視窗可能尚未完全初始化）");
                        // 如果設定失敗，直接關閉視窗
                        loadingWindow.Close();
                    }
                });
            };

            // 非同步開始初始化，同時顯示視窗
            _ = loadingVm.StartInitializationAsync();
            loadingWindow.ShowDialog();

            // 載入完成後更新顯示
            if (loadingWindow.DialogResult == true)
            {
                CurrentProjectName = project.Name;
                _logger?.LogInformation("專案載入成功: {ProjectName}", project.Name);

                // 從模型配置載入瑕疵過濾設定
                await ApplyModelDefectFilteringAsync(project);
            }
            else
            {
                _logger?.LogWarning("專案載入被取消或失敗: {ProjectName}", project.Name);
            }
        }
    }

    /// <summary>
    /// 從模型配置載入並套用瑕疵過濾設定
    /// </summary>
    private async Task ApplyModelDefectFilteringAsync(ProjectConfig project)
    {
        if (_defectFilteringService == null)
        {
            _logger?.LogDebug("瑕疵過濾服務未註冊，跳過模型設定載入");
            return;
        }

        // 嘗試從 AiModel.ModelPath 或 AiModel.WorkflowSettingPath 推斷模型名稱
        string? modelName = null;

        if (project.AiModel != null)
        {
            if (project.AiModel.IsWorkflow && !string.IsNullOrEmpty(project.AiModel.WorkflowSettingPath))
            {
                // Workflow 模式：從 WorkflowSettingPath 取得目錄名稱作為模型名稱
                modelName = Path.GetFileName(Path.GetDirectoryName(project.AiModel.WorkflowSettingPath));
            }
            else if (!string.IsNullOrEmpty(project.AiModel.ModelPath))
            {
                // SingleModel 模式：從 ModelPath 取得目錄名稱
                modelName = Path.GetFileName(project.AiModel.ModelPath);
            }
        }

        if (string.IsNullOrEmpty(modelName))
        {
            _logger?.LogDebug("專案未指定模型路徑，跳過瑕疵過濾設定");
            return;
        }

        try
        {
            // 嘗試用目錄名稱查找對應的 ModelConfig
            var modelConfig = await _modelConfigService.GetModelByNameAsync(modelName);
            if (modelConfig?.DefectFiltering == null)
            {
                _logger?.LogDebug("模型 {ModelName} 未配置瑕疵過濾設定", modelName);
                return;
            }

            if (!modelConfig.DefectFiltering.Enabled)
            {
                _logger?.LogDebug("模型 {ModelName} 的瑕疵過濾未啟用", modelName);
                return;
            }

            // 轉換並套用設定
            var options = new DefectFilteringOptions
            {
                Enabled = modelConfig.DefectFiltering.Enabled,
                PixelAreaMm2 = modelConfig.DefectFiltering.PixelAreaMm2,
                PixelSizeMm = modelConfig.DefectFiltering.PixelSizeMm,
                MinimumAreaMm2 = modelConfig.DefectFiltering.MinimumAreaMm2,
                MediumAreaMm2 = modelConfig.DefectFiltering.MediumAreaMm2,
                CloseDistanceMm = modelConfig.DefectFiltering.CloseDistanceMm,
                CriticalClasses = modelConfig.DefectFiltering.CriticalClasses
            };

            _defectFilteringService.UpdateOptions(options);

            _logger?.LogInformation("✓ 從模型 {ModelName} 載入瑕疵過濾設定: Enabled={Enabled}, MinArea={MinArea}mm², MediumArea={MediumArea}mm², CloseDistance={Distance}mm",
                modelName, options.Enabled, options.MinimumAreaMm2, options.MediumAreaMm2, options.CloseDistanceMm);

            if (options.CriticalClasses.Count > 0)
            {
                _logger?.LogInformation("  關鍵瑕疵類別: {Classes}",
                    string.Join(", ", options.CriticalClasses));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "從模型 {ModelName} 載入瑕疵過濾設定失敗", modelName);
        }
    }

    // ===== 硬體狀態燈相關方法 =====

    /// <summary>
    /// 初始化硬體狀態燈顏色
    /// </summary>
    private void InitializeDeviceStatusColors()
    {
        PlcStatusColor = System.Windows.Media.Brushes.Gray;
        PlcStatusTooltip = "PLC: 未初始化";

        CameraStatusColor = System.Windows.Media.Brushes.Gray;
        CameraStatusTooltip = "相機: 未初始化";

        LightStatusColor = System.Windows.Media.Brushes.Gray;
        LightStatusTooltip = "光源: 未初始化";

        AiModelStatusColor = System.Windows.Media.Brushes.Gray;
        AiModelStatusTooltip = "AI 模型: 未初始化";
    }

    /// <summary>
    /// 專案初始化狀態變更事件處理
    /// </summary>
    private void OnProjectInitStatusChanged(object? sender, ProjectInitializationStatus status)
    {
        _dispatcher.BeginInvoke(() =>
        {
            // 更新 PLC 狀態
            PlcStatusColor = GetStatusBrush(status.Plc.State);
            PlcStatusTooltip = GetStatusTooltip("PLC", status.Plc);

            // 更新相機狀態
            CameraStatusColor = GetStatusBrush(status.Camera.State);
            CameraStatusTooltip = GetStatusTooltip("相機", status.Camera);

            // 更新光源狀態
            LightStatusColor = GetStatusBrush(status.Light.State);
            LightStatusTooltip = GetStatusTooltip("光源", status.Light);

            // 更新 AI 模型狀態
            AiModelStatusColor = GetStatusBrush(status.AiModel.State);
            AiModelStatusTooltip = GetStatusTooltip("AI 模型", status.AiModel);
        });
    }

    /// <summary>
    /// 專案變更事件處理
    /// </summary>
    private void OnCurrentProjectChanged(object? sender, ProjectConfig? project)
    {
        _dispatcher.BeginInvoke(() =>
        {
            CurrentProjectName = project?.Name;
        });
    }

    /// <summary>
    /// 取得狀態對應的 Brush 顏色
    /// </summary>
    private static System.Windows.Media.Brush GetStatusBrush(DeviceInitState state) => state switch
    {
        DeviceInitState.Success => System.Windows.Media.Brushes.LimeGreen,
        DeviceInitState.Error => System.Windows.Media.Brushes.Red,
        DeviceInitState.InProgress => System.Windows.Media.Brushes.Orange,
        DeviceInitState.Skipped => System.Windows.Media.Brushes.DarkGray,
        _ => System.Windows.Media.Brushes.Gray
    };

    /// <summary>
    /// 取得設備狀態的工具提示文字
    /// </summary>
    private static string GetStatusTooltip(string deviceName, DeviceInitStatus status)
    {
        var stateText = status.State switch
        {
            DeviceInitState.Pending => "等待中",
            DeviceInitState.InProgress => "初始化中...",
            DeviceInitState.Success => $"已連線 ({status.ElapsedMs}ms)",
            DeviceInitState.Error => $"失敗: {status.ErrorMessage}",
            DeviceInitState.Skipped => "已跳過 (未配置)",
            _ => "未知"
        };

        return $"{deviceName}: {stateText}";
    }

    [RelayCommand]
    private async Task RunOnceAsync()
    {
        // 使用 PLC 單次模式
        await StartPlcModeAsync(runOnce: true);
    }

    [RelayCommand]
    private void ToggleModel() => IsModelLoaded = !IsModelLoaded;

    [RelayCommand]
    private void OpenLogin()
    {
        var result = _navigationService.ShowDialog<LoginView>();
        if (result == true)
        {
            UpdateAuthDisplayProperties();
        }
    }

    [RelayCommand]
    private void OpenCamera() => _navigationService.ShowWindow<CameraView>();

    [RelayCommand]
    private void OpenCameraTest() => _navigationService.ShowWindow<CameraTestView>();

    [RelayCommand]
    private void OpenHistory() => _navigationService.ShowWindow<HistoryView>();

    [RelayCommand]
    private void OpenImageBatch() => _navigationService.ShowWindow<ImageBatchView>();

    [RelayCommand]
    private void OpenBatchInference() => _navigationService.ShowWindow<BatchInferenceView>();

    [RelayCommand]
    private void OpenProductionStats() => _navigationService.ShowWindow<ProductionStatsView>();

    [RelayCommand]
    private async System.Threading.Tasks.Task OpenModelSelectorAsync()
    {
        var previousModelName = CurrentModelName;
        var result = _navigationService.ShowDialog<ModelSelectView>();
        if (result == true)
        {
            IsModelLoaded = true;
            await LoadCurrentModelAsync();

            // 如果模型已切換，重新初始化統計
            if (previousModelName != CurrentModelName)
            {
                TotalCount = 0;
                NgCount = 0;
                await InitializeDefectStatsAsync();
            }
        }
    }

    [RelayCommand]
    private void OpenModelManagement()
    {
        _navigationService.ShowWindow<ModelManagementView>();
    }

    [RelayCommand]
    private void OpenWorkOrderManagement() => _navigationService.ShowWindow<WorkOrderManagementView>();

    [RelayCommand]
    private void OpenIoPanel() => _navigationService.ShowWindow<IoPanelView>();

    [RelayCommand]
    private void OpenLightControl() => _navigationService.ShowWindow<LightControlView>();

    [RelayCommand]
    private void OpenLineScan() => _navigationService.ShowWindow<LineScanView>();

    [RelayCommand]
    private void OpenLightDeviceScan() => _navigationService.ShowWindow<LightDeviceScanView>();

    [RelayCommand]
    private void OpenLightSerialControl() => _navigationService.ShowWindow<LightSerialControlView>();

    /// <summary>
    /// 測試用：模擬新增檢測結果到歷史圖庫
    /// </summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task TestAddInspectionAsync()
    {
        try
        {
            // 1. 檢查是否有工單
            var workOrder = await _workOrderService.GetCurrentWorkOrderAsync(CancellationToken.None);
            if (workOrder == null)
            {
                System.Windows.MessageBox.Show(
                    "請先建立工單才能測試新增檢測記錄",
                    "測試新增檢測",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            // 2. 選擇圖片檔案
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "選擇測試圖片",
                Filter = "圖片檔案|*.jpg;*.jpeg;*.png;*.bmp|所有檔案|*.*",
                Multiselect = false
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var imagePath = dialog.FileName;

            // 3. 詢問結果類型
            var resultDialog = System.Windows.MessageBox.Show(
                "選擇檢測結果：\n\n" +
                "【是】= OK (綠色)\n" +
                "【否】= 輸入瑕疵名稱 (紅色)\n" +
                "【取消】= 取消操作",
                "測試新增檢測 - 選擇結果",
                System.Windows.MessageBoxButton.YesNoCancel,
                System.Windows.MessageBoxImage.Question);

            if (resultDialog == System.Windows.MessageBoxResult.Cancel)
            {
                return;
            }

            string result;
            int defectCount;
            List<Defect>? defects = null;

            if (resultDialog == System.Windows.MessageBoxResult.Yes)
            {
                // OK 結果
                result = "OK";
                defectCount = 0;
            }
            else
            {
                // NG 結果 - 輸入瑕疵名稱
                var inputWindow = new System.Windows.Window
                {
                    Title = "輸入瑕疵名稱",
                    Width = 350,
                    Height = 150,
                    WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
                    ResizeMode = System.Windows.ResizeMode.NoResize
                };

                var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(15) };
                panel.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = "請輸入瑕疵名稱（如：刮痕、TF碰撞、髒汙）：",
                    Margin = new System.Windows.Thickness(0, 0, 0, 10)
                });

                var textBox = new System.Windows.Controls.TextBox
                {
                    Text = "刮痕",
                    Margin = new System.Windows.Thickness(0, 0, 0, 15)
                };
                panel.Children.Add(textBox);

                var buttonPanel = new System.Windows.Controls.StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right
                };

                var okButton = new System.Windows.Controls.Button
                {
                    Content = "確定",
                    Width = 80,
                    Margin = new System.Windows.Thickness(0, 0, 10, 0),
                    IsDefault = true
                };
                okButton.Click += (s, e) => { inputWindow.DialogResult = true; inputWindow.Close(); };
                buttonPanel.Children.Add(okButton);

                var cancelButton = new System.Windows.Controls.Button
                {
                    Content = "取消",
                    Width = 80,
                    IsCancel = true
                };
                buttonPanel.Children.Add(cancelButton);
                panel.Children.Add(buttonPanel);

                inputWindow.Content = panel;

                if (inputWindow.ShowDialog() != true || string.IsNullOrWhiteSpace(textBox.Text))
                {
                    return;
                }

                result = textBox.Text.Trim();
                defectCount = 1;
                defects = new List<Defect>
                {
                    new Defect(
                        type: result,
                        confidence: 0.95f,
                        boundingBox: null,
                        severity: null)
                };
            }

            // 4. 複製圖片到工單資料夾
            string? savedImagePath = null;
            try
            {
                var resultFolder = result == "OK" ? "OK" : "NG";
                var (originalPath, _) = await _imageService.SaveInspectionImageAsync(
                    await LoadImageDataAsync(imagePath),
                    workOrder.Code,
                    resultFolder,
                    null,
                    CancellationToken.None);
                savedImagePath = originalPath;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "測試圖片儲存失敗，使用原路徑");
                savedImagePath = imagePath;
            }

            // 5. 新增檢測記錄到資料庫
            var inspection = new Inspection(
                workOrderId: workOrder.Id,
                result: result,
                confidence: result == "OK" ? 1.0f : 0.95f,
                imagePath: savedImagePath,
                modelVersion: CurrentModelName ?? "TestModel",
                annotatedImagePath: result != "OK" ? savedImagePath : null,
                inferenceTimeMs: 100,
                defects: defects);

            await _inspectionRepository.AddAsync(inspection, CancellationToken.None);

            // 6. 更新 UI 統計（必須在 UI 執行緒）
            await _dispatcher.InvokeAsync(() =>
            {
                TotalCount++;
                if (result != "OK")
                {
                    NgCount++;

                    // 7. 更新瑕疵統計（NG 時才需要）
                    if (defects != null && defects.Count > 0)
                    {
                        UpdateDefectStats(defects);
                        _logger?.LogInformation("測試瑕疵統計已更新: {DefectType}", result);
                    }
                }
            });

            _logger?.LogInformation(
                "測試檢測記錄已新增: Result={Result}, DefectCount={DefectCount}, WorkOrder={WorkOrder}",
                result, defectCount, workOrder.Code);

            System.Windows.MessageBox.Show(
                $"測試檢測記錄已新增！\n\n" +
                $"工單: {workOrder.Code}\n" +
                $"結果: {result}\n" +
                $"瑕疵數: {defectCount}\n" +
                (result != "OK" ? $"瑕疵統計已更新\n" : "") +
                $"\n請開啟「歷史圖庫」查看結果。",
                "測試成功",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "測試新增檢測記錄失敗");
            System.Windows.MessageBox.Show(
                $"測試失敗：{ex.Message}",
                "錯誤",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 從檔案載入圖片數據
    /// </summary>
    private async Task<ImageData> LoadImageDataAsync(string filePath)
    {
        var bytes = await System.IO.File.ReadAllBytesAsync(filePath);
        using var ms = new System.IO.MemoryStream(bytes);
        var bitmap = new System.Drawing.Bitmap(ms);
        return new ImageData(bytes, bitmap.Width, bitmap.Height, bitmap.PixelFormat.ToString());
    }

    [RelayCommand]
    private void OpenOfflineTest() => _navigationService.ShowWindow<OfflineTestView>();

    [RelayCommand]
    private async Task ToggleRunAsync(bool? isChecked)
    {
        if (isChecked == true)
        {
            await StartPlcModeAsync(runOnce: false);
        }
        else
        {
            await StopPlcModeAsync();
        }
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        await StartPlcModeAsync(runOnce: false);
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        await StopPlcModeAsync();
    }

    /// <summary>
    /// 啟動 PLC 模式（連續或單次）
    /// 方案C：ShellViewModel 負責開啟相機並控制取像，PLC 連線由 IoPanelView 控制
    /// 支援 Area Scan (原有) 和 Line Scan (Auto Run) 兩種模式
    /// </summary>
    private async Task StartPlcModeAsync(bool runOnce)
    {
        try
        {
            // 自動偵測：如果 LineScanService 正在掃描，自動切換到 Line Scan 模式
            if (_lineScanService?.IsScanning == true)
            {
                IsLineScanMode = true;
                _logger?.LogInformation("偵測到 LineScanService 正在運行，自動切換到 Line Scan 模式");
            }

            var modeText = runOnce ? "單次模式" : "連續模式";
            var cameraModeText = IsLineScanMode ? "Line Scan" : "Area Scan";
            _logger?.LogInformation("========== 開始啟動 PLC {Mode} ({CameraMode}) ==========", modeText, cameraModeText);

            // 檢查工單
            var workOrder = await _workOrderService.GetCurrentWorkOrderAsync(CancellationToken.None);
            if (workOrder == null)
            {
                _logger?.LogWarning("嘗試啟動 PLC 模式但沒有活動工單");
                System.Windows.MessageBox.Show(
                    "沒有活動工單，請先創建工單",
                    "警告",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                IsRunning = false;  // 確保按鈕狀態回復
                return;
            }
            _logger?.LogInformation("工單檢查通過: {WorkOrderCode}", workOrder.Code);

            // 檢查相機是否已連接且設定好（Line Scan Panel 應該已經處理）
            if (_lineScanService != null)
            {
                if (!_lineScanService.IsCameraConnected)
                {
                    _logger?.LogWarning("相機未連接");
                    System.Windows.MessageBox.Show(
                        "無法啟動：相機未連接。\n請先開啟「Line Scan」面板並連接相機。",
                        "相機未連接",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    IsRunning = false;
                    return;
                }

                if (_lineScanService.CurrentSettings == null)
                {
                    _logger?.LogWarning("相機尚未設定");
                    System.Windows.MessageBox.Show(
                        "無法啟動：相機尚未設定。\n請先開啟「Line Scan」面板，載入設定並測試取像。",
                        "相機未設定",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    IsRunning = false;
                    return;
                }
            }

            // 檢查 PLC 連線，若斷線則嘗試自動重連
            if (!_plcCommunication.IsConnected)
            {
                _logger?.LogWarning("PLC 連線已斷開，嘗試自動重新連線...");
                try
                {
                    await _plcCommunication.ConnectAsync(CancellationToken.None);
                    _logger?.LogInformation("✓ PLC 自動重新連線成功");
                }
                catch (Exception connEx)
                {
                    _logger?.LogError(connEx, "PLC 自動重新連線失敗");
                    System.Windows.MessageBox.Show(
                        $"無法啟動：PLC 連線失敗。\n請檢查 PLC 連線狀態後再試。\n\n錯誤: {connEx.Message}",
                        "PLC 連線失敗",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    IsRunning = false;
                    return;
                }
            }

            // 自動啟動交握（混合模式：若未啟動則自動啟動）
            if (!_plcHandshake.IsRunning)
            {
                _logger?.LogInformation("自動啟動 PLC 交握服務...");
                try
                {
                    await _plcHandshake.StartAsync();
                    _logger?.LogInformation("✓ PLC 交握服務已自動啟動");
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "自動啟動 PLC 交握服務失敗");
                    System.Windows.MessageBox.Show(
                        $"無法啟動：PLC 交握服務啟動失敗。\n{ex.Message}",
                        "交握服務啟動失敗",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    IsRunning = false;
                    return;
                }
            }

            // Line Scan 模式：使用 AutoRunService
            if (IsLineScanMode)
            {
                await StartLineScanAutoRunAsync(workOrder, runOnce);
            }
            else
            {
                // Area Scan 模式：使用原有流程
                await StartAreaScanModeAsync(workOrder, runOnce);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "啟動 PLC 模式失敗");
            PlcStatusText = $"啟動失敗: {ex.Message}";
            IsRunning = false;
            System.Windows.MessageBox.Show(
                $"啟動失敗：{ex.Message}",
                "錯誤",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 啟動 Line Scan Auto Run 模式
    /// </summary>
    private async Task StartLineScanAutoRunAsync(WorkOrder workOrder, bool runOnce)
    {
        if (_autoRunService == null || _lineScanService == null)
        {
            _logger?.LogWarning("Line Scan 服務未註冊");
            System.Windows.MessageBox.Show(
                "無法啟動：Line Scan 服務未配置。\n請確認已註冊 IAutoRunService 和 ILineScanService。",
                "服務未配置",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            IsRunning = false;  // 確保按鈕狀態回復
            return;
        }

        _logger?.LogInformation("啟動 Line Scan Auto Run 模式...");

        // 停止 IoPanel 輪詢，避免 PLC 連線競爭條件
        _ioPanelViewModel?.StopPolling();
        if (_ioPanelViewModel != null)
        {
            _ioPanelViewModel.IsAutoRunActive = true;  // 禁用 Disconnect 按鈕
        }
        _logger?.LogInformation("已停止 IoPanel 輪詢 (避免 PLC 通訊競爭)");

        // 等待殘餘的 PLC 通訊操作完成，避免 Semaphore 競爭
        // IoPanel 可能還有正在進行的讀取操作，需要等待其完成釋放 Modbus IO 鎖
        await Task.Delay(500);
        _logger?.LogDebug("已等待 500ms 確保 IoPanel 殘餘操作完成");

        // 訂閱 Auto Run 事件
        SubscribeAutoRunEvents();

        // 建立 Auto Run 選項
        var options = new AutoRunOptions
        {
            CameraMode = CameraMode.LineScan,
            LineScanSettings = new LineScanSettings
            {
                OffsetX = 0,
                OffsetY = 0,
                Width = 4096,  // 預設值，可從 LineScanService.CurrentSettings 取得
                TargetHeight = 1024,
                LineRate = 10000
            },
            WorkOrderCode = workOrder.Code,
            SaveImages = true,
            SkipInference = _configuration.GetValue<bool>("AutoRun:SkipInference", false),
            SkipInferenceResult = _configuration.GetValue<string>("AutoRun:SkipInferenceResult", "Ok") ?? "Ok",
            InspectionTimeoutMs = 30000,
            InferenceTimeoutMs = 10000,  // 推論超時設為 10 秒
            AutoRetryOnError = !runOnce  // 單次模式不重試
        };

        // 如果 LineScanService 已有設定，使用已有的
        if (_lineScanService.CurrentSettings != null)
        {
            options.LineScanSettings = new LineScanSettings
            {
                OffsetX = (int)_lineScanService.CurrentSettings.OffsetX,
                OffsetY = (int)_lineScanService.CurrentSettings.OffsetY,
                Width = (int)_lineScanService.CurrentSettings.Width,
                TargetHeight = _lineScanService.CurrentSettings.TargetHeight,
                LineRate = (int)_lineScanService.CurrentSettings.LineRate
            };
        }

        try
        {
            // 啟動 Auto Run（Fire and Forget 模式）
            // 注意：StartAsync 會持續運行直到被 StopAsync 停止，不使用超時限制
            // 使用 _ = Task.Run(...) 讓它在背景運行，不阻塞 UI
            _ = Task.Run(async () =>
            {
                try
                {
                    await _autoRunService.StartAsync(options, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Auto Run 背景任務發生錯誤");
                }
                finally
                {
                    // Auto Run 結束時（不管是正常停止還是錯誤），恢復 IoPanel 輪詢
                    await _dispatcher.InvokeAsync(() =>
                    {
                        if (_plcCommunication.IsConnected && !IsRunning)
                        {
                            _ioPanelViewModel?.StartPolling();
                            _logger?.LogInformation("Auto Run 結束，已恢復 IoPanel 輪詢");
                        }
                    });
                }
            });

            // 等待一小段時間確保 StartAsync 已開始初始化
            await Task.Delay(100);

            _isRunOnceMode = runOnce;
            IsPlcConnected = true;
            IsRunning = true;
            PlcStatusText = runOnce ? "Line Scan: 等待觸發 (單次)" : "Line Scan: 運行中";

            _logger?.LogInformation("✓ Line Scan Auto Run 已啟動");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "啟動 Line Scan Auto Run 失敗");
            UnsubscribeAutoRunEvents();

            // 啟動失敗時恢復 IoPanel 輪詢
            if (_plcCommunication.IsConnected)
            {
                _ioPanelViewModel?.StartPolling();
                _logger?.LogInformation("啟動失敗，已恢復 IoPanel 輪詢");
            }
            if (_ioPanelViewModel != null)
            {
                _ioPanelViewModel.IsAutoRunActive = false;  // 恢復 Disconnect 按鈕
            }

            throw;
        }
    }

    /// <summary>
    /// 啟動 Area Scan 模式（原有流程）
    /// </summary>
    private async Task StartAreaScanModeAsync(WorkOrder workOrder, bool runOnce)
    {
        var modeText = runOnce ? "單次模式" : "連續模式";

        // 檢查相機是否為真實相機
        var cameraTypeName = _camera.GetType().Name;
        var isFakeCamera = cameraTypeName.Contains("Fake", StringComparison.OrdinalIgnoreCase);
        _logger?.LogInformation("相機類型: {CameraType}, IsFake: {IsFake}", cameraTypeName, isFakeCamera);

        if (isFakeCamera)
        {
            _logger?.LogWarning("嘗試啟動 PLC 模式但使用的是假相機");
            System.Windows.MessageBox.Show(
                "無法啟動：未偵測到實際相機連線。\n請確認相機已正確連接並在設定中配置相機類型。",
                "相機未連線",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        // 檢查相機是否已開啟（方案C：由 ShellViewModel 負責開啟相機）
        if (!_camera.IsOpen)
        {
            _logger?.LogInformation("相機尚未開啟，嘗試自動開啟相機...");
            try
            {
                // 使用預設 DeviceId（第一台相機）
                await _camera.OpenAsync("", CancellationToken.None);
                _logger?.LogInformation("✓ 相機已自動開啟");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "自動開啟相機失敗");
                System.Windows.MessageBox.Show(
                    $"無法開啟相機：{ex.Message}\n\n請先到「相機設定」頁面手動開啟相機，或確認相機連線狀態。",
                    "相機開啟失敗",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }
        }
        else
        {
            _logger?.LogInformation("✓ 相機已開啟，準備啟動取像模式");
        }

        _isRunOnceMode = runOnce;
        IsPlcConnected = true;

        // 訂閱事件（不控制連線，只訂閱）
        SubscribePlcEvents();

        // 啟動相機預覽（用於主頁即時顯示）
        try
        {
            _logger?.LogInformation("啟動相機預覽...");
            await _camera.StartPreviewAsync(CancellationToken.None);

            // 訂閱相機幀事件以更新 LiveBitmap
            _camera.FrameReceived += OnCameraFrameReceived;

            _logger?.LogInformation("✓ 相機預覽已啟動");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "啟動相機預覽失敗（不影響取像功能）");
        }

        IsRunning = true;
        PlcStatusText = runOnce ? "Area Scan: 等待觸發 (單次)" : "Area Scan: 運行中 (連續)";
        _logger?.LogInformation("========== Area Scan {Mode}已啟動，等待觸發 ==========", modeText);
    }

    /// <summary>
    /// 停止 PLC 模式
    /// 方案C：停止相機預覽和取消訂閱事件，PLC 連線由 IoPanelView 控制
    /// 支援 Area Scan 和 Line Scan (Auto Run) 兩種模式
    /// </summary>
    private async Task StopPlcModeAsync()
    {
        try
        {
            var cameraModeText = IsLineScanMode ? "Line Scan" : "Area Scan";
            _logger?.LogInformation("========== 停止 PLC 模式 ({CameraMode}) ==========", cameraModeText);

            if (IsLineScanMode)
            {
                // 停止 Line Scan Auto Run 模式
                await StopLineScanAutoRunAsync();
            }
            else
            {
                // 停止 Area Scan 模式
                await StopAreaScanModeAsync();
            }

            IsRunning = false;
            // 保持 IsPlcConnected 狀態不變，因為 PLC 連線仍由 IoPanelView 控制
            PlcStatusText = _plcCommunication.IsConnected ? "PLC 已連線 (待命)" : "PLC 未連線";
            _logger?.LogInformation("========== PLC 模式已停止 ==========");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "停止 PLC 模式失敗");
            PlcStatusText = $"停止失敗: {ex.Message}";
        }
    }

    /// <summary>
    /// 停止 Line Scan Auto Run 模式
    /// </summary>
    private async Task StopLineScanAutoRunAsync()
    {
        if (_autoRunService == null) return;

        try
        {
            _logger?.LogInformation("停止 Line Scan Auto Run...");

            // 停止 Auto Run 服務（使用超時保護避免無限等待）
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _autoRunService.StopAsync(cts.Token);

            // 取消訂閱 Auto Run 事件
            UnsubscribeAutoRunEvents();

            // 重置 Auto Run 狀態顯示
            AutoRunState = AutoRunState.Stopped;
            AutoRunStateText = "已停止";

            // 恢復 IoPanel 輪詢（如果 PLC 已連線）
            if (_plcCommunication.IsConnected)
            {
                _ioPanelViewModel?.StartPolling();
                _logger?.LogInformation("已恢復 IoPanel 輪詢");
            }
            if (_ioPanelViewModel != null)
            {
                _ioPanelViewModel.IsAutoRunActive = false;  // 恢復 Disconnect 按鈕
            }

            _logger?.LogInformation("✓ Line Scan Auto Run 已停止");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "停止 Line Scan Auto Run 失敗");
            throw;
        }
    }

    /// <summary>
    /// 停止 Area Scan 模式
    /// </summary>
    private async Task StopAreaScanModeAsync()
    {
        // 停止相機預覽
        try
        {
            _camera.FrameReceived -= OnCameraFrameReceived;
            await _camera.StopPreviewAsync(CancellationToken.None);
            _logger?.LogInformation("✓ 相機預覽已停止");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "停止相機預覽失敗");
        }

        // 取消訂閱 PLC 事件
        UnsubscribePlcEvents();
        _logger?.LogInformation("✓ Area Scan 模式已停止（事件已取消訂閱）");
    }

    /// <summary>
    /// 訂閱 PLC 交握事件（運行模式時使用）
    /// 注意：PLC 連線狀態事件在構造函數中已訂閱，這裡只訂閱交握相關事件
    /// </summary>
    private void SubscribePlcEvents()
    {
        if (_isPlcEventSubscribed) return;

        _plcHandshake.TriggerReceived += OnPlcTriggerReceived;
        _plcHandshake.CaptureRequested += OnPlcCaptureRequested;
        _plcHandshake.StateChanged += OnPlcStateChanged;
        _isPlcEventSubscribed = true;
    }

    /// <summary>
    /// 取消訂閱 PLC 交握事件
    /// </summary>
    private void UnsubscribePlcEvents()
    {
        if (!_isPlcEventSubscribed) return;

        _plcHandshake.TriggerReceived -= OnPlcTriggerReceived;
        _plcHandshake.CaptureRequested -= OnPlcCaptureRequested;
        _plcHandshake.StateChanged -= OnPlcStateChanged;
        _isPlcEventSubscribed = false;
    }

    /// <summary>
    /// 訂閱 Auto Run 事件（Line Scan 模式）
    /// </summary>
    private void SubscribeAutoRunEvents()
    {
        if (_autoRunService == null || _isAutoRunEventSubscribed) return;

        _autoRunService.StateChanged += OnAutoRunStateChanged;
        _autoRunService.InspectionCompleted += OnAutoRunInspectionCompleted;
        _autoRunService.ErrorOccurred += OnAutoRunErrorOccurred;
        _autoRunService.TriggerReceived += OnAutoRunTriggerReceived;
        _autoRunService.CaptureCompleted += OnAutoRunCaptureCompleted;
        _isAutoRunEventSubscribed = true;
    }

    /// <summary>
    /// 取消訂閱 Auto Run 事件
    /// </summary>
    private void UnsubscribeAutoRunEvents()
    {
        if (_autoRunService == null || !_isAutoRunEventSubscribed) return;

        _autoRunService.StateChanged -= OnAutoRunStateChanged;
        _autoRunService.InspectionCompleted -= OnAutoRunInspectionCompleted;
        _autoRunService.ErrorOccurred -= OnAutoRunErrorOccurred;
        _autoRunService.TriggerReceived -= OnAutoRunTriggerReceived;
        _autoRunService.CaptureCompleted -= OnAutoRunCaptureCompleted;
        _isAutoRunEventSubscribed = false;
    }

    /// <summary>
    /// Auto Run 狀態變更處理
    /// </summary>
    private void OnAutoRunStateChanged(object? sender, AutoRunStateChangedEventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            AutoRunState = e.NewState;
            AutoRunStateText = GetAutoRunStateText(e.NewState);
            PlcStatusText = $"Line Scan: {AutoRunStateText}";

            _logger?.LogInformation("Auto Run 狀態: {PreviousState} → {NewState}", e.PreviousState, e.NewState);
        });
    }

    /// <summary>
    /// Auto Run 檢測完成處理
    /// </summary>
    private async void OnAutoRunInspectionCompleted(object? sender, InspectionCompletedEventArgs e)
    {
        // 1. 更新 UI（在 UI 執行緒）
        await _dispatcher.InvokeAsync(() =>
        {
            // 更新結果顯示（左上角只顯示 OK/NG，不顯示瑕疵標籤）
            LastResultStatus = e.IsOk ? "OK" : "NG";
            IsLastResultOk = e.IsOk;
            LastResultConfidence = e.Confidence;

            // 顯示標註圖（若有）或原圖
            if (e.AnnotatedBitmap != null)
            {
                LiveBitmap = e.AnnotatedBitmap;
                _logger?.LogInformation("Auto Run: 顯示標註圖片，包含 {DefectCount} 個瑕疵標註", e.Defects?.Count ?? 0);
            }
            else if (!string.IsNullOrEmpty(e.ImagePath) && File.Exists(e.ImagePath))
            {
                // Fallback: 從檔案載入原圖（若 BitmapSource 未提供）
                try
                {
                    var imageData = File.ReadAllBytes(e.ImagePath);
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.StreamSource = new MemoryStream(imageData);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    LiveBitmap = bitmap;
                    _logger?.LogDebug("Auto Run: 從檔案載入原圖: {Path}", e.ImagePath);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Auto Run: 載入圖片失敗: {Path}", e.ImagePath);
                }
            }

            // 更新統計
            var stats = _autoRunService?.Statistics;
            if (stats != null)
            {
                AutoRunTotalCount = stats.TotalCount;
                AutoRunOkCount = stats.OkCount;
                AutoRunNgCount = stats.NgCount;
                AutoRunYieldRate = $"{stats.YieldRate:F2}%";

                // 同步到主統計
                TotalCount = stats.TotalCount;
                NgCount = stats.NgCount;
            }

            // 更新缺陷統計
            if (e.Defects != null && e.Defects.Count > 0)
            {
                UpdateDefectStats(e.Defects);
            }

            _logger?.LogInformation("Auto Run 檢測完成 #{Index}: {Label}, 信心值: {Confidence:P2}",
                e.InspectionIndex, e.Label, e.Confidence);
        });

        // 2. 儲存到資料庫（背景執行）
        try
        {
            var workOrder = await _workOrderService.GetCurrentWorkOrderAsync(CancellationToken.None);
            if (workOrder == null)
            {
                _logger?.LogWarning("Auto Run 檢測完成但沒有工單，無法儲存到資料庫");
                return;
            }

            // 修正：資料庫 Result 欄位應存 "OK" 或 "NG"，而非瑕疵類別名稱
            // e.Label 在 NG 時是瑕疵類別（如 TF_crash），但 SQL 查詢需要 "OK"/"NG" 來計算良率
            var dbResult = e.IsOk ? "OK" : "NG";

            var inspection = new Inspection(
                workOrderId: workOrder.Id,
                result: dbResult,
                confidence: e.Confidence,
                imagePath: e.ImagePath,
                modelVersion: CurrentModelName ?? "Unknown",
                annotatedImagePath: e.AnnotatedImagePath,
                inferenceTimeMs: (int)e.InferenceTimeMs,
                defects: e.Defects);

            await _inspectionRepository.AddAsync(inspection, CancellationToken.None);
            _logger?.LogDebug("Auto Run 檢測記錄已保存，ID: {InspectionId}, Result: {Result}", inspection.Id, dbResult);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Auto Run 檢測記錄儲存到資料庫失敗");
        }
    }

    /// <summary>
    /// Auto Run 錯誤處理
    /// </summary>
    private void OnAutoRunErrorOccurred(object? sender, AutoRunErrorEventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            PlcStatusText = $"錯誤: {e.Message}";
            _logger?.LogError(e.Exception, "Auto Run 錯誤: {Message}", e.Message);

            if (!e.IsRecoverable)
            {
                System.Windows.MessageBox.Show(
                    $"Auto Run 發生嚴重錯誤：\n{e.Message}",
                    "錯誤",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        });
    }

    /// <summary>
    /// Auto Run 觸發接收處理
    /// </summary>
    private void OnAutoRunTriggerReceived(object? sender, TriggerReceivedEventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            PlcStatusText = $"Line Scan: 觸發 #{e.TriggerIndex}";
        });
    }

    /// <summary>
    /// Auto Run 取像完成處理
    /// </summary>
    private void OnAutoRunCaptureCompleted(object? sender, CaptureCompletedEventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            PlcStatusText = $"Line Scan: 推論中... ({e.ElapsedMs}ms, {e.Width}x{e.Height})";

            // 更新主頁影像顯示
            try
            {
                LiveBitmap = BitmapSourceFactory.FromImageData(e.Image);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "顯示取像影像失敗");
            }
        });
    }

    /// <summary>
    /// 取得 Auto Run 狀態文字
    /// </summary>
    private static string GetAutoRunStateText(AutoRunState state) => state switch
    {
        AutoRunState.Idle => "待機",
        AutoRunState.Initializing => "初始化中",
        AutoRunState.WaitingTrigger => "等待觸發",
        AutoRunState.Capturing => "取像中",
        AutoRunState.Inferring => "推論中",
        AutoRunState.Reporting => "回報結果",
        AutoRunState.Paused => "已暫停",
        AutoRunState.Stopping => "停止中",
        AutoRunState.Stopped => "已停止",
        AutoRunState.Error => "錯誤",
        _ => "未知"
    };

    /// <summary>
    /// PLC 觸發事件處理 (僅更新狀態顯示)
    /// </summary>
    private void OnPlcTriggerReceived(object? sender, PlcTriggerReceivedEventArgs e)
    {
        _logger?.LogInformation("收到 PLC 觸發 #{TriggerCount}", e.TriggerCount);

        _dispatcher.BeginInvoke(() =>
        {
            PlcStatusText = $"觸發 #{e.TriggerCount} - 準備取像...";
        });
    }

    /// <summary>
    /// PLC 取像請求事件處理 (執行相機取像 + AI 推論)
    /// </summary>
    private async void OnPlcCaptureRequested(object? sender, PlcCaptureRequestedEventArgs e)
    {
        _logger?.LogInformation("收到取像請求 #{TriggerCount}", e.TriggerCount);

        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                PlcStatusText = $"觸發 #{e.TriggerCount} - 取像中...";
            });

            // 執行推論流程 (包含取像)
            var result = await ExecuteInspectionAsync();

            // 通知 PLC 取像完成
            await _plcHandshake.NotifyCaptureCompleteAsync();

            await _dispatcher.InvokeAsync(() =>
            {
                PlcStatusText = $"觸發 #{e.TriggerCount} - 推論中...";
            });

            // 回報結果給 PLC
            var plcResult = result ? PlcInspectionResult.Ok : PlcInspectionResult.Ng;
            await _plcHandshake.ReportResultAsync(plcResult);

            _logger?.LogInformation("推論完成，結果: {Result}", plcResult);

            // 如果是單次模式，完成後停止
            if (_isRunOnceMode)
            {
                _logger?.LogInformation("單次模式完成，準備停止");
                // 等待 PLC 重置後再停止
                await Task.Delay(500);
                await _dispatcher.InvokeAsync(async () =>
                {
                    await StopPlcModeAsync();
                });
            }
            else
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    PlcStatusText = "運行中 (連續)";
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "執行取像/推論失敗");

            // 嘗試通知取像完成（即使失敗也要讓狀態機繼續）
            try
            {
                await _plcHandshake.NotifyCaptureCompleteAsync();
            }
            catch
            {
                // 忽略
            }

            // 回報 NG 結果
            try
            {
                await _plcHandshake.ReportResultAsync(PlcInspectionResult.Ng);
            }
            catch
            {
                // 忽略回報失敗
            }

            await _dispatcher.InvokeAsync(() =>
            {
                PlcStatusText = $"推論失敗: {ex.Message}";
                LastResultStatus = "ERR";
                IsLastResultOk = false;
            });
        }
    }

    /// <summary>
    /// PLC 狀態變更事件處理
    /// </summary>
    private void OnPlcStateChanged(object? sender, PlcHandshakeStateChangedEventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            _logger?.LogDebug("PLC 狀態: {OldState} → {NewState}", e.OldState, e.NewState);
        });
    }

    /// <summary>
    /// 執行推論流程
    /// </summary>
    private async Task<bool> ExecuteInspectionAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger?.LogInformation("----- 開始執行推論流程 -----");

        // 1. 取得當前工單
        var workOrder = await _workOrderService.GetCurrentWorkOrderAsync(CancellationToken.None);
        if (workOrder == null)
        {
            _logger?.LogError("推論失敗：沒有活動工單");
            throw new InvalidOperationException("沒有活動工單");
        }
        _logger?.LogDebug("工單: {WorkOrderCode}", workOrder.Code);

        // 2. 取像
        _logger?.LogInformation("開始取像...");
        var captureStart = sw.ElapsedMilliseconds;
        var image = await _camera.CaptureOnceAsync(CancellationToken.None);
        var captureTime = sw.ElapsedMilliseconds - captureStart;
        _logger?.LogInformation("取像完成，耗時: {CaptureTime}ms, 影像大小: {Width}x{Height}",
            captureTime, image.Width, image.Height);

        // 3. AI 推論
        _logger?.LogInformation("開始 AI 推論...");
        var inferenceStart = sw.ElapsedMilliseconds;
        var prediction = await _aiInference.PredictAsync(image, CancellationToken.None);
        var inferenceTime = sw.ElapsedMilliseconds - inferenceStart;
        _logger?.LogInformation("推論完成，耗時: {InferenceTime}ms, 結果: {Label}, 信心值: {Confidence:P2}",
            inferenceTime, prediction.Label, prediction.Confidence);

        // 4. 繪製瑕疵輪廓 Overlay（若有 WorkflowDefects）
        ImageData? annotatedImage = null;
        BitmapSource? displayBitmap = null;

        if (_overlayRenderer != null &&
            prediction.WorkflowDefects != null &&
            prediction.WorkflowDefects.Count > 0)
        {
            try
            {
                _logger?.LogInformation("Area Scan: 檢測到 {Count} 個瑕疵，繪製標註圖", prediction.WorkflowDefects.Count);
                annotatedImage = _overlayRenderer.DrawDefectContours(image, prediction.WorkflowDefects);

                // 轉換為 BitmapSource 用於 UI 顯示
                if (annotatedImage.HasValue)
                {
                    displayBitmap = BitmapSourceFactory.FromImageData(annotatedImage.Value);
                    displayBitmap.Freeze();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Area Scan: 繪製標註圖失敗，使用原圖");
                annotatedImage = null;
                displayBitmap = BitmapSourceFactory.FromImageData(image);
            }
        }
        else
        {
            // 無瑕疵或無 Overlay 渲染器，使用原圖
            displayBitmap = BitmapSourceFactory.FromImageData(image);
        }

        // 5. 更新 UI
        await _dispatcher.InvokeAsync(() =>
        {
            LastResultStatus = prediction.Label;
            IsLastResultOk = prediction.IsOk;
            LastResultConfidence = prediction.Confidence;

            // 顯示標註圖（若有）或原圖
            if (displayBitmap != null)
            {
                LiveBitmap = displayBitmap;
                if (annotatedImage != null)
                {
                    _logger?.LogInformation("Area Scan: 已顯示標註圖，包含 {DefectCount} 個瑕疵",
                        prediction.WorkflowDefects?.Count ?? 0);
                }
            }
        });

        // 6. 保存圖片（流程A：直接依結果分類儲存到工單資料夾）
        string? originalPath = null;
        string? annotatedPath = null;
        try
        {
            _logger?.LogInformation("開始保存圖片，結果: {Result}, 工單: {WorkOrderCode}",
                prediction.Label, workOrder.Code);

            var paths = await _imageService.SaveInspectionImageAsync(
                image,
                workOrder.Code,
                prediction.Label,
                annotatedImage,  // 傳入標註圖（若有）
                CancellationToken.None);

            originalPath = paths.originalPath;
            annotatedPath = paths.annotatedPath;

            _logger?.LogInformation("圖片已保存: {OriginalPath}", originalPath);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "保存圖片失敗，但推論流程將繼續");
            // 圖片保存失敗不影響整體流程
        }

        // 6. 創建檢測記錄
        var inspection = new Inspection(
            workOrderId: workOrder.Id,
            result: prediction.Label,
            confidence: prediction.Confidence,
            imagePath: originalPath,
            modelVersion: CurrentModelName ?? "Unknown",
            annotatedImagePath: annotatedPath,
            inferenceTimeMs: (int)inferenceTime,
            defects: null);

        // 7. 保存到數據庫
        await _inspectionRepository.AddAsync(inspection, CancellationToken.None);
        _logger?.LogDebug("檢測記錄已保存，ID: {InspectionId}", inspection.Id);

        // 8. 更新統計
        await _dispatcher.InvokeAsync(() =>
        {
            TotalCount++;
            if (!prediction.IsOk)
            {
                NgCount++;
            }
        });

        sw.Stop();
        _logger?.LogInformation("----- 推論流程完成，總耗時: {TotalTime}ms, 結果: {Result} -----",
            sw.ElapsedMilliseconds, prediction.IsOk ? "OK" : "NG");

        return prediction.IsOk;
    }

    [RelayCommand]
    private void Exit()
    {
        System.Windows.Application.Current?.Shutdown();
    }

    [RelayCommand]
    private void Logout()
    {
        _authService?.Logout();
        UpdateAuthDisplayProperties();
    }

    /// <summary>
    /// 認證狀態變更事件處理
    /// </summary>
    private void OnLoginStateChanged(object? sender, EventArgs e)
    {
        _dispatcher.InvokeAsync(UpdateAuthDisplayProperties);
    }

    /// <summary>
    /// 更新認證相關的顯示屬性
    /// </summary>
    private void UpdateAuthDisplayProperties()
    {
        if (_authService != null)
        {
            CurrentUserDisplay = _authService.CurrentDisplayName;
            OnPropertyChanged(nameof(IsEngineerOrAbove));
            OnPropertyChanged(nameof(IsVendor));
        }
        else
        {
            CurrentUserDisplay = "作業員";
        }
    }

    /// <summary>
    /// 是否為工程師或更高權限（用於 UI 綁定）
    /// </summary>
    public bool IsEngineerOrAbove => _authService?.HasPermission(UserRole.Engineer) ?? false;

    /// <summary>
    /// 是否為廠商權限（用於 UI 綁定）
    /// </summary>
    public bool IsVendor => _authService?.HasPermission(UserRole.Vendor) ?? false;

    private async Task LoadCurrentWorkOrderAsync()
    {
        try
        {
            var workOrder = await _workOrderService.GetCurrentWorkOrderAsync(CancellationToken.None);
            if (workOrder != null)
            {
                OrderCode = workOrder.Code;
                ProductName = workOrder.ProductName;

                // 載入工單的檢測統計
                var stats = await _inspectionRepository.GetStatisticsByWorkOrderIdAsync(workOrder.Id, CancellationToken.None);
                TotalCount = stats.TotalCount;
                NgCount = stats.NgCount;

                // 同步到 Auto Run 統計顯示
                AutoRunTotalCount = stats.TotalCount;
                AutoRunOkCount = stats.OkCount;
                AutoRunNgCount = stats.NgCount;
                AutoRunYieldRate = stats.TotalCount > 0
                    ? $"{(double)stats.OkCount / stats.TotalCount * 100:F2}%"
                    : "0.00%";

                _logger?.LogInformation("載入工單 {Code} 統計: Total={Total}, OK={Ok}, NG={Ng}",
                    workOrder.Code, stats.TotalCount, stats.OkCount, stats.NgCount);

                // 同步到 AutoRunService 的統計（避免記憶體統計與 DB 統計不一致）
                if (_autoRunService?.Statistics != null)
                {
                    _autoRunService.Statistics.Initialize(stats.TotalCount, stats.OkCount, stats.NgCount);
                    _logger?.LogDebug("已同步 AutoRunService 統計: Total={Total}, OK={Ok}, NG={Ng}",
                        stats.TotalCount, stats.OkCount, stats.NgCount);
                }

                // 根據工單的模型重新初始化缺陷統計（同時載入歷史瑕疵統計）
                await InitializeDefectStatsForWorkOrderAsync(workOrder.Id, workOrder.ModelName);
            }
            else
            {
                // 沒有活動工單時顯示預設訊息
                OrderCode = "無工單";
                ProductName = "未設定";

                // 重置統計
                TotalCount = 0;
                NgCount = 0;
                AutoRunTotalCount = 0;
                AutoRunOkCount = 0;
                AutoRunNgCount = 0;
                AutoRunYieldRate = "0.00%";

                // 重置 AutoRunService 的統計
                _autoRunService?.Statistics?.Reset();

                // 使用當前選擇的模型
                await InitializeDefectStatsAsync();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "載入當前工單失敗");
            OrderCode = "無工單";
            ProductName = "未設定";
        }
    }

    [RelayCommand]
    private async Task CreateNewWorkOrderAsync()
    {
        try
        {
            var productName = ProductName ?? "默認產品";
            var modelName = CurrentModelName;
            var machineModelName = "默認機種"; // 自動創建時使用默認機種名稱

            var workOrder = await _workOrderService.CreateWorkOrderAsync(
                productName,
                modelName,
                machineModelName,
                CancellationToken.None);

            OrderCode = workOrder.Code;
            ProductName = workOrder.ProductName;

            // 重置統計
            TotalCount = 0;
            NgCount = 0;
            await InitializeDefectStatsAsync();

            // 發送工單變更訊息
            _messenger.Send(new WorkOrderChangedMessage());
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"創建工單失敗：{ex.Message}",
                "錯誤",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task EndCurrentWorkOrderAsync()
    {
        try
        {
            // 檢查是否有工單
            var currentWorkOrder = await _workOrderService.GetCurrentWorkOrderAsync(CancellationToken.None);
            if (currentWorkOrder == null)
            {
                System.Windows.MessageBox.Show(
                    "目前沒有進行中的工單",
                    "提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            // 確認是否要結束工單
            var confirmResult = System.Windows.MessageBox.Show(
                $"確定要結束工單「{currentWorkOrder.Code}」嗎？\n\n" +
                $"產品名稱：{currentWorkOrder.ProductName}\n" +
                $"總檢測數：{TotalCount}\n" +
                $"NG 數量：{NgCount}",
                "確認結束工單",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirmResult != MessageBoxResult.Yes)
            {
                return;
            }

            await _workOrderService.EndCurrentWorkOrderAsync(CancellationToken.None);

            // 結束工單後更新顯示，不自動創建新工單
            OrderCode = "無工單";
            ProductName = "未設定";

            // 重置統計（包括 Auto Run 統計）
            TotalCount = 0;
            NgCount = 0;
            AutoRunTotalCount = 0;
            AutoRunOkCount = 0;
            AutoRunNgCount = 0;
            AutoRunYieldRate = "0.00%";
            await InitializeDefectStatsAsync();

            // 注意：不發送 WorkOrderChangedMessage，因為我們已經在本地更新了 UI
            // 如果其他組件需要同步，可以透過其他機制（如直接調用服務）
            _logger?.LogInformation("工單已結束，UI 已重置");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"結束工單失敗：{ex.Message}",
                "錯誤",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 根據當前模型配置動態初始化瑕疵統計
    /// </summary>
    private async Task InitializeDefectStatsAsync()
    {
        try
        {
            // 取得當前模型配置
            var currentModel = await _modelConfigService.GetCurrentModelAsync();

            await _dispatcher.InvokeAsync(() =>
            {
                Defects.Clear();

                // 取得結果類型列表（不再提供預設值，由模型設定決定）
                var resultTypes = currentModel?.GetEffectiveResultTypes();
                if (resultTypes == null || resultTypes.Count == 0)
                {
                    _logger?.LogDebug("模型未設定 ResultTypes，缺陷統計為空");
                    return;
                }

                // 初始化瑕疵統計項目（同時存 TypeName 和 DisplayName）
                foreach (var type in resultTypes)
                {
                    var displayName = currentModel?.GetDisplayName(type) ?? type;
                    Defects.Add(new DefectItemViewModel(type, displayName, 0, TotalCount, 0.0, "→"));
                }

                _logger?.LogDebug("已初始化 {Count} 個瑕疵類型: {Types}",
                    resultTypes.Count, string.Join(", ", resultTypes));
            });
        }
        catch (Exception ex)
        {
            // 錯誤時清空（不再顯示預設 OK/NG）
            _logger?.LogWarning(ex, "初始化缺陷統計失敗");
            await _dispatcher.InvokeAsync(() =>
            {
                Defects.Clear();
            });
        }
    }

    /// <summary>
    /// 根據工單指定的模型名稱初始化瑕疵統計，並從資料庫載入歷史統計
    /// </summary>
    private async Task InitializeDefectStatsForWorkOrderAsync(Guid workOrderId, string? modelName)
    {
        try
        {
            // 根據工單的模型名稱取得模型配置
            ModelConfig? workOrderModel = null;

            if (!string.IsNullOrEmpty(modelName))
            {
                workOrderModel = await _modelConfigService.GetModelByNameAsync(modelName);
            }

            // 如果找不到工單指定的模型，使用當前選擇的模型
            workOrderModel ??= await _modelConfigService.GetCurrentModelAsync();

            // 從資料庫查詢歷史瑕疵統計
            var defectStats = await _inspectionRepository.GetDefectStatisticsByWorkOrderIdAsync(workOrderId, CancellationToken.None);

            _logger?.LogInformation("載入工單瑕疵統計: {Count} 種類型, 詳細: {Stats}",
                defectStats.Count,
                string.Join(", ", defectStats.Select(kv => $"{kv.Key}={kv.Value}")));

            await _dispatcher.InvokeAsync(() =>
            {
                Defects.Clear();

                // 取得結果類型列表（不再提供預設值，由模型設定決定）
                var resultTypes = workOrderModel?.GetEffectiveResultTypes();
                if (resultTypes == null || resultTypes.Count == 0)
                {
                    _logger?.LogDebug("工單模型未設定 ResultTypes，缺陷統計為空");
                    return;
                }

                // 初始化瑕疵統計項目（同時存 TypeName 和 DisplayName），並填入歷史統計
                foreach (var type in resultTypes)
                {
                    var displayName = workOrderModel?.GetDisplayName(type) ?? type;

                    // 從歷史統計中查找數量（同時比對 TypeName 和 DisplayName）
                    int count = 0;
                    if (defectStats.TryGetValue(type, out var c1))
                    {
                        count = c1;
                    }
                    else if (defectStats.TryGetValue(displayName, out var c2))
                    {
                        count = c2;
                    }

                    var ratio = TotalCount > 0 ? (double)count / TotalCount : 0.0;
                    Defects.Add(new DefectItemViewModel(type, displayName, count, TotalCount, ratio, "→"));
                }

                // 處理資料庫中有但模型配置中沒有的瑕疵類型（可能是舊資料或測試資料）
                var existingTypeNames = new HashSet<string>(resultTypes, StringComparer.OrdinalIgnoreCase);
                var existingDisplayNames = new HashSet<string>(
                    resultTypes.Select(t => workOrderModel?.GetDisplayName(t) ?? t),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var kvp in defectStats)
                {
                    if (!existingTypeNames.Contains(kvp.Key) && !existingDisplayNames.Contains(kvp.Key))
                    {
                        var ratio = TotalCount > 0 ? (double)kvp.Value / TotalCount : 0.0;
                        Defects.Add(new DefectItemViewModel(kvp.Key, kvp.Key, kvp.Value, TotalCount, ratio, "→"));
                        _logger?.LogDebug("新增未配置的瑕疵類型: {Type}, 數量: {Count}", kvp.Key, kvp.Value);
                    }
                }

                _logger?.LogDebug("已初始化工單瑕疵類型 {Count} 個: {Types}",
                    Defects.Count, string.Join(", ", Defects.Select(d => $"{d.DisplayName}({d.Count})")));
            });
        }
        catch (Exception ex)
        {
            // 錯誤時清空（不再顯示預設 OK/NG）
            _logger?.LogWarning(ex, "初始化工單缺陷統計失敗");
            await _dispatcher.InvokeAsync(() =>
            {
                Defects.Clear();
            });
        }
    }

    private void UpdateDefectStats(IReadOnlyList<Defect>? defects)
    {
        if (defects == null || defects.Count == 0)
        {
            _logger?.LogDebug("UpdateDefectStats: defects 為空，略過");
            return;
        }

        // 統計每種瑕疵類型的數量
        var defectCounts = defects
            .GroupBy(d => d.Type, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        _logger?.LogInformation("更新瑕疵統計: 收到 {Count} 個瑕疵，類型: {Types}",
            defects.Count, string.Join(", ", defectCounts.Keys));

        // 同時比對 TypeName 和 DisplayName（支援英文原名和中文顯示名）
        var existingTypeNames = new HashSet<string>(
            Defects.Select(d => d.TypeName),
            StringComparer.OrdinalIgnoreCase);
        var existingDisplayNames = new HashSet<string>(
            Defects.Select(d => d.DisplayName),
            StringComparer.OrdinalIgnoreCase);

        // 收集未匹配的瑕疵類型（需要自動新增）- 同時檢查 TypeName 和 DisplayName
        var unmatchedTypes = defectCounts.Keys
            .Where(type => !existingTypeNames.Contains(type) && !existingDisplayNames.Contains(type))
            .ToList();

        // 自動新增未匹配的瑕疵類型（TypeName = DisplayName，因為沒有設定對應）
        if (unmatchedTypes.Count > 0)
        {
            _logger?.LogWarning("發現 {Count} 個未在模型中配置的瑕疵類型，自動新增: {Types}（建議在模型管理中設定對應的中文名稱）",
                unmatchedTypes.Count, string.Join(", ", unmatchedTypes));

            foreach (var type in unmatchedTypes)
            {
                var count = defectCounts[type];
                // 未配置的類型，TypeName 和 DisplayName 相同
                var newDefectVm = new DefectItemViewModel(type, type, count, TotalCount, 0.0, "→");
                Defects.Add(newDefectVm);

                _logger?.LogInformation("已自動新增瑕疵類型: {Type}，初始數量: {Count}", type, count);
            }
        }

        // 更新 UI - 使用 TypeName 或 DisplayName 進行比對
        int matchedCount = 0;
        foreach (var defectVm in Defects)
        {
            // 嘗試用 TypeName 比對，如果失敗再用 DisplayName 比對
            // 這樣可以支援：1) 模型回傳英文類名 2) 測試輸入中文名稱
            string? matchedKey = null;
            int count = 0;

            if (defectCounts.TryGetValue(defectVm.TypeName, out count))
            {
                matchedKey = defectVm.TypeName;
            }
            else if (defectCounts.TryGetValue(defectVm.DisplayName, out count))
            {
                matchedKey = defectVm.DisplayName;
            }

            if (matchedKey != null)
            {
                // 如果是新增的項目，已經在創建時設定了 Count，這裡不重複加
                var isNewlyAdded = unmatchedTypes.Contains(matchedKey, StringComparer.OrdinalIgnoreCase);

                if (!isNewlyAdded)
                {
                    defectVm.Count += count;
                }

                matchedCount++;
                _logger?.LogDebug("瑕疵統計已更新: {TypeName} ({DisplayName}) {Operation} {Count} (新總計: {Total})",
                    defectVm.TypeName, defectVm.DisplayName, isNewlyAdded ? "=" : "+=", count, defectVm.Count);
            }

            defectVm.Total = TotalCount;
            defectVm.Ratio = TotalCount > 0 ? (double)defectVm.Count / TotalCount : 0.0;

            // 更新趨勢（簡化版）
            defectVm.Trend = defectVm.Ratio > 0.05 ? "↗" : "→";
        }

        if (matchedCount == 0)
        {
            _logger?.LogWarning("瑕疵統計未更新：沒有匹配的瑕疵類型。Defects 集合有 {Count} 項 (TypeNames: {TypeNames}, DisplayNames: {DisplayNames})，收到的類型: {ReceivedTypes}",
                Defects.Count,
                string.Join(", ", Defects.Select(d => d.TypeName)),
                string.Join(", ", Defects.Select(d => d.DisplayName)),
                string.Join(", ", defectCounts.Keys));
        }
    }

    private static IReadOnlyList<Defect>? MapDefects(IReadOnlyList<AIVision.Application.Contracts.DefectDto>? dtoDefects)
    {
        if (dtoDefects == null || dtoDefects.Count == 0)
        {
            return null;
        }

        return dtoDefects.Select(d => new Defect(
            type: d.Type,
            confidence: d.Confidence,
            boundingBox: d.BoundingBox != null ? new BoundingBox(
                x: d.BoundingBox.X,
                y: d.BoundingBox.Y,
                width: d.BoundingBox.Width,
                height: d.BoundingBox.Height) : null,
            severity: d.Severity)).ToList();
    }

    /// <summary>
    /// 清理所有資源（關閉應用程式時調用）
    /// 停止所有背景服務以確保進程能正常結束
    /// </summary>
    public async Task CleanupAsync()
    {
        _logger?.LogInformation("========== 開始清理應用程式資源 ==========");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // 1. 停止 Auto Run 服務
            if (_autoRunService != null && IsRunning && IsLineScanMode)
            {
                _logger?.LogInformation("[Cleanup 1/7] 停止 Auto Run 服務...");
                try
                {
                    await WithTimeout(_autoRunService.StopAsync(CancellationToken.None), 5000, "AutoRun.StopAsync");
                    _logger?.LogInformation("[Cleanup 1/7] ✓ Auto Run 服務已停止 ({ElapsedMs}ms)", sw.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[Cleanup 1/7] 停止 Auto Run 服務時發生錯誤");
                }
            }
            else
            {
                _logger?.LogInformation("[Cleanup 1/7] Auto Run 未運行，跳過");
            }

            // 2. 停止 PLC 交握服務
            if (_plcHandshake.IsRunning)
            {
                _logger?.LogInformation("[Cleanup 2/7] 停止 PLC 交握服務...");
                try
                {
                    await WithTimeout(_plcHandshake.StopAsync(), 5000, "PlcHandshake.StopAsync");
                    _logger?.LogInformation("[Cleanup 2/7] ✓ PLC 交握服務已停止 ({ElapsedMs}ms)", sw.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[Cleanup 2/7] 停止 PLC 交握服務時發生錯誤");
                }
            }
            else
            {
                _logger?.LogInformation("[Cleanup 2/7] PLC 交握服務未運行，跳過");
            }

            // 3. 斷開 PLC 連線
            if (_plcCommunication.IsConnected)
            {
                _logger?.LogInformation("[Cleanup 3/7] 斷開 PLC 連線...");
                try
                {
                    await WithTimeout(_plcCommunication.DisconnectAsync(), 3000, "PlcCommunication.DisconnectAsync");
                    _logger?.LogInformation("[Cleanup 3/7] ✓ PLC 連線已斷開 ({ElapsedMs}ms)", sw.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[Cleanup 3/7] 斷開 PLC 連線時發生錯誤");
                }
            }
            else
            {
                _logger?.LogInformation("[Cleanup 3/7] PLC 未連線，跳過");
            }

            // 4. 停止 IoPanel 輪詢
            _logger?.LogInformation("[Cleanup 4/7] 停止 IoPanel 輪詢...");
            _ioPanelViewModel?.StopPolling();
            _logger?.LogInformation("[Cleanup 4/7] ✓ IoPanel 輪詢已停止 ({ElapsedMs}ms)", sw.ElapsedMilliseconds);

            // 5. 停止 Line Scan 服務
            if (_lineScanService?.IsScanning == true)
            {
                _logger?.LogInformation("[Cleanup 5/7] 停止 Line Scan 服務...");
                try
                {
                    await WithTimeout(_lineScanService.StopAndResetAsync(CancellationToken.None), 5000, "LineScan.StopAndResetAsync");
                    _logger?.LogInformation("[Cleanup 5/7] ✓ Line Scan 服務已停止 ({ElapsedMs}ms)", sw.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[Cleanup 5/7] 停止 Line Scan 服務時發生錯誤");
                }
            }
            else
            {
                _logger?.LogInformation("[Cleanup 5/7] Line Scan 服務未運行，跳過");
            }

            // 6. 停止相機預覽並關閉相機
            if (_camera.IsOpen)
            {
                _logger?.LogInformation("[Cleanup 6/7] 關閉相機...");
                try
                {
                    await WithTimeout(_camera.StopPreviewAsync(CancellationToken.None), 3000, "Camera.StopPreviewAsync");
                    await WithTimeout(_camera.DisposeAsync().AsTask(), 3000, "Camera.DisposeAsync");
                    _logger?.LogInformation("[Cleanup 6/7] ✓ 相機已關閉 ({ElapsedMs}ms)", sw.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[Cleanup 6/7] 關閉相機時發生錯誤");
                }
            }
            else
            {
                _logger?.LogInformation("[Cleanup 6/7] 相機未開啟，跳過");
            }

            // 7. 停止光源控制器（釋放 TCP Server 監聽資源）
            if (_lightPort is IAsyncDisposable disposableLight)
            {
                _logger?.LogInformation("[Cleanup 7/7] 停止光源控制器...");
                try
                {
                    await WithTimeout(disposableLight.DisposeAsync().AsTask(), 3000, "LightPort.DisposeAsync");
                    _logger?.LogInformation("[Cleanup 7/7] ✓ 光源控制器已停止 ({ElapsedMs}ms)", sw.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[Cleanup 7/7] 停止光源控制器時發生錯誤");
                }
            }
            else
            {
                _logger?.LogInformation("[Cleanup 7/7] 光源控制器未初始化，跳過");
            }

            sw.Stop();
            _logger?.LogInformation("========== 應用程式資源清理完成 (總耗時: {ElapsedMs}ms) ==========", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "清理應用程式資源時發生未預期錯誤");
        }
    }

    /// <summary>
    /// 帶超時的 Task 包裝器
    /// </summary>
    private async Task WithTimeout(Task task, int timeoutMs, string operationName)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var completedTask = await Task.WhenAny(task, Task.Delay(timeoutMs, cts.Token));

        if (completedTask != task)
        {
            _logger?.LogWarning("{Operation} 超時 ({TimeoutMs}ms)，強制跳過", operationName, timeoutMs);
            return;
        }

        // 傳播原始例外
        await task;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _clockTimer.Stop();
        _messenger.Unregister<WorkOrderChangedMessage>(this);

        // 取消訂閱相機幀事件
        _camera.FrameReceived -= OnCameraFrameReceived;

        // 取消訂閱 PLC 事件
        UnsubscribePlcEvents();
        _plcCommunication.ConnectionStateChanged -= OnPlcConnectionStateChanged;

        // 取消訂閱 Auto Run 事件
        UnsubscribeAutoRunEvents();

        // 取消訂閱專案初始化事件
        if (_projectInitService != null)
        {
            _projectInitService.StatusChanged -= OnProjectInitStatusChanged;
        }

        // 取消訂閱專案變更事件
        if (_projectConfigService != null)
        {
            _projectConfigService.CurrentProjectChanged -= OnCurrentProjectChanged;
        }
    }
}
