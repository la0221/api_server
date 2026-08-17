using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using AIVision.Application.Configuration;
using AIVision.Infrastructure.AiService;
using AIVision.Presentation.Wpf.Models;
using AIVision.Presentation.Wpf.Services;
using AIVision.Presentation.Wpf.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using InferenceType = AIVision.Presentation.Wpf.Models.InferenceType;

namespace AIVision.Presentation.Wpf.ViewModels;

/// <summary>
/// 模型管理 ViewModel。
/// </summary>
public partial class ModelManagementViewModel : ObservableObject
{
    private readonly ModelConfigService _configService;
    private readonly AinaviApiClient _apiClient;
    private readonly SwitchableAiInferencePort? _switchablePort;
    private readonly ILogger<ModelManagementViewModel>? _logger;
    private readonly ModelScanOptions? _scanOptions;
    private readonly string _appSettingsPath;

    public event EventHandler<ModelConfig>? ModelLoaded;

    public ModelManagementViewModel(
        ModelConfigService configService,
        AinaviApiClient apiClient,
        IOptions<ModelScanOptions> scanOptions,
        SwitchableAiInferencePort? switchablePort = null,
        ILogger<ModelManagementViewModel>? logger = null)
    {
        _configService = configService;
        _apiClient = apiClient;
        _switchablePort = switchablePort;
        _logger = logger;
        _scanOptions = scanOptions?.Value;
        _appSettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

        // 初始化掃描資料夾路徑
        _scanFolder = _scanOptions?.ScanFolder ?? "(未設定)";

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

    [ObservableProperty]
    private string _scanFolder = "(未設定)";

    /// <summary>
    /// 載入模型列表（整合自動掃描 + 手動配置）。
    /// </summary>
    [RelayCommand]
    private async Task LoadModelsAsync()
    {
        try
        {
            // 使用 GetAllModelsAsync 取得所有模型（自動掃描 + 手動配置）
            var allModels = await _configService.GetAllModelsAsync();
            var config = await _configService.LoadAsync();

            Models.Clear();
            foreach (var model in allModels)
            {
                Models.Add(model);
            }

            CurrentModelName = config.CurrentModelName;

            // 用 OriginalName 或 Name 搜尋目前模型
            SelectedModel = Models.FirstOrDefault(m =>
                m.OriginalName == CurrentModelName || m.Name == CurrentModelName);

            _logger?.LogInformation("已載入 {Count} 個模型配置（自動掃描 + 手動）", Models.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "載入模型列表失敗");
            StatusMessage = $"載入失敗: {ex.Message}";
        }
    }

    /// <summary>
    /// 關閉所有 AI 服務。
    /// </summary>
    [RelayCommand]
    private async Task CloseServicesAsync()
    {
        if (SelectedModel == null)
        {
            System.Windows.MessageBox.Show("請先選擇一個模型以取得 EdgeHub 位址", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            IsLoading = true;
            StatusMessage = "正在關閉所有 AI 服務...";

            var edgeHubUrl = SelectedModel.GetEdgeHubUrl();
            _logger?.LogInformation("正在關閉 EdgeHub 服務: {Url}", edgeHubUrl);

            var result = await _apiClient.CloseAllServicesAsync(edgeHubUrl);

            if (result.Success)
            {
                StatusMessage = $"✓ 服務已關閉: {result.Message}";
                _logger?.LogInformation("EdgeHub 服務已關閉: {Message}", result.Message);

                System.Windows.MessageBox.Show(
                    $"服務已關閉\n\n{result.Message}",
                    "關閉成功",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                StatusMessage = $"✗ 關閉失敗: {result.Message}";
                _logger?.LogWarning("關閉 EdgeHub 服務失敗: {Message}", result.Message);

                System.Windows.MessageBox.Show(
                    $"關閉服務失敗\n\n{result.Message}",
                    "關閉失敗",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"✗ 發生錯誤: {ex.Message}";
            _logger?.LogError(ex, "關閉服務時發生異常");

            System.Windows.MessageBox.Show(
                $"關閉服務時發生錯誤\n\n{ex.Message}",
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

            var isWorkflow = SelectedModel.InferenceType == InferenceType.Workflow;
            var typeLabel = isWorkflow ? "Workflow" : "模型";

            // 先關閉現有服務，再載入新服務
            StatusMessage = $"正在關閉現有服務...";
            _logger?.LogInformation("載入新{TypeLabel}前，先關閉現有服務", typeLabel);

            var closeResult = await _apiClient.CloseAllServicesAsync(SelectedModel.GetEdgeHubUrl());
            if (closeResult.Success)
            {
                _logger?.LogInformation("現有服務已關閉: {Message}", closeResult.Message);
            }
            else
            {
                _logger?.LogWarning("關閉現有服務失敗（繼續載入）: {Message}", closeResult.Message);
            }

            // 等待一下讓服務完全關閉
            await Task.Delay(500);

            StatusMessage = $"正在載入{typeLabel} {SelectedModel.Name}，請稍候... (約需 15-20 秒)";
            _logger?.LogInformation("開始載入{TypeLabel}: {ModelName}, InferenceType={InferenceType}",
                typeLabel, SelectedModel.Name, SelectedModel.InferenceType);

            ModelLoadResultDto result;

            if (isWorkflow)
            {
                // Workflow 模式：呼叫 /service/workflow API
                if (string.IsNullOrWhiteSpace(SelectedModel.WorkflowSettingPath))
                {
                    StatusMessage = $"✗ Workflow 設定檔路徑未設定";
                    System.Windows.MessageBox.Show(
                        $"請先編輯模型並設定 Workflow 設定檔路徑",
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
                // 更新目前模型
                await _configService.SetCurrentModelAsync(SelectedModel.Name);
                CurrentModelName = SelectedModel.Name;

                // 同步推論端點到 SwitchableAiInferencePort
                if (_switchablePort != null)
                {
                    if (isWorkflow)
                    {
                        // 設定 Workflow 模式的端點
                        _switchablePort.WorkflowPort.SetEndpoint(
                            SelectedModel.EdgeHubHost,
                            SelectedModel.WorkflowPort);
                        _switchablePort.SwitchTo(Application.Configuration.InferenceType.Workflow);
                        _logger?.LogInformation("已同步 Workflow 端點: {Host}:{Port}",
                            SelectedModel.EdgeHubHost, SelectedModel.WorkflowPort);
                    }
                    else
                    {
                        // 設定單一模型模式的端點
                        _switchablePort.SwitchTo(Application.Configuration.InferenceType.SingleModel);
                        _logger?.LogInformation("已切換到單一模型模式: {Host}:{Port}",
                            SelectedModel.EdgeHubHost, SelectedModel.InferencePort);
                    }
                }

                StatusMessage = $"✓ {typeLabel} {SelectedModel.Name} 已成功載入";
                _logger?.LogInformation("{TypeLabel}載入成功: {ModelName}", typeLabel, SelectedModel.Name);

                System.Windows.MessageBox.Show(
                    $"{typeLabel} {SelectedModel.Name} 已成功載入\n\n" +
                    $"EdgeHub: {SelectedModel.GetEdgeHubUrl()}\n" +
                    $"Inference: {SelectedModel.GetInferenceUrl()}\n" +
                    $"類型: {(isWorkflow ? "Workflow" : "單一模型")}",
                    "載入成功",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                // 觸發事件通知主視窗
                ModelLoaded?.Invoke(this, SelectedModel);
            }
            else
            {
                StatusMessage = $"✗ 載入失敗: {result.Message}";
                _logger?.LogWarning("{TypeLabel}載入失敗: {ModelName}, Message: {Message}",
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

    /// <summary>
    /// 新增模型。
    /// </summary>
    [RelayCommand]
    private async Task AddModelAsync()
    {
        try
        {
            // 傳入 null logger，因為類型不匹配
            var editViewModel = new ModelEditViewModel(_configService, null, null);
            var editView = new ModelEditView(editViewModel);
            editView.Owner = System.Windows.Application.Current?.MainWindow;
            
            var result = editView.ShowDialog();
            if (result == true)
            {
                await LoadModelsAsync();
                StatusMessage = "已新增模型";
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "新增模型失敗");
            System.Windows.MessageBox.Show(
                $"新增模型失敗\n\n{ex.Message}",
                "錯誤",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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
            // 傳入 null logger，因為類型不匹配
            var editViewModel = new ModelEditViewModel(_configService, SelectedModel, null);
            var editView = new ModelEditView(editViewModel);
            editView.Owner = System.Windows.Application.Current?.MainWindow;
            
            var result = editView.ShowDialog();
            if (result == true)
            {
                await LoadModelsAsync();
                StatusMessage = "已更新模型";
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "編輯模型失敗");
            System.Windows.MessageBox.Show(
                $"編輯模型失敗\n\n{ex.Message}",
                "錯誤",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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

        var result = System.Windows.MessageBox.Show(
            $"確定要刪除模型 '{SelectedModel.Name}' 嗎？",
            "確認刪除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
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
            _logger?.LogError(ex, "刪除模型失敗: {ModelName}", SelectedModel.Name);
            System.Windows.MessageBox.Show(
                $"刪除模型失敗\n\n{ex.Message}",
                "錯誤",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 選擇模型掃描資料夾。
    /// </summary>
    [RelayCommand]
    private async Task SelectScanFolderAsync()
    {
        try
        {
            // 使用 WPF 原生的 OpenFolderDialog (需要 .NET 8+)
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "選擇模型資料夾",
                InitialDirectory = Directory.Exists(ScanFolder) ? ScanFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var selectedFolder = dialog.FolderName;
            _logger?.LogInformation("使用者選擇模型資料夾: {Folder}", selectedFolder);

            // 確認資料夾存在
            if (!Directory.Exists(selectedFolder))
            {
                System.Windows.MessageBox.Show(
                    $"所選資料夾不存在: {selectedFolder}",
                    "錯誤",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            // 更新 appsettings.json
            IsLoading = true;
            StatusMessage = "正在更新設定...";

            await UpdateAppSettingsScanFolderAsync(selectedFolder);

            // 更新 UI
            ScanFolder = selectedFolder;

            // 重新掃描模型（使用新的資料夾路徑）
            StatusMessage = "正在掃描模型資料夾...";
            await _configService.RescanModelsAsync(selectedFolder);
            await LoadModelsAsync();

            StatusMessage = $"✓ 已更新模型資料夾並重新掃描，共 {Models.Count} 個模型";
            _logger?.LogInformation("模型資料夾已更新: {Folder}，共掃描到 {Count} 個模型", selectedFolder, Models.Count);

            System.Windows.MessageBox.Show(
                $"模型資料夾已更新\n\n路徑: {selectedFolder}\n掃描到 {Models.Count} 個模型\n\n注意：此設定需要重新啟動程式才會完全生效。",
                "設定已更新",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "選擇模型資料夾失敗");
            StatusMessage = $"✗ 選擇資料夾失敗: {ex.Message}";
            System.Windows.MessageBox.Show(
                $"選擇模型資料夾失敗\n\n{ex.Message}",
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
    /// 更新 appsettings.json 中的 Models.ScanFolder 設定。
    /// </summary>
    private async Task UpdateAppSettingsScanFolderAsync(string newFolder)
    {
        try
        {
            // 讀取現有的 appsettings.json
            string json;
            if (File.Exists(_appSettingsPath))
            {
                json = await File.ReadAllTextAsync(_appSettingsPath);
            }
            else
            {
                // 如果檔案不存在，建立基本結構
                json = "{}";
            }

            // 解析 JSON
            var jsonNode = JsonNode.Parse(json) ?? new JsonObject();

            // 確保 Models 區段存在
            if (jsonNode["Models"] == null)
            {
                jsonNode["Models"] = new JsonObject();
            }

            // 更新 ScanFolder
            jsonNode["Models"]!["ScanFolder"] = newFolder;

            // 確保 AutoScan 為 true
            if (jsonNode["Models"]!["AutoScan"] == null)
            {
                jsonNode["Models"]!["AutoScan"] = true;
            }

            // 寫回檔案（保持格式化）
            var options = new JsonSerializerOptions { WriteIndented = true };
            var updatedJson = jsonNode.ToJsonString(options);
            await File.WriteAllTextAsync(_appSettingsPath, updatedJson);

            _logger?.LogInformation("已更新 appsettings.json - Models.ScanFolder: {Folder}", newFolder);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "更新 appsettings.json 失敗");
            throw new InvalidOperationException($"無法更新設定檔: {ex.Message}", ex);
        }
    }
}

