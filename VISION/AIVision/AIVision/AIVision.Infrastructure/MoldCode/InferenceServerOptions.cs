namespace AIVision.Infrastructure.MoldCode;

/// <summary>
/// 中央推論伺服器（AIVision.Api 的 <c>POST /api/infer/pair</c>）連線設定。
/// 契約見 <c>.ai/designs/2026-07-14_api_infer_pair_contract.md</c>。
/// </summary>
public sealed class InferenceServerOptions
{
    public const string SectionName = "InferenceServer";

    /// <summary>server 位址（含 scheme 與 port），例：<c>http://192.168.1.50:5030</c>。</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// 單次推論逾時（毫秒）。⚠ 依情境不同：
    /// <para>試模/驗收/離線批量（現行用途）→ 用寬鬆值（建議 2000）。server 端 Passes=2 單張
    /// 實測約 385ms（p90 409ms）、冷啟首張可達 1.1s，設 350 會必逾時（2026-07-24 實際踩過）。</para>
    /// <para>生產實時（階段 3 之後）→ **必須小於產線節拍**（如 350，節拍 &lt;400ms），
    /// 否則來不及降級用本機模型。屆時應與試模逾時分開設定，勿共用。</para>
    /// </summary>
    public int TimeoutMs { get; set; } = 2000;

    /// <summary>健康檢查逾時（毫秒）。不送圖不推論，可短。</summary>
    public int HealthTimeoutMs { get; set; } = 1000;

    /// <summary>
    /// 是否啟用中央推論。**預設 false**：逐漸導入期間，生產辨識照舊走本機 ONNX；
    /// 開啟前請先用「測試中央推論」驗收按鈕確認打通。
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 已知 server 清單（「API 伺服器設定」視窗的下拉選項；仍可手填任意位址）。
    /// </summary>
    public List<string> KnownServers { get; set; } = new();
}
