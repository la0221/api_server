using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AIVision.Infrastructure.AiService;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AIVision.Presentation.Wpf.Services;

/// <summary>
/// AINAVI API 客戶端服務。
/// </summary>
public sealed class AinaviApiClient
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly ILogger<AinaviApiClient>? _logger;
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    // Workflow API 專用的 JSON 選項 - 使用 snake_case 命名以符合 EdgeHub API 要求
    private static readonly JsonSerializerOptions _workflowJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    public AinaviApiClient(IConfiguration configuration, ILogger<AinaviApiClient>? logger = null)
    {
        _httpClient = new HttpClient();
        _logger = logger;
        
        // 從配置讀取 API BaseUrl
        var apiSection = configuration.GetSection("Api");
        _baseUrl = apiSection.GetValue<string>("BaseUrl") ?? "http://localhost:5234";
        _baseUrl = _baseUrl.TrimEnd('/');
        
        _httpClient.Timeout = System.TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// 開啟/載入 AI 模型（直接呼叫 EdgeHub API）。
    /// </summary>
    /// <param name="edgeHubUrl">EdgeHub URL（例如 http://192.168.1.95:5001）</param>
    /// <param name="uuid">模型 UUID</param>
    /// <param name="modelPath">模型路徑</param>
    /// <param name="port">推論服務埠號</param>
    /// <returns>載入結果</returns>
    public async Task<ModelLoadResultDto> OpenModelAsync(
        string edgeHubUrl, 
        string uuid, 
        string modelPath, 
        int port)
    {
        try
        {
            var url = $"{edgeHubUrl.TrimEnd('/')}/services/inference?sync=true";
            
            // EdgeHub 要求的格式：JSON Array
            var payload = new[]
            {
                new
                {
                    uuid,
                    model_path = modelPath,
                    port
                }
            };

            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            _logger?.LogInformation(
                "呼叫 EdgeHub API: {Url}, UUID={Uuid}, Path={Path}, Port={Port}",
                url, uuid, modelPath, port);

            var response = await _httpClient.PostAsync(url, content);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogError(
                    "模型載入失敗: StatusCode={StatusCode}, Response={Response}",
                    (int)response.StatusCode, responseBody);
                
                return new ModelLoadResultDto(false, $"HTTP Error {(int)response.StatusCode}: {responseBody}");
            }

            // 解析 EdgeHub 回應格式
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            var status = root.TryGetProperty("status", out var statusEl) && statusEl.GetBoolean();

            if (!status)
            {
                var message = root.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : "Unknown error";
                _logger?.LogError("模型載入失敗: Status=false, Message={Message}", message);
                return new ModelLoadResultDto(false, message ?? "模型載入失敗");
            }

            // 檢查 data 陣列
            if (root.TryGetProperty("data", out var dataEl) && 
                dataEl.ValueKind == JsonValueKind.Array && 
                dataEl.GetArrayLength() > 0)
            {
                var firstItem = dataEl[0];
                var success = firstItem.TryGetProperty("success", out var successEl) && successEl.GetBoolean();
                var message = firstItem.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : "Unknown";
                
                string? modelName = null;
                if (firstItem.TryGetProperty("model_info", out var modelInfoEl))
                {
                    modelName = modelInfoEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                }

                if (success)
                {
                    var successMessage = modelName != null 
                        ? $"模型 {modelName} 已成功載入 (Port={port})"
                        : $"模型已成功載入 (Port={port})";
                    
                    _logger?.LogInformation("模型載入成功: {ModelName}, Port={Port}", modelName ?? modelPath, port);
                    return new ModelLoadResultDto(true, successMessage);
                }
                else
                {
                    _logger?.LogWarning("模型載入失敗: {ModelName}, Message={Message}", modelName ?? modelPath, message);
                    return new ModelLoadResultDto(false, message ?? "模型載入失敗");
                }
            }

            _logger?.LogWarning("回應格式異常");
            return new ModelLoadResultDto(false, "回應格式異常");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "呼叫 EdgeHub API 時發生異常");
            return new ModelLoadResultDto(false, $"Exception: {ex.Message}");
        }
    }

    /// <summary>
    /// 開啟/載入 Workflow（直接呼叫 EdgeHub API）。
    /// </summary>
    /// <param name="edgeHubUrl">EdgeHub URL（例如 http://192.168.1.95:5001）</param>
    /// <param name="workflowSettingPath">Workflow 設定檔路徑</param>
    /// <param name="workflowPort">Workflow 推論服務埠號</param>
    /// <returns>載入結果</returns>
    public async Task<ModelLoadResultDto> OpenWorkflowAsync(
        string edgeHubUrl,
        string workflowSettingPath,
        int workflowPort)
    {
        try
        {
            var url = $"{edgeHubUrl.TrimEnd('/')}/service/workflow?sync=true";

            // Workflow 設定檔需要讀取 JSON
            if (!File.Exists(workflowSettingPath))
            {
                return new ModelLoadResultDto(false, $"Workflow 設定檔不存在: {workflowSettingPath}");
            }

            var workflowJson = await File.ReadAllTextAsync(workflowSettingPath);
            using var workflowDoc = JsonDocument.Parse(workflowJson);

            // 建構 EdgeHub 要求的格式
            // 欄位名稱必須是 workflow_setting（snake_case）
            // 如果 workflow_setting.json 包含 workflow 屬性，需要提取它
            JsonElement workflowContent;
            if (workflowDoc.RootElement.TryGetProperty("workflow", out var innerWorkflow))
            {
                // workflow_setting.json 格式: { "workflow": { ... } }
                // 需要提取內部的 workflow 作為 workflow_setting
                workflowContent = innerWorkflow;
            }
            else
            {
                // workflow_setting.json 直接就是 workflow 內容
                workflowContent = workflowDoc.RootElement;
            }

            // 使用 Dictionary 來確保欄位名稱完全正確（避免被 camelCase 影響）
            var payload = new Dictionary<string, object>
            {
                ["port"] = workflowPort,
                ["workflow_setting"] = workflowContent
            };

            var json = JsonSerializer.Serialize(payload, _workflowJsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            _logger?.LogInformation(
                "呼叫 EdgeHub Workflow API: {Url}, SettingPath={Path}, Port={Port}",
                url, workflowSettingPath, workflowPort);

            var response = await _httpClient.PostAsync(url, content);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogError(
                    "Workflow 載入失敗: StatusCode={StatusCode}, Response={Response}",
                    (int)response.StatusCode, responseBody);

                return new ModelLoadResultDto(false, $"HTTP Error {(int)response.StatusCode}: {responseBody}");
            }

            // 解析回應
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            var status = root.TryGetProperty("status", out var statusEl) && statusEl.GetBoolean();

            if (!status)
            {
                var message = root.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : "Unknown error";
                _logger?.LogError("Workflow 載入失敗: Status=false, Message={Message}", message);
                return new ModelLoadResultDto(false, message ?? "Workflow 載入失敗");
            }

            _logger?.LogInformation("Workflow 載入成功: Port={Port}", workflowPort);
            return new ModelLoadResultDto(true, $"Workflow 已成功載入 (Port={workflowPort})");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "呼叫 EdgeHub Workflow API 時發生異常");
            return new ModelLoadResultDto(false, $"Exception: {ex.Message}");
        }
    }

    /// <summary>
    /// 關閉所有服務（直接呼叫 EdgeHub API）。
    /// DELETE /services
    /// </summary>
    /// <param name="edgeHubUrl">EdgeHub URL（例如 http://192.168.1.95:5001）</param>
    /// <returns>關閉結果</returns>
    public async Task<ModelLoadResultDto> CloseAllServicesAsync(string edgeHubUrl)
    {
        try
        {
            var url = $"{edgeHubUrl.TrimEnd('/')}/services";

            _logger?.LogInformation("呼叫 EdgeHub DELETE API: {Url}", url);

            var response = await _httpClient.DeleteAsync(url);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogError(
                    "關閉服務失敗: StatusCode={StatusCode}, Response={Response}",
                    (int)response.StatusCode, responseBody);

                return new ModelLoadResultDto(false, $"HTTP Error {(int)response.StatusCode}: {responseBody}");
            }

            // 解析回應
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            var status = root.TryGetProperty("status", out var statusEl) && statusEl.GetBoolean();
            var message = root.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : null;

            if (!status)
            {
                _logger?.LogWarning("關閉服務回應: Status=false, Message={Message}", message);
            }

            _logger?.LogInformation("服務已關閉: {Message}", message ?? "success");
            return new ModelLoadResultDto(true, message ?? "服務已關閉");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "呼叫 EdgeHub DELETE API 時發生異常");
            return new ModelLoadResultDto(false, $"Exception: {ex.Message}");
        }
    }

    /// <summary>
    /// 查詢 Workflow 服務狀態（直接呼叫 EdgeHub API）。
    /// GET /service?service_type=workflow
    /// </summary>
    /// <param name="edgeHubUrl">EdgeHub URL</param>
    /// <returns>服務狀態列表 JSON</returns>
    public async Task<string?> GetWorkflowServicesAsync(string edgeHubUrl)
    {
        try
        {
            var url = $"{edgeHubUrl.TrimEnd('/')}/service?service_type=workflow";

            _logger?.LogDebug("查詢 Workflow 服務: {Url}", url);

            var response = await _httpClient.GetAsync(url);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("查詢服務失敗: {StatusCode}", response.StatusCode);
                return null;
            }

            return responseBody;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "查詢 Workflow 服務時發生異常");
            return null;
        }
    }

    /// <summary>
    /// 開啟/載入 AI 模型（透過後端 API）。
    /// </summary>
    /// <param name="modelName">模型名稱</param>
    /// <param name="port">模型埠號</param>
    /// <returns>載入結果</returns>
    [Obsolete("請使用 OpenModelAsync(edgeHubUrl, uuid, modelPath, port) 直接呼叫 EdgeHub")]
    public async Task<ModelLoadResultDto> OpenModelAsync(string modelName, int port)
    {
        try
        {
            var url = $"{_baseUrl}/api/ainavi/open-model";
            var body = new { modelName, port };
            var json = JsonSerializer.Serialize(body, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");

            _logger?.LogDebug("開啟模型: {ModelName} on Port {Port}", modelName, port);

            var response = await _httpClient.PostAsync(url, content);
            response.EnsureSuccessStatusCode();

            var resultJson = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<ModelLoadResultDto>(resultJson, _jsonOptions);

            if (result == null)
            {
                throw new InvalidOperationException("無法解析回應內容");
            }

            _logger?.LogInformation("模型開啟成功: {ModelName}", modelName);
            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "開啟模型失敗: {ModelName}", modelName);
            throw;
        }
    }

    /// <summary>
    /// 執行圖片推論。
    /// </summary>
    /// <param name="port">模型埠號</param>
    /// <param name="filePath">圖片檔案路徑</param>
    /// <param name="modelName">模型名稱（可選）</param>
    /// <returns>推論結果</returns>
    public async Task<PredictResult> PredictAsync(int port, string filePath, string? modelName = null)
    {
        try
        {
            var url = $"{_baseUrl}/api/ainavi/predict?port={port}";
            if (!string.IsNullOrEmpty(modelName))
            {
                url += $"&modelName={Uri.EscapeDataString(modelName)}";
            }

            using var form = new MultipartFormDataContent();
            await using var fileStream = File.OpenRead(filePath);
            var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("image/jpeg");
            form.Add(fileContent, "img", Path.GetFileName(filePath));

            _logger?.LogDebug("發送推論請求: Port={Port}, File={FilePath}", port, filePath);

            var response = await _httpClient.PostAsync(url, form);
            response.EnsureSuccessStatusCode();

            var resultJson = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<PredictResult>(resultJson, _jsonOptions);

            if (result == null)
            {
                throw new InvalidOperationException("無法解析推論結果");
            }

            _logger?.LogInformation(
                "推論完成: Class={Class}, Confidence={Confidence:P2}",
                result.PredClass, result.Confidence ?? 0.0);

            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "推論失敗: Port={Port}, File={FilePath}", port, filePath);
            throw;
        }
    }

    /// <summary>
    /// 取得推論記錄列表。
    /// </summary>
    /// <returns>推論記錄列表</returns>
    public async Task<IReadOnlyList<PredictResult>> GetLogsAsync()
    {
        try
        {
            var url = $"{_baseUrl}/api/ainavi/logs";
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var logs = JsonSerializer.Deserialize<List<PredictResult>>(json, _jsonOptions);

            return (IReadOnlyList<PredictResult>)(logs ?? new List<PredictResult>());
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "取得推論記錄失敗");
            return Array.Empty<PredictResult>();
        }
    }
}

/// <summary>
/// 模型載入結果 DTO（用於 API 回應）。
/// </summary>
public sealed record ModelLoadResultDto(bool Success, string? Message);

