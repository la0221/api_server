using AIVision.Application.Ports.Services;
using AIVision.Presentation.Wpf.Models;

namespace AIVision.Presentation.Wpf.Services;

/// <summary>
/// ModelConfigService 的適配器，實作 IModelConfigProvider 介面
/// 用於讓 Infrastructure 層的 ProjectInitializationService 能夠存取模型配置
/// </summary>
public sealed class ModelConfigProviderAdapter : IModelConfigProvider
{
    private readonly ModelConfigService _modelConfigService;

    public ModelConfigProviderAdapter(ModelConfigService modelConfigService)
    {
        _modelConfigService = modelConfigService;
    }

    /// <inheritdoc />
    public async Task<ModelConfigInfo?> GetModelByNameAsync(string modelName)
    {
        var model = await _modelConfigService.GetModelByNameAsync(modelName);
        return model != null ? ToModelConfigInfo(model) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModelConfigInfo>> GetAllModelsAsync()
    {
        var models = await _modelConfigService.GetAllModelsAsync();
        return models.Select(ToModelConfigInfo).ToList();
    }

    /// <summary>
    /// 將 ModelConfig 轉換為 ModelConfigInfo
    /// </summary>
    private static ModelConfigInfo ToModelConfigInfo(ModelConfig model)
    {
        return new ModelConfigInfo
        {
            Name = model.Name,
            Description = model.Description,
            InferenceType = model.InferenceType.ToString(),
            EdgeHubHost = model.EdgeHubHost,
            EdgeHubPort = model.EdgeHubPort,
            InferencePort = model.InferencePort,
            ModelPath = model.ModelPath,
            WorkflowPort = model.WorkflowPort,
            WorkflowSettingPath = model.WorkflowSettingPath,
            // 瑕疵過濾設定
            DefectFilteringEnabled = model.DefectFiltering?.Enabled ?? false,
            PixelAreaMm2 = model.DefectFiltering?.PixelAreaMm2 ?? 0.00000576,
            PixelSizeMm = model.DefectFiltering?.PixelSizeMm ?? 0.0024,
            MinimumAreaMm2 = model.DefectFiltering?.MinimumAreaMm2 ?? 0.02,
            MediumAreaMm2 = model.DefectFiltering?.MediumAreaMm2 ?? 0.05,
            CloseDistanceMm = model.DefectFiltering?.CloseDistanceMm ?? 50.0,
            CriticalClasses = model.DefectFiltering?.CriticalClasses ?? new List<string>()
        };
    }
}
