using AIVision.Application.Ports.Persistence;
using AIVision.Domain.Entities;
using Dapper;

namespace AIVision.Infrastructure.Persistence.SQLite;

public sealed class SqliteWorkOrderRepository : IWorkOrderRepository
{
    private readonly IDatabaseConnectionFactory _connectionFactory;

    public SqliteWorkOrderRepository(IDatabaseConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<WorkOrder?> GetByCodeAsync(string code, CancellationToken cancellationToken)
    {
        const string sql = @"
            SELECT Id, Code, ProductName, ModelName, MachineModelName, ExpectedMoldCode, StartAt, EndAt, Status
            FROM WorkOrders
            WHERE Code = @Code
            LIMIT 1;
        ";

        using var connection = _connectionFactory.CreateConnection();
        var row = await connection.QuerySingleOrDefaultAsync<WorkOrderRow>(sql, new { Code = code });

        return row?.ToEntity();
    }

    public async Task<WorkOrder?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        const string sql = @"
            SELECT Id, Code, ProductName, ModelName, MachineModelName, ExpectedMoldCode, StartAt, EndAt, Status
            FROM WorkOrders
            WHERE Id = @Id
            LIMIT 1;
        ";

        using var connection = _connectionFactory.CreateConnection();
        var row = await connection.QuerySingleOrDefaultAsync<WorkOrderRow>(sql, new { Id = id.ToString() });

        return row?.ToEntity();
    }

    public async Task<IReadOnlyList<WorkOrder>> GetAllAsync(CancellationToken cancellationToken)
    {
        const string sql = @"
            SELECT Id, Code, ProductName, ModelName, MachineModelName, ExpectedMoldCode, StartAt, EndAt, Status
            FROM WorkOrders
            ORDER BY StartAt DESC;
        ";

        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<WorkOrderRow>(sql);

        return rows.Select(r => r.ToEntity()).ToList();
    }

    public async Task AddAsync(WorkOrder workOrder, CancellationToken cancellationToken)
    {
        const string sql = @"
            INSERT INTO WorkOrders (Id, Code, ProductName, ModelName, MachineModelName, ExpectedMoldCode, StartAt, EndAt, Status, CreatedAt, UpdatedAt)
            VALUES (@Id, @Code, @ProductName, @ModelName, @MachineModelName, @ExpectedMoldCode, @StartAt, @EndAt, @Status, @CreatedAt, @UpdatedAt);
        ";

        var row = WorkOrderRow.FromEntity(workOrder);

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(sql, row);
    }

    public async Task UpdateAsync(WorkOrder workOrder, CancellationToken cancellationToken)
    {
        const string sql = @"
            UPDATE WorkOrders
            SET ProductName = @ProductName,
                ModelName = @ModelName,
                MachineModelName = @MachineModelName,
                ExpectedMoldCode = @ExpectedMoldCode,
                StartAt = @StartAt,
                EndAt = @EndAt,
                Status = @Status,
                UpdatedAt = @UpdatedAt
            WHERE Id = @Id;
        ";

        var row = WorkOrderRow.FromEntity(workOrder);

        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(sql, row);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        // 先刪除關聯的 Defects（透過 Inspections 的外鍵）
        const string deleteDefectsSql = @"
            DELETE FROM Defects
            WHERE InspectionId IN (SELECT Id FROM Inspections WHERE WorkOrderId = @Id);
        ";

        // 再刪除 Inspections
        const string deleteInspectionsSql = @"
            DELETE FROM Inspections
            WHERE WorkOrderId = @Id;
        ";

        // 最後刪除 WorkOrder
        const string deleteWorkOrderSql = @"
            DELETE FROM WorkOrders
            WHERE Id = @Id;
        ";

        using var connection = _connectionFactory.CreateConnection();

        // 使用 transaction 確保原子操作
        connection.Open();
        using var transaction = connection.BeginTransaction();

        try
        {
            await connection.ExecuteAsync(deleteDefectsSql, new { Id = id.ToString() }, transaction);
            await connection.ExecuteAsync(deleteInspectionsSql, new { Id = id.ToString() }, transaction);
            await connection.ExecuteAsync(deleteWorkOrderSql, new { Id = id.ToString() }, transaction);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    // 輔助類：數據庫行映射
    private sealed class WorkOrderRow
    {
        public string Id { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string? ModelName { get; set; }
        public string? MachineModelName { get; set; }
        public string? ExpectedMoldCode { get; set; }
        public string StartAt { get; set; } = string.Empty;
        public string? EndAt { get; set; }
        public string Status { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;

        public WorkOrder ToEntity()
        {
            var workOrder = new WorkOrder(
                code: Code,
                productName: ProductName,
                modelName: ModelName,
                machineModelName: MachineModelName,
                id: Guid.Parse(Id),
                startAtUtc: DateTime.Parse(StartAt),
                expectedMoldCode: ExpectedMoldCode
            );

            // 設置 EndAt 和 Status（使用反射或私有setter，這裡簡化處理）
            if (!string.IsNullOrEmpty(EndAt))
            {
                workOrder.Close(DateTime.Parse(EndAt));
            }

            if (Status != "Active")
            {
                workOrder.UpdateStatus(Status);
            }

            return workOrder;
        }

        public static WorkOrderRow FromEntity(WorkOrder entity)
        {
            return new WorkOrderRow
            {
                Id = entity.Id.ToString(),
                Code = entity.Code,
                ProductName = entity.ProductName,
                ModelName = entity.ModelName,
                MachineModelName = entity.MachineModelName,
                ExpectedMoldCode = entity.ExpectedMoldCode,
                StartAt = entity.StartAt.ToString("O"),
                EndAt = entity.EndAt?.ToString("O"),
                Status = entity.Status,
                CreatedAt = DateTime.UtcNow.ToString("O"),
                UpdatedAt = DateTime.UtcNow.ToString("O")
            };
        }
    }
}
