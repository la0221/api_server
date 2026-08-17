using System.Collections.Immutable;

namespace AIVision.Application.Contracts.ProductionStats;

/// <summary>
/// 工單統計(模號三態核對語意)。
/// <para/>
/// 屬性名稱 <see cref="Ok"/> / <see cref="Ng"/> / <see cref="Yield"/> 保留不改名
/// (ObjectPathResolver / SummaryFields 設定 + 匯出以反射存取),僅重新定義語意:
/// <list type="bullet">
///   <item><see cref="Ok"/> = Match + TrustInput(良)。</item>
///   <item><see cref="Ng"/> = MixedAlarm(混料剔除)。</item>
///   <item><see cref="Total"/> 排除 Skip。</item>
/// </list>
/// <see cref="Defects"/> 字典現承載 Outcome → 次數(取代舊瑕疵類型分布)。
/// </summary>
public sealed class WorkOrderStatsDto
{
    public required WorkOrderSummaryDto Order { get; init; }

    public int Total { get; init; }

    /// <summary>良品數 = Match + TrustInput(名稱保留供反射;語意已改)。</summary>
    public int Ok { get; init; }

    /// <summary>混料剔除數 = MixedAlarm(名稱保留供反射;語意已改)。</summary>
    public int Ng { get; init; }

    public double Yield => Total == 0 ? 0 : (double)Ok / Total;

    /// <summary>Outcome → 次數(Match / TrustInput / MixedAlarm / Skip)。鍵名保留為 Defects 供反射/匯出沿用。</summary>
    public required IReadOnlyDictionary<string, int> Defects { get; init; }

    public TimeSpan Duration => (Order.EndAt ?? DateTime.UtcNow) - Order.StartAt;
}
