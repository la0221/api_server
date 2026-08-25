using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVision.Infrastructure.MoldCode;

/// <summary>
/// 中央推論機「監控」用的客戶端（2026-08-19）——父端監控畫面專用，
/// 與 <see cref="CrnnInferClient"/>（送檢）分開：這支不送圖，只問狀態。
/// <list type="bullet">
/// <item><c>GET /api/infer/recent</c>：最近收到哪些送檢（父端原本看不到，現場無法確認有沒有收到）</item>
/// <item><c>GET /api/models/pools</c>：**按用途分的模型池**（模號穴號／公母模／瑕疵各自一池）</item>
/// <item><c>POST /api/models/{task}/current</c>：切換該用途的現用版本（父端原本沒地方選模型）</item>
/// </list>
/// 連不上一律回 null / 錯誤字串，不拋——監控畫面不該因為 server 掛了而崩潰。
/// </summary>
public sealed class InferenceMonitorClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient _http;
    private readonly InferenceServerOptions _options;
    private readonly ILogger<InferenceMonitorClient>? _logger;

    public InferenceMonitorClient(
        HttpClient http,
        IOptions<InferenceServerOptions> options,
        ILogger<InferenceMonitorClient>? logger = null)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>最近 N 筆收件紀錄（新→舊）。連不上回 null。</summary>
    public async Task<RecentInferenceDto?> GetRecentAsync(int take = 50, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(_options.HealthTimeoutMs, 3000)));
            using var resp = await _http.GetAsync(BuildUrl($"api/infer/recent?take={take}"), cts.Token)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<RecentInferenceDto>(
                await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false), JsonOpts);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[Monitor] 取最近紀錄失敗: {BaseUrl}", _options.BaseUrl);
            return null;
        }
    }

    /// <summary>單筆收件詳細。找不到／連不上回 null。</summary>
    public async Task<RecentInferenceItem?> GetRecentOneAsync(long seq, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(_options.HealthTimeoutMs, 3000)));
            using var resp = await _http.GetAsync(BuildUrl($"api/infer/recent/{seq}"), cts.Token)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<RecentInferenceItem>(
                await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false), JsonOpts);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[Monitor] 取單筆失敗 seq={Seq}", seq);
            return null;
        }
    }

    /// <summary>取某筆留存下來的影像位元組。沒留存／檔案被清掉／連不上回 null。</summary>
    public async Task<byte[]?> GetRecentImageAsync(long seq, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var resp = await _http.GetAsync(BuildUrl($"api/infer/recent/{seq}/image"), cts.Token)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[Monitor] 取影像失敗 seq={Seq}", seq);
            return null;
        }
    }

    /// <summary>影像留存設定與現況。連不上回 null。</summary>
    public async Task<ReceivedImageSettingsInfo?> GetImageSettingsAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(_options.HealthTimeoutMs, 3000)));
            using var resp = await _http.GetAsync(BuildUrl("api/infer/recent/images"), cts.Token)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<ReceivedImageSettingsInfo>(
                await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false), JsonOpts);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[Monitor] 取影像留存設定失敗: {BaseUrl}", _options.BaseUrl);
            return null;
        }
    }

    /// <summary>開／關「留存收到的影像」。成功回新設定；失敗回 null。</summary>
    public async Task<ReceivedImageSettingsInfo?> SetImageSaveAsync(bool save, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var resp = await _http.PostAsJsonAsync(
                BuildUrl("api/infer/recent/images"), new { save }, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<ReceivedImageSettingsInfo>(
                await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false), JsonOpts);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[Monitor] 切換影像留存失敗");
            return null;
        }
    }

    // ── 自我強化訓練（跑在中央推論機）────────────────────────────

    /// <summary>訓練功能現況與閘門設定。連不上／未啟用回 null。</summary>
    public async Task<TrainingStatusInfo?> GetTrainingStatusAsync(CancellationToken ct = default)
        => await GetJsonAsync<TrainingStatusInfo>("api/training/status", ct).ConfigureAwait(false);

    /// <summary>已上傳的訓練資料集。</summary>
    public async Task<TrainingDatasetList?> GetDatasetsAsync(CancellationToken ct = default)
        => await GetJsonAsync<TrainingDatasetList>("api/training/datasets", ct).ConfigureAwait(false);

    /// <summary>所有訓練 run（新→舊）。</summary>
    public async Task<TrainingRunList?> GetTrainingRunsAsync(CancellationToken ct = default)
        => await GetJsonAsync<TrainingRunList>("api/training/runs", ct).ConfigureAwait(false);

    /// <summary>單一 run 的狀態＋執行紀錄。</summary>
    public async Task<TrainingRunInfo?> GetTrainingRunAsync(
        string id, int logLines = 200, CancellationToken ct = default)
        => await GetJsonAsync<TrainingRunInfo>($"api/training/runs/{id}?logLines={logLines}", ct)
            .ConfigureAwait(false);

    /// <summary>開始訓練。成功回 run；失敗回 null 並把原因放進 <paramref name="error"/>。</summary>
    public async Task<(TrainingRunInfo? Run, string? Error)> StartTrainingAsync(
        object body, CancellationToken ct = default)
        => await PostJsonAsync<TrainingRunInfo>("api/training/runs", body, 30, ct).ConfigureAwait(false);

    /// <summary>取消訓練。</summary>
    public async Task<string?> CancelTrainingAsync(string id, CancellationToken ct = default)
        => (await PostJsonAsync<object>($"api/training/runs/{id}/cancel", new { }, 15, ct)
            .ConfigureAwait(false)).Error;

    /// <summary>把通過驗證的候選上架。成功回 null；失敗回可直接顯示的訊息。</summary>
    public async Task<string?> PublishTrainingRunAsync(
        string id, string? version, CancellationToken ct = default)
        => (await PostJsonAsync<object>($"api/training/runs/{id}/publish", new { version }, 60, ct)
            .ConfigureAwait(false)).Error;

    private async Task<T?> GetJsonAsync<T>(string path, CancellationToken ct) where T : class
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(_options.HealthTimeoutMs, 3000)));
            using var resp = await _http.GetAsync(BuildUrl(path), cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<T>(
                await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false), JsonOpts);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[Monitor] GET {Path} 失敗", path);
            return null;
        }
    }

    private async Task<(T? Value, string? Error)> PostJsonAsync<T>(
        string path, object body, int timeoutSec, CancellationToken ct) where T : class
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
            using var resp = await _http.PostAsJsonAsync(BuildUrl(path), body, cts.Token)
                .ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
                return (JsonSerializer.Deserialize<T>(text, JsonOpts), null);

            // ProblemDetails 的 detail 才是講得清楚的那句，優先拿它顯示給現場。
            try
            {
                var doc = JsonSerializer.Deserialize<JsonElement>(text);
                if (doc.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String)
                    return (null, d.GetString());
            }
            catch { /* 不是 ProblemDetails */ }
            return (null, $"HTTP {(int)resp.StatusCode}：{text}");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[Monitor] POST {Path} 失敗", path);
            return (null, ex.Message);
        }
    }

    /// <summary>各用途的模型池。連不上回 null。</summary>
    public async Task<ModelPoolsDto?> GetPoolsAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(_options.HealthTimeoutMs, 3000)));
            using var resp = await _http.GetAsync(BuildUrl("api/models/pools"), cts.Token)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<ModelPoolsDto>(
                await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false), JsonOpts);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[Monitor] 取模型池失敗: {BaseUrl}", _options.BaseUrl);
            return null;
        }
    }

    /// <summary>
    /// 切換某用途的現用版本。成功回 null；失敗回**可直接顯示給現場看的**訊息
    /// （server 的 ProblemDetails detail 優先，這樣「版本不存在」之類的原因才傳得到畫面上）。
    /// </summary>
    public async Task<string?> SetCurrentVersionAsync(
        string task, string version, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));   // 雙 head 切版要載 ONNX，給寬一點
            using var resp = await _http.PostAsJsonAsync(
                BuildUrl($"api/models/{task}/current"), new { version }, cts.Token).ConfigureAwait(false);

            var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return null;

            try
            {
                var doc = JsonSerializer.Deserialize<JsonElement>(body);
                if (doc.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String)
                    return d.GetString();
            }
            catch { /* 不是 ProblemDetails 就退回原文 */ }
            return $"HTTP {(int)resp.StatusCode}：{body}";
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[Monitor] 切版失敗 {Task}/{Version}", task, version);
            return ex.Message;
        }
    }

    private string BuildUrl(string path)
    {
        var base_ = (_options.BaseUrl ?? "").TrimEnd('/');
        return $"{base_}/{path}";
    }
}

/// <summary><c>GET /api/infer/recent</c> 的回應。</summary>
public sealed class RecentInferenceDto
{
    public long TotalReceived { get; set; }
    public List<RecentInferenceItem> Items { get; set; } = new();
}

/// <summary>一筆收件紀錄。</summary>
public sealed class RecentInferenceItem
{
    /// <summary>站端的單片識別碼（兩邊 log 對帳用）。舊版站端沒帶時為 null。</summary>
    public string? PieceId { get; set; }

    /// <summary>站端的觸發時刻（TickCount64）；沒帶為 0。</summary>
    public long TrigTick { get; set; }

    public long Seq { get; set; }

    /// <summary>只有時分秒（清單顯示用）。</summary>
    public string? Time { get; set; }

    /// <summary>完整時間戳（單筆詳細頁顯示年月日用）。</summary>
    public DateTime Timestamp { get; set; }

    public string? Task { get; set; }
    public string? StationId { get; set; }
    public string? Reading { get; set; }
    public bool HasReading { get; set; }
    public bool NeedsReview { get; set; }
    public long ReceivedBytes { get; set; }
    public bool IsStrip { get; set; }
    public string? ModelVersion { get; set; }
    public int ElapsedMs { get; set; }
    public int EngineMs { get; set; }
    public string? EdgeRawPath { get; set; }

    /// <summary>父端留存這張影像的路徑（沒開留存就是 null）。</summary>
    public string? SavedImagePath { get; set; }

    /// <summary>是否有留存影像可看。</summary>
    public bool HasImage { get; set; }

    public bool Ok { get; set; }
    public string? Error { get; set; }
}

/// <summary>影像留存設定與現況。</summary>
public sealed class ReceivedImageSettingsInfo
{
    public bool Save { get; set; }
    public string? Folder { get; set; }
    public int MaxFiles { get; set; }
    public int SavedCount { get; set; }
    public long SavedBytes { get; set; }
}

/// <summary><c>GET /api/models/pools</c> 的回應。</summary>
public sealed class ModelPoolsDto
{
    public List<ModelPoolItem> Pools { get; set; } = new();
}

/// <summary>一個用途的模型池。</summary>
public sealed class ModelPoolItem
{
    public string? Task { get; set; }

    /// <summary>所屬**站點**代號（moldcode／gongmu／defect）——同站點的多個引擎共用一張卡。</summary>
    public string? GroupKey { get; set; }

    /// <summary>站點顯示名（模號穴號／公母模／瑕疵檢查）。</summary>
    public string? GroupName { get; set; }

    /// <summary>這個用途在站點裡扮演的引擎（CRNN 字元式／雙 head 分類…）。</summary>
    public string? EngineName { get; set; }

    public string? DisplayName { get; set; }
    public string? Root { get; set; }
    public bool RootExists { get; set; }
    public List<string> RequiredFiles { get; set; } = new();
    public List<string> Versions { get; set; } = new();
    public string? CurrentVersion { get; set; }
    public List<ModelPoolLoaded> LoadedVersions { get; set; } = new();
    public bool InferReady { get; set; }
    public bool CanSwitch { get; set; }
    public string? Note { get; set; }
}

/// <summary>池中一個已載入的版本。</summary>
public sealed class ModelPoolLoaded
{
    public string? Version { get; set; }
    public bool Ready { get; set; }
}

/// <summary>訓練功能現況。</summary>
public sealed class TrainingStatusInfo
{
    public bool Enabled { get; set; }
    public bool Busy { get; set; }
    public string? DatasetRoot { get; set; }
    public string? OutputRoot { get; set; }
    public int MinImages { get; set; }
    public int Epochs { get; set; }
    public string? Device { get; set; }
    public bool CrnnEntryReady { get; set; }
    public bool YoloEntryReady { get; set; }
    public bool RehearsalReady { get; set; }
    public double CrnnMinSelectedAccuracy { get; set; }
    public double CrnnMaxRehearsalRegression { get; set; }
    public double YoloMinTargetRecall { get; set; }
    public double YoloMaxFalsePositiveRate { get; set; }
    public string? Note { get; set; }
}

public sealed class TrainingDatasetList
{
    public string? Root { get; set; }
    public List<TrainingDatasetInfo> Datasets { get; set; } = new();
}

public sealed class TrainingDatasetInfo
{
    public string? Name { get; set; }
    public string? Path { get; set; }
    public int ImageCount { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class TrainingRunList
{
    public bool Busy { get; set; }
    public List<TrainingRunInfo> Runs { get; set; } = new();
}

/// <summary>一次訓練的狀態。</summary>
public sealed class TrainingRunInfo
{
    public string? Id { get; set; }
    public string? Task { get; set; }
    public string? Head { get; set; }
    public string? Dataset { get; set; }
    public int ImageCount { get; set; }
    public string? Notes { get; set; }
    public string? OutputPath { get; set; }
    public string? State { get; set; }
    public int Progress { get; set; }
    public string? Stage { get; set; }
    public string? Message { get; set; }
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
