namespace AIVision.Application.Contracts.ProductionStats;

public sealed record WorkOrderSummaryDto(
    Guid Id,
    string Code,
    string Product,
    DateTime StartAt,
    DateTime? EndAt,
    string? ModelName = null);
