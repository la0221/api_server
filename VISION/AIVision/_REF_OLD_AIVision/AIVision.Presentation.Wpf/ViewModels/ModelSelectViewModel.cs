using System;
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
/// 模型選擇 ViewModel（簡化版，僅用於選擇模型）。
/// </summary>
public partial class ModelSelectViewModel : ObservableObject
{
    private readonly ModelConfigService _configService;
    private readonly AinaviApiClient _apiClient;
    private readonly ILogger<ModelSelectViewModel>? _logger;

    public event EventHandler<ModelConfig>? ModelSelected;

    public ModelSelectViewModel(
        ModelConfigService configService,
        AinaviApiClient apiClient,
        ILogger<ModelSelectViewModel>? logger = null)
    {
        _configService = configService;
        _apiClient = apiClient;
        _logger = logger;

        Models = new ObservableCollection<ModelConfig>();
        _ = LoadModelsAsync();
    }

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
    /// 載入模型列表。
    /// </summary>
    [RelayCommand]
    private async Task LoadModelsAsync()
    {
        try
        {
            var config = await _configService.LoadAsync();
            
            Models.Clear();
            foreach (var model in config.Models)
            {
                Models.Add(model);
            }

            CurrentModelName = config.CurrentModelName;
            SelectedModel = Models.FirstOrDefault(m => m.Name == CurrentModelName);

            _logger?.LogInformation("已載入 {Count} 個模型配置", Models.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "載入模型列表失敗");
            StatusMessage = $"載入失敗: {ex.Message}";
        }
    }

    /// <summary>
    /// 載入選中的模型。
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
            StatusMessage = $"正在載入模型 {SelectedModel.Name}，請稍候... (約需 15-20 秒)";

            _logger?.LogInformation("開始載入模型: {ModelName}", SelectedModel.Name);

            // 呼叫 API 載入模型
            var result = await _apiClient.OpenModelAsync(
                SelectedModel.GetEdgeHubUrl(),
                SelectedModel.Uuid,
                SelectedModel.ModelPath,
                SelectedModel.InferencePort);

            if (result.Success)
            {
                // 更新目前模型
                await _configService.SetCurrentModelAsync(SelectedModel.Name);
                CurrentModelName = SelectedModel.Name;

                StatusMessage = $"✓ 模型 {SelectedModel.Name} 已成功載入";
                _logger?.LogInformation(
                    "模型載入成功: {ModelName}, EdgeHub: {EdgeHubUrl}, Inference: {InferenceUrl}",
                    SelectedModel.Name, 
                    SelectedModel.GetEdgeHubUrl(), 
                    SelectedModel.GetInferenceUrl());

                System.Windows.MessageBox.Show(
                    $"模型 {SelectedModel.Name} 已成功載入\n\n" +
                    $"EdgeHub: {SelectedModel.GetEdgeHubUrl()}\n" +
                    $"Inference: {SelectedModel.GetInferenceUrl()}",
                    "載入成功",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                // 觸發事件通知主視窗
                ModelSelected?.Invoke(this, SelectedModel);
            }
            else
            {
                StatusMessage = $"✗ 載入失敗: {result.Message}";
                _logger?.LogWarning("模型載入失敗: {ModelName}, Message: {Message}", SelectedModel.Name, result.Message);

                System.Windows.MessageBox.Show(
                    $"模型載入失敗\n\n模型: {SelectedModel.Name}\n\n錯誤訊息:\n{result.Message}",
                    "載入失敗",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"✗ 發生錯誤: {ex.Message}";
            _logger?.LogError(ex, "載入模型時發生異常: {ModelName}", SelectedModel?.Name);

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

    [RelayCommand]
    private void Cancel()
    {
        // 關閉視窗
    }
}

