using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Ports.MoldCode;
using AIVision.Domain.MoldCode;
using AIVision.Domain.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVision.Infrastructure.MoldCode;

/// <summary>
/// 遠端（中央推論 server）雙 head 辨識器：把 <see cref="ImageData"/> 用 multipart 送到
/// <c>POST /api/infer/pair</c>，回 <see cref="PairObservation"/>。本地不跑 ONNX。
/// <para>模型倉庫（列版本/下載/上架）另見 <see cref="ModelHubClient"/>——本類別只管推論。</para>
/// <para>
/// 送圖走契約的 <c>format=raw</c>——edge 手上已是 <see cref="ImageData"/>（含寬高/像素格式），
/// 免一次 PNG 編碼，最省延遲。
/// </para>
/// <para>
/// ⚠️ fail-closed：連不上 / 逾時 / 非 2xx / 解析失敗一律回
/// <see cref="PairObservation.Failed"/>，**絕不回「看似合法」的碼**——由 edge 的
/// <c>MoldCodePairVerifier</c> 決定三態。決策永遠留 edge，server 只回觀測。
/// </para>
/// <para>
/// ⚠️ 注意「無物件 / 辨識不出」是 server 的**有效觀測（HTTP 200）**，不是故障：
/// 那種情況照實回傳，<see cref="LastCallFailed"/> 維持 false，**不應觸發降級**。
/// 只有傳輸層失敗（連不上/逾時/5xx）才算故障。
/// </para>
/// </summary>
public sealed class RemotePairRecognizer : IMoldCodePairRecognizerPort
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient _http;
    private readonly InferenceServerOptions _options;
    private readonly ILogger<RemotePairRecognizer>? _logger;

    public RemotePairRecognizer(
        HttpClient http,
        IOptions<InferenceServerOptions> options,
        ILogger<RemotePairRecognizer>? logger = null)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>最近一次呼叫是否為「傳輸層失敗」（連不上/逾時/5xx）。供來源選擇器判斷是否降級。</summary>
    public bool LastCallFailed { get; private set; }

    /// <summary>最近一次成功呼叫時 server 回報的模型版本。</summary>
    public string? LastModelVersion { get; private set; }

    /// <summary>最近一次成功呼叫的 server 端純推論耗時（毫秒，不含網路）。</summary>
    public int LastServerElapsedMs { get; private set; }

    /// <summary>
    /// 健康檢查：打 <c>GET /api/infer/health</c>。不送圖、不推論。
    /// server 回 degraded（活著但沒模型）也算「可達」，回傳物件讓呼叫端自行判讀。
    /// </summary>
    public async Task<InferHealthDto?> CheckHealthAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(_options.HealthTimeoutMs));

            using var resp = await _http.GetAsync(BuildUrl("api/infer/health"), cts.Token)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger?.LogWarning("[RemotePair] 健康檢查非 2xx: {Status}", (int)resp.StatusCode);
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return JsonSerializer.Deserialize<InferHealthDto>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[RemotePair] 健康檢查失敗: {BaseUrl}", _options.BaseUrl);
            return null;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// port 是同步簽章，故內部 block 等待。辨識已在背景執行緒被呼叫
    /// （<c>VerifyMoldCodePairCycleCommandHandler</c> / 驗收 UI 皆用 <c>Task.Run</c>），不會鎖 UI。
    /// 此為既有 port 簽章的限制，非本類別引入。
    /// </remarks>
    public PairObservation Recognize(ImageData image)
        => RecognizeAsync(image).GetAwaiter().GetResult();

    /// <summary>
    /// 非同步版本（驗收 UI / 未來 async port 可直接用，免 block）。
    /// <paramref name="modelVersion"/>：指定 server 端登錄夾版本做隔離試模（null/空 = server 現用 baseline）。
    /// 首次指定新版本時 server 要冷載（~1s），單張延遲會多一截，屬正常。
    /// </summary>
    public async Task<PairObservation> RecognizeAsync(
        ImageData image, CancellationToken ct = default, string? modelVersion = null)
    {
        LastCallFailed = false;

        if (image.Bytes is null || image.Bytes.Length == 0 || image.Width <= 0 || image.Height <= 0)
            return PairObservation.Failed("影像無效（空 bytes 或寬高 <= 0）");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(_options.TimeoutMs));

            using var form = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(image.Bytes);
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
            form.Add(fileContent, "image", "frame.raw");

            // 契約 format=raw：必須帶寬高/像素格式，server 才還原得出 ImageData。
            form.Add(new StringContent("raw"), "format");
            form.Add(new StringContent(image.Width.ToString()), "width");
            form.Add(new StringContent(image.Height.ToString()), "height");
            form.Add(new StringContent(image.PixelFormat ?? "Bgr24"), "pixelFormat");
            if (image.Stride > 0)
                form.Add(new StringContent(image.Stride.ToString()), "stride");
            if (!string.IsNullOrWhiteSpace(modelVersion))
                form.Add(new StringContent(modelVersion.Trim()), "modelVersion");

            using var resp = await _http.PostAsync(BuildUrl("api/infer/pair"), form, cts.Token)
                .ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                LastCallFailed = true;
                _logger?.LogWarning("[RemotePair] 推論非 2xx: {Status}", (int)resp.StatusCode);
                return PairObservation.Failed($"中央推論回應 HTTP {(int)resp.StatusCode}");
            }

            var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<InferPairDto>(json, JsonOpts);
            if (dto is null)
            {
                LastCallFailed = true;
                return PairObservation.Failed("中央推論回應無法解析");
            }

            LastModelVersion = dto.ModelVersion;
            LastServerElapsedMs = dto.ElapsedMs;

            // server 已用 fail-closed 語意回覆；照實轉成領域觀測（含「無物件/讀不到」）。
            // 這些是有效觀測、不是故障 → LastCallFailed 維持 false，不觸發降級。
            if (!dto.ObjectPresent)
                return PairObservation.NoObject(dto.FailureReason);

            if (!dto.HasReading || string.IsNullOrWhiteSpace(dto.Mohao) || string.IsNullOrWhiteSpace(dto.Xuehao))
                return PairObservation.Failed(dto.FailureReason ?? "中央推論未讀到雙軸碼");

            return PairObservation.Read(dto.Mohao!, dto.ConfMohao, dto.Xuehao!, dto.ConfXuehao);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LastCallFailed = true;
            _logger?.LogWarning("[RemotePair] 推論逾時 (>{Timeout}ms)", _options.TimeoutMs);
            return PairObservation.Failed($"中央推論逾時（>{_options.TimeoutMs}ms）");
        }
        catch (Exception ex)
        {
            LastCallFailed = true;
            _logger?.LogWarning(ex, "[RemotePair] 推論失敗: {BaseUrl}", _options.BaseUrl);
            return PairObservation.Failed($"中央推論失敗：{ex.Message}");
        }
    }

    private string BuildUrl(string path)
    {
        var base_ = (_options.BaseUrl ?? "").TrimEnd('/');
        return $"{base_}/{path}";
    }
}

/// <summary><c>GET /api/infer/health</c> 的回應。</summary>
public sealed class InferHealthDto
{
    public string? Status { get; set; }
    public bool ModelLoaded { get; set; }
    public string? ModelVersion { get; set; }
    public int MohaoClassCount { get; set; }
    public int XuehaoClassCount { get; set; }
    public DateTime ServerTimeUtc { get; set; }
}

/// <summary><c>POST /api/infer/pair</c> 的回應。</summary>
public sealed class InferPairDto
{
    public bool ObjectPresent { get; set; }
    public string? Mohao { get; set; }
    public double ConfMohao { get; set; }
    public string? Xuehao { get; set; }
    public double ConfXuehao { get; set; }
    public bool HasReading { get; set; }
    public string? FailureReason { get; set; }
    public string? ModelVersion { get; set; }
    public int ElapsedMs { get; set; }
}
