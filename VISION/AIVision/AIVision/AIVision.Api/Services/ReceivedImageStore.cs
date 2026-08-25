using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace AIVision.Api.Services;

/// <summary>收到影像的留存設定（appsettings <c>ReceivedImages</c>）。</summary>
public sealed class ReceivedImageOptions
{
    public const string SectionName = "ReceivedImages";

    /// <summary>
    /// 是否把各站送來的影像存到本機。**預設 false**——原圖本來就留在站端，
    /// 父端只收前處理後的小圖；要不要再留一份是選項，不是預設行為
    /// （開了才會佔磁碟，30 張約 1MB）。可在父端監控畫面即時切換。
    /// </summary>
    public bool Save { get; set; } = false;

    /// <summary>存放資料夾。相對路徑以**程式目錄**為基準。</summary>
    public string Folder { get; set; } = "received";

    /// <summary>保留張數上限；超過就刪最舊的（避免長期跑爆磁碟）。</summary>
    public int MaxFiles { get; set; } = 5000;
}

/// <summary>
/// 父端收到影像的留存（2026-08-19）。
///
/// <para><b>為什麼是選項不是預設</b>：Route A 的設計就是「原圖留站端、只送前處理小圖」，
/// 父端本來不需要留檔。但現場要確認「到底收到什麼」、或要事後回頭看那張圖長怎樣時，
/// 就得有一份實體檔案。所以做成**可在畫面上即時開關**的選項，預設關。</para>
///
/// <para>檔名 <c>yyyyMMdd\HHmmss_fff_站號_序號.png</c>——一天一夾，好清也好找。
/// 寫檔失敗絕不影響推論（同站端事件 log 的鐵律）。</para>
/// </summary>
public sealed class ReceivedImageStore
{
    private readonly ReceivedImageOptions _options;
    private readonly ILogger<ReceivedImageStore>? _logger;
    private readonly object _gate = new();
    private bool _warned;
    private long _counter;

    /// <summary>執行期覆寫的開關；null = 用 appsettings 的值。</summary>
    private bool? _volatileSave;

    public ReceivedImageStore(
        IOptions<ReceivedImageOptions> options,
        ILogger<ReceivedImageStore>? logger = null)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>目前是否留存。</summary>
    public bool Save => _volatileSave ?? _options.Save;

    /// <summary>存放根目錄（已解析成絕對路徑）。</summary>
    public string Folder => ResolveFolder(_options.Folder);

    public int MaxFiles => Math.Max(1, _options.MaxFiles);

    /// <summary>執行期切換（父端畫面用）。⚠ 只影響本次執行；永久生效請改 appsettings。</summary>
    public void SetSave(bool save)
    {
        _volatileSave = save;
        _logger?.LogInformation("[ReceivedImages] 留存收到的影像 = {Save}（執行期，未寫回 appsettings）", save);
    }

    /// <summary>目前已留存的張數與總大小（畫面顯示用；資料夾不存在回 0）。</summary>
    public (int Count, long Bytes) Stat()
    {
        try
        {
            if (!Directory.Exists(Folder)) return (0, 0);
            var files = Directory.EnumerateFiles(Folder, "*.png", SearchOption.AllDirectories).ToList();
            long bytes = 0;
            foreach (var f in files)
            {
                try { bytes += new FileInfo(f).Length; } catch { /* 檔案剛好被刪 */ }
            }
            return (files.Count, bytes);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// 存一張。未啟用或失敗都回 null（呼叫端不必處理例外——留存失敗不該讓推論失敗）。
    /// </summary>
    public async Task<string?> SaveAsync(
        byte[] pngBytes, string stationId, CancellationToken ct = default)
    {
        if (!Save || pngBytes is null || pngBytes.Length == 0) return null;
        try
        {
            var now = DateTime.Now;
            var dir = Path.Combine(Folder, now.ToString("yyyyMMdd"));
            Directory.CreateDirectory(dir);

            var safeStation = SanitizeFileName(stationId);
            var n = System.Threading.Interlocked.Increment(ref _counter);
            var path = Path.Combine(dir, $"{now:HHmmss_fff}_{safeStation}_{n}.png");
            await File.WriteAllBytesAsync(path, pngBytes, ct).ConfigureAwait(false);

            TrimOldFiles();
            return path;
        }
        catch (Exception ex)
        {
            if (!_warned)
            {
                _warned = true;
                _logger?.LogWarning(ex, "[ReceivedImages] 留存失敗（不影響推論，後續不再重複警告）");
            }
            return null;
        }
    }

    /// <summary>超過上限就刪最舊的。刪不掉不影響主流程。</summary>
    private void TrimOldFiles()
    {
        lock (_gate)
        {
            try
            {
                if (!Directory.Exists(Folder)) return;
                var files = Directory.EnumerateFiles(Folder, "*.png", SearchOption.AllDirectories)
                    .Select(f => new FileInfo(f))
                    .ToList();
                if (files.Count <= MaxFiles) return;

                foreach (var f in files.OrderBy(f => f.CreationTimeUtc).Take(files.Count - MaxFiles))
                {
                    try { f.Delete(); } catch { /* 正被讀取就下次再刪 */ }
                }
            }
            catch { /* 清理失敗不影響留存本身 */ }
        }
    }

    /// <summary>站號可能含使用者輸入，擋掉檔名非法字元與路徑跳脫。</summary>
    private static string SanitizeFileName(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "unknown";
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(s.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return cleaned.Length == 0 ? "unknown" : cleaned;
    }

    private static string ResolveFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            folder = "received";
        try
        {
            return Path.IsPathRooted(folder)
                ? folder
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, folder));
        }
        catch
        {
            return folder;
        }
    }
}
