using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AIVision.Presentation.Wpf.Services.Realtime;

/// <summary>
/// 一片三件套的落地：<c>原圖 / 前處理圖 / 結果 json</c>，統一用 pieceId 命名。
///
/// <code>
/// &lt;根目錄&gt;\2026-08-24\
///   ST-01_20260824_000123_raw.jpg      原圖（**只存 ROI 區域**，2026-08-25 起；ROI 外是機構背景，存了浪費空間）
///   ST-01_20260824_000123_strip.png    前處理圖（實際送父端的那一張）
///   ST-01_20260824_000123.json         結果
/// </code>
///
/// <para><b>鐵律</b>：存檔失敗（磁碟滿、唯讀、路徑不存在）**絕不能影響判定與吹氣**。
/// 一律吞例外、只記一次 warning —— 存圖是稽核與訓練用途，不是產線判定的一部分。</para>
/// </summary>
public sealed class PieceRecordStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        // 中文站號/工單/路徑不要被跳脫成 \uXXXX，現場要人眼看得懂
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger? _logger;
    private bool _warned;

    public PieceRecordStore(RealtimeInspectionOptions options, ILogger? logger = null)
    {
        Options = options;
        _logger = logger;
    }

    public RealtimeInspectionOptions Options { get; }

    /// <summary>紀錄根目錄（設定沒填就用 <c>&lt;程式目錄&gt;\records</c>）。</summary>
    public string Root => Options.ResolvedRecordRoot;

    /// <summary>當天的資料夾。</summary>
    public string DayDir(DateTime t) => Path.Combine(Root, t.ToString("yyyyMMdd"));

    /// <summary>
    /// 存一片。回傳寫進 <paramref name="record"/> 的相對路徑（沒存成功就是 null）。
    /// 影像參數為 null 代表那一張不存（例如設定關掉了原圖）。
    /// </summary>
    public async Task SaveAsync(
        PieceRecord record, byte[]? rawJpeg, byte[]? stripPng, CancellationToken ct)
    {
        await SaveRawAsync(record, rawJpeg, ct).ConfigureAwait(false);
        await SaveStripAsync(record, stripPng, ct).ConfigureAwait(false);
        await SaveJsonAsync(record, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 先存原圖。
    /// <para>⚠ **必須在送父端之前呼叫**：送出去時要一起帶「原圖在站端的完整路徑」，
    /// 父端的「站端原圖位置」欄靠它溯源。等到最後才存的話，送出去時 <c>RawPath</c> 還是 null，
    /// 父端就永遠查不回這張圖在站端哪裡。</para>
    /// </summary>
    public Task SaveRawAsync(PieceRecord record, byte[]? rawJpeg, CancellationToken ct) =>
        WriteImageAsync(record, rawJpeg, Options.SaveRawImage, "_raw", ".jpg",
            (r, path, len) => { r.RawPath = path; r.RawBytes = len; }, ct);

    /// <summary>存前處理圖（實際送出去的那張）。</summary>
    public Task SaveStripAsync(PieceRecord record, byte[]? stripPng, CancellationToken ct) =>
        WriteImageAsync(record, stripPng, Options.SaveStripImage, "_strip", ".png",
            (r, path, len) => { r.StripPath = path; r.StripBytes = len; }, ct);

    /// <summary>最後才寫結果 json（此時判定與吹氣都已定案）。</summary>
    public async Task SaveJsonAsync(PieceRecord record, CancellationToken ct)
    {
        try
        {
            var dir = DayDir(record.Timestamp);
            Directory.CreateDirectory(dir);
            var jsonPath = Unique(dir, record.PieceId, ".json");
            await File.WriteAllTextAsync(
                jsonPath, JsonSerializer.Serialize(record, JsonOpts), Encoding.UTF8, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Warn(ex); }
    }

    private async Task WriteImageAsync(
        PieceRecord record, byte[]? bytes, bool enabled, string suffix, string ext,
        Action<PieceRecord, string, int> apply, CancellationToken ct)
    {
        if (bytes is not { Length: > 0 } || !enabled) return;
        try
        {
            var dir = DayDir(record.Timestamp);
            Directory.CreateDirectory(dir);
            var path = Unique(dir, record.PieceId + suffix, ext);
            await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
            apply(record, path, bytes.Length);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Warn(ex);
        }
    }

    /// <summary>只吵一次：產線一分鐘幾百片，洗版的 log 沒有人會看。</summary>
    private void Warn(Exception ex)
    {
        if (_warned) return;
        _warned = true;
        _logger?.LogWarning(ex,
            "[PieceRecord] 存檔失敗（**不影響判定與吹氣**，後續不再重複警告）。根目錄 {Root}", Root);
    }

    /// <summary>同名時加尾碼，**永不覆蓋**既有紀錄（重啟同分鐘、流水掃描失敗都可能撞名）。</summary>
    private static string Unique(string dir, string baseName, string ext)
    {
        var path = Path.Combine(dir, baseName + ext);
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(dir, $"{baseName}({i}){ext}");
        return path;
    }
}
