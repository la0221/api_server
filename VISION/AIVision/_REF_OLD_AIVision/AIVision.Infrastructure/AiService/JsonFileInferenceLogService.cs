using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVision.Infrastructure.AiService;

/// <summary>
/// JSON 檔案推論記錄服務實作。
/// </summary>
public sealed class JsonFileInferenceLogService : IInferenceLogService
{
    private readonly AinaviOptions _options;
    private readonly ILogger<JsonFileInferenceLogService> _logger;
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public JsonFileInferenceLogService(
        IOptions<AinaviOptions> options,
        ILogger<JsonFileInferenceLogService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task AppendLogAsync(PredictResult result, CancellationToken cancellationToken = default)
    {
        var path = _options.LogPath;
        var directory = Path.GetDirectoryName(path);
        
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        List<PredictResult> logs = new();
        
        if (File.Exists(path))
        {
            try
            {
                var text = await File.ReadAllTextAsync(path, cancellationToken);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var deserialized = JsonSerializer.Deserialize<List<PredictResult>>(text, _jsonOptions);
                    if (deserialized != null)
                    {
                        logs = deserialized;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "讀取推論記錄檔失敗，將建立新檔案: {Path}", path);
                logs = new List<PredictResult>();
            }
        }

        // 將新記錄插入到開頭
        logs.Insert(0, result);

        // 限制記錄數量（可選，避免檔案過大）
        const int maxLogs = 10000;
        if (logs.Count > maxLogs)
        {
            logs = logs.Take(maxLogs).ToList();
        }

        try
        {
            var json = JsonSerializer.Serialize(logs, _jsonOptions);
            await File.WriteAllTextAsync(path, json, cancellationToken);
            _logger.LogDebug("已寫入推論記錄: {ModelName}, {Port}, {PredClass}", 
                result.ModelName, result.Port, result.PredClass);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "寫入推論記錄檔失敗: {Path}", path);
            throw;
        }
    }

    public async Task<IReadOnlyList<PredictResult>> GetLogsAsync(CancellationToken cancellationToken = default)
    {
        var path = _options.LogPath;
        
        if (!File.Exists(path))
        {
            return Array.Empty<PredictResult>();
        }

        try
        {
            var text = await File.ReadAllTextAsync(path, cancellationToken);
            if (string.IsNullOrWhiteSpace(text))
            {
                return Array.Empty<PredictResult>();
            }

            var logs = JsonSerializer.Deserialize<List<PredictResult>>(text, _jsonOptions);
            return (IReadOnlyList<PredictResult>)(logs ?? new List<PredictResult>());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "讀取推論記錄檔失敗: {Path}", path);
            return Array.Empty<PredictResult>();
        }
    }
}

