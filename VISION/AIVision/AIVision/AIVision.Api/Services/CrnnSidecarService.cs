using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIVision.Api.Services;

/// <summary>CRNN sidecar 設定（appsettings）。</summary>
public sealed class CrnnSidecarOptions
{
    public const string SectionName = "CrnnSidecar";

    /// <summary>未啟用時 <c>POST /api/infer/ocr_crnn</c> 回 503（不影響其他端點）。</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>python 執行檔（需裝 torch/ultralytics/opencv——OCR_demo 的環境）。</summary>
    public string PythonPath { get; set; } = "python";

    /// <summary>OCR_demo 的 app 目錄（main.py 所在；工作目錄必須在這，內部 import 才對）。</summary>
    public string AppDir { get; set; } = @"D:\OCR_demo\app";

    /// <summary>請求未指定 modelVersion 時使用的登錄庫版本（ocr_crnn 用途）。</summary>
    public string DefaultVersion { get; set; } = "";

    /// <summary>
    /// 同時存活的 sidecar 行程上限（AINavi processor_id 借鏡：多版本共存；但每行程=完整 python+torch，
    /// 記憶體貴 → 超過上限時淘汰「最久未用且閒置」者）。2 = 現用 + 一個試模版本。
    /// </summary>
    public int MaxProcesses { get; set; } = 2;

    /// <summary>啟動等 SERVER_READY 的逾時（冷載 torch+模型可達 20-90s）。</summary>
    public int StartTimeoutMs { get; set; } = 90_000;

    /// <summary>單筆推論逾時。</summary>
    public int RequestTimeoutMs { get; set; } = 15_000;
}

/// <summary>sidecar 的單筆辨識結果（RESULT_JSON 的子集 + 映射）。</summary>
public sealed class CrnnResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Mohao { get; set; }
    public string? Xuehao { get; set; }
    public double ConfMohao { get; set; }
    public double ConfXuehao { get; set; }
    public bool NeedsReview { get; set; }
    public bool Present { get; set; }
    public bool HoughUsed { get; set; }
    public double LatencyMs { get; set; }
    public string? ModelVersion { get; set; }
}

/// <summary>health 用：一個已載入版本的狀態。</summary>
public sealed record CrnnLoadedVersion(string Version, bool Ready, DateTime LastUsedUtc);

/// <summary>
/// CRNN python sidecar 管理——**多版本行程池**（ROADMAP「AINavi 借鏡①」，2026-08-06）：
/// 每個登錄庫版本一個子行程（借 AINavi processor_id 的多模型共存語意；**維持子行程隔離**不抄內嵌 DLL
/// ——python 崩潰不拖垮 server，符合「server 掛掉不停線」鐵律）。
/// <list type="bullet">
/// <item>請求帶 modelVersion → 對應行程（沒有就冷啟）；未帶 → DefaultVersion。**換版免改設定免重啟。**</item>
/// <item>版本檔案一律由登錄庫（ModelRegistryService, task=ocr_crnn）解析——版本治理與其他模型同一套。</item>
/// <item>不同版本各自行程各自 gate → 版本間互不排隊（同版本內仍序列化，協定一問一答）。</item>
/// <item>超過 MaxProcesses → 淘汰最久未用且閒置的行程（每行程 = 完整 torch，記憶體貴）。</item>
/// </list>
/// train/infer 一致性：sidecar 直接跑訓練當下那份前處理程式碼（驗證區 OCR_demo，唯讀借用）——
/// 這正是選 sidecar 而非 ONNX 移植的理由（設計 <c>2026-07-31_crnn_engine_intake.md</c> 路線 C）。
/// </summary>
public sealed class CrnnSidecarService : IDisposable
{
    private readonly CrnnSidecarOptions _options;
    private readonly ModelRegistryService _registry;
    private readonly ILogger<CrnnSidecarService>? _logger;

    private readonly ConcurrentDictionary<string, SidecarInstance> _instances =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _createGate = new();
    private bool _disposed;

    public CrnnSidecarService(
        Microsoft.Extensions.Options.IOptions<CrnnSidecarOptions> options,
        ModelRegistryService registry,
        ILogger<CrnnSidecarService>? logger = null)
    {
        _options = options.Value;
        _registry = registry;
        _logger = logger;
    }

    public bool Enabled => _options.Enabled;

    /// <summary>預設版本（請求未指定時用）。可在執行期改（見 <see cref="SetDefaultVersion"/>）。</summary>
    public string DefaultVersion => _volatileDefaultVersion ?? _options.DefaultVersion;

    /// <summary>執行期覆寫的預設版本；null = 用 appsettings 的值。</summary>
    private string? _volatileDefaultVersion;

    /// <summary>
    /// 執行期切換「現用版本」——父端監控畫面選了版本就即刻生效，**免改設定檔免重啟**
    /// （2026-08-19：父端原本根本沒地方選模型）。
    /// <para>只改預設值，不預先冷啟行程：下一筆未指定版本的請求會用新版本，
    /// 該版本沒在池裡就自然冷啟（20-90s）。舊版本行程留著，由 LRU 自然淘汰。</para>
    /// <para>⚠ 只影響本次執行；要永久生效仍請改 appsettings 的 CrnnSidecar:DefaultVersion。</para>
    /// </summary>
    /// <returns>失敗原因；成功回 null。</returns>
    public string? SetDefaultVersion(string version)
    {
        version = (version ?? "").Trim();
        if (version.Length == 0)
            return "版本不可空白。";
        if (!ModelRegistryService.IsSafeVersionName(version))
            return "版本名不合法（僅允許字母/數字開頭與 . _ -）。";
        if (_registry.ResolveFile("ocr_crnn", version, "detector.pt") is null ||
            _registry.ResolveFile("ocr_crnn", version, "nonar.pt") is null)
            return $"登錄庫（ocr_crnn）找不到版本 '{version}'——請先從發布頁上架。";

        _volatileDefaultVersion = version;
        _logger?.LogInformation("[CrnnSidecar] 現用版本切換為 {Version}（執行期，未寫回 appsettings）", version);
        return null;
    }

    /// <summary>目前池中的版本（health 用）。</summary>
    public IReadOnlyList<CrnnLoadedVersion> LoadedVersions =>
        _instances.Values.Select(i => new CrnnLoadedVersion(i.Version, i.Ready, i.LastUsedUtc)).ToList();

    /// <summary>
    /// 辨識一張圖（PNG bytes）。<paramref name="requestedVersion"/> null/空 = DefaultVersion。
    /// 版本不存在於登錄庫 → Ok=false 且 <see cref="CrnnResult.Error"/> 以 "VERSION_NOT_FOUND:" 開頭（供 controller 轉 404）。
    /// </summary>
    public async Task<CrnnResult> RecognizeAsync(
        byte[] pngBytes, string? requestedVersion = null, CancellationToken ct = default,
        bool isStrip = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var version = string.IsNullOrWhiteSpace(requestedVersion)
            ? (_options.DefaultVersion ?? "").Trim()
            : requestedVersion!.Trim();
        if (version.Length == 0)
            return new CrnnResult { Ok = false, Error = "未指定版本且 CrnnSidecar:DefaultVersion 未配置。" };

        var detector = _registry.ResolveFile("ocr_crnn", version, "detector.pt");
        var nonar = _registry.ResolveFile("ocr_crnn", version, "nonar.pt");
        if (detector is null || nonar is null)
            return new CrnnResult
            {
                Ok = false,
                Error = $"VERSION_NOT_FOUND:登錄庫（ocr_crnn）找不到版本 '{version}'——請先從發布頁上架。",
            };

        var instance = GetOrCreateInstance(version, detector, nonar);
        return await instance.RecognizeAsync(pngBytes, ct, isStrip).ConfigureAwait(false);
    }

    private SidecarInstance GetOrCreateInstance(string version, string detector, string nonar)
    {
        if (_instances.TryGetValue(version, out var existing))
            return existing;

        lock (_createGate)
        {
            if (_instances.TryGetValue(version, out existing))
                return existing;

            // 池滿 → 淘汰最久未用且「當下閒置」的行程（絕不淘汰正在辨識中的）。
            while (_instances.Count >= Math.Max(1, _options.MaxProcesses))
            {
                var victim = _instances.Values
                    .Where(i => i.IsIdle)
                    .OrderBy(i => i.LastUsedUtc)
                    .FirstOrDefault();
                if (victim is null) break;   // 全忙：先超編，用完自然回收不了也只多佔一陣子記憶體
                if (_instances.TryRemove(victim.Version, out var removed))
                {
                    _logger?.LogInformation("[CrnnSidecar] 池滿，淘汰版本 {Version}（LRU）", removed.Version);
                    removed.Dispose();
                }
            }

            var fresh = new SidecarInstance(version, detector, nonar, _options, _logger);
            _instances[version] = fresh;
            _logger?.LogInformation("[CrnnSidecar] 建立版本 {Version} 的 sidecar（池 {Count}/{Max}）",
                version, _instances.Count, _options.MaxProcesses);
            return fresh;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var kv in _instances)
            kv.Value.Dispose();
        _instances.Clear();
    }

    // ================================================================
    /// <summary>單一版本的 sidecar 行程（自帶 gate；協定一問一答故同版本內序列化）。</summary>
    private sealed class SidecarInstance : IDisposable
    {
        private readonly string _detector;
        private readonly string _nonar;
        private readonly CrnnSidecarOptions _options;
        private readonly ILogger? _logger;
        private readonly SemaphoreSlim _gate = new(1, 1);

        private Process? _proc;
        private bool _disposed;

        public SidecarInstance(string version, string detector, string nonar,
            CrnnSidecarOptions options, ILogger? logger)
        {
            Version = version;
            _detector = detector;
            _nonar = nonar;
            _options = options;
            _logger = logger;
        }

        public string Version { get; }
        public bool Ready => _proc is { HasExited: false };
        public DateTime LastUsedUtc { get; private set; } = DateTime.UtcNow;
        public bool IsIdle => _gate.CurrentCount > 0;

        public async Task<CrnnResult> RecognizeAsync(byte[] pngBytes, CancellationToken ct, bool isStrip = false)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            string? tmpFile = null;
            try
            {
                LastUsedUtc = DateTime.UtcNow;
                if (_disposed)
                    return new CrnnResult { Ok = false, Error = "此版本行程已被淘汰，請重試（會重新冷啟）。" };

                var startError = await EnsureStartedAsync(ct).ConfigureAwait(false);
                if (startError is not null)
                    return new CrnnResult { Ok = false, Error = startError };

                // ⚠ 交圖給 sidecar 是走「寫暫存檔 → 把路徑丟給 python」。
                //   暫存檔原本放在 %TEMP%，那是防毒/索引器掃得最兇的地方：新檔剛寫好就被掃描器short暫鎖住，
                //   python open() 拿到 [Errno 13] Permission denied。實測 12 次裡失敗 1 次（約 8%）。
                //   後果在站端會被放大：一次瞬斷就讓實時管線進 30 秒降級冷卻、整段走本機舊模型。
                //   對策二選一都做：① 改用我們自己的資料夾（掃描器較少碰）② 檔案問題重試一次。
                var tmpDir = Path.Combine(AppContext.BaseDirectory, "tmp", "crnn");
                Directory.CreateDirectory(tmpDir);

                CrnnResult result = default!;
                for (var attempt = 1; attempt <= 2; attempt++)
                {
                    if (tmpFile is not null)
                        try { File.Delete(tmpFile); } catch { /* 清不掉不影響 */ }

                    tmpFile = Path.Combine(tmpDir, $"aivision_crnn_{Guid.NewGuid():N}.png");
                    await File.WriteAllBytesAsync(tmpFile, pngBytes, ct).ConfigureAwait(false);

                    // 協定：一行 JSON 請求 → 讀到 RESULT_JSON。apply_roi=false：收已裁鏡片圖（同 /pair 契約）。
                    // is_strip=true：站端(edge)已做完前處理，父端只做辨識，不再找圓/展開。
                    var request = JsonSerializer.Serialize(
                        new { image = tmpFile, apply_roi = false, is_strip = isStrip });
                    await _proc!.StandardInput.WriteLineAsync(request.AsMemory(), ct).ConfigureAwait(false);
                    await _proc.StandardInput.FlushAsync(ct).ConfigureAwait(false);

                    var json = await ReadUntilPrefixAsync("RESULT_JSON:", _options.RequestTimeoutMs, ct)
                        .ConfigureAwait(false);
                    if (json is null)
                    {
                        KillProcess("推論逾時/行程無回應");
                        return new CrnnResult { Ok = false, Error = $"CRNN sidecar（{Version}）無回應（>{_options.RequestTimeoutMs}ms），已重置行程。" };
                    }

                    result = MapResult(json);
                    if (result.Ok || attempt == 2 || !IsTransientFileError(result.Error))
                        return result;

                    // 檔案被短暫鎖住 → 換一個檔名再試一次（不重啟行程，成本很低）
                    await Task.Delay(20, ct).ConfigureAwait(false);
                }
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                KillProcess($"例外：{ex.Message}");
                return new CrnnResult { Ok = false, Error = $"CRNN sidecar（{Version}）呼叫失敗：{ex.Message}" };
            }
            finally
            {
                if (tmpFile is not null)
                    try { File.Delete(tmpFile); } catch { /* 暫存清不掉不影響結果 */ }
                LastUsedUtc = DateTime.UtcNow;
                _gate.Release();
            }
        }

        /// <summary>
        /// 是不是「暫存檔一時打不開」這種可重試的錯（防毒/索引器短暫鎖檔）。
        /// 只認檔案存取類的字眼——辨識本身失敗（讀不到字）不該重試。
        /// </summary>
        private static bool IsTransientFileError(string? error) =>
            error is not null &&
            (error.Contains("Errno 13", StringComparison.OrdinalIgnoreCase)
             || error.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
             || error.Contains("being used by another process", StringComparison.OrdinalIgnoreCase));

        private async Task<string?> EnsureStartedAsync(CancellationToken ct)
        {
            if (_proc is { HasExited: false })
                return null;
            if (!Directory.Exists(_options.AppDir))
                return $"CrnnSidecar:AppDir 不存在：{_options.AppDir}";

            var psi = new ProcessStartInfo
            {
                FileName = _options.PythonPath,
                // 權重明確指定（登錄庫路徑）＋前處理=crnn → serve 端建 CrnnEngine；版本標籤原樣回聲。
                // -B：禁寫 __pycache__——sidecar 跑的是「驗證區」程式碼（四區規則：前三區唯讀），
                // 連位元組碼快取這種副作用寫入都不可以（2026-08-06 審核實查曾寫入，據此補防）。
                Arguments = "-X utf8 -B main.py --serve" +
                            $" --model-version \"{Version}\"" +
                            $" --mohao-weights \"{_detector}\"" +
                            $" --xuehao-weights \"{_nonar}\"" +
                            " --mohao-pre crnn --xuehao-pre crnn",
                WorkingDirectory = _options.AppDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";   // 與 -B 雙保險（防子 import 繞過）

            _logger?.LogInformation("[CrnnSidecar/{Version}] 啟動：{Python} {Args}", Version, psi.FileName, psi.Arguments);
            _proc = Process.Start(psi);
            if (_proc is null)
                return "無法啟動 python 行程。";
            _proc.StandardInput.AutoFlush = true;

            var proc = _proc;
            _ = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await proc.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                        _logger?.LogDebug("[CrnnSidecar/{Version}/stderr] {Line}", Version, line);
                }
                catch { /* 行程結束時必然中斷 */ }
            }, CancellationToken.None);

            var ready = await ReadUntilPrefixAsync("SERVER_READY:", _options.StartTimeoutMs, ct, "SERVER_ERROR:")
                .ConfigureAwait(false);
            if (ready is null)
            {
                KillProcess("啟動逾時或 SERVER_ERROR");
                return $"CRNN sidecar（{Version}）啟動失敗（{_options.StartTimeoutMs}ms 內未 READY）。";
            }
            _logger?.LogInformation("[CrnnSidecar/{Version}] READY", Version);
            return null;
        }

        private async Task<string?> ReadUntilPrefixAsync(
            string prefix, int timeoutMs, CancellationToken ct, string? errorPrefix = null)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            try
            {
                while (true)
                {
                    var line = await _proc!.StandardOutput.ReadLineAsync(cts.Token).ConfigureAwait(false);
                    if (line is null) return null;
                    if (line.StartsWith(prefix, StringComparison.Ordinal))
                        return line[prefix.Length..];
                    if (errorPrefix is not null && line.StartsWith(errorPrefix, StringComparison.Ordinal))
                    {
                        _logger?.LogError("[CrnnSidecar/{Version}] {Line}", Version, line);
                        return null;
                    }
                    _logger?.LogDebug("[CrnnSidecar/{Version}/stdout] {Line}", Version, line);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }
        }

        private static CrnnResult MapResult(string json)
        {
            try
            {
                var d = JsonSerializer.Deserialize<JsonElement>(json);
                bool ok = d.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
                if (!ok)
                    return new CrnnResult
                    {
                        Ok = false,
                        Error = d.TryGetProperty("error", out var e) ? e.GetString() : "sidecar 回報失敗",
                    };

                string? GetS(string k) => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                double GetD(string k) => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
                bool GetB(string k) => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;

                return new CrnnResult
                {
                    Ok = true,
                    Mohao = GetS("mohao"),
                    Xuehao = GetS("xuehao"),
                    ConfMohao = GetD("conf_mohao"),
                    ConfXuehao = GetD("conf_xuehao"),
                    NeedsReview = GetB("needs_review"),
                    Present = GetB("present"),
                    HoughUsed = GetB("hough_used"),
                    LatencyMs = GetD("latency_ms"),
                    ModelVersion = GetS("model_version"),
                };
            }
            catch (Exception ex)
            {
                return new CrnnResult { Ok = false, Error = $"RESULT_JSON 解析失敗：{ex.Message}" };
            }
        }

        private void KillProcess(string reason)
        {
            _logger?.LogWarning("[CrnnSidecar/{Version}] 重置行程：{Reason}", Version, reason);
            try
            {
                if (_proc is { HasExited: false })
                    _proc.Kill(entireProcessTree: true);
            }
            catch { /* already dead */ }
            _proc?.Dispose();
            _proc = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_proc is { HasExited: false })
                {
                    try { _proc.StandardInput.WriteLine("EXIT"); } catch { /* best effort */ }
                    if (!_proc.WaitForExit(2000))
                        _proc.Kill(entireProcessTree: true);
                }
            }
            catch { /* shutdown path */ }
            _proc?.Dispose();
            _gate.Dispose();
        }
    }
}
