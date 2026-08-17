using System;
using System.Collections.Generic;

namespace AIVision.Application.MoldCode;

/// <summary>
/// 模號核對週期設定(走 appsettings,不 hardcode)。
/// </summary>
public sealed class MoldCodeCycleOptions
{
    public const string SectionName = "MoldCodeCycle";

    /// <summary>封閉字集(如 M101/01..18);空 = 不限制。</summary>
    public IReadOnlyList<string> ClassSet { get; set; } = Array.Empty<string>();

    /// <summary>混料警報分類信心門檻(log 證據 0.71~0.95,預設 0.85,現場校)。</summary>
    public double MixedAlarmConfThreshold { get; set; } = 0.85;

    /// <summary>單一料位最多取幾幀投票。</summary>
    public int MaxFrames { get; set; } = 7;

    /// <summary>辨識時間預算(ms);超過即停止取幀。</summary>
    public int TimeBudgetMs { get; set; } = 120;

    /// <summary>早停:勝出碼最少票數。</summary>
    public int MinConsensusVotes { get; set; } = 3;

    /// <summary>早停:勝出與次高的最少票差。</summary>
    public int MinConsensusMargin { get; set; } = 2;
}
