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
    private readonly ILogger<ModelSelectorViewModel>? _logger;

    public event EventHandler<bool>? RequestClose;

    public ModelSelectorViewModel(
        IConfiguration configuration,
        ILogger<ModelSelectorViewModel>? logger = null)
    {
        _logger = logger;
        // 不再建立 AinaviApiClient(遠端載入已停用,改走本地 ONNX);configuration 保留以維持 DI 簽章相容。
        _ = configuration;

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
    private void Load()
    {
        // 此為舊版遠端模型選擇器(硬編 AINAVI 模型清單 + EdgeHub OpenModel)。
        // 模號站已全面改走本地 ONNX,模型切換由「模型管理 / 模型選擇(ModelSelectViewModel)」走本地處理,
        // 故此處不再呼叫 AinaviApiClient(避免無伺服器時 hang 15-20 秒),改為 no-op 並記錄。
        if (SelectedModel == null)
        {
            System.Windows.MessageBox.Show("請選擇一個模型", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _logger?.LogInformation(
            "[ModelSelector] 此舊版遠端選擇器已停用遠端載入(改走本地 ONNX);選取={ModelName}, Port={Port}(未呼叫 EdgeHub)",
            SelectedModel.Name, Port);
        StatusMessage = "本站已改為本地 ONNX 推論,請改用「模型管理」選擇本地模型。";
        RequestClose?.Invoke(this, false);
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
