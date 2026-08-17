using System;
using System.Collections.Generic;
using System.Linq;

namespace AIVision.Domain.MoldCode;

/// <summary>
/// 多幀投票結果。
/// </summary>
/// <param name="Observation">投票彙整後的單一觀測(餵給 <see cref="MarkingVerifier.Decide"/>)。</param>
/// <param name="Frames">總幀數。</param>
/// <param name="WinnerVotes">勝出碼的票數。</param>
/// <param name="RunnerUpVotes">次高碼的票數(算 margin 用)。</param>
/// <param name="Tally">各正規化碼的票數分布。</param>
public sealed record VoteResult(
    MarkingObservation Observation,
    int Frames,
    int WinnerVotes,
    int RunnerUpVotes,
    IReadOnlyDictionary<string, int> Tally)
{
    /// <summary>票數差(勝出 − 次高)。</summary>
    public int Margin => WinnerVotes - RunnerUpVotes;
}

/// <summary>
/// 自適應多幀投票 — 純函式。對同一料位的連拍多幀彙整成單一觀測,壓掉單張偶發誤判。
/// 150ms 預算下辨識核心僅 ~8ms,可投多幀;早停由呼叫端依 <see cref="HasConsensus"/> + 時間預算決定。
/// </summary>
public static class MultiFrameVoter
{
    /// <summary>對多幀觀測做碼多數決,信心取勝出碼各幀平均。</summary>
    public static VoteResult Vote(IReadOnlyList<MarkingObservation> frames)
    {
        if (frames is null || frames.Count == 0)
            return new VoteResult(MarkingObservation.NoObject("no frames"), 0, 0, 0,
                new Dictionary<string, int>());

        var coded = frames
            .Where(f => f is { ObjectPresent: true, HasCode: true } && !string.IsNullOrWhiteSpace(f.Code))
            .ToList();

        bool anyObject = frames.Any(f => f.ObjectPresent);

        if (coded.Count == 0)
        {
            var obs = anyObject
                ? MarkingObservation.Failed("no coded frame among captures")
                : MarkingObservation.NoObject();
            return new VoteResult(obs, frames.Count, 0, 0, new Dictionary<string, int>());
        }

        var tally = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var f in coded)
        {
            var key = MarkingVerifier.NormalizeCode(f.Code!);
            tally[key] = tally.GetValueOrDefault(key) + 1;
        }

        var ranked = tally.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        var winner = ranked[0];
        int runnerUp = ranked.Count > 1 ? ranked[1].Value : 0;

        var winnerFrames = coded.Where(f => MarkingVerifier.NormalizeCode(f.Code!) == winner.Key).ToList();
        double confDetect = winnerFrames.Average(f => f.ConfDetect);
        double confClassify = winnerFrames.Average(f => f.ConfClassify);

        return new VoteResult(
            MarkingObservation.Read(winner.Key, confDetect, confClassify),
            frames.Count, winner.Value, runnerUp, tally);
    }

    /// <summary>
    /// 是否已達共識可早停:勝出票數達 <paramref name="minVotes"/> 且 margin 達 <paramref name="minMargin"/>。
    /// </summary>
    public static bool HasConsensus(VoteResult vote, int minVotes, int minMargin) =>
        vote.WinnerVotes >= minVotes && vote.Margin >= minMargin;
}
