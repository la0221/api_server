using System.Text.Json;
using System.Text.Json.Serialization;
using AIVision.Application.Configuration;
using AIVision.Application.Ports.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVision.Infrastructure.Services;

/// <summary>
/// 本機模型掃描服務實作
/// </summary>
public sealed class LocalModelDiscoveryService : IModelDiscoveryService
{
    private readonly ILogger<LocalModelDiscoveryService> _logger;
    private readonly ModelScanOptions _options;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public LocalModelDiscoveryService(
        ILogger<LocalModelDiscoveryService> logger,
        IOptions<ModelScanOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<DiscoveredModel>> ScanAsync(
        string folderPath,
        CancellationToken ct = default)
    {
        var models = new List<DiscoveredModel>();

        _logger.LogInformation("[ModelDiscovery] 開始掃描模型資料夾: {FolderPath}", folderPath);

        if (!Directory.Exists(folderPath))
        {
            _logger.LogWarning("[ModelDiscovery] 掃描資料夾不存在，嘗試自動創建: {FolderPath}", folderPath);
            try
            {
                Directory.CreateDirectory(folderPath);
                _logger.LogInformation("[ModelDiscovery] 資料夾創建成功: {FolderPath}", folderPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ModelDiscovery] 資料夾創建失敗: {FolderPath}", folderPath);
                return models;
            }
        }

        var directories = Directory.GetDirectories(folderPath);

        foreach (var dir in directories)
        {
            ct.ThrowIfCancellationRequested();

            var model = await ScanSingleAsync(dir, ct);
            if (model != null)
            {
                models.Add(model);
            }
        }

        _logger.LogInformation("[ModelDiscovery] 掃描完成 - 共發現 {Count} 個模型", models.Count);

        return models;
    }

    public async Task<DiscoveredModel?> ScanSingleAsync(
        string modelFolderPath,
        CancellationToken ct = default)
    {
        var folderName = Path.GetFileName(modelFolderPath);
        _logger.LogDebug("[ModelDiscovery] 發現模型資料夾: {FolderName}", folderName);

        var inferenceJsonPath = Path.Combine(modelFolderPath, "inference.json");
        var infoJsonPath = Path.Combine(modelFolderPath, "info.json");

        // 必須有 inference.json
        if (!File.Exists(inferenceJsonPath))
        {
            _logger.LogWarning("[ModelDiscovery] 缺少 inference.json: {Path}", inferenceJsonPath);
            return null;
        }

        try
        {
            _logger.LogDebug("[ModelDiscovery] 讀取 inference.json: {Path}", inferenceJsonPath);

            // 讀取 inference.json
            var inferenceJson = await File.ReadAllTextAsync(inferenceJsonPath, ct);
            var inference = JsonSerializer.Deserialize<InferenceJson>(inferenceJson, JsonOptions);

            if (inference == null)
            {
                _logger.LogWarning("[ModelDiscovery] inference.json 解析為 null: {Path}", inferenceJsonPath);
                return null;
            }

            // 讀取 info.json（可選）
            string? modelName = null;
            string? description = null;

            if (File.Exists(infoJsonPath))
            {
                var infoJson = await File.ReadAllTextAsync(infoJsonPath, ct);
                var info = JsonSerializer.Deserialize<InfoJson>(infoJson, JsonOptions);
                modelName = info?.Name;
                description = info?.Description;
            }

            // 解析 class_map
            var classMap = inference.Model?.ClassMap ?? new Dictionary<string, string>();
            var classMapInt = classMap
                .Where(kvp => int.TryParse(kvp.Key, out _))
                .ToDictionary(
                    kvp => int.Parse(kvp.Key),
                    kvp => kvp.Value
                );

            var defectClasses = classMapInt
                .OrderBy(kvp => kvp.Key)
                .Select(kvp => kvp.Value)
                .ToList();

            // 轉換 plugin 到 ModelType
            var modelType = inference.Plugin?.ToLowerInvariant() switch
            {
                "cls_1" => AinaviModelType.Classification,
                "det_1s" => AinaviModelType.Detection,
                "seg_2" => AinaviModelType.Segmentation,
                _ => AinaviModelType.Unknown
            };

            if (modelType == AinaviModelType.Unknown)
            {
                _logger.LogWarning("[ModelDiscovery] 未知的 Plugin 類型: {Plugin}", inference.Plugin);
            }

            var model = new DiscoveredModel
            {
                FolderPath = modelFolderPath,
                FolderName = folderName,
                Name = modelName ?? folderName,
                Description = description,
                Plugin = inference.Plugin ?? "unknown",
                ModelType = modelType,
                InputShape = inference.Model?.InputShape ?? [],
                ModelFileName = inference.Model?.ModelPath ?? "final.mw",
                ClassMap = classMapInt,
                DefectClasses = defectClasses,
                DiscoveredAt = DateTime.Now
            };

            _logger.LogInformation(
                "[ModelDiscovery] 解析成功 - Plugin: {Plugin}, Classes: [{Classes}]",
                model.Plugin,
                string.Join(", ", model.DefectClasses));

            return model;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "[ModelDiscovery] 解析失敗 - JSON 格式錯誤: {Path}", inferenceJsonPath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ModelDiscovery] 解析失敗 - {Error}", ex.Message);
            return null;
        }
    }

    #region JSON DTOs

    private sealed class InferenceJson
    {
        public string? Plugin { get; set; }
        public ModelJson? Model { get; set; }
    }

    private sealed class ModelJson
    {
        [JsonPropertyName("input_shape")]
        public int[]? InputShape { get; set; }

        [JsonPropertyName("model_path")]
        public string? ModelPath { get; set; }

        [JsonPropertyName("class_map")]
        public Dictionary<string, string>? ClassMap { get; set; }
    }

    private sealed class InfoJson
    {
        public string? Name { get; set; }
        public string? TaskId { get; set; }
        public string? Description { get; set; }
    }

    #endregion
}
