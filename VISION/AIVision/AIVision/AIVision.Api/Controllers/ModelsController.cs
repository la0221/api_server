using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Api.Services;
using AIVision.Application.Ports.MoldCode;
using Microsoft.AspNetCore.Mvc;

namespace AIVision.Api.Controllers;

/// <summary>
/// 模型倉庫 API（ROADMAP 主項1：線上版本控管）——**按用途（task）**管理：
/// ocr_pair（模號穴號雙 head）/ gongmu（公母模）/ defect（瑕疵）。
/// <list type="bullet">
/// <item><c>GET /api/models</c>：用途總覽</item>
/// <item><c>GET /api/models/{task}</c>：某用途的版本清單（md5+溯源+標記）</item>
/// <item><c>GET /api/models/{task}/{version}/download?file=</c>：拉檔（edge 下載後必須 md5 複驗）</item>
/// <item><c>POST /api/models/{task}</c>：上架新版本（UI 發布頁走這；multipart 上傳＋server 端算 md5＋原子落地）</item>
/// </list>
/// 設計見 <c>.ai/designs/2026-07-31_model_release_and_trust.md</c>。
/// </summary>
[ApiController]
[Route("api/models")]
public sealed class ModelsController : ControllerBase
{
    private readonly ModelRegistryService _registry;
    private readonly IMoldCodePairRecognizerPort _baseline;
    private readonly CrnnSidecarService _crnn;
    private readonly ILogger<ModelsController> _logger;

    public ModelsController(
        ModelRegistryService registry,
        IMoldCodePairRecognizerPort baseline,
        CrnnSidecarService crnn,
        ILogger<ModelsController> logger)
    {
        _registry = registry;
        _baseline = baseline;
        _crnn = crnn;
        _logger = logger;
    }

    /// <summary>用途總覽：有哪些 task、各自的檔案組成與版本數。</summary>
    [HttpGet]
    [ProducesResponseType(typeof(TaskOverviewResponse), StatusCodes.Status200OK)]
    public IActionResult Overview()
    {
        var tasks = _registry.Tasks.Select(kv => new TaskOverviewEntry
        {
            Task = kv.Key,
            DisplayName = kv.Value.DisplayName,
            Files = kv.Value.Files,
            VersionCount = _registry.ListVersions(kv.Key).Count,
            InferReady = string.Equals(kv.Key, "ocr_pair", StringComparison.OrdinalIgnoreCase)
                         || (string.Equals(kv.Key, "ocr_crnn", StringComparison.OrdinalIgnoreCase) && _crnn.Enabled),
        }).ToList();
        return Ok(new TaskOverviewResponse { Tasks = tasks });
    }

    /// <summary>某用途的版本清單（含每檔 md5 供下載後複驗、_publish.json 溯源、現用/已載入標記）。</summary>
    [HttpGet("{task}")]
    [ProducesResponseType(typeof(ModelListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult List(string task)
    {
        if (!_registry.TaskExists(task))
            return Problem($"未知用途 '{task}'（可用：{string.Join("、", _registry.Tasks.Keys)}）。",
                statusCode: StatusCodes.Status404NotFound);

        // 現用/已載入標記僅 ocr_pair 有意義（其他用途還沒有推論端點）。
        bool isOcr = string.Equals(task, "ocr_pair", StringComparison.OrdinalIgnoreCase);
        var current = isOcr ? (_baseline as IMoldCodePairModelSwitch)?.CurrentVersionName : null;
        var cached = isOcr ? _registry.CachedOcrPairVersions : Array.Empty<string>();

        var versions = _registry.ListVersions(task).Select(v => new ModelListEntry
        {
            Version = v.Version,
            Published = v.Published,
            Files = v.Files.Select(f => new ModelFileEntry { Name = f.Name, Md5 = f.Md5, Bytes = f.Bytes }).ToList(),
            IsServerCurrent = string.Equals(v.Version, current, StringComparison.OrdinalIgnoreCase),
            IsLoadedInMemory = cached.Contains(v.Version, StringComparer.OrdinalIgnoreCase),
            Publish = v.Publish,
        }).ToList();

        return Ok(new ModelListResponse
        {
            Task = task,
            RegistryRoot = _registry.GetTask(task)!.Root,
            ServerCurrentVersion = current,
            Versions = versions,
        });
    }

    /// <summary>
    /// 下載某用途/版本的一個檔案。回應標頭 <c>X-Model-Md5</c>——edge 下載完**必須**重算比對（信任鏈時機 B）。
    /// </summary>
    [HttpGet("{task}/{version}/download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Download(string task, string version, [FromQuery] string? file)
    {
        if (!_registry.TaskExists(task))
            return Problem($"未知用途 '{task}'。", statusCode: StatusCodes.Status404NotFound);
        if (string.IsNullOrWhiteSpace(file))
            return Problem($"file 必填（{task} 的檔案：{string.Join("、", _registry.GetTask(task)!.Files)}）。",
                statusCode: StatusCodes.Status400BadRequest);

        var path = _registry.ResolveFile(task, version, file!);
        if (path is null)
            return Problem($"找不到 {task}/{version}/{file}。", statusCode: StatusCodes.Status404NotFound);

        var info = _registry.ListVersions(task).FirstOrDefault(
            v => string.Equals(v.Version, version, StringComparison.OrdinalIgnoreCase));
        var md5 = info?.Files.FirstOrDefault(
            f => string.Equals(f.Name, file, StringComparison.OrdinalIgnoreCase))?.Md5;
        if (md5 is not null)
            Response.Headers["X-Model-Md5"] = md5;
        Response.Headers["X-Model-Version"] = version;

        _logger.LogInformation("[Models] 下載 {Task}/{Version}/{File}（md5={Md5}）", task, version, file, md5 ?? "?");
        return PhysicalFile(path, "application/octet-stream", file);
    }

    /// <summary>
    /// 上架新版本（UI 發布頁；multipart：<c>version</c> + 該用途要求的全部檔案 + 選填 <c>sourceNote</c>）。
    /// server 端：檔名/數量對版 → 逐檔 .tmp 串流 + 算 md5 → 全數就緒才原子改名落地 → 寫 _publish.json。
    /// 版本已存在 → 409（版本不可變原則：要重發就換版本號，不做覆蓋）。
    /// </summary>
    [HttpPost("{task}")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(500_000_000)]
    [ProducesResponseType(typeof(ModelListEntry), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Publish(
        string task, [FromForm] PublishModelRequest request, CancellationToken ct)
    {
        var t = _registry.GetTask(task);
        if (t is null)
            return Problem($"未知用途 '{task}'（可用：{string.Join("、", _registry.Tasks.Keys)}）。",
                statusCode: StatusCodes.Status404NotFound);

        var version = (request.Version ?? "").Trim();
        if (!ModelRegistryService.IsSafeVersionName(version))
            return Problem("version 必填，且僅允許字母/數字開頭與 . _ - （防路徑跳脫）。",
                statusCode: StatusCodes.Status400BadRequest);

        // 檔案對版：上傳的檔名集合必須恰好等於該用途要求的組成。
        var uploaded = (request.Files ?? new List<IFormFile>())
            .Where(f => f is { Length: > 0 })
            .ToDictionary(f => f.FileName, StringComparer.OrdinalIgnoreCase);
        var missing = t.Files.Where(f => !uploaded.ContainsKey(f)).ToList();
        var extra = uploaded.Keys.Where(k => !t.Files.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        if (missing.Count > 0 || extra.Count > 0)
            return Problem(
                $"檔案組成不符：{task} 需要 [{string.Join("、", t.Files)}]" +
                (missing.Count > 0 ? $"；缺 [{string.Join("、", missing)}]" : "") +
                (extra.Count > 0 ? $"；多出 [{string.Join("、", extra)}]" : "") +
                "。請以正確檔名上傳（UI 發布頁會自動改名）。",
                statusCode: StatusCodes.Status400BadRequest);

        if (_registry.VersionExists(task, version))
            return Problem($"版本 '{version}' 已存在於 {task}。版本不可變：請換新版本號（如 {version}b）。",
                statusCode: StatusCodes.Status409Conflict);

        // ②judge（per-class 判定門檻）：JSON 物件、值必須是 0~1 的數字——判定規則跟模型一起版控。
        JsonElement? judge = null;
        if (!string.IsNullOrWhiteSpace(request.JudgeJson))
        {
            try
            {
                var j = JsonSerializer.Deserialize<JsonElement>(request.JudgeJson);
                if (j.ValueKind != JsonValueKind.Object)
                    throw new JsonException("judge 需為 JSON 物件");
                foreach (var p in j.EnumerateObject())
                    if (p.Value.ValueKind != JsonValueKind.Number ||
                        p.Value.GetDouble() is < 0 or > 1)
                        throw new JsonException($"judge.{p.Name} 需為 0~1 的數字");
                judge = j;
            }
            catch (Exception ex)
            {
                return Problem($"judge 門檻格式錯誤：{ex.Message}", statusCode: StatusCodes.Status400BadRequest);
            }
        }

        // ③preprocess（前處理參數）：必須能對映 WarpPolarParams 的鍵（打錯鍵名擋在發布，勿默默吞）。
        JsonElement? preprocess = null;
        if (!string.IsNullOrWhiteSpace(request.PreprocessJson))
        {
            try
            {
                _ = JsonSerializer.Deserialize<AIVision.MoldCode.Onnx.WarpPolarParams>(
                    request.PreprocessJson!, ModelRegistryService.PreprocessJsonOpts)
                    ?? throw new JsonException("preprocess 不可為 null");
                preprocess = JsonSerializer.Deserialize<JsonElement>(request.PreprocessJson!);
            }
            catch (Exception ex)
            {
                return Problem($"preprocess 參數格式錯誤（鍵名需為 WarpPolarParams 欄位）：{ex.Message}",
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }

        var destDir = Path.Combine(t.Root, version);
        try
        {
            Directory.CreateDirectory(destDir);

            // 逐檔落 .tmp + 算 md5；全部就緒才改名——中途失敗不留半套版本。
            var md5s = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in t.Files)
            {
                var tmp = Path.Combine(destDir, name + ".tmp");
                await using (var fs = System.IO.File.Create(tmp))
                    await uploaded[name].CopyToAsync(fs, ct);

                // 內容把關（依目標副檔名判定合法格式）：
                //   .onnx → 不可是 zip 容器（"PK"＝.pt 誤傳，推論會炸 InvalidProtobuf；2026-07-31 實案）
                //   .pt   → 必須是 zip 容器（torch 權重本體；ocr_crnn 用途收的就是 .pt）
                var magic = new byte[2];
                await using (var fs = System.IO.File.OpenRead(tmp))
                    _ = await fs.ReadAsync(magic.AsMemory(0, 2), ct);
                bool isZip = magic[0] == (byte)'P' && magic[1] == (byte)'K';
                bool wantOnnx = name.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase);
                bool wantPt = name.EndsWith(".pt", StringComparison.OrdinalIgnoreCase);
                if (wantOnnx && isZip)
                {
                    CleanupPartial(destDir);
                    return Problem(
                        $"{name} 不是 ONNX：內容是 zip 容器，極可能選到了 .pt 訓練檔。" +
                        @"請先轉檔：python D:\AIVisionModels\export_pt_to_onnx.py <best.pt>，再上傳產出的 .onnx。",
                        statusCode: StatusCodes.Status400BadRequest);
                }
                if (wantPt && !isZip)
                {
                    CleanupPartial(destDir);
                    return Problem(
                        $"{name} 不是 PyTorch 權重（.pt 應為 zip 容器）——此用途要的是訓練產出的 .pt 原檔，勿轉檔。",
                        statusCode: StatusCodes.Status400BadRequest);
                }

                md5s[name] = ModelRegistryService.ComputeMd5(tmp);
            }
            foreach (var name in t.Files)
                System.IO.File.Move(Path.Combine(destDir, name + ".tmp"), Path.Combine(destDir, name), overwrite: true);

            var publish = new Dictionary<string, object?>
            {
                ["version"] = version,
                ["task"] = task,
                ["published"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                ["publishedVia"] = "api-upload",
                ["sourceNote"] = string.IsNullOrWhiteSpace(request.SourceNote) ? null : request.SourceNote!.Trim(),
                ["files"] = t.Files.ToDictionary(n => n, n => (object)new { md5 = md5s[n] }),
            };
            if (judge is not null) publish["judge"] = judge;           // ②判定門檻隨模型版控
            if (preprocess is not null) publish["preprocess"] = preprocess;   // ③前處理隨模型版控
            await System.IO.File.WriteAllTextAsync(Path.Combine(destDir, "_publish.json"),
                JsonSerializer.Serialize(publish, new JsonSerializerOptions { WriteIndented = true }), ct);

            _logger.LogInformation("[Models] 已上架 {Task}/{Version}（{Files}）",
                task, version, string.Join(",", t.Files.Select(n => $"{n}:{md5s[n][..8]}")));

            var entry = new ModelListEntry
            {
                Version = version,
                Published = (string?)publish["published"],
                Files = t.Files.Select(n => new ModelFileEntry
                {
                    Name = n,
                    Md5 = md5s[n],
                    Bytes = new FileInfo(Path.Combine(destDir, n)).Length,
                }).ToList(),
            };
            return Created($"/api/models/{task}/{version}", entry);
        }
        catch (Exception ex)
        {
            CleanupPartial(destDir);
            _logger.LogError(ex, "[Models] 上架失敗: {Task}/{Version}", task, version);
            return Problem($"上架失敗：{ex.Message}", statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>清掉半成品（.tmp 與變成空的版本目錄），避免污染登錄夾。</summary>
    private static void CleanupPartial(string destDir)
    {
        try
        {
            if (!Directory.Exists(destDir)) return;
            foreach (var tmp in Directory.GetFiles(destDir, "*.tmp")) System.IO.File.Delete(tmp);
            if (Directory.GetFileSystemEntries(destDir).Length == 0)
                Directory.Delete(destDir);
        }
        catch { /* 清理失敗不掩蓋原始錯誤 */ }
    }
}

/// <summary>`POST /api/models/{task}` 的 multipart 請求。</summary>
public sealed class PublishModelRequest
{
    /// <summary>版本號（如 v6.8）。字母/數字開頭，允許 . _ -。</summary>
    public string? Version { get; set; }

    /// <summary>模型檔（檔名必須等於該用途的組成，如 mohao.onnx / xuehao.onnx）。</summary>
    public List<IFormFile>? Files { get; set; }

    /// <summary>選填：來源備註（訓練出處等，寫進 _publish.json 溯源）。</summary>
    public string? SourceNote { get; set; }

    /// <summary>選填：per-class 判定門檻 JSON 物件（如 {"confMohao":0.6,"confXuehao":0.85}；值 0~1）。
    /// 隨模型版控（_publish.json "judge" 段）——產線判定標準異動＝發新版本，不改程式。</summary>
    public string? JudgeJson { get; set; }

    /// <summary>選填：前處理參數 JSON 物件（鍵=WarpPolarParams 欄位）。隨模型版控（"preprocess" 段），
    /// ocr_pair 指定版本推論時用它建辨識器——前處理與模型同發布同回滾。</summary>
    public string? PreprocessJson { get; set; }
}

/// <summary>`GET /api/models` 的回應：用途總覽。</summary>
public sealed class TaskOverviewResponse
{
    public List<TaskOverviewEntry> Tasks { get; set; } = new();
}

public sealed class TaskOverviewEntry
{
    public string Task { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public List<string> Files { get; set; } = new();
    public int VersionCount { get; set; }

    /// <summary>此用途的推論端點是否已可用（目前僅 ocr_pair）。</summary>
    public bool InferReady { get; set; }
}

/// <summary>`GET /api/models/{task}` 的回應。</summary>
public sealed class ModelListResponse
{
    public string Task { get; set; } = "";
    public string RegistryRoot { get; set; } = "";

    /// <summary>server 現用（baseline）版本名；僅 ocr_pair 有意義。</summary>
    public string? ServerCurrentVersion { get; set; }

    public List<ModelListEntry> Versions { get; set; } = new();
}

/// <summary>單一版本項目。</summary>
public sealed class ModelListEntry
{
    public string Version { get; set; } = "";
    public string? Published { get; set; }
    public List<ModelFileEntry> Files { get; set; } = new();
    public bool IsServerCurrent { get; set; }
    public bool IsLoadedInMemory { get; set; }

    /// <summary>_publish.json 原文（溯源）；edge 下載時可原樣落地。</summary>
    public JsonElement? Publish { get; set; }
}

/// <summary>版本內單一檔案。</summary>
public sealed class ModelFileEntry
{
    public string Name { get; set; } = "";
    public string? Md5 { get; set; }
    public long Bytes { get; set; }
}
