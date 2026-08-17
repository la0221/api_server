using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using AIVision.Application.Contracts.WorkOrder;
using AIVision.Application.Ports.Devices;
using AIVision.Application.Ports.MoldCode;
using AIVision.Application.Services;
using AIVision.Infrastructure.AiService;
using AIVision.MoldCode.Onnx;
using AIVision.Presentation.Wpf.Models;
using AIVision.Presentation.Wpf.Services;
using AIVision.Presentation.Wpf.Services.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace AIVision.Presentation.Wpf.ViewModels;

/// <summary>
/// Offline 測試面板 ViewModel
/// </summary>
public sealed partial class OfflineTestViewModel : ObservableObject
{
    private readonly IOfflineInspectionService _offlineService;
    private readonly IWorkOrderManagementService _workOrderService;
    private readonly INavigationService _navigationService;
    private readonly IMessenger _messenger;
    private readonly IMoldCodeModelSwitch _modelSwitch;
    private readonly MoldCodeOnnxOptions _onnxOptions;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<OfflineTestViewModel> _logger;
    private readonly Services.ModelConfigService _modelConfigService;

    private Guid? _currentWorkOrderId;

    [ObservableProperty]
    private string _currentModelName = "未選擇";

    [ObservableProperty]
    private string _selectedFolderPath = string.Empty;

    [ObservableProperty]
    private string _currentImagePath = string.Empty;

    [ObservableProperty]
    private string _lastResult = "等待檢測...";

    [ObservableProperty]
    private string _lastDefectType = "-";

    [ObservableProperty]
    private float? _lastConfidence;

    [ObservableProperty]
    private bool _isProcessing = false;

    [ObservableProperty]
    private string _currentWorkOrderCode = "無工單";

    [ObservableProperty]
    private string _currentWorkOrderProduct = "未設定";

    // 統計相關屬性（來自 OfflineInspectionService.Statistics）
    public int TotalCount => _offlineService.Statistics.TotalInspected;
    public int OkCount => _offlineService.Statistics.OkCount;
    public int NgCount => _offlineService.Statistics.NgCount;
    public double YieldRate => _offlineService.Statistics.YieldRate;

    // UI 綁定屬性
    public string? SelectedFolder => string.IsNullOrEmpty(SelectedFolderPath) ? null : System.IO.Path.GetFileName(SelectedFolderPath);
    public string? SelectedWorkOrderName => CurrentWorkOrderCode == "無工單" ? null : CurrentWorkOrderCode;
    public int TotalImages => _offlineService.TotalImageCount;
    public int CurrentImageIndex => _offlineService.CurrentImageIndex;

    // 缺陷統計集合
    public ObservableCollection<DefectStatisticItem> DefectStatistics { get; } = new();

    public OfflineTestViewModel(
        IOfflineInspectionService offlineService,
        IWorkOrderManagementService workOrderService,
        INavigationService navigationService,
        IMessenger messenger,
        IMoldCodeModelSwitch modelSwitch,
        IOptions<MoldCodeOnnxOptions> onnxOptions,
        Services.ModelConfigService modelConfigService,
        ILogger<OfflineTestViewModel> logger)
    {
        _offlineService = offlineService;
        _workOrderService = workOrderService;
        _navigationService = navigationService;
        _messenger = messenger;
        _modelSwitch = modelSwitch;
        _onnxOptions = onnxOptions.Value;
        _modelConfigService = modelConfigService;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _logger = logger;

        // 訂閱工單變更訊息
        _messenger.Register<WorkOrderChangedMessage>(this, async (_, _) =>
        {
            await _dispatcher.InvokeAsync(async () =>
            {
                await LoadCurrentWorkOrderAsync();
            });
        });

        // 載入當前工單和模型
        _ = LoadCurrentWorkOrderAsync();
        _ = LoadCurrentModelAsync();
    }

    [RelayCommand]
    private async Task SelectFolderAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "選擇影像資料夾"
        };

        if (dialog.ShowDialog() == true)
        {
            SelectedFolderPath = dialog.FolderName;
            await LoadImagesAsync();
        }
    }

    private async Task LoadImagesAsync()
    {
        try
        {
            var count = await _offlineService.LoadImagesFromFolderAsync(SelectedFolderPath);
            UpdateCurrentImageInfo();

            // 通知按鈕狀態更新
            TriggerInspectionCommand.NotifyCanExecuteChanged();
            MoveToPreviousCommand.NotifyCanExecuteChanged();
            MoveToNextCommand.NotifyCanExecuteChanged();

            _logger.LogInformation("載入 {Count} 張影像", count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "載入影像失敗");
        }
    }

    [RelayCommand]
    private void SelectWorkOrder()
    {
        // 打開工單管理視窗選擇工單
        _navigationService.ShowDialog<Views.WorkOrderManagementView>();
        // 工單變更後會自動觸發 WorkOrderChangedMessage，無需手動重新載入
    }

    private async Task LoadCurrentWorkOrderAsync()
    {
        try
        {
            var workOrder = await _workOrderService.GetCurrentWorkOrderAsync(CancellationToken.None);
            if (workOrder != null)
            {
                _currentWorkOrderId = workOrder.Id;
                CurrentWorkOrderCode = workOrder.Code;
                CurrentWorkOrderProduct = workOrder.ProductName;
                OnPropertyChanged(nameof(SelectedWorkOrderName));
                _logger.LogInformation("載入工單: {Code} - {Product}", workOrder.Code, workOrder.ProductName);

                // 如果工單有關聯的模型，則載入該模型
                if (!string.IsNullOrEmpty(workOrder.ModelName))
                {
                    CurrentModelName = workOrder.ModelName;
                    await LoadModelDefectLabelsAsync(workOrder.ModelName);
                    _logger.LogInformation("載入工單關聯的模型: {ModelName}", workOrder.ModelName);
                }
            }
            else
            {
                _currentWorkOrderId = null;
                CurrentWorkOrderCode = "無工單";
                CurrentWorkOrderProduct = "未設定";
                OnPropertyChanged(nameof(SelectedWorkOrderName));
                _logger.LogWarning("目前沒有工單");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "載入當前工單失敗");
            _currentWorkOrderId = null;
            CurrentWorkOrderCode = "無工單";
            CurrentWorkOrderProduct = "未設定";
            OnPropertyChanged(nameof(SelectedWorkOrderName));
        }
    }

    private async Task LoadCurrentModelAsync()
    {
        try
        {
            var config = await _modelConfigService.LoadAsync();
            CurrentModelName = config.CurrentModelName ?? "未選擇";
            _logger.LogInformation("載入模型: {ModelName}", CurrentModelName);

            // 載入模型的缺陷標籤
            if (!string.IsNullOrEmpty(config.CurrentModelName))
            {
                await LoadModelDefectLabelsAsync(config.CurrentModelName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "載入當前模型失敗");
            CurrentModelName = "未選擇";
        }
    }

    private async Task LoadModelDefectLabelsAsync(string modelName)
    {
        try
        {
            var config = await _modelConfigService.LoadAsync();
            var model = config.Models.FirstOrDefault(m => m.Name == modelName);

            if (model == null)
            {
                _logger.LogWarning("找不到模型配置: {ModelName}", modelName);
                return;
            }

            // 清空現有的缺陷統計
            DefectStatistics.Clear();

            // 取得模型的結果類型（缺陷標籤）
            var resultTypes = model.GetEffectiveResultTypes();

            // 初始化缺陷統計
            foreach (var label in resultTypes)
            {
                DefectStatistics.Add(new DefectStatisticItem
                {
                    Label = model.GetDisplayName(label),
                    Count = 0,
                    Total = 0,
                    Ratio = 0.0
                });
            }

            _logger.LogInformation("已載入 {Count} 個缺陷標籤", DefectStatistics.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "載入模型缺陷標籤失敗: {ModelName}", modelName);
        }
    }

    [RelayCommand(CanExecute = nameof(CanMoveToPrevious))]
    private void MoveToPrevious()
    {
        _offlineService.MoveToPrevious();
        UpdateCurrentImageInfo();

        // 通知按鈕狀態更新
        MoveToPreviousCommand.NotifyCanExecuteChanged();
        MoveToNextCommand.NotifyCanExecuteChanged();
        TriggerInspectionCommand.NotifyCanExecuteChanged();
    }

    private bool CanMoveToPrevious() => _offlineService.HasPrevious && !IsProcessing;

    [RelayCommand(CanExecute = nameof(CanMoveToNext))]
    private void MoveToNext()
    {
        _offlineService.MoveToNext();
        UpdateCurrentImageInfo();

        // 通知按鈕狀態更新
        MoveToPreviousCommand.NotifyCanExecuteChanged();
        MoveToNextCommand.NotifyCanExecuteChanged();
        TriggerInspectionCommand.NotifyCanExecuteChanged();
    }

    private bool CanMoveToNext() => _offlineService.HasNext && !IsProcessing;

    [RelayCommand(CanExecute = nameof(CanTriggerInspection))]
    private async Task TriggerInspectionAsync()
    {
        try
        {
            IsProcessing = true;

            // 若有選定模型,切換本地 ONNX 辨識器到該模型(不再做 HTTP 端點切換)。
            // 實際推論由 OfflineInspectionService → Devices.IAiInferencePort(現為本地 ONNX)完成。
            if (CurrentModelName != "未選擇")
            {
                try
                {
                    var model = (await _modelConfigService.GetAllModelsAsync())
                        .FirstOrDefault(m => m.Name == CurrentModelName || m.OriginalName == CurrentModelName);

                    if (model != null && IsLocalOnnx(model))
                    {
                        _modelSwitch.LoadModel(
                            model.ModelPath,
                            model.GetEffectiveResultTypes(),
                            _onnxOptions.CodePrefix);
                        _logger.LogInformation("[Offline] 已切換本地模型: {Path} (模型: {Model})",
                            model.ModelPath, CurrentModelName);
                    }
                    else if (model == null)
                    {
                        _logger.LogWarning("找不到模型配置: {ModelName}，沿用目前已載入模型", CurrentModelName);
                    }
                    else
                    {
                        _logger.LogWarning("模型非本地 .onnx: {ModelName}，沿用目前已載入模型", CurrentModelName);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "切換本地模型失敗: {ModelName}，沿用目前已載入模型", CurrentModelName);
                }
            }

            // 執行檢測，傳入工單 ID 和模型版本
            var result = await _offlineService.InspectCurrentImageAsync(
                _currentWorkOrderId,
                CurrentModelName != "未選擇" ? CurrentModelName : null);

            // 更新結果顯示
            LastResult = result.IsPass ? "OK" : "NG";
            LastDefectType = result.DefectType ?? "-";
            LastConfidence = (float?)result.Confidence;

            // 更新統計
            UpdateStatistics();

            // 更新缺陷統計（如果有缺陷類型）
            if (!string.IsNullOrEmpty(result.DefectType))
            {
                UpdateDefectStatistics(result.DefectType);
            }

            _logger.LogInformation("檢測完成: {Result}, 模型: {Model}", LastResult, CurrentModelName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "檢測失敗");
            LastResult = "錯誤";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private bool CanTriggerInspection() =>
        !string.IsNullOrEmpty(_offlineService.CurrentImagePath) && !IsProcessing;

    /// <summary>是否為本地 .onnx 模型(僅這類可被本地辨識器載入)。</summary>
    private static bool IsLocalOnnx(ModelConfig model) =>
        !string.IsNullOrWhiteSpace(model.ModelPath) &&
        model.ModelPath.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private void ResetStatistics()
    {
        _offlineService.ResetStatistics();
        UpdateStatistics();

        // 重置缺陷統計
        foreach (var item in DefectStatistics)
        {
            item.Count = 0;
            item.Ratio = 0.0;
        }
    }

    private void UpdateDefectStatistics(string defectType)
    {
        // 找到對應的缺陷項目並更新計數
        var item = DefectStatistics.FirstOrDefault(d => d.Label == defectType);
        if (item != null)
        {
            item.Count++;
            item.Total = _offlineService.Statistics.TotalInspected;
            item.Ratio = item.Total > 0 ? (double)item.Count / item.Total : 0.0;
        }
    }

    private void UpdateCurrentImageInfo()
    {
        CurrentImagePath = _offlineService.CurrentImagePath ?? string.Empty;
        OnPropertyChanged(nameof(CurrentImageIndex));
        OnPropertyChanged(nameof(TotalImages));
        OnPropertyChanged(nameof(SelectedFolder));
    }

    private void UpdateStatistics()
    {
        // 通知統計屬性變更
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(OkCount));
        OnPropertyChanged(nameof(NgCount));
        OnPropertyChanged(nameof(YieldRate));

        // 更新缺陷統計（如果 Service 有提供 DefectCounts）
        // TODO: 當 OfflineInspectionService 支援缺陷統計時，取消註解並實作
        // DefectStatistics.Clear();
        // foreach (var (label, count) in _offlineService.Statistics.DefectCounts)
        // {
        //     DefectStatistics.Add(new DefectStatisticItem
        //     {
        //         Label = label,
        //         Count = count,
        //         Total = _offlineService.Statistics.TotalCount,
        //         Ratio = _offlineService.Statistics.TotalCount > 0 ? (double)count / _offlineService.Statistics.TotalCount : 0.0
        //     });
        // }
    }
}

/// <summary>
/// 缺陷統計項目
/// </summary>
public partial class DefectStatisticItem : ObservableObject
{
    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private int _total;

    [ObservableProperty]
    private double _ratio;
}
