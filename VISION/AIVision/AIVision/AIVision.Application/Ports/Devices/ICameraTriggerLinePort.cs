using System;

namespace AIVision.Application.Ports.Devices;

/// <summary>
/// 相機的**數位輸入觸發線**（IDS 的 <c>Line0</c>）。
///
/// <para><b>為什麼要有這個</b>：現場的觸發開關是**接在相機的 IO 線上**，不是接 PLC ——
/// 已驗證的 <c>模號檢驗/相機版</c> 就是每擷取一幀就順便讀一次 <c>LineStatus</c>，
/// 由低變高的那一瞬間觸發一次檢測（軟體輪詢，相機維持 free-run 連續預覽）。</para>
///
/// <para>不做這條的話，現場把開關按下去**完全沒反應**，只能靠畫面上的手動按鈕 ——
/// 那等於整條實時產線沒有觸發源。</para>
///
/// <para>刻意**不放進 <see cref="ICameraPort"/>**：假相機／webcam 沒有這種線，
/// 讓它們被迫實作只會多出一堆空方法。要用的人自己判斷
/// <c>if (camera is ICameraTriggerLinePort tl)</c>。</para>
/// </summary>
public interface ICameraTriggerLinePort
{
    /// <summary>
    /// 觸發線讀得到嗎。false＝這台相機沒有這條線、或沒有權限讀
    /// —— 畫面要明講「只能手動觸發」，不要讓現場以為按了開關會動。
    /// </summary>
    bool IsTriggerLineReady { get; }

    /// <summary>觸發線用的名稱（例如 <c>Line0</c>），顯示用。</summary>
    string TriggerLineName { get; }

    /// <summary>
    /// 觸發線由低變高的瞬間（上升緣）。**每按一次開關只會發一次。**
    /// <para>⚠ 這個事件在擷取執行緒上發出，處理要短、不可阻塞，也不可直接碰 UI。</para>
    /// </summary>
    event EventHandler? TriggerLineRose;
}
