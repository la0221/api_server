using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace AIVision.Api.Controllers;

/// <summary>
/// CRNN（detector + Non-AR 字元式）中央推論——經 python sidecar 轉發
/// （設計 <c>2026-07-31_crnn_engine_intake.md</c> 路線 C）。
/// 與 <c>/api/infer/pair</c> 同精神：收前處理圖、回觀測、決策留 edge。
/// 差異：輸出是**字串**（open-vocab）、**無 NG 類**（不良品靠 needsReview 信心門檻）、固定單 pass。
/// v1 僅收 <c>format=png</c>（sidecar 走檔案路徑，raw 需先編碼——待需求再補）。
/// </summary>
[ApiController]
[Route("api/infer")]
public sealed class CrnnInferController : ControllerBase
{
    private readonly CrnnSidecarService _sidecar;
    private readonly ModelRegistryService _registry;
    private readonly RecentInferenceStore _recent;
    private readonly ReceivedImageStore _images;
    private readonly ILogger<CrnnInferController> _logger;

    public CrnnInferController(
        CrnnSidecarService sidecar,
        ModelRegistryService registry,
        RecentInferenceStore recent,
        ReceivedImageStore images,
        ILogger<CrnnInferController> logger)
    {
        _sidecar = sidecar;
        _registry = registry;
        _recent = recent;
        _images = images;
        _logger = logger;
    }

    /// <summary>CRNN 健檢：sidecar 是否啟用/預設版本/行程池中各版本狀態（不觸發啟動）。一律 200。</summary>
    [HttpGet("ocr_crnn/health")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public IActionResult Health()
    {
        var loaded = _sidecar.LoadedVersions;
        return Ok(new
        {
            Enabled = _sidecar.Enabled,
            Status = !_sidecar.Enabled ? "disabled" : (loaded.Count == 0 ? "cold" : "ready"),
            DefaultVersion = _sidecar.DefaultVersion,
            LoadedVersions = loaded.Select(v => new { v.Version, v.Ready, v.LastUsedUtc }),
            Note = loaded.Count == 0 && _sidecar.Enabled
                ? "行程池空；首發請求會冷啟該版本（torch 載入 20-90s）。請求帶 modelVersion 可直接指定登錄庫任一版本。"
                : null,
        });
    }

    /// <summary>
    /// CRNN 推論。fail-closed 語意同 /pair：讀不到/無鏡片是 200 有效觀測；sidecar 掛掉回 503。
    /// </summary>
    [HttpPost("ocr_crnn")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(InferCrnnResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Infer(
        [FromForm] InferCrnnRequest request, CancellationToken ct)
    {
        if (!_sidecar.Enabled)
            return Problem("CRNN sidecar 未啟用（appsettings CrnnSidecar:Enabled）。",
                statusCode: StatusCodes.Status503ServiceUnavailable);

        if (request.Image is null || request.Image.Length == 0)
            return Problem("缺少 image part 或內容為空。", statusCode: StatusCodes.Status400BadRequest);

        var format = (request.Format ?? "png").Trim().ToLowerInvariant();
        if (format != "png")
            return Problem("此端點 v1 僅支援 format=png（sidecar 走檔案路徑）。",
                statusCode: StatusCodes.Status400BadRequest);

        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            await request.Image.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }
        if (bytes.Length < 8 || bytes[0] != 0x89 || bytes[1] != 0x50)
            return Problem("format=png 但內容不是 PNG（禁 JPEG 等有損格式）。",
                statusCode: StatusCodes.Status415UnsupportedMediaType);

        var sw = Stopwatch.StartNew();
        var r = await _sidecar.RecognizeAsync(bytes, request.ModelVersion, ct, request.IsStrip ?? false);
        sw.Stop();

        if (!r.Ok)
        {
            // 版本不存在＝請求問題（404 明確訊息），不是 sidecar 故障。
            if (r.Error?.StartsWith("VERSION_NOT_FOUND:", StringComparison.Ordinal) == true)
                return Problem(r.Error["VERSION_NOT_FOUND:".Length..],
                    statusCode: StatusCodes.Status404NotFound);

            _logger.LogWarning("[InferCrnn] sidecar 失敗：{Error}", r.Error);
            // 失敗也要留痕：父端畫面看得到「有收到、但處理失敗」，才不會誤判成「站端根本沒送」。
            var failStation = string.IsNullOrWhiteSpace(request.StationId) ? "-" : request.StationId!;
            _recent.Add(new RecentInferenceEntry
            {
                Task = "ocr_crnn",
                StationId = failStation,
                Reading = "(推論失敗)",
                ReceivedBytes = bytes.Length,
                IsStrip = request.IsStrip ?? false,
                ElapsedMs = (int)sw.ElapsedMilliseconds,
                EdgeRawPath = request.RawPath,
                SavedImagePath = await _images.SaveAsync(bytes, failStation, ct).ConfigureAwait(false),
                Ok = false,
                Error = r.Error,
            });
            return Problem($"CRNN sidecar 失敗：{r.Error}",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        bool hasReading = r.Present &&
                          !string.IsNullOrWhiteSpace(r.Mohao) && r.Mohao != "?" &&
                          !string.IsNullOrWhiteSpace(r.Xuehao) && r.Xuehao != "?";

        // ②per-class 門檻（隨模型版控）：版本 _publish.json 有 judge 段 → 用它重算 needsReview
        //（覆蓋 sidecar 內建的固定門檻）；沒有 → 沿用 sidecar 的判定。門檻值一併回傳供 edge 對帳。
        var servedVersion = r.ModelVersion ?? _sidecar.DefaultVersion;
        bool needsReview = r.NeedsReview;
        double? thM = null, thX = null;
        if (_registry.GetPublishSection("ocr_crnn", servedVersion, "judge") is System.Text.Json.JsonElement judge)
        {
            if (judge.TryGetProperty("confMohao", out var jm) && jm.ValueKind == System.Text.Json.JsonValueKind.Number)
                thM = jm.GetDouble();
            if (judge.TryGetProperty("confXuehao", out var jx) && jx.ValueKind == System.Text.Json.JsonValueKind.Number)
                thX = jx.GetDouble();
            if (thM is not null || thX is not null)
                needsReview = !hasReading ||
                              (thM is double m && r.ConfMohao < m) ||
                              (thX is double x && r.ConfXuehao < x);
        }

        _logger.LogInformation(
            "[InferCrnn] present={Present} mohao={Mohao} xuehao={Xuehao} review={Review} {Elapsed}ms",
            r.Present, r.Mohao, r.Xuehao, r.NeedsReview, sw.ElapsedMilliseconds);

        // 父端監控的「最近辨識紀錄」資料來源（GET /api/infer/recent）——
        // 沒有這個，父端畫面就算真的收到圖也一片空白，現場無從確認。
        var station = string.IsNullOrWhiteSpace(request.StationId) ? "-" : request.StationId!;
        // 留存收到的影像——**預設關閉**（原圖本來就在站端），父端畫面可即時開關。
        var savedPath = await _images.SaveAsync(bytes, station, ct).ConfigureAwait(false);
        _recent.Add(new RecentInferenceEntry
        {
            Task = "ocr_crnn",
            StationId = station,
            Reading = hasReading
                ? $"{r.Mohao}/{r.Xuehao}"
                : (r.Present ? "(讀不到)" : "(無鏡片)"),
            HasReading = hasReading,
            NeedsReview = needsReview,
            ReceivedBytes = bytes.Length,
            IsStrip = request.IsStrip ?? false,
            ModelVersion = servedVersion,
            ElapsedMs = (int)sw.ElapsedMilliseconds,
            EngineMs = (int)r.LatencyMs,
            EdgeRawPath = request.RawPath,
            PieceId = request.PieceId,
            TrigTick = request.TrigTick ?? 0,
            SavedImagePath = savedPath,
            Ok = true,
        });

        return Ok(new InferCrnnResponse
        {
            ObjectPresent = r.Present,
            Mohao = hasReading ? r.Mohao : null,
            ConfMohao = r.ConfMohao,
            Xuehao = hasReading ? r.Xuehao : null,
            ConfXuehao = r.ConfXuehao,
            HasReading = hasReading,
            NeedsReview = needsReview,
            ReviewThresholdMohao = thM,
            ReviewThresholdXuehao = thX,
            FailureReason = hasReading ? null : (r.Present ? "CRNN 未讀出雙軸碼（fail-closed）" : "未偵測到鏡片"),
            ModelVersion = r.ModelVersion,
            Engine = "crnn",
            SidecarLatencyMs = (int)r.LatencyMs,
            ElapsedMs = (int)sw.ElapsedMilliseconds,
            StationId = request.StationId,
        });
    }
}

/// <summary><c>POST /api/infer/ocr_crnn</c> 的 multipart 請求。</summary>
public sealed class InferCrnnRequest
{
    /// <summary>前處理後（已裁鏡片區域）的 PNG。</summary>
    public Microsoft.AspNetCore.Http.IFormFile? Image { get; set; }

    /// <summary>v1 僅 'png'（預設）。</summary>
    public string? Format { get; set; }

    /// <summary>選填：指定登錄庫（ocr_crnn）版本；省略 = server 預設版本。多版本熱切換（AINavi 借鏡①）。</summary>
    public string? ModelVersion { get; set; }

    /// <summary>站點識別（原樣回聲）。</summary>
    public string? StationId { get; set; }

    /// <summary>
    /// 選填：影像是否**已由站端(edge)完成前處理**（展開好的 640 strip）。
    /// true = 父端只做辨識，不再找圓/展開——避免重複前處理導致誤判「未偵測到鏡片」。
    /// 省略/false = 沿用原行為（父端自行前處理）。
    /// </summary>
    public bool? IsStrip { get; set; }

    /// <summary>
    /// 選填：**原圖在站端的位置**（溯源用）。原圖不上傳，父端只記「這張的原圖在哪台的哪裡」。
    /// ⚠ 走 form 欄位不走 header：HTTP header 只吃 latin-1，中文路徑會讓請求整個送不出去。
    /// </summary>
    public string? RawPath { get; set; }

    /// <summary>站端的單片識別碼（<c>{站號}_{yyyyMMdd}_{流水}</c>）。兩邊 log 對帳的鑰匙。</summary>
    public string? PieceId { get; set; }

    /// <summary>站端的觸發時刻（TickCount64）。供事後算真實延遲。</summary>
    public long? TrigTick { get; set; }
}

/// <summary><c>POST /api/infer/ocr_crnn</c> 的回應。</summary>
public sealed class InferCrnnResponse
{
    public bool ObjectPresent { get; set; }
    public string? Mohao { get; set; }
    public double ConfMohao { get; set; }
    public string? Xuehao { get; set; }
    public double ConfXuehao { get; set; }
    public bool HasReading { get; set; }

    /// <summary>CRNN 特有：信心低於門檻＝建議人工複檢（無 NG 類，這是唯一的品質旗標）。
    /// 門檻來源：版本 _publish.json 的 judge 段（隨模型版控）優先；無則 sidecar 內建。</summary>
    public bool NeedsReview { get; set; }

    /// <summary>本次套用的模號複檢門檻（版本 judge 段；null=用 sidecar 內建）。</summary>
    public double? ReviewThresholdMohao { get; set; }

    /// <summary>本次套用的穴號複檢門檻。</summary>
    public double? ReviewThresholdXuehao { get; set; }

    public string? FailureReason { get; set; }
    public string? ModelVersion { get; set; }

    /// <summary>固定 "crnn"（供 edge 分辨引擎）。</summary>
    public string Engine { get; set; } = "crnn";

    /// <summary>sidecar 內部推論耗時（毫秒）。</summary>
    public int SidecarLatencyMs { get; set; }

    /// <summary>server 端整段耗時（毫秒，含暫存檔/協定往返）。</summary>
    public int ElapsedMs { get; set; }

    public string? StationId { get; set; }
}
