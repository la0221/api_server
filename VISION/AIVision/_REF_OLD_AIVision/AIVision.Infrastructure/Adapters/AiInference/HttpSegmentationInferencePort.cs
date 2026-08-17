using System;
using System.Collections.Generic;
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
/// HTTP 分割推論實作
/// </summary>
public sealed class HttpSegmentationInferencePort : IAiInferencePort
{
    private readonly HttpClient _http;
    private readonly AiSettings _cfg;

    public HttpSegmentationInferencePort(
        HttpClient http,
        IOptions<AiSettings> cfg)
    {
        _http = http;
        _cfg = cfg.Value;
        _http.Timeout = TimeSpan.FromSeconds(_cfg.Http.TimeoutSeconds);

        if (_http.BaseAddress is null && !string.IsNullOrWhiteSpace(_cfg.BaseUrl))
        {
            _http.BaseAddress = new Uri(_cfg.BaseUrl);
        }
    }

    public async Task<AiResult> PredictAsync(ImageData image, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();

        var bytes = new ByteArrayContent(image.Bytes);
        bytes.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        content.Add(bytes, _cfg.Upload.FormFieldName, _cfg.Upload.FileNameParam);

        try
        {
            // 構建完整的推論端點 URL
            // 由於 BaseUrl 已包含完整路徑 (http://192.168.1.95:8001/inference)
            // 當端點為空時，直接使用相對路徑 "/"
            var endpoint = string.IsNullOrWhiteSpace(_cfg.Endpoints.Segmentation) 
                ? "/" 
                : _cfg.Endpoints.Segmentation;

            using var resp = await _http.PostAsync(
                endpoint,
                content,
                ct);

            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);

            // 獲取第一個檔案的 base64Image 陣列
            var predictions = doc.RootElement
                .GetProperty("data")
                .GetProperty("predictions");

            var first = predictions.EnumerateObject().First().Value;
            var arr = first.GetProperty("base64Image");

            // 解碼每個 mask
            var list = new List<SegMask>();

            foreach (var m in arr.EnumerateArray())
            {
                var cls = m.GetProperty("class").GetString() ?? "other";
                var b64 = m.GetProperty("mask").GetString() ?? "";

                var pngBytes = Convert.FromBase64String(b64);

                list.Add(new SegMask
                {
                    ClassName = cls,
                    PngBytes = pngBytes
                });
            }

            return new AiResult
            {
                SegmentationMasks = list,
                RawJson = json
            };
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"分割 API 連線失敗：{_cfg.BaseUrl}{_cfg.Endpoints.Segmentation}", ex);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "分割 API 回應 JSON 格式不符", ex);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "Mask Base64 解碼失敗", ex);
        }
    }
}

