using System;
using System.Globalization;

namespace AIVision.Domain.MoldCode;

/// <summary>
/// 雙 head 模號/穴號核對 — 分軸三態決策純函式（fail-closed，零外部依賴）。
/// 對齊 Python engine.py <c>reconcile()</c>：模號、穴號**各自獨立**用不同門檻判斷，
/// 任一軸「高信心不符」→ 混料警報（氣吹）。外加模號 head 的 NG 類 → 不良品剔除。
/// <para/>
/// 分軸門檻的理由（見訓練端 config.py）：模號是粗特徵、模型可靠 → 用**嚴格**門檻（0.60），
/// 不同模具盡量抓；穴號會在 11↔17 等同模具內搖擺 → 用**寬容**門檻（0.85），低信心不符採信操作員。
/// </summary>
public static class MoldCodePairVerifier
{
    /// <summary>
    /// 對一次雙軸觀測做分軸三態核對。
    /// </summary>
    /// <param name="expectedMohao">操作員預期模號（如 "M101"；null/空 → Skip）。</param>
    /// <param name="expectedXuehao">操作員預期穴號（如 "08"；null/空 → Skip）。</param>
    /// <param name="obs">雙 head 觀測。</param>
    /// <param name="moldThreshold">模號軸混料警報信心門檻（[0,1] 有限值；建議 0.60）。</param>
    /// <param name="cavityThreshold">穴號軸混料警報信心門檻（[0,1] 有限值；建議 0.85）。</param>
    /// <param name="ngClassName">模號 head 的不良品類名（預設 "NG"）。</param>
    /// <exception cref="ArgumentOutOfRangeException">門檻非有限或不在 [0,1]（設定錯誤，fail-loud）。</exception>
    public static PairDecision Decide(
        string? expectedMohao,
        string? expectedXuehao,
        PairObservation obs,
        double moldThreshold,
        double cavityThreshold,
        string ngClassName = "NG")
    {
        ValidateThreshold(moldThreshold, nameof(moldThreshold));
        ValidateThreshold(cavityThreshold, nameof(cavityThreshold));

        if (obs is null)
            return PairDecision.Skip("observation is null");

        if (!obs.ObjectPresent)
            return PairDecision.Skip("no object present");

        if (!obs.HasReading)
            return PairDecision.Skip(obs.FailureReason ?? "no recognized code");

        if (string.IsNullOrWhiteSpace(expectedMohao) || string.IsNullOrWhiteSpace(expectedXuehao))
            return PairDecision.Skip("no operator expectation set");

        // fail-closed：信心非有限（NaN/Inf）= 辨識器異常 → 不可當「看似合法」的值用。
        if (!double.IsFinite(obs.ConfMohao) || !double.IsFinite(obs.ConfXuehao))
            return PairDecision.Skip("non-finite confidence");

        var readMohao = NormalizeMohao(obs.Mohao!);
        var readXuehao = NormalizeCavity(obs.Xuehao!);
        var wantMohao = NormalizeMohao(expectedMohao!);
        var wantXuehao = NormalizeCavity(expectedXuehao!);
        var expectedFull = $"{wantMohao}/{wantXuehao}";

        // NG：模號 head 判為不良品 → 一律不可放行（fail-closed）。
        //   高信心 → Reject（氣吹剔除）；低信心 → Skip（無法確認為良品 → 下游送 NG，不放行也不誤吹）。
        //   ⚠️ 不可落到下方分軸邏輯，否則低信心 NG 會變 TrustInput 被當良品放行（fail-open）。
        bool isNg = string.Equals(readMohao, NormalizeMohao(ngClassName), StringComparison.Ordinal);
        if (isNg)
            return obs.ConfMohao >= moldThreshold
                ? new PairDecision(
                    PairVerifyOutcome.Reject, PairDecision.RejectBin, false, false,
                    $"NG defect: mohao=NG conf={obs.ConfMohao:F2} >= {moldThreshold:F2}")
                : PairDecision.Skip(
                    $"low-confidence NG (conf={obs.ConfMohao:F2} < {moldThreshold:F2}) — cannot confirm good part");

        bool mohaoMatch = string.Equals(readMohao, wantMohao, StringComparison.Ordinal);
        bool xuehaoMatch = string.Equals(readXuehao, wantXuehao, StringComparison.Ordinal);

        bool mohaoMismatch = !mohaoMatch && obs.ConfMohao >= moldThreshold;
        bool xuehaoMismatch = !xuehaoMatch && obs.ConfXuehao >= cavityThreshold;

        if (mohaoMatch && xuehaoMatch)
            return new PairDecision(
                PairVerifyOutcome.Match, expectedFull, false, false,
                $"both match expected {expectedFull}");

        if (mohaoMismatch || xuehaoMismatch)
        {
            var why = mohaoMismatch
                ? $"mohao confident mismatch: read={readMohao} expected={wantMohao} conf={obs.ConfMohao:F2} >= {moldThreshold:F2}"
                : $"xuehao confident mismatch: read={readXuehao} expected={wantXuehao} conf={obs.ConfXuehao:F2} >= {cavityThreshold:F2}";
            return new PairDecision(
                PairVerifyOutcome.MixedAlarm, PairDecision.MismatchBin, mohaoMismatch, xuehaoMismatch, why);
        }

        // 有軸不符、但兩軸都不到門檻 → **不放行**（fail-closed）。
        //
        // ⚠ 2026-08-25 現場拍板改掉：原本這裡回 TrustInput（採信操作員輸入）＝當良品放行。
        //   原意是「保留 11↔17 這類同模具內的模型搖擺，不要為了一次低信心就誤吹良品」，
        //   但實測證明推論有兩個致命盲點：
        //     ① 真的混料時，操作員填的工單當然還是原本那個料號
        //        → 「採信操作員」＝ 正好放掉最該攔的那片（fail-open）。
        //     ② 信心低到 conf=0.31/0.59 根本不是「模型搖擺」，是**擷取到爛圖**
        //        （沒對準／模糊／根本不是鏡片）。實測讀出 "M10/M5"——穴號是不可能的值——
        //        卻仍被當成一次低信心讀取而放行。
        //   本檔上方 NG 分支的註解早就警告過同一件事：
        //     「不可落到下方分軸邏輯，否則低信心 NG 會變 TrustInput 被當良品放行（fail-open）」
        //
        // 改回 Skip，與 NG 低信心分支一致：**不放行、也不誤吹**
        //   （下游 VerifyMoldCodePairCycleCommandHandler 把 Skip 映射成 Result(false)＝NG，
        //     不會觸發氣吹——爛圖不該用氣閥去處理，那是取像品質問題）。
        return PairDecision.Skip(
            $"low-confidence mismatch (read={readMohao}/{readXuehao} conf={obs.ConfMohao:F2}/{obs.ConfXuehao:F2}) — cannot confirm good part");
    }

    private static void ValidateThreshold(double t, string name)
    {
        if (!double.IsFinite(t) || t < 0.0 || t > 1.0)
            throw new ArgumentOutOfRangeException(name, t, "信心門檻必須是 [0,1] 的有限值（設定錯誤）。");
    }

    /// <summary>模號正規化：去頭尾空白、轉大寫。例 <c>" m101 "</c> → <c>"M101"</c>。</summary>
    public static string NormalizeMohao(string code) =>
        string.IsNullOrWhiteSpace(code) ? string.Empty : code.Trim().ToUpperInvariant();

    /// <summary>穴號正規化：數字補滿兩位（<c>"8"</c> → <c>"08"</c>）；非數字則去空白大寫。</summary>
    public static string NormalizeCavity(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return string.Empty;
        var t = code.Trim();
        return int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n.ToString("D2", CultureInfo.InvariantCulture)
            : t.ToUpperInvariant();
    }
}
