using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using AIVision.Presentation.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AIVision.Presentation.Wpf.ViewModels;

public partial class ModelSelectorViewModel : ObservableObject
{
    private readonly AinaviApiClient _apiClient;
    private readonly ILogger<ModelSelectorViewModel>? _logger;

    public event EventHandler<bool>? RequestClose;

    public ModelSelectorViewModel(
        IConfiguration configuration,
        ILogger<ModelSelectorViewModel>? logger = null)
    {
        _logger = logger;
        // 建立 API Client（logger 類型不同，傳 null 即可）
        _apiClient = new AinaviApiClient(configuration, null);

        Models = new ObservableCollection<ModelItemViewModel>
        {
            new("WPC_top", "v1.0", 8001),
            new("demo_seg_1", "v1.0", 8001),
            new("demo_seg_2", "v1.0", 8001),
            new("Ecoco_cls_251103", "v1.0", 8001)
        };
        SelectedModel = Models.FirstOrDefault();
        Port = 8001; // 預設埠號
    }

    public ObservableCollection<ModelItemViewModel> Models { get; }

    [ObservableProperty]
    private ModelItemViewModel? selectedModel;

    [ObservableProperty]
    private int port = 8001;

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private string? statusMessage;

    [RelayCommand]
    private async Task Load()
    {
        if (SelectedModel == null)
        {
            System.Windows.MessageBox.Show("請選擇一個模型", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (Port <= 0 || Port > 65535)
        {
            System.Windows.MessageBox.Show("埠號必須在 1-65535 之間", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        try
        {
            IsLoading = true;
            StatusMessage = $"正在載入模型 {SelectedModel.Name}，請稍候... (約需 15-20 秒)";

            // 顯示載入開始訊息
            _logger?.LogInformation("開始載入模型: {ModelName} on Port {Port}", SelectedModel.Name, Port);

            var result = await _apiClient.OpenModelAsync(SelectedModel.Name, Port);

            if (result.Success)
            {
                StatusMessage = $"✓ 模型 {SelectedModel.Name} 已成功載入 (Port={Port})";
                _logger?.LogInformation("模型載入成功: {ModelName} on Port {Port}", SelectedModel.Name, Port);
                
                System.Windows.MessageBox.Show(
                    $"模型 {SelectedModel.Name} 已成功載入\n\nPort: {Port}\n\n載入完成！",
                    "載入成功",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                
                RequestClose?.Invoke(this, true);
            }
            else
            {
                StatusMessage = $"✗ 載入失敗: {result.Message}";
                _logger?.LogWarning("模型載入失敗: {ModelName}, Message: {Message}", SelectedModel.Name, result.Message);
                
                System.Windows.MessageBox.Show(
                    $"模型載入失敗\n\n模型: {SelectedModel.Name}\nPort: {Port}\n\n錯誤訊息:\n{result.Message}",
                    "載入失敗",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"✗ 發生錯誤: {ex.Message}";
            _logger?.LogError(ex, "載入模型時發生異常: {ModelName}", SelectedModel.Name);
            
            System.Windows.MessageBox.Show(
                $"載入模型時發生錯誤\n\n模型: {SelectedModel.Name}\nPort: {Port}\n\n錯誤訊息:\n{ex.Message}",
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
        RequestClose?.Invoke(this, false);
    }
}

/// <summary>
/// 模型項目 ViewModel。
/// </summary>
/// <param name="Name">模型名稱</param>
/// <param name="Version">版本</param>
/// <param name="DefaultPort">預設埠號</param>
public sealed record ModelItemViewModel(string Name, string Version, int DefaultPort = 8009);
