using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using AIVision.Application.Ports.MoldCode;
using AIVision.MoldCode.Onnx;
using AIVision.Presentation.Wpf.Models;
using AIVision.Presentation.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVision.Presentation.Wpf.ViewModels;

/// <summary>
/// 模型選擇 ViewModel（簡化版，僅用於選擇模型）。
/// .onnx 模型走本地切換(ModelConfigService + IMoldCodeModelSwitch),不呼叫 EdgeHub。
/// </summary>
public partial class ModelSelectViewModel : ObservableObject
{
    private readonly ModelConfigService _configService;
    private readonly IMoldCodeModelSwitch _modelSwitch;
    private readonly MoldCodeOnnxOptions _onnxOptions;
    private readonly ILogger<ModelSelectViewModel>? _logger;

    public event EventHandler<ModelConfig>? ModelSelected;

    public ModelSelectViewModel(
        ModelConfigService configService,
        IMoldCodeModelSwitch modelSwitch,
        IOptions<MoldCodeOnnxOptions> onnxOptions,
        ILogger<ModelSelectViewModel>? logger = null)
    {
        _configService = configService;
        _modelSwitch = modelSwitch;
        _onnxOptions = onnxOptions.Value;
        _logger = logger;

        Models = new ObservableCollection<ModelConfig>();
        _ = LoadModelsAsync();
    }

    /// <summary>是否為本地 .onnx 模型。</summary>
    private static bool IsLocalOnnx(ModelConfig model) =>
        !string.IsNullOrWhiteSpace(model.ModelPath) &&
        model.ModelPath.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase);

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
            var allModels = await _configService.GetAllModelsAsync();

            // 僅列出本地 .onnx 模型(本站推論已全面本地化);避免選到非本地模型而無法載入。
            Models.Clear();
            foreach (var model in allModels.Where(IsLocalOnnx))
            {
                Models.Add(model);
            }

            CurrentModelName = config.CurrentModelName;
            SelectedModel = Models.FirstOrDefault(m =>
                m.Name == CurrentModelName || m.OriginalName == CurrentModelName);

            _logger?.LogInformation("已載入 {Count} 個本地 ONNX 模型配置", Models.Count);
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

            // .onnx 模型:走本地切換,不呼叫 EdgeHub(不會 hang)。
            if (IsLocalOnnx(SelectedModel))
            {
                StatusMessage = $"正在切換本地模型 {SelectedModel.Name}...";
                _logger?.LogInformation("[ModelSelect] 切換本地 ONNX 模型: {ModelName} ({Path})",
                    SelectedModel.Name, SelectedModel.ModelPath);

                // 設定目前模型(持久化) + 即時切換本地辨識器。
                await _configService.SetCurrentModelAsync(SelectedModel.Name);
                _modelSwitch.LoadModel(
                    SelectedModel.ModelPath,
                    SelectedModel.GetEffectiveResultTypes(),
                    _onnxOptions.CodePrefix);

                CurrentModelName = SelectedModel.Name;
                StatusMessage = $"✓ 本地模型 {SelectedModel.Name} 已切換";
                _logger?.LogInformation("[ModelSelect] 本地模型切換完成: {ModelName}", SelectedModel.Name);

                ModelSelected?.Invoke(this, SelectedModel);
            }
            else
            {
                // 非 .onnx:僅設定目前模型,不做任何遠端呼叫(避免 hang)。
                await _configService.SetCurrentModelAsync(SelectedModel.Name);
                CurrentModelName = SelectedModel.Name;
                StatusMessage = $"✓ 已設定目前模型 {SelectedModel.Name}(非本地 ONNX,未做遠端載入)";
                _logger?.LogWarning("[ModelSelect] 非 .onnx 模型,僅設定目前模型不做遠端載入: {ModelName}",
                    SelectedModel.Name);

                ModelSelected?.Invoke(this, SelectedModel);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"✗ 發生錯誤: {ex.Message}";
            _logger?.LogError(ex, "切換模型時發生異常: {ModelName}", SelectedModel?.Name);

            System.Windows.MessageBox.Show(
                $"切換模型時發生錯誤\n\n{ex.Message}",
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

