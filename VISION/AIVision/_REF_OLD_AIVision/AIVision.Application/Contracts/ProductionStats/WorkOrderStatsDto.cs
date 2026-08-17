using System.Collections.Immutable;

namespace AIVision.Application.Contracts.ProductionStats;

public sealed class WorkOrderStatsDto
{
    public required WorkOrderSummaryDto Order { get; init; }

    public int Total { get; init; }

    public int Ok { get; init; }

    public int Ng { get; init; }

    public double Yield => Total == 0 ? 0 : (double)Ok / Total;

    public required IReadOnlyDictionary<string, int> Defects { get; init; }

    public TimeSpan Duration => (Order.EndAt ?? DateTime.UtcNow) - Order.StartAt;
}
