using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Microsoft.Extensions.Logging;

namespace AIVision.Presentation.Wpf.Services.Realtime;

/// <summary>
/// 實時檢測的 append-only 事件記錄檔（JSONL），一天一檔：
/// <c>&lt;程式目錄&gt;\logs\realtime_events_YYYYMMDD.jsonl</c>
///
/// <para><b>為什麼要它</b>：畫面上的數字關窗就沒了。驗收、對帳、事後追「那片到底怎麼了」
/// 都要靠這份檔自動撈，不能靠人眼抄。</para>
///
/// <para><b>鐵律</b>：寫檔失敗**絕不能打斷產線**——一律吞例外，而且只警告一次
/// （產線一分鐘幾百片，洗版的 log 沒人會看）。</para>
///
/// <para>⚠ 一律用 <see cref="JsonSerializer"/> 產生，**不要字串拼接**：
/// Windows 路徑的反斜線與中文站號都需要正確跳脫，手拼一定會出事。</para>
/// </summary>
public sealed class RealtimeEventLog
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private bool _warned;

    public RealtimeEventLog(ILogger<RealtimeEventLog>? logger = null) => _logger = logger;

    /// <summary>目前寫到哪個檔（畫面顯示用；還沒寫過就是預測路徑）。</summary>
    public string? CurrentPath { get; private set; }

    public static string ResolvePath(DateTime now) =>
        Path.Combine(AppContext.BaseDirectory, "logs", $"realtime_events_{now:yyyyMMdd}.jsonl");

    public void SessionStart(string station, string? workOrder, string? expM, string? expX,
        int captureWindowMs, int serverBudgetMs) =>
        Write(new Dictionary<string, object?>
        {
            ["event"] = "session_start",
            ["station"] = station,
            ["workOrder"] = workOrder,
            ["expectedMohao"] = expM,
            ["expectedXuehao"] = expX,
            ["captureWindowMs"] = captureWindowMs,
            ["serverBudgetMs"] = serverBudgetMs,
        });

    public void SessionEnd(string station, int triggered, int central, int local,
        int captureFault, int dropped, int pending, bool balanced) =>
        Write(new Dictionary<string, object?>
        {
            ["event"] = "session_end",
            ["station"] = station,
            ["triggered"] = triggered,
            ["central"] = central,
            ["local"] = local,
            ["captureFault"] = captureFault,
            ["dropped"] = dropped,
            ["pending"] = pending,
            // 帳平不平直接落地：事後不必重算就知道那一輪可不可信
            ["balanced"] = balanced,
        });

    /// <summary>一片的完整結果。欄位與 <see cref="PieceRecord"/> 一致，方便兩邊對照。</summary>
    public void Piece(PieceRecord r) =>
        Write(new Dictionary<string, object?>
        {
            ["event"] = "piece",
            ["pieceId"] = r.PieceId,
            ["station"] = r.StationId,
            ["trigTick"] = r.TrigTick,
            ["triggerSource"] = r.TriggerSource,
            ["workOrder"] = r.WorkOrder,
            ["expected"] = $"{r.ExpectedMohao}/{r.ExpectedXuehao}",
            ["reading"] = r.ReadingText,
            ["confMohao"] = Math.Round(r.ConfMohao, 4),
            ["confXuehao"] = Math.Round(r.ConfXuehao, 4),
            ["hasReading"] = r.HasReading,
            ["needsReview"] = r.NeedsReview,
            ["source"] = r.Source,
            ["sourceReason"] = r.SourceReason,
            ["modelVersion"] = r.ModelVersion,
            ["engine"] = r.Engine,
            ["serverMs"] = r.ServerMs,
            ["elapsedMs"] = Math.Round(r.ElapsedMs, 1),
            ["outcome"] = r.Outcome,
            ["outcomeReason"] = r.OutcomeReason,
            ["blown"] = r.Blown,
            ["blowFromTriggerMs"] = r.BlowElapsedFromTriggerMs,
            ["rawPath"] = r.RawPath,
            ["stripPath"] = r.StripPath,
        });

    /// <summary>擷取失誤：整個窗都沒有一幀過閘門。產線上多半＝重複觸發拍照。</summary>
    public void CaptureFault(string station, string pieceId, string source, string reason, int probedFrames) =>
        Write(new Dictionary<string, object?>
        {
            ["event"] = "capture_fault",
            ["station"] = station,
            ["pieceId"] = pieceId,
            ["triggerSource"] = source,
            ["reason"] = reason,
            ["probedFrames"] = probedFrames,
            ["note"] = "不是不良品；未吹氣。產線上多半代表重複觸發拍照，請查觸發訊號。",
        });

    public void TriggerDropped(string station, string pieceId, string source, int totalDropped) =>
        Write(new Dictionary<string, object?>
        {
            ["event"] = "trigger_dropped",
            ["station"] = station,
            ["pieceId"] = pieceId,
            ["triggerSource"] = source,
            ["totalDropped"] = totalDropped,
            ["note"] = "積壓超過上限而洩壓丟棄；產線可能遠快於辨識。",
        });

    private void Write(Dictionary<string, object?> fields)
    {
        try
        {
            var now = DateTime.Now;
            fields["ts"] = now.ToString("o");
            var path = ResolvePath(now);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var line = JsonSerializer.Serialize(fields, JsonOpts);
            lock (_gate)
            {
                // 每行 flush：斷電/當掉時已寫的行仍在（產線稽核不能有「最後幾筆消失」）
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
                CurrentPath = path;
            }
        }
        catch (Exception ex)
        {
            if (!_warned)
            {
                _warned = true;
                _logger?.LogWarning(ex,
                    "[RealtimeEventLog] 事件記錄寫入失敗（**不影響產線**，後續不再重複警告）");
            }
        }
    }
}
