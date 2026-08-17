using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Configuration;
using AIVision.Application.Ports.Devices;
using AIVision.Domain.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVision.Infrastructure.AiService;

/// <summary>
/// AINAVI EdgeHub 推論 Port 實作。
/// 使用 multipart/form-data 上傳圖片進行推論。
/// </summary>
public sealed class AinaviAiInferencePort : IAiInferencePort
{
    private readonly HttpClient _httpClient;
    private readonly AinaviOptions _options;
    private readonly ILogger<AinaviAiInferencePort> _logger;
    private string _currentModelHost;
    private int _currentModelPort;
    private bool _isConnected;

    public AinaviAiInferencePort(
        HttpClient httpClient,
        IOptions<AinaviOptions> options,
        ILogger<AinaviAiInferencePort> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        // 從配置中提取主機 IP（移除 http:// 前綴）
        var hostUri = new Uri(_options.Host);
        _currentModelHost = hostUri.Host;
        _currentModelPort = _options.DefaultModelPort;
        _isConnected = false;  // 初始為未連線，需透過健康檢查確認
    }

    /// <summary>
    /// 檢查 AI 服務是否已連線
    /// </summary>
    public bool IsConnected => _isConnected;

    /// <summary>
    /// 設定連線狀態（供外部設定）
    /// </summary>
    public void SetConnectionStatus(bool isConnected)
    {
        _isConnected = isConnected;
        _logger.LogDebug("AI 服務連線狀態: {Status}", isConnected ? "已連線" : "未連線");
    }

    /// <summary>
    /// 設定當前使用的模型埠號。
    /// </summary>
    /// <param name="port">模型埠號</param>
    public void SetModelPort(int port)
    {
        _currentModelPort = port;
        _logger.LogDebug("設定模型埠號: {Port}", port);
    }

    /// <summary>
    /// 設定當前使用的模型主機 IP 和埠號。
    /// </summary>
    /// <param name="host">主機 IP（例如 192.168.1.95）</param>
    /// <param name="port">推論埠號（例如 8001）</param>
    public void SetModelEndpoint(string host, int port)
    {
        _currentModelHost = host;
        _currentModelPort = port;
        _logger.LogDebug("設定模型端點: {Host}:{Port}", host, port);
    }

    public async Task<Prediction> PredictAsync(ImageData image, CancellationToken cancellationToken)
    {
        try
        {
            // 使用當前設定的主機和埠號構建推論 URL
            var url = $"http://{_currentModelHost}:{_currentModelPort}/inference";

            using var form = new MultipartFormDataContent();
            var stream = new MemoryStream(image.Bytes);
            stream.Position = 0; // 確保從頭開始讀取
            var fileContent = new StreamContent(stream);
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("image/jpeg");

            // AINAVI EdgeHub 要求的欄位名稱是 "img"
            var fileName = $"frame_{DateTime.UtcNow.Ticks}.jpg";
            form.Add(fileContent, "img", fileName);

            _logger.LogInformation(
                "發送推論請求: URL={Url}, Size={Size} bytes, Width={Width}, Height={Height}, Format={Format}",
                url, image.Bytes.Length, image.Width, image.Height, image.PixelFormat);

            var response = await _httpClient.PostAsync(url, form, cancellationToken);
            var rawJson = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "推論請求失敗: StatusCode={StatusCode}, Response={Response}",
                    (int)response.StatusCode, rawJson);

                throw new InvalidOperationException(
                    $"AI inference failed with status code {(int)response.StatusCode}. Response: {rawJson}");
            }

            // 記錄原始 JSON 回應（用於偵錯）
            _logger.LogDebug("API 回應 JSON (前500字元): {Json}",
                rawJson.Length > 500 ? rawJson.Substring(0, 500) + "..." : rawJson);

            // 解析 AINAVI EdgeHub 推論回應格式
            // 支援多種回應結構（參考批量推論的解析邏輯）
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            string? predClass = null;
            double? confidence = null;
            bool isOk = false;
            string modelVersion = "unknown";

            // 檢查 status
            var status = root.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
            if (status != "success")
            {
                _logger.LogWarning("推論回應狀態不是 success: {Status}", status);
            }

            // 嘗試結構 1: data.predictions.{filename}.maxConfidence (分類模型)
            if (root.TryGetProperty("data", out var dataEl))
            {
                // 取得模型資訊
                if (dataEl.TryGetProperty("info", out var infoEl))
                {
                    modelVersion = infoEl.TryGetProperty("version", out var verEl)
                        ? verEl.GetString() ?? "unknown"
                        : "unknown";
                }

                // 從 predictions 取得推論結果
                if (dataEl.TryGetProperty("predictions", out var predictionsEl) &&
                    predictionsEl.ValueKind == JsonValueKind.Object)
                {
                    // 取得第一個檔案的預測結果
                    foreach (var predictionEntry in predictionsEl.EnumerateObject())
                    {
                        var predData = predictionEntry.Value;

                        // 取得 maxConfidence
                        if (predData.TryGetProperty("maxConfidence", out var maxConfEl))
                        {
                            if (maxConfEl.TryGetProperty("class", out var classEl))
                            {
                                predClass = classEl.GetString();
                            }

                            if (maxConfEl.TryGetProperty("confidence", out var confEl))
                            {
                                if (confEl.ValueKind == JsonValueKind.Number)
                                {
                                    confidence = confEl.GetDouble();
                                }
                            }
                            _logger.LogDebug("解析為分類模型回應 (maxConfidence)");
                        }

                        // 只處理第一個檔案
                        break;
                    }
                }
            }

            // 嘗試結構 2: data.result.{predicted_class, confidence} (Ecoco 模型)
            if (string.IsNullOrEmpty(predClass) &&
                root.TryGetProperty("data", out dataEl) &&
                dataEl.TryGetProperty("result", out var resultProp))
            {
                if (resultProp.TryGetProperty("predicted_class", out var classProp))
                {
                    predClass = classProp.GetString();
                }
                if (resultProp.TryGetProperty("confidence", out var confProp))
                {
                    confidence = confProp.GetDouble();
                }
                _logger.LogDebug("解析為 Ecoco 模型回應 (data.result)");
            }

            // 嘗試結構 3: 直接包含 predicted_class 和 confidence
            if (string.IsNullOrEmpty(predClass) &&
                root.TryGetProperty("predicted_class", out var directClassProp))
            {
                predClass = directClassProp.GetString();
                if (root.TryGetProperty("confidence", out var directConfProp))
                {
                    confidence = directConfProp.GetDouble();
                }
                _logger.LogDebug("解析為直接結構 (predicted_class)");
            }

            // 如果還是無法解析，記錄完整 JSON
            if (string.IsNullOrEmpty(predClass))
            {
                _logger.LogWarning("無法從 API 回應中提取推論結果。完整回應: {Json}", rawJson);
            }

            // 判斷是否為 OK（根據 class 名稱，排除 NG 類別）
            if (!string.IsNullOrEmpty(predClass))
            {
                // 如果 class 是 "NG" 或包含 "NG"，視為失敗
                isOk = !string.Equals(predClass, "NG", StringComparison.OrdinalIgnoreCase) &&
                       !predClass.Contains("NG", StringComparison.OrdinalIgnoreCase);
            }
            else if (confidence.HasValue)
            {
                // 如果沒有 class，根據 confidence 判斷（>= 0.5 視為 OK）
                isOk = confidence.Value >= 0.5;
            }

            // 建立 Detection 列表（如果回應中有 detections）
            var detections = new List<Detection>();
            if (root.TryGetProperty("detections", out var detectionsElement) &&
                detectionsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var det in detectionsElement.EnumerateArray())
                {
                    var label = det.TryGetProperty("label", out var l) ? l.GetString() ?? "Unknown" : "Unknown";
                    var score = det.TryGetProperty("score", out var s) && s.TryGetDouble(out var sc) ? (float)sc : 0f;
                    
                    if (det.TryGetProperty("box", out var box))
                    {
                        var x = box.TryGetProperty("x", out var xEl) && xEl.TryGetSingle(out var xVal) ? xVal : 0f;
                        var y = box.TryGetProperty("y", out var yEl) && yEl.TryGetSingle(out var yVal) ? yVal : 0f;
                        var w = box.TryGetProperty("w", out var wEl) && wEl.TryGetSingle(out var wVal) ? wVal : 0f;
                        var h = box.TryGetProperty("h", out var hEl) && hEl.TryGetSingle(out var hVal) ? hVal : 0f;
                        
                        detections.Add(new Detection(label, new RectangleF(x, y, w, h), score));
                    }
                }
            }

            var prediction = new Prediction(
                predClass ?? "Unknown",
                (float)(confidence ?? 0.0),
                isOk,
                modelVersion,
                null,      // ImagePath
                detections);

            _logger.LogInformation(
                "推論完成: Class={Class}, Confidence={Confidence:P2}, IsOk={IsOk}",
                predClass, confidence ?? 0.0, isOk);

            return prediction;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "推論過程中發生異常");
            throw;
        }
    }

    /// <summary>
    /// 健康檢查：驗證 AI 服務是否可達且正常運作
    /// </summary>
    public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // 使用較短的超時時間進行健康檢查
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            // 嘗試連接到推論端點（使用 HEAD 或 GET 請求）
            var url = $"http://{_currentModelHost}:{_currentModelPort}/";

            _logger.LogDebug("執行 AI 服務健康檢查: {Url}", url);

            var response = await _httpClient.GetAsync(url, cts.Token);

            // 只要能連接就視為健康（不論回應狀態碼）
            _isConnected = true;
            _logger.LogInformation("AI 服務健康檢查成功: {Url} (StatusCode={StatusCode})",
                url, (int)response.StatusCode);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // 外部取消，向上傳遞
        }
        catch (Exception ex)
        {
            _isConnected = false;
            _logger.LogWarning(ex, "AI 服務健康檢查失敗: {Host}:{Port}",
                _currentModelHost, _currentModelPort);
            return false;
        }
    }
}

