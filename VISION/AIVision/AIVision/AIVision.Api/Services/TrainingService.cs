using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace AIVision.Api.Services;

/// <summary>
/// 自我強化訓練（跑在中央推論機）。移植自 <c>模號檢驗/相機版</c> 的 Training 子系統。
///
/// <para><b>為什麼要有</b>：產線抓到的混料圖是**自帶正解**的資料
/// （`exp_M108-14_got_M17-14_*.jpg`——`exp_` 後面就是答案），
/// 拿它回頭補強模型，錯過一次的下次就不會再錯。</para>
///
/// <para><b>兩條鐵律</b>：</para>
/// <list type="number">
/// <item><b>永不覆蓋 production 權重</b>——一律開新 run 夾。</item>
/// <item><b>過驗證閘門才算候選</b>，且**使用者再按上架才會生效**。
///       尤其 CRNN 一定要跑 <b>rehearsal 排練集</b>：只拿一批修正資料訓練，
///       很容易把舊標籤原本會的能力洗掉（災難性遺忘），退步超過容忍值就不採用。</item>
/// </list>
///
/// <para>與 python 的契約完全沿用相機版，現有腳本不必改：
/// 輸入 <c>training_request.json</c>（schema_version=1）、
/// 進度走 stdout 的 <c>[PROGRESS] n 訊息</c>、
/// 結果寫 <c>training_result.json</c>（<c>ok</c>/<c>message</c>/<c>weight_path</c>/<c>metrics</c>），
/// exit code 0=過、2=沒過閘門。</para>
/// </summary>
public sealed class TrainingService : IDisposable
{
    private static readonly Regex ProgressPattern =
        new(@"^\[PROGRESS\]\s+(\d+)\s*(.*)$", RegexOptions.Compiled);

    private static readonly string[] ImageExtensions =
        { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" };

    /// <summary>run 名稱白名單——這個值會變成資料夾名，必須擋路徑跳脫。</summary>
    private static readonly Regex SafeName = new("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);

    private readonly TrainingOptions _options;
    private readonly ModelRegistryService _registry;
    private readonly ILogger<TrainingService>? _logger;

    private readonly ConcurrentDictionary<string, TrainingRun> _runs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>同時只准一個 run 在跑——訓練吃滿 GPU，兩個一起跑只會互相拖垮。</summary>
    private readonly SemaphoreSlim _gpuGate = new(1, 1);

    private bool _disposed;

    public TrainingService(
        IOptions<TrainingOptions> options,
        ModelRegistryService registry,
        ILogger<TrainingService>? logger = null)
    {
        _options = options.Value;
        _options.Normalize();
        _registry = registry;
        _logger = logger;
    }

    public bool Enabled => _options.Enabled;
    public TrainingOptions Options => _options;

    /// <summary>目前有沒有 run 正在跑（畫面用；訓練是獨佔 GPU 的）。</summary>
    public bool IsBusy => _running.Count > 0;

    public IReadOnlyList<TrainingRun> ListRuns() =>
        _runs.Values.OrderByDescending(r => r.CreatedAt).ToList();

    public TrainingRun? GetRun(string id) =>
        _runs.TryGetValue(id ?? "", out var run) ? run : null;

    /// <summary>資料集裡有幾張圖（含子資料夾）。</summary>
    public int CountImages(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return 0;
            return Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Count(f => ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>送出前先擋掉一定會失敗的情況，把原因一次講完（不要讓人跑 3 小時才知道路徑打錯）。</summary>
    public List<string> Validate(string task, string head, string datasetPath, string runName)
    {
        var errors = new List<string>();

        if (!_options.Enabled)
            errors.Add("訓練功能未啟用（appsettings Training:Enabled）。");

        var isCrnn = string.Equals(task, "ocr_crnn", StringComparison.OrdinalIgnoreCase);
        var isPair = string.Equals(task, "ocr_pair", StringComparison.OrdinalIgnoreCase);
        if (!isCrnn && !isPair)
            errors.Add($"用途 '{task}' 不支援訓練（目前只有 ocr_crnn / ocr_pair）。");

        if (isPair && head is not ("mohao" or "xuehao"))
            errors.Add("ocr_pair 必須指定 head：mohao 或 xuehao。");

        var entry = isCrnn ? _options.CrnnEntry : _options.YoloEntry;
        if (string.IsNullOrWhiteSpace(entry) || !File.Exists(entry))
            errors.Add($"訓練入口不存在：{(string.IsNullOrWhiteSpace(entry) ? "(未設定)" : entry)}");

        // CRNN 沒有排練集就不准訓——這是防災難性遺忘的唯一防線，不能省。
        if (isCrnn && (string.IsNullOrWhiteSpace(_options.CrnnRehearsalPath)
                       || !Directory.Exists(_options.CrnnRehearsalPath)))
        {
            errors.Add("CRNN 訓練必須設定 rehearsal 排練集（Training:CrnnRehearsalPath）——" +
                       "少了它，一批修正資料就可能把舊標籤原本會的能力洗掉，而且事後看不出來。");
        }

        if (!Directory.Exists(datasetPath))
            errors.Add($"資料集資料夾不存在：{datasetPath}");
        else if (CountImages(datasetPath) < _options.MinImages)
            errors.Add($"資料集至少要 {_options.MinImages} 張圖。");

        if (string.IsNullOrWhiteSpace(runName) || !SafeName.IsMatch(runName))
            errors.Add("run 名稱必須是安全的單一資料夾名（字母/數字開頭，允許 . _ -）。");
        else if (Directory.Exists(Path.Combine(_options.OutputRoot, runName)))
            errors.Add($"run 名稱已存在：{runName}（為避免覆寫請換一個）。");

        if (IsBusy)
            errors.Add("已有訓練正在執行——訓練會吃滿 GPU，請等它跑完再送下一個。");

        return errors;
    }

    /// <summary>建立並啟動一個 run。回傳建立好的 run（訓練在背景跑）。</summary>
    public TrainingRun Start(string task, string head, string datasetPath, string runName, string notes)
    {
        var outputPath = Path.Combine(_options.OutputRoot, runName);
        Directory.CreateDirectory(outputPath);

        var run = new TrainingRun
        {
            Id = runName,
            Task = task.ToLowerInvariant(),
            Head = head?.ToLowerInvariant() ?? "",
            DatasetPath = Path.GetFullPath(datasetPath),
            ImageCount = CountImages(datasetPath),
            Notes = notes ?? "",
            OutputPath = outputPath,
        };
        _runs[run.Id] = run;
        TrimRuns();

        WriteManifest(run);
        run.AppendLog($"建立 run：{outputPath}");
        run.AppendLog($"資料集：{run.DatasetPath}（{run.ImageCount} 張）");

        var cts = new CancellationTokenSource();
        _running[run.Id] = cts;
        _ = Task.Run(() => ExecuteAsync(run, cts.Token));
        return run;
    }

    /// <summary>取消一個正在跑的 run。</summary>
    public bool Cancel(string id)
    {
        if (!_running.TryGetValue(id ?? "", out var cts)) return false;
        try { cts.Cancel(); } catch { /* 已結束 */ }
        return true;
    }

    // ================================================================

    /// <summary>寫 python 要吃的 manifest。格式沿用相機版，欄位一個都不改。</summary>
    private void WriteManifest(TrainingRun run)
    {
        var isCrnn = run.Task == "ocr_crnn";
        var isMohao = run.Head == "mohao";

        object backend = isCrnn
            ? new
            {
                detector_weights = _options.CrnnDetectorWeights,
                base_weights = _options.CrnnBaseWeights,
                rehearsal_path = _options.CrnnRehearsalPath,
                device = _options.Device,
                epochs = _options.Epochs,
                batch_size = _options.BatchSize,
                min_images = _options.MinImages,
                min_accuracy = _options.CrnnMinSelectedAccuracy,
                max_rehearsal_regression = _options.CrnnMaxRehearsalRegression,
                registry_path = Path.Combine(_options.OutputRoot, "model_registry.json"),
                version = run.Id,
                // 中央端**不讓 python 自己上架**：候選一律回到 API，由使用者按「上架」
                // 才走既有的 /api/models 發布流程（有 md5／溯源／版本不可變）。
                register_catalog = false,
            }
            : new
            {
                head = run.Head,
                base_weights = isMohao ? _options.YoloMohaoWeights : _options.YoloXuehaoWeights,
                other_head = isMohao ? "xuehao" : "mohao",
                other_head_weights = isMohao ? _options.YoloXuehaoWeights : _options.YoloMohaoWeights,
                preprocess = "annulus",
                device = _options.Device,
                epochs = _options.Epochs,
                batch_size = _options.BatchSize,
                min_images = _options.MinImages,
                min_accuracy = _options.YoloMinTargetRecall,
                max_false_positive_rate = _options.YoloMaxFalsePositiveRate,
                registry_path = Path.Combine(_options.OutputRoot, "model_registry.json"),
                version = run.Id,
                register_catalog = false,
            };

        var manifest = new
        {
            schema_version = 1,
            model_type = isCrnn ? "crnn" : "yolo_head",
            head = run.Head,
            dataset_path = run.DatasetPath,
            output_path = Path.GetFullPath(run.OutputPath),
            notes = run.Notes,
            created_at = DateTimeOffset.Now,
            backend,
        };

        File.WriteAllText(
            Path.Combine(run.OutputPath, "training_request.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    private async Task ExecuteAsync(TrainingRun run, CancellationToken ct)
    {
        await _gpuGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            run.State = TrainingRunState.Running;
            run.StartedAt = DateTime.Now;

            var entry = run.Task == "ocr_crnn" ? _options.CrnnEntry : _options.YoloEntry;
            var manifestPath = Path.Combine(run.OutputPath, "training_request.json");

            var psi = new ProcessStartInfo
            {
                FileName = _options.PythonPath,
                WorkingDirectory = Path.GetDirectoryName(entry) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add(entry);
            psi.ArgumentList.Add("--request");
            psi.ArgumentList.Add(manifestPath);
            psi.Environment["PYTHONIOENCODING"] = "utf-8";

            run.AppendLog($"啟動：{psi.FileName} {entry} --request {manifestPath}");

            using var process = new Process { StartInfo = psi };
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_options.TimeoutMs);

            if (!process.Start())
            {
                Finish(run, TrainingRunState.Error, "無法啟動 python 行程。");
                return;
            }

            var stdout = PumpAsync(process.StandardOutput, run, isError: false, timeoutCts.Token);
            var stderr = PumpAsync(process.StandardError, run, isError: true, timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process, run);
                Finish(run,
                    ct.IsCancellationRequested ? TrainingRunState.Cancelled : TrainingRunState.Error,
                    ct.IsCancellationRequested ? "使用者取消。" : $"逾時（>{_options.TimeoutMs / 1000}s），已終止。");
                return;
            }

            ReadResult(run, process.ExitCode);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[Training] run {Id} 執行失敗", run.Id);
            Finish(run, TrainingRunState.Error, $"執行失敗：{ex.Message}");
        }
        finally
        {
            _running.TryRemove(run.Id, out _);
            _gpuGate.Release();
        }
    }

    private async Task PumpAsync(StreamReader reader, TrainingRun run, bool isError, CancellationToken ct)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                var m = ProgressPattern.Match(line);
                if (m.Success)
                {
                    run.Progress = Math.Clamp(int.Parse(m.Groups[1].Value), 0, 100);
                    run.Stage = m.Groups[2].Value.Trim();
                    run.AppendLog($"[{run.Progress}%] {run.Stage}");
                }
                else
                {
                    run.AppendLog(isError ? "[stderr] " + line : line);
                }
            }
        }
        catch (OperationCanceledException) { /* 行程結束/取消 */ }
        catch (Exception ex)
        {
            run.AppendLog($"[讀取輸出失敗] {ex.Message}");
        }
    }

    /// <summary>讀 python 寫的 training_result.json，並套用驗證閘門。</summary>
    private void ReadResult(TrainingRun run, int exitCode)
    {
        var resultPath = Path.Combine(run.OutputPath, "training_result.json");
        if (!File.Exists(resultPath))
        {
            Finish(run, TrainingRunState.Error,
                $"python 結束（exit={exitCode}）但沒有產生 training_result.json——請看上面的執行紀錄。");
            return;
        }

        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(resultPath));
            var ok = doc.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            var message = doc.TryGetProperty("message", out var msgEl) ? msgEl.GetString() ?? "" : "";

            if (doc.TryGetProperty("weight_path", out var wp) && wp.ValueKind == JsonValueKind.String)
                run.WeightPath = wp.GetString();

            if (doc.TryGetProperty("metrics", out var metrics) && metrics.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in metrics.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Number)
                        run.Metrics[p.Name] = p.Value.GetDouble();
            }

            // ok=false 是「沒過閘門」（正常結果，不是故障）——權重留著供查，但不能上架。
            Finish(run, ok ? TrainingRunState.Passed : TrainingRunState.Failed,
                string.IsNullOrWhiteSpace(message)
                    ? (ok ? "訓練完成且通過驗證。" : "訓練完成但未通過驗證閘門。")
                    : message);
        }
        catch (Exception ex)
        {
            Finish(run, TrainingRunState.Error, $"training_result.json 解析失敗：{ex.Message}");
        }
    }

    private void Finish(TrainingRun run, TrainingRunState state, string message)
    {
        run.State = state;
        run.Message = message;
        run.FinishedAt = DateTime.Now;
        if (state == TrainingRunState.Passed) run.Progress = 100;
        run.AppendLog($"=== {state}：{message} ===");
        _logger?.LogInformation("[Training] run {Id} → {State}：{Message}", run.Id, state, message);
    }

    private void TryKill(Process process, TrainingRun run)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                run.AppendLog("已終止 python 行程。");
            }
        }
        catch { /* already dead */ }
    }

    /// <summary>記憶體裡只留最近 N 筆（磁碟上的 run 夾不動）。</summary>
    private void TrimRuns()
    {
        if (_runs.Count <= _options.MaxRuns) return;
        foreach (var old in _runs.Values
                     .Where(r => r.State is not (TrainingRunState.Running or TrainingRunState.Queued))
                     .OrderBy(r => r.CreatedAt)
                     .Take(_runs.Count - _options.MaxRuns))
        {
            _runs.TryRemove(old.Id, out _);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var kv in _running)
        {
            try { kv.Value.Cancel(); } catch { }
        }
        _gpuGate.Dispose();
    }
}
