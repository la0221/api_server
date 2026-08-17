using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIVision.MoldCode.Onnx;
using Microsoft.Extensions.Options;

namespace AIVision.Api.Services;

/// <summary>ModelRegistry 設定（appsettings）：每種**用途（task）**一個登錄根目錄與檔案組成。</summary>
public sealed class ModelRegistryOptions
{
    public const string SectionName = "ModelRegistry";

    /// <summary>用途 → 登錄設定。appsettings 沒配時用 <see cref="Defaults"/>。</summary>
    public Dictionary<string, ModelTaskOptions> Tasks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>內建預設（白板三用途 + CRNN 字元式引擎；.pt 檔＝torch 權重，內容檢查依副檔名）。</summary>
    public static Dictionary<string, ModelTaskOptions> Defaults => new(StringComparer.OrdinalIgnoreCase)
    {
        ["ocr_pair"] = new() { Root = @"D:\AIVisionModels\pairs", Files = { "mohao.onnx", "xuehao.onnx" }, DisplayName = "模號穴號 OCR（雙 head）" },
        ["ocr_crnn"] = new() { Root = @"D:\AIVisionModels\ocr_crnn", Files = { "detector.pt", "nonar.pt" }, DisplayName = "模號穴號 OCR（CRNN 字元式）" },
        ["gongmu"] = new() { Root = @"D:\AIVisionModels\gongmu", Files = { "model.onnx" }, DisplayName = "公母模" },
        ["defect"] = new() { Root = @"D:\AIVisionModels\defect", Files = { "model.onnx" }, DisplayName = "瑕疵檢查" },
    };
}

/// <summary>單一用途的登錄設定。</summary>
public sealed class ModelTaskOptions
{
    /// <summary>版本登錄根目錄（每版本一個子資料夾）。</summary>
    public string Root { get; set; } = "";

    /// <summary>一個完整版本必備的檔案清單（如雙 head 的 mohao.onnx+xuehao.onnx）。</summary>
    public List<string> Files { get; set; } = new();

    /// <summary>顯示名稱（UI 用）。</summary>
    public string DisplayName { get; set; } = "";
}

/// <summary>版本內單一檔案的中繼。</summary>
public sealed class ModelFileInfo
{
    public string Name { get; set; } = "";
    public string? Md5 { get; set; }
    public long Bytes { get; set; }
}

/// <summary>登錄夾中一個版本的中繼資訊。</summary>
public sealed class ModelVersionInfo
{
    public string Task { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Published { get; set; }
    public List<ModelFileInfo> Files { get; set; } = new();

    /// <summary>_publish.json 的原始內容（發布溯源）；無檔案為 null。</summary>
    public JsonElement? Publish { get; set; }
}

/// <summary>
/// 模型登錄服務（task 化）：按**用途**掃描/解析/上架版本，並提供 ocr_pair 的按版本辨識器快取。
/// <para>
/// 隔離試模核心（ROADMAP 主項2）：指定版本的辨識器是**獨立實例**，與 baseline
/// （appsettings 的 MoldCodeWarpPolar，DI 單例）互不影響、各自持鎖不互相排隊。
/// </para>
/// <para>gongmu / defect 目前只有「倉庫」能力（列版本/上架/下載）；推論端點待模型到位再開。</para>
/// </summary>
public sealed class ModelRegistryService : IDisposable
{
    /// <summary>版本名白名單（防路徑跳脫）：字母/數字開頭，之後允許點/底線/連字號。</summary>
    private static readonly Regex SafeVersionName = new("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);

    /// <summary>preprocess 段的反序列化規則：鍵不分大小寫、**未知鍵直接拒絕**（打錯參數名要炸在發布，不能默默吞掉）。</summary>
    public static readonly JsonSerializerOptions PreprocessJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    private readonly Dictionary<string, ModelTaskOptions> _tasks;
    private readonly MoldCodeWarpPolarOptions _warpOptions;
    private readonly ILogger<ModelRegistryService>? _logger;

    // ocr_pair 按版本辨識器快取：Lazy 確保同版本並發首呼只建構一次（ONNX 冷載 ~1s）。
    private readonly ConcurrentDictionary<string, Lazy<WarpPolarTwoHeadRecognizer>> _recognizers =
        new(StringComparer.OrdinalIgnoreCase);

    // md5 快取（key = 路徑 + 長度 + 最後寫入時刻）：_publish.json 缺漏時才需計算。
    private readonly ConcurrentDictionary<string, string> _md5Cache = new(StringComparer.OrdinalIgnoreCase);

    // _publish.json 解析快取（key = 路徑 + mtime）：judge/preprocess 段每筆推論都要查，別每次讀檔。
    private readonly ConcurrentDictionary<string, JsonElement> _publishCache = new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    public ModelRegistryService(
        IOptions<ModelRegistryOptions> options,
        IOptions<MoldCodeWarpPolarOptions> warpOptions,
        ILogger<ModelRegistryService>? logger = null)
    {
        _tasks = options.Value.Tasks is { Count: > 0 } t ? t : ModelRegistryOptions.Defaults;
        _warpOptions = warpOptions.Value;
        _logger = logger;
    }

    /// <summary>所有已配置的用途。</summary>
    public IReadOnlyDictionary<string, ModelTaskOptions> Tasks => _tasks;

    /// <summary>用途是否存在。</summary>
    public bool TaskExists(string task) => _tasks.ContainsKey(task ?? "");

    /// <summary>取用途設定；不存在回 null。</summary>
    public ModelTaskOptions? GetTask(string task) =>
        _tasks.TryGetValue(task ?? "", out var t) ? t : null;

    /// <summary>版本名是否合法（防路徑跳脫；上架與解析共用同一把尺）。</summary>
    public static bool IsSafeVersionName(string? version) => SafeVersionName.IsMatch(version ?? "");

    /// <summary>列出某用途中所有「檔案齊全」的版本（依名稱排序）。用途不存在回空。</summary>
    public IReadOnlyList<ModelVersionInfo> ListVersions(string task)
    {
        var list = new List<ModelVersionInfo>();
        var t = GetTask(task);
        if (t is null || !Directory.Exists(t.Root))
            return list;

        foreach (var dir in Directory.GetDirectories(t.Root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var files = t.Files.Select(f => Path.Combine(dir, f)).ToList();
            if (!files.All(File.Exists))
                continue;

            var info = new ModelVersionInfo
            {
                Task = task,
                Version = Path.GetFileName(dir),
                Files = t.Files.Select((f, i) => new ModelFileInfo
                {
                    Name = f,
                    Bytes = new FileInfo(files[i]).Length,
                }).ToList(),
            };

            // _publish.json 有就信它（發布時已算 md5）；缺的檔案才現算。
            var publishPath = Path.Combine(dir, "_publish.json");
            if (File.Exists(publishPath))
            {
                try
                {
                    var doc = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(publishPath));
                    info.Publish = doc;
                    if (doc.TryGetProperty("published", out var p)) info.Published = p.GetString();
                    foreach (var fi in info.Files)
                    {
                        // 相容兩種寫法：本地腳本的 {mohao:{md5}}（鍵=不含副檔名）與上架 API 的 {files:{"mohao.onnx":{md5}}}。
                        var stem = Path.GetFileNameWithoutExtension(fi.Name);
                        if (doc.TryGetProperty(stem, out var byStem) && byStem.TryGetProperty("md5", out var m1))
                            fi.Md5 = m1.GetString();
                        else if (doc.TryGetProperty("files", out var fs) &&
                                 fs.TryGetProperty(fi.Name, out var byName) && byName.TryGetProperty("md5", out var m2))
                            fi.Md5 = m2.GetString();
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[ModelRegistry] _publish.json 解析失敗: {Path}", publishPath);
                }
            }

            for (int i = 0; i < info.Files.Count; i++)
                info.Files[i].Md5 ??= ComputeMd5Cached(files[i]);
            list.Add(info);
        }
        return list;
    }

    /// <summary>解析某用途/版本/檔名的實體路徑。名稱不合法、檔名不在該用途清單、或檔案不存在 → null。</summary>
    public string? ResolveFile(string task, string version, string fileName)
    {
        var t = GetTask(task);
        if (t is null || !IsSafeVersionName(version))
            return null;
        if (!t.Files.Contains(fileName ?? "", StringComparer.OrdinalIgnoreCase))
            return null;

        var path = Path.Combine(t.Root, version, fileName!);
        return File.Exists(path) ? path : null;
    }

    /// <summary>該用途/版本是否檔案齊全。</summary>
    public bool VersionExists(string task, string version)
    {
        var t = GetTask(task);
        return t is not null && t.Files.All(f => ResolveFile(task, version, f) is not null);
    }

    /// <summary>
    /// 取得（必要時建構）ocr_pair 指定版本的辨識器。僅支援 ocr_pair（其他用途丟
    /// <see cref="NotSupportedException"/>）；版本不存在丟 <see cref="FileNotFoundException"/>。
    /// </summary>
    public WarpPolarTwoHeadRecognizer GetOcrPairRecognizer(string version)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var mo = ResolveFile("ocr_pair", version, "mohao.onnx");
        var xu = ResolveFile("ocr_pair", version, "xuehao.onnx");
        if (mo is null || xu is null)
            throw new FileNotFoundException(
                $"登錄夾中找不到版本 '{version}'（需 {GetTask("ocr_pair")?.Root}\\{version}\\{{mohao,xuehao}}.onnx）");

        var lazy = _recognizers.GetOrAdd(version, v => new Lazy<WarpPolarTwoHeadRecognizer>(() =>
        {
            // ③前處理參數外部化（AINavi 借鏡）：版本 _publish.json 有 preprocess 段 → 用它
            //（前處理與模型同版控、同回滾——消滅 train/infer 參數漂移）；沒有 → 沿用 baseline 參數。
            var pre = _warpOptions.Preprocess;
            var source = "baseline(appsettings)";
            if (GetPublishSection("ocr_pair", v, "preprocess") is JsonElement s)
            {
                try
                {
                    pre = JsonSerializer.Deserialize<WarpPolarParams>(s.GetRawText(), PreprocessJsonOpts) ?? pre;
                    source = "_publish.json";
                }
                catch (Exception ex)
                {
                    // 解析不了寧可用 baseline 也不要炸推論；發布端已擋壞 JSON，這裡是最後防線。
                    _logger?.LogWarning(ex, "[ModelRegistry] 版本 {Version} 的 preprocess 段解析失敗，改用 baseline", v);
                }
            }
            _logger?.LogInformation("[ModelRegistry] 冷載 ocr_pair 版本 {Version}（前處理來源={Source}）", v, source);
            return new WarpPolarTwoHeadRecognizer(mo, xu, pre, _warpOptions.Passes);
        }));

        try
        {
            return lazy.Value;
        }
        catch
        {
            // 建構失敗（壞檔等）別把失敗的 Lazy 留在快取，否則修好檔案也載不進來。
            _recognizers.TryRemove(version, out _);
            throw;
        }
    }

    /// <summary>ocr_pair 目前已載入記憶體的版本清單。</summary>
    public IReadOnlyList<string> CachedOcrPairVersions =>
        _recognizers.Where(kv => kv.Value.IsValueCreated).Select(kv => kv.Key).ToList();

    /// <summary>
    /// 讀某版本 _publish.json 的一個物件段落（如 <c>judge</c>＝per-class 判定門檻、
    /// <c>preprocess</c>＝前處理參數——AINavi 借鏡②③：判定規則與前處理**跟模型一起版控**）。
    /// 無檔案/無該段/解析失敗 → null。有 mtime 快取，可安心每筆推論查。
    /// </summary>
    public JsonElement? GetPublishSection(string task, string version, string section)
    {
        var t = GetTask(task);
        if (t is null || !IsSafeVersionName(version))
            return null;
        var path = Path.Combine(t.Root, version, "_publish.json");
        try
        {
            if (!File.Exists(path)) return null;
            var fi = new FileInfo(path);
            var key = $"{path}|{fi.LastWriteTimeUtc.Ticks}";
            var doc = _publishCache.GetOrAdd(key, _ =>
                JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path)));
            if (doc.TryGetProperty(section, out var s) && s.ValueKind == JsonValueKind.Object)
                return s;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[ModelRegistry] _publish.json 段落讀取失敗: {Path} §{Section}", path, section);
        }
        return null;
    }

    /// <summary>計算檔案 md5（小寫 hex）。</summary>
    public static string ComputeMd5(string path)
    {
        using var md5 = MD5.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(md5.ComputeHash(fs)).ToLowerInvariant();
    }

    private string? ComputeMd5Cached(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            var key = $"{path}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
            return _md5Cache.GetOrAdd(key, _ => ComputeMd5(path));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[ModelRegistry] md5 計算失敗: {Path}", path);
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var kv in _recognizers)
            if (kv.Value.IsValueCreated)
                kv.Value.Value.Dispose();
        _recognizers.Clear();
    }
}
