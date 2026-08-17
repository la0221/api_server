using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AIVision.Application.Configuration;
using AIVision.Application.Ports.Models;
using AIVision.Presentation.Wpf.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVision.Presentation.Wpf.Services;

/// <summary>
/// 模型配置服務，負責讀寫 models.json 並整合自動掃描功能。
/// </summary>
public sealed class ModelConfigService
{
    private readonly string _configPath;
    private readonly ILogger<ModelConfigService>? _logger;
    private readonly IModelDiscoveryService? _discoveryService;
    private readonly ModelScanOptions? _scanOptions;

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private ModelsConfiguration? _cachedConfig;
    private List<ModelConfig>? _cachedAllModels;

    /// <summary>
    /// 建構子（支援 DI 注入）
    /// </summary>
    public ModelConfigService(
        ILogger<ModelConfigService> logger,
        IModelDiscoveryService discoveryService,
        IOptions<ModelScanOptions> scanOptions,
        string configPath = "models.json")
    {
        _configPath = configPath;
        _logger = logger;
        _discoveryService = discoveryService;
        _scanOptions = scanOptions.Value;
    }

    /// <summary>
    /// 建構子（向後兼容）
    /// </summary>
    public ModelConfigService(string configPath = "models.json", ILogger<ModelConfigService>? logger = null)
    {
        _configPath = configPath;
        _logger = logger;
        _discoveryService = null;
        _scanOptions = null;
    }

    /// <summary>
    /// 取得所有模型（自動掃描 + 手動配置）
    /// </summary>
    public async Task<List<ModelConfig>> GetAllModelsAsync()
    {
        if (_cachedAllModels != null)
        {
            _logger?.LogDebug("[ModelConfig] 使用快取的模型列表 - 總計 {Count} 個", _cachedAllModels.Count);
            return _cachedAllModels;
        }

        var config = await LoadAsync();
        var allModels = new List<ModelConfig>();

        // 1. 自動掃描模型
        _logger?.LogInformation("[ModelConfig] 檢查自動掃描 - AutoScan: {AutoScan}, DiscoveryService: {HasService}",
            _scanOptions?.AutoScan, _discoveryService != null);

        if (_scanOptions?.AutoScan == true && _discoveryService != null)
        {
            var scanFolder = _scanOptions.ScanFolder;
            if (!string.IsNullOrEmpty(scanFolder))
            {
                _logger?.LogInformation("[ModelConfig] 載入配置 - 自動掃描: {AutoScan}, 資料夾: {Folder}",
                    _scanOptions.AutoScan, scanFolder);

                var discovered = await _discoveryService.ScanAsync(scanFolder);

                _logger?.LogInformation("[ModelConfig] 合併模型列表 - 掃描: {ScanCount}, 覆蓋: {OverrideCount}",
                    discovered.Count, config.Overrides.Count);

                foreach (var d in discovered)
                {
                    var modelConfig = ConvertToModelConfig(d, config.Overrides);
                    _logger?.LogDebug("[ModelConfig] 轉換模型: {Name}, ResultTypes: [{Types}]",
                        modelConfig.Name, string.Join(", ", modelConfig.ResultTypes));
                    allModels.Add(modelConfig);
                }
            }
        }
        else
        {
            _logger?.LogWarning("[ModelConfig] 自動掃描未啟用或 DiscoveryService 為 null");
        }

        // 2. 加入手動配置的模型（ManualModels 優先）
        _logger?.LogInformation("[ModelConfig] 開始處理 ManualModels: {Count} 個", config.ManualModels.Count);
        var manualAddedCount = 0;
        var manualSkippedCount = 0;
        foreach (var manual in config.ManualModels)
        {
            var existingModel = allModels.FirstOrDefault(m =>
                (!string.IsNullOrEmpty(m.OriginalName) && m.OriginalName == manual.OriginalName) ||
                m.Name == manual.Name);

            if (existingModel == null)
            {
                allModels.Add(manual);
                manualAddedCount++;
                _logger?.LogDebug("[ModelConfig] + ManualModel 已加入: {Name}", manual.Name);
            }
            else
            {
                manualSkippedCount++;
                _logger?.LogWarning("[ModelConfig] - ManualModel 重複跳過: {Name} (與 {ExistingName} 重複)",
                    manual.Name, existingModel.Name);
            }
        }
        _logger?.LogInformation("[ModelConfig] ManualModels 處理完成: 加入={Added}, 跳過={Skipped}",
            manualAddedCount, manualSkippedCount);

        // 3. 向後兼容：加入舊版 Models 配置（Legacy 最低優先）
        _logger?.LogInformation("[ModelConfig] 開始處理 Legacy Models: {Count} 個", config.Models.Count);
        var legacyAddedCount = 0;
        var legacySkippedCount = 0;
        foreach (var legacy in config.Models)
        {
            var existingModel = allModels.FirstOrDefault(m => m.Name == legacy.Name);
            if (existingModel == null)
            {
                allModels.Add(legacy);
                legacyAddedCount++;
                _logger?.LogDebug("[ModelConfig] + Legacy Model 已加入: {Name}", legacy.Name);
            }
            else
            {
                legacySkippedCount++;
                _logger?.LogWarning("[ModelConfig] - Legacy Model 重複跳過: {Name} (已存在於 ManualModels 或自動掃描)",
                    legacy.Name);
            }
        }
        _logger?.LogInformation("[ModelConfig] Legacy Models 處理完成: 加入={Added}, 跳過={Skipped}",
            legacyAddedCount, legacySkippedCount);

        _logger?.LogInformation("[ModelConfig] 模型列表已更新 - 自動掃描: {Scanned}, ManualModels: {Manual}(+{ManualAdded}), LegacyModels: {Legacy}(+{LegacyAdded}), 總計: {Total}",
            allModels.Count - manualAddedCount - legacyAddedCount,
            config.ManualModels.Count, manualAddedCount,
            config.Models.Count, legacyAddedCount,
            allModels.Count);

        _cachedAllModels = allModels;
        return allModels;
    }

    /// <summary>
    /// 重新掃描模型
    /// </summary>
    public async Task RescanModelsAsync()
    {
        _cachedConfig = null; // 清除配置快取
        _cachedAllModels = null; // 清除模型快取
        await GetAllModelsAsync();
    }

    /// <summary>
    /// 重新掃描指定資料夾的模型
    /// </summary>
    public async Task RescanModelsAsync(string scanFolder)
    {
        if (_scanOptions != null)
        {
            _scanOptions.ScanFolder = scanFolder;
            _logger?.LogInformation("[ModelConfig] 已更新掃描資料夾: {Folder}", scanFolder);
        }

        _cachedConfig = null;
        _cachedAllModels = null;
        await GetAllModelsAsync();
    }

    /// <summary>
    /// 載入模型配置。
    /// </summary>
    public async Task<ModelsConfiguration> LoadAsync()
    {
        try
        {
            _logger?.LogDebug("[ModelConfig] 開始載入配置: {Path}", _configPath);

            if (!File.Exists(_configPath))
            {
                _logger?.LogWarning("[ModelConfig] 配置檔不存在: {Path}，將建立預設配置", _configPath);
                var defaultConfig = CreateDefaultConfiguration();
                await SaveAsync(defaultConfig);
                _cachedConfig = defaultConfig;
                return defaultConfig;
            }

            var json = await File.ReadAllTextAsync(_configPath);
            _logger?.LogDebug("[ModelConfig] 讀取檔案完成，長度: {Length} bytes", json.Length);

            var config = JsonSerializer.Deserialize<ModelsConfiguration>(json, _jsonOptions);

            if (config == null)
            {
                _logger?.LogWarning("[ModelConfig] 配置反序列化為 null，使用預設配置");
                config = CreateDefaultConfiguration();
                await SaveAsync(config);
            }

            _cachedConfig = config;
            _logger?.LogInformation("[ModelConfig] ✓ 已載入配置: ManualModels={Manual}, Models={Legacy}, Overrides={Overrides}",
                config.ManualModels.Count, config.Models.Count, config.Overrides.Count);
            return config;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[ModelConfig] ✗ 載入配置失敗: {Path}", _configPath);
            var defaultConfig = CreateDefaultConfiguration();
            _cachedConfig = defaultConfig;
            return defaultConfig;
        }
    }

    /// <summary>
    /// 儲存模型配置。
    /// </summary>
    public async Task SaveAsync(ModelsConfiguration config)
    {
        try
        {
            _logger?.LogDebug("[ModelConfig] 開始儲存配置，ManualModels: {Manual}, Models: {Legacy}, Overrides: {Overrides}",
                config.ManualModels.Count, config.Models.Count, config.Overrides.Count);

            var json = JsonSerializer.Serialize(config, _jsonOptions);
            _logger?.LogDebug("[ModelConfig] JSON 序列化完成，長度: {Length} bytes", json.Length);

            // 確保目錄存在
            var directory = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                _logger?.LogDebug("[ModelConfig] 建立目錄: {Directory}", directory);
            }

            await File.WriteAllTextAsync(_configPath, json);
            _logger?.LogDebug("[ModelConfig] 檔案寫入完成: {Path}", _configPath);

            _cachedConfig = config;
            _cachedAllModels = null; // 清除快取以強制重新載入
            _logger?.LogInformation("[ModelConfig] ✓ 已儲存模型配置: {Path}", _configPath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[ModelConfig] ✗ 儲存模型配置失敗: {Path}", _configPath);
            throw;
        }
    }

    /// <summary>
    /// 取得目前使用的模型配置。
    /// </summary>
    public async Task<ModelConfig?> GetCurrentModelAsync()
    {
        var allModels = await GetAllModelsAsync();
        var config = _cachedConfig ?? await LoadAsync();

        if (string.IsNullOrEmpty(config.CurrentModelName))
        {
            return allModels.FirstOrDefault();
        }

        // 先用 OriginalName 搜尋（用於掃描的模型）
        var model = allModels.FirstOrDefault(m =>
            m.OriginalName == config.CurrentModelName ||
            m.Name == config.CurrentModelName);

        return model ?? allModels.FirstOrDefault();
    }

    /// <summary>
    /// 根據名稱取得模型配置。
    /// </summary>
    public async Task<ModelConfig?> GetModelByNameAsync(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return null;
        }

        var allModels = await GetAllModelsAsync();
        return allModels.FirstOrDefault(m =>
            string.Equals(m.OriginalName, modelName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.Name, modelName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 設定目前使用的模型。
    /// </summary>
    public async Task SetCurrentModelAsync(string modelName)
    {
        var config = _cachedConfig ?? await LoadAsync();
        config.CurrentModelName = modelName;
        await SaveAsync(config);
        _logger?.LogInformation("已設定目前模型: {ModelName}", modelName);
    }

    /// <summary>
    /// 新增模型覆蓋設定。
    /// </summary>
    public async Task ApplyOverrideAsync(string modelName, ModelOverride overrideSettings)
    {
        var config = _cachedConfig ?? await LoadAsync();
        config.Overrides[modelName] = overrideSettings;
        await SaveAsync(config);

        _logger?.LogInformation("[ModelConfig] 套用覆蓋設定: {ModelName} -> DisplayName={DisplayName}",
            modelName, overrideSettings.DisplayName);
    }

    /// <summary>
    /// 新增模型配置。
    /// </summary>
    public async Task AddModelAsync(ModelConfig model)
    {
        _logger?.LogInformation("[ModelConfig] 開始新增模型: {Name}", model.Name);

        var config = _cachedConfig ?? await LoadAsync();
        _logger?.LogDebug("[ModelConfig] 載入配置完成，ManualModels: {Manual}, Models: {Legacy}",
            config.ManualModels.Count, config.Models.Count);

        // 檢查是否已存在同名模型（在 ManualModels 中）
        if (config.ManualModels.Any(m => m.Name == model.Name))
        {
            _logger?.LogWarning("[ModelConfig] 模型 '{Name}' 已存在於 ManualModels，無法新增", model.Name);
            throw new InvalidOperationException($"模型 '{model.Name}' 已存在");
        }

        // 如果模型存在於舊版 Models 中，從 Models 移除（避免重複）
        var existingLegacy = config.Models.FirstOrDefault(m => m.Name == model.Name);
        if (existingLegacy != null)
        {
            config.Models.Remove(existingLegacy);
            _logger?.LogInformation("[ModelConfig] 從 Legacy Models 移除舊版模型: {Name}（將由 ManualModels 接管）", model.Name);
        }

        config.ManualModels.Add(model);
        _logger?.LogDebug("[ModelConfig] 已加入 ManualModels，新數量: {Count}", config.ManualModels.Count);

        await SaveAsync(config);
        _logger?.LogInformation("[ModelConfig] ✓ 已新增模型: {ModelName}，儲存至: {Path}", model.Name, _configPath);
    }

    /// <summary>
    /// 更新模型配置。
    /// </summary>
    public async Task UpdateModelAsync(string originalName, ModelConfig updatedModel)
    {
        var config = _cachedConfig ?? await LoadAsync();

        // 如果是掃描的模型，更新 Overrides
        var allModels = await GetAllModelsAsync();
        var existingModel = allModels.FirstOrDefault(m =>
            m.OriginalName == originalName || m.Name == originalName);

        if (existingModel?.IsAutoDiscovered == true)
        {
            // 對於自動掃描的模型，只能更新覆蓋設定
            var overrideSettings = new ModelOverride
            {
                DisplayName = updatedModel.Name,
                Description = updatedModel.Description,
                ClassDisplayNames = updatedModel.ResultTypeDisplayNames,
                EdgeHubHost = updatedModel.EdgeHubHost,
                EdgeHubPort = updatedModel.EdgeHubPort,
                InferencePort = updatedModel.InferencePort,
                InferenceType = updatedModel.InferenceType,
                WorkflowSettingPath = updatedModel.WorkflowSettingPath,
                WorkflowPort = updatedModel.WorkflowPort
            };
            await ApplyOverrideAsync(existingModel.OriginalName ?? originalName, overrideSettings);
            return;
        }

        // 手動模型：更新 ManualModels 或 Models
        var manualModel = config.ManualModels.FirstOrDefault(m => m.Name == originalName);
        if (manualModel != null)
        {
            var index = config.ManualModels.IndexOf(manualModel);
            config.ManualModels[index] = updatedModel;
        }
        else
        {
            var legacyModel = config.Models.FirstOrDefault(m => m.Name == originalName);
            if (legacyModel != null)
            {
                var index = config.Models.IndexOf(legacyModel);
                config.Models[index] = updatedModel;
            }
            else
            {
                throw new InvalidOperationException($"找不到模型 '{originalName}'");
            }
        }

        // 如果是目前使用的模型且名稱改變，更新 CurrentModelName
        if (config.CurrentModelName == originalName && originalName != updatedModel.Name)
        {
            config.CurrentModelName = updatedModel.Name;
        }

        await SaveAsync(config);
        _logger?.LogInformation("已更新模型: {ModelName}", updatedModel.Name);
    }

    /// <summary>
    /// 刪除模型配置。
    /// </summary>
    public async Task DeleteModelAsync(string modelName)
    {
        var config = _cachedConfig ?? await LoadAsync();
        var allModels = await GetAllModelsAsync();
        var model = allModels.FirstOrDefault(m =>
            m.OriginalName == modelName || m.Name == modelName);

        if (model == null)
        {
            throw new InvalidOperationException($"找不到模型 '{modelName}'");
        }

        if (model.IsAutoDiscovered)
        {
            // 自動掃描的模型不能刪除，只能移除覆蓋設定
            config.Overrides.Remove(model.OriginalName ?? modelName);
            _logger?.LogWarning("自動掃描的模型無法刪除，已移除覆蓋設定: {ModelName}", modelName);
        }
        else
        {
            // 從 ManualModels 或 Models 移除
            var manualModel = config.ManualModels.FirstOrDefault(m => m.Name == modelName);
            if (manualModel != null)
            {
                config.ManualModels.Remove(manualModel);
            }
            else
            {
                var legacyModel = config.Models.FirstOrDefault(m => m.Name == modelName);
                if (legacyModel != null)
                {
                    config.Models.Remove(legacyModel);
                }
            }
        }

        // 如果刪除的是目前使用的模型，清除 CurrentModelName
        if (config.CurrentModelName == modelName)
        {
            config.CurrentModelName = null;
        }

        await SaveAsync(config);
        _logger?.LogInformation("已刪除模型: {ModelName}", modelName);
    }

    /// <summary>
    /// 將 DiscoveredModel 轉換為 ModelConfig
    /// </summary>
    private ModelConfig ConvertToModelConfig(DiscoveredModel discovered, Dictionary<string, ModelOverride> overrides)
    {
        var config = new ModelConfig
        {
            OriginalName = discovered.FolderName,
            Name = discovered.Name,
            Description = discovered.Description,
            FolderPath = discovered.FolderPath,
            IsAutoDiscovered = true,
            Plugin = discovered.Plugin,
            InputShape = discovered.InputShape,
            ModelType = ConvertModelType(discovered.ModelType),
            ResultTypes = discovered.DefectClasses.ToList(),
            ModelPath = discovered.FolderPath
        };

        // 套用覆蓋設定
        if (overrides.TryGetValue(discovered.FolderName, out var ov))
        {
            _logger?.LogInformation("[ModelConfig] 套用覆蓋設定: {ModelName} -> DisplayName={DisplayName}, EdgeHubHost={Host}",
                discovered.FolderName, ov.DisplayName, ov.EdgeHubHost);

            if (!string.IsNullOrEmpty(ov.DisplayName))
                config.Name = ov.DisplayName;
            if (!string.IsNullOrEmpty(ov.Description))
                config.Description = ov.Description;
            if (ov.ClassDisplayNames != null)
                config.ResultTypeDisplayNames = ov.ClassDisplayNames;
            if (!string.IsNullOrEmpty(ov.EdgeHubHost))
                config.EdgeHubHost = ov.EdgeHubHost;
            if (ov.EdgeHubPort.HasValue)
                config.EdgeHubPort = ov.EdgeHubPort.Value;
            if (ov.InferencePort.HasValue)
                config.InferencePort = ov.InferencePort.Value;
            if (ov.InferenceType.HasValue)
                config.InferenceType = ov.InferenceType.Value;
            if (!string.IsNullOrEmpty(ov.WorkflowSettingPath))
                config.WorkflowSettingPath = ov.WorkflowSettingPath;
            if (ov.WorkflowPort.HasValue)
                config.WorkflowPort = ov.WorkflowPort.Value;
        }

        return config;
    }

    /// <summary>
    /// 轉換模型類型
    /// </summary>
    private static ModelType ConvertModelType(AinaviModelType ainaviType)
    {
        return ainaviType switch
        {
            AinaviModelType.Classification => ModelType.Classification,
            AinaviModelType.Detection => ModelType.Detection,
            AinaviModelType.Segmentation => ModelType.Detection, // Segmentation 也視為 Detection
            _ => ModelType.Classification
        };
    }

    /// <summary>
    /// 建立預設配置。
    /// </summary>
    private ModelsConfiguration CreateDefaultConfiguration()
    {
        return new ModelsConfiguration
        {
            CurrentModelName = null,
            Models = new List<ModelConfig>(),
            Overrides = new Dictionary<string, ModelOverride>(),
            ManualModels = new List<ModelConfig>()
        };
    }
}
