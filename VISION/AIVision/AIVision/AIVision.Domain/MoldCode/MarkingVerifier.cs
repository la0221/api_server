using System;
using System.Text;

namespace AIVision.Domain.MoldCode;

/// <summary>
/// 封閉字集標記核對 — 三態決策純函式(fail-closed,零外部依賴)。
/// 移植自 OpenCV_Vision.Core.MarkingVerify(194 測試過;反哺後預計統一共用庫)。
/// <para/>
/// 決策規則(單一可調門檻 = 混料警報信心門檻):
/// <list type="bullet">
///   <item>無物件 / 無辨識 / 無預期 / 信心非有限 → <see cref="MarkingVerifyOutcome.Skip"/></item>
///   <item>讀值 == 預期 → <see cref="MarkingVerifyOutcome.Match"/>(即使分類信心很低,對到預期即相符)</item>
///   <item>讀值 != 預期 且 分類信心 &gt;= 門檻 → <see cref="MarkingVerifyOutcome.MixedAlarm"/></item>
///   <item>讀值 != 預期 且 分類信心 &lt; 門檻 → <see cref="MarkingVerifyOutcome.TrustInput"/></item>
/// </list>
/// </summary>
public static class MarkingVerifier
{
    /// <summary>
    /// 對一次觀測做三態核對。
    /// </summary>
    /// <param name="expected">操作員預期模號(必填;null/空 → Skip)。</param>
    /// <param name="observation">辨識器觀測結果。</param>
    /// <param name="mixedAlarmConfThreshold">混料警報的分類信心門檻(必須是 [0,1] 有限值)。</param>
    /// <exception cref="ArgumentOutOfRangeException">門檻非有限或不在 [0,1](屬設定錯誤,fail-loud)。</exception>
    public static MarkingDecision Decide(string? expected, MarkingObservation observation, double mixedAlarmConfThreshold)
    {
        if (!double.IsFinite(mixedAlarmConfThreshold) || mixedAlarmConfThreshold < 0.0 || mixedAlarmConfThreshold > 1.0)
            throw new ArgumentOutOfRangeException(nameof(mixedAlarmConfThreshold), mixedAlarmConfThreshold,
                "混料警報信心門檻必須是 [0,1] 的有限值(設定錯誤)。");

        if (observation is null)
            return MarkingDecision.Skip("observation is null");

        if (!observation.ObjectPresent)
            return MarkingDecision.Skip("no object present");

        if (!observation.HasCode || string.IsNullOrWhiteSpace(observation.Code))
            return MarkingDecision.Skip(observation.FailureReason ?? "no recognized code");

        if (string.IsNullOrWhiteSpace(expected))
            return MarkingDecision.Skip("no operator expectation set");

        // fail-closed:信心非有限(NaN/Inf)= 辨識器異常 → 不可當「看似合法」的值用,不分類。
        if (!double.IsFinite(observation.ConfDetect) || !double.IsFinite(observation.ConfClassify))
            return MarkingDecision.Skip("non-finite confidence");

        var read = NormalizeCode(observation.Code!);
        var want = NormalizeCode(expected!);

        if (string.Equals(read, want, StringComparison.Ordinal))
            return new MarkingDecision(MarkingVerifyOutcome.Match, expected!, "read matches expected");

        if (observation.ConfClassify >= mixedAlarmConfThreshold)
            return new MarkingDecision(
                MarkingVerifyOutcome.MixedAlarm,
                MarkingDecision.MismatchBin,
                $"confident mismatch: read={observation.Code} expected={expected} " +
                $"conf={observation.ConfClassify:F2} >= {mixedAlarmConfThreshold:F2}");

        return new MarkingDecision(
            MarkingVerifyOutcome.TrustInput,
            expected!,
            $"low-confidence mismatch: read={observation.Code} " +
            $"conf={observation.ConfClassify:F2} < {mixedAlarmConfThreshold:F2} → trust operator");
    }

    /// <summary>
    /// 正規化模號以利比對:去頭尾空白、轉大寫、把分隔符(/、-、\、_、空白)統一成 '/' 並去除連續重複。
    /// 例:<c>"M101-04"</c>、<c>"M101/04"</c>、<c>"m101 04"</c> 視為同一碼 <c>"M101/04"</c>。
    /// </summary>
    public static string NormalizeCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return string.Empty;

        var sb = new StringBuilder(code.Length);
        foreach (var ch in code.Trim())
        {
            if (ch is '/' or '-' or '\\' or '_' || char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0 && sb[sb.Length - 1] != '/')
                    sb.Append('/');
            }
            else
            {
                sb.Append(char.ToUpperInvariant(ch));
            }
        }

        while (sb.Length > 0 && sb[sb.Length - 1] == '/')
            sb.Length--;

        return sb.ToString();
    }
}
