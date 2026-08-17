namespace AIVision.Domain.MoldCode;

/// <summary>
/// 雙 head 模號/穴號核對的決策結果。對齊 Python engine.py <c>reconcile()</c> 的
/// CONFIRM / MISMATCH / ACCEPT，外加 NG（不良品）與 Skip。
/// </summary>
public enum PairVerifyOutcome
{
    /// <summary>不分類、不處置：無物件 / 無辨識 / 無操作員預期 / 信心非有限。</summary>
    Skip,

    /// <summary>✓相符（CONFIRM）：兩軸讀值都 == 操作員預期。放行。</summary>
    Match,

    /// <summary>採信輸入（ACCEPT）：有軸不符但信心低於門檻 → 不信模型，採用操作員輸入。放行。</summary>
    TrustInput,

    /// <summary>⚠️混料警報（MISMATCH）：任一軸讀值 != 預期且信心 &gt;= 該軸門檻 → 疑混料，氣吹剔除。</summary>
    MixedAlarm,

    /// <summary>✗不良品（NG）：模號 head 判為 NG 類 → 剔除（氣吹）。</summary>
    Reject
}
