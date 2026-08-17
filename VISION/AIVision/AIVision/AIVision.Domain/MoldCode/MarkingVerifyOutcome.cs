namespace AIVision.Domain.MoldCode;

/// <summary>
/// 封閉字集標記核對的三態決策結果(外加 Skip)。
/// 反推自 6_4 模號 M101 測試 session log(OCR 當核對,模型沒把握就採信操作員輸入)。
/// </summary>
public enum MarkingVerifyOutcome
{
    /// <summary>不分類、不存圖:無物件 / 無辨識結果 / 無操作員預期 / 信心非有限。</summary>
    Skip,

    /// <summary>✓相符:模型讀值 == 操作員預期(即使分類信心很低,對到預期即相符)。</summary>
    Match,

    /// <summary>採信輸入:模型讀值 != 預期,但分類信心低於門檻 → 不信模型,採用操作員輸入值。</summary>
    TrustInput,

    /// <summary>⚠️混料警報:模型讀值 != 預期,且分類信心 &gt;= 門檻 → 挑到 _MISMATCH / 氣吹剔除。</summary>
    MixedAlarm
}
