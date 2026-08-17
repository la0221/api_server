using AIVision.Application.Contracts.ProductionStats;
using AIVision.Application.Ports.ProductionStats;
using Dapper;

namespace AIVision.Infrastructure.Persistence.SQLite;

public sealed class SqliteProductionStatsQuery : IProductionStatsQuery
{
    private readonly IDatabaseConnectionFactory _connectionFactory;

    public SqliteProductionStatsQuery(IDatabaseConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<WorkOrderSummaryDto>> FindOrdersAsync(
        DateTime? start,
        DateTime? end,
        string? product,
        string? orderKeyword,
        CancellationToken cancellationToken)
    {
        var whereConditions = new List<string>();
        var parameters = new DynamicParameters();

        // Filter by date range
        if (start.HasValue)
        {
            whereConditions.Add("StartAt >= @StartDate");
            parameters.Add("StartDate", start.Value.ToString("O"));
        }

        if (end.HasValue)
        {
            whereConditions.Add("COALESCE(EndAt, datetime('now')) <= @EndDate");
            parameters.Add("EndDate", end.Value.ToString("O"));
        }

        // Filter by product name
        if (!string.IsNullOrWhiteSpace(product))
        {
            whereConditions.Add("ProductName LIKE @Product");
            parameters.Add("Product", $"%{product}%");
        }

        // Filter by work order code
        if (!string.IsNullOrWhiteSpace(orderKeyword))
        {
            whereConditions.Add("Code LIKE @OrderKeyword");
            parameters.Add("OrderKeyword", $"%{orderKeyword}%");
        }

        var whereClause = whereConditions.Count > 0
            ? "WHERE " + string.Join(" AND ", whereConditions)
            : "";

        var sql = $@"
            SELECT
                Id,
                Code,
                ProductName AS Product,
                StartAt,
                EndAt,
                ModelName
            FROM WorkOrders
            {whereClause}
            ORDER BY StartAt DESC;
        ";

        using var connection = _connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<WorkOrderSummaryRow>(sql, parameters);

        return rows.Select(r => r.ToDto()).ToList();
    }

    public async Task<WorkOrderStatsDto?> GetStatsAsync(Guid orderId, CancellationToken cancellationToken)
    {
        // Query work order basic info
        const string orderSql = @"
            SELECT Id, Code, ProductName AS Product, StartAt, EndAt, ModelName
            FROM WorkOrders
            WHERE Id = @OrderId;
        ";

        // Query inspection statistics
        // 注意：OK/NG 判定邏輯需與 SqliteInspectionRepository.GetStatisticsByWorkOrderIdAsync 保持一致
        const string statsSql = @"
            SELECT
                COUNT(*) AS Total,
                SUM(CASE WHEN Result = 'OK' OR Result LIKE '%OK%' OR Result LIKE 'Simulated%OK%' THEN 1 ELSE 0 END) AS Ok,
                SUM(CASE WHEN Result = 'NG' OR Result LIKE '%NG%' OR Result LIKE 'Simulated%NG%' OR Result = 'Timeout' OR Result = 'ServiceError' THEN 1 ELSE 0 END) AS Ng
            FROM Inspections
            WHERE WorkOrderId = @OrderId;
        ";

        // Query defect type distribution from Defects table
        const string defectsSql = @"
            SELECT
                d.DefectType,
                COUNT(*) AS Count
            FROM Defects d
            INNER JOIN Inspections i ON d.InspectionId = i.Id
            WHERE i.WorkOrderId = @OrderId
            GROUP BY d.DefectType;
        ";

        // Query result type distribution from Inspections table
        // This handles classification models where Result contains the class name (e.g., 'scratch', 'stain')
        const string resultsSql = @"
            SELECT
                Result AS DefectType,
                COUNT(*) AS Count
            FROM Inspections
            WHERE WorkOrderId = @OrderId
              AND Result IS NOT NULL
              AND Result != ''
            GROUP BY Result;
        ";

        using var connection = _connectionFactory.CreateConnection();

        var orderRow = await connection.QuerySingleOrDefaultAsync<WorkOrderSummaryRow>(
            orderSql,
            new { OrderId = orderId.ToString() });

        if (orderRow == null)
        {
            return null;
        }

        var statsRow = await connection.QuerySingleAsync<StatsRow>(
            statsSql,
            new { OrderId = orderId.ToString() });

        // Get defects from Defects table
        var defectRows = await connection.QueryAsync<DefectCountRow>(
            defectsSql,
            new { OrderId = orderId.ToString() });

        // Get results from Inspections table (for classification models)
        var resultRows = await connection.QueryAsync<DefectCountRow>(
            resultsSql,
            new { OrderId = orderId.ToString() });

        // Merge both sources - Results take precedence for count
        var defectCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // First add from Inspections.Result (classification results)
        foreach (var row in resultRows)
        {
            defectCounts[row.DefectType] = row.Count;
        }

        // Then add/merge from Defects table (detection results may have multiple defects per inspection)
        foreach (var row in defectRows)
        {
            if (defectCounts.TryGetValue(row.DefectType, out var existingCount))
            {
                // If already counted from Results, use the larger count
                // (Defects table may have more detailed breakdown)
                defectCounts[row.DefectType] = Math.Max(existingCount, row.Count);
            }
            else
            {
                defectCounts[row.DefectType] = row.Count;
            }
        }

        return new WorkOrderStatsDto
        {
            Order = orderRow.ToDto(),
            Total = statsRow.Total,
            Ok = statsRow.Ok,
            Ng = statsRow.Ng,
            Defects = defectCounts
        };
    }

    // Helper class: WorkOrderSummaryRow
    private sealed class WorkOrderSummaryRow
    {
        public string Id { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string Product { get; set; } = string.Empty;
        public string StartAt { get; set; } = string.Empty;
        public string? EndAt { get; set; }
        public string? ModelName { get; set; }

        public WorkOrderSummaryDto ToDto()
        {
            return new WorkOrderSummaryDto(
                Guid.Parse(Id),
                Code,
                Product,
                DateTime.Parse(StartAt),
                EndAt != null ? DateTime.Parse(EndAt) : null,
                ModelName
            );
        }
    }

    // Helper class: StatsRow
    private sealed class StatsRow
    {
        public int Total { get; set; }
        public int Ok { get; set; }
        public int Ng { get; set; }
    }

    // Helper class: DefectCountRow
    private sealed class DefectCountRow
    {
        public string DefectType { get; set; } = string.Empty;
        public int Count { get; set; }
    }
}
