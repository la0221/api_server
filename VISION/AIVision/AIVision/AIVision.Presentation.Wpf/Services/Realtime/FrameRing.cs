using System;
using System.Collections.Generic;
using AIVision.Domain.Shared;

namespace AIVision.Presentation.Wpf.Services.Realtime;

/// <summary>環形緩衝裡的一幀。</summary>
/// <param name="Image">影像。</param>
/// <param name="Tick">收到這一幀的時刻（<see cref="Environment.TickCount64"/>）。</param>
public readonly record struct RingFrame(ImageData Image, long Tick);

/// <summary>
/// 相機幀的環形緩衝（移植自 <c>模號檢驗/相機版</c>，該版本已在現場驗證）。
///
/// <para><b>它解的問題</b>：觸發訊號來自感測器，而感測器多半裝在**拍照位的上游**，
/// 訊號到的時候工件還沒完全進框；辨識又可能正忙。如果「觸發當下才拍一張」，
/// 就會拍到工件還沒到位的畫面 —— 那正是<b>擷取失誤</b>的來源。</para>
///
/// <para><b>做法</b>：相機執行緒把**每一幀**都丟進來（保留最近 <see cref="KeepMs"/>），
/// 觸發只記時刻；檢測迴圈事後在時間窗內<b>回頭找</b>那個「工件剛好完整進框」的瞬間。
/// 不論感測器多上游、辨識多忙，那一幀都還在緩衝裡。</para>
///
/// <para>執行緒安全：相機執行緒寫、檢測迴圈讀，全部走同一把鎖。</para>
/// </summary>
public sealed class FrameRing
{
    /// <summary>保留時長。要 ≥ 擷取窗 + 最大積壓等待，否則回頭找的時候那一幀已經被丟掉了。</summary>
    public const long KeepMs = 2500;

    private readonly object _gate = new();
    private readonly Queue<RingFrame> _frames = new();

    /// <summary>目前緩衝裡有幾幀（診斷用）。</summary>
    public int Count { get { lock (_gate) return _frames.Count; } }

    /// <summary>最後一幀的時刻；沒有幀時回 0。</summary>
    public long LatestTick { get { lock (_gate) return _frames.Count == 0 ? 0 : _frames.Peek().Tick; } }

    /// <summary>相機每收到一幀就叫這個。過期的自動丟掉。</summary>
    public void Add(ImageData image, long tick)
    {
        // ImageData 是 readonly record struct（實值型別），不能跟 null 比；空影像才要擋。
        if (image.Bytes is null || image.Bytes.Length == 0) return;
        lock (_gate)
        {
            _frames.Enqueue(new RingFrame(image, tick));
            var cutoff = tick - KeepMs;
            while (_frames.Count > 0 && _frames.Peek().Tick < cutoff)
                _frames.Dequeue();
        }
    }

    /// <summary>
    /// 取出時間窗 <c>(afterTick, untilTick]</c> 內、**時間由早到晚**的幀。
    /// <para><paramref name="afterTick"/> 是去重游標：早於（含）它的幀已經被別的觸發用掉了，
    /// 不能再用 —— 這是「一片一張」的保證。</para>
    /// </summary>
    public IReadOnlyList<RingFrame> Window(long afterTick, long untilTick)
    {
        lock (_gate)
        {
            var list = new List<RingFrame>(_frames.Count);
            foreach (var f in _frames)
                if (f.Tick > afterTick && f.Tick <= untilTick)
                    list.Add(f);
            list.Sort(static (a, b) => a.Tick.CompareTo(b.Tick));
            return list;
        }
    }

    public void Clear()
    {
        lock (_gate) _frames.Clear();
    }
}
