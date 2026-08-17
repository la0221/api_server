using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AIVision.Application.Configuration;
using AIVision.Application.Contracts;
using AIVision.Application.Ports.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVision.Infrastructure.AiService;

/// <summary>
/// AINAVI EdgeHub 模型掛載 Port 實作。
/// </summary>
public sealed class AinaviAiModelPort : IAiModelPort
{
    private readonly HttpClient _httpClient;
    private readonly AinaviOptions _options;
    private readonly ILogger<AinaviAiModelPort> _logger;
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public AinaviAiModelPort(
        HttpClient httpClient,
        IOptions<AinaviOptions> options,
        ILogger<AinaviAiModelPort> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ModelLoadResult> LoadAsync(ModelLoadRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{_options.GetEdgeHubUrl()}/services/inference?sync=true";
            
            // AINAVI EdgeHub 要求的格式：JSON Array
            var payload = new[]
            {
                new
                {
                    uuid = request.ModelUuid,
                    model_path = request.ModelPath,
                    port = request.Port
                }
            };

            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            _logger.LogInformation(
                "載入模型: UUID={ModelUuid}, Path={ModelPath}, Port={Port}",
                request.ModelUuid, request.ModelPath, request.Port);

            var response = await _httpClient.PostAsync(url, content, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "模型載入失敗: StatusCode={StatusCode}, Response={Response}",
                    (int)response.StatusCode, responseBody);
                
                return new ModelLoadResult(false, $"HTTP Error {(int)response.StatusCode}: {responseBody}");
            }

            // 解析 AINAVI EdgeHub 回應格式
            // {status: true, data: [{uuid, port, success, message, model_info}]}
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;

                // 檢查 status
                var status = root.TryGetProperty("status", out var statusEl) && statusEl.GetBoolean();

                if (!status)
                {
                    var message = root.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : "Unknown error";
                    _logger.LogError("模型載入失敗: Status=false, Message={Message}", message);
                    return new ModelLoadResult(false, message ?? "模型載入失敗");
                }

                // 檢查 data 陣列的第一個元素
                if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array && dataEl.GetArrayLength() > 0)
                {
                    var firstItem = dataEl[0];
                    var success = firstItem.TryGetProperty("success", out var successEl) && successEl.GetBoolean();
                    var message = firstItem.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : "Unknown";
                    
                    // 取得模型資訊
                    string? modelName = null;
                    if (firstItem.TryGetProperty("model_info", out var modelInfoEl))
                    {
                        modelName = modelInfoEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                    }

                    if (success)
                    {
                        var successMessage = modelName != null 
                            ? $"模型 {modelName} 已成功載入 (Port={request.Port})"
                            : $"模型已成功載入 (Port={request.Port})";
                        
                        _logger.LogInformation(
                            "模型載入成功: Model={ModelName}, Port={Port}, Message={Message}",
                            modelName ?? request.ModelPath, request.Port, message);
                        
                        return new ModelLoadResult(true, successMessage);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "模型載入失敗: Model={ModelName}, Port={Port}, Message={Message}",
                            modelName ?? request.ModelPath, request.Port, message);
                        
                        return new ModelLoadResult(false, message ?? "模型載入失敗");
                    }
                }
                else
                {
                    _logger.LogWarning("回應格式異常，data 陣列為空或不存在");
                    return new ModelLoadResult(false, "回應格式異常");
                }
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "解析回應 JSON 失敗: {Response}", responseBody);
                return new ModelLoadResult(false, $"解析回應失敗: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "模型載入發生異常: {ModelPath}", request.ModelPath);
            return new ModelLoadResult(false, $"Exception: {ex.Message}");
        }
    }
}

