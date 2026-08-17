using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVision.Infrastructure.MoldCode;

/// <summary>
/// CRNN 中央推論（<c>POST /api/infer/ocr_crnn</c>，server 端經 python sidecar）的 edge 客戶端。
/// 位址沿用 <see cref="InferenceServerOptions.BaseUrl"/>（「API 伺服器設定」切換即生效）。
/// <para>
/// ⚠ 與 <see cref="RemotePairRecognizer"/> 的差異：CRNN 端點 v1 **只收 PNG**（sidecar 走檔案路徑）；
/// 逾時要寬——server 首發請求會冷啟 python+torch（可達 20-90 秒），之後 ~100-150ms。
/// 引擎策略（2026-08-04 拍板）：CRNN 逐步取代雙 head，現階段並行。
/// </para>
/// </summary>
public sealed class CrnnInferClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>單張推論逾時：涵蓋 sidecar 冷啟（首張）；熱請求遠低於此。測試頁用，非生產節拍。</summary>
    private const int RecognizeTimeoutMs = 120_000;

    private readonly HttpClient _http;
    private readonly InferenceServerOptions _options;
    private readonly ILogger<CrnnInferClient>? _logger;

    public CrnnInferClient(
        HttpClient http,
        IOptions<InferenceServerOptions> options,
        ILogger<CrnnInferClient>? logger = null)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>最近一次呼叫是否為傳輸層失敗（連不上/逾時/5xx——含 sidecar 掛掉的 503）。</summary>
    public bool LastCallFailed { get; private set; }

    /// <summary>CRNN 健檢（GET /api/infer/ocr_crnn/health；不觸發 sidecar 啟動）。連不上回 null。</summary>
    public async Task<CrnnHealthDto?> CheckHealthAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(_options.HealthTimeoutMs, 3000)));
            using var resp = await _http.GetAsync(BuildUrl("api/infer/ocr_crnn/health"), cts.Token)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<CrnnHealthDto>(
                await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false), JsonOpts);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[CrnnClient] 健檢失敗: {BaseUrl}", _options.BaseUrl);
            return null;
        }
    }

    /// <summary>
    /// 送一張 PNG 做 CRNN 推論。傳輸層失敗（連不上/逾時/5xx）回 null 並設 <see cref="LastCallFailed"/>；
    /// 200 一律回物件（含「無鏡片/讀不到」的有效觀測——那不是故障）。
    /// <paramref name="modelVersion"/>：指定 server 登錄庫（ocr_crnn）版本做隔離試模（null/空=server 預設版）；
    /// 首次指定新版本 server 要冷啟該版行程（20-90s），屬正常。
    /// </summary>
    public async Task<CrnnInferDto?> RecognizeAsync(
        byte[] pngBytes, string? stationId = null, CancellationToken ct = default, string? modelVersion = null)
    {
        LastCallFailed = false;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(RecognizeTimeoutMs));

            using var form = new MultipartFormDataContent();
            var content = new ByteArrayContent(pngBytes);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
            form.Add(content, "image", "frame.png");
            form.Add(new StringContent("png"), "format");
            if (!string.IsNullOrWhiteSpace(stationId))
                form.Add(new StringContent(stationId), "stationId");
            if (!string.IsNullOrWhiteSpace(modelVersion))
                form.Add(new StringContent(modelVersion.Trim()), "modelVersion");

            using var resp = await _http.PostAsync(BuildUrl("api/infer/ocr_crnn"), form, cts.Token)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                LastCallFailed = true;
                _logger?.LogWarning("[CrnnClient] 推論非 2xx: {Status}", (int)resp.StatusCode);
                return null;
            }
            return JsonSerializer.Deserialize<CrnnInferDto>(
                await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false), JsonOpts);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LastCallFailed = true;
            _logger?.LogWarning("[CrnnClient] 推論逾時 (>{Timeout}ms)", RecognizeTimeoutMs);
            return null;
        }
        catch (Exception ex)
        {
            LastCallFailed = true;
            _logger?.LogWarning(ex, "[CrnnClient] 推論失敗: {BaseUrl}", _options.BaseUrl);
            return null;
        }
    }

    private string BuildUrl(string path)
    {
        var base_ = (_options.BaseUrl ?? "").TrimEnd('/');
        return $"{base_}/{path}";
    }
}

/// <summary><c>GET /api/infer/ocr_crnn/health</c> 的回應（多版本行程池版）。</summary>
public sealed class CrnnHealthDto
{
    public bool Enabled { get; set; }

    /// <summary>disabled｜cold（池空，首發請求會冷啟）｜ready。</summary>
    public string? Status { get; set; }

    /// <summary>請求未指定版本時 server 用的預設版本。</summary>
    public string? DefaultVersion { get; set; }

    /// <summary>行程池中各版本的狀態。</summary>
    public System.Collections.Generic.List<CrnnLoadedVersionDto> LoadedVersions { get; set; } = new();

    public string? Note { get; set; }
}

/// <summary>行程池中一個版本。</summary>
public sealed class CrnnLoadedVersionDto
{
    public string? Version { get; set; }
    public bool Ready { get; set; }
}

/// <summary><c>POST /api/infer/ocr_crnn</c> 的回應。</summary>
public sealed class CrnnInferDto
{
    public bool ObjectPresent { get; set; }
    public string? Mohao { get; set; }
    public double ConfMohao { get; set; }
    public string? Xuehao { get; set; }
    public double ConfXuehao { get; set; }
    public bool HasReading { get; set; }

    /// <summary>CRNN 特有：信心低於門檻＝建議人工複檢（無 NG 類，這是唯一品質旗標）。</summary>
    public bool NeedsReview { get; set; }

    public string? FailureReason { get; set; }
    public string? ModelVersion { get; set; }
    public string? Engine { get; set; }
    public int SidecarLatencyMs { get; set; }
    public int ElapsedMs { get; set; }
    public string? StationId { get; set; }
}
