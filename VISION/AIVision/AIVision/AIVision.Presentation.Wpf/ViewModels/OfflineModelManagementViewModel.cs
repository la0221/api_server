using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
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
/// 離線模型管理 ViewModel（自建本地 ONNX 模型）。
/// <para>
/// 純本地 ONNX：模型來源為 DI 注入的 <see cref="ModelConfigService"/>（會自動掃描
/// <c>D:\AIVisionModels</c> 內的 *.onnx，類別與精度資訊由 <c>OnnxModelDiscoveryService</c> 推得）。
/// 「設為目前模型」會持久化目前模型並即時熱抽換執行中的本地辨識器
/// （<see cref="IMoldCodeModelSwitch"/>）。完全不涉及 AINAVI / EdgeHub。
/// </para>
/// </summary>
public partial class OfflineModelManagementViewModel : ObservableObject
{
    /// <summary>本地自建 ONNX 模型預設存放資料夾（OnnxModelDiscoveryService 掃描處）。</summary>
    private const string DefaultModelFolder = @"D:\AIVisionModels";

    private readonly ModelConfigService _configService;
    private readonly IMoldCodeModelSwitch? _modelSwitch;
    private readonly string _moldCodePrefix;
    private readonly MoldCodeOnnxOptions _baselineOptions;
    private readonly string _scanFolder;
    private readonly ILogger<OfflineModelManagementViewModel>? _logger;

    /// <summary>
    /// 目前模型變更事件（供主視窗或其他訂閱者使用）。
    /// </summary>
    public event EventHandler<ModelConfig>? ModelLoaded;

    /// <summary>
    /// 建構子（DI 注入本地 ONNX 相關服務）。
    /// </summary>
    public OfflineModelManagementViewModel(
        ModelConfigService configService,
        IMoldCodeModelSwitch? modelSwitch = null,
        IOptions<MoldCodeOnnxOptions>? onnxOptions = null,
        ILogger<OfflineModelManagementViewModel>? logger = null)
    {
        _configService = configService;
        _modelSwitch = modelSwitch;
        _baselineOptions = onnxOptions?.Value ?? new MoldCodeOnnxOptions();
        _moldCodePrefix = string.IsNullOrWhiteSpace(_baselineOptions.CodePrefix) ? "M101" : _baselineOptions.CodePrefix;
        _logger = logger;

        // 掃描資料夾：優先用 baseline 模型路徑所在目錄，否則退回預設 D:\AIVisionModels。
        var modelPath = onnxOptions?.Value.ModelPath;
        var baselineDir = string.IsNullOrWhiteSpace(modelPath) ? null : Path.GetDirectoryName(modelPath);
        _scanFolder = string.IsNullOrWhiteSpace(baselineDir) ? DefaultModelFolder : baselineDir!;

        Models = new ObservableCollection<ModelConfig>();
        _ = LoadModelsAsync();
    }

    /// <summary>
    /// 本地 ONNX 模型列表。
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
    /// 掃描資料夾路徑（僅顯示用）。
    /// </summary>
    public string ScanFolder => _scanFolder;

    /// <summary>
    /// 載入本地 ONNX 模型列表（自動掃描 D:\AIVisionModels）。
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

            _logger?.LogInformation("[Offline] 已載入 {Count} 個本地 ONNX 模型", Models.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[Offline] 載入本地模型列表失敗");
            StatusMessage = $"載入失敗: {ex.Message}";
        }
    }

    /// <summary>
    /// 設為目前模型：持久化目前模型 + 即時熱抽換執行中的本地辨識器。
    /// </summary>
    [RelayCommand]
    private async Task SetCurrentModelAsync()
    {
        if (SelectedModel == null)
        {
            System.Windows.MessageBox.Show("請選擇一個模型", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var isOnnx = !string.IsNullOrWhiteSpace(SelectedModel.ModelPath) &&
                     SelectedModel.ModelPath.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase);
        if (!isOnnx)
        {
            System.Windows.MessageBox.Show(
                $"模型 {SelectedModel.Name} 不是本地 .onnx 模型，無法於離線頁設為目前模型。",
                "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            IsLoading = true;

            var modelKey = SelectedModel.OriginalName ?? SelectedModel.Name;
            await _configService.SetCurrentModelAsync(modelKey);
            CurrentModelName = modelKey;

            // 即時切換執行中的本地辨識器（無需重啟或工單觸發）；傳該模型自己的類別。
            _modelSwitch?.LoadModel(SelectedModel.ModelPath, SelectedModel.GetEffectiveResultTypes(), _moldCodePrefix);

            StatusMessage = $"✓ 已設為目前模型: {SelectedModel.Name}";
            _logger?.LogInformation("[Offline] 已設為目前本地 ONNX 模型: {ModelName} -> {Path}",
                SelectedModel.Name, SelectedModel.ModelPath);

            ModelLoaded?.Invoke(this, SelectedModel);

            System.Windows.MessageBox.Show(
                $"已設為目前模型 {SelectedModel.Name}\n\n路徑: {SelectedModel.ModelPath}",
                "已設定",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusMessage = $"✗ 發生錯誤: {ex.Message}";
            _logger?.LogError(ex, "[Offline] 設為目前模型時發生異常: {ModelName}", SelectedModel?.Name);
            System.Windows.MessageBox.Show(
                $"設為目前模型時發生錯誤\n\n{ex.Message}",
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
    /// 編輯模型:開啟離線編輯對話框,編輯該本地 .onnx 模型的旁置設定
    /// (顯示名稱/說明/前綴 + 進階前處理 Imgsz/Blackhat/Locator/OuterFactor)。
    /// 儲存後重新掃描以更新顯示名稱;若編輯的是目前模型則即時重載以套用新設定。
    /// </summary>
    [RelayCommand]
    private async Task EditModelAsync()
    {
        if (SelectedModel == null)
        {
            System.Windows.MessageBox.Show("請選擇一個模型", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var isOnnx = !string.IsNullOrWhiteSpace(SelectedModel.ModelPath) &&
                     SelectedModel.ModelPath.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase);
        if (!isOnnx)
        {
            System.Windows.MessageBox.Show(
                $"模型 {SelectedModel.Name} 不是本地 .onnx 模型,無法編輯。",
                "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            var editVm = new OfflineModelEditViewModel(SelectedModel, _baselineOptions);
            var editView = new Views.OfflineModelEditView(editVm)
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };

            var result = editView.ShowDialog();
            if (result != true)
                return;

            // 記住目前選取模型(以路徑判斷是否為目前模型,因 DisplayName 可能已變)。
            var editedPath = SelectedModel.ModelPath;
            var wasCurrent = string.Equals(
                SelectedModel.OriginalName ?? SelectedModel.Name,
                CurrentModelName,
                StringComparison.OrdinalIgnoreCase);

            // 重新掃描以反映新的 DisplayName/Description。
            await _configService.RescanModelsAsync();
            await LoadModelsAsync();

            // 還原選取(以路徑比對,DisplayName 可能已變)。
            SelectedModel = Models.FirstOrDefault(m =>
                string.Equals(m.ModelPath, editedPath, StringComparison.OrdinalIgnoreCase)) ?? SelectedModel;

            // 若編輯的是目前模型,即時重載辨識器以套用新前處理/前綴(旁置設定會於 LoadModel 內合併)。
            if (wasCurrent && SelectedModel != null && _modelSwitch != null)
            {
                _modelSwitch.LoadModel(SelectedModel.ModelPath, SelectedModel.GetEffectiveResultTypes(), _moldCodePrefix);
                _logger?.LogInformation("[Offline] 已即時重載目前模型以套用新設定: {Path}", SelectedModel.ModelPath);
            }

            StatusMessage = $"✓ 已更新模型設定: {SelectedModel?.Name}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"✗ 編輯模型失敗: {ex.Message}";
            _logger?.LogError(ex, "[Offline] 編輯模型失敗: {ModelName}", SelectedModel?.Name);
            System.Windows.MessageBox.Show(
                $"編輯模型時發生錯誤\n\n{ex.Message}",
                "錯誤",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 重新掃描模型資料夾。
    /// </summary>
    [RelayCommand]
    private async Task RescanAsync()
    {
        try
        {
            IsLoading = true;
            StatusMessage = "正在重新掃描模型資料夾...";

            await _configService.RescanModelsAsync();
            await LoadModelsAsync();

            StatusMessage = $"✓ 已重新掃描，共 {Models.Count} 個本地 ONNX 模型";
            _logger?.LogInformation("[Offline] 重新掃描完成，共 {Count} 個模型", Models.Count);
        }
        catch (Exception ex)
        {
            StatusMessage = $"✗ 重新掃描失敗: {ex.Message}";
            _logger?.LogError(ex, "[Offline] 重新掃描失敗");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 開啟模型資料夾（檔案總管）。
    /// </summary>
    [RelayCommand]
    private void OpenModelFolder()
    {
        try
        {
            if (!Directory.Exists(_scanFolder))
            {
                System.Windows.MessageBox.Show(
                    $"模型資料夾不存在: {_scanFolder}",
                    "提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = _scanFolder,
                UseShellExecute = true
            });

            _logger?.LogInformation("[Offline] 已開啟模型資料夾: {Folder}", _scanFolder);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[Offline] 開啟模型資料夾失敗");
            System.Windows.MessageBox.Show(
                $"開啟模型資料夾失敗\n\n{ex.Message}",
                "錯誤",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
