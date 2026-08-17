using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AIVision.Application.Configuration;
using AIVision.Application.Ports.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVision.Infrastructure.AiService;

/// <summary>
/// AINAVI EdgeHub Workflow 服務管理實作
/// </summary>
public sealed class EdgeHubWorkflowService : IWorkflowService
{
    private readonly HttpClient _httpClient;
    private readonly WorkflowOptions _options;
    private readonly ILogger<EdgeHubWorkflowService> _logger;
    private bool _isRunning;
    private int? _currentPort;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public EdgeHubWorkflowService(
        HttpClient httpClient,
        IOptions<WorkflowOptions> options,
        ILogger<EdgeHubWorkflowService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _httpClient.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
    }

    public bool IsRunning => _isRunning;
    public int? CurrentPort => _currentPort;

    /// <summary>
    /// 啟動 Workflow 服務
    /// POST /service/workflow
    /// </summary>
    public async Task<WorkflowStartResult> StartAsync(CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("[Workflow] 開始啟動 Workflow 服務...");
            _logger.LogInformation("[Workflow] EdgeHub URL: {Url}", _options.GetEdgeHubUrl());
            _logger.LogInformation("[Workflow] Workflow Port: {Port}", _options.WorkflowPort);
            _logger.LogInformation("[Workflow] Setting Path: {Path}", _options.WorkflowSettingPath);

            // 讀取 workflow_setting.json
            if (string.IsNullOrEmpty(_options.WorkflowSettingPath))
            {
                _logger.LogError("[Workflow] WorkflowSettingPath 未設定");
                return new WorkflowStartResult(false, "WorkflowSettingPath 未設定");
            }

            if (!File.Exists(_options.WorkflowSettingPath))
            {
                _logger.LogError("[Workflow] workflow_setting.json 不存在: {Path}", _options.WorkflowSettingPath);
                return new WorkflowStartResult(false, $"workflow_setting.json 不存在: {_options.WorkflowSettingPath}");
            }

            var settingJson = await File.ReadAllTextAsync(_options.WorkflowSettingPath, ct);
            _logger.LogDebug("[Workflow] 讀取 workflow_setting.json 成功, 大小: {Size} bytes", settingJson.Length);

            // 解析 setting JSON
            using var settingDoc = JsonDocument.Parse(settingJson);

            // 構建請求 payload
            var payload = new
            {
                port = _options.WorkflowPort,
                workflow_setting = settingDoc.RootElement
            };

            var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
            var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

            _logger.LogInformation("[Workflow] 發送 POST 請求到: {Url}", _options.GetWorkflowServiceUrl());
            _logger.LogDebug("[Workflow] Request Body (前500字元): {Body}",
                payloadJson.Length > 500 ? payloadJson[..500] + "..." : payloadJson);

            var response = await _httpClient.PostAsync(_options.GetWorkflowServiceUrl(), content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            _logger.LogInformation("[Workflow] 回應狀態碼: {StatusCode}", response.StatusCode);
            _logger.LogDebug("[Workflow] Response Body: {Body}",
                responseBody.Length > 1000 ? responseBody[..1000] + "..." : responseBody);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("[Workflow] 啟動失敗: HTTP {StatusCode} - {Body}",
                    (int)response.StatusCode, responseBody);
                return new WorkflowStartResult(false, $"HTTP {(int)response.StatusCode}: {responseBody}");
            }

            // 解析回應
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            var status = root.TryGetProperty("status", out var statusEl) && statusEl.GetBoolean();

            if (status)
            {
                _isRunning = true;
                _currentPort = _options.WorkflowPort;
                _logger.LogInformation("[Workflow] ✅ Workflow 服務啟動成功! Port: {Port}", _currentPort);
                return new WorkflowStartResult(true, "Workflow 服務啟動成功", _currentPort);
            }
            else
            {
                var message = root.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : "未知錯誤";
                _logger.LogError("[Workflow] ❌ 啟動失敗: {Message}", message);
                return new WorkflowStartResult(false, message ?? "啟動失敗");
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "[Workflow] HTTP 請求失敗 - 無法連接到 EdgeHub: {Url}", _options.GetEdgeHubUrl());
            return new WorkflowStartResult(false, $"無法連接到 EdgeHub: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "[Workflow] 請求超時");
            return new WorkflowStartResult(false, "請求超時");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Workflow] 啟動時發生例外");
            return new WorkflowStartResult(false, $"例外: {ex.Message}");
        }
    }

    /// <summary>
    /// 載入 Workflow 服務（使用指定參數，供專案初始化使用）
    /// POST /service/workflow
    /// </summary>
    public async Task<bool> LoadWorkflowAsync(
        string edgeHubHost,
        int edgeHubPort,
        int workflowPort,
        string workflowSettingPath,
        CancellationToken ct = default)
    {
        try
        {
            var edgeHubUrl = $"http://{edgeHubHost}:{edgeHubPort}";
            var serviceUrl = $"{edgeHubUrl}/service/workflow?sync=true";

            _logger.LogInformation("[Workflow] 載入 Workflow 服務 (專案模式)...");
            _logger.LogInformation("[Workflow] EdgeHub URL: {Url}", edgeHubUrl);
            _logger.LogInformation("[Workflow] Service URL: {ServiceUrl}", serviceUrl);
            _logger.LogInformation("[Workflow] Workflow Port: {Port}", workflowPort);
            _logger.LogInformation("[Workflow] Setting Path: {Path}", workflowSettingPath);

            // 讀取 workflow_setting.json
            if (string.IsNullOrEmpty(workflowSettingPath))
            {
                _logger.LogError("[Workflow] workflowSettingPath 為空");
                return false;
            }

            if (!File.Exists(workflowSettingPath))
            {
                _logger.LogError("[Workflow] workflow_setting.json 不存在: {Path}", workflowSettingPath);
                return false;
            }

            var settingJson = await File.ReadAllTextAsync(workflowSettingPath, ct);
            _logger.LogDebug("[Workflow] 讀取 workflow_setting.json 成功, 大小: {Size} bytes", settingJson.Length);

            // 解析 setting JSON
            using var settingDoc = JsonDocument.Parse(settingJson);

            // 檢查是否有 workflow 屬性包裹
            JsonElement workflowContent;
            if (settingDoc.RootElement.TryGetProperty("workflow", out var innerWorkflow))
            {
                workflowContent = innerWorkflow;
                _logger.LogDebug("[Workflow] 從 workflow_setting.json 提取內部 workflow 屬性");
            }
            else
            {
                workflowContent = settingDoc.RootElement;
            }

            // 構建請求 payload（使用 Dictionary 確保欄位名稱正確）
            var payload = new Dictionary<string, object>
            {
                ["port"] = workflowPort,
                ["workflow_setting"] = workflowContent
            };

            var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
            var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

            _logger.LogInformation("[Workflow] 發送 POST 請求到: {Url}", serviceUrl);
            _logger.LogDebug("[Workflow] Request Body (前500字元): {Body}",
                payloadJson.Length > 500 ? payloadJson[..500] + "..." : payloadJson);

            var response = await _httpClient.PostAsync(serviceUrl, content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            _logger.LogInformation("[Workflow] 回應狀態碼: {StatusCode}", response.StatusCode);
            _logger.LogDebug("[Workflow] Response Body: {Body}",
                responseBody.Length > 1000 ? responseBody[..1000] + "..." : responseBody);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("[Workflow] 載入失敗: HTTP {StatusCode} - {Body}",
                    (int)response.StatusCode, responseBody);
                return false;
            }

            // 解析回應
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            var status = root.TryGetProperty("status", out var statusEl) && statusEl.GetBoolean();

            if (status)
            {
                _isRunning = true;
                _currentPort = workflowPort;
                _logger.LogInformation("[Workflow] ✅ Workflow 服務載入成功! Port: {Port}", _currentPort);
                return true;
            }
            else
            {
                var message = root.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : "未知錯誤";
                _logger.LogError("[Workflow] ❌ 載入失敗: {Message}", message);
                return false;
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "[Workflow] HTTP 請求失敗 - 無法連接到 EdgeHub");
            return false;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "[Workflow] 請求超時");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Workflow] 載入時發生例外");
            return false;
        }
    }

    /// <summary>
    /// 停止 Workflow 服務
    /// DELETE /services
    /// </summary>
    public async Task<bool> StopAsync(CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("[Workflow] 停止 Workflow 服務...");

            var response = await _httpClient.DeleteAsync(_options.GetServicesDeleteUrl(), ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            _logger.LogInformation("[Workflow] 停止回應: {StatusCode} - {Body}",
                response.StatusCode, responseBody);

            if (response.IsSuccessStatusCode)
            {
                _isRunning = false;
                _currentPort = null;
                _logger.LogInformation("[Workflow] ✅ Workflow 服務已停止");
                return true;
            }

            _logger.LogWarning("[Workflow] 停止服務回應非成功狀態: {StatusCode}", response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Workflow] 停止服務時發生例外");
            return false;
        }
    }

    /// <summary>
    /// 查詢 Workflow 服務狀態
    /// GET /service?service_type=workflow
    /// </summary>
    public async Task<WorkflowStatus> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            _logger.LogDebug("[Workflow] 查詢服務狀態...");

            var response = await _httpClient.GetAsync(_options.GetServiceQueryUrl(), ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            _logger.LogDebug("[Workflow] 狀態查詢回應: {StatusCode}", response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                return new WorkflowStatus(false, "UNKNOWN", null, $"HTTP {(int)response.StatusCode}");
            }

            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            // 解析回應: { status: true, data: [...] }
            if (root.TryGetProperty("data", out var dataEl) &&
                dataEl.ValueKind == JsonValueKind.Array &&
                dataEl.GetArrayLength() > 0)
            {
                var firstService = dataEl[0];
                var state = firstService.TryGetProperty("state", out var stateEl)
                    ? stateEl.GetString() ?? "UNKNOWN"
                    : "UNKNOWN";
                var port = firstService.TryGetProperty("port", out var portEl)
                    ? portEl.GetInt32()
                    : (int?)null;
                var error = firstService.TryGetProperty("error", out var errorEl)
                    ? errorEl.GetString()
                    : null;

                var isRunning = state == "RUNNING";
                _isRunning = isRunning;
                _currentPort = isRunning ? port : null;

                _logger.LogInformation("[Workflow] 服務狀態: {State}, Port: {Port}", state, port);
                return new WorkflowStatus(isRunning, state, port, error);
            }

            _logger.LogInformation("[Workflow] 無運行中的 Workflow 服務");
            _isRunning = false;
            _currentPort = null;
            return new WorkflowStatus(false, "NOT_RUNNING");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Workflow] 查詢狀態時發生例外");
            return new WorkflowStatus(false, "ERROR", null, ex.Message);
        }
    }

    /// <summary>
    /// 健康檢查 - 檢查 EdgeHub 是否可達
    /// </summary>
    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            _logger.LogDebug("[Workflow] 執行 EdgeHub 健康檢查: {Url}", _options.GetEdgeHubUrl());

            var response = await _httpClient.GetAsync(_options.GetEdgeHubUrl(), cts.Token);

            _logger.LogInformation("[Workflow] EdgeHub 健康檢查: {StatusCode}", response.StatusCode);
            return true; // 只要能連上就算健康
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Workflow] EdgeHub 健康檢查失敗");
            return false;
        }
    }
}
