using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using AIVision.Presentation.Wpf.Models;
using AIVision.Presentation.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AIVision.Presentation.Wpf.ViewModels;

/// <summary>
/// 模型編輯 ViewModel。
/// </summary>
public partial class ModelEditViewModel : ObservableObject
{
    private readonly ModelConfigService _configService;
    private readonly ILogger<ModelEditViewModel>? _logger;
    private readonly string? _originalName;

    public event EventHandler<bool>? RequestClose;

    public ModelEditViewModel(
        ModelConfigService configService,
        ModelConfig? model = null,
        ILogger<ModelEditViewModel>? logger = null)
    {
        _configService = configService;
        _logger = logger;
        
        if (model != null)
        {
            // 編輯模式
            _originalName = model.Name;
            Name = model.Name;
            Description = model.Description;
            EdgeHubHost = model.EdgeHubHost;
            EdgeHubPort = model.EdgeHubPort;
            Uuid = model.Uuid;
            ModelPath = model.ModelPath;
            InferencePort = model.InferencePort;
            ModelType = model.ModelType;
            SelectedInferenceType = model.InferenceType;
            WorkflowSettingPath = model.WorkflowSettingPath ?? string.Empty;
            WorkflowPort = model.WorkflowPort;
            IsEditMode = true;

            // 載入結果類型
            foreach (var typeName in model.ResultTypes)
            {
                var displayName = model.GetDisplayName(typeName);
                ResultTypes.Add(new ResultTypeItemViewModel(typeName, displayName));
            }

            // 載入瑕疵過濾設定
            if (model.DefectFiltering != null)
            {
                DefectFilteringEnabled = model.DefectFiltering.Enabled;
                PixelAreaMm2 = model.DefectFiltering.PixelAreaMm2;
                PixelSizeMm = model.DefectFiltering.PixelSizeMm;
                MinimumAreaMm2 = model.DefectFiltering.MinimumAreaMm2;
                MediumAreaMm2 = model.DefectFiltering.MediumAreaMm2;
                CloseDistanceMm = model.DefectFiltering.CloseDistanceMm;
                CriticalClassesText = string.Join(", ", model.DefectFiltering.CriticalClasses);
            }
        }
        else
        {
            // 新增模式
            EdgeHubHost = "192.168.1.95";
            EdgeHubPort = 5001;
            Uuid = "model";
            InferencePort = 8001;
            SelectedInferenceType = InferenceType.SingleModel;
            WorkflowPort = 8100;
            IsEditMode = false;

            // 預設結果類型
            ResultTypes.Add(new ResultTypeItemViewModel("OK", "良品"));
            ResultTypes.Add(new ResultTypeItemViewModel("NG", "不良品"));
        }
    }

    [ObservableProperty]
    private string name = string.Empty;

    [ObservableProperty]
    private string? description;

    [ObservableProperty]
    private string edgeHubHost = "192.168.1.95";

    [ObservableProperty]
    private int edgeHubPort = 5001;

    [ObservableProperty]
    private string uuid = "model";

    [ObservableProperty]
    private string modelPath = string.Empty;

    [ObservableProperty]
    private int inferencePort = 8001;

    [ObservableProperty]
    private ModelType modelType = ModelType.Classification;

    [ObservableProperty]
    private bool isEditMode;

    [ObservableProperty]
    private string? errorMessage;

    // ===== Workflow 相關屬性 =====

    /// <summary>
    /// 推論類型：SingleModel 或 Workflow
    /// </summary>
    [ObservableProperty]
    private InferenceType selectedInferenceType = InferenceType.SingleModel;

    /// <summary>
    /// Workflow 設定檔路徑
    /// </summary>
    [ObservableProperty]
    private string workflowSettingPath = string.Empty;

    /// <summary>
    /// Workflow 推論服務 Port
    /// </summary>
    [ObservableProperty]
    private int workflowPort = 8100;

    /// <summary>
    /// 是否為 Workflow 模式（用於 UI 可見性綁定）
    /// </summary>
    public bool IsWorkflowMode => SelectedInferenceType == InferenceType.Workflow;

    /// <summary>
    /// 是否為單一模型模式（用於 UI 可見性綁定）
    /// </summary>
    public bool IsSingleModelMode => SelectedInferenceType == InferenceType.SingleModel;

    /// <summary>
    /// 當 SelectedInferenceType 改變時，通知 UI 更新可見性
    /// </summary>
    partial void OnSelectedInferenceTypeChanged(InferenceType value)
    {
        OnPropertyChanged(nameof(IsWorkflowMode));
        OnPropertyChanged(nameof(IsSingleModelMode));
    }

    /// <summary>
    /// 瀏覽 Workflow 設定檔
    /// </summary>
    [RelayCommand]
    private void BrowseWorkflowSetting()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "選擇 Workflow 設定檔",
            Filter = "JSON 檔案 (*.json)|*.json|所有檔案 (*.*)|*.*",
            InitialDirectory = @"C:\Users\Public\Documents\Spingence\AINavi\workflows"
        };

        if (dialog.ShowDialog() == true)
        {
            WorkflowSettingPath = dialog.FileName;
        }
    }

    // ===== 結果類型相關屬性 =====

    /// <summary>
    /// 結果類型列表（用於 UI 綁定）
    /// </summary>
    public ObservableCollection<ResultTypeItemViewModel> ResultTypes { get; } = new();

    /// <summary>
    /// 當前選中的結果類型項目
    /// </summary>
    [ObservableProperty]
    private ResultTypeItemViewModel? selectedResultType;

    /// <summary>
    /// 新增結果類型的輸入值
    /// </summary>
    [ObservableProperty]
    private string newResultTypeName = string.Empty;

    /// <summary>
    /// 新增結果類型的顯示名稱
    /// </summary>
    [ObservableProperty]
    private string newResultTypeDisplayName = string.Empty;

    // ===== 瑕疵過濾相關屬性 =====

    /// <summary>
    /// 是否啟用瑕疵過濾
    /// </summary>
    [ObservableProperty]
    private bool defectFilteringEnabled = false;

    /// <summary>
    /// 單一像素面積 (mm²)
    /// </summary>
    [ObservableProperty]
    private double pixelAreaMm2 = 0.00000576;

    /// <summary>
    /// 單一像素邊長 (mm)
    /// </summary>
    [ObservableProperty]
    private double pixelSizeMm = 0.0024;

    /// <summary>
    /// 最小檢出面積閾值 (mm²)
    /// </summary>
    [ObservableProperty]
    private double minimumAreaMm2 = 0.02;

    /// <summary>
    /// 中等/大瑕疵分界閾值 (mm²)
    /// </summary>
    [ObservableProperty]
    private double mediumAreaMm2 = 0.05;

    /// <summary>
    /// 群聚距離閾值 (mm)
    /// </summary>
    [ObservableProperty]
    private double closeDistanceMm = 50.0;

    /// <summary>
    /// 關鍵瑕疵類別（逗號分隔）
    /// </summary>
    [ObservableProperty]
    private string criticalClassesText = string.Empty;

    // ===== 結果類型操作命令 =====

    /// <summary>
    /// 新增結果類型
    /// </summary>
    [RelayCommand]
    private void AddResultType()
    {
        if (string.IsNullOrWhiteSpace(NewResultTypeName))
            return;

        var typeName = NewResultTypeName.Trim();

        // 檢查是否已存在
        if (ResultTypes.Any(r => r.TypeName.Equals(typeName, StringComparison.OrdinalIgnoreCase)))
        {
            ErrorMessage = $"結果類型 '{typeName}' 已存在";
            return;
        }

        ErrorMessage = null;
        ResultTypes.Add(new ResultTypeItemViewModel(
            typeName,
            string.IsNullOrWhiteSpace(NewResultTypeDisplayName) ? typeName : NewResultTypeDisplayName.Trim()
        ));

        // 清空輸入
        NewResultTypeName = string.Empty;
        NewResultTypeDisplayName = string.Empty;
    }

    /// <summary>
    /// 刪除選中的結果類型
    /// </summary>
    [RelayCommand]
    private void RemoveResultType()
    {
        if (SelectedResultType == null)
            return;

        ResultTypes.Remove(SelectedResultType);
        SelectedResultType = null;
    }

    /// <summary>
    /// 上移結果類型
    /// </summary>
    [RelayCommand]
    private void MoveResultTypeUp()
    {
        if (SelectedResultType == null)
            return;

        var index = ResultTypes.IndexOf(SelectedResultType);
        if (index > 0)
        {
            ResultTypes.Move(index, index - 1);
        }
    }

    /// <summary>
    /// 下移結果類型
    /// </summary>
    [RelayCommand]
    private void MoveResultTypeDown()
    {
        if (SelectedResultType == null)
            return;

        var index = ResultTypes.IndexOf(SelectedResultType);
        if (index < ResultTypes.Count - 1)
        {
            ResultTypes.Move(index, index + 1);
        }
    }

    /// <summary>
    /// 儲存模型配置。
    /// </summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        // 驗證
        if (string.IsNullOrWhiteSpace(Name))
        {
            ErrorMessage = "模型名稱不能為空";
            return;
        }

        if (string.IsNullOrWhiteSpace(EdgeHubHost))
        {
            ErrorMessage = "EdgeHub IP 不能為空";
            return;
        }

        if (EdgeHubPort <= 0 || EdgeHubPort > 65535)
        {
            ErrorMessage = "EdgeHub Port 必須在 1-65535 之間";
            return;
        }

        // 根據推論類型驗證必要欄位
        if (SelectedInferenceType == InferenceType.SingleModel)
        {
            if (string.IsNullOrWhiteSpace(ModelPath))
            {
                ErrorMessage = "模型路徑不能為空";
                return;
            }

            if (InferencePort <= 0 || InferencePort > 65535)
            {
                ErrorMessage = "推論 Port 必須在 1-65535 之間";
                return;
            }
        }
        else // Workflow
        {
            if (string.IsNullOrWhiteSpace(WorkflowSettingPath))
            {
                ErrorMessage = "Workflow 設定檔路徑不能為空";
                return;
            }

            if (!System.IO.File.Exists(WorkflowSettingPath))
            {
                ErrorMessage = $"Workflow 設定檔不存在: {WorkflowSettingPath}";
                return;
            }

            if (WorkflowPort <= 0 || WorkflowPort > 65535)
            {
                ErrorMessage = "Workflow Port 必須在 1-65535 之間";
                return;
            }
        }

        try
        {
            ErrorMessage = null;

            // 過濾掉空白的結果類型
            var validResultTypes = ResultTypes
                .Where(r => !string.IsNullOrWhiteSpace(r.TypeName))
                .ToList();

            if (validResultTypes.Count == 0)
            {
                ErrorMessage = "至少需要定義一個結果類型";
                return;
            }

            // 解析關鍵瑕疵類別
            var criticalClasses = string.IsNullOrWhiteSpace(CriticalClassesText)
                ? new List<string>()
                : CriticalClassesText.Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();

            var model = new ModelConfig
            {
                Name = Name.Trim(),
                Description = Description?.Trim(),
                EdgeHubHost = EdgeHubHost.Trim(),
                EdgeHubPort = EdgeHubPort,
                Uuid = Uuid.Trim(),
                ModelPath = ModelPath.Trim(),
                InferencePort = InferencePort,
                ModelType = ModelType,
                InferenceType = SelectedInferenceType,
                WorkflowSettingPath = WorkflowSettingPath?.Trim(),
                WorkflowPort = WorkflowPort,
                ResultTypes = validResultTypes.Select(r => r.TypeName.Trim()).ToList(),
                ResultTypeDisplayNames = validResultTypes
                    .ToDictionary(
                        r => r.TypeName.Trim(),
                        r => string.IsNullOrWhiteSpace(r.DisplayName) ? r.TypeName.Trim() : r.DisplayName.Trim()),
                // 瑕疵過濾設定
                DefectFiltering = new DefectFilteringConfig
                {
                    Enabled = DefectFilteringEnabled,
                    PixelAreaMm2 = PixelAreaMm2,
                    PixelSizeMm = PixelSizeMm,
                    MinimumAreaMm2 = MinimumAreaMm2,
                    MediumAreaMm2 = MediumAreaMm2,
                    CloseDistanceMm = CloseDistanceMm,
                    CriticalClasses = criticalClasses
                }
            };

            if (IsEditMode && _originalName != null)
            {
                // 更新模式
                await _configService.UpdateModelAsync(_originalName, model);
                _logger?.LogInformation("已更新模型: {ModelName}", model.Name);
            }
            else
            {
                // 新增模式
                await _configService.AddModelAsync(model);
                _logger?.LogInformation("已新增模型: {ModelName}", model.Name);
            }

            RequestClose?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger?.LogError(ex, "儲存模型配置失敗");
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke(this, false);
    }
}

