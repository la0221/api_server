using System;
using System.Collections.Generic;
using System.Linq;

namespace AIVision.Api.Services;

/// <summary>
/// 最近辨識紀錄（記憶體環狀緩衝，2026-08-19）。
///
/// <para><b>為什麼要有</b>：父端監控畫面原本只看得到「服務活著/模型載入了」，
/// 就算真的收到圖、辨識完、回了值，畫面上也一片空白——現場<b>無法確認父端到底有沒有收到</b>，
/// 只能去翻 console 視窗。這個 store 讓每筆進來的推論都留下痕跡，父端畫面直接照出來。</para>
///
/// <para><b>刻意只放記憶體</b>：這是「看得到現在在收什麼」的監看用途，不是稽核帳。
/// 真正要留存的驗收紀錄在站端的 <c>routeA_events_*.jsonl</c>（原圖也在站端）。
/// 重啟即清空是預期行為，換來零磁碟寫入、不影響推論節拍。</para>
/// </summary>
public sealed class RecentInferenceStore
{
    /// <summary>保留筆數上限。夠現場往回看一輪送檢（一批 30 張）好幾遍。</summary>
    private const int Capacity = 300;

    private readonly object _gate = new();
    private readonly LinkedList<RecentInferenceEntry> _items = new();
    private long _seq;

    /// <summary>server 啟動以來累計收到的推論筆數（不受保留上限影響）。</summary>
    public long TotalReceived { get; private set; }

    /// <summary>記一筆。新的排前面；超過上限丟最舊的。</summary>
    public void Add(RecentInferenceEntry entry)
    {
        lock (_gate)
        {
            entry.Seq = ++_seq;
            TotalReceived = _seq;
            _items.AddFirst(entry);
            while (_items.Count > Capacity) _items.RemoveLast();
        }
    }

    /// <summary>依流水號取單筆（單筆詳細頁／取圖用）；找不到回 null。</summary>
    public RecentInferenceEntry? TryGet(long seq)
    {
        lock (_gate)
            return _items.FirstOrDefault(i => i.Seq == seq);
    }

    /// <summary>取最近 N 筆（新→舊）。</summary>
    public IReadOnlyList<RecentInferenceEntry> Take(int count)
    {
        lock (_gate)
            return _items.Take(Math.Clamp(count, 1, Capacity)).ToList();
    }

    /// <summary>清空（現場想從乾淨畫面開始觀察時用）。</summary>
    public void Clear()
    {
        lock (_gate) _items.Clear();
    }
}

/// <summary>一筆父端實際收到並處理過的推論。</summary>
public sealed class RecentInferenceEntry
{
    /// <summary>流水號（server 啟動後遞增；供前端判斷有沒有新資料）。</summary>
    public long Seq { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>用途（ocr_crnn / ocr_pair …）——各用途共用這個看板。</summary>
    public string Task { get; set; } = "";

    /// <summary>
    /// 站端的**單片識別碼**（<c>{站號}_{yyyyMMdd}_{流水}</c>）。
    /// <para>兩邊 log 對帳的鑰匙：站端的 records\ 三件套、事件 log、吹氣去重都用同一個 id，
    /// 父端有了它才回得出「這筆是站端哪一片」。站端沒帶就是空字串（舊版站端）。</para>
    /// </summary>
    public string? PieceId { get; set; }

    /// <summary>站端的觸發時刻（TickCount64）。供事後算真實延遲；沒帶為 0。</summary>
    public long TrigTick { get; set; }

    /// <summary>送檢的站台（站端原樣帶上來；沒帶就是 "-"）。</summary>
    public string StationId { get; set; } = "-";

    /// <summary>讀值（模號/穴號）；讀不到就寫原因。</summary>
    public string Reading { get; set; } = "-";

    public bool HasReading { get; set; }
    public bool NeedsReview { get; set; }

    /// <summary>本機實際收到的影像位元組數（＝站端送出量，用來對帳傳輸量縮減）。</summary>
    public long ReceivedBytes { get; set; }

    /// <summary>站端是否已完成前處理（true = 父端只做辨識）。</summary>
    public bool IsStrip { get; set; }

    /// <summary>實際服務這筆的模型版本。</summary>
    public string? ModelVersion { get; set; }

    /// <summary>父端整段耗時（毫秒）。</summary>
    public int ElapsedMs { get; set; }

    /// <summary>推論引擎內部耗時（毫秒）。</summary>
    public int EngineMs { get; set; }

    /// <summary>該張原圖在**站端**的位置（原圖不上傳，只帶路徑做溯源）。</summary>
    public string? EdgeRawPath { get; set; }

    /// <summary>
    /// 本機留存這張影像的檔案路徑；null = 沒留（預設不留，見 <see cref="ReceivedImageStore"/>）。
    /// </summary>
    public string? SavedImagePath { get; set; }

    /// <summary>這筆是否正常完成（false = sidecar 失敗等）。</summary>
    public bool Ok { get; set; } = true;

    /// <summary>失敗原因（Ok=false 時）。</summary>
    public string? Error { get; set; }
}
