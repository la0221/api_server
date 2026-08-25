using System;
using System.Collections.Concurrent;
using System.Threading;

namespace AIVision.Presentation.Wpf.Services.Realtime;

/// <summary>
/// 一筆待處理的觸發。觸發當下**只記時刻**，不拍照——影像稍後從 <see cref="FrameRing"/> 回頭找。
/// </summary>
public sealed class PendingCapture
{
    /// <summary>單片識別碼，一路貫穿存檔／送父端／吹氣去重／事件 log。</summary>
    public required string PieceId { get; init; }

    /// <summary>觸發時刻（<see cref="Environment.TickCount64"/>）。</summary>
    public required long TrigTick { get; init; }

    /// <summary>擷取窗截止時刻＝<see cref="TrigTick"/> + 窗長。過了還沒找到就是擷取失誤。</summary>
    public required long Deadline { get; init; }

    /// <summary>觸發來源（IO／手動），只給 log 看。</summary>
    public required string Source { get; init; }

    /// <summary>這筆已經探到哪一幀了（避免同一幀重複探）。</summary>
    public long Cursor { get; set; }

    /// <summary>建立時的牆鐘時間（存檔與紀錄用）。</summary>
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    /// <summary>
    /// 這筆是否已經記過帳（中央／本機／擷取失誤三選一）。
    /// <para>處理中途被停止打斷時，要靠它判斷「該不該再記一筆中斷」——
    /// 不看的話會兩邊都記，帳就多出一筆而假性不平。</para>
    /// </summary>
    public bool Counted { get; set; }
}

/// <summary>
/// 觸發佇列 + 帳目計數。
///
/// <para><b>帳一定要平</b>（移植自相機版的習慣，停止時會檢查）：</para>
/// <code>
/// 觸發次數 = 中央判定 + 本機接管 + 擷取失誤 + 停止時中斷 + 還在佇列
/// </code>
/// <para>（積壓丟棄另計，**不列入觸發數**——那是連排隊都沒排到的。）</para>
/// <para>不平就代表有一條路沒記帳 —— 那種漏洞在產線上會變成「明明有片子卻查不到紀錄」。</para>
/// </summary>
public sealed class TriggerQueue
{
    /// <summary>積壓上限。超過就洩壓丟棄，否則產線遠快於辨識時會越積越多、記憶體與延遲一起爆。</summary>
    public const int MaxPending = 20;

    private readonly ConcurrentQueue<PendingCapture> _queue = new();

    private int _triggered;
    private int _central;
    private int _local;
    private int _captureFault;
    private int _dropped;
    private int _interrupted;

    /// <summary>累計觸發次數（承諾要有交代的筆數）。</summary>
    public int Triggered => Volatile.Read(ref _triggered);

    /// <summary>由中央（父端）判定的片數。</summary>
    public int Central => Volatile.Read(ref _central);

    /// <summary>本機模型接管的片數（父端逾時／不可用）。</summary>
    public int Local => Volatile.Read(ref _local);

    /// <summary>擷取失誤：整個窗內都沒有一幀過閘門。產線上多半＝重複觸發拍照。</summary>
    public int CaptureFault => Volatile.Read(ref _captureFault);

    /// <summary>積壓爆掉丟棄的觸發（未列入 <see cref="Triggered"/>）。</summary>
    public int Dropped => Volatile.Read(ref _dropped);

    /// <summary>停止時處理到一半被打斷的片數（正常關機會有 0~1 筆）。</summary>
    public int Interrupted => Volatile.Read(ref _interrupted);

    /// <summary>還在排隊等處理的筆數。</summary>
    public int Pending => _queue.Count;

    /// <summary>帳平不平。停止時檢查，不平要在畫面出聲。</summary>
    public bool Balanced => Triggered == Central + Local + CaptureFault + Interrupted + Pending;

    /// <summary>帳目一行文（畫面與 log 共用）。</summary>
    public string Ledger =>
        $"觸發 {Triggered} ＝ 中央 {Central} ＋ 本機 {Local} ＋ 擷取失誤 {CaptureFault} ＋ 待補 {Pending}"
        + (Interrupted > 0 ? $" ＋ 停止時中斷 {Interrupted}" : "")
        + (Dropped > 0 ? $"　另積壓丟棄 {Dropped}（未列入觸發數）" : "")
        + (Balanced ? "" : "　⚠ 帳不平！有一條路沒記帳，請查 log");

    /// <summary>
    /// 排入一次觸發。回 null 代表積壓爆掉被丟棄（已計入 <see cref="Dropped"/>）。
    /// </summary>
    public PendingCapture? Enqueue(string pieceId, long trigTick, long windowMs, string source)
    {
        if (_queue.Count >= MaxPending)
        {
            Interlocked.Increment(ref _dropped);
            return null;
        }
        var job = new PendingCapture
        {
            PieceId = pieceId,
            TrigTick = trigTick,
            Deadline = trigTick + windowMs,
            Source = source,
            Cursor = 0,
        };
        Interlocked.Increment(ref _triggered);
        _queue.Enqueue(job);
        return job;
    }

    public bool TryPeek(out PendingCapture job) => _queue.TryPeek(out job!);

    public bool TryDequeue(out PendingCapture job) => _queue.TryDequeue(out job!);

    public void MarkCentral() => Interlocked.Increment(ref _central);
    public void MarkLocal() => Interlocked.Increment(ref _local);
    public void MarkCaptureFault() => Interlocked.Increment(ref _captureFault);
    public void MarkInterrupted() => Interlocked.Increment(ref _interrupted);

    public void Reset()
    {
        while (_queue.TryDequeue(out _)) { }
        Volatile.Write(ref _triggered, 0);
        Volatile.Write(ref _central, 0);
        Volatile.Write(ref _local, 0);
        Volatile.Write(ref _captureFault, 0);
        Volatile.Write(ref _dropped, 0);
        Volatile.Write(ref _interrupted, 0);
    }
}
