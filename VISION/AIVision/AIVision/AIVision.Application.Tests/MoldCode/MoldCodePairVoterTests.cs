using System.Collections.Generic;
using AIVision.Domain.MoldCode;
using Xunit;

namespace AIVision.Application.Tests.MoldCode;

/// <summary>
/// 雙軸多幀投票（<see cref="MoldCodePairVoter"/>）單元測試：配對加權多數決、壓掉 borderline
/// 單幀翻面、無幀/全無物件 fail-closed、共識早停。
/// </summary>
public class MoldCodePairVoterTests
{
    [Fact]
    public void Vote_SuppressesSingleBorderlineFlip()
    {
        var frames = new List<PairObservation>
        {
            PairObservation.Read("M101", 0.99, "08", 0.99),
            PairObservation.Read("M101", 0.98, "08", 0.97),
            PairObservation.Read("M101", 0.55, "03", 0.52), // borderline 翻面幀
            PairObservation.Read("M101", 0.99, "08", 0.98),
        };
        var v = MoldCodePairVoter.Vote(frames);
        Assert.Equal("M101", v.Observation.Mohao);
        Assert.Equal("08", v.Observation.Xuehao);
        Assert.Equal(3, v.WinnerVotes);
        Assert.Equal(4, v.Frames);
    }

    [Fact]
    public void Vote_NoFrames_ReturnsNoObject()
    {
        var v = MoldCodePairVoter.Vote(new List<PairObservation>());
        Assert.False(v.Observation.ObjectPresent);
        Assert.False(v.Observation.HasReading);
    }

    [Fact]
    public void Vote_AllNoObject_ReturnsNoObject()
    {
        var frames = new List<PairObservation>
        {
            PairObservation.NoObject(),
            PairObservation.NoObject(),
        };
        var v = MoldCodePairVoter.Vote(frames);
        Assert.False(v.Observation.ObjectPresent);
    }

    [Fact]
    public void Vote_ObjectPresentButNoCode_ReturnsFailed()
    {
        // 有物件但都辨識失敗 → fail-closed Failed（present=true, HasReading=false）。
        var frames = new List<PairObservation> { PairObservation.Failed("onnx error") };
        var v = MoldCodePairVoter.Vote(frames);
        Assert.True(v.Observation.ObjectPresent);
        Assert.False(v.Observation.HasReading);
    }

    [Fact]
    public void HasConsensus_TrueWhenVotesAndMarginMet()
    {
        var frames = new List<PairObservation>
        {
            PairObservation.Read("M101", 0.99, "08", 0.99),
            PairObservation.Read("M101", 0.99, "08", 0.99),
            PairObservation.Read("M101", 0.99, "08", 0.99),
        };
        var v = MoldCodePairVoter.Vote(frames);
        Assert.True(MoldCodePairVoter.HasConsensus(v, minVotes: 3, minMargin: 2));
        Assert.False(MoldCodePairVoter.HasConsensus(v, minVotes: 4, minMargin: 2));
    }
}
