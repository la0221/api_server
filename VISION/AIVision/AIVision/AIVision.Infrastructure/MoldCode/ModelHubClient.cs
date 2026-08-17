using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// 模型倉庫（server `api/models/{task}`）的 edge 客戶端：列用途/列版本/下載同步/上架發布。
/// 位址沿用 <see cref="InferenceServerOptions.BaseUrl"/>（與中央推論同一台，「API 伺服器設定」切換即生效）。
/// <para>推論客戶端見 <see cref="RemotePairRecognizer"/>——職責分開：那邊管推、這邊管模型生命週期。</para>
/// </summary>
public sealed class ModelHubClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient _http;
    private readonly InferenceServerOptions _options;
    private readonly ILogger<ModelHubClient>? _logger;

    public ModelHubClient(
        HttpClient http,
        IOptions<InferenceServerOptions> options,
        ILogger<ModelHubClient>? logger = null)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>用途總覽（GET /api/models）。連不上/解析失敗回 null。</summary>
    public async Task<TaskOverviewDto?> GetTasksAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(_options.HealthTimeoutMs, 3000)));
            using var resp = await _http.GetAsync(BuildUrl("api/models"), cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<TaskOverviewDto>(
                await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false), JsonOpts);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[ModelHub] 用途總覽失敗: {BaseUrl}", _options.BaseUrl);
            return null;
        }
    }

    /// <summary>某用途的版本清單（GET /api/models/{task}）。連不上/解析失敗回 null。</summary>
    public async Task<ModelListDto?> ListAsync(string task, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(_options.HealthTimeoutMs, 3000)));
            using var resp = await _http.GetAsync(
                BuildUrl($"api/models/{Uri.EscapeDataString(task)}"), cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger?.LogWarning("[ModelHub] 版本清單非 2xx: {Task} {Status}", task, (int)resp.StatusCode);
                return null;
            }
            return JsonSerializer.Deserialize<ModelListDto>(
                await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false), JsonOpts);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[ModelHub] 版本清單失敗: {Task}@{BaseUrl}", task, _options.BaseUrl);
            return null;
        }
    }

    /// <summary>
    /// 下載一個版本（該用途全部檔案）到本地登錄夾：逐檔 .tmp 串流 → **重算 md5 與 server 宣告比對**
    /// （信任鏈時機 B；宣告缺失或不符一律拒收）→ 原子改名 → 落 _publish.json（server 原文優先）+ _sync.json。
    /// 任何一步失敗 → 清 .tmp、不落地。
    /// </summary>
    public async Task<ModelDownloadResult> DownloadVersionAsync(
        string task, ModelListEntryDto entry, string destRoot, CancellationToken ct = default)
    {
        var version = (entry.Version ?? "").Trim();
        if (version.Length == 0 || version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || version.Contains(".."))
            return ModelDownloadResult.Fail($"版本名不合法：'{entry.Version}'");
        if (entry.Files is not { Count: > 0 })
            return ModelDownloadResult.Fail("清單項目沒有檔案資訊，無法下載。");

        var destDir = Path.Combine(destRoot, version);
        try
        {
            Directory.CreateDirectory(destDir);

            foreach (var f in entry.Files)
            {
                ct.ThrowIfCancellationRequested();
                var name = f.Name ?? "";
                if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    return ModelDownloadResult.Fail($"檔名不合法：'{f.Name}'");

                var tmp = Path.Combine(destDir, name + ".tmp");
                var final = Path.Combine(destDir, name);

                using (var resp = await _http.GetAsync(
                    BuildUrl($"api/models/{Uri.EscapeDataString(task)}/{Uri.EscapeDataString(version)}/download?file={Uri.EscapeDataString(name)}"),
                    HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                        return ModelDownloadResult.Fail($"{name} 下載失敗：HTTP {(int)resp.StatusCode}");
                    await using var fs = File.Create(tmp);
                    await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
                }

                var actualMd5 = await Task.Run(() => ComputeMd5(tmp), ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(f.Md5))
                {
                    File.Delete(tmp);
                    return ModelDownloadResult.Fail($"{name}：server 未提供 md5，無法複驗 → 拒收（fail-closed）。");
                }
                if (!string.Equals(actualMd5, f.Md5, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(tmp);
                    return ModelDownloadResult.Fail(
                        $"{name} md5 複驗不符：期望 {f.Md5}，實得 {actualMd5} → 已丟棄（檔案毀損或被改）。");
                }
                File.Move(tmp, final, overwrite: true);
            }

            // 溯源落地：server 的 _publish.json 原文優先；沒有就以清單資訊重建。
            var publishPath = Path.Combine(destDir, "_publish.json");
            if (entry.Publish is JsonElement pub && pub.ValueKind == JsonValueKind.Object)
                await File.WriteAllTextAsync(publishPath,
                    JsonSerializer.Serialize(pub, new JsonSerializerOptions { WriteIndented = true }), ct)
                    .ConfigureAwait(false);
            else if (!File.Exists(publishPath))
                await File.WriteAllTextAsync(publishPath, JsonSerializer.Serialize(new
                {
                    version,
                    task,
                    published = entry.Published,
                    files = entry.Files.ToDictionary(f => f.Name ?? "?", f => new { md5 = f.Md5 }),
                }, new JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);

            await File.WriteAllTextAsync(Path.Combine(destDir, "_sync.json"), JsonSerializer.Serialize(new
            {
                server = _options.BaseUrl,
                task,
                downloadedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                md5Verified = true,
                files = entry.Files.ToDictionary(f => f.Name ?? "?", f => new { md5 = f.Md5 }),
            }, new JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);

            _logger?.LogInformation("[ModelHub] 已下載並複驗 {Task}/{Version} → {Dir}", task, version, destDir);
            return ModelDownloadResult.Ok(destDir);
        }
        catch (OperationCanceledException)
        {
            TryCleanupTmp(destDir);
            return ModelDownloadResult.Fail("已取消下載。");
        }
        catch (Exception ex)
        {
            TryCleanupTmp(destDir);
            _logger?.LogWarning(ex, "[ModelHub] 下載失敗: {Task}/{Version}", task, version);
            return ModelDownloadResult.Fail($"下載失敗：{ex.Message}");
        }
    }

    /// <summary>
    /// 上架發布（POST /api/models/{task}）：把本機選好的模型檔上傳到 server 登錄夾。
    /// server 端會對版檔案組成、算 md5、原子落地、寫 _publish.json；版本已存在回 409（要換版本號）。
    /// </summary>
    /// <param name="files">(目標檔名, 本機來源路徑)。目標檔名須符合該用途組成（如 mohao.onnx）。</param>
    /// <param name="judgeJson">選填：per-class 判定門檻 JSON（隨模型版控進 _publish.json "judge" 段）。</param>
    /// <param name="preprocessJson">選填：前處理參數 JSON（"preprocess" 段；鍵=WarpPolarParams 欄位）。</param>
    public async Task<ModelPublishResult> PublishAsync(
        string task, string version, IReadOnlyList<(string fileName, string sourcePath)> files,
        string? sourceNote = null, CancellationToken ct = default,
        string? judgeJson = null, string? preprocessJson = null)
    {
        try
        {
            foreach (var (_, src) in files)
                if (!File.Exists(src))
                    return ModelPublishResult.Fail($"來源檔不存在：{src}");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(5));   // 上傳量可達數十 MB，別用推論等級逾時。

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(version), "version");
            if (!string.IsNullOrWhiteSpace(sourceNote))
                form.Add(new StringContent(sourceNote), "sourceNote");
            if (!string.IsNullOrWhiteSpace(judgeJson))
                form.Add(new StringContent(judgeJson), "judgeJson");
            if (!string.IsNullOrWhiteSpace(preprocessJson))
                form.Add(new StringContent(preprocessJson), "preprocessJson");

            var streams = new List<FileStream>();
            try
            {
                foreach (var (fileName, sourcePath) in files)
                {
                    var fs = File.OpenRead(sourcePath);
                    streams.Add(fs);
                    var content = new StreamContent(fs);
                    content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
                    form.Add(content, "files", fileName);
                }

                using var resp = await _http.PostAsync(
                    BuildUrl($"api/models/{Uri.EscapeDataString(task)}"), form, cts.Token).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    // ProblemDetails 的 detail 是人話錯誤（409 版本已存在 / 400 組成不符…），直接轉給 UI。
                    string? detail = null;
                    try
                    {
                        var problem = JsonSerializer.Deserialize<JsonElement>(body);
                        if (problem.TryGetProperty("detail", out var d)) detail = d.GetString();
                    }
                    catch { /* 非 JSON 回應就用狀態碼 */ }
                    return ModelPublishResult.Fail(
                        detail ?? $"上架失敗：HTTP {(int)resp.StatusCode}", (int)resp.StatusCode);
                }

                var entry = JsonSerializer.Deserialize<ModelListEntryDto>(body, JsonOpts);
                _logger?.LogInformation("[ModelHub] 已上架 {Task}/{Version}", task, version);
                return ModelPublishResult.Ok(entry);
            }
            finally
            {
                foreach (var s in streams) s.Dispose();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ModelPublishResult.Fail("上傳逾時（>5 分鐘）。");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[ModelHub] 上架失敗: {Task}/{Version}", task, version);
            return ModelPublishResult.Fail($"上架失敗：{ex.Message}");
        }
    }

    private static void TryCleanupTmp(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var tmp in Directory.GetFiles(dir, "*.tmp"))
                File.Delete(tmp);
        }
        catch { /* 清理失敗不掩蓋原始錯誤 */ }
    }

    private static string ComputeMd5(string path)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(md5.ComputeHash(fs)).ToLowerInvariant();
    }

    private string BuildUrl(string path)
    {
        var base_ = (_options.BaseUrl ?? "").TrimEnd('/');
        return $"{base_}/{path}";
    }
}

/// <summary>下載結果（成功=落地資料夾；失敗=原因，且保證未留半套檔）。</summary>
public sealed record ModelDownloadResult(bool Success, string? DestDir, string? Error)
{
    public static ModelDownloadResult Ok(string destDir) => new(true, destDir, null);
    public static ModelDownloadResult Fail(string error) => new(false, null, error);
}

/// <summary>上架結果。</summary>
public sealed record ModelPublishResult(bool Success, ModelListEntryDto? Entry, string? Error, int? StatusCode)
{
    public static ModelPublishResult Ok(ModelListEntryDto? entry) => new(true, entry, null, null);
    public static ModelPublishResult Fail(string error, int? statusCode = null) => new(false, null, error, statusCode);
}

/// <summary>`GET /api/models` 的回應：用途總覽。</summary>
public sealed class TaskOverviewDto
{
    public List<TaskOverviewEntryDto> Tasks { get; set; } = new();
}

public sealed class TaskOverviewEntryDto
{
    public string? Task { get; set; }
    public string? DisplayName { get; set; }
    public List<string> Files { get; set; } = new();
    public int VersionCount { get; set; }
    public bool InferReady { get; set; }
}

/// <summary>`GET /api/models/{task}` 的回應。</summary>
public sealed class ModelListDto
{
    public string? Task { get; set; }
    public string? RegistryRoot { get; set; }
    public string? ServerCurrentVersion { get; set; }
    public List<ModelListEntryDto> Versions { get; set; } = new();
}

/// <summary>server 登錄夾中的一個版本。</summary>
public sealed class ModelListEntryDto
{
    public string? Version { get; set; }
    public string? Published { get; set; }
    public List<ModelFileEntryDto> Files { get; set; } = new();
    public bool IsServerCurrent { get; set; }
    public bool IsLoadedInMemory { get; set; }
    public JsonElement? Publish { get; set; }
}

/// <summary>版本內單一檔案。</summary>
public sealed class ModelFileEntryDto
{
    public string? Name { get; set; }
    public string? Md5 { get; set; }
    public long Bytes { get; set; }
}
