using AIVision.Application.Contracts.ProductionStats;

namespace AIVision.Application.Ports.ProductionStats;

public interface IProductionStatsQuery
{
    Task<IReadOnlyList<WorkOrderSummaryDto>> FindOrdersAsync(
        DateTime? start,
        DateTime? end,
        string? product,
        string? orderKeyword,
        CancellationToken cancellationToken);

    Task<WorkOrderStatsDto?> GetStatsAsync(Guid orderId, CancellationToken cancellationToken);
}
