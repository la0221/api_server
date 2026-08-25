using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIVision.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace AIVision.Api.Controllers;

/// <summary>
/// 自我強化訓練（跑在中央推論機）。
///
/// <para><b>流程</b>：站端把混料圖（自帶正解的 <c>exp_X_got_Y_*.jpg</c>）上傳成資料集 →
/// 開一個 run 訓練 → 過驗證閘門才算候選 → <b>使用者按「上架」</b>才進既有的模型登錄庫
/// （<c>/api/models/{task}</c>，有 md5／溯源／版本不可變）。</para>
///
/// <list type="bullet">
/// <item><c>POST /api/training/datasets/{name}</c>：上傳資料集影像</item>
/// <item><c>GET  /api/training/datasets</c>：列資料集</item>
/// <item><c>POST /api/training/runs</c>：開始訓練</item>
/// <item><c>GET  /api/training/runs</c> / <c>{id}</c>：狀態、進度、執行紀錄</item>
/// <item><c>POST /api/training/runs/{id}/cancel</c>：取消</item>
/// <item><c>POST /api/training/runs/{id}/publish</c>：把通過的候選上架</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/training")]
public sealed class TrainingController : ControllerBase
{
    private static readonly string[] AllowedImageExt =
        { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" };

    private readonly TrainingService _training;
    private readonly ModelRegistryService _registry;
    private readonly ILogger<TrainingController> _logger;

    public TrainingController(
        TrainingService training,
        ModelRegistryService registry,
        ILogger<TrainingController> logger)
    {
        _training = training;
        _registry = registry;
        _logger = logger;
    }

    private IActionResult? DisabledGuard() => _training.Enabled
        ? null
        : Problem("訓練功能未啟用（appsettings Training:Enabled）。",
            statusCode: StatusCodes.Status503ServiceUnavailable);

    /// <summary>訓練功能的現況與設定（畫面用：能不能訓、閘門多少、入口在哪）。</summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(TrainingStatusDto), StatusCodes.Status200OK)]
    public IActionResult Status()
    {
        var o = _training.Options;
        return Ok(new TrainingStatusDto
        {
            Enabled = _training.Enabled,
            Busy = _training.IsBusy,
            DatasetRoot = o.DatasetRoot,
            OutputRoot = o.OutputRoot,
            MinImages = o.MinImages,
            Epochs = o.Epochs,
            Device = o.Device,
            CrnnEntryReady = !string.IsNullOrWhiteSpace(o.CrnnEntry) && System.IO.File.Exists(o.CrnnEntry),
            YoloEntryReady = !string.IsNullOrWhiteSpace(o.YoloEntry) && System.IO.File.Exists(o.YoloEntry),
            RehearsalReady = !string.IsNullOrWhiteSpace(o.CrnnRehearsalPath)
                             && Directory.Exists(o.CrnnRehearsalPath),
            CrnnMinSelectedAccuracy = o.CrnnMinSelectedAccuracy,
            CrnnMaxRehearsalRegression = o.CrnnMaxRehearsalRegression,
            YoloMinTargetRecall = o.YoloMinTargetRecall,
            YoloMaxFalsePositiveRate = o.YoloMaxFalsePositiveRate,
            Note = BuildStatusNote(o),
        });
    }

    private string? BuildStatusNote(TrainingOptions o)
    {
        if (!_training.Enabled) return "未啟用。";
        if (string.IsNullOrWhiteSpace(o.CrnnRehearsalPath) || !Directory.Exists(o.CrnnRehearsalPath))
            return "⚠ 尚未設定 CRNN rehearsal 排練集 → CRNN 訓練會被擋下。" +
                   "排練集是防「一批修正資料把舊標籤能力洗掉」的唯一防線，不能省。";
        return null;
    }

    /// <summary>列出已上傳的資料集。</summary>
    [HttpGet("datasets")]
    [ProducesResponseType(typeof(DatasetListDto), StatusCodes.Status200OK)]
    public IActionResult Datasets()
    {
        if (DisabledGuard() is { } d) return d;

        var root = _training.Options.DatasetRoot;
        var items = new List<DatasetDto>();
        if (Directory.Exists(root))
        {
            foreach (var dir in Directory.GetDirectories(root).OrderBy(x => x, StringComparer.Ordinal))
            {
                items.Add(new DatasetDto
                {
                    Name = Path.GetFileName(dir),
                    Path = dir,
                    ImageCount = _training.CountImages(dir),
                    UpdatedAt = Directory.GetLastWriteTime(dir),
                });
            }
        }
        return Ok(new DatasetListDto { Root = root, Datasets = items });
    }

    /// <summary>
    /// 上傳資料集影像（站端把 <c>_MISMATCH</c> 那批送上來）。可多次呼叫累加。
    /// <para><paramref name="label"/> 指定就存成 <c>{name}/{label}/檔名</c>——
    /// 分類訓練要靠資料夾名當標籤。</para>
    /// </summary>
    [HttpPost("datasets/{name}")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(2_000_000_000)]
    [ProducesResponseType(typeof(DatasetDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadDataset(
        string name, [FromForm] UploadDatasetRequest request, CancellationToken ct)
    {
        if (DisabledGuard() is { } d) return d;

        if (!ModelRegistryService.IsSafeVersionName(name))
            return Problem("資料集名稱僅允許字母/數字開頭與 . _ -（防路徑跳脫）。",
                statusCode: StatusCodes.Status400BadRequest);

        var label = (request?.Label ?? "").Trim();
        if (label.Length > 0 && !ModelRegistryService.IsSafeVersionName(label))
            return Problem("label 僅允許字母/數字開頭與 . _ -。",
                statusCode: StatusCodes.Status400BadRequest);

        var files = request?.Files?.Where(f => f is { Length: > 0 }).ToList() ?? new();
        if (files.Count == 0)
            return Problem("沒有收到任何檔案。", statusCode: StatusCodes.Status400BadRequest);

        var destDir = Path.Combine(_training.Options.DatasetRoot, name);
        if (label.Length > 0) destDir = Path.Combine(destDir, label);
        Directory.CreateDirectory(destDir);

        int saved = 0, skipped = 0;
        foreach (var f in files)
        {
            // 只收影像，而且只取檔名（不信任 client 給的路徑）。
            var fileName = Path.GetFileName(f.FileName);
            if (string.IsNullOrWhiteSpace(fileName)
                || !AllowedImageExt.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            var target = Path.Combine(destDir, fileName);
            // 同名不覆蓋：混料圖的檔名自帶正解與時間，撞名代表重傳，留舊的就好。
            if (System.IO.File.Exists(target)) { skipped++; continue; }

            await using var fs = System.IO.File.Create(target);
            await f.CopyToAsync(fs, ct);
            saved++;
        }

        var setDir = Path.Combine(_training.Options.DatasetRoot, name);
        _logger.LogInformation("[Training] 資料集 {Name} 收到 {Saved} 張（略過 {Skipped}）", name, saved, skipped);
        return Ok(new DatasetDto
        {
            Name = name,
            Path = setDir,
            ImageCount = _training.CountImages(setDir),
            UpdatedAt = DateTime.Now,
            Saved = saved,
            Skipped = skipped,
        });
    }

    /// <summary>開始一次訓練。</summary>
    [HttpPost("runs")]
    [ProducesResponseType(typeof(TrainingRunDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult StartRun([FromBody] StartTrainingRequest request)
    {
        if (DisabledGuard() is { } d) return d;

        var task = (request?.Task ?? "").Trim().ToLowerInvariant();
        var head = (request?.Head ?? "").Trim().ToLowerInvariant();
        var runName = (request?.RunName ?? "").Trim();
        var datasetName = (request?.Dataset ?? "").Trim();

        // dataset 可以給「已上傳的資料集名稱」或 server 上的絕對路徑（現場既有資料夾）。
        string datasetPath;
        if (Path.IsPathRooted(datasetName))
        {
            datasetPath = datasetName;
        }
        else if (ModelRegistryService.IsSafeVersionName(datasetName))
        {
            datasetPath = Path.Combine(_training.Options.DatasetRoot, datasetName);
        }
        else
        {
            return Problem("dataset 必填：已上傳的資料集名稱，或 server 上的絕對路徑。",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var errors = _training.Validate(task, head, datasetPath, runName);
        if (errors.Count > 0)
            return Problem(string.Join("\n", errors), statusCode: StatusCodes.Status400BadRequest);

        var run = _training.Start(task, head, datasetPath, runName, request?.Notes ?? "");
        return Ok(ToDto(run, 50));
    }

    /// <summary>列出所有 run（新→舊）。</summary>
    [HttpGet("runs")]
    [ProducesResponseType(typeof(TrainingRunListDto), StatusCodes.Status200OK)]
    public IActionResult Runs()
    {
        if (DisabledGuard() is { } d) return d;
        return Ok(new TrainingRunListDto
        {
            Busy = _training.IsBusy,
            Runs = _training.ListRuns().Select(r => ToDto(r, 0)).ToList(),
        });
    }

    /// <summary>單一 run 的狀態＋執行紀錄尾巴。</summary>
    [HttpGet("runs/{id}")]
    [ProducesResponseType(typeof(TrainingRunDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Run(string id, [FromQuery] int logLines = 200)
    {
        if (DisabledGuard() is { } d) return d;
        var run = _training.GetRun(id);
        if (run is null)
            return Problem($"找不到 run '{id}'。", statusCode: StatusCodes.Status404NotFound);
        return Ok(ToDto(run, logLines));
    }

    /// <summary>取消正在跑的 run。</summary>
    [HttpPost("runs/{id}/cancel")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult CancelRun(string id)
    {
        if (DisabledGuard() is { } d) return d;
        if (_training.GetRun(id) is null)
            return Problem($"找不到 run '{id}'。", statusCode: StatusCodes.Status404NotFound);
        var ok = _training.Cancel(id);
        return Ok(new { Cancelled = ok, Message = ok ? "已要求取消。" : "這個 run 已經不在執行中。" });
    }

    /// <summary>
    /// 把**通過驗證**的候選上架到模型登錄庫。
    ///
    /// <para>⚠ 這一步刻意要人按：訓練成功 ≠ 自動上線。上架後仍要在模型池按「設為現用」才會真的用它。</para>
    /// <para>上架不覆蓋既有版本——版本不可變，撞名直接擋。</para>
    /// </summary>
    [HttpPost("runs/{id}/publish")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public IActionResult PublishRun(string id, [FromBody] PublishRunRequest? request)
    {
        if (DisabledGuard() is { } d) return d;

        var run = _training.GetRun(id);
        if (run is null)
            return Problem($"找不到 run '{id}'。", statusCode: StatusCodes.Status404NotFound);

        if (run.State != TrainingRunState.Passed)
            return Problem(
                $"這個 run 的狀態是 {run.State}，不能上架。只有**通過驗證閘門**的候選可以上架。" +
                (run.State == TrainingRunState.Failed ? $"　未通過原因：{run.Message}" : ""),
                statusCode: StatusCodes.Status400BadRequest);

        if (run.Published)
            return Problem($"這個 run 已經上架過（版本 {run.PublishedVersion}）。",
                statusCode: StatusCodes.Status409Conflict);

        if (string.IsNullOrWhiteSpace(run.WeightPath) || !System.IO.File.Exists(run.WeightPath))
            return Problem($"找不到訓練產出的權重檔：{run.WeightPath ?? "(未回報)"}",
                statusCode: StatusCodes.Status400BadRequest);

        var version = (request?.Version ?? run.Id).Trim();
        if (!ModelRegistryService.IsSafeVersionName(version))
            return Problem("版本名僅允許字母/數字開頭與 . _ -。",
                statusCode: StatusCodes.Status400BadRequest);

        var task = _registry.GetTask(run.Task);
        if (task is null)
            return Problem($"登錄庫沒有用途 '{run.Task}'。", statusCode: StatusCodes.Status400BadRequest);

        if (_registry.VersionExists(run.Task, version))
            return Problem($"版本 '{version}' 已存在於 {run.Task}。版本不可變：請換一個版本號。",
                statusCode: StatusCodes.Status409Conflict);

        try
        {
            var result = PublishWeights(run, task, version);
            run.Published = true;
            run.PublishedVersion = version;
            run.AppendLog($"已上架到登錄庫：{run.Task}/{version}");
            _logger.LogInformation("[Training] run {Id} 上架為 {Task}/{Version}", run.Id, run.Task, version);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Training] run {Id} 上架失敗", run.Id);
            return Problem($"上架失敗：{ex.Message}", statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// 把權重複製進登錄夾並寫 <c>_publish.json</c>（格式與 <c>POST /api/models/{task}</c> 一致，
    /// 這樣模型池／下載／md5 複驗全部沿用既有機制）。
    /// </summary>
    private object PublishWeights(TrainingRun run, ModelTaskOptions task, string version)
    {
        var destDir = Path.Combine(task.Root, version);
        Directory.CreateDirectory(destDir);

        var md5s = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var isMohao = run.Head == "mohao";

        foreach (var name in task.Files)
        {
            // 訓練只產出「這一個 head」的新權重；同組其他檔案沿用現有的
            // ——這正是相機版說的「特殊版本：該 head 用新權重、另一 head 沿用現有」。
            string source;
            if (IsTrainedFile(run, name, isMohao))
            {
                source = run.WeightPath!;
            }
            else
            {
                source = ResolveCompanionFile(run, name, isMohao);
                if (string.IsNullOrWhiteSpace(source) || !System.IO.File.Exists(source))
                    throw new FileNotFoundException(
                        $"版本需要 {name}，但找不到可沿用的現有檔案。" +
                        "請確認 Training:YoloMohaoWeights / YoloXuehaoWeights 已設定。");
            }

            var target = Path.Combine(destDir, name);
            System.IO.File.Copy(source, target, overwrite: false);
            md5s[name] = ModelRegistryService.ComputeMd5(target);
        }

        var publish = new Dictionary<string, object?>
        {
            ["version"] = version,
            ["task"] = run.Task,
            ["published"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            ["publishedVia"] = "self-training",
            ["sourceNote"] =
                $"自我強化訓練 run={run.Id}；資料集={run.DatasetPath}（{run.ImageCount} 張）；" +
                $"head={run.Head}；{run.Message}" +
                (run.Notes.Length > 0 ? $"；備註：{run.Notes}" : ""),
            ["training"] = new
            {
                run = run.Id,
                dataset = run.DatasetPath,
                images = run.ImageCount,
                head = run.Head,
                metrics = run.Metrics,
                finishedAt = run.FinishedAt,
            },
            ["files"] = task.Files.ToDictionary(n => n, n => (object)new { md5 = md5s[n] }),
        };

        System.IO.File.WriteAllText(
            Path.Combine(destDir, "_publish.json"),
            System.Text.Json.JsonSerializer.Serialize(publish,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        return new
        {
            Task = run.Task,
            Version = version,
            Path = destDir,
            Files = md5s,
            Message = $"已上架 {run.Task}/{version}。" +
                      "⚠ 上架不等於啟用——要到模型池按「設為現用」才會真的用它。",
        };
    }

    /// <summary>這個檔名是不是「這次訓練產出的那一個」。</summary>
    private static bool IsTrainedFile(TrainingRun run, string fileName, bool isMohao)
    {
        if (run.Task == "ocr_crnn")
            return fileName.Contains("nonar", StringComparison.OrdinalIgnoreCase);
        return isMohao
            ? fileName.StartsWith("mohao", StringComparison.OrdinalIgnoreCase)
            : fileName.StartsWith("xuehao", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>同組其他檔案沿用現有的（另一個 head／偵測器）。</summary>
    private string ResolveCompanionFile(TrainingRun run, string fileName, bool isMohao)
    {
        var o = _training.Options;
        if (run.Task == "ocr_crnn")
            return o.CrnnDetectorWeights;
        return isMohao ? o.YoloXuehaoWeights : o.YoloMohaoWeights;
    }

    private static TrainingRunDto ToDto(TrainingRun r, int logLines) => new()
    {
        Id = r.Id,
        Task = r.Task,
        Head = r.Head,
        Dataset = r.DatasetPath,
        ImageCount = r.ImageCount,
        Notes = r.Notes,
        OutputPath = r.OutputPath,
        State = r.State.ToString(),
        Progress = r.Progress,
        Stage = r.Stage,
        Message = r.Message,
        WeightPath = r.WeightPath,
        Metrics = r.Metrics,
        Published = r.Published,
        PublishedVersion = r.PublishedVersion,
        CanPublish = r.CanPublish,
        CreatedAt = r.CreatedAt,
        StartedAt = r.StartedAt,
        FinishedAt = r.FinishedAt,
        Log = logLines > 0 ? r.TailLog(logLines).ToList() : new List<string>(),
    };
}

// ── DTO ────────────────────────────────────────────────────────────

public sealed class UploadDatasetRequest
{
    public List<IFormFile>? Files { get; set; }

    /// <summary>選填：把這批圖歸到子資料夾（分類訓練靠資料夾名當標籤）。</summary>
    public string? Label { get; set; }
}

public sealed class StartTrainingRequest
{
    /// <summary><c>ocr_crnn</c> 或 <c>ocr_pair</c>。</summary>
    public string? Task { get; set; }

    /// <summary>ocr_pair 專用：<c>mohao</c> / <c>xuehao</c>。</summary>
    public string? Head { get; set; }

    /// <summary>已上傳的資料集名稱，或 server 上的絕對路徑。</summary>
    public string? Dataset { get; set; }

    /// <summary>run 名稱（＝資料夾名，也會是上架時的預設版本名）。</summary>
    public string? RunName { get; set; }

    public string? Notes { get; set; }
}

public sealed class PublishRunRequest
{
    /// <summary>上架版本名；省略＝用 run 名稱。</summary>
    public string? Version { get; set; }
}

public sealed class TrainingStatusDto
{
    public bool Enabled { get; set; }
    public bool Busy { get; set; }
    public string DatasetRoot { get; set; } = "";
    public string OutputRoot { get; set; } = "";
    public int MinImages { get; set; }
    public int Epochs { get; set; }
    public string Device { get; set; } = "";
    public bool CrnnEntryReady { get; set; }
    public bool YoloEntryReady { get; set; }
    public bool RehearsalReady { get; set; }
    public double CrnnMinSelectedAccuracy { get; set; }
    public double CrnnMaxRehearsalRegression { get; set; }
    public double YoloMinTargetRecall { get; set; }
    public double YoloMaxFalsePositiveRate { get; set; }
    public string? Note { get; set; }
}

public sealed class DatasetListDto
{
    public string Root { get; set; } = "";
    public List<DatasetDto> Datasets { get; set; } = new();
}

public sealed class DatasetDto
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public int ImageCount { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>本次上傳存了幾張。</summary>
    public int Saved { get; set; }

    /// <summary>本次上傳略過幾張（非影像或同名已存在）。</summary>
    public int Skipped { get; set; }
}

public sealed class TrainingRunListDto
{
    public bool Busy { get; set; }
    public List<TrainingRunDto> Runs { get; set; } = new();
}

public sealed class TrainingRunDto
{
    public string Id { get; set; } = "";
    public string Task { get; set; } = "";
    public string Head { get; set; } = "";
    public string Dataset { get; set; } = "";
    public int ImageCount { get; set; }
    public string Notes { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string State { get; set; } = "";
    public int Progress { get; set; }
    public string Stage { get; set; } = "";
    public string Message { get; set; } = "";
    public string? WeightPath { get; set; }
    public Dictionary<string, double> Metrics { get; set; } = new();
    public bool Published { get; set; }
    public string? PublishedVersion { get; set; }
    public bool CanPublish { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public List<string> Log { get; set; } = new();
}
