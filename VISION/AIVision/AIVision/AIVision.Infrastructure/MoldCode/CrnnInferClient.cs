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

    /// <summary>最近一次呼叫是否失敗（連不上／逾時／任何非 2xx）。</summary>
    public bool LastCallFailed { get; private set; }

    /// <summary>
    /// 最近一次呼叫拿到的 HTTP 狀態碼；連不上／逾時（根本沒拿到回應）時為 <c>null</c>。
    /// <para>⚠ 呼叫端**必須**用它區分「對方掛了」與「我方送錯東西」：
    /// 4xx 是我方請求有問題（例如把 JPEG 標成 PNG 送過來 → 415），
    /// 把它當成「中央掛了」會觸發降級冷卻，讓後面一整批好圖被連坐
    /// （2026-08-24 UI 實測：一張裁不到圓的圖害後面 6 張全部沒送到中央）。</para>
    /// </summary>
    public int? LastStatusCode { get; private set; }

    /// <summary>最近一次失敗是不是「對方不可用」（連不上／逾時／5xx）。4xx 不算。</summary>
    public bool LastFailureWasServerSide =>
        LastCallFailed && (LastStatusCode is null || LastStatusCode >= 500);

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
    /// <param name="pieceId">單片識別碼（<c>{站號}_{yyyyMMdd}_{流水}</c>）。父端會回存進最近辨識紀錄，
    /// 兩邊 log 才對得起帳；沒有它就只能靠時間戳猜「父端這筆是站端哪一片」。</param>
    /// <param name="trigTick">站端的觸發時刻（TickCount64）。供事後算真實延遲。</param>
    public async Task<CrnnInferDto?> RecognizeAsync(
        byte[] pngBytes, string? stationId = null, CancellationToken ct = default, string? modelVersion = null,
        bool isStrip = false, string? rawPath = null, string? pieceId = null, long trigTick = 0)
    {
        LastCallFailed = false;
        LastStatusCode = null;
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
            // 站端已做完前處理時要標記，否則 server 會再做一次（對展開後的條圖找圓 → 誤判無鏡片）。
            if (isStrip)
                form.Add(new StringContent("true"), "isStrip");
            // 溯源：原圖留在站端，只把「它在哪」告訴父端，父端的最近紀錄才回得出這張圖的出處。
            // ⚠ 必須走 form 欄位不可走 HTTP header——header 只吃 latin-1，中文路徑會讓請求根本送不出去
            //   （POC 階段為此卡了一整天，症狀是封包從沒離開子機）。
            if (!string.IsNullOrWhiteSpace(rawPath))
                form.Add(new StringContent(rawPath), "rawPath");
            // 單片識別碼與觸發時刻：兩邊對帳的鑰匙。同樣走 form 欄位（header 只吃 latin-1）。
            if (!string.IsNullOrWhiteSpace(pieceId))
                form.Add(new StringContent(pieceId), "pieceId");
            if (trigTick > 0)
                form.Add(new StringContent(trigTick.ToString(System.Globalization.CultureInfo.InvariantCulture)), "trigTick");

            using var resp = await _http.PostAsync(BuildUrl("api/infer/ocr_crnn"), form, cts.Token)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                LastCallFailed = true;
                LastStatusCode = (int)resp.StatusCode;
                // 4xx 特別點出來：那是**我方送錯東西**，不是對方掛了。
                _logger?.LogWarning(
                    LastStatusCode is >= 400 and < 500
                        ? "[CrnnClient] 推論被拒 {Status}（4xx＝我方請求有問題，不是中央掛了）"
                        : "[CrnnClient] 推論非 2xx: {Status}",
                    LastStatusCode);
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

    /// <summary>父端**實際套用**的模號複檢門檻（隨模型版本的 _publish.json 走）。null＝沿用 sidecar 內建。</summary>
    /// <remarks>門檻回聲：判定標準改版時，事後要能證明「當時是用哪個門檻判的」。</remarks>
    public double? ReviewThresholdMohao { get; set; }

    /// <summary>父端實際套用的穴號複檢門檻。</summary>
    public double? ReviewThresholdXuehao { get; set; }

    public string? FailureReason { get; set; }
    public string? ModelVersion { get; set; }
    public string? Engine { get; set; }
    public int SidecarLatencyMs { get; set; }
    public int ElapsedMs { get; set; }
    public string? StationId { get; set; }

    /// <summary>父端回聲的單片識別碼（站端送什麼就回什麼），用來確認「這筆回應是哪一片的」。</summary>
    public string? PieceId { get; set; }
}
