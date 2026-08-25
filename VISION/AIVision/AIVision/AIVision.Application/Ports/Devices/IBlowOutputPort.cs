using System;
using System.Threading;
using System.Threading.Tasks;

namespace AIVision.Application.Ports.Devices;

/// <summary>
/// 吹氣觸發輸出（2026-08-19 自 `模號檢驗/相機版` 移植）。
///
/// <para><b>為什麼 PLC 之外還要這條</b>：本專案原本走 <c>IoCommand.Blow()</c> 直接寫 PLC，
/// 但現場的 IO 卡**不在這台電腦上**（在妍華那台），吹嘴是由那台的監聽程式驅動的。
/// 所以要有一條「把觸發訊號送出去」的通道，與 PLC 那條並存、互不影響。</para>
///
/// <para><b>鐵律</b>：送不出去（對方沒開、網路不通）**絕不能影響辨識流程**——
/// 實作一律吞例外只記 log。吹氣是後段動作，不是判定的一部分。</para>
/// </summary>
public interface IBlowOutputPort
{
    /// <summary>這條輸出的名稱（顯示在畫面/log，讓現場知道訊號往哪去）。</summary>
    string DisplayName { get; }

    /// <summary>送出一次吹氣觸發。實作不得拋例外。</summary>
    Task SendAsync(BlowRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// 一次吹氣觸發要帶的資訊。
/// <para>帶上預期/實際與信心，是為了讓 IO 端的 log 能單獨對帳——
/// 出事時才分得出「視覺沒判到」還是「判到了但沒吹到」。</para>
/// </summary>
/// <param name="Id">去重用的唯一識別（同一片鏡片只吹一次）。</param>
/// <param name="Reason">觸發原因：<c>MISMATCH</c>（混料）或 <c>NG</c>（不良品）。</param>
/// <param name="DelayMs">從現在起延遲幾毫秒才送出（等工件走到吹嘴位置）。</param>
public sealed record BlowRequest(
    string Id,
    DateTime CreatedAt,
    string Reason,
    string ExpectedMohao,
    string ExpectedXuehao,
    string DetectedMohao,
    string DetectedXuehao,
    double ConfMohao,
    double ConfXuehao,
    int DelayMs)
{
    /// <summary>
    /// 觸發時刻（<see cref="Environment.TickCount64"/>）。0＝沒帶。
    /// <para>只給對帳用：現場調延遲時要知道「從觸發到實際送出吹氣」花了多久
    /// ——判定時間會隨父端忙碌程度浮動，光看設定的 DelayMs 看不出來。</para>
    /// <para>⚠ 目前**不用它做自動補償**（2026-08-24 拍板：延遲由使用者自己填 ms）。</para>
    /// </summary>
    public long TriggerTick { get; init; }

    /// <summary>混料。</summary>
    public const string ReasonMismatch = "MISMATCH";

    /// <summary>不良品。</summary>
    public const string ReasonNg = "NG";

    /// <summary>現場測試按鈕用。</summary>
    public const string ReasonTest = "TEST";
}
