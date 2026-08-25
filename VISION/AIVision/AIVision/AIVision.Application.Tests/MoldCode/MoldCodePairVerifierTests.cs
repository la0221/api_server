using System;
using AIVision.Domain.MoldCode;
using Xunit;

namespace AIVision.Application.Tests.MoldCode;

/// <summary>
/// 雙軸三態決策（<see cref="MoldCodePairVerifier.Decide"/>）單元測試。
/// 涵蓋：相符、分軸高信心不符→混料、低信心不符→採信操作員、NG→剔除、各 Skip 路徑、
/// 門檻驗證、正規化（穴號補零 / 模號大小寫）。預設門檻：模號 0.60、穴號 0.85。
/// </summary>
public class MoldCodePairVerifierTests
{
    private const double Mold = 0.60;
    private const double Cav = 0.85;

    private static PairDecision Decide(string expM, string expX, PairObservation obs) =>
        MoldCodePairVerifier.Decide(expM, expX, obs, Mold, Cav);

    [Fact]
    public void BothMatch_ReturnsMatch_NoReject()
    {
        var d = Decide("M101", "08", PairObservation.Read("M101", 0.99, "08", 0.99));
        Assert.Equal(PairVerifyOutcome.Match, d.Outcome);
        Assert.False(d.ShouldReject);
        Assert.Equal("M101/08", d.ClassifiedAs);
    }

    [Fact]
    public void MohaoConfidentMismatch_ReturnsMixedAlarm_Reject()
    {
        // 模號錯且高信心(>=0.60) → 混料(不同模具=不同件)。
        var d = Decide("M101", "08", PairObservation.Read("M60", 0.97, "08", 0.99));
        Assert.Equal(PairVerifyOutcome.MixedAlarm, d.Outcome);
        Assert.True(d.ShouldReject);
        Assert.True(d.MohaoMismatch);
        Assert.Equal(PairDecision.MismatchBin, d.ClassifiedAs);
    }

    [Fact]
    public void XuehaoConfidentMismatch_ReturnsMixedAlarm_Reject()
    {
        // 穴號錯且高信心(>=0.85) → 混料。
        var d = Decide("M101", "08", PairObservation.Read("M101", 0.99, "03", 0.90));
        Assert.Equal(PairVerifyOutcome.MixedAlarm, d.Outcome);
        Assert.True(d.XuehaoMismatch);
    }

    [Fact]
    public void MohaoMismatch_LowConfidence_ReturnsSkip_FailClosed()
    {
        // 模號不符且信心 < 0.60 → **不採信操作員**（2026-08-25 現場拍板）。
        // 舊行為是 TrustInput 當良品放行；但真混料時操作員填的還是原料號，
        // 「採信操作員」正好放掉最該攔的那片（fail-open）。改為 Skip：不放行、也不誤吹。
        var d = Decide("M101", "08", PairObservation.Read("M60", 0.50, "08", 0.99));
        Assert.Equal(PairVerifyOutcome.Skip, d.Outcome);
        Assert.False(d.ShouldReject);   // 不吹：低信心多半是爛圖，那是取像問題不是不良品
    }

    [Fact]
    public void XuehaoMismatch_LowConfidence_ReturnsSkip_FailClosed()
    {
        // 穴號不符且信心 < 0.85 → 同上，不採信操作員。
        var d = Decide("M101", "08", PairObservation.Read("M101", 0.99, "03", 0.80));
        Assert.Equal(PairVerifyOutcome.Skip, d.Outcome);
        Assert.False(d.ShouldReject);
    }

    [Fact]
    public void InvalidReading_LowConfidence_ReturnsSkip_NotTrustInput()
    {
        // 現場實據 2026-08-25 14:20:09：讀出 "M10/M5"（穴號 M5 是不可能的值）
        // conf=0.31/0.59 → 舊行為當良品放行。新行為：Skip，不放行。
        var d = Decide("M58", "15", PairObservation.Read("M10", 0.31, "M5", 0.59));
        Assert.Equal(PairVerifyOutcome.Skip, d.Outcome);
        Assert.False(d.ShouldReject);
    }

    [Fact]
    public void NgHighConfidence_ReturnsReject()
    {
        var d = Decide("M101", "08", PairObservation.Read("NG", 0.95, "08", 0.99));
        Assert.Equal(PairVerifyOutcome.Reject, d.Outcome);
        Assert.True(d.ShouldReject);
        Assert.Equal(PairDecision.RejectBin, d.ClassifiedAs);
    }

    [Fact]
    public void NgLowConfidence_ReturnsSkip_FailClosed()
    {
        // NG 但信心 < 模號門檻 → 無法確認為良品 → Skip（fail-closed，下游不放行）。
        // 不可變成 TrustInput 被當良品放行（Codex Finding 1）。
        var d = Decide("M101", "08", PairObservation.Read("NG", 0.40, "08", 0.99));
        Assert.Equal(PairVerifyOutcome.Skip, d.Outcome);
        Assert.False(d.ShouldReject);
    }

    [Fact]
    public void NoObject_ReturnsSkip()
    {
        var d = Decide("M101", "08", PairObservation.NoObject());
        Assert.Equal(PairVerifyOutcome.Skip, d.Outcome);
        Assert.False(d.ShouldReject);
    }

    [Fact]
    public void RecognizerFailed_ReturnsSkip()
    {
        var d = Decide("M101", "08", PairObservation.Failed("onnx error"));
        Assert.Equal(PairVerifyOutcome.Skip, d.Outcome);
    }

    [Theory]
    [InlineData("", "08")]
    [InlineData("M101", "")]
    [InlineData(null, "08")]
    public void NoOperatorExpectation_ReturnsSkip(string? expM, string? expX)
    {
        var d = MoldCodePairVerifier.Decide(expM, expX, PairObservation.Read("M101", 0.99, "08", 0.99), Mold, Cav);
        Assert.Equal(PairVerifyOutcome.Skip, d.Outcome);
    }

    [Theory]
    [InlineData(double.NaN, 0.99)]
    [InlineData(0.99, double.PositiveInfinity)]
    public void NonFiniteConfidence_ReturnsSkip_FailClosed(double confM, double confX)
    {
        var d = Decide("M101", "08", PairObservation.Read("M101", confM, "08", confX));
        Assert.Equal(PairVerifyOutcome.Skip, d.Outcome);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void OutOfRangeThreshold_Throws(double bad)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MoldCodePairVerifier.Decide("M101", "08", PairObservation.Read("M101", 0.9, "08", 0.9), bad, Cav));
    }

    [Fact]
    public void CavityNormalization_SingleDigitMatchesPadded()
    {
        // 操作員輸入 "8"、模型輸出 "08" → 視為相符。
        var d = Decide("M101", "8", PairObservation.Read("M101", 0.99, "08", 0.99));
        Assert.Equal(PairVerifyOutcome.Match, d.Outcome);
    }

    [Fact]
    public void MohaoNormalization_CaseInsensitive()
    {
        var d = Decide("m101", "08", PairObservation.Read("M101", 0.99, "08", 0.99));
        Assert.Equal(PairVerifyOutcome.Match, d.Outcome);
    }
}
