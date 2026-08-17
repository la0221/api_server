using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Application.Ports.MoldCode;
using AIVision.Domain.MoldCode;
using AIVision.Domain.Shared;
using AIVision.MoldCode.Onnx;
using Microsoft.AspNetCore.Mvc;

namespace AIVision.Api.Controllers;

/// <summary>
/// 中央推論控制器（Path B / 協定演進階段 1）。
/// edge 送前處理圖 → server 跑雙 head warpPolar ONNX → 回讀值+信心。
/// 契約見 <c>.ai/designs/2026-07-14_api_infer_pair_contract.md</c>。
/// 鐵律：決策（三態/氣吹）不在此，永遠留 edge；本端點只回觀測。
/// </summary>
[ApiController]
[Route("api/infer")]
public sealed class InferController : ControllerBase
{
    private readonly IMoldCodePairRecognizerPort _recognizer;
    private readonly Services.ModelRegistryService _registry;
    private readonly ILogger<InferController> _logger;

    public InferController(
        IMoldCodePairRecognizerPort recognizer,
        Services.ModelRegistryService registry,
        ILogger<InferController> logger)
    {
        _recognizer = recognizer;
        _registry = registry;
        _logger = logger;
    }

    /// <summary>
    /// 健康檢查：不送圖、不跑推論的輕量探測，供 edge 判斷「server 是否可達 + 模型是否已載入」。
    /// **一律回 200**（含 degraded）——讓 edge 能分辨「連不上/逾時」（該降級）vs「活著但沒模型」（不該當故障）。
    /// </summary>
    [HttpGet("health")]
    [ProducesResponseType(typeof(InferHealthResponse), StatusCodes.Status200OK)]
    public IActionResult Health()
    {
        var sw = _recognizer as IMoldCodePairModelSwitch;
        var version = sw?.CurrentVersionName;
        var loaded = !string.IsNullOrWhiteSpace(version);

        return Ok(new InferHealthResponse
        {
            Status = loaded ? "ready" : "degraded",
            ModelLoaded = loaded,
            ModelVersion = version,
            MohaoClassCount = sw?.CurrentMohaoNames?.Count ?? 0,
            XuehaoClassCount = sw?.CurrentXuehaoNames?.Count ?? 0,
            ServerTimeUtc = DateTime.UtcNow,
        });
    }

    /// <summary>
    /// 雙 head 中央推論：收前處理圖，回 <see cref="PairObservation"/>（模號/穴號 + 各軸信心）。
    /// fail-closed：NO OBJECT / 辨識失敗都是正常 200（交 edge 判三態），只有請求壞掉/server 出錯才回 4xx/5xx。
    /// </summary>
    [HttpPost("pair")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(InferPairResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    public async Task<IActionResult> Pair(
        [FromForm] InferPairRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Image is null || request.Image.Length == 0)
            return Problem("缺少 image part 或內容為空。", statusCode: StatusCodes.Status400BadRequest);

        var format = (request.Format ?? string.Empty).Trim().ToLowerInvariant();

        // 讀 part → bytes。
        byte[] bytes;
        using (var ms = new System.IO.MemoryStream())
        {
            await request.Image.CopyToAsync(ms, cancellationToken);
            bytes = ms.ToArray();
        }

        // 依 format 還原 ImageData（修掉現況 ainavi/predict 寬高寫死 0 的坑）。
        ImageData image;
        switch (format)
        {
            case "png":
                // 禁有損格式（JPEG 會傷辨識準確度）；用 PNG 魔術位元組把關。
                if (!HasPngSignature(bytes))
                    return Problem("format=png 但內容不是 PNG（禁 JPEG 等有損格式，保準確度）。",
                        statusCode: StatusCodes.Status415UnsupportedMediaType);
                image = MoldCodeImageLoader.LoadFromBytes(bytes);
                break;

            case "raw":
                var rawError = ValidateRaw(request, bytes, out image);
                if (rawError is not null)
                    return Problem(rawError, statusCode: StatusCodes.Status400BadRequest);
                break;

            default:
                return Problem("format 必填，且需為 'png' 或 'raw'。",
                    statusCode: StatusCodes.Status400BadRequest);
        }

        if (image.Bytes.Length == 0)
            return Problem("影像解碼失敗（壞檔或無法辨識的內容）。",
                statusCode: StatusCodes.Status400BadRequest);

        // 選擇辨識器：未指定 modelVersion → baseline（現用）；有指定 → 登錄夾按版本取（獨立快取實例，
        // 隔離試模：不換掉 baseline、不影響其他站點請求；首次指定該版本要付 ~1s 冷載）。
        IMoldCodePairRecognizerPort recognizer = _recognizer;
        string? modelVersion;
        var requestedVersion = (request.ModelVersion ?? "").Trim();
        if (requestedVersion.Length > 0)
        {
            try
            {
                recognizer = _registry.GetOcrPairRecognizer(requestedVersion);
                modelVersion = requestedVersion;
            }
            catch (FileNotFoundException ex)
            {
                return Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[InferPair] 版本 {Version} 載入失敗", requestedVersion);
                return Problem($"版本 '{requestedVersion}' 載入失敗：{ex.Message}",
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        }
        else
        {
            modelVersion = _recognizer is IMoldCodePairModelSwitch sw0 ? sw0.CurrentVersionName : null;
        }

        // 推論（辨識器內部對 InferenceSession 已自帶鎖；放 Task.Run 免佔用請求執行緒過久）。
        var sw = Stopwatch.StartNew();
        PairObservation obs = await Task.Run(() => recognizer.Recognize(image), cancellationToken);
        sw.Stop();

        _logger.LogInformation(
            "[InferPair] present={Present} hasReading={HasReading} mohao={Mohao} xuehao={Xuehao} version={Version} {Elapsed}ms",
            obs.ObjectPresent, obs.HasReading, obs.Mohao, obs.Xuehao, modelVersion, sw.ElapsedMilliseconds);

        return Ok(new InferPairResponse
        {
            ObjectPresent = obs.ObjectPresent,
            Mohao = obs.Mohao,
            ConfMohao = obs.ConfMohao,
            Xuehao = obs.Xuehao,
            ConfXuehao = obs.ConfXuehao,
            HasReading = obs.HasReading,
            FailureReason = obs.FailureReason,
            ModelVersion = modelVersion,
            ElapsedMs = (int)sw.ElapsedMilliseconds,
            StationId = request.StationId,
        });
    }

    /// <summary>raw 格式：用帶入的 width/height/pixelFormat 組 ImageData，並檢查 buffer 長度相符。</summary>
    private static string? ValidateRaw(InferPairRequest request, byte[] bytes, out ImageData image)
    {
        image = default;
        if (request.Width is not int w || w <= 0 || request.Height is not int h || h <= 0)
            return "format=raw 需帶正整數 width 與 height。";

        var pixelFormat = (request.PixelFormat ?? string.Empty).Trim();
        int channels = pixelFormat.ToLowerInvariant() switch
        {
            "mono8" => 1,
            "bgr24" => 3,
            _ => 0,
        };
        if (channels == 0)
            return "format=raw 的 pixelFormat 需為 'Mono8' 或 'Bgr24'。";

        int stride = request.Stride is int s && s > 0 ? s : w * channels;
        long expected = (long)stride * h;

        // 精確比對（非 >=）：長度對不上代表「宣告的中繼與實際 buffer 不符」= 客戶端 bug。
        // 若只檢查 >=，錯報 pixelFormat/寬高 會讓 server 拿垃圾像素去跑 → Hough 找不到圓 →
        // 回 fail-closed 的「NO OBJECT」，與「真的沒鏡片」無法區分，把客戶端 bug 偽裝成有效觀測。
        // 依契約：請求壞掉一律 4xx，200 只留給有效觀測。
        if (bytes.LongLength != expected)
            return $"raw buffer 長度不符：需剛好 {expected} bytes（stride={stride} × height={h}），" +
                   $"實得 {bytes.LongLength}。請確認 width/height/pixelFormat/stride 與實際 buffer 一致。";

        image = new ImageData(bytes, w, h, pixelFormat, request.Stride is int st && st > 0 ? st : 0);
        return null;
    }

    private static bool HasPngSignature(byte[] b) =>
        b.Length >= 8 &&
        b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 &&
        b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A;
}

/// <summary>`GET /api/infer/health` 的回應（輕量探測，不含推論）。</summary>
public sealed class InferHealthResponse
{
    /// <summary>`ready`（模型已載入可推論）｜`degraded`（server 活著但模型未載入）。</summary>
    public string Status { get; set; } = "";

    /// <summary>雙 head 模型是否已載入。</summary>
    public bool ModelLoaded { get; set; }

    /// <summary>現用模型版本；未載入為 null。</summary>
    public string? ModelVersion { get; set; }

    /// <summary>模號 head 類別數（供 edge 對版健檢）。</summary>
    public int MohaoClassCount { get; set; }

    /// <summary>穴號 head 類別數。</summary>
    public int XuehaoClassCount { get; set; }

    /// <summary>server 當下 UTC 時間。</summary>
    public DateTime ServerTimeUtc { get; set; }
}

/// <summary>`POST /api/infer/pair` 的 multipart 請求。</summary>
public sealed class InferPairRequest
{
    /// <summary>前處理後的影像（png 編碼 或 raw 像素 buffer）。</summary>
    public IFormFile? Image { get; set; }

    /// <summary>影像格式：'png'（推薦，無損自帶尺寸）或 'raw'（原始 buffer，需帶尺寸）。</summary>
    public string? Format { get; set; }

    /// <summary>raw 必填：影像寬（像素）。</summary>
    public int? Width { get; set; }

    /// <summary>raw 必填：影像高（像素）。</summary>
    public int? Height { get; set; }

    /// <summary>raw 必填：'Mono8' 或 'Bgr24'。</summary>
    public string? PixelFormat { get; set; }

    /// <summary>選填：每行位元組數（含 padding）；省略 = 預設計算。</summary>
    public int? Stride { get; set; }

    /// <summary>選填：指定模型版本；省略 = server 現用版本。</summary>
    public string? ModelVersion { get; set; }

    /// <summary>
    /// 選填：站點識別（多站架構的「站點通知」，白板 2026-07-24；P-A 第一片）。
    /// 自由字串（如 "ST-01"）；server 原樣回聲，供 edge/日誌對應是哪一站的請求。
    /// </summary>
    public string? StationId { get; set; }
}

/// <summary>`POST /api/infer/pair` 的回應（對映 <see cref="PairObservation"/> + server 中繼）。</summary>
public sealed class InferPairResponse
{
    public bool ObjectPresent { get; set; }
    public string? Mohao { get; set; }
    public double ConfMohao { get; set; }
    public string? Xuehao { get; set; }
    public double ConfXuehao { get; set; }
    public bool HasReading { get; set; }
    public string? FailureReason { get; set; }

    /// <summary>server 實際使用的模型版本（供 edge 對版；未載入為 null）。</summary>
    public string? ModelVersion { get; set; }

    /// <summary>server 端推論耗時（毫秒，不含網路），供延遲量測。</summary>
    public int ElapsedMs { get; set; }

    /// <summary>站點識別回聲（請求帶什麼回什麼；未帶為 null）。P-A 第一片。</summary>
    public string? StationId { get; set; }
}
