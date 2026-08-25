using System;

namespace AIVision.Infrastructure.Devices.Blow;

/// <summary>
/// 吹氣觸發設定（appsettings <c>Devices:Blow</c>）。移植自 <c>模號檢驗/相機版</c> 的 BlowSettings。
/// </summary>
public sealed class BlowOptions
{
    public const string SectionName = "Devices:Blow";

    /// <summary>
    /// 是否啟用「TCP 吹氣觸發」。**預設 false**——這條是額外通道，
    /// 沒有它產線照跑（PLC 那條的 <c>IoCommand.Blow()</c> 不受影響）。
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// 判定完成後延遲幾毫秒才送觸發。
    /// <para>工件從拍照位走到吹嘴要時間，太早吹會吹到前一片。現場用碼錶量出來填。</para>
    /// </summary>
    public int DelayMs { get; set; } = 0;

    /// <summary>混料（MISMATCH）要不要吹。</summary>
    public bool BlowOnMismatch { get; set; } = true;

    /// <summary>不良品（NG）要不要吹。預設不吹——NG 常另有處理站。</summary>
    public bool BlowOnNg { get; set; } = false;

    /// <summary>IO 監聽程式所在主機。現場 IO 卡不在本機時填那台的 IP。</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>
    /// IO 監聽埠。**NgAirBlowService 是 5000**（見 `NgAirBlowService/對接說明.md`）。
    /// </summary>
    public int Port { get; set; } = 5000;

    /// <summary>IO 卡輸出通道（現場為 0 號）。</summary>
    public int Channel { get; set; } = 0;

    /// <summary>連線逾時（毫秒）。設短一點：吹氣連不上要快點放棄，不能拖住佇列。</summary>
    public int ConnectTimeoutMs { get; set; } = 1500;

    /// <summary>
    /// 輸出通道：<c>Tcp</c>（送到 IO 監聽程式）或 <c>Log</c>（只寫 log，開發／驗收用）。
    /// </summary>
    public string Output { get; set; } = "Tcp";

    /// <summary>
    /// 訊號格式。
    /// <list type="bullet">
    /// <item><c>NgText</c>（預設）＝ <b>實際的 NgAirBlowService</b>：ASCII 純文字，
    ///   對方的判定是「**內容含 NG（不分大小寫）就吹**」。</item>
    /// <item><c>Json</c> ＝ 模號檢驗/相機版那支自製監聽程式吃的 JSON 一行。</item>
    /// </list>
    /// <para>⚠ <b>這個值選錯會是無聲失敗</b>：JSON 格式裡**沒有 NG 這兩個字**，
    /// 送給 NgAirBlowService 會被當成雜訊丟掉——混料照判、log 照寫、就是**不吹**。
    /// 實測驗證過（2026-08-22）。</para>
    /// </summary>
    public string Format { get; set; } = "NgText";

    /// <summary>
    /// 是否維持長連線。對方的對接說明建議長連線（少掉每次重連的開銷）；
    /// 連線斷掉會自動重連，重連失敗才記 warning。
    /// </summary>
    public bool KeepAlive { get; set; } = true;

    public BlowOptions Clone() => new()
    {
        Enabled = Enabled,
        DelayMs = DelayMs,
        BlowOnMismatch = BlowOnMismatch,
        BlowOnNg = BlowOnNg,
        Host = Host,
        Port = Port,
        Channel = Channel,
        ConnectTimeoutMs = ConnectTimeoutMs,
        Output = Output,
        Format = Format,
        KeepAlive = KeepAlive,
    };

    /// <summary>把使用者輸入夾到合理範圍（畫面上可亂填，這裡是最後一道）。</summary>
    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Host)) Host = "127.0.0.1";
        Host = Host.Trim();
        Port = Math.Clamp(Port, 1, 65535);
        Channel = Math.Clamp(Channel, 0, 255);
        DelayMs = Math.Clamp(DelayMs, 0, 60_000);
        ConnectTimeoutMs = Math.Clamp(ConnectTimeoutMs, 100, 30_000);
        if (string.IsNullOrWhiteSpace(Output)) Output = "Tcp";
        Output = Output.Trim();
        if (string.IsNullOrWhiteSpace(Format)) Format = "NgText";
        Format = Format.Trim();
    }
}
