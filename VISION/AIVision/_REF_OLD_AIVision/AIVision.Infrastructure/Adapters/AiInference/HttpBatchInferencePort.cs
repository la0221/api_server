using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Ports.ImageBatch;
using AIVision.Domain.Shared;
using AIVision.Infrastructure.Configs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVision.Infrastructure.Adapters.AiInference;

/// <summary>
/// HTTP 批量推論實作（專用於批量推論功能）
/// 支援傳統 /inference 端點和 Workflow /workflow/run 端點
/// </summary>
public sealed class HttpBatchInferencePort : IAiInferencePort
{
    private readonly HttpClient _http;
    private readonly AiSettings _cfg;
    private readonly ILogger<HttpBatchInferencePort>? _logger;

    // 動態端點設定
    private string? _dynamicEndpointUrl;
    private bool _isWorkflowMode;

    public HttpBatchInferencePort(
        HttpClient http,
        IOptions<AiSettings> cfg,
        ILogger<HttpBatchInferencePort>? logger = null)
    {
        _http = http;
        _cfg = cfg.Value;
        _logger = logger;

        // 配置超時
        _http.Timeout = TimeSpan.FromSeconds(_cfg.Http.TimeoutSeconds);

        // BaseAddress 應已在 App.xaml.cs 中通過 AddHttpClient 配置
        // 這裡不應覆蓋已設置的 BaseAddress，僅作為備用設置
        if (_http.BaseAddress is null && !string.IsNullOrWhiteSpace(_cfg.BaseUrl))
        {
            // 確保 BaseUrl 包含 /inference 端點
            var baseUrl = _cfg.BaseUrl.TrimEnd('/');
            if (!baseUrl.EndsWith("/inference", StringComparison.OrdinalIgnoreCase))
            {
                baseUrl += "/inference";
            }
            _http.BaseAddress = new Uri(baseUrl);
        }

        _logger?.LogInformation("HttpBatchInferencePort 初始化完成 - BaseUrl: {BaseUrl}, Timeout: {Timeout}秒",
            _http.BaseAddress?.ToString() ?? _cfg.BaseUrl, _cfg.Http.TimeoutSeconds);
    }

    /// <summary>
    /// 設定為傳統單一模型模式
    /// </summary>
    /// <param name="host">主機 IP</param>
    /// <param name="port">推論服務埠號</param>
    public void SetSingleModelEndpoint(string host, int port)
    {
        _dynamicEndpointUrl = $"http://{host}:{port}/inference";
        _isWorkflowMode = false;
        _logger?.LogInformation("[HttpBatchInference] 設定為單一模型模式: {Url}", _dynamicEndpointUrl);
    }

    /// <summary>
    /// 設定為 Workflow 模式
    /// </summary>
    /// <param name="host">主機 IP</param>
    /// <param name="port">Workflow 推論服務埠號</param>
    public void SetWorkflowEndpoint(string host, int port)
    {
        _dynamicEndpointUrl = $"http://{host}:{port}/workflow/run";
        _isWorkflowMode = true;
        _logger?.LogInformation("[HttpBatchInference] 設定為 Workflow 模式: {Url}", _dynamicEndpointUrl);
    }

    /// <summary>
    /// 取得目前的推論端點 URL
    /// </summary>
    private string GetCurrentEndpointUrl()
    {
        if (!string.IsNullOrEmpty(_dynamicEndpointUrl))
        {
            return _dynamicEndpointUrl;
        }
        return _http.BaseAddress?.ToString() ?? _cfg.BaseUrl;
    }

    /// <summary>
    /// 是否為 Workflow 模式
    /// </summary>
    public bool IsWorkflowMode => _isWorkflowMode;

    /// <summary>
    /// 從文件路徑直接讀取原始圖片字節並執行推論（用於批量推論）
    /// </summary>
    public async Task<AiResult> PredictFromFileAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"圖片文件不存在: {filePath}");
        }

        _logger?.LogDebug("從文件讀取原始圖片字節: {FilePath}", filePath);
        
        // 直接讀取文件的原始字節（保留 JPEG 格式）
        var fileBytes = await File.ReadAllBytesAsync(filePath, ct);
        var fileName = Path.GetFileName(filePath);
        
        _logger?.LogDebug("文件讀取成功 - 文件名: {FileName}, 大小: {Size} bytes", fileName, fileBytes.Length);
        
        return await SendInferenceRequestAsync(fileBytes, fileName, ct);
    }

    public async Task<AiResult> PredictAsync(ImageData image, CancellationToken ct = default)
    {
        _logger?.LogDebug("開始推論請求 - 圖片大小: {Width}x{Height}, 資料大小: {Size} bytes", 
            image.Width, image.Height, image.Bytes.Length);

        // 注意：ImageData.Bytes 包含的是解碼後的像素數據，不是原始 JPEG 字節
        // 這會導致後端無法讀取圖片，因此應該使用 PredictFromFileAsync 方法
        _logger?.LogWarning("使用 ImageData.Bytes（像素數據）發送請求，這可能導致後端無法讀取圖片。建議使用 PredictFromFileAsync 方法。");
        
        var fileName = $"frame_{DateTime.UtcNow.Ticks}.jpg";
        return await SendInferenceRequestAsync(image.Bytes, fileName, ct);
    }

    private async Task<AiResult> SendInferenceRequestAsync(byte[] imageBytes, string fileName, CancellationToken ct)
    {
        // 建立 Multipart 表單
        using var content = new MultipartFormDataContent();

        // 添加圖片（使用原始字節）
        var bytes = new ByteArrayContent(imageBytes);
        bytes.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");

        // Workflow 模式使用 "img" 欄位名，單一模型模式使用配置的欄位名
        var formFieldName = _isWorkflowMode ? "img" : _cfg.Upload.FormFieldName;
        content.Add(bytes, formFieldName, fileName);

        _logger?.LogDebug("Multipart 表單已構建 - 欄位名: {FieldName}, 文件名: {FileName}, Content-Type: {ContentType}, 大小: {Size} bytes, Workflow模式: {IsWorkflow}",
            formFieldName, fileName, bytes.Headers.ContentType?.ToString(), imageBytes.Length, _isWorkflowMode);

        try
        {
            // 取得動態端點或預設端點
            var fullUrl = GetCurrentEndpointUrl();

            _logger?.LogInformation("發送 HTTP POST 請求到: {Url}, 欄位名: {FieldName}, 文件名: {FileName}, Workflow模式: {IsWorkflow}",
                fullUrl, formFieldName, fileName, _isWorkflowMode);

            // 發送請求
            using var resp = await _http.PostAsync(
                fullUrl,
                content,
                ct);

            _logger?.LogDebug("收到 HTTP 回應 - 狀態碼: {StatusCode}", resp.StatusCode);

            // 讀取回應內容（無論成功或失敗都需要讀取，以便記錄錯誤詳情）
            var json = await resp.Content.ReadAsStringAsync(ct);
            
            // 如果狀態碼不是成功，記錄錯誤詳情並拋出異常
            if (!resp.IsSuccessStatusCode)
            {
                _logger?.LogError("API 返回錯誤狀態碼 {StatusCode} - 回應內容: {Response}", 
                    resp.StatusCode, json.Length > 1000 ? json.Substring(0, 1000) + "..." : json);
                throw new HttpRequestException(
                    $"批量推論 API 返回錯誤狀態碼 {resp.StatusCode}。回應內容: {json}");
            }

            _logger?.LogDebug("API 回應 JSON (前500字元): {Json}", 
                json.Length > 500 ? json.Substring(0, 500) + "..." : json);

            // 解析 JSON
            using var doc = JsonDocument.Parse(json);
            
            var root = doc.RootElement;

            // 嘗試多種可能的回應結構
            string? predictedClass = null;
            double confidence = 0;
            List<SegMask>? segmentationMasks = null;

            // 嘗試結構 1: data.predictions (分類模型 - maxConfidence)
            if (root.TryGetProperty("data", out var dataProp) &&
                dataProp.TryGetProperty("predictions", out var predictionsProp))
            {
                var first = predictionsProp.EnumerateObject().First().Value;
                
                // 分類模型結構
                if (first.TryGetProperty("maxConfidence", out var maxConfProp))
                {
                    predictedClass = maxConfProp.GetProperty("class").GetString();
                    confidence = maxConfProp.GetProperty("confidence").GetDouble();
                    _logger?.LogDebug("解析為分類模型回應 (maxConfidence)");
                }
                // 分割模型結構: base64Image[]
                else if (first.TryGetProperty("base64Image", out var base64ArrayProp) &&
                         base64ArrayProp.ValueKind == JsonValueKind.Array)
                {
                    _logger?.LogDebug("解析為分割模型回應 (base64Image)");
                    segmentationMasks = new List<SegMask>();
                    
                    foreach (var maskItem in base64ArrayProp.EnumerateArray())
                    {
                        if (maskItem.TryGetProperty("class", out var classProp) &&
                            maskItem.TryGetProperty("mask", out var maskProp))
                        {
                            var className = classProp.GetString() ?? "unknown";
                            var maskBase64 = maskProp.GetString() ?? "";
                            
                            try
                            {
                                var maskBytes = Convert.FromBase64String(maskBase64);
                                segmentationMasks.Add(new SegMask
                                {
                                    ClassName = className,
                                    PngBytes = maskBytes
                                });
                                _logger?.LogDebug("解析分割遮罩: class={Class}, size={Size} bytes", 
                                    className, maskBytes.Length);
                            }
                            catch (FormatException ex)
                            {
                                _logger?.LogWarning(ex, "無法解碼 Base64 遮罩: class={Class}", className);
                            }
                        }
                    }
                    
                    if (segmentationMasks.Count > 0)
                    {
                        // 分割模式：使用第一個類別作為主要類別
                        predictedClass = segmentationMasks.First().ClassName;
                        confidence = 1.0; // 分割模式沒有信心分數，設為 1.0
                    }
                }
            }

            // 嘗試結構 2: data.result (Ecoco 模型返回結構)
            if (string.IsNullOrEmpty(predictedClass) &&
                root.TryGetProperty("data", out dataProp) &&
                dataProp.TryGetProperty("result", out var resultProp))
            {
                if (resultProp.TryGetProperty("predicted_class", out var classProp))
                {
                    predictedClass = classProp.GetString();
                }
                if (resultProp.TryGetProperty("confidence", out var confProp))
                {
                    confidence = confProp.GetDouble();
                }
                _logger?.LogDebug("解析為 Ecoco 模型回應 (data.result)");
            }

            // 嘗試結構 3: 直接包含 predicted_class 和 confidence
            if (string.IsNullOrEmpty(predictedClass) &&
                root.TryGetProperty("predicted_class", out var directClassProp))
            {
                predictedClass = directClassProp.GetString();
                if (root.TryGetProperty("confidence", out var directConfProp))
                {
                    confidence = directConfProp.GetDouble();
                }
                _logger?.LogDebug("解析為直接結構 (predicted_class)");
            }

            // 嘗試結構 4: Workflow 回應格式 (status + data 包含步驟結果)
            if (string.IsNullOrEmpty(predictedClass) && _isWorkflowMode)
            {
                var workflowResult = ParseWorkflowResponse(root, json);
                predictedClass = workflowResult.predClass;
                confidence = workflowResult.confidence;
                _logger?.LogDebug("解析為 Workflow 回應格式");
            }

            // 如果還是沒有找到，返回錯誤
            if (string.IsNullOrEmpty(predictedClass))
            {
                _logger?.LogError("無法從 API 回應中提取推論結果。完整回應: {Json}", json);
                throw new InvalidOperationException(
                    $"無法從 API 回應中提取推論結果。回應內容: {json}");
            }

            _logger?.LogInformation("推論成功 - 分類: {Class}, 信心分數: {Confidence}, 遮罩數量: {MaskCount}", 
                predictedClass, confidence, segmentationMasks?.Count ?? 0);

            return new AiResult
            {
                PredictedClass = predictedClass,
                Confidence = confidence,
                SegmentationMasks = segmentationMasks,
                RawJson = json
            };
        }
        catch (TaskCanceledException ex) when (ex.InnerException is System.Net.Sockets.SocketException)
        {
            _logger?.LogError(ex, "請求被取消或連接中斷 - BaseUrl: {BaseUrl}, 超時設定: {Timeout}秒, 錯誤訊息: {Message}", 
                _cfg.BaseUrl, _cfg.Http.TimeoutSeconds, ex.Message);
            throw new InvalidOperationException(
                $"批量推論 API 請求超時或連接中斷（超時設定: {_cfg.Http.TimeoutSeconds}秒）：{_cfg.BaseUrl}。請檢查網絡連接和後端服務狀態。", ex);
        }
        catch (TaskCanceledException ex)
        {
            _logger?.LogWarning(ex, "請求被取消 - BaseUrl: {BaseUrl}, 可能是用戶手動取消或超時", 
                _cfg.BaseUrl);
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogError(ex, "HTTP 請求失敗 - BaseUrl: {BaseUrl}, 錯誤訊息: {Message}, 內部例外: {InnerException}", 
                _cfg.BaseUrl, ex.Message, ex.InnerException?.Message);
            throw new InvalidOperationException(
                $"批量推論 API 連線失敗：{_cfg.BaseUrl}", ex);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "JSON 解析失敗 - 錯誤訊息: {Message}, 位置: {Path}", 
                ex.Message, ex.Path);
            throw new InvalidOperationException(
                "批量推論 API 回應 JSON 格式不符", ex);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "推論過程中發生未預期的錯誤 - 錯誤訊息: {Message}, 堆疊追蹤: {StackTrace}",
                ex.Message, ex.StackTrace);
            throw;
        }
    }

    /// <summary>
    /// 解析 Workflow API 回應格式
    /// </summary>
    private (string predClass, double confidence) ParseWorkflowResponse(JsonElement root, string rawJson)
    {
        var allDefects = new List<(string className, double confidence)>();
        int totalDefectCount = 0;

        // 檢查 status
        var status = root.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
        if (status != "success")
        {
            _logger?.LogWarning("[HttpBatchInference] Workflow 回應狀態不是 success: {Status}", status);
        }

        // 解析 data 物件
        if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Object)
        {
            _logger?.LogWarning("[HttpBatchInference] Workflow 回應中沒有 data 物件");
            return ("OK", 1.0);
        }

        // 遍歷每個 step 的結果
        foreach (var stepProp in dataEl.EnumerateObject())
        {
            var stepName = stepProp.Name;
            var stepResults = stepProp.Value;

            if (stepResults.ValueKind != JsonValueKind.Array)
                continue;

            _logger?.LogDebug("[HttpBatchInference] 處理 Workflow Step: {StepName}, 結果數量: {Count}",
                stepName, stepResults.GetArrayLength());

            // 判斷是物件偵測還是分割
            var isObjectDetection = stepName.Contains("objectdetection", StringComparison.OrdinalIgnoreCase);

            // 處理 step 結果，支援單層陣列 [{...}] 或雙層陣列 [[{...}]]
            foreach (var item in stepResults.EnumerateArray())
            {
                // 如果 item 是陣列（雙層結構），遍歷內層
                if (item.ValueKind == JsonValueKind.Array)
                {
                    foreach (var innerItem in item.EnumerateArray())
                    {
                        ProcessWorkflowResultItem(innerItem, isObjectDetection, allDefects, ref totalDefectCount);
                    }
                }
                // 如果 item 是物件（單層結構），直接處理
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    ProcessWorkflowResultItem(item, isObjectDetection, allDefects, ref totalDefectCount);
                }
            }
        }

        // 判定結果
        bool isOk = totalDefectCount == 0;
        string predClass = isOk ? "OK" : (allDefects.Count > 0 ? allDefects[0].className : "NG");
        double maxConfidence = allDefects.Count > 0 ? allDefects.Max(d => d.confidence) : 1.0;

        _logger?.LogInformation("[HttpBatchInference] Workflow 解析完成: 總缺陷數={DefectCount}, IsOk={IsOk}, PredClass={Class}",
            totalDefectCount, isOk, predClass);

        return (predClass, maxConfidence);
    }

    /// <summary>
    /// 處理 Workflow 結果項目，提取缺陷資訊
    /// </summary>
    private void ProcessWorkflowResultItem(
        JsonElement item,
        bool isObjectDetection,
        List<(string className, double confidence)> allDefects,
        ref int totalDefectCount)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return;

        // 取得類別名稱
        var className = item.TryGetProperty("class_name", out var classEl)
            ? classEl.GetString() ?? "unknown"
            : "unknown";

        // 取得信心分數
        var confValue = item.TryGetProperty("confidence", out var confEl)
            ? confEl.GetDouble()
            : 0.0;

        // 判斷是否為缺陷（物件偵測或有 tlbrwh 座標）
        if (isObjectDetection || item.TryGetProperty("tlbrwh", out _))
        {
            allDefects.Add((className, confValue));
            totalDefectCount++;

            _logger?.LogDebug("[HttpBatchInference] 偵測到缺陷: {Class}, Conf={Conf:P2}",
                className, confValue);
        }
    }
}

