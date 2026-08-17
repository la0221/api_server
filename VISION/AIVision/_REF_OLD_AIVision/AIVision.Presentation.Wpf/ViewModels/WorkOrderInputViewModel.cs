using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using AIVision.Application.Contracts.WorkOrder;
using AIVision.Application.Services;
using AIVision.Presentation.Wpf.Models;
using AIVision.Presentation.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AIVision.Presentation.Wpf.ViewModels;

public partial class WorkOrderInputViewModel : ObservableObject
{
    private readonly IWorkOrderManagementService _workOrderService;
    private readonly ModelConfigService _modelConfigService;
    private readonly IMessenger _messenger;
    private Window? _window;

    public WorkOrderInputViewModel(
        IWorkOrderManagementService workOrderService,
        ModelConfigService modelConfigService,
        IMessenger messenger)
    {
        _workOrderService = workOrderService;
        _modelConfigService = modelConfigService;
        _messenger = messenger;

        // 載入可用模型列表和當前模型
        _ = LoadModelsAsync();
    }

    [ObservableProperty]
    private string productName = "默認產品";

    [ObservableProperty]
    private string batchNumber = string.Empty;

    [ObservableProperty]
    private string workOrderCode = string.Empty;

    [ObservableProperty]
    private ModelConfig? selectedModel;

    public ObservableCollection<ModelConfig> AvailableModels { get; } = new();

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string? errorMessage;

    public void SetWindow(Window window)
    {
        _window = window;
    }

    private async System.Threading.Tasks.Task LoadModelsAsync()
    {
        try
        {
            // 使用 GetAllModelsAsync 載入所有模型（包含自動掃描 + 手動配置 + 向後兼容）
            var allModels = await _modelConfigService.GetAllModelsAsync();
            var config = await _modelConfigService.LoadAsync();

            // 載入所有可用模型
            AvailableModels.Clear();
            foreach (var model in allModels)
            {
                AvailableModels.Add(model);
            }

            // 設定當前模型為預選
            if (!string.IsNullOrEmpty(config.CurrentModelName))
            {
                SelectedModel = AvailableModels.FirstOrDefault(m =>
                    m.Name == config.CurrentModelName ||
                    m.OriginalName == config.CurrentModelName);
            }
            else if (AvailableModels.Count > 0)
            {
                SelectedModel = AvailableModels[0];
            }
        }
        catch
        {
            // 載入失敗時，AvailableModels 保持為空
        }
    }

    // 禁止的特殊字元（會影響檔案系統或 URL 編碼）
    private static readonly Regex InvalidCharsPattern = new(@"[%&*#@!$^()\[\]{}<>|\\/:;""\?`~]", RegexOptions.Compiled);

    // 工單號碼只允許英文字母和數字
    private static readonly Regex WorkOrderCodePattern = new(@"^[A-Za-z0-9\-_]+$", RegexOptions.Compiled);

    /// <summary>
    /// 驗證輸入字串是否包含禁止的特殊字元
    /// </summary>
    /// <param name="input">輸入字串</param>
    /// <param name="fieldName">欄位名稱（用於錯誤訊息）</param>
    /// <returns>驗證結果，null 表示通過</returns>
    private string? ValidateInput(string? input, string fieldName)
    {
        if (string.IsNullOrEmpty(input))
            return null;

        if (InvalidCharsPattern.IsMatch(input))
        {
            return $"{fieldName}不可包含特殊字元（% & * # @ ! $ ^ ( ) [ ] {{ }} < > | \\ / : ; \" ? ` ~）";
        }

        // 檢查是否只有空白
        if (string.IsNullOrWhiteSpace(input))
        {
            return $"{fieldName}不可只包含空白";
        }

        return null;
    }

    /// <summary>
    /// 驗證工單號碼 - 只允許英文字母、數字、底線和連字號
    /// </summary>
    /// <param name="input">輸入字串</param>
    /// <returns>驗證結果，null 表示通過</returns>
    private string? ValidateWorkOrderCode(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return null;

        // 檢查是否只有空白
        if (string.IsNullOrWhiteSpace(input))
        {
            return "工單編號不可只包含空白";
        }

        // 檢查是否符合英文字母、數字、底線、連字號的格式
        if (!WorkOrderCodePattern.IsMatch(input.Trim()))
        {
            return "工單編號只能包含英文字母、數字、底線(_)和連字號(-)，不可包含中文或其他特殊字元";
        }

        return null;
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task ConfirmAsync()
    {
        // 驗證產品名稱
        if (string.IsNullOrWhiteSpace(ProductName))
        {
            ErrorMessage = "產品名稱不可為空";
            return;
        }

        var productNameError = ValidateInput(ProductName, "產品名稱");
        if (productNameError != null)
        {
            ErrorMessage = productNameError;
            return;
        }

        // 驗證工單編號（如果有輸入）- 只允許英文字母、數字、底線和連字號
        if (!string.IsNullOrEmpty(WorkOrderCode))
        {
            var workOrderCodeError = ValidateWorkOrderCode(WorkOrderCode);
            if (workOrderCodeError != null)
            {
                ErrorMessage = workOrderCodeError;
                return;
            }
        }

        // 驗證批次號（如果有輸入）
        if (!string.IsNullOrEmpty(BatchNumber))
        {
            var batchNumberError = ValidateInput(BatchNumber, "批次號");
            if (batchNumberError != null)
            {
                ErrorMessage = batchNumberError;
                return;
            }
        }

        if (SelectedModel == null)
        {
            ErrorMessage = "請選擇 AI 模型";
            return;
        }

        try
        {
            IsBusy = true;
            ErrorMessage = null;

            // 創建工單，支持自訂工單號（為空時 Service 會自動生成）
            var workOrder = await _workOrderService.CreateWorkOrderAsync(
                ProductName.Trim(),
                SelectedModel.Name,
                BatchNumber.Trim(),
                WorkOrderCode?.Trim(),  // 傳入自訂工單號（可為 null 或空），並移除前後空白
                CancellationToken.None);

            // 發送工單變更訊息，通知 ShellViewModel 更新顯示
            _messenger.Send(new WorkOrderChangedMessage());

            // 關閉對話框並返回 true
            if (_window != null)
            {
                _window.DialogResult = true;
                _window.Close();
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"創建失敗：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (_window != null)
        {
            _window.DialogResult = false;
            _window.Close();
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task EndCurrentWorkOrderAsync()
    {
        try
        {
            IsBusy = true;
            ErrorMessage = null;

            // 結束當前工單
            await _workOrderService.EndCurrentWorkOrderAsync(CancellationToken.None);

            // 發送工單變更訊息，通知 ShellViewModel 更新顯示
            _messenger.Send(new WorkOrderChangedMessage());

            // 關閉對話框
            if (_window != null)
            {
                _window.DialogResult = true;
                _window.Close();
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"結束工單失敗：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
