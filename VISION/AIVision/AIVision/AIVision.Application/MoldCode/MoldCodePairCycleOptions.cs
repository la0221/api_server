namespace AIVision.Application.MoldCode;

/// <summary>
/// 雙軸模號/穴號核對週期設定（走 appsettings，不 hardcode）。
/// 門檻預設對齊訓練端 config.py（模號嚴格 0.60、穴號寬容 0.85）。
/// </summary>
public sealed class MoldCodePairCycleOptions
{
    public const string SectionName = "MoldCodePairCycle";

    /// <summary>模號軸混料警報信心門檻（嚴格：不同模具盡量抓）。</summary>
    public double MoldThreshold { get; set; } = 0.60;

    /// <summary>穴號軸混料警報信心門檻（寬容：同模具內 11↔17 搖擺採信操作員）。</summary>
    public double CavityThreshold { get; set; } = 0.85;

    /// <summary>模號 head 的不良品類名。</summary>
    public string NgClassName { get; set; } = "NG";

    /// <summary>單一料位最多取幾幀投票。</summary>
    public int MaxFrames { get; set; } = 7;

    /// <summary>辨識時間預算（ms）；超過即停止取幀。</summary>
    public int TimeBudgetMs { get; set; } = 120;

    /// <summary>早停：勝出配對最少票數。</summary>
    public int MinConsensusVotes { get; set; } = 3;

    /// <summary>早停：勝出與次高的最少票差。</summary>
    public int MinConsensusMargin { get; set; } = 2;
}
