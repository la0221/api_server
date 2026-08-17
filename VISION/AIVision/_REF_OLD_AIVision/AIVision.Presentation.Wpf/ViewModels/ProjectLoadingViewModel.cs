using System.Windows.Media;
using System.Windows.Threading;
using AIVision.Application.Configuration;
using AIVision.Application.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AIVision.Presentation.Wpf.ViewModels;

/// <summary>
/// 專案載入進度視窗 ViewModel
/// </summary>
public partial class ProjectLoadingViewModel : ObservableObject
{
    private readonly IProjectInitializationService _initService;
    private readonly ILogger<ProjectLoadingViewModel> _logger;
    private readonly Dispatcher _dispatcher;

    private ProjectConfig? _project;

    [ObservableProperty]
    private string _projectName = string.Empty;

    [ObservableProperty]
    private int _progress;

    [ObservableProperty]
    private string _currentTask = "準備初始化...";

    [ObservableProperty]
    private System.Windows.Media.Brush _plcStatusColor = System.Windows.Media.Brushes.Gray;

    [ObservableProperty]
    private System.Windows.Media.Brush _cameraStatusColor = System.Windows.Media.Brushes.Gray;

    [ObservableProperty]
    private System.Windows.Media.Brush _lightStatusColor = System.Windows.Media.Brushes.Gray;

    [ObservableProperty]
    private System.Windows.Media.Brush _aiModelStatusColor = System.Windows.Media.Brushes.Gray;

    [ObservableProperty]
    private string? _plcError;

    [ObservableProperty]
    private string? _cameraError;

    [ObservableProperty]
    private string? _lightError;

    [ObservableProperty]
    private string? _aiModelError;

    [ObservableProperty]
    private bool _isComplete;

    [ObservableProperty]
    private bool _hasFailure;

    [ObservableProperty]
    private bool _showButtons;

    public event Action<bool>? RequestClose;

    public ProjectLoadingViewModel(
        IProjectInitializationService initService,
        ILogger<ProjectLoadingViewModel> logger)
    {
        _initService = initService;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        // 訂閱狀態變更
        _initService.StatusChanged += OnStatusChanged;
    }

    /// <summary>
    /// 設定要載入的專案並開始初始化
    /// </summary>
    public void SetProject(ProjectConfig project)
    {
        _project = project;
        ProjectName = project.Name;
    }

    /// <summary>
    /// 開始初始化
    /// </summary>
    public async Task StartInitializationAsync()
    {
        if (_project == null)
        {
            _logger.LogError("未設定專案，無法初始化");
            return;
        }

        _logger.LogInformation("開始初始化專案: {Name}", _project.Name);

        try
        {
            var result = await _initService.InitializeAsync(_project);

            // 初始化完成，顯示按鈕
            ShowButtons = true;
            IsComplete = true;
            HasFailure = !result.AllSuccess;

            if (!result.AllSuccess)
            {
                _logger.LogWarning("部分設備初始化失敗: {Devices}",
                    string.Join(", ", result.FailedDevices));
            }
            else
            {
                _logger.LogInformation("所有設備初始化成功");

                // 如果全部成功，自動關閉視窗
                await Task.Delay(500); // 短暫顯示成功狀態
                RequestClose?.Invoke(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "初始化過程發生未預期錯誤");
            ShowButtons = true;
            IsComplete = true;
            HasFailure = true;
        }
    }

    /// <summary>
    /// 重試失敗的設備
    /// </summary>
    [RelayCommand]
    private async Task RetryAsync()
    {
        _logger.LogInformation("重試失敗的設備...");

        ShowButtons = false;
        HasFailure = false;
        IsComplete = false;

        await _initService.RetryFailedDevicesAsync();

        ShowButtons = true;
        IsComplete = true;
        HasFailure = _initService.Status.HasFailure;

        // 如果重試後全部成功，自動關閉
        if (!HasFailure)
        {
            await Task.Delay(500);
            RequestClose?.Invoke(true);
        }
    }

    /// <summary>
    /// 繼續（即使有失敗也進入主介面）
    /// </summary>
    [RelayCommand]
    private void Continue()
    {
        _logger.LogInformation("繼續進入主介面，忽略失敗的設備");
        RequestClose?.Invoke(true);
    }

    private void OnStatusChanged(object? sender, ProjectInitializationStatus status)
    {
        _dispatcher.BeginInvoke(() =>
        {
            // 更新進度
            Progress = status.ProgressPercent;
            CurrentTask = status.CurrentTask;

            // 更新 PLC 狀態
            PlcStatusColor = GetStatusBrush(status.Plc.State);
            PlcError = status.Plc.ErrorMessage;

            // 更新相機狀態
            CameraStatusColor = GetStatusBrush(status.Camera.State);
            CameraError = status.Camera.ErrorMessage;

            // 更新光源狀態
            LightStatusColor = GetStatusBrush(status.Light.State);
            LightError = status.Light.ErrorMessage;

            // 更新 AI 模型狀態
            AiModelStatusColor = GetStatusBrush(status.AiModel.State);
            AiModelError = status.AiModel.ErrorMessage;

            // 更新完成狀態
            IsComplete = status.IsComplete;
            HasFailure = status.HasFailure;
        });
    }

    private static System.Windows.Media.Brush GetStatusBrush(DeviceInitState state) => state switch
    {
        DeviceInitState.Success => System.Windows.Media.Brushes.LimeGreen,
        DeviceInitState.Error => System.Windows.Media.Brushes.Red,
        DeviceInitState.InProgress => System.Windows.Media.Brushes.Orange,
        DeviceInitState.Skipped => System.Windows.Media.Brushes.DarkGray,
        _ => System.Windows.Media.Brushes.Gray
    };
}
