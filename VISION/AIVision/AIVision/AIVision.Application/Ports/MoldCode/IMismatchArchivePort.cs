using System.Threading;
using System.Threading.Tasks;
using AIVision.Domain.Shared;

namespace AIVision.Application.Ports.MoldCode;

/// <summary>
/// 混料圖歸檔——**自我強化訓練的輸入來源**（2026-08-19，移植自 <c>模號檢驗/相機版</c>）。
///
/// <para><b>為什麼這件事重要</b>：混料被抓到的那一刻，我們**同時知道正確答案（工單的預期值）
/// 和模型答錯的內容**。把它用 <c>exp_{預期}_got_{偵測}_{時間}.jpg</c> 存下來，
/// 這批圖就是**自帶標註**的訓練資料——不必再找人標。錯過一次的，下次就不會再錯。</para>
///
/// <para>沒有這個歸檔，自我強化訓練就沒有輸入。</para>
///
/// <para>⚠ 存檔失敗**不可影響判定與吹氣**——這是事後分析用的資料，不是產線流程的一環。</para>
/// </summary>
public interface IMismatchArchivePort
{
    /// <summary>目前是否啟用歸檔。</summary>
    bool Enabled { get; }

    /// <summary>
    /// 存一張混料圖。回傳存檔路徑；未啟用或失敗回 null（呼叫端不必處理例外）。
    /// </summary>
    /// <param name="expectedMohao">工單的預期模號（＝正解）。</param>
    /// <param name="detectedMohao">模型判成什麼。</param>
    Task<string?> SaveMismatchAsync(
        ImageData image,
        string expectedMohao,
        string expectedXuehao,
        string detectedMohao,
        string detectedXuehao,
        string? workOrder,
        CancellationToken cancellationToken);
}
