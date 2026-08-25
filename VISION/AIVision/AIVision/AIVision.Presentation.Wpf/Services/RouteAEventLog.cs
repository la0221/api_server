using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Microsoft.Extensions.Logging;

namespace AIVision.Presentation.Wpf.Services;

/// <summary>
/// 站端送檢事件記錄檔（append-only JSONL，2026-08-19 需求 1）。
///
/// <para><b>為什麼要有</b>：8/19 跨機實測時，送達數與延遲可以從既有 log 自動撈，
/// 但<b>讀值</b>與<b>傳輸量縮減</b>撈不到——它們只活在畫面的記憶體裡，關掉視窗就沒了，
/// 只能靠人眼抄。驗收數據不該依賴人眼抄寫；有了這份檔，跨機驗收可以完全不看畫面自動回填、事後可稽核可重算。</para>
///
/// <para><b>格式</b>：一行一事件，沿用 POC 的 <c>child_events.jsonl</c> 格式。
/// 三種事件：<c>batch_start</c> / <c>item</c>（主體，每張一行）/ <c>batch_end</c>（統計卡數字直接落地）。</para>
///
/// <para><b>鐵律</b>：寫檔失敗<b>絕不能打斷送檢</b>——全部包 try/catch 只記 warning。
/// 記錄是為了驗收，不是產線流程的一環。</para>
///
/// <para>⚠ JSON 一律走 <see cref="JsonSerializer"/> 產生，不要自己拼字串：
/// Windows 路徑的反斜線沒跳脫就是壞 JSON，事後解析不了＝等於沒記（POC 的 <c>_log.bat</c> 踩過）。</para>
/// </summary>
public sealed class RouteAEventLog
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        // 中文站號/檔名不要被轉成 \uXXXX，人要看得懂；同時保留 JSON 合法性。
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = false,
    };

    private readonly ILogger<RouteAEventLog>? _logger;
    private readonly object _gate = new();
    private bool _warned;

    public RouteAEventLog(ILogger<RouteAEventLog>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>最近一次實際寫入的檔案路徑（畫面上顯示給現場看，知道去哪撈）。</summary>
    public string? CurrentPath { get; private set; }

    /// <summary>今天的記錄檔路徑：<c>&lt;程式目錄&gt;\logs\routeA_events_YYYYMMDD.jsonl</c>
    /// （與既有 <c>logs\AIVision_*.log</c> 同層，維運只要抓一個資料夾）。</summary>
    public static string ResolvePath(DateTime now) =>
        Path.Combine(AppContext.BaseDirectory, "logs",
            $"routeA_events_{now:yyyyMMdd}.jsonl");

    /// <summary>批次開始。</summary>
    public void BatchStart(string station, string folder, int total, string server)
    {
        Write(new Dictionary<string, object?>
        {
            ["event"] = "batch_start",
            ["station"] = station,
            ["folder"] = folder,
            ["total"] = total,
            ["server"] = server,
        });
    }

    /// <summary>
    /// 單張結果——這是主體。<paramref name="source"/> 固定 <c>central</c> / <c>local</c>
    /// （不寫中文，方便腳本解析；中央掉線走本機備援時**照樣要寫**，C1 那類驗收才撈得到）。
    /// </summary>
    public void Item(
        string station, int index, string file, string? rawPath, string reading,
        string source, bool ok, bool needsReview, bool preprocessed,
        long rawBytes, long sentBytes, double elapsedMs, int serverMs)
    {
        Write(new Dictionary<string, object?>
        {
            ["event"] = "item",
            ["station"] = station,
            ["index"] = index,
            ["file"] = file,
            ["rawPath"] = rawPath,
            ["reading"] = reading,
            ["source"] = source,
            ["ok"] = ok,
            ["needsReview"] = needsReview,
            ["preprocessed"] = preprocessed,
            ["rawBytes"] = rawBytes,
            ["sentBytes"] = sentBytes,
            ["reductionPct"] = rawBytes > 0
                ? Math.Round(-(1 - (double)sentBytes / rawBytes) * 100, 1)
                : (double?)null,
            ["elapsedMs"] = Math.Round(elapsedMs, 1),
            ["serverMs"] = serverMs,
        });
    }

    /// <summary>
    /// 這張連送都沒送出去（讀檔／解碼／前處理就掛了）。用獨立事件名，
    /// 解析腳本照樣只挑 <c>event=="item"</c> 算指標，但檔案裡不會出現「憑空少幾張」。
    /// </summary>
    public void ItemFailed(string station, int index, string file, string? rawPath, string reason)
    {
        Write(new Dictionary<string, object?>
        {
            ["event"] = "item_failed",
            ["station"] = station,
            ["index"] = index,
            ["file"] = file,
            ["rawPath"] = rawPath,
            ["reason"] = reason,
        });
    }

    /// <summary>批次結束：統計卡上的數字直接落地，驗收表可整段複製。</summary>
    /// <param name="captureFault">**擷取失誤**張數（找不到圓）。不是不良品——產線上代表**重複觸發拍照**
    /// 造成擷取到不該擷取的畫面。既沒送中央也不是本機接管，要單獨記，
    /// 否則 total 對不起來、彙總腳本算出來的比例會失真；混進 NG 更會被誤會成品質問題。</param>
    public void BatchEnd(
        string station, int total, int serverOk, int fallback, int localRead,
        double rawKb, double sentKb, double? latencyP50, double? latencyP90, bool stopped,
        int captureFault = 0)
    {
        Write(new Dictionary<string, object?>
        {
            ["event"] = "batch_end",
            ["station"] = station,
            ["total"] = total,
            ["serverOk"] = serverOk,
            ["fallback"] = fallback,
            ["localRead"] = localRead,
            ["captureFault"] = captureFault,
            ["rawKb"] = Math.Round(rawKb, 1),
            ["sentKb"] = Math.Round(sentKb, 1),
            ["reductionPct"] = rawKb > 0 ? Math.Round(-(1 - sentKb / rawKb) * 100, 1) : (double?)null,
            ["latencyP50"] = latencyP50 is double p50 ? Math.Round(p50, 1) : null,
            ["latencyP90"] = latencyP90 is double p90 ? Math.Round(p90, 1) : null,
            ["stopped"] = stopped,
        });
    }

    /// <summary>寫一行。任何失敗都吞掉（只記一次 warning），產線流程不受影響。</summary>
    private void Write(Dictionary<string, object?> fields)
    {
        try
        {
            var now = DateTime.Now;
            // ts 放第一個鍵：ISO 8601 本地時間含毫秒（用 sortable 排序＝檔內即時序）。
            var ordered = new Dictionary<string, object?>
            {
                ["ts"] = now.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture),
            };
            foreach (var kv in fields) ordered[kv.Key] = kv.Value;

            var line = JsonSerializer.Serialize(ordered, JsonOpts);
            var path = ResolvePath(now);

            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                // AppendAllText 每次開關檔 = 寫完即落地：程式當掉也保住已寫的行。
                File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
                CurrentPath = path;
            }
        }
        catch (Exception ex)
        {
            if (!_warned)
            {
                _warned = true;
                _logger?.LogWarning(ex, "[RouteAEventLog] 事件記錄寫入失敗（不影響送檢，後續不再重複警告）");
            }
        }
    }
}
