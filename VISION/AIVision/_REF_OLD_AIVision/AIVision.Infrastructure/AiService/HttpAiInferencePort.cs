using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Configuration;
using AIVision.Application.Ports.Devices;
using AIVision.Domain.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVision.Infrastructure.AiService;

public sealed class HttpAiInferencePort : IAiInferencePort
{
    private readonly HttpClient _httpClient;
    private readonly IOptions<AiServiceOptions> _options;
    private readonly ILogger<HttpAiInferencePort> _logger;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public HttpAiInferencePort(
        HttpClient httpClient,
        IOptions<AiServiceOptions> options,
        ILogger<HttpAiInferencePort> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 檢查 AI 服務是否已設定且可用
    /// </summary>
    public bool IsConnected => _options.Value.IsHttpEnabled;

    public Task<Prediction> PredictAsync(ImageData image, CancellationToken cancellationToken)
    {
        var opts = _options.Value;
        if (!opts.IsHttpEnabled)
        {
            throw new InvalidOperationException("AI service is not configured for HTTP mode.");
        }

        var attempts = Math.Max(1, opts.RetryCount + 1);
        return ExecuteWithRetryAsync(attempts, opts, image, cancellationToken);
    }

    private async Task<Prediction> ExecuteWithRetryAsync(int attempts, AiServiceOptions opts, ImageData image, CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                return await SendInferenceAsync(opts, image, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt >= attempts)
                {
                    _logger.LogError(ex, "AI inference failed after {Attempts} attempts.", attempts);
                    throw;
                }

                _logger.LogWarning(ex, "AI inference attempt {Attempt} failed. Retrying...", attempt);
                var delayMs = Math.Min(1000, 200 * attempt);
                try
                {
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }
        }

        throw lastError ?? new InvalidOperationException("AI inference failed without specific error.");
    }

    private async Task<Prediction> SendInferenceAsync(AiServiceOptions opts, ImageData image, CancellationToken cancellationToken)
    {
        var requestDto = new AiInferenceRequestDto
        {
            ImageBase64 = Convert.ToBase64String(image.Bytes),
            Width = image.Width,
            Height = image.Height,
            PixelFormat = image.PixelFormat,
            Model = opts.Model,
            Metadata = BuildDefaultMetadata()
        };

        var request = new HttpRequestMessage(HttpMethod.Post, opts.PredictPath);
        request.Content = JsonContent.Create(requestDto, options: _serializerOptions);

        if (!string.IsNullOrWhiteSpace(opts.ApiKey))
        {
            var value = string.IsNullOrWhiteSpace(opts.ApiKeyPrefix)
                ? opts.ApiKey
                : $"{opts.ApiKeyPrefix}{opts.ApiKey}";
            request.Headers.TryAddWithoutValidation(opts.ApiKeyHeader, value);
        }

        _logger.LogDebug("Sending inference request to {Path}", request.RequestUri);

        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await SafeReadAsync(response, cancellationToken).ConfigureAwait(false);
            _logger.LogError(
                "AI inference request failed with status {StatusCode}. Response: {Body}",
                (int)response.StatusCode,
                errorBody);

            throw new InvalidOperationException($"AI inference failed with status code {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<AiInferenceResponseDto>(_serializerOptions, cancellationToken)
            .ConfigureAwait(false);
        if (payload is null)
        {
            throw new InvalidOperationException("AI inference response body is empty.");
        }

        var detections = payload.Detections?
            .Where(d => d.Box is not null)
            .Select(d =>
                new Detection(
                    d.Label,
                    new RectangleF(d.Box!.X, d.Box.Y, d.Box.Width, d.Box.Height),
                    d.Confidence))
            .ToList() ?? new List<Detection>();

        return new Prediction(
            payload.Label,
            payload.Confidence,
            payload.IsOk,
            payload.ModelVersion,
            payload.ImagePath,
            detections);
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return "<unable to read body>";
        }
    }

    private static Dictionary<string, string> BuildDefaultMetadata() =>
        new()
        {
            ["client"] = "AIVision",
            ["sentAt"] = DateTime.UtcNow.ToString("O")
        };

    /// <summary>
    /// 健康檢查：驗證 AI 服務是否可達且正常運作
    /// </summary>
    public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        var opts = _options.Value;
        if (!opts.IsHttpEnabled)
        {
            _logger.LogDebug("AI 服務未啟用 HTTP 模式，健康檢查跳過");
            return false;
        }

        try
        {
            // 使用較短的超時時間進行健康檢查
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            // 嘗試連接到健康檢查端點或根路徑
            var healthPath = !string.IsNullOrEmpty(opts.HealthCheckPath)
                ? opts.HealthCheckPath
                : "/";

            _logger.LogDebug("執行 AI 服務健康檢查: {Path}", healthPath);

            var response = await _httpClient.GetAsync(healthPath, cts.Token).ConfigureAwait(false);

            // 只要能連接就視為健康（不論回應狀態碼）
            _logger.LogInformation("AI 服務健康檢查成功: {Path} (StatusCode={StatusCode})",
                healthPath, (int)response.StatusCode);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // 外部取消，向上傳遞
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI 服務健康檢查失敗");
            return false;
        }
    }
}
