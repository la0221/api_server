using AIVision.Application.Ports.Devices;

namespace AIVision.Application.Ports.Devices;

/// <summary>
/// 吹氣派送（排隊 + 延遲 + 去重）。
///
/// <para><b>為什麼判定端不直接呼叫輸出</b>：吹嘴要等工件走到位才吹（<c>DelayMs</c>），
/// 但產線熱迴圈**一秒都不能等**。所以判定端只負責「丟一筆進來」立刻返回，
/// 真正的等待與送出都在背景做。</para>
///
/// <para>去重：同一個 <see cref="BlowRequest.Id"/> 只會吹一次——
/// 多幀投票／重試可能對同一片鏡片產生多次判定，不去重就會連吹好幾下。</para>
/// </summary>
public interface IBlowDispatcherPort
{
    /// <summary>目前是否啟用（停用時 <see cref="Enqueue"/> 直接回 false）。</summary>
    bool Enabled { get; }

    /// <summary>
    /// 排入一次吹氣。**立即返回、不阻塞**。
    /// 回 false = 沒排入（停用、該原因被關掉、或 id 重複）。
    /// </summary>
    bool Enqueue(BlowRequest request);
}
