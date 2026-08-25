using System.Text.Json;
using AIVision.Application.Ports.Models;
using Microsoft.Extensions.Logging;

namespace AIVision.Infrastructure.Services;

/// <summary>
/// 本地 ONNX 模型發現服務(取代 AINAVI 資料夾掃描)。
/// 直接列舉 ScanFolder 下的 <c>*.onnx</c> 檔,每個檔即一個可選模型;
/// 類別由同名 <c>&lt;name&gt;.names.json</c>({index:cavity-code})推得。
/// 重點:<see cref="DiscoveredModel.FolderPath"/> = <b>.onnx 完整路徑</b>
/// (ConvertToModelConfig 會把它複製進 <c>ModelConfig.ModelPath</c>,辨識器以此載入)。
/// </summary>
public sealed class OnnxModelDiscoveryService : IModelDiscoveryService
{
    private readonly ILogger<OnnxModelDiscoveryService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public OnnxModelDiscoveryService(ILogger<OnnxModelDiscoveryService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 掃描資料夾下所有 <c>*.onnx</c> 模型。資料夾不存在 → 回空清單(不拋例外)。
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredModel>> ScanAsync(
        string folderPath,
        CancellationToken ct = default)
    {
        var models = new List<DiscoveredModel>();

        // 跨機部署：appsettings 常寫死 D:\AIVisionModels，但現場機可能只有 C 槽
        // （2026-08-19 驗證機實況）。相對路徑改以「程式目錄」為基準，才不必逐台改設定檔。
        folderPath = ResolveScanFolder(folderPath);

        _logger.LogInformation("[OnnxDiscovery] 開始掃描 ONNX 模型資料夾: {FolderPath}", folderPath);

        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            // 「沒配模型資料夾」是常態（本機不做模型管理時），不是故障 →
            // 用 Information，別每次啟動噴 WARN 讓現場以為壞掉。
            _logger.LogInformation(
                "[OnnxDiscovery] 掃描資料夾不存在，跳過本機模型掃描: {FolderPath}（如需自動列出模型，" +
                "請把 Models:ScanFolder 指到存在的路徑，或改用相對路徑如 models）", folderPath);
            return models;
        }

        var onnxFiles = Directory.EnumerateFiles(folderPath, "*.onnx", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

        foreach (var onnxPath in onnxFiles)
        {
            ct.ThrowIfCancellationRequested();

            var model = await ScanSingleAsync(onnxPath, ct);
            if (model != null)
            {
                models.Add(model);
            }
        }

        _logger.LogInformation("[OnnxDiscovery] 掃描完成 - 共發現 {Count} 個 ONNX 模型", models.Count);

        return models;
    }

    /// <summary>
    /// 掃描資料夾路徑解析：絕對路徑原樣;相對路徑以**程式目錄**為基準
    /// (不用 CurrentDirectory——從捷徑啟動時工作目錄不等於 exe 所在地)。
    /// </summary>
    private static string ResolveScanFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return folderPath;
        try
        {
            return Path.IsPathRooted(folderPath)
                ? folderPath
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, folderPath));
        }
        catch
        {
            return folderPath;   // 路徑字串本身壞掉:交給下面的 Exists 檢查處理
        }
    }

    /// <summary>
    /// 掃描單一 ONNX 模型檔。非 <c>.onnx</c> → 回 null;缺 <c>.names.json</c> 仍列出(類別為空)。
    /// </summary>
    public async Task<DiscoveredModel?> ScanSingleAsync(
        string modelFolderPath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelFolderPath) ||
            !modelFolderPath.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("[OnnxDiscovery] 非 .onnx 檔,略過: {Path}", modelFolderPath);
            return null;
        }

        if (!File.Exists(modelFolderPath))
        {
            _logger.LogWarning("[OnnxDiscovery] .onnx 檔不存在: {Path}", modelFolderPath);
            return null;
        }

        try
        {
            var name = Path.GetFileNameWithoutExtension(modelFolderPath);
            var dir = Path.GetDirectoryName(modelFolderPath) ?? string.Empty;

            // 解析同名 .names.json({index:cavity-code}),依 int key 排序。
            var (classMap, defectClasses) = await ReadClassNamesAsync(dir, name, ct);

            // 守門:缺少有效(非空)類別表 → 不列為可選模型,避免「選了模型卻靜默 fallback 至 baseline 字集」。
            if (defectClasses.Count == 0)
            {
                _logger.LogWarning(
                    "[OnnxDiscovery] 跳過模型(缺少有效 {NamesFile} 類別表): {Path}",
                    $"{name}.names.json", modelFolderPath);
                return null;
            }

            // 最佳努力:從 .report.json / .train-report.json 萃取準確率字串作為描述。
            var description = await ReadDescriptionAsync(dir, name, ct);

            // 旁置設定 <name>.config.json:若有 DisplayName/Description 則覆寫顯示用欄位
            // (FolderName 仍為檔名,維持穩定識別)。讀取失敗一律忽略,不阻斷掃描。
            var (sidecarDisplayName, sidecarDescription) = await ReadSidecarDisplayAsync(dir, name, ct);
            var displayName = string.IsNullOrWhiteSpace(sidecarDisplayName) ? name : sidecarDisplayName!;
            if (!string.IsNullOrWhiteSpace(sidecarDescription))
                description = sidecarDescription;

            var model = new DiscoveredModel
            {
                FolderPath = modelFolderPath,   // 完整 .onnx 路徑(ConvertToModelConfig→ModelPath)
                FolderName = name,              // 唯一識別(檔名去副檔名)
                Name = displayName,
                Description = description,
                Plugin = "onnx",
                ModelType = AinaviModelType.Classification,
                ModelFileName = Path.GetFileName(modelFolderPath),
                ClassMap = classMap,
                DefectClasses = defectClasses,
                DiscoveredAt = DateTime.Now
            };

            _logger.LogInformation(
                "[OnnxDiscovery] 解析成功 - {Name}, Classes={ClassCount} [{Classes}]",
                name, defectClasses.Count, string.Join(", ", defectClasses));

            return model;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OnnxDiscovery] 解析失敗: {Path}", modelFolderPath);
            return null;
        }
    }

    /// <summary>
    /// 讀同名 <c>&lt;name&gt;.names.json</c>({index:className}),回 (依索引排序的 ClassMap, 排序後 className 清單)。
    /// 檔案不存在或解析失敗 → 回 (空 dict, 空 list),不拋例外(仍要列出模型)。
    /// </summary>
    private async Task<(IReadOnlyDictionary<int, string> classMap, IReadOnlyList<string> defectClasses)>
        ReadClassNamesAsync(string dir, string name, CancellationToken ct)
    {
        var namesPath = Path.Combine(dir, $"{name}.names.json");
        if (!File.Exists(namesPath))
        {
            _logger.LogWarning("[OnnxDiscovery] 缺少 {NamesFile},類別清單將為空", $"{name}.names.json");
            return (new Dictionary<int, string>(), new List<string>());
        }

        try
        {
            var json = await File.ReadAllTextAsync(namesPath, ct);
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
                      ?? new Dictionary<string, string>();

            var classMap = raw
                .Where(kvp => int.TryParse(kvp.Key, out _))
                .ToDictionary(kvp => int.Parse(kvp.Key), kvp => kvp.Value);

            var defectClasses = classMap
                .OrderBy(kvp => kvp.Key)
                .Select(kvp => kvp.Value)
                .ToList();

            return (classMap, defectClasses);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OnnxDiscovery] 解析 {NamesFile} 失敗,類別清單將為空", $"{name}.names.json");
            return (new Dictionary<int, string>(), new List<string>());
        }
    }

    /// <summary>
    /// 最佳努力讀取同名 <c>.report.json</c> / <c>.train-report.json</c>,若含準確率欄位則組成描述字串;
    /// 否則回 null。任何錯誤一律吞掉(描述為選用資訊,不可阻斷掃描)。
    /// </summary>
    private async Task<string?> ReadDescriptionAsync(string dir, string name, CancellationToken ct)
    {
        foreach (var suffix in new[] { ".report.json", ".train-report.json" })
        {
            var reportPath = Path.Combine(dir, $"{name}{suffix}");
            if (!File.Exists(reportPath))
                continue;

            try
            {
                var json = await File.ReadAllTextAsync(reportPath, ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    continue;

                // 已知的準確率欄位(任一存在即採用)。
                foreach (var key in new[] { "val_accuracy", "ours_v3_acc", "accuracy", "acc" })
                {
                    if (root.TryGetProperty(key, out var el) &&
                        el.ValueKind == JsonValueKind.Number &&
                        el.TryGetDouble(out var acc))
                    {
                        return $"準確率 {acc:P2}";
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[OnnxDiscovery] 讀取報告 {Report} 失敗(略過描述)", $"{name}{suffix}");
            }
        }

        return null;
    }

    /// <summary>
    /// 最佳努力讀取同名旁置設定 <c>&lt;name&gt;.config.json</c> 的 <c>displayName</c> / <c>description</c>
    /// (與 <c>MoldCodeModelConfig</c> 同格式;此處僅讀顯示用欄位,避免讓 Infrastructure 依賴 Onnx 專案)。
    /// 檔案不存在 / 解析失敗 → 回 (null, null),不拋例外(顯示資訊為選用,不可阻斷掃描)。
    /// </summary>
    private async Task<(string? displayName, string? description)> ReadSidecarDisplayAsync(
        string dir, string name, CancellationToken ct)
    {
        var configPath = Path.Combine(dir, $"{name}.config.json");
        if (!File.Exists(configPath))
            return (null, null);

        try
        {
            var json = await File.ReadAllTextAsync(configPath, ct);
            if (string.IsNullOrWhiteSpace(json))
                return (null, null);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, null);

            string? displayName = null;
            string? description = null;
            if (root.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String)
                displayName = dn.GetString();
            if (root.TryGetProperty("description", out var de) && de.ValueKind == JsonValueKind.String)
                description = de.GetString();

            return (displayName, description);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[OnnxDiscovery] 讀取旁置設定 {Config} 失敗(略過顯示覆寫)", $"{name}.config.json");
            return (null, null);
        }
    }
}
