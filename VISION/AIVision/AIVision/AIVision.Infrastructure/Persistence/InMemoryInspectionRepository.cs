using System.Collections.Concurrent;
using AIVision.Application.Ports.Persistence;
using AIVision.Domain.Entities;

namespace AIVision.Infrastructure.Persistence;

public sealed class InMemoryInspectionRepository : IInspectionRepository
{
    private readonly ConcurrentDictionary<Guid, Inspection> _store = new();

    public Task AddAsync(Inspection inspection, CancellationToken cancellationToken)
    {
        _store[inspection.Id] = inspection;
        return Task.CompletedTask;
    }

    public Task<InspectionStatistics> GetStatisticsByWorkOrderIdAsync(Guid workOrderId, CancellationToken cancellationToken)
    {
        var inspections = _store.Values.Where(i => i.WorkOrderId == workOrderId).ToList();
        var totalCount = inspections.Count;
        var okCount = inspections.Count(i => i.Result.Contains("OK", StringComparison.OrdinalIgnoreCase));
        var ngCount = totalCount - okCount;
        return Task.FromResult(new InspectionStatistics(totalCount, okCount, ngCount));
    }

    public Task<Dictionary<string, int>> GetDefectStatisticsByWorkOrderIdAsync(Guid workOrderId, CancellationToken cancellationToken)
    {
        var inspections = _store.Values.Where(i => i.WorkOrderId == workOrderId).ToList();

        var defectStats = inspections
            .Where(i => i.Defects != null)
            .SelectMany(i => i.Defects!)
            .GroupBy(d => d.Type, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        return Task.FromResult(defectStats);
    }
}
