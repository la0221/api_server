using AIVision.Domain.Entities;

namespace AIVision.Application.Ports.Persistence;

public interface IWorkOrderRepository
{
    Task<WorkOrder?> GetByCodeAsync(string code, CancellationToken cancellationToken);

    Task<WorkOrder?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkOrder>> GetAllAsync(CancellationToken cancellationToken);

    Task AddAsync(WorkOrder workOrder, CancellationToken cancellationToken);

    Task UpdateAsync(WorkOrder workOrder, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}
