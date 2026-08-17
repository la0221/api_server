using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Ports.ImageBatch;
using AIVision.Domain.Shared;
using AIVision.Infrastructure.Configs;
using Microsoft.Extensions.Options;

namespace AIVision.Infrastructure.Adapters.AiInference;

/// <summary>
/// HTTP 分類推論實作
/// </summary>
public sealed class HttpClassificationInferencePort : IAiInferencePort
{
    private readonly HttpClient _http;
    private readonly AiSettings _cfg;

    public HttpClassificationInferencePort(
        HttpClient http,
        IOptions<AiSettings> cfg)
    {
        _http = http;
        _cfg = cfg.Value;

        // 配置超時
        _http.Timeout = TimeSpan.FromSeconds(_cfg.Http.TimeoutSeconds);

        // 配置基礎 URL
        if (_http.BaseAddress is null && !string.IsNullOrWhiteSpace(_cfg.BaseUrl))
        {
            _http.BaseAddress = new Uri(_cfg.BaseUrl);
        }
    }

    public async Task<AiResult> PredictAsync(ImageData image, CancellationToken ct = default)
    {
        // 建立 Multipart 表單
        using var content = new MultipartFormDataContent();

        // 添加圖片
        var bytes = new ByteArrayContent(image.Bytes);
        bytes.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        content.Add(bytes, _cfg.Upload.FormFieldName, _cfg.Upload.FileNameParam);

        try
        {
            // 構建完整的推論端點 URL
            // 由於 BaseUrl 已包含完整路徑 (http://192.168.1.95:8001/inference)
            // 當端點為空時，直接使用相對路徑 "/"
            var endpoint = string.IsNullOrWhiteSpace(_cfg.Endpoints.Classification) 
                ? "/" 
                : _cfg.Endpoints.Classification;

            // 發送請求
            using var resp = await _http.PostAsync(
                endpoint,
                content,
                ct);

            resp.EnsureSuccessStatusCode();

            // 讀取回應
            var json = await resp.Content.ReadAsStringAsync(ct);

            // 解析 JSON
            using var doc = JsonDocument.Parse(json);
            
            var root = doc.RootElement;

            // 檢查回應狀態
            if (!root.TryGetProperty("status", out var statusProp) || 
                statusProp.GetString() != "success")
            {
                throw new InvalidOperationException(
                    $"API 返回失敗狀態: {root.GetProperty("status").GetString()}");
            }

            // 嘗試多種可能的回應結構
            string? predictedClass = null;
            double confidence = 0;

            // 嘗試結構 1: data.predictions (原始預期結構)
            if (root.TryGetProperty("data", out var dataProp) &&
                dataProp.TryGetProperty("predictions", out var predictionsProp))
            {
                var first = predictionsProp.EnumerateObject().First().Value;
                if (first.TryGetProperty("maxConfidence", out var maxConfProp))
                {
                    predictedClass = maxConfProp.GetProperty("class").GetString();
                    confidence = maxConfProp.GetProperty("confidence").GetDouble();
                }
            }

            // 嘗試結構 2: data.result (檢查 Ecoco 模型返回結構)
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
            }

            // 如果還是沒有找到，返回錯誤
            if (string.IsNullOrEmpty(predictedClass))
            {
                throw new InvalidOperationException(
                    $"無法從 API 回應中提取推論結果。回應內容: {json}");
            }

            return new AiResult
            {
                PredictedClass = predictedClass,
                Confidence = confidence,
                RawJson = json
            };
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"分類 API 連線失敗：{_cfg.BaseUrl}{_cfg.Endpoints.Classification}", ex);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "分類 API 回應 JSON 格式不符", ex);
        }
    }
}

