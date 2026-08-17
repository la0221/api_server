using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using AIVision.Application.Contracts.WorkOrder;
using AIVision.Application.Ports.MoldCode;
using AIVision.Application.Services;
using AIVision.Presentation.Wpf.Models;
using AIVision.Presentation.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AIVision.Presentation.Wpf.ViewModels;

public partial class WorkOrderInputViewModel : ObservableObject
{
    /// <summary>下拉中代表「不指定 / 不核對」的哨兵值。</summary>
    public const string NoCheckOption = "（不核對）";

    private readonly IWorkOrderManagementService _workOrderService;
    private readonly ModelConfigService _modelConfigService;
    private readonly IMoldCodePairModelSwitch _pairSwitch;
    private readonly IMessenger _messenger;
    private Window? _window;
    private Guid? _editId;

    /// <summary>對話框標題（建立 / 編輯）。</summary>
    [ObservableProperty]
    private string title = "創建新工單";

    /// <summary>編輯模式時工單代碼不可改。</summary>
    [ObservableProperty]
    private bool isCodeReadOnly;

    /// <summary>載入既有工單進入「編輯」模式（工單代碼不可改，其餘可修改）。</summary>
    public void LoadForEdit(AIVision.Domain.Entities.WorkOrder wo)
    {
        _editId = wo.Id;
        Title = "編輯工單";
        IsCodeReadOnly = true;
        ProductName = wo.ProductName;
        WorkOrderCode = wo.Code;
        BatchNumber = wo.MachineModelName ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(wo.ExpectedMoldCode))
        {
            var parts = wo.ExpectedMoldCode.Split(new[] { '/', '-', '_', ' ' }, 2,
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                if (MohaoOptions.Contains(parts[0])) SelectedMohao = parts[0];
                if (XuehaoOptions.Contains(parts[1])) SelectedXuehao = parts[1];
            }
        }
    }

    public WorkOrderInputViewModel(
        IWorkOrderManagementService workOrderService,
        ModelConfigService modelConfigService,
        IMoldCodePairModelSwitch pairSwitch,
        IMessenger messenger)
    {
        _workOrderService = workOrderService;
        _modelConfigService = modelConfigService;
        _pairSwitch = pairSwitch;
        _messenger = messenger;

        MohaoOptions = new ObservableCollection<string> { NoCheckOption };
        XuehaoOptions = new ObservableCollection<string> { NoCheckOption };
        PopulateCodeOptions();

        // 載入可用模型列表和當前模型
        _ = LoadModelsAsync();
    }

    /// <summary>預期模號下拉選項（來自目前載入的雙 head 模型類別；首項＝不核對）。</summary>
    public ObservableCollection<string> MohaoOptions { get; }

    /// <summary>預期穴號下拉選項（來自目前載入的雙 head 模型類別；首項＝不核對）。</summary>
    public ObservableCollection<string> XuehaoOptions { get; }

    [ObservableProperty]
    private string selectedMohao = NoCheckOption;

    [ObservableProperty]
    private string selectedXuehao = NoCheckOption;

    /// <summary>目前載入的雙 head 版本（顯示用，讓使用者知道選項來自哪個模型）。</summary>
    public string PairModelHint =>
        string.IsNullOrWhiteSpace(_pairSwitch.CurrentVersionName)
            ? "（尚未載入雙 head 模型 → 無法選預期碼；可先到「模號穴號模型管理」載入）"
            : $"預期碼選項來自模型版本：{_pairSwitch.CurrentVersionName}";

    /// <summary>用目前載入模型的類別填入下拉；NG 不列入「預期模號」（不會預期壞件）。</summary>
    private void PopulateCodeOptions()
    {
        foreach (var m in _pairSwitch.CurrentMohaoNames)
            if (!string.Equals(m, "NG", StringComparison.OrdinalIgnoreCase))
                MohaoOptions.Add(m);
        foreach (var x in _pairSwitch.CurrentXuehaoNames)
            XuehaoOptions.Add(x);
    }

    [ObservableProperty]
    private string productName = "默認產品";

    [ObservableProperty]
    private string batchNumber = string.Empty;

    [ObservableProperty]
    private string workOrderCode = string.Empty;

    /// <summary>操作員預期模號(完整碼,例 "M101/07";留空 = 不做模號核對)。</summary>
    [ObservableProperty]
    private string expectedMoldCode = string.Empty;

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

            // 僅列出本地 .onnx 模型:工單只能挑可被本地辨識器載入的模型,
            // 否則 ShellViewModel.SwitchModelForWorkOrderAsync 會因非 .onnx 而靜默略過。
            AvailableModels.Clear();
            foreach (var model in allModels.Where(IsLocalOnnx))
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

    /// <summary>是否為本地 .onnx 模型(僅這類可被本地辨識器載入)。</summary>
    private static bool IsLocalOnnx(ModelConfig model) =>
        !string.IsNullOrWhiteSpace(model.ModelPath) &&
        model.ModelPath.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase);

    // 禁止的特殊字元（會影響檔案系統或 URL 編碼）
    private static readonly Regex InvalidCharsPattern = new(@"[%&*#@!$^()\[\]{}<>|\\/:;""\?`~]", RegexOptions.Compiled);

    // 工單號碼只允許英文字母和數字
    private static readonly Regex WorkOrderCodePattern = new(@"^[A-Za-z0-9\-_]+$", RegexOptions.Compiled);

    // 預期模號格式(完整碼):前綴 + 分隔符(/ - _ 空白) + 模穴,例 "M101/07"
    private static readonly Regex ExpectedMoldCodePattern = new(@"^[A-Za-z0-9]+[\/\-_ ][0-9A-Za-z]+$", RegexOptions.Compiled);

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

        // 預期模號改由下拉組成（模號/穴號，來自目前載入的雙 head 模型類別）。
        // 任一為「不核對」→ 不做模號核對（expectedMoldCode = null）；免手打、格式必正確。
        string? expectedMoldCode =
            (SelectedMohao != NoCheckOption && SelectedXuehao != NoCheckOption)
                ? $"{SelectedMohao}/{SelectedXuehao}"
                : null;

        // AI 模型為選填：模號穴號核對走雙 head，不需在此綁單一 AI 模型（modelName 可為 null）。

        try
        {
            IsBusy = true;
            ErrorMessage = null;

            // 編輯模式：更新既有工單（代碼不變），不建新單。
            if (_editId.HasValue)
            {
                await _workOrderService.UpdateWorkOrderAsync(
                    _editId.Value, ProductName.Trim(), BatchNumber.Trim(), expectedMoldCode, CancellationToken.None);
                _messenger.Send(new WorkOrderChangedMessage());
                if (_window != null)
                {
                    _window.DialogResult = true;
                    _window.Close();
                }
                return;
            }

            // 創建工單，支持自訂工單號（為空時 Service 會自動生成）+ 預期模號（留空 = 不核對）
            // 使用具名引數避免既有的位置參數錯位風險
            var workOrder = await _workOrderService.CreateWorkOrderAsync(
                productName: ProductName.Trim(),
                modelName: SelectedModel?.Name,
                machineModelName: BatchNumber.Trim(),
                customWorkOrderCode: WorkOrderCode?.Trim(),  // 傳入自訂工單號（可為 null 或空），並移除前後空白
                expectedMoldCode: expectedMoldCode,
                cancellationToken: CancellationToken.None);

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
