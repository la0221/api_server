using System.Collections.Concurrent;
using AIVision.Application.Ports.Persistence;
using AIVision.Domain.Entities;

namespace AIVision.Infrastructure.Persistence;

public sealed class InMemoryWorkOrderRepository : IWorkOrderRepository
{
    private readonly ConcurrentDictionary<string, WorkOrder> _byCode = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, WorkOrder> _byId = new();

    public Task<WorkOrder?> GetByCodeAsync(string code, CancellationToken cancellationToken)
    {
        _byCode.TryGetValue(code, out var workOrder);
        return Task.FromResult(workOrder);
    }

    public Task<WorkOrder?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        _byId.TryGetValue(id, out var workOrder);
        return Task.FromResult(workOrder);
    }

    public Task<IReadOnlyList<WorkOrder>> GetAllAsync(CancellationToken cancellationToken)
    {
        var workOrders = _byId.Values.OrderByDescending(w => w.StartAt).ToList();
        return Task.FromResult<IReadOnlyList<WorkOrder>>(workOrders);
    }

    public Task AddAsync(WorkOrder workOrder, CancellationToken cancellationToken)
    {
        _byCode[workOrder.Code] = workOrder;
        _byId[workOrder.Id] = workOrder;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(WorkOrder workOrder, CancellationToken cancellationToken)
    {
        _byCode[workOrder.Code] = workOrder;
        _byId[workOrder.Id] = workOrder;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        if (_byId.TryRemove(id, out var workOrder))
        {
            _byCode.TryRemove(workOrder.Code, out _);
        }
        return Task.CompletedTask;
    }
}
