using System;
using System.Collections.Generic;
using System.Linq;

namespace AIVision.Domain.MoldCode;

/// <summary>
/// 雙軸多幀投票結果。
/// </summary>
/// <param name="Observation">投票彙整後的單一雙軸觀測（餵給 <see cref="MoldCodePairVerifier.Decide"/>）。</param>
/// <param name="Frames">總幀數。</param>
/// <param name="WinnerVotes">勝出 (模號,穴號) 配對的票數。</param>
/// <param name="RunnerUpVotes">次高配對票數（算 margin 用）。</param>
/// <param name="Agreement">勝出配對占有效幀的比例（同意率）。</param>
public sealed record PairVoteResult(
    PairObservation Observation,
    int Frames,
    int WinnerVotes,
    int RunnerUpVotes,
    double Agreement)
{
    /// <summary>票數差（勝出 − 次高）。</summary>
    public int Margin => WinnerVotes - RunnerUpVotes;
}

/// <summary>
/// 雙軸自適應多幀投票 — 純函式。對同一料位連拍多幀，以 (模號,穴號) 配對做
/// 信心加權多數決（score += confMohao × confXuehao），壓掉 borderline 單幀翻面。
/// 對齊 Python engine.py <c>vote()</c>。
/// </summary>
public static class MoldCodePairVoter
{
    /// <summary>對多幀雙軸觀測做配對加權多數決；勝出配對的信心取各幀平均。</summary>
    public static PairVoteResult Vote(IReadOnlyList<PairObservation> frames)
    {
        if (frames is null || frames.Count == 0)
            return new PairVoteResult(PairObservation.NoObject("no frames"), 0, 0, 0, 0);

        var valid = frames.Where(f => f is { ObjectPresent: true } && f.HasReading).ToList();
        bool anyObject = frames.Any(f => f.ObjectPresent);

        if (valid.Count == 0)
        {
            var obs = anyObject
                ? PairObservation.Failed("no coded frame among captures")
                : PairObservation.NoObject();
            return new PairVoteResult(obs, frames.Count, 0, 0, 0);
        }

        var score = new Dictionary<(string, string), double>();
        var count = new Dictionary<(string, string), int>();
        var order = new List<(string, string)>();   // 首見順序（對齊 Python dict 插入序 + max 取首個最大）
        foreach (var f in valid)
        {
            var key = (f.Mohao!, f.Xuehao!);
            if (!score.ContainsKey(key))
                order.Add(key);
            score[key] = score.GetValueOrDefault(key) + f.ConfMohao * f.ConfXuehao;
            count[key] = count.GetValueOrDefault(key) + 1;
        }

        // 勝出：加權分數最高；平手取「首見」者（嚴格大於才更替）→ 與 Python engine.py vote()
        // 的 max(score, key=score.get) 一致（不以票數當 tie-break，避免與參考實作分歧）。
        var winner = order[0];
        double bestScore = score[winner];
        foreach (var key in order)
        {
            if (score[key] > bestScore)
            {
                bestScore = score[key];
                winner = key;
            }
        }

        int winnerVotes = count[winner];
        int runnerUp = count.Where(kv => kv.Key != winner)
            .Select(kv => kv.Value)
            .DefaultIfEmpty(0)
            .Max();

        var winFrames = valid.Where(f => (f.Mohao!, f.Xuehao!) == winner).ToList();
        double avgM = winFrames.Average(f => f.ConfMohao);
        double avgX = winFrames.Average(f => f.ConfXuehao);
        double agreement = (double)winnerVotes / valid.Count;

        return new PairVoteResult(
            PairObservation.Read(winner.Item1, avgM, winner.Item2, avgX),
            frames.Count, winnerVotes, runnerUp, agreement);
    }

    /// <summary>
    /// 是否已達共識可早停：勝出票數達 <paramref name="minVotes"/> 且 margin 達 <paramref name="minMargin"/>。
    /// </summary>
    public static bool HasConsensus(PairVoteResult vote, int minVotes, int minMargin) =>
        vote.WinnerVotes >= minVotes && vote.Margin >= minMargin;
}
