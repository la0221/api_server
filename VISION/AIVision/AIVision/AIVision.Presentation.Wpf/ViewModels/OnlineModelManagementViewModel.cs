using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using AIVision.Application.Ports.MoldCode;
using AIVision.Infrastructure.AiService;
using AIVision.Presentation.Wpf.Models;
using AIVision.Presentation.Wpf.Services;
using AIVision.Presentation.Wpf.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using InferenceType = AIVision.Presentation.Wpf.Models.InferenceType;

namespace AIVision.Presentation.Wpf.ViewModels;

/// <summary>
/// 線上模型管理 ViewModel（AINAVI EdgeHub API）。
/// <para>
/// 這是原本「模型管理」的 AINAVI 版本：透過 <see cref="AinaviApiClient"/> 直接呼叫 EdgeHub
/// 載入模型 / Workflow、關閉服務，並同步推論端點到 <see cref="SwitchableAiInferencePort"/>。
/// 模型來源為獨立的 <c>models.online.json</c>（與離線本地 ONNX 的 models.json 完全分離），
/// 內部以向後兼容建構子建立專屬的 <see cref="ModelConfigService"/>（不自動掃描）。
/// </para>
/// </summary>
public partial class OnlineModelManagementViewModel : ObservableObject
{
    private const string OnlineConfigFileName = "models.online.json";

    private readonly ModelConfigService _configService;
    private readonly AinaviApiClient _apiClient;
    private readonly SwitchableAiInferencePort? _switchablePort;
    private readonly ILogger<OnlineModelManagementViewModel>? _logger;

    /// <summary>
    /// 模型載入成功事件（供主視窗或其他訂閱者使用）。
    /// </summary>
    public event EventHandler<ModelConfig>? ModelLoaded;

    /// <summary>
    /// 建構子。內部以向後兼容建構子建立專屬於線上模型的 <see cref="ModelConfigService"/>
    /// （指向 <c>models.online.json</c>，不啟用自動掃描）。
    /// </summary>
    public OnlineModelManagementViewModel(
        AinaviApiClient apiClient,
        SwitchableAiInferencePort? switchablePort = null,
        ILogger<OnlineModelManagementViewModel>? logger = null)
    {
        _apiClient = apiClient;
        _switchablePort = switchablePort;
        _logger = logger;

        // 線上模型來源：獨立的 models.online.json（與離線 models.json 分離）。
        // 使用向後兼容建構子 → 不自動掃描，直接載入 json 內的 5 個 AINAVI 模型。
        var onlineConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, OnlineConfigFileName);
        _configService = new ModelConfigService(onlineConfigPath, logger: null);

        Models = new ObservableCollection<ModelConfig>();
        _ = LoadModelsAsync();
    }

    /// <summary>
    /// 模型列表。
    /// </summary>
    public ObservableCollection<ModelConfig> Models { get; }

    [ObservableProperty]
    private ModelConfig? selectedModel;

    [ObservableProperty]
    private string? currentModelName;

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private string? statusMessage;

    /// <summary>
    /// 載入模型列表（從 models.online.json）。
    /// </summary>
    [RelayCommand]
    private async Task LoadModelsAsync()
    {
        try
        {
            var allModels = await _configService.GetAllModelsAsync();
            var config = await _configService.LoadAsync();

            Models.Clear();
            foreach (var model in allModels)
            {
                Models.Add(model);
            }

            CurrentModelName = config.CurrentModelName;
            SelectedModel = Models.FirstOrDefault(m =>
                m.OriginalName == CurrentModelName || m.Name == CurrentModelName);

            _logger?.LogInformation("[Online] 已載入 {Count} 個線上模型配置", Models.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[Online] 載入線上模型列表失敗");
            StatusMessage = $"載入失敗: {ex.Message}";
        }
    }

    /// <summary>
    /// 載入選中的模型 / Workflow（呼叫 AINAVI EdgeHub API）。
    /// </summary>
    [RelayCommand]
    private async Task LoadSelectedModelAsync()
    {
        if (SelectedModel == null)
        {
            System.Windows.MessageBox.Show("請選擇一個模型", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            IsLoading = true;

            var isWorkflow = SelectedModel.InferenceType == InferenceType.Workflow;
            var typeLabel = isWorkflow ? "Workflow" : "模型";

            // 先關閉現有服務，再載入新服務
            StatusMessage = "正在關閉現有服務...";
            _logger?.LogInformation("[Online] 載入新{TypeLabel}前，先關閉現有服務", typeLabel);

            var closeResult = await _apiClient.CloseAllServicesAsync(SelectedModel.GetEdgeHubUrl());
            if (closeResult.Success)
            {
                _logger?.LogInformation("[Online] 現有服務已關閉: {Message}", closeResult.Message);
            }
            else
            {
                _logger?.LogWarning("[Online] 關閉現有服務失敗（繼續載入）: {Message}", closeResult.Message);
            }

            // 等待一下讓服務完全關閉
            await Task.Delay(500);

            StatusMessage = $"正在載入{typeLabel} {SelectedModel.Name}，請稍候... (約需 15-20 秒)";
            _logger?.LogInformation("[Online] 開始載入{TypeLabel}: {ModelName}, InferenceType={InferenceType}",
                typeLabel, SelectedModel.Name, SelectedModel.InferenceType);

            ModelLoadResultDto result;

            if (isWorkflow)
            {
                // Workflow 模式：呼叫 /service/workflow API
                if (string.IsNullOrWhiteSpace(SelectedModel.WorkflowSettingPath))
                {
                    StatusMessage = "✗ Workflow 設定檔路徑未設定";
                    System.Windows.MessageBox.Show(
                        "請先編輯模型並設定 Workflow 設定檔路徑",
                        "設定錯誤",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                result = await _apiClient.OpenWorkflowAsync(
                    SelectedModel.GetEdgeHubUrl(),
                    SelectedModel.WorkflowSettingPath,
                    SelectedModel.WorkflowPort);
            }
            else
            {
                // 單一模型模式：呼叫 /services/inference API
                result = await _apiClient.OpenModelAsync(
                    SelectedModel.GetEdgeHubUrl(),
                    SelectedModel.Uuid,
                    SelectedModel.ModelPath,
                    SelectedModel.InferencePort);
            }

            if (result.Success)
            {
                // 更新目前模型（持久化到 models.online.json）
                await _configService.SetCurrentModelAsync(SelectedModel.Name);
                CurrentModelName = SelectedModel.Name;

                // 同步推論端點到 SwitchableAiInferencePort
                if (_switchablePort != null)
                {
                    if (isWorkflow)
                    {
                        _switchablePort.WorkflowPort.SetEndpoint(
                            SelectedModel.EdgeHubHost,
                            SelectedModel.WorkflowPort);
                        _switchablePort.SwitchTo(Application.Configuration.InferenceType.Workflow);
                        _logger?.LogInformation("[Online] 已同步 Workflow 端點: {Host}:{Port}",
                            SelectedModel.EdgeHubHost, SelectedModel.WorkflowPort);
                    }
                    else
                    {
                        _switchablePort.SwitchTo(Application.Configuration.InferenceType.SingleModel);
                        _logger?.LogInformation("[Online] 已切換到單一模型模式: {Host}:{Port}",
                            SelectedModel.EdgeHubHost, SelectedModel.InferencePort);
                    }
                }

                StatusMessage = $"✓ {typeLabel} {SelectedModel.Name} 已成功載入";
                _logger?.LogInformation("[Online] {TypeLabel}載入成功: {ModelName}", typeLabel, SelectedModel.Name);

                System.Windows.MessageBox.Show(
                    $"{typeLabel} {SelectedModel.Name} 已成功載入\n\n" +
                    $"EdgeHub: {SelectedModel.GetEdgeHubUrl()}\n" +
                    $"Inference: {SelectedModel.GetInferenceUrl()}\n" +
                    $"類型: {(isWorkflow ? "Workflow" : "單一模型")}",
                    "載入成功",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                ModelLoaded?.Invoke(this, SelectedModel);
            }
            else
            {
                StatusMessage = $"✗ 載入失敗: {result.Message}";
                _logger?.LogWarning("[Online] {TypeLabel}載入失敗: {ModelName}, Message: {Message}",
                    typeLabel, SelectedModel.Name, result.Message);

                System.Windows.MessageBox.Show(
                    $"{typeLabel}載入失敗\n\n模型: {SelectedModel.Name}\n\n錯誤訊息:\n{result.Message}",
                    "載入失敗",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"✗ 發生錯誤: {ex.Message}";
            _logger?.LogError(ex, "[Online] 載入模型時發生異常: {ModelName}", SelectedModel?.Name);

            System.Windows.MessageBox.Show(
                $"載入模型時發生錯誤\n\n{ex.Message}",
                "錯誤",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 關閉所有 AINAVI EdgeHub 服務。
    /// </summary>
    [RelayCommand]
    private async Task CloseServicesAsync()
    {
        if (SelectedModel == null)
        {
            System.Windows.MessageBox.Show(
                "請先選擇一個模型以決定 EdgeHub 位址", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            IsLoading = true;
            StatusMessage = "正在關閉所有服務...";

            var result = await _apiClient.CloseAllServicesAsync(SelectedModel.GetEdgeHubUrl());

            StatusMessage = result.Success
                ? $"✓ 服務已關閉: {result.Message}"
                : $"✗ 關閉服務失敗: {result.Message}";

            _logger?.LogInformation("[Online] 關閉服務: Success={Success}, Message={Message}",
                result.Success, result.Message);
        }
        catch (Exception ex)
        {
            StatusMessage = $"✗ 關閉服務時發生錯誤: {ex.Message}";
            _logger?.LogError(ex, "[Online] 關閉服務時發生異常");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 新增模型（沿用 AINAVI 形式的編輯對話框）。
    /// </summary>
    [RelayCommand]
    private async Task AddModelAsync()
    {
        try
        {
            var editViewModel = new ModelEditViewModel(_configService, null, null);
            var editView = new ModelEditView(editViewModel)
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };

            var result = editView.ShowDialog();
            if (result == true)
            {
                await LoadModelsAsync();
                StatusMessage = "已新增模型";
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[Online] 新增模型失敗");
            System.Windows.MessageBox.Show(
                $"新增模型失敗\n\n{ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 編輯模型。
    /// </summary>
    [RelayCommand]
    private async Task EditModelAsync()
    {
        if (SelectedModel == null)
        {
            System.Windows.MessageBox.Show("請選擇一個模型", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var editViewModel = new ModelEditViewModel(_configService, SelectedModel, null);
            var editView = new ModelEditView(editViewModel)
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };

            var result = editView.ShowDialog();
            if (result == true)
            {
                await LoadModelsAsync();
                StatusMessage = "已更新模型";
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[Online] 編輯模型失敗");
            System.Windows.MessageBox.Show(
                $"編輯模型失敗\n\n{ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 刪除模型。
    /// </summary>
    [RelayCommand]
    private async Task DeleteModelAsync()
    {
        if (SelectedModel == null)
        {
            System.Windows.MessageBox.Show("請選擇一個模型", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            $"確定要刪除模型 '{SelectedModel.Name}' 嗎？",
            "確認刪除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _configService.DeleteModelAsync(SelectedModel.Name);
            await LoadModelsAsync();
            StatusMessage = $"已刪除模型: {SelectedModel.Name}";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[Online] 刪除模型失敗: {ModelName}", SelectedModel.Name);
            System.Windows.MessageBox.Show(
                $"刪除模型失敗\n\n{ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
