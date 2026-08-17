using System.Collections.ObjectModel;
using System.IO;
using System.IO.Ports;
using AIVision.Application.Configuration;
using AIVision.Application.Services;
using AIVision.Presentation.Wpf.Models;
using AIVision.Presentation.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AIVision.Presentation.Wpf.ViewModels;

/// <summary>
/// 專案編輯視窗 ViewModel
/// </summary>
public partial class ProjectEditViewModel : ObservableObject
{
    private readonly IProjectConfigService _projectConfigService;
    private readonly ModelConfigService _modelConfigService;
    private readonly ILogger<ProjectEditViewModel> _logger;

    private string _projectsFolder = string.Empty;
    private ProjectConfig? _existingProject;
    private bool _isEditMode;

    // 基本資訊
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    // PLC 設定
    [ObservableProperty]
    private string _plcHost = "192.168.0.20";

    [ObservableProperty]
    private int _plcPort = 502;

    // 相機設定
    [ObservableProperty]
    private string _cameraUserSet = "UserSet0";

    public ObservableCollection<string> AvailableUserSets { get; } = new()
    {
        "UserSet0",
        "UserSet1",
        "UserSet2"
    };

    // 光源設定
    [ObservableProperty]
    private bool _isLightSerial = true;

    [ObservableProperty]
    private bool _isLightTcp;

    [ObservableProperty]
    private string _lightComPort = "COM3";

    [ObservableProperty]
    private string _lightTcpHost = "192.168.0.100";

    [ObservableProperty]
    private int _lightTcpPort = 5000;

    [ObservableProperty]
    private int _lightCh1 = 255;

    [ObservableProperty]
    private int _lightCh2 = 255;

    public ObservableCollection<string> AvailableComPorts { get; } = new();

    // ===== AI 模型設定（新架構：從模型管理選擇） =====

    /// <summary>
    /// 可選擇的模型清單
    /// </summary>
    public ObservableCollection<ModelConfig> AvailableModels { get; } = new();

    /// <summary>
    /// 選中的模型
    /// </summary>
    [ObservableProperty]
    private ModelConfig? _selectedModel;

    /// <summary>
    /// 是否正在載入模型清單
    /// </summary>
    [ObservableProperty]
    private bool _isLoadingModels;

    // ===== 舊版 AI 模型設定（保留向後相容，當沒有選擇模型時可手動輸入） =====
    [ObservableProperty]
    private bool _isWorkflow = true;

    [ObservableProperty]
    private bool _isSingleModel;

    [ObservableProperty]
    private string _edgeHubHost = "192.168.1.95";

    [ObservableProperty]
    private int _edgeHubPort = 5001;

    [ObservableProperty]
    private int _workflowPort = 8100;

    [ObservableProperty]
    private string _workflowSettingPath = string.Empty;

    [ObservableProperty]
    private int _inferencePort = 8001;

    [ObservableProperty]
    private string _modelPath = string.Empty;

    /// <summary>
    /// 是否使用模型管理選擇（新架構）
    /// </summary>
    [ObservableProperty]
    private bool _useModelSelection = true;

    /// <summary>
    /// 是否使用手動輸入（舊架構 Fallback）
    /// </summary>
    public bool UseManualInput => !UseModelSelection;

    public string WindowTitle => _isEditMode ? $"編輯專案 - {_existingProject?.Name}" : "新增專案";

    public bool CanSave => !string.IsNullOrWhiteSpace(Name);

    public event Action<bool>? RequestClose;

    public ProjectEditViewModel(
        IProjectConfigService projectConfigService,
        ModelConfigService modelConfigService,
        ILogger<ProjectEditViewModel> logger)
    {
        _projectConfigService = projectConfigService;
        _modelConfigService = modelConfigService;
        _logger = logger;

        // 載入可用的 COM Ports
        RefreshComPorts();
    }

    partial void OnUseModelSelectionChanged(bool value)
    {
        OnPropertyChanged(nameof(UseManualInput));
    }

    partial void OnSelectedModelChanged(ModelConfig? value)
    {
        if (value != null)
        {
            // 同步到舊版欄位（預覽用）
            IsWorkflow = value.InferenceType == Models.InferenceType.Workflow;
            IsSingleModel = value.InferenceType == Models.InferenceType.SingleModel;
            EdgeHubHost = value.EdgeHubHost;
            EdgeHubPort = value.EdgeHubPort;
            WorkflowPort = value.WorkflowPort;
            WorkflowSettingPath = value.WorkflowSettingPath ?? string.Empty;
            InferencePort = value.InferencePort;
            ModelPath = value.ModelPath ?? string.Empty;

            _logger.LogInformation("選擇模型: {Name}, 類型: {Type}", value.Name, value.InferenceType);
        }
    }

    /// <summary>
    /// 初始化視窗（新增或編輯模式）
    /// </summary>
    /// <param name="projectsFolder">專案資料夾路徑</param>
    /// <param name="existingProject">要編輯的專案（null 表示新增模式）</param>
    public async void Initialize(string projectsFolder, ProjectConfig? existingProject)
    {
        _projectsFolder = projectsFolder;
        _existingProject = existingProject;
        _isEditMode = existingProject != null;

        OnPropertyChanged(nameof(WindowTitle));

        // 載入模型清單
        await LoadModelsAsync();

        if (existingProject != null)
        {
            LoadFromProject(existingProject);
        }

        // 重新載入 COM Ports
        RefreshComPorts();
    }

    /// <summary>
    /// 載入可選擇的模型清單
    /// </summary>
    private async Task LoadModelsAsync()
    {
        try
        {
            IsLoadingModels = true;
            AvailableModels.Clear();

            var models = await _modelConfigService.GetAllModelsAsync();
            _logger.LogInformation("載入可用模型清單，共 {Count} 個模型", models.Count);

            foreach (var model in models)
            {
                AvailableModels.Add(model);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "載入模型清單失敗");
        }
        finally
        {
            IsLoadingModels = false;
        }
    }

    private void RefreshComPorts()
    {
        AvailableComPorts.Clear();
        foreach (var port in SerialPort.GetPortNames().OrderBy(p => p))
        {
            AvailableComPorts.Add(port);
        }

        // 如果沒有選擇的 COM Port 在列表中，選擇第一個
        if (!AvailableComPorts.Contains(LightComPort) && AvailableComPorts.Count > 0)
        {
            LightComPort = AvailableComPorts[0];
        }
    }

    private void LoadFromProject(ProjectConfig project)
    {
        // 基本資訊
        Name = project.Name;
        Description = project.Description ?? string.Empty;

        // PLC 設定
        PlcHost = project.Plc?.Host ?? "192.168.0.20";
        PlcPort = project.Plc?.Port ?? 502;

        // 相機設定
        CameraUserSet = project.Camera?.UserSet ?? "UserSet0";

        // 光源設定（現在只支援 Serial COM Port）
        IsLightSerial = true;  // 強制使用 Serial 模式
        IsLightTcp = false;
        if (project.Light != null)
        {
            // 優先使用專案設定的 PortName，否則使用預設值
            LightComPort = project.Light.PortName ?? "COM3";

            if (project.Light.ChannelBrightness != null)
            {
                LightCh1 = project.Light.ChannelBrightness.TryGetValue("1", out var ch1) ? ch1 : 255;
                LightCh2 = project.Light.ChannelBrightness.TryGetValue("2", out var ch2) ? ch2 : 255;
            }
        }

        // ===== AI 模型設定（新架構優先） =====
        if (!string.IsNullOrEmpty(project.ModelName))
        {
            // 新架構：使用 ModelName 選擇模型
            UseModelSelection = true;
            SelectedModel = AvailableModels.FirstOrDefault(m => m.Name == project.ModelName);

            if (SelectedModel != null)
            {
                _logger.LogInformation("從專案載入模型: {ModelName}", project.ModelName);
            }
            else
            {
                _logger.LogWarning("專案指定的模型 {ModelName} 不存在於模型管理中", project.ModelName);
            }
        }
        else if (project.AiModel != null)
        {
            // 舊架構 Fallback：使用手動輸入
            UseModelSelection = false;
            IsWorkflow = project.AiModel.IsWorkflow;
            IsSingleModel = project.AiModel.IsSingleModel;
            EdgeHubHost = project.AiModel.EdgeHubHost;
            EdgeHubPort = project.AiModel.EdgeHubPort;
            WorkflowPort = project.AiModel.WorkflowPort;
            WorkflowSettingPath = project.AiModel.WorkflowSettingPath ?? string.Empty;
            InferencePort = project.AiModel.InferencePort;
            ModelPath = project.AiModel.ModelPath ?? string.Empty;

            _logger.LogInformation("使用舊版專案 AI 模型設定 (Fallback)");
        }
    }

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(CanSave));
    }

    /// <summary>
    /// 瀏覽 Workflow 設定檔
    /// </summary>
    [RelayCommand]
    private void BrowseWorkflow()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "JSON 檔案|*.json|所有檔案|*.*",
            Title = "選擇 Workflow 設定檔"
        };

        if (!string.IsNullOrEmpty(WorkflowSettingPath) && File.Exists(WorkflowSettingPath))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(WorkflowSettingPath);
            dialog.FileName = Path.GetFileName(WorkflowSettingPath);
        }

        if (dialog.ShowDialog() == true)
        {
            WorkflowSettingPath = dialog.FileName;
        }
    }

    /// <summary>
    /// 儲存專案
    /// </summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            System.Windows.MessageBox.Show("請輸入專案名稱", "驗證錯誤", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        try
        {
            _logger.LogInformation("儲存專案: {Name}", Name);

            var project = new ProjectConfig
            {
                Name = Name.Trim(),
                Description = Description?.Trim(),
                Camera = new ProjectCameraConfig
                {
                    UserSet = CameraUserSet
                },
                Light = new ProjectLightConfig
                {
                    // 現在只支援 Serial COM Port
                    Interface = "Serial",
                    PortName = LightComPort,
                    ChannelBrightness = new Dictionary<string, int>
                    {
                        { "1", LightCh1 },
                        { "2", LightCh2 }
                    }
                },
                Plc = new ProjectPlcConfig
                {
                    Host = PlcHost,
                    Port = PlcPort
                }
            };

            // ===== AI 模型設定（新架構優先） =====
            if (UseModelSelection && SelectedModel != null)
            {
                // 新架構：只儲存 ModelName
                project.ModelName = SelectedModel.Name;
                _logger.LogInformation("專案使用模型管理: ModelName={ModelName}", project.ModelName);
            }
            else
            {
                // 舊架構 Fallback：儲存完整 AiModel 設定
                project.AiModel = new ProjectAiModelConfig
                {
                    InferenceType = IsWorkflow ? "Workflow" : "SingleModel",
                    EdgeHubHost = EdgeHubHost,
                    EdgeHubPort = EdgeHubPort,
                    WorkflowPort = WorkflowPort,
                    WorkflowSettingPath = IsWorkflow ? WorkflowSettingPath : null,
                    InferencePort = InferencePort,
                    ModelPath = IsSingleModel ? ModelPath : null
                };
                _logger.LogInformation("專案使用手動 AI 模型設定 (Fallback)");
            }

            // 如果是編輯模式且名稱變更，需要先刪除舊專案資料夾
            if (_isEditMode && _existingProject != null &&
                !string.IsNullOrEmpty(_existingProject.FolderPath) &&
                _existingProject.Name != Name)
            {
                _logger.LogInformation("專案名稱變更，將刪除舊資料夾: {OldPath}", _existingProject.FolderPath);
                await _projectConfigService.DeleteProjectAsync(_existingProject.FolderPath);
            }

            // 儲存專案
            await _projectConfigService.SaveProjectAsync(project, _projectsFolder);

            _logger.LogInformation("專案已儲存: {Name}, ModelName={ModelName}", Name, project.ModelName ?? "(手動設定)");

            RequestClose?.Invoke(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "儲存專案失敗");
            System.Windows.MessageBox.Show(
                $"儲存專案失敗：\n{ex.Message}",
                "錯誤",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 取消並關閉視窗
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke(false);
    }
}
